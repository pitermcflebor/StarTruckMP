namespace StarTruckMP.Server;

public class ServerSettings
{
    public string IpAddress { get; set; } = "127.0.0.1";
    public ushort Port { get; set; } = 7777;
    public ushort MaxPlayers { get; set; } = 100;
}