using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // 動きの中での浮き・めり込み（autotest -Dev -Scenario custanim）。武器ごとに、付けられるアタッチメントを全部使い切るまで組み合わせを替えて持ち、
    // 撃つ・構える・リロード・弾薬チェック・歩く・走る・スライディング・しゃがむ・伏せる・ジャンプ・ナイフ・射撃モード切替・持ち替えの間、毎フレーム：
    //   ・部品のまわり 3mm 以内の武器の描画物（頂点）が、部品に対してどれだけずれたか。武器の本体に対しても測り、
    //     本体と一緒に動く物（ずれてはいけない）と、もともと動く物（マガジン・ボルト・スライド等）を分ける。
    //     もともと動く物は、部品が触れている（1mm 以内）のに 3mm 以上離れたときだけ怪しいとする（浮き・めり込み）
    //   ・手の指の骨が部品の形の中にどれだけ入ったか（手のめり込み。グリップは握るので除く）
    // を測る。怪しいときはその瞬間の画面を撮る（shots/anim_*.png）
    public static class AnimLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        const float Near = 0.003f, SupportGap = 0.0015f, BodyMoves = 0.002f, DriftWarn = 0.0015f, MovingWarn = 0.003f, ClipWarn = 0.003f, HandWarn = 0.005f;

        public static void Register()
        {
            TestMode.ExternalScenarios["custanim"] = () => Steps();
            TestMode.ExternalScenarios["custanim1"] = () => Steps(new HashSet<int> { 11, 7 });   // 試し（AKSU・Model 92）
            TestMode.ExternalScenarios["custanim2"] = () => Steps(new HashSet<int> { 0, 1, 5, 6, 7, 8, 9, 11, 12, 13, 20, 22, 23, 24, 25 });   // 見直し（ストックのハンドガン・AK・Type 89）
            TestMode.ExternalScenarios["custanim3"] = () => Steps(new HashSet<int> { 0, 1, 10, 16, 28 });   // 2026-11-01 追加の部品（スタビー・KAC・バイポッド・ブースター・アローヘッド・レーザーの色）
            // 2026-11-02 残りの武器全部（新しい銃口の部品・銃口の位置の見直し・AKM のスラントブレーキの除去）
            TestMode.ExternalScenarios["custanim4"] = () => Steps(new HashSet<int>(Enumerable.Range(0, 64).Except(new[] { 0, 1, 10, 16 })));
        }

        static readonly List<(float at, Action act)> _timed = new();
        static Sampler _cur;
        static string _phase = "";
        static bool _walk, _walkWas;

        public static void Tick()
        {
            for (int i = _timed.Count - 1; i >= 0; i--)
                if (Time.unscaledTime >= _timed[i].at) { var a = _timed[i].act; _timed.RemoveAt(i); try { a(); } catch (Exception e) { Main.Log("[anim] timed failed " + e.Message); } }
            if ((_walk || _walkWas) && Refs.Fps?._movementComponent is { } mv) mv._inputDirection = _walk ? new Vector2(0f, 1f) : Vector2.zero;   // 前へ歩く入力（W の代わり）
            _walkWas = _walk;
            if (_cur != null) try { _cur.Sample(); } catch (Exception e) { Main.Log("[anim] sample failed " + e); _cur = null; }
        }

        static void ResetAll(int id) { foreach (var c in Catalog.CatsFor(id)) Store.SetForTest(id, c, null); }

        // 組み合わせ：カテゴリごとの選択肢（最初から付いている物・見た目の無い物は除く）を k 番目ずつ。全部の選択肢が 1 回は出る
        static List<List<(Cat, string)>> Loadouts(int id)
        {
            var per = Catalog.CatsFor(id).Select(c => (c, Catalog.For(id, c).Where(a => !Catalog.IsFactory(a, id) && a.Source != Source.StatsOnly).Select(a => a.Id).ToList())).Where(x => x.Item2.Count > 0).ToList();
            int n = per.Count == 0 ? 0 : per.Max(x => x.Item2.Count);
            var list = new List<List<(Cat, string)>>();
            for (int k = 0; k < n; k++) list.Add(per.Select(x => (x.c, x.Item2[k % x.Item2.Count])).ToList());
            return list;
        }

        // 立った姿勢に戻す（伏せ → しゃがみ → 立ち）
        static void StandUp()
        {
            var mv = Refs.Fps?._movementComponent;
            if (mv == null) return;
            var ps = mv.PoseState.ToString();
            if (ps == "Prone")
            {
                // 伏せから起きる操作を順に試す（ボタンの操作で起きないときは、ゲームの伏せの終わりの処理を直接呼ぶ）
                try
                {
                    switch (_standTry++ % 4)
                    {
                        case 0: mv.OnProne(); break;
                        case 1: TestMode.Call(mv, "OnProneDisabled"); break;
                        case 2: TestMode.Call(mv, "UnCrouch"); break;
                        default: mv.OnCrouch(); break;
                    }
                }
                catch (Exception e) { Main.Log("[anim] stand up failed " + e.Message); }
            }
            else if (ps == "Crouching") mv.OnCrouch();
            else _standTry = 0;
            Main.Log($"[anim] stand up: {ps} -> try {_standTry}");
        }
        static int _standTry;

        public static List<(string, Action)> Steps(HashSet<int> only = null)
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "anim_*.png")) File.Delete(f); }));
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                var w = wi;
                if (!Catalog.Eligible(w) || (only != null && !only.Contains(w.Id))) continue;
                int id = w.Id;
                var name = Tracker.NameOf(id);
                var lds = Loadouts(id);
                if (lds.Count == 0) continue;
                s.Add(($"add {id}", () =>
                {
                    ResetAll(id);
                    if (Tracker.Held.Any(h => h.Roll.WeaponId == id)) return;
                    while (Tracker.Held.Count >= 3) Refs.Bag.MoveWeaponToStash(Tracker.ItemById(Tracker.Held.First(h => h.Roll.WeaponId != id).Roll.WeaponId));
                    Main.Log($"[anim] AddWeapon {name} -> {Tracker.Inv.AddWeapon(Tracker.ItemById(id))}");
                }));
                s.Add(("wait", () => { }));
                s.Add(($"equip {id}", () =>
                {
                    var pn = Tracker.ItemById(id).Weapon.gameObject.name;
                    var inst = Refs.Fps.InstantiatedWeapons;
                    for (int i = 0; i < inst.Count; i++)
                        if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == pn) { Refs.Fps.StartWeaponChange(i); return; }
                    Main.Log($"[anim] {name} not in hands");
                }));
                s.Add(("wait", () => { }));
                for (int k = 0; k < lds.Count; k++)
                {
                    var ld = lds[k]; var label = string.Join("+", ld.Select(x => x.Item2)); int kk = k;
                    s.Add(($"set {id} {label}", () => { StandUp(); ResetAll(id); foreach (var (c, a) in ld) Store.SetForTest(id, c, a); Tracker.ApplyAll(); }));
                    s.Add(("wait", () => StandUp()));
                    s.Add(($"run {id} {label}", () => Run(id, name, label, kk)));
                    for (int j = 0; j < 20; j++) s.Add(("wait", () => { }));
                    s.Add(($"end {id} {label}", () => { _timed.Clear(); _walk = false; var c = _cur; _cur = null; c?.Report(); }));
                }
                s.Add(($"cleanup {id}", () => { StandUp(); ResetAll(id); Tracker.ApplyAll(); Refs.Bag.MoveWeaponToStash(Tracker.ItemById(id)); }));
                s.Add(("wait", () => StandUp()));
            }
            return s;
        }

        static void Run(int id, string name, string label, int k)
        {
            var dw = Active();
            bool held = dw != null && dw.gameObject.name.Contains(Tracker.ItemById(id).Weapon.gameObject.name);
            Check($"{name} [{label}]: held", held);
            if (!held) return;
            var fps = Refs.Fps; var mv = fps._movementComponent;
            Main.Log($"[anim] [{id}] start pose={mv.PoseState} state={mv.MovementState}");
            _cur = new Sampler(dw, id, name, label, k);
            float t0 = Time.unscaledTime;
            void At(float t, string phase, Action a) => _timed.Add((t0 + t, () => { _phase = phase; a?.Invoke(); }));
            void TopUp() { var m = dw.SelectedMag; if (m != null && m.AmmoCount < 6) m.AmmoCount = 6; }
            _phase = "idle";
            for (int i = 0; i < 5; i++) { float t = 0.5f + 0.22f * i; At(t, "fire", () => { TopUp(); dw.OnFirePressed(); }); At(t + 0.06f, "fire", () => dw.OnFireReleased()); }
            At(2.0f, "aim", () => dw.OnAimPressed());
            for (int i = 0; i < 3; i++) { float t = 2.7f + 0.22f * i; At(t, "aimed fire", () => { TopUp(); dw.OnFirePressed(); }); At(t + 0.06f, "aimed fire", () => dw.OnFireReleased()); }
            At(3.6f, "unaim", () => dw.OnAimReleased());
            At(4.2f, "reload", () => { var m = dw.SelectedMag; if (m != null) m.AmmoCount = 1; fps.OnReload(); });
            At(8.6f, "check ammo", () => fps.OnCheckAmmo());
            // 体の動き：歩く → 走る → スライディング → しゃがむ → 伏せる → ジャンプ → ナイフ → 射撃モード切替 → 持ち替え（しまう・出す）
            var startPos = fps.transform.position; var startRot = fps.transform.rotation;
            At(10.8f, "walk", () => _walk = true);
            At(12.0f, "sprint", () => mv.SetSprintRequested(true));
            At(14.0f, "slide", () => mv.OnSlide());
            At(15.4f, "stop", () => { mv.SetSprintRequested(false); _walk = false; });
            At(15.8f, "crouch", () => { if (mv.PoseState.ToString() != "Crouching") mv.OnCrouch(); });
            At(17.0f, "stand", StandUp);
            At(17.8f, "prone", () => mv.OnProne());
            At(19.6f, "stand up", StandUp);
            At(20.5f, "stand up", StandUp);
            At(21.4f, "stand up", StandUp);
            At(22.2f, "jump", () => mv.OnJump());
            At(23.4f, "knife", () => fps.OnKnifeAttack());
            At(24.8f, "fire mode", () => fps.OnChangeFireMode());
            int mine = -1, other = -1;
            var inst = fps.InstantiatedWeapons;
            for (int i = 0; i < inst.Count; i++) { if (inst[i] == null) continue; if (inst[i].gameObject == dw.gameObject) mine = i; else if (other < 0) other = i; }
            if (mine >= 0 && other >= 0)
            {
                At(25.4f, "holster", () => fps.StartWeaponChange(other));
                At(26.8f, "draw", () => fps.StartWeaponChange(mine));
            }
            At(29.0f, "idle end", () =>
            {
                _walk = false; mv.SetSprintRequested(false); StandUp();
                var cc = fps.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;
                fps.transform.SetPositionAndRotation(startPos, startRot);
                if (cc != null) cc.enabled = true;
                Main.Log($"[anim] [{id}] back to the start; state={mv.MovementState} pose={mv.PoseState}");
            });
        }

        static readonly string[] MuzzleParts = { "suppressor", "psup", "p_compensator", "akbrake", "arbrake" };

        sealed class Contact
        {
            public string Host; public float Gap; public bool Behind;            // 止まっているときの部品との隙間・部品の後ろ（銃口の部品を支える側）か
            public Transform T; public Vector3[] Local, RestP, RestR, Grid;       // 固い描画物：部品の箱の角（描画物・部品・武器の座標）、描画物の形の格子点
            public SkinnedMeshRenderer Sk; public int[] Idx; public Vector3[] RestV, RestVR;   // 骨で動く描画物：近い頂点（部品・武器の座標）
            public float Max, Body, Pen0 = -1f, Pen; public string MaxPhase = "", PenPhase = "";
            public bool Moving => Body > BodyMoves;
        }

        sealed class Part
        {
            public string Name; public Transform T; public Bounds Box; public List<Contact> Contacts = new();
            public List<(Renderer r, Bounds b)> Shapes = new();
            public float Hand; public string HandBone = "", HandPhase = "";
            public float ShotAt = -9f, ShotHand, ShotClip, ShotFloat;
            public Dictionary<string, string> ShotFile = new();   // 種類ごとに、いちばんひどい瞬間の 1 枚だけ残す
            public bool Muzzle;

            // 部品の形（描画物ごとのメッシュの箱）の中への深さ（m）
            public float Depth(Vector3 world)
            {
                float best = 0f;
                foreach (var (r, b) in Shapes)
                {
                    if (r == null) continue;
                    var q = r.transform.InverseTransformPoint(world);
                    if (!b.Contains(q)) continue;
                    var sc = r.transform.lossyScale;
                    float d = Mathf.Min(Mathf.Min((q.x - b.min.x) * sc.x, (b.max.x - q.x) * sc.x), Mathf.Min(Mathf.Min((q.y - b.min.y) * sc.y, (b.max.y - q.y) * sc.y), Mathf.Min((q.z - b.min.z) * sc.z, (b.max.z - q.z) * sc.z)));
                    if (d > best) best = d;
                }
                return best;
            }
        }

        static Vector3[] GridOf(Bounds b, int n)
        {
            var list = new List<Vector3>();
            for (int x = 0; x < n; x++) for (int y = 0; y < n; y++) for (int z = 0; z < n; z++)
                list.Add(b.min + Vector3.Scale(b.size, new Vector3(x / (n - 1f), y / (n - 1f), z / (n - 1f))));
            return list.ToArray();
        }

        sealed class Sampler
        {
            readonly DemoWeapon _dw; readonly int _id, _k; readonly string _name, _label;
            readonly List<Part> _parts = new();
            readonly List<Transform> _fingers = new();
            readonly Dictionary<SkinnedMeshRenderer, Mesh> _bake = new();
            readonly Dictionary<string, HashSet<string>> _seen = new();
            readonly int _mask;
            int _frames;

            public Sampler(DemoWeapon dw, int id, string name, string label, int k)
            {
                _dw = dw; _id = id; _name = name; _label = label; _k = k;
                var root = dw.transform;
                var all = root.GetComponentsInChildren<Transform>(true);
                bool InMod(Transform t) { for (; t != null && t != root; t = t.parent) if (t.name.StartsWith(Mount.Prefix)) return true; return false; }
                bool Skip(string n) => n.StartsWith(Mount.Prefix + "slot_") || n.StartsWith(Mount.Prefix + "bone_") || n == Mount.Prefix + "noflash"
                    || n.StartsWith(Mount.Prefix + "cut_b");   // 銃の本体（MuzzleCut が骨ごとに分けた Type 89 の本体。部品ではない）
                // 付けた部品（いちばん外側の FPC_ の物）
                foreach (var t in all)
                {
                    if (!t.name.StartsWith(Mount.Prefix) || Skip(t.name) || InMod(t.parent)) continue;
                    var rs = t.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && SightLab.IsMesh(r)).ToList();
                    if (rs.Count == 0) continue;
                    var p = new Part { Name = t.name.Substring(Mount.Prefix.Length), T = t };
                    p.Muzzle = MuzzleParts.Contains(p.Name);
                    foreach (var r in rs)
                    {
                        Bounds b;
                        if (r.TryCast<SkinnedMeshRenderer>() is { } sk) b = sk.localBounds;
                        else { var mf = r.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) continue; b = mf.sharedMesh.bounds; }
                        p.Shapes.Add((r, b));
                    }
                    _parts.Add(p);
                }
                // 武器の描画物（付けた部品は除く）
                var hosts = root.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && SightLab.IsMesh(r) && !InMod(r.transform)).ToList();
                foreach (var p in _parts)
                {
                    var box = new Bounds(); bool first = true;
                    foreach (var (r, _) in p.Shapes) { var lb = SightLab.LocalBounds(r, p.T); if (first) { box = lb; first = false; } else box.Encapsulate(lb); }
                    p.Box = box;
                    var near = box; near.Expand(Near * 2f);
                    var corners = new Vector3[8];
                    for (int i = 0; i < 8; i++) corners[i] = box.center + Vector3.Scale(box.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var boxGrid = GridOf(box, 5);
                    foreach (var h in hosts)
                    {
                        var path = SightLab.RelPath(h.transform, root);
                        if (h.TryCast<SkinnedMeshRenderer>() is { } sk)
                        {
                            var vs = Bake(sk);
                            var idx = new List<int>(); var rest = new List<Vector3>(); var restR = new List<Vector3>();
                            float gap = 9f; var sum = Vector3.zero;
                            for (int i = 0; i < vs.Length; i++)
                            {
                                var wpos = sk.transform.TransformPoint(vs[i]);
                                var q = p.T.InverseTransformPoint(wpos);
                                if (!near.Contains(q)) continue;
                                idx.Add(i); rest.Add(q); restR.Add(root.InverseTransformPoint(wpos)); sum += q;
                                gap = Mathf.Min(gap, (q - box.ClosestPoint(q)).magnitude);
                            }
                            // 多すぎるときは間引く
                            if (idx.Count > 400) { int step = idx.Count / 400 + 1; idx = idx.Where((_, j) => j % step == 0).ToList(); rest = rest.Where((_, j) => j % step == 0).ToList(); restR = restR.Where((_, j) => j % step == 0).ToList(); }
                            if (idx.Count > 0)
                                p.Contacts.Add(new Contact { Host = path + $" ({idx.Count} verts)", Gap = gap, Behind = sum.z / Math.Max(1, rest.Count) < box.min.z + 0.002f, Sk = sk, Idx = idx.ToArray(), RestV = rest.ToArray(), RestVR = restR.ToArray() });
                        }
                        else
                        {
                            var hb = SightLab.LocalBounds(h, p.T);
                            if (!hb.Intersects(near)) continue;
                            var mf = h.GetComponent<MeshFilter>();
                            if (mf == null || mf.sharedMesh == null) continue;
                            var mb = mf.sharedMesh.bounds; var sc = h.transform.lossyScale;
                            // 隙間：部品の箱の格子点から、描画物のメッシュの箱までの最短
                            float gap = 9f;
                            foreach (var g in boxGrid)
                            {
                                var q = h.transform.InverseTransformPoint(p.T.TransformPoint(g));
                                var dq = q - mb.ClosestPoint(q);
                                gap = Mathf.Min(gap, Vector3.Scale(dq, sc).magnitude);
                            }
                            var hc = p.T.InverseTransformPoint(h.transform.TransformPoint(mb.center));
                            p.Contacts.Add(new Contact
                            {
                                Host = path, Gap = gap, Behind = hc.z < box.min.z + 0.002f, T = h.transform, RestP = corners, Grid = GridOf(mb, 4),
                                Local = corners.Select(c => h.transform.InverseTransformPoint(p.T.TransformPoint(c))).ToArray(),
                                RestR = corners.Select(c => root.InverseTransformPoint(p.T.TransformPoint(c))).ToArray(),
                            });
                        }
                    }
                }
                // 手の指の骨
                foreach (var t in Refs.Fps.GetComponentsInChildren<Transform>(true))
                {
                    var n = t.name;
                    if ((n.StartsWith("Left_") || n.StartsWith("Right_")) && (n.Contains("Hand") || n.Contains("Thumb") || n.Contains("Index") || n.Contains("Middle") || n.Contains("Ring") || n.Contains("Little") || n.Contains("Pinky")))
                        _fingers.Add(t);
                }
                // 撮影する層：武器と、武器の近くの腕
                int mask = 0;
                foreach (var r in root.GetComponentsInChildren<Renderer>(false)) mask |= 1 << r.gameObject.layer;
                foreach (var r in Refs.Fps.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                    if (r.enabled && (r.bounds.center - root.position).magnitude < 1.2f) mask |= 1 << r.gameObject.layer;
                _mask = mask;
                Main.Log($"[anim] [{_id}] {_name} [{_label}]: parts={string.Join(",", _parts.Select(p => $"{p.Name}(hosts {p.Contacts.Count})"))} fingers={_fingers.Count} mask={mask}");
            }

            Vector3[] Bake(SkinnedMeshRenderer sk)
            {
                if (!_bake.TryGetValue(sk, out var m)) { m = new Mesh(); _bake[sk] = m; }
                sk.BakeMesh(m, false);
                return m.vertices;
            }

            // 武器と腕だけを、部品の左右から明るく撮る（1 枚に並べる）
            void Shot(Part p, string kind)
            {
                p.ShotAt = Time.unscaledTime;
                var root = _dw.transform;
                var c = p.T.TransformPoint(p.Box.center);
                var sheet = new Texture2D(1600, 600, TextureFormat.RGB24, false);
                int x = 0;
                foreach (var side in new[] { root.right, -root.right })
                {
                    var dir = (side + root.up * 0.25f).normalized;
                    var camGo = new GameObject("FPC_AnimCam"); var cam = camGo.AddComponent<Camera>();
                    var lg = new GameObject("FPC_AnimLight"); var l = lg.AddComponent<Light>();
                    l.type = LightType.Directional; l.intensity = 1.6f; l.cullingMask = _mask;
                    camGo.transform.position = c + dir * 0.55f;   // 手前（体・腕）は近くを切って写さない
                    camGo.transform.rotation = Quaternion.LookRotation(-dir, root.up);
                    lg.transform.rotation = Quaternion.LookRotation(-dir - root.up * 0.4f, root.up);
                    cam.fieldOfView = 32f; cam.nearClipPlane = 0.40f; cam.farClipPlane = 3f; cam.cullingMask = _mask;
                    cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = SightLab.Bg;
                    var rt = new RenderTexture(800, 600, 24); cam.targetTexture = rt; cam.aspect = 800f / 600f;
                    cam.Render();
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    sheet.ReadPixels(new Rect(0, 0, 800, 600), x, 0);
                    RenderTexture.active = prev; cam.targetTexture = null;
                    UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(camGo); UnityEngine.Object.Destroy(lg);
                    x += 800;
                }
                sheet.Apply();
                if (p.ShotFile.TryGetValue(kind, out var old)) try { File.Delete(old); } catch { }
                var file = Path.Combine(ShotDir, $"anim_{_id:00}_{_k}_{p.Name}_{kind}_{_phase.Replace(' ', '-')}.png");
                File.WriteAllBytes(file, ImageConversion.EncodeToPNG(sheet));
                p.ShotFile[kind] = file;
                UnityEngine.Object.Destroy(sheet);
            }

            public void Sample()
            {
                if (_dw == null || Active() != _dw || !_dw.gameObject.activeInHierarchy) return;   // しまっている間は測らない
                _frames++;
                if (Refs.Fps?._movementComponent is { } mv)
                {
                    var st = $"{mv.MovementState}/{mv.PoseState}";
                    if (!_seen.TryGetValue(_phase, out var set)) _seen[_phase] = set = new HashSet<string>();
                    set.Add(st);
                }
                var root = _dw.transform;
                var baked = new Dictionary<SkinnedMeshRenderer, Vector3[]>();
                foreach (var p in _parts)
                {
                    if (p.T == null) continue;
                    foreach (var c in p.Contacts)
                    {
                        float d, body, pen;
                        if (c.Sk != null)
                        {
                            if (!baked.TryGetValue(c.Sk, out var vs)) { vs = Bake(c.Sk); baked[c.Sk] = vs; }
                            var ds = new float[c.Idx.Length]; var bs = new float[c.Idx.Length]; var ps = new float[c.Idx.Length];
                            for (int i = 0; i < c.Idx.Length; i++)
                            {
                                var wpos = c.Sk.transform.TransformPoint(vs[c.Idx[i]]);
                                ds[i] = (p.T.InverseTransformPoint(wpos) - c.RestV[i]).magnitude;
                                bs[i] = (root.InverseTransformPoint(wpos) - c.RestVR[i]).magnitude;
                                ps[i] = p.Depth(wpos);
                            }
                            Array.Sort(ds); Array.Sort(bs); Array.Sort(ps);
                            d = ds[ds.Length / 2]; body = bs[bs.Length / 2];   // 半分以上の頂点がずれたら（一部だけ動く物は除く）
                            pen = ps[Math.Max(0, ps.Length - 5)];              // 5 番目に深い頂点（1 点だけの誤差は除く）
                        }
                        else
                        {
                            if (c.T == null) continue;
                            d = 0f; body = 0f; pen = 0f;
                            for (int i = 0; i < 8; i++)
                            {
                                var wpos = c.T.TransformPoint(c.Local[i]);
                                d = Mathf.Max(d, (p.T.InverseTransformPoint(wpos) - c.RestP[i]).magnitude);
                                body = Mathf.Max(body, (root.InverseTransformPoint(wpos) - c.RestR[i]).magnitude);
                            }
                            foreach (var g in c.Grid) pen = Mathf.Max(pen, p.Depth(c.T.TransformPoint(g)));
                        }
                        if (c.Pen0 < 0f) c.Pen0 = pen;
                        if (body > c.Body) c.Body = body;
                        if (d > c.Max) { c.Max = d; c.MaxPhase = _phase; }
                        if (pen - c.Pen0 > c.Pen) { c.Pen = pen - c.Pen0; c.PenPhase = _phase; }
                        // めり込み：もともと動く物が、止まっているときより深く部品の形に入った
                        if (c.Moving && c.Pen > ClipWarn && c.Pen > p.ShotClip + 0.002f && Time.unscaledTime - p.ShotAt > 0.3f) { p.ShotClip = c.Pen; Shot(p, "clip"); }
                    }
                    // 浮き：銃口の部品を後ろで支える物が全部動いて離れた
                    if (p.Muzzle)
                    {
                        var sup = p.Contacts.Where(c => c.Behind && c.Gap < SupportGap).ToList();
                        if (sup.Count > 0 && sup.All(c => c.Moving))
                        {
                            float f = sup.Min(c => c.Max);
                            if (f > MovingWarn && f > p.ShotFloat + 0.002f && Time.unscaledTime - p.ShotAt > 0.3f) { p.ShotFloat = f; Shot(p, "float"); }
                        }
                    }
                    // 手のめり込み（グリップは握る物なので除く）
                    if (p.Name == "agrip" || p.Name == "vgrip") continue;
                    foreach (var f in _fingers)
                    {
                        float depth = p.Depth(f.position);
                        if (depth > p.Hand) { p.Hand = depth; p.HandBone = f.name; p.HandPhase = _phase; }
                        if (depth > HandWarn && depth > p.ShotHand + 0.002f && Time.unscaledTime - p.ShotAt > 0.3f) { p.ShotHand = depth; Shot(p, "hand"); }
                    }
                }
            }

            public void Report()
            {
                Main.Log($"[anim] [{_id}] {_name} [{_label}]: {_frames} frames; states: {string.Join(" ", _seen.Select(kv => $"{kv.Key}={string.Join(",", kv.Value)}"))}");
                Check($"{_name} [{_label}]: sampled the animations", _frames > 200);
                foreach (var p in _parts)
                {
                    var stat = p.Contacts.Where(c => !c.Moving).OrderByDescending(c => c.Max).ToList();
                    var mov = p.Contacts.Where(c => c.Moving).ToList();
                    var drift = stat.Where(c => c.Max > DriftWarn).ToList();
                    var clip = mov.Where(c => c.Pen > ClipWarn).OrderByDescending(c => c.Pen).ToList();
                    var sup = p.Muzzle ? p.Contacts.Where(c => c.Behind && c.Gap < SupportGap).ToList() : new List<Contact>();
                    bool floats = sup.Count > 0 && sup.All(c => c.Moving) && sup.Min(c => c.Max) > MovingWarn;
                    Main.Log($"[anim] [{_id}] {_name} {p.Name}: drift {(stat.Count > 0 ? stat[0].Max * 1000f : 0f):F1}mm; moving parts: {string.Join(", ", mov.Select(c => $"'{c.Host}' moves {c.Body * 1000f:F0}mm gap {c.Gap * 1000f:F1}mm in +{c.Pen * 1000f:F1}mm"))}" +
                             (p.Muzzle ? $"; support: {string.Join(", ", sup.Select(c => $"'{c.Host}' {(c.Moving ? $"moves {c.Max * 1000f:F0}mm" : "fixed")}"))}" : "") +
                             $"; hand {p.Hand * 1000f:F1}mm ({p.HandBone} during {p.HandPhase})");
                    foreach (var c in drift) Main.Log($"[anim]   DRIFT [{_id}] {_name} {p.Name}: {c.Max * 1000f:F1}mm vs '{c.Host}' (moves with the gun) during {c.MaxPhase}");
                    foreach (var c in clip) Main.Log($"[anim]   CLIP [{_id}] {_name} {p.Name}: '{c.Host}' goes {c.Pen * 1000f:F1}mm into it during {c.PenPhase}");
                    if (floats) Main.Log($"[anim]   FLOAT [{_id}] {_name} {p.Name}: everything behind it moves away ({string.Join(", ", sup.Select(c => $"'{c.Host}' {c.Max * 1000f:F0}mm during {c.MaxPhase}"))})");
                    Check($"{_name} {p.Name}: stays on the gun during the animations", drift.Count == 0);
                    Check($"{_name} {p.Name}: moving parts of the gun do not go into it (max {(clip.Count > 0 ? clip[0].Pen * 1000f : 0f):F1}mm)", clip.Count == 0);
                    if (p.Muzzle) Check($"{_name} {p.Name}: the barrel stays behind it", !floats);
                    Check($"{_name} {p.Name}: hands do not go into it (max {p.Hand * 1000f:F1}mm)", p.Hand <= HandWarn);
                }
                foreach (var m in _bake.Values) UnityEngine.Object.Destroy(m);
            }
        }
    }
}
