using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace RunSaves;

/// <summary>
/// Host -> client messages used during a restore (clients need the mod for these):
///   Spells: "re-equip these spells". Equipped spells can only be changed by the player's own game.
///   Time:   the restored run clock, so the client's progress bar matches the host.
/// </summary>
[HarmonyPatch]
internal static class Net
{
    private const string SpellsMsg = "girthy_mods.RunSaves.Spells";
    private const string TimeMsg = "girthy_mods.RunSaves.Time";
    private static CustomMessagingManager.HandleNamedMessageDelegate spellsHandler, timeHandler; // kept alive for IL2CPP

    // Client side: spells waiting for the local wizard to be ready.
    internal static List<SpellType> PendingSpells;
    internal static float PendingUntil;

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.OnNetworkSpawn))]
    [HarmonyPostfix]
    private static void Listen()
    {
        try
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer || nm.CustomMessagingManager == null) return;
            spellsHandler ??= DelegateSupport.ConvertDelegate<CustomMessagingManager.HandleNamedMessageDelegate>(new Action<ulong, FastBufferReader>(OnSpells));
            timeHandler ??= DelegateSupport.ConvertDelegate<CustomMessagingManager.HandleNamedMessageDelegate>(new Action<ulong, FastBufferReader>(OnTime));
            nm.CustomMessagingManager.RegisterNamedMessageHandler(SpellsMsg, spellsHandler);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(TimeMsg, timeHandler);
        }
        catch (Exception ex) { Plugin.Log.LogError($"Listen: {ex}"); }
    }

    // Same delivery as the game's RPCs, so these arrive in order with them (e.g. after the run-start RPC).
    private static void Send(string name, string payload, ulong? client)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;
        var writer = new FastBufferWriter(Encoding.UTF8.GetByteCount(payload) * 2 + 64, Allocator.Temp, -1);
        try
        {
            writer.WriteValueSafe(payload, false);
            if (client.HasValue) nm.CustomMessagingManager.SendNamedMessage(name, client.Value, writer, NetworkDelivery.ReliableFragmentedSequenced);
            else nm.CustomMessagingManager.SendNamedMessageToAll(name, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }
        finally { writer.Dispose(); }
    }

    internal static void SendSpells(ulong client, List<SpellType> spells) =>
        Send(SpellsMsg, string.Join(",", spells.ConvertAll(s => s.ToString())), client);

    internal static void SendTime(float cycleTime, float runTime) =>
        Send(TimeMsg, cycleTime.ToString(CultureInfo.InvariantCulture) + ";" + runTime.ToString(CultureInfo.InvariantCulture), null);

    private static void OnSpells(ulong sender, FastBufferReader reader)
    {
        try
        {
            reader.ReadValueSafe(out string payload, false);
            var spells = new List<SpellType>();
            foreach (var s in payload.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (Enum.TryParse(s, out SpellType t)) spells.Add(t);
            PendingSpells = spells;
            PendingUntil = Time.unscaledTime + 15f;
            Plugin.Log.LogInfo($"Host restored run: re-equipping {payload}.");
            TryEquipPending();
        }
        catch (Exception ex) { Plugin.Log.LogError($"Receive spells: {ex}"); }
    }

    /// <summary>Client: equip the spells the host asked for, once the local wizard exists. Called every frame.</summary>
    internal static void TryEquipPending()
    {
        if (PendingSpells == null) return;
        if (Time.unscaledTime > PendingUntil)
        {
            Plugin.Log.LogWarning("Couldn't re-equip restored spells: your wizard wasn't ready in time.");
            PendingSpells = null;
            return;
        }
        var me = Game.LocalPlayer();
        var eq = me?.spellEquipper;
        if (eq == null || Game.SpellsOf(me).Count == 0) return; // not ready yet
        EquipMissing(me, PendingSpells);
        PendingSpells = null;
    }

    internal static int EquipMissing(GameplayPlayerIdentity p, List<SpellType> spells)
    {
        var have = Game.SpellsOf(p);
        int added = 0;
        foreach (var s in spells)
        {
            if (have.Contains(s)) continue;
            p.spellEquipper.EquipSpell(s);
            have.Add(s);
            added++;
        }
        return added;
    }

    private static void OnTime(ulong sender, FastBufferReader reader)
    {
        try
        {
            reader.ReadValueSafe(out string payload, false);
            var parts = payload.Split(';');
            var rm = Game.Run;
            if (rm == null || parts.Length < 2) return;
            float cycleTime = float.Parse(parts[0], CultureInfo.InvariantCulture);
            rm.currentCycleTimeInSeconds = cycleTime;
            rm.currentRunTimeInSeconds = float.Parse(parts[1], CultureInfo.InvariantCulture);

            // Same as the host: elites from before this point count as already spawned.
            var spawned = rm.eliteEnemiesThatHaveBeenSpawned;
            var elites = rm.Configuration?.EliteEnemies;
            if (spawned != null && elites != null)
                for (int i = 0; i < elites.Count; i++)
                {
                    var e = elites[i];
                    if (e != null && e.SpawnTimeInSeconds() < cycleTime && !spawned.Contains(e)) spawned.Add(e);
                }
        }
        catch (Exception ex) { Plugin.Log.LogError($"Receive time: {ex}"); }
    }
}
