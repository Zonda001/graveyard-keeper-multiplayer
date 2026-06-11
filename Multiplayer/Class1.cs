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

    // BaseCharacterComponent (рантайм-тип PlayerComponent) split-screen дубля P2.
    // Патчі порівнюють __instance саме з цим посиланням через ReferenceEquals —
    // це суто керована перевірка, нативну пам'ять не чіпає, тож безпечна навіть
    // для знищеного компонента. Звернення до .gameObject знищеного компонента
    // (напр. гравець під час респавну) дає нативний краш — тому його уникаємо.
    public static MonoBehaviour Player2Char;
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
        // ReferenceEquals замість __instance.gameObject — не торкаємось нативної
        // пам'яті, тож безпечно навіть якщо компонент знищено (див. MultiplayerState).
        if (ReferenceEquals(__instance, MultiplayerState.Player2Char))
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
        // Патч керує лише split-screen дублем P2 (дебаг, F9). Для всіх інших,
        // включно зі справжнім гравцем, нічого не робимо й НЕ торкаємось
        // __instance.gameObject: під час респавну гравця компонент може бути
        // знищений, і звернення до .gameObject дає нативний краш (підтверджено
        // Player.log: краш на get_gameObject одразу після gd_player_respawn).
        // ReferenceEquals — чиста керована перевірка, нативну пам'ять не чіпає.
        // Мережевий клон сюди не потрапляє — у RemotePlayer_Clone узагалі
        // знятий BaseCharacterComponent (стрипається при спавні клону).
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

        // Без цього Unity зупиняє весь ігровий цикл, коли вікно гри неактивне —
        // мод перестає слати/приймати пакети, і синк «замерзає» для гравця, що
        // перемкнувся в інше вікно. З runInBackground цикл працює завжди.
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
                ChopSync.Reset();
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
                MultiplayerState.Player2Char = null;
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

        // ── Розвідка рубки дерева ────────────────────────────────────────────
        // F6 — стати біля дерева, захопити його як ціль і дампнути всі поля.
        //      Натиснути ще раз після кількох ударів → diff знайде поле HP.
        // F5 — увімк/вимк трасування методів WorldGameObject для цілі;
        //      потім рубай і дивись [RECON-TRACE] у логах — це назви методів.
        if (Input.GetKeyDown(KeyCode.F6))
            ChopRecon.CaptureNearestTree();

        if (Input.GetKeyDown(KeyCode.F5))
        {
            ChopRecon.TraceEnabled = !ChopRecon.TraceEnabled;
            Logger.LogInfo($"[RECON] Трасування методів: {(ChopRecon.TraceEnabled ? "УВІМК" : "вимк")}");
        }

        // F4 — розвідка СТАДІЙ МОГИЛИ (B-2). Стань біля могили, тисни F4 → знімок усіх
        //      полів (включно з _data). Викопай ОДНУ стадію, знову F4 → diff двох
        //      знімків покаже поле, що кодує стадію (його й синкатимемо замість 0x0D).
        if (Input.GetKeyDown(KeyCode.F4))
            ChopRecon.CaptureNearestGrave();

        // F3 — РОЗВІДКА UID ДРОПІВ (ідея Zonda): стань біля трупа на землі → дамп усіх полів
        //      дропа+Item. Зніми на ОБОХ машинах для ОДНОГО трупа → порівняй *id/*uid (★).
        if (Input.GetKeyDown(KeyCode.F3))
            ChopRecon.DumpNearestBodyDrop();
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

    // Знищує клон коли інший гравець вийшов із лобі — інакше він застрягає
    // в світі назавжди. RemotePlayerSpawned скидається в false, тож якщо друг
    // повернеться, наступний пакет 0x06 заспавнить клон знову.
    public void DespawnRemotePlayer()
    {
        if (_remotePlayerGO != null)
        {
            Destroy(_remotePlayerGO);
            Logger.LogInfo("[REMOTE] Клон видалено — інший гравець вийшов ✓");
        }
        _remotePlayerGO = null;
        _cachedRemoteController = null;
        _spawnCoroutineRunning = false;
        SteamNetwork.RemotePlayerSpawned = false;
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

            // Кешуємо компонент P2 — патчі UpdatePlayer/PlayerControlIsDisabled
            // звіряють __instance із ним через ReferenceEquals.
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
            _remoteOverheadRenderer = sr;   // кеш для показу носіння (0x12/0x13)
            break;
        }
    }

    // ── Показ предмета над головою КЛОНА напарника (синк носіння overhead) ───────────
    private SpriteRenderer _remoteOverheadRenderer;

    private SpriteRenderer RemoteOverheadRenderer()
    {
        if (_remoteOverheadRenderer != null) return _remoteOverheadRenderer;
        if (_remotePlayerGO == null) return null;
        foreach (var sr in _remotePlayerGO.GetComponentsInChildren<SpriteRenderer>(true))
            if (sr.gameObject.name == "overhead_obj") { _remoteOverheadRenderer = sr; break; }
        return _remoteOverheadRenderer;
    }

    // Аніматор персонажа клона (для пози носіння через float "walk_type_f").
    private Animator _remoteAnimator;
    private Animator RemoteCharAnimator()
    {
        if (_remoteAnimator != null) return _remoteAnimator;
        if (_remotePlayerGO == null) return null;
        // Шукаємо аніматор, що має параметр walk_type_f (головний аніматор персонажа).
        foreach (var an in _remotePlayerGO.GetComponentsInChildren<Animator>(true))
        {
            if (an.runtimeAnimatorController == null) continue;
            foreach (var p in an.parameters)
                if (p.name == "walk_type_f") { _remoteAnimator = an; return _remoteAnimator; }
        }
        return _remoteAnimator;
    }

    // Поза носіння на клоні: SetWalkAnimationType у грі = animator.SetFloat("walk_type_f", x).
    // Standard=0, OverheadItem=0.1 (руки вгору, притримує), WithTool=-0.1.
    private void SetRemoteWalkType(float v)
    {
        var an = RemoteCharAnimator();
        if (an == null) { Logger.LogWarning("[CHOP] аніматор клона з walk_type_f не знайдено"); return; }
        try { an.SetFloat("walk_type_f", v); } catch { }
    }

    // Напарник підняв важкий предмет → показати його над головою його клона + поза носіння.
    public void SetRemoteOverhead(string icon)
    {
        var sr = RemoteOverheadRenderer();
        if (sr == null) { Logger.LogWarning("[CHOP] overhead клона не знайдено"); return; }
        try
        {
            var esc = AccessTools.TypeByName("EasySpritesCollection");
            var getSprite = esc?.GetMethod("GetSprite", BindingFlags.Public | BindingFlags.Static);
            var sprite = getSprite?.Invoke(null, new object[] { icon, false, "" });
            sr.sprite = sprite as Sprite;
            sr.color = Color.white;
            sr.enabled = sr.sprite != null;
            sr.gameObject.SetActive(true);
            SetRemoteWalkType(0.1f);   // поза OverheadItem — клон тримає предмет руками
            Logger.LogInfo($"[CHOP] Overhead клона: показано icon={icon} + поза носіння ✓");
        }
        catch (Exception e) { Logger.LogError($"[CHOP] SetRemoteOverhead впав: {e.Message}"); }
    }

    // Напарник поклав/кинув → прибрати предмет над головою його клона + звичайна поза.
    public void ClearRemoteOverhead()
    {
        SetRemoteWalkType(0f);   // повернути Standard-позу
        var sr = RemoteOverheadRenderer();
        if (sr == null) return;
        sr.sprite = null;
        sr.enabled = false;
        Logger.LogInfo("[CHOP] Overhead клона: прибрано + поза Standard");
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

    private object _lobbyEnteredCallback;
    private object _lobbyJoinRequestedCallback;
    private object _lobbyChatUpdateCallback;
    private object _p2pSessionRequestCallback;

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

                    // Колбек МАЄ зберігатись у полі — інакше GC збере об'єкт
                    // Callback<T>, його фіналайзер відпише колбек і виходи гравця
                    // перестануть приходити в OnLobbyChatUpdateRaw.
                    _lobbyChatUpdateCallback = cbConcrete
                        .GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new object[] { del });

                    _logger.LogInfo($"[STEAM] LobbyChatUpdate колбек: {(_lobbyChatUpdateCallback != null ? "✓" : "✗")}");
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

                    _logger.LogInfo($"[STEAM] P2PSessionRequest колбек: {(_p2pSessionRequestCallback != null ? "✓" : "✗")}");
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

            // m_ulSteamIDUserChanged — це ulong, а не CSteamID. Беремо значення
            // напряму (раніше код шукав поле m_SteamID на ulong → завжди 0,
            // а AcceptP2PSessionWithUser падав, бо хотів CSteamID).
            ulong changedId = userChanged != null ? System.Convert.ToUInt64(userChanged) : 0UL;

            uint state = System.Convert.ToUInt32(stateChange ?? 0);
            if (state == 1 && SteamNetwork.Role == NetworkRole.Host)
            {
                SteamNetwork.RemoteID = changedId;
                _logger.LogInfo($"[STEAM] Клієнт приєднався! RemoteID={SteamNetwork.RemoteID}");

                var steamIdType = _steamNetworking?.Assembly.GetType("Steamworks.CSteamID");
                var csid = steamIdType != null && changedId != 0
                    ? System.Activator.CreateInstance(steamIdType, changedId) : null;
                if (csid != null)
                {
                    _steamNetworking
                        ?.GetMethod("AcceptP2PSessionWithUser", BindingFlags.Public | BindingFlags.Static)
                        ?.Invoke(null, new[] { csid });
                    _logger.LogInfo("[P2P] AcceptP2PSessionWithUser для клієнта ✓");
                }
            }

            // Вихід/відключення/кік/бан іншого гравця — прибираємо застряглий клон.
            // Біти EChatMemberStateChange: Left=2, Disconnected=4, Kicked=8, Banned=16.
            const uint leftMask = 0x0002 | 0x0004 | 0x0008 | 0x0010;
            if ((state & leftMask) != 0 &&
                changedId != 0 && changedId == SteamNetwork.RemoteID)
            {
                _logger.LogInfo($"[STEAM] Інший гравець вийшов (id={changedId}, state={state}) — прибираємо клон");
                Multiplayer.Instance?.DespawnRemotePlayer();
                SteamNetwork.RemoteID = 0;
                _lastLobbyMemberCount = 0; // щоб PollLobbyMembers підхопив нового гравця
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

    // Час останнього отриманого пакета. Якщо клон є, а пакети зникли надовго —
    // інший гравець вийшов/вилетів/розрив мережі. Працює для обох ролей,
    // на відміну від колбека LobbyChatUpdate, який у цій грі не дисpatchиться.
    private float _lastRemotePacketTime = 0f;
    private const float REMOTE_PACKET_TIMEOUT = 5f;

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
            _lastRemotePacketTime = Time.time;
            // Логуємо тільки службові пакети (не позицію, анімацію, час)
            if (packet[0] != 0x06 && packet[0] != 0x07 && packet[0] != 0x08)
                _logger.LogInfo($"[P2P] Отримано пакет {packet.Length} байт, type={packet[0]}");
            OnPacketReceived(packet);
        }

        // Клон заспавнений, але пакети від іншого гравця зникли — він вийшов
        if (SteamNetwork.RemotePlayerSpawned &&
            Time.time - _lastRemotePacketTime > REMOTE_PACKET_TIMEOUT)
        {
            _logger.LogInfo($"[STEAM] Пакети від іншого гравця зникли на {REMOTE_PACKET_TIMEOUT}с — прибираємо клон");
            Multiplayer.Instance?.DespawnRemotePlayer();
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

            // Переносимий труп: щокадрове відстеження руху (внутрішній throttle на відправку).
            ChopSync.TickCarriedBodies();

            // Етап 3a: ре-claim живих крафтів (тримає TTL у напарника) + прибирання мертвих.
            ChopSync.TickCraftClaims();

            // Дерева, зрубані поки ми були в іншій локації — пробуємо знищити,
            // коли гравець дійде до них і вони завантажаться.
            _chopRetryTimer += Time.deltaTime;
            if (_chopRetryTimer >= CHOP_RETRY_INTERVAL)
            {
                _chopRetryTimer = 0f;
                ChopSync.RetryPendingDestroys();
                ChopSync.RetryPendingDrops();
            }
        }
    }

    private float _chopRetryTimer = 0f;
    private const float CHOP_RETRY_INTERVAL = 2f;

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

            if (count < 2)
            {
                // Клієнт вийшов із лобі — миттєво прибираємо клон (не чекаємо таймаут)
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
            // Формат: type(1) + angle(4) + state(4) + dirX(4) + dirY(4) = 17 байт
            if (data.Length < 17)
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
        else if (type == 0x09)
        {
            // ── Синхронізація рубки дерева ────────────────────────────────────
            if (data.Length < 13) { _logger.LogWarning($"[CHOP] 0x09 замалий: {data.Length}б"); return; }
            float tx     = BitConverter.ToSingle(data, 1);
            float ty     = BitConverter.ToSingle(data, 5);
            float amount = BitConverter.ToSingle(data, 9);
            _logger.LogInfo($"[CHOP] Віддалений удар: дерево @({tx:F1},{ty:F1}) amount={amount:F3}");
            ChopSync.ApplyRemoteChop(tx, ty, amount);
        }
        else if (type == 0x0A)
        {
            // ── Обʼєкт відпрацьований на іншій машині (дерево/камінь/могила/грядка) ─
            if (data.Length < 9) { _logger.LogWarning($"[CHOP] 0x0A замалий: {data.Length}б"); return; }
            float tx = BitConverter.ToSingle(data, 1);
            float ty = BitConverter.ToSingle(data, 5);
            _logger.LogInfo($"[CHOP] Віддалена зміна стану обʼєкта @({tx:F1},{ty:F1})");
            ChopSync.ApplyRemoteDestroy(tx, ty);
        }
        else if (type == 0x0B)
        {
            // ── Лут від зрубаного обʼєкта на іншій машині ────────────────────
            if (!ChopSync.ParseDropPacket(data, out float dx, out float dy,
                                          out object items, out object dir))
            {
                _logger.LogWarning($"[CHOP] 0x0B некоректний (len={data.Length})");
                return;
            }
            _logger.LogInfo($"[CHOP] Віддалений лут @({dx:F1},{dy:F1})");
            ChopSync.ApplyRemoteDrop(dx, dy, items, dir);
        }
        else if (type == 0x0D)
        {
            // ── Візуальна стадія могили на іншій машині (RedrawPart/ReplaceWithObject) ─
            _logger.LogInfo($"[CHOP] Віддалена стадія могили (0x0D, {data.Length}б)");
            ChopSync.ParseAndApplyGraveOp(data);
        }
        else if (type == 0x0F)
        {
            // ── Позиція переносимого трупа (носій рухає → глядач дзеркалить) ──────
            // type(1) uid(8) x(4) y(4)
            if (data.Length < 17) { _logger.LogWarning($"[CHOP] 0x0F замалий ({data.Length})"); return; }
            long buid = BitConverter.ToInt64(data, 1);
            float bx = BitConverter.ToSingle(data, 9);
            float by = BitConverter.ToSingle(data, 13);
            ChopSync.ApplyRemoteBodyPos(buid, bx, by);
        }
        else if (type == 0x10)
        {
            // ── Труп прибрано (покладено в могилу/спожито) → глядач чисто видаляє ──
            // type(1) uid(8)
            if (data.Length < 9) { _logger.LogWarning($"[CHOP] 0x10 замалий ({data.Length})"); return; }
            long ruid = BitConverter.ToInt64(data, 1);
            _logger.LogInfo($"[CHOP] Труп uid={ruid} прибрано напарником (0x10)");
            ChopSync.ApplyRemoteBodyRemove(ruid);
        }
        else if (type == 0x11)
        {
            // ── Спавн трупа на граві глядача через DropItem (непідбірний переносимий) ──
            // type(1) graveUid(8) dir(4) x(4) y(4) z(4) i3(4) b4(1) jsonLen(4) json
            if (data.Length < 34) { _logger.LogWarning($"[CHOP] 0x11 замалий ({data.Length})"); return; }
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
            _logger.LogInfo($"[CHOP] Спавн трупа від напарника uid={guid} json={cjl}б (0x11)");
            ChopSync.ApplyRemoteCorpseSpawn(guid, cdir, cx, cy, cz, cforce, cb4, cjson);
        }
        else if (type == 0x12)
        {
            // ── Напарник несе важкий предмет над головою → показати на його клоні ──
            // type(1) iconLen(1) icon(utf8)
            if (data.Length < 2) return;
            int il = data[1];
            string icon = (data.Length >= 2 + il) ? System.Text.Encoding.UTF8.GetString(data, 2, il) : "";
            _logger.LogInfo($"[CHOP] Напарник несе overhead icon={icon} (0x12)");
            Multiplayer.Instance?.SetRemoteOverhead(icon);
        }
        else if (type == 0x13)
        {
            // ── Напарник поклав/кинув → прибрати overhead із клона ──
            _logger.LogInfo("[CHOP] Напарник поклав overhead (0x13)");
            Multiplayer.Instance?.ClearRemoteOverhead();
        }
        else if (type == 0x14)
        {
            // ── Напарник ПІДНЯВ наш труп-дзеркало (передача власності) → прибрати наземну копію ──
            // type(1) uid(8). Труп повернеться як нове дзеркало через 0x11, коли напарник кине.
            if (data.Length < 9) { _logger.LogWarning($"[CHOP] 0x14 замалий ({data.Length})"); return; }
            long tuid = BitConverter.ToInt64(data, 1);
            ChopSync.ApplyRemoteCorpseTransfer(tuid);
        }
        else if (type == 0x15)
        {
            // ── СПАВН-ПРИМІТИВ (Фаза 2): напарник поставив будівлю/будмайданчик → спавнимо її в
            // нас зі СПІЛЬНИМ uid (щоб подальші стадії будівництва синкались по 0x0D за тим uid) ──
            // type(1) uid(8) x(4) y(4) z(4) objIdLen(2) objId
            if (data.Length < 21) { _logger.LogWarning($"[CHOP] 0x15 замалий ({data.Length})"); return; }
            long  buid = BitConverter.ToInt64(data, 1);
            float bx = BitConverter.ToSingle(data, 9);
            float by = BitConverter.ToSingle(data, 13);
            float bz = BitConverter.ToSingle(data, 17);
            int   bidLen = BitConverter.ToUInt16(data, 21);
            if (23 + bidLen > data.Length) { _logger.LogWarning("[CHOP] 0x15 objId за межами"); return; }
            string bObjId = System.Text.Encoding.UTF8.GetString(data, 23, bidLen);
            ChopSync.ApplyRemoteBuildSpawn(buid, bx, by, bz, bObjId);
        }
        else if (type == 0x16)
        {
            // ── Напарник ЗНІС будівлю → знищуємо свою копію за uid ──
            // type(1) uid(8). Симетрично до 0x15-спавну.
            if (data.Length < 9) { _logger.LogWarning($"[CHOP] 0x16 замалий ({data.Length})"); return; }
            long ruid = BitConverter.ToInt64(data, 1);
            ChopSync.ApplyRemoteBuildRemove(ruid);
        }
        else if (type == 0x17)
        {
            // ── Крафт-черга верстата (Фаза 2 кооп-крафт Етап 1): напарник змінив чергу →
            // відбудовуємо craft_queue + RedrawBubble, щоб бачити ті самі вікна над станціями ──
            // type(1) uid(8) count(2) [idLen(1) id n(4) infinite(1) flags(1)]*  (flags біт0 = синтетика)
            if (data.Length < 11) { _logger.LogWarning($"[CHOP] 0x17 замалий ({data.Length})"); return; }
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
            else _logger.LogWarning("[CHOP] 0x17 пошкоджений");
        }
        else if (type == 0x18)
        {
            // ── Claim/release/прогрес станції (Етап 3a/3b, арбітраж власника):
            // flag=1 старт {uid, craftIdLen, craftId}; flag=0 стоп; flag=2 прогрес {uid, 0-100} ──
            if (data.Length < 11) { _logger.LogWarning($"[CHOP] 0x18 замалий ({data.Length})"); return; }
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

        // НЕ серіалізуємо сейв назад у файл через рефлексію. Round-trip
        // GameSave.FromBinary → ToBinary втрачав DLC-обʼєкти (табір біженців):
        // сейв роздувався на ~150 КБ, а RefugeesCampEngine.Init() падав з NRE
        // → вічна загрузка в клієнта. 3.dat лишається сирими байтами хоста
        // (записані вище) — рівно той сейв, що завантажує хост. Правки вище
        // лишаються на обʼєкті newSave (він іде в linked_save як запасний шлях);
        // на випадок завантаження з файлу їх доганяє пост-завантажувальна
        // логіка — ClientTutorialWatcher та UnlockPlayerAfterLoad.

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

    static void Postfix()
    {
        Multiplayer.Log?.LogInfo($"[TUT] OnGameLoaded Postfix! IsClientMode={SteamNetwork.IsClientMode}");
        if (!SteamNetwork.IsClientMode) return;
        SteamManager.ForceSkipTutorialParams(Multiplayer.Log);
        Multiplayer.Log?.LogInfo("[TUT] OnGameLoaded: туторіал скіпнуто ✓");
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

// ─────────────────────────────────────────────────────────────────────────────
// РОЗВІДКА РУБКИ ДЕРЕВА (тимчасовий код — прибрати після Фази 0)
// Мета: знайти без сирців гри (а) поле HP/ударів дерева, (б) назви методів
// WorldGameObject, що спрацьовують при ударі та знищенні.
// Робочий процес:
//   1. Стань впритул до дерева, натисни F6 — у лог піде [RECON] ЗНІМОК.
//   2. Натисни F5 (трасування УВІМК), удар по дереву — дивись [RECON-TRACE].
//   3. Удар ще пару разів, знову F6 — порівняй два ЗНІМКИ, змінене поле = HP.
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
            if (wgoType == null) { Multiplayer.Log?.LogWarning("[RECON] Тип WorldGameObject не знайдено"); return; }

            var playerGO = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()
                .FirstOrDefault(mb => mb.GetType().Name == "PlayerComponent"
                                      && mb.gameObject.name != "RemotePlayer_Clone"
                                      && mb.gameObject.name != "Player2_Clone")?.gameObject;
            if (playerGO == null) { Multiplayer.Log?.LogWarning("[RECON] Локального гравця не знайдено"); return; }
            Vector3 origin = playerGO.transform.position;

            if (_objIdField == null)
                _objIdField = wgoType.GetField("_obj_id",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            // Збираємо всі WGO з дистанцією до гравця
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

            if (candidates.Count == 0) { Multiplayer.Log?.LogWarning("[RECON] WGO поблизу не знайдено"); return; }
            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

            Multiplayer.Log?.LogInfo("[RECON] 8 найближчих WGO (щоб звірити назви/obj_id):");
            foreach (var c in candidates.Take(8))
                Multiplayer.Log?.LogInfo($"[RECON]   {c.dist,6:F1}  {c.mb.gameObject.name}  obj_id='{c.objId}'  tree={c.isTree}");

            // Ціль — дерево, якщо стоїмо біля нього впритул (≤30); інакше просто
            // найближчий WGO. Без цього радіуса F6 в шахті матчив дерева за
            // 500м замість жили за 80м.
            const float CLOSE_TREE_RADIUS = 30f;
            var pick = candidates.FirstOrDefault(c => c.isTree && c.dist <= CLOSE_TREE_RADIUS);
            if (pick.mb == null) pick = candidates[0];

            Tracked = pick.mb;
            DoActionProbePatch.Reset();
            Multiplayer.Log?.LogInfo($"[RECON] ═══ ЦІЛЬ: {pick.mb.gameObject.name} " +
                $"obj_id='{pick.objId}' dist={pick.dist:F1} ═══");
            DumpState("ЗНІМОК");
            DumpFellingInfo();
            DumpNearestGroundItem();
            DumpEverythingNearby();
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] CaptureNearestTree: {e}"); }
    }

    // B-2: захопити найближчу МОГИЛУ (obj_id починається з "grave") і дампнути всі
    // поля. Окремо від CaptureNearestTree, бо там логіка «пріоритет дерева ≤30».
    // Робочий цикл: F4 (знімок) → викопати одну стадію → F4 → diff знімків у логах.
    public static void CaptureNearestGrave()
    {
        try
        {
            var wgoType = AccessTools.TypeByName("WorldGameObject");
            if (wgoType == null) { Multiplayer.Log?.LogWarning("[RECON] Тип WorldGameObject не знайдено"); return; }

            var playerGO = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()
                .FirstOrDefault(mb => mb.GetType().Name == "PlayerComponent"
                                      && mb.gameObject.name != "RemotePlayer_Clone"
                                      && mb.gameObject.name != "Player2_Clone")?.gameObject;
            if (playerGO == null) { Multiplayer.Log?.LogWarning("[RECON] Локального гравця не знайдено"); return; }
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

            if (best == null) { Multiplayer.Log?.LogWarning("[RECON] Могилу (grave*) поблизу не знайдено"); return; }

            Tracked = best;
            DoActionProbePatch.Reset();
            Multiplayer.Log?.LogInfo($"[RECON] ═══ МОГИЛА: {best.gameObject.name} " +
                $"obj_id='{bestId}' dist={bestDist:F1} ═══");
            DumpState("ЗНІМОК-МОГИЛА");
            DumpFellingInfo();
            DumpNearestGroundItem();
            DumpSerializationApi(best);
            DumpItemSerializationApi(best);
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] CaptureNearestGrave: {e}"); }
    }

    // B-2 підхід A-3: serialize-API самого Item (_data могили). item_data у
    // SerializableWGO порожній у рантаймі, тож шукаємо ЯК GK серіалізує Item напряму
    // (методи serial/save/load/string/byte/write/read/json) + GameRes (_params).
    // Також пробуємо викликати найімовірніший серіалайзер і показуємо результат.
    public static void DumpItemSerializationApi(MonoBehaviour wgo)
    {
        try
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var dataField = wgo.GetType().GetField("_data", flags);
            var data = dataField?.GetValue(wgo);
            if (data == null) { Multiplayer.Log?.LogInfo("[RECON] _data = null"); return; }
            var itemType = data.GetType();

            Multiplayer.Log?.LogInfo($"[RECON] ─── Item ({itemType.FullName}) serialize-методи ───");
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

            // GameRes (_params) — стан стадії. Як його перелічити/відтворити?
            var paramsObj = itemType.GetField("_params", flags)?.GetValue(data);
            if (paramsObj != null)
            {
                var gr = paramsObj.GetType();
                Multiplayer.Log?.LogInfo($"[RECON] ─── GameRes ({gr.FullName}) поля+методи ───");
                foreach (var fld in gr.GetFields(flags | BindingFlags.Static))
                    Multiplayer.Log?.LogInfo($"[RECON]   поле: {fld.Name} ({fld.FieldType.Name})");
                foreach (var m in gr.GetMethods(flags | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    string ln = m.Name.ToLower();
                    if (ln.StartsWith("get_") || ln.StartsWith("set_")) continue;
                    var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                    Multiplayer.Log?.LogInfo($"[RECON]   {gr.Name}.{m.Name}({ps}) -> {m.ReturnType?.Name}");
                }
            }

            // ToJSON(ser_depth) — головний кандидат на транспорт стану. Пробуємо кілька
            // глибин і ПОКАЗУЄМО повний JSON: треба та глибина, що включає _params
            // (grave_top_stn_plate_1 тощо) ТА inventory частин. Зворотнє — JsonUtility.
            var toJson = itemType.GetMethod("ToJSON", flags, null, new[] { typeof(int) }, null);
            if (toJson != null)
            {
                foreach (int depth in new[] { 0, 1, 3, 8 })
                {
                    try
                    {
                        var r = toJson.Invoke(data, new object[] { depth }) as string;
                        Multiplayer.Log?.LogInfo($"[RECON]   ToJSON({depth}) len={r?.Length}:");
                        // лог по шматках ~400 символів (BepInEx ріже довге)
                        for (int i = 0; r != null && i < r.Length; i += 400)
                            Multiplayer.Log?.LogInfo($"[RECON]     {r.Substring(i, Math.Min(400, r.Length - i))}");
                    }
                    catch (Exception ie) { Multiplayer.Log?.LogInfo($"[RECON]   ToJSON({depth}) впав: {ie.InnerException?.Message ?? ie.Message}"); }
                }
            }
            else Multiplayer.Log?.LogInfo("[RECON]   ToJSON(int) не знайдено");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] DumpItemSerializationApi: {e.Message}"); }
    }

    // B-2/підхід A: розвідка serialize-API для повної реплікації WGO. Шукаємо як з
    // WorldGameObject зробити SerializableWGO (для передачі) — інверсію вже знайденого
    // RestoreFromSerializedObject(SerializableWGO, bool). Дампить: WGO-методи зі словом
    // "serial" або типом-результату Serializable*; конструктори+поля SerializableWGO;
    // чи [Serializable] (чи можна гнати через BinaryFormatter як байти по дроту).
    public static void DumpSerializationApi(MonoBehaviour wgo)
    {
        try
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var wgoType = wgo.GetType();

            Multiplayer.Log?.LogInfo("[RECON] ─── SERIALIZE-API: WGO-методи (serial / →Serializable*) ───");
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
            if (swType == null) { Multiplayer.Log?.LogInfo("[RECON] SerializableWGO тип не знайдено"); return; }
            Multiplayer.Log?.LogInfo($"[RECON] ─── SerializableWGO ({swType.FullName}) ───");
            Multiplayer.Log?.LogInfo($"[RECON]   [Serializable]={swType.IsSerializable}");
            foreach (var c in swType.GetConstructors(flags))
            {
                var ps = string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                Multiplayer.Log?.LogInfo($"[RECON]   ctor({ps})");
            }
            foreach (var fld in swType.GetFields(flags))
                Multiplayer.Log?.LogInfo($"[RECON]   поле: {fld.Name} ({fld.FieldType.Name})");
            // Статичні фабрики/хелпери серіалізації по всьому типу теж корисні
            foreach (var m in swType.GetMethods(flags | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                Multiplayer.Log?.LogInfo($"[RECON]   static {m.Name}({ps}) -> {m.ReturnType?.Name}");
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] DumpSerializationApi: {e.Message}"); }
    }

    // Дамп УСІХ GameObject біля гравця (будь-який тип) з їхніми компонентами —
    // щоб знайти обʼєкти, що не є WorldGameObject (напр. колода). З F6.
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

            Multiplayer.Log?.LogInfo($"[RECON] ─── Усі GameObject в радіусі 250 ({hits.Count}), топ-30 ───");
            foreach (var h in hits.Take(30))
            {
                var comps = string.Join(", ", h.Value.GetComponents<Component>()
                    .Where(c => c != null).Select(c => c.GetType().Name));
                Multiplayer.Log?.LogInfo($"[RECON]   {h.Key,6:F1}  '{h.Value.name}'  [{comps}]");
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] DumpEverythingNearby: {e}"); }
    }

    // Розвідка впалого луту: знаходить найближчий ItemOnGround і дампить його
    // компоненти, поля й методи (синк дропу/підбирання). З F6.
    public static void DumpNearestGroundItem()
    {
        try
        {
            var t = AccessTools.TypeByName("DropResGameObject");
            if (t == null) { Multiplayer.Log?.LogInfo("[RECON] тип DropResGameObject не знайдено"); return; }

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
                Multiplayer.Log?.LogInfo("[RECON] DropResGameObject поблизу нема — спершу зруби дерево");
                return;
            }

            Multiplayer.Log?.LogInfo($"[RECON] ═══ DropResGameObject '{nearest.gameObject.name}' @ {best:F1} ═══");
            Multiplayer.Log?.LogInfo("[RECON] Компоненти:");
            foreach (var comp in nearest.gameObject.GetComponents<Component>())
                if (comp != null) Multiplayer.Log?.LogInfo($"[RECON]   - {comp.GetType().Name}");

            DumpFields(nearest, 0, "");

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Multiplayer.Log?.LogInfo("[RECON] Методи:");
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

            // ── Підсвітка для синку 0x0C ────────────────────────────────────────
            // Позиція дропу (ключ матчингу між машинами) + який item він несе.
            var p = nearest.transform.position;
            Multiplayer.Log?.LogInfo($"[RECON] >>> 0x0C: drop pos=({p.x:F1},{p.y:F1})");

            // Кандидати на метод ПІДБИРАННЯ — щоб патчити саме його (0x0C відправник).
            Multiplayer.Log?.LogInfo("[RECON] >>> 0x0C КАНДИДАТИ ПІДБИРАННЯ:");
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

    // Дамп методів-кандидатів (заміна/анімація/падіння) по ієрархії типу obj.
    private static void DumpCandidateMethods(object obj, string tag,
                                             HashSet<string> seen, BindingFlags flags)
    {
        Multiplayer.Log?.LogInfo($"[RECON] ─── Методи-кандидати {tag} ({obj.GetType().Name}) ───");
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

    // Розвідка зрубування: дамп obj_def цілі + методи-кандидати на заміну/анімацію
    // (ReplaceWithObject, падіння тощо) з сигнатурами. З F6 разом зі ЗНІМКОМ.
    public static void DumpFellingInfo()
    {
        if (Tracked == null) return;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var seen = new HashSet<string>();
        DumpCandidateMethods(Tracked, "WGO", seen, flags);

        // wop (WorldObjectPart) — візуальна частина обʼєкта; анімація падіння тут.
        var wop = Tracked.GetType().GetField("wop", flags)?.GetValue(Tracked);
        if (wop != null) DumpCandidateMethods(wop, "wop", seen, flags);

        var objDef = Tracked.GetType().GetField("obj_def", flags)?.GetValue(Tracked);
        if (objDef != null)
        {
            Multiplayer.Log?.LogInfo($"[RECON] ─── obj_def ({objDef.GetType().Name}) поля ───");
            DumpFields(objDef, 1, "obj_def.");

            // after_hp_0 — на що обʼєкт перетворюється після зрубування (id пенька).
            var ahp = objDef.GetType()
                .GetField("after_hp_0", flags)?.GetValue(objDef);
            if (ahp != null)
            {
                Multiplayer.Log?.LogInfo($"[RECON] ─── after_hp_0 ({ahp.GetType().Name}) поля+методи ───");
                DumpFields(ahp, 1, "after_hp_0.");
                foreach (var m in ahp.GetType()
                    .GetMethods(flags | BindingFlags.DeclaredOnly))
                {
                    if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
                    var ps = string.Join(", ", m.GetParameters()
                        .Select(p => p.ParameterType.Name + " " + p.Name));
                    Multiplayer.Log?.LogInfo($"[RECON]   метод: {m.Name}({ps}) -> {m.ReturnType.Name}");
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

    // Дамп компонентів дерева (включно з дітьми) та всіх Animator-ів зі станами,
    // кліпами й параметрами — шукаємо анімацію падіння/смерті дерева.
    private static void DumpAnimators()
    {
        if (Tracked == null) return;
        try
        {
            Multiplayer.Log?.LogInfo("[RECON] ─── Компоненти (дерево + діти) ───");
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
                        Multiplayer.Log?.LogInfo($"[RECON]   кліп: {clip.name} ({clip.length:F2}с)");
                foreach (var p in anim.parameters)
                    Multiplayer.Log?.LogInfo($"[RECON]   параметр: {p.name} ({p.type})");
            }

            // API компонентів анімації дерева — методи й поля.
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
                Multiplayer.Log?.LogInfo($"[RECON] ─── {tn} методи+поля ───");
                foreach (var m in ct.GetMethods(flags))
                {
                    if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
                    var ps = string.Join(", ", m.GetParameters()
                        .Select(p => p.ParameterType.Name + " " + p.Name));
                    Multiplayer.Log?.LogInfo($"[RECON]   метод: {m.Name}({ps}) -> {m.ReturnType.Name}");
                }
                foreach (var fld in ct.GetFields(flags))
                    Multiplayer.Log?.LogInfo($"[RECON]   поле: {fld.Name} ({fld.FieldType.Name})");
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[RECON] DumpAnimators: {e.Message}"); }
    }

    // Дамп будь-якого об'єкта (для проби аргументів DoAction)
    public static void DumpAny(object obj, string label)
    {
        if (obj == null) { Multiplayer.Log?.LogInfo($"[RECON] {label}: null"); return; }
        string n = obj is MonoBehaviour mb ? mb.gameObject.name : obj.GetType().Name;
        Multiplayer.Log?.LogInfo($"[RECON] ─── {label}: {n} ───");
        DumpFields(obj, 0, "");
    }

    public static void DumpState(string label)
    {
        if (Tracked == null) { Multiplayer.Log?.LogWarning("[RECON] Ціль не захоплено — спершу F6"); return; }
        Multiplayer.Log?.LogInfo($"[RECON] ─── {label}: {Tracked.gameObject.name} ───");

        Multiplayer.Log?.LogInfo("[RECON] Компоненти на GameObject:");
        foreach (var mb in Tracked.gameObject.GetComponents<MonoBehaviour>())
            if (mb != null) Multiplayer.Log?.LogInfo($"[RECON]   - {mb.GetType().Name}");

        DumpFields(Tracked, 0, "");
    }

    // Дамп полів об'єкта по ієрархії його ігрових типів (без Unity-бази).
    // РОЗВІДКА UID ДРОПІВ (ідея Zonda 2026-06-09): чи має рантайм-дроп (труп) СТАБІЛЬНИЙ uid,
    // ОДНАКОВИЙ на обох машинах? Якщо так — універсальний синк дропів за uid без grave-anchor
    // (двері до загального синку). F3: стань біля трупа на землі → дамп усіх полів дропа +
    // вкладеного Item (2 рівні). Зніми на ОБОХ машинах для ОДНОГО трупа → порівняй *id/*uid.
    public static void DumpNearestBodyDrop()
    {
        var t = AccessTools.TypeByName("DropResGameObject");
        if (t == null) { Multiplayer.Log?.LogWarning("[RECON-DROP] тип DropResGameObject не знайдено"); return; }
        MonoBehaviour best = null; float bestD = float.MaxValue;
        foreach (var c in UnityEngine.Object.FindObjectsOfType(t))
        {
            var mb = c as MonoBehaviour;
            if (mb == null || DropId(mb) != "body") continue;
            float d = Camera.main != null
                ? Vector3.Distance(mb.transform.position, Camera.main.transform.position) : 0f;
            if (d < bestD) { bestD = d; best = mb; }
        }
        if (best == null) { Multiplayer.Log?.LogWarning("[RECON-DROP] трупа-дропа поблизу немає (стань біля трупа на землі)"); return; }
        Multiplayer.Log?.LogInfo($"[RECON-DROP] ===== Труп go={best.gameObject.name} instId={best.GetInstanceID()} " +
            $"pos=({best.transform.position.x:F1},{best.transform.position.y:F1}) =====");
        DumpFieldsDeep(best, "drop.", 0);
    }

    // Читає id Item з DropResGameObject (поле типу з .id:string).
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

    // Глибокий дамп (2 рівні): усі поля об'єкта + вкладені reference-поля (Item тощо), щоб
    // побачити будь-які *id/*uid. Окремо помічає поля з "id"/"uid" у назві маркером ★.
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
                try { val = f.GetValue(obj); } catch { val = "<помилка читання>"; }
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
        // Ловимо й `_data` (Item) — RECON 2026-06-05: стадія могили зберігається
        // ВСЕРЕДИНІ обʼєкта саме тут. Старий `fn == "data"` не матчив підкреслення.
        return fn.Contains("data")
            || ft.Name.IndexOf("Data", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Format(object val)
    {
        if (val == null) return "null";
        if (val is string s) return $"\"{s}\"";
        var t = val.GetType();
        if (t.IsPrimitive || t.IsEnum) return val.ToString();
        if (val is UnityEngine.Object uo) return uo == null ? "null(знищено)" : $"{uo.name} <{t.Name}>";
        if (val is System.Collections.ICollection col) return $"<{t.Name} count={col.Count}>";
        try { string r = val.ToString(); return r.Length > 120 ? r.Substring(0, 120) + "…" : r; }
        catch { return $"<{t.Name}>"; }
    }
}

// Трасує методи WorldGameObject, схожі на взаємодію/удар/знищення, але логує
// тільки для захопленої цілі (ChopRecon.Tracked) і лише коли трасування увімкнено.
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
        // B-2: могила — це крафт-інтеракція, а не hp. Ловимо крафт/частини/візуал
        // та методи перебудови обʼєкта (ReplaceWithObject/SetObject/Reset/Init…).
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

// РОЗВІДКА ПІДБИРАННЯ ТРУПА (2026-06-08): спавн розшифровано — труп іде через WGO.DropItem
// (однина, id=body). Тепер треба знайти МЕТОД ПІДБИРАННЯ (труп беруть ВРУЧНУ колізією, не
// авто-магнітом — можливо НЕ CollectDrop). Цей патч трасує методи DropResGameObject з назвами
// collect/pick/take/grab/catch/fly/destroy/remove + дампить id дропу та стек. Гейт —
// ChopRecon.TraceEnabled (F5). Цикл (СОЛО): F5 → підняти труп з землі → у логі [CORPSE-PICK]
// метод+стек. Це фундамент синку підбирання (опора C / інвентар).
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

// РОЗВІДКА РЕ-ДРОПУ/НОСІННЯ ТРУПА (2026-06-09): труп — ПЕРЕНОСИМИЙ (при підборі НЕ
// знищується, несеться). Треба знайти (а) метод узяття в руки, (б) метод ВИКИДАННЯ з рук —
// re-drop НЕ йде через WGO.DropItem (підтверджено логом: повторний труп не з'явився, 0 хуків).
// Трасуємо методи WorldGameObject з назвами hand/throw/put/place/drop (окрім DropItem/DropItems,
// що вже хукнуті) + дампимо типи аргументів і Item.id. Гейт F5. Той самий безпечний патерн, що
// й CorpsePickupTracePatch (попередній варіант падав на патчі Unity-повідомлень OnDestroy).
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
            if (m.Name == "DropItem" || m.Name == "DropItems") continue;   // вже хукнуті окремо
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

// РОЗВІДКА #3: чи CanPickupWithInteraction справді ПІДБИРАЄ (повертає true в мить забору)
// чи лише щокадрова перевірка. Логуємо __result (гейт F5).
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

// Проба DoAction(WorldGameObject player, float amount): дампить аргумент-гравця
// до і після виклику для захопленої цілі — щоб побачити, чи DoAction мутує
// об'єкт гравця (енергія/стан) на віддаленій машині при реплікації.
// Один раз на ціль (скидається при F6).
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
        Multiplayer.Log?.LogInfo($"[RECON-PROBE] ═══ DoAction ДО, amount={__args.ElementAtOrDefault(1)} ═══");
        ChopRecon.DumpAny(__args.ElementAtOrDefault(0), "ГРАВЕЦЬ-АРГ ДО");
    }

    static void Postfix(MonoBehaviour __instance, object[] __args)
    {
        if (_done || __instance == null || __instance != ChopRecon.Tracked) return;
        _done = true;
        ChopRecon.DumpAny(__args.ElementAtOrDefault(0), "ГРАВЕЦЬ-АРГ ПІСЛЯ");
        Multiplayer.Log?.LogInfo("[RECON-PROBE] ═══ DoAction готово (порівняй ДО/ПІСЛЯ) ═══");
    }

    static Exception Finalizer(Exception __exception) => null;
}

// ═════════════════════════════════════════════════════════════════════════════
// ФАЗА 1 — СИНХРОНІЗАЦІЯ РУБКИ ДЕРЕВА
// Лічильник «скільки лишилось рубати» — НЕ на дереві (розвідка: поля дерева не
// змінювались між ударами), а в роботі гравця. Тому повтор DoAction/OnWorkFinished
// на чужій машині не може звалити дерево. Модель:
//   0x09 — удар: повторюємо DoAction → дерево трясеться (косметика).
//   0x0A — дерево зрубане: коли на машині рубача спрацьовує DropItems (= дерево
//          впало й кинуло лут), транслюємо знищення; приймач прибирає своє дерево.
// Дерево ідентифікуємо за позицією (unique_id різний на кожній машині). Лут
// падає лише в того, хто реально рубав — приймач просто видаляє обʼєкт.
// ═════════════════════════════════════════════════════════════════════════════

// B-2 підхід A: SerializableWGO [Serializable], але містить Unity-структури
// (Vector3 position/rotation-scale, Vector2 spawner_coords), які BinaryFormatter
// сам не вміє. Сурогати навчають його (де)серіалізувати ці типи — і весь граф
// SerializableWGO іде по дроту. Той самий патерн, що в save-системах на Unity.
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

public static class ChopSync
{
    // Поріг збігу позиції — трохи менше відстані між сусідніми деревами.
    private const float POSITION_EPSILON = 2f;

    // Echo guard навколо нашого власного повтору DoAction.
    public static bool ApplyingRemoteChop;

    private static Type       _wgoType;
    private static FieldInfo  _objIdField;
    private static FieldInfo  _isPlayerField;
    private static MethodInfo _doActionMethod;
    private static FieldInfo  _objDefField;             // WorldGameObject.obj_def
    private static FieldInfo  _variationField;          // WorldGameObject.variation
    private static MethodInfo _replaceWithObjectMethod; // WGO.ReplaceWithObject(string,bool,int)
    private static MethodInfo _redrawPartMethod;        // WGO.RedrawPart(WorldObjectPart,string,string,int) — тригер зміни могили
    // B-2 підхід A — повна реплікація обʼєкта (state replication):
    private static Type       _serializableWgoType;     // SerializableWGO ([Serializable])
    private static MethodInfo _fromWgoMethod;           // static SerializableWGO.FromWGO(wgo) → SerializableWGO
    private static MethodInfo _restoreFromSerializedMethod; // WGO.RestoreFromSerializedObject(SerializableWGO, bool)
    private static MethodInfo _redrawMethod;            // WGO.Redraw(force_redraw,force_redraw_part,draw_puff) — форс-малювання меблів могили після restore
    private static FieldInfo  _itemInventoryField;      // Item.inventory (List<Item>) — furniture-предмети могили
    private static MethodInfo _getParamMethod;          // WGO.GetParam(string,float) — значення частини могили
    private static MethodInfo _setParamMethod;          // WGO.SetParam(string,float) — форс «завершено» у глядача (без каркаса)
    private static MethodInfo _getBodyFromInventoryMethod; // WGO.GetBodyFromInventory(bool) — надійний сигнал тіла в могилі (для підпису стадії)
    private static MethodInfo _spawnWgoMethod;          // WorldMap.SpawnWGO(Transform,string,Vector3?) — спавн нового обʼєкта (Фаза 2 будівництво)
    private static MethodInfo _destroyMeMethod;         // WGO.DestroyMe() (декомпіл 114595) — знищення обʼєкта (синк знесення будівлі 0x16)
    private static PropertyInfo _worldRootProp;         // LazyEngine.world_root (static Transform, декомпіл 98892) — батько для спавну
    private static FieldInfo  _uniqueIdField;           // WGO.unique_id (long) — надійний матчинг цілі
    private static FieldInfo  _swUniqueIdField;         // SerializableWGO.unique_id
    private static FieldInfo  _swItemDataField;         // SerializableWGO.item_data (String) — компактний стан _data
    private static FieldInfo  _swItemField;             // SerializableWGO.item (Item) — занулюємо, щоб restore читав item_data
    private static FieldInfo  _swObjIdField;            // SerializableWGO.obj_id (String)
    private static FieldInfo  _swVariationField;        // SerializableWGO.variation (Int32)
    private static FieldInfo  _dataField;               // WGO._data (Item) — стан могили
    private static MethodInfo _toJsonMethod;            // Item.ToJSON(int ser_depth) → JSON стану
    private static MethodInfo _fromJsonOverwriteMethod; // UnityEngine.JsonUtility.FromJsonOverwrite(string, object)
    // Дедуп стану могил за uid (обидва боки): не слати/не застосовувати ідентичний
    // стан двічі — інакше дубль-тригери дають зайвий RestoreFromSerializedObject = мигання.
    private static readonly Dictionary<long, string> _lastSentGraveSig = new Dictionary<long, string>();
    private static readonly Dictionary<long, string> _lastAppliedGraveSig = new Dictionary<long, string>();
    // ЧАС останнього applied-стану (для ВІКНА echo-приглушення). Луна (стан, який ми щойно
    // застосували як глядач) вертається за частки секунди; СТЕЙЛ applied-підпис (напр.
    // застосований на старті під час метушні по цвинтарю) НЕ має глушити чесну зміну через
    // хвилини. БАГ 2026-06-10: будівник застосував повну могилу 13587 на старті (лог рядок 288)
    // → відбудова до тієї ж повної (той самий підпис) глушилась вічно → 2-й furniture-предмет
    // не доходив. Вікно розрубує: глушимо лише НЕДАВНЮ луну.
    private static readonly Dictionary<long, float> _lastAppliedGraveSigTime = new Dictionary<long, float>();
    private const float ECHO_SUPPRESS_WINDOW = 4f;
    // DEDUP РЕ-ДРОПУ ЧАСТИН МОГИЛИ (фікс дюпу при co-dig, 2026-06-08): могила легітимно
    // віддає кожну частину 1× на стадію. Доки набір частин (стадія) той самий, повторний
    // 0x0B-дроп тієї ж частини = runaway-ре-дроп (restore регідратував _data при co-dig) →
    // НЕ шлемо. Скидається коли стадія реально змінилась. uid→стадія, uid→вже-надіслані-id.
    private static readonly Dictionary<long, string> _graveSentStageSig = new Dictionary<long, string>();
    private static readonly Dictionary<long, HashSet<string>> _graveSentParts = new Dictionary<long, HashSet<string>>();
    // ГАРД ВЛАСНИКА (2026-06-08): час останньої ГЕНУЇННОЇ локальної зміни стадії могили
    // (realtimeSinceStartup; ставиться ПІСЛЯ echo-перевірки → луна сюди не пише). Якщо я
    // нещодавно сам копав цю могилу — НЕ застосовую вхідний restore на неї (він би
    // регідратував _data моєї активної могили → runaway ре-дроп). Лайв-тест 2026-06-08:
    // машина слала стан 32763 (рядок 362) і за частку секунди застосувала вхідний (369) →
    // runaway. Глядач генуїнних змін не робить (його тригери = луна, відсікаються) → вікно
    // не оновлюється → він застосовує все. Це той самий сигнал, що раніше провалився ЛИШЕ
    // через баг розташування (мітку ставив після дедуп-return → при стабільній стадії не оновлювалась).
    private static readonly Dictionary<long, float> _lastLocalGraveDigTime = new Dictionary<long, float>();
    private const float GRAVE_OWNER_WINDOW = 3f;
    // Вікно «активного контесту» для гарду МОНОТОННОСТІ (ширше за owner-window): поки я
    // САМ працював цю могилу так нещодавно — тримаємо захист від stale-echo-рехідратації
    // (runaway-ре-дроп триває ~кілька секунд після завершення дії). Поза вікном (чистий
    // глядач або давно по копанню) монотонність НЕ діє → видно й зворотню відбудову могили.
    private const float GRAVE_CONTEST_WINDOW = 8f;
    // АРТЕФАКТ RESTORE (фікс локальної піраміди в ГЛЯДАЧА, 2026-06-08): час останнього
    // restore ПО uid. Якщо могилу нещодавно регідратували вхідним станом — її локальні
    // grave-дропи = runaway-артефакт (гра ре-кидає частину після restore). Скасовуємо їх
    // у Prefix DropItems. Owner-guard не дає restore на могилі яку Я копаю → recent-restore
    // = я глядач, не копач (тож копачів лут не чіпаємо). Send-dedup був Postfix → локальний
    // предмет уже спавнився; Prefix скасовує сам виклик гри = піраміди нема.
    private static readonly Dictionary<long, float> _lastGraveRestoreTime = new Dictionary<long, float>();
    private const float GRAVE_RESTORE_ARTIFACT_WINDOW = 8f;
    private static FieldInfo  _afterHp0Field;           // ObjectDefinition.after_hp_0
    private static MethodInfo _afterHp0GetValue;        // ChancedStringValue.GetValue(wgo,char)
    private static FieldInfo  _hasCraftField;           // ObjectDefinition.has_craft (верстат/крафт-станція)
    private static Type       _craftComponentType;      // CraftComponent (компонент крафту на верстаті)
    private static FieldInfo  _craftQueueField;         // CraftComponent.craft_queue (List<CraftQueueItem>)
    private static Type       _craftQueueItemType;      // CraftComponent+CraftQueueItem (вкладений)
    private static FieldInfo  _cqiIdField;              // CraftQueueItem.id (string — CraftDefinition id)
    private static FieldInfo  _cqiNField;               // CraftQueueItem.n (int — кількість у черзі)
    private static FieldInfo  _cqiInfiniteField;        // CraftQueueItem.infinite (bool)
    private static FieldInfo  _isCraftingField;         // CraftComponent.is_crafting (активний крафт)
    private static FieldInfo  _currentCraftField;       // CraftComponent.current_craft (CraftDefinition)
    private static FieldInfo  _craftDefIdField;         // CraftDefinition.id (успадковане з BalanceBaseObject)
    private static PropertyInfo _componentWgoProp;      // WorldGameObjectComponent.wgo (дістати WGO з компонента)
    private static PropertyInfo _wgoComponentsProp;     // WGO.components (ComponentsManager — НЕ Unity-компоненти!)
    private static PropertyInfo _componentsCraftProp;   // ComponentsManager.craft (CraftComponent зі словника)
    private static PropertyInfo _isRemovingProp;        // WGO.is_removing (станція в процесі знесення)
    private static PropertyInfo _wgoProgressProp;       // WGO.progress (float 0..1 → _data.progress)
    private static MethodInfo _setBubbleWidgetDataMethod; // WGO.SetBubbleWidgetData(BubbleWidgetData, WidgetID)
    private static Type _bubbleProgressDataType;        // BubbleWidgetProgressData
    private static Type _progressDelegateType;          // BubbleWidgetProgressData.ProgressDelegate (вкладений)
    private static object _widgetIdCraftingProgress;    // enum BubbleWidgetData.WidgetID.CraftingProgress
    private static MethodInfo _redrawBubbleMethod;      // WGO.RedrawBubble — перемалювати вікно черги
    private static Type       _treeDisappearType;       // TreeDisappearAnimation (компонент дерева)
    private static MethodInfo _startAnimationMethod;    // TreeDisappearAnimation.StartAnimation(VoidDelegate)
    private static Type       _voidDelegateType;        // VoidDelegate (делегат void())
    private static Type       _itemType;                // Item (id, value)
    private static ConstructorInfo _itemCtor;           // new Item(string id, int value)
    private static FieldInfo  _itemIdField;             // Item.id (string)
    private static FieldInfo  _itemValueField;          // Item.value (int)
    private static Type       _directionType;           // Direction (enum)
    private static MethodInfo _dropItemsMethod;         // WGO.DropItems(List<Item>, Direction)
    private static MethodInfo _dropItemSingularMethod;  // WGO.DropItem(Item, Direction, Vector3, float, bool)
    private static MethodInfo _getOverheadIconMethod;   // Item.GetOverheadIcon() → назва спрайта над головою
    private static bool       _ready;

    private static MonoBehaviour _cachedLocalPlayerWgo;

    // Дерева, зрубані іншим гравцем, поки ця машина була в іншій локації —
    // вони ще не «розбуджені» тут. Знищуємо їх, коли гравець підійде й вони
    // завантажаться (RetryPendingDestroys).
    private static readonly List<Vector2> _pendingDestroys = new List<Vector2>();

    // Лут, що випав у іншого гравця, поки WGO ще не завантажений тут.
    // Зберігаємо координати + готовий List<Item> + Direction. RetryPendingDrops.
    private struct PendingDrop { public Vector2 Pos; public object Items; public object Direction; }
    private static readonly List<PendingDrop> _pendingDrops = new List<PendingDrop>();

    // СПІЛЬНИЙ ЛУТ: реплей 0x0B спавнить лут і на приймачі, той збирає свою копію.
    // Інвентарі окремі, тож це не дюп — обидва гравці отримують лут (рішення
    // Zonda 2026-06-05, протестовано лайв-тестом). Жодного анти-дюпу/тегування.

    private static void EnsureReflection()
    {
        if (_ready || _wgoType != null) return;
        _wgoType = AccessTools.TypeByName("WorldGameObject");
        if (_wgoType == null) return;
        var f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _objIdField     = _wgoType.GetField("_obj_id", f);
        _isPlayerField  = _wgoType.GetField("_is_player", f);
        _doActionMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DoAction"
                              && m.GetParameters().Length == 2
                              && m.GetParameters()[1].ParameterType == typeof(float));

        // Заміна зрубаного обʼєкта наступником (дерево → пеньок). Best-effort:
        // якщо не зібралось — приймач відкотиться на простий Destroy.
        _objDefField    = _wgoType.GetField("obj_def", f);
        _variationField = _wgoType.GetField("variation", f);
        _replaceWithObjectMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "ReplaceWithObject"
                              && m.GetParameters().Length == 3);
        // B-2: візуальний перехід стадій могили. Трейс копання (2026-06-07) показав
        // RedrawPart(WorldObjectPart wop, string id, string path, int n) — для під-частин
        // wop==null (grave_bot_stn_1_stg_1, "objects/grave parts/"). Прямий метод-ефект,
        // не потребує робочої сесії (на відміну від DoAction).
        _redrawPartMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "RedrawPart"
                              && m.GetParameters().Length == 4
                              && m.GetParameters()[1].ParameterType == typeof(string));

        // B-2 підхід A: serialize-API (RECON 2026-06-07). FromWGO(wgo) → SerializableWGO
        // ([Serializable] → BinaryFormatter по дроту), RestoreFromSerializedObject застосовує
        // ВЕСЬ стан (item/_data + візуал). unique_id для матчингу цілі (краще за позицію).
        _uniqueIdField = _wgoType.GetField("unique_id", f);
        _serializableWgoType = AccessTools.TypeByName("SerializableWGO");
        _fromWgoMethod = _serializableWgoType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "FromWGO" && m.GetParameters().Length == 1);
        _restoreFromSerializedMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "RestoreFromSerializedObject" && m.GetParameters().Length == 2);
        // WGO.Redraw(bool force_redraw, bool force_redraw_part, bool draw_puff) — рядок 114000
        // декомпіляції. Кличе custom_drawers.OnObjectRedraw(force_redraw), що малює меблі могили
        // (рамка GraveFence/хрест GraveStone) з інвентаря + рефрешить quality. RestoreFromSerializedObject
        // робить лише SetObject (база), меблі не перемальовує → потрібен форс-редрав після restore.
        _redrawMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "Redraw" && m.GetParameters().Length == 3);
        // WGO.GetBodyFromInventory(bool) — надійний сигнал «у могилі є тіло» (декомпіл 115283). Тіло
        // сидить в інвентарі (не в _res_type), тож key-only підпис стадії його НЕ ловив → кладення
        // тіла в grave_empty не міняло підпис → echo/дедуп ковтав 0x0D → тіло не синкалось, виштовхувалось.
        _getBodyFromInventoryMethod = _wgoType.GetMethods(f)
            .FirstOrDefault(m => m.Name == "GetBodyFromInventory" && m.GetParameters().Length == 1);
        // Фаза 2 (будівництво): спавн нового WGO зі спільним uid. WorldMap.SpawnWGO(Transform,string,Vector3?)
        // (декомпіл 99430) + MainGame.world_root (98892) як батько. uid форсимо напряму після спавну.
        var worldMapType = AccessTools.TypeByName("WorldMap");
        _spawnWgoMethod = worldMapType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "SpawnWGO" && m.GetParameters().Length == 3
                              && m.GetParameters()[0].ParameterType.Name == "Transform"
                              && m.GetParameters()[1].ParameterType == typeof(string));
        // world_root — СТАТИЧНА property у класі LazyEngine (декомпіл 98892, НЕ MainGame!).
        _worldRootProp = AccessTools.TypeByName("LazyEngine")?.GetProperty("world_root",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        _destroyMeMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DestroyMe" && m.GetParameters().Length == 0);
        // WGO.GetParam/SetParam — щоб у глядача форсити furniture-предмети могили як «завершені»
        // (param≥1) → гра не малює деревʼяний каркас «під будівництвом». _itemInventoryField нижче
        // (після _itemType). GetParam має дефолт-аргумент → шукаємо overload з 2 параметрами.
        _getParamMethod = _wgoType.GetMethods(f).FirstOrDefault(m => m.Name == "GetParam"
            && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(string));
        _setParamMethod = _wgoType.GetMethods(f).FirstOrDefault(m => m.Name == "SetParam"
            && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(string)
            && m.GetParameters()[1].ParameterType == typeof(float));
        _swUniqueIdField  = _serializableWgoType?.GetField("unique_id", f);
        _swItemDataField  = _serializableWgoType?.GetField("item_data", f);
        _swItemField      = _serializableWgoType?.GetField("item", f);
        _swObjIdField     = _serializableWgoType?.GetField("obj_id", f);
        _swVariationField = _serializableWgoType?.GetField("variation", f);
        _dataField        = _wgoType.GetField("_data", f);
        _afterHp0Field   = AccessTools.TypeByName("ObjectDefinition")?.GetField("after_hp_0", f);
        _hasCraftField   = AccessTools.TypeByName("ObjectDefinition")?.GetField("has_craft", f);
        _afterHp0GetValue = AccessTools.TypeByName("ChancedStringValue")?.GetMethods(f)
            .FirstOrDefault(m => m.Name == "GetValue" && m.GetParameters().Length == 2);

        // Крафт-черга (Фаза 2 кооп-крафт, Етап 1): CraftComponent.craft_queue (List<CraftQueueItem>).
        _craftComponentType = AccessTools.TypeByName("CraftComponent");
        _craftQueueField    = _craftComponentType?.GetField("craft_queue", f);
        _craftQueueItemType = _craftComponentType?.GetNestedType("CraftQueueItem",
                                  BindingFlags.Public | BindingFlags.NonPublic);
        _cqiIdField         = _craftQueueItemType?.GetField("id", f);
        _cqiNField          = _craftQueueItemType?.GetField("n", f);
        _cqiInfiniteField   = _craftQueueItemType?.GetField("infinite", f);
        _isCraftingField    = _craftComponentType?.GetField("is_crafting", f);
        _currentCraftField  = _craftComponentType?.GetField("current_craft", f);
        _craftDefIdField    = AccessTools.TypeByName("CraftDefinition")?.GetField("id", f);
        _componentWgoProp   = AccessTools.TypeByName("WorldGameObjectComponent")?.GetProperty("wgo", f);
        // CraftComponent — НЕ MonoBehaviour (WorldGameObjectComponentBase = звичайний клас, декомпіл
        // 100354), GetComponentInChildren його НІКОЛИ не знайде (лайв-тест 2026-06-11: 0x17 мовчав).
        // Правильний шлях гри: wgo.components (ComponentsManager, 111820) → .craft (82514).
        _wgoComponentsProp   = _wgoType.GetProperty("components", f);
        _componentsCraftProp = _wgoComponentsProp?.PropertyType.GetProperty("craft", f);
        _isRemovingProp      = _wgoType.GetProperty("is_removing", f);
        // Прогрес-бар напарника (Етап 3b): wgo.progress (112161) + рідний віджет бару.
        // BubbleWidgetProgressData(ProgressDelegate, int, int) — 42346; WidgetID.CraftingProgress
        // — той самий слот, що гра ставить/стирає у RefreshComponentBubbleData (84597/84633).
        _wgoProgressProp           = _wgoType.GetProperty("progress", f);
        // УВАГА: SetBubbleWidgetData має кілька оверлоадів (BubbleWidgetData/string) — беремо
        // саме той, що приймає BubbleWidgetData (інакше Invoke з віджетом упав би на касті).
        _setBubbleWidgetDataMethod = _wgoType.GetMethods(f).FirstOrDefault(m =>
            m.Name == "SetBubbleWidgetData" && m.GetParameters().Length == 2 &&
            m.GetParameters()[0].ParameterType.Name == "BubbleWidgetData");
        _bubbleProgressDataType    = AccessTools.TypeByName("BubbleWidgetProgressData");
        _progressDelegateType      = _bubbleProgressDataType?.GetNestedType("ProgressDelegate",
                                         BindingFlags.Public | BindingFlags.NonPublic);
        try
        {
            var widType = AccessTools.TypeByName("BubbleWidgetData")?.GetNestedType("WidgetID",
                              BindingFlags.Public | BindingFlags.NonPublic);
            if (widType != null) _widgetIdCraftingProgress = Enum.Parse(widType, "CraftingProgress");
        }
        catch { }
        _redrawBubbleMethod = _wgoType.GetMethods(f).FirstOrDefault(m => m.Name == "RedrawBubble");

        // Анімація падіння дерева — компонент TreeDisappearAnimation на дереві.
        _treeDisappearType = AccessTools.TypeByName("TreeDisappearAnimation");
        _startAnimationMethod = _treeDisappearType?.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "StartAnimation" && m.GetParameters().Length == 1);
        _voidDelegateType = AccessTools.TypeByName("VoidDelegate");

        // Item/Direction/DropItems — для синку луту (0x0B). Best-effort: без них
        // лут не реплеїтиметься, але все інше працює.
        _itemType        = AccessTools.TypeByName("Item");
        _itemCtor        = _itemType?.GetConstructor(new[] { typeof(string), typeof(int) });
        _itemIdField     = _itemType?.GetField("id", f);
        _itemInventoryField = _itemType?.GetField("inventory", f);  // List<Item> — furniture-предмети могили
        _itemValueField  = _itemType?.GetField("value", f);
        _getOverheadIconMethod = _itemType?.GetMethod("GetOverheadIcon", f, null, Type.EmptyTypes, null);
        _toJsonMethod    = _itemType?.GetMethod("ToJSON", f, null, new[] { typeof(int) }, null);
        _fromJsonOverwriteMethod = AccessTools.TypeByName("UnityEngine.JsonUtility")
            ?.GetMethod("FromJsonOverwrite", BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(string), typeof(object) }, null);
        _directionType   = AccessTools.TypeByName("Direction");
        _dropItemsMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DropItems" && m.GetParameters().Length == 2);
        // Singular DropItem(Item, Direction, Vector3, int, bool) — спавн ПЕРЕНОСИМОГО трупа
        // (на відміну від DropItems-множини, що робить авто-збиральний лут). Для реплею на глядача.
        _dropItemSingularMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DropItem"
                              && m.GetParameters().Length >= 1
                              && m.GetParameters()[0].ParameterType.Name == "Item");

        _ready = _objIdField != null && _doActionMethod != null;
        if (!_ready)
            Multiplayer.Log?.LogWarning("[CHOP] Рефлексію не ініціалізовано — синк рубки вимкнено");
    }

    public static void Reset() { _pendingDestroys.Clear(); _pendingDrops.Clear(); }

    // Обʼєкти, ЗНИЩЕННЯ яких синхронізуємо (пакети 0x09 удар, 0x0A знищення):
    // дерева (tree*) і наземні камені (stone_N). Жили шахт (steep_*) НЕ сюди —
    // вони не зникають, реплей DoAction/ReplaceWithObject поламає стан.
    private static bool IsDestroySyncTarget(MonoBehaviour wgo)
    {
        EnsureReflection();
        if (!_ready || wgo == null) return false;
        var id = _objIdField.GetValue(wgo) as string;
        if (id == null) return false;
        return id.StartsWith("tree") || id.StartsWith("stone");
    }

    // Обʼєкти, ЗМІНУ СТАНУ яких синхронізуємо (пакет 0x0A — перехід after_hp_0
    // або знищення). Ширше за IsDestroySyncTarget: крім дерев і каменів сюди
    // йдуть грядки (garden*) — лайв-тест 2026-06-01 підтвердив, що збір врожаю
    // (одностадійний hp→0) трансформується на приймачі чисто.
    // МОГИЛИ (grave*) НАВМИСНО ВИКЛЮЧЕНІ: вони багатостадійні (частини падають
    // окремими DropItems), і форс ReplaceWithObject/Destroy на приймачі знищував
    // цілу могилу замість переходу на наступну стадію (лайв-тест 2026-06-01:
    // "могила просто пропала" + у гробаря завис work-стан / череп). Лут могил
    // далі синкається через 0x0B; їх трансформацію треба робити окремим, точним
    // шляхом (знати game-метод стадійного переходу — окрема розвідка).
    // Жили шахт (steep_*) — теж НЕ сюди: вони не зникають, ReplaceWithObject
    // зламав би стан (синк лише їх луту через 0x0B / IsLootSyncTarget).
    // 0x09 (косметичний удар) лишається вужчим — лише IsDestroySyncTarget.
    private static bool IsTransformSyncTarget(MonoBehaviour wgo)
    {
        EnsureReflection();
        if (!_ready || wgo == null) return false;
        var id = _objIdField.GetValue(wgo) as string;
        if (id == null) return false;
        return id.StartsWith("tree") || id.StartsWith("stone") ||
               id.StartsWith("garden") || IsNatureGatherTarget(id);
    }

    // Обʼєкти, ЛУТ яких синхронізуємо (пакет 0x0B). Ширше за IsDestroySyncTarget —
    // сюди йде все що при DoAction викликає WGO.DropItems, незалежно від того
    // чи обʼєкт зникає, трансформується чи залишається:
    //   tree*/stone*   — дерева й наземні камені (зникають → синк з 0x09/0x0A)
    //   steep*         — жили шахт (не зникають, "respawn себе")
    //   grave*/garden* — могили на цвинтарі й грядки на фермі (ексгумація дає
    //                    body/lifeforce, копання дає камінь). Розвідка
    //                    2026-05-27 підтвердила той самий шлях DoAction →
    //                    RewardForWork → DropItems.
    // Реплей DropItems на приймачі безпечний для будь-якого WGO (просто
    // створює лут на землі біля цілі, не чіпає стан самого обʼєкта).
    private static bool IsLootSyncTarget(MonoBehaviour wgo)
    {
        EnsureReflection();
        if (!_ready || wgo == null) return false;
        var id = _objIdField.GetValue(wgo) as string;
        if (id == null) return false;
        return id.StartsWith("tree") || id.StartsWith("stone") ||
               id.StartsWith("steep") || id.StartsWith("grave") ||
               id.StartsWith("garden") || IsNatureGatherTarget(id) ||
               IsWorkbench(wgo);   // Фаза 2 крафт: ручний крафт скидає вихід через DropItems на землю
                                   // (декомпіл 83249) — спільний лут реплеїть напарнику. Авто-крафт у
                                   // інвентар верстата йде окремо через 0x0D inv-hash.
    }

    // Природні «збиральні» обʼєкти: квіти (flower_small_*), гриби (mushroom_*),
    // кущі (bush_*). Збираються як грядка — одностадійний hp→0 → DropItems →
    // after_hp_0/знищення, тож синкаємо і трансформацію (0x0A), і лут (0x0B).
    // СПАВНЕРИ ВИКЛЮЧЕНІ (flower_spawner / mushroom_spawner) — це невидимі точки
    // респавну, чіпати їх не можна, інакше зламаємо відновлення природи.
    // Ризику фрізу як у могил нема: _linked_worker=null (RECON 2026-06-01) і ми
    // НЕ кладемо їх у 0x09-удар (IsDestroySyncTarget), лише transform+loot.
    private static bool IsNatureGatherTarget(string id)
    {
        if (id == null || id.Contains("spawner")) return false;
        return id.StartsWith("flower") || id.StartsWith("mushroom") ||
               id.StartsWith("bush");
    }

    // B-2: могили (grave*) — синк ВІЗУАЛУ стадій через 0x0D (реплей RedrawPart /
    // ReplaceWithObject). Лут (включно з трупом) лишається на 0x0B. Окремо від
    // transform(0x0A): могилу НЕ можна форс-ReplaceWithObject у after_hp_0 (порожній).
    private static bool IsGraveTarget(MonoBehaviour wgo)
    {
        EnsureReflection();
        if (!_ready || wgo == null) return false;
        var id = _objIdField.GetValue(wgo) as string;
        return id != null && id.StartsWith("grave");
    }

    // uid будівель, які пройшли спавн-примітив 0x15 (поставлені/отримані цією сесією). Стадії
    // будівництва ЦИХ обʼєктів синкаємо наявним 0x0D (плейсхолдер_place→готова). Трек по uid (НЕ
    // широкий obj_id-предикат) — щоб НЕ флудити кожен WGO; спільний uid із 0x15 гарантує матч.
    private static readonly HashSet<long> _syncedBuildUids = new HashSet<long>();
    private static void TrackBuildUid(long uid) { if (uid != 0) _syncedBuildUids.Add(uid); }

    // Ціль реплікації СТАНУ через 0x0D: могила АБО синкнута будівля (за uid). Розширює grave-only
    // на Фазу 2 будівництва, лишаючись вузьким (тільки 0x15-обʼєкти, не весь світ).
    private static bool IsStateRepTarget(MonoBehaviour wgo)
    {
        if (IsGraveTarget(wgo)) return true;
        if (IsWorkbench(wgo)) return true;   // Фаза 2: крафт на верстатах (obj_def.has_craft)
        if (_syncedBuildUids.Count == 0 || wgo == null || _uniqueIdField == null) return false;
        try { return _syncedBuildUids.Contains(Convert.ToInt64(_uniqueIdField.GetValue(wgo))); }
        catch { return false; }
    }

    // Верстат/крафт-станція = obj_def.has_craft (декомпіл 78335). Стан крафту (вихід) сидить в
    // ІНВЕНТАРІ верстата, не в obj_id → потрібен інвентар-чутливий підпис (нижче), інакше дедуп глушить.
    private static bool IsWorkbench(MonoBehaviour wgo)
    {
        if (_objDefField == null || _hasCraftField == null || wgo == null) return false;
        try
        {
            var od = _objDefField.GetValue(wgo);
            return od != null && Convert.ToBoolean(_hasCraftField.GetValue(od));
        }
        catch { return false; }
    }

    // Радіус «гравець поруч із верстатом» (юніти світу). Крафт-взаємодія впритул (~150-300 одиниць
    // за лайв-логом: лут падав за ~160 від гравця), а дев-обʼєкти bat_test на 2000+ одиниць — поріг
    // 1200 чисто розділяє. Гейтить лише верстати (могили/будівлі завжди впритул, без гейту).
    private const float WORKBENCH_SYNC_RANGE = 1200f;

    // Локальний гравець у радіусі range від обʼєкта (по XY). Дешевий sqrMagnitude-чек.
    private static bool NearLocalPlayer(MonoBehaviour wgo, float range)
    {
        if (wgo == null) return false;
        var lp = GetLocalPlayerWgo();
        if (lp == null) return false;   // не знайшли гравця — НЕ синкаємо (краще пропустити, ніж флуд)
        Vector2 d = (Vector2)wgo.transform.position - (Vector2)lp.transform.position;
        return d.sqrMagnitude <= range * range;
    }

    // Хеш ВМІСТУ інвентаря верстата (id+value пар). Міняється коли матеріали завантажено / вихід
    // додано / забрано → підпис стадії змінюється → 0x0D синкає. СТАЛИЙ під час самого крафту
    // (інвентар не міняється, лише progress) → не флудить. 30 біт (лізе в variation поряд із body-бітом).
    private static int WorkbenchInvHash(MonoBehaviour wgo)
    {
        if (_dataField == null || _itemInventoryField == null || _itemIdField == null) return 0;
        try
        {
            var data = _dataField.GetValue(wgo);
            if (data == null) return 0;
            var inv = _itemInventoryField.GetValue(data) as System.Collections.IList;
            if (inv == null) return 0;
            int h = 0;
            foreach (var it in inv)
            {
                if (it == null) continue;
                string id = _itemIdField.GetValue(it) as string ?? "";
                int val = 0;
                if (_itemValueField != null) { try { val = Convert.ToInt32(_itemValueField.GetValue(it)); } catch { } }
                unchecked { h = h * 31 + id.GetHashCode(); h = h * 31 + val; }
            }
            return h & 0x3FFFFFFF;   // 30 біт
        }
        catch { return 0; }
    }

    // ── СИНК КРАФТ-ЧЕРГИ (0x17, Фаза 2 кооп-крафт — ЕТАП 1: ВИДИМІСТЬ черги) ─────────────────
    // craft_queue сидить у CraftComponent, НЕ в _data → 0x0D її не несе. Окремий пакет: при зміні
    // черги (постановка EnqueueCraft / старт / завершення) шлемо напарнику список {id,n,infinite};
    // він відбудовує craft_queue + RedrawBubble → бачить ті самі вікна над станціями. Прогрес уже
    // йде через 0x0D. Гейт по близькості (як 0x0D) + дедуп по хешу черги — без флуду.
    public struct CraftQ { public string id; public int n; public bool infinite; public bool synthetic; }
    private static readonly Dictionary<long,int> _lastSentCraftQueueHash = new Dictionary<long,int>();
    private static readonly Dictionary<long,int> _lastAppliedCraftQueueHash = new Dictionary<long,int>();
    // Інстанси СИНТЕТИЧНИХ CraftQueueItem, створених ApplyRemoteCraftQueue (мирор активного крафту
    // напарника). При відправці власної черги пропускаємо їх (Етап 3a) — інакше енкʼю на чужій
    // станції повернув би власнику його ж активний крафт як РЕАЛЬНИЙ пункт черги (дюп).
    private static readonly Dictionary<long, object> _mirrorSyntheticItems = new Dictionary<long, object>();

    // CraftComponent з WGO — через wgo.components.craft (ComponentsManager). НЕ Unity GetComponent:
    // WorldGameObjectComponent не успадковує MonoBehaviour, гра тримає їх у власному словнику.
    private static object GetCraftComponent(MonoBehaviour wgo)
    {
        if (wgo == null || _wgoComponentsProp == null || _componentsCraftProp == null) return null;
        try
        {
            var cm = _wgoComponentsProp.GetValue(wgo);
            return cm != null ? _componentsCraftProp.GetValue(cm) : null;
        }
        catch { return null; }
    }

    private static List<CraftQ> ReadCraftQueue(MonoBehaviour wgo, long uid)
    {
        var cc = GetCraftComponent(wgo);
        if (cc == null || _craftQueueField == null) return null;
        try
        {
            var q = _craftQueueField.GetValue(cc) as System.Collections.IList;
            if (q == null) return null;
            _mirrorSyntheticItems.TryGetValue(uid, out var mirroredSynthetic);
            var list = new List<CraftQ>(q.Count);
            foreach (var it in q)
            {
                if (it == null) continue;
                // Мирор активного крафту НАПАРНИКА (Етап 3a): не відсилати назад як реальний пункт.
                if (mirroredSynthetic != null && ReferenceEquals(it, mirroredSynthetic)) continue;
                var cq = new CraftQ
                {
                    id       = _cqiIdField?.GetValue(it) as string ?? "",
                    n        = _cqiNField != null ? Convert.ToInt32(_cqiNField.GetValue(it)) : 0,
                    infinite = _cqiInfiniteField != null && Convert.ToBoolean(_cqiInfiniteField.GetValue(it))
                };
                if (!string.IsNullOrEmpty(cq.id)) list.Add(cq);
            }
            // АКТИВНИЙ крафт — синтетичним ПЕРШИМ пунктом (лайв-тест 3, 2026-06-11): TryStartCraftFromQueue
            // при старті робить --n і ВИДАЛЯЄ пункт із черги (декомпіл 83782-85) — одиночний крафт лишає
            // craft_queue порожньою, хоч станція працює. Бабл у приймача малюється з craft_queue[0] і без
            // is_crafting (гілка 84606), тож синтетичний пункт показує «це зараз роблять» природно.
            // :r:-крафти (знесення) не миримо — знесення вже синкає 0x16.
            try
            {
                if (_isCraftingField != null && _currentCraftField != null && _craftDefIdField != null
                    && Convert.ToBoolean(_isCraftingField.GetValue(cc)))
                {
                    var cur = _currentCraftField.GetValue(cc);
                    var curId = cur != null ? _craftDefIdField.GetValue(cur) as string : null;
                    if (!string.IsNullOrEmpty(curId) && !curId.Contains(":r:"))
                        list.Insert(0, new CraftQ { id = curId, n = 1, infinite = false, synthetic = true });
                }
            }
            catch { }
            return list;
        }
        catch { return null; }
    }

    private static int CraftQueueHash(List<CraftQ> q)
    {
        if (q == null) return 0;
        int h = 17;
        foreach (var c in q) unchecked { h = h * 31 + (c.id?.GetHashCode() ?? 0); h = h * 31 + c.n; h = h * 31 + (c.infinite ? 1 : 0); }
        return h;
    }

    // Викликається з Postfix EnqueueCraft (__instance = CraftComponent — НЕ MonoBehaviour, тому object).
    public static void OnLocalCraftQueueChanged(object craftComponent)
    {
        if (ApplyingRemoteChop || !Connected() || craftComponent == null) return;
        EnsureReflection();
        if (_componentWgoProp == null) return;
        try
        {
            var wgo = _componentWgoProp.GetValue(craftComponent) as MonoBehaviour;
            if (wgo != null) SendCraftQueue(wgo);
        }
        catch { }
    }

    // Відправка черги верстата напарнику (0x17). Гейт близькості + дедуп по хешу.
    public static void SendCraftQueue(MonoBehaviour wgo)
    {
        if (ApplyingRemoteChop || !Connected() || wgo == null) return;
        EnsureReflection();
        if (_craftComponentType == null || _uniqueIdField == null) return;
        // МОГИЛИ ВИКЛЮЧЕНО: grave теж has_craft=True (крафт-інтеракція), але її черга = копання.
        // Мирор чистив би активну чергу напарника при co-dig (q.Clear), а гра ще й АВТО-СТАРТУЄ
        // чергу при роботі обʼєкта (декомпіл 88287: !IsCraftQueueEmpty && !is_crafting →
        // TryStartCraftFromQueue) → примарне копання + подвійний двигун стадій. Стадії могил
        // повністю покриває 0x0D — черга їм не потрібна.
        if (!IsWorkbench(wgo) || IsGraveTarget(wgo)) return;
        if (!NearLocalPlayer(wgo, WORKBENCH_SYNC_RANGE)) return;
        try
        {
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            var q = ReadCraftQueue(wgo, uid);
            if (q == null) return;
            int hash = CraftQueueHash(q);
            if (_lastSentCraftQueueHash.TryGetValue(uid, out var prev) && prev == hash) return;
            _lastSentCraftQueueHash[uid] = hash;
            // Перехресне сіювання (анти-пінгпонг, як echo-sig у 0x0D): ця ж черга, прилетівши
            // назад від напарника, буде розпізнана як уже застосована й не перебудує нашу живу.
            _lastAppliedCraftQueueHash[uid] = hash;

            // Підготувати байти id (пропустити порожні/завеликі), порахувати payload
            var idBytes  = new List<byte[]>(q.Count);
            var prepared = new List<CraftQ>(q.Count);
            int payload = 0;
            foreach (var c in q)
            {
                var b = System.Text.Encoding.UTF8.GetBytes(c.id);
                if (b.Length == 0 || b.Length > 255) continue;
                idBytes.Add(b); prepared.Add(c);
                payload += 1 + b.Length + 4 + 1 + 1;
            }
            if (prepared.Count > 65535) return;

            // packet: 0x17 uid(8) count(2) [idLen(1) id n(4) infinite(1) flags(1)]*
            var packet = new byte[11 + payload];
            packet[0] = 0x17;
            BitConverter.GetBytes(uid).CopyTo(packet, 1);
            BitConverter.GetBytes((ushort)prepared.Count).CopyTo(packet, 9);
            int off = 11;
            for (int i = 0; i < prepared.Count; i++)
            {
                packet[off++] = (byte)idBytes[i].Length;
                Buffer.BlockCopy(idBytes[i], 0, packet, off, idBytes[i].Length); off += idBytes[i].Length;
                BitConverter.GetBytes(prepared[i].n).CopyTo(packet, off); off += 4;
                packet[off++] = (byte)(prepared[i].infinite ? 1 : 0);
                packet[off++] = (byte)(prepared[i].synthetic ? 1 : 0);
            }
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
            Multiplayer.Log?.LogInfo($"[CHOP] Крафт-черга uid={uid} → {prepared.Count} пункт(ів) (0x17)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] SendCraftQueue: {e.Message}"); }
    }

    // Приймач: відбудовуємо craft_queue з пакета + RedrawBubble. Під echo-guard (EnqueueCraft не
    // кличемо — будуємо пункти напряму, тож хук не зафаєриться й луни нема).
    public static void ApplyRemoteCraftQueue(long uid, List<CraftQ> items)
    {
        EnsureReflection();
        if (_craftQueueField == null || _craftQueueItemType == null || items == null) return;
        var wgo = FindWgoByUniqueId(uid);
        if (wgo == null) return;   // не завантажено в нас (далеко) — Етап 1 пропускає; pending — на потім
        if (IsGraveTarget(wgo)) return;   // могили не миряться (див. SendCraftQueue); захист від змішаних збірок
        var cc = GetCraftComponent(wgo);
        if (cc == null) return;

        int hash = CraftQueueHash(items);
        if (_lastAppliedCraftQueueHash.TryGetValue(uid, out var prev) && prev == hash) return;
        _lastAppliedCraftQueueHash[uid] = hash;
        // Перехресне сіювання (анти-пінгпонг): наші локальні тригери, побачивши цю ж мирор-чергу,
        // не пошлють її назад відправнику.
        _lastSentCraftQueueHash[uid] = hash;

        ApplyingRemoteChop = true;
        try
        {
            var q = _craftQueueField.GetValue(cc) as System.Collections.IList;
            if (q == null) return;
            q.Clear();
            _mirrorSyntheticItems.Remove(uid);   // старий синтетичний інстанс пішов разом із Clear
            foreach (var c in items)
            {
                var cqi = Activator.CreateInstance(_craftQueueItemType);
                _cqiIdField?.SetValue(cqi, c.id);
                _cqiNField?.SetValue(cqi, c.n);
                _cqiInfiniteField?.SetValue(cqi, c.infinite);
                q.Add(cqi);
                if (c.synthetic) _mirrorSyntheticItems[uid] = cqi;   // мирор активного крафту напарника
            }
            if (_redrawBubbleMethod != null)
            {
                var pars = _redrawBubbleMethod.GetParameters();
                _redrawBubbleMethod.Invoke(wgo, pars.Length == 1 ? new object[] { null } : new object[0]);
            }
            Multiplayer.Log?.LogInfo($"[CHOP] Крафт-черга uid={uid} застосовано ← {items.Count} пункт(ів)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] ApplyRemoteCraftQueue: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
    }

    // ── ЕТАП 3a: АРБІТРАЖ ВЛАСНИКА СТАНЦІЇ (0x18 claim/release) ──────────────────────────────
    // Подвійний вихід крафту можливий ЛИШЕ якщо ОБИДВІ машини довели is_crafting=true на одній
    // станції (CraftComponent.DoAction гейтиться is_crafting, декомпіл 83086). CraftReally (83821)
    // — єдина лійка ВСІХ стартів (GUI/черга/зомбі/gratitude/авто), матеріали списуються всередині.
    // Модель: мій старт → claim 0x18; напарник блокує локальні старти на зайнятій станції.
    // Блок на ДВОХ рівнях: TryStartCraftFromQueue (бо вона робить --n і видаляє пункт ДО
    // CraftReally, декомпіл 83782 — блок лише CraftReally тихо зʼїдав би чергу) + CraftReally
    // (прямий шлях Craft() з GUI). Fail-open: claim без оновлень вмирає за CRAFT_CLAIM_TTL;
    // власник ре-claim-ить у TickCraftClaims, поки крафт живий (включно з паузою крафту).
    private const float CRAFT_CLAIM_TTL     = 60f;
    private const float CRAFT_CLAIM_REFRESH = 20f;
    private struct CraftClaim { public string craftId; public float time; }
    private static readonly Dictionary<long, CraftClaim> _remoteCrafting = new Dictionary<long, CraftClaim>();
    private static readonly Dictionary<long, CraftClaim> _localCrafting  = new Dictionary<long, CraftClaim>();
    private static readonly Dictionary<long, float> _lastBlockLogTime = new Dictionary<long, float>();
    private static float _claimRefreshTimer;
    // Момент останнього блоку старту — для підміни ігрового «not_enough_resources» (гра показує
    // його одразу після заблокованого старту, декомпіл 88292) на чесне «зайнято напарником».
    private static float _lastCraftBlockTime = -999f;
    public static bool WasCraftBlockedRecently => Time.realtimeSinceStartup - _lastCraftBlockTime < 1f;

    // «Зайнято напарником» під УСІ локалізації гри. Коди — GJL.LANGUAGES (firstpass-декомпіл):
    // en de fr pt-br es ru it pl ja zh_cn ko. Поточна мова — GameSettings.me.language (92625);
    // порожня/невідома → en (так чинить і сама гра: "Language not found. Loading EN").
    // CJK-гліфи безпечні: текст іде рідним Say-пайплайном, який ставить шрифт поточної локалі.
    private static readonly Dictionary<string, string> _busyMessages = new Dictionary<string, string>
    {
        { "en",    "In use by your partner!" },
        { "de",    "Wird von deinem Partner benutzt!" },
        { "fr",    "Occupé par votre partenaire !" },
        { "pt-br", "Em uso pelo seu parceiro!" },
        { "es",    "¡Ocupado por tu compañero!" },
        { "ru",    "Занято напарником!" },
        { "it",    "Occupato dal tuo compagno!" },
        { "pl",    "Zajęte przez partnera!" },
        { "ja",    "仲間が使用中！" },
        { "zh_cn", "同伴正在使用！" },
        { "ko",    "동료가 사용 중!" },
    };
    private static PropertyInfo _gameSettingsMeProp;       // GameSettings.me (static)
    private static FieldInfo   _gameSettingsLanguageField; // GameSettings.language

    public static string GetStationBusyMessage()
    {
        try
        {
            if (_gameSettingsMeProp == null)
            {
                var gs = AccessTools.TypeByName("GameSettings");
                _gameSettingsMeProp       = gs?.GetProperty("me", BindingFlags.Public | BindingFlags.Static);
                _gameSettingsLanguageField = gs?.GetField("language", BindingFlags.Public | BindingFlags.Instance);
            }
            var me   = _gameSettingsMeProp?.GetValue(null);
            var lang = me != null ? _gameSettingsLanguageField?.GetValue(me) as string : null;
            if (!string.IsNullOrEmpty(lang) && _busyMessages.TryGetValue(lang, out var msg)) return msg;
        }
        catch { }
        return _busyMessages["en"];
    }

    // Станція під арбітражем = верстат, але НЕ могила (у могил власна перевірена co-dig модель).
    private static bool IsArbitratedStation(MonoBehaviour wgo) => IsWorkbench(wgo) && !IsGraveTarget(wgo);

    private static bool IsWgoRemoving(MonoBehaviour wgo)
    {
        if (_isRemovingProp == null || wgo == null) return false;
        try { return Convert.ToBoolean(_isRemovingProp.GetValue(wgo)); }
        catch { return false; }
    }

    // Prefix TryStartCraftFromQueue + CraftReally: блокувати ЛОКАЛЬНИЙ старт, якщо станцію
    // зайняв напарник. Лог із throttle (88287 кличе TryStart щотік роботи). craftArg — аргумент
    // craft із CraftReally (другий шар захисту знесення, незалежний від рефлексії is_removing).
    public static bool ShouldBlockLocalCraftStart(object craftComponent, object craftArg = null)
    {
        if (ApplyingRemoteChop || !Connected() || craftComponent == null) return false;
        if (_remoteCrafting.Count == 0) return false;
        EnsureReflection();
        if (_componentWgoProp == null || _uniqueIdField == null) return false;
        try
        {
            // Remove-крафт НЕ блокуємо (другий шар, по самому крафту): блок → ProcessRemove →
            // миттєвий DestroyMe (декомпіл 44299/115655). Перший шар — IsWgoRemoving нижче.
            if (craftArg != null && _craftDefIdField != null)
            {
                var argId = _craftDefIdField.GetValue(craftArg) as string;
                if (argId != null && argId.Contains(":r:")) return false;
            }
            var wgo = _componentWgoProp.GetValue(craftComponent) as MonoBehaviour;
            if (wgo == null || !IsArbitratedStation(wgo)) return false;
            // ЗНЕСЕННЯ НЕ БЛОКУЄМО НІКОЛИ: заблокований Craft() у ProcessRemovingCraft (декомпіл
            // 44299) одразу кличе wgo.ProcessRemove() = МИТТЄВИЙ DestroyMe в обхід крафту —
            // зніс би будівлю миттю. Контест знесення лишається на старій моделі (race-тест ✓ + 0x16).
            if (IsWgoRemoving(wgo)) return false;
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            if (!_remoteCrafting.TryGetValue(uid, out var claim)) return false;
            if (Time.realtimeSinceStartup - claim.time > CRAFT_CLAIM_TTL)
            {
                _remoteCrafting.Remove(uid);   // fail-open: стейл-claim не блокує вічно
                return false;
            }
            _lastCraftBlockTime = Time.realtimeSinceStartup;
            if (!_lastBlockLogTime.TryGetValue(uid, out var lt) || Time.realtimeSinceStartup - lt > 2f)
            {
                _lastBlockLogTime[uid] = Time.realtimeSinceStartup;
                Multiplayer.Log?.LogInfo($"[CHOP] Старт заблоковано: станцію uid={uid} зайняв напарник ({claim.craftId})");
            }
            return true;
        }
        catch { return false; }
    }

    // Postfix CraftReally (__result=true): крафт реально стартував — клеймимо станцію.
    public static void OnLocalCraftStarted(object craftComponent)
    {
        if (ApplyingRemoteChop || !Connected() || craftComponent == null) return;
        EnsureReflection();
        if (_componentWgoProp == null || _uniqueIdField == null) return;
        try
        {
            var wgo = _componentWgoProp.GetValue(craftComponent) as MonoBehaviour;
            if (wgo == null || !IsArbitratedStation(wgo)) return;
            if (IsWgoRemoving(wgo)) return;   // знесення поза арбітражем (стара модель + 0x16)
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            string craftId = "";
            if (_currentCraftField != null && _craftDefIdField != null)
            {
                var cur = _currentCraftField.GetValue(craftComponent);
                if (cur != null) craftId = _craftDefIdField.GetValue(cur) as string ?? "";
            }
            if (craftId.Contains(":r:")) return;   // remove-крафт — теж поза арбітражем
            // Throttle: zero-time крафти йдуть циклом до 500 CraftReally за ОДИН виклик черги
            // (декомпіл 83787-93) — не слати 500 claim-ів; той самий craftId у межах 1с = заклеймлено.
            if (_localCrafting.TryGetValue(uid, out var prev) && prev.craftId == craftId &&
                Time.realtimeSinceStartup - prev.time < 1f)
            {
                prev.time = Time.realtimeSinceStartup;
                _localCrafting[uid] = prev;
                return;
            }
            _localCrafting[uid] = new CraftClaim { craftId = craftId, time = Time.realtimeSinceStartup };
            _lastSentProgressQ[uid] = -1;   // 3b: свіжий крафт → перший тік прогресу зайде (q=0)
            SendCraftClaim(uid, true, craftId);
        }
        catch { }
    }

    // Викликається з GraveWorkSyncPatch (OnWorkFinished/OnCraftStateChanged): крафт скінчився →
    // release; ще живий (батч amount>1 / пауза) → освіжити claim.
    public static void CheckLocalCraftStopped(MonoBehaviour wgo)
    {
        if (ApplyingRemoteChop || !Connected() || wgo == null || _localCrafting.Count == 0) return;
        EnsureReflection();
        if (_uniqueIdField == null || _isCraftingField == null) return;
        try
        {
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            if (!_localCrafting.TryGetValue(uid, out var claim)) return;
            var cc = GetCraftComponent(wgo);
            if (cc == null) return;
            if (Convert.ToBoolean(_isCraftingField.GetValue(cc)))
            {
                claim.time = Time.realtimeSinceStartup;
                _localCrafting[uid] = claim;
                return;
            }
            _localCrafting.Remove(uid);
            _lastSentProgressQ.Remove(uid);
            SendCraftClaim(uid, false, "");
        }
        catch { }
    }

    // Періодичний тік (з SteamManager.Update): ре-claim живих крафтів (тримає TTL напарника),
    // прибирання мертвих (пропущений стоп: cancel через GUI, знесення станції тощо).
    public static void TickCraftClaims()
    {
        if (_localCrafting.Count == 0) { _claimRefreshTimer = 0f; return; }
        _claimRefreshTimer += Time.deltaTime;
        if (_claimRefreshTimer < CRAFT_CLAIM_REFRESH) return;
        _claimRefreshTimer = 0f;
        EnsureReflection();
        var uids = new List<long>(_localCrafting.Keys);
        foreach (var uid in uids)
        {
            try
            {
                var wgo = FindWgoByUniqueId(uid);
                var cc  = wgo != null ? GetCraftComponent(wgo) : null;
                bool crafting = cc != null && _isCraftingField != null &&
                                Convert.ToBoolean(_isCraftingField.GetValue(cc));
                if (crafting)
                {
                    var claim = _localCrafting[uid];
                    claim.time = Time.realtimeSinceStartup;
                    _localCrafting[uid] = claim;
                    SendCraftClaim(uid, true, claim.craftId);
                }
                else
                {
                    _localCrafting.Remove(uid);
                    _lastSentProgressQ.Remove(uid);
                    SendCraftClaim(uid, false, "");
                }
            }
            catch { }
        }
    }

    private static void SendCraftClaim(long uid, bool start, string craftId)
    {
        if (!Connected()) return;
        try
        {
            var idB = System.Text.Encoding.UTF8.GetBytes(craftId ?? "");
            if (idB.Length > 255) idB = new byte[0];
            var packet = new byte[11 + idB.Length];
            packet[0] = 0x18;
            BitConverter.GetBytes(uid).CopyTo(packet, 1);
            packet[9]  = (byte)(start ? 1 : 0);
            packet[10] = (byte)idB.Length;
            Buffer.BlockCopy(idB, 0, packet, 11, idB.Length);
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
            Multiplayer.Log?.LogInfo($"[CHOP] Claim станції uid={uid}: {(start ? "СТАРТ " + craftId : "СТОП")} (0x18)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] SendCraftClaim: {e.Message}"); }
    }

    public static void ApplyRemoteCraftClaim(long uid, bool start, string craftId)
    {
        if (!start) { _remoteCrafting.Remove(uid); ClearRemoteProgressBar(uid); return; }
        // ГОНКА одночасного старту (вікно ~RTT): обидва стартували й обмінялись claim.
        // Детермінований tiebreak: ХОСТ виграє. Хост ігнорує чужий claim (клієнт сам скасує,
        // отримавши хостів); клієнт робить mini-cancel свого крафту й реєструє блок.
        if (_localCrafting.ContainsKey(uid))
        {
            if (SteamNetwork.Role == NetworkRole.Host)
            {
                Multiplayer.Log?.LogWarning($"[CHOP] Гонка стартів uid={uid}: я хост — лишаюсь власником");
                return;
            }
            Multiplayer.Log?.LogWarning($"[CHOP] Гонка стартів uid={uid}: поступаюсь хосту (mini-cancel, витрачені матеріали втрачено)");
            MiniCancelLocalCraft(uid);
        }
        _remoteCrafting[uid] = new CraftClaim { craftId = craftId, time = Time.realtimeSinceStartup };
    }

    // ── ЕТАП 3b: ЖИВИЙ ПРОГРЕС-БАР У НАПАРНИКА (0x18 flag=2) ─────────────────────────────────
    // Власник шле квантований прогрес (10%-кроки) з Postfix CraftComponent.DoAction. Приймач
    // ставить wgo.progress (та сама властивість, з якої малює рідний бар) і інжектить
    // BubbleWidgetProgressData у слот CraftingProgress. is_crafting НЕ чіпаємо (інакше
    // ReallyUpdateComponent сам затікає is_auto = подвійний драйв, декомпіл 84155). Рідний
    // RefreshComponentBubbleData стирає бар при is_crafting=false (84633) → Postfix повертає.
    private static readonly Dictionary<long, int> _lastSentProgressQ = new Dictionary<long, int>();
    private static readonly Dictionary<long, object> _remoteBarWidgets = new Dictionary<long, object>();

    // Джерело для ProgressDelegate бару: читає живий wgo.progress (оновлюється пакетами).
    private sealed class RemoteProgressSource
    {
        public MonoBehaviour wgo;
        public float Get()
        {
            try
            {
                return wgo != null && _wgoProgressProp != null
                    ? Convert.ToSingle(_wgoProgressProp.GetValue(wgo)) : 0f;
            }
            catch { return 0f; }
        }
    }

    // Postfix CraftComponent.DoAction (кожен робочий тік власника): прогрес на 10%-кроках.
    public static void OnLocalCraftProgressTick(object craftComponent)
    {
        if (ApplyingRemoteChop || !Connected() || craftComponent == null) return;
        if (_localCrafting.Count == 0) return;
        try
        {
            var wgo = _componentWgoProp?.GetValue(craftComponent) as MonoBehaviour;
            if (wgo == null || _uniqueIdField == null || _wgoProgressProp == null) return;
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            if (!_localCrafting.ContainsKey(uid)) return;
            float p = Convert.ToSingle(_wgoProgressProp.GetValue(wgo));
            int q = Mathf.Clamp((int)(p * 10f), 0, 10);
            if (_lastSentProgressQ.TryGetValue(uid, out var prev) && prev == q) return;
            _lastSentProgressQ[uid] = q;

            var packet = new byte[11];
            packet[0] = 0x18;
            BitConverter.GetBytes(uid).CopyTo(packet, 1);
            packet[9]  = 2;                       // flag=2: прогрес
            packet[10] = (byte)(q * 10);          // 0-100
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
        }
        catch { }
    }

    // Приймач прогресу: wgo.progress + бар + освіження TTL claim-а (прогрес = сигнал життя).
    public static void ApplyRemoteCraftProgress(long uid, float p)
    {
        EnsureReflection();
        // Прогрес без claim (пропущений старт) — зареєструвати блок захисно: станція явно працює.
        if (_remoteCrafting.TryGetValue(uid, out var claim))
        {
            claim.time = Time.realtimeSinceStartup;
            _remoteCrafting[uid] = claim;
        }
        else
            _remoteCrafting[uid] = new CraftClaim { craftId = "", time = Time.realtimeSinceStartup };
        try
        {
            var wgo = FindWgoByUniqueId(uid);
            if (wgo == null || _wgoProgressProp == null) return;
            _wgoProgressProp.SetValue(wgo, Mathf.Clamp01(p));
            EnsureRemoteProgressBar(wgo, uid);
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] ApplyRemoteCraftProgress: {e.Message}"); }
    }

    // Рідний віджет бару в слот CraftingProgress (той, що гра ставить при is_crafting).
    // Делегат читає wgo.progress → бар живе сам, пакети лише рухають значення.
    private static void EnsureRemoteProgressBar(MonoBehaviour wgo, long uid)
    {
        if (_setBubbleWidgetDataMethod == null || _bubbleProgressDataType == null ||
            _progressDelegateType == null || _widgetIdCraftingProgress == null) return;
        try
        {
            if (!_remoteBarWidgets.TryGetValue(uid, out var wdata))
            {
                var src = new RemoteProgressSource { wgo = wgo };
                var del = Delegate.CreateDelegate(_progressDelegateType, src,
                              typeof(RemoteProgressSource).GetMethod("Get"));
                wdata = Activator.CreateInstance(_bubbleProgressDataType, del, 0, 2);
                _remoteBarWidgets[uid] = wdata;
            }
            _setBubbleWidgetDataMethod.Invoke(wgo, new object[] { wdata, _widgetIdCraftingProgress });
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] EnsureRemoteProgressBar: {e.Message}"); }
    }

    // Стоп/звільнення станції: прибрати бар (слот → null, як робить сама гра в 84633).
    private static void ClearRemoteProgressBar(long uid)
    {
        if (!_remoteBarWidgets.Remove(uid)) return;
        try
        {
            var wgo = FindWgoByUniqueId(uid);
            if (wgo != null && _setBubbleWidgetDataMethod != null && _widgetIdCraftingProgress != null)
                _setBubbleWidgetDataMethod.Invoke(wgo, new object[] { null, _widgetIdCraftingProgress });
        }
        catch { }
    }

    // Postfix CraftComponent.RefreshComponentBubbleData: рідний рефреш стер бар (is_crafting=false
    // у нас) → повертаємо, поки станція під активним claim напарника.
    public static void ReinjectRemoteProgressBar(object craftComponent)
    {
        if (craftComponent == null || _remoteCrafting.Count == 0) return;
        try
        {
            var wgo = _componentWgoProp?.GetValue(craftComponent) as MonoBehaviour;
            if (wgo == null || _uniqueIdField == null) return;
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            if (!_remoteCrafting.TryGetValue(uid, out var claim)) return;
            if (Time.realtimeSinceStartup - claim.time > CRAFT_CLAIM_TTL) return;
            EnsureRemoteProgressBar(wgo, uid);
        }
        catch { }
    }

    // Програна гонка: зняти власний крафт без завершення. Матеріали вже списані CraftReally —
    // прийнята втрата (рідкісний випадок, лог попереджає). Прогрес не чіпаємо (старт скидає в 0).
    private static void MiniCancelLocalCraft(long uid)
    {
        _localCrafting.Remove(uid);
        try
        {
            var wgo = FindWgoByUniqueId(uid);
            if (wgo == null) return;
            var cc = GetCraftComponent(wgo);
            if (cc == null) return;
            _isCraftingField?.SetValue(cc, false);
            _currentCraftField?.SetValue(cc, null);
            if (_redrawBubbleMethod != null)
            {
                var pars = _redrawBubbleMethod.GetParameters();
                _redrawBubbleMethod.Invoke(wgo, pars.Length == 1 ? new object[] { null } : new object[0]);
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] MiniCancelLocalCraft: {e.Message}"); }
    }

    private static bool IsPlayerActor(MonoBehaviour actor)
    {
        if (actor == null) return false;
        EnsureReflection();
        if (_isPlayerField != null)
        {
            try { if ((bool)_isPlayerField.GetValue(actor)) return true; } catch { }
        }
        return actor.gameObject.name.StartsWith("Player");
    }

    private static bool Connected() =>
        SteamNetwork.IsConnected && SteamNetwork.IsInGame && SteamNetwork.RemoteID != 0;

    // type(1) + x(4) + y(4) + amount(4) = 13 байт (amount не використовується для 0x0A)
    private static void SendTreePacket(byte type, float x, float y, float amount)
    {
        var packet = new byte[13];
        packet[0] = type;
        BitConverter.GetBytes(x).CopyTo(packet, 1);
        BitConverter.GetBytes(y).CopyTo(packet, 5);
        BitConverter.GetBytes(amount).CopyTo(packet, 9);
        SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
    }

    // ── Відправка: локальний гравець ударив дерево/камінь (з DoActionSyncPatch) ─
    public static void OnLocalChop(MonoBehaviour wgo, MonoBehaviour actor, float amount)
    {
        if (ApplyingRemoteChop || !Connected()) return;
        if (!IsDestroySyncTarget(wgo) || !IsPlayerActor(actor)) return;
        var p = wgo.transform.position;
        SendTreePacket(0x09, p.x, p.y, amount);
    }

    // ── Відправка: обʼєкт відпрацьований (DropItems) — транслюємо зміну стану ──
    // IsTransformSyncTarget: дерева, камені, могили, грядки — усе що переходить
    // у after_hp_0 (пеньок / grave_empty / garden_empty). Жили (steep_*) не сюди
    // — вони не зникають, синк лише їх луту через 0x0B.
    public static void OnTreeFelled(MonoBehaviour wgo)
    {
        if (ApplyingRemoteChop || !Connected()) return;
        if (!IsTransformSyncTarget(wgo)) return;
        var p = wgo.transform.position;
        SendTreePacket(0x0A, p.x, p.y, 0f);
        Multiplayer.Log?.LogInfo($"[CHOP] Обʼєкт відпрацьовано @({p.x:F1},{p.y:F1}) — транслюємо зміну стану");
    }

    // ═══ B-2 (підхід A): ПОВНА РЕПЛІКАЦІЯ СТАНУ МОГИЛИ (0x0D) ════════════════════
    // Тест #2 довів: реплей лише візуал-методу (RedrawPart) НЕ працює — `_data`
    // приймача незмінний, тож щокадровий UpdateTransparentParts відкочує картинку.
    // Рішення: серіалізувати ВЕСЬ WGO (FromWGO → SerializableWGO, [Serializable])
    // і на приймачі RestoreFromSerializedObject — стан (item/_data) стає ідентичним,
    // і та сама щокадрова перемалювка тепер малює ПРАВИЛЬНО (стає союзником). Той
    // самий принцип, що передача сейву, але для 1 обʼєкта. Лут лишається на 0x0B.
    // Тригер — RedrawPart/ReplaceWithObject (зміна стадії могили). Ідемпотентно:
    // повторне відновлення того ж стану нешкідливе, тож дублі тригерів не страшні.
    // Формат 0x0D: type(1) uid(8) x(4) y(4) blobLen(4) blob(BinaryFormatter SerializableWGO)
    private static bool GraveStateReflectionReady() =>
        _fromWgoMethod != null && _restoreFromSerializedMethod != null && _uniqueIdField != null
        && _swObjIdField != null && _swItemField != null
        && _dataField != null && _toJsonMethod != null;

    // РОЗВІДКА ВЛАСНИКА (2026-06-08, ВІДКИНУТО): worker-поля WGO (_linked_worker/
    // _has_linked_worker/linked_worker_unique_id/worker_unique_id) виявились ЗАВЖДИ
    // порожні при тригері (лайв-тест: 0 непорожніх на обох машинах), а резолв uid
    // гравця давав 0 → ідентичність як сигнал власності НЕ годиться. Лік дюпу зроблено
    // інакше — echo-приглушення по підпису стадії (нижче), власник не потрібен.

    // Чи є тіло в інвентарі могили (надійний сигнал гри GetBodyFromInventory, не json-парсинг).
    // Додається в підпис стадії БІТОМ, щоб кладення/виймання тіла міняло підпис (key-only його
    // не ловив — тіло не в _res_type). На відправнику береться з живого wgo; на приймачі — з біта
    // в пакеті (variation), бо приймач на момент дедупу ще НЕ застосував стан.
    private static bool GraveHasBody(MonoBehaviour wgo)
    {
        if (_getBodyFromInventoryMethod == null || wgo == null) return false;
        try { return _getBodyFromInventoryMethod.Invoke(wgo, new object[] { true }) != null; }
        catch { return false; }
    }

    // === ПІДПИС СТАДІЇ МОГИЛИ (2026-06-08) ===
    // Сирий json має per-frame шум (для ОДНІЄЇ стадії: 13587/13587/13602… → копач слав
    // 14 станів/могилу, глядач робив 13 RestoreFromSerializedObject = мигання рамки).
    // Реальна стадія = obj_id + набір частин у _params (_res_type — ключі, _res_v —
    // значення 1→0 коли частину викопано). Дедуп+echo-чек по цьому підпису: 1 відправка
    // + 1 застосування на РЕАЛЬНУ стадію. Фолбек на повний json якщо _params не знайдено.
    private static string GraveStageSig(string objId, string json)
    {
        string rt = ExtractJsonArray(json, "_res_type");
        if (rt == null) return (objId ?? "") + "|" + json;  // фолбек: _params не знайдено
        // НАБІР КЛЮЧІВ grave-частин (БЕЗ значень) — навмисно key-only (2026-06-10).
        // Ключі добре розрізняють і стадії копання, і furniture-предмети: рамка=grave_bot_stn_1,
        // хрест=grave_top_stn_plate_1 — РІЗНІ ключі, тож 2-й предмет дає інший підпис → синкається.
        // ЗНАЧЕННЯ (presence 0/1) НЕ беремо НАВМИСНО: транзієнт розбору «предмет ще в інвентарі +
        // value<1» гра малює як ДЕРЕВ'ЯНУ рамку (grave_*_building_1_stg_1, декомпіл OnDrawGrave/
        // GetNewWOPPrefabsNames 88883) — якби підпис розрізняв значення, цей транзієнт синкався б і
        // глядач блимав деревʼяною рамкою при РОЗБОРІ (баг тесту #5, спостереження Zonda). Key-only
        // його дедупає (той самий ключ при v0 і v1 → 1 синк). Стале echo-приглушення повної могили
        // зі старту (ті самі ключі) лікує ВІКНО ECHO_SUPPRESS_WINDOW (тест довів: значення НЕ
        // рятувало — старт і відбудова обидва мали plate-ключ; вікно — справжній фікс 2-го предмета).
        var parts = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(rt, "\"(grave[^\"]*)\""))
            parts.Add(m.Groups[1].Value);
        parts.Sort();
        return (objId ?? "") + "|" + string.Join(",", parts);
    }


    // Витягує "[...]" одразу після ключа (перше входження). _res_type/_res_v — плоскі
    // масиви (рядки/інти), без вкладених дужок, тож перша ']' закриває. null якщо нема.
    private static string ExtractJsonArray(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return null;
        int k = json.IndexOf(key, StringComparison.Ordinal);
        if (k < 0) return null;
        int open = json.IndexOf('[', k);
        if (open < 0) return null;
        int close = json.IndexOf(']', open);
        if (close < 0) return null;
        return json.Substring(open, close - open + 1);
    }

    // Форсує furniture-предмети могили (рамка/хрест, id="grave_*") у ІНВЕНТАРІ до param≥1, щоб гра
    // малювала завершений камінь, а не деревʼяний каркас «під будівництвом» (grave_*_building_1,
    // OnDrawGrave/GetNewWOPPrefabsNames 88883 малює його при GetParam(item.id)<1). Лише в ГЛЯДАЧА
    // після restore: він не будує, тож не має показувати каркас. Тіло (id="body") пропускаємо;
    // викопані частини вже НЕ в інвентарі → не чіпаємо (зникають коректно, копання ціле).
    private static void ForceGraveFurnitureComplete(MonoBehaviour wgo)
    {
        if (_itemInventoryField == null || _itemIdField == null
            || _getParamMethod == null || _setParamMethod == null || _dataField == null) return;
        try
        {
            var data = _dataField.GetValue(wgo);
            if (data == null) return;
            var inv = _itemInventoryField.GetValue(data) as System.Collections.IList;
            if (inv == null) return;
            foreach (var it in inv)
            {
                if (it == null) continue;
                string id = _itemIdField.GetValue(it) as string;
                if (id == null || !id.StartsWith("grave")) continue;  // furniture = grave_*; тіло "body" — ні
                float p = Convert.ToSingle(_getParamMethod.Invoke(wgo, new object[] { id, 0f }));
                if (p < 1f) _setParamMethod.Invoke(wgo, new object[] { id, 1f });
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[CHOP] furniture-complete впав: {e.Message}"); }
    }

    // === МОНОТОННІСТЬ СТАДІЙ МОГИЛИ (2026-06-08, фікс leak B / дюпу) ===
    // Копання могили НЕОБОРОТНЕ й завжди йде ВПЕРЕД: grave_ground (всі частини) → частини
    // зникають по одній → grave_exhume → grave_empty. Тож вхідний стан, що ДОДАЄ частину
    // назад або знижує obj_id, — це стейл/луна; його застосування регідратує _data
    // активної могили → гра ре-кидає частину щокадру (лайв-тест 2026-06-08: 66 ре-дропів
    // однієї плити = піраміда дюпу). Відхиляємо такі «назад»-стани на ЗАСТОСУВАННІ.
    private static int GraveObjRank(string objId)
    {
        if (string.IsNullOrEmpty(objId)) return 0;
        if (objId.StartsWith("grave_empty")) return 2;
        if (objId.StartsWith("grave_exhume")) return 1;
        return 0; // grave_ground та інші ранні під-стадії
    }

    // Набір ключів частин могили (grave_*) із сирого json стану (через _res_type).
    private static HashSet<string> GraveParts(string json)
    {
        var set = new HashSet<string>();
        string rt = ExtractJsonArray(json, "_res_type");
        if (rt == null) return set;
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(rt, "\"(grave[^\"]*)\""))
            set.Add(m.Groups[1].Value);
        return set;
    }

    // Тригер зміни стадії могили → шлемо СТАН як JSON. Транспорт = Item.ToJSON(0)
    // (RECON 2026-06-07: depth 0 = повний стан _data — _params частин + inventory;
    // стискається зі стадіями 13587→...→732). Зворотнє на приймачі — JsonUtility.
    // FromJsonOverwrite у локальний Item (типи/формули цілі лишаються валідні).
    // Формат 0x0D: type(1) uid(8) x(4) y(4) variation(4) objIdLen(2) objId jsonLen(4) json
    public static void OnLocalGraveStateChanged(MonoBehaviour wgo)
    {
        if (ApplyingRemoteChop || !Connected()) return;
        if (!IsStateRepTarget(wgo)) return;   // могила АБО синкнута будівля (Фаза 2)
        // ГЕЙТ БЛИЗЬКОСТІ для верстатів (фікс флуду bat_test, лайв-тест 2026-06-11): has_craft
        // обʼєктів у світі багато (дев-тестові bat_test розкидані по всій мапі), і на завантаженні
        // всі фаєрять RedrawPart → сплеск 0x0D, який приймач навіть не може застосувати (обʼєкт
        // не в його завантаженій зоні → "не знайдено"). Стан верстата реплікуємо лише коли
        // локальний гравець ПОРУЧ = реальний крафт/взаємодія. Могили/будівлі не чіпаємо: їх
        // гравець копає/ставить впритул, вони не авто-операційні й не розкидані фоном.
        if (IsWorkbench(wgo) && !IsGraveTarget(wgo) && !NearLocalPlayer(wgo, WORKBENCH_SYNC_RANGE))
            return;
        EnsureReflection();
        if (!GraveStateReflectionReady()) return;
        try
        {
            var data = _dataField.GetValue(wgo);
            if (data == null) return;
            string json  = _toJsonMethod.Invoke(data, new object[] { 0 }) as string ?? "";
            if (json.Length == 0) return;
            string objId = _objIdField.GetValue(wgo) as string ?? "";
            long uid     = Convert.ToInt64(_uniqueIdField.GetValue(wgo));

            // Підпис СТАДІЇ (не сирий json — він має per-frame шум, лайв-тест 2026-06-08).
            // + БІТ ТІЛА (фікс 2026-06-10): тіло сидить в інвентарі, НЕ в _res_type, тож key-only
            // підпис кладення тіла в grave_empty не міняв → echo/дедуп ковтав 0x0D → тіло не синкалось
            // напарнику й виштовхувалось назад. Біт несеться в пакеті (поле variation, біт0).
            bool hasBody = GraveHasBody(wgo);
            // + ХЕШ ВМІСТУ ВЕРСТАТА (Фаза 2 крафт): вихід/матеріали сидять в інвентарі, не в obj_id,
            // тож key-only підпис при крафті не міняється → дедуп глушив би. Хеш у пакеті (variation біти1+).
            // МОГИЛИ ВИКЛЮЧЕНО (вони теж has_craft): їх покриває body-біт + key-sig — перевірений
            // 8 ітераціями шлях; додавання inv-хешу ризикувало б echo-приглушенням (повернення
            // дюп-петлі 2026-06-08), якби restore хоч якось нормалізував інвентар.
            int invH = (IsWorkbench(wgo) && !IsGraveTarget(wgo)) ? WorkbenchInvHash(wgo) : 0;
            string sig = GraveStageSig(objId, json) + (hasBody ? "|B" : "") + (invH != 0 ? "|w" + invH : "");

            // ECHO-ПРИГЛУШЕННЯ (фікс дюпу): якщо це РІВНО та стадія, яку ми щойно отримали
            // й застосували для цього uid — це наша ж луна (ми глядач), НЕ власна зміна.
            // Не шлемо назад → петля копач↔глядач рветься в корені. Дюп луту (24 плити в
            // месивному тесті) був НАСЛІДКОМ цієї петлі: копач застосовував відлунений стан
            // на свою активну могилу → ре-кидав плиту. Echo-guard тут безсилий (луна =
            // окремий мережевий пакет, не ре-ентрі в тім же кадрі). Лайв-тест 2026-06-08:
            // глядач відлунював 1 стан, копач застосовував 1 — латентна петля, каскадила в месиві.
            if (_lastAppliedGraveSig.TryGetValue(uid, out var appliedSig) && appliedSig == sig
                && _lastAppliedGraveSigTime.TryGetValue(uid, out var appliedAt)
                && UnityEngine.Time.realtimeSinceStartup - appliedAt < ECHO_SUPPRESS_WINDOW)
                return;  // echo-приглушення: це наша НЕДАВНЯ луна (застосували цей стан як глядач щойно).
                         // Вікно ECHO_SUPPRESS_WINDOW: стейл applied-підпис (хвилини тому) НЕ глушить
                         // чесну зміну з тим самим підписом (фікс відбудови могили 2-м предметом 2026-06-10).

            // Сюди доходить лише ГЕНУЇННА локальна зміна (луну відсік echo-чек вище) → я
            // активно копаю цю могилу. Оновлюємо вікно власника ЩОРАЗУ (до дедупу нижче, бо
            // при стабільній стадії дедуп виходить раніше й мітка б протухала — це й був баг).
            _lastLocalGraveDigTime[uid] = UnityEngine.Time.realtimeSinceStartup;

            // ДЕДУП стадії: тригерів кілька (RedrawPart/OnWorkFinished/OnCraftStateChanged),
            // частина ловить стейлову/проміжну стадію з мікро-різницею json. Шлемо лише коли
            // РЕАЛЬНА стадія змінилась → 1 відправка/стадія (було ~14/могилу = мигання рамки).
            if (_lastSentGraveSig.TryGetValue(uid, out var prev) && prev == sig) return;
            _lastSentGraveSig[uid] = sig;

            var p = wgo.transform.position;

            var idB   = System.Text.Encoding.UTF8.GetBytes(objId);
            var dataB = System.Text.Encoding.UTF8.GetBytes(json);
            if (idB.Length > 65535) return;

            var packet = new byte[25 + idB.Length + 4 + dataB.Length];
            packet[0] = 0x0D;
            BitConverter.GetBytes(uid).CopyTo(packet, 1);
            BitConverter.GetBytes(p.x).CopyTo(packet, 9);
            BitConverter.GetBytes(p.y).CopyTo(packet, 13);
            BitConverter.GetBytes((hasBody ? 1 : 0) | (invH << 1)).CopyTo(packet, 17);  // variation: біт0 = тіло, біти1-30 = хеш вмісту верстата
            int off = 21;
            BitConverter.GetBytes((ushort)idB.Length).CopyTo(packet, off); off += 2;
            Buffer.BlockCopy(idB, 0, packet, off, idB.Length); off += idB.Length;
            BitConverter.GetBytes(dataB.Length).CopyTo(packet, off); off += 4;
            Buffer.BlockCopy(dataB, 0, packet, off, dataB.Length);
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
            Multiplayer.Log?.LogInfo($"[CHOP] Могила uid={uid} obj={objId} — синк СТАНУ (json={dataB.Length}б)");

            // Кооп-крафт Етап 1: для верстата піггібечимо чергу (старт/завершення міняють craft_queue,
            // а ці тригери не EnqueueCraft). SendCraftQueue сам дедупить — зайвого не шле.
            if (IsWorkbench(wgo)) SendCraftQueue(wgo);
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] OnLocalGraveStateChanged: {e.Message}"); }
    }

    // ── Прийом 0x09: повторити удар (косметика — трясіння) ──────────────────
    public static void ApplyRemoteChop(float x, float y, float amount)
    {
        EnsureReflection();
        if (!_ready) return;

        var target = FindTargetNear(x, y, out float dist);
        if (target == null || dist > POSITION_EPSILON)
        {
            Multiplayer.Log?.LogWarning($"[CHOP] Обʼєкт @({x:F1},{y:F1}) не знайдено " +
                $"(найближче: {(target != null ? dist.ToString("F1") : "немає")})");
            return;
        }

        var localPlayer = GetLocalPlayerWgo();
        if (localPlayer == null) { Multiplayer.Log?.LogWarning("[CHOP] WGO гравця не знайдено"); return; }

        ApplyingRemoteChop = true;
        try { _doActionMethod.Invoke(target, new object[] { localPlayer, amount }); }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] DoAction впав: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
    }

    // Пошук WGO за unique_id (надійніше за позицію — без epsilon-промахів). Кеш скану.
    private static MonoBehaviour FindWgoByUniqueId(long uid)
    {
        EnsureReflection();
        if (!_ready || _uniqueIdField == null) return null;
        foreach (var comp in ScanWgosCached())
        {
            var mb = comp as MonoBehaviour;
            if (mb == null) continue;
            try { if (Convert.ToInt64(_uniqueIdField.GetValue(mb)) == uid) return mb; } catch { }
        }
        return null;
    }

    // ── Прийом 0x0D: реплікація стану могили через item_data ─────────────────────
    // Знаходимо могилу за unique_id (fallback — позиція). Беремо ЛОКАЛЬНУ
    // SerializableWGO цілі (FromWGO — з валідними формулами/посиланнями цієї машини),
    // підміняємо лише item_data/obj_id/variation на отримані, item занулюємо (щоб
    // restore відбудував _data з item_data), і RestoreFromSerializedObject. Echo-guard
    // глушить зворотну трансляцію (restore смикне RedrawPart → Postfix вийде рано).
    public static void ApplyRemoteGraveState(long uid, float x, float y,
                                             string objId, int variation, string json)
    {
        EnsureReflection();
        if (!_ready || !GraveStateReflectionReady()) return;

        // ЗАХИСТ від псування: НЕ застосовуємо порожній стан (порожнім ми вже стирали
        // _data → «No data» / порожні ями, лайв-тест 2026-06-07). Краще нічого.
        if (string.IsNullOrEmpty(json))
        {
            Multiplayer.Log?.LogWarning($"[CHOP] 0x0D: json ПОРОЖНІЙ для uid={uid} — пропускаю (не псуємо могилу)");
            return;
        }

        // РЕКОНСИЛЯЦІЯ ТЕРМІНАЛЬНОГО СТАНУ (фікс застряглої «-1» при co-dig, 2026-06-08):
        // grave_empty — кінцевий стан (частин нема → runaway/ре-дроп неможливий). Тому його
        // застосовуємо ЗАВЖДИ, в обхід owner-guard. При co-dig обидва owner-guard'и блокують
        // стани одне одного → контестована могила розходиться й застрягає на grave_ground
        // («-1» в обох — лайв-тест 2026-06-08). Щойно хтось ДОБИВАЄ її локально й шле empty —
        // ця гілка форсовано зводить обидві сторони в порожній стан, розчепивши застрягання.
        bool isTerminal = !string.IsNullOrEmpty(objId) && objId.StartsWith("grave_empty");

        // ГАРД ВЛАСНИКА (фікс runaway-дюпу при co-dig тієї самої могили, 2026-06-08): якщо
        // я САМ генуїнно копав цю могилу в межах GRAVE_OWNER_WINDOW — НЕ застосовую вхідний
        // restore (крім термінального — він безпечний і потрібен для реконсиляції). Інакше
        // restore регідратує _data моєї активної могили → гра ре-кидає частину десятки разів
        // (лог: 88× plate/3.3с). Монотонність тут безсила (при co-dig стадії однакові).
        if (!isTerminal
            && _lastLocalGraveDigTime.TryGetValue(uid, out var dugAt)
            && UnityEngine.Time.realtimeSinceStartup - dugAt < GRAVE_OWNER_WINDOW)
            return;  // я активний копач цієї могили → не застосовую чужий restore (рехідратація → дюп)

        var target = FindWgoByUniqueId(uid);
        if (target == null)
        {
            target = FindTargetNear(x, y, out float dist, IsGraveTarget);
            if (target == null || dist > POSITION_EPSILON)
            {
                Multiplayer.Log?.LogWarning($"[CHOP] 0x0D: могилу uid={uid} @({x:F1},{y:F1}) не знайдено " +
                    $"(найближче: {(target != null ? dist.ToString("F1") : "немає")})");
                return;
            }
        }

        // ДЕДУП на приймачі по підпису СТАДІЇ (не сирий json — той самий шум). Та сама
        // стадія для uid не застосовується двічі → 1 RestoreFromSerializedObject/стадію.
        // ВАЖЛИВО: _lastAppliedGraveSig читає й echo-приглушення у OnLocalGraveStateChanged,
        // тож підпис ОБОВ'ЯЗКОВО той самий (GraveStageSig) на обох шляхах.
        // Біт ТІЛА з пакета (variation біт0) — той самий, що відправник додав у свій підпис. Узгоджено:
        // після restore наша могила теж матиме тіло → майбутній GraveHasBody(wgo) дасть той самий біт.
        // Хеш вмісту верстата з пакета (variation біти1-30) — той самий, що відправник додав у підпис.
        // Узгоджено: після restore наш верстат матиме той самий інвентар → майбутній WorkbenchInvHash збіжиться.
        int invH = variation >> 1;
        string sig = GraveStageSig(objId, json) + (((variation & 1) != 0) ? "|B" : "") + (invH != 0 ? "|w" + invH : "");
        if (_lastAppliedGraveSig.TryGetValue(uid, out var prevSig) && prevSig == sig) return;

        // ГАРД МОНОТОННОСТІ (фікс leak B / дюпу, 2026-06-08) — ТЕПЕР ЛИШЕ ПРИ АКТИВНОМУ
        // КОНТЕСТІ (фікс зворотного синку, 2026-06-10). Раніше діяв БЕЗУМОВНО: копання
        // вважалось незворотним, тож стан «назад» (нижчий obj_id rank або додає частини)
        // відкидався як stale-echo — інакше «назад»-restore регідратував _data й гра ре-кидала
        // плиту щокадру (66 ре-дропів = піраміда). АЛЕ зворотній синк (кладення тіла
        // grave_empty→grave_corp, відбудова →grave_ground) ЗАКОННО йде «вгору» по стадіях, і
        // безумовний гард його різав (лог 2026-06-10: глядач приймав grave_corp/grave_ground,
        // але restore не викликався — спостерігач бачив лише порожню могилу). Тому монотонність
        // діє ТІЛЬКИ якщо Я САМ працював цю могилу в межах GRAVE_CONTEST_WINDOW (тоді «назад»
        // справді = stale-echo/рехідратація мого активного _data). Чистий глядач (не торкався
        // цієї могили) застосовує стани в порядку як є — reliable+ordered гарантує послідовність
        // копання→кладення→відбудова. Читаємо живий стан цілі (істина цієї машини).
        bool recentlyWorked = _lastLocalGraveDigTime.TryGetValue(uid, out var workedAt)
                              && UnityEngine.Time.realtimeSinceStartup - workedAt < GRAVE_CONTEST_WINDOW;
        if (recentlyWorked)
        try
        {
            var locData = _dataField.GetValue(target);
            string localJson  = locData != null ? _toJsonMethod.Invoke(locData, new object[] { 0 }) as string : null;
            string localObjId = _objIdField.GetValue(target) as string;
            if (!string.IsNullOrEmpty(localJson))
            {
                int inRank = GraveObjRank(objId), locRank = GraveObjRank(localObjId);
                if (inRank < locRank) return;                                  // obj_id «назад» — стейл/луна
                if (inRank == locRank && !GraveParts(json).IsSubsetOf(GraveParts(localJson)))
                    return;                                                    // додає частини назад — ре-гідратація
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[CHOP] 0x0D гард монотонності впав (застосовую далі): {e.Message}"); }

        ApplyingRemoteChop = true;
        try
        {
            // Локальна SerializableWGO цілі (валідні типи/формули цієї машини), у її
            // Item вливаємо JSON стану відправника (FromJsonOverwrite → _params/inventory
            // частин оновлюються, OnAfterDeserialize відбудовує вкладене). Підміняємо
            // obj_id (для переходів grave_ground→exhume→empty). RestoreFromSerializedObject
            // застосовує стан + перебудовує візуал одним викликом гри.
            var sw = _fromWgoMethod.Invoke(null, new object[] { target });
            if (sw == null) { Multiplayer.Log?.LogWarning("[CHOP] 0x0D: FromWGO(приймач) = null"); return; }
            if (_fromJsonOverwriteMethod == null) { Multiplayer.Log?.LogWarning("[CHOP] 0x0D: JsonUtility.FromJsonOverwrite нема"); return; }
            var item = _swItemField.GetValue(sw);
            if (item != null)
            {
                _fromJsonOverwriteMethod.Invoke(null, new object[] { json, item });
            }
            else
            {
                // Запасний шлях: вливаємо просто в живий _data цілі.
                var data = _dataField.GetValue(target);
                if (data == null) { Multiplayer.Log?.LogWarning("[CHOP] 0x0D: _data цілі = null"); return; }
                _fromJsonOverwriteMethod.Invoke(null, new object[] { json, data });
                _swItemField.SetValue(sw, data);
            }
            if (!string.IsNullOrEmpty(objId)) _swObjIdField.SetValue(sw, objId);
            _restoreFromSerializedMethod.Invoke(target, new object[] { sw, false });
            // СПОСТЕРІГАЧ НЕ БУДУЄ → не показувати каркас «під будівництвом». Гра малює деревʼяний
            // каркас (grave_*_building_1, OnDrawGrave/GetNewWOPPrefabsNames 88883) для furniture-
            // предмета в інвентарі з param<1 (транзієнт розбору, який будівник проскакує, а глядач
            // отримує знімком і малює). Форсуємо param=1 furniture-предметам У ІНВЕНТАРІ → завжди
            // завершений камінь. Викопані предмети ВЖЕ не в інвентарі → не чіпаємо → зникають
            // коректно (копання не ламається). Має бути ПЕРЕД редравом нижче. Лайв-баг тесту #7.
            ForceGraveFurnitureComplete(target);
            // ФОРС-РЕДРАВ МЕБЛІВ (фікс 2026-06-10): RestoreFromSerializedObject кладе предмети
            // (тіло/рамка GraveFence/хрест GraveStone) в _data.inventory + робить SetObject (база),
            // але НЕ малює меблі-візуал і НЕ рефрешить grave quality. Лайв-тест #3: стан 12721б із
            // рамкою застосовувався (`відновлено ✓`), та рамка не зʼявлялась і quality застрягало на
            // «-1» у глядача. Гра малює меблі через Redraw(force_redraw:true)→custom_drawers.
            // OnObjectRedraw (декомпіл 114060) — кличемо його тут. Безпечно від re-send: ще під
            // echo-guard ApplyingRemoteChop (GraveRedrawSyncPatch.OnLocalGraveStateChanged вийде рано).
            if (_redrawMethod != null)
                try { _redrawMethod.Invoke(target, new object[] { true, false, false }); }
                catch (Exception re) { Multiplayer.Log?.LogWarning($"[CHOP] 0x0D форс-редрав впав: {re.Message}"); }
            _lastGraveRestoreTime[uid] = UnityEngine.Time.realtimeSinceStartup;  // per-uid: гасити runaway-дроп цієї могили в глядача
            _lastAppliedGraveSig[uid] = sig;
            _lastAppliedGraveSigTime[uid] = UnityEngine.Time.realtimeSinceStartup;  // мітка часу для ВІКНА echo-приглушення
            // Термінальний стан: могила завершена → прибираємо все відстеження для uid, щоб
            // нічого не висіло (owner-вікно, артефакт-restore, per-stage дедуп). Грейв спокійний.
            if (isTerminal)
            {
                _lastLocalGraveDigTime.Remove(uid);
                _lastGraveRestoreTime.Remove(uid);
                _graveSentStageSig.Remove(uid);
                _graveSentParts.Remove(uid);
            }
            Multiplayer.Log?.LogInfo($"[CHOP] Могила uid={uid} obj={objId} стан відновлено ✓ (json={json.Length}ch)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] 0x0D RestoreFromSerializedObject впав: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
    }

    // Розбір пакета 0x0D → ApplyRemoteGraveState.
    public static void ParseAndApplyGraveOp(byte[] data)
    {
        if (data == null || data.Length < 25) { Multiplayer.Log?.LogWarning($"[CHOP] 0x0D замалий: {data?.Length}"); return; }
        EnsureReflection();
        if (!GraveStateReflectionReady()) { Multiplayer.Log?.LogWarning("[CHOP] 0x0D: serialize-API не готове"); return; }
        try
        {
            long uid = BitConverter.ToInt64(data, 1);
            float x  = BitConverter.ToSingle(data, 9);
            float y  = BitConverter.ToSingle(data, 13);
            int variation = BitConverter.ToInt32(data, 17);
            int off  = 21;
            int idLen = BitConverter.ToUInt16(data, off); off += 2;
            if (off + idLen + 4 > data.Length) { Multiplayer.Log?.LogWarning("[CHOP] 0x0D objId за межами"); return; }
            string objId = System.Text.Encoding.UTF8.GetString(data, off, idLen); off += idLen;
            int dataLen = BitConverter.ToInt32(data, off); off += 4;
            if (dataLen < 0 || off + dataLen > data.Length) { Multiplayer.Log?.LogWarning("[CHOP] 0x0D itemData за межами"); return; }
            string itemData = System.Text.Encoding.UTF8.GetString(data, off, dataLen);
            ApplyRemoteGraveState(uid, x, y, objId, variation, itemData);
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] ParseAndApplyGraveOp: {e.Message}"); }
    }

    // ── Прийом 0x0A: обʼєкт зрубано/розбито — прибираємо його тут ────────────
    public static void ApplyRemoteDestroy(float x, float y)
    {
        EnsureReflection();
        if (!_ready) return;

        var target = FindTargetNear(x, y, out float dist, IsTransformSyncTarget);
        if (target == null || dist > POSITION_EPSILON)
        {
            // Обʼєкт ще не «розбуджений» — гравець у іншій локації. Запамʼятовуємо,
            // знищимо в RetryPendingDestroys, коли він підійде й воно завантажиться.
            var pos = new Vector2(x, y);
            if (!_pendingDestroys.Any(p => Vector2.Distance(p, pos) < POSITION_EPSILON))
            {
                _pendingDestroys.Add(pos);
                Multiplayer.Log?.LogInfo($"[CHOP] Обʼєкт @({x:F1},{y:F1}) ще не завантажений " +
                    $"— у чергу відкладених ({_pendingDestroys.Count})");
            }
            return;
        }
        Multiplayer.Log?.LogInfo($"[CHOP] Прибираємо зрубаний обʼєкт @({x:F1},{y:F1}) ✓");
        FellTarget(target);
    }

    // Прибирає зрубаний обʼєкт так само, як гра. Для дерева: спершу програємо
    // анімацію падіння (TreeDisappearAnimation.StartAnimation), а пеньок ставимо
    // у колбеку її завершення. Для каменю / готового пенька — одразу.
    private static void FellTarget(MonoBehaviour target)
    {
        string nextId    = ResolveAfterHp0(target);   // дерево → "tree_X_stump"; камінь → ""
        int    variation = ReadVariation(target);

        if (TryPlayFallAnimation(target, nextId, variation)) return;
        FinishFell(target, nextId, variation);
    }

    // Зчитує obj_def.after_hp_0 — id обʼєкта-наступника. null/порожнє = наступника нема.
    private static string ResolveAfterHp0(MonoBehaviour wgo)
    {
        if (_objDefField == null || _afterHp0Field == null || _afterHp0GetValue == null)
            return null;
        try
        {
            var objDef   = _objDefField.GetValue(wgo);
            var afterHp0 = objDef != null ? _afterHp0Field.GetValue(objDef) : null;
            if (afterHp0 == null) return null;
            return _afterHp0GetValue.Invoke(afterHp0, new object[] { wgo, null }) as string;
        }
        catch { return null; }
    }

    private static int ReadVariation(MonoBehaviour wgo)
    {
        if (_variationField == null) return 0;
        try { return Convert.ToInt32(_variationField.GetValue(wgo)); }
        catch { return 0; }
    }

    // Дерево: запускаємо анімацію падіння; коли вона завершиться — колбек
    // FinishFell поставить пеньок. true = анімація стартувала (FellTarget виходить).
    private static bool TryPlayFallAnimation(MonoBehaviour wgo, string nextId, int variation)
    {
        if (_treeDisappearType == null || _startAnimationMethod == null
            || _voidDelegateType == null)
            return false;
        try
        {
            // Каменю/пенька цього компонента нема — тоді не дерево, анімації не буде.
            var anim = wgo.GetComponentInChildren(_treeDisappearType, true);
            if (anim == null) return false;

            var completion = new FellCompletion { Wgo = wgo, NextId = nextId, Variation = variation };
            var onDone = Delegate.CreateDelegate(_voidDelegateType, completion,
                typeof(FellCompletion).GetMethod(nameof(FellCompletion.OnFallDone)));
            _startAnimationMethod.Invoke(anim, new object[] { onDone });
            Multiplayer.Log?.LogInfo($"[CHOP] Анімація падіння дерева запущена → потім '{nextId}'");
            return true;
        }
        catch (Exception e)
        {
            Multiplayer.Log?.LogWarning($"[CHOP] Анімація падіння не вдалась: {e.Message}");
            return false;
        }
    }

    // Фінал зрубування: пеньок через ReplaceWithObject; нема наступника → Destroy.
    private static void FinishFell(MonoBehaviour wgo, string nextId, int variation)
    {
        if (wgo == null) return;
        try
        {
            if (!string.IsNullOrEmpty(nextId) && _replaceWithObjectMethod != null)
            {
                // ApplyingRemoteChop глушить echo, якщо ReplaceWithObject смикне патч.
                ApplyingRemoteChop = true;
                try { _replaceWithObjectMethod.Invoke(wgo, new object[] { nextId, true, variation }); }
                finally { ApplyingRemoteChop = false; }
                Multiplayer.Log?.LogInfo($"[CHOP] Обʼєкт → '{nextId}' (пеньок) ✓");
            }
            else
            {
                UnityEngine.Object.Destroy(wgo.gameObject);
                Multiplayer.Log?.LogInfo("[CHOP] Обʼєкт знищено ✓");
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] FinishFell: {e.Message}"); }
    }

    // Контекст для VoidDelegate-колбека завершення анімації падіння дерева.
    private class FellCompletion
    {
        public MonoBehaviour Wgo;
        public string        NextId;
        public int           Variation;
        public void OnFallDone() => FinishFell(Wgo, NextId, Variation);
    }

    // Періодично пробуємо знищити обʼєкти з черги — коли гравець доходить до
    // локації, обʼєкт «прокидається» як WorldGameObject і стає знаходимим.
    public static void RetryPendingDestroys()
    {
        if (_pendingDestroys.Count == 0) return;
        EnsureReflection();
        if (!_ready) return;

        for (int i = _pendingDestroys.Count - 1; i >= 0; i--)
        {
            var pos = _pendingDestroys[i];
            var target = FindTargetNear(pos.x, pos.y, out float dist, IsTransformSyncTarget);
            if (target != null && dist <= POSITION_EPSILON)
            {
                Multiplayer.Log?.LogInfo($"[CHOP] Відкладене прибирання обʼєкта @({pos.x:F1},{pos.y:F1}) ✓");
                FellTarget(target);
                _pendingDestroys.RemoveAt(i);
            }
        }
    }

    // ── СИНК ЛУТА (0x0B) ─────────────────────────────────────────────────────
    // Локальний DropItems → відсилка 0x0B (список Item-ів + Direction + координати).
    // На приймачі — реплей того ж DropItems під ApplyingRemoteChop echo-guard.
    // Кожна машина матиме локальний DropResGameObject; коли буде синк інвентаря,
    // подія підбирання видалить локальний дроп без додавання предметів.
    private static bool DropReflectionReady() =>
        _itemType != null && _itemIdField != null && _itemValueField != null
        && _directionType != null && _dropItemsMethod != null;

    // Освіжає owner-вікно могили на КОЖНОМУ тіку локальної роботи (DoAction щокадру поки
    // тримаєш F). Критично: work-сесія (і перший шкідливий вхідний restore) починається
    // РАНІШЕ за першу зміну стадії, тому ставити мітку лише в OnLocalGraveStateChanged пізно
    // — restore встигав рехідратувати _data активної могили → смужка роботи застрягала повна
    // й гра повторно «завершувала» роботу, видаючи gravestone знов і знов (лайв-тест Zonda
    // 2026-06-08). Звідси: поки я САМ копаю могилу, owner-guard має блокувати ВСІ її restore.
    public static void NoteLocalGraveWork(MonoBehaviour wgo)
    {
        if (ApplyingRemoteChop) return;   // це реплей, не моя робота — не вважати власником
        EnsureReflection();
        if (!_ready || wgo == null || _objIdField == null || _uniqueIdField == null) return;
        var oid = _objIdField.GetValue(wgo) as string;
        if (oid == null || !oid.StartsWith("grave")) return;
        try { _lastLocalGraveDigTime[Convert.ToInt64(_uniqueIdField.GetValue(wgo))] = UnityEngine.Time.realtimeSinceStartup; }
        catch { }
    }

    // Викликається з PREFIX DropItems. true → СКАСУВАТИ локальний дроп гри (і Postfix не
    // транслює). Лікує локальну піраміду в ГЛЯДАЧА: після restore гра ре-кидає частину
    // могили десятки разів (runaway). Postfix-дедуп ловив лише мережу, а предмет уже
    // спавнився локально — тому рішення тут, ДО виклику гри.
    // Логіка (лише для могил, лише генуїнні локальні дропи — реплей/restore-внутрішнє пускаємо):
    //   • Я НЕ копаю цю могилу, але її нещодавно регідратували вхідним станом → артефакт
    //     restore (owner-guard не дає restore на МОЮ активну могилу, тож recent-restore = я
    //     глядач) → скасувати. Свою копію глядач дістає через реплей шеред-луту.
    //   • Backstop: per-stage дедуп — кожну частину могила віддає 1× на стадію; повтор → скасувати.
    public static bool ShouldSuppressGraveDrop(MonoBehaviour wgo, object itemsList)
    {
        if (ApplyingRemoteChop) return false;          // реплей шеред-луту / restore-внутрішнє — пускаємо
        EnsureReflection();
        if (!_ready || wgo == null || _objIdField == null || _uniqueIdField == null
            || _toJsonMethod == null || _dataField == null) return false;
        var oid = _objIdField.GetValue(wgo) as string ?? "";
        if (!oid.StartsWith("grave")) return false;    // лише могили
        long uid;
        try { uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo)); } catch { return false; }

        float now = UnityEngine.Time.realtimeSinceStartup;
        bool amDigging = _lastLocalGraveDigTime.TryGetValue(uid, out var dugAt)
                         && now - dugAt < GRAVE_OWNER_WINDOW;

        // Артефакт restore у глядача: не я копаю + могилу щойно регідратували → runaway.
        if (!amDigging && _lastGraveRestoreTime.TryGetValue(uid, out var rt)
            && now - rt < GRAVE_RESTORE_ARTIFACT_WINDOW)
            return true;

        // Backstop per-stage: кожну частину 1× на стадію (на випадок якщо вікно артефакту
        // протухло, а runaway триває; для копача — захист від повторів у межах стадії).
        var gdata = _dataField.GetValue(wgo);
        string stageSig = GraveStageSig(oid, gdata != null ? _toJsonMethod.Invoke(gdata, new object[] { 0 }) as string ?? "" : "");
        if (!_graveSentStageSig.TryGetValue(uid, out var prevStage) || prevStage != stageSig)
        {
            _graveSentStageSig[uid] = stageSig;
            _graveSentParts[uid] = new HashSet<string>();
        }
        var sent = _graveSentParts[uid];
        bool anyNew = false;
        var list = itemsList as System.Collections.IList;
        if (list != null)
            foreach (var it in list)
            {
                if (it == null) continue;
                var id = _itemIdField.GetValue(it) as string;
                if (string.IsNullOrEmpty(id)) continue;
                if (!sent.Contains(id)) { anyNew = true; sent.Add(id); }
            }
        if (!anyNew && list != null && list.Count > 0) return true;  // повтор частини на стадії — runaway
        return false;
    }

    // ТРУП — ЄДИНИЙ ХУК на `DropResGameObject.Drop` (статичний): спільний шлях УСІХ спавнів трупа —
    // і ексгумація (WGO.DropItem→Drop), і кидок із рук (DropOverheadItem→Drop напряму). Раніше
    // хукали лише WGO.DropItem → кидок із рук не ловився, труп зникав у напарника. Тепер ловимо все.
    // Реєструємо ТОЧНИЙ заспавнений GO (__result) — без сканування «найближчого». uid синтетичний
    // (Ticks, унікальний на власника; передається в 0x11, приймач використовує його як ключ). Стан
    // тіла (органи+freshness) їде в JSON (ToJSON(0)). Пакет 0x11: type uid dir x y z force b4 jsonLen json.
    public static void OnCorpseDropped(object[] dropArgs, object resultGO)
    {
        EnsureReflection();
        if (!DropReflectionReady() || dropArgs == null || dropArgs.Length < 2) return;
        var item = dropArgs[1];                                   // Drop(pos, Item, parent, dir, force, curve, walls, stacked)
        if (item == null || (_itemIdField.GetValue(item) as string) != "body") return;
        var go = resultGO as MonoBehaviour;
        if (go == null) return;

        if (ApplyingRemoteChop)
        {
            // Це наш реплей (ApplyRemoteCorpseSpawn) → реєструємо під переданим uid, owner=False.
            long ruid = _incomingCorpseUid ?? DateTime.UtcNow.Ticks;
            RegisterCorpseBodyGO(go, ruid, owner: false);
            return;
        }
        if (!Connected()) return;

        // Локальний спавн (ексгумація АБО кидок із рук) → новий uid, реєстрація власника, 0x11.
        long uid = DateTime.UtcNow.Ticks;
        RegisterCorpseBodyGO(go, uid, owner: true);

        var pos = go.transform.position;                          // фактична позиція заспавненого трупа
        int dir = (dropArgs.Length > 3 && dropArgs[3] != null) ? Convert.ToInt32(dropArgs[3]) : 0;
        float force = (dropArgs.Length > 4 && dropArgs[4] != null) ? Convert.ToSingle(dropArgs[4]) : 1f;
        bool walls = false;                                       // приймач спавнить ТОЧНО на pos (без репозиції)
        string json = "";
        try { if (_toJsonMethod != null) json = _toJsonMethod.Invoke(item, new object[] { 0 }) as string ?? ""; }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[CHOP] труп ToJSON впав: {e.Message}"); }
        var jb = System.Text.Encoding.UTF8.GetBytes(json);

        var pk = new byte[34 + jb.Length];
        pk[0] = 0x11;
        BitConverter.GetBytes(uid).CopyTo(pk, 1);
        BitConverter.GetBytes(dir).CopyTo(pk, 9);
        BitConverter.GetBytes(pos.x).CopyTo(pk, 13);
        BitConverter.GetBytes(pos.y).CopyTo(pk, 17);
        BitConverter.GetBytes(pos.z).CopyTo(pk, 21);
        BitConverter.GetBytes(force).CopyTo(pk, 25);
        pk[29] = (byte)(walls ? 1 : 0);
        BitConverter.GetBytes(jb.Length).CopyTo(pk, 30);
        Buffer.BlockCopy(jb, 0, pk, 34, jb.Length);
        SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, pk);
        Multiplayer.Log?.LogInfo($"[CHOP] Спавн трупа → uid={uid} pos=({pos.x:F1},{pos.y:F1}) json={jb.Length}б → 0x11");
    }

    // Реєстрація трупа за ТОЧНИМ GameObject (з хука Drop) — без сканування сцени.
    public static void RegisterCorpseBodyGO(MonoBehaviour go, long uid, bool owner)
    {
        if (go == null) return;
        var existing = FindBodyByUid(uid);
        if (existing != null && existing.go != null) return;
        if (_bodies.Any(b => ReferenceEquals(b.go, go))) return;   // вже відстежується
        var p = go.transform.position;
        _bodies.RemoveAll(b => b.uid == uid);
        _bodies.Add(new BodyTrack {
            go = go, uid = uid, lastPos = new Vector2(p.x, p.y),
            registeredTime = Time.time, lastRemoteTime = -99f, lastSendTime = -99f, isOwner = owner
        });
        Multiplayer.Log?.LogInfo($"[CHOP] Труп зареєстровано → uid={uid} owner={owner} pos=({p.x:F1},{p.y:F1})");
    }

    // Приймач 0x11: відтворити ПОВНИЙ труп на граві глядача через ту саму DropItem (під
    // echo-guard). Тіло відновлюємо з JSON (органи+freshness), а не порожнє new Item.
    // Постфікс DropItemSingularBroadcastPatch підхопить його в реєстр (owner=False).
    public static void ApplyRemoteCorpseSpawn(long uid, int dir, float x, float y, float z, float force, bool b4, string json)
    {
        EnsureReflection();
        if (!DropReflectionReady() || _dropItemSingularMethod == null || _itemCtor == null) return;
        // Ціль для виклику instance-методу DropItem: могила-джерело (початковий спавн) АБО, якщо її
        // uid не знайдено (re-drop з рук, uid=гравець), будь-який локальний wgo — позиція все одно
        // явна (x,y,z), тож на якому wgo кликати неважливо.
        var target = FindWgoByUniqueId(uid) ?? GetLocalPlayerWgo();
        if (target == null)
        {
            Multiplayer.Log?.LogWarning($"[CHOP] 0x11: ні джерела uid={uid}, ні локального wgo — труп не заспавнено");
            return;
        }
        // Уже є живий труп цього uid? (повторний 0x11) — не дублюємо.
        var existed = FindBodyByUid(uid);
        if (existed != null && existed.go != null)
        {
            Multiplayer.Log?.LogInfo($"[CHOP] 0x11: труп uid={uid} вже існує — пропуск");
            return;
        }
        ApplyingRemoteChop = true;
        _incomingCorpseUid = uid;   // RegisterCorpseBody (постфікс) зареєструє під цим uid, owner=False
        try
        {
            var body = _itemCtor.Invoke(new object[] { "body", 1 });
            // Влити повний стан трупа (органи/freshness) з JSON відправника.
            if (!string.IsNullOrEmpty(json) && _fromJsonOverwriteMethod != null)
            {
                try { _fromJsonOverwriteMethod.Invoke(null, new object[] { json, body }); }
                catch (Exception je) { Multiplayer.Log?.LogWarning($"[CHOP] 0x11 FromJsonOverwrite впав: {je.Message}"); }
            }
            var direction = Enum.ToObject(_directionType, dir);
            var ps = _dropItemSingularMethod.GetParameters();
            var call = new object[ps.Length];
            if (ps.Length > 0) call[0] = body;
            if (ps.Length > 1) call[1] = direction;
            if (ps.Length > 2) call[2] = new Vector3(x, y, z);
            if (ps.Length > 3) call[3] = force;     // float (декомпіляція: DropItem param 3 = float force)
            if (ps.Length > 4) call[4] = b4;        // bool check_walls
            _dropItemSingularMethod.Invoke(target, call);
            Multiplayer.Log?.LogInfo($"[CHOP] 0x11: труп uid={uid} заспавнено ✓");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] 0x11 спавн трупа впав: {e.Message}"); }
        finally { ApplyingRemoteChop = false; _incomingCorpseUid = null; }
    }

    //               + N * { idLen(1) + id(utf8) + value(4) }
    public static void OnLocalDropItems(MonoBehaviour wgo, object itemsList, object direction)
    {
        if (ApplyingRemoteChop || !Connected()) return;
        if (!IsLootSyncTarget(wgo)) return;
        EnsureReflection();
        if (!DropReflectionReady() || itemsList == null) return;

        try
        {
            var list = itemsList as System.Collections.IList;
            if (list == null || list.Count == 0) return;

            // Збір (id, value) — мовчки пропускаємо порожні id (мало б не бути).
            var triples = new List<KeyValuePair<byte[], int>>(list.Count);
            int payload = 0;
            foreach (var it in list)
            {
                if (it == null) continue;
                var id = _itemIdField.GetValue(it) as string;
                if (string.IsNullOrEmpty(id)) continue;
                int val = Convert.ToInt32(_itemValueField.GetValue(it));
                var idBytes = System.Text.Encoding.UTF8.GetBytes(id);
                if (idBytes.Length > 255) continue;       // id невеликі (як wood/branch)
                triples.Add(new KeyValuePair<byte[], int>(idBytes, val));
                payload += 1 + idBytes.Length + 4;
            }
            if (triples.Count == 0 || triples.Count > 255) return;
            // Дедуп/придушення grave-runaway тепер у Prefix DropItems (ShouldSuppressGraveDrop):
            // якщо локальний дроп придушено там, цей Postfix не викликається (прапор у патчі),
            // тож сюди доходять лише легітимні дропи, які треба транслювати.

            var p = wgo.transform.position;
            byte dir = (byte)Convert.ToInt32(direction);

            var packet = new byte[11 + payload];
            packet[0] = 0x0B;
            BitConverter.GetBytes(p.x).CopyTo(packet, 1);
            BitConverter.GetBytes(p.y).CopyTo(packet, 5);
            packet[9]  = dir;
            packet[10] = (byte)triples.Count;
            int off = 11;
            foreach (var kv in triples)
            {
                packet[off++] = (byte)kv.Key.Length;
                Buffer.BlockCopy(kv.Key, 0, packet, off, kv.Key.Length); off += kv.Key.Length;
                BitConverter.GetBytes(kv.Value).CopyTo(packet, off); off += 4;
            }
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
            Multiplayer.Log?.LogInfo($"[CHOP] Лут @({p.x:F1},{p.y:F1}) → {triples.Count} item(ів), dir={dir}");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] OnLocalDropItems: {e.Message}"); }
    }

    // Розбирає 0x0B → готує List<Item> і Direction. Повертає false якщо щось не так.
    public static bool ParseDropPacket(byte[] data, out float x, out float y,
                                       out object itemsList, out object direction)
    {
        x = y = 0f; itemsList = null; direction = null;
        EnsureReflection();
        if (!DropReflectionReady() || data == null || data.Length < 11) return false;
        try
        {
            x = BitConverter.ToSingle(data, 1);
            y = BitConverter.ToSingle(data, 5);
            byte dir   = data[9];
            int  count = data[10];
            int off    = 11;

            var listType = typeof(List<>).MakeGenericType(_itemType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType);
            for (int i = 0; i < count; i++)
            {
                if (off + 1 > data.Length) return false;
                int idLen = data[off++];
                if (off + idLen + 4 > data.Length) return false;
                string id = System.Text.Encoding.UTF8.GetString(data, off, idLen); off += idLen;
                int val = BitConverter.ToInt32(data, off); off += 4;
                list.Add(_itemCtor.Invoke(new object[] { id, val }));
            }
            itemsList = list;
            direction = Enum.ToObject(_directionType, (int)dir);
            return true;
        }
        catch (Exception e)
        {
            Multiplayer.Log?.LogError($"[CHOP] ParseDropPacket: {e.Message}");
            return false;
        }
    }

    public static void ApplyRemoteDrop(float x, float y, object itemsList, object direction)
    {
        EnsureReflection();
        if (!_ready || !DropReflectionReady() || itemsList == null) return;

        var target = FindTargetNear(x, y, out float dist, IsLootSyncTarget);
        if (target == null || dist > POSITION_EPSILON)
        {
            // Обʼєкт ще не «розбуджений» — у чергу. Спробуємо коли підійде.
            var pos = new Vector2(x, y);
            if (!_pendingDrops.Any(d => Vector2.Distance(d.Pos, pos) < POSITION_EPSILON))
            {
                _pendingDrops.Add(new PendingDrop { Pos = pos, Items = itemsList, Direction = direction });
                Multiplayer.Log?.LogInfo($"[CHOP] Лут @({x:F1},{y:F1}) відкладено ({_pendingDrops.Count})");
            }
            return;
        }

        // ВЛАСНИК-ЛУТ (фікс 2× при co-dig тієї самої могили, 2026-06-08): якщо ціль —
        // могила, яку я САМ зараз активно копаю (owner-вікно), НЕ реплею вхідний лут: я
        // вже роблю власну копію цієї частини локально. Без цього co-dig дає по 2 копії на
        // машину (своя + напарника). Нормальний спільний лут (один копає/другий дивиться)
        // НЕ зачіпається: глядач не копає → owner-вікно порожнє → отримує копію як і раніше.
        if (IsGraveTarget(target) && _uniqueIdField != null)
        {
            try
            {
                long guid = Convert.ToInt64(_uniqueIdField.GetValue(target));
                if (_lastLocalGraveDigTime.TryGetValue(guid, out var dugAt)
                    && UnityEngine.Time.realtimeSinceStartup - dugAt < GRAVE_OWNER_WINDOW)
                    return;  // я сам копаю цю могилу → роблю свою копію, чужий лут не дублюю
            }
            catch { }
        }

        InvokeDropItems(target, itemsList, direction);
    }

    private static void InvokeDropItems(MonoBehaviour target, object itemsList, object direction)
    {
        // Реплей чужого DropItems під echo-guard ApplyingRemoteChop (щоб приймач не
        // транслював його назад). Лут спільний — приймач збирає свою копію.
        int expected = (itemsList as System.Collections.IList)?.Count ?? -1;
        ApplyingRemoteChop = true;
        try { _dropItemsMethod.Invoke(target, new[] { itemsList, direction }); }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] DropItems впав: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
        Multiplayer.Log?.LogInfo($"[CHOP] Лут реплеєно ✓ (спільний режим: обидва отримують ~{expected} item(ів))");
    }

    // ── СИНК ПЕРЕНОСИМОГО ТРУПА (0x0F позиція / 0x10 видалення) ──────────────────────
    // Розвідка 2026-06-09 (F5-трейс, повний цикл взяти→нести→кинути→покласти в могилу):
    // труп при підборі НЕ знищується — гра тримає той самий DropResGameObject і НЕСЕ його з
    // гравцем (range-check id=body триває після DestroyLinkedHint; зникає лише при кладенні
    // через ReplaceWithObject). Тому модель «знищити копію в глядача» була ХИБНА. Правильно:
    // труп завжди живий в обох; машина, де його ЛОКАЛЬНО рухають (несуть/кидають), транслює
    // позицію за uid могили-джерела (uid СПІЛЬНИЙ — доведено синком стадій); інша дзеркалить
    // свою копію. Зник у носія (кладення/споживання) → 0x10 → глядач чисто видаляє
    // (DestroyLinkedHint прибирає й показник свіжості). Хук рук не потрібен — рух сам визначає
    // власника. Foothold для інвентаря (опора C) і для будь-яких переносимих (колода/камінь).
    private class BodyTrack
    {
        public MonoBehaviour go;       // локальний DropResGameObject(body)
        public long uid;               // uid могили-джерела (спільний ключ)
        public Vector2 lastPos;
        public float registeredTime;   // для відстою фізики при спавні
        public float lastRemoteTime;   // коли востаннє рухнули за 0x0F (echo-guard)
        public float lastSendTime;     // throttle відправки
        public bool alive = true;
        public bool isOwner;           // true = заспавнений РЕАЛЬНОЮ ексгумацією (не 0x0B-реплеєм)
    }
    private static readonly List<BodyTrack> _bodies = new List<BodyTrack>();
    private static MethodInfo _destroyLinkedHintMethod;
    private static FieldInfo _highlightedDropField;   // DropResGameObject.currently_higlighted_obj (static) — який дроп зараз підбираємо

    private const float BODY_SETTLE_SEC   = 1.5f;   // відстій фізики після спавну (не слати рух)
    private const float BODY_ECHO_SEC     = 0.4f;   // після 0x0F-руху не слати назад
    private const float BODY_SEND_MIN_SEC = 0.06f;  // throttle ~16/с
    private const float BODY_MOVE_EPS     = 0.1f;   // поріг «рухнувся»

    private static BodyTrack FindBodyByUid(long uid)
    {
        for (int i = 0; i < _bodies.Count; i++) if (_bodies[i].uid == uid) return _bodies[i];
        return null;
    }

    // Чи цей дроп — труп-ДЗЕРКАЛО (owner=False): мирориться з машини носія. Локально його НЕ
    // можна авто-збирати, інакше зникає (лайв-тест 2026-06-09: GONE через 1 кадр у глядача —
    // авто-магніт глядачевого гравця хапав свіжий труп; у власника не хапає, бо той щойно
    // завершив «роботу»-ексгумацію й авто-збір тимчасово приглушений).
    public static bool IsMirrorCorpse(object drop)
    {
        if (drop == null) return false;
        for (int i = 0; i < _bodies.Count; i++)
            if (!_bodies[i].isOwner && ReferenceEquals(_bodies[i].go, drop)) return true;
        return false;
    }

    // uid трупа-ДЗЕРКАЛА (owner=false) за посиланням на його наземний GO. Для передачі власності:
    // коли глядач підіймає мирор, треба знати uid, щоб сказати власнику прибрати його наземну копію.
    private static bool TryGetMirrorUid(object go, out long uid)
    {
        uid = 0;
        if (go == null) return false;
        for (int i = 0; i < _bodies.Count; i++)
            if (!_bodies[i].isOwner && ReferenceEquals(_bodies[i].go, go)) { uid = _bodies[i].uid; return true; }
        return false;
    }

    private static float _lastMirrorBlockLog = -99f;
    public static void NoteMirrorCollectBlocked()
    {
        if (Time.time - _lastMirrorBlockLog < 2f) return;   // throttle (CanCollectDrop дзвонить часто)
        _lastMirrorBlockLog = Time.time;
        Multiplayer.Log?.LogInfo("[CHOP] Авто-збір трупа-дзеркала заблоковано ✓");
    }

    // uid, під яким реєструвати труп під час 0x11-реплею (для re-drop wgo-uid ≠ переданий uid).
    private static long? _incomingCorpseUid;

    // ── НОСІННЯ OVERHEAD (0x12 несе / 0x13 поклав) ───────────────────────────────────
    // Розвідка коду: важкі предмети (труп/колода/камінь) несуться через
    // BaseCharacterComponent.SetOverheadItem(Item). Хук локального гравця → транслюємо, щоб
    // напарник показав предмет над головою НАШОГО клона. Доповнює наземний мирор (0x0F/0x11).
    public static void OnLocalOverheadChanged(object item)
    {
        if (ApplyingRemoteChop || !Connected()) return;
        if (item == null)
        {
            var p = new byte[1] { 0x13 };
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, p);
            Multiplayer.Log?.LogInfo("[CHOP] Overhead поклав → 0x13");
            return;
        }
        EnsureReflection();
        // ПЕРЕДАЧА ВЛАСНОСТІ ТРУПА: якщо предмет, який ми щойно підняли над головою — це наш
        // труп-ДЗЕРКАЛО з землі (DropResGameObject.currently_higlighted_obj у мить SetOverheadItem
        // ще НЕ занулений грою — обнуляється рядком пізніше, декомпіл TryOtherInteractions 81559→81562),
        // то власність переходить до НАС. Шлемо 0x14 {uid}, щоб ВЛАСНИК прибрав свій наземний труп
        // (інакше дубль). Далі несемо (0x12 нижче); кинемо → Drop-хук зробить нас owner=true (0x11→мирор).
        try
        {
            if (_highlightedDropField == null)
                _highlightedDropField = AccessTools.TypeByName("DropResGameObject")
                    ?.GetField("currently_higlighted_obj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var hl = _highlightedDropField?.GetValue(null);
            if (hl != null && TryGetMirrorUid(hl, out long muid))
            {
                var tp = new byte[9]; tp[0] = 0x14;
                BitConverter.GetBytes(muid).CopyTo(tp, 1);
                SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, tp);
                Multiplayer.Log?.LogInfo($"[CHOP] Підняли труп-дзеркало uid={muid} → 0x14 (передача власності)");
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[CHOP] 0x14 send впав: {e.Message}"); }
        string icon = "";
        try { if (_getOverheadIconMethod != null) icon = _getOverheadIconMethod.Invoke(item, null) as string ?? ""; }
        catch { }
        var ib = System.Text.Encoding.UTF8.GetBytes(icon);
        if (ib.Length > 255) return;
        var pk = new byte[2 + ib.Length];
        pk[0] = 0x12;
        pk[1] = (byte)ib.Length;
        Buffer.BlockCopy(ib, 0, pk, 2, ib.Length);
        SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, pk);
        Multiplayer.Log?.LogInfo($"[CHOP] Overhead несе icon={icon} → 0x12");
    }

    // Реєстрація при спавні трупа (на ОБОХ машинах: реальна ексгумація в одного, 0x0B-реплей в
    // іншого — обидва кличуть WGO.DropItem(body)). Знаходить щойно створене тіло (найближче до
    // могили, ще не зареєстроване) і прив'язує до uid могили. БЕЗ гейту ApplyingRemoteChop.
    public static void RegisterCorpseBody(MonoBehaviour graveWgo, object item)
    {
        EnsureReflection();
        if (!DropReflectionReady() || graveWgo == null || item == null || _uniqueIdField == null) return;
        if ((_itemIdField.GetValue(item) as string) != "body") return;
        // Під час 0x11-реплею uid приходить із пакета (для re-drop wgo — це локальний гравець,
        // чий uid ≠ логічному uid трупа). Інакше — uid джерела (могили) при реальному дропі.
        long uid;
        if (_incomingCorpseUid.HasValue) uid = _incomingCorpseUid.Value;
        else { try { uid = Convert.ToInt64(_uniqueIdField.GetValue(graveWgo)); } catch { return; } }
        // Уже є живий трек для цього uid? (DropItem міг злетіти двічі) — не дублюємо.
        var existing = FindBodyByUid(uid);
        if (existing != null && existing.go != null) return;
        var t = AccessTools.TypeByName("DropResGameObject");
        if (t == null) return;
        var gp = graveWgo.transform.position;
        MonoBehaviour best = null; float bestD = float.MaxValue;
        foreach (var c in UnityEngine.Object.FindObjectsOfType(t))
        {
            var mb = c as MonoBehaviour;
            if (mb == null) continue;
            if (_bodies.Any(b => ReferenceEquals(b.go, mb))) continue;   // вже відстежується
            if (ReadDropItemId(mb) != "body") continue;
            float d = Vector2.Distance(new Vector2(mb.transform.position.x, mb.transform.position.y),
                                       new Vector2(gp.x, gp.y));
            if (d < bestD) { bestD = d; best = mb; }
        }
        if (best == null) return;
        var p = best.transform.position;
        // ВЛАСНИК = машина, де труп заспавнено РЕАЛЬНОЮ ексгумацією (ApplyingRemoteChop==false).
        // Реплей-копія (0x0B) — НЕ власник, лише дзеркалить. Тільки власник транслює 0x0F/0x10:
        // без цього глядачева копія (фізика/авто-збір) слала спурйозне 0x10 і стирала справжній труп.
        bool owner = !ApplyingRemoteChop;
        _bodies.RemoveAll(b => b.uid == uid);   // замінити мертвий трек того ж uid
        _bodies.Add(new BodyTrack {
            go = best, uid = uid, lastPos = new Vector2(p.x, p.y),
            registeredTime = Time.time, lastRemoteTime = -99f, lastSendTime = -99f, isOwner = owner
        });
        Multiplayer.Log?.LogInfo($"[CHOP] Труп зареєстровано → uid={uid} owner={owner} dist={bestD:F1} " +
            $"bodyPos=({p.x:F1},{p.y:F1}) gravePos=({gp.x:F1},{gp.y:F1})");
    }

    // Читає id предмета з DropResGameObject (поле типу Item з .id:string).
    private static string ReadDropItemId(MonoBehaviour drop)
    {
        if (drop == null) return null;
        foreach (var f in drop.GetType().GetFields(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var v = f.GetValue(drop);
            var idF = v?.GetType().GetField("id");
            if (idF != null && idF.FieldType == typeof(string)) return idF.GetValue(v) as string;
        }
        return null;
    }

    // Чисте видалення дропа: спершу прибрати «прив'язану підказку» (показник свіжості — інакше
    // він лишається привидом після грубого Destroy), потім знищити сам GameObject.
    private static void CleanDestroyDrop(MonoBehaviour drop)
    {
        if (drop == null) return;
        if (_destroyLinkedHintMethod == null)
        {
            var t = AccessTools.TypeByName("DropResGameObject");
            _destroyLinkedHintMethod = t?.GetMethod("DestroyLinkedHint",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }
        try { _destroyLinkedHintMethod?.Invoke(drop, null); } catch { }
        UnityEngine.Object.Destroy(drop.gameObject);
    }

    // Per-frame тік (з SteamManager.Update): для кожного локального трупа — якщо рухнувся
    // (несуть/кидають) і це не відстій/луна, транслюємо позицію (0x0F); якщо GO зник
    // (покладено в могилу/спожито) — транслюємо видалення (0x10) і чистимо трек.
    public static void TickCarriedBodies()
    {
        if (_bodies.Count == 0) return;
        float now = Time.time;
        for (int i = _bodies.Count - 1; i >= 0; i--)
        {
            var b = _bodies[i];
            if (b.go == null)   // знищено локально (кладення/споживання АБО авто-збір копії)
            {
                // ТІЛЬКИ власник транслює видалення. Глядачева копія могла зникнути від фізики/
                // авто-збору — НЕ можна слати 0x10 (саме це стирало справжній труп у власника).
                if (b.alive && b.isOwner && Connected())
                {
                    var rm = new byte[9];
                    rm[0] = 0x10;
                    BitConverter.GetBytes(b.uid).CopyTo(rm, 1);
                    SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, rm);
                    Multiplayer.Log?.LogInfo($"[CHOP] Труп uid={b.uid} зник у власника → 0x10");
                }
                else
                    Multiplayer.Log?.LogInfo($"[DIAG-BODY] uid={b.uid} GONE локально owner={b.isOwner} (0x10 НЕ слемо)");
                _bodies.RemoveAt(i);
                continue;
            }
            var pos = b.go.transform.position;
            var p2 = new Vector2(pos.x, pos.y);
            if (Vector2.Distance(p2, b.lastPos) < BODY_MOVE_EPS) continue;   // не рухався
            float moved = Vector2.Distance(p2, b.lastPos);
            b.lastPos = p2;
            // ТІЛЬКИ власник транслює позицію (глядач лише дзеркалить вхідні 0x0F).
            if (!b.isOwner) continue;
            if (now - b.registeredTime < BODY_SETTLE_SEC) continue;          // відстій фізики
            if (now - b.lastRemoteTime < BODY_ECHO_SEC) continue;           // наша луна (рух від 0x0F)
            if (!Connected()) continue;
            if (now - b.lastSendTime < BODY_SEND_MIN_SEC) continue;          // throttle
            b.lastSendTime = now;
            var pk = new byte[17];
            pk[0] = 0x0F;
            BitConverter.GetBytes(b.uid).CopyTo(pk, 1);
            BitConverter.GetBytes(pos.x).CopyTo(pk, 9);
            BitConverter.GetBytes(pos.y).CopyTo(pk, 13);
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, pk);
            Multiplayer.Log?.LogInfo($"[DIAG-BODY] uid={b.uid} рух {moved:F1} → 0x0F @({pos.x:F1},{pos.y:F1})");
        }
    }

    // Приймач 0x0F: посунути СВОЮ копію трупа (за uid) у трансльовану позицію. Echo-guard:
    // позначаємо lastRemoteTime, щоб тік не відіслав цей рух назад носію.
    public static void ApplyRemoteBodyPos(long uid, float x, float y)
    {
        var b = FindBodyByUid(uid);
        if (b == null || b.go == null)
        {
            Multiplayer.Log?.LogInfo($"[DIAG-BODY] 0x0F uid={uid} @({x:F1},{y:F1}) — копії нема " +
                $"({(b == null ? "трек відсутній" : "go знищено")})");
            return;
        }
        var cur = b.go.transform.position;
        b.go.transform.position = new Vector3(x, y, cur.z);
        b.lastPos = new Vector2(x, y);
        b.lastRemoteTime = Time.time;
        Multiplayer.Log?.LogInfo($"[DIAG-BODY] 0x0F uid={uid} → копію посунуто @({x:F1},{y:F1})");
    }

    // Приймач 0x10: носій поклав/спожив труп → чисто видаляємо свою копію (з показником свіжості).
    public static void ApplyRemoteBodyRemove(long uid)
    {
        var b = FindBodyByUid(uid);
        if (b == null) return;
        ApplyingRemoteChop = true;
        try
        {
            if (b.go != null) CleanDestroyDrop(b.go);
            Multiplayer.Log?.LogInfo($"[CHOP] 0x10: труп uid={uid} прибрано ✓ (напарник поклав/спожив)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] 0x10 прибирання впало: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
        _bodies.RemoveAll(t => t.uid == uid);
    }

    // Приймач 0x14: напарник ПІДНЯВ наш труп-дзеркало (передача власності) → прибираємо свій
    // наземний труп (інакше дубль: він несе overhead, а в нас лежить на землі). Труп повернеться
    // НОВИМ дзеркалом через 0x11, коли напарник його кине (тоді він — owner). Видалення те саме,
    // що 0x10, але семантика інша: труп не зник, а перейшов у руки напарника.
    public static void ApplyRemoteCorpseTransfer(long uid)
    {
        var b = FindBodyByUid(uid);
        if (b == null) { Multiplayer.Log?.LogInfo($"[CHOP] 0x14 uid={uid}: трек не знайдено (вже нема)"); return; }
        ApplyingRemoteChop = true;
        try { if (b.go != null) CleanDestroyDrop(b.go); }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] 0x14 прибирання впало: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
        _bodies.RemoveAll(t => t.uid == uid);
        Multiplayer.Log?.LogInfo($"[CHOP] 0x14: труп uid={uid} передано напарнику (він підняв) — наземну копію прибрано ✓");
    }

    // ── ФАЗА 2: СПАВН-ПРИМІТИВ (0x15) ─────────────────────────────────────────
    // wobj, який збираються поставити (захоплений у Prefix DoPlace; cur_floating обнуляється
    // всередині методу, тож у Postfix його вже не прочитати — звідси Prefix-захоплення).
    private static MonoBehaviour _pendingBuildWobj;
    public static void SetPendingBuildWobj(MonoBehaviour wobj) { _pendingBuildWobj = wobj; }

    // Postfix DoPlace: розміщення ВІДБУЛОСЬ, якщо поточний floating-wobj БІЛЬШЕ не той, що захопили
    // (його лишили на сцені → cur_floating став null або новий). Якщо той самий — DoPlace вийшов
    // рано (не вистачило ресурсів / місце зайняте) → нічого не шлемо.
    public static void OnBuildPlaceFinished(MonoBehaviour nowFloatingWobj)
    {
        var placed = _pendingBuildWobj;
        _pendingBuildWobj = null;
        if (placed == null || ReferenceEquals(placed, nowFloatingWobj)) return;
        OnLocalBuildPlaced(placed);
    }

    // Відправка: гравець поставив будівлю/будмайданчик (хук BuildModeLogics.DoPlace) → транслюємо
    // {uid, pos, obj_id}, щоб обʼєкт зʼявився в напарника зі СПІЛЬНИМ uid. Спільний uid критичний:
    // подальші стадії будівництва (ReplaceWithObject placeholder→будівля) синкатимуться по 0x0D за
    // цим uid (наступний крок). Стан плейсхолдера дефолтний → JSON поки НЕ шлемо (свіжий обʼєкт).
    public static void OnLocalBuildPlaced(MonoBehaviour wobj)
    {
        if (ApplyingRemoteChop || !Connected() || wobj == null) return;
        EnsureReflection();
        if (_objIdField == null || _uniqueIdField == null) return;
        try
        {
            string objId = _objIdField.GetValue(wobj) as string;
            if (string.IsNullOrEmpty(objId)) return;
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wobj));
            var pos = wobj.transform.position;

            var idB = System.Text.Encoding.UTF8.GetBytes(objId);
            if (idB.Length > 65535) return;
            var packet = new byte[23 + idB.Length];
            packet[0] = 0x15;
            BitConverter.GetBytes(uid).CopyTo(packet, 1);
            BitConverter.GetBytes(pos.x).CopyTo(packet, 9);
            BitConverter.GetBytes(pos.y).CopyTo(packet, 13);
            BitConverter.GetBytes(pos.z).CopyTo(packet, 17);
            BitConverter.GetBytes((ushort)idB.Length).CopyTo(packet, 21);
            Buffer.BlockCopy(idB, 0, packet, 23, idB.Length);
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
            TrackBuildUid(uid);   // стадії будівництва ЦІЄЇ будівлі підуть по 0x0D (предикат IsStateRepTarget)
            Multiplayer.Log?.LogInfo($"[CHOP] Будівлю поставлено uid={uid} obj={objId} @({pos.x:F1},{pos.y:F1}) → 0x15");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] OnLocalBuildPlaced: {e.Message}"); }
    }

    // Приймач 0x15: спавнимо обʼєкт зі спільним uid. Якщо вже існує за uid (напр. повторний пакет
    // або стадія) — не дублюємо. uid форсимо НАПРЯМУ після спавну (надійно, не залежить від restore).
    public static void ApplyRemoteBuildSpawn(long uid, float x, float y, float z, string objId)
    {
        EnsureReflection();
        if (_spawnWgoMethod == null || _worldRootProp == null || _uniqueIdField == null)
        {
            Multiplayer.Log?.LogWarning("[CHOP] 0x15: spawn-API не готове"); return;
        }
        if (FindWgoByUniqueId(uid) != null)
        {
            Multiplayer.Log?.LogInfo($"[CHOP] 0x15 uid={uid}: обʼєкт уже існує — спавн пропущено");
            return;
        }
        ApplyingRemoteChop = true;
        try
        {
            var worldRoot = _worldRootProp.GetValue(null);
            object posObj = (UnityEngine.Vector3?)new UnityEngine.Vector3(x, y, z);
            var spawned = _spawnWgoMethod.Invoke(null, new object[] { worldRoot, objId, posObj }) as MonoBehaviour;
            if (spawned == null) { Multiplayer.Log?.LogWarning($"[CHOP] 0x15: SpawnWGO({objId}) = null"); return; }
            _uniqueIdField.SetValue(spawned, uid);   // ФОРС спільного uid (перебиває локальний UniqueID.GetUniqueID)
            TrackBuildUid(uid);   // приймемо стадії будівництва цієї будівлі по 0x0D
            Multiplayer.Log?.LogInfo($"[CHOP] 0x15: будівлю obj={objId} uid={uid} заспавнено ✓ @({x:F1},{y:F1})");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] 0x15 спавн впав: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
    }

    // ── ЗНЕСЕННЯ БУДІВЛІ (0x16) — симетрично до спавну ────────────────────────
    // Відправка: трекована будівля знищується локально (хук WGO.DestroyMe) → шлемо {uid},
    // щоб напарник прибрав свою копію. Гейт на _syncedBuildUids (лише наші будівлі) + Connected
    // + !ApplyingRemoteChop (не відлунюємо чужий remove). uid читається ДО знищення (Prefix).
    public static void OnLocalBuildRemoved(MonoBehaviour wgo)
    {
        if (ApplyingRemoteChop || !Connected() || wgo == null || _uniqueIdField == null) return;
        if (_syncedBuildUids.Count == 0) return;
        try
        {
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            if (!_syncedBuildUids.Contains(uid)) return;   // не наша будівля — ігнор
            _syncedBuildUids.Remove(uid);
            var packet = new byte[9];
            packet[0] = 0x16;
            BitConverter.GetBytes(uid).CopyTo(packet, 1);
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
            Multiplayer.Log?.LogInfo($"[CHOP] Будівлю uid={uid} знесено локально → 0x16");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] OnLocalBuildRemoved: {e.Message}"); }
    }

    // Приймач 0x16: напарник зніс будівлю → знищуємо свою копію за uid (під echo-guard).
    public static void ApplyRemoteBuildRemove(long uid)
    {
        EnsureReflection();
        _syncedBuildUids.Remove(uid);
        var target = FindWgoByUniqueId(uid);
        if (target == null) { Multiplayer.Log?.LogInfo($"[CHOP] 0x16 uid={uid}: обʼєкт не знайдено (вже нема)"); return; }
        if (_destroyMeMethod == null) { Multiplayer.Log?.LogWarning("[CHOP] 0x16: DestroyMe-API не готове"); return; }
        ApplyingRemoteChop = true;
        try
        {
            _destroyMeMethod.Invoke(target, null);
            Multiplayer.Log?.LogInfo($"[CHOP] 0x16: будівлю uid={uid} знесено ✓ (напарник прибрав)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] 0x16 знесення впало: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
    }

    public static void RetryPendingDrops()
    {
        if (_pendingDrops.Count == 0) return;
        EnsureReflection();
        if (!_ready) return;

        for (int i = _pendingDrops.Count - 1; i >= 0; i--)
        {
            var d = _pendingDrops[i];
            var target = FindTargetNear(d.Pos.x, d.Pos.y, out float dist, IsLootSyncTarget);
            if (target != null && dist <= POSITION_EPSILON)
            {
                Multiplayer.Log?.LogInfo($"[CHOP] Відкладений лут @({d.Pos.x:F1},{d.Pos.y:F1}) ✓");
                InvokeDropItems(target, d.Items, d.Direction);
                _pendingDrops.RemoveAt(i);
            }
        }
    }

    // Кеш скану всіх WGO. FindObjectsOfType дуже дорогий (ітерує всю сцену),
    // а при бурсті пакетів (напр. друг копає цілий цвинтар → 35 пакетів 0x0D)
    // скан-на-кожен-пакет давав сильний лаг (лайв-тест 2026-06-07). Кешуємо
    // результат на ~1с: бурст ділить один скан замість десятків. Посилання
    // лишаються валідними крізь трансформацію (ReplaceWithObject ПЕРЕвикористовує
    // той самий WGO, не знищує), а знищені обʼєкти ловить null-перевірка нижче.
    private static UnityEngine.Object[] _wgoScanCache;
    private static float _wgoScanCacheTime = -999f;
    private static UnityEngine.Object[] ScanWgosCached()
    {
        if (_wgoScanCache == null || Time.time - _wgoScanCacheTime > 1f)
        {
            _wgoScanCache = UnityEngine.Object.FindObjectsOfType(_wgoType);
            _wgoScanCacheTime = Time.time;
        }
        return _wgoScanCache;
    }

    // Найближчий синхро-обʼєкт до точки (x,y) серед активних WGO. Predicate
    // визначає тип цілі: IsDestroySyncTarget для 0x09/0x0A, IsLootSyncTarget
    // для 0x0B (ширший — пускає й жили), IsGraveTarget для 0x0D.
    private static MonoBehaviour FindTargetNear(float x, float y, out float bestDist,
                                                Func<MonoBehaviour, bool> filter = null)
    {
        bestDist = float.MaxValue;
        EnsureReflection();
        if (!_ready) return null;
        if (filter == null) filter = IsDestroySyncTarget;

        MonoBehaviour best = null;
        var target = new Vector2(x, y);
        foreach (var comp in ScanWgosCached())
        {
            var mb = comp as MonoBehaviour;
            if (mb == null || !filter(mb)) continue;
            var p = mb.transform.position;
            float d = Vector2.Distance(new Vector2(p.x, p.y), target);
            if (d < bestDist) { bestDist = d; best = mb; }
        }
        return best;
    }

    private static MonoBehaviour GetLocalPlayerWgo()
    {
        if (_cachedLocalPlayerWgo != null && _cachedLocalPlayerWgo.gameObject.activeInHierarchy)
            return _cachedLocalPlayerWgo;

        var playerGo = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()
            .FirstOrDefault(mb => mb.GetType().Name == "PlayerComponent"
                                  && mb.gameObject.name != "RemotePlayer_Clone"
                                  && mb.gameObject.name != "Player2_Clone")?.gameObject;
        if (playerGo == null) return null;

        foreach (var mb in playerGo.GetComponents<MonoBehaviour>())
            if (mb.GetType().Name == "WorldGameObject") { _cachedLocalPlayerWgo = mb; break; }
        return _cachedLocalPlayerWgo;
    }
}

// Відправка: кожен DoAction по дереву від локального гравця → пакет 0x09.
[HarmonyPatch]
public static class DoActionSyncPatch
{
    static MethodBase TargetMethod()
    {
        var wgo = AccessTools.TypeByName("WorldGameObject");
        return wgo?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DoAction"
                              && m.GetParameters().Length == 2
                              && m.GetParameters()[1].ParameterType == typeof(float));
    }

    static void Postfix(MonoBehaviour __instance, object[] __args)
    {
        if (__args == null || __args.Length < 2) return;
        float amount;
        try { amount = Convert.ToSingle(__args[1]); } catch { return; }
        ChopSync.OnLocalChop(__instance, __args[0] as MonoBehaviour, amount);
        // Щокадрова робота по могилі (тримання F) → освіжаємо owner-вікно з першого тіку,
        // щоб вхідний restore не рехідратував активну могилу й не ламав work-сесію.
        ChopSync.NoteLocalGraveWork(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// Синк переносимого трупа тепер НЕ хукає підбирання (CanPickupWithInteraction) — модель
// перейшла з «знищити копію при підборі» на дзеркалення позиції (0x0F) + видалення при
// кладенні (0x10), керовані per-frame тіком ChopSync.TickCarriedBodies. Хук рук не потрібен.

// БЛОК ЗБОРУ ТРУПА-ДЗЕРКАЛА (на основі декомпіляції 2026-06-09): коли труп збирається,
// гра ставить is_collected=true, і `DropsList.Add` при наступному дропі робить
// `Object.Destroy(gameObject)` — ОСЬ що знищувало мирорений труп за кілька кадрів. Тож
// блокуємо ОБИДВА гейти збору для трупа-дзеркала (owner=False):
//  1) WorldGameObject.CanCollectDrop(DropResGameObject) → ПОВЕРТАЄ INT (не bool!) — авто-магніт
//     збирає лише коли >0. Форсимо 0. (Старий патч мав фільтр bool → НЕ наклався — баг.)
//  2) DropResGameObject.CanPickupWithInteraction → bool — ручний підбір. Форсимо false.
// Власника НЕ зачіпає (його труп owner=True → IsMirrorCorpse=false).
[HarmonyPatch]
public static class MirrorCorpseCollectBlockPatch
{
    static MethodBase TargetMethod()
    {
        var wgo = AccessTools.TypeByName("WorldGameObject");
        return wgo?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "CanCollectDrop" && m.ReturnType == typeof(int));
    }

    static void Postfix(object[] __args, ref int __result)
    {
        if (__result <= 0 || __args == null || __args.Length == 0) return;
        if (ChopSync.IsMirrorCorpse(__args[0] as MonoBehaviour))
        {
            __result = 0;
            ChopSync.NoteMirrorCollectBlocked();
        }
    }

    static Exception Finalizer(Exception __exception) => null;
}

// ПЕРЕДАЧА ВЛАСНОСТІ ТРУПА (2026-06-10): блок РУЧНОГО підбору трупа-дзеркала ЗНЯТО — глядач
// ТЕПЕР МОЖЕ підняти мирор, і це передає власність (OnLocalOverheadChanged ловить підйом дзеркала
// → 0x14 → власник прибирає наземну копію; кидок глядача → Drop-хук робить його owner). Авто-збір
// (MirrorCorpseCollectBlockPatch вище, CanCollectDrop→0) ЛИШАЄТЬСЯ: передача лише навмисним
// підйомом (кнопка), не авто-магнітом — інакше труп безконтрольно перестрибував би.

// НОСІННЯ OVERHEAD: BaseCharacterComponent.SetOverheadItem(Item) — універсальний механізм
// носіння важких предметів (труп/колода/камінь над головою). Хук ЛОКАЛЬНОГО гравця → транслюємо
// напарнику, щоб показав предмет над головою нашого клона (0x12 несе / 0x13 поклав). Клон гри
// SetOverheadItem не викликає (його BaseCharacterComponent вимкнено), тож хук = завжди локальний;
// ФАЗА 2 — БУДІВНИЦТВО: хук розміщення будівлі. BuildModeLogics.DoPlace (декомпіл 43996) ставить
// плаваюче прев'ю у світ (StopCurrentFloating leave_on_scene → ReplaceWithObject "_place"). cur_floating
// обнуляється ВСЕРЕДИНІ методу, тож wobj захоплюємо в Prefix, а в Postfix визначаємо, чи розміщення
// відбулось (поточний floating != захоплений). Прев'ю вже має unique_id (StopCurrentFloating його читає),
// тож синкаємо за ним. ApplyRemoteBuildSpawn спавнить копію зі спільним uid → стадії підуть по 0x0D.
[HarmonyPatch]
public static class BuildPlacePatch
{
    static FieldInfo _curFloatingField;
    static PropertyInfo _wobjProp;
    static FieldInfo _wobjField;

    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("BuildModeLogics");
        return t?.GetMethod("DoPlace", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    static MonoBehaviour ReadFloatingWobj()
    {
        try
        {
            if (_curFloatingField == null)
            {
                var ft = AccessTools.TypeByName("FloatingWorldGameObject");
                var sf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var inf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _curFloatingField = ft?.GetField("cur_floating", sf) ?? ft?.GetField("_cur_floating", sf);
                _wobjProp  = ft?.GetProperty("wobj", inf);
                _wobjField = ft?.GetField("wobj", inf) ?? ft?.GetField("_wo", inf);
            }
            var cf = _curFloatingField?.GetValue(null);
            if (cf == null) return null;
            return (_wobjProp != null ? _wobjProp.GetValue(cf) : _wobjField?.GetValue(cf)) as MonoBehaviour;
        }
        catch { return null; }
    }

    static void Prefix() { ChopSync.SetPendingBuildWobj(ReadFloatingWobj()); }
    static void Postfix() { ChopSync.OnBuildPlaceFinished(ReadFloatingWobj()); }

    static Exception Finalizer(Exception __exception) => null;
}

// ФАЗА 2 — ЗНЕСЕННЯ: хук WGO.DestroyMe (декомпіл 114595). Prefix читає uid ДО знищення й шле 0x16,
// якщо це трекована будівля (фільтр у OnLocalBuildRemoved: _syncedBuildUids + Connected + !echo).
// DestroyMe — фінальна точка знесення (MarkForRemoval→ProcessRemove→DestroyMe). ВІДОМА МЕЖА:
// масовий DestroyMe при вивантаженні світу теж тригерить, але якщо Connected()=false на виході —
// гейт відсіє; інакше — edge (як вихід-із-трупом), відкладено.
[HarmonyPatch]
public static class BuildRemovePatch
{
    static MethodBase TargetMethod()
    {
        var wgo = AccessTools.TypeByName("WorldGameObject");
        return wgo?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DestroyMe" && m.GetParameters().Length == 0);
    }

    static void Prefix(MonoBehaviour __instance) { ChopSync.OnLocalBuildRemoved(__instance); }

    static Exception Finalizer(Exception __exception) => null;
}

// для певності звіряємо з MainGame.me.player_char.
[HarmonyPatch]
public static class OverheadSyncPatch
{
    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("BaseCharacterComponent");
        return t?.GetMethod("SetOverheadItem",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    static void Postfix(MonoBehaviour __instance, object[] __args)
    {
        try
        {
            var mg = AccessTools.TypeByName("MainGame");
            var fl = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var me = mg?.GetField("me", fl)?.GetValue(null);
            var pc = mg?.GetField("player_char", fl)?.GetValue(me) as MonoBehaviour;
            if (pc != null && !ReferenceEquals(pc, __instance)) return;   // не локальний гравець
        }
        catch { }
        ChopSync.OnLocalOverheadChanged(__args != null && __args.Length > 0 ? __args[0] : null);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// Відправка: DropItems на дереві = воно зрубане → транслюємо знищення (0x0A).
// Postfix не міняє поведінку — звичайна рубка гравця лишається недоторканою.
[HarmonyPatch]
public static class DropItemsBroadcastPatch
{
    static MethodBase TargetMethod()
    {
        var wgo = AccessTools.TypeByName("WorldGameObject");
        return wgo?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DropItems");
    }

    // Прапор «локальний дроп скасовано в Prefix» — щоб Postfix не транслював те, чого гра
    // не дропнула. Синхронно (Prefix→original→Postfix на одному потоці), плоский static ОК.
    static bool _graveDropSuppressed;

    // PREFIX: для grave-runaway скасовуємо САМ виклик гри (повертаємо false) → локальний
    // предмет НЕ спавниться (лікує піраміду в глядача; Postfix цього не міг — предмет уже впав).
    static bool Prefix(MonoBehaviour __instance, object[] __args)
    {
        _graveDropSuppressed = false;
        if (__args != null && __args.Length >= 1 && ChopSync.ShouldSuppressGraveDrop(__instance, __args[0]))
        {
            _graveDropSuppressed = true;
            return false;   // скасувати оригінальний DropItems
        }
        return true;
    }

    static void Postfix(MonoBehaviour __instance, object[] __args)
    {
        if (_graveDropSuppressed) { _graveDropSuppressed = false; return; }  // дроп скасовано → не транслюємо
        // Порядок важливий: ЛУТ (0x0B) шлемо ПЕРШИМ, зміну стану (0x0A) — другою.
        // Пакети reliable+ordered (k_EP2PSendReliable), тож приймач застосує лут
        // поки обʼєкт ще на місці, і лише тоді його трансформує. Інакше для природи
        // (flower→flower_spawner) лут не знаходив дроппера: квітка ставала спавнером
        // (виключений з лут-цілей) → лут зависав у _pendingDrops (лайв-тест 2026-06-01).
        if (__args != null && __args.Length >= 2)
            ChopSync.OnLocalDropItems(__instance, __args[0], __args[1]);
        ChopSync.OnTreeFelled(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// ТРУП — ЄДИНИЙ ХУК на статичний `DropResGameObject.Drop(Vector3, Item, Transform, Direction,
// float, int, bool, bool)`: спільний шлях УСІХ спавнів трупа (ексгумація: WGO.DropItem→Drop;
// кидок із рук: DropOverheadItem→Drop напряму). Постфікс дає __result = заспавнений DropResGameObject.
// Фільтр id=="body" у OnCorpseDropped. Замінив старий хук WGO.DropItem (не ловив кидок із рук).
[HarmonyPatch]
public static class CorpseDropBroadcastPatch
{
    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("DropResGameObject");
        return t?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.Static | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "Drop"
                              && m.GetParameters().Length >= 2
                              && m.GetParameters()[1].ParameterType.Name == "Item"
                              && m.ReturnType.Name == "DropResGameObject");
    }

    static void Postfix(object[] __args, object __result)
    {
        ChopSync.OnCorpseDropped(__args, __result);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// B-2: відправка візуальних стадій могили. ReplaceWithObject ловить переходи
// obj_id (grave_ground→exhume→empty); фільтр newId.StartsWith("grave") відсікає
// дерева→пеньки. Postfix не міняє локальну поведінку.
[HarmonyPatch]
public static class GraveReplaceSyncPatch
{
    static MethodBase TargetMethod()
    {
        var wgo = AccessTools.TypeByName("WorldGameObject");
        return wgo?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "ReplaceWithObject"
                              && m.GetParameters().Length == 3);
    }

    static void Postfix(MonoBehaviour __instance)
    {
        // Тригер зміни стадії могили → синк ПОВНОГО стану (підхід A). Фільтр grave*
        // усередині OnLocalGraveStateChanged (відсікає дерева→пеньки).
        ChopSync.OnLocalGraveStateChanged(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// B-2: тригер зміни стадії могили — RedrawPart фіксує КОЖНУ візуальну зміну
// (під-частини + внутрішні при ReplaceWithObject). У відповідь шлемо ПОВНИЙ стан
// (підхід A). Дублі з GraveReplaceSyncPatch нешкідливі (відновлення ідемпотентне).
[HarmonyPatch]
public static class GraveRedrawSyncPatch
{
    static MethodBase TargetMethod()
    {
        var wgo = AccessTools.TypeByName("WorldGameObject");
        return wgo?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "RedrawPart"
                              && m.GetParameters().Length == 4
                              && m.GetParameters()[1].ParameterType == typeof(string));
    }

    static void Postfix(MonoBehaviour __instance)
    {
        ChopSync.OnLocalGraveStateChanged(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// B-2 ФІКС «стадія відстає»: RedrawPart спрацьовує ДО оновлення _data (ланцюг:
// DropItems→OnCraftStateChanged→RedrawPart→OnWorkFinished), тож той тригер ловив
// ПОПЕРЕДНЮ стадію. OnWorkFinished/OnCraftStateChanged спрацьовують ПІСЛЯ оновлення
// _data → ловимо свіжий стан. Пакети reliable+ordered, тож фінальний стан приходить
// останнім. Ідемпотентно (повне відновлення), фільтр grave* усередині.
[HarmonyPatch]
public static class GraveWorkSyncPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var wgo = AccessTools.TypeByName("WorldGameObject");
        if (wgo == null) yield break;
        var flags = BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var name in new[] { "OnWorkFinished", "OnCraftStateChanged" })
        {
            var m = wgo.GetMethods(flags).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == 0);
            if (m != null) yield return m;
        }
    }

    static void Postfix(MonoBehaviour __instance)
    {
        ChopSync.OnLocalGraveStateChanged(__instance);
        // Кооп-крафт: завершення ОДИНОЧНОГО ручного крафту не міняє інвентар верстата (вихід падає
        // на землю) → 0x0D-дедуп глушить піггібек → стейл-бабл у напарника. Шлемо чергу напряму:
        // SendCraftQueue сам гейтить (верстат-не-могила, близькість, хеш-дедуп) — зайвого не шле.
        ChopSync.SendCraftQueue(__instance);
        // Етап 3a: крафт скінчився → release claim (станція вільна напарнику); живий → освіжити.
        ChopSync.CheckLocalCraftStopped(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// ЗВОРОТНІЙ СИНК МОГИЛИ — кладення тіла НАЗАД у могилу. З декомпіляції (2026-06-10):
// FlowCanvas PutOverheadToWGO (рядок 74541) кладе тіло через wgo.AddToInventory(Item)
// + Redraw, БЕЗ ReplaceWithObject/RedrawPart/OnWorkFinished — тобто жоден наш існуючий
// тригер 0x0D не спрацьовував, і глядач НЕ бачив тіла в могилі. Хук AddToInventory(Item)
// (WorldGameObject, перевантаження з 1 параметром Item) тригерить той самий синк ПОВНОГО
// стану (підхід A: ToJSON(0) → RestoreFromSerializedObject — тіло сидить у data.inventory,
// тож реплікується разом зі станом). Маркер тіла в GraveStageSig (варіант a) не дає дедупу
// проковтнути відправку. Фільтр grave* — усередині OnLocalGraveStateChanged (інші wgo з
// AddToInventory: скрині/верстати → IsGraveTarget відсіює дешево). SetOverheadItem(null)
// у власника синкається окремо (0x13 → carry-mirror клона прибирається).
[HarmonyPatch]
public static class GraveAddBodySyncPatch
{
    static MethodBase TargetMethod()
    {
        var wgo = AccessTools.TypeByName("WorldGameObject");
        return wgo?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "AddToInventory"
                              && m.GetParameters().Length == 1
                              && m.GetParameters()[0].ParameterType.Name == "Item");
    }

    static void Postfix(MonoBehaviour __instance)
    {
        ChopSync.OnLocalGraveStateChanged(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// КООП-КРАФТ Етап 1: постановка крафту в чергу (CraftComponent.EnqueueCraft) → шлемо
// напарнику оновлену craft_queue (0x17), щоб він бачив ті самі вікна-черги над станціями.
// __instance = CraftComponent; WGO дістаємо через .wgo. Дедуп/гейт — усередині SendCraftQueue.
[HarmonyPatch]
public static class CraftEnqueueSyncPatch
{
    static MethodBase TargetMethod()
    {
        var cc = AccessTools.TypeByName("CraftComponent");
        return cc?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "EnqueueCraft");
    }

    // __instance як object: CraftComponent не MonoBehaviour (WorldGameObjectComponentBase — звичайний
    // клас) — типізований параметр дав би невалідний каст і мертвий хук.
    static void Postfix(object __instance)
    {
        ChopSync.OnLocalCraftQueueChanged(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// ЕТАП 3a (арбітраж): блок стартів із ЧЕРГИ на станції, зайнятій напарником. Окремо від
// CraftReally, бо TryStartCraftFromQueue робить --n і видаляє пункт ДО виклику CraftReally
// (декомпіл 83782) — блок нижче по стеку тихо зʼїдав би чергу. Prefix=false скіпає оригінал
// цілком (черга лишається недоторканою, пункти стартують у власника через 0x17-мирор).
[HarmonyPatch]
public static class CraftQueueStartArbiterPatch
{
    static MethodBase TargetMethod()
    {
        var cc = AccessTools.TypeByName("CraftComponent");
        return cc?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "TryStartCraftFromQueue");
    }

    static bool Prefix(object __instance)
    {
        return !ChopSync.ShouldBlockLocalCraftStart(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// ЕТАП 3a (арбітраж): CraftReally — єдина лійка ВСІХ стартів крафту (Craft 83818 з GUI,
// TryStartCraftFromQueue 83786; матеріали списуються всередині). Prefix блокує старт на
// зайнятій станції ДО списання матеріалів; Postfix (__result=true) шле claim 0x18 напарнику.
[HarmonyPatch]
public static class CraftReallyArbiterPatch
{
    static MethodBase TargetMethod()
    {
        var cc = AccessTools.TypeByName("CraftComponent");
        return cc?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "CraftReally");
    }

    // craft — перший аргумент CraftReally (Harmony біндить за іменем): дає другий шар захисту
    // знесення (remove-крафт не блокується навіть якщо рефлексія is_removing не збіндилась).
    static bool Prefix(object __instance, object craft, ref bool __result)
    {
        if (ChopSync.ShouldBlockLocalCraftStart(__instance, craft))
        {
            __result = false;   // гра бачить «старт не вдався» — як нестачу матеріалів
            return false;
        }
        return true;
    }

    static void Postfix(object __instance, bool __result)
    {
        if (__result) ChopSync.OnLocalCraftStarted(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// ЕТАП 3a (UX): чесне повідомлення замість «not enough resources». Після нашого блоку старту
// гра показує гравцю «нестачу ресурсів» (робочий цикл, декомпіл 88292: !is_crafting →
// ShowCustomNeedBubble("not_enough_resources")) — вводить в оману. Перехоплюємо ТЕКСТ у
// ShowCustomNeedBubble (BaseCharacterComponent, 81495 → wgo.Say → бабл) і, якщо блок був у
// межах 1с, підміняємо на «зайнято напарником». Йде рідним пайплайном гри (latch/шрифт/бабл).
// Текст без специфічно-українських гліфів (і/ї/є/ґ) — шрифт гри гарантовано має RU-кирилицю.
[HarmonyPatch]
public static class BlockedCraftBubblePatch
{
    static MethodBase TargetMethod()
    {
        return AccessTools.TypeByName("BaseCharacterComponent")?
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "ShowCustomNeedBubble");
    }

    static void Prefix(ref string text)
    {
        if (text == "not_enough_resources" && ChopSync.WasCraftBlockedRecently)
            text = ChopSync.GetStationBusyMessage();
    }

    static Exception Finalizer(Exception __exception) => null;
}

// ЕТАП 3b: прогрес власника → напарнику. CraftComponent.DoAction тікає прогрес щороботи
// (гравець/зомбі/авто, декомпіл 83157) — Postfix шле 0x18 flag=2 на 10%-кроках (дедуп
// квантом усередині OnLocalCraftProgressTick, гейт _localCrafting).
[HarmonyPatch]
public static class CraftProgressSyncPatch
{
    static MethodBase TargetMethod()
    {
        var cc = AccessTools.TypeByName("CraftComponent");
        return cc?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                              BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DoAction");
    }

    static void Postfix(object __instance)
    {
        ChopSync.OnLocalCraftProgressTick(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// ЕТАП 3b: рідний RefreshComponentBubbleData стирає прогрес-бар, коли is_crafting=false
// (декомпіл 84633, SetBubbleWidgetData(null)) — а в напарника воно завжди false. Postfix
// повертає бар, поки станція під активним claim (гейт у ReinjectRemoteProgressBar).
[HarmonyPatch]
public static class RemoteCraftBarPatch
{
    static MethodBase TargetMethod()
    {
        var cc = AccessTools.TypeByName("CraftComponent");
        return cc?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                              BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "RefreshComponentBubbleData");
    }

    static void Postfix(object __instance)
    {
        ChopSync.ReinjectRemoteProgressBar(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}   