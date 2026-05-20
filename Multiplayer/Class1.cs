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
using Steamworks;
using Object = UnityEngine.Object;

// ─────────────────────────────────────────────────────────────────────────────
// Зберігаємо посилання на P2 глобально, щоб патчі могли його перевіряти
// ─────────────────────────────────────────────────────────────────────────────
public static class MultiplayerState
{
    public static GameObject Player2;
}

// ─────────────────────────────────────────────────────────────────────────────
// ПАТЧ 1 — PlayerControlIsDisabled завжди повертає false для P2
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
        if (MultiplayerState.Player2 != null &&
            __instance.gameObject == MultiplayerState.Player2)
        {
            __result = false;
            return false;
        }
        return true;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ПАТЧ 2 — UpdatePlayer
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
        // Мережевий клон керується RemotePlayerController (позиція з мережі).
        // Якщо дати грі обробити UpdatePlayer — клон почне рухатись інпутом
        // локального гравця і копіювати його рухи. Блокуємо повністю.
        if (__instance.gameObject.name == "RemotePlayer_Clone")
            return false;

        if (MultiplayerState.Player2 == null ||
            __instance.gameObject != MultiplayerState.Player2)
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
// ПАТЧ 3 — SmartAnimationController.Update
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

    // Кешуємо кореневий Transform P2 щоб не робити GetComponentInParent кожен кадр
    // для кожного SmartAnimationController в сцені (NPC, предмети — може бути сотні)
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

        // Оновлюємо кеш тільки якщо P2 змінився
        if (_p2GoCache != MultiplayerState.Player2)
        {
            _p2GoCache   = MultiplayerState.Player2;
            _p2RootCache = MultiplayerState.Player2.transform;
        }

        // Порівнюємо кореневий transform напряму — без GetComponentInParent
        return __instance.transform.root != _p2RootCache;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Дубль-гравець на клієнті: SaveSlotsMenuGUI.StartPlayingGame викликається двічі —
// наш кастомний флоу завантаження сейву хоста накладається на природний флоу гри.
// Кожен виклик спавнить окремий Player(Clone). Блокуємо всі виклики після першого
// в межах сесії клієнта. Прапор скидається при переході в меню.
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
            Multiplayer.Log?.LogInfo("[SPAWN] Повторний StartPlayingGame заблоковано — запобігання дублю гравця ✓");
            return false;
        }
        AlreadyStarted = true;
        return true;
    }
}

// ═════════════════════════════════════════════════════════════

