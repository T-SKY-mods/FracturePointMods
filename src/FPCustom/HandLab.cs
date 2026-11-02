using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using AttachHand = Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.AttachHandLayer.AttachHandLayerSettings;

namespace FPCustom
{
    // 下調べ（autotest -Dev -Scenario custhand）：グリップ枠の持ち方の設定・左手の位置、照準点（2 つ目＝ホイールで切り替える構え）とレーザーの向き、
    // 手に持った武器と腕を横から撮る（アングルフォアグリップ・ハンドガンのストック）
    public static class HandLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();
        static string V(Vector3 v) => $"({v.x:F4},{v.y:F4},{v.z:F4})";

        public static void Register()
        {
            TestMode.ExternalScenarios["custhand"] = Steps;
            TestMode.ExternalScenarios["custprobe"] = Probe;
            TestMode.ExternalScenarios["custcheck"] = Check;
        }

        static Il2Cpp.WeaponItem WeaponItemOf(int id) => Tracker.Inv.WeaponData.Weapons.ToArray().FirstOrDefault(w => w.Id == id);

        // 直したところの短い確認（autotest -Dev -Scenario custcheck）：
        //   木のストック（候補 2 つ）、AKSU のサプレッサー、MPS-5・Viper のサイトの中心、アングルフォアグリップの持ち方、ホイールの構えのレーザー
        static readonly int[] Grip4 = { 16, 17, 19, 27 };
        static List<(string, Action)> Check()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => Directory.CreateDirectory(ShotDir)));
            s.Add(("static", () =>
            {
                // グリップを足した武器：選択肢と、AFG＋レールの形（横から）
                foreach (var id in Grip4)
                    if (WeaponItemOf(id) is { } w)
                    {
                        Main.Log($"[check] [{id}] grip options: {string.Join(",", Catalog.For(id, Cat.Grip).Select(a => a.Id))}");
                    }
            }));
            foreach (var id0 in Grip4) foreach (var gid0 in new[] { "angled_grip", "vertical_grip" })
            {
                int id = id0; string gid = gid0;
                bool afg = Grips.Supported(id, Catalog.Get(gid));
                s.Add(($"set+add {id}", () =>
                {
                    ResetAll(id);
                    Store.SetForTest(id, Cat.SideRail, "laser");
                    if (afg) Store.SetForTest(id, Cat.Grip, gid);
                    Add(id); Tracker.ApplyAll();
                }));
                s.Add(("wait", () => { }));
                s.Add(($"equip {id}", () => Equip(id)));
                s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                string tag = gid == "angled_grip" ? "afg" : "vg";
                if (afg) s.Add(($"{tag} {id}", () => Measure(id, tag + "_hip", side: true, extra: true)));
                s.Add(($"cleanup {id}", () => { ResetAll(id); Tracker.ApplyAll(); Refs.Bag.MoveWeaponToStash(Tracker.ItemById(id)); }));
            }
            return s;
        }

        // 決めた時刻に撮る（リロード中など、1.5 秒ごとの手順の間）。Main.OnUpdate から
        static readonly List<(float at, Action act)> _timed = new();
        public static void Tick()
        {
            for (int i = _timed.Count - 1; i >= 0; i--)
                if (Time.unscaledTime >= _timed[i].at) { var a = _timed[i].act; _timed.RemoveAt(i); try { a(); } catch (Exception e) { Main.Log("[hand] timed failed " + e.Message); } }
        }

        static void Equip(int id)
        {
            var pn = Tracker.ItemById(id).Weapon.gameObject.name;
            var inst = Refs.Fps.InstantiatedWeapons;
            for (int i = 0; i < inst.Count; i++)
                if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == pn) { Refs.Fps.StartWeaponChange(i); return; }
        }

        static void Add(int id)
        {
            if (Tracker.Held.Any(h => h.Roll.WeaponId == id)) return;
            while (Tracker.Held.Count >= 3) Refs.Bag.MoveWeaponToStash(Tracker.ItemById(Tracker.Held.First(h => h.Roll.WeaponId != id).Roll.WeaponId));
            Tracker.Inv.AddWeapon(Tracker.ItemById(id));
        }

        // 下調べをまとめて 1 回で（autotest -Dev -Scenario custprobe）：
        //   ・グリップを足す武器（ACE-31・Swiss・Hunter-32・Hunter-12・MPS-5）のハンドガードの形、AKSU の銃口の部品（消したときの形）
        //   ・メイン武器ごとに、レーザー（＋付けられるならアングルフォアグリップ）を付けて構え・ホイールの構えで撮る。左手の位置
        //   ・アングルフォアグリップの持ち方の候補（AK-74・ER-15S）
        //   ・ハンドガンのストック：リロード・弾薬チェック（X）の途中
        static List<(string, Action)> Probe()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () =>
            {
                Directory.CreateDirectory(ShotDir);
                foreach (var w in Tracker.Inv.WeaponData.Weapons.ToArray())
                    if (new[] { 16, 17, 18, 19, 27 }.Contains(w.Id)) GuardLab.Dump(w);
                AksuMuzzle();
                foreach (var w in Tracker.Inv.WeaponData.Weapons.ToArray())
                    if (Catalog.Eligible(w) && !Catalog.IsPistol(w.Id))
                        Main.Log($"[probe] [{w.Id}] {w.GetName()} afg={Grips.Supported(w.Id, Catalog.Get("angled_grip"))} vgrip={Grips.Supported(w.Id, Catalog.Get("vertical_grip"))} afgSpot={Grips.AfgSpot(w.Id)} slotZ={Grips.SlotZ(w.Id)}");
            }));
            var rifles = Tracker.Inv.WeaponData.Weapons.ToArray().Where(w => Catalog.Eligible(w) && !Catalog.IsPistol(w.Id)).Select(w => w.Id).ToList();
            foreach (var id0 in rifles)
            {
                int id = id0;
                s.Add(($"set+add {id}", () =>
                {
                    ResetAll(id);
                    Store.SetForTest(id, Cat.SideRail, "laser");
                    if (Grips.Supported(id, Catalog.Get("angled_grip"))) Store.SetForTest(id, Cat.Grip, "angled_grip");
                    Add(id); Tracker.ApplyAll();
                }));
                s.Add(("wait", () => { }));
                s.Add(($"equip {id}", () => Equip(id)));
                s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                s.Add(("aim", () => { try { Active()?.OnAimPressed(); } catch { } }));
                s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                s.Add(($"ads {id}", () => Measure(id, "probe_ads", side: false)));
                s.Add(("cycle", () => { try { Active()?.OnCycleScope(); } catch (Exception e) { Main.Log("[hand] cycle failed " + e.Message); } }));
                s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                s.Add(($"ads2 {id}", () => Measure(id, "probe_ads2", side: false)));
                s.Add(("release", () => { try { Active()?.OnCycleScope(); Active()?.OnAimReleased(); } catch { } }));
                s.Add(($"cleanup {id}", () => { ResetAll(id); Tracker.ApplyAll(); Refs.Bag.MoveWeaponToStash(Tracker.ItemById(id)); }));
            }
            // アングルフォアグリップの持ち方の候補
            var cands = new[] { new Vector3(0f, 0f, 0f), new Vector3(0f, -0.010f, -0.020f), new Vector3(0f, 0.005f, -0.035f), new Vector3(-0.006f, -0.015f, -0.010f) };
            foreach (var id0 in new[] { 0, 21 })
            {
                int id = id0;
                s.Add(($"afg add {id}", () => { ResetAll(id); Store.SetForTest(id, Cat.Grip, "angled_grip"); Add(id); Tracker.ApplyAll(); }));
                s.Add(("wait", () => { }));
                s.Add(($"equip {id}", () => Equip(id)));
                s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                for (int k = 0; k < cands.Length; k++)
                {
                    int kk = k;
                    s.Add(($"afg pose {id} #{kk}", () => { Grips.AfgHandShift = cands[kk]; Tracker.ApplyAll(); }));
                    s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                    s.Add(($"afg shot {id} #{kk}", () => Measure(id, $"afg{kk}_hip", side: true, extra: true)));
                }
                s.Add(($"cleanup {id}", () => { Grips.AfgHandShift = Vector3.zero; ResetAll(id); Tracker.ApplyAll(); Refs.Bag.MoveWeaponToStash(Tracker.ItemById(id)); }));
            }
            // ハンドガンのストック：リロード・弾薬チェックの途中を 0.4 秒ごとに
            s.Add(("stock add", () => { ResetAll(7); Store.SetForTest(7, Cat.Stock, "p_stock"); Add(7); Tracker.ApplyAll(); }));
            s.Add(("wait", () => { }));
            s.Add(("equip 7", () => Equip(7)));
            s.Add(("wait", () => { })); s.Add(("wait", () => { }));
            s.Add(("reload 7", () =>
            {
                var w = Active(); if (w?.SelectedMag != null) w.SelectedMag.AmmoCount = Math.Max(0, w.SelectedMag.AmmoCount - 1);
                try { Refs.Fps.OnReload(); } catch (Exception e) { Main.Log("[hand] reload failed " + e.Message); }
                for (int k = 0; k < 7; k++) { int kk = k; _timed.Add((Time.unscaledTime + 0.15f + 0.4f * kk, () => Measure(7, $"reload{kk}", side: true, extra: true))); }
            }));
            for (int k = 0; k < 4; k++) s.Add(("wait", () => { }));
            s.Add(("check ammo 7", () =>
            {
                try { Refs.Fps.OnCheckAmmo(); } catch (Exception e) { Main.Log("[hand] check ammo failed " + e.Message); }
                for (int k = 0; k < 6; k++) { int kk = k; _timed.Add((Time.unscaledTime + 0.15f + 0.4f * kk, () => Measure(7, $"check{kk}", side: true, extra: true))); }
            }));
            for (int k = 0; k < 4; k++) s.Add(("wait", () => { }));
            s.Add(("cleanup 7", () => { ResetAll(7); Tracker.ApplyAll(); }));
            s.Add(("wait", () => { }));
            return s;
        }

        // AKSU の銃口の部品：そのまま／消した形を横から（1cm の目盛り）
        static void AksuMuzzle()
        {
            var prefab = Tracker.ItemById(11)?.Weapon?.gameObject;
            if (prefab == null) return;
            foreach (var hide in new[] { false, true })
            {
                var go = SightLab.CloneStatic(prefab, out var holder);
                holder.SetActive(true);
                try
                {
                    Lab.HideNonMesh(go);
                    var r = go.transform.Find("1/muzzle_aks74u_izhmash_std_545x39_LOD0_0")?.GetComponent<Renderer>();
                    if (r != null && hide) r.enabled = false;
                    var sh = SightLab.RenderSide(go, 0.08f, new Vector3(0f, 0f, 0.27f));
                    GuardLab.Grid(sh, 0.27f, 0f);
                    File.WriteAllBytes(Path.Combine(ShotDir, $"probe_aksu_muzzle_{(hide ? "off" : "on")}.png"), ImageConversion.EncodeToPNG(sh.Tex));
                    sh.Dispose();
                }
                catch (Exception e) { Main.Log("[probe] aksu failed " + e); }
                UnityEngine.Object.Destroy(holder);
            }
        }


        static void ResetAll(int id) { foreach (var c in Catalog.CatsFor(id)) Store.SetForTest(id, c, null); }

        public static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); DumpPrefabs(); }));
            // 武器ごとの構え：(武器, 名前, 付けるもの)
            var cases = new (int id, string name, (Cat, string)[] set)[]
            {
                (0, "none", new (Cat, string)[0]), (0, "afg", new[] { (Cat.Grip, "angled_grip") }), (0, "vgrip", new[] { (Cat.Grip, "vertical_grip") }),
                (21, "none", new (Cat, string)[0]), (21, "afg", new[] { (Cat.Grip, "angled_grip") }),
                (2, "vanilla", null), (2, "afg", new[] { (Cat.Grip, "angled_grip") }),
                (7, "stock", new[] { (Cat.Stock, "p_stock") }), (7, "none", new (Cat, string)[0]),
            };
            foreach (var g in cases.GroupBy(c => c.id))
            {
                int id = g.Key;
                s.Add(($"add {id}", () =>
                {
                    ResetAll(id);
                    if (Tracker.Held.Any(h => h.Roll.WeaponId == id)) return;
                    while (Tracker.Held.Count >= 3) Refs.Bag.MoveWeaponToStash(Tracker.ItemById(Tracker.Held.First(h => h.Roll.WeaponId != id).Roll.WeaponId));
                    Tracker.Inv.AddWeapon(Tracker.ItemById(id));
                }));
                s.Add(("wait", () => { }));
                s.Add(($"equip {id}", () =>
                {
                    var pn = Tracker.ItemById(id).Weapon.gameObject.name;
                    var inst = Refs.Fps.InstantiatedWeapons;
                    for (int i = 0; i < inst.Count; i++)
                        if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == pn) { Refs.Fps.StartWeaponChange(i); return; }
                }));
                s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                foreach (var c in g)
                {
                    var cc = c;
                    s.Add(($"set {id} {cc.name}", () => { ResetAll(id); if (cc.set != null) foreach (var (cat, att) in cc.set) Store.SetForTest(id, cat, att); Tracker.ApplyAll(); }));
                    s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                    s.Add(($"hip {id} {cc.name}", () => Measure(id, cc.name + "_hip")));
                    s.Add(("aim", () => { try { Active()?.OnAimPressed(); } catch { } }));
                    s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                    s.Add(($"ads {id} {cc.name}", () => Measure(id, cc.name + "_ads")));
                    s.Add(("cycle", () => { try { Active()?.OnCycleScope(); } catch (Exception e) { Main.Log("[hand] cycle failed " + e.Message); } }));
                    s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                    s.Add(($"ads2 {id} {cc.name}", () => Measure(id, cc.name + "_ads2")));
                    s.Add(("cycle back", () => { try { Active()?.OnCycleScope(); } catch { } }));
                    s.Add(("release", () => { try { Active()?.OnAimReleased(); } catch { } }));
                    s.Add(("wait", () => { }));
                }
                s.Add(($"cleanup {id}", () => { ResetAll(id); Tracker.ApplyAll(); Refs.Bag.MoveWeaponToStash(Tracker.ItemById(id)); }));
                s.Add(("wait", () => { }));
            }
            return s;
        }

        // プレハブ：グリップ枠の持ち方の設定、照準点、レーザー
        static void DumpPrefabs()
        {
            foreach (var id in new[] { 0, 2, 15, 16, 17, 18, 19, 21, 27, 28 })
            {
                var dw = Tracker.ItemById(id)?.Weapon?.gameObject?.GetComponent<DemoWeapon>();
                if (dw == null) continue;
                var root = dw.transform;
                var l = dw.gripAttachments?.attachments;
                for (int i = 0; l != null && i < l.Count; i++)
                {
                    if (l[i] == null) { Main.Log($"[hand] [{id}] grip[{i}] null"); continue; }
                    var ls = l[i].attachmentLayerSettings;
                    var sb = new System.Text.StringBuilder($"[hand] [{id}] grip[{i}] '{l[i].gameObject.name}' pos={V(root.InverseTransformPoint(l[i].transform.position))} act={dw.gripAttachments._activeIndex}:");
                    for (int k = 0; ls != null && k < ls.Count; k++)
                    {
                        var st = ls[k]; if (st == null) { sb.Append(" null"); continue; }
                        sb.Append($" {st.GetIl2CppType().Name}'{st.name}'");
                        var ah = st.TryCast<AttachHand>();
                        if (ah != null) sb.Append($"{{clip={ah.customHandPose?.name} off={V(ah.handPoseOffset.position)} rot={ah.handPoseOffset.rotation.eulerAngles} w={ah.overridePoseWeight:F2} chain={ah.elementChainName} hand={ah.handBone.name} ik={ah.ikHandBone.name} wb={ah.weaponBone.name} ikw={ah.ikWeaponBone.name}}}");
                    }
                    Main.Log(sb.ToString());
                }
                var sg = dw.scopeGroups;
                for (int g = 0; sg != null && g < sg.Count; g++)
                    for (int k = 0; sg[g]?.attachments != null && k < sg[g].attachments.Count; k++)
                    {
                        var ap = sg[g].attachments[k]?.aimPoint;
                        if (ap != null) Main.Log($"[hand] [{id}] scope[{g}][{k}] '{sg[g].attachments[k].gameObject.name}' aim={V(root.InverseTransformPoint(ap.position))} fwd={V(root.InverseTransformDirection(ap.forward))} up={V(root.InverseTransformDirection(ap.up))}");
                    }
                foreach (var lp in dw.GetComponentsInChildren<Il2Cpp.LaserPointController>(true))
                    Main.Log($"[hand] [{id}] laser '{SightLab.RelPath(lp.transform, root)}' pos={V(root.InverseTransformPoint(lp.transform.position))} fwd={V(root.InverseTransformDirection(lp.transform.forward))} max={lp.maxDistance:F1} mask={lp.hitMask.value}");
                foreach (var lr in dw.GetComponentsInChildren<LineRenderer>(true))
                    Main.Log($"[hand] [{id}] line '{SightLab.RelPath(lr.transform, root)}' world={lr.useWorldSpace} n={lr.positionCount} w={lr.startWidth:F4}/{lr.endWidth:F4} layer={lr.gameObject.layer} mat={lr.sharedMaterial?.name}");
            }
        }

        static readonly string[] Bones = { "Left_Hand", "Left_LowerArm", "Right_Hand", "Right_LowerArm", "Right_UpperArm", "Right_Shoulder", "UpperChest" };

        public static void MeasureForTest(int id, string tag, bool extra = false) => Measure(id, tag, true, extra);

        static void Measure(int id, string tag, bool side = true, bool extra = false)
        {
            var dw = Active(); var cam = Camera.main;
            if (dw == null || cam == null) { Main.Log($"[hand] [{id}] {tag}: no weapon"); return; }
            var root = dw.transform;
            string B(string n) { var t = Refs.Fps.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == n); return t == null ? "-" : V(root.InverseTransformPoint(t.position)); }
            Main.Log($"[hand] [{id}] {tag}: scope={dw._scopeIndex} cam(gun)={V(root.InverseTransformPoint(cam.transform.position))} camFwd={V(root.InverseTransformDirection(cam.transform.forward))} " + string.Join(" ", Bones.Select(b => $"{b}={B(b)}")));
            foreach (var n in new[] { "agrip", "vgrip", "stock", "laser" })
            {
                var t = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == Mount.Prefix + n);
                if (t != null) { var b = Lab.MeshBounds(t, root); Main.Log($"[hand] [{id}] {tag}: {n} min={V(b.min)} max={V(b.max)}"); }
            }
            foreach (var lr in root.GetComponentsInChildren<LineRenderer>(true))
                if (lr.enabled && lr.gameObject.activeInHierarchy && lr.positionCount > 1)
                    Main.Log($"[hand] [{id}] {tag}: line '{lr.name}' p0={V(root.InverseTransformPoint(lr.useWorldSpace ? lr.GetPosition(0) : lr.transform.TransformPoint(lr.GetPosition(0))))} p1={V(root.InverseTransformPoint(lr.useWorldSpace ? lr.GetPosition(1) : lr.transform.TransformPoint(lr.GetPosition(1))))} screen0={cam.WorldToViewportPoint(lr.useWorldSpace ? lr.GetPosition(0) : lr.transform.TransformPoint(lr.GetPosition(0)))}");
            ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"hand_{id:00}_{tag}.png"));
            if (side) RigShot(root, root.right, root.up, $"hand_{id:00}_{tag}_side.png");
            if (extra)
            {
                RigShot(root, -root.up, root.forward, $"hand_{id:00}_{tag}_bottom.png");
                RigShot(root, root.forward, root.up, $"hand_{id:00}_{tag}_front.png");
                RigShot(root, -root.right, root.up, $"hand_{id:00}_{tag}_left.png");
            }
        }

        // 腕と武器だけを dir の方向から撮る（正射影、60cm 四方）
        static void RigShot(Transform root, Vector3 dir, Vector3 up, string file)
        {
            try
            {
                var camGo = new GameObject("FPC_HandCam"); var c = camGo.AddComponent<Camera>();
                var lightGo = new GameObject("FPC_HandLight"); var lt = lightGo.AddComponent<Light>(); lt.type = LightType.Directional; lt.intensity = 2f;
                var center = root.TransformPoint(new Vector3(0f, -0.05f, -0.05f));
                camGo.transform.position = center + dir * 2f;
                camGo.transform.rotation = Quaternion.LookRotation(-dir, up);
                lightGo.transform.rotation = Quaternion.LookRotation(-dir - up * 0.5f, up);
                c.orthographic = true; c.orthographicSize = 0.3f; c.nearClipPlane = 1.5f; c.farClipPlane = 2.5f;
                c.cullingMask = (1 << root.gameObject.layer) | (1 << Refs.Fps.gameObject.layer);
                foreach (var r in Refs.Fps.GetComponentsInChildren<SkinnedMeshRenderer>(true)) c.cullingMask |= 1 << r.gameObject.layer;
                c.clearFlags = CameraClearFlags.SolidColor; c.backgroundColor = SightLab.Bg;
                var rt = new RenderTexture(1200, 1200, 24); c.targetTexture = rt; c.Render();
                RenderTexture.active = rt; var tex = new Texture2D(1200, 1200, TextureFormat.RGB24, false); tex.ReadPixels(new Rect(0, 0, 1200, 1200), 0, 0); tex.Apply(); RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(ShotDir, file), ImageConversion.EncodeToPNG(tex));
                c.targetTexture = null; UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(tex); UnityEngine.Object.Destroy(camGo); UnityEngine.Object.Destroy(lightGo);
            }
            catch (Exception e) { Main.Log("[hand] side render failed " + e.Message); }
        }
    }
}
