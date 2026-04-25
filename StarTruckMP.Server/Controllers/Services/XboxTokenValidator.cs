using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace StarTruckMP.Server.Controllers.Services;

/// <summary>
/// Validates an Xbox Live XBL3.0 token.
///
/// Token format:  XBL3.0 x={uhs};{token}
///
/// Security guarantee:
///   1. Format check: the token starts with "XBL3.0 x=" and has a valid structure.
///   2. Cryptographic check: the full token is forwarded to the Xbox Live Presence API.
///      Microsoft validates the token server-side; a 200 response with the matching XUID
///      proves the token was issued by Xbox GDK to a real, authenticated Xbox account.
///      It is impossible to forge this without a legitimate Xbox session (i.e. without
///      owning a copy of the game).
///   3. Title check: the presence response must contain Star Trucker's title ID as an active
///      title. This ensures the token was generated while the player is running Star Trucker,
///      not any other Xbox game. The required title ID is configured in ServerSettings.
///   4. One-time use enforcement: the SHA-256 hash of each successfully validated token is
///      stored in memory for the duration of the token's validity window (~1 hour). Any
///      subsequent attempt to reuse the same token is rejected, preventing replay attacks
///      even if the token is intercepted or intentionally shared.
/// </summary>
public sealed class XboxTokenValidator
{
    private const string Xbl3Prefix = "XBL3.0 x=";

    // The token was obtained for userpresence.xboxlive.com on the client, so we validate
    // against that same service. "users/me" resolves to the token's authenticated user.
    private const string PresenceUrl = "https://userpresence.xboxlive.com/users/me";

    // XSTS tokens typically expire in ~1 hour. We keep hashes slightly longer to ensure
    // no replay slips through at the edge of the window.
    private static readonly TimeSpan TokenCacheTtl = TimeSpan.FromHours(1.5);

    private readonly HttpClient _http;
    private readonly IMemoryCache _seenTokens;
    private readonly ServerSettings _settings;
    private readonly ILogger<XboxTokenValidator> _logger;

    public record ValidationResult(bool IsValid, ulong ParsedXuid = 0, string? Error = null);

