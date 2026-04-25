using System.Collections.Concurrent;
using System.Diagnostics;
using LiteNetLib;
using StarTruckMP.Shared;
using StarTruckMP.Shared.Cmd;
using StarTruckMP.Shared.Dto;

const int ExitSuccess = 0;
const int ExitConnectTimeout = 2;
const int ExitBroadcastTimeout = 3;
const int ExitBroadcastMismatch = 4;
const int ExitHandshakeTimeout = 5;
const int ExitProtocolMismatch = 6;

var options = ProbeOptions.Parse(args);

RunContractTests();
Console.WriteLine("[probe] Contract tests passed.");

Console.WriteLine($"[probe] Target server: {options.Host}:{options.Port}, timeout: {options.TimeoutMs}ms");

var sender = new ProbePeer("sender");
var receiver = new ProbePeer("receiver");

try
{
sender.Start();
receiver.Start();

sender.Connect(options.Host, options.Port, options.ConnectKey);
receiver.Connect(options.Host, options.Port, options.ConnectKey);

if (!WaitUntil(() => sender.IsConnected && receiver.IsConnected, options.TimeoutMs, () => PollPeers(sender, receiver)))
{
    Console.Error.WriteLine("[probe] Timeout waiting for both clients to connect.");
    return ExitConnectTimeout;
}

sender.Send(new ProtocolHelloCmd { ProtocolVersion = NetProtocol.CurrentVersion }.Serialize(PacketType.ProtocolHello), 0, DeliveryMethod.ReliableOrdered);
receiver.Send(new ProtocolHelloCmd { ProtocolVersion = NetProtocol.CurrentVersion }.Serialize(PacketType.ProtocolHello), 0, DeliveryMethod.ReliableOrdered);

if (!WaitUntil(() => sender.HandshakeCompleted && receiver.HandshakeCompleted, options.TimeoutMs, () => PollPeers(sender, receiver)))
{
    Console.Error.WriteLine("[probe] Timeout waiting for protocol handshake.");
    return ExitHandshakeTimeout;
}

if (sender.HasProtocolMismatch || receiver.HasProtocolMismatch)
{
    Console.Error.WriteLine("[probe] Server reported protocol mismatch.");
    return ExitProtocolMismatch;
}

var expected = new UpdatePositionCmd
{
    Position = new Vector3 { X = 12.34f, Y = 5.67f, Z = -3.21f },
    Rotation = new Vector3 { X = 0f, Y = 180f, Z = 0f },
    Velocity = new Vector3 { X = 0.5f, Y = 0f, Z = 0.1f },
    AngVel = new Vector3 { X = 0f, Y = 2.5f, Z = 0f },
    IsTruck = true,
    InSeat = true
};

sender.Send(expected.Serialize(PacketType.UpdatePosition), 0, DeliveryMethod.ReliableOrdered);
Console.WriteLine("[probe] Position packet sent from sender to server.");

UpdatePositionDto? received = null;
if (!WaitUntil(() => receiver.TryDequeuePosition(out received), options.TimeoutMs, () => PollPeers(sender, receiver)))
{
    Console.Error.WriteLine("[probe] Timeout waiting for broadcast packet on receiver client.");
    return ExitBroadcastTimeout;
}

if (!IsEquivalent(expected, received!))
{
    Console.Error.WriteLine("[probe] Received packet but payload does not match expected values.");
    Console.Error.WriteLine($"[probe] Received NetId={received!.NetId}, Position=({received.Position.X}, {received.Position.Y}, {received.Position.Z})");
    return ExitBroadcastMismatch;
}

Console.WriteLine($"[probe] Success. Broadcast received with NetId={received!.NetId}.");
return ExitSuccess;
}
finally
{
    sender.Dispose();
    receiver.Dispose();
}

