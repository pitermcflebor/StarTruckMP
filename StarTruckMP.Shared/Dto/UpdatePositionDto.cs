using MessagePack;

namespace StarTruckMP.Shared.Dto
{
    [MessagePackObject(true)]
    public class UpdatePositionDto
    {
        public int NetId { get; set; }
        public Vector3 Position { get; set; }
        public Vector3 Rotation { get; set; }
        public Vector3 Velocity { get; set; }
        public Vector3 AngVel { get; set; }
        public bool IsTruck { get; set; }
        public bool InSeat { get; set; }
    }
}