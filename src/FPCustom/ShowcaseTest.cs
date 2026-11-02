using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 紹介用の撮影（autotest -Dev -Scenario custshow）。セーブ・custom.json は autotest.ps1 が前後で退避・復元する。
    //   1. 壊れた武器部品で、AKSU と Model 92 のアタッチメントを解放して装着する（プレイヤーと同じ解放・装着の処理）
    //   2. カスタマイズ画面を撮る（撮影の間はゲームを英語にする）
    //   3. 2 丁を持ってエンドレスの明るい階（オフィス）へ行き、見通しのよい場所で腰だめ・エイムを撮る
    public static class ShowcaseTest
    {
        const int Rifle = 11, Pistol = 7;   // AKSU / Model 92

        static readonly (Cat Cat, string Id)[] RifleParts = { (Cat.Muzzle, "suppressor"), (Cat.Grip, "angled_grip"), (Cat.Sight, "ace_sight"), (Cat.SideRail, "laser"), (Cat.Mag, "ext_mag") };
        static readonly (Cat Cat, string Id)[] PistolParts = { (Cat.Muzzle, "p_suppressor"), (Cat.Stock, "p_stock"), (Cat.Sight, "p_dot"), (Cat.SideRail, "p_laser"), (Cat.Mag, "p_ext_mag") };

        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static void Shot(string n) { Directory.CreateDirectory(ShotDir); ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, n + ".png")); }
        static void Check(string what, bool ok) => Core.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static void Wait(List<(string, Action)> s, int n = 1) { for (int i = 0; i < n; i++) s.Add(("wait", () => { })); }
        static string _lang;

        public static void Register() => TestMode.ExternalScenarios["custshow"] = Steps;

        // 解放して装着する（解放済み・最初から付いている部品は装着だけ）
        static void Fit(int id, (Cat Cat, string Id)[] parts)
        {
            int n = 0;
            foreach (var (cat, att) in parts)
            {
                var a = Catalog.For(id, cat).FirstOrDefault(x => x.Id == att);
                if (a == null) { Core.Log($"[test] showcase: {att} does not fit {Tracker.NameOf(id)}"); continue; }
                if (!Store.Unlocked(id, a)) Store.Unlock(id, a);
                if (Store.Equip(id, cat, a)) n++;
            }
            Check($"showcase: {Tracker.NameOf(id)} unlocked and fitted every part ({Store.Describe(id)}, parts left {Parts.Count()})", n == parts.Length);
        }

        static void Aim(bool on)
        {
            var fps = Refs.Fps;
            if (fps == null) return;
            int st = (int)fps._aimState;
            if ((st == 2 || st == 3) == on) return;
            fps.OnAim(on || fps.toggleAim);   // 切り替え式なら「もう一度押す」、押している間だけなら「離す」
        }

        static void Equip(int id)
        {
            var pn = Tracker.ItemById(id).Weapon.gameObject.name;
            var inst = Refs.Fps.InstantiatedWeapons;
            for (int i = 0; i < inst.Count; i++)
                if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == pn) { Refs.Fps.StartWeaponChange(i); return; }
            Core.Log($"[test] showcase: {Tracker.NameOf(id)} not in hands");
        }

        // 自室の保管庫の前に立って、保管庫の方を向く（保管庫が背景に入る）
        static void MoveNearStash()
        {
            var fps = Refs.Fps; var bag = Refs.Bag;
            if (fps == null || bag == null) { Core.Log("[test] showcase: no player / stash"); return; }
            var target = bag.transform.position;
            float best = -1f; Vector3 bestPos = fps.transform.position;
            var rng = new System.Random(5);
            for (int k = 0; k < 800; k++)
            {
                var ang = (float)rng.NextDouble() * Mathf.PI * 2f;
                var dist = 2.2f + (float)rng.NextDouble() * 1.3f;
                var p = target + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * dist;
                // 自室には NavMesh が無いので、床を下向きの光線で探す（保管庫と同じ床の高さ）
                if (!Physics.Raycast(p + Vector3.up * 1.2f, Vector3.down, out var floor, 3f, ~0, QueryTriggerInteraction.Ignore)) continue;
                var feet = floor.point;
                if (Mathf.Abs(feet.y - target.y) > 0.8f) continue;
                if (Physics.CheckSphere(feet + Vector3.up * 0.6f, 0.3f, ~0, QueryTriggerInteraction.Ignore) || Physics.CheckSphere(feet + Vector3.up * 1.3f, 0.3f, ~0, QueryTriggerInteraction.Ignore)) continue;
                // 保管庫まで見通せて、後ろに余裕がある場所ほどよい
                var eye = feet + Vector3.up * 1.6f;
                var to = target + Vector3.up * 0.8f - eye;
                if (Physics.Raycast(eye, to.normalized, out var h, to.magnitude - 0.6f)) continue;
                float back = Physics.Raycast(eye, -new Vector3(to.x, 0f, to.z).normalized, out var hb, 5f) ? hb.distance : 5f;
                if (back > best) { best = back; bestPos = feet; }
            }
            var dir = target - bestPos; dir.y = 0f;
            var cc = fps.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            fps.transform.position = bestPos + Vector3.up * 0.1f;
            if (dir.sqrMagnitude > 0.01f) fps.transform.rotation = Quaternion.LookRotation(Quaternion.Euler(0f, -20f, 0f) * dir.normalized);   // 保管庫が少し右に入る向き
            if (cc != null) cc.enabled = true;
            Core.Log($"[test] showcase: stash at {target:F1}, standing at {bestPos:F1} (room behind {best:0.0}m)");
            Check("showcase: standing in front of the stash", best >= 0f);
        }

        static void Pose(List<(string, Action)> s, int id, string name)
        {
            s.Add(($"equip {name}", () => Equip(id)));
            Wait(s, 3);   // 持ち替えのアニメーションが終わるまで
            s.Add(($"{name} hip", () => Shot($"show_{name}_hip")));
            s.Add(($"{name} aim", () => Aim(true)));
            Wait(s, 2);
            s.Add(($"{name} aim shot", () => Shot($"show_{name}_aim")));
            s.Add(($"{name} aim off", () => Aim(false)));
            Wait(s);
        }

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>
            {
                ("parts and language", () =>
                {
                    Refs.Inv.AddLootItem(Parts.ItemId, Store.UnlockCost * 10 + 47, false);   // 10 個の解放 + 端数
                    _lang = Il2CppGameLocalization.LocManager.Code;
                    Il2CppGameLocalization.LocManager.SetLanguage("en");
                }),
            };
            EffectTest.HoldForLab(s, Rifle);
            EffectTest.HoldForLab(s, Pistol);
            s.Add(("unlock and fit", () => { Fit(Rifle, RifleParts); Fit(Pistol, PistolParts); Weapons.ApplyAll(); }));
            s.Add(("open stash", () => Refs.Bag.Interact()));
            Wait(s);
            s.Add(("click customize tab", () => CustomUi.OpenButtonForTest?.GetComponent<UnityEngine.UI.Button>()?.onClick.Invoke()));
            Wait(s);
            s.Add(("page rifle", () =>
            {
                Check("showcase: customize page open in English", CustomUi.IsOpen && !Core.Ja);
                CustomUi.SelectForTest(Rifle);
                CustomUi.BrowseToForTest(1, "angled_grip");   // 効果の多いグリップを表示
            }));
            Wait(s, 2);
            s.Add(("page rifle shot", () => Shot("show_page_aksu")));
            s.Add(("page pistol", () => { CustomUi.SelectForTest(Pistol); CustomUi.BrowseToForTest(1, "p_stock"); }));
            Wait(s, 2);
            s.Add(("page pistol shot", () => Shot("show_page_m92")));
            s.Add(("close", () => { Refs.Bag.ClosePage(); Refs.Bag.ClosePage(); }));
            Wait(s);
            s.Add(("to the stash", MoveNearStash));
            Wait(s);
            Pose(s, Rifle, "aksu");
            Pose(s, Pistol, "m92");
            s.Add(("language back", () => Il2CppGameLocalization.LocManager.SetLanguage(string.IsNullOrEmpty(_lang) ? "ja" : _lang)));
            return s;
        }
    }
}
