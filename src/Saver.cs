using System;
using System.Collections.Generic;
using HarmonyLib;

namespace RunSaves;

/// <summary>Builds the save from the live run (host only), plus autosave. Saves are never deleted automatically: only the lobby's Delete button removes one.</summary>
[HarmonyPatch]
internal static class Saver
{
    // Whether this run's saves may replace save.json. True when the run was restored from it, when there was
    // no save at the start, or once the host saves from the menu. Otherwise a different run (a fresh one, or
    // one in another world) would quietly overwrite the saved run with its autosaves.
    internal static bool OwnsSave;
    private static bool autosavePending;
    private static float autosaveGiveUpAt;

    internal static void OnRunStarted(bool restored)
    {
        lastSaveAt = UnityEngine.Time.unscaledTime;
        autosavePending = false;
        // Autosave always runs (the previous save is kept as save.prev.json). Until this run has saved once,
        // a save on disk belongs to another run, so the end-of-run clean-up leaves it alone.
        OwnsSave = restored || !SaveStore.Exists;
    }

    internal static bool CanSave(out string why)
    {
        why = null;
        if (!Plugin.Enabled.Value) why = "the mod is turned off";
        else if (!Game.IsServer) why = "only the host can save";
        else if (!Recorder.RunActive || Game.Run == null) why = "no run in progress";
        else if (Restorer.Busy) why = "still restoring";
        else if (Game.StoreBusy) why = "finish the shop first"; // its level already counts but the pick isn't made yet
        return why == null;
    }

    /// <summary>Save from the menu or the save key: takes over the save slot.</summary>
    internal static bool SaveManual(out string message)
    {
        bool ok = Save("manual", out message);
        if (ok) OwnsSave = true;
        return ok;
    }

    /// <summary>Called every frame: an autosave that waited for a shop to close.</summary>
    private static float lastSaveAt;

    internal static void Tick()
    {
        // Background save every AutosaveIntervalSeconds, so quitting or a crash mid-wave keeps the upgrades
        // and levels gained since the wave started (a restore still starts at the beginning of that wave).
        int interval = Plugin.AutosaveIntervalSeconds.Value;
        if (interval > 0 && UnityEngine.Time.unscaledTime - lastSaveAt >= interval
            && Recorder.RunActive && !Restorer.Busy && !Game.StoreBusy && Saver.CanSave(out _))
        {
            lastSaveAt = UnityEngine.Time.unscaledTime;
            if (Save("auto", out _)) OwnsSave = true;
        }

        if (!autosavePending) return;
        if (!Recorder.RunActive || UnityEngine.Time.unscaledTime > autosaveGiveUpAt) { autosavePending = false; return; }
        if (Restorer.Busy || Game.StoreBusy) return;
        autosavePending = false;
        Autosave();
    }

    private static void Autosave()
    {
        if (!Save("auto", out string msg)) return;
        OwnsSave = true;
        lastSaveAt = UnityEngine.Time.unscaledTime;
        if (Plugin.ToastOnAutosave.Value) Toast.Show(msg.Replace("Run saved", "Autosaved"), 2.5f);
    }

    internal static bool Save(string reason, out string message)
    {
        if (!CanSave(out string why)) { message = $"Can't save: {why}."; return false; }
        try
        {
            var rm = Game.Run;
            var save = new RunSave
            {
                SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Reason = reason,
                World = Game.WorldName(rm),
                ConfigName = Game.ConfigName(rm),
                Cycle = rm.CycleNumber?.Value ?? 1,
                Wave = Math.Max(1, rm.CurrentWave?.Value ?? 1),
                WaveCount = rm.Configuration?.Waves?.Count ?? 0,
                RunTime = rm.currentRunTimeInSeconds,
                CycleTime = rm.currentCycleTimeInSeconds,
            };

            var party = Game.Party;
            if (party != null)
            {
                save.PartyLevel = party.level;
                save.PartyExperience = party.currentExperience?.Value ?? 0f;
            }
            var rv = Game.Revives;
            if (rv != null) save.Revives = rv.CurrentRevives?.Value ?? 0;

            // Everyone in the run now, plus anyone who left but has a build recorded.
            var players = Game.Players();
            foreach (var p in players) Recorder.Remember(p);
            try { var host = Game.LocalPlayer(); if (host != null) save.CurseLevel = host.ActiveCurseLevel; } catch { }
            if (!string.IsNullOrEmpty(Setup.Difficulty)) Setup.FillSave(save);
            else if (Restorer.LastRestored != null) // a restored run started before setup recording existed: keep the save's
            {
                save.WorldId = Restorer.LastRestored.WorldId; save.Difficulty = Restorer.LastRestored.Difficulty; save.Modifiers = Restorer.LastRestored.Modifiers;
            }

            var keys = new List<string>();
            foreach (var p in players) { var k = Game.KeyOf(p); if (!keys.Contains(k)) keys.Add(k); }
            foreach (var k in Recorder.Log.Keys) if (!keys.Contains(k)) keys.Add(k);

            foreach (var key in keys)
            {
                Recorder.Known.TryGetValue(key, out var known);
                var sp = new SavedPlayer
                {
                    Key = key,
                    Name = known?.Name ?? key,
                    Character = known?.Character ?? "",
                    WasHost = known?.WasHost ?? false,
                    Spells = known?.Spells != null ? new List<string>(known.Spells) : new List<string>(),
                    Improvements = Recorder.Log.TryGetValue(key, out var log) ? new List<Improvement>(log) : new List<Improvement>(),
                    ArtifactStacks = known?.ArtifactStacks != null ? new Dictionary<string, float>(known.ArtifactStacks) : new Dictionary<string, float>(),
                    RevivesPerformed = known?.RevivesPerformed ?? 0,
                };
                save.Players.Add(sp);
            }

            SaveStore.Write(save);
            int changes = 0;
            foreach (var p in save.Players) changes += p.Improvements.Count;
            message = $"Run saved - wave {save.Wave}, level {save.PartyLevel}";
            Plugin.Log.LogInfo($"Saved run ({reason}): {save.World}, cycle {save.Cycle}, wave {save.Wave}/{save.WaveCount}, " +
                               $"level {save.PartyLevel}, {save.Players.Count} players, {changes} upgrades.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Save failed: {ex}");
            message = "Save failed (see the BepInEx log).";
            return false;
        }
    }

    // Autosave when each new wave starts (host; StartWave only runs on the host).
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.StartWave))]
    [HarmonyPostfix]
    private static void WaveStarted(RunManager __instance)
    {
        try
        {
            if (!Plugin.AutosaveEachWave.Value || Restorer.Busy || !Recorder.RunActive) return;
            if ((__instance.CurrentWave?.Value ?? 0) <= 1) return;
            if (Game.StoreBusy)
            {
                // Wait for the shop: a save now would count a level whose upgrade hasn't been picked.
                autosavePending = true;
                autosaveGiveUpAt = UnityEngine.Time.unscaledTime + 60f;
                return;
            }
            Autosave();
        }
        catch (Exception ex) { Plugin.Log.LogError($"Autosave: {ex}"); }
    }

    // Host quits the run from the pause menu: save first, so the run continues from here next time.
    [HarmonyPatch(typeof(ConfirmQuitPanel), nameof(ConfirmQuitPanel.Quit))]
    [HarmonyPrefix]
    private static void Quitting()
    {
        try
        {
            if (!Plugin.SaveOnQuit.Value || !CanSave(out _)) return;
            if (Save("quit", out _)) OwnsSave = true;
        }
        catch (Exception ex) { Plugin.Log.LogError($"Save on quit: {ex}"); }
    }
}
