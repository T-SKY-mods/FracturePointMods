using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using FPSController = Il2CppDemo.Scripts.Runtime.Character.FPSController;

using FPModOption;

namespace FPRarity
{
    // 自動テスト "reload"：ハンドガンのリロード速度（ロール × 熟練度）と、メイン武器（変えない）のリロード時間を測る
    //   1. メイン武器でリロード → ゲームの係数どおりの時間か（係数の読み取りが正しいか）
    //   2. ハンドガンでリロード → ロールの速さの分だけ短く／長くなるか
    //   3. 熟練度の「リロード」に 10 ポイント振る → +20% 分さらに速くなるか
    //   mastery.json は autotest.ps1 が前後で退避・復元する
    public static class ReloadTest
    {
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");

        static int _gunId = -1;
        static float _expect, _pitch0;

        public static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>
            {
                ("factor", () => Check($"delay factor read ({ReloadSpeed.Factor:0.###})", ReloadSpeed.Factor > 0f)),
                ("equip primary", () => Equip(WeaponItem.SlotType.Big)),
            };
            Wait(s, 2);
            // メイン武器は個別調整（Hunter のリロード）の分だけ速い。それ以外はバニラの速さ
            s.Add(("reload timing guard", () =>
            {
                // 初めてのリロードで本体の定数を読むと 0.1 秒ほど止まる。起動時に読んであること
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _ = ReloadSpeed.Factor; FPModOption.ReloadSound.Volume();
                Check($"reload constants already read at startup ({sw.Elapsed.TotalMilliseconds:0.00} ms)", sw.Elapsed.TotalMilliseconds < 1.0);
            }));
            s.Add(("reload primary", () => Reload(expectSpeed: Mastery.ReloadMult(Tracker.RollFor(Active())))));
            Wait(s, 4);
            s.Add(("primary timing", () => Measure("primary (vanilla or tuned)")));
            s.Add(("equip handgun", () => Equip(WeaponItem.SlotType.Small)));
            Wait(s, 2);
            s.Add(("labels", () =>
            {
                Check($"mastery label for the handgun '{Mastery.StatName(WeaponStat.FireRate, _gunId)}' {Mastery.BonusText(WeaponStat.FireRate, 1, _gunId)}",
                      Mastery.StatName(WeaponStat.FireRate, _gunId) is "リロード" or "Reload speed" && Mastery.BonusText(WeaponStat.FireRate, 1, _gunId) == "+2%");
                var roll = Tracker.RollFor(Active());
                Check($"handgun roll: reload x{roll?.ReloadMult:0.00} within ±10%, fire rate x{roll?.FireRateMult:0.00} unchanged",
                      roll != null && (roll.Legacy || (roll.ReloadMult >= 0.895f && roll.ReloadMult <= 1.105f && roll.FireRateMult == 1f)));
                var dw = Active();
                var pdw = Inv()?.WeaponsList?.weaponPrefabs?.ToArray().FirstOrDefault(p => p != null && dw != null && p.name == dw.gameObject.name.Replace("(Clone)", "").Trim())?.GetComponent<DemoWeapon>();
                Check($"handgun fire rate is vanilla ({dw?.fireRate} = {pdw?.fireRate})", dw != null && pdw != null && Math.Abs(dw.fireRate - pdw.fireRate) < 0.5f);
            }));
            s.Add(("reload handgun", () => Reload(expectSpeed: Mastery.ReloadMult(Tracker.RollFor(Active())))));
            Wait(s, 4);
            s.Add(("handgun timing", () => Measure("handgun (roll)")));
            s.Add(("mastery 10 points", () =>
            {
                Mastery.SetKillsForTest(_gunId, 5000);
                Mastery.Reset(_gunId);
                for (int i = 0; i < 10; i++) Mastery.AddPoint(_gunId, WeaponStat.FireRate);
                var roll = Tracker.RollFor(Active());
                Check($"reload x{Mastery.ReloadMult(roll):0.00} = roll x{roll?.ReloadMult:0.00} * 1.20", roll != null && Math.Abs(Mastery.ReloadMult(roll) / RarityTable.ReloadBonus(roll.WeaponId, roll.Tier) - roll.ReloadMult * 1.2f) < 0.001f);
                Check($"summary '{Mastery.Summary(_gunId)}'", Mastery.Summary(_gunId).Contains("+20%"));
            }));
            s.Add(("reload handgun (mastery)", () => Reload(expectSpeed: Mastery.ReloadMult(Tracker.RollFor(Active())))));
            Wait(s, 4);
            s.Add(("mastery timing", () => Measure("handgun (roll x mastery)")));
            // メイン武器の個別調整：Hunter-12 のリロードが表どおりに速くなるか
            const int hunter = 19;
            s.Add(("add hunter", () =>
            {
                if (!Tracker.Held.Any(h => h.Roll.WeaponId == hunter))
                {
                    // メイン武器の枠が埋まっていると手に持てないので、ほかのメイン武器は倉庫へ（セーブはテスト後に戻る）
                    foreach (var h in Tracker.Held.ToList())
                        if (Tracker.ItemById(h.Roll.WeaponId) is { } wi && wi.Type == WeaponItem.SlotType.Big) TestMode.Call(Refs.Bag, "MoveWeaponToStash", wi);
                    TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(hunter));
                }
                Tracker.Inv.AddMag(hunter);   // 予備のマガジンが無いとリロードしない
                foreach (var m in Tracker.Inv.MagStored) if (m != null) { Main.Log($"[reloadtest] mag {m.WeaponName} {m.AmmoCount}/{m.MaxAmmoCount}"); if (m.WeaponName == Tracker.ItemById(hunter).GetName()) m.AmmoCount = m.MaxAmmoCount; }
            }));
            s.Add(("equip hunter", () =>
            {
                var name = Tracker.ItemById(hunter).Weapon.gameObject.name;
                var inst = Refs.Fps.InstantiatedWeapons;
                for (int i = 0; i < inst.Count; i++)
                    if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == name) { Refs.Fps.StartWeaponChange(i); return; }
                Check("hunter in hand", false);
            }));
            Wait(s, 2);
            s.Add(("reload hunter", () =>
            {
                var roll = Tracker.RollFor(Active());
                Check($"hunter reload x{Mastery.ReloadMult(roll):0.00} is the {roll?.Tier} value", roll != null && roll.WeaponId == hunter && Mastery.ReloadMult(roll) >= 2f);
                var a = Active()?.ReloadAudio;
                float p0 = a?.pitch ?? -1f;
                Reload(expectSpeed: Mastery.ReloadMult(roll));
                var t = FPModOption.ReloadSound.LastForTest;
                float mult = Mastery.ReloadMult(roll);
                var w = Active();
                Main.Log($"[reloadtest] sound '{t.clip}' {t.origLen:0.00}s -> {t.newLen:0.00}s, {t.sounds} sounds, playing {a?.isPlaying}");
                Check($"reload sound built quickly ({FPModOption.ReloadSound.LastBuildMs:0.0} ms, cached = 0)", FPModOption.ReloadSound.LastBuildMs < 10.0);
                Check($"hunter reload sound pitch kept ({p0:0.00} -> {a?.pitch:0.00})", a != null && Math.Abs(a.pitch - p0) < 0.01f);
                Check($"hunter reload sound shortened to the animation ({t.origLen:0.00}s -> {t.newLen:0.00}s, x{mult:0.00})", a != null && a.isPlaying && w != null && t.clip == w.ReloadClip?.name && t.newLen > 0f && t.newLen < t.origLen - 0.05f);
                _pitch0 = p0;
            }));
            Wait(s, 3);
            s.Add(("hunter timing", () =>
            {
                Measure("hunter (table)");
                var a = Active()?.ReloadAudio;
                Check($"reload sound pitch unchanged after the reload ({a?.pitch:0.00})", a != null && Math.Abs(a.pitch - _pitch0) < 0.01f);
            }));
            return s;
        }

        static void Wait(List<(string, Action)> s, int n) { for (int i = 0; i < n; i++) s.Add(("wait", () => { })); }
        static Inventory Inv() => Refs.Inv;
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.TryCast<DemoWeapon>();

        static void Equip(WeaponItem.SlotType slot)
        {
            var fps = Refs.Fps;
            var list = fps.InstantiatedWeapons;
            for (int i = 0; i < list.Count; i++)
            {
                var wi = list[i] == null ? null : Tracker.ItemByPrefab(list[i].gameObject.name.Replace("(Clone)", "").Trim());
                if (wi == null || wi.Type != slot) continue;
                fps.StartWeaponChange(i);
                if (slot == WeaponItem.SlotType.Small) _gunId = wi.Id;
                Main.Log($"[reloadtest] equip {wi.GetName()} ({slot})");
                return;
            }
            Check($"has a {slot} weapon", false);
        }

        static void Reload(float expectSpeed)
        {
            var w = Active();
            if (w?.SelectedMag == null) { Check("active weapon with a mag", false); return; }
            w.SelectedMag.AmmoCount = Math.Max(0, w.SelectedMag.AmmoCount - 1);   // 満タンだとリロードしない
            var clip = w.reloadClip?.clip;
            _expect = clip == null ? -1f : clip.length * ReloadSpeed.Factor / expectSpeed;
            ReloadSpeed.StartedAt = ReloadSpeed.EndedAt = -1f;
            Refs.Fps.OnReload();
            Main.Log($"[reloadtest] reload {w.name}: clip {clip?.name} {clip?.length:0.00}s, expect {_expect:0.00}s (x{expectSpeed:0.00})");
        }

        static void Measure(string what)
        {
            float took = ReloadSpeed.EndedAt - ReloadSpeed.StartedAt;
            Check($"{what}: reload took {took:0.00}s, expected {_expect:0.00}s", ReloadSpeed.StartedAt >= 0f && ReloadSpeed.EndedAt > 0f && Math.Abs(took - _expect) < 0.12f);
        }
    }
}
