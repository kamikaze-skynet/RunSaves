using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace RunSaves;

/// <summary>
/// Drop-in joining: friends can join a run that's already going.
///
/// The game only sets players up once, when the run starts (LevelStarter): it makes each player's identity, tells
/// every game who that player is (InitializeOnClientsRpc), spawns their wizard, and from then on every upgrade is
/// a one-off message to everyone. It also turns joining off: the lobby is made unjoinable and the host refuses
/// connections while not in the main menu. So for a mid-run joiner:
///
///  Host:  keeps the lobby joinable and accepts the connection (same checks as the game: version, player cap).
///         The game itself then loads the realm on the joiner's computer and sends them everything that exists.
///  Joiner: says hello; the host answers with what the joiner missed: who each player is, everyone's upgrades,
///         the party level and revives. The joiner's game applies these to itself only (the game's own handlers,
///         run locally), so nobody else sees anything twice.
///  Joiner: once the realm is loaded, says it's ready. The host makes their identity and wizard the same way the
///         run start does, near the others.
///  Host:  a player who was in this run before (left and came back) gets their build back; a new player gets
///         catch-up picks (the paused catch-up session).
/// </summary>
[HarmonyPatch]
internal static class DropIn
{
    private const string MessageName = "girthy_mods.RunSaves.DropIn";
    private const char Rec = '\u001e', Fld = '\u001f';
    private static CustomMessagingManager.HandleNamedMessageDelegate handler; // kept alive for IL2CPP
    private static IntPtr registeredOn;

    private static bool Enabled => Plugin.Enabled.Value && Plugin.DropInEnabled.Value;

    // ===================================================================== host: letting them in

    private static bool inStartGame;

    // Starting a run turns the lobby unjoinable and marks it "game in progress" (which joiners' games refuse).
    [HarmonyPatch(typeof(EOSLobbyService), nameof(EOSLobbyService.StartGame))]
    [HarmonyPrefix]
    private static void StartGame_Prefix() => inStartGame = true;

    [HarmonyPatch(typeof(EOSLobbyService), nameof(EOSLobbyService.StartGame))]
    [HarmonyPostfix]
    private static void StartGame_Postfix() => inStartGame = false;

    [HarmonyPatch(typeof(EOSLobbyService), nameof(EOSLobbyService.SetLobbyJoinable))]
    [HarmonyPrefix]
    private static void EosJoinable_Prefix(ref bool __0) { if (inStartGame && Enabled) __0 = true; }

    [HarmonyPatch(typeof(SteamLobbyService), nameof(SteamLobbyService.SetLobbyJoinable))]
    [HarmonyPrefix]
    private static void SteamJoinable_Prefix(ref bool __0) { if (inStartGame && Enabled) __0 = true; }

    [HarmonyPatch(typeof(EOSLobbyService), nameof(EOSLobbyService.SetGameIsOngoing))]
    [HarmonyPrefix]
    private static void EosOngoing_Prefix(ref bool __0) { if (__0 && Enabled) __0 = false; }

    [HarmonyPatch(typeof(SteamLobbyService), nameof(SteamLobbyService.SetGameIsOngoing))]
    [HarmonyPrefix]
    private static void SteamOngoing_Prefix(ref bool __0) { if (__0 && Enabled) __0 = false; }

