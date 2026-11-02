using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

using FPModOption;

namespace FPRarity
{
    // 確認用（autotest -Scenario sightgallery のときだけ動く）。セーブ・mastery.json は autotest.ps1 が退避・復元する。
    // サイト対応の武器を1丁ずつ手持ちに加えて持ち替え、ホロサイトを ON にして腰だめと構えた画面を撮影する。
    public static class SightGallery
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();

        public static List<(string, Action)> Steps()
        {
            var steps = new List<(string, Action)>
            {
                ("prepare", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "gallery_*.png")) File.Delete(f); }),
            };
            // UserData/FPRarity/gallery_ids.txt があれば、その武器だけ（例: "19,20,21"）
            var idsFile = Path.Combine(Main.Dir, "gallery_ids.txt");
            var ids = File.Exists(idsFile) ? File.ReadAllText(idsFile).Split(',').Select(x => int.Parse(x.Trim())).ToList() : SightMount.Config.Keys.OrderBy(k => k).ToList();
            foreach (var id in ids)
            {
                int wid = id;
                steps.Add(($"add {wid}", () =>
                {
                    Mastery.SetKillsForTest(wid, Mastery.KillsForLevel(Mastery.SightLevel));
                    // UserData/FPRarity/gallery_nosight.txt に書いた武器はサイトなしで（比較用）
                    var nsFile = Path.Combine(Main.Dir, "gallery_nosight.txt");
                    bool noSight = File.Exists(nsFile) && File.ReadAllText(nsFile).Split(',').Any(x => x.Trim() == wid.ToString());
                    Mastery.SetSight(wid, !noSight);
                    var wi = Tracker.ItemById(wid);
                    // 既に持っている武器（Type 89 など）は追加しない
                    if (Tracker.Held.Any(h => h.Roll.WeaponId == wid)) return;
                    Main.Log($"[gallery] AddWeapon {Tracker.NameOf(wid)} -> {TestMode.Call(Tracker.Inv, "AddWeapon", wi)}");
                }));
                steps.Add(("wait", () => { }));
                steps.Add(($"equip {wid}", () =>
                {
                    var prefabName = Tracker.ItemById(wid)?.Weapon?.gameObject.name;
                    var inst = Refs.Fps.InstantiatedWeapons;
                    for (int i = 0; i < inst.Count; i++)
                        if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == prefabName)
                        {
                            Refs.Fps.StartWeaponChange(i);
                            Main.Log($"[gallery] equip {Tracker.NameOf(wid)} index={i}");
                            return;
                        }
                    Main.Log($"[gallery] {Tracker.NameOf(wid)} not in hands");
                }));
                steps.Add(("wait", () => { }));
                steps.Add(("wait", () => { }));
                steps.Add(($"hip {wid}", () =>
                {
                    var dw = Active();
                    bool holo = dw != null && SightMount.FindMount(dw.transform) != null;
                    Main.Log($"[gallery] active={dw?.gameObject.name} holo={holo}");
                    Main.Log($"[test] CHECK {Tracker.NameOf(wid)} holding with sight: {(holo && dw.gameObject.name.Contains(Tracker.ItemById(wid).Weapon.gameObject.name) ? "PASS" : "FAIL")}");
                    ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"gallery_{wid:00}_hip.png"));
                }));
                steps.Add(("aim", () => { try { Active()?.OnAimPressed(); } catch (Exception e) { Main.Log("[gallery] aim failed " + e.Message); } }));
                steps.Add(("wait", () => { }));
                steps.Add(($"ads {wid}", () =>
                {
                    ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"gallery_{wid:00}_ads.png"));
                    // 構えた状態で、本体（ボーン）・照準点・照準の模様が武器の根元に対してどこにあるか
                    var dw = Active();
                    if (dw == null) return;
                    var root = dw.transform;
                    var spot = SightMount.Config[wid];
                    var body = spot.Body != null ? root.Find(spot.Body) : null;
                    var g = dw.scopeGroups;
                    var ap = g != null && g.Count > 0 && g[0]?.attachments != null && g[0].attachments.Count > 0 ? g[0].attachments[0]?.aimPoint : null;
                    var mount = SightMount.FindMount(root);
                    var holo = mount != null ? mount.Find("FPR_Holo") ?? mount.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "FPR_Holo") : null;
                    var cam = Camera.main;
                    string Scr(Transform t) => t == null || cam == null ? "-" : cam.WorldToScreenPoint(t.position).ToString("F1");
                    Main.Log($"[gallery] ads [{wid}] cam(root)={(cam != null ? root.InverseTransformPoint(cam.transform.position).ToString("F4") : "-")} body(root)={(body != null ? root.InverseTransformPoint(body.position).ToString("F4") + " rot=" + (Quaternion.Inverse(root.rotation) * body.rotation).eulerAngles.ToString("F1") : "-")} " +
                             $"aim(root)={(ap != null ? root.InverseTransformPoint(ap.position).ToString("F4") : "-")} aimScreen={Scr(ap)} holoScreen={Scr(holo)} screen={Screen.width}x{Screen.height}");
                    if (holo != null)
                        Main.Log($"[gallery] ads [{wid}] holo(root)={root.InverseTransformPoint(holo.position):F4} holoRot(root)={(Quaternion.Inverse(root.rotation) * holo.rotation).eulerAngles:F1} holoLossy={holo.lossyScale:F4} rootLossy={root.lossyScale:F4} mountLocalScale={mount.localScale:F4} mountParent={mount.parent?.name} bodyLossy={(body != null ? body.lossyScale.ToString("F4") : "-")} camFwd(root)={(cam != null ? root.InverseTransformDirection(cam.transform.forward).ToString("F4") : "-")}");
                }));
                steps.Add(("wait", () => { }));
                steps.Add(($"ads late {wid}", () =>
                {
                    ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"gallery_{wid:00}_ads2.png"));
                    var dw = Active(); var cam = Camera.main;
                    if (dw != null && cam != null)
                        Main.Log($"[gallery] ads2 [{wid}] cam(root)={dw.transform.InverseTransformPoint(cam.transform.position):F4} holoScreen={cam.WorldToScreenPoint(SightMount.FindMount(dw.transform).GetComponentsInChildren<Transform>(true).First(t => t.name == "FPR_Holo").position):F1}");
                }));
                // 構えを解いてから少し待ってしまう（構えたまま同じ瞬間にしまうと、構えの状態が次の武器に残って照準がずれる）
                steps.Add(($"release {wid}", () => { try { Active()?.OnAimReleased(); } catch { } }));
                steps.Add(("wait", () => { }));
                steps.Add(($"cleanup {wid}", () =>
                {
                    if (wid != 20) TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(wid)); // 手持ちを空けて次の武器へ
                }));
                steps.Add(("wait", () => { }));
            }
            return steps;
        }
    }
}
