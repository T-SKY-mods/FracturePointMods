using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Il2Cpp;
using UnityEngine;

using FPModOption;

namespace FPSurvival
{
    // 開始前の持ち物（マガジン・弾・回復・戦利品・予備の防具・ドル・ルーブル）を預かり、拠点に戻ったら返す。
    // ・預かる前に内容をファイル（pending.json）に書く。ゲームが途中で終わっても、次に拠点に入ったときに必ず返す
    // ・返すときは「預かった時の状態」に戻す（サバイバル中に増えた弾・マガジンは消える）
    // ・脱出したときだけ、サバイバルで稼いだドルと拾った壊れた武器部品を足す。倒れた／途中で終わったときは無し（持ち込んだ物は失わない）
    public static class Stash
    {
        public class AmmoE { public float Caliber { get; set; } public int Count { get; set; } }
        public class MagE { public int Index { get; set; } public int Ammo { get; set; } public int Max { get; set; } public string Weapon { get; set; } }
        public class IdCount { public int Id { get; set; } public int Count { get; set; } }

        public class Snapshot
        {
            public float Dollars { get; set; }
            public float Roubles { get; set; }
            public List<AmmoE> Ammo { get; set; } = new();
            public List<MagE> Mags { get; set; } = new();
            public List<IdCount> Loot { get; set; } = new();
            public List<IdCount> Health { get; set; } = new();
            public List<int> SpareArmor { get; set; } = new();
        }

        public class PendingRun
        {
            public string State { get; set; } = "active";      // active / escaped / died
            public string StartedUtc { get; set; }
            public Snapshot Snap { get; set; }
            public bool HadFloorPref { get; set; }
            public int FloorPref { get; set; }
            public string Main { get; set; }
            public string Sub { get; set; }
            public int Waves { get; set; }
            public float Earned { get; set; }
            public List<int> CrateWeapons { get; set; } = new();     // 武器箱で受け取った武器（拠点でもマガジンを 1 本残す）
            public int PartsFound { get; set; }                       // 脱出したときに持っていた壊れた武器部品（サバイバル中に拾った分）
            public int Handicap { get; set; } = -1;                  // 縛りのポイント（ラン中のいちばん低い値。-1 はまだ数えていない）
            public List<PrefE> Prefs { get; set; } = new();          // サバイバル中だけ変えたゲームの設定（元の値）
        }

        public class PrefE { public string Key { get; set; } public bool Had { get; set; } public string S { get; set; } public float F { get; set; } public int I { get; set; } public string Kind { get; set; } }

        public class BestRecord
        {
            public int Waves { get; set; }
            public string Main { get; set; }
            public string Sub { get; set; }
            public float Dollars { get; set; }
            public string Result { get; set; }
            public string Date { get; set; }
        }

        public static PendingRun Pending { get; private set; }

        // 壊れた武器部品（ゲームの戦利品。武器カスタマイズmod の解放に使う）
        public const int PartsId = 82;
        public static int PartsIn(Inventory inv)
        {
            int n = 0;
            foreach (var kv in inv.LootItems) if (kv.Key != null && kv.Key.Id == PartsId) n += kv.Value;
            return n;
        }
        public static BestRecord Best { get; private set; }

        static string PendingPath => Path.Combine(Main.Dir, "pending.json");
        static string RecordPath => Path.Combine(Main.Dir, "record.json");
        static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static bool InRun => Pending != null && Pending.State == "active";

        public static void Load()
        {
            try { if (File.Exists(PendingPath)) Pending = JsonSerializer.Deserialize<PendingRun>(File.ReadAllText(PendingPath)); }
            catch (Exception e) { Main.Log("[stash] pending.json unreadable: " + e.Message); BackupBroken(PendingPath); }
            try { if (File.Exists(RecordPath)) Best = JsonSerializer.Deserialize<BestRecord>(File.ReadAllText(RecordPath)); }
            catch (Exception e) { Main.Log("[stash] record.json unreadable: " + e.Message); BackupBroken(RecordPath); }
            Main.Log($"[stash] loaded: pending={Pending?.State ?? "none"} best={Best?.Waves.ToString() ?? "none"}");
        }

        static void BackupBroken(string path)
        {
            try { File.Copy(path, path + ".broken", true); } catch { }
        }

        public static void SavePending()
        {
            if (Pending == null) { try { File.Delete(PendingPath); } catch { } return; }
            WriteAtomic(PendingPath, JsonSerializer.Serialize(Pending, Json));
        }

