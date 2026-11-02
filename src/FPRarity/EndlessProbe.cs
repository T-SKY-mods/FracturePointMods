using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2Cpp;
using UnityEngine;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario endlessprobe）。拠点からエンドレスモードのランを始め、階層・ボス・敵の出現の仕組みを書き出す。
    // セーブは autotest.ps1 が前後で退避・復元する。
    public static class EndlessProbe
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        public static List<(string, Action)> Steps()
        {
            var steps = new List<(string, Action)>
            {
                ("start endless run", () =>
                {
                    var b = Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault();
                    Main.Log($"[endless] button found={b != null}");
                    b?.StartEndlessRun();
                }),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("probe 1", () => Probe("after start")));
            steps.Add(("enter elevator + move", () => EnterAndMove()));
            for (int i = 0; i < 14; i++) steps.Add(("wait", () => { }));
            steps.Add(("probe 2", () => Probe("boss floor")));
            steps.Add(("check spawn + kill main boss", () =>
            {
                if (!EndlessBoss.IsBossFloor) return;
                int wantG = EndlessBoss.ExtraGrunts(EndlessBoss.Floor), wantB = EndlessBoss.ExtraBossCount(EndlessBoss.Floor);
                Check($"extra grunts {EndlessBoss.ExtraGruntsSpawned}/{wantG}, extra bosses {EndlessBoss.ExtraBosses.Count}/{wantB}", EndlessBoss.ExtraGruntsSpawned == wantG && EndlessBoss.ExtraBosses.Count == wantB);
                var d = Checker();
                var bh = d?.BossHealth?.TryCast<Il2CppEnemyAI.EnemyHealth>();
                if (bh != null)
                {
                    var parts = string.Join(", ", bh.transform.parent.GetComponentsInChildren<Il2CppBallistics.LivingEntityCollider>(true).Select(c => $"{c.gameObject.name}:{c.DamageMultiplier:0.##}"));
                    Main.Log($"[endless] boss hp={bh.health} armor={(bh.Armor != null ? bh.Armor.name + "/" + bh.Armor.activeInHierarchy : "-")} headshot={bh.headshot} parts=[{parts}]");
                }
                foreach (var hb in UnityEngine.Object.FindObjectsOfType<HealthBar>()) Main.Log($"[endless] healthbar '{hb.gameObject.name}' active={hb.gameObject.activeInHierarchy} root='{hb.transform.root.name}'");
                if (bh != null)
                {
                    float want = EndlessBoss.BossHp;
                    Check($"boss hp toughened ({bh.health:0} = {want:0})", Mathf.Abs(bh.health - want) < 1f);
                    Check("boss head multiplier <= 4", bh.transform.parent.GetComponentsInChildren<Il2CppBallistics.LivingEntityCollider>(true).All(c => c.DamageMultiplier <= EndlessBoss.BossHeadMax + 0.01f));
                    Check("extra bosses toughened", EndlessBoss.ExtraBosses.All(e => Mathf.Abs(e.health - want) < 1f));
                    Check("boss health bar not drawn", UnityEngine.Object.FindObjectsOfType<HealthBar>().All(b => !b.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).Any(g => g.enabled)));
                }
                KillEnemy(bh);
            }));
            steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
            steps.Add(("doors closed while extra bosses alive", () =>
            {
                var d = Checker();
                Main.Log($"[endless] main boss hp={d?.BossHealth?.TryCast<Il2CppEnemyAI.EnemyHealth>()?.health} opened={d?.opened} extraAlive={EndlessBoss.ExtraBosses.Count(e => e != null && e.health > 0)}");
                if (EndlessBoss.ExtraBosses.Count > 0) Check("doors stay closed while extra bosses are alive", d != null && !d.opened);
                foreach (var e in EndlessBoss.ExtraBosses) KillEnemy(e);
            }));
            steps.Add(("wait", () => { })); steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
            steps.Add(("doors open after all bosses", () =>
            {
                var d = Checker();
                Main.Log($"[endless] opened={d?.opened} extra=" + string.Join(",", EndlessBoss.ExtraBosses.Select(e => e == null ? "null" : $"{e.health}/{e.gameObject.activeInHierarchy}")) + $" main={d?.BossHealth?.TryCast<Il2CppEnemyAI.EnemyHealth>()?.health}");
                Check("doors open after all bosses are dead", d != null && d.opened);
                Probe("after kills");
            }));
            return steps;
        }

        // 調査用（autotest -Scenario bossdmg）。ボス部屋を強制し、弾が当たったときと同じ入口（LivingEntity.OnHealthChanged）で
        // ボスにダメージを与え、体力が減るかを確かめる。強制用ファイルは最後に消す。
        public static List<(string, Action)> BossDamageSteps()
        {
            var steps = new List<(string, Action)>
            {
                ("force boss floor + start endless run", () =>
                {
                    File.WriteAllText(Path.Combine(Main.Dir, "endless_boss.txt"), "0");
                    Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
                }),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EnterAndMove()));
            for (int i = 0; i < 14; i++) steps.Add(("wait", () => { }));
            steps.Add(("wait for boss room", () => { }));
            steps.Add(("damage boss like a bullet", () =>
            {
                var d = Checker();
                var bh = d?.BossHealth?.TryCast<Il2CppEnemyAI.EnemyHealth>();
                var le = bh?.transform.parent?.GetComponentInChildren<Il2CppBallistics.LivingEntity>(true) ?? d?.LivingEntity;
                if (bh == null || le == null) { Check($"boss found (health={bh != null} entity={le != null})", false); return; }
                var bars = UnityEngine.Object.FindObjectsOfType<HealthBar>(true).Select(b => $"{b.gameObject.name}:active={b.gameObject.activeInHierarchy}");
                Main.Log($"[bossdmg] boss='{bh.transform.parent?.name}' enemyHp={bh.health} entityHp={le.Health} bossFlag={le.Boss} bars=[{string.Join(", ", bars)}]");
                float before = bh.health;
                for (int i = 0; i < 3; i++)
                {
                    try { le.OnHealthChanged(-100f); }
                    catch (Exception ex) { Main.Log($"[bossdmg] hit {i + 1} threw: {ex.GetType().Name}: {ex.Message}"); }
                    Main.Log($"[bossdmg] after hit {i + 1}: enemyHp={bh.health} entityHp={le.Health}");
                }
                Check($"boss loses health when hit (enemyHp {before:0} -> {bh.health:0})", bh.health <= before - 250f);
                var bar = UnityEngine.Object.FindObjectsOfType<HealthBar>().FirstOrDefault();
                bool drawn = bar != null && bar.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).Any(g => g.enabled && g.gameObject.activeInHierarchy);
                Check($"boss health bar not drawn (bar found={bar != null})", !drawn);
                ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, "bossdmg_after_hits.png"));
            }));
            steps.Add(("cleanup", () =>
            {
                try { File.Delete(Path.Combine(Main.Dir, "endless_boss.txt")); } catch { }
                Probe("bossdmg end");
            }));
            return steps;
        }

        // 調査用（autotest -Scenario bossmulti）。ボスが 4体（元 1 + 増加 3）出る階を強制し、全員に弾と同じ入口でダメージを与えて
        // 体力が減ること、倒せること、全員倒すと扉が開くことを確かめる。
        public static List<(string, Action)> BossMultiSteps()
        {
            var steps = new List<(string, Action)>
            {
                ("force boss floor 50 + start endless run", () =>
                {
                    File.WriteAllText(Path.Combine(Main.Dir, "endless_boss.txt"), "50");
                    Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
                }),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EnterAndMove()));
            for (int i = 0; i < 15; i++) steps.Add(("wait", () => { }));
            steps.Add(("hit every boss", () =>
            {
                var bosses = AllBosses();
                Check($"bosses in the room {bosses.Count} (= 1 + {EndlessBoss.ExtraBossCount(EndlessBoss.Floor)})", bosses.Count == 1 + EndlessBoss.ExtraBossCount(EndlessBoss.Floor));
                foreach (var (name, bh, le) in bosses)
                {
                    float before = bh.health;
                    for (int i = 0; i < 3; i++)
                    {
                        try { le.OnHealthChanged(-100f); }
                        catch (Exception ex) { Main.Log($"[bossmulti] '{name}' hit threw: {ex.GetType().Name}: {ex.Message.Split((char)10)[0]}"); }
                    }
                    Check($"'{name}' loses health when hit ({before:0} -> {bh.health:0})", bh.health <= before - 250f);
                }
            }));
            steps.Add(("finish every boss", () =>
            {
                foreach (var (name, bh, le) in AllBosses())
                {
                    try { le.OnHealthChanged(-(bh.health + 50f)); }
                    catch (Exception ex) { Main.Log($"[bossmulti] '{name}' final hit threw: {ex.GetType().Name}: {ex.Message.Split((char)10)[0]}"); }
                }
            }));
            for (int i = 0; i < 4; i++) steps.Add(("wait", () => { }));
            steps.Add(("check deaths + doors", () =>
            {
                foreach (var (name, bh, le) in AllBosses())
                    // dead（倒れた印）は Steam の実績解除の後に立つので、Steam が起動していないと立たない。体力で判定する
                    Check($"'{name}' died (hp={bh.health:0} dead={bh.dead})", bh.health <= 0f);
                var d = Checker();
                Check($"boss room doors open after all bosses died (opened={d?.opened})", d != null && d.opened);
                try { File.Delete(Path.Combine(Main.Dir, "endless_boss.txt")); } catch { }
            }));
            return steps;
        }

        // 紹介用（autotest -Scenario bossshot）。ボスが 4体いる階で、ボスの前にプレイヤーを置いてスクリーンショットを撮る。
        public static List<(string, Action)> BossShotSteps()
        {
            var steps = new List<(string, Action)>
            {
                ("force boss floor 50 + start endless run", () =>
                {
                    File.WriteAllText(Path.Combine(Main.Dir, "endless_boss.txt"), "50");
                    Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
                }),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EnterAndMove()));
            for (int i = 0; i < 15; i++) steps.Add(("wait / pick card", PickCard));
            steps.Add(("place player in front of the bosses", PlaceInFrontOfBosses));
            steps.Add(("burst of screenshots", () => MelonLoader.MelonCoroutines.Start(Burst())));
            for (int i = 0; i < 8; i++) steps.Add(("wait", () => { }));
            steps.Add(("cleanup", () => { try { File.Delete(Path.Combine(Main.Dir, "endless_boss.txt")); } catch { } }));
            return steps;
        }

        static System.Collections.IEnumerator Burst()
        {
            Directory.CreateDirectory(ShotDir);
            for (int n = 0; n < 16; n++)
            {
                yield return new WaitForSeconds(0.3f);
                var alive = AllBosses().Count(b => b.Item2.health > 0f);
                Main.Log($"[bossshot] shot {n}: bosses alive={alive}");
                ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"bossshot_{n}.png"));
            }
        }

        internal static void PickCard()
        {
            var c = UnityEngine.Object.FindObjectsOfType<CardsManager>().FirstOrDefault();
            if (c == null || c.CanvasGroup == null || c.CanvasGroup.alpha < 0.5f || c.IsPicked) return;
            var b = c.CardParent.GetComponentsInChildren<UnityEngine.UI.Button>().FirstOrDefault(x => x.interactable);
            Main.Log($"[bossshot] picking a card (button={b != null})");
            b?.onClick?.Invoke();
        }

        // ボスたちの中心から、見通しのよい 7〜9m 手前に立たせ、ボスの方を向かせる
        static void PlaceInFrontOfBosses() => PlaceInFrontOfBossesForTest(killGrunts: true);

        internal static void PlaceInFrontOfBossesForTest(bool killGrunts)
        {
            var bossList = AllBosses();
            var bosses = bossList.Select(b => b.Item2.transform.position).ToList();
            var bossRoots = bossList.Select(b => (b.Item2.transform.parent != null ? b.Item2.transform.parent : b.Item2.transform)).ToList();
            var fps = Refs.Fps;
            if (bosses.Count == 0 || fps == null) { Main.Log("[bossshot] no bosses / player"); return; }
            var center = bosses.Aggregate(Vector3.zero, (a, b) => a + b) / bosses.Count;
            // 雑魚はボスが見えるように先に倒しておく（弾と同じ入口でダメージ）
            var bossSet = new HashSet<IntPtr>(AllBosses().Select(b => b.Item2.Pointer));
            foreach (var e in Main.FindAll<Il2CppEnemyAI.EnemyHealth>().Where(e => killGrunts && e.health > 0f && e.gameObject.activeInHierarchy && !bossSet.Contains(e.Pointer)))
            {
                var le = (e.transform.parent != null ? e.transform.parent : e.transform).GetComponentInChildren<Il2CppBallistics.LivingEntity>(true);
                try { if (le != null && Vector3.Distance(e.transform.position, center) < 40f) le.OnHealthChanged(-(e.health + 50f)); } catch { }
            }
            Vector3 best = Vector3.zero; int bestSeen = -1;
            for (int a = 0; a < 16; a++)
                foreach (var r in new[] { 10f, 12f, 9f, 14f })
                {
                    var p = center + Quaternion.Euler(0, a * 22.5f, 0) * Vector3.forward * r;
                    if (!UnityEngine.AI.NavMesh.SamplePosition(p, out var hit, 1.5f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                    var eye = hit.position + Vector3.up * 1.6f;
                    int seen = 0;
                    for (int k = 0; k < bosses.Count; k++)
                        if (!Physics.Linecast(eye, bosses[k] + Vector3.up * 1.3f, out var h) || h.transform.IsChildOf(bossRoots[k])) seen++;
                    if (seen > bestSeen) { bestSeen = seen; best = hit.position; }
                }
            var cc = fps.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            fps.transform.position = best + Vector3.up * 0.1f;
            var dir = center - best; dir.y = 0;
            fps.transform.rotation = Quaternion.LookRotation(dir);
            if (cc != null) cc.enabled = true;
            Main.Log($"[bossshot] player at {best} sees {bestSeen}/{bosses.Count} bosses");
            foreach (var ab in Main.FindAll<ActivateBosses>()) { try { ab.Activate(); } catch (Exception e) { Main.Log("[bossshot] activate failed " + e.Message); } }
        }

        static List<(string, Il2CppEnemyAI.EnemyHealth, Il2CppBallistics.LivingEntity)> AllBosses()
        {
            var list = new List<(string, Il2CppEnemyAI.EnemyHealth, Il2CppBallistics.LivingEntity)>();
            var main = Checker()?.BossHealth?.TryCast<Il2CppEnemyAI.EnemyHealth>();
            foreach (var bh in new[] { main }.Concat(EndlessBoss.ExtraBosses))
            {
                if (bh == null) continue;
                var root = bh.transform.parent != null ? bh.transform.parent : bh.transform;
                var le = root.GetComponentInChildren<Il2CppBallistics.LivingEntity>(true);
                if (le != null) list.Add(($"{root.name}#{list.Count}", bh, le));
            }
            return list;
        }

        // プレイヤーをエレベーターの中に移して、次の階へ動かす
        public static void EnterAndMove()
        {
            var el = Main.FindAll<LevelElevator>().FirstOrDefault();
            var fps = Refs.Fps;
            if (el == null || fps == null) { Main.Log($"[endless] elevator={el != null} fps={fps != null}"); return; }
            var col = el.EnterCollider;
            var pos = col != null ? col.bounds.center : el.transform.position;
            var cc = fps.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            fps.transform.position = pos;
            if (cc != null) cc.enabled = true;
            Main.Log($"[endless] player moved to {pos} (elevator at {el.transform.position}); MoveToNextFloor");
            el.MoveToNextFloor();
        }

        // ゲーム本来の倒れる処理で倒す（ダメージを与えてから Kill）
        static void KillEnemy(Il2CppEnemyAI.EnemyHealth e)
        {
            if (e == null) return;
            try { e.TakeDamage(e.health + 1f); } catch (Exception ex) { Main.Log("[endless] damage failed " + ex.Message); }
            try { if (e.health > 0f || true) e.Kill(); } catch (Exception ex) { Main.Log("[endless] kill failed " + ex.Message); }
        }

        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static BossfightDoorsChecker Checker() => UnityEngine.Object.FindObjectsOfType<BossfightDoorsChecker>().FirstOrDefault(d => d.BossHealth != null);

        public static void Probe(string when)
        {
            var sb = new StringBuilder($"===== ENDLESS PROBE ({when}) scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} =====\n");
            try
            {
                foreach (var el in Main.FindAll<LevelElevator>())
                    sb.AppendLine($"  elevator '{el.gameObject.name}' floor={el.CurrentFloor} endless={el.endlessMode} builder={el.LevelBuilder?.gameObject.name} level='{el.levelName}' timer={el.enableTimer} clip={el.clipPlane} msg='{el.phoneMessage}'");
                foreach (var ds in Main.FindAll<DifficultyStopwatch>())
                {
                    sb.Append($"  stopwatch '{ds.gameObject.name}' diff={ds.CurrentDifficulty} wave={ds.currentWaveIndex} custom={ds.isCustomMode} spawners={ds.enemySpawners?.Count} waves=");
                    var w = ds.Waves;
                    for (int i = 0; w != null && i < w.Length; i++) sb.Append($"[d{w[i].delayBeforeWave:0} n{w[i].enemyCount} i{w[i].spawnInterval:0.0} x{w[i].damageMultiplier:0.00}]");
                    sb.AppendLine();
                }
                var spawners = Main.FindAll<EnemySpawner>().ToList();
                sb.AppendLine($"  spawners={spawners.Count} types=" + string.Join(",", spawners.GroupBy(s => s.Type.ToString()).Select(g => $"{g.Key}:{g.Count()}")));
                var enemies = Main.FindAll<Il2CppEnemyAI.EnemyHealth>().ToList();
                sb.AppendLine($"  enemies={enemies.Count} alive={enemies.Count(e => e.health > 0 && e.gameObject.activeInHierarchy)}");
                foreach (var e in enemies.Take(40))
                    sb.AppendLine($"    '{e.gameObject.name}' hp={e.health} type={e.enemyType} active={e.gameObject.activeInHierarchy} parent='{e.transform.parent?.name}' root='{e.transform.root.name}'");
                foreach (var d in Main.FindAll<BossfightDoorsChecker>())
                    sb.AppendLine($"  bossDoors '{d.gameObject.name}' active={d.gameObject.activeInHierarchy} boss={(d.BossHealth != null ? d.BossHealth.gameObject.name : "-")} living={(d.LivingEntity != null ? d.LivingEntity.gameObject.name : "-")} root='{d.transform.root.name}'");
                sb.AppendLine($"  endlessBoss isBoss={EndlessBoss.IsBossFloor} floor={EndlessBoss.Floor}");
                foreach (var a in Main.FindAll<ActivateBosses>())
                    sb.AppendLine($"  activateBosses '{a.gameObject.name}' active={a.gameObject.activeInHierarchy} root='{a.transform.root.name}'");
                foreach (var go in UnityEngine.Object.FindObjectsOfType<Transform>().Where(t => t.parent == null).Select(t => t.gameObject))
                    if (go.name.StartsWith("DungeonSnap") || go.name.Contains("Dungeon")) sb.AppendLine($"  root '{go.name}' active={go.activeSelf} children={go.transform.childCount}");
            }
            catch (Exception e) { sb.AppendLine("  probe failed: " + e); }
            Main.Log(sb.ToString());
            Directory.CreateDirectory(ShotDir);
            ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"endless_{when.Replace(' ', '_')}.png"));
        }
    }
}
