using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

using FPModOption;

namespace FPRarity
{
    // アイアンサイトの武器にホロサイトを載せる（熟練度 Lv10 で解放、部屋で ON/OFF）。
    //
    // ・サイト本体と台座は無料の3Dモデル（assets/CREDITS.txt）を Mod が読み込んで使う（ModelLoader）
    // ・武器ごとに「銃の形の差し替え（例：M16 のキャリングハンドルを削った形）」「隠す部品」「台座（レール等）」「サイトの位置」を決める
    //   位置はすべて武器の根元の座標（+Z が銃口側、+Y が上、単位 m）。SightLab の横からの撮影・上面の高さの計測で決めた値
    // ・照準点（scopeGroups[0] の aimPoint）を、サイトの照準の模様の中心の高さへ移す
    public static class SightMount
    {
        public class Part
        {
            public string Model;                 // models/<名前>
            public Vector3 Pos;                  // 底面の中心の位置
            public Vector3 Euler;                // 向き（度）
            public Vector3 Scale = Vector3.one;  // 大きさ（前後だけ伸ばす等）
        }

        public class Swap
        {
            public string Renderer;  // 形を差し替える部品（描画物のパス）
            public string Mesh;      // models/ 以下の .fpm
            public int SrcVerts;     // 加工元のゲームのメッシュの頂点数（アップデートで形が変わったら差し替えない）
        }

        public class Spot
        {
            public float Z;          // サイトの底面の中心の前後位置
            public float BaseY;      // サイトの底面の高さ
            public string[] Hide = Array.Empty<string>();   // 隠す部品（描画物のパスの末尾）
            public Swap[] Swaps = Array.Empty<Swap>();
            public Part[] Parts = Array.Empty<Part>();      // 台座など
            public string Body;      // 本体がボーン等で動く武器は、サイト一式をこの部品に付けて一緒に動かす
            public float X;          // 一式（台座・サイト・照準点）の左右の微調整（銃身・フロントサイトの中心に合わせる。+が右）
            public Optic Sight = Holo;   // 載せるサイト
        }

        // 載せるサイトの複製元（ゲームの武器に付いているもの）
        public class Optic
        {
            public string Ja, En;          // 表示名
            public int WeaponId;           // 複製元の武器
            public string Path;            // 複製する部品（武器の根元からのパス）
            public string Reticle = "cobra-glass";   // 照準の模様（ゲーム独自のレフレックスサイト用の描画）
            public string[] Exclude = Array.Empty<string>();   // 複製しない子（サイトと一緒にまとめられている銃の部品）
            public float Bottom;           // 計算上の底面（傾いた部品の範囲は大きめに出る）から、見た目の底面までの高さ
            // 以下は読み込み時に求める（複製元の武器の根元の座標）
            public GameObject Go;
            public Bounds Bounds;
            public Vector3 Ret, Pivot, Scale;
            public Quaternion Rot;
        }

        // ライフル用：Tactic R-15 のホロサイト
        public static readonly Optic Holo = new() { Ja = "ホロサイト", En = "Holo sight", WeaponId = 15, Path = "holo" };
        // ハンドガン用：ゲームの Trijicon RMR（ライフルに隠れている小型のドットサイト。RMR 用の台座は使わずスライドに直接載せる）
        // （SP-320 に付いているサイトは幅が約4cmでスライドからはみ出すため、実物でもハンドガンによく使われる RMR を採用）
        public static readonly Optic PistolDot = new()
        {
            Ja = "ドットサイト", En = "Red dot sight", WeaponId = 0, Path = "Trijicon/RMRMount/Reddot", Reticle = "Plane",
            Bottom = 0.0042f,   // 横からの撮影で実測（底の突起はスライドの溝に入る想定で沈める）
        };
        const string PistolSlide = "W_Mk23Mod0_Skeleton/Mk23Mod0Rig/Root/Slider";   // ハンドガン共通のスライドの骨

        public static string SightName(int weaponId, bool ja)
        {
            var o = Config.TryGetValue(weaponId, out var sp) ? sp.Sight : Holo;
            return ja ? o.Ja : o.En;
        }

        public const int SourceWeaponId = 20;           // 透明材質の複製元（Type 89 に隠れているホロサイトの照準の板）
        const string MountName = "FPR_Sight";

