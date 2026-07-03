# Graveyard Keeper — Multiplayer Mod

A mod that adds 2-player co-op to **Graveyard Keeper** (originally a
single-player game). Connection is over Steam P2P — no separate server.

> Status: in development, preparing for beta. Synced: movement/animations/time,
> tree & stone chopping, foraging, garden beds, **graves** (digging stages +
> reburying a body + repair), **carried corpse** (carry/drop + ownership
> handoff), **building** (placement → construction → demolition), **crafting**
> at workstations, **inventory** (shared chests + stockpiles + item exchange),
> **weather**, plus a personal save for the 2nd character and a personal story
> layer (quests/NPCs). NPC villagers and dungeon mobs/zombies — not yet.

---

## Features

Already working:

- Create/join a Steam lobby (FriendsOnly, up to 2 players)
- Save transfer from host to client — both play in the same world
- Position and animation sync (the other player's clone is visible on both sides)
- In-game day and time-of-day sync — anyone sleeping advances both
- Player-leave detection — the clone disappears on lobby exit, crash, or network
  drop (three paths: lobby callback, lobby-membership polling, 5s packet timeout)
- Tree-chopping sync — hits and felling are visible to the partner
- Ground-stone breaking + loot (logs, stone, coal, marble, iron)
- Mine-vein loot sync (the vein doesn't disappear, only the loot is replayed)
- Foraging (mushrooms/flowers/bushes) + farm garden-bed transitions
- **Shared loot** — both players receive the loot (separate inventories)
- **Graves** — digging-stage sync (state replication via JSON), reburying a body,
  rebuilding the mound/fence/cross, repair (durability)
- **Carried corpse** — overhead carrying + drop, with position mirroring;
  **ownership handoff** (the other player picks the corpse up for themselves)
- **Building (Phase 2)** — full cycle: place a build site → construct (anyone can
  build, placeholder→finished) → demolish, all synced by a shared uid
- **Workstation crafting** — co-op crafting: shared queues, station-owner
  arbitration, live progress bar on the partner's side
- **Inventory** — shared chests (op-based sync, concurrent editing by both),
  resource stockpiles, item exchange through chests
- **Weather** — host-authoritative daily preset schedule
- **Personal 2nd-character save** — the client's own character file layered over
  the host's world (skills/hp/techs/crafts/perks are separate), plus a personal
  story layer (quest journal, known NPCs) — shared world, personal progress
  (Stardew model)
- Tutorial/intro skip for the client
- Optimizations: cached reflection, allocation-free packet buffers, a custom
  reliability layer over Steam-unreliable (ReliableNet)

**Not yet** synced:

- NPC-villager state (positions/schedule)
- Dungeon mobs and zombies (deferred — waiting for dungeon-stage progress)

---

## Installation

1. Install **BepInEx 5.4.23.5** into the game folder
2. Drop `Multiplayer.dll` into `Graveyard Keeper/BepInEx/plugins/`
3. Launch the game **through Steam** (otherwise Steamworks won't initialize)

Both players must run the same version of `Multiplayer.dll`.

---

## How to play

**Host:**
1. Load your save
2. Press `F11` — a lobby is created and the Steam invite overlay opens
3. Invite your friend

**Client:**
1. Accept the Steam invite
2. The mod downloads the host's save and joins you into the world

### Keys

| Key | Action |
|-----|--------|
| `F11` | **Create a lobby (host)** — the main action to start co-op |

The mod also ships with developer keybinds (F2–F10, F12, `Shift+C`, `IJKL`) for
diagnostics, recon dumps, a split-screen test clone, and a packet-loss injector.
They're **off by default** and only active when `DebugMode = true` in
`BepInEx/config/com.denys.multiplayer.cfg`. Leave it off for normal play — those
tools can disrupt a live game. Beta testers: turn it on so bug reports include
full diagnostics.

---

## Building

```sh
dotnet build Multiplayer/Multiplayer.csproj -c Release
```

After a build, the DLL is copied into the game's `BepInEx/plugins/`
automatically (when the game folder is found).

- **Game path:** set the `GRAVEYARD_KEEPER_DIR` environment variable (e.g.
  `C:\Program Files (x86)\Steam\steamapps\common\Graveyard Keeper`). If it's not
  set, the build tries to locate the game in common Steam locations automatically.
- `Steamworks.NET.dll` is vendored in `Multiplayer/lib/` — nothing to download.
- `build.ps1` — build with colored output, optional game restart
- `log-watch.ps1` — live BepInEx-log viewer with highlighting

---

## Network protocol

P2P packets (first byte is the type):

| Type | Purpose |
|------|---------|
| `0x01` | Client requests the save from the host |
| `0x02` | Host: save size |
| `0x03` | Save chunk (500 KB each) |
| `0x04` | Save transfer complete |
| `0x05` | Host's start position for the client |
| `0x06` | Position + animation (26 bytes, 20/s) |
| `0x07` | Animation event (17 bytes, on change) |
| `0x08` | Day and time-of-day sync (9 bytes, every 2s) |
| `0x09` | Tree/stone hit — cosmetic shake on the partner |
| `0x0A` | Tree/stone destroyed — object removal on the partner |
| `0x0B` | Loot from chopping/vein — replays `WGO.DropItems` on the partner |
| `0x0D` | Grave/building state replication (JSON `ToJSON(0)` + `RestoreFromSerializedObject`) — digging stages, reburying, building, grave repair |
| `0x0F` | Carried-corpse position (mirrors the carrier, 16/s) |
| `0x10` | Corpse removed (placed/consumed) — the viewer deletes its copy |
| `0x11` | Corpse spawn (full state JSON) on the partner's screen |
| `0x12` / `0x13` | Overhead carrying: the clone carries an item overhead / put it down |
| `0x14` | Corpse ownership handoff (viewer picked up the mirror → owner removes its copy) |
| `0x15` | Spawn primitive: a new object (building) on the partner with a shared uid |
| `0x16` | Building demolition — the partner removes its copy |
| `0x17` | Workstation craft queue (co-op crafting, visibility stage) — the partner sees the same queue widgets over stations |
| `0x18` | Station claim/release (owner arbitration) — a craft start locks the station for the partner, finishing releases it; flag=2 carries craft progress (live bar on the partner) |
| `0x19` | Chest ops (shared chests = item exchange): content deltas ("took 2 water", "put 3 planks") — concurrent editing by both players; full-state reconciliation via 0x0D on GUI close |
| `0x1A` | Weather (host-authoritative daily preset schedule) |
| `0x1C` | Story world-flags (whitelisted shared flags) |

---

## Roadmap

**✅ Done**
- Steam lobby, P2P session
- Save transfer host → client
- Position and animation sync
- Day and time-of-day sync
- Stability: no stray shadows, no duplicate clone, no chop lag, no sleep crash
- Player-leave detection — clone disappears on exit, crash, or network drop
- Tree-chopping sync (Phase 1) — hits, destruction, stump, fall animation
- Ground-stone sync — hits, destruction, loot
- Mine-vein loot sync (coal, stone, marble, iron)
- Foraging + farm garden beds; shared loot (both receive it)
- Graves: digging stages + reburying a body + rebuild (state replication 0x0D)
- Carried corpse: carry/drop + ownership handoff between players
- Building (Phase 2): placement → construction → demolition
- Workstation crafting (co-op queues, arbitration, live bar)
- Inventory: shared chests + stockpiles + exchange
- Weather (host-authoritative)
- Personal 2nd-character save + personal story layer (quests/NPCs)
- Custom reliability layer (ReliableNet) over Steam-unreliable

**🔜 Next**
- Closed beta for volunteers
- NPC-villager state sync
- Dungeon mobs and zombies
- Sleep animation for the player who didn't sleep (currently — instant day jump)

**💡 Ideas for later**
- More than 2 players
- In-game voice/text chat

---

## Troubleshooting

### F11 does nothing

The lobby **is** created — but the invite window is drawn by the **Steam Overlay**,
so if the overlay is disabled you won't see anything. Two ways out:

1. **Enable the overlay:** Steam → Settings → In Game → *Enable the Steam overlay
   while in-game* (also check it's not disabled in the game's Properties). The mod
   shows an in-game message when it detects this situation.
2. **Or skip the overlay entirely:** press `F11` anyway, then your friend joins via
   the **Steam friends list** — right-click your name → *Join Game*. This works even
   with the overlay off on both sides.

Game progress doesn't matter — you can invite from day 1. One note for a **brand-new
game**: the HUD is hidden during the intro sequence, so the best moment to press
`F11` is right after the game interface appears (that's early on, at the graveyard).

### The game crashes on startup — white screen, "Not responding"

**Cause:** a conflict between recent NVIDIA drivers (the 32.x series, from late
2025) and the game's Unity stack. The crash happens **before** BepInEx loads —
the mod isn't involved, the same behavior occurs without it. The `Player-prev.log`
stack trace shows `NvPresent64.NVP_Init_Vulkan` → `dxgi.CreateDXGIFactory2`.

**Fixes, fastest first:**

1. **Switch the game to the iGPU** (laptops with hybrid graphics — the fastest
   workaround). NVIDIA Control Panel → Manage 3D Settings → Program Settings →
   find `Graveyard Keeper.exe` → *Preferred graphics processor* = **Integrated**.
   The game is light and runs fine on an iGPU.
2. **Disable NVIDIA overlay features.** NVIDIA App → Settings → Graphics: turn off
   *Smooth Motion*, *Frame Generation*, *Game Filters*, *Overlay*. Close
   Discord/GeForce Experience before launching.
3. **Roll back the driver with DDU** (most reliable). Download an older Game Ready
   Driver (the 5xx series, autumn 2025) from
   [nvidia.com/Download](https://www.nvidia.com/Download/Find.aspx), download
   [DDU](https://www.guru3d.com/download/display-driver-uninstaller-download/),
   in Safe Mode do Clean and restart, then install the downloaded driver manually
   (WITHOUT NVIDIA App / GeForce Experience — so it doesn't auto-update back).

If none of the fixes help, the problem may be something else — open an issue with
`Player.log` and `Player-prev.log` from the game folder.

---

## Technical

- **Engine:** Unity 2020.3, .NET Framework 4.7.2
- **Modding:** BepInEx 5.4.23.5 + HarmonyLib (patching via reflection)
- **Network:** Steamworks.NET (Steam P2P, no relay) + a custom reliability layer
  `ReliableNet` (TCP-lite over unreliable — Steam's reliable send is broken on
  this SDK)
- Code is split by subsystem: `Class1.cs` (core/network), `WorldSync.cs`,
  `Crafting.cs`, `Chests.cs`, `Story.cs`, `Weather.cs`, `CharacterSave.cs`,
  `Reliability.cs`
- The game is patched via reflection and Harmony — there's no direct access to
  its API
- Game mechanics are read from the decompiled `Assembly-CSharp` (ilspycmd) — so
  we sync against the real code instead of guessing

---

## Contributing

Help is welcome — testing, bug reports, or code. See [CONTRIBUTING.md](CONTRIBUTING.md)
for build setup, the (2-player) testing workflow, and code layout.

## Support

This mod is free and always will be. If you'd like to support development, you can
buy me a coffee: **[ko-fi.com/zonda](https://ko-fi.com/zonda)**. Totally optional —
bug reports and testing help just as much. ❤️

---

## License

[MIT](LICENSE) — use, modify, and distribute freely. This is an unofficial fan
mod, not affiliated with Lazy Bear Games or tinyBuild. `Steamworks.NET` (in
`lib/`) is under its own MIT license (© rlabrecque).
