using System;
using System.Collections.Generic;
using Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core;
using Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.AdsLayer;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPModOption
{
    // エルゴ → 構える速さ。エルゴ +1 で構える速さ ×1.01（0.5 倍〜2.5 倍。表示のエルゴと同じ単位：StatScale）。
    //   ゲームの WeaponItem.Ergonomic は表示だけで、どこにも使われていない（build 25787540 で調査）。
    //   構える速さは武器のアニメーションのプロファイルの AdsLayerSettings.aimingSpeed（AK 系は 2.3）。プロファイルは武器の種類どうしで
    //   共有なので、手に持っている武器が変わるたびに「元の値 × その 1 本の倍率」を書き込む
    public static class AdsSpeed
    {
        public const float PerPoint = 0.01f, Min = 0.5f, Max = 2.5f;

        // アタッチメントによるエルゴ（FPCustom が入れる。武器の種類ごと）
        public static Func<int, float> ExtraErgo;

        public static float Mult(float ergo) => StatScale.AdsMult(ergo);

        public static float ErgoOf(DemoWeapon w)
        {
            float e = Link.Recoil(w)?.Ergo ?? 0f;
            try { int id = Weapons.IdOf(w); if (id >= 0 && ExtraErgo != null) e += ExtraErgo(id); } catch { }
            return e;
        }

        static readonly Dictionary<IntPtr, float> _orig = new();   // AdsLayerSettings ごとの元の値
        static IntPtr _weapon;
        static float _set = -1f;
        static int _frame;
        public static float LastMultForTest = 1f;
        public static AdsLayerSettings LastSettingsForTest;

        public static AdsLayerSettings SettingsOf(DemoWeapon w)
        {
            var prof = w?.GetComponent<FPSAnimatorEntity>()?.animatorProfile;
            if (prof?.settings == null) return null;
            foreach (var s in prof.settings) { var a = s?.TryCast<AdsLayerSettings>(); if (a != null) return a; }
            return null;
        }

        public static float Original(AdsLayerSettings a) => a == null ? 0f : _orig.TryGetValue(a.Pointer, out var o) ? o : a.aimingSpeed;

        // 毎フレーム（持ち替えたとき・10 フレームごと）
        public static void Tick()
        {
            if (++_frame % 10 != 0 && _weapon != IntPtr.Zero) return;
            DemoWeapon w = null;
            try { w = Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>(); } catch { }
            if (w == null) { _weapon = IntPtr.Zero; return; }
            float m = Mult(ErgoOf(w));
            if (w.Pointer == _weapon && Mathf.Abs(m - _set) < 0.0001f) return;
            var a = SettingsOf(w);
            if (a == null) return;
            if (!_orig.ContainsKey(a.Pointer)) _orig[a.Pointer] = a.aimingSpeed;
            a.aimingSpeed = _orig[a.Pointer] * m;
            _weapon = w.Pointer; _set = m;
            LastMultForTest = m; LastSettingsForTest = a;
        }

        // 次のフレームで書き直す（テスト・付け替えのとき）
        public static void Refresh() { _weapon = IntPtr.Zero; _set = -1f; }
    }
}
