# Changelog

All notable changes to this project are documented here. The format is loosely
based on [Keep a Changelog](https://keepachangelog.com/).

## [0.1.1-beta] — 2026-07-01

First beta. 2-player co-op over Steam P2P — no separate server, free.

### Added
- Steam lobby + P2P session; host→client save transfer (both play one world)
- Position, animation, and time-of-day sync; player-leave detection
- Tree & stone chopping, mine-vein loot, foraging, farm garden beds (shared loot)
- Graves: digging stages, reburying a body, rebuilding, and **repair**
- Carried corpse with position mirroring and ownership handoff
- Building: placement → construction → demolition
- Co-op workstation crafting: shared queues, station-owner arbitration, live progress bar
- Inventory: shared chests (op-based, concurrent editing), stockpiles, item exchange
- Host-authoritative weather
- Personal 2nd-character save + personal story layer (quest journal, known NPCs)
- `ReliableNet` — a custom TCP-lite reliability layer over Steam-unreliable P2P
  (Steam's own reliable send is broken on this SDK)
- `DebugMode` config gating developer keybinds and verbose diagnostics (off by default)

### Known issues
- Rare: both players grabbing the very last item of one stack in a chest at the
  same instant can duplicate 1 item
- NPC villagers and dungeon mobs/zombies are not synced yet

[0.1.1-beta]: https://github.com/Zonda001/graveyard-keeper-multiplayer/releases/tag/v0.1.1-beta
