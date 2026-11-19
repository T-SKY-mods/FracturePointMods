using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using FPSMovementState = Il2CppDemo.Scripts.Runtime.Character.FPSMovementState;

using FPModOption;

namespace FPRarity
{
    // スライディングの調査と確認（autotest -Scenario slide）。エンドレスの階で、メイン武器とハンドガンそれぞれについて
    // 「走る → スライディング」を実際のキー・マウス入力で行い、毎フレームの状態（移動状態・姿勢・速さ・
    // 撃った弾数・エイム）を記録して、SlideTweaks の動き（ハンドガンの滑り・立ち上がり・エイム、弾の回避）を確かめる。
    public static class SlideProbe
    {
        public static List<(string, Action)> Steps()
        {
            var steps = new List<(string, Action)>
            {
                ("hold a rifle and a pistol", GiveLoadout),
                ("count shots", InstallShotCounter),
                ("start endless run", () => Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EndlessProbe.EnterAndMove()));
            for (int i = 0; i < 15; i++) steps.Add(("wait / pick card", EndlessProbe.PickCard));
            steps.Add(("move to an open spot", MoveToOpenSpot));
            foreach (bool pistol in new[] { false, true })
            {
                steps.Add(($"equip {(pistol ? "pistol" : "main weapon")}", () => Equip(pistol)));
                steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
                steps.Add(("fill the magazine", FillMag));
                foreach (var mode in new[] { "fire", "aim+fire" })
                {
                    string label = $"{(pistol ? "pistol" : "main")}/{mode}";
                    steps.Add(($"slide ({label})", () => MelonCoroutines.Start(Run(label, mode.StartsWith("aim")))));
                    for (int i = 0; i < 5; i++) steps.Add(("wait", () => { }));
                }
            }
            // ハンドガンの動きは武器カスタマイズmod。無い・オフなら、ハンドガンはゲームのまま（上の滑りで確かめる）
            if (!Slide.PistolOn) { steps.Add(("evade test", () => MelonCoroutines.Start(EvadeTest()))); for (int i = 0; i < 5; i++) steps.Add(("wait", () => { })); return steps; }
            steps.Add(("pistol: hip fire while sprinting", () => MelonCoroutines.Start(SprintFireTest(false))));
            for (int i = 0; i < 4; i++) steps.Add(("wait", () => { }));
            steps.Add(("pistol: aimed fire while sprinting", () => MelonCoroutines.Start(SprintFireTest(true))));
            for (int i = 0; i < 4; i++) steps.Add(("wait", () => { }));
            steps.Add(("aim, sprint and slide with the pistol", () => MelonCoroutines.Start(AimRunTest())));
            for (int i = 0; i < 6; i++) steps.Add(("wait", () => { }));
            foreach (bool sprintFirst in new[] { true, false })
            {
                bool sf = sprintFirst;
                steps.Add(($"pistol: {(sf ? "sprint, aim" : "aim, sprint")}, then stop aiming", () => MelonCoroutines.Start(AimReleaseTest(sf))));
                for (int i = 0; i < 5; i++) steps.Add(("wait", () => { }));
            }
            steps.Add(("evade test", () => MelonCoroutines.Start(EvadeTest())));
            for (int i = 0; i < 5; i++) steps.Add(("wait", () => { }));
            return steps;
        }

