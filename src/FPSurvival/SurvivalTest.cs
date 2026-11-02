using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using Il2Cpp;
using UnityEngine;
using EnemyHealth = Il2CppEnemyAI.EnemyHealth;
using Object = UnityEngine.Object;

namespace FPSurvival
{
    // 自動テスト（FPRarity の autotest.ps1 -Scenario ... から動く）。セーブは autotest.ps1 が前後で退避・復元する
    //   survival   : 開始 → 自動で屋上 → ウェーブ 1〜2 → 死体をあさる → 自販機 → ボスのウェーブ → 非常口で終了 → 武器箱 → 脱出 → 拠点で返却を確認
    //   survdeath  : 開始 → ウェーブ 1 で倒れる → 拠点で返却（稼ぎ無し・持ち込み品はそのまま）を確認
    //   survquit   : 開始 → ウェーブ 1 の途中でゲームを終了（1 回目）
    //   survresume : もう一度起動して、拠点で返却されることを確認（2 回目）
    public static class SurvivalTest
    {
        static string ShotDir => Path.Combine(Core.Dir, "shots");
        static string ExpectFile => Path.Combine(Main.Dir, "test_expect.txt");
        static string _before;
        static int _partsBefore;
        const int PartsPicked = 3;   // サバイバル中に拾ったことにする壊れた武器部品
        static float _dollarsBefore;
        static int _heldBefore;

        public static void Register()
        {
            TestMode.ExternalScenarios["survival"] = () => Steps(Ending.Escape);
            TestMode.ExternalScenarios["survdeath"] = () => Steps(Ending.Death);
            TestMode.ExternalScenarios["survquit"] = () => Steps(Ending.Quit);
            TestMode.ExternalScenarios["survresume"] = ResumeSteps;
        }

        enum Ending { Escape, Death, Quit }

        static bool SameColor(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;
        static void Check(string what, bool ok) => Core.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static void Shot(string name) { Directory.CreateDirectory(ShotDir); ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"surv_{name}.png")); }
        static void Wait(List<(string, Action)> s, int n, string name = "wait") { for (int i = 0; i < n; i++) s.Add((name, () => { })); }

