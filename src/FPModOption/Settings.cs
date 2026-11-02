using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MelonLoader;

namespace FPModOption
{
    // 設定画面の「MOD」タブに並べる Mod
    public sealed class ModEntry
    {
        public string Id, NameJa, NameEn, DescJa, DescEn;
        public string Parent;   // Mod の中の機能（親の Mod の下に並べる。親がオフなら機能もオフ）
        public string Name => Core.Ja ? NameJa : NameEn;
        public string Desc => Core.Ja ? DescJa : DescEn;
    }

    // Mod ごとのオン/オフ（UserData/FPModOption/settings.json）。
    //   切り替えはすぐ保存し、次の起動から反映する（起動中のオン/オフは起動時の値のまま）。
    //   初めての起動ではすべてオン。オフにしても各 Mod のデータ（ロール・熟練度・アタッチメント）は消さない
    public static class Settings
    {
        public static readonly ModEntry[] Known =
        {
            new() { Id = "FPRarity", NameJa = "レアリティmod", NameEn = "Rarity mod",
                    DescJa = "武器のレアリティ・武器熟練度・エンドレスのボスフロア。オンにすると、機能ごとにもオン/オフできる。",
                    DescEn = "Weapon rarity, weapon mastery and endless boss floors. When on, each feature can also be turned on or off." },
            new() { Id = "FPRarity.Rarity", Parent = "FPRarity", NameJa = "レアリティ", NameEn = "Rarity",
                    DescJa = "拾った武器にレアリティが付き、性能が変わる。オフの間はバニラの性能で、付いたレアリティはオンに戻すと戻る。",
                    DescEn = "Weapons you pick up get a rarity that changes their stats. While off, weapons use vanilla stats; their rarity comes back when turned on." },
            new() { Id = "FPRarity.Mastery", Parent = "FPRarity", NameJa = "武器熟練度", NameEn = "Weapon mastery",
                    DescJa = "敵を倒すと武器の熟練度が上がり、強化ポイントで武器を強くできる。オフの間はキルを数えず、強化も効かない。",
                    DescEn = "Kills level up your weapons and upgrade points make them stronger. While off, kills are not counted and upgrades do nothing." },
            new() { Id = "FPRarity.Boss", Parent = "FPRarity", NameJa = "ボスフロア", NameEn = "Boss floors",
                    DescJa = "エンドレスモードの階が、ときどきボス部屋になる。",
                    DescEn = "Endless mode floors are sometimes boss rooms." },
            new() { Id = "FPCustom", NameJa = "武器カスタマイズmod", NameEn = "Weapon customization mod",
                    DescJa = "壊れた武器部品でアタッチメントを解放し、保管庫で武器に付け外しできる。スライディングの調整も追加。",
                    DescEn = "Unlock attachments with broken weapon parts and fit them to your weapons at the stash. Also adds slide changes." },
            new() { Id = "FPSurvival", NameJa = "サバイバルモードmod", NameEn = "Survival mode mod",
                    DescJa = "エンドレス端末から、屋上で敵のウェーブを倒し続けるサバイバルモードを遊べる。途中でやめた回の預かり品は、オフにしても消えず、オンに戻して拠点に入ると返る。",
                    DescEn = "Play survival from the endless terminal: fight waves of enemies on the rooftop. Items kept from an unfinished run are not lost when turned off: turn it on and enter your room to get them back." },
            new() { Id = "FPOptimize", NameJa = "最適化mod", NameEn = "Optimization mod",
                    DescJa = "ゲームの動きは変えずに、無駄な処理を減らして動作を軽くする。",
                    DescEn = "Cuts wasted work to make the game run smoother without changing how it plays." },
        };

        static string FilePath => Path.Combine(Core.Dir, "settings.json");
        static Dictionary<string, bool> _saved = new();     // 保存してある値（次の起動で使う）
        static readonly Dictionary<string, bool> _atStart = new();   // この起動で使っている値
        static readonly HashSet<string> _testOff = new();   // 自動テストで一時的にオフにする Mod（保存しない）

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    _saved = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception e) { Core.Log("[settings] read failed (all mods on): " + e.Message); _saved = new(); }
            foreach (var m in Known) _atStart[m.Id] = !_saved.TryGetValue(m.Id, out var on) || on;
            foreach (var id in TestMode.ModsOff) { _testOff.Add(id); _atStart[id] = false; }
            Core.Log("[settings] " + string.Join(", ", Known.Select(m => $"{m.Id}={(Enabled(m.Id) ? "on" : "off")}{(Installed(m.Id) ? "" : "(not installed)")}"))
                     + (_testOff.Count > 0 ? $" (test off: {string.Join(",", _testOff)})" : ""));
        }

        // この起動でその Mod（機能）を動かすか（すべての Mod が止まっているときは false。機能は親の Mod がオフならオフ）
        public static bool Enabled(string id)
        {
            if (GameVersion.Disabled) return false;
            var parent = Known.FirstOrDefault(m => m.Id == id)?.Parent;
            if (parent != null && !Enabled(parent)) return false;
            return !_atStart.TryGetValue(id, out var on) || on;
        }

        // 次の起動での値（タブのスイッチの状態）
        public static bool Wanted(string id) => !_saved.TryGetValue(id, out var on) || on;

        public static void Set(string id, bool on)
        {
            _saved[id] = on;
            Save();
            Core.Log($"[settings] {id} -> {(on ? "on" : "off")} (after restart)");
        }

        // 起動し直すと変わるものがあるか
        public static bool RestartNeeded => Known.Any(m => !_testOff.Contains(m.Id) && Wanted(m.Id) != _atStart[m.Id]);

        // Mods フォルダに入っているか
        public static bool Installed(string id)
        {
            var parent = Known.FirstOrDefault(m => m.Id == id)?.Parent;
            if (parent != null) return Installed(parent);
            foreach (var m in MelonMod.RegisteredMelons) if (m.Info.Name == id) return true;
            if (TestMode.Active && TestMode.FakeInstalled.Contains(id)) return true;
            return false;
        }

        public static void Save()
        {
            try
            {
                var all = new Dictionary<string, bool>();
                foreach (var m in Known) all[m.Id] = Wanted(m.Id);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) { Core.Log("[settings] save failed: " + e.Message); }
        }
    }
}
