using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using StarTruckMP.Server;
using StarTruckMP.Server.Server;
using StarTruckMP.Server.Server.Services;

internal class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateDefaultBuilder(args)
            .UseSystemd() // Linux daemon
            .UseWindowsService() // Windows services
            .ConfigureLogging(logging => logging
                .AddSimpleConsole(opt =>
                {
                    opt.ColorBehavior = LoggerColorBehavior.Enabled;
                    opt.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
                })
                #if DEBUG
                // log all while debugging
                .SetMinimumLevel(LogLevel.Trace)
                #endif
            )
            .ConfigureServices(services =>
            {
                services.AddSingleton<ServerSettings>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<Program>>();
                    
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("Server startup at {DateTime:yyyy-MM-dd HH:mm:ss}, log level {logLevel}", DateTime.Now, logger.IsEnabled(LogLevel.Trace) ? "Trace" : logger.IsEnabled(LogLevel.Debug) ? "Debug" : logger.IsEnabled(LogLevel.Information) ? "Information" : logger.IsEnabled(LogLevel.Warning) ? "Warning" : logger.IsEnabled(LogLevel.Error) ? "Error" : "Critical");
                    
                    // Load 'server.json' file
                    if (!File.Exists("server.json"))
                    {
                        logger.LogWarning("server.json not found, creating a new one with default settings.");
                        var defaultSettings = new ServerSettings();
                        File.WriteAllText("server.json", JsonSerializer.Serialize(defaultSettings, App.JsonOptionsWrite));
                    }
                    
                    var serverSettings = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText("server.json"), App.JsonOptionsRead);
                    if (serverSettings != null) return serverSettings;
                    
                    // create a new file
                    serverSettings = new ServerSettings();
                    File.WriteAllText("server.json", JsonSerializer.Serialize(serverSettings));

                    return serverSettings;
                });

                services.AddSingleton<PlayerContainer>();
                services.AddSingleton<ServerManager>();

                services.AddHostedService<ServerBackgroundService>();
            });
        var host = builder.Build();
        host.Run();
    }
}