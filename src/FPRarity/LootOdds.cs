using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using UnityEngine;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario lootodds）。エンドレスの 1 階に入り、落とし物の一覧（SpawnableLoot）ごとに
    //   GetLoot を何度も呼んで、出る物の種類・戦利品（LootItem）のレアリティの割合を階（PlayerPrefs currentFloor）ごとに数える。
    //   ゲームの GetChanceByRarity の値も書き出す。セーブは autotest.ps1 が前後で退避・復元する
    public static class LootOdds
    {
        public static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>
            {
                ("start endless run", () => Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()),
            };
            for (int i = 0; i < 15; i++) s.Add(("wait load", () => { }));
            s.Add(("dump", Dump));
            return s;
        }

        static void Dump()
        {
            int keep = PlayerPrefs.GetInt("currentFloor", 1);
            var tables = Resources.FindObjectsOfTypeAll<SpawnableLoot>().ToArray();
            Main.Log($"[odds] {tables.Length} loot tables, currentFloor={keep}");
            // 置き場所ごとに何個あるか
            var use = new Dictionary<string, int>();
            foreach (var p in Main.FindAll<LootSpawnPoint>()) { var n = p.loot != null ? p.loot.name : "null"; use[n] = use.TryGetValue(n, out var c) ? c + 1 : 1; }
            foreach (var p in Main.FindAll<LootableChanceItems>()) { var n = "box:" + (p.loot != null ? p.loot.name : "null"); use[n] = use.TryGetValue(n, out var c) ? c + 1 : 1; }
            foreach (var kv in use) Main.Log($"[odds] in scene: {kv.Key} x{kv.Value}");
            foreach (var t in tables)
            {
                var drops = t.lootDrop;
                var byType = new Dictionary<string, int>();
                if (drops != null) foreach (var d in drops) { var k = d.dropType.ToString(); byType[k] = byType.TryGetValue(k, out var c) ? c + 1 : 1; }
                Main.Log($"[odds] table '{t.name}' nothing={t.chanceOfNothing:F2} rows={drops?.Count} types={string.Join(",", byType.Select(kv => kv.Key + ":" + kv.Value))}");
                // 戦利品の行のレアリティ
                var rowRar = new int[5];
                if (drops != null && t.LootData != null) foreach (var d in drops) if (d.dropType == LootDrop.DropType.Item && d.ItemIndex >= 0 && d.ItemIndex < t.LootData.Loot.Count) rowRar[(int)t.LootData.Loot[d.ItemIndex].GetRarity()]++;
                Main.Log($"[odds]   item rows by rarity C/U/R/E/L = {string.Join("/", rowRar)}");
                foreach (int floor in new[] { 1, 2, 3, 5, 7, 10, 15 })
                {
                    PlayerPrefs.SetInt("currentFloor", floor);
                    string ch = "";
                    for (int r = 0; r < 5; r++) { try { ch += $"{t.GetChanceByRarity((RarityData.LootRarity)r, floor):F3} "; } catch (Exception e) { ch += "err "; Main.Log("[odds] chance err " + e.Message); } }
                    var type = new Dictionary<string, int>(); var rar = new int[5]; int items = 0, calls = 2000;
                    for (int i = 0; i < calls; i++)
                    {
                        var got = t.GetLoot(1);
                        if (got == null || got.Count == 0) { type["none"] = type.TryGetValue("none", out var c0) ? c0 + 1 : 1; continue; }
                        foreach (var d in got)
                        {
                            var k = d.dropType.ToString(); type[k] = type.TryGetValue(k, out var c) ? c + 1 : 1;
                            if (d.dropType == LootDrop.DropType.Item && t.LootData != null && d.ItemIndex >= 0 && d.ItemIndex < t.LootData.Loot.Count) { rar[(int)t.LootData.Loot[d.ItemIndex].GetRarity()]++; items++; }
                        }
                    }
                    string pct(int n, int of) => of == 0 ? "-" : $"{100.0 * n / of:F1}";
                    Main.Log($"[odds]   floor {floor,2}: chanceByRarity={ch}| per call: {string.Join(" ", type.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + pct(kv.Value, calls) + "%"))} | items C/U/R/E/L % = {string.Join("/", rar.Select(x => pct(x, items)))} (n={items})");
                }
            }
            PlayerPrefs.SetInt("currentFloor", keep);
            // この Mod の武器のレアリティ（階ごとの重み）
            foreach (int floor in new[] { 1, 2, 3, 5, 7, 10, 15 }) Main.Log($"[odds] mod weapon tier weights floor {floor}: {string.Join("/", RarityTable.WeightsForTest(floor).Select(w => w.ToString("F1")))}");
        }
    }
}
