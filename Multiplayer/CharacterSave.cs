using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

// ─────────────────────────────────────────────────────────────────────────────
// CLIENT CHARACTER SAVE (M1, 2026-06-19) — client's PERSONAL character layer over the host's SHARED
// world (Stardew model). M1: inventory + money + stat ceilings (max_hp/energy/sanity) + toolbar
// (equipped_items). Knowledge/story = M2/M3.
//   • Live inventory = MainGame.me.player.data (PrepareForSave sets _inventory=player.data, decomp 104876),
//     so we use live player.data after spawn, not save._inventory. Serialize Item.ToJSON(0); restore via
//     JsonUtility.FromJsonOverwrite.
//   • ⚠️ WORLD FLAGS (lock_tp/church_level/… = StorySync.IsWorldParam) live in the SAME Item — overwrite
//     would clobber them with the client's stale values, so we snapshot the host's and restore them AFTER.
//   • Per-host file coop_character_{RemoteID}.dat — character tied to host's world; first join (no file) =
//     stays a clone of the host, saved as the client's baseline.
// ─────────────────────────────────────────────────────────────────────────────
public static class ClientCharacterStore
{
    private const string FILE_VERSION = "GKCOOP-CHAR-3";   // V3 = + story layer (M3a: quests + known_npcs)
    private const string FILE_VERSION_V2 = "GKCOOP-CHAR-2"; // V2 = + knowledge layer (M2); read for compatibility
    private const string FILE_VERSION_V1 = "GKCOOP-CHAR-1"; // V1 = inventory/money/stats only (M1); read for compatibility
    private static readonly BindingFlags F =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // M2: personal KNOWLEDGE layer (GameSave 104544-574). All List<string> EXCEPT unlocked_tech_branches
    // (List<int>). The game reads them live (IsCraftUnlocked=unlocked_crafts.Contains, …), so copying the
    // lists is enough — WITHOUT replaying ApplyTech/UnlockPerk (perk stat bonuses already live in
    // player.data._params via the M1 overlay; re-applying would double them).
    // known_world_zones DELIBERATELY ABSENT: map/zones are the SHARED world (Denis 2026-06-20).
    private static readonly string[] _knowledgeFields =
    {
        "unlocked_techs", "unlocked_crafts", "locked_crafts", "unlocked_works",
        "unlocked_phrases", "unlocked_perks", "black_list_of_phrases", "completed_one_time_crafts",
        "known_fishes", "known_fishes_clear", "last_bait_reservoirs",
        "last_bait_baits", "revealed_techs", "visible_techs", "unlocked_tech_branches",
    };
    private const string INT_LIST_FIELD = "unlocked_tech_branches";
    private const char LIST_SEP = '\x1f';  // between fields (unit separator — never appears in ids/names)
    private const char ITEM_SEP = '\x1e';  // between list elements (record separator)

