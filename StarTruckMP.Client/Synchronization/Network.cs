using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;
using StarTruckMP.Client.Components;
using StarTruckMP.Shared;
using StarTruckMP.Shared.Cmd;
using StarTruckMP.Shared.Dto;

namespace StarTruckMP.Client.Synchronization;

public class Network
{
    public static event Action<int> OnConnected;
    public static event Action OnDisconnected;
    public static event Action<UpdatePositionDto> OnPlayerPositionUpdate;
    public static event Action<UpdateLiveryDto> OnTruckLiveryUpdate;
    public static event Action<UpdateSectorDto> OnPlayerSectorUpdate;
    public static event Action<int> OnPlayerDisconnected;
    public static event Action<UpdateTrailerDto> OnTrailerUpdate; 

    private static bool _isInitialized;
    private static NetManager _client;
    private static NetPeer _server;
    private static bool _handshakeCompleted = false;
    private static int _netId = -1;
    
    public static int NetId => _netId;
    
    /// <summary>
    /// This should only run once, on plugin startup.
    /// </summary>
    /// <returns></returns>
    public static void SetupConnection()
    {
        GameEventsComponent.ArrivedAtSector += _ =>
        {
            if (_isInitialized) return;
            
            // Loaded into the map, start server connection
            _isInitialized = true;

            Plugin.StartAttachedThread(Polling);
        };
    }

    public static void SendServerMessage<T>(T data, PacketType packetType)
    {
        try
        {
            _server.Send(data.Serialize(packetType), packetType switch
            {
                PacketType.UpdatePosition => DeliveryMethod.Unreliable,
                PacketType.ProtocolHello => DeliveryMethod.ReliableOrdered,
                _ => DeliveryMethod.ReliableSequenced
            });
            
            if (packetType != PacketType.UpdatePosition)
                App.Log.LogInfo($"Packet:{packetType} out {data.Serialize(packetType).Length} bytes");
        }
        catch (Exception e)
        {
            App.Log.LogError("Failed to send message to server:");
            App.Log.LogError(e);
        }
    }

    private static void Polling()
    {
        // Setup connection
        try
        {
            if (!IPEndPoint.TryParse($"{App.ServerAddress.Value}:{App.ServerPort.Value}", out var endPoint))
            {
                App.Log.LogError($"Invalid endpoint {App.ServerAddress.Value}:{App.ServerPort.Value}");
                return;
            }

            var listener = new EventBasedNetListener();
            _client  = new NetManager(listener);

            listener.PeerConnectedEvent += ListenerOnPeerConnectedEvent;
            listener.PeerDisconnectedEvent += ListenerOnPeerDisconnectedEvent;
            listener.NetworkErrorEvent += ListenerOnNetworkErrorEvent;
            listener.NetworkReceiveEvent += ListenerOnNetworkReceiveEvent;
            
            _client.Start();

            var data = new ProtocolAuthenticateCmd
            {
                Token = PlayerState.Token
            };
            
            _server = _client.Connect(endPoint, data.Serialize(PacketType.ProtocolAuthenticate));
        }
        catch (Exception e)
        {
            App.Log.LogError("Failed to connect to server:");
            App.Log.LogError(e);
            return;
        }
        
        while (!_handshakeCompleted)
        {
            _client.PollEvents();
            Thread.Sleep(50);
        }

        while ((_server.ConnectionState & ConnectionState.Connected) == ConnectionState.Connected)
        {
            _client.PollEvents();
            Thread.Sleep(50);
        }
        
        App.Log.LogError("Server Polling end. Connection state: " + _server.ConnectionState);
    }

    private static void ListenerOnNetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            var packetType = (PacketType)reader.GetByte();
            var raw = reader.GetRemainingBytes();

            if (packetType is not PacketType.ProtocolWelcome and not PacketType.ProtocolMismatch &&
                !_handshakeCompleted)
                return;

            if (packetType != PacketType.UpdatePosition)
                App.Log.LogInfo($"Packet:{packetType} in {raw.Length} bytes");
            
