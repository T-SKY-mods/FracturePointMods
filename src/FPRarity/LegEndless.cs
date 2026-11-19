using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario legendless）。エンドレスを 1 階から進み、各階に置いてある武器を全部「見た」ことにして抽選し、記録する。
    //   記録の行: [legstat] boot=<起動ごとの印> src=floor floor=<階> weapon=<名前> id=<番号> tier=<レアリティ> active=<表示中か>
    //   設定（UserData/FPRarity、無ければ既定）: legstat_floors.txt 何階まで（12）
    // セーブは autotest.ps1 が前後で退避・復元する
    public static class LegEndless
    {
        enum St { Start, Floor, Moving }
        static St _st;
        static int _floors, _floorIdx, _ticksOnFloor, _ticksPerFloor = 30;
        static float _since;
        static string _boot;
        static readonly HashSet<int> _seen = new();
        static int _total;

        public static List<(string, Action)> Steps()
        {
            _floors = 12;
            try { var p = Path.Combine(Main.Dir, "legstat_floors.txt"); if (File.Exists(p) && int.TryParse(File.ReadAllText(p).Trim(), out var f)) _floors = f; } catch { }
            _boot = DateTime.Now.ToString("HHmmss");
            _st = St.Start; _floorIdx = 0; _seen.Clear(); _total = 0; _since = Time.realtimeSinceStartup;
            Main.Log($"[legstat] endless start boot={_boot} floors={_floors}");
            var s = new List<(string, Action)>();
            for (int i = 0; i < 1200; i++) s.Add(("tick", Tick));
            return s;
        }

        static void Go(St st) { _st = st; _since = Time.realtimeSinceStartup; _ticksOnFloor = 0; }

        static void Tick()
        {
            try
            {
                var phm = UnityEngine.Object.FindObjectOfType<PlayerHealthManager>();
                if (phm != null) { phm.invincible = true; phm.Health = phm.MaxHealth; }
                if (Time.realtimeSinceStartup - _since > 300f) { Main.Log($"[legstat] TIMEOUT in {_st}"); TestMode.Finish(); return; }
                switch (_st)
                {
                    case St.Start:
                        Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
                        Go(St.Moving);
                        return;
                    case St.Moving:
                        // 読み込みが済むまで待つ。階を移るとカードを選ぶ画面が出るので選ぶ
                        EndlessProbe.PickCard();
                        if (Time.realtimeSinceStartup - _since < 24f) return;
                        _floorIdx++;
                        Go(St.Floor);
                        return;
                    case St.Floor:
                        _ticksOnFloor++;
                        Roll();
                        // 敵を倒していく（次の敵の波が来る。敵の持っていた武器は倒すと拾える）
                        foreach (var e in Main.FindAll<Il2CppEnemyAI.EnemyHealth>().Where(e => e != null && e.health > 0f && e.gameObject.activeInHierarchy).ToList())
                        {
                            var root = e.transform.parent != null ? e.transform.parent : e.transform;
                            try { root.GetComponentInChildren<Il2CppBallistics.LivingEntity>(true)?.OnHealthChanged(-(e.health + 50f)); } catch { }
                        }
                        if (_ticksOnFloor < _ticksPerFloor) return;   // 敵の波が来て、部屋の物が出そろうまで見る
                        {
                            var sp = UnityEngine.Object.FindObjectsOfType<LootSpawnPoint>(true);
                            var items = UnityEngine.Object.FindObjectsOfType<LootableItem>(true).Where(l => l.gameObject.scene.name == "URP_OfficeCorridor").GroupBy(l => $"{l.ItemType}/{(l.gameObject.activeInHierarchy ? "on" : "off")}").Select(g => $"{g.Key}x{g.Count()}");
                            Main.Log($"[legstat] floor {_floorIdx} (currentFloor={RarityTable.CurrentFloor()}, boss room={EndlessBoss.IsBossFloor}) done, weapons so far {_total}; GetLoot calls {LootPatch.Calls} swapped {LootPatch.Swapped}; spawn points {sp.Length} (active {sp.Count(p => p.gameObject.activeInHierarchy)}); items {string.Join(" ", items)}");
                        }
                        if (_floorIdx >= _floors) { Main.Log("[legstat] all floors done"); TestMode.Finish(); return; }
                        Move();
                        Go(St.Moving);
                        return;
                }
            }
            catch (Exception e) { Main.Log($"[legstat] tick failed in {_st}: {e.Message}"); }
        }

        // 次の階へ。エレベーターが何台あるか・動いている途中か・ボタンを記録し、プレイヤーのいる側（着いたエレベーター）に入れて動かす
        static void Move()
        {
            var fps = Refs.Fps;
            var els = Main.FindAll<LevelElevator>().ToList();
            foreach (var e in els)
            {
                string moving = "?"; try { moving = e.isMoving.ToString(); } catch { }
                var btns = e.GetComponentsInChildren<ElevatorButton>(true).Select(b => b.gameObject.name);
                Main.Log($"[legstat] elevator '{e.name}' at {e.transform.position} floor={e.CurrentFloor} moving={moving} dist={(fps != null ? Vector3.Distance(fps.transform.position, e.transform.position) : -1):0} buttons=[{string.Join(",", btns)}]");
            }
            // 階の奥にある出口のエレベーター（着いたエレベーターではない方）を使う。1 台しかなければそれ
            var el = fps == null || els.Count < 2 ? els.FirstOrDefault() : els.OrderByDescending(e => Vector3.Distance(fps.transform.position, e.transform.position)).FirstOrDefault();
            if (el == null || fps == null) { Main.Log("[legstat] no elevator / player"); return; }
            var col = el.EnterCollider;
            var pos = col != null ? col.bounds.center : el.transform.position;
            var cc = fps.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            fps.transform.position = pos;
            if (cc != null) cc.enabled = true;
            // 上へのボタン（下へは拠点に戻る）。押されていないもの
            var all = Main.FindAll<ElevatorButton>().ToList();
            foreach (var b in all)
            {
                string act = "?"; try { act = b.activated.ToString(); } catch { }
                Main.Log($"[legstat]   button '{b.gameObject.name}' type={b.type} activated={act} first={b.first} elevator='{b.elevator?.name}' active={b.gameObject.activeInHierarchy}");
            }
            var go = all.FirstOrDefault(b => b.type == ElevatorButton.ButtonType.Up && b.elevator != null && b.elevator.Pointer == el.Pointer && !b.activated)
                  ?? all.FirstOrDefault(b => b.type == ElevatorButton.ButtonType.Up && !b.activated && b.gameObject.activeInHierarchy);
            Main.Log($"[legstat] moving with '{el.name}' (up button={go?.gameObject.name ?? "none"} of '{go?.elevator?.name}')");
            if (go != null) go.Interact(); else el.MoveToNextFloor();
        }

        static void Roll()
        {
            foreach (var li in UnityEngine.Object.FindObjectsOfType<LootableItem>(true))
            {
                if (li == null || li.ItemType != LootableItem.LootType.Weapon) continue;
                if (li.gameObject.scene.name == "DontDestroyOnLoad") continue;
                int key = li.GetInstanceID();
                if (!_seen.Add(key)) continue;
                var r = Tracker.GroundRoll(li);
                // 同じ武器・同じ階で、同じ抽選を 2000 回（記録だけ。武器には付けない）
                var wi = Tracker.ItemById(li.ItemId);
                var cnt = new int[5];
                for (int k = 0; k < 2000; k++) { var x = RarityTable.Roll(wi, Tracker.BaseTier(wi)); if (!x.Legacy) cnt[(int)x.Tier]++; }
                Main.Log($"[legstat] boot={_boot} src=simtier floor={RarityTable.CurrentFloor()} weapon={Tracker.NameOf(li.ItemId)} counts={string.Join("/", cnt)}");
                _total++;
                bool enemy = li.GetComponentInParent<Il2CppEnemyAI.EnemyHealth>(true) != null || li.transform.root.GetComponentInChildren<Il2CppEnemyAI.EnemyHealth>(true) != null;
                Main.Log($"[legstat] boot={_boot} src={(enemy ? "enemy" : "floor")} floor={RarityTable.CurrentFloor()} weapon={Tracker.NameOf(li.ItemId)} id={li.ItemId} tier={r?.Tier} active={li.gameObject.activeInHierarchy} obj={li.gameObject.name} scene={li.gameObject.scene.name}");
            }
        }
    }
}
