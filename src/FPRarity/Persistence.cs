using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using FPModOption;

namespace FPRarity
{
    // Mod専用のセーブ。ゲーム本体のセーブには一切書き込まない。
    //
    // ゲームは手持ち（SavesManager.Save）と倉庫（SaveBag.SaveProgress）を別々のタイミングで保存し、
    // セーブせずに終了すれば直前のセーブ時点に戻る。Mod の記録がそれより新しいと、次回起動時に
    // ゲームの並びと食い違ってしまうため、ゲームが保存した瞬間の状態だけをそれぞれ確定保存する。
    public static class Persistence
    {
        class SaveFile
        {
            public int Version { get; set; } = 3;   // 3: コントロールの増減が今の単位（StatScale）
            public List<Tracker.HeldEntry> Held { get; set; }
            public List<WeaponRoll> Stash { get; set; }
            // 記録と合わなくなったロール（この Mod をオフにしている間に動かした武器）。同じ種類の武器が来たら戻す
            public List<WeaponRoll> Spare { get; set; }
        }

        static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
        static string PathOf => Path.Combine(Main.Dir, "state.json");

        // 最後にゲームが保存した時点の内容
        static List<Tracker.HeldEntry> _held = new();
        static List<WeaponRoll> _stash = new();

        public static void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(PathOf)) { Main.Log("[save] no state.json (fresh)"); return; }
                var s = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(PathOf), Opts);
                _held = s?.Held ?? new();
                _stash = s?.Stash ?? new();
                if (s != null && s.Version < 3) Upgrade(s);
                Tracker.Restore(_held.ToList(), _stash.ToList(), s?.Spare);
                Main.Log($"[save] loaded held={_held.Count} stash={_stash.Count} spare={Tracker.Spare.Count}");
            }
            catch (Exception e)
            {
                // 壊れていたら退避して空から始める（ゲームのセーブには影響しない）
                Main.Log("[save] load failed: " + e.Message);
                try { File.Move(PathOf, PathOf + $".broken_{DateTime.Now:yyyyMMdd_HHmmss}"); } catch { }
            }
        }

        // 2 まで：ロールのコントロールの増減は以前の単位（+1 = 反動 -1%）。同じ強さの今の単位に直す（次の保存で 3 になる）
        static void Upgrade(SaveFile s)
        {
            int n = 0;
            void Fix(WeaponRoll r) { if (r == null || r.ControlDelta == 0) return; r.ControlDelta = (int)MathF.Round(StatScale.FromOld(r.ControlDelta)); n++; }
            foreach (var h in s.Held ?? new()) Fix(h.Roll);
            foreach (var r in s.Stash ?? new()) Fix(r);
            foreach (var r in s.Spare ?? new()) Fix(r);
            Main.Log($"[save] converted the control of {n} rolls to the new scale (v{s.Version} -> 3)");
        }

        public static void CommitHeld(string reason)
        {
            Tracker.ReconcileHeld("pre-commit " + reason);
            _held = Tracker.Held.ToList();
            Write($"held ({reason})");
        }

        public static void CommitStash(string reason)
        {
            Tracker.ReconcileStash("pre-commit " + reason);
            _stash = Tracker.Stash.ToList();
            Write($"stash ({reason})");
        }

        // ゲームのセーブファイルの更新を検知して確定保存する（非同期セーブは関数を差し替えると落ちるため）
        static DateTime _heldStamp, _stashStamp;
        static float _nextPoll;

        public static void Poll()
        {
            if (UnityEngine.Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = UnityEngine.Time.realtimeSinceStartup + 0.5f;
            try
            {
                var dir = UnityEngine.Application.persistentDataPath;
                CheckFile(Path.Combine(dir, "SavedData.json"), ref _heldStamp, () => CommitHeld("SavedData.json updated"));
                CheckFile(Path.Combine(dir, "StashData.json"), ref _stashStamp, () => CommitStash("StashData.json updated"));
            }
            catch (Exception e) { Main.Log("[save] poll failed: " + e.Message); }
        }

        static void CheckFile(string path, ref DateTime stamp, Action commit)
        {
            if (!File.Exists(path)) return;
            var t = File.GetLastWriteTimeUtc(path);
            if (stamp == default) { stamp = t; return; } // 起動時点の状態は基準として記録するだけ
            if (t == stamp) return;
            stamp = t;
            commit();
        }

        static void Write(string what)
        {
            try
            {
                var tmp = PathOf + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new SaveFile { Held = _held, Stash = _stash, Spare = Tracker.Spare.ToList() }, Opts));
                File.Copy(tmp, PathOf, true);
                File.Delete(tmp);
                Main.Log($"[save] committed {what}: held={_held.Count} stash={_stash.Count} spare={Tracker.Spare.Count}");
            }
            catch (Exception e) { Main.Log("[save] write failed: " + e.Message); }
        }
    }
}
