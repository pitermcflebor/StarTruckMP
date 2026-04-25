using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using LiteNetLib;
using Microsoft.Extensions.Logging;
using StarTruckMP.Server.Controllers.Services;
using StarTruckMP.Server.Entities;
using StarTruckMP.Server.Server.Services;
using StarTruckMP.Shared;
using StarTruckMP.Shared.Cmd;
using StarTruckMP.Shared.Dto;

namespace StarTruckMP.Server.Server;

public class ServerManager
{
    private const byte ReliableChannel = 0;
    private const int MaxIncomingPacketsPerTick = 256;
    private const int MaxOutgoingPacketsPerTick = 512;

    private readonly EventBasedNetListener _listener;
    private readonly NetManager _server;
    private readonly ILogger _logger;
    private readonly ServerSettings _settings;
    private readonly PlayerContainer _playerContainer;
    private readonly AuthService _authService;
    private readonly ConcurrentQueue<IncomingPacketWorkItem> _incomingPackets = new();
    private readonly ConcurrentQueue<OutgoingSendWorkItem> _outgoingPackets = new();

    private readonly record struct IncomingPacketWorkItem(int PeerId, PacketType PacketType, byte[] Raw, byte Channel, DeliveryMethod DeliveryMethod);

    private readonly record struct OutgoingSendWorkItem(byte[] Payload, byte Channel, DeliveryMethod DeliveryMethod, int? TargetPeerId = null, int? ExceptPeerId = null, bool DisconnectTarget = false);

    public ServerManager(ILogger<ServerManager> logger, ServerSettings settings, PlayerContainer playerContainer, AuthService authService)
    {
        _listener = new EventBasedNetListener();
        _server = new NetManager(_listener);
        _logger = logger;
        _settings = settings;
        _playerContainer = playerContainer;
        _authService = authService;

        _listener.ConnectionRequestEvent += ListenerOnConnectionRequestEvent;
        
        _listener.NetworkReceiveEvent += ListenerOnNetworkReceiveEvent;
        _listener.NetworkErrorEvent += ListenerOnNetworkErrorEvent;
        _listener.NetworkLatencyUpdateEvent += ListenerOnNetworkLatencyUpdateEvent;
        _listener.NetworkReceiveUnconnectedEvent += ListenerOnNetworkReceiveUnconnectedEvent;
        
        _listener.PeerConnectedEvent += ListenerOnPeerConnectedEvent;
        _listener.PeerAddressChangedEvent += ListenerOnPeerAddressChangedEvent;
        _listener.PeerDisconnectedEvent += ListenerOnPeerDisconnectedEvent;
        
        _listener.DeliveryEvent += ListenerOnDeliveryEvent;
    }

    #region Net Events

    #region Network
    
    private void ListenerOnNetworkReceiveUnconnectedEvent(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Received unconnected message from {EndPoint}, type: {MessageType}", remoteEndPoint, messageType);
    }

    private void ListenerOnNetworkLatencyUpdateEvent(NetPeer peer, int latency)
    {
        // TODO: really needed?
        /*if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Peer {PeerId} latency updated: {Latency} ms", peer.Id, latency);*/
    }

    private void ListenerOnNetworkErrorEvent(IPEndPoint endPoint, SocketError socketError)
    {
        if (_logger.IsEnabled(LogLevel.Error))
            _logger.LogError("Network error from {EndPoint}, socket error: {SocketError}", endPoint, socketError);
    }

