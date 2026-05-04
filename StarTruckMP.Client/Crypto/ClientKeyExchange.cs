using System;
using System.Security.Cryptography;
using System.Text;

namespace StarTruckMP.Client.Crypto;

/// <summary>
/// Handles the ephemeral ECDH P-256 key exchange on the client side.
/// Uses <see cref="ECDiffieHellman.DeriveKeyFromHash"/> which is available since .NET 5,
/// ensuring compatibility with the game's net6.0 runtime.
/// </summary>
public static class ClientKeyExchange
{
    private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("StarTruckerMP-v2");

    /// <summary>
    /// Generates a new ephemeral P-256 ECDH key pair.
    /// The caller is responsible for disposing the returned instance after deriving the session key.
    /// </summary>
    public static ECDiffieHellman GenerateEphemeralKeyPair()
    {
        return ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>
    /// Exports the SubjectPublicKeyInfo (DER) bytes of the local key pair.
    /// These bytes are sent to the server in <c>ProtocolHelloCmd.ClientPublicKey</c>.
    /// </summary>
    public static byte[] ExportPublicKeyBytes(ECDiffieHellman key)
    {
        return key.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// Performs ECDH with the server's SubjectPublicKeyInfo DER public key bytes and derives
    /// a 32-byte session key using SHA-256: SHA256(sharedSecret || "StarTruckerMP-v2").
    /// This is compatible with the server's derivation via <see cref="ECDiffieHellman.DeriveKeyFromHash"/>.
    /// </summary>
    public static byte[] DeriveSessionKey(ECDiffieHellman localKey, byte[] serverPublicKeyBytes)
    {
        using var serverKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        serverKey.ImportSubjectPublicKeyInfo(serverPublicKeyBytes, out _);

        // DeriveKeyFromHash returns SHA256(sharedSecret || secretAppend) — 32 bytes.
        // The server uses the identical call, so both sides derive the same key.
        return localKey.DeriveKeyFromHash(serverKey.PublicKey, HashAlgorithmName.SHA256, null, HkdfInfo);
    }
}
