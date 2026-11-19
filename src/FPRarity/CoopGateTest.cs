using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using FPModOption;

namespace FPRarity
{
    // 自動テスト（autotest -Scenario coopgate）。co-op が始まったら全 Mod が止まり、Mod のセーブにも触らないことを確かめる。
    //   拠点（ソロ）で Mod が効いていることを見る → ゲームの co-op 開始の入口（CoopSaves.Begin(Host)）を呼ぶ →
    //   Mod が止まる・書き換えた値が戻る・武器を倉庫へ移して保存しても Mod は追跡も保存もしない
    //   （実際の co-op の接続はしない。プロファイルはソロと同じ "" を使う。セーブは autotest.ps1 が前後で退避・復元する）
    public static class CoopGateTest
    {
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");

        static int ShownChanged()
        {
            int n = 0;
            foreach (var wi in Weapons.All())
                if (wi != null && (wi.Ergonomic != StatScale.OrigErgo(wi) || wi.Control != StatScale.OrigControl(wi))) n++;
            return n;
        }

        static string StatePath => Path.Combine(Main.Dir, "state.json");
        static DateTime StateTime => File.Exists(StatePath) ? File.GetLastWriteTimeUtc(StatePath) : DateTime.MinValue;

        public static List<(string, Action)> Steps()
        {
            int stashBefore = 0, heldBefore = 0;
            DateTime stateBefore = default;
            long skippedAtStart = 0;
            var s = new List<(string, Action)>
            {
                ("solo: mods are active", () =>
                {
                    Check($"patches wrapped for co-op ({CoopGuard.WrappedCount})", CoopGuard.WrappedCount >= 60);
                    Check("not in co-op at the start", !CoopGuard.Off && !CoopGuard.InCoop(out _));
                    int n = ShownChanged();
                    Check($"solo: shown ergo/control changed by the mods on {n} weapon(s)", n > 0);
                    Main.Log($"[test] held={Tracker.Held.Count} stash={Tracker.Stash.Count} mode={Il2CppCoop.CoopSaves.Mode}");
                }),
                ("start co-op through the game's entry (CoopSaves.Begin Host)", () =>
                {
                    stateBefore = StateTime;
                    stashBefore = Tracker.Stash.Count; heldBefore = Tracker.Held.Count;
                    Il2CppCoop.CoopSaves.Begin(Il2CppCoop.CoopPlayMode.Host, "");
                    skippedAtStart = CoopGuard.Skipped;
                    Check($"co-op detected from the game's entry ({CoopGuard.Why})", CoopGuard.Off && (CoopGuard.Why ?? "").Contains("Begin"));
                }),
                ("values written into game data are restored", () =>
                {
                    int n = ShownChanged();
                    Check($"co-op: shown ergo/control back to the game's values (changed {n})", n == 0);
                }),
                ("move a weapon to the stash and save (vanilla only)", () =>
                {
                    var inv = Refs.Inv; var bag = Refs.Bag;
                    var prefabs = inv?.WeaponsList?.weaponPrefabs;
                    WeaponItem w = null;
                    if (prefabs != null) for (int i = 0; i < prefabs.Count && w == null; i++) if (prefabs[i] != null) w = Weapons.ItemByPrefab(prefabs[i].name);
                    Main.Log($"[test] held weapon to move={w?.GetName() ?? "none"} bag={bag != null}");
                    if (w != null && bag != null)
                    {
                        try { bag.MoveWeaponToStash(w); } catch (Exception e) { Main.Log("[test] move failed " + e.Message); }
                        try { TestMode.Call(bag, "SaveProgress"); } catch (Exception e) { Main.Log("[test] save failed " + e.Message); }
                    }
                }),
            };
            for (int i = 0; i < 4; i++) s.Add(("wait", () => { }));
            s.Add(("mods did not track or save anything", () =>
            {
                Check($"co-op: mod did not track the move (stash {stashBefore}->{Tracker.Stash.Count}, held {heldBefore}->{Tracker.Held.Count})",
                      Tracker.Stash.Count == stashBefore && Tracker.Held.Count == heldBefore);
                Check($"co-op: state.json not written ({stateBefore:HH:mm:ss.fff} -> {StateTime:HH:mm:ss.fff})", StateTime == stateBefore);
                long sk = CoopGuard.Skipped - skippedAtStart;
                Check($"co-op: mod patch bodies skipped while the game runs ({sk} calls)", sk > 0);
            }));
            s.Add(("back to solo mode (mods stay off until restart)", () =>
            {
                Il2CppCoop.CoopSaves.Begin(Il2CppCoop.CoopPlayMode.Solo, "");
                Check("mods stay off after co-op until the game is restarted", CoopGuard.Off);
            }));
            return s;
        }
    }
}
