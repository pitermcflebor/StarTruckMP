using System.Globalization;
using System.Net.Sockets;
using LiteNetLib;
using StarTruckMP.Shared;
using StarTruckMP.Shared.Cmd;
using StarTruckMP.Shared.Dto;

// ---------------------------------------------------------------------------
// StarTruckMP – FakeClient
// Emulates a second player so you can test the server without running the game
// twice.  Usage:  dotnet run [host:port]   (default: 127.0.0.1:7777)
// ---------------------------------------------------------------------------

const int MovementUpdateMs = 15;
const float OrbitRadius    = 50f;
// Speed expressed in world-units per second (arc length).
// At radius 50 the full circumference is ~314 units.
// 30 u/s → the fake player travels 3 units every 100 ms (≈ 6-7 ticks of 15 ms).
const float DefaultOrbitSpeed = 30f;
const string DefaultLivery  = "livery_fake_client";
const string DEFAULT_SECTOR = "Sector_07_EmeraldJunction";

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

var endpoint = args.Length > 0 ? args[0] : "127.0.0.1:7777";

if (!TryParseEndpoint(endpoint, out var host, out var port))
{
    Console.Error.WriteLine($"[FakeClient] Invalid endpoint '{endpoint}'. Expected format: host:port");
    return 1;
}

// ── State ──────────────────────────────────────────────────────────────────

NetPeer?  server            = null;
bool      handshakeDone     = false;
int       myNetId           = -1;
string    currentSector     = "none";
float     orbitAngle        = 0f;
float     orbitOriginX     = 0f;
float     orbitOriginY     = 0f;
float     orbitOriginZ     = 0f;
float     orbitSpeed       = DefaultOrbitSpeed; // world-units per second

// ── LiteNetLib setup ──────────────────────────────────────────────────────-

var listener = new EventBasedNetListener();
var client   = new NetManager(listener) { UnconnectedMessagesEnabled = false };

listener.PeerConnectedEvent += OnPeerConnected;
listener.PeerDisconnectedEvent += OnPeerDisconnected;
listener.NetworkErrorEvent += OnNetworkError;
listener.NetworkReceiveEvent += OnNetworkReceive;

client.Start();
Console.WriteLine($"[FakeClient] Connecting to {host}:{port} ...");
client.Connect(host, port, NetworkConstants.ConnectKey);

// ── Task: movement loop ────────────────────────────────────────────────────

var cts = new CancellationTokenSource();

var movementTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(MovementUpdateMs, cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        if (server is null || !handshakeDone)
            continue;

        orbitAngle += (orbitSpeed / OrbitRadius) * (MovementUpdateMs / 1000f);
        if (orbitAngle > MathF.Tau) orbitAngle -= MathF.Tau;

        float x    = orbitOriginX + MathF.Cos(orbitAngle) * OrbitRadius;
        float z    = orbitOriginZ + MathF.Sin(orbitAngle) * OrbitRadius;
        // Tangential velocity: speed (u/s) in the direction perpendicular to the radius
        float velX = -MathF.Sin(orbitAngle) * orbitSpeed;
        float velZ =  MathF.Cos(orbitAngle) * orbitSpeed;
        float yaw  = orbitAngle * (180f / MathF.PI) % 360f;

        // Truck position
        var truckCmd = new UpdatePositionCmd
        {
            Position = new Vector3 { X = x,       Y = orbitOriginY,        Z = z       },
            Rotation = new Vector3 { X = 0f,       Y = yaw,  Z = 0f      },
            Velocity = new Vector3 { X = velX,     Y = 0f,   Z = velZ    },
            AngVel   = new Vector3 { X = 0f,       Y = 0f,   Z = 0f      },
            IsTruck  = true,
            InSeat   = false
        };
        server.Send(truckCmd.Serialize(PacketType.UpdatePosition), DeliveryMethod.Unreliable);

        // Player position (slightly above / in front of the truck)
        var playerCmd = new UpdatePositionCmd
        {
            Position = new Vector3 { X = x + 1f,  Y = orbitOriginY + 1.5f, Z = z + 1f  },
            Rotation = new Vector3 { X = 0f,       Y = yaw,  Z = 0f      },
            Velocity = new Vector3 { X = velX,     Y = 0f,   Z = velZ    },
            AngVel   = new Vector3 { X = 0f,       Y = 0f,   Z = 0f      },
            IsTruck  = false,
            InSeat   = false
        };
        server.Send(playerCmd.Serialize(PacketType.UpdatePosition), DeliveryMethod.Unreliable);
    }
});

