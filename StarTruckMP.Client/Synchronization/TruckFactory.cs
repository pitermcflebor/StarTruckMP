using Il2CppSystem.Collections.Generic;
using StarTruckMP.Client.Components;
using StarTruckMP.Client.Patches;
using UnityEngine;

namespace StarTruckMP.Client.Synchronization;

public static class TruckFactory
{
    // This types will be removed from the clone
    private static readonly string[] AIComponentTypes =
    [
        "AIVehicle_Truck",
        "AIVehicleEngine",
        //"AIVehicleCustomiser",
        "WarpGateTraverserAIVehicle",
        "AITruckHorn",
        "AIVehicleThrusters",
        "NavObstacle",
        "RegisterPointOfInterest",
        "CollisionFineReporter",
        "DevCameraTarget",
        "NPCVehicleAudio",
        "FloatingOriginTrailMover",
        "InstantiatedAddressableAutoRelease",
        "NPCTruckMaglockVFX"
    ];

    private static readonly string[] GameObjectNames =
    [
        "MaglockHitchVFX"
    ];

    public static GameObject CreatePlayerTruck(int nContainers, Vector3 spawnPos, Quaternion spawnRot)
    {
        var vehicleDef = AIVehicleDef_Patch.Instance;
        if (vehicleDef == null)
        {
            App.Log.LogError("Failed to find AIVehicleDef in scene");
            return null;
        }

        var prefab = vehicleDef.GetPrefab(nContainers);
        if (prefab == null)
        {
            App.Log.LogError($"Cannot get prefab of {nContainers} containers");
            return null;
        }

        var truckGo = Object.Instantiate(prefab, spawnPos, spawnRot);
        var truck = truckGo.GetComponent<AIVehicle_Truck>();
        if (truck == null)
        {
            App.Log.LogError("Failed to get AIVehicle_Truck component from prefab");
            return null;
        }
        truck.name = "PlayerTruck_Remote";

        truckGo.AddComponent<TruckControllerComponent>();

        return truckGo;
    }
    
    public static GameObject CreatePlayerTruckFromNpc(Vector3 spawnPos, Quaternion spawnRot)
    {
        var npcPrefab = AIVehicleTruck_Patch.GetPrefab();
        if (npcPrefab == null)
            return null;
        
        // Clone a npc truck
        var truck = Object.Instantiate(npcPrefab, spawnPos, spawnRot);
        truck.SetActive(false); // disable before Awake/Start runs
        truck.name = "GhostTruck_Remote";
        
        // disable collisions (maybe it can be enabled other time)
        var rigidbody = truck.GetComponent<Rigidbody>();
        if (rigidbody != null)
            rigidbody.detectCollisions = false;

        // Delete all AI things 
        StripAiComponents(truck);
        
        // Delete objects by name
        StripByName(truck);
        
        // Clear existing NPC cargo but keep the slot GameObjects intact
        // (slots must remain alive so their serialized fields are preserved for SpawnContainer)
        ReinitContainers(truck);

        // Add custom movement controller
        truck.AddComponent<TruckControllerComponent>();

        // Disable collisions with truck
        SetTruckLayer(truck);

        var npcTruckVisual = truck.transform.Find("NPCTruck");
        if (npcTruckVisual != null)
            npcTruckVisual.gameObject.SetActive(false);

        truck.SetActive(true); // enable without AI thingy
        return truck;
    }

    private static void StripByName(GameObject truck)
    {
        var allObjects = truck.GetComponentsInChildren<Transform>(includeInactive: true);

        foreach (var t in allObjects)
        {
            foreach (var name in GameObjectNames)
            {
                if (t.gameObject.name == name)
                    Object.Destroy(t.gameObject);
            }
        }
    }

    private static void StripAiComponents(GameObject root)
    {
        var allObjects = root.GetComponentsInChildren<Transform>(includeInactive: true);

        foreach (var t in allObjects)
        {
            foreach (var typeName in AIComponentTypes)
            {
                var comp = t.GetComponent(typeName);
                if (comp != null)
                    Object.Destroy(comp);
            }
        }
    }
    
    /// <summary>
    /// Calls Reinit() on every AIVehicleContainerSlot found in the truck hierarchy
    /// to clear any pre-existing NPC cargo while keeping the slot GameObjects (and their
    /// serialized fields) alive for later use by SpawnContainer.
    /// </summary>
    private static void ReinitContainers(GameObject root)
    {
        foreach (var slot in root.GetComponentsInChildren<AIVehicleContainerSlot>(true))
            slot.Reinit();
    }
    
    private static void SetTruckLayer(GameObject root)
    {
        // TODO: this cannot be done, we can figure out another way or just remove this
        /*int truckLayer = LayerMask.NameToLayer("RemoteTruck");
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            t.gameObject.layer = truckLayer;*/
    }
}