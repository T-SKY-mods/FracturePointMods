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

        // バランス調査用：今の計算の中心値（熟練度・アタッチメントなし）と、リロード時間・弾の性能。読むだけ
        public static void Balance()
        {
            var inv = Tracker.Inv;
            var weapons = inv.WeaponData.Weapons;
            var sb = new StringBuilder();
            sb.AppendLine("Id,Name,Slot,Rarity,Weight,Price,Rpm,Auto,Burst,BurstLen,Mode,Damage,Speed,SpeedRand,Mass,Diam,Drag,PreDrag,Life,Mag,MagFactor,MagByControl,MagCtlBonus," +
                "OrigCtl,OrigErg,ShownCtl,ShownErg,Aim,VMin,VMax,HMin,HMax,Damp,Pitch0,Pitch1,Kick0,Kick1,ReloadLen,ReloadEmptyLen,Factor," +
                "C_D,C_R,C_Ctl,C_Erg,C_Rl,U_D,U_R,U_Ctl,U_Erg,U_Rl,R_D,R_R,R_Ctl,R_Erg,R_Rl,E_D,E_R,E_Ctl,E_Erg,E_Rl,L_D,L_R,L_Ctl,L_Erg,L_Rl");
            for (int i = 0; i < weapons.Count; i++)
            {
                var wi = weapons[i];
                var dw = wi.Weapon;
                var bw = dw?.GetComponentInChildren<BallisticWeapon>(true);
                if (dw == null || bw == null) continue;
                var rp = dw.recoilPatternSettings; var ra = dw.recoilData;
                float aim = 0f; try { aim = AdsSpeed.Original(AdsSpeed.SettingsOf(dw)); } catch { }
                float rl = 0f, rle = 0f; try { rl = dw.reloadClip?.clip?.length ?? 0f; rle = dw.reloadEmptyClip?.clip?.length ?? 0f; } catch { }
                var cols = new List<string>
                {
                    wi.Id.ToString(), Q(SafeName(wi)), wi.Type.ToString(), Rarity(wi), F(wi.GetWeight()), wi.price.ToString(),
                    F(dw.fireRate), dw.supportsAuto.ToString(), dw.supportsBurst.ToString(), dw.burstLength.ToString(), dw.shootingType.ToString(),
                    F(bw.MuzzleDamage), F(bw.MaxBulletSpeed), F(bw.randomSpeedOffset), F(bw.BulletMass), F(bw.Diameter), F(bw.DragCoefficient), bw.PreDrag.ToString("0.########", CultureInfo.InvariantCulture), F(bw.LifeTimeOfBullets),
                    RarityTable.MagSize(wi).ToString(), F(RarityTable.MagFactor(wi)), RarityTable.MagByControl(wi).ToString(), RarityTable.MagControlBonus(wi).ToString(),
                    StatScale.OrigControl(wi).ToString(), StatScale.OrigErgo(wi).ToString(), wi.Control.ToString(), wi.Ergonomic.ToString(), F(aim),
                    F(rp?.verticalRecoil.x ?? 0), F(rp?.verticalRecoil.y ?? 0), F(rp?.horizontalRecoil.x ?? 0), F(rp?.horizontalRecoil.y ?? 0), F(rp?.damping ?? 0),
                    F(ra?.pitch.x ?? 0), F(ra?.pitch.y ?? 0), F(ra?.kickback.x ?? 0), F(ra?.kickback.y ?? 0), F(rl), F(rle), F(ReloadSpeed.Factor),
                };
                foreach (Tier t in Enum.GetValues(typeof(Tier)))
                {
                    var a = RarityTable.Average(wi, t);
                    cols.AddRange(a == null ? new[] { "", "", "", "", "" } : new[] { F(a.DamageMult), F(a.FireRateMult), a.ControlDelta.ToString(), a.ErgoDelta.ToString(), F(a.ReloadMult * RarityTable.ReloadBonus(a.WeaponId, a.Tier)) });
                }
                sb.AppendLine(string.Join(",", cols));
            }
            var s = BallisticSettings(inv);
            var path = Path.Combine(Main.Dir, "balance_dump.csv");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            Main.Log($"[dump] wrote balance data to {path}; {s}");
        }

        // 全武器・全レアリティの性能の下限・中央値・上限（熟練度・アタッチメントなし）。読むだけ
        public static void Ranges()
        {
            var inv = Tracker.Inv;
            var weapons = inv.WeaponData.Weapons;
            var sb = new StringBuilder();
            sb.AppendLine("Id,Name,Slot,VanillaRarity,Tier,Mode,Mag,VanillaDamage,VanillaRpm,VanillaControl,VanillaErgo,VanillaAim,VanillaReload," +
                "DmgMin,DmgMed,DmgMax,RpmMin,RpmMed,RpmMax,DpsMin,DpsMed,DpsMax,ReloadMin,ReloadMed,ReloadMax,CtlMin,CtlMed,CtlMax,ErgMin,ErgMed,ErgMax,AimMin,AimMed,AimMax");
            for (int i = 0; i < weapons.Count; i++)
            {
                var wi = weapons[i];
                var dw = wi.Weapon;
                var bw = dw?.GetComponentInChildren<BallisticWeapon>(true);
                if (dw == null || bw == null) continue;
                float aim = 0f, rl = 0f;
                try { aim = AdsSpeed.Original(AdsSpeed.SettingsOf(dw)); } catch { }
                try { rl = (dw.reloadClip?.clip?.length ?? 0f) * ReloadSpeed.Factor; } catch { }
                string mode = dw.supportsAuto ? "Auto" : dw.supportsBurst ? "Burst" : "Semi";
                foreach (Tier t in Enum.GetValues(typeof(Tier)))
                {
                    var med = RarityTable.Average(wi, t);
                    var all = RarityTable.Corners(wi, t);
                    if (med == null || all.Count == 0) continue;
                    float Dmg(WeaponRoll r) => bw.MuzzleDamage * r.DamageMult;
                    float Rpm(WeaponRoll r) => dw.fireRate * r.FireRateMult;
                    float Dps(WeaponRoll r) => Dmg(r) * RarityTable.EffectiveRpm(dw, Rpm(r)) / 60f;
                    float Rl(WeaponRoll r) => rl / (r.ReloadMult * RarityTable.ReloadBonus(wi.Id, r.Tier));
                    float Ctl(WeaponRoll r) => Math.Max(0, wi.Control + r.ControlDelta);
                    float Erg(WeaponRoll r) => Math.Max(0, wi.Ergonomic + r.ErgoDelta);
                    float Aim(WeaponRoll r) => aim * StatScale.AdsMult(r.ErgoDelta);
                    var cols = new List<string> { wi.Id.ToString(), Q(SafeName(wi).Trim()), wi.Type.ToString(), Rarity(wi), t.ToString(), mode, RarityTable.MagSize(wi).ToString(),
                        F(bw.MuzzleDamage), F(dw.fireRate), wi.Control.ToString(), wi.Ergonomic.ToString(), F(aim), F(rl) };
                    foreach (var f in new Func<WeaponRoll, float>[] { Dmg, Rpm, Dps, Rl, Ctl, Erg, Aim })
                        cols.AddRange(new[] { F(all.Min(f)), F(f(med)), F(all.Max(f)) });
                    sb.AppendLine(string.Join(",", cols));
                }
            }
            var path = Path.Combine(Main.Dir, "range_dump.csv");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            Main.Log($"[dump] wrote stat ranges to {path}");
        }

        static string BallisticSettings(Inventory inv)
        {
            try
            {
                var s = Il2CppBallistics.BulletHandler.instance?.Settings;
                return s == null ? "no ballistics settings" : $"drop {s.useBulletdrop} drag {s.useBulletdrag} materials {s.useBallisticMaterials} air {s.AirDensity}";
            }
            catch (Exception e) { return e.Message; }
        }

        static string SafeName(WeaponItem wi) { try { return wi.GetName(); } catch { return "?"; } }
        static string Rarity(WeaponItem wi) { try { return wi.GetRarity().ToString(); } catch { return "?"; } }
    }
}