        static List<(string, Action)> Steps(Ending how)
        {
            // 屋上に着く時間は読み込みの速さで変わるので、準備時間は長くしておき、屋上の確認が済んでからウェーブを始める
            Arena.PrepSeconds = 600f; Arena.BreakSeconds = 120f; Arena.SpawnInterval = 1f; Arena.BossInterval = 1f;
            var s = new List<(string, Action)>
            {
                ("make the loadout 1 primary + 1 secondary", () =>
                {
                    // 持ち込みは大型 1・小型 1 だけ。テスト前のセーブで多く持っていたら、余分を倉庫へ（セーブは autotest.ps1 が戻す）
                    var held = Survival.Held(Refs.Inv);
                    foreach (var type in new[] { WeaponItem.SlotType.Big, WeaponItem.SlotType.Small })
                        foreach (var w in held.Where(h => h.Item.Type == type).Skip(1))
                        {
                            Core.Log($"[survtest] moving {w.Item.GetName()} to the stash for the test");
                            Refs.Bag.MoveWeaponToStash(w.Item);
                        }
                }),
                ("wait", () => { }),
                ("before", () =>
                {
                    var inv = Refs.Inv;
                    _before = Stash.Describe(Stash.Take(inv));
                    _partsBefore = Stash.PartsIn(inv);
                    _dollarsBefore = inv.DollarsCount;
                    _heldBefore = Survival.Held(inv).Count;
                    Core.Log("[survtest] before: " + _before + $" held={_heldBefore}");
                    File.WriteAllText(ExpectFile, _before);
                    // 熊の像（瀕死で無敵）と時の像（時間が遅くなる）を ON にしてから始め、サバイバル中は効かないことを見る
                    PlayerPrefs.SetInt("Mighty Bear Statue_Furniture", 1);
                    PlayerPrefs.SetInt("Statue of Time_Furniture", 1);
                    _prefsBefore = Prefs();
                    Core.Log("[survtest] prefs before: " + _prefsBefore);
                    Check("no pending run at start", Stash.Pending == null);
                    Check("tablet page 2 built", Tablet.ReadyForTest);
                }),
                ("focus tablet + page 2", () =>
                {
                    var t = Tablet.TabletForTest;
                    try { t?.Interact(); } catch (Exception e) { Core.Log("[survtest] focus failed " + e.Message); }
                }),
                ("click the tab", () =>
                {
                    var at = Tablet.TabScreenPointForTest();
                    bool clicked = at != null && Tablet.Click(at.Value);
                    Check($"tab click switches to page 2 (at {at}, page {Tablet.Page})", clicked && Tablet.Page == 1);
                    if (Tablet.Page != 1) Tablet.SetPage(1);
                }),
            };
            Wait(s, 2);
            s.Add(("tablet shot", () => { Shot("tablet_page2"); Core.Log("[survtest] tablet " + Tablet.DebugForTest()); }));
            s.Add(("tablet in English", () =>
            {
                _lang = Il2CppGameLocalization.LocManager.Code;
                Il2CppGameLocalization.LocManager.SetLanguage("en");
                Tablet.SetPage(1);
            }));
            Wait(s, 1);   // 英語の画面の写真は -Scenario survtablet で撮る
            s.Add(("tablet shot (en)", () =>
            {
                Core.Log("[survtest] tablet en " + Tablet.DebugForTest());
                Check($"English rules have no Japanese", !System.Text.RegularExpressions.Regex.IsMatch(Tablet.BodyForTest, @"[぀-ヿ一-鿿]"));
                Il2CppGameLocalization.LocManager.SetLanguage(_lang);
                Tablet.SetPage(1);
            }));
            s.Add(("check loadout rule", () =>
            {
                var held = Survival.Held(Refs.Inv);
                Check("current loadout allowed", Survival.CheckLoadout(held) == null);
                var two = held.Concat(held.Where(w => w.Item.Type == WeaponItem.SlotType.Big)).ToList();
                Check("two primaries refused", Survival.CheckLoadout(two) != null);
                // 縛りの報酬（内部の表）：コモンの小型だけ = +20、全部レジェンダリー = ±0、大型なし・小型レア・ヘルメットレジェンダリー・ベストエリート = 15 ポイント
                int Pts(bool big, Tier? bt, bool small, Tier? st, int? helmet, int? vest) => Waves.WeaponPoints(bt, big) + Waves.WeaponPoints(st, small) + Waves.ArmorPoints(helmet) + Waves.ArmorPoints(vest);
                int max = Pts(false, null, true, Tier.Common, null, null), min = Pts(true, Tier.Legendary, true, Tier.Legendary, 4, 4), ex = Pts(false, null, true, Tier.Rare, 4, 3);
                bool mono = Enumerable.Range(0, 40).All(p => Waves.HandicapWaves(p + 1) >= Waves.HandicapWaves(p));
                Check($"handicap: common handgun only {max}pt +{Waves.HandicapWaves(max)}, all legendary {min}pt +{Waves.HandicapWaves(min)}, example {ex}pt +{Waves.HandicapWaves(ex)}, nothing {Pts(false, null, false, null, null, null)}pt +{Waves.HandicapWaves(30)}",
                      max == 28 && Waves.HandicapWaves(max) == 20 && min == 0 && Waves.HandicapWaves(min) == 0 && ex == 15 && Waves.HandicapWaves(ex) == 11 && Waves.HandicapWaves(30) == 20 && mono
                      && Waves.WeaponPoints(null, false) > Waves.WeaponPoints(Tier.Common, true));
                int now = Survival.HandicapPoints(Refs.Inv, out var detail);
                Check($"handicap of the current loadout: {now}pt ({detail})", now >= 0 && now <= 30);
            }));
            s.Add(("start (SPACE)", () => Object.FindObjectOfType<ButtonStartEndlessMode>()?.StartEndlessRun()));
            Wait(s, 12, "wait load");
            s.Add(("run start: stripped + loadout", () =>
            {
                var inv = Refs.Inv;
                Check("pending run saved", Stash.Pending != null && Stash.Pending.State == "active" && File.Exists(Path.Combine(Main.Dir, "pending.json")));
                Check($"handicap counted at the start ({Stash.Pending?.Handicap}pt)", Stash.Pending != null && Stash.Pending.Handicap >= 0);
                Check($"dollars 0 after the game's $500 (= {inv.DollarsCount})", inv.DollarsCount < 0.5f);
                Check($"roubles 0 (= {inv.RoublesCount})", inv.RoublesCount < 0.5f);
                Check($"no loot ({Count(inv.LootItems)})", Count(inv.LootItems) == 0);
                Core.Log($"[survtest] heals at the run start (the game gives 1; removed on the rooftop): {Count(inv.HealthItems)}");
                // ゲームがエンドレス開始で渡す武器（と空のマガジン）は屋上で取り上げるので、ここでは数えない
                int mags = 0; bool full = true;
                foreach (var m in inv.MagStored) if (m != null && Stash.Pending.Snap.Mags.Any(e => e.Weapon == m.WeaponName)) { mags++; if (m.AmmoCount != m.MaxAmmoCount) full = false; }
                Check($"{Survival.StartMags} full mags per weapon (mags={mags})", mags == Survival.StartMags * _heldBefore && full);
                bool ammo = true;
                foreach (var kv in inv.AmmoStored) if (kv.Value != 0 && kv.Value % Survival.StartAmmo != 0) ammo = false;
                Check("250 rounds per weapon", ammo);
                Core.Log("[survtest] in run: " + Stash.Describe(Stash.Take(inv)));
                Check($"difficulty and furniture effects neutral in the run ({Prefs()})", Prefs() == "Default|1.00|0|0|0|0.00");
            }));
            Wait(s, 18, "wait rooftop (auto ride)");
            s.Add(("arena ready", () =>
            {
                Arena.SkipTimerForTest();
                Check($"arena ready (phase {Arena.Now})", Arena.Now == Arena.Phase.Prep || Arena.Now == Arena.Phase.Wave);
                var inv = Refs.Inv;
                Check($"only the brought weapons ({Survival.Held(inv).Count} = {_heldBefore})", Survival.Held(inv).Count == _heldBefore);
                Check($"no heals on the rooftop ({Count(inv.HealthItems)})", Count(inv.HealthItems) == 0);
                Check($"only their mags ({inv.MagStored.Count} = {Survival.StartMags * _heldBefore})", inv.MagStored.Count == Survival.StartMags * _heldBefore);
                var cm = Object.FindObjectOfType<CardsManager>();
                Check("no cards shown", cm == null || cm.CanvasGroup == null || cm.CanvasGroup.alpha < 0.5f);
                Check("boss template found", Arena.BossTemplateFound);
                Check($"started on the roof, outside the elevator room ({Refs.Fps.transform.position})", !Arena.PlayerInRoomForTest);
                Check("exit gate closed", Arena.GateForTest != null && Arena.GateForTest.activeSelf);
                int covers = Object.FindObjectsOfType<Transform>().Count(t => t.name.StartsWith("FPSurvival cover"));
                Check($"cover placed ({covers})", covers > 0);
                Shot("roof_start");
                Arena.Teleport(new Vector3(20f, 1.1f, 0.5f), Vector3.right);
            }));
            s.Add(("low health", () =>
            {
                var phm = Object.FindObjectOfType<PlayerHealthManager>();
                // 剛熊の像は 10% 未満、時の像は 20% 未満で発動。防具で減らないよう体力を直接下げてから被弾させる
                phm.Health = Math.Max(2, phm.MaxHealth / 20);
                phm.Damage(1);
                Core.Log($"[survtest] health {phm.Health}/{phm.MaxHealth}");
            }));
            Wait(s, 1);
            s.Add(("no statue effects", () =>
            {
                var phm = Object.FindObjectOfType<PlayerHealthManager>();
                Check($"no invincibility at low health ({phm.Health}/{phm.MaxHealth}, active={phm.lowHealthInvincible} cooldown={phm.lowHealthInvincibleOnCooldown})",
                      phm.Health < phm.MaxHealth / 10 && !phm.lowHealthInvincible && !phm.lowHealthInvincibleOnCooldown);
                Check($"no slowdown at low health (active={phm.slowdownActive} cooldown={phm.slowdownOnCooldown} timeScale={Time.timeScale:0.00})",
                      !phm.slowdownActive && !phm.slowdownOnCooldown && Time.timeScale > 0.99f);
                // 対照：像を ON にすると同じ体力で効く（このテストが効果を見分けられること）。
                //   20 秒無敵になるので、この後に倒れるテスト（survdeath）では行わない
                if (how == Ending.Escape)
                {
                    PlayerPrefs.SetInt("Mighty Bear Statue_Furniture", 1);
                    phm.Damage(1);
                    Check($"control: the bear statue would make you invincible here (active={phm.lowHealthInvincible})", phm.lowHealthInvincible);
                    PlayerPrefs.SetInt("Mighty Bear Statue_Furniture", 0);
                }
                phm.Health = phm.MaxHealth;
            }));
            Wait(s, 5, "wait wave 1");
            if (how == Ending.Quit)
            {
                s.Add(("wave 1: kill one", () => KillAll(1)));
                Wait(s, 3);
                s.Add(("pending still active", () => Check("pending active before quitting", Stash.Pending?.State == "active")));
                return s;
            }
            if (how == Ending.Death)
            {
                s.Add(("wave 1: kill one", () => KillAll(1)));
                Wait(s, 2);
                s.Add(("player dies", () =>
                {
                    Refs.Inv.AddLootItem(Stash.PartsId, PartsPicked, false);   // 拾ったが、倒れたので持ち帰れない
                    var phm = Object.FindObjectOfType<PlayerHealthManager>();
                    phm.Damage(phm.Health + 500);
                    Check($"survival death handled (phase {Arena.Now})", Arena.Now == Arena.Phase.Leaving);
                }));
                Wait(s, 16, "wait hub");
                s.Add(("hub: returned", () => CheckReturned(expectEarned: false, extraWeapon: 0)));
                return s;
            }

            // 脱出まで
            s.Add(("wave 1 shot", () => Shot("wave1")));
            s.Add(("wave 1: kill one", () =>
            {
                Check($"at most {Waves.MaxAlive} alive ({Arena.AliveForTest().Count()})", Arena.AliveForTest().Count() <= Waves.MaxAlive);
                var e = Arena.AliveForTest().FirstOrDefault();
                if (e == null) return;
                Hit(e, e.health + 50f);
                _corpse = e; _corpseAt = e.transform.position;
                CorpseLog("killed");
            }));
            for (int i = 0; i < 3; i++) s.Add(("wait", () => CorpseLog("falling")));
            s.Add(("corpse settled", () => { if (_corpse != null) _corpseAt = _corpse.transform.position; CorpseLog("settled"); }));
            for (int i = 0; i < 2; i++) s.Add(("wait", () => CorpseLog("still")));
            s.Add(("corpse stays still", () =>
            {
                var moved = _corpse == null ? -1f : Vector3.Distance(_corpse.transform.position, _corpseAt);
                Check($"killed enemy stopped walking (moved {moved:0.00} m)", _corpse != null && moved < 1.5f);
                var pm = _corpse?.puppet;
                var hips = pm != null && pm.muscles != null && pm.muscles.Length > 0 ? Hips(pm) : null;
                float up = hips == null ? -1f : hips.position.y - _corpse.transform.position.y;
                Check($"killed enemy lies down (hips {up:0.00} m up)", hips != null && up < 0.6f);
                Shot("corpse");
            }));
            s.Add(("vending refused in a wave", () =>
            {
                var inv = Refs.Inv;
                inv.AddDollars(Arena.HealPrice + 5f);
                float before = inv.DollarsCount;
                var vm = Object.FindObjectsOfType<VendingMachine>().FirstOrDefault();
                vm?.Interact();
                Check($"no drink during a wave ($ {before:0} -> {inv.DollarsCount:0})", vm != null && Mathf.Abs(before - inv.DollarsCount) < 0.5f);
                inv.AddDollars(-(Arena.HealPrice + 5f));
            }));
            s.Add(("crate refused in a wave", () =>
            {
                int before = Survival.Held(Refs.Inv).Count;
                Arena.CrateForTest?.GetComponentInChildren<LootableChanceItems>(true)?.Interact();
                Check($"no crate during a wave (phase {Arena.Now}, held {before} -> {Survival.Held(Refs.Inv).Count})", Arena.Now == Arena.Phase.Wave && Survival.Held(Refs.Inv).Count == before);
            }));
            s.Add(("room refused in a wave", () => Arena.Teleport(Arena.LobbyForTest, -Arena.InwardForTest)));
            Wait(s, 2);
            s.Add(("pushed out", () => Check($"pushed out of the elevator room during a wave ({Refs.Fps.transform.position})", !Arena.PlayerInRoomForTest)));
            // 最後の 1 体が遠くで引っかかったとき、近くへ移されるか（待ち時間はテスト用に短く）
            s.Add(("straggler: leave one stuck far away", () =>
            {
                Arena.StaleSeconds = 3f;
                Check($"wave 1 all spawned (queue {Arena.QueueForTest})", Arena.QueueForTest == 0);
                Arena.Teleport(Arena.RoofStartForTest, -Arena.InwardForTest);
                var player = Arena.RoofStartForTest;
                var alive = Arena.AliveForTest().ToList();
                foreach (var e in alive.Skip(1)) Hit(e, e.health + 50f);
                _straggler = alive.FirstOrDefault();
                if (_straggler == null) return;
                var far = Arena.SpawnPointsForTest.OrderByDescending(p => Vector3.Distance(p, player)).First();
                foreach (var a in _straggler.GetComponentsInParent<UnityEngine.AI.NavMeshAgent>()) { a.Warp(far); if (a.isOnNavMesh) a.isStopped = true; }
                _straggler.puppet?.Teleport(far, Quaternion.identity, true);
                _relocatedBefore = Arena.RelocatedForTest;
                Core.Log($"[survtest] straggler stuck at {far} ({Vector3.Distance(far, player):0}m)");
            }));
            Wait(s, 5);
            s.Add(("straggler moved closer", () =>
            {
                float d = _straggler == null ? -1f : Vector3.Distance(_straggler.transform.position, Refs.Fps.transform.position);
                Check($"stuck last enemy moved near the player ({Arena.RelocatedForTest - _relocatedBefore} moves, now {d:0}m)", Arena.RelocatedForTest > _relocatedBefore && d >= 3f && d <= 30f);   // 移った後は歩くので、近づいたり少し離れたりする
                var agent = _straggler?.GetComponentInParent<UnityEngine.AI.NavMeshAgent>();
                Check($"it walks again (stopped={agent?.isStopped})", agent != null && !agent.isStopped);
                Arena.StaleSeconds = 20f;
            }));
            for (int i = 0; i < 14; i++) s.Add(("play to wave 2", () => Play(2)));
            s.Add(("after wave 1-2", () =>
            {
                var inv = Refs.Inv;
                Check($"in break after 2 waves (phase {Arena.Now}, cleared {Arena.Cleared})", Arena.Now == Arena.Phase.Break && Arena.Cleared == 2);
                float want = Waves.For(1).Office * Waves.Reward(Kind.Office, 1) + Waves.For(2).Office * Waves.Reward(Kind.Office, 2);
                Check($"money from kills ({inv.DollarsCount:0} = {want:0})", Mathf.Abs(inv.DollarsCount - want) < 0.5f);
                Check($"wave 2 corpses kept until the next wave ({Arena.CorpsesForTest.Count()})", Arena.CorpsesForTest.Count() >= Waves.For(2).Total);
                // 立っている = 腰が床から 0.6m 以上（Arena の判定と同じ）
                int standing = Arena.DeadForTest().Count(h =>
                {
                    var pm = h.puppet;
                    var hips = pm != null && pm.muscles != null && pm.muscles.Length > 0 ? Hips(pm) : null;
                    var b = h.transform.root.GetComponentsInChildren<Rigidbody>();
                    float up = hips == null ? -1f : hips.position.y - h.transform.position.y;
                    Core.Log($"[survtest] corpse {h.transform.parent?.name}: hips {up:0.00} m up, state {pm?.state}, mode {pm?.mode}");
                    return up > 0.6f;
                });
                Check($"no body left standing ({standing})", standing == 0);
                Check("room gate stays closed in the break", Arena.GateForTest != null && Arena.GateForTest.activeSelf);
                Check($"fire exit door closed ({Arena.DoorAngleForTest:0} deg)", Arena.DoorAngleForTest >= 0f && Arena.DoorAngleForTest < 1f);
                var fc = Refs.Fps;
                Check($"wind sounds quieter (turning max {fc.maxVolume:0.000}, running max {fc.MaxVolume:0.000})", fc.maxVolume < 0.2f && fc.MaxVolume < 0.2f);
                Check($"rooftop hum quieter ({Arena.HumVolumeForTest():0.00})", Arena.HumVolumeForTest() is >= 0f and <= 0.16f);
                _drops = Drops();
                Core.Log($"[survtest] drops after wave 2: {_drops} [{string.Join(", ", Object.FindObjectsOfType<LootableItem>().Where(l => l.gameObject.activeInHierarchy).Select(l => l.gameObject.name))}]");
                Check("no healing items in the bag", Count(inv.HealthItems) == 0);
                Shot("break");
            }));
            s.Add(("look at the helicopter", () =>
            {
                // 紹介用：ヘリの上から戦いを見ている人物の方を向く（休憩中の表示と一緒に撮る）
                var heli = Object.FindObjectsOfType<AudioSource>().FirstOrDefault(a => a.clip != null && a.clip.name == "heli");
                if (heli == null) { Core.Log("[survtest] helicopter not found"); return; }
                // ヘリの約 14m 手前（スタート地点の側）の床に立つ
                var h = heli.transform.position;
                var away = Arena.RoofStartForTest - h; away.y = 0;
                var spot = h + away.normalized * 14f;
                var from = new Vector3(spot.x, Arena.RoofStartForTest.y + 3f, spot.z);
                if (Physics.Raycast(from, Vector3.down, out var hit, 10f)) spot = hit.point; else spot.y = Arena.RoofStartForTest.y;
                var dir = h - spot; dir.y = 0;
                Core.Log($"[survtest] helicopter at {h}, standing at {spot}");
                Arena.Teleport(spot, dir.normalized);
            }));
            Wait(s, 1);
            s.Add(("watcher shot", () => Shot("watcher")));
            s.Add(("look at the door", () => Arena.Teleport(Arena.RoofStartForTest, Arena.InwardForTest)));
            Wait(s, 1);
            s.Add(("vending shot", () => Shot("vending_outside")));
            s.Add(("search a corpse", () =>
            {
                var c = Arena.CorpseSearchForTest().FirstOrDefault(x => x.enabled);
                Check($"corpse searchable '{c?.GetDescription()}'", c != null);
                _ammoBefore = AmmoTotal();
                c?.Interact();
                Check($"ammo from the corpse ({_ammoBefore} -> {AmmoTotal()})", AmmoTotal() > _ammoBefore);
                Check("corpse searched once", c != null && !c.enabled);
            }));
            s.Add(("vending", () =>
            {
                var inv = Refs.Inv;
                _keep = inv.DollarsCount;
                inv.DollarsCount = Arena.HealPrice + 5f;
                float before = inv.DollarsCount, rub = inv.RoublesCount;
                var vm = Object.FindObjectsOfType<VendingMachine>().OrderBy(v => Vector3.Distance(v.transform.position, Refs.Fps.transform.position)).FirstOrDefault();
                Check($"vending text '{vm?.GetDescription()}'", vm != null && vm.GetDescription().Contains("10,000"));
                Check($"one vending machine ({Object.FindObjectsOfType<VendingMachine>().Length})", Object.FindObjectsOfType<VendingMachine>().Length == 1);
                foreach (var v in Object.FindObjectsOfType<VendingMachine>())
                    Check($"vending '{v.name}' outside the elevator room ({v.transform.position})", Vector3.Dot(v.transform.position - Arena.DoorPosForTest, Arena.InwardForTest) < 0f);
                var phm = Object.FindObjectOfType<PlayerHealthManager>();
                // 体力を強化して最大体力が 180 を超えていても満タンになること
                _maxBefore = phm.MaxHealth;
                phm.MaxHealth = _maxBefore + 80;
                phm.Health = 30;
                Core.Log($"[survtest] health before drink {phm.Health}/{phm.MaxHealth}");
                _healsBefore = Object.FindObjectsOfType<FastHealItem>().Select(f => f.Pointer).ToHashSet();
                vm?.Interact();
                Check($"paid $10,000 ({before:0} -> {inv.DollarsCount:0}), roubles unchanged ({rub} -> {inv.RoublesCount})", Mathf.Abs(before - inv.DollarsCount - Arena.FirstHealPrice) < 0.5f && Mathf.Abs(rub - inv.RoublesCount) < 0.5f);
                Check($"next drink costs more ('{vm?.GetDescription()}')", vm != null && vm.GetDescription().Contains("11,000") && Arena.HealPrice == 11000f);
                vm?.Interact();
                Check($"second drink refused without money (= {inv.DollarsCount:0})", inv.DollarsCount > 0f && inv.DollarsCount < 100f);
                inv.DollarsCount = _keep + inv.DollarsCount;
            }));
            Wait(s, 2);
            s.Add(("drink", () =>
            {
                var drinks = Object.FindObjectsOfType<FastHealItem>().Where(f => !_healsBefore.Contains(f.Pointer)).ToList();
                Check($"a drink came out ({drinks.Count})", drinks.Count > 0);
                var cam = Camera.main;
                foreach (var d in drinks.Take(1))
                {
                    var vm = Object.FindObjectsOfType<VendingMachine>().FirstOrDefault();
                    float dist = vm == null ? -1f : Vector3.Distance(d.transform.position, vm.transform.position);
                    bool seen = cam != null && (!Physics.Linecast(cam.transform.position, d.transform.position, out var hit, ~0, QueryTriggerInteraction.Ignore) || hit.transform.IsChildOf(d.transform));
                    Check($"the drink is in front of the machine ({dist:0.0} m from it, visible {seen}, placed {Arena.DrinksPlaced})", dist >= 0f && dist < 1.8f && seen && Arena.DrinksPlaced > 0);
                }
                foreach (var d in drinks.Take(1)) { Core.Log($"[survtest] drink '{d.name}' heal={d.healAmount} '{d.GetDescription()}'"); d.Interact(); }
            }));
            Wait(s, 2);
            s.Add(("drink heals fully", () =>
            {
                var phm = Object.FindObjectOfType<PlayerHealthManager>();
                Check($"one drink heals to full, even above 180 ({phm.Health}/{phm.MaxHealth})", phm.Health >= phm.MaxHealth && phm.MaxHealth > 180);
                phm.MaxHealth = _maxBefore;
                if (phm.Health > _maxBefore) phm.Health = _maxBefore;
            }));
            s.Add(("boss wave", () =>
            {
                Check($"in break before boss wave (phase {Arena.Now})", Arena.Now == Arena.Phase.Break);
                Arena.SetNextWaveForTest(20);
                Arena.SpecForTest = new WaveSpec { Boss = 2, MaxAlive = Waves.MaxAlive, Damage = 1.2f };   // ボスだけのウェーブ
                Arena.SkipTimerForTest();
            }));
            Wait(s, 3, "wait bosses");
            s.Add(("bosses: hit", () =>
            {
                Check($"old corpses cleared at the wave start ({Arena.CorpsesForTest.Count()})", !Arena.CorpsesForTest.Any());
                Check($"drops cleared at the wave start ({_drops} -> {Drops()})", Drops() == 0);
                Check("exit gate closed in the wave", Arena.GateForTest != null && Arena.GateForTest.activeSelf);
                var bosses = Arena.AliveForTest().ToList();
                Check($"wave 20 bosses spawned ({bosses.Count}, phase {Arena.Now} wave {Arena.Wave})", bosses.Count == 2 && Arena.Wave == 20);
                foreach (var b in bosses)
                {
                    float hp = b.health;
                    Hit(b, 100f);
                    Check($"boss '{b.transform.parent?.name}' hp {hp:0} -> {b.health:0}", Mathf.Abs(hp - Waves.BossHp) < 1f && b.health < hp);
                }
                Arena.PlaceForTest();
                var me = Object.FindObjectOfType<PlayerHealthManager>(); me.Health = me.MaxHealth;
            }));
            Wait(s, 2);
            s.Add(("bosses shot", () => Shot("bosses")));
            for (int i = 0; i < 6; i++) s.Add(("play boss wave", () => Play(20)));
            s.Add(("boss money", () => { Arena.SpecForTest = null; Check($"boss kills paid ($ {Refs.Inv.DollarsCount:0} >= {2 * Waves.Reward(Kind.Boss, 20)})", Refs.Inv.DollarsCount >= 2 * Waves.Reward(Kind.Boss, 20)); }));
            s.Add(("into the room", () =>
            {
                Check($"rare heal given after wave {Arena.Cleared} ({Count(Refs.Inv.HealthItems)} kinds)", Arena.Cleared % 5 == 0 && Count(Refs.Inv.HealthItems) == 1);
                foreach (var kv in Refs.Inv.HealthItems) if (kv.Value > 0) Core.Log($"[survtest] heal '{kv.Key.GetName()}' rarity {kv.Key.GetRarity()} x{kv.Value}");
                foreach (var a in Arena.RoomForTest.GetComponentsInChildren<AudioSource>(true))
                    if (a.clip != null && a.clip.name == "heli") Check($"helicopter sound quieter ({a.volume:0.00})", a.volume <= 0.36f);
                Check($"in break (phase {Arena.Now}, cleared {Arena.Cleared})", Arena.Now == Arena.Phase.Break);
                Check("no crate before the end", Arena.CrateForTest == null);
                Arena.Teleport(Arena.LobbyForTest, -Arena.InwardForTest);
            }));
            Wait(s, 2);
            s.Add(("room closed in the break", () => Check($"pushed out of the elevator room in the break ({Refs.Fps.transform.position})", !Arena.PlayerInRoomForTest)));
            s.Add(("look at the fire exit", () =>
            {
                var dp = Arena.DoorPosForTest;
                Arena.Teleport(dp - Arena.InwardForTest * 1.5f, Arena.InwardForTest);
            }));
            Wait(s, 2);
            s.Add(("fire exit", () =>
            {
                var text = Refs.Interactions?.Description?.text;
                var cam = Camera.main;
                if (cam != null) foreach (var h in Physics.RaycastAll(cam.transform.position, cam.transform.forward, 4f).OrderBy(h => h.distance))
                    Core.Log($"[survtest] ray hit '{h.collider.name}' trig={h.collider.isTrigger} at {h.distance:0.00}m");
                Check($"fire exit prompt shown when looking at it ('{text}')", text != null && text.Contains(Arena.ExitText()));
                Shot("fire_exit");
                Arena.ExitDoorForTest?.Interact();
                Check($"survival ended at the fire exit (phase {Arena.Now})", Arena.Now == Arena.Phase.Ending);
            }));
            Wait(s, 2);
            s.Add(("door open", () =>
            {
                Check($"fire exit door opened ({Arena.DoorAngleForTest:0} deg)", Arena.DoorAngleForTest > 30f);
                Check("gate open to walk in", Arena.GateForTest != null && !Arena.GateForTest.activeSelf);
                Check($"still outside until walking in ({Refs.Fps.transform.position})", !Arena.PlayerInRoomForTest);
                Shot("door_open");
                Arena.Teleport(Arena.LobbyForTest, -Arena.InwardForTest);   // walked in
            }));
            Wait(s, 2);
            s.Add(("lobby + crate", () =>
            {
                var fps = Refs.Fps;
                Check($"moved into the elevator room ({fps.transform.position})", Arena.PlayerInRoomForTest);
                Check("exit gate closed after the end", Arena.GateForTest != null && Arena.GateForTest.activeSelf);
                var crate = Arena.CrateForTest;
                Check("crate placed", crate != null);
                if (crate != null) { var dir = crate.transform.position - fps.transform.position; dir.y = 0; fps.transform.rotation = Quaternion.LookRotation(dir); }
                Shot("crate");
            }));
            s.Add(("search crate", () =>
            {
                _crateHeld = Survival.Held(Refs.Inv).Count;
                _heldIds = Survival.Held(Refs.Inv).Select(w => w.Item.Id).ToHashSet();
                var loot = Arena.CrateForTest?.GetComponentInChildren<LootableChanceItems>(true);
                Core.Log($"[survtest] crate text '{loot?.GetDescription()}'");
                Patches.Notices.Clear();
                Link.NoticesForTest?.Invoke()?.Clear();
                // テストは 20 ウェーブで脱出するので、ドルだけ 41 ウェーブとして出す（$8,000）
                _crateDollarsBefore = Refs.Inv.DollarsCount;
                Arena.CrateDollarWavesForTest = 41;
                try { loot?.Interact(); } finally { Arena.CrateDollarWavesForTest = -1; }
            }));
            Wait(s, 2);
            s.Add(("crate weapon", () =>
            {
                var held = Survival.Held(Refs.Inv);
                _crateAdded = held.Count - _crateHeld;
                Check($"primary and secondary added (held {_crateHeld} -> {held.Count})", _crateAdded == 2);
                var fresh = held.Where(w => !_heldIds.Contains(w.Item.Id)).ToList();
                Check($"one of each slot ({string.Join(", ", fresh.Select(w => w.Item.Type))})", fresh.Any(w => w.Item.Type == WeaponItem.SlotType.Big) && fresh.Any(w => w.Item.Type == WeaponItem.SlotType.Small));
                foreach (var w in fresh)
                {
                    int mags = 0; foreach (var m in Refs.Inv.MagStored) if (m != null && m.WeaponName == w.Item.GetName()) mags++;
                    Check($"crate weapon {Survival.Label(w)}: rarity >= {Waves.MinTier(Arena.CrateWavesForTest)}, mags {mags}", (!Link.RarityActive || w.Tier >= Waves.MinTier(Arena.CrateWavesForTest)) && mags == 1);
                }
                int hp = Math.Max(0, Stash.Pending?.Handicap ?? 0);
                Check($"crate uses the handicap: waves {Arena.Cleared} + {Waves.HandicapWaves(hp)} ({hp}pt) = {Arena.CrateWavesForTest}", Arena.CrateWavesForTest == Arena.Cleared + Waves.HandicapWaves(hp));
                Check($"crate: only the weapon and dollar notices, no mag popup [{string.Join(" / ", Patches.Notices)}]",
                      Patches.Notices.Count == 3 && !Patches.Notices.Any(n => n.Contains("Mag ") || n.Contains("マガジン")));
                float gotDollars = Refs.Inv.DollarsCount - _crateDollarsBefore;
                Check($"crate dollars: 39 ${Waves.CrateDollars(39):0}, 40 ${Waves.CrateDollars(40):0}, 41 ${Waves.CrateDollars(41):0}, 50 ${Waves.CrateDollars(50):0}; got ${gotDollars:0} as wave 41",
                      Waves.CrateDollars(20) == 0f && Waves.CrateDollars(39) == 0f && Waves.CrateDollars(40) == 4000f && Waves.CrateDollars(50) == 44000f && Math.Abs(gotDollars - 8000f) < 1f);
                Check($"crate: both weapons have the same rarity ({string.Join(" / ", Arena.CrateTiers)})", !Link.RarityActive || Arena.CrateTiers.Count == 2 && Arena.CrateTiers[0] == Arena.CrateTiers[1]);
                {
                    // 武器箱のレアリティの確率表：基本のレアリティがほぼ確定＋ごくたまに上（合計 100%、1-9 はほぼコモン、10 はコモンなし、40 以上はレジェンダリーだけ）
                    //   逆転なし：ウェーブが進んでも、基本より上のどのレアリティの確率も下がらない。レジェンダリーはどのウェーブでも出る
                    string Odds(int c) => string.Join("/", Waves.CrateOdds(c).Select(o => (o * 100f).ToString("0.##")));
                    bool Near(float a, float b) => Math.Abs(a - b) < 1e-3f;
                    var o1 = Waves.CrateOdds(1); var o9 = Waves.CrateOdds(9); var o10 = Waves.CrateOdds(10); var o45 = Waves.CrateOdds(45);
                    bool sums = Enumerable.Range(0, 60).All(c => Near(Waves.CrateOdds(c).Sum(), 1f));
                    bool noFlip = Enumerable.Range(0, 59).All(c => Enumerable.Range(0, 5).All(t =>
                        t <= (int)Waves.MinTier(c + 1) || Waves.CrateOdds(c + 1)[t] >= Waves.CrateOdds(c)[t] - 1e-6f));
                    bool legendary = Enumerable.Range(0, 60).All(c => Waves.CrateOdds(c)[4] > 0.001f);
                    // 急に跳ねない：1 ウェーブでどのレアリティの確率も 20% より大きくは変わらない（9 → 10 のアンコモンなど）
                    float jump = Enumerable.Range(0, 59).Max(c => Enumerable.Range(0, 5).Max(t => Math.Abs(Waves.CrateOdds(c + 1)[t] - Waves.CrateOdds(c)[t])));
                    bool ok = sums && noFlip && legendary && jump <= 0.2001f && Near(o1[0], 0.96f) && Near(o9[0], 0.20f) && o10[0] == 0f && o10[1] > 0.95f && o45[4] == 1f;
                    Check($"crate odds (C/U/R/E/L %, no flips {noFlip}, legendary always {legendary}, max jump {jump * 100f:0.#}): 1 {Odds(1)}, 5 {Odds(5)}, 9 {Odds(9)}, 10 {Odds(10)}, 20 {Odds(20)}, 39 {Odds(39)}, 45 {Odds(45)}", ok);
                }
                {
                    // ゲームの「〇〇 added to your inventory」の通知の色が、それぞれの武器のレアリティの色
                    // レアリティmod がオフなら、ふつうの武器（色は付かない）
                    var ns = Link.NoticesForTest?.Invoke() ?? new List<(string text, Color color)>();
                    bool colorOk = !Link.RarityActive ? fresh.All(w => w.Tier == null)
                        : fresh.Count == 2 && ns.Count == 2 && fresh.All(w => w.Tier != null && ns.Any(n => n.text.Contains(w.Item.GetName()) && SameColor(n.color, Link.TierColor(w.Tier.Value))));
                    Check($"crate: notice colors match the rarity [{string.Join(" / ", ns.Select(n => n.text + " " + n.color))}]", colorOk);
                }
                var empty = fresh.All(w => { foreach (var m in Refs.Inv.MagStored) if (m != null && m.WeaponName == w.Item.GetName()) return m.AmmoCount == 0; return false; });
                Check("crate weapons come with an empty mag (like a normal pickup)", empty);
                _crateNames = fresh.Select(w => w.Item.GetName()).ToList();
                Shot("after_crate");
                Arena.Teleport(Arena.RoofStartForTest, -Arena.InwardForTest);
            }));
            Wait(s, 2);
            s.Add(("cannot leave", () => Check($"cannot leave the room after the crate ({Refs.Fps.transform.position})", Arena.PlayerInRoomForTest)));
            s.Add(("escape", () =>
            {
                Refs.Inv.AddLootItem(Stash.PartsId, PartsPicked, false);   // 敵が落とした壊れた武器部品を拾った
                _earned = Refs.Inv.DollarsCount;
                var go = GameObject.Find("LevelElevator_0")?.transform.Find("Go")?.GetComponent<ElevatorButton>();
                Check("elevator button found", go != null);
                string back = Il2CppGameLocalization.LocManager.Translate("Return to Hub");
                Check($"elevator button text '{go?.GetDescription()}' = '{back}'", go != null && go.GetDescription() == back);
                go?.Interact();
                Check($"escaping (phase {Arena.Now})", Arena.Now == Arena.Phase.Leaving);
                // すぐにもう一度押しても、次の階（最終ボス）へは行かない（拠点に戻ることは "hub: returned" で確かめる）
                go?.Interact();
                Check($"second press while leaving is ignored (phase {Arena.Now})", Arena.Now == Arena.Phase.Leaving);
            }));
            Wait(s, 16, "wait hub");
            s.Add(("hub: returned", () => CheckReturned(expectEarned: true, extraWeapon: _crateAdded)));
            s.Add(("tablet record", () =>
            {
                var t = Tablet.TabletForTest;
                try { t?.Interact(); } catch { }
                Tablet.SetPage(1);
            }));
            Wait(s, 2);
            s.Add(("tablet shot", () => Shot("tablet_record")));
            return s;
        }

