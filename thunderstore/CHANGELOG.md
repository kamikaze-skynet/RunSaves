# Changelog

## 1.1.0
- Autosave always runs, at the start of every wave in every run, including restored ones. It no longer turns itself off when an older save exists. The previous save is kept as `save.prev.json`.
- The save is now only deleted after a **win**. After a loss you can resume from the last autosave and try again. New setting `ClearSaveWhen` (Won / WonOrLost / Never) replaces `ClearSaveWhenRunEnds`.
- Messages are smaller and sit in the top-right corner.

## 1.0.2
- The lobby's saved-run panel now sits on the right side with stacked buttons, out of the way of the mission bar. New setting `LobbyPanelSide` (Right / Left).

## 1.0.1
- Fix: the mod didn't load ("Error loading [RunSaves]"), so there was no Save Run button and no lobby panel. It hooked a RunManager method the game doesn't have.

## 1.0.0
- First release: save a run from the pause menu or by autosave each wave, then resume it from the lobby. Restores the world, wave, party level and XP, revives, and every player's spells and upgrades.