[BepInPlugin("com.denys.multiplayer", "Multiplayer", "1.0.0")]
public class Multiplayer : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    private GameObject player;
    private GameObject player2;
    private bool showPosition = false;
    public static Multiplayer Instance;
    
    // Клон іншого гравця (remote) — існує на обох сторонах
    private GameObject _remotePlayerGO;

    private float logTimer = 0f;
    private const float LOG_INTERVAL = 1f;

    void Awake()
    {
        Log = Logger;
        Instance = this;

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
                    Logger.LogError($"[MP] Патч впав на {type.Name}: {e.Message}");
                }
            }
            Logger.LogInfo("[MP] PatchAll успішно ✓");

            var tutType = AccessTools.TypeByName("TutorialGUI");
            var openMethod = tutType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Open");
            var patchInfo = Harmony.GetPatchInfo(openMethod);
            Logger.LogInfo($"[TUT] Open патчів: prefixes={patchInfo?.Prefixes?.Count ?? 0}");
        }
        catch (Exception e)
        {
            Logger.LogError($"[MP] PatchAll впав: {e.Message}\n{e.StackTrace}");
        }

        var onGameLoaded = AccessTools.TypeByName("MainGame")
            ?.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "OnGameLoaded");
        Logger.LogInfo($"[MP] OnGameLoaded знайдено: {onGameLoaded != null}");

        var patches2 = Harmony.GetPatchInfo(onGameLoaded);
        Logger.LogInfo($"[MP] OnGameLoaded патчів: postfixes={patches2?.Postfixes?.Count ?? 0}");

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
            Logger.LogInfo($"[MP] UpdatePlayer патчів: prefixes={patches?.Prefixes?.Count ?? 0}");
        }

        var pcdMethod = bcType?.GetMethod("PlayerControlIsDisabled", flags);
        if (pcdMethod != null)
        {
            var patches1 = Harmony.GetPatchInfo(pcdMethod);
            Logger.LogInfo($"[MP] PlayerControlIsDisabled патчів: prefixes={patches1?.Prefixes?.Count ?? 0}");
        }

        // Application.logMessageReceived видалено — викликається для КОЖНОГО
        // exception включаючи SimplifiedWGO.Restore що кидає кожен кадр.
        // Тепер exceptions заглушуються через Harmony Finalizer напряму.

        SceneManager.sceneLoaded += (scene, mode) =>
        {
            Logger.LogInfo($"[SCENE] Завантажено сцену: name='{scene.name}' buildIndex={scene.buildIndex} mode={mode}");
        };
        SceneManager.sceneUnloaded += (scene) =>
        {
            Logger.LogInfo($"[SCENE] Вивантажено сцену: name='{scene.name}'");
        };
        SceneManager.activeSceneChanged += (from, to) =>
        {
            Logger.LogInfo($"[SCENE] Активна сцена: '{from.name}' -> '{to.name}'");
            if (to.name.ToLower().Contains("menu") || to.buildIndex == 0)
            {
                Logger.LogInfo("[SCENE] Перехід в меню — скидаємо стан");
                SteamNetwork.IsInGame = false;
                SteamNetwork.IsInGameSince = -1f;
                SteamNetwork.IsLoadingAsClient = false;
                SteamNetwork.IsClientMode = false;
                SteamNetwork.Role = NetworkRole.None;
                SteamNetwork.LobbyID = 0;
                SteamNetwork.RemoteID = 0;
                // ── Фаза 3: скидаємо клон при зміні сцени ──────────────────
                SteamNetwork.RemotePlayerSpawned = false;
                _remotePlayerGO = null;
                // Скидаємо guards для наступної сесії
                SteamManager._unlockAlreadyDone = false;
                OnGameStartedPlayingPatch.Reset();
                StartPlayingGameGuardPatch.AlreadyStarted = false;
            }
        };
    }

    private bool _hadPlayer2 = false;
    private int _p1InstanceId = 0;
    private float _p1MissingTimer = 0f;
    private const float P1_MISSING_THRESHOLD = 2f;

    void Update()
    {
        if (player != null && !_hadPlayer2)
            _p1InstanceId = player.GetInstanceID();

        if (_hadPlayer2 && player != null &&
            player.GetInstanceID() != _p1InstanceId)
        {
            Logger.LogInfo("[MP] P1 змінився після завантаження — скидаємо P2");
            ResetP2();
        }

        if (_hadPlayer2 && (player == null || !player.activeInHierarchy))
        {
            _p1MissingTimer += Time.deltaTime;
            if (_p1MissingTimer >= P1_MISSING_THRESHOLD)
            {
                Logger.LogInfo("[MP] P1 зник надовго — скидаємо P2");
                _p1MissingTimer = 0f;
                ResetP2();
            }
        }
        else
        {
            _p1MissingTimer = 0f;
        }

        // F7 — знайти гравця
        if (Input.GetKeyDown(KeyCode.F7))
        {
            var oldP2 = GameObject.Find("Player2_Clone");
            if (oldP2 != null && oldP2 != player2)
            {
                Destroy(oldP2);
                player2 = null;
                MultiplayerState.Player2 = null;
                Logger.LogInfo("[MP] Старий Player2_Clone знищено");
            }

            var allPlayers = FindObjectsOfType<GameObject>(true)
                .Where(obj => obj.name.Contains("Player"))
                .ToList();

            Logger.LogInfo($"Знайдено об'єктів з 'Player': {allPlayers.Count}");
            foreach (var p in allPlayers)
                Logger.LogInfo($"  -> {p.name} active={p.activeSelf}");

            player = allPlayers.FirstOrDefault(o => o.activeSelf && o.name == "Player(Clone)");
            if (player == null)
                player = allPlayers.FirstOrDefault(o => o.activeSelf);

            Logger.LogInfo(player != null
                ? $"PLAYER FOUND: {player.name}"
                : "Player not found!");
        }

        // F8 — показувати/приховувати позицію
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

        // F9 — заспавнити локального P2 (split-screen, тільки для тестів)
        if (Input.GetKeyDown(KeyCode.F9))
        {
            if (player == null)  { Logger.LogInfo("Спочатку натисни F7!"); return; }
            if (player2 != null) { Logger.LogInfo("Player2 вже існує!"); return; }
            StartCoroutine(SpawnPlayer2Delayed());
        }

        // F10 — діагностика
        if (Input.GetKeyDown(KeyCode.F10))
        {
            if (player2 == null) { Logger.LogInfo("Player2 не існує"); return; }

            Logger.LogInfo($"[DIAG] Player2 active={player2.activeSelf}, pos={player2.transform.position}");
            Logger.LogInfo($"[DIAG] Layer={player2.layer} ({LayerMask.LayerToName(player2.layer)})");
            Logger.LogInfo($"[DIAG] localScale={player2.transform.localScale}");

            Logger.LogInfo("=== УСІ MONOBEHAVIOUR НА PLAYER2 ===");
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
                : "  Rigidbody2D: не знайдено");
        }

        // F11 — створити лобі (хост)
        if (Input.GetKeyDown(KeyCode.F11))
        {
            SteamManager.Instance?.CreateLobby();
        }

        // F12 — статус з'єднання
        if (Input.GetKeyDown(KeyCode.F12))
        {
            Logger.LogInfo($"[STEAM] Role={SteamNetwork.Role}");
            Logger.LogInfo($"[STEAM] LobbyID={SteamNetwork.LobbyID}");
            Logger.LogInfo($"[STEAM] RemoteID={SteamNetwork.RemoteID}");
            Logger.LogInfo($"[STEAM] Connected={SteamNetwork.IsConnected}");
            Logger.LogInfo($"[STEAM] IsInGame={SteamNetwork.IsInGame}");
            Logger.LogInfo($"[STEAM] RemotePlayerSpawned={SteamNetwork.RemotePlayerSpawned}");
        }
    }

    // ── Спавн клону іншого гравця (мережевий) ────────────────────────────────
    // Викликається з SteamManager.OnPacketReceived(0x06) при першому пакеті позиції.
    // RemotePlayerSpawned виставляється тільки після успішного спавну — щоб при
    // невдачі (сцена ще не готова) наступний пакет 0x06 спробував ще раз.
    private bool _spawnCoroutineRunning = false;

    public void SpawnRemotePlayer(Vector3 startPos)
    {
        // Подвійний захист: і по об'єкту і по прапору корутини
        if (_remotePlayerGO != null || _spawnCoroutineRunning) return;
        _spawnCoroutineRunning = true;
        StartCoroutine(SpawnRemotePlayerCoroutine(startPos));
    }

    private IEnumerator SpawnRemotePlayerCoroutine(Vector3 startPos)
    {
        // ── Чекаємо поки локальний гравець з'явиться в сцені ─────────────────
        // Клієнт може отримати 0x06 раніше ніж сцена завантажиться.
        // Чекаємо до 30 секунд з перевіркою кожні 0.5с.
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
                Logger.LogInfo($"[REMOTE] Чекаємо локального гравця... ({waitTimeout:F0}с залишилось)");
                yield return new WaitForSeconds(0.5f);
                waitTimeout -= 0.5f;
            }
        }

        if (p1 == null)
        {
            Logger.LogWarning("[REMOTE] Локального гравця не знайдено за 30с — спавн скасовано");
            SteamNetwork.RemotePlayerSpawned = false;
            _spawnCoroutineRunning = false;
            yield break;
        }

        Logger.LogInfo($"[REMOTE] Локальний гравець знайдений: {p1.name}, спавнимо клон на {startPos}");

        var allGOs = FindObjectsOfType<GameObject>(true)
            .Where(o => o.name.Contains("Player"))
            .ToList();
        Logger.LogInfo($"[REMOTE] Всі Player об'єкти: {string.Join(", ", allGOs.Select(o => o.name + "(active=" + o.activeSelf + ")"))}");


        var p1Materials   = SnapshotMaterials(p1);
        var p1ChildScales = SnapshotScales(p1);

        // Інстанціюємо клон всередині НЕАКТИВНОГО контейнера. Поки об'єкт
        // неактивний у ієрархії, Unity не викликає Awake/OnEnable/Start.
        // Це критично: PlayerComponent.Awake() на копії гравця змушує гру
        // заспавнити ще одного повноцінного дубль-гравця, а WorldGameObject.Awake()
        // реєструє клон у GameAwakenerEngine. Знімаємо ці компоненти ДО Awake.
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
        Logger.LogInfo($"[REMOTE] Знято {strippedComps} геймплейних компонентів до Awake ✓");

        // Виймаємо з контейнера — клон стає активним, Awake спрацює тільки для
        // решти (візуальних) компонентів. PlayerComponent уже немає → дубль не спавниться.
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

        // RemotePlayerController — плавне переміщення до позиції з мережі
        var ctrl = _remotePlayerGO.AddComponent<RemotePlayerController>();
        ctrl.Init(startPos, Logger, p1.transform); // ← передаємо transform локального гравця для scale

        // ── КАМЕРА НЕ ЧІПАЄТЬСЯ ──────────────────────────────────────────────
        // Кожен гравець має свою камеру на своєму ПК.
        // RemotePlayer_Clone просто ходить по світу — камера слідкує тільки за локальним гравцем.

        SteamNetwork.RemotePlayerSpawned = true;
        _spawnCoroutineRunning = false;
        Logger.LogInfo("[REMOTE] Клон іншого гравця заспавнений ✓ (камера не змінена)");
    }

    // Миттєве оновлення тільки анімації — викликається з пакету 0x07
    // Надсилається одразу при зміні стану — без очікування 50мс таймера позиції
    public void ApplyRemoteAnimation(float angle, int state, float dirX, float dirY)
    {
        if (_remotePlayerGO == null) return;
        if (_cachedRemoteController == null)
            _cachedRemoteController = _remotePlayerGO.GetComponent<RemotePlayerController>();
        _cachedRemoteController?.ApplyAnimationOnly(angle, state, dirX, dirY);
    }

    // Оновлення позиції/анімації клону — викликається з SteamManager кожен пакет 0x06
    private RemotePlayerController _cachedRemoteController;

    public void UpdateRemotePlayer(Vector3 pos, float angle, int state, float dirX, float dirY)
    {
        if (_remotePlayerGO == null) return;
        // Кешуємо GetComponent — викликається 20 разів/сек
        if (_cachedRemoteController == null)
            _cachedRemoteController = _remotePlayerGO.GetComponent<RemotePlayerController>();
        _cachedRemoteController?.SetTargetData(pos, angle, state, dirX, dirY);
    }

    private void ResetP2()
    {
        if (player2 != null) Destroy(player2);
        player2 = null;
        player = null;
        MultiplayerState.Player2 = null;
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
        Logger.LogInfo("[MP] P2 скинуто. Натисни F7 → F9 щоб заспавнити знову");
    }

    // ── Корутина локального split-screen P2 (F9) ─────────────────────────────
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
        Logger.LogInfo("[P2] Scale відновлено після вимкнення PixelPerfect");

        SetupWorldObject(player2);
        EnableAllRenderers(player2);
        DisableFireAndParticles(player2);
        FixOverheadObj(player2);
        SetupCharacterComponent(player2);

        var ctrl = player2.AddComponent<P2DirectController>();
        ctrl.Init(Logger, player.transform);

        // Локальний split-screen — тут DualPlayerCamera доречна
        AttachDualCamera();

        _hadPlayer2 = true;
        Logger.LogInfo("[P2] Спавн завершено!");
    }

    // ── Знімок scale по іменах ────────────────────────────────────────────────
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
        Logger.LogInfo($"[P2] Scale знято: {result.Count} об'єктів");
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
        Logger.LogInfo("[P2] Scale відновлено ✓");
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
            if (charComp == null) { Logger.LogWarning("[P2] PlayerComponent не знайдено!"); return; }
            Logger.LogInfo($"[P2] charComp runtime type: {charComp.GetType().FullName}");

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
            Logger.LogInfo($"[P2] SetAnimationState: {(animMethod != null ? "✓" : "не знайдено")}");

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
        Logger.LogWarning($"[P2] Поле '{name}' не знайдено в ієрархії {type.Name}!");
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
                Logger.LogInfo($"[P2] {t.Name}.{name}() викликано ✓");
                return;
            }
            t = t.BaseType;
        }
        Logger.LogWarning($"[P2] Метод '{name}' не знайдено в ієрархії {type.Name}!");
    }

    private void CopyScaleRecursive(GameObject src, GameObject dst)
    {
        var srcTransforms = src.GetComponentsInChildren<Transform>(true);
        var dstTransforms = dst.GetComponentsInChildren<Transform>(true);
        int count = Mathf.Min(srcTransforms.Length, dstTransforms.Length);
        for (int i = 0; i < count; i++)
            dstTransforms[i].localScale = srcTransforms[i].localScale;
        Logger.LogInfo($"[P2] Scale скопійовано ({count} transforms)");
    }

    // ── Матеріали ─────────────────────────────────────────────────────────────
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
            Logger.LogInfo($"[P2] Матеріали: {renderers[i].gameObject.name}");
        }
    }

    // ── Фізика ────────────────────────────────────────────────────────────────
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
        var fireNames = new[] { "fire", "fire (1)", "Point light", "ground light",
            "ground light (small)", "garlic_cloud",
            "memories_fx", "water_fx", "fx_memory_cloud",
            "eating_memory_cloud", "eyes_memory_cloud",
            "dynamic shadow", "[dynamic shadow] #0", "[dynamic shadow] #1",
            "[dynamic shadow] #2", "[dynamic shadow] #3" };

        foreach (Transform t in obj.GetComponentsInChildren<Transform>(true))
        {
            if (fireNames.Contains(t.gameObject.name))
            {
                t.gameObject.SetActive(false);
                Logger.LogInfo($"[P2] Деактивовано: {t.gameObject.name}");
            }
        }

        foreach (var ps in obj.GetComponentsInChildren<ParticleSystem>(true))
        {
            ps.gameObject.SetActive(false);
            Logger.LogInfo($"[P2] ParticleSystem деактивовано: {ps.gameObject.name}");
        }
    }

    private void DisableGameplayBehaviours(GameObject obj)
    {
        var gameplayComponents = new[]
        {
            "Seeker", "CustomNetworkAnimatorSync", "ChunkedGameObject",
            "SimpleSmoothModifierXY", "AIPath", "AILerp", "FunnelModifier",
            "PixelPerfect",
            "DynamicLight", "GroundLight", "LightFlicker", "RandomCoordinate",
            "ObjectDynamicShadow", "ObjectDynamicShadowChild",
            // Вимикаємо компоненти управління гравцем — інакше гра рухає клон
            // тим самим інпутом що і локального гравця
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
                Logger.LogInfo($"[P2] Вимкнено: {typeName} ({b.gameObject.name})");
            }
        }

        foreach (var light in obj.GetComponentsInChildren<Light>(true))
        {
            light.enabled = false;
            Logger.LogInfo($"[P2] Light вимкнено: {light.gameObject.name}");
        }
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

        // player може бути null якщо клонуємо remote — використовуємо obj сам для шарів
        if (player != null)
        {
            CopySortingLayers(player, obj);
            obj.layer = player.layer;
        }
        Logger.LogInfo($"[P2] Layer: {LayerMask.LayerToName(obj.layer)}");
    }

    // Копіює шари рендерингу на мережевий клон.
    // WorldGameObject вже знищено в SpawnRemotePlayerCoroutine — налаштовувати нічого.
    private void SetupRemoteWorldObject(GameObject obj)
    {
        // Беремо шари від першого знайденого локального гравця
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
        Logger.LogInfo($"[P2] Sorting layers скопійовано ({count})");
    }

    private void EnableAllRenderers(GameObject obj)
    {
        foreach (var r in obj.GetComponentsInChildren<Renderer>(true))
        {
            r.enabled = true;
            r.gameObject.SetActive(true);
            if (r is SpriteRenderer sr && sr.color.a < 0.01f)
            {
                var c = sr.color; c.a = 1f; sr.color = c;
                Logger.LogInfo($"[P2] Alpha виправлено -> 1 на {r.gameObject.name}");
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
                Logger.LogInfo("[P2] overhead_obj: виправлено magenta -> white");
            }
            sr.enabled = false;
            break;
        }
    }

    private void EnableAnimations(GameObject obj)
    {
        foreach (var anim in obj.GetComponentsInChildren<Animator>(true))
            anim.enabled = true;
        foreach (var anim in obj.GetComponentsInChildren<SmartAnimationController>(true))
            anim.enabled = true;
    }

    // ── Камера для локального split-screen (F9) ───────────────────────────────
    private void AttachDualCamera()
    {
        var cam = Camera.main;
        if (cam == null) { Logger.LogInfo("[CAM] Камера не знайдена!"); return; }

        foreach (var b in cam.GetComponents<MonoBehaviour>())
        {
            string n = b.GetType().Name;
            if (n.Contains("ProCamera") || n.Contains("Follow") || n.Contains("Track"))
            {
                b.enabled = false;
                Logger.LogInfo($"[CAM] Вимкнено: {n}");
            }
        }

        if (cam.gameObject.GetComponent<DualPlayerCamera>() == null)
        {
            cam.gameObject.AddComponent<DualPlayerCamera>()
               .Init(player.transform, player2.transform, cam, Logger);
        }
    }

    // ── P2DirectController (локальний split-screen, клавіші HJKL) ────────────
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
            _logger.LogInfo($"[P2] Базовий scale зафіксовано: {_baseScale}");
            if (p1col != null && p2col != null)
            {
                p2col.size   = p1col.size;
                p2col.offset = p1col.offset;
                _logger.LogInfo("[P2] Колайдер скопійовано від P1 ✓");
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
                    _logger.LogInfo($"[P2] velocity_cached ініціалізовано: {_p1velocity_cached:F1}");
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
// RemotePlayerController — плавно переміщує клон до позиції з мережі
// Кожен ПК бачить клон ІНШОГО гравця. Своя камера кожного не чіпається.
// ─────────────────────────────────────────────────────────────────────────────
public class RemotePlayerController : MonoBehaviour
{
    private Vector3 _targetPosition;
    private Animator _animator;
    private ManualLogSource _logger;
    private bool _initialized = false;
    private const float INTERP_SPEED = 12f;
    // Поріг телепорту — якщо клон далі цього від цілі, телепортуємо одразу
    private const float TELEPORT_THRESHOLD = 100f;

    // Зберігаємо transform локального гравця для правильного scale клону
    private Transform _localPlayerTransform;
    // Прапор першого отримання позиції — телепортуємо одразу без Lerp
    private bool _firstPositionReceived = false;

    // Лічильник для throttled логів
    private int _logCounter = 0;

    public void Init(Vector3 startPos, ManualLogSource logger, Transform localPlayerTransform)
    {
        _targetPosition = startPos;
        transform.position = startPos;
        _logger = logger;
        _localPlayerTransform = localPlayerTransform;
        _animator = GetComponentInChildren<Animator>();
        _initialized = true;
        _logger.LogInfo($"[REMOTE] RemotePlayerController ініціалізовано ✓ | localPlayer={(_localPlayerTransform != null ? _localPlayerTransform.name : "null")}");
    }

    public void SetTargetData(Vector3 pos, float angle, int state, float dirX, float dirY)
    {
        _targetPosition = pos;

        // Перша валідна позиція — телепортуємо клон одразу замість Lerp з (0,0,0)
        if (!_firstPositionReceived)
        {
            transform.position = pos;
            _firstPositionReceived = true;
            _logger?.LogInfo($"[REMOTE] Перша позиція — телепорт на {pos}");
        }

        // Логуємо позицію кожні 120 пакетів (~6 секунд при 20 пакетів/сек)
        _logCounter++;
        if (_logCounter % 120 == 0)
            _logger?.LogInfo($"[REMOTE] Позиція хоста: {pos}");

        ApplyAnimationOnly(angle, state, dirX, dirY);
    }

    // Окремий метод для миттєвого оновлення анімації з пакету 0x07
    // Викликається без зміни позиції — тільки стан аніматора
    public void ApplyAnimationOnly(float angle, int state, float dirX, float dirY)
    {
        if (_animator == null) return;
        _animator.SetFloat("direction_angle", angle);
        _animator.SetFloat("direction_x", dirX);
        _animator.SetFloat("direction_y", dirY);
        _animator.SetInteger("global_state", state);
    }

    // Кеш scale — lossyScale обчислюється рекурсивно по ієрархії, дорого кожен кадр
    private Vector3 _cachedScale = Vector3.one;
    private int     _scaleUpdateFrame = -1;
    private const int SCALE_UPDATE_EVERY = 10; // оновлюємо раз на 10 кадрів

    void LateUpdate()
    {
        if (!_initialized) return;

        // lossyScale — рекурсивний обхід ієрархії, оновлюємо рідше
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

        // Якщо далеко від цілі — телепортуємо одразу (зміна локації, перший фрейм)
        float distSq = (transform.position - _targetPosition).sqrMagnitude;
        if (distSq > TELEPORT_THRESHOLD * TELEPORT_THRESHOLD)
            transform.position = _targetPosition;
        else
            transform.position = Vector3.Lerp(
                transform.position, _targetPosition, Time.deltaTime * INTERP_SPEED);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DualPlayerCamera — тільки для локального split-screen (F9)
// В мережевому режимі НЕ використовується
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
        logger.LogInfo($"[CAM] DualPlayerCamera активована. size={originalSize}");
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
            logger.LogInfo($"[CAM] Телепорт P2 до P1, dist={dist:F0}");
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
    public static bool IsConnected => Role != NetworkRole.None && RemoteID != 0;
    public static bool IsInGame = false;
    public static bool IsLoadingAsClient = false;
    public static bool RemotePlayerSpawned = false;
    // Час коли IsInGame став true — щоб заблокувати spurious Open() одразу після завантаження
    public static float IsInGameSince = -1f;
    // Скільки секунд після IsInGame блокуємо відкриття меню
    public const float MENU_BLOCK_DURATION = 15f;
}

// ─────────────────────────────────────────────────────────────────────────────
// Синхронізація ігрового часу між хостом і клієнтом.
// День: GameSave.day (int). Час доби: EnvironmentEngine._cur_time (float).
// Модель "максимум перемагає": приймач переймає час лише якщо віддалений
// попереду — так стрибок дня від сну будь-кого пошириться на іншого, а звичайний
// дрейф годинників вирівнюється до лідера.
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
        // EnvironmentEngine — компонент сцени; кешуємо, перешукуємо якщо знищено
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
    private const float SEND_INTERVAL = 0.05f; // 20 разів/сек

    private float _timeSyncTimer = 0f;
    private const float TIME_SYNC_INTERVAL = 2f; // синхронізація часу — раз на 2с

    // ── Кешовані reflection-об'єкти (ініціалізуються один раз) ───────────────
    // Замість того щоб кожен кадр шукати типи/методи через reflection
    private MethodInfo  _cachedRunCallbacks;      // SteamAPI.RunCallbacks
    private MethodInfo  _cachedSendP2PPacket;     // SteamNetworking.SendP2PPacket
    private MethodInfo  _cachedIsP2PAvailable;    // SteamNetworking.IsP2PPacketAvailable
    private MethodInfo  _cachedReadP2PPacket;     // SteamNetworking.ReadP2PPacket
    private ConstructorInfo _cachedSteamIdCtor;   // CSteamID(ulong)
    private object      _cachedSendReliable;      // EP2PSend.k_EP2PSendReliable
    private object      _cachedRemoteSteamId;     // CSteamID для RemoteID (оновлюється при зміні)
    private ulong       _cachedRemoteIdValue;     // останнє значення для якого створено CSteamID

    // Кешований локальний гравець для SendMyPosition — щоб не робити FindObjectsOfType кожні 50мс
    private GameObject  _cachedLocalPlayer;
    private Animator    _cachedLocalAnimator;

    public void Init(ManualLogSource logger)
    {
        _logger = logger;
        Instance = this;
        StartCoroutine(InitDelayed());
    }

    private void OnLobbyCreated(object param)
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
                _logger.LogInfo($"[STEAM] ✓ Ти ХОСТ! LobbyID={SteamNetwork.LobbyID}");
            }
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[STEAM] OnLobbyCreated помилка: {e.Message}");
        }
    }

    private void OnLobbyEntered(object param)
    {
        try
        {
            var flags   = BindingFlags.Public | BindingFlags.Instance;
            var lobbyId = param.GetType().GetField("m_ulSteamIDLobby", flags)?.GetValue(param);

            SteamNetwork.LobbyID = System.Convert.ToUInt64(lobbyId ?? 0UL);
            if (SteamNetwork.Role != NetworkRole.Host)
            {
                SteamNetwork.Role = NetworkRole.Client;
                _logger.LogInfo($"[STEAM] ✓ Ти КЛІЄНТ! LobbyID={SteamNetwork.LobbyID}");
            }
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[STEAM] OnLobbyEntered помилка: {e.Message}");
        }
    }

    private object _lobbyEnteredCallback;
    private object _lobbyJoinRequestedCallback;

    private IEnumerator InitDelayed()
    {
        _logger.LogInfo("[STEAM] Чекаємо ініціалізації Steam...");

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
                _logger.LogInfo($"[STEAM] Ім'я: {name}");

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

                    _logger.LogInfo($"[STEAM] LobbyJoinRequested колбек: {(_lobbyJoinRequestedCallback != null ? "✓" : "✗")}");
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

                    var cb = cbConcrete
                        .GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new object[] { del });

                    _logger.LogInfo($"[STEAM] LobbyChatUpdate колбек: {(cb != null ? "✓" : "✗")}");
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

                    var cb = cbConcrete
                        .GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new object[] { del });

                    _logger.LogInfo($"[STEAM] P2PSessionRequest колбек: {(cb != null ? "✓" : "✗")}");
                }

                _initialized = true;
                CheckLaunchLobbyJoin();
                InitCachedReflection();
                _logger.LogInfo("[STEAM] Готово! F11 = створити лобі");

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

                    _logger.LogInfo($"[STEAM] LobbyEntered колбек: {(_lobbyEnteredCallback != null ? "✓" : "✗")}");
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

                    _logger.LogInfo($"[STEAM] LobbyCreated колбек: {(_lobbyCreatedCallback != null ? "✓" : "✗")}");
                }

                yield break;
            }
            catch (System.Exception e)
            {
                var inner = e.InnerException ?? e;
                _logger.LogInfo($"[STEAM] Ще не готово ({inner.Message}), чекаємо...");
            }
        }
        _logger.LogError("[STEAM] Steam не ініціалізувався за 60 секунд!");
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

            uint state = System.Convert.ToUInt32(stateChange ?? 0);
            if (state == 1 && SteamNetwork.Role == NetworkRole.Host)
            {
                var rawId = userChanged?.GetType()
                    .GetField("m_SteamID",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(userChanged);
                SteamNetwork.RemoteID = System.Convert.ToUInt64(rawId ?? 0UL);
                _logger.LogInfo($"[STEAM] Клієнт приєднався! RemoteID={SteamNetwork.RemoteID}");

                _steamNetworking
                    ?.GetMethod("AcceptP2PSessionWithUser", BindingFlags.Public | BindingFlags.Static)
                    ?.Invoke(null, new[] { userChanged });
                _logger.LogInfo("[P2P] AcceptP2PSessionWithUser для клієнта ✓");
            }
        }
        catch (System.Exception e) { _logger.LogError($"[STEAM] LobbyChatUpdate помилка: {e.Message}"); }
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

            // CSteamID конструктор і EP2PSend.Reliable
            var steamIdType      = _steamNetworking?.Assembly.GetType("Steamworks.CSteamID");
            _cachedSteamIdCtor   = steamIdType?.GetConstructor(new[] { typeof(ulong) });
            _readSteamIdObj      = _cachedSteamIdCtor?.Invoke(new object[] { 0UL });
            _steamIdField        = steamIdType?.GetField("m_SteamID",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            var ep2pSendType     = _steamNetworking?.Assembly.GetType("Steamworks.EP2PSend");
            _cachedSendReliable  = System.Enum.ToObject(ep2pSendType, 2);

            _logger.LogInfo($"[CACHE] RunCallbacks: {(_cachedRunCallbacks != null ? "✓" : "✗")}");
            _logger.LogInfo($"[CACHE] SendP2PPacket: {(_cachedSendP2PPacket != null ? "✓" : "✗")}");
            _logger.LogInfo($"[CACHE] IsP2PAvailable: {(_cachedIsP2PAvailable != null ? "✓" : "✗")}");
            _logger.LogInfo($"[CACHE] ReadP2PPacket: {(_cachedReadP2PPacket != null ? "✓" : "✗")}");
            _logger.LogInfo($"[CACHE] SteamIdCtor: {(_cachedSteamIdCtor != null ? "✓" : "✗")}");
            _logger.LogInfo("[CACHE] Reflection кеш ініціалізовано ✓");
        }
        catch (Exception e)
        {
            _logger.LogError($"[CACHE] InitCachedReflection помилка: {e.Message}");
        }
    }

    public void CreateLobby()
    {
        if (!_initialized) { _logger.LogWarning("[STEAM] Ще не ініціалізовано!"); return; }

        if (SteamNetwork.Role == NetworkRole.Host && SteamNetwork.LobbyID != 0)
        {
            _logger.LogInfo("[STEAM] Лобі вже створено! Відкриваємо overlay...");
            var steamIdType = _steamMatchmaking?.Assembly.GetType("Steamworks.CSteamID");
            var steamIdObj  = System.Activator.CreateInstance(steamIdType, SteamNetwork.LobbyID);
            _steamFriends?.GetMethod("ActivateGameOverlayInviteDialog",
                    BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new[] { steamIdObj });
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

            _logger.LogInfo($"[STEAM] CreateLobby викликано! handle={handle}");
        }
        catch (System.Exception e)
        {
            SteamNetwork.Role = NetworkRole.None;
            _logger.LogError($"[STEAM] CreateLobby помилка: {e.Message}");
        }
    }

    public void SendPacket(ulong targetSteamId, byte[] data, int channel = 0)
    {
        if (_cachedSendP2PPacket == null || _cachedSteamIdCtor == null) return;
        try
        {
            // Переробляємо CSteamID тільки якщо RemoteID змінився — інакше беремо кешований
            if (_cachedRemoteSteamId == null || _cachedRemoteIdValue != targetSteamId)
            {
                _cachedRemoteSteamId = _cachedSteamIdCtor.Invoke(new object[] { targetSteamId });
                _cachedRemoteIdValue = targetSteamId;
            }

            _cachedSendP2PPacket.Invoke(null,
                new object[] { _cachedRemoteSteamId, data, (uint)data.Length, _cachedSendReliable, channel });

            // Логуємо тільки службові пакети (не позицію, анімацію, час)
            if (data.Length > 0 && data[0] != 0x06 && data[0] != 0x07 && data[0] != 0x08)
                _logger.LogInfo($"[P2P] SendPacket to {targetSteamId}: type=0x{data[0]:X2} dataLen={data.Length}");
        }
        catch (System.Exception e) { _logger.LogError($"[P2P] Send помилка: {e.Message}"); }
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
            _logger.LogInfo($"[P2P] SessionRequest від: {SteamNetwork.RemoteID}");

            _steamNetworking
                ?.GetMethod("AcceptP2PSessionWithUser", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new[] { steamId });
            _logger.LogInfo("[P2P] AcceptP2PSessionWithUser ✓");
        }
        catch (System.Exception e) { _logger.LogError($"[P2P] SessionRequest помилка: {e.Message}"); }
    }

    // Повторно використовувані об'єкти для ReadPacket — без алокацій кожен кадр
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

            // Зберігаємо RemoteID якщо ще не встановлено
            if (SteamNetwork.RemoteID == 0 && _steamIdField != null)
            {
                var rawId = _steamIdField.GetValue(readArgs[3]);
                SteamNetwork.RemoteID = System.Convert.ToUInt64(rawId ?? 0UL);
                if (SteamNetwork.RemoteID != 0)
                    _logger.LogInfo($"[P2P] RemoteID встановлено: {SteamNetwork.RemoteID}");
            }

            return buffer;
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[P2P] Read помилка: {e.Message}");
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

            // Steam іноді стріляє колбек двічі підряд — ігноруємо дублікат
            float now = UnityEngine.Time.time;
            if (id == _lastJoinedLobbyId && (now - _lastJoinTime) < 5f)
            {
                _logger.LogInfo("[STEAM] JoinRequested дублікат — ігноруємо ✓");
                return;
            }
            _lastJoinedLobbyId = id;
            _lastJoinTime = now;

            StartCoroutine(JoinLobbyDelayed(id));
        }
        catch (System.Exception e) { _logger.LogError($"[STEAM] JoinRequested помилка: {e.Message}"); }
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
                    _logger.LogInfo($"[STEAM] Знайдено +connect_lobby: {lobbyId}");
                    StartCoroutine(JoinLobbyDelayed(lobbyId));
                    return;
                }
            }
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[STEAM] CheckLaunchLobbyJoin помилка: {e.Message}");
        }
    }

    private IEnumerator JoinLobbyDelayed(ulong lobbyId)
    {
        yield return new WaitForSeconds(1f);

        try
        {
            var steamIdType = _steamMatchmaking?.Assembly.GetType("Steamworks.CSteamID");
            var steamIdObj  = System.Activator.CreateInstance(steamIdType, lobbyId);

            _steamMatchmaking
                ?.GetMethod("JoinLobby", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new[] { steamIdObj });

            _logger.LogInfo($"[STEAM] JoinLobby викликано! id={lobbyId}");
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[STEAM] JoinLobby помилка: {e.Message}");
        }
    }

    private int _lastLobbyMemberCount = 0;

    void Update()
    {
        if (!_initialized) return;

        // ── Кешований RunCallbacks — без пошуку assembly кожен кадр ──────────
        _cachedRunCallbacks?.Invoke(null, null);

        if (SteamNetwork.Role == NetworkRole.Host && SteamNetwork.LobbyID != 0)
            PollLobbyMembers();

        // Читаємо всі доступні пакети за один Update
        int maxPacketsPerFrame = 10;
        for (int i = 0; i < maxPacketsPerFrame; i++)
        {
            var packet = ReadPacket();
            if (packet == null || packet.Length == 0) break;
            // Логуємо тільки службові пакети (не позицію, анімацію, час)
            if (packet[0] != 0x06 && packet[0] != 0x07 && packet[0] != 0x08)
                _logger.LogInfo($"[P2P] Отримано пакет {packet.Length} байт, type={packet[0]}");
            OnPacketReceived(packet);
        }

        // Надсилаємо свою позицію якщо з'єднані і в грі
        if (SteamNetwork.IsConnected && SteamNetwork.IsInGame && SteamNetwork.RemoteID != 0)
        {
            _sendTimer += Time.deltaTime;
            if (_sendTimer >= SEND_INTERVAL)
            {
                _sendTimer = 0f;
                SendMyPosition();
            }

            _timeSyncTimer += Time.deltaTime;
            if (_timeSyncTimer >= TIME_SYNC_INTERVAL)
            {
                _timeSyncTimer = 0f;
                SendTimeSync();
            }
        }
    }

    // Пакет 0x08: type(1) + day(4 int) + cur_time(4 float) = 9 байт
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
            _logger.LogInfo($"[STEAM] Членів лобі: {count}");
            _lastLobbyMemberCount = count;

            if (count < 2) return;

            var clientSteamId = _steamMatchmaking
                ?.GetMethod("GetLobbyMemberByIndex", flags)
                ?.Invoke(null, new object[] { lobbyIdObj, 1 });

            var rawId = clientSteamId?.GetType()
                .GetField("m_SteamID",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(clientSteamId);

            SteamNetwork.RemoteID = System.Convert.ToUInt64(rawId ?? 0UL);
            _logger.LogInfo($"[STEAM] Клієнт знайдений! RemoteID={SteamNetwork.RemoteID}");

            _steamNetworking
                ?.GetMethod("AcceptP2PSessionWithUser", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new[] { clientSteamId });
            _logger.LogInfo("[P2P] AcceptP2PSessionWithUser ✓");

            // Хост вже в грі якщо RemoteID щойно встановлено — вмикаємо позиційний синк.
            // Не можна покладатись на OnGameStartedPlayingPatch бо він спрацьовує
            // ДО того як роль Host встановлена (F11 натискається після завантаження гри).
            if (SteamNetwork.Role == NetworkRole.Host && !SteamNetwork.IsInGame)
            {
                SteamNetwork.IsInGame = true;
                SteamNetwork.IsInGameSince = UnityEngine.Time.time;
                _logger.LogInfo("[STEAM] Host IsInGame=true — позиційний синк активовано ✓");
            }
        }
        catch (System.Exception e) { _logger.LogError($"[P2P] PollLobby помилка: {e.Message}"); }
    }

    private byte[] _saveBuffer;
    private int    _saveBufferExpectedSize;
    private int    _saveChunksReceived;
    private Vector3 _hostSpawnPosition = Vector3.zero;

    private void OnPacketReceived(byte[] data)
    {
        byte type = data[0];

        // Сейв хоста завантажується рівно один раз при підключенні. Якщо клієнт
        // уже в грі — ігноруємо повторний трансфер сейву (0x02-0x04). Інакше
        // LoadSaveFromHost перезавантажить сцену посеред гри, і таймери гри
        // (GJTimer/FlowCanvas) спрацюють у знищені об'єкти → FATAL NullReferenceException.
        if ((type == 0x02 || type == 0x03 || type == 0x04) && SteamNetwork.IsInGame)
        {
            if (type == 0x02)
                _logger.LogInfo("[P2P] Повторний трансфер сейву проігноровано — клієнт уже в грі ✓");
            return;
        }

        if (type == 0x01)
        {
            if (SteamNetwork.Role == NetworkRole.Host)
                StartCoroutine(SendSaveToClient());
        }
        else if (type == 0x02)
        {
            _saveBufferExpectedSize = System.BitConverter.ToInt32(data, 1);
            _saveBuffer = new byte[_saveBufferExpectedSize];
            _saveChunksReceived = 0;
            _logger.LogInfo($"[P2P] Очікуємо сейв {_saveBufferExpectedSize} байт");
        }
        else if (type == 0x03)
        {
            int chunkIndex = System.BitConverter.ToInt32(data, 1);
            int offset     = chunkIndex * 500000;
            int size       = data.Length - 5;
            System.Array.Copy(data, 5, _saveBuffer, offset, size);
            _saveChunksReceived++;
            _logger.LogInfo($"[P2P] Отримано чанк {chunkIndex} ({size} байт)");
        }
        else if (type == 0x04)
        {
            _logger.LogInfo($"[P2P] Передача завершена! {_saveChunksReceived} чанків, {_saveBuffer?.Length} байт");
            if (_saveBuffer != null)
                StartCoroutine(LoadSaveFromHost(_saveBuffer));
        }
        else if (type == 0x05)
        {
            if (data.Length < 13) { _logger.LogWarning($"[SYNC] 0x05 замалий: {data.Length}б"); return; }
            float px = System.BitConverter.ToSingle(data, 1);
            float py = System.BitConverter.ToSingle(data, 5);
            float pz = System.BitConverter.ToSingle(data, 9);
            _hostSpawnPosition = new Vector3(px, py, pz);
            _logger.LogInfo($"[CLIENT] Позиція хоста отримана: {_hostSpawnPosition}");
        }
        else if (type == 0x07)
        {
            // ── Миттєва подія анімації — без позиції ─────────────────────────
            // Надсилається одразу при зміні global_state або direction
            if (data.Length < 14)
            {
                _logger.LogWarning($"[ANIM] 0x07 замалий: {data.Length}б");
                return;
            }
            // Розбір 0x07: type(1) + angle(4) + state(4) + dirX(4) + dirY(4) = 17 байт
            float a7  = BitConverter.ToSingle(data, 1);   // bytes 1-4
            int   st7 = BitConverter.ToInt32 (data, 5);   // bytes 5-8
            float dx7 = BitConverter.ToSingle(data, 9);   // bytes 9-12
            float dy7 = BitConverter.ToSingle(data, 13);  // bytes 13-16

            // Застосовуємо тільки анімацію — позицію не чіпаємо
            Multiplayer.Instance?.ApplyRemoteAnimation(a7, st7, dx7, dy7);
        }
        else if (type == 0x06)
        {
            // ── Фаза 3: позиція іншого гравця ────────────────────────────────────
            // Надсилається 20 разів/сек. Формат: pos(xyz) + angle + state + dirX + dirY = 26 байт
            if (data.Length < 26)
            {
                _logger.LogWarning($"[SYNC] 0x06 замалий: {data.Length}б, пропускаємо");
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

            // Пропускаємо нульові позиції — хост ще не завантажив гравця
            if (remotePos.sqrMagnitude < 1f)
            {
                _logger.LogInfo("[SYNC] Пропускаємо нульову позицію хоста");
                return;
            }

            // Запускаємо спавн при першому валідному пакеті.
            // RemotePlayerSpawned = true виставляємо ТУТ щоб не запускати десятки корутин.
            // Корутина скидає його в false якщо сцена ще не готова.
            if (!SteamNetwork.RemotePlayerSpawned && Multiplayer.Instance != null)
            {
                SteamNetwork.RemotePlayerSpawned = true;
                _logger.LogInfo($"[SYNC] Спавнимо клон на {remotePos}");
                Multiplayer.Instance.SpawnRemotePlayer(remotePos);
            }

            // Оновлюємо позицію і анімацію клону
            Multiplayer.Instance?.UpdateRemotePlayer(remotePos, angle, state, dirX, dirY);
        }
        else if (type == 0x08)
        {
            // ── Синхронізація ігрового часу ──────────────────────────────────
            if (data.Length < 9) return;
            int   remoteDay  = BitConverter.ToInt32 (data, 1);
            float remoteTime = BitConverter.ToSingle(data, 5);

            if (!GameTimeSync.TryRead(out int localDay, out float localTime)) return;

            // Переймаємо час лише якщо віддалений попереду (день головніший).
            bool remoteAhead = remoteDay > localDay
                            || (remoteDay == localDay && remoteTime > localTime);
            if (remoteAhead)
            {
                GameTimeSync.Apply(remoteDay, remoteTime);
                if (remoteDay != localDay)
                    _logger.LogInfo($"[TIME] Синхронізовано: день {localDay}→{remoteDay}, час {remoteTime:F2}");
            }
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

            _logger.LogInfo($"[P2P] Розмір сейву: {binary.Length} байт");

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
                    _logger.LogInfo($"[P2P] Позиція хоста надіслана клієнту: {pos}");
                }
            }
            catch (System.Exception e) { _logger.LogWarning($"[P2P] Не вдалось надіслати позицію: {e.Message}"); }

            var sizePacket = new byte[5];
            sizePacket[0] = 0x02;
            System.BitConverter.GetBytes(binary.Length).CopyTo(sizePacket, 1);
            SendPacket(SteamNetwork.RemoteID, sizePacket);
            _logger.LogInfo("[P2P] Size packet надіслано");
        }
        catch (System.Exception e) { _logger.LogError($"[P2P] SendSave помилка: {e.Message}"); yield break; }

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
            _logger.LogInfo($"[P2P] Чанк {i+1}/{totalChunks} надіслано ({size} байт)");

            yield return new WaitForSeconds(0.05f);
        }

        SendPacket(SteamNetwork.RemoteID, new byte[] { 0x04 });
        _logger.LogInfo("[P2P] Всі чанки надіслано ✓");
    }

    // ── Фаза 3: надсилаємо свою позицію іншому гравцю ────────────────────────
    // Пакет 0x06: type(1) + x(4) + y(4) + z(4) + angle(4) + state(1) + dirX(4) + dirY(4) = 26 байт
    // Буфер виділяємо один раз — без алокацій кожні 50мс
    private readonly byte[] _posPacket  = new byte[26];
    private readonly byte[] _animPacket = new byte[17]; // 0x07(1) + angle(4) + state(4) + dirX(4) + dirY(4) = 17 байт
    private float _localPlayerRefreshTimer = 0f;
    private const float LOCAL_PLAYER_REFRESH_INTERVAL = 2f;

    // Попередні значення анімації — надсилаємо 0x07 тільки при зміні
    private float _lastAngle = float.MinValue;
    private int   _lastState = int.MinValue;
    private float _lastDirX  = float.MinValue;
    private float _lastDirY  = float.MinValue;

    private void SendMyPosition()
    {
        try
        {
            // Оновлюємо кеш локального гравця раз на 2 секунди
            // (замість FindObjectsOfType кожні 50мс)
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
            }
            if (_cachedLocalPlayer == null) return;

            float angle = _cachedLocalAnimator?.GetFloat("direction_angle") ?? 0f;
            float dirX  = _cachedLocalAnimator?.GetFloat("direction_x")    ?? 0f;
            float dirY  = _cachedLocalAnimator?.GetFloat("direction_y")    ?? 0f;
            int   state = _cachedLocalAnimator?.GetInteger("global_state") ?? 0;
            var   pos   = _cachedLocalPlayer.transform.position;

            // ── 0x07: подія анімації — надсилаємо ОДРАЗУ при будь-якій зміні ──
            // Не чекаємо 50мс таймера — інакше швидкі анімації (рубка, копання)
            // не встигають дійти до іншого гравця
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

                // Пакет 0x07: type(1) + angle(4) + state(4) + dirX(4) + dirY(4) = 17 байт
                // Без позиції — тільки стан анімації, надсилається миттєво
                _animPacket[0] = 0x07;
                BitConverter.GetBytes(angle).CopyTo(_animPacket, 1);  // bytes 1-4
                BitConverter.GetBytes(state).CopyTo(_animPacket, 5);  // bytes 5-8
                BitConverter.GetBytes(dirX).CopyTo(_animPacket, 9);   // bytes 9-12
                BitConverter.GetBytes(dirY).CopyTo(_animPacket, 13);  // bytes 13-16
                SendPacket(SteamNetwork.RemoteID, _animPacket);
            }

            // ── 0x06: позиція — надсилається по таймеру 20 разів/сек ────────
            _posPacket[0] = 0x06;
            BitConverter.GetBytes(pos.x).CopyTo(_posPacket, 1);
            BitConverter.GetBytes(pos.y).CopyTo(_posPacket, 5);
            BitConverter.GetBytes(pos.z).CopyTo(_posPacket, 9);
            BitConverter.GetBytes(angle).CopyTo(_posPacket, 13);
            _posPacket[17] = (byte)(state + 128);
            BitConverter.GetBytes(dirX).CopyTo(_posPacket, 18);
            BitConverter.GetBytes(dirY).CopyTo(_posPacket, 22);

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
        _logger.LogInfo("[CLIENT] ClientTutorialWatcher запущено");

        float timer = 30f;
        while (timer > 0f)
        {
            yield return new WaitForSeconds(1f);
            timer -= 1f;
            ForceSkipTutorialParams();
        }

        _logger.LogInfo("[CLIENT] ClientTutorialWatcher завершено");
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

            logger?.LogInfo("[TUT] ForceSkip: in_tutorial=0 встановлено ✓");
        }
        catch (Exception e) { logger?.LogWarning($"[TUT] ForceSkip виняток: {e.Message}"); }
    }

    public static bool _unlockAlreadyDone = false;

    private IEnumerator UnlockPlayerAfterLoad()
    {
        // Захист від подвійного запуску — в логах видно що корутина
        // запускається двічі через два виклики StartPlayingGame
        if (_unlockAlreadyDone)
        {
            _logger.LogInfo("[UNLOCK] Вже виконано — пропускаємо дублікат");
            yield break;
        }
        _unlockAlreadyDone = true;

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
                if (me == null) { _logger.LogInfo("[UNLOCK] me=null, чекаємо..."); continue; }

                var save = mainGameType.GetField("save", iFlags)?.GetValue(me);
                if (save == null) { _logger.LogInfo("[UNLOCK] save=null, чекаємо..."); continue; }

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
                    _logger.LogInfo($"[UNLOCK] Перевірка: in_tutorial={inTut}, gone_out={goneOut}");
                }
                else
                {
                    _logger.LogWarning("[UNLOCK] SetParam не знайдено!");
                    continue;
                }

                foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (mb.GetType().Name == "PlayerComponent")
                    {
                        Traverse.Create(mb).Field("_control_enabled").SetValue(true);
                        _logger.LogInfo("[UNLOCK] PlayerComponent розблоковано ✓");

                        if (_hostSpawnPosition != Vector3.zero)
                        {
                            mb.transform.position = _hostSpawnPosition;
                            _logger.LogInfo($"[UNLOCK] Телепорт до позиції хоста: {_hostSpawnPosition} ✓");
                        }
                        break;
                    }
                }

                _logger.LogInfo("[UNLOCK] ✓ Завершено успішно!");
                SteamNetwork.IsLoadingAsClient = false;

                // ── Фаза 3: клієнт починає надсилати свою позицію ────────────
                SteamNetwork.IsInGame = true;
                SteamNetwork.IsInGameSince = UnityEngine.Time.time;
                _logger.LogInfo("[UNLOCK] IsInGame=true — позиційний синк активовано ✓");

                yield break;
            }
            catch (Exception e)
            {
                _logger.LogWarning($"[UNLOCK] спроба {attempt}: {e.Message}");
            }
        }

        _logger.LogError("[UNLOCK] Timeout! Гравець може бути заблокований.");
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
                    _logger.LogInfo("[CLIENT] MainMenuGUI прихований ✓");
                }

                if (t == "TitleScreen" && mb.gameObject.activeSelf)
                {
                    mb.gameObject.SetActive(false);
                    _logger.LogInfo("[CLIENT] TitleScreen прихований ✓");
                }
            }

            var player = Object.FindObjectsOfType<MonoBehaviour>()
                .FirstOrDefault(mb => mb.GetType().Name == "PlayerComponent"
                                      && mb.gameObject.activeInHierarchy);
            if (player != null)
            {
                _logger.LogInfo("[CLIENT] ✓ Гравець у грі, меню приховано назавжди");
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

        _logger.LogInfo($"[CLIENT] Завантажуємо сейв від хоста ({binary.Length} байт)");

        var savePath = System.IO.Path.Combine(Application.persistentDataPath, "3.dat");
        System.IO.File.WriteAllBytes(savePath, binary);
        _logger.LogInfo($"[CLIENT] Сейв записано: {savePath} ✓");

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
                _logger.LogInfo("[CLIENT] Туторіал скіпнуто в сейві ✓");
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
                    _logger.LogWarning("[CLIENT] player_position поле не знайдено в GameSave!");
                }
            }
            else
            {
                _logger.LogWarning("[CLIENT] _hostSpawnPosition не отримано від хоста!");
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning($"[CLIENT] Туторіал скіп: {e.Message}");
        }

        try
        {
            var modBinary = (byte[])newSave.GetType()
                .GetMethod("ToBinary", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(newSave, null);

            if (modBinary != null)
            {
                System.IO.File.WriteAllBytes(savePath, modBinary);
                _logger.LogInfo($"[CLIENT] 3.dat перезаписано ({modBinary.Length} байт) ✓");
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning($"[CLIENT] Re-serialize не вдалось: {e.Message}");
        }

        yield return null;
        yield return null;

        var saveSlotDataType = AccessTools.TypeByName("SaveSlotData");
        var slotData = System.Activator.CreateInstance(saveSlotDataType);
        saveSlotDataType.GetField("filename_no_extension", flags)?.SetValue(slotData, "3");
        saveSlotDataType.GetField("linked_save", flags)?.SetValue(slotData, newSave);

        foreach (var f in saveSlotDataType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            _logger.LogInfo($"[CLIENT] SaveSlotData.{f.Name} = {f.GetValue(slotData)}");

        _logger.LogInfo("[CLIENT] SaveSlotData створено ✓");

        var saveSlotsType     = AccessTools.TypeByName("SaveSlotsMenuGUI");
        var allObjects        = Resources.FindObjectsOfTypeAll(saveSlotsType);
        var saveSlotsInstance = allObjects.Length > 0 ? allObjects[0] : null;
        if (saveSlotsInstance == null) { _logger.LogError("[CLIENT] SaveSlotsMenuGUI не знайдено!"); yield break; }
        _logger.LogInfo("[CLIENT] SaveSlotsMenuGUI знайдено ✓");

        var slotDatasField = saveSlotsType.GetField("_slot_datas",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var slotDatasList = slotDatasField?.GetValue(saveSlotsInstance);
        slotDatasList?.GetType().GetMethod("Clear")?.Invoke(slotDatasList, null);
        slotDatasList?.GetType().GetMethod("Add")?.Invoke(slotDatasList, new[] { slotData });
        _logger.LogInfo("[CLIENT] _slot_datas встановлено ✓");

        var onSelectMethod = saveSlotsType.GetMethod("OnSelectSlotPressed",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (onSelectMethod != null)
        {
            onSelectMethod.Invoke(saveSlotsInstance, new object[] { slotData });
            _logger.LogInfo("[CLIENT] OnSelectSlotPressed викликано ✓");
        }

        var saveSlotsGO = (saveSlotsInstance as UnityEngine.Component)?.gameObject;
        if (saveSlotsGO != null) saveSlotsGO.SetActive(true);

        yield return null;

        saveSlotsType.GetMethod("PrepareScene",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.Invoke(saveSlotsInstance, null);
        _logger.LogInfo("[CLIENT] PrepareScene викликано ✓");

        yield return new WaitForSeconds(0.3f);

        saveSlotsType.GetMethod("StartPlayingGame",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, System.Type.EmptyTypes, null)
            ?.Invoke(saveSlotsInstance, null);
        _logger.LogInfo("[CLIENT] StartPlayingGame викликано ✓");

        StartCoroutine(ClientTutorialWatcher());

        yield return new WaitForSeconds(1f);

        StartCoroutine(HideMenuUntilInGame());
        StartCoroutine(UnlockPlayerAfterLoad());
        _loadingInProgress = false;
    }

    private object _lobbyCreatedCallback;

    private void OnLobbyCreatedDispatch(object result)
    {
        try
        {
            var resultType = result.GetType();
            var flags = BindingFlags.Public | BindingFlags.Instance;

            var eResult = resultType.GetField("m_eResult", flags)?.GetValue(result);
            var lobbyId = resultType.GetField("m_ulSteamIDLobby", flags)?.GetValue(result);

            _logger.LogInfo($"[STEAM] OnLobbyCreated! result={eResult}, lobbyID={lobbyId}");

            if (eResult?.ToString() == "k_EResultOK")
            {
                SteamNetwork.LobbyID = System.Convert.ToUInt64(lobbyId);
                SteamNetwork.Role = NetworkRole.Host;
                _logger.LogInfo($"[STEAM] Лобі створено! ID: {SteamNetwork.LobbyID}");
            }
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[STEAM] OnLobbyCreated помилка: {e.Message}");
        }
    }

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
                _logger.LogInfo($"[STEAM] ✓ Ти ХОСТ! LobbyID={SteamNetwork.LobbyID}");

                OpenInviteOverlay(lobbyId);
            }
        }
        catch (System.Exception e) { _logger.LogError($"[STEAM] OnLobbyCreated помилка: {e.Message}"); }
    }

    private void OpenInviteOverlay(object lobbyId)
    {
        try
        {
            var steamIdType = _steamMatchmaking?.Assembly.GetType("Steamworks.CSteamID");
            var steamIdObj  = System.Activator.CreateInstance(steamIdType,
                System.Convert.ToUInt64(lobbyId));

            _steamFriends
                ?.GetMethod("ActivateGameOverlayInviteDialog",
                    BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new[] { steamIdObj });

            _logger.LogInfo("[STEAM] Overlay запрошення відкрито ✓");
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[STEAM] Overlay помилка: {e.Message}");
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
                _logger.LogInfo($"[STEAM] ✓ Ти КЛІЄНТ! LobbyID={SteamNetwork.LobbyID}");

                FetchHostSteamID();
                StartCoroutine(LoadGameAsClient());
            }
        }
        catch (System.Exception e) { _logger.LogError($"[STEAM] OnLobbyEntered помилка: {e.Message}"); }
    }

    private IEnumerator LoadGameAsClient()
    {
        yield return new WaitForSeconds(1f);

        SendPacket(SteamNetwork.RemoteID, new byte[] { 0x01 });
        _logger.LogInfo("[CLIENT] Запит сейву надіслано хосту ✓");
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
            _logger.LogInfo($"[STEAM] Хост SteamID: {SteamNetwork.RemoteID}");
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[STEAM] FetchHostSteamID помилка: {e.Message}");
        }
    }

    private IEnumerator RegisterLobbyCallback()
    {
        yield return new WaitForSeconds(0.5f);

        try
        {
            var asm = _steamMatchmaking?.Assembly;

            var nativeMethods = asm?.GetType("Steamworks.NativeMethods");
            var callbackBase  = asm?.GetType("Steamworks.CCallbackBase");

            _logger.LogInfo($"[STEAM] NativeMethods: {(nativeMethods != null ? "✓" : "✗")}");
            _logger.LogInfo($"[STEAM] CCallbackBase: {(callbackBase != null ? "✓" : "✗")}");

            var steamApiCallResult = asm?.GetType("Steamworks.CallResult`1");
            _logger.LogInfo($"[STEAM] CallResult: {(steamApiCallResult != null ? "✓" : "✗")}");

            var callbackTypes = asm?.GetTypes()
                .Where(t => t.Name.Contains("Callback") && !t.Name.Contains("Identity"))
                .Select(t => t.Name)
                .Take(10);
            foreach (var name in callbackTypes ?? System.Linq.Enumerable.Empty<string>())
                _logger.LogInfo($"[STEAM] Type: {name}");
        }
        catch (System.Exception e)
        {
            _logger.LogError($"[STEAM] RegisterCallback помилка: {e.Message}");
        }
    }
}

