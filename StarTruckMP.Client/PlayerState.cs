using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace StarTruckMP.Client;

public static class PlayerState
{
    /// <summary>
    /// This will be populated automatic if we have successfully authenticated with Xbox Live token
    /// or Steam Authentication token.
    /// </summary>
    public static string Token { get; set; } = "";

    #region Game State

    public static string Sector { get; set; } = "";
    public static GameObject Truck { get; set; }
    public static GameObject Player { get; set; }
    public static GameObject SpaceSuit { get; set; }
    public static Material[] SpaceSuitMats { get; set; }

    #endregion
}