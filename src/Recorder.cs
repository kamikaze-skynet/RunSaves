using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Netcode;
using UnityEngine;

namespace RunSaves;

/// <summary>
/// Host only: records every build change of the run, per player, as the game broadcasts it.
///
/// Every upgrade goes through one of ImprovementsManager's RPCs (stat upgrades, spell upgrades, infusions,
/// spell variants, artifacts, final upgrades). They're sent to everyone and run on the host for every player,
/// so the host sees the whole party's builds. Recorded at the execute stage, so each one is seen once.
///
/// Not recorded (the game re-applies them by itself at the start of a run):
///   - curses (from the lobby difficulty),
///   - each player's starting stats, sent when their wizard spawns (ignored for 10 s after that).
/// </summary>
[HarmonyPatch]
internal static class Recorder
{
    // Player key -> ordered build changes for this run.
    internal static readonly Dictionary<string, List<Improvement>> Log = new();
    internal static readonly Dictionary<string, SavedPlayer> Known = new();   // name/character/spells last seen
    private static readonly Dictionary<ulong, float> initTime = new();       // clientId -> when their wizard spawned
    private static readonly Dictionary<ulong, string> keyCache = new();

    internal static bool Suppress;          // replaying a save: the restorer logs its own entries
    internal static bool RunActive;         // host, between run start and run end
    private const float StartingStatsWindow = 10f;

    internal static void ResetForNewRun()
    {
        Log.Clear();
        Known.Clear();
        initTime.Clear();
        keyCache.Clear();
        RunActive = false;
    }

    private static bool Executing(NetworkBehaviour b)
    {
        try { return b.__rpc_exec_stage == NetworkBehaviour.__RpcExecStage.Execute; } catch { return false; }
    }

    private static bool ShouldRecord(NetworkBehaviour b) =>
        Plugin.Enabled.Value && !Suppress && RunActive && Game.IsServer && Executing(b);

    internal static string KeyFor(ulong clientId)
    {
        if (keyCache.TryGetValue(clientId, out var k)) return k;
        var p = Game.PlayerFor(clientId);
        if (p == null) return $"client:{clientId}";
        k = Game.KeyOf(p);
        keyCache[clientId] = k;
        Remember(p);
        return k;
    }

    internal static void Remember(GameplayPlayerIdentity p)
    {
        try
        {
            string key = Game.KeyOf(p);
            if (!Known.TryGetValue(key, out var sp)) Known[key] = sp = new SavedPlayer { Key = key };
            sp.Name = Game.NameOf(p);
            sp.Character = Game.CharacterOf(p);
            sp.WasHost = p.IsOwner;
            var spells = Game.SpellsOf(p);
            if (spells.Count > 0) sp.Spells = spells.ConvertAll(s => s.ToString());
        }
        catch { }
    }

    internal static void Add(string key, Improvement imp)
    {
        if (!Log.TryGetValue(key, out var list)) Log[key] = list = new List<Improvement>();
        list.Add(imp);
    }

