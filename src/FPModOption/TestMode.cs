using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Il2Cpp;
using UnityEngine;

namespace FPModOption
{
    // Claude が単独で動作確認するための自動テスト（すべての Mod で共通の進め方）。
    // UserData/FPModOption/autotest ファイルがあるときだけ動く（通常プレイでは何もしない）。
    // タイトルで「続ける」を押し、拠点で各 Mod が登録した手順を 1.5 秒ごとに実行して結果をログに出す。
    // ※ゲームのセーブが変わるので、テスト用スクリプト側で前後にセーブを退避・復元する。
    public static class TestMode
    {
        static bool _enabled;
        static string _scenario = "";
        public static string Scenario => _scenario;
        public static bool Active => _enabled;
        static bool _skipWait;
        // 待つ必要がなくなった手順（例: 終わった処理の待ち）から呼ぶと、次の手順にすぐ進む
        public static void SkipWaitsForTest() => _skipWait = true;
        static string _scene;
        static float _sceneAt = -1, _nextAt = -1, _quitAt = -1;
        static bool _clicked;
        static int _step = -1;
        static List<(string name, Action act)> _steps;
        static string _lang;

        // テスト中だけオフにする Mod（UserData/FPModOption/autotest_off.txt、カンマ区切り）
        public static readonly List<string> ModsOff = new();
        // テスト中だけ「入っている」ことにする Mod（UserData/FPModOption/autotest_installed.txt。MOD タブの見た目の確認用）
        public static readonly List<string> FakeInstalled = new();

        // 各 Mod の自動テスト手順（名前 → 手順）。"" は名前を指定しないときの手順（FPRarity の基本テスト）
        public static readonly Dictionary<string, Func<List<(string name, Action act)>>> ExternalScenarios = new();
        // タイトル画面のまま実行する手順（設定画面の MOD タブなど）
        public static readonly Dictionary<string, Func<List<(string name, Action act)>>> MenuScenarios = new();

        public static void Init()
        {
            var flag = Path.Combine(Core.Dir, "autotest");
            _enabled = File.Exists(flag);
            if (!_enabled) return;
            _scenario = File.ReadAllText(flag).Trim();
            // "シナリオ@言語"（例 survival@en）：その言語に切り替えて実行し、最後に元に戻す（紹介用の撮影など）
            if (_scenario.Contains('@'))
            {
                var parts = _scenario.Split('@');
                _scenario = parts[0];
                _lang = parts[1];
            }
            var off = Path.Combine(Core.Dir, "autotest_off.txt");
            if (File.Exists(off))
                ModsOff.AddRange(File.ReadAllText(off).Split(new[] { ',', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            var fake = Path.Combine(Core.Dir, "autotest_installed.txt");
            if (File.Exists(fake)) FakeInstalled.AddRange(File.ReadAllText(fake).Split(new[] { ',', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            Core.Log($"[test] AUTOTEST enabled: '{_scenario}'{(ModsOff.Count > 0 ? " mods off: " + string.Join(",", ModsOff) : "")}");
        }

        public static void OnSceneLoaded(string scene)
        {
            _scene = scene;
            _sceneAt = Time.realtimeSinceStartup;
        }

        public static void Check(string what, bool ok) => Core.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");

        public static void Update()
        {
            if (!_enabled || _sceneAt < 0) return;
            var now = Time.realtimeSinceStartup;

            // 公式の Mod ルール：ゲームは強制終了せず、通常の終了処理で閉じる（保存中に強制終了するとセーブが壊れることがある）
            if (_quitAt > 0 && now >= _quitAt)
            {
                _quitAt = -1;
                _enabled = false;
                Core.Log("[test] QUIT (graceful)");
                Application.Quit();
                return;
            }

            // 起動してメインメニューまで行くだけ（起動時の安定性の確認用）
            if (_scenario == "menu")
            {
                if (_scene == "MainMenu" && now - _sceneAt > 12f && _quitAt < 0) { Core.Log("[test] ALL DONE"); _quitAt = now + 1f; }
                return;
            }

            if (MenuScenarios.TryGetValue(_scenario, out var menu))
            {
                if (_scene == "MainMenu" && _step < 0 && now - _sceneAt > 8f) Begin(menu(), now);
            }
            else
            {
                if (_scene == "MainMenu" && !_clicked && now - _sceneAt > 8f)
                {
                    _clicked = true;
                    var cbm = Core.FindAll<ContinueButtonManager>().FirstOrDefault();
                    Core.Log($"[test] clicking Continue (found={cbm != null})");
                    try { cbm?.ContinueButton?.onClick?.Invoke(); } catch (Exception e) { Core.Log("[test] click failed " + e); }
                    return;
                }
                if (_scene != "MainMenu" && _step < 0 && now - _sceneAt > 10f)
                {
                    var steps = ExternalScenarios.TryGetValue(_scenario, out var ext) ? ext() : null;
                    if (steps == null)
                    {
                        Core.Log($"[test] no scenario '{_scenario}' (mod off or not installed)");
                        steps = new List<(string, Action)> { ("nothing to run", () => { }) };
                    }
                    Begin(steps, now);
                }
            }

            if (_steps != null && _step >= 0 && _step < _steps.Count && now >= _nextAt)
            {
                var (name, act) = _steps[_step++];
                Core.Log($"[test] STEP {_step}/{_steps.Count}: {name}");
                _skipWait = false;
                try { act(); } catch (Exception e) { Core.Log($"[test] step failed: {e}"); }
                _nextAt = _skipWait ? now : now + 1.5f;
                if (_step == _steps.Count) { Core.Log("[test] ALL DONE"); _quitAt = now + 3f; }
            }
        }

        static void Begin(List<(string name, Action act)> steps, float now)
        {
            _steps = steps;
            if (!string.IsNullOrEmpty(_lang))
            {
                string original = null;
                _steps.Insert(0, ($"language {_lang}", () =>
                {
                    original = Il2CppGameLocalization.LocManager.Code;
                    Il2CppGameLocalization.LocManager.SetLanguage(_lang);
                    Core.Log($"[test] language {original} -> {Il2CppGameLocalization.LocManager.Code}");
                }));
                _steps.Add(("restore language", () => { if (!string.IsNullOrEmpty(original)) Il2CppGameLocalization.LocManager.SetLanguage(original); }));
            }
            _step = 0;
            _nextAt = now;
        }

        // ゲームの非公開の関数を呼ぶ（テスト用）
        public static object Call(object target, string method, params object[] args)
        {
            var m = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .First(x => x.Name == method && x.GetParameters().Length == args.Length
                            && x.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(b => b));
            return m.Invoke(target, args);
        }
    }
}
