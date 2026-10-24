using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Netcode;
using UnityEngine;

namespace RunSaves;

/// <summary>
/// Restoring a saved run (host). The host presses Resume in the lobby, then starts a run in the saved world:
///
///  1. Run start: jump the wave loop to the start of the saved wave (the game's own "next wave" step does the
///     rest), set the run clock to match, and mark elites from before that point as already spawned.
///  2. Once every wizard has spawned: match players to the save (Steam/Epic id, else same wizard) and
///     re-equip their spells. Each player's own game does this (the host asks clients over a message).
///  3. Once the spells are in: replay every recorded upgrade through the game's own upgrade messages,
///     so each player's build is rebuilt exactly as the game built it the first time.
///  4. Party level, XP and revives.
/// Enemies, objectives in progress and pickups aren't saved: the saved wave starts fresh.
/// </summary>
[HarmonyPatch]
internal static class Restorer
{
    internal static RunSave Armed;        // chosen in the lobby, waiting for a run to start
    internal static RunSave LastRestored; // the save this run was restored from (its setup is carried into new saves)
    private static RunSave active;        // being restored now
    private static readonly List<GameplayPlayerIdentity> fresh = new(); // players the save has no build for
    internal static bool Busy => active != null;

    private enum Phase { None, WaitPlayers, WaitSpells, Settle, Replay, Finish }
    private static Phase phase;
    private static float t0, phaseStart;
    private static bool wavesJumped, startedThisRun;

    private class Match { public GameplayPlayerIdentity Player; public SavedPlayer Saved; public List<SpellType> Spells = new(); }
    private static readonly List<Match> matches = new();
    private static readonly Queue<(Match m, Improvement imp)> queue = new();
    private static int applied, skipped;
    private static readonly List<string> notes = new();

    internal static void Abort(string why)
    {
        if (active == null) return;
        Plugin.Log.LogWarning($"Restore stopped: {why}.");
        Recorder.Suppress = false;
        // Keep what wasn't replayed in this run's record, so the next save doesn't lose it.
        while (queue.Count > 0) { var (m, imp) = queue.Dequeue(); Recorder.Add(m.Saved.Key, imp); }
        active = null;
        phase = Phase.None;
    }

