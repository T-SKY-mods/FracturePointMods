using System;
using System.Collections.Generic;
using System.Linq;
using FPModOption;
using Il2Cpp;
using UnityEngine;

namespace FPEndless
{
    // 調査（autotest -Dev -Scenario endfloorloot）：床・棚に置かれる戦利品（LootSpawnPoint）にも、調節の運が効いているか。
    //   1. 調節したラン（4/2/2）で階が作られるとき、LootSpawnPoint がゲームの抽選（SpawnableLoot.GetLoot）を呼んでいるか、
    //      そのとき調節が有効か・運は何倍か・運で選び直されたか（自然に置かれた分）
    //   2. その階の LootSpawnPoint をオフ→オンして本物の置き方をくり返させ、運 1 と運 4 で置かれた戦利品のレアリティを比べる
    public static class FloorLootTest
    {
        static bool _inPoint;
        static int _points, _lootCalls, _activeCalls, _items, _rerolledInPoint;
        static float _lastLuck;
        static readonly int[] _byRarity = new int[5], _rarest = new int[5];
        static readonly Dictionary<string, int> _tables = new();

        public static void Register() => TestMode.ExternalScenarios["endfloorloot"] = Steps;

        static void Install()
        {
            var h = new HarmonyLib.Harmony("fpendless.test.floorloot");
            h.Patch(HarmonyLib.AccessTools.Method(typeof(LootSpawnPoint), "OnEnable"),
                new HarmonyLib.HarmonyMethod(typeof(FloorLootTest), nameof(PointPre)), new HarmonyLib.HarmonyMethod(typeof(FloorLootTest), nameof(PointPost)));
            var post = new HarmonyLib.HarmonyMethod(typeof(FloorLootTest), nameof(LootPost)) { priority = HarmonyLib.Priority.Last };   // 調節の運の後に見る
            h.Patch(HarmonyLib.AccessTools.Method(typeof(SpawnableLoot), "GetLoot"), null, post);
            Core.Log("[floorloot] probes installed");
        }

        static int _rerollsAtPoint;
        static void PointPre() { _inPoint = true; _points++; _rerollsAtPoint = Patches.LootRerolls; }
        static void PointPost() { _inPoint = false; _rerolledInPoint += Patches.LootRerolls - _rerollsAtPoint; }

        static void LootPost(SpawnableLoot __instance, Il2CppSystem.Collections.Generic.List<LootDrop> __result)
        {
            if (!_inPoint || __instance == null) return;
            try
            {
                _lootCalls++;
                if (Tuning.Active) _activeCalls++;
                _lastLuck = Tuning.Luck;
                var n = __instance.name; _tables[n] = _tables.TryGetValue(n, out var c) ? c + 1 : 1;
                if (__result == null) return;
                int best = -1;
                for (int i = 0; i < __result.Count; i++)
                {
                    var d = __result[i];
                    if (d == null || d.dropType != LootDrop.DropType.Item) continue;
                    int r = Math.Clamp(Patches.RarityOf(__instance, d), 0, 4);
                    _byRarity[r]++; _items++; if (r > best) best = r;
                }
                if (best >= 0) _rarest[best]++;
            }
            catch (Exception e) { Core.LogOnce("floorloot", "[floorloot] probe failed: " + e.Message); }
        }

        static void Reset() { _points = _lootCalls = _activeCalls = _items = _rerolledInPoint = 0; Array.Clear(_byRarity); Array.Clear(_rarest); _tables.Clear(); }
        static string Mix(int[] a) { int n = a.Sum(); return n == 0 ? "-" : string.Join("/", a.Select(x => (100.0 * x / n).ToString("0.0"))) + $" (n={n})"; }
        static double Mean(int[] a) { int n = a.Sum(); if (n == 0) return 0; double s = 0; for (int r = 0; r < 5; r++) s += r * a[r]; return s / n; }
        static string Report(string tag) =>
            $"[floorloot] {tag}: spawn points {_points}, loot rolls in them {_lootCalls} (tuning active in {_activeCalls}, luck x{_lastLuck:0.00}), rerolled by luck {_rerolledInPoint}, "
            + $"tables {string.Join(", ", _tables.Select(k => k.Key + " " + k.Value))} | item drops C/U/R/E/L % {Mix(_byRarity)} mean {Mean(_byRarity):0.00} | rarest per point {Mix(_rarest)}";