        static void WriteAtomic(string path, string text)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        // 記録：到達ウェーブが多い方、同じならドルが多い方
        public static void Offer(int waves, float dollars, string result)
        {
            if (Pending == null) return;
            if (Best != null && (waves < Best.Waves || (waves == Best.Waves && dollars <= Best.Dollars))) return;
            Best = new BestRecord { Waves = waves, Main = Pending.Main, Sub = Pending.Sub, Dollars = dollars, Result = result, Date = DateTime.Now.ToString("yyyy-MM-dd") };
            try { WriteAtomic(RecordPath, JsonSerializer.Serialize(Best, Json)); } catch (Exception e) { Main.Log("[stash] record save failed: " + e.Message); }
            Main.Log($"[stash] new best: wave {waves} ${dollars:0} ({result})");
        }

        // ---------- 預かる ----------

        public static Snapshot Take(Inventory inv)
        {
            var s = new Snapshot { Dollars = inv.DollarsCount, Roubles = inv.RoublesCount };
            foreach (var kv in inv.AmmoStored) if (kv.Key != null && kv.Value > 0) s.Ammo.Add(new AmmoE { Caliber = kv.Key.Caliber, Count = kv.Value });
            foreach (var m in inv.MagStored) if (m != null) s.Mags.Add(new MagE { Index = m.Index, Ammo = m.AmmoCount, Max = m.MaxAmmoCount, Weapon = m.WeaponName });
            foreach (var kv in inv.LootItems) if (kv.Key != null && kv.Value > 0) s.Loot.Add(new IdCount { Id = kv.Key.Id, Count = kv.Value });
            foreach (var kv in inv.HealthItems) if (kv.Key != null && kv.Value > 0) s.Health.Add(new IdCount { Id = kv.Key.Id, Count = kv.Value });
            foreach (var a in SpareArmor(inv)) s.SpareArmor.Add(a.Id);
            return s;
        }

        // 装備していない防具
        static List<Armor> SpareArmor(Inventory inv)
        {
            var list = new List<Armor>();
            if (inv.ArmorItems == null) return list;
            foreach (var a in inv.ArmorItems)
            {
                if (a == null) continue;
                if (inv.HelmetEquipped != null && a.Pointer == inv.HelmetEquipped.Pointer) continue;
                if (inv.VestEquipped != null && a.Pointer == inv.VestEquipped.Pointer) continue;
                list.Add(a);
            }
            return list;
        }

        // マガジン・弾・回復・戦利品・ドル・ルーブルを空にする（予備の防具も）
        // keepMags：武器箱の武器のマガジン（1 本ずつ、空にして残す）
        public static void Strip(Inventory inv, bool spareArmor, ICollection<string> keepMags = null)
        {
            var ammo = new List<(float, int)>();
            foreach (var kv in inv.AmmoStored) if (kv.Key != null && kv.Value > 0) ammo.Add((kv.Key.Caliber, kv.Value));
            foreach (var (c, n) in ammo) inv.DeleteAmmo(c, n);
            var mags = new List<Mag>();
            var kept = new HashSet<string>();
            foreach (var m in inv.MagStored)
            {
                if (m == null) continue;
                if (keepMags != null && keepMags.Contains(m.WeaponName) && kept.Add(m.WeaponName)) { m.AmmoCount = 0; continue; }
                mags.Add(m);
            }
            // ゲームの DeleteMag は武器の情報が欠けたマガジンで止まることがある。1 本の失敗で返却全体が止まらないよう、直接外す
            foreach (var m in mags)
            {
                try { inv.DeleteMag(m); }
                catch (Exception e)
                {
                    Main.Log($"[stash] DeleteMag failed for '{m?.WeaponName}' ({e.Message.Split('\n')[0]}), removing it directly");
                    try { inv.MagStored.Remove(m); } catch { }
                }
            }
            var loot = new List<(int, int)>();
            foreach (var kv in inv.LootItems) if (kv.Key != null && kv.Value > 0) loot.Add((kv.Key.Id, kv.Value));
            foreach (var (id, n) in loot) inv.DeleteLoot(id, n, false);
            var heal = new List<(int, int)>();
            foreach (var kv in inv.HealthItems) if (kv.Key != null && kv.Value > 0) heal.Add((kv.Key.Id, kv.Value));
            foreach (var (id, n) in heal) inv.DeleteHealthItem(id, n, false);
            if (spareArmor) foreach (var a in SpareArmor(inv)) inv.DeleteArmor(a);
            inv.DollarsCount = 0f;
            inv.RoublesCount = 0f;
        }

