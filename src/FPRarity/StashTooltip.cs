using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using FPModOption;

namespace FPRarity
{
    // 保管庫の画面（インベントリ側・保管庫側の一覧）と、インベントリ画面（Tab）の武器の枠にカーソルを合わせると、
    // 地面の武器を見たときと同じ性能の表示を出す。
    //
    // ・表示の枠は、ゲームの拾うときのツールチップ（UserInteractions.tooltip）を複製して使う（見た目を合わせるため）
    // ・文章は地面の武器と同じ作り方：その武器の拾える物（LootableItem）にゲームの GetTooltipText を作らせ、
    //   MOD の書き換え（TooltipRewriter）を一覧の武器のロールで行う。拾える物は画面外に 1 つずつ置き、画面を閉じたら消す
    // ・カーソルの下の項目は、EventSystem の UI レイキャストで調べる
    public static class StashTooltip
    {
        static GameObject _panel;
        static RectTransform _holder;
        static TMP_Text _text, _rarity;
        static Graphic _background;
        static RectTransform _panelRect;
        static readonly Dictionary<int, GameObject> _probes = new();   // 武器 ID → 文章作り用の拾える物
        static bool _failed;

        // 他の Mod の、保管庫の上に重ねるページ（FPCustom のカスタマイズ）が開いている間は出さない

        // Main.OnUpdate から呼ぶ
        public static void Tick()
        {
            if (_failed) return;
            try
            {
                if (Link.CustomPageOpen != null && Link.CustomPageOpen()) { Hide(); return; }
                var bag = Refs.Bag;
                bool bagOpen = bag != null && bag._stashOpened && !MasteryUi.IsOpen;
                // インベントリ画面などでカーソルが出ている間だけ調べる（遊んでいる間は何もしない）
                if (!bagOpen && !Cursor.visible) { Hide(); ClearProbes(); return; }

                var roll = HoveredRoll(bagOpen);
                if (roll == null) { Hide(); return; }
                var wi = Tracker.ItemById(roll.WeaponId);
                var li = Probe(wi);
                if (wi == null || li == null) { Hide(); return; }   // 拾える物が次のフレームで準備できる

                string raw;
                Patches.RawTooltip = true;
                try { raw = li.GetTooltipText(); }
                finally { Patches.RawTooltip = false; }
                if (string.IsNullOrEmpty(raw)) { Hide(); return; }

                if (!EnsurePanel()) return;
                _text.text = TooltipRewriter.Rewrite(raw, li, roll);
                if (_rarity != null) _rarity.text = UiPatches.TierName(roll.Tier);   // 文字は白（枠がレアリティの色なので、同じ色だと読めない）
                UiPatches.Recolor(_background, roll.Tier);
                _panel.SetActive(true);
                _panel.transform.SetAsLastSibling();
                PlaceNearCursor();
            }
            catch (Exception e)
            {
                _failed = true;
                Hide();
                Main.Log("[stashtip] disabled: " + e);
                GameVersion.AddDegraded("保管庫・インベントリの性能表示");
            }
        }

        // カーソルの下にある武器のロール（保管庫の一覧の項目、またはインベントリ画面の武器の枠・詳細欄）
        static WeaponRoll HoveredRoll(bool bagOpen)
        {
            var es = EventSystem.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (es == null || mouse == null) return null;
            var data = new PointerEventData(es) { position = mouse.position.ReadValue() };
            var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            es.RaycastAll(data, hits);
            if (hits.Count == 0) return null;
            var bag = Refs.Bag;
            for (int i = 0; i < hits.Count; i++)
            {
                var go = hits[i].gameObject;
                if (go == null) continue;
                if (bagOpen)
                {
                    var item = go.GetComponentInParent<UiItemParams>();
                    if (item == null) continue;
                    var parent = item.transform.parent;
                    int index = item.transform.GetSiblingIndex();
                    WeaponRoll roll = null;
                    if (parent != null && bag.StashList != null && parent.Pointer == bag.StashList.Pointer)
                        roll = index < Tracker.Stash.Count ? Tracker.Stash[index] : null;
                    else if (parent != null && bag.InventoryList != null && parent.Pointer == bag.InventoryList.Pointer)
                        roll = index < Tracker.Held.Count ? Tracker.Held[index].Roll : null;
                    var label = item.texts != null && item.texts.Length > 0 ? item.texts[0]?.text : null;
                    return roll != null && UiPatches.LabelIs(label, roll.WeaponId) ? Features.View(roll) : null;   // 弾薬などの項目、並びの作り直し中は出さない
                }
                var slotRoll = InventorySlotRoll(go.transform);
                if (slotRoll != null) return slotRoll;
            }
            return null;
        }

