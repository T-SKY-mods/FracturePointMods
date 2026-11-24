using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using Il2Cpp;
using UnityEngine;

namespace FPEndless
{
    // 自動テスト（autotest -Dev -Scenario endtune）。セーブは autotest.ps1 が前後で退避・復元する
    //   拠点：端末の通常/調整・◀▶・運と参加費・ボタンの金額・お金が足りないときは始まらない
    //   ラン：参加費・敵の弾のダメージ・敵の体力・湧く数・戦利品の運・拠点に戻ったら元に戻る
    public static class EndlessTest
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        public static void Register()
        {
            TestMode.ExternalScenarios["endtune"] = () => Steps(false);
            TestMode.ExternalScenarios["endtuneui"] = () => Steps(true);   // 端末の画面だけ（写真）
            TestMode.ExternalScenarios["endluck"] = LuckProbe;              // 調査：招き猫・花瓶の運の仕組み
            TestMode.ExternalScenarios["endbonus"] = BonusSteps;            // 招き猫・花瓶の運を調整に掛ける
            TestMode.ExternalScenarios["endperf"] = PerfSteps;              // 調整したランの重さ
            FloorLootTest.Register();                                      // 調査：床・棚の戦利品にも運が効くか
            TestMode.ExternalScenarios["endmaps"] = MapSteps;               // 調査：階の種類ごとに、ゲームの何が重いか（GameProf）
        }

