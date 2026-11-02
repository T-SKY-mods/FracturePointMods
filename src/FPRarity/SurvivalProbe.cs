using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario survivalprobe）。サバイバルモードを作れるかを確かめるため、
    // 拠点（所持品・端末の画面・自販機）とエンドレスの数階（敵の種類・死体・落とし物・調べられる物）を書き出す。
    // 最終ボスの階は調べない。セーブは autotest.ps1 が前後で退避・復元する。
    public static class SurvivalProbe
    {
        static string ShotDir => System.IO.Path.Combine(Main.Dir, "shots");
        static string ForceFile => System.IO.Path.Combine(Main.Dir, "endless_boss.txt");

        public static List<(string, Action)> Steps()
        {
            var steps = new List<(string, Action)>
            {
                ("hub: enemy data + prefabs", DumpEnemyAssets),
                ("hub: inventory", () => DumpInventory("hub")),
                ("hub: tablet ui", DumpTablet),
                ("hub: interactables", () => DumpInteractables("hub")),
                ("start endless run", () => Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("run start: inventory", () => DumpInventory("run start")));
            for (int f = 1; f <= 4; f++)
            {
                int floor = f;
                steps.Add(($"move to floor {floor}", () =>
                {
                    try { File.Delete(ForceFile); } catch { }
                    if (floor == 4) File.WriteAllText(ForceFile, "0");   // 最後はボス部屋（武器箱を探す）
                    EndlessProbe.EnterAndMove();
                }));
                for (int i = 0; i < 14; i++) steps.Add(("wait / pick card", EndlessProbe.PickCard));
                steps.Add(($"floor {floor}: probe", () => { DumpFloor(floor); DumpInteractables($"floor {floor}"); }));
                steps.Add(($"floor {floor}: kill one enemy", KillOne));
                for (int i = 0; i < 4; i++) steps.Add(("wait", () => { }));
                steps.Add(($"floor {floor}: after kill", AfterKill));
            }
            steps.Add(("cleanup", () => { try { File.Delete(ForceFile); } catch { } }));
            return steps;
        }

        // 2 回目（autotest -Scenario survivalprobe2）。ボス部屋で始め、敵のデータとボスを倒した後に出る物（武器箱）を調べる。
        public static List<(string, Action)> BossSteps()
        {
            var steps = new List<(string, Action)>
            {
                ("force boss floor + start endless run", () =>
                {
                    File.WriteAllText(ForceFile, "0");
                    Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
                }),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EndlessProbe.EnterAndMove()));
            for (int i = 0; i < 15; i++) steps.Add(("wait / pick card", EndlessProbe.PickCard));
            steps.Add(("boss floor: enemy data", DumpEnemyAssets));
            steps.Add(("boss floor: probe", () => { DumpFloor(0); DumpInteractables("boss floor before kill"); }));
            steps.Add(("kill all enemies", () =>
            {
                foreach (var e in Main.FindAll<Il2CppEnemyAI.EnemyHealth>().Where(e => e.health > 0 && e.gameObject.activeInHierarchy).ToList())
                {
                    var le = Root(e).GetComponentInChildren<Il2CppBallistics.LivingEntity>(true);
                    try { le?.OnHealthChanged(-(e.health + 50f)); } catch { }
                }
            }));
            for (int i = 0; i < 6; i++) steps.Add(("wait", () => { }));
            steps.Add(("boss floor: after kills", () => { DumpFloor(1); DumpInteractables("boss floor after kill"); }));
            steps.Add(("cleanup", () => { try { File.Delete(ForceFile); } catch { } }));
            return steps;
        }

        // 3 回目（autotest -Scenario survivalmap）。UserData/FPRarity/survival_floor.txt の番号の通常ランの階を作り、
        // マップの広さ・部屋・敵・ボス・エレベーターを書き出し、真上からの画像を撮る。
        public static List<(string, Action)> MapSteps()
        {
            int n = 12;
            try { n = int.Parse(File.ReadAllText(System.IO.Path.Combine(Main.Dir, "survival_floor.txt")).Trim()); } catch { }
            var steps = new List<(string, Action)>
            {
                ($"force normal floor {n} + start endless run", () =>
                {
                    EndlessBoss.ForceNormalSetupForTest = n;
                    Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
                }),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EndlessProbe.EnterAndMove()));
            for (int i = 0; i < 18; i++) steps.Add(("wait / pick card", EndlessProbe.PickCard));
            steps.Add(("map probe", () => DumpMap(n)));
            steps.Add(("top view", () => TopView(n)));
            steps.Add(("enemy data", DumpEnemyAssets));
            steps.Add(("interactables", () => DumpInteractables($"normal floor {n}")));
            steps.Add(("room structure", DumpRooms));
            steps.Add(("around the elevator", DumpAroundElevator));
            foreach (var (name, pos, yaw) in new[] { ("elev_out", new Vector3(-11.8f, 1f, 0.5f), 90f), ("lobby_back", new Vector3(-4f, 1f, 0.5f), 270f), ("lobby_out", new Vector3(-4f, 1f, 0.5f), 90f), ("roof_back", new Vector3(8f, 1f, 0.5f), 270f) })
            {
                steps.Add(($"view {name}", () => { Teleport(pos); var f = Refs.Fps; if (f != null) f.transform.rotation = Quaternion.Euler(0, yaw, 0); }));
                steps.Add(($"shot {name}", () => ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(ShotDir, $"survival_{name}.png"))));
            }
            steps.Add(("enemies later", () => { DumpFloor(n * 10 + 1); }));
            steps.Add(("cleanup", () => EndlessBoss.ForceNormalSetupForTest = -1));
            return steps;
        }

        // 生成された階の見た目の範囲を 2m 間隔で調べ、歩ける場所（NavMesh）の範囲と面積を出す
        static Bounds NavBounds(out float area)
        {
            var level = new Bounds(); bool any = false;
            foreach (var gl in UnityEngine.Object.FindObjectsOfType<Transform>().Where(t => t.name == "GeneratedLevel" || (t.parent == null && t.name.StartsWith("DungeonSnap"))))
                foreach (var r in gl.GetComponentsInChildren<Renderer>())
                {
                    if (!any) { level = r.bounds; any = true; } else level.Encapsulate(r.bounds);
                }
            area = 0;
            var b = new Bounds(); bool hitAny = false;
            if (!any) return b;
            const float step = 2f;
            for (float x = level.min.x; x <= level.max.x; x += step)
                for (float z = level.min.z; z <= level.max.z; z += step)
                    for (float y = level.min.y; y <= level.max.y; y += 3f)
                    {
                        if (!UnityEngine.AI.NavMesh.SamplePosition(new Vector3(x, y, z), out var hit, 1.2f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                        if (Mathf.Abs(hit.position.x - x) > step / 2 || Mathf.Abs(hit.position.z - z) > step / 2) continue;
                        if (!hitAny) { b = new Bounds(hit.position, Vector3.zero); hitAny = true; } else b.Encapsulate(hit.position);
                        area += step * step;
                        break;
                    }
            return b;
        }

        static void DumpMap(int n)
        {
            var sb = new StringBuilder($"===== SURVIVAL PROBE: map (normal floor {n}) scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} =====\n");
            try
            {
                var b = NavBounds(out var area);
                sb.AppendLine($"  navmesh bounds center={b.center} size={b.size} walkable={area:0}m2");
                var fps = Refs.Fps;
                sb.AppendLine($"  player at {(fps != null ? fps.transform.position.ToString() : "-")}");
                foreach (var el in Main.FindAll<LevelElevator>())
                    sb.AppendLine($"  elevator '{el.gameObject.name}' at={el.transform.position} floor={el.CurrentFloor} level='{el.levelName}' enter={(el.EnterCollider != null ? el.EnterCollider.bounds.center.ToString() : "-")}");
                foreach (var eb in Main.FindAll<ElevatorButton>()) sb.AppendLine($"  elevatorButton '{Path(eb.transform)}' at={eb.transform.position}");
                foreach (var go in UnityEngine.Object.FindObjectsOfType<Transform>().Where(t => t.parent == null).Select(t => t.gameObject))
                    if (go.name.StartsWith("DungeonSnap") || go.name.Contains("Level")) sb.AppendLine($"  root '{go.name}' active={go.activeSelf} children={go.transform.childCount}");
                foreach (var gl in UnityEngine.Object.FindObjectsOfType<Transform>().Where(t => t.name == "GeneratedLevel"))
                    for (int i = 0; i < gl.childCount; i++)
                    {
                        var r = gl.GetChild(i);
                        var rb = new Bounds(r.position, Vector3.zero);
                        foreach (var rend in r.GetComponentsInChildren<Renderer>()) rb.Encapsulate(rend.bounds);
                        sb.AppendLine($"    room '{r.name}' active={r.gameObject.activeSelf} center={rb.center} size={rb.size}");
                    }
                var enemies = Main.FindAll<Il2CppEnemyAI.EnemyHealth>().ToList();
                sb.AppendLine($"  enemies={enemies.Count}");
                foreach (var e in enemies)
                    sb.AppendLine($"    '{Root(e).name}' type={e.enemyType} hp={e.health} active={e.gameObject.activeInHierarchy} at={e.transform.position} root='{e.transform.root.name}'");
                foreach (var s in Main.FindAll<EnemySpawner>()) sb.AppendLine($"  spawner type={s.Type} at={s.transform.position}");
                foreach (var d in Main.FindAll<BossfightDoorsChecker>())
                    sb.AppendLine($"  bossDoors '{Path(d.transform)}' boss={(d.BossHealth != null ? d.BossHealth.gameObject.name : "-")} at={d.transform.position}");
                foreach (var f in Main.FindAll<FinalBossBehaviour>())
                    sb.AppendLine($"  finalBoss '{Path(f.transform)}' at={f.transform.position} weapon={(f.FireWeapon != null ? f.FireWeapon.gameObject.name : "-")} toDisable={f.ToDisable?.Count}");
                foreach (var a in Main.FindAll<ActivateBosses>()) sb.AppendLine($"  activateBosses '{Path(a.transform)}' at={a.transform.position}");
                foreach (var ds in Main.FindAll<DifficultyStopwatch>()) sb.AppendLine($"  stopwatch spawners={ds.enemySpawners?.Count} custom={ds.isCustomMode}");
                foreach (var hm in Main.FindAll<HealthManager>().Where(h => h.TryCast<Il2CppEnemyAI.EnemyHealth>() == null).Take(20))
                    sb.AppendLine($"  otherHealth '{Path(hm.transform)}' type={hm.GetIl2CppType().Name}");
            }
            catch (Exception e) { sb.AppendLine("  failed: " + e); }
            Log(sb);
            Directory.CreateDirectory(ShotDir);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(ShotDir, $"survival_map{n}_player.png"));
        }

        // 歩ける範囲の真上から正射影で撮る（屋根を避けるため、近くの切り取り面を床の少し上に置く）
        static void TopView(int n)
        {
            try
            {
                var b = NavBounds(out _);
                var go = new GameObject("SurvivalProbeTopCam");
                var cam = go.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = Mathf.Max(b.size.x, b.size.z) * 0.55f + 2f;
                go.transform.position = new Vector3(b.center.x, b.max.y + 2.6f, b.center.z);
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = b.size.y + 40f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                var rt = new RenderTexture(1600, 1600, 24);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(1600, 1600, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1600, 1600), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                cam.targetTexture = null;
                Directory.CreateDirectory(ShotDir);
                File.WriteAllBytes(System.IO.Path.Combine(ShotDir, $"survival_map{n}_top.png"), ImageConversion.EncodeToPNG(tex).ToArray());
                UnityEngine.Object.Destroy(go); UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(tex);
                Main.Log($"[survival] top view saved (center={b.center} size={b.size} ortho={cam.orthographicSize})");
            }
            catch (Exception e) { Main.Log("[survival] top view failed: " + e); }
        }

        static void DumpAroundElevator()
        {
            var sb = new StringBuilder("===== SURVIVAL PROBE: around the elevator =====\n");
            try
            {
                var el = Main.FindAll<LevelElevator>().FirstOrDefault(e => e.gameObject.name == "LevelElevator_0");
                var c = el != null ? el.transform.position : Vector3.zero;
                foreach (var d in Main.FindAll<DoorInteraction>().Where(d => Vector3.Distance(d.transform.position, c) < 30f))
                    sb.AppendLine($"  doorInteraction '{Path(d.transform)}' at={d.transform.position} door={(d.door != null ? d.door.gameObject.name : "-")} open={d.door?.isOpen}");
                foreach (var d in Main.FindAll<Door>().Where(d => Vector3.Distance(d.transform.position, c) < 30f))
                    sb.AppendLine($"  door '{Path(d.transform)}' at={d.transform.position} open={d.isOpen}");
                foreach (var l in Main.FindAll<SpawnableLoot>()) sb.AppendLine($"  spawnableLoot '{l.name}' n={l.lootDrop?.Count}");
                var inv = Refs.Inv;
                var w1 = Main.FindAll<SpawnableLoot>().FirstOrDefault(l => l.name == "Weapons 1") ?? Resources.FindObjectsOfTypeAll<SpawnableLoot>().FirstOrDefault(l => l.name == "Weapons 1");
                sb.AppendLine($"  'Weapons 1' found={w1 != null}");
                if (w1 != null && inv?.WeaponData?.Weapons != null)
                    foreach (var d in w1.lootDrop.ToArray())
                    {
                        var wl = inv.WeaponData.Weapons;
                        var byIdx = d.ItemIndex >= 0 && d.ItemIndex < wl.Count ? wl[d.ItemIndex] : null;
                        sb.AppendLine($"    drop {d.dropType} idx={d.ItemIndex} -> byIndex id={byIdx?.Id} '{byIdx?.GetName()}' type={byIdx?.Type}");
                    }
                // 1 本の雑魚を出してみる（場所・親・敵の種類）
                var sp = Main.FindAll<EnemySpawner>().FirstOrDefault();
                if (sp != null && Refs.Fps != null)
                {
                    var before = Main.FindAll<Il2CppEnemyAI.EnemyHealth>().Select(e => e.Pointer).ToHashSet();
                    sp.transform.position = new Vector3(40f, 1.1f, 20f);
                    sp.Type = Enemy.EnemyType.OfficeSecurity;
                    var wps = new Il2CppSystem.Collections.Generic.List<Transform>();
                    foreach (var s in Main.FindAll<EnemySpawner>()) wps.Add(s.transform);
                    sp.Spawn(1f, 1, Refs.Fps.transform, wps);
                    foreach (var e in Main.FindAll<Il2CppEnemyAI.EnemyHealth>().Where(e => !before.Contains(e.Pointer)))
                        sb.AppendLine($"  spawned '{Root(e).name}' type={e.enemyType} at={e.transform.position} parent='{Root(e).parent?.name}' chance={Root(e).GetComponentInChildren<LootableChanceItems>(true) != null}");
                }
            }
            catch (Exception e) { sb.AppendLine("  failed: " + e); }
            Log(sb);
        }

        static void Teleport(Vector3 p)
        {
            var fps = Refs.Fps; if (fps == null) return;
            var cc = fps.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            fps.transform.position = p + Vector3.up * 0.1f;
            if (cc != null) cc.enabled = true;
            Main.Log($"[survival] player teleported to {p}");
        }

        // 生成された部屋ごとに、使われているスクリプトの種類と数、大きな子の構成を書き出す
        static void DumpRooms()
        {
            var sb = new StringBuilder("===== SURVIVAL PROBE: rooms =====\n");
            try
            {
                var skip = new HashSet<string> { "MeshRenderer", "MeshFilter", "BoxCollider", "MeshCollider", "LODGroup", "Light", "SkinnedMeshRenderer", "ReflectionProbe", "CapsuleCollider", "SphereCollider", "Rigidbody", "AudioSource", "ParticleSystem", "ParticleSystemRenderer", "UniversalAdditionalLightData", "DoorInteraction", "LootableItem", "LootableChanceItems", "DefaultBallisticObject", "ImpactObjectRigidbody", "ExcludeReflections", "SpatialAudioCuller", "Animator", "Animation", "NavMeshObstacle", "OcclusionPortal" };
                foreach (var gl in UnityEngine.Object.FindObjectsOfType<Transform>().Where(t => t.name == "GeneratedLevel"))
                    for (int i = 0; i < gl.childCount; i++)
                    {
                        var r = gl.GetChild(i);
                        var types = r.GetComponentsInChildren<Component>(true).Where(c => c != null).Select(c => c.GetIl2CppType().Name).Where(t => !skip.Contains(t) && t != "Transform").GroupBy(t => t).OrderByDescending(g => g.Count());
                        sb.AppendLine($"  room '{r.name}': {string.Join(", ", types.Select(g => $"{g.Key}:{g.Count()}"))}");
                        if (r.childCount > 0 && r.GetComponentsInChildren<Renderer>().Length > 50) Tree(sb, r, 0, 2);
                    }
                foreach (var t in new[] { "FinalBossBehaviour", "ActivateBosses", "EventOnInteraction", "OnDeathEvent", "GiftAfterDeath", "BtrHealth", "RobotAI", "EnemiesWaves", "DifficultyStopwatch", "Helicopter" })
                    foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(m => m != null && m.gameObject.scene.IsValid() && m.GetIl2CppType().Name.Contains(t)))
                        sb.AppendLine($"  {mb.GetIl2CppType().Name} '{Path(mb.transform)}' active={mb.gameObject.activeInHierarchy} at={mb.transform.position}");
            }
            catch (Exception e) { sb.AppendLine("  failed: " + e); }
            Log(sb);
        }

        static void Log(StringBuilder sb) => Main.Log(sb.ToString());

        static void DumpEnemyAssets()
        {
            var sb = new StringBuilder("===== SURVIVAL PROBE: enemy assets =====\n");
            try
            {
                foreach (var ed in Resources.FindObjectsOfTypeAll<EnemyData>())
                {
                    sb.AppendLine($"  EnemyData '{ed.name}' mine={(ed.APMinePrefab != null ? ed.APMinePrefab.name : "-")}");
                    if (ed.enemies == null) continue;
                    foreach (var e in ed.enemies)
                        sb.AppendLine($"    type={e.type} prefabs=[{(e.prefabs == null ? "" : string.Join(", ", Enumerable.Range(0, e.prefabs.Count).Select(i => e.prefabs[i] != null ? e.prefabs[i].name : "null")))}]");
                }
                // 読み込まれている敵のプレハブ（シーンに置かれていないもの）
                var prefabs = Resources.FindObjectsOfTypeAll<Il2CppEnemyAI.EnemyHealth>().Where(h => !h.gameObject.scene.IsValid()).ToList();
                sb.AppendLine($"  enemy prefabs loaded: {prefabs.Count}");
                foreach (var h in prefabs)
                    sb.AppendLine($"    '{h.transform.root.name}' type={h.enemyType} hp={h.health} death={h.deathType}");
            }
            catch (Exception e) { sb.AppendLine("  failed: " + e); }
            Log(sb);
        }

        static void DumpInventory(string when)
        {
            var sb = new StringBuilder($"===== SURVIVAL PROBE: inventory ({when}) =====\n");
            try
            {
                var inv = Refs.Inv;
                if (inv == null) { sb.AppendLine("  no inventory"); Log(sb); return; }
                sb.AppendLine($"  dollars={inv.DollarsCount} roubles={inv.RoublesCount} bank={inv.DollarsInBank} maxBig={inv.MaxBigWeapons} maxSmall={inv.MaxSmallWeapons} maxWeight={inv.MaxWeight}");
                sb.AppendLine($"  ammoKinds={inv.AmmoStored?.Count} mags={inv.MagStored?.Count} lootKinds={inv.LootItems?.Count} healKinds={inv.HealthItems?.Count} grenadeKinds={inv.GrenadeItems?.Count} armors={inv.ArmorItems?.Count}");
                sb.AppendLine($"  helmet={(inv.HelmetEquipped != null ? "id" + inv.HelmetEquipped.Id : "-")} vest={(inv.VestEquipped != null ? "id" + inv.VestEquipped.Id : "-")} nvg={inv.hasNVG}");
                if (inv.AmmoStored != null) foreach (var kv in inv.AmmoStored) sb.AppendLine($"    ammo cal={kv.Key?.Caliber} idx={kv.Key?.Index} x{kv.Value}");
                if (inv.MagStored != null) foreach (var m in inv.MagStored) sb.AppendLine($"    mag {m?.WeaponName} {m?.AmmoCount}/{m?.MaxAmmoCount}");
                var fps = Refs.Fps;
                if (fps?.InstantiatedWeapons != null)
                    foreach (var w in fps.InstantiatedWeapons) sb.AppendLine($"    weapon '{w?.gameObject.name}'");
                sb.AppendLine($"  tracker held={Tracker.Held.Count}");
            }
            catch (Exception e) { sb.AppendLine("  failed: " + e); }
            Log(sb);
        }

        static void DumpTablet()
        {
            var sb = new StringBuilder("===== SURVIVAL PROBE: endless tablet =====\n");
            try
            {
                foreach (var t in Main.FindAll<EndlessModeTablet>())
                    sb.AppendLine($"  tablet '{Path(t.transform)}' focus={(t.FocusPosition != null ? t.FocusPosition.name : "-")} onSpace={t.EventOnSpace?.GetPersistentEventCount()}");
                foreach (var m in Main.FindAll<EndlessModeTabletManager>())
                {
                    sb.AppendLine($"  manager '{Path(m.transform)}' best={(m.PersonalBestObject != null ? m.PersonalBestObject.name : "-")} list={(m.ListParent != null ? m.ListParent.name : "-")}");
                    var canvas = m.GetComponentInParent<Canvas>(true);
                    Tree(sb, canvas != null ? canvas.transform : m.transform, 0, 7);
                }
                foreach (var b in Main.FindAll<ButtonStartEndlessMode>())
                    sb.AppendLine($"  startButton '{Path(b.transform)}'");
            }
            catch (Exception e) { sb.AppendLine("  failed: " + e); }
            Log(sb);
        }

        static void Tree(StringBuilder sb, Transform t, int depth, int max)
        {
            var comps = t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetIl2CppType().Name);
            var tmp = t.GetComponent<Il2CppTMPro.TMP_Text>();
            var rt = t as RectTransform;
            sb.AppendLine($"{new string(' ', 4 + depth * 2)}'{t.name}' act={t.gameObject.activeSelf} [{string.Join(",", comps)}]{(rt != null ? $" pos={rt.anchoredPosition} size={rt.sizeDelta}" : "")}{(tmp != null ? $" text='{Short(tmp.text)}'" : "")}");
            if (depth >= max) return;
            for (int i = 0; i < t.childCount; i++)
            {
                if (i >= 12) { sb.AppendLine($"{new string(' ', 6 + depth * 2)}... {t.childCount - i} more"); break; }
                Tree(sb, t.GetChild(i), depth + 1, max);
            }
        }

        static string Short(string s) => s == null ? "" : (s.Length > 60 ? s.Substring(0, 60) + "…" : s).Replace("\n", "\\n");

        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null && parts.Count < 6; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static void DumpInteractables(string when)
        {
            var sb = new StringBuilder($"===== SURVIVAL PROBE: interactables ({when}) =====\n");
            try
            {
                var it = Il2CppType.Of<Interactable>();
                var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(m => m != null && m.gameObject.scene.IsValid() && it.IsAssignableFrom(m.GetIl2CppType())).ToList();
                foreach (var g in all.GroupBy(m => m.GetIl2CppType().Name).OrderByDescending(g => g.Count()))
                    sb.AppendLine($"  {g.Key}: {g.Count()}");
                foreach (var v in Main.FindAll<VendingMachine>())
                {
                    var soda = v.sodaPrefab;
                    var li = soda != null ? soda.GetComponentInChildren<LootableItem>(true) : null;
                    sb.AppendLine($"  vending '{Path(v.transform)}' active={v.gameObject.activeInHierarchy} soda={(soda != null ? soda.name : "-")} sodaLoot={(li != null ? $"{li.ItemType}/{li.ItemId}" : "-")} desc='{v.GetDescription()}'");
                }
                foreach (var c in Main.FindAll<LootableChanceItems>())
                {
                    var loot = c.loot;
                    var drops = loot?.lootDrop == null ? "" : string.Join(",", loot.lootDrop.ToArray().GroupBy(d => d.dropType.ToString()).Select(g => $"{g.Key}:{g.Count()}"));
                    sb.AppendLine($"  chance '{Path(c.transform)}' name='{c.Name}' active={c.gameObject.activeInHierarchy} loot='{loot?.name}' nothing={loot?.chanceOfNothing} drops=[{drops}]");
                }
                foreach (var r in Main.FindAll<RpgCrate>()) sb.AppendLine($"  rpgCrate '{Path(r.transform)}' active={r.gameObject.activeInHierarchy}");
                foreach (var l in Main.FindAll<LootableItem>().Where(l => l.ItemType == LootableItem.LootType.Weapon))
                    sb.AppendLine($"  weaponPickup '{Path(l.transform)}' id={l.ItemId} active={l.gameObject.activeInHierarchy}");
                foreach (var d in Main.FindAll<DoorInteraction>().Take(6)) sb.AppendLine($"  door '{Path(d.transform)}'");
            }
            catch (Exception e) { sb.AppendLine("  failed: " + e); }
            Log(sb);
        }

        static void DumpFloor(int floor)
        {
            var sb = new StringBuilder($"===== SURVIVAL PROBE: floor {floor} =====\n");
            try
            {
                foreach (var el in Main.FindAll<LevelElevator>()) sb.AppendLine($"  elevator floor={el.CurrentFloor} level='{el.levelName}'");
                var enemies = Main.FindAll<Il2CppEnemyAI.EnemyHealth>().ToList();
                sb.AppendLine($"  enemies={enemies.Count}");
                foreach (var g in enemies.GroupBy(e => $"{Root(e).name.Replace("(Clone)", "")} type={e.enemyType} hp={e.health}"))
                    sb.AppendLine($"    {g.Key} x{g.Count()}");
                foreach (var s in Main.FindAll<EnemySpawner>())
                    sb.AppendLine($"  spawner type={s.Type} onStart={s.SpawnOnStart} data='{s.EnemyData?.name}' at={s.transform.position}");
                sb.AppendLine($"  lootables on floor={Main.FindAll<LootableItem>().Count()}");
            }
            catch (Exception e) { sb.AppendLine("  failed: " + e); }
            Log(sb);
            Directory.CreateDirectory(ShotDir);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(ShotDir, $"survival_floor{floor}.png"));
        }

        static Transform Root(Il2CppEnemyAI.EnemyHealth e) => e.transform.parent != null ? e.transform.parent : e.transform;

        static Il2CppEnemyAI.EnemyHealth _victim;
        static int _lootBefore;
        static float _killedAt;

        static void KillOne()
        {
            _victim = Main.FindAll<Il2CppEnemyAI.EnemyHealth>().Where(e => e.health > 0 && e.gameObject.activeInHierarchy && !EndlessBoss.ExtraBosses.Any(b => b != null && b.Pointer == e.Pointer)).FirstOrDefault();
            _lootBefore = Main.FindAll<LootableItem>().Count();
            if (_victim == null) { Main.Log("[survival] no enemy to kill"); return; }
            var le = Root(_victim).GetComponentInChildren<Il2CppBallistics.LivingEntity>(true);
            Main.Log($"[survival] killing '{Root(_victim).name}' type={_victim.enemyType}");
            try { le?.OnHealthChanged(-(_victim.health + 50f)); } catch (Exception ex) { Main.Log("[survival] kill threw " + ex.Message.Split((char)10)[0]); }
            _killedAt = Time.realtimeSinceStartup;
        }

        static void AfterKill()
        {
            var sb = new StringBuilder("===== SURVIVAL PROBE: corpse =====\n");
            try
            {
                if (_victim == null || _victim.WasCollected) { sb.AppendLine("  victim gone"); Log(sb); return; }
                var root = Root(_victim);
                sb.AppendLine($"  {Time.realtimeSinceStartup - _killedAt:0.0}s after kill: hp={_victim.health} active={root.gameObject.activeInHierarchy} dead={_victim.dead}");
                sb.AppendLine($"  root comps=[{string.Join(",", root.GetComponents<Component>().Select(c => c.GetIl2CppType().Name))}] children={root.childCount} rigidbodies={root.GetComponentsInChildren<Rigidbody>(true).Length} awakeRb={root.GetComponentsInChildren<Rigidbody>().Count(r => !r.IsSleeping())}");
                sb.AppendLine($"  behaviours enabled=[{string.Join(",", root.GetComponentsInChildren<Behaviour>().Where(b => b.enabled).Select(b => b.GetIl2CppType().Name).Distinct().Take(30))}]");
                var lootAfter = Main.FindAll<LootableItem>().ToList();
                sb.AppendLine($"  lootables {_lootBefore} -> {lootAfter.Count}");
                foreach (var l in lootAfter.Where(l => Vector3.Distance(l.transform.position, _victim.transform.position) < 4f))
                    sb.AppendLine($"    near corpse: '{l.gameObject.name}' type={l.ItemType} id={l.ItemId} ammo={l.ammoCount}");
            }
            catch (Exception e) { sb.AppendLine("  failed: " + e); }
            Log(sb);
        }
    }
}
