using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using StarTruckMP.Client.Components;
using StarTruckMP.Client.Crypto;
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
    public static event Action<VoiceDto> OnVoiceReceived;

    private static bool _isInitialized;
    private static NetManager _client;
    private static NetPeer _server;
    private static bool _handshakeCompleted = false;
    private static int _netId = -1;

    // ── Encryption ────────────────────────────────────────────────────────────
    /// <summary>Ephemeral ECDH key pair generated before connecting. Disposed after session key derivation.</summary>
    private static ECDiffieHellman? _ephemeralKey;
    /// <summary>ChaCha20-Poly1305 session cipher, ready after <see cref="HandleWelcome"/>.</summary>
    private static SessionCipher? _sessionCipher;

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
            var deliveryMethod = packetType switch
            {
                PacketType.UpdatePosition => DeliveryMethod.Unreliable,
                PacketType.ProtocolHello  => DeliveryMethod.ReliableOrdered,
                _                         => DeliveryMethod.ReliableSequenced
            };

            // ProtocolHello is sent before the cipher exists - always plaintext.
            if (packetType == PacketType.ProtocolHello || _sessionCipher is null)
            {
                _server.Send(data.Serialize(packetType), deliveryMethod);
            }
            else
            {
                _server.Send(BuildEncryptedPacket(data.Serialize(packetType)), deliveryMethod);
            }

            if (packetType != PacketType.UpdatePosition)
                App.Log.LogInfo($"Packet:{packetType} out {data.Serialize(packetType).Length} bytes");
        }
        catch (Exception e)
        {
            App.Log.LogError("Failed to send message to server:");
            App.Log.LogError(e);
        }
    }

    /// <summary>Do not use this for normal messages.</summary>
    public static void SendOpusFrame(byte[] opusFrame)
    {
        if (_sessionCipher is null)
        {
            // Cipher not ready yet - drop the frame; it will be missed but that's acceptable.
            return;
        }

        // Build plaintext: [1-byte PacketType][opus bytes]
        var plain = new byte[1 + opusFrame.Length];
        plain[0] = (byte)PacketType.Voice;
        opusFrame.CopyTo(plain, 1);

        _server.Send(BuildEncryptedPacket(plain), DeliveryMethod.Unreliable);
    }

    /// <summary>
    /// Wraps <paramref name="serializedPacket"/> (which already contains the 1-byte PacketType header)
    /// in an EncryptedPayload frame using the current session cipher.
    /// </summary>
    private static byte[] BuildEncryptedPacket(byte[] serializedPacket)
    {
        var encrypted = _sessionCipher!.Encrypt(serializedPacket);
        var frame = new byte[1 + encrypted.Length];
        frame[0] = (byte)PacketType.EncryptedPayload;
        encrypted.CopyTo(frame, 1);
        return frame;
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

            // Generate ephemeral ECDH key pair before connecting.
            _ephemeralKey?.Dispose();
            _ephemeralKey = ClientKeyExchange.GenerateEphemeralKeyPair();

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
            var firstByte = reader.GetByte();
            var packetType = (PacketType)firstByte;

            byte[] raw;

            if (packetType == PacketType.EncryptedPayload)
            {
                if (_sessionCipher is null)
                {
                    App.Log.LogError("Received EncryptedPayload but session cipher is not ready - dropping.");
                    return;
                }

                var encFrame = reader.GetRemainingBytes();
                try
                {
                    var plaintext = _sessionCipher.Decrypt(encFrame);
                    if (plaintext.Length < 1) return;
                    packetType = (PacketType)plaintext[0];
                    raw = plaintext[1..];
                }
                catch (CryptographicException ex)
                {
                    App.Log.LogError("Decryption failed: " + ex.Message);
                    return;
                }
            }
            else
            {
                raw = reader.GetRemainingBytes();
            }

            if (packetType is not PacketType.ProtocolWelcome and not PacketType.ProtocolMismatch &&
                !_handshakeCompleted)
                return;

            if (packetType is not PacketType.UpdatePosition and not PacketType.Voice)
                App.Log.LogInfo($"Packet:{packetType} in {raw.Length} bytes");
            
            switch (packetType)
            {
                // ordered by most common to less common
                case PacketType.UpdatePosition:
                    HandlePositionUpdate(raw.Deserialize<UpdatePositionDto>());
                    break;
                case PacketType.Voice:
                    HandleVoice(raw);
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

    private static void HandleVoice(byte[] raw)
    {
        var dto = raw.Deserialize<VoiceDto>();
        OnVoiceReceived?.Invoke(dto);
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
        // Derive session key from the server's public key received during HTTPS auth.
        if (_ephemeralKey is not null && PlayerState.ServerPublicKey is { Length: > 0 })
        {
            try
            {
                var sessionKey = ClientKeyExchange.DeriveSessionKey(_ephemeralKey, PlayerState.ServerPublicKey);
                _sessionCipher?.Dispose();
                _sessionCipher = new SessionCipher(sessionKey);
                App.Log.LogInfo("[Crypto] Session cipher established.");
            }
            catch (Exception ex)
            {
                App.Log.LogError("[Crypto] Failed to derive session key: " + ex.Message);
            }
            finally
            {
                _ephemeralKey.Dispose();
                _ephemeralKey = null;
                PlayerState.ServerPublicKey = null;
            }
        }
        else
        {
            App.Log.LogWarning("[Crypto] No ephemeral key or server public key available - UDP traffic will be unencrypted.");
        }

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
        _sessionCipher?.Dispose();
        _sessionCipher = null;

        var hello = new ProtocolHelloCmd
        {
            ProtocolVersion = NetProtocol.CurrentVersion,
            ClientPublicKey = _ephemeralKey is not null
                ? ClientKeyExchange.ExportPublicKeyBytes(_ephemeralKey)
                : Array.Empty<byte>()
        };
        SendServerMessage(hello, PacketType.ProtocolHello);
        App.Log.LogInfo($"Connected to server {peer.Address}:{peer.Port}, waiting for handshake...");
    }
}