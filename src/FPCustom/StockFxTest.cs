using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using MoveState = Il2CppDemo.Scripts.Runtime.Character.FPSMovementState;

namespace FPCustom
{
    // autotest -Dev -Scenario custrstockfx：メイン武器のストックの効果（拠点で AK-12 を持って）。
    //   PRS：止まっている間コントロール +10、移動速度 -10%
    //   SBA3：移動中エルゴ +15、コントロール -5
    //   MCX：ダッシュ中にリロードできて走り続ける（実際の W・Shift キー）、リロード速度 +15%、弾込め速度 -80%
    //   MOE：リロードの後 10 秒コントロール +5・エルゴ +7、リロード速度 -10%、弾込め速度 -80%
    public static class StockFxTest
    {
        const int Ak12 = 2;
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();
        static bool Near(float a, float b) => Mathf.Abs(a - b) < 0.002f * Mathf.Max(1f, Mathf.Abs(b));

        public static void Register() => TestMode.ExternalScenarios["custrstockfx"] = Steps;

        static void Set(string att) { Store.SetForTest(Ak12, Cat.Stock, att); Tracker.ApplyAll(); }

        static float LiveScale(DemoWeapon dw) => Effects.LiveScaleForTest(Link.Key(dw, Ak12));
        static float LivePts(DemoWeapon dw) => Effects.LivePointsForTest(dw, Ak12);

        static float _walkNone;

        static void PrepareReload(DemoWeapon w)
        {
            w.SelectedMag.AmmoCount = Math.Max(0, w.SelectedMag.AmmoCount - 5);   // 満タンだとリロードしない
            string name = Tracker.ItemById(Ak12).GetName();
            if (!Tracker.Inv.MagStored.ToArray().Any(m => m != null && m.WeaponName == name && m.AmmoCount > 0))
            {
                Tracker.Inv.AddMag(Ak12);
                foreach (var m in Tracker.Inv.MagStored) if (m != null && m.WeaponName == name) m.AmmoCount = m.MaxAmmoCount;
            }
        }

        // 弾込めのキー（AmmoButtonLoader.StartLoading）を止める前置きの差し替えが無い
        static bool QuickLoadFree()
        {
            var info = HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(typeof(Il2Cpp.AmmoButtonLoader), "StartLoading"));
            return info == null || info.Prefixes.Count == 0;
        }

