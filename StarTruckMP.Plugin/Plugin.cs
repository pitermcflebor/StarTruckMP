using BepInEx.Unity.IL2CPP;
using BepInEx;
using Object = UnityEngine.Object;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.UnityEngine;
using StarTruckMP.Client;

namespace StarTruckMP;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class StarTruckMP : BasePlugin
{
    private const string PluginGuid = "StarTruckMP";
    private const string PluginName = "Star Trucker MP";
    private const string PluginVersion = "0.1";
    internal new static ManualLogSource Log;
    public static ConfigEntry<string> IPAddress;
    public static ConfigEntry<int> MoveUpdate;
    public static ConfigEntry<UnityEngine.KeyCode> JoinKey;
    public static ConfigEntry<UnityEngine.KeyCode> ReconnectKey;

    public override void Load()
    {
        Log = base.Log;
        HookAssemblyResolver();
        Log.LogInfo($"Plugin {PluginGuid} is loaded!");
        IPAddress = Config.Bind("Server Info", "ServerIP", "127.0.0.1:7777", "IP Address to Join");
        MoveUpdate = Config.Bind("Server Info", "MovementUpdate", 100, "Movement update frequencey in ms");
        JoinKey = Config.Bind("Keybinds", "JoinKey", UnityEngine.KeyCode.LeftBracket, "Set the Key to press for joining the listed IP");
        ReconnectKey = Config.Bind("Keybinds", "ReconnectKey", UnityEngine.KeyCode.RightBracket, "Set the Key to press for reconnecting to the listed IP");
        Harmony.CreateAndPatchAll(typeof(TruckClient));
    }

    private static void HookAssemblyResolver()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var requested = new AssemblyName(args.Name);
            if (!string.Equals(requested.Name, "System.Collections.Immutable", StringComparison.OrdinalIgnoreCase))
                return null;

            // Return already-loaded copy with matching version if available.
            var existing = FindLoadedImmutable(requested.Version);
            if (existing != null)
                return existing;

            var immutablePath = GetImmutablePath();
            if (immutablePath == null)
                return null;

            try
            {
                // Load from bytes to bypass load-context version conflicts.
                var loaded = LoadAssemblyFromBytes(immutablePath);
                Log.LogInfo($"Resolved System.Collections.Immutable {loaded.GetName().Version} from plugin directory.");
                return loaded;
            }
            catch (Exception ex)
            {
                Log.LogError("AssemblyResolve failed for System.Collections.Immutable: " + ex);
                return FindLoadedImmutable(); // Last resort: return any loaded version.
            }
        };

        // Force preload to make sure later serializer initialization binds to this copy.
        TryPreloadImmutable();
    }

    private static Assembly FindLoadedImmutable(Version requiredVersion = null)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, "System.Collections.Immutable", StringComparison.OrdinalIgnoreCase))
                continue;
            if (requiredVersion == null || asm.GetName().Version == requiredVersion)
                return asm;
        }
        return null;
    }

    private static string GetImmutablePath()
    {
        var pluginDirectory = Path.GetDirectoryName(typeof(StarTruckMP).Assembly.Location);
        if (string.IsNullOrWhiteSpace(pluginDirectory))
            return null;
        var path = Path.Combine(pluginDirectory, "System.Collections.Immutable.dll");
        return File.Exists(path) ? path : null;
    }

    private static Assembly LoadAssemblyFromBytes(string path)
    {
        // Loading from raw bytes avoids LoadFrom context conflicts and allows
        // loading a specific version even when another version is already loaded.
        return Assembly.Load(File.ReadAllBytes(path));
    }

    private static void TryPreloadImmutable()
    {
        try
        {
            var immutablePath = GetImmutablePath();
            if (immutablePath == null)
                return;

            var diskVersion = AssemblyName.GetAssemblyName(immutablePath).Version;

            if (FindLoadedImmutable(diskVersion) != null)
                return; // Exact version already loaded, nothing to do.

            LoadAssemblyFromBytes(immutablePath);
            Log.LogInfo($"Preloaded System.Collections.Immutable {diskVersion} from plugin directory.");
        }
        catch (Exception ex)
        {
            Log.LogError("Failed to preload System.Collections.Immutable.dll: " + ex);
        }
    }

    [HarmonyPatch]
    public class TruckClient
    {
        [HarmonyPatch(typeof(PauseController), nameof(Update), new Type[] { })]
        [HarmonyPostfix]
        public static void Update()
        {
            StarTruckClient.Update();
            StarTruckClient.FixedUpdate();
        }

        [HarmonyPatch(typeof(CustomizationState), nameof(CustomizationState.EquipLivery))]
        [HarmonyPostfix]
        public static void EquipLivery(string itemId)
        {
            StarTruckClient.UpdateLivery(itemId);
        }

        [HarmonyPatch(typeof(SectorPersistence), nameof(SectorPersistence.OnArrivedAtSector))]
        [HarmonyPostfix]
        public static void OnArrivedAtSector(Object sender, EventArgs eventArgs)
        {
            StarTruckClient.OnArrivedAtSector();
        }
    }
}
