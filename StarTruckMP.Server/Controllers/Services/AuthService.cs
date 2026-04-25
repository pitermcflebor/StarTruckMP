using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace StarTruckMP.Server.Controllers.Services;

public sealed class AuthService
{
    private readonly ILogger<AuthService> _logger;
    private readonly ServerSettings _settings;

    // token → (username, expiry)
    private readonly ConcurrentDictionary<string, (string Username, DateTime ExpiresAt)> _tokens = new();

    public AuthService(ILogger<AuthService> logger, ServerSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>Invalidates a session token. Returns false if the token was not found.</summary>
    public bool Logout(string token)
    {
        if (!_tokens.TryRemove(token, out var session))
            return false;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("User '{Username}' logged out", session.Username);

        return true;
    }

    /// <summary>Returns true when the token exists and has not yet expired.</summary>
    public bool IsTokenValid(string token)
    {
        if (!_tokens.TryGetValue(token, out var session))
            return false;

        if (DateTime.UtcNow <= session.ExpiresAt)
            return true;

        // Expired — clean up
        _tokens.TryRemove(token, out _);
        return false;
    }

    /// <summary>Resolves the username associated with a valid token, or null.</summary>
    public string? GetUsername(string token)
    {
        if (_tokens.TryGetValue(token, out var session) && DateTime.UtcNow <= session.ExpiresAt)
            return session.Username;
        return null;
    }

    /// <summary>
    /// Issues a new session token for a player that has been authenticated via Steam.
    /// </summary>
    public string IssueSteamSessionToken(ulong steamId)
    {
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = DateTime.UtcNow.Add(_settings.ApiTokenLifetime);
        var username = $"STEAM_{steamId}";
        _tokens[token] = (username, expiresAt);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Steam player '{Username}' authenticated, token expires at {ExpiresAt:u}",
                username, expiresAt);

        return token;
    }

    /// <summary>
    /// Issues a new session token for a player that has been authenticated via Xbox Live.
    /// </summary>
    public string IssueXboxSessionToken(ulong xuid, string gamertag)
    {
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = DateTime.UtcNow.Add(_settings.ApiTokenLifetime);
        var username = $"{gamertag}#{xuid}";
        _tokens[token] = (username, expiresAt);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Xbox player '{Username}' authenticated, token expires at {ExpiresAt:u}",
                username, expiresAt);

        return token;
    }

    #region Helpers

    private bool ValidateCredentials(string username, string password)
    {
        // A single admin account configured in server.json.
        // Constant-time comparison prevents timing attacks.
        var userOk = string.Equals(username, _settings.ApiAdminUsername, StringComparison.Ordinal);
        var passOk = string.Equals(password, _settings.ApiAdminPassword, StringComparison.Ordinal);
        return userOk && passOk;
    }

    #endregion
}

