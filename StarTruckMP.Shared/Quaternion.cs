using System;
using MessagePack;

namespace StarTruckMP.Shared;

[MessagePackObject]
public struct Quaternion : IEquatable<Quaternion>
{
    [Key(0)]
    public float X { get; set; }
    [Key(1)]
    public float Y { get; set; }
    [Key(2)]
    public float Z { get; set; }
    [Key(3)]
    public float W { get; set; }

    public bool Equals(Quaternion other)
    {
        return X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);
    }

    public override bool Equals(object? obj)
    {
        return obj is Quaternion other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y, Z, W);
    }
}