    /// <summary>The game refuses anyone while a run is going. Accept them instead (same version and cap checks).</summary>
    [HarmonyPatch(typeof(HostState), nameof(HostState.OnApprovalCheck))]
    [HarmonyPrefix]
    private static bool Approval_Prefix(HostState __instance, NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        if (!Enabled) return true;
        try
        {
            bool inRun = Game.Run != null;
            try { inRun |= __instance.lobbyIsInGame; } catch { }
            if (!inRun) return true; // lobby: the game's own check

            var bytes = request.Payload;
            if (bytes == null || bytes.Length == 0 || bytes.Length > 0x400) return true;
            var raw = new byte[bytes.Length];
            for (int i = 0; i < raw.Length; i++) raw[i] = bytes[i];
            string json = Encoding.UTF8.GetString(raw);
            // The game reads this with Unity's JsonUtility: field names as keys, enums as numbers.
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
            int I(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int x) ? x : 0;
            float F(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0f;
            bool B(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

            if (BuildVersionProvider.IsIncompatibleWith(S("Version"))) return true; // the game refuses it with its reason
            var sm = Session;
            if (sm == null) return true;
            int max = MaxPlayers();
            if (sm.GetNumberOfPlayers() >= max) return true;

            ulong id = request.ClientNetworkId;
            string name = S("DisplayName");
            Plugin.Log.LogInfo($"Drop-in: join request from {name}: character {I("CharacterId")}, skin {I("SkinId")}, title {I("PlayerTitleId")}, platform {I("Platform")}, version {S("Version")}.");
            sm.AddPlayerData(id, S("PlayerId"), name, (CharacterId)I("CharacterId"), (SkinId)I("SkinId"), B("JoiningViaMatchmaking"),
                I("CharacterRank"), F("CharacterRankProgress"), I("CharacterPrestige"), (Platform)I("Platform"), S("PlatformPlayerId"),
                (PlayerTitleId)I("PlayerTitleId"), I("ActiveCurseLevel"), B("HasTinyModeCurse"));
            response.Approved = true;
            try { response.Pending = false; } catch { }
            joiners[id] = new Joiner { Id = id, Name = name, Seen = Time.unscaledTime, Key = string.IsNullOrWhiteSpace(S("PlatformPlayerId")) ? "name:" + name : S("PlatformPlayerId") };
            Plugin.Log.LogInfo($"Drop-in: {name} is joining the run in progress.");
            Toast.Show($"{name} is joining the run...", 5f);
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Drop-in (approval): {ex}");
            return true;
        }
    }

    /// <summary>The run's settings the joiner's game must have before its map loads: the realm, difficulty, modes, the
    /// shared random seed (it picks the map layout) and the layout itself.</summary>
    private static string EarlyPayload()
    {
        var c = CultureInfo.InvariantCulture;
        var rd = UObject.FindObjectOfType<RunData>();
        if (rd == null) return "";
        int variant = -1;
        try { var wvm = UObject.FindObjectOfType<WorldVariantManager>(); if (wvm != null) variant = wvm.activeMapVariantIndex; } catch { }
        return string.Join(Fld.ToString(), ((int)rd.SelectedWorld).ToString(c), ((int)rd.SelectedDifficulty).ToString(c),
            rd.IsPeacefulModeEnabled ? "1" : "0", rd.IsEndlessModeEnabled ? "1" : "0", rd.RandomSeed.ToString(c), Clean(rd.RunId), variant.ToString(c));
    }

    private static SessionManager session;
    private static SessionManager Session
    {
        get
        {
            if (!Game.Alive(session)) session = UObject.FindObjectOfType<SessionManager>();
            return session;
        }
    }

    /// <summary>The lobby size: MoreWizards' MaxPlayers if it's installed, else the game's 4.</summary>
    private static int MaxPlayers()
    {
        try
        {
            if (IL2CPPChainloader.Instance.Plugins.TryGetValue("girthy_mods.MoreWizards", out var info) && info.Instance is BasePlugin bp)
                if (bp.Config.TryGetEntry<int>(new ConfigDefinition("Lobby", "MaxPlayers"), out var e)) return Math.Max(1, e.Value);
        }
        catch { }
        return 4;
    }

    // ===================================================================== host: setting them up

    private sealed class Joiner
    {
        public ulong Id;
        public string Name;
        public float Seen, At;
        public int Step;                 // 0 waiting, 1 identity made, 2 told everyone, 3 wizard spawning, 4 done
        public bool Ready, StateSent, Snapshotted;
        public string Key;              // their RunSaves player key (Steam/Epic id), known from the join request
        public float NextEarly;
        public float InitAt;            // when everyone was told who they are (0 = not yet)
        public GameplayPlayerIdentity Identity;
        public int Spells;
        public float SpellsSince;
        // a returning player: their build, sent once their spells are back
        public List<SpellType> OldSpells;
        public List<Improvement> OldLog;
        public Dictionary<string, float> OldStacks;
        public int OldRevives;
    }

    private static readonly Dictionary<ulong, Joiner> joiners = new();

    private static void HostTick()
    {
        if (joiners.Count == 0) return;
        float t = Time.unscaledTime;
        // Tell the joiner's game early that it's joining a run in progress (it then ignores upgrades sent live
        // until it has the full picture, so none are applied twice). Repeated until they say hello.
        foreach (var j in joiners.Values)
            if (!j.StateSent && t >= j.NextEarly && t - j.Seen < 60f)
            {
                j.NextEarly = t + (t - j.Seen < 15f ? 0.25f : 1f);
                try { Send("early", EarlyPayload(), j.Id); } catch { }
            }
        if (Game.Run == null || !Recorder.RunActive || Restorer.Busy) return;
        var ids = ConnectedIds();

        foreach (var j in new List<Joiner>(joiners.Values))
        {
            try
            {
                if (ids != null && !ids.Contains(j.Id) && Time.unscaledTime - j.Seen > 10f) { joiners.Remove(j.Id); continue; } // left
                HostStep(j);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Drop-in ({j.Name}): {ex}");
                joiners.Remove(j.Id);
            }
        }
    }

    private static HashSet<ulong> ConnectedIds()
    {
        try
        {
            var sm = Session;
            var list = sm?.GetClientIdsForAllPlayers();
            if (list == null) return null;
            var set = new HashSet<ulong>();
            for (int i = 0; i < list.Count; i++) set.Add(list[i]);
            return set;
        }
        catch { return null; }
    }

    private static void HostStep(Joiner j)
    {
        float now = Time.unscaledTime;
        switch (j.Step)
        {
            case 0:
            {
                if (Game.PlayerFor(j.Id) != null) { joiners.Remove(j.Id); return; } // already in (normal start)
                // Without RunSaves on their side nobody says "ready": set them up anyway after a while.
                if (!j.Ready && now - j.Seen < 45f) return;
                Snapshot(j);
                var ls = UObject.FindObjectOfType<LevelStarter>();
                if (ls == null) return;
                j.Identity = ls.SpawnIdentity(j.Id);
                j.At = now;
                j.Step = 1;
                Plugin.Log.LogInfo($"Drop-in: made {j.Name}'s player.");
                return;
            }
            case 1:
            {
                if (!Game.Alive(j.Identity) || !j.Identity.IsSpawned || now - j.At < 0.5f) return;
                var d = DataFor(j.Id);
                if (d == null) { Plugin.Log.LogWarning($"Drop-in: no session data for {j.Name}."); joiners.Remove(j.Id); return; }
                j.Identity.InitializeOnClientsRpc(d.DisplayName, d.CharacterId, d.SkinId, d.Color, d.PlayerTitleId, d.CharacterRank,
                    d.CharacterRankProgress, d.CharacterPrestige, d.ActiveCurseLevel, d.Platform, d.PlatformPlayerId, d.PlayerId);
                StatsFix.EnsureFor(j.Id);
                j.At = now;
                j.InitAt = now;
                j.Step = 2;
                return;
            }
            case 2:
            {
                if (now - j.At < 1f) return;
                var pos = SpawnPosition();
                j.Identity.SetCameraPositionRpc(pos);
                j.Identity.StartPlayerRespawnOnOwnerRpc(pos);
                j.At = now;
                j.Step = 3;
                Plugin.Log.LogInfo($"Drop-in: {j.Name}'s wizard is on the way.");
                return;
            }
            case 3:
            {
                // Wait for their wizard and starting spell, then match them to the party.
                int spells = Game.SpellsOf(j.Identity).Count;
                if (spells != j.Spells) { j.Spells = spells; j.SpellsSince = now; }
                bool settled = spells > 0 && now - j.SpellsSince > 3f && HasWizard(j.Id);
                if (!settled && now - j.At < 30f) return;
                if (!settled)
                {
                    // Their game never got a working wizard: catch-up shops would only freeze everyone.
                    Plugin.Log.LogWarning($"Drop-in: {j.Name}'s wizard didn't come up within 30 s (spells: {spells}, wizard: {HasWizard(j.Id)}); no catch-up picks.");
                    Toast.Show($"{j.Name}'s game didn't finish joining. No catch-up picks.", 6f);
                    joiners.Remove(j.Id);
                    return;
                }
                j.Step = 4;
                j.At = now;
                Finish(j);
                if (j.OldLog == null || j.OldLog.Count == 0) joiners.Remove(j.Id);
                return;
            }
            case 4:
            {
                // A returning player: their spells first (their own game equips them), then the upgrades on them.
                var have = Game.SpellsOf(j.Identity);
                bool spellsBack = j.OldSpells == null || j.OldSpells.TrueForAll(have.Contains);
                if (!spellsBack && now - j.At < 15f) return;
                RestoreBuild(j);
                joiners.Remove(j.Id);
                return;
            }
        }
    }

    private static void Finish(Joiner j)
    {
        var p = j.Identity;
        if (!Game.Alive(p)) return;
        try
        {
            var rm = Game.Run;
            if (rm != null) Net.SendTime(rm.currentCycleTimeInSeconds, rm.currentRunTimeInSeconds);
        }
        catch { }

        if (j.OldLog != null && j.OldLog.Count > 0)
        {
            // Back in the same run: give them their build back. Spells first (their own game equips them).
            Plugin.Log.LogInfo($"Drop-in: {j.Name} was in this run before; restoring {j.OldSpells.Count} spells and {j.OldLog.Count} upgrades.");
            if (j.OldSpells.Count > 0) Net.SendSpells(p.OwnerClientId, j.OldSpells);
            return;
        }
        j.OldLog = null;
        Recorder.Remember(p);

        var others = new List<GameplayPlayerIdentity>();
        foreach (var o in Game.Players()) if (o.OwnerClientId != p.OwnerClientId) others.Add(o);
        Toast.Show($"{j.Name} joined the run.", 4f);
        CatchUp.Plan(new List<GameplayPlayerIdentity> { p }, others, Recorder.Log);
    }

    /// <summary>What we knew about this player from earlier in the run, copied before anything (an autosave, their
    /// new wizard) overwrites it.</summary>
    private static void Snapshot(Joiner j)
    {
        if (j.Snapshotted) return;
        j.Snapshotted = true;
        if (string.IsNullOrEmpty(j.Key) || !Recorder.Log.TryGetValue(j.Key, out var log) || log.Count == 0) return;
        Recorder.Known.TryGetValue(j.Key, out var known);
        j.OldLog = new List<Improvement>(log);
        j.OldSpells = new List<SpellType>();
        if (known?.Spells != null) foreach (var sp in known.Spells) if (Enum.TryParse(sp, out SpellType t)) j.OldSpells.Add(t);
        j.OldStacks = known?.ArtifactStacks != null ? new Dictionary<string, float>(known.ArtifactStacks) : null;
        j.OldRevives = known?.RevivesPerformed ?? 0;
    }

    private static void RestoreBuild(Joiner j)
    {
        var p = j.Identity;
        if (!Game.Alive(p)) return;
        var im = Game.Improvements;
        var have = Game.SpellsOf(p);
        int sent = 0, noSpell = 0;
        if (im != null)
        {
            Recorder.Suppress = true; // already in the record
            try
            {
                foreach (var imp in j.OldLog)
                {
                    bool spellBound = imp.Kind is "SpellStat" or "Infusion" or "Variant";
                    if (spellBound && (!Enum.TryParse(imp.Spell, out SpellType sp) || !have.Contains(sp))) { noSpell++; continue; }
                    try { if (Upgrades.Send(im, p.OwnerClientId, imp)) sent++; } catch { }
                }
            }
            finally { Recorder.Suppress = false; }
        }
        if (noSpell > 0) Plugin.Log.LogWarning($"Drop-in: {noSpell} of {j.Name}'s spell upgrades skipped (those spells didn't come back).");
        try { Restorer.ApplyCounters(p, j.OldStacks, j.OldRevives); } catch { } // the host's own copy
        Net.SendCounters(p.OwnerClientId, j.OldStacks, j.OldRevives);       // everyone else
        Recorder.Remember(p);
        Plugin.Log.LogInfo($"Drop-in: gave {j.Name} back {sent} upgrades.");
        Toast.Show($"{j.Name} is back with their build.", 5f);
    }

    private static bool HasWizard(ulong clientId)
    {
        try
        {
            foreach (var pl in UObject.FindObjectsOfType<GameplayPlayer>())
                if (pl != null && pl.IsSpawned && pl.OwnerClientId == clientId) return true;
        }
        catch { }
        return false;
    }

    private static Vector3 SpawnPosition()
    {
        foreach (var pl in UObject.FindObjectsOfType<GameplayPlayer>())
        {
            try
            {
                if (pl == null || !pl.IsSpawned) continue;
                var off = UnityEngine.Random.insideUnitCircle.normalized * 2.5f;
                return pl.transform.position + new Vector3(off.x, 0f, off.y);
            }
            catch { }
        }
        try
        {
            var ls = UObject.FindObjectOfType<LevelStarter>();
            var sp = ls?.spawnPoints;
            if (sp != null && sp.Count > 0 && sp[0] != null) return sp[0].position;
        }
        catch { }
        return Vector3.zero;
    }

    private static SessionPlayerData DataFor(ulong clientId)
    {
        var list = Session?.GetPlayerDataForAllPlayers();
        if (list == null) return null;
        for (int i = 0; i < list.Count; i++) if (list[i].ClientId == clientId) return list[i];
        return null;
    }

    /// <summary>What a joiner missed: who everyone is, their upgrades, the party level and revives.</summary>
    private static string BuildState(ulong forClient)
    {
        var c = CultureInfo.InvariantCulture;
        var recs = new List<string>();
        var party = Game.Party;
        if (party != null) recs.Add("L" + Fld + party.level.ToString(c));
        try { var rv = Game.Revives; if (rv != null) recs.Add("R" + Fld + rv.CalculateTotalRevives().ToString(c)); } catch { }

        var list = Session?.GetPlayerDataForAllPlayers();
        if (list != null)
            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i];
                if (d.ClientId == forClient || Game.PlayerFor(d.ClientId) == null) continue;
                // Another joiner being set up: if they're announced after this one was let in, that reaches them live.
                if (joiners.TryGetValue(d.ClientId, out var other) && other.Step >= 1 &&
                    (other.InitAt <= 0f || (joiners.TryGetValue(forClient, out var me) && other.InitAt >= me.Seen))) continue;
                var col = d.Color;
                recs.Add(string.Join(Fld.ToString(), "P", d.ClientId.ToString(c), Clean(d.PlayerId), Clean(d.DisplayName),
                    ((int)d.CharacterId).ToString(c), ((int)d.SkinId).ToString(c),
                    col.r.ToString(c), col.g.ToString(c), col.b.ToString(c), col.a.ToString(c),
                    ((int)d.PlayerTitleId).ToString(c), d.CharacterRank.ToString(c), d.CharacterRankProgress.ToString(c),
                    d.CharacterPrestige.ToString(c), d.ActiveCurseLevel.ToString(c), ((int)d.Platform).ToString(c), Clean(d.PlatformPlayerId)));
            }

        foreach (var p in Game.Players())
        {
            if (p.OwnerClientId == forClient) continue;
            if (!Recorder.Log.TryGetValue(Game.KeyOf(p), out var log)) continue;
            foreach (var imp in log)
                recs.Add("U" + Fld + p.OwnerClientId.ToString(c) + Fld + JsonSerializer.Serialize(imp));
        }
        return string.Join(Rec.ToString(), recs);
    }

