using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // サイトの載せ方の確認（autotest -Dev -Scenario custoptic）。武器ごとに、付けられるサイトを1つずつ載せて横から撮る
    public static class OpticLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        public static void Register() => TestMode.ExternalScenarios["custoptic"] = Steps;

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "optic_*.png")) File.Delete(f); }));
            s.Add(("pistol widths", PistolWidths));
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                var w = wi;
                if (!Catalog.Eligible(w)) continue;
                s.Add(($"optic {w.Id}", () =>
                {
                    var opts = Catalog.For(w.Id, Cat.Sight).Where(a => !Catalog.IsFactory(a, w.Id)).ToList();
                    Main.Log($"[optic] [{w.Id}] {w.GetName()} options: {string.Join(",", Catalog.For(w.Id, Cat.Sight).Select(a => a.Id))}");
                    foreach (var a in opts) Shot(w, a);
                }));
            }
            return s;
        }

        // ハンドガンのスライドの幅（サイトの台座の前後位置で、台座の 3mm 下）。ドットサイトの大きさを SP-320 の幅との比で合わせる（Optics.Spot.Size）
        public static void PistolWidths()
        {
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                if (!Catalog.Eligible(wi) || !Catalog.IsPistol(wi.Id)) continue;
                var sp = Optics.SpotFor(wi.Id);
                if (sp == null) continue;
                var prefab = wi.Weapon?.gameObject;
                var go = SightLab.CloneStatic(prefab, out var holder);
                holder.SetActive(true);
                try
                {
                    Lab.HideNonMesh(go);
                    foreach (var fp in Factory.On(wi.Id)) Factory.Show(go.transform, fp, false);
                    float baseY = sp.BaseY + (wi.Id == 9 ? Optics.Get(Catalog.Get("p_dot")).Bottom : 0f);
                    // 上から見下ろす（台座の 3cm 上〜6mm 下だけ）。画の右が +X、上が前
                    const float halfH = 0.03f;
                    var tex = TopSlice(go, new Vector3(0f, baseY, sp.Z), halfH, baseY + 0.03f, baseY - 0.010f);
                    var ws = new List<float>();
                    for (float z = sp.Z + 0.004f; z <= sp.Z + 0.016f; z += 0.001f)   // 台座の前側（後ろはリアサイトやレバーが張り出す）
                    {
                        int py = Mathf.RoundToInt(SightLab.H / 2f + (z - sp.Z) / halfH * (SightLab.H / 2f));
                        var row = tex.GetPixels(0, py, SightLab.W, 1);
                        int l = -1, r = -1;
                        for (int px = 0; px < SightLab.W; px++) if (!SightLab.IsBg(row[px])) { if (l < 0) l = px; r = px; }
                        if (l >= 0) ws.Add((r - l + 1) / (SightLab.W / 2f) * halfH * 2f);
                    }
                    ws.Sort();
                    float w = ws.Count > 0 ? ws[ws.Count / 2] : 0f;
                    Main.Log($"[optic] WIDTH [{wi.Id}] {wi.GetName()} z={sp.Z:F4} baseY={baseY:F4} width={w:F4} rows={ws.Count}");
                    File.WriteAllBytes(Path.Combine(ShotDir, $"optic_{wi.Id:00}_width.png"), ImageConversion.EncodeToPNG(tex));
                    UnityEngine.Object.Destroy(tex);
                }
                catch (Exception e) { Main.Log($"[optic] [{wi.Id}] width failed: {e}"); }
                UnityEngine.Object.Destroy(holder);
            }
        }

        static Texture2D TopSlice(GameObject go, Vector3 center, float halfH, float yTop, float yBottom)
        {
            var root = go.transform;
            var camGo = new GameObject("FPC_LabCam"); var cam = camGo.AddComponent<Camera>();
            var lightGo = new GameObject("FPC_LabLight"); var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 2f;
            camGo.transform.position = root.TransformPoint(new Vector3(center.x, yTop + 1f, center.z));
            camGo.transform.rotation = Quaternion.LookRotation(-root.up, root.forward);
            lightGo.transform.rotation = camGo.transform.rotation;
            cam.orthographic = true; cam.orthographicSize = halfH;
            cam.nearClipPlane = 1f; cam.farClipPlane = 1f + (yTop - yBottom);
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = SightLab.Bg;
            var rt = new RenderTexture(SightLab.W, SightLab.H, 24);
            cam.targetTexture = rt; cam.aspect = (float)SightLab.W / SightLab.H;
            cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(SightLab.W, SightLab.H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, SightLab.W, SightLab.H), 0, 0); tex.Apply();
            RenderTexture.active = prev; cam.targetTexture = null;
            UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(camGo); UnityEngine.Object.Destroy(lightGo);
            return tex;
        }

        static void Shot(Il2Cpp.WeaponItem wi, Att a)
        {
            var prefab = wi.Weapon?.gameObject;
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                Lab.HideNonMesh(go);
                foreach (var fp in Factory.On(wi.Id)) if (Catalog.Get(fp.Att)?.Cat == Cat.Sight) Factory.Show(go.transform, fp, false);
                if (!Optics.Apply(go, prefab, wi.Id, a)) return;
                var r = Optics.LastReticle;
                var sh = SightLab.RenderSide(go, 0.07f, new Vector3(0f, r.y - 0.01f, r.z));
                // 照準の模様の中心の高さに赤い線
                int py = sh.PyOfY(r.y);
                for (int x = 0; x < SightLab.W; x += 2) if (py >= 0 && py < SightLab.H) sh.Tex.SetPixel(x, py, Color.red);
                sh.Tex.Apply();
                File.WriteAllBytes(Path.Combine(ShotDir, $"optic_{wi.Id:00}_{a.Id}.png"), ImageConversion.EncodeToPNG(sh.Tex));
                sh.Dispose();
                var back = LaserLab.Slice(go, new Vector3(0f, r.y - 0.01f, r.z), 0.05f, r.z - 0.08f, r.z + 0.08f);
                File.WriteAllBytes(Path.Combine(ShotDir, $"optic_{wi.Id:00}_{a.Id}_back.png"), ImageConversion.EncodeToPNG(back));
                UnityEngine.Object.Destroy(back);
            }
            catch (Exception e) { Main.Log($"[optic] [{wi.Id}] {a.Id} failed: {e}"); }
            UnityEngine.Object.Destroy(holder);
        }
    }
}
