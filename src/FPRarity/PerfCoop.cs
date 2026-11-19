using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using FPModOption;

namespace FPRarity
{
    // co-op の更新で増えたゲームの毎フレームの処理（Coop の MonoBehaviour）が、ひとりで遊ぶときに FPS をどれだけ下げているか。
    // エンドレスの最初の階で立ち止まり、Coop の部品を止めた区間と動かした区間を交互に測る（autotest -Scenario perfcoop）
    public static class PerfCoop
    {
        public static List<(string, Action)> Steps()
        {
            var steps = new List<(string, Action)>
            {
                ("start endless run", () => Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EndlessProbe.EnterAndMove()));
            for (int i = 0; i < 15; i++) steps.Add(("wait / pick card", EndlessProbe.PickCard));
            steps.Add(("measure", () => MelonCoroutines.Start(Run())));
            for (int i = 0; i < 70; i++) steps.Add(("wait", () => { if (_done) TestMode.SkipWaitsForTest(); }));
            return steps;
        }

        static bool _done;
        public static bool Done => _done;
        public static void Start() { _done = false; MelonCoroutines.Start(Run()); }

        static List<MonoBehaviour> CoopParts()
        {
            var list = new List<MonoBehaviour>();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MonoBehaviour>()))
            {
                var mb = o.TryCast<MonoBehaviour>();
                if (mb == null || mb.gameObject.scene.name == null) continue;   // シーンに無い（プレハブ）は除く
                var ns = mb.GetIl2CppType()?.Namespace ?? "";
                var full = mb.GetIl2CppType()?.FullName ?? "";
                if (ns.StartsWith("Coop") || full.StartsWith("Coop")) list.Add(mb);
            }
            return list;
        }

        static IEnumerator Run()
        {
            _done = false;
            var player = UnityEngine.Object.FindObjectsOfType<Il2CppBallistics.LivingEntity>().FirstOrDefault(e => e.PlayerHealthManager != null);
            if (player != null) player.PlayerHealthManager.invincible = true;
            var parts = CoopParts();
            var was = parts.Select(p => p.enabled).ToList();
            Main.Log($"[perfcoop] co-op parts in the scene: {parts.Count}: " + string.Join(", ", parts.GroupBy(p => p.GetIl2CppType().FullName + (p.enabled ? "" : "(off)")).Select(g => $"{g.Key} x{g.Count()}")));
            yield return new WaitForSeconds(2f);
            var on = new List<float>(); var off = new List<float>();
            for (int round = 0; round < 8; round++)
            {
                bool coopOn = round % 2 == 0;
                for (int i = 0; i < parts.Count; i++) if (parts[i] != null) parts[i].enabled = coopOn && was[i];
                yield return new WaitForSeconds(0.5f);
                var ft = new List<float>();
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 5f) { yield return null; ft.Add(Time.unscaledDeltaTime * 1000f); }
                ft.Sort();
                float med = ft[ft.Count / 2], avg = ft.Average(), p99 = ft[(int)(ft.Count * 0.99f)];
                (coopOn ? on : off).Add(avg);
                Main.Log($"[perfcoop] round {round} co-op {(coopOn ? "ON " : "OFF")}: frames={ft.Count} avg={avg:F2}ms ({1000f / avg:F1} fps) median={med:F2}ms p99={p99:F2}ms");
            }
            for (int i = 0; i < parts.Count; i++) if (parts[i] != null) parts[i].enabled = was[i];
            Main.Log($"[perfcoop] RESULT co-op ON avg {on.Average():F2}ms ({1000f / on.Average():F1} fps)  OFF avg {off.Average():F2}ms ({1000f / off.Average():F1} fps)  cost {on.Average() - off.Average():F2}ms/frame");
            if (player != null) player.PlayerHealthManager.invincible = false;
            _done = true;
        }
    }
}