        static EnemyHealth _corpse;
        static EnemyHealth _straggler;
        static string _lang;
        static int _relocatedBefore;
        // 倒した敵の位置（敵本体と、体の一番下の部位）
        // 腰の骨。倒れてしばらくするとゲームが物理演算の部品（Rigidbody）を外すので、そのときは骨の位置で見る
        static Transform Hips(Il2CppRootMotion.Dynamics.PuppetMaster pm)
        {
            var m = pm.muscles[0];
            return m.rigidbody != null ? m.rigidbody.transform : m.transform;
        }

        static void CorpseLog(string tag)
        {
            if (_corpse == null || _corpse.WasCollected) { Main.Log($"[test] corpse {tag}: gone"); return; }
            var p = _corpse.transform.position;
            var pm = _corpse.puppet;
            var hips = pm != null && pm.muscles != null && pm.muscles.Length > 0 ? Hips(pm) : null;
            var target = pm?.targetRoot;
            var agent = _corpse.GetComponentInParent<UnityEngine.AI.NavMeshAgent>();
            Main.Log($"[test] corpse {tag}: {_corpse.name} at {p} hips {(hips == null ? "-" : hips.position.ToString())} target {(target == null ? "-" : target.position.ToString())}"
                + $" parent {_corpse.transform.parent?.name} agent {(agent == null ? "-" : $"{agent.enabled} v{agent.velocity.magnitude:0.0}")} mode {pm?.mode} state {pm?.state}");
        }
        static Vector3 _corpseAt;
        static int _ammoBefore;
        static float _keep;
        static int AmmoTotal() { int n = 0; foreach (var kv in Refs.Inv.AmmoStored) n += kv.Value; return n; }