        // インベントリの弾込め（MagUI）に Mod の差し替えが無い
        static bool InvLoadFree()
        {
            var info = HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(typeof(Il2Cpp.MagUI._LoadAmmoProgressively_d__21), "MoveNext"));
            return info == null || (info.Prefixes.Count == 0 && info.Postfixes.Count == 0);
        }

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            s.Add(("patches", () =>
            {
                Check("sprint reload patch installed", Effects.SprintReloadPatched);
                foreach (var c in Catalog.CatsFor(Ak12)) Store.SetForTest(Ak12, c, "");
            }));
            EffectTest.HoldForLab(s, Ak12);
            s.Add(("none", () =>
            {
                Set(null);
                var dw = Active();
                Check("AK-12 held", dw != null && Weapons.IdOf(dw) == Ak12);
                _walkNone = Refs.Fps._movementComponent.movementSettings.walking.velocity;
                Check("no stock: reload x1, move x1", Near(ReloadSpeed.ExtraMult(dw), 1f) && Near(Link.MoveSpeedMult(), 1f));
                Check("no stock: quick load not blocked", QuickLoadFree());
                Check("no stock: key loading normal", Near(Effects.ButtonLoadScaleForTest(Tracker.ItemById(Ak12).GetName()), 1f));
                try { Refs.Fps.AmmoButtonLoader?.CancelLoading(); } catch { }
            }));

            // PRS
            s.Add(("prs", () => { Set("stock_prs"); }));
            s.Add(("prs check", () =>
            {
                var dw = Active();
                Effects.StillOverrideForTest = true; Effects.Tick();
                float still = LiveScale(dw), pts = LivePts(dw);
                Effects.StillOverrideForTest = false; Effects.Tick();
                float moving = LiveScale(dw);
                Effects.StillOverrideForTest = null;
                Main.Log($"[test] PRS: points={pts} still={still:0.000} moving={moving:0.000}");
                Check($"PRS: control +10 standing still ({still:0.000} = {Effects.ScaleForTest(pts + 10f):0.000}, moving {moving:0.000})", Near(still, Effects.ScaleForTest(pts + 10f)) && Near(moving, Effects.ScaleForTest(pts)));
                float walk = Refs.Fps._movementComponent.movementSettings.walking.velocity;
                Check($"PRS: move speed -10% ({walk:0.000} vs {_walkNone:0.000}, mult {Link.MoveSpeedMult()}, pistolOn {Slide.PistolOn}, mobile {Slide.Mobile()})", Near(Link.MoveSpeedMult(), 0.9f) && Near(walk, _walkNone * 0.9f));
            }));

            // SBA3
            s.Add(("sba3", () => { Set("stock_sba3"); }));
            s.Add(("sba3 check", () =>
            {
                Effects.MovingOverrideForTest = true; float on = AdsSpeed.ExtraErgo(Ak12);
                Effects.MovingOverrideForTest = false; float off = AdsSpeed.ExtraErgo(Ak12);
                Effects.MovingOverrideForTest = null;
                Check($"SBA3: ergonomics +15 while moving ({on} vs {off})", Near(on - off, 15f));
                Check("SBA3: control -5", Catalog.Get("stock_sba3").Effects.Any(e => e.Item1 == WeaponStat.Control && e.Item2 == -5f));
                float walk = Refs.Fps._movementComponent.movementSettings.walking.velocity;
                Check($"SBA3: move speed back to normal ({walk:0.000})", Near(walk, _walkNone));
            }));

            // MCX
            s.Add(("mcx", () => { Set("stock_mcx"); }));
            s.Add(("mcx check", () =>
            {
                var dw = Active();
                Check($"MCX: reload speed +15% (x{ReloadSpeed.ExtraMult(dw):0.00})", Near(ReloadSpeed.ExtraMult(dw), 1.15f));
                try { Refs.Fps.OnAmmoQuickLoad(); } catch (Exception e) { Main.Log("[test] quick load: " + e.Message); }
                Main.Log($"[test] MCX: quick load started, loading {Refs.Fps.AmmoButtonLoader?.IsLoading}");
                Check("MCX: quick load key works (no mod blocks it)", QuickLoadFree());
                Check($"MCX: key loading -80% (time x{Effects.ButtonLoadScaleForTest(Tracker.ItemById(Ak12).GetName()):0.0})", Near(Effects.ButtonLoadScaleForTest(Tracker.ItemById(Ak12).GetName()), 5f));
                try { Refs.Fps.AmmoButtonLoader?.CancelLoading(); } catch { }
                Check("MCX: inventory loading left to the game", InvLoadFree());
                PrepareReload(dw);
                FaceOpen();
                _idle = GunInCam();
                for (int w = 0; w < 10 && !Foreground(); w++) BringToFront();
                Key(VK_W, true); Key(VK_LSHIFT, true);
            }));
            int reloads = 0;
            // 走っているだけのときの画面の揺れ（カメラの上下・左右の幅）
            s.Add(("mcx sprint only", () => MelonLoader.MelonCoroutines.Start(Watch("sprint", 1.2f, new[] { 0.8f }))));
            s.Add(("mcx sprint reload", () =>
            {
                var mv = Refs.Fps._movementComponent;
                bool sprinting = mv.MovementState == MoveState.Sprinting;
                Main.Log($"[test] MCX: before reload state={mv.MovementState} foreground={Foreground()}");
                reloads = Effects.SprintReloadsForTest; ReloadSpeed.StartedAt = ReloadSpeed.EndedAt = -1f;
                Refs.Fps.OnReload();
                Check($"MCX: sprinting before the reload ({mv.MovementState})", sprinting);
                Check($"MCX: reload started while sprinting (action {Refs.Fps._actionState})", Effects.SprintReloadsForTest == reloads + 1);
                MelonLoader.MelonCoroutines.Start(Watch("reload", 1.4f, new[] { 0.15f, 0.5f, 0.9f, 1.3f }));
            }));
            s.Add(("mcx sprint during", () =>
            {
                var mv = Refs.Fps._movementComponent;
                Main.Log($"[test] MCX: during reload state={mv.MovementState} action={Refs.Fps._actionState}");
                Check($"MCX: still sprinting during the reload ({mv.MovementState})", mv.MovementState == MoveState.Sprinting);
                Main.Log($"[test] MCX: camera sway sprint={_sway["sprint"]:0.0000} reload={_sway.GetValueOrDefault("reload"):0.0000}, hands moved {_hand:0.000}m");
                Check($"MCX: sprint camera sway kept while reloading ({_sway.GetValueOrDefault("reload"):0.0000} vs {_sway["sprint"]:0.0000})", _sway.GetValueOrDefault("reload") > _sway["sprint"] * 0.5f);
                // 走る構えでは銃が下がって横を向く。リロード中は構えを戻すので、銃の位置が止まっているときに近い
                float dSprint = (_gun["sprint"] - _idle).magnitude, dReload = (_gun["reload"] - _idle).magnitude;
                Main.Log($"[test] MCX: gun in camera idle={_idle:F3} sprint={_gun["sprint"]:F3} reload={_gun["reload"]:F3}");
                Check($"MCX: gun raised for the reload animation while sprinting (off idle {dReload:0.000}m, sprint pose {dSprint:0.000}m)", dReload < dSprint * 0.5f);
            }));
            s.Add(("mcx sprint end", () =>
            {
                Key(VK_LSHIFT, false); Key(VK_W, false);
                try { Refs.Fps._movementComponent.SetSprintRequested(false); } catch { }
            }));
            s.Add(("mcx reloaded", () =>
            {
                var dw = Active();
                // 拠点では弾が減らない（満タンのまま）ので、リロードの始まりと終わり（FPRarity のリロード速度の記録）で見る
                Main.Log($"[test] MCX: reload {ReloadSpeed.StartedAt:0.00} -> {ReloadSpeed.EndedAt:0.00} action={Refs.Fps._actionState}");
                Check($"MCX: reload finished ({ReloadSpeed.EndedAt - ReloadSpeed.StartedAt:0.00}s)", ReloadSpeed.StartedAt >= 0f && ReloadSpeed.EndedAt > ReloadSpeed.StartedAt && (int)Refs.Fps._actionState == 0);
            }));

            // MOE
            s.Add(("moe", () => { Set("stock_moe"); }));
            s.Add(("moe reload", () =>
            {
                var dw = Active();
                Check($"MOE: reload speed -10% (x{ReloadSpeed.ExtraMult(dw):0.00})", Near(ReloadSpeed.ExtraMult(dw), 0.9f));
                try { Refs.Fps.OnAmmoQuickLoad(); } catch (Exception e) { Main.Log("[test] quick load: " + e.Message); }
                Main.Log($"[test] MOE: quick load started, loading {Refs.Fps.AmmoButtonLoader?.IsLoading}");
                Check("MOE: quick load key works (no mod blocks it)", QuickLoadFree());
                Check($"MOE: key loading -80% (time x{Effects.ButtonLoadScaleForTest(Tracker.ItemById(Ak12).GetName()):0.0})", Near(Effects.ButtonLoadScaleForTest(Tracker.ItemById(Ak12).GetName()), 5f));
                try { Refs.Fps.AmmoButtonLoader?.CancelLoading(); } catch { }
                Check("MOE: inventory loading left to the game", InvLoadFree());
                Check($"MOE: no bonus before reloading ({Effects.MoeLeftForTest:0.0})", Effects.MoeLeftForTest <= 0f);
                PrepareReload(dw);
                Refs.Fps.OnReload();
            }));
            s.Add(("wait", () => { }));
            s.Add(("wait", () => { }));
            s.Add(("moe bonus", () =>
            {
                var dw = Active();
                Effects.Tick();
                float left = Effects.MoeLeftForTest, pts = LivePts(dw), sc = LiveScale(dw);
                Main.Log($"[test] MOE: left={left:0.0}s points={pts} scale={sc:0.000} action={Refs.Fps._actionState}");
                Check($"MOE: bonus running after the reload ({left:0.0}s left)", left > 0f && left <= 10f);
                Check($"MOE: control +5 ({sc:0.000} = {Effects.ScaleForTest(pts + 5f):0.000})", Near(sc, Effects.ScaleForTest(pts + 5f)));
                Effects.MovingOverrideForTest = false;
                Check($"MOE: ergonomics +7 ({AdsSpeed.ExtraErgo(Ak12)})", Near(AdsSpeed.ExtraErgo(Ak12), 7f));
                Effects.MovingOverrideForTest = null;
            }));
            for (int i = 0; i < 6; i++) s.Add(("wait", () => { }));
            s.Add(("moe over", () =>
            {
                var dw = Active();
                Effects.Tick();
                Effects.MovingOverrideForTest = false;
                Check($"MOE: bonus over after 10s ({Effects.MoeLeftForTest:0.0}, ergo {AdsSpeed.ExtraErgo(Ak12)})", Effects.MoeLeftForTest <= 0f && Near(AdsSpeed.ExtraErgo(Ak12), 0f) && Near(LiveScale(dw), Effects.ScaleForTest(LivePts(dw))));
                Effects.MovingOverrideForTest = null;
                Set(null);
            }));
            return s;
        }

        // 走っている間の画面の揺れ（プレイヤーから見たカメラの位置の上下・左右の幅）と、左手の動き（リロードのアニメーション）。shots/rstockfx_<label>_<n>.png
        static readonly Dictionary<string, float> _sway = new();
        static readonly Dictionary<string, Vector3> _gun = new();
        static Vector3 _idle;

        // 手に持っている武器の位置（カメラから見た）
        static Vector3 GunInCam() { var c = Camera.main; var w = Active(); return c == null || w == null ? Vector3.zero : c.transform.InverseTransformPoint(w.transform.position); }

        // いちばん開けた向きを向く（走っている間に壁に当たらないように）
        static void FaceOpen()
        {
            var body = Refs.Fps.transform; Vector3 best = body.forward; float far = -1f;
            for (int i = 0; i < 24; i++)
            {
                var d = Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward;
                float dist = Physics.Raycast(body.position + Vector3.up * 1.0f, d, out var hit, 50f) ? hit.distance : 50f;
                if (dist > far) { far = dist; best = d; }
            }
            body.rotation = Quaternion.LookRotation(best);
            Main.Log($"[test] facing {best:F2}, free {far:0.0}m");
        }
        static float _hand;
        static IEnumerator Watch(string label, float time, float[] shotsAt)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Main.Dir, "shots"));
            var cam = Camera.main; var body = Refs.Fps.transform;
            var hand = FindBone(Refs.Fps.transform, "hand_l") ?? FindBone(Refs.Fps.transform, "Left_Hand");
            Vector3 lo = Vector3.one * 9f, hi = -Vector3.one * 9f; float handMax = 0f; Vector3? hand0 = null; Vector3 gunSum = Vector3.zero; int gunN = 0;
            int shot = 0;
            for (float t = 0; t < time; t += Time.deltaTime)
            {
                if (cam != null)
                {
                    var p = body.InverseTransformPoint(cam.transform.position);
                    lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                    if (hand != null)
                    {
                        var hp = cam.transform.InverseTransformPoint(hand.position);
                        hand0 ??= hp;
                        handMax = Mathf.Max(handMax, (hp - hand0.Value).magnitude);
                    }
                }
                if (t > 0.35f && t < time - 0.2f) { gunSum += GunInCam(); gunN++; }   // 構えが変わり終わってから
                if (shot < shotsAt.Length && t >= shotsAt[shot])
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Main.Dir, "shots", $"rstockfx_{label}_{shot++}.png"));
                yield return null;
            }
            _sway[label] = Mathf.Max(hi.x - lo.x, hi.y - lo.y);
            _gun[label] = gunN > 0 ? gunSum / gunN : Vector3.zero;
            if (label == "reload") _hand = handMax;
            Main.Log($"[test] watch {label}: camera x {hi.x - lo.x:0.0000} y {hi.y - lo.y:0.0000} hand={hand?.name} moved {handMax:0.000}");
        }

        static Transform FindBone(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }

        // ---------- 実際のキー入力（ゲームのウィンドウが前面のときだけ） ----------
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        const byte VK_W = 0x57, VK_LSHIFT = 0xA0, VK_MENU = 0x12;

        static bool Foreground()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            return pid == (uint)Process.GetCurrentProcess().Id;
        }

        static void BringToFront()
        {
            var h = Process.GetCurrentProcess().MainWindowHandle;
            if (h == IntPtr.Zero) return;
            keybd_event(VK_MENU, (byte)MapVirtualKey(VK_MENU, 0), 0, UIntPtr.Zero);
            keybd_event(VK_MENU, (byte)MapVirtualKey(VK_MENU, 0), 2, UIntPtr.Zero);
            SetForegroundWindow(h);
        }

        static void Key(byte vk, bool down)
        {
            if (down && !Foreground()) return;
            keybd_event(vk, (byte)MapVirtualKey(vk, 0), down ? 0u : 0x0002u, UIntPtr.Zero);
        }
    }
}
