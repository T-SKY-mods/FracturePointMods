using System;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using FPSController = Il2CppDemo.Scripts.Runtime.Character.FPSController;

using FPModOption;

namespace FPRarity
{
    // 差し替えるのは武器の追跡に必要な最小限の関数だけ（広く差し替えるとゲームが落ちることを確認済み）。
    // どのパッチも例外を外に漏らさない。
    public static class Patches
    {
        // 失敗した差し替えの名前（"型.関数"）を返す
        public static System.Collections.Generic.List<string> Install(HarmonyLib.Harmony h)
        {
            int ok = 0, total = 0;
            var failed = new System.Collections.Generic.List<string>();
            void P(Type t, string m, Type[] args, string pre, string post)
            {
                total++;
                if (SafePatcher.Patch(h, t, m, args, pre, post, typeof(Patches))) ok++;
                else failed.Add($"{t.Name}.{m}{(args == null ? "" : $"({args.Length})")}");
            }

            // 手持ちの武器への反映（作り直し・装備のとき）は共通の仕組み（Weapons）が行う。ここではロールを当てる処理を登録するだけ（RarityLink）

            P(typeof(LootableItem), "Interact", Type.EmptyTypes, nameof(Interact_Pre), nameof(Interact_Post));
            P(typeof(LootableItem), "Interact", new[] { typeof(UserInteractions) }, nameof(Interact_Pre), nameof(Interact_Post));
            P(typeof(LootableItem), "GetTooltipText", null, null, nameof(GetTooltipText_Post));

            P(typeof(Inventory), "DeleteWeapon", null, nameof(DeleteWeapon_Pre), nameof(DeleteWeapon_Post));
            P(typeof(Inventory), "SpawnDroppedObject", null, null, nameof(SpawnDroppedObject_Post));

            P(typeof(SaveBag), "MoveWeaponToStash", null, nameof(MoveWeaponToStash_Pre), nameof(MoveWeaponToStash_Post));
            P(typeof(SaveBag), "MoveWeaponToInventory", null, nameof(MoveWeaponToInventory_Pre), nameof(MoveWeaponToInventory_Post));
            P(typeof(SaveBag), "LoadProgress", null, null, nameof(LoadProgress_Post));

            // ゲームが保存した瞬間に Mod 側も確定保存する
            P(typeof(SavesManager), "Save", null, null, nameof(GameSave_Post));
            // SaveAsync / SaveProgressAsync は差し替えるとメニューで落ちる → Persistence.Poll でファイル更新を検知
            P(typeof(SaveBag), "SaveProgress", null, null, nameof(StashSave_Post));

            P(typeof(EquipmentShop), "SellWeapon", null, nameof(SellWeapon_Pre), nameof(SellWeapon_Post));
            P(typeof(EquipmentShop), "GetWeaponSellPrice", null, null, nameof(GetWeaponSellPrice_Post));

            // ハンドガンのリロード速度は共通の仕組み（ReloadSpeed）が行う。倍率だけ渡す（RarityLink）

            // 武器熟練度：敵を倒した瞬間（失敗しても熟練度が増えないだけ）
            P(typeof(Il2CppEnemyAI.EnemyHealth), "Kill", null, null, nameof(EnemyKill_Post));

            Main.Log($"[patch] installed {ok}/{total}");
            return failed;
        }

        static void Safe(string where, Action a)
        {
            try { a(); }
            catch (Exception e) { Main.Log($"[{where}] ERROR {e}"); }
        }

        static void Interact_Pre(LootableItem __instance) => Safe("Interact", () => Tracker.BeginPickup(__instance));
        static void Interact_Post() => Safe("Interact", Tracker.EndPickup);

        static readonly System.Collections.Generic.HashSet<int> _loggedTooltips = new();
        static readonly System.Collections.Generic.Dictionary<IntPtr, (string raw, IntPtr active, string result)> _tipCache = new();

        public static long TooltipPostTicks, TooltipPostCalls;

        // true の間は書き換えずにゲームの文章のまま返す（保管庫のツールチップが、地面の武器と同じ文章を作るときに使う）
        public static bool RawTooltip;

        static void GetTooltipText_Post(LootableItem __instance, ref string __result)
        {
            if (RawTooltip) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            TooltipPostCalls++;
            try
            {
                var r = Tracker.GroundRoll(__instance);
                if (r == null || string.IsNullOrEmpty(__result)) return;
                // 書き換え前の文章を武器の種類ごとに1回だけ記録（表示の書き換えルールを作るため）
                // ツールチップは毎フレーム作り直されるので、元の文章と手持ちの武器が同じ間は結果を使い回す
                var raw = __result;
                var active = Refs.Fps?.GetActiveItem();
                var activePtr = active != null ? active.Pointer : IntPtr.Zero;
                if (_tipCache.TryGetValue(__instance.Pointer, out var cached) && cached.raw == raw && cached.active == activePtr)
                {
                    __result = cached.result;
                    return;
                }
                __result = TooltipRewriter.Rewrite(__result, __instance, r);
                _tipCache[__instance.Pointer] = (raw, activePtr, __result);
                if (_tipCache.Count > 256) _tipCache.Clear();
                if (_loggedTooltips.Add(__instance.ItemId))
                    Main.Log($"[tooltip] id={__instance.ItemId}\n--- raw ---\n{raw}\n--- rewritten ---\n{__result}");
            }
            catch (Exception e) { Main.Log("[tooltip] ERROR " + e.Message); }
            finally { TooltipPostTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        }


        static void DeleteWeapon_Pre() => Safe("DeleteWeapon", Tracker.BeginDrop);
        static void DeleteWeapon_Post() => Safe("DeleteWeapon", Tracker.EndDrop);
        static void SpawnDroppedObject_Post(GameObject __result) => Safe("SpawnDropped", () => Tracker.OnDroppedSpawned(__result));

        static void MoveWeaponToStash_Pre() => Safe("ToStash", Tracker.BeginMoveToStash);
        static void MoveWeaponToStash_Post() => Safe("ToStash", Tracker.EndMoveToStash);
        static void MoveWeaponToInventory_Pre(WeaponStashEntry entry) => Safe("ToInventory", () => Tracker.BeginMoveToInventory(entry));
        static void MoveWeaponToInventory_Post() => Safe("ToInventory", Tracker.EndMoveToInventory);
        static void LoadProgress_Post() => Safe("LoadProgress", () => Tracker.ReconcileStash("LoadProgress"));

        static void GameSave_Post() => Safe("GameSave", () => Persistence.CommitHeld("SavesManager.Save"));
        static void StashSave_Post() => Safe("StashSave", () => Persistence.CommitStash("SaveBag.SaveProgress"));

        static void EnemyKill_Post(Il2CppEnemyAI.EnemyHealth __instance) => Safe("Kill", () => Mastery.OnEnemyKilled(__instance));

        static void SellWeapon_Pre() => Safe("Sell", Tracker.BeginSell);
        static void SellWeapon_Post() => Safe("Sell", Tracker.EndSell);

        static void GetWeaponSellPrice_Post(WeaponItem weapon, ref float __result)
        {
            try { __result *= Tracker.SellMultFor(weapon); }
            catch (Exception e) { Main.Log("[sellprice] ERROR " + e.Message); }
        }
    }
}
