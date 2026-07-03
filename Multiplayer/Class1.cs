using System;
using UnityEngine;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine.SceneManagement;
using System.Linq.Expressions;
using Object = UnityEngine.Object;

// ─────────────────────────────────────────────────────────────────────────────
// Keep a global reference to P2 so patches can check against it
// ─────────────────────────────────────────────────────────────────────────────
public static class MultiplayerState
{
    public static GameObject Player2;

    // BaseCharacterComponent (PlayerComponent) of the split-screen P2 dummy. Patches compare via
    // ReferenceEquals — a managed check that never touches native memory, so it's safe even for a
    // destroyed component. (Accessing .gameObject of a destroyed component, e.g. player respawn, crashes.)
    public static MonoBehaviour Player2Char;
}

// ─────────────────────────────────────────────────────────────────────────────
// PATCH 1 — PlayerControlIsDisabled always returns false for P2
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch]
public static class PlayerControlPatch
{
    static MethodBase TargetMethod()
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        return AccessTools.TypeByName("BaseCharacterComponent")
            ?.GetMethod("PlayerControlIsDisabled", flags);
    }

    static bool Prefix(MonoBehaviour __instance, ref bool __result)
    {
        // ReferenceEquals (not __instance.gameObject) — safe even if the component is destroyed (see MultiplayerState).
        if (ReferenceEquals(__instance, MultiplayerState.Player2Char))
        {
            __result = false;
            return false;
        }
        // Client movement lock during join: PlayerControlIsDisabled is player-side only (decomp 81209,
        // called from UpdatePlayer/ProcessInteraction), so forcing true disables control ONLY for the local
        // player. Set on the client during loading only.
        if (SteamNetwork.ClientMovementLocked)
        {
            __result = true;
            return false;
        }
        return true;
    }
}

