using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using UnityEngine.UI;

using FPModOption;

namespace FPRarity
{
    // 確認用（autotest -Scenario stashshot のときだけ動く）。
    // プレイヤーと同じく保管庫ページを開き、上から順にスクロールしながら画面を撮影する。
    // あわせて各項目の画像部品（Image）の色を書き出し、枠として見えている部品がどれかを調べる。
    public static class StashShot
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");

        public static List<(string, Action)> Steps()
        {
            Directory.CreateDirectory(ShotDir);
            foreach (var f in Directory.GetFiles(ShotDir, "*.png")) File.Delete(f);
            var steps = new List<(string, Action)>
            {
                ("open stash (Interact)", () => Refs.Bag.Interact()),
                ("wait", () => { }), ("wait", () => { }), ("wait", () => { }),
                ("dump item graphics", DumpItems),
            };
            foreach (var pos in new[] { 1f, 0.6f, 0.3f, 0f })
            {
                var p = pos;
                steps.Add(($"scroll {p}", () => Scroll(p)));
                steps.Add(("wait", () => { }));
                steps.Add(($"shot {p}", () => ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, $"stash_{(int)(p * 100):000}.png"))));
            }
            return steps;
        }

        static void Scroll(float pos)
        {
            var sr = Refs.Bag.StashList.GetComponentInParent<ScrollRect>();
            if (sr != null) sr.verticalNormalizedPosition = pos;
            Main.Log($"[shot] scroll={pos} scrollRect={(sr != null)}");
        }

        static void DumpItems()
        {
            var list = Refs.Bag.StashList;
            for (int i = 0; i < list.childCount && i < Tracker.Stash.Count; i++)
            {
                var item = list.GetChild(i);
                var p = item.GetComponent<UiItemParams>();
                var r = Tracker.Stash[i];
                var imgs = item.GetComponentsInChildren<Image>(true)
                    .Select(g => $"{g.gameObject.name}{(p != null && p.Background != null && g.Pointer == p.Background.Pointer ? "*" : "")}=({g.color.r:0.00},{g.color.g:0.00},{g.color.b:0.00},{g.color.a:0.00})");
                Main.Log($"[shot] stash[{i}] '{p?.texts[0].text}' roll={r.Tier}{(r.Legacy ? "(legacy)" : "")} want={RarityTable.ColorOf(r.Tier)} :: {string.Join(" ", imgs)}");
            }
        }
    }
}
