using MessagePack;

namespace StarTruckMP.Shared.Dto
{
    [MessagePackObject]
    public class UpdatePositionDto
    {
        [Key(0)]
        public int NetId { get; set; }
        [Key(1)]
        public Vector3 Position { get; set; }
        [Key(2)]
        public Quaternion Rotation { get; set; }
        [Key(3)]
        public Vector3 Velocity { get; set; }
        [Key(4)]
        public Vector3 AngVel { get; set; }
        [Key(5)]
        public bool IsTruck { get; set; }
        [Key(6)]
        public bool InSeat { get; set; }
    }
}