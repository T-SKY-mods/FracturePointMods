using System;
using System.Collections.Generic;
using System.Linq;
using FPModOption;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.AI;
using EnemyHealth = Il2CppEnemyAI.EnemyHealth;
using Object = UnityEngine.Object;

namespace FPSurvival
{
    // サバイバルの本体（ランのシーンの屋上）。
    //   開始するとエレベーターが自動で屋上へ → 屋上に出た所から開始 → ウェーブ → 全滅させたら休憩 → 次のウェーブ …
    //   エレベーターの部屋（非常口の奥）の扉は閉めたまま。休憩中に扉を調べるとサバイバル終了 → 扉が開いて部屋に入れる
    //   （入ったら出られない）→ 部屋に出る武器箱をあさって武器 → エレベーターのボタンで拠点へ脱出
    //   倒れたら拠点へ戻る（持ち込んだ物は失わない。稼いだドルと武器箱は無し）
    public static class Arena
    {
        public enum Phase { Off, WaitArena, Prep, Wave, Break, Ending, Leaving }
        public static Phase Now { get; private set; }
        public static int Wave { get; private set; }
        public static int Cleared { get; private set; }
        public static bool Hard => Stash.Pending?.Hard == true;
        // 敵の中身を決めるウェーブ（ハードは表示のウェーブ 1 がノーマルの 40）
        static int ContentWave(int wave) => Hard ? wave + Waves.HardOffset : wave;

        public const int MapFloor = 12;            // 通常ランの最終ボスの屋上（SetupNormalFloor の番号）
        const string MapRoom = "boss 10(Clone)";
        // 回復ドリンクは買うたびに $1,000 ずつ高くなる（最初は $10,000）
        public const float FirstHealPrice = 10000f, HealPriceStep = 1000f;
        static int _drinksBought;
        public static float HealPrice => FirstHealPrice + HealPriceStep * _drinksBought;
        public static void OnDrinkBought() => _drinksBought++;

        // テストで短くできる時間
        public static float PrepSeconds = 10f, BreakSeconds = 60f, SpawnInterval = 4f, BossInterval = 6f;

        public static bool InSurvival => Stash.InRun && Main.Scene == Main.RunScene;
        public static bool InArena => InSurvival && Now >= Phase.Prep && Now <= Phase.Ending;

        class Tracked
        {
            public EnemyHealth Health;
            public GameObject Root;
            public Kind Kind;
            public bool Dead;
            public LootableChanceItems Search;   // 死体をあさる所（倒すとゲームが有効にする）
            public float DiedAt;
            public bool PoseChecked;
            public bool Searched;
            public float Closest = float.MaxValue, ProgressAt = -1f, LastHp;   // 残り少ないときの見張り
            public Il2CppEnemyAI.StateController Ai;   // ボスの巡回で使う
        }

        static readonly System.Random Rng = new();
        static readonly List<Tracked> _enemies = new();
        static readonly List<GameObject> _waypointObjs = new();
        static readonly List<Vector3> _spawnPoints = new();
        static readonly List<Vector3> _recentSpawns = new();
        static readonly List<float> _calibers = new();
        static List<Kind> _queue = new();
        static WaveSpec _spec;
        static Il2CppSystem.Collections.Generic.List<Transform> _wpList;
        static Transform _room;
        static LevelElevator _elevator;
        static Vector3 _doorPos, _inward, _lobbyPos, _cratePos, _roofStart;
        static EnemySpawner _spawner;
        static GameObject _bossTemplate, _crate, _gate, _exit;
        static DoorInteraction _exitDoor;
        static LootableChanceItems _crateLoot;
        static bool _crateOpened, _doorsClosed, _rode, _entered;
        static Door _door;
        static Transform _doorLeaf;
        static Quaternion _leafClosed;
        static int _doorDir = 1;
        static PlayerHealthManager _phm;
        static float _sceneAt, _phaseUntil, _nextSpawn, _nextPoll, _nextWaypoint, _foundAt, _deathAt = -1f, _nextRegen, _drinkFixUntil;
        static TMP_Text _hudTop, _hudSub;
        static GameObject _hud;

        public static void OnSceneLoaded()
        {
            RestoreWind();   // 前のシーンで下げた風の音（プレイヤーが残っていれば）
            _enemies.Clear(); _waypointObjs.Clear(); _spawnPoints.Clear(); _recentSpawns.Clear(); _calibers.Clear(); _queue.Clear();
            _room = null; _elevator = null; _spawner = null; _bossTemplate = null; _crate = null; _gate = null; _exit = null; _exitDoor = null; _crateLoot = null;
            _crateOpened = false; _doorsClosed = false; _entered = false; _door = null; _doorDir = 1; _drinksBought = 0; _quiet.Clear(); _loggedSounds = false; _nextSoundScan = 0f; _strayLogged.Clear(); _nextStray = 0f; _nextQuiet = 0f; _rode = false; _phm = null; _hud = null; _hudTop = null; _hudSub = null;
            _foundAt = -1f; _deathAt = -1f; _drinkFixUntil = 0f; Wave = 0; Cleared = 0;
            _sceneAt = Time.realtimeSinceStartup;
            Now = InSurvival ? Phase.WaitArena : Phase.Off;
            if (Now == Phase.WaitArena)
            {
                Main.Log("[arena] survival run scene: going up to the rooftop");
                // 初めて倒れた後の 1 回きりの贈り物（武器）は、サバイバルでは渡さない（通常のランで受け取れるよう残す）
                foreach (var g in Object.FindObjectsOfType<GiftAfterDeath>())
                {
                    g.StopAllCoroutines(); g.enabled = false; Object.Destroy(g);
                    Main.Log("[arena] the after-death gift is kept for a normal run");
                }
                StopLuckyCat();
            }
        }

        // 招き猫（拠点の家具）はランの開始ごとに武器・防具・回復・戦利品をランダムに 1 つくれる。サバイバルは持ち込みだけなので、このランでは働かせない
        internal static void StopLuckyCat()
        {
            foreach (var c in Resources.FindObjectsOfTypeAll<LuckyCatStatue>())
            {
                if (c == null || !c.gameObject.scene.IsValid()) continue;
                c.StopAllCoroutines();
                if (c.gameObject.scene.name == Main.RunScene) Object.Destroy(c);   // この場面の物だけ（拠点の家具そのものは触らない）
                Main.Log($"[arena] lucky cat gift stopped for survival (scene {c.gameObject.scene.name})");
            }
        }

        public static void Tick()
        {
            if (Now == Phase.Off) return;
            float now = Time.realtimeSinceStartup;
            switch (Now)
            {
                case Phase.WaitArena: WaitForArena(now); return;
                case Phase.Leaving: TickLeaving(now); return;
            }
            if (now >= _nextPoll)
            {
                _nextPoll = now + 0.25f;
                PollEnemies();
                if (now >= _nextRegen) { _nextRegen = now + 1f; NoRegen(); }
                if (now < _drinkFixUntil) FixDrinks();
                if (now >= _nextQuiet) { _nextQuiet = now + 0.5f; QuietHeli(); }
                if ((Now == Phase.Wave || Now == Phase.Prep || Now == Phase.Break) && PlayerInRoom()) PutOnRoof(false);
                if (Now == Phase.Ending) TickEnding();
                if (Now == Phase.Wave) CheckStragglers(now);
                if (Now == Phase.Wave && now >= _nextBossWalk) { _nextBossWalk = now + 3f; BossWalk(); }
            }
            if (now >= _nextWaypoint) { _nextWaypoint = now + 2f; MoveWaypoints(); }
            switch (Now)
            {
                case Phase.Prep:
                    if (now >= _phaseUntil) StartWave(1);
                    break;
                case Phase.Wave:
                    int alive = _enemies.Count(e => !e.Dead);
                    if (_queue.Count > 0 && alive < _spec.MaxAlive && now >= _nextSpawn)
                    {
                        var k = _queue[0];
                        if (SpawnOne(k)) _queue.RemoveAt(0);
                        _nextSpawn = now + (k == Kind.Boss ? BossInterval : SpawnInterval);
                    }
                    else if (_queue.Count == 0 && alive == 0) WaveCleared(now);
                    break;
                case Phase.Break:
                    if (now >= _phaseUntil) StartWave(Wave + 1);
                    break;
            }
            UpdateHud(now);
        }

        // ---------- 屋上へ（エレベーターを自動で動かす）→ 準備 ----------

        static void WaitForArena(float now)
        {
            if (now < _nextPoll) return;
            _nextPoll = now + 0.5f;
            var gl = GameObject.Find("GeneratedLevel");
            var room = gl != null ? gl.transform.Find(MapRoom) : null;
            if (room == null)
            {
                if (!_rode && now - _sceneAt > 2f) RideUp();
                return;
            }
            if (_foundAt < 0f) { _foundAt = now; return; }
            if (now - _foundAt < 2f) return;
            _room = room;
            try { Setup(); }
            catch (Exception e) { Main.Log("[arena] setup failed: " + e); }
        }

