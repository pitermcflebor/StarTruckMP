using MessagePack;

namespace StarTruckMP.Shared.Dto;

[MessagePackObject(true)]
public class ProtocolMismatchDto
{
    public ushort ClientVersion { get; set; }
    public ushort MinSupportedVersion { get; set; } = NetProtocol.MinSupportedVersion;
    public ushort ServerVersion { get; set; } = NetProtocol.CurrentVersion;
}

