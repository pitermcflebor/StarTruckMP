namespace StarTruckMP.Overlay.Core.Ipc;

public abstract record OverlayCommand(string Raw);

public sealed record SetInteractiveCommand(string Raw, bool Enabled) : OverlayCommand(Raw);

public sealed record SetClickThroughCommand(string Raw, bool Enabled) : OverlayCommand(Raw);

public sealed record ToggleInteractiveCommand(string Raw) : OverlayCommand(Raw);

public sealed record NavigateCommand(string Raw, string Url) : OverlayCommand(Raw);

public sealed record NavigateHtmlCommand(string Raw, string Html) : OverlayCommand(Raw);

public sealed record SetTokenCommand(string Raw, string Token) : OverlayCommand(Raw);

public sealed record PostMessageCommand(string Raw, string Json) : OverlayCommand(Raw);

public sealed record ShowOverlayCommand(string Raw) : OverlayCommand(Raw);

public sealed record HideOverlayCommand(string Raw) : OverlayCommand(Raw);

public sealed record RunDiagnosticsCommand(string Raw) : OverlayCommand(Raw);

public sealed record UnknownOverlayCommand(string Raw) : OverlayCommand(Raw);

