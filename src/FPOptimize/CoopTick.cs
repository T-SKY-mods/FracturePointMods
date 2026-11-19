using System;
using Il2CppCoop;
using UnityEngine.SceneManagement;

namespace FPOptimize
{
    // アップデートで入った co-op の処理（CoopBootstrap.Update）は、ひとりで遊んでいるときも毎フレーム動いている。
    // そのうち次の 2 つが、ときどきシーン全体を検索して 1 回 10〜35ms 止まる（カクつき）：
    //   ・"lobby UI"       … co-op ロビー画面の一覧を定期的に探して更新する
    //   ・"delivery boxes" … co-op の仲間と配達ボックスを同期する
    // co-op のセッションが無い（ロビーにも入っていない）・メインメニューではないときは、この 2 つを呼ばない。
    // セッションができたら・メニューに戻ったら元どおり呼ぶ。co-op 中は CoopGuard で何もしない（バニラのまま）。
    public static class CoopTick
    {
        static bool _active;
        static long _skipped;
        public static long Skipped => _skipped;

        public static void Install(HarmonyLib.Harmony _)
        {
            var h = new HarmonyLib.Harmony("FPOptimize.CoopTick");
            if (!SafePatcher.Patch(h, typeof(CoopBootstrap), "SafeTick", null, typeof(CoopTick), nameof(SafeTickPre)))
            {
                Main.Log("[cooptick] OFF (patch failed)");
                return;
            }
            _active = true;
            Main.Log("[cooptick] ON");
        }

        public static string Stats() => $"cooptick skipped={_skipped}";

        static bool SafeTickPre(string name)
        {
            if (!_active || !Main.Enabled) return true;
            if (name != "lobby UI" && name != "delivery boxes") return true;
            try
            {
                if (Coop.Active || CoopSession.HasSession) return true;
                if (SceneManager.GetActiveScene().buildIndex == 0) return true;   // メインメニュー（co-op のメニュー・ロビーがある）
            }
            catch { return true; }
            _skipped++;
            return false;
        }
    }
}
