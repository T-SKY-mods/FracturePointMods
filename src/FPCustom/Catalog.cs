using System.Collections.Generic;
using System.Linq;
using FPModOption;
using Il2Cpp;

namespace FPCustom
{
    public enum Cat { Muzzle, Grip, Sight, SideRail, Mag, Stock }

    // 見た目の出どころ。ゲームで実際に使われている部品（Vanilla）だけを流用し、無いものは無料素材（Asset）で作る
    public enum Source { Vanilla, Asset, StatsOnly }

    public sealed class Att
    {
        public string Id, Ja, En;
        public Cat Cat;
        public bool Pistol;
        public Source Source;
        public bool OnlyFactory;               // 最初から付いている武器だけ（Swiss のスコープ：他の武器には付けない）
        public int[] Factory = new int[0];     // 最初から付いている武器（ゲームのまま）。この武器では最初から解放済み・装着済み
        public string Look;                    // 見た目を借りる部品（色違いのレーザー：形は Look のもの。最初から付いている武器ではその部品を見せる）
        public UnityEngine.Color? Beam;        // レーザーの光線の色（null＝ゲームの赤のまま）
        // 付けたときの効果（プラス・マイナス）。値の単位は熟練度と同じ（ダメージ・連射＝%、コントロール・エルゴ＝ポイント）。
        // 数値にならない効果は Notes（Plus＝プラスの効果）。中身は Effects.Define
        public readonly List<(WeaponStat Stat, float Value)> Effects = new();
        public readonly List<(string Ja, string En, bool Plus)> Notes = new();
        public string Name(bool ja) => ja ? Ja : En;
    }

    // アタッチメントの一覧。種類を増やすときはここに足す
    public static class Catalog
    {
        public const int RpgId = 26;
        // 銃口の部品を付けない武器：VAL は銃身そのものがサプレッサー、Hunter-32 は銃口の部品が太くてかぶせられない
        public static readonly int[] NoMuzzle = { 14 };

        // レーザーの色違い（色相だけ。明るさ・濃さはゲームの赤の光線のまま）
        public static readonly UnityEngine.Color BlueBeam = new(0.10f, 0.35f, 1f), GreenBeam = new(0.15f, 1f, 0.20f);

