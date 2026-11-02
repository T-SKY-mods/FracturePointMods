using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2Cpp;
using UnityEngine;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario enemyhp）。読み込まれている敵（シーン上・プレハブ）の体力と部位ごとのダメージ倍率を書き出す。何も変更しない。
    public static class EnemyProbe
    {
        public static List<(string, Action)> Steps() => new() { ("enemy hp", Dump) };

        static void Dump()
        {
            var sb = new StringBuilder("===== ENEMY HP =====\n");
            var seen = new HashSet<string>();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Il2CppEnemyAI.EnemyHealth>()))
            {
                var e = o.TryCast<Il2CppEnemyAI.EnemyHealth>();
                if (e == null) continue;
                var where = e.gameObject.scene.IsValid() ? "scene:" + e.gameObject.scene.name : "asset";
                var key = $"{e.gameObject.name}|{where}|{e.health}";
                if (!seen.Add(key)) continue;
                sb.Append($"  '{e.gameObject.name}' ({where}) health={e.health} headshot={e.headshot} type={e.enemyType} armor={(e.Armor != null ? e.Armor.name + (e.Armor.activeSelf ? "(on)" : "(off)") : "-")}");
                var parts = new SortedDictionary<string, float>();
                foreach (var c in e.GetComponentsInChildren<Il2CppBallistics.LivingEntityCollider>(true))
                    parts[c.gameObject.name] = c.DamageMultiplier;
                if (parts.Count > 0) sb.Append(" parts=[" + string.Join(", ", parts.Select(kv => $"{kv.Key}:{kv.Value:0.##}")) + "]");
                sb.AppendLine();
            }
            Main.Log(sb.ToString());
        }
    }
}
