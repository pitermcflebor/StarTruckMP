using StarTruckMP.Server.Crypto;

namespace StarTruckMP.Server.Entities;

/// <summary>
/// This contains the player information, it shouldn't be constructed.
/// </summary>
public class Player(int id)
{
    public int Id { get; set; } = id;
    public bool HandshakeCompleted { get; set; }
    public ushort ProtocolVersion { get; set; }
    public string Sector { get; set; } = "none";
    public string Livery { get; set; } = string.Empty;

    /// <summary>
    /// Per-player ChaCha20-Poly1305 session cipher, available after the ECDH handshake.
    /// </summary>
    public SessionCipher? Cipher { get; set; }

    /// <summary>True when the network handshake and key exchange are both complete.</summary>
    public bool EncryptionReady => Cipher is not null && HandshakeCompleted;

    public StarTruckMP.Shared.Vector3 PlayerPosition { get; set; }
    public StarTruckMP.Shared.Quaternion PlayerRotation { get; set; }
    public StarTruckMP.Shared.Vector3 PlayerVelocity { get; set; }
    public StarTruckMP.Shared.Vector3 PlayerAngVel { get; set; }

    public StarTruckMP.Shared.Vector3 TruckPosition { get; set; }
    public StarTruckMP.Shared.Quaternion TruckRotation { get; set; }
    public StarTruckMP.Shared.Vector3 TruckVelocity { get; set; }
    public StarTruckMP.Shared.Vector3 TruckAngVel { get; set; }
    
    public int TrailerCount { get; set; }
    public string TrailerLivery { get; set; } = string.Empty;
    public string TrailerCargoTypeId { get; set; } = string.Empty;
}