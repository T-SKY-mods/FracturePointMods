using System;
using UnityEngine;

using FPModOption;

namespace FPRarity
{
    // ゲームの LootRarity と同じ並び・値
    // レアリティの段階 Tier は共通（FPModOption）

    // 武器1本ぶんのレアリティと性能補正
    public class WeaponRoll
    {
        public string Uid { get; set; }
        public int WeaponId { get; set; }
        public Tier Tier { get; set; }
        // その武器の元（バニラ）のレアリティ。性能の上下は Tier - BaseTier の段階差で決まる
        public Tier BaseTier { get; set; }
        public float DamageMult { get; set; } = 1f;
        public float FireRateMult { get; set; } = 1f;
        // リロードの速さ（ハンドガンだけ。1.1 = 10% 速い）。ハンドガンは連射速度の代わりにこちらを抽選する
        public float ReloadMult { get; set; } = 1f;
        public int ControlDelta { get; set; }
        public int ErgoDelta { get; set; }
        // Mod導入前から持っていた武器。元のレアリティ・補正なしで扱う
        public bool Legacy { get; set; }
        // バランス計算の版（古い版で抽選した武器は、レアリティはそのままで今の計算に置き換える）
        public int Bal { get; set; }

        public int Steps => (int)Tier - (int)BaseTier;

        public override string ToString() =>
            $"{Tier}{(Legacy ? "(legacy)" : $"({Steps:+0;-0;0} from {BaseTier})")} dmg x{DamageMult:0.00} rpm x{FireRateMult:0.00} reload x{ReloadMult:0.00} " +
            $"ctl {ControlDelta:+0;-0;0} erg {ErgoDelta:+0;-0;0} [{Uid}]";
    }

    // バランス調整（weapons_vanilla.csv の全武器のバニラ性能をもとに設定）v8
    //
    // ・レアリティはどの武器でも Common〜Legendary のどれにでもなる（深い階層ほど高レアが出やすい）
    // ・同じレアリティなら同じくらいの強さになるよう、実際に出せる DPS（ダメージ×連射）をレアリティごとの基準値に合わせる。
    //   - セミオートは1発ずつクリックするので、連射は「武器の連射速度」と「人が連打できる速さ（毎分 420 発）」の小さい方で計算する。
    //     バーストも同様に毎分 600 発を上限にする
    //   - 当てやすい武器（コントロールが高い）ほど DPS を下げ、反動が大きく当てにくい武器ほど上げる
    //     （コントロールの基準値との差 1 につき 0.8%、+30%〜-20%）。コントロール自体は基準値へ半分だけ寄せて武器の個性を残す
    //   - マガジンが小さい武器は、武器の個性に合わせた性能で補う（基準 メイン30発・サブ15発、(基準/弾数)^0.25）
    //       一撃型（8発以下、またはセミオート・バースト）… ダメージを上乗せ
    //       連射型（フルオートで弾数が少ない：VAL、Swiss 751、"Beater" MPS-5 など）… コントロールを上乗せ（短い連射を当てやすく）
    //   - ダメージの上乗せ（マガジン×コントロール）は合わせて最大 1.35 倍
    // ・1発のダメージはバニラの最高（約250）を中心値の上限にする。抽選の上振れで少し超えるのはよい
    // ・最後はレジェンダリーの厳選になるので、抽選の幅は大きめ（ダメージ ±12%、コントロール・エルゴ ±8）
    // ・連射の増減は ±5% までに抑え、強さの差は主にダメージで付ける（連射＝武器の個性として残す）
    // ・エルゴはバニラでレアリティと相関がないので、元の値から1段階 ±5 だけ動かす
    // ・ハンドガン（サブ武器）はクリックで撃つので連射を変えても意味が薄い。連射は元のまま、代わりにリロードの速さを ±10% で抽選する
    public static class RarityTable
    {
        static readonly System.Random Rng = new();

        public const int BalanceVersion = 11;   // 10: 連射速度にも ±8% の個体差 / 11: ハンドガンは連射の代わりにリロード速度
        const int RpgId = 26;              // ロケットランチャーは補正対象外

