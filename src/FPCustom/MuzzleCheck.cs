using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 銃口の部品の位置のずれ（autotest -Dev -Scenario custmuzfit）。武器ごとに、銃口の部品を外した銃身を測り、付けた部品と比べる：
    //   ・銃口の穴の中心（先端の薄い輪切りを前から撮り、外とつながっていない背景の画素＝穴の中心）と、部品の中心の左右・上下の差
    //   ・銃身の先端（横から撮り、銃身の中心の高さで一番前の描画）と、部品の後ろの端の前後の差（＋は隙間）
    //   ・Fits の Tip・Axis と測った値の差
    // ずれた物は横と前の画を撮る（shots/muzfit_*.png）
    public static class MuzzleCheck
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        const float OffWarn = 0.0008f;
        // 銃身のまわりに太い筒（テープを巻いた覆い）があり、横・上の輪郭では銃身を測れない武器（正面の画で合わせた）
        static readonly HashSet<int> Shroud = new() { 29 };

        public static void Register()
        {
            TestMode.ExternalScenarios["custmuzfit"] = () =>
            {
                var s = new List<(string, Action)> { ("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "muzfit_*.png")) File.Delete(f); }) };
                for (int id0 = 0; id0 < 64; id0++)
                {
                    int id = id0; var w = Tracker.ItemById(id);
                    if (w == null || !Catalog.Eligible(w) || !Fits.Muzzles.ContainsKey(id)) continue;
                    s.Add(($"muzfit {id}", () => Weapon(w)));
                }
                return s;
            };
        }

        static void Prepare(GameObject go, Il2Cpp.WeaponItem wi)
        {
            Lab.HideNonMesh(go);
            foreach (var fp in Factory.On(wi.Id)) if (Catalog.Get(fp.Att)?.Cat == Cat.Muzzle) Factory.Show(go.transform, fp, false);
            if (Fits.Devices.TryGetValue(wi.Id, out var dev) && go.transform.Find(dev)?.GetComponent<Renderer>() is { } dr) dr.enabled = false;
            MuzzleCut.Apply(go, wi.Weapon.gameObject, wi.Id);
        }

        static void Weapon(Il2Cpp.WeaponItem wi)
        {
            int id = wi.Id; var f = Fits.Muzzles[id]; var name = wi.GetName();
            var prefab = wi.Weapon.gameObject;
            // 1) 部品なしの銃身
            float tip, bx, by, bore;
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                Prepare(go, wi);
                tip = TipZ(go, f);
                // 部品の後ろの端の 3mm 手前（銃口の部品を外した武器は銃身の先）で測る
                // 部品の後ろの端（銃口の部品を外した武器は銃身の先）から 1〜15mm 手前で、一番細い所（銃身だけの所）
                float zr = f.Back > 0f ? f.Tip - f.Back : tip;
                (by, float dv, float zm) = Thinnest(go, f.Axis, zr - 0.015f, zr - 0.001f, false);
                (bx, float dh, _) = Thinnest(go, f.X, zm, zm, true);
                bore = Mathf.Max(dv, dh);
                Main.Log($"[muzfit] [{id}] {name} barrel tip={tip:F4} (Fits {f.Tip:F4}, d {(tip - f.Tip) * 1000f:F1}mm) at z={zm:F4} center=({bx:F4},{by:F4}) (Axis {f.Axis:F4}, dy {(by - f.Axis) * 1000f:F1}mm) size={dh * 1000f:F1}x{dv * 1000f:F1}mm back={f.Back:F4}");
                // 銃口のまわりの描画物（銃口の部品が別の描画物か、銃身とひとつのメッシュか）
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled || !r.gameObject.activeInHierarchy || !SightLab.IsMesh(r)) continue;
                    var lb = SightLab.LocalBounds(r, go.transform);
                    if (lb.max.z < zr - 0.03f || lb.min.z > tip + 0.01f || lb.min.y > by + 0.015f || lb.max.y < by - 0.015f) continue;
                    var mesh = r.TryCast<SkinnedMeshRenderer>()?.sharedMesh ?? r.GetComponent<MeshFilter>()?.sharedMesh;
                    var M = go.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    string row(int i) => $"{M[i, 0]:F5} {M[i, 1]:F5} {M[i, 2]:F5} {M[i, 3]:F5}";
                    Main.Log($"[muzfit] [{id}]   near '{SightLab.RelPath(r.transform, go.transform)}' M='{row(0)} {row(1)} {row(2)}' mesh='{mesh?.name}' verts={mesh?.vertexCount} min=({lb.min.x:F4},{lb.min.y:F4},{lb.min.z:F4}) max=({lb.max.x:F4},{lb.max.y:F4},{lb.max.z:F4})");
                }
                var fr = SightLab.RenderFront(go, 0.012f, f.At(tip));
                File.WriteAllBytes(Path.Combine(ShotDir, $"muzfit_{id:00}_front.png"), ImageConversion.EncodeToPNG(fr.Tex)); fr.Dispose();
                var bare = SightLab.RenderSide(go, 0.03f, new Vector3(0f, by, zr));   // 部品なしの銃口（元から付いている部品が残っていないか）
                File.WriteAllBytes(Path.Combine(ShotDir, $"muzfit_{id:00}_bare.png"), ImageConversion.EncodeToPNG(bare.Tex)); bare.Dispose();
            }
            catch (Exception e) { Main.Log($"[muzfit] [{id}] barrel failed: {e}"); UnityEngine.Object.DestroyImmediate(holder); return; }
            UnityEngine.Object.DestroyImmediate(holder);

            // 2) 部品ごと
            foreach (var a in Catalog.For(id, Cat.Muzzle))
            {
                if (Catalog.IsFactory(a, id) || a.Source == Source.StatsOnly) continue;
                AssetParts.Model m;
                try { m = AssetParts.MuzzleModel(id, a); } catch { continue; }
                if (m == null) continue;
                go = SightLab.CloneStatic(prefab, out holder);
                holder.SetActive(true);
                try
                {
                    Prepare(go, wi);
                    var p = AssetParts.AttachRear(go.transform, m, f.At(AssetParts.RearZ(id, m, f.Tip, f.Back)), a.Id);
                    var pb = Lab.MeshBounds(p.transform, go.transform);
                    float mx = pb.center.x, my = pb.center.y;
                    float dx = mx - bx, dy = my - by, gap = pb.min.z - tip;
                    // 隙間：銃身の先端と部品の後ろの端の間の、銃身の中心の高さの列に描画の無い列
                    int hole = pb.min.z > tip - 0.0005f ? GapColumns(go, by, tip - 0.0005f, pb.min.z + 0.0005f) : 0;   // 部品が銃身にかぶっていれば隙間なし（穴の開いた部品の穴を数えない）
                    bool ok = (Catalog.IsPistol(id) || Shroud.Contains(id) || Mathf.Abs(dx) <= OffWarn && Mathf.Abs(dy) <= OffWarn) && hole == 0;   // ハンドガンの横・上の輪郭はスライド（銃口の穴は正面の画で見る）
                    Main.Log($"[muzfit] [{id}] {name} {a.Id} ({m.Name}) center=({mx:F4},{my:F4}) dx={dx * 1000f:F1}mm dy={dy * 1000f:F1}mm rear={pb.min.z:F4} gap={gap * 1000f:F1}mm open={hole}px size={pb.size.x * 1000f:F1}x{pb.size.y * 1000f:F1}x{pb.size.z * 1000f:F1}mm");
                    Check($"{name} {a.Id}: on the bore, no gap", ok);
                    var sh = SightLab.RenderSide(go, Catalog.IsPistol(id) ? 0.03f : 0.05f, new Vector3(0f, by, tip));
                    File.WriteAllBytes(Path.Combine(ShotDir, $"muzfit_{id:00}_{a.Id}{(ok ? "" : "_NG")}.png"), ImageConversion.EncodeToPNG(sh.Tex));
                    sh.Dispose();
                }
                catch (Exception e) { Main.Log($"[muzfit] [{id}] {a.Id} failed: {e}"); }
                UnityEngine.Object.DestroyImmediate(holder);
            }
        }

        // 横から撮り、銃身の中心の高さ ±1.5mm の行で一番前の描画の z
        static float TipZ(GameObject go, Fits.Muzzle f)
        {
            var sh = SightLab.RenderSide(go, 0.03f + f.Back / 2f, new Vector3(0f, f.Axis, f.Tip));
            try
            {
                int y0 = sh.PyOfY(f.Axis - 0.0015f), y1 = sh.PyOfY(f.Axis + 0.0015f);
                for (int px = Mathf.Min(SightLab.W - 1, sh.PxOfZ(f.Tip + 0.03f)); px > Mathf.Max(0, sh.PxOfZ(f.Tip - f.Back - 0.02f)); px--)
                {
                    var col = sh.Tex.GetPixels(px, y0, 1, y1 - y0 + 1);
                    if (col.Any(c => !SightLab.IsBg(c))) return sh.ZAt(px);
                }
                return f.Tip;
            }
            finally { sh.Dispose(); }
        }

        // 銃身の中心の列で、z0..z1 の間に描画の無い（上下 ±1mm すべて背景の）列の数
        static int GapColumns(GameObject go, float y, float z0, float z1)
        {
            var sh = SightLab.RenderSide(go, Mathf.Max(0.02f, (z1 - z0) / 3f), new Vector3(0f, y, (z0 + z1) / 2f));
            try
            {
                int n = 0, a = sh.PyOfY(y - 0.001f), b = sh.PyOfY(y + 0.001f);
                for (int px = Mathf.Max(0, sh.PxOfZ(z0)); px <= Mathf.Min(SightLab.W - 1, sh.PxOfZ(z1)); px++)
                    if (sh.Tex.GetPixels(px, a, 1, b - a + 1).All(SightLab.IsBg)) n++;
                return n;
            }
            finally { sh.Dispose(); }
        }

        // z0..z1 の各列で、中心の位置 c0 を含む描画の続き（横から：上下。上から：左右）を測り、一番細い所の中心・幅・z
        static (float c, float d, float z) Thinnest(GameObject go, float c0, float z0, float z1, bool top)
        {
            const float H2 = 0.02f; int W = SightLab.W, H = SightLab.H;
            float zc = (z0 + z1) / 2f;
            Texture2D tex = top ? SightLab.RenderTop(go, H2, new Vector3(c0, 0.5f, zc)) : SightLab.RenderSide(go, H2, new Vector3(0f, c0, zc)).Tex;
            try
            {
                (float c, float d, float z) best = (c0, 0f, zc);
                for (float z = z0; z <= z1 + 1e-5f; z += 0.0005f)
                {
                    Color[] line; int n, i0; Func<int, float> at;
                    if (top)
                    {
                        int py = Mathf.Clamp(Mathf.RoundToInt(H / 2f + (z - zc) / H2 * (H / 2f)), 0, H - 1);
                        line = tex.GetPixels(0, py, W, 1); n = W; i0 = W / 2; at = i => c0 + (i - W / 2f) / (W / 2f) * H2 * 2f;
                    }
                    else
                    {
                        int px = Mathf.Clamp(Mathf.RoundToInt(W / 2f + (z - zc) / (H2 * 2f) * (W / 2f)), 0, W - 1);
                        line = tex.GetPixels(px, 0, 1, H); n = H; i0 = H / 2; at = i => c0 + (i - H / 2f) / (H / 2f) * H2;
                    }
                    if (SightLab.IsBg(line[i0])) continue;
                    int lo = i0, hi = i0;
                    while (lo > 0 && !SightLab.IsBg(line[lo - 1])) lo--;
                    while (hi < n - 1 && !SightLab.IsBg(line[hi + 1])) hi++;
                    float d = at(hi + 1) - at(lo);
                    if (best.d == 0f || d < best.d - 1e-5f) best = ((at(lo) + at(hi + 1)) / 2f, d, z);
                }
                return best;
            }
            finally { UnityEngine.Object.Destroy(tex); }
        }
    }
}
