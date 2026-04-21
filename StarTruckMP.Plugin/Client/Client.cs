using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;
using StarTruckMP.Encoding;
using StarTruckMP.Shared;
using StarTruckMP.Shared.Cmd;
using StarTruckMP.Shared.Dto;
using StarTruckMP.Utilities;
using UnityEngine;
using Object = UnityEngine.Object;
using Vector3 = UnityEngine.Vector3;

namespace StarTruckMP.Client;

public class StarTruckClient
{
    private const int DefaultServerPort = 7777;
    private const int RetryDelayMs = 100;

    private static NetManager? _client;
    private static NetPeer? _server;
    private static readonly Dictionary<int, playerInfo> _playerList = new();
    private static string _currentSector = "none";
    private static movementTrans _playerTrans;
    private static movementTrans _truckTrans;
    public static GameObject? MyPlayer;
    private static Rigidbody? _myPlayerRigid;
    public static GameObject? MyTruck;
    private static Rigidbody? _myTruckRigid;
    private static GameObject? _playerCam;
    public static FloatingOriginManager? FloatingOrigin;
    private static PlayerLocation? _playerLocation;
    public static GameObject? SpaceSuitObj;
    public static Material[]? SpaceSuitMats;
    private static CancellationTokenSource? _movementLoopCts;
    private static System.Threading.Tasks.Task? _movementLoopTask;
    private static bool _handshakeCompleted;

    public static void FixedUpdate()
    {
        _client?.PollEvents();
    }

    public static void Update()
    {
        if (Input.GetKeyDown(StarTruckMP.JoinKey.Value))
        {
            if (_client is not { IsRunning: true } || !IsConnected())
            {
                ConnectToServer(StarTruckMP.IPAddress.Value);
            }
        }
        else if (Input.GetKeyDown(StarTruckMP.ReconnectKey.Value))
        {
            if (_client is { IsRunning: true } && IsConnected())
            {
                StarTruckMP.Log.LogInfo("Already connected to a server. Disconnecting first...");
                _server?.Disconnect();
            }

            ConnectToServer(StarTruckMP.IPAddress.Value);
        }
    }

    public static void ConnectToServer(string endpoint)
    {
        if (!TryParseEndpoint(endpoint, out var host, out var port))
        {
            StarTruckMP.Log.LogError($"Invalid endpoint '{endpoint}'. Expected format host:port");
            return;
        }

        if (_client is { IsRunning: true })
            _client.Stop();

        StopMovementLoop();

        var listener = new EventBasedNetListener();
        _client = new NetManager(listener);

        listener.PeerConnectedEvent += ListenerOnPeerConnectedEvent;
        listener.PeerDisconnectedEvent += ListenerOnPeerDisconnectedEvent;
        listener.NetworkErrorEvent += ListenerOnNetworkErrorEvent;
        listener.NetworkReceiveEvent += ListenerOnNetworkReceiveEvent;

        _client.Start();
        _server = _client.Connect(host, port, NetworkConstants.ConnectKey);
        _handshakeCompleted = false;

        ResolveLocalReferences();
    }

    private static void ListenerOnNetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            var packetType = (PacketType)reader.GetByte();
            var raw = reader.GetRemainingBytes();

            if (packetType is not PacketType.ProtocolWelcome and not PacketType.ProtocolMismatch && !_handshakeCompleted)
                return;

