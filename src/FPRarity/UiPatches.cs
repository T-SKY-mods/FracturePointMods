using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Il2Cpp;
using Il2CppGameLocalization;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

using FPModOption;

namespace FPRarity
{
    // 画面表示をModのレアリティ・性能に合わせる。
    // ゲームは武器データ（WeaponItem）のレアリティで色を付けるが、WeaponItem.GetRarity は
    // 他の関数と実体を共有していて差し替えられないため、各画面の描画処理の後で上書きする。
    public static class UiPatches
    {
        public static void Install(HarmonyLib.Harmony h)
        {
            var c = typeof(UiPatches);
            SafePatcher.Patch(h, typeof(UserInteractions), "UpdateTooltip", null, null, nameof(UpdateTooltip_Post), c);
            SafePatcher.Patch(h, typeof(InventoryUI), "UpdateWeapons", null, null, nameof(UpdateWeapons_Post), c);
            SafePatcher.Patch(h, typeof(InventoryUI), "UpdateWeaponDetails", null, null, nameof(UpdateWeaponDetails_Post), c);
            SafePatcher.Patch(h, typeof(SaveBag), "SetupItemButton", null, null, nameof(SetupItemButton_Post), c);
            // 保管庫を開くと UpdateUI が呼ばれ、その中に下の2関数がインライン展開されている（直接は呼ばれない）
            SafePatcher.Patch(h, typeof(SaveBag), "UpdateUI", null, nameof(BagList_Pre), null, c);
            SafePatcher.Patch(h, typeof(SaveBag), "UpdateInventoryList", null, nameof(BagList_Pre), null, c);
            SafePatcher.Patch(h, typeof(SaveBag), "UpdateStashList", null, nameof(BagList_Pre), null, c);
            // 武器を手に入れたときのゲームの通知「〇〇 added to your inventory」の色
            SafePatcher.Patch(h, typeof(Inventory), "AddWeapon", new[] { typeof(WeaponItem) }, nameof(AddWeaponItem_Pre), nameof(AddWeapon_Post), c);
            SafePatcher.Patch(h, typeof(Inventory), "AddWeapon", new[] { typeof(int) }, nameof(AddWeaponIndex_Pre), nameof(AddWeapon_Post), c);
            SafePatcher.Patch(h, typeof(NotificationsManager), "NotifyWithColor", null, nameof(NotifyWithColor_Pre), null, c);
        }

        // ---------- 言語 ----------

        public static bool Japanese
        {
            get { try { return (LocManager.Code ?? "").StartsWith("ja"); } catch { return false; } }
        }

        public static string TierName(Tier t)
        {
            // 日本語ではゲームの訳語（レジェンダリー など）、それ以外の言語では英語
            if (!Japanese) return t.ToString();
            try { return LocManager.Translate(t.ToString()) ?? t.ToString(); } catch { return t.ToString(); }
        }

        // 色だけ差し替え、透明度は元のまま
        internal static void Recolor(Graphic g, Tier t)
        {
            if (g == null) return;
            var c = RarityTable.ColorOf(t);
            c.a = g.color.a;
            g.color = c;
        }

        // ---------- 武器を手に入れたときの通知の色 ----------
        // ゲームは AddWeapon の最後に、武器データのレアリティの色で通知を出す（地面・倉庫・店・報酬・FPSurvival の武器箱など、すべての入手経路）。
        // AddWeapon の間に出る色付きの通知は、その武器に付くロールの色にする

        static readonly List<WeaponItem> _adding = new();
        public static readonly List<(string text, Color color)> AddNotices = new();   // テスト用：AddWeapon の中で出た通知と最終的な色

        static void AddWeaponItem_Pre(WeaponItem weapon) => _adding.Add(weapon);
        static void AddWeaponIndex_Pre(Inventory __instance, int index)
        {
            WeaponItem wi = null;
            try { var ws = __instance?.WeaponData?.Weapons; if (ws != null && index >= 0 && index < ws.Count) wi = ws[index]; } catch { }
            _adding.Add(wi);
        }
        static void AddWeapon_Post() { if (_adding.Count > 0) _adding.RemoveAt(_adding.Count - 1); }

        static void NotifyWithColor_Pre(string notification, ref Color color)
        {
            try
            {
                if (_adding.Count == 0) return;
                // その武器の名前が入った通知だけ（マガジンなど、ほかの通知の色は変えない）
                var wi = _adding[_adding.Count - 1];
                string name = null;
                try { name = wi?.GetName(); } catch { }
                if (string.IsNullOrEmpty(name) || notification == null || !notification.Contains(name)) return;
                var r = Tracker.RollForAdding(wi);
                if (r != null)
                {
                    var c = RarityTable.ColorOf(r.Tier);
                    c.a = color.a;
                    color = c;
                }
                AddNotices.Add((notification, color));
                if (AddNotices.Count > 20) AddNotices.RemoveAt(0);
            }
            catch (Exception e) { LogOnce("addnotify", e); }
        }

