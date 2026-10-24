using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace RunSaves;

/// <summary>
/// Catch-up picks for players who join a restored run without a build in the save.
///
/// The party level is shared, so a new player is already at the party's level; what they lack is the build. After
/// the restore, the host opens the game's own shops for them, one after another, in which only they pick:
///   - spell shops until they have as many spells as the others,
///   - artifact shops (about as many artifacts as the others have),
///   - upgrade shops: up to CatchUpPicks of them, each pick counting as several level-ups (it's applied that many
///     times) so the total matches the levels they missed.
///
/// How: the host announces each catch-up shop (named message), opens it with StoreManager.OfferStoreToAllPlayers,
/// and narrows its "players still choosing" list to the new player (PrepareChoosingPlayers postfix). Everyone else's
/// shop shows "waiting for other players" and their picks/rerolls are blocked; the countdown and kick are off for
/// catch-up shops. On the new player's game, a spell/upgrade shop is forced by answering the spell-shop check.
/// The host re-sends each upgrade the new player picks (the same RPC) to multiply it.
/// </summary>
[HarmonyPatch]
internal static class CatchUp
{
    private const string MessageName = "girthy_mods.RunSaves.CatchUp";
    private static CustomMessagingManager.HandleNamedMessageDelegate handler; // kept alive for IL2CPP
    private static IntPtr registeredOn;

    private enum Kind { Spell, Artifact, Upgrade }

    private sealed class Pick
    {
        public ulong Target;
        public string Name;
        public Kind Kind;
        public int Index, Count, Multiplier;
    }

    // ---------- host ----------
    private static readonly Queue<Pick> queue = new();
    private static Pick hostCurrent;            // the catch-up shop that's open now
    private static bool storeSeenOpen;
    private static float offeredAt;
    private static readonly List<Action> repeats = new();
    private static int echoesExpected;

    // ---------- every game ----------
    private static Pick local;                  // what this game knows about the current catch-up shop
    private static bool localSawOpen, localWaitingShown;
    private static float localSince;

    internal static bool Active => local != null;
    private static bool IsTargetHere => local != null && Game.LocalClientId() == local.Target;

    internal static void Reset()
    {
        queue.Clear();
        hostCurrent = null;
        repeats.Clear();
        echoesExpected = 0;
        local = null;
    }

    /// <summary>Host, at the end of a restore: plan catch-up picks for players the save had no build for.</summary>
    internal static void Plan(List<GameplayPlayerIdentity> fresh, List<GameplayPlayerIdentity> restored, Dictionary<string, List<Improvement>> builds)
    {
        if (!Plugin.CatchUpEnabled.Value || fresh.Count == 0 || !Game.IsServer) return;
        var party = Game.Party;
        if (party == null) return;
        int level = party.level;

        int targetSpells = 1, artifacts = 0, counted = 0;
        foreach (var p in restored)
        {
            targetSpells = Math.Max(targetSpells, Game.SpellsOf(p).Count);
            if (builds.TryGetValue(Game.KeyOf(p), out var log)) { artifacts += log.FindAll(i => i.Kind == "Artifact").Count; counted++; }
        }
        int artifactPicks = counted > 0 && Plugin.CatchUpArtifacts.Value ? (int)Math.Round((double)artifacts / counted) : 0;

        foreach (var p in fresh)
        {
            if (!Game.Alive(p)) continue;
            string name = Game.NameOf(p);
            int spells = Math.Max(0, targetSpells - Game.SpellsOf(p).Count);
            int upgradeLevels = Math.Max(0, level - 1 - (targetSpells - 1));
            int picks = Math.Min(upgradeLevels, Math.Max(1, Plugin.CatchUpPicks.Value));
            int mult = picks > 0 ? (int)Math.Ceiling((double)upgradeLevels / picks) : 1;
            int total = spells + artifactPicks + picks;
            if (total == 0) continue;

            int i = 0;
            for (int s = 0; s < spells; s++) queue.Enqueue(new Pick { Target = p.OwnerClientId, Name = name, Kind = Kind.Spell, Index = ++i, Count = total, Multiplier = 1 });
            for (int a = 0; a < artifactPicks; a++) queue.Enqueue(new Pick { Target = p.OwnerClientId, Name = name, Kind = Kind.Artifact, Index = ++i, Count = total, Multiplier = 1 });
            for (int u = 0; u < picks; u++) queue.Enqueue(new Pick { Target = p.OwnerClientId, Name = name, Kind = Kind.Upgrade, Index = ++i, Count = total, Multiplier = mult });
            Plugin.Log.LogInfo($"Catch-up for {name}: {spells} spells, {artifactPicks} artifacts, {picks} upgrades x{mult} (party level {level}).");
            Toast.Show($"{name} gets {total} catch-up picks to match the party.", 6f);
        }
    }

