using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // 構えたときの見え方の一覧（autotest -Dev -Scenario custads）。武器ごとに、サイトの選択肢（最初のまま・外す・載せられるサイト）と
    // ハンドガンはストック付きで構えて撮り、照準の模様の画面上の位置・カメラと腕の位置を記録する
    public static class AdsLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();

        public static void Register()
        {
            TestMode.ExternalScenarios["custads"] = () => Steps();
            // MPS-5・Viper のサイトだけ（銃身の中心と照準の左右のずれ）
            TestMode.ExternalScenarios["custmp5"] = () => Steps(new HashSet<int> { 27, 28 }, n => n != "vanilla" && n != "iron");
            // Swiss 751 だけ（スコープ・アイアンサイト・ほかのサイトでリアサイトの有無）
            TestMode.ExternalScenarios["custswiss"] = () => { var s = Steps(new HashSet<int> { 17 }); s.Add(("swiss side", SwissSide)); return s; };
            // 下調べをまとめて 1 回で（材質＋構えたときの見え方）
            TestMode.ExternalScenarios["custlook"] = () => { var s = SkinLab.Steps(); s.AddRange(Steps()); return s; };
            // 見た目の確認をまとめて 1 回で（部品の横からの撮影＋構えたときの見え方）
            TestMode.ExternalScenarios["custvis"] = () => { var s = PartsLab.Steps(); s.AddRange(Steps()); return s; };
            // 見た目＋付け外しの確認をまとめて 1 回で
            TestMode.ExternalScenarios["custall"] = () => { var s = PartsLab.Steps(); s.AddRange(Steps()); s.AddRange(MountTest.Steps()); return s; };
        }

        // Swiss 751 を横から：アイアンサイト（リアサイトあり）・ホロ（リアサイトなし）・最初のスコープ
        static void SwissSide()
        {
            var prefab = Tracker.Inv.WeaponData.Weapons.ToArray().FirstOrDefault(w => w.Id == 17)?.Weapon?.gameObject;
            if (prefab == null) return;
            foreach (var sid in new[] { "iron", "holo", "reddot", "ace_sight", "scope" })
            {
                var go = SightLab.CloneStatic(prefab, out var holder);
                holder.SetActive(true);
                try
                {
                    Lab.HideNonMesh(go);
                    if (sid != "scope") foreach (var fp in Factory.On(17)) if (Catalog.Get(fp.Att)?.Cat == Cat.Sight) Factory.Show(go.transform, fp, false);
                    if (sid != "iron" && sid != "scope") Optics.Apply(go, prefab, 17, Catalog.Get(sid));
                    var mf = go.transform.Find("SIG 751 1/main_part")?.GetComponent<MeshFilter>();
                    Main.Log($"[ads] swiss side {sid}: main_part verts={(mf?.sharedMesh != null ? mf.sharedMesh.vertexCount : -1)}");
                    var shot = SightLab.RenderSide(go, 0.06f, new Vector3(0f, 0.04f, -0.10f));
                    File.WriteAllBytes(Path.Combine(ShotDir, $"swiss_side_{sid}.png"), ImageConversion.EncodeToPNG(shot.Tex));
                    shot.Dispose();
                    // 台座の近く（脚とレールのすき間）を拡大
                    var zoom = SightLab.RenderSide(go, 0.012f, new Vector3(0f, 0.041f, -0.075f));
                    File.WriteAllBytes(Path.Combine(ShotDir, $"swiss_feet_{sid}.png"), ImageConversion.EncodeToPNG(zoom.Tex));
                    zoom.Dispose();
                }
                catch (Exception e) { Main.Log("[ads] swiss side failed " + e); }
                finally { UnityEngine.Object.DestroyImmediate(holder); }   // 同じ場所に次の複製を作るので、すぐ消す（Destroy だと前の複製が重なって写る）
            }
        }

        sealed class Cfg { public string Name; public (Cat, string)[] Set; }

        static List<Cfg> Configs(int id)
        {
            var list = new List<Cfg> { new Cfg { Name = "vanilla", Set = Array.Empty<(Cat, string)>() } };
            if (Sights.HasFactorySight(id) && Catalog.CanRemove(id, Cat.Sight)) list.Add(new Cfg { Name = "iron", Set = new[] { (Cat.Sight, "") } });
            foreach (var a in Catalog.For(id, Cat.Sight))
                if (!Catalog.IsFactory(a, id)) list.Add(new Cfg { Name = a.Id, Set = new[] { (Cat.Sight, a.Id) } });
            if (Catalog.IsPistol(id) && Catalog.For(id, Cat.Stock).Count > 0)
                list.Add(new Cfg { Name = "stock", Set = new[] { (Cat.Stock, Catalog.For(id, Cat.Stock)[0].Id) } });
            return list;
        }

        static void ResetAll(int id) { foreach (var c in Catalog.CatsFor(id)) Store.SetForTest(id, c, null); }

        public static List<(string, Action)> Steps(HashSet<int> only = null, Func<string, bool> cfgOk = null)
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "ads_*.png")) File.Delete(f); }));
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                var w = wi;
                if (!Catalog.Eligible(w) || (only != null && !only.Contains(w.Id))) continue;
                int id = w.Id;
                var name = Tracker.NameOf(id);
                s.Add(($"add {id}", () =>
                {
                    ResetAll(id);
                    if (Tracker.Held.Any(h => h.Roll.WeaponId == id)) return;
                    while (Tracker.Held.Count >= 3) Refs.Bag.MoveWeaponToStash(Tracker.ItemById(Tracker.Held.First(h => h.Roll.WeaponId != id).Roll.WeaponId));
                    Main.Log($"[ads] AddWeapon {name} -> {Tracker.Inv.AddWeapon(Tracker.ItemById(id))}");
                }));
                s.Add(("wait", () => { }));
                s.Add(($"equip {id}", () =>
                {
                    var pn = Tracker.ItemById(id).Weapon.gameObject.name;
                    var inst = Refs.Fps.InstantiatedWeapons;
                    for (int i = 0; i < inst.Count; i++)
                        if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == pn) { Refs.Fps.StartWeaponChange(i); return; }
                    Main.Log($"[ads] {name} not in hands");
                }));
                s.Add(("wait", () => { }));
                s.Add(("wait", () => { }));
                foreach (var cf in Configs(id))
                {
                    if (cfgOk != null && !cfgOk(cf.Name)) continue;
                    var c = cf;
                    s.Add(($"set {id} {c.Name}", () => { ResetAll(id); foreach (var (cat, att) in c.Set) Store.SetForTest(id, cat, att); Tracker.ApplyAll(); }));
                    s.Add(("aim", () => { try { Active()?.OnAimPressed(); } catch (Exception e) { Main.Log("[ads] aim failed " + e.Message); } }));
                    s.Add(("wait", () => { }));
                    s.Add(("wait", () => { }));
                    if (id == 5) { s.Add(($"ads early {id} {c.Name}", () => Measure(id, name, c.Name + "_early"))); s.Add(("wait", () => { })); s.Add(("wait", () => { })); s.Add(("wait", () => { })); }   // Eagle は構えるのが遅い？
                    s.Add(($"ads {id} {c.Name}", () => Measure(id, name, c.Name)));
                    s.Add(("release", () => { try { Active()?.OnAimReleased(); } catch { } }));
                    s.Add(("wait", () => { }));
                }
                s.Add(($"cleanup {id}", () => { ResetAll(id); Tracker.ApplyAll(); Refs.Bag.MoveWeaponToStash(Tracker.ItemById(id)); }));
                s.Add(("wait", () => { }));
            }
            return s;
        }

        static string V(Vector3 v) => $"({v.x:F3},{v.y:F3},{v.z:F3})";

        static void Measure(int id, string name, string cfg)
        {
            ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"ads_{id:00}_{cfg}.png"));
            var dw = Active(); var cam = Camera.main;
            if (dw == null || cam == null) { Main.Log($"[ads] [{id}] {cfg}: no weapon/camera"); return; }
            bool held = dw.gameObject.name.Contains(Tracker.ItemById(id).Weapon.gameObject.name);
            var root = dw.transform;
            var ap = MountTestAim(dw);
            var camLocal = root.InverseTransformPoint(cam.transform.position);
            var apScreen = cam.WorldToScreenPoint(ap != null ? ap.position : root.position);
            Main.Log($"[ads] [{id}] {name} {cfg}: held={held} cam(gun)={V(camLocal)} aim(gun)={(ap != null ? V(root.InverseTransformPoint(ap.position)) : "-")} aimScreen=({apScreen.x / Screen.width:F3},{apScreen.y / Screen.height:F3}) fov={cam.fieldOfView:F1} near={cam.nearClipPlane:F3}");
            if (cfg == "stock" && AssetParts.StockAt.TryGetValue(id, out var sa))
                Check($"{name} stock: shouldered (eye {Sights.ShoulderReach * 100:F1}cm behind the grip)", Mathf.Abs(camLocal.z - (sa.HeelZ - Sights.ShoulderReach)) < 0.01f);
            if (ap != null) Check($"{name} {cfg}: camera on the aim point", (camLocal - root.InverseTransformPoint(ap.position)).magnitude < 0.005f);
            if (Mount.Count(root, "sight") == 1 && Optics.ReticleById.TryGetValue(id, out var rl))
            {
                var sp = cam.WorldToScreenPoint(root.TransformPoint(rl));
                float off = new Vector2(sp.x - Screen.width / 2f, sp.y - Screen.height / 2f).magnitude / Screen.height;
                Main.Log($"[ads] [{id}] {cfg}: reticle screen=({sp.x / Screen.width:F3},{sp.y / Screen.height:F3}) off={off:F3}");
                Check($"{name} {cfg}: reticle at screen center", off < 0.03f);
            }
            // 銃身の中心の線（元の発射点の左右・上下。武器の根元の座標）が画面のどこに見えるか：銃口と、その 25cm 後ろ
            if (Optics.BoreX(Tracker.ItemById(id).Weapon.gameObject) is float bx)
            {
                var psp = Tracker.ItemById(id).Weapon.gameObject.GetComponentInChildren<Il2CppBallistics.Weapon>(true)?.PhysicalBulletSpawnPoint;
                var pr = Tracker.ItemById(id).Weapon.transform;
                var mz = psp != null ? pr.InverseTransformPoint(psp.position) : new Vector3(bx, 0f, 0.3f);
                var a = cam.WorldToScreenPoint(root.TransformPoint(mz));
                var b = cam.WorldToScreenPoint(root.TransformPoint(mz - new Vector3(0f, 0f, 0.25f)));
                var sg = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == Mount.Prefix + "sight");
                var sgs = sg != null ? cam.WorldToScreenPoint(Lab.MeshBounds(sg, root) is var sb ? root.TransformPoint(sb.center) : sg.position) : Vector3.zero;
                Main.Log($"[ads] [{id}] {cfg}: bore muzzle={V(mz)} screenX muzzle={a.x / Screen.width:F4} rear={b.x / Screen.width:F4} sightBody={sgs.x / Screen.width:F4} (center 0.5)");
            }
            // 傾き（カメラから見た左右の回転。度、右回りが正）：サイトの台座・武器の根元・本体の骨
            float Roll(Transform x) { if (x == null) return float.NaN; var u = cam.transform.InverseTransformDirection(x.up); return -Mathf.Atan2(u.x, u.y) * Mathf.Rad2Deg; }
            float Yaw(Transform x) { if (x == null) return float.NaN; var f = cam.transform.InverseTransformDirection(x.forward); return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg; }
            var sightT = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == Mount.Prefix + "sight");
            var rootBone = root.Find("mp5/Armature/Root") ?? root.Find("mp5 custom/Armature/Root");
            Main.Log($"[ads] [{id}] {cfg}: roll sight={Roll(sightT):F2} gun={Roll(root):F2} bone={Roll(rootBone):F2} yaw sight={Yaw(sightT):F2} gun={Yaw(root):F2} bone={Yaw(rootBone):F2}");
            // 腕・体の骨と、付けたストックの位置（カメラの座標。x 右・y 上・z 前）
            var ct = cam.transform;
            string Bone(string n) { var t = Refs.Fps.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == n); return t == null ? "-" : V(ct.InverseTransformPoint(t.position)); }
            Main.Log($"[ads] [{id}] {cfg}: bones(cam) RHand={Bone("Right_Hand")} RLow={Bone("Right_LowerArm")} RUp={Bone("Right_UpperArm")} RSh={Bone("Right_Shoulder")} LHand={Bone("Left_Hand")} LLow={Bone("Left_LowerArm")} Chest={Bone("UpperChest")} Neck={Bone("Neck")} Head={Bone("Head")} gun={V(ct.InverseTransformPoint(root.position))}");
            var st = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == Mount.Prefix + "stock");
            if (st != null)
            {
                var b = Lab.MeshBounds(st, ct);
                Main.Log($"[ads] [{id}] {cfg}: stock(cam) min={V(b.min)} max={V(b.max)}");
            }
            // 見えている体のメッシュ（一人称で体が描かれているか）
            if (cfg == "vanilla" && id == 13)
                foreach (var r in Refs.Fps.GetComponentsInChildren<Renderer>(true))
                    if (r.enabled && r.gameObject.activeInHierarchy && !r.transform.IsChildOf(root))
                        Main.Log($"[ads] body renderer '{SightLab.RelPath(r.transform, Refs.Fps.transform)}' {r.GetIl2CppType().Name} layer={r.gameObject.layer} shadowOnly={r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly}");
        }

        static Transform MountTestAim(DemoWeapon dw)
        {
            var g = dw?.scopeGroups;
            return g != null && g.Count > 0 && g[0]?.attachments != null && g[0].attachments.Count > 0 ? g[0].attachments[0]?.aimPoint : null;
        }
    }
}
