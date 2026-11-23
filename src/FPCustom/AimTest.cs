using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using AimState = Il2CppDemo.Scripts.Runtime.Character.FPSAimState;

namespace FPCustom
{
    // autotest -Dev -Scenario custaimcheck：
    //   ・Swiss 751 がエイムできるか（ストック・サイトの組み合わせ）。比べる元に AK-12
    //   ・エルゴで構える速さが変わるか（AK-12。エルゴ +0 と +50 で、画面の拡大・カメラの移動が 90% まで進む時間）
    //   ・ストックの仕上げ（銃の色・つや）と M16 の付け根の色。shots/aim_*.png, rstockfin_*.png
    public static class AimTest
    {
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();
        static string Shots { get { var d = Path.Combine(Main.Dir, "shots"); Directory.CreateDirectory(d); return d; } }

        public static void Register()
        {
            TestMode.ExternalScenarios["custaimcheck"] = Steps;
            TestMode.ExternalScenarios["custaimall"] = AllSteps;
            TestMode.ExternalScenarios["custadsprobe"] = ProbeSteps;
            TestMode.ExternalScenarios["custstockads"] = StockAdsSteps;   // 構えたときのストックの見え方（透けないか）
            TestMode.ExternalScenarios["custstockcap"] = () => { _only = new HashSet<int> { 3, 10, 20, 27, 28 }; _cap = true; StockIds = new[] { "stock_prs", "stock_mcx" }; return StockAdsSteps(); };
            TestMode.ExternalScenarios["custreddot"] = () => { _holo = true; _sight = "ace_sight"; _only = new HashSet<int> { 2, 10, 16 }; StockIds = new[] { "none" }; return StockAdsSteps(); };
            TestMode.ExternalScenarios["custeagleaim"] = EagleSteps;
            TestMode.ExternalScenarios["custscrollaim"] = ScrollSteps;   // マウスホイールで持ち替えた後に、実際の右クリックで構えられるか   // ストック付きのイーグルで、持ち替え・リロード・射撃の直後にも構えられるか
            TestMode.ExternalScenarios["custstockholo"] = () => { _holo = true; StockIds = new[] { "none", "stock_prs" }; return StockAdsSteps(); };
        }

        // ---------- custaimall：Swiss 751 × 付けられるアタッチメント 1 つずつ（ほかは外す）で、実際の右クリックで構えられるか ----------
        //   構えた：エイムの状態が Aiming/PointAiming で、銃がカメラから見て 1cm 以上動いた（構えの姿勢に入った）
        static int _fails, _done;
        static readonly List<string> _failList = new();

        static List<(int id, List<(string label, (Cat, string)[] set)> cfgs)> Plan()
        {
            var plan = new List<(int, List<(string, (Cat, string)[])>)>();
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                if (wi == null || wi.Id != 17) continue;   // Swiss 751 だけ
                int id = wi.Id;
                var cfgs = new List<(string, (Cat, string)[])> { ("none", Array.Empty<(Cat, string)>()) };
                foreach (var c in Catalog.CatsFor(id))
                    foreach (var a in Catalog.For(id, c)) cfgs.Add(($"{c}={a.Id}", new[] { (c, a.Id) }));
                // サイト × ストック（ホロとストックの組み合わせなど）
                foreach (var sg in Catalog.For(id, Cat.Sight))
                    foreach (var st in Catalog.For(id, Cat.Stock))
                        cfgs.Add(($"Sight={sg.Id}+Stock={st.Id}", new[] { (Cat.Sight, sg.Id), (Cat.Stock, st.Id) }));
                plan.Add((id, cfgs));
            }
            return plan;
        }

        static IEnumerator AimAll(int id, List<(string label, (Cat, string)[] set)> cfgs)
        {
            _real = true;
            foreach (var (label, set) in cfgs)
            {
                var name = $"{Tracker.NameOf(id)} {label}";
                Exception err = null;
                try { Set(id, set); } catch (Exception e) { err = e; }
                if (err != null) { Main.Log($"[test] aimall {name}: set failed {err.Message}"); }
                yield return new WaitForSeconds(0.45f);
                if (!Foreground()) { SetForegroundWindow(System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle); yield return new WaitForSeconds(0.2f); }
                if (!Foreground()) { Main.Log($"[test] aimall {name}: game not in front, skipped"); continue; }
                var dw = Active();
                if (dw == null || Weapons.IdOf(dw) != id) { Check($"aimall {name}: held", false); continue; }
                yield return Aim("all_" + id + "_" + label.Replace('=', '_'));
                var r = _r["all_" + id + "_" + label.Replace('=', '_')];
                bool ok = r.aimed;
                if (!ok) { _fails++; _failList.Add(name); }
                Check($"aimall {name}: aims with a right click (state ok {r.aimed}, gun 90% at {r.tCam:0.00}s)", ok);
                _done++;
                yield return new WaitForSeconds(0.45f);
            }
            Set(id, Factory(id));
            _real = false;
        }

        // 元の姿（最初から付いている物）
        static (Cat, string)[] Factory(int id)
        {
            var l = new List<(Cat, string)>();
            foreach (var c in Catalog.CatsFor(id)) foreach (var a in Catalog.For(id, c)) if (Catalog.IsFactory(a, id)) { l.Add((c, a.Id)); break; }
            return l.ToArray();
        }

