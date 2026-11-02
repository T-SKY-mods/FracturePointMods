using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 無料モデルのアタッチメントの見た目の確認（autotest -Dev -Scenario custparts）。
    // 武器ごとに、アングルフォアグリップ（＋レール）・サプレッサー／コンペンセイターを付けて横から撮る
    public static class PartsLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        public static void Register()
        {
            TestMode.ExternalScenarios["custparts"] = Steps;
            // 短い確認：ストック付きのハンドガンを横から撮るだけ
            TestMode.ExternalScenarios["custstock"] = () =>
            {
                var s = new List<(string, Action)> { ("setup", () => Directory.CreateDirectory(ShotDir)) };
                foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
                    if (Catalog.Eligible(wi) && Catalog.IsPistol(wi.Id)) { var w = wi; s.Add(($"stock {w.Id}", () => StockShot(w))); }
                return s;
            };
        }

        public static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "parts_*.png")) File.Delete(f); }));
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                var w = wi;
                if (!Catalog.Eligible(w)) continue;
                s.Add(($"parts {w.Id}", () =>
                {
                    Main.Log($"[plab] [{w.Id}] {w.GetName()} grip={string.Join(",", Catalog.For(w.Id, Cat.Grip).Select(a => a.Id))} muzzle={string.Join(",", Catalog.For(w.Id, Cat.Muzzle).Select(a => a.Id))}");
                    var afg = Catalog.Get("angled_grip");
                    if (!Catalog.IsPistol(w.Id) && Grips.Supported(w.Id, afg)) Shot(w, afg);
                    if (Catalog.IsPistol(w.Id)) StockShot(w);
                    OpticBack(w);
                    foreach (var id in new[] { "suppressor", "p_compensator" })
                    {
                        var a = Catalog.Get(id);
                        if (a.Pistol == Catalog.IsPistol(w.Id) && Catalog.For(w.Id, Cat.Muzzle).Contains(a)) Shot(w, a);
                    }
                }));
            }
            return s;
        }

        static string V(Vector3 v) => $"({v.x:F4},{v.y:F4},{v.z:F4})";

        public static void Shot(Il2Cpp.WeaponItem wi, Att a)
        {
            var prefab = wi.Weapon?.gameObject;
            var pdw = prefab.GetComponent<Il2CppDemo.Scripts.Runtime.Item.Weapon>();
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                Lab.HideNonMesh(go);
                foreach (var fp in Factory.On(wi.Id)) if (Catalog.Get(fp.Att)?.Cat == a.Cat) Factory.Show(go.transform, fp, false);
                Vector3 c;
                if (a.Cat == Cat.Grip)
                {
                    int idx = Grips.SlotIndex(pdw, a);
                    if (idx < 0) idx = Grips.SlotIndex(pdw, null);   // 枠が合わない武器（Grips.Manual）は「なし」の枠
                    if (idx < 0) idx = pdw.gripAttachments._activeIndex;   // 「なし」の枠も無い武器（Hunter-32）：最初の枠
                    var sp = SightLab.RelPath(pdw.gripAttachments.attachments[idx].transform, prefab.transform);
                    var slot = go.transform.Find(sp) ?? go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == Grips.AngledName);
                    if (a.Id != "bipod") slot.gameObject.SetActive(true);   // バイポッドは「なし」のまま（枠の部品を出さない）
                    var g = Grips.Place(slot, go.transform, wi.Id, a);
                    Guards.Apply(go, prefab, wi.Id, g);
                    var gb = Lab.MeshBounds(g.transform, go.transform);
                    Main.Log($"[plab] [{wi.Id}] afg min={V(gb.min)} max={V(gb.max)}");
                    if (a.Id == "angled_grip") AfgClearance(go, g, gb, wi);
                    if (a.Id == "bipod") BipodFit(go, g, gb, wi);
                    c = new Vector3(0f, gb.max.y, gb.center.z);
                }
                else
                {
                    var f = Fits.Muzzles[wi.Id];
                    if (Fits.Devices.TryGetValue(wi.Id, out var dev) && go.transform.Find(dev)?.GetComponent<Renderer>() is { } dr) dr.enabled = false;   // Mount と同じく外す
                    MuzzleCut.Apply(go, wi.Weapon.gameObject, wi.Id);   // 銃身とひとつのメッシュの銃口の部品も Mount と同じく除く
                    var m = AssetParts.MuzzleModel(wi.Id, a);
                    var p = AssetParts.AttachRear(go.transform, m, f.At(AssetParts.RearZ(wi.Id, m, f.Tip, f.Back)), a.Id);
                    var pb = Lab.MeshBounds(p.transform, go.transform);
                    Main.Log($"[plab] [{wi.Id}] {a.Id} ({m.Name}) min={V(pb.min)} max={V(pb.max)} tip={f.Tip:F4} back={f.Back:F4} axis={f.Axis:F4}");
                    c = new Vector3(0f, f.Axis, f.Tip);
                }
                var sh = SightLab.RenderSide(go, Catalog.IsPistol(wi.Id) ? 0.06f : 0.10f, c);
                File.WriteAllBytes(Path.Combine(ShotDir, $"parts_{wi.Id:00}_{a.Id}.png"), ImageConversion.EncodeToPNG(sh.Tex));
                sh.Dispose();
            }
            catch (Exception e) { Main.Log($"[plab] [{wi.Id}] {a.Id} failed: {e}"); }
            UnityEngine.Object.DestroyImmediate(holder);   // 同じフレームで続けて撮るとき、前の複製が写らないように
        }

        // アングルフォアグリップのめり込み：グリップを隠して横から撮り、グリップの前後の範囲で銃の一番下（レールなら面）を測る。
        // グリップのレールの溝の底（上面から 10.6mm 下）より下に銃がある所はめり込み。弾倉など大きく下に出るもの（3cm 超）は除く
        public const float AfgChannel = 0.0106f;
        static void AfgClearance(GameObject go, GameObject g, Bounds gb, Il2Cpp.WeaponItem wi)
        {
            var rs = g.GetComponentsInChildren<Renderer>(true);
            foreach (var r in rs) r.enabled = false;
            var sh = SightLab.RenderSide(go, 0.10f, new Vector3(0f, gb.max.y, gb.center.z));
            foreach (var r in rs) r.enabled = true;
            float floor = gb.max.y - AfgChannel, worst = -1f, worstZ = 0f;
            var prof = new List<string>();
            for (float z = gb.min.z + 0.004f; z <= gb.max.z - 0.004f; z += 0.002f)
            {
                var col = sh.Tex.GetPixels(sh.PxOfZ(z), 0, 1, SightLab.H);
                float low = float.NaN;
                for (int py = sh.PyOfY(gb.max.y - 0.03f); py <= sh.PyOfY(gb.max.y + 0.03f); py++) if (!SightLab.IsBg(col[py])) { low = sh.YAt(py); break; }
                if (float.IsNaN(low) || low < gb.max.y - 0.029f) continue;
                if (Mathf.RoundToInt((z - gb.min.z) * 1000f) % 10 == 4) prof.Add($"{z:F3}:{low:F4}");
                if (floor - low > worst) { worst = floor - low; worstZ = z; }
            }
            // グリップだけを撮った形と重なる銃の画素（溝の底より下＝グリップの本体の中）。横から見て重なる＝めり込み（左右に外れた部品を除けば）
            var others = go.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && !r.transform.IsChildOf(g.transform)).ToArray();
            foreach (var r in others) r.enabled = false;
            var only = SightLab.RenderSide(go, 0.10f, new Vector3(0f, gb.max.y, gb.center.z));
            foreach (var r in others) r.enabled = true;
            int overlap = 0; float oz0 = 9f, oz1 = -9f;
            int pyFloor = sh.PyOfY(floor - 0.001f);
            for (int px = sh.PxOfZ(gb.min.z) - 2; px <= sh.PxOfZ(gb.max.z) + 2; px++)
            {
                var gc = sh.Tex.GetPixels(px, 0, 1, pyFloor); var ac = only.Tex.GetPixels(px, 0, 1, pyFloor);
                for (int py = 0; py < pyFloor; py++)
                    if (!SightLab.IsBg(gc[py]) && !SightLab.IsBg(ac[py])) { overlap++; oz0 = Mathf.Min(oz0, sh.ZAt(px)); oz1 = Mathf.Max(oz1, sh.ZAt(px)); }
            }
            only.Dispose();
            sh.Dispose();
            Main.Log($"[plab] AFGHIT [{wi.Id}] overlap={overlap}px z={oz0:F3}..{oz1:F3}");
            // MPS-5 / Viper：レール（Viper の rail.smd）が他の武器より 2mm ほど高く、溝の奥まで入る。横から見るとレールがグリップの溝に
            // はまっているだけで、外から見えるめり込みは無い（画像で確認済み）。この 2 丁だけ、溝の奥 2.5mm までは許す
            float allow = wi.Id == 27 || wi.Id == 28 ? 0.0025f : 0.001f;
            Check($"{wi.GetName()} angled grip: not inside the gun", (overlap < 40 || allow > 0.001f) && worst < allow);
            Main.Log($"[plab] AFGFIT [{wi.Id}] {wi.GetName()} top={gb.max.y:F4} floor={floor:F4} sink={worst:F4} at z={worstZ:F3} low=[{string.Join(" ", prof)}]");
        }

        // バイポッドの当たる所：ほかのアタッチメント（グリップ）が付くレール（またはハンドガード）の下面の、前の端に付ける。
        //   バイポッドと足したレールを隠して横から撮り、銃の下面の高さを 1mm ごとに測る。グリップの付く所（gz）の下面の一番下を基準（ref）に、
        //   そこから前へ、（歯の凹凸は前後 6mm の一番下でならす）下面が ref の ±3mm に収まる間をレールの続き（ゆるい傾きを含む）とし、その前の端から 3mm 手前（ならした 6mm も戻す）にバイポッドの前の端を合わせる。
        //   上面の高さ：グリップの高さ＋（その所の下面の一番下 − ref）。足したレールの武器はその所の下面（レールをそこに付ける）。結果（BIPODAT）を Grips.BipodAt に書き写す
        static void BipodFit(GameObject go, GameObject g, Bounds gb, Il2Cpp.WeaponItem wi)
        {
            int id = wi.Id;
            if (Guards.Blocks.ContainsKey(id)) { Main.Log($"[plab] BIPODAT [{id}] {wi.GetName()}: block (rule)"); return; }   // 台の範囲の前の端（Grips.BipodSpot）
            var gs = Grips.RailSpot(id);
            if (gs == null) { Main.Log($"[plab] BIPODAT [{id}] {wi.GetName()}: no grip spot"); return; }
            float gy = gs.Value.x, gz = gs.Value.y;
            bool bolt = Guards.BoltOn.Contains(id);
            var rs = g.GetComponentsInChildren<Renderer>(true).ToList();
            if (bolt) rs.AddRange(go.GetComponentsInChildren<Renderer>(true).Where(r => r.gameObject.name == Mount.Prefix + "rail"));
            foreach (var r in rs) r.enabled = false;
            float z0 = gz - 0.015f, z1 = Fits.Muzzles.TryGetValue(id, out var mf) ? mf.Tip - mf.Back - 0.005f : gz + 0.3f;
            var c = new Vector3(0f, gy, (z0 + z1) / 2f);
            var sh = SightLab.RenderSide(go, Mathf.Max(0.06f, (z1 - z0) / 2f + 0.02f) / 2f, c);
            foreach (var r in rs) r.enabled = true;
            float Low(float z)
            {
                var col = sh.Tex.GetPixels(sh.PxOfZ(z), 0, 1, SightLab.H);
                for (int py = Mathf.Max(0, sh.PyOfY(gy - 0.03f)); py <= Mathf.Min(SightLab.H - 1, sh.PyOfY(gy + 0.04f)); py++)
                    if (!SightLab.IsBg(col[py])) { float y = sh.YAt(py); return y < gy - 0.029f ? float.NaN : y; }
                return float.NaN;
            }
            var prof = new List<(float z, float y)>();
            for (float z = z0; z <= z1; z += 0.001f) prof.Add((z, Low(z)));
            sh.Dispose();
            Main.Log($"[plab] BIPODPROF [{id}] grip=({gy:F4},{gz:F4}) " + string.Join(" ", prof.Where((p, i) => i % 5 == 0).Select(p => $"{p.z:F3}:{(float.IsNaN(p.y) ? "-" : p.y.ToString("F4"))}")));
            // レールの歯（10mm ごとの凹凸）をならす：前後 6mm の中の一番下
            var raw = prof.ToList();
            prof = raw.Select(p => (p.z, raw.Where(q => Mathf.Abs(q.z - p.z) <= 0.006f && !float.IsNaN(q.y)).Select(q => q.y).DefaultIfEmpty(float.NaN).Min())).ToList();
            var at = prof.Where(p => Mathf.Abs(p.z - gz) <= 0.01f && !float.IsNaN(p.y)).ToList();
            if (at.Count == 0) { Main.Log($"[plab] BIPODAT [{id}] {wi.GetName()}: nothing at the grip"); Check($"{wi.GetName()} bipod: rail found", false); return; }
            float rf = at.Min(p => p.y);
            bool OnRail(float y) => !float.IsNaN(y) && y >= rf - 0.003f && y <= rf + 0.003f;
            // グリップの所から前へ、レールの続く前の端（3mm 以上続けて外れたら終わり）
            float end = gz; int miss = 0;
            foreach (var p in prof.Where(p => p.z >= gz))
            {
                if (OnRail(p.y)) { end = p.z; miss = 0; }
                else if (++miss >= 3) break;
            }
            float depth = gb.size.z;
            float zc = Mathf.Max(gz, end - 0.009f - depth / 2f);   // ならした分（6mm）＋3mm 手前
            var span = prof.Where(p => p.z >= zc - depth / 2f && p.z <= zc + depth / 2f && OnRail(p.y)).ToList();
            float lo = span.Count > 0 ? span.Min(p => p.y) : rf;
            float y = bolt ? lo : gy + (lo - rf);
            Main.Log($"[plab] BIPODAT [{id}] = new({y:F4}f, {zc:F4}f),   // {wi.GetName()}  rail ref={rf:F4} end={end:F3}  now top={gb.max.y:F4} z={gb.center.z:F4}");
            // 位置は Grips.BipodAt（見た目で合わせた値）が決める。測った前の端との差は参考に残すだけ
            Main.Log($"[plab] BIPODDZ [{id}] {wi.GetName()} bipod vs measured rail front: dz {gb.center.z - zc:F4}");
        }

        // サイトを載せて真後ろから撮り、サイトの本体の左右の中心と銃の中心（最初の照準点の左右）を比べる
        public static void OpticBack(Il2Cpp.WeaponItem wi)
        {
            var prefab = wi.Weapon?.gameObject;
            var pap = prefab.GetComponent<Il2CppDemo.Scripts.Runtime.Item.Weapon>()?.scopeGroups;
            float gunX = pap != null && pap.Count > 0 && pap[0].attachments.Count > 0 ? prefab.transform.InverseTransformPoint(pap[0].attachments[0].aimPoint.position).x : 0f;
            foreach (var a in Catalog.For(wi.Id, Cat.Sight))
            {
                if (Catalog.IsFactory(a, wi.Id) || Optics.Get(a) == null) continue;
                var go = SightLab.CloneStatic(prefab, out var holder);
                holder.SetActive(true);
                try
                {
                    Lab.HideNonMesh(go);
                    foreach (var fp in Factory.On(wi.Id)) if (Catalog.Get(fp.Att)?.Cat == Cat.Sight) Factory.Show(go.transform, fp, false);
                    if (!Optics.Apply(go, prefab, wi.Id, a)) continue;
                    var opt = go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == Mount.Prefix + "optic_" + a.Id);
                    var ob = Lab.MeshBounds(opt, go.transform);
                    var ret = Optics.LastReticle;
                    // 銃の中心の線＝最初の照準点の左右（ハンドガンはスライドの中心のずれ X を足す）。サイトの本体の描画の範囲は横のつまみ等で左右非対称なので記録だけ
                    float center = gunX + (Optics.SpotFor(wi.Id)?.X ?? 0f), off = ret.x - center;
                    Main.Log($"[plab] OPTICX [{wi.Id}] {wi.GetName()} {a.Id}: body x={ob.min.x:F4}..{ob.max.x:F4} center={ob.center.x:F4} width={ob.size.x:F4} reticle x={ret.x:F4} gun x={center:F4} off={off:F4}");
                    Check($"{wi.GetName()} {a.Id}: sight on the gun center line", Mathf.Abs(off) < 0.0008f);
                    var tex = SightLab.RenderBack(go, 0.05f, new Vector3(gunX, ret.y - 0.02f, 0f));
                    File.WriteAllBytes(Path.Combine(ShotDir, $"parts_{wi.Id:00}_back_{a.Id}.png"), ImageConversion.EncodeToPNG(tex));
                    UnityEngine.Object.Destroy(tex);
                }
                catch (Exception e) { Main.Log($"[plab] [{wi.Id}] optic back {a.Id} failed: {e}"); }
                finally { UnityEngine.Object.Destroy(holder); }
            }
        }

        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");

        // ハンドガンのストック：グリップの下端と背中（後ろの面）を横からの撮影で測り、ストックの前の上端をグリップの背中の中ほどに当てる
        public static void StockShot(Il2Cpp.WeaponItem wi)
        {
            var prefab = wi.Weapon?.gameObject;
            var go = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            try
            {
                Lab.HideNonMesh(go);
                foreach (var fp in Factory.On(wi.Id)) Factory.Show(go.transform, fp, false);
                var f = Fits.Muzzles[wi.Id];
                var center = new Vector3(0f, f.Axis - 0.07f, f.Tip - 0.12f);
                var sh = SightLab.RenderSide(go, 0.12f, center);
                // 一番下の描画（グリップの下端）
                float bottom = float.MaxValue, bottomZ = 0f;
                for (int px = 0; px < SightLab.W; px++)
                {
                    var col = sh.Tex.GetPixels(px, 0, 1, SightLab.H);
                    for (int py = 0; py < SightLab.H; py++) if (!SightLab.IsBg(col[py])) { float y = sh.YAt(py); if (y < bottom) { bottom = y; bottomZ = sh.ZAt(px); } break; }
                }
                // グリップの背中：下端から 1〜5cm 上の各行で一番後ろの描画
                var rows = new List<(float y, float z)>();
                for (float y = bottom + 0.01f; y <= bottom + 0.05f; y += 0.005f)
                {
                    var row = sh.Tex.GetPixels(0, sh.PyOfY(y), SightLab.W, 1);
                    for (int px = 0; px < SightLab.W; px++) if (!SightLab.IsBg(row[px])) { rows.Add((y, sh.ZAt(px))); break; }
                }
                // 金具の当て方の実測（1mm ごと）：グリップの背中の下端（弾倉の底板より上で背中が後ろへ出る所）・背中の傾き・グリップの幅・スライドの後ろの端
                var rear = new List<(float y, float z)>();
                for (float y = bottom + 0.001f; y <= bottom + 0.06f; y += 0.001f)
                {
                    var row = sh.Tex.GetPixels(0, sh.PyOfY(y), SightLab.W, 1);
                    for (int px = 0; px < SightLab.W; px++) if (!SightLab.IsBg(row[px])) { rear.Add((y, sh.ZAt(px))); break; }
                }
                float zBack = rear.Where(r => r.y <= bottom + 0.035f).Min(r => r.z);
                var heel = rear.First(r => r.z < zBack + 0.002f);
                float ZAtY(float y) { var r = rear.OrderBy(q => Mathf.Abs(q.y - y)).First(); return r.z; }
                float slope = (ZAtY(heel.y + 0.020f) - ZAtY(heel.y + 0.004f)) / 0.016f;
                float slideRear = float.MaxValue;
                {
                    var row = sh.Tex.GetPixels(0, sh.PyOfY(f.Axis + 0.008f), SightLab.W, 1);
                    for (int px = 0; px < SightLab.W; px++) if (!SightLab.IsBg(row[px])) { slideRear = sh.ZAt(px); break; }
                }
                sh.Dispose();
                var bk = SightLab.RenderBack(go, 0.06f, new Vector3(0f, heel.y + 0.01f, 0f));
                float Width(float y, out float cx)
                {
                    int py = Mathf.RoundToInt(SightLab.H / 2f + (y - (heel.y + 0.01f)) / 0.06f * (SightLab.H / 2f));
                    var row = bk.GetPixels(0, py, SightLab.W, 1);
                    int l = -1, r = -1;
                    for (int px = 0; px < SightLab.W; px++) if (!SightLab.IsBg(row[px])) { if (l < 0) l = px; r = px; }
                    float X(int px) => (px - SightLab.W / 2f) / (SightLab.W / 2f) * 0.12f;
                    cx = l < 0 ? 0f : (X(l) + X(r)) / 2f;
                    return l < 0 ? 0f : X(r) - X(l);
                }
                float w4 = Width(heel.y + 0.004f, out var cx4), w15 = Width(heel.y + 0.015f, out var cx15);
                UnityEngine.Object.Destroy(bk);
                Main.Log($"[plab] STOCKFIT [{wi.Id}] {wi.GetName()} heel=({heel.y:F4},{heel.z:F4}) slope={slope:F3} width4={w4:F4} (cx {cx4:F4}) width15={w15:F4} (cx {cx15:F4}) slideRear={slideRear:F4} len={f.Tip - slideRear:F4} bottom={bottom:F4}");
                Main.Log($"[plab] STOCK [{wi.Id}] {wi.GetName()} bottom={bottom:F4} at z={bottomZ:F4} back=[{string.Join(" ", rows.Select(r => $"{r.y:F3}:{r.z:F4}"))}] axis={f.Axis:F4} tip={f.Tip:F4}");
                // 装備中のストック（実際の武器から複製したとき）は外してから撮る
                for (Transform old; (old = go.transform.Find(Mount.Prefix + "stock")) != null;) UnityEngine.Object.DestroyImmediate(old.gameObject);
                if (!AssetParts.Has(AssetParts.StockModel) || AssetParts.AttachStock(go.transform, wi.Id) == null) return;
                // ストックの床板まで入るように、銃口から床板までの真ん中を中心に撮る
                var fit = AssetParts.StockAt[wi.Id];
                float rearZ = fit.HeelZ - AssetParts.StockLen(fit);
                var sh2 = SightLab.RenderSide(go, Mathf.Max(0.16f, (f.Tip - rearZ) / 4f + 0.02f), new Vector3(0f, f.Axis - 0.06f, (f.Tip + rearZ) / 2f));
                File.WriteAllBytes(Path.Combine(ShotDir, $"parts_{wi.Id:00}_stock.png"), ImageConversion.EncodeToPNG(sh2.Tex));
                sh2.Dispose();
            }
            catch (Exception e) { Main.Log($"[plab] [{wi.Id}] stock failed: {e}"); }
            UnityEngine.Object.Destroy(holder);
        }
    }
}
