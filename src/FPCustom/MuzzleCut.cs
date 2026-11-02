using System.Collections.Generic;
using System.IO;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 銃身とひとつのメッシュになっている武器の銃口の部品（AK-74 のブレーキ・AKM のスラントブレーキ・Swiss 751 のスリットの部品・ACE-31 のリング・QBZ・M16・Type 89 のハイダー）。
    //   銃口の部品を付けるときは、今の形からそれを除いた形（<今の形>_nomuz.fpm。tools/build_custom_assets.ps1 の --tribox / --box）に差し替える。
    //   今の形：ゲームのまま（Base）か、ハンドガード・サイトで差し替えた形（ak74_noguard・m16_flattop・qbz_norear など。メッシュの名前 FPR_<名前>）
    //   ほかの差し替え（Optics・Guards）より前に Undo で戻し、銃口の部品を付けるところで Apply する
    //   動くメッシュ（SkinnedMeshRenderer。Type 89）：骨の重みは IL2CPP から入れられないので、除いた形を骨ごとに分けた物（<Base>_nomuz_b<番号>.fpm。
    //   その骨の座標）を骨の子に置き、元の描画を消す（ほぼすべての頂点が 1 つの骨に重み 1 なので、動きは同じ）
    public static class MuzzleCut
    {
        const string Suffix = "_nomuz", Loaded = "FPR_", PartName = "cut_b";
        static readonly Dictionary<int, (string Renderer, string Base)> Merged = new()
        {
            [0] = ("AKS-74N/ak74m/GameObject/handle_low", "ak74"),   // AK-74：ブレーキ z 0.425〜0.511
            [3] = ("AKS-74N/cmr/QBZ", "qbz"),                         // QBZ 191：ハイダー z 0.4285〜0.499
            [12] = ("AKM_low7/upperReceiver_low/upperReceiver_a_low", "akm"),   // AKM：スラントブレーキ z 0.4662〜0.4915
            [17] = ("SIG 751 1/barrel1", "swiss_barrel"),             // Swiss 751：スリットの付いた部品 z 0.5541〜0.5829
            [16] = ("sk_ace32/Geo/Barrel", "ace"),                    // ACE-31：銃身の先のポート付きのリング z 0.4416〜0.4592（動くメッシュ。骨 1 つ）
            [10] = ("M16/M16", "m16"),                               // M16：ハイダー z 0.5175〜0.566
            [20] = ("type89_classic/type89_classic", "type89"),       // Type 89：ハイダー z 0.4724〜0.5372（動くメッシュ）
        };

        public static bool Has(int id) => Merged.ContainsKey(id);

        static (MeshFilter mf, MeshFilter pmf, string cur)? Current(Transform root, Transform prefab, int id)
        {
            if (!Merged.TryGetValue(id, out var m)) return null;
            var mf = root.Find(m.Renderer)?.GetComponent<MeshFilter>(); var pmf = prefab.Find(m.Renderer)?.GetComponent<MeshFilter>();
            if (mf?.sharedMesh == null || pmf?.sharedMesh == null) return null;
            string cur = mf.sharedMesh.Pointer == pmf.sharedMesh.Pointer ? m.Base
                : mf.sharedMesh.name.StartsWith(Loaded) ? mf.sharedMesh.name.Substring(Loaded.Length) : null;
            return cur == null ? null : (mf, pmf, cur);
        }

        static void Set(MeshFilter mf, MeshFilter pmf, string name, int id)
        {
            if (name == Merged[id].Base) { mf.sharedMesh = pmf.sharedMesh; return; }
            // FPCustom の形（Guards と同じ読み方）か、サイトの差し替えの形（FPRarity の models。Optics と同じ読み方）
            var path = Path.Combine(Guards.ModelDir, name + ".fpm");
            if (File.Exists(path)) mf.sharedMesh = ModelLoader.LoadMesh(path);
            else if (ModelLoader.Exists(name + ".fpm")) mf.sharedMesh = ModelLoader.LoadMesh(name + ".fpm");
            else Main.Log($"[muzcut] missing {name}.fpm");
        }

        static (SkinnedMeshRenderer sk, SkinnedMeshRenderer psk)? Skinned(Transform root, Transform prefab, int id)
        {
            if (!Merged.TryGetValue(id, out var m)) return null;
            var sk = root.Find(m.Renderer)?.GetComponent<SkinnedMeshRenderer>(); var psk = prefab.Find(m.Renderer)?.GetComponent<SkinnedMeshRenderer>();
            return sk == null || psk == null ? null : (sk, psk);
        }

        // 除いた形を元の形に戻す（動くメッシュの骨ごとの形は Mount が FPC_ の物と一緒に消すので、元の描画を戻すだけ）
        public static void Undo(GameObject inst, GameObject prefab, int id)
        {
            if (Skinned(inst.transform, prefab.transform, id) is { } s) { s.sk.enabled = s.psk.enabled; return; }
            if (Current(inst.transform, prefab.transform, id) is not { } c || !c.cur.EndsWith(Suffix)) return;
            Set(c.mf, c.pmf, c.cur.Substring(0, c.cur.Length - Suffix.Length), id);
        }

        // 銃口の部品を付けたとき：今の形から銃口の部品を除いた形に
        public static void Apply(GameObject inst, GameObject prefab, int id)
        {
            if (Skinned(inst.transform, prefab.transform, id) is { } s) { ApplySkinned(s.sk, Merged[id].Base + Suffix); return; }
            if (Current(inst.transform, prefab.transform, id) is not { } c || c.cur.EndsWith(Suffix)) return;
            Set(c.mf, c.pmf, c.cur + Suffix, id);
        }

        static void ApplySkinned(SkinnedMeshRenderer sk, string name)
        {
            if (!sk.enabled) return;   // 置いてある
            var bones = sk.bones;
            var mats = sk.sharedMaterials;
            int placed = 0;
            for (int b = 0; b < bones.Length; b++)
            {
                var path = Path.Combine(Guards.ModelDir, $"{name}_b{b}.fpm");
                if (bones[b] == null || !File.Exists(path)) continue;
                var go = new GameObject(Mount.Prefix + PartName + b) { layer = sk.gameObject.layer };
                go.transform.SetParent(bones[b], false);
                go.AddComponent<MeshFilter>().sharedMesh = ModelLoader.LoadMesh(path);
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = mats;
                mr.shadowCastingMode = sk.shadowCastingMode;
                placed++;
            }
            if (placed == 0) { Main.Log($"[muzcut] missing {name}_b*.fpm"); return; }
            sk.enabled = false;
        }
    }
}
