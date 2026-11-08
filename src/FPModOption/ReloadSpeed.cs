using System;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using FPSPlayablesController = Il2CppKINEMATION.FPSAnimationFramework.Runtime.Playables.FPSPlayablesController;

namespace FPModOption
{
    // リロード速度を変える仕組み（倍率は各 Mod が決める。どれも無ければバニラの速さ）。
    //   ゲームはリロード開始時に「アニメーションの長さ × 係数」秒後に OnActionEnded を呼ぶよう予約する。
    //   その予約を取り消して速さに合わせて予約し直し、武器と腕のアニメーションの再生速度も合わせる。
    //   係数はゲームの本体（このビルド）の定数を読む。読めない・ビルドが違うときはリロード速度を変えない
    public static class ReloadSpeed
    {
        const long DelayFactorRva = 0x425AB08;   // OnReload が使う係数（build 25787540）
        static float _factor = -1f;              // -1 = まだ読んでいない、0 = 使えない

        static DemoWeapon _active;               // 速さを変えてリロード中の武器
        static AnimationClip _activeClip;        // 速さを変えた腕のアニメーション

        // FPRarity（ハンドガンのロール ±10% × 熟練度）がかける倍率
        public static Func<DemoWeapon, float> BaseMult;
        // FPCustom（アタッチメント）がかける倍率。ハンドガン以外にも効く
        public static Func<DemoWeapon, float> ExtraMult;

        public static void Install(HarmonyLib.Harmony h)
        {
            // 失敗してもリロードがバニラの速さになるだけ
            void P(string m, string pre, string post)
            {
                if (!SafePatcher.Patch(h, typeof(DemoWeapon), m, null, pre, post, typeof(ReloadSpeed))) GameVersion.AddDegraded("リロード速度");
            }
            P("OnReload", nameof(OnReload_Pre), nameof(OnReload_Post));
            P("OnActionEnded", null, nameof(ActionEnded_Post));
            P("OnUnEquip", nameof(UnEquip_Pre), null);
            // 本体の定数は起動時に読んでおく（初めてのリロードで読むと一瞬止まる）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ = Factor; ReloadSound.Volume();
            Core.Log($"[reload] constants read in {sw.Elapsed.TotalMilliseconds:0.0} ms");
        }

        static void Safe(Action a) { try { a(); } catch (Exception e) { Core.Log("[reload] ERROR " + e); } }
        static void OnReload_Pre(DemoWeapon __instance) => Safe(() => Before(__instance));
        static void OnReload_Post(DemoWeapon __instance, bool __result) => Safe(() => After(__instance, __result));
        static void ActionEnded_Post(DemoWeapon __instance) => Safe(() => End(__instance, actionEnded: true));
        static void UnEquip_Pre(DemoWeapon __instance) => Safe(() => End(__instance));

        public static float Factor
        {
            get
            {
                if (_factor >= 0f) return _factor;
                _factor = 0f;
                try
                {
                    if (GameVersion.Untested) { Core.Log("[reload] untested game build: reload speed stays vanilla"); return _factor; }
                    var mod = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                        .FirstOrDefault(m => string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
                    if (mod == null) { Core.Log("[reload] GameAssembly.dll not found"); return _factor; }
                    float f = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(mod.BaseAddress + (nint)DelayFactorRva));
                    if (f > 0.2f && f < 2f) _factor = f;
                    Core.Log($"[reload] delay factor {f:0.###} ({(_factor > 0f ? "ok" : "rejected")})");
                }
                catch (Exception e) { Core.Log("[reload] factor read failed: " + e.Message); }
                return _factor;
            }
        }

        // OnReload の前：空のマガジンからのリロードか（ゲームは別のアニメーションを使う）
        static bool _empty;
        public static void Before(DemoWeapon w)
        {
            _empty = w?.SelectedMag != null && w.SelectedMag.AmmoCount <= 0;
        }

        public static void After(DemoWeapon w, bool started)
        {
            if (!started || w == null) return;
            StartedAt = Time.time;
            float m = 1f;
            try { m *= BaseMult?.Invoke(w) ?? 1f; } catch { }
            try { m *= ExtraMult?.Invoke(w) ?? 1f; } catch { }
            if (MathF.Abs(m - 1f) < 0.005f || Factor <= 0f) return;
            // 終わりの時間は空のマガジンからでも通常のリロードの長さで決まる（ゲームの作り）。腕の動きは実際に流れている方
            var length = w.reloadClip?.clip;
            var played = _empty && Il2CppKINEMATION.FPSAnimationFramework.Runtime.Playables.FPSAnimationAsset.IsValid(w.reloadEmptyClip) ? w.reloadEmptyClip.clip : length;
            if (length == null) return;
            float vanilla = length.length * Factor;
            float delay = vanilla / m;
            w.CancelInvoke("OnActionEnded");
            w.Invoke("OnActionEnded", delay);
            _active = w;
            _activeClip = played;
            SetAnimSpeed(w, played, m);
            // リロードの音は高さを変えずに、中の音の時刻を詰めて動きと合わせる
            try { ReloadSound.Start(w, m); }
            catch (Exception e) { Core.Log("[reload] audio: " + e.Message); }
            LastForTest = (vanilla, delay, m);
            Core.Log($"[reload] {w.name} {(_empty ? "empty " : "")}reload x{m:0.00}: {vanilla:0.00}s -> {delay:0.00}s");
        }

        // リロードが終わった・武器を持ち替えた：速さを元に戻す
        public static void End(DemoWeapon w, bool actionEnded = false)
        {
            if (actionEnded) EndedAt = Time.time;
            if (_active == null || w == null || _active.Pointer != w.Pointer) return;
            SetAnimSpeed(w, _activeClip, 1f);
            _active = null;
            _activeClip = null;
        }

        // 武器のアニメーションと、腕のアニメーション（リロードの動き）の再生速度
        static void SetAnimSpeed(DemoWeapon w, AnimationClip clip, float speed)
        {
            try { if (w._weaponAnimator != null) w._weaponAnimator.speed = speed; }
            catch (Exception e) { Core.Log("[reload] weapon animator: " + e.Message); }
            try
            {
                var pc = w._playablesController?.TryCast<FPSPlayablesController>();
                if (pc == null) return;
                int n = SetMixer(pc._slotMixer, clip, speed) + SetMixer(pc._overrideMixer, clip, speed);
                if (clip != null && n == 0 && speed != 1f) Core.Log("[reload] arm animation not found");
            }
            catch (Exception e) { Core.Log("[reload] arm animation: " + e.Message); }
        }

        static int SetMixer(Il2CppKINEMATION.FPSAnimationFramework.Runtime.Playables.FPSAnimatorMixer mixer, AnimationClip clip, float speed)
        {
            var list = mixer?._playables;
            if (list == null) return 0;
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i].playable;
                var h = p.GetHandle();
                if (!h.IsValid()) continue;
                // 速さを変える・戻すのはリロードの動きだけ
                if (clip == null || p.GetAnimationClip()?.Pointer != clip.Pointer) continue;
                h.SetSpeed(speed);
                n++;
            }
            return n;
        }

        public static (float vanilla, float delay, float speed) LastForTest;
        public static float StartedAt = -1f, EndedAt = -1f;   // テスト用：最後のリロードの開始と、OnActionEnded の時刻
    }
}
