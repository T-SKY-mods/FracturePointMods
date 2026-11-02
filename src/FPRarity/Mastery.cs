using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityEngine;

using FPModOption;
using Stat = FPModOption.WeaponStat;

namespace FPRarity
{
    // 武器熟練度。武器の種類（WeaponId）ごとに記録するので、同じ武器ならレアリティ違いでも共通で育つ。
    //
    // ・敵を倒すと、そのとき手に持っていた武器のキル数が 1 増える（キル数 = 経験値）
    // ・Lv1〜100。序盤はすぐ上がり、後半ほど上がりにくい（Lv100 で累計 1500 キル）
    // ・Lv20 ごとに強化ポイント +1（最大 5）。ダメージ／連射／コントロール／エルゴに自由に振れる
    // ・振り直しは部屋（拠点）でいつでも無料。ラン中は変更できない
    //
    // 保存先は UserData/FPRarity/mastery.json（ゲームのセーブ・state.json とは別）。
    // キルはラン中に死んでも取り消されないので、ゲームのセーブを待たずに保存する。
    public static class Mastery
    {
        public const int MaxLevel = 100;
        public const int LevelsPerPoint = 10;   // 10レベルごとに1ポイント（最大10ポイント）
        public const int MaxPoints = MaxLevel / LevelsPerPoint;
        const float KillsForMax = 1500f;
        const float CurveExp = 1.5f;

        // 1ポイントあたりの強化量
        // （ポイントは最大10。1つの能力に全部振ると ダメージ+10% / 連射+10%（ハンドガンはリロード速度+20%）/ コントロール+25 / エルゴ+20）
        public const float DamagePerPoint = 0.01f;   // +1%
        public const float FireRatePerPoint = 0.01f; // +1%
        public const float ReloadPerPoint = 0.02f;   // ハンドガンは「連射」の枠がリロード速度 +2%
        public const float ControlPerPoint = 1f;   // 反動 約 -3%（StatScale）。反動の大きい武器は熟練度で扱いやすくする想定
        public const float ErgoPerPoint = 2f;

        // 性能の種類は共通の WeaponStat（Mod 間で共有）
        public static readonly Stat[] Stats = { Stat.Damage, Stat.FireRate, Stat.Control, Stat.Ergo };

        public class Entry
        {
            public int Kills { get; set; }
            public int[] Points { get; set; } = new int[4];
            public bool Sight { get; set; }   // ホロサイトを付けるか（Lv10 で解放、部屋で ON/OFF）
        }

        class SaveFile
        {
            public int Version { get; set; } = 1;
            public Dictionary<int, Entry> Weapons { get; set; } = new();
        }

        const int RpgId = 26; // ロケットランチャーは性能補正の対象外なので熟練度もなし

        static Dictionary<int, Entry> _data = new();
        static bool _dirty;
        static float _flushAt;
        static readonly HashSet<IntPtr> _killed = new();

        static string PathOf => Path.Combine(Main.Dir, "mastery.json");
        static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

        // ---------- レベル計算 ----------

        public static bool Eligible(int weaponId) => weaponId >= 0 && weaponId != RpgId;

        // そのレベルに必要な累計キル数
        public static int KillsForLevel(int level)
        {
            if (level <= 1) return 0;
            level = Math.Min(level, MaxLevel);
            return (int)MathF.Ceiling(KillsForMax * MathF.Pow((level - 1) / (float)(MaxLevel - 1), CurveExp));
        }

        public static int LevelOf(int kills)
        {
            int lv = 1;
            while (lv < MaxLevel && kills >= KillsForLevel(lv + 1)) lv++;
            return lv;
        }

        public static Entry Get(int weaponId) => _data.TryGetValue(weaponId, out var e) ? e : null;
        public static int Level(int weaponId) => LevelOf(Get(weaponId)?.Kills ?? 0);
        public static int PointsEarned(int weaponId) => Eligible(weaponId) ? Level(weaponId) / LevelsPerPoint : 0;
        public static int PointsUsed(int weaponId) => Get(weaponId)?.Points.Sum() ?? 0;
        // 熟練度がオフの間は強化が効かない（振ったポイントの記録は残す）
        public static int PointsIn(int weaponId, Stat s) => Features.Mastery ? Get(weaponId)?.Points[(int)s] ?? 0 : 0;

        // ---------- 性能（ロールの補正 + 熟練度） ----------