        // ランの最初の階からエレベーターに乗せて屋上へ向かわせる（ボタンを押したのと同じ）
        static void RideUp()
        {
            var el = Object.FindObjectsOfType<LevelElevator>().FirstOrDefault();
            var fps = Refs.Fps;
            if (el == null || fps == null) return;
            _rode = true;
            var col = el.EnterCollider;
            var pos = col != null ? col.bounds.center : el.transform.position;
            var cc = fps.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            fps.transform.position = pos;
            if (cc != null) cc.enabled = true;
            try { el.MoveToNextFloor(); Main.Log("[arena] riding the elevator to the rooftop"); }
            catch (Exception e) { Main.Log("[arena] MoveToNextFloor failed: " + e.Message); }
        }

        static void Setup()
        {
            _elevator = GameObject.Find("LevelElevator_0")?.GetComponent<LevelElevator>();
            var door = _room.Find("door");
            var elevPos = _elevator != null && _elevator.EnterCollider != null ? _elevator.EnterCollider.bounds.center : new Vector3(-11.8f, 1f, 0.5f);
            _doorPos = door != null ? door.position : new Vector3(-5.3f, 1f, 1.9f);
            _inward = elevPos - _doorPos; _inward.y = 0f; _inward = _inward.sqrMagnitude > 0.01f ? _inward.normalized : Vector3.left;
            var side = Vector3.Cross(Vector3.up, _inward);
            _lobbyPos = OnNav(_doorPos + _inward * 2.5f, 2f) ?? _doorPos + _inward * 2.5f;
            _cratePos = OnNav(_doorPos + _inward * 4.5f + side * 1.2f, 1.5f) ?? OnNav(_doorPos + _inward * 4.5f - side * 1.2f, 1.5f) ?? _lobbyPos + _inward;
            _roofStart = OnNav(_doorPos - _inward * 4f, 2f) ?? _doorPos - _inward * 4f;
            Main.Log($"[arena] elevator={_elevator != null} door={door != null} at {_doorPos} inward={_inward} lobby={_lobbyPos} crate={_cratePos} start={_roofStart}");

            DisableBossEvent();
            ClearLoot();
            foreach (var ds in Object.FindObjectsOfType<DifficultyStopwatch>()) { try { ds.DisableTimer(); } catch { } }
            Cover.Place(_room, _doorPos, _inward);
            KeepDoorClosed();
            MoveVending();
            Ambience();

            var spawners = Object.FindObjectsOfType<EnemySpawner>().ToList();
            _spawner = spawners.FirstOrDefault();
            BuildSpawnPoints(spawners.Select(s => s.transform.position).ToList());
            _wpList = new Il2CppSystem.Collections.Generic.List<Transform>();
            for (int i = 0; i < 4; i++)
            {
                var go = new GameObject("FPSurvival waypoint " + i);
                _waypointObjs.Add(go);
                _wpList.Add(go.transform);
            }

            foreach (var h in Resources.FindObjectsOfTypeAll<EnemyHealth>())
                if (h != null && !h.gameObject.scene.IsValid() && h.transform.parent != null && h.transform.parent.name == "BossFirst") { _bossTemplate = h.transform.parent.gameObject; break; }

            FindCalibers();
            _phm = Object.FindObjectOfType<PlayerHealthManager>();
            NoRegen();
            BuildGate();
            BuildExit();
            BuildHud();

            // 屋上（非常口の外）から始める
            PutOnRoof(true);
            MoveWaypoints();
            SetGate(true);
            Now = Phase.Prep;
            _phaseUntil = Time.realtimeSinceStartup + PrepSeconds;
            Main.Log($"[arena] ready: spawner={_spawner != null} spawnPoints={_spawnPoints.Count} bossTemplate={_bossTemplate != null} crate={_crateLoot != null} calibers=[{string.Join(",", _calibers)}]");
        }

        static Vector3? OnNav(Vector3 p, float r) => NavMesh.SamplePosition(p, out var hit, r, NavMesh.AllAreas) ? hit.position : null;

        // 屋上のボス戦（ヘリ・ドローン・演出）を始めさせない。屋上の店・ATM なども使えなくする（Patches 側）
        static readonly HashSet<string> TriggerTypes = new() { "StartAnimTrigger", "DroneActivator", "OnTriggerAction", "FinalBossBehaviour", "FinalExplosion", "LoadDeathScene", "RocketSpawnDelayed", "ExecuteDelayed" };
        static readonly HashSet<string> RemoveTypes = new() { "FPVDroneAI", "Landmine", "RpgCrate" };

