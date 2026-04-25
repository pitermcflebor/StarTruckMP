using BepInEx.Configuration;
using BepInEx.Logging;

namespace StarTruckMP.Client;

public static class App
{
    public static ConfigEntry<string> ServerAddress;
    public static ConfigEntry<string> ServerPort;

    public static void Configure(ConfigFile config)
    {
        ServerAddress = config.Bind("Connection", "ServerAddress", "127.0.0.1", "StarTruckMP server address");
        ServerPort = config.Bind("Connection", "ServerPort", "7777", "StarTruckMP server port");
    }

    public static ManualLogSource Log;
}