        static void Check(string what, bool ok) => Core.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static void Wait(List<(string, Action)> s, int n, string name = "wait") { for (int i = 0; i < n; i++) s.Add((name, () => { })); }
        static bool Click(string what, int row = 0) { var p = Tablet.ScreenOf(what, row); return p.HasValue && Tablet.Click(p.Value); }
        static void Shot(string name) { Directory.CreateDirectory(ShotDir); ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"endtune_{name}.png")); }

        static int _fee, _calls; static float _dollarsAfter;
        static string BossFile => Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "FPRarity", "endless_boss.txt");

        // 次の階へ：プレイヤーから遠い方のエレベーター（今いる階の出口）に入って上へのボタン
        static void MoveUp()
        {
            var fps = Refs.Fps;
            var els = Core.FindAll<LevelElevator>().ToList();
            var el = fps == null || els.Count < 2 ? els.FirstOrDefault() : els.OrderByDescending(e => Vector3.Distance(fps.transform.position, e.transform.position)).FirstOrDefault();
            if (el == null || fps == null) { Check("run: elevator found", false); return; }
            var cc = fps.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            fps.transform.position = el.EnterCollider != null ? el.EnterCollider.bounds.center : el.transform.position;
            if (cc != null) cc.enabled = true;
            var go = Core.FindAll<ElevatorButton>().FirstOrDefault(b => b.type == ElevatorButton.ButtonType.Up && b.elevator != null && b.elevator.Pointer == el.Pointer && !b.activated);
            Main.Log($"[test] moving up with '{el.name}' (button {go?.name ?? "none"})");
            if (go != null) go.Interact(); else el.MoveToNextFloor();
        }


        // ---------- 調査：招き猫のお布施・戦士の花瓶（LootStatue）が何を変えるか ----------
        static readonly string[] PrefKeys = { "LootKittyBonus", "LootMultiplier", "StatueDamage", "StatueEffect", "Difficulty", "NoDamage", "currentFloor" };
        static string Prefs()
        {
            var parts = new List<string>();
            foreach (var k in PrefKeys)
            {
                if (!PlayerPrefs.HasKey(k)) { parts.Add(k + "=none"); continue; }
                parts.Add($"{k}=i{PlayerPrefs.GetInt(k, -999)}/f{PlayerPrefs.GetFloat(k, -999f):0.###}/s'{PlayerPrefs.GetString(k, "")}'");
            }
            return string.Join(" ", parts);
        }
        static void Sample(string tag)
        {
            var t = Resources.FindObjectsOfTypeAll<SpawnableLoot>().FirstOrDefault(x => x.name == "DefaultLootList");
            if (t == null) { Main.Log("[luck] no table"); return; }
            string ch = ""; for (int r = 0; r < 5; r++) ch += $"{t.GetChanceByRarity((RarityData.LootRarity)r, PlayerPrefs.GetInt("currentFloor", 1)):0.0} ";
            var cnt = new int[5]; int n = 0, none = 0;
            for (int i = 0; i < 3000; i++)
            {
                var got = t.GetLoot(1);
                if (got == null || got.Count == 0) { none++; continue; }
                foreach (var d in got) if (d != null && d.dropType == LootDrop.DropType.Item) { cnt[Patches.RarityOf(t, d)]++; n++; }
            }
            Main.Log($"[luck] {tag}: chance {ch}| items {n} none {none} C/U/R/E/L % {string.Join("/", cnt.Select(c => n == 0 ? "-" : (100.0 * c / n).ToString("0.0")))} | AKStats dmg {Tuning.DamageOfForTest("AKStats")} | {Prefs()}");
        }

        static List<(string, Action)> LuckProbe()
        {
            var s = new List<(string, Action)>();
            Wait(s, 4, "wait hub");
            s.Add(("hub prefs", () =>
            {
                Main.Log("[luck] hub: " + Prefs());
                foreach (var c in Core.FindAll<LuckyCat>()) Main.Log($"[luck] LuckyCat '{c.name}' price {c.price} desc '{c.GetDescription()}'");
                foreach (var v in Core.FindAll<LootStatue>()) Main.Log($"[luck] LootStatue '{v.name}' effect {v.effectEnabled} desc '{v.GetDescription()}' tip '{v.GetTooltipText()}'");
                Tuning.SetForTest(false, 1, 1, 1);
            }));
            s.Add(("donate to the cat", () =>
            {
                Refs.Inv.DollarsCount = 100000f;
                Core.FindAll<LuckyCat>().FirstOrDefault()?.Interact();
            }));
            Wait(s, 3);
            s.Add(("after cat", () => Main.Log($"[luck] after cat (dollars {Refs.Inv.DollarsCount:0}): " + Prefs())));
            s.Add(("vase on", () =>
            {
                var v = Core.FindAll<LootStatue>().FirstOrDefault();
                if (v != null && !v.effectEnabled) v.SetEffect(true);
                Main.Log($"[luck] after vase SetEffect(true): effect {v?.effectEnabled} " + Prefs());
            }));
            s.Add(("start run", () => { Refs.Inv.DollarsCount = 5000f; Core.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun(); }));
            Wait(s, 15, "wait load");
            s.Add(("run: measure", () =>
            {
                var t = Resources.FindObjectsOfTypeAll<SpawnableLoot>().FirstOrDefault(x => x.name == "DefaultLootList");
                var wt = Resources.FindObjectsOfTypeAll<SpawnableLoot>().FirstOrDefault(x => x.name == "Weapons 1");
                void Measure(string tag)
                {
                    bool boost = false, kitty = false; float dmg = -1f;
                    try { boost = Il2CppCoop.Proposals.StatueRules.LootBoostEnabled; dmg = Il2CppCoop.Proposals.StatueRules.DamageMultiplier; } catch (Exception e) { Main.Log("[luck] rules err " + e.Message); }
                    try { kitty = Il2CppCoop.CoopLoot.KittyBonus; } catch (Exception e) { Main.Log("[luck] kitty err " + e.Message); }
                    var cnt = new int[5]; int n = 0; var wc = new int[5]; int wn = 0;
                    for (int i = 0; i < 6000; i++)
                    {
                        var d = t.GetSingleLoot(); if (d != null && d.dropType == LootDrop.DropType.Item) { cnt[Patches.RarityOf(t, d)]++; n++; }
                        var w = wt.GetSingleLoot(); if (w != null && w.dropType == LootDrop.DropType.Weapon) { wc[Patches.RarityOf(wt, w)]++; wn++; }
                    }
                    string P(int[] c, int m) => string.Join("/", c.Select(x => m == 0 ? "-" : (100.0 * x / m).ToString("0.0")));
                    Main.Log($"[luck] {tag}: boost {boost} kitty {kitty} dmgMult {dmg} | items {P(cnt, n)} (n {n}) | weapons(game rarity) {P(wc, wn)} (n {wn}) | {Prefs()}");
                }
                PlayerPrefs.DeleteKey("StatueEffect"); PlayerPrefs.DeleteKey("KittyEffect"); Measure("none");
                PlayerPrefs.SetInt("StatueEffect", 1); Measure("vase");
                PlayerPrefs.DeleteKey("StatueEffect"); PlayerPrefs.SetInt("KittyEffect", 1); Measure("kitty");
                PlayerPrefs.SetInt("StatueEffect", 1); Measure("vase+kitty");
            }));
            return s;
        }

        // 招き猫・花瓶の運が調整の運に掛かり、花瓶の敵のダメージも強さと掛け合わさる（設定はテストの後に元へ戻る）
        static List<(string, Action)> BonusSteps()
        {
            var s = new List<(string, Action)>();
            Wait(s, 4, "wait hub");
            s.Add(("set cat+vase", () =>
            {
                PlayerPrefs.SetInt("KittyEffect", 1); PlayerPrefs.SetInt("StatueEffect", 1); PlayerPrefs.SetFloat("StatueDamage", 1.35f);
                Tuning.SetForTest(true, 2, 2, 2);
                Tablet.TabletForTest?.Interact();
            }));
            Wait(s, 3);
            s.Add(("tablet", () =>
            {
                Main.Log($"[test] summary '{Tablet.SummaryForTest}' cat {Tuning.CatNow()} vase {Tuning.VaseNow()} total {Tuning.TotalSet}");
                Check("bonus: cat and vase seen", Tuning.CatNow() && Tuning.VaseNow());
                float want = Tuning.LuckOf(2, 2, 2) * 1.25f * 1.5f;
                Check($"bonus: total luck tuning x 1.25 x 1.5 = {want:0.00} ({Tuning.TotalSet})", Mathf.Abs(Tuning.TotalSet - want) < 0.001f);
                Check($"bonus: fee only from tuning ({Tuning.FeeSet})", Tuning.FeeSet == Tuning.FeeOf(Tuning.LuckOf(2, 2, 2)));
                Check("bonus: summary shows the total", Tablet.SummaryForTest.Contains(Tuning.TotalSet.ToString("0.0#")));
                Shot("bonus");
            }));
            Wait(s, 1);
            s.Add(("start", () =>
            {
                _fee = Tuning.FeeSet; Refs.Inv.DollarsCount = _fee + 1000f;
                Core.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
            }));
            Wait(s, 15, "wait load");
            s.Add(("run", () =>
            {
                float dmg = -1f; try { dmg = Il2CppCoop.Proposals.StatueRules.DamageMultiplier; } catch { }
                var d = Tuning.FirstDamagedForTest();
                Main.Log($"[test] run luck {Tuning.Luck} link {Link.Luck()} vaseDmg {dmg} class '{d.name}' {d.orig} -> {d.now}");
                Check($"run: luck tuning x cat x vase ({Tuning.Luck})", Mathf.Abs(Tuning.Luck - Tuning.LuckOf(2, 2, 2) * 1.875f) < 0.001f && Mathf.Abs(Link.Luck() - Tuning.Luck) < 0.001f);
                Check($"run: vase enemy damage still on ({dmg})", Mathf.Abs(dmg - 1.35f) < 0.01f);
                Check("run: enemy bullet damage x2 on top", d.orig > 0f && Mathf.Abs(d.now - d.orig * 2f) < 0.01f);
            }));
            return s;
        }

        // ---------- 調整したランの重さ（autotest -Dev -Scenario endperf） ----------
        static System.Collections.IEnumerator Frames(string tag, int n)
        {
            var ts = new List<float>();
            for (int i = 0; i < n; i++) { yield return null; ts.Add(Time.unscaledDeltaTime * 1000f); }
            ts.Sort();
            float avg = ts.Average(), p99 = ts[(int)(ts.Count * 0.99f)], max = ts[ts.Count - 1];
            int spikes = ts.Count(x => x > 33f);
            Main.Log($"[perf] {tag}: frames {n} avg {avg:0.0}ms ({1000f / avg:0}fps) p99 {p99:0.0}ms max {max:0.0}ms spikes>33ms {spikes}");
        }
        // 敵（生きている・倒れた）と、動き続けている物理・人形の数
        static void Census(string tag)
        {
            int alive = 0, dead = 0, pmOn = 0, animOn = 0, rbAwake = 0, rbAll = 0, aiOn = 0;
            foreach (var e in Core.FindAll<Il2CppEnemyAI.EnemyHealth>())
            {
                if (e == null || !e.gameObject.activeInHierarchy) continue;
                if (e.health > 0f) { alive++; continue; }
                dead++;
                var root = e.transform.root;
                foreach (var pm in root.GetComponentsInChildren<Il2CppRootMotion.Dynamics.PuppetMaster>(false)) if (pm.enabled) pmOn++;
                foreach (var an in root.GetComponentsInChildren<Animator>(false)) if (an.enabled) animOn++;
                foreach (var rb in root.GetComponentsInChildren<Rigidbody>(false)) { rbAll++; if (!rb.IsSleeping() && !rb.isKinematic) rbAwake++; }
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(false)) if (mb.enabled && mb.GetIl2CppType().Namespace == "EnemyAI") aiOn++;
            }
            int loose = 0, looseAwake = 0;
            foreach (var rb in UnityEngine.Object.FindObjectsOfType<Rigidbody>()) { if (rb.isKinematic) continue; loose++; if (!rb.IsSleeping()) looseAwake++; }
            Main.Log($"[perf] census {tag}: alive {alive}, dead {dead} (puppet on {pmOn}, animators on {animOn}, ragdoll bodies {rbAll} awake {rbAwake}, EnemyAI scripts on {aiOn}); all dynamic rigidbodies {loose} awake {looseAwake}");
        }

        // ---------- 階の種類ごとの重さ（autotest -Dev -Scenario endmaps） ----------
        internal static void NoBossFloors()
        {
            try { Type.GetType("FPRarity.EndlessBoss, FPRarity")?.GetField("ForceNormalForTest", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.SetValue(null, true); }
            catch (Exception e) { Main.Log("[prof] boss off failed: " + e.Message); }
        }
        internal static void Invincible() { try { var p = UnityEngine.Object.FindObjectsOfType<Il2CppBallistics.LivingEntity>().FirstOrDefault(e => e.PlayerHealthManager != null); if (p != null) p.PlayerHealthManager.invincible = true; } catch { } }
        internal static string LevelName() { try { return string.Join(",", Core.FindAll<LevelElevator>().Select(e => $"{e.LevelBuilder?.gameObject.name}#{e.CurrentFloor}").Distinct()); } catch { return "?"; } }
        static List<(string, Action)> MapSteps()
        {
            var s = new List<(string, Action)>();
            Wait(s, 4, "wait hub");
            s.Add(("start tuned", () =>
            {
                GameProf.PatchLocation(new HarmonyLib.Harmony("fpendless.prof.loc"));
                NoBossFloors();
                GameProf.ForceLocation = 0;
                Tuning.SetForTest(true, 4, 2, 2);
                _fee = Tuning.FeeSet; Refs.Inv.DollarsCount = _fee + 1000f;
                Core.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
            }));
            Wait(s, 12, "wait load");
            int[] locs = { 0, 1, 2, 3 };
            for (int i = 0; i < locs.Length; i++)
            {
                int loc = locs[i], next = i + 1 < locs.Length ? locs[i + 1] : -1;
                s.Add(("arrive " + loc, () => { Invincible(); Main.Log($"[prof] floor for location {loc}: {LevelName()} scene {Main.Scene}"); }));
                Wait(s, 4, "let waves come");
                s.Add(("measure " + loc, () => { Invincible(); GameProf.MeasureFloor($"loc{loc} {LevelName()}"); }));
                Wait(s, 115, "measuring");
                if (next >= 0) s.Add(("next floor", () => { GameProf.ForceLocation = next; Main.Log($"[prof] busy before moving: {GameProf.Busy}"); MoveUp(); }));
                Wait(s, 22, "wait next floor");
            }
            return s;
        }

        static List<(string, Action)> PerfSteps()
        {
            var s = new List<(string, Action)>();
            Wait(s, 4, "wait hub");
            s.Add(("start tuned", () =>
            {
                Tuning.SetForTest(true, 4, 2, 2);
                _fee = Tuning.FeeSet; Refs.Inv.DollarsCount = _fee + 1000f;
                Patches.LootMs = 0; Patches.LootCalls = 0;
                Core.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
            }));
            Wait(s, 15, "wait load");
            s.Add(("costs", () =>
            {
                Main.Log($"[perf] loot luck during load: {Patches.LootCalls} calls {Patches.LootMs:0.0}ms, rerolls {Patches.LootRerolls}");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int n = 0; for (int i = 0; i < 5; i++) n = Resources.FindObjectsOfTypeAll<Il2CppEnemyAI.ClassStats>().Length;
                Main.Log($"[perf] FindObjectsOfTypeAll<ClassStats> x5: {sw.Elapsed.TotalMilliseconds / 5:0.00}ms each ({n} found)");
                sw.Restart(); Tuning.ScanForTest(); Main.Log($"[perf] damage scan: {sw.Elapsed.TotalMilliseconds:0.00}ms");
                int alive = Core.FindAll<Il2CppEnemyAI.EnemyHealth>().Count(x => x.health > 0f && x.gameObject.activeInHierarchy);
                Main.Log($"[perf] enemies alive {alive}, spawner asked {Patches.SpawnAsked} given {Patches.SpawnGiven}");
                MelonLoader.MelonCoroutines.Start(Frames("tuned run", 900));
            }));
            Wait(s, 12, "measure");
            s.Add(("next floor", MoveUp));
            Wait(s, 30, "wait next floor");
            s.Add(("floor 2", () =>
            {
                Main.Log($"[perf] loot luck so far: {Patches.LootCalls} calls {Patches.LootMs:0.0}ms; spawner asked {Patches.SpawnAsked} given {Patches.SpawnGiven}");
                MelonLoader.MelonCoroutines.Start(Frames("tuned floor 2", 900));
            }));
            Wait(s, 12, "measure");
            s.Add(("census alive", () => Census("alive")));
            s.Add(("kill all", () =>
            {
                int n = 0;
                foreach (var e in Core.FindAll<Il2CppEnemyAI.EnemyHealth>())
                    if (e != null && e.health > 0f && e.gameObject.activeInHierarchy) { try { e.TakeDamage(100000f); n++; } catch (Exception ex) { Main.Log("[perf] kill failed " + ex.Message); } }
                Main.Log($"[perf] killed {n}");
            }));
            Wait(s, 6, "settle");
            s.Add(("dead frames", () => { Census("dead"); MelonLoader.MelonCoroutines.Start(Frames("after kills", 900)); }));
            Wait(s, 12, "measure");
            s.Add(("census later", () => { Census("dead later"); MelonLoader.MelonCoroutines.Start(Frames("after kills later", 900)); }));
            Wait(s, 12, "measure");
            return s;
        }

        static List<(string, Action)> Steps(bool uiOnly)
        {
            var s = new List<(string, Action)>();
            Wait(s, 4, "wait tablet");
            s.Add(("fee table", () =>
            {
                for (int k = 0; k <= 18; k++) { float l = 1f + k / 6f; Main.Log($"[test] luck x{l:0.00} -> fee ${Tuning.FeeOf(l):N0}"); }
                Check("fee: vanilla $500 at luck 1", Tuning.FeeOf(1f) == 500);
                Check("fee: $50,000 at luck 4", Tuning.FeeOf(4f) == 50000);
                Check($"luck: 4 when all at max x4/x2/x2 ({Tuning.LuckOf(4, 2, 2)})", Mathf.Abs(Tuning.LuckOf(4, 2, 2) - 4f) < 0.001f);
            }));
            s.Add(("focus tablet", () => { Tuning.SetForTest(false, 1, 1, 1); Tablet.TabletForTest?.Interact(); }));
            Wait(s, 3);
            s.Add(("tablet: normal", () =>
            {
                Check("tablet: tuning UI built", Tablet.Ready);
                Check("tablet: click 調節", Click("tuned") && Tuning.Mode);
            }));
            Wait(s, 1);
            s.Add(("tablet: raise", () =>
            {
                Check("tablet: rows shown in tuned mode", Tablet.RowsShownForTest);
                for (int row = 0; row < 3; row++) for (int k = 0; k < 7; k++) Click("plus", row);
                Check($"tablet: max x4 / x2 / x2 after clicks ({Tuning.PowerSet}/{Tuning.CountSet}/{Tuning.HpSet})", Tuning.PowerSet == 4f && Tuning.CountSet == 2f && Tuning.HpSet == 2f);
                Check($"tablet: fee $50,000 at max ({Tuning.FeeSet}, luck {Tuning.LuckSet:0.00})", Tuning.FeeSet == 50000 && Mathf.Abs(Tuning.LuckSet - 4f) < 0.001f);
                Click("minus", 0);
                Check("tablet: strength ◀ lowers by 0.5", Tuning.PowerSet == 3.5f);
                Click("minus", 1); Click("minus", 2);
                Check($"tablet: count / health ◀ lower by 0.25 ({Tuning.CountSet}/{Tuning.HpSet})", Tuning.CountSet == 1.75f && Tuning.HpSet == 1.75f);
            }));
            Wait(s, 1);
            s.Add(("tablet: texts", () =>
            {
                Main.Log($"[test] summary '{Tablet.SummaryForTest}' button '{Tablet.ButtonTextForTest}'");
                Check("tablet: summary shows luck and fee", Tablet.SummaryForTest.Contains(Tuning.TotalSet.ToString("0.0#")) && Tablet.SummaryForTest.Contains(Tuning.FeeSet.ToString("N0")));
                Check("tablet: start button shows the fee", Tablet.ButtonTextForTest.Contains(Tuning.FeeSet.ToString("N0")));
            }));
            s.Add(("shot tablet", () => { Shot("tablet"); Tablet.DumpButtonsForTest(); }));
            Wait(s, 1);
            s.Add(("tablet: back to normal", () =>
            {
                Check("tablet: click 通常", Click("normal") && !Tuning.Mode);
            }));
            Wait(s, 1);
            s.Add(("tablet: normal texts", () =>
            {
                Check("tablet: rows hidden in normal mode", !Tablet.RowsShownForTest);
                Check($"tablet: button back to the game's text ('{Tablet.ButtonTextForTest}')", !Tablet.ButtonTextForTest.Contains("50,000"));
            }));
            s.Add(("shot normal", () => { Shot("normal"); Tablet.DumpButtonsForTest(); }));
            if (uiOnly)
            {
                Wait(s, 1);
                return s;
            }
            // お金が足りない
            s.Add(("not enough dollars", () =>
            {
                Tuning.SetForTest(true, 4, 4, 4);
                Refs.Inv.DollarsCount = 1000f;
                Core.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
                Check("start refused without enough dollars (dollars kept)", Mathf.Abs(Refs.Inv.DollarsCount - 1000f) < 0.5f && !Tuning.Active);
            }));
            Wait(s, 4);
            s.Add(("still in the hub", () => Check("start refused: still in the hub", Main.Scene == Core.HubScene)));
            // 調整して始める（全部 x2 → 運 1+1/3+1+1）
            s.Add(("start tuned", () =>
            {
                Tuning.SetForTest(true, 2, 2, 2);
                _fee = Tuning.FeeSet;
                Refs.Inv.DollarsCount = _fee + 1000f;
                File.WriteAllText(BossFile, "1");   // 最初の階をボス部屋に（レアリティmod）
                Core.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
                Main.Log($"[test] started with fee ${_fee:N0}");
            }));
            Wait(s, 15, "wait load");
            s.Add(("run: fee and state", () =>
            {
                _dollarsAfter = Refs.Inv?.DollarsCount ?? -1f;
                Check($"run: in the run scene ({Main.Scene})", Main.Scene != Core.HubScene);
                Check($"run: tuning active, luck x{Tuning.Luck}", Tuning.Active && Mathf.Abs(Tuning.Luck - Tuning.LuckOf(2, 2, 2) * Tuning.BonusOf(Tuning.RunCat, Tuning.RunVase)) < 0.001f && Mathf.Abs(Link.Luck() - Tuning.Luck) < 0.001f);
                Check($"run: paid the fee ${_fee:N0} (dollars left {_dollarsAfter:0})", Mathf.Abs(_dollarsAfter - 1000f) < 1f);
                var (n, orig, now) = Tuning.FirstDamagedForTest();
                Check($"run: enemy bullet damage x2 ('{n}' {orig} -> {now}, {Tuning.DamagedForTest} classes)", orig > 0f && Mathf.Abs(now - orig * 2f) < 0.01f);
            }));
            Wait(s, 10, "wait boss room");
            s.Add(("run: boss floor count", () =>
            {
                int grunts = Core.FindAll<Il2CppEnemyAI.EnemyHealth>().Count(x => x.health > 0f && x.gameObject.activeInHierarchy);
                Main.Log($"[test] boss floor {Link.IsEndlessBossFloor()}: spawner calls {Patches.SpawnCalls} asked {Patches.SpawnAsked} given {Patches.SpawnGiven}, enemies alive {grunts}");
                Check("run: boss floor built", Link.IsEndlessBossFloor());
                try { File.Delete(BossFile); } catch { }
                Patches.SpawnCalls = Patches.SpawnAsked = Patches.SpawnGiven = 0;
            }));
            s.Add(("run: enemy health", () =>
            {
                var e = Core.FindAll<Il2CppEnemyAI.EnemyHealth>().FirstOrDefault(x => x != null && x.health > 30f && x.gameObject.activeInHierarchy);
                if (e == null) { Check("run: an enemy to hit", false); return; }
                float h0 = e.health; e.TakeDamage(10f); float d2 = h0 - e.health;
                Tuning.SetRunForTest(2, 2, 1);
                float h1 = e.health; e.TakeDamage(10f); float d1 = h1 - e.health;
                Tuning.SetRunForTest(2, 2, 2);
                Main.Log($"[test] damage per 10: hp x2 {d2:0.00}, hp x1 {d1:0.00}");
                Check($"run: enemy takes half damage at health x2 ({d2:0.00} vs {d1:0.00})", d1 > 0f && Mathf.Abs(d2 - d1 / 2f) < 0.05f);
            }));
            s.Add(("run: loot luck", () =>
            {
                var t = Resources.FindObjectsOfTypeAll<SpawnableLoot>().FirstOrDefault(x => x.name == "DefaultLootList");
                if (t == null) { Check("run: loot table found", false); return; }
                double Mean(float luck, out string mix)
                {
                    Tuning.SetRunForTest(1f, 1f, 1f); Tuning.SetLuckForTest(luck);
                    var cnt = new int[5]; int n = 0;
                    for (int i = 0; i < 1500; i++)
                    {
                        var got = t.GetLoot(1);
                        if (got == null) continue;
                        foreach (var d in got) if (d != null && d.dropType == LootDrop.DropType.Item) { cnt[Patches.RarityOf(t, d)]++; n++; }
                    }
                    mix = string.Join("/", cnt.Select(c => n == 0 ? "-" : (100.0 * c / n).ToString("0.0")));
                    double sum = 0; for (int r = 0; r < 5; r++) sum += r * cnt[r];
                    return n == 0 ? 0 : sum / n;
                }
                double m1 = Mean(1f, out var mix1), m2 = Mean(2f, out var mix2), m4 = Mean(4f, out var mix4);
                Main.Log($"[test] DefaultLootList items C/U/R/E/L %: luck1 {mix1} | luck2 {mix2} | luck4 {mix4}");
                Check($"run: loot rarity rises with luck (mean {m1:0.00} < {m2:0.00} < {m4:0.00})", m1 < m2 && m2 < m4);
                // 武器：レアリティmod の抽選（PickTier は Link.Luck() を使う）を、運 1 と今のランの運で引き比べる
                var pick = Type.GetType("FPRarity.RarityTable, FPRarity")?.GetMethod("PickTierFor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (pick == null) { Check("run: rarity mod tier roll found", false); }
                else
                {
                    Tuning.SetRunForTest(2, 2, 2);
                    float runLuck = Link.Luck();
                    int floor = PlayerPrefs.GetInt("currentFloor", 1);
                    double WMean(float luck, out string mix)
                    {
                        var c = new int[5];
                        for (int i = 0; i < 5000; i++) c[Math.Clamp(Convert.ToInt32(pick.Invoke(null, new object[] { floor, luck })), 0, 4)]++;
                        mix = string.Join("/", c.Select(x => (x / 50.0).ToString("0.0")));
                        double sum = 0; for (int r = 0; r < 5; r++) sum += r * c[r]; return sum / 5000;
                    }
                    double w1 = WMean(1f, out var wm1), wr = WMean(runLuck, out var wmr);
                    Main.Log($"[test] weapons floor {floor} C/U/R/E/L %: luck1 {wm1} | run luck {runLuck:0.00} {wmr}");
                    Check($"run: weapon rarity higher with the run luck x{runLuck:0.00} (mean {w1:0.00} < {wr:0.00})", runLuck > 1f && wr > w1 + 0.05);
                }
                Tuning.SetRunForTest(2, 2, 2);
            }));
            // 次の階へ（敵の数 x2）
            s.Add(("next floor", MoveUp));
            Wait(s, 30, "wait next floor");
            s.Add(("run: enemy census", () =>
            {
                var el = Core.FindAll<LevelElevator>().FirstOrDefault();
                Main.Log($"[test] levelParent '{el?.levelParent?.name}' children {el?.levelParent?.childCount} floor {el?.CurrentFloor}, spawner calls {Patches.SpawnCalls} asked {Patches.SpawnAsked} given {Patches.SpawnGiven}");
                foreach (var e in Resources.FindObjectsOfTypeAll<Il2CppEnemyAI.EnemyHealth>())
                {
                    if (e == null || e.gameObject.scene.name == null) continue;
                    string path = e.name; for (var t = e.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                    bool under = el?.levelParent != null && e.transform.IsChildOf(el.levelParent);
                    Main.Log($"[test]   enemy hp {e.health:0} active {e.gameObject.activeInHierarchy} under level {under} scene '{e.gameObject.scene.name}' {path}");
                }
            }));
            s.Add(("run: enemy count", () =>
            {
                if (Link.IsEndlessBossFloor()) { Check("run: boss floor (count handled by the rarity mod)", true); return; }
                Check("run: room spawners asked for enemies", Patches.SpawnCalls > 0);
                Check($"run: enemy count x2 ({Patches.SpawnGiven} for {Patches.SpawnAsked})", Patches.SpawnGiven == Patches.SpawnAsked * 2);
            }));
            Wait(s, 3);
            s.Add(("shot floor", () => Shot("floor")));
            s.Add(("run end restores", () =>
            {
                var (n, orig, _) = Tuning.FirstDamagedForTest();
                Tuning.EndRun("test");
                Check("run end: tuning off", !Tuning.Active && Link.Luck() == 1f);
                Check($"run end: enemy bullet damage restored ('{n}' {Tuning.DamageOfForTest(n)} = {orig})", Mathf.Abs(Tuning.DamageOfForTest(n) - orig) < 0.01f);
            }));
            return s;
        }
    }
}
