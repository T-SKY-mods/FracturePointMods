using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // ホイールの構え（構えてからマウスのホイールのクリック）でのレーザーの見え方（autotest -Dev -Scenario custlaserads）。
    // 全武器で、最初から付いている武器はそのまま、ほかはレーザーを付けて、構え→ホイールの構えで撮り、光線の出口・向き・先の点の画面上の位置を記録する
    public static class LaserAdsLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();
        static string V(Vector3 v) => $"({v.x:F3},{v.y:F3},{v.z:F3})";
        static string S(Vector3 p) => $"({p.x / Screen.width:F3},{p.y / Screen.height:F3})";

        public static void Register()
        {
            TestMode.ExternalScenarios["custlaserads"] = () => Steps();
            TestMode.ExternalScenarios["custlaserqbz"] = () => Steps(new HashSet<int> { 3 });
        }

        static void ResetAll(int id) { foreach (var c in Catalog.CatsFor(id)) Store.SetForTest(id, c, null); }

        public static List<(string, Action)> Steps(HashSet<int> only = null)
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "wheel_*.png")) File.Delete(f); }));
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                var w = wi;
                if (!Catalog.Eligible(w) || (only != null && !only.Contains(w.Id))) continue;
                int id = w.Id;
                var lasers = Catalog.For(id, Cat.SideRail);
                if (lasers.Count == 0) continue;
                var factory = lasers.FirstOrDefault(a => Catalog.IsFactory(a, id));
                var name = Tracker.NameOf(id);
                string cfg = factory != null ? "vanilla" : "added";
                s.Add(($"add {id}", () =>
                {
                    ResetAll(id);
                    if (factory == null) Store.SetForTest(id, Cat.SideRail, lasers[0].Id);
                    if (!Tracker.Held.Any(h => h.Roll.WeaponId == id))
                    {
                        while (Tracker.Held.Count >= 3) Refs.Bag.MoveWeaponToStash(Tracker.ItemById(Tracker.Held.First(h => h.Roll.WeaponId != id).Roll.WeaponId));
                        Tracker.Inv.AddWeapon(Tracker.ItemById(id));
                    }
                    Tracker.ApplyAll();
                }));
                s.Add(("wait", () => { }));
                s.Add(($"equip {id}", () =>
                {
                    var pn = Tracker.ItemById(id).Weapon.gameObject.name;
                    var inst = Refs.Fps.InstantiatedWeapons;
                    for (int i = 0; i < inst.Count; i++)
                        if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == pn) { Refs.Fps.StartWeaponChange(i); return; }
                    Main.Log($"[wheel] {name} not in hands");
                }));
                s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                s.Add(("aim", () => { try { Active()?.OnAimPressed(); } catch (Exception e) { Main.Log("[wheel] aim failed " + e.Message); } }));
                s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                s.Add(($"ads {id}", () => Measure(id, name, cfg, "ads", false)));
                s.Add(("wheel", () => { try { Active()?.OnCycleScope(); } catch (Exception e) { Main.Log("[wheel] cycle failed " + e.Message); } }));
                s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                s.Add(($"wheel {id}", () => Measure(id, name, cfg, "wheel", true)));
                s.Add(($"fire {id}", () => Fire(id, name, cfg)));
                s.Add(("wait", () => { }));
                s.Add(("wheel back", () => { try { Active()?.OnCycleScope(); } catch { } }));
                s.Add(("release", () => { try { Active()?.OnAimReleased(); } catch { } }));
                s.Add(("wait", () => { }));
                s.Add(($"cleanup {id}", () => { ResetAll(id); Tracker.ApplyAll(); Refs.Bag.MoveWeaponToStash(Tracker.ItemById(id)); }));
                s.Add(("wait", () => { }));
            }
            return s;
        }

        static readonly string[] Fingers = { "Left_Hand", "Left_Thumb", "Left_Index", "Left_Middle", "Left_Ring", "Left_Little", "Left_Pinky", "Right_Hand", "Right_Thumb", "Right_Index" };

        static void Measure(int id, string name, string cfg, string tag, bool shot)
        {
            if (shot) ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"wheel_{id:00}_{cfg}.png"));
            var dw = Active(); var cam = Camera.main;
            if (dw == null || cam == null) { Main.Log($"[wheel] [{id}] {tag}: no weapon/camera"); return; }
            var root = dw.transform;
            bool held = dw.gameObject.name.Contains(Tracker.ItemById(id).Weapon.gameObject.name);
            var sg = dw.scopeGroups;
            int si = dw._scopeIndex;
            Transform ap = null;
            if (sg != null && si >= 0 && si < sg.Count && sg[si]?.attachments != null && sg[si].attachments.Count > 0) ap = sg[si].attachments[0]?.aimPoint;
            var camLocal = root.InverseTransformPoint(cam.transform.position);
            float roll; { var u = cam.transform.InverseTransformDirection(root.up); roll = -Mathf.Atan2(u.x, u.y) * Mathf.Rad2Deg; }
            Main.Log($"[wheel] [{id}] {name} {cfg} {tag}: held={held} groups={sg?.Count} index={si} cam(gun)={V(camLocal)} aim(gun)={(ap != null ? V(root.InverseTransformPoint(ap.position)) : "-")} roll={roll:F1}");
            if (tag == "ads") return;
            // "Beater" MPS-5 は元から照準点が無い（ホイールの構えが無い）
            if (sg != null && sg.Count > 1) Check($"{name} {cfg}: wheel aim switches the aim point", si != 0);
            else Main.Log($"[wheel] [{id}] {name}: no wheel aim in the game");
            if (ap != null) Check($"{name} {cfg}: camera on the wheel aim point", (camLocal - root.InverseTransformPoint(ap.position)).magnitude < 0.01f);

            var lines = root.GetComponentsInChildren<LineRenderer>(true).Where(l => l.gameObject.name == "Lazer").ToList();
            // SP-320 の最初のレーザーは形だけ（光線が無い）。付けたレーザーには光線が 1 本
            if (cfg == "added") Check($"{name} {cfg}: one laser beam", lines.Count == 1);
            else Main.Log($"[wheel] [{id}] {name}: {lines.Count} beam(s) in the game");
            foreach (var l in lines)
            {
                bool on = l.enabled && l.gameObject.activeInHierarchy;
                var o = l.transform.position;
                var dir = l.useWorldSpace ? (l.GetPosition(1) - l.GetPosition(0)).normalized : l.transform.TransformDirection((l.GetPosition(1) - l.GetPosition(0)).normalized);
                if (l.useWorldSpace) o = l.GetPosition(0);
                var oc = cam.transform.InverseTransformPoint(o);
                var so = cam.WorldToScreenPoint(o);
                var s1 = cam.WorldToScreenPoint(o + dir * 1f);
                var s10 = cam.WorldToScreenPoint(o + dir * 10f);
                float ang = Vector3.Angle(dir, cam.transform.forward);
                float wpx = l.startWidth / Mathf.Max(oc.z, 0.01f) / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)) * Screen.height;
                // 光線の出口の手前（カメラ側）に指の骨があるか（画面上で近い）
                var cover = new List<string>();
                foreach (var t in Refs.Fps.GetComponentsInChildren<Transform>(true))
                {
                    if (!Fingers.Any(f => t.name.StartsWith(f))) continue;
                    var sp = cam.WorldToScreenPoint(t.position);
                    if (sp.z < so.z && new Vector2(sp.x - so.x, sp.y - so.y).magnitude < Screen.height * 0.04f) cover.Add(t.name);
                }
                Main.Log($"[wheel] [{id}] {cfg}: beam '{SightLab.RelPath(l.transform, root)}' on={on} layer={l.gameObject.layer} mat={l.sharedMaterial?.name} width={l.startWidth:F4} ({wpx:F1}px at exit) exit(cam)={V(oc)} exit={S(so)} 1m={S(s1)} 10m={S(s10)} angle={ang:F2} cover=[{string.Join(",", cover.Distinct())}]");
                Check($"{name} {cfg}: laser beam on", on);
                bool inView = so.z > cam.nearClipPlane && so.x >= 0 && so.x <= Screen.width && so.y >= 0 && so.y <= Screen.height;
                Check($"{name} {cfg}: beam exit on screen", inView);
                Check($"{name} {cfg}: beam points ahead (angle {ang:F1})", ang < 10f);
            }
            // 光線の先の点（LaserPointController：点の向きにレイを飛ばし、当たったところへゲームの LaserPoint を置く）
            foreach (var lpc in root.GetComponentsInChildren<Il2Cpp.LaserPointController>(true))
            {
                var lt = lpc.transform;
                bool hit = Physics.Raycast(lt.position, lt.forward, out var rh, lpc.maxDistance, lpc.hitMask.value);
                var dots = UnityEngine.Object.FindObjectsOfType<Il2Cpp.LaserPoint>(true);
                var dot = dots.Length > 0 ? dots[0].transform : null;
                var tip = hit ? rh.point : lt.position + lt.forward * 30f;
                var st = cam.WorldToScreenPoint(tip);
                // 光線の先（点と、その手前の光線）が銃・腕に隠れているか：自分の体と武器だけを描いて、その画素を見る
                var covered = new List<string>();
                using (var mask = BodyMask(cam))
                {
                    foreach (var f in new[] { 1f, 0.5f, 0.25f })
                    {
                        var p = lt.position + (tip - lt.position) * f;
                        if (mask.Covered(cam.WorldToScreenPoint(p))) covered.Add(f == 1f ? "dot" : $"beam{f * 100:F0}%");
                    }
                }
                Main.Log($"[wheel] [{id}] {cfg}: point '{SightLab.RelPath(lt, root)}' hit={hit} dist={(tip - cam.transform.position).magnitude:F1}m dotScreen={S(st)} dots={dots.Length} dotActive={(dot != null && dot.gameObject.activeInHierarchy)} dotAt={(dot != null ? (dot.position - tip).magnitude.ToString("F3") : "-")} covered=[{string.Join(",", covered)}]");
                // QBZ 191 はレーザーを付けたときだけ目の位置を上げて、照星の先の丸に点が隠れないようにしている（Mount.LiftWheelAim）
                Check($"{name} {cfg}: laser dot not hidden by the gun or hands", !covered.Contains("dot"));
                Check($"{name} {cfg}: end of the beam not hidden by the gun or hands", !covered.Contains("beam50%"));
            }
        }

        // 1 発撃って（弾道の計算だけ。反動・弾数は使わない）、光線の点の距離での弾の位置と、光線とのずれ（cm、画面の右・上）。画面の撮影とは別の手順で
        static void Fire(int id, string name, string cfg)
        {
            var dw = Active(); var cam = Camera.main;
            if (dw == null || cam == null) return;
            var root = dw.transform;
            var bw = dw.GetComponentInChildren<Il2CppBallistics.Weapon>(true);
            foreach (var lpc in root.GetComponentsInChildren<Il2Cpp.LaserPointController>(true))
            {
                var lt = lpc.transform;
                bool hit = Physics.Raycast(lt.position, lt.forward, out var rh, lpc.maxDistance, lpc.hitMask.value);
                var tip = hit ? rh.point : lt.position + lt.forward * 30f;
                var c = cam.transform.position; var fw = cam.transform.forward;
                float tipD = Vector3.Dot(tip - c, fw);
                var sp = bw?.PhysicalBulletSpawnPoint;
                float corr = 0f;
                if (bw != null && bw.BarrelZeroingCorrections != null && bw.currentBarrelZero >= 0 && bw.currentBarrelZero < bw.BarrelZeroingCorrections.Count) corr = bw.BarrelZeroingCorrections[bw.currentBarrelZero];
                Main.Log($"[wheel] [{id}] {cfg}: laser at={V(root.InverseTransformPoint(lt.position))} fwd={V(root.InverseTransformDirection(lt.forward))} spawn at={(sp != null ? V(root.InverseTransformPoint(sp.position)) : "-")} fwd={(sp != null ? V(root.InverseTransformDirection(sp.forward)) : "-")} zeroCorr={corr:F3} angle(laser,spawn)={(sp != null ? Vector3.Angle(lt.forward, sp.forward) : -1f):F2}");
                // 5 発（弾道の計算だけ。反動・弾数は使わない）：点の奥行きでの弾と点のずれ（cm、画面の右・上）
                var offs = new List<Vector2>();
                for (int n = 0; n < 5; n++)
                {
                    try
                    {
                        dw.hitManager.Fire();
                        var bh = Il2CppBallistics.BulletHandler.instance;
                        var arr = bh.Bullets.ToArray();
                        var bd = arr[arr.Length - 1];
                        float g = 9.81f; try { g = bh.g; } catch { }
                        var p = bd.bulletPos; float ys = bd.ySpeed; float dt = 0.0005f;
                        for (int k = 0; k < 400000; k++)
                        {
                            p += bd.bulletDir * bd.Speed * dt; ys -= g * dt; p.y += ys * dt;
                            if (Vector3.Dot(p - c, fw) >= tipD) break;
                        }
                        float tl = (tipD - Vector3.Dot(lt.position - c, fw)) / Vector3.Dot(lt.forward, fw);
                        var off = p - (lt.position + lt.forward * tl);
                        offs.Add(new Vector2(Vector3.Dot(off, cam.transform.right) * 100f, Vector3.Dot(off, cam.transform.up) * 100f));
                        if (n == 0) Main.Log($"[wheel] [{id}] {cfg}: bullet start={V(root.InverseTransformPoint(bd.bulletPos))} dir={V(root.InverseTransformDirection(bd.bulletDir))} angle(laser,bullet)={Vector3.Angle(lt.forward, bd.bulletDir):F2} screen at dot depth={S(cam.WorldToScreenPoint(p))}");
                    }
                    catch (Exception e) { Main.Log($"[wheel] [{id}] fire failed {e.Message}"); break; }
                }
                if (offs.Count == 0) continue;
                var mean = new Vector2(offs.Average(o => o.x), offs.Average(o => o.y));
                Main.Log($"[wheel] [{id}] {cfg}: bullets vs laser dot at {tipD:F1}m (cm right,up): mean=({mean.x:F1},{mean.y:F1}) each=[{string.Join(" ", offs.Select(o => $"({o.x:F1},{o.y:F1})"))}]");
                if (hit) Check($"{name} {cfg}: bullets land on the laser dot (mean {mean.magnitude:F1}cm at {tipD:F1}m)", mean.magnitude < 5f);
            }
        }

        // 自分の体・武器だけを、画面と同じカメラで描いた画（その画素が背景でなければ、体か武器がある）
        sealed class Mask : IDisposable
        {
            public Texture2D Tex; public int W, H;
            public bool Covered(Vector3 sp)
            {
                if (sp.z <= 0f) return true;
                int x = Mathf.RoundToInt(sp.x / Screen.width * W), y = Mathf.RoundToInt(sp.y / Screen.height * H);
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = Mathf.Clamp(x + dx, 0, W - 1), yy = Mathf.Clamp(y + dy, 0, H - 1);
                    var c = Tex.GetPixel(xx, yy);
                    if (!(c.r > 0.9f && c.g < 0.1f && c.b > 0.9f)) return true;
                }
                return false;
            }
            public void Dispose() { if (Tex != null) UnityEngine.Object.Destroy(Tex); }
        }

        static Mask BodyMask(Camera main)
        {
            // 自分の体と武器の描画物（光線は除く）だけを、使われていないレイヤー 31 に一時的に移して描く
            const int L = 31;
            var moved = new List<(GameObject go, int layer)>();
            foreach (var r in Refs.Fps.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly || r.TryCast<LineRenderer>() != null) continue;
                if (((1 << r.gameObject.layer) & main.cullingMask) == 0) continue;
                moved.Add((r.gameObject, r.gameObject.layer)); r.gameObject.layer = L;
            }
            var go = new GameObject("FPC_MaskCam");
            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.cullingMask = 1 << L;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.magenta;
            int w = Screen.width / 2, h = Screen.height / 2;
            var rt = new RenderTexture(w, h, 24);
            cam.targetTexture = rt; cam.aspect = main.aspect;
            cam.Render();
            foreach (var (g, l) in moved) g.layer = l;
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            RenderTexture.active = prev; cam.targetTexture = null;
            UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(go);
            File.WriteAllBytes(Path.Combine(ShotDir, $"wheelmask_{_maskN++:00}.png"), ImageConversion.EncodeToPNG(tex));
            return new Mask { Tex = tex, W = w, H = h };
        }
        static int _maskN;
    }
}
