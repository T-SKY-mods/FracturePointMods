using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

using FPModOption;

namespace FPRarity
{
    // 保管庫画面に「熟練度」ページを追加する。
    // 見た目を合わせるため、文字・枠・カードは保管庫画面の部品（タイトル文字、説明文字、武器カード、一覧のスクロール枠）を複製して使う。
    //
    //   通常の保管庫画面 … 右上に [熟練度] ボタン
    //   熟練度ページ     … 左：全武器の一覧（カードに Lv）  右：選んだ武器の Lv・経験値バー・強化ポイント・強化ボタン・リセット
    //   Esc / Tab / [戻る] で保管庫画面に戻る
    public static class MasteryUi
    {
        public static void Install(HarmonyLib.Harmony h)
        {
            var c = typeof(MasteryUi);
            SafePatcher.Patch(h, typeof(SaveBag), "OpenPage", null, null, nameof(OpenPage_Post), c);
            // Esc/Tab は SaveBag.Update → ClosePage。熟練度ページを開いているときは、ページだけ閉じる
            if (!SafePatcher.Patch(h, typeof(SaveBag), "ClosePage", null, nameof(ClosePage_Pre), null, c))
                GameVersion.AddDegraded("熟練度画面の Esc で戻る");
        }

        static readonly Color Gold = new(1f, 0.85f, 0.4f, 1f);
        static readonly Color Dim = new(1f, 1f, 1f, 0.35f);
        static readonly Color PageBg = new(0.094f, 0.094f, 0.094f, 1f);

        static SaveBag _bag;
        static GameObject _openButton, _page;
        static Transform _listContent;
        static readonly List<(int id, UiItemParams card)> _cards = new();
        static int _selected = -1;
        public static bool IsOpen => _page != null && _page.activeSelf;

        // 右側の詳細欄
        static Image _icon, _xpFill;
        static TMP_Text _name, _level, _xp, _points, _status;
        static readonly TMP_Text[] _statValue = new TMP_Text[4], _statLabel = new TMP_Text[4];
        static readonly UiItemParams[] _plus = new UiItemParams[4];
        static UiItemParams _reset, _sight;
        static TMP_Text _sightLabel, _sightValue;

        static string T(string ja, string en) => UiPatches.Japanese ? ja : en;

        public static void OnSceneChanged()
        {
            _bag = null; _openButton = null; _page = null; _listContent = null; _cards.Clear(); _selected = -1; _wasOpen = false;
        }

        // プレイヤーが保管庫を開く経路（Interact）では OpenPage がインライン展開されていて呼ばれないため、
        // 開いている状態（_stashOpened）を見て組み立てる。閉じたらページも閉じておく
        static bool _wasOpen;
        public static void Tick()
        {
            if (Main.Scene != Main.HubScene) return;
            var bag = Refs.Bag;
            bool open = bag != null && bag._stashOpened;
            if (open && !_wasOpen) Safe("open", () =>
            {
                if (!Refs.Alive(_openButton) || _bag == null || _bag.Pointer != bag.Pointer) Build(bag);
                if (_page != null) _page.SetActive(false);
                if (_openButton != null)
                {
                    _openButton.SetActive(true);
                    // ゲームの言語が途中で変わっても合うように、開くたびに文字を付け直す
                    var label = _openButton.GetComponentInChildren<Il2CppTMPro.TMP_Text>(true);
                    if (label != null) label.text = T("熟練度", "MASTERY");
                }
            });
            if (!open && _wasOpen && _page != null && _page.activeSelf) _page.SetActive(false);
            _wasOpen = open;
        }

        // ---------- 開閉 ----------

        static void OpenPage_Post(SaveBag __instance) => Safe("open", () =>
        {
            if (!Refs.Alive(_openButton) || _bag == null || _bag.Pointer != __instance.Pointer) Build(__instance);
            if (_page != null) _page.SetActive(false);
        });