            switch (packetType)
            {
                // ordered by most common to less common
                case PacketType.UpdatePosition:
                    HandlePositionUpdate(raw.Deserialize<UpdatePositionDto>());
                    break;
                case PacketType.UpdateTrailer:
                    HandleTrailerUpdate(raw.Deserialize<UpdateTrailerDto>());
                    break;
                case PacketType.UpdateLivery:
                    HandleUpdateLivery(raw.Deserialize<UpdateLiveryDto>());
                    break;
                case PacketType.UpdateSector:
                    HandleSectorUpdate(raw.Deserialize<UpdateSectorDto>());
                    break;
                case PacketType.SyncPlayers:
                    HandleSyncPlayers(raw.Deserialize<SyncPlayersDto>());
                    break;
                case PacketType.PlayerConnected:
                    HandlePlayerConnected(raw.Deserialize<PlayerSnapshotDto>());
                    break;
                case PacketType.PlayerDisconnected:
                    HandlePlayerDisconnected(raw.Deserialize<PlayerDisconnectedDto>());
                    break;
                case PacketType.ProtocolWelcome:
                    HandleWelcome(raw.Deserialize<ProtocolWelcomeDto>());
                    break;
                case PacketType.ProtocolMismatch:
                    HandleMismatch(raw.Deserialize<ProtocolMismatchDto>());
                    break;
                default:
                    App.Log.LogError($"Received not handled packet type {packetType}");
                    break;
            }
        }
        catch (Exception ex)
        {
            App.Log.LogError("Error handling network message:");
            App.Log.LogError(ex);
        }
        finally
        {
            reader.Recycle();
        }
    }

    private static void HandlePlayerDisconnected(PlayerDisconnectedDto disconnected)
    {
        OnPlayerDisconnected?.Invoke(disconnected.NetId);
    }

    private static void HandlePlayerConnected(PlayerSnapshotDto snapshot)
    {
        if (snapshot.NetId == _netId)
        {
            App.Log.LogInfo("Received own player snapshot, ignoring.");
            return;
        }

        OnPlayerSectorUpdate?.Invoke(new UpdateSectorDto
        {
            NetId = snapshot.NetId,
            Sector = snapshot.Sector
        });
        
        OnPlayerPositionUpdate?.Invoke(new UpdatePositionDto
        {
            NetId = snapshot.NetId,
            Position = snapshot.Player.Position,
            Rotation = snapshot.Player.Rotation,
            Velocity = snapshot.Player.Velocity,
            AngVel = snapshot.Player.AngVel,
            IsTruck = false,
            InSeat = false
        });
        
        OnTruckLiveryUpdate?.Invoke(new UpdateLiveryDto
        {
            NetId = snapshot.NetId,
            Livery = snapshot.Livery,
        });
        
        OnTrailerUpdate?.Invoke(new UpdateTrailerDto
        {
            NetId = snapshot.NetId,
            TrailerCount = snapshot.TrailersCount,
            LiveryId = snapshot.TrailerLivery,
            CargoTypeId = snapshot.TrailerCargoTypeId
        });
    }

    private static void HandleSyncPlayers(SyncPlayersDto syncPlayers)
    {
        foreach (var snapshot in syncPlayers.Players) HandlePlayerConnected(snapshot);
    }

    private static void HandleSectorUpdate(UpdateSectorDto sector)
    {
        OnPlayerSectorUpdate?.Invoke(sector);
    }

    private static void HandleUpdateLivery(UpdateLiveryDto livery)
    {
        OnTruckLiveryUpdate?.Invoke(livery);
    }

    private static void HandlePositionUpdate(UpdatePositionDto position)
    {
        OnPlayerPositionUpdate?.Invoke(position);
    }

    private static void HandleMismatch(ProtocolMismatchDto mismatch)
    {
        App.Log.LogError($"Protocol mismatch with server ({mismatch.ServerVersion}). Please update your client (min {mismatch.MinSupportedVersion}).");
        _handshakeCompleted = false;
        _server.Disconnect();
    }

    private static void HandleWelcome(ProtocolWelcomeDto welcome)
    {
        _handshakeCompleted = true;
        _netId = welcome.NetId;
        OnConnected?.Invoke(_netId);
        App.Log.LogInfo("Handshake completed with server. NetId: " + _netId);
    }

    private static void HandleTrailerUpdate(UpdateTrailerDto trailer)
    {
        OnTrailerUpdate?.Invoke(trailer);
    }

    private static void ListenerOnNetworkErrorEvent(IPEndPoint endPoint, SocketError socketError)
    {
        App.Log.LogError($"Network error: {socketError}");
    }

    private static void ListenerOnPeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        App.Log.LogInfo($"Disconnected from server {peer.Address}:{peer.Port} ({disconnectInfo.Reason})");
        _handshakeCompleted = false;
        OnDisconnected?.Invoke();
    }

    private static void ListenerOnPeerConnectedEvent(NetPeer peer)
    {
        _handshakeCompleted = false;

        var hello = new ProtocolHelloCmd() { ProtocolVersion = NetProtocol.CurrentVersion };
        SendServerMessage(hello, PacketType.ProtocolHello);
        App.Log.LogInfo($"Connected to server {peer.Address}:{peer.Port}, waiting for handshake...");
    }
}