        public static void Put(Inventory inv, Snapshot s, float extraDollars)
        {
            foreach (var a in s.Ammo) inv.AddAmmo(a.Caliber, a.Count);
            foreach (var e in s.Mags)
            {
                Mag proto = null;
                foreach (var m in inv.MagData.Mags) if (m != null && m.Index == e.Index) { proto = m; break; }
                if (proto == null) { Main.Log($"[stash] mag index {e.Index} ({e.Weapon}) not found"); continue; }
                var mag = proto.Clone();
                if (e.Max > 0) mag.MaxAmmoCount = e.Max;
                mag.AmmoCount = e.Ammo;
                if (!inv.AddMag(mag)) Main.Log($"[stash] AddMag refused {e.Weapon}");
            }
            foreach (var e in s.Loot) inv.AddLootItem(e.Id, e.Count, false);
            foreach (var e in s.Health) inv.AddHealthItem(e.Id, e.Count);
            foreach (var id in s.SpareArmor)
            {
                Armor proto = null;
                foreach (var a in inv.ArmorData.Armor) if (a != null && a.Id == id) { proto = a; break; }
                if (proto == null) { Main.Log($"[stash] armor id {id} not found"); continue; }
                inv.AddArmor(proto.Clone());
            }
            inv.DollarsCount = s.Dollars + extraDollars;
            inv.RoublesCount = s.Roubles;
        }

        public static string Describe(Snapshot s) =>
            $"$={s.Dollars:0} rub={s.Roubles:0} ammo=[{string.Join(",", s.Ammo.Select(a => $"{a.Caliber}:{a.Count}"))}] mags=[{string.Join(",", s.Mags.Select(m => $"{m.Weapon}:{m.Ammo}/{m.Max}"))}] loot=[{string.Join(",", s.Loot.Select(l => $"{l.Id}x{l.Count}"))}] heal=[{string.Join(",", s.Health.Select(l => $"{l.Id}x{l.Count}"))}] spareArmor=[{string.Join(",", s.SpareArmor)}]";

        // ---------- 開始 ----------

        // 縛りのポイントを数え直す（開始・ウェーブのクリア・武器箱のとき）。下がったときだけ書き換える
        public static void UpdateHandicap(Inventory inv)
        {
            if (Pending == null || inv == null) return;
            int p = Survival.HandicapPoints(inv, out var detail);
            if (Pending.Handicap >= 0 && p >= Pending.Handicap) return;
            Pending.Handicap = p;
            SavePending();
            Main.Log($"[stash] handicap {p} points ({detail}) = crate as wave +{Waves.HandicapWaves(p)}");
        }

        public static PendingRun Begin(Inventory inv, string main, string sub)
        {
            var run = new PendingRun
            {
                StartedUtc = DateTime.UtcNow.ToString("o"),
                Snap = Take(inv),
                HadFloorPref = PlayerPrefs.HasKey("currentFloor"),
                FloorPref = PlayerPrefs.GetInt("currentFloor", 0),
                Main = main, Sub = sub,
            };
            // 難易度と拠点の家具の効果（戦士の花瓶・瀕死で無敵になる熊の像・時間が遅くなる時の像）、
            // カードの被弾無効はサバイバルでは使わない。元の値は記録して、拠点で戻す
            foreach (var (key, kind, _) in NeutralPrefs)
                run.Prefs.Add(new PrefE
                {
                    Key = key, Kind = kind, Had = PlayerPrefs.HasKey(key),
                    S = kind == "s" ? PlayerPrefs.GetString(key, "") : null,
                    F = kind == "f" ? PlayerPrefs.GetFloat(key, 1f) : 0f,
                    I = kind == "i" ? PlayerPrefs.GetInt(key, 0) : 0,
                });
            Pending = run;
            SavePending();     // 預かる前にファイルへ（途中でゲームが終わっても返せるように）
            foreach (var (key, kind, value) in NeutralPrefs)
            {
                if (kind == "s") PlayerPrefs.SetString(key, (string)value);
                else if (kind == "f") PlayerPrefs.SetFloat(key, (float)value);
                else PlayerPrefs.SetInt(key, (int)value);
            }
            Main.Log("[stash] difficulty/furniture for survival: " + string.Join(", ", run.Prefs.Select(p => $"{p.Key}={(p.Had ? p.S ?? (p.Kind == "f" ? p.F.ToString("0.00") : p.I.ToString()) : "-")}"))
                + " -> " + string.Join(", ", NeutralPrefs.Select(n => n.value)));
            Main.Log("[stash] taken: " + Describe(run.Snap));
            Strip(inv, spareArmor: true);
            return run;
        }

