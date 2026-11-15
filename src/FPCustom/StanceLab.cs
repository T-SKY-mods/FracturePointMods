using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using Entity = Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorEntity;

namespace FPCustom
{
    // 調査用（autotest -Dev -Scenario custstance）。ストック付きハンドガンの構えを、動作ごとに連続で撮る。
    //   同じ手順を「ゲームのハンドガンのまま」「見本のメイン武器」「新しい構え」で流し、
    //   プレイヤーの視点と横からの 2 枚を 1 枚に並べて shots/st_<設定>_<秒>_<動作>.png に書き出す
    public static class StanceLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static DemoWeapon Active() => Weapons.Active();
        static float Every = 0.3f;
        static bool Short, Look, LookAim, FireRun, Poi;
        static bool _turning; static float _turnUntil;
        static readonly Dictionary<string, string> AttOf = new();   // 設定の名前 → 付けるストック（無ければ M93R）

        public static void Register()
        {
            TestMode.ExternalScenarios["custstance"] = () => Steps(new[] { ("pistol", 7, true, 0), ("rifle", 0, false, 0), ("smg", 28, false, 0) });
            TestMode.ExternalScenarios["custstance3"] = () => Steps(new[] { ("anchor", 7, true, 3) });
            TestMode.ExternalScenarios["custstance2"] = () => Steps(new[] { ("ctrl", 7, true, 2) });
            TestMode.ExternalScenarios["custstancegif"] = () => { Every = 0.1f; return Steps(new[] { ("pistol", 7, true, 0), ("anchor", 7, true, 3) }); };
            // ストックを付けられる全ハンドガン：リロード・弾薬チェック・空のリロードの間の床尾の動きと傾きを測る
            TestMode.ExternalScenarios["custstanceall"] = () => { Every = 0.5f; Short = true; return Steps(AssetParts.StockAt.Keys.OrderBy(k => k).Select(k => ($"all{k:00}", k, true, 3)).ToArray()); };
            // 追加のストック 4 種類を手に持ったときの見え方（G-18C に 4 種類、Model 92 に 3 種類）
            TestMode.ExternalScenarios["custstocks"] = () =>
            {
                Every = 0.5f; Short = true;
                var list = new List<(string, int, bool, int)>();
                foreach (var (id, atts) in new[] { (13, new[] { "p_poly_stock", "p_wood_stock", "p_frame_stock" }), (7, new[] { "p_wood_stock", "p_frame_stock" }) })
                    foreach (var a in atts) { var cfg = $"{a.Substring(2, 4)}{id:00}"; AttOf[cfg] = a; list.Add((cfg, id, true, 3)); }
                return Steps(list.ToArray());
            };
            TestMode.ExternalScenarios["custstockp"] = () =>
            {
                Every = 0.5f; Short = true;
                AttOf["poly13"] = AttOf["poly09"] = AttOf["poly23"] = "p_poly_stock";
                return Steps(new[] { ("poly13", 13, true, 3), ("poly09", 9, true, 3), ("poly23", 23, true, 3) });
            };
            // めり込みの計測：G-18C に M93R と追加の 4 種類
            TestMode.ExternalScenarios["custclip"] = () =>
            {
                Every = 0.25f; Short = true;
                var list = new List<(string, int, bool, int)> { ("m93r13", 13, true, 3) };
                foreach (var a in new[] { "p_poly_stock", "p_wood_stock", "p_frame_stock" }) { var cfg = $"{a.Substring(2, 4)}13"; AttOf[cfg] = a; list.Add((cfg, 13, true, 3)); }
                return Steps(list.ToArray());
            };
            // ワイヤーストックのめり込み：グリップの高さ・背中の傾きが違う 4 丁
            TestMode.ExternalScenarios["custclipw"] = () =>
            {
                Every = 0.25f; Short = true;
                var list = new List<(string, int, bool, int)>();
                foreach (var id in new[] { 13, 25, 9, 23 }) { var cfg = $"fram{id:00}"; AttOf[cfg] = "p_frame_stock"; list.Add((cfg, id, true, 3)); }
                return Steps(list.ToArray());
            };
            TestMode.ExternalScenarios["custclip1"] =() => { Every = 0.25f; Short = true; return Steps(new[] { ("m93r13", 13, true, 3) }); };
            TestMode.ExternalScenarios["custclip0"] = () => { Every = 0.25f; Short = true; Stance.ArmsOn = false; return Steps(new[] { ("noarm13", 13, true, 3) }); };
            // 見た目の確認：ゲームの中の明るさで、手に持った銃とストックを左から近くで撮る（look_<設定>.png）
            TestMode.ExternalScenarios["custlook"] = () =>
            {
                Look = true;
                var list = new List<(string, int, bool, int)>();
                foreach (var (id, atts) in new[] { (13, new[] { "p_poly_stock", "p_wood_stock", "p_frame_stock" }), (9, new[] { "p_poly_stock" }), (7, new[] { "p_wood_stock", "p_frame_stock" }), (25, new[] { "p_frame_stock" }) })
                    foreach (var a in atts) { var cfg = $"{a.Substring(2, 4)}{id:00}"; AttOf[cfg] = a; list.Add((cfg, id, true, 3)); }
                return Steps(list.ToArray());
            };
            TestMode.ExternalScenarios["custlookw"] = () =>
            {
                Look = true;
                foreach (var id in new[] { 13, 25 }) AttOf[$"fram{id:00}"] = "p_frame_stock";
                return Steps(new[] { ("fram13", 13, true, 3), ("fram25", 25, true, 3) });
            };
            // 構えの設定（AdsLayerSettings）を武器ごとに書き出す
            TestMode.ExternalScenarios["custadsdump"] = () => new List<(string, Action)> { ("dump", () =>
            {
                for (int id = 0; id < 40; id++)
                {
                    var w = Tracker.ItemById(id)?.Weapon?.gameObject?.GetComponent<DemoWeapon>(); if (w == null) continue;
                    var a = FPModOption.AdsSpeed.SettingsOf(w);
                    var prof = w.GetComponent<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorEntity>()?.animatorProfile;
                    Main.Log($"[adsdump] [{id}] {Tracker.NameOf(id)} profile={prof?.name} ads={a?.name} ptr={a?.Pointer} cameraBlend={a?.cameraBlend} aimTarget={a?.aimTargetBone?.name}#{a?.aimTargetBone?.index} camBone={UnityEngine.Object.FindObjectOfType<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Camera.FPSCameraController>()?.cameraBone?.name} aimingSpeed={a?.aimingSpeed} aimPointSpeed={a?.aimPointSpeed}");
                }
            }) };
            TestMode.ExternalScenarios["custlooka"] = () =>
            {
                Look = true; LookAim = true;
                AttOf["poly13"] = "p_poly_stock"; AttOf["fram13"] = "p_frame_stock"; AttOf["wood07"] = "p_wood_stock";
                return Steps(new[] { ("poly13", 13, true, 3), ("fram13", 13, true, 3), ("wood07", 7, true, 3) });
            };
            // G-18C にストック 4 種類と無しで、腰だめ・エイムで撃ち続けたときの見た目（st_<設定>_*.png）
            TestMode.ExternalScenarios["custg18"] = () =>
            {
                Every = 0.15f; FireRun = true;
                var list = new List<(string, int, bool, int)> { ("none13", 13, false, 3) };
                foreach (var a in new[] { "p_stock", "p_poly_stock", "p_wood_stock", "p_frame_stock" }) { var cfg = $"{a.Substring(2, 4)}13"; AttOf[cfg] = a; list.Add((cfg, 13, true, 3)); }
                return Steps(list.ToArray());
            };
            // ストックを付けられる全ハンドガン：エイムして 1 発撃ち、弾が画面の中心からどれだけずれるか（付けた・付けていない）
            // 連射したときの着弾：1911 と G-18C に、ストック無し・M93R・VP70・ワイヤー（腰だめで撃ち続ける → エイムで速く撃つ）
            TestMode.ExternalScenarios["custrapid"] = () =>
            {
                Every = 0.1f; FireRun = true;
                var list = new List<(string, int, bool, int)>();
                foreach (var id in new[] { 25, 13 })
                {
                    list.Add(($"none{id:00}", id, false, 3));
                    foreach (var a in id == 25 ? new[] { "p_stock", "p_wood_stock", "p_frame_stock" } : new[] { "p_stock", "p_poly_stock", "p_frame_stock" }) { var cfg = $"{a.Substring(2, 4)}{id:00}"; AttOf[cfg] = a; list.Add((cfg, id, true, 3)); }
                }
                // 見せてもらった画面と同じ組み合わせ：G-18C に VP70 ストック・RMR・レーザー
                AttOf["full13"] = "p_poly_stock+p_dot+p_laser"; list.Add(("full13", 13, true, 3));
                return Steps(list.ToArray());
            };
            TestMode.ExternalScenarios["custpoi"] = () =>
            {
                Poi = true;
                var list = new List<(string, int, bool, int)>();
                foreach (var k in AssetParts.StockAt.Keys.OrderBy(k => k)) { list.Add(($"poiv{k:00}", k, false, 3)); AttOf[$"pois{k:00}"] = "p_wood_stock"; list.Add(($"pois{k:00}", k, true, 3)); }
                return Steps(list.ToArray());
            };
            TestMode.ExternalScenarios["custstance0"] =() => Steps(new[] { ("pistol", 7, true, 0) });
        }

