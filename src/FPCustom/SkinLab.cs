using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 銃の材質の下調べ（autotest -Dev -Scenario custskin）。武器ごとに描画物の材質（シェーダー・テクスチャ・色・金属感）を記録し、
    // 色のテクスチャを縮小して保存する（調べるためだけ。配布物には入れない）
    public static class SkinLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        public static void Register() => TestMode.ExternalScenarios["custskin"] = Steps;

        public static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "skin_*.png")) File.Delete(f); }));
            foreach (var wi in Tracker.Inv.WeaponData.Weapons.ToArray())
            {
                var w = wi;
                if (!Catalog.Eligible(w)) continue;
                s.Add(($"skin {w.Id}", () => Dump(w)));
            }
            return s;
        }

        static readonly HashSet<IntPtr> _saved = new();

        static void Dump(Il2Cpp.WeaponItem wi)
        {
            var prefab = wi.Weapon?.gameObject;
            if (prefab == null) return;
            var root = prefab.transform;
            Main.Log($"[skin] ===== [{wi.Id}] {wi.GetName()}");
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (!SightLab.IsMesh(r) || !Lab.IsShown(r.transform, root) || !r.enabled) continue;
                var sk = r.TryCast<SkinnedMeshRenderer>();
                Mesh m = sk != null ? sk.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                var lb = SightLab.LocalBounds(r, root);
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;
                    string T(string p) { if (!mat.HasProperty(p)) return "-"; var t = mat.GetTexture(p); return t == null ? "none" : $"{t.name}({t.width}x{t.height})"; }
                    string F(string p) => mat.HasProperty(p) ? mat.GetFloat(p).ToString("F2") : "-";
                    var bc = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
                    var st = mat.HasProperty("_BaseMap") ? mat.GetVector("_BaseMap_ST") : Vector4.zero;
                    Main.Log($"[skin]   '{SightLab.RelPath(r.transform, root)}' mesh='{m?.name}' verts={m?.vertexCount} readable={m?.isReadable} sub={i} size=({lb.size.x:F3},{lb.size.y:F3},{lb.size.z:F3}) center=({lb.center.x:F3},{lb.center.y:F3},{lb.center.z:F3}) " +
                             $"mat='{mat.name}' shader='{mat.shader?.name}' base={T("_BaseMap")} st={st} color=({bc.r:F2},{bc.g:F2},{bc.b:F2}) bump={T("_BumpMap")} ms={T("_MetallicGlossMap")} occ={T("_OcclusionMap")} metal={F("_Metallic")} smooth={F("_Smoothness")} " +
                             $"kw=[{string.Join(",", mat.shaderKeywords)}]");
                    var tex = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;
                    if (tex != null && _saved.Add(tex.Pointer)) SaveTex(tex, $"skin_{wi.Id:00}_{tex.name}");
                    var ms = mat.HasProperty("_MetallicGlossMap") ? mat.GetTexture("_MetallicGlossMap") : null;
                    if (ms != null && _saved.Add(ms.Pointer)) SaveTex(ms, $"skin_{wi.Id:00}_{ms.name}_ms");
                }
            }
        }

        // GPU で縮小コピーして保存（ゲームのテクスチャは CPU から読めない）
        public static Texture2D Readable(Texture src, int size)
        {
            int w = Mathf.Min(size, src.width), h = Mathf.Max(1, w * src.height / src.width);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, w, h), 0, 0); t.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            return t;
        }

        static void SaveTex(Texture src, string name)
        {
            try
            {
                var t = Readable(src, 512);
                File.WriteAllBytes(Path.Combine(ShotDir, string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".png"), ImageConversion.EncodeToPNG(t));
                UnityEngine.Object.Destroy(t);
            }
            catch (Exception e) { Main.Log($"[skin] save {name} failed: {e.Message}"); }
        }
    }
}
