using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging.Console;
using StarTruckMP.Server;
using StarTruckMP.Server.Controllers.Services;
using StarTruckMP.Server.Crypto;
using StarTruckMP.Server.Server;
using StarTruckMP.Server.Server.Services;

internal class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        
        if (!File.Exists("server.json"))
        {
            Console.WriteLine("server.json not found, creating a new one with default settings.");
            var defaultSettings = new ServerSettings();
            File.WriteAllText("server.json", JsonSerializer.Serialize(defaultSettings, App.JsonOptionsWrite));
        }

        builder.Configuration.AddJsonFile("server.json", false, true);

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
            .AddFilter("Microsoft.AspNetCore", LogLevel.Warning) // filter out noisy Kestrel logs
#if DEBUG
            .SetMinimumLevel(LogLevel.Information)
#endif
            ;

        builder.WebHost.ConfigureKestrel(options =>
        {
            // Bind HTTP API on the same port number as LiteNetLib (UDP).
            // LiteNetLib uses UDP, Kestrel uses TCP — they can share the same port number.
            var address = IPAddress.TryParse(builder.Configuration.GetValue<string>("IpAddress"), out var ip)
                ? ip
                : IPAddress.Any;
            
            options.ConfigureHttpsDefaults(httpsOptions =>
            {
                var certPath = builder.Configuration["CertificatePath"] ?? "certs/localhost.pfx";
                var certPassword = builder.Configuration["CertificatePassword"] ?? "changeit";

                Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);

                if (!File.Exists(certPath))
                {
                    using var rsa = RSA.Create(2048);

                    var request = new CertificateRequest(
                        "CN=localhost",
                        rsa,
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1);

                    request.CertificateExtensions.Add(
                        new X509BasicConstraintsExtension(false, false, 0, false));

                    request.CertificateExtensions.Add(
                        new X509KeyUsageExtension(
                            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                            false));

                    request.CertificateExtensions.Add(
                        new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

                    var san = new SubjectAlternativeNameBuilder();
                    san.AddDnsName("localhost");
                    san.AddIpAddress(System.Net.IPAddress.Loopback);
                    san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
                    request.CertificateExtensions.Add(san.Build());

                    using var cert = request.CreateSelfSigned(
                        DateTimeOffset.UtcNow.AddDays(-1),
                        DateTimeOffset.UtcNow.AddYears(1));

                    var pfxBytes = cert.Export(X509ContentType.Pfx, certPassword);
                    File.WriteAllBytes(certPath, pfxBytes);
                }

                httpsOptions.ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword);
            });
            options.Listen(address, builder.Configuration.GetValue<int>("Port"), listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                listenOptions.UseHttps();
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

            var serverSettings = JsonSerializer.Deserialize<ServerSettings>(
                File.ReadAllText("server.json"), App.JsonOptionsRead) ?? new ServerSettings();

            // rewrite the settings file on every startup to ensure it has all the latest fields,
            // and to fix any formatting issues.
            File.WriteAllText("server.json", JsonSerializer.Serialize(serverSettings, App.JsonOptionsWrite));
            return serverSettings;
        });

        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<PlayerContainer>();
        builder.Services.AddSingleton<ServerKeyPair>();
        builder.Services.AddSingleton<ServerManager>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddHttpClient<XboxTokenValidator>();
        builder.Services.AddHttpClient<SteamTicketValidator>();

        builder.Services.AddHostedService<ServerBackgroundService>();

        var app = builder.Build();

        App.ServiceProvider = app.Services;

        app.UseHttpsRedirection()
            .UseHsts()
            .UseCors(policy =>  policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
        
        // Serve SvelteKit static assets (JS, CSS, fonts, etc.) from wwwroot
        app.UseStaticFiles();

        app.MapControllers();
        
        app.MapGet("/", () => "OK");

        var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
        var settings = app.Services.GetRequiredService<ServerSettings>();
        var bindAddress = IPAddress.TryParse(settings.IpAddress, out var bindIp) ? bindIp : IPAddress.Any;
        startupLogger.LogInformation("Kestrel HTTP server starting on https://{Address}:{Port}", bindAddress, settings.Port);

        app.Run();
    }
}