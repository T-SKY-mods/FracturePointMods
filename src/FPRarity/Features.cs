using System.Collections.Generic;
using FPModOption;

namespace FPRarity
{
    // レアリティmod の中の機能のオン/オフ（設定の MOD タブ。次の起動から反映）。
    //   レアリティ：オフの間は武器をバニラの性能・バニラのレアリティとして見せる（View）。ロールは記録し続けるので、オンに戻せば戻る
    //   武器熟練度：オフの間はキルを数えず、強化ポイント・ホロサイトも効かない（記録は残す）
    //   ボスフロア：オフならエンドレスの階をボス部屋にしない
    public static class Features
    {
        public static bool Rarity => Settings.Enabled("FPRarity.Rarity");
        public static bool Mastery => Settings.Enabled("FPRarity.Mastery");
        public static bool Boss => Settings.Enabled("FPRarity.Boss");

        static readonly Dictionary<string, WeaponRoll> _neutral = new();

        // レアリティがオフのときの見え方：元のレアリティ・補正なし（Mod 導入前の武器と同じ扱い）。ロールそのものは書き換えない
        public static WeaponRoll View(WeaponRoll r)
        {
            if (r == null || Rarity) return r;
            if (_neutral.TryGetValue(r.Uid ?? "", out var n) && n.WeaponId == r.WeaponId && n.BaseTier == r.BaseTier) return n;
            n = new WeaponRoll { Uid = r.Uid, WeaponId = r.WeaponId, Tier = r.BaseTier, BaseTier = r.BaseTier, Legacy = true, Bal = RarityTable.BalanceVersion };
            _neutral[r.Uid ?? ""] = n;
            return n;
        }
    }
}