        static IntPtr _buttFor; static Vector3 _butt;
        static Dictionary<string, Transform> _bones;
        static bool _sane;
        static readonly Dictionary<(string, string), (float d, string phase, Vector3 at, string model)> _clipMax = new();
        public static void ClipSummary(string cfg)
        {
            var mine = _clipMax.Where(kv => kv.Key.Item1 == cfg).OrderByDescending(kv => kv.Value.d).ToList();
            if (mine.Count == 0) { Main.Log($"[stclip] SUMMARY {cfg}: no clipping"); return; }
            float worst = mine.Where(kv => !kv.Key.Item2.StartsWith("body") && !kv.Key.Item2.Contains("upper") && kv.Value.phase != "aimcheck").Select(kv => kv.Value.d).DefaultIfEmpty(0f).Max();
            Main.Log($"[stclip] WORST {cfg}: hands and forearms {worst * 1000:0.0}mm");
            foreach (var kv in mine) Main.Log($"[stclip] SUMMARY {cfg}: {kv.Key.Item2} max {kv.Value.d * 1000:0.0}mm during {kv.Value.phase} at {kv.Value.at.ToString("F3")} in {kv.Value.model}");
        }
        static readonly List<(float at, Action act)> _timed = new();
        static string _phase = "", _cfg;
        static float _t0, _nextShot = -1f;
        static bool _walk, _walkWas;

