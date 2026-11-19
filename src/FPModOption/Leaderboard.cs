using System;
using System.Linq;
using Il2CppFPLeaderboard.Unity;
using Il2CppSystem.Threading;
using Task = Il2CppSystem.Threading.Tasks.Task;

namespace FPModOption
{
    // Mod使用中のランがオンラインのリーダーボードに記録されないようにする。
    //
    // ラン開始・階層クリア・ラン終了の送信は GameApiClient の3関数が行う。呼び出し元（エレベーター、死亡処理）は
    // 送信の完了を待ってから後続処理（死亡時のリセット等）を行うため、失敗させるとゲーム進行が壊れる恐れがある。
    // そこで通信だけを止め、「成功したが、ランキング対象外」という結果を即座に返す。
    // ランキングの閲覧（読み取り）は止めない。
    public static class Leaderboard
    {
        public static bool Active;

        public static void Install(HarmonyLib.Harmony h)
        {
            var t = typeof(GameApiClient);
            var cls = typeof(Leaderboard);
            bool a = SafePatcher.Patch(h, t, "StartRunAsync", null, nameof(StartRun_Pre), null, cls, gate: false);
            bool b = SafePatcher.Patch(h, t, "CompleteFloorAsync", null, nameof(CompleteFloor_Pre), null, cls, gate: false);
            bool c = SafePatcher.Patch(h, t, "FinishRunAsync", null, nameof(FinishRun_Pre), null, cls, gate: false);
            Active = a && b && c;
            // 保険：上の 3 つ以外の送信（今後の更新で増えるアリーナのスコア送信など）も、読み取り（GET）以外は送らない
            SendNetOn = SafePatcher.Patch(h, t, "SendAuthorizedAsync", null, nameof(SendAuthorized_Pre), null, cls, gate: false);
            if (!SendNetOn) Core.Log("[leaderboard] safety net for other submissions not installed (the 3 known ones are still blocked)");
            Core.Log(Active ? "[leaderboard] submission blocked (start/floor/finish)"
                            : $"[leaderboard] WARNING: block incomplete start={a} floor={b} finish={c}");
        }

        static bool StartRun_Pre(ref Il2CppSystem.Threading.Tasks.Task<RankedRunSession> __result)
        {
            try
            {
                var session = new RankedRunSession(Il2CppSystem.Guid.NewGuid(), "fprarity-offline");
                __result = Task.FromResult(session);
                Core.Log("[leaderboard] StartRun intercepted (local session, nothing sent)");
                return false;
            }
            catch (Exception e)
            {
                Core.Log("[leaderboard] StartRun intercept failed, letting original run: " + e);
                return true;
            }
        }

        static bool CompleteFloor_Pre(ref Il2CppSystem.Threading.Tasks.Task<int> __result)
        {
            try
            {
                var svc = Core.FindAll<LeaderboardService>().FirstOrDefault();
                int floor = (svc?.CurrentFloor ?? 0) + 1;
                __result = Task.FromResult(floor);
                Core.Log($"[leaderboard] CompleteFloor intercepted -> {floor}");
                return false;
            }
            catch (Exception e)
            {
                Core.Log("[leaderboard] CompleteFloor intercept failed, letting original run: " + e);
                return true;
            }
        }

        public static bool SendNetOn;
        public static int BlockedOther;

        // 認証済みの送信の入口。読み取り（GET）だけ通し、それ以外は通信せずに「通信に失敗した」ことにする（ゲームはオフラインのときと同じ扱いになる）
        static bool SendAuthorized_Pre(string method, string path, ref Il2CppSystem.Threading.Tasks.Task<string> __result)
        {
            try
            {
                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) return true;
                BlockedOther++;
                __result = Task.FromException<string>(new Il2CppSystem.Exception("FPModOption: leaderboard submission blocked while mods are installed"));
                Core.Log($"[leaderboard] blocked {method} {path} (not sent)");
                return false;
            }
            catch (Exception e)
            {
                Core.Log("[leaderboard] safety net failed, letting original run: " + e.Message);
                return true;
            }
        }

        static bool FinishRun_Pre(long dollars, ref Il2CppSystem.Threading.Tasks.Task<FinishRunResult> __result)
        {
            try
            {
                __result = Task.FromResult(FinishRunResult.NotEligibleForLeaderboard);
                Core.Log($"[leaderboard] FinishRun intercepted (dollars={dollars}) -> NotEligibleForLeaderboard");
                return false;
            }
            catch (Exception e)
            {
                Core.Log("[leaderboard] FinishRun intercept failed, letting original run: " + e);
                return true;
            }
        }
    }
}