    private static string Clean(string s) => (s ?? "").Replace(Rec, ' ').Replace(Fld, ' ');

    // ===================================================================== joiner

    private sealed class PlayerInit
    {
        public ulong Id;
        public string PlayerId, Name, PlatformId;
        public int Character, Skin, Title, Rank, Prestige, Curse, Platform;
        public float RankProgress;
        public Color Color;
        public bool Done;
    }

    private static bool stateReceived, upgradesDone, levelDone, runStartDone;
    private static float stateAt, nextHello, nextReady, localSeenAt;
    private static int level = -1, totalRevives = -1;
    private static readonly List<PlayerInit> inits = new();
    private static readonly List<(ulong client, Improvement imp)> upgrades = new();
    private static bool dropping;      // this game joined a run in progress
    private static bool expectDropIn;  // the host said so; upgrades sent live are ignored until the full picture is in
    private static bool replaying;     // our own local replay is running
    private static readonly List<(ulong client, Improvement imp)> spellWaiting = new();
    private static float spellWaitUntil, nextSpellTry;

    private static bool Replay(ImprovementsManager im, ulong client, Improvement imp)
    {
        replaying = true;
        try { return Local(im, () => Upgrades.Send(im, client, imp)); }
        catch { return false; }
        finally { replaying = false; }
    }

