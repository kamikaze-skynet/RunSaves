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

    private static void OnLog(string message, string stack, LogType type)
    {
        if (type != LogType.Exception || string.IsNullOrWhiteSpace(stack)) return;
        float now = Time.realtimeSinceStartup;
        if (now - windowStart > 60f) { windowStart = now; inWindow = 0; }
        if (++inWindow > 8) return;
        Plugin.Log.LogWarning($"Game exception: {message}\n{stack.Trim()}");
    }
}
