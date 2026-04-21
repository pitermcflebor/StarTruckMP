namespace StarTruckMP.Shared
{
    public enum PacketType : byte
    {
        SyncPlayers = 0,
        PlayerConnected = 1,
        PlayerDisconnected = 2,
        UpdatePosition = 3,
        UpdateLivery = 4,
        UpdateSector = 5,
        ProtocolHello = 6,
        ProtocolWelcome = 7,
        ProtocolMismatch = 8
    }
}