public static class PendingSave
{
    public static object Data = null;
}

[System.Serializable]
public struct NetworkPacket
{
    public byte type;
    public float x;
    public float y;
    public float dirAngle;
    public int globalState;
    public float inputH;
    public float inputV;
}

// ─────────────────────────────────────────────────────────────────────────────
// HARMONY ПАТЧІ
// ─────────────────────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────────────────────────────────────
// Патч проти дублікатів гравця від GameAwakenerEngine
// Коли взаємодієш з деревом/каменем — GameAwakenerEngine відновлює об'єкти зі
// збереження, включаючи Player(Clone). Цей патч вимикає будь-який PlayerComponent
// який не є головним гравцем MainGame.me.player.
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch]
public static class PlayerComponentStartPatch
{
    static MethodBase TargetMethod()
    {
        var type = AccessTools.TypeByName("PlayerComponent");
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        // Патчимо Start — там PlayerComponent ініціалізується
        return type?.GetMethod("Start", flags)
            ?? type?.GetMethod("Awake", flags);
    }

    static void Postfix(MonoBehaviour __instance)
    {
        try
        {
            // Якщо це RemotePlayer_Clone або Player2_Clone — вже вимкнено раніше, пропускаємо
            var goName = __instance.gameObject.name;
            if (goName == "RemotePlayer_Clone" || goName == "Player2_Clone") return;

            var flags        = BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Static | BindingFlags.Instance;
            var mainGameType = AccessTools.TypeByName("MainGame");
            var me           = mainGameType?.GetField("me", flags)?.GetValue(null);
            if (me == null) return;

            // Знаходимо MainGame.me.player_char або player
            var playerField = mainGameType.GetField("player_char", flags)
                           ?? mainGameType.GetField("player", flags);
            var mainPlayer = playerField?.GetValue(me) as MonoBehaviour;

            if (mainPlayer == null) return;

            // Якщо цей PlayerComponent НЕ є головним гравцем — вимикаємо
            if (__instance != mainPlayer && __instance.gameObject != mainPlayer.gameObject)
            {
                __instance.enabled = false;
                Multiplayer.Log?.LogInfo($"[AWAKE] Вимкнено зайвий PlayerComponent на {goName} " +
                    $"(не є MainGame.me.player) ✓");
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
// SimplifiedWGO.Restore кидає NullReferenceException кожен кадр під час
// взаємодії (рубка, копання). Це головна причина 1 FPS у клієнта.
// Finalizer мовчки ковтає exception — без логування, без overhead.
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch]
static class SimplifiedWGORestorePatch
{
    static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("SimplifiedWGO"), "Restore");

    static Exception Finalizer(Exception __exception) => null;
}

// GameAwakenerEngine.Update також може кидати через SimplifiedWGO —
// заглушуємо і його
[HarmonyPatch]
static class GameAwakenerEnginePatch
{
    static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("GameAwakenerEngine"), "Update");

    static Exception Finalizer(Exception __exception) => null;
}

// Блокуємо відновлення наших клонів GameAwakenerEngine-ом.
// Коли гравець взаємодіє з об'єктом — awakener намагається "відновити"
// всі WGO в радіусі, включаючи RemotePlayer_Clone і Player2_Clone.
// SimplifiedWGO — не MonoBehaviour, тому перевіряємо через WorldGameObject.
[HarmonyPatch]
static class SimplifiedWGORestoreFilterPatch
{
    static MethodBase TargetMethod() =>
        AccessTools.Method(AccessTools.TypeByName("SimplifiedWGO"), "Restore");

