using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppGameLocalization;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FPOptimize
{
    // ゲーム本体の翻訳処理（GameLocalization）は 2 秒ごとに
    //   ① 読み込み済みの全フォントに日本語などの予備フォントを追加（PatchLoadedFontAssets）
    //   ② シーン内の全 TextMeshPro に翻訳フックを付ける（LocTextHook.ScanAll）
    //   ③ シーン内の全 旧式UI.Text を翻訳対象に登録（LocLegacyText.Scan）
    // を行う。どれも「プレハブ等を含むゲーム内の全オブジェクト」を毎回調べるため 1 回 25ms 前後かかり、2 秒ごとのカクつきになる。
    //
    // ここでは、テキストが表示された（OnEnable）時点で同じ登録をして、2 秒ごとの処理は「表示されたことのあるテキストだけ」を見る。
    // シーン読み込み時・言語切り替え時の全体処理はゲームの元の処理のまま。
    public static class LocScan
    {
        static bool _active;
        static bool _inPeriodic;   // LocBootstrap.Update の中（2 秒ごとの処理）
        static bool _inTick;       // LocLegacyText.Tick の中（旧式テキストの一覧を読んでいる最中は一覧に追加しない）
        static bool _callingOriginal;

        static readonly Dictionary<IntPtr, TMP_Text> _tmp = new();
        static readonly HashSet<IntPtr> _fontsOk = new();     // 予備フォント追加済みを確認したフォント
        static readonly HashSet<IntPtr> _fontsTried = new();  // 元の処理を呼んでも追加されなかったフォント（同じシーンでは呼び直さない）
        static readonly List<IntPtr> _dead = new();

        // 計測用
        public static double LastCheapMs;
        static long _cheapCount;
        static double _cheapTotalMs, _cheapMaxMs;
        static int _fontPatchCalls;

        public static void Install(HarmonyLib.Harmony _)
        {
            var h = new HarmonyLib.Harmony("FPOptimize.LocScan");
            var t = typeof(LocScan);
            // 表示時の登録と 2 秒ごとの処理の置き換えは、両方そろったときだけ使う（片方だけでは翻訳漏れになる）
            bool ok = SafePatcher.Patch(h, typeof(TextMeshProUGUI), "OnEnable", null, t, null, nameof(TmpEnablePost))
                   && SafePatcher.Patch(h, typeof(TextMeshPro), "OnEnable", null, t, null, nameof(TmpEnablePost))
                   && SafePatcher.Patch(h, typeof(LocBootstrap), "Update", null, t, nameof(BootUpdatePre), nameof(BootUpdatePost))
                   && SafePatcher.Patch(h, typeof(LocTextHook), "ScanAll", new[] { typeof(bool) }, t, nameof(ScanAllPre))
                   && SafePatcher.Patch(h, typeof(LocLegacyText), "Scan", new[] { typeof(bool) }, t, nameof(LegacyScanPre))
                   && SafePatcher.Patch(h, typeof(LocLegacyText), "Tick", null, t, nameof(TickPre), nameof(TickPost))
                   && SafePatcher.Patch(h, typeof(LocFonts), "PatchLoadedFontAssets", null, t, nameof(FontsPre));
            if (!ok)
            {
                try { h.UnpatchSelf(); } catch (Exception e) { Main.Log("[loc] unpatch failed: " + e.Message); }
                Main.Log("[loc] OFF (patch failed) - the game's own scans stay as they are");
                return;
            }
            _active = true;
            Main.Log("[loc] ON");
        }

        public static void OnSceneLoaded()
        {
            _fontsOk.Clear();
            _fontsTried.Clear();
        }

        public static void Update()
        {
            _inPeriodic = false;   // 念のため（元の Update が例外で抜けた場合）
        }

        static bool On => _active && Main.Enabled && !_callingOriginal;

        // ---- 2 秒ごとの処理の置き換え ----

        static void BootUpdatePre() => _inPeriodic = true;
        static void BootUpdatePost() => _inPeriodic = false;

        static bool FontsPre()
        {
            if (!On || !_inPeriodic)
            {
                // ゲームの元の処理が全フォントを調べ直すので、こちらの記録も最初から
                _fontsOk.Clear();
                _fontsTried.Clear();
                return true;
            }
            var sw = Stopwatch.StartNew();
            try { CheckUsedFonts(); }
            catch (Exception e) { Main.Log("[loc] font check failed: " + e.Message); }
            Account(sw);
            return false;
        }

        // 表示中のテキストが使っているフォントに予備フォントが付いているかを調べ、付いていないものがあればゲームの元の処理を呼ぶ
        static void CheckUsedFonts()
        {
            bool need = false;
            var seen = new HashSet<IntPtr>();
            foreach (var t in _tmp.Values)
            {
                if (!Alive(t) || !t.isActiveAndEnabled) continue;
                var f = t.font;
                if (f == null || !seen.Add(f.Pointer)) continue;
                if (FontLacks(f)) need = true;
            }
            if (need) CallOriginalFontPatch("periodic");
        }

        static bool FontLacks(TMP_FontAsset f)
        {
            if (_fontsTried.Contains(f.Pointer)) return false;
            if (LocFonts.IsOurs(f) || LocFonts.HasOurs(f.fallbackFontAssetTable)) { _fontsOk.Add(f.Pointer); return false; }
            _fontsOk.Remove(f.Pointer);
            return true;
        }

        static void CallOriginalFontPatch(string why)
        {
            _callingOriginal = true;
            try { LocFonts.PatchLoadedFontAssets(); }
            catch (Exception e) { Main.Log("[loc] PatchLoadedFontAssets failed: " + e.Message); }
            finally { _callingOriginal = false; }
            _fontPatchCalls++;
            // 呼んでも付かなかったフォント（ゲーム側が対象外にしているもの）は、このシーンでは呼び直さない
            foreach (var t in _tmp.Values)
            {
                if (!Alive(t) || !t.isActiveAndEnabled) continue;
                var f = t.font;
                if (f == null) continue;
                if (!LocFonts.IsOurs(f) && !LocFonts.HasOurs(f.fallbackFontAssetTable)) _fontsTried.Add(f.Pointer);
            }
            Main.Log($"[loc] font patch ({why}); still lacking: {_fontsTried.Count}");
        }

        static bool ScanAllPre(bool regenerate, ref int __result)
        {
            if (!On || !_inPeriodic || regenerate) return true;
            var sw = Stopwatch.StartNew();
            int added = 0;
            try
            {
                bool diag = LocManager.Active != null;
                _dead.Clear();
                foreach (var kv in _tmp)
                {
                    var t = kv.Value;
                    if (!Alive(t)) { _dead.Add(kv.Key); continue; }
                    if (!t.gameObject.scene.IsValid()) continue;
                    if (LocTextHook.Install(t)) added++;
                    if (!t.isActiveAndEnabled) continue;
                    var pre = t.textPreprocessor;
                    if (pre == null || pre.TryCast<LocTextHook>() == null) continue;
                    if (diag) LocDiag.Check(t);   // ゲーム側の翻訳漏れ診断（ログのみ）
                }
                foreach (var d in _dead) _tmp.Remove(d);
            }
            catch (Exception e) { Main.Log("[loc] scan failed: " + e.Message); }
            __result = added;
            Account(sw);
            return false;
        }

        // ゲームは毎フレーム LocLegacyText.Scan を呼び、中で 1.5 秒ごとに全 UI.Text を調べている。
        // 代わりに、Unity が「表示中で文字を描く UI.Text」を管理している一覧（FontUpdateTracker）から未登録のものを登録する（毎フレーム・数十件）。
        // ※ UI.Text.OnEnable への差し込みは、UnityExplorer の起動処理と組み合わさると落ちることがあったので使わない
        static bool LegacyScanPre(bool force)
        {
            if (!On || !_inPeriodic || force) return true;
            if (!_inTick) RegisterShownLegacy();
            return false;
        }

        static void RegisterShownLegacy()
        {
            try
            {
                var tracked = FontUpdateTracker.m_Tracked;
                if (tracked == null) return;
                foreach (var kv in tracked)
                {
                    var set = kv.Value;
                    if (set == null) continue;
                    foreach (var t in set) AddLegacy(t);
                }
            }
            catch (Exception e) { Main.Log("[loc] legacy list failed: " + e.Message); }
        }

        static void TickPre() => _inTick = true;
        static void TickPost() => _inTick = false;

        static void Account(Stopwatch sw)
        {
            LastCheapMs = sw.Elapsed.TotalMilliseconds;
            _cheapCount++;
            _cheapTotalMs += LastCheapMs;
            if (LastCheapMs > _cheapMaxMs) _cheapMaxMs = LastCheapMs;
        }

        // ---- 表示された時点での登録 ----

        static void TmpEnablePost(TMP_Text __instance)
        {
            if (!_active) return;
            try
            {
                var t = __instance;
                if (t == null) return;
                _tmp[t.Pointer] = t;   // 計測で一時的に無効にしている間も登録だけはしておく
                if (!Main.Enabled || _callingOriginal) return;
                if (!t.gameObject.scene.IsValid()) return;
                LocTextHook.Install(t);
                // 初めて見るフォントで予備フォントが付いていなければ、描画される前に付ける
                var f = t.font;
                if (f != null && !_fontsOk.Contains(f.Pointer) && FontLacks(f)) CallOriginalFontPatch("new font " + f.name);
            }
            catch (Exception e) { Main.Log("[loc] tmp enable failed: " + e.Message); }
        }

        // LocLegacyText.Scan が 1 つのテキストに対して行うことと同じ
        static void AddLegacy(Text t)
        {
            try
            {
                if (!Alive(t) || !t.gameObject.scene.IsValid()) return;
                var tracked = LocLegacyText.Tracked;
                if (tracked == null || tracked.ContainsKey(t)) return;
                var root = t.transform.root;
                if (Alive(root))
                {
                    var n = root.name;
                    if (n.StartsWith("[Graphy]") || n.StartsWith("Quantum Console")) return;
                }
                tracked[t] = new LocLegacyText.Entry();
            }
            catch (Exception e) { Main.Log("[loc] legacy add failed: " + e.Message); }
        }

        static bool Alive(UnityEngine.Object o) => o != null && !o.WasCollected;

        // ---- 検証モード：元の全体スキャンと比べて取りこぼしがないか ----

        static float _nextVerify;
        public static void VerifyTick()
        {
            if (!_active || !Main.Enabled || Time.realtimeSinceStartup < _nextVerify) return;
            _nextVerify = Time.realtimeSinceStartup + 10f;
            try
            {
                int total = 0, active = 0, missReg = 0, missInstall = 0, inactiveUninstalled = 0;
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_Text>()))
                {
                    var t = o.TryCast<TMP_Text>();
                    if (t == null || !t.gameObject.scene.IsValid()) continue;
                    total++;
                    bool act = t.isActiveAndEnabled;
                    var pre = t.textPreprocessor;
                    bool ours = pre != null && pre.TryCast<LocTextHook>() != null;
                    if (!act) { if (!ours) inactiveUninstalled++; continue; }
                    active++;
                    if (!_tmp.ContainsKey(t.Pointer)) { missReg++; if (missReg <= 3) Main.Log("[verify] TMP not registered: " + PathOf(t.transform)); }
                    if (!ours && LocTextHook.Install(t)) { missInstall++; if (missInstall <= 3) Main.Log("[verify] TMP not installed: " + PathOf(t.transform)); }
                }

                int legacyActive = 0, legacyMiss = 0;
                var tracked = LocLegacyText.Tracked;
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Text>()))
                {
                    var t = o.TryCast<Text>();
                    if (t == null || !t.gameObject.scene.IsValid() || !t.isActiveAndEnabled) continue;
                    var n = t.transform.root.name;
                    if (n.StartsWith("[Graphy]") || n.StartsWith("Quantum Console")) continue;
                    legacyActive++;
                    if (!tracked.ContainsKey(t)) { legacyMiss++; if (legacyMiss <= 3) Main.Log("[verify] legacy Text not tracked: " + PathOf(t.transform)); }
                }

                // 表示中のテキストが使っているフォントで、予備フォントが付いていないもの
                var used = new Dictionary<IntPtr, string>();
                foreach (var t in _tmp.Values)
                    if (Alive(t) && t.isActiveAndEnabled && t.font != null) used[t.font.Pointer] = t.font.name;
                int fontLack = 0, fontLackUsed = 0;
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>()))
                {
                    var f = o.TryCast<TMP_FontAsset>();
                    if (f == null || LocFonts.IsOurs(f) || LocFonts.HasOurs(f.fallbackFontAssetTable)) continue;
                    fontLack++;
                    if (used.ContainsKey(f.Pointer)) { fontLackUsed++; Main.Log("[verify] USED font without fallbacks: " + f.name); }
                }

                Main.Log($"[verify] loc tmp={total} active={active} missReg={missReg} missInstall={missInstall} inactiveUninstalled={inactiveUninstalled} " +
                         $"legacyActive={legacyActive} legacyMiss={legacyMiss} fontsLacking={fontLack} usedLacking={fontLackUsed} " +
                         $"registry={_tmp.Count} cheapRuns={_cheapCount} avg={(_cheapCount > 0 ? _cheapTotalMs / _cheapCount : 0):F2}ms max={_cheapMaxMs:F2}ms fontPatches={_fontPatchCalls}");
            }
            catch (Exception e) { Main.Log("[verify] loc failed: " + e); }
        }

        static string PathOf(Transform tr)
        {
            var s = tr.name;
            for (var p = tr.parent; p != null && s.Length < 200; p = p.parent) s = p.name + "/" + s;
            return s;
        }
    }
}
