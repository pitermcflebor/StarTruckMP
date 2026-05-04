using System.Security.Cryptography;
using System.Text;

namespace StarTruckMP.Server.Crypto;

/// <summary>
/// Cryptographic utilities for the ECDH key exchange and HKDF key derivation.
/// </summary>
public static class CryptoUtils
{
    private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("StarTruckerMP-v2");

    /// <summary>
    /// Derives a 32-byte ChaCha20-Poly1305 session key from the raw ECDH shared secret
    /// using HKDF-SHA256 (RFC 5869).
    /// </summary>
    public static byte[] DeriveSessionKey(byte[] sharedSecret)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt: null, info: HkdfInfo);
    }
}

