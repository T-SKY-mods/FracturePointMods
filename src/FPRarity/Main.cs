using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FPRarity.Main), "FPRarity", "1.0.0", "Claude")]
[assembly: MelonGame("Kyrylo Burlaka", "Fracture Point Release")]

namespace FPRarity
{
    // レアリティmod：武器のレアリティ・武器熟練度・エンドレスのボス部屋・スライディングの調整。
    // 共通の土台（FPModOption）が必要。設定の MOD タブでオフにすると、次の起動から何もしない（ロール・熟練度の記録は残す）
    public class Main : MelonMod
    {
        public static string Dir;
        public static string Scene => Core.Scene;
        public const string HubScene = Core.HubScene; // 拠点（自分の部屋）
        // この起動で動いているか（オフ・未対応のゲーム更新・必須の差し替えの失敗なら false）
        public static bool Enabled { get; private set; }
        public static string DisabledReason { get; private set; }

        public override void OnInitializeMelon()
        {
            Dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "FPRarity");
            Directory.CreateDirectory(Dir);
            Log("FPRarity 1.0.0 start");
            Init();
            RarityTest.Register();   // 動いているかで登録する手順が変わるので、初期化の後
        }

        void Init()
        {
            if (GameVersion.Disabled) { DisabledReason = "all mods disabled"; Log("[init] all mods are disabled: rarity OFF"); return; }
            if (!Settings.Enabled("FPRarity")) { DisabledReason = "turned off in the MOD tab"; Log("[init] turned off in the MOD tab: rarity OFF (records kept)"); return; }
            try
            {
                BackupOnGameUpdate();

                // 武器の追跡・保存に必要な差し替えが 1 つでも欠けたら、中途半端に動かさずこの Mod を止める
                var failed = Patches.Install(HarmonyInstance);
                var critical = failed.Where(f => !OptionalPatches.Contains(f)).ToList();
                if (failed.Contains("LootableItem.Interact(0)") && failed.Contains("LootableItem.Interact(1)"))
                    critical.Add("LootableItem.Interact");
                critical.RemoveAll(f => f.StartsWith("LootableItem.Interact("));
                if (!Weapons.Ready) critical.Add("weapon apply pipeline");
                if (TestMode.Scenario == "safemode") critical.Add("(simulated update breakage)");
                if (critical.Count > 0) { Disable("必須の差し替えに失敗: " + string.Join(", ", critical)); return; }
                if (failed.Contains("EnemyHealth.Kill")) GameVersion.AddDegraded("熟練度のキル計測");
                if (TestMode.Scenario == "degraded") GameVersion.AddDegraded("テスト用の模擬停止");
                if (failed.Contains("LootableItem.GetTooltipText")) GameVersion.AddDegraded("拾う時の性能表示");
                if (failed.Contains("EquipmentShop.GetWeaponSellPrice")) GameVersion.AddDegraded("売値の補正");

                // 画面表示は欠けても遊べるので、失敗しても続行（ログに残す）
                Persistence.LoadFromDisk();
                Mastery.Load();
                UiPatches.Install(HarmonyInstance);
                if (Features.Mastery) MasteryUi.Install(HarmonyInstance);
                if (Features.Boss) EndlessBoss.Install(HarmonyInstance);
                if (Features.Rarity) LootPatch.Install(HarmonyInstance);
                Log($"[init] features: rarity={(Features.Rarity ? "on" : "off")} mastery={(Features.Mastery ? "on" : "off")} boss={(Features.Boss ? "on" : "off")}");
                RarityLink.Install();
                Enabled = true;
                Log("[init] OK");
            }
            catch (Exception e) { Disable("初期化中の例外: " + e.GetType().Name); Log(e.ToString()); }
        }

        // 欠けても致命的ではない差し替え（表示・売値の補正）
        static readonly HashSet<string> OptionalPatches = new() { "LootableItem.GetTooltipText", "EquipmentShop.GetWeaponSellPrice", "EnemyHealth.Kill" };

        void Disable(string reason)
        {
            DisabledReason = reason;
            Log("[init] RARITY DISABLED: " + reason);
            GameVersion.AddDegraded("レアリティmod");
            try { HarmonyInstance.UnpatchSelf(); } catch (Exception e) { Log("[init] unpatch failed: " + e.Message); }
        }

        // 前回起動時とゲームのビルドが変わっていたら、ロールと熟練度の記録を退避しておく（アップデート直後の保険）
        static void BackupOnGameUpdate()
        {
            var seenPath = Path.Combine(Dir, "last_game_build.txt");
            var last = File.Exists(seenPath) ? File.ReadAllText(seenPath).Trim() : null;
            if (last != null && last != GameVersion.BuildGuid)
            {
                foreach (var name in new[] { "state.json", "mastery.json" })
                {
                    var state = Path.Combine(Dir, name);
                    if (!File.Exists(state)) continue;
                    var bak = Path.Combine(Dir, $"{name}.before_update_{last}_{DateTime.Now:yyyyMMdd_HHmmss}");
                    File.Copy(state, bak, true);
                    Log($"[version] GAME UPDATED {last} -> {GameVersion.BuildGuid}; {name} backed up to {Path.GetFileName(bak)}");
                }
            }
            File.WriteAllText(seenPath, GameVersion.BuildGuid);
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Tracker.OnSceneChanged();
            if (!Enabled) return;
            Mastery.OnSceneChanged();
            if (Features.Mastery) MasteryUi.OnSceneChanged();
        }

        public override void OnUpdate()
        {
            PerfProbe.OnUpdateStamp();
            if (TestMode.Scenario == "poiprobe") PoiProbe.Tick();
            if (!Enabled || GameVersion.Disabled || PerfProbe.SkipModTick) return; // 無効化中は何もしない（バニラのまま）
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Tick();
            PerfProbe.LastModTickMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            PerfProbe.ModTickMs += PerfProbe.LastModTickMs;
        }

        public override void OnLateUpdate() => PerfProbe.OnLateUpdateStamp();

        static void Tick()
        {
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            Persistence.Poll(); PerfProbe.Sub(0, ref t);
            UiPatches.Tick(); PerfProbe.Sub(1, ref t);
            Mastery.Tick(); PerfProbe.Sub(2, ref t);
            if (Features.Mastery) MasteryUi.Tick(); PerfProbe.Sub(3, ref t);
            if (Features.Boss) EndlessBoss.Tick(); PerfProbe.Sub(4, ref t);
            PerfProbe.Sub(5, ref t);   // スライディングは共通の土台が動かす
            StashTooltip.Tick(); PerfProbe.Sub(6, ref t);
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.f9Key.wasPressedThisFrame) Tracker.DumpState("F9");
            }
            catch { }
            PerfProbe.Sub(7, ref t);
        }

        public override void OnApplicationQuit()
        {
            if (!Enabled) return;
            try { Mastery.Flush(); } catch { }
        }

        // ログは共通のファイルに書く（自動テストの判定もこの行を読む）
        public static void Log(string msg) => Core.Log(msg);

        public static IEnumerable<T> FindAll<T>() where T : UnityEngine.Object => Core.FindAll<T>();
    }
}