        // 武器ごとの取り付け
        public static readonly Dictionary<int, Spot> Config = new()
        {
            // M16：キャリングハンドルを削り（M16A4 のようなフラットトップにする）、開いた上面をレールでふさぎ、その上にサイト
            [10] = new Spot
            {
                Z = -0.020f, BaseY = 0.036f,
                Swaps = new[] { new Swap { Renderer = "M16/M16", Mesh = "m16_flattop.fpm", SrcVerts = 7678 } },
                Hide = new[] { "M16/sight" },   // ハンドルの中にあったリアサイト（ハンドルを削ると宙に浮く）
                Parts = new[] { new Part { Model = "rail_riser", Pos = new Vector3(0f, 0.031f, -0.0185f), Scale = new Vector3(0.53f, 0.53f, 0.94f) } },
            },
            // AK-74：機関部の左側に付ける AK 用サイドマウント（MTK-83）。上のレールが機関部カバーの上に来る
            [0] = new Spot
            {
                Z = -0.090f, BaseY = 0.045f,
                Parts = new[] { new Part { Model = "mtk83", Pos = new Vector3(0f, 0.051f, -0.090f) } },
            },
            // AKSU：同上
            [11] = new Spot
            {
                Z = -0.100f, BaseY = 0.046f,
                Parts = new[] { new Part { Model = "mtk83", Pos = new Vector3(0f, 0.052f, -0.100f) } },
            },
            // QBZ 191：レール上のリアサイトを削った形に差し替え、もとからあるレールの上にサイト
            [3] = new Spot
            {
                Z = -0.030f, BaseY = 0.042f,
                Swaps = new[] { new Swap { Renderer = "AKS-74N/cmr/QBZ", Mesh = "qbz_norear.fpm", SrcVerts = 37168 } },
            },
            // AKMS：機関部カバーが無いモデルなので、ハンドガードの上にレール（スカウトマウント）
            [4] = new Spot
            {
                Z = 0.170f, BaseY = 0.049f,
                Parts = new[] { new Part { Model = "rail_riser", Pos = new Vector3(0f, 0.044f, 0.170f), Scale = new Vector3(0.53f, 0.53f, 0.55f) } },
            },
            // Hunter-12：同上（機関部の前方の上）
            [19] = new Spot
            {
                Z = 0.155f, BaseY = 0.054f,
                Parts = new[] { new Part { Model = "rail_riser", Pos = new Vector3(0f, 0.049f, 0.155f), Scale = new Vector3(0.53f, 0.53f, 0.55f) } },
            },
            // Type 89：リアサイトの前の機関部の上にレール。本体がボーンで動くので本体に付ける
            [20] = new Spot
            {
                Z = 0.015f, BaseY = 0.034f, Body = "type89_classic/type89_rig_classic/root",
                Parts = new[] { new Part { Model = "rail_riser", Pos = new Vector3(0f, 0.029f, 0.015f), Scale = new Vector3(0.53f, 0.53f, 0.60f) } },
            },
            // ER-15S：もとからあるレールの上。リアサイトは視界に入るので隠す
            [21] = new Spot
            {
                Z = 0.000f, BaseY = 0.028f, Body = "ar15 long/Reciever",
                Hide = new[] { "IronSight_Rear", "IronSight_RearAim" },
            },
            // MPS-5：MP5 用のクローマウント（機関部の上の突起をつかむ脚付きレール）。リアサイトのドラムの前
            [27] = new Spot
            {
                Z = 0.030f, BaseY = -0.002f, Body = "mp5/Armature/Root",   // 左右は最初の照準の線（銃の中心）のまま（以前の -4.5mm はサイトが左へずれていた）
                Parts = new[] { new Part { Model = "mp5_claw", Pos = new Vector3(0f, -0.023f, 0.030f), Scale = new Vector3(1f, 1f, 0.7f) } },
            },

            // ---- ハンドガン：RMR をスライドの上（リアサイトの前）に載せ、スライドと一緒に動かす ----
            [1] = new Spot { Sight = PistolDot, Body = PistolSlide, Z = 0.017f, BaseY = 0.072f },   // SP-229
            [5] = new Spot { Sight = PistolDot, Body = PistolSlide, Z = 0.022f, BaseY = 0.079f, X = 0.0006f },   // Eagle .50（スライドの中心が +0.6mm）
            [6] = new Spot { Sight = PistolDot, Body = PistolSlide, Z = 0.007f, BaseY = 0.063f },   // TT
            [7] = new Spot { Sight = PistolDot, Body = PistolSlide, Z = 0.027f, BaseY = 0.072f, X = -0.0011f },   // Model 92（スライドの中心が -1.1mm）
            [8] = new Spot { Sight = PistolDot, Body = PistolSlide, Z = 0.017f, BaseY = 0.065f },   // GR-21
            [13] = new Spot { Sight = PistolDot, Body = PistolSlide, Z = 0.002f, BaseY = 0.053f },   // G-18C Auto
            [22] = new Spot { Sight = PistolDot, Body = PistolSlide, Z = 0.037f, BaseY = 0.065f, X = 0.0015f },   // J-941（スライドの中心が +1.5mm）
            [23] = new Spot { Sight = PistolDot, Body = PistolSlide, Z = 0.012f, BaseY = 0.056f },   // Compact CV-1
            [25] = new Spot { Sight = PistolDot, Body = PistolSlide, Z = 0.017f, BaseY = 0.067f },   // 1911 Works
            [24] = new Spot { Sight = PistolDot, Body = PistolSlide + "/slider", Z = 0.007f, BaseY = 0.056f },   // GSH
        };