        // その階の LootSpawnPoint をオフ→オンして、本物の置き方をくり返させる
        static (double mean, string line) Replay(float luck, int rounds)
        {
            Reset();
            Tuning.SetLuckForTest(luck);
            var pts = Core.FindAll<LootSpawnPoint>().Where(p => p != null && p.loot != null).ToList();
            for (int k = 0; k < rounds; k++)
                foreach (var p in pts)
                {
                    var go = p.gameObject; bool was = go.activeSelf;
                    go.SetActive(false); go.SetActive(true);
                    if (!was) go.SetActive(false);
                }
            return (Mean(_byRarity), Report($"replay luck x{luck:0.0} ({pts.Count} points x {rounds})"));
        }

        // 結果の表（最後にまとめて出す）
        static readonly List<string> _summary = new();
        static int _fails;

        // 階の深さ（ゲームの抽選は PlayerPrefs の currentFloor でレアリティの重みを変える）を変えて、運 1 と運 4 を比べる
        static void MeasureHere(int loc)
        {
            string level = EndlessTest.LevelName(), scene = Main.Scene;
            Core.Log(Report($"map {loc} ({level}, {scene}) as built"));
            var pts = Core.FindAll<LootSpawnPoint>().Where(p => p != null && p.loot != null).ToList();
            bool natural = _lootCalls > 0 && _activeCalls == _lootCalls;
            Core.Log($"[floorloot] map {loc}: spawn points {pts.Count}, tuning active {Tuning.Active} luck x{Tuning.Luck:0.00}");
            if (!natural) _fails++;
            _summary.Add($"map {loc} {level}: built with tuning active in {_activeCalls}/{_lootCalls} spawn-point rolls ({pts.Count} points)");
            float runLuck = Tuning.Luck;
            int floor0 = PlayerPrefs.GetInt("currentFloor", 0);
            foreach (int fl in new[] { 1, 5, 10 })
            {
                PlayerPrefs.SetInt("currentFloor", fl);
                var (m1, l1) = Replay(1f, 15); Core.Log($"  floor {fl} " + l1); var mix1 = Mix(_byRarity);
                var (m4, l4) = Replay(4f, 15); Core.Log($"  floor {fl} " + l4); var mix4 = Mix(_byRarity);
                bool ok = m4 > m1 + 0.05;
                if (!ok) _fails++;
                _summary.Add($"map {loc} floor {fl}: luck1 {mix1} mean {m1:0.00} | luck4 {mix4} mean {m4:0.00} {(ok ? "UP" : "NOT UP")}");
            }
            PlayerPrefs.SetInt("currentFloor", floor0);
            Tuning.SetLuckForTest(runLuck);
        }

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            void W(int n, string name) { for (int i = 0; i < n; i++) s.Add((name, () => { })); }
            W(4, "wait hub");
            s.Add(("install", () => { Install(); GameProf.PatchLocation(new HarmonyLib.Harmony("fpendless.test.floorloot.loc")); EndlessTest.NoBossFloors(); }));
            foreach (int loc in new[] { 0, 1, 2, 3 })
            {
                int l = loc;
                s.Add(($"start map {l}", () =>
                {
                    Reset();
                    GameProf.ForceLocation = l;
                    Tuning.SetForTest(true, 4, 2, 2);
                    Refs.Inv.DollarsCount = Tuning.FeeSet + 1000f;
                    var b = Core.FindAll<ButtonStartEndlessMode>().FirstOrDefault();
                    Core.Log($"[floorloot] start map {l} from scene {Main.Scene} (button {(b != null)})");
                    b?.StartEndlessRun();
                }));
                W(15, "wait load");
                s.Add(($"measure map {l}", () => { EndlessTest.Invincible(); MeasureHere(l); }));
                s.Add(($"back to hub {l}", () =>
                {
                    var el = Core.FindAll<LevelElevator>().FirstOrDefault();
                    Core.Log($"[floorloot] back to hub with {(el != null ? el.name : "none")}");
                    el?.ReturnToHub();
                }));
                W(15, "wait hub");
            }
            s.Add(("summary", () =>
            {
                GameProf.ForceLocation = -1;
                foreach (var line in _summary) Core.Log("[floorloot] " + line);
                Core.Log($"[test] CHECK every map and floor: tuning active when floor loot is placed, and luck x4 raises its rarity ({_summary.Count} results, {_fails} failed): {(_fails == 0 && _summary.Count == 16 ? "PASS" : "FAIL")}");
            }));
            W(2, "wait");
            return s;
        }
    }
}
