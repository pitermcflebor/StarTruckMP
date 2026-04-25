using System;
using Microsoft.Extensions.Caching.Memory;
using StarTruckMP.Client.Synchronization;
using StarTruckMP.Shared;
using StarTruckMP.Shared.Cmd;
using StarTruckMP.Shared.Dto;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = Il2CppSystem.Object;
using SynchronizationContext = Il2CppSystem.Threading.SynchronizationContext;
using Vector3 = UnityEngine.Vector3;

namespace StarTruckMP.Client.Components;

public class NetworkEventsComponent : MonoBehaviour
{
    private bool _connected;
    private SynchronizationContext _mainThreadContext;

    private void Awake()
    {
        _mainThreadContext = SynchronizationContext.Current;
        DontDestroyOnLoad(gameObject);
        
        Network.OnConnected += HandleConnected;
        Network.OnDisconnected += HandleDisconnected;
        Network.OnPlayerDisconnected += HandlePlayerDisconnected;
        Network.OnPlayerSectorUpdate += HandlePlayerSectorUpdate;
        Network.OnPlayerPositionUpdate += HandlePlayerPositionUpdate;
        Network.OnTruckLiveryUpdate += HandleTruckLiveryUpdate;
        
        App.Log.LogInfo("NetworkEventsComponent Awake and subscribed to network events");
    }
    
    private IMemoryCache _players = new MemoryCache(new MemoryCacheOptions());

    private class NetPlayer
    {
        public int PlayerId { get; set; }
        public string PlayerName { get; set; }
        public GameObject TruckObj { get; set; }
        public GameObject PlayerObj { get; set; }
        public GameObject TruckExteriorObj { get; set; }
        public GameObject SuitObj { get; set; }
    }

    private NetPlayer CreateNetPlayer(int netId)
    {
        var player = new NetPlayer
        {
            PlayerId = netId,
            PlayerName = $"Player #{netId}"
        };

        var go = GameObject.Find("[Sector]");
        var scene = go?.scene;
        if (scene == null) App.Log.LogError($"({netId}) Could not find sector root object to get scene for player");

        #region Truck setup

        player.TruckObj = new GameObject($"ClientTruck-{netId}");
        App.Log.LogInfo($"({netId}) Created truck");
        SceneManager.MoveGameObjectToScene(player.TruckObj, scene!.Value);
        App.Log.LogInfo($"({netId}) Moved truck to scene");
        player.TruckObj.transform.SetParent(null);
        var rigid = player.TruckObj.AddComponent<Rigidbody>();
        var rigidProb = PlayerState.Truck.GetComponent<Rigidbody>();
        App.Log.LogInfo($"({netId}) Added Rigidbody to truck");
        rigid.useGravity = rigidProb.useGravity;
        rigid.drag = rigidProb.drag;
        rigid.angularDrag = rigidProb.angularDrag;
        rigid.mass = rigidProb.mass;
        rigid.centerOfMass = rigidProb.centerOfMass;
        rigid.detectCollisions = false;
        rigid.isKinematic = rigidProb.isKinematic;
        rigid.maxAngularVelocity = rigidProb.maxAngularVelocity;
        rigid.maxDepenetrationVelocity = rigidProb.maxDepenetrationVelocity;
        rigid.inertiaTensor = rigidProb.inertiaTensor;
        rigid.inertiaTensorRotation = rigidProb.inertiaTensorRotation;
        App.Log.LogInfo($"({netId}) Configured Rigidbody of truck");
        
        #endregion

        #region Player setup

        player.PlayerObj = new GameObject($"RemotePlayer-{netId}");
        App.Log.LogInfo($"({netId}) Created player object");
        SceneManager.MoveGameObjectToScene(player.PlayerObj, scene!.Value);
        App.Log.LogInfo($"({netId}) Moved player object to scene");
        player.PlayerObj.transform.SetParent(null);

        #endregion
        
        #region Truck Exterior setup
        
        var exteriorProb = GameObject.Find("Exterior");
        App.Log.LogInfo($"({netId}) Found exterior prefab {exteriorProb.GetIl2CppType().FullName}");
        
        var exterior = GameObject.Instantiate(exteriorProb,
            Vector3.zero,
            Quaternion.EulerAngles(Vector3.zero),
            player.TruckObj.transform);
        exterior.name = $"ClientExterior-{netId}";
        App.Log.LogInfo($"({netId}) Instantiated exterior");
        player.TruckExteriorObj = exterior;
        exterior.transform.Find("StarTruck_Hatch").Find("Marker").gameObject.SetActive(false);
        Destroy(exterior.transform.Find("StarTruck_Hatch").GetChild(0).GetComponent<DoorAnimator>());
        Destroy(exterior.transform.Find("StarTruck_Hatch").GetChild(0).GetComponent<GameEventListener>());
        Destroy(exterior.transform.Find("StarTruck_Hatch").GetChild(0).GetComponent<EPOOutline.TargetStateListener>());
        exterior.transform.Find("MonitorCameras").gameObject.SetActive(false);
        exterior.transform.Find("PlayerSpawnMarker").gameObject.SetActive(false);
        exterior.transform.Find("ThrusterCameraShakeController").gameObject.SetActive(false);
        var customization = exterior.transform.GetComponent<CustomizationApplier>();
        if (customization != null)
        {
            var livDamApp = exterior.transform.GetComponent<LiveryAndDamageApplierTruckExterior>();
            customization.m_linkedLiveryApplier = livDamApp;
        }
        // Why this would happen?
        else App.Log.LogError($"({netId}) Could not find CustomizationApplier on exterior");
        
        // Disable collision
        foreach (var collider in exterior.GetComponentsInChildren<Collider>())
            collider.enabled = false;
        
        App.Log.LogInfo($"({netId}) Configured exterior");
        
        #endregion
        
        #region Player suit setup

        var suit = Instantiate(PlayerState.SpaceSuit, Vector3.zero, Quaternion.identity, player.PlayerObj.transform);
        App.Log.LogInfo($"({netId}) Instantiated player suit");
        player.SuitObj = suit;
        if (PlayerState.SpaceSuitMats != null)
            PlayerState.SpaceSuitMats.CopyTo(suit.GetComponent<MeshRenderer>().materials, 0);
        suit.active = true;
        suit.name = $"ClientSuit-{netId}";
        Destroy(suit.transform.GetComponent<SpaceSuitController>());
        Destroy(suit.transform.GetComponent<CapsuleCollider>());
        Destroy(suit.transform.GetComponent<OutlinableSetterUpper>());
        Destroy(suit.transform.GetComponent<EPOOutline.Outlinable>());
        Destroy(suit.transform.GetComponent<EPOOutline.TargetStateListener>());
        Destroy(suit.transform.GetComponent<MaterialSwitcher>());
        Destroy(suit.transform.GetComponent<InteractTarget>());
        Destroy(suit.transform.GetComponent<DoorController>());
        App.Log.LogInfo($"({netId}) Configured player suit");
        
        #endregion
        
        App.Log.LogInfo($"Created NetPlayer for netId {netId}");
        
        return player;
    }

