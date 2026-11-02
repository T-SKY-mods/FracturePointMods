using System;
using System.Collections.Generic;
using Il2Cpp;

using FPModOption;

namespace FPRarity
{
    // 落ちている武器の種類を、全武器同じ確率にする（2026-10-31 ユーザー指定。レアリティがオンのときだけ）。
    //   ゲームは武器ごとのゲームのレアリティで出やすさを決めている（1 階では Elite・Legendary の武器は出ず、深い階では Hunter-32 が 7 本に 1 本）。
    //   強さはこの Mod のレアリティで決まるので、武器の種類は一覧（SpawnableLoot）の武器の行から均等に選び直す。
    //   武器が出るかどうか・何個出るか・武器以外の物はゲームのまま
    public static class LootPatch
    {
        static readonly Random Rng = new();
        public static int Calls, Swapped;   // テスト・ログ用

        public static bool Install(HarmonyLib.Harmony h)
        {
            try
            {
                var m = HarmonyLib.AccessTools.Method(typeof(SpawnableLoot), nameof(SpawnableLoot.GetLoot));
                h.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(LootPatch), nameof(Post)));
                Main.Log("[loot] weapon drops: every weapon equally likely");
                return true;
            }
            catch (Exception e) { Main.Log("[loot] patch failed (game's weapon chances kept): " + e.Message); return false; }
        }

        static void Post(SpawnableLoot __instance, Il2CppSystem.Collections.Generic.List<LootDrop> __result)
        {
            try
            {
                Calls++;
                if (__result == null || __result.Count == 0 || !Features.Rarity) return;
                List<LootDrop> rows = null;
                for (int i = 0; i < __result.Count; i++)
                {
                    var d = __result[i];
                    if (d == null || d.dropType != LootDrop.DropType.Weapon) continue;
                    rows ??= WeaponRows(__instance);
                    if (rows.Count < 2) return;
                    // 一覧の行（ゲームのデータ）は書き換えず、別の武器の行に差し替える
                    __result[i] = rows[Rng.Next(rows.Count)];
                    Swapped++;
                }
            }
            catch (Exception e) { Core.LogOnce("lootpatch", "[loot] swap failed: " + e.Message); }
        }

        static readonly Dictionary<IntPtr, List<LootDrop>> _rows = new();

        // その一覧の武器の行（同じ武器が何行あっても 1 つとして数える）
        static List<LootDrop> WeaponRows(SpawnableLoot l)
        {
            if (_rows.TryGetValue(l.Pointer, out var rows)) return rows;
            rows = new List<LootDrop>();
            var seen = new HashSet<int>();
            var drops = l.lootDrop;
            for (int i = 0; drops != null && i < drops.Count; i++)
            {
                var d = drops[i];
                if (d != null && d.dropType == LootDrop.DropType.Weapon && seen.Add(d.ItemIndex)) rows.Add(d);
            }
            _rows[l.Pointer] = rows;
            return rows;
        }
    }
}