        static WeaponRoll Up(WeaponRoll r) { if (!Features.Rarity) return Features.View(r); RarityTable.Upgrade(r); return r; }   // 古いバランスの武器は今の計算に置き換えてから使う
        public static float DamageMult(WeaponRoll r) => r == null ? 1f : Up(r).DamageMult * (1f + DamagePerPoint * PointsIn(r.WeaponId, Stat.Damage));
        public static float FireRateMult(WeaponRoll r) => r == null ? 1f : IsHandgun(r.WeaponId) ? Up(r).FireRateMult : Up(r).FireRateMult * (1f + FireRatePerPoint * PointsIn(r.WeaponId, Stat.FireRate));
        // リロードの速さ（ハンドガンだけ。ロールの補正 × 熟練度）
        public static float ReloadMult(WeaponRoll r) => r == null || !IsHandgun(r.WeaponId) ? 1f : Up(r).ReloadMult * (1f + ReloadPerPoint * PointsIn(r.WeaponId, Stat.FireRate));
        public static bool IsHandgun(int weaponId) => RarityTable.IsHandgun(Tracker.ItemById(weaponId));
        // 反動の計算用（小数のまま）と、表示用（整数に丸める）
        public static float ControlDeltaF(WeaponRoll r) => r == null ? 0f : Up(r).ControlDelta + ControlPerPoint * PointsIn(r.WeaponId, Stat.Control);
        public static float ErgoDeltaF(WeaponRoll r) => r == null ? 0f : Up(r).ErgoDelta + ErgoPerPoint * PointsIn(r.WeaponId, Stat.Ergo);
        public static int ControlDelta(WeaponRoll r) => (int)MathF.Round(ControlDeltaF(r), MidpointRounding.AwayFromZero);
        public static int ErgoDelta(WeaponRoll r) => (int)MathF.Round(ErgoDeltaF(r), MidpointRounding.AwayFromZero);
        // 表示用：ほかの Mod（FPCustom のアタッチメント）が足す値。武器 ID → (コントロール, エルゴ)、連射（元の値 → 表示する値）
        static (float Control, float Ergo) Extra(WeaponRoll r) { try { return r != null && Link.ExtraDelta != null ? Link.ExtraDelta(r.WeaponId) : default; } catch { return default; } }
        public static int ShownControlDelta(WeaponRoll r) => (int)MathF.Round(ControlDeltaF(r) + Extra(r).Control, MidpointRounding.AwayFromZero);
        public static int ShownErgoDelta(WeaponRoll r) => (int)MathF.Round(ErgoDeltaF(r) + Extra(r).Ergo, MidpointRounding.AwayFromZero);
        public static float ShownRpm(WeaponRoll r, float rpm) { try { return r != null && Link.RpmShown != null ? Link.RpmShown(r.WeaponId, rpm) : rpm; } catch { return rpm; } }
        public static float ShownDamage(WeaponRoll r, float dmg) { try { return r != null && Link.DamageShown != null ? Link.DamageShown(r.WeaponId, dmg) : dmg; } catch { return dmg; } }
        // ---------- ホロサイト（Lv10 で解放） ----------

        public const int SightLevel = 10;
        // 武器カスタマイズmod が入っているときは、サイトはカスタマイズで付ける（熟練度のサイトは出さない。設定は残す）
        public static bool SightFeature => !Link.CustomSights;
        public static bool SightSupported(int weaponId) => SightFeature && Eligible(weaponId) && SightMount.Supported(weaponId);
        public static bool SightUnlocked(int weaponId) => SightSupported(weaponId) && Level(weaponId) >= SightLevel;
        public static bool SightOn(int weaponId) => Features.Mastery && SightUnlocked(weaponId) && (Get(weaponId)?.Sight ?? false);

        public static bool SetSight(int weaponId, bool on)
        {
            if (!CanEdit || !SightUnlocked(weaponId) || SightOn(weaponId) == on) return false;
            GetOrCreate(weaponId).Sight = on;
            Main.Log($"[mastery] sight {(on ? "ON" : "OFF")} {Tracker.NameOf(weaponId)}");
            Changed();
            return true;
        }

        public static bool HasRecoilBonus(WeaponRoll r) => r != null && (PointsIn(r.WeaponId, Stat.Control) > 0 || PointsIn(r.WeaponId, Stat.Ergo) > 0);

        // ---------- 強化の変更（部屋でのみ） ----------

