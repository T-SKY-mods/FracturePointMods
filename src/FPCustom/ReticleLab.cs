using System;
using System.Collections.Generic;
using FPModOption;
using UnityEngine;
using UnityEngine.Rendering;

namespace FPCustom
{
    // サイトの照準の模様の材質を調べる（autotest -Dev -Scenario custreticle）
    public static class ReticleLab
    {
        public static void Register() => TestMode.ExternalScenarios["custreticle"] = () => new List<(string, Action)> { ("dump", Dump) };

        static void Dump()
        {
            foreach (var o in Optics.All)
            {
                var src = Tracker.ItemById(o.WeaponId)?.Weapon?.transform?.Find(o.Path);
                if (src == null) { Main.Log($"[ret] {o.Att}: no source"); continue; }
                foreach (var r in src.GetComponentsInChildren<Renderer>(true))
                {
                    var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                    Main.Log($"[ret] {o.Att} {r.name} verts={(mesh != null ? mesh.vertexCount : -1)} bounds={(mesh != null ? mesh.bounds.ToString("F4") : "-")} mats={r.sharedMaterials.Length}");
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        var sh = m.shader;
                        Main.Log($"[ret]   mat {m.name} shader={sh.name} queue={m.renderQueue} kw={string.Join(",", m.shaderKeywords)}");
                        int n = sh.GetPropertyCount();
                        for (int i = 0; i < n; i++)
                        {
                            var pn = sh.GetPropertyName(i); var pt = sh.GetPropertyType(i);
                            string v = pt switch
                            {
                                ShaderPropertyType.Color => m.GetColor(pn).ToString("F3"),
                                ShaderPropertyType.Vector => m.GetVector(pn).ToString("F3"),
                                ShaderPropertyType.Float or ShaderPropertyType.Range => m.GetFloat(pn).ToString("F4"),
                                ShaderPropertyType.Texture => m.GetTexture(pn) is { } t ? $"{t.name} {t.width}x{t.height} st={m.GetTextureScale(pn)}/{m.GetTextureOffset(pn)}" : "null",
                                _ => "?"
                            };
                            Main.Log($"[ret]     {pn} ({pt}) = {v}");
                        }
                    }
                }
            }
        }
    }
}
