using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace RunSaves;

/// <summary>Finding the game's run objects, players and their spells.</summary>
internal static class Game
{
    private static RunManager run;
    private static PartyLevelManager party;
    private static ReviveManager revives;
    private static ImprovementsManager improvements;
    private static StoreManager store;

    private static T Find<T>(ref T cache) where T : UObject
    {
        if (cache is not null && !cache.WasCollected && cache != null) return cache; // WasCollected first: == throws on a collected wrapper
        cache = UObject.FindObjectOfType<T>();
        return cache;
    }

    internal static RunManager Run => Find(ref run);
    internal static PartyLevelManager Party => Find(ref party);
    internal static ReviveManager Revives => Find(ref revives);
    internal static ImprovementsManager Improvements => Find(ref improvements);
    internal static StoreManager Store => Find(ref store);

    /// <summary>A level-up (or other) shop is open or queued. Its level counts already, but its pick isn't made yet.</summary>
    internal static bool StoreBusy
    {
        get
        {
            try
            {
                var s = Store;
                if (s == null) return false;
                return s.showStoresCoroutine is not null || s.IsShopPanelActive || s.IsExitingStore;
            }
            catch { return false; }
        }
    }

    internal static void ForgetObjects()
    {
        run = null; party = null; revives = null; improvements = null; store = null;
    }

    internal static bool Alive(UObject o) => o is not null && !o.WasCollected && o != null;

    internal static ulong LocalClientId()
    {
        try { var nm = NetworkManager.Singleton; return nm != null ? nm.LocalClientId : ulong.MaxValue; } catch { return ulong.MaxValue; }
    }

    internal static bool IsServer
    {
        get { try { var nm = NetworkManager.Singleton; return nm != null && nm.IsListening && nm.IsServer; } catch { return false; } }
    }

    internal static bool IsClientOnly
    {
        get { try { var nm = NetworkManager.Singleton; return nm != null && nm.IsListening && !nm.IsServer; } catch { return false; } }
    }

    internal static List<GameplayPlayerIdentity> Players()
    {
        var list = new List<GameplayPlayerIdentity>();
        foreach (var p in UObject.FindObjectsOfType<GameplayPlayerIdentity>())
            if (p != null && p.IsSpawned) list.Add(p);
        list.Sort((a, b) => a.OwnerClientId.CompareTo(b.OwnerClientId));
        return list;
    }

    internal static GameplayPlayerIdentity PlayerFor(ulong clientId)
    {
        foreach (var p in UObject.FindObjectsOfType<GameplayPlayerIdentity>())
            if (p != null && p.IsSpawned && p.OwnerClientId == clientId) return p;
        return null;
    }

    internal static GameplayPlayerIdentity LocalPlayer()
    {
        foreach (var p in UObject.FindObjectsOfType<GameplayPlayerIdentity>())
            if (p != null && p.IsSpawned && p.IsOwner) return p;
        return null;
    }

    /// <summary>Stable id for a player across sessions: their Steam/Epic id, else their name.</summary>
    internal static string KeyOf(GameplayPlayerIdentity p)
    {
        try
        {
            string id = p.PlatformPlayerId;
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }
        catch { }
        return "name:" + NameOf(p);
    }

    internal static string NameOf(GameplayPlayerIdentity p)
    {
        try { var n = p.GetDisplayName(); if (!string.IsNullOrWhiteSpace(n)) return n; } catch { }
        return $"Player {p.OwnerClientId}";
    }

    internal static string CharacterOf(GameplayPlayerIdentity p)
    {
        try { var c = p.Character; if (c != null) return c.Id.ToString(); } catch { }
        return "";
    }

    internal static List<SpellType> SpellsOf(GameplayPlayerIdentity p)
    {
        var spells = new List<SpellType>();
        try
        {
            var list = p.spellEquipper?.equippedSpells;
            if (list != null)
                for (int i = 0; i < list.Count; i++) spells.Add(list[i].SpellType);
        }
        catch { }
        return spells;
    }

    internal static string WorldName(RunManager rm)
    {
        try
        {
            var cfg = rm.Configuration;
            if (cfg == null) return "Unknown world";
            if (cfg == rm.verdantMeadowsConfiguration) return "Verdant Meadows";
            if (cfg == rm.frozenAncientsConfiguration) return "Frozen Ancients";
            if (cfg == rm.pyrestormPitConfiguration) return "Pyrestorm Pit";
            if (cfg == rm.astralPlanesConfiguration) return "Astral Planes";
            return cfg.name;
        }
        catch { return "Unknown world"; }
    }

    internal static string ConfigName(RunManager rm)
    {
        try { return rm.Configuration?.name ?? ""; } catch { return ""; }
    }

    internal static bool InMainMenu()
    {
        try { return UObject.FindObjectOfType<MainMenuManager>() != null && Run == null; } catch { return false; }
    }

    internal static bool TryParse<T>(string s, out T value) where T : struct, Enum => Enum.TryParse(s, out value);

    // ---------- artifact stacks ----------

    internal static PlayerArtifacts ArtifactsOf(GameplayPlayerIdentity p)
    {
        try { return p.Artifacts?.TryCast<PlayerArtifacts>(); } catch { return null; }
    }

    /// <summary>Stacks of each artifact this player has (e.g. per revive, per objective, per health pickup).</summary>
    internal static Dictionary<string, float> ArtifactStacksOf(GameplayPlayerIdentity p)
    {
        var d = new Dictionary<string, float>();
        try
        {
            var values = ArtifactsOf(p)?.artifactValues;
            if (values == null) return d;
            for (int i = 0; i < values.Count; i++)
            {
                var v = values[i];
                if (v == null) continue;
                float stacks = v.Stacks;
                if (stacks != 0f) d[v.ArtifactType.ToString()] = stacks;
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Reading artifact stacks: {ex.Message}"); }
        return d;
    }

    /// <summary>Sets saved stacks on this player's artifacts (on this game). Returns how many were set.</summary>
    internal static unsafe int SetArtifactStacks(GameplayPlayerIdentity p, Dictionary<string, float> saved)
    {
        int set = 0;
        if (saved == null || saved.Count == 0) return 0;
        var pa = ArtifactsOf(p);
        var values = pa?.artifactValues;
        if (values == null) return 0;
        for (int i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (v == null || !saved.TryGetValue(v.ArtifactType.ToString(), out float stacks)) continue;
            // Stacks only has a getter on the interface; each artifact value keeps it in an auto-property field.
            IntPtr obj = v.Pointer;
            IntPtr field = IntPtr.Zero;
            for (IntPtr k = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(obj); k != IntPtr.Zero && field == IntPtr.Zero;
                 k = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_parent(k))
                field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(k, "<Stacks>k__BackingField");
            if (field == IntPtr.Zero) continue;
            int offset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(field);
            *(float*)((byte*)obj + offset) = stacks;
            try { pa.NotifyStackChanged(v.ArtifactType, stacks); } catch { } // the HUD's artifact stack display
            set++;
        }
        return set;
    }

    internal static int RevivesPerformedOf(GameplayPlayerIdentity p)
    {
        try { return p.RevivesPerformed; } catch { return 0; }
    }
}
