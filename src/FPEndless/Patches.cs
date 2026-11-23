using System;
using System.Collections.Generic;
using FPModOption;
using Il2Cpp;
using UnityEngine;
using Random = System.Random;

namespace FPEndless
{
    public static class Patches
    {
        static readonly Random Rng = new();
        public static int SpawnCalls, SpawnAsked, SpawnGiven, LootRerolls, DamageScaled, LootCalls; public static double LootMs;   // テスト・ログ用

        public static bool Install(HarmonyLib.Harmony h)
        {
            var c = typeof(Patches);
            bool ok = SafePatcher.Patch(h, typeof(ButtonStartEndlessMode), "StartEndlessRun", null, nameof(StartRun_Pre), null, c);
            ok &= SafePatcher.Patch(h, typeof(EnemySpawner), "Spawn", null, nameof(Spawn_Pre), null, c);
            // 敵が受けるダメージ（体力の倍率）。2 つの入口があり、片方がもう片方を呼ぶかもしれないので、外側の 1 回だけ割る
            ok &= SafePatcher.Patch(h, typeof(Il2CppEnemyAI.EnemyHealth), "TakeDamage", new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(Collider), typeof(GameObject), typeof(bool) }, nameof(Hit6_Pre), nameof(Hit_Post), c);
            ok &= SafePatcher.Patch(h, typeof(Il2CppEnemyAI.EnemyHealth), "TakeDamage", new[] { typeof(float) }, nameof(Hit1_Pre), nameof(Hit_Post), c);
            if (!ok) return false;
            // 戦利品の運（欠けても遊べる）
            SafePatcher.Patch(h, typeof(SpawnableLoot), "GetLoot", null, null, nameof(Loot_Post), c);
            return true;
        }

        // ---------- 参加費 ----------
        static bool StartRun_Pre(ButtonStartEndlessMode __instance)
        {
            try
            {
                // サバイバルの開始・サバイバルのページはサバイバルmod の担当（調整は効かせない）
                if (Link.SurvivalPageOpen() || Link.InSurvival()) return true;
                if (Main.Scene != Core.HubScene || __instance.inProcess) return true;   // 開始の途中で押し直したときは二重に引かない
                var inv = Refs.Inv;
                if (inv == null) return true;
                int fee = Tuning.Mode ? Tuning.FeeSet : Tuning.VanillaFee;
                if (fee > Tuning.VanillaFee)
                {
                    if (inv.DollarsCount < fee)
                    {
                        Main.Notify(Main.T($"ドルが足りません。参加費は ${fee:N0} です", $"Not enough dollars. The entry fee is ${fee:N0}"), true);
                        return false;
                    }
                    // ゲームが $500 を引くので、残りをここで引く
                    inv.DeleteDollars(fee - Tuning.VanillaFee);
                    Main.Log($"[start] entry fee ${fee:N0} (paid ${fee - Tuning.VanillaFee:N0} here, ${Tuning.VanillaFee} by the game), left ${inv.DollarsCount:N0}");
                }
                Tuning.BeginRun();
            }
            catch (Exception e) { Main.Log("[start] failed: " + e); }
            return true;
        }

        // ---------- 敵の数 ----------
        static void Spawn_Pre(ref int enemiesCount)
        {
            try
            {
                SpawnCalls++; SpawnAsked += enemiesCount;
                Tuning.OnSpawn();   // 新しい敵の種類の弾のダメージも（湧く前に）
                if (Tuning.Active && Tuning.Count > 1f && enemiesCount > 0)
                {
                    float want = enemiesCount * Tuning.Count;
                    int n = (int)want;
                    if (Rng.NextDouble() < want - n) n++;   // 端数は確率で
                    enemiesCount = n;
                }
                SpawnGiven += enemiesCount;
            }
            catch (Exception e) { Core.LogOnce("endl-spawn", "[spawn] failed: " + e.Message); }
        }

        // ---------- 敵の体力（受けるダメージを割る） ----------
        [ThreadStatic] static int _depth;
        static void Hit6_Pre(ref float damage) => Scale(ref damage);
        static void Hit1_Pre(ref float damage) => Scale(ref damage);
        static void Scale(ref float damage)
        {
            if (_depth++ > 0) return;
            if (!Tuning.Active || Tuning.Hp <= 1f) return;
            damage /= Tuning.Hp;
            DamageScaled++;
        }
        static void Hit_Post() { if (_depth > 0) _depth--; }
        public static void ResetDepth() => _depth = 0;   // 途中で例外が出て後処理が呼ばれなかったときのため（毎フレーム）

        // ---------- 戦利品の運 ----------
        //   ゲームの選び方（GetSingleLoot）で同じ種類の候補を何個か引き、レアリティが高いほど選ばれやすくする（運が 1 なら元のまま）。
        //   武器はレアリティmod があればそちらの抽選（Link.Luck）に任せる。弾薬・回復・防具・マガジン・お金は変えない
        const int Candidates = 6;
        static bool _inLoot;
        static void Loot_Post(SpawnableLoot __instance, Il2CppSystem.Collections.Generic.List<LootDrop> __result)
        {
            if (_inLoot || !Tuning.Active || Tuning.Luck <= 1.001f || __result == null || __instance == null) return;
            _inLoot = true;
            var sw = System.Diagnostics.Stopwatch.StartNew(); LootCalls++;
            try
            {
                for (int i = 0; i < __result.Count; i++)
                {
                    var d = __result[i];
                    if (d == null) continue;
                    bool item = d.dropType == LootDrop.DropType.Item;
                    bool weapon = d.dropType == LootDrop.DropType.Weapon && !Link.RarityActive;
                    if (!item && !weapon) continue;
                    var pick = Reroll(__instance, d);
                    if (pick != null && pick.Pointer != d.Pointer) { __result[i] = pick; LootRerolls++; }
                }
            }
            catch (Exception e) { Core.LogOnce("endl-loot", "[loot] luck failed: " + e.Message); }
            finally { _inLoot = false; LootMs += sw.Elapsed.TotalMilliseconds; }
        }

        static LootDrop Reroll(SpawnableLoot t, LootDrop first)
        {
            var cands = new List<LootDrop> { first };
            for (int tries = 0; cands.Count < Candidates && tries < Candidates * 6; tries++)
            {
                var d = t.GetSingleLoot();
                if (d != null && d.dropType == first.dropType) cands.Add(d);
            }
            float total = 0f; var w = new float[cands.Count];
            for (int i = 0; i < cands.Count; i++) { w[i] = Tuning.Tilt(RarityOf(t, cands[i]), Tuning.Luck); total += w[i]; }
            float r = (float)Rng.NextDouble() * total;
            for (int i = 0; i < cands.Count; i++) { r -= w[i]; if (r <= 0f) return cands[i]; }
            return cands[cands.Count - 1];
        }

        public static int RarityOf(SpawnableLoot t, LootDrop d)
        {
            try
            {
                if (d.dropType == LootDrop.DropType.Item && t.LootData != null && d.ItemIndex >= 0 && d.ItemIndex < t.LootData.Loot.Count) return (int)t.LootData.Loot[d.ItemIndex].GetRarity();
                if (d.dropType == LootDrop.DropType.Weapon && t.WeaponData != null && d.ItemIndex >= 0 && d.ItemIndex < t.WeaponData.Weapons.Count) return (int)t.WeaponData.Weapons[d.ItemIndex].GetRarity();
            }
            catch { }
            return 0;
        }
    }
}
