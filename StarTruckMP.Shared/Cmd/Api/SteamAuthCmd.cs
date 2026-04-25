namespace StarTruckMP.Shared.Cmd.Api;

public class SteamAuthCmd
{
    public ulong SteamId { get; set; }

    /// <summary>
    /// Raw auth-session ticket bytes encoded as a lowercase hex string.
    /// Obtained via SteamUser.GetAuthSessionTicket() on the client.
    /// </summary>
    public string Ticket { get; set; } = string.Empty;
}

