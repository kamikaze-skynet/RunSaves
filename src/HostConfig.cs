using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace RunSaves;

/// <summary>
/// Everyone plays with the host's settings. While a game is hosted, the host sends its whole config (except the
/// personal display/key settings) to all players every couple of seconds, lobby included. On a player's game the
/// values are swapped in memory for as long as they're connected, so every part of the mod reads the host's values;
/// the player's own config file is never changed, and their own values come back when they leave.
/// If the host doesn't have this mod, nothing arrives and the mod's gameplay stays off for that player.
/// In single player and on the host, the local config is used.
/// </summary>
internal static class HostConfig
{
    private static ConfigFile cfg;
    private static string messageName;
    private static string[] personal;                       // "Section.Key" or "Section.*"
    private static Dictionary<string, string> whenHostLacksMod;
    private static Func<bool> active;

    private static CustomMessagingManager.HandleNamedMessageDelegate handler; // kept alive for IL2CPP
    private static IntPtr registeredOn;
    private static float nextSend, joinedAt;
    private static string lastSent, lastApplied;
    private static bool overriding, heardFromHost, warnedMissing, savedFlag, savingPersonal;
    private static readonly Dictionary<ConfigEntryBase, string> originals = new();

    /// <summary>True on a player's game once the host's settings have arrived.</summary>
    internal static bool FromHost => overriding && heardFromHost;

    internal static void Init(BasePlugin plugin, string guid, string[] personalKeys, Dictionary<string, string> offWithoutHost, Func<bool> isActive = null)
    {
        cfg = plugin.Config;
        messageName = guid + ".HostConfig";
        personal = personalKeys ?? Array.Empty<string>();
        whenHostLacksMod = offWithoutHost ?? new Dictionary<string, string>();
        active = isActive;
        cfg.SettingChanged += OnSettingChanged;
        plugin.AddComponent<HostConfigRunner>();
    }

    private static void OnSettingChanged(object sender, SettingChangedEventArgs e)
    {
        try { if (overriding && !savingPersonal && IsPersonal(e.ChangedSetting.Definition)) SavePersonal(); } catch { }
    }

    private static string Id(ConfigDefinition d) => d.Section + "." + d.Key;

    private static bool IsPersonal(ConfigDefinition d)
    {
        foreach (var p in personal)
        {
            if (p.EndsWith(".*") ? d.Section == p.Substring(0, p.Length - 2) : Id(d) == p) return true;
        }
        return false;
    }

    internal static void Tick()
    {
        try
        {
            var nm = NetworkManager.Singleton;
            bool listening = nm != null && nm.IsListening;
            bool client = listening && !nm.IsServer && (active == null || active());
            if (!client)
            {
                if (overriding) Restore();
                if (listening && nm.IsServer) HostTick(nm);
                if (!listening) registeredOn = IntPtr.Zero;
                return;
            }
            ClientTick(nm);
        }
        catch (Exception ex) { Plugin.Log.LogError($"Host settings: {ex.Message}"); }
    }

    // ---------- host ----------

