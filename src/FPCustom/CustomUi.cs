using System;
using System.Collections.Generic;
using System.Linq;
using FPModOption;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FPCustom
{
    // 保管庫に「カスタマイズ」ページを足す。作りは熟練度ページと同じ（保管庫画面の部品を複製）。
    //   右上のタブ [熟練度] [カスタマイズ] … 保管庫・熟練度・カスタマイズのどの画面でも同じ位置。今の画面のタブは金色
    //   熟練度・カスタマイズの画面では、タブの左に [戻る (Esc)]（Esc でも保管庫へ）
    //   カスタマイズ … 左：武器の一覧  右：横から見た武器（今の装着）、カテゴリごとに ◀ 部品名 ▶ [解放 / 装着 / 外す]、効果（プラス・マイナス）
    public static class CustomUi
    {
        public static void Install(HarmonyLib.Harmony h)
        {
            SafePatcher.Patch(h, typeof(SaveBag), "ClosePage", null, nameof(ClosePage_Pre), null, typeof(CustomUi));
        }

        static readonly Color Gold = new(1f, 0.85f, 0.4f, 1f);
        static readonly Color Dim = new(1f, 1f, 1f, 0.35f);
        static readonly Color PageBg = new(0.094f, 0.094f, 0.094f, 1f);
        static bool Ja => Core.Ja;
        static string T(string ja, string en) => Ja ? ja : en;

        static SaveBag _bag;
        static GameObject _openButton, _page;
        // タブの位置（保管庫・熟練度・カスタマイズで共通）と [戻る] の位置
        //   保管庫：[熟練度][カスタマイズ]（右上の端まで）。熟練度・カスタマイズ：[熟練度][カスタマイズ][戻る]（戻るが一番右）
        static readonly Vector2 StashMastery = new(500, 494), StashCustom = new(760, 494);
        static readonly Vector2 TabMastery = new(280, 494), TabCustom = new(520, 494), BackPos = new(760, 494), TabSize = new(230, 60);
        static Transform _listContent;
        static readonly List<(int id, UiItemParams card)> _cards = new();
        static int _selected = -1;
        public static bool IsOpen => _page != null && _page.activeSelf;

        // 保管庫の武器の性能のポップアップ（FPRarity）は、カスタマイズの画面では出さない
        public static void HookTooltip() => Link.CustomPageOpen = () => IsOpen;

        static RawImage _view;
        static TMP_Text _name, _parts, _status, _effHead, _effPlus, _effMinus, _eqHead, _eqPlus, _eqMinus;
        const int Rows = 5;
        static readonly TMP_Text[] _cat = new TMP_Text[Rows], _option = new TMP_Text[Rows];
        static readonly UiItemParams[] _prev = new UiItemParams[Rows], _next = new UiItemParams[Rows], _act = new UiItemParams[Rows];
        static readonly int[] _browse = new int[Rows];   // 各行で今見ている選択肢（0 = なし）
        static int _lastRow = -1;                         // 最後に ◀ ▶ を押した行（効果の欄に出す部品）
        static UiItemParams _toMastery, _masteryCustomTab;

        public static void OnSceneChanged() { _bag = null; _openButton = null; _page = null; _listContent = null; _cards.Clear(); _selected = -1; _built.Clear(); }

        // 作ったもの（言語を変えたら消して作り直す）と、作ったときの言語
        static readonly List<GameObject> _built = new();
        static bool _builtJa;

        // 熟練度ページ（レアリティmod が作る）ができたら、そこにボタンを足す。レアリティmod が無ければ保管庫に直接
        public static void Tick()
        {
            if (Core.Scene != Core.HubScene) return;
            var bag = Refs.Bag;
            if (bag == null || !bag._stashOpened) { if (IsOpen) _page.SetActive(false); return; }
            if (Refs.Alive(_openButton) && _bag != null && _bag.Pointer == bag.Pointer)
            {
                if (_builtJa == Ja || IsOpen) return;
                foreach (var go in _built) if (Refs.Alive(go)) UnityEngine.Object.Destroy(go);
                _built.Clear(); _openButton = null; _page = null; _cards.Clear();
                Main.Log($"[ui] language changed: rebuild ({(Ja ? "ja" : "en")})");
            }
            var canvas = bag.canvas != null ? bag.canvas.transform : null;
            var mastery = canvas?.Find("FPR_MasteryPage");
            if (canvas == null) return;
            if (Link.MasteryActive && (mastery == null || canvas.Find("FPR_MasteryButton") == null)) return;
            if (!Link.MasteryActive) mastery = null;
            Safe("build", () => Build(bag, canvas, mastery));
        }

        static bool ClosePage_Pre()
        {
            try { if (!IsOpen) return true; Show(false); return false; }
            catch (Exception e) { LogOnce("close", e); return true; }
        }

        static void Show(bool show, bool toMastery = false)
        {
            if (_page == null) return;
            if (show)
            {
                Link.ShowMastery?.Invoke(false);
                if (_selected < 0 && _cards.Count > 0) _selected = FirstSelection();
                ResetBrowse();
                RefreshList(); RefreshDetails(); RefreshPreview();
            }
            _page.SetActive(show);
            if (!show && toMastery) Link.ShowMastery?.Invoke(true);
            Main.Log($"[ui] page {(show ? "open" : "closed")}");
        }

        static int FirstSelection()
        {
            int held = Weapons.IdOf(Weapons.Active());
            if (held >= 0 && _cards.Any(c => c.id == held)) return held;
            return _cards[0].id;
        }

        // ---------- 組み立て ----------

        static void Build(SaveBag bag, Transform canvas, Transform mastery)
        {
            _bag = bag;
            _cards.Clear();
            TMP_Text title = null, help = null;
            ScrollRect leftScroll = null;
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
            }
            if (title == null || help == null || leftScroll == null) { Main.Log($"[ui] template missing title={title != null} help={help != null} list={leftScroll != null}"); return; }

            // 保管庫：[熟練度]（レアリティmod のボタン）を左へずらして、右に [カスタマイズ]
            var stashMastery = mastery != null ? canvas.Find("FPR_MasteryButton") : null;
            if (stashMastery != null) Move(stashMastery, StashMastery, TabSize);
            var ob = Tab(canvas, T("カスタマイズ", "CUSTOMIZE"), StashCustom, false, () => Show(true));
            _openButton = ob.gameObject;
            _builtJa = Ja;
            _built.Add(_openButton);
            _openButton.name = "FPC_CustomButton";
            // 保管庫のタブは、ページ（熟練度・カスタマイズ）より下に描く（ページを開いたら隠れる）
            if (stashMastery != null) _openButton.transform.SetSiblingIndex(stashMastery.GetSiblingIndex() + 1);
            // 熟練度ページ：[戻る]（レアリティmod のボタン、一番右のまま）の左にタブ [熟練度]（今の画面）[カスタマイズ]
            if (mastery != null)
            {
                _built.Add(Tab(mastery, T("熟練度", "MASTERY"), TabMastery, true, null).gameObject);
                _masteryCustomTab = Tab(mastery, T("カスタマイズ", "CUSTOMIZE"), TabCustom, false, () => Show(true));
                _built.Add(_masteryCustomTab.gameObject);
            }

            _page = new GameObject("FPC_CustomPage");
            _built.Add(_page);
            var prt = _page.AddComponent<RectTransform>();
            prt.SetParent(canvas, false);
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
            prt.offsetMin = prt.offsetMax = Vector2.zero;
            _page.AddComponent<Image>().color = PageBg;
            if (mastery != null) _page.transform.SetSiblingIndex(mastery.GetSiblingIndex() + 1);
            else _page.transform.SetAsLastSibling();
            var page = _page.transform;

            CloneText(title, page, T("カスタマイズ", "CUSTOMIZE"), title.rectTransform.anchoredPosition, title.rectTransform.sizeDelta, title.fontSize);
            var helpPos = help.rectTransform.anchoredPosition + new Vector2((700 - help.rectTransform.sizeDelta.x) / 2f, 0);
            CloneText(help, page, T($"{Parts.Name(true)}{Store.UnlockCost}個でアタッチメントを1つ解放。", $"Unlock an attachment with {Store.UnlockCost} {Parts.Name(false)}."), helpPos, new Vector2(700, 40), help.fontSize);
            CloneText(help, page, T("付け外しは自由。変更は部屋でのみ。", "Attach and remove freely. Changes at home only."), helpPos + new Vector2(0, -33), new Vector2(700, 40), help.fontSize);
            if (Link.RarityActive) CloneText(help, page, T("同じ武器ならレアリティ違いでも共通。", "Shared by every rarity of the same weapon."), helpPos + new Vector2(0, -66), new Vector2(700, 40), help.fontSize);

            var back = NewButton(page, T("戻る (Esc)", "BACK (Esc)"), BackPos, new Vector2(220, 60), 28);
            back.Button.onClick.AddListener(new Action(() => Safe("back", () => Show(false))));
            if (mastery != null)
            {
                _toMastery = Tab(page, T("熟練度", "MASTERY"), TabMastery, false, () => Show(false, toMastery: true));
                Tab(page, T("カスタマイズ", "CUSTOMIZE"), TabCustom, true, null);
            }

            var list = UnityEngine.Object.Instantiate(leftScroll.gameObject, page, false);
            list.name = "FPC_CustomList";
            list.SetActive(true);
            var lsr = list.GetComponent<ScrollRect>();
            _listContent = lsr.content;
            for (int i = _listContent.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_listContent.GetChild(i).gameObject);
            lsr.verticalNormalizedPosition = 1f;

            var weapons = Weapons.Inv?.WeaponData?.Weapons;
            for (int pass = 0; pass < 2 && weapons != null; pass++)
                for (int i = 0; i < weapons.Count; i++)
                {
                    var wi = weapons[i];
                    if (!Catalog.Eligible(wi) || (pass == 0) != (wi.Type == WeaponItem.SlotType.Big)) continue;
                    var card = UnityEngine.Object.Instantiate(bag.ItemPrefab, _listContent, false).GetComponent<UiItemParams>();
                    card.gameObject.name = "FPC_CustomCard";
                    card.gameObject.SetActive(true);
                    if (card.MainSprite != null) card.MainSprite.sprite = wi.Image;
                    if (card.texts != null && card.texts.Length > 0) card.texts[0].text = SafeName(wi);
                    int id = wi.Id;
                    card.Button.onClick.AddListener(new Action(() => Safe("select", () => { _selected = id; ResetBrowse(); RefreshList(); RefreshDetails(); RefreshPreview(); })));
                    _cards.Add((id, card));
                }

            BuildDetails(page, title, help);
            _page.SetActive(false);
            Main.Log($"[ui] built: {_cards.Count} weapons");
        }

        static void BuildDetails(Transform page, TMP_Text title, TMP_Text help)
        {
            var panel = new GameObject("FPC_CustomDetails").AddComponent<RectTransform>();
            panel.SetParent(page, false);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = new Vector2(453, -70);
            panel.sizeDelta = new Vector2(825, 940);
            panel.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.392f);
            var p = panel.transform;

            _name = TopText(title, p, new Vector2(40, 18), new Vector2(480, 50), 42);
            _status = TopText(help, p, new Vector2(40, 66), new Vector2(480, 26), 20);
            _parts = TopText(help, p, new Vector2(440, 22), new Vector2(345, 64), 24);
            _parts.alignment = TextAlignmentOptions.TopRight;

            // 横から見た武器（今の装着）
            var view = new GameObject("FPC_CustomView").AddComponent<RectTransform>();
            Place(view, p, new Vector2(40, 96), new Vector2(Preview.W / 2, Preview.H / 2));   // 描いた画像と同じ縦横比
            _view = view.gameObject.AddComponent<RawImage>();
            _view.texture = Preview.Texture;

            // 部品の行：カテゴリ ◀ 部品名 ▶ [解放 / 装着 / 外す]（行を詰めて、下の効果の欄を広く取る）
            for (int i = 0; i < Rows; i++)
            {
                float y = 266 + i * 54;
                var line = new GameObject("Line").AddComponent<RectTransform>();
                Place(line, p, new Vector2(40, y + 51), new Vector2(745, 2));
                line.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);
                _cat[i] = TopText(title, p, new Vector2(40, y + 7), new Vector2(150, 40), 26);
                int row = i;
                _prev[i] = NewButton(p, "◀", Vector2.zero, new Vector2(56, 46), 24);
                Place(_prev[i].GetComponent<RectTransform>(), p, new Vector2(195, y + 2), new Vector2(56, 46));
                _prev[i].Button.onClick.AddListener(new Action(() => Safe("prev", () => Browse(row, -1))));
                _option[i] = TopText(title, p, new Vector2(254, y + 9), new Vector2(292, 40), 24);
                _option[i].alignment = TextAlignmentOptions.Top;
                _option[i].enableAutoSizing = true; _option[i].fontSizeMin = 18; _option[i].fontSizeMax = 24;
                _next[i] = NewButton(p, "▶", Vector2.zero, new Vector2(56, 46), 24);
                Place(_next[i].GetComponent<RectTransform>(), p, new Vector2(549, y + 2), new Vector2(56, 46));
                _next[i].Button.onClick.AddListener(new Action(() => Safe("next", () => Browse(row, +1))));
                _act[i] = NewButton(p, "", Vector2.zero, new Vector2(175, 46), 22);
                Place(_act[i].GetComponent<RectTransform>(), p, new Vector2(610, y + 2), new Vector2(175, 46));
                _act[i].Button.onClick.AddListener(new Action(() => Safe("act", () => Act(row))));
            }

            // 効果は 2 段：上＝選んでいるアタッチメント、下＝付けているアタッチメントすべて（数値は合計、数値にならない効果はすべて）。
            // どちらも左＝プラスの効果、右＝マイナスの効果。効果が多いときだけ少し小さく（小さくても読める 18 まで）
            _effHead = TopText(help, p, new Vector2(40, 544), new Vector2(745, 32), 24);
            _effPlus = TopText(help, p, new Vector2(40, 578), new Vector2(360, 120), 22);
            _effMinus = TopText(help, p, new Vector2(425, 578), new Vector2(360, 120), 22);
            _eqHead = TopText(help, p, new Vector2(40, 710), new Vector2(745, 30), 22);
            _eqPlus = TopText(help, p, new Vector2(40, 744), new Vector2(360, 186), 22);
            _eqMinus = TopText(help, p, new Vector2(425, 744), new Vector2(360, 186), 22);
            // 日本語の字体は行の間が広いので詰める
            foreach (var t in new[] { _effPlus, _effMinus, _eqPlus, _eqMinus }) { t.enableAutoSizing = true; t.fontSizeMin = 18; t.fontSizeMax = 22; t.lineSpacing = -22f; }
            foreach (var t in new[] { _effHead, _eqHead }) { t.enableAutoSizing = true; t.fontSizeMin = 18; t.fontSizeMax = t.fontSize; }
            foreach (var (y, h) in new[] { (580f, 116f), (746f, 182f) })
            {
                var sep = new GameObject("Sep").AddComponent<RectTransform>();
                Place(sep, p, new Vector2(410, y), new Vector2(2, h));
                sep.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);
            }
            var eqSep = new GameObject("SepEquipped").AddComponent<RectTransform>();
            Place(eqSep, p, new Vector2(40, 704), new Vector2(745, 1));
            eqSep.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);
        }

        // ---------- 操作 ----------

        // 行の選択肢：0 = なし、1.. = 付けられる部品
        static List<Att> Options(int row)
        {
            var cats = Catalog.CatsFor(_selected);
            var l = new List<Att>();
            if (row < cats.Length && Catalog.CanRemove(_selected, cats[row])) l.Add(null);   // アイアンサイトの無い武器はサイトを外せない
            if (row < cats.Length) l.AddRange(Catalog.For(_selected, cats[row]));
            if (l.Count == 0) l.Add(null);
            return l;
        }

        static void ResetBrowse()
        {
            _lastRow = -1;
            if (_selected < 0) return;
            var cats = Catalog.CatsFor(_selected);
            for (int i = 0; i < Rows; i++)
            {
                var opts = Options(i);
                var eq = i < cats.Length ? Store.Equipped(_selected, cats[i]) : null;
                _browse[i] = Math.Max(0, opts.IndexOf(eq));
                if (_lastRow < 0 && eq != null) _lastRow = i;
            }
            if (_lastRow < 0) _lastRow = 0;
        }

        static void Browse(int row, int d)
        {
            var n = Options(row).Count;
            _browse[row] = ((_browse[row] + d) % n + n) % n;
            _lastRow = row;
            RefreshDetails();
        }

        static void Act(int row)
        {
            if (_selected < 0) return;
            _lastRow = row;
            var cat = Catalog.CatsFor(_selected)[row];
            var a = Options(row)[_browse[row]];
            var eq = Store.Equipped(_selected, cat);
            bool ok;
            if (a == null) ok = Store.Equip(_selected, cat, null);                 // 外す
            else if (!Store.Unlocked(_selected, a)) ok = Store.Unlock(_selected, a) && Store.Equip(_selected, cat, a);   // 解放してそのまま付ける
            else if (eq != a) ok = Store.Equip(_selected, cat, a);
            else if (Catalog.CanRemove(_selected, cat)) ok = Store.Equip(_selected, cat, null);   // 付けているものをもう一度押すと外す
            else ok = false;
            if (ok) Changed?.Invoke(_selected);
            RefreshList(); RefreshDetails();
            if (ok) RefreshPreview();
        }

        // 付け外ししたとき（見た目を付け直す側が受け取る）
        public static event Action<int> Changed;

        // ---------- 表示 ----------

        static void RefreshList()
        {
            foreach (var (id, card) in _cards)
            {
                if (card == null) continue;
                int u = Store.UnlockedCount(id), t = Store.TotalCount(id);
                if (card.texts != null && card.texts.Length > 1) card.texts[1].text = $"{u}/{t}";
                bool sel = id == _selected;
                if (card.Background != null) card.Background.color = sel ? Gold : (u == 0 ? Dim : Color.white);
                if (card.MainSprite != null) card.MainSprite.color = u == 0 ? new Color(1f, 1f, 1f, 0.45f) : Color.white;
            }
        }

        static void RefreshDetails()
        {
            if (_selected < 0 || _name == null) return;
            var wi = Tracker.ItemById(_selected);
            _name.text = wi != null ? SafeName(wi) : $"#{_selected}";
            int have = Parts.Count();
            _parts.text = $"{Parts.Name(Ja)}  <color=#FFD966>{have}</color>\n<size=85%><color=#AAAAAA>{T($"解放に {Store.UnlockCost} 個", $"{Store.UnlockCost} to unlock")}</color></size>";

            bool canEdit = Store.CanEdit;
            var cats = Catalog.CatsFor(_selected);
            Att browsed = null;
            for (int i = 0; i < Rows; i++)
            {
                var cat = cats[i];
                var opts = Options(i);
                var a = opts[_browse[i]];
                var eq = Store.Equipped(_selected, cat);
                _cat[i].text = Catalog.CatName(cat, Ja);
                // 付けている部品は金色、まだ解放していない部品は灰色
                string name = a == null ? T("なし", "None") : a.Name(Ja);
                _option[i].text = a != null && eq == a ? $"<color=#FFD966>{name}</color>" : a != null && !Store.Unlocked(_selected, a) ? $"<color=#AAAAAA>{name}</color>" : name;
                string label; bool on;
                if (a == null) { label = T("外す", "REMOVE"); on = eq != null; }
                else if (!Store.Unlocked(_selected, a)) { label = T($"解放 ({Store.UnlockCost})", $"UNLOCK ({Store.UnlockCost})"); on = have >= Store.UnlockCost; }
                else if (eq == a) { bool rm = Catalog.CanRemove(_selected, cat); label = rm ? T("外す", "REMOVE") : T("装着中", "EQUIPPED"); on = rm; }
                else { label = T("装着", "ATTACH"); on = true; }
                if (_act[i].texts != null && _act[i].texts.Length > 0) _act[i].texts[0].text = label;
                SetEnabled(_act[i], canEdit && on);
                SetEnabled(_prev[i], opts.Count > 1);
                SetEnabled(_next[i], opts.Count > 1);
                if (i == _lastRow) browsed = a;
            }
            // 上：選んでいるアタッチメントのプラス（左）・マイナス（右）を 1 行に 1 つ
            var eqSel = browsed != null && Store.Equipped(_selected, browsed.Cat) == browsed;
            _effHead.text = browsed != null
                ? $"<color=#FFD966>{browsed.Name(Ja)}</color>" + (eqSel ? $"  <size=80%><color=#AAAAAA>{T("装着中", "Equipped")}</color></size>" : "")
                : $"<color=#AAAAAA>{T("アタッチメントなし", "No attachment")}</color>";
            var (plus, minus) = Split(new[] { browsed });
            _effPlus.text = Column(T("プラス効果", "Pros"), plus);
            _effMinus.text = Column(T("マイナス効果", "Cons"), minus);
            // 下：付けているアタッチメントすべての効果（同じ能力の数値はまとめる）
            var equipped = cats.Select(c => Store.Equipped(_selected, c)).Where(e => e != null).ToList();
            _eqHead.text = $"<color=#AAAAAA>{T("装着中", "Equipped")} ({equipped.Count})</color>  "
                + (equipped.Count == 0 ? $"<color=#AAAAAA>{T("なし", "None")}</color>" : string.Join(" <color=#AAAAAA>/</color> ", equipped.Select(e => e.Name(Ja))));
            var (ePlus, eMinus) = Split(equipped);
            _eqPlus.text = ePlus.Count > 0 ? string.Join("\n", ePlus) : $"<color=#AAAAAA>{T("プラス効果なし", "No pros")}</color>";
            _eqMinus.text = eMinus.Count > 0 ? string.Join("\n", eMinus) : $"<color=#AAAAAA>{T("マイナス効果なし", "No cons")}</color>";
            _status.text = canEdit ? "" : T("ラン中は変更できません", "Can only be changed at home");
        }

        // アタッチメントの効果をプラスとマイナスに分ける（数値の効果は能力ごとに合計してから、そのあとに数値にならない効果）
        static (List<string> Plus, List<string> Minus) Split(IEnumerable<Att> atts)
        {
            var plus = new List<string>(); var minus = new List<string>();
            var list = atts.Where(a => a != null).ToList();
            var stats = new List<(WeaponStat Stat, float Value)>();
            foreach (var a in list) stats.AddRange(Effects.Stats(a, _selected));
            foreach (var g in stats.GroupBy(e => e.Stat).OrderBy(g => g.Key))
            {
                float v = g.Sum(e => e.Value);
                if (Mathf.Abs(v) < 0.001f) continue;
                (v > 0 ? plus : minus).Add(StatLine(g.Key, v));
            }
            foreach (var a in list)
                foreach (var nt in Effects.Notes(a, _selected)) (nt.Plus ? plus : minus).Add($"<color={(nt.Plus ? Plus : Minus)}>{(Ja ? nt.Ja : nt.En)}</color>");
            return (plus, minus);
        }

        static string Column(string title, List<string> lines) =>
            $"<size=85%><color=#AAAAAA>{title}</color></size>\n" + (lines.Count > 0 ? string.Join("\n", lines) : $"<color=#AAAAAA>{T("なし", "None")}</color>");

        // 性能の名前（レアリティmod の熟練度と同じ。ハンドガンの「連射」の枠はリロード速度）
        static string StatName(WeaponStat s, int weaponId) => s == WeaponStat.FireRate && Weapons.IsHandgun(weaponId)
            ? T("リロード", "Reload speed")
            : Ja ? s switch { WeaponStat.Damage => "ダメージ", WeaponStat.FireRate => "連射", WeaponStat.Control => "コントロール", _ => "エルゴ" }
                 : s switch { WeaponStat.Damage => "Damage", WeaponStat.FireRate => "Fire rate", WeaponStat.Control => "Control", _ => "Ergonomics" };

        static string StatLine(WeaponStat stat, float v)
        {
            bool pct = stat == WeaponStat.FireRate;   // ダメージは固定値（リニアコンペンセイター +15）
            return $"<color={(v > 0 ? Plus : Minus)}>{StatName(stat, _selected)} {(v > 0 ? "+" : "-")}{Mathf.Abs(v):0.#}{(pct ? "%" : "")}</color>";
        }

        const string Plus = "#7CDC7C", Minus = "#FF6B6B";

        static void RefreshPreview()
        {
            if (_selected < 0 || _view == null) return;
            Preview.Render(_selected);
            _view.texture = Preview.Texture;
        }

        static void SetEnabled(UiItemParams b, bool on)
        {
            if (b == null) return;
            b.Button.interactable = on;
            var c = new Color(1f, 1f, 1f, on ? 1f : 0.25f);
            if (b.Background != null) b.Background.color = c;
            if (b.texts != null && b.texts.Length > 0 && b.texts[0] != null) b.texts[0].color = c;
        }

        // ---------- 部品（熟練度ページと同じ作り） ----------

        static UiItemParams NewButton(Transform parent, string label, Vector2 pos, Vector2 size, float fontSize)
        {
            var go = UnityEngine.Object.Instantiate(_bag.ItemPrefab, parent, false);
            go.name = "FPC_Button";
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

        // タブ：今の画面のタブは金色で押せない
        static UiItemParams Tab(Transform parent, string label, Vector2 pos, bool current, Action onClick)
        {
            var b = NewButton(parent, label, pos, TabSize, 28);
            b.gameObject.name = "FPC_Tab";
            if (current)
            {
                if (b.Background != null) b.Background.color = Gold;
                if (b.texts != null && b.texts.Length > 0) b.texts[0].color = Gold;
                b.Button.transition = Selectable.Transition.None;   // 押しても何もしない（無効にすると色が暗くなるので有効のまま）
            }
            else if (onClick != null) b.Button.onClick.AddListener(new Action(() => Safe("tab", onClick)));
            return b;
        }

        static void Move(Transform t, Vector2 pos, Vector2 size)
        {
            var rt = t?.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        static TMP_Text CloneText(TMP_Text template, Transform parent, string text, Vector2 pos, Vector2 size, float fontSize)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
            go.SetActive(true);
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
        public static void SelectForTest(int id) { _selected = id; ResetBrowse(); RefreshList(); RefreshDetails(); RefreshPreview(); }
        public static void ClickMasteryTabForTest() => _toMastery?.Button.onClick.Invoke();
        public static void ClickCustomTabOnMasteryForTest() => _masteryCustomTab?.Button.onClick.Invoke();
        public static string EffectsForTest => $"head='{_effHead?.GetParsedText()}' plus='{_effPlus?.GetParsedText()}' minus='{_effMinus?.GetParsedText()}' sum='{_eqHead?.GetParsedText()}' eqplus='{_eqPlus?.GetParsedText()}' eqminus='{_eqMinus?.GetParsedText()}'";
        public static void BrowseForTest(int row, int d) => Browse(row, d);
        public static void BrowseToForTest(int row, string attId) { var o = Options(row); int i = o.FindIndex(a => a?.Id == attId); if (i >= 0) { _browse[row] = i; _lastRow = row; RefreshDetails(); } else Main.Log($"[ui] test browse: {attId} not in row {row} ({string.Join(",", o.Select(a => a?.Id ?? "none"))})"); }
        public static void ClickActForTest(int row) => _act[row]?.Button.onClick.Invoke();
        public static void ClickToMasteryForTest() => Show(false, toMastery: true);
        public static string RowForTest(int row) => $"cat='{_cat[row]?.text}' option='{_option[row]?.text}' act='{_act[row]?.texts?[0]?.text}' enabled={_act[row]?.Button.interactable}";
        public static string PartsTextForTest => _parts?.text;
        public static IEnumerable<int> CardIdsForTest => _cards.Select(c => c.id).ToList();
        public static bool BuiltJapaneseForTest => _builtJa;
        public static void OpenForTest() => Show(true);

        // 効果のいちばん多い組み合わせ（行ごとに、数値と数値にならない効果の数が最大の部品）を付ける。戻り値：効果が最も多い行
        public static int MaxOutForTest(int id)
        {
            _selected = id;
            int bestRow = 0, bestAll = -1;
            for (int row = 0; row < Rows; row++)
            {
                Att best = null; int n = -1;
                foreach (var a in Options(row))
                {
                    if (a == null) continue;
                    int c = Effects.Stats(a, id).Count() + Effects.Notes(a, id).Count();
                    if (c > n) { n = c; best = a; }
                }
                if (best == null) continue;
                Store.SetForTest(id, best.Cat, best.Id);
                if (n > bestAll) { bestAll = n; bestRow = row; }
            }
            return bestRow;
        }

        // 見切れている文字・小さすぎる文字（ページの中の文字すべて）。空なら問題なし
        public static string LayoutProblemsForTest(float minSize)
        {
            if (_page == null) return "no page";
            var bad = new List<string>();
            foreach (var t in (_name != null ? _name.transform.parent : _page.transform).GetComponentsInChildren<TMP_Text>(false))
            {
                if (string.IsNullOrEmpty(t.text)) continue;
                t.ForceMeshUpdate();
                string what = t.GetParsedText().Replace("\n", " / ");
                if (what.Length > 40) what = what.Substring(0, 40);
                if (t.fontSize >= 40f) continue;   // 画面と武器の名前（1 行で収まる大きさ）
                if (t.enableAutoSizing && t.isTextOverflowing) bad.Add($"overflow '{what}' size={t.fontSize:0.#}");
                else if (t.fontSize < minSize - 0.01f) bad.Add($"small '{what}' size={t.fontSize:0.#}");
                else if (t.textInfo != null && t.textInfo.lineCount > 0 && t.rectTransform.rect.height > 0f && t.preferredHeight > t.rectTransform.rect.height + 2f) bad.Add($"tall '{what}' {t.preferredHeight:0}>{t.rectTransform.rect.height:0}");
            }
            return string.Join("; ", bad);
        }
        public static int CardCount => _cards.Count;

        static readonly HashSet<string> _logged = new();
        static void Safe(string where, Action a) { try { a(); } catch (Exception e) { LogOnce(where, e); } }
        static void LogOnce(string where, Exception e) { if (_logged.Add(where)) Main.Log($"[ui:{where}] ERROR {e}"); }
    }
}