        static bool ClosePage_Pre()
        {
            try
            {
                if (!IsOpen) return true;
                ShowPage(false);
                return false; // 保管庫は閉じない
            }
            catch (Exception e) { LogOnce("close", e); return true; }
        }

        public static void ShowPage(bool show)
        {
            if (_page == null) return;
            if (show)
            {
                RefreshList();
                if (_selected < 0 && _cards.Count > 0) _selected = FirstSelection();
                RefreshDetails();
            }
            _page.SetActive(show);
            if (_openButton != null) _openButton.SetActive(!show);
            Main.Log($"[masteryui] page {(show ? "open" : "closed")}");
        }

        // 手に持っている武器があればそれを、なければ一覧の先頭を選ぶ
        static int FirstSelection()
        {
            Tracker.ActiveRoll(out var item);
            if (item != null && Mastery.Eligible(item.Id)) return item.Id;
            return _cards[0].id;
        }

        // ---------- 組み立て ----------

        static void Build(SaveBag bag)
        {
            _bag = bag;
            _cards.Clear();
            var canvas = bag.canvas != null ? bag.canvas.transform : null;
            if (canvas == null || bag.ItemPrefab == null) { Main.Log("[masteryui] stash canvas not found"); GameVersion.AddDegraded("熟練度画面"); return; }

            // 複製元の部品を名前と内容で探す
            TMP_Text title = null, help = null;
            ScrollRect leftScroll = null;
            Transform modal = null;
            for (int i = 0; i < canvas.childCount; i++)
            {
                var ch = canvas.GetChild(i);
                var tmp = ch.GetComponent<TMP_Text>();
                if (tmp != null && ch.gameObject.activeSelf)
                {
                    if (tmp.fontSize >= 40 && title == null) title = tmp;
                    else if (tmp.fontSize < 40 && help == null) help = tmp;
                }
                var sr = ch.GetComponent<ScrollRect>();
                if (sr != null && sr.content != null && bag.InventoryList != null && sr.content.Pointer == bag.InventoryList.Pointer) leftScroll = sr;
                if (ch.name.StartsWith("Modal")) modal = ch;
            }
            if (title == null || help == null || leftScroll == null)
            {
                Main.Log($"[masteryui] template missing title={title != null} help={help != null} leftScroll={leftScroll != null}");
                GameVersion.AddDegraded("熟練度画面");
                return;
            }
            int sibling = modal != null ? modal.GetSiblingIndex() : canvas.childCount;

            // 通常画面の [熟練度] ボタン（保管庫タイトルの行の右端）
            var ob = NewButton(canvas, T("熟練度", "MASTERY"), new Vector2(760, 494), new Vector2(220, 60), 28);
            ob.Button.onClick.AddListener(new Action(() => Safe("openbtn", () => ShowPage(true))));
            _openButton = ob.gameObject;
            _openButton.name = "FPR_MasteryButton";
            _openButton.transform.SetSiblingIndex(sibling);

            // ページ本体（保管庫画面の上に重ねる不透明な背景）
            _page = new GameObject("FPR_MasteryPage");
            var prt = _page.AddComponent<RectTransform>();
            prt.SetParent(canvas, false);
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
            prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;
            var bg = _page.AddComponent<Image>();
            bg.color = PageBg;
            _page.transform.SetSiblingIndex(sibling + 1);
            var page = _page.transform;

            CloneText(title, page, T("熟練度", "MASTERY"), title.rectTransform.anchoredPosition, title.rectTransform.sizeDelta, title.fontSize);
            // 説明文は元より横に長くするので、左端の位置が元と同じになるよう中心をずらす
            var helpPos = help.rectTransform.anchoredPosition + new Vector2((700 - help.rectTransform.sizeDelta.x) / 2f, 0);
            CloneText(help, page, T("敵を倒すと、手に持っている武器の熟練度が上がる。", "Kills raise the mastery of the weapon in your hands."), helpPos, new Vector2(700, 40), help.fontSize);
            CloneText(help, page, T($"Lv{Mastery.LevelsPerPoint}ごとに強化ポイント+1（最大{Mastery.MaxPoints}）。強化とリセットは部屋でのみ。", $"+1 upgrade point every {Mastery.LevelsPerPoint} levels (max {Mastery.MaxPoints}). Upgrade/reset at home only."), helpPos + new Vector2(0, -33), new Vector2(700, 40), help.fontSize);
            CloneText(help, page, T("同じ武器ならレアリティ違いでも共通。", "Shared by every rarity of the same weapon."), helpPos + new Vector2(0, -66), new Vector2(700, 40), help.fontSize);

            var back = NewButton(page, T("戻る (Esc)", "BACK (Esc)"), new Vector2(760, 494), new Vector2(220, 60), 28);
            back.Button.onClick.AddListener(new Action(() => Safe("backbtn", () => ShowPage(false))));

            // 左：全武器の一覧（インベントリ側のスクロール枠を複製し、中身を作り直す）
            var list = UnityEngine.Object.Instantiate(leftScroll.gameObject, page, false);
            list.name = "FPR_MasteryList";
            list.SetActive(true);
            var lsr = list.GetComponent<ScrollRect>();
            _listContent = lsr.content;
            for (int i = _listContent.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_listContent.GetChild(i).gameObject);
            lsr.verticalNormalizedPosition = 1f;

            var weapons = Tracker.Inv?.WeaponData?.Weapons;
            for (int pass = 0; pass < 2 && weapons != null; pass++)         // メイン武器 → サブ武器の順
                for (int i = 0; i < weapons.Count; i++)
                {
                    var wi = weapons[i];
                    if (wi == null || !Mastery.Eligible(wi.Id)) continue;
                    bool big = wi.Type == WeaponItem.SlotType.Big;
                    if ((pass == 0) != big) continue;
                    var card = UnityEngine.Object.Instantiate(bag.ItemPrefab, _listContent, false).GetComponent<UiItemParams>();
                    card.gameObject.name = "FPR_MasteryCard";
                    card.gameObject.SetActive(true);
                    if (card.MainSprite != null) card.MainSprite.sprite = wi.Image;
                    if (card.texts != null && card.texts.Length > 0) card.texts[0].text = SafeName(wi);
                    int id = wi.Id;
                    card.Button.onClick.AddListener(new Action(() => Safe("select", () => { _selected = id; RefreshList(); RefreshDetails(); })));
                    _cards.Add((id, card));
                }

            BuildDetails(page, title, help, bag);
            _page.SetActive(false);
            Main.Log($"[masteryui] built: {_cards.Count} weapons");
        }

