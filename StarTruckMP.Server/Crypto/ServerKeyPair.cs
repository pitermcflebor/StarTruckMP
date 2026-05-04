using System;
using System.Security.Cryptography;
using System.Text;

namespace StarTruckMP.Server.Crypto;

/// <summary>
/// Singleton that holds the server's ephemeral P-256 ECDH key pair for the lifetime of the process.
/// A new pair is generated on every server restart (provides forward secrecy between sessions).
/// </summary>
public sealed class ServerKeyPair : IDisposable
{
    private static readonly byte[] KdfInfo = Encoding.UTF8.GetBytes("StarTruckerMP-v2");

    private readonly ECDiffieHellman _key;

    public ServerKeyPair()
    {
        _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        // Export SubjectPublicKeyInfo (DER) so the client can import it with ImportSubjectPublicKeyInfo.
        PublicKeyBytes = _key.ExportSubjectPublicKeyInfo();
    }

    /// <summary>SubjectPublicKeyInfo (DER) bytes of the server's ephemeral P-256 public key.</summary>
    public byte[] PublicKeyBytes { get; }

    /// <summary>
    /// Derives the 32-byte ChaCha20-Poly1305 session key for a given client using ECDH P-256
    /// followed by SHA-256: SHA256(sharedSecret || "StarTruckerMP-v2").
    /// Mirrors <c>ClientKeyExchange.DeriveSessionKey</c> in the client project.
    /// </summary>
    public byte[] DeriveSessionKey(byte[] clientPublicKeyBytes)
    {
        using var clientKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        clientKey.ImportSubjectPublicKeyInfo(clientPublicKeyBytes, out _);

        // DeriveKeyFromHash returns SHA256(sharedSecret || secretAppend) = 32 bytes.
        return _key.DeriveKeyFromHash(clientKey.PublicKey, HashAlgorithmName.SHA256, null, KdfInfo);
    }

    public void Dispose() => _key.Dispose();
}
