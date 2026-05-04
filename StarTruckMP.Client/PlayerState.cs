using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace StarTruckMP.Client;

public static class PlayerState
{
    /// <summary>
    /// This will be populated automatic if we have successfully authenticated with Xbox Live token
    /// or Steam Authentication token.
    /// </summary>
    public static string Token { get; set; } = "";

    /// <summary>
    /// Server ephemeral P-256 public key (SubjectPublicKeyInfo DER bytes) received during HTTPS auth.
    /// Used by the client to derive the shared ChaCha20-Poly1305 session key via ECDH + HKDF.
    /// Cleared after the session key is derived.
    /// </summary>
    public static byte[]? ServerPublicKey { get; set; }

    #region Game State

    public static string Sector { get; set; } = "";
    public static GameObject Truck { get; set; }
    public static GameObject Player { get; set; }
    public static GameObject SpaceSuit { get; set; }
    public static Material[] SpaceSuitMats { get; set; }

    #endregion
}