        static void BuildDetails(Transform page, TMP_Text title, TMP_Text help, SaveBag bag)
        {
            // 右：詳細欄（保管庫側の一覧と同じ位置・大きさの半透明パネル）
            var panel = new GameObject("FPR_MasteryDetails").AddComponent<RectTransform>();
            panel.SetParent(page, false);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = new Vector2(453, -70);
            panel.sizeDelta = new Vector2(825, 940);
            var pimg = panel.gameObject.AddComponent<Image>();
            pimg.color = new Color(0f, 0f, 0f, 0.392f);
            var p = panel.transform;

            // 武器画像（カードの画像と同じ部品）
            var iconGo = new GameObject("Icon").AddComponent<RectTransform>();
            Place(iconGo, p, new Vector2(40, 30), new Vector2(330, 165));
            _icon = iconGo.gameObject.AddComponent<Image>();
            _icon.preserveAspect = true;

            _name = TopText(title, p, new Vector2(400, 40), new Vector2(400, 60), 44);
            _level = TopText(title, p, new Vector2(400, 105), new Vector2(400, 60), 40);
            _level.color = Gold;

            // 経験値バー
            var barBg = new GameObject("XpBar").AddComponent<RectTransform>();
            Place(barBg, p, new Vector2(40, 230), new Vector2(745, 14));
            barBg.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);
            var fill = new GameObject("Fill").AddComponent<RectTransform>();
            fill.SetParent(barBg, false);
            fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(0f, 1f);
            fill.offsetMin = fill.offsetMax = Vector2.zero;
            _xpFill = fill.gameObject.AddComponent<Image>();
            _xpFill.color = Gold;
            _xp = TopText(help, p, new Vector2(40, 252), new Vector2(745, 36), 24);

