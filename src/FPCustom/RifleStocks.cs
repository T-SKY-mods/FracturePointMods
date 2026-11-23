using System;
using System.Collections.Generic;
using System.IO;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // メイン武器のストック（KK Models の LOW POLY MODULAR WEAPON BUNDLE - PACK ALPHA。ユーザーが購入したもの）。
    //   ストック一式（ストック＋チューブ・ナット・ヒンジなど）を 1 つの模型にしてある（tools/build_kk_stocks.sh。原点＝取り付け面の中心、前が +Z）。
    //   付けるときは、元のストックの描画物を消して、機関部の後ろの端（At）に置く。外したら元のストックを戻す。
    //   バニラでストックの無い武器（AKMS・Hunter-12・"Beater" MPS-5）と、ストックが本体とひとつのメッシュの武器（今は未対応）には付けない
    public static class RifleStocks
    {
        public sealed class Fit
        {
            public Vector3 At;            // 取り付け面の中心（武器の根元の座標）
            public string[] Hide;         // 元のストックの描画物（武器の根元からの道）
            public float Thick = 1f;      // 太さ・高さの倍率（長さはそのまま）。機関部の後ろの大きさに合わせて、チューブが細すぎ・太すぎに見えないように
            public StockCut.Spec Cut;     // ストックが本体とひとつのメッシュの武器：除く三角形
        }

        public const string Name = "rstock";

        // custstockscan（research/stock）で測った値。At.z ＝ 機関部の後ろの端、At.y ＝ ストックの付け根の中心の高さ。
        // Thick：KK のチューブ（直径 約 3cm。実物の大きさ）は、機関部の後ろが高い AK などでは細く見えるので太く
        public static readonly Dictionary<int, Fit> Fits = new()
        {
            [0] = new Fit { At = new(0f, -0.012f, -0.205f), Thick = 1.15f, Hide = new[] { "AKS-74N/ak74m/GameObject/stock_low", "AKS-74N/ak74m/GameObject/stockHinge_low" } },          // AK-74
            [2] = new Fit { At = new(0f, -0.0165f, -0.190f), Thick = 1.1f, Hide = new[] { "AKS-74N/ak12/AK12_Stock_EVO", "AKS-74N/ak12/AK12_Stock_EVO_Base", "AKS-74N/ak12/BufferTube" } },   // AK-12
            [11] = new Fit { At = new(0f, -0.006f, -0.199f), Thick = 1.15f, Hide = new[] { "1/stock_aks74u_izhmash_aks74u_std_LOD0_0" } },   // AKSU
            [12] = new Fit { At = new(0f, -0.010f, -0.205f), Thick = 1.15f, Hide = new[] { "AKM_low7/buttstock_low/buttstock_c_low", "AKM_low7/buttstock_low/buttstock_d_low", "AKM_low7/buttstock_low/buttstock_e_low", "AKM_low7/buttstock_low/buttstock_f_low", "AKM_low7/buttstock_low/buttstock_g_low", "AKM_low7/buttstock_low/buttstock_h_low" } },   // AKM
            [14] = new Fit { At = new(0f, -0.019f, -0.178f), Thick = 1.1f, Hide = new[] { "uploads_files_5248530_ASVAL/2.002_low", "uploads_files_5248530_ASVAL/2_048_low", "uploads_files_5248530_ASVAL/2_049_low", "uploads_files_5248530_ASVAL/2_060_low", "uploads_files_5248530_ASVAL/21_low", "uploads_files_5248530_ASVAL/2_062_low", "uploads_files_5248530_ASVAL/2_063_low", "uploads_files_5248530_ASVAL/2_050_low", "uploads_files_5248530_ASVAL/2_051_low" } },   // VAL
            [15] = new Fit { At = new(0f, 0.0124f, -0.144f), Hide = new[] { "uploads_files_4287500_ar15_default/ar15_default_stock_open" } },   // Tactic R-15
            [16] = new Fit { At = new(0f, 0.000f, -0.232f), Thick = 1.1f, Hide = new[] { "sk_ace32/Geo/Stock" } },   // ACE-31（ストックの付け根 StockTrunnion は残す）
            [17] = new Fit { At = new(0f, 0.000f, -0.184f), Thick = 1.1f, Hide = new[] { "SIG 751 1/stock" } },   // Swiss 751
            [18] = new Fit { At = new(0f, -0.020f, -0.240f), Thick = 1.15f, Hide = new[] { "saiga12_custom_parts/saiga12_stock" } },   // Hunter-32
            // ストックが本体とひとつのメッシュ（StockCut。箱は tools の切り口の確認で決めた値。グリップは箱の下）
            [3] = new Fit { At = new(0f, 0.004f, -0.200f), Thick = 1.05f, Hide = Array.Empty<string>(),
                Cut = new StockCut.Spec { Renderer = "AKS-74N/cmr/QBZ", Src = "qbz_src", Min = new(-0.05f, -0.10f, -0.40f), Max = new(0.05f, 0.045f, -0.192f) } },   // QBZ 191
            [10] = new Fit { At = new(0f, 0.003f, -0.128f), Hide = Array.Empty<string>(),
                Cut = new StockCut.Spec { Renderer = "M16/M16", Src = "m16_src", Min = new(-0.05f, -0.095f, -0.40f), Max = new(0.05f, 0.05f, -0.127f), RecolorMax = new(0.05f, 0.05f, -0.1155f) } },          // M16（残る付け根 z -0.129〜-0.115 は茶色の樹脂 → 金属の色に）
            [20] = new Fit { At = new(0f, 0.002f, -0.122f), Thick = 1.1f, Hide = Array.Empty<string>(),
                Cut = new StockCut.Spec { Renderer = "type89_classic/type89_classic", Src = "type89", Min = new(-0.05f, -0.10f, -0.40f), Max = new(0.05f, 0.05f, -0.1222f), Skinned = true } },   // Type 89
            // MPS-5・Viper M5A3：動くメッシュひとつ（伸縮ストックの棒・支柱・床尾板は z -0.29〜-0.12、機関部の後ろの蓋は z -0.113〜。custsmgscan で測定）
            [27] = new Fit { At = new(0f, -0.047f, -0.113f), Hide = Array.Empty<string>(),
                Cut = new StockCut.Spec { Renderer = "mp5/Magazine_release_button_1", Src = "mps5", Min = new(-0.05f, -0.16f, -0.40f), Max = new(0.05f, 0.0f, -0.1135f), LongMax = new(0.05f, 0.0f, 0.0f), Skinned = true } },   // MPS-5
            [28] = new Fit { At = new(0f, -0.047f, -0.113f), Hide = Array.Empty<string>(),
                Cut = new StockCut.Spec { Renderer = "mp5 custom/Magazine_release_button_1", Src = "viper", Min = new(-0.05f, -0.16f, -0.40f), Max = new(0.05f, 0.0f, -0.1135f), LongMax = new(0.05f, 0.0f, 0.0f), Skinned = true } },   // Viper M5A3
            [21] = new Fit { At = new(0f, 0.0082f, -0.118f), Hide = new[] { "ar15 long/Reciever/Stock_BufferTube", "ar15 long/Reciever/Stock_BufferTube/ButtStock" } },   // ER-15S
        };

        public static string ModelFor(string att) => att switch
        {
            "stock_mcx" => "rstock_mcx",
            "stock_prs" => "rstock_prs",
            "stock_sba3" => "rstock_sba3",
            "stock_moe" => "rstock_moe",
            _ => null,
        };

        public static bool Supported(int id, Att a) => a != null && !a.Pistol && Fits.ContainsKey(id) && ModelFor(a.Id) is { } m && AssetParts.Has(m);

        // 元のストックを見せる・隠す（プレハブの状態に戻すか、消す）
        public static void ShowVanilla(Transform root, Transform prefab, int id, bool show)
        {
            if (!Fits.TryGetValue(id, out var f)) return;
            foreach (var path in f.Hide)
            {
                var r = root.Find(path)?.GetComponent<Renderer>(); var pr = prefab.Find(path)?.GetComponent<Renderer>();
                if (r == null || pr == null) { Main.Log($"[rstock] [{id}] renderer not found: {path}"); continue; }
                r.enabled = show && pr.enabled;
            }
        }

        public static GameObject Attach(Transform root, Transform prefab, int id, Att a)
        {
            if (!Fits.TryGetValue(id, out var f) || ModelFor(a.Id) is not { } model) return null;
            var go = new GameObject(Mount.Prefix + Name) { layer = root.gameObject.layer };
            go.transform.SetParent(root, false);
            go.transform.localPosition = f.At;
            go.transform.localScale = new Vector3(f.Thick, f.Thick, 1f);
            GameObject body, hw = null;
            try
            {
                body = ModelLoader.Spawn(Path.Combine(Guards.ModelDir, model), go.transform, go.layer);
                if (AssetParts.Has(model + "_hw")) hw = ModelLoader.Spawn(Path.Combine(Guards.ModelDir, model + "_hw"), go.transform, go.layer);
            }
            catch (Exception e) { Main.Log($"[rstock] model {model} failed: {e.Message}"); UnityEngine.Object.Destroy(go); return null; }
            ShowVanilla(root, prefab, id, false);
            StockCut.Apply(root, id);
            StockCut.Cap(root, id);   // 切り口を塞ぐ（構えたとき機関部の中が透けないように）
            // 色・つやを銃に合わせる（金属の部品は銃の本体の材質そのもの、樹脂は銃の主な色・つやに）
            Finish(go, root, id, hw);
            return go;
        }

        // ---------- 銃に合わせた仕上げ ----------
        //   KK の模型の材質は使わず、銃そのものの材質（の無地の所を実物の細かさで繰り返した物）を貼る → 同じ照明・凹凸・汚れ・つやで、他の部品と並べて浮かない
        //   金属（模型のテクスチャが metal_base・金具の模型）：銃の本体の材質
        //   樹脂：銃の元のストック／グリップ／ハンドガードの材質（Skin.FurnitureSource）。無い時（木の銃）は本体の材質を樹脂らしく（つや半分・金属感なし）
        //         模型の中の明暗の差（当て板など）は残す：いちばん面積の大きい灰色の樹脂に対する色の比を掛ける。色の付いた樹脂（FDE）は色合いを残す
        public static string LastFurnitureForTest; public static int LastTiledSubsForTest;

        static float Lin(float c) => Mathf.GammaToLinearSpace(c);
        static float Lum(Color c) => 0.2126f * Lin(c.r) + 0.7152f * Lin(c.g) + 0.0722f * Lin(c.b);
        static float Sat(Color c) { float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b)); return mx > 0.02f ? (mx - mn) / mx : 0f; }

        // hw：金具（チューブ・ナット・ヒンジ）の模型。樹脂の色の材質でも金属として扱う（実物は金属）
        static void Finish(GameObject go, Transform root, int id, GameObject hw)
        {
            try
            {
                Fits.TryGetValue(id, out var ff);
                var src = Skin.BodySource(root, Lab.MeshBounds(go.transform, root));
                // 本体が動くメッシュひとつの武器（MPS-5・Viper）：骨ごとに置いた形は Mod の物として除かれるので、元の描画物の材質から選ぶ
                if (src == null && ff?.Cut != null && root.Find(ff.Cut.Renderer)?.GetComponent<Renderer>() is { } orig)
                {
                    // いちばん三角形の多いサブメッシュの材質（本体）
                    var sm = orig.TryCast<SkinnedMeshRenderer>()?.sharedMesh; var oms = orig.sharedMaterials; long best = -1;
                    for (int i = 0; i < oms.Length; i++)
                    {
                        if (oms[i] == null || oms[i].GetTexture("_BaseMap") == null) continue;
                        long n = sm != null && i < sm.subMeshCount ? (long)sm.GetSubMesh(i).indexCount : 0;
                        if (n > best) { best = n; src = oms[i]; }
                    }
                }
                if (src == null) { Main.Log($"[rstock] [{id}] no gun material to match"); return; }
                // 樹脂の色は全武器で統一：ACE-31 のストックの材質（ユーザーの指定 2026-11-23）。取れない時だけ、その銃の樹脂
                var furn = UnifiedPolymer();
                if (furn == null)
                {
                    furn = Skin.FurnitureSource(root, ff?.Hide);
                    // 元のストックが本体と同じテクスチャ（金属の地）なら樹脂の元にはしない（そのままだと金属のつやで FDE が金色に見える）
                    if (furn != null && furn.GetTexture("_BaseMap") == src.GetTexture("_BaseMap")) furn = null;
                }
                var polyRole = furn != null ? Skin.Role.Body : Skin.Role.Grip;
                var polySrc = furn ?? src;
                LastFurnitureForTest = polySrc.name + (furn != null ? "" : " (body as polymer)");
                // 全体でいちばん面積の大きい灰色の樹脂（明暗の比の基準）
                Color main = default; int mainN = -1;
                foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mesh = mr.GetComponent<MeshFilter>()?.sharedMesh; var mats = mr.sharedMaterials;
                    for (int i = 0; i < mats.Length && mesh != null && i < mesh.subMeshCount; i++)
                    {
                        if (mats[i] == null || IsMetal(mats[i]) || InHw(mr, hw)) continue;
                        var c = mats[i].GetColor("_BaseColor");
                        int n = (int)mesh.GetSubMesh(i).indexCount;
                        if (Sat(c) < 0.25f && n > mainN) { mainN = n; main = c; }
                    }
                }
                var furnDom = Skin.DomColor(polySrc);
                int tiled = 0;
                foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mats = mr.sharedMaterials; bool inHw = InHw(mr, hw);
                    Skin.TileSubs(mr, root, i =>
                    {
                        var m = mats[i]; if (m == null) return (null, Skin.Role.Body, Color.white);
                        tiled++;
                        if (inHw || IsMetal(m)) return (src, Skin.Role.Body, Color.white);
                        return (polySrc, polyRole, PolyMult(m.GetColor("_BaseColor"), main, mainN >= 0, furnDom));
                    });
                }
                LastTiledSubsForTest = tiled;
                Main.Log($"[rstock] [{id}] finish: metal '{src.name}', polymer '{LastFurnitureForTest}', {tiled} subs (main poly {(mainN >= 0 ? main.ToString() : "none")})");
            }
            catch (Exception e) { Main.Log($"[rstock] [{id}] finish failed: {e.Message}"); }
        }

        // 樹脂の色の倍率（リニア）：灰色は主な樹脂との明るさの比（0.4〜1.3）。色付きは色合い × 銃の樹脂との明るさの間
        static Color PolyMult(Color c, Color main, bool hasMain, Color furnDom)
        {
            if (Sat(c) < 0.25f)
            {
                if (!hasMain) return Color.white;
                float k = Mathf.Clamp(Lum(c) / Mathf.Max(0.0005f, Lum(main)), 0.4f, 1.3f);
                return Mathf.Abs(k - 1f) < 0.02f ? Color.white : new Color(k, k, k);
            }
            // 色付き：銃の樹脂（灰色）を、この色の色合いと（銃の樹脂とこの色の間の）明るさに
            float lf = Mathf.Max(0.0005f, Lum(furnDom)), lc = Lum(c), want = Mathf.Sqrt(lf * lc);
            float f = want / Mathf.Max(0.0005f, lc);
            Color r = new(Lin(c.r) * f / lf, Lin(c.g) * f / lf, Lin(c.b) * f / lf);
            return new Color(Mathf.Clamp(r.r, 0.2f, 4f), Mathf.Clamp(r.g, 0.2f, 4f), Mathf.Clamp(r.b, 0.2f, 4f));
        }

        // 全武器のストックの樹脂の元：ACE-31（16）のプレハブのストックの材質
        public const int PolymerDonor = 16;
        static Material _unified;
        static Material UnifiedPolymer()
        {
            if (_unified != null) return _unified;
            var ace = Tracker.ItemById(PolymerDonor)?.Weapon?.transform;
            if (ace == null || !Fits.TryGetValue(PolymerDonor, out var f)) { Main.Log("[rstock] ACE-31 polymer not found"); return null; }
            _unified = Skin.FurnitureSource(ace, f.Hide);
            if (_unified != null) Main.Log($"[rstock] unified polymer from ACE-31: '{_unified.name}'");
            return _unified;
        }

        static bool InHw(Component c, GameObject hw) => hw != null && c.transform.IsChildOf(hw.transform);
        static bool IsMetal(Material m) => m.GetTexture("_BaseMap") is Texture t && t.name.Contains("metal_base");

    }
}
