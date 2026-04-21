using MessagePack;

namespace StarTruckMP.Shared.Dto;

[MessagePackObject(true)]
public class UpdateSectorDto
{
    public int NetId { get; set; }
    public string Sector { get; set; } = "none";
}

