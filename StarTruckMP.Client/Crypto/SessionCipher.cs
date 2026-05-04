using System;
using System.Security.Cryptography;
using System.Threading;

namespace StarTruckMP.Client.Crypto;

/// <summary>
/// Per-session ChaCha20-Poly1305 cipher for the client.
/// Symmetric to the server implementation: the client's send counter is the
/// server's receive counter and vice-versa.
///
/// Frame layout: [8-byte counter LE][ciphertext][16-byte Poly1305 tag]
/// </summary>
public sealed class SessionCipher : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int CounterSize = 8;

    private readonly ChaCha20Poly1305 _cipher;
    private long _sendCounter;
    private long _recvCounter = -1;

    public SessionCipher(byte[] sessionKey)
    {
        if (sessionKey.Length != 32)
            throw new ArgumentException("Session key must be 32 bytes.", nameof(sessionKey));
        _cipher = new ChaCha20Poly1305(sessionKey);
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> (which must already include the 1-byte PacketType header).
    /// Returns the framed payload: [8-byte counter LE][ciphertext][16-byte tag]
    /// </summary>
    public byte[] Encrypt(byte[] plaintext)
    {
        var counter = Interlocked.Increment(ref _sendCounter);
        Span<byte> nonce = stackalloc byte[NonceSize];
        WriteLe64(nonce[4..], counter);

        var output = new byte[CounterSize + plaintext.Length + TagSize];
        WriteLe64(output, counter);

        _cipher.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(CounterSize, plaintext.Length),
            output.AsSpan(CounterSize + plaintext.Length, TagSize));

        return output;
    }

    /// <summary>
    /// Decrypts a framed payload. Throws <see cref="CryptographicException"/> on failure.
    /// Applies a 256-packet replay window to tolerate Unreliable-channel reordering.
    /// </summary>
    public byte[] Decrypt(byte[] frame)
    {
        if (frame.Length < CounterSize + TagSize)
            throw new CryptographicException("Frame is too short.");

        var counter = ReadLe64(frame);

        const long replayWindow = 256;
        var last = Volatile.Read(ref _recvCounter);
        if (counter <= last - replayWindow)
            throw new CryptographicException($"Replay/stale packet. counter={counter}, last={last}");

        Span<byte> nonce = stackalloc byte[NonceSize];
        WriteLe64(nonce[4..], counter);

        var ciphertextLen = frame.Length - CounterSize - TagSize;
        var plaintext = new byte[ciphertextLen];

        _cipher.Decrypt(
            nonce,
            frame.AsSpan(CounterSize, ciphertextLen),
            frame.AsSpan(CounterSize + ciphertextLen, TagSize),
            plaintext);

        long prev;
        do
        {
            prev = Volatile.Read(ref _recvCounter);
            if (counter <= prev) break;
        } while (Interlocked.CompareExchange(ref _recvCounter, counter, prev) != prev);

        return plaintext;
    }

    private static void WriteLe64(Span<byte> dest, long value)
    {
        var uval = (ulong)value;
        for (var i = 0; i < 8; i++)
            dest[i] = (byte)(uval >> (i * 8));
    }

    private static long ReadLe64(ReadOnlySpan<byte> src)
    {
        ulong v = 0;
        for (var i = 0; i < 8; i++)
            v |= (ulong)src[i] << (i * 8);
        return (long)v;
    }

    public void Dispose() => _cipher.Dispose();
}