static void RunContractTests()
{
    Expect(NetProtocol.MinSupportedVersion <= NetProtocol.CurrentVersion, "Protocol version range is invalid.");

    var hello = new ProtocolHelloCmd { ProtocolVersion = NetProtocol.CurrentVersion };
    AssertRoundTrip(PacketType.ProtocolHello, hello, "ProtocolHello roundtrip failed.");

    var welcome = new ProtocolWelcomeDto { NetId = 42, ProtocolVersion = NetProtocol.CurrentVersion };
    AssertRoundTrip(PacketType.ProtocolWelcome, welcome, "ProtocolWelcome roundtrip failed.");

    var mismatch = new ProtocolMismatchDto
    {
        ClientVersion = 99,
        MinSupportedVersion = NetProtocol.MinSupportedVersion,
        ServerVersion = NetProtocol.CurrentVersion
    };
    AssertRoundTrip(PacketType.ProtocolMismatch, mismatch, "ProtocolMismatch roundtrip failed.");

    var position = new UpdatePositionCmd
    {
        Position = new Vector3 { X = 1, Y = 2, Z = 3 },
        Rotation = new Vector3 { X = 4, Y = 5, Z = 6 },
        Velocity = new Vector3 { X = 7, Y = 8, Z = 9 },
        AngVel = new Vector3 { X = 10, Y = 11, Z = 12 },
        IsTruck = true,
        InSeat = false
    };
    AssertRoundTrip(PacketType.UpdatePosition, position, "UpdatePosition roundtrip failed.");

    var sync = new SyncPlayersDto
    {
        Players = new[]
        {
            new PlayerSnapshotDto
            {
                NetId = 1,
                Sector = "Alpha",
                Livery = "Livery01",
                Player = new TransformDto
                {
                    Position = new Vector3 { X = 1, Y = 1, Z = 1 },
                    Rotation = new Vector3 { X = 2, Y = 2, Z = 2 },
                    Velocity = new Vector3 { X = 3, Y = 3, Z = 3 },
                    AngVel = new Vector3 { X = 4, Y = 4, Z = 4 }
                },
                Truck = new TransformDto
                {
                    Position = new Vector3 { X = 5, Y = 5, Z = 5 },
                    Rotation = new Vector3 { X = 6, Y = 6, Z = 6 },
                    Velocity = new Vector3 { X = 7, Y = 7, Z = 7 },
                    AngVel = new Vector3 { X = 8, Y = 8, Z = 8 }
                }
            }
        }
    };
    AssertRoundTrip(PacketType.SyncPlayers, sync, "SyncPlayers roundtrip failed.");
}

static void AssertRoundTrip<T>(PacketType expectedType, T payload, string error)
{
    var packet = payload!.Serialize(expectedType);
    Expect(PacketSerializer.TrySplitPacket<T>(packet, out var actualType, out var actualPayload), "Packet split failed.");
    Expect(actualType == expectedType, error + " Unexpected packet type.");
}

static void Expect(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void PollPeers(ProbePeer first, ProbePeer second)
{
    first.Poll();
    second.Poll();
}

static bool WaitUntil(Func<bool> condition, int timeoutMs, Action poll)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        poll();
        if (condition())
            return true;

        Thread.Sleep(5);
    }

    return false;
}

static bool IsEquivalent(UpdatePositionCmd sent, UpdatePositionDto received)
{
    const float epsilon = 0.0001f;

    return sent.IsTruck == received.IsTruck
           && sent.InSeat == received.InSeat
           && Near(sent.Position, received.Position, epsilon)
           && Near(sent.Rotation, received.Rotation, epsilon)
           && Near(sent.Velocity, received.Velocity, epsilon)
           && Near(sent.AngVel, received.AngVel, epsilon);
}

static bool Near(Vector3 a, Vector3 b, float epsilon)
{
    return Math.Abs(a.X - b.X) < epsilon
           && Math.Abs(a.Y - b.Y) < epsilon
           && Math.Abs(a.Z - b.Z) < epsilon;
}

