using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FPModOption;

namespace FPCustom
{
    // 武器の種類（WeaponId）ごとの解放・装着。レアリティ違いは共通。UserData/FPCustom/custom.json に保存し、ゲームのセーブには書かない
    public static class Store
    {
        public sealed class Entry
        {
            public List<string> Unlocked { get; set; } = new();
            public Dictionary<string, string> Equipped { get; set; } = new();   // カテゴリ → アタッチメント（"" は外している）。無いカテゴリは最初の状態
        }

        public const int UnlockCost = 20;
        static Dictionary<int, Entry> _data = new();
        public static int Version;   // 付け外し・読み込みのたびに増える（効果の合計の覚えを捨てる目印）
        static string FilePath => Path.Combine(Main.Dir, "custom.json");

        public static void Load()
        {
            try
            {
                Version++;
                if (File.Exists(FilePath)) _data = JsonSerializer.Deserialize<Dictionary<int, Entry>>(File.ReadAllText(FilePath)) ?? new();
                Main.Log($"[store] loaded {_data.Count} weapons");
            }
            catch (Exception e) { Main.Log("[store] load failed: " + e.Message); _data = new(); }
        }

        static void Save()
        {
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
                File.Copy(tmp, FilePath, true);
                File.Delete(tmp);
            }
            catch (Exception e) { Main.Log("[store] save failed: " + e.Message); }
        }

        static Entry Get(int id, bool create)
        {
            if (_data.TryGetValue(id, out var e)) return e;
            if (!create) return null;
            return _data[id] = new Entry();
        }

        public static bool CanEdit => Core.Scene == Core.HubScene;

        public static bool Unlocked(int id, Att a) => a != null && (DebugMode.On || Catalog.IsFactory(a, id) || (Get(id, false)?.Unlocked.Contains(a.Id) ?? false));

        public static Att Equipped(int id, Cat cat)
        {
            var e = Get(id, false);
            if (e != null && e.Equipped.TryGetValue(cat.ToString(), out var v)) return string.IsNullOrEmpty(v) ? null : Catalog.Get(v);
            return Catalog.For(id, cat).FirstOrDefault(a => Catalog.IsFactory(a, id));   // 最初から付いている部品
        }

        public static int UnlockedCount(int id) => Catalog.CatsFor(id).Sum(c => Catalog.For(id, c).Count(a => Unlocked(id, a)));
        public static int TotalCount(int id) => Catalog.CatsFor(id).Sum(c => Catalog.For(id, c).Count);

        public static bool Unlock(int id, Att a)
        {
            if (a == null || !CanEdit || Unlocked(id, a)) return false;
            if (!Parts.Take(UnlockCost)) return false;
            Get(id, true).Unlocked.Add(a.Id);
            Save();
            Main.Log($"[store] unlocked {a.Id} for {Tracker.NameOf(id)} (parts left {Parts.Count()})");
            return true;
        }

        public static bool Equip(int id, Cat cat, Att a)
        {
            if (!CanEdit || (a != null && (!Unlocked(id, a) || a.Cat != cat)) || (a == null && !Catalog.CanRemove(id, cat))) return false;
            Get(id, true).Equipped[cat.ToString()] = a?.Id ?? "";
            Version++;
            Save();
            Main.Log($"[store] {Tracker.NameOf(id)} {cat} = {a?.Id ?? "none"}");
            return true;
        }

        // テスト用：解放・部屋の確認なしで装着状態を決める（null = 最初の状態に戻す、"" = 外す）
        internal static void SetForTest(int id, Cat cat, string attId)
        {
            Version++;
            if (attId == null) Get(id, true).Equipped.Remove(cat.ToString());
            else Get(id, true).Equipped[cat.ToString()] = attId;
        }

        public static string Describe(int id) => string.Join(", ", Catalog.CatsFor(id).Select(c => $"{c}={Equipped(id, c)?.Id ?? "none"}"));
    }
}
