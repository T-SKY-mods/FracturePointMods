using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // 射撃音・敵に聞こえる距離・銃口の火。サプレッサーを付けたら、ゲームでサプレッサーが付いている武器のものに差し替える
    //   ハンドガン ← Compact CV-1、メイン武器 ← VAL。CV-1 のサプレッサーを外したときは、同じ系統の G-18C Auto のものにする
    public static class Sound
    {
        const int PistolSuppressed = 23, RifleSuppressed = 14, PistolLoud = 13;

        static bool Suppressed(Att a) => a != null && (a.Id == "p_suppressor" || a.Id == "suppressor");

        public static int? DonorFor(int id)
        {
            bool pistol = Catalog.IsPistol(id);
            bool factory = pistol ? id == PistolSuppressed : id == RifleSuppressed;   // 最初からサプレッサー
            bool now = Suppressed(Store.Equipped(id, Cat.Muzzle)) || (!pistol && id == RifleSuppressed);
            if (now == factory) return null;   // ゲームのまま
            if (now) return pistol ? PistolSuppressed : RifleSuppressed;
            return pistol ? PistolLoud : (int?)null;
        }

        public static void Apply(DemoWeapon dw, DemoWeapon pdw, int id)
        {
            if (dw == null || pdw == null) return;
            var src = pdw;
            if (DonorFor(id) is int d && Tracker.ItemById(d)?.Weapon?.gameObject?.GetComponent<DemoWeapon>() is { } ddw) src = ddw;
            dw.fireClips = src.fireClips;
            // 借りた音は、鳴らす側（FPSGenericPlayer）が起動時に読み込む音に入っていない。撃ち始めに読み込みが間に合わず抜けないよう先に読む
            if (src != pdw && src.fireClips != null)
                for (int i = 0; i < src.fireClips.Count; i++)
                    if (src.fireClips[i] is { } c && c.loadState == AudioDataLoadState.Unloaded) c.LoadAudioData();
            // 鳴らすのは FireSoundController.fireClips（装備のとき Weapon.OnEquip が武器の fireClips を写す）。
            // 手に持ったまま付け替えたときは写し直されないので、ここで渡す（持っていない武器は次の装備のときに写る）
            if (dw.fireSound != null && Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>()?.Pointer == dw.Pointer)
                dw.fireSound.fireClips = dw.fireClips;
            dw.customAlertDistance = src.customAlertDistance;
            // 銃口の火（fireParticlesEffect）は触らない：ゲームは装備のときに場面の "MuzzleFlash10" を探して入れる（武器の元データは空）。
            // ここで元データの値を入れると空になり、撃てなくなっていた（OnFire が途中で止まる）。サプレッサーの火は Effects.Flash が消す
        }
    }
}
