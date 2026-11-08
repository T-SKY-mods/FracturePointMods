using System;
using System.Collections.Generic;
using System.Linq;
using FPModOption;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FPSurvival
{
    // 拠点のエンドレス端末に 2 ページ目（サバイバル）を足す。端末を見ている間に右上の案内をクリック（または → / ←）で切り替え、SPACE で開始。
    // 1 ページ目はゲームのまま。2 ページ目ではランキングを隠し、ベスト記録と簡単なルールを出す
    public static class Tablet
    {
        public static int Page { get; private set; }
        static EndlessModeTablet _tablet;
        static Transform _canvas;
        static TMP_Text _tab, _title, _body, _modeNormal, _modeHard;
        public static bool HardPicked { get; private set; }   // 2 ページ目で選んでいる難易度
        static readonly List<GameObject> _endlessOnly = new();
        static readonly List<(TMP_Text text, string orig)> _buttonTexts = new();
        static float _nextFind;

        public static void OnSceneLoaded()
        {
            Page = 0; _tablet = null; _canvas = null; _tab = null; _title = null; _body = null; _modeNormal = null; _modeHard = null;
            HardPicked = false;
            _endlessOnly.Clear(); _buttonTexts.Clear(); _nextFind = 0f;
        }

        public static void Tick()
        {
            if (Main.Scene != Core.HubScene) return;
            if (_tab == null)
            {
                if (Time.realtimeSinceStartup < _nextFind) return;
                _nextFind = Time.realtimeSinceStartup + 2f;
                try { Build(); } catch (Exception e) { Main.Log("[tablet] build failed: " + e); _nextFind = Time.realtimeSinceStartup + 30f; }
                return;
            }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null || _tablet == null || !_tablet.focused) return;
            if (kb.rightArrowKey.wasPressedThisFrame && Page == 0) SetPage(1);
            else if (kb.leftArrowKey.wasPressedThisFrame && Page == 1) SetPage(0);
            else if (Page == 1 && kb.upArrowKey.wasPressedThisFrame) Pick(false);
            else if (Page == 1 && kb.downArrowKey.wasPressedThisFrame) Pick(true);
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) Click(mouse.position.ReadValue());
        }

        // 右上の案内（「サバイバル ▶」/「◀ エンドレス」）をクリックするとページを切り替える
        public static bool Click(Vector2 screen)
        {
            if (_tab == null) return false;
            if (Page == 1 && Hits(_modeNormal, screen)) { Pick(false); return true; }
            if (Page == 1 && Hits(_modeHard, screen)) { Pick(true); return true; }
            if (!Hits(_tab, screen)) return false;
            SetPage(Page == 0 ? 1 : 0);
            return true;
        }

        static void Pick(bool hard)
        {
            HardPicked = hard;
            SetPage(Page);
        }

        public static void ChooseForTest(bool hard) { HardPicked = hard; SetPage(1); }

        static bool Hits(TMP_Text text, Vector2 screen)
        {
            var cam = Camera.main;
            if (cam == null) return false;
            // 文字の周りを少し広めに当たりにする
            var corners = Corners(text);
            var min = new Vector2(float.MaxValue, float.MaxValue); var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var c in corners)
            {
                var p = cam.WorldToScreenPoint(c);
                if (p.z <= 0f) return false;
                min = Vector2.Min(min, p); max = Vector2.Max(max, p);
            }
            float pad = Mathf.Max(8f, (max.y - min.y) * 0.3f);
            return screen.x >= min.x - pad && screen.x <= max.x + pad && screen.y >= min.y - pad && screen.y <= max.y + pad;
        }

        // 案内の四隅（ワールド座標）。ゲーム側の配列で受け取る（C# の配列を渡すと写しに書かれて値が戻らない）
        static Vector3[] Corners(TMP_Text text)
        {
            var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            text.rectTransform.GetWorldCorners(arr);
            return new[] { arr[0], arr[1], arr[2], arr[3] };
        }

        public static Vector2? TabScreenPointForTest()
        {
            var cam = Camera.main;
            if (_tab == null || cam == null) return null;
            var corners = Corners(_tab);
            var mid = (corners[0] + corners[2]) / 2f;
            var p = cam.WorldToScreenPoint(mid);
            return new Vector2(p.x, p.y);
        }

        static void Build()
        {
            _tablet = Object.FindObjectOfType<EndlessModeTablet>();
            if (_tablet == null) return;
            _canvas = _tablet.transform.Find("Canvas");
            if (_canvas == null) { Main.Log("[tablet] canvas not found"); _nextFind = Time.realtimeSinceStartup + 30f; return; }
            var title = _canvas.Find("Text (TMP)")?.GetComponent<TMP_Text>();
            var warning = _canvas.Find("Text (TMP) (2)")?.GetComponent<TMP_Text>();
            if (title == null || warning == null) { Main.Log("[tablet] texts not found"); _nextFind = Time.realtimeSinceStartup + 30f; return; }
            foreach (var n in new[] { "Text (TMP)", "Text (TMP) (2)", "Text (TMP) (1)", "PERSONAL_UI_Prefab", "list parent" })
            {
                var t = _canvas.Find(n);
                if (t != null) _endlessOnly.Add(t.gameObject);
            }
            var button = _canvas.Find("Button (Panel)");
            if (button != null) foreach (var t in button.GetComponentsInChildren<TMP_Text>(true)) _buttonTexts.Add((t, t.text));

            var canvasRt = _canvas.GetComponent<RectTransform>();
            // ページ切り替えの案内（右上）
            _tab = Clone(title, "FPSurvival tab");
            var trt = _tab.rectTransform;
            trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(1f, 1f);
            trt.sizeDelta = new Vector2(canvasRt.rect.width * 0.5f, title.rectTransform.rect.height);
            trt.anchoredPosition = new Vector2(-canvasRt.rect.width * 0.03f, -canvasRt.rect.height * 0.02f);
            _tab.fontSize = title.fontSize * 0.6f;
            _tab.enableAutoSizing = false;
            _tab.alignment = TextAlignmentOptions.TopRight;
            _tab.gameObject.SetActive(true);

            // 2 ページ目の見出しと本文
            _title = Clone(title, "FPSurvival title");
            _title.text = Main.T("• サバイバルモード", "• SURVIVAL MODE");
            _body = Clone(warning, "FPSurvival body");
            // 見出しの下からボタンの上まで
            var cr = canvasRt.rect;
            var area = new Rect(cr.xMin + cr.width * 0.06f, cr.yMin + cr.height * 0.2f, cr.width * 0.88f, cr.height * 0.6f);
            var brt = _body.rectTransform;
            // 元の注意書きとは縮尺が違うので、キャンバス直下に置き直して見出しと同じ縮尺にする
            brt.SetParent(_canvas, false);
            var k = Math.Max(0.01f, title.rectTransform.localScale.x);
            brt.localScale = Vector3.one;   // 拡大した文字は表示されないので、等倍のまま文字の大きさの方を合わせる
            brt.localRotation = Quaternion.identity;
            brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0.5f, 0.5f);
            brt.localPosition = new Vector3(area.center.x, area.center.y, title.rectTransform.localPosition.z);
            brt.sizeDelta = area.size;
            _body.alignment = TextAlignmentOptions.TopLeft;
            _body.enableAutoSizing = true;
            _body.fontSizeMin = title.fontSize * k * 0.2f;
            _body.fontSizeMax = title.fontSize * k * 0.42f;
            _body.enableWordWrapping = true;
            _title.gameObject.SetActive(false);
            _body.gameObject.SetActive(false);
            // 見出しの横に難易度（↑↓ かクリックで選ぶ）
            _modeNormal = ModeText(title, "FPSurvival normal", canvasRt, 0f);
            _modeHard = ModeText(title, "FPSurvival hard", canvasRt, 0.2f);
            SetPage(0);
            Main.Log($"[tablet] page 2 ready (body area {area}, button texts {_buttonTexts.Count})");
        }

        static TMP_Text ModeText(TMP_Text title, string name, RectTransform canvasRt, float x)
        {
            var t = Clone(title, name);
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(canvasRt.rect.width * 0.18f, canvasRt.rect.height * 0.1f) / Math.Max(0.01f, title.rectTransform.localScale.x);   // 見出しは拡大して置かれている
            rt.anchoredPosition = new Vector2(canvasRt.rect.width * x, canvasRt.rect.height * 0.4f);
            t.fontSize = title.fontSize * 0.5f;
            t.enableAutoSizing = false;
            t.alignment = TextAlignmentOptions.Left;
            t.gameObject.SetActive(false);
            return t;
        }

        static TMP_Text Clone(TMP_Text src, string name)
        {
            var go = Object.Instantiate(src.gameObject, src.transform.parent, false);
            go.name = name;
            for (int i = go.transform.childCount - 1; i >= 0; i--) Object.Destroy(go.transform.GetChild(i).gameObject);   // 文字の部品（サブメッシュ）は作り直される
            return go.GetComponent<TMP_Text>();
        }

        // 部品の範囲をまとめた四角（キャンバスの座標）
        public static void SetPage(int page)
        {
            Page = page;
            if (_tab == null) return;
            bool surv = page == 1;
            foreach (var g in _endlessOnly) if (g != null) g.SetActive(!surv);
            _title.gameObject.SetActive(surv);
            _body.gameObject.SetActive(surv);
            _tab.text = surv ? Main.T("◀ エンドレス", "◀ ENDLESS") : Main.T("サバイバル ▶", "SURVIVAL ▶");
            foreach (var (t, orig) in _buttonTexts)
                if (t != null) t.text = !surv ? orig : HardPicked ? Main.T("無料 - ハードで開始 - SPACE", "FREE - start HARD - PRESS SPACE") : Main.T("無料 - ノーマルで開始 - SPACE", "FREE - start NORMAL - PRESS SPACE");
            ShowMode(_modeNormal, surv, !HardPicked, Main.T("ノーマル", "NORMAL"));
            ShowMode(_modeHard, surv, HardPicked, Main.T("ハード", "HARD"));
            if (surv)
            {
                _title.text = Main.T("• サバイバルモード", "• SURVIVAL MODE");   // 言語を切り替えたときのため
                _body.text = BodyText();
                foreach (var t in new[] { _title, _body, _tab }) t.ForceMeshUpdate(true, true);
            }
            Main.Log($"[tablet] page {page}");
        }

        static void ShowMode(TMP_Text t, bool show, bool picked, string name)
        {
            if (t == null) return;
            t.gameObject.SetActive(show);
            if (!show) return;
            t.text = (picked ? "▶ " : "   ") + name;
            t.color = picked ? Color.white : new Color(0.5f, 0.5f, 0.5f);
            t.ForceMeshUpdate(true, true);
        }

        static List<string> BestLines(Stash.BestRecord b)
        {
            var lines = new List<string>();
            if (b == null) lines.Add(Main.T("ベスト記録：まだありません", "Best: none yet"));
            else
            {
                string result = b.Result == "escaped" ? Main.T("脱出", "escaped") : Main.T("倒れた", "went down");
                lines.Add(Main.T($"ベスト記録：ウェーブ {b.Waves}（{result}・{b.Date}）", $"Best: wave {b.Waves} ({result}, {b.Date})"));
                lines.Add(Main.T($"  大型武器：{Survival.Show(b.Main)}　小型武器：{Survival.Show(b.Sub)}", $"  Big weapon: {Survival.Show(b.Main)}   Small weapon: {Survival.Show(b.Sub)}"));
                lines.Add(Main.T($"  脱出時のドル：${b.Dollars:N0}", $"  Dollars at escape: ${b.Dollars:N0}"));
            }
            return lines;
        }

        static string BodyText()
        {
            var lines = BestLines(HardPicked ? Stash.BestHard : Stash.Best);
            var err = Survival.CheckLoadout(Survival.Held(Refs.Inv));
            if (err != null) lines.Add("<color=#FF6060>" + err + "</color>");
            lines.Add("");
            lines.Add(Main.T("ルール", "Rules"));
            lines.AddRange(Main.Ja
                ? new[]
                {
                    "・持ち込めるのは大型武器と小型武器を 1 つずつ、装備中の防具、グレネード、暗視ゴーグルです",
                    $"・開始時に、武器ごとにマガジン {Survival.StartMags} 本と弾薬 {Survival.StartAmmo} 発が支給されます",
                    "・舞台は屋上です。敵を倒すとドルが手に入り、死体を調べると弾薬が見つかります",
                    $"・回復はウェーブの合間に自動販売機で。${Arena.FirstHealPrice:N0} から、買うたびに ${Arena.HealPriceStep:N0} ずつ上がります",
                    "・5 ウェーブごとに、レアの回復アイテムが届きます",
                    "・剛熊の像・時の像・戦士の花瓶・招き猫の像の効果と、難易度の設定はサバイバルでは働きません",
                    "・ウェーブの合間にエレベーター前の扉を調べると終了。武器箱で大型・小型武器を 1 つずつ受け取り、エレベーターで拠点へ戻ります",
                    "・持ち込む装備を絞るほど、武器箱から良い報酬が出やすくなります",
                    "・ハードはウェーブ 40 の敵から始まり、武器箱の報酬も早く良くなります",
                    "・ダウンしても持ち込んだ物は失いませんが、稼いだドルは持ち帰れません",
                    "・持ち込まなかったドル・ルーブル・弾薬・回復アイテム・戦利品は預かり、拠点に戻ると返却します",
                }
                : new[]
                {
                    "- You may bring one big weapon and one small weapon, your worn armor, grenades and night vision goggles",
                    $"- Each weapon starts with {Survival.StartMags} magazines and {Survival.StartAmmo} rounds of ammo",
                    "- The fight takes place on the rooftop. Kills earn dollars, and searching bodies finds ammo",
                    $"- Heal at the Vending Machine between waves. Drinks start at ${Arena.FirstHealPrice:N0} and cost ${Arena.HealPriceStep:N0} more each time",
                    "- Every 5 waves you receive a rare health item",
                    "- The Mighty Bear Statue, Statue of Time, The Warrior's Vase, Lucky Cat Statue and the difficulty setting have no effect in survival",
                    "- Between waves, use the door to the elevator room to end the run. Take one big and one small weapon from the Weapon Crate, then return to the hub by elevator",
                    "- The lighter your loadout, the better the Weapon Crate rewards tend to be",
                    "- Hard starts with the enemies of wave 40, and Weapon Crate rewards improve sooner",
                    "- If you go down, you keep what you brought, but the dollars you earned are lost",
                    "- The dollars, roubles, ammo, health items and loot you did not bring are kept safe and returned at the hub",
                });
            return string.Join("\n", lines);
        }

        // テスト用
        public static string DebugForTest() => _body == null ? "no body" :
            $"body active={_body.gameObject.activeInHierarchy} len={_body.text?.Length} overflow={_body.isTextOverflowing} preferredH={_body.preferredHeight:0} parsed={_body.GetParsedText()?.Length} size={_body.fontSize} color={_body.color} alpha={_body.alpha} rect={_body.rectTransform.rect} local={_body.rectTransform.localPosition} scale={_body.rectTransform.localScale} enabled={_body.enabled} canvasRect={_canvas.GetComponent<RectTransform>().rect} titleLocal={_title.rectTransform.localPosition}";
        public static EndlessModeTablet TabletForTest => _tablet;
        public static bool ReadyForTest => _tab != null;
        public static string BodyForTest => _body?.text ?? "";
    }
}
