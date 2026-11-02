using System;
using System.Collections.Generic;
using System.IO;
using FPModOption;

namespace FPCustom
{
    // autotest -Dev -Scenario custfix：デバッグプレイで指摘された武器を、カスタマイズ画面と同じ絵（Preview）で撮る（横・上）。
    // 装着は Store.SetForTest で一時的に変える（custom.json は autotest.ps1 が前後で退避・復元する）
    public static class FixLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        public static void Register() => TestMode.ExternalScenarios["custfix"] = Steps;

        // （武器, 撮影名, 上からも撮るか, 装着）
        static readonly (int Id, string Name, bool Top, (Cat, string)[] Set)[] Cases =
        {
            (11, "aksu_sup", false, new[] { (Cat.Muzzle, "suppressor"), (Cat.Grip, "") }),
            (11, "aksu_brake", false, new[] { (Cat.Muzzle, "muzzle_brake") }),
            (11, "aksu_afg", false, new[] { (Cat.Muzzle, ""), (Cat.Grip, "angled_grip") }),
            (20, "type89_sup", false, new[] { (Cat.Muzzle, "suppressor") }),
            (18, "hunter32_bare", false, new[] { (Cat.Muzzle, "") }),
            (18, "hunter32_sup", false, new[] { (Cat.Muzzle, "suppressor") }),
            (18, "hunter32_brake", false, new[] { (Cat.Muzzle, "muzzle_brake") }),
            (19, "hunter12_sup", false, new[] { (Cat.Muzzle, "suppressor") }),
            (14, "val_vgrip", false, new[] { (Cat.Grip, "vertical_grip") }),
            (14, "val_afg", false, new[] { (Cat.Grip, "angled_grip") }),
            (14, "val_none", false, new[] { (Cat.Grip, "") }),
            (0, "ak74_brake", false, new[] { (Cat.Muzzle, "muzzle_brake") }),
            (10, "m16_brake", false, new[] { (Cat.Muzzle, "muzzle_brake") }),
            (27, "mps5_holo", true, new[] { (Cat.Sight, "holo"), (Cat.Grip, "") }),
            (27, "mps5_holo_vgrip", true, new[] { (Cat.Sight, "holo"), (Cat.Grip, "vertical_grip") }),
            (27, "mps5_vgrip", false, new[] { (Cat.Sight, ""), (Cat.Grip, "vertical_grip") }),
            (28, "viper_holo", true, new[] { (Cat.Sight, "holo") }),
            (1, "sp229_m93r", false, new[] { (Cat.Stock, "p_stock") }),
            (5, "eagle_m93r", false, new[] { (Cat.Stock, "p_stock") }),
            (6, "tt_m93r", false, new[] { (Cat.Stock, "p_stock") }),
            (7, "m92_m93r", false, new[] { (Cat.Stock, "p_stock") }),
            (8, "gr21_m93r", false, new[] { (Cat.Stock, "p_stock") }),
            (9, "sp320_m93r", false, new[] { (Cat.Stock, "p_stock") }),
            (13, "g18_m93r", false, new[] { (Cat.Stock, "p_stock") }),
            (22, "j941_m93r", false, new[] { (Cat.Stock, "p_stock") }),
            (23, "cv1_m93r", false, new[] { (Cat.Stock, "p_stock") }),
            (24, "gsh_m93r", false, new[] { (Cat.Stock, "p_stock") }),
            (25, "m1911_m93r", false, new[] { (Cat.Stock, "p_stock") }),
            (20, "type89_brake", false, new[] { (Cat.Muzzle, "muzzle_brake") }),
            (16, "sight_ace", false, new[] { (Cat.Sight, "ace_sight") }),
            (12, "sight_ak", false, new[] { (Cat.Sight, "ak_sight") }),
            (18, "sight_reddot", false, new[] { (Cat.Sight, "reddot") }),
            (17, "sight_scope", false, new[] { (Cat.Sight, "swiss_scope") }),
            (8, "sight_pdot", false, new[] { (Cat.Sight, "p_dot") }),
        };

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)> { ("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "fix_*.png")) File.Delete(f); }) };
            foreach (var c in Cases)
            {
                var k = c;
                s.Add(($"shot {k.Name}", () =>
                {
                    try
                    {
                        foreach (var (cat, att) in k.Set) Store.SetForTest(k.Id, cat, att);
                        Preview.Render(k.Id);
                        Preview.Save(Path.Combine(ShotDir, $"fix_{k.Name}.png"));
                        Main.Log($"[fix] {k.Name}: {Store.Describe(k.Id)} center=({Preview.LastCenter.x:F4},{Preview.LastCenter.y:F4}) unit={Preview.LastUnit:F6}");
                    }
                    catch (Exception e) { Main.Log($"[fix] {k.Name} failed: {e}"); }
                }));
                // 上からの絵は次の手順で（横の絵の複製は、そのフレームの終わりまで残る）
                if (k.Top) s.Add(($"top {k.Name}", () => { Preview.Render(k.Id, true); Preview.Save(Path.Combine(ShotDir, $"fix_{k.Name}_top.png")); }));
            }
            s.Add(("muzzle parts", () => { foreach (var id in new[] { 11, 14, 18, 20, 27 }) Parts(id); }));
            foreach (var (wid, skin) in new[] { (27, "mp5/Magazine_release_button_1"), (28, "mp5 custom/Magazine_release_button_1") })
            {
            var w = wid; var sp = skin;
            s.Add(($"center {w}", () =>
            {
                // 本体（骨で動くメッシュ）の左右の中心を、前後の位置ごとに測る（武器の根元の座標）。サイト・台座・発射点と比べる
                Store.SetForTest(w, Cat.Grip, ""); Store.SetForTest(w, Cat.SideRail, ""); Store.SetForTest(w, Cat.Sight, "holo");
                var prefab = Tracker.ItemById(w).Weapon.gameObject;
                var holder = new UnityEngine.GameObject("FPC_centerprobe"); holder.SetActive(false);
                var go = UnityEngine.Object.Instantiate(prefab, holder.transform, false);
                Mount.Apply(go, prefab, w);
                holder.SetActive(true);
                var sk = go.transform.Find(sp).GetComponent<UnityEngine.SkinnedMeshRenderer>();
                var m = new UnityEngine.Mesh(); sk.BakeMesh(m, true);
                var bins = new Dictionary<int, (float lo, float hi, float top)>();
                foreach (var p in m.vertices)
                {
                    var q = go.transform.InverseTransformPoint(sk.transform.TransformPoint(p));
                    int k = UnityEngine.Mathf.FloorToInt(q.z / 0.03f);
                    var e = bins.TryGetValue(k, out var v) ? v : (99f, -99f, -99f);
                    bins[k] = (UnityEngine.Mathf.Min(e.Item1, q.x), UnityEngine.Mathf.Max(e.Item2, q.x), UnityEngine.Mathf.Max(e.Item3, q.y));
                }
                var keys = new List<int>(bins.Keys); keys.Sort();
                foreach (var k in keys) Main.Log($"[fix] c{w} body z={k * 0.03f:F2} x=({bins[k].lo:F4},{bins[k].hi:F4}) mid={(bins[k].lo + bins[k].hi) / 2f:F4} top={bins[k].top:F4}");
                foreach (var r in go.GetComponentsInChildren<UnityEngine.Renderer>())
                    if (r.name.StartsWith(Mount.Prefix) || SightLab.RelPath(r.transform, go.transform).Contains(Mount.Prefix))
                    { var b = Lab.MeshBounds(r.transform, go.transform); Main.Log($"[fix] c{w} part {SightLab.RelPath(r.transform, go.transform)} x=({b.min.x:F4},{b.max.x:F4}) mid={b.center.x:F4} y=({b.min.y:F4},{b.max.y:F4})"); }
                Main.Log($"[fix] c{w} bore={Optics.BoreX(prefab)} reticle={(Optics.ReticleById.TryGetValue(w, out var rv) ? rv.ToString("F4") : "-")}");
                UnityEngine.Object.Destroy(holder);
            }));
            }
            s.Add(("mps5 skin", () =>
            {
                Store.SetForTest(27, Cat.Grip, "vertical_grip");
                var prefab = Tracker.ItemById(27).Weapon.gameObject;
                var holder = new UnityEngine.GameObject("FPC_skinprobe"); holder.SetActive(false);
                var go = UnityEngine.Object.Instantiate(prefab, holder.transform, false);
                Mount.Apply(go, prefab, 27);
                holder.SetActive(true);
                foreach (var sk in go.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(true))
                {
                    int nul = 0; foreach (var b in sk.bones) if (b == null) nul++;
                    var b0 = sk.bones.Length > 0 ? sk.bones[0] : null;
                    if (sk.gameObject.activeInHierarchy)
                    {
                        var m = new UnityEngine.Mesh(); sk.BakeMesh(m, true);
                        var v = m.vertices; var mn = UnityEngine.Vector3.one * 99f; var mx = -mn;
                        foreach (var p in v) { var q = go.transform.InverseTransformPoint(sk.transform.TransformPoint(p)); mn = UnityEngine.Vector3.Min(mn, q); mx = UnityEngine.Vector3.Max(mx, q); }
                        Main.Log($"[fix] baked {sk.name}: {v.Length} verts min={mn.ToString("F3")} max={mx.ToString("F3")}");
                    }
                    Main.Log($"[fix] skin {SightLab.RelPath(sk.transform, go.transform)} on={sk.enabled}/{sk.gameObject.activeInHierarchy} mesh={sk.sharedMesh?.name} bones={sk.bones.Length} null={nul} root={sk.rootBone?.name} bounds={sk.bounds} bone0={b0?.name}@{(b0 != null ? go.transform.InverseTransformPoint(b0.position) : UnityEngine.Vector3.zero)} s={(b0 != null ? b0.lossyScale : UnityEngine.Vector3.zero)}");
                }
                UnityEngine.Object.Destroy(holder);
            }));
            return s;
        }

        // 銃口の近く（前から 12cm）に見えている部品と範囲（武器の根元の座標）
        static void Parts(int id)
        {
            var prefab = Tracker.ItemById(id)?.Weapon?.gameObject;
            if (prefab == null || !Fits.Muzzles.TryGetValue(id, out var f)) return;
            foreach (var r in prefab.GetComponentsInChildren<UnityEngine.Renderer>(true))
            {
                if (!Lab.IsShown(r.transform, prefab.transform) || !r.enabled) continue;
                var b = Lab.MeshBounds(r.transform, prefab.transform);
                if (b.max.z < f.Tip - 0.12f && id != 20 && id != 14) continue;
                Main.Log($"[fix] [{id}] {SightLab.RelPath(r.transform, prefab.transform)} min=({b.min.x:F4},{b.min.y:F4},{b.min.z:F4}) max=({b.max.x:F4},{b.max.y:F4},{b.max.z:F4})");
            }
            var sp = Lab.SpawnPoints(prefab);
            Main.Log($"[fix] [{id}] tip={f.Tip:F4} axis={f.Axis:F4} back={f.Back:F4} spawn vis={sp.vis} phys={sp.phys}");
        }
    }
}
