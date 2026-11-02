using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

using FPModOption;

namespace FPSurvival
{
    // 屋上の中央：ソーラーパネルを片付け、屋上にもともとある木の壁（壁・窓あき・ドアあき）を複製して遮蔽物にする
    public static class Cover
    {
        static readonly List<Bounds> _placed = new();
        // 屋上にある木の壁の種類（合板の壁 "Wall 1"・窓あき "Window"・ドアあき "Door"）
        static readonly Regex[] Kinds =
        {
            new(@"^Wall 1( \(\d+\))?$", RegexOptions.Compiled),
            new(@"^Window( \(\d+\))?$", RegexOptions.Compiled),
            new(@"^Door( \(\d+\))?$", RegexOptions.Compiled),
        };

        // 置き場所（Out: 非常口から外向きの距離、Side: 横、Yaw: 0 で非常口の方を向く・90 で縦向き）
        //   L 字・T 字・平行の列を組み合わせて、平行と垂直の壁が混ざるようにする
        const float Half = 1.58f, Gap = 0.25f;   // 板の長さの半分・板どうしのすき間
        static readonly (float Out, float Side, float Yaw)[] Layout = BuildLayout();

        static (float, float, float)[] BuildLayout()
        {
            var l = new List<(float, float, float)>();
            // L 字：横向きの板と、その端から外向きに伸びる縦向きの板（dirSide: 縦の板を付ける側、dirOut: 伸ばす向き）
            void L(float o, float s, float dirSide, float dirOut)
            {
                l.Add((o, s, 0f));
                l.Add((o + dirOut * (Half + Gap), s + dirSide * (Half - 0.1f), 90f));
            }
            // T 字：横向きの板の真ん中から縦向きの板
            void T(float o, float s, float dirOut)
            {
                l.Add((o, s, 0f));
                l.Add((o + dirOut * (Half + Gap), s, 90f));
            }
            L(11f, -7f, -1f, 1f); L(11f, 7f, 1f, 1f);
            l.Add((14f, 0f, 90f));
            T(19f, -10f, -1f); T(19f, 10f, -1f);
            l.Add((20f, -3.5f, 0f)); l.Add((20f, 3.5f, 0f));                 // 平行の列
            L(26f, -6f, 1f, 1f); L(26f, 6f, -1f, 1f);
            l.Add((30f, 0f, 0f)); l.Add((30f, -12f, 90f)); l.Add((30f, 12f, 90f));
            T(35f, -6f, -1f); T(35f, 6f, -1f);
            l.Add((40f, -2f, 90f)); l.Add((40f, 2f, 90f));                   // 縦の平行
            return l.ToArray();
        }

        public static void Place(Transform room, Vector3 doorPos, Vector3 inward)
        {
            _placed.Clear();
            RemoveSolar(room, doorPos);
            var templates = Kinds.Select(k => FindTemplate(room, k)).Where(t => t != null).ToList();
            if (templates.Count == 0) { Main.Log("[cover] no wooden wall found on the roof"); return; }
            var outward = -inward;
            var side = Vector3.Cross(Vector3.up, outward);
            var baseRot = Quaternion.LookRotation(side);   // 壁の板は自分の z 方向に伸びるので、yaw 0 で非常口の方を向く
            int n = 0;
            foreach (var (o, s, yaw) in Layout)
            {
                var template = templates[n % templates.Count];
                var want = doorPos + outward * o + side * s;
                var rot = baseRot * Quaternion.Euler(0f, yaw, 0f);
                if (!FreeSpot(want, rot, doorPos.y, out var at)) { Main.Log($"[cover] no room near {want}"); continue; }
                float lift = template.position.y - BoundsOf(template).min.y;
                var go = Object.Instantiate(template.gameObject, at + Vector3.up * lift, rot * Quaternion.Inverse(Quaternion.Euler(0f, template.eulerAngles.y, 0f)) * template.rotation);
                go.name = "FPSurvival cover " + n;
                go.transform.SetParent(room, true);
                go.SetActive(true);
                // 板の中心が置き場所に来るようにずらす
                var b = BoundsOf(go.transform);
                var shift = at - b.center; shift.y = 0f;
                go.transform.position += shift;
                b = BoundsOf(go.transform);
                // 板の向き：自分の x が厚み、z が長さ
                float length = Mathf.Sqrt(b.size.x * b.size.x + b.size.z * b.size.z) * 0.95f;
                var obs = go.AddComponent<NavMeshObstacle>();
                obs.shape = NavMeshObstacleShape.Box;
                obs.center = go.transform.InverseTransformPoint(b.center);
                obs.size = Vector3.Scale(new Vector3(0.4f, b.size.y, Mathf.Min(length, 3.4f)), Inv(go.transform.lossyScale));
                obs.carving = true;
                Physics.SyncTransforms();   // 次の板の空き確認で、この板の当たり判定が新しい位置にあるように
                b.Expand(1.5f);
                _placed.Add(b);
                n++;
            }
            Main.Log($"[cover] placed {n} walls from [{string.Join(", ", templates.Select(t => t.name))}]");
        }