        static int _crateHeld, _crateAdded, _drops;
        static float _crateDollarsBefore;
        static List<string> _crateNames = new();
        static HashSet<int> _heldIds = new();
        static string _prefsBefore;
        static int _maxBefore;
        static string Prefs() => $"{PlayerPrefs.GetString("Difficulty", "-")}|{PlayerPrefs.GetFloat("StatueDamage", -1f):0.00}|{PlayerPrefs.GetInt("StatueEffect", -1)}"
            + $"|{PlayerPrefs.GetInt("Mighty Bear Statue_Furniture", -1)}|{PlayerPrefs.GetInt("Statue of Time_Furniture", -1)}|{PlayerPrefs.GetFloat("NoDamage", 0f):0.00}";
        static int Drops() => Object.FindObjectsOfType<LootableItem>().Count(l => l.gameObject.activeInHierarchy && l.gameObject.scene.name == Main.RunScene
            && (Arena.CrateForTest == null || !l.transform.IsChildOf(Arena.CrateForTest.transform)) && !Arena.AliveRootsForTest.Any(r => l.transform.IsChildOf(r.transform)));
        static HashSet<IntPtr> _healsBefore = new();
        static float _earned;

        static int Count<TK>(Il2CppSystem.Collections.Generic.Dictionary<TK, int> d)
        {
            int n = 0;
            foreach (var kv in d) if (kv.Value > 0) n++;
            return n;
        }

