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
    private static float offeredAt, openedAt;
    private static bool gaveUp, forceCountdown, prepared, offeringOurs;
    private static readonly List<Action> repeats = new();
    private static int echoesExpected;

    // ---------- every game ----------
    private static Pick local;                  // what this game knows about the current catch-up shop
    private static bool localSawOpen, localWaitingShown;
    private static float localSince;

    internal static bool Active => local != null;
    internal static bool Watching => local != null || hostSession || pausedFor != null;
    private static bool IsTargetHere => local != null && Game.LocalClientId() == local.Target;

    // Shared with MoreSpellSlots: only one mod opens a player-only shop at a time.
    private const string SpecialStoreFlag = "girthy_mods.SpecialStore";
    // GameObject.Find is broken in this game build (its string marshalling is missing), so the flag is shared
    // through the .NET AppDomain instead (both mods run in the same one).
    private static void SetFlag(bool on)
    {
        try { AppDomain.CurrentDomain.SetData(SpecialStoreFlag, on ? (object)Time.realtimeSinceStartup : null); } catch { }
    }

    private static bool FlagSet()
    {
        try { return AppDomain.CurrentDomain.GetData(SpecialStoreFlag) is float t && Time.realtimeSinceStartup - t < 180f; } catch { return false; } // stale after 3 min
    }

    internal static void Reset()
    {
        SetFlag(false);
        hostSession = false;
        deferred.Clear();
        prepared = false;
        if (pausedFor != null) SetPaused(null);
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
        if (queue.Count > 0) StartSession();
    }

    // ---------- the pause around a catch-up session ----------
    // Everyone's game stays paused from the first catch-up shop to the last (the shops themselves pause the game,
    // but it would run between them). The game's shop queue waits in real time, so shops still open while paused.

    private static bool hostSession;
    private static string pausedFor;            // every game: the session's player names, null when not paused
    private static bool forcedPause;

    private static void StartSession()
    {
        lastProgress = Time.unscaledTime;
        hostSession = true;
        SetFlag(true);
        var names = new List<string>();
        foreach (var p in queue) if (!names.Contains(p.Name)) names.Add(p.Name);
        string who = string.Join(", ", names);
        Plugin.Log.LogInfo($"Catch-up: game paused while {who} pick ({queue.Count} shops).");
        localSessionSince = Time.unscaledTime;
        SendRaw("pause|" + who.Replace("|", "/"));
        SetPaused(who);
    }

    private static void EndSession()
    {
        hostSession = false;
        SetFlag(false);
        Plugin.Log.LogInfo("Catch-up done; game resumed.");
        SendRaw("resume");
        SetPaused(null);
        OfferDeferred();
    }

    private static void SetPaused(string who)
    {
        pausedFor = who;
        if (who != null) Toast.Show($"Paused: {who} is catching up. The game resumes when they're done.", 6f);
        else
        {
            if (forcedPause) { Time.timeScale = 1f; forcedPause = false; }
            Toast.Show("Catch-up done. Resuming!", 3f);
        }
    }

    /// <summary>Every game, late in the frame (after the shop's own time-scale changes).</summary>
    internal static void EnforcePause()
    {
        if (hostSession) SetFlag(true); // keep MoreSpellSlots' owed-spell check waiting for the whole session
        if (pausedFor == null) return;
        if (Time.unscaledTime - localSessionSince > 900f) { SetPaused(null); return; } // safety: 15 min
        if (Time.timeScale != 0f) { Time.timeScale = 0f; forcedPause = true; }
    }

    private static float localSessionSince;

    /// <summary>Host and clients, every frame.</summary>
    internal static void Tick()
    {
        Listen();
        LocalTick();
        if (!Game.IsServer) return;

        // Never stay frozen: the host's key, or no progress for too long, ends the catch-up for everyone.
        if (hostSession)
        {
            float stuckFor = Time.unscaledTime - lastProgress;
            float limit = Math.Max(60f, Plugin.CatchUpTimeout.Value + 30f);
            bool key = UiKit.Pressed(Plugin.CatchUpCancelKey.Value);
            if (key || stuckFor > limit)
            {
                Plugin.Log.LogWarning(key ? "Catch-up: ended by the host's key." : $"Catch-up: no progress for {stuckFor:0} s; ending it so the game can go on.");
                Toast.Show("Catch-up ended. Resuming!", 4f);
                ForceEnd();
                return;
            }
        }

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
            if (open)
            {
                if (!storeSeenOpen) openedAt = Time.unscaledTime;
                storeSeenOpen = true;
                // The game starts its shop countdown as soon as it knows who chooses, then picks for whoever hasn't:
                // the new player lost their shop before they could pick. A catch-up shop waits for them instead
                // (our own time-out below still applies).
                if (!gaveUp) HoldCountdown(store);
                // Everyone else passes: the shop only waits for the player catching up.
                if (Time.unscaledTime - openedAt > 0.5f && Time.unscaledTime >= nextPass)
                {
                    nextPass = Time.unscaledTime + 1f;
                    PassOthers(store, hostCurrent.Target);
                }
                // The player can't (or won't) pick: don't keep everyone waiting forever. Mark their choice as made,
                // which closes the shop, and drop the rest of their catch-up picks.
                float limit = Plugin.CatchUpTimeout.Value;
                if (limit > 0f && Time.unscaledTime - openedAt > limit && !gaveUp)
                {
                    gaveUp = true;
                    var cur = hostCurrent;
                    Plugin.Log.LogWarning($"Catch-up: {cur.Name} didn't pick within {limit:0} s; picking for them and skipping the rest of their picks.");
                    Toast.Show($"{cur.Name} didn't pick in time; picked for them.", 6f);
                    DropTarget(cur.Target);
                    // The game's own "time's up": each player still choosing gets a card picked for them.
                    try { store.ForceChoiceForRemainingPlayers(); }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"Catch-up (auto-pick): {ex.Message}");
                        try { store.SetChoiceCompletedOnServerRpc(cur.Target); } catch { }
                    }
                }
                else if (gaveUp && Time.unscaledTime - openedAt > limit + 6f)
                {
                    // Still open: end the catch-up now rather than leave everyone frozen.
                    Plugin.Log.LogWarning("Catch-up: the shop is still open after the auto-pick; ending the catch-up.");
                    ForceEnd();
                    return;
                }
            }
            else if (storeSeenOpen || Time.unscaledTime - offeredAt > 20f) { hostCurrent = null; Announce(null); }
            return;
        }
        if (queue.Count == 0)
        {
            if (hostSession && !Game.StoreBusy) EndSession();
            return;
        }
        if (Game.StoreBusy) return;

        var next = queue.Dequeue();
        if (Game.PlayerFor(next.Target) == null) { Plugin.Log.LogInfo($"Catch-up: {next.Name} left; skipping their picks."); DropTarget(next.Target); return; }
        hostCurrent = next;
        lastProgress = Time.unscaledTime;
        storeSeenOpen = false;
        gaveUp = false;
        offeredAt = Time.unscaledTime;
        Announce(next);
        prepared = false;
        offeringOurs = true;
        try { store.OfferStoreToAllPlayers(next.Kind == Kind.Artifact ? StoreType.Artifact : StoreType.LevelUp); }
        catch (Exception ex) { Plugin.Log.LogError($"Catch-up shop: {ex}"); hostCurrent = null; Announce(null); }
        finally { offeringOurs = false; }
    }

    private static float lastProgress, nextPass;

    internal static void HoldCountdown(StoreManager store)
    {
        try
        {
            if (store.isCountdownTimerActive) store.isCountdownTimerActive = false;
            if (store.countdownTimer < 30f) store.countdownTimer = 30f;
        }
        catch { }
    }

    /// <summary>Counts every player except <paramref name="target"/> as having already picked in the open shop.</summary>
    internal static void PassOthers(StoreManager store, ulong target)
    {
        try
        {
            var made = store.playersWhoHaveMadeAChoice;
            int passed = 0;
            foreach (var p in Game.Players())
            {
                ulong id = p.OwnerClientId;
                if (id == target || (made != null && made.Contains(id))) continue;
                store.SetChoiceCompletedOnServerRpc(id);
                passed++;
            }
            if (passed > 0) Plugin.Log.LogInfo($"Catch-up: {passed} other player(s) pass; the shop waits only for the player catching up.");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Catch-up (pass others): {ex.Message}"); }
    }

    /// <summary>Ends the catch-up now: no more catch-up shops, any open shop is closed (everyone's choice counts as
    /// made, then the game's own countdown), and everyone's game resumes.</summary>
    private static void ForceEnd()
    {
        queue.Clear();
        repeats.Clear();
        var store = Game.Store;
        hostCurrent = null;
        prepared = false;
        Announce(null);
        if (store != null && Game.StoreBusy)
        {
            foreach (var p in Game.Players())
                try { store.SetChoiceCompletedOnServerRpc(p.OwnerClientId); } catch { }
            forceCountdown = true;
            try { store.StartCountdown(); } catch { }
            finally { forceCountdown = false; }
        }
        if (hostSession) EndSession();
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
        if (prepared)
        {
            // Another shop is starting, so ours is over: everyone picks in this one.
            Plugin.Log.LogInfo("Catch-up: a normal shop came up after ours; everyone picks in it.");
            hostCurrent = null;
            prepared = false;
            Announce(null);
            return;
        }
        prepared = true;
        try { __instance.SetChoosingPlayersRpc(new Il2CppStructArray<ulong>(new[] { c.Target })); }
        catch (Exception ex) { Plugin.Log.LogError($"Catch-up choosers: {ex.Message}"); }
    }

    // Our shop closed (the game's exit, on the host).
    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.ExitStoreRpc))]
    [HarmonyPostfix]
    private static void ExitStore_Postfix(StoreManager __instance)
    {
        try
        {
            if (!Game.IsServer || hostCurrent == null || !prepared) return;
            if (__instance.__rpc_exec_stage != Unity.Netcode.NetworkBehaviour.__RpcExecStage.Execute) return;
            lastProgress = Time.unscaledTime;
            hostCurrent = null;
            prepared = false;
            Announce(null);
        }
        catch { }
    }

    // While a catch-up session runs, the game's own shops (a level-up that happened just before the pause) wait
    // until it's over, so shops never interleave with the catch-up ones.
    private static readonly List<StoreType> deferred = new();

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.OfferStoreToAllPlayers))]
    [HarmonyPrefix]
    private static bool Offer_Prefix(StoreType storeType)
    {
        if (offeringOurs || !hostSession || !Game.IsServer) return true;
        deferred.Add(storeType);
        Plugin.Log.LogInfo($"Catch-up: the game's {storeType} shop waits until the catch-up is done.");
        return false;
    }

    private static void OfferDeferred()
    {
        if (deferred.Count == 0) return;
        var list = new List<StoreType>(deferred);
        deferred.Clear();
        var store = Game.Store;
        if (store == null) return;
        foreach (var t in list) try { store.OfferStoreToAllPlayers(t); } catch (Exception ex) { Plugin.Log.LogError($"Catch-up (deferred shop): {ex.Message}"); }
    }

    // No countdown or kick for a catch-up shop (the new player may take their time).
    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.StartCountdown))]
    [HarmonyPrefix]
    private static bool StartCountdown_Prefix() => forceCountdown || (hostCurrent == null && local == null && pausedFor == null);

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.KickPlayer))]
    [HarmonyPrefix]
    private static bool Kick_Prefix() => hostCurrent == null && local == null && pausedFor == null;

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
        if (open && !Game.IsServer) HoldCountdown(store); // no countdown on screen for a catch-up shop
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

    private static void SendRaw(string payload)
    {
        try
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;
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
            if (payload == "resume") { SetPaused(null); return; }
            if (payload.StartsWith("pause|")) { localSessionSince = Time.unscaledTime; SetPaused(payload.Substring(6)); return; }
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
