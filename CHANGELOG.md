# Changelog

All notable changes to this project are documented here. The format is loosely
based on [Keep a Changelog](https://keepachangelog.com/).

## [Unreleased]

### Fixed
- **`F11` doing nothing at all, with no explanation anywhere on screen.** If Steam never
  finished starting up, pressing `F11` wrote a line into the log file and gave up silently, so
  the only thing the player saw was a key that didn't work — and several people reasonably
  concluded the mod was broken. `F11` now opens an on-screen message instead: either "Steam is
  still starting up, try again in a few seconds", or, if Steam gave up entirely, the actual fix —
  create `steam_appid.txt` next to `Graveyard Keeper.exe` containing `599140`, then restart.
  The startup log line says the same thing now. Root cause diagnosed by **Winstonnn002** on
  Nexus, who found it on his own after the standard overlay-and-reinstall advice got him nowhere

## [0.1.4-beta] — 2026-08-10

### Fixed
- **The game hanging on the loading screen when you leave to the main menu.** Same dead entry as
  the freeze below, but through a different door: on the way out the game deletes every dropped
  item from the scene, and it does that without checking whether the item is still there. One dead
  entry threw, which aborted the shutdown halfway — and the step that hides the loading overlay
  never ran, so the game sat on that screen forever. Nothing was wrong with your save: the crash
  happened before anything was written to disk. Found while testing 0.1.4, so it never reached a
  release with the other fixes
- **The saving screen freeze / sleep freeze** (thanks to robinbodar for attaching a log — it
  found this). When your partner picked something up, the mod deleted that item from your
  world but left a dead entry behind in the game's internal drop list. From then on the game
  crashed while writing the save, so the save never finished and the game hung on the saving
  screen — which is what you saw when going to sleep. The same dead entry also broke character
  interaction: some players suddenly couldn't build, repair or talk to NPCs, with no way to
  recover. Both symptoms had the same cause and are fixed. A safety sweep now also runs before
  every save, so a session that already went bad repairs itself instead of hanging
- **Stuck indoors with "Not yet" on every exit** (reported by drromaine1). The mod was
  syncing the game's temporary cutscene teleport-lock between players, so if the connection
  dropped at the wrong moment the value stayed stuck in the other player's save permanently —
  and it followed them back into singleplayer, where removing the mod didn't help because the
  flag lives in the save file. The mod no longer syncs that flag, and it now detects and
  clears a stuck one when the world loads, so affected saves repair themselves on first load

### Changed
- **Large transfers no longer flood the connection.** When you join, the host's world is sent in
  one burst of a few thousand packets. That could overflow the connection's send buffer, and the
  mod would then mistake the silence it caused itself for a dead link and restart the transfer —
  occasionally looping forever on "Waiting for player". Data now goes out through a fixed window
  and follows as the other side confirms receipt, and a link only counts as dead when confirmations
  actually stop arriving. Live-tested on a 3 MB world
- The game no longer keeps running at full speed in the background while you sit in the menu with
  no session — only while multiplayer is actually up

### Added
- Diagnostics for bug reports: outgoing traffic volume (flagged in the log when abnormal), the
  build timestamp of the loaded DLL, and a trace around saving and exiting — the last one turned a
  freeze that left no log trace at all into a one-line answer

## [0.1.3-beta] — 2026-07-08

### Fixed
- **Sleep soft-lock** (reported by three players — thanks!): going to bed could
  freeze the game right after the sleep fade, looking like a save that never
  finishes. The mod now guards the bed lookup that crashed the sleep screen:
  a healthy bed sleeps as usual, a broken one is repaired on the spot (the fix
  persists into the save), and if repair is impossible the character simply
  gets up with an on-screen message instead of locking up

### Added
- Diagnostic counters for silently swallowed world-object restore errors
  (`[RESTORE-MUTE]` in the log) — groundwork for fixing gravestone/decor desync

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
- **Dual-host trap**: if both players pressed `F11`, accepting an invite silently kept
  each player in their OWN world — "connected" but seeing only each other's movement.
  Accepting an invite now automatically switches you to guest mode (leaving your empty
  lobby), and a guest pressing `F11` inside the host's world gets a clear message
  instead of hijacking the session (thanks **ELance262** for pinpointing the repro!)
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