        //                                            Common Uncommon Rare  Elite  Legendary
        // 1発のダメージがバニラの最高（約250）を超えないよう、全体を下げてある（v7。以前は Legendary メイン 2750）
        static readonly float[] MainDps   = {          750,   950,   1200, 1600,  2000 };
        static readonly float[] SubDps    = {          480,   650,    850, 1100,  1350 };
        public const float MaxShotDamage = 250f;  // 1発のダメージの中心値の上限（バニラの最高 Hunter-32 と同じ）。抽選の上振れで少し超えるのはよい
        static readonly float[] Control   = {           20,    40,     65,   85,   100 };
        const float DpsJitter = 0.12f;     // ダメージのばらつき ±12%（厳選の幅）
        const float RpmJitter = 0.08f;     // 連射速度のばらつき ±8%（ダメージとは別に抽選。DPS の中心値の計算には含めない）
        const float ReloadJitter = 0.10f;  // ハンドガンのリロード速度のばらつき ±10%
        const float ControlPull = 0.5f;    // コントロールを基準値へ寄せる割合（残りは武器の個性）
        const float ControlDpsPerPoint = 0.008f;  // 基準値よりコントロールが 1 低いごとに DPS +0.8%（反動が大きく当てにくい分）
        const float ControlDpsMax = 0.30f;        // 上乗せの上限 +30%
        const float ControlDpsMin = -0.20f;       // 当てやすい武器の減らし幅の下限 -20%
        const float BonusCap = 1.35f;             // ダメージの上乗せ（マガジン×コントロール）の合計の上限
        const float SemiAutoRpmCap = 420f;        // セミオートで人が連打できる速さ（毎分）
        const float BurstRpmCap = 600f;           // バースト
        const int MainRefMag = 30, SubRefMag = 15;
        const int ErgoPerTier = 5;         // Rare を ±0 として1段階ごとのエルゴ増減
        const float StatJitter = 8f;       // コントロール/エルゴの ±ばらつき（厳選の幅）

        // 出現重み（Common → Legendary）。深い階層ほど高レアが出やすくなる（10階で頭打ち）
        //   1階: 40/30/18/9/3   10階: 15/30/28/17/10
        static float[] WeightsForFloor(int floor)
        {
            float luck = Math.Clamp(floor - 1, 0, 9) * (10f / 9f);
            return new[] { 40f - 2.5f * luck, 30f, 18f + 1.0f * luck, 9f + 0.8f * luck, 3f + 0.7f * luck };
        }

        public static int CurrentFloor()
        {
            try { return Math.Max(1, PlayerPrefs.GetInt("currentFloor", 1)); } catch { return 1; }
        }

        static readonly Color[] Colors =
        {
            new(0.600f, 0.600f, 0.600f), new(0.239f, 0.620f, 1.000f), new(0.700f, 0.400f, 1.000f),
            new(1.000f, 0.200f, 0.200f), new(1.000f, 0.890f, 0.000f),
        };

        public static WeaponRoll Roll(Il2Cpp.WeaponItem wi, Tier baseTier) => Roll(wi, baseTier, PickTier());

        public static WeaponRoll Roll(Il2Cpp.WeaponItem wi, Tier baseTier, Tier tier)
        {
            int weaponId = wi?.Id ?? -1;
            var roll = new WeaponRoll { Uid = NewUid(), WeaponId = weaponId, Tier = tier, BaseTier = baseTier };
            return Compute(roll, wi, Between(Rng)) ? roll : Legacy(weaponId, baseTier);
        }

        // 古い版のバランスで抽選された武器を、レアリティはそのままで今の計算に置き換える（ばらつきは個体ごとに固定）
        public static void Upgrade(WeaponRoll r)
        {
            if (r == null || r.Legacy || r.Bal >= BalanceVersion) return;
            var wi = Tracker.ItemById(r.WeaponId);
            if (wi == null) return;
            var old = r.ToString();
            if (r.Bal == 9 || r.Bal == 10)
            {
                // 版9 → 10 は連射速度の個体差が増えただけ。ダメージ・コントロール・エルゴはそのままにして、連射速度だけ引く
                if (r.Bal == 9)
                {
                    var rng = new System.Random(StableSeed(r.Uid + "/rpm"));
                    r.FireRateMult = Round2(r.FireRateMult * (1 - RpmJitter + (float)rng.NextDouble() * 2 * RpmJitter));
                }
                // 版10 → 11：ハンドガンは連射を元に戻し、リロード速度を引く（ダメージ・コントロール・エルゴはそのまま）
                if (IsHandgun(wi))
                {
                    var rng = new System.Random(StableSeed(r.Uid + "/reload"));
                    r.FireRateMult = 1f;
                    r.ReloadMult = Round2(1 - ReloadJitter + (float)rng.NextDouble() * 2 * ReloadJitter);
                }
                r.Bal = BalanceVersion;
                Main.Log($"[balance] {Tracker.NameOf(r.WeaponId)}: {old} -> {r}");
                return;
            }
            if (Compute(r, wi, Between(new System.Random(StableSeed(r.Uid))))) Main.Log($"[balance] upgraded {Tracker.NameOf(r.WeaponId)}: {old} -> {r}");
            else r.Bal = BalanceVersion;
        }

