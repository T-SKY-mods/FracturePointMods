using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using FPModOption;

namespace FPCustom
{
    // 最初から武器に付いている部品（ゲームのまま）。外すときはこの描画物を消す。武器の根元からのパス
    public static class Factory
    {
        public sealed class Part
        {
            public int WeaponId; public string Att; public string[] Paths;
            public string[] Keep = new string[0];   // パスの下にあるが部品ではない（銃の一部）もの
            public bool WholeObject;                // 描画物ではなく物ごと消す（サイトの照準の模様は描画物を消しても残るため）
        }

        public static readonly List<Part> All = new()
        {
            new Part { WeaponId = 2,  Att = "muzzle_brake",  Paths = new[] { "AKS-74N/ak12/MuzzleBrake" } },
            new Part { WeaponId = 2,  Att = "vertical_grip", Paths = new[] { "GripMount" } },
            new Part { WeaponId = 2,  Att = "holo", WholeObject = true,          Paths = new[] { "holo", "AKS-74N/ak12/cobra-glass" } },
            new Part { WeaponId = 2,  Att = "laser",         Paths = new[] { "tigg_laser" } },
            new Part { WeaponId = 21, Att = "muzzle_brake",  Paths = new[] { "ar15 long/Reciever/Barrel/Muzzle_Brake" } },
            new Part { WeaponId = 21, Att = "laser",         Paths = new[] { "tigg_laser" } },
            new Part { WeaponId = 28, Att = "vertical_grip", Paths = new[] { "mp5 custom/Armature/GripMount" } },
            new Part { WeaponId = 28, Att = "holo", WholeObject = true,          Paths = new[] { "mp5 custom/Armature/sight" } },
            new Part { WeaponId = 28, Att = "laser",         Paths = new[] { "tigg_laser" } },
            new Part { WeaponId = 18, Att = "vertical_grip", Paths = new[] { "GripMount", "saiga12_custom_parts/saiga12_front_handle" } },
            new Part { WeaponId = 15, Att = "vertical_grip", Paths = new[] { "uploads_files_4287500_ar15_default/ar15_grip" } },
            new Part { WeaponId = 15, Att = "holo", WholeObject = true,          Paths = new[] { "holo" } },
            new Part { WeaponId = 18, Att = "reddot", WholeObject = true,        Paths = new[] { "saiga12_custom_parts/saiga12_reddot" } },
            new Part { WeaponId = 16, Att = "ace_sight", WholeObject = true,     Paths = new[] { "sk_ace32/SightLP" } },
            new Part { WeaponId = 17, Att = "swiss_scope", WholeObject = true,   Paths = new[] { "Scope [Example 1]", "SIG 751 1/Untitled" } },   // 効果の部分＋ELCAN の本体
            new Part { WeaponId = 12, Att = "ak_sight", WholeObject = true,      Paths = new[] { "AKS-74N/AK12Rig/Root/accessories_low" } },
            new Part { WeaponId = 14, Att = "ak_sight", WholeObject = true,      Paths = new[] { "uploads_files_5248530_ASVAL/accessories_low" } },
            new Part { WeaponId = 9,  Att = "p_dot",         Paths = new[] { "W_Mk23Mod0_Skeleton/Mk23Mod0Rig/Root/Slider/Sight Body" },
                       Keep = new[] { "Slide", "Front Sight", "Front White Dot", "Rear Sight", "Rear White Dot" } },
            new Part { WeaponId = 9,  Att = "p_laser",       Paths = new[] { "Laser Sight SBAL-PL_Final" } },
            new Part { WeaponId = 7,  Att = "p_laser",       Paths = new[] { "Laser Sight SBAL-PL_Final" } },
            new Part { WeaponId = 23, Att = "p_suppressor",  Paths = new[] { "W_Mk23Mod0_Skeleton/Mk23Mod0Rig/Root/Barrel/silencer 9" } },
        };

        public static IEnumerable<Part> On(int weaponId) => All.Where(p => p.WeaponId == weaponId);

        // 部品の描画物（Keep の下は除く）
        public static List<Renderer> Renderers(Transform root, Part p)
        {
            var list = new List<Renderer>();
            foreach (var path in p.Paths)
            {
                var t = root.Find(path);
                if (t == null) continue;
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    bool keep = false;
                    for (var x = r.transform; x != null && x != t; x = x.parent) if (p.Keep.Contains(x.name)) { keep = true; break; }
                    if (!keep) list.Add(r);
                }
            }
            return list;
        }

        // 見えているか（テスト用）
        public static bool Shown(Transform root, Part p)
        {
            if (p.WholeObject) { foreach (var path in p.Paths) { var t = root.Find(path); if (t != null && t.gameObject.activeSelf) return true; } return false; }
            foreach (var r in Renderers(root, p)) if (r.enabled) return true;
            return false;
        }

        public static void Show(Transform root, Part p, bool show)
        {
            foreach (var r in Renderers(root, p)) r.enabled = show;
        }

        // 付けるときはプレハブと同じ状態に戻す（元から消えている描画物は消えたまま）
        public static void Show(Transform root, Transform prefabRoot, Part p, bool show)
        {
            if (p.WholeObject)
            {
                foreach (var path in p.Paths)
                {
                    var t = root.Find(path);
                    var pt = prefabRoot?.Find(path);
                    if (t == null) continue;
                    bool on = show && (pt == null || pt.gameObject.activeSelf);
                    if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
                }
                return;
            }
            var rs = Renderers(root, p);
            var ps = prefabRoot != null ? Renderers(prefabRoot, p) : null;
            for (int i = 0; i < rs.Count; i++)
                rs[i].enabled = show && (ps == null || i >= ps.Count || ps[i].enabled);
        }
    }
}
