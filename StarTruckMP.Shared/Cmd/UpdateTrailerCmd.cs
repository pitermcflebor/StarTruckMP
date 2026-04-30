using MessagePack;

namespace StarTruckMP.Shared.Cmd;

[MessagePackObject]
public class UpdateTrailerCmd
{
    [Key(0)]
    public int TrailerCount { get; set; }
    [Key(1)]
    public string LiveryId { get; set; }
    [Key(2)]
    public string CargoTypeId { get; set; }
}