    // ---------- 1. run start ----------

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.OnNetworkSpawn))]
    [HarmonyPostfix]
    private static void RunSpawned()
    {
        startedThisRun = false;
        wavesJumped = false;
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.StartRunOnServerRpc))]
    [HarmonyPrefix]
    private static void StartRun_Prefix(RunManager __instance, out bool __state)
    {
        __state = false;
        try
        {
            __state = __instance.__rpc_exec_stage == NetworkBehaviour.__RpcExecStage.Execute && Game.IsServer;
            if (!__state || startedThisRun) return; // later loops of an endless run use the same RPC
            if (Armed == null || !Plugin.Enabled.Value) return;

            var save = Armed;
            Armed = null;
            string here = Game.ConfigName(__instance);
            if (!string.IsNullOrEmpty(save.ConfigName) && here != save.ConfigName)
            {
                string msg = $"Saved run is in {save.World}, but this run is in {Game.WorldName(__instance)}. Not restored (the save is kept).";
                Plugin.Log.LogWarning(msg);
                Toast.Show(msg, 8f);
                return;
            }

            try
            {
                var host = Game.LocalPlayer();
                if (host != null && host.ActiveCurseLevel != save.CurseLevel)
                    Toast.Show($"Heads up: the saved run used curse level {save.CurseLevel}, this one uses {host.ActiveCurseLevel}.", 6f);
            }
            catch { }

            active = save;
            LastRestored = save;
            wavesJumped = false;
            if (save.Cycle > 1 && __instance.CycleNumber != null && __instance.CycleNumber.Value < save.Cycle)
                __instance.CycleNumber.Value = save.Cycle;
            Plugin.Log.LogInfo($"Restoring saved run: {save.World}, cycle {save.Cycle}, wave {save.Wave}.");
        }
        catch (Exception ex) { Plugin.Log.LogError($"Restore (start): {ex}"); active = null; }
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.StartRunOnServerRpc))]
    [HarmonyPostfix]
    private static void StartRun_Postfix(RunManager __instance, bool __state)
    {
        if (!__state || startedThisRun) return;
        startedThisRun = true;
        Recorder.RunActive = true;
        Saver.OnRunStarted(active != null);
        if (active == null) return;
        try
        {
            if (wavesJumped) Net.SendTime(__instance.currentCycleTimeInSeconds, __instance.currentRunTimeInSeconds);
            SetLevelAndRevives(active); // now, not at the end: at level 1 the first kills would hand out free level-ups
            t0 = Time.unscaledTime;
            SetPhase(Phase.WaitPlayers);
            Toast.Show($"Restoring saved run - wave {active.Wave}...", 4f);
        }
        catch (Exception ex) { Plugin.Log.LogError($"Restore (after start): {ex}"); }
    }

    // The wave loop's first step. Vanilla: wave 1, then "next wave" every RunDuration / waveCount seconds.
    // Set it up as if wave (saved - 1) just finished, so the game's own "next wave" step starts the saved wave.
    [HarmonyPatch(typeof(RunManager._StartWaves_d__53), nameof(RunManager._StartWaves_d__53.MoveNext))]
    [HarmonyPrefix]
    private static void Waves_Prefix(RunManager._StartWaves_d__53 __instance)
    {
        if (active == null || wavesJumped) return;
        try
        {
            if (__instance.__1__state != 0) return;
            var rm = __instance.__4__this;
            var cfg = rm?.Configuration;
            var waves = cfg?.Waves;
            if (waves == null || waves.Count == 0) return;
            wavesJumped = true;

            int count = waves.Count;
            int wave = Math.Clamp(active.Wave, 1, count);
            float between = cfg.RunDurationInSeconds / count;

            __instance._waves_5__2 = waves;
            __instance._timeBetweenWavesInSeconds_5__3 = between;
            __instance._index_5__4 = wave - 2;
            rm.CurrentWave.Value = wave - 1;
            __instance.__1__state = 1; // continue as "the previous wave just ended"

            float cycleTime = (wave - 1) * between;
            rm.currentCycleTimeInSeconds = cycleTime;
            rm.currentRunTimeInSeconds = Math.Max(cycleTime, active.RunTime - active.CycleTime + cycleTime);

            // Elites from before this point count as already spawned (otherwise they'd all spawn at once).
            int marked = 0;
            var spawned = rm.eliteEnemiesThatHaveBeenSpawned;
            var elites = cfg.EliteEnemies;
            if (spawned != null && elites != null)
                for (int i = 0; i < elites.Count; i++)
                {
                    var e = elites[i];
                    if (e != null && e.SpawnTimeInSeconds() < cycleTime && !spawned.Contains(e)) { spawned.Add(e); marked++; }
                }
            Plugin.Log.LogInfo($"Restore: starting at wave {wave}/{count} ({cycleTime:0}s into the run), {marked} earlier elites skipped.");
        }
        catch (Exception ex) { Plugin.Log.LogError($"Restore (waves): {ex}"); }
    }

    // ---------- 2-4. players, spells, upgrades, level ----------

    private static void SetPhase(Phase p) { phase = p; phaseStart = Time.unscaledTime; }

    /// <summary>Called every frame by the plugin's component.</summary>
    internal static void Tick()
    {
        if (active == null || phase == Phase.None) return;
        try
        {
            if (!Game.IsServer || Game.Run == null) { Abort("not hosting a run any more"); return; }
            float now = Time.unscaledTime;
            switch (phase)
            {
                case Phase.WaitPlayers:
                {
                    var players = Game.Players();
                    bool ready = players.Count > 0 && players.TrueForAll(p => Game.SpellsOf(p).Count > 0);
                    if ((ready && now - t0 >= 1.5f) || now - t0 > 15f)
                    {
                        MatchPlayers(players);
                        EquipSpells();
                        SetPhase(Phase.WaitSpells);
                    }
                    break;
                }
                case Phase.WaitSpells:
                {
                    bool done = matches.TrueForAll(m => m.Spells.TrueForAll(s => Game.SpellsOf(m.Player).Contains(s)));
                    if ((done && now - t0 >= 4f) || now - phaseStart > 10f) SetPhase(Phase.Settle);
                    break;
                }
                case Phase.Settle: // let every client receive the spell lists before upgrades for those spells arrive
                    if (now - phaseStart >= 1.5f) { BuildQueue(); SetPhase(Phase.Replay); }
                    break;
                case Phase.Replay:
                    for (int i = 0; i < 10 && queue.Count > 0; i++) Apply(queue.Dequeue());
                    if (queue.Count == 0) SetPhase(Phase.Finish);
                    break;
                case Phase.Finish:
                    FinishUp();
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Restore: {ex}");
            Abort("an error (see above)");
        }
    }

    private static void MatchPlayers(List<GameplayPlayerIdentity> players)
    {
        matches.Clear();
        notes.Clear();
        fresh.Clear();
        var unused = new List<SavedPlayer>(active.Players);
        var waiting = new List<GameplayPlayerIdentity>();

        foreach (var p in players)
        {
            string key = Game.KeyOf(p);
            var sp = unused.Find(s => s.Key == key);
            if (sp != null && !string.IsNullOrEmpty(sp.Character) && sp.Character != Game.CharacterOf(p))
            {
                // Same person, different wizard: the saved spells wouldn't fit. Try the other players' builds.
                notes.Add($"{Game.NameOf(p)} picked {RunSave.Pretty(Game.CharacterOf(p))} instead of {RunSave.Pretty(sp.Character)}");
                sp = null;
            }
            if (sp != null) { unused.Remove(sp); matches.Add(new Match { Player = p, Saved = sp }); }
            else waiting.Add(p);
        }
        foreach (var p in waiting) // someone on another account: match by wizard
        {
            string ch = Game.CharacterOf(p);
            var sp = unused.Find(s => s.Character == ch);
            if (sp != null)
            {
                unused.Remove(sp);
                matches.Add(new Match { Player = p, Saved = sp });
                notes.Add($"{Game.NameOf(p)} gets {sp.Name}'s build (same wizard)");
            }
            else { notes.Add($"{Game.NameOf(p)} isn't in the save (gets catch-up picks)"); fresh.Add(p); }
        }
        foreach (var sp in unused)
        {
            notes.Add($"{sp.Name} isn't here (build kept in the save)");
            // Carry their build into this run's record so later saves still have it.
            if (!Recorder.Log.ContainsKey(sp.Key)) Recorder.Log[sp.Key] = new List<Improvement>(sp.Improvements);
            if (!Recorder.Known.ContainsKey(sp.Key)) Recorder.Known[sp.Key] = sp;
        }

        foreach (var m in matches)
            foreach (var s in m.Saved.Spells)
                if (Enum.TryParse(s, out SpellType t) && !m.Spells.Contains(t)) m.Spells.Add(t);

        foreach (var n in notes) Plugin.Log.LogInfo("Restore: " + n);
    }

    private static void EquipSpells()
    {
        foreach (var m in matches)
        {
            if (m.Spells.Count == 0) continue;
            if (m.Player.IsOwner)
            {
                int added = Net.EquipMissing(m.Player, m.Spells);
                Plugin.Log.LogInfo($"Restore: equipped {added} spells for {Game.NameOf(m.Player)}.");
            }
            else
            {
                Net.SendSpells(m.Player.OwnerClientId, m.Spells);
                Plugin.Log.LogInfo($"Restore: asked {Game.NameOf(m.Player)} to re-equip {m.Spells.Count} spells.");
            }
        }
    }

    private static void BuildQueue()
    {
        queue.Clear();
        applied = skipped = 0;
        foreach (var m in matches)
        {
            var have = Game.SpellsOf(m.Player);
            int missing = 0;
            foreach (var imp in m.Saved.Improvements)
            {
                // Upgrades for a spell the player doesn't have would fail on every game: skip them.
                if (!string.IsNullOrEmpty(imp.Spell) && (!Enum.TryParse(imp.Spell, out SpellType s) || !have.Contains(s))) { missing++; skipped++; continue; }
                queue.Enqueue((m, imp));
            }
            if (missing > 0)
                notes.Add($"{Game.NameOf(m.Player)}: {missing} spell upgrades skipped (spells not re-equipped - do they have RunSaves?)");
        }
        Plugin.Log.LogInfo($"Restore: replaying {queue.Count} upgrades.");
    }

    private static void Apply((Match m, Improvement imp) item)
    {
        var im = Game.Improvements;
        if (im == null) { skipped++; return; }
        var p = item.m.Player;
        if (p is null || p.WasCollected) { skipped++; return; }
        ulong client = p.OwnerClientId;
        var imp = item.imp;

        Recorder.Suppress = true;
        try
        {
            switch (imp.Kind)
            {
                case "Stats":
                {
                    var list = new List<StatModifierMessage>();
                    foreach (var e in imp.Stats ?? new List<StatEntry>())
                        if (Enum.TryParse(e.Stat, out StatType st) && Enum.TryParse(e.Type, out StatModifiersType ty))
                            list.Add(new StatModifierMessage { Stat = st, Value = e.Value, Type = ty });
                    if (list.Count == 0) { skipped++; return; }
                    var arr = new Il2CppReferenceArray<StatModifierMessage>(list.Count);
                    for (int i = 0; i < list.Count; i++) arr[i] = list[i];
                    im.SendCharacterStatModifiersRpc(client, arr);
                    break;
                }
                case "SpellStat":
                    if (!Enum.TryParse(imp.Spell, out SpellType sp1) || !Enum.TryParse(imp.Stat, out StatType st1)) { skipped++; return; }
                    im.SendSpellStatModifierRpc(client, new SpellStatModifierMessage { Spell = sp1, Stat = st1, Value = imp.Value });
                    break;
                case "Infusion":
                    if (!Enum.TryParse(imp.Spell, out SpellType sp2) || !Enum.TryParse(imp.Element, out ElementType el)) { skipped++; return; }
                    im.SendSpellInfusionRpc(client, new SpellInfusionMessage { Spell = sp2, Element = el });
                    break;
                case "Variant":
                    if (!Enum.TryParse(imp.Spell, out SpellType sp3) || !Enum.TryParse(imp.Variant, out SpellVariant va)) { skipped++; return; }
                    im.SendSpellVariantRpc(client, new SpellVariantMessage { Spell = sp3, SpellVariant = va });
                    break;
                case "Artifact":
                    if (!Enum.TryParse(imp.Artifact, out ArtifactType ar)) { skipped++; return; }
                    im.SendArtifactRpc(client, new ArtifactMessage { Artifact = ar });
                    break;
                case "Final":
                    if (!Enum.TryParse(imp.Final, out FinalUpgradeType fu)) { skipped++; return; }
                    im.SendFinalUpgradeRpc(client, fu);
                    break;
                default:
                    skipped++;
                    return;
            }
            applied++;
            Recorder.Add(Game.KeyOf(p), imp); // the restored run's own record, so later saves stay complete
        }
        catch (Exception ex)
        {
            skipped++;
            Plugin.Log.LogError($"Restore: couldn't apply {imp.Kind} for {Game.NameOf(p)}: {ex.Message}");
        }
        finally { Recorder.Suppress = false; }
    }

    private static bool revivesUsedSinceStart;

    internal static int ApplyCounters(GameplayPlayerIdentity p, Dictionary<string, float> stacks, int revivesPerformed)
    {
        int set = 0;
        try { set = Game.SetArtifactStacks(p, stacks); }
        catch (Exception ex) { Plugin.Log.LogError($"Restore (artifact stacks): {ex.Message}"); }
        try { if (revivesPerformed > p.RevivesPerformed) p.RevivesPerformed = revivesPerformed; } catch { }
        return set;
    }

    private static void SetLevelAndRevives(RunSave save)
    {
        revivesUsedSinceStart = false;
        try
        {
            var party = Game.Party;
            if (party != null)
            {
                party.level = Math.Max(1, save.PartyLevel);
                party.SetLevelRpc(party.level);
                if (party.currentExperience != null) party.currentExperience.Value = save.PartyExperience;
            }
            var rv = Game.Revives;
            if (rv?.CurrentRevives != null) rv.CurrentRevives.Value = save.Revives;
            Plugin.Log.LogInfo($"Restore: party level {save.PartyLevel}, revives {save.Revives}.");
        }
        catch (Exception ex) { Plugin.Log.LogError($"Restore (level/revives): {ex}"); }
    }

    // A revive used during the restore shouldn't be handed back at the end.
    [HarmonyPatch(typeof(ReviveManager), nameof(ReviveManager.UseReviveRpc))]
    [HarmonyPostfix]
    private static void ReviveUsed() { if (active != null) revivesUsedSinceStart = true; }

    private static void FinishUp()
    {
        var save = active;
        try
        {
            var party = Game.Party;
            if (party != null && save.PartyLevel > party.level) // in case it didn't take at run start
            {
                party.level = save.PartyLevel;
                party.SetLevelRpc(save.PartyLevel);
            }
            var rv = Game.Revives;
            if (rv?.CurrentRevives != null && rv.CurrentRevives.Value != save.Revives && !revivesUsedSinceStart)
                rv.CurrentRevives.Value = save.Revives;

            // Counters that grow during a run: artifact stacks and revives performed. Set after the replay
            // (replaying can add stacks of its own, e.g. per infusion), on every game.
            foreach (var m in matches)
            {
                int set = ApplyCounters(m.Player, m.Saved.ArtifactStacks, m.Saved.RevivesPerformed);
                Net.SendCounters(m.Player.OwnerClientId, m.Saved.ArtifactStacks, m.Saved.RevivesPerformed);
                if (set > 0) Plugin.Log.LogInfo($"Restore: {set} artifact stacks for {Game.NameOf(m.Player)}.");
            }

            foreach (var m in matches) Recorder.Remember(m.Player);

            try { CatchUp.Plan(new List<GameplayPlayerIdentity>(fresh), matches.ConvertAll(m => m.Player), Recorder.Log); }
            catch (Exception ex) { Plugin.Log.LogError($"Catch-up plan: {ex}"); }
        }
        catch (Exception ex) { Plugin.Log.LogError($"Restore (level/revives): {ex}"); }

        string summary = $"Run restored: wave {save.Wave}, level {save.PartyLevel}, {applied} upgrades" + (skipped > 0 ? $" ({skipped} skipped)" : "");
        Plugin.Log.LogInfo(summary);
        Toast.Show(summary + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : ""), notes.Count > 0 ? 10f : 5f);
        active = null;
        phase = Phase.None;
    }
}