        // 実際に出せる連射（セミオート・バーストは連打の速さが上限）
        public static float EffectiveRpm(Il2CppDemo.Scripts.Runtime.Item.Weapon dw, float rpm) =>
            dw.supportsAuto ? rpm : dw.supportsBurst ? Math.Min(rpm, BurstRpmCap) : Math.Min(rpm, SemiAutoRpmCap);

        public static bool IsHandgun(Il2Cpp.WeaponItem wi) => wi != null && wi.Type == Il2Cpp.WeaponItem.SlotType.Small;

        public static int MagSize(Il2Cpp.WeaponItem wi)
        {
            int best = 0;
            try
            {
                var mags = Refs.Inv?.MagData?.Mags;
                var name = wi.GetName();
                for (int m = 0; mags != null && m < mags.Count; m++)
                    if (mags[m] != null && mags[m].WeaponName == name) best = Math.Max(best, mags[m].MaxAmmoCount);
            }
            catch { }
            return best;
        }

        public static float MagFactor(Il2Cpp.WeaponItem wi)
        {
            int mag = MagSize(wi);
            if (mag <= 0) return 1f;
            int refMag = wi.Type == Il2Cpp.WeaponItem.SlotType.Big ? MainRefMag : SubRefMag;
            return Math.Clamp(MathF.Pow((float)refMag / mag, 0.25f), 0.9f, 1.5f);
        }

        // マガジンの小ささを何で補うか：一撃型（8発以下・セミオート・バースト）はダメージ、連射型（フルオート）はコントロール
        public static bool MagByControl(Il2Cpp.WeaponItem wi) => wi.Weapon != null && wi.Weapon.supportsAuto && MagSize(wi) > 8;
        public static float MagDamageFactor(Il2Cpp.WeaponItem wi) => MagByControl(wi) ? 1f : MagFactor(wi);
        public static int MagControlBonus(Il2Cpp.WeaponItem wi) => MagByControl(wi) ? Math.Max(0, (int)MathF.Round((MagFactor(wi) - 1f) * 100f)) : 0;

        public static float ControlFactor(Tier t, int control) =>
            1f + Math.Clamp((Control[(int)t] - control) * ControlDpsPerPoint, ControlDpsMin, ControlDpsMax);

        // 狙う DPS（ばらつきなしの中心値。control はマガジンの上乗せ前のコントロール）。テスト用
        public static float TargetDps(Il2Cpp.WeaponItem wi, Tier t, int control) =>
            (wi.Type == Il2Cpp.WeaponItem.SlotType.Big ? MainDps : SubDps)[(int)t] * Math.Min(MagDamageFactor(wi) * ControlFactor(t, control), BonusCap);

        // a〜b の乱数を返す関数（抽選用）。平均の個体を求めるときは、常に真ん中を返す関数を使う
        static Func<float, float, float> Between(System.Random rng) => (a, b) => a + (float)rng.NextDouble() * (b - a);
        static readonly Func<float, float, float> Middle = (a, b) => (a + b) / 2f;

        // その武器・レアリティの「平均的な個体」（ばらつきが真ん中）。拾うときに、この個体と比べて当たり外れを表示する
        public static WeaponRoll Average(Il2Cpp.WeaponItem wi, Tier t)
        {
            var avg = new WeaponRoll { WeaponId = wi?.Id ?? -1, Tier = t, BaseTier = t };
            return Compute(avg, wi, Middle) ? avg : null;
        }

