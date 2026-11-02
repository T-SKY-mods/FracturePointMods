using System;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPModOption
{
    // Mod どうしの連携。それぞれの Mod が自分の分を入れる。入っていなければ（その Mod が無い・オフ）バニラの値になる
    public static class Link
    {
        // ---------- FPRarity が入れる ----------

        public static bool RarityActive;      // レアリティの機能が動いている（MOD タブで機能ごとにオフにできる）
        public static bool MasteryActive;     // 武器熟練度（保管庫の熟練度ページ）が動いている
        public static bool BossFloorsActive;  // エンドレスの階を FPRarity が作っている（FloorOverride はそこから呼ばれる）

        // その実体 1 本を見分ける名前（レアリティのロールごと）。無ければ武器の種類ごと
        public static Func<DemoWeapon, string> KeyOf;
        public static string Key(DemoWeapon w, int weaponId)
        {
            try { var k = KeyOf?.Invoke(w); if (k != null) return k; } catch { }
            return "w" + weaponId;
        }

        // レアリティ・熟練度による反動の補正（コントロール・エルゴの点数）。null なら反動はゲームのまま（Mod 導入前の武器など）
        public static Func<DemoWeapon, (float Control, float Ergo)?> RecoilDelta;
        public static (float Control, float Ergo)? Recoil(DemoWeapon w)
        {
            try { return RecoilDelta == null ? null : RecoilDelta(w); } catch { return null; }
        }

        // レアリティ・熟練度の連射倍率（テストの期待値用）
        public static Func<DemoWeapon, float> FireRateMult;
        public static float FireMult(DemoWeapon w) { try { return FireRateMult?.Invoke(w) ?? 1f; } catch { return 1f; } }
        // ハンドガンの動き（Slide。武器カスタマイズmod が使う）
        public static bool IsMobile() { try { return Slide.Mobile(); } catch { return false; } }
        public static bool IsPistolActive() { try { return Slide.PistolActive(); } catch { return false; } }

        // 保管庫の熟練度ページ
        public static Action<bool> ShowMastery;
        public static Func<bool> MasteryOpen;
        public static Func<GameObject> MasteryOpenButtonForTest;

        // 武器のレアリティの名前と色（FPSurvival の記録・通知で使う）
        public static Func<Tier, string> TierName;
        public static Func<Tier, Color> TierColor;
        public static string NameOfTier(Tier t) { try { return TierName?.Invoke(t) ?? t.ToString(); } catch { return t.ToString(); } }

        // 手持ちの slot 番目の武器（prefab の名前）のレアリティ（記録が無ければ null）
        public static Func<int, string, Tier?> HeldTier;
        public static Tier? HeldTierAt(int slot, string prefab) { try { return HeldTier?.Invoke(slot, prefab); } catch { return null; } }

        // テスト用：武器を手持ちに入れたときのゲームの通知と色
        public static Func<System.Collections.Generic.List<(string text, Color color)>> NoticesForTest;

        // これから手持ちに入る武器を、このレアリティにする（FPSurvival の武器箱。通知はゲームの通知だけ）。入れられたら true
        public static Func<Il2Cpp.WeaponItem, Tier, bool> ExpectIncoming;

        // エンドレスのボスのバーを隠す（FPSurvival が出したボス）
        public static Action HideBossBars;

        // ---------- FPCustom が入れる ----------

        // アタッチメントによるコントロール・エルゴ（武器の種類ごと）。ステータス画面・比較の表示に足す
        public static Func<int, (float Control, float Ergo)> ExtraDelta;
        // 表示する連射速度（M93R ストックのフルオートなど）
        public static Func<int, float, float> RpmShown;
        // 表示するダメージ（リニアコンペンセイターの +15% など）。武器 ID, 元の値 → 表示する値
        public static Func<int, float, float> DamageShown;
        // ハンドガンの動きを付ける（true）・外す（false）・そのまま（null）
        public static Func<bool?> MobileOverride;
        // カスタマイズの画面が開いているか（開いている間は保管庫の性能のポップアップを出さない）
        public static Func<bool> CustomPageOpen;
        // サイトをアタッチメントで選ぶので、熟練度のサイトは載せない
        public static bool CustomSights;

        // ---------- FPSurvival が入れる ----------

        // エンドレスの階の作り方を差し替える。true なら差し替えた（FPRarity のボス部屋も作らない）
        public static Func<Il2Cpp.LevelElevator, int, bool> FloorOverride;
    }
}
