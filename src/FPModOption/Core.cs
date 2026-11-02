using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FPModOption.ModOption), "FPModOption", "1.0.0", "Claude")]
[assembly: MelonGame("Kyrylo Burlaka", "Fracture Point Release")]
// 他の Mod より先に初期化する（設定・共通の仕組みを先に用意する）
[assembly: MelonPriority(-1000)]

namespace FPModOption
{
    // Mod 共通の土台（常に有効）。
    //   ・リーダーボード送信の停止（失敗したらすべての Mod を止める）
    //   ・設定画面の「MOD」タブと、Mod ごとのオン/オフ（再起動で反映）
    //   ・各 Mod が使う共通の仕組み（ログ・武器の参照・手持ちの武器への反映・自動テスト）
    public class ModOption : MelonMod
    {
        public const string Version = "1.0.0";

        public override void OnInitializeMelon()
        {
            Core.Init();
            Core.Log($"FPModOption {Version} start");
            TestMode.Init();
            Settings.Load();
            try
            {
                GameVersion.Check();
                // リーダーボードの送信停止が効かないなら、Mod を使ったランが送信されてしまうので全体を止める
                Leaderboard.Install(HarmonyInstance);
                if (!Leaderboard.Active) { GameVersion.Disable("リーダーボード送信の停止に失敗"); return; }
                Weapons.Install(HarmonyInstance);
                ReloadSpeed.Install(HarmonyInstance);
                ModTab.Install(HarmonyInstance);
                Core.Log("[init] OK" + (GameVersion.Untested ? " (UNTESTED game build)" : ""));
            }
            catch (Exception e) { GameVersion.Disable("初期化中の例外: " + e.GetType().Name); Core.Log(e.ToString()); }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Core.Log($"SCENE {buildIndex} {sceneName}");
            Core.SetScene(sceneName);
            Refs.Clear();
            Weapons.OnSceneChanged();
            ModTab.OnSceneChanged();
            TestMode.OnSceneLoaded(sceneName);
        }

        public override void OnUpdate()
        {
            GameVersion.NotifyIfNeeded(Core.Scene);
            TestMode.Update();
            try { ModTab.Tick(); } catch (Exception e) { Core.LogOnce("modtab", "[modtab] tick failed: " + e); }
            Slide.Tick();
            if (!GameVersion.Disabled) try { AdsSpeed.Tick(); } catch (Exception e) { Core.LogOnce("ads", "[ads] tick failed: " + e.Message); }
        }

        public override void OnApplicationQuit() => Settings.Save();
    }

    public static class Core
    {
        // 共通のデータ置き場（設定・ログ）
        public static string Dir;
        static string _scene = "MainMenu";
        public static string Scene => _scene;
        public const string HubScene = "Example_Scene_Night 1";   // 拠点（自分の部屋）
        public const string RunScene = "URP_OfficeCorridor";      // ランの階（エンドレスも同じシーン）
        static StreamWriter _log;

        internal static void Init()
        {
            Dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "FPModOption");
            Directory.CreateDirectory(Dir);
            // 前回のログは 1 世代だけ残す（プレイ後に再起動しても前回分を調べられるように）
            var logPath = Path.Combine(Dir, "fpmod.log");
            try { if (File.Exists(logPath)) File.Copy(logPath, Path.Combine(Dir, "fpmod.prev.log"), true); } catch { }
            _log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        }

        internal static void SetScene(string s) => _scene = s;

        // すべての Mod のログ（1 つのファイルにまとめる。各 Mod は "[surv]" などを付けて書く）
        public static void Log(string msg)
        {
            try { _log?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}"); } catch { }
        }

        static readonly HashSet<string> _once = new();
        public static void LogOnce(string key, string msg) { if (_once.Add(key)) Log(msg); }

        // ゲームの言語が日本語か（日本語以外は英語で出す）
        public static bool Ja
        {
            get { try { return Il2CppGameLocalization.LocManager.Code?.StartsWith("ja") == true; } catch { return false; } }
        }
        public static string T(string ja, string en) => Ja ? ja : en;

        public static IEnumerable<T> FindAll<T>() where T : UnityEngine.Object
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
            {
                var t = o.TryCast<T>();
                if (t == null) continue;
                var comp = t.TryCast<Component>();
                if (comp != null && !comp.gameObject.scene.IsValid()) continue; // プレハブ等シーン外は除外
                yield return t;
            }
        }
    }
}
