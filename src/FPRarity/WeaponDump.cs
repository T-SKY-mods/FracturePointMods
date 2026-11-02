using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Il2Cpp;
using UnityEngine;
using BallisticWeapon = Il2CppBallistics.Weapon;

using FPModOption;

namespace FPRarity
{
    // 全武器の素の性能を CSV に書き出す（バランス調整用）。武器の元データ・プレハブを読むだけで何も変更しない。
    public static class WeaponDump
    {
        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        public static List<(string, Action)> Steps() => new()
        {
            ("dump all weapons", Dump),
        };

        static void Dump()
        {
            var inv = Tracker.Inv;
            var weapons = inv.WeaponData.Weapons;
            var mags = inv.MagData?.Mags;

            var sb = new StringBuilder();
            sb.AppendLine("Id,Name,Slot,BaseRarity,Price,Weight,Ergonomic,Control,Prefab,FireRate,Auto,Burst,BurstLength," +
                          "MuzzleDamage,MaxBulletSpeed,BulletMass,Caliber,MagSizes," +
                          "RecoilPattern,HorizRecoilMin,HorizRecoilMax,VertRecoilMin,VertRecoilMax,RecoilDamping," +
                          "RecoilAnim,KickbackMin,KickbackMax,PitchMin,PitchMax");

            for (int i = 0; i < weapons.Count; i++)
            {
                var wi = weapons[i];
                var dw = wi.Weapon;
                var bw = dw?.GetComponentInChildren<BallisticWeapon>(true);
                var rp = dw?.recoilPatternSettings;
                var ra = dw?.recoilData;
                string name = SafeName(wi);

                // 弾倉データは WeaponName で武器に対応する
                var sizes = new SortedSet<int>();
                float caliber = 0;
                if (mags != null)
                    for (int m = 0; m < mags.Count; m++)
                        if (mags[m] != null && mags[m].WeaponName == name)
                        {
                            sizes.Add(mags[m].MaxAmmoCount);
                            caliber = mags[m].Caliber;
                        }

                sb.Append(string.Join(",", new[]
                {
                    wi.Id.ToString(), Q(name), wi.Type.ToString(), Rarity(wi), wi.price.ToString(), F(wi.GetWeight()),
                    wi.Ergonomic.ToString(), wi.Control.ToString(), Q(dw?.gameObject.name),
                    dw != null ? F(dw.fireRate) : "", dw?.supportsAuto.ToString() ?? "", dw?.supportsBurst.ToString() ?? "",
                    dw != null ? dw.burstLength.ToString() : "",
                    bw != null ? F(bw.MuzzleDamage) : "", bw != null ? F(bw.MaxBulletSpeed) : "", bw != null ? F(bw.BulletMass) : "",
                    F(caliber), Q(string.Join("/", sizes)),
                    Q(rp?.name), rp != null ? F(rp.horizontalRecoil.x) : "", rp != null ? F(rp.horizontalRecoil.y) : "",
                    rp != null ? F(rp.verticalRecoil.x) : "", rp != null ? F(rp.verticalRecoil.y) : "", rp != null ? F(rp.damping) : "",
                    Q(ra?.name), ra != null ? F(ra.kickback.x) : "", ra != null ? F(ra.kickback.y) : "",
                    ra != null ? F(ra.pitch.x) : "", ra != null ? F(ra.pitch.y) : "",
                }));
                sb.AppendLine();
            }

            var path = Path.Combine(Main.Dir, "weapons_vanilla.csv");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            Main.Log($"[dump] wrote {weapons.Count} weapons to {path}");
        }

        static string SafeName(WeaponItem wi) { try { return wi.GetName(); } catch { return "?"; } }
        static string Rarity(WeaponItem wi) { try { return wi.GetRarity().ToString(); } catch { return "?"; } }
    }
}