        static void DisableBossEvent()
        {
            var log = new List<string>();
            foreach (var mb in _room.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var name = mb.GetIl2CppType().Name;
                if (TriggerTypes.Contains(name))
                {
                    foreach (var col in mb.GetComponents<Collider>()) col.enabled = false;
                    mb.enabled = false;
                    log.Add(name);
                }
                else if (RemoveTypes.Contains(name) && mb.gameObject.activeSelf)
                {
                    mb.gameObject.SetActive(false);
                    log.Add(name + "(off)");
                }
            }
            Main.Log("[arena] boss event disabled: " + string.Join(", ", log.GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}")));
        }

        // 屋上に落ちている物・調べられる物を片付ける（回復・武器を拾えないように）
        static void ClearLoot()
        {
            int items = 0, chance = 0;
            foreach (var li in _room.GetComponentsInChildren<LootableItem>(true)) { if (li.gameObject.activeSelf) { li.gameObject.SetActive(false); items++; } }
            foreach (var c in _room.GetComponentsInChildren<LootableChanceItems>(true)) { Object.Destroy(c); chance++; }
            int heals = 0;
            foreach (var f in _room.GetComponentsInChildren<FastHealItem>(true)) { if (f.gameObject.activeSelf) { f.gameObject.SetActive(false); heals++; } }
            Main.Log($"[arena] loot cleared: items={items} searchables={chance} heals={heals}");
            // ランの開始時にゲームが渡す回復・戦利品も持ち込めない
            var inv = Refs.Inv;
            if (inv != null)
            {
                var extra = new List<(int, int)>();
                foreach (var kv in inv.HealthItems) if (kv.Key != null && kv.Value > 0) extra.Add((kv.Key.Id, kv.Value));
                foreach (var (id, n) in extra) inv.DeleteHealthItem(id, n, false);
                var loot = new List<(int, int)>();
                foreach (var kv in inv.LootItems) if (kv.Key != null && kv.Value > 0) loot.Add((kv.Key.Id, kv.Value));
                foreach (var (id, n) in loot) inv.DeleteLoot(id, n, false);
                if (extra.Count + loot.Count > 0) Main.Log($"[arena] removed items given at the run start: heals={extra.Count} loot={loot.Count}");
            }
        }

        // 屋上の歩ける場所を 4m 間隔で集める（エレベーターの部屋の外で、屋上の敵の出現位置からたどり着ける所）
        static void BuildSpawnPoints(List<Vector3> refs)
        {
            var b = new Bounds(_doorPos, Vector3.zero);
            foreach (var r in _room.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
            var target = refs.Count > 0 ? refs[0] : _doorPos - _inward * 10f;
            var path = new NavMeshPath();
            for (float x = b.min.x; x <= b.max.x; x += 4f)
                for (float z = b.min.z; z <= b.max.z; z += 4f)
                {
                    if (!NavMesh.SamplePosition(new Vector3(x, _doorPos.y + 0.5f, z), out var hit, 1.5f, NavMesh.AllAreas)) continue;
                    var p = hit.position;
                    if (Mathf.Abs(p.y - _doorPos.y) > 1.6f) continue;                       // 屋上の床だけ（給水塔などの上は除く）
                    if (Vector3.Dot(p - _doorPos, _inward) > -3f) continue;                 // エレベーターの部屋の中・扉の前は除く
                    if (Cover.Blocks(p)) continue;                                           // 置いた木の壁の所は除く
                    if (!NavMesh.CalculatePath(p, target, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;
                    _spawnPoints.Add(p);
                }
        }

        static void FindCalibers()
        {
            var inv = Refs.Inv;
            var prefabs = inv?.WeaponsList?.weaponPrefabs;
            if (prefabs == null) return;
            for (int i = 0; i < prefabs.Count; i++)
            {
                if (prefabs[i] == null) continue;
                var wi = Weapons.ItemByPrefab(prefabs[i].name);
                var c = Survival.CaliberOf(inv, wi);
                if (c > 0f) _calibers.Add(c);   // 武器 1 本につき 1 つ（同じ口径でも武器ごとに数える）
            }
        }

        static void NoRegen()
        {
            try { if (_phm != null) _phm.healthRegenAmount = 0f; } catch { }
        }

        // ---------- エレベーターの部屋の入口（非常口） ----------
        // ゲームの非常口は自動ドアで閉められないので、入口に見えない壁を置いて出入りを止める（敵の通り道もふさぐ）

        static void BuildGate()
        {
            _gate = new GameObject("FPSurvival exit gate");
            _gate.transform.SetParent(_room, true);
            _gate.transform.position = _doorPos + Vector3.up * 1.3f + _inward * 0.1f;
            _gate.transform.rotation = Quaternion.LookRotation(_inward);
            var size = new Vector3(2.2f, 2.8f, 0.4f);
            var box = _gate.AddComponent<BoxCollider>();
            box.size = size;
            var obs = _gate.AddComponent<NavMeshObstacle>();
            obs.shape = NavMeshObstacleShape.Box;
            obs.size = size;
            obs.carving = true;
        }

        static void SetGate(bool closed)
        {
            if (_gate != null && _gate.activeSelf != closed) _gate.SetActive(closed);
        }

        static bool PlayerInRoom()
        {
            var fps = Refs.Fps;
            return fps != null && Vector3.Dot(fps.transform.position - _doorPos, _inward) > 0.2f;
        }

        // 自販機をエレベーターの部屋から出し、非常口の外の壁ぎわ（扉の横）に並べる。ウェーブの合間に外で買えるように
        static void MoveVending()
        {
            var machines = new List<Transform>();
            foreach (var v in _room.GetComponentsInChildren<VendingMachine>())
            {
                var t = v.transform;
                while (t.parent != null && t.parent != _room) t = t.parent;
                if (!machines.Contains(t)) machines.Add(t);
            }
            if (machines.Count == 0) { Main.Log("[arena] no vending machine"); return; }
            // 自販機は 1 台だけ（残りは片付ける）
            foreach (var extra in machines.Skip(1)) { extra.gameObject.SetActive(false); Main.Log($"[arena] vending '{extra.name}' removed"); }
            machines = machines.Take(1).ToList();
            var along = Vector3.Cross(Vector3.up, _inward);   // 壁に沿う向き
            foreach (float sign in new[] { -1f, 1f })
            {
                var walls = new List<Vector3>();
                var normals = new List<Vector3>();
                var clutter = new List<Collider>();
                bool blocked = false;
                for (int i = 0; i < machines.Count && !blocked; i++)
                {
                    var p = _doorPos - _inward * 2f + along * sign * (1.6f + 1.15f * i);
                    if (!Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out var floor, 4f, ~0, QueryTriggerInteraction.Ignore)) { blocked = true; break; }
                    // 外から壁に向けて測る。手前の椅子やゴミなどの小物は飛ばして、建物の壁の面を探す
                    var from = floor.point + Vector3.up * 1.2f;
                    Vector3? wall = null;
                    var normal = -_inward;
                    foreach (var h in Physics.RaycastAll(from, _inward, 4f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
                    {
                        if (FlatOnWall(h.collider)) continue;
                        if (IsSmall(h.collider)) { clutter.Add(h.collider); continue; }
                        wall = new Vector3(h.point.x, floor.point.y, h.point.z);
                        normal = Flat(h.normal);   // 壁の面の向き（非常口の向きとは少しずれている）
                        break;
                    }
                    if (wall == null) { Main.Log($"[arena] vending side {sign:+0;-0}: no wall found"); blocked = true; break; }
                    // 壁の前（自販機が置かれる所）に配管など大きな物があればこの側は使わない。小物は片付ける
                    var center = wall.Value + normal * 0.72f + Vector3.up * 1.2f;
                    foreach (var c in Physics.OverlapBox(center, new Vector3(0.55f, 0.9f, 0.6f), Quaternion.LookRotation(normal), ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (FlatOnWall(c)) continue;               // 壁に付いた配線や看板
                        if (IsSmall(c)) { clutter.Add(c); continue; }
                        Main.Log($"[arena] vending side {sign:+0;-0}: blocked by '{c.name}' size {c.bounds.size}");
                        blocked = true; break;
                    }
                    walls.Add(wall.Value);
                    normals.Add(normal);
                }
                if (blocked || walls.Count < machines.Count) continue;
                foreach (var c in clutter)
                {
                    var t = c.transform;
                    while (t.parent != null && t.parent != _room) t = t.parent;
                    if (t == _room || machines.Contains(t) || !t.gameObject.activeSelf) continue;
                    Main.Log($"[arena] moved aside '{t.name}' for the vending machines");
                    t.gameObject.SetActive(false);
                }
                for (int i = 0; i < machines.Count; i++)
                {
                    var m = machines[i];
                    float lift = m.position.y - Bottom(m);
                    // 部屋の中では部屋の方を向いているので、外向きに回す
                    var n = normals[i];
                    m.rotation = Quaternion.FromToRotation(Flat(m.forward), n) * m.rotation;
                    m.position = walls[i] + Vector3.up * lift;
                    // 本体の当たり判定の中心で揃えて、背中を壁の面に付ける（基準点や光の表示は本体の中心からずれている）
                    Physics.SyncTransforms();
                    var body = m.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).OrderByDescending(c => c.bounds.size.y).FirstOrDefault();
                    if (body != null)
                    {
                        var bb = body.bounds;
                        float depth = Mathf.Abs(bb.extents.x * n.x) + Mathf.Abs(bb.extents.z * n.z);
                        var d = walls[i] + n * (depth + 0.03f) - bb.center; d.y = 0f;
                        m.position += d;
                    }
                    var obs = m.gameObject.AddComponent<NavMeshObstacle>();
                    obs.carving = true;
                    Main.Log($"[arena] vending '{m.name}' moved outside to {m.position} (wall {walls[i]})");
                }
                Physics.SyncTransforms();
                return;
            }
            Main.Log("[arena] no room for the vending machines outside");
        }

        // 壁に張り付いた薄い物（壁と直角の向きの厚みが薄い）
        //   建物の壁そのもの（横に長い）は含めない
        static bool FlatOnWall(Collider c)
        {
            var z = c.bounds.size;
            float depth = Mathf.Abs(z.x * _inward.x) + Mathf.Abs(z.z * _inward.z);
            float width = Mathf.Abs(z.x * _inward.z) + Mathf.Abs(z.z * _inward.x);
            return depth < 0.35f && width < 2.5f && z.y < 3.5f;
        }

        // 片付けてよい小物（椅子・ゴミなど）：背が低く小さい物
        static bool IsSmall(Collider c) => c.bounds.size.y <= 1.3f && c.bounds.size.x <= 1.5f && c.bounds.size.z <= 1.5f;

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 0.001f ? v.normalized : Vector3.forward; }

        static float Bottom(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return t.position.y;
            float y = float.MaxValue;
            foreach (var r in rs) y = Mathf.Min(y, r.bounds.min.y);
            return y;
        }

        // 屋上の環境：ヘリの音と、ずっと鳴っている「コー」という環境音（雨の音 rain inside）を小さく、少し明るく
        public const float HumVolume = 0.15f;
        const float HeliVolume = 0.35f, ExposureUp = 0.5f, AmbientUp = 1.4f;
        static float _nextQuiet;
        static void Ambience()
        {
            QuietHeli();
            QuietWind();
            try
            {
                RenderSettings.ambientIntensity *= AmbientUp;
                foreach (var v in Object.FindObjectsOfType<UnityEngine.Rendering.Volume>())
                {
                    if (v == null || !v.isGlobal || v.gameObject.scene.name != Main.RunScene) continue;
                    var profile = v.profile;   // この場面用の複製（元の設定は変えない）
                    UnityEngine.Rendering.Universal.ColorAdjustments ca = null;
                    foreach (var c in profile.components) { ca = c?.TryCast<UnityEngine.Rendering.Universal.ColorAdjustments>(); if (ca != null) break; }
                    if (ca == null) { Main.Log($"[arena] volume '{v.name}' has no color adjustments"); continue; }
                    float before = ca.postExposure.overrideState ? ca.postExposure.value : 0f;
                    ca.active = true;
                    ca.postExposure.overrideState = true;
                    ca.postExposure.value = before + ExposureUp;
                    Main.Log($"[arena] brighter: '{v.name}' exposure {before:0.00} -> {ca.postExposure.value:0.00}, ambient x{AmbientUp}");
                }
            }
            catch (Exception e) { Main.Log("[arena] brightness failed: " + e.Message); }
        }

        // 風の音（走ったとき・振り向いたときの風切り音）は、プレイヤーの操作の部品が音量の上限を持っていて毎フレーム決め直す。
        //   その上限をサバイバルの間だけ下げ、拠点へ戻るときに元に戻す
        const float WindScale = 0.15f;
        static Il2CppDemo.Scripts.Runtime.Character.FPSController _windFor;
        static float _windMax, _windMin, _windRunMax;

        static void QuietWind()
        {
            var fc = Refs.Fps;
            if (fc == null || (_windFor != null && !_windFor.WasCollected && _windFor.Pointer == fc.Pointer)) return;
            RestoreWind();
            _windFor = fc;
            _windMax = fc.maxVolume; _windMin = fc.minVolume; _windRunMax = fc.MaxVolume;
            fc.maxVolume = _windMax * WindScale; fc.minVolume = _windMin * WindScale; fc.MaxVolume = _windRunMax * WindScale;
            Main.Log($"[arena] wind sounds quieter: turning {_windMin:0.00}-{_windMax:0.00} -> {fc.minVolume:0.00}-{fc.maxVolume:0.00}, running max {_windRunMax:0.00} -> {fc.MaxVolume:0.00}");
        }

        static void RestoreWind()
        {
            var fc = _windFor;
            _windFor = null;
            if (fc == null || fc.WasCollected) return;
            fc.maxVolume = _windMax; fc.minVolume = _windMin; fc.MaxVolume = _windRunMax;
            Main.Log("[arena] wind sounds restored");
        }
        static readonly List<(AudioSource src, float vol)> _quiet = new();
        static bool _loggedSounds;
        static float _nextSoundScan;

        // 音を探すのはシーン全体なので重い。10 秒ごとに探し直し、間は見つけた音の音量だけ直す
        static void QuietHeli()
        {
            if (_room == null) return;
            float now = Time.realtimeSinceStartup;
            if (now >= _nextSoundScan)
            {
                _nextSoundScan = now + 10f;
                _quiet.Clear();
                if (!_loggedSounds)
                    foreach (var a in Object.FindObjectsOfType<AudioSource>())
                        if (a != null && a.clip != null && a.loop) Main.Log($"[arena] looping sound '{a.name}' clip '{a.clip.name}' vol {a.volume:0.00} blend {a.spatialBlend:0.0}");
                _loggedSounds = true;
                foreach (var a in _room.GetComponentsInChildren<AudioSource>(true))
                    if (a != null && a.clip != null && a.clip.name == "heli") _quiet.Add((a, HeliVolume));
                foreach (var a in Object.FindObjectsOfType<AudioSource>())
                    if (a != null && a.clip != null && a.clip.name == "rain_inside") _quiet.Add((a, HumVolume));
            }
            foreach (var (a, want) in _quiet)
                if (a != null && !a.WasCollected && a.volume > want + 0.001f) a.volume = want;
        }


        // 非常口の扉（エレベーターの部屋の入口）は自動で開く仕組みなので、近づいても開かないようにする。
        //   ゲームの「扉を開ける」も外し、休憩中だけ Mod の「サバイバルを終了する」を調べられるようにする
        static void KeepDoorClosed()
        {
            _door = Object.FindObjectsOfType<Door>().Where(d => d != null && Vector3.Distance(d.transform.position, _doorPos) < 3f)
                                                   .OrderBy(d => Vector3.Distance(d.transform.position, _doorPos)).FirstOrDefault();
            if (_door == null) { Main.Log("[arena] fire exit door not found"); return; }
            _doorLeaf = _door.door;
            if (_doorLeaf != null) _leafClosed = _doorLeaf.localRotation;
            int triggers = 0, prompts = 0;
            foreach (var t in Object.FindObjectsOfType<DoorTrigger>())
            {
                if (t == null || t.Door == null || t.Door.Pointer != _door.Pointer) continue;
                // 外側（屋上側）の仕掛けの向きで開けると、扉は部屋の中へ開く
                if (Vector3.Dot(t.transform.position - _doorPos, _inward) < 0f) _doorDir = t.direction;
                foreach (var c in t.GetComponents<Collider>()) c.enabled = false;
                Object.Destroy(t);
                triggers++;
            }
            foreach (var di in Object.FindObjectsOfType<DoorInteraction>())
            {
                if (di == null || di == _exitDoor || di.door == null || di.door.Pointer != _door.Pointer) continue;
                Object.Destroy(di);
                prompts++;
            }
            Main.Log($"[arena] fire exit door '{_door.name}' kept closed (open={_door.isOpen}, triggers {triggers}, prompts {prompts}, dir {_doorDir})");
        }

        static void OpenDoor()
        {
            if (_door == null) return;
            try { _door.OpenDoorFast(_doorDir, true, true); Main.Log("[arena] fire exit door opened"); }
            catch (Exception e) { Main.Log("[arena] open door failed: " + e.Message); }
        }

        // 終了後：開いた扉から部屋に入ったら、もう出られない
        static void TickEnding()
        {
            var fps = Refs.Fps;
            if (fps == null) return;
            float depth = Vector3.Dot(fps.transform.position - _doorPos, _inward);
            if (!_entered)
            {
                if (depth < 1.2f) return;
                _entered = true;
                SetGate(true);
                Main.Log("[arena] entered the elevator room");
            }
            else if (depth < 0.2f) Teleport(_lobbyPos, -_inward);
        }

        // 屋上の非常口の前に立たせる
        static void PutOnRoof(bool quiet) => Teleport(_roofStart, -_inward);

        // 非常口の扉を調べるとサバイバル終了（休憩中だけ）。扉の前に調べられる所を置く
        //   通り抜けられるよう当たり判定はトリガーにする
        static void BuildExit()
        {
            _exit = new GameObject("FPSurvival exit");
            _exit.transform.SetParent(_room, true);
            _exit.transform.position = _doorPos + Vector3.up * 1.2f;
            _exit.transform.rotation = Quaternion.LookRotation(_inward);
            var box = _exit.AddComponent<BoxCollider>();
            box.size = new Vector3(1.4f, 2.4f, 0.6f);
            box.isTrigger = true;
            _exitDoor = _exit.AddComponent<DoorInteraction>();
            _exit.SetActive(false);
        }

        static void SetExit(bool on)
        {
            if (_exit != null && _exit.activeSelf != on) _exit.SetActive(on);
        }

        public static bool IsExit(Il2CppSystem.Object o) => o != null && _exitDoor != null && o.Pointer == _exitDoor.Pointer;
        public static string ExitText() => Main.T("サバイバルを終了する", "End survival");   // エレベーター前の扉

        public static void OnExit()
        {
            if (Now == Phase.Break && Cleared >= 1) End();
        }

        // ---------- ウェーブ ----------

        static void StartWave(int n)
        {
            ClearCorpses();
            ClearDrops();
            SetGate(true);
            SetExit(false);
            if (PlayerInRoom()) PutOnRoof(true);
            Wave = n;
            _spec = SpecForTest ?? Waves.For(ContentWave(n));
            _queue = Waves.Queue(_spec, Rng);
            Now = Phase.Wave;
            Main.Log($"[arena] wave {n}: {_spec}");
            if (!_doorsClosed && _elevator != null) { try { _elevator.CloseDoors(); } catch { } _doorsClosed = true; }
            int first = Math.Min(Math.Min(Waves.InitialSpawn, _spec.MaxAlive), _queue.Count);
            for (int i = 0; i < first; i++) if (SpawnOne(_queue[0])) _queue.RemoveAt(0);
            _nextSpawn = Time.realtimeSinceStartup + SpawnInterval;
        }

        static void WaveCleared(float now)
        {
            Cleared = Wave;
            Now = Phase.Break;
            _phaseUntil = now + BreakSeconds;
            SetExit(true);
            if (Stash.Pending != null) { Stash.Pending.Waves = Cleared; Stash.SavePending(); }
            Stash.UpdateHandicap(Refs.Inv);
            Main.Log($"[arena] wave {Wave} cleared, ${Refs.Inv?.DollarsCount:0}");
            Main.Notify(Main.T($"ウェーブ {Wave} クリア", $"Wave {Wave} cleared"));
            if (Cleared % 5 == 0) GiveRareHeal();
        }

        // 5 ウェーブごとにレアの回復アイテムを 1 個（ゲームの「〇〇 をインベントリに追加」が出る）。拠点には持ち帰らない
        const int RareRarity = 2;   // LootRarity.Rare
        static void GiveRareHeal()
        {
            var inv = Refs.Inv;
            var items = inv?.HealthData?.HealthItems;
            if (items == null) return;
            var all = new List<HealthItem>();
            for (int i = 0; i < items.Count; i++) if (items[i] != null) all.Add(items[i]);
            var rare = all.Where(h => (int)h.GetRarity() == RareRarity).ToList();
            if (rare.Count == 0) rare = all.OrderByDescending(h => h.HealAmount).Take(1).ToList();
            if (rare.Count == 0) return;
            var pick = rare[Rng.Next(rare.Count)];
            try { inv.AddHealthItem(pick.Id, 1); Main.Log($"[arena] rare heal for wave {Cleared}: {pick.GetName()} (id {pick.Id}, heal {pick.HealAmount})"); }
            catch (Exception e) { Main.Log("[arena] AddHealthItem failed: " + e.Message); }
        }

        static bool SpawnOne(Kind k)
        {
            var fps = Refs.Fps;
            if (fps == null) return false;
            if (!PickPoint(fps.transform.position, out var pos)) { Main.Log("[arena] no spawn point"); return false; }
            GameObject root = null;
            try
            {
                if (k == Kind.Boss && _bossTemplate != null) root = SpawnBoss(pos, fps.transform.position);
                else root = SpawnGrunt(k == Kind.Boss ? Kind.Special : k, pos, fps.transform);
            }
            catch (Exception e) { Main.Log($"[arena] spawn {k} failed: {e}"); }
            if (root == null) return false;
            var eh = root.GetComponentInChildren<EnemyHealth>(true);
            if (eh == null) { Main.Log($"[arena] spawned {root.name} has no health"); Object.Destroy(root); return false; }
            if (k == Kind.Boss) { BossHealth(eh); Link.HideBossBars?.Invoke(); _nextBossWalk = Time.realtimeSinceStartup + 0.5f; }
            _enemies.Add(new Tracked { Health = eh, Root = root, Kind = k, Search = root.GetComponentInChildren<LootableChanceItems>(true) });
            _recentSpawns.Add(pos); if (_recentSpawns.Count > 4) _recentSpawns.RemoveAt(0);
            Main.Log($"[arena] + {k} '{root.name}' hp={eh.health:0} at {pos} (player {Vector3.Distance(pos, fps.transform.position):0}m)");
            return true;
        }

        static GameObject SpawnGrunt(Kind k, Vector3 pos, Transform player)
        {
            if (_spawner == null) return null;
            var parent = _spawner.transform;
            var before = new HashSet<IntPtr>();
            for (int i = 0; i < parent.childCount; i++) before.Add(parent.GetChild(i).Pointer);
            parent.position = pos;
            _spawner.Type = k switch { Kind.Office => Enemy.EnemyType.OfficeSecurity, Kind.Gallery => Enemy.EnemyType.GallerySecurity, _ => Enemy.EnemyType.SpecialForces };
            _spawner.Spawn(_spec.Damage, 1, player, _wpList);
            GameObject found = null;
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var c = parent.GetChild(i);
                if (before.Contains(c.Pointer)) continue;
                // 出現口を次の場所へ動かしたときに一緒に動かないよう、出た敵は屋上の下へ移す。2 体以上出たら残りは消す
                if (found == null) { c.SetParent(_room, true); found = c.gameObject; }
                else { Main.Log($"[arena] spawner made an extra enemy '{c.name}', removed"); Object.Destroy(c.gameObject); }
            }
            return found;
        }

        // ボスの体力は駐車場の特殊部隊（310〜390）より少し多いくらい。頭への倍率もエンドレスのボスと同じく 4 倍まで
        static void BossHealth(EnemyHealth e)
        {
            float m = e.health > 0f ? Waves.BossHp / e.health : 1f;
            e.health = Waves.BossHp;
            try { e.totalHealth *= m; } catch { }
            var root = e.transform.parent != null ? e.transform.parent : e.transform;
            foreach (var c in root.GetComponentsInChildren<Il2CppBallistics.LivingEntityCollider>(true))
                if (c.DamageMultiplier > Waves.BossHeadMax) c.DamageMultiplier = Waves.BossHeadMax;
        }

        static GameObject SpawnBoss(Vector3 pos, Vector3 player)
        {
            var dir = player - pos; dir.y = 0f;
            var go = Object.Instantiate(_bossTemplate, pos, dir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(dir) : Quaternion.identity);
            go.name = "BossFirst (FPSurvival)";
            go.SetActive(true);
            return go;
        }

        // プレイヤーから 20m 以上離れていて、見えていない場所（無ければ条件をゆるめる）
        static bool PickPoint(Vector3 player, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (_spawnPoints.Count == 0) return false;
            var cam = Camera.main;
            bool Ok(Vector3 p, float minDist, bool needHidden)
            {
                float d = Vector3.Distance(p, player);
                if (d < minDist || d > 65f) return false;
                if (_recentSpawns.Any(r => Vector3.Distance(r, p) < 3f)) return false;
                return !needHidden || !Visible(cam, p);
            }
            foreach (var (minDist, hidden) in new[] { (20f, true), (14f, true), (12f, false) })
            {
                var c = _spawnPoints.Where(p => Ok(p, minDist, hidden)).ToList();
                if (c.Count > 0) { pos = c[Rng.Next(c.Count)]; return true; }
            }
            pos = _spawnPoints.OrderByDescending(p => Vector3.Distance(p, player)).First();
            return true;
        }

        static bool Visible(Camera cam, Vector3 p)
        {
            if (cam == null) return false;
            var head = p + Vector3.up * 1.5f;
            var v = cam.WorldToViewportPoint(head);
            if (v.z <= 0f || v.x < -0.1f || v.x > 1.1f || v.y < -0.1f || v.y > 1.1f) return false;
            return !Physics.Linecast(cam.transform.position, head);
        }

        // 敵が向かう先（プレイヤーの周り）を少しずつ動かす
        static void MoveWaypoints()
        {
            var fps = Refs.Fps;
            if (fps == null) return;
            foreach (var go in _waypointObjs)
            {
                if (go == null) continue;
                float a = (float)(Rng.NextDouble() * Math.PI * 2), r = 3f + (float)Rng.NextDouble() * 6f;
                var p = fps.transform.position + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
                go.transform.position = OnNav(p, 3f) ?? fps.transform.position;
            }
        }

        // ---------- ボスの巡回 ----------
        //   ボスは元の部屋の巡回先を持ったまま出てくるので、離れたまま端を回り続けてプレイヤーを見つけない。
        //   雑魚と同じプレイヤーのまわりの巡回先へ歩かせる。プレイヤーを見つけた後はゲームの AI に任せる
        static float _nextBossWalk;

        static void BossWalk()
        {
            foreach (var e in _enemies)
            {
                if (e.Dead || e.Kind != Kind.Boss || e.Root == null) continue;
                try
                {
                    e.Ai ??= e.Root.GetComponentInChildren<Il2CppEnemyAI.StateController>(true);
                    var c = e.Ai; var n = c?.nav;
                    if (n == null || !n.enabled || !n.isOnNavMesh || c.targetInSight || c.currentState?.name != "PatrolState") continue;
                    if (c.patrolWayPoints == null || c.patrolWayPoints.Pointer != _wpList.Pointer) { c.patrolWayPoints = _wpList; c.waypointIndex = 0; }
                    n.SetDestination(_wpList[Rng.Next(_wpList.Count)].position);
                }
                catch (Exception ex) { Main.Log("[arena] boss walk: " + ex.Message); }
            }
        }

        // ---------- 残り少ない敵が見つからないとき ----------
        //   出す敵が無くなり残りが 2 体以下のとき、近づいてこない・見えない・ダメージを受けない状態が続いた敵は
        //   引っかかっているか遠くをうろついているので、プレイヤーから見えない近めの場所（12〜25m）へ移す
        public static float StaleSeconds = 20f;
        public const int StragglerCount = 2;
        static int _relocated;

        static void CheckStragglers(float now)
        {
            var fps = Refs.Fps;
            if (fps == null || _queue.Count > 0) return;
            var alive = _enemies.Where(e => !e.Dead && e.Root != null && e.Health != null && !e.Health.WasCollected).ToList();
            if (alive.Count == 0 || alive.Count > StragglerCount) return;
            var player = fps.transform.position;
            var cam = Camera.main;
            foreach (var e in alive)
            {
                var pos = e.Health.transform.position;
                float d = Vector3.Distance(pos, player);
                bool progress = e.ProgressAt < 0f || d < e.Closest - 2f || e.Health.health < e.LastHp - 0.5f || Visible(cam, pos);
                if (progress) { e.ProgressAt = now; e.Closest = Math.Min(e.Closest, d); e.LastHp = e.Health.health; continue; }
                if (now - e.ProgressAt < StaleSeconds || d < 12f) continue;
                Relocate(e, player, now - e.ProgressAt, d);
                e.ProgressAt = now; e.Closest = float.MaxValue; e.LastHp = e.Health.health;
            }
        }

        static void Relocate(Tracked e, Vector3 player, float stale, float dist)
        {
            var cam = Camera.main;
            var path = new NavMeshPath();
            bool Ok(Vector3 p, float lo, float hi)
            {
                float d = Vector3.Distance(p, player);
                return d >= lo && d <= hi && !Visible(cam, p) && NavMesh.CalculatePath(p, player, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
            }
            var pts = _spawnPoints.Where(p => Ok(p, 12f, 25f)).ToList();
            if (pts.Count == 0) pts = _spawnPoints.Where(p => Ok(p, 10f, 35f)).ToList();
            if (pts.Count == 0) { Main.Log($"[arena] {e.Kind} lost {dist:0}m away for {stale:0}s, no place to move it"); return; }
            var to = pts[Rng.Next(pts.Count)];
            var dir = player - to; dir.y = 0f;
            var rot = dir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(dir) : Quaternion.identity;
            try
            {
                var agents = e.Root.GetComponentsInChildren<NavMeshAgent>();
                foreach (var a in agents) { if (!a.enabled) continue; a.Warp(to); if (a.isOnNavMesh) a.isStopped = false; }
                if (agents.Length == 0) e.Root.transform.position = to;
                var pm = e.Health.puppet;
                if (pm != null) pm.Teleport(to, rot, true);
                _relocated++;
                Main.Log($"[arena] {e.Kind} lost {dist:0}m away for {stale:0}s, moved to {to} ({Vector3.Distance(to, player):0}m from the player)");
            }
            catch (Exception ex) { Main.Log($"[arena] moving {e.Kind} failed: {ex.Message}"); }
        }

        // ---------- 倒した敵（お金・死体をあさる） ----------

        static void PollEnemies()
        {
            if (Time.realtimeSinceStartup >= _nextStray) { _nextStray = Time.realtimeSinceStartup + 1f; RemoveStrays(); }
            float now = Time.realtimeSinceStartup;
            foreach (var e in _enemies)
            {
                if (e.Dead) { if (!e.PoseChecked && now >= e.DiedAt + 2f) { e.PoseChecked = true; FixStanding(e); MakeSearchable(e); } continue; }
                bool gone = e.Root == null || e.Health == null || e.Health.WasCollected;
                if (gone) { e.Dead = true; Main.Log($"[arena] {e.Kind} vanished (no reward)"); continue; }
                if (e.Health.health > 0f && e.Health.transform.position.y > _doorPos.y - 15f) continue;
                e.Dead = true;
                e.DiedAt = now;
                if (e.Health.health > 0f) { Main.Log($"[arena] {e.Kind} fell off the roof"); Object.Destroy(e.Root); continue; }
                OnKilled(e);
            }
        }

        // サバイバルが出した敵以外（屋上の仕掛けやゲームの出現口が出した敵）は消す。ウェーブの数より多く出ないように
        static float _nextStray;
        static readonly HashSet<string> _strayLogged = new();
        static void RemoveStrays()
        {
            var roots = new HashSet<IntPtr>();
            foreach (var e in _enemies) if (e.Root != null) roots.Add(e.Root.Pointer);
            foreach (var h in Object.FindObjectsOfType<EnemyHealth>())
            {
                if (h == null || h.health <= 0f) continue;
                var root = EnemyRoot(h.transform);
                if (roots.Contains(root.gameObject.Pointer)) continue;
                if (_strayLogged.Add(root.name)) Main.Log($"[arena] removed an enemy not spawned by survival: '{root.name}' under '{root.parent?.name}'");
                Object.Destroy(root.gameObject);
            }
        }

        // 敵 1 体ぶんの一番上（出現口・屋上・GeneratedLevel の直下）
        static Transform EnemyRoot(Transform t)
        {
            while (t.parent != null && t.parent != _room && t.parent.name != "GeneratedLevel" && t.parent.GetComponent<EnemySpawner>() == null) t = t.parent;
            return t;
        }

        // まれにラグドールが動かず、立ったまま死んでいる敵がいる。倒れていなければラグドールに切り替えて倒す
        static void FixStanding(Tracked e)
        {
            try
            {
                if (e.Root == null || e.Health == null || e.Health.WasCollected) return;
                var bodies = e.Root.GetComponentsInChildren<Rigidbody>();
                if (bodies.Length == 0) return;
                float lo = float.MaxValue, hi = float.MinValue;
                int kinematic = 0;
                foreach (var b in bodies) { var y = b.position.y; lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y); if (b.isKinematic) kinematic++; }
                var pm = e.Health.puppet;
                string info = pm == null ? "no puppet" : $"mode {pm.mode}, state {pm.state}, pin {pm.pinWeight:0.00}, muscle {pm.muscleWeight:0.00}, enabled {pm.enabled}";
                // 立ったまま：体の高さがある・人形がまだ生きている扱い・腰が床から高い（しゃがんだ姿勢だと高さだけでは分からない）
                var hips = pm != null && pm.muscles != null && pm.muscles.Length > 0 ? pm.muscles[0].rigidbody : null;
                float hipsUp = hips == null ? 0f : hips.position.y - e.Health.transform.position.y;
                bool puppetAlive = pm != null && pm.state == Il2CppRootMotion.Dynamics.PuppetMaster.State.Alive;
                if (hi - lo < 1.1f && !puppetAlive && hipsUp < 0.6f) return;
                // 死んだ後も歩かないように
                foreach (var a in e.Root.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>())
                {
                    if (a.enabled && a.isOnNavMesh) a.isStopped = true;
                    a.enabled = false;
                }
                info += $", hips {hipsUp:0.00} m up";
                Main.Log($"[arena] {e.Kind} died standing (height {hi - lo:0.00}, kinematic {kinematic}/{bodies.Length}, {info}), dropping it");
                var anim = e.Health.GetComponentInChildren<Animator>();
                if (anim != null) anim.enabled = false;
                if (pm != null)
                {
                    pm.enabled = true;
                    pm.mode = Il2CppRootMotion.Dynamics.PuppetMaster.Mode.Active;
                    pm.pinWeight = 0f;
                    pm.muscleWeight = 0f;
                    if (pm.state == Il2CppRootMotion.Dynamics.PuppetMaster.State.Alive) pm.Kill();
                }
                foreach (var b in bodies)
                {
                    b.isKinematic = false;
                    b.useGravity = true;
                    b.WakeUp();
                }
                bodies[0].AddForce(Vector3.down, ForceMode.VelocityChange);
            }
            catch (Exception ex) { Main.Log("[arena] fix standing body failed: " + ex.Message); }
        }

        // 遠くで倒した敵は（ゲームの簡易表示のまま死ぬと）あされるようにならないことがあるので、こちらで有効にする
        static void MakeSearchable(Tracked e)
        {
            if (e.Searched || e.Search == null || e.Search.WasCollected || e.Search.enabled) return;
            e.Search.enabled = true;
            Main.Log($"[arena] {e.Kind} body made searchable");
        }

        static void OnKilled(Tracked e)
        {
            var inv = Refs.Inv;
            int reward = Waves.Reward(e.Kind, ContentWave(Wave));
            // ゲームの AddDollars は「〇ドルをインベントリに追加」を出すので、通知なしで足す
            try { if (inv != null) inv.DollarsCount = inv.DollarsCount + reward; } catch (Exception ex) { Main.Log("[arena] add dollars failed: " + ex.Message); }
            Main.Log($"[arena] killed {e.Kind} +${reward} total=${inv?.DollarsCount:0}");
        }

        // 死体は次のウェーブが始まるときに片付ける
        static void ClearCorpses()
        {
            int n = 0;
            for (int i = _enemies.Count - 1; i >= 0; i--)
            {
                var e = _enemies[i];
                if (!e.Dead) continue;
                if (e.Root != null) Object.Destroy(e.Root);
                _enemies.RemoveAt(i);
                n++;
            }
            if (n > 0) Main.Log($"[arena] cleared {n} bodies");
        }

        // 倒した敵が落とした物（壊れた武器部品など）も、死体と同じく次のウェーブの開始で片付ける
        static void ClearDrops()
        {
            var names = new List<string>();
            foreach (var li in Object.FindObjectsOfType<LootableItem>())
            {
                if (li == null || !li.gameObject.activeInHierarchy || li.gameObject.scene.name != Main.RunScene) continue;
                if (_crate != null && li.transform.IsChildOf(_crate.transform)) continue;
                if (_enemies.Any(e => !e.Dead && e.Root != null && li.transform.IsChildOf(e.Root.transform))) continue;   // 生きている敵が持っている武器
                names.Add(li.gameObject.name);
                Object.Destroy(li.gameObject);
            }
            if (names.Count > 0) Main.Log($"[arena] cleared {names.Count} drops: {string.Join(", ", names.GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}"))}");
        }

        // 倒した敵の死体（体力 0 になった直後も含む）
        public static bool IsCorpse(LootableChanceItems c)
        {
            if (!InArena || c == null) return false;
            foreach (var e in _enemies)
            {
                if (e.Root == null || !(e.Dead || (e.Health != null && !e.Health.WasCollected && e.Health.health <= 0f))) continue;
                if (e.Search != null && e.Search.Pointer == c.Pointer) return true;
                if (c.transform.IsChildOf(e.Root.transform)) { e.Search ??= c; return true; }
            }
            return false;
        }

        // 死体をあさる：手持ちの武器のどちらかの弾を 5〜20 発（メイン・サブは同じ確率、弾の種類で変えない）
        public static void SearchCorpse(LootableChanceItems c)
        {
            c.enabled = false;
            foreach (var e in _enemies) if (e.Search != null && e.Search.Pointer == c.Pointer) e.Searched = true;
            var inv = Refs.Inv;
            if (inv == null || _calibers.Count == 0) return;
            float cal = _calibers[Rng.Next(_calibers.Count)];
            int count = Rng.Next(5, 21);
            var nm = inv.NotificationsManager;
            int shown = nm != null && nm.Container != null ? nm.Container.childCount : -1;
            inv.AddAmmo(cal, count);
            Main.Log($"[arena] body searched: {count} x {cal}");
            if (shown >= 0 && nm.Container.childCount > shown) return;   // ゲームが通知を出した
            string name = null;
            foreach (var kv in inv.AmmoStored) if (kv.Key != null && Mathf.Abs(kv.Key.Caliber - cal) < 0.001f) { try { name = kv.Key.GetName(); } catch { } break; }
            Main.Notify($"{name ?? cal.ToString()} x{count}");
        }

        // 自販機の缶は 1 本で体力が満タンになるようにする（出てくるまで少し待つことがあるので数秒見張る）
        // 自動販売機の缶は勢いをつけて飛び出すので、機械の中や手の届かない所に引っかかることがある。
        //   買う前からある缶を覚えておき、新しく出た缶は機械の前（プレイヤー側）の足元に置き直す。
        //   ゲームが缶に加えた勢いは次の物理の計算で効くので、少しの間は物理で動かないようにして勢いを消す
        static readonly HashSet<IntPtr> _cansBefore = new();
        static readonly List<(FastHealItem can, Vector3 spot, float at)> _placed = new();
        static VendingMachine _vendor;
        public static int DrinksPlaced { get; private set; }   // テスト用

        public static void BeforeDrink()
        {
            _cansBefore.Clear();
            foreach (var f in Object.FindObjectsOfType<FastHealItem>()) if (f != null) _cansBefore.Add(f.Pointer);
        }

        public static void FullHealDrinks(VendingMachine vm) { _vendor = vm; _drinkFixUntil = Time.realtimeSinceStartup + 3f; FixDrinks(); }

        static void FixDrinks()
        {
            var phm = _phm ?? Object.FindObjectOfType<PlayerHealthManager>();
            if (phm == null) return;
            foreach (var f in Object.FindObjectsOfType<FastHealItem>())
            {
                if (f == null) continue;
                if (f.healAmount < phm.MaxHealth) f.healAmount = phm.MaxHealth;
                if (_cansBefore.Add(f.Pointer)) PlaceDrink(f);
            }
            float now = Time.realtimeSinceStartup;
            for (int i = _placed.Count - 1; i >= 0; i--)
            {
                var (can, spot, at) = _placed[i];
                if (can == null || can.WasCollected) { _placed.RemoveAt(i); continue; }
                if (now - at < 0.4f) { can.transform.position = spot; continue; }
                foreach (var rb in can.GetComponentsInChildren<Rigidbody>())
                {
                    rb.isKinematic = false;
                    rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                }
                _placed.RemoveAt(i);
            }
        }

        static void PlaceDrink(FastHealItem can)
        {
            var vm = _vendor;
            var cam = Camera.main;
            if (vm == null || vm.WasCollected || cam == null) return;
            var player = Refs.Fps != null ? Refs.Fps.transform.position : cam.transform.position;
            var rs = vm.GetComponentsInChildren<Renderer>();
            var b = rs.Length > 0 ? rs[0].bounds : new Bounds(vm.transform.position, Vector3.one);
            foreach (var r in rs) b.Encapsulate(r.bounds);
            var toPlayer = player - b.center; toPlayer.y = 0f;
            var dir = toPlayer.sqrMagnitude > 0.01f ? toPlayer.normalized : vm.transform.forward;
            float reach = Mathf.Max(b.extents.x, b.extents.z) + 0.35f;
            var spot = b.center + dir * Mathf.Min(reach, toPlayer.magnitude);
            // 床の上に置く
            spot.y = Physics.Raycast(spot + Vector3.up * 1.0f, Vector3.down, out var hit, 3f, ~0, QueryTriggerInteraction.Ignore) ? hit.point.y + 0.15f : player.y + 0.15f;
            // 機械と缶の間に壁などがあれば、プレイヤーの目の前にする
            var eye = cam.transform.position;
            if (Physics.Linecast(eye, spot, out var block, ~0, QueryTriggerInteraction.Ignore) && !block.transform.IsChildOf(can.transform))
            {
                var fwd = cam.transform.forward; fwd.y = 0f;
                spot = player + (fwd.sqrMagnitude > 0.01f ? fwd.normalized : dir) * 0.8f + Vector3.up * 0.3f;
            }
            var was = can.transform.position;
            foreach (var rb in can.GetComponentsInChildren<Rigidbody>())
            {
                rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
                rb.position = spot;
            }
            can.transform.position = spot;
            _placed.Add((can, spot, Time.realtimeSinceStartup));
            DrinksPlaced++;
            Main.Log($"[arena] drink moved from {was} to {spot} ({Vector3.Distance(spot, player):0.0} m from the player)");
        }

        // ---------- 武器箱（エレベーターの部屋）と脱出 ----------

        static void SpawnCrate()
        {
            GameObject template = null;
            foreach (var c in Resources.FindObjectsOfTypeAll<LootableChanceItems>())
                if (c != null && !c.gameObject.scene.IsValid() && c.Name == "Weapon Crate") { template = c.gameObject; break; }
            if (template == null) { Main.Log("[arena] no weapon crate template"); return; }
            _crate = Object.Instantiate(template, _cratePos, Quaternion.LookRotation(Vector3.Cross(Vector3.up, -_inward)));
            _crate.transform.SetParent(_room, true);
            _crate.SetActive(true);
            _crateLoot = _crate.GetComponentInChildren<LootableChanceItems>(true);
            Main.Log($"[arena] crate at {_cratePos} (search={_crateLoot != null})");
        }

        public static bool IsCrate(Il2CppSystem.Object o) => o != null && _crateLoot != null && o.Pointer == _crateLoot.Pointer;

        // 武器箱をあさる（サバイバル終了後に部屋に出る）：メイン武器とサブ武器を 1 本ずつ（到達ウェーブで最低レアリティが上がる）
        //   入手は通常のランと同じ（ゲームが武器と一緒に空のマガジンを 1 本入れる）。拠点に戻ってもそのマガジンは残す
        public static readonly List<Tier> CrateTiers = new();   // テスト用：武器箱から出た武器のレアリティ
        public static int CrateWavesForTest = -1;                // テスト用：武器箱の報酬に使ったウェーブ（縛りの分を足したもの）

        public static void OpenCrate()
        {
            if (_crateOpened || Now != Phase.Ending) return;
            var inv = Refs.Inv;
            if (inv == null) return;
            var got = new List<string>();
            // 縛りのポイントの分だけ先のウェーブの報酬にする（表示はしない）
            Stash.UpdateHandicap(inv);
            bool hard = Hard;
            int bonus = Waves.HandicapWaves(Math.Max(0, Stash.Pending?.Handicap ?? 0), hard);
            int waves = Cleared + bonus;
            CrateWavesForTest = waves;
            // レアリティは 1 回だけ抽選して、大型・小型の 2 本とも同じにする
            var tier = Waves.CrateTier(waves, Rng, hard);
            CrateTiers.Clear();
            foreach (var slot in new[] { WeaponItem.SlotType.Big, WeaponItem.SlotType.Small })
            {
                var wi = PickCrateWeapon(inv, slot);
                if (wi == null) { Main.Log($"[arena] crate: no {slot} weapon to give"); continue; }
                // レアリティmod があれば、このレアリティの武器にする（無ければふつうの武器）
                bool rolled = false;
                try { rolled = Link.ExpectIncoming != null && Link.ExpectIncoming(wi, tier); } catch (Exception e) { Main.Log("[arena] crate rarity failed: " + e.Message); }
                bool added = false;
                try { added = inv.AddWeapon(wi); } catch (Exception e) { Main.Log("[arena] AddWeapon failed: " + e.Message); }
                int mags = 0;
                foreach (var m in inv.MagStored) if (m != null && m.WeaponName == wi.GetName()) mags++;
                Main.Log($"[arena] crate: {slot} {wi.GetName()} {tier} (min {Waves.MinTier(waves, hard)}, waves {Cleared} +{bonus}{(hard ? ", hard" : "")}) added={added} mags={mags}");
                if (!added) continue;
                got.Add(wi.GetName());
                if (rolled) CrateTiers.Add(tier);
                Stash.Pending?.CrateWeapons.Add(wi.Id);
            }
            if (got.Count == 0) return;
            // 40 ウェーブ以降はドルも入っている。脱出時に稼いだお金として持ち帰る。AddDollars は通知を出さないので、ゲームの文言で知らせる
            float dollars = Waves.CrateDollars(CrateDollarWavesForTest >= 0 ? CrateDollarWavesForTest : waves, hard);
            if (dollars > 0f)
            {
                try { inv.AddDollars(dollars); } catch (Exception e) { Main.Log("[arena] crate dollars failed: " + e.Message); }
                string fmt = "{0} dollars added to your inventory";
                try { fmt = Il2CppGameLocalization.LocManager.Translate(fmt) ?? fmt; } catch { }
                Main.Notify(string.Format(fmt, dollars.ToString("N0")));
                Main.Log($"[arena] crate: +${dollars:0} total=${inv.DollarsCount:0}");
            }
            Stash.SavePending();
            _crateOpened = true;
            if (_crateLoot != null) _crateLoot.enabled = false;
        }

        // ゲームは同じ武器を 2 つ持たせないので、手持ちと同じ武器は出さない
        static WeaponItem PickCrateWeapon(Inventory inv, WeaponItem.SlotType slot)
        {
            var have = Survival.Held(inv).Select(h => h.Item.Id).ToHashSet();
            var pool = WeaponPool(inv).Select(i => inv.WeaponData.Weapons[i]).Where(w => w != null && w.Type == slot).ToList();
            var fresh = pool.Where(w => !have.Contains(w.Id)).ToList();
            if (fresh.Count == 0) return null;
            return fresh[Rng.Next(fresh.Count)];
        }

        static List<int> WeaponPool(Inventory inv)
        {
            var pool = new List<int>();
            foreach (var l in Resources.FindObjectsOfTypeAll<SpawnableLoot>())
            {
                if (l == null || l.name != "Weapons 1" || l.lootDrop == null) continue;
                foreach (var d in l.lootDrop) if (d != null && d.dropType == LootDrop.DropType.Weapon && d.ItemIndex >= 0 && d.ItemIndex < inv.WeaponData.Weapons.Count) pool.Add(d.ItemIndex);
                break;
            }
            if (pool.Count == 0) for (int i = 0; i < Math.Min(30, inv.WeaponData.Weapons.Count); i++) if (i != 26) pool.Add(i);
            return pool;
        }

        // サバイバル終了：扉を開けて部屋に入れるようにし、武器箱を出し、エレベーターを開ける（入ったら出られない）
        static void End()
        {
            Now = Phase.Ending;
            _entered = false;
            SetGate(false);
            SetExit(false);
            OpenDoor();
            SpawnCrate();
            foreach (var e in _enemies.Where(e => !e.Dead && e.Root != null)) Object.Destroy(e.Root);
            if (_elevator != null) { try { _elevator.OpenDoors(); } catch (Exception e) { Main.Log("[arena] OpenDoors failed: " + e.Message); } }
            Main.Log($"[arena] ended after {Cleared} waves with ${Refs.Inv?.DollarsCount:0}");
        }

        // エレベーターのボタン：終了後は脱出。それまでは使えない（最初に屋上へ上がるときは普通に動く）
        public static bool Escaping => InSurvival && Now == Phase.Ending;

        public static bool OnElevatorButton()
        {
            // 脱出・倒れて拠点へ戻る途中（ラン状態はもう終わっている）にもう一度押されても、次の階（最終ボス）へ行かせない。
            // Now はシーンが変わると Off に戻るので、次の通常のランには影響しない
            if (Now == Phase.Leaving) { Main.Log("[arena] elevator pressed again while leaving: ignored"); return true; }
            if (!InSurvival || Now == Phase.WaitArena || Now == Phase.Off) return false;
            if (Now == Phase.Ending) Escape();
            return true;
        }

        static void Escape()
        {
            Now = Phase.Leaving;
            var run = Stash.Pending;
            float earned = Refs.Inv?.DollarsCount ?? 0f;
            // 持ち帰れるのは稼いだドルと、拾った壊れた武器部品（開始時に戦利品は預かって空にしてあるので、今ある分が拾った分）
            int parts = Refs.Inv != null ? Stash.PartsIn(Refs.Inv) : 0;
            run.State = "escaped"; run.Earned = earned; run.Waves = Cleared; run.PartsFound = parts;
            Stash.SavePending();
            Stash.Offer(Cleared, earned, "escaped");
            Main.Log($"[arena] escape: waves={Cleared} earned=${earned:0} broken parts={parts}");
            LeaveToHub();
        }

        // 倒れたとき：ゲームの死亡処理（持ち物を失う）の代わりに拠点へ戻す
        public static bool OnDie(PlayerHealthManager phm)
        {
            if (!InSurvival) return false;
            _phm = phm ?? _phm;
            KeepAlive();
            if (Now == Phase.Leaving) return true;
            Now = Phase.Leaving;
            var run = Stash.Pending;
            run.State = "died"; run.Earned = 0f; run.Waves = Cleared;
            Stash.SavePending();
            Stash.Offer(Cleared, 0f, "died");
            foreach (var e in _enemies.Where(e => !e.Dead && e.Root != null)) Object.Destroy(e.Root);
            _deathAt = Time.realtimeSinceStartup;
            Main.Log($"[arena] died after {Cleared} waves");
            Main.Notify(Main.T("倒れた", "You went down"), true);
            return true;
        }

        static void KeepAlive()
        {
            try { if (_phm != null) { _phm.died = false; _phm.Health = _phm.MaxHealth; } } catch { }
        }

        static void TickLeaving(float now)
        {
            if (_deathAt < 0f) return;
            KeepAlive();
            if (now - _deathAt < 3f) return;
            _deathAt = -1f;
            LeaveToHub();
        }

        static void LeaveToHub()
        {
            RestoreWind();
            PlayerPrefs.DeleteKey("ENDLESS_MODE");
            if (_hud != null) _hud.SetActive(false);
            try
            {
                if (_elevator != null) { _elevator.ReturnToHub(); return; }
            }
            catch (Exception e) { Main.Log("[arena] ReturnToHub failed: " + e.Message); }
            UnityEngine.SceneManagement.SceneManager.LoadScene(Core.HubScene);
        }

        // ---------- 補助 ----------

        public static void Teleport(Vector3 p, Vector3 face)
        {
            var fps = Refs.Fps;
            if (fps == null) return;
            var cc = fps.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            fps.transform.position = p + Vector3.up * 0.1f;
            face.y = 0f;
            if (face.sqrMagnitude > 0.01f) fps.transform.rotation = Quaternion.LookRotation(face);
            if (cc != null) cc.enabled = true;
        }

        static void BuildHud()
        {
            var like = Refs.Interactions?.Description;
            _hud = new GameObject("FPSurvival HUD");
            var canvas = _hud.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 31000;
            var scaler = _hud.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            _hudTop = HudText("top", like, 34f, -24f);
            _hudSub = HudText("sub", like, 24f, -70f);
        }

        static TMP_Text HudText(string name, TMP_Text like, float size, float y)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_hud.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.sizeDelta = new Vector2(1400f, 60f);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (like != null) { t.font = like.font; t.fontSharedMaterial = like.fontSharedMaterial; }
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.color = Color.white;
            t.outlineWidth = 0.15f;
            t.raycastTarget = false;
            t.enableWordWrapping = false;
            return t;
        }

