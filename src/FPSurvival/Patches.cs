using System;
using System.Collections.Generic;
using System.Linq;
using FPModOption;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FPSurvival
{
    public static class Patches
    {
        // 必須（欠けたら無効化）：開始・倒れたとき・エレベーターのボタン・非常口の扉・カード
        // サバイバル中にゲームが出した通知をログに残す（不要なポップアップの確認用）
        static void Notify_Pre(string notification)
        {
            if (!Stash.InRun) return;
            Main.Log("[notify] " + notification);
            Notices.Add(notification); if (Notices.Count > 40) Notices.RemoveAt(0);
        }
        public static readonly List<string> Notices = new();   // テスト用：最近の通知

        static bool _rainLogged;
        static void Rain_Post(RainAudioController __instance)
        {
            if (!Stash.InRun) { _rainLogged = false; return; }
            foreach (var a in new[] { __instance.indoorSource, __instance.outdoorSource })
            {
                if (a == null || a.volume <= Arena.HumVolume) continue;
                if (!_rainLogged) { _rainLogged = true; Main.Log($"[arena] rooftop hum '{a.name}' {a.volume:0.00} -> {Arena.HumVolume:0.00}"); }
                a.volume = Arena.HumVolume;
            }
        }

        public static bool Install(HarmonyLib.Harmony h)
        {
            var c = typeof(Patches);
            bool ok = SafePatcher.Patch(h, typeof(ButtonStartEndlessMode), "StartEndlessRun", null, nameof(StartRun_Pre), null, c);
            ok &= SafePatcher.Patch(h, typeof(PlayerHealthManager), "Die", null, nameof(Die_Pre), null, c);
            // Die は呼び出し元に組み込まれて差し替えが効かないことがあるので、中身（非同期の本体）の方でも止める
            var dieBody = typeof(PlayerHealthManager).GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).FirstOrDefault(t => t.Name.Contains("Die_d__"));
            ok &= dieBody != null && SafePatcher.Patch(h, dieBody, "MoveNext", null, nameof(DieBody_Pre), null, c);
            ok &= SafePatcher.Patch(h, typeof(ElevatorButton), "Interact", null, nameof(Elevator_Pre), null, c);
            // 屋上を作る（レアリティmod が動いているときは、そちらのエンドレスの階の処理が Link.FloorOverride を呼ぶ）
            ok &= SafePatcher.Patch(h, typeof(LevelElevator), "SetupEndlessFloor", null, nameof(EndlessFloor_Pre), null, c);
            ok &= SafePatcher.Patch(h, typeof(CardsManager), "Show", null, nameof(CardsShow_Pre), null, c);
            ok &= SafePatcher.Patch(h, typeof(CardsManager), "GenerateCards", null, nameof(CardsGen_Pre), null, c);
            ok &= SafePatcher.Patch(h, typeof(LootableChanceItems), "Interact", null, nameof(Chance_Pre), null, c);
            ok &= SafePatcher.Patch(h, typeof(VendingMachine), "Interact", null, nameof(Vending_Pre), nameof(Vending_Post), c);
            // 非常口（サバイバルの終了）：Mod が入口に置く調べられる所
            ok &= SafePatcher.Patch(h, typeof(DoorInteraction), "Interact", null, nameof(Exit_Pre), null, c);
            ok &= SafePatcher.Patch(h, typeof(DoorInteraction), "GetDescription", null, nameof(ExitText_Pre), null, c);
            ok &= SafePatcher.Patch(h, typeof(Inventory), "SpawnDroppedObject", null, null, nameof(Drop_Post), c);
            if (!ok) return false;

            // 表示（欠けても遊べる）
            SafePatcher.Patch(h, typeof(NotificationsManager), "NotifyWithColor", null, nameof(Notify_Pre), null, c);
            SafePatcher.Patch(h, typeof(VendingMachine), "GetDescription", null, null, nameof(VendingText_Post), c);
            SafePatcher.Patch(h, typeof(ElevatorButton), "GetDescription", null, null, nameof(ElevatorText_Post), c);
            // 屋上でずっと鳴る「コー」という環境音（雨の音）は、ゲームが毎フレーム音量を決めるので、その後で小さくする
            SafePatcher.Patch(h, typeof(RainAudioController), "Update", null, null, nameof(Rain_Post), c);
            // 屋上の店・ATM・両替・配達箱はサバイバル中は使えない
            foreach (var t in new[] { typeof(Store), typeof(ATM), typeof(Exchanger), typeof(DeliveryBox) })
                SafePatcher.Patch(h, t, "Interact", null, nameof(Blocked_Pre), null, c);

            // 屋上（通常ランの最終ボスの階）を作る
            Link.FloorOverride = (el, floor) =>
            {
                if (!Stash.InRun) return false;
                Main.Log($"[floor] survival: building the rooftop (normal floor {Arena.MapFloor}) instead of endless floor {floor}");
                el.SetupNormalFloor(Arena.MapFloor);
                return true;
            };
            return true;
        }

        static bool EndlessFloor_Pre(LevelElevator __instance, int currentFloor)
        {
            try
            {
                if (Link.BossFloorsActive || Link.FloorOverride == null) return true;   // FPRarity のボスフロアが階を作るときは、そこから呼ばれる
                return !Link.FloorOverride(__instance, currentFloor);
            }
            catch (Exception e) { Main.Log("[floor] override failed: " + e); return true; }
        }

        static bool StartRun_Pre(ButtonStartEndlessMode __instance)
        {
            if (Survival.Bypass || Tablet.Page != 1 || Main.Scene != Core.HubScene) return true;
            try { Survival.Start(__instance, Tablet.HardPicked); }
            catch (Exception e) { Main.Log("[start] failed: " + e); Main.Notify(Main.T("サバイバルを開始できませんでした", "Could not start survival"), true); }
            return false;
        }

        static bool Die_Pre(PlayerHealthManager __instance)
        {
            try { return !Arena.OnDie(__instance); }
            catch (Exception e) { Main.Log("[die] failed: " + e); return true; }
        }

        static bool DieBody_Pre(Il2CppSystem.Object __instance)
        {
            try
            {
                if (!Arena.InSurvival) return true;
                var phm = __instance.Cast<PlayerHealthManager._Die_d__54>().__4__this;
                return !Arena.OnDie(phm);
            }
            catch (Exception e) { Main.Log("[die] body failed: " + e); return true; }
        }

        // サバイバル終了後のエレベーターのボタンは拠点へ戻るボタン（ゲームの「Return to Hub」の訳を使う）
        static void ElevatorText_Post(ref string __result)
        {
            if (!Arena.Escaping) return;
            try { __result = Il2CppGameLocalization.LocManager.Translate("Return to Hub") ?? "Return to Hub"; } catch { __result = "Return to Hub"; }
        }

        static bool Elevator_Pre()
        {
            try { return !Arena.OnElevatorButton(); }
            catch (Exception e) { Main.Log("[elevator] failed: " + e); return true; }
        }

        // カードは出さない（選んだことにしておく。エレベーターがカードの選択を待つ場合に止まらないように）
        static bool CardsShow_Pre(CardsManager __instance)
        {
            if (!Arena.InSurvival) return true;
            __instance.IsPicked = true;
            Main.Log("[cards] skipped (survival)");
            return false;
        }

        static bool CardsGen_Pre() => !Arena.InSurvival;

        // 武器箱・倒した敵の死体をあさったとき（長押しはゲームのまま）。死体からは弾だけ（ゲームの戦利品は出さない）
        static bool Chance_Pre(LootableChanceItems __instance)
        {
            try
            {
                if (Arena.IsCrate(__instance)) { Arena.OpenCrate(); return false; }
                if (Arena.IsCorpse(__instance)) { Arena.SearchCorpse(__instance); return false; }
                // ほかの調べられる所（ゲームの戦利品の抽選）はサバイバルでは使わない
                if (Arena.InArena) { Main.Log($"[search] blocked '{__instance.Name}' at {__instance.transform.position}"); return false; }
            }
            catch (Exception e) { Main.Log("[search] failed: " + e); return false; }
            return true;
        }

        static bool Exit_Pre(DoorInteraction __instance)
        {
            try { if (!Arena.IsExit(__instance)) return true; Arena.OnExit(); }
            catch (Exception e) { Main.Log("[exit] failed: " + e); }
            return false;
        }

        static bool ExitText_Pre(DoorInteraction __instance, ref string __result)
        {
            if (!Arena.IsExit(__instance)) return true;
            __result = Arena.ExitText();
            return false;
        }

        // 自販機：サバイバル中はドルで高額。ゲームの処理（ルーブル払い・缶を出す）はそのまま使い、払うルーブルはこちらで足しておく
        const float GameDrinkPrice = 300f;
        static bool Vending_Pre()
        {
            if (!Arena.InArena) return true;
            var inv = Refs.Inv;
            if (inv == null) return false;
            if (Arena.Now != Arena.Phase.Break)
            {
                Main.Notify(Main.T("ウェーブ中は買えません", "Not during a wave"), true);
                return false;
            }
            if (inv.DollarsCount < Arena.HealPrice)
            {
                Main.Notify(Main.T("お金が足りません", "Not enough money"), true);
                return false;
            }
            Arena.BeforeDrink();
            float price = Arena.HealPrice;
            inv.DeleteDollars(price);
            inv.RoublesCount = inv.RoublesCount + GameDrinkPrice;
            Arena.OnDrinkBought();   // 次は $1,000 高くなる
            Main.Log($"[vending] bought a drink for ${price:0}, left ${inv.DollarsCount:0}, next ${Arena.HealPrice:0}");
            return true;
        }

        // 出てきた缶は 1 本で体力が満タンになる
        static void Vending_Post(VendingMachine __instance)
        {
            try { if (Arena.InArena) Arena.FullHealDrinks(__instance); } catch (Exception e) { Main.Log("[vending] post failed: " + e.Message); }
        }
        static void VendingText_Post(ref string __result)
        {
            if (Arena.InArena) __result = Main.T($"回復ドリンク ${Arena.HealPrice:N0}", $"Healing drink ${Arena.HealPrice:N0}");
        }

        static bool Blocked_Pre()
        {
            if (!Arena.InSurvival) return true;
            Main.Notify(Main.T("サバイバル中は使えません", "Not available during survival"), true);
            return false;
        }

        // 弾を死体の所に落とすため、ゲームが足元に出した物を受け取る
        internal static bool CaptureDrop;
        static GameObject _captured;
        static void Drop_Post(GameObject __result)
        {
            if (CaptureDrop) _captured = __result;
        }

        internal static GameObject TakeDrop()
        {
            var g = _captured;
            _captured = null;
            return g;
        }
    }
}
