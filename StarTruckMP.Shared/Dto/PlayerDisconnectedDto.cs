using MessagePack;

namespace StarTruckMP.Shared.Dto;

[MessagePackObject(true)]
public class PlayerDisconnectedDto
{
    public int NetId { get; set; }
}

