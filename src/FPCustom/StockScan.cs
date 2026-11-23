using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 調査用（autotest -Dev -Scenario custstockscan）。メイン武器ごとに、見えている描画物の名前・範囲（武器の根元の座標）と
    // 後ろの部分（ストックのあたり）を書き出し、横から撮る（shots/stockscan_<id>.png）。メイン武器のストックの実装の下調べ
    public static class StockScan
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        public static void Register()
        {
            RegisterShots();
            RegisterScan();
            TestMode.ExternalScenarios["custsmgscan"] = () => new List<(string, Action)> { ("smg 27", () => SmgScan(27, "mp5/Magazine_release_button_1", "mps5")), ("smg 28", () => SmgScan(28, "mp5 custom/Magazine_release_button_1", "viper")) };
        }

        // 動くメッシュひとつの SMG（MPS-5・Viper）：骨ごとの形を置いて、武器の根元の座標で後ろの部分の断面（z 5mm ごとの x・y の範囲）を書き出す
        static void SmgScan(int id, string path, string src)
        {
            var prefab = Tracker.ItemById(id)?.Weapon?.gameObject;
            if (prefab == null) return;
            var go = SightLab.CloneStatic(prefab, out var holder);
            try
            {
                holder.SetActive(true);
                var root = go.transform;
                var sk = root.Find(path)?.GetComponent<SkinnedMeshRenderer>();
                if (sk == null) { Main.Log($"[smgscan] [{id}] no renderer {path}"); return; }
                MuzzleCut.SplitSkinned(sk, src);
                var pts = new List<Vector3>();
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith(Mount.Prefix + MuzzleCut.PartName)) continue;
                    var mesh = t.GetComponent<MeshFilter>()?.sharedMesh; if (mesh == null) continue;
                    var m = root.worldToLocalMatrix * t.localToWorldMatrix;
                    foreach (var v in mesh.vertices) pts.Add(m.MultiplyPoint3x4(v));
                    Main.Log($"[smgscan] [{id}] part {t.name} under {t.parent.name} verts={mesh.vertexCount}");
                }
                for (float z = -0.32f; z < 0.0f; z += 0.005f)
                {
                    float x0 = 9, x1 = -9, y0 = 9, y1 = -9; int n = 0;
                    foreach (var p in pts) if (p.z >= z && p.z < z + 0.005f) { n++; x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y); }
                    if (n > 0) Main.Log($"[smgscan] [{id}] z {z:F3}: n={n} x {x0:F4}..{x1:F4} y {y0:F4}..{y1:F4}");
                }
            }
            catch (Exception e) { Main.Log($"[smgscan] [{id}] failed: {e}"); }
            finally { UnityEngine.Object.Destroy(holder); }
        }


        static void RegisterScan() => TestMode.ExternalScenarios["custstockscan"] = () =>
        {
            var s = new List<(string, Action)> { ("setup", () => Directory.CreateDirectory(ShotDir)) };
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
                if (Catalog.Eligible(wi) && !Catalog.IsPistol(wi.Id) && Catalog.Checked(wi.Id)) { var w = wi; s.Add(($"scan {w.Id}", () => Scan(w))); }
            return s;
        };

        // ストックを付けた横の絵（autotest -Dev -Scenario custrstock）。shots/rstock_<id>_<att>.png
        public static void RegisterShots() => TestMode.ExternalScenarios["custrstock"] = () =>
        {
            var s = new List<(string, Action)> { ("setup", () => Directory.CreateDirectory(ShotDir)) };
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                if (!Catalog.Eligible(wi) || Catalog.IsPistol(wi.Id) || !RifleStocks.Fits.ContainsKey(wi.Id)) continue;
                var w = wi;
                // 1 枚ずつ別のフレームで撮る（前の絵の複製はフレームの終わりに消えるので、同じフレームで続けて撮ると重なる）
                foreach (var id in new[] { "none", "stock_mcx", "stock_prs", "stock_sba3", "stock_moe" })
                {
                    var att = id;
                    s.Add(($"rstock {w.Id} {att}", () =>
                    {
                        Store.SetForTest(w.Id, Cat.Stock, att == "none" ? null : att);
                        Preview.Render(w.Id);
                        Preview.Save(Path.Combine(ShotDir, $"rstock_{w.Id}_{att}.png"));
                        if (att == "stock_moe") Store.SetForTest(w.Id, Cat.Stock, null);
                    }));
                }
            }
            return s;
        };

        static string V(Vector3 v) => $"({v.x:F4},{v.y:F4},{v.z:F4})";

        static void Scan(Il2Cpp.WeaponItem wi)
        {
            var prefab = wi.Weapon?.gameObject;
            if (prefab == null) return;
            var go = SightLab.CloneStatic(prefab, out var holder);
            try
            {
                holder.SetActive(true);
                Lab.HideNonMesh(go);
                var root = go.transform;
                var all = Lab.MeshBounds(root, root);
                Main.Log($"[stockscan] [{wi.Id}] {wi.GetName()} all min={V(all.min)} max={V(all.max)}");
                foreach (var r in go.GetComponentsInChildren<Renderer>(false))
                {
                    if (!SightLab.IsMesh(r) || !r.enabled) continue;
                    Bounds b;
                    var sk = r.TryCast<SkinnedMeshRenderer>();
                    if (sk != null && sk.sharedMesh != null)
                    {
                        sk.updateWhenOffscreen = true;
                        b = SightLab.LocalBounds(r, root);
                    }
                    else b = Lab.MeshBounds(r.transform, root);
                    var mesh = sk != null ? sk.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                    var m = root.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    string mat = string.Join(",", r.sharedMaterials.Select(x => x == null ? "null" : x.name));
                    Main.Log($"[stockscan] [{wi.Id}]   {(sk != null ? "S" : "M")} '{SightLab.RelPath(r.transform, root)}' mesh='{mesh?.name}' verts={mesh?.vertexCount} min={V(b.min)} max={V(b.max)} mats=[{mat}] M=[{m.m00:F5} {m.m01:F5} {m.m02:F5} {m.m03:F5} {m.m10:F5} {m.m11:F5} {m.m12:F5} {m.m13:F5} {m.m20:F5} {m.m21:F5} {m.m22:F5} {m.m23:F5}]");
                }
            }
            catch (Exception e) { Main.Log($"[stockscan] [{wi.Id}] failed: {e}"); }
            finally { UnityEngine.Object.Destroy(holder); }
            try { Preview.Render(wi.Id); Preview.Save(Path.Combine(ShotDir, $"stockscan_{wi.Id}.png")); }
            catch (Exception e) { Main.Log($"[stockscan] [{wi.Id}] shot failed: {e.Message}"); }
        }
    }
}