        public static bool CanEdit => Main.Scene == Main.HubScene;

        public static bool AddPoint(int weaponId, Stat s)
        {
            if (!CanEdit || !Eligible(weaponId) || PointsUsed(weaponId) >= PointsEarned(weaponId)) return false;
            var e = GetOrCreate(weaponId);
            e.Points[(int)s]++;
            Main.Log($"[mastery] +1 {s} {Tracker.NameOf(weaponId)} -> [{string.Join(",", e.Points)}]");
            Changed();
            return true;
        }

        public static bool Reset(int weaponId)
        {
            var e = Get(weaponId);
            if (!CanEdit || e == null || e.Points.Sum() == 0) return false;
            e.Points = new int[4];
            Main.Log($"[mastery] reset {Tracker.NameOf(weaponId)}");
            Changed();
            return true;
        }

        // 強化を変えたら、手持ちの武器に反映して即保存
        static void Changed()
        {
            _dirty = true;
            Flush();
            try
            {
                Tracker.ApplyAll();
                Tracker.ReinitActiveRecoil();
            }
            catch (Exception ex) { Main.Log("[mastery] apply failed: " + ex.Message); }
        }

        static Entry GetOrCreate(int weaponId)
        {
            if (!_data.TryGetValue(weaponId, out var e)) _data[weaponId] = e = new Entry();
            if (e.Points == null || e.Points.Length != 4) e.Points = new int[4];
            return e;
        }

        // ---------- キル ----------

        public static void OnEnemyKilled(Il2CppEnemyAI.EnemyHealth enemy)
        {
            if (!Features.Mastery) return;   // 熟練度がオフの間はキルを数えない
            if (enemy == null || !_killed.Add(enemy.Pointer)) return; // 同じ敵を二重に数えない
            var roll = Tracker.ActiveRoll(out var item);
            int id = item?.Id ?? roll?.WeaponId ?? -1;
            if (!Eligible(id)) { Main.Log($"[mastery] kill not counted (active weapon id={id})"); return; }
            var e = GetOrCreate(id);
            int before = LevelOf(e.Kills);
            e.Kills++;
            int after = LevelOf(e.Kills);
            _dirty = true;
            Main.Log($"[mastery] kill {Tracker.NameOf(id)} kills={e.Kills} Lv{after}");
            if (after > before) Announce(id, after);
        }

        static void Announce(int weaponId, int level)
        {
            bool point = level % LevelsPerPoint == 0;
            if (level == SightLevel && SightSupported(weaponId))
            {
                var t = UiPatches.Japanese ? $"{Tracker.NameOf(weaponId)} 熟練度 Lv.{level}  {SightMount.SightName(weaponId, true)}解放！{(point ? "＋強化ポイント獲得！" : "")}（部屋の保管庫で）"
                                           : $"{Tracker.NameOf(weaponId)} Mastery Lv.{level}  {SightMount.SightName(weaponId, false)} unlocked!{(point ? " + upgrade point!" : "")} (at the stash)";
                Main.Log("[mastery] " + t);
                try { Refs.Inv?.NotificationsManager?.NotifyWithColor(t, new Color(1f, 0.85f, 0.4f)); } catch { }
                return;
            }
            var name = Tracker.NameOf(weaponId);
            var text = UiPatches.Japanese
                ? $"{name} 熟練度 Lv.{level}{(point ? "  強化ポイント獲得！（部屋の保管庫で強化）" : "")}"
                : $"{name} Mastery Lv.{level}{(point ? "  Upgrade point earned! (use it at the stash)" : "")}";
            Main.Log("[mastery] " + text);
            // 1キルごとに通知が出ると邪魔なので、5レベルごとと強化ポイント獲得時だけ通知する
            if (!point && level % 5 != 0) return;
            try { Refs.Inv?.NotificationsManager?.NotifyWithColor(text, new Color(1f, 0.85f, 0.4f)); }
            catch (Exception ex) { Main.Log("[mastery] notify failed: " + ex.Message); }
        }

        public static void OnSceneChanged()
        {
            _killed.Clear();
            Flush();
        }

        // ---------- 保存 ----------