// DIAG (2026-06-24, temporary): client SLEEP soft-lock (freeze tied to the Gerry/first-burial intro). The client
// went to bed but SleepGUI never reached State.Sleeping (TIME-DIAG never showed timeScale=10/stopped, no wake_up,
// energy not restored) → hard soft-lock. SleepGUI.Open's FIRST line dereferences
// WorldMap.GetWorldGameObjectByCustomTag("hero_bed") — if that's null on the client it NREs before the GUI opens.
// This probe logs the bed lookup + any exception thrown by Open. Remove once root-caused.
[HarmonyPatch]
public static class SleepGuiDiagPatch
{
    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("SleepGUI");
        return t?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "Open");
    }

    static void Prefix()
    {
        try
        {
            var wm = AccessTools.TypeByName("WorldMap");
            var getTag = wm?.GetMethod("GetWorldGameObjectByCustomTag", BindingFlags.Public | BindingFlags.Static);
            var bed = getTag?.Invoke(null, new object[] { "hero_bed" });
            Multiplayer.Log?.LogInfo($"[SLEEP-DIAG] SleepGUI.Open entered — hero_bed={(bed != null ? "found" : "NULL")} role={SteamNetwork.Role}");
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[SLEEP-DIAG] prefix: {e.Message}"); }
    }

    static Exception Finalizer(Exception __exception)
    {
        if (__exception != null)
            Multiplayer.Log?.LogError($"[SLEEP-DIAG] SleepGUI.Open THREW: {__exception.GetType().Name}: {__exception.Message}");
        return __exception;   // rethrow — pure diagnostic, don't change behavior
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PATCH 2 — UpdatePlayer
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch]
public static class UpdatePlayerPatch
{
    static MethodBase TargetMethod()
    {
        var type  = AccessTools.TypeByName("BaseCharacterComponent");
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        return type?.GetMethod("UpdatePlayer", flags,
            null, new[] { typeof(float) }, null);
    }

    static bool Prefix(MonoBehaviour __instance, float delta_time)
    {
        // Only drives the split-screen P2 dummy (debug, F9). For everyone else we do nothing and do NOT
        // touch __instance.gameObject — during respawn the component may be destroyed and .gameObject
        // crashes natively (Player.log: crash on get_gameObject after gd_player_respawn). ReferenceEquals
        // is a pure managed check. The network clone never reaches here (its BaseCharacterComponent is stripped).
        if (!ReferenceEquals(__instance, MultiplayerState.Player2Char))
            return true;

        float h = 0f, v = 0f;
        if (Input.GetKey(KeyCode.I)) v =  1f;
        if (Input.GetKey(KeyCode.K)) v = -1f;
        if (Input.GetKey(KeyCode.J)) h = -1f;
        if (Input.GetKey(KeyCode.L)) h =  1f;

        if (h != 0 || v != 0)
            Debug.Log($"[P2 PATCH] UpdatePlayer fired! dir=({h},{v})");

        AccessTools
            .Method(__instance.GetType(), "OnChangeDir", new[] { typeof(Vector2) })
            ?.Invoke(__instance, new object[] { new Vector2(h, v) });

        return false;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PATCH 3 — SmartAnimationController.Update
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch]
public static class SmartAnimPatch
{
    static MethodBase TargetMethod()
    {
        var type = typeof(SmartAnimationController);
        return type.GetMethod("Update",
            BindingFlags.NonPublic | BindingFlags.Public |
            BindingFlags.Instance | BindingFlags.DeclaredOnly);
    }

    // Cache P2's root Transform so we don't call GetComponentInParent every frame
    // for every SmartAnimationController in the scene (NPCs, items — could be hundreds)
    private static Transform _p2RootCache;
    private static GameObject _p2GoCache;

    public static void InvalidateCache()
    {
        _p2RootCache = null;
        _p2GoCache   = null;
    }

    static bool Prefix(SmartAnimationController __instance)
    {
        if (MultiplayerState.Player2 == null) return true;

        // Update the cache only if P2 changed
        if (_p2GoCache != MultiplayerState.Player2)
        {
            _p2GoCache   = MultiplayerState.Player2;
            _p2RootCache = MultiplayerState.Player2.transform;
        }

        // Compare the root transform directly — no GetComponentInParent
        return __instance.transform.root != _p2RootCache;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Duplicate player on the client: SaveSlotsMenuGUI.StartPlayingGame is called twice (our host-save load
// overlaps the natural flow), each call spawning a Player(Clone). Block all calls after the first per
// client session; flag reset on the menu transition.
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch]
static class StartPlayingGameGuardPatch
{
    internal static bool AlreadyStarted = false;

    static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("SaveSlotsMenuGUI"), "StartPlayingGame");

    static bool Prefix()
    {
        if (SteamNetwork.Role != NetworkRole.Client) return true;
        if (AlreadyStarted)
        {
            Multiplayer.Log?.LogInfo("[SPAWN] Repeat StartPlayingGame blocked — preventing a duplicate player ✓");
            return false;
        }
        AlreadyStarted = true;
        return true;
    }
}

// ═════════════════════════════════════════════════════════════

[BepInPlugin("com.denys.multiplayer", "Multiplayer", "0.1.2")]
public class Multiplayer : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    // Developer mode: gates the dev keybinds (F2-F12, Shift+C loss injector, IJKL test-clone movement) and the
    // verbose per-tick diagnostics. OFF by default so a normal player can't accidentally break their game or
    // drown their log. Flip it in BepInEx/config/com.denys.multiplayer.cfg for debugging.
    internal static bool DebugMode;

    private GameObject player;
    private GameObject player2;
    private bool showPosition = false;
    public static Multiplayer Instance;
    
    // The other player's clone (remote) — exists on both sides
    private GameObject _remotePlayerGO;

    private float logTimer = 0f;
    private const float LOG_INTERVAL = 1f;

    void Awake()
    {
        Log = Logger;
        Instance = this;
        DebugMode = Config.Bind("Debug", "DebugMode", false,
            "Enables developer keybinds (F2-F12, Shift+C packet-loss injector, IJKL test-clone movement) and verbose " +
            "per-tick diagnostic logging. Leave OFF for normal play — these are dev tools that can disrupt a live game.").Value;
        if (DebugMode) Logger.LogWarning("[MP] DebugMode ON — dev keybinds + verbose diagnostics enabled");

        // Without this Unity halts the game loop when the window is inactive → the mod stops sending/receiving
        // packets and sync "freezes" for a player who switched windows. runInBackground keeps the loop running.
        Application.runInBackground = true;

        try
        {
            var harmony = new Harmony("com.denys.multiplayer");
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                try
                {
                    harmony.PatchAll(type);
                }
                catch (Exception e)
                {
                    Logger.LogError($"[MP] Patch failed on {type.Name}: {e.Message}");
                }
            }
            Logger.LogInfo("[MP] PatchAll succeeded ✓");
            // BUILD MARKER: first line we check in every live log, so a stale DLL never eats a test again
            // (incident 2026-06-11: two sessions tested the old build because the game wasn't restarted).
            Logger.LogInfo($"[MP] BUILD: {System.IO.File.GetLastWriteTime(System.Reflection.Assembly.GetExecutingAssembly().Location):yyyy-MM-dd HH:mm:ss}");

            var tutType = AccessTools.TypeByName("TutorialGUI");
            var openMethod = tutType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Open");
            var patchInfo = Harmony.GetPatchInfo(openMethod);
            Logger.LogInfo($"[TUT] Open patches: prefixes={patchInfo?.Prefixes?.Count ?? 0}");
        }
        catch (Exception e)
        {
            Logger.LogError($"[MP] PatchAll failed: {e.Message}\n{e.StackTrace}");
        }

        var onGameLoaded = AccessTools.TypeByName("MainGame")
            ?.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "OnGameLoaded");
        Logger.LogInfo($"[MP] OnGameLoaded found: {onGameLoaded != null}");

        var patches2 = Harmony.GetPatchInfo(onGameLoaded);
        Logger.LogInfo($"[MP] OnGameLoaded patches: postfixes={patches2?.Postfixes?.Count ?? 0}");

        var steamGo = new GameObject("SteamManager");
        DontDestroyOnLoad(steamGo);
        steamGo.AddComponent<SteamManager>().Init(Logger);

        var flags   = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var bcType  = AccessTools.TypeByName("BaseCharacterComponent");

        Logger.LogInfo($"[MP] BaseChar.UpdatePlayer: {(bcType?.GetMethod("UpdatePlayer", flags) != null ? "✓" : "✗")}");
        Logger.LogInfo($"[MP] BaseChar.PlayerControlIsDisabled: {(bcType?.GetMethod("PlayerControlIsDisabled", flags) != null ? "✓" : "✗")}");

        var updMethod = bcType?.GetMethod("UpdatePlayer", flags);
        if (updMethod != null)
        {
            var patches = Harmony.GetPatchInfo(updMethod);
            Logger.LogInfo($"[MP] UpdatePlayer patches: prefixes={patches?.Prefixes?.Count ?? 0}");
        }

        var pcdMethod = bcType?.GetMethod("PlayerControlIsDisabled", flags);
        if (pcdMethod != null)
        {
            var patches1 = Harmony.GetPatchInfo(pcdMethod);
            Logger.LogInfo($"[MP] PlayerControlIsDisabled patches: prefixes={patches1?.Prefixes?.Count ?? 0}");
        }

        // Application.logMessageReceived removed — it fired for EVERY exception (incl. SimplifiedWGO.Restore,
        // every frame). Exceptions are now silenced via a Harmony Finalizer directly.

        SceneManager.sceneLoaded += (scene, mode) =>
        {
            Logger.LogInfo($"[SCENE] Scene loaded: name='{scene.name}' buildIndex={scene.buildIndex} mode={mode}");
        };
        SceneManager.sceneUnloaded += (scene) =>
        {
            Logger.LogInfo($"[SCENE] Scene unloaded: name='{scene.name}'");
        };
        SceneManager.activeSceneChanged += (from, to) =>
        {
            Logger.LogInfo($"[SCENE] Active scene: '{from.name}' -> '{to.name}'");
            if (to.name.ToLower().Contains("menu") || to.buildIndex == 0)
            {
                Logger.LogInfo("[SCENE] Transition to menu — resetting state");
                SteamNetwork.IsInGame = false;
                SteamNetwork.IsInGameSince = -1f;
                SteamNetwork.IsLoadingAsClient = false;
                SteamNetwork.IsClientMode = false;
                SteamNetwork.Role = NetworkRole.None;
                SteamNetwork.LobbyID = 0;
                SteamNetwork.RemoteID = 0;
                // ── Phase 3: reset the clone on scene change ──────────────────
                SteamNetwork.RemotePlayerSpawned = false;
                _remotePlayerGO = null;
                // Reset guards for the next session
                SteamManager._unlockAlreadyDone = false;
                ClientCharacterStore.OverlayDone = false;
                OnGameStartedPlayingPatch.Reset();
                StartPlayingGameGuardPatch.AlreadyStarted = false;
                ChopSync.Reset();
            }
        };
    }

    private bool _hadPlayer2 = false;
    private int _p1InstanceId = 0;
    private float _p1MissingTimer = 0f;
    private const float P1_MISSING_THRESHOLD = 2f;

    // Mod version in the menu corner (IsInGame=false only) — lets players diagnose the version mismatch
    // that blocks joining. Version = BepInPlugin metadata + protocol number (the protocol must match).
    private GUIStyle _versionStyle;
    private string _versionLabel;

    void OnGUI()
    {
        if (SteamNetwork.IsInGame) return;

        if (_versionLabel == null)
            _versionLabel = $"Co-op Multiplayer  v{Info.Metadata.Version}  ·  protocol {SteamNetwork.PROTOCOL_VERSION}";

        if (_versionStyle == null)
        {
            _versionStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _versionStyle.normal.textColor = new Color(1f, 1f, 1f, 0.65f);
        }

        // Shadow for readability on any background + the text itself, bottom-left corner.
        var rect = new Rect(14f, Screen.height - 30f, 700f, 26f);
        var shadow = _versionStyle.normal.textColor;
        _versionStyle.normal.textColor = new Color(0f, 0f, 0f, 0.5f);
        GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), _versionLabel, _versionStyle);
        _versionStyle.normal.textColor = shadow;
        GUI.Label(rect, _versionLabel, _versionStyle);
    }

    void Update()
    {
        if (player != null && !_hadPlayer2)
            _p1InstanceId = player.GetInstanceID();

        if (_hadPlayer2 && player != null &&
            player.GetInstanceID() != _p1InstanceId)
        {
            Logger.LogInfo("[MP] P1 changed after load — resetting P2");
            ResetP2();
        }

        if (_hadPlayer2 && (player == null || !player.activeInHierarchy))
        {
            _p1MissingTimer += Time.deltaTime;
            if (_p1MissingTimer >= P1_MISSING_THRESHOLD)
            {
                Logger.LogInfo("[MP] P1 gone for too long — resetting P2");
                _p1MissingTimer = 0f;
                ResetP2();
            }
        }
        else
        {
            _p1MissingTimer = 0f;
        }

        // F11 — create a lobby (host). The one player-facing keybind → always available.
        if (Input.GetKeyDown(KeyCode.F11))
            SteamManager.Instance?.CreateLobby();

        // Everything below is a developer tool (diagnostics, recon dumps, split-screen test clone, packet-loss
        // injector). Gate it behind DebugMode so a player can't wreck their session with an accidental F9/Shift+C.
        if (!DebugMode) return;

        // F7 — find the player
        if (Input.GetKeyDown(KeyCode.F7))
        {
            var oldP2 = GameObject.Find("Player2_Clone");
            if (oldP2 != null && oldP2 != player2)
            {
                Destroy(oldP2);
                player2 = null;
                MultiplayerState.Player2 = null;
                MultiplayerState.Player2Char = null;
                Logger.LogInfo("[MP] Old Player2_Clone destroyed");
            }

            var allPlayers = FindObjectsOfType<GameObject>(true)
                .Where(obj => obj.name.Contains("Player"))
                .ToList();

            Logger.LogInfo($"Objects with 'Player' found: {allPlayers.Count}");
            foreach (var p in allPlayers)
                Logger.LogInfo($"  -> {p.name} active={p.activeSelf}");

            player = allPlayers.FirstOrDefault(o => o.activeSelf && o.name == "Player(Clone)");
            if (player == null)
                player = allPlayers.FirstOrDefault(o => o.activeSelf);

            Logger.LogInfo(player != null
                ? $"PLAYER FOUND: {player.name}"
                : "Player not found!");
        }

        // F8 — show/hide position
        if (Input.GetKeyDown(KeyCode.F8))
        {
            showPosition = !showPosition;
            Logger.LogInfo($"Show position: {showPosition}");
        }

        if (showPosition && player != null)
        {
            logTimer += Time.deltaTime;
            if (logTimer >= LOG_INTERVAL)
            {
                logTimer = 0f;
                Logger.LogInfo($"Player pos: {player.transform.position}");
                if (player2 != null)
                    Logger.LogInfo($"Player2 pos: {player2.transform.position}");
                if (_remotePlayerGO != null)
                    Logger.LogInfo($"RemotePlayer pos: {_remotePlayerGO.transform.position}");
            }
        }

        // F9 — spawn a local P2 (split-screen, test only)
        if (Input.GetKeyDown(KeyCode.F9))
        {
            if (player == null)  { Logger.LogInfo("Press F7 first!"); return; }
            if (player2 != null) { Logger.LogInfo("Player2 already exists!"); return; }
            StartCoroutine(SpawnPlayer2Delayed());
        }

        // F10 — diagnostics
        if (Input.GetKeyDown(KeyCode.F10))
        {
            if (player2 == null) { Logger.LogInfo("Player2 doesn't exist"); return; }

            Logger.LogInfo($"[DIAG] Player2 active={player2.activeSelf}, pos={player2.transform.position}");
            Logger.LogInfo($"[DIAG] Layer={player2.layer} ({LayerMask.LayerToName(player2.layer)})");
            Logger.LogInfo($"[DIAG] localScale={player2.transform.localScale}");

            Logger.LogInfo("=== ALL MONOBEHAVIOURS ON PLAYER2 ===");
            foreach (var b in player2.GetComponentsInChildren<MonoBehaviour>(true))
                Logger.LogInfo($"  [{(b.enabled ? "ON " : "OFF")}] {b.GetType().Name} (obj={b.gameObject.name})");

            Logger.LogInfo("=== SPRITE RENDERERS ===");
            foreach (var sr in player2.GetComponentsInChildren<SpriteRenderer>(true))
            {
                Logger.LogInfo($"  [SR] {sr.gameObject.name} " +
                    $"enabled={sr.enabled} alpha={sr.color.a} " +
                    $"sortLayer={sr.sortingLayerName} order={sr.sortingOrder} " +
                    $"sprite={(sr.sprite != null ? sr.sprite.name : "NULL")}");
            }

            var rb2d = player2.GetComponent<Rigidbody2D>();
            Logger.LogInfo(rb2d != null
                ? $"  Rigidbody2D: bodyType={rb2d.bodyType}, simulated={rb2d.simulated}"
                : "  Rigidbody2D: not found");
        }

        // F12 — connection status
        if (Input.GetKeyDown(KeyCode.F12))
        {
            Logger.LogInfo($"[STEAM] Role={SteamNetwork.Role}");
            Logger.LogInfo($"[STEAM] LobbyID={SteamNetwork.LobbyID}");
            Logger.LogInfo($"[STEAM] RemoteID={SteamNetwork.RemoteID}");
            Logger.LogInfo($"[STEAM] Connected={SteamNetwork.IsConnected}");
            Logger.LogInfo($"[STEAM] IsInGame={SteamNetwork.IsInGame}");
            Logger.LogInfo($"[STEAM] RemotePlayerSpawned={SteamNetwork.RemotePlayerSpawned}");
        }

        // ── Tree-chop recon ────────────────────────────────────────────
        // F6 — capture nearest tree + dump fields (press again after hits → diff finds the HP field).
        // F5 — toggle WGO method tracing for the target (chop → [RECON-TRACE] = method names).
        if (Input.GetKeyDown(KeyCode.F6))
            ChopRecon.CaptureNearestTree();

        if (Input.GetKeyDown(KeyCode.F5))
        {
            ChopRecon.TraceEnabled = !ChopRecon.TraceEnabled;
            Logger.LogInfo($"[RECON] Method tracing: {(ChopRecon.TraceEnabled ? "ON" : "off")}");
        }

        // F4 — GRAVE STAGE recon (B-2). F4 = snapshot of all fields (incl. _data); dig ONE stage, F4 again →
        //      the diff reveals the field encoding the stage.
        if (Input.GetKeyDown(KeyCode.F4))
            ChopRecon.CaptureNearestGrave();

        // F3 — DROP UID RECON (Zonda's idea): stand near a corpse on the ground → dump all fields
        //      of the drop+Item. Capture on BOTH machines for the SAME corpse → compare *id/*uid (★).
        if (Input.GetKeyDown(KeyCode.F3))
            ChopRecon.DumpNearestBodyDrop();

        // F2 — NPC RECON (cosmetic villager-position sync): dump all NPCs in the scene
        //      (id/uid/pos). Capture on BOTH machines near the SAME NPC → uid stable? positions differ?
        if (Input.GetKeyDown(KeyCode.F2))
            ChopSync.DumpNpcs();

        // Shift+C — CHAOS: cycle simulated packet loss OFF→10%→25%→40%→OFF on this machine's outgoing reliable
        //      datagrams. Reproduces the lossy-internet bursts that surface the chest/grave races on a clean LAN.
        //      Enable on one or both PCs, then repeat the buggy actions. See ReliableNet.CycleChaos.
        if (Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.C))
            ReliableNet.CycleChaos();

        // Shift+X — kill THIS machine's ReliableNet (Stop() without a session restart): simulates the wild
        //      zombie state (spurious lobby "left" → Stop, movement keeps flowing, gameplay sync dead) so the
        //      degraded-link warning can be live-tested on demand. Within ~20s BOTH machines should show it.
        //      Recovery is the real one: the client exits to the menu and re-joins.
        if (Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.X))
        {
            ReliableNet.Stop();
            Logger.LogWarning("[NET] Shift+X — ReliableNet stopped on THIS side (zombie-state simulation); expect the degraded warning on both machines");
        }
    }

    // ── Spawn the other player's clone (networked) ────────────────────────────────
    // Called from OnPacketReceived(0x06) on the first position packet. RemotePlayerSpawned is set only
    // after a successful spawn → on failure (scene not ready) the next 0x06 retries.
    private bool _spawnCoroutineRunning = false;

    public void SpawnRemotePlayer(Vector3 startPos)
    {
        // Double guard: by the object and by the coroutine flag
        if (_remotePlayerGO != null || _spawnCoroutineRunning) return;
        _spawnCoroutineRunning = true;
        StartCoroutine(SpawnRemotePlayerCoroutine(startPos));
    }

    private IEnumerator SpawnRemotePlayerCoroutine(Vector3 startPos)
    {
        // ── Wait until the local player appears (client may get 0x06 before the scene loads). 30s, every 0.5s.
        GameObject p1 = null;
        float waitTimeout = 30f;
        while (p1 == null && waitTimeout > 0f)
        {
            p1 = FindObjectsOfType<GameObject>(true)
                .FirstOrDefault(o => o.activeSelf &&
                                     o.name != "RemotePlayer_Clone" &&
                                     o.name != "Player2_Clone" &&
                                     (o.name == "Player(Clone)" || o.name == "Player"));

            if (p1 == null)
            {
                Logger.LogInfo($"[REMOTE] Waiting for the local player... ({waitTimeout:F0}s left)");
                yield return new WaitForSeconds(0.5f);
                waitTimeout -= 0.5f;
            }
        }

        if (p1 == null)
        {
            Logger.LogWarning("[REMOTE] Local player not found in 30s — spawn cancelled");
            SteamNetwork.RemotePlayerSpawned = false;
            _spawnCoroutineRunning = false;
            yield break;
        }

        Logger.LogInfo($"[REMOTE] Local player found: {p1.name}, spawning the clone at {startPos}");

        // ── SINGLETON GUARD (fix for duplicate clones on a long join, live test 2026-06-13) ──
        // During a long scene load the despawn-on-"packets gone 5s" reset _spawnCoroutineRunning → several
        // coroutines accumulated and each instantiated a clone (3-4 stacked). Here, BEFORE Instantiate (no
        // yield until the assignment): if a clone is tracked, exit; clean up any leftover RemotePlayer_Clone.
        if (_remotePlayerGO != null)
        {
            Logger.LogInfo("[REMOTE] Clone already exists — duplicate spawn cancelled ✓");
            _spawnCoroutineRunning = false;
            yield break;
        }
        foreach (var stray in FindObjectsOfType<GameObject>(true))
            if (stray.name == "RemotePlayer_Clone")
            {
                Logger.LogInfo("[REMOTE] Removed leftover clone ✓");
                Destroy(stray);
            }

        var allGOs = FindObjectsOfType<GameObject>(true)
            .Where(o => o.name.Contains("Player"))
            .ToList();
        Logger.LogInfo($"[REMOTE] All Player objects: {string.Join(", ", allGOs.Select(o => o.name + "(active=" + o.activeSelf + ")"))}");


        var p1Materials   = SnapshotMaterials(p1);
        var p1ChildScales = SnapshotScales(p1);

        // Instantiate inside an INACTIVE container — while inactive Unity skips Awake/OnEnable/Start. Critical:
        // PlayerComponent.Awake() would spawn another full duplicate player and WorldGameObject.Awake() would
        // register the clone in GameAwakenerEngine. We strip these components BEFORE Awake.
        var cloneHolder = new GameObject("mp_clone_holder");
        cloneHolder.SetActive(false);

        _remotePlayerGO = Instantiate(p1, cloneHolder.transform);
        _remotePlayerGO.name = "RemotePlayer_Clone";

        int strippedComps = 0;
        foreach (var comp in _remotePlayerGO.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (comp == null) continue;
            var n = comp.GetType().Name;
            if (n == "PlayerComponent" || n == "WorldGameObject" || n == "BaseCharacterComponent")
            {
                Object.DestroyImmediate(comp);
                strippedComps++;
            }
        }
        Logger.LogInfo($"[REMOTE] Stripped {strippedComps} gameplay components before Awake ✓");

        // Take it out of the container — the clone becomes active, Awake fires only for the
        // remaining (visual) components. PlayerComponent is gone → no duplicate spawns.
        _remotePlayerGO.transform.SetParent(null);
        _remotePlayerGO.transform.position = startPos;
        _remotePlayerGO.SetActive(true);
        Object.Destroy(cloneHolder);

        yield return null;
        yield return null;

        RestoreMaterials(_remotePlayerGO, p1Materials);
        EnableAnimations(_remotePlayerGO);
        FreezePhysics(_remotePlayerGO);
        DisableGameplayBehaviours(_remotePlayerGO);

        yield return null;
        RestoreScales(_remotePlayerGO, p1ChildScales);

        SetupRemoteWorldObject(_remotePlayerGO);
        EnableAllRenderers(_remotePlayerGO);
        DisableFireAndParticles(_remotePlayerGO);
        FixOverheadObj(_remotePlayerGO);
        CacheCloneLight(p1, _remotePlayerGO);
        RegisterCloneLights(_remotePlayerGO);

        // RemotePlayerController — smooth movement toward the position from the network
        var ctrl = _remotePlayerGO.AddComponent<RemotePlayerController>();
        ctrl.Init(startPos, Logger, p1.transform); // ← pass the local player's transform for scale

        // Clone trail (footprints on snow/mud) — mirror of LeaveTrailComponent
        _remotePlayerGO.AddComponent<RemoteTrailEmitter>();
        Logger.LogInfo("[REMOTE] Clone trail emitter added ✓");

        // ── CAMERA IS NOT TOUCHED ──────────────────────────────────────────────
        // Each player has their own camera on their own PC.
        // RemotePlayer_Clone just walks the world — the camera follows only the local player.

        SteamNetwork.RemotePlayerSpawned = true;
        _spawnCoroutineRunning = false;
        Logger.LogInfo("[REMOTE] Other player's clone spawned ✓ (camera unchanged)");
    }

    // Instant animation-only update — called from packet 0x07.
    // Sent immediately on a state change — without waiting for the 50ms position timer.
    public void ApplyRemoteAnimation(float angle, int state, float dirX, float dirY)
    {
        if (_remotePlayerGO == null) return;
        if (_cachedRemoteController == null)
            _cachedRemoteController = _remotePlayerGO.GetComponent<RemotePlayerController>();
        _cachedRemoteController?.ApplyAnimationOnly(angle, state, dirX, dirY);
    }

    // Clone position/animation update — called from SteamManager on every 0x06 packet
    private RemotePlayerController _cachedRemoteController;

    public void UpdateRemotePlayer(Vector3 pos, float angle, int state, float dirX, float dirY)
    {
        if (_remotePlayerGO == null) return;
        // Cache GetComponent — called 20 times/sec
        if (_cachedRemoteController == null)
            _cachedRemoteController = _remotePlayerGO.GetComponent<RemotePlayerController>();
        _cachedRemoteController?.SetTargetData(pos, angle, state, dirX, dirY);
    }

    // Destroys the clone when the other player leaves the lobby — else it stays
    // in the world forever. RemotePlayerSpawned is reset to false, so if the friend
    // returns, the next 0x06 packet spawns the clone again.
    public void DespawnRemotePlayer()
    {
        if (_remotePlayerGO != null)
        {
            UnregisterCloneLights(_remotePlayerGO);
            Destroy(_remotePlayerGO);
            Logger.LogInfo("[REMOTE] Clone removed — the other player left ✓");
        }
        _remotePlayerGO = null;
        _cachedRemoteController = null;
        _spawnCoroutineRunning = false;
        SteamNetwork.RemotePlayerSpawned = false;
        ChopSync.ResetNpcSync();      // reset NPC stop/re-enable tracking
        ChopSync.ResetGardenSync();   // reset garden dedup → re-push all beds on reconnect
    }

    private void ResetP2()
    {
        if (player2 != null) Destroy(player2);
        player2 = null;
        player = null;
        MultiplayerState.Player2 = null;
        MultiplayerState.Player2Char = null;
        _hadPlayer2 = false;
        _p1InstanceId = 0;

        var dualCam = Camera.main?.GetComponent<DualPlayerCamera>();
        if (dualCam != null) Destroy(dualCam);

        if (Camera.main != null)
        {
            foreach (var b in Camera.main.GetComponents<MonoBehaviour>())
            {
                if (b.GetType().Name.Contains("ProCamera"))
                    b.enabled = true;
            }
        }
        Logger.LogInfo("[MP] P2 reset. Press F7 → F9 to spawn again");
    }

    // ── Local split-screen P2 coroutine (F9) ─────────────────────────────
    private IEnumerator SpawnPlayer2Delayed()
    {
        var p1Materials   = SnapshotMaterials(player);
        var p1ChildScales = SnapshotScales(player);

        player2 = Instantiate(player);
        player2.name = "Player2_Clone";
        MultiplayerState.Player2 = player2;

        player2.transform.position = new Vector3(
            player.transform.position.x + 2f,
            player.transform.position.y,
            player.transform.position.z);
        player2.transform.SetParent(null);
        player2.SetActive(true);

        yield return null;
        yield return null;

        RestoreMaterials(player2, p1Materials);
        EnableAnimations(player2);
        FreezePhysics(player2);
        DisableGameplayBehaviours(player2);

        yield return null;

        RestoreScales(player2, p1ChildScales);
        Logger.LogInfo("[P2] Scale restored after disabling PixelPerfect");

        SetupWorldObject(player2);
        EnableAllRenderers(player2);
        DisableFireAndParticles(player2);
        FixOverheadObj(player2);
        SetupCharacterComponent(player2);

        var ctrl = player2.AddComponent<P2DirectController>();
        ctrl.Init(Logger, player.transform);

        // Local split-screen — DualPlayerCamera is appropriate here
        AttachDualCamera();

        _hadPlayer2 = true;
        Logger.LogInfo("[P2] Spawn complete!");
    }

    // ── Snapshot scale by name ────────────────────────────────────────────────
    private Dictionary<string, Vector3> SnapshotScales(GameObject src)
    {
        var result = new Dictionary<string, Vector3>();
        foreach (Transform t in src.GetComponentsInChildren<Transform>(true))
        {
            string key = t.gameObject.name;
            int idx = 0;
            string uniqueKey = key;
            while (result.ContainsKey(uniqueKey))
                uniqueKey = $"{key}_{idx++}";
            result[uniqueKey] = t.localScale;
        }
        Logger.LogInfo($"[P2] Scale captured: {result.Count} objects");
        return result;
    }

    private void RestoreScales(GameObject dst, Dictionary<string, Vector3> scales)
    {
        var counters = new Dictionary<string, int>();
        foreach (Transform t in dst.GetComponentsInChildren<Transform>(true))
        {
            string key = t.gameObject.name;
            if (!counters.ContainsKey(key)) counters[key] = 0;

            string uniqueKey = counters[key] == 0 ? key : $"{key}_{counters[key]-1}";
            counters[key]++;

            if (scales.TryGetValue(uniqueKey, out Vector3 scale))
                t.localScale = scale;
        }
        Logger.LogInfo("[P2] Scale restored ✓");
    }

    // ── SetupCharacterComponent ───────────────────────────────────────────────
    private void SetupCharacterComponent(GameObject obj)
    {
        try
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            MonoBehaviour charComp = null;
            foreach (var mb in obj.GetComponents<MonoBehaviour>())
            {
                if (mb.GetType().Name == "PlayerComponent")
                {
                    charComp = mb;
                    break;
                }
            }
            if (charComp == null) { Logger.LogWarning("[P2] PlayerComponent not found!"); return; }
            Logger.LogInfo($"[P2] charComp runtime type: {charComp.GetType().FullName}");

            // Cache the P2 component — the UpdatePlayer/PlayerControlIsDisabled patches
            // compare __instance against it via ReferenceEquals.
            MultiplayerState.Player2Char = charComp;

            var tr = Traverse.Create(charComp);
            tr.Field("_control_enabled").SetValue(true);
            Logger.LogInfo($"[P2] _control_enabled = {tr.Field("_control_enabled").GetValue<bool>()}");

            tr.Field("<can_be_locally_controlled>k__BackingField").SetValue(true);
            Logger.LogInfo($"[P2] can_be_locally_controlled = {tr.Field("<can_be_locally_controlled>k__BackingField").GetValue<bool>()}");

            tr.Field("started").SetValue(true);
            Logger.LogInfo("[P2] started = true ✓");

            var type = charComp.GetType();
            MethodInfo animMethod = null;
            while (type != null && animMethod == null)
            {
                animMethod = type.GetMethod("SetAnimationState",
                    flags | BindingFlags.DeclaredOnly);
                type = type.BaseType;
            }
            animMethod?.Invoke(charComp, new object[] { CharAnimState.Idle, 0 });
            Logger.LogInfo($"[P2] SetAnimationState: {(animMethod != null ? "✓" : "not found")}");

            Logger.LogInfo($"[P2] dont_work_anymore = {tr.Field("dont_work_anymore").GetValue<bool>()}");
            Logger.LogInfo($"[P2] player_controlled_by_script = {tr.Field("player_controlled_by_script").GetValue<bool>()}");
        }
        catch (System.Exception e)
        {
            Logger.LogError($"[P2] SetupCharacterComponent: {e.Message}");
        }
    }

    private void TrySetField(object target, System.Type type, string name, object value)
    {
        var t = type;
        while (t != null)
        {
            var field = t.GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(target, value);
                Logger.LogInfo($"[P2] {t.Name}.{name} = {value} ✓");
                return;
            }
            t = t.BaseType;
        }
        Logger.LogWarning($"[P2] Field '{name}' not found in the {type.Name} hierarchy!");
    }

    private void TryInvokeMethod(object target, System.Type type, string name, object[] args)
    {
        var t = type;
        while (t != null)
        {
            var method = t.GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null)
            {
                method.Invoke(target, args);
                Logger.LogInfo($"[P2] {t.Name}.{name}() invoked ✓");
                return;
            }
            t = t.BaseType;
        }
        Logger.LogWarning($"[P2] Method '{name}' not found in the {type.Name} hierarchy!");
    }

    private void CopyScaleRecursive(GameObject src, GameObject dst)
    {
        var srcTransforms = src.GetComponentsInChildren<Transform>(true);
        var dstTransforms = dst.GetComponentsInChildren<Transform>(true);
        int count = Mathf.Min(srcTransforms.Length, dstTransforms.Length);
        for (int i = 0; i < count; i++)
            dstTransforms[i].localScale = srcTransforms[i].localScale;
        Logger.LogInfo($"[P2] Scale copied ({count} transforms)");
    }

    // ── Materials ─────────────────────────────────────────────────────────────
    private struct MatSnapshot
    {
        public Material[] materials;
    }

    private MatSnapshot[] SnapshotMaterials(GameObject src)
    {
        var renderers = src.GetComponentsInChildren<Renderer>(true);
        var result = new MatSnapshot[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
            result[i] = new MatSnapshot { materials = renderers[i].sharedMaterials };
        return result;
    }

    private void RestoreMaterials(GameObject dst, MatSnapshot[] snapshots)
    {
        var renderers = dst.GetComponentsInChildren<Renderer>(true);
        int count = Mathf.Min(renderers.Length, snapshots.Length);
        for (int i = 0; i < count; i++)
        {
            renderers[i].sharedMaterials = snapshots[i].materials;
            Logger.LogInfo($"[P2] Materials: {renderers[i].gameObject.name}");
        }
    }

    // ── Physics ────────────────────────────────────────────────────────────────
    private void FreezePhysics(GameObject obj)
    {
        var rb2d = obj.GetComponent<Rigidbody2D>();
        if (rb2d != null)
        {
            rb2d.bodyType        = RigidbodyType2D.Dynamic;
            rb2d.simulated       = true;
            rb2d.velocity        = Vector2.zero;
            rb2d.angularVelocity = 0f;
            Logger.LogInfo("[P2] Rigidbody2D -> Dynamic, simulated=true");
        }
    }

    private void DisableFireAndParticles(GameObject obj)
    {
        // "Point/ground light" REMOVED (2026-06-12): this IS the player's night light, driven by
        // DynamicLights.Update by time of day (decomp 98337); time syncs via 0x08 → it fades on its own.
        // "dynamic shadow" RETURNED (2026-06-12, live test #2): shadow copies on the clone are ORPHANS
        // (_child_shadows non-serialized → empty after Instantiate, but _shadows_initialized=true copied →
        // nobody drives them) = a frozen white figure. Reviving them is a separate task.
        var fireNames = new[] { "fire", "fire (1)", "garlic_cloud",
            "memories_fx", "water_fx", "fx_memory_cloud",
            "eating_memory_cloud", "eyes_memory_cloud",
            "dynamic shadow", "[dynamic shadow] #0", "[dynamic shadow] #1",
            "[dynamic shadow] #2", "[dynamic shadow] #3" };

        foreach (Transform t in obj.GetComponentsInChildren<Transform>(true))
        {
            if (fireNames.Contains(t.gameObject.name))
            {
                t.gameObject.SetActive(false);
                Logger.LogInfo($"[P2] Deactivated: {t.gameObject.name}");
            }
        }

        foreach (var ps in obj.GetComponentsInChildren<ParticleSystem>(true))
        {
            ps.gameObject.SetActive(false);
            Logger.LogInfo($"[P2] ParticleSystem deactivated: {ps.gameObject.name}");
        }
    }

    private void DisableGameplayBehaviours(GameObject obj)
    {
        var gameplayComponents = new[]
        {
            "Seeker", "CustomNetworkAnimatorSync", "ChunkedGameObject",
            "SimpleSmoothModifierXY", "AIPath", "AILerp", "FunnelModifier",
            "PixelPerfect",
            // DynamicLight/GroundLight/LightFlicker REMOVED (2026-06-12): player's night light, left alive.
            // ObjectDynamicShadow(Child) RETURNED: orphan copies = white frozen silhouette — see DisableFireAndParticles.
            "RandomCoordinate",
            "ObjectDynamicShadow", "ObjectDynamicShadowChild",
            // Disable player-control components — else the clone moves with the local player's input
            "PlayerComponent", "BaseCharacterComponent",
        };

        var visualComponents = new[] { "RoundAndSortComponent", "DualPlayerCamera" };

        foreach (Transform t in obj.GetComponentsInChildren<Transform>(true))
            t.gameObject.SetActive(true);

        foreach (var b in obj.GetComponentsInChildren<MonoBehaviour>(true))
        {
            string typeName = b.GetType().Name;
            if (visualComponents.Contains(typeName)) continue;
            if (gameplayComponents.Contains(typeName))
            {
                b.enabled = false;
                Logger.LogInfo($"[P2] Disabled: {typeName} ({b.gameObject.name})");
            }
        }

        // Light components are NOT disabled (2026-06-12): the player's night light circle should
        // work on the clone too. Registration in DynamicLights — RegisterCloneLights.
    }

    // ── WorldGameObject ───────────────────────────────────────────────────────
    private void SetupWorldObject(GameObject obj)
    {
        foreach (var b in obj.GetComponents<MonoBehaviour>())
        {
            if (b.GetType().Name != "WorldGameObject") continue;
            try
            {
                var type = b.GetType();
                SetField(b, type, "_obj_id", "player2_clone_unique");
                SetField(b, type, "_is_player", true);
                SetField(b, type, "_saved_position", obj.transform.position);
            }
            catch (System.Exception e)
            {
                Logger.LogWarning($"[P2] WorldGameObject: {e.Message}");
            }
        }

        // player may be null if we're cloning a remote — use obj itself for layers
        if (player != null)
        {
            CopySortingLayers(player, obj);
            obj.layer = player.layer;
        }
        Logger.LogInfo($"[P2] Layer: {LayerMask.LayerToName(obj.layer)}");
    }

    // Copies rendering layers onto the networked clone.
    // WorldGameObject is already destroyed in SpawnRemotePlayerCoroutine — nothing to configure.
    private void SetupRemoteWorldObject(GameObject obj)
    {
        // Take layers from the first local player found
        var localPlayer = FindObjectsOfType<GameObject>(true)
            .FirstOrDefault(o => o.activeSelf &&
                                 o.name != "RemotePlayer_Clone" &&
                                 o.name != "Player2_Clone" &&
                                 (o.name == "Player(Clone)" || o.name == "Player"));
        if (localPlayer != null)
        {
            CopySortingLayers(localPlayer, obj);
            obj.layer = localPlayer.layer;
        }
        Logger.LogInfo($"[REMOTE] Layer: {LayerMask.LayerToName(obj.layer)}");
    }

    private void SetField(object target, System.Type type, string name, object value)
    {
        var field = type.GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            field.SetValue(target, value);
            Logger.LogInfo($"[P2] {type.Name}.{name} -> {value}");
        }
    }

    private void CopySortingLayers(GameObject src, GameObject dst)
    {
        var srcR = src.GetComponentsInChildren<SpriteRenderer>(true);
        var dstR = dst.GetComponentsInChildren<SpriteRenderer>(true);
        int count = Mathf.Min(srcR.Length, dstR.Length);
        for (int i = 0; i < count; i++)
        {
            dstR[i].sortingLayerID = srcR[i].sortingLayerID;
            dstR[i].sortingOrder   = srcR[i].sortingOrder;
        }
        Logger.LogInfo($"[P2] Sorting layers copied ({count})");
    }

    private void EnableAllRenderers(GameObject obj)
    {
        foreach (var r in obj.GetComponentsInChildren<Renderer>(true))
        {
            // Don't touch dynamic-shadow sprites (2026-06-12): their enabled/alpha is driven by the
            // game (ObjectDynamicShadow, now alive on the clone). Forcing alpha 0→1 drew a
            // WHITE opaque silhouette under the clone's feet (live test #2 photo).
            if (r.gameObject.name.Contains("dynamic shadow")) continue;
            r.enabled = true;
            r.gameObject.SetActive(true);
            if (r is SpriteRenderer sr && sr.color.a < 0.01f)
            {
                var c = sr.color; c.a = 1f; sr.color = c;
                Logger.LogInfo($"[P2] Alpha fixed -> 1 on {r.gameObject.name}");
            }
        }
    }

    private void FixOverheadObj(GameObject obj)
    {
        foreach (var sr in obj.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr.gameObject.name != "overhead_obj") continue;
            var c = sr.color;
            if (c.r > 0.9f && c.g < 0.1f && c.b > 0.9f)
            {
                sr.color = Color.white;
                Logger.LogInfo("[P2] overhead_obj: fixed magenta -> white");
            }
            sr.enabled = false;
            _remoteOverheadRenderer = sr;   // cache for showing carried items (0x12/0x13)
            break;
        }
    }

    // ── Show an item above the partner's CLONE head (overhead carry sync) ───────────
    private SpriteRenderer _remoteOverheadRenderer;

    private SpriteRenderer RemoteOverheadRenderer()
    {
        if (_remoteOverheadRenderer != null) return _remoteOverheadRenderer;
        if (_remotePlayerGO == null) return null;
        foreach (var sr in _remotePlayerGO.GetComponentsInChildren<SpriteRenderer>(true))
            if (sr.gameObject.name == "overhead_obj") { _remoteOverheadRenderer = sr; break; }
        return _remoteOverheadRenderer;
    }

    // The clone's character animator (for the carry pose via the float "walk_type_f").
    private Animator _remoteAnimator;
    private Animator RemoteCharAnimator()
    {
        if (_remoteAnimator != null) return _remoteAnimator;
        if (_remotePlayerGO == null) return null;
        // Find the animator that has the walk_type_f parameter (the main character animator).
        foreach (var an in _remotePlayerGO.GetComponentsInChildren<Animator>(true))
        {
            if (an.runtimeAnimatorController == null) continue;
            foreach (var p in an.parameters)
                if (p.name == "walk_type_f") { _remoteAnimator = an; return _remoteAnimator; }
        }
        return _remoteAnimator;
    }

    // Carry pose on the clone: SetWalkAnimationType in the game = animator.SetFloat("walk_type_f", x).
    // Standard=0, OverheadItem=0.1 (arms up, holding), WithTool=-0.1.
    private void SetRemoteWalkType(float v)
    {
        var an = RemoteCharAnimator();
        if (an == null) { Logger.LogWarning("[CHOP] clone animator with walk_type_f not found"); return; }
        try { an.SetFloat("walk_type_f", v); } catch { }
    }

    // Partner lifted a heavy item → show it above their clone's head + the carry pose.
    public void SetRemoteOverhead(string icon)
    {
        var sr = RemoteOverheadRenderer();
        if (sr == null) { Logger.LogWarning("[CHOP] clone overhead not found"); return; }
        try
        {
            var esc = AccessTools.TypeByName("EasySpritesCollection");
            var getSprite = esc?.GetMethod("GetSprite", BindingFlags.Public | BindingFlags.Static);
            var sprite = getSprite?.Invoke(null, new object[] { icon, false, "" });
            sr.sprite = sprite as Sprite;
            sr.color = Color.white;
            sr.enabled = sr.sprite != null;
            sr.gameObject.SetActive(true);
            SetRemoteWalkType(0.1f);   // OverheadItem pose — the clone holds the item in its arms
            Logger.LogInfo($"[CHOP] Clone overhead: shown icon={icon} + carry pose ✓");
        }
        catch (Exception e) { Logger.LogError($"[CHOP] SetRemoteOverhead failed: {e.Message}"); }
    }

    // Partner placed/dropped → remove the item above their clone's head + normal pose.
    public void ClearRemoteOverhead()
    {
        SetRemoteWalkType(0f);   // return to the Standard pose
        var sr = RemoteOverheadRenderer();
        if (sr == null) return;
        sr.sprite = null;
        sr.enabled = false;
        Logger.LogInfo("[CHOP] Clone overhead: removed + Standard pose");
    }

    // ── TORCH light on the partner's CLONE (a bit in 0x06) ────────────────────────────
    // The player's NIGHT light circle is NOT here: it's always active, intensity driven by
    // DynamicLights by time of day (see DisableGameplayBehaviours/RegisterCloneLights).
    // This is the MANUAL torch: light_go, a child GO of the player (a PlayerComponent field, decompiled
    // 87407), the game enables it SetActive(tool==Torch=9). We mirror the owner's activeSelf.
    // Re-enabling components on first turn-on is a safeguard (DisableFireAndParticles may have
    // deactivated "fire"-like children under light_go).
    private GameObject _cloneLightGo;
    private bool _cloneTorchOn;
    private bool _cloneLightCompsEnabled;

    // Take the light_go path from the LOCAL player (its PlayerComponent is alive; the clone's is stripped)
    // and find the same descendant in the clone — Instantiate preserves names and hierarchy.
    private void CacheCloneLight(GameObject localPlayer, GameObject clone)
    {
        _cloneLightGo = null;
        _cloneTorchOn = false;
        _cloneLightCompsEnabled = false;
        try
        {
            var pc = localPlayer.GetComponentsInChildren<MonoBehaviour>(true)
                .FirstOrDefault(m => m != null && m.GetType().Name == "PlayerComponent");
            var lightGo = pc?.GetType().GetField("light_go")?.GetValue(pc) as GameObject;
            if (lightGo == null) { Logger.LogWarning("[REMOTE] local player's light_go not found"); return; }

            var parts = new System.Collections.Generic.List<string>();
            var t = lightGo.transform;
            while (t != null && t != localPlayer.transform) { parts.Insert(0, t.name); t = t.parent; }
            if (t == null) { Logger.LogWarning($"[REMOTE] light_go ({lightGo.name}) is not a descendant of the player"); return; }

            var path = string.Join("/", parts.ToArray());
            var found = clone.transform.Find(path);
            if (found == null) { Logger.LogWarning($"[REMOTE] clone's light_go not found at path '{path}'"); return; }

            _cloneLightGo = found.gameObject;
            _cloneLightGo.SetActive(false); // start dark; the bit in 0x06 sets the state in ~50ms
            Logger.LogInfo($"[REMOTE] clone's light_go cached: '{path}' ✓");
        }
        catch (Exception e) { Logger.LogWarning($"[REMOTE] CacheCloneLight: {e.Message}"); }
    }

    // Register the clone's Lights in DynamicLights — the game does this in WGO.Awake (decomp 112390), but we
    // strip the clone's WGO before Awake. Without it the light doesn't track time of day (always dead/frozen).
    private void RegisterCloneLights(GameObject clone)
    {
        try
        {
            DynamicLights.SearchForLightsInNewObject(clone);
            Logger.LogInfo("[REMOTE] Clone lights registered in DynamicLights ✓");
        }
        catch (Exception e) { Logger.LogWarning($"[REMOTE] RegisterCloneLights: {e.Message}"); }
    }

    // Symmetric removal on despawn — else DynamicLights' static lists accumulate dead Lights
    // (the game removes via WGO.OnDestroy, which the clone lacks).
    private void UnregisterCloneLights(GameObject clone)
    {
        if (clone == null) return;
        try { DynamicLights.SearchForLightsInDestroyedObject(clone); } catch { }
    }

    // Called from every 0x06 (20/s) — acts only on a state change.
    public void SetRemoteTorch(bool on)
    {
        if (_cloneLightGo == null || on == _cloneTorchOn) return;
        _cloneTorchOn = on;
        try
        {
            if (on && !_cloneLightCompsEnabled)
            {
                _cloneLightCompsEnabled = true;
                var lightTypes = new[] { "DynamicLight", "GroundLight", "LightFlicker" };
                foreach (var b in _cloneLightGo.GetComponentsInChildren<MonoBehaviour>(true))
                    if (b != null && lightTypes.Contains(b.GetType().Name)) b.enabled = true;
                foreach (var l in _cloneLightGo.GetComponentsInChildren<Light>(true))
                    l.enabled = true;
                foreach (Transform tr in _cloneLightGo.GetComponentsInChildren<Transform>(true))
                    tr.gameObject.SetActive(true);
            }
            _cloneLightGo.SetActive(on);
            Logger.LogInfo($"[REMOTE] Clone torch: {(on ? "on" : "off")} ✓");
        }
        catch (Exception e) { Logger.LogWarning($"[REMOTE] SetRemoteTorch: {e.Message}"); }
    }

    private void EnableAnimations(GameObject obj)
    {
        foreach (var anim in obj.GetComponentsInChildren<Animator>(true))
            anim.enabled = true;
        foreach (var anim in obj.GetComponentsInChildren<SmartAnimationController>(true))
            anim.enabled = true;
    }

    // ── Camera for local split-screen (F9) ───────────────────────────────
    private void AttachDualCamera()
    {
        var cam = Camera.main;
        if (cam == null) { Logger.LogInfo("[CAM] Camera not found!"); return; }

        foreach (var b in cam.GetComponents<MonoBehaviour>())
        {
            string n = b.GetType().Name;
            if (n.Contains("ProCamera") || n.Contains("Follow") || n.Contains("Track"))
            {
                b.enabled = false;
                Logger.LogInfo($"[CAM] Disabled: {n}");
            }
        }

        if (cam.gameObject.GetComponent<DualPlayerCamera>() == null)
        {
            cam.gameObject.AddComponent<DualPlayerCamera>()
               .Init(player.transform, player2.transform, cam, Logger);
        }
    }

    // ── P2DirectController (local split-screen, HJKL keys) ────────────
    public class P2DirectController : MonoBehaviour
    {
        private Rigidbody2D _rb;
        private FieldInfo _velocityField;
        private Dictionary<string, Vector3> _p1Scales = new Dictionary<string, Vector3>();
        private Animator _animator;
        private ManualLogSource _logger;
        private float _speed = 5.26f;
        private Transform _p1Transform;
        private Vector3 _p1LastPos;
        private int _frameCounter;
        private float _baseScale = 80f;
        private MonoBehaviour _p1CharComp;
        private float _p1velocity_cached = 0f;
        private bool  _wasFocused = true;

        public void Init(ManualLogSource logger, Transform p1)
        {
            _logger = logger;
            _p1LastPos = p1.position;
            _p1Transform = p1;
            _rb = GetComponent<Rigidbody2D>();
            _animator = GetComponentInChildren<Animator>();

            var p1col = p1.GetComponent<CapsuleCollider2D>() ?? p1.GetComponent<Collider2D>() as CapsuleCollider2D;
            var p2col = GetComponent<CapsuleCollider2D>();
            _baseScale = Mathf.Abs(p1.lossyScale.x);
            _logger.LogInfo($"[P2] Base scale fixed: {_baseScale}");
            if (p1col != null && p2col != null)
            {
                p2col.size   = p1col.size;
                p2col.offset = p1col.offset;
                _logger.LogInfo("[P2] Collider copied from P1 ✓");
            }

            foreach (var mb in p1.GetComponents<MonoBehaviour>())
            {
                if (mb.GetType().Name == "PlayerComponent")
                {
                    _p1CharComp = mb;
                    break;
                }
            }

            _frameCounter++;
            if (_frameCounter % 120 == 0)
                _logger.LogInfo($"[P2] p1vel={_p1velocity_cached:F1} scale={_baseScale:F1}");

            _logger.LogInfo($"[P2] P1 charComp: {(_p1CharComp != null ? "✓" : "✗")}");
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var bcType = AccessTools.TypeByName("BaseCharacterComponent");
            _velocityField = bcType?.GetField("velocity", flags);
            _logger.LogInfo($"[P2] velocity field: {(_velocityField != null ? "✓" : "✗")}");

            try
            {
                if (_velocityField != null && _p1CharComp != null)
                {
                    float p1vel = (float)(_velocityField.GetValue(_p1CharComp) ?? 0f);
                    _p1velocity_cached = (p1vel > 0.01f && p1vel < 20f)
                        ? p1vel * _baseScale * 0.95f
                        : _speed * _baseScale * 0.95f;
                    _logger.LogInfo($"[P2] velocity_cached initialized: {_p1velocity_cached:F1}");
                }
                else
                {
                    _p1velocity_cached = _speed * _baseScale * 0.95f;
                    _logger.LogInfo($"[P2] velocity_cached fallback: {_p1velocity_cached:F1}");
                }
            }
            catch { _p1velocity_cached = _speed * _baseScale * 0.95f; }

            _logger.LogInfo($"[P2] RB:{(_rb!=null?"✓":"✗")} Anim:{(_animator!=null?"✓":"✗")}");
        }

        private Vector3 _cachedP1Scale = Vector3.one;
        private int     _scaleFrame = -1;

        void LateUpdate()
        {
            if (_p1Transform == null) return;
            int frame = Time.frameCount;
            if (frame - _scaleFrame >= 10)
            {
                _cachedP1Scale = _p1Transform.lossyScale;
                _scaleFrame = frame;
            }
            transform.localScale = _cachedP1Scale;
        }

        void FixedUpdate()
        {
            if (_rb == null || _p1Transform == null) return;

            bool isFocused = Application.isFocused;

            if (!isFocused || !_wasFocused)
            {
                _rb.velocity = Vector2.zero;
                _animator?.SetInteger("global_state", 0);
                _wasFocused = isFocused;
                return;
            }
            _wasFocused = true;

            float h = 0f, v = 0f;
            if (Input.GetKey(KeyCode.H)) v =  1f;
            if (Input.GetKey(KeyCode.J)) v = -1f;
            if (Input.GetKey(KeyCode.K)) h = -1f;
            if (Input.GetKey(KeyCode.L)) h =  1f;

            float distToP1 = Vector2.Distance(
                new Vector2(transform.position.x, transform.position.y),
                new Vector2(_p1Transform.position.x, _p1Transform.position.y));
            bool p1IsTeleporting = distToP1 > 2000f;

            if (!p1IsTeleporting && _velocityField != null && _p1CharComp != null)
            {
                try
                {
                    float p1vel = (float)(_velocityField.GetValue(_p1CharComp) ?? 0f);
                    if (p1vel > 0.01f && p1vel < 20f)
                        _p1velocity_cached = p1vel * _baseScale * 0.95f;
                }
                catch { }
            }

            _rb.velocity = new Vector2(h, v).normalized * _p1velocity_cached;

            var dir = new Vector2(h, v);
            if (dir.magnitude > 0.01f)
            {
                float angle = Mathf.Atan2(v, h) * Mathf.Rad2Deg;
                _animator?.SetFloat("direction_angle", Mathf.Round(angle / 90f) * 90f);
                _animator?.SetFloat("direction_x", h);
                _animator?.SetFloat("direction_y", v);
                _animator?.SetInteger("global_state", -1);
            }
            else
                _animator?.SetInteger("global_state", 0);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RemoteTrailEmitter — the partner's clone footprints.
// Mirror of LeaveTrailComponent.CustomUpdate (decompiled 111172) WITHOUT BaseCharacterComponent
// (stripped on the clone): ground type — WorldMap.GetGroundType(pos) (static, 99502), sprite —
// TrailDefinition("Trails/human").GetByType().GetByDirection(), spawn — TrailObject.Spawn (static,
// 111341; places into trails_root under world_root itself).
// SIMPLIFICATION vs vanilla (accepted): no carrying "dirt" onto clean surfaces (_dirty_amount logic)
// and is_outside=true always (affects only how fast the trail fades indoors — cosmetic).
// ─────────────────────────────────────────────────────────────────────────────
public class RemoteTrailEmitter : MonoBehaviour
{
    private TrailDefinition _def;
    private Vector2 _prevPos;
    private bool _leftFoot = true;
    private const float TRAIL_DIST_SQR    = 370f;    // LEAVE_TRAIL_DISTANCE (compared with sqrMagnitude!)
    private const float TELEPORT_DIST_SQR = 40000f;  // 200² — clone teleport, don't draw a trail

    public void Start()
    {
        _def = Resources.Load<TrailDefinition>("Trails/human");
        _prevPos = transform.position;
        if (_def == null)
            Multiplayer.Log?.LogWarning("[REMOTE] Trails/human didn't load — no clone footprints");
    }

    public void Update()
    {
        if (_def == null) return;
        try
        {
            Vector2 pos = transform.position;
            Vector2 dir = _prevPos - pos;            // like the game: prev - cur (111179)
            float sqr = dir.sqrMagnitude;
            if (sqr > TELEPORT_DIST_SQR) { _prevPos = pos; return; }

            var ground = WorldMap.GetGroundType(pos);
            var byType = ground == Ground.GroudType.None ? null : _def.GetByType(ground);
            float need = (byType != null && byType.custom_trail_dist) ? byType.leave_trail_dist : TRAIL_DIST_SQR;
            if (sqr < need) return;
            _prevPos = pos;
            if (byType == null) return;              // surface with no trails (no definition)

            bool flip;
            var spr = byType.GetByDirection(dir, _leftFoot, out flip);
            if (spr == null) return;
            var trail = TrailObject.Spawn(pos, spr, flip, is_outside: true);
            if (trail != null)
            {
                _leftFoot = !_leftFoot;
                trail.SetColor(byType.color, 1f);
            }
        }
        catch { /* trails are cosmetic, don't spam the log every frame */ }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RemotePlayerController — smoothly moves the clone to the position from the network.
// Each PC sees the OTHER player's clone. Each one's own camera is untouched.
// ─────────────────────────────────────────────────────────────────────────────
public class RemotePlayerController : MonoBehaviour
{
    private Vector3 _targetPosition;
    private Animator _animator;
    private ManualLogSource _logger;
    private bool _initialized = false;
    private const float INTERP_SPEED = 12f;
    // Teleport threshold — if the clone is farther than this from the target, teleport immediately
    private const float TELEPORT_THRESHOLD = 100f;

    // Keep the local player's transform for the clone's correct scale
    private Transform _localPlayerTransform;
    // First-position-received flag — teleport immediately without Lerp
    private bool _firstPositionReceived = false;

    // Counter for throttled logs
    private int _logCounter = 0;

    public void Init(Vector3 startPos, ManualLogSource logger, Transform localPlayerTransform)
    {
        _targetPosition = startPos;
        transform.position = startPos;
        _logger = logger;
        _localPlayerTransform = localPlayerTransform;
        _animator = GetComponentInChildren<Animator>();
        _initialized = true;
        _logger.LogInfo($"[REMOTE] RemotePlayerController initialized ✓ | localPlayer={(_localPlayerTransform != null ? _localPlayerTransform.name : "null")}");
    }

    public void SetTargetData(Vector3 pos, float angle, int state, float dirX, float dirY)
    {
        _targetPosition = pos;

        // First valid position — teleport the clone immediately instead of Lerping from (0,0,0)
        if (!_firstPositionReceived)
        {
            transform.position = pos;
            _firstPositionReceived = true;
            _logger?.LogInfo($"[REMOTE] First position — teleport to {pos}");
        }

        // Log the position every 120 packets (~6 seconds at 20 packets/sec)
        _logCounter++;
        if (_logCounter % 120 == 0)
            _logger?.LogInfo($"[REMOTE] Host position: {pos}");

        ApplyAnimationOnly(angle, state, dirX, dirY);
    }

    // Separate method for instant animation update from packet 0x07.
    // Called without changing position — only the animator state.
    public void ApplyAnimationOnly(float angle, int state, float dirX, float dirY)
    {
        if (_animator == null) return;
        _animator.SetFloat("direction_angle", angle);
        _animator.SetFloat("direction_x", dirX);
        _animator.SetFloat("direction_y", dirY);
        _animator.SetInteger("global_state", state);
    }

    // Scale cache — lossyScale is computed recursively over the hierarchy, expensive every frame
    private Vector3 _cachedScale = Vector3.one;
    private int     _scaleUpdateFrame = -1;
    private const int SCALE_UPDATE_EVERY = 10; // update once per 10 frames

    void LateUpdate()
    {
        if (!_initialized) return;

        // lossyScale — a recursive hierarchy walk, update less often
        if (_localPlayerTransform != null)
        {
            int frame = Time.frameCount;
            if (frame - _scaleUpdateFrame >= SCALE_UPDATE_EVERY)
            {
                _cachedScale = _localPlayerTransform.lossyScale;
                _scaleUpdateFrame = frame;
            }
            transform.localScale = _cachedScale;
        }

        // If far from the target — teleport immediately (location change, first frame)
        float distSq = (transform.position - _targetPosition).sqrMagnitude;
        if (distSq > TELEPORT_THRESHOLD * TELEPORT_THRESHOLD)
            transform.position = _targetPosition;
        else
            transform.position = Vector3.Lerp(
                transform.position, _targetPosition, Time.deltaTime * INTERP_SPEED);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DualPlayerCamera — only for local split-screen (F9).
// NOT used in network mode.
// ─────────────────────────────────────────────────────────────────────────────
public class DualPlayerCamera : MonoBehaviour
{
    private Transform p1, p2;
    private Camera cam;
    private ManualLogSource logger;
    private float originalSize;

    public void Init(Transform player1, Transform player2, Camera camera, ManualLogSource log)
    {
        p1 = player1; p2 = player2; cam = camera; logger = log;
        originalSize = camera.orthographicSize;
        cam.orthographicSize = originalSize;
        logger.LogInfo($"[CAM] DualPlayerCamera activated. size={originalSize}");
    }

    void LateUpdate()
    {
        if (p1 == null || p2 == null || cam == null) return;
        if (!p1.gameObject.activeInHierarchy || !p2.gameObject.activeInHierarchy) return;

        Vector2 p1pos = new Vector2(p1.position.x, p1.position.y);
        Vector2 p2pos = new Vector2(p2.position.x, p2.position.y);
        float dist = Vector2.Distance(p1pos, p2pos);

        if (dist > 2000f)
        {
            p2.position = p1.position;
            logger.LogInfo($"[CAM] Teleport P2 to P1, dist={dist:F0}");
            return;
        }

        Vector3 mid = (p1.position + p2.position) * 0.5f;
        cam.transform.position = Vector3.Lerp(
            cam.transform.position,
            new Vector3(mid.x, mid.y, cam.transform.position.z),
            Time.deltaTime * 5f);

        cam.orthographicSize = originalSize;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// STEAM NETWORKING
// ─────────────────────────────────────────────────────────────────────────────

public enum NetworkRole { None, Host, Client }

public static class SteamNetwork
{
    public static NetworkRole Role = NetworkRole.None;
    public static bool IsClientMode = false;
    public static ulong LobbyID = 0;
    public static ulong RemoteID = 0;
    // NETWORK protocol version — checked on connect (0x01 → host rejects with 0x22 on mismatch, else builds
    // silently desync). BUMP ON ANY PACKET FORMAT CHANGE. Separate from the BepInPlugin version.
    public const int PROTOCOL_VERSION = 3;   // v3: epoch byte in 0xFE/0xFF framing + 0x25/0x26 auto-resync — wire-incompatible with v2
    // Client's coop save slot. "999999" never collides — the game numbers slots 0..1000 only
    // (GetNewSlotFilename, decomp 102766). Removed in SteamManager.OnApplicationQuit.
    public const string CoopSaveSlot = "999999";
    public static bool IsConnected => Role != NetworkRole.None && RemoteID != 0;
    public static bool IsInGame = false;
    public static bool IsLoadingAsClient = false;
    public static bool RemotePlayerSpawned = false;
    // Movement lock for the local CLIENT during join: true from load start until the client has loaded,
    // teleported to the host and the host can see it (two-way link). While true, PlayerControlPatch keeps
    // controls disabled so the client doesn't walk invisible and then get yanked. Client only.
    public static bool ClientMovementLocked = false;
    // Time when IsInGame became true — to block the spurious Open() right after loading
    public static float IsInGameSince = -1f;
    // How many seconds after IsInGame we block opening the menu
    public const float MENU_BLOCK_DURATION = 15f;
    // Time of the last REAL exit to the menu (InGameMenuGUI.ReturnToMainMenu just called). A spurious Open()
    // during init does NOT go through ReturnToMainMenu → that's how we don't cut off a legitimate quick exit
    // within MENU_BLOCK_DURATION.
    public static float RealExitRequestedAt = -1f;

    // Host is REALLY in a loaded world (not title/menu, where MainGame.me.save/player are null). CRITICAL:
    // if the host accepts a client too early, the client loads into the intro state and sends its tutorial
    // pairs (lock_tp=1) back over 0x1C → overwrites the host world ("can't leave the house"). So we gate
    // save/sync on this.
    public static bool HostInWorld()
    {
        try { return MainGame.me != null && MainGame.me.save != null && MainGame.me.player != null; }
        catch { return false; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Game time sync. Day: GameSave.day (int). Time of day: EnvironmentEngine._cur_time (float).
// "Maximum wins": the receiver adopts the time only if the remote is ahead — so a day jump from anyone's
// sleep propagates, while ordinary clock drift levels up to the leader.
// ─────────────────────────────────────────────────────────────────────────────
public static class GameTimeSync
{
    private static FieldInfo _saveField;     // MainGame.save
    private static FieldInfo _dayField;      // GameSave.day
    private static FieldInfo _curTimeField;  // EnvironmentEngine._cur_time
    private static bool _ready;
    private static object _envCache;

    private static void EnsureReflection()
    {
        if (_ready) return;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _saveField    = AccessTools.TypeByName("MainGame")?.GetField("save", flags);
        _dayField     = AccessTools.TypeByName("GameSave")?.GetField("day", flags);
        _curTimeField = AccessTools.TypeByName("EnvironmentEngine")?.GetField("_cur_time", flags);
        _ready = _saveField != null && _dayField != null && _curTimeField != null;
    }

    private static object GetSave()
    {
        var mgType = AccessTools.TypeByName("MainGame");
        var me = mgType?.GetField("me", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null);
        return me != null ? _saveField?.GetValue(me) : null;
    }

    private static object GetEnv()
    {
        // EnvironmentEngine is a scene component; cache it, re-find if destroyed
        if (_envCache != null && (_envCache as UnityEngine.Object) != null)
            return _envCache;
        var envType = AccessTools.TypeByName("EnvironmentEngine");
        if (envType == null) return null;
        var found = UnityEngine.Object.FindObjectsOfType(envType);
        _envCache = found.Length > 0 ? found[0] : null;
        return _envCache;
    }

    public static bool TryRead(out int day, out float curTime)
    {
        day = 0; curTime = 0f;
        EnsureReflection();
        if (!_ready) return false;
        var save = GetSave();
        var env  = GetEnv();
        if (save == null || env == null) return false;
        try
        {
            day     = (int)_dayField.GetValue(save);
            curTime = (float)_curTimeField.GetValue(env);
            return true;
        }
        catch { return false; }
    }

    public static void Apply(int day, float curTime)
    {
        EnsureReflection();
        if (!_ready) return;
        var save = GetSave();
        var env  = GetEnv();
        try
        {
            if (save != null) _dayField.SetValue(save, day);
            if (env  != null) _curTimeField.SetValue(env, curTime);
        }
        catch { }
    }
}

public class SteamManager : MonoBehaviour
{
    private ManualLogSource _logger;
    private bool _loadingInProgress = false;
    public static SteamManager Instance;
    private bool _initialized = false;

    private System.Type _steamMatchmaking;
    private System.Type _steamNetworking;
    private System.Type _steamUser;
    private System.Type _steamFriends;

    private float _sendTimer = 0f;
    private const float SEND_INTERVAL = 0.05f; // 20 times/sec

    private float _timeSyncTimer = 0f;
    private const float TIME_SYNC_INTERVAL = 2f; // time sync — once every 2s

    // ── Cached reflection objects (initialized once) ───────────────
    // Instead of looking up types/methods via reflection every frame
    private MethodInfo  _cachedRunCallbacks;      // SteamAPI.RunCallbacks
    private MethodInfo  _cachedSendP2PPacket;     // SteamNetworking.SendP2PPacket
    private MethodInfo  _cachedIsP2PAvailable;    // SteamNetworking.IsP2PPacketAvailable
    private MethodInfo  _cachedReadP2PPacket;     // SteamNetworking.ReadP2PPacket
    private ConstructorInfo _cachedSteamIdCtor;   // CSteamID(ulong)
    private object      _cachedSendReliable;      // EP2PSend.k_EP2PSendReliable
    private object      _cachedSendUnreliable;    // EP2PSend.k_EP2PSendUnreliable — for high-freq position/anim/time
    private object      _cachedRemoteSteamId;     // CSteamID for RemoteID (updated on change)
    private ulong       _cachedRemoteIdValue;     // last value for which the CSteamID was created

    // Cached local player for SendMyPosition — to avoid FindObjectsOfType every 50ms
    private GameObject  _cachedLocalPlayer;
    private Animator    _cachedLocalAnimator;

    public void Init(ManualLogSource logger)
    {
        _logger = logger;
        Instance = this;
        StartCoroutine(InitDelayed());
    }

    private object _lobbyEnteredCallback;
    private object _lobbyJoinRequestedCallback;
    private object _lobbyChatUpdateCallback;
    private object _p2pSessionRequestCallback;
    private object _p2pSessionConnectFailCallback;  // DIAG (patch A): logs P2PSessionConnectFail_t — transport failures were invisible

    private IEnumerator InitDelayed()
    {
        _logger.LogInfo("[STEAM] Waiting for Steam initialization...");

        float timeout = 60f;
        while (timeout > 0f)
        {
            yield return new WaitForSeconds(0.5f);
            timeout -= 0.5f;

            try
            {
                var asm2 = System.AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetType("Steamworks.SteamUser") != null);
                if (asm2 == null) continue;

                _steamMatchmaking = asm2.GetType("Steamworks.SteamMatchmaking");
                _steamNetworking  = asm2.GetType("Steamworks.SteamNetworking");
                _steamUser        = asm2.GetType("Steamworks.SteamUser");
                _steamFriends     = asm2.GetType("Steamworks.SteamFriends");

                var flags2  = BindingFlags.Public | BindingFlags.Static;
                var steamId = _steamUser?.GetMethod("GetSteamID", flags2)?.Invoke(null, null);
                var name    = _steamFriends?.GetMethod("GetPersonaName", flags2)?.Invoke(null, null);

                _logger.LogInfo($"[STEAM] SteamID: {steamId}");
                _logger.LogInfo($"[STEAM] Name: {name}");

                var lobbyEnteredType       = asm2.GetType("Steamworks.LobbyEnter_t");
                var callbackGeneric        = asm2.GetType("Steamworks.Callback`1");
                var lobbyJoinRequestedType = asm2.GetType("Steamworks.GameLobbyJoinRequested_t");

                if (callbackGeneric != null && lobbyJoinRequestedType != null)
                {
                    var cbConcrete   = callbackGeneric.MakeGenericType(lobbyJoinRequestedType);
                    var delegateType = cbConcrete.GetNestedType("DispatchDelegate");
                    if (delegateType.IsGenericTypeDefinition)
                        delegateType = delegateType.MakeGenericType(lobbyJoinRequestedType);

                    var handler = GetType().GetMethod("OnLobbyJoinRequestedRaw",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    var param = Expression.Parameter(lobbyJoinRequestedType);
                    var call  = Expression.Call(
                        Expression.Constant(this), handler,
                        Expression.Convert(param, typeof(object)));
                    var del = Expression.Lambda(delegateType, call, param).Compile();

                    _lobbyJoinRequestedCallback = cbConcrete
                        .GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new object[] { del });

                    _logger.LogInfo($"[STEAM] LobbyJoinRequested callback: {(_lobbyJoinRequestedCallback != null ? "✓" : "✗")}");
                }

                var lobbyChatUpdateType = asm2.GetType("Steamworks.LobbyChatUpdate_t");
                if (callbackGeneric != null && lobbyChatUpdateType != null)
                {
                    var cbConcrete   = callbackGeneric.MakeGenericType(lobbyChatUpdateType);
                    var delegateType = cbConcrete.GetNestedType("DispatchDelegate");
                    if (delegateType.IsGenericTypeDefinition)
                        delegateType = delegateType.MakeGenericType(lobbyChatUpdateType);

                    var handler = GetType().GetMethod("OnLobbyChatUpdateRaw",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    var param = Expression.Parameter(lobbyChatUpdateType);
                    var call  = Expression.Call(
                        Expression.Constant(this), handler,
                        Expression.Convert(param, typeof(object)));
                    var del = Expression.Lambda(delegateType, call, param).Compile();

                    // The callback MUST be kept in a field — otherwise GC collects the
                    // Callback<T> object, its finalizer unregisters the callback and player exits
                    // stop arriving in OnLobbyChatUpdateRaw.
                    _lobbyChatUpdateCallback = cbConcrete
                        .GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new object[] { del });

                    _logger.LogInfo($"[STEAM] LobbyChatUpdate callback: {(_lobbyChatUpdateCallback != null ? "✓" : "✗")}");
                }

                var p2pRequestType = asm2.GetType("Steamworks.P2PSessionRequest_t");
                if (callbackGeneric != null && p2pRequestType != null)
                {
                    var cbConcrete   = callbackGeneric.MakeGenericType(p2pRequestType);
                    var delegateType = cbConcrete.GetNestedType("DispatchDelegate");
                    if (delegateType.IsGenericTypeDefinition)
                        delegateType = delegateType.MakeGenericType(p2pRequestType);

                    var handler = GetType().GetMethod("OnP2PSessionRequestRaw",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    var param = Expression.Parameter(p2pRequestType);
                    var call  = Expression.Call(
                        Expression.Constant(this), handler,
                        Expression.Convert(param, typeof(object)));
                    var del = Expression.Lambda(delegateType, call, param).Compile();

                    _p2pSessionRequestCallback = cbConcrete
                        .GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new object[] { del });

                    _logger.LogInfo($"[STEAM] P2PSessionRequest callback: {(_p2pSessionRequestCallback != null ? "✓" : "✗")}");
                }

                // DIAG (patch A): P2PSessionConnectFail_t — fired when a P2P session dies (timeout, NAT, etc).
                // Without this we were blind to transport breaks; the symptom was reliable packets silently lost.
                var p2pFailType = asm2.GetType("Steamworks.P2PSessionConnectFail_t");
                if (callbackGeneric != null && p2pFailType != null)
                {
                    var cbConcrete   = callbackGeneric.MakeGenericType(p2pFailType);
                    var delegateType = cbConcrete.GetNestedType("DispatchDelegate");
                    if (delegateType.IsGenericTypeDefinition)
                        delegateType = delegateType.MakeGenericType(p2pFailType);

                    var handler = GetType().GetMethod("OnP2PSessionConnectFailRaw",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    var param = Expression.Parameter(p2pFailType);
                    var call  = Expression.Call(
                        Expression.Constant(this), handler,
                        Expression.Convert(param, typeof(object)));
                    var del = Expression.Lambda(delegateType, call, param).Compile();

                    _p2pSessionConnectFailCallback = cbConcrete
                        .GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new object[] { del });

                    _logger.LogInfo($"[STEAM] P2PSessionConnectFail callback: {(_p2pSessionConnectFailCallback != null ? "✓" : "✗")}");
                }

                _initialized = true;
                CheckLaunchLobbyJoin();
                InitCachedReflection();
                _logger.LogInfo("[STEAM] Ready! F11 = create lobby");

                if (callbackGeneric != null && lobbyEnteredType != null)
                {
                    var cbConcrete   = callbackGeneric.MakeGenericType(lobbyEnteredType);
                    var delegateType = cbConcrete.GetNestedType("DispatchDelegate");

                    if (delegateType.IsGenericTypeDefinition)
                        delegateType = delegateType.MakeGenericType(lobbyEnteredType);

                    var handler = GetType().GetMethod("OnLobbyEnteredRaw",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    var param = Expression.Parameter(lobbyEnteredType);
                    var call  = Expression.Call(
                        Expression.Constant(this), handler,
                        Expression.Convert(param, typeof(object)));
                    var del = Expression.Lambda(delegateType, call, param).Compile();

                    _lobbyEnteredCallback = cbConcrete
                        .GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new object[] { del });

                    _logger.LogInfo($"[STEAM] LobbyEntered callback: {(_lobbyEnteredCallback != null ? "✓" : "✗")}");
                }

                var lobbyCreatedType = asm2.GetType("Steamworks.LobbyCreated_t");
                if (callbackGeneric != null && lobbyCreatedType != null)
                {
                    var cbConcrete   = callbackGeneric.MakeGenericType(lobbyCreatedType);
                    var delegateType = cbConcrete.GetNestedType("DispatchDelegate");
                    if (delegateType.IsGenericTypeDefinition)
                        delegateType = delegateType.MakeGenericType(lobbyCreatedType);

                    var handler = GetType().GetMethod("OnLobbyCreatedRaw",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    var param = Expression.Parameter(lobbyCreatedType);
                    var call  = Expression.Call(
                        Expression.Constant(this), handler,
                        Expression.Convert(param, typeof(object)));
                    var del = Expression.Lambda(delegateType, call, param).Compile();

                    _lobbyCreatedCallback = cbConcrete
                        .GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new object[] { del });

                    _logger.LogInfo($"[STEAM] LobbyCreated callback: {(_lobbyCreatedCallback != null ? "✓" : "✗")}");
                }

                yield break;
            }
            catch (System.Exception e)
            {
                var inner = e.InnerException ?? e;
                _logger.LogInfo($"[STEAM] Not ready yet ({inner.Message}), waiting...");
            }
        }
        _logger.LogError("[STEAM] Steam did not initialize within 60 seconds!");
    }

    private void OnLobbyChatUpdateRaw(object param)
    {
        try
        {
            var flags = BindingFlags.Public | BindingFlags.Instance;
            var userChanged = param.GetType()
                .GetField("m_ulSteamIDUserChanged", flags)?.GetValue(param);
            var stateChange = param.GetType()
                .GetField("m_rgfChatMemberStateChange", flags)?.GetValue(param);

            _logger.LogInfo($"[STEAM] LobbyChatUpdate: user={userChanged}, state={stateChange}");

            // m_ulSteamIDUserChanged is a ulong, not a CSteamID. We take the value
            // directly (previously the code looked for an m_SteamID field on a ulong → always 0,
            // and AcceptP2PSessionWithUser failed because it wanted a CSteamID).
            ulong changedId = userChanged != null ? System.Convert.ToUInt64(userChanged) : 0UL;

            uint state = System.Convert.ToUInt32(stateChange ?? 0);
            if (state == 1 && SteamNetwork.Role == NetworkRole.Host)
            {
                SteamNetwork.RemoteID = changedId;
                _logger.LogInfo($"[STEAM] Client joined! RemoteID={SteamNetwork.RemoteID}");

                var steamIdType = _steamNetworking?.Assembly.GetType("Steamworks.CSteamID");
                var csid = steamIdType != null && changedId != 0
                    ? System.Activator.CreateInstance(steamIdType, changedId) : null;
                if (csid != null)
                {
                    _steamNetworking
                        ?.GetMethod("AcceptP2PSessionWithUser", BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new[] { csid });
                    _logger.LogInfo("[P2P] AcceptP2PSessionWithUser for client ✓");
                }
            }

            // Other player exit/disconnect/kick/ban — remove the stuck clone.
            // EChatMemberStateChange bits: Left=2, Disconnected=4, Kicked=8, Banned=16.
            const uint leftMask = 0x0002 | 0x0004 | 0x0008 | 0x0010;
            if ((state & leftMask) != 0 &&
                changedId != 0 && changedId == SteamNetwork.RemoteID)
            {
                _logger.LogInfo($"[STEAM] Other player left (id={changedId}, state={state}) — removing clone");
                Multiplayer.Instance?.DespawnRemotePlayer();
                ReliableNet.Stop();        // peer gone — quit resending + block until next handshake
                _resyncPending = false; _resyncAttempts = 0;   // nobody left to answer 0x25
                SteamNetwork.RemoteID = 0;
                _lastLobbyMemberCount = 0; // so PollLobbyMembers picks up a new player
            }
        }
        catch (System.Exception e) { _logger.LogError($"[STEAM] LobbyChatUpdate error: {e.Message}"); }
    }

    private void InitCachedReflection()
    {
        try
        {
            var flags = BindingFlags.Public | BindingFlags.Static;

            // SteamAPI.RunCallbacks
            var steamApiType = System.AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } })
                .FirstOrDefault(t => t.Name == "SteamAPI");
            _cachedRunCallbacks = steamApiType?.GetMethod("RunCallbacks", flags);

            // SendP2PPacket, IsP2PPacketAvailable, ReadP2PPacket
            _cachedSendP2PPacket  = _steamNetworking?.GetMethods(flags)
                .FirstOrDefault(m => m.Name == "SendP2PPacket" && m.GetParameters().Length == 5);
            _cachedIsP2PAvailable = _steamNetworking?.GetMethods(flags)
                .FirstOrDefault(m => m.Name == "IsP2PPacketAvailable" && m.GetParameters().Length == 2);
            _cachedReadP2PPacket  = _steamNetworking?.GetMethods(flags)
                .FirstOrDefault(m => m.Name == "ReadP2PPacket" && m.GetParameters().Length == 5);

            // CSteamID constructor and EP2PSend.Reliable
            var steamIdType      = _steamNetworking?.Assembly.GetType("Steamworks.CSteamID");
            _cachedSteamIdCtor   = steamIdType?.GetConstructor(new[] { typeof(ulong) });
            _readSteamIdObj      = _cachedSteamIdCtor?.Invoke(new object[] { 0UL });
            _steamIdField        = steamIdType?.GetField("m_SteamID",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            var ep2pSendType     = _steamNetworking?.Assembly.GetType("Steamworks.EP2PSend");
            _cachedSendReliable   = System.Enum.ToObject(ep2pSendType, 2);   // k_EP2PSendReliable
            _cachedSendUnreliable = System.Enum.ToObject(ep2pSendType, 0);   // k_EP2PSendUnreliable

            _logger.LogInfo($"[CACHE] RunCallbacks: {(_cachedRunCallbacks != null ? "✓" : "✗")}");
            _logger.LogInfo($"[CACHE] SendP2PPacket: {(_cachedSendP2PPacket != null ? "✓" : "✗")}");
            _logger.LogInfo($"[CACHE] IsP2PAvailable: {(_cachedIsP2PAvailable != null ? "✓" : "✗")}");
            _logger.LogInfo($"[CACHE] ReadP2PPacket: {(_cachedReadP2PPacket != null ? "✓" : "✗")}");
            _logger.LogInfo($"[CACHE] SteamIdCtor: {(_cachedSteamIdCtor != null ? "✓" : "✗")}");

            // Wire the C3 reliability layer: it sends raw UNRELIABLE datagrams to RemoteID and hands
            // reassembled inner messages back to the dispatcher (with diag counting).
            ReliableNet.Log       = _logger;
            ReliableNet.RawSend   = bytes => SendRawDatagram(SteamNetwork.RemoteID, bytes, reliable: false);
            ReliableNet.OnDeliver = DispatchReliableInner;
            _logger.LogInfo("[CACHE] ReliableNet wired ✓");
            _logger.LogInfo("[CACHE] Reflection cache initialized ✓");
        }
        catch (Exception e)
        {
            _logger.LogError($"[CACHE] InitCachedReflection error: {e.Message}");
        }
    }

    public void CreateLobby()
    {
        if (!_initialized) { _logger.LogWarning("[STEAM] Not initialized yet!"); return; }

        // The symmetric half of the dual-host guard: a CLIENT pressing F11 inside the friend's world must
        // not hijack Role=Host mid-session (their world is the host's save — hosting it would fork it).
        if (SteamNetwork.Role == NetworkRole.Client)
        {
            _logger.LogWarning("[STEAM] F11 ignored — you are in a co-op session as the GUEST (only the host creates lobbies)");
            ShowJoinError("You are playing in your friend's world right now — F11 (host a game)\n" +
                          "is disabled. To host your own game, first exit to the main menu.");
            return;
        }

        if (SteamNetwork.Role == NetworkRole.Host && SteamNetwork.LobbyID != 0)
        {
            _logger.LogInfo("[STEAM] Lobby already created! Opening overlay...");
            OpenInviteOverlay(SteamNetwork.LobbyID);
            return;
        }

        SteamNetwork.Role = NetworkRole.Host;

        try
        {
            var lobbyTypeEnum = _steamMatchmaking.Assembly.GetType("Steamworks.ELobbyType");
            object lobbyType  = System.Enum.ToObject(lobbyTypeEnum, 2); // FriendsOnly

            var handle = _steamMatchmaking
                .GetMethod("CreateLobby", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new object[] { lobbyType, 2 });

            _logger.LogInfo($"[STEAM] CreateLobby called! handle={handle}");
        }
        catch (System.Exception e)
        {
            SteamNetwork.Role = NetworkRole.None;
            _logger.LogError($"[STEAM] CreateLobby error: {e.Message}");
        }
    }

    // Routing entry point. Three transport classes (see ReliableNet for the why):
    //   0x06/0x07/0x08, 0x25/0x26 → raw UNRELIABLE (high-freq superseded next tick / resync control beaten by repetition)
    //   0x01-0x05, 0x22, 0x23     → native Steam RELIABLE (handshake/save/version — setup + host→client, proven OK)
    //   everything else (gameplay)→ ReliableNet (TCP-lite over unreliable; Steam reliable drops client→host on SDK 1.42)
    public void SendPacket(ulong targetSteamId, byte[] data, int channel = 0)
    {
        if (data == null || data.Length == 0) return;
        byte t = data[0];

        // Log service packets (not high-freq position/anim/time, not the 1/s resync control spam)
        if (t != 0x06 && t != 0x07 && t != 0x08 && t != 0x25 && t != 0x26)
            _logger.LogInfo($"[P2P] SendPacket to {targetSteamId}: type=0x{t:X2} dataLen={data.Length}");

        if (t == 0x06 || t == 0x07 || t == 0x08)
            SendRawDatagram(targetSteamId, data, reliable: false);
        else if (t == 0x25 || t == 0x26)
            // Resync control MUST ride raw unreliable: it repairs ReliableNet (can't ride what it fixes) and
            // native reliable client→host is the proven-broken channel this whole layer exists to replace.
            // Loss is beaten by repetition (0x25 re-sent every second until acked by 0x26).
            SendRawDatagram(targetSteamId, data, reliable: false);
        else if (t <= 0x05 || t == 0x22 || t == 0x23)
            SendRawDatagram(targetSteamId, data, reliable: true);
        else
            ReliableNet.Send(data);
    }

    // Actual SteamNetworking.SendP2PPacket call — no routing. Used by SendPacket and (unreliable) by ReliableNet.
    public bool SendRawDatagram(ulong targetSteamId, byte[] data, bool reliable)
    {
        if (_cachedSendP2PPacket == null || _cachedSteamIdCtor == null) return false;
        try
        {
            // Rebuild the CSteamID only if RemoteID changed — otherwise use the cached one
            if (_cachedRemoteSteamId == null || _cachedRemoteIdValue != targetSteamId)
            {
                _cachedRemoteSteamId = _cachedSteamIdCtor.Invoke(new object[] { targetSteamId });
                _cachedRemoteIdValue = targetSteamId;
            }

            var sendType = reliable ? _cachedSendReliable : _cachedSendUnreliable;
            _cachedSendP2PPacket.Invoke(null,
                new object[] { _cachedRemoteSteamId, data, (uint)data.Length, sendType, 0 });
            return true;
        }
        catch (System.Exception e) { _logger.LogError($"[P2P] Send error: {e.Message}"); return false; }
    }

    private void OnP2PSessionRequestRaw(object param)
    {
        try
        {
            var flags   = BindingFlags.Public | BindingFlags.Instance;
            var steamId = param.GetType().GetField("m_steamIDRemote", flags)?.GetValue(param);

            var rawId = steamId?.GetType()
                .GetField("m_SteamID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(steamId);
            SteamNetwork.RemoteID = System.Convert.ToUInt64(rawId ?? 0UL);
            _logger.LogInfo($"[P2P] SessionRequest from: {SteamNetwork.RemoteID}");

            _steamNetworking
                ?.GetMethod("AcceptP2PSessionWithUser", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new[] { steamId });
            _logger.LogInfo("[P2P] AcceptP2PSessionWithUser ✓");
        }
        catch (System.Exception e) { _logger.LogError($"[P2P] SessionRequest error: {e.Message}"); }
    }

    // DIAG (patch A): P2P session died. m_eP2PSessionError: 0=None 1=NotRunningApp 2=NoRightsToApp
    // 3=DestinationNotLoggedIn 4=Timeout. A Timeout/repeating fail mid-session = transport is the culprit
    // (→ migrate to SteamNetworkingMessages). uid + error printed so the live test is decisive.
    private void OnP2PSessionConnectFailRaw(object param)
    {
        try
        {
            var flags = BindingFlags.Public | BindingFlags.Instance;
            var steamId = param.GetType().GetField("m_steamIDRemote", flags)?.GetValue(param);
            var rawId = steamId?.GetType()
                .GetField("m_SteamID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(steamId);
            var errObj = param.GetType().GetField("m_eP2PSessionError", flags)?.GetValue(param);
            int err = errObj != null ? System.Convert.ToInt32(errObj) : -1;
            string name = err == 0 ? "None" : err == 1 ? "NotRunningApp" : err == 2 ? "NoRightsToApp"
                        : err == 3 ? "DestinationNotLoggedIn" : err == 4 ? "Timeout" : "Unknown";
            _logger.LogError($"[P2P-FAIL] Session with {System.Convert.ToUInt64(rawId ?? 0UL)} FAILED — error={err}({name})");
        }
        catch (System.Exception e) { _logger.LogError($"[P2P-FAIL] handler error: {e.Message}"); }
    }

    // Reusable objects for ReadPacket — no allocations every frame
    private object[] _readAvailArgs = new object[] { (uint)0, 0 };
    private object _readSteamIdObj;
    private FieldInfo _steamIdField;

    public byte[] ReadPacket(int channel = 0)
    {
        if (_cachedIsP2PAvailable == null || _cachedReadP2PPacket == null) return null;
        try
        {
            _readAvailArgs[0] = (uint)0;
            _readAvailArgs[1] = channel;
            bool avail = (bool)(_cachedIsP2PAvailable.Invoke(null, _readAvailArgs) ?? false);
            if (!avail) return null;

            uint size = (uint)_readAvailArgs[0];
            if (size == 0) return null;

            var buffer   = new byte[size];
            uint bytesRead = 0;

            var readArgs = new object[] { buffer, size, bytesRead, _readSteamIdObj, channel };
            _cachedReadP2PPacket.Invoke(null, readArgs);

            // Store RemoteID if not set yet
            if (SteamNetwork.RemoteID == 0 && _steamIdField != null)
            {
                var rawId = _steamIdField.GetValue(readArgs[3]);
                SteamNetwork.RemoteID = System.Convert.ToUInt64(rawId ?? 0UL);
                if (SteamNetwork.RemoteID != 0)
                    _logger.LogInfo($"[P2P] RemoteID set: {SteamNetwork.RemoteID}");
            }

            return buffer;
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[P2P] Read error: {e.Message}");
            return null;
        }
    }

    private ulong _lastJoinedLobbyId = 0;
    private float _lastJoinTime = -999f;

    private void OnLobbyJoinRequestedRaw(object param)
    {
        try
        {
            var flags   = BindingFlags.Public | BindingFlags.Instance;
            var lobbyId = param.GetType().GetField("m_steamIDLobby", flags)?.GetValue(param);

            ulong id = System.Convert.ToUInt64(
                lobbyId?.GetType().GetField("m_SteamID",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(lobbyId) ?? 0UL);

            _logger.LogInfo($"[STEAM] JoinRequested! lobbyId={id}");

            // Steam sometimes fires the callback twice in a row — ignore the duplicate
            float now = UnityEngine.Time.time;
            if (id == _lastJoinedLobbyId && (now - _lastJoinTime) < 5f)
            {
                _logger.LogInfo("[STEAM] JoinRequested duplicate — ignoring ✓");
                return;
            }
            _lastJoinedLobbyId = id;
            _lastJoinTime = now;

            StartCoroutine(JoinLobbyDelayed(id));
        }
        catch (System.Exception e) { _logger.LogError($"[STEAM] JoinRequested error: {e.Message}"); }
    }

    private void CheckLaunchLobbyJoin()
    {
        try
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "+connect_lobby")
                {
                    ulong lobbyId = ulong.Parse(args[i + 1]);
                    _logger.LogInfo($"[STEAM] Found +connect_lobby: {lobbyId}");
                    StartCoroutine(JoinLobbyDelayed(lobbyId));
                    return;
                }
            }
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[STEAM] CheckLaunchLobbyJoin error: {e.Message}");
        }
    }

    private IEnumerator JoinLobbyDelayed(ulong lobbyId)
    {
        yield return new WaitForSeconds(1f);

        // DUAL-HOST GUARD (ELance262's root cause, 2026-07-03): if this player pressed F11 earlier,
        // CreateLobby already set Role=Host — and OnLobbyEntered skips the client branch for hosts, so
        // accepting an invite "connected" two SEPARATE worlds: each played their own save, and the only
        // things crossing were movement and time. Accepting an invite is an explicit "I want to JOIN" —
        // an empty own lobby must yield: leave it and proceed as a clean client.
        if (SteamNetwork.Role == NetworkRole.Host && SteamNetwork.RemoteID != 0)
        {
            // A host with a LIVE session keeps hosting — silently dropping their current guest would be worse.
            _logger.LogWarning($"[STEAM] Join request ignored — already hosting an ACTIVE session (client {SteamNetwork.RemoteID})");
            ShowJoinError("You are hosting a co-op session right now, so the invite was ignored.\n" +
                          "To join your friend instead: exit to the main menu and accept the invite\n" +
                          "WITHOUT pressing F11 first (F11 = host your own game).");
            yield break;
        }
        if (SteamNetwork.Role == NetworkRole.Host)
        {
            try
            {
                if (SteamNetwork.LobbyID != 0)
                {
                    var sidType  = _steamMatchmaking?.Assembly.GetType("Steamworks.CSteamID");
                    var ownLobby = System.Activator.CreateInstance(sidType, SteamNetwork.LobbyID);
                    _steamMatchmaking?.GetMethod("LeaveLobby", BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new[] { ownLobby });
                    _logger.LogInfo("[STEAM] Left own empty lobby ✓");
                }
                SteamNetwork.Role    = NetworkRole.None;
                SteamNetwork.LobbyID = 0;
                _logger.LogWarning("[STEAM] F11 was pressed earlier (empty host lobby), but an invite was accepted — switching to CLIENT ✓");
            }
            catch (System.Exception e) { _logger.LogError($"[STEAM] Dual-host guard error: {e.Message}"); }
        }

        try
        {
            var steamIdType = _steamMatchmaking?.Assembly.GetType("Steamworks.CSteamID");
            var steamIdObj  = System.Activator.CreateInstance(steamIdType, lobbyId);

            _steamMatchmaking
                ?.GetMethod("JoinLobby", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new[] { steamIdObj });

            _logger.LogInfo($"[STEAM] JoinLobby called! id={lobbyId}");
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[STEAM] JoinLobby error: {e.Message}");
        }
    }

    private int _lastLobbyMemberCount = 0;

    // Time of the last received packet. If a clone exists but packets have been gone for a long time —
    // the other player left/crashed/network broke. Works for both roles,
    // unlike the LobbyChatUpdate callback, which doesn't dispatch in this game.
    private float _lastRemotePacketTime = 0f;
    private const float REMOTE_PACKET_TIMEOUT = 5f;

    // ── DIAG (patch A): localize the client→host packet loss ──────────────────────────
    // Heartbeat 0x24 [type][int32 seq]: both sides send reliably every 2s. The receiver logs seq + gap,
    // so we measure reliable delivery + loss per direction independent of gameplay actions. Receive counters
    // (pos = unreliable 0x06/07/08, other = everything else) flush every 2s. Combined with [P2P-FAIL] this
    // tells us: transport dying (→ go SteamNetworkingMessages) vs a half-open session. Remove after diagnosis.
    private float _heartbeatTimer = 0f;
    private int   _heartbeatSeqOut = 0;
    private int   _heartbeatSeqIn  = -1;
    private float _netDiagTimer = 0f;
    private int   _diagRecvPos = 0;
    private int   _diagRecvOther = 0;
    private int   _diagRecvHeartbeat = 0;

    // ── DEGRADED-LINK DETECTOR ──────────────────────────────────────────────────────────
    // Zombie state seen in the wild (Nexus report 2026-07-03): raw unreliable keeps flowing (movement and
    // time-of-day look fine) while the ReliableNet session is dead — e.g. a spurious lobby "left" event
    // called ReliableNet.Stop() and nothing ever re-armed it (only a fresh client 0x01 does). Every gameplay
    // sync silently stops and the players only notice much later, reporting it as many separate bugs.
    // The 0x24 heartbeat rides ReliableNet every 2s from both sides, so "unreliable fresh + no reliable
    // delivery for 15s" is a dependable degraded signal. Warn ON SCREEN so players restart the session.
    private const float REL_SILENCE_WARN = 15f;
    private float _lastReliableInTime;    // any ReliableNet-delivered inner message (heartbeat guarantees ≥1/2s)
    private float _lastUnreliableInTime;  // any raw 0x06/0x07/0x08 arrival
    private float _relEligibleSince;      // when the detector's preconditions became true (grace anchor)
    private bool  _degradedWarned;

    // ── AUTO-RESYNC (0x25 request / 0x26 confirm, raw unreliable, epoch byte payload) ──
    // When the detector trips, don't just warn — rebuild the reliable layer in place: the initiator
    // PrepareResync()s to the next epoch and asks the peer to Reset() to it; the 0x26 confirm Resume()s the
    // initiator. Stale wire frames from the old generation are dropped by the epoch check in ReliableNet.
    // The dialog becomes the FALLBACK for when the peer never answers (RESYNC_DIALOG_AFTER).
    private const float RESYNC_DIALOG_AFTER = 20f;
    private byte  _relEpoch;              // last epoch both sides agreed on (0 = the 0x01-handshake generation)
    private bool  _resyncPending;
    private byte  _resyncPendingEpoch;
    private float _resyncSendTimer;
    private int   _resyncAttempts;        // consecutive resyncs without a real recovery (episode counter)
    private float _resyncEpisodeStartedAt;

    void Update()
    {
        if (!_initialized) return;

        // ── Cached RunCallbacks — no assembly lookup every frame ──────────
        _cachedRunCallbacks?.Invoke(null, null);

        if (SteamNetwork.Role == NetworkRole.Host && SteamNetwork.LobbyID != 0)
            PollLobbyMembers();

        // Read all available packets in a single Update (bumped: fragmented reliable bursts during initial sync)
        int maxPacketsPerFrame = 64;
        for (int i = 0; i < maxPacketsPerFrame; i++)
        {
            var packet = ReadPacket();
            if (packet == null || packet.Length == 0) break;
            _lastRemotePacketTime = Time.time;
            byte pt = packet[0];

            // Isolate per-packet dispatch: a throw in any handler (rogue NRE, malformed payload) must NOT
            // escape Update — otherwise Unity skips the rest of the frame, including ReliableNet.Tick below
            // (the ACK/retransmit pump), stalling the reliable layer. One bad packet ≠ a stalled session.
            try
            {
                // Reliability framing (0xFE data / 0xFF ack) → ReliableNet; it delivers reassembled inner
                // messages back through DispatchReliableInner (which counts + dispatches). Don't log framing.
                if (pt == 0xFE || pt == 0xFF) { ReliableNet.HandleIncoming(packet); continue; }

                // DIAG: classify raw arrivals (position = unreliable; native-setup = "other")
                if (pt == 0x06 || pt == 0x07 || pt == 0x08) { _diagRecvPos++; _lastUnreliableInTime = Time.time; }
                else _diagRecvOther++;
                if (pt != 0x06 && pt != 0x07 && pt != 0x08)
                    _logger.LogInfo($"[P2P] Received packet {packet.Length} bytes, type={pt}");
                OnPacketReceived(packet);
            }
            catch (Exception e)
            {
                _logger.LogError($"[P2P] Dispatch threw for type=0x{pt:X2} ({packet.Length}b): {e}");
            }
        }

        // Drive the reliability layer: retransmit unacked fragments + flush cumulative acks (runs always)
        ReliableNet.Tick(Time.deltaTime);

        // DIAG (patch A): flush receive counters every 2s — shows reliable vs unreliable arrival per direction
        _netDiagTimer += Time.deltaTime;
        if (_netDiagTimer >= 2f)
        {
            _netDiagTimer = 0f;
            if (SteamNetwork.RemoteID != 0 && Multiplayer.DebugMode)
                _logger.LogInfo($"[NET-DIAG] recv last 2s: pos(unrel)={_diagRecvPos} hb={_diagRecvHeartbeat} other(rel)={_diagRecvOther} | lastHbSeqIn={_heartbeatSeqIn} hbSeqOut={_heartbeatSeqOut}");
            _diagRecvPos = _diagRecvOther = _diagRecvHeartbeat = 0;
        }

        TickDegradedDetector();

        // Clone is spawned, but packets from the other player are gone — they left
        if (SteamNetwork.RemotePlayerSpawned &&
            Time.time - _lastRemotePacketTime > REMOTE_PACKET_TIMEOUT)
        {
            _logger.LogInfo($"[STEAM] Packets from the other player gone for {REMOTE_PACKET_TIMEOUT}s — removing clone");
            Multiplayer.Instance?.DespawnRemotePlayer();
        }

        // Send our position if connected and in game
        if (SteamNetwork.IsConnected && SteamNetwork.IsInGame && SteamNetwork.RemoteID != 0)
        {
            _sendTimer += Time.deltaTime;
            if (_sendTimer >= SEND_INTERVAL)
            {
                _sendTimer = 0f;
                SendMyPosition();
            }

            ChopSync.TickNpc();      // 0x20 NPC sync DISABLED via SYNC_NPC_POSITIONS flag (Stardew: NPCs are personal)
            ChopSync.TickGardens();  // host-reconcile of garden growth (host sends 0x21 for changed beds)
            ChopSync.TickReconcile(); // post-resync world sweep (drip-fed; no-op when the queue is empty)

            _timeSyncTimer += Time.deltaTime;
            if (_timeSyncTimer >= TIME_SYNC_INTERVAL)
            {
                _timeSyncTimer = 0f;
                SendTimeSync();
            }

            // DIAG (patch A): reliable heartbeat 0x24 every 2s — measures reliable delivery + loss per direction
            _heartbeatTimer += Time.deltaTime;
            if (_heartbeatTimer >= 2f)
            {
                _heartbeatTimer = 0f;
                var hb = new byte[5];
                hb[0] = 0x24;
                System.Buffer.BlockCopy(System.BitConverter.GetBytes(_heartbeatSeqOut++), 0, hb, 1, 4);
                SendPacket(SteamNetwork.RemoteID, hb);
            }

            // 🔬 TIME-DIAG (2026-06-12): "the partner's day period doesn't change" —
            // full time-domain snapshot once every 15s on BOTH machines. Remove after the root cause.
            TickTimeDiag();

            // Carried corpse: per-frame movement tracking (internal throttle on send).
            ChopSync.TickCarriedBodies();

            // Stage 3a: re-claim live crafts (keeps the TTL alive at the partner) + cleanup of dead ones.
            ChopSync.TickCraftClaims();

            // Weather: re-send the host's schedule as soon as there's someone (including late-join).
            ChopSync.TickWeatherSync();

            // Story: flush story ops that arrived before entering the world.
            StorySync.TickFlushPending();

            // Trees chopped while we were in another location — we try to destroy them
            // when the player reaches them and they load.
            _chopRetryTimer += Time.deltaTime;
            if (_chopRetryTimer >= CHOP_RETRY_INTERVAL)
            {
                _chopRetryTimer = 0f;
                ChopSync.RetryPendingDestroys();
                ChopSync.RetryPendingDrops();
                ChopSync.RetryPendingBigPickups();
                ChopSync.RetryPendingGardens();
            }
        }
    }

    private float _chopRetryTimer = 0f;
    private const float CHOP_RETRY_INTERVAL = 2f;

    // 🔬 TIME-DIAG: time-domain snapshot (day/_cur_time/clock/TimeOfDay/preset). Hypotheses: (a)
    // _auto_adjust_time=false (stopped=True with ctrl=True); (b) time_of_day==null (clock runs, visuals dead);
    // (c) cur_preset="inside" stuck; (d) everything ticks → problem is elsewhere.
    private float _timeDiagTimer = 0f;
    private const float TIME_DIAG_INTERVAL = 15f;

    private void TickTimeDiag()
    {
        _timeDiagTimer += Time.deltaTime;
        if (_timeDiagTimer < TIME_DIAG_INTERVAL) return;
        _timeDiagTimer = 0f;
        try
        {
            GameTimeSync.TryRead(out int day, out float curTime);
            string auto = "?", stopped = "?", ctrl = "?", envTod = "?", state = "?";
            var env = EnvironmentEngine.me;
            if (env != null)
            {
                auto    = env.auto_adjust_time.ToString();
                stopped = env.IsTimeStopped().ToString();
                envTod  = env.time_of_day == null ? "NULL!" : "ok";
                state   = env.data != null ? env.data.state.ToString() : "data=null";
            }
            string preset = EnvironmentEngine.cur_preset == null ? "-" : EnvironmentEngine.cur_preset.name;
            try
            {
                var pc = MainGame.me != null ? MainGame.me.player_char : null;
                ctrl = pc != null ? pc.control_enabled.ToString() : "char=null";
            }
            catch { }
            string tod = "me=NULL!";
            var t = TimeOfDay.me;
            if (t != null) tod = $"{t.time_of_day:F3}({t.time_of_day_enum})";
            // Live weather VALUES (not the schedule!) — comparing these between machines shows the real sky
            // divergence. en= is Rain's _enabled (a disabled state drives value=0 despite a healthy value —
            // how the friend "didn't see rain" at rain=1.50).
            string weather = "?";
            try
            {
                if (env != null)
                {
                    if (_weatherEnabledField == null)
                        _weatherEnabledField = typeof(SmartWeatherState).GetField("_enabled",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                    var rainSt = env.FindStateByType(SmartWeatherState.WeatherType.Rain);
                    string en = _weatherEnabledField != null && rainSt != null
                        ? ((bool)_weatherEnabledField.GetValue(rainSt)).ToString() : "?";
                    weather = $"rain={rainSt.value:F2}(en={en}) " +
                              $"fog={env.FindStateByType(SmartWeatherState.WeatherType.Fog).value:F2} " +
                              $"wind={env.FindStateByType(SmartWeatherState.WeatherType.Wind).value:F2}";
                }
            }
            catch { }
            _logger.LogInfo($"[TIME-DIAG] day={day} dow={day % 6} cur={curTime:F3} auto={auto} stopped={stopped} " +
                            $"ctrl={ctrl} tod={tod} envTod={envTod} preset={preset} state={state} " +
                            $"lightK={TimeOfDay.light_intensity_k:F2} {weather}");

            // Every 4th tick (60s) — compact dump of the nature line: shows whether a schedule
            // entry survives to its t_start, or whether something removes it early.
            if (++_lineDumpCounter >= 4)
            {
                _lineDumpCounter = 0;
                try
                {
                    var line = env?.data?.nature_weather_line;
                    if (line != null)
                    {
                        var parts = new List<string>();
                        foreach (var st in line)
                            if (st != null)
                                parts.Add($"{st.preset_name}/{st.type}@{st.t_start:F2}" +
                                          (st.start_removing_time > 1f ? $"(rem@{st.start_removing_time:F2})" : ""));
                        _logger.LogInfo($"[LINE-DIAG] {(parts.Count > 0 ? string.Join("; ", parts.ToArray()) : "EMPTY")}");
                    }
                }
                catch { }
            }
        }
        catch (Exception e) { _logger.LogWarning($"[TIME-DIAG] failed: {e.Message}"); }
    }
    private int _lineDumpCounter;
    private FieldInfo _weatherEnabledField;   // SmartWeatherState._enabled (DIAG en=)

    // Packet 0x08: type(1) + day(4 int) + cur_time(4 float) = 9 bytes
    private readonly byte[] _timePacket = new byte[9];

    private void SendTimeSync()
    {
        if (!GameTimeSync.TryRead(out int day, out float curTime)) return;
        _timePacket[0] = 0x08;
        System.BitConverter.GetBytes(day).CopyTo(_timePacket, 1);
        System.BitConverter.GetBytes(curTime).CopyTo(_timePacket, 5);
        SendPacket(SteamNetwork.RemoteID, _timePacket);
    }

    private void PollLobbyMembers()
    {
        try
        {
            var flags       = BindingFlags.Public | BindingFlags.Static;
            var steamIdType = _steamMatchmaking?.Assembly.GetType("Steamworks.CSteamID");
            var lobbyIdObj  = System.Activator.CreateInstance(steamIdType, SteamNetwork.LobbyID);

            int count = (int)(_steamMatchmaking
                ?.GetMethod("GetNumLobbyMembers", flags)
                ?.Invoke(null, new[] { lobbyIdObj }) ?? 0);

            if (count == _lastLobbyMemberCount) return;
            _logger.LogInfo($"[STEAM] Lobby members: {count}");
            _lastLobbyMemberCount = count;

            if (count < 2)
            {
                // Client left the lobby — remove the clone immediately (don't wait for the timeout)
                Multiplayer.Instance?.DespawnRemotePlayer();
                return;
            }

            var clientSteamId = _steamMatchmaking
                ?.GetMethod("GetLobbyMemberByIndex", flags)
                ?.Invoke(null, new object[] { lobbyIdObj, 1 });

            var rawId = clientSteamId?.GetType()
                .GetField("m_SteamID",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(clientSteamId);

            SteamNetwork.RemoteID = System.Convert.ToUInt64(rawId ?? 0UL);
            _logger.LogInfo($"[STEAM] Client found! RemoteID={SteamNetwork.RemoteID}");

            _steamNetworking
                ?.GetMethod("AcceptP2PSessionWithUser", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new[] { clientSteamId });
            _logger.LogInfo("[P2P] AcceptP2PSessionWithUser ✓");

            // Host is already in game if RemoteID was just set — enable position sync. We can't rely on
            // OnGameStartedPlayingPatch (it fires before the Host role is set). BUT only if HostInWorld() —
            // else a premature IsInGame=true makes the host absorb the client's intro pairs.
            if (SteamNetwork.Role == NetworkRole.Host && !SteamNetwork.IsInGame && SteamNetwork.HostInWorld())
            {
                SteamNetwork.IsInGame = true;
                SteamNetwork.IsInGameSince = UnityEngine.Time.time;
                _logger.LogInfo("[STEAM] Host IsInGame=true — position sync activated ✓");
            }
        }
        catch (System.Exception e) { _logger.LogError($"[P2P] PollLobby error: {e.Message}"); }
    }

    // See the field block above REL_SILENCE_WARN for the why. Preconditions: in game with a peer AND raw
    // unreliable arriving (<5s old) — so a peer that truly left (all packets stop → clone despawn) never
    // trips this. Grace: silence is measured from _relEligibleSince, not from 0, so a fresh session has
    // REL_SILENCE_WARN seconds to deliver its first reliable message before we'd ever warn.
    private void TickDegradedDetector()
    {
        bool eligible = SteamNetwork.IsInGame && SteamNetwork.RemoteID != 0 &&
                        Time.time - _lastUnreliableInTime < 5f;
        if (!eligible) { _relEligibleSince = 0f; return; }
        if (_relEligibleSince == 0f) _relEligibleSince = Time.time;

        // Keep asking until the peer answers (0x26 clears _resyncPending). 1s cadence: 5-byte datagram,
        // and repetition is the loss strategy for this raw-unreliable control channel.
        if (_resyncPending)
        {
            _resyncSendTimer += Time.deltaTime;
            if (_resyncSendTimer >= 1f)
            {
                _resyncSendTimer = 0f;
                SendPacket(SteamNetwork.RemoteID, new byte[] { 0x25, _resyncPendingEpoch });
            }
        }

        // Two eyes (2026-07-03 live test: Stop() keeps _recvNext, so the broken side still receives the peer
        // fine and inbound silence fires only on the OTHER machine): inbound = peer's reliable went quiet;
        // outbound = our own sends are blocked or unacked (ReliableNet self-reports).
        float relSilence  = Time.time - Mathf.Max(_lastReliableInTime, _relEligibleSince);
        bool inboundDead  = relSilence > REL_SILENCE_WARN;
        bool outboundDead = ReliableNet.OutboundLooksDead;
        if (inboundDead || outboundDead)
        {
            if (!_resyncPending)
            {
                if (_resyncAttempts == 0) _resyncEpisodeStartedAt = Time.time;
                _resyncAttempts++;
                _resyncPendingEpoch = (byte)(_relEpoch + 1);
                _resyncPending   = true;
                _resyncSendTimer = 1f;   // fire the first 0x25 on the next tick
                ReliableNet.PrepareResync(_resyncPendingEpoch);
                _logger.LogWarning("[NET] DEGRADED LINK: " +
                    (inboundDead ? $"no reliable delivery from the peer for {relSilence:F0}s" : "inbound OK") + " | " +
                    (outboundDead ? $"outbound dead: {ReliableNet.OutboundDeathReason}" : "outbound OK") +
                    $" — starting auto-resync (epoch {_resyncPendingEpoch}, attempt {_resyncAttempts})");
            }

            // Fallback dialog counts the EPISODE, not the attempt (live test 2026-07-03 #3: a resync "completes"
            // and immediately re-fires when the silence isn't cured, so a per-attempt timer never reaches 20s).
            if (!_degradedWarned && _resyncAttempts >= 2 && Time.time - _resyncEpisodeStartedAt > RESYNC_DIALOG_AFTER)
            {
                _degradedWarned = true;
                _logger.LogError($"[NET] DEGRADED LINK: still down after {_resyncAttempts} resync attempts over {Time.time - _resyncEpisodeStartedAt:F0}s — gameplay sync is DOWN (movement/time still look fine)");
                ShowJoinError("Co-op connection problem detected!\n" +
                              "You can still see each other move, but items, stations and world changes\n" +
                              "have STOPPED syncing, and automatic repair isn't taking hold.\n\n" +
                              "Fix: the joined player should exit to the main menu and re-join the host\n" +
                              "(Steam invite or friends list -> Join Game).\n\n" +
                              "If this keeps happening, please send BepInEx/LogOutput.log from both\n" +
                              "players to the mod page.");
            }
        }
        else if ((_degradedWarned || _resyncAttempts > 0) &&
                 Time.time - _lastReliableInTime < 5f && !ReliableNet.OutboundLooksDead)
        {
            // Both directions healthy again (resync or re-join) — close the episode, re-arm for a future one.
            _degradedWarned = false;
            _resyncAttempts = 0;
            _logger.LogInfo("[NET] Reliable channel recovered ✓ — degraded episode closed");
        }
    }

    // 0x25 arrived: the peer's detector tripped and it already PrepareResync()ed to epoch e — realign to it.
    private void OnResyncRequest(byte e)
    {
        if (_resyncPending && e == _resyncPendingEpoch)
        {
            // Both-initiators race (both detectors tripped, both bumped from the same _relEpoch). We already
            // cleared+aligned in PrepareResync — a full Reset here could arrive AFTER the peer's post-Reset
            // traffic (UDP reorder) and rewind _recvNext below it, redelivering duplicates. Just unblock+confirm.
            _relEpoch = e; _resyncPending = false;
            ReliableNet.Resume();
            _relEligibleSince = Time.time;   // grace: give heartbeats REL_SILENCE_WARN s to cure the old silence
            _logger.LogWarning($"[NET] Peer requested the resync we were also requesting → realigned (epoch {e}) ✓");
            for (int i = 0; i < 3; i++)
                SendPacket(SteamNetwork.RemoteID, new byte[] { 0x26, e });
            if (SteamNetwork.Role == NetworkRole.Host) ChopSync.ReconcileAfterResync();
            return;
        }

        if (e == _relEpoch)
        {
            // Duplicate of a request we already applied (its 0x26 got lost) — just confirm again, do NOT
            // Reset a second time: new-generation traffic is already flowing and a rewind would desync it.
            SendPacket(SteamNetwork.RemoteID, new byte[] { 0x26, e });
            return;
        }

        _relEpoch = e;
        ReliableNet.Reset(e);
        // Grace after the realign: our own inbound-silence counter predates this fresh stream — measuring it
        // now would instantly fire OUR detector and bump the epoch again, killing the fresh stream in turn.
        // That exact ping-pong looped epochs 2..40+ in live test #3 (2026-07-03) until nothing could flow.
        _relEligibleSince = Time.time;
        _logger.LogWarning($"[NET] Peer requested reliable-layer resync → realigned to epoch {e} ✓");
        for (int i = 0; i < 3; i++)   // burst the confirm — raw unreliable, cheap insurance
            SendPacket(SteamNetwork.RemoteID, new byte[] { 0x26, e });
        if (SteamNetwork.Role == NetworkRole.Host) ChopSync.ReconcileAfterResync();   // heal the WORLD, not just the channel
    }

    // 0x26 arrived: the peer realigned to our pending epoch — unblock our sending half.
    private void OnResyncConfirm(byte e)
    {
        if (!_resyncPending || e != _resyncPendingEpoch) return;   // stray/stale confirm
        _relEpoch      = e;
        _resyncPending = false;
        ReliableNet.Resume();
        _relEligibleSince = Time.time;   // grace: the stale silence needs REL_SILENCE_WARN s for heartbeats to cure it
        _logger.LogWarning($"[NET] Peer confirmed resync — reliable layer restored ✓ (epoch {e})");
        if (SteamNetwork.Role == NetworkRole.Host) ChopSync.ReconcileAfterResync();   // heal the WORLD, not just the channel
    }

    private byte[] _saveBuffer;
    private int    _saveBufferExpectedSize;
    private int    _saveChunksReceived;
    private Vector3 _hostSpawnPosition = Vector3.zero;
    private bool    _coopSaveWritten;   // we wrote the coop save into CoopSaveSlot this session → remove on exit

    // The client writes the host save into CoopSaveSlot. On exit we remove .dat+.info so no junk slot is
    // left in the load menu. The _coopSaveWritten gate: clean up ONLY what we wrote (user slots untouched).
    private void OnApplicationQuit()
    {
        // M1: save the client's character layer before the game closes (the player is still in memory).
        // Separate file coop_character_* — unlike the coop slot 999999, we do NOT clean it up.
        ClientCharacterStore.Save();

        if (!_coopSaveWritten) return;
        foreach (var ext in new[] { ".dat", ".info" })
        {
            try
            {
                var p = System.IO.Path.Combine(Application.persistentDataPath, SteamNetwork.CoopSaveSlot + ext);
                if (System.IO.File.Exists(p)) { System.IO.File.Delete(p); _logger.LogInfo($"[CLIENT] Coop slot removed: {p} ✓"); }
            }
            catch (Exception e) { _logger.LogWarning($"[CLIENT] Coop slot cleanup {ext} failed: {e.Message}"); }
        }
    }

    private static void NoOpDelegate() { }

    // Show the client ON SCREEN why the join was cancelled, so they don't think it's a mod bug. The client is
    // on the title/menu (no character for a bubble), so we use GUIElements.me.dialog (DialogGUI, works in the
    // menu). Reflection: DialogGUI.Open's button delegate is GJCommons.VoidDelegate from a firstpass assembly
    // we don't reference (its globals collide → CS0436), so we build the delegate dynamically.
    private void ShowJoinError(string message)
    {
        try
        {
            var guiType = AccessTools.TypeByName("GUIElements");
            var me = guiType?.GetProperty("me", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var dialog = me?.GetType().GetField("dialog", BindingFlags.Public | BindingFlags.Instance)?.GetValue(me);
            if (dialog == null) { _logger.LogWarning("[JOIN] Dialog unavailable — message only in the log"); return; }

            var openM = dialog.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Open" && m.GetParameters().Length >= 3 &&
                                     m.GetParameters()[0].ParameterType == typeof(string) &&
                                     m.GetParameters()[1].ParameterType == typeof(string));
            if (openM == null) { _logger.LogWarning("[JOIN] DialogGUI.Open not found — message only in the log"); return; }

            var ps = openM.GetParameters();
            var noop = Delegate.CreateDelegate(ps[2].ParameterType,
                typeof(SteamManager).GetMethod("NoOpDelegate", BindingFlags.NonPublic | BindingFlags.Static));

            var args = new object[ps.Length];
            args[0] = message;
            args[1] = "OK";
            args[2] = noop;
            for (int i = 3; i < ps.Length; i++)
                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : Type.Missing;

            openM.Invoke(dialog, args);
        }
        catch (Exception e) { _logger.LogWarning($"[JOIN] Showing the message failed: {e.Message}"); }
    }

    // A reliable inner message was fully reassembled by ReliableNet → count it (NET-DIAG) then dispatch normally.
    private void DispatchReliableInner(byte[] inner)
    {
        if (inner == null || inner.Length == 0) return;
        _lastReliableInTime = Time.time;
        if (inner[0] == 0x24) _diagRecvHeartbeat++; else _diagRecvOther++;
        if (Multiplayer.DebugMode && inner[0] != 0x06 && inner[0] != 0x07 && inner[0] != 0x08)
            _logger.LogInfo($"[P2P] Received(rel) {inner.Length} bytes, type={inner[0]}");
        OnPacketReceived(inner);
    }

    private void OnPacketReceived(byte[] data)
    {
        byte type = data[0];

        // Reliable-layer resync control (raw unreliable, see TickDegradedDetector)
        if (type == 0x25) { OnResyncRequest(data.Length >= 2 ? data[1] : (byte)0); return; }
        if (type == 0x26) { OnResyncConfirm(data.Length >= 2 ? data[1] : (byte)0); return; }

        // The host save is loaded exactly once on connect. If the client is already in game, ignore a
        // repeated transfer (0x02-0x04) — else LoadSaveFromHost reloads the scene mid-game and game timers
        // (GJTimer/FlowCanvas) fire into destroyed objects → FATAL NRE.
        if ((type == 0x02 || type == 0x03 || type == 0x04) && SteamNetwork.IsInGame)
        {
            if (type == 0x02)
                _logger.LogInfo("[P2P] Repeated save transfer ignored — client already in game ✓");
            return;
        }

        if (type == 0x01)
        {
            if (SteamNetwork.Role == NetworkRole.Host)
            {
                // Protocol version check BEFORE the save. Legacy 0x01 without the field (1 byte) → version 0 →
                // also a mismatch (an old client is honestly filtered out). Match → send the save as before.
                int clientVer = data.Length >= 5 ? System.BitConverter.ToInt32(data, 1) : 0;
                if (clientVer != SteamNetwork.PROTOCOL_VERSION)
                {
                    var rej = new byte[5];
                    rej[0] = 0x22;
                    System.Buffer.BlockCopy(System.BitConverter.GetBytes(SteamNetwork.PROTOCOL_VERSION), 0, rej, 1, 4);
                    SendPacket(SteamNetwork.RemoteID, rej);
                    _logger.LogError($"[VERSION] Client protocol v{clientVer} ≠ host v{SteamNetwork.PROTOCOL_VERSION} — save NOT sent. Both need the same mod version.");
                    return;
                }
                // The host MUST be IN THE WORLD before sending the save. If the lobby is opened from the menu/title —
                // we reject (0x23): otherwise the client loads without a position into the intro state and its
                // tutorial pairs (lock_tp=1 etc.) over 0x1C overwrite the host world.
                if (!SteamNetwork.HostInWorld())
                {
                    SendPacket(SteamNetwork.RemoteID, new byte[] { 0x23 });
                    _logger.LogError("[STEAM] Client connected, but the host is NOT in the world yet — save NOT sent (0x23). Enter your world, then invite again.");
                    return;
                }
                // Realign the reliable streams at the session (re)start — both sides reset on the 0x01 handshake
                // (client resets right before sending it), so seq spaces agree for all gameplay that follows.
                ReliableNet.Reset();
                _relEpoch = 0; _resyncPending = false; _resyncAttempts = 0;   // fresh handshake supersedes any in-flight resync
                StartCoroutine(SendSaveToClient());
            }
        }
        else if (type == 0x22)
        {
            // The host refused due to a version mismatch — do NOT load the save (otherwise a silent desync).
            int hostVer = data.Length >= 5 ? System.BitConverter.ToInt32(data, 1) : 0;
            SteamNetwork.IsLoadingAsClient = false;
            _logger.LogError($"[VERSION] INCOMPATIBLE: you have protocol v{SteamNetwork.PROTOCOL_VERSION}, the host has v{hostVer}. Update the mod to the SAME version on both. Join cancelled.");
            ShowJoinError("Mod version mismatch between players. Join cancelled.\nInstall the SAME mod version on both, then try again.");
        }
        else if (type == 0x23)
        {
            // The host refused because they haven't entered their world yet (opened the lobby from the menu). Load NOTHING —
            // otherwise we'd end up in the intro state and overwrite the host world. The host will enter the world and
            // invite again.
            SteamNetwork.IsLoadingAsClient = false;
            _logger.LogError("[JOIN] The host is NOT in their world yet — join cancelled. Let the host enter their world first, then invite again.");
            ShowJoinError("The host hasn't entered their world yet. Join cancelled.\nAsk them to enter the game, then join again.\n\n(This is not a mod bug.)");
        }
        else if (type == 0x02)
        {
            _saveBufferExpectedSize = System.BitConverter.ToInt32(data, 1);
            _saveBuffer = new byte[_saveBufferExpectedSize];
            _saveChunksReceived = 0;
            _logger.LogInfo($"[P2P] Expecting save {_saveBufferExpectedSize} bytes");
        }
        else if (type == 0x03)
        {
            int chunkIndex = System.BitConverter.ToInt32(data, 1);
            int offset     = chunkIndex * 500000;
            int size       = data.Length - 5;
            // Defensive: a 0x03 chunk arriving before its 0x02 header (lost/reordered), or one that runs past
            // the announced size, would NRE/throw inside Array.Copy. Drop it instead — the transfer is reliable
            // and ordered, so a missing 0x02 means something is already wrong; don't compound it with a crash.
            if (_saveBuffer == null) { _logger.LogWarning("[P2P] 0x03 chunk before 0x02 header — dropped"); return; }
            if (offset < 0 || size < 0 || offset + size > _saveBuffer.Length)
            {
                _logger.LogWarning($"[P2P] 0x03 chunk {chunkIndex} out of bounds (offset={offset} size={size} buf={_saveBuffer.Length}) — dropped");
                return;
            }
            System.Array.Copy(data, 5, _saveBuffer, offset, size);
            _saveChunksReceived++;
            _logger.LogInfo($"[P2P] Received chunk {chunkIndex} ({size} bytes)");
        }
        else if (type == 0x04)
        {
            _logger.LogInfo($"[P2P] Transfer complete! {_saveChunksReceived} chunks, {_saveBuffer?.Length} bytes");
            if (_saveBuffer != null)
                StartCoroutine(LoadSaveFromHost(_saveBuffer));
        }
        else if (type == 0x05)
        {
            if (data.Length < 13) { _logger.LogWarning($"[SYNC] 0x05 too small: {data.Length}b"); return; }
            float px = System.BitConverter.ToSingle(data, 1);
            float py = System.BitConverter.ToSingle(data, 5);
            float pz = System.BitConverter.ToSingle(data, 9);
            _hostSpawnPosition = new Vector3(px, py, pz);
            _logger.LogInfo($"[CLIENT] Host position received: {_hostSpawnPosition}");
        }
        else if (type == 0x07)
        {
            // ── Instant animation event — without position ─────────────────────────
            // Sent immediately when global_state or direction changes
            // Format: type(1) + angle(4) + state(4) + dirX(4) + dirY(4) = 17 bytes
            if (data.Length < 17)
            {
                _logger.LogWarning($"[ANIM] 0x07 too small: {data.Length}b");
                return;
            }
            // Parse 0x07: type(1) + angle(4) + state(4) + dirX(4) + dirY(4) = 17 bytes
            float a7  = BitConverter.ToSingle(data, 1);   // bytes 1-4
            int   st7 = BitConverter.ToInt32 (data, 5);   // bytes 5-8
            float dx7 = BitConverter.ToSingle(data, 9);   // bytes 9-12
            float dy7 = BitConverter.ToSingle(data, 13);  // bytes 13-16

            // Apply only the animation — don't touch the position
            Multiplayer.Instance?.ApplyRemoteAnimation(a7, st7, dx7, dy7);
        }
        else if (type == 0x06)
        {
            // ── Phase 3: the other player's position ────────────────────────────────────
            // Sent 20 times/sec. Format: pos(xyz) + angle + state + dirX + dirY
            // [+ torch(1) = 27b] — minimum 26 for tolerance to the old format
            if (data.Length < 26)
            {
                _logger.LogWarning($"[SYNC] 0x06 too small: {data.Length}b, skipping");
                return;
            }

            float px    = BitConverter.ToSingle(data, 1);
            float py    = BitConverter.ToSingle(data, 5);
            float pz    = BitConverter.ToSingle(data, 9);
            float angle = BitConverter.ToSingle(data, 13);
            int   state = (int)data[17] - 128;
            float dirX  = BitConverter.ToSingle(data, 18);
            float dirY  = BitConverter.ToSingle(data, 22);

            var remotePos = new Vector3(px, py, pz);

            // Skip zero positions — the host hasn't loaded the player yet
            if (remotePos.sqrMagnitude < 1f)
            {
                _logger.LogInfo("[SYNC] Skipping zero host position");
                return;
            }

            // Trigger spawn on the first valid packet.
            // RemotePlayerSpawned = true is set HERE so we don't start dozens of coroutines.
            // The coroutine resets it to false if the scene isn't ready yet.
            if (!SteamNetwork.RemotePlayerSpawned && Multiplayer.Instance != null)
            {
                SteamNetwork.RemotePlayerSpawned = true;
                _logger.LogInfo($"[SYNC] Spawning clone at {remotePos}");
                Multiplayer.Instance.SpawnRemotePlayer(remotePos);
            }

            // Update the clone's position and animation
            Multiplayer.Instance?.UpdateRemotePlayer(remotePos, angle, state, dirX, dirY);

            // Torch bit (byte 26, appeared when 0x06 grew to 27b) — the clone's light
            if (data.Length >= 27)
                Multiplayer.Instance?.SetRemoteTorch(data[26] == 1);
        }
        else if (type == 0x08)
        {
            // ── Game time synchronization ──────────────────────────────────
            if (data.Length < 9) return;
            int   remoteDay  = BitConverter.ToInt32 (data, 1);
            float remoteTime = BitConverter.ToSingle(data, 5);

            if (!GameTimeSync.TryRead(out int localDay, out float localTime)) return;

            // 🔬 DAY-DESYNC DIAG (2026-06-13): the friend saw a different day name (day%6 diverged). Max-wins
            // converges with normal exchange, so we log EVERY divergence on receive to see if 0x08 fixes it.
            if (remoteDay != localDay)
                _logger.LogWarning($"[TIME-DESYNC] local day={localDay}(dow={localDay % 6},cur={localTime:F2}) " +
                    $"⟷ remote day={remoteDay}(dow={remoteDay % 6},cur={remoteTime:F2}) → " +
                    (remoteDay > localDay ? "adopting remote" : "we're ahead, ignore"));

            // Adopt the time only if the remote is ahead (day takes priority).
            bool remoteAhead = remoteDay > localDay
                            || (remoteDay == localDay && remoteTime > localTime);
            if (remoteAhead)
            {
                GameTimeSync.Apply(remoteDay, remoteTime);
                if (remoteDay != localDay)
                    _logger.LogInfo($"[TIME] Synchronized: day {localDay}→{remoteDay}, time {remoteTime:F2}");
            }
        }
        else if (type == 0x09)
        {
            // ── Tree chopping synchronization ────────────────────────────────────
            if (data.Length < 13) { _logger.LogWarning($"[CHOP] 0x09 too small: {data.Length}b"); return; }
            float tx     = BitConverter.ToSingle(data, 1);
            float ty     = BitConverter.ToSingle(data, 5);
            float amount = BitConverter.ToSingle(data, 9);
            _logger.LogInfo($"[CHOP] Remote hit: tree @({tx:F1},{ty:F1}) amount={amount:F3}");
            ChopSync.ApplyRemoteChop(tx, ty, amount);
        }
        else if (type == 0x0A)
        {
            // ── Object processed on the other machine (tree/stone/grave/garden bed) ─
            if (data.Length < 9) { _logger.LogWarning($"[CHOP] 0x0A too small: {data.Length}b"); return; }
            float tx = BitConverter.ToSingle(data, 1);
            float ty = BitConverter.ToSingle(data, 5);
            _logger.LogInfo($"[CHOP] Remote object state change @({tx:F1},{ty:F1})");
            ChopSync.ApplyRemoteDestroy(tx, ty);
        }
        else if (type == 0x0B)
        {
            // ── Loot from a processed object on the other machine ────────────────────
            if (!ChopSync.ParseDropPacket(data, out float dx, out float dy,
                                          out object items, out object dir))
            {
                _logger.LogWarning($"[CHOP] 0x0B malformed (len={data.Length})");
                return;
            }
            _logger.LogInfo($"[CHOP] Remote loot @({dx:F1},{dy:F1})");
            ChopSync.ApplyRemoteDrop(dx, dy, items, dir);
        }
        else if (type == 0x0D)
        {
            // ── Visual grave stage on the other machine (RedrawPart/ReplaceWithObject) ─
            _logger.LogInfo($"[CHOP] Remote grave stage (0x0D, {data.Length}b)");
            ChopSync.ParseAndApplyGraveOp(data);
        }
        else if (type == 0x0F)
        {
            // ── Carried corpse position (carrier moves → observer mirrors) ──────
            // type(1) uid(8) x(4) y(4)
            if (data.Length < 17) { _logger.LogWarning($"[CHOP] 0x0F too small ({data.Length})"); return; }
            long buid = BitConverter.ToInt64(data, 1);
            float bx = BitConverter.ToSingle(data, 9);
            float by = BitConverter.ToSingle(data, 13);
            ChopSync.ApplyRemoteBodyPos(buid, bx, by);
        }
        else if (type == 0x10)
        {
            // ── Corpse removed (placed in a grave/consumed) → observer cleanly deletes ──
            // type(1) uid(8)
            if (data.Length < 9) { _logger.LogWarning($"[CHOP] 0x10 too small ({data.Length})"); return; }
            long ruid = BitConverter.ToInt64(data, 1);
            _logger.LogInfo($"[CHOP] Corpse uid={ruid} removed by partner (0x10)");
            ChopSync.ApplyRemoteBodyRemove(ruid);
        }
        else if (type == 0x11)
        {
            // ── Spawn a corpse on the observer's grave via DropItem (non-pickable carried) ──
            // type(1) graveUid(8) dir(4) x(4) y(4) z(4) i3(4) b4(1) jsonLen(4) json
            if (data.Length < 34) { _logger.LogWarning($"[CHOP] 0x11 too small ({data.Length})"); return; }
            long guid = BitConverter.ToInt64(data, 1);
            int  cdir = BitConverter.ToInt32(data, 9);
            float cx = BitConverter.ToSingle(data, 13);
            float cy = BitConverter.ToSingle(data, 17);
            float cz = BitConverter.ToSingle(data, 21);
            float cforce = BitConverter.ToSingle(data, 25);
            bool cb4 = data[29] != 0;
            int  cjl = BitConverter.ToInt32(data, 30);
            string cjson = (cjl > 0 && data.Length >= 34 + cjl)
                ? System.Text.Encoding.UTF8.GetString(data, 34, cjl) : "";
            _logger.LogInfo($"[CHOP] Corpse spawn from partner uid={guid} json={cjl}b (0x11)");
            ChopSync.ApplyRemoteCorpseSpawn(guid, cdir, cx, cy, cz, cforce, cb4, cjson);
        }
        else if (type == 0x12)
        {
            // ── Partner carries a heavy item overhead → show it on their clone ──
            // type(1) iconLen(1) icon(utf8)
            if (data.Length < 2) return;
            int il = data[1];
            string icon = (data.Length >= 2 + il) ? System.Text.Encoding.UTF8.GetString(data, 2, il) : "";
            _logger.LogInfo($"[CHOP] Partner carrying overhead icon={icon} (0x12)");
            Multiplayer.Instance?.SetRemoteOverhead(icon);
        }
        else if (type == 0x13)
        {
            // ── Partner placed/dropped → remove the overhead from the clone ──
            _logger.LogInfo("[CHOP] Partner placed overhead (0x13)");
            Multiplayer.Instance?.ClearRemoteOverhead();
        }
        else if (type == 0x14)
        {
            // ── Partner PICKED UP our mirror corpse (ownership transfer) → remove the ground copy ──
            // type(1) uid(8). The corpse returns as a new mirror via 0x11 when the partner drops it.
            if (data.Length < 9) { _logger.LogWarning($"[CHOP] 0x14 too small ({data.Length})"); return; }
            long tuid = BitConverter.ToInt64(data, 1);
            ChopSync.ApplyRemoteCorpseTransfer(tuid);
        }
        else if (type == 0x15)
        {
            // ── SPAWN PRIMITIVE (Phase 2): partner placed a building/construction site → we spawn it
            // with a SHARED uid (so later construction stages sync via 0x0D by that uid) ──
            // type(1) uid(8) x(4) y(4) z(4) objIdLen(2) objId
            if (data.Length < 21) { _logger.LogWarning($"[CHOP] 0x15 too small ({data.Length})"); return; }
            long  buid = BitConverter.ToInt64(data, 1);
            float bx = BitConverter.ToSingle(data, 9);
            float by = BitConverter.ToSingle(data, 13);
            float bz = BitConverter.ToSingle(data, 17);
            int   bidLen = BitConverter.ToUInt16(data, 21);
            if (23 + bidLen > data.Length) { _logger.LogWarning("[CHOP] 0x15 objId out of bounds"); return; }
            string bObjId = System.Text.Encoding.UTF8.GetString(data, 23, bidLen);
            ChopSync.ApplyRemoteBuildSpawn(buid, bx, by, bz, bObjId);
            // Garden construction site: drop the preview-garden_empty from the retry queue (the DoPlace preview
            // races ahead of 0x15 → otherwise the frame instantly "gets dug up"). Finishing the dig syncs later.
            if (bObjId != null && bObjId.Contains("garden") && bObjId.Contains("_place"))
                ChopSync.InvalidatePendingGarden(buid);
        }
        else if (type == 0x16)
        {
            // ── Partner DEMOLISHED a building → we destroy our copy by uid ──
            // type(1) uid(8). Symmetric to the 0x15 spawn.
            if (data.Length < 9) { _logger.LogWarning($"[CHOP] 0x16 too small ({data.Length})"); return; }
            long ruid = BitConverter.ToInt64(data, 1);
            ChopSync.ApplyRemoteBuildRemove(ruid);
        }
        else if (type == 0x17)
        {
            // ── Workbench craft queue (Phase 2 coop-craft Stage 1): partner changed the queue →
            // we rebuild craft_queue + RedrawBubble, to see the same windows over the stations ──
            // type(1) uid(8) count(2) [idLen(1) id n(4) infinite(1) flags(1)]*  (flags bit0 = synthetic)
            if (data.Length < 11) { _logger.LogWarning($"[CHOP] 0x17 too small ({data.Length})"); return; }
            long cuid = BitConverter.ToInt64(data, 1);
            int ccount = BitConverter.ToUInt16(data, 9);
            var items = new List<ChopSync.CraftQ>(ccount);
            int coff = 11;
            bool cok = true;
            for (int i = 0; i < ccount; i++)
            {
                if (coff + 1 > data.Length) { cok = false; break; }
                int idLen = data[coff++];
                if (coff + idLen + 6 > data.Length) { cok = false; break; }
                string id = System.Text.Encoding.UTF8.GetString(data, coff, idLen); coff += idLen;
                int n = BitConverter.ToInt32(data, coff); coff += 4;
                bool inf = data[coff++] != 0;
                byte fl = data[coff++];
                items.Add(new ChopSync.CraftQ { id = id, n = n, infinite = inf, synthetic = (fl & 1) != 0 });
            }
            if (cok) ChopSync.ApplyRemoteCraftQueue(cuid, items);
            else _logger.LogWarning("[CHOP] 0x17 corrupted");
        }
        else if (type == 0x18)
        {
            // ── Station claim/release/progress (Stage 3a/3b, owner arbitration):
            // flag=1 start {uid, craftIdLen, craftId}; flag=0 stop; flag=2 progress {uid, 0-100} ──
            if (data.Length < 11) { _logger.LogWarning($"[CHOP] 0x18 too small ({data.Length})"); return; }
            long auid = BitConverter.ToInt64(data, 1);
            byte aflag = data[9];
            if (aflag == 2)
            {
                ChopSync.ApplyRemoteCraftProgress(auid, data[10] / 100f);
            }
            else
            {
                bool astart = aflag != 0;
                int aidLen = data[10];
                string acraftId = "";
                if (aidLen > 0 && 11 + aidLen <= data.Length)
                    acraftId = System.Text.Encoding.UTF8.GetString(data, 11, aidLen);
                ChopSync.ApplyRemoteCraftClaim(auid, astart, acraftId);
            }
        }
        else if (type == 0x19)
        {
            // ── Chest ops (Phase 3, concurrent sync): partner moved items around →
            // we apply the deltas to our copy ──
            // type(1) uid(8) count(1) [idLen(1) id delta(4) jsonLen(2) json]*
            if (data.Length < 10) { _logger.LogWarning($"[CHOP] 0x19 too small ({data.Length})"); return; }
            long ouid = BitConverter.ToInt64(data, 1);
            int ocount = data[9];
            var oops = new List<ChopSync.ChestOp>(ocount);
            int ooff = 10;
            bool ook = true;
            for (int i = 0; i < ocount; i++)
            {
                if (ooff + 1 > data.Length) { ook = false; break; }
                int idLen = data[ooff++];
                if (ooff + idLen + 6 > data.Length) { ook = false; break; }
                string oid = System.Text.Encoding.UTF8.GetString(data, ooff, idLen); ooff += idLen;
                int odelta = BitConverter.ToInt32(data, ooff); ooff += 4;
                int jsonLen = BitConverter.ToUInt16(data, ooff); ooff += 2;
                string ojson = "";
                if (jsonLen > 0)
                {
                    if (ooff + jsonLen > data.Length) { ook = false; break; }
                    ojson = System.Text.Encoding.UTF8.GetString(data, ooff, jsonLen); ooff += jsonLen;
                }
                oops.Add(new ChopSync.ChestOp { id = oid, delta = odelta, json = ojson });
            }
            if (ook) ChopSync.ApplyRemoteChestOps(ouid, oops);
            else _logger.LogWarning("[CHOP] 0x19 corrupted");
        }
        else if (type == 0x1A)
        {
            // ── Weather from the host (v2): type day(4) count(1) [slotIdx(1)+len(1)+name]* ──
            // Explicit slots: late-join sends only current+future (the game removes past ones from the line).
            if (data.Length < 6) { _logger.LogWarning($"[CHOP] 0x1A too small ({data.Length})"); return; }
            int wday = BitConverter.ToInt32(data, 1);
            int wcount = data[5];
            var wslots = new List<int>(wcount);
            var wnames = new List<string>(wcount);
            int woff = 6;
            bool wok = true;
            for (int i = 0; i < wcount; i++)
            {
                if (woff + 2 > data.Length) { wok = false; break; }
                int sidx = data[woff++];
                int nlen = data[woff++];
                if (woff + nlen > data.Length) { wok = false; break; }
                wslots.Add(sidx);
                wnames.Add(System.Text.Encoding.UTF8.GetString(data, woff, nlen)); woff += nlen;
            }
            if (wok) ChopSync.ApplyRemoteWeather(wday, wslots, wnames);
            else _logger.LogWarning("[CHOP] 0x1A corrupted");
        }
        else if (type == 0x1B)
        {
            // ── Pickup of an untracked BIG drop: type x(4) y(4) idLen(1) id ──
            // Partner picked up their save-copy of a boulder/log → we remove ours by id+position.
            if (data.Length < 11) { _logger.LogWarning($"[CHOP] 0x1B too small ({data.Length})"); return; }
            float bx = BitConverter.ToSingle(data, 1);
            float by = BitConverter.ToSingle(data, 5);
            int bidLen = data[9];
            if (10 + bidLen > data.Length) { _logger.LogWarning("[CHOP] 0x1B corrupted"); return; }
            string bid = System.Text.Encoding.UTF8.GetString(data, 10, bidLen);
            ChopSync.ApplyRemoteBigPickup(bid, bx, by);
        }
        else if (type == 0x1C)
        {
            // ── Story: partner's story param: type nameLen(1) name value(4) ──
            if (data.Length < 7) { _logger.LogWarning($"[STORY] 0x1C too small ({data.Length})"); return; }
            int snLen = data[1];
            if (2 + snLen + 4 > data.Length) { _logger.LogWarning("[STORY] 0x1C corrupted"); return; }
            string sname = System.Text.Encoding.UTF8.GetString(data, 2, snLen);
            float sval = BitConverter.ToSingle(data, 2 + snLen);
            StorySync.ApplyRemoteParam(sname, sval);
        }
        else if (type == 0x1D)
        {
            // ── Story: partner's quest-key action: type keyLen(1) key ──
            if (data.Length < 3) { _logger.LogWarning($"[STORY] 0x1D too small ({data.Length})"); return; }
            int skLen = data[1];
            if (2 + skLen > data.Length) { _logger.LogWarning("[STORY] 0x1D corrupted"); return; }
            string skey = System.Text.Encoding.UTF8.GetString(data, 2, skLen);
            StorySync.ApplyRemoteKey(skey);
        }
        else if (type == 0x1E)
        {
            // ── Story: quest lifecycle: type kind(1: 0=start,1=success,2=fail) idLen(1) id ──
            if (data.Length < 4) { _logger.LogWarning($"[STORY] 0x1E too small ({data.Length})"); return; }
            int qkind = data[1];
            int qidLen = data[2];
            if (3 + qidLen > data.Length) { _logger.LogWarning("[STORY] 0x1E corrupted"); return; }
            string qid = System.Text.Encoding.UTF8.GetString(data, 3, qidLen);
            StorySync.ApplyRemoteQuestEvent(qkind, qid);
        }
        else if (type == 0x1F)
        {
            // ── Story: journal NPC task: type state(1) npcLen(1) npc taskLen(1) task ──
            if (data.Length < 5) { _logger.LogWarning($"[STORY] 0x1F too small ({data.Length})"); return; }
            int nstate = data[1];
            int nnLen = data[2];
            if (3 + nnLen + 1 > data.Length) { _logger.LogWarning("[STORY] 0x1F corrupted"); return; }
            string nnpc = System.Text.Encoding.UTF8.GetString(data, 3, nnLen);
            int ntLen = data[3 + nnLen];
            if (4 + nnLen + ntLen > data.Length) { _logger.LogWarning("[STORY] 0x1F corrupted"); return; }
            string ntask = System.Text.Encoding.UTF8.GetString(data, 4 + nnLen, ntLen);
            StorySync.ApplyRemoteNpcTask(nnpc, ntask, nstate);
        }
        else if (type == 0x20)
        {
            // ── Cosmetic NPC position sync: type count(2) + N×{uid(8) x(4) y(4)} ──
            ChopSync.ApplyRemoteNpcPositions(data);
        }
        else if (type == 0x21)
        {
            // ── Garden/orchard state sync: uid(8) x(4) y(4) objIdLen(2) objId json ──
            ChopSync.ApplyRemoteGardenState(data);
        }
        else if (type == 0x24)
        {
            // DIAG (patch A): heartbeat from the partner. Log seq + gap so reliable loss is visible per direction.
            int seq = data.Length >= 5 ? System.BitConverter.ToInt32(data, 1) : -1;
            int gap = (_heartbeatSeqIn >= 0 && seq > _heartbeatSeqIn) ? seq - _heartbeatSeqIn - 1 : 0;
            if (gap > 0) _logger.LogWarning($"[HEARTBEAT] recv seq={seq} — LOST {gap} reliable heartbeat(s) since {_heartbeatSeqIn}");
            else if (Multiplayer.DebugMode) _logger.LogInfo($"[HEARTBEAT] recv seq={seq} ✓");
            _heartbeatSeqIn = seq;
        }
    }


    private IEnumerator SendSaveToClient()
    {
        yield return null;

        byte[] binary = null;
        try
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Static | BindingFlags.Instance;
            var mainGameType = System.AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } })
                .FirstOrDefault(t => t.Name == "MainGame");

            var mainGameInstance = mainGameType.GetField("me", flags)?.GetValue(null);
            var saveInstance     = mainGameType.GetField("save",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(mainGameInstance);

            binary = (byte[])saveInstance.GetType()
                .GetMethod("ToBinary", flags)?.Invoke(saveInstance, null);

            _logger.LogInfo($"[P2P] Save size: {binary.Length} bytes");

            try
            {
                var playerGO = Object.FindObjectsOfType<MonoBehaviour>()
                    .FirstOrDefault(mb => mb.GetType().Name == "PlayerComponent")?.gameObject;
                if (playerGO != null)
                {
                    var pos = playerGO.transform.position;
                    var posPacket = new byte[13];
                    posPacket[0] = 0x05;
                    System.BitConverter.GetBytes(pos.x).CopyTo(posPacket, 1);
                    System.BitConverter.GetBytes(pos.y).CopyTo(posPacket, 5);
                    System.BitConverter.GetBytes(pos.z).CopyTo(posPacket, 9);
                    SendPacket(SteamNetwork.RemoteID, posPacket);
                    _logger.LogInfo($"[P2P] Host position sent to client: {pos}");
                }
            }
            catch (System.Exception e) { _logger.LogWarning($"[P2P] Failed to send position: {e.Message}"); }

            var sizePacket = new byte[5];
            sizePacket[0] = 0x02;
            System.BitConverter.GetBytes(binary.Length).CopyTo(sizePacket, 1);
            SendPacket(SteamNetwork.RemoteID, sizePacket);
            _logger.LogInfo("[P2P] Size packet sent");
        }
        catch (System.Exception e) { _logger.LogError($"[P2P] SendSave error: {e.Message}"); yield break; }

        yield return new WaitForSeconds(0.1f);

        const int chunkSize   = 500000;
        int       totalChunks = (binary.Length + chunkSize - 1) / chunkSize;

        for (int i = 0; i < totalChunks; i++)
        {
            int offset = i * chunkSize;
            int size   = System.Math.Min(chunkSize, binary.Length - offset);

            var chunk = new byte[5 + size];
            chunk[0] = 0x03;
            System.BitConverter.GetBytes(i).CopyTo(chunk, 1);
            System.Array.Copy(binary, offset, chunk, 5, size);

            SendPacket(SteamNetwork.RemoteID, chunk);
            _logger.LogInfo($"[P2P] Chunk {i+1}/{totalChunks} sent ({size} bytes)");

            yield return new WaitForSeconds(0.05f);
        }

        SendPacket(SteamNetwork.RemoteID, new byte[] { 0x04 });
        _logger.LogInfo("[P2P] All chunks sent ✓");
    }

    // ── Phase 3: send our position to the other player ────────────────────────
    // Packet 0x06: type(1) + x(4) + y(4) + z(4) + angle(4) + state(1) + dirX(4) + dirY(4)
    //             + torch(1) = 27 bytes (torch = light_go.activeSelf, torch-light sync)
    // The buffer is allocated once — no allocations every 50ms
    private readonly byte[] _posPacket  = new byte[27];
    private GameObject _cachedLocalLightGo; // local player's light_go (reflection, 2s cache)
    private readonly byte[] _animPacket = new byte[17]; // 0x07(1) + angle(4) + state(4) + dirX(4) + dirY(4) = 17 bytes
    private float _localPlayerRefreshTimer = 0f;
    private const float LOCAL_PLAYER_REFRESH_INTERVAL = 2f;

    // Previous animation values — we send 0x07 only on change
    private float _lastAngle = float.MinValue;
    private int   _lastState = int.MinValue;
    private float _lastDirX  = float.MinValue;
    private float _lastDirY  = float.MinValue;

    private void SendMyPosition()
    {
        try
        {
            // Refresh the local player cache once every 2 seconds
            // (instead of FindObjectsOfType every 50ms)
            _localPlayerRefreshTimer -= SEND_INTERVAL;
            if (_localPlayerRefreshTimer <= 0f || _cachedLocalPlayer == null ||
                !_cachedLocalPlayer.activeInHierarchy)
            {
                _localPlayerRefreshTimer = LOCAL_PLAYER_REFRESH_INTERVAL;
                var mb = Object.FindObjectsOfType<MonoBehaviour>()
                    .FirstOrDefault(m => m.GetType().Name == "PlayerComponent"
                                         && m.gameObject.name != "RemotePlayer_Clone"
                                         && m.gameObject.name != "Player2_Clone");
                if (mb == null) return;
                _cachedLocalPlayer  = mb.gameObject;
                _cachedLocalAnimator = _cachedLocalPlayer.GetComponentInChildren<Animator>();
                _cachedLocalLightGo  = mb.GetType().GetField("light_go")?.GetValue(mb) as GameObject;
            }
            if (_cachedLocalPlayer == null) return;

            float angle = _cachedLocalAnimator?.GetFloat("direction_angle") ?? 0f;
            float dirX  = _cachedLocalAnimator?.GetFloat("direction_x")    ?? 0f;
            float dirY  = _cachedLocalAnimator?.GetFloat("direction_y")    ?? 0f;
            int   state = _cachedLocalAnimator?.GetInteger("global_state") ?? 0;
            var   pos   = _cachedLocalPlayer.transform.position;

            // ── 0x07: animation event — sent IMMEDIATELY on any change ──
            // We don't wait for the 50ms timer — otherwise fast animations (chopping, digging)
            // don't reach the other player in time
            bool animChanged = state != _lastState
                            || Mathf.Abs(angle - _lastAngle) > 0.5f
                            || Mathf.Abs(dirX  - _lastDirX)  > 0.01f
                            || Mathf.Abs(dirY  - _lastDirY)  > 0.01f;

            if (animChanged)
            {
                _lastAngle = angle;
                _lastState = state;
                _lastDirX  = dirX;
                _lastDirY  = dirY;

                // Packet 0x07: type(1) + angle(4) + state(4) + dirX(4) + dirY(4) = 17 bytes
                // Without position — only the animation state, sent instantly
                _animPacket[0] = 0x07;
                BitConverter.GetBytes(angle).CopyTo(_animPacket, 1);  // bytes 1-4
                BitConverter.GetBytes(state).CopyTo(_animPacket, 5);  // bytes 5-8
                BitConverter.GetBytes(dirX).CopyTo(_animPacket, 9);   // bytes 9-12
                BitConverter.GetBytes(dirY).CopyTo(_animPacket, 13);  // bytes 13-16
                SendPacket(SteamNetwork.RemoteID, _animPacket);
            }

            // ── 0x06: position — sent on the timer 20 times/sec ────────
            _posPacket[0] = 0x06;
            BitConverter.GetBytes(pos.x).CopyTo(_posPacket, 1);
            BitConverter.GetBytes(pos.y).CopyTo(_posPacket, 5);
            BitConverter.GetBytes(pos.z).CopyTo(_posPacket, 9);
            BitConverter.GetBytes(angle).CopyTo(_posPacket, 13);
            _posPacket[17] = (byte)(state + 128);
            BitConverter.GetBytes(dirX).CopyTo(_posPacket, 18);
            BitConverter.GetBytes(dirY).CopyTo(_posPacket, 22);
            _posPacket[26] = (byte)(_cachedLocalLightGo != null && _cachedLocalLightGo.activeSelf ? 1 : 0);

            SendPacket(SteamNetwork.RemoteID, _posPacket);
        }
        catch (Exception e)
        {
            _logger.LogWarning($"[SYNC] SendMyPosition: {e.Message}");
        }
    }

    private IEnumerator ClientTutorialWatcher()
    {
        if (SteamNetwork.Role != NetworkRole.Client) yield break;
        _logger.LogInfo("[CLIENT] ClientTutorialWatcher started");

        float timer = 30f;
        while (timer > 0f)
        {
            yield return new WaitForSeconds(1f);
            timer -= 1f;
            ForceSkipTutorialParams();
        }

        _logger.LogInfo("[CLIENT] ClientTutorialWatcher finished");
    }

    public static void ForceSkipTutorialParams(ManualLogSource logger = null)
    {
        try
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic
                                            | BindingFlags.Instance | BindingFlags.Static;

            var mainGameType = AccessTools.TypeByName("MainGame");
            var me = mainGameType?.GetField("me", flags)?.GetValue(null);
            if (me == null) return;

            var save = mainGameType
                .GetField("save", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(me);
            if (save == null) return;

            var inv = save.GetType()
                .GetField("_inventory", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(save);
            if (inv == null) return;

            var setParam = inv.GetType().GetMethod("SetParam", new[] { typeof(string), typeof(float) });
            if (setParam == null) return;

            var tutFlags = new[]
            {
                "in_tutorial", "gone_out_from_house",
                "tut_shown_tut_pickup", "tut_shown_tut_1",
                "tut_shown_tut_hud_right", "tut_shown_tut_tech",
                "tut_shown_tut_energy", "tut_shown_tut_build",
                "tut_shown_tut_npc_list", "tut_shown_tut_grave",
            };
            foreach (var flag in tutFlags)
                setParam.Invoke(inv, new object[] { flag, flag == "in_tutorial" ? 0f : 1f });
            setParam.Invoke(inv, new object[] { "craft_tut", 10f });

            setParam.Invoke(inv, new object[] { "first_day",         1f });
            setParam.Invoke(inv, new object[] { "bury_gerry",        1f });
            setParam.Invoke(inv, new object[] { "morgue_quest",      1f });
            setParam.Invoke(inv, new object[] { "cant_leave_house",  0f });
            setParam.Invoke(inv, new object[] { "house_exit_locked", 0f });
            setParam.Invoke(inv, new object[] { "intro_done",        1f });
            setParam.Invoke(inv, new object[] { "first_day_done",    1f });

            logger?.LogInfo("[TUT] ForceSkip: in_tutorial=0 set ✓");
        }
        catch (Exception e) { logger?.LogWarning($"[TUT] ForceSkip exception: {e.Message}"); }
    }

    public static bool _unlockAlreadyDone = false;

    private IEnumerator UnlockPlayerAfterLoad()
    {
        // Guard against double-launch — the logs show the coroutine
        // starts twice due to two StartPlayingGame calls
        if (_unlockAlreadyDone)
        {
            _logger.LogInfo("[UNLOCK] Already done — skipping duplicate");
            yield break;
        }
        _unlockAlreadyDone = true;

        // Variant A: apply the character layer in a SEPARATE coroutine that starts immediately and applies
        // the overlay as soon as the inventory is ready+stable — without waiting for the pauses below (cosmetic UX fix
        // for latency). The late call at the end of this method is a fallback (OverlayDone guard).
        StartCoroutine(OverlayClientCharacterASAP());

        yield return new WaitForSeconds(5f);

        var iFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var sFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        for (int attempt = 0; attempt < 20; attempt++)
        {
            yield return new WaitForSeconds(1f);
            try
            {
                var mainGameType = AccessTools.TypeByName("MainGame");
                var me = mainGameType?.GetField("me", sFlags)?.GetValue(null);
                if (me == null) { _logger.LogInfo("[UNLOCK] me=null, waiting..."); continue; }

                var save = mainGameType.GetField("save", iFlags)?.GetValue(me);
                if (save == null) { _logger.LogInfo("[UNLOCK] save=null, waiting..."); continue; }

                var saveType = save.GetType();

                var playerGO = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()
                    .FirstOrDefault(mb => mb.GetType().Name == "PlayerComponent")?.gameObject;
                if (playerGO != null)
                {
                    saveType.GetField("player_position", iFlags)
                        ?.SetValue(save, playerGO.transform.position);
                    _logger.LogInfo($"[UNLOCK] player_position: {playerGO.transform.position}");
                }

                var inv = saveType.GetField("_inventory", iFlags)?.GetValue(save);
                var setParam = inv?.GetType().GetMethod("SetParam", new[] { typeof(string), typeof(float) });
                var getParam = inv?.GetType().GetMethod("GetParam", new[] { typeof(string), typeof(float) });

                if (setParam != null)
                {
                    setParam.Invoke(inv, new object[] { "in_tutorial",           0f });
                    setParam.Invoke(inv, new object[] { "gone_out_from_house",   1f });
                    setParam.Invoke(inv, new object[] { "tut_shown_tut_pickup",  1f });
                    setParam.Invoke(inv, new object[] { "tut_shown_tut_1",       1f });
                    setParam.Invoke(inv, new object[] { "tut_shown_tut_hud_right", 1f });
                    setParam.Invoke(inv, new object[] { "tut_shown_tut_tech",    1f });
                    setParam.Invoke(inv, new object[] { "tut_shown_tut_energy",  1f });
                    setParam.Invoke(inv, new object[] { "tut_shown_tut_build",   1f });
                    setParam.Invoke(inv, new object[] { "tut_shown_tut_npc_list",1f });
                    setParam.Invoke(inv, new object[] { "tut_shown_tut_grave",   1f });
                    setParam.Invoke(inv, new object[] { "craft_tut",             10f });
                    setParam.Invoke(inv, new object[] { "first_day",             1f });
                    setParam.Invoke(inv, new object[] { "bury_gerry",            1f });
                    setParam.Invoke(inv, new object[] { "morgue_quest",          1f });
                    setParam.Invoke(inv, new object[] { "cant_leave_house",      0f });
                    setParam.Invoke(inv, new object[] { "house_exit_locked",     0f });
                    setParam.Invoke(inv, new object[] { "intro_done",            1f });
                    setParam.Invoke(inv, new object[] { "first_day_done",        1f });

                    var inTut   = getParam?.Invoke(inv, new object[] { "in_tutorial", 0f });
                    var goneOut = getParam?.Invoke(inv, new object[] { "gone_out_from_house", 0f });
                    _logger.LogInfo($"[UNLOCK] Check: in_tutorial={inTut}, gone_out={goneOut}");
                }
                else
                {
                    _logger.LogWarning("[UNLOCK] SetParam not found!");
                    continue;
                }

                foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (mb.GetType().Name == "PlayerComponent")
                    {
                        Traverse.Create(mb).Field("_control_enabled").SetValue(true);
                        _logger.LogInfo("[UNLOCK] PlayerComponent unlocked ✓");

                        if (_hostSpawnPosition != Vector3.zero)
                        {
                            mb.transform.position = _hostSpawnPosition;
                            _logger.LogInfo($"[UNLOCK] Teleport to host position: {_hostSpawnPosition} ✓");
                        }
                        break;
                    }
                }

                _logger.LogInfo("[UNLOCK] ✓ Completed successfully!");
                SteamNetwork.IsLoadingAsClient = false;

                // ── Phase 3: the client starts sending its position ────────────
                SteamNetwork.IsInGame = true;
                SteamNetwork.IsInGameSince = UnityEngine.Time.time;
                _logger.LogInfo("[UNLOCK] IsInGame=true — position sync activated ✓");

                // The host save is written during sleep (Inside) → weather states arrive
                // DISABLED; the client spawns outside without the Inside→RealTime transition
                // that would enable them → we force it (otherwise the friend doesn't see rain/fog).
                ChopSync.ForceWeatherStatesEnabledOutside();

                // Unlock movement — but only once the host sees the client (two-way link). A separate
                // coroutine, because there's a try/catch here (yield return is forbidden inside try). The teleport to the host already
                // happened above, so the client stands at the host's spot until release — without jitter.
                StartCoroutine(ReleaseClientLockWhenSeen());

                // M1: periodic autosave of the client's character while it's in game. The exit hooks (menu /
                // OnApplicationQuit) are NOT reliable on their own: a full quit/Alt+F4/crash may not let us
                // save. Autosave = losing at most ~a minute, like the game's own autosave.
                StartCoroutine(AutosaveClientCharacter());

                // M1 FALLBACK: if the ASAP coroutine (started at the top) hasn't applied the character layer (inventory
                // didn't stabilize in time), we apply it here at the end. The OverlayDone guard won't let it
                // double up if the early call already fired.
                ClientCharacterStore.Overlay();

                yield break;
            }
            catch (Exception e)
            {
                _logger.LogWarning($"[UNLOCK] attempt {attempt}: {e.Message}");
            }
        }

        // Fail-open: all attempts failed — do NOT leave the client locked (better without a teleport
        // than frozen forever).
        SteamNetwork.ClientMovementLocked = false;
        _logger.LogError("[UNLOCK] Timeout! Movement unlocked forcibly (fail-open).");
    }

    // We keep the client locked until we confirm the host sees it. There's no direct ACK, so the
    // proxy = RemotePlayerSpawned (we received the host's 0x06 → the connection is two-way, the host is in game and
    // receives our position too). Fail-open 10s, so we don't hang locked if the host's packets don't come.
    private IEnumerator ReleaseClientLockWhenSeen()
    {
        float waited = 0f;
        while (!SteamNetwork.RemotePlayerSpawned && waited < 10f)
        {
            waited += 0.25f;
            yield return new WaitForSeconds(0.25f);
        }
        SteamNetwork.ClientMovementLocked = false;
        _logger.LogInfo($"[UNLOCK] Client movement unlocked (host sees={SteamNetwork.RemotePlayerSpawned}, waited {waited:F1}s) ✓");
    }

    // Variant A: apply the client's character layer AS SOON AS player.data appears AND stabilizes — without
    // waiting for UnlockPlayerAfterLoad's cosmetic pauses (which made the player see the host's inventory for
    // seconds). Stability = ToJSON(0) length unchanged for 2 ticks (~0.4s): while the game fills the inventory
    // the length grows, so applying early would overwrite. UnlockPlayerAfterLoad's late call is a fallback.
    private IEnumerator OverlayClientCharacterASAP()
    {
        if (SteamNetwork.Role != NetworkRole.Client) yield break;

        float start = Time.time;
        float firstReady = -1f;
        string lastJson = null;
        int stableHits = 0;

        for (float waited = 0f; waited < 15f; waited += 0.2f)
        {
            yield return new WaitForSeconds(0.2f);
            if (ClientCharacterStore.OverlayDone) yield break;

            string json = ClientCharacterStore.LivePlayerJson();
            if (json == null) continue;

            if (firstReady < 0f)
            {
                firstReady = Time.time - start;
                _logger.LogInfo($"[CHAR] player.data ready after {firstReady:F1}s (json={json.Length}b) — waiting for stabilization…");
            }

            stableHits = (json == lastJson) ? stableHits + 1 : 0;
            lastJson = json;

            if (stableHits >= 2)
            {
                _logger.LogInfo($"[CHAR] Inventory stable after {Time.time - start:F1}s — applying overlay early");
                ClientCharacterStore.Overlay();
                // The hotbar/HUD may still redraw with the host's counters after our overlay
                // (the GUI initializes ~at the same time). A few repeated redraws finish off the visible state.
                for (int i = 0; i < 3; i++)
                {
                    yield return new WaitForSeconds(0.5f);
                    ClientCharacterStore.RefreshHud();
                }
                yield break;
            }
        }
        _logger.LogWarning("[CHAR] ASAP-overlay: inventory didn't stabilize within 15s — leaving it to the late fallback");
    }

    private bool _charAutosaveRunning;

    // Periodic autosave of the client's character — independent of exit hooks (a crash loses at most one
    // interval). Save() is gated by Role==Client && IsInGame. Loop lives while IsInGame; re-join starts a new
    // one (guard against doubling).
    private IEnumerator AutosaveClientCharacter()
    {
        if (_charAutosaveRunning) yield break;
        _charAutosaveRunning = true;
        try
        {
            const float intervalSec = 60f;
            while (SteamNetwork.IsInGame && SteamNetwork.Role == NetworkRole.Client)
            {
                yield return new WaitForSeconds(intervalSec);
                if (!SteamNetwork.IsInGame || SteamNetwork.Role != NetworkRole.Client) break;
                ClientCharacterStore.Save();
            }
        }
        finally { _charAutosaveRunning = false; }
    }

    private IEnumerator HideMenuUntilInGame()
    {
        float timeout = 60f;
        while (timeout > 0f)
        {
            yield return new WaitForSeconds(0.3f);
            timeout -= 0.3f;

            foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                var t = mb.GetType().Name;

                if (t == "MainMenuGUI" && mb.gameObject.activeSelf)
                {
                    mb.gameObject.SetActive(false);
                    _logger.LogInfo("[CLIENT] MainMenuGUI hidden ✓");
                }

                if (t == "TitleScreen" && mb.gameObject.activeSelf)
                {
                    mb.gameObject.SetActive(false);
                    _logger.LogInfo("[CLIENT] TitleScreen hidden ✓");
                }
            }

            var player = Object.FindObjectsOfType<MonoBehaviour>()
                .FirstOrDefault(mb => mb.GetType().Name == "PlayerComponent"
                                      && mb.gameObject.activeInHierarchy);
            if (player != null)
            {
                _logger.LogInfo("[CLIENT] ✓ Player in game, menu hidden permanently");
                yield break;
            }
        }
        _logger.LogError("[CLIENT] HideMenuUntilInGame: timeout!");
    }

    private IEnumerator LoadSaveFromHost(byte[] binary)
    {
        if (_loadingInProgress) yield break;
        _loadingInProgress = true;
        SteamNetwork.IsLoadingAsClient = true;

        _logger.LogInfo($"[CLIENT] Loading host save ({binary.Length} bytes)");

        // Coop save → SEPARATE slot "999999" the game never creates itself (slots 0..1000, decomp 102766) →
        // user slots UNTOUCHED. The game loads {filename_no_extension}.dat from persistentDataPath (decomp
        // 102674/102752), so we write there and set the same filename_no_extension below. Removed on quit.
        var savePath = System.IO.Path.Combine(Application.persistentDataPath, SteamNetwork.CoopSaveSlot + ".dat");
        System.IO.File.WriteAllBytes(savePath, binary);
        _coopSaveWritten = true;
        _logger.LogInfo($"[CLIENT] Coop save into separate slot {SteamNetwork.CoopSaveSlot}: {savePath} ✓");

        yield return new WaitForSeconds(0.3f);

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var gameSaveType = AccessTools.TypeByName("GameSave");
        var newSave = gameSaveType
            ?.GetMethod("FromBinary", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?.Invoke(null, new object[] { binary });
        if (newSave == null) { _logger.LogError("[CLIENT] FromBinary null!"); yield break; }
        _logger.LogInfo("[CLIENT] FromBinary ✓");

        try
        {
            var flags3 = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var invField = newSave?.GetType().GetField("_inventory", flags3);
            var inv = invField?.GetValue(newSave);
            var setParamInv = inv?.GetType()
                .GetMethod("SetParam", new[] { typeof(string), typeof(float) });

            if (setParamInv != null)
            {
                setParamInv.Invoke(inv, new object[] { "in_tutorial",           0f });
                setParamInv.Invoke(inv, new object[] { "tut_shown_tut_pickup",  1f });
                setParamInv.Invoke(inv, new object[] { "tut_shown_tut_1",       1f });
                setParamInv.Invoke(inv, new object[] { "tut_shown_tut_hud_right", 1f });
                setParamInv.Invoke(inv, new object[] { "tut_shown_tut_tech",    1f });
                setParamInv.Invoke(inv, new object[] { "tut_shown_tut_energy",  1f });
                setParamInv.Invoke(inv, new object[] { "tut_shown_tut_build",   1f });
                setParamInv.Invoke(inv, new object[] { "tut_shown_tut_npc_list",1f });
                setParamInv.Invoke(inv, new object[] { "tut_shown_tut_grave",   1f });
                setParamInv.Invoke(inv, new object[] { "craft_tut",             10f });
                setParamInv.Invoke(inv, new object[] { "gone_out_from_house",   1f });
                setParamInv.Invoke(inv, new object[] { "first_day",             1f });
                setParamInv.Invoke(inv, new object[] { "bury_gerry",            1f });
                setParamInv.Invoke(inv, new object[] { "morgue_quest",          1f });
                setParamInv.Invoke(inv, new object[] { "cant_leave_house",      0f });
                setParamInv.Invoke(inv, new object[] { "house_exit_locked",     0f });
                setParamInv.Invoke(inv, new object[] { "intro_done",            1f });
                setParamInv.Invoke(inv, new object[] { "first_day_done",        1f });
                _logger.LogInfo("[CLIENT] Tutorial skipped in the save ✓");
            }

            if (_hostSpawnPosition != Vector3.zero)
            {
                var posField = newSave.GetType().GetField("player_position",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (posField != null)
                {
                    posField.SetValue(newSave, _hostSpawnPosition);
                    _logger.LogInfo($"[CLIENT] player_position -> {_hostSpawnPosition} ✓");
                }
                else
                {
                    _logger.LogWarning("[CLIENT] player_position field not found in GameSave!");
                }
            }
            else
            {
                _logger.LogWarning("[CLIENT] _hostSpawnPosition not received from the host!");
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning($"[CLIENT] Tutorial skip: {e.Message}");
        }

        // Do NOT re-serialize the save to a file: a FromBinary → ToBinary round-trip lost DLC objects (refugee
        // camp bloated ~150KB, RefugeesCampEngine.Init() NRE → endless loading). The 999999.dat slot keeps the
        // host's raw bytes (written above). The edits above stay on newSave (linked_save fallback); post-load
        // logic (ClientTutorialWatcher, UnlockPlayerAfterLoad) catches up.

        yield return null;
        yield return null;

        var saveSlotDataType = AccessTools.TypeByName("SaveSlotData");
        var slotData = System.Activator.CreateInstance(saveSlotDataType);
        saveSlotDataType.GetField("filename_no_extension", flags)?.SetValue(slotData, SteamNetwork.CoopSaveSlot);
        saveSlotDataType.GetField("linked_save", flags)?.SetValue(slotData, newSave);

        foreach (var f in saveSlotDataType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            _logger.LogInfo($"[CLIENT] SaveSlotData.{f.Name} = {f.GetValue(slotData)}");

        _logger.LogInfo("[CLIENT] SaveSlotData created ✓");

        var saveSlotsType     = AccessTools.TypeByName("SaveSlotsMenuGUI");
        var allObjects        = Resources.FindObjectsOfTypeAll(saveSlotsType);
        var saveSlotsInstance = allObjects.Length > 0 ? allObjects[0] : null;
        if (saveSlotsInstance == null) { _logger.LogError("[CLIENT] SaveSlotsMenuGUI not found!"); yield break; }
        _logger.LogInfo("[CLIENT] SaveSlotsMenuGUI found ✓");

        var slotDatasField = saveSlotsType.GetField("_slot_datas",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var slotDatasList = slotDatasField?.GetValue(saveSlotsInstance);
        slotDatasList?.GetType().GetMethod("Clear")?.Invoke(slotDatasList, null);
        slotDatasList?.GetType().GetMethod("Add")?.Invoke(slotDatasList, new[] { slotData });
        _logger.LogInfo("[CLIENT] _slot_datas set ✓");

        var onSelectMethod = saveSlotsType.GetMethod("OnSelectSlotPressed",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (onSelectMethod != null)
        {
            onSelectMethod.Invoke(saveSlotsInstance, new object[] { slotData });
            _logger.LogInfo("[CLIENT] OnSelectSlotPressed called ✓");
        }

        var saveSlotsGO = (saveSlotsInstance as UnityEngine.Component)?.gameObject;
        if (saveSlotsGO != null) saveSlotsGO.SetActive(true);

        yield return null;

        saveSlotsType.GetMethod("PrepareScene",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.Invoke(saveSlotsInstance, null);
        _logger.LogInfo("[CLIENT] PrepareScene called ✓");

        yield return new WaitForSeconds(0.3f);

        saveSlotsType.GetMethod("StartPlayingGame",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, System.Type.EmptyTypes, null)
            ?.Invoke(saveSlotsInstance, null);
        _logger.LogInfo("[CLIENT] StartPlayingGame called ✓");

        StartCoroutine(ClientTutorialWatcher());

        yield return new WaitForSeconds(1f);

        StartCoroutine(HideMenuUntilInGame());
        StartCoroutine(UnlockPlayerAfterLoad());
        _loadingInProgress = false;
    }

    private object _lobbyCreatedCallback;

    private void OnLobbyCreatedRaw(object param)
    {
        try
        {
            var flags   = BindingFlags.Public | BindingFlags.Instance;
            var eResult = param.GetType().GetField("m_eResult", flags)?.GetValue(param);
            var lobbyId = param.GetType().GetField("m_ulSteamIDLobby", flags)?.GetValue(param);

            _logger.LogInfo($"[STEAM] OnLobbyCreated! result={eResult} id={lobbyId}");

            if (eResult?.ToString() == "k_EResultOK")
            {
                SteamNetwork.LobbyID = System.Convert.ToUInt64(lobbyId ?? 0UL);
                SteamNetwork.Role    = NetworkRole.Host;
                _logger.LogInfo($"[STEAM] ✓ You are the HOST! LobbyID={SteamNetwork.LobbyID}");

                OpenInviteOverlay(lobbyId);
            }
        }
        catch (System.Exception e) { _logger.LogError($"[STEAM] OnLobbyCreated error: {e.Message}"); }
    }

    private void OpenInviteOverlay(object lobbyId)
    {
        try
        {
            // With the Steam overlay disabled, ActivateGameOverlayInviteDialog is a silent no-op: the lobby
            // exists but the player sees nothing and thinks F11 is broken (Nexus report 2026-07-03). Tell
            // them on screen instead — the friend can still join via the Steam friends list (desktop UI).
            var utils = _steamFriends?.Assembly.GetType("Steamworks.SteamUtils");
            var overlayEnabled = utils?.GetMethod("IsOverlayEnabled",
                    BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, null) as bool?;
            if (overlayEnabled == false)
            {
                _logger.LogWarning("[STEAM] Steam overlay is disabled — invite dialog can't open");
                ShowJoinError("Lobby created, but the Steam Overlay is disabled, so the invite window can't open.\n" +
                              "Your friend can still join: Steam friends list -> right-click your name -> Join Game.\n" +
                              "To get the invite window back, enable the overlay in Steam -> Settings -> In Game.");
                return;
            }

            var steamIdType = _steamMatchmaking?.Assembly.GetType("Steamworks.CSteamID");
            var steamIdObj  = System.Activator.CreateInstance(steamIdType,
                System.Convert.ToUInt64(lobbyId));

            _steamFriends
                ?.GetMethod("ActivateGameOverlayInviteDialog",
                    BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new[] { steamIdObj });

            _logger.LogInfo("[STEAM] Invite overlay opened ✓");
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[STEAM] Overlay error: {e.Message}");
        }
    }

    private void OnLobbyEnteredRaw(object param)
    {
        try
        {
            var flags   = BindingFlags.Public | BindingFlags.Instance;
            var lobbyId = param.GetType().GetField("m_ulSteamIDLobby", flags)?.GetValue(param);

            SteamNetwork.LobbyID = System.Convert.ToUInt64(lobbyId ?? 0UL);

            if (SteamNetwork.Role != NetworkRole.Host)
            {
                SteamNetwork.Role = NetworkRole.Client;
                SteamNetwork.IsClientMode = true;
                _logger.LogInfo($"[STEAM] ✓ You are the CLIENT! LobbyID={SteamNetwork.LobbyID}");

                FetchHostSteamID();
                StartCoroutine(LoadGameAsClient());
            }
        }
        catch (System.Exception e) { _logger.LogError($"[STEAM] OnLobbyEntered error: {e.Message}"); }
    }

    private IEnumerator LoadGameAsClient()
    {
        // Lock the client's movement for the whole join: the game unlocks controls early (on scene load) but
        // the teleport to the host happens later (UnlockPlayerAfterLoad). Reset _unlockAlreadyDone so the unlock
        // runs again on reconnect (else the client stays locked forever).
        SteamNetwork.ClientMovementLocked = true;
        _unlockAlreadyDone = false;

        // RE-JOIN: exiting "to the menu" is a GUI overlay over the still-loaded scene → the active scene
        // doesn't change → activeSceneChanged (which zeroes these guards) doesn't fire. So on re-join the
        // StartPlayingGame guard stayed true and blocked the player spawn (client hung loading). We reset the
        // session guards HERE, at the canonical (re)join point. Safe for the first join too.
        StartPlayingGameGuardPatch.AlreadyStarted = false;
        OnGameStartedPlayingPatch.Reset();
        SteamNetwork.RemotePlayerSpawned = false;
        ClientCharacterStore.OverlayDone = false;
        ChopSync.Reset();

        yield return new WaitForSeconds(1f);

        // Realign the reliable streams BEFORE the handshake — the host resets on receiving this 0x01, so both
        // seq spaces start at 0 for everything that follows (covers re-join without restart too).
        ReliableNet.Reset();
        _relEpoch = 0; _resyncPending = false; _resyncAttempts = 0;   // fresh handshake supersedes any in-flight resync

        // 0x01 carries the protocol version [0x01][int32]: the host checks it and refuses (0x22) on mismatch,
        // otherwise different mod builds silently desync (critical for a public release).
        var req = new byte[5];
        req[0] = 0x01;
        System.Buffer.BlockCopy(System.BitConverter.GetBytes(SteamNetwork.PROTOCOL_VERSION), 0, req, 1, 4);
        SendPacket(SteamNetwork.RemoteID, req);
        _logger.LogInfo($"[CLIENT] Save request sent to host (protocol v{SteamNetwork.PROTOCOL_VERSION}) ✓");
    }

    private void FetchHostSteamID()
    {
        try
        {
            var steamIdType = _steamMatchmaking?.Assembly.GetType("Steamworks.CSteamID");
            var lobbyIdObj  = System.Activator.CreateInstance(steamIdType, SteamNetwork.LobbyID);

            var hostSteamId = _steamMatchmaking
                ?.GetMethod("GetLobbyOwner", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new[] { lobbyIdObj });

            var rawId = hostSteamId?.GetType()
                .GetField("m_SteamID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(hostSteamId);

            SteamNetwork.RemoteID = System.Convert.ToUInt64(rawId ?? 0UL);
            _logger.LogInfo($"[STEAM] Host SteamID: {SteamNetwork.RemoteID}");
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[STEAM] FetchHostSteamID error: {e.Message}");
        }
    }

}

// ─────────────────────────────────────────────────────────────────────────────
// HARMONY PATCHES
// ─────────────────────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────────────────────────────────────
// Patch against player duplicates from GameAwakenerEngine: interacting with a tree/stone makes it restore
// objects from the save, incl. Player(Clone). This disables any PlayerComponent that isn't MainGame.me.player.
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch]
public static class PlayerComponentStartPatch
{
    static MethodBase TargetMethod()
    {
        var type = AccessTools.TypeByName("PlayerComponent");
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        // We patch Start — that's where PlayerComponent initializes
        return type?.GetMethod("Start", flags)
            ?? type?.GetMethod("Awake", flags);
    }

    static void Postfix(MonoBehaviour __instance)
    {
        try
        {
            // If this is RemotePlayer_Clone or Player2_Clone — already disabled earlier, skip
            var goName = __instance.gameObject.name;
            if (goName == "RemotePlayer_Clone" || goName == "Player2_Clone") return;

            var flags        = BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Static | BindingFlags.Instance;
            var mainGameType = AccessTools.TypeByName("MainGame");
            var me           = mainGameType?.GetField("me", flags)?.GetValue(null);
            if (me == null) return;

            // Find MainGame.me.player_char or player
            var playerField = mainGameType.GetField("player_char", flags)
                           ?? mainGameType.GetField("player", flags);
            var mainPlayer = playerField?.GetValue(me) as MonoBehaviour;

            if (mainPlayer == null) return;

            // If this PlayerComponent is NOT the main player — disable it
            if (__instance != mainPlayer && __instance.gameObject != mainPlayer.gameObject)
            {
                __instance.enabled = false;
                Multiplayer.Log?.LogInfo($"[AWAKE] Disabled an extra PlayerComponent on {goName} " +
                    $"(not MainGame.me.player) ✓");
            }
        }
        catch (Exception e)
        {
            Multiplayer.Log?.LogWarning($"[AWAKE] PlayerComponentStartPatch: {e.Message}");
        }
    }
}

[HarmonyPatch]
static class EnvWeatherPatch
{
    static MethodBase TargetMethod() =>
        AccessTools.Method("EnvironmentEngine:UpdateWeather");

    static Exception Finalizer(Exception __exception) => null;
}

// ─────────────────────────────────────────────────────────────────────────────
// SimplifiedWGO.Restore throws a NullReferenceException every frame during
// interaction (chopping, digging). This is the main cause of 1 FPS on the client.
// The Finalizer silently swallows the exception — no logging, no overhead.
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch]
static class SimplifiedWGORestorePatch
{
    static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("SimplifiedWGO"), "Restore");

    static Exception Finalizer(Exception __exception) => null;
}

// GameAwakenerEngine.Update can also throw via SimplifiedWGO —
// we mute it too
[HarmonyPatch]
static class GameAwakenerEnginePatch
{
    static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("GameAwakenerEngine"), "Update");

    static Exception Finalizer(Exception __exception) => null;
}

// Block GameAwakenerEngine from restoring our clones: on interaction it "restores" all WGOs in radius,
// incl. RemotePlayer_Clone/Player2_Clone. SimplifiedWGO isn't a MonoBehaviour, so we check via WorldGameObject.
[HarmonyPatch]
static class SimplifiedWGORestoreFilterPatch
{
    static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("SimplifiedWGO"), "Restore");

    // Cache the FieldInfo once — GetField is expensive and this Prefix runs hundreds of times/frame while chopping.
    private static FieldInfo _wgoFieldCache;
    private static bool _wgoFieldSearched;

    static bool Prefix(object __instance)
    {
        if (__instance == null) return false;
        try
        {
            if (!_wgoFieldSearched)
            {
                _wgoFieldSearched = true;
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var type  = __instance.GetType();
                _wgoFieldCache = type.GetField("wgo", flags)
                              ?? type.GetField("_wgo", flags)
                              ?? type.GetField("world_game_object", flags);
            }

            if (_wgoFieldCache != null)
            {
                var wgo = _wgoFieldCache.GetValue(__instance) as UnityEngine.Component;
                if (wgo != null)
                {
                    var goName = wgo.gameObject?.name;
                    if (goName == "RemotePlayer_Clone" || goName == "Player2_Clone")
                        return false;
                }
            }
        }
        catch { }
        return true;
    }

    static Exception Finalizer(Exception __exception) => null;
}

[HarmonyPatch]
public static class RefreshPositionCachePatch
{
    static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("WorldGameObject"), "RefreshPositionCache");

    // No try/catch in the hot path (called for EVERY WGO during tree/stone interaction; try/catch in Unity
    // Mono is expensive even without an exception). Unity's operator bool returns false for destroyed objects.
    static bool Prefix(MonoBehaviour __instance)
    {
        return __instance != null && (bool)(UnityEngine.Object)__instance;
    }

    // The Finalizer swallows any exception that slips through — for edge cases
    static Exception Finalizer(Exception __exception) => null;
}

[HarmonyPatch]
public static class OnGameLoadedPatch
{
    static MethodBase TargetMethod() =>
        AccessTools.TypeByName("MainGame")
            ?.GetMethod("OnGameLoaded", BindingFlags.NonPublic | BindingFlags.Instance);

    static void Postfix()
    {
        Multiplayer.Log?.LogInfo($"[TUT] OnGameLoaded Postfix! IsClientMode={SteamNetwork.IsClientMode}");
        if (!SteamNetwork.IsClientMode) return;
        SteamManager.ForceSkipTutorialParams(Multiplayer.Log);
        Multiplayer.Log?.LogInfo("[TUT] OnGameLoaded: tutorial skipped ✓");
    }
}

[HarmonyPatch]
public static class IntroShowIntroPatch
{
    static MethodBase TargetMethod() =>
        AccessTools.TypeByName("Intro")
            ?.GetMethod("ShowIntro",
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance,
                null,
                new[] { typeof(Action), typeof(bool), typeof(bool) },
                null);

    static bool Prefix(Action on_finished)
    {
        if (!SteamNetwork.IsClientMode) return true;

        Multiplayer.Log.LogInfo("[CLIENT] Intro.ShowIntro — skipped, calling on_finished ✓");
        on_finished?.Invoke();
        return false;
    }
}

[HarmonyPatch]
public static class TutorialGUIBlockAllPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var tutType = AccessTools.TypeByName("TutorialGUI");
        if (tutType == null)
        {
            Multiplayer.Log?.LogWarning("[TUT] TutorialGUI type NOT found!");
            yield break;
        }

        var allFlags = BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Instance | BindingFlags.Static
                                           | BindingFlags.DeclaredOnly;

        var allMethods = tutType.GetMethods(allFlags);
        Multiplayer.Log?.LogInfo($"[TUT] TutorialGUI has {allMethods.Length} methods:");
        foreach (var m in allMethods)
            Multiplayer.Log?.LogInfo($"[TUT]   -> {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");

        int count = 0;
        foreach (var m in allMethods)
        {
            string n = m.Name;
            if (n == "Open" || n.StartsWith("Show") || n == "OnEnable")
            {
                Multiplayer.Log?.LogInfo($"[TUT] Patching: {n}");
                count++;
                yield return m;
            }
        }

        if (count == 0)
            Multiplayer.Log?.LogWarning("[TUT] NOTHING FOUND to patch!");

        var mainGameType = AccessTools.TypeByName("MainGame");
        if (mainGameType != null)
        {
            foreach (var m in mainGameType.GetMethods(allFlags | BindingFlags.Instance))
            {
                if (m.Name.Contains("Tutorial") || m.Name.Contains("Intro") || m.Name.Contains("tutorial"))
                {
                    Multiplayer.Log?.LogInfo($"[TUT] Patching MainGame.{m.Name}");
                    yield return m;
                }
            }
        }
    }

    // Phase gate (2026-06-13): block tutorials ONLY in the join/intro window (where the burst comes in).
    // After it we PASS THROUGH, because gameplay tutorials (energy/tech) MUST play: tut_shown_<id> is set on
    // Open (67022) while tech goes through the _on_closed callback in Hide (67058) — a blanket block ate both
    // → soft-lock (live test 2026-06-13, friend stuck at blacksmith). Stardew: the client lives its own
    // tutorials. A popup past the window just shows (safe degradation); gameplay ones can't reach 60s anyway.
    private const float INTRO_BLOCK_WINDOW = 60f;

    static bool Prefix(MethodBase __originalMethod)
    {
        if (!SteamNetwork.IsClientMode) return true;
        float since = SteamNetwork.IsInGameSince;
        bool introPhase = since < 0f || (UnityEngine.Time.time - since) < INTRO_BLOCK_WINDOW;
        if (!introPhase) return true;   // long in game — let the gameplay tutorial play
        Multiplayer.Log?.LogInfo($"[TUT] BLOCKED (intro phase): {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}");
        return false;
    }
}

[HarmonyPatch]
public static class OnGameStartedPlayingPatch
{
    // Guard — OnGameStartedPlaying can fire several times per session (each ShowIntro → a new Player(Clone)).
    // Allow only the first call.
    private static bool _hasRun = false;

    public static void Reset() { _hasRun = false; }

    static MethodBase TargetMethod() =>
        AccessTools.TypeByName("MainGame")
            ?.GetMethod("OnGameStartedPlaying",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    static bool Prefix()
    {
        if (!SteamNetwork.IsClientMode) return true;

        if (_hasRun)
        {
            Multiplayer.Log?.LogInfo("[TUT] OnGameStartedPlaying — blocked (duplicate) ✓");
            return false; // block the repeated run
        }
        _hasRun = true;
        return true;
    }

    static void Postfix(object __instance)
    {
        // The host also starts sending its position
        if (SteamNetwork.Role == NetworkRole.Host)
        {
            SteamNetwork.IsInGame = true;
            SteamNetwork.IsInGameSince = Time.time;
        }

        if (!SteamNetwork.IsClientMode) return;
        SteamManager.ForceSkipTutorialParams(Multiplayer.Log);
        Multiplayer.Log?.LogInfo("[TUT] OnGameStartedPlaying Postfix — tutorial skipped ✓");
    }
}

[HarmonyPatch]
public static class MainMenuGUIOpenPatch
{
    static MethodBase TargetMethod() =>
        AccessTools.TypeByName("MainMenuGUI")
            ?.GetMethod("Open",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(bool) }, null);

    static bool Prefix()
    {
        // Block opening the menu while the client is loading
        if (SteamNetwork.IsClientMode && SteamNetwork.IsLoadingAsClient)
        {
            Multiplayer.Log?.LogInfo("[CLIENT] MainMenuGUI.Open blocked (loading) ✓");
            return false;
        }

        if (SteamNetwork.IsClientMode && SteamNetwork.IsInGame)
        {
            float timeSinceInGame = UnityEngine.Time.time - SteamNetwork.IsInGameSince;

            // A REAL exit (ReturnToMainMenu just fired) — ALWAYS pass through, even within MENU_BLOCK_DURATION.
            // Without this a quick exit in the first 15s was cut off as spurious and the client got stuck.
            bool realExit = SteamNetwork.RealExitRequestedAt > 0f &&
                            (UnityEngine.Time.time - SteamNetwork.RealExitRequestedAt) < 2f;

            // The first MENU_BLOCK_DURATION seconds after join — we block spurious Open() from the game
            // loop during init (they do NOT go through ReturnToMainMenu).
            if (!realExit && timeSinceInGame < SteamNetwork.MENU_BLOCK_DURATION)
            {
                Multiplayer.Log?.LogInfo($"[CLIENT] MainMenuGUI.Open blocked (spurious, t={timeSinceInGame:F1}s) ✓");
                return false;
            }

            // After MENU_BLOCK_DURATION — a real exit. FIX for "exit with a corpse in hands": drop the overhead
            // BEFORE exiting while P2P is alive (0x11 reaches the partner in time; spurious cases filtered above).
            ChopSync.DropOverheadOnExit();
            // Reset IsInGame and allow the menu to open.
            Multiplayer.Log?.LogInfo($"[CLIENT] MainMenuGUI.Open allowed (player exit, t={timeSinceInGame:F1}s, real={realExit})");
            SteamNetwork.IsInGame = false;
            SteamNetwork.IsInGameSince = -1f;
            SteamNetwork.RealExitRequestedAt = -1f;
        }
        else if (!SteamNetwork.IsClientMode && SteamNetwork.IsInGame && SteamNetwork.IsConnected)
        {
            // The HOST exits to the menu with an item in hands — the same fix (the drop also goes into the save).
            ChopSync.DropOverheadOnExit();
        }

        return true;
    }
}

// Marker of a REAL exit to the menu. InGameMenuGUI.ReturnToMainMenu (decomp 60391) is called ONLY when the
// player chose "exit to menu" from the pause; a spurious init Open() does NOT come here. MainMenuGUIOpenPatch
// reads this marker so as not to cut off a quick legitimate exit within MENU_BLOCK_DURATION.
[HarmonyPatch]
public static class InGameMenuReturnPatch
{
    static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("InGameMenuGUI"), "ReturnToMainMenu");

    static void Prefix()
    {
        SteamNetwork.RealExitRequestedAt = UnityEngine.Time.time;
        // M1: save the client's character layer BEFORE the world unloads (the player is still alive).
        ClientCharacterStore.Save();
    }
}

// MainMenuDiagPatch removed — it patched every MainMenuGUI method and wrote a log
// on every call, which added overhead. No longer needed after stabilization.

// ─────────────────────────────────────────────────────────────────────────────
// TREE CHOPPING RECON (temporary — remove after Phase 0). Finds the tree HP field + the WGO methods that
// fire on hit/destruction. Workflow: F6 (snapshot) → F5 (trace) → chop → F6 again; the changed field = HP.
// ─────────────────────────────────────────────────────────────────────────────
public static class ChopRecon
{
    public static MonoBehaviour Tracked;
    public static bool TraceEnabled;
    private static FieldInfo _objIdField;

    public static void CaptureNearestTree()
    {
        try
        {
            var wgoType = AccessTools.TypeByName("WorldGameObject");
            if (wgoType == null) { Multiplayer.Log?.LogWarning("[RECON] WorldGameObject type not found"); return; }

            var playerGO = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()
                .FirstOrDefault(mb => mb.GetType().Name == "PlayerComponent"
                                      && mb.gameObject.name != "RemotePlayer_Clone"
                                      && mb.gameObject.name != "Player2_Clone")?.gameObject;
            if (playerGO == null) { Multiplayer.Log?.LogWarning("[RECON] Local player not found"); return; }
            Vector3 origin = playerGO.transform.position;

            if (_objIdField == null)
                _objIdField = wgoType.GetField("_obj_id",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            // Collect all WGOs with their distance to the player
            var candidates = new List<(MonoBehaviour mb, float dist, string objId, bool isTree)>();
            foreach (var comp in UnityEngine.Object.FindObjectsOfType(wgoType))
            {
                var mb = comp as MonoBehaviour;
                if (mb == null) continue;
                string n = mb.gameObject.name;
                if (n == "RemotePlayer_Clone" || n == "Player2_Clone" ||
                    n == "Player(Clone)" || n == "Player") continue;

                string objId = _objIdField?.GetValue(mb) as string ?? "";
                float dist = Vector3.Distance(mb.transform.position, origin);
                bool isTree = n.ToLower().Contains("tree") || objId.ToLower().Contains("tree");
                candidates.Add((mb, dist, objId, isTree));
            }

            if (candidates.Count == 0) { Multiplayer.Log?.LogWarning("[RECON] No WGO nearby found"); return; }
            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

            Multiplayer.Log?.LogInfo("[RECON] 8 nearest WGOs (to check names/obj_id):");
            foreach (var c in candidates.Take(8))
                Multiplayer.Log?.LogInfo($"[RECON]   {c.dist,6:F1}  {c.mb.gameObject.name}  obj_id='{c.objId}'  tree={c.isTree}");

            // Target = a tree if we're right next to it (≤30); else the nearest WGO. Without this radius, F6
            // in a mine matched a tree at 500m instead of the ore vein at 80m.
            const float CLOSE_TREE_RADIUS = 30f;
            var pick = candidates.FirstOrDefault(c => c.isTree && c.dist <= CLOSE_TREE_RADIUS);
            if (pick.mb == null) pick = candidates[0];

            Tracked = pick.mb;
            DoActionProbePatch.Reset();
            Multiplayer.Log?.LogInfo($"[RECON] ═══ TARGET: {pick.mb.gameObject.name} " +
                $"obj_id='{pick.objId}' dist={pick.dist:F1} ═══");
            DumpState("SNAPSHOT");
            DumpFellingInfo();
            DumpNearestGroundItem();
            DumpEverythingNearby();
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] CaptureNearestTree: {e}"); }
    }

    // B-2: capture the nearest GRAVE (obj_id starts with "grave") and dump all
    // fields. Separate from CaptureNearestTree, because that has the "tree priority ≤30" logic.
    // Workflow: F4 (snapshot) → dig one stage → F4 → diff the snapshots in the logs.
    public static void CaptureNearestGrave()
    {
        try
        {
            var wgoType = AccessTools.TypeByName("WorldGameObject");
            if (wgoType == null) { Multiplayer.Log?.LogWarning("[RECON] WorldGameObject type not found"); return; }

            var playerGO = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()
                .FirstOrDefault(mb => mb.GetType().Name == "PlayerComponent"
                                      && mb.gameObject.name != "RemotePlayer_Clone"
                                      && mb.gameObject.name != "Player2_Clone")?.gameObject;
            if (playerGO == null) { Multiplayer.Log?.LogWarning("[RECON] Local player not found"); return; }
            Vector3 origin = playerGO.transform.position;

            if (_objIdField == null)
                _objIdField = wgoType.GetField("_obj_id",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            MonoBehaviour best = null; float bestDist = float.MaxValue; string bestId = "";
            foreach (var comp in UnityEngine.Object.FindObjectsOfType(wgoType))
            {
                var mb = comp as MonoBehaviour;
                if (mb == null) continue;
                string objId = _objIdField?.GetValue(mb) as string ?? "";
                if (!objId.StartsWith("grave")) continue;
                float dist = Vector3.Distance(mb.transform.position, origin);
                if (dist < bestDist) { bestDist = dist; best = mb; bestId = objId; }
            }

            if (best == null) { Multiplayer.Log?.LogWarning("[RECON] No grave (grave*) nearby found"); return; }

            Tracked = best;
            DoActionProbePatch.Reset();
            Multiplayer.Log?.LogInfo($"[RECON] ═══ GRAVE: {best.gameObject.name} " +
                $"obj_id='{bestId}' dist={bestDist:F1} ═══");
            DumpState("SNAPSHOT-GRAVE");
            DumpFellingInfo();
            DumpNearestGroundItem();
            DumpSerializationApi(best);
            DumpItemSerializationApi(best);
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] CaptureNearestGrave: {e}"); }
    }

    // B-2 approach A-3: serialize-API of the Item itself (grave's _data; item_data in SerializableWGO is empty
    // at runtime). Looks for serial/save/load/string/byte/write/read/json methods + GameRes (_params), and
    // tries the most likely serializer.
    public static void DumpItemSerializationApi(MonoBehaviour wgo)
    {
        try
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var dataField = wgo.GetType().GetField("_data", flags);
            var data = dataField?.GetValue(wgo);
            if (data == null) { Multiplayer.Log?.LogInfo("[RECON] _data = null"); return; }
            var itemType = data.GetType();

            Multiplayer.Log?.LogInfo($"[RECON] ─── Item ({itemType.FullName}) serialize methods ───");
            bool Match(string ln) => ln.Contains("serial") || ln.Contains("save") || ln.Contains("load")
                || ln.Contains("tostring") || ln.Contains("byte") || ln.Contains("write")
                || ln.Contains("read") || ln.Contains("json") || ln.Contains("clone") || ln.Contains("copy");
            for (var t = itemType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) break;
                foreach (var m in t.GetMethods(flags | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    string ln = m.Name.ToLower();
                    if (ln.StartsWith("get_") || ln.StartsWith("set_") || !Match(ln)) continue;
                    var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                    Multiplayer.Log?.LogInfo($"[RECON]   {(m.IsStatic ? "static " : "")}{t.Name}.{m.Name}({ps}) -> {m.ReturnType?.Name}");
                }
            }

            // GameRes (_params) — the stage state. How to enumerate/reproduce it?
            var paramsObj = itemType.GetField("_params", flags)?.GetValue(data);
            if (paramsObj != null)
            {
                var gr = paramsObj.GetType();
                Multiplayer.Log?.LogInfo($"[RECON] ─── GameRes ({gr.FullName}) fields+methods ───");
                foreach (var fld in gr.GetFields(flags | BindingFlags.Static))
                    Multiplayer.Log?.LogInfo($"[RECON]   field: {fld.Name} ({fld.FieldType.Name})");
                foreach (var m in gr.GetMethods(flags | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    string ln = m.Name.ToLower();
                    if (ln.StartsWith("get_") || ln.StartsWith("set_")) continue;
                    var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                    Multiplayer.Log?.LogInfo($"[RECON]   {gr.Name}.{m.Name}({ps}) -> {m.ReturnType?.Name}");
                }
            }

            // ToJSON(ser_depth) — the main candidate for transporting state. We try several
            // depths and SHOW the full JSON: we need the depth that includes _params
            // (grave_top_stn_plate_1 etc.) AND the inventory parts. The inverse is JsonUtility.
            var toJson = itemType.GetMethod("ToJSON", flags, null, new[] { typeof(int) }, null);
            if (toJson != null)
            {
                foreach (int depth in new[] { 0, 1, 3, 8 })
                {
                    try
                    {
                        var r = toJson.Invoke(data, new object[] { depth }) as string;
                        Multiplayer.Log?.LogInfo($"[RECON]   ToJSON({depth}) len={r?.Length}:");
                        // log in ~400-char chunks (BepInEx truncates long lines)
                        for (int i = 0; r != null && i < r.Length; i += 400)
                            Multiplayer.Log?.LogInfo($"[RECON]     {r.Substring(i, Math.Min(400, r.Length - i))}");
                    }
                    catch (Exception ie) { Multiplayer.Log?.LogInfo($"[RECON]   ToJSON({depth}) failed: {ie.InnerException?.Message ?? ie.Message}"); }
                }
            }
            else Multiplayer.Log?.LogInfo("[RECON]   ToJSON(int) not found");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] DumpItemSerializationApi: {e.Message}"); }
    }

    // B-2/approach A: recon of the serialize-API for full WGO replication — how to make a SerializableWGO from
    // a WorldGameObject (inverse of RestoreFromSerializedObject). Dumps WGO serial methods, SerializableWGO
    // ctors+fields, and whether [Serializable] (i.e. can we push it through BinaryFormatter over the wire).
    public static void DumpSerializationApi(MonoBehaviour wgo)
    {
        try
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var wgoType = wgo.GetType();

            Multiplayer.Log?.LogInfo("[RECON] ─── SERIALIZE-API: WGO methods (serial / →Serializable*) ───");
            for (var t = wgoType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) break;
                foreach (var m in t.GetMethods(flags | BindingFlags.DeclaredOnly))
                {
                    string ln = m.Name.ToLower();
                    bool byName = ln.Contains("serial");
                    bool byRet  = m.ReturnType != null && m.ReturnType.Name.IndexOf("Serializable", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!byName && !byRet) continue;
                    var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                    Multiplayer.Log?.LogInfo($"[RECON]   {t.Name}.{m.Name}({ps}) -> {m.ReturnType?.Name}");
                }
            }

            var swType = AccessTools.TypeByName("SerializableWGO");
            if (swType == null) { Multiplayer.Log?.LogInfo("[RECON] SerializableWGO type not found"); return; }
            Multiplayer.Log?.LogInfo($"[RECON] ─── SerializableWGO ({swType.FullName}) ───");
            Multiplayer.Log?.LogInfo($"[RECON]   [Serializable]={swType.IsSerializable}");
            foreach (var c in swType.GetConstructors(flags))
            {
                var ps = string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                Multiplayer.Log?.LogInfo($"[RECON]   ctor({ps})");
            }
            foreach (var fld in swType.GetFields(flags))
                Multiplayer.Log?.LogInfo($"[RECON]   field: {fld.Name} ({fld.FieldType.Name})");
            // Static serialization factories/helpers across the whole type are useful too
            foreach (var m in swType.GetMethods(flags | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                Multiplayer.Log?.LogInfo($"[RECON]   static {m.Name}({ps}) -> {m.ReturnType?.Name}");
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] DumpSerializationApi: {e.Message}"); }
    }

    // Dump ALL GameObjects near the player (any type) with their components —
    // to find objects that aren't WorldGameObject (e.g. a log). From F6.
    public static void DumpEverythingNearby()
    {
        try
        {
            var playerGO = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()
                .FirstOrDefault(mb => mb.GetType().Name == "PlayerComponent"
                                      && mb.gameObject.name != "RemotePlayer_Clone"
                                      && mb.gameObject.name != "Player2_Clone")?.gameObject;
            if (playerGO == null) return;
            Vector3 origin = playerGO.transform.position;

            var hits = new List<KeyValuePair<float, GameObject>>();
            foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                float d = Vector3.Distance(go.transform.position, origin);
                if (d <= 250f) hits.Add(new KeyValuePair<float, GameObject>(d, go));
            }
            hits.Sort((a, b) => a.Key.CompareTo(b.Key));

            Multiplayer.Log?.LogInfo($"[RECON] ─── All GameObjects within radius 250 ({hits.Count}), top-30 ───");
            foreach (var h in hits.Take(30))
            {
                var comps = string.Join(", ", h.Value.GetComponents<Component>()
                    .Where(c => c != null).Select(c => c.GetType().Name));
                Multiplayer.Log?.LogInfo($"[RECON]   {h.Key,6:F1}  '{h.Value.name}'  [{comps}]");
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] DumpEverythingNearby: {e}"); }
    }

    // Recon of dropped loot: finds the nearest ItemOnGround and dumps its
    // components, fields and methods (drop/pickup sync). From F6.
    public static void DumpNearestGroundItem()
    {
        try
        {
            var t = AccessTools.TypeByName("DropResGameObject");
            if (t == null) { Multiplayer.Log?.LogInfo("[RECON] DropResGameObject type not found"); return; }

            var playerGO = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()
                .FirstOrDefault(mb => mb.GetType().Name == "PlayerComponent"
                                      && mb.gameObject.name != "RemotePlayer_Clone"
                                      && mb.gameObject.name != "Player2_Clone")?.gameObject;
            if (playerGO == null) return;
            Vector3 origin = playerGO.transform.position;

            MonoBehaviour nearest = null; float best = float.MaxValue;
            foreach (var c in UnityEngine.Object.FindObjectsOfType(t))
            {
                var mb = c as MonoBehaviour;
                if (mb == null) continue;
                float d = Vector3.Distance(mb.transform.position, origin);
                if (d < best) { best = d; nearest = mb; }
            }
            if (nearest == null)
            {
                Multiplayer.Log?.LogInfo("[RECON] No DropResGameObject nearby — chop a tree first");
                return;
            }

            Multiplayer.Log?.LogInfo($"[RECON] ═══ DropResGameObject '{nearest.gameObject.name}' @ {best:F1} ═══");
            Multiplayer.Log?.LogInfo("[RECON] Components:");
            foreach (var comp in nearest.gameObject.GetComponents<Component>())
                if (comp != null) Multiplayer.Log?.LogInfo($"[RECON]   - {comp.GetType().Name}");

            DumpFields(nearest, 0, "");

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Multiplayer.Log?.LogInfo("[RECON] Methods:");
            for (var ty = nearest.GetType(); ty != null; ty = ty.BaseType)
            {
                if (ty.Namespace != null && ty.Namespace.StartsWith("UnityEngine")) break;
                if (ty == typeof(object)) break;
                foreach (var m in ty.GetMethods(flags | BindingFlags.DeclaredOnly))
                {
                    if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
                    var ps = string.Join(", ", m.GetParameters()
                        .Select(p => p.ParameterType.Name + " " + p.Name));
                    Multiplayer.Log?.LogInfo($"[RECON]   {ty.Name}.{m.Name}({ps}) -> {m.ReturnType.Name}");
                }
            }

            // ── Highlight for the 0x0C sync ────────────────────────────────────────
            // Drop position (the matching key between machines) + which item it carries.
            var p = nearest.transform.position;
            Multiplayer.Log?.LogInfo($"[RECON] >>> 0x0C: drop pos=({p.x:F1},{p.y:F1})");

            // Candidates for the PICKUP method — so we patch exactly it (the 0x0C sender).
            Multiplayer.Log?.LogInfo("[RECON] >>> 0x0C PICKUP CANDIDATES:");
            for (var ty = nearest.GetType(); ty != null; ty = ty.BaseType)
            {
                if (ty.Namespace != null && ty.Namespace.StartsWith("UnityEngine")) break;
                if (ty == typeof(object)) break;
                foreach (var m in ty.GetMethods(flags | BindingFlags.DeclaredOnly))
                {
                    string ln = m.Name.ToLower();
                    if (!(ln.Contains("collect") || ln.Contains("pick") || ln.Contains("give")
                          || ln.Contains("take") || ln.Contains("grab") || ln.Contains("fly")
                          || ln.Contains("reward") || ln.Contains("catch") || ln.Contains("equip")
                          || ln.Contains("destroy") || ln.Contains("remove"))) continue;
                    var ps = string.Join(", ", m.GetParameters()
                        .Select(p => p.ParameterType.Name + " " + p.Name));
                    Multiplayer.Log?.LogInfo($"[RECON]   >>> {ty.Name}.{m.Name}({ps}) -> {m.ReturnType.Name}");
                }
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] DumpNearestGroundItem: {e}"); }
    }

    private static bool IsFellingCandidate(string ln) =>
        ln.Contains("replace") || ln.Contains("reborn") || ln.Contains("morph")
        || ln.Contains("destroy") || ln.Contains("object") || ln.Contains("dead")
        || ln.Contains("stump") || ln.Contains("anim") || ln.Contains("dying")
        || ln.Contains("fall") || ln.Contains("play") || ln.Contains("tween")
        || ln.Contains("drop");

    // Dump candidate methods (replace/animation/fall) across the obj type hierarchy.
    private static void DumpCandidateMethods(object obj, string tag,
                                             HashSet<string> seen, BindingFlags flags)
    {
        Multiplayer.Log?.LogInfo($"[RECON] ─── Candidate methods {tag} ({obj.GetType().Name}) ───");
        for (var t = obj.GetType(); t != null; t = t.BaseType)
        {
            if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) break;
            if (t == typeof(object)) break;
            foreach (var m in t.GetMethods(flags | BindingFlags.DeclaredOnly))
            {
                string ln = m.Name.ToLower();
                if (ln.StartsWith("get_") || ln.StartsWith("set_")) continue;
                if (!IsFellingCandidate(ln)) continue;
                var ps = string.Join(", ", m.GetParameters()
                    .Select(p => p.ParameterType.Name + " " + p.Name));
                if (seen.Add($"{tag}|{m.Name}|{ps}"))
                    Multiplayer.Log?.LogInfo($"[RECON]   {t.Name}.{m.Name}({ps})");
            }
        }
    }

    // Felling recon: dump the target's obj_def + candidate replace/animation methods
    // (ReplaceWithObject, falling, etc.) with signatures. From F6 together with the SNAPSHOT.
    public static void DumpFellingInfo()
    {
        if (Tracked == null) return;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var seen = new HashSet<string>();
        DumpCandidateMethods(Tracked, "WGO", seen, flags);

        // wop (WorldObjectPart) — the object's visual part; the fall animation is here.
        var wop = Tracked.GetType().GetField("wop", flags)?.GetValue(Tracked);
        if (wop != null) DumpCandidateMethods(wop, "wop", seen, flags);

        var objDef = Tracked.GetType().GetField("obj_def", flags)?.GetValue(Tracked);
        if (objDef != null)
        {
            Multiplayer.Log?.LogInfo($"[RECON] ─── obj_def ({objDef.GetType().Name}) fields ───");
            DumpFields(objDef, 1, "obj_def.");

            // after_hp_0 — what the object turns into after felling (the stump id).
            var ahp = objDef.GetType()
                .GetField("after_hp_0", flags)?.GetValue(objDef);
            if (ahp != null)
            {
                Multiplayer.Log?.LogInfo($"[RECON] ─── after_hp_0 ({ahp.GetType().Name}) fields+methods ───");
                DumpFields(ahp, 1, "after_hp_0.");
                foreach (var m in ahp.GetType()
                    .GetMethods(flags | BindingFlags.DeclaredOnly))
                {
                    if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
                    var ps = string.Join(", ", m.GetParameters()
                        .Select(p => p.ParameterType.Name + " " + p.Name));
                    Multiplayer.Log?.LogInfo($"[RECON]   method: {m.Name}({ps}) -> {m.ReturnType.Name}");
                }
                try
                {
                    Multiplayer.Log?.LogInfo($"[RECON]   after_hp_0.ToString() = \"{ahp}\"");
                }
                catch { }
            }
            else Multiplayer.Log?.LogInfo("[RECON] after_hp_0 = null");
        }
        else Multiplayer.Log?.LogInfo("[RECON] obj_def = null");

        DumpAnimators();
    }

    // Dump the tree's components (including children) and all Animators with states,
    // clips and parameters — we're looking for the tree's fall/death animation.
    private static void DumpAnimators()
    {
        if (Tracked == null) return;
        try
        {
            Multiplayer.Log?.LogInfo("[RECON] ─── Components (tree + children) ───");
            foreach (var c in Tracked.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                Multiplayer.Log?.LogInfo($"[RECON]   {c.gameObject.name} : {c.GetType().Name}");
            }

            foreach (var anim in Tracked.GetComponentsInChildren<Animator>(true))
            {
                var rac = anim.runtimeAnimatorController;
                Multiplayer.Log?.LogInfo($"[RECON] ─── Animator '{anim.gameObject.name}' " +
                    $"(controller={(rac != null ? rac.name : "null")}) ───");
                if (rac != null)
                    foreach (var clip in rac.animationClips)
                        Multiplayer.Log?.LogInfo($"[RECON]   clip: {clip.name} ({clip.length:F2}s)");
                foreach (var p in anim.parameters)
                    Multiplayer.Log?.LogInfo($"[RECON]   parameter: {p.name} ({p.type})");
            }

            // API of the tree's animation components — methods and fields.
            var flags = BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var dumped = new HashSet<string>();
            foreach (var c in Tracked.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var ct = c.GetType();
                var tn = ct.Name;
                if (!(tn.Contains("Tree") || tn.Contains("Fall")
                      || tn.Contains("Disappear") || tn.Contains("Chop"))) continue;
                if (!dumped.Add(tn)) continue;
                Multiplayer.Log?.LogInfo($"[RECON] ─── {tn} methods+fields ───");
                foreach (var m in ct.GetMethods(flags))
                {
                    if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
                    var ps = string.Join(", ", m.GetParameters()
                        .Select(p => p.ParameterType.Name + " " + p.Name));
                    Multiplayer.Log?.LogInfo($"[RECON]   method: {m.Name}({ps}) -> {m.ReturnType.Name}");
                }
                foreach (var fld in ct.GetFields(flags))
                    Multiplayer.Log?.LogInfo($"[RECON]   field: {fld.Name} ({fld.FieldType.Name})");
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] DumpAnimators: {e.Message}"); }
    }

    // Dump any object (for probing DoAction arguments)
    public static void DumpAny(object obj, string label)
    {
        if (obj == null) { Multiplayer.Log?.LogInfo($"[RECON] {label}: null"); return; }
        string n = obj is MonoBehaviour mb ? mb.gameObject.name : obj.GetType().Name;
        Multiplayer.Log?.LogInfo($"[RECON] ─── {label}: {n} ───");
        DumpFields(obj, 0, "");
    }

    public static void DumpState(string label)
    {
        if (Tracked == null) { Multiplayer.Log?.LogWarning("[RECON] No target captured — press F6 first"); return; }
        Multiplayer.Log?.LogInfo($"[RECON] ─── {label}: {Tracked.gameObject.name} ───");

        Multiplayer.Log?.LogInfo("[RECON] Components on the GameObject:");
        foreach (var mb in Tracked.gameObject.GetComponents<MonoBehaviour>())
            if (mb != null) Multiplayer.Log?.LogInfo($"[RECON]   - {mb.GetType().Name}");

        DumpFields(Tracked, 0, "");
    }

    // Dump an object's fields across its game-type hierarchy.
    // DROP UID RECON (Zonda 2026-06-09): does a runtime drop (corpse) have a STABLE uid, the SAME on both
    // machines? If so → universal drop sync by uid without a grave anchor. F3: stand near a corpse → dump the
    // drop + nested Item (2 levels). Capture on BOTH machines for the SAME corpse → compare *id/*uid.
    public static void DumpNearestBodyDrop()
    {
        var t = AccessTools.TypeByName("DropResGameObject");
        if (t == null) { Multiplayer.Log?.LogWarning("[RECON-DROP] DropResGameObject type not found"); return; }
        MonoBehaviour best = null; float bestD = float.MaxValue;
        foreach (var c in UnityEngine.Object.FindObjectsOfType(t))
        {
            var mb = c as MonoBehaviour;
            if (mb == null || DropId(mb) != "body") continue;
            float d = Camera.main != null
                ? Vector3.Distance(mb.transform.position, Camera.main.transform.position) : 0f;
            if (d < bestD) { bestD = d; best = mb; }
        }
        if (best == null) { Multiplayer.Log?.LogWarning("[RECON-DROP] no corpse drop nearby (stand near a corpse on the ground)"); return; }
        Multiplayer.Log?.LogInfo($"[RECON-DROP] ===== Corpse go={best.gameObject.name} instId={best.GetInstanceID()} " +
            $"pos=({best.transform.position.x:F1},{best.transform.position.y:F1}) =====");
        DumpFieldsDeep(best, "drop.", 0);
    }

    // Reads the Item id from a DropResGameObject (a typed field with .id:string).
    private static string DropId(MonoBehaviour drop)
    {
        if (drop == null) return null;
        foreach (var f in drop.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            object v; try { v = f.GetValue(drop); } catch { continue; }
            var idF = v?.GetType().GetField("id");
            if (idF != null && idF.FieldType == typeof(string)) return idF.GetValue(v) as string;
        }
        return null;
    }

    // Deep dump (2 levels): all of an object's fields + nested reference fields (Item etc.), to
    // see any *id/*uid. Marks fields with "id"/"uid" in the name with a ★ marker.
    private static void DumpFieldsDeep(object obj, string prefix, int depth)
    {
        if (obj == null || depth > 2) return;
        var seen = new HashSet<string>();
        for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) break;
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!seen.Add(f.Name)) continue;
                object val; try { val = f.GetValue(obj); } catch { val = "<err>"; }
                string fl = f.Name.ToLower();
                string mark = (fl.Contains("id") || fl.Contains("uid") || fl.Contains("guid")) ? " ★" : "";
                Multiplayer.Log?.LogInfo($"[RECON-DROP]   {prefix}{f.Name} ({f.FieldType.Name}) = {Format(val)}{mark}");
                if (depth < 2 && val != null)
                {
                    var vt = val.GetType();
                    if (!vt.IsPrimitive && !vt.IsEnum && vt != typeof(string) &&
                        !(val is UnityEngine.Object) && !(val is System.Collections.IEnumerable))
                        DumpFieldsDeep(val, prefix + f.Name + ".", depth + 1);
                }
            }
        }
    }

    private static void DumpFields(object obj, int depth, string prefix)
    {
        if (obj == null || depth > 1) return;
        var seen = new HashSet<string>();
        for (var t = obj.GetType(); t != null; t = t.BaseType)
        {
            if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) break;
            if (t == typeof(object)) break;

            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!seen.Add(f.Name)) continue;
                object val;
                try { val = f.GetValue(obj); } catch { val = "<read error>"; }
                Multiplayer.Log?.LogInfo($"[RECON]   {prefix}{f.Name} ({f.FieldType.Name}) = {Format(val)}");
                if (depth == 0 && ShouldRecurse(f, val))
                    DumpFields(val, depth + 1, prefix + f.Name + ".");
            }
        }
    }

    private static bool ShouldRecurse(FieldInfo f, object val)
    {
        if (val == null) return false;
        var ft = val.GetType();
        if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) return false;
        if (val is UnityEngine.Object || val is System.Collections.IEnumerable) return false;
        string fn = f.Name.ToLower();
        // Also catch `_data` (Item) — RECON 2026-06-05: the grave stage is stored
        // INSIDE the object right here. The old `fn == "data"` didn't match the underscore.
        return fn.Contains("data")
            || ft.Name.IndexOf("Data", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Format(object val)
    {
        if (val == null) return "null";
        if (val is string s) return $"\"{s}\"";
        var t = val.GetType();
        if (t.IsPrimitive || t.IsEnum) return val.ToString();
        if (val is UnityEngine.Object uo) return uo == null ? "null(destroyed)" : $"{uo.name} <{t.Name}>";
        if (val is System.Collections.ICollection col) return $"<{t.Name} count={col.Count}>";
        try { string r = val.ToString(); return r.Length > 120 ? r.Substring(0, 120) + "…" : r; }
        catch { return $"<{t.Name}>"; }
    }
}

// Traces WorldGameObject methods that look like interaction/hit/destruction, but logs
// only for the captured target (ChopRecon.Tracked) and only when tracing is enabled.
[HarmonyPatch]
public static class WGOChopTracePatch
{
    static readonly string[] Keywords =
    {
        "hit", "damage", "destroy", "break", "multi", "interact",
        "action", "work", "take", "remove", "chop", "harvest", "spawn", "die",
        "drop", "give", "loot", "resource", "kill", "death", "dead",
        "reward", "drain", "consume", "progress",
        "anim", "dying", "fall", "play", "tween", "state",
        // B-2: a grave is a craft interaction, not hp. We catch craft/parts/visual
        // and object-rebuild methods (ReplaceWithObject/SetObject/Reset/Init…).
        "craft", "part", "wop", "inventory", "object", "reset", "init",
        "build", "finish", "complete", "step",
    };

    static IEnumerable<MethodBase> TargetMethods()
    {
        var wgoType = AccessTools.TypeByName("WorldGameObject");
        if (wgoType == null) yield break;

        var flags = BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var m in wgoType.GetMethods(flags))
        {
            if (m.IsAbstract || m.IsGenericMethodDefinition) continue;
            if (m.GetMethodBody() == null) continue;
            string ln = m.Name.ToLower();
            if (ln.StartsWith("get_") || ln.StartsWith("set_")) continue;
            if (Keywords.Any(k => ln.Contains(k)))
                yield return m;
        }
    }

    static void Prefix(MonoBehaviour __instance, MethodBase __originalMethod, object[] __args)
    {
        if (!ChopRecon.TraceEnabled || __instance == null) return;
        if (__instance != ChopRecon.Tracked) return;
        string args = __args != null && __args.Length > 0
            ? string.Join(", ", __args.Select(a => a?.ToString() ?? "null"))
            : "";
        Multiplayer.Log?.LogInfo($"[RECON-TRACE] {__originalMethod.Name}({args})");
    }

    static Exception Finalizer(Exception __exception) => null;
}

// CORPSE PICKUP RECON (2026-06-08): spawn decoded (corpse via WGO.DropItem, id=body). Now find the PICKUP
// METHOD (taken MANUALLY by collision, possibly NOT CollectDrop). Traces DropResGameObject collect/pick/
// take/grab/catch/fly/destroy/remove + dumps id and stack. Gated by F5. Workflow (SOLO): F5 → pick up a
// corpse → [CORPSE-PICK] method+stack.
[HarmonyPatch]
public static class CorpsePickupTracePatch
{
    static readonly string[] Keywords =
        { "collect", "pick", "take", "grab", "catch", "fly", "destroy", "remove", "give" };

    static IEnumerable<MethodBase> TargetMethods()
    {
        var t = AccessTools.TypeByName("DropResGameObject");
        if (t == null) yield break;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var seen = new HashSet<string>();
        for (var ty = t; ty != null && ty != typeof(MonoBehaviour) && ty != typeof(object); ty = ty.BaseType)
            foreach (var m in ty.GetMethods(flags))
            {
                if (m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null) continue;
                string ln = m.Name.ToLower();
                if (ln.StartsWith("get_") || ln.StartsWith("set_")) continue;
                if (!Keywords.Any(k => ln.Contains(k))) continue;
                if (seen.Add(m.Name)) yield return m;
            }
    }

    static void Prefix(MonoBehaviour __instance, MethodBase __originalMethod)
    {
        if (!ChopRecon.TraceEnabled || __instance == null) return;
        try
        {
            string id = "?";
            foreach (var f in __instance.GetType().GetFields(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var v = f.GetValue(__instance);
                var idF = v?.GetType().GetField("id");
                if (idF != null && idF.FieldType == typeof(string))
                { id = idF.GetValue(v) as string ?? "?"; break; }
            }
            var stack = string.Join("\n", (Environment.StackTrace ?? "").Split('\n').Skip(2).Take(12));
            Multiplayer.Log?.LogInfo($"[CORPSE-PICK] {__originalMethod.Name} id={id} go={__instance.gameObject.name}\n{stack}");
        }
        catch { }
    }

    static Exception Finalizer(Exception __exception) => null;
}

// CORPSE RE-DROP/CARRY RECON (2026-06-09): the corpse is CARRIABLE (on pickup it's carried, not destroyed).
// Find (a) take-in-hands, (b) DROP-from-hands — re-drop does NOT go through WGO.DropItem (0 hooks in the log).
// Traces WGO methods hand/throw/put/place/drop (except already-hooked DropItem/DropItems) + arg types and
// Item.id. Gated by F5. Same safe pattern as CorpsePickupTracePatch.
[HarmonyPatch]
public static class CorpseHandsTracePatch
{
    static readonly string[] Keywords = { "hand", "throw", "put", "place", "drop" };

    static IEnumerable<MethodBase> TargetMethods()
    {
        var t = AccessTools.TypeByName("WorldGameObject");
        if (t == null) yield break;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var seen = new HashSet<string>();
        foreach (var m in t.GetMethods(flags))
        {
            if (m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null) continue;
            string ln = m.Name.ToLower();
            if (ln.StartsWith("get_") || ln.StartsWith("set_")) continue;
            if (m.Name == "DropItem" || m.Name == "DropItems") continue;   // already hooked separately
            if (!Keywords.Any(k => ln.Contains(k))) continue;
            if (m.GetParameters().Length > 5) continue;
            if (seen.Add(m.Name)) yield return m;
        }
    }

    static void Prefix(MonoBehaviour __instance, object[] __args, MethodBase __originalMethod)
    {
        if (!ChopRecon.TraceEnabled) return;
        try
        {
            var parts = new List<string>();
            if (__args != null)
                foreach (var a in __args)
                {
                    if (a == null) { parts.Add("null"); continue; }
                    var idF = a.GetType().GetField("id");
                    string idv = (idF != null && idF.FieldType == typeof(string)) ? (idF.GetValue(a) as string) : null;
                    parts.Add(idv != null ? $"{a.GetType().Name}(id={idv})" : a.GetType().Name);
                }
            var stack = string.Join("\n", (Environment.StackTrace ?? "").Split('\n').Skip(2).Take(10));
            Multiplayer.Log?.LogInfo($"[CORPSE-HANDS] {__originalMethod.Name}({string.Join(", ", parts)})\n{stack}");
        }
        catch { }
    }

    static Exception Finalizer(Exception __exception) => null;
}

// RECON #3: does CanPickupWithInteraction actually PICK UP (returns true at the moment of taking)
// or is it just a per-frame check. We log __result (gated by F5).
[HarmonyPatch]
public static class CanPickupReturnReconPatch
{
    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("DropResGameObject");
        return t?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "CanPickupWithInteraction");
    }

    static void Postfix(MonoBehaviour __instance, object __result)
    {
        if (!ChopRecon.TraceEnabled || __instance == null) return;
        try
        {
            string id = "?";
            foreach (var f in __instance.GetType().GetFields(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var v = f.GetValue(__instance);
                var idF = v?.GetType().GetField("id");
                if (idF != null && idF.FieldType == typeof(string))
                { id = idF.GetValue(v) as string ?? "?"; break; }
            }
            Multiplayer.Log?.LogInfo($"[CORPSE-CANPICK] id={id} result={__result}");
        }
        catch { }
    }

    static Exception Finalizer(Exception __exception) => null;
}

// Probe DoAction(player, amount): dumps the player arg before/after for the captured target — to see whether
// DoAction mutates the player (energy/state) on the remote machine. Once per target (reset on F6).
[HarmonyPatch]
public static class DoActionProbePatch
{
    private static bool _done;
    public static void Reset() => _done = false;

    static MethodBase TargetMethod()
    {
        var wgo = AccessTools.TypeByName("WorldGameObject");
        return wgo?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DoAction"
                              && m.GetParameters().Length == 2
                              && m.GetParameters()[1].ParameterType == typeof(float));
    }

    static void Prefix(MonoBehaviour __instance, object[] __args)
    {
        if (_done || __instance == null || __instance != ChopRecon.Tracked) return;
        Multiplayer.Log?.LogInfo($"[RECON-PROBE] ═══ DoAction BEFORE, amount={__args.ElementAtOrDefault(1)} ═══");
        ChopRecon.DumpAny(__args.ElementAtOrDefault(0), "PLAYER-ARG BEFORE");
    }

    static void Postfix(MonoBehaviour __instance, object[] __args)
    {
        if (_done || __instance == null || __instance != ChopRecon.Tracked) return;
        _done = true;
        ChopRecon.DumpAny(__args.ElementAtOrDefault(0), "PLAYER-ARG AFTER");
        Multiplayer.Log?.LogInfo("[RECON-PROBE] ═══ DoAction done (compare BEFORE/AFTER) ═══");
    }

    static Exception Finalizer(Exception __exception) => null;
}

