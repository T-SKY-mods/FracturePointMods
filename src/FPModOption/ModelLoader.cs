using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;

namespace FPModOption
{
    // Mod 用の3Dモデル（tools/convert_glb.py・extract_cut_mesh.py で作った .fpm / model.json / png）を読み込む。
    // 置き場所: UserData/FPRarity/models/<名前>/
    //
    // 材質はゲームの既存の材質を複製して作る（シェーダーの設定やビルドに含まれる描画パターンをそのまま使うため）。
    //   不透明 … 法線・金属感・陰影マップ付きの URP/Lit の材質（AK-74 の部品）
    //   透明   … 発光する透明の材質（Type 89 に隠れているホロサイトの照準の板）
    public static class ModelLoader
    {
        // モデルの置き場（Mod ごとのフォルダ。どの Mod の配布物に入っていても見つかるよう順に探す）
        static readonly string[] Dirs = { "FPRarity", "FPCustom", "FPModOption" };
        static string UserData => MelonLoader.Utils.MelonEnvironment.UserDataDirectory;
        public static string Dir => Path.Combine(UserData, "FPRarity", "models");

        // relPath（models からの相対パス）がある置き場のフルパス。どこにも無ければ最初の置き場のパス
        public static string Resolve(string relPath)
        {
            foreach (var d in Dirs)
            {
                var p = Path.Combine(UserData, d, "models", relPath);
                if (File.Exists(p) || Directory.Exists(p)) return p;
            }
            return Path.Combine(Dir, relPath);
        }

        public static bool Exists(string relPath) => File.Exists(Resolve(relPath));

        public class Model
        {
            public Mesh Mesh;
            public Material[] Materials;
            public string[] MaterialNames;
            public Bounds Bounds;
            public Bounds SubmeshBounds(int i) => Mesh.GetSubMesh(i).bounds;
            public int SubmeshOf(string materialName) => Array.IndexOf(MaterialNames, materialName);
        }

        static readonly Dictionary<string, Model> _models = new();
        static readonly Dictionary<string, Mesh> _meshes = new();
        static readonly Dictionary<string, Texture2D> _textures = new();

        // ---------- メッシュ (.fpm) ----------