        public static readonly List<Att> All = new()
        {
            // ---- メイン武器 ----
            new Att { Id = "suppressor",    Cat = Cat.Muzzle,   Ja = "サプレッサー",          En = "Suppressor",       Source = Source.Asset },
            new Att { Id = "muzzle_brake",  Cat = Cat.Muzzle,   Ja = "マズルブレーキ",        En = "Muzzle brake",     Source = Source.Vanilla, Factory = new[] { 2, 21 } },
            new Att { Id = "arrowhead",     Cat = Cat.Muzzle,   Ja = "アローヘッドブレーキ",  En = "Arrowhead brake",  Source = Source.Asset },
            new Att { Id = "flash_cone",       Cat = Cat.Muzzle,   Ja = "フラッシュコーン",      En = "Flash cone",       Source = Source.Asset },
            new Att { Id = "flash_hider",   Cat = Cat.Muzzle,   Ja = "フラッシュハイダー",    En = "Flash hider",      Source = Source.Asset },
            new Att { Id = "vertical_grip", Cat = Cat.Grip,     Ja = "バーティカルグリップ",  En = "Vertical grip",    Source = Source.Vanilla, Factory = new[] { 2, 28, 18, 15 } },
            new Att { Id = "angled_grip",   Cat = Cat.Grip,     Ja = "アングルフォアグリップ", En = "Angled foregrip", Source = Source.Asset },
            new Att { Id = "bipod",         Cat = Cat.Grip,     Ja = "バイポッド",            En = "Bipod",            Source = Source.Asset },
            new Att { Id = "kac_grip",    Cat = Cat.Grip,     Ja = "KACグリップ",           En = "KAC grip",         Source = Source.Asset },
            new Att { Id = "stubby_grip",           Cat = Cat.Grip,     Ja = "スタビーグリップ",      En = "Stubby grip",      Source = Source.Asset },
            new Att { Id = "holo",          Cat = Cat.Sight,    Ja = "ホロサイト",            En = "Holo sight",       Source = Source.Vanilla, Factory = new[] { 2, 15, 28 } },
            new Att { Id = "reddot",        Cat = Cat.Sight,    Ja = "ドクターサイト",        En = "Docter sight",    Source = Source.Vanilla, Factory = new[] { 18 }, OnlyFactory = true },   // 他の武器に載せると照準の模様が出ないので Hunter-32 だけ
            new Att { Id = "ace_sight",     Cat = Cat.Sight,    Ja = "シールド RMS",          En = "Shield RMS",        Source = Source.Vanilla, Factory = new[] { 16 } },
            new Att { Id = "ak_sight",      Cat = Cat.Sight,    Ja = "OKP-7リフレックスサイト", En = "OKP-7 reflex sight",    Source = Source.Vanilla, Factory = new[] { 12, 14 } },
            new Att { Id = "swiss_scope",   Cat = Cat.Sight,    Ja = "ケルン 4倍スコープ",    En = "Kern 4x scope",            Source = Source.Vanilla, Factory = new[] { 17 }, OnlyFactory = true },
            new Att { Id = "laser",         Cat = Cat.SideRail, Ja = "レーザーサイト(赤)",    En = "Laser sight (red)",   Source = Source.Vanilla, Factory = new[] { 2, 21, 28 } },
            new Att { Id = "laser_blue",    Cat = Cat.SideRail, Ja = "レーザーサイト(青)",    En = "Laser sight (blue)",  Source = Source.Vanilla, Look = "laser", Beam = BlueBeam },
            new Att { Id = "laser_green",   Cat = Cat.SideRail, Ja = "レーザーサイト(緑)",    En = "Laser sight (green)", Source = Source.Vanilla, Look = "laser", Beam = GreenBeam },
            new Att { Id = "ext_mag",       Cat = Cat.Mag,      Ja = "拡張マガジン",          En = "Extended mag",     Source = Source.StatsOnly },
            new Att { Id = "fast_mag",      Cat = Cat.Mag,      Ja = "ファストマグ",          En = "Fast mag",         Source = Source.StatsOnly },

            // ---- サブ武器（ハンドガン） ----
            new Att { Id = "p_suppressor",  Cat = Cat.Muzzle,   Ja = "サプレッサー",          En = "Suppressor",       Source = Source.Vanilla, Factory = new[] { 23 }, Pistol = true },
            new Att { Id = "p_compensator", Cat = Cat.Muzzle,   Ja = "コンペンセイター",      En = "Compensator",      Source = Source.Asset, Pistol = true },
            new Att { Id = "p_arrowhead",   Cat = Cat.Muzzle,   Ja = "アローヘッドブレーキ",  En = "Arrowhead brake",  Source = Source.Asset, Pistol = true },
            new Att { Id = "p_linear_comp",     Cat = Cat.Muzzle,   Ja = "リニアコンペンセイター", En = "Linear compensator", Source = Source.Asset, Pistol = true },
            new Att { Id = "p_flash_hider", Cat = Cat.Muzzle,   Ja = "フラッシュハイダー",    En = "Flash hider",      Source = Source.Asset, Pistol = true },
            new Att { Id = "p_stock",       Cat = Cat.Stock,    Ja = "M93Rストック",         En = "M93R stock",       Source = Source.Asset, Pistol = true },
            new Att { Id = "p_dot",         Cat = Cat.Sight,    Ja = "RMR",                  En = "RMR",    Source = Source.Vanilla, Factory = new[] { 9 }, Pistol = true },
            new Att { Id = "p_laser",       Cat = Cat.SideRail, Ja = "レーザーサイト(赤)",    En = "Laser sight (red)",   Source = Source.Vanilla, Factory = new[] { 7, 9 }, Pistol = true },
            new Att { Id = "p_laser_blue",  Cat = Cat.SideRail, Ja = "レーザーサイト(青)",    En = "Laser sight (blue)",  Source = Source.Vanilla, Look = "p_laser", Beam = BlueBeam, Pistol = true },
            new Att { Id = "p_laser_green", Cat = Cat.SideRail, Ja = "レーザーサイト(緑)",    En = "Laser sight (green)", Source = Source.Vanilla, Look = "p_laser", Beam = GreenBeam, Pistol = true },
            new Att { Id = "p_ext_mag",     Cat = Cat.Mag,      Ja = "拡張マガジン",          En = "Extended mag",     Source = Source.StatsOnly, Pistol = true },
            new Att { Id = "p_fast_mag",    Cat = Cat.Mag,      Ja = "ファストマグ",          En = "Fast mag",         Source = Source.StatsOnly, Pistol = true },
        };

