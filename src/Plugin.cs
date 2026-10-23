using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace RunSaves;

/// <summary>
/// Save a run and continue it later (after quitting, a crash or a restart).
///
/// Saving (host): "Save Run" in the pause menu, plus an autosave at the start of each wave. A save holds the world,
/// wave, run time, party level and XP, revives, and every player's spells and upgrades.
/// Restoring (host): a prompt in the lobby. Press Resume, everyone rejoins and picks the same wizards, then start a
/// run in the same world. The run jumps to the start of the saved wave and every build is rebuilt by replaying
/// the upgrades through the game's own upgrade messages.
///
/// Everyone needs the mod: equipped spells can only be changed by each player's own game.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "girthy_mods.RunSaves";
    public const string Name = "RunSaves";
    public const string Version = "1.0.2";

    internal static new ManualLogSource Log;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> AutosaveEachWave;
    internal static ConfigEntry<bool> ToastOnAutosave;
    internal static ConfigEntry<bool> ClearSaveWhenRunEnds;
    internal static ConfigEntry<bool> ShowSaveButton;
    internal static ConfigEntry<string> SaveKey;
    internal static ConfigEntry<bool> ShowLobbyPrompt;
    internal static ConfigEntry<string> LobbyPanelSide;

    public override void Load()
    {
        Log = base.Log;

        Enabled = Config.Bind("General", "Enabled", true, "Turn run saving and restoring on or off.");

        AutosaveEachWave = Config.Bind("Saving", "AutosaveEachWave", true,
            "Host: save automatically when each new wave starts (covers crashes).");
        ToastOnAutosave = Config.Bind("Saving", "ShowAutosaveMessage", true,
            "Show a short message when an autosave happens.");
        ClearSaveWhenRunEnds = Config.Bind("Saving", "ClearSaveWhenRunEnds", true,
            "Delete the save when the run is won or lost. Quitting or crashing keeps it.");
        ShowSaveButton = Config.Bind("Saving", "PauseMenuButton", true,
            "Host: add a Save Run button to the pause menu.");
        SaveKey = Config.Bind("Saving", "SaveKey", "",
            "Optional key to save during a run (Unity Input System key name, e.g. F5). Empty = no key.");

        ShowLobbyPrompt = Config.Bind("Restoring", "LobbyPrompt", true,
            "Host: show the saved run in the lobby with Resume / Delete / Hide.");
        LobbyPanelSide = Config.Bind("Restoring", "LobbyPanelSide", "Right",
            new ConfigDescription("Which side of the lobby the saved-run panel sits on.", new AcceptableValueList<string>("Right", "Left")));

        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(Recorder));
        harmony.PatchAll(typeof(Saver));
        harmony.PatchAll(typeof(Restorer));
        harmony.PatchAll(typeof(Net));
        harmony.PatchAll(typeof(PauseButton));
        AddComponent<RunSavesUI>();

        Log.LogInfo($"{Name} {Version} ready.");
    }
}
