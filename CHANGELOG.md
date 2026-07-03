# Changelog

All notable changes to this project are documented here. The format is loosely
based on [Keep a Changelog](https://keepachangelog.com/).

## [0.1.2-beta] — 2026-07-03

### Added
- **Self-healing network layer.** The mod now detects a dead sync channel — the state
  where you still see each other move but items, stations and world changes stop
  syncing — and repairs it automatically within ~15 seconds, then re-syncs affected
  world objects. Only if automatic repair fails do both players get an on-screen
  warning with instructions, instead of unknowingly playing desynced for hours
- On-screen message when `F11` is pressed with the Steam Overlay disabled: the
  invite window can't open in that case (Steam silently ignores the call), so the
  mod now explains it and points to the overlay-free path — joining via the Steam
  friends list (right-click the host → Join Game)
- README: "F11 does nothing" troubleshooting section

### Changed
- Network protocol version 2 → 3 (reliability framing now carries a stream-generation
  byte). As before, both players must run the same mod version — mismatched versions
  are refused at join with an on-screen message

### Fixed
- Stale packets from before a connection reset can no longer be delivered into the
  new stream (silent world-state corruption)
- World objects your partner is actively working with (a running craft, an open chest)
  are no longer overwritten during post-repair reconciliation (was: "−1" item artifacts)

### Thanks
- **ELance262** and their co-op partner — the detailed Nexus bug report that led to the
  self-healing transport, and a model example of asking the right questions before
  sharing logs

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

[0.1.2-beta]: https://github.com/Zonda001/graveyard-keeper-multiplayer/releases/tag/v0.1.2-beta
[0.1.1-beta]: https://github.com/Zonda001/graveyard-keeper-multiplayer/releases/tag/v0.1.1-beta
