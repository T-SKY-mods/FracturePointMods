using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Il2Cpp;
using UnityEngine;

using FPModOption;

namespace FPRarity
{
    // FPRarity の自動テスト（進め方は共通の TestMode。ここでは手順を登録する）。
    // 名前を指定しないとき（""）は武器の追加・倉庫への出し入れ・売却の基本テスト
    public static class RarityTest
    {
        static readonly List<GameObject> _spawned = new();
        static string _suffixUid, _spUid;
        static Tier _suffixOrigTier;
        static string _scene => Core.Scene;

        static object Call(object target, string method, params object[] args) => TestMode.Call(target, method, args);

        public static void Register()
        {
            var S = TestMode.ExternalScenarios;
            // この Mod がオフ・無効のときは、レアリティの手順の代わりに状態の確認だけ
            if (!Main.Enabled && TestMode.Scenario != "safemode")
            {
                S[""] = OffSteps;
                S["spareoff"] = SpareOffSteps;
                return;
            }
            S["dumpweapons"] = () => WeaponDump.Steps();
            S["safemode"] = () => SafeModeSteps();
            S["stashshot"] = () => StashShot.Steps();
            S["unused"] = () => UnusedDump.Steps();
            S["masteryprobe"] = () => MasteryProbe.Steps();
            S["mastery"] = () => MasteryTest.Steps();
            S["features"] = () => FeatureTest.Steps();
            S["reload"] = () => ReloadTest.Steps();
            S["sightlab"] = () => SightLab.Steps();
            S["pistollab"] = () => SightLab.PistolSteps();
            S["sightcheck"] = () => SightCheck.Steps();
            S["sightcenter"] = () => SightCenter.Steps();
            S["sightcenterlive"] = () => SightCenter.LiveSteps();
            S["poiprobe"] = () => PoiProbe.Steps();
            S["enemyhp"] = () => EnemyProbe.Steps();
            S["endlessprobe"] = () => EndlessProbe.Steps();
            S["survivalprobe"] = () => SurvivalProbe.Steps();
            S["survivalprobe2"] = () => SurvivalProbe.BossSteps();
            S["survivalmap"] = () => SurvivalProbe.MapSteps();
            S["bossdmg"] = () => EndlessProbe.BossDamageSteps();
            S["bossmulti"] = () => EndlessProbe.BossMultiSteps();
            S["bossshot"] = () => EndlessProbe.BossShotSteps();
            S["slide"] = () => SlideProbe.Steps();
            S["slideprobe"] = () => SlideProbe.Steps();
            S["bossfire"] = () => BossFireTest.Steps();
            S["lootfloor"] = () =>
            {
                // 落ちている武器の出方（FPModOption.LootStats）を、エンドレスの階の中で調べる
                var st = new List<(string, Action)> { ("start endless run", () => Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun()) };
                for (int i = 0; i < 12; i++) st.Add(("wait load", () => { }));
                st.Add(("enter elevator + move", () => EndlessProbe.EnterAndMove()));
                for (int i = 0; i < 16; i++) st.Add(("wait / pick card", EndlessProbe.PickCard));
                st.Add(("places", () => { LootStats.Places(); Main.Log($"[loot] GetLoot calls while the floor was made: {LootPatch.Calls}, weapons swapped: {LootPatch.Swapped}"); }));
                st.Add(("loot", LootStats.Run));
                st.Add(("mod rarity in the run", (Action)(() =>
                {
                    // 今いる階（エンドレスの階）でのレアリティの抽選と、全武器にレジェンダリーが付くか
                    int floor = RarityTable.CurrentFloor();
                    var weapons = Weapons.All().Where(w => w.Id != 26).ToList();
                    Main.Log($"[loot] in the endless run now: floor {floor}");
                    foreach (int f in new[] { floor, 5, 10 })
                    {
                        // 階の数は抽選のときに読むので、一時的に変えて戻す
                        UnityEngine.PlayerPrefs.SetInt("currentFloor", f);
                        var count = new int[5]; int n = 0;
                        foreach (var wi in weapons)
                            for (int k = 0; k < 2000; k++) { var r = RarityTable.Roll(wi, Tracker.BaseTier(wi)); if (!r.Legacy) { count[(int)r.Tier]++; n++; } }
                        Main.Log($"[loot] mod rarity at floor {f}: " + string.Join(" ", Enumerable.Range(0, 5).Select(t => $"{(Tier)t}={100.0 * count[t] / Math.Max(1, n):0.0}%")) + $" ({n} rolls)");
                    }
                    UnityEngine.PlayerPrefs.SetInt("currentFloor", floor);
                    var noLeg = weapons.Where(wi => RarityTable.Roll(wi, Tracker.BaseTier(wi), Tier.Legendary) is var r && (r.Legacy || r.Tier != Tier.Legendary)).Select(wi => wi.GetName()).ToList();
                    TestMode.Check($"every weapon can be legendary ({weapons.Count - noLeg.Count}/{weapons.Count}) {string.Join(",", noLeg)}", noLeg.Count == 0);
                    var perWeapon = weapons.Select(wi => { int l = 0; for (int k = 0; k < 2000; k++) if (RarityTable.Roll(wi, Tracker.BaseTier(wi)).Tier == Tier.Legendary) l++; return $"{wi.GetName()}={l / 20.0:0.0}%"; });
                    Main.Log("[loot] legendary chance per weapon at this floor: " + string.Join(", ", perWeapon));
                })));
                return st;
            };
            S["perf"] = () => PerfProbe.Steps(boss: false);
            S["perfboss"] = () => PerfProbe.Steps(boss: true);
            S["langcheck"] = () => LangCheck.Steps();
            S["stashtip"] = () => LangCheck.StashSteps();
            S["opticscan"] = () => SightLab.OpticScanSteps();
            S["sight"] = () => SightTest.Steps();
            S["sightgallery"] = () => SightGallery.Steps();
            S["sightprobe"] = () => SightProbe.Steps();
            S["aimprobe"] = () => AimProbe.Steps();
            S["layerprobe"] = () => AimProbe.LayerSteps();
            S["aimfix"] = () => AimProbe.FixSteps();
            S["degraded"] = () => DegradedSteps();
            S["degraded"] = DegradedSteps;
            S["safemode"] = SafeModeSteps;
            S["spareon"] = SpareOnSteps;
            S[""] = BuildSteps;
        }

        // ---------- オフの間に動かした武器のロールが、オンに戻したときに戻るか（2 回の起動：-Chain で spareoff → -Resume で spareon） ----------
        static string SpareFile => Path.Combine(Main.Dir, "spare_test.txt");

