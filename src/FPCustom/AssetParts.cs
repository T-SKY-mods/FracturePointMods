using System;
using System.Collections.Generic;
using System.IO;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 無料モデルで足したアタッチメント（ゲームに無いもの）。モデルは Guards.ModelDir（CC BY 4.0。assets\custom_src と CREDITS 参照）
    //   サプレッサー：KAC QDSS（1 種類）。ハンドガンのコンペンセイター：1911 用
    public static class AssetParts
    {
        public sealed class Model { public string Name; public bool Flip, Part; public float Scale = 1f; }   // Flip：模型の前が -Z のもの（Y で 180 度回す）。Part：ほかのアタッチメントと同じ質感（銃身の続きでない部品）。Scale：大きさの倍率

        static readonly HashSet<int> NoSuppressor = new();        // サプレッサーを付けられない武器（今は無し。散弾銃の Hunter-12 にも付けてよい：2026-10-30 ユーザー確認）

        public static Model MuzzleModel(int id, Att a) => a?.Id switch
        {
            "suppressor" when NoSuppressor.Contains(id) => null,
            "suppressor" => new Model { Name = "qdss", Flip = true, Scale = RifleScale(id) },   // 模型は前が -Z（銃身の穴の小さい端）
            "p_compensator" => new Model { Name = "pcomp" },
            // アローヘッドブレーキ：Firearms Kit（CC0）の muzzle.005（先が矢じり形の 2 ポートのブレーキ）。模型は前が -Z
            "arrowhead" => new Model { Name = "arrowhead", Flip = true, Part = true, Scale = RifleScale(id) },
            // ハンドガン用のアローヘッドブレーキ：矢じり形の大きなブレーキはハンドガンに付けないので、先のとがったハンドガン用のブレーキ（tools/make_pmuzzle.py。左右にポート 3 つずつ）
            "p_arrowhead" => new Model { Name = "p_arrowhead", Part = true, Scale = PistolScale(id) },
            // フラッシュコーン（円すい形のフラッシュハイダー。クリンコフ型のコーン）：Firearms Kit（CC0）の先が広がった銃口部品。模型は広がった口が -Z。
            //   模型のままでは口が 34mm と実物（28mm ほど）より大きいので 0.85 倍
            "flash_cone" => new Model { Name = "fcone", Flip = true, Part = true, Scale = 0.85f * RifleScale(id) },
            // リニアコンペンセイター（ハンドガン用。発砲の爆風を前へ逃がす筒）（make_pmuzzle.py。後ろに手で回す刻み、前に逃がしの穴）
            "p_linear_comp" => new Model { Name = "p_lincomp", Part = true, Scale = PistolScale(id) },
            // フラッシュハイダー：同じキットの muzzle.010（先が割れた筒。後ろの銃身の部分は切る）。模型は前が -Z
            "flash_hider" => new Model { Name = "fhider", Flip = true, Part = true, Scale = RifleScale(id) },
            // ハンドガン用のフラッシュハイダー：9mm の 3 本爪のハイダー（make_pmuzzle.py）
            "p_flash_hider" => new Model { Name = "p_fhider", Part = true, Scale = PistolScale(id) },
            _ => null,
        };

        // ハンドガン用の部品（make_pmuzzle.py。9mm の実物の寸法）の倍率：ゲームのハンドガンは実物より 1 割ほど大きい（SP-229 のスライドの上の端が銃身の軸から 11mm）。
        //   .50 の Eagle は銃身もスライドも太いので大きく
        static float PistolScale(int id) => id == 5 ? 1.35f : 1.1f;

        // メイン武器用の部品の倍率：模型は銃身の太さ 16.4mm（M16）に合う大きさ。細い銃身（Type 89・AK-12 等）では銃身の太さに合わせて小さく（0.8 倍まで）
        const float RefBarrel = 0.0164f;
        static float RifleScale(int id) => Fits.Barrel.TryGetValue(id, out var d) ? UnityEngine.Mathf.Clamp(d / RefBarrel, 0.8f, 1f) : 1f;

        public static bool Has(string model) => File.Exists(Path.Combine(Guards.ModelDir, model, "model.json"));

        // その武器に付けられるか（ゲームの部品を複製するもの・最初から付いているものは別に判断）
        public static bool Supported(int id, Att a)
        {
            if (a == null || a.Source != Source.Asset) return true;
            return a.Cat switch
            {
                Cat.Muzzle => MuzzleModel(id, a) is { } m && Has(m.Name) && Fits.Muzzles.ContainsKey(id),
                Cat.Stock => StockModelFor(a.Id, id) is { } sm && Has(sm) && (sm != StockModel || Has(ClampModel)) && StockAt.ContainsKey(id),
                _ => false,
            };
        }

        // ハンドガンの折りたたみストック（ベレッタ 93R 型。tools/make_stock.py で作った形）。2 つの模型を組み合わせる：
        //   金具（p93_clamp。原点＝グリップの背中の下端＝かかと）：かかとを左右からはさむ。グリップの幅に合わせて X を伸縮し、背中の傾きに合わせて回す
        //   ストック（p93_stock。原点＝ヒンジの軸）：ハンドガンの長さに合わせて Z を伸縮（基準のハンドガン 0.205m でかかとから床板まで 0.30m）
        public const string StockModel = "p93_stock", ClampModel = "p93_clamp";
        public static string StockModelFor(string att, int id) => att == "p_stock" ? StockModel : null;
        const float ClampWidth = 0.032f, StockLength = 0.30f, RefPistol = 0.205f;
        static readonly Vector3 Hinge = new(0f, -0.0025f, -0.0105f);   // 金具の模型の中のヒンジの軸
        const int StockRubber = 1;         // ゴムの当て板（グリップの材質を貼る）

        // かかとの位置（高さ, 前後）・背中の傾き（上へ 1m で前へ）・グリップの幅・ハンドガンの長さ（銃口〜スライドの後ろ）。custparts で横・後ろから 1mm ごとに測った値
        public readonly struct StockFit
        {
            public readonly float HeelY, HeelZ, Slope, Width, Length;
            public StockFit(float y, float z, float slope, float width, float length) { HeelY = y; HeelZ = z; Slope = slope; Width = width; Length = length; }
        }
        public static readonly Dictionary<int, StockFit> StockAt = new()
        {
            [1] = new(-0.0698f, -0.0458f, 0.131f, 0.0307f, 0.2025f),   // SP-229
            [5] = new(-0.0809f, -0.0549f, -0.019f, 0.0437f, 0.2718f),  // Eagle .50
            [6] = new(-0.0709f, -0.0415f, 0.187f, 0.0330f, 0.2226f),   // TT
            [7] = new(-0.0673f, -0.0412f, 0.000f, 0.0435f, 0.2031f),   // Model 92
            [8] = new(-0.0540f, -0.0442f, 0.094f, 0.0348f, 0.1944f),   // GR-21
            [9] = new(-0.0769f, -0.0515f, 0.056f, 0.0437f, 0.2061f),   // SP-320
            [13] = new(-0.0597f, -0.0445f, 0.300f, 0.0313f, 0.1854f),  // G-18C Auto
            [22] = new(-0.0697f, -0.0470f, 0.113f, 0.0294f, 0.1785f),  // J-941
            [23] = new(-0.0477f, -0.0389f, 0.281f, 0.0306f, 0.1527f),  // Compact CV-1
            [24] = new(-0.0816f, -0.0481f, -0.056f, 0.0313f, 0.2010f), // GSH
            [25] = new(-0.0655f, -0.0468f, 0.412f, 0.0340f, 0.1785f),  // 1911 Works
        };

        public static float StockLen(StockFit f) => StockLength * Mathf.Clamp(f.Length / RefPistol, 0.88f, 1.15f);

        public static GameObject AttachStock(Transform root, int id, string att = "p_stock")
        {
            var model = StockModelFor(att, id);
            if (model == null || !StockAt.TryGetValue(id, out var f)) return null;
            var go = new GameObject(Mount.Prefix + "stock");
            go.layer = root.gameObject.layer;
            go.transform.SetParent(root, false);
            GameObject clamp, arms;
            try
            {
                clamp = ModelLoader.Spawn(Path.Combine(Guards.ModelDir, ClampModel), go.transform, go.layer);
                arms = ModelLoader.Spawn(Path.Combine(Guards.ModelDir, model), go.transform, go.layer);
            }
            catch (Exception e) { Main.Log($"[parts] stock model failed: {e.Message}"); UnityEngine.Object.Destroy(go); return null; }
            var heel = new Vector3(0f, f.HeelY, f.HeelZ);
            var tilt = Quaternion.Euler(Mathf.Atan(Mathf.Clamp(f.Slope, -0.1f, 0.35f)) * Mathf.Rad2Deg, 0f, 0f);
            clamp.transform.localPosition = heel;
            clamp.transform.localRotation = tilt;
            clamp.transform.localScale = new Vector3((f.Width + 0.0006f) / ClampWidth, 1f, 1f);
            arms.transform.localPosition = heel + tilt * Hinge;
            arms.transform.localScale = new Vector3(1f, 1f, StockLen(f) / StockLength);
            // 色・模様・つやはほかのアタッチメント（レーザーサイト）と同じ灰色の金属。銃がタンなどの色でも同じ
            Skin.ApplyPart(clamp, root, Skin.Role.Body);
            Skin.ApplyPart(arms, root, Skin.Role.Body, StockRubber);
            Skin.ApplyPart(arms, root, Skin.Role.Grip, 0);   // ゴムの当て板：つやを抑える
            return go;
        }

        // 銃口の部品の後ろの端の前後の位置。ふつうは銃口の先（tip）から back だけ後ろ（銃身にかぶせる）。
        // 模型が短い（フラッシュコーン）と元の銃口の部品が前へはみ出すので、模型の前の端が銃口の先より MinOut 前に出るまで前へずらす
        //   ハンドガン：銃口の面に 0.5mm 食い込ませる（銃口の面の測り誤差で細い隙間が見えないように）
        const float MinOut = 0.012f, PistolSink = 0.0005f;
        //   武器の銃口の部品を外す武器（Fits.Devices：AKSU・Hunter-32・R-15・ACE-31、MuzzleCut：AK-74・QBZ・M16）は外した後ろの端に付けるので、ずらさない
        public static float RearZ(int id, Model m, float tip, float back)
        {
            if (Fits.Devices.ContainsKey(id) || MuzzleCut.Has(id)) return tip - back;   // 元の銃口の部品を外した後ろの端に付ける
            if (Catalog.IsPistol(id)) return tip - back - PistolSink;
            var dir = Path.Combine(Guards.ModelDir, m.Name);
            float len = Has(m.Name) ? ModelLoader.Load(dir).Bounds.size.z * m.Scale : back + MinOut;
            return Mathf.Max(tip - back, tip + MinOut - len);
        }

        // 模型を回して、描画の範囲の「後ろの端の中心」を rear に置く（大きさは実物大のまま）
        public static GameObject AttachRear(Transform root, Model m, Vector3 rear, string name)
        {
            GameObject go;
            var dir = Path.Combine(Guards.ModelDir, m.Name);
            try { go = ModelLoader.Spawn(dir, root, root.gameObject.layer); }
            catch (Exception e) { Main.Log($"[parts] model {m.Name} failed: {e.Message}"); return null; }
            go.name = Mount.Prefix + name;
            var b = ModelLoader.Load(dir).Bounds;
            var rot = m.Flip ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            var c = rot * b.center * m.Scale;   // 回しても前後の長さは同じ
            go.transform.localRotation = rot;
            go.transform.localScale = Vector3.one * m.Scale;
            go.transform.localPosition = rear - new Vector3(c.x, c.y, c.z - b.extents.z * m.Scale);
            if (m.Part) Skin.ApplyPart(go, root, Skin.Role.Body); else Skin.Apply(go, root, Skin.Role.Barrel);
            return go;
        }
    }
}
