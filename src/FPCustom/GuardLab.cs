using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // レール付きハンドガードの下調べ（autotest -Dev -Scenario custguard）。
    // レールの無い武器の描画物（メッシュ名・頂点数・武器の根元から見た変換）を記録し、グリップ枠のまわりを横から撮る
    public static class GuardLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        public static void Register()
        {
            TestMode.ExternalScenarios["custguard"] = Steps;
            // 下調べをまとめて 1 回で（ハンドガード＋ハンドガンのスライドの幅）
            TestMode.ExternalScenarios["custguardfit"] = () => { var s = new List<(string, Action)> { ("setup", () => Directory.CreateDirectory(ShotDir)) }; foreach (var w in Tracker.Inv.WeaponData.Weapons.ToArray()) if (Grips.NeedsRail.Contains(w.Id)) { var x = w; s.Add(($"fit {x.Id}", () => Fit(x))); } return s; };
            // レールの無い（グリップ枠が AK-12 の部品の中にある）武器のハンドガードの下面を測る
            TestMode.ExternalScenarios["custrail"] = () => { var s = new List<(string, Action)> { ("setup", () => Directory.CreateDirectory(ShotDir)) }; foreach (var w in Tracker.Inv.WeaponData.Weapons.ToArray()) if (RailSurvey.Contains(w.Id) || w.Id == 28) { var x = w; s.Add(($"rail {x.Id}", () => { Under(x); Dump(x); })); } return s; };
            TestMode.ExternalScenarios["custsurvey"] = () => { var s = Steps(); s.Add(("pistol widths", OpticLab.PistolWidths)); return s; };
            // AK-74：木のハンドガードを外してレール付きハンドガードに替えるための下調べ（メッシュ・変換・ハンドガードの下面）
            TestMode.ExternalScenarios["custak74"] = () => new List<(string, Action)> { ("setup", () => Directory.CreateDirectory(ShotDir)), ("ak74", () => { foreach (var i in new[] { 0, 3, 10, 20 }) Dump(Tracker.ItemById(i)); }) };
        }

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "guard_*.png")) File.Delete(f); }));
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                var w = wi;
                if (!Grips.NeedsRail.Contains(w.Id)) continue;
                s.Add(($"guard {w.Id}", () => Dump(w)));
                s.Add(($"guard fit {w.Id}", () => Fit(w)));
            }
            return s;
        }

        static string V(Vector3 v) => $"({v.x:F4},{v.y:F4},{v.z:F4})";

        public static void Dump(Il2Cpp.WeaponItem wi)
        {
            var prefab = wi.Weapon?.gameObject;
            if (prefab == null) return;
            var root = prefab.transform;
            Main.Log($"[guard] ===== [{wi.Id}] {wi.GetName()} slotZ={Grips.SlotZ(wi.Id)?.ToString("F4") ?? "-"}");
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                var sk = r.TryCast<SkinnedMeshRenderer>();
                if (sk == null && r.TryCast<MeshRenderer>() == null) continue;
                if (!Lab.IsShown(r.transform, root) || !r.enabled) continue;
                Mesh m = sk != null ? sk.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (m == null) continue;
                var lb = SightLab.LocalBounds(r, root);
                var M = root.worldToLocalMatrix * r.transform.localToWorldMatrix;
                string row(int i) => $"{M[i, 0]:F5},{M[i, 1]:F5},{M[i, 2]:F5},{M[i, 3]:F5}";
                Main.Log($"[guard]   '{SightLab.RelPath(r.transform, root)}' mesh='{m.name}' verts={m.vertexCount} subs={m.subMeshCount} readable={m.isReadable} skinned={sk != null} center={V(lb.center)} size={V(lb.size)} M=[{row(0)};{row(1)};{row(2)}]");
            }
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                Lab.HideNonMesh(go);
                foreach (var fp in Factory.On(wi.Id)) Factory.Show(go.transform, fp, false);
                float z = Grips.SlotZ(wi.Id) ?? 0.2f;
                float axis = Fits.Muzzles.TryGetValue(wi.Id, out var mz) ? mz.Axis : 0f;
                var sh = SightLab.RenderSide(go, 0.12f, new Vector3(0f, axis - 0.02f, z));
                Grid(sh, z, axis - 0.02f);
                File.WriteAllBytes(Path.Combine(ShotDir, $"guard_{wi.Id:00}.png"), ImageConversion.EncodeToPNG(sh.Tex));
                Main.Log($"[guard]   shot center z={z:F4} y={axis - 0.02f:F4} (grid 1cm, red=5cm; z+ is right)");
                sh.Dispose();
            }
            catch (Exception e) { Main.Log($"[guard] [{wi.Id}] failed: {e}"); }
            UnityEngine.Object.Destroy(holder);
        }

        public static readonly int[] RailSurvey = { 16, 17, 19, 27 };

        // 列 z で、銃身の中心から下へ見て銃の描画が終わる高さ（下面）。銃身の中心に描画が無ければ NaN
        public static float BottomAt(SightLab.Shot s, float z, float axis)
        {
            int px = s.PxOfZ(z);
            if (px < 0 || px >= SightLab.W) return float.NaN;
            var c = s.Tex.GetPixels(px, 0, 1, SightLab.H);
            int py = Mathf.Clamp(s.PyOfY(axis), 0, SightLab.H - 1);
            if (SightLab.IsBg(c[py])) return float.NaN;
            for (; py > 0; py--) if (SightLab.IsBg(c[py])) return s.YAt(py + 1);
            return float.NaN;
        }

        static void Under(Il2Cpp.WeaponItem wi)
        {
            var prefab = wi.Weapon?.gameObject;
            var pdw = prefab?.GetComponent<Il2CppDemo.Scripts.Runtime.Item.Weapon>();
            var l = pdw?.gripAttachments?.attachments;
            var sb = new System.Text.StringBuilder($"[guard] RAIL [{wi.Id}] {wi.GetName()} slots=");
            var slotPaths = new List<string>();
            for (int i = 0; l != null && i < l.Count; i++)
            {
                if (l[i] == null) continue;
                var t = l[i].transform; slotPaths.Add(SightLab.RelPath(t, prefab.transform));
                sb.Append($"{i}:'{slotPaths[^1]}' at={V(prefab.transform.InverseTransformPoint(t.position))} active={pdw.gripAttachments._activeIndex == i}; ");
            }
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                Lab.HideNonMesh(go);
                foreach (var fp in Factory.On(wi.Id)) Factory.Show(go.transform, fp, false);
                foreach (var sp in slotPaths) { var t = go.transform.Find(sp); if (t != null) foreach (var r in t.GetComponentsInChildren<Renderer>(true)) r.enabled = false; }
                float axis = Fits.Muzzles.TryGetValue(wi.Id, out var mz) ? mz.Axis : 0f;
                float tip = Fits.Muzzles.ContainsKey(wi.Id) ? mz.Tip : 0.45f;
                var sh = SightLab.RenderSide(go, 0.16f, new Vector3(0f, axis - 0.04f, 0.22f));
                sb.Append($" axis={axis:F4} tip={tip:F4} bottom(z cm:y mm)=");
                for (float z = 0f; z <= tip; z += 0.005f) { float b = BottomAt(sh, z, axis); if (!float.IsNaN(b)) sb.Append($"{z * 100:F1}:{b * 1000:F1} "); }
                Main.Log(sb.ToString());
                Grid(sh, 0.22f, axis - 0.04f);
                File.WriteAllBytes(Path.Combine(ShotDir, $"guard_{wi.Id:00}_under.png"), ImageConversion.EncodeToPNG(sh.Tex));
                sh.Dispose();
            }
            catch (Exception e) { Main.Log($"[guard] [{wi.Id}] under failed: {e}"); }
            UnityEngine.Object.Destroy(holder);
        }

        // 1cm ごとの目盛り（5cm ごとに濃く。z, y の線を基準に）
        public static void Grid(SightLab.Shot sh, float z, float y0)
        {
            for (int k = -30; k <= 30; k++)
            {
                int px = sh.PxOfZ(z + k * 0.01f); int py = sh.PyOfY(y0 + k * 0.01f);
                var c = k % 5 == 0 ? new Color(1f, 0f, 0f) : new Color(1f, 0.6f, 0.6f);
                if (px >= 0 && px < SightLab.W) for (int y = 0; y < SightLab.H; y += 3) sh.Tex.SetPixel(px, y, c);
                if (py >= 0 && py < SightLab.H) for (int x = 0; x < SightLab.W; x += 3) sh.Tex.SetPixel(x, py, c);
            }
            sh.Tex.Apply();
        }

        // グリップ＋レール（レール付きハンドガード）を付けて横・後ろから撮る
        static void Fit(Il2Cpp.WeaponItem wi)
        {
            var prefab = wi.Weapon?.gameObject;
            var pdw = prefab?.GetComponent<Il2CppDemo.Scripts.Runtime.Item.Weapon>();
            int idx = Grips.SlotIndex(pdw, Catalog.Get("vertical_grip"));
            if (idx < 0) return;
            var path = SightLab.RelPath(pdw.gripAttachments.attachments[idx].transform, prefab.transform);
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                Lab.HideNonMesh(go);
                foreach (var fp in Factory.On(wi.Id)) Factory.Show(go.transform, fp, false);
                var slot = go.transform.Find(path);
                slot.gameObject.SetActive(true);
                var g = Grips.Place(slot, go.transform, wi.Id);
                Guards.Apply(go, prefab, wi.Id, g);
                var gb = Lab.MeshBounds(g.transform, go.transform);
                var gd = go.transform.Find(Mount.Prefix + "guard") ?? go.transform.Find(Mount.Prefix + "rail");
                var b = gd != null ? Lab.MeshBounds(gd, go.transform) : new Bounds();
                Main.Log($"[guard] FIT [{wi.Id}] {wi.GetName()} grip top={gb.max.y:F4} z={gb.center.z:F4} part={(gd != null ? gd.name : "-")} min={V(b.min)} max={V(b.max)}");
                var c = new Vector3(0f, gb.max.y, gb.center.z);
                var a = SightLab.RenderSide(go, 0.09f, c);
                File.WriteAllBytes(Path.Combine(ShotDir, $"guard_{wi.Id:00}_fit.png"), ImageConversion.EncodeToPNG(a.Tex)); a.Dispose();
                var bk = LaserLab.Slice(go, c, 0.06f, gb.center.z - 0.02f, gb.center.z + 0.02f);
                File.WriteAllBytes(Path.Combine(ShotDir, $"guard_{wi.Id:00}_back.png"), ImageConversion.EncodeToPNG(bk)); UnityEngine.Object.Destroy(bk);
            }
            catch (Exception e) { Main.Log($"[guard] [{wi.Id}] fit failed: {e}"); }
            UnityEngine.Object.Destroy(holder);
        }
    }
}
