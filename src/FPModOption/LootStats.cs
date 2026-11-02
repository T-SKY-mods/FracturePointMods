using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using UnityEngine;

namespace FPModOption
{
    // ゲームの落ちている武器の出方を調べる（autotest -Dev -Scenario lootstats）。読むだけで、何も変えない。
    //   ・読み込まれている SpawnableLoot（落ちている物・箱の中身の一覧）ごとに、武器の行（同じ武器が何行あるか・ゲームのレアリティ）
    //   ・ゲームの抽選（GetLoot）を何度も呼んで、どの武器が何回出たか（階ごと。階は PlayerPrefs の currentFloor を一時的に変えて戻す）
    public static class LootStats
    {
        const int Samples = 20000;

        public static List<(string, Action)> Steps() => new() { ("list", Run) };

        // 階の中の置き場所（LootSpawnPoint・LootableChanceItems）がどの一覧を使っているか
        public static void Places()
        {
            var sp = Core.FindAll<LootSpawnPoint>().Where(p => p != null).GroupBy(p => p.loot != null ? p.loot.name : "-").Select(g => $"{g.Key}x{g.Count()}");
            var ch = Core.FindAll<LootableChanceItems>().Where(p => p != null).GroupBy(p => (p.loot != null ? p.loot.name : "-") + "/" + p.Name).Select(g => $"{g.Key}x{g.Count()}");
            Core.Log($"[loot] spawn points: {string.Join(", ", sp)} | containers: {string.Join(", ", ch)} | floor={PlayerPrefs.GetInt("currentFloor", 1)}");
        }

        public static void Run()
        {
            {
                var inv = Refs.Inv;
                var weapons = inv?.WeaponData?.Weapons;
                string Name(int idx) => weapons != null && idx >= 0 && idx < weapons.Count ? $"{weapons[idx].GetName()}[{weapons[idx].GetRarity()}]" : $"#{idx}";
                var loots = Resources.FindObjectsOfTypeAll<SpawnableLoot>().Where(l => l != null).ToList();
                Core.Log($"[loot] {loots.Count} loot tables loaded");
                foreach (var l in loots)
                {
                    var drops = l.lootDrop;
                    int n = drops?.Count ?? 0;
                    var w = new Dictionary<int, int>();
                    for (int i = 0; i < n; i++)
                    {
                        var d = drops[i];
                        if (d != null && d.dropType == LootDrop.DropType.Weapon) w[d.ItemIndex] = w.TryGetValue(d.ItemIndex, out var c) ? c + 1 : 1;
                    }
                    Core.Log($"[loot] table '{l.name}': rows={n} weaponRows={w.Values.Sum()} nothing={l.chanceOfNothing}" +
                             (w.Count > 0 ? " weapons: " + string.Join(", ", w.OrderBy(kv => kv.Key).Select(kv => $"{Name(kv.Key)}x{kv.Value}")) : ""));
                    if (w.Count == 0) continue;
                    int orig = PlayerPrefs.GetInt("currentFloor", 1);
                    foreach (int floor in new[] { 1, 5, 10, 20 })
                    {
                        PlayerPrefs.SetInt("currentFloor", floor);
                        string chances = "";
                        foreach (RarityData.LootRarity r in Enum.GetValues(typeof(RarityData.LootRarity)))
                            try { chances += $" {r}={l.GetChanceByRarity(r, floor):0.###}"; } catch (Exception e) { chances += $" {r}=err({e.Message})"; }
                        var count = new Dictionary<string, int>();
                        int got = 0, none = 0;
                        for (int s = 0; s < Samples; s++)
                        {
                            List<LootDrop> res = null;
                            try { var r = l.GetLoot(1); res = r != null ? new List<LootDrop>(r.ToArray()) : null; } catch { }
                            if (res == null || res.Count == 0) { none++; continue; }
                            foreach (var d in res)
                            {
                                string k = d.dropType == LootDrop.DropType.Weapon ? Name(d.ItemIndex) : d.dropType.ToString();
                                count[k] = count.TryGetValue(k, out var c) ? c + 1 : 1;
                                got++;
                            }
                        }
                        Core.Log($"[loot] table '{l.name}' floor {floor}: chanceByRarity{chances} | {Samples} draws, empty {none}: " +
                                 string.Join(", ", count.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
                    }
                    PlayerPrefs.SetInt("currentFloor", orig);
                }
                TestMode.Check($"loot tables read ({loots.Count})", loots.Count > 0);
            }
        }
    }
}
