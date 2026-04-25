using UnityEngine;

namespace StarTruckMP.Client.Synchronization;

public static class Utils
{
    public static TruckInfo ExtractTruckInfo(GameObject truckObj)
    {
        var livery = truckObj.GetComponent<LiveryAndDamageApplierTruckExterior>();
        
        return new TruckInfo
        {
            LiveryId = livery.AppliedLiveryId ?? livery.CurrentLiveryId
        };
    }
}

/// <summary>
/// This class contains all the information of a Truck.
/// Used for sharing with other players.
/// </summary>
public class TruckInfo
{
    // Misc
    public string LiveryId { get; set; }
}