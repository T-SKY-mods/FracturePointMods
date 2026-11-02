using System;
using System.Collections.Generic;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 拡張マガジンの弾がランに出ても減らないか（autotest -Dev -Scenario custmagrun）。
    //   拠点で持っている武器に拡張マガジンを付けて、その武器のマガジンを全部満タンにしてからエンドレスランに出る
    public static class MagRunTest
    {
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static string _weapon;
        static int _cap, _count;

        public static void Register() => TestMode.ExternalScenarios["custmagrun"] = Steps;

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>
            {
                ("fit extended mag", () =>
                {
                    var mags = Refs.Inv?.MagStored;
                    var first = mags?.ToArray().FirstOrDefault(m => m != null && Tracker.ItemById(IdOf(m.WeaponName)) != null);
                    _weapon = first?.WeaponName;
                    int id = IdOf(_weapon);
                    Main.Log($"[test] mag run: weapon {_weapon} (id {id})");
                    if (id < 0) return;
                    Store.SetForTest(id, Cat.Mag, Weapons.IsHandgun(id) ? "p_ext_mag" : "ext_mag");
                    Effects.SyncMags();
                }),
                ("load all mags", () =>
                {
                    var mags = Refs.Inv.MagStored.ToArray().Where(m => m != null && m.WeaponName == _weapon).ToList();
                    foreach (var m in mags) m.AmmoCount = m.MaxAmmoCount;
                    _cap = mags.FirstOrDefault()?.MaxAmmoCount ?? 0;
                    _count = mags.Count;
                    Main.Log($"[test] mag run: {_count} mags of {_weapon} loaded to {_cap}");
                    Check($"extended mags loaded in the base ({_count} x {_cap})", _count > 0 && _cap > 0);
                }),
                ("start endless run", () => Core.FindAll<Il2Cpp.ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()),
            };
            for (int i = 0; i < 14; i++) s.Add(("wait load", () => { }));
            s.Add(("check in the run", () =>
            {
                var mags = Refs.Inv?.MagStored?.ToArray().Where(m => m != null && m.WeaponName == _weapon).ToList() ?? new();
                Main.Log($"[test] mag run: scene {Core.Scene}, restore pending {Effects.RestorePendingForTest}, mags " + string.Join(" ", mags.Select(m => $"{m.AmmoCount}/{m.MaxAmmoCount}")));
                Check($"in the run: extended mags still full ({string.Join(" ", mags.Select(m => $"{m.AmmoCount}/{m.MaxAmmoCount}"))}, want {_cap})",
                      Core.Scene != Core.HubScene && mags.Count == _count && mags.All(m => m.MaxAmmoCount == _cap && m.AmmoCount == _cap));
            }));
            return s;
        }

        static int IdOf(string weaponName)
        {
            if (weaponName == null) return -1;
            for (int i = 0; i < 64; i++) { var wi = Tracker.ItemById(i); if (wi != null && wi.GetName() == weaponName) return i; }
            return -1;
        }
    }
}
