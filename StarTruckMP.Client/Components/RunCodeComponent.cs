using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using StarTruckMP.Client.UI;
using UnityEngine;

namespace StarTruckMP.Client.Components;

public class RunCodeComponent : MonoBehaviour
{
    private readonly ConcurrentQueue<string> _pendingScripts = new();

    private void Awake()
    {
        // This allows during development to run code outside the game
        #if DEBUG
        OverlayManager.MessageReceived += HandleOverlayMessageReceived;
        App.Log.LogWarning("RunCode started, careful. Scripts will run on the Unity main thread.");
        #endif
    }

    private void Update()
    {
        #if DEBUG
        while (_pendingScripts.TryDequeue(out var payload))
            ExecuteCodeOnMainThread(payload);
        #endif
    }

    private void OnDestroy()
    {
        #if DEBUG
        OverlayManager.MessageReceived -= HandleOverlayMessageReceived;
        #endif
    }

    #if DEBUG
    private void HandleOverlayMessageReceived(string type, string message)
    {
        if (type != "runcode")
            return;

        _pendingScripts.Enqueue(message ?? string.Empty);
    }

    private void ExecuteCodeOnMainThread(string payload)
    {
        try
        {
            var globals = new ScriptGlobals();
            try
            {
                var code = NormalizeCodePayload(payload);
                if (string.IsNullOrWhiteSpace(code))
                {
                    globals.Log("No code received.");
                    OverlayManager.PostMessage("runcodeResponse", new RunCodeResponse { Status = 0, Logs = globals.Logs.ToArray() });
                    return;
                }

                var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location));
                var options = ScriptOptions.Default
                    .WithReferences(assemblies)
                    .WithImports("System", "UnityEngine", "StarTruckMP.Client");

                globals.Log("Executing script on the Unity main thread.");
                CSharpScript.RunAsync(code, options, globals, typeof(ScriptGlobals)).GetAwaiter().GetResult();
                OverlayManager.PostMessage("runcodeResponse", new RunCodeResponse { Status = 1, Logs = globals.Logs.ToArray() });
            }
            catch (Exception e)
            {
                App.Log.LogError("Error executing code");
                App.Log.LogError(e);
                globals.Log(e.ToString());
                OverlayManager.PostMessage("runcodeResponse", new RunCodeResponse { Status = 0, Logs = globals.Logs.ToArray() });
            }
        }
        catch (Exception e)
        {
            App.Log.LogError("Error executing code");
            App.Log.LogError(e);
            OverlayManager.PostMessage("runcodeResponse", new { status = 0, logs = new List<string>() });
        }
    }
    #endif

    private static string NormalizeCodePayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.String
                ? doc.RootElement.GetString()
                : doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return payload;
        }
    }

    public class RunCodeResponse
    {
        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("logs")]
        public string[] Logs { get; set; }
    }

    public class ScriptGlobals
    {
        public List<string> Logs { get; set; } = new List<string>();

        public void Log(string message)
        {
            Logs.Add(message);
        }
    }
}