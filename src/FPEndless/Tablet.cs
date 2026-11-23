using System;
using System.Collections.Generic;
using FPModOption;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FPEndless
{
    // 拠点のエンドレス端末の 1 ページ目（エンドレス）の見出しの横に「通常 / 調整」を足す（サバイバルのノーマル / ハードと同じ形）。
    //   調整を選ぶと、ランキングの代わりに 敵の強さ・数・体力（◀ ▶ をクリックで 0.5 ずつ）と、運・参加費を出す。開始のボタンの $500 も参加費に
    //   サバイバルmod の 2 ページ目を開いている間は何も出さない
    public static class Tablet
    {
        static EndlessModeTablet _tablet;
        static Transform _canvas;
        static RectTransform _canvasRt;
        static TMP_Text _title, _modeNormal, _modeTuned, _summary, _note;
        static readonly TMP_Text[] _label = new TMP_Text[3], _minus = new TMP_Text[3], _value = new TMP_Text[3], _plus = new TMP_Text[3];
        static readonly List<GameObject> _endlessOnly = new();
        static readonly List<TMP_Text> _buttonTexts = new();
        static readonly List<string> _gameText = new();   // ゲームが出している文字（言語に合わせて変わる）。調整中はその $500 を参加費にする
        static bool _wasTuned;
        static readonly List<string> _lastSet = new();
        static float _nextFind;
        static string _shown;   // 最後に出した内容（変わったときだけ書き直す）

        public static void OnSceneLoaded()
        {
            _tablet = null; _canvas = null; _title = null; _modeNormal = null; _modeTuned = null; _summary = null; _note = null;
            Array.Clear(_label); Array.Clear(_minus); Array.Clear(_value); Array.Clear(_plus);
            _endlessOnly.Clear(); _buttonTexts.Clear(); _gameText.Clear(); _lastSet.Clear(); _nextFind = 0f; _shown = null; _wasTuned = false;
        }

        public static bool Ready => _modeNormal != null;

        public static void Tick()
        {
            if (Main.Scene != Core.HubScene) return;
            if (_modeNormal == null)
            {
                if (Time.realtimeSinceStartup < _nextFind) return;
                _nextFind = Time.realtimeSinceStartup + 2f;
                try { Build(); } catch (Exception e) { Main.Log("[tablet] build failed: " + e); _nextFind = Time.realtimeSinceStartup + 30f; }
                return;
            }
            Refresh();
            if (_tablet == null || !_tablet.focused || Link.SurvivalPageOpen()) return;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) Click(mouse.position.ReadValue());
        }

        public static bool Click(Vector2 screen)
        {
            if (_modeNormal == null || Link.SurvivalPageOpen()) return false;
            if (Hits(_modeNormal, screen)) { Tuning.Mode = false; Refresh(true); return true; }
            if (Hits(_modeTuned, screen)) { Tuning.Mode = true; Refresh(true); return true; }
            if (!Tuning.Mode) return false;
            for (int i = 0; i < 3; i++)
            {
                if (Hits(_minus[i], screen)) { Tuning.Change(i, -1); Refresh(true); return true; }
                if (Hits(_plus[i], screen)) { Tuning.Change(i, +1); Refresh(true); return true; }
            }
            return false;
        }

        // 画面の表示を今の状態に合わせる（サバイバルmod がページを切り替えたときも、次のフレームで直す）
        static void Refresh(bool force = false)
        {
            bool surv = Link.SurvivalPageOpen();
            bool tuned = !surv && Tuning.Mode;
            foreach (var g in _endlessOnly) if (g != null && g.activeSelf == tuned && !surv) g.SetActive(!tuned);
            SetActive(_modeNormal, !surv); SetActive(_modeTuned, !surv);
            foreach (var t in Rows()) SetActive(t, tuned);
            SetActive(_summary, tuned); SetActive(_note, tuned);
            if (!surv)
                for (int i = 0; i < _buttonTexts.Count; i++)
                {
                    var t = _buttonTexts[i];
                    if (t == null) continue;
                    if (!tuned)
                    {
                        if (_wasTuned) { t.text = _gameText[i]; t.ForceMeshUpdate(true, true); }
                        else _gameText[i] = t.text;
                        continue;
                    }
                    var want = FeeText(_gameText[i]);
                    if (t.text != want)
                    {
                        // 言語が切り替わってゲームが書き直したら、その文字を元にする
                        if (_wasTuned && t.text != _lastSet[i]) _gameText[i] = t.text;
                        want = FeeText(_gameText[i]);
                        t.text = want; t.ForceMeshUpdate(true, true);
                    }
                    _lastSet[i] = want;
                }
            if (!surv) _wasTuned = tuned;
            string key = $"{surv}|{tuned}|{Tuning.PowerSet}|{Tuning.CountSet}|{Tuning.HpSet}|{Main.Ja}";
            if (!force && key == _shown) return;
            _shown = key;
            if (surv) return;
            Mode(_modeNormal, !Tuning.Mode, Main.T("通常", "NORMAL"));
            Mode(_modeTuned, Tuning.Mode, Main.T("調節", "TUNED"));
            if (!tuned) return;
            string[] names = { Main.T("敵の強さ", "Enemy damage"), Main.T("敵の数", "Enemy count"), Main.T("敵の体力", "Enemy health") };
            for (int i = 0; i < 3; i++)
            {
                float v = Tuning.Get(i);
                Set(_label[i], names[i]);
                Set(_minus[i], "◀", v > Tuning.Min);
                Set(_value[i], $"×{v:0.0#}");
                Set(_plus[i], "▶", v < Tuning.MaxOf(i));
            }
            Set(_summary, Main.T($"運が {Tuning.TotalSet:0.0#}倍　参加費 ${Tuning.FeeSet:N0}", $"Luck x{Tuning.TotalSet:0.0#}   Entry fee ${Tuning.FeeSet:N0}"));
            bool cat = Tuning.CatNow(), vase = Tuning.VaseNow();
            string parts = !cat && !vase ? "" : Main.T($"調節 {Tuning.LuckSet:0.0#}倍" + (cat ? $" × 招き猫 {Tuning.CatLuck:0.0#}倍" : "") + (vase ? $" × 花瓶 {Tuning.VaseLuck:0.0#}倍" : "") + "\n",
                                                     $"Tuning x{Tuning.LuckSet:0.0#}" + (cat ? $" × Cat x{Tuning.CatLuck:0.0#}" : "") + (vase ? $" × Vase x{Tuning.VaseLuck:0.0#}" : "") + "\n");
            Set(_note, parts + Main.T("運が高いほど、武器と戦利品のレアリティが高くなる。弾薬・回復・防具・マガジン・お金はいつも通り出る",
                                      "Higher luck raises the rarity of weapons and loot. Ammo, healing, armor, magazines and money drop as usual"));
        }

        // ボタンの「$500」を参加費に
        //   ゲームの文字は英語の見本（訳は描くときにゲームが付ける）なので、先に訳してから金額を変える
        static string FeeText(string orig)
        {
            int fee = Tuning.FeeSet;
            try { var tr = Il2CppGameLocalization.LocManager.Translate(orig); if (!string.IsNullOrEmpty(tr)) orig = tr; } catch { }
            if (orig.Contains("500")) return orig.Replace("500", fee.ToString("N0"));
            return $"${fee:N0} - " + orig;
        }

        static IEnumerable<TMP_Text> Rows() { foreach (var a in new[] { _label, _minus, _value, _plus }) foreach (var t in a) yield return t; }

        static void SetActive(TMP_Text t, bool on) { if (t != null && t.gameObject.activeSelf != on) t.gameObject.SetActive(on); }
        static void Set(TMP_Text t, string s, bool enabled = true)
        {
            if (t == null) return;
            t.text = s;
            t.color = enabled ? Color.white : new Color(0.35f, 0.35f, 0.35f);
            t.ForceMeshUpdate(true, true);
        }
        static void Mode(TMP_Text t, bool picked, string name)
        {
            if (t == null) return;
            t.text = (picked ? "▶ " : "   ") + name;
            t.color = picked ? Color.white : new Color(0.5f, 0.5f, 0.5f);
            t.ForceMeshUpdate(true, true);
        }

        static void Build()
        {
            _tablet = Object.FindObjectOfType<EndlessModeTablet>();
            if (_tablet == null) return;
            _canvas = _tablet.transform.Find("Canvas");
            if (_canvas == null) { Main.Log("[tablet] canvas not found"); _nextFind = Time.realtimeSinceStartup + 30f; return; }
            _canvasRt = _canvas.GetComponent<RectTransform>();
            _title = _canvas.Find("Text (TMP)")?.GetComponent<TMP_Text>();
            if (_title == null) { Main.Log("[tablet] title not found"); _nextFind = Time.realtimeSinceStartup + 30f; return; }
            // ランキング・注意書き（見出しは残す）
            foreach (var n in new[] { "Text (TMP) (2)", "Text (TMP) (1)", "PERSONAL_UI_Prefab", "list parent" })
            {
                var t = _canvas.Find(n);
                if (t != null) _endlessOnly.Add(t.gameObject);
            }
            var button = _canvas.Find("Button (Panel)");
            if (button != null) foreach (var t in button.GetComponentsInChildren<TMP_Text>(true)) { _buttonTexts.Add(t); _gameText.Add(t.text); _lastSet.Add(null); }

            // 見出しの横に「通常 / 調整」（サバイバルのノーマル / ハードと同じ所）
            // 開始のボタンの左に縦に並べる（右上の下だとランキングが埋まったとき 1 位の行に重なる）
            _modeNormal = Text("FPEndless normal", -0.39f, -0.383f, 0.12f, 0.05f, 0.42f, TextAlignmentOptions.Left);
            _modeTuned = Text("FPEndless tuned", -0.39f, -0.438f, 0.12f, 0.05f, 0.42f, TextAlignmentOptions.Left);
            for (int i = 0; i < 3; i++)
            {
                float y = 0.2f - i * 0.13f;
                _label[i] = Text($"FPEndless label {i}", -0.2f, y, 0.4f, 0.11f, 0.42f, TextAlignmentOptions.Left);
                _minus[i] = Text($"FPEndless minus {i}", 0.06f, y, 0.08f, 0.11f, 0.42f, TextAlignmentOptions.Center);
                _value[i] = Text($"FPEndless value {i}", 0.17f, y, 0.14f, 0.11f, 0.42f, TextAlignmentOptions.Center);
                _plus[i] = Text($"FPEndless plus {i}", 0.28f, y, 0.08f, 0.11f, 0.42f, TextAlignmentOptions.Center);
            }
            _summary = Text("FPEndless summary", 0f, -0.2f, 0.86f, 0.1f, 0.42f, TextAlignmentOptions.Left);
            _note = Text("FPEndless note", 0f, -0.32f, 0.86f, 0.12f, 0.32f, TextAlignmentOptions.TopLeft);
            _note.enableWordWrapping = true;
            Refresh(true);
            Main.Log($"[tablet] tuning ready (button texts {_buttonTexts.Count})");
        }

        // 見出しを複製した文字。位置・大きさはキャンバスの大きさに対する割合（中央が 0）、size は見出しの文字に対する倍率
        static TMP_Text Text(string name, float x, float y, float w, float h, float size, TextAlignmentOptions align)
        {
            var go = Object.Instantiate(_title.gameObject, _title.transform.parent, false);
            go.name = name;
            for (int i = go.transform.childCount - 1; i >= 0; i--) Object.Destroy(go.transform.GetChild(i).gameObject);
            var t = go.GetComponent<TMP_Text>();
            var rt = t.rectTransform;
            var r = _canvasRt.rect;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(r.width * w, r.height * h) / Math.Max(0.01f, _title.rectTransform.localScale.x);   // 見出しは拡大して置かれている
            rt.anchoredPosition = new Vector2(r.width * x, r.height * y);
            t.fontSize = _title.fontSize * size;
            t.enableAutoSizing = false;
            t.alignment = align;
            t.text = "";
            go.SetActive(false);
            return t;
        }

        static bool Hits(TMP_Text text, Vector2 screen)
        {
            var cam = Camera.main;
            if (text == null || cam == null || !text.gameObject.activeInHierarchy) return false;
            var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            text.rectTransform.GetWorldCorners(arr);
            var min = new Vector2(float.MaxValue, float.MaxValue); var max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < 4; i++)
            {
                var p = cam.WorldToScreenPoint(arr[i]);
                if (p.z <= 0f) return false;
                min = Vector2.Min(min, p); max = Vector2.Max(max, p);
            }
            float pad = Mathf.Max(6f, (max.y - min.y) * 0.2f);
            return screen.x >= min.x - pad && screen.x <= max.x + pad && screen.y >= min.y - pad && screen.y <= max.y + pad;
        }

        // テスト用：その文字の真ん中の画面の位置
        public static Vector2? ScreenOf(string what, int row = 0)
        {
            TMP_Text t = what switch { "normal" => _modeNormal, "tuned" => _modeTuned, "minus" => _minus[row], "plus" => _plus[row], _ => null };
            var cam = Camera.main;
            if (t == null || cam == null) return null;
            var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            t.rectTransform.GetWorldCorners(arr);
            var p = cam.WorldToScreenPoint((arr[0] + arr[2]) / 2f);
            return new Vector2(p.x, p.y);
        }
        public static void DumpButtonsForTest()
        {
            if (_canvas == null) return;
            foreach (var t in _canvas.GetComponentsInChildren<TMP_Text>(true))
            {
                if (t.text == null || !t.text.Contains("500")) continue;
                string path = t.name; for (var p = t.transform.parent; p != null && p != _canvas; p = p.parent) path = p.name + "/" + path;
                var comps = string.Join(",", System.Linq.Enumerable.Select(t.GetComponents<Component>(), c => c.GetIl2CppType().Name));
                Main.Log($"[tablet] text '{t.text}' active {t.gameObject.activeInHierarchy} {path} [{comps}] tracked {_buttonTexts.Exists(b => b != null && b.Pointer == t.Pointer)}");
            }
        }
        public static string ButtonTextForTest => _buttonTexts.Count > 0 && _buttonTexts[0] != null ? _buttonTexts[0].text : "";
        public static bool RowsShownForTest => _value[0] != null && _value[0].gameObject.activeInHierarchy;
        public static string SummaryForTest => _summary?.text ?? "";
        public static EndlessModeTablet TabletForTest => _tablet;
    }
}
