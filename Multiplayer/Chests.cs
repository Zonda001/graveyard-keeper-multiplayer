using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

// PHASE 3 (shared chests): opening a chest → track its uid (narrow, only chests opened this session).
// ChestGUI.Open(WorldGameObject) — decompiled 45002; the argument binds by the name chest_obj.
[HarmonyPatch]
public static class ChestOpenTrackPatch
{
    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("ChestGUI");
        return t?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Open" && m.GetParameters().Length == 1);
    }

    static void Postfix(object chest_obj)
    {
        ChopSync.TrackChestWgo(chest_obj);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// PHASE 3 (shared chests): closing the GUI → a full 0x0D state as reconciliation (an idempotent
// snapshot; picks up ops lost by a far-away partner). Quiesce inside OnLocalChestClosed.
[HarmonyPatch]
public static class ChestCloseSnapshotPatch
{
    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("ChestGUI");
        return t?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "Hide");
    }

    static void Postfix(object __instance)
    {
        ChopSync.OnLocalChestClosed(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}

// PHASE 3 (shared chests, CONCURRENT): item moves go through ChestGUI.MoveItem (5 args, decomp 45167).
// Prefix snapshots "before" (non-blocking), Postfix diffs → 0x19 ops (±N). Both players edit at once.
[HarmonyPatch]
public static class ChestMoveSyncPatch
{
    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("ChestGUI");
        return t?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "MoveItem" && m.GetParameters().Length == 5);
    }

    static void Prefix(object __instance)
    {
        ChopSync.CaptureChestBeforeMove(__instance);
    }

    static void Postfix(object __instance)
    {
        ChopSync.OnLocalChestChanged(__instance);
    }

    static Exception Finalizer(Exception __exception) => null;
}