            _points = TopText(title, p, new Vector2(40, 320), new Vector2(745, 50), 34);

            // 強化項目 4 行
            for (int i = 0; i < 4; i++)
            {
                float y = 400 + i * 85;
                var line = new GameObject("Line").AddComponent<RectTransform>();
                Place(line, p, new Vector2(40, y + 78), new Vector2(745, 2));
                line.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);

                _statLabel[i] = TopText(title, p, new Vector2(40, y + 8), new Vector2(330, 50), 32);
                _statValue[i] = TopText(help, p, new Vector2(370, y + 14), new Vector2(260, 50), 28);
                var s = Mastery.Stats[i];
                var plus = NewButton(p, "+", Vector2.zero, new Vector2(110, 60), 34);
                Place(plus.GetComponent<RectTransform>(), p, new Vector2(675, y + 6), new Vector2(110, 60));
                plus.Button.onClick.AddListener(new Action(() => Safe("plus", () =>
                {
                    if (_selected >= 0 && Mastery.AddPoint(_selected, s)) { RefreshList(); RefreshDetails(); }
                })));
                _plus[i] = plus;
            }

            // ホロサイト（Lv10 で解放、ON/OFF）
            {
                float y = 400 + 4 * 85;
                _sightLabel = TopText(title, p, new Vector2(40, y + 8), new Vector2(330, 50), 32);
                _sightValue = TopText(help, p, new Vector2(370, y + 14), new Vector2(300, 50), 28);
                _sight = NewButton(p, "ON", Vector2.zero, new Vector2(110, 60), 28);
                Place(_sight.GetComponent<RectTransform>(), p, new Vector2(675, y + 6), new Vector2(110, 60));
                _sight.Button.onClick.AddListener(new Action(() => Safe("sight", () =>
                {
                    if (_selected >= 0 && Mastery.SetSight(_selected, !Mastery.SightOn(_selected))) { RefreshList(); RefreshDetails(); }
                })));
            }

