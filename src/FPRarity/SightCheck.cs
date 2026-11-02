using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

using FPModOption;

namespace FPRarity
{
    // アップデート確認用（autotest -Scenario sightcheck）。ゲームの状態は変えない（プレハブの複製だけで確かめる）。
    // ・サイト対応の全武器で、使うゲームの部品がそろっていること（SightMount.ValidateSpot）
    // ・複製にサイトを付けると、サイト一式ができて照準点が計算どおりに移り、外すと元に戻ること
    // ・部品が見つからない設定はきちんと「壊れている」と判定されること（判定そのもののテスト）
    public static class SightCheck
    {
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");

        public static List<(string, Action)> Steps()
        {
            var steps = new List<(string, Action)>
            {
                ("validate", () =>
                {
                    bool ok = SightMount.Config.Keys.All(SightMount.Supported);
                    foreach (var kv in SightMount.Broken) Main.Log($"[test] broken [{kv.Key}] {kv.Value}");
                    Check($"sight parts present for all {SightMount.Config.Count} weapons", ok && SightMount.Broken.Count == 0);
                    // 形を差し替える部品の頂点数（SightMount の SrcVerts と比べる値）
                    foreach (var kv in SightMount.Config)
                        foreach (var sw in kv.Value.Swaps)
                        {
                            var mf = Tracker.ItemById(kv.Key)?.Weapon?.transform.Find(sw.Renderer)?.GetComponent<MeshFilter>();
                            Main.Log($"[test] swap [{kv.Key}] '{sw.Renderer}' verts={mf?.sharedMesh?.vertexCount} expected={sw.SrcVerts}");
                        }
                }),
                ("negative", () =>
                {
                    // 存在しない部品を指定した設定は壊れていると判定される
                    var bad1 = new SightMount.Spot { Body = "no/such/bone" };
                    var bad2 = new SightMount.Spot { Hide = new[] { "NoSuchPart" } };
                    var bad3 = new SightMount.Spot { Sight = new SightMount.Optic { WeaponId = 0, Path = "No/Such/Sight" } };
                    var r1 = SightMount.ValidateSpot(10, bad1); var r2 = SightMount.ValidateSpot(10, bad2); var r3 = SightMount.ValidateSpot(10, bad3);
                    Main.Log($"[test] negative: {r1} / {r2} / {r3}");
                    Check("broken config is detected", r1 != null && r2 != null && r3 != null);
                }),
            };
            foreach (var id in SightMount.Config.Keys.OrderBy(k => k))
            {
                int wid = id;
                steps.Add(($"mount {wid}", () => Mount(wid)));
            }
            return steps;
        }

        static void Mount(int id)
        {
            var name = Tracker.NameOf(id);
            var prefab = Tracker.ItemById(id)?.Weapon?.gameObject;
            if (prefab == null) { Check($"[{id}] {name} mount", false); return; }
            // 非アクティブの入れ物の中に複製する（スクリプトは動かないが照準点などの参照は複製側を指す）
            var holder = new GameObject("FPR_CheckHolder");
            holder.SetActive(false);
            holder.transform.position = SightLab.Origin;
            var go = UnityEngine.Object.Instantiate(prefab, holder.transform, false);
            try
            {
                var before = AimLocal(go, prefab);
                int degradedBefore = GameVersion.Degraded.Count;
                SightMount.Apply(go, prefab, id, true);
                var mount = SightMount.FindMount(go.transform);
                var renderers = mount != null ? mount.GetComponentsInChildren<Renderer>(true).Length : 0;
                var after = AimLocal(go, prefab);
                bool aimOk = (after - SightMount.LastAim).magnitude < 0.0005f && Mathf.Abs(after.z - before.z) < 0.001f;
                Main.Log($"[test] [{id}] {name}: renderers={renderers} aim ({before.x:F4},{before.y:F4}) -> ({after.x:F4},{after.y:F4}) expected ({SightMount.LastAim.x:F4},{SightMount.LastAim.y:F4})");
                Check($"[{id}] {name} mount", mount != null && renderers >= 1 && aimOk && GameVersion.Degraded.Count == degradedBefore);
                // 弾道：発射点が照準の線の上にあり、向きが照準の線と同じ（25m 先でのずれが 1mm 未満）
                var sp = go.GetComponentInChildren<Il2CppBallistics.Weapon>(true)?.PhysicalBulletSpawnPoint;
                if (sp != null)
                {
                    var r = go.transform;
                    var bp = r.InverseTransformPoint(sp.position); var bd = r.InverseTransformDirection(sp.forward);
                    var camFwd = SightMount.LastBulletDir; var aim = SightMount.LastAim;
                    var hitAt25 = bp + bd * ((aim.z + camFwd.z * 25f - bp.z) / bd.z);
                    var dotAt25 = aim + camFwd * 25f;
                    float err = (hitAt25 - dotAt25).magnitude;
                    Main.Log($"[test] [{id}] bullet spawn ({bp.x:F4},{bp.y:F4},{bp.z:F4}) dir ({bd.x:F4},{bd.y:F4},{bd.z:F4}) error at 25m = {err * 1000f:F2}mm");
                    Check($"[{id}] {name} bullet goes to the dot", err < 0.001f);
                }
                else Check($"[{id}] {name} bullet spawn point found", false);
                SightMount.Apply(go, prefab, id, false);
                UnityEngine.Object.DestroyImmediate(SightMount.FindMount(go.transform)?.gameObject);
                Check($"[{id}] {name} unmount restores aim", (AimLocal(go, prefab) - before).magnitude < 0.0005f);
                var psp = prefab.GetComponentInChildren<Il2CppBallistics.Weapon>(true)?.PhysicalBulletSpawnPoint;
                Check($"[{id}] {name} unmount restores bullet", sp != null && psp != null && (sp.localPosition - psp.localPosition).magnitude < 0.0001f && Quaternion.Angle(sp.localRotation, psp.localRotation) < 0.01f);
            }
            catch (Exception e) { Main.Log($"[test] [{id}] mount exception {e}"); Check($"[{id}] {name} mount", false); }
            finally { UnityEngine.Object.Destroy(holder); }
        }

        static Vector3 AimLocal(GameObject go, GameObject prefab)
        {
            var g = go.GetComponent<DemoWeapon>()?.scopeGroups;
            var ap = g != null && g.Count > 0 && g[0]?.attachments != null && g[0].attachments.Count > 0 ? g[0].attachments[0]?.aimPoint : null;
            return ap != null && ap.IsChildOf(go.transform) ? go.transform.InverseTransformPoint(ap.position) : new Vector3(float.NaN, 0, 0);
        }
    }
}
