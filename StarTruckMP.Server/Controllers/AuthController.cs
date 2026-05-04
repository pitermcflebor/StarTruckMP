using Microsoft.AspNetCore.Mvc;
using StarTruckMP.Server.Controllers.Services;
using StarTruckMP.Server.Crypto;
using StarTruckMP.Shared.Cmd.Api;
using StarTruckMP.Shared.Dto.Api;

namespace StarTruckMP.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly XboxTokenValidator _xboxValidator;
    private readonly SteamTicketValidator _steamValidator;
    private readonly ServerKeyPair _serverKeyPair;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        AuthService authService,
        XboxTokenValidator xboxValidator,
        SteamTicketValidator steamValidator,
        ServerKeyPair serverKeyPair,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _xboxValidator = xboxValidator;
        _steamValidator = steamValidator;
        _serverKeyPair = serverKeyPair;
        _logger = logger;
    }

    [HttpPost("xbox")]
    [ProducesResponseType<TicketAuthenticationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> XboxAuthenticate([FromBody] XboxAuthCmd cmd, CancellationToken ct)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Received Xbox auth request: XUID={Xuid}, Gamertag={Gamertag}",
                cmd.Xuid, cmd.Gamertag);

        // 1. Basic field presence check
        if (string.IsNullOrWhiteSpace(cmd.XblToken))
            return BadRequest("XblToken is required.");

        if (string.IsNullOrWhiteSpace(cmd.Gamertag))
            return BadRequest("Gamertag is required.");

        // 2. Validate the XBL3.0 token:
        //    - Format + uhs ↔ XUID binding (local)
        //    - Cryptographic check against Xbox Live Presence API (network)
        var result = await _xboxValidator.ValidateAsync(cmd.XblToken, cmd.Xuid, ct);
        if (!result.IsValid)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(
                    "Xbox auth failed for XUID={Xuid}, Gamertag={Gamertag}: {Error}",
                    cmd.Xuid, cmd.Gamertag, result.Error);

            return Unauthorized(result.Error);
        }

        // 3. Issue a session token bound to this player
        var sessionToken = _authService.IssueXboxSessionToken(cmd.Xuid, cmd.Gamertag);

        return Ok(new TicketAuthenticationDto
        {
            Token = sessionToken,
            ServerPublicKey = Convert.ToBase64String(_serverKeyPair.PublicKeyBytes)
        });
    }

    [HttpPost("steam")]
    [ProducesResponseType<TicketAuthenticationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SteamAuthenticate([FromBody] SteamAuthCmd cmd, CancellationToken ct)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Received Steam auth request: SteamID={SteamId}", cmd.SteamId);

        // 1. Basic field presence check
        if (string.IsNullOrWhiteSpace(cmd.Ticket))
            return BadRequest("Ticket is required.");

        if (cmd.SteamId == 0)
            return BadRequest("SteamId is required.");

        // 2. Validate the auth session ticket:
        //    - Format check (valid hex string)
        //    - Cryptographic check via Steam Web API (ISteamUserAuth/AuthenticateUserTicket)
        //    - SteamID binding: API response must match the claimed SteamID
        var result = await _steamValidator.ValidateAsync(cmd.Ticket, cmd.SteamId, ct);
        if (!result.IsValid)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(
                    "Steam auth failed for SteamID={SteamId}: {Error}",
                    cmd.SteamId, result.Error);

            return Unauthorized(result.Error);
        }

        // 3. Issue a session token bound to this Steam player
        var sessionToken = _authService.IssueSteamSessionToken(cmd.SteamId);

        return Ok(new TicketAuthenticationDto
        {
            Token = sessionToken,
            ServerPublicKey = Convert.ToBase64String(_serverKeyPair.PublicKeyBytes)
        });
    }
}
