using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPHitMarker
{
    // autotest -Scenario hitmarker：4 種類の見た目を撮り、エンドレスで敵に本物の当たり（BulletHandler.BulletHit）を起こして印を見る
    public static class HitTest
    {
        static void Check(string what, bool ok) => TestMode.Check(what, ok);
        static string ShotDir => Path.Combine(Core.Dir, "shots");
        static void Shot(string name) { Directory.CreateDirectory(ShotDir); ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"hit_{name}.png")); }

        public static void Register()
        {
            TestMode.ExternalScenarios["hitmarker"] = Steps;
            TestMode.ExternalScenarios["hitmarkerview"] = ViewSteps;
            TestMode.ExternalScenarios["hitmarker_headkill"] = () => KillSteps("head kill", "head", Kind.HeadKill);
            TestMode.ExternalScenarios["hitmarker_boss"] = BossSteps;
            TestMode.ExternalScenarios["hitmarker_armkill"] = () => KillSteps("arm kill = red, not blue", "lowerarm", Kind.Kill);
        }

        // ボス（オフィスのボス部屋の Victor ほか）：体力が残っている間は赤にならず、倒れた弾だけ赤
        static List<(string, Action)> BossSteps()
        {
            string force = Path.Combine(Core.Dir, "..", "FPRarity", "endless_boss.txt");
            var probe = Type.GetType("FPRarity.EndlessProbe, FPRarity");
            void Call(string m) => HarmonyLib.AccessTools.Method(probe, m)?.Invoke(null, null);
            var s = new List<(string, Action)> { ("start endless run", () => { File.WriteAllText(force, "50"); Core.FindAll<Il2Cpp.ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun(); }) };
            for (int i = 0; i < 12; i++) s.Add(("wait load", () => { }));
            s.Add(("enter elevator + move", () => Call("EnterAndMove")));
            for (int i = 0; i < 16; i++) s.Add(("wait / pick card", () => Call("PickCard")));
            s.Add(("boss", () =>
            {
                var gun = Gun();
                var bosses = Core.FindAll<Il2CppBallistics.LivingEntity>().Where(e => e.Boss && e.EnemyHealth != null && e.EnemyHealth.health > 0f && e.gameObject.scene.IsValid()).ToList();
                foreach (var b in bosses) Core.Log($"[hit] boss {b.gameObject.name} '{b.BossName}' hit-box {b.Health:0}/{b.StartHealth:0}, enemy side {b.EnemyHealth.health:0}");
                if (bosses.Count == 0 || gun == null) { Check($"boss found ({bosses.Count}) and gun", false); return; }
                float dmg0 = gun.MuzzleDamage;
                foreach (var b in bosses)
                {
                    void One(string name, string part, Kind want)
                    {
                        var c = Part(b, part);
                        if (c == null) { Check($"{b.BossName} {name}: no {part}", false); return; }
                        gun.MuzzleDamage = 0.01f;
                        int before = Marker.ShownForTest;
                        Hit(c, 1f);
                        gun.MuzzleDamage = dmg0;
                        Check($"{b.BossName} {name} ({c.gameObject.name} x{c.DamageMultiplier:0.0}): {want} (got {Marker.Last}, hit-box {b.Health:0}, enemy side {b.EnemyHealth.health:0.###})", Marker.ShownForTest == before + 1 && Marker.Last == want);
                    }
                    One("chest", "spine_02", Kind.Normal);
                    b.EnemyHealth.health += 5000f; b.Health = 0f;   // 当たり判定側が空でも生きている
                    One("chest, hit-box empty", "spine_02", Kind.Normal);
                    One("head", "head", Kind.Head);
                    b.EnemyHealth.health = 0.001f;
                    One("kill", "spine_02", Kind.Kill);
                }
            }));
            s.Add(("cleanup", () => { try { File.Delete(force); } catch { } }));
            return s;
        }

        // 強い弾 1 発で倒す
        static List<(string, Action)> KillSteps(string name, string part, Kind want)
        {
            var s = new List<(string, Action)> { ("start endless run", () => Core.FindAll<Il2Cpp.ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()) };
            for (int i = 0; i < 18; i++) s.Add(("wait load", () => { }));
            s.Add((name, () =>
            {
                var le = Fresh(); var gun = Gun(); var c = le == null ? null : Part(le, part);
                if (c == null || gun == null) { Check($"real hit {name}: enemy {le != null} part {c != null} gun {gun != null}", false); return; }
                float dmg0 = gun.MuzzleDamage; gun.MuzzleDamage = 100000f;
                int before = Marker.ShownForTest;
                Hit(c, 1f);
                gun.MuzzleDamage = dmg0;
                Check($"real hit {name} ({c.gameObject.name} x{c.DamageMultiplier:0.0}): {want} (got {Marker.Last}, health {le.Health:0}, enemy side {le.EnemyHealth.health:0.##})",
                      Marker.ShownForTest == before + 1 && Marker.Last == want && Marker.ColorForTest.r > 0.9f && Marker.ColorForTest.g < 0.3f && Marker.OuterVisibleForTest == (want == Kind.HeadKill));
            }));
            return s;
        }

        // 敵の正面（8m・15m）に立ち、体の向きだけ胸へ向けて、敵に重ねたマーカーを撮る（当てない）
        static List<(string, Action)> ViewSteps()
        {
            var s = new List<(string, Action)> { ("hold", () => Marker.HoldForTest = true), ("start endless run", () => Core.FindAll<Il2Cpp.ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()) };
            for (int i = 0; i < 18; i++) s.Add(("wait load", () => { }));
            foreach (var r0 in new[] { 8f, 15f })
            {
                float r = r0;
                s.Add(($"place {r}m", () => { _enemy ??= Fresh(); Core.Log($"[hit] view at {r}m: placed={(_enemy != null && Place(_enemy, r))}"); }));
                s.Add(("wait", () => { }));
                foreach (var k in new[] { Kind.Normal, Kind.Head, Kind.Kill })
                {
                    var kind = k;
                    s.Add(($"show {kind}", () => Marker.Show(kind)));
                    s.Add(($"shot {kind}", () => Shot($"view_{r:0}m_{kind.ToString().ToLowerInvariant()}")));
                }
            }
            s.Add(("release", () => Marker.HoldForTest = false));
            return s;
        }

        static bool Place(Il2CppBallistics.LivingEntity le, float dist)
        {
            var fps = Refs.Fps; var cam = Camera.main;
            var chest = Part(le, "spine_02")?.transform.position ?? le.transform.position + Vector3.up * 1.3f;
            if (fps == null || cam == null) return false;
            for (int a = 0; a < 24; a++)
            {
                var p = le.transform.position + Quaternion.Euler(0, a * 15f, 0) * le.transform.forward * dist;
                if (!UnityEngine.AI.NavMesh.SamplePosition(p, out var hit, 1.5f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                if (Mathf.Abs(hit.position.y - le.transform.position.y) > 1f) continue;
                var eye = hit.position + Vector3.up * 1.6f;
                if (Physics.Linecast(eye, chest, out var h, ~0, QueryTriggerInteraction.Ignore) && !h.transform.IsChildOf(le.transform.root)) continue;
                var cc = fps.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;
                fps.transform.position = hit.position + Vector3.up * 0.05f;
                if (cc != null) cc.enabled = true;
                var flat = chest - hit.position; flat.y = 0;
                fps.transform.rotation = Quaternion.LookRotation(flat);
                return true;
            }
            return false;
        }

        static Il2CppBallistics.LivingEntityCollider Part(Il2CppBallistics.LivingEntity le, string name) =>
            le.GetComponentsInChildren<Il2CppBallistics.LivingEntityCollider>(true).FirstOrDefault(c => c.gameObject.name.ToLowerInvariant().StartsWith(name));

        static Il2CppBallistics.LivingEntity _enemy;
        static readonly HashSet<IntPtr> _used = new();
        static Il2CppBallistics.LivingEntity Fresh()
        {
            var le = Core.FindAll<Il2CppBallistics.LivingEntity>().FirstOrDefault(e => e.EnemyHealth != null && Hits.Alive(e) && e.gameObject.scene.IsValid() && Part(e, "head") != null && !_used.Contains(e.Pointer));
            if (le != null) _used.Add(le.Pointer);
            return le;
        }
        static Il2CppBallistics.Weapon Gun() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponentInChildren<Il2CppBallistics.Weapon>(true);

        // 弾の速さ（ダメージは 速さ / 最高の速さ に比例）を変えて、1 発の当たりを起こす
        static void Hit(Il2CppBallistics.LivingEntityCollider c, float speedRate)
        {
            var w = Gun();
            TestMode.Call(Il2CppBallistics.BulletHandler.instance, "BulletHit", w, c, null, Vector3.forward, c.transform.position, w.MaxBulletSpeed * speedRate, 0f);
        }

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)> { ("hold", () => Marker.HoldForTest = true) };
            foreach (var k in new[] { Kind.Normal, Kind.Weak, Kind.Head, Kind.Kill, Kind.HeadKill })
            {
                var kind = k;
                s.Add(($"show {kind}", () => Marker.Show(kind)));
                s.Add(($"shot {kind}", () =>
                {
                    Shot(kind.ToString().ToLowerInvariant());
                    bool kill = kind == Kind.Kill || kind == Kind.HeadKill, head = kind == Kind.Head || kind == Kind.HeadKill;
                    var c = Marker.ColorForTest;
                    string col = kill ? "red" : kind == Kind.Weak ? "blue" : "white";
                    bool colOk = kill ? c.r > 0.9f && c.g < 0.3f : kind == Kind.Weak ? c.b > 0.9f && c.r < 0.5f : c.r > 0.9f && c.g > 0.9f && c.b > 0.9f;
                    Check($"marker {kind}: {col}, {(head ? "double" : "single")} cross", Marker.VisibleForTest && colOk && Marker.OuterVisibleForTest == head);
                }));
            }
            s.Add(("rules", () =>
            {
                Check("hit marker is off at first for players, other mods on",
                      !Settings.OnAtFirstForPlayers("FPHitMarker") && Settings.OnAtFirstForPlayers("FPCustom") && Settings.OnAtFirstForPlayers("FPOptimize"));
                Check("classify: chest x1.3 = white, arm x0.4 = blue, head = double, kill = red",
                      Hits.Classify("spine_02", 1.3f, false) == Kind.Normal && Hits.Classify("lowerarm_l", 0.4f, false) == Kind.Weak && Hits.Classify("hand_r", 0.1f, false) == Kind.Weak
                      && Hits.Classify("head", 55f, false) == Kind.Head && Hits.Classify("lowerarm_l", 0.4f, true) == Kind.Kill && Hits.Classify("head", 7f, true) == Kind.HeadKill);
            }));
            s.Add(("start endless run", () => Core.FindAll<Il2Cpp.ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()));
            for (int i = 0; i < 18; i++) s.Add(("wait load", () => { }));
            s.Add(("find enemy", () =>
            {
                int n = Core.FindAll<Il2CppBallistics.LivingEntity>().Count(le => le.EnemyHealth != null && !le.IsDead && le.gameObject.scene.IsValid());
                _enemy = Fresh();
                Core.Log($"[hit] living enemies in the scene: {n}");
                Core.Log($"[hit] test enemy {_enemy?.gameObject.name} health {_enemy?.Health} gun {Gun()?.name}");
                Check("enemy and gun found", _enemy != null && Gun() != null);
            }));
            // 開始地点の敵はすぐ片付けられることがあるので、同じフレームで続けて当てる。
            //   腕・手・胸・頭は弱い弾、最後は敵側の体力だけ 0 にする弾（敵側の体力が先に 0 になって倒れたとき）。
            //   開始地点で動いている敵は 1 人なので、頭・腕で倒す確認は別のシナリオ（hitmarker_headkill / hitmarker_armkill）
            s.Add(("real hits", () =>
            {
                var gun = Gun();
                if (_enemy == null || !Hits.Alive(_enemy)) _enemy = Fresh();
                if (_enemy == null || gun == null) { Check($"real hits: enemy {_enemy != null} gun {gun != null}", false); return; }
                float dmg0 = gun.MuzzleDamage;
                void One(Il2CppBallistics.LivingEntity le, string name, string part, float dmg, Kind want, bool upgradeKill = false)
                {
                    var c = le == null ? null : Part(le, part);
                    if (c == null) { Check($"real hit {name}: no enemy or no {part}", false); return; }
                    gun.MuzzleDamage = dmg;
                    if (upgradeKill) le.EnemyHealth.health = 0.001f;
                    int before = Marker.ShownForTest; float hp = le.Health;
                    Hit(c, 1f);
                    gun.MuzzleDamage = dmg0;
                    Check($"real hit {name} ({c.gameObject.name} x{c.DamageMultiplier:0.0}): {want} (got {Marker.Last}, health {hp:0} -> {le.Health:0}, enemy side {le.EnemyHealth.health:0.###})",
                          Marker.ShownForTest == before + 1 && Marker.Last == want);
                }
                var a = _enemy;
                One(a, "arm", "lowerarm", 0.01f, Kind.Weak);
                One(a, "hand", "hand", 0.01f, Kind.Weak);
                One(a, "chest", "spine_02", 0.01f, Kind.Normal);
                One(a, "head", "head", 0.01f, Kind.Head);
                // 当たり判定側だけ 0 になっても敵は倒れていない（赤にしない）
                a.EnemyHealth.health = 1000f; a.Health = 0f;
                One(a, "hit-box health empty, enemy still up", "spine_02", 0.01f, Kind.Normal);
                One(a, "kill when the enemy side runs out", "spine_02", 0.01f, Kind.Kill, upgradeKill: true);
                int n = Marker.ShownForTest;
                gun.MuzzleDamage = 0.01f; Hit(Part(a, "spine_02"), 1f); gun.MuzzleDamage = dmg0;
                Check("no marker on a dead enemy", Marker.ShownForTest == n);
            }));
            s.Add(("shot", () => Shot("real_kill")));
            s.Add(("release", () => Marker.HoldForTest = false));
            return s;
        }
    }
}