    /// <summary>Spell upgrades go into the player's stats for that spell, which exist only once the spell is
    /// equipped on that player in this game (else the game throws "key not present").</summary>
    private static bool SpellReady(ulong client, Improvement imp)
    {
        if (imp.Kind != "SpellStat" && imp.Kind != "Infusion" && imp.Kind != "Variant") return true;
        if (!Enum.TryParse(imp.Spell, out SpellType spell)) return true;
        try
        {
            var p = Game.PlayerFor(client);
            var attrs = p?.stats?.attributeForSpells;
            return attrs != null && attrs.ContainsKey(spell);
        }
        catch { return false; }
    }
    private static int helloTries;

    private static int hostVariant = -1;
    private static bool runSetupApplied;

    private static void OnEarly(string body)
    {
        if (runSetupApplied || string.IsNullOrEmpty(body)) return;
        var f = body.Split(Fld);
        if (f.Length < 7) return;
        var c = CultureInfo.InvariantCulture;
        try
        {
            var rd = UObject.FindObjectOfType<RunData>();
            if (rd == null) return;
            rd.SelectedWorld = (World)int.Parse(f[0], c);
            rd.SelectedDifficulty = (Difficulty)int.Parse(f[1], c);
            rd.IsPeacefulModeEnabled = f[2] == "1";
            rd.IsEndlessModeEnabled = f[3] == "1";
            rd.RandomSeed = int.Parse(f[4], c);
            if (!string.IsNullOrEmpty(f[5])) rd.RunId = f[5];
            hostVariant = int.Parse(f[6], c);
            runSetupApplied = true;
            Plugin.Log.LogInfo($"Drop-in: using the host's run: {rd.SelectedWorld}, {rd.SelectedDifficulty}, map layout {hostVariant}.");

            // Too late for the map? Switch to the host's layout now.
            var wvm = UObject.FindObjectOfType<WorldVariantManager>();
            if (wvm != null && hostVariant >= 0 && wvm.activeMapVariantIndex != hostVariant)
            {
                Plugin.Log.LogWarning($"Drop-in: this game had already loaded map layout {wvm.activeMapVariantIndex}; switching to the host's ({hostVariant}).");
                wvm.activeMapVariantIndex = hostVariant;
                wvm.ActivateCorrectVariant();
            }
        }
        catch (Exception ex) { Plugin.Log.LogError($"Drop-in (run setup): {ex.Message}"); }
    }