        static List<(string, Action)> SpareOffSteps()
        {
            var s = new List<(string, Action)>
            {
                ("move a rolled weapon to the stash (mod off)", () =>
                {
                    Persistence.LoadFromDisk();   // 記録を読むだけ（オフなので書かない）
                    var game = Weapons.HeldIds();
                    var pick = Tracker.Held.FirstOrDefault(h => !h.Roll.Legacy && game.Contains(h.Roll.WeaponId));
                    Main.Log($"[test] spareoff: game held={string.Join(",", game)} records={Tracker.Held.Count} pick={pick?.Roll}");
                    if (pick == null) { TestMode.Check("spareoff: a held weapon with a roll", false); return; }
                    File.WriteAllText(SpareFile, $"{pick.Roll.Uid},{pick.Roll.WeaponId}");
                    Call(Refs.Bag, "MoveWeaponToStash", Weapons.ItemById(pick.Roll.WeaponId));
                }),
                ("game save", () =>
                {
                    Call(Main.FindAll<SavesManager>().FirstOrDefault(), "Save");
                    Call(Refs.Bag, "SaveProgress");
                    var id = int.Parse(File.ReadAllText(SpareFile).Split(',')[1]);
                    TestMode.Check("spareoff: weapon moved to the stash while the mod is off", Weapons.StashIds().Contains(id));
                    TestMode.Check("spareoff: mod records untouched", !File.Exists(Path.Combine(Main.Dir, "state.json.tmp")));
                }),
            };
            return s;
        }

        static List<(string, Action)> SpareOnSteps() => new()
        {
            ("roll restored", () =>
            {
                if (!File.Exists(SpareFile)) { TestMode.Check("spareon: previous run info", false); return; }
                var parts = File.ReadAllText(SpareFile).Split(',');
                File.Delete(SpareFile);
                Tracker.ReconcileStash("test");
                var inStash = Tracker.Stash.FirstOrDefault(x => x.Uid == parts[0]);
                Main.Log($"[test] spareon: uid={parts[0]} stash={inStash} held has it={Tracker.Held.Any(h => h.Roll.Uid == parts[0])} spare={Tracker.Spare.Count}");
                TestMode.Check("spareon: the weapon moved while the mod was off keeps its rarity", inStash != null && !inStash.Legacy);
            }),
        };

        // オフのとき：ゲームの手持ちはあるが、この Mod の記録（ロール）は作らない・保存しない
        static List<(string, Action)> OffSteps() => new()
        {
            ("rarity off", () =>
            {
                var state = System.IO.Path.Combine(Main.Dir, "state.json");
                Main.Log($"[test] rarity off: reason='{Main.DisabledReason}' held={Tracker.Held.Count} game held={Weapons.HeldIds().Count}");
                TestMode.Check("rarity off: no rolls tracked, mod inactive", !Main.Enabled && Tracker.Held.Count == 0 && !Link.RarityActive);
            }),
        };

        // 保管庫画面の両方の一覧について、武器の枠がすべてロールの色になっているか
        static void CheckBagColors(SaveBag bag, string when)
        {
            int bad = 0, n = 0;
            void Check(Transform list, IList<WeaponRoll> rolls, string side)
            {
                for (int i = 0; i < list.childCount && i < rolls.Count; i++)
                {
                    var p = list.GetChild(i).GetComponent<UiItemParams>();
                    if (p == null) continue;
                    n++;
                    var want = RarityTable.ColorOf(rolls[i].Tier); var c = p.Background.color;
                    bool nameOk = UiPatches.LabelIs(p.texts[0].text, rolls[i].WeaponId);
                    bool colOk = Mathf.Abs(c.r - want.r) < 0.01f && Mathf.Abs(c.g - want.g) < 0.01f && Mathf.Abs(c.b - want.b) < 0.01f;
                    if (!nameOk || !colOk)
                    {
                        bad++;
                        Main.Log($"[test]   {side}[{i}] label='{p.texts[0].text}' expected '{Tracker.NameOf(rolls[i].WeaponId)}' {rolls[i].Tier} color={c}");
                    }
                }
            }
            Check(bag.InventoryList, Tracker.Held.Select(h => h.Roll).ToList(), "inv");
            Check(bag.StashList, Tracker.Stash, "stash");
            Main.Log($"[test] CHECK bag colors {when}: {(bad == 0 && n > 0 ? "PASS" : "FAIL")} (n={n} bad={bad} invChildren={bag.InventoryList.childCount} stashChildren={bag.StashList.childCount})");
        }


        // アップデートで差し替えに失敗した状況を模擬：Mod が全体を無効化し、バニラのまま拠点まで動くこと
        // 一部機能の停止を模擬：Mod 全体は動き続け、拠点で「一部機能が停止中」と通知されること
        static int _pickedId = -1;
        static WeaponRoll _pickedGround;
        static LootableItem _pickedLoot;

