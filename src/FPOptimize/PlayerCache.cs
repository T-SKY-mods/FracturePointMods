using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using FPSController = Il2CppDemo.Scripts.Runtime.Character.FPSController;
using Object = UnityEngine.Object;

namespace FPOptimize
{
    // ゲームは Unity の FindAnyObjectByType で、シーン全体から「1 つしかない物」を何度も探し直している。1 回 約 8ms。
    //   ・プレイヤー（FPSController）：敵が攻撃状態に入るたび・地面の物のツールチップを作るたび（毎フレーム）
    //   ・敵が倒れたとき：QuestManager・PlayerHealthManager（2026-11-23）
    //   ・落とし物が出るたび（LootableItem.Start）：Inventory と InventoryUI を 1 個ごとに 2 回
    //   ・エンドレスの敵の波：DifficultyStopwatch を数秒おき
    //   → 敵が何体も倒れて物を落とす瞬間に 1 フレーム 30〜50ms 止まる原因（2026-11-23 の調査）。
    //
    // シーン内にその物がちょうど 1 つのときは、探しても必ず同じものが返るので、見つけたものを覚えておいて返す。
    //   ・数え直す：シーンが変わったとき・エンドレスの階が変わったとき・その種類の物が新しく作られたとき（Start）・覚えた物が壊れたか非表示になったとき
    //   ・0 個なら、元の検索と同じく「無し」を返す。2 個以上なら、次に数え直すまで元の検索のまま
    //   ・プレイヤー以外は「表示中の物だけ」の検索（Exclude）だけを肩代わりする（非表示も含む検索は元のまま）
    public static class PlayerCache
    {
        sealed class Slot
        {
            public string Name; public IntPtr TypePtr; public bool Include;   // Include：非表示も含む検索も肩代わりする（プレイヤーだけ）
            public Object Cached; public bool Dirty = true, PassThrough;
            public long Hits, Fills, Mismatch;
        }
        static readonly Dictionary<IntPtr, Slot> _slots = new();
        static bool _active, _verifying;
        public static bool On = true;   // 計測用（A/B）

        public static void Install(HarmonyLib.Harmony _)
        {
            var h = new HarmonyLib.Harmony("FPOptimize.PlayerCache");
            if (!SafePatcher.Patch(h, typeof(Object), "FindAnyObjectByType", new[] { typeof(Il2CppSystem.Type), typeof(FindObjectsInactive) }, typeof(PlayerCache), nameof(FindPre)))
            {
                try { h.UnpatchSelf(); } catch (Exception e) { Main.Log("[player] unpatch failed: " + e.Message); }
                Main.Log("[player] OFF (patch failed)");
                return;
            }
            // 新しく作られたら数え直す（Start に付けられない種類は覚えない）
            void Add<T>(string name, bool include) where T : Object
            {
                if (!SafePatcher.Patch(h, typeof(T), "Start", null, typeof(PlayerCache), null, nameof(StartPost))) { Main.Log($"[player] {name}: no Start hook, not cached"); return; }
                var p = Il2CppType.Of<T>().Pointer;
                _slots[p] = new Slot { Name = name, TypePtr = p, Include = include };
            }
            Add<FPSController>("player", true);
            Add<Il2Cpp.QuestManager>("quests", false);
            Add<Il2Cpp.PlayerHealthManager>("player health", false);
            Add<Il2Cpp.Inventory>("inventory", false);
            Add<Il2Cpp.InventoryUI>("inventory ui", false);
            // DifficultyStopwatch.Start は空（他と共有）なので付けられない。代わりに階が変わるたびと、壊れた・非表示になったときに数え直す
            var dsw = Il2CppType.Of<Il2Cpp.DifficultyStopwatch>().Pointer;
            if (SafePatcher.Patch(h, typeof(Il2Cpp.LevelElevator), "SetupEndlessFloor", null, typeof(PlayerCache), null, nameof(FloorPost)))
                _slots[dsw] = new Slot { Name = "wave timer", TypePtr = dsw };
            else Main.Log("[player] wave timer: no floor hook, not cached");
            _active = true;
            Main.Log($"[player] ON ({_slots.Count} kinds)");
        }

