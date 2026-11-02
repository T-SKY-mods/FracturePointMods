using System.Collections.Generic;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // サイト。最初から付いているサイトを外したときは、照準点をアイアンサイト（前の照星の先端）の高さへ下げる。
    // 高さは autotest -Dev -Scenario custsight で測った値：照星の先端 + 3mm（最初からアイアンサイトの武器の照準点は、照星の先端の -5〜+6mm、中央 +3mm）
    public static class Sights
    {
        // 武器 → アイアンサイトの照準点の高さ（武器の根元の座標、m）
        static readonly Dictionary<int, float> IronY = new()
        {
            [2] = 0.0671f,    // AK-12（照星 0.0641）
            [12] = 0.0643f,   // AKM（照星 0.0613。横のマウントのスコープを外す）
            [14] = 0.0463f,   // VAL（照星 0.0433）
            [17] = 0.0613f,   // Swiss 751（照星 0.0583。スコープを外す）
        };

        // 同じ模型でアイアンサイトのままの武器があるときは、その照準点（位置・向き）をそのまま使う
        static readonly Dictionary<int, int> IronFrom = new()
        {
            [28] = 27,   // Viper M5A3 ← MPS-5（銃口の位置も同じ）
        };

        // アイアンサイトの高さを照星の描画物から求める武器（照星の先端 + 2mm。ハンドガンは前後の照準の上端をそろえて狙う）
        static readonly Dictionary<int, string> IronPost = new()
        {
            [9] = "W_Mk23Mod0_Skeleton/Mk23Mod0Rig/Root/Slider/Sight Body/Front Sight",   // SP-320：ドットサイトを外すと下のアイアンサイト
        };

        // 最初からサイトが付いていて、外すとアイアンサイトが無い武器
        // R-15 はアイアンサイトがたたんだまま（起こした形が無い）で、たたんだ照門が目の前をふさぐ
        static readonly HashSet<int> NoIron = new() { 16, 18, 15 };   // ACE-31、Hunter-32、Tactic R-15

        public static bool HasIron(int id) => !NoIron.Contains(id);

        public static bool HasFactorySight(int id)
        {
            foreach (var p in Factory.On(id)) if (Catalog.Get(p.Att)?.Cat == Cat.Sight) return true;
            return false;
        }

        static Transform AimPoint(DemoWeapon dw)
        {
            var g = dw?.scopeGroups;
            return g != null && g.Count > 0 && g[0]?.attachments != null && g[0].attachments.Count > 0 ? g[0].attachments[0]?.aimPoint : null;
        }

        public static void Apply(GameObject inst, GameObject prefab, int id)
        {
            var dw = inst.GetComponent<DemoWeapon>(); var pdw = prefab.GetComponent<DemoWeapon>();
            var ap = AimPoint(dw); var pap = AimPoint(pdw);
            if (ap == null || pap == null) return;
            // まずゲームのまま（照準点の親に対する位置・向き）に戻す
            ap.localPosition = pap.localPosition; ap.localRotation = pap.localRotation;
            Optics.Restore(inst, prefab, id);
            var eq = Store.Equipped(id, Cat.Sight);
            if (eq != null && !Catalog.IsFactory(eq, id)) Optics.Apply(inst, prefab, id, eq);   // 別のサイトを載せる
            else if (eq == null && HasFactorySight(id)) ToIron(inst, prefab, id, ap, pap);
            // ストック付きのハンドガン：肩に当てて構える（ライフルと同じく、目からグリップの背中まで ShoulderReach。照準の線はそのまま前後だけ）
            var st = Store.Equipped(id, Cat.Stock);
            if (st != null && AssetParts.Supported(id, st) && AssetParts.StockAt.TryGetValue(id, out var sa))
            {
                var lp = inst.transform.InverseTransformPoint(ap.position);
                lp.z = sa.HeelZ - ShoulderReach;
                ap.position = inst.transform.TransformPoint(lp);
            }
        }

        public const float ShoulderReach = 0.16f;

        static void ToIron(GameObject inst, GameObject prefab, int id, Transform ap, Transform pap)
        {
            // 最初のサイトを外した：アイアンサイトの高さへ
            var root = inst.transform; var proot = prefab.transform;
            var pos = proot.InverseTransformPoint(pap.position);
            var rot = Quaternion.Inverse(proot.rotation) * pap.rotation;
            if (IronFrom.TryGetValue(id, out var src) && AimPoint(Tracker.ItemById(src)?.Weapon?.gameObject?.GetComponent<DemoWeapon>()) is { } sap)
            {
                var sr = Tracker.ItemById(src).Weapon.gameObject.transform;
                pos = sr.InverseTransformPoint(sap.position);
                rot = Quaternion.Inverse(sr.rotation) * sap.rotation;
            }
            else if (IronY.TryGetValue(id, out var y)) pos.y = y;
            else if (IronPost.TryGetValue(id, out var post) && proot.Find(post) is { } pt) pos.y = Lab.MeshBounds(pt, proot).max.y + 0.002f;
            ap.position = root.TransformPoint(pos);
            ap.rotation = root.rotation * rot;
            LastAim = pos;
        }

        public static Vector3 LastAim;   // テスト用（武器の根元の座標）
    }
}
