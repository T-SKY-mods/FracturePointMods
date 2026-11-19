using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using FPModOption;

namespace FPRarity
{
    // co-op の毎フレームの処理のうち、どれが重いか（ひとりで遊んでいるとき）。CoopBootstrap の Update・LateUpdate・OnGUI と、
    // そこから呼ばれる各 Tick（SafeTick は名前ごと）の時間を測る（autotest -Scenario coopprof）
    public static class CoopProfile
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
            steps.Add(("timers", Install));
            for (int i = 0; i < 14; i++) steps.Add(("measure", () => { }));
            steps.Add(("report", Report));
            steps.Add(("each part off in turn", () => MelonCoroutines.Start(PartsOff())));
            for (int i = 0; i < 50; i++) steps.Add(("wait", () => { if (_partsDone) TestMode.SkipWaitsForTest(); }));
            return steps;
        }

        sealed class Stat { public double Total, Max; public int Calls, Slow; }
        static readonly Dictionary<string, Stat> _stats = new();
        static readonly Stack<(string, long)> _stack = new();
        static float _t0;
        static int _f0;

        static Type Find(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!a.GetName().Name.Contains("Assembly-CSharp")) continue;
                try { var t = a.GetTypes().FirstOrDefault(x => x.FullName == fullName); if (t != null) return t; } catch { }
            }
            return null;
        }

        static void Install()
        {
            var h = new HarmonyLib.Harmony("fprarity.coopprof");
            var pre = new HarmonyLib.HarmonyMethod(typeof(CoopProfile), nameof(Pre));
            var post = new HarmonyLib.HarmonyMethod(typeof(CoopProfile), nameof(Post));
            int n = 0;
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            void P(string type, string names)
            {
                var t = Find(type);
                if (t == null) { Main.Log($"[coopprof] type {type} not found"); return; }
                foreach (var nm in names.Split(','))
                    foreach (var m in t.GetMethods(all).Where(x => x.Name == nm))
                        try { h.Patch(m, pre, post); n++; } catch (Exception e) { Main.Log($"[coopprof] {type}.{nm}: {e.Message}"); }
            }
            P("Il2CppCoop.CoopBootstrap", "Update,LateUpdate,OnGUI");
            foreach (var c in new[] { "CoopLifecycle", "CoopSpectatorView", "CoopNotifications", "CoopLootPings", "CoopPing", "CoopAudio", "CoopVoice", "CoopChat", "CoopDebug", "CoopConsole",
                                      "CoopApartment", "CoopSlowdown", "CoopOutfit", "CoopWeapons", "CoopEnemies", "CoopFloors", "CoopLoot", "CoopHub", "CoopWorld", "CoopCombat", "CoopRagdolls",
                                      "CoopSession", "CoopMenu", "CoopHud", "CoopFinalBoss", "CoopEquipmentBags" })
                P("Il2CppCoop." + c, "Tick,LateTick,BossTick,Census,AimLateUpdate,Draw,DrawMarkers,DrawWavePreview,DrawBanner,AutoHostTick");
            P("Il2CppCoop.CoopLobbyUI", "Update,LateUpdate");
            P("Il2CppCoop.CoopLobbySettingsUI", "Update,LateUpdate");
            P("Il2CppCoop.Proposals.PartnerTracers+Driver", "Update,LateUpdate");
            P("Il2CppCoop.Proposals.WaveHudProbe+Driver", "Update,LateUpdate");
            P("Il2Cpp.SteamManager", "EnsureInitialized");
            // SafeTick は渡された名前ごとに
            var sb = Find("Il2CppCoop.CoopBootstrap");
            var st = sb?.GetMethod("SafeTick", all);
            if (st != null) try { h.Patch(st, new HarmonyLib.HarmonyMethod(typeof(CoopProfile), nameof(SafePre)), new HarmonyLib.HarmonyMethod(typeof(CoopProfile), nameof(SafePost))); n++; } catch (Exception e) { Main.Log("[coopprof] SafeTick: " + e.Message); }
            Main.Log($"[coopprof] timers on {n} methods");
            _stats.Clear(); _t0 = Time.realtimeSinceStartup; _f0 = Time.frameCount;
        }

        static void Pre(System.Reflection.MethodBase __originalMethod) => _stack.Push((__originalMethod.DeclaringType.Name + "." + __originalMethod.Name, Stopwatch.GetTimestamp()));
        static void Post() { if (_stack.Count > 0) { var (k, t) = _stack.Pop(); Add(k, t); } }
        static void SafePre(string name) => _stack.Push(("SafeTick:" + name, Stopwatch.GetTimestamp()));
        static void SafePost() => Post();

        static void Add(string key, long start)
        {
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (!_stats.TryGetValue(key, out var s)) _stats[key] = s = new Stat();
            s.Total += ms; s.Calls++; if (ms > s.Max) s.Max = ms; if (ms > 1.0) s.Slow++;
        }

        static void Report()
        {
            float secs = Time.realtimeSinceStartup - _t0; int frames = Math.Max(1, Time.frameCount - _f0);
            Main.Log($"[coopprof] {secs:0.0}s, {frames} frames");
            foreach (var kv in _stats.OrderByDescending(k => k.Value.Total))
                Main.Log($"[coopprof] {kv.Key,-45} {kv.Value.Total / frames:F4} ms/frame  calls={kv.Value.Calls}  max={kv.Value.Max:F2}ms  over1ms={kv.Value.Slow}");
        }

        static bool _partsDone;
        static IEnumerator PartsOff()
        {
            _partsDone = false;
            var parts = new List<MonoBehaviour>();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MonoBehaviour>()))
            {
                var mb = o.TryCast<MonoBehaviour>();
                if (mb == null || mb.gameObject.scene.name == null || !mb.enabled) continue;
                var full = mb.GetIl2CppType()?.FullName ?? "";
                if (full.StartsWith("Coop")) parts.Add(mb);
            }
            for (int r = 0; r < parts.Count * 2 + 2; r++)
            {
                int off = r % 2 == 0 ? -1 : r / 2;   // 偶数回 = 全部動かす、奇数回 = 1 つだけ止める（最後は全部止める）
                bool allOff = off >= parts.Count;
                for (int i = 0; i < parts.Count; i++) parts[i].enabled = !(allOff || i == off);
                yield return new WaitForSeconds(0.5f);
                var ft = new List<float>();
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 4f) { yield return null; ft.Add(Time.unscaledDeltaTime * 1000f); }
                ft.Sort();
                string what = off < 0 ? "all on" : allOff ? "all off" : parts[off].GetIl2CppType().FullName + " off";
                Main.Log($"[coopprof] {what,-50} avg={ft.Average():F2}ms p99={ft[(int)(ft.Count * 0.99f)]:F2}ms max={ft[ft.Count - 1]:F2}ms frames={ft.Count}");
            }
            foreach (var p in parts) p.enabled = true;
            _partsDone = true;
        }
    }
}