        // インベントリ画面（Tab）の武器の枠。枠の中の武器の絵（または開いた詳細欄）の上なら、その枠の武器のロール
        static InventoryUI _inv;
        static WeaponRoll InventorySlotRoll(Transform hit)
        {
            if (_inv == null) _inv = UnityEngine.Object.FindObjectOfType<InventoryUI>();
            var inv = _inv;
            if (inv == null) return null;
            var icons = new[] { inv.MainWeaponOne, inv.MainWeaponTwo, inv.SmallWeaponOne, inv.SmallWeaponTwo };
            Image slot = null;
            foreach (var icon in icons)
                if (icon != null && icon.transform.parent != null && hit.IsChildOf(icon.transform.parent)) { slot = icon; break; }
            if (slot == null && inv.WeaponDetails != null)
            {
                // 詳細欄の上: 縦の位置がいちばん近い枠の武器
                for (int i = 0; i < inv.WeaponDetails.Count && slot == null; i++)
                {
                    var frame = inv.WeaponDetails[i]?.transform.parent;
                    if (frame == null || !hit.IsChildOf(frame)) continue;
                    float best = float.MaxValue;
                    foreach (var icon in icons)
                    {
                        if (icon == null) continue;
                        float d = Mathf.Abs(icon.transform.position.y - frame.position.y);
                        if (d < best) { best = d; slot = icon; }
                    }
                }
            }
            if (slot == null || slot.sprite == null) return null;
            // 絵の画像から、どの手持ちの武器かを決める（UiPatches の枠の色付けと同じ決め方）
            foreach (var h in Tracker.Held)
            {
                var wi = Tracker.ItemByPrefab(h.Prefab);
                if (wi?.Image != null && wi.Image.Pointer == slot.sprite.Pointer) return Features.View(h.Roll);
            }
            return null;
        }

