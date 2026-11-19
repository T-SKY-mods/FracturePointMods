using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FPModOption;
using Il2Cpp;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FPSurvival
{
    // 調査用（autotest -Scenario legsurv）。サバイバルを実際の流れで何周もして、手に入る武器とレアリティを記録する。
    //   拠点 → タブレットでモードを選んで開始 → 屋上 → ウェーブを倒して進む（休憩は飛ばす）→ 指定ウェーブで非常口 → 武器箱 → 脱出 → 拠点 → 次の周
    //   敵が落とした武器は、出てきたらすぐ「見た」ことにして抽選する（ゲームと同じ、最初に見たときの抽選）
    //   記録の行: [legstat] boot=<起動ごとの印> run=<この起動での何周目> src=drop|crate wave=<ウェーブ> weapon=<名前> id=<番号> tier=<レアリティ>
    //   設定（UserData/FPSurvival、無ければ既定）: legstat_runs.txt 周回数（2）、legstat_waves.txt ウェーブ数（17）、legstat_mode.txt hard/normal（hard）
    // セーブは autotest.ps1 が前後で退避・復元する
    public static class LegStat
    {
        enum St { Hub, Loading, Arena, Lobby, Crate, CrateRead, Escape, BackHub }
        static St _st;
        static int _run, _runs, _waves;
        static int[] _waveList;
        static bool _hard;
        static float _since;
        static string _boot;
        static readonly HashSet<int> _seen = new();
        static HashSet<int> _heldIds = new();
        static int _drops, _crates;
        static MethodInfo _ground;
        static float _breakAt = -1f;

        public static void Register() => TestMode.ExternalScenarios["legsurv"] = Steps;

        static string Setting(string file, string def)
        {
            try { var p = Path.Combine(Main.Dir, file); if (File.Exists(p)) return File.ReadAllText(p).Trim(); } catch { }
            return def;
        }

        static List<(string, Action)> Steps()
        {
            _runs = int.TryParse(Setting("legstat_runs.txt", "2"), out var r) ? r : 2;
            // ウェーブ数は "5,10,17" のように並べると、周ごとに順に使う
            _waveList = Setting("legstat_waves.txt", "17").Split(',').Select(x => int.TryParse(x.Trim(), out var v) ? v : 17).ToArray();
            _waves = _waveList[0];
            _hard = Setting("legstat_mode.txt", "hard") != "normal";
            _boot = DateTime.Now.ToString("HHmmss");
            _run = 0; _st = St.Hub; _since = Time.realtimeSinceStartup; _seen.Clear(); _drops = 0; _crates = 0;
            _ground = Type.GetType("FPRarity.Tracker, FPRarity")?.GetMethod("GroundRoll", BindingFlags.Public | BindingFlags.Static);
            Core.Log($"[legstat] start boot={_boot} runs={_runs} waves={_waves} mode={(_hard ? "hard" : "normal")} groundRoll={_ground != null}");
            Arena.PrepSeconds = 600f; Arena.BreakSeconds = 120f; Arena.SpawnInterval = 0.3f; Arena.BossInterval = 0.3f;
            var s = new List<(string, Action)>();
            for (int i = 0; i < 2400; i++) s.Add(("tick", Tick));   // 1 回 1.5 秒。終わったら Finish で残りを飛ばす
            return s;
        }

        static void Go(St st) { _st = st; _since = Time.realtimeSinceStartup; }

        static void Safe()
        {
            var phm = Object.FindObjectOfType<PlayerHealthManager>();
            if (phm != null) { phm.invincible = true; phm.Health = phm.MaxHealth; }
        }

        static void Tick()
        {
            try
            {
                if (Time.realtimeSinceStartup - _since > 900f) { Core.Log($"[legstat] TIMEOUT in {_st}"); TestMode.Finish(); return; }
                switch (_st)
                {
                    case St.Hub:
                        if (Main.Scene != Core.HubScene || Time.realtimeSinceStartup - _since < 4f) return;
                        // 持ち込みは大型 1・小型 1（武器箱の前の周で増えた分は倉庫へ）
                        var held = Survival.Held(Refs.Inv);
                        foreach (var type in new[] { WeaponItem.SlotType.Big, WeaponItem.SlotType.Small })
                            foreach (var h in held.Where(x => x.Item.Type == type).Skip(1)) Refs.Bag.MoveWeaponToStash(h.Item);
                        try { Tablet.TabletForTest?.Interact(); } catch { }
                        Tablet.ChooseForTest(_hard);
                        _waves = _waveList[_run % _waveList.Length];
                        Object.FindObjectOfType<ButtonStartEndlessMode>()?.StartEndlessRun();
                        Core.Log($"[legstat] run {_run + 1} start ({(_hard ? "hard" : "normal")}, {_waves} waves), loadout {string.Join(", ", Survival.Held(Refs.Inv).Select(Survival.Label))}");
                        Go(St.Loading);
                        return;
                    case St.Loading:
                        Safe();
                        if (!Arena.InArena) return;
                        if (Arena.Now == Arena.Phase.Prep) Arena.SkipTimerForTest();
                        Go(St.Arena);
                        return;
                    case St.Arena:
                        Safe();
                        RollDrops();
                        if (Arena.Now == Arena.Phase.Wave) foreach (var e in Arena.AliveForTest().ToList()) Hit(e, e.health + 50f);
                        else if (Arena.Now == Arena.Phase.Break)
                        {
                            // 倒れた敵の武器は少し遅れて落ちる。次のウェーブの開始で片付けられるので、休憩の最初の数秒は待って抽選してから進む
                            if (_breakAt < 0f) _breakAt = Time.realtimeSinceStartup;
                            if (Time.realtimeSinceStartup - _breakAt < 6f) return;
                            _breakAt = -1f;
                            if (Arena.Cleared < _waves) Arena.SkipTimerForTest();
                            else { Core.Log($"[legstat] run {_run + 1}: cleared {Arena.Cleared}, drops so far {_drops}"); Arena.OnExit(); Go(St.Lobby); }
                        }
                        else if (Arena.Now == Arena.Phase.Prep) Arena.SkipTimerForTest();
                        return;
                    case St.Lobby:
                        Arena.Teleport(Arena.LobbyForTest, -Arena.InwardForTest);
                        Go(St.Crate);
                        return;
                    case St.Crate:
                        _heldIds = Survival.Held(Refs.Inv).Select(h => h.Item.Id).ToHashSet();
                        {
                            // 同じ場面・同じ手持ちで、武器箱と同じ抽選を 2000 回（武器は渡さない）
                            int bonus = Waves.HandicapWaves(Math.Max(0, Stash.Pending?.Handicap ?? 0), _hard);
                            var sim = Arena.CrateSampleForTest(2000, Arena.Cleared + bonus, _hard);
                            Core.Log($"[legstat] boot={_boot} run={_run + 1} src=sim waves={Arena.Cleared}+{bonus} hard={_hard} " + string.Join(";", sim.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
                        }
                        Arena.CrateForTest?.GetComponentInChildren<LootableChanceItems>(true)?.Interact();
                        Go(St.CrateRead);
                        return;
                    case St.CrateRead:
                        foreach (var h in Survival.Held(Refs.Inv).Where(h => !_heldIds.Contains(h.Item.Id)))
                        {
                            _crates++;
                            Core.Log($"[legstat] boot={_boot} run={_run + 1} src=crate wave={Arena.Cleared} weapon={h.Item.GetName()} id={h.Item.Id} slot={h.Item.Type} tier={h.Tier}");
                        }
                        var go = GameObject.Find("LevelElevator_0")?.transform.Find("Go")?.GetComponent<ElevatorButton>();
                        go?.Interact();
                        Go(St.BackHub);
                        return;
                    case St.BackHub:
                        if (Main.Scene != Core.HubScene || Stash.Pending != null) return;
                        _run++;
                        Core.Log($"[legstat] run {_run} done (drops {_drops}, crate weapons {_crates})");
                        if (_run >= _runs) { Core.Log("[legstat] all runs done"); TestMode.Finish(); return; }
                        Go(St.Hub);
                        return;
                }
            }
            catch (Exception e) { Core.Log($"[legstat] tick failed in {_st}: {e.Message}"); }
        }

        // 敵が落とした武器：出てきたものを「見た」ことにしてレアリティを決める（FPRarity の地面の武器の抽選）
        static void RollDrops()
        {
            if (_ground == null) return;
            var alive = Arena.AliveForTest().Select(e => e.transform.root).ToList();
            foreach (var li in Object.FindObjectsOfType<LootableItem>())
            {
                if (li == null || !li.gameObject.activeInHierarchy || li.ItemType != LootableItem.LootType.Weapon) continue;
                if (li.gameObject.scene.name != Main.RunScene) continue;
                if (Arena.CrateForTest != null && li.transform.IsChildOf(Arena.CrateForTest.transform)) continue;
                int key = li.GetInstanceID();
                if (!_seen.Add(key)) continue;
                object roll = null;
                try { roll = _ground.Invoke(null, new object[] { li }); } catch (Exception e) { Core.Log("[legstat] roll failed " + e.InnerException?.Message); }
                var tier = roll?.GetType().GetProperty("Tier")?.GetValue(roll)?.ToString() ?? "-";
                string name = "?";
                try { name = Refs.Inv.WeaponData.Weapons[li.ItemId].GetName(); } catch { }
                bool onEnemy = alive.Any(r => li.transform.IsChildOf(r));
                _drops++;
                Core.Log($"[legstat] boot={_boot} run={_run + 1} src=drop wave={Arena.Wave} weapon={name} id={li.ItemId} tier={tier} onEnemy={onEnemy}");
            }
        }

        // 弾が当たったときと同じ入口でダメージを与える
        static void Hit(Il2CppEnemyAI.EnemyHealth e, float dmg)
        {
            var root = e.transform.parent != null ? e.transform.parent : e.transform;
            var le = root.GetComponentInChildren<Il2CppBallistics.LivingEntity>(true);
            try { le?.OnHealthChanged(-dmg); } catch { }
        }
    }
}
