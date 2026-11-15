using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // 調査用：ストックに手・腕がめり込んでいないかを測る（StanceLab の撮影のたびに呼ぶ）。
    //   ストックの模型の model.json にある「当たりの確認用の簡単な形」（proxy。tools/meshparts.py が書く）の中に、
    //   腕の描画物（骨で動くメッシュをその瞬間の形に焼いたもの）の頂点がどれだけ深く入っているかを見る。
    //   頂点は近い骨で「右手首・右前腕・左手首・左前腕」に分ける
    public static class ClipLab
    {
        sealed class Shape { public string Kind; public float[] A; public Vector2[] Poly; }
        static readonly Dictionary<string, List<Shape>> _proxies = new();
        static readonly Dictionary<IntPtr, Mesh> _bake = new();
        static Dictionary<string, Transform> _bones;

        public sealed class Hit { public string Part; public float Depth; public Vector3 Local; public string Model; }

        static List<Shape> Proxy(string dir)
        {
            if (_proxies.TryGetValue(dir, out var l)) return l;
            l = new List<Shape>();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "model.json")));
                if (doc.RootElement.TryGetProperty("proxy", out var arr))
                    foreach (var e in arr.EnumerateArray())
                    {
                        var kind = e[0].GetString();
                        var s = new Shape { Kind = kind };
                        if (kind == "box") s.A = e[1].EnumerateArray().Concat(e[2].EnumerateArray()).Select(x => x.GetSingle()).ToArray();
                        else if (kind == "slab") { s.A = new[] { e[1].GetSingle() }; s.Poly = e[2].EnumerateArray().Select(q => new Vector2(q[0].GetSingle(), q[1].GetSingle())).ToArray(); }
                        else if (kind == "poly") { s.A = new[] { e[1].GetSingle(), e[2].GetSingle() }; s.Poly = e[3].EnumerateArray().Select(q => new Vector2(q[0].GetSingle(), q[1].GetSingle())).ToArray(); }
                        else s.A = e.EnumerateArray().Skip(1).Select(x => x.GetSingle()).ToArray();
                        l.Add(s);
                    }
            }
            catch (Exception ex) { Main.Log("[clip] proxy failed " + ex.Message); }
            return _proxies[dir] = l;
        }

        // 形の中への深さ（m。外なら 0）。p は模型の座標
        static float Depth(Shape s, Vector3 p)
        {
            var a = s.A;
            switch (s.Kind)
            {
                case "box":
                    if (p.x < a[0] || p.x > a[3] || p.y < a[1] || p.y > a[4] || p.z < a[2] || p.z > a[5]) return 0f;
                    return Mathf.Min(Mathf.Min(p.x - a[0], a[3] - p.x), Mathf.Min(Mathf.Min(p.y - a[1], a[4] - p.y), Mathf.Min(p.z - a[2], a[5] - p.z)));
                case "loft":
                {
                    // 断面 (z, ylo, yhi, w) を 2 つつないだ形
                    float z0 = a[0], z1 = a[4];
                    float t = Mathf.Abs(z1 - z0) < 1e-6f ? 0f : (p.z - z0) / (z1 - z0);
                    if (t < 0f || t > 1f) return 0f;
                    float ylo = Mathf.Lerp(a[1], a[5], t), yhi = Mathf.Lerp(a[2], a[6], t), w = Mathf.Lerp(a[3], a[7], t);
                    if (p.y < ylo || p.y > yhi || Mathf.Abs(p.x) > w / 2f) return 0f;
                    return Mathf.Min(Mathf.Min(p.y - ylo, yhi - p.y), w / 2f - Mathf.Abs(p.x));
                }
                case "bar":
                {
                    // 平たい棒：中心線 (y0,z0)→(y1,z1)、左右の位置 a[0]、幅 w（左右）・高さ h
                    float cx = a[0], y0 = a[1], z0 = a[2], y1 = a[3], z1 = a[4], w = a[5], h = a[6];
                    var d = new Vector2(z1 - z0, y1 - y0); float len = d.magnitude;
                    if (len < 1e-6f) return 0f;
                    d /= len;
                    var q = new Vector2(p.z - z0, p.y - y0);
                    float along = Vector2.Dot(q, d), side = Mathf.Abs(q.x * d.y - q.y * d.x);
                    if (along < 0f || along > len || side > h / 2f || Mathf.Abs(p.x - cx) > w / 2f) return 0f;
                    return Mathf.Min(h / 2f - side, w / 2f - Mathf.Abs(p.x - cx));
                }
                case "slab":
                {
                    // 横から見た多角形（(z, y)。へこみがあってもよい）を左右に厚み a[0] で押し出した板
                    float hw = a[0] / 2f;
                    if (Mathf.Abs(p.x) > hw) return 0f;
                    var poly = s.Poly; int n = poly.Length; bool inside = false; float edge = float.MaxValue;
                    var q = new Vector2(p.z, p.y);
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        var u = poly[i]; var v = poly[j];
                        if ((u.y > q.y) != (v.y > q.y) && q.x < (v.x - u.x) * (q.y - u.y) / (v.y - u.y) + u.x) inside = !inside;
                        var e = v - u; float t = Mathf.Clamp01(Vector2.Dot(q - u, e) / Mathf.Max(1e-10f, e.sqrMagnitude));
                        edge = Mathf.Min(edge, (q - (u + e * t)).magnitude);
                    }
                    return inside ? Mathf.Min(edge, hw - Mathf.Abs(p.x)) : 0f;
                }
                case "poly":
                {
                    if (p.x < a[0] || p.x > a[1]) return 0f;
                    float best = Mathf.Min(p.x - a[0], a[1] - p.x);
                    var poly = s.Poly; int n = poly.Length; float sign = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        var u = poly[i]; var v = poly[(i + 1) % n];
                        var e = v - u; float el = e.magnitude; if (el < 1e-7f) continue;
                        float cr = (e.x * (p.y - u.y) - e.y * (p.z - u.x)) / el;   // 辺からの符号付きの距離（(z, y) の平面）
                        if (sign == 0f) sign = Mathf.Sign(cr);
                        if (cr * sign < 0f) return 0f;
                        best = Mathf.Min(best, Mathf.Abs(cr));
                    }
                    return best;
                }
            }
            return 0f;
        }

        static readonly Dictionary<IntPtr, string[]> _vertPart = new();
        static string PartOf(SkinnedMeshRenderer sk, int vertex)
        {
            if (!_vertPart.TryGetValue(sk.Pointer, out var parts))
            {
                var mesh = sk.sharedMesh; var bones = sk.bones; var bw = mesh.boneWeights;
                parts = new string[mesh.vertexCount];
                for (int i = 0; i < parts.Length; i++)
                {
                    string p = "body:" + sk.name;
                    if (i < bw.Length)
                    {
                        var b = bw[i];
                        int bi = b.boneIndex0; float best = b.weight0;
                        if (b.weight1 > best) { best = b.weight1; bi = b.boneIndex1; }
                        if (b.weight2 > best) { best = b.weight2; bi = b.boneIndex2; }
                        if (b.weight3 > best) { bi = b.boneIndex3; }
                        // 骨の名前は 2 通りある（Right_Hand / hand_l、Right_LowerArm / lowerarm_twist_01_r など）
                        var n = (bi >= 0 && bi < bones.Length ? bones[bi]?.name ?? "" : "").ToLowerInvariant();
                        string side = n.StartsWith("left") || n.EndsWith("_l") ? "L" : n.StartsWith("right") || n.EndsWith("_r") ? "R" : null;
                        if (side != null)
                        {
                            if (n.Contains("lowerarm") || n.Contains("forearm")) p = side + " forearm";
                            else if (n.Contains("upperarm") || n.Contains("shoulder") || n.Contains("clavicle")) p = "body:" + side + " upper arm";
                            else if (n.Contains("hand") || n.Contains("thumb") || n.Contains("index") || n.Contains("middle") || n.Contains("ring") || n.Contains("pinky")) p = side + " hand";
                        }
                    }
                    parts[i] = p;
                }
                var cnt = new Dictionary<string, int>();
                for (int i = 0; i < Math.Min(parts.Length, bw.Length); i++)
                {
                    var b = bw[i]; int bi = b.boneIndex0;
                    var n = bi >= 0 && bi < bones.Length ? bones[bi]?.name ?? "?" : "?";
                    cnt[n] = cnt.TryGetValue(n, out var c) ? c + 1 : 1;
                }
                Main.Log($"[clip] '{sk.name}' bones: " + string.Join(", ", cnt.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
                _vertPart[sk.Pointer] = parts;
                Main.Log($"[clip] '{sk.name}': {parts.Count(x => x.EndsWith("hand"))} hand, {parts.Count(x => x.EndsWith("forearm"))} forearm, {parts.Count(x => x.StartsWith("body"))} body vertices");
            }
            return vertex < parts.Length ? parts[vertex] : "body:" + sk.name;
        }

        static float SegDist(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a; float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
            return (p - (a + ab * t)).magnitude;
        }

        // 手に持っている武器のストックへの、手・腕のめり込み（部位ごとにいちばん深い所）
        public static List<Hit> Measure(DemoWeapon dw)
        {
            var hits = new Dictionary<string, Hit>();
            var fps = Refs.Fps;
            if (dw == null || fps == null) return new List<Hit>();
            var stock = dw.transform.Find(Mount.Prefix + "stock");
            if (stock == null) return new List<Hit>();
            if (_bones == null)
            {
                _bones = new Dictionary<string, Transform>();
                foreach (var tr in fps.GetComponentsInChildren<Transform>(true))
                    if (tr.name is "Right_Hand" or "Right_LowerArm" or "Right_UpperArm" or "Left_Hand" or "Left_LowerArm" or "Left_UpperArm") _bones[tr.name] = tr;
            }
            var models = new List<(Transform t, List<Shape> shapes, string name)>();
            foreach (var mr in stock.GetComponentsInChildren<MeshRenderer>(true))
                if (mr.name.StartsWith("FPR_")) { var dir = mr.name.Substring(4); models.Add((mr.transform, Proxy(dir), Path.GetFileName(dir))); }
            var segs = new (string name, Vector3 a, Vector3 b)[]
            {
                ("R hand", _bones["Right_Hand"].position, _bones["Right_Hand"].position + (_bones["Right_Hand"].position - _bones["Right_LowerArm"].position).normalized * 0.10f),
                ("R forearm", _bones["Right_LowerArm"].position, _bones["Right_Hand"].position),
                ("R upper arm", _bones["Right_UpperArm"].position, _bones["Right_LowerArm"].position),
                ("L hand", _bones["Left_Hand"].position, _bones["Left_Hand"].position + (_bones["Left_Hand"].position - _bones["Left_LowerArm"].position).normalized * 0.10f),
                ("L forearm", _bones["Left_LowerArm"].position, _bones["Left_Hand"].position),
                ("L upper arm", _bones["Left_UpperArm"].position, _bones["Left_LowerArm"].position),
            };
            foreach (var sk in fps.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!sk.enabled || sk.transform.IsChildOf(dw.transform)) continue;
                if ((sk.bounds.center - dw.transform.position).magnitude > 1.5f) continue;
                if (!_bake.TryGetValue(sk.Pointer, out var m) || m == null) { m = new Mesh(); _bake[sk.Pointer] = m; }
                sk.BakeMesh(m, true);
                var vs = m.vertices;
                var l2w = Matrix4x4.TRS(sk.transform.position, sk.transform.rotation, Vector3.one);
                var near = dw.transform.position;
                for (int i = 0; i < vs.Length; i++)
                {
                    var w = l2w.MultiplyPoint3x4(vs[i]);
                    if ((w - near).sqrMagnitude > 0.36f) continue;
                    foreach (var (t, shapes, name) in models)
                    {
                        var p = t.InverseTransformPoint(w);
                        float d = 0f;
                        for (int k = 0; k < shapes.Count; k++) { float dd = Depth(shapes[k], p); if (dd > d) d = dd; }
                        if (d <= 0f) continue;
                        // その頂点がいちばん強く付いている骨で部位を決める。手・前腕以外（上腕・肩・首・胸）は「body」（床尾は肩・胸に当てるもの）
                        string part = PartOf(sk, i);
                        if (!hits.TryGetValue(part, out var h) || d > h.Depth) hits[part] = new Hit { Part = part, Depth = d, Local = dw.transform.InverseTransformPoint(w), Model = name };
                    }
                }
            }
            return hits.Values.OrderByDescending(h => h.Depth).ToList();
        }

        // 確認：焼いた腕の頂点が骨の近くにあるか（座標の取り違えが無いか）
        public static float SanityForTest(DemoWeapon dw)
        {
            var fps = Refs.Fps; if (fps == null || _bones == null) return -1f;
            var target = _bones["Right_Hand"].position; float best = float.MaxValue;
            foreach (var sk in fps.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!sk.enabled || sk.transform.IsChildOf(dw.transform)) continue;
                var m = new Mesh(); sk.BakeMesh(m, true);
                var l2w = Matrix4x4.TRS(sk.transform.position, sk.transform.rotation, Vector3.one);
                foreach (var v in m.vertices) best = Mathf.Min(best, (l2w.MultiplyPoint3x4(v) - target).magnitude);
                UnityEngine.Object.Destroy(m);
            }
            return best;
        }
    }
}
