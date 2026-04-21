using MessagePack;

namespace StarTruckMP.Shared.Dto;

[MessagePackObject(true)]
public class SyncPlayersDto
{
    public PlayerSnapshotDto[] Players { get; set; } = [];
}

