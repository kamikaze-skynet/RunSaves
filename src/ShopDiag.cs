using System;
using HarmonyLib;
using UnityEngine;

namespace RunSaves;

/// <summary>
/// Diagnostics for drop-in catch-up shops: while a catch-up runs (or for 10 minutes after someone drops in), every
/// shop event is logged with the game's own call stack, so a shop that closes on its own shows what closed it.
/// Logging only; nothing is changed.
/// </summary>
[HarmonyPatch]
internal static class ShopDiag
{
    private static bool On => CatchUp.Watching || DropIn.Recent;

    private static void Log(string what, bool stack = false)
    {
        try
        {
            if (!On) return;
            string role = Game.IsServer ? "host" : "client " + Game.LocalClientId();
            string msg = $"Shop [{role}, t={Time.unscaledTime:0.0}]: {what}";
            if (stack && false) // capturing the game's call stack here is risky (suspected crash); events only
            {
                var st = StackTraceUtility.ExtractStackTrace();
                var lines = st.Split('\n');
                var keep = new System.Text.StringBuilder();
                int n = 0;
                foreach (var l in lines)
                {
                    if (l.Contains("ShopDiag") || l.Contains("HarmonyLib") || l.Contains("DMD<") || string.IsNullOrWhiteSpace(l)) continue;
                    keep.Append("\n    ").Append(l.Trim());
                    if (++n >= 8) break;
                }
                msg += keep.ToString();
            }
            Plugin.Log.LogInfo(msg);
        }
        catch { }
    }

    private static int Cards(StorePanel p) { try { return p.activeImprovementCards?.Count ?? -1; } catch { return -1; } }

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.EnterStoreRpc))] [HarmonyPrefix]
    private static void EnterStore(StoreType storeType, int partyLevel) => Log($"enter {storeType} shop (party level {partyLevel})");

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.ExitStoreRpc))] [HarmonyPrefix]
    private static void ExitStore() => Log("exit shop", true);

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.CloseStoreRpc))] [HarmonyPrefix]
    private static void CloseStore() => Log("close shop", true);

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.ShutdownOnClientsRpc))] [HarmonyPrefix]
    private static void Shutdown() => Log("shut down shop", true);

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.ForceChoiceOnClientsRpc))] [HarmonyPrefix]
    private static void ForceOnClients() => Log("forced pick (from host)", true);

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.KickPlayer))] [HarmonyPrefix]
    private static void Kick() => Log("kick player", true);

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.ChooseShopItem))] [HarmonyPrefix]
    private static void Choose() => Log("item chosen", true);

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.SetChoosingPlayersRpc))] [HarmonyPostfix]
    private static void Choosing(StoreManager __instance)
    {
        try
        {
            var left = __instance.GetPlayersLeftToChoose();
            var ids = new System.Collections.Generic.List<string>();
            if (left != null) for (int i = 0; i < left.Count; i++) ids.Add(left[i].ToString());
            Log($"still choosing: [{string.Join(",", ids)}], countdown {(__instance.isCountdownTimerActive ? "on" : "off")}");
        }
        catch { }
    }

    [HarmonyPatch(typeof(StorePanel), nameof(StorePanel.SetOptions))] [HarmonyPostfix]
    private static void Options(StorePanel __instance) => Log($"cards shown: {Cards(__instance)}");

    [HarmonyPatch(typeof(StorePanel), nameof(StorePanel.ChooseCard))] [HarmonyPrefix]
    private static void ChooseCard() => Log("card chosen on this screen", true);

    [HarmonyPatch(typeof(StorePanel), nameof(StorePanel.SetAsWaitingForOtherPlayers))] [HarmonyPrefix]
    private static void Waiting(StorePanel __instance) => Log($"screen switched to 'waiting for others' ({Cards(__instance)} cards)", true);

    [HarmonyPatch(typeof(StorePanel), nameof(StorePanel.ForceChoice))] [HarmonyPrefix]
    private static void ForceChoice(StorePanel __instance) => Log($"forced pick on this screen ({Cards(__instance)} cards)", true);
}
