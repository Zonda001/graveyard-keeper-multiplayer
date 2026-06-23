using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

// ─────────────────────────────────────────────────────────────────────────────
// 🔬 PPAR-RECON (story sync P1, 2026-06-12): logs story params + quest keys from the live game to
// map the "story flag vs personal stat" boundary. FlowCanvas Ppar funnels (74000-74113) converge on
// player WGO.SetParam; quests start via QuestSystem.CheckKeyQuests(key). Throttled 5s/name.
// ─────────────────────────────────────────────────────────────────────────────
public static class PparRecon
{
    private static readonly Dictionary<string, float> _lastLogTime = new Dictionary<string, float>();

    public static void OnPlayerSetParam(MonoBehaviour wgo, string name, float value)
    {
        try
        {
            var mg = MainGame.me;
            if (wgo == null || mg == null) return;
            if (!ReferenceEquals(wgo, mg.player))
            {
                // 🔬 DIAG for the dialog-restart issue (P2d): hypothesis — branch progress is written into
                // the NPC's OWN params (self_wgo). Log non-player SetParam to see where it goes.
                var w = wgo as WorldGameObject;
                string objId = w != null ? w.obj_id : wgo.gameObject.name;
                string k = objId + "." + name;
                float nowN = Time.realtimeSinceStartup;
                if (_lastLogTime.TryGetValue(k, out var tn) && nowN - tn < 5f) return;
                _lastLogTime[k] = nowN;
                Multiplayer.Log?.LogInfo($"[PPAR-DIAG] NPC param {objId}.{name}={value:F2}");
                return;
            }
            StorySync.OnLocalParam(name, value);   // sync (P2) — no throttle, deduped by value inside
            float now = Time.realtimeSinceStartup;
            if (_lastLogTime.TryGetValue(name, out var t) && now - t < 5f) return;
            _lastLogTime[name] = now;
            Multiplayer.Log?.LogInfo($"[PPAR-DIAG] SetParam {name}={value:F2}");
        }
        catch { }
    }

    public static void OnKeyQuest(string key)
    {
        try
        {
            StorySync.OnLocalKey(key);             // sync (P2)
            Multiplayer.Log?.LogInfo($"[PPAR-DIAG] CheckKeyQuests '{key}'");
        }
        catch { }
    }
}


// ─────────────────────────────────────────────────────────────────────────────
// STORY SYNC (P2, 2026-06-12): bidirectional op-sync of story params (0x1C) + quest action-keys (0x1D),
// so both quest engines see the UNION of actions. The join baseline is shared (quest state is in the
// transfer save). Filter = WHITELIST of WORLD gates (see _worldParams below); narrative/stats are personal.
// Echo-guard ApplyingRemote: replaying a key cascades SetParam/CheckKeyQuests — the cascade is NOT
// re-broadcast (initiator already sent its ops; SetParam is ABSOLUTE → no double rewards).
// quest_finished — internal engine key, NOT replayed (loop/duplicate).
// ─────────────────────────────────────────────────────────────────────────────
public static class StorySync
{
    public static bool ApplyingRemote;

    // ── STARDEW MODEL (2026-06-13) ──────────────────────────────────────────────
    // World shared, but CHARACTER quests are personal. Journal (0x1E) and KnownNPC tasks (0x1F) are no
    // longer mirrored; 0x1C params were narrowed from blacklist to a WHITELIST of WORLD gates. Flags kept
    // for reversibility (true → old shared behavior). static readonly (not const) so the gated code doesn't
    // trip CS0162.
    private static readonly bool MIRROR_QUESTS = false;     // 0x1E — QuestSystem journal
    private static readonly bool MIRROR_NPC_TASKS = false;  // 0x1F — KnownNPC tasks

    // Whitelist of WORLD params (synced via 0x1C). lock_tp confirmed in code (tp-lock node SetParam,
    // decomp 136775); the rest by meaning of the P1 dictionary. Generous with gates: a missed gate breaks
    // coop (friend gets no unlock), whereas an extra flag only leaks a bit of narrative (safe).
    private static readonly HashSet<string> _worldParams = new HashSet<string>
    {
        "church_level", "skull_digged", "take_tools_from_grave_chest",
        "waiting_for_first_bureal", "rednecks_spawned", "do_spawn_rednecks",
        "donkey_on_scene", "donkey_coming_chance", "witch_hill_is_closed",
    };