        public static void OnSceneLoaded() { foreach (var s in _slots.Values) Reset(s); }
        static void Reset(Slot s) { s.Cached = null; s.Dirty = true; s.PassThrough = false; }

        static void StartPost(Component __instance)
        {
            try { if (__instance != null && _slots.TryGetValue(__instance.GetIl2CppType().Pointer, out var s)) Reset(s); } catch { }
        }
        static void FloorPost() { foreach (var s in _slots.Values) Reset(s); }

        static bool FindPre(Il2CppSystem.Type type, FindObjectsInactive findObjectsInactive, ref Object __result)
        {
            if (!_active || !On || !Main.Enabled || _verifying || type == null) return true;
            if (!_slots.TryGetValue(type.Pointer, out var s) || s.PassThrough) return true;
            bool exclude = findObjectsInactive == FindObjectsInactive.Exclude;
            if (!exclude && !s.Include) return true;
            try
            {
                if (!s.Dirty && (s.Cached == null || s.Cached.WasCollected || (exclude && !s.Cached.Cast<Component>().gameObject.activeInHierarchy))) s.Dirty = true;
                if (s.Dirty)
                {
                    // 数える：プレイヤーは非表示も含めて 1 つ、ほかは表示中のものが 1 つ
                    var all = Object.FindObjectsByType(type, s.Include ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                    s.Fills++;
                    int n = all?.Length ?? 0;
                    if (n == 0 && !s.Include)
                    {
                        // 表示中のものが無い：元の検索も「無し」。次の検索でまた数える
                        s.Cached = null;
                        __result = null;
                        return false;
                    }
                    s.Dirty = false;
                    if (n != 1)
                    {
                        s.Cached = null;
                        s.PassThrough = true;
                        Main.Log($"[player] {n} {s.Name} in scene: using the game's own search here");
                        return true;
                    }
                    s.Cached = all[0];
                    if (exclude && !s.Cached.Cast<Component>().gameObject.activeInHierarchy) return true;
                }
                s.Hits++;
                __result = s.Cached;
                if (Main.Verify && (s.Hits % 20) == 1)
                {
                    // 元の検索の結果と同じかを確かめる（この呼び出しは自分を素通りさせる）
                    _verifying = true;
                    try
                    {
                        var real = Object.FindAnyObjectByType(type, findObjectsInactive);
                        if (real == null || real.Pointer != s.Cached.Pointer) { s.Mismatch++; Main.Log($"[verify] {s.Name} cache MISMATCH"); }
                    }
                    finally { _verifying = false; }
                    Main.Log($"[verify] {s.Name} " + Stats());
                }
                return false;
            }
            catch (Exception e)
            {
                s.PassThrough = true;
                Main.Log($"[player] {s.Name} failed, pass-through: " + e.Message);
                return true;
            }
        }

        public static string Stats()
        {
            long h = 0, f = 0, m = 0;
            foreach (var s in _slots.Values) { h += s.Hits; f += s.Fills; m += s.Mismatch; }
            return $"hits={h} fills={f} mismatch={m}";
        }
        public static string Detail()
        {
            var parts = new List<string>();
            foreach (var s in _slots.Values) parts.Add($"{s.Name} {s.Hits}/{s.Fills}{(s.Mismatch > 0 ? " MISMATCH " + s.Mismatch : "")}{(s.PassThrough ? " (game search)" : "")}");
            return string.Join(", ", parts);
        }
        public static long[] TestStats() { long h = 0, f = 0, m = 0; foreach (var s in _slots.Values) { h += s.Hits; f += s.Fills; m += s.Mismatch; } return new[] { h, f, m }; }
    }
}
