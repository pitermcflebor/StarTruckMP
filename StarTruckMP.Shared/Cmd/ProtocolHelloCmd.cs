using MessagePack;

namespace StarTruckMP.Shared.Cmd;

[MessagePackObject(true)]
public class ProtocolHelloCmd
{
    public ushort ProtocolVersion { get; set; } = NetProtocol.CurrentVersion;
}

