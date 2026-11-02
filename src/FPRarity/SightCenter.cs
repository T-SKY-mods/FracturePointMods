using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

using FPModOption;

namespace FPRarity
{
    // 確認用（autotest -Scenario sightcenter）。ゲームの状態は変えない（プレハブの複製だけで測る）。
    // サイト対応の全武器で、サイトを載せる位置の前後 ±2cm だけを後ろから撮り、
    // 「サイトの下の銃（スライド・機関部）の上面の左右の中心」と「載せたサイトの左右の中心」のずれを測る。
    public static class SightCenter
    {
        const float Slab = 0.02f;   // 前後に写す範囲

        public static List<(string, Action)> Steps()
        {
            var steps = new List<(string, Action)>();
            foreach (var id in SightMount.Config.Keys.OrderBy(k => k))
            {
                int wid = id;
                steps.Add(($"center {wid}", () => Measure(wid)));
            }
            return steps;
        }

        // 手に持った状態（待機のアニメーション中）で同じように測る（autotest -Scenario sightcenterlive）
        public static List<(string, Action)> LiveSteps()
        {
            var steps = new List<(string, Action)>();
            foreach (var id in SightMount.Config.Keys.OrderBy(k => k))
            {
                int wid = id;
                steps.Add(($"add {wid}", () =>
                {
                    Mastery.SetKillsForTest(wid, Mastery.KillsForLevel(Mastery.SightLevel));
                    Mastery.SetSight(wid, true);
                    if (!Tracker.Held.Any(h => h.Roll.WeaponId == wid)) TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(wid));
                }));
                steps.Add(("wait", () => { }));
                steps.Add(($"equip {wid}", () =>
                {
                    var name = Tracker.ItemById(wid).Weapon.gameObject.name;
                    var inst = Refs.Fps.InstantiatedWeapons;
                    for (int i = 0; i < inst.Count; i++)
                        if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == name) { Refs.Fps.StartWeaponChange(i); return; }
                }));
                steps.Add(("wait", () => { }));
                steps.Add(("wait", () => { }));
                steps.Add(($"live {wid}", () =>
                {
                    var go = Refs.Fps.GetActiveItem()?.gameObject;
                    if (go == null || go.name.Replace("(Clone)", "").Trim() != Tracker.ItemById(wid).Weapon.gameObject.name) { Main.Log($"[center] live [{wid}] not equipped"); return; }
                    Report(wid, "live", go, 1 << go.layer, null);
                }));
                steps.Add(($"cleanup {wid}", () => TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(wid))));
                steps.Add(("wait", () => { }));
            }
            return steps;
        }

        static void Measure(int id)
        {
            var spot = SightMount.Config[id];
            var prefab = Tracker.ItemById(id)?.Weapon?.gameObject;
            if (prefab == null) return;
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                Report(id, "prefab", go, -1, () => SightMount.Apply(go, prefab, id, true));
            }
            finally { UnityEngine.Object.Destroy(holder); }
        }

        // サイト一式の入れ物（照準の模様の中心が x=0、底面が y=BaseY、前後が z=Z。ハンドガンはスライドと一緒に動く）の座標で測る
        // mount: サイトをまだ付けていない複製に付ける処理（手に持った武器はすでに付いているので null）
        static void Report(int id, string what, GameObject go, int mask, Action mount)
        {
            var spot = SightMount.Config[id];
            var c = new Vector3(0, spot.BaseY, spot.Z);
            mount?.Invoke();
            var m = SightMount.FindMount(go.transform);
            if (m == null) { Main.Log($"[center] {what} [{id}] no sight"); return; }
            // サイトの下の銃：サイトの底面の下 2〜30mm の帯（サイト一式はいったん隠す）
            m.gameObject.SetActive(false);
            var gun = Back(m, c, mask);
            var g = Rows(gun, c, -0.030f, -0.002f);
            UnityEngine.Object.Destroy(gun);
            // サイトの 4cm 前（スライド・機関部の平らなところ）も
            var cf = c + new Vector3(0, 0, 0.04f);
            var gunF = Back(m, cf, mask);
            var gf = Rows(gunF, cf, -0.030f, -0.002f);
            UnityEngine.Object.Destroy(gunF);
            m.gameObject.SetActive(true);
            // サイトだけ：銃を消してサイトの帯（底面の上 3〜15mm）
            var off = new List<Renderer>();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
                if (r.enabled && !r.transform.IsChildOf(m)) { r.enabled = false; off.Add(r); }
            var sTex = Back(m, c, mask);
            var s = Rows(sTex, c, 0.003f, 0.015f);
            UnityEngine.Object.Destroy(sTex);
            foreach (var r in off) r.enabled = true;

            float gc = Median(g.Select(t => t.c)), sc = Median(s.Select(t => t.c));
            var sb = new StringBuilder($"[center] {what} [{id}] {Tracker.NameOf(id)} X={spot.X:F4} ");
            sb.Append($"gun={gc * 1000f:F1}mm sightbody={sc * 1000f:F1}mm (reticle=0) | gun rows:");
            foreach (var t in g) sb.Append($" {t.y * 1000f:F0}:[{t.l * 1000f:F1},{t.r * 1000f:F1}]");
            sb.Append($" | front={Median(gf.Select(t => t.c)) * 1000f:F1}mm rows:");
            foreach (var t in gf) sb.Append($" {t.y * 1000f:F0}:[{t.l * 1000f:F1},{t.r * 1000f:F1}]");
            Main.Log(sb.ToString());
        }

        static float Median(IEnumerable<float> v)
        {
            var a = v.Where(x => !float.IsNaN(x)).OrderBy(x => x).ToList();
            return a.Count == 0 ? float.NaN : a[a.Count / 2];
        }

        const float HalfH = 0.05f;

        // 真後ろから（正射影）、center の前後 ±Slab だけを写す。画の右が +X
        static Texture2D Back(Transform root, Vector3 center, int mask)
        {
            var camGo = new GameObject("FPR_LabCam");
            var cam = camGo.AddComponent<Camera>();
            var lightGo = new GameObject("FPR_LabLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            const float dist = 1f;
            camGo.transform.position = root.TransformPoint(center) - root.forward * dist;
            camGo.transform.rotation = Quaternion.LookRotation(root.forward, root.up);
            lightGo.transform.rotation = Quaternion.LookRotation(root.forward - root.up * 0.5f, root.up);
            cam.orthographic = true;
            cam.orthographicSize = HalfH;
            cam.nearClipPlane = dist - Slab; cam.farClipPlane = dist + Slab;
            cam.cullingMask = mask;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = SightLab.Bg;
            int W = SightLab.W, H = SightLab.H;
            var rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt;
            cam.aspect = (float)W / H;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            UnityEngine.Object.Destroy(rt);
            UnityEngine.Object.Destroy(camGo); UnityEngine.Object.Destroy(lightGo);
            return tex;
        }

        // center の高さからの dy の範囲で 2mm ごとに、左右の端と中心（武器の根元の X）
        static List<(float y, float l, float r, float c)> Rows(Texture2D tex, Vector3 center, float dy0, float dy1)
        {
            int W = SightLab.W, H = SightLab.H;
            float px = HalfH * 2f / H;
            var list = new List<(float, float, float, float)>();
            for (float dy = dy0; dy <= dy1 + 1e-5f; dy += 0.002f)
            {
                int py = Mathf.RoundToInt(H / 2f + dy / px);
                if (py < 0 || py >= H) continue;
                var row = tex.GetPixels(0, py, W, 1);
                int l = -1, r = -1;
                for (int x = 0; x < W; x++) if (!SightLab.IsBg(row[x])) { if (l < 0) l = x; r = x; }
                if (l < 0) continue;
                float lx = (l - W / 2f) * px + center.x, rx = (r - W / 2f) * px + center.x;
                list.Add((center.y + dy, lx, rx, (lx + rx) / 2f));
            }
            return list;
        }
    }
}