        static void Equip(bool pistol)
        {
            var fps = Refs.Fps;
            var list = fps?.InstantiatedWeapons;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var wi = list[i]?.TryCast<Il2CppDemo.Scripts.Runtime.Item.Weapon>()?.WeaponItem;
                Main.Log($"[slide]   hand {i}: '{list[i]?.gameObject.name}' item={(wi == null ? "-" : $"{wi.Id}/{wi.Type}")}");
            }
            // 手に持つ武器は名前で見分ける（武器のプレハブ名 = WeaponItem.Weapon の名前）
            string want = Tracker.ItemById(pistol ? PistolId : RifleId)?.Weapon?.gameObject.name;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null || list[i].gameObject.name.Replace("(Clone)", "").Trim() != want) continue;
                fps.StartWeaponChange(i);
                Main.Log($"[slide] equip index {i} '{list[i].gameObject.name}' pistol={pistol}");
                return;
            }
            Main.Log($"[slide] no {(pistol ? "pistol" : "main weapon")} in hand");
        }

        static IEnumerator Run(string label, bool aim)
        {
            var fps = Refs.Fps;
            var mv = fps?._movementComponent;
            bool pistol = label.StartsWith("pistol");
            _aimAfter = false;
            if (fps == null || mv == null) { Main.Log("[slide] no player"); yield break; }
            for (int w = 0; w < 50 && !GameIsForeground(); w++) { if (w % 10 == 0) BringToFront(); yield return new WaitForSeconds(0.2f); }
            if (!GameIsForeground()) { Check($"{label}: game window in the foreground", false); yield break; }

            Teleport(fps, _spot);
            fps.transform.rotation = Quaternion.LookRotation(_dir);
            yield return new WaitForSeconds(0.5f);
            Main.Log($"[slide] {label}: start at {fps.transform.position:F1} (spot {_spot:F1}) facing {_dir:F2}");
            Key(VK_W, true);
            mv.SetSprintRequested(true);
            yield return new WaitForSeconds(0.8f);
            var weapon = fps.GetActiveItem()?.TryCast<Il2CppDemo.Scripts.Runtime.Item.Weapon>();
            int ammo0 = Ammo(weapon);
            int shots0 = _shots, stood0 = Slide.StoodUp, blocked0 = Slide.AimBlocked;
            float runSpeed = Speed(mv);
            Main.Log($"[slide] {label}: before slide state={mv.MovementState} pose={mv.PoseState} speed={Speed(mv):0.00} weapon='{weapon?.gameObject.name}' ammo={ammo0}");

            if (aim) Mouse(RightDown);
            mv.OnSlide();
            Mouse(LeftDown);
            bool firing = true;
            float t0 = Time.time, slideEnd = -1f;
            Vector3 p0 = fps.transform.position, pEnd = p0;
            int shotsInSlide = -1, aimFrames = 0, slideFrames = 0;
            var lines = new List<string>();
            var back = fps.transform.position;
            float lastLog = -1f, standUp = -1f, roll = 0f;
            bool pendingAimCheck = false;
            bool shot = false;
            Vector3 lastPos = fps.transform.position; float lastT = 0f;
            while (Time.time - t0 < 3.0f)
            {
                yield return null;
                float t = Time.time - t0;
                bool sliding = mv.MovementState == FPSMovementState.Sliding;
                if (sliding) { slideFrames++; pEnd = fps.transform.position; if (IsAiming(fps)) aimFrames++; }
                if (!sliding && slideEnd < 0f && t > 0.05f)
                {
                    slideEnd = t;
                    shotsInSlide = _shots - shots0;
                    pendingAimCheck = true;
                    if (firing) { Mouse(LeftUp); firing = false; }
                }
                // 立ち上がって動き出したか（エイム中は走れないので歩き）
                if (slideEnd > 0f && standUp < 0f && mv.PoseState == Il2CppDemo.Scripts.Runtime.Character.FPSPoseState.Standing
                    && mv.MovementState == (aim && !pistol ? FPSMovementState.Walking : FPSMovementState.Sprinting))
                    standUp = t - slideEnd;
                if (pendingAimCheck && t > slideEnd + 0.3f) { pendingAimCheck = false; _aimAfter = IsAiming(fps); }
                if (!shot && t > 0.35f)
                {
                    shot = true;
                    roll = Mathf.Abs(Mathf.DeltaAngle(0f, Camera.main.transform.eulerAngles.z));
                    Main.Log($"[slide] {label}: camera roll mid-slide={roll:0.0}deg");
                    Main.Log($"[slide] {label}: barrel vs camera mid-slide={BarrelAngle(fps)}");
                    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Main.Dir, "shots"));
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Main.Dir, "shots", $"slide_{label.Replace('/', '_').Replace('+', '_')}.png"));
                }
                if (t - lastLog >= 0.1f)
                {
                    var pos = fps.transform.position;
                    float v = new Vector2(pos.x - lastPos.x, pos.z - lastPos.z).magnitude / Mathf.Max(0.001f, t - lastT);
                    lastPos = pos; lastT = t;
                    lastLog = t;
                    lines.Add($"t={t:0.00} state={mv.MovementState} pose={mv.PoseState} v={v:0.00} progress={mv._slideProgress:0.00} consume={mv._consumeMoveInput} action={fps._actionState} aim={fps._aimState} ammo={Ammo(weapon)}");
                }
            }
            if (firing) Mouse(LeftUp);
            if (aim) Mouse(RightUp);
            Key(VK_W, false);
            mv.SetSprintRequested(false);
            Teleport(fps, _spot);   // 次の試行も同じ広い場所から

            foreach (var l in lines) Main.Log($"[slide]   {l}");
            float dist = Vector3.Distance(new Vector3(p0.x, 0, p0.z), new Vector3(pEnd.x, 0, pEnd.z));
            Main.Log($"[slide] {label}: RESULT slide time={slideEnd:0.00}s frames={slideFrames} distance={dist:0.00}m shots during slide={shotsInSlide} aiming frames during slide={aimFrames}/{slideFrames} sprinting again after={(standUp < 0f ? "never" : standUp.ToString("0.00") + "s")}");

            Check($"{label}: shots fired while sliding ({shotsInSlide})", shotsInSlide > 0);
            if (aim && pistol && Slide.PistolOn) Check($"{label}: aiming during the whole slide ({aimFrames}/{slideFrames})", slideFrames > 0 && aimFrames == slideFrames);
            if (aim && !pistol && Slide.RolesOn)
            {
                Check($"{label}: rifle cannot aim while sliding (aiming frames {aimFrames}/{slideFrames})", slideFrames > 0 && aimFrames <= 2 && Slide.AimBlocked > blocked0);
                Check($"{label}: rifle aims again after the slide (aim button still held)", _aimAfter);
            }
            if (pistol && !Slide.PistolOn)
            {
                Check($"{label}: pistol slide unchanged without the customization mod (distance {dist:0.00}m, about 4m)", dist > 3.5f && dist < 4.6f);
                Check($"{label}: pistol does not stand up during the slide", Slide.StoodUp == stood0);
            }
            else if (!pistol)
            {
                Check($"{label}: rifle slide unchanged (distance {dist:0.00}m, about 4m)", dist > 3.5f && dist < 4.6f);
            }
            else
            {
                float wantRun = _rifleRunSpeed * Slide.PistolMoveSpeed;
                Check($"{label}: pistol run speed +5% ({runSpeed:0.00} vs rifle {_rifleRunSpeed:0.00})", Mathf.Abs(runSpeed - wantRun) < 0.1f);
                Check($"{label}: pistol slide about 5m (distance {dist:0.00}m)", dist > 4.7f && dist < 5.3f);
                Check($"{label}: pistol standing and sprinting{(aim ? " (still aiming)" : "")} right after the slide ({standUp:0.00}s)", standUp >= 0f && standUp < 0.3f);
                Check($"{label}: pistol stood up when the slide slowed to running speed", Slide.StoodUp == stood0 + 1);
            }
            if (!pistol && !aim) _rifleRunSpeed = runSpeed;
        }

        // ---------- ストック付きハンドガン：ダッシュ中にエイムして撃つ（2026-11-19 の報告：エイムが中途半端で止まる）----------
        const int StockPistolId = 22;   // J-941
        public static List<(string, Action)> StockSprintSteps()
        {
            var steps = new List<(string, Action)>
            {
                ("hold the J-941", () =>
                {
                    if (!Tracker.Held.Any(h => h.Roll.WeaponId == StockPistolId))
                    {
                        if (Tracker.Held.Count >= 3) TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(Tracker.Held.First().Roll.WeaponId));
                        TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(StockPistolId));
                    }
                }),
                ("count shots", InstallShotCounter),
                ("start endless run", () => Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()),
            };
            // 走れる広さのある所（エンドレスの最初の階）で行う
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EndlessProbe.EnterAndMove()));
            for (int i = 0; i < 15; i++) steps.Add(("wait / pick card", EndlessProbe.PickCard));
            steps.Add(("move to an open spot", MoveToOpenSpot));
            foreach (var stock in new[] { "p_stock", "" })
            {
                string st = stock;
                steps.Add(($"stock {(st == "" ? "none" : st)}", () => { SetCustom(StockPistolId, "Stock", st == "" ? null : st); Tracker.ApplyAll(); }));
                steps.Add(("equip J-941", () => EquipId(StockPistolId)));
                steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
                foreach (var delay in new[] { 0.05f, 0.15f, 0.4f })
                {
                    float d = delay;
                    string label = $"{(st == "" ? "no stock" : st)} fire {d:0.00}s after aim";
                    steps.Add(($"dash, aim, fire ({label})", () => MelonCoroutines.Start(StockSprintTest(label, d))));
                    for (int i = 0; i < 9; i++) steps.Add(("wait", () => { }));
                }
            }
            return steps;
        }

        static void SetCustom(int id, string cat, string att)
        {
            var store = Type.GetType("FPCustom.Store, FPCustom");
            var catT = Type.GetType("FPCustom.Cat, FPCustom");
            var m = store?.GetMethod("SetForTest", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (m == null || catT == null) { Main.Log("[stock] FPCustom not found"); return; }
            m.Invoke(null, new object[] { id, Enum.Parse(catT, cat), att });
            Main.Log($"[stock] {Tracker.NameOf(id)} {cat} = {att ?? "none"}");
        }

        static void EquipId(int id)
        {
            var fps = Refs.Fps; var list = fps?.InstantiatedWeapons;
            string want = Tracker.ItemById(id)?.Weapon?.gameObject.name;
            for (int i = 0; list != null && i < list.Count; i++)
                if (list[i] != null && list[i].gameObject.name.Replace("(Clone)", "").Trim() == want) { fps.StartWeaponChange(i); return; }
            Main.Log($"[stock] {want} not in hands");
        }

        // ---------- ダッシュ・エイム・射撃をほぼ同時に、撃ち続ける（どの武器でも、エイムが途中で止まるという報告）----------
        public static List<(string, Action)> SimulSteps()
        {
            var steps = new List<(string, Action)>
            {
                ("count shots", InstallShotCounter),
                ("start endless run", () => Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EndlessProbe.EnterAndMove()));
            for (int i = 0; i < 15; i++) steps.Add(("wait / pick card", EndlessProbe.PickCard));
            steps.Add(("move to an open spot", MoveToOpenSpot));
            steps.Add(("invincible", Invincible));
            foreach (var (id, stock) in new[] { (0, ""), (13, ""), (22, ""), (22, "p_stock") })
            {
                int wid = id; string st = stock;
                steps.Add(($"hold {wid}", () =>
                {
                    if (!Tracker.Held.Any(h => h.Roll.WeaponId == wid))
                    {
                        var big = Tracker.ItemById(wid).Type == WeaponItem.SlotType.Big;
                        var same = Tracker.Held.FirstOrDefault(h => (Tracker.ItemById(h.Roll.WeaponId).Type == WeaponItem.SlotType.Big) == big);
                        if (same != null && (big || Tracker.Held.Count >= 3)) TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(same.Roll.WeaponId));
                        TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(wid));
                    }
                    if (Catalog_IsPistol(wid)) SetCustom(wid, "Stock", st == "" ? null : st);
                    Tracker.ApplyAll();
                }));
                steps.Add(("wait", () => { }));
                steps.Add(($"equip {wid}", () => EquipId(wid)));
                steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
                string label = $"{Tracker.NameOf(wid)}{(st == "" ? "" : " + " + st)}";
                foreach (var mode in new[] { "dash, then aim+fire", "dash, then fire+aim", "dash+aim+fire" })
                {
                    string md = mode;
                    steps.Add(($"{md} ({label})", () => MelonCoroutines.Start(SimulTest($"{label} [{md}]", md))));
                    for (int i = 0; i < 12; i++) steps.Add(("wait", () => { }));
                }
            }
            return steps;
        }

        // どの仕組みが原因かを 1 回のテストで調べる：武器ごとに、仕組みを 1 つずつ止めて同じ操作をする
        public static List<(string, Action)> SimulBisectSteps()
        {
            var steps = new List<(string, Action)>();
            steps.Add(("count shots", InstallShotCounter));
            steps.Add(("start endless run", () => Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()));
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EndlessProbe.EnterAndMove()));
            for (int i = 0; i < 15; i++) steps.Add(("wait / pick card", EndlessProbe.PickCard));
            steps.Add(("move to an open spot", MoveToOpenSpot));
            steps.Add(("invincible", Invincible));
            foreach (var wid0 in new[] { 0, 22 })
            {
                int wid = wid0;
                steps.Add(($"hold {wid}", () => HoldForTest(wid, "")));
                steps.Add(("wait", () => { }));
                steps.Add(($"equip {wid}", () => EquipId(wid)));
                steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
                foreach (var cfg0 in new[] { "all on", "stance off", "pistol move off", "stance+pistol move off", "all on again" })
                {
                    string cfg = cfg0;
                    steps.Add(($"set {cfg}", () => SetToggles(cfg)));
                    steps.Add(($"{cfg} ({wid})", () => MelonCoroutines.Start(SimulTest($"{Tracker.NameOf(wid)} [{cfg}]", "dash, then aim+fire"))));
                    for (int i = 0; i < 10; i++) steps.Add(("wait", () => { }));
                }
                steps.Add(("restore toggles", () => SetToggles("all on")));
            }
            return steps;
        }

        static void Invincible()
        {
            var player = UnityEngine.Object.FindObjectsOfType<Il2CppBallistics.LivingEntity>().FirstOrDefault(e => e.PlayerHealthManager != null);
            if (player != null) player.PlayerHealthManager.invincible = true;
            Main.Log($"[simul] invincible={player != null}");
        }

        static void HoldForTest(int wid, string stock)
        {
            if (!Tracker.Held.Any(h => h.Roll.WeaponId == wid))
            {
                var big = Tracker.ItemById(wid).Type == WeaponItem.SlotType.Big;
                var same = Tracker.Held.FirstOrDefault(h => (Tracker.ItemById(h.Roll.WeaponId).Type == WeaponItem.SlotType.Big) == big);
                if (same != null && (big || Tracker.Held.Count >= 3)) TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(same.Roll.WeaponId));
                TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(wid));
            }
            if (Catalog_IsPistol(wid)) SetCustom(wid, "Stock", stock == "" ? null : stock);
            Tracker.ApplyAll();
        }

        static void SetToggles(string cfg)
        {
            bool stanceOff = cfg.Contains("stance"), pistolOff = cfg.Contains("pistol");
            try { Type.GetType("FPCustom.Stance, FPCustom")?.GetField("Mode")?.SetValue(null, stanceOff ? 0 : 3); } catch (Exception e) { Main.Log("[simul] stance toggle " + e.Message); }
            try { typeof(Slide).GetProperty("PistolOn")?.GetSetMethod(true)?.Invoke(null, new object[] { !pistolOff }); } catch (Exception e) { Main.Log("[simul] pistol toggle " + e.Message); }
            Main.Log($"[simul] toggles: {cfg} (stance mode {(stanceOff ? 0 : 3)}, pistol move {!pistolOff})");
        }

        // ---------- 壁に向かってダッシュ中にエイム・射撃 → 壁から離れたとき、エイムが元に戻るか（実際のプレイの報告）----------
        //   まとめて 1 回で：co-op の部品の重さの比べ（PerfCoop）→ Mod オンで壁のテスト → すべての Mod を止めて（co-op に入ったときと同じ）もう一度
        public static List<(string, Action)> WallSteps()
        {
            var steps = new List<(string, Action)>
            {
                ("count shots", InstallShotCounter),
                ("start endless run", () => Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EndlessProbe.EnterAndMove()));
            for (int i = 0; i < 15; i++) steps.Add(("wait / pick card", EndlessProbe.PickCard));
            steps.Add(("invincible", Invincible));
            steps.Add(("co-op parts fps", () => PerfCoop.Start()));
            for (int i = 0; i < 40; i++) steps.Add(("wait", () => { if (PerfCoop.Done) TestMode.SkipWaitsForTest(); }));
            steps.Add(("move to an open spot", MoveToOpenSpot));
            foreach (var modsOn in new[] { true, false })
            {
                bool on = modsOn;
                if (!on) steps.Add(("all mods off (same as entering co-op)", () => { CoopGuard.EnterForTest(); Main.Log("[wall] all mods off now"); }));
                foreach (var wid0 in new[] { 0, 22 })
                {
                    int wid = wid0;
                    steps.Add(($"hold {wid}", () => { if (on) HoldForTest(wid, ""); else HoldPlain(wid); }));
                    steps.Add(("wait", () => { }));
                    steps.Add(($"equip {wid}", () => EquipId(wid)));
                    steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
                    string label = $"{Tracker.NameOf(wid)} [mods {(on ? "on" : "off")}]";
                    steps.Add(($"wall test {label}", () => MelonCoroutines.Start(WallTest($"{Tracker.NameOf(wid)} [mods {(on ? "on" : "off")}]"))));
                    for (int i = 0; i < 10; i++) steps.Add(("wait", () => { }));
                }
            }
            return steps;
        }

        static void HoldPlain(int wid)
        {
            if (Tracker.Held.Any(h => h.Roll.WeaponId == wid)) return;
            Main.Log($"[wall] {wid} not held (mods off: not adding)");
        }

        static IEnumerator WallTest(string label)
        {
            var fps = Refs.Fps; var mv = fps?._movementComponent;
            if (mv == null) { Check($"{label}: player found", false); yield break; }
            for (int w = 0; w < 50 && !GameIsForeground(); w++) { if (w % 10 == 0) BringToFront(); yield return new WaitForSeconds(0.2f); }
            if (!GameIsForeground()) { Check($"{label}: game window in the foreground", false); yield break; }
            // 基準：開けた所で止まってエイム
            FillMag();
            Teleport(fps, _spot); fps.transform.rotation = Quaternion.LookRotation(_dir);
            yield return new WaitForSeconds(0.5f);
            Mouse(RightDown); yield return null; if (fps.toggleAim) Mouse(RightUp);
            yield return new WaitForSeconds(1.2f);
            var still = CamInWeapon(fps);
            if (fps.toggleAim) { Mouse(RightDown); yield return null; } Mouse(RightUp);
            yield return new WaitForSeconds(0.8f);
            // 壁の 4m 手前から、壁に向かってダッシュ → エイム → 撃ち続ける（壁に当たる）
            var wall = _spot + _dir * Mathf.Max(0f, _free - 4f);
            Teleport(fps, wall); fps.transform.rotation = Quaternion.LookRotation(_dir);
            yield return new WaitForSeconds(0.4f);
            int shots0 = _shots;
            Key(VK_W, true); Key(VK_LSHIFT, true);
            yield return new WaitForSeconds(0.5f);
            bool dashing = mv.MovementState == FPSMovementState.Sprinting;
            Mouse(RightDown); yield return null; if (fps.toggleAim) Mouse(RightUp);
            Mouse(LeftDown);
            bool semi = Refs.Fps.GetActiveItem()?.TryCast<Il2CppDemo.Scripts.Runtime.Item.Weapon>() is { } aw && !aw.supportsAuto;
            for (float t = 0; t < 1.8f; t += Time.deltaTime)
            {
                if (semi) { if (((int)(t / 0.12f)) % 2 == 1) Mouse(LeftUp); else Mouse(LeftDown); }
                yield return null;
            }
            float atWall = (CamInWeapon(fps) - still).magnitude;
            float distToWall = Vector3.Distance(fps.transform.position, wall) ;
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Main.Dir, "shots", $"wall_{System.Text.RegularExpressions.Regex.Replace(label, "[^A-Za-z0-9]+", "_")}_atwall.png"));
            var stateAtWall = $"move={mv.MovementState} aim={fps._aimState}";
            Mouse(LeftUp); Key(VK_LSHIFT, false); Key(VK_W, false); mv.SetSprintRequested(false);
            yield return new WaitForSeconds(0.3f);
            // 壁から離れて（開けた所へ戻って）エイムしたまま待つ
            Teleport(fps, _spot); fps.transform.rotation = Quaternion.LookRotation(_dir);
            float maxAway = 0f;
            for (float t = 0; t < 1.5f; t += Time.deltaTime) { if (t > 0.8f) maxAway = Mathf.Max(maxAway, (CamInWeapon(fps) - still).magnitude); yield return null; }
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Main.Dir, "shots", $"wall_{System.Text.RegularExpressions.Regex.Replace(label, "[^A-Za-z0-9]+", "_")}_away.png"));
            Main.Log($"[wall] {label}: dashing={dashing} shots={_shots - shots0} moved {distToWall:0.0}m | at the wall {stateAtWall} off={atWall:F3} | away from the wall aim={fps._aimState} off max={maxAway:F3}");
            Check($"{label}: aim back to the sight after leaving the wall (off {maxAway:F3}m, at the wall {atWall:F3}m)", IsAiming(fps) && maxAway < 0.01f);
            if (fps.toggleAim && IsAiming(fps)) { Mouse(RightDown); yield return null; } Mouse(RightUp);
            yield return new WaitForSeconds(0.5f);
        }

        static bool Catalog_IsPistol(int id) => Tracker.ItemById(id)?.Type == WeaponItem.SlotType.Small;

        static IEnumerator SimulTest(string label, string mode)
        {
            var fps = Refs.Fps; var mv = fps?._movementComponent;
            if (mv == null) { Check($"{label}: player found", false); yield break; }
            for (int w = 0; w < 50 && !GameIsForeground(); w++) { if (w % 10 == 0) BringToFront(); yield return new WaitForSeconds(0.2f); }
            if (!GameIsForeground()) { Check($"{label}: game window in the foreground", false); yield break; }
            // 1) 止まってエイムして撃ち続けたときのずれ（基準）
            FillMag();
            Teleport(fps, _spot); fps.transform.rotation = Quaternion.LookRotation(_dir);
            yield return new WaitForSeconds(0.5f);
            Mouse(RightDown); yield return null; if (fps.toggleAim) Mouse(RightUp);
            yield return new WaitForSeconds(1.2f);
            var still = CamInWeapon(fps);
            Mouse(LeftDown);
            float baseMax = 0f;
            for (float t = 0; t < 1.0f; t += Time.deltaTime) { if (t > 0.4f) baseMax = Mathf.Max(baseMax, (CamInWeapon(fps) - still).magnitude); yield return null; }
            Mouse(LeftUp);
            if (fps.toggleAim) { Mouse(RightDown); yield return null; } Mouse(RightUp);
            yield return new WaitForSeconds(1.0f);
            // 2) 止まった所から、走る・エイム・撃つをほぼ同時に（1 フレームずつ）押して、押し続ける
            FillMag();
            Teleport(fps, _spot); fps.transform.rotation = Quaternion.LookRotation(_dir);
            yield return new WaitForSeconds(0.4f);
            int shots0 = _shots;
            Key(VK_W, true); Key(VK_LSHIFT, true); yield return null;
            if (mode != "dash+aim+fire") yield return new WaitForSeconds(0.7f);   // 走っている最中に
            bool dashing = mv.MovementState == FPSMovementState.Sprinting;
            if (mode == "dash, then fire+aim") { Mouse(LeftDown); yield return null; }
            Mouse(RightDown); yield return null; if (fps.toggleAim) Mouse(RightUp);
            if (mode != "dash, then fire+aim") Mouse(LeftDown);
            bool pistolSemi = Refs.Fps.GetActiveItem()?.TryCast<Il2CppDemo.Scripts.Runtime.Item.Weapon>() is { } aw && !aw.supportsAuto;
            float maxOff = 0f, sum = 0f; int n = 0;
            for (float t = 0; t < 1.6f; t += Time.deltaTime)
            {
                if (pistolSemi && ((int)(t / 0.12f)) % 2 == 1) Mouse(LeftUp); else if (pistolSemi) Mouse(LeftDown);   // 単発の銃は連打
                if (t > 0.7f) { float o = (CamInWeapon(fps) - still).magnitude; maxOff = Mathf.Max(maxOff, o); sum += o; n++; }
                if (t > 1.2f && t - Time.deltaTime <= 1.2f) ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Main.Dir, "shots", $"simul_{System.Text.RegularExpressions.Regex.Replace(label, "[^A-Za-z0-9]+", "_")}.png"));
                yield return null;
            }
            float avg = n > 0 ? sum / n : -1f;
            var stateDuring = $"move={mv.MovementState} aim={fps._aimState} action={fps._actionState}";
            Mouse(LeftUp);
            Key(VK_LSHIFT, false); Key(VK_W, false); mv.SetSprintRequested(false);
            yield return new WaitForSeconds(1.0f);
            float after = (CamInWeapon(fps) - still).magnitude;
            Main.Log($"[simul] {label}: dashing at aim={dashing} shots={_shots - shots0} during {stateDuring} off avg={avg:F3} max={maxOff:F3} (standing fire max {baseMax:F3}) | 1s after release off={after:F3} aim={fps._aimState}");
            Check($"{label}: aim reaches the sight while dashing+aiming+firing (avg off {avg:F3}m, standing {baseMax:F3}m)", avg >= 0f && avg < baseMax + 0.02f);
            if (fps.toggleAim && (int)fps._aimState >= 2) { Mouse(RightDown); yield return null; } Mouse(RightUp);
            yield return new WaitForSeconds(0.5f);
        }

        // 構えたときのカメラの位置（武器から見た位置）。止まってエイムしたときと比べて、エイムが最後まで行ったかを見る
        static Vector3 CamInWeapon(Il2CppDemo.Scripts.Runtime.Character.FPSController fps)
        {
            var w = fps.GetActiveItem(); var cam = Camera.main;
            return w == null || cam == null ? Vector3.zero : w.transform.InverseTransformPoint(cam.transform.position);
        }

        static IEnumerator StockSprintTest(string label, float fireDelay)
        {
            var fps = Refs.Fps; var mv = fps?._movementComponent;
            if (mv == null) { Check($"{label}: player found", false); yield break; }
            for (int w = 0; w < 50 && !GameIsForeground(); w++) { if (w % 10 == 0) BringToFront(); yield return new WaitForSeconds(0.2f); }
            if (!GameIsForeground()) { Check($"{label}: game window in the foreground", false); yield break; }
            FillMag();
            // 1) 止まってエイムしたときの位置（基準）
            Teleport(fps, _spot); fps.transform.rotation = Quaternion.LookRotation(_dir);
            yield return new WaitForSeconds(0.5f);
            Mouse(RightDown); yield return null; if (fps.toggleAim) Mouse(RightUp);
            yield return new WaitForSeconds(1.2f);
            var still = CamInWeapon(fps);
            for (int i = 0; i < 3; i++) { Mouse(LeftDown); yield return new WaitForSeconds(0.08f); Mouse(LeftUp); yield return new WaitForSeconds(i < 2 ? 0.2f : 0.05f); }
            float stillShot = (CamInWeapon(fps) - still).magnitude;   // 止まって撃ったときの、撃った直後のずれ（反動の分）
            Main.Log($"[stock] {label}: still aim {fps._aimState} cam={still:F3} offRightAfterShots={stillShot:F3}");
            yield return new WaitForSeconds(1.2f);
            if (fps.toggleAim) { Mouse(RightDown); yield return null; } Mouse(RightUp);
            yield return new WaitForSeconds(0.8f);
            // 2) ダッシュ → エイム → 撃つ
            Teleport(fps, _spot); fps.transform.rotation = Quaternion.LookRotation(_dir);
            yield return new WaitForSeconds(0.3f);
            Key(VK_W, true); Key(VK_LSHIFT, true);
            yield return new WaitForSeconds(0.8f);
            bool sprinting = mv.MovementState == FPSMovementState.Sprinting;
            Mouse(RightDown); yield return null; if (fps.toggleAim) Mouse(RightUp);
            yield return new WaitForSeconds(fireDelay);
            int shots0 = _shots;
            float maxOff = 0f;
            for (int i = 0; i < 3; i++) { Mouse(LeftDown); yield return new WaitForSeconds(0.08f); Mouse(LeftUp); yield return new WaitForSeconds(i < 2 ? 0.2f : 0.05f); }
            float dashShot = (CamInWeapon(fps) - still).magnitude;   // 撃った直後のずれ
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Main.Dir, "shots", $"stock_sprint_shot_{label.Replace(' ', '_').Replace('.', '_')}.png"));
            for (float t = 0; t < 1.0f; t += Time.deltaTime) { maxOff = Mathf.Max(maxOff, (CamInWeapon(fps) - still).magnitude); yield return null; }
            Main.Log($"[stock] {label}: right after the shots off={dashShot:F3} (standing still {stillShot:F3}), max in the next 1s {maxOff:F3}");
            Check($"{label}: aim not stuck while firing (off {dashShot:F3}m vs {stillShot:F3}m standing)", dashShot < stillShot + 0.02f);
            var during = CamInWeapon(fps);
            float sprintAnim = mv._animator != null ? mv._animator.GetFloat(Il2CppDemo.Scripts.Runtime.Character.FPSMovement.Sprinting) : -1f;
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Main.Dir, "shots", $"stock_sprint_{label.Replace(' ', '_').Replace('.', '_')}.png"));
            Main.Log($"[stock] {label}: sprinting before aim={sprinting} shots={_shots - shots0} after: state={mv.MovementState} aim={fps._aimState} cam={during:F3} off={(during - still).magnitude:F3} sprintAnim={sprintAnim:0.00} toggleAim={fps.toggleAim}");
            Check($"{label}: aimed fully after dash+aim+fire (off {(during - still).magnitude:F3}m, aim {fps._aimState})", IsAiming(fps) && (during - still).magnitude < 0.01f);
            // 3) 止まってもエイムが戻るか
            Key(VK_LSHIFT, false); Key(VK_W, false); mv.SetSprintRequested(false);
            yield return new WaitForSeconds(1.2f);
            var after = CamInWeapon(fps);
            Main.Log($"[stock] {label}: stopped: state={mv.MovementState} aim={fps._aimState} off={(after - still).magnitude:F3}");
            Check($"{label}: aimed fully after stopping (off {(after - still).magnitude:F3}m)", IsAiming(fps) && (after - still).magnitude < 0.01f);
            if (fps.toggleAim) { Mouse(RightDown); yield return null; } Mouse(RightUp);
            yield return new WaitForSeconds(0.5f);
        }

        static float _rifleRunSpeed;
        static bool _aimAfter;
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");

        // ハンドガン: エイム → 走る（実際の Shift キー）→ スライディング → 走る、をエイムを解かずにできること
        // ハンドガン: Shift で走りながら左クリックで撃てること（腰だめ・エイム）。撃った後も走り続けること
        static IEnumerator SprintFireTest(bool aim)
        {
            string label = aim ? "aimed fire while sprinting" : "hip fire while sprinting";
            var fps = Refs.Fps; var mv = fps?._movementComponent;
            if (mv == null) { Check($"{label}: player found", false); yield break; }
            for (int w = 0; w < 50 && !GameIsForeground(); w++) { if (w % 10 == 0) BringToFront(); yield return new WaitForSeconds(0.2f); }
            if (!GameIsForeground()) { Check($"{label}: game window in the foreground", false); yield break; }
            FillMag();
            if (!aim && IsAiming(fps)) { Mouse(RightDown); yield return null; Mouse(RightUp); yield return new WaitForSeconds(0.3f); }   // 腰だめにする
            Teleport(fps, _spot);
            fps.transform.rotation = Quaternion.LookRotation(_dir);
            yield return new WaitForSeconds(0.5f);
            if (aim && !IsAiming(fps)) { Mouse(RightDown); yield return null; if (fps.toggleAim) Mouse(RightUp); }
            yield return new WaitForSeconds(0.3f);
            Key(VK_W, true);
            Key(VK_LSHIFT, true);
            yield return new WaitForSeconds(0.8f);
            bool sprinting = mv.MovementState == FPSMovementState.Sprinting;
            int shots0 = _shots;
            for (int i = 0; i < 3; i++)
            {
                Mouse(LeftDown);
                yield return new WaitForSeconds(0.08f);
                Mouse(LeftUp);
                yield return new WaitForSeconds(0.2f);
            }
            int shots = _shots - shots0;
            bool stillSprinting = mv.MovementState == FPSMovementState.Sprinting;
            float sprintWeight = mv._animator != null ? mv._animator.GetFloat(Il2CppDemo.Scripts.Runtime.Character.FPSMovement.Sprinting) : -1f;
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Main.Dir, "shots", $"slide_pistol_{(aim ? "aim" : "hip")}_fire_sprint.png"));
            if (!aim) Check($"{label}: not aiming ({fps._aimState})", !IsAiming(fps));
            Main.Log($"[slide] {label}: sprinting={sprinting} shots={shots} after: state={mv.MovementState} aim={fps._aimState} speed={Speed(mv):0.00} sprintAnim={sprintWeight:0.00}");
            Check($"{label}: sprinting before firing", sprinting);
            Check($"{label}: 3 clicks fired 3 shots ({shots})", shots == 3);
            Check($"{label}: still sprinting after firing ({mv.MovementState})", stillSprinting);
            if (aim) Check($"{label}: still aiming ({fps._aimState})", IsAiming(fps));
            Key(VK_LSHIFT, false);
            Key(VK_W, false);
            mv.SetSprintRequested(false);
            if (aim && IsAiming(fps)) { if (fps.toggleAim) { Mouse(RightDown); yield return null; } Mouse(RightUp); }
            yield return new WaitForSeconds(0.3f);
        }

        static IEnumerator AimRunTest()
        {
            var fps = Refs.Fps; var mv = fps?._movementComponent;
            if (mv == null) { Check("aim run: player found", false); yield break; }
            Check("aim run: holding the pistol", Slide.PistolActive());
            Teleport(fps, _spot);
            fps.transform.rotation = Quaternion.LookRotation(_dir);
            yield return new WaitForSeconds(0.5f);
            if (!IsAiming(fps)) { Mouse(RightDown); yield return null; Mouse(RightUp); }   // エイム（切り替え式でも押しっぱなし式でも、まず押す）
            if (!fps.toggleAim) Mouse(RightDown);
            yield return new WaitForSeconds(0.4f);
            bool aimedFirst = IsAiming(fps);
            int starts0 = Slide.AimRunStarts;
            Key(VK_W, true);
            Key(VK_LSHIFT, true);
            yield return new WaitForSeconds(0.8f);
            bool usedKey = mv.MovementState == FPSMovementState.Sprinting;
            if (!usedKey) { Main.Log("[slide] aim run: Shift did not start a sprint, requesting it directly"); mv.SetSprintRequested(true); yield return new WaitForSeconds(0.6f); }
            float sprintSpeed = Speed(mv);
            bool sprintAim = mv.MovementState == FPSMovementState.Sprinting && IsAiming(fps);
            Main.Log($"[slide] aim run: state={mv.MovementState} aim={fps._aimState} speed={sprintSpeed:0.00} shiftKey={usedKey}");
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Main.Dir, "shots", "slide_pistol_aim_sprint.png"));
            var sprintParam = mv._animator != null ? mv._animator.GetFloat(Il2CppDemo.Scripts.Runtime.Character.FPSMovement.Sprinting) : -1f;
            Check($"aim run: arms keep the aiming pose (sprint animation weight {sprintParam:0.00})", sprintParam >= 0f && sprintParam < 0.1f);
            Check($"aim run: aiming first ({aimedFirst})", aimedFirst);
            Check($"aim run: sprinting while aiming (state {mv.MovementState}, aim {fps._aimState}, {sprintSpeed:0.00}m/s)", sprintAim && sprintSpeed > 4.0f);
            Check("aim run: the game's sprint pose was not applied", Slide.AimRunStarts > starts0);

            mv.OnSlide();
            int frames = 0, aimFrames = 0;
            float t0 = Time.time;
            while (Time.time - t0 < 2.0f)
            {
                yield return null;
                if (mv.MovementState == FPSMovementState.Sliding) { frames++; if (IsAiming(fps)) aimFrames++; }
                else if (frames > 0) break;
            }
            yield return new WaitForSeconds(0.4f);
            bool after = mv.MovementState == FPSMovementState.Sprinting && IsAiming(fps);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Main.Dir, "shots", "slide_pistol_aim_sprint_after_slide.png"));
            Main.Log($"[slide] aim run: slide frames={frames} aiming={aimFrames}; after: state={mv.MovementState} aim={fps._aimState} speed={Speed(mv):0.00}");
            Check($"aim run: aiming during the slide ({aimFrames}/{frames})", frames > 0 && aimFrames == frames);
            Check($"aim run: sprinting and still aiming after the slide (state {mv.MovementState}, aim {fps._aimState})", after);

            Key(VK_LSHIFT, false);
            Key(VK_W, false);
            mv.SetSprintRequested(false);
            if (!fps.toggleAim) Mouse(RightUp);
            else if (IsAiming(fps)) { Mouse(RightDown); yield return null; Mouse(RightUp); }
        }

        // ハンドガンでダッシュしながらエイムし、エイムをやめてもダッシュが続くこと（Shift は押したまま、または切り替え式なら 1 回押す）
        static IEnumerator AimReleaseTest(bool sprintFirst)
        {
            var fps = Refs.Fps; var mv = fps?._movementComponent;
            if (mv == null) { Check("aim release: player found", false); yield break; }
            var proxy = UnityEngine.Object.FindObjectOfType<SprintProxy>();
            string tag = sprintFirst ? "sprint first" : "aim first";
            Teleport(fps, _spot);
            fps.transform.rotation = Quaternion.LookRotation(_dir);
            yield return new WaitForSeconds(0.5f);
            if (IsAiming(fps)) { Mouse(RightDown); yield return null; Mouse(RightUp); yield return new WaitForSeconds(0.3f); }
            string St() => $"state={mv.MovementState} aim={fps._aimState} req={mv._sprintRequested} toggle={(proxy != null && proxy._toggleSprint)} speed={Speed(mv):0.00}";
            IEnumerator AimPress() { Mouse(RightDown); yield return null; if (fps.toggleAim) Mouse(RightUp); }
            IEnumerator AimRelease() { if (fps.toggleAim) { Mouse(RightDown); yield return null; } Mouse(RightUp); }

            Key(VK_W, true);
            if (sprintFirst)
            {
                Key(VK_LSHIFT, true);
                yield return new WaitForSeconds(0.6f);
                yield return AimPress();
            }
            else
            {
                yield return AimPress();
                yield return new WaitForSeconds(0.3f);
                Key(VK_LSHIFT, true);
            }
            yield return new WaitForSeconds(0.7f);
            bool aimRun = mv.MovementState == FPSMovementState.Sprinting && IsAiming(fps);
            Main.Log($"[slide] aim release ({tag}): before release {St()}");
            yield return AimRelease();
            int frames = 0, sprintFrames = 0;
            float t0 = Time.time, topSpeed = 0f;
            while (Time.time - t0 < 0.8f)
            {
                yield return null;
                frames++;
                if (mv.MovementState == FPSMovementState.Sprinting) sprintFrames++;
                if (Time.time - t0 < 0.3f) topSpeed = Mathf.Max(topSpeed, Speed(mv));   // 離した直後の速さ（その後は物にぶつかって止まることがある）
                if (frames <= 3 || frames % 10 == 0) Main.Log($"[slide] aim release ({tag}): +{Time.time - t0:0.00}s {St()}");
            }
            float speed = Speed(mv);
            var sprintParam = mv._animator != null ? mv._animator.GetFloat(Il2CppDemo.Scripts.Runtime.Character.FPSMovement.Sprinting) : -1f;
            Main.Log($"[slide] aim release ({tag}): after {St()} sprint frames {sprintFrames}/{frames} anim {sprintParam:0.00}");
            Check($"aim release ({tag}): sprinting while aiming before release", aimRun);
            Check($"aim release ({tag}): still sprinting after aiming ends ({sprintFrames}/{frames}, {topSpeed:0.00}m/s just after, {speed:0.00}m/s at the end)", sprintFrames == frames && !IsAiming(fps) && topSpeed > 4.0f);
            Check($"aim release ({tag}): back to the normal sprint pose (anim {sprintParam:0.00})", sprintParam > 0.5f);
            Key(VK_LSHIFT, false);
            Key(VK_W, false);
            mv.SetSprintRequested(false);
            yield return new WaitForSeconds(0.3f);
        }

        // スライディング中だけ、弾のダメージが 70% の確率で無効になること（弾と同じ入口でごく小さいダメージを当てる）
        static IEnumerator EvadeTest()
        {
            var fps = Refs.Fps; var mv = fps?._movementComponent;
            var player = UnityEngine.Object.FindObjectsOfType<Il2CppBallistics.LivingEntity>().FirstOrDefault(e => e.PlayerHealthManager != null);
            if (mv == null || player == null) { Check("evade test: player found", false); yield break; }
            const int Hits = 300;
            int e0 = Slide.Evaded, r0 = Slide.Received;
            for (int i = 0; i < 50; i++) player.OnHealthChanged(-0.001f);
            Check($"not sliding: no damage is evaded ({Slide.Evaded - e0})", Slide.Evaded == e0 && Slide.Received == r0);

            Teleport(fps, _spot);
            fps.transform.rotation = Quaternion.LookRotation(_dir);
            yield return new WaitForSeconds(0.5f);
            Key(VK_W, true);
            mv.SetSprintRequested(true);
            yield return new WaitForSeconds(1.2f);
            mv.OnSlide();
            yield return null; yield return null;
            bool sliding = Slide.IsSliding;
            e0 = Slide.Evaded; r0 = Slide.Received;
            for (int i = 0; i < Hits; i++) player.OnHealthChanged(-0.001f);
            int got = Slide.Received - r0, evaded = Slide.Evaded - e0;
            Key(VK_W, false);
            mv.SetSprintRequested(false);
            float rate = got > 0 ? (float)evaded / got : 0f;
            Main.Log($"[slide] evade: sliding={sliding} hits={got} evaded={evaded} ({rate:P0})");
            // 回避は武器カスタマイズmod（無い・オフならゲームのまま＝回避しない）
            if (Slide.RolesOn) Check($"sliding: about 70% of bullet damage evaded ({evaded}/{got} = {rate:P0})", sliding && got == Hits && rate > 0.62f && rate < 0.78f);
            else Check($"sliding: nothing evaded without the customization mod ({evaded}/{got})", sliding && evaded == 0);
        }

        // ---------- 準備 ----------
        const int RifleId = 0, PistolId = 1;   // AK-74 / SP-229
        static int _shots;
        static Vector3 _spot, _dir = Vector3.forward;
        static float _free;

        static void GiveLoadout()
        {
            foreach (var id in new[] { RifleId, PistolId })
            {
                if (Tracker.Held.Any(h => h.Roll.WeaponId == id)) continue;
                if (Tracker.Held.Count >= 3)
                {
                    var other = Tracker.Held.First(h => h.Roll.WeaponId != RifleId && h.Roll.WeaponId != PistolId);
                    TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(other.Roll.WeaponId));
                }
                TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(id));
                Main.Log($"[slide] added {Tracker.NameOf(id)} to hands");
            }
        }

        static void InstallShotCounter()
        {
            var h = new HarmonyLib.Harmony("fprarity.slideprobe");
            bool ok = SafePatcher.Patch(h, typeof(FPSFireManager), "Fire", null, null, nameof(AfterFire), typeof(SlideProbe));
            Main.Log($"[slide] shot counter installed={ok}");
        }

        static void AfterFire() => _shots++;

        static string BarrelAngle(Il2CppDemo.Scripts.Runtime.Character.FPSController fps)
        {
            var sp = fps.GetActiveItem()?.gameObject.GetComponentInChildren<Il2CppBallistics.Weapon>(true)?.PhysicalBulletSpawnPoint;
            var cam = Camera.main;
            return sp == null || cam == null ? "-" : $"{Vector3.Angle(sp.forward, cam.transform.forward):0.0}deg";
        }

        static void FillMag()
        {
            var w = Refs.Fps?.GetActiveItem()?.TryCast<Il2CppDemo.Scripts.Runtime.Item.Weapon>();
            if (w?.SelectedMag == null) { Main.Log("[slide] no magazine selected"); return; }
            w.SelectedMag.AmmoCount = w.SelectedMag.MaxAmmoCount;
            Main.Log($"[slide] magazine filled: {w.SelectedMag.AmmoCount}");
        }

        // 歩ける場所（NavMesh）の中から、まっすぐ遠くまで障害物のない場所を探して立たせる
        static void MoveToOpenSpot()
        {
            var fps = Refs.Fps; if (fps == null) return;
            var origin = fps.transform.position;
            var elevator = Main.FindAll<LevelElevator>().FirstOrDefault();
            float best = -1f; Vector3 bestPos = origin, bestDir = Vector3.forward;
            var rng = new System.Random(1);
            for (int k = 0; k < 2000; k++)
            {
                var p = origin + new Vector3((float)rng.NextDouble() * 200f - 100f, 0f, (float)rng.NextDouble() * 200f - 100f);
                if (!UnityEngine.AI.NavMesh.SamplePosition(p, out var hit, 2f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                var feet = hit.position;
                if (Mathf.Abs(feet.y - origin.y) > 2f) continue;
                if (elevator != null && Vector3.Distance(feet, elevator.transform.position) < 6f) continue;   // エレベーターの中は除く
                if (Physics.CheckSphere(feet + Vector3.up * 0.6f, 0.35f) || Physics.CheckSphere(feet + Vector3.up * 1.3f, 0.35f)) continue;
                float free = FreeDistance(feet, out var dir);
                if (free > best) { best = free; bestPos = feet; bestDir = dir; }
            }
            _spot = bestPos;
            _dir = bestDir;
            _free = best;
            Teleport(fps, bestPos);
            Main.Log($"[slide] open spot {bestPos:F1}: free run {best:0.0}m towards {_dir:F2}");
            Check($"test floor has a straight open run of 9m or more ({best:0.0}m)", best >= 9f);
        }

        static float FreeDistance(Vector3 feet, out Vector3 dir)
        {
            float best = -1f; dir = Vector3.forward;
            for (int a = 0; a < 16; a++)
            {
                var d = Quaternion.Euler(0, a * 22.5f, 0) * Vector3.forward;
                float free = 25f;
                foreach (var hgt in new[] { 0.6f, 1.3f })
                    if (Physics.SphereCast(feet + Vector3.up * hgt, 0.35f, d, out var h, 25f)) free = Mathf.Min(free, h.distance);
                // 歩ける面がまっすぐ続いているか（NavMesh 上の直線）
                if (UnityEngine.AI.NavMesh.Raycast(feet, feet + d * free, out var navHit, UnityEngine.AI.NavMesh.AllAreas)) free = Mathf.Min(free, navHit.distance);
                // 段差や坂がない（床の高さが変わらない）ところまで
                for (float x = 1f; x < free; x += 1f)
                {
                    var q = feet + d * x;
                    if (!Physics.Raycast(q + Vector3.up * 1.0f, Vector3.down, out var floor, 2.5f) || Mathf.Abs(floor.point.y - feet.y) > 0.08f) { free = x - 1f; break; }
                }
                if (free > best) { best = free; dir = d; }
            }
            return best;
        }

        static void Teleport(Il2CppDemo.Scripts.Runtime.Character.FPSController fps, Vector3 pos)
        {
            var cc = fps.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            fps.transform.position = pos + Vector3.up * 0.1f;
            if (cc != null) cc.enabled = true;
        }

        static bool IsAiming(Il2CppDemo.Scripts.Runtime.Character.FPSController fps)
        {
            int s = (int)fps._aimState;
            return s == 2 || s == 3;
        }

        static float Speed(Il2CppDemo.Scripts.Runtime.Character.FPSMovement mv)
        {
            var cc = mv.GetComponent<CharacterController>();
            if (cc == null) return 0f;
            var v = cc.velocity; v.y = 0;
            return v.magnitude;
        }

        static int Ammo(Il2CppDemo.Scripts.Runtime.Item.Weapon w) => w?.SelectedMag != null ? w.SelectedMag.AmmoCount : -1;

        // 壁までの距離がいちばん長い向きを向く（走ってスライディングする余裕をつくる）
        static void FaceLongestFreeDirection(Il2CppDemo.Scripts.Runtime.Character.FPSController fps)
        {
            float best = FreeDistance(fps.transform.position, out var bestDir);
            fps.transform.rotation = Quaternion.LookRotation(bestDir);
            Main.Log($"[slide] facing {bestDir:F2}, free distance {best:0.0}m");
        }

        // ---------- 実際のキー・マウス入力（ゲームのウィンドウが前面のときだけ） ----------
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        const byte VK_W = 0x57, VK_LSHIFT = 0xA0;
        const uint LeftDown = 0x02, LeftUp = 0x04, RightDown = 0x08, RightUp = 0x10;

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        const byte VK_MENU = 0x12;

        // テストで起動したゲームのウィンドウを前面に出す。Windows は、ほかのアプリ（最後に操作されたもの）が前にあると
        // 後から起動したアプリを前面にしないので、Alt キーを一度押して離してから前面にする（よく使われる方法。Alt 単独ではゲームに影響なし）
        static void BringToFront()
        {
            var h = Process.GetCurrentProcess().MainWindowHandle;
            if (h == IntPtr.Zero) return;
            if (IsIconic(h)) ShowWindow(h, 9);   // SW_RESTORE
            keybd_event(VK_MENU, (byte)MapVirtualKey(VK_MENU, 0), 0, UIntPtr.Zero);
            keybd_event(VK_MENU, (byte)MapVirtualKey(VK_MENU, 0), 2, UIntPtr.Zero);
            bool ok = SetForegroundWindow(h);
            BringWindowToTop(h);
            Main.Log($"[slide] bring the game window to the front: {ok} foreground={GameIsForeground()}");
        }

        static bool GameIsForeground()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            return pid == (uint)Process.GetCurrentProcess().Id;
        }

        static void Key(byte vk, bool down)
        {
            if (down && !GameIsForeground()) return;
            keybd_event(vk, (byte)MapVirtualKey(vk, 0), down ? 0u : 0x0002u, UIntPtr.Zero);
        }

        static void Mouse(uint flags)
        {
            bool release = flags == LeftUp || flags == RightUp;
            if (!release && !GameIsForeground()) return;
            mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
        }
    }
}
