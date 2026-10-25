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
    public const string Version = "1.5.5";

    internal static new ManualLogSource Log;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> AutosaveEachWave;
    internal static ConfigEntry<int> AutosaveIntervalSeconds;
    internal static ConfigEntry<bool> SaveOnQuit;
    internal static ConfigEntry<bool> ToastOnAutosave;
    internal static ConfigEntry<bool> ShowSaveButton;
    internal static ConfigEntry<string> SaveKey;
    internal static ConfigEntry<bool> ShowLobbyPrompt;
    internal static ConfigEntry<string> LobbyPanelSide;
    internal static ConfigEntry<string> PanelKey;
    internal static ConfigEntry<bool> CatchUpEnabled;
    internal static ConfigEntry<int> CatchUpPicks;
    internal static ConfigEntry<bool> CatchUpArtifacts;
    internal static ConfigEntry<float> CatchUpTimeout;
    internal static ConfigEntry<string> CatchUpCancelKey;
    internal static ConfigEntry<bool> DropInEnabled;

    public override void Load()
    {
        Log = base.Log;
        HostConfig.Init(this, Guid, new[] { "Saving.ShowAutosaveMessage", "Saving.PauseMenuButton", "Saving.SaveKey", "Restoring.*" }, new System.Collections.Generic.Dictionary<string, string> { ["General.Enabled"] = "false" });

        Enabled = Config.Bind("General", "Enabled", true, "Turn run saving and restoring on or off.");

        AutosaveEachWave = Config.Bind("Saving", "AutosaveEachWave", true,
            "Host: save automatically when each new wave starts, in every run, including restored ones (the previous save is kept as save.prev.json).");
        AutosaveIntervalSeconds = Config.Bind("Saving", "AutosaveIntervalSeconds", 60,
            new ConfigDescription("Host: also save quietly every this many seconds (0 = off). Keeps upgrades and levels gained mid-wave; a restore still starts at the beginning of the saved wave.",
                new AcceptableValueRange<int>(0, 600)));
        SaveOnQuit = Config.Bind("Saving", "SaveOnQuit", true,
            "Host: save when quitting the run from the pause menu.");
        ToastOnAutosave = Config.Bind("Saving", "ShowAutosaveMessage", true,
            "Show a short message when an autosave happens.");
        ShowSaveButton = Config.Bind("Saving", "PauseMenuButton", true,
            "Host: add a Save Run button to the pause menu.");
        SaveKey = Config.Bind("Saving", "SaveKey", "",
            "Optional key to save during a run (Unity Input System key name, e.g. F5). Empty = no key.");

        ShowLobbyPrompt = Config.Bind("Restoring", "LobbyPrompt", true,
            "Host: show the saved run in the lobby with Resume / Delete / Hide.");
        LobbyPanelSide = Config.Bind("Restoring", "LobbyPanelSide", "Right",
            new ConfigDescription("Which side of the lobby the saved-run panel sits on.", new AcceptableValueList<string>("Right", "Left")));
        PanelKey = Config.Bind("Restoring", "PanelKey", "F10",
            "Key to show or hide the saved-run panel in the lobby (Unity Input System key name). Hide also leaves a small \"Saved Run\" tab to click.");

        CatchUpEnabled = Config.Bind("Catch-up", "Enabled", true,
            "Host: players who join a restored run without a build in the save get catch-up shops (only they pick) to match the party: spells, artifacts and upgrades.");
        CatchUpPicks = Config.Bind("Catch-up", "UpgradePicks", 15,
            new ConfigDescription("Most upgrade picks a new player gets. Each pick counts as several level-ups so the total matches the levels they missed.", new AcceptableValueRange<int>(1, 100)));
        CatchUpArtifacts = Config.Bind("Catch-up", "Artifacts", true, "Also give artifact picks (about as many as the others have).");
        CatchUpTimeout = Config.Bind("Catch-up", "PickTimeoutSeconds", 90f,
            new ConfigDescription("If the player hasn't picked in a catch-up shop after this long, the shop closes and the rest of their catch-up is skipped, so the game never stays paused. 0 = wait forever.", new AcceptableValueRange<float>(0f, 600f)));

        CatchUpCancelKey = Config.Bind("Catch-up", "CancelKey", "Delete",
            "Host: key that ends a catch-up right away and unpauses everyone (Unity Input System key name). Empty = no key.");
        DropInEnabled = Config.Bind("Drop-in", "Enabled", true,
            "Host: friends can join a run that's already going. They get their wizard next to the others, then catch-up picks (or their old build back if they were in this run before). Everyone needs RunSaves.");

        Diag.Hook();
        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(Recorder));
        harmony.PatchAll(typeof(Saver));
        harmony.PatchAll(typeof(Restorer));
        harmony.PatchAll(typeof(Net));
        harmony.PatchAll(typeof(PauseButton));
        harmony.PatchAll(typeof(Setup));
        harmony.PatchAll(typeof(CatchUp));
        try { harmony.PatchAll(typeof(ShopDiag)); } catch (System.Exception ex) { Log.LogWarning($"Shop diagnostics not available: {ex.Message}"); }
        try { harmony.PatchAll(typeof(StatsFix)); } catch (System.Exception ex) { Log.LogError($"Failed to apply the shop stats fix: {ex.Message}"); }
        try { harmony.PatchAll(typeof(DropIn)); }
        catch (System.Exception ex) { Log.LogError($"Drop-in joining couldn't be set up: {ex.Message}"); }
        AddComponent<RunSavesUI>();

        Log.LogInfo($"{Name} {Version} ready.");
    }
}
