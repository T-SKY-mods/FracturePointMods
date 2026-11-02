using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // autotest -Dev -Scenario custmount：手に持った武器に部品が付く・最初の部品が消える・元に戻せるかを確かめて、腰だめと構えた画面を撮る。
    // セーブは autotest.ps1 が退避・復元し、UserData/FPCustom はテスト後に消える
    public static class MountTest
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static void Shot(string n) => ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, n + ".png"));
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();

        public static void Register() => TestMode.ExternalScenarios["custmount"] = Steps;

        sealed class Case { public int Id; public (Cat, string)[] Set; public string Label, Label2; public bool FactoryOff, Grip, Laser; public int IronFrom = -1; public string GripLabel = "vgrip"; }

        static readonly Case[] Cases =
        {
            new Case { Id = 0,  Set = new[] { (Cat.Muzzle, "muzzle_brake") }, Label = "arbrake" },                  // AK-74：銃口の部品にかぶせる
            new Case { Id = 2,  Set = new[] { (Cat.Muzzle, ""), (Cat.Grip, ""), (Cat.Sight, ""), (Cat.SideRail, "") }, FactoryOff = true },   // AK-12：最初の部品を全部外す
            new Case { Id = 1,  Set = new[] { (Cat.Muzzle, "p_suppressor"), (Cat.SideRail, "p_laser"), (Cat.Sight, "p_dot") }, Label = "psup", Laser = true },                     // SP-229
            new Case { Id = 7,  Set = new[] { (Cat.Muzzle, "p_suppressor") }, Label = "psup" },                     // Model 92（レーザー付き）
            new Case { Id = 23, Set = new[] { (Cat.Muzzle, "") }, FactoryOff = true },                              // CV-1：サプレッサーを外す
            new Case { Id = 28, Set = new[] { (Cat.Sight, "") }, FactoryOff = false, IronFrom = 27 },               // Viper：ホロを外す → MPS-5 の照準点
            new Case { Id = 2,  Set = new[] { (Cat.Sight, "ace_sight") }, Label = null },                          // AK-12：ホロ → ACE のサイト
            new Case { Id = 4,  Set = new[] { (Cat.Sight, "holo") }, Label = null },                               // AKMS：ハンドガードの上のレールにホロ
            new Case { Id = 17, Set = new[] { (Cat.Sight, "") }, Label = null },                                   // Swiss：スコープを外す → アイアンサイト（リアサイトは残す）
            new Case { Id = 17, Set = new[] { (Cat.Sight, "holo") }, Label = null },                               // Swiss：スコープ → ホロ（リアサイトを削った形）
            new Case { Id = 3,  Set = new[] { (Cat.Grip, "vertical_grip") }, Label = "vgrip", Grip = true },        // QBZ：グリップを付ける（持ち方も変わる）
            new Case { Id = 10, Set = new[] { (Cat.Grip, "vertical_grip"), (Cat.Muzzle, "muzzle_brake"), (Cat.SideRail, "laser"), (Cat.Sight, "holo") }, Label = "guard", Grip = true, Laser = true },   // M16：レール付きハンドガード＋ハンドルなし＋ブレーキ＋レーザー＋ホロ
            new Case { Id = 0,  Set = new[] { (Cat.Grip, "angled_grip") }, Label = "rail", Grip = true, GripLabel = "agrip" },          // AK-74：アングルフォアグリップ＋ボルト留めのレール
            new Case { Id = 4,  Set = new[] { (Cat.Grip, "vertical_grip"), (Cat.Sight, "holo") }, Label = "guard", Grip = true },      // AKMS：木を除いた形＋B-10、上にホロ
            new Case { Id = 11, Set = new[] { (Cat.Grip, "vertical_grip") }, Label = "guard", Grip = true },                           // AKSU：木のハンドガードを消して B-10
            new Case { Id = 20, Set = new[] { (Cat.Muzzle, "suppressor") }, Label = "suppressor" },                                    // Type 89：サプレッサー（音も）
            new Case { Id = 13, Set = new[] { (Cat.Muzzle, "p_compensator"), (Cat.Stock, "p_stock") }, Label = "p_compensator", Label2 = "stock" },   // G-18C：コンペンセイター＋ストック
        };

        // 左手の位置（武器の根元の座標）。グリップの枠を切り替えると持ち方が変わるかを見る
        static Vector3 AimLocal(DemoWeapon dw)
        {
            var g = dw?.scopeGroups;
            var ap = g != null && g.Count > 0 && g[0]?.attachments != null && g[0].attachments.Count > 0 ? g[0].attachments[0]?.aimPoint : null;
            return ap == null ? Vector3.zero : dw.transform.InverseTransformPoint(ap.position);
        }

        static Transform _hand;
        static void LogHand(DemoWeapon dw, string when)
        {
            try
            {
                if (_hand == null)
                {
                    foreach (var t in Refs.Fps.GetComponentsInChildren<Transform>(true))
                    {
                        var n = t.name.ToLowerInvariant();
                        if (n.Contains("hand") && (n.Contains("_l") || n.Contains("left") || n.EndsWith(".l")) && !n.Contains("ik") && !n.Contains("finger")) { _hand = t; break; }
                    }
                    Main.Log($"[test] left hand bone = {(_hand != null ? SightLab.RelPath(_hand, Refs.Fps.transform) : "none")}");
                }
                var h = _hand;
                var g = dw.gripAttachments;
                Main.Log($"[test] hand {dw.name} {when}: left={(h != null ? dw.transform.InverseTransformPoint(h.position).ToString("F3") : "-")} slot={(g != null ? g._activeIndex : -1)}");
            }
            catch (Exception e) { Main.Log("[test] hand failed " + e.Message); }
        }

        // 付けた部品の名前と数（同じ名前が 2 つ以上なら x2）
        static string PartNames(Transform root) => string.Join(",", root.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith(Mount.Prefix)).GroupBy(t => t.name).Select(g => g.Count() > 1 ? $"{g.Key}x{g.Count()}" : g.Key));

        public static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () =>
            {
                Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "mount_*.png")) File.Delete(f);
                Check("ACE-31 sight cannot be removed (no iron sights)", !Catalog.CanRemove(16, Cat.Sight) && !Store.Equip(16, Cat.Sight, null) && Store.Equipped(16, Cat.Sight)?.Id == "ace_sight");
                string Opts(int id, Cat cat) => string.Join(",", Catalog.For(id, cat).Select(a => a.Id));
                Main.Log($"[test] options: AKSU muzzle={Opts(11, Cat.Muzzle)} Hunter-32 muzzle={Opts(18, Cat.Muzzle)} VAL muzzle={Opts(14, Cat.Muzzle)} AK-74 grip={Opts(0, Cat.Grip)} ACE-31 grip={Opts(16, Cat.Grip)} QBZ grip={Opts(3, Cat.Grip)} Hunter-12 muzzle={Opts(19, Cat.Muzzle)}");
                Check("VAL has no muzzle options; AKSU and Hunter-32 have", Catalog.For(11, Cat.Muzzle).Count > 0 && Catalog.For(18, Cat.Muzzle).Count > 0 && Catalog.For(14, Cat.Muzzle).Count == 0);
                Check("grip options: AK-74 / ACE-31 angled grip, QBZ vertical grip", Catalog.For(0, Cat.Grip).Any(a => a.Id == "angled_grip") && Catalog.For(16, Cat.Grip).Any(a => a.Id == "angled_grip") && Catalog.For(3, Cat.Grip).Any(a => a.Id == "vertical_grip"));
                Check("suppressor on rifles; pistol stock = M93R only", Catalog.For(10, Cat.Muzzle).Any(a => a.Id == "suppressor")
                      && Catalog.For(13, Cat.Stock).Select(a => a.Id).SequenceEqual(new[] { "p_stock" }));
            }));
            foreach (var cs in Cases)
            {
                var c = cs;
                var name = Tracker.NameOf(c.Id);
                s.Add(($"set {c.Id}", (Action)(() =>
                {
                    foreach (var (cat, att) in c.Set) Store.SetForTest(c.Id, cat, att);
                    if (Tracker.Held.Any(h => h.Roll.WeaponId == c.Id)) { Tracker.ApplyAll(); return; }
                    if (Tracker.Held.Count >= 3)
                    {
                        var other = Tracker.Held.First(h => h.Roll.WeaponId != c.Id);
                        Refs.Bag.MoveWeaponToStash(Tracker.ItemById(other.Roll.WeaponId));
                    }
                    Main.Log($"[test] AddWeapon {name} -> {Tracker.Inv.AddWeapon(Tracker.ItemById(c.Id))}");
                })));
                s.Add(("wait", () => { }));
                s.Add(($"equip {c.Id}", () =>
                {
                    var pn = Tracker.ItemById(c.Id).Weapon.gameObject.name;
                    var inst = Refs.Fps.InstantiatedWeapons;
                    for (int i = 0; i < inst.Count; i++)
                        if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == pn) { Refs.Fps.StartWeaponChange(i); return; }
                    Main.Log($"[test] {name} not in hands");
                }));
                s.Add(("wait", () => { }));
                s.Add(("wait", () => { }));
                s.Add(($"check {c.Id}", () =>
                {
                    var dw = Active();
                    bool held = dw != null && dw.gameObject.name.Contains(Tracker.ItemById(c.Id).Weapon.gameObject.name);
                    Check($"{name}: held", held);
                    if (!held) return;
                    var root = dw.transform;
                    Main.Log($"[test] parts {name}: {PartNames(root)}");
                    if (c.Label != null) Check($"{name}: {c.Label} attached once", Mount.Count(root, c.Label) == 1);
                    if (c.Label2 != null) Check($"{name}: {c.Label2} attached once", Mount.Count(root, c.Label2) == 1);
                    if (Guards.Replace.TryGetValue(c.Id, out var gd) && c.Grip)
                    {
                        Check($"{name}: wooden handguard hidden", gd.Hide.All(h => root.Find(h)?.GetComponent<Renderer>()?.enabled == false)
                              && gd.Swaps.All(sw => root.Find(sw.Renderer)?.GetComponent<MeshFilter>()?.sharedMesh?.Pointer != Tracker.ItemById(c.Id).Weapon.transform.Find(sw.Renderer)?.GetComponent<MeshFilter>()?.sharedMesh?.Pointer));
                        if (c.Id == 10) Check($"{name}: no handguard and no carry handle (sight on)", root.Find("M16/M16")?.GetComponent<MeshFilter>()?.sharedMesh?.vertexCount == 5187);
                    }
                    var pdw = Tracker.ItemById(c.Id).Weapon.gameObject.GetComponent<DemoWeapon>();
                    var want = Store.Equipped(c.Id, Cat.Grip);
                    if (c.Grip || c.Id == 2)
                    {
                        // アングルフォアグリップは「なし」の枠に持ち方を入れる（枠が無い武器はアングルの枠）
                        int idx = want?.Id == "angled_grip" && Grips.SlotIndex(dw, null) >= 0 ? Grips.SlotIndex(dw, null) : Grips.SlotIndex(dw, want);
                        Check($"{name}: grip slot = {want?.Id ?? "none"} (#{idx})", idx >= 0 && dw.gripAttachments._activeIndex == idx && dw.gripAttachments.attachments[idx].gameObject.activeSelf);
                    }
                    if (c.Grip) Check($"{name}: grip model added", Mount.Count(root, c.GripLabel) == 1);
                    if (c.Laser)
                    {
                        var lz = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == Mount.Prefix + "laser");
                        var beam = lz?.GetComponentInChildren<LineRenderer>(true);
                        Check($"{name}: laser attached with beam on", lz != null && lz.gameObject.activeInHierarchy && beam != null && beam.enabled && beam.gameObject.activeInHierarchy);
                    }
                    LogHand(dw, "equipped");
                    {
                        var d = Sound.DonorFor(c.Id);
                        var exp = Tracker.ItemById(d ?? c.Id).Weapon.gameObject.GetComponent<DemoWeapon>();
                        // 気付かれる範囲：付けたサプレッサーは元の武器の範囲の半分（Effects）、最初から付いているもの・付けていないものは音の元と同じ
                        var muz = Store.Equipped(c.Id, Cat.Muzzle);
                        bool addedSup = (muz?.Id == "suppressor" || muz?.Id == "p_suppressor") && !Catalog.IsFactory(muz, c.Id);
                        float wantAlert = addedSup ? (pdw.customAlertDistance != 0f ? pdw.customAlertDistance : Effects.DefaultAlert) * Effects.SuppressedAlert
                            : Catalog.IsFactory(muz, c.Id) ? pdw.customAlertDistance : exp.customAlertDistance;
                        Check($"{name}: fire sound from {Tracker.NameOf(d ?? c.Id)}, alert {dw.customAlertDistance:0.##} (want {wantAlert:0.##})", dw.fireClips?.Pointer == exp.fireClips?.Pointer && Mathf.Abs(dw.customAlertDistance - wantAlert) < 0.001f);
                        Main.Log($"[test] sound {name}: alert {exp.customAlertDistance} clips {exp.fireClips?.Count}; held alert {dw.customAlertDistance} clips {dw.fireClips?.Count} same={dw.fireClips?.Pointer == exp.fireClips?.Pointer}");
                    }
                    if (c.Id == 2)
                    {
                        // サイトを外しても残る照準の模様の正体を探す（見えている描画物で、材質・シェーダーの名前がサイトらしいもの）
                        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                        {
                            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                            var mats = string.Join(",", r.sharedMaterials.Where(m => m != null).Select(m => m.name + "/" + m.shader?.name));
                            var low = (r.name + mats).ToLowerInvariant();
                            if (low.Contains("reflex") || low.Contains("reticle") || low.Contains("holo") || low.Contains("dot") || low.Contains("sight") || low.Contains("glass") || low.Contains("lens"))
                                Main.Log($"[test] sightish renderer '{SightLab.RelPath(r.transform, root)}' {r.GetIl2CppType().Name} mats=[{mats}]");
                        }
                    }
                    var ap = AimLocal(dw);
                    if (c.Set.Any(x => x.Item1 == Cat.Sight))
                    {
                        if (Sights.HasFactorySight(c.Id)) Check($"{name}: factory sight hidden", Factory.On(c.Id).Where(p => Catalog.Get(p.Att)?.Cat == Cat.Sight).All(p => !Factory.Shown(root, p)));
                        // 最初からサイトが付いている武器の形の差し替え（Swiss のリアサイト）：ほかのサイトのときだけ
                        if (Sights.HasFactorySight(c.Id) && Optics.SpotFor(c.Id) is { } fsp && fsp.Swaps.Length > 0)
                        {
                            bool wantSwap = Store.Equipped(c.Id, Cat.Sight) is { } eq && !Catalog.IsFactory(eq, c.Id);
                            var pr = Tracker.ItemById(c.Id).Weapon.transform;
                            bool swapped = fsp.Swaps.All(sw => root.Find(sw.Renderer)?.GetComponent<MeshFilter>()?.sharedMesh?.Pointer != pr.Find(sw.Renderer)?.GetComponent<MeshFilter>()?.sharedMesh?.Pointer);
                            Check($"{name}: rear sight {(wantSwap ? "removed for the added sight" : "kept")}", swapped == wantSwap);
                        }
                        var pap = AimLocal(Tracker.ItemById(c.Id).Weapon.gameObject.GetComponent<DemoWeapon>());
                        Main.Log($"[test] aim {name}: prefab=({pap.x:F4},{pap.y:F4},{pap.z:F4}) now=({ap.x:F4},{ap.y:F4},{ap.z:F4})");
                        if (c.IronFrom >= 0)
                        {
                            var sap = AimLocal(Tracker.ItemById(c.IronFrom).Weapon.gameObject.GetComponent<DemoWeapon>());
                            Check($"{name}: aim copied from {Tracker.NameOf(c.IronFrom)}", (ap - sap).magnitude < 0.0005f);
                        }
                        else if (Store.Equipped(c.Id, Cat.Sight) == null) Check($"{name}: aim lowered to iron", ap.y < pap.y - 0.01f && Mathf.Abs(ap.z - pap.z) < 0.0005f);
                        else Check($"{name}: {Store.Equipped(c.Id, Cat.Sight).Id} mounted, aim on its reticle", Mount.Count(root, "sight") == 1 && (ap - Optics.LastAim).magnitude < 0.0005f);
                    }
                    if (c.FactoryOff)
                        Check($"{name}: factory parts hidden", Factory.On(c.Id).All(p => !Factory.Shown(root, p)));
                    // 付け直しても増えない
                    int before = Mount.Count(root);
                    Tracker.ApplyAll();
                    var again = Active().transform;
                    Main.Log($"[test] parts {name} re-applied ({before} before): {PartNames(again)}");
                    Check($"{name}: re-apply does not duplicate parts", new[] { "sight", "vgrip", "agrip", "rail", "guard", "laser", "akbrake", "arbrake", "psup", "suppressor", "p_compensator", "stock" }.All(l => Mount.Count(again, l) <= 1)
                          && Mount.Count(again) == before && before > 0 == c.Set.Any(x => !string.IsNullOrEmpty(x.Item2) && !Catalog.IsFactory(Catalog.Get(x.Item2), c.Id)));
                    Shot($"mount_{c.Id:00}_hip");
                }));
                s.Add(("aim", () => { try { Active()?.OnAimPressed(); } catch (Exception e) { Main.Log("[test] aim failed " + e.Message); } }));
                s.Add(("wait", () => { }));
                s.Add(($"ads {c.Id}", () =>
                {
                    Shot($"mount_{c.Id:00}_ads");
                    // 構えたとき、載せたサイトの照準の模様が画面の中心に来ているか
                    var dw = Active(); var cam = Camera.main;
                    if (dw != null && cam != null && Mount.Count(dw.transform, "sight") == 1 && Optics.ReticleById.TryGetValue(c.Id, out var rl))
                    {
                        var sp = cam.WorldToScreenPoint(dw.transform.TransformPoint(rl));
                        float off = new Vector2(sp.x - Screen.width / 2f, sp.y - Screen.height / 2f).magnitude / Screen.height;
                        Main.Log($"[test] ads reticle {name}: screen=({sp.x:F0},{sp.y:F0}) of {Screen.width}x{Screen.height} off={off:F3}");
                        Check($"{name}: reticle at screen center when aiming", off < 0.03f);
                    }
                }));
                s.Add(("release", () => { try { Active()?.OnAimReleased(); } catch { } }));
                s.Add(("wait", () => { }));
                s.Add(($"revert {c.Id}", (Action)(() =>
                {
                    // 最初の状態に戻すと、付けた部品が消えて最初の部品がゲームのままに戻る
                    foreach (var (cat, _) in c.Set) Store.SetForTest(c.Id, cat, null);
                    Tracker.ApplyAll();
                    var dw = Active();
                    if (dw == null) return;
                    var prefab = Tracker.ItemById(c.Id).Weapon.gameObject.transform;
                    Check($"{name}: revert removes added parts ({PartNames(dw.transform)})", Mount.Count(dw.transform) == 0);
                    LogHand(dw, "reverted (same frame)");
                    var pg = Tracker.ItemById(c.Id).Weapon.gameObject.GetComponent<DemoWeapon>().gripAttachments;
                    var pa = AimLocal(Tracker.ItemById(c.Id).Weapon.gameObject.GetComponent<DemoWeapon>());
                    if (c.Set.Any(x => x.Item1 == Cat.Sight)) Check($"{name}: revert restores aim point", (AimLocal(dw) - pa).magnitude < 0.0005f);
                    Check($"{name}: revert restores fire sound", dw.fireClips?.Pointer == Tracker.ItemById(c.Id).Weapon.gameObject.GetComponent<DemoWeapon>().fireClips?.Pointer);
                    Check($"{name}: revert restores grip slot", pg == null || dw.gripAttachments._activeIndex == pg._activeIndex);
                    if (Guards.Replace.TryGetValue(c.Id, out var rg))
                        Check($"{name}: revert restores handguard", rg.Hide.All(h => dw.transform.Find(h)?.GetComponent<Renderer>()?.enabled == prefab.Find(h)?.GetComponent<Renderer>()?.enabled)
                              && rg.Swaps.All(sw => dw.transform.Find(sw.Renderer)?.GetComponent<MeshFilter>()?.sharedMesh?.Pointer == prefab.Find(sw.Renderer)?.GetComponent<MeshFilter>()?.sharedMesh?.Pointer));
                    Check($"{name}: revert restores factory parts", Factory.On(c.Id).All(p =>
                    {
                        var a = Factory.Renderers(dw.transform, p); var b = Factory.Renderers(prefab, p);
                        return Factory.Shown(dw.transform, p) == Factory.Shown(prefab, p) && a.Count == b.Count && a.Zip(b, (x, y) => x.enabled == y.enabled).All(v => v);
                    }));
                })));
                s.Add(($"hand after revert {c.Id}", () => { var dw = Active(); if (dw != null) LogHand(dw, "reverted"); }));
                s.Add(($"cleanup {c.Id}", () => { Refs.Bag.MoveWeaponToStash(Tracker.ItemById(c.Id)); }));
                s.Add(("wait", () => { }));
            }
            return s;
        }
    }
}
