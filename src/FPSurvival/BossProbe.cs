using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppEnemyAI;
using UnityEngine;
using Object = UnityEngine.Object;

using FPModOption;

namespace FPSurvival
{
    // 調査用（autotest -Scenario bossprobe）。ボスと特殊部隊の AI の状態を並べて記録する。読むだけ
    public static class BossProbe
    {
        static void Safe() { var phm = Object.FindObjectOfType<PlayerHealthManager>(); if (phm != null) { phm.invincible = true; phm.Health = phm.MaxHealth; } }

        static void Log(string when)
        {
            var player = Refs.Fps?.transform.position ?? Vector3.zero;
            foreach (var h in Arena.AliveForTest().ToList())
            {
                var root = h.transform.parent != null ? h.transform.parent : h.transform;
                var c = root.GetComponentInChildren<StateController>(true);
                var n = c?.nav;
                string nav = n == null ? "nav=null" : $"nav en={n.enabled} mesh={n.isOnNavMesh} speed={n.speed:0.0} path={(n.isOnNavMesh ? n.hasPath : false)} stop={(n.isOnNavMesh ? n.isStopped : false)} rem={(n.isOnNavMesh ? n.remainingDistance : -1f):0.0} v={n.velocity.magnitude:0.00}";
                Core.Log($"[bossprobe] {when} {root.name}: state={c?.currentState?.name} aim={c?.aimTarget?.name} wp={c?.patrolWayPoints?.Count} idx={c?.waypointIndex} sight={c?.targetInSight} {nav} pos={h.transform.position:F1} dist={Vector3.Distance(h.transform.position, player):0.0}");
            }
        }

        public static List<(string, System.Action)> Steps()
        {
            Arena.PrepSeconds = 600f; Arena.BreakSeconds = 120f; Arena.SpawnInterval = 1f; Arena.BossInterval = 1f;
            var s = new List<(string, System.Action)>
            {
                ("loadout", () =>
                {
                    var held = Survival.Held(Refs.Inv);
                    foreach (var type in new[] { WeaponItem.SlotType.Big, WeaponItem.SlotType.Small })
                        foreach (var w in held.Where(h => h.Item.Type == type).Skip(1)) Refs.Bag.MoveWeaponToStash(w.Item);
                }),
                ("focus tablet", () => { try { Tablet.TabletForTest?.Interact(); } catch (Exception e) { Core.Log("[bossprobe] focus failed " + e.Message); } }),
                ("page 2", () => Tablet.SetPage(1)),
                ("wait", () => { }),
                ("wait", () => { }),
                ("start", () => Object.FindObjectOfType<ButtonStartEndlessMode>()?.StartEndlessRun()),
            };
            for (int i = 0; i < 16; i++) s.Add(("wait load", () => { }));
            for (int i = 0; i < 20; i++) s.Add(("wait arena", () =>
            {
                Safe();
                if (Arena.Now == Arena.Phase.Prep) { Arena.SpecForTest = new WaveSpec { Boss = 1, Special = 1, MaxAlive = Waves.MaxAlive, Damage = 1f }; Arena.SkipTimerForTest(); }
            }));
            for (int i = 0; i < 14; i++) { int k = i; s.Add(("watch", () => { Safe(); Log($"t{k} {Arena.Now}"); })); }
            return s;
        }
    }
}
