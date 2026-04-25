using System.Text.Json.Serialization;

namespace StarTruckMP.Server.Controllers.Services;

/// <summary>
/// Validates a Steam auth session ticket obtained via SteamUser.GetAuthSessionTicket().
///
/// Security guarantee:
///   1. The raw ticket bytes (hex-encoded) are forwarded to the Steam Web API:
///        GET https://api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1/
///           ?key={SteamWebApiKey}&amp;appid={SteamAppId}&amp;ticket={hexTicket}
///      Steam validates the ticket cryptographically server-side. A successful response
///      proves the ticket was issued to a real, authenticated Steam account that owns
///      the app. It is impossible to forge without a valid Steam session.
///   2. The SteamID returned by the API is compared against the SteamID claimed by the
///      client, preventing one player from authenticating as another.
///   3. If SteamWebApiKey is empty the validator falls back to a warning-only mode,
///      issuing a session without cryptographic proof (for local dev only).
/// </summary>
public sealed class SteamTicketValidator
{
    private const string SteamAuthUrl =
        "https://api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1/";

    private readonly HttpClient _http;
    private readonly ServerSettings _settings;
    private readonly ILogger<SteamTicketValidator> _logger;

    public record ValidationResult(bool IsValid, ulong ParsedSteamId = 0, string? Error = null);

    public SteamTicketValidator(
        HttpClient http,
        ServerSettings settings,
        ILogger<SteamTicketValidator> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Validates a Steam auth session ticket.
    ///   Step 1 — Format check: ticket must be a non-empty hex string.
    ///   Step 2 — Web API call: forward the ticket to Steam for cryptographic verification.
    ///   Step 3 — SteamID binding: Steam's response must match the claimed SteamID.
    /// </summary>
    public async Task<ValidationResult> ValidateAsync(
        string? ticketHex, ulong claimedSteamId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticketHex))
            return Fail("Ticket is null or empty.");

        if (!IsValidHex(ticketHex))
            return Fail("Ticket is not a valid hex string.");

        if (string.IsNullOrWhiteSpace(_settings.SteamWebApiKey))
        {
            _logger.LogWarning(
                "[Steam] SteamWebApiKey is not configured. Ticket for SteamID={SteamId} " +
                "accepted WITHOUT cryptographic validation. Set SteamWebApiKey in server.json.",
                claimedSteamId);
            return new ValidationResult(IsValid: true, ParsedSteamId: claimedSteamId);
        }

        return await VerifyWithSteamAsync(ticketHex, claimedSteamId, ct);
    }

    #region Private helpers

    private async Task<ValidationResult> VerifyWithSteamAsync(
        string ticketHex, ulong claimedSteamId, CancellationToken ct)
    {
        var url = $"{SteamAuthUrl}?key={_settings.SteamWebApiKey}" +
                  $"&appid={_settings.SteamAppId}&ticket={ticketHex}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[Steam] Steam Web API returned HTTP {Status} for SteamID={SteamId}.",
                    (int)response.StatusCode, claimedSteamId);
                return Fail($"Steam Web API returned HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadFromJsonAsync<SteamAuthResponse>(
                System.Text.Json.JsonSerializerOptions.Web, ct);

            var responseParams = body?.Response?.Params;
            var error = body?.Response?.Error;

            if (error is not null)
            {
                _logger.LogWarning(
                    "[Steam] Ticket rejected for SteamID={SteamId}: [{Code}] {Desc}",
                    claimedSteamId, error.ErrorCode, error.ErrorDesc);
                return Fail($"Steam rejected the ticket: {error.ErrorDesc}");
            }

            if (responseParams is null || responseParams.Result != "OK")
            {
                _logger.LogWarning(
                    "[Steam] Unexpected response for SteamID={SteamId}: result={Result}",
                    claimedSteamId, responseParams?.Result ?? "<null>");
                return Fail("Steam ticket validation did not return OK.");
            }

            if (!ulong.TryParse(responseParams.SteamId, out var responseSteamId))
                return Fail("Could not parse SteamID from Steam Web API response.");

            if (responseSteamId != claimedSteamId)
            {
                _logger.LogWarning(
                    "[Steam] SteamID mismatch: response={ResponseId}, claimed={ClaimedId}",
                    responseSteamId, claimedSteamId);
                return Fail($"SteamID mismatch: client claimed {claimedSteamId}.");
            }

            return new ValidationResult(IsValid: true, ParsedSteamId: responseSteamId);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[Steam] HTTP error while verifying ticket for SteamID={SteamId}", claimedSteamId);
            return Fail("Network error contacting Steam Web API. Please try again later.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Steam] Unexpected error while verifying ticket for SteamID={SteamId}", claimedSteamId);
            return Fail("Unexpected error during Steam ticket validation. Please try again later.");
        }
    }

    private static bool IsValidHex(string value)
    {
        if (value.Length == 0 || value.Length % 2 != 0)
            return false;
        foreach (var c in value)
            if (!Uri.IsHexDigit(c))
                return false;
        return true;
    }

    private static ValidationResult Fail(string error) => new(IsValid: false, Error: error);

    #endregion

    #region Steam Web API response DTOs

    private sealed class SteamAuthResponse
    {
        [JsonPropertyName("response")]
        public SteamAuthResponseBody? Response { get; init; }
    }

    private sealed class SteamAuthResponseBody
    {
        [JsonPropertyName("params")]
        public SteamAuthParams? Params { get; init; }

        [JsonPropertyName("error")]
        public SteamAuthError? Error { get; init; }
    }

    private sealed class SteamAuthParams
    {
        [JsonPropertyName("result")]
        public string? Result { get; init; }

        [JsonPropertyName("steamid")]
        public string? SteamId { get; init; }

        [JsonPropertyName("ownersteamid")]
        public string? OwnerSteamId { get; init; }

        [JsonPropertyName("vacbanned")]
        public bool VacBanned { get; init; }

        [JsonPropertyName("publisherbanned")]
        public bool PublisherBanned { get; init; }
    }

    private sealed class SteamAuthError
    {
        [JsonPropertyName("errorcode")]
        public int ErrorCode { get; init; }

        [JsonPropertyName("errordesc")]
        public string? ErrorDesc { get; init; }
    }

    #endregion
}

