using System;
using System.Linq;
using System.Threading;
using StarTruckMP.Client.Synchronization;
using StarTruckMP.Client.UI;
using StarTruckMP.Shared;
using StarTruckMP.Shared.Dto;
using UnityEngine;
using Vector3 = UnityEngine.Vector3;

namespace StarTruckMP.Client.Components;

public class GameEventsComponent : MonoBehaviour
{
    public static event Action<string> ArrivedAtSector;
    
    private void Awake()
    {
        App.Log.LogInfo("GameEventsComponent Awake");
        DontDestroyOnLoad(gameObject);
    }

    private GameObject _player;
    private GameObject _playerCam;
    private Rigidbody _playerRigid;
    private PlayerLocation _playerLocation;

    private GameObject _truck;
    private Rigidbody _truckRigid;
    
    private void OnArrivedAtSector(Il2CppSystem.Object sender, Il2CppSystem.EventArgs args)
    {
        PlayerState.Sector = GameObject.Find("[Sector]").scene.name;
        ArrivedAtSector?.Invoke(PlayerState.Sector);
        
        // This event also happens when the player load a saved game
        
        _player = GameObject.FindGameObjectWithTag("Player");
        PlayerState.Player = _player;
        _playerCam = GameObject.FindGameObjectWithTag("MainCamera");
        _playerRigid = _player?.GetComponent<Rigidbody>();
        _playerLocation = _player?.GetComponent<PlayerLocation>();
        
        _truck = GameObject.Find("StarTruck(Clone)"); // clone?
        PlayerState.Truck = _truck;
        _truckRigid = _truck?.GetComponent<Rigidbody>();
        var truckInterior = _truck?.transform.Find("Interior");
        var root = truckInterior?.Find("SpaceSuit_Root");
        var suit = root?.Find("SpaceSuit");
        if (suit && suit.childCount > 0) 
            PlayerState.SpaceSuit = suit.GetChild(0).gameObject;

        if (!_pptRunning) PlayerPositionThread();
        if (!_tptRunning) TruckPositionThread();
    }
    
    private bool _subscribedToSectorArrival = false;
    
    private void Update()
    {
        if (SectorPersistence.instance && !_subscribedToSectorArrival)
        {
            SectorPersistence.instance.onArrivedAtSector.onTriggered +=
                new System.Action<Il2CppSystem.Object, Il2CppSystem.EventArgs>(OnArrivedAtSector);
            _subscribedToSectorArrival = true;
        }
        
        // TODO: maybe this component not exists?
        if (PlayerState.SpaceSuitMats == null &&  PlayerState.SpaceSuit != null)
            PlayerState.SpaceSuitMats = PlayerState.SpaceSuit.GetComponent<MeshRenderer>()?.materials.ToArray();
        
        // Real overlay hotkeys: F2 toggles UI mode and Esc closes it.
        if (Input.GetKeyDown(KeyCode.F2))
        {
            OverlayManager.ToggleInteractiveMode();
            App.Log.LogInfo($"[Overlay] F2 => toggle interactive mode => {(OverlayManager.IsInteractiveMode ? "ON" : "OFF")}");
        }

        if (OverlayManager.IsInteractiveMode && Input.GetKeyDown(KeyCode.Escape))
        {
            App.Log.LogInfo("[Overlay] Esc => interactive mode OFF (click-through ON)");
            OverlayManager.SetInteractiveMode(false);
        }

        if (Input.GetKeyDown(KeyCode.F8))
        {
            App.Log.LogInfo("[Overlay] running diagnostics page");
            OverlayManager.RunDiagnostics();
        }
    }
    
    private CancellationTokenSource _cts = new();
    private const float ThresholdChange = 0.1f;
    
    #region Player Location updates
    
    private bool _pptRunning = false;

    /// <summary>
    /// Unity isn't thread-safe, so we will update the position outside the main thread
    /// to avoid stuck the Update thread
    /// </summary>
    /// <returns></returns>
    private void PlayerPositionThread()
    {
        if (_pptRunning) return;
        
        var ct = _cts.Token;
        Plugin.StartAttachedThread(() =>
        {
            _pptRunning = true;
            
            App.Log.LogInfo("PlayerPositionThread started");
            
            var lastPosition = Vector3.zero;
            while (!ct.IsCancellationRequested)
            {
                if (Network.NetId == -1)
                {
                    ct.WaitHandle.WaitOne(15);
                    continue;
                }
                
                var newPlayerPosition = _player.transform.position;
                if (Vector3.Distance(lastPosition, newPlayerPosition) > ThresholdChange)
                {
                    Network.SendServerMessage(new UpdatePositionDto
                    {
                        NetId = Network.NetId,
                        Position = ConvertToSharedVector3(newPlayerPosition),
                        Rotation = ConvertToSharedVector3(_player.transform.rotation.eulerAngles),
                        Velocity = ConvertToSharedVector3(_playerRigid.velocity),
                        AngVel = ConvertToSharedVector3(_playerRigid.angularVelocity),
                        IsTruck = false,
                        InSeat = false
                    }, PacketType.UpdatePosition);
                }

                ct.WaitHandle.WaitOne(15);
            }
            
            _pptRunning = false;
        });
    }
    
    #endregion

    #region Truck Location updates
    
    private bool _tptRunning = false;
    
    private void TruckPositionThread()
    {
        if (_tptRunning) return;
        
        var ct = _cts.Token;
        Plugin.StartAttachedThread(() =>
        {
            _tptRunning = true;
            
            App.Log.LogInfo("TruckPositionThread started");
            
            var lastPosition = Vector3.zero;
            while (!ct.IsCancellationRequested)
            {
                if (Network.NetId == -1)
                {
                    ct.WaitHandle.WaitOne(15);
                    continue;
                }
                
                var newTruckPosition = _truck.transform.position;
                if (Vector3.Distance(lastPosition, newTruckPosition) > ThresholdChange)
                {
                    Network.SendServerMessage(new UpdatePositionDto
                    {
                        NetId = Network.NetId,
                        Position = ConvertToSharedVector3(newTruckPosition),
                        Rotation = ConvertToSharedVector3(_truck.transform.rotation.eulerAngles),
                        Velocity = ConvertToSharedVector3(_truckRigid.velocity),
                        AngVel = ConvertToSharedVector3(_truckRigid.angularVelocity),
                        IsTruck = true,
                        InSeat = false
                    }, PacketType.UpdatePosition);
                }

                ct.WaitHandle.WaitOne(15);
            }
            
            _tptRunning = false;
        });
    }

    #endregion
    
    private StarTruckMP.Shared.Vector3 ConvertToSharedVector3(Vector3 unityVector)
    {
        return new StarTruckMP.Shared.Vector3
        {
            X = unityVector.x,
            Y = unityVector.y,
            Z = unityVector.z
        };
    }
    
    private void OnEnable()
    {
        App.Log.LogInfo("ConnectionComponent OnEnable");
    }

    #region Recycle
    
    private void OnDestroy()
    {
        App.Log.LogInfo("ConnectionComponent destroyed");
        SectorPersistence.instance.onArrivedAtSector.onTriggered -=
            new System.Action<Il2CppSystem.Object, Il2CppSystem.EventArgs>(OnArrivedAtSector);
    }

    private void OnDisable()
    {
        SectorPersistence.instance.onArrivedAtSector.onTriggered -=
            new System.Action<Il2CppSystem.Object, Il2CppSystem.EventArgs>(OnArrivedAtSector);
    }
    
    #endregion
}