        static bool Compute(WeaponRoll roll, Il2Cpp.WeaponItem wi, Func<float, float, float> R)
        {
            var dw = wi?.Weapon;
            var bw = dw?.GetComponentInChildren<Il2CppBallistics.Weapon>(true);
            if (roll.WeaponId == RpgId || dw == null || bw == null || bw.MuzzleDamage <= 0 || dw.fireRate <= 0) return false;
            int t = (int)roll.Tier;

            // コントロール：基準値へ半分寄せる（残りは武器の個性）。連射型でマガジンが小さい武器は、その分コントロールを上乗せ（DPS の計算には含めない）
            //   抽選は以前の単位（ゲームの元の表示の値・+1 = 反動 -1%）で行い、同じ強さの今の単位（StatScale）に直して持つ
            int ctl = StatScale.OrigControl(wi);
            int rolledCtl = Math.Max(0, (int)MathF.Round(ctl + (Control[t] - ctl) * ControlPull + R(-StatJitter, StatJitter)));   // コントロール・エルゴに上限はない
            roll.ControlDelta = (int)MathF.Round(StatScale.FromOld(Math.Max(0, rolledCtl + MagControlBonus(wi)) - ctl));

            // 実際に出せる DPS を目標（中心値）に合わせる倍率 k を、ダメージと連射に分ける
            float k = TargetDps(wi, roll.Tier, rolledCtl) / (bw.MuzzleDamage * EffectiveRpm(dw, dw.fireRate) / 60f);
            // 連射は ±5% まで（DPS 差のごく一部だけ）、残りはすべてダメージで調整する（連打が上限の武器は連射を上げても DPS は変わらない）
            //   ハンドガンは連射を変えない
            bool handgun = IsHandgun(wi);
            float rpm = handgun ? 1f : Math.Clamp(MathF.Pow(k, 0.05f), 0.95f, 1.05f);
            float effGain = EffectiveRpm(dw, dw.fireRate * rpm) / EffectiveRpm(dw, dw.fireRate);
            // ダメージの中心値はバニラの最高（250）まで。ばらつきはその後に掛ける（上振れで少し超えるのはよい）
            float center = Math.Min(k / effGain, MaxShotDamage / bw.MuzzleDamage);
            roll.DamageMult = Round2(Math.Max(0.2f, center * R(1 - DpsJitter, 1 + DpsJitter)));
            roll.FireRateMult = Round2(rpm);

            int erg = wi.Ergonomic;
            int newErg = Math.Max(0, (int)MathF.Round(erg + (t - 2) * ErgoPerTier + R(-StatJitter, StatJitter)));
            roll.ErgoDelta = newErg - erg;

            // 連射速度の個体差（抽選の順番を変えないよう最後に引く。古い版の武器を置き換えるときも他の値が変わらない）
            //   ハンドガンは連射の代わりにリロード速度の個体差
            if (handgun) { roll.FireRateMult = 1f; roll.ReloadMult = Round2(R(1 - ReloadJitter, 1 + ReloadJitter)); }
            else { roll.FireRateMult = Round2(rpm * R(1 - RpmJitter, 1 + RpmJitter)); roll.ReloadMult = 1f; }
            roll.Bal = BalanceVersion;
            return true;
        }

        static int StableSeed(string s)
        {
            unchecked { int h = 17; foreach (var c in s ?? "") h = h * 31 + c; return h; }
        }

        public static WeaponRoll Legacy(int weaponId, Tier baseTier) => new()
        {
            Uid = NewUid(), WeaponId = weaponId, Tier = baseTier, BaseTier = baseTier, Legacy = true, Bal = BalanceVersion,
        };

        // 売値は段階差で増減（1段階で 15%）
        public static float SellPriceMult(WeaponRoll r) =>
            r == null || r.Legacy ? 1f : MathF.Pow(1.15f, Math.Clamp(r.Steps, -4, 4));

        // コントロール +1 で反動（視点の持ち上がりと銃の跳ね上がり）-1%（熟練度の分も含む。0倍〜1.6倍に制限）
        // 下限は 0%（コントロールが +100 なら反動が無くなる。以前は 20%）。エルゴは構える速さ（FPModOption.AdsSpeed）
        public const float MinRecoilScale = 0f;
        public static float RecoilScale(WeaponRoll r) => r == null ? 1f : Math.Max(MinRecoilScale, StatScale.RecoilMult(Mastery.ControlDeltaF(r)));

        public static Color ColorOf(Tier t) => Colors[(int)t];
        public static string Hex(Tier t) => ColorUtility.ToHtmlStringRGB(Colors[(int)t]);

        static Tier PickTier() => PickTier(CurrentFloor(), Tier.Common);

        // floor の階の出現率で抽選し、min より低ければ min にする（FPSurvival の武器箱）
        public static Tier PickTier(int floor, Tier min)
        {
            var t = PickTierFor(floor);
            return t < min ? min : t;
        }

        static Tier PickTierFor(int floor)
        {
            var weights = WeightsForFloor(floor);
            float total = 0;
            foreach (var w in weights) total += w;
            float r = (float)Rng.NextDouble() * total;
            for (int i = 0; i < weights.Length; i++)
            {
                if (r < weights[i]) return (Tier)i;
                r -= weights[i];
            }
            return Tier.Common;
        }

        static float Range(float min, float max) => min + (float)Rng.NextDouble() * (max - min);
        static float Round2(float v) => MathF.Round(v, 2);
        static string NewUid() => Guid.NewGuid().ToString("N").Substring(0, 8);
    }
}
