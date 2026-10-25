using System;
using HarmonyLib;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace RunSaves;

/// <summary>
/// Players who join a run in progress (drop-in) were never given a stats entry on the host: the game makes one per
/// player only when the run starts. Every shop then failed on the host while building everyone's stats ("Sequence
/// contains no matching element"), so the host's shop had no cards and the shop waited on the host forever.
/// The entry is now made when it's first needed. And as a last resort, a forced pick on a shop with no cards
/// marks that player done instead of failing.
/// </summary>
[HarmonyPatch]
internal static class StatsFix
{
    [HarmonyPatch(typeof(ServerPlayerStatsTracker), nameof(ServerPlayerStatsTracker.GetStatsFor))]
    [HarmonyPrefix]
    private static void Stats_Prefix(ServerPlayerStatsTracker __instance, ulong clientId) => Ensure(__instance, clientId);

    [HarmonyPatch(typeof(ServerPlayerStatsTracker), nameof(ServerPlayerStatsTracker.GetSpellStatsFor))]
    [HarmonyPrefix]
    private static void SpellStats_Prefix(ServerPlayerStatsTracker __instance, ulong clientId) => Ensure(__instance, clientId);

    /// <summary>Host: makes sure this player has a stats entry (drop-in players get theirs here).</summary>
    internal static void Ensure(ServerPlayerStatsTracker tracker, ulong clientId)
    {
        try
        {
            if (tracker == null || !Game.IsServer) return;
            var set = tracker.playerStatsTrackers;
            if (set == null) return;
            var e = set.GetEnumerator();
            while (e.MoveNext())
            {
                var t = e.Current;
                if (t != null && t.TrackerClientId == clientId) return;
            }
            if (Game.PlayerFor(clientId) == null) return; // not a player (yet)
            var added = new PlayerStatsTracker(clientId, tracker.serverGameplayEvents);
            set.Add(added);
            added.StartTracking();
            Plugin.Log.LogInfo($"Drop-in: started tracking stats for client {clientId}.");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Drop-in (stats for client {clientId}): {ex.Message}"); }
    }

    internal static void EnsureFor(ulong clientId)
    {
        try { Ensure(UObject.FindObjectOfType<ServerPlayerStatsTracker>(), clientId); } catch { }
    }

    // A forced pick takes the first card; with no cards the game throws and the shop never hears this player is
    // done. Tell it they're done instead.
    [HarmonyPatch(typeof(StorePanel), nameof(StorePanel.ForceChoice))]
    [HarmonyPrefix]
    private static bool ForceChoice_Prefix(StorePanel __instance)
    {
        try
        {
            var cards = __instance.activeImprovementCards;
            if (cards != null && cards.Count > 0) return true;
            var store = Game.Store;
            if (store == null) return true;
            Plugin.Log.LogWarning("Shop: time ran out with no cards on this screen; marking this player done so the shop can close.");
            store.SetChoiceCompleted();
            return false;
        }
        catch { return true; }
    }
}
