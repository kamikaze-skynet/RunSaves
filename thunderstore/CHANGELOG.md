## 1.4.3
- **Catch-up shops only wait for the player catching up.** Everyone else passes automatically as soon as the shop opens, so nobody has to sit there waiting with nothing to pick.

## 1.4.2
- **Catch-up can't freeze the game any more.** If a catch-up stops making progress (the joiner's shop is done but something else is stuck), the host ends it after about two minutes: any open shop is closed and everyone's game resumes. The host can also end it at once with **Delete** (`[Catch-up] CancelKey`).
- The game's own shops (a level-up that happened right before the pause) now wait until the catch-up is over, instead of mixing in with the catch-up shops. Mixed-in shops were what left the host unable to pick.
- Drop-in: the joiner's game now uses the host's run settings and **the host's map layout**. Each realm has several layouts, picked from a random number the host shares before the run, which a joiner never got. A different layout broke dozens of objects on the joiner's side.
- Drop-in: the party level is set directly on the joiner's game (replaying the game's own level message errored there).

## 1.4.1
- **The game can't stay paused forever any more.** If a player hasn't picked in a catch-up shop after 90 seconds, the shop closes and the rest of their catch-up is skipped. New setting `[Catch-up] PickTimeoutSeconds` (0 = wait forever).
- Drop-in: catch-up picks only start once the joiner's wizard is actually in the game. If it doesn't show up within 30 seconds, there are no catch-up picks, so nobody is frozen.
- More detail in the log for drop-in joins: the join request, each player being set up on every computer, and the game's own errors with where they came from.

## 1.4.0
- **Drop-in joining (experimental).** Friends can join a run that's already going, from a Steam invite or "Join game", without waiting for the next run. Their game loads the realm and gets caught up on everyone else (who they are, their upgrades, the party level), then their wizard appears next to the others.
  - A new player then gets catch-up picks in the paused catch-up session (spells, artifacts, upgrades).
  - Someone who was in this run before (crashed or left) gets their old build back instead.
  - The lobby stays joinable during the run and isn't shown as "game in progress". It still isn't listed for matchmaking. The player cap (MoreWizards' `MaxPlayers`, or 4) still applies.
  - Everyone needs RunSaves 1.4.0. New setting `[Drop-in] Enabled` (on).

## 1.3.4
- **Everyone uses the host's settings.** While you're in someone's game, this mod reads the host's config instead of yours, from the lobby on. Your own messages, the save button and key, and the lobby panel stay yours. Your config file isn't changed, and your own values are back when you leave. If the host doesn't have the mod, it stays off for you in that game.

## 1.3.3
- **The game pauses during catch-up.** When a new player joins a restored run, everyone's game stays paused while they pick all their catch-up shops (spells, artifacts, upgrades), then resumes by itself. Before, the game ran between shops.
- Catch-up spell shops are back with MoreSpellSlots installed too, so all of a new player's picks happen in that one paused session.

## 1.3.2
- Fixed the check that keeps RunSaves' catch-up shops from overlapping with MoreSpellSlots' owed-spell shops.

## 1.3.1
- With MoreSpellSlots installed, catch-up spells come from its owed-spell shops instead of RunSaves', so nobody gets spells twice. Only one player-only shop opens at a time.

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
