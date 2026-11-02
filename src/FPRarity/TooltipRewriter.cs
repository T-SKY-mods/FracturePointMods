using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Il2Cpp;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using FPSController = Il2CppDemo.Scripts.Runtime.Character.FPSController;
using BallisticWeapon = Il2CppBallistics.Weapon;

using FPModOption;

namespace FPRarity
{
    // 地面の武器のツールチップの数値を、レアリティ補正後の値に書き換える。
    //
    // ゲームの文章は次の形（ラベルは言語により変わるので、並び順で判別する）:
    //   <color=#FF0000>max damage:<b>90->70</b></color>      ← 比較行の1つ目 = ダメージ
    //   <color=#FF0000>control:<b>99->80</b></color>          ← 2つ目 = コントロール（変更しない）
    //   <color=#FF0000>ergonomic:<b>78->67</b></color>        ← 3つ目 = エルゴノミクス（変更しない）
    //   <color=#FFFFFF>fire rate:<b>800->800</color></b>      ← 4つ目 = 連射速度
    public static class TooltipRewriter
    {
        const int DamageLine = 0, ControlLine = 1, ErgoLine = 2, FireRateLine = 3;
        const string Better = "00FF00", Worse = "FF0000", Same = "FFFFFF";

        static readonly Regex Compare = new(@"<color=#[0-9A-Fa-f]{6}>(?<label>[^<]*)<b>(?<a>-?[\d.]+)->(?<b>-?[\d.]+)",
                                            RegexOptions.Compiled);

        public static string Rewrite(string text, LootableItem li, WeaponRoll roll)
        {
            var wi = Tracker.ItemById(li.ItemId);
            var prefab = wi?.Weapon;
            if (prefab == null) return text;

            var pbw = prefab.GetComponentInChildren<BallisticWeapon>(true);
            // 拾った後の値（レアリティのロール × 武器熟練度）
            float newDmg = Mastery.ShownDamage(roll, (pbw != null ? pbw.MuzzleDamage : 0) * Mastery.DamageMult(roll));   // アタッチメント（ほかの Mod）込み
            float newRpm = Mastery.ShownRpm(roll, prefab.fireRate * Mastery.FireRateMult(roll));   // アタッチメント（ほかの Mod）込み
            float newCtl = Math.Max(0, wi.Control + Mastery.ShownControlDelta(roll));   // コントロール・エルゴに上限はない。アタッチメント込み
            float newErg = Math.Max(0, wi.Ergonomic + Mastery.ShownErgoDelta(roll));

            // 「現在」は手に持っている武器の実際の値（Modの補正込み）
            float curDmg = float.NaN, curRpm = float.NaN, curCtl = float.NaN, curErg = float.NaN;
            var activeRoll = Tracker.ActiveRoll(out var activeItem);
            if (activeItem != null)
            {
                curCtl = Math.Max(0, activeItem.Control + Mastery.ShownControlDelta(activeRoll));
                curErg = Math.Max(0, activeItem.Ergonomic + Mastery.ShownErgoDelta(activeRoll));
            }
            var active = Refs.Fps?.GetActiveItem();
            var activeGo = active?.gameObject;
            if (activeGo != null)
            {
                var dw = activeGo.GetComponent<DemoWeapon>();
                if (dw != null) curRpm = dw.fireRate;
                var bw = activeGo.GetComponentInChildren<BallisticWeapon>(true);
                if (bw != null) curDmg = bw.MuzzleDamage;
            }

            // 比較行を先頭から数えて、ダメージ行と連射行だけ置き換える
            var sb = new System.Text.StringBuilder();
            int last = 0, index = 0;
            foreach (Match m in Compare.Matches(text))
            {
                sb.Append(text, last, m.Index - last);
                last = m.Index + m.Length;
                int i = index++;
                if (i > FireRateLine) { sb.Append(m.Value); continue; }
                float a = i switch { DamageLine => curDmg, ControlLine => curCtl, ErgoLine => curErg, _ => curRpm };
                if (float.IsNaN(a)) a = float.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture);
                float b = i switch { DamageLine => newDmg, ControlLine => newCtl, ErgoLine => newErg, _ => newRpm };
                int ai = (int)MathF.Round(a), bi = (int)MathF.Round(b);
                var color = bi > ai ? Better : bi < ai ? Worse : Same;
                sb.Append($"<color=#{color}>{m.Groups["label"].Value}<b>{ai}->{bi}");
            }
            sb.Append(text, last, text.Length - last);
            var result = sb.ToString();

            // レアリティ名はゲームの欄（ツールチップの下）に出るので、ここには書かない
            var quality = roll.Legacy ? "" : "\n" + QualityLines(roll, wi, newDmg, newRpm, newCtl, newErg);
            var mastery = Features.Mastery && Mastery.Eligible(roll.WeaponId) ? $"\n<color=#FFD966>{Mastery.DetailLines(roll.WeaponId)}</color>" : "";
            return result.TrimEnd('\r', '\n') + quality + mastery;
        }

