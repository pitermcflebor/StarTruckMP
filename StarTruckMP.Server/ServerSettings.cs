namespace StarTruckMP.Server;

public class ServerSettings
{
    public string IpAddress { get; set; } = "0.0.0.0";
    public ushort Port { get; set; } = 7777;
    public ushort MaxPlayers { get; set; } = 100;

    // HTTP API authentication
    public string ApiAdminUsername { get; set; } = "admin";
    public string ApiAdminPassword { get; set; } = "changeme";
    public TimeSpan ApiTokenLifetime { get; set; } = TimeSpan.FromHours(8);

    // Xbox Live: title ID that must be active when the player authenticates.
    // Ensures the player is running Star Trucker — tokens from other Xbox games are rejected.
    // Default: Star Trucker (0x6E2BD7CD). Set to 0 to disable the check.
    public uint XboxRequiredTitleId { get; set; } = 0x6E2BD7CD;

    // Steam: Steamworks Web API key used to validate auth session tickets.
    // Obtain one at https://steamcommunity.com/dev/apikey
    // Leave empty to skip cryptographic validation (NOT recommended for production).
    public string SteamWebApiKey { get; set; } = string.Empty;

    // Steam: Star Trucker App ID. Used when calling ISteamUserAuth/AuthenticateUserTicket.
    // Default: 2380050 (Star Trucker on Steam).
    public uint SteamAppId { get; set; } = 2380050;
}