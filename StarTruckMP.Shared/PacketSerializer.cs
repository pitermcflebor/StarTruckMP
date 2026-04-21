using System;
using MessagePack;

namespace StarTruckMP.Shared
{
    public static class PacketSerializer
    {
        public static byte[] Serialize<T>(this T obj, PacketType type)
        {
            var bytes = MessagePackSerializer.Serialize(obj);
            var result = new byte[bytes.Length + 1];
            result[0] = (byte)type;
            Buffer.BlockCopy(bytes, 0, result, 1, bytes.Length);
            return result;
        }

        public static T Deserialize<T>(byte[] payload)
        {
            return MessagePackSerializer.Deserialize<T>(payload);
        }

        public static T Deserialize<T>(ReadOnlySpan<byte> payload)
        {
            return MessagePackSerializer.Deserialize<T>(payload.ToArray());
        }

        public static PacketType ReadPacketType(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 1)
                throw new ArgumentException("Packet is empty", nameof(packet));

            return (PacketType)packet[0];
        }

        public static byte[] ReadPayload(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 2)
                return [];

            return packet[1..].ToArray();
        }

        public static bool TrySplitPacket(ReadOnlySpan<byte> packet, out PacketType type, out byte[] payload)
        {
            if (packet.Length < 1)
            {
                type = default;
                payload = [];
                return false;
            }

            type = (PacketType)packet[0];
            payload = packet.Length > 1 ? packet[1..].ToArray() : [];
            return true;
        }
    }
}