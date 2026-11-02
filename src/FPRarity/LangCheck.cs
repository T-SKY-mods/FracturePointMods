using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Il2Cpp;
using Il2CppGameLocalization;
using MelonLoader;
using UnityEngine;

using FPModOption;

namespace FPRarity
{
    // 確認用（autotest -Scenario langcheck）。拾うときのツールチップとインベントリの武器の詳細欄を、
    // 日本語と英語で表示してスクリーンショットを撮り、MOD が足した部分の言語・レアリティの重複を確かめる。
    // 言語はゲームの LocManager.SetLanguage で切り替え、最後に元に戻す（設定は autotest.ps1 が前後で元に戻す）。
    public static class LangCheck
    {
        static GameObject _item;
        static string _original;

        public static List<(string, Action)> Steps()
        {
            var steps = new List<(string, Action)>
            {
                ("remember language", (Action)(() => { _original = LocManager.Code; Main.Log($"[lang] game language = {_original}"); })),
            };
            foreach (var code in new[] { "ja", "en" })
            {
                string c = code;
                steps.Add(($"language {c}", (Action)(() => { LocManager.SetLanguage(c); Main.Log($"[lang] set {c}: now {LocManager.Code}, Japanese={UiPatches.Japanese}"); })));
                steps.Add(("place a weapon in front of the camera", PlaceWeapon));
                steps.Add(("wait", () => { }));
                steps.Add(($"tooltip ({c})", () => CheckTooltip(c)));
                steps.Add(("open inventory (Tab)", () => Tap(VK_TAB)));
                steps.Add(("wait", () => { }));
                foreach (var slot in new[] { "main", "pistol" })
                {
                    string sl = slot;
                    steps.Add(($"hover the {sl} slot", () => ClickSlot(sl == "main", click: false)));
                    steps.Add(($"inventory popup ({c}, {sl})", () => CheckInventory(c, sl)));
                }
                steps.Add(("close inventory (Tab)", () => Tap(VK_TAB)));
                steps.Add(("wait", () => { }));
            }
            steps.Add(("restore language", (Action)(() => { if (!string.IsNullOrEmpty(_original)) LocManager.SetLanguage(_original); if (_item != null) UnityEngine.Object.Destroy(_item); })));
            return steps;
        }

        // 確認用（autotest -Scenario stashtip）。保管庫を開き、実際のカーソルを一覧の武器に重ねて、性能の表示を確かめる（日本語・英語）
        public static List<(string, Action)> StashSteps()
        {
            var steps = new List<(string, Action)> { ("remember language", (Action)(() => _original = LocManager.Code)) };
            foreach (var code in new[] { "ja", "en" })
            {
                string c = code;
                steps.Add(($"language {c}", (Action)(() => LocManager.SetLanguage(c))));
                steps.Add(("open stash", (Action)(() => Refs.Bag.Interact())));
                steps.Add(("wait", (Action)(() => { })));
                steps.Add(("wait", (Action)(() => { })));
                foreach (var list in new[] { "stash", "inventory" })
                {
                    string l = list;
                    steps.Add(($"hover a {l} weapon", (Action)(() => HoverWeapon(l == "stash"))));
                    steps.Add(($"check ({c}, {l})", (Action)(() =>
                    {
                        string plain = Plain(StashTooltip.Text);
                        Main.Log($"[stashtip] {c} {l}: visible={StashTooltip.Visible}\n{plain}");
                        Check($"{c} {l}: stats panel shown on hover", StashTooltip.Visible && plain.Contains("->"));
                        Main.Log("[stashtip] panel " + StashTooltip.Debug());
                        var shown = StashTooltip.Shown;
                        if (c == "ja") Check($"ja {l}: game labels translated in the panel", JapaneseChars.IsMatch(shown.Split('\n')[1]));
                        Check($"{c} {l}: roll lines in the panel", c == "ja" ? plain.Contains("連射速度") || plain.Contains("リロード速度") : plain.Contains("Fire rate") || plain.Contains("Reload speed"));   // ハンドガンはリロード速度
                        if (c == "en") Check($"en {l}: mod lines have no Japanese", !JapaneseChars.IsMatch(plain.Substring(Math.Max(0, plain.IndexOf("Damage ", StringComparison.Ordinal)))));
                        Shot($"stashtip_{c}_{l}");
                    })));
                }
                steps.Add(("close stash (Esc)", (Action)(() => Tap(0x1B))));
                steps.Add(("wait", (Action)(() => { })));
            }
            steps.Add(("restore language", (Action)(() => { if (!string.IsNullOrEmpty(_original)) LocManager.SetLanguage(_original); })));
            return steps;
        }

        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
        struct POINT { public int X, Y; }

