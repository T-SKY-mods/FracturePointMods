using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FPModOption;
using Il2Cpp;
using UnityEngine;
using EnemyHealth = Il2CppEnemyAI.EnemyHealth;
using Object = UnityEngine.Object;

namespace FPSurvival
{
    // 調査用（autotest -Scenario survprobe）：屋上の非常口の扉・木の壁・死体と武器箱を調べたときの中身をログに出す
    public static class SurvivalProbe
    {
        static void Log(string m) => Core.Log("[survprobe] " + m);
        static void Wait(List<(string, Action)> s, int n, string name = "wait") { for (int i = 0; i < n; i++) s.Add((name, () => { })); }

        static Vector3 _corpseAt, _corpseAt2;
        static HashSet<IntPtr> _lootBefore = new();
        static string _invBefore;
        static int _heldBefore;

        public static void Register() { TestMode.ExternalScenarios["survprobe"] = Steps; TestMode.ExternalScenarios["survprobe2"] = Steps2; TestMode.ExternalScenarios["survloc"] = StepsLoc; TestMode.ExternalScenarios["survtablet"] = StepsTablet; }

        // 確認用（-Scenario survtablet）：端末の 2 ページ目を日本語・英語で撮る
        static string _lang;
        static List<(string, Action)> StepsTablet()
        {
            var s = new List<(string, Action)>();
            foreach (var code in new[] { "ja", "en" })
            {
                string c = code;
                s.Add(($"language {c}", () =>
                {
                    _lang ??= Il2CppGameLocalization.LocManager.Code;
                    Il2CppGameLocalization.LocManager.SetLanguage(c);
                    try { Tablet.TabletForTest?.Interact(); } catch { }
                    Tablet.SetPage(1);
                }));
                Wait(s, 4);
                s.Add(($"shot {c}", () => { ShotFile("surv_tablet_" + c); Log($"tablet {c}: " + Tablet.DebugForTest()); }));
                Wait(s, 1);
            }
            s.Add(("restore", () => Il2CppGameLocalization.LocManager.SetLanguage(_lang)));
            return s;
        }

        // 調査用（-Scenario survloc）：ゲームの日本語訳から、ルール説明に使う固有名詞を探してファイルに書き出す（配布しない）
        static List<(string, Action)> StepsLoc() => new()
        {
            ("dump", () =>
            {
                var orig = Il2CppGameLocalization.LocManager.Code;
                Il2CppGameLocalization.LocManager.SetLanguage("ja");
                var cat = Il2CppGameLocalization.LocManager.Catalog;
                var words = new Regex(@"crate|vending|elevator|fire exit|exit|hub|night|nvg|grenade|armor|armour|helmet|healix|heal|ammo|mag|primary|secondary|bear|statue|vase|time|rouble|ruble|dollar|wave|endless|stash|inventory|loot|drink|door|roof|boss|weapon|search|survival|money|cash|item|base|room|floor|run|tablet", RegexOptions.IgnoreCase);
                var lines = new List<string>();
                foreach (var kv in cat._exact)
                {
                    string k = kv.Key, v = kv.Value;
                    if (k != null && k.Length <= 80 && words.IsMatch(k)) lines.Add(k.Replace("\n", " ") + "\t" + (v ?? "").Replace("\n", " "));
                }
                lines.Sort(StringComparer.OrdinalIgnoreCase);
                System.IO.File.WriteAllLines(System.IO.Path.Combine(Core.Dir, "loc_ja_probe.txt"), lines);
                Log($"exact={cat.ExactCount} matched={lines.Count}");
                Il2CppGameLocalization.LocManager.SetLanguage(orig);
            }),
        };