// ═════════════════════════════════════════════════════════════════════════════
// PHASE 1 — TREE CHOPPING SYNCHRONIZATION
// The "chopping left" counter is in the player's work, NOT the tree (recon: tree fields don't change between
// hits), so a repeated DoAction can't fell the tree. Model:
//   0x09 — hit: repeat DoAction → tree shakes (cosmetic).
//   0x0A — felled: when DropItems fires on the chopper's machine, broadcast the destruction → receiver removes its tree.
// Tree identified by position (unique_id differs per machine); loot drops only for the actual chopper.
// ═════════════════════════════════════════════════════════════════════════════

// B-2 approach A: SerializableWGO is [Serializable] but contains Unity structs (Vector3/Vector2) that
// BinaryFormatter can't handle. Surrogates teach it to (de)serialize them so the whole graph goes over the
// wire. Same pattern as Unity save systems.
[Serializable] sealed class Vector3Surrogate : System.Runtime.Serialization.ISerializationSurrogate
{
    public void GetObjectData(object o, System.Runtime.Serialization.SerializationInfo i, System.Runtime.Serialization.StreamingContext c)
    { var v = (Vector3)o; i.AddValue("x", v.x); i.AddValue("y", v.y); i.AddValue("z", v.z); }
    public object SetObjectData(object o, System.Runtime.Serialization.SerializationInfo i, System.Runtime.Serialization.StreamingContext c, System.Runtime.Serialization.ISurrogateSelector s)
    { return new Vector3(i.GetSingle("x"), i.GetSingle("y"), i.GetSingle("z")); }
}
[Serializable] sealed class Vector2Surrogate : System.Runtime.Serialization.ISerializationSurrogate
{
    public void GetObjectData(object o, System.Runtime.Serialization.SerializationInfo i, System.Runtime.Serialization.StreamingContext c)
    { var v = (Vector2)o; i.AddValue("x", v.x); i.AddValue("y", v.y); }
    public object SetObjectData(object o, System.Runtime.Serialization.SerializationInfo i, System.Runtime.Serialization.StreamingContext c, System.Runtime.Serialization.ISurrogateSelector s)
    { return new Vector2(i.GetSingle("x"), i.GetSingle("y")); }
}
[Serializable] sealed class QuaternionSurrogate : System.Runtime.Serialization.ISerializationSurrogate
{
    public void GetObjectData(object o, System.Runtime.Serialization.SerializationInfo i, System.Runtime.Serialization.StreamingContext c)
    { var q = (Quaternion)o; i.AddValue("x", q.x); i.AddValue("y", q.y); i.AddValue("z", q.z); i.AddValue("w", q.w); }
    public object SetObjectData(object o, System.Runtime.Serialization.SerializationInfo i, System.Runtime.Serialization.StreamingContext c, System.Runtime.Serialization.ISurrogateSelector s)
    { return new Quaternion(i.GetSingle("x"), i.GetSingle("y"), i.GetSingle("z"), i.GetSingle("w")); }
}
[Serializable] sealed class ColorSurrogate : System.Runtime.Serialization.ISerializationSurrogate
{
    public void GetObjectData(object o, System.Runtime.Serialization.SerializationInfo i, System.Runtime.Serialization.StreamingContext c)
    { var col = (Color)o; i.AddValue("r", col.r); i.AddValue("g", col.g); i.AddValue("b", col.b); i.AddValue("a", col.a); }
    public object SetObjectData(object o, System.Runtime.Serialization.SerializationInfo i, System.Runtime.Serialization.StreamingContext c, System.Runtime.Serialization.ISurrogateSelector s)
    { return new Color(i.GetSingle("r"), i.GetSingle("g"), i.GetSingle("b"), i.GetSingle("a")); }
}