        // その武器の拾える物（画面外・物理なし）。置いた次のフレームから文章を作れる（ゲームの Start が済んでから）
        static LootableItem Probe(WeaponItem wi)
        {
            if (wi == null) return null;
            if (_probes.TryGetValue(wi.Id, out var go))
                return go != null && Time.frameCount > _probeFrame[wi.Id] ? go.GetComponentInChildren<LootableItem>() : null;
            LootableItem prefab = null;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<LootableItem>()))
            {
                var li = o.TryCast<LootableItem>();
                if (li != null && !li.gameObject.scene.IsValid() && li.ItemType == LootableItem.LootType.Weapon && li.ItemId == wi.Id) { prefab = li; break; }
            }
            if (prefab == null) { _probes[wi.Id] = null; _probeFrame[wi.Id] = int.MaxValue; return null; }
            go = UnityEngine.Object.Instantiate(prefab.gameObject, new Vector3(0f, -5000f, 0f), Quaternion.identity);
            go.name = prefab.gameObject.name + " (FPR stash tooltip)";
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>()) { rb.isKinematic = true; rb.useGravity = false; }
            foreach (var c in go.GetComponentsInChildren<Collider>()) c.enabled = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) r.enabled = false;
            _probes[wi.Id] = go;
            _probeFrame[wi.Id] = Time.frameCount;
            return null;
        }

        static readonly Dictionary<int, int> _probeFrame = new();

        static void ClearProbes()
        {
            if (_probes.Count == 0) return;
            foreach (var go in _probes.Values) if (go != null) UnityEngine.Object.Destroy(go);
            _probes.Clear();
            _probeFrame.Clear();
        }

        // 地面の武器のツールチップと同じ見た目の枠を作る（文字のフォント・大きさ・色はゲームのツールチップから写す）。
        // ゲームのツールチップをそのまま複製すると、配置の仕組み（親の大きさに合わせる設定）が崩れて文字が縦一列になるため、自前で組む
        const float PanelWidth = 520f, Padding = 18f;

        static bool EnsurePanel()
        {
            if (_panel != null && _holder != null) return true;
            var ui = Refs.Interactions;
            var srcText = ui?._tooltipText;
            if (srcText == null) return false;

            // 画面の一番手前に出す専用のキャンバス（画面の座標そのまま）
            var holder = new GameObject("FPR stash tooltip canvas");
            var canvas = holder.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            _holder = holder.GetComponent<RectTransform>();

            _panel = new GameObject("FPR stash tooltip");
            _panel.transform.SetParent(holder.transform, false);
            _panelRect = _panel.AddComponent<RectTransform>();
            _panelRect.sizeDelta = new Vector2(PanelWidth, 100f);
            var bg = _panel.AddComponent<Image>();
            bg.raycastTarget = false;
            var srcBg = ui.tooltipBackground;
            if (srcBg != null && srcBg.TryCast<Image>() is Image srcImage) { bg.sprite = srcImage.sprite; bg.type = srcImage.type; }
            bg.color = srcBg != null ? srcBg.color : new Color(0.6f, 0.6f, 0.6f, 0.8f);   // 透明度はゲームのツールチップと同じ（色はレアリティで塗る）
            _background = bg;
            var layout = _panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset((int)Padding, (int)Padding, (int)Padding, (int)Padding);
            layout.spacing = 6f;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            var fit = _panel.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _text = NewText("text", srcText);
            _rarity = NewText("rarity", srcText);
            // ゲームは文字欄ごとの翻訳フック（LocTextHook）で表示時に訳す。作った欄にも付ける（ゲームの見出しを訳すため）
            try { Il2CppGameLocalization.LocTextHook.Install(_text); } catch (Exception e) { Main.Log("[stashtip] translate hook failed: " + e.Message); }
            _panel.SetActive(false);
            Main.Log($"[stashtip] panel ready (font={srcText.font?.name} size={srcText.fontSize})");
            return true;
        }

        static TMP_Text NewText(string name, TMP_Text like)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_panel.transform, false);
            go.AddComponent<RectTransform>();
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = like.font;
            t.fontSharedMaterial = like.fontSharedMaterial;
            t.fontSize = like.enableAutoSizing ? Math.Max(like.fontSizeMax * 0.6f, 20f) : like.fontSize;
            t.color = like.color;
            t.alignment = TextAlignmentOptions.Center;
            t.richText = true;
            t.enableWordWrapping = true;
            t.raycastTarget = false;
            return t;
        }

        // カーソルの右下（画面の端では左や上）に出す
        static void PlaceNearCursor()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || _panelRect == null) return;
            var p = mouse.position.ReadValue();
            bool left = p.x > Screen.width * 0.6f, up = p.y < Screen.height * 0.45f;
            // 元のツールチップは出るときに大きさ 0 から広がる演出がある。複製は常に等倍・回転なしで置く
            _panelRect.localScale = Vector3.one;
            _panelRect.localRotation = Quaternion.identity;
            _panelRect.anchorMin = _panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            _panelRect.pivot = new Vector2(left ? 1f : 0f, up ? 0f : 1f);
            if (_holder == null) return;
            var offset = new Vector2(left ? -24f : 24f, up ? 24f : -24f);
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_holder, p + offset, null, out var local))
                _panelRect.localPosition = new Vector3(local.x, local.y, 0f);
        }

        // テスト用
        public static bool Visible => _panel != null && _panel.activeSelf;
        public static string Text => _text != null ? _text.text : "";
        public static string Debug()
        {
            if (_panelRect == null) return "no panel";
            var corners = new Vector3[4];
            _panelRect.GetWorldCorners(corners);
            var canvas = _panel.GetComponentInParent<Canvas>();
            var cgs = string.Join(",", System.Linq.Enumerable.Select(_panel.GetComponentsInParent<CanvasGroup>(true), c => $"{c.gameObject.name}:{c.alpha:0.00}"));
            return $"parent={_panelRect.parent?.name} canvas={canvas?.name} mode={canvas?.renderMode} sort={canvas?.sortingOrder} " +
                   $"local={_panelRect.localPosition} scale={_panelRect.localScale} size={_panelRect.rect.size} corners={corners[0]}..{corners[2]} " +
                   $"activeInHierarchy={_panel.activeInHierarchy} groups=[{cgs}] textAlpha={_text?.alpha} bg={_background?.color}";
        }
        public static string Shown => _text != null ? System.Text.RegularExpressions.Regex.Replace(_text.GetParsedText() ?? "", "<[^>]+>", "") : "";

        static void Hide()
        {
            if (_panel != null && _panel.activeSelf) _panel.SetActive(false);
        }
    }
}
