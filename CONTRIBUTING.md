# Contributing

Thanks for your interest in the Graveyard Keeper co-op mod! This is a solo hobby
project, so any help — testing, bug reports, or code — is very welcome.

## Prerequisites

- A legal copy of **Graveyard Keeper** on Steam (the build references the game's
  own assemblies — they are **not** redistributed here).
- **BepInEx 5.4.23.5** installed into the game folder.
- **.NET SDK** (or Visual Studio 2022 with the MSBuild tools). The project targets
  **.NET Framework 4.7.2**.

## Building

1. Tell the build where the game lives — set an environment variable:

   ```
   setx GRAVEYARD_KEEPER_DIR "C:\Program Files (x86)\Steam\steamapps\common\Graveyard Keeper"
   ```

   If you skip this, the build tries common Steam locations automatically.

2. Build:

   ```sh
   dotnet build Multiplayer/Multiplayer.csproj -c Release
   ```

   On success the DLL is copied into `<game>/BepInEx/plugins/` automatically.
   `Steamworks.NET.dll` is vendored in `Multiplayer/lib/` — nothing to download.

3. Launch the game **through Steam** (Steamworks won't initialize otherwise).

## Testing — please read

This is a **networked** mod. Sync behavior **cannot be tested single-player** —
you need two Steam accounts on two machines. A build compiling is *not* evidence
that a sync change works; only a live 2-player session is.

To make debugging possible:

- Set `DebugMode = true` in `BepInEx/config/com.denys.multiplayer.cfg`. This
  enables the developer keybinds and verbose diagnostics.
- **`Shift+C`** cycles a simulated packet-loss injector (0/10/25/40%). Many sync
  bugs only appear under packet loss — a clean LAN hides them. Use this to
  reproduce network-timing bugs deliberately.
- Grab `BepInEx/LogOutput.log` from **both** players right after a bug (the game
  overwrites it on the next launch).

## Understanding the game's code

Game mechanics are read from the **decompiled `Assembly-CSharp`** (via ilspycmd),
not guessed. Before changing how something syncs, find the real method/field in
the decompilation and verify the full signature, return value, and failure
conditions. Guessing at reflection bindings has cost real debugging sessions here.

## Code layout

The mod is patched into the game via Harmony + reflection (no direct API access):

| File | Responsibility |
|------|----------------|
| `Class1.cs`      | Core plugin, Steam networking, packet dispatch, save transfer |
| `Reliability.cs` | `ReliableNet` — a TCP-lite reliability layer over Steam-unreliable |
| `WorldSync.cs`   | World object sync — chopping, graves, gardens, containers, corpses |
| `Crafting.cs`    | Co-op crafting (queues, station-owner arbitration) |
| `Chests.cs`      | Shared chests / stockpiles / item exchange |
| `Story.cs`       | Shared story world-flags |
| `Weather.cs`     | Host-authoritative weather |
| `CharacterSave.cs` | Per-client character + personal story layer |

See the protocol table in the [README](README.md) for the packet types.

## Rules of thumb

- **Bump `PROTOCOL_VERSION`** (in `Class1.cs`) on any change to a packet's wire
  format. Mismatched versions refuse to connect (by design) rather than silently
  desync. Both players must run the exact same DLL.
- Match the style of the surrounding code — comment density, naming, the existing
  reflection/patch patterns.
- Keep new developer diagnostics behind `Multiplayer.DebugMode` so they don't spam
  a normal player's log.

## Pull requests

- Keep PRs focused on one thing.
- Describe **how you tested it** — ideally a live 2-player session, with what each
  player did and what the logs showed.
- Note any protocol change and whether you bumped `PROTOCOL_VERSION`.
