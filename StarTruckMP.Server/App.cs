using System.Text.Json;

namespace StarTruckMP.Server;

public static class App
{
    public static readonly JsonSerializerOptions JsonOptionsWrite = new() { WriteIndented = true };
    public static readonly JsonSerializerOptions JsonOptionsRead = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowDuplicateProperties = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
    
    public static IServiceProvider ServiceProvider { get; internal set; } = null!;
}