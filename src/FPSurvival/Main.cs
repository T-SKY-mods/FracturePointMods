using System;
using System.IO;
using FPModOption;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FPSurvival.Main), "FPSurvival", "1.0.0", "Claude")]
[assembly: MelonGame("Kyrylo Burlaka", "Fracture Point Release")]

namespace FPSurvival
{
    // サバイバルモード：エンドレス端末の 2 ページ目から始め、通常ランの最終ボスの屋上（ROOFTOP）で敵のウェーブを倒し続ける。
    // 共通の土台（FPModOption）が必要。レアリティmod があれば武器箱の武器にレアリティが付く（無ければふつうの武器）
    public class Main : MelonMod
    {
        public const string Version = "1.0.0";
        public static string Dir;
        public static bool Enabled { get; private set; }
        public static string Scene { get; private set; } = "MainMenu";
        public const string RunScene = "URP_OfficeCorridor";   // ランの階（エンドレスも同じシーン）

        public override void OnInitializeMelon()
        {
            Dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "FPSurvival");
            Directory.CreateDirectory(Dir);
            Log($"FPSurvival {Version} start");
            try
            {
                // すべての Mod が止まっている（未対応のゲーム更新・リーダーボード送信の停止に失敗）・MOD タブでオフなら、何もしない
                if (GameVersion.Disabled) { Log("[init] all mods are disabled: survival mode OFF"); return; }
                if (!Settings.Enabled("FPSurvival")) { Log("[init] turned off in the MOD tab: survival mode OFF (records kept)"); return; }
                if (!Patches.Install(HarmonyInstance)) { Log("[init] patches failed: survival mode OFF"); HarmonyInstance.UnpatchSelf(); return; }
                Stash.Load();
                SurvivalTest.Register();
                SurvivalProbe.Register();
                Enabled = true;
                Log("[init] OK");
            }
            catch (Exception e) { Log("[init] failed: " + e); try { HarmonyInstance.UnpatchSelf(); } catch { } }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Scene = sceneName;
            if (!Enabled) return;
            Tablet.OnSceneLoaded();
            Arena.OnSceneLoaded();
            Stash.OnSceneLoaded();
        }

        public override void OnUpdate()
        {
            if (!Enabled || GameVersion.Disabled) return;
            try { Stash.Tick(); } catch (Exception e) { Log("[stash] tick failed: " + e); }
            try { Tablet.Tick(); } catch (Exception e) { Log("[tablet] tick failed: " + e); }
            try { Arena.Tick(); } catch (Exception e) { Log("[arena] tick failed: " + e); }
        }

        public static void Log(string msg) => Core.Log("[surv] " + msg);

        public static bool Ja => Core.Ja;
        public static string T(string ja, string en) => Ja ? ja : en;

        public static void Notify(string msg, bool error = false)
        {
            try
            {
                var nm = Refs.Inv?.NotificationsManager;
                if (nm == null) return;
                if (error) nm.NotifyError(msg); else nm.NotifySuccess(msg);
            }
            catch (Exception e) { Log("[notify] failed: " + e.Message); }
            Log("[notify] " + msg);
            if (Stash.InRun) { Patches.Notices.Add(msg); if (Patches.Notices.Count > 40) Patches.Notices.RemoveAt(0); }   // テスト用（NotifySuccess は NotifyWithColor の記録に入らない）
        }
    }
}
