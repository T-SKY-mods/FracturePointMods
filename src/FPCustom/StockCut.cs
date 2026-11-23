using System;
using System.Collections.Generic;
using System.IO;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // ストックが本体とひとつのメッシュの武器（QBZ 191・M16・Type 89）に、メイン武器のストックを付けるとき：
    //   今の形（ゲームのまま・サイト／ハンドガード／銃口の差し替えの形）から、ストックの三角形（3 頂点とも箱の中。武器の根元の座標）を除いた形に替える。
    //   ゲームのメッシュは読めないので、そのままの形を書き出した <Src>.fpm を元にする（tools/build_custom_assets.ps1）。
    //   ほかの差し替えより前に Undo で元の形に戻し（Mount.Apply の最初）、最後に Apply する。
    //   動くメッシュ（Type 89）：銃口と同じく骨ごとの形を骨の子に置き（MuzzleCut.SplitSkinned。銃口の部品を付けているときはもう置いてある）、それぞれを削る
    public static class StockCut
    {
        public sealed class Spec
        {
            public string Renderer;   // 武器の根元からの道
            public string Src;        // ゲームのままの形の読める版（.fpm の名前。動くメッシュは骨ごとの形の名前）
            public Vector3 Min, Max;  // 除く箱（武器の根元の座標）
            public bool Skinned;
            // 残す付け根（元のストックの色のまま機関部に付いている所）：3 頂点とも Min〜RecolorMax の中の三角形は、
            //   UV を同じテクスチャの無地の金属の所（Skin.Window）に移して、機関部の金属の色にする。null なら無し
            public Vector3? RecolorMax;
            // 長い棒（伸縮ストックの棒など、機関部の中へ入っていく細長い三角形）：3 頂点とも Min〜LongMax の中で、前後の長さが LongSpan より長い三角形も除く
            public Vector3? LongMax;
            public float LongSpan = 0.05f;
        }

        const string Prefix = "FPC_nostock_";
        static readonly Dictionary<IntPtr, Mesh> _orig = new();       // 削った形 → 元の形
        static readonly Dictionary<string, Mesh> _cut = new();        // 元の形＋箱 → 削った形

        // 削った形を元の形に戻す（動くメッシュの骨ごとの形は Mount が FPC_ の物と一緒に消す）
        public static void Undo(Transform root, int id)
        {
            if (!RifleStocks.Fits.TryGetValue(id, out var f) || f.Cut is not { } c) return;
            // 動くメッシュ：骨ごとの形は Mount が消したので、元の描画を戻す（MPS-5・Viper は銃口の差し替えが無く、ここで戻さないと銃が消えたままになる）
            if (c.Skinned) { var sk = root.Find(c.Renderer)?.GetComponent<SkinnedMeshRenderer>(); if (sk != null) sk.enabled = true; return; }
            var mf = root.Find(c.Renderer)?.GetComponent<MeshFilter>();
            if (mf?.sharedMesh != null && _orig.TryGetValue(mf.sharedMesh.Pointer, out var o) && o != null) mf.sharedMesh = o;
        }

        public static void Apply(Transform root, int id)
        {
            if (!RifleStocks.Fits.TryGetValue(id, out var f) || f.Cut is not { } c) return;
            try
            {
                if (c.Skinned) { ApplySkinned(root, c); return; }
                var mf = root.Find(c.Renderer)?.GetComponent<MeshFilter>();
                if (mf?.sharedMesh == null) { Main.Log($"[stockcut] [{id}] renderer not found: {c.Renderer}"); return; }
                var cur = mf.sharedMesh;
                if (_orig.ContainsKey(cur.Pointer)) return;   // もう削ってある
                var src = cur.isReadable ? cur : ModelLoader.LoadMesh(Path.Combine(Guards.ModelDir, c.Src + ".fpm"));
                var tex = mf.GetComponent<Renderer>()?.sharedMaterial?.GetTexture("_BaseMap");
                var win = c.RecolorMax != null && tex != null ? Skin.Window(tex) : Vector4.zero;
                var cut = Cut(src, root.worldToLocalMatrix * mf.transform.localToWorldMatrix, c, cur.name, win);
                _orig[cut.Pointer] = cur;
                mf.sharedMesh = cut;
            }
            catch (Exception e) { Core.LogOnce("stockcut" + id, $"[stockcut] [{id}] failed: {e.Message}"); }
        }

        static void ApplySkinned(Transform root, Spec c)
        {
            var sk = root.Find(c.Renderer)?.GetComponent<SkinnedMeshRenderer>();
            if (sk == null) return;
            if (sk.enabled) MuzzleCut.SplitSkinned(sk, c.Src);   // 銃口の部品が無いとき：ゲームのままの形を骨ごとに置く
            foreach (var b in sk.bones)
            {
                if (b == null) continue;
                for (int i = 0; i < b.childCount; i++)
                {
                    var t = b.GetChild(i);
                    if (!t.name.StartsWith(Mount.Prefix + MuzzleCut.PartName)) continue;
                    var mf = t.GetComponent<MeshFilter>();
                    if (mf?.sharedMesh == null || _orig.ContainsKey(mf.sharedMesh.Pointer)) continue;
                    var cur = mf.sharedMesh;
                    var cut = Cut(cur, root.worldToLocalMatrix * t.localToWorldMatrix, c, cur.name + "_" + i, Vector4.zero);
                    _orig[cut.Pointer] = cur;
                    mf.sharedMesh = cut;
                }
            }
        }

        // ---------- 切り口のふた ----------
        //   ストックを除くと機関部の後ろの端が開いたままになり（ゲームの形は片面だけ描くので）、構えたとき後ろから中が透けて見える。
        //   切り口の面（z = Max.z）に、そこに残った形の断面（前後 ±1cm の頂点の外形）の板を置いて塞ぐ。色は銃の材質の無地の金属の所
        public const string CapName = "rstock_cap";
        public static int LastCapPointsForTest;
        public static void Cap(Transform root, int id)
        {
            if (!RifleStocks.Fits.TryGetValue(id, out var f) || f.Cut is not { } c) return;
            try
            {
                var pts = new List<Vector2>();
                Material mat = null; Renderer orig = root.Find(c.Renderer)?.GetComponent<Renderer>();
                void Take(Mesh m, Matrix4x4 toRoot)
                {
                    if (m == null || !m.isReadable) return;
                    var v = m.vertices;
                    for (int s2 = 0; s2 < m.subMeshCount; s2++)
                        foreach (var i in m.GetTriangles(s2))
                        {
                            var p = toRoot.MultiplyPoint3x4(v[i]);
                            if (Mathf.Abs(p.z - c.Max.z) <= 0.01f && p.y >= c.Min.y && p.y <= c.Max.y && p.x >= c.Min.x && p.x <= c.Max.x) pts.Add(new Vector2(p.x, p.y));
                        }
                }
                if (c.Skinned)
                {
                    var sk = orig?.TryCast<SkinnedMeshRenderer>();
                    if (sk != null) foreach (var b in sk.bones)
                    {
                        if (b == null) continue;
                        for (int i = 0; i < b.childCount; i++)
                        {
                            var t = b.GetChild(i);
                            if (!t.name.StartsWith(Mount.Prefix + MuzzleCut.PartName)) continue;
                            Take(t.GetComponent<MeshFilter>()?.sharedMesh, root.worldToLocalMatrix * t.localToWorldMatrix);
                        }
                    }
                }
                else if (orig != null) Take(orig.GetComponent<MeshFilter>()?.sharedMesh, root.worldToLocalMatrix * orig.transform.localToWorldMatrix);
                var hull = Hull(pts);
                LastCapPointsForTest = hull.Count;
                if (hull.Count < 3) { Main.Log($"[stockcut] [{id}] cap: no section ({pts.Count} points)"); return; }
                // 材質：元の描画物のいちばん三角形の多いサブメッシュの材質（本体）
                if (orig != null)
                {
                    var ms = orig.sharedMaterials; Mesh om = orig.TryCast<SkinnedMeshRenderer>()?.sharedMesh ?? orig.GetComponent<MeshFilter>()?.sharedMesh; long best = -1;
                    for (int i = 0; i < ms.Length; i++)
                    {
                        long n = om != null && i < om.subMeshCount ? (long)om.GetIndexCount(i) : 0;
                        if (ms[i] != null && n > best) { best = n; mat = ms[i]; }
                    }
                }
                var tex = mat != null && mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;
                var win = tex != null ? Skin.Window(tex) : new Vector4(0.4f, 0.4f, 0.05f, 0.05f);
                // 板：中心から扇形。後ろ向き（−Z）と前向きの両面。少し後ろ（0.5mm）に置いて、残った形とちらつかないように
                float z = c.Max.z - 0.0005f;
                var cen = Vector2.zero; foreach (var h in hull) cen += h; cen /= hull.Count;
                var verts = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
                Vector2 Uv(Vector2 q) => new(win.x + win.z * (0.1f + 0.8f * Mathf.Repeat(q.x / 0.02f, 1f)), win.y + win.w * (0.1f + 0.8f * Mathf.Repeat(q.y / 0.02f, 1f)));
                for (int side = 0; side < 2; side++)
                {
                    int b0 = verts.Count; var nn = side == 0 ? Vector3.back : Vector3.forward;
                    verts.Add(new Vector3(cen.x, cen.y, z)); nrm.Add(nn); uv.Add(Uv(cen));
                    foreach (var h in hull) { verts.Add(new Vector3(h.x, h.y, z)); nrm.Add(nn); uv.Add(Uv(h)); }
                    for (int i = 0; i < hull.Count; i++)
                    {
                        int a2 = b0 + 1 + i, b2 = b0 + 1 + (i + 1) % hull.Count;
                        if (side == 0) { tri.Add(b0); tri.Add(a2); tri.Add(b2); } else { tri.Add(b0); tri.Add(b2); tri.Add(a2); }
                    }
                }
                var m = new Mesh { name = Prefix + "cap_" + id };
                m.vertices = verts.ToArray(); m.normals = nrm.ToArray(); m.uv = uv.ToArray(); m.triangles = tri.ToArray(); m.RecalculateBounds(); m.RecalculateTangents();
                var go = new GameObject(Mount.Prefix + CapName) { layer = root.gameObject.layer };
                go.transform.SetParent(root, false);
                go.AddComponent<MeshFilter>().sharedMesh = m;
                var r = go.AddComponent<MeshRenderer>();
                if (mat != null) r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var bb = m.bounds;
                Main.Log($"[stockcut] [{id}] cap: {hull.Count} corners from {pts.Count} points, x {bb.min.x:0.000}..{bb.max.x:0.000} y {bb.min.y:0.000}..{bb.max.y:0.000} z {z:0.0000} mat {mat?.name}");
            }
            catch (Exception e) { Core.LogOnce("stockcap" + id, $"[stockcut] [{id}] cap failed: {e.Message}"); }
        }

        // 2D の凸包（単調連鎖）。反時計回り
        static List<Vector2> Hull(List<Vector2> p)
        {
            var pts = new List<Vector2>(p); pts.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            var h = new List<Vector2>();
            if (pts.Count < 3) return h;
            static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            for (int pass = 0; pass < 2; pass++)
            {
                int start = h.Count;
                for (int k = 0; k < pts.Count; k++)
                {
                    var q = pass == 0 ? pts[k] : pts[pts.Count - 1 - k];
                    while (h.Count >= start + 2 && Cross(h[h.Count - 2], h[h.Count - 1], q) <= 1e-9f) h.RemoveAt(h.Count - 1);
                    h.Add(q);
                }
                h.RemoveAt(h.Count - 1);
            }
            return h;
        }

        public static int LastRecoloredForTest;
        static bool In(Vector3 p, Vector3 mn, Vector3 mx) => p.x >= mn.x && p.x <= mx.x && p.y >= mn.y && p.y <= mx.y && p.z >= mn.z && p.z <= mx.z;

        static Mesh Cut(Mesh src, Matrix4x4 toRoot, Spec c, string label, Vector4 win)
        {
            string key = $"{src.Pointer}|{toRoot.m03:F4},{toRoot.m13:F4},{toRoot.m23:F4}|{c.Min}|{c.Max}|{c.RecolorMax}|{c.LongMax}|{win}";
            if (_cut.TryGetValue(key, out var have) && have != null) return have;
            var v = src.vertices;
            int n = v.Length;
            var inBox = new bool[n]; var inRe = new bool[n]; var pr = new Vector3[n];
            var rmax = c.RecolorMax ?? c.Max;
            bool recolor = c.RecolorMax != null && win.z > 0f;
            for (int i = 0; i < n; i++)
            {
                var p = pr[i] = toRoot.MultiplyPoint3x4(v[i]);
                inBox[i] = p.x >= c.Min.x && p.x <= c.Max.x && p.y >= c.Min.y && p.y <= c.Max.y && p.z >= c.Min.z && p.z <= c.Max.z;
                inRe[i] = recolor && p.x >= c.Min.x && p.x <= rmax.x && p.y >= c.Min.y && p.y <= rmax.y && p.z >= c.Min.z && p.z <= rmax.z;
            }
            var verts = new List<Vector3>(v);
            var nrm = src.normals; var uv = src.uv; var tan = src.tangents;
            var nrmL = nrm.Length == n ? new List<Vector3>(nrm) : null;
            var uvL = uv.Length == n ? new List<Vector2>(uv) : null;
            var tanL = tan.Length == n ? new List<Vector4>(tan) : null;
            // 付け根の三角形の頂点の複製（UV を金属の所に。箱の投影：面の向きで 2 軸、2cm で窓の 1 辺）
            var dup = new Dictionary<int, int>();
            int Dup(int i)
            {
                if (dup.TryGetValue(i, out var j)) return j;
                j = verts.Count; verts.Add(v[i]);
                if (nrmL != null) nrmL.Add(nrm[i]);
                if (tanL != null) tanL.Add(tan[i]);
                if (uvL != null)
                {
                    var nn = nrmL != null ? toRoot.MultiplyVector(nrm[i]) : Vector3.forward; var p = pr[i];
                    float ax = Mathf.Abs(nn.x), ay = Mathf.Abs(nn.y), az = Mathf.Abs(nn.z);
                    var q = ax >= ay && ax >= az ? new Vector2(p.z, p.y) : ay >= az ? new Vector2(p.z, p.x) : new Vector2(p.x, p.y);
                    float fu = Mathf.Repeat(q.x / 0.02f, 1f), fv = Mathf.Repeat(q.y / 0.02f, 1f);
                    uvL.Add(new Vector2(win.x + win.z * (0.1f + 0.8f * fu), win.y + win.w * (0.1f + 0.8f * fv)));
                }
                return dup[i] = j;
            }
            var m = new Mesh { name = Prefix + label, indexFormat = src.indexFormat };
            m.subMeshCount = src.subMeshCount;
            int removed = 0, recolored = 0;
            var subs = new List<int[]>();
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var tri = src.GetTriangles(s);
                var keep = new List<int>(tri.Length);
                for (int k = 0; k + 2 < tri.Length; k += 3)
                {
                    int a = tri[k], b = tri[k + 1], d = tri[k + 2];
                    if (inBox[a] && inBox[b] && inBox[d]) { removed++; continue; }
                    if (c.LongMax is Vector3 lm && In(pr[a], c.Min, lm) && In(pr[b], c.Min, lm) && In(pr[d], c.Min, lm)
                        && Mathf.Min(pr[a].z, Mathf.Min(pr[b].z, pr[d].z)) < c.Max.z - 0.01f   // ストックの所（機関部より 1cm 以上後ろ）から始まる三角形だけ
                        && Mathf.Max(pr[a].z, Mathf.Max(pr[b].z, pr[d].z)) - Mathf.Min(pr[a].z, Mathf.Min(pr[b].z, pr[d].z)) > c.LongSpan) { removed++; continue; }
                    if (inRe[a] && inRe[b] && inRe[d] && uvL != null) { recolored++; a = Dup(a); b = Dup(b); d = Dup(d); }
                    keep.Add(a); keep.Add(b); keep.Add(d);
                }
                subs.Add(keep.ToArray());
            }
            if (verts.Count > 65535) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = verts.ToArray();
            if (nrmL != null) m.normals = nrmL.ToArray();
            if (uvL != null) m.uv = uvL.ToArray();
            if (tanL != null) m.tangents = tanL.ToArray();
            for (int s = 0; s < subs.Count; s++) m.SetTriangles(subs[s], s);
            m.RecalculateBounds();
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            LastRecoloredForTest = recolored;
            Main.Log($"[stockcut] {label}: removed {removed} triangles, recolored {recolored}");
            return _cut[key] = m;
        }
    }
}
