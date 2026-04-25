using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using XGamingRuntime;

namespace StarTruckMP.Client.Authentication;

/// <summary>
/// Handles Xbox (GDK) authentication through the BepInEx IL2CPP interop of XGamingRuntime.
/// Tick() must be called every frame to dispatch the GDK task queue.
/// </summary>
public class XboxAuthManager
{
    private static ManualLogSource Log => Plugin.Log;

    private XUserHandle _userHandle;
    private bool _sdkInitialized;
    private bool _signingIn;
    private bool _tokenRequested;

    public bool IsSignedIn => _userHandle != null;
    public bool IsSigningIn => _signingIn;

    /// <summary>Xbox User ID — unique, permanent numeric identifier.</summary>
    public ulong Xuid { get; private set; }

    /// <summary>Classic gamertag (max 15 chars) of the signed-in user.</summary>
    public string Gamertag { get; private set; }

    /// <summary>XBL3.0 token received from the GDK. Format: "XBL3.0 x={uhs};{jwt}"</summary>
    public string XblToken { get; private set; }

    /// <summary>Fired when the user signs in and identity is read (xuid, gamertag).</summary>
    public event Action<ulong, string> OnSignedIn;

    /// <summary>Fired when a signed XBL3.0 token is received (token).</summary>
    public event Action<string> OnXblToken;

    /// <summary>Fired on any error (message).</summary>
    public event Action<string> OnError;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public void Initialize()
    {
        int hr = SDK.XGameRuntimeInitialize();
        if (HR.FAILED(hr))
        {
            Log.LogError("[Auth] XGameRuntimeInitialize failed: 0x" + hr.ToString("X8"));
            OnError?.Invoke("XGameRuntimeInitialize failed: 0x" + hr.ToString("X8"));
            return;
        }
        _sdkInitialized = true;
        Log.LogInfo("[Auth] GDK runtime initialized.");
    }

    /// <summary>Must be called every frame to process async GDK callbacks.</summary>
    public void Tick()
    {
        if (_sdkInitialized) SDK.XTaskQueueDispatch(0u);
    }

    public void Dispose()
    {
        if (_userHandle != null) { SDK.XUserCloseHandle(_userHandle); _userHandle = null; }
        if (_sdkInitialized)     { SDK.XGameRuntimeUninitialize(); _sdkInitialized = false; }
    }

    // ── Authentication ───────────────────────────────────────────────────────

    /// <summary>
    /// Adds the default Xbox user, showing UI if needed.
    /// On success, reads XUID and gamertag and fires OnSignedIn.
    /// </summary>
    public void SignIn()
    {
        if (!_sdkInitialized)          { Log.LogError("[Auth] Not initialized."); return; }
        if (IsSignedIn || _signingIn)  { return; }

        _signingIn = true;
        Log.LogInfo("[Auth] Signing in...");

        // XUserAddCompleted has an implicit operator from Action<int, XUserHandle>.
        Action<int, XUserHandle> cb = (hr, handle) =>
        {
            _signingIn = false;
            if (HR.FAILED(hr)) { OnError?.Invoke("SignIn failed: 0x" + hr.ToString("X8")); return; }
            _userHandle = handle;
            ReadIdentity();
        };
        SDK.XUserAddAsync(XUserAddOptions.AddDefaultUserAllowingUI, cb);
    }

    private void ReadIdentity()
    {
        int hr = SDK.XUserGetId(_userHandle, out ulong xuid);
        if (HR.FAILED(hr)) { OnError?.Invoke("XUserGetId failed: 0x" + hr.ToString("X8")); return; }

        hr = SDK.XUserGetGamertag(_userHandle, XUserGamertagComponent.Classic, out string gamertag);
        if (HR.FAILED(hr)) { OnError?.Invoke("XUserGetGamertag failed: 0x" + hr.ToString("X8")); return; }

        Xuid     = xuid;
        Gamertag = gamertag;
        Log.LogInfo("[Auth] Signed in — XUID: " + xuid + "  Gamertag: " + gamertag);
        OnSignedIn?.Invoke(xuid, gamertag);
    }

    // ── XBL3.0 Token ────────────────────────────────────────────────────────

    /// <summary>
    /// Requests a GDK-signed XBL3.0 token for the given service URL.
    /// The URL must be registered in Star Trucker's Xbox Live service configuration.
    /// Known working URL: "https://userpresence.xboxlive.com"
    ///
    /// Server-side validation:
    ///   1. GET https://xsts.auth.xboxlive.com/xsts/properties/x509certs  → public keys
    ///   2. Verify the JWT signature in the token.
    ///   3. Extract XUID from the JWT claims (uhs claim or sub).
    /// </summary>
    public void RequestXblToken(string serviceUrl = "https://userpresence.xboxlive.com")
    {
        if (!IsSignedIn)       { Log.LogError("[Auth] Not signed in."); return; }
        if (_tokenRequested)   { return; }
        _tokenRequested = true;

        var headers = new Il2CppReferenceArray<XUserGetTokenAndSignatureUtf16HttpHeader>(
            new[] { new XUserGetTokenAndSignatureUtf16HttpHeader("Content-Type", "application/json") });
        var body = new Il2CppStructArray<byte>(System.Text.Encoding.UTF8.GetBytes("{}"));

        // XUserGetTokenAndSignatureUtf16Result has an implicit operator from
        // Action<int, XUserGetTokenAndSignatureUtf16Data>.
        Action<int, XUserGetTokenAndSignatureUtf16Data> cb = (hr, result) =>
        {
            if (HR.FAILED(hr))
            {
                _tokenRequested = false;
                OnError?.Invoke("RequestXblToken failed: 0x" + hr.ToString("X8"));
                return;
            }
            XblToken = result.Token;
            Log.LogInfo("[Auth] XBL3.0 token received.");
            OnXblToken?.Invoke(result.Token);
        };

        SDK.XUserGetTokenAndSignatureUtf16Async(
            _userHandle, XUserGetTokenAndSignatureOptions.None,
            "POST", serviceUrl, headers, body, cb);
    }
}
