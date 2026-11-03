using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2Cpp;
using UnityEngine;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario enemyhp）。エンドレスに出て、場面の敵の体力と部位ごとのダメージ倍率・防具・弾の貫通の設定を書き出す。何も変更しない。
    public static class EnemyProbe
    {
        public static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)> { ("start endless run", () => Core.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()) };
            for (int i = 0; i < 18; i++) s.Add(("wait load", () => { }));
            s.Add(("enemy hp", Dump));
            return s;
        }

        static void Dump()
        {
            var sb = new StringBuilder("===== ENEMY HP =====\n");
            var seen = new HashSet<string>();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Il2CppEnemyAI.EnemyHealth>()))
            {
                var e = o.TryCast<Il2CppEnemyAI.EnemyHealth>();
                if (e == null) continue;
                var where = e.gameObject.scene.IsValid() ? "scene:" + e.gameObject.scene.name : "asset";
                var le = e.GetComponentInParent<Il2CppBallistics.LivingEntity>() ?? e.GetComponentInChildren<Il2CppBallistics.LivingEntity>(true);
                var key = $"{e.gameObject.name}|{where}|{e.health}";
                if (!seen.Add(key)) continue;
                sb.Append($"  '{e.gameObject.name}' ({where}) health={e.health} start={le?.StartHealth} boss={le?.Boss} type={e.enemyType} armor={(e.Armor != null ? e.Armor.name + (e.Armor.activeSelf ? "(on)" : "(off)") : "-")}");
                var parts = new SortedDictionary<string, string>();
                foreach (var c in e.GetComponentsInChildren<Il2CppBallistics.LivingEntityCollider>(true))
                {
                    var col = c.GetComponent<Collider>();
                    var bo = c.GetComponent<Il2CppBallistics.BallisticObject>();
                    parts[c.gameObject.name] = $"{c.DamageMultiplier:0.###}{(col == null ? " nocol" : col.enabled ? "" : " coloff")}{(bo != null ? " mat" + bo.MatID : "")} L{c.gameObject.layer}";
                }
                if (parts.Count > 0) sb.Append(" parts=[" + string.Join(", ", parts.Select(kv => $"{kv.Key}:{kv.Value}")) + "]");
                // 部位の当たり判定ではないのに弾を止めうる当たり判定（持っている武器・防具など）
                var other = new List<string>();
                foreach (var col in e.GetComponentsInChildren<Collider>(true))
                    if (col.GetComponent<Il2CppBallistics.LivingEntityCollider>() == null && !col.isTrigger) other.Add($"{col.name}(L{col.gameObject.layer}{(col.enabled ? "" : ",off")})");
                if (other.Count > 0) sb.Append(" other=[" + string.Join(", ", other.Distinct().Take(20)) + "]");
                sb.AppendLine();
            }
            var st = Il2CppBallistics.BulletHandler.instance?.Settings;
            if (st != null)
            {
                sb.AppendLine($"ballistics: drop={st.useBulletdrop} drag={st.useBulletdrag} materials={st.useBallisticMaterials} air={st.AirDensity}");
                for (int i = 0; i < st.MaterialData.Count; i++) { var m = st.MaterialData[i]; sb.AppendLine($"  mat{i} '{m.Name}' loss/unit={m.EnergylossPerUnit} spread={m.RndSpread}"); }
            }
            var w = Refs.Fps?.GetActiveItem()?.gameObject?.GetComponentInChildren<Il2CppBallistics.Weapon>(true);
            if (w != null) sb.AppendLine($"held weapon: dmg={w.MuzzleDamage} speed={w.MaxBulletSpeed} rnd={w.randomSpeedOffset} mass={w.BulletMass} drag={w.DragCoefficient} hitmask={w.HitMask.value}");
            Main.Log(sb.ToString());
        }
    }
}
