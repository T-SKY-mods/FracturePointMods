using System;
using System.IO;
using FPModOption;
using MelonLoader;

[assembly: MelonInfo(typeof(FPCustom.Main), "FPCustom", "0.0.1", "Claude")]
[assembly: MelonGame("Kyrylo Burlaka", "Fracture Point Release")]

namespace FPCustom
{
    // 武器カスタマイズ（開発中）。保管庫の熟練度の次のページで、壊れた武器部品を使ってアタッチメントを解放・付け外しする。
    // 共通の土台（FPModOption）が必要。レアリティmod があれば熟練度のページと並べる（無ければ保管庫にこの Mod のボタンだけ）。
    // 完成するまでは autotest.ps1 -Dev のときだけ読み込まれる
    public class Main : MelonMod
    {
        public const string Version = "0.0.1";
        public static string Dir;
        public static bool Enabled { get; private set; }

        public override void OnInitializeMelon()
        {
            Dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "FPCustom");
            Directory.CreateDirectory(Dir);
            Log($"FPCustom {Version} start");
            try
            {
                if (GameVersion.Disabled) { Log("[init] all mods are disabled: customization OFF"); return; }
                if (!Settings.Enabled("FPCustom"))
                {
                    Log("[init] turned off in the MOD tab: customization OFF (attachments kept)");
                    // 拡張マガジンの余った弾は捨てる（ゲームのセーブのマガジンは元の大きさで書いてある。オンに戻しても戻さない）
                    Effects.DropExtraRounds();
                    return;
                }
                Store.Load();
                CustomUi.Install(HarmonyInstance);
                CustomUi.HookTooltip();
                Mount.Install(HarmonyInstance);
                Effects.Install(HarmonyInstance);
                Slide.UseRoles(HarmonyInstance);    // スライディング中の回避とメイン武器のエイム制限
                Slide.UsePistol(HarmonyInstance);   // ハンドガンの動き（アタッチメントで変わる）
                Parts.Install(HarmonyInstance);
                Scan.Register();
                CustomTest.Register();
                ShowcaseTest.Register();
                MountTest.Register();
                EffectTest.Register();
                MagRunTest.Register();
                NewPartsTest.Register();
                MuzzleLab.Register();
                MuzzleCheck.Register();
                FixLab.Register();
                Lab.Register();
                LaserLab.Register();
                LaserAdsLab.Register();
                SeatLab.Register();
                StatLab.Register();
                OpticLab.Register();
                GuardLab.Register();
                PartsLab.Register();
                SkinLab.Register();
                AdsLab.Register();
                HandLab.Register();
                AnimLab.Register();
                Enabled = true;
                Log("[init] OK");
            }
            catch (Exception e) { Log("[init] failed: " + e); Slide.StopRoles(); Slide.StopPistol(); try { HarmonyInstance.UnpatchSelf(); } catch { } }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (Enabled) CustomUi.OnSceneChanged();
        }

        public override void OnUpdate()
        {
            if (!Enabled || GameVersion.Disabled) return;
            try { CustomUi.Tick(); } catch (Exception e) { Log("[ui] tick failed: " + e); }
            Effects.Tick();
            Parts.Release();
            DebugMode.Tick();
            HandLab.Tick();
            AnimLab.Tick();
            StatLab.Tick();
        }

        public static void Log(string msg) => Core.Log("[cust] " + msg);
    }
}
