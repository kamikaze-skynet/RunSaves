using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace RunSaves;

/// <summary>Copies the game's own exceptions, with where they came from, into the BepInEx log (which normally only
/// gets the message). Limited to a few a minute.</summary>
internal static class Diag
{
    private static Application.LogCallback callback; // kept alive for IL2CPP
    private static float windowStart;
    private static int inWindow;

    internal static void Hook()
    {
        try
        {
            callback = DelegateSupport.ConvertDelegate<Application.LogCallback>(new Action<string, string, LogType>(OnLog));
            Application.add_logMessageReceived(callback);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Couldn't hook the game's error log: {ex.Message}"); }
    }

    private static readonly System.Collections.Generic.Dictionary<string, int> seen = new();

    private static void OnLog(string message, string stack, LogType type)
    {
        if (type != LogType.Exception && !(type == LogType.Error && message != null && message.Contains("Exception"))) return;
        if (string.IsNullOrWhiteSpace(stack)) return;
        float now = Time.realtimeSinceStartup;
        if (now - windowStart > 60f) { windowStart = now; inWindow = 0; seen.Clear(); }
        // Each distinct place once a minute (a broken Update throws every frame), at most 12 a minute.
        string key = message + "|" + (stack.Length > 300 ? stack.Substring(0, 300) : stack);
        seen.TryGetValue(key, out int n);
        seen[key] = n + 1;
        if (n > 0 || ++inWindow > 12) return;
        Plugin.Log.LogWarning($"Game exception: {message}\n{stack.Trim()}");
    }
}