        public static bool Supported(int weaponId)
        {
            if (!Config.ContainsKey(weaponId)) return false;
            EnsureValidated();
            return !Broken.ContainsKey(weaponId);
        }

        // ---------- アップデート対策：ゲーム側の部品がそろっているかの確認 ----------
        // 武器ごとに、使うゲームの部品（照準点・本体の骨・隠す部品・形を差し替える部品とその頂点数・複製するサイト）と
        // Mod のモデルファイルがそろっているかを確かめ、足りない武器だけサイトを止める（Mod のほかの機能は動く）。
        public static readonly Dictionary<int, string> Broken = new();
        static bool _validated;

        static void EnsureValidated()
        {
            if (_validated || Tracker.Inv?.WeaponData?.Weapons == null) return;
            _validated = true;
            try
            {
                foreach (var kv in Config)
                {
                    var why = ValidateSpot(kv.Key, kv.Value);
                    if (why != null) { Broken[kv.Key] = why; Main.Log($"[sight] CHECK broken [{kv.Key}] {Tracker.NameOf(kv.Key)}: {why}"); }
                }
                Main.Log($"[sight] validated {Config.Count - Broken.Count}/{Config.Count} weapons");
                // モデルのファイルが無いだけ（共有版にはゲームのモデルを加工したファイルを入れない）なら、そのサイトだけ付けずに知らせない
                var updateBroken = Broken.Where(kv => !kv.Value.EndsWith(" missing", StringComparison.Ordinal)).ToList();
                if (updateBroken.Count > 0)
                {
                    var names = new List<string>();
                    foreach (var kv in updateBroken) names.Add(Tracker.NameOf(kv.Key));
                    GameVersion.AddDegraded("サイト（" + string.Join("、", names) + "）");
                }
            }
            catch (Exception e)
            {
                Main.Log("[sight] validate failed: " + e);
                foreach (var id in Config.Keys) Broken[id] = "validate failed";
                GameVersion.AddDegraded("サイト");
            }
        }

        // 問題があればその内容、なければ null
        public static string ValidateSpot(int weaponId, Spot spot)
        {
            var prefab = Tracker.ItemById(weaponId)?.Weapon?.gameObject;
            if (prefab == null) return "weapon not found";
            var root = prefab.transform;
            if (PrefabAimPoint(prefab) == null) return "aim point not found";
            if (spot.Body != null && root.Find(spot.Body) == null) return $"body '{spot.Body}' not found";
            foreach (var h in spot.Hide)
            {
                bool found = false;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    if (SightLab.RelPath(r.transform, root).EndsWith(h, StringComparison.Ordinal)) { found = true; break; }
                if (!found) return $"hide target '{h}' not found";
            }
            foreach (var sw in spot.Swaps)
            {
                var mf = root.Find(sw.Renderer)?.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) return $"swap target '{sw.Renderer}' not found";
                if (sw.SrcVerts > 0 && mf.sharedMesh.vertexCount != sw.SrcVerts) return $"swap target '{sw.Renderer}' changed (verts {mf.sharedMesh.vertexCount} != {sw.SrcVerts})";
                if (!System.IO.File.Exists(System.IO.Path.Combine(ModelLoader.Dir, sw.Mesh))) return $"model '{sw.Mesh}' missing";
            }
            foreach (var p in spot.Parts)
                if (!System.IO.File.Exists(System.IO.Path.Combine(ModelLoader.Dir, p.Model, "model.json"))) return $"model '{p.Model}' missing";
            var o = spot.Sight;
            if (!EnsureOptic(o)) return $"sight source '{o.Path}' (weapon {o.WeaponId}) not found";
            if (o.Go.transform.Find(o.Reticle) == null) return $"sight reticle '{o.Reticle}' not found";
            return null;
        }
        public static Vector3 LastReticle; // 直前に載せたサイトの照準の中心（テスト用）
        public static Vector3 LastAim;     // 同、移した照準点（武器の根元の座標。テスト用）

