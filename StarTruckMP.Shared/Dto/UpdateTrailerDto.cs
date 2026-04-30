using MessagePack;

namespace StarTruckMP.Shared.Dto;

[MessagePackObject]
public class UpdateTrailerDto
{
    [Key(0)]
    public int NetId { get; set; }
    [Key(1)]
    public int TrailerCount { get; set; }
    [Key(2)]
    public string LiveryId { get; set; }
    [Key(3)]
    public string CargoTypeId { get; set; }
}