    private static void HostTick(NetworkManager nm)
    {
        if (Time.unscaledTime < nextSend || nm.CustomMessagingManager == null) return;
        nextSend = Time.unscaledTime + 1.5f;
        var parts = new List<string>();
        foreach (var kv in cfg)
        {
            if (IsPersonal(kv.Key)) continue;
            parts.Add(kv.Key.Section + "\u001f" + kv.Key.Key + "\u001f" + kv.Value.GetSerializedValue());
        }
        string payload = string.Join("\u001e", parts);
        var writer = new FastBufferWriter(payload.Length * 2 + 64, Allocator.Temp, -1);
        try
        {
            writer.WriteValueSafe(payload, false);
            nm.CustomMessagingManager.SendNamedMessageToAll(messageName, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }
        finally { writer.Dispose(); }
        if (payload != lastSent) { lastSent = payload; Plugin.Log.LogInfo("Sending this game's settings to all players."); }
    }

    // ---------- player ----------

    private static void ClientTick(NetworkManager nm)
    {
        var cmm = nm.CustomMessagingManager;
        if (cmm != null && registeredOn != cmm.Pointer)
        {
            handler ??= DelegateSupport.ConvertDelegate<CustomMessagingManager.HandleNamedMessageDelegate>(new Action<ulong, FastBufferReader>(OnReceived));
            cmm.RegisterNamedMessageHandler(messageName, handler);
            registeredOn = cmm.Pointer;
        }
        if (!overriding)
        {
            // Until the host's settings arrive (they never do if the host doesn't have the mod), the gameplay is off.
            Begin();
            joinedAt = Time.unscaledTime;
            foreach (var kv in whenHostLacksMod) Set(kv.Key, kv.Value);
        }
        if (!heardFromHost && !warnedMissing && Time.unscaledTime - joinedAt > 15f)
        {
            warnedMissing = true;
            Plugin.Log.LogWarning($"No settings from the host: the host doesn't seem to have {Plugin.Name}, so it stays off in this game.");
        }
    }

    private static void OnReceived(ulong sender, FastBufferReader reader)
    {
        try
        {
            reader.ReadValueSafe(out string payload, false);
            if (!overriding) Begin();
            heardFromHost = true;
            if (payload == lastApplied) return;
            lastApplied = payload;
            int n = 0;
            foreach (var item in payload.Split('\u001e'))
            {
                var f = item.Split('\u001f');
                if (f.Length != 3) continue;
                var def = new ConfigDefinition(f[0], f[1]);
                if (IsPersonal(def)) continue;
                if (Set(Id(def), f[2])) n++;
            }
            Plugin.Log.LogInfo($"Using the host's settings ({n} values).");
        }
        catch (Exception ex) { Plugin.Log.LogError($"Host settings (receive): {ex.Message}"); }
    }

    private static void Begin()
    {
        overriding = true;
        heardFromHost = false;
        warnedMissing = false;
        lastApplied = null;
        savedFlag = cfg.SaveOnConfigSet;
        cfg.SaveOnConfigSet = false; // never write the host's values into this player's file
    }

    private static bool Set(string id, string value)
    {
        foreach (var kv in cfg)
        {
            if (Id(kv.Key) != id) continue;
            var e = kv.Value;
            if (!originals.ContainsKey(e)) originals[e] = e.GetSerializedValue();
            if (e.GetSerializedValue() != value) e.SetSerializedValue(value);
            return true;
        }
        return false;
    }

    /// <summary>A personal setting changed while connected (a picker or a toggle key): save the file with this
    /// player's own values, not the host's.</summary>
    private static void SavePersonal()
    {
        savingPersonal = true;
        var hostValues = new Dictionary<ConfigEntryBase, string>();
        try
        {
            foreach (var kv in originals) { hostValues[kv.Key] = kv.Key.GetSerializedValue(); kv.Key.SetSerializedValue(kv.Value); }
            cfg.Save();
        }
        finally
        {
            foreach (var kv in hostValues) try { kv.Key.SetSerializedValue(kv.Value); } catch { }
            savingPersonal = false;
        }
    }

    private static void Restore()
    {
        foreach (var kv in originals)
            try { kv.Key.SetSerializedValue(kv.Value); } catch { }
        originals.Clear();
        overriding = false;
        heardFromHost = false;
        lastApplied = null;
        cfg.SaveOnConfigSet = savedFlag;
        try { cfg.Save(); } catch { } // keeps any personal setting changed while connected
        Plugin.Log.LogInfo("Left the game; back to this computer's own settings.");
    }
}

public class HostConfigRunner : MonoBehaviour
{
    public HostConfigRunner(IntPtr ptr) : base(ptr) { }
    private void Update() => HostConfig.Tick();
}