    /// <summary>The joiner's map uses the host's layout (it's picked from the run's seed, which a joiner never got).</summary>
    [HarmonyPatch(typeof(WorldVariantManager), nameof(WorldVariantManager.ActivateCorrectVariant))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Variant_Prefix(WorldVariantManager __instance)
    {
        try
        {
            if (hostVariant < 0 || Game.IsServer) return;
            if (__instance.activeMapVariantIndex != hostVariant)
            {
                Plugin.Log.LogInfo($"Drop-in: map layout {__instance.activeMapVariantIndex} -> {hostVariant} (the host's).");
                __instance.activeMapVariantIndex = hostVariant;
            }
        }
        catch { }
    }

    private static void ResetClient()
    {
        hostVariant = -1;
        runSetupApplied = false;
        stateReceived = upgradesDone = levelDone = runStartDone = dropping = expectDropIn = replaying = false;
        spellWaiting.Clear();
        helloTries = 0;
        level = totalRevives = -1;
        inits.Clear();
        upgrades.Clear();
        localSeenAt = 0f;
    }

    private static void ClientTick()
    {
        float now = Time.unscaledTime;
        if (!stateReceived)
        {
            // Only once the realm and its managers are here, so the snapshot covers everything sent before it.
            bool synced = Game.Run != null && Game.Improvements != null && Game.Party != null;
            if (synced && now >= nextHello)
            {
                nextHello = now + 2f;
                if (++helloTries > 10 && !expectDropIn) { stateReceived = true; return; } // host without drop-in
                Send("hello", null);
            }
            return;
        }
        if (!dropping) return;

        // Who everyone is: as each of their identities exists here.
        foreach (var pi in inits) if (!pi.Done) TryInit(pi);

        bool initsDone = inits.TrueForAll(p => p.Done) || now - stateAt > 20f;
        var im = Game.Improvements;
        if (initsDone && !upgradesDone && im != null)
        {
            int n = 0, bad = 0;
            foreach (var (client, imp) in upgrades)
            {
                // A spell's upgrades need that spell on the player here first; they wait until it arrives.
                if (!SpellReady(client, imp)) { spellWaiting.Add((client, imp)); continue; }
                if (Replay(im, client, imp)) n++; else bad++;
            }
            upgradesDone = true;
            spellWaitUntil = now + 60f;
            Plugin.Log.LogInfo($"Drop-in: applied {n} of the other players' upgrades here" + (bad > 0 ? $" ({bad} skipped)" : "") +
                               (spellWaiting.Count > 0 ? $", {spellWaiting.Count} waiting for their spells." : "."));
        }
        if (upgradesDone && spellWaiting.Count > 0 && im != null && now >= nextSpellTry)
        {
            nextSpellTry = now + 1f;
            int n = 0;
            for (int i = spellWaiting.Count - 1; i >= 0; i--)
            {
                var (client, imp) = spellWaiting[i];
                if (!SpellReady(client, imp)) continue;
                spellWaiting.RemoveAt(i);
                if (Replay(im, client, imp)) n++;
            }
            if (n > 0) Plugin.Log.LogInfo($"Drop-in: applied {n} more spell upgrades.");
            if (now > spellWaitUntil && spellWaiting.Count > 0)
            {
                Plugin.Log.LogWarning($"Drop-in: {spellWaiting.Count} spell upgrades never found their spell here; skipped.");
                spellWaiting.Clear();
            }
        }

        var party = Game.Party;
        if (!levelDone && party != null && level > 0)
        {
            if (level > party.level) try { party.level = level; } catch (Exception ex) { Plugin.Log.LogWarning($"Drop-in (level): {ex.Message}"); }
            var rv = Game.Revives;
            if (rv != null && totalRevives >= 0)
                try { Local(rv, () => { rv.InitializeOnClientsRpc(totalRevives); return true; }); } catch (Exception ex) { Plugin.Log.LogWarning($"Drop-in (revives): {ex.Message}"); }
            levelDone = true;
        }

        // Once the realm is here and everyone's known: ask for our wizard.
        var me = Game.LocalPlayer();
        if (me == null)
        {
            if (Game.Run != null && initsDone && now >= nextReady) { nextReady = now + 3f; Send("ready", null); }
            return;
        }
        if (localSeenAt <= 0f) localSeenAt = now;
        if (!runStartDone && now - localSeenAt > 2f)
        {
            runStartDone = true;
            var rm = Game.Run;
            if (rm != null)
                try { Local(rm, () => { rm.StartRunOnClientRpc(); return true; }); } catch (Exception ex) { Plugin.Log.LogWarning($"Drop-in (run start): {ex.Message}"); }
            Plugin.Log.LogInfo("Drop-in: you're in.");
        }
    }

    private static void TryInit(PlayerInit pi)
    {
        var id = Game.PlayerFor(pi.Id);
        if (id == null) return;
        pi.Done = true;
        try
        {
            Local(id, () =>
            {
                id.InitializeOnClientsRpc(pi.Name, (CharacterId)pi.Character, (SkinId)pi.Skin, pi.Color, (PlayerTitleId)pi.Title, pi.Rank,
                    pi.RankProgress, pi.Prestige, pi.Curse, (Platform)pi.Platform, pi.PlatformId, pi.PlayerId);
                return true;
            });
            Plugin.Log.LogInfo($"Drop-in: set up {pi.Name} here.");
        }
        catch (Exception ex) { Plugin.Log.LogError($"Drop-in (set up {pi.Name}): {ex.Message}"); }
    }

    // Diagnostics: every game logs each player being set up (and the game's own errors show up with where they came from).
    [HarmonyPatch(typeof(GameplayPlayerIdentity), nameof(GameplayPlayerIdentity.InitializeOnClientsRpc))]
    [HarmonyPrefix]
    private static void Init_Prefix(GameplayPlayerIdentity __instance, string displayName, CharacterId characterId)
    {
        try
        {
            if (__instance.__rpc_exec_stage == NetworkBehaviour.__RpcExecStage.Execute)
                Plugin.Log.LogInfo($"Setting up player {displayName} (client {__instance.OwnerClientId}, {characterId}){(__instance.IsOwner ? " - that's you" : "")}.");
        }
        catch { }
    }

    // As soon as another player's identity arrives here, before their wizard does.
    [HarmonyPatch(typeof(GameplayPlayerIdentity), nameof(GameplayPlayerIdentity.OnNetworkSpawn))]
    [HarmonyPostfix]
    private static void IdentitySpawned(GameplayPlayerIdentity __instance)
    {
        if (!dropping || Game.IsServer) return;
        try
        {
            foreach (var pi in inits)
                if (!pi.Done && pi.Id == __instance.OwnerClientId) { TryInit(pi); break; }
        }
        catch { }
    }

    /// <summary>Joiner: upgrades broadcast live before the snapshot are in the snapshot too; skip them here.</summary>
    private static bool SkipLive(NetworkBehaviour b)
    {
        if (replaying || !expectDropIn || upgradesDone || Game.IsServer) return true;
        try { return b.__rpc_exec_stage != NetworkBehaviour.__RpcExecStage.Execute; } catch { return true; }
    }

    [HarmonyPatch(typeof(ImprovementsManager), nameof(ImprovementsManager.SendCharacterStatModifiersRpc))]
    [HarmonyPatch(typeof(ImprovementsManager), nameof(ImprovementsManager.SendSpellStatModifierRpc))]
    [HarmonyPatch(typeof(ImprovementsManager), nameof(ImprovementsManager.SendSpellInfusionRpc))]
    [HarmonyPatch(typeof(ImprovementsManager), nameof(ImprovementsManager.SendSpellVariantRpc))]
    [HarmonyPatch(typeof(ImprovementsManager), nameof(ImprovementsManager.SendArtifactRpc))]
    [HarmonyPatch(typeof(ImprovementsManager), nameof(ImprovementsManager.SendFinalUpgradeRpc))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool LiveUpgrade_Prefix(ImprovementsManager __instance) => SkipLive(__instance);

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.OnDestroy))]
    [HarmonyPostfix]
    private static void RunEnded()
    {
        if (!Game.IsServer) ResetClient();
        joiners.Clear();
    }

