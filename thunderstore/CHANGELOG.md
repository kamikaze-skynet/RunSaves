## 1.5.7
- Drop-in: the last spell still failing every frame on the new player's game (the Proximity spell, from its own setup that never ran) is now finished too.

## 1.5.6
- **Drop-in: fixed the remaining error flood on the new player's game.** Spells the other players already had spawned on the new player's game before those players were set up there, so they never finished setting up and failed every frame (thousands of errors, and stutter). They're finished once the players are known.
- **Catch-up spell picks are off by default** (new setting `[Catch-up] SpellPicks`). From party level 5 a new player got a spell pick, and the host's game crashed afterwards. Without it, new players get upgrade picks and their next spells as they level.

## 1.5.5
- Smoother: player lookups (several every frame) searched the whole scene each time; they're now cached for half a second.

## 1.5.4
- **Drop-in: fixed a flood of errors on the new player's game** (tens of thousands of "key not present" errors). The players already in the run had equipped their spells before the new player arrived, so the new player's game never added those spells to their stats, and their spell objects (including ones they don't have) failed every frame. The new player's game now adds the equipped spells and switches off the unused spell objects (they come back if that player equips them later).

## 1.5.3
- Removed two diagnostics added in 1.4.8–1.4.9 that captured the game's call stacks (one for every error, one on shop events). They're the main suspect for the game crashing shortly after a shop closed with a joined player. Shop events are still logged, without the call stacks.

## 1.5.2
- **Fixed: the game stayed frozen after the catch-up finished.** A shop puts the game speed back to what it was when the shop opened, which during a catch-up was paused, so the last catch-up shop froze everyone again as it closed. After a catch-up ends, every game now makes sure it's running again.

## 1.5.1
- **Fixed: only the first catch-up shop opened** ("1/3", then nothing). The catch-up pause held the game frozen while the shop was closing, so the game's shop sequence never finished and the next shop never came. The pause now only holds between shops; each shop pauses the game itself.
- Removed the 1.4.9 guard on the new player's pick (not needed now, and it could ignore a very quick pick).

## 1.5.0
- **Fixed: the catch-up shop closed the moment it opened.** The game's "who has chosen" list was being told the new player had already picked, so their shop closed at once, then everyone else was skipped and it closed for all. It now lists everyone else as done, so the shop waits for the new player only.

## 1.4.9
- **Drop-in: the catch-up shop can't be closed out from under the new player.** The host's game sometimes counted the new player as having picked the moment their catch-up shop opened, so it closed for everyone before they could choose. The host now ignores that until they've really picked (the time-out and the host's cancel key still end it).
- Better error reports in the log: errors that repeat every frame are now reported once, with where they come from.

## 1.4.8
- Drop-in diagnostics: while a catch-up runs, or for 10 minutes after someone drops in, every shop event (opened, cards shown, card chosen, forced pick, 'waiting for others', closed) is written to the log with what caused it. Nothing else changes.

## 1.4.7
- **Drop-in: the catch-up shop now waits for the new player.** The game starts its normal shop countdown the moment it knows who's choosing, then picks for anyone who hasn't, so the new player's shop disappeared before they could pick. During a catch-up shop that countdown is held: it stays open for the new player (up to `[Catch-up] PickTimeoutSeconds`, and the host's cancel key still works), and everyone else is skipped automatically.

## 1.4.6
- **Drop-in: the catch-up shop no longer vanishes on the new player's screen.** Their game finishes starting the run a moment after their wizard appears, which closed a shop that was already open, so everyone sat frozen until the time-out. The host now waits until the new player's game says it has started.
- If the new player still doesn't pick in time, the game picks a card for them (as when a normal shop's time runs out) and the catch-up ends within a few seconds, instead of freezing everyone for another 30 s.

## 1.4.5
- **Fixed the host's shop breaking when someone drops in.** The game gives each player a stats entry only when the run starts, so a player who joined mid-run had none. Every shop then failed on the host's game: the joiner got cards, the host got none, and the shop sat "waiting" on the host forever. Joiners now get their stats entry when they arrive.
- Safety net: if a shop's time runs out on a screen with no cards, that player is marked done so the shop can close instead of hanging.

## 1.4.4
- **Drop-in: other players' spell upgrades now carry over.** When you joined mid-run, your game tried to apply another player's spell upgrades before it knew they had that spell, so those upgrades were lost (and the log filled with "key not present" errors). They now wait until the spell is there.

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