    /// <summary>Host and clients, every frame.</summary>
    internal static void Tick()
    {
        Listen();
        LocalTick();
        if (!Game.IsServer) return;

        // Re-send the new player's last upgrade (one per frame).
        if (repeats.Count > 0)
        {
            var a = repeats[0];
            repeats.RemoveAt(0);
            try { echoesExpected++; a(); } catch (Exception ex) { echoesExpected = Math.Max(0, echoesExpected - 1); Plugin.Log.LogError($"Catch-up repeat: {ex.Message}"); }
            return;
        }

        var store = Game.Store;
        if (store == null) return;
        if (hostCurrent != null)
        {
            bool open = Game.StoreBusy;
            if (open) storeSeenOpen = true;
            else if (storeSeenOpen || Time.unscaledTime - offeredAt > 20f) { hostCurrent = null; Announce(null); }
            return;
        }
        if (queue.Count == 0 || Game.StoreBusy || Time.timeScale <= 0.01f) return;

        var next = queue.Dequeue();
        if (Game.PlayerFor(next.Target) == null) { Plugin.Log.LogInfo($"Catch-up: {next.Name} left; skipping their picks."); DropTarget(next.Target); return; }
        hostCurrent = next;
        storeSeenOpen = false;
        offeredAt = Time.unscaledTime;
        Announce(next);
        try { store.OfferStoreToAllPlayers(next.Kind == Kind.Artifact ? StoreType.Artifact : StoreType.LevelUp); }
        catch (Exception ex) { Plugin.Log.LogError($"Catch-up shop: {ex}"); hostCurrent = null; Announce(null); }
    }

    private static void DropTarget(ulong target)
    {
        var keep = new List<Pick>(queue);
        queue.Clear();
        foreach (var p in keep) if (p.Target != target) queue.Enqueue(p);
    }

