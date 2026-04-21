using MessagePack;

namespace StarTruckMP.Shared.Dto;

[MessagePackObject(true)]
public class UpdateLiveryDto
{
    public int NetId { get; set; }
    public string Livery { get; set; } = string.Empty;
}