    // Serialize the knowledge layer into one line: "field=a\x1eb\x1ec\x1ffield2=…". Order-independent
    // and tolerant of missing fields (forward/backward compatible).
    private static string EncodeKnowledge(object save)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _knowledgeFields.Length; i++)
        {
            if (i > 0) sb.Append(LIST_SEP);
            sb.Append(_knowledgeFields[i]).Append('=');
            var list = save.GetType().GetField(_knowledgeFields[i], F)?.GetValue(save) as System.Collections.IEnumerable;
            if (list != null)
            {
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(ITEM_SEP);
                    sb.Append(item);
                    first = false;
                }
            }
        }
        return sb.ToString();
    }

    // Apply the saved knowledge layer onto save: for each field, fully replace the list with the client's.
    private static int ApplyKnowledge(object save, string encoded)
    {
        int applied = 0;
        foreach (var group in encoded.Split(LIST_SEP))
        {
            int eq = group.IndexOf('=');
            if (eq < 0) continue;
            var name = group.Substring(0, eq);
            var rest = group.Substring(eq + 1);
            var field = save.GetType().GetField(name, F);
            if (field?.GetValue(save) is not System.Collections.IList list) continue;
            list.Clear();
            if (rest.Length > 0)
            {
                bool isInt = name == INT_LIST_FIELD;
                foreach (var s in rest.Split(ITEM_SEP))
                {
                    if (isInt) { if (int.TryParse(s, out var n)) list.Add(n); }
                    else list.Add(s);
                }
            }
            applied++;
        }
        return applied;
    }

    // ── M3a: PERSONAL STORY LAYER (quests + known_npcs) ───────────────────────────
    // Stardew Phase B: world shared, relationships/quests personal. Personal story PARAMS
    // (met_donkey/goto_tavern…) live in player.data._params, already carried by M1. Here we handle the two
    // GameSave objects OUTSIDE player.data (QuestSystem 103990, KnownNPCList 106951):
    //   • known_npcs (KnownNPCList) — flat [Serializable] → JsonUtility.
    //   • quests (QuestSystem) — 3 string lists + _currnet_quests holding a QuestDefinition (FlowCanvas
    //     triggers JsonUtility can't restore) → we store only {id, start_time, state} and REHYDRATE the
    //     definition from GameBalance.GetDataOrNull on overlay (as StorySync does).
    private const char GROUP_SEP = '\x1d';  // between the 4 quest-block groups (failed/succed/executed/current)
    private const char FIELD_SEP = '\x1c';  // between fields of an active-quest record (id/start_time/state)

    private static System.Collections.IList PrivList(object obj, string field)
        => obj?.GetType().GetField(field, F)?.GetValue(obj) as System.Collections.IList;

    private static string JoinItems(System.Collections.IList list)
    {
        if (list == null) return "";
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (var x in list) { if (!first) sb.Append(ITEM_SEP); sb.Append(x); first = false; }
        return sb.ToString();
    }

    private static object StaticMe(string typeName)
    {
        var t = AccessTools.TypeByName(typeName);
        if (t == null) return null;
        var bf = BindingFlags.Public | BindingFlags.Static;
        return t.GetProperty("me", bf)?.GetValue(null) ?? t.GetField("me", bf)?.GetValue(null);
    }

    // Serialize the story layer into one line (\n-free): "<questsBlock>\x1f<knownNpcsJson>".
    // questsBlock = failed \x1d succed \x1d executed \x1d current; current = records (\x1e), record = id\x1ctime\x1cstate.
    private static string EncodeStory(object save)
    {
        var sb = new System.Text.StringBuilder();
        var quests = save.GetType().GetField("quests", F)?.GetValue(save);
        sb.Append(JoinItems(PrivList(quests, "_failed_quests"))).Append(GROUP_SEP);
        sb.Append(JoinItems(PrivList(quests, "_succed_quests"))).Append(GROUP_SEP);
        sb.Append(JoinItems(PrivList(quests, "_executed_quests"))).Append(GROUP_SEP);
        var current = PrivList(quests, "_currnet_quests");
        if (current != null)
        {
            bool first = true;
            foreach (var qs in current)
            {
                if (qs == null) continue;
                var def = qs.GetType().GetField("definition", F)?.GetValue(qs);
                if (!(def?.GetType().GetField("id", F)?.GetValue(def) is string id) || id.Length == 0) continue;
                var st = qs.GetType().GetField("start_time", F)?.GetValue(qs);
                var state = qs.GetType().GetField("state", F)?.GetValue(qs);
                if (!first) sb.Append(ITEM_SEP);
                sb.Append(id).Append(FIELD_SEP).Append(Convert.ToInt64(st)).Append(FIELD_SEP).Append(Convert.ToInt32(state));
                first = false;
            }
        }

        var npcs = save.GetType().GetField("known_npcs", F)?.GetValue(save);
        var toJson = AccessTools.TypeByName("UnityEngine.JsonUtility")?.GetMethod("ToJson", new[] { typeof(object) });
        var npcsJson = (npcs != null && toJson != null) ? (string)toJson.Invoke(null, new object[] { npcs }) : "{}";
        return sb.ToString() + LIST_SEP + npcsJson;
    }

    // Apply the story layer onto save: quest lists + active-quest rehydration + known_npcs.
    // Returns (active quest count, NPC count) for logging. The journal UI is refreshed at the end.
    private static (int quests, int npcs) ApplyStory(object save, string encoded)
    {
        int sep = encoded.IndexOf(LIST_SEP);
        var questsBlock = sep >= 0 ? encoded.Substring(0, sep) : encoded;
        var npcsJson    = sep >= 0 ? encoded.Substring(sep + 1) : null;

        int curApplied = 0, npcApplied = 0;
        var quests = save.GetType().GetField("quests", F)?.GetValue(save);
        if (quests != null)
        {
            var groups = questsBlock.Split(GROUP_SEP);
            if (groups.Length >= 4)
            {
                ReplaceStrList(quests, "_failed_quests",   groups[0]);
                ReplaceStrList(quests, "_succed_quests",   groups[1]);
                ReplaceStrList(quests, "_executed_quests", groups[2]);
                curApplied = RebuildCurrentQuests(quests, groups[3]);
            }
        }

        if (npcsJson != null && npcsJson.Length > 0)
        {
            var npcs = save.GetType().GetField("known_npcs", F)?.GetValue(save);
            var fromJsonOverwrite = AccessTools.TypeByName("UnityEngine.JsonUtility")
                ?.GetMethod("FromJsonOverwrite", new[] { typeof(string), typeof(object) });
            if (npcs != null && fromJsonOverwrite != null)
            {
                fromJsonOverwrite.Invoke(null, new object[] { npcsJson, npcs });
                npcApplied = (PrivList(npcs, "npcs"))?.Count ?? 0;
            }
        }

        RedrawQuestList();
        return (curApplied, npcApplied);
    }

    private static void ReplaceStrList(object obj, string field, string joined)
    {
        if (PrivList(obj, field) is not System.Collections.IList list) return;
        list.Clear();
        if (joined.Length > 0) foreach (var s in joined.Split(ITEM_SEP)) list.Add(s);
    }

    // Rebuild _currnet_quests: for each record id\x1ctime\x1cstate create a QuestState, rehydrating
    // the definition from GameBalance (an unknown id = different version/DLC → skip, no crash).
    private static int RebuildCurrentQuests(object quests, string recordsBlob)
    {
        if (PrivList(quests, "_currnet_quests") is not System.Collections.IList list) return 0;
        list.Clear();
        if (recordsBlob.Length == 0) return 0;

        var gbMe = StaticMe("GameBalance");
        var qdefType = AccessTools.TypeByName("QuestDefinition");
        var getDataOrNull = gbMe?.GetType().GetMethod("GetDataOrNull")?.MakeGenericMethod(qdefType);
        var qstateType = AccessTools.TypeByName("QuestState");
        if (gbMe == null || getDataOrNull == null || qstateType == null)
        {
            Multiplayer.Log.LogWarning("[CHAR] RebuildCurrentQuests: GameBalance/QuestState not found — active quests skipped");
            return 0;
        }

        int n = 0;
        foreach (var rec in recordsBlob.Split(ITEM_SEP))
        {
            var f = rec.Split(FIELD_SEP);
            if (f.Length < 3) continue;
            var def = getDataOrNull.Invoke(gbMe, new object[] { f[0] });
            if (def == null) continue;   // unknown quest — skip
            var qs = System.Activator.CreateInstance(qstateType);
            qstateType.GetField("definition", F)?.SetValue(qs, def);
            if (long.TryParse(f[1], out var st)) qstateType.GetField("start_time", F)?.SetValue(qs, st);
            var stateField = qstateType.GetField("state", F);
            if (stateField != null && int.TryParse(f[2], out var state))
                stateField.SetValue(qs, System.Enum.ToObject(stateField.FieldType, state));
            list.Add(qs);
            n++;
        }
        return n;
    }

    private static void RedrawQuestList()
    {
        try
        {
            var guiMe = StaticMe("GUIElements");
            var ql = guiMe?.GetType().GetField("quest_list", BindingFlags.Public | BindingFlags.Instance)?.GetValue(guiMe);
            ql?.GetType().GetMethod("Redraw", BindingFlags.Public | BindingFlags.Instance, null, System.Type.EmptyTypes, null)
               ?.Invoke(ql, null);
        }
        catch (Exception e) { Multiplayer.Log.LogWarning($"[CHAR] RedrawQuestList: {e.Message}"); }
    }

    // Guard for one overlay per join (reset on re-join): lets the early ASAP call beat the late
    // UnlockPlayerAfterLoad fallback so we never apply twice.
    public static bool OverlayDone;

    // Stable per-world key: dungeon_seed (assigned once at CreateNewSave 104842, never mutated; travels
    // in the host's raw save bytes → host's WORLD seed). -1 = save not ready → FilePath uses the old key.
    private static int WorldSeed()
    {
        GetLive(out var save, out _);
        if (save == null) return -1;
        try { return (int)save.GetType().GetField("dungeon_seed", F).GetValue(save); }
        catch { return -1; }
    }

    // The old key (host only, no world) — for the one-time auto-migration.
    private static string LegacyFilePath() =>
        System.IO.Path.Combine(Application.persistentDataPath, $"coop_character_{SteamNetwork.RemoteID}.dat");

    private static string FilePath()
    {
        int seed = WorldSeed();
        var name = seed >= 0
            ? $"coop_character_{SteamNetwork.RemoteID}_{seed}.dat"
            : $"coop_character_{SteamNetwork.RemoteID}.dat";   // fallback: save not ready
        return System.IO.Path.Combine(Application.persistentDataPath, name);
    }

    // One-time auto-migration: the old file was named by host only (no world key) → the character "leaked"
    // between a host's worlds. Key is now +dungeon_seed. If this world's file is missing but the legacy one
    // exists, rename it → the existing character goes to the FIRST world joined after the update; others get
    // a fresh clone. Called from Overlay() (save ready, before HasSaved).
    private static void MigrateLegacyFile()
    {
        try
        {
            var legacy  = LegacyFilePath();
            var current = FilePath();
            if (legacy == current) return;               // seed=-1 fallback → keys coincide
            if (System.IO.File.Exists(current)) return;  // new one already exists — leave it
            if (!System.IO.File.Exists(legacy)) return;  // nothing to migrate
            System.IO.File.Move(legacy, current);
            Multiplayer.Log.LogInfo($"[CHAR] Character auto-migration: {System.IO.Path.GetFileName(legacy)} → {System.IO.Path.GetFileName(current)}");
        }
        catch (Exception e) { Multiplayer.Log.LogWarning($"[CHAR] Auto-migration skipped: {e.Message}"); }
    }

    public static bool HasSaved()
    {
        try { return System.IO.File.Exists(FilePath()); } catch { return false; }
    }

    // Current JSON of the live inventory (ToJSON(0)), or null if player.data isn't ready. For ASAP polling:
    // changing length = game still filling inventory; stable length = safe to apply the overlay.
    public static string LivePlayerJson()
    {
        GetLive(out var save, out var pdata);
        if (save == null || pdata == null) return null;
        try
        {
            var toJson = pdata.GetType().GetMethod("ToJSON", new[] { typeof(int) });
            return (string)toJson.Invoke(pdata, new object[] { 0 });
        }
        catch { return null; }
    }

    // (save, playerData) of the live world, or (null,null) if not ready yet.
    private static void GetLive(out object save, out object playerData)
    {
        save = null; playerData = null;
        var mg = AccessTools.TypeByName("MainGame");
        var me = mg?.GetField("me", F)?.GetValue(null);
        if (me == null) return;
        save = mg.GetField("save", F)?.GetValue(me);
        var player = mg.GetField("player", F)?.GetValue(me);
        if (player != null)
            playerData = player.GetType().GetProperty("data", F)?.GetValue(player);
    }

    // ── SAVE ─────────────────────────────────────────────────────────────────────
    // Called on exit (to menu / quit), while the player is still in the world. Idempotent
    // (overwrites the file). Gate: client only, only when actually in-game.
    public static void Save()
    {
        try
        {
            if (SteamNetwork.Role != NetworkRole.Client || !SteamNetwork.IsInGame) return;
            GetLive(out var save, out var pdata);
            if (save == null || pdata == null)
            {
                Multiplayer.Log.LogWarning("[CHAR] Save: save/player still null — skipping");
                return;
            }

            // Ordering guard: if this join hasn't overlaid yet (e.g. quit <1s after joining) and an
            // un-migrated legacy file exists — do NOT overwrite it with a host clone (would orphan the legacy
            // character). Skip: the next join's overlay migrates it.
            if (!OverlayDone && System.IO.File.Exists(LegacyFilePath()) && !System.IO.File.Exists(FilePath()))
            {
                Multiplayer.Log.LogInfo("[CHAR] Save before overlay with a legacy file present — skipping (so migration isn't lost)");
                return;
            }

            var toJson = pdata.GetType().GetMethod("ToJSON", new[] { typeof(int) });
            var invJson = (string)toJson.Invoke(pdata, new object[] { 0 });

            int maxHp  = (int)save.GetType().GetField("max_hp",     F).GetValue(save);
            int maxEn  = (int)save.GetType().GetField("max_energy", F).GetValue(save);
            int maxSan = (int)save.GetType().GetField("max_sanity", F).GetValue(save);

            var equipped = save.GetType().GetField("equipped_items", F)?.GetValue(save) as string[];
            var eq = new string[4];
            for (int i = 0; i < 4; i++) eq[i] = (equipped != null && i < equipped.Length && equipped[i] != null) ? equipped[i] : "";

            var knowledge = EncodeKnowledge(save);
            var story = EncodeStory(save);

            // V3 format: 6 sections separated by \n (json last → split limit 6 keeps it whole).
            // Knowledge/story/json contain no \n (Encode* use \x1c..\x1f + JsonUtility single-line; ToJSON too).
            var text = FILE_VERSION + "\n"
                     + $"{maxHp} {maxEn} {maxSan}" + "\n"
                     + string.Join("\t", eq) + "\n"
                     + knowledge + "\n"
                     + story + "\n"
                     + invJson;
            System.IO.File.WriteAllText(FilePath(), text);
            Multiplayer.Log.LogInfo($"[CHAR] Character saved → {FilePath()} (maxHp={maxHp} json={invJson.Length}B knowledge={knowledge.Length}B story={story.Length}B)");
        }
        catch (Exception e) { Multiplayer.Log.LogError($"[CHAR] Save error: {e.Message}"); }
    }

    // ── OVERLAY ──────────────────────────────────────────────────────────────────
    // Called after the client spawns (UnlockPlayerAfterLoad): applies the saved character onto live
    // player.data, preserving the host's world flags. First join (no file) = stays a clone, save baseline.
    public static void Overlay()
    {
        try
        {
            if (SteamNetwork.Role != NetworkRole.Client) return;
            if (OverlayDone) return;   // already applied this join (the early ASAP beat us to it)
            GetLive(out var save, out var pdata);
            if (save == null || pdata == null)
            {
                Multiplayer.Log.LogWarning("[CHAR] Overlay: save/player still null — skipping");
                return;
            }

            MigrateLegacyFile();   // move the pre-fix file (no world key) onto this world, if any
            if (!HasSaved())
            {
                Multiplayer.Log.LogInfo("[CHAR] First join — character = host clone, saving the client's baseline");
                Save();
                OverlayDone = true;
                return;
            }

            var parts = System.IO.File.ReadAllText(FilePath()).Split(new[] { '\n' }, 6);
            var ver = parts.Length > 0 ? parts[0].Trim() : "";
            string knowledge = null, story = null, invJson;
            if (ver == FILE_VERSION && parts.Length >= 6)         // V3: + story layer
            {
                knowledge = parts[3];
                story = parts[4];
                invJson = parts[5];
            }
            else if (ver == FILE_VERSION_V2 && parts.Length >= 5) // V2: + knowledge layer (no story) — compatibility
            {
                knowledge = parts[3];
                invJson = parts[4];
            }
            else if (ver == FILE_VERSION_V1 && parts.Length >= 4) // V1: M1 only (no knowledge/story) — compatibility
            {
                invJson = parts[3];
            }
            else
            {
                Multiplayer.Log.LogWarning("[CHAR] Character file incompatible/corrupt — overlay skipped");
                return;
            }
            var statToks = parts[1].Split(' ');
            int maxHp = int.Parse(statToks[0]), maxEn = int.Parse(statToks[1]), maxSan = int.Parse(statToks[2]);
            var equipped = parts[2].Split('\t');

            // 1) Snapshot the host's WORLD flags (before the overwrite).
            var worldSnap = SnapshotWorldParams(pdata);

            // 2) Overwrite the live inventory with the client's (items + money + params + hp).
            // JsonUtility is in a module the project doesn't reference (like the rest of the code) → reflection.
            var fromJsonOverwrite = AccessTools.TypeByName("UnityEngine.JsonUtility")
                ?.GetMethod("FromJsonOverwrite", new[] { typeof(string), typeof(object) });
            if (fromJsonOverwrite == null)
            {
                Multiplayer.Log.LogError("[CHAR] JsonUtility.FromJsonOverwrite missing — overlay aborted");
                return;
            }
            fromJsonOverwrite.Invoke(null, new object[] { invJson, pdata });

            // 3) Restore the host's world flags (so gates/progression don't roll back).
            var setParam = pdata.GetType().GetMethod("SetParam", new[] { typeof(string), typeof(float) });
            if (setParam != null)
                foreach (var kv in worldSnap) setParam.Invoke(pdata, new object[] { kv.Key, kv.Value });

            // 4) Stat ceilings + toolbar = the client's.
            save.GetType().GetField("max_hp",     F).SetValue(save, maxHp);
            save.GetType().GetField("max_energy", F).SetValue(save, maxEn);
            save.GetType().GetField("max_sanity", F).SetValue(save, maxSan);
            if (equipped.Length > 0)
            {
                var eq = new string[4];
                for (int i = 0; i < 4; i++) eq[i] = i < equipped.Length ? equipped[i] : "";
                save.GetType().GetField("equipped_items", F)?.SetValue(save, eq);
            }

            // 5) M2: KNOWLEDGE layer = the client's. List copy only — NO ApplyTech (perk bonuses already in
            // player.data via step 2; a replay would double them).
            int knownApplied = knowledge != null ? ApplyKnowledge(save, knowledge) : 0;

            // 6) M3a: story layer = the client's. Active quests rehydrate their definition from GameBalance;
            // journal refreshed (RedrawQuestList).
            var (curQ, nNpc) = story != null ? ApplyStory(save, story) : (0, 0);

            OverlayDone = true;
            RefreshHud();
            Multiplayer.Log.LogInfo($"[CHAR] Character applied ✓ (maxHp={maxHp} worldFlags={worldSnap.Count} knowledge={knownApplied} activeQuests={curQ} NPC={nNpc} json={invJson.Length}B)");
        }
        catch (Exception e) { Multiplayer.Log.LogError($"[CHAR] Overlay error: {e.Message}\n{e.StackTrace}"); }
    }

    // Redraw the always-visible HUD hotbar after the overlay — FromJsonOverwrite changes data directly
    // without the "inventory changed" event, so the HUD shows host counters until the player acts.
    // Target = GUIElements.me.hud.toolbar (ToolbarGUI 66829). ⚠️ InventoryGUI.toolbelt is a DIFFERENT
    // component (inside the inventory screen) — redrawing it did NOT fix the visible HUD hotbar.
    public static void RefreshHud()
    {
        try
        {
            var guiType = AccessTools.TypeByName("GUIElements");
            var me = guiType?.GetProperty("me", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (me == null) return;
            var hud = me.GetType().GetField("hud", BindingFlags.Public | BindingFlags.Instance)?.GetValue(me);
            var toolbar = hud?.GetType().GetField("toolbar", BindingFlags.Public | BindingFlags.Instance)?.GetValue(hud);
            toolbar?.GetType()
                .GetMethod("Redraw", BindingFlags.Public | BindingFlags.Instance, null, System.Type.EmptyTypes, null)
                ?.Invoke(toolbar, null);
        }
        catch (Exception e) { Multiplayer.Log.LogWarning($"[CHAR] RefreshHud: {e.Message}"); }
    }

    // Param names = Item._params (GameRes).Types; we pick the world ones (StorySync.IsWorldParam).
    private static Dictionary<string, float> SnapshotWorldParams(object pdata)
    {
        var d = new Dictionary<string, float>();
        try
        {
            var paramsObj = pdata.GetType().GetField("_params", F)?.GetValue(pdata);
            var types = paramsObj?.GetType().GetProperty("Types", F)?.GetValue(paramsObj) as System.Collections.IEnumerable;
            var getParam = pdata.GetType().GetMethod("GetParam", new[] { typeof(string), typeof(float) });
            if (types == null || getParam == null) return d;
            // Copy the names into a list (SetParam during restore won't mutate the current iteration).
            var names = new List<string>();
            foreach (var n in types) if (n is string s) names.Add(s);
            foreach (var name in names)
                if (StorySync.IsWorldParam(name))
                    d[name] = (float)getParam.Invoke(pdata, new object[] { name, 0f });
        }
        catch (Exception e) { Multiplayer.Log.LogWarning($"[CHAR] SnapshotWorldParams: {e.Message}"); }
        return d;
    }
}
