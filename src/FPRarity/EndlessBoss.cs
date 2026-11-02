using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using UnityEngine.AI;

using FPModOption;

namespace FPRarity
{
    // エンドレスモードに、通常のランのオフィスのボス部屋（NEE 警備隊長 Victor、DungeonSnapBoss1）を 10% で出す。
    // ・エンドレスの階の決め方（LevelElevator.SetupEndlessFloor）の代わりに、通常ランの2階の設定（SetupNormalFloor(2)）を使う
    // ・ボス部屋は通常ランと同じ（ボス1体＋雑魚3体が配置済み、時間で湧く敵はなし、ボスを倒すと扉が開く）
    // ・階層が上がるほど、まず雑魚を増やす（10階ごとに +3、最大 +6）。雑魚が上限に達したら、増やした雑魚をボスに置き換えていく
    //   （10階ごとに1体、最大3体。敵の総数は増えず、中身が強くなる）
    //     1〜10階 +0 / 11〜20階 雑魚+3 / 21〜30階 雑魚+6 / 31〜40階 雑魚+5 ボス+1 / 41〜50階 雑魚+4 ボス+2 / 51階〜 雑魚+3 ボス+3
    //   増やす敵は部屋に配置済みの敵の複製を、近くの歩ける場所（NavMesh 上）に置く
    // ・扉は増えたボスも含めて全員倒すまで開かない
    // ・ボスは硬くする：体力は階に関係なく 2000（元の 5倍）、頭のダメージ倍率は最大 4倍（元は 55倍で頭に当たれば一撃）
    // ・ボスの体力バーは出さない
    public static class EndlessBoss
    {
        public const float Chance = 0.10f;
        const int NormalBossFloor = 2;     // 通常ランでオフィスのボスが出る階（SetupNormalFloor の番号）
        public const int MaxExtraGrunts = 6;
        public const int FloorsPerStep = 10;      // 10階ごとに敵が強くなる
        public const int GruntsPerStep = 3;
        public const int MaxExtraBosses = 3;
        public const float BossHp = 2000f;   // 元は 400
        public const float BossHeadMax = 4f;

        // 隠した体力バーの描画部品（次の階で元に戻す）
        static readonly List<Behaviour> _hiddenGraphics = new();
        static readonly List<Renderer> _hiddenRenderers = new();
        static readonly System.Random Rng = new();

        public static bool IsBossFloor { get; private set; }
        public static int Floor { get; private set; }      // エンドレスの階（最初の階が 0）
        static bool _spawned;
        static float _nextPoll, _seenAt = -1f;
        public static readonly List<Il2CppEnemyAI.EnemyHealth> ExtraBosses = new();
        public static int ExtraGruntsSpawned { get; private set; }

        // floor はエンドレスの階（最初の階が 0）。0〜9 が段階 0、10〜19 が段階 1 …
        static int Step(int floor) => Math.Max(0, floor) / FloorsPerStep;
        static int GruntSteps => MaxExtraGrunts / GruntsPerStep;   // 雑魚が上限に達するまでの段階数（2）
        public static int ExtraBossCount(int floor) => Math.Clamp(Step(floor) - GruntSteps, 0, MaxExtraBosses);
        public static int ExtraGrunts(int floor) => Math.Min(Step(floor), GruntSteps) * GruntsPerStep - ExtraBossCount(floor);   // ボスに置き換えた分は減る

        public static void Install(HarmonyLib.Harmony h)
        {
            var c = typeof(EndlessBoss);
            bool ok = SafePatcher.Patch(h, typeof(LevelElevator), "SetupEndlessFloor", null, nameof(SetupEndlessFloor_Pre), null, c);
            // 増えたボスが残っている間は扉を開けない
            ok &= SafePatcher.Patch(h, typeof(BossfightDoorsChecker), "CheckBossDeath", null, nameof(BossDeath_Pre), null, c);
            ok &= SafePatcher.Patch(h, typeof(BossfightDoorsChecker), "OnBossDied", null, nameof(BossDeath_Pre), null, c);
            if (!ok) GameVersion.AddDegraded("エンドレスのボス部屋");
        }

        // テスト用：UserData/FPRarity/endless_boss.txt があれば必ずボス部屋にする（中に数字があれば、その階として敵を増やす）
        static string ForceFile => Path.Combine(Main.Dir, "endless_boss.txt");
        internal static bool ForceNormalForTest;     // テスト用：必ず通常の階にする（PerfProbe）
        internal static int ForceFloorForTest = -1;  // テスト用：この階として作る（PerfProbe）
        internal static int ForceNormalSetupForTest = -1;  // テスト用：通常ランのこの番号の階を作る（SurvivalProbe）

        // 他の Mod（FPSurvival）が階の作り方を差し替える（Link.FloorOverride）。true を返したらその Mod が作ったので、ここでは何もしない

