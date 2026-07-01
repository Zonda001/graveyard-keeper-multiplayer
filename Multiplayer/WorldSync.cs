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

// Send: every DoAction on a tree from the local player → packet 0x09.
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
        // Per-frame work on a grave (holding F) → refresh the owner-window from the first tick,
        // so an incoming restore doesn't rehydrate the active grave and break the work session.
        ChopSync.NoteLocalGraveWork(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// Carried-corpse sync no longer hooks pickup — the model moved to position mirroring (0x0F) + removal on
// drop (0x10), driven by the per-frame ChopSync.TickCarriedBodies.

// MIRROR-CORPSE COLLECT BLOCK (decomp 2026-06-09): on collect the game sets is_collected=true, and the next
// drop's DropsList.Add does Object.Destroy — that destroyed the mirrored corpse. So we block both collect
// gates for the mirror (owner=False): CanCollectDrop → RETURNS INT (not bool!), force 0; CanPickupWithInteraction
// → force false. The owner is unaffected (owner=True → IsMirrorCorpse=false).
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

// CORPSE OWNERSHIP TRANSFER (2026-06-10): MANUAL pickup of the mirror is now ALLOWED and transfers ownership
// (mirror lift → 0x14 → owner removes the ground copy; the observer's drop makes them the owner). Auto-collect
// stays blocked (CanCollectDrop→0): transfer only via a deliberate lift, not the auto-magnet.

// OVERHEAD CARRYING: BaseCharacterComponent.SetOverheadItem(Item) — universal mechanism for carrying heavy
// items above the head. We hook the LOCAL player → broadcast (0x12 carry / 0x13 put down); the clone never
// calls it (its BaseCharacterComponent is disabled), so the hook is always local.
// PHASE 2 — BUILDING: BuildModeLogics.DoPlace (decomp 43996) puts the floating preview into the world.
// cur_floating is nulled INSIDE the method, so we capture wobj in Prefix and detect placement in Postfix
// (floating != captured). The preview's unique_id syncs it → ApplyRemoteBuildSpawn copies it; stages via 0x0D.
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

// PHASE 2 — DEMOLITION: hook WGO.DestroyMe (decomp 114595, final point MarkForRemoval→ProcessRemove→DestroyMe).
// Prefix reads the uid before destruction and sends 0x16 for a tracked building (filter in OnLocalBuildRemoved).
// KNOWN LIMIT: mass DestroyMe on world unload also fires, but Connected()=false on exit filters it; else an edge (deferred).
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

// for certainty we check against MainGame.me.player_char.
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
            if (pc != null && !ReferenceEquals(pc, __instance)) return;   // not the local player
        }
        catch { }
        ChopSync.OnLocalOverheadChanged(__args != null && __args.Length > 0 ? __args[0] : null);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// Send: DropItems on a tree = it was felled → broadcast the destruction (0x0A).
