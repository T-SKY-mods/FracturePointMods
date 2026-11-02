using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

using FPModOption;

namespace FPRarity
{
    // 確認用（autotest -Scenario bossfire）。ボスが 4体出る階を強制し、実際に銃を撃って（左クリック）ボスを倒す。
    //   ・弾が当たるとボスの体力が減ること（射撃のダメージがボスに入ること）
    //   ・全員倒せること、雑魚も全滅したらボス部屋の扉が開くこと
    // プレイヤーは撃たれて倒れないように、テストの間だけ無敵にする（セーブは autotest.ps1 が前後で元に戻す）。
    // 雑魚は弾と同じ入口（LivingEntity.OnHealthChanged）で倒す。
    public static class BossFireTest
    {
        static int _shots;

        public static List<(string, Action)> Steps()
        {
            var steps = new List<(string, Action)>
            {
                ("count shots", InstallShotCounter),
                ("force boss floor 50 + start endless run", () =>
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(Main.Dir, "endless_boss.txt"), "50");
                    Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
                }),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EndlessProbe.EnterAndMove()));
            for (int i = 0; i < 16; i++) steps.Add(("wait / pick card", EndlessProbe.PickCard));
            steps.Add(("fight", () => MelonCoroutines.Start(Fight())));
            for (int i = 0; i < 200; i++) steps.Add(("wait", () => { if (_done) TestMode.SkipWaitsForTest(); }));
            steps.Add(("check", Report));
            return steps;
        }

        static bool _done;
        static readonly List<string> _results = new();

        static void InstallShotCounter()
        {
            var h = new HarmonyLib.Harmony("fprarity.bossfire");
            bool ok = SafePatcher.Patch(h, typeof(FPSFireManager), "Fire", null, null, nameof(AfterFire), typeof(BossFireTest));
            Main.Log($"[bossfire] shot counter installed={ok}");
        }

        static void AfterFire() => _shots++;

        static IEnumerator Fight()
        {
            _done = false;
            var fps = Refs.Fps;
            var player = UnityEngine.Object.FindObjectsOfType<Il2CppBallistics.LivingEntity>().FirstOrDefault(e => e.PlayerHealthManager != null);
            if (fps == null || player == null) { Check("player found", false); _done = true; yield break; }
            player.PlayerHealthManager.invincible = true;
            Equip(fps);
            yield return new WaitForSeconds(2f);

            var bosses = Bosses();
            Check($"bosses in the room {bosses.Count} (= 1 + {EndlessBoss.ExtraBossCount(EndlessBoss.Floor)})", bosses.Count == 1 + EndlessBoss.ExtraBossCount(EndlessBoss.Floor));
            int n = 0;
            foreach (var boss in bosses)
            {
                n++;
                string name = $"boss {n}";
                float before = boss.health;
                int shots0 = _shots;
                bool firstHitSeen = false;
                float t0 = Time.time;
                while (boss.health > 0f && Time.time - t0 < 60f)
                {
                    if (!PlaceFacing(fps, boss)) { yield return new WaitForSeconds(0.2f); continue; }
                    for (int w = 0; w < 25 && !GameIsForeground(); w++) yield return new WaitForSeconds(0.2f);
                    FillMag(fps);
                    float hp = boss.health;
                    Mouse(LeftDown);
                    float burst = Time.time;
                    while (Time.time - burst < 0.6f && boss.health > 0f) { AimAt(fps, boss); yield return null; }
                    Mouse(LeftUp);
                    yield return new WaitForSeconds(0.15f);
                    if (!firstHitSeen && boss.health < hp)
                    {
                        firstHitSeen = true;
                        Main.Log($"[bossfire] {name}: first hits {hp:0} -> {boss.health:0} after {_shots - shots0} shots");
                        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Main.Dir, "shots", $"bossfire_{n}.png"));
                    }
                }
                Check($"{name}: gunfire damages the boss ({before:0} -> {Math.Max(0f, boss.health):0}, {_shots - shots0} shots)", firstHitSeen);
                Check($"{name}: killed by gunfire (hp {boss.health:0})", boss.health <= 0f);
                yield return new WaitForSeconds(0.5f);
            }

            // 雑魚（弾と同じ入口で倒す）
            var bossSet = new HashSet<IntPtr>(bosses.Select(b => b.Pointer));
            int grunts = 0;
            foreach (var e in Main.FindAll<Il2CppEnemyAI.EnemyHealth>().Where(e => e.health > 0f && e.gameObject.activeInHierarchy && !bossSet.Contains(e.Pointer)))
            {
                var le = (e.transform.parent != null ? e.transform.parent : e.transform).GetComponentInChildren<Il2CppBallistics.LivingEntity>(true);
                if (le == null) continue;
                try { le.OnHealthChanged(-(e.health + 50f)); grunts++; } catch (Exception ex) { Main.Log("[bossfire] grunt kill failed: " + ex.Message); }
            }
            Main.Log($"[bossfire] grunts finished: {grunts}");
            yield return new WaitForSeconds(4f);
            var d = UnityEngine.Object.FindObjectsOfType<BossfightDoorsChecker>().FirstOrDefault(x => x.BossHealth != null);
            int alive = Main.FindAll<Il2CppEnemyAI.EnemyHealth>().Count(e => e.health > 0f && e.gameObject.activeInHierarchy);
            Check($"all enemies dead (alive {alive})", alive == 0);
            Check($"boss room doors open after every enemy is dead (opened={d?.opened})", d != null && d.opened);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Main.Dir, "shots", "bossfire_doors.png"));
            player.PlayerHealthManager.invincible = false;
            try { System.IO.File.Delete(System.IO.Path.Combine(Main.Dir, "endless_boss.txt")); } catch { }
            _done = true;
        }

        static void Report() => Main.Log($"[bossfire] done (shots fired {_shots})");

        static List<Il2CppEnemyAI.EnemyHealth> Bosses()
        {
            var list = new List<Il2CppEnemyAI.EnemyHealth>();
            var main = UnityEngine.Object.FindObjectsOfType<BossfightDoorsChecker>().FirstOrDefault(x => x.BossHealth != null)?.BossHealth?.TryCast<Il2CppEnemyAI.EnemyHealth>();
            if (main != null) list.Add(main);
            list.AddRange(EndlessBoss.ExtraBosses.Where(b => b != null));
            return list;
        }

        // メイン武器（手持ちのうち、ハンドガンでないもの）に持ち替える
        static void Equip(Il2CppDemo.Scripts.Runtime.Character.FPSController fps)
        {
            var list = fps.InstantiatedWeapons;
            for (int i = 0; i < list.Count; i++)
            {
                var wi = list[i] == null ? null : Tracker.ItemByPrefab(list[i].gameObject.name.Replace("(Clone)", "").Trim());
                if (wi == null || wi.Type != WeaponItem.SlotType.Big) continue;
                fps.StartWeaponChange(i);
                Main.Log($"[bossfire] using {wi.GetName()}");
                return;
            }
        }

        static void FillMag(Il2CppDemo.Scripts.Runtime.Character.FPSController fps)
        {
            var w = fps.GetActiveItem()?.TryCast<Il2CppDemo.Scripts.Runtime.Item.Weapon>();
            if (w?.SelectedMag != null) w.SelectedMag.AmmoCount = w.SelectedMag.MaxAmmoCount;
        }

        static Vector3 Chest(Il2CppEnemyAI.EnemyHealth boss) => boss.transform.position + Vector3.up * 1.35f;

        // ボスから 5〜8m 離れた、ボスの胸が見える場所に立つ（ボスが動いたら立ち直す）
        static Vector3 _stand;
        static bool PlaceFacing(Il2CppDemo.Scripts.Runtime.Character.FPSController fps, Il2CppEnemyAI.EnemyHealth boss)
        {
            var chest = Chest(boss);
            var cam = Camera.main;
            if (cam != null && Vector3.Distance(fps.transform.position, boss.transform.position) < 10f && Visible(cam.transform.position, chest, boss)) return true;
            for (int a = 0; a < 16; a++)
                foreach (var r in new[] { 6f, 8f, 5f })
                {
                    var p = boss.transform.position + Quaternion.Euler(0, a * 22.5f, 0) * Vector3.forward * r;
                    if (!UnityEngine.AI.NavMesh.SamplePosition(p, out var hit, 1.5f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                    if (Mathf.Abs(hit.position.y - boss.transform.position.y) > 1.5f) continue;
                    if (!Visible(hit.position + Vector3.up * 1.6f, chest, boss)) continue;
                    var cc = fps.GetComponent<CharacterController>();
                    if (cc != null) cc.enabled = false;
                    fps.transform.position = hit.position + Vector3.up * 0.05f;
                    if (cc != null) cc.enabled = true;
                    _stand = hit.position;
                    AimAt(fps, boss);
                    return true;
                }
            return false;
        }

        static bool Visible(Vector3 eye, Vector3 target, Il2CppEnemyAI.EnemyHealth boss)
        {
            var root = boss.transform.parent != null ? boss.transform.parent : boss.transform;
            return !Physics.Linecast(eye, target, out var h, ~0, QueryTriggerInteraction.Ignore) || h.transform.IsChildOf(root);
        }

        // 左右は体の向き、上下は実際のマウスの動きで、ボスの胸を狙う
        static void AimAt(Il2CppDemo.Scripts.Runtime.Character.FPSController fps, Il2CppEnemyAI.EnemyHealth boss)
        {
            var cam = Camera.main; if (cam == null) return;
            var want = (Chest(boss) - cam.transform.position).normalized;
            var flat = want; flat.y = 0;
            if (flat.sqrMagnitude > 0.001f) fps.transform.rotation = Quaternion.LookRotation(flat);
            float pitchErr = Mathf.Asin(Mathf.Clamp(want.y, -1, 1)) * Mathf.Rad2Deg - Mathf.Asin(Mathf.Clamp(cam.transform.forward.y, -1, 1)) * Mathf.Rad2Deg;
            if (Mathf.Abs(pitchErr) > 0.5f) MouseMove(0, Mathf.RoundToInt(Mathf.Clamp(-pitchErr * 3f, -40, 40)));
        }

        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");

        // ---------- 実際のマウス入力（ゲームのウィンドウが前面のときだけ） ----------
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        const uint LeftDown = 0x02, LeftUp = 0x04, Move = 0x01;

        static bool GameIsForeground()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            return pid == (uint)Process.GetCurrentProcess().Id;
        }

        static void Mouse(uint flags)
        {
            if (flags != LeftUp && !GameIsForeground()) return;
            mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
        }

        static void MouseMove(int dx, int dy)
        {
            if (GameIsForeground()) mouse_event(Move, dx, dy, 0, UIntPtr.Zero);
        }
    }
}
