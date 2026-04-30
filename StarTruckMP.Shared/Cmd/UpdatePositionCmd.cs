using MessagePack;

namespace StarTruckMP.Shared.Cmd
{
    [MessagePackObject]
    public class UpdatePositionCmd
    {
        [Key(0)]
        public Vector3 Position { get; set; }
        [Key(1)]
        public Quaternion Rotation { get; set; }
        [Key(2)]
        public Vector3 Velocity { get; set; }
        [Key(3)]
        public Vector3 AngVel { get; set; }
        [Key(4)]
        public bool IsTruck { get; set; }
        [Key(5)]
        public bool InSeat { get; set; }
    }
}