        public static Mesh LoadMesh(string relPath)
        {
            if (_meshes.TryGetValue(relPath, out var cached) && cached != null) return cached;
            var path = Resolve(relPath);
            using var br = new BinaryReader(File.OpenRead(path));
            if (new string(br.ReadChars(4)) != "FPM1") throw new InvalidDataException("not FPM1: " + relPath);
            int n = br.ReadInt32(), flags = br.ReadInt32(), subCount = br.ReadInt32();
            var counts = new int[subCount];
            for (int i = 0; i < subCount; i++) counts[i] = br.ReadInt32();
            var pos = new Vector3[n];
            for (int i = 0; i < n; i++) pos[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            Vector3[] nrm = null; Vector2[] uv = null; Vector4[] tan = null;
            if ((flags & 1) != 0) { nrm = new Vector3[n]; for (int i = 0; i < n; i++) nrm[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()); }
            if ((flags & 2) != 0) { uv = new Vector2[n]; for (int i = 0; i < n; i++) uv[i] = new Vector2(br.ReadSingle(), br.ReadSingle()); }
            if ((flags & 4) != 0) { tan = new Vector4[n]; for (int i = 0; i < n; i++) tan[i] = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()); }

            var mesh = new Mesh { name = "FPR_" + Path.GetFileNameWithoutExtension(relPath) };
            if (n > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = pos;
            if (nrm != null) mesh.normals = nrm;
            if (uv != null) mesh.uv = uv;
            mesh.subMeshCount = subCount;
            for (int s = 0; s < subCount; s++)
            {
                var idx = new int[counts[s]];
                for (int i = 0; i < idx.Length; i++) idx[i] = br.ReadInt32();
                mesh.SetTriangles(idx, s);
            }
            if (nrm == null) mesh.RecalculateNormals();
            if (tan != null) mesh.tangents = tan; else if (uv != null) mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _meshes[relPath] = mesh;
            Core.Log($"[model] mesh {relPath}: verts={n} submeshes={subCount}");
            return mesh;
        }

        // ---------- モデル（メッシュ＋材質） ----------

        class MatJson
        {
            public string name { get; set; }
            public string kind { get; set; }   // null=通常, "reticle"=発光する照準の模様, "glass"=ガラス
            public string @base { get; set; }
            public float[] baseColor { get; set; }
            public string ms { get; set; }
            public float metallic { get; set; }
            public float smoothness { get; set; }
            public string normal { get; set; }
            public string occ { get; set; }
            public string emis { get; set; }
            public float[] emission { get; set; }
            public string alpha { get; set; }
        }

        class ModelJson
        {
            public MatJson[] materials { get; set; }
        }

        public static Model Load(string name)
        {
            if (_models.TryGetValue(name, out var m) && m.Mesh != null) return m;
            var dir = Resolve(name);
            var json = JsonSerializer.Deserialize<ModelJson>(File.ReadAllText(Path.Combine(dir, "model.json")));
            var mesh = LoadMesh(Path.Combine(name, "model.fpm"));
            var mats = new Material[json.materials.Length];
            var names = new string[mats.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                mats[i] = MakeMaterial(dir, json.materials[i]);
                names[i] = json.materials[i].name;
            }
            m = new Model { Mesh = mesh, Materials = mats, MaterialNames = names, Bounds = mesh.bounds };
            _models[name] = m;
            Core.Log($"[model] {name}: materials=[{string.Join(",", names)}] bounds min={mesh.bounds.min:F3} max={mesh.bounds.max:F3}");
            return m;
        }

        // モデルを表示する GameObject を作る
        public static GameObject Spawn(string name, Transform parent, int layer)
        {
            var model = Load(name);
            var go = new GameObject("FPR_" + name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = model.Materials;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // 一人称の武器は影を落とさない（ゲームの武器と同じ）
            return go;
        }

        // ---------- 材質 ----------

        static Material _opaqueTemplate, _transparentTemplate;
        const float ReticleGlow = 4f; // 照準の模様の明るさ（暗い場所でもくっきり見えるように）

        static void EnsureTemplates()
        {
            if (_opaqueTemplate != null && _transparentTemplate != null) return;
            _opaqueTemplate = FindTemplateMaterial(0, "AKS-74N/ak74m/GameObject/bolt_low");
            _transparentTemplate = FindTemplateMaterial(20 /* 透明材質の複製元：Type 89 に隠れているホロサイト */, "HoloSight/Holosight_Mesh/Plane");
            Core.Log($"[model] templates opaque={_opaqueTemplate?.name}({_opaqueTemplate?.shader?.name}) transparent={_transparentTemplate?.name}({_transparentTemplate?.shader?.name})");
        }

        static Material FindTemplateMaterial(int weaponId, string path)
        {
            var t = Weapons.ItemById(weaponId)?.Weapon?.transform.Find(path);
            var r = t != null ? t.GetComponent<Renderer>() : null;
            return r != null ? r.sharedMaterial : null;
        }

        static Material MakeMaterial(string dir, MatJson j)
        {
            EnsureTemplates();
            bool transparent = j.alpha == "BLEND";
            var tpl = transparent ? _transparentTemplate : _opaqueTemplate;
            var mat = tpl != null ? new Material(tpl) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.name = "FPR_" + j.name;
            mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var c = j.baseColor != null && j.baseColor.Length >= 4 ? new Color(j.baseColor[0], j.baseColor[1], j.baseColor[2], j.baseColor[3]) : Color.white;
            var baseTex = Tex(dir, j.@base, false);
            mat.SetTexture("_BaseMap", baseTex);
            mat.SetColor("_BaseColor", c);
            if (transparent)
            {
                if (j.kind == "glass" || baseTex == null) mat.SetTexture("_BaseMap", Texture2D.whiteTexture);
                // 発光（照準の模様）。模様の部分だけ不透明で、明るく光らせる
                var em = j.kind == "reticle" ? Tex(dir, j.emis, false) : null;
                if (em != null)
                {
                    mat.SetTexture("_EmissionMap", em);
                    var e = j.emission != null && j.emission.Length >= 3 ? new Color(j.emission[0], j.emission[1], j.emission[2]) : Color.white;
                    mat.SetColor("_EmissionColor", e * ReticleGlow);
                    mat.EnableKeyword("_EMISSION");
                }
                else
                {
                    mat.SetColor("_EmissionColor", Color.black);
                    mat.DisableKeyword("_EMISSION");
                }
                return mat;
            }
            var nrm = Tex(dir, j.normal, true);
            if (nrm != null) { mat.SetTexture("_BumpMap", nrm); mat.SetFloat("_BumpScale", 1f); mat.EnableKeyword("_NORMALMAP"); }
            else mat.DisableKeyword("_NORMALMAP");
            var ms = Tex(dir, j.ms, true);
            if (ms != null) { mat.SetTexture("_MetallicGlossMap", ms); mat.SetFloat("_Smoothness", 1f); mat.EnableKeyword("_METALLICSPECGLOSSMAP"); }
            else { mat.SetFloat("_Metallic", j.metallic); mat.SetFloat("_Smoothness", j.smoothness); mat.DisableKeyword("_METALLICSPECGLOSSMAP"); }
            var occ = Tex(dir, j.occ, true);
            if (occ != null) { mat.SetTexture("_OcclusionMap", occ); mat.SetFloat("_OcclusionStrength", 1f); mat.EnableKeyword("_OCCLUSIONMAP"); }
            else mat.DisableKeyword("_OCCLUSIONMAP");
            mat.DisableKeyword("_EMISSION");
            return mat;
        }

        static Texture2D Tex(string dir, string file, bool linear)
        {
            if (string.IsNullOrEmpty(file)) return null;
            var path = Path.Combine(dir, file);
            if (_textures.TryGetValue(path, out var t) && t != null) return t;
            if (!File.Exists(path)) { Core.Log("[model] texture missing " + path); return null; }
            t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "FPR_" + file, wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            ImageConversion.LoadImage(t, File.ReadAllBytes(path), false);
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _textures[path] = t;
            return t;
        }
    }
}
