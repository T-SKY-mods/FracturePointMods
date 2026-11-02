using System;
using System.Collections.Generic;
using System.Linq;
using FPModOption;
using Il2CppKINEMATION.FPSAnimationFramework.Runtime.Recoil;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // autotest -Dev -Scenario custfx：アタッチメントの効果が武器と動きに当たるかを、拠点で 4 丁を持ち替えて確かめる。
    // セーブ・custom.json は autotest.ps1 が前後で退避・復元する
    public static class EffectTest
    {
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();
        static DemoWeapon Prefab(int id) => Tracker.ItemById(id).Weapon.gameObject.GetComponent<DemoWeapon>();
        static float Scale(float pts) => StatScale.RecoilMult(pts);   // 反動の倍率（Effects と同じ）
        static bool Near(float a, float b) => Mathf.Abs(a - b) < 0.002f * Mathf.Max(1f, Mathf.Abs(b));

        public static void Register() => TestMode.ExternalScenarios["custfx"] = Steps;

        const int Akms = 4, Ak74 = 0, G18 = 13;

        // 武器を持つ（MountTest と同じ：手持ちがいっぱいなら 1 丁を保管庫へ）
        static void Hold(List<(string, Action)> s, int id)
        {
            s.Add(($"add {id}", () =>
            {
                if (Tracker.Held.Any(h => h.Roll.WeaponId == id)) { Tracker.ApplyAll(); return; }
                var small = Tracker.ItemById(id).Type == Il2Cpp.WeaponItem.SlotType.Small;
                var same = Tracker.Held.Where(h => h.Roll.WeaponId != id && (Tracker.ItemById(h.Roll.WeaponId).Type == Il2Cpp.WeaponItem.SlotType.Small) == small).ToList();
                if (Tracker.Held.Count >= 3 || (small && same.Count > 0)) Refs.Bag.MoveWeaponToStash(Tracker.ItemById((same.Count > 0 ? same[0] : Tracker.Held[0]).Roll.WeaponId));
                Main.Log($"[test] AddWeapon {Tracker.NameOf(id)} -> {Tracker.Inv.AddWeapon(Tracker.ItemById(id))}");
            }));
            s.Add(("wait", () => { }));
            s.Add(($"equip {id}", () =>
            {
                var pn = Tracker.ItemById(id).Weapon.gameObject.name;
                var inst = Refs.Fps.InstantiatedWeapons;
                for (int i = 0; i < inst.Count; i++)
                    if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == pn) { Refs.Fps.StartWeaponChange(i); return; }
                Main.Log($"[test] {Tracker.NameOf(id)} not in hands");
            }));
            s.Add(("wait", () => { }));
        }

        public static void HoldForLab(List<(string, Action)> s, int id) => Hold(s, id);

        static bool Holding(int id, out DemoWeapon dw)
        {
            dw = Active();
            bool ok = dw != null && Weapons.IdOf(dw) == id;
            Check($"{Tracker.NameOf(id)} held", ok);
            return ok;
        }

        // レアリティ・熟練度の反動の補正（レアリティmod が無ければ 0）
        static float CtlBase(DemoWeapon dw) => Link.Recoil(dw)?.Control ?? 0f;
        static float C(string att) => Catalog.Get(att).Effects.Where(e => e.Stat == WeaponStat.Control).Sum(e => e.Value);   // アタッチメントのコントロール
        static float ErgBase(DemoWeapon dw) => Link.Recoil(dw)?.Ergo ?? 0f;

        static int _base, _slot;
        // 敵に見立てた頭（55 倍）と胸（spine_02、1.8 倍）。スイスのスコープを付けていない武器では胸はそのまま
        static Il2CppBallistics.LivingEntityCollider _chest;
        static float ChestDuring(DemoWeapon dw)
        {
            if (_chest == null)
            {
                var root = new GameObject("FPC_test_enemy");
                var head = new GameObject("head"); head.transform.SetParent(root.transform, false);
                head.AddComponent<Il2CppBallistics.LivingEntityCollider>().DamageMultiplier = 55f;
                var go = new GameObject("spine_02"); go.transform.SetParent(root.transform, false);
                _chest = go.AddComponent<Il2CppBallistics.LivingEntityCollider>();
            }
            _chest.DamageMultiplier = 1.8f;
            var bw = dw.GetComponentInChildren<Il2CppBallistics.Weapon>(true);
            if (bw == null) return -1f;
            Effects.BulletHitForTest(bw, _chest, out float during);
            return during;
        }

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () =>
            {
                Store.SetForTest(Akms, Cat.Muzzle, "suppressor"); Store.SetForTest(Akms, Cat.Grip, "vertical_grip"); Store.SetForTest(Akms, Cat.Mag, "ext_mag");
                Store.SetForTest(Ak74, Cat.Muzzle, "muzzle_brake"); Store.SetForTest(Ak74, Cat.Grip, "angled_grip"); Store.SetForTest(Ak74, Cat.Mag, "fast_mag");
                Store.SetForTest(G18, Cat.Stock, "p_stock"); Store.SetForTest(G18, Cat.Muzzle, "p_compensator");
                Check("every attachment has a plus and a minus effect", Catalog.All.Where(a => a.Cat != Cat.Sight && a.Cat != Cat.SideRail)
                      .All(a => (a.Effects.Any(e => e.Value > 0) || a.Notes.Any(n => n.Plus)) && (a.Effects.Any(e => e.Value < 0) || a.Notes.Any(n => !n.Plus))));
            }));

            // AKMS：サプレッサー＋バーティカルグリップ＋拡張マガジン
            Hold(s, Akms);
            s.Add(("check AKMS", (Action)(() =>
            {
                if (!Holding(Akms, out var dw)) return;
                var p = Prefab(Akms);
                Check($"suppressor: no muzzle flash ({dw.fireParticlesEffect?.name})", dw.fireParticlesEffect != null && dw.fireParticlesEffect.name == Mount.Prefix + "noflash" && !dw.fireParticlesEffect.activeSelf);
                float alert = p.customAlertDistance != 0f ? p.customAlertDistance : Effects.DefaultAlert;
                Check($"suppressor: alert range halved ({dw.customAlertDistance:0.##}, was {alert:0.##})", Near(dw.customAlertDistance, alert * 0.5f));
                // エルゴは構える速さ（FPModOption.AdsSpeed）
                Check($"suppressor: ergo -3 (aim speed x{AdsSpeed.Mult(AdsSpeed.ErgoOf(dw)):0.00})", Near(AdsSpeed.ErgoOf(dw), ErgBase(dw) - 3f) && Near(AdsSpeed.Mult(AdsSpeed.ErgoOf(dw)), AdsSpeed.Mult(ErgBase(dw) - 3f)));
                Check($"vertical grip {C("vertical_grip")} + extended mag +{C("ext_mag")}", Near(dw.recoilPatternSettings.verticalRecoil.y, p.recoilPatternSettings.verticalRecoil.y * Scale(CtlBase(dw) + C("vertical_grip") + C("ext_mag"))));
                Effects.ShotsOverrideForTest = 10;
            })));
            s.Add(("vertical grip: 10 shots", () =>
            {
                var dw = Active(); var p = Prefab(Akms);
                Check($"vertical grip: recoil -2% per shot (10 shots -> {dw.recoilPatternSettings.verticalRecoil.y / p.recoilPatternSettings.verticalRecoil.y:0.000})",
                      Near(dw.recoilPatternSettings.verticalRecoil.y, p.recoilPatternSettings.verticalRecoil.y * Scale(CtlBase(dw) + C("vertical_grip") + C("ext_mag") + Effects.VgripPerShot * 10)));
                Effects.ShotsOverrideForTest = -1;   // 指を離した
            }));
            s.Add(("vertical grip: released", () =>
            {
                var dw = Active(); var p = Prefab(Akms);
                Check("vertical grip: control resets when the trigger is released", Effects.ShotsForTest == 0 && Near(dw.recoilPatternSettings.verticalRecoil.y, p.recoilPatternSettings.verticalRecoil.y * Scale(CtlBase(dw) + C("vertical_grip") + C("ext_mag"))));
                Effects.ShotsOverrideForTest = null;
                // 拡張マガジン：容量 1.5 倍。外すと入りきらない弾は弾薬に戻る
                var inv = Refs.Inv;
                inv.AddMag(Akms);
                Effects.SyncMags();
                var name = Tracker.ItemById(Akms).GetName();
                var mag = inv.MagStored.ToArray().LastOrDefault(m => m != null && m.WeaponName == name);
                _base = inv.MagData.Mags.ToArray().FirstOrDefault(m => m != null && mag != null && m.Index == mag.Index)?.MaxAmmoCount ?? 0;
                int baseCap = _base;
                Check($"extended mag: capacity x1.5 ({mag?.MaxAmmoCount} of {baseCap})", mag != null && baseCap > 0 && mag.MaxAmmoCount == Mathf.RoundToInt(baseCap * 1.5f));
                if (mag == null) return;
                mag.AmmoCount = mag.MaxAmmoCount;
                int ammo = AmmoOf(mag.Caliber);
                Store.SetForTest(Akms, Cat.Mag, "");
                Effects.SyncMags();
                Check($"extended mag removed: back to {baseCap}, extra rounds returned (ammo {ammo} -> {AmmoOf(mag.Caliber)})",
                      mag.MaxAmmoCount == baseCap && mag.AmmoCount == baseCap && AmmoOf(mag.Caliber) == ammo + (Mathf.RoundToInt(baseCap * 1.5f) - baseCap));
                Store.SetForTest(Akms, Cat.Mag, "ext_mag");
                Effects.SyncMags();
                mag.AmmoCount = mag.MaxAmmoCount;
                _slot = inv.MagStored.IndexOf(mag);
                Check("loading speed: only the in-run load button is slowed", Effects.LoadPatchesForTest == 1);
                Check("save hooks and hit hook installed", Effects.SavePatched && Effects.HitPatched);
                // セーブ：ファイルには元の大きさで書き、余った弾は Mod 側に覚える
                UnityEngine.Object.FindObjectOfType<Il2Cpp.SavesManager>()?.Save();
            }));
            s.Add(("save written", () =>
            {
                var sm = UnityEngine.Object.FindObjectOfType<Il2Cpp.SavesManager>();
                var json = sm?.GetSavedJson() ?? "";
                var mag = Refs.Inv.MagStored[_slot];
                int start = json.IndexOf("\"MagStored\""), open = json.IndexOf('[', Math.Max(0, start));
                var objs = System.Text.RegularExpressions.Regex.Matches(json.Substring(open, json.IndexOf(']', open) - open), @"\{[^{}]*\}");
                string saved = _slot < objs.Count ? objs[_slot].Value : "";
                Main.Log($"[test] saved mag {_slot}: {System.Text.RegularExpressions.Regex.Replace(saved, @"\s+", " ")}");
                bool fileOk = System.Text.RegularExpressions.Regex.IsMatch(saved, "\"AmmoCount\"\\s*:\\s*" + _base + "\\b") && System.Text.RegularExpressions.Regex.IsMatch(saved, "\"MaxAmmoCount\"\\s*:\\s*" + _base + "\\b");
                var extra = System.IO.File.Exists(System.IO.Path.Combine(Main.Dir, "mag_extra.json")) ? System.IO.File.ReadAllText(System.IO.Path.Combine(Main.Dir, "mag_extra.json")) : "";
                Check($"extended mag in the save file: normal size and full (no extra rounds) ({extra})", fileOk && extra.Contains($"\"Ammo\":{Mathf.RoundToInt(_base * 1.5f) - _base}"));
                Check($"game keeps the extended mag after saving ({mag.AmmoCount}/{mag.MaxAmmoCount})", mag.MaxAmmoCount == Mathf.RoundToInt(_base * 1.5f) && mag.AmmoCount == mag.MaxAmmoCount);
                // 読み込んだときと同じ状態（元の大きさで満タン）にして、Mod が余りの弾を戻すか
                mag.MaxAmmoCount = _base; mag.AmmoCount = _base;
                Effects.RestoreExtra(Refs.Inv.MagStored);
                Check($"with the mod: extra rounds come back after loading ({mag.AmmoCount}/{mag.MaxAmmoCount})", mag.MaxAmmoCount == Mathf.RoundToInt(_base * 1.5f) && mag.AmmoCount == mag.MaxAmmoCount);
            }));

            // AK-74：マズルブレーキ＋アングルフォアグリップ＋ファストマグ
            Hold(s, Ak74);
            s.Add(("check AK-74", (Action)(() =>
            {
                if (!Holding(Ak74, out var dw)) return;
                var p = Prefab(Ak74);
                Check($"swiss scope: chest multiplier x2 ({Effects.ChestTargetForTest(_chest ?? (ChestDuring(dw) > 0 ? _chest : null)):0.##}); unchanged without it ({ChestDuring(dw):0.##})", Near(Effects.ChestTargetForTest(_chest), 3.6f) && Near(ChestDuring(dw), 1.8f) && Near(_chest.DamageMultiplier, 1.8f));
                Check($"broken parts: bulk sell patched {Parts.SellPatched}", Parts.SellPatched);
                Check($"muzzle brake {C("muzzle_brake")} +{Effects.CompBonus} (first shot)", Near(dw.recoilPatternSettings.verticalRecoil.y, p.recoilPatternSettings.verticalRecoil.y * Scale(CtlBase(dw) + C("muzzle_brake") + Effects.CompBonus)));
                Check($"angled grip -5 + fast mag +8: ergo +3 (aim speed x{AdsSpeed.Mult(AdsSpeed.ErgoOf(dw)):0.00})", Near(AdsSpeed.ErgoOf(dw), ErgBase(dw) + 3f));
                Check("angled grip: pistol movement on a rifle (no slide stiffness, aim/fire while sprinting)", Link.IsMobile() && !Link.IsPistolActive());
                Check($"fast mag: reload x{ReloadSpeed.ExtraMult?.Invoke(dw)}", Near(ReloadSpeed.ExtraMult?.Invoke(dw) ?? 0f, 1.2f));
                Check("no flash/alert change without a suppressor", dw.fireParticlesEffect != null && !dw.fireParticlesEffect.name.StartsWith(Mount.Prefix) && Near(dw.customAlertDistance, p.customAlertDistance));
            })));

            // G-18C：M93R ストック（連射速度 -50%・コントロール +50）＋コンペンセイター（-10、最初の 5 発は +20）→ 外す
            Hold(s, G18);
            s.Add(("check G-18C", () =>
            {
                if (!Holding(G18, out var dw)) return;
                var p = Prefab(G18);
                Check($"M93R stock on G-18C: fire rate -50% ({dw.fireRate:0} of {p.fireRate * Link.FireMult(dw):0})", Near(dw.fireRate, p.fireRate * Link.FireMult(dw) * 0.5f));
                float comp = Catalog.Get("p_compensator").Effects.Where(e => e.Stat == WeaponStat.Control).Sum(e => e.Value);
                Check($"stock +{Effects.G18Control}, compensator {comp} +{Effects.CompBonus} (first shot)", Near(dw.recoilPatternSettings.verticalRecoil.y, p.recoilPatternSettings.verticalRecoil.y * Scale(CtlBase(dw) + Effects.G18Control + comp + Effects.CompBonus)));
                Check("M93R stock: pistol movement removed (slide stiffness, no aim while sprinting, no +5% speed)", !Link.IsMobile() && Link.IsPistolActive());
                var extra = Link.ExtraDelta?.Invoke(G18) ?? default;
                Check($"stats screen: attachments add control {extra.Control} (= {Effects.G18Control} {comp}), fire rate halved",
                      Near(extra.Control, Effects.G18Control + comp) && Near(Link.RpmShown?.Invoke(G18, 1000f) ?? 0f, 500f));
                Effects.ShotsOverrideForTest = 5;
            }));
            s.Add(("compensator: 6th shot", () =>
            {
                var dw = Active(); var p = Prefab(G18);
                Check($"compensator: after 5 shots the bonus ends (x{dw.recoilPatternSettings.verticalRecoil.y / p.recoilPatternSettings.verticalRecoil.y:0.000})", Near(dw.recoilPatternSettings.verticalRecoil.y, p.recoilPatternSettings.verticalRecoil.y * Scale(CtlBase(dw) + Effects.G18Control + C("p_compensator"))));
                Effects.ShotsOverrideForTest = -1;
            }));
            s.Add(("compensator: released", () =>
            {
                var dw = Active(); var p = Prefab(G18);
                Check("compensator: bonus back after releasing the trigger", Near(dw.recoilPatternSettings.verticalRecoil.y, p.recoilPatternSettings.verticalRecoil.y * Scale(CtlBase(dw) + Effects.G18Control + C("p_compensator") + Effects.CompBonus)));
                Effects.ShotsOverrideForTest = null;
                Store.SetForTest(G18, Cat.Muzzle, null);
                Store.SetForTest(G18, Cat.Stock, null);
                Tracker.ApplyAll();
                dw = Active();
                Check("stock removed: fire rate and pistol movement back", Near(dw.fireRate, p.fireRate * Link.FireMult(dw)) && Link.IsMobile());
            }));
            return s;
        }

        static int AmmoOf(float caliber)
        {
            int n = 0;
            foreach (var kv in Refs.Inv.AmmoStored) if (kv.Key != null && Mathf.Approximately(kv.Key.Caliber, caliber)) n += kv.Value;
            return n;
        }
    }
}
