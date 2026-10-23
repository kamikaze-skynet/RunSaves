using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;

namespace RunSaves;

/// <summary>One build change, exactly as the game sent it (enum names as strings so saves stay readable).</summary>
public class Improvement
{
    public string Kind { get; set; }          // Stats, SpellStat, Infusion, Variant, Artifact, Final
    public string Spell { get; set; }
    public string Stat { get; set; }
    public float Value { get; set; }
    public string StatType { get; set; }      // StatModifiersType (flat / percent ...)
    public string Element { get; set; }
    public string Variant { get; set; }
    public string Artifact { get; set; }
    public string Final { get; set; }
    public List<StatEntry> Stats { get; set; } // Kind == Stats
}

public class StatEntry
{
    public string Stat { get; set; }
    public float Value { get; set; }
    public string Type { get; set; }
}

public class SavedPlayer
{
    public string Key { get; set; }            // Steam / Epic id (falls back to the display name)
    public string Name { get; set; }
    public string Character { get; set; }
    public bool WasHost { get; set; }
    public List<string> Spells { get; set; } = new();
    public List<Improvement> Improvements { get; set; } = new();
    // Counters that grow during a run (not upgrades): artifact stacks (per revive, per objective, per health
    // pickup, ...) and how many revives this player has performed.
    public Dictionary<string, float> ArtifactStacks { get; set; } = new();
    public int RevivesPerformed { get; set; }
}

public class RunSave
{
    public int Version { get; set; } = 1;
    public string SavedAt { get; set; }
    public string Reason { get; set; }         // manual / auto
    public string World { get; set; }
    public string ConfigName { get; set; }
    public int Cycle { get; set; }
    public int Wave { get; set; }
    public int WaveCount { get; set; }
    public float RunTime { get; set; }
    public float CycleTime { get; set; }
    public int PartyLevel { get; set; }
    public float PartyExperience { get; set; }
    public int Revives { get; set; }
    public int CurseLevel { get; set; }
    public List<SavedPlayer> Players { get; set; } = new();

    public string Summary()
    {
        string players = Players.Count == 0 ? "no players" : string.Join(", ", Players.ConvertAll(p => $"{p.Name} ({Pretty(p.Character)})"));
        return $"{World}  -  Wave {Wave}/{WaveCount}{(Cycle > 1 ? $" (loop {Cycle})" : "")}  -  Level {PartyLevel}\n{players}";
    }

    internal static string Pretty(string enumName)
    {
        if (string.IsNullOrEmpty(enumName)) return "?";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < enumName.Length; i++)
        {
            char c = enumName[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(enumName[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>The save file: BepInEx/config/RunSaves/save.json (the previous one is kept as save.prev.json).</summary>
internal static class SaveStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string Dir => Path.Combine(Paths.ConfigPath, "RunSaves");
    private static string FilePath => Path.Combine(Dir, "save.json");
    private static string PrevPath => Path.Combine(Dir, "save.prev.json");

    private static RunSave cached;
    private static DateTime cachedStamp;

    internal static bool Exists => File.Exists(FilePath);

    private static readonly object writeLock = new();
    private static string pendingJson;      // newest save waiting to be written
    private static bool writerRunning;

    /// <summary>
    /// Saves to disk on a background thread, so the game never waits for the disk. The data is gathered and turned
    /// into JSON here (on the game's thread, ~1 ms); only the file writing happens in the background. If saves come
    /// quicker than the disk, only the newest is written.
    /// </summary>
    internal static void Write(RunSave save)
    {
        string json = JsonSerializer.Serialize(save, Options);
        cached = save;
        cachedStamp = DateTime.MaxValue; // the file on disk may lag behind for a moment; keep using this copy
        lock (writeLock)
        {
            pendingJson = json;
            if (writerRunning) return;
            writerRunning = true;
        }
        System.Threading.Tasks.Task.Run(WriterLoop);
    }

    private static void WriterLoop()
    {
        while (true)
        {
            string json;
            lock (writeLock)
            {
                json = pendingJson;
                pendingJson = null;
                if (json == null) { writerRunning = false; return; }
            }
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Copy(FilePath, PrevPath, true);
                File.Copy(tmp, FilePath, true);
                File.Delete(tmp);
            }
            catch (Exception ex) { Plugin.Log.LogError($"Writing the save failed: {ex.Message}"); }
        }
    }

    internal static RunSave Read()
    {
        try
        {
            if (!File.Exists(FilePath)) { cached = null; return null; }
            var stamp = File.GetLastWriteTimeUtc(FilePath);
            if (cached != null && (stamp == cachedStamp || cachedStamp == DateTime.MaxValue)) return cached;
            cached = JsonSerializer.Deserialize<RunSave>(File.ReadAllText(FilePath));
            cachedStamp = stamp;
            return cached;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Couldn't read the saved run: {ex.Message}");
            return null;
        }
    }

    internal static void Delete()
    {
        lock (writeLock) pendingJson = null; // a save still waiting to be written mustn't bring it back
        try
        {
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, PrevPath, true); // one level of undo, by hand
                File.Delete(FilePath);
            }
            cached = null;
        }
        catch (Exception ex) { Plugin.Log.LogError($"Couldn't delete the saved run: {ex.Message}"); }
    }
}
