using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging.Console;
using StarTruckMP.Server;
using StarTruckMP.Server.Controllers.Services;
using StarTruckMP.Server.Server;
using StarTruckMP.Server.Server.Services;

internal class Program
{
    public static void Main(string[] args)
    {
        // Load settings early so Kestrel can bind to the correct port
        ServerSettings earlySettings;
        earlySettings = File.Exists("server.json")
            ? JsonSerializer.Deserialize<ServerSettings>(
                File.ReadAllText("server.json"), App.JsonOptionsRead) ?? new ServerSettings()
            : new ServerSettings();

        var builder = WebApplication.CreateBuilder(args);

        builder.Host
            .UseSystemd()
            .UseWindowsService();

        builder.Logging
            .ClearProviders()
            .AddSimpleConsole(opt =>
            {
                opt.ColorBehavior = LoggerColorBehavior.Enabled;
                opt.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
            })
#if DEBUG
            .SetMinimumLevel(LogLevel.Information)
#endif
            ;

        builder.WebHost.ConfigureKestrel(options =>
        {
            // Bind HTTP API on the same port number as LiteNetLib (UDP).
            // LiteNetLib uses UDP, Kestrel uses TCP — they can share the same port number.
            var address = IPAddress.TryParse(earlySettings.IpAddress, out var ip)
                ? ip
                : IPAddress.Any;
            
            options.Listen(address, earlySettings.Port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
            });
        });

        builder.Services.AddControllers()
            .AddJsonOptions(opt =>
            {
                opt.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                opt.JsonSerializerOptions.WriteIndented = false;
            });

        builder.Services.AddSingleton<ServerSettings>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<Program>>();

            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "Server startup at {DateTime:yyyy-MM-dd HH:mm:ss}, log level {logLevel}",
                    DateTime.Now,
                    logger.IsEnabled(LogLevel.Trace) ? "Trace"
                    : logger.IsEnabled(LogLevel.Debug) ? "Debug"
                    : logger.IsEnabled(LogLevel.Information) ? "Information"
                    : logger.IsEnabled(LogLevel.Warning) ? "Warning"
                    : logger.IsEnabled(LogLevel.Error) ? "Error"
                    : "Critical");

            if (!File.Exists("server.json"))
            {
                logger.LogWarning("server.json not found, creating a new one with default settings.");
                var defaultSettings = new ServerSettings();
                File.WriteAllText("server.json", JsonSerializer.Serialize(defaultSettings, App.JsonOptionsWrite));
            }

            var serverSettings = JsonSerializer.Deserialize<ServerSettings>(
                File.ReadAllText("server.json"), App.JsonOptionsRead) ?? new ServerSettings();

            // rewrite the settings file on every startup to ensure it has all the latest fields,
            // and to fix any formatting issues.
            File.WriteAllText("server.json", JsonSerializer.Serialize(serverSettings, App.JsonOptionsWrite));
            return serverSettings;
        });

        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<PlayerContainer>();
        builder.Services.AddSingleton<ServerManager>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddHttpClient<XboxTokenValidator>();
        builder.Services.AddHttpClient<SteamTicketValidator>();

        builder.Services.AddHostedService<ServerBackgroundService>();

        var app = builder.Build();

        App.ServiceProvider = app.Services;

        app.MapControllers();
        
        app.MapGet("/", () => "OK");

        var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
        var settings = app.Services.GetRequiredService<ServerSettings>();
        var bindAddress = IPAddress.TryParse(settings.IpAddress, out var bindIp) ? bindIp : IPAddress.Any;
        startupLogger.LogInformation("Kestrel HTTP server starting on http://{Address}:{Port}", bindAddress, settings.Port);

        app.Run();
    }
}