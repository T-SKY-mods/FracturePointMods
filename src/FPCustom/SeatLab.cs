using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 付けたサイトが台座（レール・マウント・銃の上面）から浮いていないか（autotest -Dev -Scenario custseat）。
    // 全武器・全サイトで、横から「銃（とマウント）だけ」「サイトだけ」を描き、列ごとにサイトの下端とその真下の銃の上端のすき間を測る
    public static class SeatLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        const float FloatWarn = 0.0006f;   // 0.6mm 以上のすき間は浮いて見える

        public static void Register() => TestMode.ExternalScenarios["custseat"] = Steps;

        public static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "seat_*.png")) File.Delete(f); }));
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                if (!Catalog.Eligible(wi)) continue;
                var w = wi;
                foreach (var a0 in Catalog.For(w.Id, Cat.Sight))
                {
                    var a = a0;
                    if (Catalog.IsFactory(a, w.Id) || !Optics.Supported(w.Id, a)) continue;
                    s.Add(($"seat {w.Id} {a.Id}", () => Seat(w, a)));
                }
            }
            return s;
        }

        static void Seat(Il2Cpp.WeaponItem wi, Att a)
        {
            var prefab = wi.Weapon?.gameObject;
            if (prefab == null) return;
            int id = wi.Id; var name = Tracker.NameOf(id);
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                Lab.HideNonMesh(go);
                foreach (var fp in Factory.On(id)) if (Catalog.Get(fp.Att)?.Cat == Cat.Sight) Factory.Show(go.transform, fp, false);
                if (!Optics.Apply(go, prefab, id, a)) { Main.Log($"[seat] [{id}] {a.Id}: not applied"); return; }
                var root = go.transform;
                var optic = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == Mount.Prefix + "optic_" + a.Id);
                if (optic == null) { Main.Log($"[seat] [{id}] {a.Id}: optic not found"); return; }
                var ob = Lab.MeshBounds(optic, root);
                float halfH = Mathf.Max(0.012f, ob.size.z * 0.6f / 2f);
                var center = new Vector3(0f, ob.min.y + halfH * 0.3f, ob.center.z);
                var opticR = optic.GetComponentsInChildren<Renderer>(true).ToList();
                var all = go.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToList();
                var both = SightLab.RenderSide(go, halfH, center);
                foreach (var r in opticR) r.enabled = false;
                var gun = SightLab.RenderSide(go, halfH, center);
                foreach (var r in all) r.enabled = opticR.Contains(r);
                var only = SightLab.RenderSide(go, halfH, center);
                foreach (var r in all) r.enabled = true;

                // 列ごと：サイトの一番下の画素と、そこから下へ最初の銃の画素のすき間（px）
                bool Px(Texture2D t, int x, int y) { var c = t.GetPixel(x, y); return Mathf.Abs(c.r - SightLab.Bg.r) + Mathf.Abs(c.g - SightLab.Bg.g) + Mathf.Abs(c.b - SightLab.Bg.b) > 0.06f; }
                var gaps = new List<(int x, int gap)>();
                for (int x = 0; x < SightLab.W; x += 2)
                {
                    int bottom = -1;
                    for (int y = 0; y < SightLab.H; y++) if (Px(only.Tex, x, y)) { bottom = y; break; }
                    if (bottom < 0) continue;
                    int top = -1;
                    for (int y = bottom; y >= 0; y--) if (Px(gun.Tex, x, y)) { top = y; break; }
                    gaps.Add((x, top < 0 ? SightLab.H : bottom - top));
                }
                float mPerPx = halfH * 2f / SightLab.H;
                if (gaps.Count == 0) { Main.Log($"[seat] [{id}] {a.Id}: sight not drawn"); return; }
                int x0 = gaps.First().x, x1 = gaps.Last().x, n = gaps.Count;
                float Min(IEnumerable<(int x, int gap)> g) => g.Any() ? g.Min(v => v.gap) * mPerPx : float.NaN;
                float all0 = Min(gaps);
                float rear = Min(gaps.Where(g => g.x < x0 + (x1 - x0) * 0.35f));
                float front = Min(gaps.Where(g => g.x > x1 - (x1 - x0) * 0.35f));
                // 小さい方から 1/4 の列のすき間（ピンなど細い出っ張りだけが接していて、底の面は浮いているもの）
                var sorted = gaps.Select(g => g.gap).OrderBy(v => v).ToList();
                float q25 = sorted[sorted.Count / 4] * mPerPx;
                // ハンドガンのスライドは平ら（ライフルのレールは歯のすき間があるので見ない）
                if (a.Pistol) Check($"{name} {a.Id}: bottom of the sight sits on the slide (quarter gap {q25 * 1000:F1}mm)", q25 <= 0.0004f);
                // 銃の上にかかっている列のうち、すき間が 0.3mm 以下（接している）の割合
                float touch = gaps.Count(g => g.gap * mPerPx <= 0.0003f) / (float)n;
                Main.Log($"[seat] [{id}] {name} {a.Id}: gap min={all0 * 1000:F2}mm rear={rear * 1000:F2}mm front={front * 1000:F2}mm touching={touch * 100:F0}% q25={q25 * 1000:F2}mm sight z={ob.min.z:F4}..{ob.max.z:F4} bottom={ob.min.y:F4}");
                Check($"{name} {a.Id}: sight sits on the gun (gap {all0 * 1000:F1}mm)", all0 <= FloatWarn);
                Check($"{name} {a.Id}: rear and front of the sight sit on the gun (rear {rear * 1000:F1}mm, front {front * 1000:F1}mm)", rear <= 0.0015f && front <= 0.0015f);
                File.WriteAllBytes(Path.Combine(ShotDir, $"seat_{id:00}_{a.Id}.png"), ImageConversion.EncodeToPNG(both.Tex));
                both.Dispose(); gun.Dispose(); only.Dispose();
            }
            catch (Exception e) { Main.Log($"[seat] [{id}] {a.Id} failed: {e}"); }
            finally { UnityEngine.Object.Destroy(holder); }
        }
    }
}
