using System;
using MessagePack;

namespace StarTruckMP.Shared
{
    [MessagePackObject]
    public struct Vector3 : IEquatable<Vector3>
    {
        [Key(0)]
        public float X;
        [Key(1)]
        public float Y;
        [Key(2)]
        public float Z;

        public bool Equals(Vector3 other)
        {
            return X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        }

        public override bool Equals(object? obj)
        {
            return obj is Vector3 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Z);
        }
    }
}