        // ---------- 拾うときのツールチップ（枠の色とレアリティ名） ----------

        static void UpdateTooltip_Post(UserInteractions __instance, Interactable interactable)
        {
            try
            {
                var li = interactable?.TryCast<LootableItem>();
                var r = li == null ? null : Tracker.GroundRoll(li);
                if (r == null) return;
                if (__instance.ItemRarityText != null)
                {
                    __instance.ItemRarityText.text = TierName(r.Tier);
                    Recolor(__instance.ItemRarityText, r.Tier);
                }
                Recolor(__instance.tooltipBackground, r.Tier);
            }
            catch (Exception e) { LogOnce("tooltipui", e); }
        }

        // ---------- インベントリ画面（武器スロットの枠） ----------

        static void UpdateWeapons_Post(InventoryUI __instance)
        {
            try
            {
                // 各スロットに表示中の武器画像から、どの手持ち武器かを特定して色を付ける。
                // （ゲームのスロット割り当て順は手持ちの並びと一致しないことがあるため、順番では決めない）
                var used = new HashSet<int>();
                Slot(__instance.MainWeaponOne, __instance.BigWeaponBackground, used);
                Slot(__instance.MainWeaponTwo, __instance.BigWeaponTwoBackground, used);
                Slot(__instance.SmallWeaponOne, __instance.SmallWeaponBackground, used);
                Slot(__instance.SmallWeaponTwo, __instance.SmallWeaponTwoBackground, used);
            }
            catch (Exception e) { LogOnce("invslots", e); }
        }

        static void Slot(Image icon, Image background, HashSet<int> used)
        {
            if (background == null || icon == null || icon.sprite == null || !icon.gameObject.activeInHierarchy) return;
            for (int i = 0; i < Tracker.Held.Count; i++)
            {
                if (used.Contains(i)) continue;
                var wi = Tracker.ItemByPrefab(Tracker.Held[i].Prefab);
                if (wi?.Image == null || wi.Image.Pointer != icon.sprite.Pointer) continue;
                used.Add(i);
                Recolor(background, Features.View(Tracker.Held[i].Roll).Tier);
                return;
            }
        }

        // ---------- インベントリ画面（武器の詳細欄） ----------

        // 例: "<b>Max damage:</b> 125" / "<b>Fire rate:</b> 650BPM"（ラベルは翻訳前の英語、表示時にゲームが翻訳する）
        static readonly Regex Value = new(@"^(?<key>.*?[:：](?:\s*</b>)?\s*)(?<val>.*)$", RegexOptions.Compiled);
        static readonly Regex Number = new(@"-?\d+(\.\d+)?", RegexOptions.Compiled);

        static void UpdateWeaponDetails_Post(TMP_Text text, TMP_Text secondText, WeaponItem weapon)
        {
            try
            {
                var r = Tracker.HeldRollFor(weapon);
                if (r == null) return;
                // 詳細パネルの枠（バニラは常に白）もレアリティの色にする
                Recolor(text?.transform.parent?.GetComponent<Image>(), r.Tier);
                if (weapon.Weapon == null) return;
                // 数値はレアリティのロール × 武器熟練度
                var bw = weapon.Weapon.GetComponentInChildren<Il2CppBallistics.Weapon>(true);
                int dmg = (int)MathF.Round(Mastery.ShownDamage(r, (bw != null ? bw.MuzzleDamage : 0) * Mastery.DamageMult(r)));   // アタッチメント（ほかの Mod）込み
                int ctl = Math.Max(0, weapon.Control + Mastery.ShownControlDelta(r));   // コントロール・エルゴに上限はない。アタッチメント（ほかの Mod）込み
                int erg = Math.Max(0, weapon.Ergonomic + Mastery.ShownErgoDelta(r));
                int rpm = (int)MathF.Round(Mastery.ShownRpm(r, weapon.Weapon.fireRate * Mastery.FireRateMult(r)));

                if (text != null) text.text = RewriteLines(text.text, r, dmg, ctl, erg, rpm);
                if (secondText != null) secondText.text = RewriteLines(secondText.text, r, dmg, ctl, erg, rpm);
            }
            catch (Exception e) { LogOnce("details", e); }
        }

        const string MasteryMark = "<link=\"fpm\"></link>";