    private static readonly Dictionary<string, float> _lastSent = new Dictionary<string, float>();

    // Ops that arrived BEFORE entering the world — a queue flushed from SteamManager.Update in arrival
    // order (critical: a key starts a quest that reads params).
    // op: 0=param, 1=key, 2=quest-start, 3=quest-success, 4=quest-fail, 5=NPC task (name=npc_id, extra=task_id, value=state).
    private class PendingOp { public int op; public string name; public string extra; public float value; }
    private static readonly List<PendingOp> _pendingOps = new List<PendingOp>();
    private const int PENDING_OPS_MAX = 512;

    // A world param = in the whitelist OR the teleport-gate family (prefix "tp_" or substring "lock_tp").
    // The rest (narrative, stats, tut_*, *_quality) is personal, NOT synced.
    public static bool IsWorldParam(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 200) return false;
        if (_worldParams.Contains(name)) return true;
        if (name.StartsWith("tp_") || name.Contains("lock_tp")) return true;   // teleport gates
        return false;
    }

    public static bool IsActionKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 200) return false;
        if (key == "quest_finished") return false;
        return true;
    }

    public static void OnLocalParam(string name, float value)
    {
        try
        {
            if (ApplyingRemote || !ChopSync.IsPeerConnected() || !IsWorldParam(name)) return;
            if (_lastSent.TryGetValue(name, out var v) && Mathf.Abs(v - value) < 0.0001f) return;
            _lastSent[name] = value;
            var nb = System.Text.Encoding.UTF8.GetBytes(name);
            if (nb.Length > 255) return;
            var pk = new byte[6 + nb.Length];
            pk[0] = 0x1C;
            pk[1] = (byte)nb.Length;
            Buffer.BlockCopy(nb, 0, pk, 2, nb.Length);
            BitConverter.GetBytes(value).CopyTo(pk, 2 + nb.Length);
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, pk);
            Multiplayer.Log?.LogInfo($"[STORY] Param {name}={value:F2} → 0x1C");
        }
        catch { }
    }

    // 0x1D DISABLED (live test P2c #1): replaying a key into CheckKeyQuests starts quests WITH SCRIPTS
    // (windows/rewards/movement lock). Lifecycle now runs via 0x1E (state-only), params 0x1C, NPC tasks 0x1F.
    public static void OnLocalKey(string key)
    {
        // disabled — see the comment above
    }

    private static bool ReadyToApply()
    {
        try
        {
            return SteamNetwork.IsInGame && MainGame.me != null &&
                   MainGame.me.player != null && MainGame.me.save != null;
        }
        catch { return false; }
    }

    public static void ApplyRemoteParam(string name, float value)
    {
        if (!IsWorldParam(name)) return;   // safety belt (the sender filters too)
        if (!ReadyToApply())
        {
            if (_pendingOps.Count < PENDING_OPS_MAX)
                _pendingOps.Add(new PendingOp { op = 0, name = name, value = value });
            return;
        }
        DoApplyParam(name, value);
    }

    public static void ApplyRemoteKey(string key)
    {
        if (!IsActionKey(key)) return;
        if (!ReadyToApply())
        {
            if (_pendingOps.Count < PENDING_OPS_MAX)
                _pendingOps.Add(new PendingOp { op = 1, name = key });
            return;
        }
        DoApplyKey(key);
    }

    // 0x1E receiver: the friend's quest lifecycle (kind: 0=start, 1=success, 2=fail).
    public static void ApplyRemoteQuestEvent(int kind, string id)
    {
        if (!MIRROR_QUESTS) return;   // Stardew: journal is personal — ignore incoming
        if (string.IsNullOrEmpty(id) || kind < 0 || kind > 2) return;
        if (!ReadyToApply())
        {
            if (_pendingOps.Count < PENDING_OPS_MAX)
                _pendingOps.Add(new PendingOp { op = 2 + kind, name = id });
            return;
        }
        DoApplyQuestEvent(kind, id);
    }

    // From SteamManager.Update: flush the op queue after entering the world.
    public static void TickFlushPending()
    {
        if (_pendingOps.Count == 0 || !ReadyToApply()) return;
        Multiplayer.Log?.LogInfo($"[STORY] Flushing {_pendingOps.Count} pending ops (world ready)");
        foreach (var op in _pendingOps)
        {
            if (op.op == 0) DoApplyParam(op.name, op.value);
            else if (op.op == 1) DoApplyKey(op.name);
            else if (op.op == 5) DoApplyNpcTask(op.name, op.extra, (int)op.value);
            else DoApplyQuestEvent(op.op - 2, op.name);
        }
        _pendingOps.Clear();
    }

    // ── NPC TASKS (0x1F) — live test P2b #1: the village head's quest never showed up ─────
    // GK has TWO quest systems: QuestSystem (0x1E) and the per-NPC TASK JOURNAL — KnownNPC.TaskState set
    // via KnownNPC.SetQuestState(task_id, state) (the "rednecks" quest is here). Hook SetQuestState → 0x1F;
    // receiver: known_npcs.GetOrCreateNPC(npc).SetQuestState(task, state). Idempotent.
    public static void OnLocalNpcTask(object knownNpc, string taskId, int state)
    {
        try
        {
            if (!MIRROR_NPC_TASKS) return;   // Stardew: NPC tasks are personal
            if (ApplyingRemote || !ChopSync.IsPeerConnected()) return;
            var npc = knownNpc as KnownNPC;
            if (npc == null || string.IsNullOrEmpty(npc.npc_id) || string.IsNullOrEmpty(taskId)) return;
            var nb = System.Text.Encoding.UTF8.GetBytes(npc.npc_id);
            var tb = System.Text.Encoding.UTF8.GetBytes(taskId);
            if (nb.Length > 255 || tb.Length > 255) return;
            var pk = new byte[4 + nb.Length + tb.Length];
            pk[0] = 0x1F;
            pk[1] = (byte)state;
            pk[2] = (byte)nb.Length;
            Buffer.BlockCopy(nb, 0, pk, 3, nb.Length);
            pk[3 + nb.Length] = (byte)tb.Length;
            Buffer.BlockCopy(tb, 0, pk, 4 + nb.Length, tb.Length);
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, pk);
            Multiplayer.Log?.LogInfo($"[STORY] NPC task {npc.npc_id}/{taskId}={state} → 0x1F");
        }
        catch { }
    }

    public static void ApplyRemoteNpcTask(string npcId, string taskId, int state)
    {
        if (!MIRROR_NPC_TASKS) return;   // Stardew: NPC tasks are personal — ignore incoming
        if (string.IsNullOrEmpty(npcId) || string.IsNullOrEmpty(taskId)) return;
        if (!ReadyToApply())
        {
            if (_pendingOps.Count < PENDING_OPS_MAX)
                _pendingOps.Add(new PendingOp { op = 5, name = npcId, extra = taskId, value = state });
            return;
        }
        DoApplyNpcTask(npcId, taskId, state);
    }

    private static void DoApplyNpcTask(string npcId, string taskId, int state)
    {
        ApplyingRemote = true;
        try
        {
            var npc = MainGame.me.save.known_npcs.GetOrCreateNPC(npcId);
            if (npc == null) { Multiplayer.Log?.LogWarning($"[STORY] 0x1F: NPC '{npcId}' was not created"); return; }
            npc.SetQuestState(taskId, (KnownNPC.TaskState.State)state);
            Multiplayer.Log?.LogInfo($"[STORY] 0x1F: task {npcId}/{taskId}={state} applied ✓");
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[STORY] 0x1F apply: {e.Message}"); }
        finally { ApplyingRemote = false; }
    }

    private static void DoApplyParam(string name, float value)
    {
        ApplyingRemote = true;
        try
        {
            MainGame.me.player.SetParam(name, value);
            _lastSent[name] = value;   // don't send the same value back on a local repeat
            Multiplayer.Log?.LogInfo($"[STORY] 0x1C: param {name}={value:F2} applied ✓");
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[STORY] 0x1C apply: {e.Message}"); }
        finally { ApplyingRemote = false; }
    }

    private static void DoApplyKey(string key)
    {
        // 0x1D disabled (script hazard on the replica) — old packets are just logged.
        Multiplayer.Log?.LogInfo($"[STORY] 0x1D: key '{key}' ignored (disabled, lifecycle runs via 0x1E)");
    }

    // ── QUEST LIFECYCLE (0x1E) — live test P2 #1: keys are NOT enough ──────────
    // A quest's start lives in the DIALOG (local) and its finish in CONDITIONS over the LOCAL inventory →
    // the friend's engine can't reach the same conclusions. So we replicate start/finish EXPLICITLY: hook
    // StartQuest + EndQuest (single chokepoint) → 0x1E {kind,id}; receiver bypasses local conditions.
    public static void OnLocalQuestStart(object questDef)
    {
        try
        {
            if (!MIRROR_QUESTS) return;   // Stardew: journal is personal
            if (ApplyingRemote || !ChopSync.IsPeerConnected()) return;
            var def = questDef as QuestDefinition;
            if (def == null || string.IsNullOrEmpty(def.id)) return;
            SendQuestEvent(0, def.id);
            Multiplayer.Log?.LogInfo($"[STORY] Quest START '{def.id}' → 0x1E");
        }
        catch { }
    }

    public static void OnLocalQuestEnd(object questState)
    {
        try
        {
            if (!MIRROR_QUESTS) return;   // Stardew: journal is personal
            if (ApplyingRemote || !ChopSync.IsPeerConnected()) return;
            var st = questState as QuestState;
            if (st == null || st.definition == null || string.IsNullOrEmpty(st.definition.id)) return;
            int kind = st.state == QuestState.State.Succeeded ? 1 : 2;
            SendQuestEvent(kind, st.definition.id);
            Multiplayer.Log?.LogInfo($"[STORY] Quest {(kind == 1 ? "SUCCESS" : "FAIL")} '{st.definition.id}' → 0x1E");
        }
        catch { }
    }

    private static void SendQuestEvent(int kind, string id)
    {
        var ib = System.Text.Encoding.UTF8.GetBytes(id);
        if (ib.Length == 0 || ib.Length > 255) return;
        var pk = new byte[3 + ib.Length];
        pk[0] = 0x1E;
        pk[1] = (byte)kind;
        pk[2] = (byte)ib.Length;
        Buffer.BlockCopy(ib, 0, pk, 3, ib.Length);
        SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, pk);
    }

    private static QuestDefinition FindQuestDef(string id)
    {
        try
        {
            foreach (var d in GameBalance.me.quests_data)
                if (d != null && d.id == id) return d;
        }
        catch { }
        return null;
    }

    // STATE-ONLY replay (live test P2c #1): StartQuest/ForceQuestEnd ran the quest SCRIPTS (windows,
    // movement lock, rewards) → an invisible blocking modal for the friend. So the replica touches ONLY the
    // STATE (quest lists directly in QuestSystem's fields) + journal Redraw; scripts run only in the
    // initiator. Trade-off (accepted): only the initiator gets a quest's item rewards.
    private static FieldInfo _qsCurrentField, _qsSuccedField, _qsFailedField, _qsExecutedField;
    private static bool _qsReflectionTried;

    private static void EnsureQuestReflection()
    {
        if (_qsReflectionTried) return;
        _qsReflectionTried = true;
        var t = typeof(QuestSystem);
        var f = BindingFlags.NonPublic | BindingFlags.Instance;
        _qsCurrentField  = t.GetField("_currnet_quests", f);   // the game's own typo (as in the decompile)
        _qsSuccedField   = t.GetField("_succed_quests", f);
        _qsFailedField   = t.GetField("_failed_quests", f);
        _qsExecutedField = t.GetField("_executed_quests", f);
        Multiplayer.Log?.LogInfo($"[STORY] quest reflection: cur={_qsCurrentField != null} " +
            $"ok={_qsSuccedField != null} fail={_qsFailedField != null} exec={_qsExecutedField != null}");
    }

    private static void RedrawQuestList()
    {
        try { GUIElements.me?.quest_list?.Redraw(); } catch { }
    }

    private static void DoApplyQuestEvent(int kind, string id)
    {
        ApplyingRemote = true;
        try
        {
            var qs = MainGame.me.save.quests;
            EnsureQuestReflection();
            var current = _qsCurrentField?.GetValue(qs) as List<QuestState>;
            if (current == null) { Multiplayer.Log?.LogWarning("[STORY] 0x1E: _currnet_quests could not be read"); return; }

            if (kind == 0)
            {
                // Start: only if the quest is not running and not already closed (idempotency).
                if (qs.IsQuestCurrent(id) || qs.IsQuestSucced(id) || qs.IsQuestFaild(id)) return;
                var def = FindQuestDef(id);
                if (def == null) { Multiplayer.Log?.LogWarning($"[STORY] 0x1E: quest '{id}' not found in quests_data"); return; }
                current.Add(new QuestState { definition = def });
                var ex = _qsExecutedField?.GetValue(qs) as List<string>;
                if (ex != null && !ex.Contains(id)) ex.Add(id);
                RedrawQuestList();
                Multiplayer.Log?.LogInfo($"[STORY] 0x1E: quest '{id}' added to journal (state-only) ✓");
            }
            else
            {
                bool ok = kind == 1;
                if (qs.IsQuestSucced(id) || qs.IsQuestFaild(id)) return;   // already closed
                for (int i = current.Count - 1; i >= 0; i--)
                    if (current[i] != null && current[i].definition != null && current[i].definition.id == id)
                        current.RemoveAt(i);
                var doneList = (ok ? _qsSuccedField : _qsFailedField)?.GetValue(qs) as List<string>;
                if (doneList != null && !doneList.Contains(id)) doneList.Add(id);
                var ex = _qsExecutedField?.GetValue(qs) as List<string>;
                if (ex != null && !ex.Contains(id)) ex.Add(id);
                RedrawQuestList();
                Multiplayer.Log?.LogInfo($"[STORY] 0x1E: quest '{id}' closed ({(ok ? "success" : "fail")}, state-only) ✓");
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[STORY] 0x1E apply '{id}': {e.Message}"); }
        finally { ApplyingRemote = false; }
    }
}

[HarmonyPatch]
public static class PparSetParamReconPatch
{
    static MethodBase TargetMethod()
    {
        var wgo = AccessTools.TypeByName("WorldGameObject");
        return wgo?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "SetParam"
                              && m.GetParameters().Length == 2
                              && m.GetParameters()[0].ParameterType == typeof(string)
                              && m.GetParameters()[1].ParameterType == typeof(float));
    }

    static void Postfix(MonoBehaviour __instance, string param_name, float value)
    {
        PparRecon.OnPlayerSetParam(__instance, param_name, value);
    }

    static Exception Finalizer(Exception __exception) => null;
}

[HarmonyPatch]
public static class PparKeyQuestReconPatch
{
    static MethodBase TargetMethod()
    {
        var qs = AccessTools.TypeByName("QuestSystem");
        return qs?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                              BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "CheckKeyQuests"
                              && m.GetParameters().Length == 1
                              && m.GetParameters()[0].ParameterType == typeof(string));
    }

    static void Postfix(string key)
    {
        PparRecon.OnKeyQuest(key);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// STORY (0x1E): quest start — QuestSystem.StartQuest(QuestDefinition), the shared path for natural start
// and replay. Echo gate (ApplyingRemote) inside OnLocalQuestStart.
[HarmonyPatch]
public static class QuestStartSyncPatch
{
    static MethodBase TargetMethod()
    {
        var qs = AccessTools.TypeByName("QuestSystem");
        return qs?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "StartQuest"
                              && m.GetParameters().Length == 1
                              && m.GetParameters()[0].ParameterType.Name == "QuestDefinition");
    }

    static void Postfix(object quest)
    {
        StorySync.OnLocalQuestStart(quest);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// STORY (0x1E): quest end — QuestSystem.EndQuest(QuestState), the SINGLE chokepoint for natural and forced
// end; q_to_end.state is already set at the call (decomp 104168).
[HarmonyPatch]
public static class QuestEndSyncPatch
{
    static MethodBase TargetMethod()
    {
        var qs = AccessTools.TypeByName("QuestSystem");
        return qs?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "EndQuest"
                              && m.GetParameters().Length == 1
                              && m.GetParameters()[0].ParameterType.Name == "QuestState");
    }

    static void Postfix(object q_to_end)
    {
        StorySync.OnLocalQuestEnd(q_to_end);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// STORY (0x1F): NPC journal tasks — KnownNPC.SetQuestState(task_id, state), what dialogs write into the
// per-character journal. Echo gate (ApplyingRemote) inside OnLocalNpcTask.
[HarmonyPatch]
public static class NpcTaskSyncPatch
{
    static MethodBase TargetMethod()
    {
        var kn = AccessTools.TypeByName("KnownNPC");
        return kn?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "SetQuestState"
                              && m.GetParameters().Length == 2
                              && m.GetParameters()[0].ParameterType == typeof(string));
    }

    static void Postfix(object __instance, string task_id, object state)
    {
        try { StorySync.OnLocalNpcTask(__instance, task_id, Convert.ToInt32(state)); } catch { }
    }

    static Exception Finalizer(Exception __exception) => null;
}
