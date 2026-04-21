using MessagePack;

namespace StarTruckMP.Shared.Dto;

[MessagePackObject(true)]
public class PlayerSnapshotDto
{
    public int NetId { get; set; }
    public string Sector { get; set; } = "none";
    public string Livery { get; set; } = string.Empty;
    public TransformDto Player { get; set; } = new();
    public TransformDto Truck { get; set; } = new();
}