        // サバイバル中の値
        public static readonly (string key, string kind, object value)[] NeutralPrefs =
        {
            ("Difficulty", "s", "Default"), ("StatueDamage", "f", 1f), ("StatueEffect", "i", 0),
            ("Mighty Bear Statue_Furniture", "i", 0), ("Statue of Time_Furniture", "i", 0), ("NoDamage", "f", 0f),
        };

        static void RestorePrefs(PendingRun run)
        {
            foreach (var p in run.Prefs ?? new List<PrefE>())
            {
                if (!p.Had) { PlayerPrefs.DeleteKey(p.Key); continue; }
                switch (p.Kind)
                {
                    case "s": PlayerPrefs.SetString(p.Key, p.S ?? ""); break;
                    case "f": PlayerPrefs.SetFloat(p.Key, p.F); break;
                    default: PlayerPrefs.SetInt(p.Key, p.I); break;
                }
            }
            if (run.Prefs?.Count > 0) Main.Log("[stash] difficulty/furniture restored");
        }

        // ---------- 拠点に戻ったら返す ----------

        static float _restoreAt = -1f;

        public static void OnSceneLoaded()
        {
            _restoreAt = Pending != null && Main.Scene == Core.HubScene ? Time.realtimeSinceStartup + 3f : -1f;
        }

        public static void Tick()
        {
            if (_restoreAt < 0f || Time.realtimeSinceStartup < _restoreAt) return;
            var inv = Refs.Inv;
            if (inv == null || Refs.Fps == null) { _restoreAt = Time.realtimeSinceStartup + 1f; return; }
            _restoreAt = -1f;
            RestoreNow(inv);
        }

        public static void RestoreNow(Inventory inv)
        {
            var run = Pending;
            if (run == null) return;
            bool escaped = run.State == "escaped";
            if (run.State == "active")
            {
                Main.Log("[stash] the run did not finish (game closed during survival): treated as a death");
                if (run.Waves > 0) Offer(run.Waves, 0f, "died");
            }
            float earned = escaped ? Math.Max(0f, run.Earned) : 0f;
            Main.Log($"[stash] returning items (state={run.State}, earned=${earned:0}, parts={(escaped ? run.PartsFound : 0)}); before: " + Describe(Take(inv)));
            // 武器箱の武器は、入手のときに一緒に入った空のマガジンを残す（通常のランで拾ったときと同じ）
            var crateMags = escaped ? (run.CrateWeapons ?? new List<int>()).Select(id => Weapons.ItemById(id)?.GetName()).Where(n => n != null).ToList() : null;
            Strip(inv, spareArmor: false, crateMags);   // サバイバル中の弾・マガジン・ドルを消してから
            Put(inv, run.Snap, earned);
            int parts = escaped ? Math.Max(0, run.PartsFound) : 0;
            if (parts > 0) inv.AddLootItem(PartsId, parts, false);
            Main.Log("[stash] after: " + Describe(Take(inv)));
            RestorePrefs(run);
            if (run.HadFloorPref) PlayerPrefs.SetInt("currentFloor", run.FloorPref); else PlayerPrefs.DeleteKey("currentFloor");
            PlayerPrefs.DeleteKey("ENDLESS_MODE");
            PlayerPrefs.Save();
            Pending = null;
            SaveGame();
            SavePending();   // ゲームのセーブの後でファイルを消す
            Main.Notify(escaped
                ? (parts > 0
                    ? Main.T($"サバイバル：預けた物を返却し、${earned:N0} と壊れた武器部品 {parts} 個を受け取りました", $"Survival: items returned, received ${earned:N0} and {parts} Broken Weapon Parts")
                    : Main.T($"サバイバル：預けた物を返却し、${earned:N0} を受け取りました", $"Survival: items returned, received ${earned:N0}"))
                : Main.T("サバイバル：預けた物を返却しました", "Survival: your items were returned"));
        }

        static void SaveGame()
        {
            try
            {
                var sm = UnityEngine.Object.FindObjectOfType<SavesManager>();
                if (sm == null) { Main.Log("[stash] SavesManager not found"); return; }
                sm.Save();
                Main.Log("[stash] game saved");
            }
            catch (Exception e) { Main.Log("[stash] save failed: " + e); }
        }
    }
}