        public static void Load()
        {
            try
            {
                if (!File.Exists(PathOf)) { Main.Log("[mastery] no mastery.json (fresh)"); return; }
                var s = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(PathOf), Opts);
                _data = s?.Weapons ?? new();
                foreach (var e in _data.Values)
                    if (e.Points == null || e.Points.Length != 4) e.Points = new int[4];
                Main.Log($"[mastery] loaded {_data.Count} weapon(s): " +
                         string.Join(", ", _data.Select(kv => $"{kv.Key}:{kv.Value.Kills}k[{string.Join(",", kv.Value.Points)}]")));
            }
            catch (Exception e)
            {
                Main.Log("[mastery] load failed: " + e.Message);
                try { File.Move(PathOf, PathOf + $".broken_{DateTime.Now:yyyyMMdd_HHmmss}"); } catch { }
            }
        }

        // Main.OnUpdate から呼ぶ。キルは数秒ごとにまとめて保存する
        public static void Tick()
        {
            if (!_dirty || Time.realtimeSinceStartup < _flushAt) return;
            Flush();
        }

        public static void Flush()
        {
            if (!_dirty) return;
            _dirty = false;
            _flushAt = Time.realtimeSinceStartup + 5f;
            try
            {
                var tmp = PathOf + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new SaveFile { Weapons = _data }, Opts));
                File.Copy(tmp, PathOf, true);
                File.Delete(tmp);
            }
            catch (Exception e) { Main.Log("[mastery] save failed: " + e.Message); _dirty = true; }
        }

        // テスト用：記録を差し替える（autotest のみ）
        public static void SetKillsForTest(int weaponId, int kills) { GetOrCreate(weaponId).Kills = kills; _dirty = true; }

        // 表示用の短い要約（例: "熟練度 Lv.47  ダメージ+6%  コントロール+4"）
        public static string Summary(int weaponId)
        {
            if (!Eligible(weaponId)) return "";
            bool ja = UiPatches.Japanese;
            var parts = new List<string> { ja ? $"熟練度 Lv.{Level(weaponId)}" : $"Mastery Lv.{Level(weaponId)}" };
            if (SightOn(weaponId)) parts.Add(SightMount.SightName(weaponId, ja));
            foreach (var s in Stats)
            {
                int p = PointsIn(weaponId, s);
                if (p > 0) parts.Add($"{StatName(s, weaponId)}{BonusText(s, p, weaponId)}");
            }
            return string.Join("  ", parts);
        }

        // インベントリの詳細欄用：列の幅に収まるよう「Lv」「強化2つずつ」で改行する
        public static string DetailLines(int weaponId)
        {
            if (!Eligible(weaponId)) return "";
            var lines = new List<string> { (UiPatches.Japanese ? $"熟練度 Lv.{Level(weaponId)}" : $"Mastery Lv.{Level(weaponId)}") +
                                           (SightOn(weaponId) ? "  " + SightMount.SightName(weaponId, UiPatches.Japanese) : "") };
            var bonus = new List<string>();
            foreach (var s in Stats)
            {
                int p = PointsIn(weaponId, s);
                if (p > 0) bonus.Add($"{StatName(s, weaponId)}{BonusText(s, p, weaponId)}");
            }
            for (int i = 0; i < bonus.Count; i += 2) lines.Add(string.Join("  ", bonus.Skip(i).Take(2)));
            return string.Join("\n", lines);
        }

        // ハンドガンは「連射」の枠がリロード速度になる（weaponId を渡したときだけ見分ける）
        public static string StatName(Stat s, int weaponId = -1) => s == Stat.FireRate && IsHandgun(weaponId)
            ? (UiPatches.Japanese ? "リロード" : "Reload speed")
            : UiPatches.Japanese
            ? s switch { Stat.Damage => "ダメージ", Stat.FireRate => "連射", Stat.Control => "コントロール", _ => "エルゴ" }
            : s switch { Stat.Damage => "Damage", Stat.FireRate => "Fire rate", Stat.Control => "Control", _ => "Ergonomics" };

        public static string BonusText(Stat s, int points, int weaponId = -1) => s switch
        {
            Stat.Damage => $"+{MathF.Round(DamagePerPoint * 100 * points)}%",
            Stat.FireRate when IsHandgun(weaponId) => $"+{MathF.Round(ReloadPerPoint * 100 * points)}%",
            Stat.FireRate => $"+{MathF.Round(FireRatePerPoint * 100 * points)}%",
            Stat.Control => $"+{ControlPerPoint * points:0.#}",
            _ => $"+{ErgoPerPoint * points:0.#}",
        };
    }
}
