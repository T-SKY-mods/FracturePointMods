using System;
using System.Collections.Generic;
using System.Linq;
using FPModOption;
using UnityEngine;
using BallisticWeapon = Il2CppBallistics.Weapon;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPRarity
{
    // MOD タブの機能ごとのオン/オフ（autotest -Dev -Scenario features [-Off FPRarity.Rarity など]）。
    //   手に持っている武器に熟練度のポイントを 1 つダメージに振り、オン/オフに合わせた性能・表示・ほかの Mod への見え方になっているか
    public static class FeatureTest
    {
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static int _id = -1;
        static WeaponRoll _real;
        static float _baseDmg, _baseRate;

        static (DemoWeapon dw, BallisticWeapon bw) Active()
        {
            var go = Refs.Fps?.GetActiveItem()?.gameObject;
            return (go?.GetComponent<DemoWeapon>(), go?.GetComponentInChildren<BallisticWeapon>(true));
        }

        public static List<(string, Action)> Steps() => new()
        {
            ("features", () =>
            {
                Main.Log($"[test] features: rarity={Features.Rarity} mastery={Features.Mastery} boss={Features.Boss}");
                Check("other mods see the feature switches", Link.RarityActive == Features.Rarity && Link.MasteryActive == Features.Mastery && Link.BossFloorsActive == Features.Boss);
                Check("mastery page only with mastery on", (Link.ShowMastery != null) == Features.Mastery);
            }),
            ("setup", () =>
            {
                var view = Tracker.ActiveRoll(out var item);
                _id = item?.Id ?? -1;
                _real = Tracker.Held.FirstOrDefault(h => h.Roll.Uid == view?.Uid)?.Roll;
                _baseDmg = item?.Weapon?.GetComponentInChildren<BallisticWeapon>(true)?.MuzzleDamage ?? 0f;
                _baseRate = item?.Weapon?.fireRate ?? 0f;
                Main.Log($"[test] active {Tracker.NameOf(_id)} real={_real} view={view}");
                Check("active weapon with a roll", _id >= 0 && _real != null && view != null);
                if (Mastery.PointsUsed(_id) > 0) Mastery.Reset(_id);
                Mastery.SetKillsForTest(_id, Mastery.KillsForLevel(30));
                Main.Log($"[test] add damage point: {Mastery.AddPoint(_id, WeaponStat.Damage)}");
                Tracker.ApplyAll();
            }),
            ("wait", () => { }),
            ("check", () =>
            {
                var view = Tracker.ActiveRoll(out _);
                var (dw, bw) = Active();
                bool handgun = Mastery.IsHandgun(_id);
                float wantDmg = _baseDmg * (Features.Rarity ? _real.DamageMult : 1f) * (Features.Mastery ? 1f + Mastery.DamagePerPoint : 1f);
                float wantRate = _baseRate * (Features.Rarity && !handgun ? _real.FireRateMult : 1f);
                Main.Log($"[test] dmg {bw?.MuzzleDamage} want {wantDmg} / rate {dw?.fireRate} want {wantRate}");
                Check($"damage = vanilla x rarity({Features.Rarity}) x mastery({Features.Mastery})", bw != null && Mathf.Abs(bw.MuzzleDamage - wantDmg) < 0.05f);
                Check($"fire rate = vanilla x rarity({Features.Rarity})", dw != null && Mathf.Abs(dw.fireRate - wantRate) < 0.05f);
                if (Features.Rarity)
                    Check("rarity on: the weapon shows its roll", view != null && view.Tier == _real.Tier && view.DamageMult == _real.DamageMult);
                else
                {
                    var wi = Tracker.ItemById(_id);
                    Check($"rarity off: shown as the vanilla rarity ({view?.Tier} = {_real.BaseTier}), no roll", view != null && view.Tier == _real.BaseTier && view.Legacy && view.DamageMult == 1f);
                    Check("rarity off: roll kept for later", Tracker.Held.Any(h => h.Roll.Uid == _real.Uid && h.Roll.Tier == _real.Tier));
                    Check("rarity off: sell price unchanged", Mathf.Abs(Tracker.SellMultFor(wi) - 1f) < 0.0001f);
                    Check("rarity off: no rarity for other mods", Link.HeldTierAt(0, Tracker.Held[0].Prefab) == null);
                }
                if (Features.Mastery)
                    Check("mastery on: point counts", Mastery.PointsIn(_id, WeaponStat.Damage) == 1);
                else
                    Check("mastery off: point kept but not used", Mastery.PointsIn(_id, WeaponStat.Damage) == 0 && Mastery.Get(_id)?.Points[0] == 1 && !Mastery.SightOn(_id));
            }),
            ("restore", () => { if (_id >= 0) { Mastery.Reset(_id); Tracker.ApplyAll(); } }),
        };
    }
}