    /// <summary>Runs one of the game's network calls on this game only: its own handler, as if it had arrived.</summary>
    private static bool Local(NetworkBehaviour b, Func<bool> call)
    {
        var prev = b.__rpc_exec_stage;
        b.__rpc_exec_stage = NetworkBehaviour.__RpcExecStage.Execute;
        try { return call(); }
        finally { b.__rpc_exec_stage = prev; }
    }

    private static void OnState(string payload)
    {
        if (stateReceived) return;      // an answer to a repeated hello
        if (payload == "wait") { helloTries = 0; return; } // the host's run is still starting: ask again
        stateReceived = true;
        if (payload == "idle") { expectDropIn = false; return; } // joined in the lobby: nothing to catch up on
        dropping = true;
        stateAt = Time.unscaledTime;
        inits.Clear();
        upgrades.Clear();
        var c = CultureInfo.InvariantCulture;
        foreach (var rec in payload.Split(Rec))
        {
            var f = rec.Split(Fld);
            try
            {
                switch (f[0])
                {
                    case "L": level = int.Parse(f[1], c); break;
                    case "R": totalRevives = int.Parse(f[1], c); break;
                    case "P" when f.Length >= 17:
                        inits.Add(new PlayerInit
                        {
                            Id = ulong.Parse(f[1], c), PlayerId = f[2], Name = f[3],
                            Character = int.Parse(f[4], c), Skin = int.Parse(f[5], c),
                            Color = new Color(float.Parse(f[6], c), float.Parse(f[7], c), float.Parse(f[8], c), float.Parse(f[9], c)),
                            Title = int.Parse(f[10], c), Rank = int.Parse(f[11], c), RankProgress = float.Parse(f[12], c),
                            Prestige = int.Parse(f[13], c), Curse = int.Parse(f[14], c), Platform = int.Parse(f[15], c), PlatformId = f[16],
                        });
                        break;
                    case "U" when f.Length >= 3:
                        var imp = JsonSerializer.Deserialize<Improvement>(f[2]);
                        if (imp != null) upgrades.Add((ulong.Parse(f[1], c), imp));
                        break;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"Drop-in: skipped a record ({ex.Message})."); }
        }
        Plugin.Log.LogInfo($"Drop-in: joining a run in progress ({inits.Count} players, {upgrades.Count} upgrades, party level {level}).");
        Toast.Show("Joining the run in progress...", 5f);
        foreach (var pi in inits) TryInit(pi);
    }

