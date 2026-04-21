using MessagePack;

namespace StarTruckMP.Shared.Dto;

[MessagePackObject(true)]
public class ProtocolWelcomeDto
{
    public int NetId { get; set; }
    public ushort ProtocolVersion { get; set; } = NetProtocol.CurrentVersion;
}

