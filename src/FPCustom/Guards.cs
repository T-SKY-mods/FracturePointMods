using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System;
using System.Collections.Generic;
using System.IO;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // グリップを付けるときのレール。ハンドガードの下にレールが無い武器だけ：
    //   木のハンドガードの武器（AK-74・AKMS・AKM・AKSU・M16）：レール付きのハンドガードに替える（木の部分を消す／木を除いた形に差し替える）
    //   樹脂のハンドガードの武器（QBZ・VAL・Type 89）：ハンドガードの下にレールをボルト留め
    // モデルは FPCustom の models（無料モデル、CC BY 4.0。assets\custom_src と CREDITS 参照）
    public static class Guards
    {
        public sealed class Swap { public string Renderer, Mesh, AltMesh; }   // AltMesh：サイトで既に形が差し替わっているとき（M16 のハンドルなし）

        public sealed class Guard
        {
            public string Model;
            public Vector3 Min, Max;           // 置く範囲（武器の根元の座標）。モデルをこの箱に合わせて伸縮する
            public string[] Hide = Array.Empty<string>();
            public Swap[] Swaps = Array.Empty<Swap>();
            public Vector2? UpZ;               // 上の部品（B-19）の前後の範囲。元の木の上のハンドガード（ガスチューブのカバー）の範囲に合わせて、機関部との間を空けない
        }

        // B-10＋B-19 は上下 2 つの部品に分けた模型（b10_low・b10_up。y 0.074 で切った物）も持ち、上の部品だけ前後を UpZ に合わせる
        const string SplitLow = "_low", SplitUp = "_up";

        // レール付きハンドガード（下面がレール）
        public static readonly Dictionary<int, Guard> Replace = new()
        {
            // AK-74：木の下のハンドガード・上のハンドガード（ガスチューブのカバー）を除いた形に差し替えて、Zenitco B-10（下）＋B-19（上）（AKMS と同じ）
            [0] = new Guard { Model = "b10", Min = new Vector3(-0.022f, -0.035f, 0.064f), Max = new Vector3(0.022f, 0.048f, 0.221f), UpZ = new(0.1015f, 0.221f),
                              Swaps = new[] { new Swap { Renderer = "AKS-74N/ak74m/GameObject/handle_low", Mesh = "ak74_noguard.fpm" } } },
            // AKMS：木の上下のハンドガードを除いた形に差し替えて、Zenitco B-10（下）＋B-19（上）
            [4] = new Guard { Model = "b10", Min = new Vector3(-0.018f, -0.034f, 0.061f), Max = new Vector3(0.018f, 0.046f, 0.224f), UpZ = new(0.110f, 0.224f),
                              Swaps = new[] { new Swap { Renderer = "AKS-74N/rusty_aks/blade", Mesh = "akms_noguard.fpm" } } },
            // AKM：木の上下のハンドガードは別の部品なので消す
            [12] = new Guard { Model = "b10", Min = new Vector3(-0.021f, -0.030f, 0.072f), Max = new Vector3(0.021f, 0.052f, 0.237f), UpZ = new(0.123f, 0.237f),
                               Hide = new[] { "AKM_low7/upperReceiver_low/upperReceiver_j_low", "AKM_low7/upperReceiver_low/upperReceiver_i_low" } },
            // AKSU：木のハンドガード（上下一体）を消す
            [11] = new Guard { Model = "b10", Min = new Vector3(-0.020f, -0.030f, 0.073f), Max = new Vector3(0.020f, 0.047f, 0.200f), UpZ = new(0.075f, 0.200f),
                               Hide = new[] { "1/handguard_aks74u_izhmash_aks74u_std_wood_LOD0_0" } },
            // M16：木のハンドガードを除いた形に差し替えて、Daniel Defense RIS II
            [10] = new Guard { Model = "ris2", Min = new Vector3(-0.026f, -0.021f, 0.086f), Max = new Vector3(0.026f, 0.034f, 0.357f),
                               Swaps = new[] { new Swap { Renderer = "M16/M16", Mesh = "m16_noguard.fpm", AltMesh = "m16_flattop_noguard.fpm" } } },
        };

        // ボルト留めのレール（Hexagon のレール。上下逆さにしてハンドガードの下面に当てる）
        public static readonly HashSet<int> BoltOn = new() { 3, 20, 17, 19 };   // QBZ・Type 89・Swiss 751・Hunter-12

        // レール付きの台を付ける武器：四角い台（銃の材質）の上下左右に Hexagon のレール（実物のクアッドレールのブロック）
        //   VAL：サプレッサーの根元（樹脂のハンドガードのすぐ前 z 0.165〜0.245）。台はサプレッサーの管（中心の高さ 0.006・半径 0.020）を包む
        public sealed class Block { public string Material; public Vector3 Min, Max; }   // Material：台の材質を借りる描画物
        public static readonly Dictionary<int, Block> Blocks = new()
        {
            [14] = new Block { Material = "uploads_files_5248530_ASVAL/1_low", Min = new Vector3(-0.023f, -0.0166f, 0.165f), Max = new Vector3(0.023f, 0.0294f, 0.247f) },
        };
        const float BlockEmbed = 0.002f;   // レールを台にめり込ませる深さ

        // 同じ形の別の武器の部品に替える（ゲームの部品を複製）：MPS-5 → Viper M5A3 のレール付きハンドガード。
        //   Viper の本体のメッシュは MPS-5 のメッシュからハンドガードだけを除いた物（頂点 18085 - 599 = 17486）なので、本体のメッシュを差し替えてハンドガードを複製する
        public sealed class Donated { public int Weapon; public string Skin, DonorSkin; public string[] Parts = Array.Empty<string>(); }
        public static readonly Dictionary<int, Donated> FromWeapon = new()
        {
            [27] = new Donated { Weapon = 28, Skin = "mp5/Magazine_release_button_1", DonorSkin = "mp5 custom/Magazine_release_button_1", Parts = new[] { "mp5 custom/Armature/mp handguard" } },
        };

        // 足したレールの下にグリップが付く武器で、グリップが下がる量
        public static float RailDrop(int id) => BoltOn.Contains(id) && File.Exists(Path.Combine(ModelDir, RailModel, "model.json"))
            ? ModelLoader.Load(Path.Combine(ModelDir, RailModel)).Bounds.size.y - RailHook : 0f;
        public const string RailModel = "hex_rail";
        public const float RailHook = 0.004f;   // グリップの留め具がレールにかかる深さ

        public static bool Needs(int id) => Replace.ContainsKey(id) || BoltOn.Contains(id) || FromWeapon.ContainsKey(id) || Blocks.ContainsKey(id);

        public static string ModelDir => Path.Combine(Main.Dir, "models");
        public static bool Available(int id)
        {
            string m = Replace.TryGetValue(id, out var g) ? g.Model : BoltOn.Contains(id) || Blocks.ContainsKey(id) ? RailModel : null;
            if (m == null) return true;
            if (!File.Exists(Path.Combine(ModelDir, m, "model.json"))) return false;
            if (g != null) foreach (var s in g.Swaps) if (!File.Exists(Path.Combine(ModelDir, s.Mesh)) || (s.AltMesh != null && !File.Exists(Path.Combine(ModelDir, s.AltMesh)))) return false;
            return true;
        }

        static readonly Dictionary<string, Mesh> _meshes = new();
        static Mesh LoadMesh(string name)
        {
            if (_meshes.TryGetValue(name, out var m) && m != null) return m;
            m = ModelLoader.LoadMesh(Path.Combine(ModelDir, name));
            return _meshes[name] = m;
        }
        static bool Ours(Mesh m) { foreach (var x in _meshes.Values) if (x != null && m != null && x.Pointer == m.Pointer) return true; return false; }

        // ゲームのままに戻す（消した部品・差し替えた形）。サイトの差し替え（Optics.Restore）の後に呼ぶ
        public static void Restore(GameObject inst, GameObject prefab, int id)
        {
            if (FromWeapon.TryGetValue(id, out var dn))
            {
                var sk = inst.transform.Find(dn.Skin)?.GetComponent<SkinnedMeshRenderer>(); var psk = prefab.transform.Find(dn.Skin)?.GetComponent<SkinnedMeshRenderer>();
                if (sk != null && psk != null && sk.sharedMesh != psk.sharedMesh)
                {
                    // ゲームの骨（複製された武器の中の同じ名前の骨）に戻す
                    var pb = psk.bones; var bones = new Il2CppReferenceArray<Transform>(pb.Length);
                    for (int i = 0; i < pb.Length; i++) bones[i] = pb[i] == null ? null : (FindDeep(inst.transform, pb[i].name) ?? sk.rootBone);
                    sk.sharedMesh = psk.sharedMesh;
                    sk.bones = bones;
                    sk.localBounds = psk.localBounds;
                }
                return;
            }
            if (!Replace.TryGetValue(id, out var g)) return;
            var root = inst.transform;
            foreach (var h in g.Hide)
            {
                var r = root.Find(h)?.GetComponent<Renderer>(); var pr = prefab.transform.Find(h)?.GetComponent<Renderer>();
                if (r != null && pr != null) r.enabled = pr.enabled;
            }
            foreach (var s in g.Swaps)
            {
                var mf = root.Find(s.Renderer)?.GetComponent<MeshFilter>(); var pmf = prefab.transform.Find(s.Renderer)?.GetComponent<MeshFilter>();
                if (mf == null || pmf == null) continue;
                if (Ours(mf.sharedMesh)) mf.sharedMesh = pmf.sharedMesh;   // サイトの差し替え（M16）は Optics.Restore が先に戻している
            }
        }

        // グリップ（FPC_vgrip）を付けたあと：レール／レール付きハンドガードを付けて、グリップをその下面に合わせる
        public static void Apply(GameObject inst, GameObject prefab, int id, GameObject grip)
        {
            if (grip == null || !Needs(id)) return;
            if (grip.name == Mount.Prefix + "bipod" && Grips.BipodOwnRail.Contains(id)) return;   // 元からあるレールに直接付ける
            if (!Available(id)) { Main.Log($"[guard] models missing for {Tracker.NameOf(id)}"); return; }
            var root = inst.transform;
            int layer = inst.layer;
            if (FromWeapon.TryGetValue(id, out var dn)) { ApplyDonated(root, prefab, dn, layer); return; }   // グリップの位置は Grips.Manual で合わせてある
            var gb = Lab.MeshBounds(grip.transform, root);
            float bottom;   // レールの下面
            GameObject rail = null;
            if (Blocks.TryGetValue(id, out var bk))
            {
                if (!PlaceBlock(root, bk, layer, out bottom)) return;
            }
            else if (Replace.TryGetValue(id, out var g))
            {
                foreach (var h in g.Hide) { var r = root.Find(h)?.GetComponent<Renderer>(); if (r != null) r.enabled = false; }
                foreach (var s in g.Swaps)
                {
                    var mf = root.Find(s.Renderer)?.GetComponent<MeshFilter>(); var pmf = prefab.transform.Find(s.Renderer)?.GetComponent<MeshFilter>();
                    if (mf == null || pmf == null) continue;
                    bool alt = s.AltMesh != null && mf.sharedMesh != null && mf.sharedMesh.Pointer != pmf.sharedMesh.Pointer && !Ours(mf.sharedMesh);
                    mf.sharedMesh = LoadMesh(alt ? s.AltMesh : s.Mesh);
                }
                var go = SpawnGuard(g, root, layer);
                if (go == null) return;
                bottom = g.Min.y;
            }
            else
            {
                var size = ModelLoader.Load(Path.Combine(ModelDir, RailModel)).Bounds.size;
                // 今のグリップの上面＝ハンドガードの下面（Grips.Manual の武器は測った下面。グリップは既にレールの厚みの分だけ下げて置いてある）
                float top = Grips.Manual.TryGetValue(id, out var ms) ? ms.Y : gb.max.y;
                var min = new Vector3(-size.x / 2f, top - size.y, gb.center.z - size.z / 2f);
                var max = new Vector3(size.x / 2f, top, gb.center.z + size.z / 2f);
                // バイポッド：短いレールを、ハンドガードの下面（測った高さ）に、前の端をハンドガードの前の端に合わせて付ける（Grips.BipodRail）
                if (grip.name == Mount.Prefix + "bipod" && Grips.BipodRail(id) is { } br)
                {
                    min = new Vector3(-size.x / 2f, br.x - size.y, br.y - Grips.BipodRailLen);
                    max = new Vector3(size.x / 2f, br.x, br.y);
                }
                rail = Spawn(RailModel, root, layer, "rail", Quaternion.Euler(0f, 0f, 180f), min, max);
                if (rail == null) return;
                bottom = min.y;
            }
            grip.transform.position += root.TransformVector(new Vector3(0f, bottom + RailHook - gb.max.y, 0f));
            // 下面が傾いている武器（MPS-5）：レールとグリップを、グリップの上面の中心を軸に下面の傾きに合わせて回す
            float slope = Grips.Slope(id);
            if (slope != 0f && rail != null)
            {
                var pivot = root.TransformPoint(new Vector3(0f, gb.max.y, gb.center.z));
                var axis = root.TransformDirection(Vector3.right);
                float deg = -Mathf.Atan(slope) * Mathf.Rad2Deg;   // X まわりの正は前下がり
                rail.transform.RotateAround(pivot, axis, deg);
                grip.transform.RotateAround(pivot, axis, deg);
            }
        }

        static void ApplyDonated(Transform root, GameObject prefab, Donated dn, int layer)
        {
            var dp = Tracker.ItemById(dn.Weapon)?.Weapon?.gameObject;
            if (dp == null) return;
            var psk = prefab.transform.Find(dn.Skin)?.GetComponent<SkinnedMeshRenderer>();
            var sk = root.Find(dn.Skin)?.GetComponent<SkinnedMeshRenderer>();
            var dsk = dp.transform.Find(dn.DonorSkin)?.GetComponent<SkinnedMeshRenderer>();
            if (sk == null || dsk?.sharedMesh == null) { Main.Log("[guard] donated mesh not found"); return; }
            // 骨は名前で対応させる（Viper は骨が多い。無い骨は根元の骨に）
            var dbones = dsk.bones;
            var bones = new Il2CppReferenceArray<Transform>(dbones.Length);
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < dbones.Length; i++)
            {
                var n = dbones[i]?.name;
                Transform t = null;
                foreach (var b in sk.bones) if (b != null && b.name == n) { t = b; break; }
                if (t == null && n != null) t = FindDeep(root, n);
                t ??= sk.rootBone ?? root;
                // 骨の置き方（武器の根元から見た行列）が複製元と違う分を、子の補助の骨で打ち消す（MPS-5 の骨の動きには付いていく）
                // プレハブの同じ骨は道筋で探す（名前だけだと、別の Root（中の AK の骨組み）を拾う）
                var pt = psk == null ? null : PathIn(t, root) is string tp ? (tp.Length == 0 ? prefab.transform : prefab.transform.Find(tp)) : null;
                var md = dp.transform.worldToLocalMatrix * dbones[i].localToWorldMatrix;
                var mo = pt != null ? prefab.transform.worldToLocalMatrix * pt.localToWorldMatrix : root.worldToLocalMatrix * t.localToWorldMatrix;
                var l = mo.inverse * md;
                var h = new GameObject(Mount.Prefix + "bone_" + n).transform;
                h.SetParent(t, false);
                h.localPosition = l.GetColumn(3); h.localRotation = l.rotation; h.localScale = l.lossyScale;
                bones[i] = h;
                names.Append($"{n}->{t.name} d=({l.GetColumn(3).x:F3},{l.GetColumn(3).y:F3},{l.GetColumn(3).z:F3}) s={l.lossyScale.x:F3} ");
            }
            if (_loggedBones.Add(dn.Skin)) Main.Log($"[guard] donated bones: {names}");
            sk.sharedMesh = dsk.sharedMesh;
            sk.bones = bones;
            // メッシュを替えると描画の範囲が Viper の物になり、MPS-5 の位置から外れて写らない（カスタマイズ画面）。本体はほぼ同じ形なので MPS-5 の範囲を使う
            if (psk != null) sk.localBounds = psk.localBounds;
            foreach (var path in dn.Parts)
            {
                var src = dp.transform.Find(path);
                if (src == null) { Main.Log($"[guard] donated part not found: {path}"); continue; }
                var go = UnityEngine.Object.Instantiate(src.gameObject, root, false);
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
                go.name = Mount.Prefix + "guard";
                // 複製元の武器の根元から見た位置・向き・大きさのまま
                go.transform.localPosition = dp.transform.InverseTransformPoint(src.position);
                go.transform.localRotation = Quaternion.Inverse(dp.transform.rotation) * src.rotation;
                go.transform.localScale = Div(src.lossyScale, dp.transform.lossyScale);
                go.SetActive(true);
                SetLayer(go.transform, layer);
                if (_loggedBones.Add(path)) { var gb = Lab.MeshBounds(go.transform, root); Main.Log($"[guard] donated part {path}: min={gb.min} max={gb.max} scale={go.transform.localScale}"); }
            }
        }

        // 台とその 4 面のレールを置く。bottom：下のレールの下面
        static bool PlaceBlock(Transform root, Block bk, int layer, out float bottom)
        {
            bottom = bk.Min.y;
            var src = root.Find(bk.Material)?.GetComponent<Renderer>();
            if (src == null) { Main.Log($"[guard] block: {bk.Material} not found"); return false; }
            var core = new GameObject(Mount.Prefix + "block");
            core.transform.SetParent(root, false);
            core.layer = layer;
            core.AddComponent<MeshFilter>().sharedMesh = BoxMesh();
            core.AddComponent<MeshRenderer>().sharedMaterial = src.sharedMaterial;
            core.transform.localPosition = (bk.Min + bk.Max) / 2f;
            core.transform.localScale = bk.Max - bk.Min;
            Skin.ApplyPart(core, root, Skin.Role.Body);
            var size = ModelLoader.Load(Path.Combine(ModelDir, RailModel)).Bounds.size;
            float w = size.x, h = size.y, e = BlockEmbed;
            float z0 = bk.Min.z + 0.002f, z1 = bk.Max.z - 0.002f;
            float cx = (bk.Min.x + bk.Max.x) / 2f, cy = (bk.Min.y + bk.Max.y) / 2f;
            // 模型のレールは上向き。上・下（180 度）・右（-90 度で +X 向き）・左（+90 度）
            Spawn(RailModel, root, layer, "rail_top", Quaternion.identity, new Vector3(cx - w / 2f, bk.Max.y - e, z0), new Vector3(cx + w / 2f, bk.Max.y - e + h, z1));
            Spawn(RailModel, root, layer, "rail", Quaternion.Euler(0f, 0f, 180f), new Vector3(cx - w / 2f, bk.Min.y + e - h, z0), new Vector3(cx + w / 2f, bk.Min.y + e, z1));
            Spawn(RailModel, root, layer, "rail_right", Quaternion.Euler(0f, 0f, -90f), new Vector3(bk.Max.x - e, cy - w / 2f, z0), new Vector3(bk.Max.x - e + h, cy + w / 2f, z1));
            Spawn(RailModel, root, layer, "rail_left", Quaternion.Euler(0f, 0f, 90f), new Vector3(bk.Min.x + e - h, cy - w / 2f, z0), new Vector3(bk.Min.x + e, cy + w / 2f, z1));
            bottom = bk.Min.y + e - h;
            return true;
        }

        // 1 辺 1 の箱（面ごとに頂点を分ける。UV は Skin が箱の投影で作り直す）
        static Mesh _box;
        static Mesh BoxMesh()
        {
            if (_box != null) return _box;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<int>();
            var dirs = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            foreach (var d in dirs)
            {
                var u = Mathf.Abs(d.y) > 0.5f ? Vector3.right : Vector3.up;
                var s = Vector3.Cross(d, u);
                int b = v.Count;
                v.Add((d - u - s) / 2f); v.Add((d + u - s) / 2f); v.Add((d + u + s) / 2f); v.Add((d - u + s) / 2f);
                for (int i = 0; i < 4; i++) n.Add(d);
                t.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }
            _box = new Mesh { name = "FPC_box" };
            _box.vertices = v.ToArray(); _box.normals = n.ToArray(); _box.uv = new Vector2[v.Count];
            _box.triangles = t.ToArray();
            _box.RecalculateBounds();
            _box.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return _box;
        }

        static readonly HashSet<string> _loggedBones = new();
        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var r = FindDeep(t.GetChild(i), name); if (r != null) return r; }
            return null;
        }

        static string PathIn(Transform t, Transform root)
        {
            var s = "";
            for (; t != null && t != root; t = t.parent) s = s.Length == 0 ? t.name : t.name + "/" + s;
            return t == root ? s : null;
        }

        static Vector3 Div(Vector3 a, Vector3 b) => new(a.x / b.x, a.y / b.y, a.z / b.z);
        static void SetLayer(Transform t, int layer) { t.gameObject.layer = layer; for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer); }

        // モデルを rot で回して、描画の範囲が箱 min〜max にぴったり入るように伸縮して置く
        // レール付きハンドガード：上下に分けた模型があれば、それぞれを全体と同じ倍率で置き、上の部品だけ前後を UpZ に伸ばす
        static GameObject SpawnGuard(Guard g, Transform root, int layer)
        {
            string lo = g.Model + SplitLow, up = g.Model + SplitUp;
            if (g.UpZ is not Vector2 uz || !File.Exists(Path.Combine(ModelDir, lo, "model.json")) || !File.Exists(Path.Combine(ModelDir, up, "model.json")))
                return Spawn(g.Model, root, layer, "guard", Quaternion.identity, g.Min, g.Max);
            var fb = ModelLoader.Load(Path.Combine(ModelDir, g.Model)).Bounds;
            Vector3 Map(Vector3 v) => g.Min + Vector3.Scale(Vector3.Scale(v - fb.min, new Vector3(1f / fb.size.x, 1f / fb.size.y, 1f / fb.size.z)), g.Max - g.Min);
            var lb = ModelLoader.Load(Path.Combine(ModelDir, lo)).Bounds;
            var ub = ModelLoader.Load(Path.Combine(ModelDir, up)).Bounds;
            var go = Spawn(lo, root, layer, "guard", Quaternion.identity, Map(lb.min), Map(lb.max));
            if (go == null) return null;
            Vector3 umin = Map(ub.min), umax = Map(ub.max);
            umin.z = uz.x; umax.z = uz.y;
            Spawn(up, root, layer, "guard_up", Quaternion.identity, umin, umax);
            return go;
        }

        static GameObject Spawn(string model, Transform root, int layer, string name, Quaternion rot, Vector3 min, Vector3 max)
        {
            GameObject go;
            try { go = ModelLoader.Spawn(Path.Combine(ModelDir, model), root, layer); }
            catch (Exception e) { Main.Log($"[guard] model {model} failed: {e.Message}"); return null; }
            go.name = Mount.Prefix + name;
            var b = ModelLoader.Load(Path.Combine(ModelDir, model)).Bounds;
            // 回したあとの範囲
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var c = rot * new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                lo = Vector3.Min(lo, c); hi = Vector3.Max(hi, c);
            }
            var rs = hi - lo; var want = max - min;
            var k = new Vector3(want.x / rs.x, want.y / rs.y, want.z / rs.z);   // 回したあとの軸ごとの倍率
            // 倍率はモデルの軸に戻して掛ける（回転は 90 度単位なので軸の入れ替えだけ）
            var kl = Quaternion.Inverse(rot) * k;
            go.transform.localRotation = rot;
            go.transform.localScale = new Vector3(Mathf.Abs(kl.x), Mathf.Abs(kl.y), Mathf.Abs(kl.z));
            var center = Vector3.Scale((lo + hi) / 2f, k);
            go.transform.localPosition = (min + max) / 2f - center;
            Skin.ApplyPart(go, root, Skin.Role.Body);   // ハンドガード・レール：ほかのアタッチメントと同じ質感
            return go;
        }
    }
}
