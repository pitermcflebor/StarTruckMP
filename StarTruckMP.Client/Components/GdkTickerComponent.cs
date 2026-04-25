using System;
using StarTruckMP.Client.Authentication;
using UnityEngine;

namespace StarTruckMP.Client.Components;

/// <summary>
/// Persistent MonoBehaviour that drives the GDK task queue every frame.
/// Registered and added by Plugin.Load() using BepInEx's own persistent GameObject.
/// </summary>
public class GdkTickerComponent : MonoBehaviour
{
    // Required by Il2CppInterop for injected MonoBehaviour types.
    public GdkTickerComponent(IntPtr ptr) : base(ptr) { }

    internal static XboxAuthManager AuthManager { get; private set; }

    private void Awake()
    {
        AuthManager = new XboxAuthManager();
        AuthManager.Initialize();
        DontDestroyOnLoad(gameObject);
    }

    private void Update() => AuthManager?.Tick();

    private void OnDestroy()
    {
        AuthManager?.Dispose();
        AuthManager = null;
    }
}
