namespace StarTruckMP.Shared.Dto.Api;

public class TicketAuthenticationDto
{
    public string? Token { get; set; }

    /// <summary>
    /// 32-byte X25519 ephemeral public key of the server, Base64-encoded.
    /// The client uses this to derive the shared ChaCha20-Poly1305 session key.
    /// </summary>
    public string? ServerPublicKey { get; set; }
}