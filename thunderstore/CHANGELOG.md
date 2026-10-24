## 1.3.0
- **Run setup is saved.** Saves now store the threat level (difficulty), Harmony (peaceful mode), Eternity (endless mode) and single-spell mode along with the realm. Pressing **Resume** in the lobby sets all of them for you, and the saved-run panel shows the difficulty.
- **Catch-up picks for new players.** Someone who joins a restored run without a build in the save gets shops that only they pick from, until they match the party:
  - spell shops until they have as many spells as the others,
  - artifact shops (about as many as the others have),
  - up to 15 upgrade picks, where each pick counts as several level-ups so the total matches the levels they missed.
  Everyone else's shop shows "waiting for other players" while this happens. There's no countdown or kick during catch-up shops. Settings are in `[Catch-up]`.

# Changelog

## 1.2.2
- Fix: once hidden, the lobby's saved-run panel couldn't be brought back. Hide now folds it into a small "Saved Run" tab on the same side; click it (or press F10, setting `PanelKey`) to open it again.

## 1.2.1
- Saves are written to disk in the background, so the game never waits for the disk while saving.

## 1.2.0
- Artifact stacks are saved and restored: artifacts that grow during a run (per revive, per objective, per health pickup, per infusion, per common upgrade) keep their stacks. Revives performed are kept too.
- Saves when the host quits the run from the pause menu, and quietly every 60 seconds (`AutosaveIntervalSeconds`), so quitting mid-wave keeps the upgrades and levels from that wave. A restore still starts at the beginning of the saved wave.
- Fix: Save Run was missing from the pause menu in every run after the first one in a session, including resumed runs.

## 1.1.1
- Fix: Save Run was missing from the pause menu in every run after the first one in a session (including resumed runs). The button belonged to the previous run's menu, which the game destroys. It's now added to each new pause menu.

## 1.1.0
- Autosave always runs, at the start of every wave in every run, including restored ones. It no longer turns itself off when an older save exists. The previous save is kept as `save.prev.json`.
- Saves are no longer deleted automatically when a run is won or lost. Only the lobby's **Delete** button removes one, so after a loss you can resume from the last autosave and try again. The `ClearSaveWhenRunEnds` setting is gone.
- Messages are smaller and sit in the top-right corner.

## 1.0.2
- The lobby's saved-run panel now sits on the right side with stacked buttons, out of the way of the mission bar. New setting `LobbyPanelSide` (Right / Left).

## 1.0.1
- Fix: the mod didn't load ("Error loading [RunSaves]"), so there was no Save Run button and no lobby panel. It hooked a RunManager method the game doesn't have.

## 1.0.0
- First release: save a run from the pause menu or by autosave each wave, then resume it from the lobby. Restores the world, wave, party level and XP, revives, and every player's spells and upgrades.
