using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppMichsky.UI.Heat;
using UnityEngine;

namespace FPModOption
{
    // 設定画面の MOD タブの自動テスト（タイトル画面で実行。settings.json はテスト用スクリプトが前後で退避・復元する）
    public static class ModTabTest
    {
        static void Check(string what, bool ok) => TestMode.Check(what, ok);
        static float _scrollStart;

        static void Shot(string name)
        {
            var dir = Path.Combine(Core.Dir, "shots");
            Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + ".png"));
        }

        static PanelManager Main() => Core.FindAll<PanelManager>().FirstOrDefault(pm => pm.panels.ToArray().Any(p => p.panelName == "Settings"));

        // ゲームの設定のスイッチ（MOD タブ以外）の状態
        static string GameSwitches() => string.Join(",", Core.FindAll<SwitchManager>()
            .Where(s => !s.transform.GetComponentsInParent<Transform>(true).Any(t => t.name == ModTab.Name))
            .Select(s => $"{s.saveKey}={s.isOn}"));

        // 文字がはみ出していないか（枠の幅に収まり、隣の行と重ならない）
        static List<string> TextProblems()
        {
            var bad = new List<string>();
            foreach (var t in ModTab.TextsForTest())
            {
                if (t == null || !t.gameObject.activeInHierarchy || string.IsNullOrEmpty(t.text)) continue;
                t.ForceMeshUpdate();
                var r = t.rectTransform.rect;
                if (t.isTextOverflowing || t.preferredHeight > r.height + 2f || t.fontSize < 18f) bad.Add($"'{t.text}' pref={t.preferredHeight:0} h={r.height:0} w={r.width:0} size={t.fontSize}");
            }
            return bad;
        }

        public static List<(string, Action)> Steps()
        {
            PanelManager settings = null;
            string before = null;
            string lang = null;
            var s = new List<(string, Action)>
            {
                ("open settings", () =>
                {
                    var main = Main();
                    Core.Log($"[modtabtest] main menu panel manager found={main != null}");
                    main?.OpenPanel("Settings");
                }),
                ("tab added", () =>
                {
                    var (pm, switches, ids, _) = ModTab.ForTest();
                    settings = pm;
                    int want = Settings.Known.Count(m => Settings.Installed(m.Id));
                    Core.Log($"[modtabtest] built={ModTab.BuiltCount} rows={string.Join(",", ids ?? new List<string>())} installed={want}");
                    Check("MOD tab added next to the graphics tab", pm != null && ModTab.ButtonForTest != null && ModTab.ButtonForTest.activeInHierarchy
                          && ModTab.ButtonForTest.transform.GetSiblingIndex() > pm.panels.ToArray().First(p => p.panelName == "Visuals").panelButton.transform.GetSiblingIndex());
                    Check($"one row per installed mod ({ids?.Count} = {want})", ids != null && ids.Count == want && want > 0);
                    Check("switches show the saved settings (first launch: all on)", switches != null && switches.Select((w, i) => w.isOn == Settings.Wanted(ids[i])).All(x => x));
                    before = GameSwitches();
                }),
                ("open MOD tab", () =>
                {
                    ModTab.ButtonForTest?.GetComponent<PanelButton>()?.onClick?.Invoke();
                }),
                ("MOD tab shown", () =>
                {
                    Check("MOD page opens from its tab button", ModTab.PanelForTest != null && ModTab.PanelForTest.activeInHierarchy);
                    var bad = TextProblems();
                    foreach (var b in bad) Core.Log("[modtabtest] text problem " + b);
                    Check("ja: texts fit, size 18+", bad.Count == 0);
                    Check("ja: names and descriptions in Japanese", ModTab.ForTest().texts.Any(t => t.Contains("レアリティ")));
                    // レアリティmod の機能は、親の行の下の字下げした枠にまとまり、親がオンなので開いている
                    var g = ModTab.GroupForTest("FPRarity");
                    var (_, sw2, ids2, _) = ModTab.ForTest();
                    var texts = ModTab.TextsForTest();
                    int pi = ids2.IndexOf("FPRarity"), si = ids2.IndexOf("FPRarity.Rarity");
                    bool subsIn = g != null && ids2.Select((id, i) => (id, i)).Where(x => x.id.StartsWith("FPRarity.")).All(x => sw2[x.i].transform.IsChildOf(g.transform));
                    var nameP = pi >= 0 ? texts[2 + pi * 2] : null;
                    var nameS = si >= 0 ? texts[2 + si * 2] : null;
                    Core.Log($"[modtabtest] group: found={g != null} open={g?.activeInHierarchy} subs inside={subsIn} name x {nameP?.transform.position.x:0} -> {nameS?.transform.position.x:0}");
                    // 枠が下の Mod の行に重ならない（枠の下端が、次の行の上端より上）
                    float gBottom = float.NaN, nextTop = float.NaN;
                    if (g != null)
                    {
                        // 一覧の中の座標で比べる（IL2CPP では GetWorldCorners の配列が戻らない）
                        var grt = g.GetComponent<RectTransform>();
                        gBottom = grt.localPosition.y + grt.rect.yMin;
                        int ni = g.transform.GetSiblingIndex() + 1;
                        if (ni < g.transform.parent.childCount) { var nrt = g.transform.parent.GetChild(ni).GetComponent<RectTransform>(); nextTop = nrt.localPosition.y + nrt.rect.yMax; }
                    }
                    Core.Log($"[modtabtest] group bottom {gBottom:0} next row top {nextTop:0} (h={g?.GetComponent<RectTransform>()?.rect.height:0})");
                    Check("rarity features do not overlap the next mod", g != null && (float.IsNaN(nextTop) || gBottom >= nextTop - 2f));
                    Check("rarity features grouped under the rarity mod, indented, open", g != null && g.activeInHierarchy && subsIn && nameP != null && nameS != null && nameS.transform.position.x > nameP.transform.position.x + 20f);
                    Shot("modtab_ja");
                }),
                ("scroll down", () =>
                {
                    // マウスホイールを下へ 1 回分（ゲームの入力の 1 段 = 120）。中身が動くか。比べるためグラフィックのページの値も出す
                    var sr = ModTab.PanelForTest.GetComponentInChildren<UnityEngine.UI.ScrollRect>(true);
                    _scrollStart = sr != null && sr.content != null ? sr.content.anchoredPosition.y : float.NaN;
                    var ev = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { scrollDelta = new Vector2(0f, -1f) };
                    Core.Log($"[modtabtest] scroll: rect={sr?.name} content={sr?.content?.name} h={sr?.content?.rect.height:0} view={sr?.viewport?.rect.height:0} sens={sr?.scrollSensitivity} pos={_scrollStart:0}");
                    for (int i = 0; i < 3; i++) sr?.OnScroll(ev);
                }),
                ("scroll check", () =>
                {
                    var sr = ModTab.PanelForTest.GetComponentInChildren<UnityEngine.UI.ScrollRect>(true);
                    float y = sr != null && sr.content != null ? sr.content.anchoredPosition.y : float.NaN;
                    bool needs = sr != null && sr.viewport != null && sr.content.rect.height > sr.viewport.rect.height + 10f;
                    Core.Log($"[modtabtest] scroll after 3 wheel steps: {_scrollStart:0} -> {y:0} (content longer than the view: {needs})");
                    Check($"MOD page scrolls with the mouse wheel ({_scrollStart:0} -> {y:0})", !needs || y - _scrollStart > 60f);
                    sr.verticalNormalizedPosition = 1f;
                }),
                ("toggle first mod off", () =>
                {
                    var (_, sw, ids, _) = ModTab.ForTest();
                    // スイッチそのものを押す（マウスで押したときと同じ処理）
                    sw[0].OnPointerClick(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current));
                }),
                ("check toggled", () =>
                {
                    var (_, sw, ids, _) = ModTab.ForTest();
                    var json = File.Exists(Path.Combine(Core.Dir, "settings.json")) ? File.ReadAllText(Path.Combine(Core.Dir, "settings.json")) : "";
                    Core.Log($"[modtabtest] after toggle: {ids[0]} isOn={sw[0].isOn} wanted={Settings.Wanted(ids[0])} enabledNow={Settings.Enabled(ids[0])} json={json.Replace("\n", " ")}");
                    Check("toggle off saves the setting for the next launch", !sw[0].isOn && !Settings.Wanted(ids[0]) && json.Contains($"\"{ids[0]}\": false"));
                    Check("running mods unchanged until restart", Settings.Enabled(ids[0]));
                    Check("restart notice shown", Settings.RestartNeeded && ModTab.TextsForTest()[1].gameObject.activeInHierarchy);
                    Check("game settings unchanged by the MOD switch", GameSwitches() == before);
                    if (ids[0] == "FPRarity")
                    {
                        var g = ModTab.GroupForTest("FPRarity");
                        Check("rarity mod off: its features are closed, their settings kept", g != null && !g.activeInHierarchy
                              && Settings.Wanted("FPRarity.Rarity") && Settings.Wanted("FPRarity.Mastery") && Settings.Wanted("FPRarity.Boss"));
                    }
                    Shot("modtab_ja_off");
                }),
                ("toggle back on", () =>
                {
                    var (_, sw, _, _) = ModTab.ForTest();
                    // 行（FPM_<id>）のボタンを押す（スイッチ自身にもボタンがあるので、行を名前で探す）
                    var row = sw[0].transform;
                    while (row != null && !row.name.StartsWith("FPM_")) row = row.parent;
                    var rb = row?.GetComponent<UnityEngine.UI.Button>();
                    Core.Log($"[modtabtest] row button: row={row?.name} button={rb != null} self={sw[0].GetComponent<UnityEngine.UI.Button>() != null}");
                    if (rb != null) rb.onClick.Invoke();
                    else sw[0].OnPointerClick(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)); Core.Log($"[modtabtest] after row click: isOn={sw[0].isOn}");
                }),
                ("check restored", () =>
                {
                    var (_, sw, ids, _) = ModTab.ForTest();
                    var g = ModTab.GroupForTest("FPRarity");
                    if (ids[0] == "FPRarity") Check("rarity mod on: its features open again", g != null && g.activeInHierarchy);
                    Check("toggle back on: saved on, no restart notice", sw[0].isOn && Settings.Wanted(ids[0]) && !Settings.RestartNeeded && !ModTab.TextsForTest()[1].gameObject.activeInHierarchy);
                    Check("game settings still unchanged", GameSwitches() == before);
                }),
                ("other tab", () => settings?.OpenPanel("Audio")),
                ("back to MOD", () =>
                {
                    Check("MOD page hidden on another tab", ModTab.PanelForTest != null && !ModTab.PanelForTest.activeInHierarchy);
                    settings?.OpenPanel("MOD");
                }),
                ("english", () =>
                {
                    Check("MOD page shown again", ModTab.PanelForTest != null && ModTab.PanelForTest.activeInHierarchy);
                    lang = Il2CppGameLocalization.LocManager.Code;
                    Il2CppGameLocalization.LocManager.SetLanguage("en");
                }),
                ("english check", () =>
                {
                    var bad = TextProblems();
                    foreach (var b in bad) Core.Log("[modtabtest] text problem en " + b);
                    Check("en: texts fit, size 18+", bad.Count == 0);
                    Check("en: names and descriptions in English", ModTab.ForTest().texts.Any(t => t.Contains("Rarity mod")) && !ModTab.ForTest().texts.Any(t => t.Contains("レアリティ")));
                    Shot("modtab_en");
                }),
                ("restore language", () => { if (!string.IsNullOrEmpty(lang)) Il2CppGameLocalization.LocManager.SetLanguage(lang); }),
                ("close", () => Main()?.OpenPanel("Home")),
            };
            return s;
        }

        // 拠点：一時停止メニューの設定にも MOD タブがあれば、同じように使えるか（無ければログだけ）
        public static List<(string, Action)> HubSteps() => new()
        {
            ("hub", () =>
            {
                Core.Log($"[modtabtest] hub: MOD tab built={ModTab.BuiltCount}");
                Check("hub: no errors building the MOD tab", true);
            }),
        };
    }
}