        // 一覧の最初の武器の項目の中央にカーソルを置く（ゲームの座標は左下が原点、Windows は左上）
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

        // インベントリの武器の枠を実際にクリックして、詳細欄を開く
        static void ClickSlot(bool main, bool click = true)
        {
            var inv = Main.FindAll<InventoryUI>().FirstOrDefault();
            var img = main ? inv?.MainWeaponOne : inv?.SmallWeaponOne;
            if (img == null) { Main.Log("[lang] slot not found"); return; }
            var rt = img.rectTransform;
            var canvas = img.canvas;
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var sp = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.center));
            var pt = new POINT { X = (int)sp.x, Y = Screen.height - (int)sp.y };
            ClientToScreen(Process.GetCurrentProcess().MainWindowHandle, ref pt);
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid != (uint)Process.GetCurrentProcess().Id) { Main.Log("[lang] game window not in the foreground: click not sent"); return; }
            SetCursorPos(pt.X, pt.Y);
            if (click) { mouse_event(0x02, 0, 0, 0, UIntPtr.Zero); mouse_event(0x04, 0, 0, 0, UIntPtr.Zero); }
            Main.Log($"[lang] {(click ? "clicked" : "cursor on")} {(main ? "main" : "pistol")} slot at {pt.X},{pt.Y}");
        }

        static void HoverWeapon(bool stash)
        {
            var bag = Refs.Bag;
            var list = stash ? bag?.StashList : bag?.InventoryList;
            if (list == null || list.childCount == 0) { Check($"{(stash ? "stash" : "inventory")} list has items", false); return; }
            // MOD 導入前から持っていた武器（ロールなし）ではなく、ロールのある武器の項目を選ぶ
            int index = 0;
            for (int i = 0; i < list.childCount; i++)
            {
                var r = stash ? (i < Tracker.Stash.Count ? Tracker.Stash[i] : null) : (i < Tracker.Held.Count ? Tracker.Held[i].Roll : null);
                if (r != null && !r.Legacy) { index = i; break; }
            }
            var item = list.GetChild(index).GetComponent<RectTransform>();
            var canvas = item.GetComponentInParent<Canvas>();
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var sp = RectTransformUtility.WorldToScreenPoint(cam, item.TransformPoint(item.rect.center));
            var pt = new POINT { X = (int)sp.x, Y = Screen.height - (int)sp.y };
            ClientToScreen(Process.GetCurrentProcess().MainWindowHandle, ref pt);
            SetCursorPos(pt.X, pt.Y);
            Main.Log($"[stashtip] cursor on '{item.name}' screen {sp} -> {pt.X},{pt.Y}");
        }

        static void PlaceWeapon()
        {
            if (_item != null) UnityEngine.Object.Destroy(_item);
            var cam = Camera.main;
            var prefab = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<LootableItem>())
                .Select(o => o.TryCast<LootableItem>())
                .FirstOrDefault(li => li != null && !li.gameObject.scene.IsValid() && li.ItemType == LootableItem.LootType.Weapon);
            if (cam == null || prefab == null) { Check("weapon placed", false); return; }
            _item = UnityEngine.Object.Instantiate(prefab.gameObject, cam.transform.position + cam.transform.forward * 1.2f, cam.transform.rotation);
            foreach (var rb in _item.GetComponentsInChildren<Rigidbody>()) { rb.isKinematic = true; rb.useGravity = false; }
            Main.Log($"[lang] placed '{prefab.gameObject.name}'");
        }

        static readonly Regex JapaneseChars = new(@"[぀-ヿ一-鿿]", RegexOptions.Compiled);
        static string Plain(string s) => Regex.Replace(s ?? "", "<[^>]+>", "");

        static void CheckTooltip(string code)
        {
            var li = _item?.GetComponentInChildren<LootableItem>();
            var roll = li == null ? null : Tracker.GroundRoll(li);
            if (li == null || roll == null) { Check($"{code}: ground weapon has a roll", false); return; }
            string tip = li.GetTooltipText();
            // MOD が足したのは、ゲームの最後の行（重さなど）より後ろ。個体差の見出し以降を取り出す
            string plain = Plain(tip);
            Main.Log($"[lang] {code} tooltip:\n{plain}");
            // 個体差の最初の行（ダメージ +10（+5%） / Damage +10 (+5%)）
            var first = new Regex(code == "ja" ? @"ダメージ [+-]\d+（" : @"Damage [+-]\d+ \(").Match(plain);
            string marker = "roll lines";
            int at = first.Success ? first.Index : -1;
            Check($"{code}: tooltip shows the roll quality line ('{marker}')", at >= 0);
            if (at >= 0)
            {
                int start = plain.LastIndexOf('\n', at) + 1;
                string modPart = plain.Substring(start);
                string tier = UiPatches.TierName(roll.Tier);
                Check($"{code}: rarity name not repeated in the mod lines ('{tier}')", !modPart.Contains(tier) && !modPart.Contains(roll.Tier.ToString()));
                if (code == "en") Check("en: mod lines have no Japanese", !JapaneseChars.IsMatch(modPart));
            }
            var ui = Refs.Interactions;
            Main.Log($"[lang] {code} rarity text='{ui?.ItemRarityText?.text}' tooltip active={ui?.tooltip?.activeInHierarchy}");
            Shot($"lang_{code}_tooltip");
        }

        // インベントリ画面の武器の枠にカーソルを合わせたとき、保管庫と同じポップアップが出るか
        static void CheckInventory(string code, string slot)
        {
            string plain = Plain(StashTooltip.Text);
            Main.Log($"[lang] {code} inventory {slot}: popup visible={StashTooltip.Visible}\n{plain}");
            Check($"{code} {slot}: inventory popup shown on hover", StashTooltip.Visible && plain.Contains("->"));
            // ハンドガンは連射速度の代わりにリロード速度の行
            var rate = new Regex(code == "ja" ? @"連射速度 [+-]\d+ RPM" : @"Fire rate [+-]\d+ RPM");
            var reload = new Regex(code == "ja" ? @"リロード速度 [+-]\d+%" : @"Reload speed [+-]\d+%");
            bool pistol = slot == "pistol";
            Check($"{code} {slot}: popup has the roll lines ({(pistol ? "reload" : "fire rate")})",
                  pistol ? reload.IsMatch(plain) && !rate.IsMatch(plain) : rate.IsMatch(plain) && !reload.IsMatch(plain));
            if (code == "en") Check($"en {slot}: popup mod lines have no Japanese", !JapaneseChars.IsMatch(plain.Substring(Math.Max(0, plain.IndexOf("Damage ", StringComparison.Ordinal)))));
            Shot($"lang_{code}_inventory_{slot}");
        }

        static void Shot(string name)
        {
            Directory.CreateDirectory(Path.Combine(Main.Dir, "shots"));
            ScreenCapture.CaptureScreenshot(Path.Combine(Main.Dir, "shots", name + ".png"));
        }

        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");

        // ---------- 実際のキー入力（ゲームのウィンドウが前面のときだけ） ----------
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
        const byte VK_TAB = 0x09;

        static void Tap(byte vk)
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid != (uint)Process.GetCurrentProcess().Id) { Main.Log("[lang] game window not in the foreground: key not sent"); return; }
            byte scan = (byte)MapVirtualKey(vk, 0);
            keybd_event(vk, scan, 0, UIntPtr.Zero);
            MelonCoroutines.Start(Release(vk, scan));
        }

        static IEnumerator Release(byte vk, byte scan)
        {
            yield return null; yield return null;
            keybd_event(vk, scan, 0x0002, UIntPtr.Zero);
        }
    }
}
