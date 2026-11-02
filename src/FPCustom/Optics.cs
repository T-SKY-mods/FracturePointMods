using System;
using System.Collections.Generic;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // 最初から付いていないサイトを載せる。ゲームの武器に実際に付いているサイトを複製して、武器ごとの台座の上に置き、
    // 照準点を照準の模様の中心へ、弾の発射点を照準の線の上へ移す（FPRarity の熟練度のサイトと同じ考え方。完成時に熟練度のサイトは外す）
    public static class Optics
    {
        public sealed class Optic
        {
            public string Att;                 // アタッチメント
            public int WeaponId;               // 複製元の武器
            public string Path;                // 複製する部品
            public string Reticle;             // 照準の模様（子の名前。null ならサブメッシュ ReticleSub、それも無ければ全体の中心）
            public int ReticleSub = -1;
            public string[] Exclude = Array.Empty<string>();   // 複製しない子（サイトの下にまとめられている銃の部品）
            public float Bottom;               // 計算上の底面から見た目の底面までの高さ
            // 読み込み時に求める（複製元の武器の根元の座標）
            public GameObject Go; public Bounds Bounds; public Vector3 Ret, Pivot, Scale; public Quaternion Rot;
        }

        public sealed class Part { public string Model; public Vector3 Pos, Euler; public Vector3 Scale = Vector3.one; }
        public sealed class Swap { public string Renderer, Mesh; public int SrcVerts; }

        // 最初からサイトが付いている武器で、ほかのサイトを載せたときに差し替える形（Swiss 751：回転式のリアサイトを削る）
        static readonly Dictionary<int, Swap[]> FactorySwaps = new()
        {
            [17] = new[] { new Swap { Renderer = "SIG 751 1/main_part", Mesh = "swiss_norear.fpm", SrcVerts = 10109 } },
        };
        // 描画の範囲が前に大きく伸びているサイト（Swiss 751 のスコープは中心が z=0.06 になり、ホロ等が銃口側へ行く）：レールの後ろ寄りに置く（高さはレールの歯の上面）。
        // Swiss のレールの歯は z=-0.1325 から前。その後ろの平らな台はリアサイトと一緒に消えるので、サイトの脚が歯の上だけに載る位置に
        static readonly Dictionary<int, (float z, float y)> FactoryPlace = new() { [17] = (-0.075f, 0.0412f) };

        public sealed class Spot
        {
            public float Z, BaseY, X;          // サイトの底面の中心
            public string[] Hide = Array.Empty<string>();
            public Swap[] Swaps = Array.Empty<Swap>();
            public Part[] Parts = Array.Empty<Part>();
            public string Body;                // 本体が骨で動く武器は、一式をこの部品に付ける
            public float Size = 1f;            // サイトの大きさ（倍）
            public Borrow Borrow;              // ほかの武器の台座を複製して使う（台座の上面・中心にサイトを置く）
        }

        // ほかの武器の部品を複製する。From（複製元）と To（この武器）は同じ模型の同じ部品で、そこを基準に同じ位置へ置く
        public sealed class Borrow
        {
            public int WeaponId; public string Path, Child, From, To, Ref, RefCenter, RefAtt; public bool Ready; public Matrix4x4 Pose; public float RefBase;
            // 部品の名前に「/」が入っているもの（Viper の "other/rail.smd"）は Find で探せないので、親の子から名前で探す
            public Transform Find(Transform src)
            {
                var parent = src?.Find(Path);
                if (parent == null || Child == null) return parent;
                for (int i = 0; i < parent.childCount; i++) if (parent.GetChild(i).name == Child) return parent.GetChild(i);
                return null;
            }
        }

        public static readonly List<Optic> All = new()
        {
            new Optic { Att = "holo", WeaponId = 15, Path = "holo", Reticle = "cobra-glass" },                         // Tactic R-15
            new Optic { Att = "ace_sight", WeaponId = 16, Path = "sk_ace32/SightLP", Reticle = "Cube" },               // ACE-31
            // ハンドガン（SP-320 以外）：熟練度の Mod（FPRarity）のサイトと同じ、ゲームの Trijicon RMR をスライドに直に載せる（同じ位置・大きさ）
            new Optic { Att = "p_dot", WeaponId = 0, Path = "Trijicon/RMRMount/Reddot", Reticle = "Plane", Bottom = 0.0042f },
        };

        const string PistolSlide = "W_Mk23Mod0_Skeleton/Mk23Mod0Rig/Root/Slider";   // ハンドガン共通のスライドの骨

        // 武器ごとの台座。最初からサイトが付いている武器は、そのサイトの底面（SpotFromFactory）
        public static readonly Dictionary<int, Spot> Spots = new()
        {
            // M16：キャリングハンドルを削った形に差し替えてレール（FPRarity のモデル。共有版に無ければ付けない）
            [10] = new Spot { Z = -0.020f, BaseY = 0.036f, Swaps = new[] { new Swap { Renderer = "M16/M16", Mesh = "m16_flattop.fpm", SrcVerts = 7678 } },
                              Hide = new[] { "M16/sight" }, Parts = new[] { new Part { Model = "rail_riser", Pos = new Vector3(0f, 0.031f, -0.0185f), Scale = new Vector3(0.53f, 0.53f, 0.94f) } } },
            // AK-74・AKSU・AKM：機関部の左の AK 用サイドマウント（MTK-83）
            [0] = new Spot { Z = -0.090f, BaseY = 0.045f, Parts = new[] { new Part { Model = "mtk83", Pos = new Vector3(0f, 0.051f, -0.090f) } } },
            [11] = new Spot { Z = -0.100f, BaseY = 0.046f, Parts = new[] { new Part { Model = "mtk83", Pos = new Vector3(0f, 0.052f, -0.100f) } } },
            [12] = new Spot { Z = -0.090f, BaseY = 0.045f, Parts = new[] { new Part { Model = "mtk83", Pos = new Vector3(0f, 0.051f, -0.090f) } } },
            // QBZ 191：レール上のリアサイトを削った形に差し替え
            [3] = new Spot { Z = -0.030f, BaseY = 0.042f, Swaps = new[] { new Swap { Renderer = "AKS-74N/cmr/QBZ", Mesh = "qbz_norear.fpm", SrcVerts = 37168 } } },
            // AKMS・Hunter-12：ハンドガードの上にレール（スカウトマウント）
            [4] = new Spot { Z = 0.170f, BaseY = 0.049f, Parts = new[] { new Part { Model = "rail_riser", Pos = new Vector3(0f, 0.044f, 0.170f), Scale = new Vector3(0.53f, 0.53f, 0.55f) } } },
            [19] = new Spot { Z = 0.155f, BaseY = 0.054f, Parts = new[] { new Part { Model = "rail_riser", Pos = new Vector3(0f, 0.049f, 0.155f), Scale = new Vector3(0.53f, 0.53f, 0.55f) } } },
            // Type 89：機関部の上にレール（本体が骨で動く）
            [20] = new Spot { Z = 0.015f, BaseY = 0.034f, Body = "type89_classic/type89_rig_classic/root",
                              Parts = new[] { new Part { Model = "rail_riser", Pos = new Vector3(0f, 0.029f, 0.015f), Scale = new Vector3(0.53f, 0.53f, 0.60f) } } },
            // ER-15S：もとからあるレールの上。リアサイトは隠す
            [21] = new Spot { Z = 0.000f, BaseY = 0.028f, Body = "ar15 long/Reciever", Hide = new[] { "IronSight_Rear", "IronSight_RearAim" } },
            // MPS-5：Viper（同じ模型）に最初から付いている台座（rail.smd）を複製して、同じ位置に載せる。
            // サイトは Viper の最初のホロと同じ前後の中心・照準の模様の左右に置く。高さはホロだけ Viper と同じ（台座を下から抱える）、ほかは台座の上面
            [27] = new Spot { Body = "mp5/Armature/Root",
                              Borrow = new Borrow { WeaponId = 28, Path = "mp5 custom/Armature/sight", Child = "other/rail.smd", From = "mp5 custom/Armature", To = "mp5/Armature",
                                                  Ref = "mp5 custom/Armature/sight/holo", RefCenter = "mp5 custom/Armature/sight/cobra-glass", RefAtt = "holo" } },
            // MPS-5 系：MP5 用のクローマウント
            // Viper M5A3：MPS-5 と同じ模型。最初のホロの台座の高さでは低すぎるので、MPS-5 と同じクローマウントに
            [28] = new Spot { Z = 0.030f, BaseY = -0.002f, Body = "mp5 custom/Armature/Root",
                              Parts = new[] { new Part { Model = "mp5_claw", Pos = new Vector3(0f, -0.023f, 0.030f), Scale = new Vector3(1f, 1f, 0.7f) } } },

            // ハンドガン：熟練度の Mod と同じ位置（スライドの上、リアサイトの前）。スライドと一緒に動かす。X＝スライドの中心の左右のずれ
            [1] = new Spot { Body = PistolSlide, Z = 0.017f, BaseY = 0.0716f },   // 底板がスライドに付く高さ（custseat）
            [5] = new Spot { Body = PistolSlide, Z = 0.022f, BaseY = 0.079f, X = 0.0006f },
            [6] = new Spot { Body = PistolSlide, Z = 0.007f, BaseY = 0.063f },
            [7] = new Spot { Body = PistolSlide, Z = 0.027f, BaseY = 0.072f, X = -0.0011f },
            [8] = new Spot { Body = PistolSlide, Z = 0.017f, BaseY = 0.0647f },   // 底板がスライドに付く高さ（custseat）
            [13] = new Spot { Body = PistolSlide, Z = 0.002f, BaseY = 0.053f },
            [22] = new Spot { Body = PistolSlide, Z = 0.037f, BaseY = 0.065f, X = 0.0015f },
            [23] = new Spot { Body = PistolSlide, Z = 0.012f, BaseY = 0.056f },
            [25] = new Spot { Body = PistolSlide, Z = 0.017f, BaseY = 0.067f },
            [24] = new Spot { Body = PistolSlide + "/slider", Z = 0.007f, BaseY = 0.056f },
        };

        // AR 系：低いサイトをレールに直に載せると、機関部（チャージングハンドル・たたんだ照門）が目の前をふさぐ。
        // 照準の模様がこの高さ（武器の根元の座標）より低いときは、ライザー（かさ上げの台）に載せる（実銃でも AR には高さを合わせる台を使う）
        static readonly Dictionary<int, float> MinReticle = new() { [10] = 0.070f, [15] = 0.082f, [21] = 0.070f };   // R-15 はたたんだ照門（上端 0.061）より十分上に
        const string RiserModel = "rail_riser";

        public static Optic Get(Att a) => a == null ? null : All.Find(o => o.Att == a.Id);

        // 最初からサイトが付いている武器：そのサイトの描画の範囲の底面の中心に置く（台座はそのサイトのものを使わない＝サイトごと消える）
        static readonly Dictionary<int, Spot> _fromFactory = new();
        static Spot SpotFromFactory(int id)
        {
            if (_fromFactory.TryGetValue(id, out var c)) return c;
            Spot sp = null;
            var prefab = Tracker.ItemById(id)?.Weapon?.gameObject;
            foreach (var p in Factory.On(id))
            {
                if (Catalog.Get(p.Att)?.Cat != Cat.Sight || prefab == null) continue;
                var t = prefab.transform.Find(p.Paths[0]);
                if (t == null) continue;
                var b = Lab.MeshBounds(t, prefab.transform);
                // 骨の下にあるサイト（Viper）は、その骨に付けて一緒に動かす
                string body = null;
                var parentPath = SightLab.RelPath(t.parent, prefab.transform);
                if (t.parent != prefab.transform && parentPath.Contains("Armature")) body = parentPath;
                // 左右は最初の照準の線（銃の中心）に合わせる（サイトの描画の範囲の中心は、横のつまみ等で中心からずれている）
                sp = new Spot { Z = b.center.z, BaseY = b.min.y, Body = body, Swaps = FactorySwaps.TryGetValue(id, out var fs) ? fs : Array.Empty<Swap>() };
                if (FactoryPlace.TryGetValue(id, out var fp)) { sp.Z = fp.z; sp.BaseY = fp.y; }
                break;
            }
            return _fromFactory[id] = sp;
        }

        public static Spot SpotFor(int id)
        {
            var f = SpotFromFactory(id);
            if (f != null && !Spots.ContainsKey(id)) return f;
            return Spots.TryGetValue(id, out var s) ? s : f;
        }

        // 付けられるか（台座・モデル・複製元がそろっている）
        // 載せる場所が無い武器（VAL：機関部の上にレールも台座も付けられない。最初の横のスコープだけ）
        static readonly HashSet<int> NoOptic = new() { 14 };

        public static bool Supported(int id, Att a)
        {
            if (NoOptic.Contains(id)) return false;
            var o = Get(a);
            if (o == null) return false;
            var sp = SpotFor(id);
            if (sp == null || !EnsureOptic(o)) return false;
            foreach (var sw in sp.Swaps) if (!ModelLoader.Exists(sw.Mesh)) return false;
            foreach (var p in sp.Parts) if (!ModelLoader.Exists(System.IO.Path.Combine(p.Model, "model.json"))) return false;
            if (sp.Borrow != null && !FitBorrow(sp, Tracker.ItemById(id)?.Weapon?.gameObject)) return false;
            return true;
        }

        // 複製する台座の位置（この武器の根元の座標）を求め、台座の上面の中心をサイトの底面の中心にする
        static bool FitBorrow(Spot sp, GameObject prefab)
        {
            var b = sp.Borrow;
            if (b.Ready) return true;
            var src = Tracker.ItemById(b.WeaponId)?.Weapon?.transform;
            var part = b.Find(src); var sa = src?.Find(b.From); var ta = prefab?.transform.Find(b.To);
            if (part == null || sa == null || ta == null) { Main.Log($"[optic] borrow {b.Path}/{b.Child} missing (part {part != null}, from {sa != null}, to {ta != null})"); return false; }
            var toPrefab = prefab.transform.worldToLocalMatrix * ta.localToWorldMatrix;
            b.Pose = toPrefab * sa.worldToLocalMatrix * part.localToWorldMatrix;
            // 複製元の部品の描画の範囲を、この武器の根元の座標で
            Bounds Mapped(Transform t)
            {
                var bb = Lab.MeshBounds(t, sa);
                var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue); var hi = -lo;
                for (int i = 0; i < 8; i++)
                {
                    var c = toPrefab.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? bb.min.x : bb.max.x, (i & 2) == 0 ? bb.min.y : bb.max.y, (i & 4) == 0 ? bb.min.z : bb.max.z));
                    lo = Vector3.Min(lo, c); hi = Vector3.Max(hi, c);
                }
                var mb = new Bounds(); mb.SetMinMax(lo, hi); return mb;
            }
            var rail = Mapped(part);
            var pap = AimPoint(prefab.GetComponent<DemoWeapon>());
            float apX = pap != null ? prefab.transform.InverseTransformPoint(pap.position).x : 0f;
            // 置く所：参考のサイト（複製元に最初から付いているもの）があればその底面・前後の中心・照準の模様の左右、無ければ台座の上面の中心
            var refT = b.Ref != null ? src.Find(b.Ref) : null;
            var refC = b.RefCenter != null ? src.Find(b.RefCenter) : null;
            if (refT != null)
            {
                var rb = Mapped(refT);
                float cx = refC != null ? Mapped(refC).center.x : rb.center.x;
                sp.X = cx - apX; sp.BaseY = rail.max.y; sp.Z = rb.center.z; b.RefBase = rb.min.y;
            }
            else { sp.X = rail.center.x - apX; sp.BaseY = rail.max.y; sp.Z = rail.center.z; }
            Main.Log($"[optic] borrow {b.Child}: rail top={rail.max.y:F4} center=({rail.center.x:F4},{rail.center.z:F4}); sight base=({sp.X:F4},{sp.BaseY:F4},{sp.Z:F4}) ref={refT != null}/{refC != null}");
            b.Ready = true;
            return true;
        }

        static Transform AimPoint(DemoWeapon dw)
        {
            var g = dw?.scopeGroups;
            return g != null && g.Count > 0 && g[0]?.attachments != null && g[0].attachments.Count > 0 ? g[0].attachments[0]?.aimPoint : null;
        }

        // 銃身の中心の左右の位置（弾の発射点。武器の根元の座標）
        public static float? BoreX(GameObject prefab)
        {
            var sp = Ballistic(prefab)?.PhysicalBulletSpawnPoint;
            return sp == null ? null : prefab.transform.InverseTransformPoint(sp.position).x;
        }

        static Il2CppBallistics.Weapon Ballistic(GameObject go) => go.GetComponentInChildren<Il2CppBallistics.Weapon>(true);

        // 毎回ゲームのままに戻してから付け直す（形の差し替え・隠した部品・弾の発射点）
        public static void Restore(GameObject inst, GameObject prefab, int id)
        {
            var root = inst.transform;
            var sp = Spots.TryGetValue(id, out var s) ? s : FactorySwaps.ContainsKey(id) ? SpotFromFactory(id) : null;
            if (sp != null)
            {
                foreach (var sw in sp.Swaps)
                {
                    var mf = root.Find(sw.Renderer)?.GetComponent<MeshFilter>();
                    var pmf = prefab.transform.Find(sw.Renderer)?.GetComponent<MeshFilter>();
                    if (mf != null && pmf != null && mf.sharedMesh != pmf.sharedMesh) mf.sharedMesh = pmf.sharedMesh;
                }
                SetHidden(root, sp, false);
            }
            var b = Ballistic(inst)?.PhysicalBulletSpawnPoint; var pb = Ballistic(prefab)?.PhysicalBulletSpawnPoint;
            if (b != null && pb != null) { b.localPosition = pb.localPosition; b.localRotation = pb.localRotation; }
        }

        public static Vector3 LastReticle, LastAim;   // テスト用（武器の根元の座標）
        public static readonly Dictionary<int, Vector3> ReticleById = new();

        public static bool Apply(GameObject inst, GameObject prefab, int id, Att a)
        {
            var o = Get(a); var spot = SpotFor(id);
            if (o == null || spot == null || !Supported(id, a)) { Main.Log($"[optic] {a?.Id} not supported on {Tracker.NameOf(id)}"); return false; }
            var root = inst.transform;
            int layer = inst.layer;
            var mount = new GameObject(Mount.Prefix + "sight");
            mount.layer = layer;
            mount.transform.SetParent(root, false);
            foreach (var p in spot.Parts)
            {
                var go = ModelLoader.Spawn(p.Model, mount.transform, layer);
                go.transform.localPosition = p.Pos;
                go.transform.localRotation = Quaternion.Euler(p.Euler);
                go.transform.localScale = p.Scale;
            }
            var sight = UnityEngine.Object.Instantiate(o.Go, mount.transform, false);
            sight.name = Mount.Prefix + "optic_" + o.Att;
            foreach (var ex in o.Exclude) { var c = sight.transform.Find(ex); if (c != null) UnityEngine.Object.DestroyImmediate(c.gameObject); }
            foreach (var mb in sight.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
            sight.SetActive(true);
            SetLayer(sight.transform, layer);
            sight.transform.localRotation = o.Rot;
            // 大きさは複製元の根元（Pivot）を中心に k 倍
            float k = spot.Size;
            Vector3 S(Vector3 p) => o.Pivot + (p - o.Pivot) * k;
            sight.transform.localScale = o.Scale * k;
            float baseY = spot.Borrow is { Ready: true } sb && sb.RefAtt == o.Att ? sb.RefBase : spot.BaseY;
            var shift = new Vector3(-S(o.Ret).x, baseY - S(o.Bounds.min).y - o.Bottom * k, spot.Z - S(o.Bounds.center).z);
            float lift = MinReticle.TryGetValue(id, out var minRet) ? Mathf.Max(0f, minRet - (S(o.Ret) + shift).y) : 0f;
            if (lift > 0.001f)
            {
                shift.y += lift;
                var riser = ModelLoader.Spawn(RiserModel, mount.transform, layer);
                riser.name = Mount.Prefix + "riser";
                var rb = ModelLoader.Load(RiserModel).Bounds;
                var sc = new Vector3(o.Bounds.size.x * k * 0.75f / rb.size.x, lift / rb.size.y, o.Bounds.size.z * k * 0.7f / rb.size.z);
                riser.transform.localScale = sc;
                riser.transform.localPosition = new Vector3(-rb.center.x * sc.x, baseY + lift / 2f - rb.center.y * sc.y, spot.Z - rb.center.z * sc.z);
                Skin.Apply(riser, root, Skin.Role.Body);
            }
            sight.transform.localPosition = o.Pivot + shift;
            var reticle = S(o.Ret) + shift;

            foreach (var s in spot.Swaps)
            {
                var mf = root.Find(s.Renderer)?.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = ModelLoader.LoadMesh(s.Mesh);
            }
            SetHidden(root, spot, true);

            // 構えたときのカメラの向き（照準点の向きの逆）に合わせる
            var gunRot = Quaternion.identity; var gunPos = Vector3.zero;
            var pap = AimPoint(prefab.GetComponent<DemoWeapon>());
            if (pap != null)
            {
                var apPos = prefab.transform.InverseTransformPoint(pap.position);
                var apRot = Quaternion.Inverse(prefab.transform.rotation) * pap.rotation;
                gunRot = Quaternion.Angle(apRot, Quaternion.identity) > 0.05f ? Quaternion.Inverse(apRot) : Quaternion.identity;
                // MPS-5・Viper も熟練度のサイト（FPRarity.SightMount）と同じ置き方：照準点の向き（模型のわずかな傾きの補正）のまま、
                // 左右は最初の照準の線を中心に（台座が受けにまっすぐ載る）
                gunPos = apPos + gunRot * (new Vector3(apPos.x + spot.X, 0f, 0f) - apPos);
            }
            // 照準の模様を銃の中心の線（最初の照準点の左右＋ X）にそろえる（照準点の向きで回した分のずれを直す）
            if (pap != null)
            {
                var want = prefab.transform.InverseTransformPoint(pap.position).x + spot.X;
                gunPos.x += want - (gunPos + gunRot * reticle).x;
            }
            Transform body = spot.Body != null ? root.Find(spot.Body) : null;
            Transform pBody = spot.Body != null ? prefab.transform.Find(spot.Body) : null;
            if (body != null && pBody != null)
            {
                var p = prefab.transform;
                mount.transform.SetParent(body, false);
                mount.transform.localPosition = pBody.InverseTransformPoint(p.TransformPoint(gunPos));
                mount.transform.localRotation = Quaternion.Inverse(pBody.rotation) * (p.rotation * gunRot);
                var ls = pBody.lossyScale; var ps = p.lossyScale;
                mount.transform.localScale = new Vector3(ps.x / ls.x, ps.y / ls.y, ps.z / ls.z);
            }
            else { mount.transform.localPosition = gunPos; mount.transform.localRotation = gunRot; }
            if (spot.Borrow is { Ready: true } bw && bw.Find(Tracker.ItemById(bw.WeaponId)?.Weapon?.transform) is { } bsrc)
            {
                var rail = UnityEngine.Object.Instantiate(bsrc.gameObject, mount.transform, false);
                rail.name = Mount.Prefix + "rail";
                foreach (var mb in rail.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
                rail.SetActive(true);
                SetLayer(rail.transform, layer);
                var L = Matrix4x4.TRS(gunPos, gunRot, Vector3.one).inverse * bw.Pose;
                rail.transform.localPosition = L.GetColumn(3);
                rail.transform.localRotation = L.rotation;
                rail.transform.localScale = L.lossyScale;
            }
            var retRoot = gunPos + gunRot * reticle;
            var camFwd = gunRot * Vector3.forward;
            LastReticle = retRoot;
            ReticleById[id] = retRoot;

            // 照準点を照準の模様の中心の線上へ（前後の位置は元のまま）
            var ap = AimPoint(inst.GetComponent<DemoWeapon>());
            if (ap != null)
            {
                var local = pap != null ? prefab.transform.InverseTransformPoint(pap.position) : root.InverseTransformPoint(ap.position);
                float len = Mathf.Abs(camFwd.z) > 0.1f ? (retRoot.z - local.z) / camFwd.z : 0f;
                var newAp = retRoot - camFwd * len;
                LastAim = new Vector3(newAp.x, newAp.y, local.z);
                ap.position = root.TransformPoint(LastAim);
                AlignBullet(inst, prefab, LastAim, camFwd);
            }
            Main.Log($"[optic] {o.Att} on {Tracker.NameOf(id)}: base=({spot.X:F4},{baseY:F4},{spot.Z:F4}) reticle=({retRoot.x:F4},{retRoot.y:F4},{retRoot.z:F4})");
            return true;
        }

        // 弾は発射点から前へ飛ぶ。発射点を「銃口と同じ前後位置の、照準の線の上」に移し、向きを照準の線と同じにする（距離に関係なく照準の位置に当たる）
        static void AlignBullet(GameObject inst, GameObject prefab, Vector3 aim, Vector3 camFwd)
        {
            var sp = Ballistic(inst)?.PhysicalBulletSpawnPoint;
            var psp = Ballistic(prefab)?.PhysicalBulletSpawnPoint;
            if (sp == null || psp == null || !sp.IsChildOf(inst.transform)) return;
            var p = prefab.transform;
            var pos = p.InverseTransformPoint(psp.position);
            var fwd = p.InverseTransformDirection(psp.forward);
            float len = Mathf.Abs(camFwd.z) > 0.1f ? (pos.z - aim.z) / camFwd.z : 0f;
            var newPos = aim + camFwd * len;
            var parent = psp.parent != null ? psp.parent : p;
            var parentRot = Quaternion.Inverse(p.rotation) * parent.rotation;
            var spRot = Quaternion.Inverse(p.rotation) * psp.rotation;
            sp.localPosition = parent.InverseTransformPoint(p.TransformPoint(newPos));
            sp.localRotation = Quaternion.Inverse(parentRot) * Quaternion.FromToRotation(fwd, camFwd) * spRot;
        }

        static bool EnsureOptic(Optic o)
        {
            if (o.Go != null) return true;
            var srcRoot = Tracker.ItemById(o.WeaponId)?.Weapon?.transform;
            var t = srcRoot?.Find(o.Path);
            if (t == null) return false;
            o.Go = t.gameObject;
            o.Bounds = Bounds(t, srcRoot, o.Exclude);
            var ret = o.Reticle != null ? t.Find(o.Reticle) : null;
            if (ret != null) o.Ret = Bounds(ret, srcRoot, null).center;
            else if (o.ReticleSub >= 0 && t.GetComponent<MeshFilter>()?.sharedMesh is { } mesh && o.ReticleSub < mesh.subMeshCount)
                o.Ret = (srcRoot.worldToLocalMatrix * t.localToWorldMatrix).MultiplyPoint3x4(mesh.GetSubMesh(o.ReticleSub).bounds.center);
            else o.Ret = o.Bounds.center;
            o.Pivot = srcRoot.InverseTransformPoint(t.position);
            o.Rot = Quaternion.Inverse(srcRoot.rotation) * t.rotation;
            var rs = srcRoot.lossyScale; var ts = t.lossyScale;
            o.Scale = new Vector3(ts.x / rs.x, ts.y / rs.y, ts.z / rs.z);
            Main.Log($"[optic] source {o.Att}: bounds min=({o.Bounds.min.x:F4},{o.Bounds.min.y:F4},{o.Bounds.min.z:F4}) max=({o.Bounds.max.x:F4},{o.Bounds.max.y:F4},{o.Bounds.max.z:F4}) reticle=({o.Ret.x:F4},{o.Ret.y:F4},{o.Ret.z:F4})");
            return true;
        }

        static Bounds Bounds(Transform obj, Transform root, string[] exclude)
        {
            bool first = true; var all = new Bounds();
            foreach (var mf in obj.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || (exclude != null && Excluded(mf.transform, obj, exclude))) continue;
                var m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                var mb = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (first) { all = new Bounds(c, Vector3.zero); first = false; } else all.Encapsulate(c);
                }
            }
            return all;
        }

        static bool Excluded(Transform t, Transform top, string[] exclude)
        {
            for (; t != null && t != top; t = t.parent)
                if (t.parent == top && Array.IndexOf(exclude, t.name) >= 0) return true;
            return false;
        }

        static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
        }

        static void SetHidden(Transform root, Spot spot, bool hide)
        {
            if (spot.Hide.Length == 0) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var path = SightLab.RelPath(r.transform, root);
                foreach (var h in spot.Hide) if (path.EndsWith(h, StringComparison.Ordinal)) r.enabled = !hide;
            }
        }
    }
}