        static void CheckReturned(bool expectEarned, int extraWeapon)
        {
            var inv = Refs.Inv;
            Check($"back in the hub ({Main.Scene})", Main.Scene == Core.HubScene);
            Check("pending cleared", Stash.Pending == null && !File.Exists(Path.Combine(Main.Dir, "pending.json")));
            var now = Stash.Take(inv);
            var expect = File.ReadAllText(ExpectFile);
            // 壊れた武器部品：脱出なら拾った分が増え、倒れたら増えない
            int parts = Stash.PartsIn(inv), wantParts = _partsBefore + (expectEarned ? PartsPicked : 0);
            Check($"broken weapon parts {(expectEarned ? "kept after escaping" : "lost after going down")} ({_partsBefore} -> {parts}, want {wantParts})", parts == wantParts);
            var pe = now.Loot.FirstOrDefault(l => l.Id == Stash.PartsId);
            if (pe != null) { if (_partsBefore > 0) pe.Count = _partsBefore; else now.Loot.Remove(pe); }
            // ドル以外は開始前と同じ
            string Strip(string d) => d.Substring(d.IndexOf(" rub=", StringComparison.Ordinal));
            var crateMags = now.Mags.Where(m => _crateNames.Contains(m.Weapon)).ToList();
            foreach (var n in _crateNames) Check($"crate weapon {n} keeps a mag at base ({crateMags.Count(m => m.Weapon == n)})", crateMags.Count(m => m.Weapon == n) == 1);
            now.Mags.RemoveAll(m => _crateNames.Contains(m.Weapon));
            Check("items equal to before (except $ and crate mags)", Strip(Stash.Describe(now)) == Strip(expect));
            Check($"difficulty, vase and statues restored ({Prefs()} = {_prefsBefore})", Prefs() == _prefsBefore);
            Core.Log("[survtest] expect: " + expect);
            Core.Log("[survtest] now:    " + Stash.Describe(now));
            float want = _dollarsBefore + (expectEarned ? _earned : 0f);
            Check($"dollars {now.Dollars:0} = {want:0}", Mathf.Abs(now.Dollars - want) < 1f);
            Check($"weapons kept ({_heldBefore}+{extraWeapon} = {Survival.Held(inv).Count})", Survival.Held(inv).Count == _heldBefore + extraWeapon);
            Check($"best record saved (wave {Stash.Best?.Waves}, {Stash.Best?.Result})", Stash.Best != null);
            Check("ENDLESS_MODE cleared", !PlayerPrefs.HasKey("ENDLESS_MODE") || PlayerPrefs.GetInt("ENDLESS_MODE") != 1);
        }