        static string RewriteLines(string s, WeaponRoll r, int dmg, int ctl, int erg, int rpm)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var lines = s.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var m = Value.Match(lines[i].TrimEnd('\r'));
                if (!m.Success) continue;
                var key = m.Groups["key"].Value.ToLowerInvariant();
                var val = m.Groups["val"].Value;
                string v = null;
                if (key.Contains("rarity") || key.Contains("レアリティ")) v = $"<color=#{RarityTable.Hex(r.Tier)}>{TierName(r.Tier)}</color>";
                else if (key.Contains("damage") || key.Contains("ダメージ")) v = Number.Replace(val, dmg.ToString(), 1);
                else if (key.Contains("control") || key.Contains("コントロール")) v = Number.Replace(val, ctl.ToString(), 1);
                else if (key.Contains("ergonom") || key.Contains("エルゴ")) v = Number.Replace(val, erg.ToString(), 1);
                else if (key.Contains("fire rate") || key.Contains("連射")) v = Number.Replace(val, rpm.ToString(), 1);
                if (v != null) lines[i] = m.Groups["key"].Value + v;
            }
            return string.Join("\n", lines);
        }

        // ---------- 保管庫画面（インベントリ側・保管庫側の一覧） ----------

        // 一覧は非同期で1件ずつ作られ、作り直しの際は古い項目の削除がフレーム末まで遅れる。
        // そのため「何番目の子か」ではなく「作り直し開始から何件目に作られたか」で武器と対応付ける。
        static readonly Dictionary<IntPtr, int> _built = new();
        static readonly List<float> _repaintAt = new();

        static void BagList_Pre(SaveBag __instance) => Safe("baglist", () =>
        {
            if (__instance.InventoryList != null) _built[__instance.InventoryList.Pointer] = 0;
            if (__instance.StashList != null) _built[__instance.StashList.Pointer] = 0;
            // 古い項目が消えたあとに全体を塗り直す（念のため数回）
            var now = Time.realtimeSinceStartup;
            _repaintAt.Add(now + 0.5f); _repaintAt.Add(now + 1.5f); _repaintAt.Add(now + 3f);
        });

        static void SetupItemButton_Post(UiItemParams item) => Safe("bagitem", () =>
        {
            var parent = item?.transform.parent;
            if (parent == null || !_built.TryGetValue(parent.Pointer, out var n)) return;
            _built[parent.Pointer] = n + 1;
            ColorBagItem(item, n);
            // 一覧の作成が終わった直後にも全体を塗り直す（最後の項目から 0.3 秒後）
            _repaintAt.Add(Time.realtimeSinceStartup + 0.3f);
        });

        // Main.OnUpdate から呼ぶ
        public static void Tick()
        {
            if (_repaintAt.Count == 0) return;
            var now = Time.realtimeSinceStartup;
            if (_repaintAt.RemoveAll(t => t <= now) == 0) return;
            Safe("repaint", () =>
            {
                var bag = Refs.Bag;
                if (bag == null) return;
                ColorChildren(bag.InventoryList);
                ColorChildren(bag.StashList);
            });
        }

        static void ColorChildren(Transform list)
        {
            if (list == null) return;
            for (int i = 0; i < list.childCount; i++)
                ColorBagItem(list.GetChild(i).GetComponent<UiItemParams>(), i);
        }

        // 一覧の先頭から武器が並び順どおりに表示される。index 番目の武器のロールで色を付ける（名前で照合）
        static void ColorBagItem(UiItemParams item, int index)
        {
            var bag = Refs.Bag;
            if (item == null || bag == null) return;
            var parent = item.transform.parent;
            WeaponRoll r = null;
            if (parent != null && bag.StashList != null && parent.Pointer == bag.StashList.Pointer)
                r = index < Tracker.Stash.Count ? Tracker.Stash[index] : null;
            else if (parent != null && bag.InventoryList != null && parent.Pointer == bag.InventoryList.Pointer)
                r = index < Tracker.Held.Count ? Tracker.Held[index].Roll : null;
            r = Features.View(r);
            if (r == null) return;

            var label = item.texts != null && item.texts.Length > 0 ? item.texts[0]?.text : null;
            if (!LabelIs(label, r.WeaponId)) return;
            Recolor(item.Background, r.Tier);
        }

        // 一覧の表示名が武器名か。ゲームは条件によって「SP-320 (last)」のように後ろへ文字を足すので、
        // 完全一致に加えて「武器名＋空白/括弧」で始まる場合も同じ武器とみなす（AKM と AKMS は区別する）
        public static bool LabelIs(string label, int weaponId)
        {
            if (label == null) return false;
            var t = label.Trim();
            var n = Tracker.NameOf(weaponId).Trim();
            return t == n || t.StartsWith(n + " ", StringComparison.Ordinal) || t.StartsWith(n + "(", StringComparison.Ordinal);
        }

        // ---------- 共通 ----------

        static readonly HashSet<string> _logged = new();

        static void Safe(string where, Action a)
        {
            try { a(); } catch (Exception e) { LogOnce(where, e); }
        }

        static void LogOnce(string where, Exception e)
        {
            if (_logged.Add(where)) Main.Log($"[ui:{where}] ERROR {e}");
        }
    }
}