        // AddWeapon の中で出たゲームの「〇〇 added to your inventory」の通知が、その武器のロールの色か
        static void CheckNotice(string what, WeaponRoll r)
        {
            var n = UiPatches.AddNotices.LastOrDefault();
            bool ok = r != null && n.text != null && n.text.Contains(Tracker.ItemById(r.WeaponId)?.GetName() ?? "?") && SameColor(n.color, RarityTable.ColorOf(r.Tier));
            Main.Log($"[test] notice '{n.text}' color={n.color} roll={r}");
            Main.Log($"[test] CHECK {what}: notice color = roll tier: {(ok ? "PASS" : "FAIL")}");
        }
        static bool SameColor(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;

        static List<(string, Action)> DegradedSteps() => new()
        {
            ("check", () =>
            {
                Main.Log($"[test] degraded=[{string.Join(",", GameVersion.Degraded)}] notified={GameVersion.DegradedNotified} disabled={GameVersion.Disabled}");
                Main.Log($"[test] CHECK degraded feature notified, mod still enabled: {(GameVersion.Degraded.Count > 0 && GameVersion.DegradedNotified == GameVersion.Degraded.Count && !GameVersion.Disabled && Tracker.Held.Count > 0 ? "PASS" : "FAIL")}");
            }),
        };

        static List<(string, Action)> SafeModeSteps() => new()
        {
            ("safe mode check", () =>
            {
                int patched = HarmonyLib.Harmony.GetAllPatchedMethods().Count(m => HarmonyLib.Harmony.GetPatchInfo(m)?.Owners.Any(o => o.Contains("FPRarity")) == true);
                Main.Log($"[test] rarity enabled={Main.Enabled} reason='{Main.DisabledReason}' fprarityPatches={patched} scene={_scene} allDisabled={GameVersion.Disabled}");
                Main.Log($"[test] CHECK safe mode (rarity off, 0 patches, reached hub, other mods unaffected): {(!Main.Enabled && patched == 0 && _scene != "MainMenu" && !GameVersion.Disabled && GameVersion.Degraded.Contains("レアリティmod") ? "PASS" : "FAIL")}");
            }),
            ("wait for notification", () => { }),
            ("wait for notification", () => { }),
        };

        // ---------- 反動の下限 0% の確認 ----------
        static (int c, int e)? _saved; static (int c, int e) _orig;
        static string _scaleMax = "";

        static List<(string, Action)> BuildSteps()
        {
            var bag = Main.FindAll<SaveBag>().FirstOrDefault();
            var shop = Main.FindAll<EquipmentShop>().FirstOrDefault();
            WeaponRoll moved = null;
            int addedId = -1;

            return new List<(string, Action)>
            {
                ("dump initial", () =>
                {
                    Tracker.DumpState("test initial");
                    // 拠点にある保管庫をすべて列挙（Refs.Bag が実際に使われるものか確認）
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<SaveBag>()))
                    {
                        var b = o.TryCast<SaveBag>();
                        Main.Log($"[test] SaveBag '{b.gameObject.name}' scene='{b.gameObject.scene.name}' active={b.gameObject.activeInHierarchy} " +
                                 $"isRefs={(Refs.Bag != null && b.Pointer == Refs.Bag.Pointer)} saveData={(b.saveData == null ? "null" : b.saveData.Pointer.ToString())} " +
                                 $"weapons={b.saveData?.Weapons?.Count} stashList={(b.StashList == null ? "null" : b.StashList.Pointer.ToString())}");
                    }
                }),

                ("spawn ground weapons for tooltip", () =>
                {
                    var fps = Main.FindAll<Il2CppDemo.Scripts.Runtime.Character.FPSController>().FirstOrDefault();
                    int n = 0;
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<LootableItem>()))
                    {
                        var li = o.TryCast<LootableItem>();
                        if (li == null || li.gameObject.scene.IsValid() || li.ItemType != LootableItem.LootType.Weapon) continue;
                        var go = UnityEngine.Object.Instantiate(li.gameObject, fps.transform.position + fps.transform.forward * (2 + n), Quaternion.identity);
                        _spawned.Add(go);
                        Main.Log($"[test] spawned ground weapon prefab '{li.gameObject.name}' ItemId={li.ItemId}");
                        if (++n >= 2) break;
                    }
                }),
                ("tooltip ui + perf", () =>
                {
                    var ui = Refs.Interactions;
                    var upd = ui?.GetType().GetMethod("UpdateTooltip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var go in _spawned)
                    {
                        var li = go.GetComponentInChildren<LootableItem>();
                        var roll = Tracker.GroundRoll(li);
                        Patches.TooltipPostTicks = 0; Patches.TooltipPostCalls = 0;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        string tip = null;
                        for (int k = 0; k < 200; k++) tip = li.GetTooltipText();
                        sw.Stop();
                        double postMs = Patches.TooltipPostTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        Main.Log($"[test] 200x GetTooltipText = {sw.Elapsed.TotalMilliseconds:0.0} ms ({sw.Elapsed.TotalMilliseconds / 200:0.000} ms/call), of which mod postfix {postMs:0.0} ms over {Patches.TooltipPostCalls} calls");
                        // 全体の時間はゲーム本体の GetTooltipText のコスト。Mod の後処理だけを判定する
                        Main.Log($"[test] CHECK mod tooltip cost < 0.1ms/call: {(postMs / 200 < 0.1 ? "PASS" : "FAIL")}");
                        if (upd == null) { Main.Log("[test] CHECK tooltip ui: FAIL (no UserInteractions)"); continue; }
                        upd.Invoke(ui, new object[] { new Interactable(li.Pointer) });
                        var bg = ui.tooltipBackground.color; var want = RarityTable.ColorOf(roll.Tier);
                        bool ok = Mathf.Abs(bg.r - want.r) < 0.01f && Mathf.Abs(bg.g - want.g) < 0.01f && Mathf.Abs(bg.b - want.b) < 0.01f;
                        Main.Log($"[test] tooltip roll={roll.Tier} rarityText='{ui.ItemRarityText?.text}' bg={bg}");
                        Main.Log($"[test] CHECK tooltip frame color = roll tier: {(ok ? "PASS" : "FAIL")}");
                    }
                    foreach (var go in _spawned) UnityEngine.Object.Destroy(go);
                }),

                ("leaderboard: client-level intercept", () =>
                {
                    var svc = Main.FindAll<Il2CppFPLeaderboard.Unity.LeaderboardService>().FirstOrDefault();
                    var client = svc?._client;
                    if (client == null) { Main.Log("[test] CHECK leaderboard: no client FAIL"); return; }
                    var token = svc._lifetime.Token;
                    var t1 = client.StartRunAsync(token);
                    var session = t1.Result;
                    var t2 = client.CompleteFloorAsync(session, null, token);
                    var t3 = client.FinishRunAsync(session, 15, token);
                    bool ok = t1.IsCompleted && session != null && t2.IsCompleted && t3.IsCompleted
                              && t3.Result == Il2CppFPLeaderboard.Unity.FinishRunResult.NotEligibleForLeaderboard;
                    Main.Log($"[test] client start={t1.Status} floor={t2.Status}:{t2.Result} finish={t3.Status}:{t3.Result}");
                    Main.Log($"[test] CHECK leaderboard client intercept: {(ok ? "PASS" : "FAIL")}");
                }),
                ("leaderboard: service-level run", () =>
                {
                    var svc = Main.FindAll<Il2CppFPLeaderboard.Unity.LeaderboardService>().FirstOrDefault();
                    var st = svc.StartRunAsync();
                    Main.Log($"[test] svc.StartRunAsync status={st.Status} authenticated={svc.IsAuthenticated} active={svc.HasActiveRun}");
                }),
                ("leaderboard: service-level finish", () =>
                {
                    var svc = Main.FindAll<Il2CppFPLeaderboard.Unity.LeaderboardService>().FirstOrDefault();
                    var ft = svc.FinishRunAsync(15);
                    Main.Log($"[test] svc.FinishRunAsync status={ft.Status} result={(ft.IsCompleted && !ft.IsFaulted ? ft.Result.ToString() : ft.Exception?.ToString())} active={svc.HasActiveRun}");
                }),

                ("make room for a new weapon", () =>
                {
                    // 新しい武器の追加と地面からの拾いの 2 本分、大型・小型とも 1 枠ずつ空ける（セーブは autotest.ps1 が戻す）
                    var items = Tracker.Held.Select(h => Tracker.ItemByPrefab(h.Prefab)).Where(w => w != null).ToList();
                    foreach (var type in new[] { WeaponItem.SlotType.Big, WeaponItem.SlotType.Small })
                    {
                        var ofType = items.Where(w => w.Type == type).ToList();
                        if (ofType.Count < 2) continue;
                        Main.Log($"[test] {type} slots full; moving {ofType.Last().GetName()} to the stash");
                        Call(Refs.Bag, "MoveWeaponToStash", ofType.Last());
                    }
                }),
                ("add new weapon (simulated pickup/store)", () =>
                {
                    // 手持ちにない種類の小火器を1本追加
                    var have = Tracker.Held.Select(h => h.Roll.WeaponId).ToHashSet();
                    var wi = Tracker.Inv.WeaponData.Weapons;
                    int bigs = Tracker.Held.Count(h => Tracker.ItemByPrefab(h.Prefab)?.Type == WeaponItem.SlotType.Big);
                    int smalls = Tracker.Held.Count - bigs;
                    var freeType = smalls < 2 ? WeaponItem.SlotType.Small : WeaponItem.SlotType.Big;   // 枠は大型 2・小型 2
                    Main.Log($"[test] held big={bigs}/{Tracker.Inv.MaxBigWeapons} small={smalls}/{Tracker.Inv.MaxSmallWeapons} -> adding {freeType}");
                    WeaponItem pick = null;
                    for (int i = 0; i < wi.Count; i++)
                        if (wi[i].Type == freeType && !have.Contains(wi[i].Id) && wi[i].Weapon != null && wi[i].Id != 26) { pick = wi[i]; break; }
                    addedId = pick?.Id ?? -1;
                    UiPatches.AddNotices.Clear();
                    Main.Log($"[test] AddWeapon {pick?.GetName()} -> {Call(Tracker.Inv, "AddWeapon", pick)}");
                }),
                ("dump after add", () =>
                {
                    Tracker.DumpState("test after add");
                    var r = Tracker.Held.FirstOrDefault(x => x.Roll.WeaponId == addedId)?.Roll;
                    CheckNotice("new weapon", r);
                }),
                ("pick up a ground weapon", () =>
                {
                    // 手持ちにない種類で、空きのある枠の武器を地面に出して拾う（通常のラン・エンドレスでの入手と同じ処理）
                    var have = Tracker.Held.Select(h => h.Roll.WeaponId).ToHashSet();
                    int bigs = Tracker.Held.Count(h => Tracker.ItemByPrefab(h.Prefab)?.Type == WeaponItem.SlotType.Big);
                    int smalls = Tracker.Held.Count - bigs;
                    var fps = Main.FindAll<Il2CppDemo.Scripts.Runtime.Character.FPSController>().FirstOrDefault();
                    _pickedId = -1;
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<LootableItem>()))
                    {
                        var li = o.TryCast<LootableItem>();
                        if (li == null || li.gameObject.scene.IsValid() || li.ItemType != LootableItem.LootType.Weapon) continue;
                        var wi = Tracker.ItemById(li.ItemId);
                        if (wi == null || have.Contains(wi.Id) || wi.Id == 26) continue;
                        // 枠は大型 2・小型 2（Inventory.MaxBigWeapons などはここでは 0 が読めるので使わない）
                        if (wi.Type == WeaponItem.SlotType.Big ? bigs >= 2 : smalls >= 2) continue;
                        var go = UnityEngine.Object.Instantiate(li.gameObject, fps.transform.position + fps.transform.forward * 1.5f, Quaternion.identity);
                        var gli = go.GetComponentInChildren<LootableItem>();
                        _pickedLoot = gli;
                        _pickedGround = Tracker.GroundRoll(gli);
                        _pickedId = wi.Id;
                        Main.Log($"[test] spawned {wi.GetName()} to pick up, ground roll {_pickedGround}");
                        break;
                    }
                    if (_pickedId < 0) Main.Log("[test] no free slot for a ground pickup (skip)");
                }),
                ("pick it up", () =>
                {
                    // 出した次のフレーム以降（武器の初期化の後）に、プレイヤーが拾うときと同じ呼び出しで拾う
                    if (_pickedId < 0 || _pickedLoot == null) return;
                    UiPatches.AddNotices.Clear();
                    _pickedLoot.Interact(Refs.Interactions);
                }),
                ("after ground pickup", () =>
                {
                    if (_pickedId < 0) return;
                    var r = Tracker.Held.FirstOrDefault(x => x.Roll.WeaponId == _pickedId)?.Roll;
                    Main.Log($"[test] CHECK ground pickup keeps the ground roll ({_pickedGround?.Tier} -> {r?.Tier}): {(r != null && ReferenceEquals(r, _pickedGround) ? "PASS" : "FAIL")}");
                    CheckNotice("ground pickup", r);
                }),
                ("inventory ui colors + details", () =>
                {
                    var inv = Main.FindAll<InventoryUI>().FirstOrDefault();
                    if (inv == null) { Main.Log("[test] no InventoryUI"); return; }
                    inv.UpdateInventory();
                    var added = Tracker.Held.First(x => x.Roll.WeaponId == addedId);
                    var img = Tracker.ItemById(addedId).Type == WeaponItem.SlotType.Big ? inv.BigWeaponBackground : inv.SmallWeaponBackground;
                    var want = RarityTable.ColorOf(added.Roll.Tier);
                    bool ok = Mathf.Abs(img.color.r - want.r) < 0.01f && Mathf.Abs(img.color.g - want.g) < 0.01f && Mathf.Abs(img.color.b - want.b) < 0.01f;
                    Main.Log($"[test] inv slot color={img.color} want={want} roll={added.Roll}");
                    Main.Log($"[test] CHECK inventory slot color = roll tier: {(ok ? "PASS" : "FAIL")}");
                    // 4スロットすべて：表示中の画像の武器のレアリティ色になっているか
                    int bad = 0, n = 0;
                    foreach (var (nm, icon, bg) in new[] {
                        ("Main1", inv.MainWeaponOne, inv.BigWeaponBackground), ("Main2", inv.MainWeaponTwo, inv.BigWeaponTwoBackground),
                        ("Small1", inv.SmallWeaponOne, inv.SmallWeaponBackground), ("Small2", inv.SmallWeaponTwo, inv.SmallWeaponTwoBackground) })
                    {
                        if (icon == null || icon.sprite == null || !icon.gameObject.activeInHierarchy) continue;
                        var matches = Tracker.Held.Where(h => Tracker.ItemByPrefab(h.Prefab)?.Image?.Pointer == icon.sprite.Pointer).ToList();
                        if (matches.Count != 1) { Main.Log($"[test]   slot {nm}: {matches.Count} held weapons share this image (skip)"); continue; }
                        n++;
                        var w = RarityTable.ColorOf(matches[0].Roll.Tier); var c = bg.color;
                        bool good = Mathf.Abs(c.r - w.r) < 0.01f && Mathf.Abs(c.g - w.g) < 0.01f && Mathf.Abs(c.b - w.b) < 0.01f;
                        if (!good) bad++;
                        Main.Log($"[test]   slot {nm}: {Tracker.NameOf(matches[0].Roll.WeaponId)} {matches[0].Roll.Tier} color={c} {(good ? "ok" : "WRONG")}");
                    }
                    Main.Log($"[test] CHECK all equipment slots: {(n > 0 && bad == 0 ? "PASS" : "FAIL")} (n={n} bad={bad})");
                    string P(Component c) { var t = c?.transform; var a = new List<string>(); for (int k = 0; t != null && k < 4; k++, t = t.parent) a.Insert(0, t.name); return string.Join("/", a); }
                    foreach (var (nm, icon, bg) in new[] {
                        ("Main1", inv.MainWeaponOne, inv.BigWeaponBackground), ("Main2", inv.MainWeaponTwo, inv.BigWeaponTwoBackground),
                        ("Small1", inv.SmallWeaponOne, inv.SmallWeaponBackground), ("Small2", inv.SmallWeaponTwo, inv.SmallWeaponTwoBackground) })
                        Main.Log($"[test] slot {nm}: icon={P(icon)} sprite={icon?.sprite?.name} bg={P(bg)} color={bg?.color}");
                    for (int k = 0; k < Tracker.Held.Count; k++)
                    { var wi2 = Tracker.ItemByPrefab(Tracker.Held[k].Prefab); Main.Log($"[test] held[{k}] {wi2?.GetName()} type={wi2?.Type} image={wi2?.Image?.name} tier={Tracker.Held[k].Roll.Tier}"); }
                    if (inv.WeaponDetails != null && inv.WeaponDetails.Count > 2)
                    {
                        var t0 = inv.WeaponDetails[2].transform;
                        for (int up = 0; up < 4 && t0 != null; up++, t0 = t0.parent)
                            foreach (var g in t0.GetComponents<UnityEngine.UI.Graphic>())
                                Main.Log($"[test] details up{up} '{P(g)}' {g.GetIl2CppType().Name} color={g.color} active={g.gameObject.activeInHierarchy}");
                        var dp = inv.WeaponDetails[2].transform.parent;
                        for (int k = 0; dp != null && k < dp.childCount; k++)
                            foreach (var g in dp.GetChild(k).GetComponents<UnityEngine.UI.Graphic>())
                                Main.Log($"[test] details child '{P(g)}' {g.GetIl2CppType().Name} color={g.color}");
                    }
                    // ゲームと同じ流れ（UpdateInventory）で作られた詳細欄を全部書き出す
                    var det = inv.WeaponDetails;
                    for (int i = 0; det != null && i < det.Count; i++)
                        Main.Log($"[test] WeaponDetails[{i}] '{det[i]?.gameObject.name}' parent='{det[i]?.transform.parent?.name}':\n{det[i]?.text}");
                }),
                // ゲームと同じく保管庫ページを開く（OpenPage → UpdateUI）
                ("bag lists build #1", () => Call(bag, "OpenPage")),
                // ゲームで保管庫を開き直すのと同じく、古い項目が残っている状態で作り直す
                ("wait build #1", () => { }),
                ("wait build #1", () => { }),
                ("wait build #1", () => { }),
                ("bag lists rebuild #2", () => { Call(bag, "ClosePage"); Call(bag, "OpenPage"); }),
                ("wait for rebuild", () => { }),
                ("wait for rebuild", () => { }),
                ("wait for rebuild", () => { }),
                ("wait for repaint", () => { }),
                ("bag inventory list colors", () =>
                {
                    var list = bag.InventoryList;
                    int bad = 0, n = 0;
                    for (int i = 0; i < list.childCount; i++)
                    {
                        var p = list.GetChild(i).GetComponent<UiItemParams>();
                        var label = p?.texts != null && p.texts.Length > 0 ? p.texts[0]?.text : null;
                        var h = i < Tracker.Held.Count ? Tracker.Held[i] : null;
                        bool isWeapon = h != null && UiPatches.LabelIs(label, h.Roll.WeaponId);
                        Main.Log($"[test] invlist[{i}] active={list.GetChild(i).gameObject.activeSelf} label='{label}' color={p?.Background?.color} held={(h == null ? "-" : Tracker.NameOf(h.Roll.WeaponId) + ":" + h.Roll.Tier)} match={isWeapon}");
                        if (!isWeapon) continue;
                        n++;
                        var want = RarityTable.ColorOf(h.Roll.Tier); var c = p.Background.color;
                        if (!(Mathf.Abs(c.r - want.r) < 0.01f && Mathf.Abs(c.g - want.g) < 0.01f && Mathf.Abs(c.b - want.b) < 0.01f)) bad++;
                    }
                    Main.Log($"[test] CHECK bag inventory weapon colors: {(n > 0 && bad == 0 ? "PASS" : "FAIL")} (n={n} bad={bad})");
                }),
                ("stash ui colors", () =>
                {
                    int good = 0, bad = 0, n = 0;
                    var list = bag.StashList;
                    for (int i = 0; i < list.childCount && i < Tracker.Stash.Count; i++)
                    {
                        var p = list.GetChild(i).GetComponent<UiItemParams>();
                        if (p == null) continue;
                        n++;
                        var want = RarityTable.ColorOf(Tracker.Stash[i].Tier);
                        var c = p.Background.color;
                        if (Mathf.Abs(c.r - want.r) < 0.01f && Mathf.Abs(c.g - want.g) < 0.01f && Mathf.Abs(c.b - want.b) < 0.01f) good++; else bad++;
                        if (i < 3) Main.Log($"[test] stash[{i}] '{p.texts[0].text}' color={c} roll={Tracker.Stash[i]}");
                    }
                    Main.Log($"[test] stash items checked={n} good={good} bad={bad} children={list.childCount}");
                    Main.Log($"[test] CHECK stash item colors: {(n > 0 && bad == 0 ? "PASS" : "FAIL")}");
                }),
                ("recoil clone check", () =>
                {
                    var fps = Main.FindAll<Il2CppDemo.Scripts.Runtime.Character.FPSController>().FirstOrDefault(f => f.InstantiatedWeapons != null);
                    var dw = fps?.GetActiveItem()?.TryCast<Il2CppDemo.Scripts.Runtime.Item.Weapon>();
                    var roll = Tracker.ActiveRoll(out var wi);
                    var pdw = wi?.Weapon;
                    var rs = dw?.recoilPatternSettings;
                    var used = dw?._recoilPattern?.recoilSettings;
                    Main.Log($"[test] active={dw?.gameObject.name} roll={roll}");
                    Main.Log($"[test] recoil vanilla h={pdw?.recoilPatternSettings.horizontalRecoil} v={pdw?.recoilPatternSettings.verticalRecoil} kickUp={pdw?.recoilData.kickUp}");
                    Main.Log($"[test] recoil instance '{rs?.name}' h={rs?.horizontalRecoil} v={rs?.verticalRecoil} kickUp={dw?.recoilData.kickUp} usedByPlayer={(used != null && rs != null && used.Pointer == rs.Pointer)}");
                    bool ok = roll != null && !roll.Legacy && rs != null && used != null && used.Pointer == rs.Pointer
                              && Mathf.Abs(rs.verticalRecoil.y - pdw.recoilPatternSettings.verticalRecoil.y * RarityTable.RecoilScale(roll)) < 0.001f;
                    Main.Log($"[test] CHECK recoil clone applied & used: {(ok ? "PASS" : "FAIL")}");
                }),
                // 反動の下限 0%：コントロールを +300 にすると、反動の倍率が 0 になり、
                // 手に持っている武器が実際に使う反動のデータ（視点の持ち上がり・撃った瞬間の跳ね上がり）が 0 になる。
                // エルゴ +50 では構える速さ（AdsSpeed）が上がる。ロールは最後に元に戻す
                ("no recoil: control +300, ergo +50", () =>
                {
                    var roll = Tracker.ActiveRoll(out _);
                    if (roll == null) { Main.Log("[test] no recoil: no active weapon"); return; }
                    _saved = _orig = (roll.ControlDelta, roll.ErgoDelta);
                    roll.ControlDelta += 300; roll.ErgoDelta += 50;
                    Tracker.ApplyAll(); Tracker.ReinitActiveRecoil();   // 熟練度を変えたときと同じ
                    _scaleMax = $"{RarityTable.RecoilScale(roll):0.000}";
                }),
                ("wait", () => { }),
                ("no recoil: check and restore", () =>
                {
                    var fps = Main.FindAll<Il2CppDemo.Scripts.Runtime.Character.FPSController>().FirstOrDefault(f => f.InstantiatedWeapons != null);
                    var dw = fps?.GetActiveItem()?.TryCast<Il2CppDemo.Scripts.Runtime.Item.Weapon>();
                    var rs = dw?.recoilPatternSettings; var used = dw?._recoilPattern?.recoilSettings; var ra = dw?.recoilData;
                    bool zero = rs != null && used != null && used.Pointer == rs.Pointer && ra != null
                                && rs.verticalRecoil == Vector2.zero && rs.horizontalRecoil == Vector2.zero
                                && ra.kickUp == Vector2.zero && ra.kickRight == Vector2.zero && ra.kickback == Vector2.zero
                                && ra.pitch == Vector2.zero && ra.yaw == Vector4.zero && ra.roll == Vector4.zero && ra.noiseScalar == 0f && ra.pushAmount == 0f;
                    Main.Log($"[test] no recoil: scales={_scaleMax} v={rs?.verticalRecoil} h={rs?.horizontalRecoil} kickUp={ra?.kickUp} pitch={ra?.pitch} usedByPlayer={(used != null && rs != null && used.Pointer == rs.Pointer)}");
                    Main.Log($"[test] CHECK recoil lower limit is 0% (scale at +300 control {_scaleMax}): {(_scaleMax == "0.000" ? "PASS" : "FAIL")}");
                    Main.Log($"[test] CHECK no recoil in the weapon in hand at max control: {(zero ? "PASS" : "FAIL")}");
                    var ads = AdsSpeed.SettingsOf(dw);
                    float ergo = dw != null ? AdsSpeed.ErgoOf(dw) : 0f, mult = AdsSpeed.Mult(ergo);
                    // +50 の分だけ速くなっている（元のエルゴはロールでまちまち）
                    bool fast = ads != null && mult - AdsSpeed.Mult(ergo - 50f) > 0.45f && Mathf.Abs(ads.aimingSpeed - AdsSpeed.Original(ads) * mult) < 0.001f;
                    Main.Log($"[test] CHECK ergo +50 makes aiming faster (x{mult:0.00}, aimingSpeed {ads?.aimingSpeed:0.###} from {(ads != null ? AdsSpeed.Original(ads) : 0f):0.###}): {(fast ? "PASS" : "FAIL")}");
                    var roll = Tracker.ActiveRoll(out _);
                    if (roll != null && _saved.HasValue) { roll.ControlDelta = _saved.Value.c; roll.ErgoDelta = _saved.Value.e; _saved = null; Tracker.ApplyAll(); Tracker.ReinitActiveRecoil(); }
                    Main.Log($"[test] CHECK roll restored after the no-recoil test: {(roll != null && roll.ControlDelta == _orig.c && roll.ErgoDelta == _orig.e ? "PASS" : "FAIL")}");
                }),
                ("roll balance: DPS per tier", () =>
                {
                    // 全武器を各レアリティで抽選し、実際に出せる DPS（セミオートは連打の上限で計算）が
                    // 目標（レアリティの基準値 × マガジンの補正 × コントロールの補正）の ±10% 以内かを見る
                    var weapons = Tracker.Inv.WeaponData.Weapons;
                    int bad = 0; float rpmMin = 9, rpmMax = 0, maxShot = 0;
                    var effRare = new Dictionary<int, double>(); var ctlRare = new Dictionary<int, double>();
                    for (int t = 0; t < 5; t++)
                    {
                        var line = new System.Text.StringBuilder($"[balance] {(Tier)t,-9}");
                        for (int i = 0; i < weapons.Count; i++)
                        {
                            var wi = weapons[i];
                            if (wi.Id == 26) continue;
                            var dw = wi.Weapon;
                            var bw = dw.GetComponentInChildren<Il2CppBallistics.Weapon>(true);
                            double sum = 0, ratio = 0, ctlSum = 0, dmgSum = 0; int n = 200;
                            for (int k = 0; k < n; k++)
                            {
                                var r = RarityTable.Roll(wi, Tracker.BaseTier(wi), (Tier)t);
                                rpmMin = Math.Min(rpmMin, r.FireRateMult); rpmMax = Math.Max(rpmMax, r.FireRateMult);
                                double eff = bw.MuzzleDamage * r.DamageMult * RarityTable.EffectiveRpm(dw, dw.fireRate * r.FireRateMult) / 60.0;
                                int ctl = Math.Max(0, StatScale.OrigControl(wi) + (int)MathF.Round(StatScale.ToOld(r.ControlDelta)));   // 抽選は以前の単位
                                int ctlForDps = Math.Max(0, ctl - RarityTable.MagControlBonus(wi));   // マガジンの上乗せ分は DPS の計算に含めない
                                sum += eff; ctlSum += ctl; dmgSum += bw.MuzzleDamage * r.DamageMult;
                                maxShot = Math.Max(maxShot, bw.MuzzleDamage * r.DamageMult);
                                // 1発のダメージの上限に当たった個体は、上限で出せる DPS を目標にする
                                double cap = RarityTable.MaxShotDamage * RarityTable.EffectiveRpm(dw, dw.fireRate * r.FireRateMult) / 60.0;
                                ratio += eff / Math.Min(RarityTable.TargetDps(wi, (Tier)t, ctlForDps), cap);
                            }
                            var avg = sum / n; ratio /= n;
                            line.Append($" {wi.GetName()}=dmg{dmgSum / n:0}/eff{avg:0}/ctl{ctlSum / n:0}/mag{RarityTable.MagSize(wi)}");
                            if (Math.Abs(ratio - 1) > 0.10) { bad++; line.Append("(!)"); }
                            if (t == 2) { effRare[wi.Id] = avg; ctlRare[wi.Id] = ctlSum / n; }
                        }
                        Main.Log(line.ToString());
                    }
                    Main.Log($"[test] CHECK effective DPS matches target (±10%): {(bad == 0 ? "PASS" : $"FAIL ({bad})")}");
                    Main.Log($"[test] CHECK max damage per shot <= {RarityTable.MaxShotDamage} +12% (max {maxShot:0.0}): {(maxShot <= RarityTable.MaxShotDamage * 1.12f + 1f ? "PASS" : "FAIL")}");
                    Main.Log($"[test] CHECK rpm change within ±5% and ±8% roll (x{rpmMin:0.00}~x{rpmMax:0.00}): {(rpmMin >= 0.95f * 0.92f - 0.006f && rpmMax <= 1.05f * 1.08f + 0.006f ? "PASS" : "FAIL")}");
                    // 方針の確認（レア同士）：反動の大きい AKM は扱いやすい R-15 より DPS が高く、コントロールは低いまま
                    //                       セミオートの 1911 は実際の DPS でフルオートの G-18C と同程度（以前は約4割だった）
                    Main.Log($"[test] CHECK low control => higher DPS (AKM {effRare[12]:0}/ctl{ctlRare[12]:0} vs R-15 {effRare[15]:0}/ctl{ctlRare[15]:0}): {(effRare[12] > effRare[15] * 1.05 && ctlRare[12] < ctlRare[15] - 15 ? "PASS" : "FAIL")}");
                    Main.Log($"[test] CHECK semi-auto not penalized (1911 {effRare[25]:0} vs G-18C {effRare[13]:0}): {(effRare[25] > effRare[13] * 0.85 ? "PASS" : "FAIL")}");
                }),
                ("game save -> mod commit", () =>
                {
                    var sm = Main.FindAll<SavesManager>().FirstOrDefault();
                    Call(sm, "Save");
                    Call(bag, "SaveProgress");
                    var json = File.ReadAllText(Path.Combine(Main.Dir, "state.json"));
                    var added = Tracker.Held.FirstOrDefault(x => x.Roll.WeaponId == addedId)?.Roll;
                    bool ok = added != null && json.Contains(added.Uid);
                    Main.Log($"[test] CHECK commit on game save contains new roll {added?.Uid}: {(ok ? "PASS" : "FAIL")}");
                }),

                ("move added weapon to stash", () =>
                {
                    var h = Tracker.Held.FirstOrDefault(x => x.Roll.WeaponId == addedId);
                    moved = h?.Roll;
                    Main.Log($"[test] moving to stash: {moved}");
                    // 実際の操作と同じく、保管庫画面のインベントリ一覧の項目を左クリックする
                    var list = bag.InventoryList;
                    UnityEngine.EventSystems.EventTrigger trig = null;
                    for (int i = 0; i < list.childCount && trig == null; i++)
                    {
                        var p = list.GetChild(i).GetComponent<UiItemParams>();
                        if (p != null && UiPatches.LabelIs(p.texts[0].text, addedId))
                            trig = p.GetComponentInChildren<UnityEngine.EventSystems.EventTrigger>(true);
                    }
                    if (trig == null) { Main.Log("[test] inventory list item not found; falling back to direct call"); Call(bag, "MoveWeaponToStash", Tracker.ItemById(addedId)); return; }
                    var ev = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { button = UnityEngine.EventSystems.PointerEventData.InputButton.Left };
                    Main.Log($"[test] clicking inventory item (triggers={trig.triggers.Count})");
                    for (int i = 0; i < trig.triggers.Count; i++) trig.triggers[i].callback.Invoke(ev);
                }),
                ("dump after stash", () =>
                {
                    Tracker.DumpState("test after stash");
                    var inStash = Tracker.Stash.Any(r => r.Uid == moved?.Uid);
                    Main.Log($"[test] CHECK roll {moved?.Uid} in stash: {(inStash ? "PASS" : "FAIL")}");
                }),
                ("bag colors +1.5s", () => CheckBagColors(bag, "+1.5s")),
                ("bag colors +3s", () => CheckBagColors(bag, "+3s")),
                ("suffixed label: force tier & rebuild", () =>
                {
                    // 表示名に「(last)」などが付く項目でも、Mod のレアリティ色が付くことを確かめる
                    var list = bag.InventoryList;
                    for (int i = 0; i < list.childCount && i < Tracker.Held.Count; i++)
                    {
                        var p = list.GetChild(i).GetComponent<UiItemParams>();
                        var label = p?.texts[0].text ?? "";
                        if (!label.Contains("(")) continue;
                        var r = Tracker.Held[i].Roll;
                        _suffixUid = r.Uid;
                        _suffixOrigTier = r.Tier;
                        r.Tier = r.BaseTier == Tier.Legendary ? Tier.Common : Tier.Legendary; // 元と必ず違う色にする
                        Main.Log($"[test] suffixed item '{label}' -> forcing {r.Tier} (base {r.BaseTier}) and rebuilding");
                        Call(bag, "UpdateUI");
                        return;
                    }
                    Main.Log("[test] no suffixed label in inventory list");
                }),
                ("wait", () => { }),
                ("suffixed label: check", () =>
                {
                    if (_suffixUid == null) { Main.Log("[test] CHECK suffixed label color: SKIP"); return; }
                    int i = Tracker.Held.FindIndex(h => h.Roll.Uid == _suffixUid);
                    var p = bag.InventoryList.GetChild(i).GetComponent<UiItemParams>();
                    var r = Tracker.Held[i].Roll;
                    var want = RarityTable.ColorOf(r.Tier); var c = p.Background.color;
                    bool ok = Mathf.Abs(c.r - want.r) < 0.01f && Mathf.Abs(c.g - want.g) < 0.01f && Mathf.Abs(c.b - want.b) < 0.01f;
                    Main.Log($"[test] suffixed '{p.texts[0].text}' color={c} want={want}");
                    Main.Log($"[test] CHECK suffixed label color: {(ok ? "PASS" : "FAIL")}");
                    r.Tier = _suffixOrigTier;
                }),
                ("stash ui color after move", () =>
                {
                    // 倉庫へ移した直後の一覧で、移した武器の枠がそのロールの色になっているか
                    var list = bag.StashList;
                    int idx = Tracker.Stash.FindIndex(r => r.Uid == moved?.Uid);
                    var p = idx >= 0 && idx < list.childCount ? list.GetChild(idx).GetComponent<UiItemParams>() : null;
                    var want = RarityTable.ColorOf(moved.Tier);
                    var c = p?.Background?.color ?? Color.clear;
                    bool ok = p != null && UiPatches.LabelIs(p.texts[0].text, moved.WeaponId)
                              && Mathf.Abs(c.r - want.r) < 0.01f && Mathf.Abs(c.g - want.g) < 0.01f && Mathf.Abs(c.b - want.b) < 0.01f;
                    Main.Log($"[test] moved #{idx}/{Tracker.Stash.Count} children={list.childCount} label='{p?.texts[0].text}' color={c} want={want} ({moved.Tier}, base {moved.BaseTier})");
                    Main.Log($"[test] CHECK stash ui color after move: {(ok ? "PASS" : "FAIL")}");
                }),

                ("move it back to inventory", () =>
                {
                    var entries = bag.saveData.Weapons;
                    var idx = Tracker.Stash.FindIndex(r => r.Uid == moved?.Uid);
                    Main.Log($"[test] moving stash #{idx} back (click)");
                    // 実際の操作と同じく、保管庫一覧の項目を左クリックする
                    var trig = idx >= 0 && idx < bag.StashList.childCount
                        ? bag.StashList.GetChild(idx).GetComponentInChildren<UnityEngine.EventSystems.EventTrigger>(true) : null;
                    if (trig == null) { Main.Log("[test] stash item not found; direct call"); if (idx >= 0) Call(bag, "MoveWeaponToInventory", entries[idx]); return; }
                    var ev = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { button = UnityEngine.EventSystems.PointerEventData.InputButton.Left };
                    for (int i = 0; i < trig.triggers.Count; i++) trig.triggers[i].callback.Invoke(ev);
                }),
                ("bag colors after back +1.5s", () => CheckBagColors(bag, "after back +1.5s")),
                ("bag colors after back +3s", () => CheckBagColors(bag, "after back +3s")),
                ("dump after back", () =>
                {
                    Tracker.DumpState("test after back");
                    var back = Tracker.Held.Any(h => h.Roll.Uid == moved?.Uid);
                    Main.Log($"[test] CHECK roll {moved?.Uid} back in held: {(back ? "PASS" : "FAIL")}");
                }),

                ("move equipped SP-320 to stash (click)", () =>
                {
                    // ユーザーの操作の再現：装備中の SP-320（Mod のレアリティ）を倉庫へ。倉庫には導入前の SP-320 もある
                    _spUid = Tracker.Held.FirstOrDefault(h => h.Roll.WeaponId == 9)?.Roll.Uid;
                    var list = bag.InventoryList;
                    for (int i = 0; i < list.childCount; i++)
                    {
                        var p = list.GetChild(i).GetComponent<UiItemParams>();
                        if (p == null || !UiPatches.LabelIs(p.texts[0].text, 9)) continue;
                        var trig = p.GetComponentInChildren<UnityEngine.EventSystems.EventTrigger>(true);
                        var ev = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { button = UnityEngine.EventSystems.PointerEventData.InputButton.Left };
                        Main.Log($"[test] clicking '{p.texts[0].text}' uid={_spUid}");
                        for (int k = 0; k < trig.triggers.Count; k++) trig.triggers[k].callback.Invoke(ev);
                        return;
                    }
                    Main.Log("[test] SP-320 not in inventory list");
                }),
                ("sp320 colors +1.5s", () => CheckBagColors(bag, "sp320 +1.5s")),
                ("sp320 colors +3s", () =>
                {
                    CheckBagColors(bag, "sp320 +3s");
                    var idx = Tracker.Stash.FindIndex(r => r.Uid == _spUid);
                    for (int i = 0; i < Tracker.Stash.Count; i++)
                        if (Tracker.Stash[i].WeaponId == 9)
                        {
                            var p = bag.StashList.GetChild(i).GetComponent<UiItemParams>();
                            Main.Log($"[test]   stash[{i}] SP-320 roll={Tracker.Stash[i].Tier}{(Tracker.Stash[i].Legacy ? "(legacy)" : "")} uid={Tracker.Stash[i].Uid} ui='{p.texts[0].text}' color={p.Background.color}{(i == idx ? "  <- moved" : "")}");
                        }
                }),
                ("sell price check", () =>
                {
                    if (shop == null) { Main.Log("[test] no EquipmentShop"); return; }
                    var wi = Tracker.ItemById(addedId);
                    var price = (float)Call(shop, "GetWeaponSellPrice", wi);
                    Main.Log($"[test] sell price {wi?.GetName()} = {price} (mult {RarityTable.SellPriceMult(moved)})");
                }),
                ("sell it", () =>
                {
                    if (shop == null) return;
                    Main.Log($"[test] SellWeapon -> {Call(shop, "SellWeapon", Tracker.ItemById(addedId))}");
                }),
                ("dump after sell", () =>
                {
                    Tracker.DumpState("test after sell");
                    var gone = Tracker.Held.All(h => h.Roll.Uid != moved?.Uid);
                    Main.Log($"[test] CHECK sold roll removed: {(gone ? "PASS" : "FAIL")}");
                }),
            };
        }
    }
}
