using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario masteryprobe のときだけ動く）。何も変更しない。
    // 保管庫画面の UI 構造を書き出し、熟練度ページで流用できる部品を調べる。
    public static class MasteryProbe
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        public static List<(string, Action)> Steps() => new()
        {
            ("patch safety", PatchSafety),
            ("open stash (Interact)", () => Refs.Bag.Interact()),
            ("wait", () => { }), ("wait", () => { }),
            ("dump bag ui", DumpBag),
            ("shot", () => { Directory.CreateDirectory(ShotDir); ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, "probe_bag.png")); }),
        };

        static void PatchSafety()
        {
            foreach (var (t, m) in new (Type, string)[]
                     {
                         (typeof(Il2CppEnemyAI.EnemyHealth), "Kill"),
                         (typeof(SaveBag), "ClosePage"),
                         (typeof(SaveBag), "OpenPage"),
                         (typeof(SaveBag), "Update"),
                     })
                Main.Log($"[probe] {t.Name}.{m}: {SafePatcher.Describe(t, m)}");
            Main.Log($"[probe] EnemiesKilled pref = {PlayerPrefs.GetInt("EnemiesKilled", -1)}");
        }

        static void DumpBag()
        {
            var bag = Refs.Bag;
            var sb = new StringBuilder("===== BAG UI =====\n");
            sb.AppendLine($"bag go='{bag.gameObject.name}' pos={bag.transform.position} canvas={(bag.canvas != null ? bag.canvas.gameObject.name : "null")} alpha={bag.canvas?.alpha}");
            var root = bag.canvas != null ? bag.canvas.transform : bag.transform;
            // ルート Canvas まで遡る
            var top = root;
            while (top.parent != null && top.GetComponent<Canvas>() == null) top = top.parent;
            sb.AppendLine($"top='{top.name}' path={PathOf(root)}");
            Walk(top, 0, sb, 0);
            Main.Log(sb.ToString());
        }

        static int _count;
        static void Walk(Transform t, int depth, StringBuilder sb, int itemDepth)
        {
            if (_count++ > 400) return;
            // 一覧の項目は1件だけ詳しく出す
            bool isItem = t.GetComponent<UiItemParams>() != null;
            var rt = t.GetComponent<RectTransform>();
            var comps = t.GetComponents<Component>().Select(c => c.GetIl2CppType().Name).Where(n => n != "Transform" && n != "RectTransform");
            var line = new StringBuilder($"{new string(' ', depth * 2)}- {t.name} [{(t.gameObject.activeSelf ? "on" : "off")}] {{{string.Join(",", comps)}}}");
            if (rt != null) line.Append($" anchor=({rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##})-({rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}) pos={rt.anchoredPosition} size={rt.sizeDelta}");
            var tmp = t.GetComponent<TMP_Text>();
            if (tmp != null) line.Append($" TEXT='{Short(tmp.text)}' font={tmp.font?.name} size={tmp.fontSize} color={tmp.color}");
            var img = t.GetComponent<Image>();
            if (img != null) line.Append($" IMG sprite={img.sprite?.name} color={img.color} type={img.type}");
            sb.AppendLine(line.ToString());
            if (isItem && itemDepth++ > 0) return;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c.GetComponent<UiItemParams>() != null && i > 0) { if (i == 1) sb.AppendLine($"{new string(' ', depth * 2 + 2)}... ({t.childCount} items)"); continue; }
                Walk(c, depth + 1, sb, itemDepth);
            }
        }

        static string Short(string s) => s == null ? "" : (s.Length > 40 ? s.Substring(0, 40) : s).Replace("\n", "\\n");

        static string PathOf(Transform t)
        {
            var a = new List<string>();
            for (; t != null; t = t.parent) a.Insert(0, t.name);
            return string.Join("/", a);
        }
    }
}
