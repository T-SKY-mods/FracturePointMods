using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppMichsky.UI.Heat;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace FPModOption
{
    // 設定画面の「MOD」タブ（キーボード・コントローラー・サウンド・グラフィックの右）。
    //   Mod ごとに名前・説明・オン/オフのスイッチを並べる。切り替えはすぐ保存し、ゲームを再起動すると反映される。
    //   見た目はゲームの設定の行（スイッチの行・見出し）を複製して作る。複製した行のイベント（ゲームの設定を変えるもの）はすべて外す
    public static class ModTab
    {
        public const string Name = "FPM_Mods";
        static readonly List<Built> _built = new();
        static float _sceneAt;
        static int _tries;
        static bool _ja;

        sealed class Built
        {
            public PanelManager Manager;
            public GameObject Button, Panel;
            public TMP_Text Note, Status;
            public float LaidWidth;   // 文字の高さを合わせたときの幅（開いて幅が決まったら合わせ直す）
            public bool Wired;        // 行のボタンを付け直したか（ページを初めて開いたとき）
            public readonly List<(ModEntry Mod, TMP_Text Name, TMP_Text Desc, SwitchManager Switch)> Rows = new();
            public readonly Dictionary<string, GameObject> Groups = new();   // 親の Mod の Id → その中の機能の行をまとめた枠
        }

        public static void Install(HarmonyLib.Harmony h)
        {
            TestMode.MenuScenarios["modtab"] = ModTabTest.Steps;
            TestMode.ExternalScenarios["modtabhub"] = ModTabTest.HubSteps;
            TestMode.ExternalScenarios["statlist"] = StatList.Steps;
            TestMode.ExternalScenarios["lootstats"] = LootStats.Steps;
        }

        public static void OnSceneChanged()
        {
            _built.Clear();
            _sceneAt = Time.realtimeSinceStartup;
            _tries = 0;
        }

        // 設定画面はシーンの読み込みの少し後にできることがあるので、数回だけ探す（毎フレーム探すと重い）
        static readonly float[] SearchAt = { 1f, 4f, 10f, 25f };

        public static void Tick()
        {
            float t = Time.realtimeSinceStartup - _sceneAt;
            if (_tries < SearchAt.Length && t >= SearchAt[_tries])
            {
                _tries++;
                foreach (var pm in Core.FindAll<PanelManager>()) TryBuild(pm);
            }
            _built.RemoveAll(b => !Refs.Alive(b.Panel));
            if (_built.Count == 0) return;
            // 言語を変えたら書き直す
            if (_ja != Core.Ja) { _ja = Core.Ja; foreach (var b in _built) Refresh(b); }
            foreach (var b in _built)
            {
                if (!b.Panel.activeInHierarchy) continue;
                if (!b.Wired) { b.Wired = true; Wire(b); }
                float w = b.Note.rectTransform.rect.width;
                if (w > 10f && Mathf.Abs(w - b.LaidWidth) > 1f) { b.LaidWidth = w; Refresh(b); }
                foreach (var row in b.Rows)
                    if (row.Switch != null && row.Switch.isOn != Settings.Wanted(row.Mod.Id)) Settings.Set(row.Mod.Id, row.Switch.isOn);
                RefreshGroups(b);
                RefreshStatus(b);
            }
        }

        // ---------- 組み立て ----------

        // 設定のタブ（サウンドとグラフィックがある PanelManager）に MOD のタブを足す
        static void TryBuild(PanelManager pm)
        {
            try
            {
                if (pm == null || _built.Any(b => b.Manager.Pointer == pm.Pointer)) return;
                var panels = pm.panels;
                if (panels == null) return;
                PanelManager.PanelItem audio = null, visuals = null;
                for (int i = 0; i < panels.Count; i++)
                {
                    var p = panels[i];
                    if (p?.panelName == "MOD") return;
                    if (p?.panelName == "Audio") audio = p;
                    if (p?.panelName == "Visuals") visuals = p;
                }
                if (audio?.panelObject == null || visuals?.panelButton == null) return;
                var list = FindList(pm.transform);
                var rowTpl = list?.GetComponentsInChildren<SettingsElement>(true).FirstOrDefault(e => e.GetComponentInChildren<SwitchManager>(true) != null);
                var labelTpl = rowTpl?.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.transform.parent == rowTpl.transform);
                if (rowTpl == null || labelTpl == null) { Core.LogOnce("modtab-tpl", $"[modtab] templates missing row={rowTpl != null} label={labelTpl != null} under {pm.name}"); return; }
                Build(pm, audio, visuals, rowTpl.gameObject, labelTpl);
            }
            catch (Exception e) { Core.Log("[modtab] build failed: " + e); }
        }

        // スイッチの行がある一覧（Panels/…/Content/List/Layout Group）
        static Transform FindList(Transform pm)
        {
            foreach (var se in pm.GetComponentsInChildren<SettingsElement>(true))
                if (se.GetComponentInChildren<SwitchManager>(true) != null && se.transform.parent?.GetComponent<VerticalLayoutGroup>() != null)
                    return se.transform.parent;
            return null;
        }

        // 無効な親の下に複製する（複製の Awake・OnEnable でゲームの設定の処理が走らないように）
        static GameObject Clone(GameObject src, Transform parent)
        {
            var holder = new GameObject("FPM_tmp");
            holder.SetActive(false);
            var go = UnityEngine.Object.Instantiate(src, holder.transform, false);
            // 翻訳の部品（Heat）は元の英語に戻してしまうので外す
            foreach (var lo in go.GetComponentsInChildren<LocalizedObject>(true)) UnityEngine.Object.DestroyImmediate(lo);
            go.transform.SetParent(parent, false);
            UnityEngine.Object.Destroy(holder);
            return go;
        }

        static void Build(PanelManager pm, PanelManager.PanelItem audio, PanelManager.PanelItem visuals, GameObject rowTpl, TMP_Text labelTpl)
        {
            var b = new Built { Manager = pm };

            // タブのボタン（グラフィックを複製して右に置く。グラフィックの区切り線を出す）
            var vis = visuals.panelButton;
            var btn = Clone(vis.gameObject, vis.transform.parent);
            btn.name = Name;
            btn.transform.SetSiblingIndex(vis.transform.GetSiblingIndex() + 1);
            var pb = btn.GetComponent<PanelButton>();
            pb.buttonText = "MOD";
            pb.useLocalization = false;
            pb.isSelected = false;
            pb.onClick = new UnityEvent();
            pb.onClick.AddListener(new Action(() => { try { pm.OpenPanel("MOD"); } catch (Exception e) { Core.Log("[modtab] open failed: " + e.Message); } }));
            foreach (var t in btn.GetComponentsInChildren<TMP_Text>(true)) t.text = "MOD";
            SetSeparator(vis.transform, true);
            SetSeparator(btn.transform, false);
            btn.SetActive(true);
            try { pb.UpdateUI(); } catch { }
            b.Button = btn;

            // ページ（サウンドを複製して中身を入れ替える）
            var page = Clone(audio.panelObject.gameObject, audio.panelObject.transform.parent);
            page.name = Name;
            b.Panel = page;
            var list = FindListIn(page.transform);
            if (list == null) { Core.Log("[modtab] list not found in the page copy"); UnityEngine.Object.Destroy(btn); UnityEngine.Object.Destroy(page); return; }
            for (int i = list.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(list.GetChild(i).gameObject);
            // 下の「リセット」の帯はこのページには無い
            foreach (var tr in page.GetComponentsInChildren<Transform>(true)) if (tr.name == "PadResetStrip") tr.gameObject.SetActive(false);

            b.Note = TextBlock(labelTpl, list, 24f, new Color(0.85f, 0.85f, 0.85f, 1f));
            b.Status = TextBlock(labelTpl, list, 24f, new Color(1f, 0.8f, 0.3f, 1f));
            foreach (var m in Settings.Known)
            {
                if (!Settings.Installed(m.Id)) continue;
                // Mod の中の機能は、親の Mod の行のすぐ下の枠（字下げ・左に線）にまとめる。親がオンのときだけ開く
                var parent = m.Parent != null ? Group(b, m.Parent, list) : list;
                var row = Clone(rowTpl, parent);
                row.name = "FPM_" + m.Id;
                var name = row.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.transform.parent == row.transform);
                var sw = row.GetComponentInChildren<SwitchManager>(true);
                // ゲームの設定に繋がっているイベント・保存を外して、この Mod の設定だけを変える
                sw.saveValue = false;
                sw.saveKey = "";
                sw.invokeOnEnable = false;
                sw.onValueChanged = new SwitchManager.SwitchEvent();
                sw.onEvents = new UnityEvent();
                sw.offEvents = new UnityEvent();
                sw.isOn = Settings.Wanted(m.Id);
                var id = m.Id;
                // 行を押したらスイッチを切り替える（保存は Tick がスイッチの状態を見て行う。どこを押しても同じになるように）
                WireRow(row.GetComponent<Button>(), sw);
                var desc = TextBlock(labelTpl, parent, 20f, new Color(0.7f, 0.7f, 0.7f, 1f));
                b.Rows.Add((m, name, desc, sw));
            }
            // 枠は親の Mod の説明のすぐ下に置く
            foreach (var g in b.Groups)
            {
                var pr = b.Rows.FirstOrDefault(r => r.Mod.Id == g.Key);
                if (pr.Desc != null) g.Value.transform.SetSiblingIndex(pr.Desc.transform.parent.GetSiblingIndex() + 1);
            }

            // マウスホイールのスクロール：ページはサウンドの複製で、スクロールの感度がサウンドのまま（ほとんど動かない）。
            // スクロールするページ（グラフィック）と同じ感度にする
            var vsr = visuals.panelObject != null ? visuals.panelObject.GetComponentInChildren<ScrollRect>(true) : null;
            foreach (var sr in page.GetComponentsInChildren<ScrollRect>(true))
            {
                Core.Log($"[modtab] scroll {sr.name}: content={sr.content?.name} sens={sr.scrollSensitivity} vertical={sr.vertical} graphics sens={vsr?.scrollSensitivity}");
                if (vsr != null && vsr.scrollSensitivity > sr.scrollSensitivity) sr.scrollSensitivity = vsr.scrollSensitivity;
                sr.vertical = true;
            }

            var anim = page.GetComponent<Animator>();
            pm.panels.Add(new PanelManager.PanelItem { panelName = "MOD", panelObject = anim, panelButton = pb });
            page.SetActive(false);
            _built.Add(b);
            _ja = Core.Ja;
            Refresh(b);
            RefreshGroups(b);
            foreach (var r in b.Rows) { try { r.Switch.UpdateUI(); } catch { } }
            Core.Log($"[modtab] added to {pm.transform.parent?.parent?.name}/{pm.name}: {string.Join(", ", b.Rows.Select(r => r.Mod.Id))}");
            var d = b.Rows.Count > 0 ? b.Rows[0].Desc : b.Note;
            Core.Log($"[modtab] text check: color={d.color} alpha={d.alpha} enabled={d.enabled} active={d.gameObject.activeSelf} rect={d.rectTransform.rect}");
        }

        static Transform FindListIn(Transform page)
        {
            foreach (var v in page.GetComponentsInChildren<VerticalLayoutGroup>(true))
                if (v.GetComponent<ContentSizeFitter>() != null) return v.transform;
            return null;
        }

        // 親の Mod の中の機能をまとめる枠：一覧と同じ並べ方で、左を空けて細い線を引く
        static Transform Group(Built b, string parentId, Transform list)
        {
            if (b.Groups.TryGetValue(parentId, out var g)) return g.transform;
            g = new GameObject("FPM_group_" + parentId);
            g.AddComponent<RectTransform>().SetParent(list, false);
            var src = list.GetComponent<VerticalLayoutGroup>();
            var v = g.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(GroupIndent, 0, 0, 0);
            v.spacing = src.spacing;
            v.childControlWidth = src.childControlWidth; v.childControlHeight = src.childControlHeight;
            v.childForceExpandWidth = src.childForceExpandWidth; v.childForceExpandHeight = false;
            // 一覧は行の高さを自分では決めないので、枠の高さは中身に合わせる（合わせないと下の Mod の行に重なる）
            g.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var line = new GameObject("FPM_line");
            var lrt = line.AddComponent<RectTransform>();
            lrt.SetParent(g.transform, false);
            line.AddComponent<LayoutElement>().ignoreLayout = true;
            lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(0, 1); lrt.pivot = new Vector2(0, 0.5f);
            lrt.offsetMin = new Vector2(GroupIndent / 2f - 2f, 6f); lrt.offsetMax = new Vector2(GroupIndent / 2f + 2f, -6f);
            var img = line.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.35f);
            img.raycastTarget = false;
            b.Groups[parentId] = g;
            return g.transform;
        }

        const int GroupIndent = 56;

        // 親の Mod のスイッチがオンのときだけ機能の枠を開く（オフの間は機能を押せない。機能の設定はそのまま残す）
        static void RefreshGroups(Built b)
        {
            foreach (var g in b.Groups)
            {
                var pr = b.Rows.FirstOrDefault(r => r.Mod.Id == g.Key);
                bool open = pr.Switch != null ? pr.Switch.isOn : Settings.Wanted(g.Key);
                if (g.Value.activeSelf != open) g.Value.SetActive(open);
            }
        }

        static void SetSeparator(Transform button, bool on)
        {
            var s = button.Find("Seperator");
            if (s != null) s.gameObject.SetActive(on);
        }

        // 説明などの文字の行：行の名前の文字を複製して、行と同じ左端に置く（折り返しあり、高さは文字に合わせる）
        static TMP_Text TextBlock(TMP_Text label, Transform list, float size, Color color)
        {
            var box = new GameObject("FPM_text");
            var brt = box.AddComponent<RectTransform>();
            brt.SetParent(list, false);
            var le = box.AddComponent<LayoutElement>();
            le.minHeight = 30f; le.preferredHeight = 30f;
            var go = Clone(label.gameObject, box.transform);
            // テーマの色・字体の部品は色を上書きするので外す
            foreach (var c in go.GetComponents<Component>()) if (c.GetIl2CppType().Name == "UIManagerText") UnityEngine.Object.DestroyImmediate(c);
            var t = go.GetComponent<TMP_Text>();
            var rt = t.rectTransform;
            float left = Mathf.Max(10f, label.rectTransform.offsetMin.x);
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(left, 0); rt.offsetMax = new Vector2(-left, 0);
            t.enableWordWrapping = true;
            t.overflowMode = TextOverflowModes.Overflow;
            t.fontStyle = FontStyles.Normal;
            t.alignment = TextAlignmentOptions.TopLeft;
            t.fontSize = size;
            t.enableAutoSizing = false;
            t.color = color;
            t.alpha = 1f;
            return t;
        }

        // 行を押したらスイッチを切り替える。ゲームの部品がページを開いたときにボタンの処理を置き換えるので、開いたときにも付け直す
        static void WireRow(Button button, SwitchManager sw)
        {
            if (button == null || sw == null) return;
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(new Action(() => { try { Toggle(sw); } catch (Exception e) { Core.Log("[modtab] toggle failed: " + e.Message); } }));
        }

        static void Wire(Built b)
        {
            foreach (var r in b.Rows)
            {
                var row = r.Switch != null ? r.Switch.transform : null;
                while (row != null && !row.name.StartsWith("FPM_")) row = row.parent;
                WireRow(row != null ? row.GetComponent<Button>() : null, r.Switch);
            }
        }

        static void Toggle(SwitchManager sw)
        {
            // スイッチを直接押したときと同じ処理（見た目の切り替えも含む）
            sw.OnPointerClick(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current));
        }

        static void SetText(TMP_Text t, string s)
        {
            if (t == null) return;
            t.text = s;
            // 説明の行（FPM_text の中）は、入れ物の高さを文字に合わせる
            var box = t.transform.parent;
            if (box == null || box.name != "FPM_text") return;
            float w = t.rectTransform.rect.width > 10f ? t.rectTransform.rect.width : 1150f;
            float h = s.Length == 0 ? 0f : Mathf.Max(30f, t.GetPreferredValues(s, w, 0f).y + 8f);
            var le = box.GetComponent<LayoutElement>();
            le.minHeight = h; le.preferredHeight = h;
        }

        static void Refresh(Built b)
        {
            SetText(b.Note, GameVersion.Disabled
                ? Core.T("ゲーム更新に未対応のため、すべてのMODを停止中。", "All mods are off: not compatible with this game update.")
                : Core.T("オン/オフはゲームの再起動後に反映。", "Changes apply after restarting the game."));
            foreach (var r in b.Rows)
            {
                SetText(r.Name, r.Mod.Name);
                SetText(r.Desc, r.Mod.Desc);
            }
            RefreshStatus(b, force: true);
        }

        static void RefreshStatus(Built b, bool force = false)
        {
            string s = Settings.RestartNeeded ? Core.T("再起動すると変更が反映されます。", "Restart the game to apply the changes.") : "";
            if (!force && b.Status.text == s) return;
            SetText(b.Status, s);
            b.Status.gameObject.SetActive(s.Length > 0);
        }

        // ---------- テスト用 ----------

        public static int BuiltCount => _built.Count;
        public static (PanelManager pm, List<SwitchManager> switches, List<string> ids, string[] texts) ForTest()
        {
            var b = _built.FirstOrDefault();
            if (b == null) return default;
            var sw = new List<SwitchManager>(); var ids = new List<string>(); var texts = new List<string>();
            foreach (var r in b.Rows) { sw.Add(r.Switch); ids.Add(r.Mod.Id); texts.Add(r.Name.text); texts.Add(r.Desc.text); }
            texts.Add(b.Note.text);
            return (b.Manager, sw, ids, texts.ToArray());
        }
        public static GameObject PanelForTest => _built.FirstOrDefault()?.Panel;
        public static GameObject GroupForTest(string parentId) => _built.FirstOrDefault()?.Groups.TryGetValue(parentId, out var g) == true ? g : null;
        public static GameObject ButtonForTest => _built.FirstOrDefault()?.Button;
        public static List<TMP_Text> TextsForTest()
        {
            var b = _built.FirstOrDefault();
            if (b == null) return new();
            var l = new List<TMP_Text> { b.Note, b.Status };
            foreach (var r in b.Rows) { l.Add(r.Name); l.Add(r.Desc); }
            return l;
        }
    }
}