        static void ShotFile(string n) => UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Core.Dir, "shots", n + ".png"));

        static List<(string, Action)> Steps2()
        {
            Arena.PrepSeconds = 600f;
            var s = new List<(string, Action)> { ("start", () => { Tablet.SetPage(1); Object.FindObjectOfType<ButtonStartEndlessMode>()?.StartEndlessRun(); }) };
            Wait(s, 12, "wait load");

            Wait(s, 16, "wait rooftop");
            s.Add(("env", DumpEnv));
            s.Add(("door tree", DumpDoorTree));
            s.Add(("roof objects", DumpRoofObjects));
            s.Add(("go outside", () => { var dp = Arena.DoorPosForTest; Arena.Teleport(dp - Arena.InwardForTest * 4f, Arena.InwardForTest); }));
            Wait(s, 2);
            s.Add(("shot", () => ShotFile("surv_door_outside")));
            s.Add(("walk in", () => { var dp = Arena.DoorPosForTest; Arena.Teleport(dp - Arena.InwardForTest * 0.6f, Arena.InwardForTest); }));
            Wait(s, 3);
            s.Add(("door after", DumpDoorTree));
            s.Add(("back outside", () => { var dp = Arena.DoorPosForTest; Arena.Teleport(dp - Arena.InwardForTest * 4f, Arena.InwardForTest); }));
            Wait(s, 2);
            s.Add(("shot", () => ShotFile("surv_door_after")));
            s.Add(("center", () => Arena.Teleport(new Vector3(10f, 1.1f, 0.5f), Vector3.right)));
            Wait(s, 2);
            s.Add(("shot", () => ShotFile("surv_center")));
            return s;
        }

        // 屋上の環境音と明るさの設定
        static void DumpEnv()
        {
            foreach (var a in Object.FindObjectsOfType<AudioSource>())
            {
                if (a == null || !a.isPlaying) continue;
                Log($"audio '{Path(a.transform)}' clip='{a.clip?.name}' vol={a.volume:0.00} loop={a.loop} blend={a.spatialBlend:0.0} min={a.minDistance:0} max={a.maxDistance:0} at {a.transform.position} mixer='{a.outputAudioMixerGroup?.name}'");
            }
            Log($"render: ambientMode={RenderSettings.ambientMode} ambientLight={RenderSettings.ambientLight} sky={RenderSettings.ambientSkyColor} equator={RenderSettings.ambientEquatorColor} ground={RenderSettings.ambientGroundColor} intensity={RenderSettings.ambientIntensity} fog={RenderSettings.fog} fogColor={RenderSettings.fogColor} fogDensity={RenderSettings.fogDensity} skybox='{RenderSettings.skybox?.name}'");
            foreach (var l in Object.FindObjectsOfType<Light>())
                if (l != null && l.enabled && (l.type == LightType.Directional || l.intensity > 50f)) Log($"light '{Path(l.transform)}' type={l.type} int={l.intensity:0.00} color={l.color} range={l.range:0}");
            foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null) continue;
                var n = mb.GetIl2CppType().Name;
                if (n != "Volume") continue;
                Log($"volume '{Path(mb.transform)}' enabled={mb.enabled}");
            }
        }

        static void DumpDoorTree()
        {
            var d = Arena.RoomForTest.Find("door");
            if (d == null) { Log("no door object"); return; }
            Walk(d, 0);
            foreach (var mb in d.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var dd = mb.TryCast<Door>();
                if (dd != null) Log($"Door script on {dd.name}: isOpen={dd.isOpen} opened={dd.opened} leaf={dd.door?.name} angle={dd.openAngle} speed={dd.speed} enabled={dd.enabled}");
            }
        }

        static void Walk(Transform t, int depth)
        {
            var comps = string.Join(",", t.GetComponents<Component>().Select(c => c.GetIl2CppType().Name));
            var r = t.GetComponent<Renderer>();
            var pad = new string(' ', depth * 2);
            var extra = r != null ? $" bounds={r.bounds.center}/{r.bounds.size}" : "";
            Log($"{pad}{t.name} active={t.gameObject.activeSelf} pos={t.position} rot={t.localEulerAngles} layer={LayerMask.LayerToName(t.gameObject.layer)} comps=[{comps}]{extra}");
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
        }

        static void DumpRoofObjects()
        {
            var room = Arena.RoomForTest;
            var mats = new Dictionary<string, int>();
            foreach (var r in room.GetComponentsInChildren<Renderer>(true)) foreach (var m in r.sharedMaterials) if (m != null) mats[m.name] = mats.TryGetValue(m.name, out var n) ? n + 1 : 1;
            Log("materials: " + string.Join(", ", mats.OrderByDescending(k => k.Value).Take(120).Select(k => $"{k.Key}x{k.Value}")));
            // 屋上の床の上にある、人の背丈ほどの物（遮蔽物の候補）
            for (int i = 0; i < room.childCount; i++)
            {
                var t = room.GetChild(i);
                if (!t.gameObject.activeInHierarchy) continue;
                var rs = t.GetComponentsInChildren<Renderer>();
                if (rs.Length == 0) continue;
                var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                if (b.center.x < -8f || b.size.y < 1.0f || b.size.y > 6f || b.min.y > 2.5f || b.size.x > 20f || b.size.z > 20f) continue;
                var ms = new List<string>(); foreach (var r in rs) foreach (var x in r.sharedMaterials) if (x != null && !ms.Contains(x.name) && ms.Count < 3) ms.Add(x.name);
                var m = string.Join(",", ms);
                Log($"obj '{t.name}' at {t.position.ToString("0.0")} size {b.size.ToString("0.0")} rotY={t.eulerAngles.y:0} cols={t.GetComponentsInChildren<Collider>().Length} mats=[{m}]");
            }
        }

        static List<(string, Action)> Steps()
        {
            Arena.PrepSeconds = 6f; Arena.BreakSeconds = 300f; Arena.SpawnInterval = 1f;
            var s = new List<(string, Action)>
            {
                ("start", () => { Tablet.SetPage(1); Object.FindObjectOfType<ButtonStartEndlessMode>()?.StartEndlessRun(); }),
            };
            Wait(s, 12, "wait load");

            Wait(s, 16, "wait rooftop");
            s.Add(("door", DumpDoor));
            s.Add(("walls", DumpWalls));
            s.Add(("go out", () => Arena.Teleport(new Vector3(20f, 1.1f, 0.5f), Vector3.right)));
            Wait(s, 8, "wait wave");
            s.Add(("kill one", () =>
            {
                var e = Arena.AliveForTest().FirstOrDefault();
                if (e == null) { Log("no enemy"); return; }
                Log($"killing '{e.transform.parent?.name}/{e.name}' hp={e.health}");
                _lootBefore = Object.FindObjectsOfType<LootableItem>().Select(l => l.Pointer).ToHashSet();
                try { e.TakeDamage(e.health + 50f); } catch (Exception ex) { Log("TakeDamage threw: " + ex.Message.Split('\n')[0]); }
            }));
            Wait(s, 3);
            s.Add(("corpse 1", () => { var e = Arena.DeadForTest().FirstOrDefault(); if (e != null) _corpseAt = e.transform.position; DumpCorpse(e); }));
            Wait(s, 2);
            s.Add(("corpse 2", () =>
            {
                var e = Arena.DeadForTest().FirstOrDefault();
                if (e == null) return;
                _corpseAt2 = e.transform.position;
                Log($"corpse moved {Vector3.Distance(_corpseAt, _corpseAt2):0.00} m in 3 s (still walking if large)");
            }));
            s.Add(("search corpse", () =>
            {
                var e = Arena.DeadForTest().FirstOrDefault();
                var root = e == null ? null : (e.transform.parent != null ? e.transform.parent : e.transform);
                var c = root?.GetComponentInChildren<LootableChanceItems>(true) ?? e?.GetComponentInParent<LootableChanceItems>();
                if (c == null) { Log("corpse has no LootableChanceItems"); return; }
                _invBefore = Stash.Describe(Stash.Take(Refs.Inv));
                Log($"search '{c.GetDescription()}' hold={c.RequiresHold()} enabled={c.enabled}");
                try { c.Interact(); } catch (Exception ex) { Log("Interact threw: " + ex.Message.Split('\n')[0]); }
            }));
            Wait(s, 2);
            s.Add(("after search", () =>
            {
                Log("inv before: " + _invBefore);
                Log("inv after:  " + Stash.Describe(Stash.Take(Refs.Inv)));
                NewLoot(_corpseAt2, 6f);
                var e = Arena.DeadForTest().FirstOrDefault();
                var c = e?.transform.root.GetComponentInChildren<LootableChanceItems>(true);
                Log($"after: chance comp exists={c != null} enabled={c?.enabled} desc='{c?.GetDescription()}'");
            }));
            s.Add(("crate", () =>
            {
                var t = Arena.CrateForTest;
                if (t == null) { Log("no crate template"); return; }
                var fps = Refs.Fps.transform;
                var go = Object.Instantiate(t, fps.position + fps.forward * 2f, Quaternion.identity);
                go.SetActive(true);
                var c = go.GetComponentInChildren<LootableChanceItems>(true);
                DumpChance("crate", c);
                _heldBefore = Survival.Held(Refs.Inv).Count;
                _lootBefore = Object.FindObjectsOfType<LootableItem>().Select(l => l.Pointer).ToHashSet();
                _corpseAt2 = go.transform.position;
                Log($"held before {_heldBefore}: " + string.Join(", ", Survival.Held(Refs.Inv).Select(Survival.Label)) + $" max big {Refs.Inv.MaxBigWeapons} small {Refs.Inv.MaxSmallWeapons}");
                for (int i = 0; i < 4; i++)
                {
                    try { c.Interact(); } catch (Exception ex) { Log("crate Interact threw: " + ex.Message.Split('\n')[0]); }
                    Log($"after interact {i + 1}: held {Survival.Held(Refs.Inv).Count}: " + string.Join(", ", Survival.Held(Refs.Inv).Select(Survival.Label)) + $" desc='{c.GetDescription()}' dropped={c.DroppedItems?.Count}");
                }
            }));
            Wait(s, 2);
            s.Add(("after crate", () => NewLoot(_corpseAt2, 6f)));
            return s;
        }

        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null && parts.Count < 6; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static void DumpDoor()
        {
            var room = Arena.RoomForTest;
            var dp = Arena.DoorPosForTest;
            Log($"door pos {dp} inward {Arena.InwardForTest}");
            foreach (var d in Object.FindObjectsOfType<Door>().Where(d => Vector3.Distance(d.transform.position, dp) < 12f))
                Log($"Door '{Path(d.transform)}' at {d.transform.position} isOpen={d.isOpen} opened={d.opened} openAngle={d.openAngle} leaf='{d.door?.name}' leafRot={d.door?.localEulerAngles} breakOnBreach={d.breakOnBreach}");
            foreach (var di in Object.FindObjectsOfType<DoorInteraction>().Where(d => Vector3.Distance(d.transform.position, dp) < 12f))
            {
                var cols = di.GetComponents<Collider>().Select(c => $"{c.GetIl2CppType().Name}(en={c.enabled},trig={c.isTrigger},layer={LayerMask.LayerToName(c.gameObject.layer)},b={c.bounds.center}/{c.bounds.size})");
                Log($"DoorInteraction '{Path(di.transform)}' at {di.transform.position} active={di.gameObject.activeInHierarchy} enabled={di.enabled} dir={di.direction} door='{di.door?.name}' desc='{di.GetDescription()}' cols=[{string.Join(" ", cols)}]");
            }
            // 扉の周りの当たり判定
            foreach (var c in Physics.OverlapSphere(dp + Vector3.up, 1.8f))
                Log($"  collider near door: '{Path(c.transform)}' {c.GetIl2CppType().Name} trig={c.isTrigger} en={c.enabled} layer={LayerMask.LayerToName(c.gameObject.layer)} b={c.bounds.center}/{c.bounds.size}");
        }

        static readonly Regex WallName = new("(?i)wood|plank|board|barric|wall|fence|pallet|cover|panel|shield|sheet", RegexOptions.Compiled);

        static void DumpWalls()
        {
            var room = Arena.RoomForTest;
            var pts = Arena.SpawnPointsForTest;
            if (pts.Count > 0)
            {
                var b = new Bounds(pts[0], Vector3.zero); foreach (var p in pts) b.Encapsulate(p);
                Log($"roof walkable bounds {b.center} size {b.size}");
            }
            var groups = new Dictionary<string, List<Transform>>();
            foreach (var r in room.GetComponentsInChildren<MeshRenderer>(true))
            {
                var t = r.transform;
                string key = null;
                for (var x = t; x != null && x != room; x = x.parent) if (WallName.IsMatch(x.name)) { key = x.name; t = x; break; }
                if (key == null) { var mats = string.Join(",", r.sharedMaterials.Where(m => m != null).Select(m => m.name)); if (Regex.IsMatch(mats, "(?i)wood|plank")) key = "mat:" + r.name + " [" + mats + "]"; }
                if (key == null) continue;
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Transform>();
                if (!list.Contains(t)) list.Add(t);
            }
            foreach (var kv in groups.OrderByDescending(k => k.Value.Count).Take(40))
            {
                var t = kv.Value[0];
                var rs = t.GetComponentsInChildren<Renderer>(true);
                var bb = rs.Length > 0 ? rs[0].bounds : new Bounds(t.position, Vector3.zero);
                foreach (var r in rs) bb.Encapsulate(r.bounds);
                var col = t.GetComponentsInChildren<Collider>(true).Length;
                Log($"'{kv.Key}' x{kv.Value.Count} e.g. '{Path(t)}' at {t.position} size {bb.size} colliders={col} active={t.gameObject.activeInHierarchy} static={t.gameObject.isStatic} more at " + string.Join(" ", kv.Value.Skip(1).Take(5).Select(x => x.position.ToString("0"))));
            }
        }

        static void DumpCorpse(EnemyHealth e)
        {
            if (e == null) { Log("no corpse"); return; }
            Log($"corpse '{Path(e.transform)}' hp={e.health} anim={(e.GetComponent<Animator>()?.enabled)} pos={e.transform.position}");
            var root = e.transform.parent != null ? e.transform.parent : e.transform;
            foreach (var c in root.GetComponentsInChildren<LootableChanceItems>(true)) DumpChance("corpse", c);
            foreach (var c in e.GetComponentsInParent<LootableChanceItems>(true)) DumpChance("corpse(parent)", c);
            NewLoot(e.transform.position, 6f);
        }

        static void DumpChance(string what, LootableChanceItems c)
        {
            if (c == null) { Log($"{what}: no LootableChanceItems"); return; }
            var l = c.loot;
            var drops = l?.lootDrop == null ? "-" : string.Join(" ", l.lootDrop.ToArray().Where(d => d != null).GroupBy(d => d.dropType).Select(g => $"{g.Key}x{g.Count()}"));
            Log($"{what}: '{Path(c.transform)}' Name='{c.Name}' enabled={c.enabled} desc='{c.GetDescription()}' hold={c.RequiresHold()} loot='{l?.name}' nothing={l?.chanceOfNothing} drops=[{drops}]");
        }

        static void NewLoot(Vector3 at, float r)
        {
            foreach (var li in Object.FindObjectsOfType<LootableItem>().Where(l => !_lootBefore.Contains(l.Pointer) && Vector3.Distance(l.transform.position, at) < r))
                Log($"  new pickup '{Path(li.transform)}' type={li.ItemType} id={li.ItemId} ammo={li.ammoCount} at {li.transform.position} tip='{li.GetTooltipText()?.Split('\n')[0]}'");
        }
    }
}
