using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core;

namespace FPModOption
{
    // 武器ごとの「表示のエルゴ・コントロール」と「実際の構えの速さ・反動の設定」の一覧（autotest -Dev -Scenario statlist）。
    //   武器の元データ（プレハブ）を読むだけで、何も変えない。UserData/FPModOption/statlist.csv とログに書く
    public static class StatList
    {
        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        public static List<(string, Action)> Steps() => new()
        {
            ("list", () =>
            {
                var lines = new List<string> { "id,name,type,ergo_shown,control_shown,aim_speed,aim_profile,fire_rate,vert_min,vert_max,horiz_min,horiz_max,pitch_min,pitch_max,kickup_min,kickup_max,kickback_min,kickback_max,noise,push" };
                foreach (var wi in Weapons.All())
                {
                    var w = wi.Weapon;
                    if (w == null) continue;
                    var ads = AdsSpeed.SettingsOf(w);
                    var prof = w.GetComponent<FPSAnimatorEntity>()?.animatorProfile;
                    var p = w.recoilPatternSettings; var a = w.recoilData;
                    lines.Add(string.Join(",", wi.Id, Weapons.NameOf(wi.Id).Replace(",", " "), wi.Type, wi.Ergonomic, wi.Control,
                        F(AdsSpeed.Original(ads)), prof?.name ?? "", F(w.fireRate),
                        F(p?.verticalRecoil.x ?? 0), F(p?.verticalRecoil.y ?? 0), F(p?.horizontalRecoil.x ?? 0), F(p?.horizontalRecoil.y ?? 0),
                        F(a?.pitch.x ?? 0), F(a?.pitch.y ?? 0), F(a?.kickUp.x ?? 0), F(a?.kickUp.y ?? 0), F(a?.kickback.x ?? 0), F(a?.kickback.y ?? 0),
                        F(a?.noiseScalar ?? 0), F(a?.pushAmount ?? 0)));
                }
                File.WriteAllLines(Path.Combine(Core.Dir, "statlist.csv"), lines, new UTF8Encoding(true));
                foreach (var line in lines) Core.Log("[statlist] " + line);
                TestMode.Check($"weapon list written ({lines.Count - 1} weapons)", lines.Count > 21);
            }),
        };
    }
}
