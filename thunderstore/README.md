# RunSaves

Save a run and pick it up later: after quitting, a crash, or restarting the game. Works solo and in multiplayer.

## Saving (host)

- **Save Run** in the pause menu.
- **Autosave** at the start of every wave, in every run, including restored ones, so a crash costs at most one wave. If a level-up shop is open, it waits until everyone has picked. The previous save is kept as a backup.
- A restored run keeps saving like any other, so you can play a run across as many sessions as you like.
- **Saves are never deleted automatically**, not even after a win or a loss. Only the **Delete** button in the lobby removes one.
- Optional save key (off by default, see config).

A save holds:

- the world, loop and wave;
- the party level and XP;
- revives;
- every player's spells and upgrades: stat upgrades, spell upgrades, infusions, spell variants, artifacts and final upgrades.

## Restoring (host)

1. In the lobby, a **Saved Run** panel on the right shows the world, wave, level and players. Press **Resume**.
2. Everyone rejoins and picks **the same wizards** as before.
3. Start a run in **the same world**.

The run jumps to the **start of the saved wave**. Each player's spells are re-equipped, then every upgrade is replayed through the game's own upgrade system, so builds come back exactly as they were. The party level, XP and revives are set last. It takes a few seconds at the start of the run.

Players are matched by their Steam/Epic account. Someone on a different account gets the build saved for the same wizard.

## What isn't saved

- **The moment itself.** Enemies on screen, an objective halfway done and orbs on the ground aren't saved. The saved wave starts fresh.
- Health (everyone starts full), and gold picked up during the run.
- **Curses and starting stats.** These aren't saved because the game applies them again when the run starts, so pick the same difficulty.

## Multiplayer

**Everyone needs the mod.** A player's equipped spells can only be changed by their own game, so the host asks each player's game to re-equip them. If someone doesn't have the mod, their stats and artifacts still come back but their extra spells (and those spells' upgrades) don't.

## Config

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | Enabled | true | Turn saving and restoring on or off. |
| Saving | AutosaveEachWave | true | Save when each new wave starts. |
| Saving | ShowAutosaveMessage | true | Show a short message on autosave. |
| Saving | PauseMenuButton | true | Add Save Run to the pause menu. |
| Saving | SaveKey | (empty) | Optional key to save, e.g. F5. |
| Restoring | LobbyPrompt | true | Show the saved run in the lobby. |
| Restoring | LobbyPanelSide | Right | Which side of the lobby the panel sits on (Right / Left). |

The save is stored in `BepInEx/config/RunSaves/save.json`, and the one before it is kept as `save.prev.json`.

Works with MoreWizards, StarterSpells, TripleInfusion, ReviveRegen and BetterNetcode.