        public static void Tick()
        {
            for (int i = _timed.Count - 1; i >= 0; i--)
                if (Time.unscaledTime >= _timed[i].at) { var a = _timed[i].act; _timed.RemoveAt(i); try { a(); } catch (Exception e) { Main.Log("[stlab] timed failed " + e.Message); } }
            if ((_walk || _walkWas) && Refs.Fps?._movementComponent is { } mv) mv._inputDirection = _walk ? new Vector2(0f, 1f) : Vector2.zero;
            _walkWas = _walk;
            if (_turning)
            {
                // 壁の前だと銃を引く動きが入るので、前が 4m 以上あいている向きまで回る（マウスを動かすのと同じ入力）
                var cam = Camera.main; var fps = Refs.Fps;
                if (cam != null && fps != null)
                {
                    var f = cam.transform.forward; f.y = 0f; f.Normalize();
                    bool open = !Physics.Raycast(cam.transform.position, f, 4f) && !Physics.Raycast(cam.transform.position + Vector3.down * 0.4f, f, 4f);
                    if (open || Time.unscaledTime > _turnUntil) { _turning = false; fps._lookDeltaInput = Vector2.zero; Main.Log($"[stlab] facing open space: {open}"); }
                    else fps._lookDeltaInput = new Vector2(12f, 0f);
                }
            }
            if (_nextShot >= 0f && Time.unscaledTime >= _nextShot)
            {
                _nextShot += Every;
                try { Shot(); } catch (Exception e) { Main.Log("[stlab] shot failed " + e); _nextShot = -1f; }
            }
        }

        static void ResetAll(int id) { foreach (var c in Catalog.CatsFor(id)) Store.SetForTest(id, c, null); }

