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
        static float _expect;

        public static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>
            {
                ("factor", () => Check($"delay factor read ({ReloadSpeed.Factor:0.###})", ReloadSpeed.Factor > 0f)),
                ("equip primary", () => Equip(WeaponItem.SlotType.Big)),
            };
            Wait(s, 2);
            s.Add(("reload primary", () => Reload(expectSpeed: 1f)));
            Wait(s, 4);
            s.Add(("primary timing", () => Measure("primary (vanilla)")));
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
                Check($"reload x{Mastery.ReloadMult(roll):0.00} = roll x{roll?.ReloadMult:0.00} * 1.20", roll != null && Math.Abs(Mastery.ReloadMult(roll) - roll.ReloadMult * 1.2f) < 0.001f);
                Check($"summary '{Mastery.Summary(_gunId)}'", Mastery.Summary(_gunId).Contains("+20%"));
            }));
            s.Add(("reload handgun (mastery)", () => Reload(expectSpeed: Mastery.ReloadMult(Tracker.RollFor(Active())))));
            Wait(s, 4);
            s.Add(("mastery timing", () => Measure("handgun (roll x mastery)")));
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
