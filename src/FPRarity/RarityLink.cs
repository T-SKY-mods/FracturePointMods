using System;
using System.Linq;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPRarity
{
    // 共通の土台（FPModOption）と他の Mod への連携を登録する（この Mod が動いているときだけ）
    public static class RarityLink
    {
        public static void Install()
        {
            // 手持ちの武器：当てる前に記録を突き合わせ、ロール × 熟練度を当てる（FPCustom のアタッチメントはこの後）
            Weapons.BeforeApply += why => Tracker.ReconcileHeld(why);
            Weapons.AddApplier(0, "rarity", Tracker.ApplySlot);
            ReloadSpeed.BaseMult = w => Mastery.ReloadMult(Tracker.RollFor(w));

            // レアリティがオフの間は、ほかの Mod（FPSurvival の武器箱・縛りの点数）からはレアリティの無い武器に見える
            Link.RarityActive = Features.Rarity;
            Link.MasteryActive = Features.Mastery;
            Link.BossFloorsActive = Features.Boss;
            Link.KeyOf = w => Tracker.RollFor(w)?.Uid;
            // 反動の補正：Mod 導入前の武器で熟練度の反動補正もないものは、ゲームの反動のまま
            Link.RecoilDelta = w =>
            {
                var roll = Tracker.RollFor(w);
                if (roll == null) return null;
                if (roll.Legacy && !Mastery.HasRecoilBonus(roll)) return null;
                return (Mastery.ControlDeltaF(roll), Mastery.ErgoDeltaF(roll));
            };
            Link.FireRateMult = w => Mastery.FireRateMult(Tracker.RollFor(w));
            if (Features.Mastery) Link.ShowMastery = MasteryUi.ShowPage;
            Link.MasteryOpen = () => MasteryUi.IsOpen;
            Link.MasteryOpenButtonForTest = () => MasteryUi.OpenButtonForTest;
            Link.TierName = UiPatches.TierName;
            Link.TierColor = RarityTable.ColorOf;
            Link.HeldTier = (slot, prefab) => Features.Rarity ? Tracker.HeldRoll(slot, prefab)?.Tier : null;
            Link.NoticesForTest = () => UiPatches.AddNotices;
            Link.ExpectIncoming = (wi, tier) =>
            {
                if (wi == null || !Features.Rarity) return false;
                Tracker.ExpectIncoming(RarityTable.Roll(wi, Tracker.BaseTier(wi), tier), announce: false);
                return true;
            };
            Link.HideBossBars = EndlessBoss.HideBossBars;
        }
    }
}