        public static List<(string, Action)> Steps((string cfg, int id, bool stock, int mode)[] cfgs)
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "st_*.png")) File.Delete(f); }));
            foreach (var (cfg, id, stock, mode) in cfgs)
            {
                var name = Tracker.NameOf(id);
                s.Add(($"add {cfg}", () =>
                {
                    Stance.Mode = mode;
                    ResetAll(id);
                    if (stock) foreach (var a in (AttOf.TryGetValue(cfg, out var sa) ? sa : "p_stock").Split('+')) Store.SetForTest(id, Catalog.Get(a)?.Cat ?? Cat.Stock, a);
                    if (Tracker.Held.Any(h => h.Roll.WeaponId == id)) return;
                    while (Tracker.Held.Count >= 3) Refs.Bag.MoveWeaponToStash(Tracker.ItemById(Tracker.Held.First(h => h.Roll.WeaponId != id).Roll.WeaponId));
                    Main.Log($"[stlab] AddWeapon {name} -> {Tracker.Inv.AddWeapon(Tracker.ItemById(id))}");
                }));
                s.Add(("wait", () => { }));
                s.Add(($"equip {cfg}", () =>
                {
                    var pn = Tracker.ItemById(id).Weapon.gameObject.name;
                    var inst = Refs.Fps.InstantiatedWeapons;
                    for (int i = 0; i < inst.Count; i++)
                        if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == pn) { Refs.Fps.StartWeaponChange(i); return; }
                    Main.Log($"[stlab] {name} not in hands");
                }));
                s.Add(("wait", () => { }));
                s.Add(($"apply {cfg}", () =>
                {
                    Tracker.ApplyAll();
                    // 予備のマガジンが無いとリロードしない
                    for (int k = 0; k < 3; k++) Tracker.Inv.AddMag(id);
                    var wn = Tracker.ItemById(id).GetName();
                    foreach (var m in Tracker.Inv.MagStored) if (m != null && m.WeaponName == wn) m.AmmoCount = m.MaxAmmoCount;
                }));
                s.Add(("wait", () => { }));
                s.Add(($"face open space {cfg}", () => { _turning = true; _turnUntil = Time.unscaledTime + 6f; }));
                for (int j = 0; j < 4; j++) s.Add(("wait", () => { }));
                if (Look && LookAim)
                {
                    s.Add(($"aim {cfg}", () => Refs.Fps.OnAim(true))); s.Add(("wait", () => { }));
                    s.Add(($"look {cfg}", () => LookShot(cfg + "_aim"))); s.Add(("wait", () => { }));
                    s.Add(($"unaim {cfg}", () => Refs.Fps.OnAim(Refs.Fps.toggleAim))); s.Add(("wait", () => { }));
                }
                else if (Look) { s.Add(($"look {cfg}", () => LookShot(cfg))); s.Add(("wait", () => { })); }
                else if (Poi)
                {
                    s.Add(($"aim {cfg}", () => Refs.Fps.OnAim(true))); s.Add(("wait", () => { })); s.Add(("wait", () => { }));
                    s.Add(($"poi {cfg}", () => PoiLog(cfg, id, stock)));
                    s.Add(($"unaim {cfg}", () => Refs.Fps.OnAim(Refs.Fps.toggleAim))); s.Add(("wait", () => { }));
                }
                else
                {
                s.Add(($"run {cfg}", () => Run(cfg, id)));
                for (int j = 0; j < (Short ? 10 : 21); j++) s.Add(("wait", () => { }));
                }
                s.Add(($"end {cfg}", () => { _timed.Clear(); _walk = false; _nextShot = -1f; }));
                s.Add(($"cleanup {cfg}", () => { Stance.Mode = 3; ResetAll(id); Tracker.ApplyAll(); Refs.Bag.MoveWeaponToStash(Tracker.ItemById(id)); }));
                s.Add(("wait", () => { }));
            }
            return s;
        }

        static void Dump(string cfg, DemoWeapon dw)
        {
            try
            {
                var ent = dw.GetComponent<Entity>();
                var prof = ent?.animatorProfile;
                Main.Log($"[stlab] {cfg}: weapon '{dw.name}' profile '{prof?.name}' controller '{dw.overrideController?.name}' reload '{dw.reloadClip?.clip?.name}' ({dw.reloadClip?.clip?.length:0.00}s) empty '{dw.reloadEmptyClip?.clip?.name}' fire '{dw.fireClip?.clip?.name}' grenade '{dw.grenadeClip?.clip?.name}'");
                if (prof?.settings != null)
                    for (int i = 0; i < prof.settings.Count; i++)
                    {
                        var st = prof.settings[i];
                        Main.Log($"[stlab] {cfg}:   layer {i} {st?.GetIl2CppType()?.Name} '{st?.name}' alpha {st?.alpha:0.00}");
                    }
                var clips = dw.overrideController?.animationClips;
                if (clips != null) Main.Log($"[stlab] {cfg}:   clips " + string.Join(", ", clips.Select(c => c?.name).Distinct()));
            }
            catch (Exception e) { Main.Log("[stlab] dump failed " + e.Message); }
        }

        static void Run(string cfg, int id)
        {
            var dw = Active();
            bool held = dw != null && dw.gameObject.name.Contains(Tracker.ItemById(id).Weapon.gameObject.name);
            Main.Log($"[test] CHECK {cfg}: held: {(held ? "PASS" : "FAIL")}");
            if (!held) return;
            Dump(cfg, dw);
            foreach (var t in dw.GetComponentsInChildren<Transform>(true))
                if (t.name == Mount.Prefix + "stock")
                {
                    Main.Log($"[stlab] {cfg}: stock active {t.gameObject.activeInHierarchy} layer {t.gameObject.layer} local {t.localPosition} scale {t.lossyScale}");
                    foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                        Main.Log($"[stlab] {cfg}:   renderer '{r.name}' enabled {r.enabled} active {r.gameObject.activeInHierarchy} layer {r.gameObject.layer} bounds {r.bounds.center} size {r.bounds.size} mat '{r.sharedMaterial?.name}' shader '{r.sharedMaterial?.shader?.name}'");
                }
            Main.Log($"[stlab] {cfg}: weapon root {dw.transform.position} cam {Camera.main?.transform.position} mask {Camera.main?.cullingMask:X}");
            var fps = Refs.Fps; var mv = fps._movementComponent;
            _cfg = cfg; _t0 = Time.unscaledTime; _nextShot = _t0 + 0.1f;
            void At(float t, string phase, Action a) => _timed.Add((_t0 + t, () => { _phase = phase; a?.Invoke(); }));
            void TopUp() { var m = dw.SelectedMag; if (m != null && m.AmmoCount < 6) m.AmmoCount = 6; }
            _phase = "idle";
            if (FireRun)
            {
                bool safe = fps.inSafeZone, consume = fps.consumeAmmo;
                void Hold(float t, string phase, float len)
                {
                    At(t, phase, () => { var m = dw.SelectedMag; if (m != null) m.AmmoCount = m.MaxAmmoCount; fps.inSafeZone = false; fps.consumeAmmo = true; dw.OnFirePressed(); });
                    At(t + len, phase + "end", () => { dw.OnFireReleased(); fps.inSafeZone = safe; fps.consumeAmmo = consume; });
                }
                // 撃つたびに、弾が撃った瞬間の画面の中心から 25m 先でどれだけずれるか
                _hits.Clear();
                Stance.AfterFireForTest = () => { if (_phase.StartsWith("aimfire") && Bullet25() is { } v) _hits.Add(v); };
                At(0.55f, "hipfire", () => { Stance.MaxDist = Stance.MaxAngle = Stance.RawAngle = 0f; });
                Hold(0.6f, "hipfire", 1.2f);
                At(2.5f, "hipfireend", () => Main.Log($"[test] CHECK {cfg} ({Tracker.NameOf(id)}): hip fire keeps the butt at the shoulder (moved {Stance.MaxDist * 100:0.0}cm, tilt {Stance.MaxAngle:0}° of {Stance.RawAngle:0}°): {(!Stance.StockedId(id) || (Stance.MaxDist <= Stance.MaxAway + 0.01f && Stance.MaxAngle <= Stance.RawAngle * Stance.RotKeep + 2f) ? "PASS" : "FAIL")}"));
                for (int i = 1; i < 5; i++) { float t = 0.6f + 0.25f * i; At(t - 0.03f, "hipfire", () => dw.OnFireReleased()); At(t, "hipfire", () => dw.OnFirePressed()); }
                At(2.6f, "aim", () => fps.OnAim(true));
                // エイムで速く撃つ：0.12 秒ごとに引き金を引き直す（セミでも連射）
                At(3.4f, "aimfire", () => { var m = dw.SelectedMag; if (m != null) m.AmmoCount = m.MaxAmmoCount; fps.inSafeZone = false; fps.consumeAmmo = true; });
                for (int i = 0; i < 12; i++) { float t = 3.4f + 0.12f * i; At(t, "aimfire", () => dw.OnFirePressed()); At(t + 0.06f, "aimfire", () => dw.OnFireReleased()); }
                At(5.0f, "aimfireend", () => { dw.OnFireReleased(); fps.inSafeZone = safe; fps.consumeAmmo = consume; });
                At(5.4f, "unaim", () => fps.OnAim(fps.toggleAim));
                At(6.0f, "result", () =>
                {
                    _nextShot = -1f; Stance.AfterFireForTest = null;
                    Main.Log($"[stlab] {cfg}: fire mode {dw._fireMode} recoil '{dw.recoilData?.name}' pattern '{dw.recoilPatternSettings?.name}'");
                    Main.Log($"[rapid] {cfg} ({Tracker.NameOf(id)}): {_hits.Count} shots at 25m (cm, right/up): " + string.Join(" ", _hits.Select(h => $"({h.x:0},{h.y:0})")));
                    if (_hits.Count > 0)
                    {
                        float worst = _hits.Max(h => h.magnitude), avgY = _hits.Average(h => h.y);
                        Main.Log($"[test] CHECK {cfg} ({Tracker.NameOf(id)}): rapid aimed shots hit the sight (avg up {avgY:0.0}cm, worst {worst:0.0}cm at 25m): {((Mathf.Abs(avgY) < 8f && worst < 12f) ? "PASS" : "FAIL")}");
                    }
                });
                return;
            }
            if (Short)
            {
                // 短い手順：構え → リロード → 弾薬チェック → 空のマガジンからのリロード → 結果
                At(0.6f, "aim", () => fps.OnAim(true));
                At(1.0f, "aimcheck", () => { var ads = Stance.WeightForTest; Main.Log($"[test] CHECK {cfg} ({Tracker.NameOf(id)}): aim pose untouched (weight {ads:0.00}): {(ads < 0.01f ? "PASS" : "FAIL")}");
                    // 肩に当てたエイム：カメラがサイトへ寄る設定・床尾が肩の位置・カメラが銃の真後ろ
                    if (Stance.Mode == 3 && Stance.StockedId(id))
                    {
                        Transform wb = null; foreach (var t in fps.GetComponentsInChildren<Transform>(true)) if (t.name == "IK WeaponBone") { wb = t; break; }
                        var hd = wb.parent; var cam = Camera.main.transform;
                        var butt = hd.InverseTransformPoint(dw.transform.TransformPoint(Stance.ButtLocal(dw.transform, true)));
                        var camW = dw.transform.InverseTransformPoint(cam.position);
                        float blend = Stance.CameraBlendForTest(dw);
                        Main.Log($"[test] CHECK {cfg} ({Tracker.NameOf(id)}): shouldered aim (camera blend {blend:0.#}, bone {Stance.AimBoneForTest}, butt in head {butt.ToString("F3")}, camera from the gun x {camW.x * 1000:0.0}mm z {camW.z * 1000:0}mm): {(blend == 1f && Stance.AimBoneForTest == "Camera" && butt.y < -0.080f && butt.x > 0.055f && Mathf.Abs(camW.x) < 0.006f && camW.z < -0.10f ? "PASS" : "FAIL")}");
                    }
                });
                At(1.3f, "unaim", () => fps.OnAim(fps.toggleAim));
                At(2.0f, "reload", () => { Stance.MaxDist = Stance.MaxAngle = Stance.RawAngle = 0f; var m = dw.SelectedMag; if (m != null) m.AmmoCount = 1; fps.OnReload(); });
                At(6.6f, "ammo", () => fps.OnCheckAmmo());
                At(9.0f, "emptyreload", () => { var m = dw.SelectedMag; if (m != null) m.AmmoCount = 0; fps.OnReload(); });
                At(14.0f, "result", () =>
                {
                    _nextShot = -1f;
                    var n = Tracker.NameOf(id);
                    ClipSummary(cfg);
                    Main.Log($"[stlab] {cfg} {n}: max butt move {Stance.MaxDist * 100:0.0}cm, max tilt {Stance.MaxAngle:0}° (vanilla {Stance.RawAngle:0}°)");
                    Main.Log($"[test] CHECK {cfg} ({n}): stance engaged during actions (vanilla tilt {Stance.RawAngle:0}°): {(Stance.RawAngle > 5f ? "PASS" : "FAIL")}");
                    Main.Log($"[test] CHECK {cfg} ({n}): butt stays at the shoulder ({Stance.MaxDist * 100:0.0}cm <= {Stance.MaxAway * 100 + 1:0}cm): {(Stance.MaxDist <= Stance.MaxAway + 0.01f ? "PASS" : "FAIL")}");
                    Main.Log($"[test] CHECK {cfg} ({n}): tilt damped ({Stance.MaxAngle:0}° <= {Stance.RotKeep * 100:0}% of vanilla {Stance.RawAngle:0}° + 2°): {(Stance.MaxAngle <= Stance.RawAngle * Stance.RotKeep + 2f ? "PASS" : "FAIL")}");
                    Main.Log($"[test] CHECK {cfg} ({n}): back to normal after actions: {(!Stance.HoldingForTest && Stance.WeightForTest < 0.01f ? "PASS" : "FAIL")}");
                });
                return;
            }
            for (int i = 0; i < 3; i++) { float t = 0.9f + 0.3f * i; At(t, "fire", () => { TopUp(); dw.OnFirePressed(); }); At(t + 0.06f, "fire", () => dw.OnFireReleased()); }
            At(2.0f, "aim", () => fps.OnAim(true));
            for (int i = 0; i < 2; i++) { float t = 2.8f + 0.3f * i; At(t, "aimfire", () => { TopUp(); dw.OnFirePressed(); }); At(t + 0.06f, "aimfire", () => dw.OnFireReleased()); }
            At(3.6f, "unaim", () => fps.OnAim(fps.toggleAim));
            At(4.2f, "reload", () => { var m = dw.SelectedMag; if (m != null) m.AmmoCount = 1; fps.OnReload(); });
            At(8.6f, "ammo", () => fps.OnCheckAmmo());
            var startPos = fps.transform.position; var startRot = fps.transform.rotation;
            At(10.8f, "walk", () => _walk = true);
            At(12.0f, "sprint", () => mv.SetSprintRequested(true));
            At(14.0f, "slide", () => mv.OnSlide());
            At(15.4f, "stop", () => { mv.SetSprintRequested(false); _walk = false; });
            At(15.8f, "crouch", () => { if (mv.PoseState.ToString() != "Crouching") mv.OnCrouch(); });
            At(17.0f, "stand", () => { if (mv.PoseState.ToString() == "Crouching") mv.OnCrouch(); });
            At(18.0f, "jump", () => mv.OnJump());
            At(19.2f, "knife", () => fps.OnKnifeAttack());
            At(20.6f, "firemode", () => fps.OnChangeFireMode());
            At(21.4f, "emptyreload", () => { var m = dw.SelectedMag; if (m != null) m.AmmoCount = 0; fps.OnReload(); });
            int mine = -1, other = -1;
            var inst = fps.InstantiatedWeapons;
            for (int i = 0; i < inst.Count; i++) { if (inst[i] == null) continue; if (inst[i].gameObject == dw.gameObject) mine = i; else if (other < 0) other = i; }
            if (mine >= 0 && other >= 0)
            {
                At(26.4f, "holster", () => fps.StartWeaponChange(other));
                At(27.8f, "draw", () => fps.StartWeaponChange(mine));
            }
            At(30.0f, "end", () =>
            {
                _walk = false; mv.SetSprintRequested(false); _nextShot = -1f;
                var cc = fps.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;
                fps.transform.SetPositionAndRotation(startPos, startRot);
                if (cc != null) cc.enabled = true;
            });
        }

        // エイム中に 1 発撃ち（弾道の計算だけ）、25m 先で画面の中心から弾がどれだけずれるか。照準点（サイト）の向き・位置も
        static void PoiLog(string cfg, int id, bool stock)
        {
            var dw = Active(); var cam = Camera.main;
            if (dw == null || cam == null) return;
            var n = Tracker.NameOf(id);
            var bw = dw.GetComponentInChildren<Il2CppBallistics.Weapon>(true);
            var sp = bw?.PhysicalBulletSpawnPoint;
            var g = dw.scopeGroups; var ap = g != null && g.Count > 0 && g[0]?.attachments != null && g[0].attachments.Count > 0 ? g[0].attachments[0]?.aimPoint : null;
            var c = cam.transform;
            string apInfo = ap == null ? "no aim point" : $"aim point angle {Vector3.Angle(ap.forward, c.forward):0.00}° off axis ({Vector3.Dot(ap.position - c.position, c.right) * 1000:0.0},{Vector3.Dot(ap.position - c.position, c.up) * 1000:0.0})mm";
            string spInfo = sp == null ? "" : $" spawn angle {Vector3.Angle(sp.forward, c.forward):0.00}°";
            try
            {
                dw.hitManager.Fire();
                var bh = Il2CppBallistics.BulletHandler.instance;
                var arr = bh.Bullets.ToArray(); var bd = arr[arr.Length - 1];
                float gr = 9.81f; try { gr = bh.g; } catch { }
                var p = bd.bulletPos; float ys = bd.ySpeed; const float dt = 0.0005f; var off = Vector3.zero;
                for (int k = 0; k < 400000; k++) { p += bd.bulletDir * bd.Speed * dt; ys -= gr * dt; p.y += ys * dt; if (Vector3.Dot(p - c.position, c.forward) >= 25f) { off = p - (c.position + c.forward * 25f); break; } }
                float x = Vector3.Dot(off, c.right) * 100f, y = Vector3.Dot(off, c.up) * 100f;
                Main.Log($"[poi] {cfg} ({n}) stock={stock}: 25m=({x:0.0},{y:0.0})cm {apInfo}{spInfo}");
                PoiResult[(id, stock)] = new Vector2(x, y);
                Main.Log($"[test] CHECK {cfg} ({n}): aimed bullet hits the sight ({x:0.0},{y:0.0})cm at 25m: {(Mathf.Abs(x) < 2f && Mathf.Abs(y) < 5f ? "PASS" : "FAIL")}");
            }
            catch (Exception e) { Main.Log("[poi] fire failed " + e.Message); }
        }
        static readonly Dictionary<(int, bool), Vector2> PoiResult = new();
        static readonly List<Vector2> _hits = new();
        // いちばん新しい弾が、今の画面の中心の線から 25m 先でどれだけずれるか（cm、右・上）
        static Vector2? Bullet25()
        {
            var cam = Camera.main; var bh = Il2CppBallistics.BulletHandler.instance;
            if (cam == null || bh == null) return null;
            var arr = bh.Bullets.ToArray(); if (arr.Length == 0) return null;
            var bd = arr[arr.Length - 1]; var c = cam.transform;
            float gr = 9.81f; try { gr = bh.g; } catch { }
            var p = bd.bulletPos; float ys = bd.ySpeed; const float dt = 0.0005f;
            for (int k = 0; k < 400000; k++) { p += bd.bulletDir * bd.Speed * dt; ys -= gr * dt; p.y += ys * dt; if (Vector3.Dot(p - c.position, c.forward) >= 25f) break; }
            var off = p - (c.position + c.forward * 25f);
            return new Vector2(Vector3.Dot(off, c.right) * 100f, Vector3.Dot(off, c.up) * 100f);
        }

        static void LookShot(string cfg)
        {
            var dw = Active(); var main = Camera.main;
            if (dw == null || main == null) return;
            var g = dw.transform;
            const int W = 1600, H = 900;
            var sheet = new Texture2D(W, H * 2, TextureFormat.RGB24, false);
            // 上：左の真横から。下：左後ろの少し上から（プレイヤーが見る向きに近い）
            var views = new[] { (new Vector3(-0.50f, 0.00f, -0.13f), new Vector3(0f, -0.005f, -0.13f)), (new Vector3(-0.30f, 0.16f, -0.50f), new Vector3(0f, 0.0f, -0.10f)) };
            // エイム中：右の真横から・右後ろの上から（右腕とストック）
            if (LookAim) views = new[] { (new Vector3(0.55f, 0.02f, -0.15f), new Vector3(0f, -0.02f, -0.15f)), (new Vector3(0.30f, 0.30f, -0.55f), new Vector3(0f, 0.0f, -0.12f)) };
            for (int v = 0; v < 2; v++)
            {
                var camGo = new GameObject("FPC_LookCam"); var cam = camGo.AddComponent<Camera>();
                cam.CopyFrom(main);
                camGo.transform.position = g.TransformPoint(views[v].Item1);
                camGo.transform.rotation = Quaternion.LookRotation(g.TransformPoint(views[v].Item2) - camGo.transform.position, g.up);
                cam.fieldOfView = 40f; cam.nearClipPlane = 0.03f;
                var lg = new GameObject("FPC_LookLight"); var l = lg.AddComponent<Light>();
                l.type = LightType.Point; l.intensity = 1.1f; l.range = 3f; l.color = new Color(1f, 0.96f, 0.9f);
                lg.transform.position = camGo.transform.position + g.up * 0.25f + g.forward * 0.15f;
                var rt = new RenderTexture(W, H, 24); cam.targetTexture = rt; cam.aspect = (float)W / H;
                cam.Render();
                var prev = RenderTexture.active; RenderTexture.active = rt;
                sheet.ReadPixels(new Rect(0, 0, W, H), 0, (1 - v) * H);
                RenderTexture.active = prev; cam.targetTexture = null;
                UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(camGo); UnityEngine.Object.Destroy(lg);
            }
            sheet.Apply();
            File.WriteAllBytes(Path.Combine(ShotDir, $"look_{cfg}.png"), ImageConversion.EncodeToPNG(sheet));
            UnityEngine.Object.Destroy(sheet);
            Main.Log($"[stlab] look shot {cfg}");
        }

        // プレイヤーの視点（左）と、右前から見た腕と武器（右）
        static void Shot()
        {
            var main = Camera.main;
            if (main == null) return;
            const int W = 960, H = 540;
            var sheet = new Texture2D(W * 3, H, TextureFormat.RGB24, false);
            for (int v = 0; v < 3; v++)
            {
                var camGo = new GameObject("FPC_StCam"); var cam = camGo.AddComponent<Camera>();
                cam.CopyFrom(main);
                GameObject lg = null;
                if (v >= 1)
                {
                    var m = main.transform;
                    var look = m.position + m.forward * 0.30f - m.up * 0.12f;
                    camGo.transform.position = m.position + m.right * (v == 1 ? 0.75f : -0.75f) + m.forward * (v == 1 ? 0.55f : 0.35f) + m.up * 0.05f;
                    camGo.transform.rotation = Quaternion.LookRotation(look - camGo.transform.position, Vector3.up);
                    cam.fieldOfView = 50f; cam.nearClipPlane = 0.05f;
                    lg = new GameObject("FPC_StLight"); var l = lg.AddComponent<Light>();
                    l.type = LightType.Point; l.intensity = 2f; l.range = 4f; lg.transform.position = camGo.transform.position;
                }
                var rt = new RenderTexture(W, H, 24); cam.targetTexture = rt; cam.aspect = (float)W / H;
                cam.Render();
                var prev = RenderTexture.active; RenderTexture.active = rt;
                sheet.ReadPixels(new Rect(0, 0, W, H), v * W, 0);
                RenderTexture.active = prev; cam.targetTexture = null;
                UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(camGo); if (lg != null) UnityEngine.Object.Destroy(lg);
            }
            sheet.Apply();
            float t = Time.unscaledTime - _t0;
            try
            {
                var dw = Active();
                if (dw != null)
                {
                    var head = dw.transform.parent?.parent; var root = dw.transform;
                    if (_buttFor != dw.Pointer) { _buttFor = dw.Pointer; _butt = Stance.ButtLocal(root, Catalog.IsPistol(Weapons.IdOf(dw))); Main.Log($"[stlab] {_cfg}: butt local {_butt.ToString("F3")} bone local {root.localPosition.ToString("F3")} {root.localEulerAngles.ToString("F1")}"); }
                    Main.Log($"[stpose] {_cfg} {t:00.0} {_phase} act {Refs.Fps._actionState} aim {Refs.Fps._aimState} mv {Refs.Fps._movementComponent?.MovementState} root {head.InverseTransformPoint(root.position).ToString("F3")} fwd {head.InverseTransformDirection(root.forward).ToString("F2")} up {head.InverseTransformDirection(root.up).ToString("F2")} butt {head.InverseTransformPoint(root.TransformPoint(_butt)).ToString("F3")} camInHead {head.InverseTransformPoint(main.transform.position).ToString("F3")}");
                }
            }
            catch (Exception e) { Main.Log("[stlab] pose log " + e.Message); }
            try
            {
                // 腕の骨の位置（武器の根から見た値）：ストックの形を腕に当たらないように決めるのに使う
                var dw2 = Active();
                if (dw2 != null)
                {
                    if (_bones == null)
                    {
                        _bones = new Dictionary<string, Transform>();
                        foreach (var tr in Refs.Fps.GetComponentsInChildren<Transform>(true))
                            if (tr.name is "Right_Hand" or "Right_LowerArm" or "Right_UpperArm" or "Left_Hand" or "Left_LowerArm" or "Left_UpperArm") _bones[tr.name] = tr;
                    }
                    var r = dw2.transform;
                    Main.Log($"[starm] {_cfg} {t:00.0} {_phase} " + string.Join(" ", _bones.Select(kv => $"{kv.Key}={r.InverseTransformPoint(kv.Value.position).ToString("F3")}")));
                }
            }
            catch (Exception e) { Main.Log("[stlab] arm log " + e.Message); }
            try
            {
                // ストックへの手・腕のめり込み
                var dw3 = Active();
                if (dw3 != null)
                {
                    var hitsNow = ClipLab.Measure(dw3);
                    if (!_sane)
                    {
                        _sane = true;
                        Main.Log($"[stclip] sanity: nearest arm vertex to the right wrist bone {ClipLab.SanityForTest(dw3) * 100:0.0}cm");
                        foreach (var sk in Refs.Fps.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                            Main.Log($"[stclip] skinned '{sk.name}' enabled {sk.enabled} verts {sk.sharedMesh?.vertexCount} scale {sk.transform.lossyScale.ToString("F2")} root '{sk.rootBone?.name}' inWeapon {sk.transform.IsChildOf(dw3.transform)}");
                    }
                    foreach (var h in hitsNow)
                    {
                        Main.Log($"[stclip] {_cfg} {t:00.0} {_phase} {h.Part} {h.Depth * 1000:0.0}mm at {h.Local.ToString("F3")} in {h.Model}");
                        var key = (_cfg, h.Part);
                        if (!_clipMax.TryGetValue(key, out var old) || h.Depth > old.d) _clipMax[key] = (h.Depth, _phase, h.Local, h.Model);
                    }
                }
            }
            catch (Exception e) { Main.Log("[stlab] clip " + e); }
            File.WriteAllBytes(Path.Combine(ShotDir, $"st_{_cfg}_{t:00.0}_{_phase}.png"), ImageConversion.EncodeToPNG(sheet));
            UnityEngine.Object.Destroy(sheet);
        }
    }
}
