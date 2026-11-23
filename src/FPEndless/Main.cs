using System;
using System.IO;
using FPModOption;
using MelonLoader;

[assembly: MelonInfo(typeof(FPEndless.Main), "FPEndless", "1.0.0", "Claude")]
[assembly: MelonGame("Kyrylo Burlaka", "Fracture Point Release")]

namespace FPEndless
{
    // エンドレス調整：エンドレス端末で「調整」を選ぶと、敵の強さを 1〜4 倍（0.5 刻み）、数・体力を 1〜2 倍（0.25 刻み）にできる。
    //   上げるほど運（武器・戦利品のレアリティ）が上がり、参加費も上がる（$500 → 最大 $50,000）。
    //   共通の土台（FPModOption）が必要。武器のレアリティはレアリティmod があればその抽選に、無ければゲームの武器の行の選び方に効く
    public class Main : MelonMod
    {
        public const string Version = "1.0.0";
        public static string Dir;
        public static bool Enabled { get; private set; }
        public static string Scene { get; private set; } = "MainMenu";

        public override void OnInitializeMelon()
        {
            Dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "FPEndless");
            Directory.CreateDirectory(Dir);
            Log($"FPEndless {Version} start");
            try
            {
                if (GameVersion.Disabled) { Log("[init] all mods are disabled: endless tuning OFF"); return; }
                if (!Settings.Enabled("FPEndless")) { Log("[init] turned off in the MOD tab: endless tuning OFF"); return; }
                Tuning.Load();
                if (!Patches.Install(HarmonyInstance)) { Log("[init] patches failed: endless tuning OFF"); HarmonyInstance.UnpatchSelf(); return; }
                Link.EndlessLuck = () => Tuning.Active ? Tuning.Luck : 1f;
                Link.EndlessCount = () => Tuning.Active ? Tuning.Count : 1f;
                EndlessTest.Register();
                Enabled = true;
                Log("[init] OK");
            }
            catch (Exception e) { Log("[init] failed: " + e); try { HarmonyInstance.UnpatchSelf(); } catch { } }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Scene = sceneName;
            if (!Enabled) return;
            if (CoopGuard.Off) { Tuning.EndRun("co-op"); return; }
            Tablet.OnSceneLoaded();
            Tuning.OnSceneLoaded(sceneName);
        }

        public override void OnUpdate()
        {
            if (!Enabled || GameVersion.Disabled) return;
            if (CoopGuard.Off) { if (Tuning.Active) Tuning.EndRun("co-op"); return; }
            GameProf.OnUpdate();
            Patches.ResetDepth();
            try { Tablet.Tick(); } catch (Exception e) { Log("[tablet] tick failed: " + e); }
            try { Tuning.Tick(); } catch (Exception e) { Log("[run] tick failed: " + e); }
            Tuning.TickFps(Scene != Core.HubScene && Scene != "MainMenu" && !Link.InSurvival());   // ランの間の重さのログ（30 秒ごと）
        }

        public override void OnLateUpdate() { GameProf.OnLateUpdate(); }

        public static void Log(string msg) => Core.Log("[endl] " + msg);
        public static bool Ja => Core.Ja;
        public static string T(string ja, string en) => Ja ? ja : en;

        public static void Notify(string msg, bool error = false)
        {
            try
            {
                var nm = Refs.Inv?.NotificationsManager;
                if (nm != null) { if (error) nm.NotifyError(msg); else nm.NotifySuccess(msg); }
            }
            catch (Exception e) { Log("[notify] failed: " + e.Message); }
            Log("[notify] " + msg);
        }
    }
}
