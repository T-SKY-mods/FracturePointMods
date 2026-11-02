using System;
using System.Collections.Generic;
using System.Linq;
using FPModOption;
using Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core;
using Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.AdsLayer;
using Il2CppKINEMATION.FPSAnimationFramework.Runtime.Recoil;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // アタッチメントの組み合わせで撃ち方・性能が変わらないか（autotest -Dev -Scenario custfire / custfire1）。
    //   連射：フルオートの武器（とストックのハンドガン）で、銃口×グリップの全組み合わせごとに 1.2 秒引き金を引き、出た弾数を数える
    //   構え：武器ごとの構えの速さ（AdsLayerSettings.aimingSpeed）と、構え終わるまでの時間
    public static class StatLab
    {
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        const float HoldSec = 1.2f;

        public static void Register()
        {
            TestMode.ExternalScenarios["custfire"] = () => Steps();
            TestMode.ExternalScenarios["custfire1"] = () => Steps(new HashSet<int> { 11 });   // AKSU
            TestMode.ExternalScenarios["custfire2"] = () => Steps(new HashSet<int> { 2, 15, 18, 28 });   // 最初からバーティカルグリップ
        }

        static readonly List<(float at, Action act)> _timed = new();
        static Action _sample;

        public static void Tick()
        {
            for (int i = _timed.Count - 1; i >= 0; i--)
                if (Time.unscaledTime >= _timed[i].at) { var a = _timed[i].act; _timed.RemoveAt(i); try { a(); } catch (Exception e) { Main.Log("[stat] timed failed " + e.Message); } }
            if (_sample != null) try { _sample(); } catch (Exception e) { Main.Log("[stat] sample failed " + e.Message); _sample = null; }
        }

        static IEnumerable<Att> ErgoAtts(int id)
        {
            float E(Att a) => Effects.Stats(a, id).Where(x => x.Stat == WeaponStat.Ergo).Sum(x => x.Value);
            var all = new List<Att>();
            foreach (var c in Catalog.CatsFor(id))
                foreach (var a in Catalog.For(id, c))
                    if (!Catalog.IsFactory(a, id) && a.Source != Source.StatsOnly && E(a) != 0f) all.Add(a);
            all.Sort((x, y) => E(x).CompareTo(E(y)));
            if (all.Count == 0) yield break;
            yield return all.First();
            if (E(all.Last()) > 0f && all.Count > 1) yield return all.Last();
        }

        static void ResetAll(int id) { foreach (var c in Catalog.CatsFor(id)) Store.SetForTest(id, c, null); }

        // 銃口・グリップ・ストック（と、それぞれ無し）の全組み合わせ
        static List<List<(Cat, string)>> Configs(int id, bool auto)
        {
            List<string> Of(Cat c) => new[] { (string)null }.Concat(Catalog.CatsFor(id).Contains(c) ? Catalog.For(id, c).Where(a => a.Source != Source.StatsOnly).Select(a => a.Id) : Enumerable.Empty<string>()).Distinct().ToList();
            var list = new List<List<(Cat, string)>>();
            foreach (var m in Of(Cat.Muzzle))
                foreach (var g in Of(Cat.Grip))
                    foreach (var st in Of(Cat.Stock))
                    {
                        if (!auto && st != "p_stock") continue;   // セミオートのハンドガンはストックを付けたときだけ連射
                        var l = new List<(Cat, string)>();
                        if (m != null) l.Add((Cat.Muzzle, m));
                        if (g != null) l.Add((Cat.Grip, g));
                        if (st != null) l.Add((Cat.Stock, st));
                        list.Add(l);
                    }
            return list;
        }

        public static List<(string, Action)> Steps(HashSet<int> only = null)
        {
            var s = new List<(string, Action)>();
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                var w = wi;
                if (!Catalog.Eligible(w) || (only != null && !only.Contains(w.Id))) continue;
                int id = w.Id; var name = Tracker.NameOf(id);
                var pdw = w.Weapon?.gameObject?.GetComponent<DemoWeapon>();
                if (pdw == null) continue;
                var cfgs = Configs(id, pdw.supportsAuto);
                s.Add(($"add {id}", () =>
                {
                    ResetAll(id); Tracker.ApplyAll();
                    if (Tracker.Held.Any(h => h.Roll.WeaponId == id)) return;
                    while (Tracker.Held.Count >= 3) Refs.Bag.MoveWeaponToStash(Tracker.ItemById(Tracker.Held.First(h => h.Roll.WeaponId != id).Roll.WeaponId));
                    Tracker.Inv.AddWeapon(Tracker.ItemById(id));
                }));
                s.Add(("wait", () => { }));
                s.Add(($"equip {id}", () =>
                {
                    var pn = Tracker.ItemById(id).Weapon.gameObject.name;
                    var inst = Refs.Fps.InstantiatedWeapons;
                    for (int i = 0; i < inst.Count; i++)
                        if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == pn) { Refs.Fps.StartWeaponChange(i); return; }
                    Main.Log($"[stat] {name} not in hands");
                }));
                s.Add(("wait", () => { }));
                s.Add(("wait", () => { }));
                s.Add(($"flash {id}", () =>
                {
                    var d = Active(); var pf = pdw.fireParticlesEffect;
                    Main.Log($"[stat] [{id}] prefab flash={(pf != null ? pf.name + " parent=" + pf.transform.parent?.name + " scene=" + pf.scene.IsValid() : "null")} inst flash={(d?.fireParticlesEffect != null ? d.fireParticlesEffect.name + " parent=" + d.fireParticlesEffect.transform.parent?.name : "dead/null")}");
                }));
                for (int j = 0; j < 3; j++) s.Add(("wait", () => { }));   // 持ち替えの動きが終わるまで
                // 構えの速さ（何も付けない状態）
                s.Add(($"ads {id}", () => Ads(id, name, "none")));
                for (int j = 0; j < 4; j++) s.Add(("wait", () => { }));
                foreach (var cfg in cfgs)
                {
                    var c = cfg; var label = c.Count == 0 ? "none" : string.Join("+", c.Select(x => x.Item2));
                    s.Add(($"set {id} {label}", () => { ResetAll(id); foreach (var (cat, a) in c) Store.SetForTest(id, cat, a); Tracker.ApplyAll(); }));
                    s.Add(("wait", () => { }));
                    s.Add(($"fire {id} {label}", () => Fire(id, name, label, c)));
                    for (int j = 0; j < 3; j++) s.Add(("wait", () => { }));
                }
                // エルゴのアタッチメント（いちばん上がる物・下がる物）を付けて構える速さ
                foreach (var ea in ErgoAtts(id))
                {
                    var e = ea;
                    s.Add(($"set {id} {e.Id}", () => { ResetAll(id); Store.SetForTest(id, e.Cat, e.Id); Tracker.ApplyAll(); }));
                    s.Add(("wait", () => { }));
                    s.Add(("wait", () => { }));
                    s.Add(($"ads {id} {e.Id}", () => Ads(id, name, e.Id)));
                    for (int j = 0; j < 4; j++) s.Add(("wait", () => { }));
                }
                s.Add(($"cleanup {id}", () => { _timed.Clear(); RestoreFps(); _sample = null; ResetAll(id); Tracker.ApplyAll(); Refs.Bag.MoveWeaponToStash(Tracker.ItemById(id)); }));
                s.Add(("wait", () => { }));
            }
            return s;
        }

        static bool? _safe, _consume;
        static void RestoreFps() { var f = Refs.Fps; if (f == null) return; if (_safe is bool a) f.inSafeZone = a; if (_consume is bool b) f.consumeAmmo = b; }

        static void Fire(int id, string name, string label, List<(Cat, string)> c0)
        {
            var dw = Active();
            if (dw == null || !dw.gameObject.name.Contains(Tracker.ItemById(id).Weapon.gameObject.name)) { Check($"{name} [{label}]: held", false); return; }
            if (!dw.supportsAuto) { Check($"{name} [{label}]: full auto available", false); return; }
            var ra = dw._recoilAnimation;
            string before = $"weapon={dw._fireMode} player={ra?.fireMode}";
            var fx = dw.fireParticlesEffect;
            string Path(Transform t) { var n = ""; for (; t != null && t != dw.transform; t = t.parent) n = t.name + "/" + n; return n; }
            Main.Log($"[stat] [{id}] flash: alive={fx != null} {(fx != null ? $"{Path(fx.transform)} active={fx.activeSelf} inHier={fx.activeInHierarchy} ps={fx.GetComponentsInChildren<ParticleSystem>(true).Length}" : "")} firePos={(dw.FireParticlePosition != null ? Path(dw.FireParticlePosition) : "null")}");
            // 撃ち方はゲームが武器ごとに覚えている。ここではフルオートにしてから撃つ（切り替えのボタンと同じく、プレイヤー側にも渡す）
            if (dw._fireMode != FireMode.Auto) { dw._fireMode = FireMode.Auto; if (ra != null && dw.recoilData != null) ra.Init(dw.recoilData, dw.fireRate, dw._fireMode); }
            var mag = dw.SelectedMag;
            if (mag == null) { Check($"{name} [{label}]: has a mag", false); return; }
            mag.AmmoCount = 60;
            int start = mag.AmmoCount;
            float t0 = Time.unscaledTime;
            // 拠点では撃てない・弾が減らない（inSafeZone / consumeAmmo）。撃つ間だけ外す
            var fps = Refs.Fps; bool safe = fps.inSafeZone, consume = fps.consumeAmmo;
            _safe ??= safe; _consume ??= consume;
            fps.inSafeZone = false; fps.consumeAmmo = true;
            dw.OnFirePressed();
            // バーティカルグリップ・コンペンセイター：1 発ごとの倍率と、ゲームが使っている反動のデータがそれか
            bool live = Store.Equipped(id, Cat.Grip)?.Id == "vertical_grip" || Store.Equipped(id, Cat.Muzzle)?.Id is "p_compensator" or "muzzle_brake";
            var perShot = new List<string>(); int lastAmmo = start; bool allUsed = true; var scales = new List<float>();
            if (live) _sample = () =>
            {
                int a = mag.AmmoCount;
                if (a >= lastAmmo) return;
                lastAmmo = a;
                bool used = Effects.LiveUsedForTest(dw, id, out float sc);
                allUsed &= used; scales.Add(sc);
                perShot.Add($"{Effects.ShotsForTest}:{sc:0.00}{(used ? "" : "!")}");
            };
            _timed.Add((t0 + HoldSec, () =>
            {
                _sample = null;
                if (live)
                {
                    Main.Log($"[stat] [{id}] {name} [{label}]: recoil scale per shot (shots:scale) {string.Join(" ", perShot)}");
                    Check($"{name} [{label}]: the game uses the per-shot recoil", allUsed && scales.Count >= 3);
                    // 最初から付いているバーティカルグリップ（AK-12 など）も数える
                    bool vg = Store.Equipped(id, Cat.Grip)?.Id == "vertical_grip", comp = Store.Equipped(id, Cat.Muzzle)?.Id is "p_compensator" or "muzzle_brake";
                    if (vg) Check($"{name} [{label}]: vertical grip lowers the recoil while firing ({(scales.Count > 0 ? scales.First() : 0):0.00} -> {(scales.Count > 0 ? scales.Last() : 0):0.00})", !comp && scales.Count >= 5 ? scales.Last() <= scales.First() - 0.015f * (scales.Count - 1) : scales.Count >= 8 && scales.Last() < scales[Effects.CompShots + 1]);
                    if (comp && vg) Check($"{name} [{label}]: compensator and vertical grip: lower for the first {Effects.CompShots} shots, then lowering per shot", scales.Count > Effects.CompShots + 3 && scales[1] < scales[Effects.CompShots + 1] - 0.05f && scales[Effects.CompShots + 3] < scales[Effects.CompShots + 1]);
                    if (comp && !vg) Check($"{name} [{label}]: compensator: lower recoil for the first {Effects.CompShots} shots", scales.Count > Effects.CompShots + 1 && scales[1] < scales[Effects.CompShots + 1] - 0.15f);
                }
                int shots = start - mag.AmmoCount;
                dw.OnFireReleased();
                fps.inSafeZone = safe; fps.consumeAmmo = consume;
                float expect = dw.fireRate / 60f * HoldSec;
                Main.Log($"[stat] [{id}] {name} [{label}]: {shots} shots in {HoldSec}s (rpm {dw.fireRate:0} -> about {expect:0}) mode before {before}, now weapon={dw._fireMode} player={ra?.fireMode}");
                Check($"{name} [{label}]: full auto keeps firing ({shots} shots, about {expect:0})", shots >= Mathf.Max(3f, expect * 0.6f));
                mag.AmmoCount = Math.Min(mag.AmmoCount, 30);
            }));
        }

        // 構え終わるまで：カメラが照準点から 2mm 以内になるまでの時間
        // エルゴ → 構える速さ（FPModOption.AdsSpeed）：書いた速さになっているか、その速さで構え終わるか（時間 ≒ 1 / aimingSpeed）
        static void Ads(int id, string name, string label)
        {
            var dw = Active(); var cam = Camera.main;
            if (dw == null || cam == null) return;
            var ads = AdsSpeed.SettingsOf(dw);
            if (ads == null) { Main.Log($"[stat] [{id}] {name}: no ads settings"); return; }
            float ergo = AdsSpeed.ErgoOf(dw), mult = AdsSpeed.Mult(ergo), want = AdsSpeed.Original(ads) * mult;
            Check($"{name} [{label}]: aim speed from ergo {ergo:+0.#;-0.#;0} = x{mult:0.00} ({ads.aimingSpeed:0.###}, want {want:0.###})", Mathf.Abs(ads.aimingSpeed - want) < 0.001f);
            var sg = dw.scopeGroups;
            var ap = sg != null && sg.Count > 0 && sg[0]?.attachments != null && sg[0].attachments.Count > 0 ? sg[0].attachments[0]?.aimPoint : null;
            if (ap == null) { Main.Log($"[stat] [{id}] no aim point"); return; }
            float t0 = Time.unscaledTime, done = -1f, speed = ads.aimingSpeed;
            dw.OnAimPressed();
            _sample = () =>
            {
                float d = (cam.transform.position - ap.position).magnitude;
                if (done < 0f && d < 0.002f) done = Time.unscaledTime - t0;
                if (done >= 0f || Time.unscaledTime - t0 > 3f)
                {
                    _sample = null;
                    dw.OnAimReleased();
                    Main.Log($"[stat] [{id}] {name} [{label}]: aimingSpeed {speed:0.###} (orig {AdsSpeed.Original(ads):0.###}) -> aimed in {(done >= 0f ? done.ToString("0.000") + "s" : "never")}");
                    Check($"{name} [{label}]: aim time follows the speed ({done:0.00}s, about {1f / speed:0.00}s)", done > 0.55f / speed && done < 1.2f / speed);
                }
            };
        }
    }
}
