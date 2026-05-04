namespace StarTruckMP.Shared
{
    public enum PacketType : byte
    {
        SyncPlayers,
        PlayerConnected,
        PlayerDisconnected,
        UpdatePosition,
        UpdateLivery,
        UpdateSector,
        ProtocolHello,
        ProtocolWelcome,
        ProtocolMismatch,
        ProtocolAuthenticate,
        UpdateTrailer,
        Voice,
        EncryptedPayload
    }
}