using MessagePack;

namespace StarTruckMP.Shared.Dto;

[MessagePackObject]
public class TransformDto
{
    [Key(0)]
    public Vector3 Position { get; set; }
    [Key(1)]
    public Quaternion Rotation { get; set; }
    [Key(2)]
    public Vector3 Velocity { get; set; }
    [Key(3)]
    public Vector3 AngVel { get; set; }
}

