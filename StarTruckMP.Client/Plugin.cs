using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using StarTruckMP.Client.Authentication;
using StarTruckMP.Client.Components;
using StarTruckMP.Client.Patches;
using StarTruckMP.Client.Synchronization;
using StarTruckMP.Client.UI;
using StarTruckMP.Shared.Cmd.Api;
using StarTruckMP.Shared.Dto.Api;

namespace StarTruckMP.Client;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal new static ManualLogSource Log;

    /// <summary>Global access to the Xbox auth manager. Available after Load().</summary>
    private static XboxAuthManager XboxAuth => GdkTickerComponent.AuthManager;

    private static readonly HttpClient Http = new();

    public override void Load()
    {
        Log = base.Log;
        
        // For some reason the assembly System.Collections.Immutable is not being autoloaded by the runtime,
        // so we need to force this behavior.
        HookAssemblyResolver();
        
        App.Log = Log;
        App.Configure(Config);

        SetupXboxAuth();
        SetupSteamAuth();
        SetupComponents();
        SetupUI();

        Network.SetupConnection();
        
        Harmony.CreateAndPatchAll(typeof(ClientHooks));
        Harmony.CreateAndPatchAll(typeof(AIVehicleTruck_Patch));
        Harmony.CreateAndPatchAll(typeof(AIVehicleDef_Patch));
        
        Log.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} loaded.");
        
        #if DEBUG
        OverlayManager.MessageReceived += (type, payload) =>
        {
            if (type == "overlayLoaded")
                App.Log.LogInfo("[Overlay] Overlay has loaded and is ready to receive messages.");
        };
        #endif
    }

    private void SetupUI()
    {
        OverlayManager.Launch();
        // Navigation is deferred until a session token is obtained from auth.
    }

    private void SetupComponents()
    {
        ClassInjector.RegisterTypeInIl2Cpp<TruckControllerComponent>();
        ClassInjector.RegisterTypeInIl2Cpp<GameEventsComponent>();
        ClassInjector.RegisterTypeInIl2Cpp<NetworkEventsComponent>();
        ClassInjector.RegisterTypeInIl2Cpp<RunCodeComponent>();
        ClassInjector.RegisterTypeInIl2Cpp<CbRadioPttComponent>();
        ClassInjector.RegisterTypeInIl2Cpp<CbRadioSpeakerComponent>();
        AddComponent<GameEventsComponent>();
        AddComponent<NetworkEventsComponent>();
        AddComponent<RunCodeComponent>();
    }

    #region Steam auth

    private void SetupSteamAuth()
    {
        if (FindAssembly(null, "com.rlabrecque.steamworks.net") == null)
        {
            Log.LogInfo("Steamworks.NET not found, skipping Steam authentication setup.");
            return;
        }

        // Delegate to the isolated helper so the CLR never JIT-compiles Steamworks
        // types on platforms where the assembly is absent (e.g. Xbox Game Pass).
        StartAttachedThread(SteamAuthHelper.Run);
    }

    #endregion
    
    #region Xbox auth

    private void SetupXboxAuth()
    {
        // Check if XGamingRuntime is loaded, that means we're on Xbox Game Pass version
        if (FindAssembly(null, "XGamingRuntime") == null)
        {
            Log.LogInfo("XGamingRuntime not found, skipping Xbox authentication setup.");
            return;
        }
        
        ClassInjector.RegisterTypeInIl2Cpp<GdkTickerComponent>();
        AddComponent<GdkTickerComponent>(); // Awake() runs synchronously → AuthManager is set

        // Wire events now that AuthManager is ready.
        XboxAuth.OnError += msg => Log.LogError("[Auth] " + msg);
        XboxAuth.OnSignedIn += (_, _) =>
        {
            // Once signed in, request the XBL3.0 token.
            // "userpresence.xboxlive.com" is confirmed to work with Star Trucker's Xbox Live config.
            XboxAuth.RequestXblToken();
        };
        XboxAuth.OnXblToken += token =>
        {
            // Capture values — Auth properties are accessed from the game thread,
            // but the HTTP call runs on a background thread to avoid blocking the game loop.
            var xuid = XboxAuth.Xuid;
            var gamertag = XboxAuth.Gamertag;
            StartAttachedThread(() => SendXboxAuth(xuid, gamertag, token));
        };
    }

    /// <summary>
    /// POST /api/auth/xbox  with the following JSON body:
    /// {
    ///   "xuid":     "2535412345678901",
    ///   "gamertag": "PlayerName",
    ///   "token":    "XBL3.0 x={uhs};{jwt}"
    /// }
    ///
    /// Server-side token validation:
    ///   1. Split the token on ';' → take the JWT part (index 1).
    ///   2. Fetch Microsoft's public keys:
    ///        GET https://xsts.auth.xboxlive.com/xsts/properties/x509certs
    ///   3. Verify the JWT RS256 signature with those keys.
    ///   4. Extract XUID from the JWT claims and compare with the posted xuid field.
    /// </summary>
    private static void SendXboxAuth(ulong xuid, string gamertag, string xblToken)
    {
        var url = $"http://{App.ServerAddress.Value}:{App.ServerPort.Value}/api/auth/xbox";
        try
        {
            var cmd = new XboxAuthCmd
            {
                Xuid = xuid,
                Gamertag = gamertag,
                XblToken = xblToken
            };

            using var content = new StringContent(JsonSerializer.Serialize(cmd), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = content;
            using var response = Http.Send(request);

            Log.LogInfo("[Auth] Server responded " + (int)response.StatusCode);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                // sometimes the Xbox API is not updated real-time, we can repeat this request
                Log.LogWarning("[Auth] Authentication failed, retrying in 5 seconds...");
                Thread.Sleep(5000);
                StartAttachedThread(() => SendXboxAuth(xuid, gamertag, xblToken));
                return;
            }

            using var stream = response.Content.ReadAsStream();
            var body = JsonSerializer.Deserialize<TicketAuthenticationDto>(stream, App.JsonReaderOptions);
            App.Log.LogInfo($"[Auth] Token: {body?.Token}");

            if (body?.Token == null) return;
            PlayerState.Token = body.Token;
            
            OverlayManager.SetSessionTokenAndNavigate(
                body.Token,
                $"http://{App.ServerAddress.Value}:{App.ServerPort.Value}/overlay");
        }
        catch (Exception ex)
        {
            Log.LogError($"[Auth] HTTP request failed: ({url})");
            Log.LogError(ex);

            Log.LogWarning("[Auth] Authentication failed, retrying in 5 seconds...");
            Thread.Sleep(5000);
            StartAttachedThread(() => SendXboxAuth(xuid, gamertag, xblToken));
        }
    }

    #endregion

    #region Harmony hooks

    [HarmonyPatch]
    private static class ClientHooks
    {
        [HarmonyPatch(typeof(PauseController), nameof(PauseController.Update), new Type[] { })]
        [HarmonyPostfix]
        public static void Update()
        {
            var auth = XboxAuth;
            if (auth == null || auth.IsSignedIn || auth.IsSigningIn) return;
            auth.SignIn();
        }
    }
    
    #endregion

    #region Force Assembly Load

    private static readonly string[] ManagedAssemblyNames =
    [
        "System.Collections.Immutable",
        "Microsoft.Extensions.Primitives",
        "System.Reflection.Metadata"
    ];

    private static void HookAssemblyResolver()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var requested = new AssemblyName(args.Name);
            if (!IsManagedAssembly(requested.Name))
                return null;

            // Return already-loaded copy with matching version if available.
            var existing = FindLoadedAssembly(requested.Name, requested.Version);
            if (existing != null)
                return existing;

            var assemblyPath = GetManagedAssemblyPath(requested.Name);
            if (assemblyPath == null)
                return null;

            try
            {
                // Load from bytes to bypass load-context version conflicts.
                var loaded = LoadAssemblyFromBytes(assemblyPath);
                Log.LogInfo($"Resolved {requested.Name} {loaded.GetName().Version} from plugin directory.");
                return loaded;
            }
            catch (Exception ex)
            {
                Log.LogError($"AssemblyResolve failed for {requested.Name}: " + ex);
                return FindLoadedAssembly(requested.Name); // Last resort: return any loaded version.
            }
        };

        // Force preload to make sure later serializer initialization binds to these copies.
        foreach (var assemblyName in ManagedAssemblyNames)
            TryPreloadAssembly(assemblyName);
    }

    private static bool IsManagedAssembly(string name)
    {
        foreach (var managed in ManagedAssemblyNames)
            if (string.Equals(name, managed, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static Assembly FindLoadedAssembly(string assemblyName, Version requiredVersion = null)
    {
        return FindAssembly(requiredVersion, assemblyName);
    }

    private static string GetManagedAssemblyPath(string assemblyName)
    {
        var pluginDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        if (string.IsNullOrWhiteSpace(pluginDirectory))
            return null;
        var path = Path.Combine(pluginDirectory, assemblyName + ".dll");
        return File.Exists(path) ? path : null;
    }

    private static Assembly LoadAssemblyFromBytes(string path)
    {
        // Loading from raw bytes avoids LoadFrom context conflicts and allows
        // loading a specific version even when another version is already loaded.
        return Assembly.Load(File.ReadAllBytes(path));
    }

    private static void TryPreloadAssembly(string assemblyName)
    {
        try
        {
            var assemblyPath = GetManagedAssemblyPath(assemblyName);
            if (assemblyPath == null)
                return;

            var diskVersion = AssemblyName.GetAssemblyName(assemblyPath).Version;

            if (FindLoadedAssembly(assemblyName, diskVersion) != null)
                return; // Exact version already loaded, nothing to do.

            LoadAssemblyFromBytes(assemblyPath);
            Log.LogInfo($"Preloaded {assemblyName} {diskVersion} from plugin directory.");
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to preload {assemblyName}.dll: " + ex);
        }
    }

    #endregion
    
    /// <summary>
    /// Starts a background thread that is attached to the IL2CPP domain, preventing
    /// "GC: Collecting from unknown thread" errors when IL2CPP objects are used
    /// from threads not created by the IL2CPP runtime.
    /// </summary>
    public static void StartAttachedThread(Action action)
    {
        new Thread(() =>
        {
            var domain = IL2CPP.il2cpp_domain_get();
            var thread = IL2CPP.il2cpp_thread_attach(domain);
            try
            {
                action();
            }
            finally
            {
                IL2CPP.il2cpp_thread_detach(thread);
            }
        }) { IsBackground = true }.Start();
    }

    private static Assembly FindAssembly(Version requiredVersion, string assemblyName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (requiredVersion == null || asm.GetName().Version == requiredVersion)
                return asm;
        }

        return null;
    }
}