        // ---------- 個体差（同じ武器・同じレアリティの中央値の個体と比べた当たり外れ） ----------
        //   ダメージ +10（+5%）               ← 緑 = 中央値より良い / 赤 = 悪い / 白 = ほぼ中央値
        //   連射速度 +8 RPM                    ← 連射速度は RPM の増減そのもの
        //   コントロール -3
        //   エルゴ +2
        // 中央値は、その武器・そのレアリティで抽選のばらつきがちょうど真ん中の個体（RarityTable.Average）。
        // 比べる値にも同じ熟練度の強化を含めるので、差はロールの当たり外れだけになる。
        // 連射速度はダメージとの配分で少しだけ上下する（ロールしたコントロールに連動）。
        const string Good = "7CFC00", Bad = "FF5050", Even = "FFFFFF";
        const float DamageEvenPct = 2f;   // ±2% 以内は「ほぼ中央値」
        const int StatEven = 1;           // コントロール・エルゴは ±1 以内

        public static string QualityLines(WeaponRoll r, Il2Cpp.WeaponItem wi, float dmg, float rpm, float ctl, float erg)
        {
            Upgraded(r);
            var avg = RarityTable.Average(wi, r.Tier);
            if (avg == null) return Summary(r);
            bool ja = UiPatches.Japanese;

            float dmgPct = (r.DamageMult / avg.DamageMult - 1f) * 100f;
            float rpmPct = (r.FireRateMult / avg.FireRateMult - 1f) * 100f;
            int ctlDiff = r.ControlDelta - avg.ControlDelta;
            int ergDiff = r.ErgoDelta - avg.ErgoDelta;

            string Line(string name, string diff, int sign) => $"<color=#{(sign > 0 ? Good : sign < 0 ? Bad : Even)}>{name} {diff}</color>";
            // 実際の値と中央値の差を「+10（+5%）」の形にする
            string WithPct(float value, float pct)
            {
                int d = (int)MathF.Round(value - value / (1f + pct / 100f));
                int p = (int)MathF.Round(pct);
                string ps = (p >= 0 ? "+" : "") + p + "%";
                return ja ? $"{Sign(d)}（{ps}）" : $"{Sign(d)} ({ps})";
            }
            int SignOf(float v, float even) => Math.Abs(v) <= even ? 0 : Math.Sign(v);
            int rpmDiff = (int)MathF.Round(rpm - rpm / (1f + rpmPct / 100f));
            // ハンドガンは連射の代わりにリロード速度（中央値の個体との差を % で）
            int reloadPct = (int)MathF.Round((r.ReloadMult / avg.ReloadMult - 1f) * 100f);
            var speedLine = RarityTable.IsHandgun(wi)
                ? Line(ja ? "リロード速度" : "Reload speed", (reloadPct >= 0 ? "+" : "") + reloadPct + "%", SignOf(reloadPct, StatEven))
                : Line(ja ? "連射速度" : "Fire rate", Sign(rpmDiff) + " RPM", Math.Sign(rpmDiff));
            return string.Join("\n", new[]
            {
                Line(ja ? "ダメージ" : "Damage", WithPct(dmg, dmgPct), SignOf(dmgPct, DamageEvenPct)),
                speedLine,
                Line(ja ? "コントロール" : "Control", Sign(ctlDiff), SignOf(ctlDiff, StatEven)),
                Line(ja ? "エルゴ" : "Ergonomics", Sign(ergDiff), SignOf(ergDiff, StatEven)),
            });
        }

        static bool Upgraded(WeaponRoll r) { RarityTable.Upgrade(r); return true; }   // 古いバランスの武器は今の計算に置き換えてから表示
        public static string Summary(WeaponRoll r) => Upgraded(r) && UiPatches.Japanese
            ? $"{UiPatches.TierName(r.Tier)}  ダメージ{Pct(r.DamageMult)}  {(Mastery.IsHandgun(r.WeaponId) ? $"リロード{Pct(r.ReloadMult)}" : $"連射{Pct(r.FireRateMult)}")}  コントロール{Sign(r.ControlDelta)}  エルゴ{Sign(r.ErgoDelta)}"
            : $"{r.Tier}  DMG {Pct(r.DamageMult)}  {(Mastery.IsHandgun(r.WeaponId) ? $"RELOAD {Pct(r.ReloadMult)}" : $"RPM {Pct(r.FireRateMult)}")}  CTL {Sign(r.ControlDelta)}  ERG {Sign(r.ErgoDelta)}";

        static string Sign(int v) => v >= 0 ? $"+{v}" : $"{v}";

        static string Pct(float mult)
        {
            int p = (int)MathF.Round((mult - 1f) * 100f);
            return p >= 0 ? $"+{p}%" : $"{p}%";
        }
    }
}
