using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

// COOP CRAFT Stage 1: EnqueueCraft → send the friend the updated craft_queue (0x17), so they see the
// same station windows. __instance = CraftComponent. Dedup/gate inside SendCraftQueue.
[HarmonyPatch]
public static class CraftEnqueueSyncPatch
{
    static MethodBase TargetMethod()
    {
        var cc = AccessTools.TypeByName("CraftComponent");
        return cc?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "EnqueueCraft");
    }

    // __instance as object: CraftComponent isn't a MonoBehaviour (plain class) — a typed param = dead hook.
    static void Postfix(object __instance)
    {
        ChopSync.OnLocalCraftQueueChanged(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// STAGE 3a (arbitration): block queue starts on a station occupied by the friend. Separate from
// CraftReally because TryStartCraftFromQueue does --n BEFORE calling it (decomp 83782) — blocking lower
// would eat the queue. Prefix=false skips the original (queue untouched, items start in owner via 0x17).
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

// STAGE 3a (arbitration): CraftReally — the single funnel for ALL craft starts (GUI 83818, queue 83786;
// materials deducted inside). Prefix blocks on an occupied station before deduction; Postfix sends claim 0x18.
[HarmonyPatch]
public static class CraftReallyArbiterPatch
{
    static MethodBase TargetMethod()
    {
        var cc = AccessTools.TypeByName("CraftComponent");
        return cc?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "CraftReally");
    }

    // craft — CraftReally's first arg (bound by name): second layer of remove-protection
    // (a remove-craft isn't blocked even if the is_removing reflection didn't bind).
    static bool Prefix(object __instance, object craft, ref bool __result)
    {
        if (ChopSync.ShouldBlockLocalCraftStart(__instance, craft))
        {
            __result = false;   // the game sees "start failed" — like a shortage of materials
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

// STAGE 3a (UX): honest message instead of "not enough resources". After our block the game shows a
// misleading shortage bubble (88292 → ShowCustomNeedBubble("not_enough_resources")). We intercept the TEXT
// in ShowCustomNeedBubble (BaseCharacterComponent 81495) and, if a block happened within 1s, swap it for
// "occupied by partner". Text avoids UA-only glyphs (і/ї/є/ґ) — the font only guarantees RU Cyrillic.
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

// QUEUE BUGFIX (test 3b): GUI queue edits change craft_queue directly, without EnqueueCraft → no hook →
// friend saw a stale icon. Deletion goes through CraftQueueGUI.OnDeleteItemPressed (48434, _craft_component
// on the GUI) — Postfix sends a fresh queue.
[HarmonyPatch]
public static class CraftQueueDeleteSyncPatch
{
    static FieldInfo _ccField;

    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("CraftQueueGUI");
        _ccField = t?.GetField("_craft_component", BindingFlags.NonPublic | BindingFlags.Instance);
        return t?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "OnDeleteItemPressed");
    }

    static void Postfix(object __instance)
    {
        if (_ccField != null) ChopSync.OnLocalCraftQueueChanged(_ccField.GetValue(__instance));
    }

    static Exception Finalizer(Exception __exception) => null;
}

// QUEUE BUGFIX (test 3b): the ±/∞ buttons on a queue item (CraftQueueItemGUI 48644/48658/48670)
// mutate _ci.n/_ci.infinite directly. The station's WGO is the private field _craftery_wgo on the GUI item.
[HarmonyPatch]
public static class CraftQueueButtonsSyncPatch
{
    static FieldInfo _wgoField;

    static IEnumerable<MethodBase> TargetMethods()
    {
        var t = AccessTools.TypeByName("CraftQueueItemGUI");
        if (t == null) yield break;
        _wgoField = t.GetField("_craftery_wgo", BindingFlags.NonPublic | BindingFlags.Instance);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var name in new[] { "OnIncreasePressed", "OnDecreasePressed", "OnInfinityButtonPressed" })
        {
            var m = t.GetMethods(flags).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == 0);
            if (m != null) yield return m;
        }
    }

    static void Postfix(object __instance)
    {
        var wgo = _wgoField?.GetValue(__instance) as MonoBehaviour;
        if (wgo != null) ChopSync.SendCraftQueue(wgo);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// QUEUE BUGFIX (test 3b): cancelling a craft from the dialog (craft_queue.Clear()+Cancel(), 48233) —
// also bypasses EnqueueCraft. Postfix CraftComponent.Cancel → fresh queue + immediate claim release.
[HarmonyPatch]
public static class CraftCancelSyncPatch
{
    static MethodBase TargetMethod()
    {
        var cc = AccessTools.TypeByName("CraftComponent");
        return cc?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                              BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "Cancel" && m.GetParameters().Length == 0);
    }

    static void Postfix(object __instance)
    {
        ChopSync.OnLocalCraftCancelled(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// STAGE 3b: owner's progress → friend. CraftComponent.DoAction ticks every work step (player/zombie/auto,
// 83157) — Postfix sends 0x18 flag=2 on 10% steps (deduped inside OnLocalCraftProgressTick, gated by _localCrafting).
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

// STAGE 3b: native RefreshComponentBubbleData wipes the progress bar when is_crafting=false (84633) —
// always false for the friend. Postfix restores the bar under an active claim (gated in ReinjectRemoteProgressBar).
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