    // Кешуємо FieldInfo один раз — GetField через reflection дорогий,
    // а цей Prefix може викликатись сотні разів за кадр при рубці/копанні
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

    // Без try/catch в hot path — викликається для КОЖНОГО WorldGameObject при
    // взаємодії з деревами/каменями. try/catch в Unity Mono дорогий навіть без exception.
    // Unity operator bool повертає false для знищених об'єктів — без exception.
    static bool Prefix(MonoBehaviour __instance)
    {
        return __instance != null && (bool)(UnityEngine.Object)__instance;
    }

    // Finalizer ковтає будь-який exception що прорвався — на випадок крайніх ситуацій
    static Exception Finalizer(Exception __exception) => null;
}

[HarmonyPatch]
public static class OnGameLoadedPatch
{
    static MethodBase TargetMethod() =>
        AccessTools.TypeByName("MainGame")
            ?.GetMethod("OnGameLoaded", BindingFlags.NonPublic | BindingFlags.Instance);

    static void Postfix(object __instance)
    {
        Multiplayer.Log?.LogInfo($"[TUT] OnGameLoaded Postfix! IsClientMode={SteamNetwork.IsClientMode}");
        if (!SteamNetwork.IsClientMode) return;
        {
            SteamManager.ForceSkipTutorialParams(Multiplayer.Log);
            Multiplayer.Log?.LogInfo("[TUT] OnGameLoaded: туторіал скіпнуто ✓");
        }

        if (PendingSave.Data == null) return;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var mainGameType = __instance.GetType();

        mainGameType.GetField("save", flags)?.SetValue(__instance, PendingSave.Data);
        PendingSave.Data = null;
        Multiplayer.Log.LogInfo("[CLIENT] PendingSave ін'єктовано ✓");

        var saveObj = mainGameType.GetField("save", flags)?.GetValue(__instance);
        if (saveObj != null)
        {
            var mapField = saveObj.GetType()
                .GetField("map", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var mapObj = mapField?.GetValue(saveObj);
            mapObj?.GetType()
                .GetMethod("RestoreScene", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(mapObj, null);
            Multiplayer.Log.LogInfo("[CLIENT] RestoreScene викликано ✓");
        }
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

        Multiplayer.Log.LogInfo("[CLIENT] Intro.ShowIntro — пропущено, викликаємо on_finished ✓");
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
            Multiplayer.Log?.LogWarning("[TUT] TutorialGUI тип НЕ знайдено!");
            yield break;
        }

        var allFlags = BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Instance | BindingFlags.Static
                                           | BindingFlags.DeclaredOnly;

        var allMethods = tutType.GetMethods(allFlags);
        Multiplayer.Log?.LogInfo($"[TUT] TutorialGUI має {allMethods.Length} методів:");
        foreach (var m in allMethods)
            Multiplayer.Log?.LogInfo($"[TUT]   -> {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");

        int count = 0;
        foreach (var m in allMethods)
        {
            string n = m.Name;
            if (n == "Open" || n.StartsWith("Show") || n == "OnEnable")
            {
                Multiplayer.Log?.LogInfo($"[TUT] Патчимо: {n}");
                count++;
                yield return m;
            }
        }

        if (count == 0)
            Multiplayer.Log?.LogWarning("[TUT] НІЧОГО НЕ ЗНАЙДЕНО для патчу!");

        var mainGameType = AccessTools.TypeByName("MainGame");
        if (mainGameType != null)
        {
            foreach (var m in mainGameType.GetMethods(allFlags | BindingFlags.Instance))
            {
                if (m.Name.Contains("Tutorial") || m.Name.Contains("Intro") || m.Name.Contains("tutorial"))
                {
                    Multiplayer.Log?.LogInfo($"[TUT] Патчимо MainGame.{m.Name}");
                    yield return m;
                }
            }
        }
    }

    static bool Prefix(MethodBase __originalMethod)
    {
        if (!SteamNetwork.IsClientMode) return true;
        // StackTrace прибрано — дуже дорога операція для hot path
        Multiplayer.Log?.LogInfo($"[TUT] ЗАБЛОКОВАНО: {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}");
        return false;
    }
}

[HarmonyPatch]
public static class OnGameStartedPlayingPatch
{
    // Guard — OnGameStartedPlaying може спрацювати кілька разів за одну сесію
    // (кожен ShowIntro → OnGameStartedPlaying → новий Player(Clone)).
    // Дозволяємо тільки перший виклик — решта ігноруються.
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
            Multiplayer.Log?.LogInfo("[TUT] OnGameStartedPlaying — заблоковано (дублікат) ✓");
            return false; // блокуємо повторний запуск
        }
        _hasRun = true;
        return true;
    }

