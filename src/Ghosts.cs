using System;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace RunSaves;

/// <summary>
/// A player whose connection drops while they're joining (the joining game sometimes reconnects straight away, as a
/// new client) used to linger as a ghost player: when their player was removed, the game's party list at the top
/// left failed on them ("Sequence contains no matching element") because they had no portrait yet, and that error
/// cut the rest of the clean-up short. The game kept counting them, so every shop waited for them forever
/// ("Waiting for other players to pick...").
///   - The party list now skips a player it has no portrait for, so the clean-up runs to the end.
///   - As a safety net, the host passes, in any open shop, anyone it's waiting for who isn't connected.
/// </summary>
[HarmonyPatch]
internal static class Ghosts
{
    [HarmonyPatch(typeof(GameplayPartyOverviewUI), nameof(GameplayPartyOverviewUI.RemovePlayerOverview))]
    [HarmonyPrefix]
    private static bool RemoveOverview_Prefix(GameplayPartyOverviewUI __instance, GameplayPlayerIdentity identity)
    {
        try
        {
            if (identity == null) return true;
            var list = __instance.playerOverviews;
            if (list == null) return true;
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                if (o != null && o.Identity != null && o.Identity.Pointer == identity.Pointer) return true;
            }
            // No portrait for this player (they left before it was made): nothing to remove. Running the game's own
            // code here would throw and stop the rest of the player's removal.
            if (identity.IsOwner) return true; // the local player has none either; the game handles that itself
            Plugin.Log.LogInfo($"Drop-in: client {identity.OwnerClientId} left before their portrait was made; removing them cleanly.");
            return false;
        }
        catch { return true; }
    }

    private static float nextCheck;

    /// <summary>Host, every frame (cheap: looks every 2 s while a shop is open).</summary>
    internal static void Tick()
    {
        if (!Game.IsServer || !Game.StoreBusy || Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 2f;
        try
        {
            var nm = NetworkManager.Singleton;
            var store = Game.Store;
            if (nm == null || store == null) return;
            var left = store.GetPlayersLeftToChoose();
            if (left == null) return;
            for (int i = 0; i < left.Count; i++)
            {
                ulong id = left[i];
                if (id == nm.LocalClientId) continue;
                bool connected;
                try { connected = nm.ConnectedClients.ContainsKey(id); } catch { connected = true; }
                if (connected) continue;
                Plugin.Log.LogWarning($"Shop: client {id} isn't connected any more; passing for them so the shop can close.");
                store.SetChoiceCompletedOnServerRpc(id);
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Shop (left players): {ex.Message}"); }
    }
}