            _reset = NewButton(p, T("リセット", "RESET"), Vector2.zero, new Vector2(220, 60), 28);
            Place(_reset.GetComponent<RectTransform>(), p, new Vector2(565, 835), new Vector2(220, 60));
            _reset.Button.onClick.AddListener(new Action(() => Safe("reset", () =>
            {
                if (_selected >= 0 && Mastery.Reset(_selected)) { RefreshList(); RefreshDetails(); }
            })));
            _status = TopText(help, p, new Vector2(40, 845), new Vector2(500, 50), 24);
        }

        // ---------- 表示の更新 ----------

        static void RefreshList()
        {
            foreach (var (id, card) in _cards)
            {
                if (card == null) continue;
                var e = Mastery.Get(id);
                int lv = Mastery.Level(id);
                int unused = Mastery.PointsEarned(id) - Mastery.PointsUsed(id);
                if (card.texts != null && card.texts.Length > 1)
                    card.texts[1].text = $"Lv.{lv}{(unused > 0 ? $"  <color=#FFD966>+{unused}</color>" : "")}";
                bool sel = id == _selected;
                if (card.Background != null) card.Background.color = sel ? Gold : (e == null || e.Kills == 0 ? Dim : Color.white);
                if (card.MainSprite != null) card.MainSprite.color = e == null || e.Kills == 0 ? new Color(1f, 1f, 1f, 0.45f) : Color.white;
            }
        }

        static void RefreshDetails()
        {
            if (_selected < 0 || _name == null) return;
            var wi = Tracker.ItemById(_selected);
            var e = Mastery.Get(_selected);
            int kills = e?.Kills ?? 0;
            int lv = Mastery.Level(_selected);
            int earned = Mastery.PointsEarned(_selected), used = Mastery.PointsUsed(_selected);

            if (_icon != null) { _icon.sprite = wi?.Image; _icon.enabled = wi?.Image != null; }
            _name.text = wi != null ? SafeName(wi) : $"#{_selected}";
            _level.text = $"Lv.{lv} <size=60%>/ {Mastery.MaxLevel}</size>";

            float prog = 1f;
            if (lv < Mastery.MaxLevel)
            {
                int a = Mastery.KillsForLevel(lv), b = Mastery.KillsForLevel(lv + 1);
                prog = Mathf.Clamp01((kills - a) / (float)Math.Max(1, b - a));
                _xp.text = T($"累計 {kills} キル　・　次のレベルまで {b - kills} キル", $"{kills} kills total  ·  {b - kills} to next level");
            }
            else _xp.text = T($"累計 {kills} キル　・　最大レベル", $"{kills} kills total  ·  MAX LEVEL");
            if (_xpFill != null) _xpFill.rectTransform.anchorMax = new Vector2(prog, 1f);

            int nextPointLv = (lv / Mastery.LevelsPerPoint + 1) * Mastery.LevelsPerPoint;
            var next = earned >= Mastery.MaxPoints ? "" : T($"　<size=70%>次のポイント: Lv.{nextPointLv}</size>", $"  <size=70%>next point at Lv.{nextPointLv}</size>");
            _points.text = T($"強化ポイント  {used} / {earned}", $"Upgrade points  {used} / {earned}") + next;

            bool canEdit = Mastery.CanEdit;
            for (int i = 0; i < 4; i++)
            {
                var s = Mastery.Stats[i];
                int pts = Mastery.PointsIn(_selected, s);
                _statLabel[i].text = Mastery.StatName(s, _selected);
                _statValue[i].text = (pts > 0 ? $"<color=#FFD966>{Mastery.BonusText(s, pts, _selected)}</color>" : "+0") +
                                     $"  <size=75%><color=#AAAAAA>({T("1ポイント", "per point")} {Mastery.BonusText(s, 1, _selected)})</color></size>";
                SetEnabled(_plus[i], canEdit && used < earned);
            }
            SetEnabled(_reset, canEdit && used > 0);
            _status.text = canEdit ? "" : T("ラン中は強化できません", "Upgrades can only be changed at home");

            // ホロサイト
            _sightLabel.text = Mastery.SightSupported(_selected) ? SightMount.SightName(_selected, UiPatches.Japanese) : T("サイト", "Sight");
            bool sightOn = Mastery.SightOn(_selected);
            if (!Mastery.SightSupported(_selected))
                _sightValue.text = $"<color=#AAAAAA><size=85%>{T("この武器には付けられません", "Not available for this weapon")}</size></color>";
            else if (!Mastery.SightUnlocked(_selected))
                _sightValue.text = $"<color=#AAAAAA><size=85%>{T($"Lv.{Mastery.SightLevel} で解放", $"Unlocks at Lv.{Mastery.SightLevel}")}</size></color>";
            else
                _sightValue.text = sightOn ? $"<color=#FFD966>{T("装着中", "Equipped")}</color>" : T("外している", "Not equipped");
            if (_sight?.texts != null && _sight.texts.Length > 0) _sight.texts[0].text = sightOn ? "OFF" : "ON";
            _sight?.gameObject.SetActive(Mastery.SightSupported(_selected));
            _sightLabel.gameObject.SetActive(Mastery.SightFeature);
            _sightValue.gameObject.SetActive(Mastery.SightFeature);
            SetEnabled(_sight, canEdit && Mastery.SightUnlocked(_selected));
        }

        static void SetEnabled(UiItemParams b, bool on)
        {
            if (b == null) return;
            b.Button.interactable = on;
            // 使えないときは枠と文字を薄くする（カードの CanvasGroup は別の処理が透明度を戻すため色で表す）
            var c = new Color(1f, 1f, 1f, on ? 1f : 0.25f);
            if (b.Background != null) b.Background.color = c;
            if (b.texts != null && b.texts.Length > 0 && b.texts[0] != null) b.texts[0].color = c;
        }

        // ---------- 部品 ----------

        // 武器カードを複製してボタンにする（枠と文字だけ使う）
        static UiItemParams NewButton(Transform parent, string label, Vector2 pos, Vector2 size, float fontSize)
        {
            var go = UnityEngine.Object.Instantiate(_bag.ItemPrefab, parent, false);
            go.name = "FPR_Button";
            go.SetActive(true);
            var b = go.GetComponent<UiItemParams>();
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            if (b.MainSprite != null) b.MainSprite.gameObject.SetActive(false);
            if (b.texts != null)
            {
                for (int i = 1; i < b.texts.Length; i++) b.texts[i].gameObject.SetActive(false);
                if (b.texts.Length > 0)
                {
                    var t = b.texts[0];
                    var trt = t.rectTransform;
                    trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
                    trt.offsetMin = trt.offsetMax = Vector2.zero;
                    t.alignment = TextAlignmentOptions.Center;
                    t.fontSize = fontSize;
                    t.text = label;
                }
            }
            if (b.Background != null) b.Background.color = Color.white;
            return b;
        }

        static TMP_Text CloneText(TMP_Text template, Transform parent, string text, Vector2 pos, Vector2 size, float fontSize)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
            go.SetActive(true);
            // 操作説明の文字はパッド用の表示切り替え部品が付いているので外す（文章を上書きされないように）
            foreach (var c in go.GetComponents<MonoBehaviour>())
                if (c.GetIl2CppType().Name == "PadPromptText") UnityEngine.Object.Destroy(c);
            var t = go.GetComponent<TMP_Text>();
            t.rectTransform.anchoredPosition = pos;
            t.rectTransform.sizeDelta = size;
            t.fontSize = fontSize;
            t.enableWordWrapping = false;
            t.text = text;
            return t;
        }

        // 詳細欄の中に左上基準で文字を置く
        static TMP_Text TopText(TMP_Text template, Transform parent, Vector2 topLeft, Vector2 size, float fontSize)
        {
            var t = CloneText(template, parent, "", Vector2.zero, size, fontSize);
            Place(t.rectTransform, parent, topLeft, size);
            t.alignment = TextAlignmentOptions.TopLeft;
            return t;
        }

        static void Place(RectTransform rt, Transform parent, Vector2 topLeft, Vector2 size)
        {
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
            rt.sizeDelta = size;
        }

        static string SafeName(WeaponItem wi) { try { return wi.GetName(); } catch { return $"#{wi.Id}"; } }

        // ---------- テスト用 ----------

        public static GameObject OpenButtonForTest => _openButton;
        public static void SelectForTest(int id) { _selected = id; RefreshList(); RefreshDetails(); }
        public static void ClickPlusForTest(int stat) => _plus[stat]?.Button.onClick.Invoke();
        public static void ClickResetForTest() => _reset?.Button.onClick.Invoke();
        public static void ClickSightForTest() => _sight?.Button.onClick.Invoke();
        public static string SightTextForTest => $"value='{_sightValue?.text}' button='{_sight?.texts?[0]?.text}' enabled={_sight?.Button.interactable} visible={_sight?.gameObject.activeSelf}";
        public static string DetailsTextForTest =>
            $"name='{_name?.text}' level='{_level?.text}' xp='{_xp?.text}' points='{_points?.text}' " +
            $"stats=[{string.Join(" | ", Array.ConvertAll(_statValue, t => t?.text))}] status='{_status?.text}' " +
            $"plusEnabled={_plus[0]?.Button.interactable} resetEnabled={_reset?.Button.interactable} cards={_cards.Count}";

        // ---------- 共通 ----------

        static readonly HashSet<string> _logged = new();
        static void Safe(string where, Action a) { try { a(); } catch (Exception e) { LogOnce(where, e); } }
        static void LogOnce(string where, Exception e) { if (_logged.Add(where)) Main.Log($"[masteryui:{where}] ERROR {e}"); }
    }
}