    // ===================================================================== messages

    internal static void Tick()
    {
        try
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || nm.CustomMessagingManager == null)
            {
                registeredOn = IntPtr.Zero;
                if (stateReceived || dropping) ResetClient();
                joiners.Clear();
                return;
            }
            var cmm = nm.CustomMessagingManager;
            if (registeredOn != cmm.Pointer)
            {
                handler ??= DelegateSupport.ConvertDelegate<CustomMessagingManager.HandleNamedMessageDelegate>(new Action<ulong, FastBufferReader>(OnMessage));
                cmm.RegisterNamedMessageHandler(MessageName, handler);
                registeredOn = cmm.Pointer;
            }
            if (!Enabled) return;
            if (nm.IsServer) HostTick();
            else ClientTick();
        }
        catch (Exception ex) { Plugin.Log.LogError($"Drop-in: {ex.Message}"); }
    }

    private static void OnMessage(ulong sender, FastBufferReader reader)
    {
        try
        {
            reader.ReadValueSafe(out string payload, false);
            int bar = payload.IndexOf('|');
            string kind = bar < 0 ? payload : payload.Substring(0, bar);
            string body = bar < 0 ? "" : payload.Substring(bar + 1);
            if (Game.IsServer)
            {
                if (!Enabled) return;
                switch (kind)
                {
                    case "hello":
                        if (!joiners.TryGetValue(sender, out var hj)) { Send("state", "idle", sender); break; }
                        if (hj.StateSent) break; // already answered (reliable): repeated hellos are ignored
                        if (Game.Run == null || !Recorder.RunActive || Restorer.Busy) { Send("state", "wait", sender); break; }
                        Send("state", BuildState(sender), sender);
                        hj.StateSent = true;
                        break;
                    case "ready":
                        if (joiners.TryGetValue(sender, out var j) && !j.Ready)
                        {
                            j.Ready = true;
                            Plugin.Log.LogInfo($"Drop-in: {j.Name} has loaded the realm.");
                        }
                        break;
                }
            }
            else if (kind == "state") OnState(body);
            else if (kind == "early" && !stateReceived) { expectDropIn = true; OnEarly(body); }
        }
        catch (Exception ex) { Plugin.Log.LogError($"Drop-in (receive): {ex}"); }
    }

    private static void Send(string kind, string body, ulong? to = null)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || nm.CustomMessagingManager == null) return;
        string payload = body == null ? kind : kind + "|" + body;
        var writer = new FastBufferWriter(Encoding.UTF8.GetByteCount(payload) * 2 + 64, Allocator.Temp, -1);
        try
        {
            writer.WriteValueSafe(payload, false);
            ulong target = nm.IsServer ? (to ?? 0UL) : NetworkManager.ServerClientId;
            nm.CustomMessagingManager.SendNamedMessage(MessageName, target, writer, NetworkDelivery.ReliableFragmentedSequenced);
        }
        finally { writer.Dispose(); }
    }
}