// ── Main loop: console commands + poll ────────────────────────────────────

Console.WriteLine("[FakeClient] Running.  Commands: sector <name> | livery <id> | origin <x> <y> <z> | speed <u/s> | quit");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

while (!cts.Token.IsCancellationRequested)
{
    client.PollEvents();

    if (Console.KeyAvailable)
    {
        var line = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(line))
            continue;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "quit":
            case "exit":
                cts.Cancel();
                break;

            case "sector" when parts.Length == 2:
                SendSector(parts[1]);
                break;

            case "livery" when parts.Length == 2:
                SendLivery(parts[1]);
                break;

            case "origin" when parts.Length == 4:
                if (float.TryParse(parts[1], out var ox) &&
                    float.TryParse(parts[2], out var oy) &&
                    float.TryParse(parts[3], out var oz))
                {
                    orbitOriginX = ox;
                    orbitOriginY = oy;
                    orbitOriginZ = oz;
                    orbitAngle   = 0f;
                    Console.WriteLine($"[FakeClient] Orbit origin set to ({ox}, {oy}, {oz})");
                }
                else
                {
                    Console.WriteLine("[FakeClient] Usage: origin <x> <y> <z>  (float values)");
                }
                break;

            case "speed" when parts.Length == 2:
                if (float.TryParse(parts[1], out var newSpeed) && newSpeed >= 0f)
                {
                    orbitSpeed = newSpeed;
                    Console.WriteLine($"[FakeClient] Orbit speed set to {newSpeed} u/s" +
                                      $"  (full orbit ≈ {(newSpeed > 0f ? (MathF.Tau * OrbitRadius / newSpeed):float.PositiveInfinity):F1} s)");
                }
                else
                {
                    Console.WriteLine("[FakeClient] Usage: speed <value>  (non-negative float, world-units per second)");
                }
                break;

            default:
                Console.WriteLine("[FakeClient] Unknown command. Use: sector <name> | livery <id> | origin <x> <y> <z> | speed <u/s> | quit");
                break;
        }
    }

    Thread.Sleep(15);
}

await movementTask;
client.Stop();
Console.WriteLine("[FakeClient] Stopped.");
return 0;

// ── Event handlers ─────────────────────────────────────────────────────────

void OnPeerConnected(NetPeer peer)
{
    server = peer;
    Console.WriteLine($"[FakeClient] Connected to {peer.Address}:{peer.Port}");

    var hello = new ProtocolHelloCmd { ProtocolVersion = NetProtocol.CurrentVersion };
    peer.Send(hello.Serialize(PacketType.ProtocolHello), DeliveryMethod.ReliableOrdered);
}

void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
{
    Console.WriteLine($"[FakeClient] Disconnected: {info.Reason}");
    handshakeDone = false;
    server        = null;
    myNetId       = -1;
}

void OnNetworkError(System.Net.IPEndPoint endPoint, SocketError socketError)
{
    Console.Error.WriteLine($"[FakeClient] Network error: {socketError} at {endPoint}");
}

