using System;
using System.Collections.Generic;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 無料モデルの部品に、その銃自身の材質（テクスチャ・凹凸・金属感・色）を貼って、銃と同じ仕上げに見せる。
    //   ・材質は銃の描画物の材質を複製する（シェーダー・ノーマル・マスクもそのまま）
    //   ・部品の UV は箱の投影（各面の向きで 2 軸を選ぶ）で作り直し、銃のテクスチャの「無地の金属」の窓に収める
    //     （窓：縮小したテクスチャで、銃の主な色に近く、模様の境目が少なく、木などの色の付いていない所を探す）
    public static class Skin
    {
        public enum Role { Body, Barrel, Grip }

        // 部品の見た目の 1 辺（m）を窓の 1 辺に合わせる。これより大きい部品は窓の中で縮める
        const float WindowMeters = 0.12f;
        const int Probe = 128, Win = 14;   // 縮小したテクスチャの大きさ・窓の大きさ（画素）

        static readonly Dictionary<(IntPtr, int), Vector4> _window = new();   // （テクスチャ, 窓の大きさ）→ 窓 (x0, y0, w, h)（UV）
        static readonly Dictionary<IntPtr, float> _greyFrac = new();      // テクスチャ → 色の付いていない（金属・黒の樹脂の）画素の割合
        static readonly Dictionary<string, Material> _mats = new();        // 材質の複製（元の材質＋窓＋大きさ）
        static readonly Dictionary<IntPtr, Mesh> _boxMeshes = new();       // 元のメッシュ → 箱の投影の UV のメッシュ

        // part の描画物（keepSubs のサブメッシュはそのまま）に銃の材質を貼る。gun：武器の実体の根元
        public static void Apply(GameObject part, Transform gun, Role role, params int[] keepSubs)
        {
            if (part == null || gun == null) return;
            try
            {
                var src = Source(gun, role, Lab.MeshBounds(part.transform, gun));
                if (src == null) { Main.Log($"[skin] no source material on {gun.name}"); return; }
                ApplyFrom(part, gun, src, role, keepSubs);
            }
            catch (Exception e) { Main.Log($"[skin] failed on {part.name}: {e.Message}"); }
        }

        // part の描画物に src の材質（の無地の窓）を貼る。銃の色に関係なく同じ仕上げにしたい部品用
        public static void ApplyFrom(GameObject part, Transform gun, Material src, Role role, params int[] keepSubs)
        {
            if (part == null || gun == null || src == null) return;
            try
            {
                foreach (var mr in part.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    var box = BoxMesh(mf.sharedMesh);
                    mf.sharedMesh = box;
                    float size = Mathf.Max(box.bounds.size.x, box.bounds.size.y, box.bounds.size.z) * MaxScale(mr.transform, gun);
                    var mats = mr.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                        if (Array.IndexOf(keepSubs, i) < 0) mats[i] = Skinned(src, size, MaxScale(mr.transform, gun), role);
                    mr.sharedMaterials = mats;
                }
            }
            catch (Exception e) { Main.Log($"[skin] failed on {part.name}: {e.Message}"); }
        }

        // アタッチメントの材質（ゲームのハンドガン用レーザーサイト）。銃の色に関係なく、ほかの部品と同じ灰色の金属の仕上げ
        static Material _partMat;
        public static Material PartMaterial()
        {
            if (_partMat != null) return _partMat;
            var d = LaserLab.Pistol;
            var root = Tracker.ItemById(d.WeaponId)?.Weapon?.transform.Find(d.Path);
            if (root == null) { Main.Log("[skin] part material: laser not found"); return null; }
            float best = 0f;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var m = r.sharedMaterial;
                if (m == null || !m.HasProperty("_BaseMap") || m.GetTexture("_BaseMap") == null) continue;
                var b = r.bounds.size; float size = b.x + b.y + b.z;
                if (size > best) { best = size; _partMat = m; }
            }
            Main.Log($"[skin] part material: '{_partMat?.name}'");
            return _partMat;
        }

        // 足した部品（ストック・ハンドガード・レール）：ほかのアタッチメント（レーザーサイト）と同じ質感。見つからなければ銃の材質
        public static void ApplyPart(GameObject part, Transform gun, Role role, params int[] keepSubs)
        {
            if (PartMaterial() is { } pm) ApplyTiled(part, gun, pm, role, keepSubs);
            else Apply(part, gun, role, keepSubs);
        }

        // ほかのアタッチメントと同じ質感で貼る：src のテクスチャ（色・凹凸・つや・陰）から、汚れの付いた無地の所（PatchWin）を切り出し、
        //   実物と同じくらいの細かさ（1 枚 PatchMeters）で繰り返す（折り返しで継ぎ目を目立たせない）。つやは src のまま
        //   （窓を部品全体に引き伸ばすと細かい汚れ・凹凸が消えて、のっぺりした樹脂のように見える）
        const int PatchWin = 12, PatchPx = 256;
        const float PatchMeters = 0.03f;
        // レーザーサイトのテクスチャで切り出す所（UV）：傷・汚れだけの無地の黒い金属の面（枠・文字・色の付いた所を避けて選んだ）
        static readonly Vector4 PartPatch = new(0.40f, 0.46f, 0.085f, 0.085f);
        static readonly string[] PatchMaps = { "_BaseMap", "_MainTex", "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap" };
        static readonly Dictionary<(IntPtr, int), Texture2D> _patches = new();
        static readonly Dictionary<string, Material> _tiled = new();

        public static void ApplyTiled(GameObject part, Transform gun, Material src, Role role, params int[] keepSubs)
        {
            if (part == null || gun == null || src == null) return;
            try
            {
                foreach (var mr in part.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    mf.sharedMesh = BoxMesh(mf.sharedMesh);
                    var mats = mr.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                        if (Array.IndexOf(keepSubs, i) < 0) mats[i] = Tiled(src, MaxScale(mr.transform, gun), role);
                    mr.sharedMaterials = mats;
                }
            }
            catch (Exception e) { Main.Log($"[skin] tiled failed on {part.name}: {e.Message}"); }
        }

        static Material Tiled(Material src, float scale, Role role)
        {
            float uvPerUnit = scale / PatchMeters;   // メッシュの 1 単位 = 実物の scale m
            string key = $"{src.Pointer}_{uvPerUnit:F2}_{role}";
            if (_tiled.TryGetValue(key, out var m) && m != null) return m;
            var baseTex = src.GetTexture("_BaseMap") ?? src.GetTexture("_MainTex");
            if (baseTex == null) return src;
            var w = src == _partMat ? PartPatch : Window(baseTex, PatchWin);
            m = new Material(src) { name = Mount.Prefix + "tiled_" + src.name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            foreach (var p in PatchMaps)
            {
                if (!m.HasProperty(p) || m.GetTexture(p) is not Texture t) continue;
                bool colour = p == "_BaseMap" || p == "_MainTex";
                m.SetTexture(p, Patch(t, w, !colour));
                m.SetTextureScale(p, new Vector2(uvPerUnit, uvPerUnit));
                m.SetTextureOffset(p, Vector2.zero);
            }
            // ゴムの当て板（Grip）はつやを抑えて少し暗く
            if (role == Role.Grip)
            {
                foreach (var p in new[] { "_Smoothness", "_GlossMapScale", "_Glossiness" }) if (m.HasProperty(p)) m.SetFloat(p, m.GetFloat(p) * 0.5f);
                foreach (var p in new[] { "_Metallic" }) if (m.HasProperty(p)) m.SetFloat(p, 0f);
                foreach (var p in new[] { "_BaseColor", "_Color" })
                    if (m.HasProperty(p)) { var c = m.GetColor(p); m.SetColor(p, new Color(c.r * 0.8f, c.g * 0.8f, c.b * 0.8f, c.a)); }
            }
            Main.Log($"[skin] tiled from '{src.name}' ({baseTex.name}) patch=({w.x:F3},{w.y:F3},{w.z:F3}) uv/unit={uvPerUnit:F1} {role}");
            return _tiled[key] = m;
        }

        // テクスチャの窓 w の所を切り出した、繰り返し用のテクスチャ（折り返し・ミップ付き）。linear：凹凸・つや・陰のマップ（色の変換をしない）
        static Texture2D Patch(Texture src, Vector4 w, bool linear)
        {
            if (_patches.TryGetValue((src.Pointer, PatchWin), out var t) && t != null) return t;
            var rt = RenderTexture.GetTemporary(PatchPx, PatchPx, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt, new Vector2(w.z, w.w), new Vector2(w.x, w.y));
            var prev = RenderTexture.active; RenderTexture.active = rt;
            t = new Texture2D(PatchPx, PatchPx, TextureFormat.RGBA32, true, linear) { name = Mount.Prefix + "patch_" + src.name };
            t.ReadPixels(new Rect(0, 0, PatchPx, PatchPx), 0, 0);
            t.Apply(true, true);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            t.wrapMode = TextureWrapMode.Mirror; t.filterMode = FilterMode.Trilinear; t.anisoLevel = 4;
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return _patches[(src.Pointer, PatchWin)] = t;
        }

        // 木の部分（subs のサブメッシュ）に、ゲームの木の材質（AKSU の木のハンドガード）を貼る。木目は部品の前後方向
        const int WoodDonor = 11;
        const string WoodPath = "1/handguard_aks74u_izhmash_aks74u_std_wood_LOD0_0";
        static readonly Color WoodTint = new(0.50f, 0.36f, 0.26f);   // 木の色を暗く・少し赤茶に
        const int WoodWin = 40;           // 木の窓（縮小したテクスチャの画素）。金属の窓より大きく取って木目を見せる
        public static void ApplyWood(GameObject part, Transform gun, params int[] subs)
        {
            if (part == null || gun == null) return;
            try
            {
                var src = Tracker.ItemById(WoodDonor)?.Weapon?.transform.Find(WoodPath)?.GetComponent<Renderer>()?.sharedMaterial;
                if (src == null || src.GetTexture("_BaseMap") == null) { Main.Log("[skin] wood material not found"); return; }
                foreach (var mr in part.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    var box = mf.sharedMesh.name.EndsWith("_box") ? mf.sharedMesh : BoxMesh(mf.sharedMesh);
                    mf.sharedMesh = box;
                    float scale = MaxScale(mr.transform, gun);
                    float size = Mathf.Max(box.bounds.size.x, box.bounds.size.y, box.bounds.size.z) * scale;
                    var mats = mr.sharedMaterials;
                    foreach (var i in subs) if (i < mats.Length) mats[i] = Wooded(src, size, scale);
                    mr.sharedMaterials = mats;
                }
            }
            catch (Exception e) { Main.Log($"[skin] wood failed on {part.name}: {e.Message}"); }
        }

        static Material Wooded(Material src, float sizeMeters, float scale)
        {
            var tex = src.GetTexture("_BaseMap");
            var w = WoodWindow(tex);
            float uvPerUnit = w.z / Mathf.Max(0.05f, sizeMeters) * scale;
            string key = $"wood_{src.Pointer}_{uvPerUnit:F3}";
            if (_mats.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(src) { name = Mount.Prefix + "wood_" + src.name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            var st = new Vector4(uvPerUnit, uvPerUnit * w.w / w.z, w.x, w.y);
            foreach (var p in new[] { "_BaseMap", "_MainTex" })
                if (m.HasProperty(p)) { m.SetTextureScale(p, new Vector2(st.x, st.y)); m.SetTextureOffset(p, new Vector2(st.z, st.w)); }
            // 暗めの使い込んだ木の色に（AKSU の木は明るすぎる）。つやも少し落とす
            foreach (var p in new[] { "_BaseColor", "_Color" })
                if (m.HasProperty(p)) { var c = m.GetColor(p); m.SetColor(p, new Color(c.r * WoodTint.r, c.g * WoodTint.g, c.b * WoodTint.b, c.a)); }
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", m.GetFloat("_Smoothness") * 0.7f);
            _mats[key] = m;
            Main.Log($"[skin] wood from '{src.name}' ({tex.name}) window=({w.x:F3},{w.y:F3},{w.z:F3},{w.w:F3}) size={sizeMeters:F3}m");
            return m;
        }

        // 木の窓：色の付いた（木の）画素が多く、境目（UV の島の端・金具）が少ない所
        static readonly Dictionary<IntPtr, Vector4> _woodWindow = new();
        static Vector4 WoodWindow(Texture tex)
        {
            if (_woodWindow.TryGetValue(tex.Pointer, out var v)) return v;
            v = new Vector4(0.3f, 0.3f, WoodWin / (float)Probe, WoodWin / (float)Probe);
            Texture2D t = null;
            try
            {
                t = SkinLab.Readable(tex, Probe);
                int W = t.width, H = t.height;
                var px = t.GetPixels();
                var lum = new float[W * H]; var wood = new float[W * H];
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i];
                    float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                    lum[i] = 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;
                    wood[i] = mx > 0.05f && (mx - mn) / mx > 0.25f && c.r >= c.g && c.g >= c.b * 0.8f ? 1f : 0f;   // 赤み・黄みのある画素
                }
                float best = float.MaxValue;
                for (int y = 0; y + WoodWin <= H; y += 2)
                    for (int x = 0; x + WoodWin <= W; x += 2)
                    {
                        float wsum = 0f, edge = 0f; int n = 0;
                        for (int j = y; j < y + WoodWin; j++)
                            for (int i = x; i < x + WoodWin; i++)
                            {
                                int k = j * W + i; wsum += wood[k]; n++;
                                if (i + 1 < x + WoodWin) edge += Mathf.Abs(lum[k] - lum[k + 1]) > 0.12f ? 1f : 0f;
                                if (j + 1 < y + WoodWin) edge += Mathf.Abs(lum[k] - lum[k + W]) > 0.12f ? 1f : 0f;
                            }
                        float score = (1f - wsum / n) * 4f + edge / n * 3f;
                        if (score < best) { best = score; v = new Vector4(x / (float)W, y / (float)H, WoodWin / (float)W, WoodWin / (float)H); }
                    }
                Main.Log($"[skin] wood window {tex.name}: best={best:F3} at ({v.x:F3},{v.y:F3})");
            }
            catch (Exception e) { Main.Log($"[skin] wood window {tex?.name} failed: {e.Message}"); }
            if (t != null) UnityEngine.Object.Destroy(t);
            return _woodWindow[tex.Pointer] = v;
        }

        // 銃の描画物の中から材質の元を選ぶ：部品のすぐ近くにある、ある程度大きな描画物（銃口の部品は銃身・スライドを優先）
        static Material Source(Transform gun, Role role, Bounds part)
        {
            Renderer best = null; float bestScore = float.MaxValue;
            string[] keys = role == Role.Barrel ? new[] { "barrel", "muzzle", "cannon", "slide", "slider" } : Array.Empty<string>();
            foreach (var r in gun.GetComponentsInChildren<Renderer>(true))
            {
                // 表示の判定は銃の中だけで（カスタマイズ画面の複製は、非表示の親の下で組み立てる）
                if (!r.enabled || !ActiveUnder(r.transform, gun) || !SightLab.IsMesh(r)) continue;
                bool ours = false;
                for (var t = r.transform; t != null && t != gun; t = t.parent) if (t.name.StartsWith(Mount.Prefix) || t.name.StartsWith("FPR_")) { ours = true; break; }
                if (ours) continue;
                var m = r.sharedMaterial;
                if (m == null || !m.HasProperty("_BaseMap") || m.GetTexture("_BaseMap") == null || m.shader == null || !m.shader.name.Contains("Lit")) continue;
                if (m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f) continue;   // 透明は除く
                var b = SightLab.LocalBounds(r, gun);
                float size = b.size.x + b.size.y + b.size.z;
                if (size < 0.06f) continue;   // ねじ・小さな部品は除く
                var tex = m.GetTexture("_BaseMap");
                Window(tex);
                if (_greyFrac.TryGetValue(tex.Pointer, out var gf) && gf < 0.5f) continue;   // 木・色付きの樹脂が主のテクスチャは除く
                // 部品の範囲との距離（重なっていれば 0）
                var d = Vector3.Max(Vector3.zero, Vector3.Max(b.min - part.max, part.min - b.max)).magnitude;
                float score = d - Mathf.Min(size, 0.6f) * 0.05f;
                var n = r.name.ToLowerInvariant();
                foreach (var k in keys) if (n.Contains(k)) { score -= 0.05f; break; }
                if (score < bestScore) { bestScore = score; best = r; }
            }
            if (best != null) Main.Log($"[skin] source for {role}: '{best.name}' mat='{best.sharedMaterial.name}'");
            return best?.sharedMaterial;
        }

        static bool ActiveUnder(Transform t, Transform gun)
        {
            for (; t != null && t != gun; t = t.parent) if (!t.gameObject.activeSelf) return false;
            return t == null || t.gameObject.activeSelf;
        }

        static float MaxScale(Transform t, Transform gun)
        {
            var a = t.lossyScale; var g = gun.lossyScale;
            return Mathf.Max(Mathf.Abs(a.x / g.x), Mathf.Abs(a.y / g.y), Mathf.Abs(a.z / g.z));
        }

        // 銃の本体のつや（0〜1）：材質の滑らかさ × つやのテクスチャ（A）の平均
        static readonly Dictionary<IntPtr, float> _gunGloss = new();
        public static float GunGloss(Transform gun)
        {
            var m = Source(gun, Role.Body, new Bounds(Vector3.zero, Vector3.one * 0.3f));
            if (m == null) return 0.5f;
            if (_gunGloss.TryGetValue(m.Pointer, out var g)) return g;
            g = m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness") : 0.5f;
            var map = m.HasProperty("_MetallicGlossMap") ? m.GetTexture("_MetallicGlossMap") : null;
            if (map != null && m.IsKeywordEnabled("_METALLICSPECGLOSSMAP")) g *= MeanAlpha(map);
            Main.Log($"[skin] gun gloss {gun.name}: {g:F2} (mat '{m.name}')");
            return _gunGloss[m.Pointer] = g;
        }

        public static float MeanAlpha(Texture tex)
        {
            var rt = RenderTexture.GetTemporary(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var t = new Texture2D(32, 32, TextureFormat.RGBA32, false, true);
            t.ReadPixels(new Rect(0, 0, 32, 32), 0, 0); t.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            float s = 0f; var px = t.GetPixels();
            foreach (var p in px) s += p.a;
            UnityEngine.Object.Destroy(t);
            return px.Length > 0 ? s / px.Length : 1f;
        }

        // 仕上げの調整（光沢・明るさの倍率）。窓の無地の所は、銃の汚れ・影（焼き込み）が無いぶん明るく、光沢も強く見えるので抑える
        static (float gloss, float tint) Finish(Role r) => r switch
        {
            Role.Body => (0.55f, 0.86f),     // ハンドガード・レール・台・ストック
            Role.Barrel => (0.70f, 0.92f),   // 銃口の部品・サイトのレール
            _ => (0.60f, 0.90f),             // グリップ（樹脂）
        };

        static Material Skinned(Material src, float sizeMeters, float scale, Role role)
        {
            var tex = src.GetTexture("_BaseMap");
            var w = Window(tex);
            // 部品のメッシュの UV（メッシュの単位）→ 窓：1 辺 WindowMeters（実際の大きさ）を窓の 1 辺に。大きすぎる部品は窓に収める
            float span = Mathf.Max(WindowMeters, sizeMeters);
            float uvPerUnit = w.z / span * scale;   // メッシュの 1 単位 = 実物の scale m
            string key = $"{src.Pointer}_{uvPerUnit:F3}_{role}";
            if (_mats.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(src) { name = Mount.Prefix + "skin_" + src.name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            // UV は 0 から始まる（BoxMesh で最小の角を 0 に）
            var st = new Vector4(uvPerUnit, uvPerUnit * w.w / w.z, w.x, w.y);
            foreach (var p in new[] { "_BaseMap", "_MainTex" })
                if (m.HasProperty(p)) { m.SetTextureScale(p, new Vector2(st.x, st.y)); m.SetTextureOffset(p, new Vector2(st.z, st.w)); }
            var (gloss, tint) = Finish(role);
            foreach (var p in new[] { "_Smoothness", "_GlossMapScale", "_Glossiness" })
                if (m.HasProperty(p)) m.SetFloat(p, m.GetFloat(p) * gloss);
            foreach (var p in new[] { "_BaseColor", "_Color" })
                if (m.HasProperty(p)) { var c = m.GetColor(p); m.SetColor(p, new Color(c.r * tint, c.g * tint, c.b * tint, c.a)); }
            _mats[key] = m;
            Main.Log($"[skin] material from '{src.name}' ({tex.name}) window=({w.x:F3},{w.y:F3},{w.z:F3}) size={sizeMeters:F3}m");
            return m;
        }

        // 無地の金属の窓（UV）。銃の主な色（彩度の低い画素の明るさの中央値）に近く、境目（隣の画素との差）が少ない所
        public static Vector4 Window(Texture tex) => Window(tex, Win);
        public static Vector4 Window(Texture tex, int win)
        {
            if (_window.TryGetValue((tex.Pointer, win), out var v)) return v;
            v = new Vector4(0.4f, 0.4f, win / (float)Probe, win / (float)Probe);
            Texture2D t = null;
            try
            {
                t = SkinLab.Readable(tex, Probe);
                int W = t.width, H = t.height;
                var px = t.GetPixels();
                var lum = new float[W * H]; var sat = new float[W * H];
                var grey = new List<float>();
                var cols = new List<Color>();
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i];
                    float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                    lum[i] = 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;
                    sat[i] = mx > 0.02f ? (mx - mn) / mx : 0f;
                    if (sat[i] < 0.35f && lum[i] > 0.03f) { grey.Add(lum[i]); cols.Add(c); }
                }
                _greyFrac[tex.Pointer] = grey.Count / (float)px.Length;
                var sorted = new List<float>(grey); sorted.Sort();
                float dom = sorted.Count > 0 ? sorted[sorted.Count / 2] : 0.3f;
                // 主な色：明るさが中央値の近くの画素の平均（色味も合わせる）
                Color domC = new(dom, dom, dom); int dn = 0; float sr = 0, sg = 0, sb = 0;
                for (int i = 0; i < cols.Count; i++) if (Mathf.Abs(grey[i] - dom) < 0.06f) { sr += cols[i].r; sg += cols[i].g; sb += cols[i].b; dn++; }
                if (dn > 0) domC = new Color(sr / dn, sg / dn, sb / dn);
                int wh = win, ww = Mathf.Max(4, win * W / Probe);
                float best = float.MaxValue;
                for (int y = 0; y + wh <= H; y += 2)
                    for (int x = 0; x + ww <= W; x += 2)
                    {
                        float sl = 0f, ss = 0f, edge = 0f, cr = 0f, cg = 0f, cb = 0f; int n = 0;
                        for (int j = y; j < y + wh; j++)
                            for (int i = x; i < x + ww; i++)
                            {
                                int k = j * W + i; sl += lum[k]; ss += sat[k]; n++; cr += px[k].r; cg += px[k].g; cb += px[k].b;
                                if (i + 1 < x + ww) edge += Mathf.Abs(lum[k] - lum[k + 1]);
                                if (j + 1 < y + wh) edge += Mathf.Abs(lum[k] - lum[k + W]);
                            }
                        float ml = sl / n, ms = ss / n;
                        float dc = Mathf.Abs(cr / n - domC.r) + Mathf.Abs(cg / n - domC.g) + Mathf.Abs(cb / n - domC.b);
                        float score = Mathf.Abs(ml - dom) * 3f + dc * 2f + edge / n * 6f + Mathf.Max(0f, ms - 0.25f) * 4f;
                        if (score < best) { best = score; v = new Vector4(x / (float)W, y / (float)H, ww / (float)W, wh / (float)H); }
                    }
                Main.Log($"[skin] window {tex.name}: dom={dom:F2} color=({domC.r:F2},{domC.g:F2},{domC.b:F2}) best={best:F3} at ({v.x:F3},{v.y:F3})");
            }
            catch (Exception e) { Main.Log($"[skin] window {tex?.name} failed: {e.Message}"); }
            if (t != null) UnityEngine.Object.Destroy(t);
            return _window[(tex.Pointer, win)] = v;
        }

        // 箱の投影の UV：面の向きで一番近い軸を捨て、残りの 2 軸の位置（最小の角を 0）を UV に
        static Mesh BoxMesh(Mesh src)
        {
            if (_boxMeshes.TryGetValue(src.Pointer, out var m) && m != null) return m;
            m = UnityEngine.Object.Instantiate(src);
            m.name = src.name + "_box";
            var vs = m.vertices; var ns = m.normals;
            var mn = m.bounds.min;
            var uv = new Vector2[vs.Length];
            for (int i = 0; i < vs.Length; i++)
            {
                var p = vs[i] - mn; var n = ns.Length == vs.Length ? ns[i] : Vector3.up;
                float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
                uv[i] = ax >= ay && ax >= az ? new Vector2(p.z, p.y) : ay >= az ? new Vector2(p.z, p.x) : new Vector2(p.x, p.y);
            }
            m.uv = uv;
            m.RecalculateTangents();
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return _boxMeshes[src.Pointer] = m;
        }
    }
}
