using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPModOption
{
    // エルゴ・コントロールの表示を、実際の性能から共通の式で決める（2026-10-31 ユーザー指定）。
    //   ゲームの WeaponItem.Ergonomic / Control は表示だけで、実際の構えの速さ・反動とは合っていない。
    //   表示の値を書き換えて「表示が同じなら性能も同じ」にする（性能そのものはバニラのまま）。
    //   エルゴ：構えの速さ 2.3（AK-74）= 50、+1 = 構えが 1% 速い
    //   コントロール：1 発の反動 3.0（AK-74）= 50、+1 = 反動 ×exp(-1/31.2)（約 -3.2%）。バニラは 0〜106 に収まる
    //   Mod の増減（レアリティ・熟練度・アタッチメント）も同じ単位。以前の「コントロール +1 = 反動 -1%」の値は FromOld で直す
    public static class StatScale
    {
        public const float RefAim = 2.3f, RefRecoil = 3f;   // AK-74
        public const float RefShown = 50f;
        public const float CtlLog = 31.2f;                  // コントロール CtlLog で反動 1/e
        const int RpgId = 26;                               // RPG は撃ち方が違うので表示を変えない

        // ---------- 性能の倍率 ----------

        public static float AdsMult(float ergo) => Mathf.Clamp(Mathf.Pow(1.01f, ergo), AdsSpeed.Min, AdsSpeed.Max);
        // 0.1% 未満は 0（コントロール +216 くらいから反動なし）
        public static float RecoilMult(float control) { float m = Mathf.Exp(-control / CtlLog); return m < 0.001f ? 0f : Mathf.Min(m, 1.6f); }

        // 以前の単位（+1 = 反動 -1%）の増減を、同じ強さの今の単位に直す
        public static float FromOld(float oldPoints) => -CtlLog * Mathf.Log(Mathf.Clamp(1f - oldPoints * 0.01f, 0.01f, 1.6f));
        public static float ToOld(float points) => 100f * (1f - Mathf.Exp(-points / CtlLog));

        // ---------- 表示の値 ----------

        public static int ShownErgo(float aimSpeed) => aimSpeed <= 0f ? 0 : Mathf.RoundToInt(RefShown + Mathf.Log(aimSpeed / RefAim) / Mathf.Log(1.01f));
        public static int ShownControl(float recoil) => recoil <= 0f ? 0 : Mathf.RoundToInt(RefShown - CtlLog * Mathf.Log(recoil / RefRecoil));

        // 1 発で照準が動く量の目安：縦の反動の平均 + 横のぶれの平均（±の幅の半分）
        public static float RecoilOf(DemoWeapon w)
        {
            var p = w?.recoilPatternSettings;
            if (p == null) return 0f;
            float v = (Mathf.Abs(p.verticalRecoil.x) + Mathf.Abs(p.verticalRecoil.y)) / 2f;
            float h = Mathf.Max(Mathf.Abs(p.horizontalRecoil.x), Mathf.Abs(p.horizontalRecoil.y)) / 2f;
            return v + h;
        }

        // ---------- 武器のデータに書き込む ----------

        static readonly Dictionary<int, (int Ergo, int Control)> _orig = new();
        static bool _applied;

        // ゲームの元の表示の値（レアリティの抽選は元の値を基準にしている）
        public static int OrigControl(WeaponItem wi) => wi != null && _orig.TryGetValue(wi.Id, out var o) ? o.Control : wi?.Control ?? 0;
        public static int OrigErgo(WeaponItem wi) => wi != null && _orig.TryGetValue(wi.Id, out var o) ? o.Ergo : wi?.Ergonomic ?? 0;

        // 武器の一覧ができたとき（Weapons）。エルゴ・コントロールを使う Mod が動いているときだけ
        public static void Apply(IEnumerable<WeaponItem> items)
        {
            if (_applied || GameVersion.Disabled) return;
            if (!Settings.Enabled("FPRarity") && !Settings.Enabled("FPCustom")) return;
            _applied = true;
            var log = new List<string>();
            foreach (var wi in items)
            {
                var w = wi?.Weapon;
                if (w == null || wi.Id == RpgId) continue;
                if (!_orig.ContainsKey(wi.Id)) _orig[wi.Id] = (wi.Ergonomic, wi.Control);
                float aim = AdsSpeed.Original(AdsSpeed.SettingsOf(w)), recoil = RecoilOf(w);
                if (aim > 0f) wi.Ergonomic = ShownErgo(aim);
                if (recoil > 0f) wi.Control = ShownControl(recoil);
                log.Add($"{wi.Id}:{_orig[wi.Id].Ergo}/{_orig[wi.Id].Control}->{wi.Ergonomic}/{wi.Control}");
            }
            Core.Log("[statscale] ergo/control shown " + string.Join(" ", log));
        }
    }
}