        public static bool Blocks(Vector3 p) => _placed.Any(b => b.Contains(new Vector3(p.x, b.center.y, p.z)));

        // 屋上の中央のソーラーパネルを片付ける（エレベーターの部屋の近くの物は残す）
        static void RemoveSolar(Transform room, Vector3 doorPos)
        {
            int n = 0;
            var seen = new HashSet<System.IntPtr>();
            foreach (var r in room.GetComponentsInChildren<MeshRenderer>())
            {
                if (r == null || !IsSolar(r)) continue;
                // 部屋の直下の親（パネルの列）ごと止める
                var top = r.transform;
                while (top.parent != null && top.parent != room) top = top.parent;
                if (!seen.Add(top.Pointer)) continue;
                if (Vector3.Distance(top.position, doorPos) < 4f || BoundsOf(top).size.y > 2.5f) { Main.Log($"[cover] solar kept '{top.name}'"); continue; }
                Main.Log($"[cover] solar removed '{top.name}' at {top.position}");
                top.gameObject.SetActive(false);
                n++;
            }
            Main.Log($"[cover] solar panels removed: {n}");
            if (n > 0) return;
            // 見つからないとき：中央の低い物の名前を出す（調査用）
            foreach (var r in room.GetComponentsInChildren<MeshRenderer>())
            {
                var b = r.bounds;
                if (b.size.y > 1.6f || b.min.y > doorPos.y + 0.5f || Vector3.Distance(b.center, doorPos) > 30f || Vector3.Distance(b.center, doorPos) < 6f) continue;
                var mf = r.GetComponent<MeshFilter>();
                Main.Log($"[cover]   low '{r.name}' parent '{r.transform.parent?.name}' mesh '{mf?.sharedMesh?.name}' mat '{r.sharedMaterial?.name}' at {b.center}");
            }
        }

        // ソーラーパネル：屋上の直下の "Untitled (n)" で、中に板のメッシュ "Plane.00n" がある物
        static readonly Regex SolarParent = new(@"^Untitled( \(\d+\))?$", RegexOptions.Compiled);
        static readonly Regex SolarMesh = new(@"^Plane\.\d+$", RegexOptions.Compiled);

        static bool IsSolar(MeshRenderer r)
        {
            var p = r.transform.parent;
            if (p == null || !SolarParent.IsMatch(p.name)) return false;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null && mf.sharedMesh != null && SolarMesh.IsMatch(mf.sharedMesh.name);
        }

        // 床の上で、周りに物が無い所（組み合わせが崩れないよう、ずらさない）
        static bool FreeSpot(Vector3 want, Quaternion rot, float floorY, out Vector3 at)
        {
            at = want;
            if (!Physics.Raycast(want + Vector3.up * 3f, Vector3.down, out var hit, 6f, ~0, QueryTriggerInteraction.Ignore)) return false;
            if (Mathf.Abs(hit.point.y - floorY) > 1.2f) return false;
            var c = hit.point + Vector3.up * 1.3f;
            if (Physics.CheckBox(c, new Vector3(0.3f, 1.0f, Half - 0.1f), rot, ~0, QueryTriggerInteraction.Ignore)) return false;
            if (!NavMesh.SamplePosition(hit.point, out _, 3f, NavMesh.AllAreas)) return false;
            at = hit.point;
            return true;
        }

        // 屋上にある、まっすぐ置かれた木の壁（斜めの物は向きが分かりにくいので使わない）
        static Transform FindTemplate(Transform room, Regex kind)
        {
            for (int i = 0; i < room.childCount; i++)
            {
                var t = room.GetChild(i);
                if (!t.gameObject.activeInHierarchy || !kind.IsMatch(t.name)) continue;
                var b = BoundsOf(t);
                if (Mathf.Min(b.size.x, b.size.z) > 0.35f || Mathf.Max(b.size.x, b.size.z) < 3f) continue;
                Main.Log($"[cover] template '{t.name}' size {b.size}");
                return t;
            }
            return null;
        }

        static Bounds BoundsOf(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(t.position, Vector3.zero);
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        static Vector3 Inv(Vector3 v) => new(v.x != 0f ? 1f / v.x : 1f, v.y != 0f ? 1f / v.y : 1f, v.z != 0f ? 1f / v.z : 1f);
    }
}
