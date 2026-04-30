using System;
using Microsoft.Extensions.Caching.Memory;
using StarTruckMP.Client.Synchronization;
using StarTruckMP.Shared;
using StarTruckMP.Shared.Cmd;
using StarTruckMP.Shared.Dto;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;
using Object = Il2CppSystem.Object;
using Quaternion = UnityEngine.Quaternion;
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
        Network.OnTrailerUpdate += HandleTrailerUpdate;
        
        App.Log.LogInfo("NetworkEventsComponent Awake and subscribed to network events");
    }

    private IMemoryCache _players = new MemoryCache(new MemoryCacheOptions());

    private class NetPlayer
    {
        public int PlayerId { get; set; }
        public string PlayerName { get; set; }
        public GameObject TruckObj { get; set; }
        public GameObject PlayerObj { get; set; }
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

        player.TruckObj = TruckFactory.CreatePlayerTruck(0, Vector3.zero, Quaternion.identity);
        if (player.TruckObj == null) App.Log.LogError($"({netId}) Failed to create player truck object");
        App.Log.LogInfo($"({netId}) Created player truck object");

        var rigidbody = player.TruckObj.GetComponent<Rigidbody>();
        rigidbody.detectCollisions = false;
        var colliders = player.TruckObj.GetComponentsInChildren<Collider>();
        foreach (var collider in colliders) DestroyImmediate(collider);

        #endregion

        #region Player setup

        player.PlayerObj = new GameObject($"RemotePlayer-{netId}");
        App.Log.LogInfo($"({netId}) Created player object");
        SceneManager.MoveGameObjectToScene(player.PlayerObj, scene!.Value);
        App.Log.LogInfo($"({netId}) Moved player object to scene");
        player.PlayerObj.transform.SetParent(null);

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

    private void RecreateNetPlayerTruck(int netId, int cargoCount)
    {
        if (!_players.TryGetValue(netId, out NetPlayer player))
            return;
        
        // copy actual data
        player.TruckObj.transform.GetPositionAndRotation(out var currentPos, out var currentRot);
        var customizer = player.TruckObj.GetComponent<AIVehicleCustomiser>();
        var liveryId = customizer.m_cabLiveryApplier.CurrentLiveryId ?? customizer.m_cabLiveryApplier.AppliedLiveryId;
        // delete
        player.TruckObj.SetActive(false);
        DestroyImmediate(player.TruckObj);
        // recreate with same data
        player.TruckObj = TruckFactory.CreatePlayerTruck(cargoCount, currentPos, currentRot);
        customizer = player.TruckObj.GetComponent<AIVehicleCustomiser>();
        customizer.AssignCabLivery(liveryId, 0f);
        var rigidbody = player.TruckObj.GetComponent<Rigidbody>();
        rigidbody.detectCollisions = false;
        var colliders = player.TruckObj.GetComponentsInChildren<Collider>();
        foreach (var collider in colliders) DestroyImmediate(collider);
        App.Log.LogInfo($"Recreated truck for player {netId} with cargo count {cargoCount}");
    }

    private void HandleTruckLiveryUpdate(UpdateLiveryDto liveryDto)
    {
        _mainThreadContext.Post(new Action<Object>(_ =>
        {
            if (!_players.TryGetValue(liveryDto.NetId, out NetPlayer player)) return;
            
            var customiser = player.TruckObj.GetComponentInChildren<AIVehicleCustomiser>(true);
            customiser.AssignCabLivery(liveryDto.Livery, 0f);
        }), null);
    }

    private void HandlePlayerPositionUpdate(UpdatePositionDto positionDto)
    {
        _mainThreadContext.Post(new Action<Object>(_ =>
        {
            if (!_players.TryGetValue(positionDto.NetId, out NetPlayer player)) return;

            if (positionDto.IsTruck && player.TruckObj != null)
            {
                var controller = player.TruckObj.GetComponent<TruckControllerComponent>();
                if (controller != null)
                    controller.ApplyNetworkState(
                        Vec(positionDto.Position),
                        Quat(positionDto.Rotation),
                        Vec(positionDto.Velocity)
                        );
                else App.Log.LogError("TruckControllerComponent is NULL");
            }
            else if (!positionDto.IsTruck && player.PlayerObj != null)
            {
                player.PlayerObj.transform.SetPositionAndRotation(
                    Vec(positionDto.Position),
                    Quat(positionDto.Rotation)
                );
                var rigid = player.PlayerObj.transform.GetComponent<Rigidbody>();
                if (rigid != null)
                {
                    rigid.velocity = Vec(positionDto.Velocity);
                    rigid.angularVelocity = Vec(positionDto.AngVel);
                }
            }
        }), null);
    }

    private static Vector3 Vec(global::StarTruckMP.Shared.Vector3 vec) => new(vec.X, vec.Y, vec.Z);
    
    private static Quaternion Quat(global::StarTruckMP.Shared.Quaternion quat) => new(quat.X, quat.Y, quat.Z, quat.W);
    
    private void HandleTrailerUpdate(UpdateTrailerDto trailerDto)
    {
        _mainThreadContext.Post(new Action<Object>(_ =>
        {
            if (!_players.TryGetValue(trailerDto.NetId, out NetPlayer player)) return;

            App.Log.LogInfo($"Trailer update info for player {trailerDto.NetId}: TrailerCount=[{trailerDto.TrailerCount}], LiveryId='{trailerDto.LiveryId}', CargoTypeId='{trailerDto.CargoTypeId}'");
            
            RecreateNetPlayerTruck(trailerDto.NetId, trailerDto.TrailerCount);

            if (trailerDto.TrailerCount == 0) return; // nothing to do
            
            var existingSlots = player.TruckObj.GetComponentsInChildren<AIVehicleContainerSlot>(true);
            
            foreach (var slot in existingSlots)
            {
                if (slot == null)
                {
                    App.Log.LogInfo($"Tried to spawn container for {trailerDto.NetId} but no AIVehicleContainerSlot found in truck hierarchy");
                    return;
                }
                if (CargoMetadataProvider.instance == null)
                {
                    App.Log.LogError($"CargoMetadataProvider is null, cannot spawn container for {trailerDto.NetId}");
                    return;
                }
                if (CargoMetadataProvider.instance.cargoCatalogue == null)
                {
                    App.Log.LogError($"CargoCatalogue is null, cannot spawn container for {trailerDto.NetId}");
                    return;
                }

                CargoType cargoType = null;
                if (!string.IsNullOrEmpty(trailerDto.CargoTypeId))
                {
                    if (!CargoMetadataProvider.instance.cargoCatalogue.lookUp.TryGetValue(trailerDto.CargoTypeId, out cargoType))
                        App.Log.LogWarning($"GetById returned null for CargoTypeId '{trailerDto.CargoTypeId}', falling back to index 0");
                }
                cargoType ??= CargoMetadataProvider.instance.cargoCatalogue.GetByIndex(0);
                if (cargoType == null)
                {
                    App.Log.LogError($"Cargo type at index 0 is null, cannot spawn container for {trailerDto.NetId}");
                    return;
                }
                if (cargoType.container == null)
                {
                    App.Log.LogError($"cargoType.container is null for {trailerDto.NetId}, cannot spawn container");
                    return;
                }

                var liveryAssetRef = CustomizationManager.instance.GetLiveryAssetRefFromId(trailerDto.LiveryId);
                if (liveryAssetRef == null)
                    App.Log.LogWarning($"GetLiveryAssetRefFromId returned null for livery ID '{trailerDto.LiveryId}', SpawnContainer may fail");

                App.Log.LogInfo($"Spawning container for {trailerDto.NetId}: cargoType={cargoType}, container={cargoType.container}, liveryAssetRef={liveryAssetRef}");
                
                slot.SpawnContainer(new AIVehicleCustomizationData.CargoContainerData
                {
                    m_container = cargoType.container,
                    m_cargoType = cargoType,
                    m_containerLivery = liveryAssetRef,
                    m_damagePercent = 0f
                });
            }
        }), null);
    }

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
        
        // Send livery
        if (PlayerState.Truck != null)
        {
            var truckInfo = Utils.ExtractTruckInfo(PlayerState.Truck);
            if (!string.IsNullOrEmpty(truckInfo.LiveryId))
            {
                Network.SendServerMessage(new UpdateLiveryCmd { Livery = truckInfo.LiveryId }, PacketType.UpdateLivery);
                App.Log.LogInfo($"Sent livery to server: {truckInfo.LiveryId}");
            }
            else
            {
                App.Log.LogWarning("Could not extract livery from player truck, skipping livery update.");
            }
        }
    }

    private void Unsubscribe()
    {
        Network.OnConnected -= HandleConnected;
        Network.OnDisconnected -= HandleDisconnected;
        Network.OnPlayerDisconnected -= HandlePlayerDisconnected;
        Network.OnPlayerSectorUpdate -= HandlePlayerSectorUpdate;
        Network.OnPlayerPositionUpdate -= HandlePlayerPositionUpdate;
        Network.OnTruckLiveryUpdate -= HandleTruckLiveryUpdate;
        Network.OnTrailerUpdate -= HandleTrailerUpdate;
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