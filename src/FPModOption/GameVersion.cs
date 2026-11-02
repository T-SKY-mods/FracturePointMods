using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace FPModOption
{
    // ゲームのアップデート検知と、Mod の有効/無効状態の通知（すべての Mod で共通。Disabled ならどの Mod も何もしない）
    public static class GameVersion
    {
        public static string BuildGuid { get; private set; } = "?";
        public static bool Untested { get; private set; }
        public static string DisabledReason { get; private set; }
        public static bool Disabled => DisabledReason != null;
        static bool _notified;

        // アップデートで一部の機能だけ動かなくなったもの（Mod 全体は動く）。画面に一度知らせる
        static readonly System.Collections.Generic.List<string> _degraded = new();
        static int _degradedNotified;
        public static System.Collections.Generic.IReadOnlyList<string> Degraded => _degraded;
        public static int DegradedNotified => _degradedNotified;

        public static void AddDegraded(string feature)
        {
            if (_degraded.Contains(feature)) return;
            _degraded.Add(feature);
            Core.Log("[version] FEATURE DISABLED: " + feature);
        }

        public static void Check()
        {
            try
            {
                var boot = Path.Combine(Application.dataPath, "boot.config");
                var line = File.ReadAllLines(boot).FirstOrDefault(l => l.StartsWith("build-guid="));
                BuildGuid = line?.Substring("build-guid=".Length).Trim() ?? "?";
            }
            catch (Exception e) { Core.Log("[version] read failed: " + e.Message); }

            Untested = BuildGuid != TestedVersion.BuildGuid;
            Core.Log($"[version] game build-guid={BuildGuid} tested={TestedVersion.BuildGuid} ({(Untested ? "UNTESTED" : "ok")})");

        }

        public static void Disable(string reason)
        {
            DisabledReason ??= reason;
            Core.Log("[version] ALL MODS DISABLED: " + reason);
        }

        // 停止した機能の名前（記録は日本語）を、日本語以外の言語では英語で出す
        static readonly Dictionary<string, string> EnglishNames = new()
        {
            ["エンドレスのボス部屋"] = "endless boss floors", ["サイト"] = "optics", ["ホロサイト"] = "holo sight",
            ["スライディングの調整"] = "slide changes", ["ハンドガンの動き"] = "handgun movement", ["売値の補正"] = "sell price", ["拾う時の性能表示"] = "stats on pickup",
            ["熟練度のキル計測"] = "mastery kill counting", ["熟練度画面"] = "mastery page", ["熟練度画面の Esc で戻る"] = "Esc on the mastery page",
            ["テスト用の模擬停止"] = "simulated failure (test)", ["保管庫・インベントリの性能表示"] = "stats popup on the stash / inventory screen",
        };

        static string English(string name)
        {
            if (EnglishNames.TryGetValue(name, out var en)) return en;
            if (name.StartsWith("サイト（")) return "optics (" + name.Substring(4).TrimEnd('）').Replace("、", ", ") + ")";   // 武器名はそのまま
            return name;
        }

        // 拠点・ダンジョンに入ったら一度だけ画面に知らせる
        public static void NotifyIfNeeded(string scene)
        {
            if (!Disabled && _degradedNotified < _degraded.Count && scene != "MainMenu")
            {
                var dn = Refs.Inv?.NotificationsManager;
                if (dn != null)
                {
                    _degradedNotified = _degraded.Count;
                    var msg = Core.Ja
                        ? "MOD: ゲーム更新の影響で一部機能が停止中（" + string.Join("、", _degraded) + "）"
                        : "MOD: some features are off after a game update (" + string.Join(", ", _degraded.Select(English)) + ")";
                    try { dn.NotifyWithColor(msg, new Color(1f, 0.6f, 0.2f)); } catch { }
                }
            }
            if (_notified || scene == "MainMenu" || (!Disabled && !Untested)) return;
            var nm = Refs.Inv?.NotificationsManager;
            if (nm == null) return;
            _notified = true;
            try
            {
                if (Disabled) nm.NotifyError(Core.Ja ? "MOD: ゲーム更新に未対応のため無効化中（バニラで動作）"
                                                                 : "MOD: turned off - not compatible with this game update (playing vanilla)");
                else nm.NotifyWithColor(Core.Ja ? "MOD: 未検証のゲームバージョンで動作中"
                                                           : "MOD: running on an untested game version", new Color(1f, 0.8f, 0.2f));
            }
            catch { }
        }
    }
}