    private void ListenerOnNetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            var packetType = (PacketType)reader.GetByte();
            var raw = reader.GetRemainingBytes();
            _incomingPackets.Enqueue(new IncomingPacketWorkItem(peer.Id, packetType, raw, channel, deliveryMethod));
        }
        finally
        {
            reader.Recycle();
        }
    }
    
    #endregion

    #region Peer
    
    private void ListenerOnPeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Peer {PeerId} disconnected from {EndPoint}, reason: {Reason}", peer.Id, peer.Address, disconnectInfo.Reason);

        _playerContainer.RemovePlayer(peer.Id, out var removedPlayer);
        if (removedPlayer is not { HandshakeCompleted: true })
            return;

        var payload = new PlayerDisconnectedDto { NetId = peer.Id };
        QueueSendReliableToAllExcept(payload.Serialize(PacketType.PlayerDisconnected), peer.Id);
    }

    private void ListenerOnPeerAddressChangedEvent(NetPeer peer, IPEndPoint previousAddress)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Peer {PeerId} changed address from {PreviousAddress} to {CurrentAddress}", peer.Id, previousAddress, peer.Address);
    }
    
    private void ListenerOnPeerConnectedEvent(NetPeer peer)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Peer {PeerId} connected from {EndPoint}", peer.Id, peer.Address);

        _playerContainer.RegisterPlayer(peer.Id);
    }
    
    #endregion

    #region Other
    
    private void ListenerOnDeliveryEvent(NetPeer peer, object userData)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Delivered data to peer {PeerId}", peer.Id);
    }
    
    private void ListenerOnConnectionRequestEvent(ConnectionRequest request)
    {
        if (_logger.IsEnabled(LogLevel.Debug)) 
            _logger.LogDebug("Connection request from {EndPoint}", request.RemoteEndPoint);

        var raw = request.Data.GetRemainingBytesSpan();
        if (!PacketSerializer.TrySplitPacket<ProtocolAuthenticateCmd>(raw, out var packetType, out var authenticate))
        {
            // We need to notify the rejection?
            request.RejectForce();
            _logger.LogWarning("Rejected connection from {EndPoint} due to invalid initial packet!", request.RemoteEndPoint);
            return;
        }

        // Validate token
        if (packetType != PacketType.ProtocolAuthenticate && 
            !_authService.IsTokenValid(authenticate.Token))
        {
            request.Reject();
            _logger.LogWarning("Rejected connection from {EndPoint} due to invalid token: {token}.", request.RemoteEndPoint, authenticate.Token);
            return;
        }
        
        var peer = request.Accept();

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Accepted connection, peer {peerId}", peer.Id);
        
        
    }
    
    #endregion

    #endregion

    public void Start()
    {
        _server.Start(_settings.Port);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Server started on port {Port}", _settings.Port);
    }
    
    public void Polling()
    {
        _server.PollEvents();
        ProcessIncomingQueue();
        ProcessOutgoingQueue();
    }

    public void Stop()
    {
        ClearQueues();
        _server.Stop();
    }

    private void ProcessIncomingQueue()
    {
        var processed = 0;
        while (processed < MaxIncomingPacketsPerTick && _incomingPackets.TryDequeue(out var packet))
        {
            ProcessIncomingPacket(packet);
            processed++;
        }
    }

    private void ProcessIncomingPacket(IncomingPacketWorkItem packet)
    {
        var player = GetKnownPlayerOrNull(packet.PeerId, packet.PacketType);
        if (player is null)
            return;

        if (packet.PacketType == PacketType.ProtocolHello)
        {
            HandleProtocolHello(packet.PeerId, player, packet.Raw);
            return;
        }

        if (!player.HandshakeCompleted)
        {
            _logger.LogWarning("Ignoring packet {PacketType} from peer {PeerId} before protocol handshake", packet.PacketType, packet.PeerId);
            return;
        }

        switch (packet.PacketType)
        {
            case PacketType.UpdatePosition:
                HandleUpdatePosition(packet.PeerId, player, packet.Raw, packet.Channel, packet.DeliveryMethod);
                break;
            case PacketType.UpdateSector:
                HandleUpdateSector(packet.PeerId, player, packet.Raw);
                break;
            case PacketType.UpdateLivery:
                HandleUpdateLivery(packet.PeerId, player, packet.Raw);
                break;
            default:
                _logger.LogWarning("Unhandled packet type {PacketType} from peer {PeerId}", packet.PacketType, packet.PeerId);
                break;
        }
    }

    private void ProcessOutgoingQueue()
    {
        var processed = 0;
        while (processed < MaxOutgoingPacketsPerTick && _outgoingPackets.TryDequeue(out var send))
        {
            if (send.TargetPeerId.HasValue)
            {
                var targetPeer = _server.GetPeerById(send.TargetPeerId.Value);
                if (targetPeer is not null)
                {
                    targetPeer.Send(send.Payload, send.DeliveryMethod);
                    if (send.DisconnectTarget)
                        targetPeer.Disconnect();
                }

                processed++;
                continue;
            }

            var exceptPeer = send.ExceptPeerId.HasValue ? _server.GetPeerById(send.ExceptPeerId.Value) : null;
            _server.SendToAll(send.Payload, send.Channel, send.DeliveryMethod, exceptPeer);
            processed++;
        }
    }

    private void ClearQueues()
    {
        while (_incomingPackets.TryDequeue(out _))
        {
        }

        while (_outgoingPackets.TryDequeue(out _))
        {
        }
    }

    private static PlayerSnapshotDto ToSnapshot(Player player)
    {
        return new PlayerSnapshotDto
        {
            NetId = player.Id,
            Sector = player.Sector,
            Livery = player.Livery,
            Player = new TransformDto
            {
                Position = player.PlayerPosition,
                Rotation = player.PlayerRotation,
                Velocity = player.PlayerVelocity,
                AngVel = player.PlayerAngVel
            },
            Truck = new TransformDto
            {
                Position = player.TruckPosition,
                Rotation = player.TruckRotation,
                Velocity = player.TruckVelocity,
                AngVel = player.TruckAngVel
            }
        };
    }

    private void HandleProtocolHello(int peerId, Player player, byte[] raw)
    {
        var hello = PacketSerializer.Deserialize<ProtocolHelloCmd>(raw);
        if (!IsVersionSupported(hello.ProtocolVersion))
        {
            var mismatch = new ProtocolMismatchDto
            {
                ClientVersion = hello.ProtocolVersion,
                MinSupportedVersion = NetProtocol.MinSupportedVersion,
                ServerVersion = NetProtocol.CurrentVersion
            };

            QueueSendToPeer(mismatch.Serialize(PacketType.ProtocolMismatch), peerId, ReliableChannel, DeliveryMethod.ReliableOrdered, disconnectAfterSend: true);
            return;
        }

        if (player.HandshakeCompleted)
            return;

        player.HandshakeCompleted = true;
        player.ProtocolVersion = hello.ProtocolVersion;

        var welcome = new ProtocolWelcomeDto
        {
            NetId = peerId,
            ProtocolVersion = NetProtocol.CurrentVersion
        };

        QueueSendToPeer(welcome.Serialize(PacketType.ProtocolWelcome), peerId, ReliableChannel, DeliveryMethod.ReliableOrdered);

        var existingPlayers = _playerContainer
            .SnapshotPlayers()
            .Where(x => x.Id != peerId && x.HandshakeCompleted)
            .Select(ToSnapshot)
            .ToArray();

        var sync = new SyncPlayersDto { Players = existingPlayers };
        QueueSendToPeer(sync.Serialize(PacketType.SyncPlayers), peerId, ReliableChannel, DeliveryMethod.ReliableOrdered);

        var connectedPayload = ToSnapshot(player);
        QueueSendReliableToAllExcept(connectedPayload.Serialize(PacketType.PlayerConnected), peerId);
    }

    private static bool IsVersionSupported(ushort version)
    {
        return version >= NetProtocol.MinSupportedVersion && version <= NetProtocol.CurrentVersion;
    }

    private Player? GetKnownPlayerOrNull(int peerId, PacketType packetType)
    {
        if (_playerContainer.TryGetPlayer(peerId, out var player) && player is not null)
            return player;

        _logger.LogWarning("Ignoring packet {PacketType} from unknown peer {PeerId}", packetType, peerId);
        return null;
    }

    private void HandleUpdatePosition(int peerId, Player player, byte[] raw, byte channel, DeliveryMethod deliveryMethod)
    {
        var positionData = PacketSerializer.Deserialize<UpdatePositionCmd>(raw);

        if (positionData.IsTruck)
        {
            player.TruckPosition = positionData.Position;
            player.TruckRotation = positionData.Rotation;
            player.TruckVelocity = positionData.Velocity;
            player.TruckAngVel = positionData.AngVel;

            if (positionData.InSeat)
            {
                player.PlayerPosition = positionData.Position;
                player.PlayerRotation = positionData.Rotation;
                player.PlayerVelocity = positionData.Velocity;
                player.PlayerAngVel = positionData.AngVel;
            }
        }
        else
        {
            player.PlayerPosition = positionData.Position;
            player.PlayerRotation = positionData.Rotation;
            player.PlayerVelocity = positionData.Velocity;
            player.PlayerAngVel = positionData.AngVel;
        }

        var updateData = new UpdatePositionDto
        {
            NetId = peerId,
            Position = positionData.Position,
            Rotation = positionData.Rotation,
            Velocity = positionData.Velocity,
            AngVel = positionData.AngVel,
            IsTruck = positionData.IsTruck,
            InSeat = positionData.InSeat
        };

        QueueSendToAllExcept(updateData.Serialize(PacketType.UpdatePosition), peerId, channel, deliveryMethod);
    }

    private void HandleUpdateSector(int peerId, Player player, byte[] raw)
    { 
        var sectorData = PacketSerializer.Deserialize<UpdateSectorCmd>(raw);
        player.Sector = string.IsNullOrWhiteSpace(sectorData.Sector) ? "none" : sectorData.Sector;
        
        var update = new UpdateSectorDto { NetId = peerId, Sector = player.Sector };
        QueueSendReliableToAllExcept(update.Serialize(PacketType.UpdateSector), peerId);
        
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Peer {peerId} updated sector to '{sector}'", peerId, sectorData.Sector);
    }

    private void HandleUpdateLivery(int peerId, Player player, byte[] raw)
    {
        var liveryData = PacketSerializer.Deserialize<UpdateLiveryCmd>(raw);
        player.Livery = liveryData.Livery;

        var update = new UpdateLiveryDto { NetId = peerId, Livery = player.Livery };
        QueueSendReliableToAllExcept(update.Serialize(PacketType.UpdateLivery), peerId);
        
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Peer {peerId} updated livery to '{livery}'", peerId, liveryData.Livery);
    }

    private void QueueSendReliableToAllExcept(byte[] payload, int exceptPeerId)
    {
        QueueSendToAllExcept(payload, exceptPeerId, ReliableChannel, DeliveryMethod.ReliableOrdered);
    }

    private void QueueSendToPeer(byte[] payload, int targetPeerId, byte channel, DeliveryMethod deliveryMethod, bool disconnectAfterSend = false)
    {
        _outgoingPackets.Enqueue(new OutgoingSendWorkItem(payload, channel, deliveryMethod, TargetPeerId: targetPeerId, DisconnectTarget: disconnectAfterSend));
    }

    private void QueueSendToAllExcept(byte[] payload, int exceptPeerId, byte channel, DeliveryMethod deliveryMethod)
    {
        _outgoingPackets.Enqueue(new OutgoingSendWorkItem(payload, channel, deliveryMethod, ExceptPeerId: exceptPeerId));
    }
}