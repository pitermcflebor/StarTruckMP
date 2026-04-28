using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
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
        OverlayManager.MessageReceived += (type, data) =>
        {
            switch (type)
            {
                case "inspectObjectExtra":
                    App.Log.LogInfo($"[Object Inspector] request extra data for object {data}");
                    GetMoreData(data);
                    break;
            }
        };
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

        if (Input.GetKeyDown(KeyCode.F3))
        {
            // Raycast
            if (Camera.main)
            {
                var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
                if (Physics.Raycast(ray, out var hit))
                {
                    App.Log.LogInfo("[Object Inspector] object hit!");
                    // we hit something
                    var objectData = new ObjectData();
                    
                    var go = hit.transform.gameObject;
                    objectData.Name = go.name;
                    objectData.Type = $"{go.GetType().FullName}";
                    objectData.Go = go;
                    AddChildComponents(objectData, go);
                    
                    OverlayManager.PostMessage("inspectObject", objectData);

                    _inspectedObject = objectData;

                    void AddChildComponents(ObjectData data, GameObject obj, int depth = 0)
                    {
                        if (depth > 3) return;

                        // components of this obj
                        foreach (var component in obj.GetComponents<Component>())
                        {
                            var compData = new ObjectData()
                            {
                                Name = component.name,
                                Type = component.GetIl2CppType().FullName,
                                Go = component.gameObject
                            };
                            data.Children.Add(compData);
                        }

                        // childs of this obj
                        for (int i = 0; i < obj.transform.childCount; i++)
                        {
                            var child = obj.transform.GetChild(i).gameObject;
                            var childData = new ObjectData()
                            {
                                Name = child.name,
                                Type = child.GetIl2CppType().FullName,
                                Go = child
                            };
                            AddChildComponents(childData, child, depth + 1);
                            data.Children.Add(childData);
                        }
                    }
                }
            }
        }

        if (Input.GetKeyDown(KeyCode.F8))
        {
            App.Log.LogInfo("[Overlay] running diagnostics page");
            OverlayManager.RunDiagnostics();
        }
    }

    private class ObjectData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "n/a";
        public string Type { get; set; } = "unk";
        public List<ObjectData> Children { get; set; } = [];
        
        [NotMapped]
        [JsonIgnore]
        internal GameObject Go { get; set; } 
    }

    private class ObjectExtraData
    {
        public string K { get; set; }
        public string V { get; set; }
    }

    private ObjectData _inspectedObject;

    private void GetMoreData(string id)
    {
        App.Log.LogInfo($"[Object Inspector] GetMoreData for object with id {id}");
        if (_inspectedObject == null) return;

        // search in all tree an object with that id
        var selected = FindInside(_inspectedObject, id);
        if (selected == null)
        {
            App.Log.LogInfo($"[Object Inspector] object with id {id} not found in the inspected tree");
            return;
        }
        if (selected.Go == null)
        {
            App.Log.LogInfo($"[Object Inspector] object with id {id} has no GameObject reference");
            return;
        }

        var data = new List<ObjectExtraData>();
        
        switch (selected.Type)
        {
            case "Transform":
            {
                data.Add(new ObjectExtraData { K = "position", V = selected.Go.transform.localPosition.ToString() });
                data.Add(new ObjectExtraData { K = "rotation", V = selected.Go.transform.localEulerAngles.ToString() });
                data.Add(new ObjectExtraData { K = "scale", V = selected.Go.transform.localScale.ToString() });
                break;
            }
            case "Rigidbody":
            {
                var rigidbody = selected.Go.GetComponent<Rigidbody>();
                if (rigidbody == null) break;
                data.Add(new ObjectExtraData { K = "mass", V = rigidbody.mass.ToString(CultureInfo.InvariantCulture) });
                data.Add(new ObjectExtraData { K = "drag", V = rigidbody.drag.ToString(CultureInfo.InvariantCulture) });
                data.Add(new ObjectExtraData { K = "angularDrag", V = rigidbody.angularDrag.ToString(CultureInfo.InvariantCulture) });
                data.Add(new ObjectExtraData { K = "useGravity", V = rigidbody.useGravity.ToString() });
                data.Add(new ObjectExtraData { K = "isKinematic", V = rigidbody.isKinematic.ToString() });
                break;
            }
            case "Collider":
            {
                var collider = selected.Go.GetComponent<Collider>();
                if (collider == null) break;
                data.Add(new ObjectExtraData { K = "enabled", V = collider.enabled.ToString() });
                data.Add(new ObjectExtraData { K = "isTrigger", V = collider.isTrigger.ToString() });
                data.Add(new ObjectExtraData { K = "material", V = collider.material?.name ?? "null" });
                break;
            }
            case "MeshRenderer":
            {
                var mr = selected.Go.GetComponent<MeshRenderer>();
                if (mr == null) break;
                data.Add(new ObjectExtraData { K = "enabled", V = mr.enabled.ToString() });
                data.Add(new ObjectExtraData { K = "castShadows", V = mr.shadowCastingMode.ToString()});
                data.Add(new ObjectExtraData { K = "material", V = mr.material?.name ?? "null"});
                break;
            }
        }
        
        OverlayManager.PostMessage("inspectObjectExtra", data);
        App.Log.LogInfo($"[Object Inspector] sent extra data for object with id {id}");
        App.Log.LogInfo(data);
        
        return;

        ObjectData FindInside(ObjectData oData, string innerId)
        {
            if (oData.Id == innerId) return oData;
            
            foreach (var objectData in oData.Children)
            {
                if (objectData.Id == innerId)
                    return objectData;
                if (FindInside(objectData, innerId) is {} found)
                    return found;
            }

            return null;
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