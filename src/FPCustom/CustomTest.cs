using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 確認用（autotest -Dev -Scenario custui[@en]）。セーブ・custom.json は autotest.ps1 が前後で退避・復元する。
    // プレイヤーと同じ入口（保管庫の右上のタブ [カスタマイズ]）で開き、タブで熟練度と行き来し、解放・装着・外す・効果・保存・Esc を確かめて撮影する
    public static class CustomTest
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        static void Check(string what, bool ok) => Core.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");
        static void Shot(string n) { Directory.CreateDirectory(ShotDir); ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, n + ".png")); }
        static string _lang;
        static int _worstRifle = -1, _worstPistol = -1, _worstRifleRow, _worstPistolRow;

        static void MaxLayout(List<(string, Action)> s, string lang)
        {
            s.Add(($"max layout ({lang})", () =>
            {
                if (!CustomUi.IsOpen) CustomUi.OpenForTest();
                bool en = lang == "en";
                Check($"page built in {lang}", CustomUi.IsOpen && CustomUi.BuiltJapaneseForTest == !en && Core.Ja == !en);
                int bad = 0, rifleMax = -1, pistolMax = -1;
                foreach (var id in CustomUi.CardIdsForTest)
                {
                    int row = CustomUi.MaxOutForTest(id);
                    CustomUi.SelectForTest(id);
                    CustomUi.BrowseToForTest(row, Store.Equipped(id, Catalog.CatsFor(id)[row])?.Id);
                    var fx = CustomUi.EffectsForTest;
                    int lines = fx.Split('/').Length;
                    var prob = CustomUi.LayoutProblemsForTest(18f);
                    if (prob.Length > 0) { bad++; Core.Log($"[test] layout {lang} {Tracker.NameOf(id)}: {prob}"); }
                    if (Catalog.IsPistol(id)) { if (lines > pistolMax) { pistolMax = lines; _worstPistol = id; _worstPistolRow = row; } }
                    else if (lines > rifleMax) { rifleMax = lines; _worstRifle = id; _worstRifleRow = row; }
                }
                Core.Log($"[test] max layout {lang}: most effects rifle={Tracker.NameOf(_worstRifle)} pistol={Tracker.NameOf(_worstPistol)}");
                Check($"{lang}: every weapon with the most effects fits, text 18+ ({bad} with problems)", bad == 0);
            }));
            foreach (var pistol in new[] { false, true })
            {
                s.Add(($"max shot {lang} {(pistol ? "pistol" : "rifle")}", () =>
                {
                    int id = pistol ? _worstPistol : _worstRifle, row = 1;   // ストック・グリップ（効果がいちばん多い部品）
                    if (id < 0) return;
                    CustomUi.SelectForTest(id);
                    CustomUi.BrowseToForTest(row, Store.Equipped(id, Catalog.CatsFor(id)[row])?.Id);
                    Core.Log($"[test] max {lang} {Tracker.NameOf(id)}: {CustomUi.EffectsForTest.Replace("\n", " / ")}");
                }));
                Wait(s);
                s.Add(($"max shot {lang} {(pistol ? "pistol" : "rifle")} take", () => Shot($"cust_max_{lang}_{(pistol ? "pistol" : "rifle")}")));
            }
        }

        static void Wait(List<(string, Action)> s, int n = 1) { for (int i = 0; i < n; i++) s.Add(("wait", () => { })); }

        public static void Register() => TestMode.ExternalScenarios["custui"] = Steps;

        static int _rifle = 2, _pistol = 9, _partsBefore;   // AK-12（最初から部品付き）と SP-320
        static int Given => Store.UnlockCost * 2 + 5;   // 解放 2 回分 + 端数

        static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            s.Add(("setup", () =>
            {
                _partsBefore = Parts.Count();
                // 解放 2 回分 + 端数（テスト後にセーブは戻る）
                Refs.Inv.AddLootItem(Parts.ItemId, Given, false);
                Check($"parts given ({_partsBefore} -> {Parts.Count()})", Parts.Count() == _partsBefore + Given);
                Check($"AK-12 starts with its factory parts ({Store.Describe(_rifle)})",
                      Store.Equipped(_rifle, Cat.Muzzle)?.Id == "muzzle_brake" && Store.Equipped(_rifle, Cat.Grip)?.Id == "vertical_grip" &&
                      Store.Equipped(_rifle, Cat.Sight)?.Id == "holo" && Store.Equipped(_rifle, Cat.SideRail)?.Id == "laser" && Store.Equipped(_rifle, Cat.Mag) == null);
                Check($"AKMS starts bare ({Store.Describe(4)})", Store.Equipped(4, Cat.Muzzle) == null && Store.UnlockedCount(4) == 0);
            }));
            s.Add(("open stash", () => Refs.Bag.Interact()));
            Wait(s);
            s.Add(("tabs on the stash", () =>
            {
                var b = CustomUi.OpenButtonForTest;
                Check("customize tab on the stash screen, next to mastery", b != null && b.activeInHierarchy && (!Link.MasteryActive || Link.MasteryOpenButtonForTest?.Invoke() != null && Link.MasteryOpenButtonForTest().activeInHierarchy));
                Shot("cust_0_stash");   // 撮影はフレームの終わりなので、ページを開くのは次の手順で
            }));
            s.Add(("click customize tab", () => CustomUi.OpenButtonForTest?.GetComponent<UnityEngine.UI.Button>()?.onClick.Invoke()));
            Wait(s);
            s.Add(("tab to mastery", () => { Check("customize page opened from the stash tab", CustomUi.IsOpen); CustomUi.ClickMasteryTabForTest(); }));
            Wait(s);
            s.Add(("mastery page", () =>
            {
                Check("mastery tab switches pages", !Link.MasteryActive || (Link.MasteryOpen?.Invoke() ?? false) && !CustomUi.IsOpen);
                Shot("cust_0_mastery");
            }));
            s.Add(("tab back to customize", () => { if (Link.MasteryActive) CustomUi.ClickCustomTabOnMasteryForTest(); else CustomUi.OpenButtonForTest?.GetComponent<UnityEngine.UI.Button>()?.onClick.Invoke(); }));
            Wait(s);
            s.Add(("page", () =>
            {
                Check("customize page open, mastery page closed", CustomUi.IsOpen && !(Link.MasteryOpen?.Invoke() ?? false));
                Check($"29 weapons listed (all but the RPG) ({CustomUi.CardCount})", CustomUi.CardCount == 29);
                CustomUi.SelectForTest(_rifle);
                for (int r = 0; r < 5; r++) Core.Log($"[test] AK-12 row {r}: {CustomUi.RowForTest(r)}");
                Shot("cust_1_ak12");
            }));
            s.Add(("remove factory grip", () =>
            {
                CustomUi.BrowseToForTest(1, "vertical_grip");
                CustomUi.ClickActForTest(1);
                Check($"factory grip removed, no parts used ({Store.Describe(_rifle)}, parts {Parts.Count()})", Store.Equipped(_rifle, Cat.Grip) == null && Parts.Count() == _partsBefore + Given);
                CustomUi.ClickActForTest(1);
                Check("and attached again", Store.Equipped(_rifle, Cat.Grip)?.Id == "vertical_grip");
            }));
            s.Add(("unlock + attach on AKMS", () =>
            {
                CustomUi.SelectForTest(4);
                CustomUi.BrowseToForTest(0, "suppressor");
                Core.Log($"[test] AKMS muzzle before: {CustomUi.RowForTest(0)}");
                CustomUi.ClickActForTest(0);
                Check($"suppressor unlocked and attached, {Store.UnlockCost} parts used ({Store.Describe(4)}, parts {Parts.Count()})",
                      Store.Equipped(4, Cat.Muzzle)?.Id == "suppressor" && Parts.Count() == _partsBefore + Given - Store.UnlockCost);
                CustomUi.BrowseToForTest(4, "ext_mag");
                CustomUi.ClickActForTest(4);
                CustomUi.BrowseToForTest(4, "fast_mag");
                Check($"second unlock leaves 5 parts; third is not possible ({CustomUi.RowForTest(4)})", Parts.Count() == _partsBefore + 5 && (_partsBefore + 5 >= Store.UnlockCost || CustomUi.RowForTest(4).Contains("enabled=False")));
                CustomUi.BrowseToForTest(4, "ext_mag");
                Core.Log($"[test] AKMS effects: {CustomUi.EffectsForTest.Replace("\n", " / ")}");
                // 選択中＝拡張マガジン：プラス＝コントロール +5・容量 1.5 倍、マイナス＝弾込め -15%。合計＝コントロール +5・エルゴ -3（サプレッサー）
                var fx = CustomUi.EffectsForTest;
                var mag = Catalog.Get("ext_mag").Notes;
                string Col(string key, string next) { int i = fx.IndexOf(key + "='"), j = next != null ? fx.IndexOf(next + "='") : fx.Length; return i >= 0 && j > i ? fx.Substring(i, j - i) : ""; }
                string plusCol = Col("plus", "minus"), minusCol = Col("minus", "sum"), eqHead = Col("sum", "eqplus"), eqPlus = Col("eqplus", "eqminus"), eqMinus = Col("eqminus", null);
                string N((string Ja, string En, bool Plus) n) => Core.Ja ? n.Ja : n.En;
                var sup = Catalog.Get("suppressor");
                Check("effects: picked part split into pros (control +5, capacity) and cons (loading)",
                      plusCol.Contains("+5") && plusCol.Contains(N(mag[0])) && minusCol.Contains(N(mag[1])) && !minusCol.Contains("+5"));
                Check("effects: every equipped part listed with all its effects (suppressor + extended mag)",
                      eqHead.Contains(sup.Name(Core.Ja)) && eqHead.Contains(Catalog.Get("ext_mag").Name(Core.Ja))
                      && eqPlus.Contains("+5") && eqPlus.Contains(N(mag[0])) && sup.Notes.Where(n => n.Plus).All(n => eqPlus.Contains(N(n)))
                      && eqMinus.Contains("-3") && eqMinus.Contains(N(mag[1])));
                Shot("cust_2_akms");
                Preview.Save(Path.Combine(ShotDir, "cust_2_akms_view.png"));
            }));
            s.Add(("remove AKMS muzzle", () =>
            {
                CustomUi.BrowseToForTest(0, null);
                CustomUi.ClickActForTest(0);
                Check($"muzzle removed, stays unlocked ({Store.Describe(4)})", Store.Equipped(4, Cat.Muzzle) == null && Store.Unlocked(4, Catalog.Get("suppressor")));
                Check("other rarities share it: unlock is per weapon type", Store.UnlockedCount(4) == 2);
            }));
            s.Add(("pistol", () =>
            {
                CustomUi.SelectForTest(_pistol);
                for (int r = 0; r < 5; r++) Core.Log($"[test] SP-320 row {r}: {CustomUi.RowForTest(r)}");
                Check($"SP-320 rows are pistol categories with its dot sight and laser ({Store.Describe(_pistol)})",
                      CustomUi.RowForTest(1).Contains(Catalog.CatName(Cat.Stock, Core.Ja)) && Store.Equipped(_pistol, Cat.Sight)?.Id == "p_dot" && Store.Equipped(_pistol, Cat.SideRail)?.Id == "p_laser");
                Shot("cust_3_sp320");
            }));
            s.Add(("Esc closes to the stash", () =>
            {
                Refs.Bag.ClosePage();
                Check("page closed, stash still open", !CustomUi.IsOpen && Refs.Bag._stashOpened);
            }));
            s.Add(("saved file", () =>
            {
                var f = Path.Combine(Main.Dir, "custom.json");
                var txt = File.Exists(f) ? File.ReadAllText(f) : "";
                Check("custom.json written with the AKMS unlocks", txt.Contains("suppressor") && txt.Contains("ext_mag"));
                Store.Load();
                Check($"reload keeps the state ({Store.Describe(4)})", Store.Unlocked(4, Catalog.Get("ext_mag")) && Store.Equipped(4, Cat.Mag)?.Id == "ext_mag");
            }));
            // 全部の武器に効果のいちばん多い組み合わせを付けて、見切れ・小さすぎる文字が無いか（日本語→英語）
            MaxLayout(s, "ja");
            s.Add(("switch to English", () =>
            {
                _lang = Il2CppGameLocalization.LocManager.Code;
                Refs.Bag.ClosePage();
                Il2CppGameLocalization.LocManager.SetLanguage("en");
                Core.Log($"[test] language {_lang} -> {Il2CppGameLocalization.LocManager.Code}");
            }));
            Wait(s, 2);
            MaxLayout(s, "en");
            s.Add(("language back", () =>
            {
                Refs.Bag.ClosePage();
                Il2CppGameLocalization.LocManager.SetLanguage(string.IsNullOrEmpty(_lang) ? "ja" : _lang);
                Check($"language restored ({Il2CppGameLocalization.LocManager.Code})", Il2CppGameLocalization.LocManager.Code == (string.IsNullOrEmpty(_lang) ? "ja" : _lang));
            }));
            Wait(s, 2);
            return s;
        }
    }
}