        static void UpdateHud(float now)
        {
            if (_hudTop == null) return;
            int alive = _enemies.Count(e => !e.Dead);
            float dollars = Refs.Inv?.DollarsCount ?? 0f;
            switch (Now)
            {
                case Phase.Prep:
                    _hudTop.text = (Hard ? Main.T("サバイバル ハード", "SURVIVAL HARD") : Main.T("サバイバル", "SURVIVAL")) + $"   ${dollars:N0}";
                    _hudSub.text = Main.T($"ウェーブ 1 開始まで {Math.Max(0, _phaseUntil - now):0} 秒", $"Wave 1 starts in {Math.Max(0, _phaseUntil - now):0}s");
                    break;
                case Phase.Wave:
                    _hudTop.text = Main.T($"ウェーブ {Wave}　残り {_queue.Count + alive}", $"WAVE {Wave}   {_queue.Count + alive} left") + $"   ${dollars:N0}";
                    _hudSub.text = "";
                    break;
                case Phase.Break:
                    _hudTop.text = Main.T($"ウェーブ {Cleared} クリア", $"WAVE {Cleared} CLEARED") + $"   ${dollars:N0}";
                    _hudSub.text = Main.T($"次のウェーブまで {Math.Max(0, _phaseUntil - now):0} 秒", $"Next wave in {Math.Max(0, _phaseUntil - now):0}s");
                    break;
                case Phase.Ending:
                    _hudTop.text = Main.T($"到達ウェーブ {Cleared}", $"Reached wave {Cleared}") + $"   ${dollars:N0}";
                    _hudSub.text = "";
                    break;
            }
        }

