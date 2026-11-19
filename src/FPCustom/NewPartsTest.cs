using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;
using Il2CppKINEMATION.FPSAnimationFramework.Runtime.Recoil;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // 2026-11-01 追加のアタッチメントの短い確認（autotest -Dev -Scenario custnew）：
    //   ・横からの画像：スタビー・KAC グリップ・バイポッド（比べる用にバーティカルグリップ）、アローヘッドブレーキ・フラッシュコーン・リニアコンペンセイター（比べる用にサプレッサー）
    //   ・手に持って：グリップの握る高さ、バイポッド・RK-2、レーザーの色（緑・最初から付いている武器の青）
    //   ・効果：連射速度（フラッシュコーン）、しゃがみ中の反動（バイポッド）、腕・脚の倍率（アローヘッドブレーキ）、リーンの手ぶれ（取り付けの確認）
    public static class NewPartsTest
    {
        // 持っている武器すべてにレーザー（FPRarity の perffight からも呼ぶ）
        public static void LaserAll(string rifle, string pistol)
        {
            for (int i = 0; i < 64; i++) if (Tracker.ItemById(i) is { } wi && Catalog.Eligible(wi)) Store.SetForTest(i, Cat.SideRail, Catalog.IsPistol(i) ? pistol : rifle);
            Tracker.ApplyAll();
        }

        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();
        static Il2Cpp.WeaponItem Item(int id) => Tracker.Inv.WeaponData.Weapons.ToArray().FirstOrDefault(w => w.Id == id);

        static readonly int[] Rifles = { 0, 2, 10, 11, 16, 28 };   // AK-74・AK-12・M16・AKSU・ACE-31（枠が合わない武器）・Viper
        static readonly int[] Pistols = { 1, 9, 13 };

        public static void Register()
        {
            TestMode.ExternalScenarios["custnew"] = Steps;
            // グリップの左手の位置（スタビー・KAC・バーティカル）とバイポッド：手に持って測る（HandLab の Left_Hand と vgrip の範囲）
            TestMode.ExternalScenarios["custgriphand"] = () =>
            {
                var s = new List<(string, Action)> { ("setup", () => Directory.CreateDirectory(ShotDir)) };
                foreach (var (id0, gs) in new[] { (0, new[] { "vertical_grip", "stubby_grip", "kac_grip" }), (28, new[] { "stubby_grip", "kac_grip" }), (10, new[] { "vertical_grip", "stubby_grip", "kac_grip" }),
                    (15, new[] { "vertical_grip", "stubby_grip", "kac_grip" }), (20, new[] { "vertical_grip", "stubby_grip", "kac_grip" }), (21, new[] { "vertical_grip", "stubby_grip", "kac_grip" }), (18, new[] { "bipod" }) })
                {
                    int id = id0;
                    foreach (var g0 in gs.Where(g => Catalog.For(id, Cat.Grip).Any(a => a.Id == g)))
                    {
                        string g = g0;
                        s.Add(($"set {id} {g}", () => { ResetAll(id); if (!Catalog.IsFactory(Catalog.Get(g), id)) Store.SetForTest(id, Cat.Grip, g); Hold(id); }));
                        s.Add(("wait", () => { }));
                        s.Add(($"equip {id}", () => Equip(id)));
                        s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                        s.Add(($"shot {id} {g}", () => HandLab.MeasureForTest(id, "gh_" + g, extra: false)));
                        s.Add(($"cleanup {id}", () => { ResetAll(id); Tracker.ApplyAll(); }));
                    }
                    s.Add(($"stash {id}", () => Refs.Bag.MoveWeaponToStash(Tracker.ItemById(id))));
                }
                return s;
            };
            // レーザーの先の点の中身（色を替えたあと、何が赤のまま残るか）
            TestMode.ExternalScenarios["custstockfx"] = StockFx;
            TestMode.ExternalScenarios["custdot"] = () =>
            {
                // 点（LaserPoint）はランの中にしかないので、持っている武器すべてに緑のレーザーを付けてエンドレスに出る
                var s = new List<(string, Action)>
                {
                    ("set", () =>
                    {
                        Directory.CreateDirectory(ShotDir);
                        for (int i = 0; i < 64; i++) if (Tracker.ItemById(i) is { } wi && Catalog.Eligible(wi)) Store.SetForTest(i, Cat.SideRail, Catalog.IsPistol(i) ? "p_laser_green" : "laser_green");
                        Tracker.ApplyAll();
                    }),
                    ("start endless run", () => Core.FindAll<Il2Cpp.ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()),
                };
                for (int i = 0; i < 16; i++) s.Add(("wait load", () => { }));
                s.Add(("dot", () => { DumpDot(Weapons.IdOf(Active()), "laser_green"); ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, "dot_run.png")); }));
                s.Add(("wait", () => { }));
                s.Add(("dot again", () =>
                {
                    DumpDot(Weapons.IdOf(Active()), "laser_green");
                    var lights = new List<Light>();
                    foreach (var p in UnityEngine.Object.FindObjectsOfType<Il2Cpp.LaserPoint>(true)) foreach (var l in p.GetComponentsInChildren<Light>(true)) lights.Add(l);
                    Check($"laser dot light is green in the run ({string.Join(" ", lights.Select(l => l.color.ToString()))})", lights.Count > 0 && lights.All(l => l.color.g > 0.5f && l.color.r < 0.3f));
                }));
                return s;
            };
            TestMode.ExternalScenarios["custpmuz"] = () => AllMuzzles(true);    // ハンドガン全部 × 銃口の部品（横から）
            TestMode.ExternalScenarios["custrmuz"] = () => AllMuzzles(false);   // メイン武器全部 × 銃口の部品（横から）
            // Type 89 にフラッシュコーンを付けて持つ（動くメッシュの差し替えの確認）
            TestMode.ExternalScenarios["custt89"] = () => new List<(string, Action)>
            {
                ("set", () => { Directory.CreateDirectory(ShotDir); ResetAll(20); Store.SetForTest(20, Cat.Muzzle, "flash_cone"); Hold(20); }), ("wait", () => { }),
                ("equip", () => Equip(20)), ("wait", () => { }), ("wait", () => { }),
                ("shot", () => { HandLab.MeasureForTest(20, "t89_cone", extra: false); Check("Type 89 flash cone held", Active() != null && Weapons.IdOf(Active()) == 20); }),
                ("cleanup", () => { ResetAll(20); Tracker.ApplyAll(); }),
            };
        }

        static List<(string, Action)> AllMuzzles(bool pistol)
        {
            var s = new List<(string, Action)> { ("setup", () => Directory.CreateDirectory(ShotDir)) };
            var ids = pistol ? new[] { "p_suppressor", "p_compensator", "p_arrowhead", "p_linear_comp", "p_flash_hider" } : new[] { "suppressor", "muzzle_brake", "arrowhead", "flash_cone", "flash_hider" };
            for (int id0 = 0; id0 < 64; id0++)
            {
                int id = id0; var w = Tracker.ItemById(id);
                if (w == null || !Catalog.Eligible(w) || Catalog.IsPistol(id) != pistol) continue;
                s.Add(($"muzzles {id}", () => { foreach (var g in ids) { var a = Catalog.Get(g); if (a != null && Catalog.For(id, Cat.Muzzle).Contains(a) && !Catalog.IsFactory(a, id)) PartsLab.Shot(w, a); } }));
            }
            return s;
        }

        // 追加のストックの効果（Model 92 を手に持って）：VP70＝M16 の 3 点バースト・ハンドガンの動きなし、ウッド＝しゃがみ中の反動減、ワイヤー＝ポイントエイムで強く・通常のエイムで弱く
        static int _ammo0;
        static bool _safe, _consume;
        static List<(string, Action)> StockFx()
        {
            const int P = 7;
            var s = new List<(string, Action)>();
            void W(int n = 1) { for (int i = 0; i < n; i++) s.Add(("wait", () => { })); }
            s.Add(("setup", () => { ResetAll(P); Hold(P); })); W();
            s.Add(("equip", () => Equip(P))); W(3);
            s.Add(("vp70", () => { Store.SetForTest(P, Cat.Stock, "p_poly_stock"); Tracker.ApplyAll(); })); W(2);
            s.Add(("vp70 check", () =>
            {
                var dw = Active();
                Main.Log("[test] burst " + Effects.BurstForTest(dw));
                var m16 = Tracker.ItemById(10)?.Weapon?.gameObject?.GetComponent<DemoWeapon>();
                Check($"VP70 stock: 3-round burst from the M16 ({Effects.BurstForTest(dw)})", dw != null && m16 != null && dw._fireMode == FireMode.Burst && dw.supportsBurst && dw.burstLength == (m16.burstLength > 0 ? m16.burstLength : 3) && Mathf.Abs(dw.fireRate - m16.fireRate) < 0.5f);
                Check("VP70 stock: pistol movement off (same as the M93R stock)", FPModOption.Link.MobileOverride?.Invoke() == false);
                var st = Effects.Stats(Catalog.Get("p_poly_stock"), P).ToList();
                Check($"VP70 stock: Control +3 only", st.Count == 1 && st[0].Stat == FPModOption.WeaponStat.Control && st[0].Value == 3f);
                var m = dw.SelectedMag; if (m != null) m.AmmoCount = 12; _ammo0 = m?.AmmoCount ?? 0;
                // 拠点では撃てない・弾が減らない。撃つ間だけ外す
                _safe = Refs.Fps.inSafeZone; _consume = Refs.Fps.consumeAmmo; Refs.Fps.inSafeZone = false; Refs.Fps.consumeAmmo = true;
                dw.OnFirePressed();
            }));
            s.Add(("release", () => Active()?.OnFireReleased())); W(1);
            s.Add(("burst fired", () =>
            {
                int fired = _ammo0 - (Active()?.SelectedMag?.AmmoCount ?? _ammo0);
                Refs.Fps.inSafeZone = _safe; Refs.Fps.consumeAmmo = _consume;
                Check($"VP70 stock: one trigger pull fires {fired} rounds", fired == Active().burstLength);
                Store.SetForTest(P, Cat.Stock, "p_wood_stock"); Tracker.ApplyAll(); Effects.CrouchOverrideForTest = false;
            })); W(2);
            s.Add(("wood stand", () =>
            {
                var dw = Active();
                Check($"wood stock: burst removed ({Effects.BurstForTest(dw)})", dw._fireMode != FireMode.Burst && !dw.supportsBurst);
                Check("wood stock: pistol movement back", FPModOption.Link.MobileOverride?.Invoke() == null);
                Effects.LiveUsedForTest(dw, P, out _scaleStand);
                Effects.CrouchOverrideForTest = true;
            })); W(2);
            s.Add(("wood crouch", () =>
            {
                bool used = Effects.LiveUsedForTest(Active(), P, out float crouch);
                Effects.CrouchOverrideForTest = null;
                Check($"wood stock: recoil lower while crouching ({_scaleStand:0.###} -> {crouch:0.###}, live {used})", used && crouch < _scaleStand * 0.75f);
                var st = Effects.Stats(Catalog.Get("p_wood_stock"), P).ToList();
                Check("wood stock: Control -5 only", st.Count == 1 && st[0].Stat == FPModOption.WeaponStat.Control && st[0].Value == -5f);
                Store.SetForTest(P, Cat.Stock, "p_frame_stock"); Tracker.ApplyAll();
            })); W(2);
            // ワイヤーストック：ポイントエイムでコントロール +20・エルゴ +25、通常のエイムでコントロール -15・エルゴ -20、腰だめは変わらない
            float[] scale = new float[3], ergo = new float[3]; bool[] live = new bool[3];
            foreach (var k in new[] { Effects.AimKind.Hip, Effects.AimKind.Normal, Effects.AimKind.Point })
            {
                s.Add(($"wire {k}", () => Effects.AimOverrideForTest = k)); W(1);
                s.Add(($"wire {k} read", () => { int i = (int)k; live[i] = Effects.LiveUsedForTest(Active(), P, out scale[i]); ergo[i] = FPModOption.AdsSpeed.ExtraErgo?.Invoke(P) ?? float.NaN; }));
            }
            s.Add(("wire lean", () =>
            {
                var dw = Active();
                Effects.AimOverrideForTest = null;
                Check($"wire stock: recoil live, point aim lower and normal aim higher than hip ({scale[2]:0.###} < {scale[0]:0.###} < {scale[1]:0.###})", live.All(x => x) && scale[2] < scale[0] && scale[0] < scale[1]);
                // 元の値（熟練度・ほかのアタッチメント）に足す。エルゴの元＝腰だめ（次に構えるのは通常の照準）の値 + 20
                float bc = Effects.LivePointsForTest(dw, P), be = ergo[0] + 20f;
                Check($"wire stock: control +20 point aim, -15 normal aim over base {bc} (point {scale[2]:0.###} = {Effects.ScaleForTest(bc + 20f):0.###}, normal {scale[1]:0.###} = {Effects.ScaleForTest(bc - 15f):0.###}, hip {scale[0]:0.###} = {Effects.ScaleForTest(bc):0.###})", Mathf.Abs(scale[2] - Effects.ScaleForTest(bc + 20f)) < 1e-3f && Mathf.Abs(scale[1] - Effects.ScaleForTest(bc - 15f)) < 1e-3f && Mathf.Abs(scale[0] - Effects.ScaleForTest(bc)) < 1e-3f);
                Check($"wire stock: ergonomics +25 point aim, -20 normal aim over base {be} (point {ergo[2]}, normal {ergo[1]})", ergo[2] == be + 25f && ergo[1] == be - 20f);
                _ergoBase = be;
                var before = Effects.MoveSwayScaleForTest(dw);
                Effects.LeanOverrideForTest = true; Effects.Tick(); var leanS = Effects.MoveSwayScaleForTest(dw);
                Effects.LeanOverrideForTest = null; Effects.Tick();
                Check($"wire stock: no lean effect any more ({before} -> {leanS})", leanS == before);
                var st = Effects.Stats(Catalog.Get("p_frame_stock"), P).ToList();
                Check("wire stock: no fixed stats", st.Count == 0);
                var notes = string.Join(" / ", Catalog.Get("p_poly_stock").Notes.Select(n => n.Ja)) + " | " + string.Join(" / ", Catalog.Get("p_frame_stock").Notes.Select(n => n.Ja)) + " | " + string.Join(" / ", Catalog.Get("p_wood_stock").Notes.Select(n => n.Ja));
                Check($"stock notes have no brackets ({notes})", !notes.Contains("(") && !notes.Contains("（"));
            }));
            // 本当のポイントエイム：構えて、ホイールのクリック（cycle scope）で 2 つ目の照準へ
            s.Add(("point aim", () => { Refs.Fps.OnAim(true); })); W(2);
            s.Add(("point cycle", () => { Active()?.OnCycleScope(); })); W(2);
            s.Add(("point check", () =>
            {
                Effects.Tick();
                Check($"wire stock: wheel click while aiming is point aim (scope {Active()?._scopeIndex}, {Effects.AimForTest}, ergo {FPModOption.AdsSpeed.ExtraErgo?.Invoke(P)})", Effects.AimForTest == Effects.AimKind.Point && FPModOption.AdsSpeed.ExtraErgo?.Invoke(P) == _ergoBase + 25f);
                Refs.Fps.OnAim(Refs.Fps.toggleAim);
            })); W(2);
            s.Add(("point after release", () =>
            {
                Main.Log($"[test] point aim after releasing aim: scope {Active()?._scopeIndex}, ergo {FPModOption.AdsSpeed.ExtraErgo?.Invoke(P)}");
                Refs.Fps.OnAim(true);
            })); W(2);
            s.Add(("point reaim", () =>
            {
                Effects.Tick();
                Main.Log($"[test] aim again after point aim: scope {Active()?._scopeIndex}, {Effects.AimForTest}");
                if (Active()?._scopeIndex != 0) Active()?.OnCycleScope();
            })); W(1);
            s.Add(("normal check", () =>
            {
                Effects.Tick();
                Check($"wire stock: back to normal aim (scope {Active()?._scopeIndex}, {Effects.AimForTest})", Effects.AimForTest == Effects.AimKind.Normal);
                Refs.Fps.OnAim(Refs.Fps.toggleAim);
            })); W(2);
            // ストックを外したら、構えの設定（カメラを寄せる・寄せる骨）がハンドガンの元の値に戻る
            s.Add(("stock on check", () => Check($"stocked pistol: camera goes to the sights (blend {Stance.CameraBlendForTest(Active())}, bone {Stance.AimBoneForTest})", Stance.CameraBlendForTest(Active()) == 1f && Stance.AimBoneForTest == "Camera")));
            s.Add(("stock off", () => { ResetAll(P); Tracker.ApplyAll(); })); W(2);
            s.Add(("stock off check", () => Check($"no stock: pistol aim settings restored (blend {Stance.CameraBlendForTest(Active())}, bone {Stance.AimBoneForTest})", Stance.CameraBlendForTest(Active()) == 0f && Stance.AimBoneForTest == "CameraSocket")));
            s.Add(("cleanup", () => { ResetAll(P); Tracker.ApplyAll(); Refs.Bag.MoveWeaponToStash(Tracker.ItemById(P)); }));
            return s;
        }

        static void ResetAll(int id) { foreach (var c in Catalog.CatsFor(id)) Store.SetForTest(id, c, null); }

        static void Hold(int id)
        {
            if (!Tracker.Held.Any(h => h.Roll.WeaponId == id))
            {
                while (Tracker.Held.Count >= 3) Refs.Bag.MoveWeaponToStash(Tracker.ItemById(Tracker.Held.First(h => h.Roll.WeaponId != id).Roll.WeaponId));
                Tracker.Inv.AddWeapon(Tracker.ItemById(id));
            }
            Tracker.ApplyAll();
        }

        static void Equip(int id)
        {
            var pn = Tracker.ItemById(id).Weapon.gameObject.name;
            var inst = Refs.Fps.InstantiatedWeapons;
            for (int i = 0; i < inst.Count; i++)
                if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == pn) { Refs.Fps.StartWeaponChange(i); return; }
        }

        static float _rate0, _ergoBase;
        static float _scaleStand;

        static void DumpDot(int id, string g)
        {
            static string Path(Transform t) { var p = t.name; while (t.parent != null) { t = t.parent; p = t.name + "/" + p; } return p; }
            void One(Transform t, string why)
            {
                if (t == null) { Main.Log($"[dot] {why}: null"); return; }
                Main.Log($"[dot] {why}: '{Path(t)}' active={t.gameObject.activeInHierarchy} comps=[{string.Join(",", t.GetComponents<Component>().Select(c => c.GetIl2CppType().Name))}]");
                foreach (var r in t.GetComponentsInChildren<Renderer>(true)) Main.Log($"[dot]   renderer '{r.name}' {r.GetIl2CppType().Name} on={r.enabled && r.gameObject.activeInHierarchy}: {LaserLab.Describe(r)}");
                foreach (var l in t.GetComponentsInChildren<Light>(true)) Main.Log($"[dot]   light '{l.name}' color={l.color} on={l.enabled}");
                foreach (var ps in t.GetComponentsInChildren<ParticleSystem>(true)) Main.Log($"[dot]   particles '{ps.name}' start={ps.main.startColor.color}");
                foreach (var d in t.GetComponentsInChildren<UnityEngine.Rendering.Universal.DecalProjector>(true)) Main.Log($"[dot]   decal '{d.name}' mat={d.material?.name}");
            }
            Main.Log($"[dot] weapon {id} {g} held={Weapons.IdOf(Active())}");
            foreach (var c in Active().GetComponentsInChildren<Il2Cpp.LaserPointController>(true)) One(c.laserPoint, "controller point");
            foreach (var p in UnityEngine.Object.FindObjectsOfType<Il2Cpp.LaserPoint>(true)) One(p.transform, "LaserPoint");
        }

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "new_*.png")) File.Delete(f); foreach (var f in Directory.GetFiles(ShotDir, "parts_*.png")) File.Delete(f); }));
            s.Add(("catalog", () =>
            {
                foreach (var id in new[] { "bipod", "kac_grip", "stubby_grip", "arrowhead", "flash_cone", "flash_hider", "p_arrowhead", "p_linear_comp", "p_flash_hider", "laser_blue", "laser_green", "p_laser_blue", "p_laser_green" })
                    Check($"catalog has {id}", Catalog.Get(id) != null);
                Check("red laser renamed", Catalog.Get("laser").Ja == "レーザーサイト(赤)" && Catalog.Get("p_laser").En == "Laser sight (red)");
                foreach (var id in Rifles.Concat(Pistols))
                    Main.Log($"[new] [{id}] {Tracker.NameOf(id)} grip=[{string.Join(",", Catalog.For(id, Cat.Grip).Select(a => a.Id))}] muzzle=[{string.Join(",", Catalog.For(id, Cat.Muzzle).Select(a => a.Id))}] rail=[{string.Join(",", Catalog.For(id, Cat.SideRail).Select(a => a.Id))}]");
                foreach (var id in Rifles)
                {
                    // スタビー・KAC はバーティカルグリップの枠がある武器だけ（AK-74 は無い）。バイポッドは左手より前にレールがある武器だけ（AKSU は無い）
                    var want = new List<string>();
                    if (Catalog.For(id, Cat.Grip).Any(a => a.Id == "vertical_grip")) want.AddRange(new[] { "stubby_grip", "kac_grip" });
                    if (Grips.BipodSpot(id) != null) want.Add("bipod");
                    Check($"{Tracker.NameOf(id)}: new grips offered ({string.Join(",", want)})", want.All(g => Catalog.For(id, Cat.Grip).Any(a => a.Id == g)));
                    Check($"{Tracker.NameOf(id)}: new muzzles offered", id == 14 || new[] { "arrowhead", "flash_cone", "flash_hider" }.All(g => Catalog.For(id, Cat.Muzzle).Any(a => a.Id == g)));
                    Check($"{Tracker.NameOf(id)}: 3 laser colors", Catalog.For(id, Cat.SideRail).Count == 3);
                }
                foreach (var id in Pistols)
                    Check($"{Tracker.NameOf(id)}: pistol muzzles and lasers", new[] { "p_arrowhead", "p_linear_comp", "p_flash_hider" }.All(g => Catalog.For(id, Cat.Muzzle).Any(a => a.Id == g)) && Catalog.For(id, Cat.SideRail).Count == 3);
                Check("lean hand shake hook installed (or not needed)", Effects.TremorPatched);
            }));
            // 横からの画像（銃だけ）。グリップ・バイポッドは付けられる武器すべて（バイポッドはレールの下面も測る：BIPODAT）
            for (int id0 = 0; id0 < 64; id0++)   // 持っていない武器も（所持の一覧は場面で変わる）
            {
                int id = id0; var w = Tracker.ItemById(id);
                if (w == null || !Catalog.Eligible(w) || Catalog.IsPistol(id)) continue;
                var parts = (Rifles.Contains(id) ? new[] { "vertical_grip", "angled_grip", "stubby_grip", "kac_grip", "bipod", "suppressor", "arrowhead", "flash_cone", "flash_hider" } : new[] { "vertical_grip", "angled_grip", "stubby_grip", "kac_grip", "bipod" })
                    .Select(Catalog.Get).Where(a => Catalog.For(id, a.Cat).Contains(a) && !Catalog.IsFactory(a, id)).ToList();
                Main.Log($"[new] side [{id}] {Tracker.NameOf(id)} grips=[{string.Join(",", Catalog.For(id, Cat.Grip).Select(a => a.Id))}]");
                if (parts.Count > 0) s.Add(($"side {id}", () => { foreach (var a in parts) PartsLab.Shot(w, a); }));
            }
            foreach (var id0 in Pistols)
            {
                int id = id0;
                s.Add(($"side {id}", () => { var w = Item(id); foreach (var g in new[] { "p_compensator", "p_arrowhead", "p_linear_comp", "p_flash_hider" }) { var a = Catalog.Get(g); if (Catalog.For(id, Cat.Muzzle).Contains(a)) PartsLab.Shot(w, a); } }));
            }

            // 手に持って：AK-74（緑のレーザー）・Viper（最初の赤のレーザーを青に）・Type 89（青のレーザー＋フラッシュコーン：動くメッシュの元のハイダーを除いた形）
            foreach (var (id0, laser) in new[] { (0, "laser_green"), (28, "laser_blue"), (20, "laser_blue") })
            {
                int id = id0; string lz = laser;
                foreach (var g0 in new[] { "kac_grip", "stubby_grip", "bipod" }.Where(g => Catalog.For(id, Cat.Grip).Any(a => a.Id == g)))
                {
                    string g = g0;
                    s.Add(($"set {id} {g}", () => { ResetAll(id); Store.SetForTest(id, Cat.Grip, g); Store.SetForTest(id, Cat.SideRail, lz); if (id == 20) Store.SetForTest(id, Cat.Muzzle, "flash_cone"); Hold(id); }));
                    s.Add(("wait", () => { }));
                    s.Add(($"equip {id}", () => Equip(id)));
                    s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                    s.Add(($"shot {id} {g}", () =>
                    {
                        HandLab.MeasureForTest(id, "new_" + g, extra: false);
                        var dw = Active();
                        bool held = dw != null && Weapons.IdOf(dw) == id;
                        var part = dw?.transform.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == Mount.Prefix + (g == "bipod" ? "bipod" : "vgrip"));
                        Check($"{Tracker.NameOf(id)} {g}: model on the gun", held && part != null && part.GetComponentsInChildren<Renderer>(true).Any(r => r.enabled));
                        if (dw == null) return;
                        // レーザーの色（材質の色の青・緑が赤より強い）
                        var beams = dw.GetComponentsInChildren<LineRenderer>(true).Where(l => l.gameObject.name == "Lazer").ToList();
                        foreach (var b in beams) Main.Log($"[new] [{id}] beam {LaserLab.Describe(b)}");
                        var col = beams.Select(b => LaserLab.MainColor(b)).FirstOrDefault(c => c != null);
                        bool ok = col is Color c && (lz == "laser_blue" ? c.b > c.r : c.g > c.r);
                        if (g == "kac_grip" || id == 0) Check($"{Tracker.NameOf(id)} {g}: {lz} beam color", beams.Count == 1 && ok);
                        // 持ち方：KAC はバーティカルグリップの枠、バイポッドはいつもの（「なし」の枠）
                        var slot = dw.gripAttachments.attachments[dw.gripAttachments._activeIndex];
                        var pose = slot?.attachmentLayerSettings;
                        string pn = pose != null && pose.Count > 0 ? pose[0]?.name : "-";
                        Main.Log($"[new] [{id}] {g}: slot {slot?.gameObject.name} pose {pn}");
                        if (g == "kac_grip") Check($"{Tracker.NameOf(id)}: KAC grip uses the vertical grip hold", slot != null && (slot.gameObject.name == Grips.VerticalName || pn.Contains("VerticalGrip")));
                        if (g == "bipod") Check($"{Tracker.NameOf(id)}: bipod keeps the normal hold", slot != null && (slot.gameObject.name.Contains("Empty") || slot.gameObject.name.Contains(Grips.EmptyName2)));
                    }));
                    s.Add(($"cleanup {id}", () => { ResetAll(id); Tracker.ApplyAll(); }));
                }
                s.Add(($"stash {id}", () => Refs.Bag.MoveWeaponToStash(Tracker.ItemById(id))));
            }

            // 効果（AK-74 を手に持って）
            const int E = 0;
            s.Add(("fx setup", () => { ResetAll(E); Hold(E); }));
            s.Add(("wait", () => { }));
            s.Add(("fx equip", () => Equip(E)));
            s.Add(("wait", () => { })); s.Add(("wait", () => { }));
            s.Add(("fx rate base", () => { _rate0 = Active()?.fireRate ?? 0f; Store.SetForTest(E, Cat.Muzzle, "flash_cone"); Tracker.ApplyAll(); }));
            s.Add(("wait", () => { }));
            s.Add(("fx rate flash cone", () =>
            {
                float r = Active()?.fireRate ?? 0f;
                Check($"flash cone: fire rate x{Effects.BoosterRate} ({_rate0:0} -> {r:0})", _rate0 > 0f && Mathf.Abs(r - _rate0 * Effects.BoosterRate) < 0.5f);
                int p = Enumerable.Range(0, 40).First(Catalog.IsPistol);
                Store.SetForTest(p, Cat.Muzzle, "p_linear_comp");
                float d = FPModOption.Link.DamageShown?.Invoke(p, 100f) ?? 0f, d30 = FPModOption.Link.DamageShown?.Invoke(p, 30f) ?? 0f, rp = FPModOption.Link.RpmShown?.Invoke(p, 600f) ?? 0f;
                Store.SetForTest(p, Cat.Muzzle, null);
                Check($"linear comp: damage +15 and no fire-rate bonus (100 -> {d:0.#}, 600 -> {rp:0})", Mathf.Abs(d - 115f) < 0.01f && Mathf.Abs(d30 - 45f) < 0.01f && Mathf.Abs(rp - 600f) < 0.01f && FPModOption.Link.DamageShown(p, 100f) == 100f);
                Store.SetForTest(E, Cat.Muzzle, "arrowhead");
                Store.SetForTest(E, Cat.Grip, "bipod");
                Tracker.ApplyAll();
                Effects.CrouchOverrideForTest = false;
            }));
            s.Add(("wait", () => { }));
            s.Add(("fx stand", () =>
            {
                var dw = Active();
                Check($"arrowhead brake: fire rate back to normal ({dw?.fireRate:0})", dw != null && Mathf.Abs(dw.fireRate - _rate0) < 0.5f);
                Effects.LiveUsedForTest(dw, E, out _scaleStand);
                Effects.CrouchOverrideForTest = true;
            }));
            s.Add(("wait", () => { }));
            s.Add(("fx crouch", () =>
            {
                var dw = Active();
                bool used = Effects.LiveUsedForTest(dw, E, out float crouch);
                Check($"bipod: recoil lower while crouching ({_scaleStand:0.###} -> {crouch:0.###}, live data used {used})", used && crouch < _scaleStand * 0.75f);
                Effects.CrouchOverrideForTest = null;
                // アローヘッドブレーキ：腕は胴体と同じ 1 倍（ゲームの腕の倍率 0.25 → 1 なので 4 倍をかける）、脚は LegMult 倍、胸はそのまま
                var bw = dw.GetComponentInChildren<Il2CppBallistics.Weapon>(true);
                var holder = new GameObject("FPC_TestEnemy");
                float M(string part, float dm = 1f) { var go = new GameObject(part); go.transform.SetParent(holder.transform, false); var c = go.AddComponent<Il2CppBallistics.LivingEntityCollider>(); c.DamageMultiplier = dm; return dm * Effects.PartMultForTest(c, bw); }
                float arm = M("lowerarm_l", 0.25f), hand = M("hand_r", 0.1f), leg = M("thigh_l"), chest = M("spine_02");
                UnityEngine.Object.Destroy(holder);
                // スタビーグリップ：止まっているとエルゴ -5、移動中は +15 足して +10
                var grip0 = Store.Equipped(E, Cat.Grip)?.Id;
                Store.SetForTest(E, Cat.Grip, "stubby_grip");
                Effects.MovingOverrideForTest = false; float still = AdsSpeed.ExtraErgo(E);
                Effects.MovingOverrideForTest = true; float moving = AdsSpeed.ExtraErgo(E);
                Effects.MovingOverrideForTest = null;
                // KAC グリップ：ワイヤーストックと同じ（ポイントエイムでエルゴ +25、通常のエイムで -20。リーンの効果は無い）
                var before = Effects.MoveSwayScaleForTest(dw);
                Store.SetForTest(E, Cat.Grip, "kac_grip");
                Effects.LeanOverrideForTest = true; Effects.Tick(); var leanS = Effects.MoveSwayScaleForTest(dw);
                Effects.LeanOverrideForTest = null; Effects.Tick();
                Effects.AimOverrideForTest = Effects.AimKind.Point; float kp = AdsSpeed.ExtraErgo(E);
                Effects.AimOverrideForTest = Effects.AimKind.Normal; float kn = AdsSpeed.ExtraErgo(E);
                Effects.AimOverrideForTest = null;
                var kst = Effects.Stats(Catalog.Get("kac_grip"), E).ToList(); var knotes = Effects.Notes(Catalog.Get("kac_grip"), E).Select(n => n.Ja).ToList();
                Check($"KAC grip: no lean effect any more ({before} -> {leanS})", leanS == before);
                Check($"KAC grip: same as the wire stock (point ergo {kp}, normal {kn}, stats {kst.Count}, notes {string.Join(" / ", knotes)})", kp - kn == 45f && kst.Count == 0 && knotes.SequenceEqual(Effects.Notes(Catalog.Get("p_frame_stock"), 7).Select(n => n.Ja)));
                Store.SetForTest(E, Cat.Grip, grip0);
                Check($"stubby grip: ergo still {still:0} moving {moving:0}", Mathf.Approximately(still, -5f) && Mathf.Approximately(moving, 10f));
                Check($"arrowhead brake: arm x{arm} hand x{hand} leg x{leg} chest x{chest}", Mathf.Approximately(arm, 1f) && Mathf.Approximately(hand, 1f) && leg == Effects.LegMult && chest == 1f);
            }));
            s.Add(("fx flash hider", () => { Store.SetForTest(E, Cat.Muzzle, "flash_hider"); Tracker.ApplyAll(); }));
            s.Add(("wait", () => { }));
            s.Add(("fx flash check", () =>
            {
                var dw = Active();
                Check($"flash hider: no muzzle flash ({dw?.fireParticlesEffect?.name})", dw?.fireParticlesEffect != null && dw.fireParticlesEffect.name == Mount.Prefix + "noflash");
                Store.SetForTest(E, Cat.Muzzle, null); Tracker.ApplyAll();
            }));
            s.Add(("wait", () => { }));
            s.Add(("fx flash back", () => { var dw = Active(); Check($"no flash hider: muzzle flash back ({dw?.fireParticlesEffect?.name})", dw?.fireParticlesEffect != null && !dw.fireParticlesEffect.name.StartsWith(Mount.Prefix)); }));
            s.Add(("fx cleanup", () => { ResetAll(E); Tracker.ApplyAll(); Refs.Bag.MoveWeaponToStash(Tracker.ItemById(E)); }));
            return s;
        }
    }
}