    static void Postfix(object __instance)
    {
        // Хост теж починає надсилати позицію
        if (SteamNetwork.Role == NetworkRole.Host)
        {
            SteamNetwork.IsInGame = true;
            SteamNetwork.IsInGameSince = Time.time;
        }

        if (!SteamNetwork.IsClientMode) return;
        SteamManager.ForceSkipTutorialParams(Multiplayer.Log);
        Multiplayer.Log?.LogInfo("[TUT] OnGameStartedPlaying Postfix — туторіал скіпнуто ✓");
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
        // Блокуємо відкриття меню поки клієнт завантажується
        if (SteamNetwork.IsClientMode && SteamNetwork.IsLoadingAsClient)
        {
            Multiplayer.Log?.LogInfo("[CLIENT] MainMenuGUI.Open заблоковано (завантаження) ✓");
            return false;
        }

        if (SteamNetwork.IsClientMode && SteamNetwork.IsInGame)
        {
            float timeSinceInGame = UnityEngine.Time.time - SteamNetwork.IsInGameSince;

            // Перші MENU_BLOCK_DURATION секунд після входу в гру — блокуємо
            // spurious Open() що викликається game loop під час ініціалізації.
            if (timeSinceInGame < SteamNetwork.MENU_BLOCK_DURATION)
            {
                Multiplayer.Log?.LogInfo($"[CLIENT] MainMenuGUI.Open заблоковано (spurious, t={timeSinceInGame:F1}с) ✓");
                return false;
            }

            // Після MENU_BLOCK_DURATION — це справжній вихід гравця в меню.
            // Скидаємо IsInGame і дозволяємо меню відкритись.
            Multiplayer.Log?.LogInfo($"[CLIENT] MainMenuGUI.Open дозволено (вихід гравця, t={timeSinceInGame:F1}с)");
            SteamNetwork.IsInGame = false;
            SteamNetwork.IsInGameSince = -1f;
        }

        return true;
    }
}

// MainMenuDiagPatch видалено — патчив кожен метод MainMenuGUI і писав лог
// на кожен виклик, що додавало overhead. Більше не потрібен після стабілізації.