    private void HandleTruckLiveryUpdate(UpdateLiveryDto liveryDto)
    {
        _mainThreadContext.Post(new Action<Object>(_ =>
        {
            if (!_players.TryGetValue(liveryDto.NetId, out NetPlayer player)) return;

            var applier = player.TruckExteriorObj.GetComponent<LiveryAndDamageApplierTruckExterior>();
            applier?.LoadAndApplyLiveryById(liveryDto.Livery);
        }), null);
    }

    private void HandlePlayerPositionUpdate(UpdatePositionDto positionDto)
    {
        _mainThreadContext.Post(new Action<Object>(_ =>
        {
            if (!_players.TryGetValue(positionDto.NetId, out NetPlayer player)) return;

            if (positionDto.IsTruck && player.TruckObj != null)
            {
                player.TruckObj.transform.position = Vec(positionDto.Position);
                player.TruckObj.transform.rotation = Quaternion.Euler(Vec(positionDto.Rotation));
                var rigid = player.TruckObj.transform.GetComponent<Rigidbody>();
                rigid?.velocity = Vec(positionDto.Velocity);
                rigid?.angularVelocity = Vec(positionDto.AngVel);
            }
            else if (!positionDto.IsTruck && player.PlayerObj != null)
            {
                player.PlayerObj.transform.position = Vec(positionDto.Position);
                player.PlayerObj.transform.rotation = Quaternion.Euler(Vec(positionDto.Rotation));
                var rigid = player.PlayerObj.transform.GetComponent<Rigidbody>();
                // why this can be null?
                rigid?.velocity = Vec(positionDto.Velocity);
                rigid?.angularVelocity = Vec(positionDto.AngVel);
            }
        }), null);
    }

    private Vector3 Vec(global::StarTruckMP.Shared.Vector3 vec) => new(vec.X, vec.Y, vec.Z);

    private void HandlePlayerSectorUpdate(UpdateSectorDto sectorDto)
    {
        _mainThreadContext.Post(new Action<Object>(_ =>
        {
            if (sectorDto.Sector != PlayerState.Sector)
            {
                _players.Remove(sectorDto.NetId);
                App.Log.LogInfo($"Removed player {sectorDto.NetId} from cache due to sector change ({sectorDto.Sector} != {PlayerState.Sector})");
                return;
            }

            lock (sectorDto.NetId.ToString())
            {
                var netPlayer = CreateNetPlayer(sectorDto.NetId);
                _players.Set(sectorDto.NetId, netPlayer);
                App.Log.LogInfo($"Added player {sectorDto.NetId} to cache for sector {sectorDto.Sector}");
            }
        }), null);
    }

    private void HandlePlayerDisconnected(int netId)
    {
        _mainThreadContext.Post(new Action<Object>(_ =>
        {
            lock (netId.ToString())
            {
                if (!_players.TryGetValue(netId, out NetPlayer player)) return;
                player.TruckObj.SetActive(false);
                player.PlayerObj.SetActive(false);
                _players.Remove(netId);
            }
        }), null);
    }

    private void HandleDisconnected()
    {
        _connected = false;
    }

    private void HandleConnected(int netId)
    {
        _connected = true;
        Network.SendServerMessage(new UpdateSectorCmd { Sector = PlayerState.Sector }, PacketType.UpdateSector);
    }

    private void Unsubscribe()
    {
        Network.OnConnected -= HandleConnected;
        Network.OnDisconnected -= HandleDisconnected;
        Network.OnPlayerDisconnected -= HandlePlayerDisconnected;
        Network.OnPlayerSectorUpdate -= HandlePlayerSectorUpdate;
        Network.OnPlayerPositionUpdate -= HandlePlayerPositionUpdate;
        Network.OnTruckLiveryUpdate -= HandleTruckLiveryUpdate;
    }
    
    private void OnDisable()
    {
        Unsubscribe();
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }
}