    // ---------- run lifecycle (host) ----------

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.OnNetworkSpawn))]
    [HarmonyPostfix]
    private static void RunSpawned()
    {
        Game.ForgetObjects();
        ResetForNewRun();
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.OnNetworkDespawn))]
    [HarmonyPostfix]
    private static void RunDespawned()
    {
        RunActive = false;
        Restorer.Abort("the run ended");
        Game.ForgetObjects();
    }

    // A player's wizard spawned: their starting stats are about to arrive (from their own game).
    // Prefix: the RPC body resets the exec stage before running, so a postfix never sees Execute.
    [HarmonyPatch(typeof(GameplayPlayerIdentity), nameof(GameplayPlayerIdentity.InitializeOnClientsRpc))]
    [HarmonyPrefix]
    private static void PlayerInitialized(GameplayPlayerIdentity __instance)
    {
        try
        {
            if (!Game.IsServer || !Executing(__instance)) return;
            initTime[__instance.OwnerClientId] = Time.unscaledTime;
            keyCache.Remove(__instance.OwnerClientId);
        }
        catch { }
    }

    // ---------- the six kinds of build change ----------

    [HarmonyPatch(typeof(ImprovementsManager), nameof(ImprovementsManager.SendCharacterStatModifiersRpc))]
    [HarmonyPrefix]
    private static void Stats(ImprovementsManager __instance, ulong clientId, Il2CppReferenceArray<StatModifierMessage> messages)
    {
        if (!ShouldRecord(__instance) || messages == null) return;
        try
        {
            if (initTime.TryGetValue(clientId, out float t) && Time.unscaledTime - t < StartingStatsWindow) return; // starting stats
            var entries = new List<StatEntry>();
            foreach (var m in messages)
                if (m != null) entries.Add(new StatEntry { Stat = m.Stat.ToString(), Value = m.Value, Type = m.Type.ToString() });
            if (entries.Count > 0) Add(KeyFor(clientId), new Improvement { Kind = "Stats", Stats = entries });
        }
        catch (Exception ex) { Plugin.Log.LogError($"Record stats: {ex}"); }
    }

    [HarmonyPatch(typeof(ImprovementsManager), nameof(ImprovementsManager.SendSpellStatModifierRpc))]
    [HarmonyPrefix]
    private static void SpellStat(ImprovementsManager __instance, ulong clientId, SpellStatModifierMessage message)
    {
        if (!ShouldRecord(__instance) || message == null) return;
        try { Add(KeyFor(clientId), new Improvement { Kind = "SpellStat", Spell = message.Spell.ToString(), Stat = message.Stat.ToString(), Value = message.Value }); }
        catch (Exception ex) { Plugin.Log.LogError($"Record spell upgrade: {ex}"); }
    }

    [HarmonyPatch(typeof(ImprovementsManager), nameof(ImprovementsManager.SendSpellInfusionRpc))]
    [HarmonyPrefix]
    private static void Infusion(ImprovementsManager __instance, ulong clientId, SpellInfusionMessage message)
    {
        if (!ShouldRecord(__instance) || message == null) return;
        try { Add(KeyFor(clientId), new Improvement { Kind = "Infusion", Spell = message.Spell.ToString(), Element = message.Element.ToString() }); }
        catch (Exception ex) { Plugin.Log.LogError($"Record infusion: {ex}"); }
    }

    [HarmonyPatch(typeof(ImprovementsManager), nameof(ImprovementsManager.SendSpellVariantRpc))]
    [HarmonyPrefix]
    private static void Variant(ImprovementsManager __instance, ulong clientId, SpellVariantMessage message)
    {
        if (!ShouldRecord(__instance) || message == null) return;
        try { Add(KeyFor(clientId), new Improvement { Kind = "Variant", Spell = message.Spell.ToString(), Variant = message.SpellVariant.ToString() }); }
        catch (Exception ex) { Plugin.Log.LogError($"Record spell variant: {ex}"); }
    }

    [HarmonyPatch(typeof(ImprovementsManager), nameof(ImprovementsManager.SendArtifactRpc))]
    [HarmonyPrefix]
    private static void Artifact(ImprovementsManager __instance, ulong clientId, ArtifactMessage message)
    {
        if (!ShouldRecord(__instance) || message == null) return;
        try { Add(KeyFor(clientId), new Improvement { Kind = "Artifact", Artifact = message.Artifact.ToString() }); }
        catch (Exception ex) { Plugin.Log.LogError($"Record artifact: {ex}"); }
    }

    [HarmonyPatch(typeof(ImprovementsManager), nameof(ImprovementsManager.SendFinalUpgradeRpc))]
    [HarmonyPrefix]
    private static void Final(ImprovementsManager __instance, ulong clientId, FinalUpgradeType finalUpgradeType)
    {
        if (!ShouldRecord(__instance)) return;
        try { Add(KeyFor(clientId), new Improvement { Kind = "Final", Final = finalUpgradeType.ToString() }); }
        catch (Exception ex) { Plugin.Log.LogError($"Record final upgrade: {ex}"); }
    }
}
