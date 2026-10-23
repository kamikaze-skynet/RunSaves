# RunSaves

A [BepInEx](https://github.com/BepInEx/BepInEx) IL2CPP mod for **The Spell Brigade**: save a run and continue it after quitting, a crash or a restart, solo or in multiplayer.

See [thunderstore/README.md](thunderstore/README.md) for the player-facing description and config.

## How it works

The game's own crash recovery (`RunProgressCaretaker` / `RunProgressMemento`) only keeps the character, play time, gold and rank progress, so it can't resume a run. This mod records the run as it happens and rebuilds it.

**Recording (host, `Recorder`).** Every build change goes through one of `ImprovementsManager`'s universal RPCs: `SendCharacterStatModifiersRpc`, `SendSpellStatModifierRpc`, `SendSpellInfusionRpc`, `SendSpellVariantRpc`, `SendArtifactRpc` and `SendFinalUpgradeRpc`. The host runs each of them for every player, so it logs each one (at the execute stage) per player key, which is `GameplayPlayerIdentity.PlatformPlayerId`. Two things are skipped:
- curses, which the game re-applies from the lobby difficulty;
- each player's starting stats, sent by their own game when their wizard spawns. Character stat RPCs from a player in the 10 seconds after their `InitializeOnClientsRpc` are ignored.

**Saving (`Saver`).** A save is written from the pause-menu button, or by autosave in a `RunManager.StartWave` postfix. It holds the run (world config, `CycleNumber`, `CurrentWave`, run and cycle time), `PartyLevelManager.level` and `currentExperience`, `ReviveManager.CurrentRevives`, and each player's equipped spells (`SpellEquipper.equippedSpells`) and logged upgrades. It's JSON, stored in `BepInEx/config/RunSaves/save.json`.

**Restoring (`Restorer`).**
1. A `StartRunOnServerRpc` prefix checks the world matches. A prefix on the wave coroutine's `MoveNext` (`RunManager._StartWaves_d__53`) then sets up its first step as if the previous wave just ended (`state = 1`, `index = wave - 2`, `CurrentWave = wave - 1`). The game's own "next wave" step then starts the saved wave. The run clock is set to that wave's start, and elites from before it are added to `eliteEnemiesThatHaveBeenSpawned`.
2. Once every wizard has spawned, the mod matches players by platform id (else by wizard). The host re-equips its own spells. Clients get a named message, since `equippedSpells` is owner-write.
3. Once the host sees the spells, plus a short settle delay, the mod replays the logged upgrades through the same RPCs (10 per frame). Upgrades for spells that weren't re-equipped are skipped.
4. Party level (`level` + `SetLevelRpc`), XP and revives are set last.

## Building

Requires the .NET 6 SDK and a game install with BepInEx IL2CPP 6.0.0-be.788 that has been run once.

```powershell
.\package.ps1 -GameDir "D:\SteamLibrary\steamapps\common\The Spell Brigade"
```

## License

MIT, see [LICENSE](LICENSE).
