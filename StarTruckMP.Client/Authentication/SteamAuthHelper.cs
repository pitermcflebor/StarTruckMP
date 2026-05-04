using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using StarTruckMP.Client.Http;
using StarTruckMP.Shared.Cmd.Api;
using StarTruckMP.Shared.Dto.Api;
using StarTruckMP.Client.UI;

namespace StarTruckMP.Client.Authentication;

/// <summary>
/// Contains all Steamworks.NET-specific authentication logic.
/// This class is intentionally isolated so that the CLR never JIT-compiles
/// Steamworks types unless the assembly is confirmed to be present at runtime.
/// </summary>
internal static class SteamAuthHelper
{
    private static ManualLogSource Log => Plugin.Log;

    /// <summary>
    /// Initialises Steam and obtains an auth ticket, then posts it to the server.
    /// Must only be called after verifying that Steamworks.NET is loaded.
    /// </summary>
    public static void Run()
    {
        try
        {
            while (!Steamworks.SteamAPI.Init())
            {
                Thread.Sleep(50);
            }

            if (!Steamworks.SteamAPI.IsSteamRunning())
            {
                Log.LogWarning("[Auth] Steam is not running, skipping Steam authentication.");
                return;
            }

            if (!Steamworks.SteamUser.BLoggedOn())
            {
                Log.LogWarning("[Auth] Steam user is not logged on, skipping Steam authentication.");
                return;
            }

            var steamId = Steamworks.SteamUser.GetSteamID();

            // GetAuthSessionTicket requires an Il2CppStructArray<byte> buffer.
            // 1024 bytes is more than enough for a Steam auth ticket (~200 bytes typical).
            const int bufferSize = 1024;
            var buffer = new Il2CppStructArray<byte>(bufferSize);
            var handle = Steamworks.SteamUser.GetAuthSessionTicket(buffer, bufferSize, out var ticketSize);

            if (handle.m_HAuthTicket == 0 || ticketSize == 0)
            {
                Log.LogError("[Auth] GetAuthSessionTicket returned an invalid handle or empty ticket.");
                return;
            }

            // Copy only the valid bytes from the buffer.
            var ticketBytes = new byte[ticketSize];
            for (var i = 0; i < (int)ticketSize; i++)
                ticketBytes[i] = buffer[i];

            // Encode as lowercase hex — the format expected by ISteamUserAuth/AuthenticateUserTicket.
            var ticketHex = Convert.ToHexString(ticketBytes).ToLowerInvariant();
            var steamIdValue = steamId.m_SteamID;

            Log.LogInfo($"[Auth] Steam ticket obtained for SteamID={steamIdValue} ({ticketSize} bytes).");

            Plugin.StartAttachedThread(() => Send(steamIdValue, ticketHex));
        }
        catch (Exception ex)
        {
            Log.LogError("[Auth] Failed to obtain Steam auth ticket:");
            Log.LogError(ex);
        }
    }

    /// <summary>
    /// POST /api/auth/steam  with the following JSON body:
    /// {
    ///   "steamId": 76561198000000000,
    ///   "ticket":  "0123abcdef..."
    /// }
    ///
    /// Server-side ticket validation:
    ///   1. Forward the hex-encoded ticket to the Steam Web API:
    ///        GET https://api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1/
    ///           ?key={SteamWebApiKey}&amp;appid={SteamAppId}&amp;ticket={hexTicket}
    ///   2. Verify response.params.result == "OK".
    ///   3. Compare response.params.steamid against the posted steamId field.
    /// </summary>
    private static void Send(ulong steamId, string ticketHex)
    {
        var url = $"https://{App.ServerAddress.Value}:{App.ServerPort.Value}/api/auth/steam";
        try
        {
            var cmd = new SteamAuthCmd
            {
                SteamId = steamId,
                Ticket = ticketHex
            };

            using var content = new StringContent(JsonSerializer.Serialize(cmd), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = content;
            using var response = HttpFactory.Create().Send(request);

            Log.LogInfo("[Auth] Steam server responded " + (int)response.StatusCode);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Log.LogWarning("[Auth] Steam authentication failed, retrying in 5 seconds...");
                Thread.Sleep(5000);
                Send(steamId, ticketHex);
                return;
            }

            using var stream = response.Content.ReadAsStream();
            var body = JsonSerializer.Deserialize<TicketAuthenticationDto>(stream, App.JsonReaderOptions);
            if (body == null)
            {
                var rawResult = response.Content.ReadAsStringAsync().Result;
                App.Log.LogError($"[Auth] Failed to parse Steam auth response body. Content: {rawResult}");
                return;
            }
            
            App.Log.LogInfo($"[Auth] Steam token: {body.Token}");
            if (body.Token == null)
            {
                var rawResult = response.Content.ReadAsStringAsync().Result;
                App.Log.LogError($"[Auth] Token was empty. Content: {rawResult}");
            }

            if (body.Token == null) return;
            
            PlayerState.Token = body.Token;

            if (!string.IsNullOrEmpty(body.ServerPublicKey))
            {
                try
                {
                    PlayerState.ServerPublicKey = Convert.FromBase64String(body.ServerPublicKey);
                    App.Log.LogInfo("[Auth] Steam - server public key stored for ECDH.");
                }
                catch (Exception ex)
                {
                    App.Log.LogError("[Auth] Steam - failed to decode server public key: " + ex.Message);
                }
            }
            else
            {
                App.Log.LogWarning("[Auth] Steam - server did not return a public key; UDP encryption will not be established.");
            }
            
            OverlayManager.SetSessionTokenAndNavigate(
                body.Token,
                $"https://{App.ServerAddress.Value}:{App.ServerPort.Value}/overlay");
        }
        catch (Exception ex)
        {
            Log.LogError($"[Auth] Steam HTTP request failed: ({url})");
            Log.LogError(ex);

            Log.LogWarning("[Auth] Steam authentication failed, retrying in 5 seconds...");
            Thread.Sleep(5000);
            Send(steamId, ticketHex);
        }
    }
}

