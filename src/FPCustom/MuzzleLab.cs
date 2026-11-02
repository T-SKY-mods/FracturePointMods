using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 銃口の見直し（autotest -Dev -Scenario custmuz2）。メイン武器ごとに：
    //   ・銃口のまわりの描画物（銃口の部品が別の描画物か、銃身とまとめたメッシュか）
    //   ・銃身の太さ（銃口の部品の後ろの端のすぐ後ろ）・銃口の部品の太さ
    //   ・何も付けない銃口を横から撮る（shots/muz_<id>_bare.png）
    public static class MuzzleLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        public static void Register() => TestMode.ExternalScenarios["custmuz2"] = Steps;

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)> { ("setup", () => Directory.CreateDirectory(ShotDir)) };
            for (int id0 = 0; id0 < 64; id0++)
            {
                int id = id0; var w = Tracker.ItemById(id);
                if (w == null || !Catalog.Eligible(w) || !Fits.Muzzles.ContainsKey(id)) continue;
                s.Add(($"muzzle {id}", () => Survey(w)));
            }
            return s;
        }

        static string V(Vector3 v) => $"({v.x:F4},{v.y:F4},{v.z:F4})";

        static void Survey(Il2Cpp.WeaponItem wi)
        {
            int id = wi.Id;
            var f = Fits.Muzzles[id];
            var prefab = wi.Weapon.gameObject;
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                Lab.HideNonMesh(go);
                float rear = f.Tip - f.Back;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled || !r.gameObject.activeInHierarchy || !SightLab.IsMesh(r)) continue;
                    var b = SightLab.LocalBounds(r, go.transform);
                    if (b.max.z < rear - 0.03f || Mathf.Abs(b.center.y - f.Axis) > 0.05f && b.size.y < 0.1f) continue;
                    if (b.max.z < f.Tip - 0.06f) continue;
                    int verts = r.GetComponent<MeshFilter>()?.sharedMesh?.vertexCount ?? r.TryCast<SkinnedMeshRenderer>()?.sharedMesh?.vertexCount ?? -1;
                    string kind = b.min.z >= rear - 0.01f ? "DEVICE" : "MERGED";
                    Main.Log($"[muz] [{id}] {wi.GetName()} {kind} '{SightLab.RelPath(r.transform, go.transform)}' min={V(b.min)} max={V(b.max)} verts={verts}");
                }
                // 太さ：横から撮って、銃身の軸を含む縦の続き
                var sh = SightLab.RenderSide(go, 0.05f, new Vector3(0f, f.Axis, f.Tip - 0.05f));
                float Dia(float z)
                {
                    var col = sh.Tex.GetPixels(sh.PxOfZ(z), 0, 1, SightLab.H);
                    int c = sh.PyOfY(f.Axis);
                    if (c < 0 || c >= SightLab.H || SightLab.IsBg(col[c])) return 0f;
                    int lo = c, hi = c;
                    while (lo > 0 && !SightLab.IsBg(col[lo - 1])) lo--;
                    while (hi < SightLab.H - 1 && !SightLab.IsBg(col[hi + 1])) hi++;
                    return sh.YAt(hi) - sh.YAt(lo);
                }
                var prof = new List<string>();
                for (float z = rear - 0.03f; z <= f.Tip + 0.001f; z += 0.004f) prof.Add($"{z:F3}:{Dia(z) * 1000f:0.0}");
                // ハンドガン：スライドの高さ（先の 5mm・15mm 手前の縦の幅）
                float Span(float z, out float top, out float bot)
                {
                    var col = sh.Tex.GetPixels(sh.PxOfZ(z), 0, 1, SightLab.H);
                    int lo = -1, hi = -1;
                    for (int py = 0; py < SightLab.H; py++) if (!SightLab.IsBg(col[py])) { if (lo < 0) lo = py; hi = py; }
                    top = hi < 0 ? 0f : sh.YAt(hi); bot = lo < 0 ? 0f : sh.YAt(lo);
                    return top - bot;
                }
                // 銃口の面：軸から ±9mm（ハンドガンの部品の太さ）の中で、銃の一番前（部品の後ろの面を合わせる所）
                float face = float.NaN;
                for (float z = f.Tip + 0.01f; z >= f.Tip - 0.02f && float.IsNaN(face); z -= 0.00025f)
                {
                    var col = sh.Tex.GetPixels(sh.PxOfZ(z), 0, 1, SightLab.H);
                    for (int py = Mathf.Max(0, sh.PyOfY(f.Axis - 0.009f)); py <= Mathf.Min(SightLab.H - 1, sh.PyOfY(f.Axis + 0.009f)); py++)
                        if (!SightLab.IsBg(col[py])) { face = z; break; }
                }
                Main.Log($"[muz] FACE [{id}] {wi.GetName()} face={face:F4} tip={f.Tip:F4} d={(face - f.Tip) * 1000f:F2}mm");
                Span(f.Tip - 0.005f, out var t5, out var b5); Span(f.Tip - 0.015f, out var t15, out var b15);
                Main.Log($"[muz] [{id}] {wi.GetName()} pistol={Catalog.IsPistol(id)} span(tip-5mm)=({b5:F4}..{t5:F4}) span(tip-15mm)=({b15:F4}..{t15:F4})");
                Main.Log($"[muz] [{id}] {wi.GetName()} tip={f.Tip:F4} back={f.Back:F4} axis={f.Axis:F4} barrel(rear-5mm)={Dia(rear - 0.005f) * 1000f:0.0}mm dia(mm) {string.Join(" ", prof)}");
                File.WriteAllBytes(Path.Combine(ShotDir, $"muz_{id:00}_bare.png"), ImageConversion.EncodeToPNG(sh.Tex));
                sh.Dispose();
            }
            catch (Exception e) { Main.Log($"[muz] [{id}] failed: {e}"); }
            UnityEngine.Object.DestroyImmediate(holder);
        }
    }
}
