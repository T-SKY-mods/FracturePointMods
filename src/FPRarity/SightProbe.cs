using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario sightprobe のときだけ動く）。
    // ① 手に持った武器（サイト付き）を真上・横から撮り、サイトと銃の左右・前後のずれを調べる（プレハブの複製と比較）
    // ② M16 のキャリングハンドルを消せるか（メッシュが読めるか）と、隠れている Mk18Mesh の見た目
    public static class SightProbe
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static readonly int[] Ids = { 20, 21, 27, 0 };

        public static List<(string, Action)> Steps()
        {
            var steps = new List<(string, Action)>
            {
                ("prepare", () => { Directory.CreateDirectory(ShotDir); foreach (var f in Directory.GetFiles(ShotDir, "probe_*.png")) File.Delete(f); }),
                ("m16 mesh", M16Mesh),
            };
            foreach (var id in Ids)
            {
                int wid = id;
                steps.Add(($"add {wid}", () =>
                {
                    Mastery.SetKillsForTest(wid, Mastery.KillsForLevel(Mastery.SightLevel));
                    Mastery.SetSight(wid, true);
                    if (!Tracker.Held.Any(h => h.Roll.WeaponId == wid)) TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(wid));
                }));
                steps.Add(("wait", () => { }));
                steps.Add(($"equip {wid}", () =>
                {
                    var name = Tracker.ItemById(wid).Weapon.gameObject.name;
                    var inst = Refs.Fps.InstantiatedWeapons;
                    for (int i = 0; i < inst.Count; i++)
                        if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == name) { Refs.Fps.StartWeaponChange(i); return; }
                }));
                steps.Add(("wait", () => { }));
                steps.Add(("wait", () => { }));
                steps.Add(($"probe {wid}", () => { Probe(wid); Pose(wid, "idle"); }));
                steps.Add(($"reload {wid}", () => { try { Refs.Fps.GetActiveItem().gameObject.GetComponent<DemoWeapon>().OnReload(); } catch (Exception e) { Main.Log("[probe] reload failed " + e.Message); } }));
                steps.Add(($"during reload {wid}", () => { Pose(wid, "reload"); ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"probe_{wid:00}_reload.png")); }));
                steps.Add(("wait", () => { }));
                steps.Add(("wait", () => { }));
                steps.Add(($"cleanup {wid}", () => { if (wid != 20) TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(wid)); }));
                steps.Add(("wait", () => { }));
            }
            return steps;
        }

        // 本体（Body またはメイン部品）と根元・サイトの相対位置を記録。姿勢が変わっても サイト-本体 の関係が一定なら正しく付いている
        static void Pose(int id, string when)
        {
            var go = Refs.Fps.GetActiveItem()?.gameObject;
            if (go == null) return;
            var root = go.transform;
            var spot = SightMount.Config[id];
            var body = spot.Body != null ? root.Find(spot.Body) : null;
            var holo = SightMount.FindMount(root);
            var bodyRel = body != null ? root.InverseTransformPoint(body.position).ToString("F4") + " rot=" + (Quaternion.Inverse(root.rotation) * body.rotation).eulerAngles.ToString("F1") : "-";
            var holoRoot = holo != null ? root.InverseTransformPoint(holo.position).ToString("F4") : "-";
            var holoBody = holo != null && body != null ? body.InverseTransformPoint(holo.position).ToString("F4") : "-";
            Main.Log($"[probe] pose [{id}] {when}: body(root)={bodyRel} holo(root)={holoRoot} holo(body)={holoBody}");
            if (when != "idle") return;
            // 銃とサイトのマテリアルのシェーダー（一人称の武器用の特殊な描画をしているか）
            var seen = new HashSet<string>();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || !SightLab.IsMesh(r)) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    bool isHolo = holo != null && r.transform.IsChildOf(holo);
                    var key = (isHolo ? "HOLO " : "gun  ") + m.shader.name;
                    if (!seen.Add(key)) continue;
                    var props = new List<string>();
                    int n = m.shader.GetPropertyCount();
                    for (int i = 0; i < n; i++) props.Add(m.shader.GetPropertyName(i));
                    Main.Log($"[probe] shader [{id}] {key} (mat '{m.name}' on '{r.gameObject.name}') kw=[{string.Join(",", m.shaderKeywords)}] props=[{string.Join(",", props)}]");
                }
            }
        }

        static void Probe(int id)
        {
            var go = Refs.Fps.GetActiveItem()?.gameObject;
            if (go == null) return;
            var root = go.transform;
            var holo = SightMount.FindMount(root);
            var sb = new StringBuilder($"[probe] [{id}] {go.name} layer={go.layer} holo={(holo != null)}\n");
            // 各部品の位置（武器の根元の座標）。手に持った状態とプレハブで比べる
            var prefab = Tracker.ItemById(id).Weapon.gameObject;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || !SightLab.IsMesh(r)) continue;
                var b = SightLab.LocalBounds(r, root);
                var path = SightLab.RelPath(r.transform, root);
                var pt = prefab.transform.Find(path);
                var prefabLocal = pt != null ? prefab.transform.InverseTransformPoint(pt.position) : Vector3.positiveInfinity;
                var nowLocal = root.InverseTransformPoint(r.transform.position);
                sb.AppendLine($"  '{path}' center={b.center:F3} size={b.size:F3} pivotNow={nowLocal:F3} pivotPrefab={prefabLocal:F3}");
                var smr = r.TryCast<SkinnedMeshRenderer>();
                if (smr != null && smr.rootBone != null)
                {
                    var rb = smr.rootBone;
                    var rbp = prefab.transform.Find(SightLab.RelPath(rb, root));
                    sb.AppendLine($"     skinned rootBone='{SightLab.RelPath(rb, root)}' now={root.InverseTransformPoint(rb.position):F3} rot={(Quaternion.Inverse(root.rotation) * rb.rotation).eulerAngles:F1} prefab={(rbp != null ? prefab.transform.InverseTransformPoint(rbp.position).ToString("F3") : "-")} prefabRot={(rbp != null ? (Quaternion.Inverse(prefab.transform.rotation) * rbp.rotation).eulerAngles.ToString("F1") : "-")}");
                }
            }
            Main.Log(sb.ToString());
            // サイトと銃の描画レイヤー、シーンのカメラ（どのカメラがどのレイヤーをどの視野角で描くか）
            var layers = new HashSet<string>();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
                if (r.enabled && r.gameObject.activeInHierarchy && SightLab.IsMesh(r))
                    layers.Add($"{(holo != null && r.transform.IsChildOf(holo) ? "HOLO" : "gun")}:{r.gameObject.layer}");
            Main.Log($"[probe] layers [{id}] {string.Join(" ", layers)}");
            foreach (var c in Camera.allCameras)
                Main.Log($"[probe] camera '{c.name}' enabled={c.enabled} depth={c.depth} fov={c.fieldOfView} mask=0x{c.cullingMask:X} pos={c.transform.position:F3} parent='{c.transform.parent?.name}'");
            int mask = 1 << go.layer;
            var spot = SightMount.Config[id];
            var top = SightLab.RenderTop(go, 0.25f, new Vector3(0, 0, spot.Z), mask);
            SightLab.Save(top, $"probe_{id:00}_live_top"); UnityEngine.Object.Destroy(top);

            // 比較：プレハブの複製に同じようにサイトを載せて真上から
            var clone = SightLab.CloneStatic(prefab, out var holder);
            holder.SetActive(true);
            SightMount.Apply(clone, prefab, id, true);
            var top2 = SightLab.RenderTop(clone, 0.25f, new Vector3(0, 0, spot.Z));
            SightLab.Save(top2, $"probe_{id:00}_prefab_top"); UnityEngine.Object.Destroy(top2);
            UnityEngine.Object.Destroy(holder);
        }

        static void M16Mesh()
        {
            var prefab = Tracker.ItemById(10).Weapon.gameObject;
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
                if (SightLab.RelPath(mf.transform, prefab.transform).StartsWith("M16"))
                    Main.Log($"[probe] m16 mesh '{SightLab.RelPath(mf.transform, prefab.transform)}' readable={mf.sharedMesh?.isReadable} verts={mf.sharedMesh?.vertexCount} sub={mf.sharedMesh?.subMeshCount}");
            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                Main.Log($"[probe] m16 skinned '{SightLab.RelPath(smr.transform, prefab.transform)}' readable={smr.sharedMesh?.isReadable} verts={smr.sharedMesh?.vertexCount} mats={string.Join(",", smr.sharedMaterials.Select(m => m?.name))}");

            // Mk18Mesh を表示し M16 本体を隠した見た目（2種類）
            var smrs = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(s => s.gameObject.name == "Mk18Mesh").ToList();
            for (int v = 0; v < smrs.Count; v++)
            {
                var clone = SightLab.CloneStatic(prefab, out var holder);
                var cs = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(s => s.gameObject.name == "Mk18Mesh").ToList();
                foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
                    if (SightLab.RelPath(r.transform, clone.transform).StartsWith("M16/")) r.enabled = false;
                for (int k = 0; k < cs.Count; k++) cs[k].gameObject.SetActive(k == v);
                holder.SetActive(true);
                SightLab.Shoot(clone, $"probe_m16_mk18_{v}", side: true);
                UnityEngine.Object.Destroy(holder);
            }
        }
    }
}