        public static Transform FindMount(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == MountName) return t;
            return null;
        }

        // inst: 武器の実体（またはテスト用の複製）、prefab: 元のプレハブ
        public static void Apply(GameObject inst, GameObject prefab, int weaponId, bool on)
        {
            if (inst == null || prefab == null || !Config.TryGetValue(weaponId, out var spot)) return;
            if (!Supported(weaponId)) return;   // アップデートで部品が見つからない武器には付けない（熟練度の ON の記録はそのまま残す）
            var root = inst.transform;
            var existing = FindMount(root);
            if (on == (existing != null)) return;

            if (!on)
            {
                UnityEngine.Object.Destroy(existing.gameObject);
                SetHidden(root, spot, false);
                RestoreSwaps(root, prefab, spot);
                RestoreAimPoint(inst, prefab);
                RestoreBullet(inst, prefab);
                return;
            }

            try
            {
                var o = spot.Sight;
                if (!EnsureOptic(o)) throw new Exception($"game sight not found (weapon {o.WeaponId} '{o.Path}')");
                int layer = inst.layer;
                // 一式をまとめる入れ物（武器の根元と同じ位置・向き）
                var mount = new GameObject(MountName);
                mount.layer = layer;
                mount.transform.SetParent(root, false);

                foreach (var p in spot.Parts)
                {
                    var go = ModelLoader.Spawn(p.Model, mount.transform, layer);
                    go.transform.localPosition = p.Pos;
                    go.transform.localRotation = Quaternion.Euler(p.Euler);
                    go.transform.localScale = p.Scale;
                }
                // サイト：底面の中心を (0, BaseY, Z) に。左右は照準の模様の中心が銃の中心線に来るように置く
                var holo = UnityEngine.Object.Instantiate(o.Go, mount.transform, false);
                holo.name = "FPR_Holo";
                foreach (var ex in o.Exclude) { var c = holo.transform.Find(ex); if (c != null) UnityEngine.Object.DestroyImmediate(c.gameObject); }
                holo.SetActive(true);
                SetLayer(holo.transform, layer);
                holo.transform.localRotation = o.Rot;
                holo.transform.localScale = o.Scale;
                var shift = new Vector3(-o.Ret.x, spot.BaseY - o.Bounds.min.y - o.Bottom, spot.Z - o.Bounds.center.z);
                holo.transform.localPosition = o.Pivot + shift;
                var reticle = o.Ret + shift;   // 入れ物（武器の根元）の座標での照準の模様の中心

                foreach (var s in spot.Swaps)
                {
                    var mf = root.Find(s.Renderer)?.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = ModelLoader.LoadMesh(s.Mesh);
                    else Main.Log($"[sight] swap target '{s.Renderer}' not found on {inst.name}");
                }
                SetHidden(root, spot, true);

                // 銃の向きに合わせる：武器によっては銃のモデルが根元に対してわずかに傾いて（ずれて）おり、ゲームは照準点の向き・位置で
                // それを補正して、構えたときに銃がまっすぐ見えるようにしている（構えたときのカメラの向き＝照準点の向きの逆回転・実測）。
                // 台座とサイトもこの「構えたときのカメラの座標」に合わせて置き、左右は元の照準点の線を中心にする
                var gunRot = Quaternion.identity;
                var gunPos = Vector3.zero;
                var pap = PrefabAimPoint(prefab);
                if (pap != null)
                {
                    var apPos = prefab.transform.InverseTransformPoint(pap.position);
                    var apRot = Quaternion.Inverse(prefab.transform.rotation) * pap.rotation;
                    gunRot = Quaternion.Angle(apRot, Quaternion.identity) > 0.05f ? Quaternion.Inverse(apRot) : Quaternion.identity;
                    gunPos = apPos + gunRot * (new Vector3(apPos.x + spot.X, 0f, 0f) - apPos);
                }

                // 本体が動く武器は、一式を本体の部品に付け替える（プレハブの静止姿勢を基準に換算）
                Transform body = spot.Body != null ? root.Find(spot.Body) : null;
                Transform pBody = spot.Body != null ? prefab.transform.Find(spot.Body) : null;
                if (spot.Body != null && (body == null || pBody == null)) Main.Log($"[sight] body '{spot.Body}' not found on {inst.name}");
                if (body != null && pBody != null)
                {
                    var p = prefab.transform;
                    mount.transform.SetParent(body, false);
                    mount.transform.localPosition = pBody.InverseTransformPoint(p.TransformPoint(gunPos));
                    mount.transform.localRotation = Quaternion.Inverse(pBody.rotation) * (p.rotation * gunRot);
                    var ls = pBody.lossyScale; var ps = p.lossyScale;
                    mount.transform.localScale = new Vector3(ps.x / ls.x, ps.y / ls.y, ps.z / ls.z);
                }
                else
                {
                    mount.transform.localPosition = gunPos;
                    mount.transform.localRotation = gunRot;
                }
                var retRoot = gunPos + gunRot * reticle;   // 武器の根元の座標での照準の模様の中心
                var camFwd = gunRot * Vector3.forward;     // 構えたときのカメラの向き（武器の根元の座標）

                // 照準点を照準の模様の中心の高さへ（前後の位置は元のまま）
                LastReticle = retRoot;
                var groups = inst.GetComponent<DemoWeapon>()?.scopeGroups;
                if (groups != null && groups.Count > 0 && groups[0]?.attachments != null)
                    for (int k = 0; k < groups[0].attachments.Count; k++)
                    {
                        var ap = groups[0].attachments[k]?.aimPoint;
                        if (ap == null) continue;
                        // 照準点を「構えたときのカメラの向きの線上に照準の模様の中心が来る位置」へ移す（前後の位置は元のまま）
                        var local = root.InverseTransformPoint(ap.position);
                        float len = Mathf.Abs(camFwd.z) > 0.1f ? (retRoot.z - local.z) / camFwd.z : 0f;
                        var newAp = retRoot - camFwd * len;
                        ap.position = root.TransformPoint(new Vector3(newAp.x, newAp.y, local.z));
                        if (k == 0) LastAim = new Vector3(newAp.x, newAp.y, local.z);
                        if (k == 0) Main.Log($"[sight] gunRot={gunRot.eulerAngles:F2} gunPos={gunPos:F4} aim {local:F4} -> {newAp:F4}");
                    }
                AlignBullet(inst, prefab, LastAim, camFwd);
                Main.Log($"[sight] mounted on {inst.name} (id {weaponId}) holo base={spot.BaseY:F3} z={spot.Z:F3} reticle={LastReticle:F3}");
            }
            catch (Exception e)
            {
                Main.Log($"[sight] mount failed on {inst.name}: {e}");
                GameVersion.AddDegraded("ホロサイト");
            }
        }

        // ---------- 弾道を照準に合わせる ----------
        // 弾は「弾の発射点（PhysicalBulletSpawnPoint）」から、その前方向に飛ぶ（FPSFireManager.Fire → Ballistics.Weapon.ShootBullet）。
        // サイトを載せると目の位置（照準点）が上がるが銃身はそのままなので、そのままだと弾は照準より下に飛ぶ（AK のサイドマウントでは約9cm）。
        // サイトを付けている間は、発射点を「銃口と同じ前後位置の、構えたときの照準の線の上」に移し、向きを照準の線と同じにする。
        // → 距離に関係なく、照準（レッドドット）の位置に弾が飛ぶ。銃口の火や弾の光の筋は見た目用の発射点（VisualSpawnPoint）から出るので変わらない。
        public static Vector3 LastBulletPos, LastBulletDir;   // 直前に設定した発射点と向き（武器の根元の座標。テスト用）

        static Il2CppBallistics.Weapon Ballistic(GameObject go) => go.GetComponentInChildren<Il2CppBallistics.Weapon>(true);

        static void AlignBullet(GameObject inst, GameObject prefab, Vector3 aim, Vector3 camFwd)
        {
            var sp = Ballistic(inst)?.PhysicalBulletSpawnPoint;
            var psp = Ballistic(prefab)?.PhysicalBulletSpawnPoint;
            if (sp == null || psp == null || !sp.IsChildOf(inst.transform)) { Main.Log($"[sight] bullet spawn point not found on {inst.name}"); return; }
            // プレハブの静止姿勢で計算し、発射点の親に対する位置・向きとして設定する（構えやボーンの動きに左右されない）
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
            LastBulletPos = newPos; LastBulletDir = camFwd;
            Main.Log($"[sight] bullet {inst.name}: pos ({pos.x:F4},{pos.y:F4},{pos.z:F4}) -> ({newPos.x:F4},{newPos.y:F4},{newPos.z:F4}) dir ({fwd.x:F4},{fwd.y:F4},{fwd.z:F4}) -> ({camFwd.x:F4},{camFwd.y:F4},{camFwd.z:F4})");
        }

        static void RestoreBullet(GameObject inst, GameObject prefab)
        {
            var sp = Ballistic(inst)?.PhysicalBulletSpawnPoint;
            var psp = Ballistic(prefab)?.PhysicalBulletSpawnPoint;
            if (sp != null && psp != null) { sp.localPosition = psp.localPosition; sp.localRotation = psp.localRotation; }
        }

        static Transform PrefabAimPoint(GameObject prefab)
        {
            var g = prefab.GetComponent<DemoWeapon>()?.scopeGroups;
            return g != null && g.Count > 0 && g[0]?.attachments != null && g[0].attachments.Count > 0 ? g[0].attachments[0]?.aimPoint : null;
        }

        // ---------- ゲームのサイト（複製元） ----------

        static bool EnsureOptic(Optic o)
        {
            if (o.Go != null) return true;
            var srcRoot = Tracker.ItemById(o.WeaponId)?.Weapon?.transform;
            var t = srcRoot?.Find(o.Path);
            if (t == null) return false;
            o.Go = t.gameObject;
            o.Bounds = MeshBounds(t, srcRoot, null, o.Exclude);
            var ret = t.Find(o.Reticle);
            o.Ret = ret != null ? MeshBounds(ret, srcRoot, null, null).center : o.Bounds.center;
            o.Pivot = srcRoot.InverseTransformPoint(t.position);
            o.Rot = Quaternion.Inverse(srcRoot.rotation) * t.rotation;
            var rs = srcRoot.lossyScale; var ts = t.lossyScale;
            o.Scale = new Vector3(ts.x / rs.x, ts.y / rs.y, ts.z / rs.z);
            Main.Log($"[sight] game sight '{o.Path}' bounds min={o.Bounds.min:F4} max={o.Bounds.max:F4} reticle={o.Ret:F4}");
            return true;
        }

        // プレハブ（シーン外）は Renderer.bounds が空なので、メッシュの範囲を変換して求める
        static Bounds MeshBounds(Transform obj, Transform root, string onlyName, string[] exclude)
        {
            bool first = true; var all = new Bounds();
            foreach (var mf in obj.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || (onlyName != null && mf.gameObject.name != onlyName)) continue;
                if (exclude != null && Excluded(mf.transform, obj, exclude)) continue;
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
                foreach (var h in spot.Hide)
                    if (path.EndsWith(h, StringComparison.Ordinal)) r.enabled = !hide;
            }
        }

        static void RestoreSwaps(Transform root, GameObject prefab, Spot spot)
        {
            foreach (var s in spot.Swaps)
            {
                var mf = root.Find(s.Renderer)?.GetComponent<MeshFilter>();
                var pmf = prefab.transform.Find(s.Renderer)?.GetComponent<MeshFilter>();
                if (mf != null && pmf != null) mf.sharedMesh = pmf.sharedMesh;
            }
        }

        static void RestoreAimPoint(GameObject inst, GameObject prefab)
        {
            var groups = inst.GetComponent<DemoWeapon>()?.scopeGroups;
            if (groups == null || groups.Count == 0 || groups[0]?.attachments == null) return;
            for (int k = 0; k < groups[0].attachments.Count; k++)
            {
                var ap = groups[0].attachments[k]?.aimPoint;
                if (ap == null) continue;
                var p = prefab.transform.Find(SightLab.RelPath(ap, inst.transform));
                if (p != null) ap.localPosition = p.localPosition;
            }
        }
    }
}