// The Postfix doesn't change behavior — a normal player chop stays untouched.
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

    // Flag "local drop was cancelled in Prefix" — so the Postfix doesn't broadcast something the game
    // didn't drop. Synchronous (Prefix→original→Postfix on one thread), a flat static is fine.
    static bool _graveDropSuppressed;

    // PREFIX: for a grave-runaway we cancel the game's call itself (return false) → the local item is
    // NOT spawned (fixes the pyramid for the observer; the Postfix couldn't — the item had already dropped).
    static bool Prefix(MonoBehaviour __instance, object[] __args)
    {
        _graveDropSuppressed = false;
        if (__args != null && __args.Length >= 1 && ChopSync.ShouldSuppressGraveDrop(__instance, __args[0]))
        {
            _graveDropSuppressed = true;
            return false;   // cancel the original DropItems
        }
        return true;
    }

    static void Postfix(MonoBehaviour __instance, object[] __args)
    {
        if (_graveDropSuppressed) { _graveDropSuppressed = false; return; }  // drop cancelled → don't broadcast
        // Order matters: send the LOOT (0x0B) FIRST, the state change (0x0A) second.
        // Packets are reliable+ordered (k_EP2PSendReliable), so the receiver applies the loot while the
        // object is still in place, and only then transforms it. Otherwise for nature
        // (flower→flower_spawner) the loot couldn't find the dropper: the flower became a spawner
        // (excluded from loot targets) → the loot hung in _pendingDrops (live test 2026-06-01).
        if (__args != null && __args.Length >= 2)
            ChopSync.OnLocalDropItems(__instance, __args[0], __args[1]);
        ChopSync.OnTreeFelled(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// CARRIED — single hook on static DropResGameObject.DoDrop (decomp 90093): called exactly once per physical
// ground object and always returns a GO. We used to hook Drop, but for value>1 (logs from a perk) Drop SPLITS
// into a DoDrop loop and returns NULL (90060-88) → the hook skipped those logs (live test v3 #2: "friend sees
// 1 log out of 3-4"). Arg indices match Drop: [1]=Item, [3]=Direction, [4]=force; the DoDrop Item is a value=1 copy.
[HarmonyPatch]
public static class CorpseDropBroadcastPatch
{
    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("DropResGameObject");
        return t?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.Static | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DoDrop"
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

// B-2: send a grave's visual stages. ReplaceWithObject catches obj_id transitions
// (grave_ground→exhume→empty); the filter newId.StartsWith("grave") cuts off trees→stumps.
// The Postfix doesn't change local behavior.
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
        // Trigger on a grave stage change → sync the FULL state (approach A). The grave* filter
        // is inside OnLocalGraveStateChanged (cuts off trees→stumps).
        ChopSync.OnLocalGraveStateChanged(__instance);
        // Garden/orchards: planting (empty→garden_X) and harvest go through ReplaceWithObject (0x21).
        ChopSync.OnLocalGardenStateChanged(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// B-2: trigger on a grave stage change — RedrawPart captures EVERY visual change (sub-parts +
// internals during ReplaceWithObject). In response we send the FULL state (approach A). Duplicates
// with GraveReplaceSyncPatch are harmless (restore is idempotent).
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
        ChopSync.OnLocalGardenStateChanged(__instance);   // garden: a visual stage change
    }

    static Exception Finalizer(Exception __exception) => null;
}

// B-2 FIX "stage lags": RedrawPart fires BEFORE _data is updated (chain:
// DropItems→OnCraftStateChanged→RedrawPart→OnWorkFinished), so that trigger caught the PREVIOUS stage.
// OnWorkFinished/OnCraftStateChanged fire AFTER _data is updated → we catch the fresh state. Packets are
// reliable+ordered, so the final state arrives last. Idempotent (full restore), grave* filter inside.
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
        // Coop craft: finishing a SINGLE manual craft doesn't change the station's inventory (the output
        // drops to the ground) → the 0x0D dedup silences the piggyback → a stale bubble for the friend.
        // Send the queue directly: SendCraftQueue gates itself (station-not-grave, proximity, hash dedup)
        // — it won't send anything extra.
        ChopSync.SendCraftQueue(__instance);
        // Stage 3a: the craft finished → release the claim (station free for the friend); if alive → refresh.
        ChopSync.CheckLocalCraftStopped(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// REVERSE GRAVE SYNC — putting a body BACK into the grave (decomp 2026-06-10): FlowCanvas PutOverheadToWGO
// (74541) does wgo.AddToInventory(Item) + Redraw WITHOUT any of our 0x0D triggers → the observer didn't see
// the body. Hooking AddToInventory(Item) triggers the same FULL-state sync (ToJSON(0) → RestoreFromSerializedObject;
// the body sits in data.inventory). grave* filter inside OnLocalGraveStateChanged (chests/stations rejected
// cheaply). SetOverheadItem(null) on the owner syncs separately (0x13).
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

    // __0 = the added Item; __result = whether it was actually added (112588 returns bool — verified).
    static void Postfix(MonoBehaviour __instance, object __0, bool __result)
    {
        ChopSync.OnLocalGraveStateChanged(__instance);
        // Phase 3 (stockpiles): a player's carried put onto a NON-0x0D target → op +N (gates inside).
        if (__result) ChopSync.OnLocalContainerPut(__instance, __0);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// PHASE 3 (stockpiles): take a carried item from the stockpile into hands — WGO.GiveItemToPlayersHands
// (115845: data.RemoveItem + SetOverheadItem). Op −N; for 0x0D targets — full state (closes the gap
// "took a station's output into hands — the station's inventory wasn't synced").
[HarmonyPatch]
public static class GiveToHandsSyncPatch
{
    static MethodBase TargetMethod()
    {
        var wgo = AccessTools.TypeByName("WorldGameObject");
        return wgo?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "GiveItemToPlayersHands"
                              && m.GetParameters().Length == 1);
    }

    static void Postfix(MonoBehaviour __instance, object __0)
    {
        ChopSync.OnLocalContainerTake(__instance, __0);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// PHASE 3 (stockpiles, fix for the live-test "the pile grew without minuses"): taking from a stockpile
// goes through the flow lambda TakeItemFromWGO (74197: _wgo.data.RemoveItem(item,1)), NOT through
// GiveItemToPlayersHands. We don't patch the lambda — we hook the FUNNEL Item.RemoveItem(Item,int,Item)
// (71886, returns bool; all container takes converge here). Gates/data→WGO map/suppression inside
// ChestGUI.MoveItem — in OnLocalDataItemRemoved.
[HarmonyPatch]
public static class ItemRemoveSyncPatch
{
    static MethodBase TargetMethod()
    {
        var it = AccessTools.TypeByName("Item");
        return it?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "RemoveItem" && m.GetParameters().Length == 3 &&
                                 m.GetParameters()[0].ParameterType.Name == "Item" &&
                                 m.GetParameters()[1].ParameterType == typeof(int));
    }

    static void Postfix(object __instance, object __0, int __1, bool __result)
    {
        ChopSync.OnLocalDataItemRemoved(__instance, __0, __1, __result);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// CARRY-MIRROR: deterministic ownership transfer — hook the real pickup moment (TryOtherInteractions, 81542).
// Candidate = static DropResGameObject.currently_higlighted_obj (nulled inside → catch in Prefix); __result=true = picked up.
[HarmonyPatch]
public static class MirrorPickupTransferPatch
{
    static FieldInfo _hlField;

    static MethodBase TargetMethod()
    {
        _hlField = AccessTools.TypeByName("DropResGameObject")
            ?.GetField("currently_higlighted_obj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return AccessTools.TypeByName("BaseCharacterComponent")
            ?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "TryOtherInteractions" && m.GetParameters().Length == 0);
    }

    static void Prefix()
    {
        ChopSync.CapturePickupCandidate(_hlField?.GetValue(null));
    }

    static void Postfix(bool __result)
    {
        ChopSync.ConfirmPickup(__result);
    }

    static Exception Finalizer(Exception __exception) => null;
}

public static class ChopSync
{
    // Position-match threshold — a bit less than the distance between adjacent trees.
    private const float POSITION_EPSILON = 2f;

    // Echo guard around our own DoAction replay.
    public static bool ApplyingRemoteChop;

    private static Type       _wgoType;
    private static FieldInfo  _objIdField;
    private static FieldInfo  _isPlayerField;
    private static MethodInfo _doActionMethod;
    private static FieldInfo  _objDefField;             // WorldGameObject.obj_def
    private static FieldInfo  _variationField;          // WorldGameObject.variation
    private static MethodInfo _replaceWithObjectMethod; // WGO.ReplaceWithObject(string,bool,int)
    private static MethodInfo _redrawPartMethod;        // WGO.RedrawPart(WorldObjectPart,string,string,int) — grave-change trigger
    // B-2 approach A — full object replication (state replication):
    private static Type       _serializableWgoType;     // SerializableWGO ([Serializable])
    private static MethodInfo _fromWgoMethod;           // static SerializableWGO.FromWGO(wgo) → SerializableWGO
    private static MethodInfo _restoreFromSerializedMethod; // WGO.RestoreFromSerializedObject(SerializableWGO, bool)
    private static MethodInfo _redrawMethod;            // WGO.Redraw(force_redraw,force_redraw_part,draw_puff) — force-draws the grave furniture after restore
    private static FieldInfo  _itemInventoryField;      // Item.inventory (List<Item>) — the grave's furniture items
    private static MethodInfo _getParamMethod;          // WGO.GetParam(string,float) — a grave part's value
    private static MethodInfo _setParamMethod;          // WGO.SetParam(string,float) — force "finished" on the observer (no scaffold)
    private static MethodInfo _getBodyFromInventoryMethod; // WGO.GetBodyFromInventory(bool) — a reliable signal of a body in the grave (for the stage signature)
    private static MethodInfo _spawnWgoMethod;          // WorldMap.SpawnWGO(Transform,string,Vector3?) — spawn a new object (Phase 2 building)
    private static MethodInfo _destroyMeMethod;         // WGO.DestroyMe() (decompiled 114595) — object destruction (building demolition sync 0x16)
    private static PropertyInfo _worldRootProp;         // LazyEngine.world_root (static Transform, decompiled 98892) — the parent for spawning
    private static FieldInfo  _uniqueIdField;           // WGO.unique_id (long) — reliable target matching
    private static FieldInfo  _swUniqueIdField;         // SerializableWGO.unique_id
    private static FieldInfo  _swItemDataField;         // SerializableWGO.item_data (String) — compact _data state
    private static FieldInfo  _swItemField;             // SerializableWGO.item (Item) — we null it so restore reads item_data
    private static FieldInfo  _swObjIdField;            // SerializableWGO.obj_id (String)
    private static FieldInfo  _swVariationField;        // SerializableWGO.variation (Int32)
    private static FieldInfo  _dataField;               // WGO._data (Item) — grave state
    private static MethodInfo _toJsonMethod;            // Item.ToJSON(int ser_depth) → state JSON
    private static MethodInfo _fromJsonOverwriteMethod; // UnityEngine.JsonUtility.FromJsonOverwrite(string, object)
    // Grave-state dedup by uid (both sides): don't send/apply the identical state twice —
    // otherwise duplicate triggers cause an extra RestoreFromSerializedObject = flickering.
    private static readonly Dictionary<long, string> _lastSentGraveSig = new Dictionary<long, string>();
    private static readonly Dictionary<long, string> _lastAppliedGraveSig = new Dictionary<long, string>();
    // TIME of the last applied state (echo-suppression WINDOW). An echo comes back within a fraction of a
    // second; a STALE applied signature must NOT suppress an honest change minutes later. BUG 2026-06-10: a
    // full grave applied at startup suppressed the same-signature rebuild forever (2nd furniture never arrived).
    // The window suppresses only a RECENT echo.
    private static readonly Dictionary<long, float> _lastAppliedGraveSigTime = new Dictionary<long, float>();
    private const float ECHO_SUPPRESS_WINDOW = 4f;
    // GRAVE PART RE-DROP DEDUP (co-dig dupe fix, 2026-06-08): a grave gives each part 1×/stage. While the
    // stage is unchanged, a repeated 0x0B drop = a runaway re-drop (restore rehydrated _data) → we DON'T send.
    // Reset when the stage changes. uid→stage, uid→already-sent-ids.
    private static readonly Dictionary<long, string> _graveSentStageSig = new Dictionary<long, string>();
    private static readonly Dictionary<long, HashSet<string>> _graveSentParts = new Dictionary<long, HashSet<string>>();
    // OWNER GUARD (2026-06-08): time of the last GENUINE local grave-stage change (set AFTER the echo check).
    // If I recently dug this grave myself, I do NOT apply an incoming restore (it would rehydrate my active
    // _data → runaway re-drop). An observer makes no genuine changes → window never updates → it applies
    // everything.
    private static readonly Dictionary<long, float> _lastLocalGraveDigTime = new Dictionary<long, float>();
    private const float GRAVE_OWNER_WINDOW = 3f;
    // "Active contest" window for the MONOTONICITY guard (wider than the owner window): while I worked this
    // grave recently, keep protection against stale-echo rehydration. Outside the window monotonicity does NOT
    // apply → the reverse grave rebuild is visible too.
    private const float GRAVE_CONTEST_WINDOW = 8f;
    // RESTORE ARTIFACT (observer local-pyramid fix, 2026-06-08): time of the last restore by uid. If a grave
    // was recently rehydrated by an incoming state, its local grave-drops are a runaway artifact (game re-throws
    // a part after restore) → cancelled in the DropItems Prefix. A recent restore = I'm an observer (the owner-guard
    // blocks restore on a grave I'm digging), so this doesn't touch a digger's loot.
    private static readonly Dictionary<long, float> _lastGraveRestoreTime = new Dictionary<long, float>();
    private const float GRAVE_RESTORE_ARTIFACT_WINDOW = 8f;
    private static FieldInfo  _afterHp0Field;           // ObjectDefinition.after_hp_0
    private static MethodInfo _afterHp0GetValue;        // ChancedStringValue.GetValue(wgo,char)
    private static FieldInfo  _hasCraftField;           // ObjectDefinition.has_craft (workbench/crafting station)
    private static Type       _craftComponentType;      // CraftComponent (the crafting component on the workbench)
    private static FieldInfo  _craftQueueField;         // CraftComponent.craft_queue (List<CraftQueueItem>)
    private static Type       _craftQueueItemType;      // CraftComponent+CraftQueueItem (nested)
    private static FieldInfo  _cqiIdField;              // CraftQueueItem.id (string — CraftDefinition id)
    private static FieldInfo  _cqiNField;               // CraftQueueItem.n (int — count in the queue)
    private static FieldInfo  _cqiInfiniteField;        // CraftQueueItem.infinite (bool)
    private static FieldInfo  _isCraftingField;         // CraftComponent.is_crafting (active craft)
    private static FieldInfo  _currentCraftField;       // CraftComponent.current_craft (CraftDefinition)
    private static FieldInfo  _craftDefIdField;         // CraftDefinition.id (inherited from BalanceBaseObject)
    private static FieldInfo  _craftDefHiddenField;     // CraftDefinition.hidden (system/ambient crafts)
    private static PropertyInfo _componentWgoProp;      // WorldGameObjectComponent.wgo (get the WGO from the component)
    private static PropertyInfo _wgoComponentsProp;     // WGO.components (ComponentsManager — NOT Unity components!)
    private static PropertyInfo _componentsCraftProp;   // ComponentsManager.craft (CraftComponent from the dictionary)
    private static PropertyInfo _isRemovingProp;        // WGO.is_removing (station being demolished)
    private static PropertyInfo _wgoProgressProp;       // WGO.progress (float 0..1 → _data.progress)
    private static Type       _chestGuiType;            // ChestGUI (chest GUI, decompiled 44979)
    private static FieldInfo  _chestObjField;           // ChestGUI._chest_obj (WGO of the open chest)
    private static MethodInfo _chestFullRedrawMethod;   // ChestGUI.FullRedrawPanels(int) — live refresh
    private static PropertyInfo _chestIsShownProp;      // BaseGUI.is_shown (whether it's open right now)
    private static FieldInfo  _guiElementsChestField;   // GUIElements.chest (ChestGUI instance, 68701)
    private static PropertyInfo _guiElementsMeProp;     // GUIElements.me (static singleton, property)
    private static FieldInfo  _guiElementsMeField;      // GUIElements.me (fallback, if it's a field)
    private static MethodInfo _wgoSayMethod;            // WGO.Say(string,...) — bubble above the player (114227)
    private static MethodInfo _dataAddItemMethod;       // Item.AddItem(Item, bool) — add a stack (71563)
    private static MethodInfo _dataRemoveNoCheckMethod; // Item.RemoveItemNoCheck(Item,int,string,List,Item) — remove as many as available (71904)
    private static MethodInfo _dataGetTotalCountMethod; // Item.GetTotalCount(string) — how many of id exist
    private static PropertyInfo _wgoDataProp;           // WGO.data (property read by GUI/Inventory)
    private static PropertyInfo _componentsCharacterProp; // ComponentsManager.character (BaseCharacterComponent)
    private static MethodInfo _getOverheadItemMethod;   // BaseCharacterComponent.GetOverheadItem() (81896)
    private static MethodInfo _dropOverheadMethod;      // BaseCharacterComponent.DropOverheadItem(bool) (82064)
    private static PropertyInfo _gameBalanceMeProp;     // GameBalance.me (static)
    private static MethodInfo _getCraftDefMethod;       // GameBalance.GetData<CraftDefinition>(string)
    private static FieldInfo  _craftDefOutputField;     // CraftDefinition.output (List<Item>)
    private static FieldInfo  _craftDefIconField;       // CraftDefinition.icon (sprite id)
    private static Type       _bubbleItemDataType;      // BubbleWidgetItemData
    private static ConstructorInfo _bubbleItemCtor;     // BubbleWidgetItemData(string item_id, ...)
    private static FieldInfo  _bubbleItemIconIdField;   // BubbleWidgetItemData.icon_id
    private static object _widgetIdCraftingItem;        // enum WidgetID.CraftingItem
    private static PropertyInfo _envMeProp;             // EnvironmentEngine.me (static, 36015)
    private static MethodInfo _findNatureMethod;        // EnvironmentEngine.FindNatureWithoutRemoves (36709)
    private static MethodInfo _tryRemoveNatureMethod;   // EnvironmentEngine.TryRemoveNatureWeatherState (36573)
    private static MethodInfo _addNatureMethod;         // EnvironmentEngine.AddNatureWeatherState (36500)
    private static MethodInfo _weatherPresetGetMethod;  // WeatherPreset.GetPreset(string) static (37494)
    private static MethodInfo _getStatesFromPresetMethod; // SwitchableWeatherState.GetStatesFromPreset static (37448)
    private static MethodInfo _setBubbleWidgetDataMethod; // WGO.SetBubbleWidgetData(BubbleWidgetData, WidgetID)
    private static Type _bubbleProgressDataType;        // BubbleWidgetProgressData
    private static Type _progressDelegateType;          // BubbleWidgetProgressData.ProgressDelegate (nested)
    private static object _widgetIdCraftingProgress;    // enum BubbleWidgetData.WidgetID.CraftingProgress
    private static MethodInfo _redrawBubbleMethod;      // WGO.RedrawBubble — redraw the queue window
    private static Type       _treeDisappearType;       // TreeDisappearAnimation (tree component)
    private static MethodInfo _startAnimationMethod;    // TreeDisappearAnimation.StartAnimation(VoidDelegate)
    private static Type       _voidDelegateType;        // VoidDelegate (void() delegate)
    private static Type       _itemType;                // Item (id, value)
    private static ConstructorInfo _itemCtor;           // new Item(string id, int value)
    private static FieldInfo  _itemIdField;             // Item.id (string)
    private static FieldInfo  _itemValueField;          // Item.value (int)
    private static PropertyInfo _itemDurabilityProp;    // Item.durability (float 0..1, get=_params.durability) — repair ratchet
    private static Type       _directionType;           // Direction (enum)
    private static MethodInfo _dropItemsMethod;         // WGO.DropItems(List<Item>, Direction)
    private static MethodInfo _dropItemSingularMethod;  // WGO.DropItem(Item, Direction, Vector3, float, bool)
    private static MethodInfo _getOverheadIconMethod;   // Item.GetOverheadIcon() → name of the sprite above the head
    private static bool       _ready;

    private static MonoBehaviour _cachedLocalPlayerWgo;

    // Trees felled by another player while this machine was in a different location —
    // they aren't "awake" here yet. We destroy them when the player walks up and they
    // load (RetryPendingDestroys).
    private static readonly List<Vector2> _pendingDestroys = new List<Vector2>();

    // Loot that dropped on another player while the WGO isn't loaded here yet.
    // We store the coordinates + a ready List<Item> + Direction. RetryPendingDrops.
    private struct PendingDrop { public Vector2 Pos; public object Items; public object Direction; }
    private static readonly List<PendingDrop> _pendingDrops = new List<PendingDrop>();

    // SHARED LOOT: replaying 0x0B spawns the loot on the receiver too, and they collect their own copy.
    // Inventories are separate, so this isn't a dupe — both players get the loot (Zonda's decision
    // 2026-06-05, verified by live test). No anti-dupe/tagging at all.

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

        // Replace a felled object with its successor (tree → stump). Best-effort:
        // if it can't be resolved, the receiver falls back to a plain Destroy.
        _objDefField    = _wgoType.GetField("obj_def", f);
        _variationField = _wgoType.GetField("variation", f);
        _replaceWithObjectMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "ReplaceWithObject"
                              && m.GetParameters().Length == 3);
        // B-2: visual transition of grave stages. The digging trace (2026-06-07) showed
        // RedrawPart(WorldObjectPart wop, string id, string path, int n) — for sub-parts
        // wop==null (grave_bot_stn_1_stg_1, "objects/grave parts/"). A direct effect method,
        // it doesn't need a working session (unlike DoAction).
        _redrawPartMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "RedrawPart"
                              && m.GetParameters().Length == 4
                              && m.GetParameters()[1].ParameterType == typeof(string));

        // B-2 approach A: serialize API (RECON 2026-06-07). FromWGO(wgo) → SerializableWGO
        // ([Serializable] → BinaryFormatter over the wire), RestoreFromSerializedObject applies
        // the WHOLE state (item/_data + visual). unique_id is used to match the target (better than position).
        _uniqueIdField = _wgoType.GetField("unique_id", f);
        _serializableWgoType = AccessTools.TypeByName("SerializableWGO");
        _fromWgoMethod = _serializableWgoType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "FromWGO" && m.GetParameters().Length == 1);
        _restoreFromSerializedMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "RestoreFromSerializedObject" && m.GetParameters().Length == 2);
        // WGO.Redraw(bool force_redraw, bool force_redraw_part, bool draw_puff) — line 114000
        // of the decompilation. Calls custom_drawers.OnObjectRedraw(force_redraw), which draws the grave furniture
        // (the GraveFence/GraveStone cross) from the inventory + refreshes quality. RestoreFromSerializedObject
        // only does SetObject (the base), it doesn't redraw the furniture → a force-redraw is needed after restore.
        _redrawMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "Redraw" && m.GetParameters().Length == 3);
        // WGO.GetBodyFromInventory(bool) — a reliable "the grave has a body" signal (decompiled 115283). The
        // body sits in the inventory (not in _res_type), so a key-only stage signature did NOT catch it →
        // putting a body into grave_empty didn't change the signature → echo/dedup swallowed the 0x0D → the
        // body wasn't synced, it got pushed out.
        _getBodyFromInventoryMethod = _wgoType.GetMethods(f)
            .FirstOrDefault(m => m.Name == "GetBodyFromInventory" && m.GetParameters().Length == 1);
        // Phase 2 (building): spawn a new WGO with a shared uid. WorldMap.SpawnWGO(Transform,string,Vector3?)
        // (decompiled 99430) + MainGame.world_root (98892) as the parent. We force the uid directly after spawn.
        var worldMapType = AccessTools.TypeByName("WorldMap");
        _spawnWgoMethod = worldMapType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "SpawnWGO" && m.GetParameters().Length == 3
                              && m.GetParameters()[0].ParameterType.Name == "Transform"
                              && m.GetParameters()[1].ParameterType == typeof(string));
        // world_root — a STATIC property on the LazyEngine class (decompiled 98892, NOT MainGame!).
        _worldRootProp = AccessTools.TypeByName("LazyEngine")?.GetProperty("world_root",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        _destroyMeMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DestroyMe" && m.GetParameters().Length == 0);
        // WGO.GetParam/SetParam — on the observer, force the grave's furniture items as "completed"
        // (param≥1) → the game won't draw the wooden "under construction" frame. _itemInventoryField is below
        // (after _itemType). GetParam has a default argument → we look for the 2-parameter overload.
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

        // Craft queue (Phase 2 co-op crafting, Stage 1): CraftComponent.craft_queue (List<CraftQueueItem>).
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
        _craftDefHiddenField = AccessTools.TypeByName("CraftDefinition")?.GetField("hidden", f);
        _componentWgoProp   = AccessTools.TypeByName("WorldGameObjectComponent")?.GetProperty("wgo", f);
        // CraftComponent is NOT a MonoBehaviour (WorldGameObjectComponentBase = a plain class, decompiled
        // 100354), GetComponentInChildren will NEVER find it (live test 2026-06-11: 0x17 was silent).
        // The game's correct path: wgo.components (ComponentsManager, 111820) → .craft (82514).
        _wgoComponentsProp   = _wgoType.GetProperty("components", f);
        _componentsCraftProp = _wgoComponentsProp?.PropertyType.GetProperty("craft", f);
        _isRemovingProp      = _wgoType.GetProperty("is_removing", f);
        // Partner's progress bar (Stage 3b): wgo.progress (112161) + the native bar widget.
        // BubbleWidgetProgressData(ProgressDelegate, int, int) — 42346; WidgetID.CraftingProgress
        // — the same slot the game sets/clears in RefreshComponentBubbleData (84597/84633).
        _wgoProgressProp           = _wgoType.GetProperty("progress", f);
        // NOTE: SetBubbleWidgetData has several overloads (BubbleWidgetData/string) — we take
        // exactly the one that accepts BubbleWidgetData (otherwise Invoke with the widget would fail on the cast).
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
        // Shared chests (Phase 3): ChestGUI._chest_obj + FullRedrawPanels (live refresh on the
        // partner if the chest is open at the moment of the 0x0D restore). GUIElements.me.chest (68701).
        _chestGuiType          = AccessTools.TypeByName("ChestGUI");
        _chestObjField         = _chestGuiType?.GetField("_chest_obj", f);
        _chestFullRedrawMethod = _chestGuiType?.GetMethods(f)
                                     .FirstOrDefault(m => m.Name == "FullRedrawPanels");
        _chestIsShownProp      = _chestGuiType?.GetProperty("is_shown",
                                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var guiElemType        = AccessTools.TypeByName("GUIElements");
        _guiElementsChestField = guiElemType?.GetField("chest", f);
        _guiElementsMeProp     = guiElemType?.GetProperty("me", BindingFlags.Public | BindingFlags.Static);
        if (_guiElementsMeProp == null)
            _guiElementsMeField = guiElemType?.GetField("me", BindingFlags.Public | BindingFlags.Static);
        _wgoSayMethod = _wgoType.GetMethods(f).FirstOrDefault(m =>
            m.Name == "Say" && m.GetParameters().Length >= 4 &&
            m.GetParameters()[0].ParameterType == typeof(string));
        _wgoDataProp = _wgoType.GetProperty("data", f);
        _redrawBubbleMethod = _wgoType.GetMethods(f).FirstOrDefault(m => m.Name == "RedrawBubble");

        // Tree-fall animation — the TreeDisappearAnimation component on the tree.
        _treeDisappearType = AccessTools.TypeByName("TreeDisappearAnimation");
        _startAnimationMethod = _treeDisappearType?.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "StartAnimation" && m.GetParameters().Length == 1);
        _voidDelegateType = AccessTools.TypeByName("VoidDelegate");

        // Item/Direction/DropItems — for loot sync (0x0B). Best-effort: without them the loot
        // won't replicate, but everything else works.
        _itemType        = AccessTools.TypeByName("Item");
        _itemCtor        = _itemType?.GetConstructor(new[] { typeof(string), typeof(int) });
        _itemIdField     = _itemType?.GetField("id", f);
        _itemInventoryField = _itemType?.GetField("inventory", f);  // List<Item> — the grave's furniture items
        _itemValueField  = _itemType?.GetField("value", f);
        _itemDurabilityProp = _itemType?.GetProperty("durability", f);   // repair ratchet — AFTER _itemType (rule #2)
        // ⚠️ CHEST OPS (0x19) — these bindings MUST come STRICTLY AFTER _itemType is assigned! Incident
        // 2026-06-11: this block was above `_itemType = ...`, and EnsureReflection runs once → the methods
        // were null FOREVER → the add/remove branches silently skipped ("ops ✓", but contents didn't change; 4 tests).
        _dataAddItemMethod = _itemType?.GetMethods(f).FirstOrDefault(m =>
            m.Name == "AddItem" && m.GetParameters().Length == 2 &&
            m.GetParameters()[0].ParameterType == _itemType);
        _dataRemoveNoCheckMethod = _itemType?.GetMethods(f).FirstOrDefault(m =>
            m.Name == "RemoveItemNoCheck" && m.GetParameters().Length == 5);
        // GetTotalCount(string, bool count_in_bags = true) — TWO parameters (decompiled 72327).
        _dataGetTotalCountMethod = _itemType?.GetMethods(f).FirstOrDefault(m =>
            m.Name == "GetTotalCount" && m.GetParameters().Length == 2 &&
            m.GetParameters()[0].ParameterType == typeof(string) &&
            m.GetParameters()[1].ParameterType == typeof(bool));
        Multiplayer.Log?.LogInfo($"[CHOP] 0x19 bindings: ctor={_itemCtor != null} add={_dataAddItemMethod != null} " +
            $"removeNC={_dataRemoveNoCheckMethod != null} total={_dataGetTotalCountMethod != null}");
        // STOCKPILES (Phase 3): put/take of carried resources. Bindings AFTER _wgoComponentsProp (rule #2).
        _componentsCharacterProp = _wgoComponentsProp?.PropertyType.GetProperty("character", f);
        _getOverheadItemMethod = AccessTools.TypeByName("BaseCharacterComponent")?
            .GetMethods(f).FirstOrDefault(m => m.Name == "GetOverheadItem" && m.GetParameters().Length == 0);
        _dropOverheadMethod = AccessTools.TypeByName("BaseCharacterComponent")?
            .GetMethods(f).FirstOrDefault(m => m.Name == "DropOverheadItem" && m.GetParameters().Length == 1 &&
                                               m.GetParameters()[0].ParameterType == typeof(bool));
        // Icon bubble over the partner's grave (visual stamps): CraftDefinition by craftId from the stamp.
        try
        {
            var gbType = AccessTools.TypeByName("GameBalance");
            var cdType = AccessTools.TypeByName("CraftDefinition");
            _gameBalanceMeProp = gbType?.GetProperty("me", BindingFlags.Public | BindingFlags.Static);
            var getData = gbType?.GetMethods(f | BindingFlags.Public).FirstOrDefault(m =>
                m.Name == "GetData" && m.IsGenericMethod && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(string));
            if (getData != null && cdType != null) _getCraftDefMethod = getData.MakeGenericMethod(cdType);
            _craftDefOutputField = cdType?.GetField("output", f);
            _craftDefIconField   = cdType?.GetField("icon", f);
            _bubbleItemDataType  = AccessTools.TypeByName("BubbleWidgetItemData");
            _bubbleItemCtor      = _bubbleItemDataType?.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length >= 1 &&
                                     c.GetParameters()[0].ParameterType == typeof(string));
            _bubbleItemIconIdField = _bubbleItemDataType?.GetField("icon_id", f);
            var widType2 = AccessTools.TypeByName("BubbleWidgetData")?.GetNestedType("WidgetID",
                               BindingFlags.Public | BindingFlags.NonPublic);
            if (widType2 != null) _widgetIdCraftingItem = Enum.Parse(widType2, "CraftingItem");
        }
        catch { }
        Multiplayer.Log?.LogInfo($"[CHOP] grave-bubble bindings: getData={_getCraftDefMethod != null} " +
            $"ctor={_bubbleItemCtor != null} widId={_widgetIdCraftingItem != null}");
        // WEATHER (0x1A): host-authoritative daily preset schedule.
        var envType = AccessTools.TypeByName("EnvironmentEngine");
        _envMeProp             = envType?.GetProperty("me", BindingFlags.Public | BindingFlags.Static);
        _findNatureMethod      = envType?.GetMethod("FindNatureWithoutRemoves", f);
        _tryRemoveNatureMethod = envType?.GetMethod("TryRemoveNatureWeatherState", f);
        _addNatureMethod       = envType?.GetMethod("AddNatureWeatherState", f);
        _weatherPresetGetMethod = AccessTools.TypeByName("WeatherPreset")
            ?.GetMethod("GetPreset", BindingFlags.Public | BindingFlags.Static);
        _getStatesFromPresetMethod = AccessTools.TypeByName("SwitchableWeatherState")
            ?.GetMethod("GetStatesFromPreset", BindingFlags.Public | BindingFlags.Static);
        Multiplayer.Log?.LogInfo($"[CHOP] weather bindings: me={_envMeProp != null} find={_findNatureMethod != null} " +
            $"rm={_tryRemoveNatureMethod != null} add={_addNatureMethod != null} " +
            $"preset={_weatherPresetGetMethod != null} states={_getStatesFromPresetMethod != null}");
        Multiplayer.Log?.LogInfo($"[CHOP] stockpile bindings: character={_componentsCharacterProp != null} " +
            $"overhead={_getOverheadItemMethod != null}");
        _getOverheadIconMethod = _itemType?.GetMethod("GetOverheadIcon", f, null, Type.EmptyTypes, null);
        _toJsonMethod    = _itemType?.GetMethod("ToJSON", f, null, new[] { typeof(int) }, null);
        _fromJsonOverwriteMethod = AccessTools.TypeByName("UnityEngine.JsonUtility")
            ?.GetMethod("FromJsonOverwrite", BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(string), typeof(object) }, null);
        _directionType   = AccessTools.TypeByName("Direction");
        _dropItemsMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DropItems" && m.GetParameters().Length == 2);
        // Singular DropItem(Item, Direction, Vector3, int, bool) — spawns a CARRIED corpse
        // (unlike DropItems-plural, which makes auto-collectible loot). For replay on the observer.
        _dropItemSingularMethod = _wgoType.GetMethods(f | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "DropItem"
                              && m.GetParameters().Length >= 1
                              && m.GetParameters()[0].ParameterType.Name == "Item");

        _ready = _objIdField != null && _doActionMethod != null;
        if (!_ready)
            Multiplayer.Log?.LogWarning("[CHOP] Reflection not initialized — chop sync disabled");
    }

    public static void Reset() { _pendingDestroys.Clear(); _pendingDrops.Clear(); }

    // Objects whose DESTRUCTION we sync (packets 0x09 hit, 0x0A destroy):
    // trees (tree*) and ground stones (stone_N). Mine veins (steep_*) do NOT belong here —
    // they don't disappear, replaying DoAction/ReplaceWithObject would break the state.
    private static bool IsDestroySyncTarget(MonoBehaviour wgo)
    {
        EnsureReflection();
        if (!_ready || wgo == null) return false;
        var id = _objIdField.GetValue(wgo) as string;
        if (id == null) return false;
        return id.StartsWith("tree") || id.StartsWith("stone");
    }

    // Objects whose STATE CHANGE we sync (packet 0x0A — an after_hp_0 transition or
    // destruction). Wider than IsDestroySyncTarget: besides trees and stones, this includes
    // a garden's BUILD objects (the _place / builddesk scaffold) — digging the scaffold
    // in garden_empty transforms cleanly (Stage 3). PLANTABLE garden beds are NO LONGER synced
    // here: their object transitions (planting/harvest/growth) are owned by 0x21. Before 2026-06-17
    // they matched via garden* and planting (garden_empty→garden_carrot) sent a spurious 0x0A —
    // the receiver didn't know the target obj_id → "Object destroyed" → the bed vanished for the friend (uid=39506).
    // GRAVES (grave*) ARE DELIBERATELY EXCLUDED: they are multi-stage (parts fall as
    // separate DropItems), and a forced ReplaceWithObject/Destroy on the receiver destroyed
    // the whole grave instead of transitioning to the next stage (live test 2026-06-01:
    // "the grave just disappeared" + the gravedigger's work-state / skull got stuck). Grave loot
    // is still synced via 0x0B; their transformation must be done by a separate, precise
    // path (knowing the game's stage-transition method — a separate recon).
    // Mine veins (steep_*) — also NOT here: they don't disappear, ReplaceWithObject
    // would break the state (we sync only their loot via 0x0B / IsLootSyncTarget).
    // 0x09 (the cosmetic hit) stays narrower — only IsDestroySyncTarget.
    private static bool IsTransformSyncTarget(MonoBehaviour wgo)
    {
        EnsureReflection();
        if (!_ready || wgo == null) return false;
        var id = _objIdField.GetValue(wgo) as string;
        if (id == null) return false;
        return id.StartsWith("tree") || id.StartsWith("stone") ||
               (IsGardenRelated(wgo) && !IsGardenTarget(wgo)) || IsNatureGatherTarget(id);
    }

    // Objects whose LOOT we sync (0x0B). Wider than IsDestroySyncTarget — anything that on DoAction calls
    // WGO.DropItems, whether it disappears, transforms or stays: tree*/stone* (→0x09/0x0A), steep* (mine veins),
    // grave*/garden* (same DoAction→RewardForWork→DropItems path, recon 2026-05-27). Replaying DropItems on the
    // receiver is safe for any WGO (just makes ground loot, doesn't touch the object's state).
    private static bool IsLootSyncTarget(MonoBehaviour wgo)
    {
        EnsureReflection();
        if (!_ready || wgo == null) return false;
        var id = _objIdField.GetValue(wgo) as string;
        if (id == null) return false;
        // Garden — WITHOUT loot dupes (Zonda 2026-06-17): plantable beds/orchards do NOT copy loot on
        // harvest/planting. The harvester gets the crop LOCALLY (game does DropItems itself); 0x0B disabled →
        // zero dupes. Bed state still driven by 0x21. Trees/stone/logs keep their shared loot (coop work).
        if (IsGardenRelated(wgo)) return false;
        return id.StartsWith("tree") || id.StartsWith("stone") ||
               id.StartsWith("steep") || id.StartsWith("grave") ||
               IsNatureGatherTarget(id) ||
               IsWorkbench(wgo);   // Phase 2 craft: a manual craft drops its output via DropItems onto the ground
                                   // (decompiled 83249) — shared loot replays to the friend. Auto-craft into the
                                   // station's inventory goes separately via the 0x0D inv-hash.
    }

    // Natural gatherables: flower*/mushroom*/bush*. Gathered like a bed (single-stage hp→0 → DropItems →
    // after_hp_0), so we sync transform (0x0A) + loot (0x0B). SPAWNERS EXCLUDED (invisible respawn points —
    // touching them breaks regrowth). No grave-style freeze risk: _linked_worker=null (RECON 2026-06-01) and
    // they're NOT in the 0x09 hit.
    private static bool IsNatureGatherTarget(string id)
    {
        if (id == null || id.Contains("spawner")) return false;
        return id.StartsWith("flower") || id.StartsWith("mushroom") ||
               id.StartsWith("bush");
    }

    // B-2: graves — sync stage VISUALS via 0x0D (RedrawPart/ReplaceWithObject); loot (incl. corpse) on 0x0B.
    // Separate from transform(0x0A): a grave must NOT be force-ReplaceWithObject'd into after_hp_0 (empty).
    private static bool IsGraveTarget(MonoBehaviour wgo)
    {
        EnsureReflection();
        if (!_ready || wgo == null) return false;
        var id = _objIdField.GetValue(wgo) as string;
        return id != null && id.StartsWith("grave");
    }

    // uids of buildings placed/received via the 0x15 spawn primitive this session. We sync THEIR build stages
    // via 0x0D. Tracking by uid (not a wide obj_id predicate) so we don't flood every WGO; the shared uid matches.
    private static readonly HashSet<long> _syncedBuildUids = new HashSet<long>();
    private static void TrackBuildUid(long uid) { if (uid != 0) _syncedBuildUids.Add(uid); }

    // State-replication target via 0x0D: a grave OR a synced building (by uid). Extends grave-only
    // to Phase 2 building, while staying narrow (only 0x15 objects, not the whole world).
    private static bool IsStateRepTarget(MonoBehaviour wgo)
    {
        // The whole garden — NOT via 0x0D state-rep (plantables via 0x21, scaffolds via CHOP). WIDE
        // IsGardenRelated (not IsGardenTarget!): the garden_empty_place scaffold lands in _syncedBuildUids via
        // 0x15, so without this it would leak into 0x0D (Stage 1, 2026-06-16: garden via three paths → conflict).
        if (IsGardenRelated(wgo)) return false;
        if (IsGraveTarget(wgo)) return true;
        if (IsWorkbench(wgo)) return true;   // Phase 2: crafting on stations (obj_def.has_craft)
        if ((_syncedBuildUids.Count == 0 && _knownChestUids.Count == 0) ||
            wgo == null || _uniqueIdField == null) return false;
        try
        {
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            return _syncedBuildUids.Contains(uid) || _knownChestUids.Contains(uid);   // Phase 3: chests
        }
        catch { return false; }
    }

    // A workbench/craft station = obj_def.has_craft (decompiled 78335). The craft state (output) sits in
    // the station's INVENTORY, not in obj_id → an inventory-sensitive signature is needed (below), otherwise dedup suppresses it.
    private static bool IsWorkbench(MonoBehaviour wgo)
    {
        if (_objDefField == null || _hasCraftField == null || wgo == null) return false;
        // Gardens/orchards have their own path (plantables — 0x21, scaffolds — CHOP), NOT the craft-station
        // one (even though planting has has_craft=true). The WIDE IsGardenRelated removes the whole garden from
        // the craft queue (0x17), claim arbitration (0x18, IsArbitratedStation) and IsStateSyncedStation. Harvest
        // loot does NOT suffer: IsLootSyncTarget matches a bed via the id "garden", not via IsWorkbench.
        if (IsGardenRelated(wgo)) return false;
        try
        {
            var od = _objDefField.GetValue(wgo);
            return od != null && Convert.ToBoolean(_hasCraftField.GetValue(od));
        }
        catch { return false; }
    }

    // The "player near the workbench" radius (world units). Craft interaction is close-up (~150-300 units
    // per the live log: loot fell ~160 from the player), while dev objects bat_test are 2000+ units — a
    // threshold of 1200 cleanly separates them. Gates only workbenches (graves/buildings are always close-up, no gate).
    private const float WORKBENCH_SYNC_RANGE = 1200f;

    // Is the local player within range of the object (by XY)? A cheap sqrMagnitude check.
    private static bool NearLocalPlayer(MonoBehaviour wgo, float range)
    {
        if (wgo == null) return false;
        var lp = GetLocalPlayerWgo();
        if (lp == null) return false;   // player not found — do NOT sync (better to skip than to flood)
        Vector2 d = (Vector2)wgo.transform.position - (Vector2)lp.transform.position;
        return d.sqrMagnitude <= range * range;
    }

    // Hash of the workbench inventory CONTENTS (id+value pairs). Changes when materials are loaded /
    // output added / removed → the stage signature changes → 0x0D syncs. STABLE during the craft itself
    // (the inventory doesn't change, only progress) → no flooding. 30 bits (fits into variation next to the body bit).
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
                // + a count of the NESTED inventory (Phase 3): moving items INTO A BAG inside a chest
                // doesn't change the bag's id/value — without this the hash wouldn't budge and the change wouldn't sync.
                int nested = 0;
                try { nested = (_itemInventoryField.GetValue(it) as System.Collections.IList)?.Count ?? 0; } catch { }
                unchecked { h = h * 31 + id.GetHashCode(); h = h * 31 + val; h = h * 31 + nested; }
            }
            return h & 0x3FFFFFFF;   // 30 bits
        }
        catch { return 0; }
    }

    // ── CRAFT-QUEUE SYNC (0x17, Phase 2 coop craft — STAGE 1: queue VISIBILITY) ──────────────
    // craft_queue sits in CraftComponent, NOT in _data → 0x0D doesn't carry it. A separate packet: on a
    // queue change (EnqueueCraft / start / finish) we send the friend the list {id,n,infinite}; they
    // rebuild craft_queue + RedrawBubble → they see the same windows above the stations. Progress already
    // goes via 0x0D. Gated by proximity (like 0x0D) + deduped by the queue hash — no flooding.
    public struct CraftQ { public string id; public int n; public bool infinite; public bool synthetic; }
    private static readonly Dictionary<long,int> _lastSentCraftQueueHash = new Dictionary<long,int>();
    private static readonly Dictionary<long,int> _lastAppliedCraftQueueHash = new Dictionary<long,int>();
    // Instances of the SYNTHETIC CraftQueueItem created by ApplyRemoteCraftQueue (a mirror of the friend's
    // active craft). When sending our own queue we skip them (Stage 3a) — otherwise enqueuing on the friend's
    // station would return the owner their own active craft as a REAL queue item (a dupe).
    private static readonly Dictionary<long, object> _mirrorSyntheticItems = new Dictionary<long, object>();

    // CraftComponent from a WGO — via wgo.components.craft (ComponentsManager). NOT Unity GetComponent:
    // WorldGameObjectComponent doesn't inherit MonoBehaviour, the game keeps them in its own dictionary.
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
                // A mirror of the FRIEND's active craft (Stage 3a): don't send it back as a real item.
                if (mirroredSynthetic != null && ReferenceEquals(it, mirroredSynthetic)) continue;
                var cq = new CraftQ
                {
                    id       = _cqiIdField?.GetValue(it) as string ?? "",
                    n        = _cqiNField != null ? Convert.ToInt32(_cqiNField.GetValue(it)) : 0,
                    infinite = _cqiInfiniteField != null && Convert.ToBoolean(_cqiInfiniteField.GetValue(it))
                };
                if (!string.IsNullOrEmpty(cq.id)) list.Add(cq);
            }
            // The ACTIVE craft as a synthetic FIRST item (live test 3, 2026-06-11): TryStartCraftFromQueue does
            // --n and REMOVES the item (decomp 83782-85), so a single craft leaves craft_queue empty while the
            // station works. The receiver draws the bubble from craft_queue[0], so a synthetic item shows it.
            // :r: crafts (demolition) aren't mirrored — already synced by 0x16.
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

    // Called from the EnqueueCraft Postfix (__instance = CraftComponent — NOT a MonoBehaviour, hence object).
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

    // Cancelling a craft (Cancel dialog: craft_queue.Clear()+Cancel(), decomp 48233): send the emptied queue +
    // immediately release the claim. The material refund goes via DropItems → shared loot 0x0B.
    public static void OnLocalCraftCancelled(object craftComponent)
    {
        if (ApplyingRemoteChop || !Connected() || craftComponent == null) return;
        EnsureReflection();
        try
        {
            var wgo = _componentWgoProp?.GetValue(craftComponent) as MonoBehaviour;
            if (wgo == null) return;
            SendCraftQueue(wgo);
            CheckLocalCraftStopped(wgo);
        }
        catch { }
    }

    // Send the station's queue to the friend (0x17). Proximity gate + hash dedup.
    public static void SendCraftQueue(MonoBehaviour wgo)
    {
        if (ApplyingRemoteChop || !Connected() || wgo == null) return;
        EnsureReflection();
        if (_craftComponentType == null || _uniqueIdField == null) return;
        // GRAVES ARE EXCLUDED: a grave also has_craft=True (a craft interaction), but its queue = digging.
        // The mirror would clear the friend's active queue on co-dig (q.Clear), and the game also AUTO-STARTS
        // the queue while the object works (decompiled 88287: !IsCraftQueueEmpty && !is_crafting →
        // TryStartCraftFromQueue) → phantom digging + a double stage engine. Grave stages are
        // fully covered by 0x0D — they don't need the queue.
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
            // Cross-seeding (anti-ping-pong, like the echo-sig in 0x0D): this same queue, arriving
            // back from the friend, will be recognized as already applied and won't rebuild our live one.
            _lastAppliedCraftQueueHash[uid] = hash;

            // Prepare id bytes (skip empty/oversized), compute the payload
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
            Multiplayer.Log?.LogInfo($"[CHOP] Craft queue uid={uid} → {prepared.Count} item(s) (0x17)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] SendCraftQueue: {e.Message}"); }
    }

    // Receiver: we rebuild craft_queue from the packet + RedrawBubble. Under echo-guard (we DON'T call
    // EnqueueCraft — we build the items directly, so the hook doesn't fire and there's no echo).
    public static void ApplyRemoteCraftQueue(long uid, List<CraftQ> items)
    {
        EnsureReflection();
        if (_craftQueueField == null || _craftQueueItemType == null || items == null) return;
        var wgo = FindWgoByUniqueId(uid);
        if (wgo == null) return;   // not loaded for us (far away) — Stage 1 skips; pending — for later
        if (IsGraveTarget(wgo)) return;   // graves aren't mirrored (see SendCraftQueue); protection against mixed builds
        var cc = GetCraftComponent(wgo);
        if (cc == null) return;

        int hash = CraftQueueHash(items);
        if (_lastAppliedCraftQueueHash.TryGetValue(uid, out var prev) && prev == hash) return;
        _lastAppliedCraftQueueHash[uid] = hash;
        // Cross-seeding (anti-ping-pong): our local triggers, seeing this same mirror queue,
        // won't send it back to the sender.
        _lastSentCraftQueueHash[uid] = hash;

        ApplyingRemoteChop = true;
        try
        {
            var q = _craftQueueField.GetValue(cc) as System.Collections.IList;
            if (q == null) return;
            q.Clear();
            _mirrorSyntheticItems.Remove(uid);   // the old synthetic instance went away with Clear
            foreach (var c in items)
            {
                var cqi = Activator.CreateInstance(_craftQueueItemType);
                _cqiIdField?.SetValue(cqi, c.id);
                _cqiNField?.SetValue(cqi, c.n);
                _cqiInfiniteField?.SetValue(cqi, c.infinite);
                q.Add(cqi);
                if (c.synthetic) _mirrorSyntheticItems[uid] = cqi;   // mirror of the partner's active craft
            }
            if (_redrawBubbleMethod != null)
            {
                var pars = _redrawBubbleMethod.GetParameters();
                _redrawBubbleMethod.Invoke(wgo, pars.Length == 1 ? new object[] { null } : new object[0]);
            }
            Multiplayer.Log?.LogInfo($"[CHOP] Craft queue uid={uid} applied ← {items.Count} item(s)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] ApplyRemoteCraftQueue: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
    }

    // ── STAGE 3a: STATION OWNER ARBITRATION (0x18 claim/release) ──────────────────────────────
    // A double craft output is possible ONLY if BOTH machines drove is_crafting=true on the same
    // station (CraftComponent.DoAction is gated by is_crafting, decompiled 83086). CraftReally (83821)
    // — the single funnel for ALL starts (GUI/queue/zombie/gratitude/auto), materials are consumed inside.
    // Model: my start → claim 0x18; the partner blocks local starts on a busy station.
    // Block on TWO levels: TryStartCraftFromQueue (because it does --n and removes the item BEFORE
    // CraftReally, decompiled 83782 — blocking only CraftReally would silently eat the queue) + CraftReally
    // (the direct Craft() path from the GUI). Fail-open: a claim with no updates dies after CRAFT_CLAIM_TTL;
    // the owner re-claims in TickCraftClaims while the craft is alive (including a paused craft).
    private const float CRAFT_CLAIM_TTL     = 60f;
    private const float CRAFT_CLAIM_REFRESH = 20f;
    private struct CraftClaim { public string craftId; public float time; }
    private static readonly Dictionary<long, CraftClaim> _remoteCrafting = new Dictionary<long, CraftClaim>();
    private static readonly Dictionary<long, CraftClaim> _localCrafting  = new Dictionary<long, CraftClaim>();
    private static readonly Dictionary<long, float> _lastBlockLogTime = new Dictionary<long, float>();
    private static float _claimRefreshTimer;
    // Time of the last blocked start — to replace the game's "not_enough_resources" (the game shows
    // it right after a blocked start, decompiled 88292) with an honest "in use by your partner".
    private static float _lastCraftBlockTime = -999f;
    public static bool WasCraftBlockedRecently => Time.realtimeSinceStartup - _lastCraftBlockTime < 1f;

    // "In use by your partner" across ALL game localizations. Codes — GJL.LANGUAGES (firstpass decompile):
    // en de fr pt-br es ru it pl ja zh_cn ko. The current language — GameSettings.me.language (92625);
    // empty/unknown → en (the same as the game itself does: "Language not found. Loading EN").
    // CJK glyphs are safe: the text goes through the native Say pipeline, which sets the current locale's font.
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

    // A station under arbitration = a workbench, but NOT a grave (graves have their own proven co-dig model).
    private static bool IsArbitratedStation(MonoBehaviour wgo) => IsWorkbench(wgo) && !IsGraveTarget(wgo);

    private static bool IsWgoRemoving(MonoBehaviour wgo)
    {
        if (_isRemovingProp == null || wgo == null) return false;
        try { return Convert.ToBoolean(_isRemovingProp.GetValue(wgo)); }
        catch { return false; }
    }

    // hidden crafts = system/ambient (bat_remove/slime_remove etc.): the player doesn't drive them (gate 83086)
    // and no bubble shows. Arbitration for them is HARMFUL: they start on EVERY machine → eternal claims (live
    // test 2026-06-11: ~14 stations re-claimed in a loop) and mutual blocking if both players share a zone.
    private static bool IsHiddenCraftDef(object craftDef)
    {
        if (craftDef == null || _craftDefHiddenField == null) return false;
        try { return Convert.ToBoolean(_craftDefHiddenField.GetValue(craftDef)); }
        catch { return false; }
    }

    // Prefix TryStartCraftFromQueue + CraftReally: block a LOCAL start if the partner holds the station.
    // Throttled log (88287 calls TryStart every work tick). craftArg = CraftReally's craft arg (a second
    // demolition-protection layer, independent of is_removing reflection).
    public static bool ShouldBlockLocalCraftStart(object craftComponent, object craftArg = null)
    {
        if (ApplyingRemoteChop || !Connected() || craftComponent == null) return false;
        if (_remoteCrafting.Count == 0) return false;
        EnsureReflection();
        if (_componentWgoProp == null || _uniqueIdField == null) return false;
        try
        {
            // Never block a remove-craft (second layer): a block → ProcessRemove → instant DestroyMe
            // (decomp 44299/115655). First layer = IsWgoRemoving below. Hidden crafts also never blocked.
            if (craftArg != null)
            {
                if (IsHiddenCraftDef(craftArg)) return false;
                var argId = _craftDefIdField?.GetValue(craftArg) as string;
                if (argId != null && argId.Contains(":r:")) return false;
            }
            var wgo = _componentWgoProp.GetValue(craftComponent) as MonoBehaviour;
            if (wgo == null || !IsArbitratedStation(wgo)) return false;
            // NEVER BLOCK DEMOLITION: a blocked Craft() in ProcessRemovingCraft (44299) immediately calls
            // ProcessRemove() = INSTANT DestroyMe → would demolish the building. Demolition stays on the old model + 0x16.
            if (IsWgoRemoving(wgo)) return false;
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            if (!_remoteCrafting.TryGetValue(uid, out var claim)) return false;
            if (Time.realtimeSinceStartup - claim.time > CRAFT_CLAIM_TTL)
            {
                _remoteCrafting.Remove(uid);   // fail-open: a stale claim doesn't block forever
                return false;
            }
            _lastCraftBlockTime = Time.realtimeSinceStartup;
            if (!_lastBlockLogTime.TryGetValue(uid, out var lt) || Time.realtimeSinceStartup - lt > 2f)
            {
                _lastBlockLogTime[uid] = Time.realtimeSinceStartup;
                Multiplayer.Log?.LogInfo($"[CHOP] Start blocked: station uid={uid} taken by the partner ({claim.craftId})");
            }
            return true;
        }
        catch { return false; }
    }

    // Postfix CraftReally (__result=true): the craft actually started — we claim the station.
    public static void OnLocalCraftStarted(object craftComponent)
    {
        if (ApplyingRemoteChop || !Connected() || craftComponent == null) return;
        EnsureReflection();
        if (_componentWgoProp == null || _uniqueIdField == null) return;
        try
        {
            var wgo = _componentWgoProp.GetValue(craftComponent) as MonoBehaviour;
            // GRAVES: the claim is PURELY VISUAL (bubble + progress bar for the partner, Zonda's request 2026-06-11).
            // Grave blocking is NOT enabled (ShouldBlock filters it out via IsArbitratedStation),
            // a claim race for graves doesn't cancel the craft (a branch in ApplyRemoteCraftClaim) — co-dig stays intact.
            if (wgo == null || !(IsArbitratedStation(wgo) || IsGraveTarget(wgo))) return;
            if (IsWgoRemoving(wgo)) return;   // demolition is outside arbitration (old model + 0x16)
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            string craftId = "";
            if (_currentCraftField != null && _craftDefIdField != null)
            {
                var cur = _currentCraftField.GetValue(craftComponent);
                if (cur != null)
                {
                    // We do NOT claim ambient/system hidden crafts (bat_remove/slime_remove…):
                    // they run by themselves on both machines forever → a flood of claims + a mutual block.
                    if (IsHiddenCraftDef(cur)) return;
                    craftId = _craftDefIdField.GetValue(cur) as string ?? "";
                }
            }
            if (craftId.Contains(":r:")) return;   // a remove-craft — also outside arbitration
            // Throttle: zero-time crafts loop up to 500 CraftReally per ONE queue call
            // (decompiled 83787-93) — don't send 500 claims; the same craftId within 1s = already claimed.
            if (_localCrafting.TryGetValue(uid, out var prev) && prev.craftId == craftId &&
                Time.realtimeSinceStartup - prev.time < 1f)
            {
                prev.time = Time.realtimeSinceStartup;
                _localCrafting[uid] = prev;
                return;
            }
            _localCrafting[uid] = new CraftClaim { craftId = craftId, time = Time.realtimeSinceStartup };
            _lastSentProgressQ[uid] = -1;   // 3b: a fresh craft → the first progress tick gets through (q=0)
            SendCraftClaim(uid, true, craftId);
            // BUGFIX (test 3b): a craft started NOT via EnqueueCraft (Craft() directly — a furnace,
            // "put to work" from the GUI) didn't update the queue mirror → the partner got a bar with no bubble
            // (furnace) or a stale icon of the old queue. CraftReally is the funnel for ALL starts, so the fresh
            // queue (with the synthetic active item) flies on every start. Dedup mutes the duplicates.
            SendCraftQueue(wgo);
        }
        catch { }
    }

    // Called from GraveWorkSyncPatch (OnWorkFinished/OnCraftStateChanged): the craft finished →
    // release; still alive (batch amount>1 / pause) → refresh the claim.
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

    // Periodic tick (from SteamManager.Update): re-claim live crafts (keeps the partner's TTL alive),
    // cleanup of dead ones (a missed stop: cancel via GUI, station demolition, etc.).
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
            Multiplayer.Log?.LogInfo($"[CHOP] Station claim uid={uid}: {(start ? "START " + craftId : "STOP")} (0x18)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] SendCraftClaim: {e.Message}"); }
    }

    public static void ApplyRemoteCraftClaim(long uid, bool start, string craftId)
    {
        if (!start) { _remoteCrafting.Remove(uid); ClearRemoteProgressBar(uid); return; }
        // RACE of a simultaneous start (window ~RTT): both started and exchanged a claim.
        // Deterministic tiebreak: the HOST wins. The host ignores the other's claim (the client will cancel itself
        // after receiving the host's); the client does a mini-cancel of its craft and registers a block.
        if (_localCrafting.ContainsKey(uid))
        {
            // GRAVES (visual claims): co-dig is NORMAL, not a race. We cancel nothing, both
            // claims live (grave blocking doesn't exist anyway); the progress mirror is gated by
            // the local is_crafting in ApplyRemoteCraftProgress.
            try
            {
                var raceWgo = FindWgoByUniqueId(uid);
                if (raceWgo != null && IsGraveTarget(raceWgo))
                {
                    _remoteIconWidgets.Remove(uid);
                    _remoteCrafting[uid] = new CraftClaim { craftId = craftId, time = Time.realtimeSinceStartup };
                    return;
                }
            }
            catch { }
            if (SteamNetwork.Role == NetworkRole.Host)
            {
                Multiplayer.Log?.LogWarning($"[CHOP] Start race uid={uid}: I'm the host — I stay the owner");
                return;
            }
            Multiplayer.Log?.LogWarning($"[CHOP] Start race uid={uid}: yielding to the host (mini-cancel, spent materials lost)");
            MiniCancelLocalCraft(uid);
        }
        _remoteIconWidgets.Remove(uid);   // a new craft (new stage) → the icon will be rebuilt
        _remoteCrafting[uid] = new CraftClaim { craftId = craftId, time = Time.realtimeSinceStartup };
    }

    // ── STAGE 3b: LIVE PROGRESS BAR ON THE PARTNER (0x18 flag=2) ─────────────────────────────────
    // Owner sends quantized progress (10% steps). Receiver sets wgo.progress (what the native bar draws from)
    // + injects a BubbleWidgetProgressData. We do NOT touch is_crafting (else ReallyUpdateComponent leaks
    // is_auto = double drive, 84155). Native RefreshComponentBubbleData clears the bar at is_crafting=false (84633)
    // → the Postfix restores it.
    private static readonly Dictionary<long, int> _lastSentProgressQ = new Dictionary<long, int>();
    private static readonly Dictionary<long, object> _remoteBarWidgets = new Dictionary<long, object>();

    // Source for the bar's ProgressDelegate: reads the live wgo.progress (updated by packets).
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

    // Postfix CraftComponent.DoAction (every work tick of the owner): progress in 10% steps.
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
            packet[9]  = 2;                       // flag=2: progress
            packet[10] = (byte)(q * 10);          // 0-100
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
        }
        catch { }
    }

    // Progress receiver: wgo.progress + bar + refreshing the claim's TTL (progress = a sign of life).
    public static void ApplyRemoteCraftProgress(long uid, float p)
    {
        EnsureReflection();
        // Progress with no claim (a missed start) — register a block defensively: the station is clearly working.
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
            // CO-DIG PROTECTION (graves with visual claims): if I MYSELF am crafting/digging this
            // wgo right now (is_crafting locally) — the other's progress must NOT overwrite mine (my bar = my DoAction).
            try
            {
                var myCc = GetCraftComponent(wgo);
                if (myCc != null && _isCraftingField != null &&
                    Convert.ToBoolean(_isCraftingField.GetValue(myCc))) return;
            }
            catch { }
            _wgoProgressProp.SetValue(wgo, Mathf.Clamp01(p));
            EnsureRemoteProgressBar(wgo, uid);
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] ApplyRemoteCraftProgress: {e.Message}"); }
    }

    // The partner's work icon (bubble): from CraftDefinition by the claim's craftId — output[0] (like the game
    // in the queue, 84608-11; for grave-digging crafts the output = a grave part) or a sprite-icon.
    private static readonly Dictionary<long, object> _remoteIconWidgets = new Dictionary<long, object>();
    private static object BuildClaimIconWidget(string craftId)
    {
        try
        {
            if (string.IsNullOrEmpty(craftId) || _getCraftDefMethod == null ||
                _gameBalanceMeProp == null || _bubbleItemDataType == null) return null;
            var gb = _gameBalanceMeProp.GetValue(null);
            var cd = gb != null ? _getCraftDefMethod.Invoke(gb, new object[] { craftId }) : null;
            if (cd == null) return null;
            // 1) item icon from output[0].id (the item's native look with a frame)
            var output = _craftDefOutputField?.GetValue(cd) as System.Collections.IList;
            if (output != null && output.Count > 0 && output[0] != null && _bubbleItemCtor != null)
            {
                var outId = _itemIdField?.GetValue(output[0]) as string;
                if (!string.IsNullOrEmpty(outId))
                {
                    var ps = _bubbleItemCtor.GetParameters();
                    var args = new object[ps.Length];
                    args[0] = outId;
                    for (int i = 1; i < ps.Length; i++)
                        args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                                : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                    return _bubbleItemCtor.Invoke(args);
                }
            }
            // 2) fallback: the craft's sprite icon
            var icon = _craftDefIconField?.GetValue(cd) as string;
            if (!string.IsNullOrEmpty(icon) && _bubbleItemIconIdField != null)
            {
                var w = Activator.CreateInstance(_bubbleItemDataType);
                _bubbleItemIconIdField.SetValue(w, icon);
                return w;
            }
        }
        catch { }
        return null;
    }

    // The native bar widget into the CraftingProgress slot (the one the game sets when is_crafting).
    // The delegate reads wgo.progress → the bar lives on its own, packets just move the value.
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
            // Work icon (bubble over the partner's grave; for stations — consistent with the 0x17 queue).
            if (_widgetIdCraftingItem != null)
            {
                if (!_remoteIconWidgets.TryGetValue(uid, out var iconW))
                {
                    string cid = _remoteCrafting.TryGetValue(uid, out var cl) ? cl.craftId : null;
                    iconW = BuildClaimIconWidget(cid);
                    _remoteIconWidgets[uid] = iconW;   // we cache null too (nothing to draw)
                }
                if (iconW != null)
                    _setBubbleWidgetDataMethod.Invoke(wgo, new object[] { iconW, _widgetIdCraftingItem });
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] EnsureRemoteProgressBar: {e.Message}"); }
    }

    // Station stop/release: remove the bar and icon (slots → null, like the game at 84632-33).
    private static void ClearRemoteProgressBar(long uid)
    {
        bool hadIcon = _remoteIconWidgets.Remove(uid);
        if (!_remoteBarWidgets.Remove(uid) && !hadIcon) return;
        try
        {
            var wgo = FindWgoByUniqueId(uid);
            if (wgo == null || _setBubbleWidgetDataMethod == null) return;
            if (_widgetIdCraftingProgress != null)
                _setBubbleWidgetDataMethod.Invoke(wgo, new object[] { null, _widgetIdCraftingProgress });
            if (_widgetIdCraftingItem != null)
                _setBubbleWidgetDataMethod.Invoke(wgo, new object[] { null, _widgetIdCraftingItem });
        }
        catch { }
    }

    // Postfix CraftComponent.RefreshComponentBubbleData: the native refresh wiped the bar (is_crafting=false
    // for us) → we put it back while the station is under the partner's active claim.
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

    // A lost race: cancel our own craft without finishing it. Materials were already consumed by CraftReally —
    // an accepted loss (a rare case, the log warns). We don't touch progress (a start resets it to 0).
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

    // ── PHASE 3: SHARED CHESTS (C2; the C1 exchange = via a chest) ─────────────────────────────────
    // In GK you CAN'T drop an item from the inventory onto the ground → exchange naturally goes through a chest.
    // Mechanic: all player↔chest transfers go through ChestGUI.MoveItem (decompiled 45167)
    // → the Postfix sends the chest's state via the existing 0x0D (full JSON _data, inventory inside). uid tracking
    // is narrow (only chests opened this session, like _syncedBuildUids — not the whole world). The signature
    // becomes inv-sensitive via WorkbenchInvHash (+a nested counter). The receiver — the existing restore;
    // if the chest is OPEN for them — FullRedrawPanels (live refresh, sees the changes immediately).
    // KNOWN EDGE (accepted, like co-dig): both transfer in ONE chest within RTT →
    // last-write-wins, a possible dupe/disappearance of an item. Don't edit one chest together at the same time.
    private static readonly HashSet<long> _knownChestUids = new HashSet<long>();

    public static void TrackChestWgo(object wgoObj)
    {
        if (!Connected()) return;
        EnsureReflection();
        try
        {
            var wgo = wgoObj as MonoBehaviour;
            if (wgo == null || _uniqueIdField == null) return;
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            if (_knownChestUids.Add(uid))
                Multiplayer.Log?.LogInfo($"[CHOP] Chest uid={uid} added to sync tracking");
        }
        catch { }
    }

    private static bool IsTrackedChest(MonoBehaviour wgo)
    {
        if (_knownChestUids.Count == 0 || wgo == null || _uniqueIdField == null) return false;
        try { return _knownChestUids.Contains(Convert.ToInt64(_uniqueIdField.GetValue(wgo))); }
        catch { return false; }
    }

    // ── CHEST SYNC BY OPERATIONS (0x19) — true CONCURRENT sync ──────────────────────────
    // Full states (the first C2 version) broke under contention: in-flight states overwrote parallel
    // changes → resurrection of items (a water dupe, live test 2026-06-11). OPERATIONS ("took 2 waters",
    // "put 3 boards") apply on top of any state: different items don't conflict
    // at all, one stack converges arithmetically (A −2 and B −3 from five → 0 on both). Both can
    // edit the chest SIMULTANEOUSLY — no locks. Residual edge: both took the LAST item
    // within the RTT window → clamp at zero → 1 extra instance (rare, a log warning).
    // Operations = a DIFF of the contents before/after MoveItem (Prefix snapshot → Postfix compare): catches
    // the exact actual change (including the game's own clamps), without trusting the arguments.
    // RECONCILIATION: closing the GUI sends a full 0x0D state (an idempotent snapshot) — but ONLY if
    // the partner hasn't touched this chest in the last CHEST_SNAPSHOT_QUIESCE s (so as not to overwrite their fresh changes).
    public struct ChestOp { public string id; public int delta; public string json; }
    private const float CHEST_SNAPSHOT_QUIESCE = 2f;
    private static readonly Dictionary<long, float> _lastRemoteChestOpTime = new Dictionary<long, float>();
    // Snapshot of the contents before MoveItem (the call is synchronous and single-threaded — one static slot).
    private static long _moveSnapUid = -1;
    private static Dictionary<string, int> _moveSnapCounts;
    private static Dictionary<string, int> _moveSnapNested;

    // id → total value across stacks; nested — total size of nested inventories (bag-in-chest).
    private static Dictionary<string, int> ReadChestCounts(MonoBehaviour wgo, Dictionary<string, int> nested)
    {
        var map = new Dictionary<string, int>();
        try
        {
            var data = _dataField?.GetValue(wgo);
            var inv = data != null ? _itemInventoryField?.GetValue(data) as System.Collections.IList : null;
            if (inv == null) return map;
            foreach (var it in inv)
            {
                if (it == null) continue;
                string id = _itemIdField?.GetValue(it) as string ?? "";
                if (id.Length == 0) continue;
                int val = 1;
                try { val = Convert.ToInt32(_itemValueField.GetValue(it)); } catch { }
                map.TryGetValue(id, out var cur); map[id] = cur + val;
                if (nested != null)
                {
                    int n = 0;
                    try { n = (_itemInventoryField.GetValue(it) as System.Collections.IList)?.Count ?? 0; } catch { }
                    nested.TryGetValue(id, out var nc); nested[id] = nc + n;
                }
            }
        }
        catch { }
        return map;
    }

    // Prefix ChestGUI.MoveItem: a "before" snapshot (we block nothing).
    public static void CaptureChestBeforeMove(object chestGui)
    {
        _moveSnapUid = -1;
        if (!Connected() || chestGui == null) return;
        EnsureReflection();
        try
        {
            var wgo = _chestObjField?.GetValue(chestGui) as MonoBehaviour;
            if (wgo == null || _uniqueIdField == null) return;
            _moveSnapUid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            _moveSnapNested = new Dictionary<string, int>();
            _moveSnapCounts = ReadChestCounts(wgo, _moveSnapNested);
        }
        catch { _moveSnapUid = -1; }
    }

    // Postfix ChestGUI.MoveItem: a "before/after" diff → 0x19 operations (or a full state, if the change
    // is only inside a bag-in-chest — that can't be expressed as add/remove).
    public static void OnLocalChestChanged(object chestGui)
    {
        if (ApplyingRemoteChop || !Connected() || chestGui == null) return;
        EnsureReflection();
        try
        {
            var wgo = _chestObjField?.GetValue(chestGui) as MonoBehaviour;
            if (wgo == null || _uniqueIdField == null) return;
            TrackChestWgo(wgo);
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            if (_moveSnapUid != uid || _moveSnapCounts == null) return;   // no snapshot — nothing to send
            var nestedAfter = new Dictionary<string, int>();
            var after = ReadChestCounts(wgo, nestedAfter);

            var ops = new List<ChestOp>();
            foreach (var kv in after)
            {
                _moveSnapCounts.TryGetValue(kv.Key, out var before);
                if (kv.Value != before) ops.Add(new ChestOp { id = kv.Key, delta = kv.Value - before });
            }
            foreach (var kv in _moveSnapCounts)
                if (!after.ContainsKey(kv.Key)) ops.Add(new ChestOp { id = kv.Key, delta = -kv.Value });

            if (ops.Count == 0)
            {
                // Counts didn't change — possibly a change inside a bag-in-chest → full state.
                bool nestedChanged = false;
                foreach (var kv in nestedAfter)
                {
                    _moveSnapNested.TryGetValue(kv.Key, out var nb);
                    if (kv.Value != nb) { nestedChanged = true; break; }
                }
                if (nestedChanged) OnLocalGraveStateChanged(wgo);
                return;
            }
            // json for additions (quality/bag contents): we take the live stack of this id from the chest.
            for (int i = 0; i < ops.Count; i++)
                if (ops[i].delta > 0)
                {
                    var op = ops[i];
                    op.json = GetChestStackJson(wgo, op.id);
                    ops[i] = op;
                }
            SendChestOps(uid, ops);
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] OnLocalChestChanged: {e.Message}"); }
        finally { _moveSnapUid = -1; }
    }

    private static string GetChestStackJson(MonoBehaviour wgo, string id)
    {
        try
        {
            var data = _dataField?.GetValue(wgo);
            var inv = data != null ? _itemInventoryField?.GetValue(data) as System.Collections.IList : null;
            if (inv == null || _toJsonMethod == null) return "";
            foreach (var it in inv)
            {
                if (it == null) continue;
                if ((_itemIdField?.GetValue(it) as string) == id)
                    return _toJsonMethod.Invoke(it, new object[] { 0 }) as string ?? "";
            }
        }
        catch { }
        return "";
    }

    // 0x19: uid(8) count(1) [idLen(1) id delta(4) jsonLen(2) json]*
    private static void SendChestOps(long uid, List<ChestOp> ops)
    {
        try
        {
            int payload = 0;
            var idB = new List<byte[]>(ops.Count);
            var jsB = new List<byte[]>(ops.Count);
            foreach (var op in ops)
            {
                var ib = System.Text.Encoding.UTF8.GetBytes(op.id);
                var jb = op.delta > 0 && !string.IsNullOrEmpty(op.json)
                             ? System.Text.Encoding.UTF8.GetBytes(op.json) : new byte[0];
                if (jb.Length > 60000) jb = new byte[0];   // safety; the receiver will build it without quality
                idB.Add(ib); jsB.Add(jb);
                payload += 1 + ib.Length + 4 + 2 + jb.Length;
            }
            var packet = new byte[10 + payload];
            packet[0] = 0x19;
            BitConverter.GetBytes(uid).CopyTo(packet, 1);
            packet[9] = (byte)Math.Min(ops.Count, 255);
            int off = 10;
            for (int i = 0; i < ops.Count && i < 255; i++)
            {
                packet[off++] = (byte)idB[i].Length;
                Buffer.BlockCopy(idB[i], 0, packet, off, idB[i].Length); off += idB[i].Length;
                BitConverter.GetBytes(ops[i].delta).CopyTo(packet, off); off += 4;
                BitConverter.GetBytes((ushort)jsB[i].Length).CopyTo(packet, off); off += 2;
                Buffer.BlockCopy(jsB[i], 0, packet, off, jsB[i].Length); off += jsB[i].Length;
            }
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
            var descr = string.Join(", ", ops.ConvertAll(o => $"{(o.delta > 0 ? "+" : "")}{o.delta} {o.id}").ToArray());
            Multiplayer.Log?.LogInfo($"[CHOP] Chest uid={uid}: ops → {descr} (0x19)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] SendChestOps: {e.Message}"); }
    }

    // Receiver of 0x19: apply the deltas to OUR copy of the chest. delta>0 → a new stack (the json carries
    // quality/bag contents, value=delta on top); delta<0 → RemoveItemNoCheck (removes as many as available —
    // the clamp is natural; a shortfall = a last-item contest, a log warning).
    public static void ApplyRemoteChestOps(long uid, List<ChestOp> ops)
    {
        EnsureReflection();
        _lastRemoteChestOpTime[uid] = Time.realtimeSinceStartup;

        var wgo = FindWgoByUniqueId(uid);
        if (wgo == null)
        {
            Multiplayer.Log?.LogWarning($"[CHOP] 0x19: chest uid={uid} not found (zone not loaded?) — ops lost, reconciliation on the partner's GUI close");
            return;
        }
        var data = _dataField?.GetValue(wgo);
        if (data == null) return;
        // DIAGNOSTIC (test C2 #3: "ops ✓, but nothing changes"): does the GUI read the SAME
        // container we're mutating (the wgo.data property vs the _data field)?
        try
        {
            var dataProp = _wgoDataProp?.GetValue(wgo);
            if (dataProp != null && !ReferenceEquals(dataProp, data))
                Multiplayer.Log?.LogWarning($"[CHOP] 0x19 DIAG: wgo.data ≠ _data FOR uid={uid} — we're mutating an orphan!");
        }
        catch { }
        ApplyingRemoteChop = true;
        try
        {
            foreach (var op in ops)
            {
                if (op.delta < 0)
                {
                    int have = 0;
                    try
                    {
                        if (_dataGetTotalCountMethod != null)
                            have = Convert.ToInt32(_dataGetTotalCountMethod.Invoke(data, new object[] { op.id, true }));
                        else
                        {
                            // Fallback: a manual count over the top-level inventory (the method didn't bind).
                            var inv = _itemInventoryField?.GetValue(data) as System.Collections.IList;
                            if (inv != null)
                                foreach (var it in inv)
                                    if (it != null && (_itemIdField?.GetValue(it) as string) == op.id)
                                        have += Convert.ToInt32(_itemValueField?.GetValue(it) ?? 0);
                        }
                    }
                    catch { }
                    int take = Math.Min(have, -op.delta);
                    if (take < -op.delta)
                        Multiplayer.Log?.LogWarning($"[CHOP] 0x19: clamp {op.id} (requested −{-op.delta}, had {have}) — last-item contest");
                    if (take > 0 && _dataRemoveNoCheckMethod != null && _itemCtor != null)
                    {
                        var probe = _itemCtor.Invoke(new object[] { op.id, take });
                        var removed = _dataRemoveNoCheckMethod.Invoke(data, new object[] { probe, take, "", null, null });
                        int afterR = 0;
                        try { afterR = Convert.ToInt32(_dataGetTotalCountMethod?.Invoke(data, new object[] { op.id, true }) ?? -1); } catch { }
                        Multiplayer.Log?.LogInfo($"[CHOP] 0x19 DIAG: remove {op.id} take={take} ret={removed} had={have} now={afterR}");
                    }
                }
                else if (op.delta > 0 && _itemCtor != null && _dataAddItemMethod != null)
                {
                    var item = _itemCtor.Invoke(new object[] { op.id, 1 });
                    if (!string.IsNullOrEmpty(op.json) && _fromJsonOverwriteMethod != null)
                        try { _fromJsonOverwriteMethod.Invoke(null, new object[] { op.json, item }); } catch { }
                    _itemValueField?.SetValue(item, op.delta);
                    var ok = _dataAddItemMethod.Invoke(data, new object[] { item, true });
                    bool added = ok is bool ab && ab;
                    if (!added)
                    {
                        // AUDIT 2026-06-11: CanAddCount (72203) = inventory_size − inventory.Count →
                        // AddItem REFUSES on a full/divergent copy. But on the SENDER the item
                        // physically sits there — the copy must mirror it: a direct insert past the limit.
                        try
                        {
                            var invList = _itemInventoryField?.GetValue(data) as System.Collections.IList;
                            if (invList != null)
                            {
                                invList.Add(item);
                                added = true;
                                Multiplayer.Log?.LogWarning($"[CHOP] 0x19: AddItem refused ({op.id} x{op.delta}, copy full/divergent) — direct insert ✓");
                            }
                        }
                        catch (Exception ie) { Multiplayer.Log?.LogError($"[CHOP] 0x19: direct insert failed: {ie.Message}"); }
                    }
                    int afterA = 0;
                    try { afterA = Convert.ToInt32(_dataGetTotalCountMethod?.Invoke(data, new object[] { op.id, true }) ?? -1); } catch { }
                    Multiplayer.Log?.LogInfo($"[CHOP] 0x19 DIAG: add {op.id} x{op.delta} ok={added} now={afterA}");
                }
            }
            var descr = string.Join(", ", ops.ConvertAll(o => $"{(o.delta > 0 ? "+" : "")}{o.delta} {o.id}").ToArray());
            Multiplayer.Log?.LogInfo($"[CHOP] Chest uid={uid}: ops applied ← {descr} ✓");
            RefreshOpenChestGui(wgo);
            // Phase 3 (stockpiles): the pile's visual (logs/stone are drawn) — a force redraw.
            // For chests it's a no-op (they don't change their look), under the echo-guard — no re-send.
            if (_redrawMethod != null)
                try { _redrawMethod.Invoke(wgo, new object[] { true, false, false }); } catch { }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] ApplyRemoteChestOps: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
    }

    // ── STOCKPILES OF CARRIED RESOURCES (Phase 3): the same 0x19 op-channel ────────────────────────
    // Logs/stone are carried in hand: PUT = PutOverheadToWGO → wgo.AddToInventory (decompiled 74558,
    // already hooked by GraveAddBodySyncPatch); TAKE = WGO.GiveItemToPlayersHands (115845:
    // data.RemoveItem + into the hands). The op receiver is generic (mutates _data by uid) — the triggers are new.
    // 0x0D targets (graves/workbenches/buildings/chests) are NOT driven by ops (state drives them) — no duplication.

    // Targets driven by the FULL 0x0D STATE with their own triggers (ops for them = duplication).
    // Tracked chests/stockpiles are NOT included here — they're driven by ops (+ reconciliation snapshots).
    private static bool IsStateSyncedStation(MonoBehaviour wgo)
    {
        if (IsGraveTarget(wgo) || IsWorkbench(wgo)) return true;
        if (_syncedBuildUids.Count == 0 || _uniqueIdField == null) return false;
        try { return _syncedBuildUids.Contains(Convert.ToInt64(_uniqueIdField.GetValue(wgo))); }
        catch { return false; }
    }

    // Reverse map data(Item) → WGO (lazy scan, cached forever — _data lives with the object).
    private static readonly Dictionary<object, MonoBehaviour> _dataWgoMap = new Dictionary<object, MonoBehaviour>();
    private static MonoBehaviour MapDataToWgo(object data)
    {
        if (data == null) return null;
        if (_dataWgoMap.TryGetValue(data, out var cached)) return cached;
        MonoBehaviour found = null;
        try
        {
            foreach (var w in ScanWgosCached())
            {
                var mb = w as MonoBehaviour;
                if (mb == null) continue;
                if (ReferenceEquals(_dataField?.GetValue(mb), data)) { found = mb; break; }
            }
        }
        catch { }
        _dataWgoMap[data] = found;   // we cache null too (data of a bag/save object — not a wgo)
        return found;
    }

    // Throttled full container snapshot (reconciliation of stockpile divergences — they have
    // no GUI close like chests do). Quiesce on the other's ops + 10s/uid.
    private const float CONTAINER_SNAPSHOT_THROTTLE = 10f;
    private static readonly Dictionary<long, float> _lastContainerSnapshotTime = new Dictionary<long, float>();
    // An explicit permit for a full state of an op-driven container (a reconciliation snapshot). Without it
    // OnLocalGraveStateChanged STAYS SILENT for tracked chests/stockpiles — otherwise every put
    // (the AddToInventory hook) would serialize 7KB of json (LAG) and race the partner's fresh ops
    // in flight (a "+1 log" DUPE, stockpile live test #2).
    private static bool _containerSnapshotPass;

    private static void MaybeSendContainerSnapshot(MonoBehaviour wgo, long uid)
    {
        try
        {
            if (_lastRemoteChestOpTime.TryGetValue(uid, out var rt) &&
                Time.realtimeSinceStartup - rt < CHEST_SNAPSHOT_QUIESCE) return;
            if (_lastContainerSnapshotTime.TryGetValue(uid, out var st) &&
                Time.realtimeSinceStartup - st < CONTAINER_SNAPSHOT_THROTTLE) return;
            _lastContainerSnapshotTime[uid] = Time.realtimeSinceStartup;
            TrackChestWgo(wgo);
            _containerSnapshotPass = true;
            try { OnLocalGraveStateChanged(wgo); }
            finally { _containerSnapshotPass = false; }
        }
        catch { _containerSnapshotPass = false; }
    }

    // Hook Item.RemoveItem(Item,int,Item) — the SINGLE funnel for removal from the data of any container
    // (the stockpiles' TakeItemFromWGO lambda 74197, GiveItemToPlayersHands 115847, etc.).
    public static void OnLocalDataItemRemoved(object data, object item, int count, bool result)
    {
        if (!result || ApplyingRemoteChop || !Connected() || data == null || item == null) return;
        if (_moveSnapUid != -1) return;   // inside ChestGUI.MoveItem — the chest diff-ops will cover it
        EnsureReflection();
        try
        {
            var wgo = MapDataToWgo(data);
            if (wgo == null) return;                       // data of a bag/player substructures
            if (IsPlayerActor(wgo)) return;                // we don't sync the player's inventory (by design)
            if (IsStateSyncedStation(wgo)) return;         // workbenches/graves/buildings — driven by 0x0D
            if (_uniqueIdField == null || _itemIdField == null) return;
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            string id = _itemIdField.GetValue(item) as string ?? "";
            if (id.Length == 0) return;
            int val = count;
            if (val <= 0) { try { val = Math.Max(1, Convert.ToInt32(_itemValueField.GetValue(item))); } catch { val = 1; } }
            SendChestOps(uid, new List<ChestOp> { new ChestOp { id = id, delta = -val, json = "" } });
            MaybeSendContainerSnapshot(wgo, uid);
        }
        catch { }
    }

    private static object GetLocalPlayerOverheadItem()
    {
        try
        {
            var lp = GetLocalPlayerWgo();
            if (lp == null || _wgoComponentsProp == null || _componentsCharacterProp == null ||
                _getOverheadItemMethod == null) return null;
            var cm = _wgoComponentsProp.GetValue(lp);
            var ch = cm != null ? _componentsCharacterProp.GetValue(cm) : null;
            return ch != null ? _getOverheadItemMethod.Invoke(ch, null) : null;
        }
        catch { return null; }
    }

    // ── DIAG (temporary) — grave furniture state around a 0x0D apply, to localize grave-sync issues
    // (marble/parts showing in the grave bubble; repair not syncing). Remove once root-caused.

    // Grave furniture (grave_* items in the grave's inventory = what the bubble shows). Call before/after a 0x0D
    // apply to see whether marble was already there, arrived in the json, or got injected by our apply/force.
    public static void LogGraveFurniture(MonoBehaviour wgo, long uid, string when)
    {
        try
        {
            if (wgo == null || _dataField == null || _itemInventoryField == null || _itemIdField == null) return;
            var data = _dataField.GetValue(wgo);
            var inv = data != null ? _itemInventoryField.GetValue(data) as System.Collections.IList : null;
            var ids = new List<string>();
            if (inv != null)
                foreach (var it in inv)
                {
                    if (it == null) continue;
                    string id = _itemIdField.GetValue(it) as string;
                    if (id == null || !id.StartsWith("grave")) continue;
                    float dur = -1f; try { dur = Convert.ToSingle(_itemDurabilityProp.GetValue(it, null)); } catch { }
                    ids.Add($"{id}:{dur:0.00}");   // include durability — repair changes THIS, not the id set
                }
            Multiplayer.Log?.LogInfo($"[DIAG-GRAVE] uid={uid} {when}: furniture=[{string.Join(",", ids.ToArray())}]");
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[DIAG-GRAVE] {e.Message}"); }
    }

    // REPAIR SYNC: a positional 2-bit code of each grave part's VISUAL stage (1..3), ordered by part id. The game
    // renders a part at stage = clamp(4 - ceil(durability*3), 1, 3) (GetNewWOPPrefabsNames, decomp 88903) — so a
    // repair (durability up) or decay (down) that crosses a bucket changes this code, while the part-id set stays
    // the same. We fold it into the grave's `variation`/invH so it flows into BOTH the send- and receive-side
    // signatures (which are otherwise KEY-ONLY and would dedup a repair away). Deterministic and machine-independent
    // (no string hashing): both sides derive the same code from the same parts+durability → echoes still suppress.
    private static int GraveStageCode(MonoBehaviour wgo)
    {
        try
        {
            if (wgo == null || _dataField == null || _itemInventoryField == null
                || _itemIdField == null || _itemDurabilityProp == null) return 0;
            var data = _dataField.GetValue(wgo);
            var inv = data != null ? _itemInventoryField.GetValue(data) as System.Collections.IList : null;
            if (inv == null) return 0;
            var parts = new List<KeyValuePair<string, int>>();
            foreach (var it in inv)
            {
                if (it == null) continue;
                string id = _itemIdField.GetValue(it) as string;
                if (id == null || !id.StartsWith("grave")) continue;
                float dur = 0f; try { dur = Convert.ToSingle(_itemDurabilityProp.GetValue(it, null)); } catch { }
                int stage = Mathf.Clamp(4 - Mathf.CeilToInt(dur * 3f), 1, 3);
                parts.Add(new KeyValuePair<string, int>(id, stage));
            }
            parts.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));   // stable order → same code on both machines
            int code = 0;
            for (int i = 0; i < parts.Count && i < 15; i++) code |= (parts[i].Value & 3) << (2 * i);
            return code;   // 0 = no grave parts (empty grave)
        }
        catch { return 0; }
    }

    // HOME STRETCH — fix for "exit with a corpse in hand" (deferred bug 2026-06-10): when the carrier
    // exits to the menu with an overhead item, the item vanished from the world FOREVER (0x14 removed the
    // partner's copy; the corpse existed only in the carrier's memory). Fix: a forced drop to the ground BEFORE
    // exiting → DropOverheadItem → DropResGameObject.Drop → the existing CorpseDropBroadcastPatch
    // sends 0x11 (the corpse with full JSON appears on the partner), and in the carrier's save the corpse lands on the ground.
    public static void DropOverheadOnExit()
    {
        try
        {
            EnsureReflection();
            if (GetLocalPlayerOverheadItem() == null) return;
            var lp = GetLocalPlayerWgo();
            var cm = lp != null ? _wgoComponentsProp?.GetValue(lp) : null;
            var ch = cm != null ? _componentsCharacterProp?.GetValue(cm) : null;
            if (ch == null || _dropOverheadMethod == null) return;
            _dropOverheadMethod.Invoke(ch, new object[] { false });
            Multiplayer.Log?.LogInfo("[CHOP] Exit with an item in hand → dropped to the ground ✓ (the corpse will sync via 0x11)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] DropOverheadOnExit: {e.Message}"); }
    }

    // PUT of a carried item (from the Postfix of AddToInventory, only __result=true). The gate "this is EXACTLY
    // the player's carried put": in the Postfix SetOverheadItem(null) is NOT called yet (74560 — after) → overhead == item.
    public static void OnLocalContainerPut(MonoBehaviour wgo, object item)
    {
        if (ApplyingRemoteChop || !Connected() || wgo == null || item == null) return;
        EnsureReflection();
        try
        {
            if (IsStateSyncedStation(wgo)) return;   // workbenches/graves/buildings — driven by 0x0D (trigger nearby)
            if (!ReferenceEquals(GetLocalPlayerOverheadItem(), item)) return;
            if (_uniqueIdField == null || _itemIdField == null) return;
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            string id = _itemIdField.GetValue(item) as string ?? "";
            if (id.Length == 0) return;
            int val = 1;
            try { val = Math.Max(1, Convert.ToInt32(_itemValueField.GetValue(item))); } catch { }
            SendChestOps(uid, new List<ChestOp>
                { new ChestOp { id = id, delta = val, json = GetChestStackJson(wgo, id) } });
            MaybeSendContainerSnapshot(wgo, uid);
        }
        catch { }
    }

    // TAKE via GiveItemToPlayersHands: ONLY the state path for 0x0D targets (closes the hole "took
    // a workbench's output into hand"). −Ops for stockpiles are sent by OnLocalDataItemRemoved (the Item.RemoveItem
    // hook — GiveItemToPlayersHands itself calls data.RemoveItem inside, 115847 → no double op).
    public static void OnLocalContainerTake(MonoBehaviour wgo, object item)
    {
        if (ApplyingRemoteChop || !Connected() || wgo == null || item == null) return;
        EnsureReflection();
        try
        {
            if (IsStateSyncedStation(wgo)) OnLocalGraveStateChanged(wgo);
        }
        catch { }
    }

    // GUI close: a full state as reconciliation (idempotent; covers ops lost in far
    // zones) — but we don't overwrite the partner's fresh activity (quiesce).
    public static void OnLocalChestClosed(object chestGui)
    {
        if (ApplyingRemoteChop || !Connected() || chestGui == null) return;
        EnsureReflection();
        try
        {
            var wgo = _chestObjField?.GetValue(chestGui) as MonoBehaviour;
            if (wgo == null || _uniqueIdField == null) return;
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            if (!_knownChestUids.Contains(uid)) return;
            if (_lastRemoteChestOpTime.TryGetValue(uid, out var t) &&
                Time.realtimeSinceStartup - t < CHEST_SNAPSHOT_QUIESCE)
            {
                Multiplayer.Log?.LogInfo($"[CHOP] Chest uid={uid}: close snapshot skipped (the partner just edited it)");
                return;
            }
            _containerSnapshotPass = true;
            try { OnLocalGraveStateChanged(wgo); }
            finally { _containerSnapshotPass = false; }
        }
        catch { }
    }

    // After a 0x0D restore: if WE have exactly this chest open right now — rebuild the GUI panels
    // (otherwise the player looks at a stale view until they reopen it).
    private static void RefreshOpenChestGui(MonoBehaviour wgo)
    {
        if (_chestGuiType == null || _chestObjField == null || _chestFullRedrawMethod == null) return;
        try
        {
            object me = _guiElementsMeProp != null ? _guiElementsMeProp.GetValue(null)
                                                   : _guiElementsMeField?.GetValue(null);
            var gui = me != null ? _guiElementsChestField?.GetValue(me) : null;
            if (gui == null) return;
            if (_chestIsShownProp != null && !Convert.ToBoolean(_chestIsShownProp.GetValue(gui))) return;
            if (!ReferenceEquals(_chestObjField.GetValue(gui), wgo)) return;
            var pars = _chestFullRedrawMethod.GetParameters();
            _chestFullRedrawMethod.Invoke(gui, pars.Length == 1 ? new object[] { -1 } : new object[0]);
            Multiplayer.Log?.LogInfo("[CHOP] Open chest synced — panels refreshed ✓");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] RefreshOpenChestGui: {e.Message}"); }
    }

    // ── WEATHER (0x1A, Phase 5-lite): host-authoritative daily schedule ────────────────────────
    // SmartWeatherEngine.UpdateWeather (37129) rolls 4 day presets (offsets 0/0.15/0.35/0.7) RANDOMLY PER
    // MACHINE → rain diverges. After its roll the host sends the 4 names; the client reproduces the SAME
    // schedule (clear nature line + GetStatesFromPreset + AddNatureWeatherState). Fail-open: no packet → the
    // client keeps its own roll.
    private static readonly float[] WEATHER_DAY_OFFSETS = { 0f, 0.15f, 0.35f, 0.7f };
    private const float WEATHER_REMOVE_DEC = 10f / 450f;   // TimeOfDay.FromSecondsToTimeK(10) = t/450
    private static bool _collectingWeather;
    private static readonly List<string> _weatherCollect = new List<string>();
    private static int _lastWeatherDay = -1;
    private static string[] _lastWeatherNames;
    private static bool _weatherSyncedToPeer;
    private static FieldInfo _mainGameSaveField, _gameSaveDayField;
    private static FieldInfo _mainGameMeField;   // ⚠️ MainGame.me — a FIELD (100838), not a property!

    private static int GetGameDay()
    {
        try
        {
            if (_mainGameMeField == null)
            {
                var mg = AccessTools.TypeByName("MainGame");
                _mainGameMeField   = mg?.GetField("me", BindingFlags.Public | BindingFlags.Static);
                _mainGameSaveField = mg?.GetField("save", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _gameSaveDayField  = AccessTools.TypeByName("GameSave")?.GetField("day",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            var me   = _mainGameMeField?.GetValue(null);
            var save = me != null ? _mainGameSaveField?.GetValue(me) : null;
            return save != null && _gameSaveDayField != null
                ? Convert.ToInt32(_gameSaveDayField.GetValue(save)) : -1;
        }
        catch { return -1; }
    }

    public static void OnWeatherGenStart()
    {
        _collectingWeather = true;
        _weatherCollect.Clear();
    }

    public static void OnWeatherPresetChosen(object preset)
    {
        if (!_collectingWeather) return;
        var uo = preset as UnityEngine.Object;
        if (uo != null) _weatherCollect.Add(uo.name);
    }

    public static void OnWeatherGenEnd()
    {
        // Prefix-skip on the client → the collector didn't start → the Postfix has nothing to record
        // (otherwise a stale collect of the previous roll would be saved with the current day).
        if (!_collectingWeather) return;
        _collectingWeather = false;
        if (_weatherCollect.Count != 4) return;
        // We save REGARDLESS of role: the host generates the daily schedule even BEFORE the lobby is created
        // (Role==None at the moment of world load) — otherwise a late-join would have nothing to be sent.
        // SENDING is gated by TickWeatherSync (host only + Connected).
        _lastWeatherDay = GetGameDay();
        _lastWeatherNames = _weatherCollect.ToArray();
        _weatherSyncedToPeer = false;
        TickWeatherSync();
        if (_lastWeatherDay < 0)
            Multiplayer.Log?.LogWarning("[CHOP] Weather: day not read (-1) — the schedule won't apply correctly!");
    }

    // From SteamManager.Update (host): send the saved schedule as soon as there's someone to send it to (including
    // a late-join — the client joined mid-day). Once per connection/schedule.
    // LATE-JOIN HOLE (live test #3, 2026-06-12): the join-day schedule is often from the host's SAVE
    // (the roll was in a past session) → _lastWeatherNames empty → the join day wasn't synced
    // AT ALL. Fix: harvest the current day's schedule directly from the host's nature line.
    public static void TickWeatherSync()
    {
        if (SteamNetwork.Role != NetworkRole.Host) return;
        if (!Connected()) { _weatherSyncedToPeer = false; return; }
        // The client is still LOADING: a 0x1A sent before it enters the world is OVERWRITTEN
        // by the save load (DeserializeData replaces data; live test #6: apply at
        // game_time=1.0 on the title screen). The marker "client in game" = its clone has spawned
        // (its 0x06 arrived). Until then we keep the sync reset → we'll send it after entry.
        if (!SteamNetwork.RemotePlayerSpawned) { _weatherSyncedToPeer = false; return; }
        int today = GetGameDay();
        // A new day with no roll (a day jump from the client's sleep bypasses OnEndOfDay on both) —
        // a resync is needed: a harvest or the host's own roll below.
        if (today >= 0 && _lastWeatherDay != today) _weatherSyncedToPeer = false;
        if (_weatherSyncedToPeer) return;
        if (_lastWeatherNames == null || _lastWeatherDay != today) TryHarvestTodaySchedule();
        if (_lastWeatherNames == null || _lastWeatherDay != today) return;
        try
        {
            // Format v2 (2026-06-12): [slotIdx(1)+len(1)+name]* — the slots are EXPLICIT, because a harvest
            // mid-day gives only the current+future ones (the game PHYSICALLY removes past ones from
            // the line after they fade — live test #4/#5: "incomplete 1/4 → 2/4" all day).
            // The client doesn't need past slots anyway: it needs the weather FROM NOW ON.
            int payload = 0;
            var entries = new List<KeyValuePair<int, byte[]>>(4);
            for (int i = 0; i < _lastWeatherNames.Length; i++)
            {
                if (string.IsNullOrEmpty(_lastWeatherNames[i])) continue;   // past slot
                var b = System.Text.Encoding.UTF8.GetBytes(_lastWeatherNames[i]);
                if (b.Length > 255) continue;
                entries.Add(new KeyValuePair<int, byte[]>(i, b));
                payload += 2 + b.Length;
            }
            if (entries.Count == 0) return;
            var packet = new byte[6 + payload];
            packet[0] = 0x1A;
            BitConverter.GetBytes(_lastWeatherDay).CopyTo(packet, 1);
            packet[5] = (byte)entries.Count;
            int off = 6;
            foreach (var e in entries)
            {
                packet[off++] = (byte)e.Key;
                packet[off++] = (byte)e.Value.Length;
                Buffer.BlockCopy(e.Value, 0, packet, off, e.Value.Length); off += e.Value.Length;
            }
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
            _weatherSyncedToPeer = true;
            Multiplayer.Log?.LogInfo($"[CHOP] Weather for day {_lastWeatherDay} → " +
                string.Join("/", entries.Select(e => $"{e.Key}:{_lastWeatherNames[e.Key]}").ToArray()) + " (0x1A)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] TickWeatherSync: {e.Message}"); }
    }

    // FIX "the friend doesn't see rain" (live test #8: rain=1.50 in DIAG but a clear sky): the host's save is
    // written IN SLEEP (state=Inside) → _enabled=false IS SERIALIZED (37352-67) and forces value=0 (37327-30).
    // The host heals via the Inside→RealTime transition on going outside (36271-78), but the CLIENT teleports
    // straight OUTSIDE → no transition → disabled forever. Force-enable after entering the world (non-Inside).
    public static void ForceWeatherStatesEnabledOutside()
    {
        try
        {
            var env = EnvironmentEngine.me;
            if (env == null || env.data == null || env.states == null) return;
            if (env.data.state == EnvironmentEngine.State.Inside) return; // indoors that's how it should be
            int n = 0;
            foreach (var st in env.states)
                if (st != null) { st.SetEnabled(true); n++; }
            Multiplayer.Log?.LogInfo($"[CHOP] Weather states force-enabled after entry ({n}) ✓");
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[CHOP] ForceWeatherStatesEnabled: {e.Message}"); }
    }

    // Harvest the CURRENT day's schedule from the host's nature line (late-join: the roll was in a past
    // session and lives only in the save). A slot = states with t_start == day+offset (the Rain/Fog/
    // Wind triad of one preset — we take the first name). All 4 empty → a day with no schedule
    // (nobody's roll) → the host rolls itself, the WeatherGenBracketPatch brackets will collect and send it.
    private static float _lastHarvestTry;
    private static void TryHarvestTodaySchedule()
    {
        if (Time.realtimeSinceStartup - _lastHarvestTry < 5f) return;
        _lastHarvestTry = Time.realtimeSinceStartup;
        try
        {
            var env = EnvironmentEngine.me;
            int day = GetGameDay();
            if (env == null || env.data == null || env.data.nature_weather_line == null || day < 0) return;
            var names = new string[WEATHER_DAY_OFFSETS.Length];
            int filled = 0;
            foreach (var st in env.data.nature_weather_line)
            {
                // We do NOT filter out the removal marker (live test #4: "incomplete 1/4" all day) —
                // the game itself marks the day's past slots as they play out, this is a normal life-cycle
                // thing; t_start stays unchanged on marking → the slot is identified precisely.
                if (st == null || string.IsNullOrEmpty(st.preset_name)) continue;
                for (int i = 0; i < WEATHER_DAY_OFFSETS.Length; i++)
                {
                    if (names[i] == null && Mathf.Abs(st.t_start - (day + WEATHER_DAY_OFFSETS[i])) < 0.005f)
                    {
                        names[i] = st.preset_name;
                        filled++;
                        break;
                    }
                }
            }
            if (filled == 0)
            {
                Multiplayer.Log?.LogInfo($"[CHOP] Weather: day {day} has no schedule — the host rolls itself");
                SmartWeatherEngine.me?.UpdateWeather();
                return;
            }
            // A PARTIAL schedule is NORMAL mid-day (the game physically removed past slots from
            // the line after they faded). We send what we have: current + future (format v2 with slotIdx).
            _lastWeatherDay = day;
            _lastWeatherNames = names;
            _weatherSyncedToPeer = false;
            Multiplayer.Log?.LogInfo($"[CHOP] Weather: day {day} schedule collected from the line ({filled}/4, late-join)");
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[CHOP] TryHarvestTodaySchedule: {e.Message}"); }
    }

    // Client: reproduce the host's schedule (a mirror of the UpdateWeather body, but with GIVEN
    // presets). v2: the slots are EXPLICIT (slots[i] = an index into WEATHER_DAY_OFFSETS) — a late-join
    // sends a partial schedule (current+future slots), past ones aren't replayed.
    public static void ApplyRemoteWeather(int day, List<int> slots, List<string> names)
    {
        if (SteamNetwork.Role == NetworkRole.Host || names == null || names.Count == 0 ||
            slots == null || slots.Count != names.Count) return;
        if (day < 0) { Multiplayer.Log?.LogWarning("[CHOP] 0x1A: day<0 — schedule discarded (safety)"); return; }
        // Not in the world yet (loading) — env will be replaced by DeserializeData, there's nowhere
        // to apply it; the host will resend after our spawn (its RemotePlayerSpawned gate).
        if (!SteamNetwork.IsInGame)
        {
            Multiplayer.Log?.LogWarning("[CHOP] 0x1A before entering the world — discarded (the host will resend after spawn)");
            return;
        }
        EnsureReflection();
        if (_envMeProp == null || _addNatureMethod == null || _tryRemoveNatureMethod == null ||
            _weatherPresetGetMethod == null || _getStatesFromPresetMethod == null || _findNatureMethod == null) return;
        try
        {
            var env = _envMeProp.GetValue(null);
            if (env == null) { Multiplayer.Log?.LogWarning("[CHOP] 0x1A: EnvironmentEngine not ready yet"); return; }
            for (int i = 0; i < names.Count; i++)
            {
                if (slots[i] < 0 || slots[i] >= WEATHER_DAY_OFFSETS.Length) continue;
                float t = day + WEATHER_DAY_OFFSETS[slots[i]];
                var existing = _findNatureMethod.Invoke(env, null) as System.Collections.IEnumerable;
                if (existing != null)
                {
                    var toRemove = new List<string>();
                    foreach (var pn in existing) if (pn is string s) toRemove.Add(s);
                    foreach (var s in toRemove)
                        _tryRemoveNatureMethod.Invoke(env, new object[] { s, t, WEATHER_REMOVE_DEC });
                }
                var preset = _weatherPresetGetMethod.Invoke(null, new object[] { names[i] });
                if (preset == null) continue;
                var states = _getStatesFromPresetMethod.Invoke(null, new object[] { t, preset }) as System.Collections.IEnumerable;
                if (states == null) continue;
                foreach (var st in states) _addNatureMethod.Invoke(env, new object[] { st });
            }
            Multiplayer.Log?.LogInfo($"[CHOP] Weather from the host: day {day} → " +
                string.Join("/", names.Select((n, i) => $"{slots[i]}:{n}").ToArray()) + " ✓");
            // DIAGNOSTIC for late-apply (rain was only on the host, 2026-06-11): game_time at the moment
            // of application + the nature line AFTER — to see which state is actually active.
            try
            {
                float gt = -1f;
                var gtProp = AccessTools.TypeByName("MainGame")?.GetProperty("game_time",
                    BindingFlags.Public | BindingFlags.Static);
                var gtField = AccessTools.TypeByName("MainGame")?.GetField("game_time",
                    BindingFlags.Public | BindingFlags.Static);
                var gtv = gtProp != null ? gtProp.GetValue(null) : gtField?.GetValue(null);
                if (gtv != null) gt = Convert.ToSingle(gtv);
                var lineInfo = "?";
                var dataField = env.GetType().GetField("data",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var envData = dataField?.GetValue(env);
                var natLine = envData?.GetType().GetField("nature_weather_line",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(envData) as System.Collections.IEnumerable;
                if (natLine != null)
                {
                    var parts = new List<string>();
                    foreach (var st in natLine)
                    {
                        var t = st.GetType();
                        var pn = t.GetField("preset_name")?.GetValue(st) as string ?? "?";
                        var ts = t.GetField("t_start", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(st);
                        var tp = t.GetField("type", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(st);
                        parts.Add($"{pn}/{tp}@{ts}");
                    }
                    lineInfo = parts.Count > 0 ? string.Join("; ", parts.ToArray()) : "EMPTY";
                }
                Multiplayer.Log?.LogInfo($"[CHOP] 0x1A DIAG: game_time={gt:F3}, nature line: {lineInfo}");
            }
            catch (Exception de) { Multiplayer.Log?.LogWarning($"[CHOP] 0x1A DIAG failed: {de.Message}"); }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] ApplyRemoteWeather: {e.Message}"); }
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

    // For patches outside ChopSync (the gate for muting the weather roll on the client).
    public static bool IsPeerConnected() => Connected();

    // type(1) + x(4) + y(4) + amount(4) = 13 bytes (amount is not used for 0x0A)
    private static void SendTreePacket(byte type, float x, float y, float amount)
    {
        var packet = new byte[13];
        packet[0] = type;
        BitConverter.GetBytes(x).CopyTo(packet, 1);
        BitConverter.GetBytes(y).CopyTo(packet, 5);
        BitConverter.GetBytes(amount).CopyTo(packet, 9);
        SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
    }

    // ── Send: local player hit a tree/stone (from DoActionSyncPatch) ─
    public static void OnLocalChop(MonoBehaviour wgo, MonoBehaviour actor, float amount)
    {
        if (ApplyingRemoteChop || !Connected()) return;
        if (!IsDestroySyncTarget(wgo) || !IsPlayerActor(actor)) return;
        var p = wgo.transform.position;
        SendTreePacket(0x09, p.x, p.y, amount);
    }

    // ── Send: object finished its work (DropItems) — we broadcast the state change ──
    // IsTransformSyncTarget: trees, stones, graves, beds — everything that transitions
    // into after_hp_0 (stump / grave_empty / garden_empty). Veins (steep_*) don't go here
    // — they don't disappear, we sync only their loot via 0x0B.
    public static void OnTreeFelled(MonoBehaviour wgo)
    {
        if (ApplyingRemoteChop || !Connected()) return;
        if (!IsTransformSyncTarget(wgo)) return;
        var p = wgo.transform.position;
        SendTreePacket(0x0A, p.x, p.y, 0f);
        Multiplayer.Log?.LogInfo($"[CHOP] Object worked @({p.x:F1},{p.y:F1}) — broadcasting the state change");
    }

    // ═══ B-2 (approach A): FULL GRAVE STATE REPLICATION (0x0D) ════════════════════
    // Test #2 proved replaying only the visual (RedrawPart) fails — the receiver's _data is unchanged, so the
    // per-frame UpdateTransparentParts rolls it back. Solution: serialize the WHOLE WGO (FromWGO → SerializableWGO)
    // and RestoreFromSerializedObject on the receiver → state (item/_data) identical → the per-frame redraw now
    // draws correctly. Loot stays on 0x0B. Trigger = RedrawPart/ReplaceWithObject; idempotent (re-restore is harmless).
    // 0x0D format: type(1) uid(8) x(4) y(4) blobLen(4) blob(BinaryFormatter SerializableWGO)
    private static bool GraveStateReflectionReady() =>
        _fromWgoMethod != null && _restoreFromSerializedMethod != null && _uniqueIdField != null
        && _swObjIdField != null && _swItemField != null
        && _dataField != null && _toJsonMethod != null;

    // OWNER RECON (2026-06-08, REJECTED): the WGO worker fields were ALWAYS empty at the trigger and the
    // player uid resolved to 0 → identity is no good as an ownership signal. Dupes cured by echo-suppression
    // via the stage signature (below) instead.

    // Whether there's a body in the grave (GetBodyFromInventory, not json). Added to the stage signature as a
    // BIT so putting/taking a body changes it (key-only missed it — the body isn't in _res_type). Sender reads
    // the live wgo; receiver reads the packet bit (it hasn't applied the state at dedup time).
    private static bool GraveHasBody(MonoBehaviour wgo)
    {
        if (_getBodyFromInventoryMethod == null || wgo == null) return false;
        try { return _getBodyFromInventoryMethod.Invoke(wgo, new object[] { true }) != null; }
        catch { return false; }
    }

    // === GRAVE STAGE SIGNATURE (2026-06-08) ===
    // Raw json has per-frame noise (one stage → 14 states sent = flicker). The real stage = obj_id + the set
    // of part keys in _params (_res_type). Dedup+echo-check by this signature: 1 send + 1 apply per REAL stage.
    // Fallback to full json if _params isn't found.
    private static string GraveStageSig(string objId, string json)
    {
        string rt = ExtractJsonArray(json, "_res_type");
        if (rt == null) return (objId ?? "") + "|" + json;  // fallback: _params not found
        // SET OF KEYS of grave parts, WITHOUT values — deliberately key-only (2026-06-10). Keys distinguish
        // stages and furniture (frame vs cross = different keys → 2nd item syncs). We skip VALUES (0/1) because
        // the disassembly transient "item in inventory + value<1" draws a WOODEN frame (OnDrawGrave 88883) → with
        // values the viewer would flash a wooden frame during disassembly (test #5 bug). Stale full-grave echo
        // (same keys) is cured by ECHO_SUPPRESS_WINDOW, not values.
        var parts = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(rt, "\"(grave[^\"]*)\""))
            parts.Add(m.Groups[1].Value);
        parts.Sort();
        return (objId ?? "") + "|" + string.Join(",", parts);
    }


    // Extracts "[...]" right after the key (first occurrence). _res_type/_res_v are flat
    // arrays (strings/ints), with no nested brackets, so the first ']' closes it. null if absent.
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

    // Forces the grave's furniture items (frame/cross, id="grave_*") in the INVENTORY to param≥1, so the game
    // draws a finished stone, not a wooden "under construction" frame (grave_*_building_1,
    // OnDrawGrave/GetNewWOPPrefabsNames 88883 draws it when GetParam(item.id)<1). Only on the VIEWER
    // after a restore: they aren't building, so they shouldn't show the frame. We skip the body (id="body");
    // dug-out parts are NO LONGER in the inventory → we don't touch them (they vanish correctly, digging stays intact).
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
                if (id == null || !id.StartsWith("grave")) continue;  // furniture = grave_*; the "body" — no
                float p = Convert.ToSingle(_getParamMethod.Invoke(wgo, new object[] { id, 0f }));
                if (p < 1f) _setParamMethod.Invoke(wgo, new object[] { id, 1f });
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[CHOP] furniture-complete failed: {e.Message}"); }
    }

    // === GRAVE STAGE MONOTONICITY (2026-06-08, dupe fix) ===
    // Digging is IRREVERSIBLE and moves FORWARD (grave_ground → parts vanish → grave_exhume → grave_empty).
    // So an incoming state that ADDS a part back or lowers obj_id is stale/echo; applying it rehydrates an
    // active grave's _data → re-drops every frame (live test 2026-06-08: 66 re-drops = pyramid). We reject
    // such "backward" states on APPLY.
    private static int GraveObjRank(string objId)
    {
        if (string.IsNullOrEmpty(objId)) return 0;
        if (objId.StartsWith("grave_empty")) return 2;
        if (objId.StartsWith("grave_exhume")) return 1;
        return 0; // grave_ground and other early sub-stages
    }

    // The set of grave part keys (grave_*) from the raw json state (via _res_type).
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

    // A grave stage-change trigger → we send the STATE as JSON. Transport = Item.ToJSON(0)
    // (RECON 2026-06-07: depth 0 = full _data state — the parts' _params + inventory;
    // shrinks across stages 13587→...→732). The reverse on the receiver — JsonUtility.
    // FromJsonOverwrite into a local Item (types/formulas stay valid).
    // 0x0D format: type(1) uid(8) x(4) y(4) variation(4) objIdLen(2) objId jsonLen(4) json
    public static void OnLocalGraveStateChanged(MonoBehaviour wgo)
    {
        if (ApplyingRemoteChop || !Connected()) return;
        if (!IsStateRepTarget(wgo)) return;   // a grave OR a synced building (Phase 2)
        // OP-DRIVEN CONTAINERS (tracked chests/stockpiles): a full state — ONLY an explicit
        // reconciliation (_containerSnapshotPass: a GUI-close snapshot / a throttled
        // MaybeSendContainerSnapshot). Live changes are driven by 0x19 OPS. Otherwise every put serializes
        // 7KB of json (LAG on spam) and in-flight states race the partner's ops (DUPE).
        if (!_containerSnapshotPass && IsTrackedChest(wgo) && !IsStateSyncedStation(wgo)) return;
        // PROXIMITY GATE for workbenches (bat_test flood fix, live test 2026-06-11): many has_craft objects
        // (dev-test bat_tests scattered over the map) all fire RedrawPart on load → a 0x0D burst the receiver
        // can't apply (object not in its zone). So we replicate a workbench's state only when the local player
        // is NEARBY. Graves/buildings are exempt (worked up close, not scattered).
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

            // The STAGE signature (not raw json — it has per-frame noise, live test 2026-06-08).
            // + BODY BIT (fix 2026-06-10): the body sits in the inventory, NOT in _res_type, so a key-only
            // signature didn't change when a body was put into grave_empty → echo/dedup swallowed the 0x0D → the body didn't sync
            // to the partner and was pushed back. The bit is carried in the packet (the variation field, bit 0).
            bool hasBody = GraveHasBody(wgo);
            // + WORKBENCH CONTENT HASH (Phase 2 craft): the output/materials sit in the inventory, not in obj_id,
            // so a key-only signature doesn't change during a craft → dedup would mute it. The hash is in the packet (variation bits 1+).
            // GRAVES: fold in the part VISUAL-STAGE code (GraveStageCode) — repair/decay changes a part's rendered
            // stage without changing the key set, and a key-only sig would dedup it away on BOTH sides (repair
            // wasn't syncing, 2026-07-01). It rides invH exactly like the workbench hash → same wire, same receiver
            // sig. Safe from the old dupe loop: it tracks durability STAGE, not inventory counts a restore reorders.
            int invH = IsGraveTarget(wgo)                       ? GraveStageCode(wgo)
                     : (IsWorkbench(wgo) || IsTrackedChest(wgo)) ? WorkbenchInvHash(wgo)
                     : 0;                                       // Phase 3: chests are inv-sensitive too
            string sig = GraveStageSig(objId, json) + (hasBody ? "|B" : "") + (invH != 0 ? "|w" + invH : "");

            // ECHO-SUPPRESSION (dupe fix): if this is EXACTLY the stage we just received
            // and applied for this uid — it's our own echo (we're the viewer), NOT our own change.
            // We don't send it back → the digger↔viewer loop breaks at the root. The loot dupe (24 slabs in
            // a frantic test) was a CONSEQUENCE of this loop: the digger applied the echoed state
            // to its own active grave → re-dropped the slab. The echo-guard is powerless here (the echo =
            // a separate network packet, not a re-entry in the same frame). Live test 2026-06-08:
            // the viewer echoed 1 state, the digger applied 1 — a latent loop, cascading in a mess.
            if (_lastAppliedGraveSig.TryGetValue(uid, out var appliedSig) && appliedSig == sig
                && _lastAppliedGraveSigTime.TryGetValue(uid, out var appliedAt)
                && UnityEngine.Time.realtimeSinceStartup - appliedAt < ECHO_SUPPRESS_WINDOW)
                return;  // echo-suppression: this is our RECENT echo (we applied this state as a viewer just now).
                         // The ECHO_SUPPRESS_WINDOW: a stale applied signature (minutes ago) does NOT mute
                         // an honest change with the same signature (grave-rebuild-by-2nd-item fix 2026-06-10).

            // Only a GENUINE local change reaches here → I'm actively digging. Update the owner window EVERY
            // time (before the dedup below — at a stable stage the dedup returns early and the mark would go stale).
            _lastLocalGraveDigTime[uid] = UnityEngine.Time.realtimeSinceStartup;

            // Stage DEDUP: several triggers (RedrawPart/OnWorkFinished/OnCraftStateChanged) catch intermediate
            // stages with a micro json diff. Send only when the REAL stage changed → 1 send/stage (was ~14 = flicker).
            // A repair now changes `sig` via the stage code in invH → it passes this dedup instead of being muted.
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
            BitConverter.GetBytes((hasBody ? 1 : 0) | (invH << 1)).CopyTo(packet, 17);  // variation: bit0 = body, bits 1-30 = workbench content hash
            int off = 21;
            BitConverter.GetBytes((ushort)idB.Length).CopyTo(packet, off); off += 2;
            Buffer.BlockCopy(idB, 0, packet, off, idB.Length); off += idB.Length;
            BitConverter.GetBytes(dataB.Length).CopyTo(packet, off); off += 4;
            Buffer.BlockCopy(dataB, 0, packet, off, dataB.Length);
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
            Multiplayer.Log?.LogInfo($"[CHOP] Grave uid={uid} obj={objId} — STATE sync (json={dataB.Length}b)");

            // Co-op craft Stage 1: for a workbench we piggyback the queue (start/finish change craft_queue,
            // and these triggers aren't EnqueueCraft). SendCraftQueue dedups itself — it doesn't send extras.
            if (IsWorkbench(wgo)) SendCraftQueue(wgo);
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] OnLocalGraveStateChanged: {e.Message}"); }
    }

    // ── Receive 0x09: replay the hit (cosmetic — the shake) ──────────────────
    public static void ApplyRemoteChop(float x, float y, float amount)
    {
        EnsureReflection();
        if (!_ready) return;

        var target = FindTargetNear(x, y, out float dist);
        if (target == null || dist > POSITION_EPSILON)
        {
            Multiplayer.Log?.LogWarning($"[CHOP] Object @({x:F1},{y:F1}) not found " +
                $"(nearest: {(target != null ? dist.ToString("F1") : "none")})");
            return;
        }

        var localPlayer = GetLocalPlayerWgo();
        if (localPlayer == null) { Multiplayer.Log?.LogWarning("[CHOP] Player WGO not found"); return; }

        ApplyingRemoteChop = true;
        try { _doActionMethod.Invoke(target, new object[] { localPlayer, amount }); }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] DoAction failed: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
    }

    // Find a WGO by unique_id (more reliable than position — no epsilon misses). Scan cache.
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

    // ── NPC-RECON (2026-06-13, start of the cosmetic villager-position sync) ──────────
    // Zonda: NPCs in different places on the two machines look odd → he wants a POSITION sync
    // (cosmetic), WITHOUT an AI stop that breaks personal quests. RECON: an NPC = a WGO with
    // obj_def.IsNPC() (ObjType.NPC, decompiled 78583), obj_id like "npc_blacksmith"/
    // "npc_redneck_2"; movement via components.character (MovementComponent) + GraphOwner.
    // KEY DESIGN QUESTION: is an NPC's unique_id stable on BOTH machines? If yes —
    // mirror by uid (like graves/buildings); if no — by obj_id (unique named ones) + position match.
    // TEST: F2 on BOTH machines near the SAME NPC → compare uid (matches?) and pos (different?).
    public static void DumpNpcs()
    {
        try
        {
            EnsureReflection();
            if (!_ready || _objIdField == null) { Multiplayer.Log?.LogWarning("[NPC-RECON] reflection not ready"); return; }
            int n = 0;
            foreach (var comp in ScanWgosCached())
            {
                var mb = comp as MonoBehaviour;
                if (mb == null) continue;
                string id = _objIdField.GetValue(mb) as string ?? "";
                if (!id.StartsWith("npc_")) continue;
                n++;
                long uid = 0;
                try { uid = Convert.ToInt64(_uniqueIdField.GetValue(mb)); } catch { }
                var p = mb.transform.position;
                Multiplayer.Log?.LogInfo($"[NPC-RECON] id={id} uid={uid} pos=({p.x:F1},{p.y:F1}) active={mb.gameObject.activeInHierarchy}");
            }
            Multiplayer.Log?.LogInfo($"[NPC-RECON] total NPCs in the scene: {n} (press F2 on BOTH machines near the same NPC → compare uid and pos)");
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[NPC-RECON] {e.Message}"); }
    }

    // ── NPC POSITION SYNC (0x20, 2026-06-13) — a cosmetic villager mirror ───────────
    // Host-authoritative: host sends a batch {uid,x,y} (10/s); the client finds the NPC by uid (stable per F2
    // recon), ONCE stops the route-AI (GraphOwner.StopBehaviour, like PreDisable 114815) and sets the position.
    // Dialogue untouched. Visible only when nearby. After parting (2s no update) we restore the AI (StartBehaviour),
    // else the NPC freezes on the client.
    private static float _npcSyncTimer;
    private const float NPC_SYNC_INTERVAL = 0.1f;
    private const float NPC_REENABLE_TIMEOUT = 2f;
    private static readonly HashSet<long> _npcAiStopped = new HashSet<long>();
    private static readonly Dictionary<long, float> _npcLastSeen = new Dictionary<long, float>();
    private static readonly Dictionary<long, MonoBehaviour> _npcStoppedGo = new Dictionary<long, MonoBehaviour>();
    private static bool _graphOwnerResolved;
    private static System.Type _graphOwnerType;
    private static MethodInfo _stopBehaviourMethod, _startBehaviourMethod;
    private static object[] _stopArgs, _startArgs;
    // Bug 1 fix: StopBehaviour stops only the behaviour tree, while MovementComponent (components.character)
    // runs on the WGO's own loop and overrides our position every frame (86132/86667/86695). We kill it via
    // wgo.components.character.StopMovement() (like the game at 80586/85178/99687).
    private static MethodInfo _stopMovementMethod;

    // Stardew pivot: NPCs are PERSONAL — each machine runs its own AI/schedule. Position sync (0x20) is
    // disabled: it would StopBehaviour on all NPC GraphOwners (incl. story flow-graphs) and clash with personal
    // quests; the game does that only for ObjType.Mob (114811). Cost: NPC positions differ visually (accepted).
    // Flag kept for rollback (static readonly, not const → no CS0162).
    private static readonly bool SYNC_NPC_POSITIONS = false;

    public static void TickNpc()
    {
        if (!SYNC_NPC_POSITIONS) return;   // Stardew: NPCs are personal — neither send nor apply positions
        try
        {
            if (!IsPeerConnected() || !SteamNetwork.IsInGame) return;
            if (SteamNetwork.Role == NetworkRole.Host) TickNpcHostSend();
            else TickNpcClientReenable();
        }
        catch { }
    }

    private static void TickNpcHostSend()
    {
        _npcSyncTimer += Time.deltaTime;
        if (_npcSyncTimer < NPC_SYNC_INTERVAL) return;
        _npcSyncTimer = 0f;
        EnsureReflection();
        if (!_ready || _objIdField == null) return;

        var ids = new List<long>(); var xs = new List<float>(); var ys = new List<float>();
        foreach (var comp in ScanWgosCached())
        {
            var mb = comp as MonoBehaviour;
            if (mb == null) continue;
            string id = _objIdField.GetValue(mb) as string ?? "";
            if (!id.StartsWith("npc_")) continue;
            long uid; try { uid = Convert.ToInt64(_uniqueIdField.GetValue(mb)); } catch { continue; }
            var p = mb.transform.position;
            ids.Add(uid); xs.Add(p.x); ys.Add(p.y);
        }
        if (ids.Count == 0) return;
        int cnt = Math.Min(ids.Count, 500);
        var pk = new byte[3 + cnt * 16];
        pk[0] = 0x20;
        pk[1] = (byte)(cnt & 0xFF);
        pk[2] = (byte)((cnt >> 8) & 0xFF);
        int o = 3;
        for (int i = 0; i < cnt; i++)
        {
            BitConverter.GetBytes(ids[i]).CopyTo(pk, o); o += 8;
            BitConverter.GetBytes(xs[i]).CopyTo(pk, o); o += 4;
            BitConverter.GetBytes(ys[i]).CopyTo(pk, o); o += 4;
        }
        SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, pk);
    }

    public static void ApplyRemoteNpcPositions(byte[] data)
    {
        if (!SYNC_NPC_POSITIONS) return;   // Stardew: NPCs are personal — ignore incoming positions
        try
        {
            if (SteamNetwork.Role == NetworkRole.Host) return;   // authority — only the client applies
            if (data.Length < 3) return;
            int cnt = data[1] | (data[2] << 8);
            if (data.Length < 3 + cnt * 16) return;
            EnsureReflection();
            if (!_ready) return;
            ResolveGraphOwner();
            int o = 3;
            for (int i = 0; i < cnt; i++)
            {
                long uid = BitConverter.ToInt64(data, o); o += 8;
                float x = BitConverter.ToSingle(data, o); o += 4;
                float y = BitConverter.ToSingle(data, o); o += 4;
                var mb = FindWgoByUniqueId(uid);
                if (mb == null) continue;            // not loaded on the client — skip
                _npcLastSeen[uid] = Time.time;
                if (!_npcAiStopped.Contains(uid))
                {
                    StopNpcRouteAI(mb);
                    _npcAiStopped.Add(uid);
                    _npcStoppedGo[uid] = mb;
                }
                var p = mb.transform.position;
                mb.transform.position = new Vector3(x, y, p.z);
            }
        }
        catch { }
    }

    private static void TickNpcClientReenable()
    {
        if (_npcAiStopped.Count == 0) return;
        _npcSyncTimer += Time.deltaTime;
        if (_npcSyncTimer < 0.5f) return;
        _npcSyncTimer = 0f;
        List<long> revive = null;
        foreach (var uid in _npcAiStopped)
        {
            float seen; if (!_npcLastSeen.TryGetValue(uid, out seen)) seen = 0f;
            if (Time.time - seen > NPC_REENABLE_TIMEOUT)
                (revive ?? (revive = new List<long>())).Add(uid);
        }
        if (revive == null) return;
        foreach (var uid in revive)
        {
            MonoBehaviour mb; _npcStoppedGo.TryGetValue(uid, out mb);
            if (mb != null) StartNpcRouteAI(mb);
            _npcAiStopped.Remove(uid); _npcLastSeen.Remove(uid); _npcStoppedGo.Remove(uid);
        }
    }

    public static void ResetNpcSync()
    {
        // Bug 2: restore route-AI on every frozen NPC BEFORE clearing, else they freeze forever
        // (DespawnRemotePlayer also fires on a transient packet-timeout mid-session, not just a real disconnect).
        foreach (var kv in _npcStoppedGo)
            if (kv.Value != null) StartNpcRouteAI(kv.Value);
        _npcAiStopped.Clear(); _npcLastSeen.Clear(); _npcStoppedGo.Clear(); _npcSyncTimer = 0f;
    }

    private static object[] BuildDefaultArgs(MethodInfo m)
    {
        var ps = m.GetParameters();
        if (ps.Length == 0) return null;
        var a = new object[ps.Length];
        for (int i = 0; i < ps.Length; i++)
            a[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                 : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
        return a;
    }

    private static void ResolveGraphOwner()
    {
        if (_graphOwnerResolved) return;
        _graphOwnerResolved = true;
        try
        {
            _graphOwnerType = AccessTools.TypeByName("NodeCanvas.Framework.GraphOwner")
                           ?? AccessTools.TypeByName("GraphOwner");
            if (_graphOwnerType != null)
                foreach (var m in _graphOwnerType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "StopBehaviour" && _stopBehaviourMethod == null) { _stopBehaviourMethod = m; _stopArgs = BuildDefaultArgs(m); }
                    if (m.Name == "StartBehaviour" && _startBehaviourMethod == null && m.GetParameters().Length == 0) { _startBehaviourMethod = m; _startArgs = null; }
                }
            // Bug 1: StopMovement() on BaseCharacterComponent (inherited from MovementComponent).
            var chType = _componentsCharacterProp?.PropertyType;
            _stopMovementMethod = chType?.GetMethod("StopMovement",
                BindingFlags.Public | BindingFlags.Instance, null, System.Type.EmptyTypes, null);
            Multiplayer.Log?.LogInfo($"[NPC] GraphOwner resolve: type={(_graphOwnerType != null)} stop={(_stopBehaviourMethod != null)} start={(_startBehaviourMethod != null)} stopMove={(_stopMovementMethod != null)}");
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[NPC] GraphOwner: {e.Message}"); }
    }

    private static void StopNpcRouteAI(MonoBehaviour npc)
    {
        try
        {
            if (_graphOwnerType == null || _stopBehaviourMethod == null) return;
            var owners = npc.GetComponentsInChildren(_graphOwnerType);
            if (owners == null) return;
            foreach (var ow in owners) try { _stopBehaviourMethod.Invoke(ow, _stopArgs); } catch { }
        }
        catch { }
        // Bug 1: the tree is stopped, but MovementComponent still drags the NPC along the rest of the
        // A* path — clear state+path, else it overrides our position (jitter).
        StopNpcMovement(npc);
    }

    // wgo.components.character.StopMovement() via reflection (fields already resolved in
    // EnsureReflection for stockpiles; the method — in ResolveGraphOwner).
    private static void StopNpcMovement(MonoBehaviour npc)
    {
        try
        {
            if (_wgoComponentsProp == null || _componentsCharacterProp == null || _stopMovementMethod == null) return;
            var cm = _wgoComponentsProp.GetValue(npc);
            var ch = cm != null ? _componentsCharacterProp.GetValue(cm) : null;
            if (ch != null) _stopMovementMethod.Invoke(ch, null);
        }
        catch { }
    }

    private static void StartNpcRouteAI(MonoBehaviour npc)
    {
        try
        {
            if (_graphOwnerType == null || _startBehaviourMethod == null) return;
            var owners = npc.GetComponentsInChildren(_graphOwnerType);
            if (owners == null) return;
            foreach (var ow in owners) try { _startBehaviourMethod.Invoke(ow, _startArgs); } catch { }
        }
        catch { }
    }

    // ── GARDEN/ORCHARD SYNC (0x21, 2026-06-15) ─────────────────────────────────────
    // A bed = a WGO with obj_id state (garden_X) + a "growing" param (growth stage; child GOs switched by
    // GardenCustomDrawer on "growing", decompiled 89098). 0x0A syncs only HARVEST (garden_X→after_hp_0);
    // PLANTING (empty→garden_X) and GROWTH (growing, per-machine drift like weather) were NOT synced.
    // Uses the SAME RestoreFromSerializedObject primitive as the grave 0x0D but on a separate packet without
    // grave guards (avoids the echo-loop dupe). POLICY: planting/harvest (obj_id change) is TWO-WAY (the actor
    // sends); GROWTH is HOST-AUTHORITATIVE (TickGardens reconcile) so the client doesn't fight the host on drift.
    public static bool ApplyingRemoteGarden;
    private static float _gardenSyncTimer;
    private const float GARDEN_SYNC_INTERVAL = 1.5f;
    private static readonly Dictionary<long, string> _lastSentGardenJson = new Dictionary<long, string>();
    private static readonly Dictionary<long, string> _lastGardenObjId = new Dictionary<long, string>();

    // Anything "garden-related" (WIDE check): a real bed OR its build object. Used ONLY to exclude the whole
    // garden from other paths (0x0D state-rep, craft). The garden sync itself uses the narrow IsGardenTarget below.
    private static bool IsGardenRelated(MonoBehaviour wgo)
    {
        if (_objIdField == null) return false;
        string id = _objIdField.GetValue(wgo) as string ?? "";
        if (id.StartsWith("zombie")) return false;   // zombie_garden_desk_* = zombie auto-production (3c), not here
        return id.StartsWith("garden") || id.Contains("_garden");   // garden_*, tree_apple_garden, bush_berry_garden
    }

    // 0x21 sync target = ONLY a plantable bed. Excludes build objects: the ghost frame (obj_id+"_place",
    // 44018/76244) and build sites (*builddesk) go via the CHOP path (0x15 + 0x0A), NOT garden reconcile.
    // Without this a late 0x21 "un-dug" a fresh bed back into a frame → stuck building (live test 2026-06-17).
    private static bool IsGardenTarget(MonoBehaviour wgo)
    {
        if (!IsGardenRelated(wgo)) return false;
        string id = _objIdField.GetValue(wgo) as string ?? "";
        return !id.EndsWith("_place") && !id.Contains("builddesk");
    }

    // Bed-change trigger (from GraveReplaceSyncPatch/GraveRedrawSyncPatch). The host sends ANY change
    // (json-dedup), the client — ONLY an obj_id change (plant/harvest), not growth (host authority).
    public static void OnLocalGardenStateChanged(MonoBehaviour wgo)
    {
        if (ApplyingRemoteChop || ApplyingRemoteGarden || !Connected()) return;
        if (_objIdField == null || !IsGardenTarget(wgo)) return;
        try
        {
            if (SteamNetwork.Role != NetworkRole.Host)
            {
                long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
                string objId = _objIdField.GetValue(wgo) as string ?? "";
                if (_lastGardenObjId.TryGetValue(uid, out var prevId) && prevId == objId)
                    return;   // client: same obj_id = growth only → let the host drive it
            }
        }
        catch { return; }
        TrySendGarden(wgo);
    }

    private static void TrySendGarden(MonoBehaviour wgo)
    {
        if (!GraveStateReflectionReady()) return;
        try
        {
            var data = _dataField.GetValue(wgo);
            if (data == null) return;
            string json = _toJsonMethod.Invoke(data, new object[] { 0 }) as string ?? "";
            if (json.Length == 0) return;
            string objId = _objIdField.GetValue(wgo) as string ?? "";
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            if (uid == 0) return;
            if (_lastSentGardenJson.TryGetValue(uid, out var prev) && prev == json) return;   // state unchanged
            _lastSentGardenJson[uid] = json;
            _lastGardenObjId[uid] = objId;

            var idB   = System.Text.Encoding.UTF8.GetBytes(objId);
            var dataB = System.Text.Encoding.UTF8.GetBytes(json);
            if (idB.Length > 65535) return;
            var p = wgo.transform.position;
            // 0x21 | uid(8) | x(4) | y(4) | objIdLen(2) | objId | json(rest)
            var pk = new byte[19 + idB.Length + dataB.Length];
            pk[0] = 0x21;
            BitConverter.GetBytes(uid).CopyTo(pk, 1);
            BitConverter.GetBytes(p.x).CopyTo(pk, 9);
            BitConverter.GetBytes(p.y).CopyTo(pk, 13);
            BitConverter.GetBytes((ushort)idB.Length).CopyTo(pk, 17);
            int off = 19;
            Buffer.BlockCopy(idB, 0, pk, off, idB.Length); off += idB.Length;
            Buffer.BlockCopy(dataB, 0, pk, off, dataB.Length);
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, pk);
            Multiplayer.Log?.LogInfo($"[GARDEN] bed uid={uid} obj={objId} — state sync (json={dataB.Length}b, growing={ReadGrowing(wgo):F3})");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[GARDEN] send: {e.Message}"); }
    }

    // Returns true if the target bed was FOUND (state applied best-effort, remove from queue);
    // false — fail-open (object not loaded yet → into the retry queue). fromRetry prevents re-queuing
    // during the retry itself. DIAG: we log growing (Stage 2 — to measure growth determinism).
    public static bool ApplyRemoteGardenState(byte[] data, bool fromRetry = false)
    {
        try
        {
            if (data.Length < 19) return true;   // corrupt data — drop from queue
            long uid  = BitConverter.ToInt64(data, 1);
            float x   = BitConverter.ToSingle(data, 9);
            float y   = BitConverter.ToSingle(data, 13);
            int idLen = BitConverter.ToUInt16(data, 17);
            if (19 + idLen > data.Length) return true;
            string objId = System.Text.Encoding.UTF8.GetString(data, 19, idLen);
            int jsonOff  = 19 + idLen;
            string json  = System.Text.Encoding.UTF8.GetString(data, jsonOff, data.Length - jsonOff);
            EnsureReflection();
            if (!GraveStateReflectionReady()) return false;   // reflection not ready — try again

            var target = FindWgoByUniqueId(uid);
            if (target == null)
            {
                target = FindTargetNear(x, y, out float dist, IsGardenTarget);
                if (target != null && dist > POSITION_EPSILON) target = null;
            }
            if (target == null)
            {
                // 0x15/0x21 build race OR out-of-zone bed → queue it, retry from the timer.
                if (!fromRetry) EnqueuePendingGarden(uid, data);
                Multiplayer.Log?.LogWarning($"[GARDEN] 0x21 uid={uid} obj={objId} not found → {(fromRetry ? "still waiting" : "queued for retry")}");
                return false;
            }

            ApplyingRemoteGarden = true;
            try
            {
                var sw = _fromWgoMethod.Invoke(null, new object[] { target });
                if (sw != null && _fromJsonOverwriteMethod != null)
                {
                    var item = _swItemField.GetValue(sw);
                    if (item != null) _fromJsonOverwriteMethod.Invoke(null, new object[] { json, item });
                    else
                    {
                        var d = _dataField.GetValue(target);
                        if (d != null)
                        {
                            _fromJsonOverwriteMethod.Invoke(null, new object[] { json, d });
                            _swItemField.SetValue(sw, d);
                        }
                    }
                    if (!string.IsNullOrEmpty(objId)) _swObjIdField.SetValue(sw, objId);
                    _restoreFromSerializedMethod.Invoke(target, new object[] { sw, false });
                    if (_redrawMethod != null)
                        try { _redrawMethod.Invoke(target, new object[] { true, false, false }); } catch { }
                    _lastSentGardenJson[uid] = json;   // don't echo back
                    _lastGardenObjId[uid] = objId;
                    Multiplayer.Log?.LogInfo($"[GARDEN] bed uid={uid} obj={objId} state applied ✓ (json={json.Length}ch, growing={ReadGrowing(target):F3})");
                }
            }
            finally { ApplyingRemoteGarden = false; }
            return true;   // target found — remove from queue
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[GARDEN] 0x21 apply: {e.Message}"); return true; }
    }

    // Host-authoritative growth reconcile: periodically sends each bed's state (json-dedup →
    // only changed = growth/plant/harvest). Closes the day-skip "growing" drift. HOST only.
    public static void TickGardens()
    {
        try
        {
            if (!Connected() || SteamNetwork.Role != NetworkRole.Host || !SteamNetwork.RemotePlayerSpawned) return;
            _gardenSyncTimer += Time.deltaTime;
            if (_gardenSyncTimer < GARDEN_SYNC_INTERVAL) return;
            _gardenSyncTimer = 0f;
            if (!GraveStateReflectionReady()) return;
            foreach (var comp in ScanWgosCached())
            {
                var mb = comp as MonoBehaviour;
                if (mb == null || !IsGardenTarget(mb)) continue;
                TrySendGarden(mb);
            }
        }
        catch { }
    }

    public static void ResetGardenSync()
    {
        _lastSentGardenJson.Clear(); _lastGardenObjId.Clear(); _gardenSyncTimer = 0f;
        _pendingGardens.Clear();
    }

    // The bed's growing param (for Stage 2 DIAG: measure whether growth is deterministic from time).
    private static float ReadGrowing(MonoBehaviour wgo)
    {
        try { return _getParamMethod != null ? Convert.ToSingle(_getParamMethod.Invoke(wgo, new object[] { "growing", 0f })) : -1f; }
        catch { return -1f; }
    }

    // Fail-open 0x21 retry: a bed-state packet arrived before its target loaded (0x15 build race OR
    // out-of-zone bed) → queue it, retry from a 2s timer until the object appears. Dedup by uid: a fresher
    // state replaces the older one.
    private class PendingGarden { public long uid; public byte[] data; }
    private static readonly List<PendingGarden> _pendingGardens = new List<PendingGarden>();
    private const int PENDING_GARDEN_MAX = 128;

    private static void EnqueuePendingGarden(long uid, byte[] data)
    {
        var existing = _pendingGardens.FirstOrDefault(p => p.uid == uid);
        if (existing != null) { existing.data = data; return; }   // fresher state of the same bed
        if (_pendingGardens.Count < PENDING_GARDEN_MAX)
            _pendingGardens.Add(new PendingGarden { uid = uid, data = data });
    }

    // 0x15 (placing garden_*_place) invalidates a queued garden_empty preview-state: DoPlace does
    // ReplaceWithObject(obj_id+"_place") (decompiled 44018), so the floating bed preview has obj_id
    // garden_empty and our trigger sends it BEFORE 0x15. Without this the retry would apply empty over a
    // fresh frame → "bed placed already dug". The real dig sends garden_empty AFTER 0x15 (reliable+ordered).
    public static void InvalidatePendingGarden(long uid)
    {
        _pendingGardens.RemoveAll(p => p.uid == uid);
    }

    // From SteamManager.Update (retry timer, every 2s): zone/0x15 finished loading → apply.
    public static void RetryPendingGardens()
    {
        for (int i = _pendingGardens.Count - 1; i >= 0; i--)
        {
            long uid = _pendingGardens[i].uid;
            if (ApplyRemoteGardenState(_pendingGardens[i].data, fromRetry: true))
            {
                Multiplayer.Log?.LogInfo($"[GARDEN] 0x21 retry: bed uid={uid} applied ✓");
                _pendingGardens.RemoveAt(i);
            }
        }
    }

    // ── Receive 0x0D: grave state replication via item_data ─────────────────────
    // Find the grave by unique_id (fallback — position). Take the target's LOCAL SerializableWGO
    // (FromWGO — with this machine's valid formulas/refs), swap only item_data/obj_id/variation with the
    // received ones, null out item (so restore rebuilds _data from item_data), then RestoreFromSerializedObject.
    // The echo-guard mutes the back-broadcast (restore triggers RedrawPart → the Postfix exits early).
    public static void ApplyRemoteGraveState(long uid, float x, float y,
                                             string objId, int variation, string json)
    {
        EnsureReflection();
        if (!_ready || !GraveStateReflectionReady()) return;

        // Corruption guard: do NOT apply an empty state (an empty one already wiped _data →
        // "No data" / empty pits, live test 2026-06-07). Better nothing.
        if (string.IsNullOrEmpty(json))
        {
            Multiplayer.Log?.LogWarning($"[CHOP] 0x0D: json EMPTY for uid={uid} — skipping (don't corrupt the grave)");
            return;
        }

        // TERMINAL-STATE RECONCILIATION (fix for a stuck "-1" on co-dig, 2026-06-08):
        // grave_empty is the final state (no parts → runaway/re-drop impossible). So we apply it
        // ALWAYS, bypassing the owner-guard. On co-dig both owner-guards block each other's states →
        // the contested grave diverges and gets stuck on grave_ground. Once someone FINISHES it locally and
        // sends empty, this branch forces both sides into the empty state, unsticking it.
        bool isTerminal = !string.IsNullOrEmpty(objId) && objId.StartsWith("grave_empty");

        // OWNER GUARD (fix for runaway dupe on co-dig of the same grave, 2026-06-08): if I genuinely
        // dug this grave within GRAVE_OWNER_WINDOW — do NOT apply the incoming restore (except terminal —
        // it's safe and needed for reconciliation). Otherwise restore rehydrates my active grave's _data → the
        // game re-drops the part dozens of times (log: 88× plate/3.3s). Monotonicity is useless here (co-dig stages match).
        if (!isTerminal
            && _lastLocalGraveDigTime.TryGetValue(uid, out var dugAt)
            && UnityEngine.Time.realtimeSinceStartup - dugAt < GRAVE_OWNER_WINDOW)
            return;  // I'm the active digger of this grave → don't apply the other's restore (rehydration → dupe)

        var target = FindWgoByUniqueId(uid);
        if (target == null)
        {
            target = FindTargetNear(x, y, out float dist, IsGraveTarget);
            if (target == null || dist > POSITION_EPSILON)
            {
                Multiplayer.Log?.LogWarning($"[CHOP] 0x0D: grave uid={uid} @({x:F1},{y:F1}) not found " +
                    $"(nearest: {(target != null ? dist.ToString("F1") : "none")})");
                return;
            }
        }

        // Receiver-side DEDUP by the STAGE signature (not raw json — same noise). The same stage for a uid
        // isn't applied twice → 1 RestoreFromSerializedObject/stage. IMPORTANT: _lastAppliedGraveSig is also
        // read by the echo-suppression in OnLocalGraveStateChanged, so the signature MUST be the same
        // (GraveStageSig) on both paths. Body bit from the packet (variation bit0) — the same one the sender
        // added to its signature; after restore our grave will also have the body → future GraveHasBody matches.
        // Workbench content hash from the packet (variation bits 1-30) — same as the sender's; after restore our
        // workbench has the same inventory → future WorkbenchInvHash converges.
        int invH = variation >> 1;
        string sig = GraveStageSig(objId, json) + (((variation & 1) != 0) ? "|B" : "") + (invH != 0 ? "|w" + invH : "");
        if (_lastAppliedGraveSig.TryGetValue(uid, out var prevSig) && prevSig == sig) return;

        // MONOTONICITY GUARD (dupe fix 2026-06-08) — NOW ONLY DURING ACTIVE CONTEST (reverse-sync fix 2026-06-10).
        // Used to act unconditionally (digging = irreversible), dropping any "backward" state as stale-echo — else
        // a backward restore rehydrated _data and re-dropped a slab every frame (pyramid). But a legitimate
        // reverse-sync (placing a body, rebuild) goes "up" the stages and the guard cut it (viewer saw only an
        // empty grave). So monotonicity acts ONLY if I worked this grave within GRAVE_CONTEST_WINDOW; a pure
        // viewer applies states in order (reliable+ordered).
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
                if (inRank < locRank) return;                                  // obj_id "backward" — stale/echo
                if (inRank == locRank && !GraveParts(json).IsSubsetOf(GraveParts(localJson)))
                    return;                                                    // adds parts back — rehydration
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[CHOP] 0x0D monotonicity guard failed (applying anyway): {e.Message}"); }

        LogGraveFurniture(target, uid, "BEFORE-apply");   // DIAG: grave furniture / repair sync
        ApplyingRemoteChop = true;
        try
        {
            // Build the target's local SerializableWGO, pour the sender's JSON into its Item (FromJsonOverwrite →
            // _params/inventory update, OnAfterDeserialize rebuilds nested data), swap obj_id (grave_ground→empty
            // transitions). RestoreFromSerializedObject applies the state + rebuilds the visual in one call.
            var sw = _fromWgoMethod.Invoke(null, new object[] { target });
            if (sw == null) { Multiplayer.Log?.LogWarning("[CHOP] 0x0D: FromWGO(receiver) = null"); return; }
            if (_fromJsonOverwriteMethod == null) { Multiplayer.Log?.LogWarning("[CHOP] 0x0D: JsonUtility.FromJsonOverwrite missing"); return; }
            var item = _swItemField.GetValue(sw);
            if (item != null)
            {
                _fromJsonOverwriteMethod.Invoke(null, new object[] { json, item });
            }
            else
            {
                // Fallback path: pour directly into the target's live _data.
                var data = _dataField.GetValue(target);
                if (data == null) { Multiplayer.Log?.LogWarning("[CHOP] 0x0D: target's _data = null"); return; }
                _fromJsonOverwriteMethod.Invoke(null, new object[] { json, data });
                _swItemField.SetValue(sw, data);
            }
            if (!string.IsNullOrEmpty(objId)) _swObjIdField.SetValue(sw, objId);
            _restoreFromSerializedMethod.Invoke(target, new object[] { sw, false });
            // THE VIEWER DOESN'T BUILD → don't show the "under construction" frame. The game draws a wooden
            // frame (grave_*_building_1, OnDrawGrave/GetNewWOPPrefabsNames 88883) for a furniture item in the
            // inventory with param<1 (a disassembly transient the builder skips but the viewer gets via snapshot).
            // We force param=1 on furniture items IN THE INVENTORY → always a finished stone. Dug-out items are no
            // longer in the inventory → untouched → vanish correctly. Must be BEFORE the redraw below. Test #7 bug.
            ForceGraveFurnitureComplete(target);
            // FURNITURE FORCE-REDRAW (fix 2026-06-10): RestoreFromSerializedObject puts items into _data.inventory
            // + SetObject, but does NOT draw the furniture visual or refresh grave quality (live test #3: frame
            // applied but didn't appear, quality stuck at "-1"). The game draws furniture via Redraw(force:true)→
            // OnObjectRedraw (114060), called here. Safe from re-send (still under the ApplyingRemoteChop guard).
            if (_redrawMethod != null)
                try { _redrawMethod.Invoke(target, new object[] { true, false, false }); }
                catch (Exception re) { Multiplayer.Log?.LogWarning($"[CHOP] 0x0D force-redraw failed: {re.Message}"); }
            _lastGraveRestoreTime[uid] = UnityEngine.Time.realtimeSinceStartup;  // per-uid: mute this grave's runaway-drop on the viewer
            _lastAppliedGraveSig[uid] = sig;
            _lastAppliedGraveSigTime[uid] = UnityEngine.Time.realtimeSinceStartup;  // timestamp for the echo-suppression WINDOW
            // Terminal state: grave finished → clear all tracking for the uid so nothing lingers
            // (owner window, artifact-restore, per-stage dedup). The grave is quiet.
            if (isTerminal)
            {
                _lastLocalGraveDigTime.Remove(uid);
                _lastGraveRestoreTime.Remove(uid);
                _graveSentStageSig.Remove(uid);
                _graveSentParts.Remove(uid);
            }
            LogGraveFurniture(target, uid, "AFTER-apply");   // DIAG: did marble appear from the json/force?
            Multiplayer.Log?.LogInfo($"[CHOP] Grave uid={uid} obj={objId} state restored ✓ (json={json.Length}ch)");
            // Phase 3: if this chest is open for us right now — live panel refresh (see changes immediately).
            RefreshOpenChestGui(target);
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] 0x0D RestoreFromSerializedObject failed: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
    }

    // Parse a 0x0D packet → ApplyRemoteGraveState.
    public static void ParseAndApplyGraveOp(byte[] data)
    {
        if (data == null || data.Length < 25) { Multiplayer.Log?.LogWarning($"[CHOP] 0x0D too small: {data?.Length}"); return; }
        EnsureReflection();
        if (!GraveStateReflectionReady()) { Multiplayer.Log?.LogWarning("[CHOP] 0x0D: serialize-API not ready"); return; }
        try
        {
            long uid = BitConverter.ToInt64(data, 1);
            float x  = BitConverter.ToSingle(data, 9);
            float y  = BitConverter.ToSingle(data, 13);
            int variation = BitConverter.ToInt32(data, 17);
            int off  = 21;
            int idLen = BitConverter.ToUInt16(data, off); off += 2;
            if (off + idLen + 4 > data.Length) { Multiplayer.Log?.LogWarning("[CHOP] 0x0D objId out of bounds"); return; }
            string objId = System.Text.Encoding.UTF8.GetString(data, off, idLen); off += idLen;
            int dataLen = BitConverter.ToInt32(data, off); off += 4;
            if (dataLen < 0 || off + dataLen > data.Length) { Multiplayer.Log?.LogWarning("[CHOP] 0x0D itemData out of bounds"); return; }
            string itemData = System.Text.Encoding.UTF8.GetString(data, off, dataLen);
            ApplyRemoteGraveState(uid, x, y, objId, variation, itemData);
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] ParseAndApplyGraveOp: {e.Message}"); }
    }

    // ── Receive 0x0A: object felled/broken — remove it here ────────────
    public static void ApplyRemoteDestroy(float x, float y)
    {
        EnsureReflection();
        if (!_ready) return;

        var target = FindTargetNear(x, y, out float dist, IsTransformSyncTarget);
        if (target == null || dist > POSITION_EPSILON)
        {
            // Object not "awake" yet — the player is in another location. Remember it,
            // destroy in RetryPendingDestroys once they approach and it loads.
            var pos = new Vector2(x, y);
            if (!_pendingDestroys.Any(p => Vector2.Distance(p, pos) < POSITION_EPSILON))
            {
                _pendingDestroys.Add(pos);
                Multiplayer.Log?.LogInfo($"[CHOP] Object @({x:F1},{y:F1}) not loaded yet " +
                    $"— queued ({_pendingDestroys.Count})");
            }
            return;
        }
        Multiplayer.Log?.LogInfo($"[CHOP] Removing felled object @({x:F1},{y:F1}) ✓");
        FellTarget(target);
    }

    // Removes a felled object like the game does. Tree: play the fall animation first
    // (TreeDisappearAnimation.StartAnimation), set the stump in its completion callback.
    // Stone / ready stump — immediately.
    private static void FellTarget(MonoBehaviour target)
    {
        string nextId    = ResolveAfterHp0(target);   // tree → "tree_X_stump"; stone → ""
        int    variation = ReadVariation(target);

        if (TryPlayFallAnimation(target, nextId, variation)) return;
        FinishFell(target, nextId, variation);
    }

    // Reads obj_def.after_hp_0 — the successor object's id. null/empty = no successor.
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

    // Tree: start the fall animation; when it finishes, the FinishFell callback
    // sets the stump. true = the animation started (FellTarget returns).
    private static bool TryPlayFallAnimation(MonoBehaviour wgo, string nextId, int variation)
    {
        if (_treeDisappearType == null || _startAnimationMethod == null
            || _voidDelegateType == null)
            return false;
        try
        {
            // Stone/stump lacks this component — then it's not a tree, no animation.
            var anim = wgo.GetComponentInChildren(_treeDisappearType, true);
            if (anim == null) return false;

            var completion = new FellCompletion { Wgo = wgo, NextId = nextId, Variation = variation };
            var onDone = Delegate.CreateDelegate(_voidDelegateType, completion,
                typeof(FellCompletion).GetMethod(nameof(FellCompletion.OnFallDone)));
            _startAnimationMethod.Invoke(anim, new object[] { onDone });
            Multiplayer.Log?.LogInfo($"[CHOP] Tree fall animation started → then '{nextId}'");
            return true;
        }
        catch (Exception e)
        {
            Multiplayer.Log?.LogWarning($"[CHOP] Fall animation failed: {e.Message}");
            return false;
        }
    }

    // Felling finale: stump via ReplaceWithObject; no successor → Destroy.
    private static void FinishFell(MonoBehaviour wgo, string nextId, int variation)
    {
        if (wgo == null) return;
        try
        {
            if (!string.IsNullOrEmpty(nextId) && _replaceWithObjectMethod != null)
            {
                // ApplyingRemoteChop mutes the echo if ReplaceWithObject triggers the patch.
                ApplyingRemoteChop = true;
                try { _replaceWithObjectMethod.Invoke(wgo, new object[] { nextId, true, variation }); }
                finally { ApplyingRemoteChop = false; }
                Multiplayer.Log?.LogInfo($"[CHOP] Object → '{nextId}' (stump) ✓");
            }
            else
            {
                UnityEngine.Object.Destroy(wgo.gameObject);
                Multiplayer.Log?.LogInfo("[CHOP] Object destroyed ✓");
            }
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] FinishFell: {e.Message}"); }
    }

    // Context for the VoidDelegate callback of the tree fall animation's completion.
    private class FellCompletion
    {
        public MonoBehaviour Wgo;
        public string        NextId;
        public int           Variation;
        public void OnFallDone() => FinishFell(Wgo, NextId, Variation);
    }

    // Periodically try to destroy queued objects — when the player reaches the
    // location, the object "wakes up" as a WorldGameObject and becomes findable.
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
                Multiplayer.Log?.LogInfo($"[CHOP] Deferred object removal @({pos.x:F1},{pos.y:F1}) ✓");
                FellTarget(target);
                _pendingDestroys.RemoveAt(i);
            }
        }
    }

    // ── LOOT SYNC (0x0B) ─────────────────────────────────────────────────────
    // Local DropItems → send 0x0B (list of Items + Direction + coords). On the receiver — replay the
    // same DropItems under the ApplyingRemoteChop echo-guard. Each machine gets its own local
    // DropResGameObject; with inventory sync, the pickup event removes the local drop without adding items.
    private static bool DropReflectionReady() =>
        _itemType != null && _itemIdField != null && _itemValueField != null
        && _directionType != null && _dropItemsMethod != null;

    // Refreshes the grave owner-window on EVERY tick of local work (DoAction each frame while holding F).
    // Critical: the work session (and the first harmful incoming restore) starts BEFORE the first stage
    // change, so marking only in OnLocalGraveStateChanged is too late — restore rehydrated the active grave's
    // _data → the work bar stuck full and the game repeatedly "finished" the work, spitting out gravestones
    // (Zonda live test 2026-06-08). Hence: while I'm digging a grave, the owner-guard must block ALL its restores.
    public static void NoteLocalGraveWork(MonoBehaviour wgo)
    {
        if (ApplyingRemoteChop) return;   // this is a replay, not my work — don't claim ownership
        EnsureReflection();
        if (!_ready || wgo == null || _objIdField == null || _uniqueIdField == null) return;
        var oid = _objIdField.GetValue(wgo) as string;
        if (oid == null || !oid.StartsWith("grave")) return;
        try { _lastLocalGraveDigTime[Convert.ToInt64(_uniqueIdField.GetValue(wgo))] = UnityEngine.Time.realtimeSinceStartup; }
        catch { }
    }

    // Called from the DropItems PREFIX. true → CANCEL the game's local drop (and the Postfix won't
    // broadcast). Cures the local pyramid on the VIEWER: after restore the game re-drops a grave part
    // dozens of times (runaway). Postfix-dedup caught only the network, but the item already spawned
    // locally — so the decision is here, BEFORE the game call. Graves only, genuine local drops only:
    //   • I'm NOT digging this grave but it was just rehydrated by an incoming state → a restore artifact
    //     (the owner-guard blocks restore on MY active grave, so a recent restore = I'm the viewer) → cancel.
    //     The viewer gets its copy via shared-loot replay.
    //   • Backstop: per-stage dedup — each part is dropped 1× per stage; a repeat → cancel.
    public static bool ShouldSuppressGraveDrop(MonoBehaviour wgo, object itemsList)
    {
        if (ApplyingRemoteChop) return false;          // shared-loot replay / restore-internal — let through
        EnsureReflection();
        if (!_ready || wgo == null || _objIdField == null || _uniqueIdField == null
            || _toJsonMethod == null || _dataField == null) return false;
        var oid = _objIdField.GetValue(wgo) as string ?? "";
        if (!oid.StartsWith("grave")) return false;    // graves only
        long uid;
        try { uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo)); } catch { return false; }

        float now = UnityEngine.Time.realtimeSinceStartup;
        bool amDigging = _lastLocalGraveDigTime.TryGetValue(uid, out var dugAt)
                         && now - dugAt < GRAVE_OWNER_WINDOW;

        // Viewer restore artifact: I'm not digging + the grave was just rehydrated → runaway.
        if (!amDigging && _lastGraveRestoreTime.TryGetValue(uid, out var rt)
            && now - rt < GRAVE_RESTORE_ARTIFACT_WINDOW)
            return true;

        // Per-stage backstop: each part 1× per stage (in case the artifact window expired but the
        // runaway continues; for the digger — protection against repeats within a stage).
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
        if (!anyNew && list != null && list.Count > 0) return true;  // repeat part within a stage — runaway
        return false;
    }

    // CORPSE — SINGLE hook on static DropResGameObject.Drop: the shared path of ALL corpse spawns (exhuming +
    // hand-drop). Hooking only WGO.DropItem missed hand-drop → the corpse vanished on the partner. We register
    // the EXACT spawned GO (__result), no "nearest" scan. uid is synthetic (Ticks, key in 0x11). Body state
    // rides in JSON (ToJSON(0)). Packet 0x11: type uid dir x y z force b4 jsonLen json.
    public static void OnCorpseDropped(object[] dropArgs, object resultGO)
    {
        EnsureReflection();
        if (!DropReflectionReady() || dropArgs == null || dropArgs.Length < 2) return;
        var item = dropArgs[1];                                   // Drop(pos, Item, parent, dir, force, curve, walls, stacked)
        if (item == null) return;
        var itemId = _itemIdField.GetValue(item) as string ?? "";
        var go = resultGO as MonoBehaviour;
        if (go == null) return;

        if (ApplyingRemoteChop)
        {
            // Our 0x11 replay → register the mirror under the given uid, owner=False. Other echo-drops
            // (0x0B loot, etc.) are NOT registered: _incomingCorpseUid is set only by ApplyRemoteCorpseSpawn.
            if (_incomingCorpseUid == null) return;
            RegisterCorpseBodyGO(go, _incomingCorpseUid.Value, owner: false, itemId);
            return;
        }
        if (!Connected()) return;

        // CARRY-MIRROR v2 (2026-06-12): generalization RESTORED with anti-spam. Mirrored: a corpse
        // (body, exhume+drop) OR any item dropped FROM HAND (overhead == item: DropOverheadItem calls Drop
        // BEFORE SetOverheadItem(null), decompiled 82071-72 → in the Postfix overhead still matches). Felled
        // logs/loot go via DropItems(plural) → don't pass the overhead gate → the shared 0x0B is untouched.
        bool isBody = itemId == "body";
        bool isHandDrop = false;
        try { isHandDrop = ReferenceEquals(GetLocalPlayerOverheadItem(), item); } catch { }
        // BIG = ONE SHARED: item_size>=2 (boulder/marble/log from a vein or sawmill) is tracked
        // from BIRTH → 0x11 mirror; such items are excluded from 0x0B (OnLocalDropItems).
        bool isBig = false;
        try
        {
            var ti = item as Item;
            isBig = ti != null && ti.definition != null && ti.definition.item_size >= 2;
        }
        catch { }
        if (!isBody && !isHandDrop && !isBig) return;

        // ANTI-SPAM: re-drop of the same item nearby within the TTL window → reuse uid (the partner's mirror
        // lives on; the scheduled 0x10 is cancelled). Otherwise — a fresh uid (Ticks).
        long uid = 0;
        var p0 = go.transform.position;
        for (int pi = _pendingCarry.Count - 1; pi >= 0; pi--)
        {
            var pc = _pendingCarry[pi];
            if (pc.itemId == itemId &&
                Vector2.Distance(pc.pos, new Vector2(p0.x, p0.y)) < CARRY_UID_REUSE_RADIUS)
            {
                uid = pc.uid;
                Multiplayer.Log?.LogInfo($"[CHOP] Re-drop '{itemId}' → reuse uid={uid} " +
                    $"(0x10 {(pc.removeSent ? "already sent — receiver will respawn via 0x11" : "cancelled")})");
                _pendingCarry.RemoveAt(pi);
                break;
            }
        }
        // Ticks has ~10-15ms granularity → 3-4 logs from a perk in ONE frame got the SAME uid (on the
        // partner all 0x11 fell into one mirror that "pinged" between positions). A monotonic counter
        // guarantees uniqueness within a frame.
        if (uid == 0)
        {
            uid = DateTime.UtcNow.Ticks;
            if (uid <= _lastIssuedUid) uid = _lastIssuedUid + 1;
            _lastIssuedUid = uid;
        }
        RegisterCorpseBodyGO(go, uid, owner: true, itemId);

        var pos = go.transform.position;                          // actual position of the spawned corpse
        int dir = (dropArgs.Length > 3 && dropArgs[3] != null) ? Convert.ToInt32(dropArgs[3]) : 0;
        float force = (dropArgs.Length > 4 && dropArgs[4] != null) ? Convert.ToSingle(dropArgs[4]) : 1f;
        bool walls = false;                                       // receiver spawns EXACTLY at pos (no reposition)
        string json = "";
        try { if (_toJsonMethod != null) json = _toJsonMethod.Invoke(item, new object[] { 0 }) as string ?? ""; }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[CHOP] corpse ToJSON failed: {e.Message}"); }
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
        Multiplayer.Log?.LogInfo($"[CHOP] Carried-item spawn '{itemId}' → uid={uid} pos=({pos.x:F1},{pos.y:F1}) json={jb.Length}b → 0x11");
    }

    // Register a corpse by its EXACT GameObject (from the Drop hook) — no scene scan.
    public static void RegisterCorpseBodyGO(MonoBehaviour go, long uid, bool owner, string itemId = "body")
    {
        if (go == null) return;
        var existing = FindBodyByUid(uid);
        if (existing != null && existing.go != null)
        {
            // v2: a DEACTIVATED old GO (picking up ore/log deactivates, doesn't destroy) — stale,
            // don't block re-registering a new GO under a reused uid in the same frame.
            bool act = false;
            try { act = existing.go.gameObject.activeInHierarchy; } catch { }
            if (act && !ReferenceEquals(existing.go, go)) return;
        }
        if (_bodies.Any(b => ReferenceEquals(b.go, go))) return;   // already tracked
        var p = go.transform.position;
        _bodies.RemoveAll(b => b.uid == uid);
        _bodies.Add(new BodyTrack {
            go = go, uid = uid, itemId = itemId, lastPos = new Vector2(p.x, p.y),
            registeredTime = Time.time, lastRemoteTime = -99f, lastSendTime = -99f, isOwner = owner
        });
        Multiplayer.Log?.LogInfo($"[CHOP] Carried item '{itemId}' registered → uid={uid} owner={owner} pos=({p.x:F1},{p.y:F1})");
    }

    // Receiver of 0x11: recreate the FULL corpse on the viewer's grave via the same DropItem (under the
    // echo-guard). Body restored from JSON (organs+freshness), not an empty new Item. The
    // DropItemSingularBroadcastPatch postfix picks it up into the registry (owner=False).
    public static void ApplyRemoteCorpseSpawn(long uid, int dir, float x, float y, float z, float force, bool b4, string json)
    {
        EnsureReflection();
        if (!DropReflectionReady() || _dropItemSingularMethod == null || _itemCtor == null) return;
        // Target for the instance DropItem call: the source grave (initial spawn) OR, if its uid isn't found
        // (hand re-drop, uid=player), any local wgo — the position is explicit (x,y,z) anyway, so which wgo
        // we call on doesn't matter.
        var target = FindWgoByUniqueId(uid) ?? GetLocalPlayerWgo();
        if (target == null)
        {
            Multiplayer.Log?.LogWarning($"[CHOP] 0x11: neither source uid={uid} nor a local wgo — corpse not spawned");
            return;
        }
        // Already a live mirror of this uid? (repeat 0x11 / reused uid on re-drop) — don't duplicate:
        // alive+active → reposition to the packet's position; dead/deactivated → respawn.
        var existed = FindBodyByUid(uid);
        if (existed != null && existed.go != null)
        {
            bool act = false;
            try { act = existed.go.gameObject.activeInHierarchy; } catch { }
            if (act)
            {
                try
                {
                    existed.go.transform.position = new Vector3(x, y, z);
                    existed.lastRemoteTime = Time.time;   // echo-guard: the tick won't send this move back
                    existed.lastPos = new Vector2(x, y);
                }
                catch { }
                Multiplayer.Log?.LogInfo($"[CHOP] 0x11: mirror uid={uid} already live — reposition ✓");
                return;
            }
            _bodies.Remove(existed);   // stale track — respawn cleanly below
        }
        ApplyingRemoteChop = true;
        _incomingCorpseUid = uid;   // RegisterCorpseBody (postfix) registers under this uid, owner=False
        try
        {
            // "body" is just a STUB: FromJsonOverwrite overwrites public fields incl. id,
            // so the real item (corpse/log/stone) comes from the sender's JSON (generalized).
            var body = _itemCtor.Invoke(new object[] { "body", 1 });
            if (string.IsNullOrEmpty(json))
                Multiplayer.Log?.LogWarning($"[CHOP] 0x11 uid={uid}: empty json — the item stays 'body' (stub)!");
            if (!string.IsNullOrEmpty(json) && _fromJsonOverwriteMethod != null)
            {
                try { _fromJsonOverwriteMethod.Invoke(null, new object[] { json, body }); }
                catch (Exception je) { Multiplayer.Log?.LogWarning($"[CHOP] 0x11 FromJsonOverwrite failed: {je.Message}"); }
            }
            var direction = Enum.ToObject(_directionType, dir);
            var ps = _dropItemSingularMethod.GetParameters();
            var call = new object[ps.Length];
            if (ps.Length > 0) call[0] = body;
            if (ps.Length > 1) call[1] = direction;
            if (ps.Length > 2) call[2] = new Vector3(x, y, z);
            if (ps.Length > 3) call[3] = force;     // float (decompile: DropItem param 3 = float force)
            if (ps.Length > 4) call[4] = b4;        // bool check_walls
            _dropItemSingularMethod.Invoke(target, call);
            Multiplayer.Log?.LogInfo($"[CHOP] 0x11: corpse uid={uid} spawned ✓");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] 0x11 corpse spawn failed: {e.Message}"); }
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

            // Collect (id, value) — silently skip empty ids (shouldn't happen).
            var triples = new List<KeyValuePair<byte[], int>>(list.Count);
            int payload = 0;
            foreach (var it in list)
            {
                if (it == null) continue;
                // BIG = ONE SHARED (2026-06-12, Zonda's decision): carried items (item_size>=2 —
                // boulders/marble/logs) are NOT duplicated by shared loot — they're mirrored as a single
                // object via 0x11 (the Drop hook tracks them at spawn). Otherwise the partner's copy + the
                // hand-drop mirror = 2 boulders (carry v2 live test). Small loot — as before ("both receive").
                try
                {
                    var ti = it as Item;
                    if (ti != null && ti.definition != null && ti.definition.item_size >= 2) continue;
                }
                catch { }
                var id = _itemIdField.GetValue(it) as string;
                if (string.IsNullOrEmpty(id)) continue;
                int val = Convert.ToInt32(_itemValueField.GetValue(it));
                var idBytes = System.Text.Encoding.UTF8.GetBytes(id);
                if (idBytes.Length > 255) continue;       // ids are small (like wood/branch)
                triples.Add(new KeyValuePair<byte[], int>(idBytes, val));
                payload += 1 + idBytes.Length + 4;
            }
            if (triples.Count == 0 || triples.Count > 255) return;
            // Grave-runaway dedup/suppression is now in the DropItems Prefix (ShouldSuppressGraveDrop):
            // if a local drop is suppressed there, this Postfix isn't called (a flag in the patch),
            // so only legitimate drops that should be broadcast reach here.

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
            Multiplayer.Log?.LogInfo($"[CHOP] Loot @({p.x:F1},{p.y:F1}) → {triples.Count} item(s), dir={dir}");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] OnLocalDropItems: {e.Message}"); }
    }

    // Parses 0x0B → builds List<Item> and Direction. Returns false on any problem.
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
            // Object not "awake" yet — queue it. Retry when the player approaches.
            var pos = new Vector2(x, y);
            if (!_pendingDrops.Any(d => Vector2.Distance(d.Pos, pos) < POSITION_EPSILON))
            {
                _pendingDrops.Add(new PendingDrop { Pos = pos, Items = itemsList, Direction = direction });
                Multiplayer.Log?.LogInfo($"[CHOP] Loot @({x:F1},{y:F1}) deferred ({_pendingDrops.Count})");
            }
            return;
        }

        // OWNER-LOOT (fix for 2× on co-dig of the same grave, 2026-06-08): if the target is a grave
        // I'm actively digging right now (owner window), do NOT replay incoming loot: I'm already making
        // my own copy of this part locally. Without this co-dig gives 2 copies per machine (mine + the
        // partner's). Normal shared loot (one digs/the other watches) is unaffected: the viewer doesn't
        // dig → owner window empty → gets the copy as before.
        if (IsGraveTarget(target) && _uniqueIdField != null)
        {
            try
            {
                long guid = Convert.ToInt64(_uniqueIdField.GetValue(target));
                if (_lastLocalGraveDigTime.TryGetValue(guid, out var dugAt)
                    && UnityEngine.Time.realtimeSinceStartup - dugAt < GRAVE_OWNER_WINDOW)
                    return;  // I'm digging this grave → making my own copy, don't dup the other's loot
            }
            catch { }
        }

        InvokeDropItems(target, itemsList, direction);
    }

    private static void InvokeDropItems(MonoBehaviour target, object itemsList, object direction)
    {
        // Replay the other's DropItems under the ApplyingRemoteChop echo-guard (so the receiver doesn't
        // broadcast it back). Loot is shared — the receiver gets its own copy.
        int expected = (itemsList as System.Collections.IList)?.Count ?? -1;
        ApplyingRemoteChop = true;
        try { _dropItemsMethod.Invoke(target, new[] { itemsList, direction }); }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] DropItems failed: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
        Multiplayer.Log?.LogInfo($"[CHOP] Loot replayed ✓ (shared mode: both get ~{expected} item(s))");
    }

    // ── CARRIED CORPSE SYNC (0x0F position / 0x10 removal) ──────────────────────
    // Recon 2026-06-09 (F5 trace, full cycle take→carry→drop→put in grave): the corpse is NOT destroyed on
    // pickup — the game keeps the same DropResGameObject and CARRIES it with the player (the id=body
    // range-check continues after DestroyLinkedHint; it vanishes only on placing via ReplaceWithObject). So
    // the "destroy the viewer's copy" model was WRONG. Correct: the corpse is always alive on both; the machine
    // that moves it LOCALLY (carry/drop) broadcasts the position by the source grave's uid (the uid is SHARED —
    // proven by the stage sync); the other mirrors its copy. Gone on the carrier (placing/consuming) → 0x10 →
    // the viewer removes cleanly (DestroyLinkedHint also clears the freshness indicator). No hand hook needed —
    // movement determines the owner. Foothold for inventory (anchor C) and any carried item (log/stone).
    private class BodyTrack
    {
        public MonoBehaviour go;       // local DropResGameObject(body)
        public long uid;               // source grave's uid (shared key)
        public string itemId = "body"; // item id (carry-mirror v2: reuse uid on re-drop)
        public Vector2 lastPos;
        public float registeredTime;   // for physics settling on spawn
        public float lastRemoteTime;   // when last moved by 0x0F (echo-guard)
        public float lastSendTime;     // send throttle
        public bool alive = true;
        public bool isOwner;           // true = spawned by a REAL exhumation (not a 0x0B replay)
    }
    private static readonly List<BodyTrack> _bodies = new List<BodyTrack>();
    private static MethodInfo _destroyLinkedHintMethod;

    private const float BODY_SETTLE_SEC   = 1.5f;   // physics settle after spawn (don't send movement)
    private const float BODY_ECHO_SEC     = 0.4f;   // after a 0x0F move, don't send back
    private const float BODY_SEND_MIN_SEC = 0.06f;  // throttle ~16/s
    private const float BODY_MOVE_EPS     = 0.1f;   // "moved" threshold

    // ── CARRY-MIRROR v2: anti-spam of drop cycles (2026-06-12) ──────────
    // Holding the button = a "drop-pickup" cycle each frame → v1 spawned 12 mirrors in seconds. v2: a
    // disappearance on the owner goes into _pendingCarry; 0x10 fires only after a debounce; a re-drop of the
    // same itemId nearby within the TTL REUSES the uid and cancels the 0x10 → ONE mirror, no flicker/dupes.
    private class PendingCarry
    {
        public long uid;
        public string itemId;
        public Vector2 pos;
        public float goneTime;
        public bool removeSent;
    }
    private static readonly List<PendingCarry> _pendingCarry = new List<PendingCarry>();
    private static long _lastIssuedUid;                   // monotonic uids (Ticks collide within a frame)
    private const float CARRY_REMOVE_DEBOUNCE   = 0.5f;  // 0x10 no sooner than 0.5s
    private const float CARRY_UID_REUSE_TTL     = 3f;    // uid-reuse window after disappearance
    private const float CARRY_UID_REUSE_RADIUS  = 300f;  // re-drop nearby = the same item

    private static BodyTrack FindBodyByUid(long uid)
    {
        for (int i = 0; i < _bodies.Count; i++) if (_bodies[i].uid == uid) return _bodies[i];
        return null;
    }

    // Is this drop a MIRROR corpse (owner=False): mirrored from the carrier's machine. It must NOT be
    // auto-collected locally, else it vanishes (live test 2026-06-09: GONE in 1 frame on the viewer —
    // the viewer player's auto-magnet grabbed the fresh corpse; on the owner it doesn't, because they just
    // finished the "work" exhumation and auto-collect is temporarily muted).
    public static bool IsMirrorCorpse(object drop)
    {
        if (drop == null) return false;
        for (int i = 0; i < _bodies.Count; i++)
            if (!_bodies[i].isOwner && ReferenceEquals(_bodies[i].go, drop)) return true;
        return false;
    }

    // uid of a MIRROR corpse (owner=false) by a reference to its ground GO. For ownership transfer:
    // when the viewer picks up the mirror, we need the uid to tell the owner to remove its ground copy.
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
        if (Time.time - _lastMirrorBlockLog < 2f) return;   // throttle (CanCollectDrop is called often)
        _lastMirrorBlockLog = Time.time;
        Multiplayer.Log?.LogInfo("[CHOP] Mirror-corpse auto-collect blocked ✓");
    }

    // uid to register the corpse under during a 0x11 replay (for re-drop wgo-uid ≠ the given uid).
    private static long? _incomingCorpseUid;

    // ── OVERHEAD CARRYING (0x12 carrying / 0x13 put down) ───────────────────────────────────
    // Recon: heavy items (corpse/log/stone) are carried via BaseCharacterComponent.SetOverheadItem(Item).
    // Hook the local player → broadcast so the partner shows the item above OUR clone's head. Complements
    // the ground mirror (0x0F/0x11).
    public static void OnLocalOverheadChanged(object item)
    {
        if (ApplyingRemoteChop || !Connected()) return;
        if (item == null)
        {
            var p = new byte[1] { 0x13 };
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, p);
            Multiplayer.Log?.LogInfo("[CHOP] Overhead put down → 0x13");
            return;
        }
        EnsureReflection();
        // CORPSE OWNERSHIP TRANSFER: if the item we just lifted overhead is our MIRROR corpse from the
        // ground (DropResGameObject.currently_higlighted_obj isn't nulled by the game yet at SetOverheadItem
        // time — nulled a line later, decompiled TryOtherInteractions 81559→81562), ownership passes to US.
        // We send 0x14 {uid} so the OWNER removes its ground corpse (else a dupe). Then we carry (0x12 below);
        // on drop the Drop hook makes us owner=true (0x11→mirror).
        // (The old 0x14 path here was REMOVED 2026-06-11: it fired a frame BEFORE MirrorPickupTransferPatch
        // and gave a DOUBLE 0x14 (test #5). Now only the deterministic TryOtherInteractions hook sends the
        // transfer — it covers the corpse too.)
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
        Multiplayer.Log?.LogInfo($"[CHOP] Overhead carrying icon={icon} → 0x12");
    }

    // Registration at corpse spawn (on BOTH machines: a real exhumation on one, a 0x0B replay on the
    // other — both call WGO.DropItem(body)). Finds the just-created body (nearest to the grave, not yet
    // registered) and binds it to the grave's uid. NO ApplyingRemoteChop gate.
    public static void RegisterCorpseBody(MonoBehaviour graveWgo, object item)
    {
        EnsureReflection();
        if (!DropReflectionReady() || graveWgo == null || item == null || _uniqueIdField == null) return;
        if ((_itemIdField.GetValue(item) as string) != "body") return;
        // During a 0x11 replay the uid comes from the packet (for a re-drop wgo it's the local player,
        // whose uid ≠ the corpse's logical uid). Otherwise — the source (grave) uid on a real drop.
        long uid;
        if (_incomingCorpseUid.HasValue) uid = _incomingCorpseUid.Value;
        else { try { uid = Convert.ToInt64(_uniqueIdField.GetValue(graveWgo)); } catch { return; } }
        // Already a live track for this uid? (DropItem may fire twice) — don't duplicate.
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
            if (_bodies.Any(b => ReferenceEquals(b.go, mb))) continue;   // already tracked
            if (ReadDropItemId(mb) != "body") continue;
            float d = Vector2.Distance(new Vector2(mb.transform.position.x, mb.transform.position.y),
                                       new Vector2(gp.x, gp.y));
            if (d < bestD) { bestD = d; best = mb; }
        }
        if (best == null) return;
        var p = best.transform.position;
        // OWNER = the machine where the corpse was spawned by a REAL exhumation (ApplyingRemoteChop==false).
        // The replay copy (0x0B) is NOT the owner, it only mirrors. Only the owner broadcasts 0x0F/0x10:
        // without this the viewer's copy (physics/auto-collect) sent a spurious 0x10 and erased the real corpse.
        bool owner = !ApplyingRemoteChop;
        _bodies.RemoveAll(b => b.uid == uid);   // replace a dead track of the same uid
        _bodies.Add(new BodyTrack {
            go = best, uid = uid, lastPos = new Vector2(p.x, p.y),
            registeredTime = Time.time, lastRemoteTime = -99f, lastSendTime = -99f, isOwner = owner
        });
        Multiplayer.Log?.LogInfo($"[CHOP] Corpse registered → uid={uid} owner={owner} dist={bestD:F1} " +
            $"bodyPos=({p.x:F1},{p.y:F1}) gravePos=({gp.x:F1},{gp.y:F1})");
    }

    // Reads the item id from a DropResGameObject (an Item-typed field with .id:string).
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

    // Clean drop removal: first remove the "linked hint" (freshness indicator — else it lingers as
    // a ghost after a raw Destroy), then destroy the GameObject itself.
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

    // Per-frame tick (from SteamManager.Update): for each local corpse — if it moved (carry/drop) and it's
    // not settling/an echo, broadcast the position (0x0F); if the GO disappeared (placed in a grave/consumed)
    // — broadcast removal (0x10) and clean up the track.
    public static void TickCarriedBodies()
    {
        float nowP = Time.time;
        // v2: deferred 0x10 — survived the debounce (re-drop didn't cancel) → send; TTL expired → clean up.
        for (int pi = _pendingCarry.Count - 1; pi >= 0; pi--)
        {
            var pc = _pendingCarry[pi];
            if (!pc.removeSent && nowP - pc.goneTime >= CARRY_REMOVE_DEBOUNCE)
            {
                pc.removeSent = true;
                if (Connected())
                {
                    var rm = new byte[9];
                    rm[0] = 0x10;
                    BitConverter.GetBytes(pc.uid).CopyTo(rm, 1);
                    SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, rm);
                    Multiplayer.Log?.LogInfo($"[CHOP] Carried '{pc.itemId}' uid={pc.uid} gone (debounce elapsed) → 0x10");
                }
            }
            if (nowP - pc.goneTime > CARRY_UID_REUSE_TTL) _pendingCarry.RemoveAt(pi);
        }

        if (_bodies.Count == 0) return;
        float now = Time.time;
        for (int i = _bodies.Count - 1; i >= 0; i--)
        {
            var b = _bodies[i];
            // "Gone" = destroyed OR DEACTIVATED (carry-mirror generalization 2026-06-11: ore/log isn't
            // destroyed on pickup from the ground, it's deactivated — the corpse is carried as a live GO, so
            // the old null check sufficed; without this the partner's mirror hung on the ground while the owner
            // carried the item, and a second drop spawned a dupe until the zone reloaded).
            bool goneLocally = b.go == null;
            try { goneLocally = goneLocally || !b.go.gameObject.activeInHierarchy; } catch { goneLocally = true; }
            if (goneLocally)
            {
                // ONLY the owner broadcasts removal. The viewer's copy may vanish from physics/auto-collect
                // — must NOT send 0x10 (that's exactly what erased the real corpse on the owner).
                // v2: don't send immediately — into the debounce queue: a re-drop in the window reuses the uid
                // and cancels the 0x10 (anti-spam of "drop-pickup" cycles while holding the button).
                if (b.alive && b.isOwner && Connected())
                {
                    _pendingCarry.Add(new PendingCarry {
                        uid = b.uid, itemId = b.itemId, pos = b.lastPos, goneTime = now
                    });
                    Multiplayer.Log?.LogInfo($"[CHOP] Carried '{b.itemId}' uid={b.uid} gone on the owner → 0x10 debounced ({CARRY_REMOVE_DEBOUNCE}s)");
                }
                // (The "GONE+player nearby → 0x14" heuristic was REMOVED: GetLocalPlayerWgo/overhead turned
                // out unreliable in the pickup frame — diag showed "hands empty dist=-1". Transfer is now sent
                // by the deterministic MirrorPickupTransferPatch hook on TryOtherInteractions, which removes the
                // track BEFORE this tick.)
                else
                {
                    // DIAGNOSTIC (carry-mirror dupe saga): WHY transfer/removal wasn't sent.
                    string why;
                    try
                    {
                        var lp = GetLocalPlayerWgo();
                        var ov = GetLocalPlayerOverheadItem();
                        float dist = -1f;
                        if (lp != null)
                        {
                            var p = lp.transform.position;
                            dist = Vector2.Distance(new Vector2(p.x, p.y), b.lastPos);
                        }
                        why = !b.alive ? "not-alive"
                            : !Connected() ? "not connected"
                            : ov == null ? $"hands empty (dist={dist:F0})"
                            : $"far (dist={dist:F0}, limit 300)";
                    }
                    catch (Exception de) { why = "diag-failed: " + de.Message; }
                    Multiplayer.Log?.LogInfo($"[DIAG-BODY] uid={b.uid} GONE owner={b.isOwner} — NOT sending 0x14/0x10: {why}");
                }
                _bodies.RemoveAt(i);
                continue;
            }
            var pos = b.go.transform.position;
            var p2 = new Vector2(pos.x, pos.y);
            if (Vector2.Distance(p2, b.lastPos) < BODY_MOVE_EPS) continue;   // didn't move
            float moved = Vector2.Distance(p2, b.lastPos);
            b.lastPos = p2;
            // ONLY the owner broadcasts position (the viewer just mirrors incoming 0x0F).
            if (!b.isOwner) continue;
            if (now - b.registeredTime < BODY_SETTLE_SEC) continue;          // physics settle
            if (now - b.lastRemoteTime < BODY_ECHO_SEC) continue;           // our echo (move from 0x0F)
            if (!Connected()) continue;
            if (now - b.lastSendTime < BODY_SEND_MIN_SEC) continue;          // throttle
            b.lastSendTime = now;
            var pk = new byte[17];
            pk[0] = 0x0F;
            BitConverter.GetBytes(b.uid).CopyTo(pk, 1);
            BitConverter.GetBytes(pos.x).CopyTo(pk, 9);
            BitConverter.GetBytes(pos.y).CopyTo(pk, 13);
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, pk);
            Multiplayer.Log?.LogInfo($"[DIAG-BODY] uid={b.uid} move {moved:F1} → 0x0F @({pos.x:F1},{pos.y:F1})");
        }
    }

    // ── DETERMINISTIC OWNERSHIP TRANSFER (carry-mirror dupe-saga fix, 2026-06-11) ─────────
    // The real moment of a MANUAL drop pickup = BaseCharacterComponent.TryOtherInteractions
    // (decompiled 81542): takes DropResGameObject.currently_higlighted_obj (a STATIC field), creates
    // a NEW Item for the hands (so a hands-match failed) and sets is_collected. The Prefix captures the
    // candidate from the static field (the method nulls it inside), Postfix __result=true = the pickup
    // HAPPENED. If the lifted GO is our mirror → 0x14 + remove the track (the GONE-tick and old overhead
    // path no longer match → no double transfer).
    private static MonoBehaviour _pendingPickupGo;
    public static void CapturePickupCandidate(object go) { _pendingPickupGo = go as MonoBehaviour; }
    public static void ConfirmPickup(bool result)
    {
        var go = _pendingPickupGo;
        _pendingPickupGo = null;
        if (!result || go == null || ApplyingRemoteChop || !Connected()) return;
        for (int i = 0; i < _bodies.Count; i++)
        {
            var b = _bodies[i];
            if (ReferenceEquals(b.go, go))
            {
                if (b.isOwner) return;   // own track → GONE-tick/debounced 0x10 handles it
                var tp = new byte[9]; tp[0] = 0x14;
                BitConverter.GetBytes(b.uid).CopyTo(tp, 1);
                SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, tp);
                Multiplayer.Log?.LogInfo($"[CHOP] Picked up mirror uid={b.uid} (TryOtherInteractions) → 0x14 (ownership transfer)");
                _bodies.RemoveAt(i);
                return;
            }
        }
        // UNtracked BIG drop (from an old save: both have independent copies born BEFORE connecting) → 0x1B:
        // the partner removes ITS copy by id+position (save-drops sit still → positional match is reliable).
        // Fail-open: not found → old behavior (the copy lingers).
        try
        {
            var dr = go as DropResGameObject;
            var res = dr != null ? dr.res : null;
            if (res == null || res.definition == null || res.definition.item_size < 2) return;
            var p = go.transform.position;
            var idb = System.Text.Encoding.UTF8.GetBytes(res.id ?? "");
            if (idb.Length == 0 || idb.Length > 255) return;
            var pk = new byte[10 + idb.Length];
            pk[0] = 0x1B;
            BitConverter.GetBytes(p.x).CopyTo(pk, 1);
            BitConverter.GetBytes(p.y).CopyTo(pk, 5);
            pk[9] = (byte)idb.Length;
            Buffer.BlockCopy(idb, 0, pk, 10, idb.Length);
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, pk);
            Multiplayer.Log?.LogInfo($"[CHOP] Pickup of untracked big '{res.id}' @({p.x:F1},{p.y:F1}) → 0x1B");
        }
        catch (Exception e) { Multiplayer.Log?.LogWarning($"[CHOP] 0x1B send: {e.Message}"); }
    }

    // Receiver of 0x1B: remove OUR copy of an untracked big drop (id + position ≤64). Tracked ones
    // (mirrors/own) are skipped — they're driven by 0x10/0x14. Zone not loaded (partner far — live test:
    // wood at base, host in the quarry) → into the QUEUE with retry, remove when our player approaches and
    // the chunk wakes (RetryPendingDestroys pattern).
    private class PendingBigPickup { public string id; public Vector2 pos; }
    private static readonly List<PendingBigPickup> _pendingBigPickups = new List<PendingBigPickup>();
    private const int PENDING_BIG_MAX = 64;

    public static void ApplyRemoteBigPickup(string id, float x, float y)
    {
        if (TryRemoveBigCopy(id, x, y))
        {
            Multiplayer.Log?.LogInfo($"[CHOP] 0x1B: copy '{id}' removed ✓");
            return;
        }
        var pos = new Vector2(x, y);
        if (_pendingBigPickups.Count < PENDING_BIG_MAX &&
            !_pendingBigPickups.Any(p => p.id == id && Vector2.Distance(p.pos, pos) < 1f))
        {
            _pendingBigPickups.Add(new PendingBigPickup { id = id, pos = pos });
            Multiplayer.Log?.LogInfo($"[CHOP] 0x1B: copy '{id}' @({x:F1},{y:F1}) not found — queued (zone not loaded?)");
        }
    }

    // From SteamManager.Update (retry timer, every 2s): zone finished loading → remove.
    public static void RetryPendingBigPickups()
    {
        for (int i = _pendingBigPickups.Count - 1; i >= 0; i--)
        {
            var p = _pendingBigPickups[i];
            if (TryRemoveBigCopy(p.id, p.pos.x, p.pos.y))
            {
                Multiplayer.Log?.LogInfo($"[CHOP] 0x1B retry: copy '{p.id}' removed ✓");
                _pendingBigPickups.RemoveAt(i);
            }
        }
    }

    private static bool TryRemoveBigCopy(string id, float x, float y)
    {
        try
        {
            DropResGameObject best = null;
            float bestD = 64f;
            foreach (var dr in UnityEngine.Object.FindObjectsOfType<DropResGameObject>())
            {
                if (dr == null || dr.res == null || dr.res.id != id) continue;
                bool tracked = false;
                for (int i = 0; i < _bodies.Count; i++)
                    if (ReferenceEquals(_bodies[i].go, dr)) { tracked = true; break; }
                if (tracked) continue;
                var dp = dr.transform.position;
                float d = Vector2.Distance(new Vector2(dp.x, dp.y), new Vector2(x, y));
                if (d < bestD) { bestD = d; best = dr; }
            }
            if (best == null) return false;
            CleanDestroyDrop(best);
            return true;
        }
        catch (Exception e)
        {
            Multiplayer.Log?.LogWarning($"[CHOP] TryRemoveBigCopy: {e.Message}");
            return false;
        }
    }

    // Receiver of 0x0F: move OUR corpse copy (by uid) to the broadcast position. Echo-guard:
    // mark lastRemoteTime so the tick won't send this move back to the carrier.
    public static void ApplyRemoteBodyPos(long uid, float x, float y)
    {
        var b = FindBodyByUid(uid);
        if (b == null || b.go == null)
        {
            Multiplayer.Log?.LogInfo($"[DIAG-BODY] 0x0F uid={uid} @({x:F1},{y:F1}) — no copy " +
                $"({(b == null ? "track missing" : "go destroyed")})");
            return;
        }
        var cur = b.go.transform.position;
        b.go.transform.position = new Vector3(x, y, cur.z);
        b.lastPos = new Vector2(x, y);
        b.lastRemoteTime = Time.time;
        Multiplayer.Log?.LogInfo($"[DIAG-BODY] 0x0F uid={uid} → copy moved @({x:F1},{y:F1})");
    }

    // Receiver of 0x10: the carrier placed/consumed the corpse → cleanly remove our copy (with the freshness indicator).
    public static void ApplyRemoteBodyRemove(long uid)
    {
        var b = FindBodyByUid(uid);
        if (b == null) return;
        ApplyingRemoteChop = true;
        try
        {
            if (b.go != null) CleanDestroyDrop(b.go);
            Multiplayer.Log?.LogInfo($"[CHOP] 0x10: corpse uid={uid} removed ✓ (partner placed/consumed)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] 0x10 removal failed: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
        _bodies.RemoveAll(t => t.uid == uid);
    }

    // Receiver of 0x14: the partner PICKED UP our mirror corpse (ownership transfer) → remove our ground
    // corpse (else a dupe: they carry it overhead while it lies on our ground). It returns as a NEW mirror
    // via 0x11 when the partner drops it (then they're the owner). Removal is the same as 0x10, but the
    // semantics differ: the corpse didn't vanish, it moved into the partner's hands.
    public static void ApplyRemoteCorpseTransfer(long uid)
    {
        var b = FindBodyByUid(uid);
        if (b == null) { Multiplayer.Log?.LogInfo($"[CHOP] 0x14 uid={uid}: track not found (already gone)"); return; }
        ApplyingRemoteChop = true;
        try { if (b.go != null) CleanDestroyDrop(b.go); }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] 0x14 removal failed: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
        _bodies.RemoveAll(t => t.uid == uid);
        Multiplayer.Log?.LogInfo($"[CHOP] 0x14: corpse uid={uid} transferred to partner (they picked it up) — ground copy removed ✓");
    }

    // ── PHASE 2: SPAWN PRIMITIVE (0x15) ─────────────────────────────────────────
    // The wobj about to be placed (captured in the DoPlace Prefix; cur_floating is nulled inside the method,
    // so it can't be read in the Postfix — hence the Prefix capture).
    private static MonoBehaviour _pendingBuildWobj;
    public static void SetPendingBuildWobj(MonoBehaviour wobj) { _pendingBuildWobj = wobj; }

    // Postfix DoPlace: placement HAPPENED if the current floating wobj is NO LONGER the one we captured
    // (it was left in the scene → cur_floating became null or a new one). If it's the same — DoPlace exited
    // early (not enough resources / spot taken) → send nothing.
    public static void OnBuildPlaceFinished(MonoBehaviour nowFloatingWobj)
    {
        var placed = _pendingBuildWobj;
        _pendingBuildWobj = null;
        if (placed == null || ReferenceEquals(placed, nowFloatingWobj)) return;
        OnLocalBuildPlaced(placed);
    }

    // Send: player placed a building/build site (BuildModeLogics.DoPlace hook) → broadcast {uid, pos, obj_id}
    // so the object appears on the partner with a SHARED uid. The shared uid is critical: later build stages
    // (ReplaceWithObject placeholder→building) sync via 0x0D by this uid (next step). Placeholder state is
    // default → no JSON sent yet (fresh object).
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
            TrackBuildUid(uid);   // THIS building's stages will sync via 0x0D (IsStateRepTarget predicate)
            Multiplayer.Log?.LogInfo($"[CHOP] Building placed uid={uid} obj={objId} @({pos.x:F1},{pos.y:F1}) → 0x15");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] OnLocalBuildPlaced: {e.Message}"); }
    }

    // Receiver of 0x15: spawn the object with the shared uid. If it already exists by uid (e.g. a repeat
    // packet or a stage) — don't duplicate. We force the uid DIRECTLY after spawn (reliable, no restore dependency).
    public static void ApplyRemoteBuildSpawn(long uid, float x, float y, float z, string objId)
    {
        EnsureReflection();
        if (_spawnWgoMethod == null || _worldRootProp == null || _uniqueIdField == null)
        {
            Multiplayer.Log?.LogWarning("[CHOP] 0x15: spawn-API not ready"); return;
        }
        if (FindWgoByUniqueId(uid) != null)
        {
            Multiplayer.Log?.LogInfo($"[CHOP] 0x15 uid={uid}: object already exists — spawn skipped");
            return;
        }
        ApplyingRemoteChop = true;
        try
        {
            var worldRoot = _worldRootProp.GetValue(null);
            object posObj = (UnityEngine.Vector3?)new UnityEngine.Vector3(x, y, z);
            var spawned = _spawnWgoMethod.Invoke(null, new object[] { worldRoot, objId, posObj }) as MonoBehaviour;
            if (spawned == null) { Multiplayer.Log?.LogWarning($"[CHOP] 0x15: SpawnWGO({objId}) = null"); return; }
            _uniqueIdField.SetValue(spawned, uid);   // FORCE the shared uid (overrides local UniqueID.GetUniqueID)
            TrackBuildUid(uid);   // we'll accept this building's stages via 0x0D
            Multiplayer.Log?.LogInfo($"[CHOP] 0x15: building obj={objId} uid={uid} spawned ✓ @({x:F1},{y:F1})");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] 0x15 spawn failed: {e.Message}"); }
        finally { ApplyingRemoteChop = false; }
    }

    // ── BUILDING DEMOLITION (0x16) — symmetric to spawn ────────────────────────
    // Send: a tracked building is destroyed locally (WGO.DestroyMe hook) → send {uid} so the partner removes
    // its copy. Gated on _syncedBuildUids (our buildings only) + Connected + !ApplyingRemoteChop (don't echo
    // the other's remove). uid is read BEFORE destruction (Prefix).
    public static void OnLocalBuildRemoved(MonoBehaviour wgo)
    {
        if (ApplyingRemoteChop || !Connected() || wgo == null || _uniqueIdField == null) return;
        if (_syncedBuildUids.Count == 0) return;
        try
        {
            long uid = Convert.ToInt64(_uniqueIdField.GetValue(wgo));
            if (!_syncedBuildUids.Contains(uid)) return;   // not our building — ignore
            _syncedBuildUids.Remove(uid);
            var packet = new byte[9];
            packet[0] = 0x16;
            BitConverter.GetBytes(uid).CopyTo(packet, 1);
            SteamManager.Instance?.SendPacket(SteamNetwork.RemoteID, packet);
            Multiplayer.Log?.LogInfo($"[CHOP] Building uid={uid} demolished locally → 0x16");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] OnLocalBuildRemoved: {e.Message}"); }
    }

    // Receiver of 0x16: the partner demolished a building → destroy our copy by uid (under the echo-guard).
    public static void ApplyRemoteBuildRemove(long uid)
    {
        EnsureReflection();
        _syncedBuildUids.Remove(uid);
        var target = FindWgoByUniqueId(uid);
        if (target == null) { Multiplayer.Log?.LogInfo($"[CHOP] 0x16 uid={uid}: object not found (already gone)"); return; }
        if (_destroyMeMethod == null) { Multiplayer.Log?.LogWarning("[CHOP] 0x16: DestroyMe-API not ready"); return; }
        ApplyingRemoteChop = true;
        try
        {
            _destroyMeMethod.Invoke(target, null);
            Multiplayer.Log?.LogInfo($"[CHOP] 0x16: building uid={uid} demolished ✓ (partner removed it)");
        }
        catch (Exception e) { Multiplayer.Log?.LogError($"[CHOP] 0x16 demolition failed: {e.Message}"); }
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
                Multiplayer.Log?.LogInfo($"[CHOP] Deferred loot @({d.Pos.x:F1},{d.Pos.y:F1}) ✓");
                InvokeDropItems(target, d.Items, d.Direction);
                _pendingDrops.RemoveAt(i);
            }
        }
    }

    // Cache of the all-WGO scan. FindObjectsOfType is very expensive (iterates the whole scene), and on a
    // packet burst (e.g. a friend digging a whole cemetery → 35 0x0D packets) a scan-per-packet caused heavy
    // lag (live test 2026-06-07). We cache the result for ~1s: a burst shares one scan instead of dozens.
    // References stay valid through a transformation (ReplaceWithObject REUSES the same WGO, doesn't destroy
    // it), and destroyed objects are caught by the null check below.
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

    // Nearest sync object to point (x,y) among active WGOs. The predicate sets the target type:
    // IsDestroySyncTarget for 0x09/0x0A, IsLootSyncTarget for 0x0B (wider — allows veins too),
    // IsGraveTarget for 0x0D.
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


