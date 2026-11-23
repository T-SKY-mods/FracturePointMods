using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using SteamAudioSource = Il2CppSteamAudio.SteamAudioSource;

namespace FPOptimize
{
    // 鳴っていない音源の Steam Audio（立体音響の計算）を休ませる（2026-11-23）。
    //   ゲームでは敵・武器・効果音の音源ごとに SteamAudioSource が付いていて、鳴っていなくても毎フレーム
    //   位置や遮蔽の計算を Steam Audio に渡している。調節したエンドレスの撃ち合いで 1 フレーム約 1.5ms（全体の 1 割以上）。
    //   ・鳴らなくなってから IdleAfter 秒たった音源は、その SteamAudioSource を止める
    //   ・ゲームが音を鳴らす直前（AudioSource の Play 系）に戻す。取りこぼしても毎フレームの見回りで、鳴っていれば戻す
    //   ・物が非表示になったら元の状態（有効）に戻す（次に表示されたときはゲームのまま）
    //   鳴っている間の計算はゲームのまま。
    public static class SteamIdle
    {
        public static bool On = true;            // 計測用（A/B）
        public static float IdleAfter = 1.0f;    // 鳴り終わってから止めるまで（秒）
        static bool _active;

        sealed class Entry { public SteamAudioSource Sas; public AudioSource Src; public GameObject Go; public float SilentSince; public bool OffByUs; }
        static readonly Dictionary<IntPtr, Entry> _bySas = new();
        static readonly Dictionary<IntPtr, Entry> _bySrc = new();
        static readonly List<Entry> _all = new();
        static long _sleeps, _wakes, _lateWakes; static double _toggleMs;

        public static void Install(HarmonyLib.Harmony _)
        {
            var h = new HarmonyLib.Harmony("FPOptimize.SteamIdle");
            // Update は中身が無い（他と共有）。Steam Audio の管理役が毎フレーム音源ごとに呼ぶ SetInputs で見つける
            bool ok = SafePatcher.Patch(h, typeof(SteamAudioSource), "SetInputs", null, typeof(SteamIdle), nameof(SasUpdatePre));
            if (!ok) { try { h.UnpatchSelf(); } catch { } Main.Log("[steamidle] OFF (patch failed)"); return; }
            // 鳴らす直前に戻す（入らないものがあっても見回りで戻る）
            int n = 0;
            if (SafePatcher.Patch(h, typeof(AudioSource), "Play", Type.EmptyTypes, typeof(SteamIdle), nameof(PlayInstPre))) n++;
            if (SafePatcher.Patch(h, typeof(AudioSource), "Play", new[] { typeof(ulong) }, typeof(SteamIdle), nameof(PlayInstPre))) n++;
            if (SafePatcher.Patch(h, typeof(AudioSource), "PlayDelayed", null, typeof(SteamIdle), nameof(PlayInstPre))) n++;
            if (SafePatcher.Patch(h, typeof(AudioSource), "PlayHelper", null, typeof(SteamIdle), nameof(PlayPre))) n++;
            if (SafePatcher.Patch(h, typeof(AudioSource), "PlayOneShotHelper", null, typeof(SteamIdle), nameof(PlayPre))) n++;
            if (SafePatcher.Patch(h, typeof(AudioSource), "PlayScheduled", null, typeof(SteamIdle), nameof(PlayScheduledPre))) n++;
            _active = true;
            Main.Log($"[steamidle] ON ({n}/6 play hooks)");
        }

        public static string Stats() => $"tracked {_all.Count}, sleeping {Sleeping}, slept {_sleeps}, woken before play {_wakes}, woken late {_lateWakes}, toggle cost {_toggleMs:0.0}ms total";
        public static int Sleeping { get { int k = 0; foreach (var e in _all) if (e.OffByUs) k++; return k; } }
        // テスト用：鳴っているのに Steam Audio を休ませたままの音源の数（0 でなければならない）
        public static int PlayingWhileAsleep() { int k = 0; foreach (var e in _all) try { if (e.OffByUs && e.Src != null && e.Src.isPlaying) k++; } catch { } return k; }
        public static long[] TestStats() => new[] { _all.Count, Sleeping, _sleeps, _wakes, _lateWakes };

