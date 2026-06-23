using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

// WEATHER (0x1A): brackets around the daily-schedule generator SmartWeatherEngine.UpdateWeather
// (37129) — Prefix turns the collector on, Postfix sends the gathered presets (host only, gated inside).
[HarmonyPatch]
public static class WeatherGenBracketPatch
{
    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("SmartWeatherEngine");
        return t?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "UpdateWeather" && m.GetParameters().Length == 0);
    }

    // When connected, the CLIENT does NOT roll weather itself (host-authoritative 0x1A): its own midnight
    // roll polluted the host's schedule (live test 2026-06-12, day-boundary races). Fail-open: solo rolls like vanilla.
    static bool Prefix()
    {
        if (SteamNetwork.Role == NetworkRole.Client && ChopSync.IsPeerConnected())
        {
            Multiplayer.Log?.LogInfo("[CHOP] Weather roll on the client skipped (waiting for the host's 0x1A) ✓");
            return false;
        }
        ChopSync.OnWeatherGenStart();
        return true;
    }
    static void Postfix() { ChopSync.OnWeatherGenEnd(); }

    static Exception Finalizer(Exception __exception) => null;
}

// WEATHER (0x1A): every preset roll (SmartWeatherSettings.GetWeatherPreset, 37200) during
// generation → into the collector (fixed order: night/morning/day/evening).
[HarmonyPatch]
public static class WeatherPresetChosenPatch
{
    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("SmartWeatherSettings");
        return t?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "GetWeatherPreset" && m.GetParameters().Length == 0);
    }

    static void Postfix(object __result)
    {
        ChopSync.OnWeatherPresetChosen(__result);
    }

    static Exception Finalizer(Exception __exception) => null;
}