        static bool SetupEndlessFloor_Pre(LevelElevator __instance, ref int currentFloor)
        {
            try
            {
                if (Link.FloorOverride != null)
                {
                    bool handled = false;
                    try { handled = Link.FloorOverride(__instance, currentFloor); }
                    catch (Exception e) { Main.Log("[endless] floor override failed: " + e); }
                    if (handled)
                    {
                        IsBossFloor = false; _spawned = false; ExtraBosses.Clear(); MainBoss = null; _checker = null; _bars = null;
                        ShowHealthBars();
                        return false;
                    }
                }
                if (ForceNormalSetupForTest >= 0)
                {
                    IsBossFloor = false;
                    Main.Log($"[endless] test: normal-run floor {ForceNormalSetupForTest}");
                    __instance.SetupNormalFloor(ForceNormalSetupForTest);
                    return false;
                }
                if (ForceFloorForTest >= 0) { currentFloor = ForceFloorForTest; __instance.CurrentFloor = ForceFloorForTest; }
                bool forced = File.Exists(ForceFile);
                Floor = currentFloor;
                if (forced && int.TryParse(File.ReadAllText(ForceFile).Trim(), out var f)) Floor = f;
                IsBossFloor = !ForceNormalForTest && (forced || Rng.NextDouble() < Chance);
                _spawned = false; _seenAt = -1f; _doorsOpened = false; ExtraBosses.Clear(); ExtraGruntsSpawned = 0; MainBoss = null;
                _checker = null; _bars = null;
                ShowHealthBars();   // 前の階で隠した体力バーを戻す
                Main.Log($"[endless] floor {currentFloor}: {(IsBossFloor ? $"BOSS (office boss room, +{ExtraGrunts(Floor)} grunts, +{ExtraBossCount(Floor)} bosses)" : "normal")}{(forced ? $" (forced as floor {Floor})" : "")}");
                if (!IsBossFloor) return true;
                __instance.SetupNormalFloor(NormalBossFloor);
                return false;
            }
            catch (Exception e)
            {
                Main.Log("[endless] boss floor setup failed: " + e);
                IsBossFloor = false;
                return true;   // 失敗したら通常のエンドレスの階
            }
        }

        static bool BossDeath_Pre() => !ExtraBosses.Any(e => e != null && e.health > 0f);

        // ボス部屋ができたら（ボスの扉の確認役が現れたら）一度だけ敵を増やす
        public static Il2CppEnemyAI.EnemyHealth MainBoss { get; private set; }