    // The game narrows "who still has to choose" to the new player.
    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.PrepareChoosingPlayers))]
    [HarmonyPostfix]
    private static void PrepareChoosing_Postfix(StoreManager __instance)
    {
        var c = hostCurrent;
        if (c == null || !Game.IsServer) return;
        try { __instance.SetChoosingPlayersRpc(new Il2CppStructArray<ulong>(new[] { c.Target })); }
        catch (Exception ex) { Plugin.Log.LogError($"Catch-up choosers: {ex.Message}"); }
    }

    // No countdown or kick for a catch-up shop (the new player may take their time).
    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.StartCountdown))]
    [HarmonyPrefix]
    private static bool StartCountdown_Prefix() => hostCurrent == null && local == null;

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.KickPlayer))]
    [HarmonyPrefix]
    private static bool Kick_Prefix() => hostCurrent == null && local == null;

    /// <summary>Host: the new player picked an upgrade; apply it (Multiplier - 1) more times.</summary>
    internal static void OnUpgradePicked(ulong clientId, Action resend)
    {
        if (echoesExpected > 0) { echoesExpected--; return; } // one of our own re-sends coming back
        var c = hostCurrent;
        if (c == null || c.Kind != Kind.Upgrade || c.Target != clientId || c.Multiplier <= 1) return;
        for (int i = 1; i < c.Multiplier; i++) repeats.Add(resend);
    }

    // ---------- every game: blocking picks, forcing the shop kind ----------

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.ChooseShopItem))]
    [HarmonyPrefix]
    private static bool Choose_Prefix()
    {
        if (local == null || IsTargetHere) return true;
        Toast.Show($"{local.Name} is catching up ({local.Index}/{local.Count}); this shop is theirs.", 3f);
        return false;
    }

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.Reroll))]
    [HarmonyPrefix]
    private static bool Reroll_Prefix() => local == null || IsTargetHere;

    // On the new player's game: the shop is a spell shop or an upgrade shop as planned (the game decides by asking
    // whether this party level gives a spell). Last, so it wins over other mods' answers.
    [HarmonyPatch(typeof(SpellStoreAvailabilityChecker), nameof(SpellStoreAvailabilityChecker.SpellStoreShouldBeOffered))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void SpellShop_Postfix(ref bool __result)
    {
        if (local == null || !IsTargetHere || local.Kind == Kind.Artifact) return;
        __result = local.Kind == Kind.Spell;
    }

    private static void LocalTick()
    {
        if (local == null) return;
        if (Time.unscaledTime - localSince > 300f) { local = null; return; } // safety
        var store = Game.Store;
        if (store == null) return;
        bool open = false;
        try { open = store.IsShopPanelActive; } catch { }
        if (open && !localSawOpen)
        {
            localSawOpen = true;
            if (IsTargetHere)
                Toast.Show(local.Kind == Kind.Upgrade && local.Multiplier > 1
                    ? $"Catch-up pick {local.Index}/{local.Count}: each upgrade counts {local.Multiplier} times."
                    : $"Catch-up pick {local.Index}/{local.Count}: pick a {(local.Kind == Kind.Spell ? "spell" : local.Kind == Kind.Artifact ? "artifact" : "upgrade")}.", 4f);
        }
        if (open && !IsTargetHere && !localWaitingShown)
        {
            localWaitingShown = true;
            try { store.storePanel?.SetAsWaitingForOtherPlayers(); } catch { }
            Toast.Show($"{local.Name} is catching up ({local.Index}/{local.Count})...", 3f);
        }
    }

    // ---------- messages ----------

    private static void Listen()
    {
        try
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || nm.IsServer || nm.CustomMessagingManager == null) { if (nm == null || !nm.IsListening) registeredOn = IntPtr.Zero; return; }
            var cmm = nm.CustomMessagingManager;
            if (registeredOn == cmm.Pointer) return;
            handler ??= DelegateSupport.ConvertDelegate<CustomMessagingManager.HandleNamedMessageDelegate>(new Action<ulong, FastBufferReader>(OnMessage));
            cmm.RegisterNamedMessageHandler(MessageName, handler);
            registeredOn = cmm.Pointer;
        }
        catch { }
    }

    private static void Announce(Pick p)
    {
        SetLocal(p);
        try
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;
            var c = CultureInfo.InvariantCulture;
            string payload = p == null ? "end" : string.Join("|", p.Target.ToString(c), p.Kind.ToString(), p.Index.ToString(c), p.Count.ToString(c), p.Multiplier.ToString(c), p.Name.Replace("|", "/"));
            var writer = new FastBufferWriter(payload.Length * 2 + 64, Allocator.Temp, -1);
            try
            {
                writer.WriteValueSafe(payload, false);
                nm.CustomMessagingManager.SendNamedMessageToAll(MessageName, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
            finally { writer.Dispose(); }
        }
        catch (Exception ex) { Plugin.Log.LogError($"Catch-up message: {ex.Message}"); }
    }

    private static void OnMessage(ulong sender, FastBufferReader reader)
    {
        try
        {
            reader.ReadValueSafe(out string payload, false);
            if (payload == "end") { SetLocal(null); return; }
            var f = payload.Split('|');
            if (f.Length < 6) return;
            var c = CultureInfo.InvariantCulture;
            SetLocal(new Pick
            {
                Target = ulong.Parse(f[0], c), Kind = Enum.TryParse(f[1], out Kind k) ? k : Kind.Upgrade,
                Index = int.Parse(f[2], c), Count = int.Parse(f[3], c), Multiplier = int.Parse(f[4], c), Name = f[5],
            });
        }
        catch (Exception ex) { Plugin.Log.LogError($"Catch-up message (receive): {ex.Message}"); }
    }

    private static void SetLocal(Pick p)
    {
        local = p;
        localSawOpen = localWaitingShown = false;
        localSince = Time.unscaledTime;
    }
}
