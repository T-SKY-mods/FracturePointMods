using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using BallisticWeapon = Il2CppBallistics.Weapon;

using FPModOption;

namespace FPRarity
{
    // 確認用（autotest -Scenario mastery のときだけ動く）。mastery.json は autotest.ps1 が前後で退避・復元する。
    // プレイヤーと同じ入口（保管庫を開く → [熟練度] ボタン → 武器を選ぶ → [+] / [リセット] → Esc）で操作し、
    // 表示・武器の実際の性能・保存を確認して画面を撮影する。
    public static class MasteryTest
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static int _id = -1;
        static float _baseDmg, _baseRate, _rollDmg, _rollRate;
        static WeaponRoll _roll;
        static readonly List<(CanvasGroup, float)> _shown = new();
        static readonly List<GameObject> _activated = new();

        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static void Shot(string n) { Directory.CreateDirectory(ShotDir); ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, n + ".png")); }

        static (DemoWeapon dw, BallisticWeapon bw) Active()
        {
            var go = Refs.Fps?.GetActiveItem()?.gameObject;
            return (go?.GetComponent<DemoWeapon>(), go?.GetComponentInChildren<BallisticWeapon>(true));
        }

        public static List<(string, Action)> Steps() => new()
        {
            ("setup", () =>
            {
                Directory.CreateDirectory(ShotDir);
                foreach (var f in Directory.GetFiles(ShotDir, "mastery_*.png")) File.Delete(f);
                _roll = Tracker.ActiveRoll(out var item);
                _id = item?.Id ?? -1;
                var (dw, bw) = Active();
                var pbw = item?.Weapon?.GetComponentInChildren<BallisticWeapon>(true);
                _baseDmg = pbw?.MuzzleDamage ?? 0; _baseRate = item?.Weapon?.fireRate ?? 0;
                _rollDmg = bw?.MuzzleDamage ?? 0; _rollRate = dw?.fireRate ?? 0;
                Main.Log($"[test] active={Tracker.NameOf(_id)} id={_id} roll={_roll} dmg={_rollDmg} rate={_rollRate} base={_baseDmg}/{_baseRate}");
                Check("active weapon found", _id >= 0 && _roll != null && bw != null);
                // 遊んでいて振ったポイントがあれば、先に振り直しておく（テスト後に mastery.json は元に戻る）
                if (Mastery.PointsUsed(_id) > 0) Main.Log($"[test] resetting {Mastery.PointsUsed(_id)} points spent in play: {Mastery.Reset(_id)}");
                (dw, bw) = Active();
                _rollDmg = bw?.MuzzleDamage ?? 0; _rollRate = dw?.fireRate ?? 0;   // ポイントなしの値
                Check("starts with no points", Mastery.PointsUsed(_id) == 0);

                // レベル計算: Lv20/40/60/80/100 の必要キル数と、ポイント数
                Main.Log($"[test] kills for Lv2={Mastery.KillsForLevel(2)} Lv20={Mastery.KillsForLevel(20)} Lv40={Mastery.KillsForLevel(40)} Lv60={Mastery.KillsForLevel(60)} Lv80={Mastery.KillsForLevel(80)} Lv100={Mastery.KillsForLevel(100)}");
                Check("Lv100 at 1500 kills", Mastery.LevelOf(1500) == 100 && Mastery.LevelOf(1499) == 99);
                Mastery.SetKillsForTest(_id, Mastery.KillsForLevel(30) + 2);
                Mastery.SetKillsForTest(0, 40); // AK-74 を少しだけ（一覧の表示確認用）
                Check("Lv30 gives 3 points", Mastery.Level(_id) == 30 && Mastery.PointsEarned(_id) == 3);
            }),
            ("open stash", () => Refs.Bag.Interact()),
            ("wait", () => { }),
            ("click mastery button", () =>
            {
                var b = MasteryUi.OpenButtonForTest;
                Check("mastery button exists and visible", b != null && b.activeInHierarchy);
                b?.GetComponent<UnityEngine.UI.Button>()?.onClick.Invoke();
            }),
            ("check page", () =>
            {
                Check("page opened", MasteryUi.IsOpen);
                Main.Log("[test] details: " + MasteryUi.DetailsTextForTest);
                Check("all weapons listed (29)", MasteryUi.DetailsTextForTest.Contains("cards=29"));
                Shot("mastery_0_open");
            }),
            ("allocate dmg x2 + control x1 + one too many", () =>
            {
                MasteryUi.SelectForTest(_id);
                MasteryUi.ClickPlusForTest(0);
                MasteryUi.ClickPlusForTest(0);
                MasteryUi.ClickPlusForTest(2);
                MasteryUi.ClickPlusForTest(1); // 4ポイント目：使い切っているので増えない
            }),
            ("check allocation", () =>
            {
                Main.Log("[test] details: " + MasteryUi.DetailsTextForTest);
                Check("points used 3/3", Mastery.PointsUsed(_id) == 3 && Mastery.PointsIn(_id, WeaponStat.Damage) == 2
                                         && Mastery.PointsIn(_id, WeaponStat.Control) == 1 && Mastery.PointsIn(_id, WeaponStat.FireRate) == 0);
                var (dw, bw) = Active();
                float want = _baseDmg * _roll.DamageMult * (1f + 2 * Mastery.DamagePerPoint);
                Main.Log($"[test] dmg now={bw?.MuzzleDamage} want={want} rate={dw?.fireRate}");
                Check($"held weapon damage +{2 * Mastery.DamagePerPoint * 100:0}%", bw != null && Mathf.Abs(bw.MuzzleDamage - want) < 0.05f);
                Check("fire rate unchanged", dw != null && Mathf.Abs(dw.fireRate - _rollRate) < 0.01f);
                float wantScale = Mathf.Max(RarityTable.MinRecoilScale, StatScale.RecoilMult(_roll.ControlDelta + Mastery.ControlPerPoint));
                Check($"recoil scale includes +{Mastery.ControlPerPoint} control", Mathf.Abs(RarityTable.RecoilScale(_roll) - wantScale) < 0.001f);
                Check("plus disabled when no points left", MasteryUi.DetailsTextForTest.Contains("plusEnabled=False"));
                Check("mastery.json saved", File.Exists(Path.Combine(Main.Dir, "mastery.json")) &&
                                            File.ReadAllText(Path.Combine(Main.Dir, "mastery.json")).Contains("\"Points\""));
                Main.Log("[test] summary: " + Mastery.Summary(_id));
                Shot("mastery_1_allocated");
            }),
            ("close stash for inventory check", () => { Refs.Bag.ClosePage(); Refs.Bag.ClosePage(); }),
            ("inventory details (mastery line)", () =>
            {
                var inv = Main.FindAll<InventoryUI>().FirstOrDefault();
                if (inv == null) { Main.Log("[test] no InventoryUI"); return; }
                inv.UpdateInventory();
                inv.UpdateInventory(); // 2回目で行が重複しないこと
                var all = string.Join("\n---\n", inv.WeaponDetails.ToArray().Select(t => t?.text));
                Main.Log("[test] weapon details:\n" + all);
                int n = 0, at = 0;
                while ((at = all.IndexOf("熟練度 Lv.30", at, StringComparison.Ordinal)) >= 0) { n++; at++; }
                // 0.8.6 から熟練度は詳細欄ではなく、カーソルを合わせたときのポップアップに出す（stashtip で確認）
                Check("details panel has no mastery line", n == 0);
                // 画面を撮るため、インベントリ画面を一時的に表示する（撮影後に戻す）
                _shown.Clear();
                for (var t = inv.WeaponDetails[0]?.transform; t != null; t = t.parent)
                {
                    var g = t.GetComponent<CanvasGroup>();
                    if (g != null) { _shown.Add((g, g.alpha)); g.alpha = 1f; }
                    if (!t.gameObject.activeSelf) { _activated.Add(t.gameObject); t.gameObject.SetActive(true); }
                }
            }),
            ("shot inventory", () =>
            {
                Shot("mastery_4_inventory");
            }),
            ("restore inventory", () =>
            {
                foreach (var (g, a) in _shown) if (g != null) g.alpha = a;
                foreach (var go in _activated) if (go != null) go.SetActive(false);
                _shown.Clear(); _activated.Clear();
            }),
            ("reopen stash", () => Refs.Bag.Interact()),
            ("wait", () => { }),
            ("open page again", () => MasteryUi.OpenButtonForTest?.GetComponent<UnityEngine.UI.Button>()?.onClick.Invoke()),
            ("press Esc (ClosePage)", () => Refs.Bag.ClosePage()),
            ("check esc", () =>
            {
                Check("Esc closes only the mastery page", !MasteryUi.IsOpen && Refs.Bag._stashOpened);
                Shot("mastery_2_back_to_stash");
            }),
            ("reopen + reset", () =>
            {
                MasteryUi.OpenButtonForTest?.GetComponent<UnityEngine.UI.Button>()?.onClick.Invoke();
                MasteryUi.SelectForTest(_id);
                MasteryUi.ClickResetForTest();
            }),
            ("check reset", () =>
            {
                var (dw, bw) = Active();
                Check("reset returns points", Mastery.PointsUsed(_id) == 0 && Mastery.PointsEarned(_id) == 3);
                Check("damage back to rarity value", bw != null && Mathf.Abs(bw.MuzzleDamage - _rollDmg) < 0.05f);
                Main.Log("[test] details: " + MasteryUi.DetailsTextForTest);
                Shot("mastery_3_reset");
            }),
            ("close all", () => { Refs.Bag.ClosePage(); Refs.Bag.ClosePage(); }),
            ("check closed", () => Check("stash closes after page", !MasteryUi.IsOpen && !Refs.Bag._stashOpened)),
        };
    }
}