    public XboxTokenValidator(
        HttpClient http,
        IMemoryCache seenTokens,
        ServerSettings settings,
        ILogger<XboxTokenValidator> logger)
    {
        _http = http;
        _seenTokens = seenTokens;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Validates the XBL3.0 token:
    ///   Step 1 — Format + uhs parsing: quick checks on the token structure and presence of the uhs component.
    ///   Step 2 — One-time use check: rejects previously seen tokens (replay protection).
    ///   Step 3 — Cryptographic validation: call Xbox Live and verify the returned XUID.
    ///   Step 4 — Title check: verify Star Trucker is listed as an active title.
    /// </summary>
    public async Task<ValidationResult> ValidateAsync(
        string? xblToken, ulong claimedXuid, CancellationToken ct = default)
    {
        var formatResult = ValidateFormat(xblToken, claimedXuid);
        if (!formatResult.IsValid)
            return formatResult;

        // Step 2: reject replayed tokens before hitting the network.
        var tokenHash = ComputeTokenHash(xblToken!);
        if (_seenTokens.TryGetValue(tokenHash, out _))
        {
            _logger.LogWarning(
                "Replay attack detected: token for XUID={Xuid} was already used.", claimedXuid);
            return Fail("Token has already been used. Obtain a fresh token and try again.");
        }

        // Steps 3 & 4: call Xbox Live to cryptographically verify the token and title.
        var liveResult = await VerifyWithXboxLiveAsync(xblToken!, claimedXuid, ct);
        if (!liveResult.IsValid)
            return liveResult;

        // Mark the token as consumed so it cannot be replayed.
        _seenTokens.Set(tokenHash, true, TokenCacheTtl);

        return liveResult;
    }

    #region Private helpers

    private static ValidationResult ValidateFormat(string? xblToken, ulong claimedXuid)
    {
        if (string.IsNullOrWhiteSpace(xblToken))
            return Fail("Token is null or empty.");

        if (!xblToken.StartsWith(Xbl3Prefix, StringComparison.Ordinal))
            return Fail($"Token does not start with '{Xbl3Prefix}'.");

        var rest = xblToken[Xbl3Prefix.Length..];
        var sep = rest.IndexOf(';');

        if (sep < 1)
            return Fail("Token format invalid: missing ';' separator after uhs.");

        if (string.IsNullOrEmpty(rest[(sep + 1)..]))
            return Fail("Token value (after ';') is empty.");

        // The uhs (User Hash) is NOT the XUID — it is a separate opaque value derived by
        // Xbox Live. XUID verification is handled in step 3 by comparing the xuid field
        // returned by the Xbox Live Presence API against the claimed XUID.
        return new ValidationResult(IsValid: true, ParsedXuid: claimedXuid);
    }

    private async Task<ValidationResult> VerifyWithXboxLiveAsync(
        string xblToken, ulong claimedXuid, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, PresenceUrl);
            request.Headers.TryAddWithoutValidation("Authorization", xblToken);
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "3");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Xbox Live rejected token for XUID={Xuid}: HTTP {Status}",
                    claimedXuid, (int)response.StatusCode);

                return Fail($"Xbox Live rejected the token (HTTP {(int)response.StatusCode}).");
            }

            var body = await response.Content.ReadFromJsonAsync<PresenceResponse>(
                JsonSerializerOptions.Web, ct);

            if (body is null || !ulong.TryParse(body.Xuid, out var responseXuid))
                return Fail("Could not parse XUID from Xbox Live presence response.");

            if (responseXuid != claimedXuid)
            {
                _logger.LogWarning(
                    "Xbox Live XUID mismatch: response={ResponseXuid}, claimed={ClaimedXuid}",
                    responseXuid, claimedXuid);

                return Fail(
                    $"XUID mismatch: client claimed {claimedXuid}.");
            }

            // Step 4: verify the player is running Star Trucker right now.
            if (_settings.XboxRequiredTitleId != 0)
            {
                var allTitles = body.Devices?
                    .SelectMany(d => d.Titles ?? [])
                    .ToList() ?? [];

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(
                        "Active titles for XUID={Xuid}: [{Titles}]",
                        claimedXuid,
                        string.Join(", ", allTitles.Select(t => $"{t.Id} ({t.Name})")));

                var titleIdStr = _settings.XboxRequiredTitleId.ToString();
                var hasTitle = allTitles.Any(t => t.Id == titleIdStr);

                if (!hasTitle)
                {
                    _logger.LogWarning(
                        "Title check failed for XUID={Xuid}: title {TitleId} not active. " +
                        "Token may have been generated from a different Xbox game.",
                        claimedXuid, _settings.XboxRequiredTitleId);

                    return Fail(
                        "Star Trucker does not appear to be running. " +
                        "Authenticate while the game is active. Or retry again.");
                }
            }

            return new ValidationResult(IsValid: true, ParsedXuid: responseXuid);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error while verifying Xbox Live token for XUID={Xuid}", claimedXuid);
            return Fail($"Network error contacting Xbox Live. Please try again later.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while verifying Xbox Live token for XUID={Xuid}", claimedXuid);
            return Fail($"Unexpected error during Xbox Live validation. Please try again later.");
        }
    }

    private static ValidationResult Fail(string error) => new(IsValid: false, Error: error);

    /// <summary>
    /// Returns a compact, collision-resistant fingerprint of the raw token string.
    /// The hash is stored as the cache key — never the raw token — to avoid
    /// keeping sensitive material in memory longer than necessary.
    /// </summary>
    private static string ComputeTokenHash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes);
    }

    #endregion

    #region Presence API DTOs

    private sealed class PresenceResponse
    {
        [JsonPropertyName("xuid")]
        public string? Xuid { get; init; }

        [JsonPropertyName("devices")]
        public List<DeviceEntry>? Devices { get; init; }
    }

    private sealed class DeviceEntry
    {
        [JsonPropertyName("titles")]
        public List<TitleEntry>? Titles { get; init; }
    }

    private sealed class TitleEntry
    {
        // Decimal string representation of the uint32 title ID.
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    #endregion
}
