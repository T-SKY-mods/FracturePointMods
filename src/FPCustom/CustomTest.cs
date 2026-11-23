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

        public static void Register()
        {
            TestMode.ExternalScenarios["custui"] = Steps;
            TestMode.ExternalScenarios["custstockui"] = StockSteps;
        }

        // 追加のストック（VP70・ウッド・スケルトン）を、カスタマイズ画面の操作だけで解放・装着できるか
        //   G-18C（ハンマー無し）：ストックの枠に M93R・VP70・ウッド・スケルトンが並ぶ。Model 92（ハンマーあり）：VP70 は出ない
        static List<(string, Action)> StockSteps()
        {
            const int glock = 13, m92 = 7, row = 1;   // ハンドガンの枠：0 = マズル、1 = ストック
            var names = new[] { "p_poly_stock", "p_wood_stock", "p_frame_stock" };
            int before = 0;
            var s = new List<(string, Action)>();
            s.Add(("setup", () => { before = Parts.Count(); Refs.Inv.AddLootItem(Parts.ItemId, Store.UnlockCost * 4, false); }));
            s.Add(("open stash", () => Refs.Bag.Interact()));
            Wait(s);
            s.Add(("open customize", () => CustomUi.OpenButtonForTest?.GetComponent<UnityEngine.UI.Button>()?.onClick.Invoke()));
            Wait(s);
            s.Add(("select G-18C", () =>
            {
                Check("customize page open", CustomUi.IsOpen);
                CustomUi.SelectForTest(glock);
                var ids = Catalog.For(glock, Cat.Stock).Select(a => a.Id).ToList();
                Check($"G-18C stock options [{string.Join(", ", ids)}]", ids.SequenceEqual(new[] { "p_stock", "p_poly_stock", "p_wood_stock", "p_frame_stock" }));
            }));
            int used = 0;
            foreach (var id in names)
            {
                var att = id;
                s.Add(($"attach {att}", () =>
                {
                    CustomUi.BrowseToForTest(row, att);
                    var a = Catalog.Get(att);
                    string shown = CustomUi.RowForTest(row);
                    Check($"{att}: shown on the stock row ({shown})", shown.Contains(a.Name(Core.Ja)));
                    int parts0 = Parts.Count();
                    CustomUi.ClickActForTest(row);
                    used += parts0 - Parts.Count();
                    var dw = Tracker.ItemById(glock)?.Weapon;
                    Check($"{att}: unlocked with {parts0 - Parts.Count()} parts and attached ({Store.Describe(glock)})", Store.Equipped(glock, Cat.Stock)?.Id == att && parts0 - Parts.Count() == Store.UnlockCost);
                    var bad = CustomUi.LayoutProblemsForTest(10f);
                    Check($"{att}: page layout ok ({bad})", string.IsNullOrEmpty(bad));
                }));
                Wait(s);
                s.Add(($"shot {att}", () =>
                {
                    // 画面の文字は次のフレームで変わるので、ここで読む。選択中＝装着中のストックの効果
                    var fx = CustomUi.EffectsForTest;
                    var key = att == "p_poly_stock" ? "3点バースト" : att == "p_wood_stock" ? "しゃがみ中コントロール+15" : "ポイントエイム時コントロール+20";
                    Check($"{att}: effects shown ({key}) and listed as equipped", fx.Contains(key) && fx.Contains(Catalog.Get(att).Name(Core.Ja)));
                    Shot("stockui_" + att);
                }));
                Wait(s);
            }
            s.Add(("switch back and remove", () =>
            {
                // 解放済みの物への付け替えは部品を使わない。外すこともできる
                int parts0 = Parts.Count();
                CustomUi.BrowseToForTest(row, "p_wood_stock");
                CustomUi.ClickActForTest(row);
                Check($"switch to an unlocked stock costs nothing ({Store.Describe(glock)})", Store.Equipped(glock, Cat.Stock)?.Id == "p_wood_stock" && Parts.Count() == parts0);
                CustomUi.ClickActForTest(row);
                Check($"stock removed ({Store.Describe(glock)})", Store.Equipped(glock, Cat.Stock) == null);
            }));
            s.Add(("Model 92", () =>
            {
                CustomUi.SelectForTest(m92);
                var ids = Catalog.For(m92, Cat.Stock).Select(a => a.Id).ToList();
                Check($"Model 92 stock options [{string.Join(", ", ids)}] (no VP70: it has a hammer)", ids.SequenceEqual(new[] { "p_stock", "p_wood_stock", "p_frame_stock" }));
                CustomUi.BrowseToForTest(row, "p_frame_stock");
                int parts0 = Parts.Count();
                CustomUi.ClickActForTest(row);
                // 解放は武器ごと：Model 92 では改めて部品を使う
                Check($"Model 92: skeleton stock unlocked with {parts0 - Parts.Count()} parts and attached ({Store.Describe(m92)})", Store.Equipped(m92, Cat.Stock)?.Id == "p_frame_stock" && parts0 - Parts.Count() == Store.UnlockCost);
            }));
            Wait(s);
            s.Add(("shot Model 92", () => Shot("stockui_m92_frame")));
            Wait(s);
            return s;
        }

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
                for (int r = 0; r < Catalog.CatsFor(_rifle).Length; r++) Core.Log($"[test] AK-12 row {r}: {CustomUi.RowForTest(r)}");
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
            s.Add(("rifle stock on AK-12", () =>
            {
                CustomUi.SelectForTest(_rifle);
                int row = Array.IndexOf(Catalog.CatsFor(_rifle), Cat.Stock);
                Check($"AK-12 has a stock row with the 4 stocks (row {row}: {string.Join(",", Catalog.For(_rifle, Cat.Stock).Select(a => a.Id))})", row == 4 && Catalog.For(_rifle, Cat.Stock).Count == 4);
                Check("AKMS / Hunter-12 / Beater have no stock row", new[] { 4, 19, 29 }.All(i => !Catalog.CatsFor(i).Contains(Cat.Stock)));
                Store.UnlockForTest(_rifle, "stock_prs");
                CustomUi.BrowseToForTest(row, "stock_prs");
                CustomUi.ClickActForTest(row);
                Core.Log($"[test] AK-12 stock: {CustomUi.RowForTest(row)} / {CustomUi.EffectsForTest.Replace("\n", " / ")}");
                Check($"PRS stock attached ({Store.Describe(_rifle)})", Store.Equipped(_rifle, Cat.Stock)?.Id == "stock_prs");
                var bad = CustomUi.LayoutProblemsForTest(18f);
                Check($"6-row layout fits ({bad})", bad.Length == 0);
            }));
            Wait(s);
            s.Add(("rifle stock shot", () =>
            {
                Shot("cust_1b_ak12_stock");
                Preview.Save(Path.Combine(ShotDir, "cust_1b_ak12_stock_view.png"));
                Store.SetForTest(_rifle, Cat.Stock, null);
                Store.UnlockForTest(_rifle, "stock_prs", false);
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
                // コントロールの値は表示の単位（StatScale）に直した値（以前の +5 → 今は +2）
                string ctl = "+" + Catalog.Get("ext_mag").Effects.First(e => e.Item1 == FPModOption.WeaponStat.Control).Item2.ToString("0");
                Check($"effects: picked part split into pros (control {ctl}, capacity) and cons (loading)",
                      plusCol.Contains(ctl) && plusCol.Contains(N(mag[0])) && minusCol.Contains(N(mag[1])) && !minusCol.Contains(ctl));
                Check("effects: every equipped part listed with all its effects (suppressor + extended mag)",
                      eqHead.Contains(sup.Name(Core.Ja)) && eqHead.Contains(Catalog.Get("ext_mag").Name(Core.Ja))
                      && eqPlus.Contains(ctl) && eqPlus.Contains(N(mag[0])) && sup.Notes.Where(n => n.Plus).All(n => eqPlus.Contains(N(n)))
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
                for (int r = 0; r < Catalog.CatsFor(_pistol).Length; r++) Core.Log($"[test] SP-320 row {r}: {CustomUi.RowForTest(r)}");
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