        static List<(string, Action)> ResumeSteps() => new()
        {
            ("hub after restart", () =>
            {
                var inv = Refs.Inv;
                Check("pending cleared after restart", Stash.Pending == null);
                var expect = File.Exists(ExpectFile) ? File.ReadAllText(ExpectFile) : "";
                var now = Stash.Describe(Stash.Take(inv));
                Core.Log("[survtest] expect: " + expect);
                Core.Log("[survtest] now:    " + now);
                Check("items and $ equal to before the run", now == expect);
            }),
        };

        // ウェーブを進める：出ている敵を倒し、until まで休憩は飛ばす
        static void Play(int until)
        {
            var phm = Object.FindObjectOfType<PlayerHealthManager>();
            if (phm != null) phm.Health = phm.MaxHealth;   // テストで撃たれて倒れないように
            if (Arena.Now == Arena.Phase.Wave) KillAll(99);
            else if (Arena.Now == Arena.Phase.Break && Arena.Cleared < until) Arena.SkipTimerForTest();
        }

        static void KillAll(int max)
        {
            foreach (var e in Arena.AliveForTest().Take(max).ToList()) Hit(e, e.health + 50f);
        }

        // 弾が当たったときと同じ入口でダメージを与える
        static void Hit(EnemyHealth e, float dmg)
        {
            var root = e.transform.parent != null ? e.transform.parent : e.transform;
            var le = root.GetComponentInChildren<Il2CppBallistics.LivingEntity>(true);
            try { le?.OnHealthChanged(-dmg); } catch (Exception ex) { Core.Log("[survtest] hit threw " + ex.Message.Split('\n')[0]); }
        }
    }
}
