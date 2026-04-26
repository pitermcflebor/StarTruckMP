using System.Text;

namespace StarTruckMP.Overlay.Core.Ipc;

public static class OverlayCommandParser
{
    public static OverlayCommand Parse(string? raw)
    {
        var command = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command))
            return new UnknownOverlayCommand(string.Empty);

        if (command.Equals("TOGGLEINTERACTIVE", StringComparison.OrdinalIgnoreCase))
            return new ToggleInteractiveCommand(command);

        if (command.Equals("SHOW", StringComparison.OrdinalIgnoreCase))
            return new ShowOverlayCommand(command);

        if (command.Equals("HIDE", StringComparison.OrdinalIgnoreCase))
            return new HideOverlayCommand(command);

        if (command.Equals("RUNTESTS", StringComparison.OrdinalIgnoreCase))
            return new RunDiagnosticsCommand(command);

        if (TryReadBool(command, "INTERACTIVE:", out var interactive))
            return new SetInteractiveCommand(command, interactive);

        if (TryReadBool(command, "CLICKTHROUGH:", out var clickThrough))
            return new SetClickThroughCommand(command, clickThrough);

        if (TryReadPayload(command, "NAVIGATE:", out var url))
            return new NavigateCommand(command, url);

        if (TryReadPayload(command, "TOKEN:", out var token))
            return new SetTokenCommand(command, token);

        if (TryReadPayload(command, "POSTMESSAGE:", out var postMessageBase64))
        {
            var json = DecodeBase64Utf8(postMessageBase64);
            return new PostMessageCommand(command, json);
        }

        if (TryReadPayload(command, "NAVHTML:", out var htmlBase64))
        {
            var html = DecodeBase64Utf8(htmlBase64);
            return new NavigateHtmlCommand(command, html);
        }

        return new UnknownOverlayCommand(command);
    }

    private static bool TryReadBool(string command, string prefix, out bool value)
    {
        if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return false;
        }

        var rawValue = command[prefix.Length..].Trim();
        value = rawValue is "1" or "true" or "TRUE" or "True";
        return true;
    }

    private static bool TryReadPayload(string command, string prefix, out string payload)
    {
        if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            payload = string.Empty;
            return false;
        }

        payload = command[prefix.Length..];
        return true;
    }

    private static string DecodeBase64Utf8(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return string.Empty;

        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}