internal sealed class ProbePeer : IDisposable
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private readonly string _name;
    private readonly ConcurrentQueue<UpdatePositionDto> _positions = new();
    private readonly ConcurrentQueue<ProtocolWelcomeDto> _welcomes = new();
    private readonly ConcurrentQueue<ProtocolMismatchDto> _mismatches = new();

    private NetPeer? _server;

    public ProbePeer(string name)
    {
        _name = name;
        _manager = new NetManager(_listener) { AutoRecycle = true };

        _listener.PeerConnectedEvent += peer =>
        {
            _server = peer;
            Console.WriteLine($"[probe:{_name}] Connected. PeerId={peer.Id}");
        };

        _listener.PeerDisconnectedEvent += (_, info) =>
            Console.WriteLine($"[probe:{_name}] Disconnected. Reason={info.Reason}");

        _listener.NetworkErrorEvent += (endPoint, error) =>
            Console.Error.WriteLine($"[probe:{_name}] Network error {error} from {endPoint}");

        _listener.NetworkReceiveEvent += (_, reader, channel, _) =>
        {
            try
            {
                var packetType = (PacketType)reader.GetByte();
                var payload = reader.GetRemainingBytes();

                switch (packetType)
                {
                    case PacketType.ProtocolWelcome:
                        var welcome = PacketSerializer.Deserialize<ProtocolWelcomeDto>(payload);
                        _welcomes.Enqueue(welcome);
                        Console.WriteLine($"[probe:{_name}] Protocol welcome. Version={welcome.ProtocolVersion}");
                        break;
                    case PacketType.ProtocolMismatch:
                        var mismatch = PacketSerializer.Deserialize<ProtocolMismatchDto>(payload);
                        _mismatches.Enqueue(mismatch);
                        Console.Error.WriteLine($"[probe:{_name}] Protocol mismatch. Client={mismatch.ClientVersion} Server={mismatch.ServerVersion}");
                        break;
                    case PacketType.UpdatePosition:
                        var update = PacketSerializer.Deserialize<UpdatePositionDto>(payload);
                        _positions.Enqueue(update);
                        Console.WriteLine($"[probe:{_name}] Received UpdatePosition on channel {channel}.");
                        break;
                }
            }
            finally
            {
                reader.Recycle();
            }
        };
    }

    public bool IsConnected => _server?.ConnectionState == ConnectionState.Connected;

    public bool HandshakeCompleted => _welcomes.TryPeek(out _);
    public bool HasProtocolMismatch => _mismatches.TryPeek(out _);

    public void Start()
    {
        _manager.Start();
    }

    public void Connect(string host, int port, string key)
    {
        _manager.Connect(host, port, key);
    }

    public void Send(byte[] payload, byte channel, DeliveryMethod deliveryMethod)
    {
        if (_server is null)
            throw new InvalidOperationException($"Peer '{_name}' is not connected.");

        _server.Send(payload, channel, deliveryMethod);
    }

    public bool TryDequeuePosition(out UpdatePositionDto? update)
    {
        if (_positions.TryDequeue(out var value))
        {
            update = value;
            return true;
        }

        update = null;
        return false;
    }

    public void Poll()
    {
        _manager.PollEvents();
    }

    public void Dispose()
    {
        _manager.Stop();
    }
}

internal sealed class ProbeOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7777;
    public string ConnectKey { get; set; } = NetworkConstants.ConnectKey;
    public int TimeoutMs { get; set; } = 5000;

    public static ProbeOptions Parse(string[] args)
    {
        var options = new ProbeOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host" when i + 1 < args.Length:
                    options.Host = args[++i];
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var port):
                    options.Port = port;
                    i++;
                    break;
                case "--key" when i + 1 < args.Length:
                    options.ConnectKey = args[++i];
                    break;
                case "--timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out var timeout):
                    options.TimeoutMs = timeout;
                    i++;
                    break;
            }
        }

        return options;
    }
}