        // ---------- テスト用 ----------
        public static IEnumerable<EnemyHealth> AliveForTest() => _enemies.Where(e => !e.Dead && e.Health != null).Select(e => e.Health);
        public static IEnumerable<EnemyHealth> DeadForTest() => _enemies.Where(e => e.Dead && e.Health != null).Select(e => e.Health);
        public static IEnumerable<LootableChanceItems> CorpseSearchForTest() => _enemies.Where(e => e.Dead && e.Search != null).Select(e => e.Search);
        public static int QueueForTest => _queue.Count;
        internal static int CrateDollarWavesForTest = -1;   // テスト用：武器箱のドルだけこのウェーブとして出す
        public static int RelocatedForTest => _relocated;
        public static GameObject CrateForTest => _crate;
        public static GameObject GateForTest => _gate;
        public static GameObject ExitForTest => _exit;
        public static DoorInteraction ExitDoorForTest => _exitDoor;
        public static Door DoorForTest => _door;
        // 扉の板が閉じた向きから何度回ったか（ゲームは開け終わると扉の仕組みを消すので、板の向きで見る）
        public static float DoorAngleForTest => _doorLeaf == null ? -1f : Quaternion.Angle(_doorLeaf.localRotation, _leafClosed);
        public static IEnumerable<GameObject> AliveRootsForTest => _enemies.Where(e => !e.Dead && e.Root != null).Select(e => e.Root);
        public static float WindScaleForTest => WindScale;
        public static float HumVolumeForTest() => _quiet.Where(q => q.src != null && !q.src.WasCollected && q.src.clip?.name == "rain_inside").Select(q => q.src.volume).DefaultIfEmpty(-1f).Max();
        public static Vector3 LobbyForTest => _lobbyPos;
        public static Vector3 RoofStartForTest => _roofStart;
        public static Transform RoomForTest => _room;
        public static Vector3 DoorPosForTest => _doorPos;
        public static Vector3 InwardForTest => _inward;
        public static List<Vector3> SpawnPointsForTest => _spawnPoints;
        public static bool PlayerInRoomForTest => PlayerInRoom();
        public static IEnumerable<GameObject> CorpsesForTest => _enemies.Where(e => e.Dead && e.Root != null).Select(e => e.Root);
        public static WaveSpec? SpecForTest;
        public static WaveSpec SpecNowForTest => _spec;
        public static void SetNextWaveForTest(int n) { Wave = n - 1; Cleared = Math.Max(Cleared, n - 1); }
        public static void SkipTimerForTest() => _phaseUntil = 0f;
        public static bool BossTemplateFound => _bossTemplate != null;
        // ボスの写真用：ボスが見える所に立つ
        public static void PlaceForTest()
        {
            var b = _enemies.Where(e => !e.Dead && e.Kind == Kind.Boss && e.Health != null).Select(e => e.Health.transform.position).ToList();
            if (b.Count == 0) return;
            var c = b.Aggregate(Vector3.zero, (x, y) => x + y) / b.Count;
            var from = OnNav(c + (Refs.Fps.transform.position - c).normalized * 14f, 3f) ?? c + Vector3.back * 14f;
            Teleport(from, c - from);
        }
    }
}