        // 見つけたら覚える（止めたものは SetInputs が来ないが、覚えたまま）
        static void SasUpdatePre(SteamAudioSource __instance)
        {
            if (!_active || __instance == null) return;
            try
            {
                var p = __instance.Pointer;
                if (_bySas.ContainsKey(p)) return;
                var src = __instance.GetComponent<AudioSource>();
                if (src == null) return;
                var e = new Entry { Sas = __instance, Src = src, Go = __instance.gameObject, SilentSince = Time.time };
                _bySas[p] = e; _bySrc[src.Pointer] = e; _all.Add(e);
            }
            catch { }
        }

        static void PlayPre(AudioSource source) => Wake(source, false);
        static void PlayScheduledPre(AudioSource __instance) => Wake(__instance, false);
        static void PlayInstPre(AudioSource __instance) => Wake(__instance, false);

        static void Wake(AudioSource source, bool late)
        {
            if (!_active || source == null) return;
            try
            {
                if (!_bySrc.TryGetValue(source.Pointer, out var e)) return;
                e.SilentSince = Time.time;
                if (!e.OffByUs) return;
                Toggle(e, true);
                if (late) _lateWakes++; else _wakes++;
            }
            catch { }
        }

        static void Toggle(Entry e, bool on)
        {
            long t = Stopwatch.GetTimestamp();
            e.Sas.enabled = on;
            e.OffByUs = !on;
            _toggleMs += (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency;
        }

        // 毎フレームの見回り
        public static void Tick(bool forceOff = false)
        {
            if (!_active || _all.Count == 0) return;
            float now = Time.time;
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                var e = _all[i];
                try
                {
                    // 消えた物：触ると例外になるので、そこで忘れる（毎フレームの生存確認は省く）
                    if (!On || !Main.Enabled || forceOff)
                    {
                        if (e.OffByUs) Toggle(e, true);
                        continue;
                    }
                    if (e.OffByUs)
                    {
                        // 休ませている物：非表示になったら元へ（次に表示されたときゲームのまま。非表示の物では OnEnable は呼ばれない）
                        if (!e.Go.activeInHierarchy) { e.Sas.enabled = true; e.OffByUs = false; e.SilentSince = now; continue; }
                        if (e.Src.isPlaying) { Toggle(e, true); _lateWakes++; e.SilentSince = now; }
                        continue;
                    }
                    // 動いている物：鳴っているかだけ毎フレーム見る。休ませる前にだけ、表示中か・ゲームが止めていないかを見る
                    if (e.Src.isPlaying) { e.SilentSince = now; continue; }
                    if (now - e.SilentSince <= IdleAfter) continue;
                    if (!e.Go.activeInHierarchy || !e.Sas.enabled) { e.SilentSince = now; continue; }
                    Toggle(e, false); _sleeps++;
                }
                catch { Forget(i); }
            }
        }

        static void Forget(int i)
        {
            var e = _all[i];
            try { if (e.OffByUs && e.Sas != null) e.Sas.enabled = true; } catch { }
            _all.RemoveAt(i);
            foreach (var kv in new List<KeyValuePair<IntPtr, Entry>>(_bySas)) if (kv.Value == e) _bySas.Remove(kv.Key);
            foreach (var kv in new List<KeyValuePair<IntPtr, Entry>>(_bySrc)) if (kv.Value == e) _bySrc.Remove(kv.Key);
        }

        public static void OnSceneLoaded()
        {
            foreach (var e in _all) try { if (e.OffByUs && e.Sas != null) e.Sas.enabled = true; } catch { }
            _all.Clear(); _bySas.Clear(); _bySrc.Clear();
        }
    }
}