        public static readonly Cat[] RifleCats = { Cat.Muzzle, Cat.Grip, Cat.Sight, Cat.SideRail, Cat.Mag };
        public static readonly Cat[] PistolCats = { Cat.Muzzle, Cat.Stock, Cat.Sight, Cat.SideRail, Cat.Mag };

        public static Att Get(string id) => All.FirstOrDefault(a => a.Id == id);

        public static bool IsPistol(int weaponId) => Tracker.ItemById(weaponId)?.Type == WeaponItem.SlotType.Small;

        public static bool Eligible(WeaponItem wi) => wi != null && wi.Id != RpgId;

        public static Cat[] CatsFor(int weaponId) => IsPistol(weaponId) ? PistolCats : RifleCats;

        // その武器に付けられるもの（見た目の確認が済むまでは、メイン武器用・ハンドガン用の全部）
        public static List<Att> For(int weaponId, Cat cat)
        {
            bool pistol = IsPistol(weaponId);
            if (cat == Cat.Muzzle && NoMuzzle.Contains(weaponId)) return new List<Att>();
            return All.Where(a => a.Cat == cat && a.Pistol == pistol && (!a.OnlyFactory || IsFactory(a, weaponId)) && (IsFactory(a, weaponId) || cat switch { Cat.Grip => Grips.Supported(weaponId, a), Cat.Sight => Optics.Supported(weaponId, a), _ => AssetParts.Supported(weaponId, a) })).ToList();
        }

        // 「なし」にできるか。アイアンサイトの無い武器（ACE-31・Hunter-32）はサイトを外すと狙えないので外せない（別のサイトへの付け替えはできる）
        public static bool CanRemove(int weaponId, Cat cat) => cat != Cat.Sight || Sights.HasIron(weaponId);

        public static bool IsFactory(Att a, int weaponId) => a != null && a.Factory.Contains(weaponId);

        // 見た目の部品（色違いのレーザーは元のレーザー）
        public static Att LookOf(Att a) => a?.Look != null ? Get(a.Look) ?? a : a;
        // 最初から付いている部品をそのまま見せるか（色違いも、形が同じなら最初の部品を使う）
        public static bool ShowsFactory(Att a, int weaponId) => IsFactory(LookOf(a), weaponId);

        public static string CatName(Cat c, bool ja) => c switch
        {
            Cat.Muzzle => ja ? "マズル" : "Muzzle",
            Cat.Grip => ja ? "グリップ" : "Grip",
            Cat.Sight => ja ? "サイト" : "Sight",
            Cat.SideRail => ja ? "サイドレール" : "Side rail",
            Cat.Mag => ja ? "マガジン" : "Magazine",
            Cat.Stock => ja ? "ストック" : "Stock",
            _ => c.ToString(),
        };
    }
}