            switch (packetType)
            {
                case PacketType.ProtocolWelcome:
                    HandleProtocolWelcome(PacketSerializer.Deserialize<ProtocolWelcomeDto>(raw));
                    break;
                case PacketType.ProtocolMismatch:
                    HandleProtocolMismatch(PacketSerializer.Deserialize<ProtocolMismatchDto>(raw));
                    break;
                case PacketType.SyncPlayers:
                    HandleSyncPlayers(PacketSerializer.Deserialize<SyncPlayersDto>(raw));
                    break;
                case PacketType.PlayerConnected:
                    HandlePlayerConnected(PacketSerializer.Deserialize<PlayerSnapshotDto>(raw));
                    break;
                case PacketType.PlayerDisconnected:
                    HandlePlayerDisconnected(PacketSerializer.Deserialize<PlayerDisconnectedDto>(raw));
                    break;
                case PacketType.UpdatePosition:
                    HandlePositionUpdate(PacketSerializer.Deserialize<UpdatePositionDto>(raw));
                    break;
                case PacketType.UpdateSector:
                    HandleSectorUpdate(PacketSerializer.Deserialize<UpdateSectorDto>(raw));
                    break;
                case PacketType.UpdateLivery:
                    HandleLiveryUpdate(PacketSerializer.Deserialize<UpdateLiveryDto>(raw));
                    break;
                default:
                    StarTruckMP.Log.LogWarning($"Unhandled packet type: {packetType}");
                    break;
            }
        }
        finally
        {
            reader.Recycle();
        }
    }

    private static void ListenerOnNetworkErrorEvent(IPEndPoint endPoint, SocketError socketError)
    {
        StarTruckMP.Log.LogError($"Network error: {socketError} at {endPoint}");
    }

    private static void ListenerOnPeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        StarTruckMP.Log.LogInfo($"Disconnected from Server: {disconnectInfo.Reason}");
        StopMovementLoop();
        _handshakeCompleted = false;
        ClearRemotePlayers();
    }

    private static void ListenerOnPeerConnectedEvent(NetPeer peer)
    {
        StarTruckMP.Log.LogInfo($"Connected to Server: {peer.Address}:{peer.Port}");
        _handshakeCompleted = false;
        ResolveLocalReferences();

        var hello = new ProtocolHelloCmd { ProtocolVersion = NetProtocol.CurrentVersion };
        peer.Send(hello.Serialize(PacketType.ProtocolHello), DeliveryMethod.ReliableOrdered);
    }

    private static void HandleProtocolWelcome(ProtocolWelcomeDto data)
    {
        _handshakeCompleted = true;
        StarTruckMP.Log.LogInfo($"Protocol handshake completed. Version={data.ProtocolVersion}, NetId={data.NetId}");
        OnArrivedAtSector();
        StartMovementLoop();
    }

    private static void HandleProtocolMismatch(ProtocolMismatchDto data)
    {
        _handshakeCompleted = false;
        StarTruckMP.Log.LogError($"Protocol mismatch. Client={data.ClientVersion}, Server={data.ServerVersion}, MinSupported={data.MinSupportedVersion}");
        _server?.Disconnect();
    }

    private static void HandleSyncPlayers(SyncPlayersDto data)
    {
        foreach (var player in data.Players)
            ApplySnapshot(player);
    }

    private static void HandlePlayerConnected(PlayerSnapshotDto data)
    {
        ApplySnapshot(data);
    }

    private static void HandlePlayerDisconnected(PlayerDisconnectedDto data)
    {
        if (_playerList.TryGetValue(data.NetId, out var player))
        {
            DestroyRemotePlayerObjects(player);
            _playerList.Remove(data.NetId);
        }
    }

    private static void HandlePositionUpdate(UpdatePositionDto data)
    {
        var player = GetOrCreatePlayerInfo(data.NetId, "none");

        var pos = ToUnity(data.Position);
        var rot = ToUnity(data.Rotation);
        var vel = ToUnity(data.Velocity);
        var angVel = ToUnity(data.AngVel);

        if (data.IsTruck)
        {
            player.truckTrans.Pos = pos;
            player.truckTrans.Rot = rot;
            player.truckTrans.Vel = vel;
            player.truckTrans.AngVel = angVel;
            Messages.updateMovement(player.Truck, pos, rot, vel, angVel);

            if (data.InSeat)
            {
                player.playerTrans.Pos = pos;
                player.playerTrans.Rot = rot;
                player.playerTrans.Vel = vel;
                player.playerTrans.AngVel = angVel;
                Messages.updateMovement(player.Player, pos, rot, vel, angVel);
            }
        }
        else
        {
            player.playerTrans.Pos = pos;
            player.playerTrans.Rot = rot;
            player.playerTrans.Vel = vel;
            player.playerTrans.AngVel = angVel;
            Messages.updateMovement(player.Player, pos, rot, vel, angVel);
        }

        _playerList[data.NetId] = player;
        UpdateSectorVisibility(data.NetId, player);
    }

    private static void HandleSectorUpdate(UpdateSectorDto data)
    {
        var player = GetOrCreatePlayerInfo(data.NetId);

        player.sector = data.Sector;
        _playerList[data.NetId] = player;
        UpdateSectorVisibility(data.NetId, player);
    }

    private static void HandleLiveryUpdate(UpdateLiveryDto data)
    {
        var player = GetOrCreatePlayerInfo(data.NetId);

        player.livery = data.Livery;
        _playerList[data.NetId] = player;
        TryApplyLivery(player, data.Livery);
    }

    private static void ApplySnapshot(PlayerSnapshotDto snapshot)
    {
        var player = GetOrCreatePlayerInfo(snapshot.NetId);

        player.sector = snapshot.Sector;
        player.livery = snapshot.Livery;
        player.playerTrans = ToMovement(snapshot.Player);
        player.truckTrans = ToMovement(snapshot.Truck);
        _playerList[snapshot.NetId] = player;

        UpdateSectorVisibility(snapshot.NetId, player);
        if (_playerList.TryGetValue(snapshot.NetId, out var updated))
            TryApplyLivery(updated, snapshot.Livery);
    }

    private static void StartMovementLoop()
    {
        if (_movementLoopTask is { IsCompleted: false })
            return;

        _movementLoopCts = new CancellationTokenSource();
        _movementLoopTask = SendMovementLoopAsync(_movementLoopCts.Token);
    }

    private static void StopMovementLoop()
    {
        if (_movementLoopCts is null)
            return;

        _movementLoopCts.Cancel();
        _movementLoopCts.Dispose();
        _movementLoopCts = null;
    }

    private static async System.Threading.Tasks.Task SendMovementLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _client is { IsRunning: true })
        {
            if (_server is null || !IsConnected() || !ResolveLocalReferences())
            {
                await System.Threading.Tasks.Task.Delay(RetryDelayMs, cancellationToken);
                continue;
            }

            if (MyTruck != null && _playerLocation != null && FloatingOrigin != null && _myTruckRigid != null)
            {
                var truckPosition = FloatingOrigin.m_currentOrigin + MyTruck.transform.position;
                var truckRotation = MyTruck.transform.eulerAngles;
                var truckVelocity = _myTruckRigid.velocity;
                var truckAngVel = _myTruckRigid.angularVelocity;

                if (truckPosition != _truckTrans.Pos || truckRotation != _truckTrans.Rot || truckVelocity != _truckTrans.Vel || truckAngVel != _truckTrans.AngVel)
                {
                    _truckTrans.Pos = truckPosition;
                    _truckTrans.Rot = truckRotation;
                    _truckTrans.Vel = truckVelocity;
                    _truckTrans.AngVel = truckAngVel;

                    var data = new UpdatePositionCmd()
                    {
                        Position = ToShared(truckPosition),
                        Rotation = ToShared(truckRotation),
                        Velocity = ToShared(truckVelocity),
                        AngVel = ToShared(truckAngVel),
                        IsTruck = true,
                        InSeat = false
                    };

                    _server.Send(data.Serialize(PacketType.UpdatePosition), DeliveryMethod.Unreliable);
                }
            }

            if (MyPlayer != null && _playerLocation != null)
            {
                if (_playerCam == null || _myPlayerRigid == null)
                {
                    await System.Threading.Tasks.Task.Delay(RetryDelayMs, cancellationToken);
                    continue;
                }

                if (PlayerLocation.worldPosition != _playerTrans.Pos || _playerCam.transform.eulerAngles != _playerTrans.Rot || _myPlayerRigid.velocity != _playerTrans.Vel || _myPlayerRigid.angularVelocity != _playerTrans.AngVel)
                {
                    _playerTrans.Pos = PlayerLocation.worldPosition;
                    _playerTrans.Rot = _playerCam.transform.eulerAngles;
                    _playerTrans.Vel = _myPlayerRigid.velocity;
                    _playerTrans.AngVel = _myPlayerRigid.angularVelocity;

                    var position = PlayerLocation.worldPosition + new Vector3(0, -1, 0);

                    var data = new UpdatePositionCmd
                    {
                        Position = ToShared(position),
                        Rotation = ToShared(_playerCam.transform.eulerAngles),
                        Velocity = ToShared(_myPlayerRigid.velocity),
                        AngVel = ToShared(_myPlayerRigid.angularVelocity),
                        IsTruck = false,
                        InSeat = false
                    };

                    _server.Send(data.Serialize(PacketType.UpdatePosition), DeliveryMethod.Unreliable);
                }
            }

            await System.Threading.Tasks.Task.Delay(StarTruckMP.MoveUpdate.Value, cancellationToken);
        }
    }

    public static void UpdateLivery(string livery)
    {
        if (!CanSendGameplayPackets())
            return;

        var server = _server;
        if (server == null)
            return;

        var data = new UpdateLiveryCmd() { Livery = livery };
        server.Send(data.Serialize(PacketType.UpdateLivery), DeliveryMethod.ReliableSequenced);
    }

    public static void OnArrivedAtSector()
    {
        if (!CanSendGameplayPackets())
            return;

        var server = _server;
        if (server == null)
            return;

        _currentSector = GameObject.Find("[Sector]").scene.name;
        var data = new UpdateSectorCmd() { Sector = _currentSector };
        server.Send(data.Serialize(PacketType.UpdateSector), DeliveryMethod.ReliableSequenced);

        StarTruckMP.Log.LogInfo($"Entered Sector: {_currentSector}");

        foreach (var (cId, c) in _playerList)
        {
            UpdateSectorVisibility(cId, c);
        }
    }

    private static void UpdateSectorVisibility(int clientId, playerInfo clientInfo)
    {
        if (clientInfo.sector != _currentSector)
        {
            DestroyRemotePlayerObjects(clientInfo);
            _playerList[clientId] = clientInfo;
        }
        else if (clientInfo.Truck == null && MyTruck != null && SpaceSuitObj != null)
        {
            playerInfo player = Messages.createPlayer(clientId, _playerList[clientId].truckTrans.Pos, _playerList[clientId].truckTrans.Rot, _currentSector);
            clientInfo.Truck = player.Truck;
            clientInfo.Player = player.Player;
            _playerList[clientId] = clientInfo;

            if (!string.IsNullOrWhiteSpace(clientInfo.livery))
                TryApplyLivery(clientInfo, clientInfo.livery);
        }
    }

    private static bool IsConnected()
    {
        return _server is { ConnectionState: ConnectionState.Connected };
    }

    private static bool CanSendGameplayPackets()
    {
        return _client is { IsRunning: true } && _server != null && _handshakeCompleted && IsConnected();
    }

    private static playerInfo GetOrCreatePlayerInfo(int netId, string defaultSector = "")
    {
        if (_playerList.TryGetValue(netId, out var player))
            return player;

        return string.IsNullOrEmpty(defaultSector)
            ? new playerInfo()
            : new playerInfo { sector = defaultSector };
    }

    private static void ClearRemotePlayers()
    {
        foreach (var player in _playerList.Values)
            DestroyRemotePlayerObjects(player);

        _playerList.Clear();
    }

    private static bool ResolveLocalReferences()
    {
        MyPlayer ??= GameObject.FindGameObjectWithTag("Player");
        _playerCam ??= GameObject.Find("Main Camera");
        MyTruck ??= GameObject.Find("StarTruck(Clone)");

        if (FloatingOrigin == null)
        {
            var floatingOriginObj = GameObject.Find("[FloatingOriginManager]");
            if (floatingOriginObj != null)
                FloatingOrigin = floatingOriginObj.GetComponent<FloatingOriginManager>();
        }

        if (MyPlayer != null)
        {
            _myPlayerRigid ??= MyPlayer.GetComponent<Rigidbody>();
            _playerLocation ??= MyPlayer.GetComponent<PlayerLocation>();
        }

        if (MyTruck != null)
        {
            _myTruckRigid ??= MyTruck.GetComponent<Rigidbody>();
            if (SpaceSuitObj == null)
            {
                var interior = MyTruck.transform.Find("Interior");
                var root = interior?.Find("SpaceSuit_Root");
                var suit = root?.Find("SpaceSuit");
                if (suit != null && suit.childCount > 0)
                    SpaceSuitObj = suit.GetChild(0).gameObject;
            }
        }

        if (SpaceSuitObj != null)
            SpaceSuitMats ??= SpaceSuitObj.GetComponent<MeshRenderer>().materials;

        return MyPlayer != null
               && MyTruck != null
               && _playerCam != null
               && _myPlayerRigid != null
               && _myTruckRigid != null
               && _playerLocation != null
               && FloatingOrigin != null
               && SpaceSuitObj != null
               && SpaceSuitMats != null;
    }

    private static movementTrans ToMovement(TransformDto dto)
    {
        return new movementTrans
        {
            Pos = ToUnity(dto.Position),
            Rot = ToUnity(dto.Rotation),
            Vel = ToUnity(dto.Velocity),
            AngVel = ToUnity(dto.AngVel)
        };
    }

    private static global::StarTruckMP.Shared.Vector3 ToShared(Vector3 value)
    {
        return new global::StarTruckMP.Shared.Vector3 { X = value.x, Y = value.y, Z = value.z };
    }

    private static Vector3 ToUnity(global::StarTruckMP.Shared.Vector3 value)
    {
        return new Vector3(value.X, value.Y, value.Z);
    }

    private static void DestroyRemotePlayerObjects(playerInfo player)
    {
        if (player.Player != null)
            Object.Destroy(player.Player);
        if (player.Truck != null)
            Object.Destroy(player.Truck);
    }

    private static void TryApplyLivery(playerInfo player, string livery)
    {
        if (player.Truck == null || string.IsNullOrWhiteSpace(livery))
            return;

        var exterior = player.Truck.transform.childCount > 0 ? player.Truck.transform.GetChild(0) : null;
        var applier = exterior?.GetComponent<LiveryAndDamageApplierTruckExterior>();
        applier?.LoadAndApplyLiveryById(livery);
    }

    private static bool TryParseEndpoint(string endpoint, out string host, out int port)
    {
        host = endpoint;
        port = DefaultServerPort;

        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var trimmed = endpoint.Trim();
        var delimiter = trimmed.LastIndexOf(':');
        if (delimiter <= 0)
        {
            host = trimmed;
            return true;
        }

        host = trimmed.Substring(0, delimiter);
        return int.TryParse(trimmed.Substring(delimiter + 1), out port);
    }
}