        static List<(string, Action)> AllSteps()
        {
            var s = new List<(string, Action)>();
            List<(int id, List<(string label, (Cat, string)[] set)> cfgs)> plan;
            try { plan = Plan(); } catch (Exception e) { Main.Log("[test] aimall plan failed " + e); return s; }
            Main.Log($"[test] aimall: {plan.Count} weapons, {plan.Sum(p => p.cfgs.Count)} configs");
            foreach (var (id, cfgs) in plan)
            {
                EffectTest.HoldForLab(s, id);
                s.Add(($"aimall {id}", () => MelonLoader.MelonCoroutines.Start(AimAll(id, cfgs))));
                int waits = Mathf.CeilToInt(cfgs.Count * 2.4f / 1.5f) + 1;
                for (int i = 0; i < waits; i++) s.Add(("wait", () => { }));
            }
            s.Add(("aimall result", () =>
            {
                Main.Log($"[test] aimall: {_done} configs, {_fails} failed: {string.Join(" | ", _failList)}");
                Check($"aimall: every weapon and attachment aims ({_done} configs, {_fails} failed)", _fails == 0 && _done > 0);
            }));
            return s;
        }

        // ---------- custstockads：ストックを付けた武器を構えた画面（shots/sads_<id>_<att>.png）と、カメラの近くで切れるストックの頂点 ----------
        static string[] StockIds = { "none", "stock_mcx", "stock_prs", "stock_sba3", "stock_moe" };
        static bool _holo, _cap; static HashSet<int> _only; static string _sight = "holo";
        static List<(string, Action)> StockAdsSteps()
        {
            var s = new List<(string, Action)>();
            foreach (var id in RifleStocks.Fits.Keys.OrderBy(k => k))
            {
                if (Tracker.ItemById(id) == null || (_only != null && !_only.Contains(id))) continue;
                int w = id;
                EffectTest.HoldForLab(s, w);
                s.Add(($"stockads {w}", () => MelonLoader.MelonCoroutines.Start(StockAds(w))));
                for (int i = 0; i < Mathf.CeilToInt(StockIds.Length * (_cap ? 2 : 1) * 2.6f / 1.5f) + 1; i++) s.Add(("wait", () => { }));
            }
            s.Add(("stockads result", () => Main.Log($"[test] stockads: {_clipBad} configs with stock vertices cut by the camera: {string.Join(" | ", _clipList)}")));
            return s;
        }
        static int _clipBad; static readonly List<string> _clipList = new();

        static IEnumerator StockAds(int id)
        {
            var fps = Refs.Fps;
            var cfgs = new List<string>();
            foreach (var a in StockIds) { if (_cap) { cfgs.Add(a + "|holo"); cfgs.Add(a + "|irons"); } else cfgs.Add(a); }
            foreach (var cfg in cfgs)
            {
                var att = cfg.Split('|')[0];
                if (_cap) _holo = cfg.EndsWith("|holo");
                var set = new List<(Cat, string)>();
                if (att != "none") set.Add((Cat.Stock, att));
                if (_holo && Catalog.For(id, Cat.Sight).Any(a => a.Id == _sight)) set.Add((Cat.Sight, _sight));
                try { Set(id, set.ToArray()); } catch (Exception e) { Main.Log($"[test] stockads {id} {att}: set failed {e.Message}"); }
                yield return new WaitForSeconds(0.5f);
                var dw = Active();
                if (dw == null || Weapons.IdOf(dw) != id) { Check($"stockads {Tracker.NameOf(id)} {att}: held", false); continue; }
                fps.OnAim(true);
                yield return new WaitForSeconds(1.4f);
                yield return new WaitForEndOfFrame();
                var cam = ScreenCam();
                string info = "";
                foreach (var c in Camera.allCameras) info += $"{c.name}(near {c.nearClipPlane:0.####} depth {c.depth} mask {c.cullingMask}) ";
                Transform st = null;
                foreach (var t in dw.GetComponentsInChildren<Transform>(false)) if (t.name == Mount.Prefix + RifleStocks.Name) { st = t; break; }
                if (st != null)
                {
                    float near = cam.nearClipPlane, minZ = 99f; int cut = 0, n = 0;
                    foreach (var mf in st.GetComponentsInChildren<MeshFilter>(false))
                    {
                        var m = mf.sharedMesh; if (m == null || !m.isReadable) continue;
                        var vs = m.vertices; var l2w = mf.transform.localToWorldMatrix;
                        foreach (var v in vs)
                        {
                            var p = cam.transform.InverseTransformPoint(l2w.MultiplyPoint3x4(v)); n++;
                            float half = Mathf.Max(p.z, near) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                            if (Mathf.Abs(p.y) > half * 1.05f || Mathf.Abs(p.x) > half * cam.aspect * 1.05f) continue;   // 画面の外
                            minZ = Mathf.Min(minZ, p.z);
                            if (p.z < near) cut++;
                        }
                    }
                    var sb = Lab.MeshBounds(st, cam.transform);
                    Main.Log($"[test] stockads {id} {Tracker.NameOf(id)} {att}: verts {n} on screen min z {minZ:0.0000} near {near:0.####} cut {cut} | stock bounds in camera min {sb.min} max {sb.max}");
                    if (cut > 0) { _clipBad++; _clipList.Add($"{id} {att} ({cut})"); }
                }
                else if (att != "none") Main.Log($"[test] stockads {id} {att}: stock object not found");
                if (att == "none" && id == RifleStocks.Fits.Keys.Min()) Main.Log("[test] cameras: " + info);
                ScreenCapture.CaptureScreenshot(Path.Combine(Shots, $"{(_cap ? "scap" : _holo ? (_sight == "holo" ? "sholo" : "s" + _sight) : "sads")}_{id}_{att}{(_cap ? (_holo ? "_holo" : "_irons") : "")}.png"));
                yield return null;
                fps.OnAim(fps.toggleAim);
                yield return new WaitForSeconds(0.4f);
            }
            try { Set(id, Factory(id)); } catch { }
        }