void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
{
    try
    {
        var packetType = (PacketType)reader.GetByte();
        var raw        = reader.GetRemainingBytes();

        switch (packetType)
        {
            case PacketType.ProtocolWelcome:
                var welcome  = PacketSerializer.Deserialize<ProtocolWelcomeDto>(raw);
                myNetId      = welcome.NetId;
                handshakeDone = true;
                Console.WriteLine($"[FakeClient] Handshake OK – NetId={myNetId}, ProtocolVersion={welcome.ProtocolVersion}");

                // Announce our sector and livery right after handshake
                SendSector(DEFAULT_SECTOR);
                SendLivery(DefaultLivery);
                break;

            case PacketType.ProtocolMismatch:
                var mismatch = PacketSerializer.Deserialize<ProtocolMismatchDto>(raw);
                Console.Error.WriteLine($"[FakeClient] Protocol mismatch! Client={mismatch.ClientVersion}, Server={mismatch.ServerVersion}, Min={mismatch.MinSupportedVersion}");
                peer.Disconnect();
                break;

            case PacketType.SyncPlayers:
                var sync = PacketSerializer.Deserialize<SyncPlayersDto>(raw);
                Console.WriteLine($"[FakeClient] SyncPlayers – {sync.Players.Length} player(s) already in session");
                foreach (var p in sync.Players)
                    Console.WriteLine($"             NetId={p.NetId}  Sector={p.Sector}  Livery={p.Livery}");
                break;

            case PacketType.PlayerConnected:
                var connected = PacketSerializer.Deserialize<PlayerSnapshotDto>(raw);
                if (connected.NetId != myNetId)
                    Console.WriteLine($"[FakeClient] Player connected – NetId={connected.NetId}  Sector={connected.Sector}");
                break;

            case PacketType.PlayerDisconnected:
                var disconnected = PacketSerializer.Deserialize<PlayerDisconnectedDto>(raw);
                Console.WriteLine($"[FakeClient] Player disconnected – NetId={disconnected.NetId}");
                break;

            case PacketType.UpdatePosition:
                // Avoid spamming the console; only log non-local players
                var pos = PacketSerializer.Deserialize<UpdatePositionDto>(raw);
                if (pos.NetId != myNetId)
                    Console.WriteLine($"[FakeClient] Pos  NetId={pos.NetId}  IsTruck={pos.IsTruck}  ({pos.Position.X:F1}, {pos.Position.Y:F1}, {pos.Position.Z:F1})");
                break;

            case PacketType.UpdateSector:
                var sectorDto = PacketSerializer.Deserialize<UpdateSectorDto>(raw);
                if (sectorDto.NetId != myNetId)
                    Console.WriteLine($"[FakeClient] Sector update – NetId={sectorDto.NetId}  Sector={sectorDto.Sector}");
                break;

            case PacketType.UpdateLivery:
                var liveryDto = PacketSerializer.Deserialize<UpdateLiveryDto>(raw);
                if (liveryDto.NetId != myNetId)
                    Console.WriteLine($"[FakeClient] Livery update – NetId={liveryDto.NetId}  Livery={liveryDto.Livery}");
                break;

            default:
                Console.WriteLine($"[FakeClient] Unhandled packet type: {packetType}");
                break;
        }
    }
    finally
    {
        reader.Recycle();
    }
}

// ── Helper senders ─────────────────────────────────────────────────────────

void SendSector(string sector)
{
    if (server is null || !handshakeDone)
        return;

    currentSector = sector;
    var cmd = new UpdateSectorCmd { Sector = sector };
    server.Send(cmd.Serialize(PacketType.UpdateSector), DeliveryMethod.ReliableSequenced);
    Console.WriteLine($"[FakeClient] Sent sector: {sector}");
}

void SendLivery(string livery)
{
    if (server is null || !handshakeDone)
        return;

    var cmd = new UpdateLiveryCmd { Livery = livery };
    server.Send(cmd.Serialize(PacketType.UpdateLivery), DeliveryMethod.ReliableSequenced);
    Console.WriteLine($"[FakeClient] Sent livery: {livery}");
}

// ── Utility ────────────────────────────────────────────────────────────────

static bool TryParseEndpoint(string endpoint, out string host, out int port)
{
    host = endpoint;
    port = 7777;

    if (string.IsNullOrWhiteSpace(endpoint))
        return false;

    var trimmed   = endpoint.Trim();
    var delimiter = trimmed.LastIndexOf(':');
    if (delimiter <= 0)
    {
        host = trimmed;
        return true;
    }

    host = trimmed[..delimiter];
    return int.TryParse(trimmed[(delimiter + 1)..], out port);
}
