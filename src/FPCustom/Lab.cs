using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using Il2Cpp;
using UnityEngine;

namespace FPCustom
{
    // 調査・見た目の確認用（autotest -Dev -Scenario custmuzzle）。武器をスクリプト抜きで地下に複製し、部品を付けて横から撮る
    public static class Lab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        public static void Register()
        {
            TestMode.ExternalScenarios["custmuzzle"] = MuzzleSteps;
            TestMode.ExternalScenarios["custbore"] = BoreSteps;
            TestMode.ExternalScenarios["custfactory"] = FactorySteps;
            TestMode.ExternalScenarios["custgrip"] = GripSteps;
            TestMode.ExternalScenarios["custsight"] = SightSteps;
        }

        // 最初から付いている部品を、付けたまま・外した状態で横から撮る
        static List<(string, Action)> FactorySteps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "fac_*.png")) File.Delete(f); }));
            foreach (var part in Factory.All)
            {
                var p = part;
                s.Add(($"factory {p.WeaponId} {p.Att}", () =>
                {
                    var prefab = Tracker.ItemById(p.WeaponId)?.Weapon?.gameObject;
                    if (prefab == null) return;
                    var go = SightLab.CloneStatic(prefab, out var holder);
                    holder.SetActive(true);
                    try
                    {
                        HideNonMesh(go);
                        var rs = Factory.Renderers(go.transform, p);
                        var b = new Bounds(); bool first = true;
                        foreach (var r in rs) { var lb = SightLab.LocalBounds(r, go.transform); if (first) { b = lb; first = false; } else b.Encapsulate(lb); }
                        Main.Log($"[lab] factory [{p.WeaponId}] {p.Att}: {rs.Count} renderers ({string.Join(",", rs.Select(r => r.name))}) bounds min={b.min:F3} max={b.max:F3}");
                        var all = SightLab.VisibleBounds(go);
                        float half = Mathf.Max(all.extents.z / 2f, all.extents.y) * 1.1f;
                        var a = SightLab.RenderSide(go, half, all.center); Save(a.Tex, $"fac_{p.WeaponId:00}_{p.Att}_on"); a.Dispose();
                        Factory.Show(go.transform, p, false);
                        var o = SightLab.RenderSide(go, half, all.center); Save(o.Tex, $"fac_{p.WeaponId:00}_{p.Att}_off"); o.Dispose();
                        // 部品のまわりを拡大
                        if (!first)
                        {
                            float h2 = Mathf.Max(b.extents.z / 2f, b.extents.y) * 2.2f + 0.02f;
                            Factory.Show(go.transform, p, true);
                            var z1 = SightLab.RenderSide(go, h2, b.center); Save(z1.Tex, $"fac_{p.WeaponId:00}_{p.Att}_zon"); z1.Dispose();
                            Factory.Show(go.transform, p, false);
                            var z0 = SightLab.RenderSide(go, h2, b.center); Save(z0.Tex, $"fac_{p.WeaponId:00}_{p.Att}_zoff"); z0.Dispose();
                        }
                    }
                    catch (Exception e) { Main.Log($"[lab] factory [{p.WeaponId}] failed: {e}"); }
                    UnityEngine.Object.Destroy(holder);
                }));
            }
            return s;
        }

        // バーティカルグリップを各武器のグリップ枠に置いて横から撮る（浮き・めり込みの確認）
        static List<(string, Action)> GripSteps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "grip_*.png")) File.Delete(f); }));
            var ws = Tracker.Inv.WeaponData.Weapons;
            for (int i = 0; i < ws.Count; i++)
            {
                var wi = ws[i];
                if (!Catalog.Eligible(wi) || Catalog.IsPistol(wi.Id)) continue;
                s.Add(($"grip {wi.Id}", () => GripShot(wi)));
            }
            return s;
        }

        static void GripShot(WeaponItem wi)
        {
            var prefab = wi.Weapon?.gameObject;
            var pdw = prefab?.GetComponent<Il2CppDemo.Scripts.Runtime.Item.Weapon>();
            var vg = Catalog.Get("vertical_grip");
            int idx = Grips.SlotIndex(pdw, vg);
            if (idx < 0) { Main.Log($"[lab] grip [{wi.Id}] {wi.GetName()}: no vertical slot"); return; }
            var path = SightLab.RelPath(pdw.gripAttachments.attachments[idx].transform, prefab.transform);
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                HideNonMesh(go);
                foreach (var fp in Factory.On(wi.Id)) Factory.Show(go.transform, fp, false);
                var slot = go.transform.Find(path);
                slot.gameObject.SetActive(true);
                foreach (var r in slot.GetComponentsInChildren<Renderer>(true)) r.enabled = false;   // 枠の中の使われていない模型
                var c = slot.position;
                var center = go.transform.InverseTransformPoint(c) + new Vector3(0f, -0.03f, 0f);
                var a0 = SightLab.RenderSide(go, 0.12f, center);   // 測る用（方眼なし）
                var a1 = SightLab.RenderSide(go, 0.12f, center); SaveGrid(a1, $"grip_{wi.Id:00}_bare"); a1.Dispose();
                bool factory = Catalog.IsFactory(vg, wi.Id);
                var sp = go.transform.InverseTransformPoint(c);
                // 枠の近く（下側）にある描画物：銃の模型に作り込まれたグリップを探す
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled || !IsShown(r.transform, go.transform) || r.transform.IsChildOf(slot)) continue;
                    var lb = SightLab.LocalBounds(r, go.transform);
                    if (Mathf.Abs(lb.center.z - sp.z) < 0.12f && lb.center.y < sp.y - 0.01f && lb.size.y > 0.03f && lb.size.z < 0.08f)
                        Main.Log($"[lab] grip [{wi.Id}] near: '{SightLab.RelPath(r.transform, go.transform)}' center={lb.center:F3} size={lb.size:F3}");
                }
                if (factory) foreach (var fp in Factory.On(wi.Id)) if (fp.Att == vg.Id) Factory.Show(go.transform, fp, true);
                var g = factory ? null : Grips.Place(slot, go.transform, wi.Id);
                Vector3 fix = Vector3.zero;
                if (g != null)
                {
                    var gb = MeshBounds(g.transform, go.transform);
                    fix = GripFix(a0, gb, Fits.Muzzles.TryGetValue(wi.Id, out var mf) ? mf.Axis : 0f);
                    g.transform.position = go.transform.TransformPoint(go.transform.InverseTransformPoint(g.transform.position) + fix);
                    Main.Log($"[lab] GRIPFIT [{wi.Id}] {wi.GetName()} top={gb.max.y:F4} z={gb.min.z:F4}..{gb.max.z:F4} fix={fix.z:F4}");
                }
                a0.Dispose();
                var b = SightLab.RenderSide(go, 0.12f, center); SaveGrid(b, $"grip_{wi.Id:00}_{(factory ? "factory" : "vgrip")}"); b.Dispose();
                Main.Log($"[lab] grip [{wi.Id}] {wi.GetName()}: slot '{path}' at {sp:F4} placed={(g != null)}");
            }
            catch (Exception e) { Main.Log($"[lab] grip [{wi.Id}] failed: {e}"); }
            UnityEngine.Object.Destroy(holder);
        }

        // サイトを外したときの照準：照準点（ゲームの値）と、サイトを外した銃の上面の高さ（1cm ごと）を測る。
        // 比較用に、最初からアイアンサイトの武器も測る（照準点が前の照星の先端からどれだけ下か）
        static List<(string, Action)> SightSteps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "sight_*.png")) File.Delete(f); }));
            var ids = Factory.All.Where(p => Catalog.Get(p.Att)?.Cat == Cat.Sight && !Catalog.IsPistol(p.WeaponId)).Select(p => p.WeaponId).Distinct().ToList();
            foreach (var r in new[] { 0, 3, 4, 10, 11, 17, 19, 20, 21, 27 }) if (!ids.Contains(r)) ids.Add(r);
            foreach (var id in ids) { int w = id; s.Add(($"sight {w}", () => SightShot(Tracker.ItemById(w)))); }
            return s;
        }

        static void SightShot(WeaponItem wi)
        {
            var prefab = wi?.Weapon?.gameObject;
            var pdw = prefab?.GetComponent<Il2CppDemo.Scripts.Runtime.Item.Weapon>();
            if (pdw == null) return;
            var sb = new System.Text.StringBuilder($"[lab] SIGHT [{wi.Id}] {wi.GetName()}");
            var aims = new List<Vector3>();
            var sg = pdw.scopeGroups;
            for (int g = 0; sg != null && g < sg.Count; g++)
                for (int k = 0; sg[g]?.attachments != null && k < sg[g].attachments.Count; k++)
                {
                    var ap = sg[g].attachments[k]?.aimPoint;
                    if (ap == null) continue;
                    var lp = prefab.transform.InverseTransformPoint(ap.position);
                    var lr = (Quaternion.Inverse(prefab.transform.rotation) * ap.rotation).eulerAngles;
                    aims.Add(lp);
                    sb.Append($" aim{g}.{k}=({lp.x:F4},{lp.y:F4},{lp.z:F4}) rot=({lr.x:F2},{lr.y:F2},{lr.z:F2})");
                }
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                HideNonMesh(go);
                foreach (var fp in Factory.On(wi.Id)) if (Catalog.Get(fp.Att)?.Cat == Cat.Sight) Factory.Show(go.transform, fp, false);
                float tip = Fits.Muzzles.TryGetValue(wi.Id, out var mf) ? mf.Tip : 0.5f;
                float axis = mf.Axis;
                float ay = aims.Count > 0 ? aims[0].y : 0.06f;
                var center = new Vector3(0f, ay, tip / 2f);
                var shot = SightLab.RenderSide(go, Mathf.Max(0.12f, tip / 4f + 0.02f), center);
                sb.Append(" top(z cm:y mm)=");
                for (float z = -0.05f; z <= tip; z += 0.01f)
                {
                    float t = TopAt(shot, z, ay + 0.05f, axis);
                    if (!float.IsNaN(t)) sb.Append($"{z * 100:F0}:{t * 1000:F0} ");
                }
                // 前の照星：銃口から銃の長さの 45% までの範囲で一番高い所
                float front = float.MinValue, frontZ = 0f;
                for (float z = tip * 0.55f; z <= tip; z += 0.002f) { float t = TopAt(shot, z, ay + 0.05f, axis); if (!float.IsNaN(t) && t > front) { front = t; frontZ = z; } }
                sb.Append($" FRONT y={front:F4} z={frontZ:F3} aim-front={(aims.Count > 0 ? aims[0].y - front : 0f):F4}");
                Main.Log(sb.ToString());
                // 照準点の高さに赤い線
                foreach (var a in aims) { int py = shot.PyOfY(a.y); if (py >= 0 && py < SightLab.H) for (int x = 0; x < SightLab.W; x += 2) shot.Tex.SetPixel(x, py, Color.red); }
                SaveGrid(shot, $"sight_{wi.Id:00}_off");
                shot.Dispose();
            }
            catch (Exception e) { Main.Log($"[lab] sight [{wi.Id}] failed: {e}"); }
            UnityEngine.Object.Destroy(holder);
        }

        // 列 z で、上（from）から下へ見て最初に銃の描画がある高さ。銃身の中心まで無ければ NaN
        static float TopAt(SightLab.Shot s, float z, float from, float axis)
        {
            int px = s.PxOfZ(z);
            if (px < 0 || px >= SightLab.W) return float.NaN;
            var c = s.Tex.GetPixels(px, 0, 1, SightLab.H);
            int py = Mathf.Clamp(s.PyOfY(from), 0, SightLab.H - 1), stop = Mathf.Max(0, s.PyOfY(axis));
            for (; py > stop; py--) if (!SightLab.IsBg(c[py])) return s.YAt(py);
            return float.NaN;
        }

        public static bool IsShown(Transform t, Transform root)
        {
            for (; t != null; t = t.parent) { if (!t.gameObject.activeSelf) return false; if (t == root) break; }
            return true;
        }

        // グリップの前が銃からはみ出していれば後ろへ下げる。
        // 列ごとに、銃身の中心から下へ見て、銃の描画が終わる高さ＝下面。下面がグリップの上面の近くにある列が「支えがある」列
        static Vector3 GripFix(SightLab.Shot s, Bounds gb, float axis)
        {
            float lowest = float.MaxValue, frontSupported = float.MinValue;
            for (float z = gb.min.z; z <= gb.max.z; z += 0.002f)
            {
                float bottom = BottomAt(s, z, axis, gb.max.y + 0.08f);
                // グリップの上面より 6mm 以上 上で切れる列（銃身だけの所）や 3cm 以上 下まで続く列（マガジン等）は支えにならない
                if (float.IsNaN(bottom) || bottom > gb.max.y + 0.006f || bottom < gb.max.y - 0.015f) continue;
                lowest = Mathf.Min(lowest, bottom);
                frontSupported = Mathf.Max(frontSupported, z);
            }
            if (lowest == float.MaxValue) return Vector3.zero;
            float dz = frontSupported < gb.max.z - 0.004f ? frontSupported - gb.max.z : 0f;
            return new Vector3(0f, 0f, dz);   // 高さはゲームの枠の位置のまま（上面がちょうど銃の下面に来ている）
        }

        // 列 z で、上から（銃身の中心の高さから）下へたどって、銃の描画が切れる高さ。limit より 下 に 8cm 以上ないなら NaN
        static float BottomAt(SightLab.Shot s, float z, float axis, float top)
        {
            int px = s.PxOfZ(z);
            if (px < 0 || px >= SightLab.W) return float.NaN;
            var c = s.Tex.GetPixels(px, 0, 1, SightLab.H);
            int py = Mathf.Clamp(s.PyOfY(Mathf.Min(axis, top)), 0, SightLab.H - 1);
            int stop = Mathf.Max(0, s.PyOfY(axis - 0.08f));
            while (py > stop && SightLab.IsBg(c[py])) py--;       // 銃身と下の部品の間のすき間
            if (py <= stop) return float.NaN;
            while (py > 0 && !SightLab.IsBg(c[py - 1])) py--;
            return s.YAt(py);
        }

        static void Save(Texture2D tex, string name) => File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), ImageConversion.EncodeToPNG(tex));

        // ゲームで実際に使われているマズルの部品（複製元の武器・部品のパス）
        public sealed class Donor { public int WeaponId; public string Path; public string Label; }
        public static readonly Donor AkBrake = new() { WeaponId = 2, Path = "AKS-74N/ak12/MuzzleBrake", Label = "akbrake" };
        public static readonly Donor ArBrake = new() { WeaponId = 21, Path = "ar15 long/Reciever/Barrel/Muzzle_Brake", Label = "arbrake" };
        public static readonly Donor PistolSup = new() { WeaponId = 23, Path = "W_Mk23Mod0_Skeleton/Mk23Mod0Rig/Root/Barrel/silencer 9", Label = "psup" };

        static List<(string, Action)> MuzzleSteps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "muz_*.png")) File.Delete(f); }));
            foreach (var d in new[] { AkBrake, ArBrake, PistolSup }) s.Add(($"donor {d.Label}", () => DescribeDonor(d)));
            var ws = Tracker.Inv.WeaponData.Weapons;
            for (int i = 0; i < ws.Count; i++)
            {
                var wi = ws[i];
                if (!Catalog.Eligible(wi)) continue;
                var donor = wi.Type == WeaponItem.SlotType.Small ? PistolSup : AkBrake;
                s.Add(($"muzzle {wi.Id}", () => MuzzleShot(wi, donor)));
            }
            return s;
        }

        // ハンドガンの銃口の穴の高さ：正面から撮り、今の高さ（赤）・ゲームの発射位置（緑：見た目、青：弾）の線を引く。横からはコンペンセイターを付けて撮る
        static List<(string, Action)> BoreSteps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "bore_*.png")) File.Delete(f); }));
            var ws = Tracker.Inv.WeaponData.Weapons;
            for (int i = 0; i < ws.Count; i++)
            {
                var wi = ws[i];
                if (!Catalog.Eligible(wi) || wi.Type != WeaponItem.SlotType.Small) continue;
                s.Add(($"bore {wi.Id}", () => BoreShot(wi)));
            }
            return s;
        }

        static void BoreShot(WeaponItem wi)
        {
            var prefab = wi.Weapon?.gameObject;
            if (prefab == null || !Fits.Muzzles.TryGetValue(wi.Id, out var f)) return;
            var (vis, phys) = SpawnPoints(prefab);
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                HideNonMesh(go);
                foreach (var fp in Factory.On(wi.Id)) Factory.Show(go.transform, fp, false);
                var front = SightLab.RenderFront(go, 0.03f, new Vector3(0f, f.Axis, f.Tip));
                void Line(SightLab.Shot sh, float y, Color c) { int py = sh.PyOfY(y); if (py >= 0 && py < SightLab.H) for (int x = 0; x < SightLab.W; x += 2) sh.Tex.SetPixel(x, py, c); }
                // 中央の縦の列の明るさ（穴は暗い）。1mm ごと
                var sb = new System.Text.StringBuilder();
                for (float y = f.Axis - 0.02f; y <= f.Axis + 0.0201f; y += 0.001f)
                {
                    var c = front.Tex.GetPixel(SightLab.W / 2, front.PyOfY(y));
                    sb.Append(SightLab.IsBg(c) ? "_ " : $"{c.grayscale:F2} ");
                }
                Line(front, f.Axis, Color.red); Line(front, vis.y, Color.green); Line(front, phys.y, Color.blue);
                front.Tex.Apply();
                Save(front.Tex, $"bore_{wi.Id:00}_front");
                front.Dispose();
                Main.Log($"[lab] BORE [{wi.Id}] {wi.GetName()} axis={f.Axis:F4} tip={f.Tip:F4} visual={vis:F4} physical={phys:F4} gray(y {f.Axis - 0.02f:F4}..+1mm)={sb}");
                var pm = AssetParts.MuzzleModel(wi.Id, Catalog.Get("p_compensator"));
                if (pm != null && AssetParts.AttachRear(go.transform, pm, new Vector3(0f, f.Axis, f.Tip - f.Back), "p_compensator") != null)
                {
                    var side = SightLab.RenderSide(go, 0.04f, new Vector3(0f, f.Axis, f.Tip));
                    Line(side, f.Axis, Color.red); Line(side, vis.y, Color.green);
                    side.Tex.Apply();
                    Save(side.Tex, $"bore_{wi.Id:00}_side");
                    side.Dispose();
                }
            }
            catch (Exception e) { Main.Log($"[lab] [{wi.Id}] bore failed: {e}"); }
            UnityEngine.Object.Destroy(holder);
        }

        static void DescribeDonor(Donor d)
        {
            var prefab = Tracker.ItemById(d.WeaponId)?.Weapon?.gameObject;
            var t = prefab?.transform.Find(d.Path);
            if (t == null) { Main.Log($"[lab] donor {d.Label} not found"); return; }
            var b = MeshBounds(t, prefab.transform);
            var (vis, phys) = SpawnPoints(prefab);
            Main.Log($"[lab] donor {d.Label}: bounds min={b.min:F4} max={b.max:F4} visual={vis:F4} physical={phys:F4}");
        }

        public static (Vector3 vis, Vector3 phys) SpawnPoints(GameObject prefab)
        {
            var bw = prefab.GetComponentInChildren<Il2CppBallistics.Weapon>(true);
            var r = prefab.transform;
            return (bw?.VisualSpawnPoint != null ? r.InverseTransformPoint(bw.VisualSpawnPoint.position) : Vector3.zero,
                    bw?.PhysicalBulletSpawnPoint != null ? r.InverseTransformPoint(bw.PhysicalBulletSpawnPoint.position) : Vector3.zero);
        }

        static void MuzzleShot(WeaponItem wi, Donor donor)
        {
            var prefab = wi.Weapon?.gameObject;
            if (prefab == null) return;
            var (vis, phys) = SpawnPoints(prefab);
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                HideNonMesh(go);
                foreach (var fp in Factory.On(wi.Id)) Factory.Show(go.transform, fp, false);   // 最初から付いている部品は外して測る
                var m = FindMuzzle(go, vis);
                var center = new Vector3(0f, m.y, m.z);
                var shot = SightLab.RenderSide(go, 0.08f, center);
                float cover = HiderLength(shot, m, out float hr, out string prof);
                SaveGrid(shot, $"muz_{wi.Id:00}_bare");
                shot.Dispose();
                float db = DonorRadius(donor);
                float back = cover > 0f && db >= hr * 0.95f ? cover : 0f;
                Main.Log($"[lab] FIT [{wi.Id}] {wi.GetName()} tip={m.z:F4} axis={m.y:F4} hiderR={hr:F4} hider={cover:F4} donorR={db:F4} back={back:F4} prof(mm per 5mm)={prof}");   // 銃口の部品（フラッシュハイダー等）は、太さが足りればかぶせる
                if (Attach(go.transform, prefab.transform, donor, new Vector3(0f, m.y, m.z - back)) != null)
                {
                    var s2 = SightLab.RenderSide(go, 0.08f, center + new Vector3(0, 0, 0.06f));
                    SaveGrid(s2, $"muz_{wi.Id:00}_{donor.Label}");
                    s2.Dispose();
                }
            }
            catch (Exception e) { Main.Log($"[lab] [{wi.Id}] failed: {e}"); }
            UnityEngine.Object.Destroy(holder);
        }

        public static float DonorRadius(Donor d)
        {
            var dp = Tracker.ItemById(d.WeaponId)?.Weapon?.gameObject;
            var t = dp?.transform.Find(d.Path);
            if (t == null) return 0f;
            var b = MeshBounds(t, dp.transform);
            return Mathf.Min(b.extents.x, b.extents.y);
        }

        // 銃口の先端から後ろへ、銃身の中心から上下に描かれている幅（半径）を見て、太い部分（フラッシュハイダー・ブレーキ）の長さを求める。
        // 先端 15mm の最大の太さの 82% 未満が 5 回続いたところを銃身とする（スリットで抜けている所は数えない）。10cm 以内に細くならなければ 0
        public static float HiderLength(SightLab.Shot s, Vector3 m, out float r0, out string profile)
        {
            r0 = 0f;
            for (float d = 0.001f; d <= 0.015f; d += 0.001f) r0 = Mathf.Max(r0, RadiusAt(s, m.z - d, m.y));
            var sb = new System.Text.StringBuilder();
            for (float d = 0.005f; d <= 0.10f; d += 0.005f) sb.Append($"{RadiusAt(s, m.z - d, m.y) * 1000f:F1} ");
            profile = sb.ToString();
            int run = 0;
            for (float d = 0.008f; d < 0.10f; d += 0.001f)
            {
                float r = RadiusAt(s, m.z - d, m.y);
                if (r <= 0f) continue;
                if (r < r0 * 0.82f) { if (++run >= 5) return d - 0.004f; }
                else run = 0;
            }
            return 0f;
        }

        static float RadiusAt(SightLab.Shot s, float z, float axis)
        {
            int px = s.PxOfZ(z);
            if (px < 0 || px >= SightLab.W) return 0f;
            var c = s.Tex.GetPixels(px, 0, 1, SightLab.H);
            int py = s.PyOfY(axis);
            if (py < 0 || py >= SightLab.H || SightLab.IsBg(c[py])) return 0f;
            int up = py, dn = py;
            while (up < SightLab.H - 1 && !SightLab.IsBg(c[up + 1])) up++;
            while (dn > 0 && !SightLab.IsBg(c[dn - 1])) dn--;
            return Mathf.Min(s.YAt(up) - axis, axis - s.YAt(dn));
        }

        // レーザーの光線・火花などメッシュ以外の描画を消す（撮影と測定の邪魔になる）
        public static void HideNonMesh(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) if (!SightLab.IsMesh(r)) r.enabled = false;
        }

        // 銃口：横から見て一番前に描かれている位置を先端とし、その少し後ろの縦の幅の中心を銃身の中心の高さとする。
        // 戻り値 (x=銃身の半径, y=中心の高さ, z=先端)
        public static Vector3 FindMuzzle(GameObject go, Vector3 hint)
        {
            var wide = SightLab.RenderSide(go, 0.4f, new Vector3(0f, hint.y, 0.2f));
            int maxPx = -1, maxPy = -1;
            for (int py = 0; py < SightLab.H; py++)
            {
                var row = wide.Tex.GetPixels(0, py, SightLab.W, 1);
                for (int px = SightLab.W - 1; px > maxPx; px--) if (!SightLab.IsBg(row[px])) { maxPx = px; maxPy = py; break; }
            }
            float tipZ = wide.ZAt(maxPx), y0 = wide.YAt(maxPy);
            wide.Dispose();
            // 細かく撮り直して、先端から 4mm 後ろの縦の幅
            var fine = SightLab.RenderSide(go, 0.05f, new Vector3(0f, y0, tipZ));
            int fx = -1, fy = fine.PyOfY(y0);
            {
                for (int py = 0; py < SightLab.H; py++)
                {
                    var row = fine.Tex.GetPixels(0, py, SightLab.W, 1);
                    for (int px = SightLab.W - 1; px > fx; px--) if (!SightLab.IsBg(row[px])) { fx = px; fy = py; break; }
                }
            }
            tipZ = fine.ZAt(fx);
            int col = fine.PxOfZ(tipZ - 0.004f);
            var c = fine.Tex.GetPixels(col, 0, 1, SightLab.H);
            int lo = fy, hi = fy;
            if (SightLab.IsBg(c[fy])) { for (int d = 1; d < 60; d++) { if (fy - d >= 0 && !SightLab.IsBg(c[fy - d])) { fy -= d; break; } if (fy + d < SightLab.H && !SightLab.IsBg(c[fy + d])) { fy += d; break; } } lo = hi = fy; }
            while (lo > 0 && !SightLab.IsBg(c[lo - 1])) lo--;
            while (hi < SightLab.H - 1 && !SightLab.IsBg(c[hi + 1])) hi++;
            float yl = fine.YAt(lo), yh = fine.YAt(hi);
            fine.Dispose();
            return new Vector3((yh - yl) / 2f, (yl + yh) / 2f, tipZ);
        }

        // 部品を複製して、後ろの面の中心が at（武器の根元の座標）に来るように置く
        public static GameObject Attach(Transform root, Transform prefabRoot, Donor d, Vector3 at)
        {
            var dPrefab = Tracker.ItemById(d.WeaponId)?.Weapon?.gameObject;
            var src = dPrefab?.transform.Find(d.Path);
            if (src == null) return null;
            var b = MeshBounds(src, dPrefab.transform);
            var go = UnityEngine.Object.Instantiate(src.gameObject, root, false);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
            go.name = "FPC_" + d.Label;
            go.SetActive(true);
            var dr = dPrefab.transform;
            var pivot = dr.InverseTransformPoint(src.position);
            go.transform.localRotation = Quaternion.Inverse(dr.rotation) * src.rotation;
            var rs = dr.lossyScale; var ts = src.lossyScale;
            go.transform.localScale = new Vector3(ts.x / rs.x, ts.y / rs.y, ts.z / rs.z);
            var rear = new Vector3(b.center.x, b.center.y, b.min.z);
            go.transform.localPosition = pivot + (at - rear);
            SetLayer(go.transform, root.gameObject.layer);
            return go;
        }

        static float TipZ(SightLab.Shot s, float y)
        {
            float best = float.NaN;
            for (int dy = -2; dy <= 2; dy++)
            {
                int py = s.PyOfY(y) + dy;
                if (py < 0 || py >= SightLab.H) continue;
                var row = s.Tex.GetPixels(0, py, SightLab.W, 1);
                for (int px = SightLab.W - 1; px >= 0; px--)
                    if (!SightLab.IsBg(row[px])) { float z = s.ZAt(px); if (float.IsNaN(best) || z > best) best = z; break; }
            }
            return best;
        }

        static void SaveGrid(SightLab.Shot s, string name)
        {
            var tex = s.Tex;
            for (int mm = -300; mm <= 300; mm += 10)
            {
                float v = mm / 1000f; bool major = mm % 50 == 0;
                var c = major ? new Color(1f, 0.9f, 0f) : Color.white;
                int px = s.PxOfZ(s.Center.z + v);
                if (px >= 0 && px < SightLab.W) for (int py = 0; py < SightLab.H; py += major ? 1 : 4) tex.SetPixel(px, py, Color.Lerp(tex.GetPixel(px, py), c, 0.35f));
                int y = s.PyOfY(s.Center.y + v);
                if (y >= 0 && y < SightLab.H) for (int x = 0; x < SightLab.W; x += major ? 1 : 4) tex.SetPixel(x, y, Color.Lerp(tex.GetPixel(x, y), c, 0.35f));
            }
            tex.Apply();
            File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), ImageConversion.EncodeToPNG(tex));
        }

        // プレハブ（シーン外）は Renderer.bounds が空なので、メッシュの範囲を変換して求める
        public static Bounds MeshBounds(Transform obj, Transform root)
        {
            bool first = true; var all = new Bounds();
            foreach (var mf in obj.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                var m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                var mb = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (first) { all = new Bounds(c, Vector3.zero); first = false; } else all.Encapsulate(c);
                }
            }
            return all;
        }

        static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
        }
    }
}
