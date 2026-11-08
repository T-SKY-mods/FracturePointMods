using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using Il2Cpp;
using UnityEngine;

namespace FPCustom
{
    // レーザーの位置を測る（autotest -Dev -Scenario custlaser）。最初から付いている武器での位置・部品の中身を記録し、
    // 他の武器では、前後の位置での銃の右側面（メイン武器）・下面（ハンドガン）に付けて撮る
    public static class LaserLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        public static void Register() => TestMode.ExternalScenarios["custlaser"] = Steps;

        public sealed class Donor { public int WeaponId; public string Path; }
        public static readonly Donor Rifle = new() { WeaponId = 2, Path = "tigg_laser" };
        public static readonly Donor Pistol = new() { WeaponId = 7, Path = "Laser Sight SBAL-PL_Final" };
        public const float PistolInset = 0.0045f;   // 部品の上面をレールの面より上に入れる量（最初から付いている Model 92 の位置に合わせた）

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "laser_*.png")) File.Delete(f); }));
            s.Add(("factory lasers", () =>
            {
                foreach (var p in Factory.All.Where(p => p.Att == "laser" || p.Att == "p_laser"))
                {
                    var prefab = Tracker.ItemById(p.WeaponId)?.Weapon?.gameObject;
                    var t = prefab?.transform.Find(p.Paths[0]);
                    if (t == null) continue;
                    var b = Lab.MeshBounds(t, prefab.transform);
                    Fits.Muzzles.TryGetValue(p.WeaponId, out var m);
                    Main.Log($"[laser] FACTORY [{p.WeaponId}] {Tracker.NameOf(p.WeaponId)} bounds min={V(b.min)} max={V(b.max)} center={V(b.center)} tip={m.Tip:F4} axis={m.Axis:F4}");
                    foreach (var x in t.GetComponentsInChildren<Transform>(true))
                    {
                        var comps = string.Join(",", x.GetComponents<Component>().Select(c => c.GetIl2CppType().Name));
                        var lr = x.GetComponent<LineRenderer>();
                        var extra = lr != null ? $" line world={lr.useWorldSpace} n={lr.positionCount} p0={V(lr.GetPosition(0))} p1={(lr.positionCount > 1 ? V(lr.GetPosition(1)) : "-")} width={lr.startWidth:F4}" : "";
                        Main.Log($"[laser]    '{SightLab.RelPath(x, prefab.transform)}' active={x.gameObject.activeSelf} [{comps}]{extra}");
                    }
                }
            }));
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                var w = wi;
                if (!Catalog.Eligible(w)) continue;
                s.Add(($"laser {w.Id}", () => Shot(w)));
            }
            return s;
        }

        static string V(Vector3 v) => $"({v.x:F4},{v.y:F4},{v.z:F4})";

        static void Shot(WeaponItem wi)
        {
            var prefab = wi.Weapon?.gameObject;
            if (prefab == null) return;
            bool pistol = Catalog.IsPistol(wi.Id);
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                Lab.HideNonMesh(go);
                foreach (var fp in Factory.On(wi.Id)) Factory.Show(go.transform, fp, false);
                var fit = Place(go.transform, wi.Id, pistol, out string why);
                Main.Log($"[laser] FIT [{wi.Id}] {wi.GetName()} {why}");
                if (fit != null)
                {
                    var g = Attach(go.transform, pistol ? Pistol : Rifle, fit.Value);
                    var c = new Vector3(0f, fit.Value.y, fit.Value.z);
                    var a = SightLab.RenderSide(go, 0.09f, c); Save(a.Tex, $"laser_{wi.Id:00}_side"); a.Dispose();
                    var b = Slice(go, new Vector3(0f, fit.Value.y, fit.Value.z), 0.06f, fit.Value.z - 0.06f, fit.Value.z + 0.06f); Save(b, $"laser_{wi.Id:00}_back"); UnityEngine.Object.Destroy(b);
                }
            }
            catch (Exception e) { Main.Log($"[laser] [{wi.Id}] failed: {e}"); }
            UnityEngine.Object.Destroy(holder);
        }

        // 置き場所（部品の中心。武器の根元の座標）
        //   メイン武器：グリップ枠の 5cm 前（無ければ銃口から 25cm 後ろ）で、銃身の中心の高さ〜4cm 上の範囲の銃の右側面の外
        //   ハンドガン：銃口から 4cm 後ろで、銃身の中心から下へたどった銃の下面の下
        public static Vector3? Place(Transform root, int id, bool pistol, out string why)
        {
            if (!Fits.Muzzles.TryGetValue(id, out var m)) { why = "no muzzle fit"; return null; }
            var size = DonorSize(pistol ? Pistol : Rifle);
            if (pistol)
            {
                // 銃口の少し後ろ（1〜3.5cm）でのフレームの下面（レールの面）。そこから後ろへ見て、下面が 6mm 以上下がる所＝用心金の前
                var side = SightLab.RenderSide(root.gameObject, 0.10f, new Vector3(0f, m.Axis - 0.03f, m.Tip - 0.07f));
                float yd = float.MaxValue;
                for (float zz = m.Tip - 0.035f; zz <= m.Tip - 0.010f; zz += 0.001f) yd = Mathf.Min(yd, Bottom(side, zz, m.Axis));
                if (yd == float.MaxValue) { side.Dispose(); why = "no frame bottom"; return null; }
                float zg = m.Tip - 0.13f;
                for (float zz = m.Tip - 0.035f; zz >= m.Tip - 0.13f; zz -= 0.001f)
                {
                    float b = Bottom(side, zz, m.Axis);
                    if (b != float.MaxValue && b < yd - 0.006f) { zg = zz; break; }
                }
                side.Dispose();
                // 後ろの端は用心金の 3mm 前、または前の端が銃口の 7mm 後ろになる位置の、後ろでない方
                float rear = Mathf.Max(zg + 0.003f, m.Tip - 0.007f - size.z);
                why = $"pistol railY={yd:F4} guardZ={zg:F4} rear={rear:F4} front={rear + size.z:F4} tip={m.Tip:F4}";
                return new Vector3(0f, yd + PistolInset - size.y / 2f, rear + size.z / 2f);
            }
            float zc = (Grips.SlotZ(id) is float sz ? sz + 0.05f : m.Tip - 0.25f);
            var sl = Slice(root.gameObject, new Vector3(0f, m.Axis + 0.02f, zc), 0.08f, zc - size.z / 2f, zc + size.z / 2f);
            // 右側面：高さ axis〜axis+4cm の各行で一番右の描画
            float right = float.MinValue, atY = m.Axis;
            for (float y = m.Axis; y <= m.Axis + 0.04f; y += 0.002f)
            {
                int py = Mathf.RoundToInt(SightLab.H / 2f + (y - (m.Axis + 0.02f)) / 0.08f * (SightLab.H / 2f));
                if (py < 0 || py >= SightLab.H) continue;
                var row = sl.GetPixels(0, py, SightLab.W, 1);
                for (int px = SightLab.W - 1; px >= SightLab.W / 2; px--)
                    if (!SightLab.IsBg(row[px])) { float x = (px - SightLab.W / 2f) / (SightLab.W / 2f) * 0.16f; if (x > right) { right = x; atY = y; } break; }
            }
            UnityEngine.Object.Destroy(sl);
            if (right == float.MinValue) { why = "no side surface"; return null; }
            float yc = Mathf.Clamp(atY, m.Axis, m.Axis + 0.03f);
            why = $"rifle z={zc:F4} rightX={right:F4} y={yc:F4}";
            return new Vector3(right + size.x / 2f, yc, zc);
        }

        static float Bottom(SightLab.Shot s, float z, float axis)
        {
            int px = s.PxOfZ(z);
            if (px < 0 || px >= SightLab.W) return float.MaxValue;
            var c = s.Tex.GetPixels(px, 0, 1, SightLab.H);
            int py = Mathf.Clamp(s.PyOfY(axis), 0, SightLab.H - 1);
            if (SightLab.IsBg(c[py])) return float.MaxValue;
            while (py > 0 && !SightLab.IsBg(c[py - 1])) py--;
            return s.YAt(py);
        }

        public static Vector3 DonorSize(Donor d)
        {
            var dp = Tracker.ItemById(d.WeaponId)?.Weapon?.gameObject;
            var t = dp?.transform.Find(d.Path);
            return t == null ? Vector3.zero : Lab.MeshBounds(t, dp.transform).size;
        }

        // 部品を複製して、描画の範囲の中心が at に来るように置く（向き・大きさは複製元の武器に対してと同じ）
        //   keepScripts：ゲームで使うとき（光線の先の点 LaserPointController を動かす）。撮影用はスクリプトを外す
        public static GameObject Attach(Transform root, Donor d, Vector3 at, bool keepScripts = false)
        {
            var dp = Tracker.ItemById(d.WeaponId)?.Weapon?.gameObject;
            var src = dp?.transform.Find(d.Path);
            if (src == null) return null;
            var b = Lab.MeshBounds(src, dp.transform);
            var go = UnityEngine.Object.Instantiate(src.gameObject, root, false);
            if (!keepScripts) foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
            go.name = Mount.Prefix + "laser";
            go.SetActive(true);
            var dr = dp.transform;
            go.transform.localRotation = Quaternion.Inverse(dr.rotation) * src.rotation;
            var rs = dr.lossyScale; var ts = src.lossyScale;
            go.transform.localScale = new Vector3(ts.x / rs.x, ts.y / rs.y, ts.z / rs.z);
            go.transform.localPosition = dr.InverseTransformPoint(src.position) + (at - b.center);
            return go;
        }

        // 光線（LineRenderer "Lazer"）と先の点（LaserPointController）の向きを、弾の通り道（弾の発射点の前方向）と ZeroDist 先で交わるように。
        // 弾は発射点の前方向に飛ぶ（FPSFireManager.Fire → Ballistics.Weapon.ShootBullet）が、発射点は武器ごとに 0.5〜1.3 度傾いていて、
        // 平行な光線のままだと 15m 先で最大 30cm ずれる。発射点は構えの途中の骨の動きに左右されないよう、プレハブの親の並びで計算する
        // （サイトを載せたときに Optics.AlignBullet が動かした位置・向きはそのまま使う）
        public const float ZeroDist = 15f;
        public static void AimAtBullets(GameObject inst, GameObject prefab)
        {
            var root = inst.transform;
            var lasers = root.GetComponentsInChildren<Il2Cpp.LaserPointController>(true);
            if (lasers.Length == 0) return;
            var sp = inst.GetComponentInChildren<Il2CppBallistics.Weapon>(true)?.PhysicalBulletSpawnPoint;
            if (sp == null || !sp.IsChildOf(root)) return;
            var psp = prefab.transform.Find(SightLab.RelPath(sp, root));
            var parent = psp != null ? psp.parent : null;
            Vector3 pos, dir;
            if (parent != null)
            {
                pos = prefab.transform.InverseTransformPoint(parent.TransformPoint(sp.localPosition));
                dir = prefab.transform.InverseTransformDirection(parent.rotation * sp.localRotation * Vector3.forward);
            }
            else { pos = root.InverseTransformPoint(sp.position); dir = root.InverseTransformDirection(sp.forward); }
            var target = pos + dir.normalized * ZeroDist;
            foreach (var lpc in lasers)
            {
                var holder = lpc.transform.parent;
                if (holder == null) continue;
                foreach (var t in holder.GetComponentsInChildren<Transform>(true))
                {
                    if (t != lpc.transform && t.GetComponent<LineRenderer>() == null) continue;
                    var o = root.InverseTransformPoint(t.position);
                    t.rotation = root.rotation * Quaternion.LookRotation((target - o).normalized, Vector3.up);
                }
            }
        }

        // ---------- 光線の色（色違いのレーザー） ----------
        // ゲームの赤の光線（LineRenderer "Lazer"）と先の点（LaserPoint）の色を、明るさ・濃さはそのままで色相だけ替える。
        // null ならゲームの色に戻す。替えた材質は元の材質ごとに 1 つだけ作って使い回す
        static readonly string[] ColorProps = { "_BaseColor", "_Color", "_EmissionColor", "_TintColor", "_MainColor" };
        static readonly Dictionary<(IntPtr, int), Material> _tinted = new();
        static readonly Dictionary<IntPtr, Material> _original = new();                  // 替えた材質 → 元の材質
        static readonly Dictionary<IntPtr, (Color, Color)> _lineColors = new();         // 光線の元の色（頂点の色）
        static readonly Dictionary<IntPtr, Color> _lightColors = new();                 // 先の点の元の色（点は赤いライト）

        static Color Hue(Color c, Color to)
        {
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (max <= 0f) return c;
            Color.RGBToHSV(new Color(c.r / max, c.g / max, c.b / max), out _, out float s, out float v);
            Color.RGBToHSV(to, out float h, out float ts, out _);
            var r = Color.HSVToRGB(h, Mathf.Min(s, ts), v) * max;
            r.a = c.a;
            return r;
        }

        static Material Original(Material m) => m != null && _original.TryGetValue(m.Pointer, out var o) && o != null ? o : m;

        static Material TintedOf(Material orig, Color to)
        {
            if (orig == null) return null;
            var key = (orig.Pointer, to.GetHashCode());
            if (_tinted.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(orig) { name = orig.name + "_FPC" };
            foreach (var p in ColorProps) if (m.HasProperty(p)) m.SetColor(p, Hue(m.GetColor(p), to));
            UnityEngine.Object.DontDestroyOnLoad(m);
            _tinted[key] = m;
            _original[m.Pointer] = orig;
            return m;
        }

        static void TintRenderer(Renderer r, Color? to)
        {
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                var orig = Original(mats[i]);
                var want = to is Color c ? TintedOf(orig, c) : orig;
                if (want != mats[i]) { mats[i] = want; changed = true; }
            }
            if (changed) r.sharedMaterials = mats;
            if (r.TryCast<LineRenderer>() is { } lr)
            {
                if (!_lineColors.TryGetValue(lr.Pointer, out var lc)) _lineColors[lr.Pointer] = lc = (lr.startColor, lr.endColor);
                lr.startColor = to is Color c1 ? Hue(lc.Item1, c1) : lc.Item1;
                lr.endColor = to is Color c2 ? Hue(lc.Item2, c2) : lc.Item2;
            }
        }

        public static void Tint(GameObject inst, Color? to)
        {
            if (inst == null) return;
            foreach (var lr in inst.GetComponentsInChildren<LineRenderer>(true))
                if (lr.gameObject.name == "Lazer") TintRenderer(lr, to);
        }

        // 先の点（LaserPoint）は場面に 1 つで、どの武器のレーザーも同じものを使う → 手に持っている武器のレーザーの色にする
        static int _dotId = -2, _dotVersion = -1;
        static float _dotNext, _dotWait;
        static string _dotScene;
        static readonly List<LaserPoint> _dots = new();
        public static void TickDot(int activeId)
        {
            // 全体を探すのは重い（シーンが大きいと 1 回 30ms ほど）。武器・付け替え・シーンが変わったときと、点がまだ見つからないときだけ探す
            bool same = activeId == _dotId && Store.Version == _dotVersion && _dotScene == Core.Scene;
            if (same)
            {
                if (_dots.Count > 0 && _dots.TrueForAll(d => d != null)) return;
                if (Time.unscaledTime < _dotNext) return;
                _dotWait = System.Math.Min(30f, _dotWait * 2f);
            }
            else _dotWait = 1f;
            _dotId = activeId; _dotVersion = Store.Version; _dotScene = Core.Scene; _dotNext = Time.unscaledTime + _dotWait;
            var beam = activeId >= 0 ? Store.Equipped(activeId, Cat.SideRail)?.Beam : null;
            _dots.Clear();
            foreach (var d in UnityEngine.Object.FindObjectsOfType<LaserPoint>(true)) _dots.Add(d);
            foreach (var dot in _dots)
            {
                foreach (var r in dot.GetComponentsInChildren<Renderer>(true)) TintRenderer(r, beam);
                // ランの中の点は Renderer ではなく赤い Light（壁を照らして点に見える）
                foreach (var l in dot.GetComponentsInChildren<Light>(true))
                {
                    if (!_lightColors.TryGetValue(l.Pointer, out var lc)) _lightColors[l.Pointer] = lc = l.color;
                    l.color = beam is Color c ? Hue(lc, c) : lc;
                }
            }
        }

        // テスト用：光線・点の今の色（材質の色と頂点の色）
        public static string Describe(Renderer r)
        {
            var parts = new List<string>();
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                var cs = ColorProps.Where(p => m.HasProperty(p)).Select(p => $"{p}={m.GetColor(p)}");
                parts.Add($"{m.name}[{m.shader?.name}] tex={m.mainTexture?.name ?? "-"} {string.Join(" ", cs)}");
            }
            if (r.TryCast<LineRenderer>() is { } lr) parts.Add($"line {lr.startColor}->{lr.endColor}");
            return string.Join(" | ", parts);
        }

        public static Color? MainColor(Renderer r)
        {
            var m = r?.sharedMaterial;
            if (m == null) return null;
            foreach (var p in ColorProps) if (m.HasProperty(p)) return m.GetColor(p);
            return null;
        }

        // 後ろから前を見た正射影で、前後 z0〜z1 の範囲だけを撮る（画の右が +X）
        public static Texture2D Slice(GameObject go, Vector3 center, float halfH, float z0, float z1)
        {
            var root = go.transform;
            var camGo = new GameObject("FPC_LabCam");
            var cam = camGo.AddComponent<Camera>();
            var lightGo = new GameObject("FPC_LabLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 2f;
            var camLocal = new Vector3(center.x, center.y, z0 - 1f);
            camGo.transform.position = root.TransformPoint(camLocal);
            camGo.transform.rotation = Quaternion.LookRotation(root.forward, root.up);
            lightGo.transform.rotation = Quaternion.LookRotation(root.forward - root.up * 0.5f + root.right * 0.3f, root.up);
            cam.orthographic = true; cam.orthographicSize = halfH;
            cam.nearClipPlane = 1f; cam.farClipPlane = 1f + (z1 - z0);
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

        static void Save(Texture2D tex, string name) => File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), ImageConversion.EncodeToPNG(tex));
    }
}