        // ---------- custeagleaim：ユーザーの構成（イーグル＋木のストック＋ドット＋コンペンセイター＋レーザー＋拡張マガジン）で、いろいろな直後に実際の右クリックで構える ----------
        const int Eagle = 5, Val = 14;
        static int Slot(int id)
        {
            var pn = Tracker.ItemById(id).Weapon.gameObject.name; var inst = Refs.Fps.InstantiatedWeapons;
            for (int i = 0; i < inst.Count; i++) if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == pn) return i;
            return -1;
        }
        static IEnumerator PressAfter(string label, Action before, float delay, float hold)
        {
            var fps = Refs.Fps;
            try { before?.Invoke(); } catch (Exception e) { Main.Log($"[test] {label}: before failed {e.Message}"); }
            yield return new WaitForSeconds(delay);
            SetForegroundWindow(System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle);
            yield return null;
            var s0 = fps._aimState; var a0 = fps._actionState;
            mouse_event(0x08, 0, 0, 0, UIntPtr.Zero);
            if (fps.toggleAim) { yield return null; mouse_event(0x10, 0, 0, 0, UIntPtr.Zero); }
            float t0 = Time.time; bool ever = false; string seen = "";
            while (Time.time - t0 < hold)
            {
                yield return null;
                bool on = (int)fps._aimState == 2 || (int)fps._aimState == 3; ever |= on;
                string st = $"{fps._aimState}/{fps._actionState}/{fps._movementComponent?.MovementState}";
                if (!seen.EndsWith(st)) seen += $" {Time.time - t0:0.00}s {st}";
            }
            bool now = (int)fps._aimState == 2 || (int)fps._aimState == 3;
            ScreenCapture.CaptureScreenshot(Path.Combine(Shots, $"eagle_{label}.png"));
            Main.Log($"[test] eagle {label}: before {s0}/{a0} | held {Weapons.IdOf(Active())} | {seen} | toggle {fps.toggleAim} pause {fps._isPauseMenuOpened}");
            Check($"eagle {label}: aiming while the button is held", now && Weapons.IdOf(Active()) == Eagle);
            yield return null;
            if (fps.toggleAim) { mouse_event(0x08, 0, 0, 0, UIntPtr.Zero); yield return null; }
            mouse_event(0x10, 0, 0, 0, UIntPtr.Zero);
        }
        // ---------- custscrollaim：VAL・イーグル・AKSU を持って、実際のマウスホイールで持ち替えてから右クリック ----------
        static IEnumerator Wheel(int ticks, float gap)
        {
            SetForegroundWindow(System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle);
            yield return null;
            for (int i = 0; i < Math.Abs(ticks); i++)
            {
                mouse_event(0x0800, 0, 0, unchecked((uint)(ticks > 0 ? 120 : -120)), UIntPtr.Zero);
                if (gap > 0f) yield return new WaitForSeconds(gap); else yield return null;
            }
        }
        static IEnumerator ScrollPress(string label, int ticks, float gap, float delay, bool aimFirst)
        {
            var fps = Refs.Fps; var cam = ScreenCam();
            if (aimFirst)
            {
                SetForegroundWindow(System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle); yield return null;
                mouse_event(0x08, 0, 0, 0, UIntPtr.Zero); yield return null; mouse_event(0x10, 0, 0, 0, UIntPtr.Zero);
                yield return new WaitForSeconds(0.6f);
                Main.Log($"[test] scroll {label}: aimed first {fps._aimState}");
            }
            int w0 = Weapons.IdOf(Active());
            yield return Wheel(ticks, gap);
            yield return new WaitForSeconds(delay);
            yield return new WaitForEndOfFrame();
            float fov0 = cam.fieldOfView; var s0 = fps._aimState; var a0 = fps._actionState;
            mouse_event(0x08, 0, 0, 0, UIntPtr.Zero);
            if (fps.toggleAim) { yield return null; mouse_event(0x10, 0, 0, 0, UIntPtr.Zero); }
            float t0 = Time.time; string seen = "";
            while (Time.time - t0 < 1.3f)
            {
                yield return new WaitForEndOfFrame();
                string st = $"{fps._aimState}/{fps._actionState}";
                if (!seen.EndsWith(st)) seen += $" {Time.time - t0:0.00}s {st}";
            }
            float fov1 = cam.fieldOfView;
            bool on = (int)fps._aimState == 2 || (int)fps._aimState == 3;
            var ap = AimPt(); var lp = ap != null ? cam.transform.InverseTransformPoint(ap.position) : Vector3.one;
            float off = new Vector2(lp.x, lp.y).magnitude;
            ScreenCapture.CaptureScreenshot(Path.Combine(Shots, $"scroll_{label}.png"));
            Main.Log($"[test] scroll {label}: weapon {w0}->{Weapons.IdOf(Active())} before {s0}/{a0} |{seen} | sight off the camera line {off * 1000f:0.0}mm (ap {ap?.name}) toggle {fps.toggleAim}");
            Main.Log($"[test] scroll {label}: stance {Stance.DiagForTest()}");
            Check($"scroll {label}: aims after the wheel switch (state {fps._aimState}, sight {off * 1000f:0.0}mm off centre)", on && off < 0.01f);
            yield return null;
            if (fps.toggleAim) { mouse_event(0x08, 0, 0, 0, UIntPtr.Zero); yield return null; }
            mouse_event(0x10, 0, 0, 0, UIntPtr.Zero);
        }
        static List<(string, Action)> ScrollSteps()
        {
            var s = new List<(string, Action)>();
            EffectTest.HoldForLab(s, Val);
            EffectTest.HoldForLab(s, 11);
            EffectTest.HoldForLab(s, Eagle);
            s.Add(("eagle config", () => Set(Eagle, (Cat.Muzzle, "p_compensator"), (Cat.Stock, "p_wood_stock"), (Cat.Sight, "p_dot"), (Cat.SideRail, "p_laser"), (Cat.Mag, "p_ext_mag"))));
            s.Add(("wait", () => { }));
            int k = 0;
            void P(string label, int ticks, float gap, float delay, bool aimFirst = false)
            {
                string l = $"{k++:00}_{label}";
                s.Add((l, () => MelonLoader.MelonCoroutines.Start(ScrollPress(l, ticks, gap, delay, aimFirst))));
                for (int i = 0; i < 3; i++) s.Add(("wait", () => { }));
            }
            for (int i = 0; i < 3; i++) P("one_down", -1, 0f, 1.5f);
            for (int i = 0; i < 3; i++) P("one_up", 1, 0f, 1.5f);
            P("three_fast", -3, 0f, 1.5f);
            P("five_fast", 5, 0f, 1.5f);
            P("two_spaced", -2, 0.25f, 1.5f);
            P("press_right_after", -1, 0f, 0.15f);
            P("press_mid_switch", -1, 0f, 0.5f);
            P("while_aiming", -1, 0f, 1.5f, true);
            P("while_aiming_fast", 2, 0f, 1.5f, true);
            for (int i = 0; i < 3; i++) P("after_all", -1, 0f, 1.5f);
            s.Add(("socket fixes", () => Main.Log($"[test] scroll: CameraSocket put back {Stance.SocketFixesForTest} times")));
            return s;
        }

        static List<(string, Action)> EagleSteps()
        {
            var s = new List<(string, Action)>();
            EffectTest.HoldForLab(s, Val);
            EffectTest.HoldForLab(s, Eagle);
            s.Add(("eagle config", () => Set(Eagle, (Cat.Muzzle, "p_compensator"), (Cat.Stock, "p_wood_stock"), (Cat.Sight, "p_dot"), (Cat.SideRail, "p_laser"), (Cat.Mag, "p_ext_mag"))));
            s.Add(("wait", () => { }));
            void P(string label, Action before, float delay) { s.Add((label, () => MelonLoader.MelonCoroutines.Start(PressAfter(label, before, delay, 1.5f)))); s.Add(("wait", () => { })); s.Add(("wait", () => { })); }
            P("plain", null, 0.1f);
            s.Add(("to VAL", () => Refs.Fps.StartWeaponChange(Slot(Val)))); s.Add(("wait", () => { }));
            P("right_after_switch", () => Refs.Fps.StartWeaponChange(Slot(Eagle)), 0.05f);
            s.Add(("to VAL", () => Refs.Fps.StartWeaponChange(Slot(Val)))); s.Add(("wait", () => { }));
            P("mid_switch", () => Refs.Fps.StartWeaponChange(Slot(Eagle)), 0.4f);
            P("after_switch_1s", null, 0.1f);
            P("during_reload", () => { var inv = Refs.Fps; inv.OnReload(); }, 0.2f);
            P("after_fire", () => { Refs.Fps.OnFirePressed(); Refs.Fps.OnFireReleased(); }, 0.1f);
            s.Add(("to VAL quick", () => Refs.Fps.StartWeaponChange(Slot(Val))));
            P("switch_back_fast", () => Refs.Fps.StartWeaponChange(Slot(Eagle)), 0.2f);
            P("final_plain", null, 0.5f);
            // 構えたまま（切り替え式で ON のまま）持ち替えて戻る → 次の右クリックで構えられるか
            s.Add(("aim on and keep", () => { Refs.Fps.OnAim(true); Main.Log($"[test] eagle kept aim: {Refs.Fps._aimState}"); })); s.Add(("wait", () => { }));
            s.Add(("to VAL while aiming", () => { Refs.Fps.StartWeaponChange(Slot(Val)); })); s.Add(("wait", () => Main.Log($"[test] eagle after switching away while aiming: {Refs.Fps._aimState}/{Refs.Fps._actionState}"))); s.Add(("wait", () => { }));
            s.Add(("back to eagle", () => { Refs.Fps.StartWeaponChange(Slot(Eagle)); })); s.Add(("wait", () => Main.Log($"[test] eagle back: {Refs.Fps._aimState}/{Refs.Fps._actionState}"))); s.Add(("wait", () => { }));
            P("after_switch_while_aiming", null, 0.1f);
            return s;
        }
        static void Clear(int id) { foreach (var c in Catalog.CatsFor(id)) Store.SetForTest(id, c, ""); }
        static void Set(int id, params (Cat, string)[] set)
        {
            Clear(id);
            foreach (var (c, a) in set) { Store.UnlockForTest(id, a); Store.SetForTest(id, c, a); }
            Tracker.ApplyAll();
        }

        static readonly Dictionary<string, (bool aimed, float fov0, float fov1, float tFov, float tCam)> _r = new();

        // 構えて 1.2 秒見る：エイムの状態・画面の拡大（FOV）・カメラの位置（体から見た）。90% まで進んだ時間
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        static bool Foreground() { GetWindowThreadProcessId(GetForegroundWindow(), out var pid); return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id; }
        static bool _real;   // 実際の右クリックで構える（ゲームの入力を通す）

        static IEnumerator Aim(string label)
        {
            var fps = Refs.Fps; var cam = ScreenCam(); var body = fps.transform;
            // 構えの姿勢は LateUpdate（KINEMATION）で骨に足されるので、フレームの終わりに測る
            yield return new WaitForEndOfFrame();
            float fov0 = cam.fieldOfView; var w = AimPt(); var p0 = Off(cam, w);
            var s0 = fps._aimState;
            if (!label.StartsWith("all_")) ScreenCapture.CaptureScreenshot(Path.Combine(Shots, $"aim_{label}_before.png"));
            var fovs = new List<(float t, float f, Vector3 p)>();
            if (_real)
            {
                SetForegroundWindow(System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle);
                yield return null;
                Main.Log($"[test] {label}: real right click (foreground {Foreground()}, toggle {fps.toggleAim})");
                mouse_event(0x08, 0, 0, 0, UIntPtr.Zero);   // 右ボタンを押す
                if (fps.toggleAim) { yield return null; mouse_event(0x10, 0, 0, 0, UIntPtr.Zero); }
            }
            else fps.OnAim(true);
            float t0 = Time.time;
            while (Time.time - t0 < 1.2f)
            {
                yield return new WaitForEndOfFrame();
                w = AimPt() ?? w; fovs.Add((Time.time - t0, cam.fieldOfView, Off(cam, w)));
            }
            bool aimed = fps._aimState == AimState.Aiming || (int)fps._aimState == 3;
            float fov1 = cam.fieldOfView; w = AimPt() ?? w; var p1 = Off(cam, w);
            float tF = -1f, tC = -1f;
            foreach (var (t, f, p) in fovs)
            {
                if (tF < 0f && Mathf.Abs(fov1 - fov0) > 0.5f && Mathf.Abs(f - fov0) >= 0.9f * Mathf.Abs(fov1 - fov0)) tF = t;
                if (tC < 0f && p0.magnitude - p1.magnitude > 0.005f && p0.magnitude - p.magnitude >= 0.75f * (p0.magnitude - p1.magnitude)) tC = t;   // 75%: skip the small settle at the end (~8mm, ~0.45s regardless of ergo)
            }
            if (label.StartsWith("ak12")) { var sb = new System.Text.StringBuilder(); float next = 0f; foreach (var (t, f, p) in fovs) if (t >= next) { sb.Append($"{t:0.00}:{p.magnitude:0.000} "); next += 0.04f; } Main.Log($"[test] curve {label}: {sb}"); }
            bool okNow = (fps._aimState == AimState.Aiming || (int)fps._aimState == 3);
            if (!label.StartsWith("all_") || !okNow) ScreenCapture.CaptureScreenshot(Path.Combine(Shots, $"aim_{label}.png"));   // 全部の組み合わせでは失敗したときだけ
            Main.Log($"[test] aim {label}: state {s0}->{fps._aimState} action={fps._actionState} fov {fov0:0.0}->{fov1:0.0} (90% {tF:0.000}s) gun centre off the screen centre {p0.magnitude:0.000}m -> {p1.magnitude:0.000}m (75% {tC:0.000}s) ergo {AdsSpeed.ErgoOf(Active()):0.#} aimingSpeed {AdsSpeed.SettingsOf(Active())?.aimingSpeed:0.###}");
            _r[label] = (aimed && s0 != AimState.Aiming && (int)s0 != 3, fov0, fov1, tF, tC);
            yield return null;
            if (_real)
            {
                if (fps.toggleAim) { mouse_event(0x08, 0, 0, 0, UIntPtr.Zero); yield return null; }
                mouse_event(0x10, 0, 0, 0, UIntPtr.Zero);
            }
            else fps.OnAim(fps.toggleAim);   // 切り替え式なら押し直し、押しっぱなし式なら離す
        }

        // 今の照準の点（サイトの覗く所）と、カメラの正面の線からのずれ（横・縦。m）
        static Transform AimPt()
        {
            var dw = Active(); var sg = dw?.scopeGroups; int si = dw != null ? dw._scopeIndex : -1;
            if (sg == null || si < 0 || si >= sg.Count || sg[si]?.attachments == null) return null;
            var at = sg[si].attachments;
            for (int k = 0; k < at.Count; k++) if (at[k] != null && at[k].gameObject.activeInHierarchy && at[k].aimPoint != null) return at[k].aimPoint;
            return at.Count > 0 ? at[0]?.aimPoint : null;
        }
        // 銃のいちばん大きな描画物の中心（カメラから見た横・縦。構えると画面の中心へ寄る）
        // 画面を描いているカメラ（描画先なし・有効・いちばん手前の深さ）
        static Camera ScreenCam()
        {
            Camera best = null; string all = "";
            foreach (var c in Camera.allCameras)
            {
                all += $"{c.name}(depth {c.depth}, tex {(c.targetTexture != null)}, fov {c.fieldOfView:0}) ";
                if (c.targetTexture == null && (best == null || c.depth > best.depth)) best = c;
            }
            if (!_camLogged) { _camLogged = true; Main.Log($"[test] cameras: {all}main={Camera.main?.name} use={best?.name}"); }
            return best ?? Camera.main;
        }
        static bool _camLogged;
        static Renderer _big;
        static Vector3 Off(Camera cam, Transform ap)
        {
            var dw = Active();
            if (_big == null || dw == null || !_big.transform.IsChildOf(dw.transform))
            {
                _big = null; float best = 0f;
                if (dw != null) foreach (var r in dw.GetComponentsInChildren<Renderer>(false)) { var sz = r.bounds.size.magnitude; if (r.enabled && sz > best && sz < 2f) { best = sz; _big = r; } }
            }
            if (_big == null) return Vector3.one;
            var p = cam.transform.InverseTransformPoint(_big.bounds.center); return new Vector3(p.x, p.y, 0f);
        }

        // 構えの動きを決めていそうな物を調べる：AimInClip などを持つ型と、手に持つ武器・プレイヤーのそれらの値
        // ---------- custadsprobe：構えの動き（腰だめ → 構え）を決めている物を調べる（AK-12） ----------
        static void DumpProfile()
        {
            var dw = Active();
            var prof = dw?.GetComponent<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorEntity>()?.animatorProfile;
            Main.Log($"[adsprobe] profile {prof?.name}");
            if (prof?.settings == null) return;
            foreach (var st in prof.settings)
            {
                if (st == null) continue;
                var il = st.GetIl2CppType();
                var t = Type.GetType(il.FullName.Replace("KINEMATION", "Il2CppKINEMATION") + ", Il2CppFPSAnimationFramework.Runtime") ?? Type.GetType(il.FullName + ", Assembly-CSharp");
                var vals = new List<string>();
                if (t != null)
                {
                    object cast = null;
                    try { cast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(t).Invoke(st, null); } catch { }
                    if (cast != null)
                        foreach (var pr in t.GetProperties())
                        {
                            if (pr.DeclaringType == typeof(UnityEngine.Object) || pr.DeclaringType == typeof(ScriptableObject) || pr.GetIndexParameters().Length > 0) continue;
                            if (pr.PropertyType != typeof(float) && pr.PropertyType != typeof(int) && pr.PropertyType != typeof(bool) && !pr.PropertyType.IsEnum && pr.PropertyType.Name != "Vector3" && !pr.PropertyType.Name.Contains("Spring")) continue;
                            try { vals.Add($"{pr.Name}={pr.GetValue(cast)}"); } catch { }
                        }
                }
                Main.Log($"[adsprobe] layer {st.name} ({il.FullName}): {string.Join(", ", vals)}");
            }
        }

        static float? _ik0, _sw0;
        static Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorProfile Prof() => Active()?.GetComponent<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorEntity>()?.animatorProfile;

        static List<(string, Action)> ProbeSteps()
        {
            var s = new List<(string, Action)>();
            const int Ak12 = 2;
            EffectTest.HoldForLab(s, Ak12);
            s.Add(("dump", () => { Set(Ak12); DumpProfile(); }));
            Func<int, float> keep = null;
            foreach (var (lab, e) in new[] { ("e0", 0f), ("e100", 100f), ("e160", 160f), ("em60", -60f) })
            {
                float ee = e;
                AimStep(s, "ak12_" + lab, () => { keep ??= AdsSpeed.ExtraErgo; AdsSpeed.ExtraErgo = id => ee - (Link.Recoil(Active())?.Ergo ?? 0f); AdsSpeed.Refresh(); });
            }
            // IK の動き（構えに入るときの揺れ）の再生速度と、構えの揺れのばねを変えたとき
            foreach (var (lab, rate, damp) in new[] { ("e100_play25", 2.5f, 1f), ("e100_damp25", 1f, 2.5f), ("e0_play25", 2.5f, 1f) })
            {
                float r = rate, d = damp; string l = lab;
                AimStep(s, "ak12_" + lab, () =>
                {
                    float e = l.StartsWith("e100") ? 100f : 0f;
                    AdsSpeed.ExtraErgo = id => e - (Link.Recoil(Active())?.Ergo ?? 0f); AdsSpeed.Refresh();
                    foreach (var st in Prof().settings)
                    {
                        var ik = st?.TryCast<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.IkMotionLayer.IkMotionLayerSettings>();
                        if (ik != null) { _ik0 ??= ik.playRate; ik.playRate = _ik0.Value * r; Main.Log($"[adsprobe] ik playRate {ik.playRate}"); }
                        var sw = st?.TryCast<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.SwayLayer.SwayLayerSettings>();
                        if (sw != null) { _sw0 ??= sw.aimSwayTargetDamping; sw.aimSwayTargetDamping = _sw0.Value * d; Main.Log($"[adsprobe] aimSwayTargetDamping {sw.aimSwayTargetDamping}"); }
                    }
                });
            }
            s.Add(("restore", () =>
            {
                AdsSpeed.ExtraErgo = keep; AdsSpeed.Refresh();
                foreach (var st in Prof().settings)
                {
                    var ik = st?.TryCast<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.IkMotionLayer.IkMotionLayerSettings>(); if (ik != null && _ik0 != null) ik.playRate = _ik0.Value;
                    var sw = st?.TryCast<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.SwayLayer.SwayLayerSettings>(); if (sw != null && _sw0 != null) sw.aimSwayTargetDamping = _sw0.Value;
                }
            }));
            return s;
        }

        static void DumpAim()
        {
            try
            {
                var names = new[] { "AimInClip", "AimOutClip", "AimingWeight", "adsScalar", "aimingEaseMode", "aimingSpeed", "aimPointSpeed", "cameraBlend", "aimFovZoom" };
                var types = new List<Type>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var an = asm.GetName().Name;
                    if (!an.StartsWith("Assembly-CSharp") && !an.StartsWith("Il2CppFPSAnimation") && !an.StartsWith("Il2CppKAnimation")) continue;
                    Type[] ts; try { ts = asm.GetTypes(); } catch { continue; }
                    foreach (var t in ts)
                        foreach (var n in names)
                            if (t.GetProperty(n) != null) { Main.Log($"[aimdump] type {t.FullName} has {n}"); if (!types.Contains(t)) types.Add(t); }
                }
                var objs = new List<UnityEngine.Object>();
                foreach (var c in Active().GetComponentsInChildren<Component>(true)) objs.Add(c);
                foreach (var c in Refs.Fps.GetComponentsInChildren<Component>(true)) objs.Add(c);
                var ads = AdsSpeed.SettingsOf(Active()); if (ads != null) objs.Add(ads);
                foreach (var o in objs)
                {
                    if (o == null) continue;
                    foreach (var t in types)
                    {
                        object cast;
                        try { cast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(t).Invoke(o, null); } catch { continue; }
                        if (cast == null) continue;
                        var vals = new List<string>();
                        foreach (var n in names) { var pr = t.GetProperty(n); if (pr == null) continue; object v; try { v = pr.GetValue(cast); } catch (Exception e) { v = "!" + e.Message; } if (v is AnimationClip ac) v = $"{ac.name} len {ac.length:0.000}"; vals.Add($"{n}={v}"); }
                        Main.Log($"[aimdump] {o.name} as {t.Name}: {string.Join(", ", vals)}");
                    }
                }
            }
            catch (Exception e) { Main.Log("[aimdump] failed " + e); }
        }

        static void AimStep(List<(string, Action)> s, string label, Action setup)
        {
            s.Add(($"set {label}", setup));
            s.Add(($"aim {label}", () => MelonLoader.MelonCoroutines.Start(Aim(label))));
            s.Add(($"check {label}", () =>
            {
                if (!_r.TryGetValue(label, out var r)) { Check($"{label}: measured", false); return; }
                Check($"{label}: aims (fov {r.fov0:0.0}->{r.fov1:0.0}, camera 90% {r.tCam:0.00}s)", r.aimed && (r.fov1 < r.fov0 - 0.5f || r.tCam >= 0f));
            }));
        }

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            const int Swiss = 17, Ak12 = 2, M16 = 10;

            // ---------- Swiss 751 ----------
            EffectTest.HoldForLab(s, Swiss);
            AimStep(s, "swiss_factory", () => Set(Swiss, (Cat.Sight, "swiss_scope")));
            AimStep(s, "swiss_prs", () => Set(Swiss, (Cat.Sight, "swiss_scope"), (Cat.Stock, "stock_prs")));
            AimStep(s, "swiss_mcx_holo", () => Set(Swiss, (Cat.Sight, "holo"), (Cat.Stock, "stock_mcx")));
            AimStep(s, "swiss_irons", () => Set(Swiss));
            AimStep(s, "swiss_all", () => Set(Swiss, (Cat.Sight, "swiss_scope"), (Cat.Stock, "stock_moe"), (Cat.Muzzle, "suppressor"), (Cat.SideRail, "laser"), (Cat.Mag, "ext_mag")));
            // 実際の右クリック（ホロ・ホロ＋ストック・スコープでホイールの照準に切り替えてからホロ・持ち替えてからホロ）
            s.Add(("real on", () => _real = true));
            AimStep(s, "swiss_holo_real", () => Set(Swiss, (Cat.Sight, "holo")));
            AimStep(s, "swiss_holo_prs_real", () => Set(Swiss, (Cat.Sight, "holo"), (Cat.Stock, "stock_prs")));
            AimStep(s, "swiss_scope_real", () => Set(Swiss, (Cat.Sight, "swiss_scope")));
            AimStep(s, "swiss_cycled_holo_real", () => { try { Active()?.OnCycleScope(); } catch (Exception e) { Main.Log("[test] cycle: " + e.Message); } Main.Log($"[test] scope index after cycle {Active()?._scopeIndex}"); Set(Swiss, (Cat.Sight, "holo")); Main.Log($"[test] scope index after holo {Active()?._scopeIndex}, groups {Active()?.scopeGroups?.Count}"); });
            EffectTest.HoldForLab(s, 2);
            s.Add(("swiss holo stored", () => { Set(Swiss, (Cat.Sight, "holo")); }));
            EffectTest.HoldForLab(s, Swiss);
            AimStep(s, "swiss_switched_holo_real", () => Main.Log($"[test] swiss after switch: scope index {Active()?._scopeIndex}"));
            s.Add(("real off", () => _real = false));
            s.Add(("swiss reset", () => Set(Swiss, (Cat.Sight, "swiss_scope"))));

            // ---------- エルゴ → 構える速さ（AK-12） ----------
            Func<int, float> keep = null;
            EffectTest.HoldForLab(s, Ak12);

            AimStep(s, "ak12_ergo0", () => { Set(Ak12); keep = AdsSpeed.ExtraErgo; AdsSpeed.ExtraErgo = id => -(Link.Recoil(Active())?.Ergo ?? 0f); AdsSpeed.Refresh(); });
            AimStep(s, "ak12_ergo50", () => { AdsSpeed.ExtraErgo = id => 50f - (Link.Recoil(Active())?.Ergo ?? 0f); AdsSpeed.Refresh(); });
            // 持ち替えてから（構えの層が設定を持ち替えのときに読むか）
            s.Add(("ergo -50", () => { AdsSpeed.ExtraErgo = id => -50f - (Link.Recoil(Active())?.Ergo ?? 0f); AdsSpeed.Refresh(); }));
            AimStep(s, "ak12_ergom50_held", () => { });
            EffectTest.HoldForLab(s, 10);
            EffectTest.HoldForLab(s, Ak12);
            AimStep(s, "ak12_ergom50_reequip", () => Main.Log($"[test] aimingSpeed now {AdsSpeed.SettingsOf(Active())?.aimingSpeed}"));
            s.Add(("ergo +50", () => { AdsSpeed.ExtraErgo = id => 50f - (Link.Recoil(Active())?.Ergo ?? 0f); AdsSpeed.Refresh(); }));
            EffectTest.HoldForLab(s, 10);
            EffectTest.HoldForLab(s, Ak12);
            AimStep(s, "ak12_ergo50_reequip", () => Main.Log($"[test] aimingSpeed now {AdsSpeed.SettingsOf(Active())?.aimingSpeed}"));
            s.Add(("ergo compare", () =>
            {
                AdsSpeed.ExtraErgo = keep; AdsSpeed.Refresh();
                var a = _r["ak12_ergo0"]; var b = _r["ak12_ergo50"];
                float want = 1f / StatScale.AdsMult(50f);
                Main.Log($"[test] ergo 0 vs 50: fov 90% {a.tFov:0.000}s -> {b.tFov:0.000}s, gun 75% {a.tCam:0.000}s -> {b.tCam:0.000}s (want x{want:0.00})");
                Check($"ergo +50 brings the gun up faster (75% {a.tCam:0.00}s -> {b.tCam:0.00}s)", a.tCam > 0f && b.tCam > 0f && b.tCam < a.tCam * 0.85f);
            }));

            // ---------- ストックの仕上げ・M16 の付け根 ----------
            EffectTest.HoldForLab(s, M16);
            s.Add(("m16 moe", () => Set(M16, (Cat.Stock, "stock_moe"))));
            s.Add(("m16 check", () =>
            {
                Main.Log($"[test] M16 finish polymer '{RifleStocks.LastFurnitureForTest}' subs {RifleStocks.LastTiledSubsForTest}, recolored {StockCut.LastRecoloredForTest}");
                Check($"M16: stock base recolored to metal ({StockCut.LastRecoloredForTest} triangles)", StockCut.LastRecoloredForTest > 0);
                Transform go = null; foreach (var t in Active().GetComponentsInChildren<Transform>(true)) if (t.name == Mount.Prefix + RifleStocks.Name) go = t;
                int all = 0, gun = 0;
                if (go != null) foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true)) foreach (var m in mr.sharedMaterials) if (m != null) { all++; if (m.name.StartsWith(Mount.Prefix + "tiled_")) gun++; }
                Check($"M16: every stock material is the gun's own ({gun}/{all})", all > 0 && gun == all);
                Check($"M16: stock polymer is the unified ACE-31 one ('{RifleStocks.LastFurnitureForTest}')", !RifleStocks.LastFurnitureForTest.Contains("body as polymer"));
                Set(M16);
            }));
            return s;
        }
    }
}