        public static void Tick()
        {
            if (!IsBossFloor || Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + 0.5f;
            HideHealthBars();
            if (_spawned) { OpenDoorsWhenAllBossesDead(); return; }
            try
            {
                var checker = Checker();
                if (checker == null) return;
                var boss = checker.BossHealth.TryCast<Il2CppEnemyAI.EnemyHealth>();
                if (boss == null) return;
                // 部屋ができてすぐは雑魚がまだ置かれていないので、少し待つ
                if (_seenAt < 0f) { _seenAt = Time.realtimeSinceStartup; return; }
                var room = checker.transform.root;
                var grunts = room.GetComponentsInChildren<Il2CppEnemyAI.EnemyHealth>(true).Where(e => e.Pointer != boss.Pointer && e.health > 0f && e.gameObject.activeInHierarchy).ToList();
                if (grunts.Count == 0 && Time.realtimeSinceStartup - _seenAt < 15f) return;
                _spawned = true;
                Spawn(grunts.Select(g => g.transform.parent != null ? g.transform.parent.gameObject : g.gameObject).ToList(), ExtraGrunts(Floor), false);
                Spawn(new List<GameObject> { boss.transform.parent != null ? boss.transform.parent.gameObject : boss.gameObject }, ExtraBossCount(Floor), true);
                MainBoss = boss;
                _bars = null;   // 増やした敵の分も含めて、次の確認で体力バーを探し直す（一度だけ）
                Toughen(boss);
                foreach (var e in ExtraBosses) Toughen(e);
                Main.Log($"[endless] boss room ready: grunts {grunts.Count}+{ExtraGruntsSpawned}, bosses 1+{ExtraBosses.Count}");
            }
            catch (Exception e)
            {
                _spawned = true;
                Main.Log("[endless] extra enemies failed: " + e);
            }
        }

        // ゲームは元のボスが倒れた瞬間に一度だけ扉を開けようとする（OnBossDied）。増えたボスが残っている間はそれを止めているので、
        // 増えたボスが全員倒れたら、ゲームの扉を開ける処理をここで呼ぶ
        static bool _doorsOpened;
        static void OpenDoorsWhenAllBossesDead()
        {
            if (_doorsOpened || MainBoss == null || MainBoss.health > 0f || !BossDeath_Pre()) return;
            try
            {
                var checker = Checker();
                if (checker == null) return;
                _doorsOpened = true;
                if (checker.opened) return;
                try { checker.OnBossDied(); }
                catch (Exception e)
                {
                    // OnBossDied は先に Steam の実績を解除する。Steam につながっていないと例外で止まるので、実績なしで開ける方を使う
                    Main.Log("[endless] OnBossDied failed (" + e.Message.Split((char)10)[0] + "), using OnBossDiesForce");
                    if (!checker.opened) checker.OnBossDiesForce();
                }
                Main.Log($"[endless] all bosses down: doors opened={checker.opened}");
            }
            catch (Exception e)
            {
                Main.Log("[endless] opening the doors failed: " + e.Message);
            }
        }

        // ボスを硬くする（体力を増やし、頭のダメージ倍率を抑える）
        public static void ToughenBoss(Il2CppEnemyAI.EnemyHealth e) => Toughen(e);

        // 他の Mod が出したボスの体力バーも描画しない（階の UI にあるバーを探して隠す）
        public static void HideBossBars() => HideHealthBars();

        static void Toughen(Il2CppEnemyAI.EnemyHealth e)
        {
            if (e == null) return;
            float m = e.health > 0f ? BossHp / e.health : 1f;
            e.health = BossHp;
            try { e.totalHealth *= m; } catch { }
            var root = e.transform.parent != null ? e.transform.parent : e.transform;
            var parts = new List<string>();
            foreach (var c in root.GetComponentsInChildren<Il2CppBallistics.LivingEntityCollider>(true))
            {
                string part = $"{c.gameObject.name} x{c.DamageMultiplier:0.##}";
                if (c.DamageMultiplier > BossHeadMax) { c.DamageMultiplier = BossHeadMax; part += $"->x{BossHeadMax:0.##}"; }
                parts.Add(part);
            }
            Main.Log($"[endless] boss '{e.transform.parent?.name}' hp -> {e.health:0}, parts: {string.Join(", ", parts)}");
        }

        // 体力バーは「描画だけ」止める。バーのオブジェクト自体を無効にすると、ボスが体力バーを見つけられず
        // 弾が当たるたびにエラーになり、ダメージが入らなくなる（体力無限になる）ため。
        // シーン全体を探すのは重い（1回 8〜25ms）。0.5 秒ごとに探すとカクつくので、一度見つけたら使い回す
        static BossfightDoorsChecker _checker;
        static List<HealthBar> _bars;

        static BossfightDoorsChecker Checker()
        {
            if (Refs.Alive(_checker)) return _checker;
            _checker = UnityEngine.Object.FindObjectsOfType<BossfightDoorsChecker>().FirstOrDefault(d => d.BossHealth != null);
            return _checker;
        }

        static void HideHealthBars()
        {
            // 体力バーは画面の UI にあり、階の最初から（非表示のものも含めて）存在する。階ごとに一度だけ探す
            if (_bars == null)
            {
                _bars = new List<HealthBar>();
                foreach (var o in UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<HealthBar>(), FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    var hb = o.TryCast<HealthBar>();
                    if (hb != null) _bars.Add(hb);
                }
            }
            foreach (var hb in _bars)
            {
                if (!Refs.Alive(hb)) continue;
                int n = 0;
                foreach (var g in hb.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                    if (g.enabled) { g.enabled = false; _hiddenGraphics.Add(g); n++; }
                foreach (var r in hb.GetComponentsInChildren<Renderer>(true))
                    if (r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); n++; }
                if (n > 0) Main.Log($"[endless] hid boss health bar '{hb.gameObject.name}' ({n} parts)");
            }
        }

        static void ShowHealthBars()
        {
            foreach (var g in _hiddenGraphics) if (g != null) g.enabled = true;
            foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
            _hiddenGraphics.Clear();
            _hiddenRenderers.Clear();
        }

        static void Spawn(List<GameObject> templates, int count, bool boss)
        {
            if (templates.Count == 0) return;
            for (int i = 0; i < count; i++)
            {
                var t = templates[i % templates.Count];
                if (!FindSpot(t.transform.position, out var pos)) { Main.Log($"[endless] no free spot near {t.name}"); continue; }
                var clone = UnityEngine.Object.Instantiate(t, pos, t.transform.rotation, t.transform.parent);
                clone.name = t.name + (boss ? " (FPR boss)" : " (FPR)");
                if (boss)
                {
                    var eh = clone.GetComponentInChildren<Il2CppEnemyAI.EnemyHealth>(true);
                    if (eh != null) ExtraBosses.Add(eh);
                }
                else ExtraGruntsSpawned++;
            }
        }

        // 元の敵から 2〜5m 離れた、歩ける場所（NavMesh 上）
        static bool FindSpot(Vector3 near, out Vector3 pos)
        {
            for (int k = 0; k < 20; k++)
            {
                float a = (float)(Rng.NextDouble() * Math.PI * 2); float r = 2f + (float)Rng.NextDouble() * 3f;
                var p = near + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
                if (NavMesh.SamplePosition(p, out var hit, 1.5f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - near.y) < 1.5f) { pos = hit.position; return true; }
            }
            pos = near; return false;
        }
    }
}
