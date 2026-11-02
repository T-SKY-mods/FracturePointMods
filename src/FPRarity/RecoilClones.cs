using System.Collections.Generic;
using Il2CppKINEMATION.FPSAnimationFramework.Runtime.Recoil;
using UnityEngine;

using FPModOption;

namespace FPRarity
{
    // 反動データ（武器の種類ごとの共有アセット）を、ロールごとに複製して倍率をかけたものを返す。
    // 武器の実体は手持ちが変わるたびに作り直されるので、複製はロールの Uid ごとに使い回す。
    public static class RecoilClones
    {
        static readonly Dictionary<string, RecoilPatternSettings> _patterns = new();
        static readonly Dictionary<string, RecoilAnimData> _anims = new();

        // コントロール：射撃中に視点が持ち上がる量（横・縦の反動の範囲）
        public static RecoilPatternSettings Pattern(WeaponRoll roll, RecoilPatternSettings original)
        {
            if (original == null) return null;
            float s = RarityTable.RecoilScale(roll);
            var key = $"{roll.Uid}|{s:0.000}"; // 熟練度で倍率が変わったら別の複製を作る
            if (_patterns.TryGetValue(key, out var c) && c != null) return c;

            c = Object.Instantiate(original).Cast<RecoilPatternSettings>();
            c.hideFlags = HideFlags.DontUnloadUnusedAsset;
            c.horizontalRecoil = original.horizontalRecoil * s;
            c.verticalRecoil = original.verticalRecoil * s;
            _patterns[key] = c;
            return c;
        }

        // コントロール：撃った瞬間の銃の跳ね上がり・揺れ（見た目と照準のブレ）も同じ倍率
        public static RecoilAnimData Anim(WeaponRoll roll, RecoilAnimData original)
        {
            if (original == null) return null;
            float s = RarityTable.RecoilScale(roll);
            var key = $"{roll.Uid}|{s:0.000}";
            if (_anims.TryGetValue(key, out var c) && c != null) return c;

            c = Object.Instantiate(original).Cast<RecoilAnimData>();
            c.hideFlags = HideFlags.DontUnloadUnusedAsset;
            c.pitch = original.pitch * s;
            c.roll = original.roll * s;
            c.yaw = original.yaw * s;
            c.kickback = original.kickback * s;
            c.kickUp = original.kickUp * s;
            c.kickRight = original.kickRight * s;
            c.noiseScalar = original.noiseScalar * s;   // 撃ったときの小刻みな揺れ
            c.pushAmount = original.pushAmount * s;     // 押し込み（0 倍で無ブレ）
            _anims[key] = c;
            return c;
        }
    }
}
