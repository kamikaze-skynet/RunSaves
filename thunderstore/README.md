# RunSaves

Save a run and pick it up later: after quitting, a crash, or restarting the game. Works solo and in multiplayer.

## Saving (host)

- **Save Run** in the pause menu.
- **Autosave** at the start of every wave, in every run, including restored ones, so a crash costs at most one wave. If a level-up shop is open, it waits until everyone has picked. The previous save is kept as a backup.
- A restored run keeps saving like any other, so you can play a run across as many sessions as you like.
- **Saves are never deleted automatically**, not even after a win or a loss. Only the **Delete** button in the lobby removes one.
- Optional save key (off by default, see config).

A save holds (in addition to the list below, each artifact's stacks and how many revives each player has performed):

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
| Saving | AutosaveIntervalSeconds | 60 | Also save quietly every this many seconds (0 = off). |
| Saving | SaveOnQuit | true | Save when the host quits the run from the pause menu. |
| Saving | ShowAutosaveMessage | true | Show a short message on autosave. |
| Saving | PauseMenuButton | true | Add Save Run to the pause menu. |
| Saving | SaveKey | (empty) | Optional key to save, e.g. F5. |
| Restoring | LobbyPrompt | true | Show the saved run in the lobby. |
| Restoring | LobbyPanelSide | Right | Which side of the lobby the panel sits on (Right / Left). |
| Restoring | PanelKey | F10 | Show or hide the panel. Hide also leaves a small "Saved Run" tab to click. |

The save is stored in `BepInEx/config/RunSaves/save.json`, and the one before it is kept as `save.prev.json`.

Works with MoreWizards, StarterSpells, TripleInfusion, ReviveRegen and BetterNetcode.

## Run setup

Saves remember the realm, the threat level (difficulty), and the Harmony, Eternity and single-spell modifiers. When the host presses **Resume** in the lobby, the lobby is switched to those settings.

## Drop-in joining (experimental)

Friends can join a run that's already going, from a Steam invite or "Join game". Normally the game turns joining off once a run starts; with this on, the lobby stays joinable (but not listed for matchmaking) and the host lets them in.

- Their game loads the realm and is caught up on the others: who each player is, everyone's upgrades, the party level and revives.
- Their wizard appears next to another player.
- A new player then gets catch-up picks (below). Someone who was in this run before and dropped out gets their old build back instead.
- The player cap still applies (MoreWizards' `MaxPlayers`, or the game's 4).

Everyone needs RunSaves for this. Turn it off with `[Drop-in] Enabled`.

## Catch-up for new players

If someone joins a restored run and the save has no build for them, or drops into a run in progress, they get catch-up shops. The party level is shared, so they're already at the party's level; the shops give them a build to match:

- **Spells** until they have as many as the others.
- **Artifacts**, about as many as the others have.
- **Upgrades**, up to `UpgradePicks` (15) of them. Each pick is applied several times, so the total matches the levels they missed. For example, joining at level 110 means 15 picks that each count about 7 times.

The game stays paused for everyone from the first catch-up shop to the last, so nobody fights while the new player builds up. Only the new player can pick in these shops; everyone else sees "waiting for other players". There is no countdown or kick, and the game resumes on its own when they're done.

| `[Drop-in]` setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Host: let friends join a run that's already going. |

| `[Catch-up]` setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Turns catch-up shops on or off. |
| `UpgradePicks` | `15` | The most upgrade picks a new player gets. |
| `Artifacts` | `true` | Also gives them artifact picks. |

## Multiplayer settings

In multiplayer everyone plays with the **host's** settings: the host's config is sent to every player from the lobby on, and the mod reads it instead of yours while you're connected. Your own messages, the save button and key, and the lobby panel stay yours. Your config file is never changed. If the host doesn't have this mod, it stays off for you in that game.
