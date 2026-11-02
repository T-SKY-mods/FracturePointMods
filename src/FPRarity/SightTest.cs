using System;
using System.Collections.Generic;
using System.IO;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

using FPModOption;

namespace FPRarity
{
    // 確認用（autotest -Scenario sight のときだけ動く）。mastery.json は autotest.ps1 が退避・復元する。
    // 手に持っている武器（サイト対応）で、Lv9 では付けられない → Lv10 で解放 → 熟練度ページで ON → 武器にサイトが付き照準点が上がる
    // → 構えた画面・腰だめの画面を撮影 → OFF で元に戻る、を確認する。
    public static class SightTest
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static int _id = -1;
        static Vector3 _aimBefore;

        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static void Shot(string n) => ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, n + ".png"));

        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();

        static Transform AimPoint(DemoWeapon dw)
        {
            var g = dw?.scopeGroups;
            return g != null && g.Count > 0 && g[0]?.attachments != null && g[0].attachments.Count > 0 ? g[0].attachments[0]?.aimPoint : null;
        }

        static Vector3 AimLocal(DemoWeapon dw)
        {
            var ap = AimPoint(dw);
            return ap == null ? Vector3.zero : dw.transform.InverseTransformPoint(ap.position);
        }

        public static List<(string, Action)> Steps() => new()
        {
            ("give a supported weapon", () =>
            {
                // サイト対応の武器（設定の先頭）を手持ちに加えて持ち替える
                _id = System.Linq.Enumerable.First(SightMount.Config.Keys);
                var idFile = Path.Combine(Main.Dir, "sighttest_id.txt");   // 別の武器で試すとき（例: "13"）
                if (File.Exists(idFile)) _id = int.Parse(File.ReadAllText(idFile).Trim());
                if (!System.Linq.Enumerable.Any(Tracker.Held, h => h.Roll.WeaponId == _id))
                {
                    // 手持ちが埋まっている（3丁）ときは1丁を保管庫へ移して空ける（セーブはテスト後に元に戻る）
                    if (Tracker.Held.Count >= 3)
                    {
                        var other = System.Linq.Enumerable.First(Tracker.Held, h => h.Roll.WeaponId != _id);
                        Main.Log($"[test] hands full: moving {Tracker.NameOf(other.Roll.WeaponId)} to stash");
                        TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(other.Roll.WeaponId));
                    }
                    TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(_id));
                }
            }),
            ("wait", () => { }),
            ("equip", () =>
            {
                var name = Tracker.ItemById(_id).Weapon.gameObject.name;
                var inst = Refs.Fps.InstantiatedWeapons;
                for (int i = 0; i < inst.Count; i++)
                    if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == name) { Refs.Fps.StartWeaponChange(i); return; }
            }),
            ("wait", () => { }), ("wait", () => { }),
            ("setup", () =>
            {
                Directory.CreateDirectory(ShotDir);
                foreach (var f in Directory.GetFiles(ShotDir, "sighttest_*.png")) File.Delete(f);
                Tracker.ActiveRoll(out var item);
                Check("held the supported weapon", item?.Id == _id);
                Main.Log($"[test] active={Tracker.NameOf(_id)} id={_id} supported={SightMount.Supported(_id)}");
                Check("active weapon supports sight", SightMount.Supported(_id));
                _aimBefore = AimLocal(Active());
                Mastery.SetKillsForTest(_id, Mastery.KillsForLevel(9));
                Check("Lv9: sight locked", Mastery.Level(_id) == 9 && !Mastery.SightUnlocked(_id));
            }),
            ("hip view (no sight)", () => Shot("sighttest_0_hip_nosight")),
            ("open stash + page", () => Refs.Bag.Interact()),
            ("wait", () => { }),
            ("page", () =>
            {
                MasteryUi.OpenButtonForTest?.GetComponent<UnityEngine.UI.Button>()?.onClick.Invoke();
                MasteryUi.SelectForTest(_id);
                MasteryUi.ClickSightForTest(); // ロック中なので何も起きない
            }),
            ("check locked", () =>
            {
                Main.Log("[test] sight ui: " + MasteryUi.SightTextForTest);
                Check("locked: click does nothing", !Mastery.SightOn(_id) && MasteryUi.SightTextForTest.Contains("enabled=False"));
                Shot("sighttest_1_locked");
                Mastery.SetKillsForTest(_id, Mastery.KillsForLevel(10));
                MasteryUi.SelectForTest(_id);
            }),
            ("click ON", () => MasteryUi.ClickSightForTest()),
            ("check on", () =>
            {
                Main.Log("[test] sight ui: " + MasteryUi.SightTextForTest);
                var dw = Active();
                var holo = (dw == null ? null : SightMount.FindMount(dw.transform));
                var aim = AimLocal(dw);
                Main.Log($"[test] aim before=({_aimBefore.x:F4},{_aimBefore.y:F4},{_aimBefore.z:F4}) now=({aim.x:F4},{aim.y:F4},{aim.z:F4}) expected=({SightMount.LastAim.x:F4},{SightMount.LastAim.y:F4},{SightMount.LastAim.z:F4}) reticleY={SightMount.LastReticle.y:F4} holo={(holo != null)}");
                Check("sight ON saved", Mastery.SightOn(_id) && File.ReadAllText(Path.Combine(Main.Dir, "mastery.json")).Contains("\"Sight\": true"));
                Check("holo attached to held weapon", holo != null && holo.gameObject.activeInHierarchy);
                Check("aim point moved to reticle center", (aim - SightMount.LastAim).magnitude < 0.0005f && Mathf.Abs(aim.y - SightMount.LastReticle.y) < 0.006f && Mathf.Abs(aim.z - _aimBefore.z) < 0.001f);
                Shot("sighttest_2_page_on");
            }),
            ("close stash", () => { Refs.Bag.ClosePage(); Refs.Bag.ClosePage(); }),
            ("hip view (sight)", () => Shot("sighttest_3_hip_sight")),
            ("aim", () => { try { Active()?.OnAimPressed(); } catch (Exception e) { Main.Log("[test] aim failed " + e.Message); } }),
            ("wait", () => { }),
            ("ads view", () => Shot("sighttest_4_ads_sight")),
            ("release aim", () => { try { Active()?.OnAimReleased(); } catch { } }),
            ("re-equip check (InitializeWeapons)", () =>
            {
                // 手持ちの作り直し後もサイトが付いているか
                Tracker.ApplyAll();
                Check("sight still on after re-apply", (Active() != null && SightMount.FindMount(Active().transform) != null));
            }),
            ("open stash + OFF", () => Refs.Bag.Interact()),
            ("wait", () => { }),
            ("off", () =>
            {
                MasteryUi.OpenButtonForTest?.GetComponent<UnityEngine.UI.Button>()?.onClick.Invoke();
                MasteryUi.SelectForTest(_id);
                MasteryUi.ClickSightForTest();
            }),
            ("check off", () =>
            {
                var dw = Active();
                var aim = AimLocal(dw);
                Main.Log($"[test] after OFF aim={aim:F3} before={_aimBefore:F3}");
                Check("holo removed", (dw == null ? null : SightMount.FindMount(dw.transform)) == null || !SightMount.FindMount(dw.transform).gameObject.activeInHierarchy);
                Check("aim point restored", (aim - _aimBefore).magnitude < 0.0005f);
                Refs.Bag.ClosePage(); Refs.Bag.ClosePage();
            }),
            ("aim (no sight)", () => { try { Active()?.OnAimPressed(); } catch { } }),
            ("wait", () => { }),
            ("ads view (no sight)", () => Shot("sighttest_5_ads_nosight")),
            ("release", () => { try { Active()?.OnAimReleased(); } catch { } }),
        };
    }
}
