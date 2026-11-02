using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // 2026-11-01 追加のアタッチメントの短い確認（autotest -Dev -Scenario custnew）：
    //   ・横からの画像：スタビー・KAC グリップ・バイポッド（比べる用にバーティカルグリップ）、アローヘッドブレーキ・フラッシュコーン・リニアコンペンセイター（比べる用にサプレッサー）
    //   ・手に持って：グリップの握る高さ、バイポッド・RK-2、レーザーの色（緑・最初から付いている武器の青）
    //   ・効果：連射速度（フラッシュコーン）、しゃがみ中の反動（バイポッド）、腕・脚の倍率（アローヘッドブレーキ）、リーンの手ぶれ（取り付けの確認）
    public static class NewPartsTest
    {
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

        static float _rate0;
        static float _scaleStand;

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
                Check("lean hand shake hook installed", Effects.TremorPatched);
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
                // KAC グリップ：リーン中は移動の揺れの倍率が 0、リーンをやめると元に戻る
                var before = Effects.MoveSwayScaleForTest(dw); float bob0 = Effects.AdsBobForTest(dw);
                Store.SetForTest(E, Cat.Grip, "kac_grip");
                Effects.LeanOverrideForTest = true; Effects.Tick(); var leanS = Effects.MoveSwayScaleForTest(dw); float bobL = Effects.AdsBobForTest(dw);
                Effects.LeanOverrideForTest = false; Effects.Tick(); var after = Effects.MoveSwayScaleForTest(dw); float bobA = Effects.AdsBobForTest(dw);
                Effects.LeanOverrideForTest = null;
                Check($"KAC grip: move sway off while leaning ({before} -> {leanS} -> {after})", before.sqrMagnitude > 0f && leanS == Vector3.zero && after == before);
                Check($"KAC grip: aim walk bob off while leaning ({bob0} -> {bobL} -> {bobA})", bob0 > 0f && bobL == 0f && bobA == bob0);
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
