using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FPOptimize
{
    // 敵の声（EnemySoundController の音のリスト）を、階の始まりにまとめて読み込んでおく（2026-11-23）。
    //   声の音は「初めて鳴らすときに読み込む」設定のものがあり、撃ち合いの最中に初めて鳴る声で 1 回 約 13ms 止まる。
    //   シーンや階が始まって少ししてから、読み込み済みの敵の元データ（後から出てくる敵の分も含む）の声のうち、
    //   まだ読み込まれていないものを読み込む。鳴らす音・鳴らし方はゲームのまま。
    //   1 つ読むのに数 ms かかる（初めての階で 100 個ほど・合わせて約 0.5 秒）ので、1 フレームに約 2ms ずつ分けて読む。
    public static class VoicePreload
    {
        public static bool On = true;
        static bool _active;
        static int _wait = -1;              // あと何フレームで読み込むか（-1 = 予定なし）
        static long _runs, _clips, _loaded, _already, _failed;
        static string _states = "";
        static readonly Queue<AudioClip> _queue = new();
        static double _loadMs; static long _loadFrames;
        const double BudgetMs = 2.0;

        public static void Install(HarmonyLib.Harmony _)
        {
            var h = new HarmonyLib.Harmony("FPOptimize.VoicePreload");
            if (!SafePatcher.Patch(h, typeof(Il2Cpp.LevelElevator), "SetupEndlessFloor", null, typeof(VoicePreload), null, nameof(FloorPost)))
                Main.Log("[voice] no floor hook: only on scene load");
            _active = true;
            Main.Log("[voice] ON");
        }

        public static void OnSceneLoaded() { _queue.Clear(); if (_active) _wait = 90; }
        static void FloorPost() { _wait = 90; }

        public static void Tick()
        {
            if (_queue.Count > 0) LoadSome();
            if (_wait < 0 || --_wait > 0) return;
            _wait = -1;
            if (!_active || !On || !Main.Enabled) return;
            try { Run(); } catch (Exception e) { Main.Log("[voice] failed: " + e.Message); }
        }

        static void Run()
        {
            _runs++;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var seen = new HashSet<IntPtr>();
            var byState = new Dictionary<string, int>();
            int controllers = 0, queued = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<Il2Cpp.EnemySoundController>())
            {
                controllers++;
                foreach (var list in new[] { c.EyeContactSounds, c.HearAudioSounds, c.DamageTakenSounds, c.WhileFiringSounds, c.DeadbodySounds, c.GrenadeSounds })
                {
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var clip = list[i];
                        if (clip == null || !seen.Add(clip.Pointer)) continue;
                        _clips++;
                        var key = $"{clip.loadType}/{clip.loadState}{(clip.preloadAudioData ? "" : "/lazy")}";
                        byState[key] = byState.TryGetValue(key, out var n) ? n + 1 : 1;
                        if (clip.loadState == AudioDataLoadState.Loaded || clip.loadState == AudioDataLoadState.Loading) { _already++; continue; }
                        _queue.Enqueue(clip); queued++;
                    }
                }
            }
            _states = string.Join(", ", byState);
            Main.Log($"[voice] {controllers} enemies (incl. not yet spawned), {seen.Count} clips: {_states}; to load {queued} (scan {sw.Elapsed.TotalMilliseconds:0}ms)");
        }

        // 予算の分だけ読む（少なくとも 1 つ）
        static void LoadSome()
        {
            if (!_active || !On || !Main.Enabled) { _queue.Clear(); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _loadFrames++;
            while (_queue.Count > 0)
            {
                var clip = _queue.Dequeue();
                try
                {
                    if (clip == null || clip.WasCollected || clip.loadState != AudioDataLoadState.Unloaded) continue;
                    if (clip.LoadAudioData()) { _loaded++; _bytes += (long)clip.samples * clip.channels * 2; } else _failed++;
                }
                catch { _failed++; }
                if (sw.Elapsed.TotalMilliseconds >= BudgetMs) break;
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            _loadMs += ms; if (ms > _maxFrameMs) _maxFrameMs = ms;
            if (_queue.Count == 0) Main.Log($"[voice] loaded: {Stats()}");
        }
        static long _bytes; static double _maxFrameMs;

        public static string Stats() => $"runs={_runs} clips={_clips} loaded={_loaded} (~{_bytes / 1048576.0:0.0} MB, {_loadMs:0}ms over {_loadFrames} frames, max {_maxFrameMs:0.0}ms/frame) already={_already} failed={_failed} waiting={_queue.Count} last: {_states}";
        public static long[] TestStats() => new[] { _runs, _clips, _loaded, _already, _failed };
    }
}
