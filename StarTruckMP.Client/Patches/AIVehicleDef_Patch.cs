using HarmonyLib;

namespace StarTruckMP.Client.Patches;

[HarmonyPatch(typeof(AIVehicleDef), nameof(AIVehicleDef.GetPrefab))]
public class AIVehicleDef_Patch
{
    private static AIVehicleDef _def;
    public static AIVehicleDef Instance => _def;
    
    static void Postfix(AIVehicleDef __instance)
    {
        if (_def != null && __instance == _def) return;
        _def = __instance;
    }
}