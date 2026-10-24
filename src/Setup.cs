using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using UObject = UnityEngine.Object;

namespace RunSaves;

/// <summary>
/// The run's setup from the lobby: realm, difficulty ("threat level") and modifiers (Harmony = peaceful mode,
/// Eternity = endless mode, single-spell mode). The lobby (MainMenuManager) holds them as CurrentWorld /
/// CurrentDifficulty and the host's active modifiers; they're recorded when the host starts the level and stored in
/// every save. Pressing Resume puts them back in the lobby, the same way the host's own lobby buttons do.
/// </summary>
[HarmonyPatch]
internal static class Setup
{
    internal static string World, Difficulty;
    internal static List<string> Modifiers;   // "EndlessMode:1" ... (enabled ones)

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.StartLevelOnServerRpc))]
    [HarmonyPrefix]
    private static void StartLevel_Prefix(MainMenuManager __instance)
    {
        try
        {
            if (__instance.__rpc_exec_stage != NetworkBehaviour.__RpcExecStage.Execute) return;
            Capture(__instance);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Recording the run setup: {ex.Message}"); }
    }

    internal static void Capture(MainMenuManager mm)
    {
        World = mm.CurrentWorld?.Value.ToString();
        Difficulty = mm.CurrentDifficulty?.Value.ToString();
        var mods = new List<string>();
        try
        {
            var list = mm.GetActiveModifiers();
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var m = list[i];
                    if (m != null && m.IsEnabled) mods.Add($"{m.ModifierIdentifier}:{m.StackNumber}");
                }
        }
        catch { }
        Modifiers = mods;
        Plugin.Log.LogInfo($"Run setup: {World}, {Difficulty}, modifiers [{string.Join(", ", mods)}].");
    }

    internal static void FillSave(RunSave save)
    {
        save.WorldId = World ?? "";
        save.Difficulty = Difficulty ?? "";
        save.Modifiers = Modifiers != null ? new List<string>(Modifiers) : new List<string>();
    }

    private static readonly string[] WorldOrder = { "Verdant Meadows", "Frozen Ancients", "Pyrestorm Pit", "Astral Planes" };

    /// <summary>Host in the lobby: set the realm, difficulty and modifiers the save was played with.</summary>
    internal static string ApplyToLobby(RunSave save)
    {
        var mm = UObject.FindObjectOfType<MainMenuManager>();
        if (mm == null) return "";
        var done = new List<string>();

        try
        {
            World w;
            if (!string.IsNullOrEmpty(save.WorldId) && Enum.TryParse(save.WorldId, out w)) { }
            else
            {
                int idx = Array.IndexOf(WorldOrder, save.World);
                if (idx < 0) throw new Exception("unknown realm " + save.World);
                w = (World)idx; // the RunManager's realm order (GetRunConfigurationForWorld)
            }
            if (mm.CurrentWorld == null || !mm.CurrentWorld.Value.Equals(w)) { mm.SetWorld(w); done.Add(save.World); }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Setting the realm: {ex.Message}"); }

        try
        {
            if (!string.IsNullOrEmpty(save.Difficulty) && Enum.TryParse(save.Difficulty, out Difficulty d))
            {
                if (mm.CurrentDifficulty == null || !mm.CurrentDifficulty.Value.Equals(d)) mm.SetDifficulty(d);
                done.Add(RunSave.Pretty(save.Difficulty));
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Setting the difficulty: {ex.Message}"); }

        try
        {
            if (save.Modifiers != null && !string.IsNullOrEmpty(save.Difficulty)) // only saves that recorded the setup
            {
                var list = new Il2CppSystem.Collections.Generic.List<ModifierSelection>();
                foreach (ModifierIdentifier id in Enum.GetValues(typeof(ModifierIdentifier)))
                {
                    int stack = 0;
                    foreach (var m in save.Modifiers)
                    {
                        var p = m.Split(':');
                        if (p[0] == id.ToString()) { stack = p.Length > 1 && int.TryParse(p[1], out var n) ? n : 1; break; }
                    }
                    var sel = new ModifierSelection();
                    sel.ModifierIdentifier = id;
                    sel.StackNumber = stack;
                    list.Add(sel);
                }
                mm.SetActiveModifiers(list);
                mm.SetModifiersOnServer();
                if (save.Modifiers.Count > 0) done.Add(string.Join(", ", save.Modifiers.ConvertAll(m => RunSave.Pretty(m.Split(':')[0]))));
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Setting the modifiers: {ex.Message}"); }

        return string.Join(", ", done);
    }
}
