using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FPModOption;
using Il2Cpp;
using UnityEngine;

namespace FPEndless
{
    // 調整の値（端末で選ぶ）と、調整したランの間の状態
    //   強さ：敵の弾のダメージ（ClassStats.bulletDamage。ゲームの敵の種類ごとの設定を、ランの間だけ書き換えて、拠点に戻ると戻す）
    //   数：敵の湧く数（階の部屋の EnemySpawner.Spawn の数。ボス部屋に置いてある雑魚はレアリティmod が Link.CountMult で増やす）
    //   体力：敵が受けるダメージを 1/倍率 にする（EnemyHealth.TakeDamage。ボスも同じ）
    //   運 = 1 + （強さ−1）÷3 ＋（数−1）＋（体力−1）。それぞれの上限で 1 ずつ、全部最大で 4 倍（上限を 2 倍にしても最大の運は前と同じ）
    //   参加費 = $500 × 100^((運−1)/3)。$100 単位（4 倍で $50,000）
    //   招き猫に賽銭（次のラン）×1.25、戦士の花瓶（敵のダメージ +35%）×1.5 を運に掛ける（調整のときだけ。参加費は調整の分だけ）
    //   花瓶の敵のダメージはゲームが湧く敵に掛けるので、強さの倍率と掛け合わさる
    public static class Tuning
    {
        public const float Min = 1f;
        // 強さは 4 倍まで 0.5 刻み、数・体力は 2 倍まで 0.25 刻み（2026-11-23 ユーザー指定）
        public static float MaxOf(int which) => which == 0 ? 4f : 2f;
        public static float StepOf(int which) => which == 0 ? 0.5f : 0.25f;
        public const int VanillaFee = 500, MaxFee = 50000;

        class Saved { public bool Mode { get; set; } public float Power { get; set; } = 1f; public float Count { get; set; } = 1f; public float Hp { get; set; } = 1f; }
        static Saved _s = new();
        static string FilePath => Path.Combine(Main.Dir, "tuning.json");

        public static bool Mode { get => _s.Mode; set { _s.Mode = value; Save(); } }   // 端末で「調整」を選んでいる
        public static float PowerSet => _s.Power;
        public static float CountSet => _s.Count;
        public static float HpSet => _s.Hp;

        // 調整したランの間の値（ランを始めたときの値に固定）
        public static bool Active { get; private set; }
        static float _power = 1f, _count = 1f, _hp = 1f, _luck = 1f;
        public static float Power => _power;
        public static float Count => _count;
        public static float Hp => _hp;
        public static float Luck => _luck;

        public static float LuckOf(float p, float c, float h) => 1f + (p - 1f) / (MaxOf(0) - 1f) + (c - 1f) / (MaxOf(1) - 1f) + (h - 1f) / (MaxOf(2) - 1f);
        public static float LuckSet => Mode ? LuckOf(_s.Power, _s.Count, _s.Hp) : 1f;   // 調整の分（参加費のもと）

        // 招き猫・花瓶の運（ゲームの状態をそのまま読む）
        public const float CatLuck = 1.25f, VaseLuck = 1.5f;
        public static bool CatNow() { try { return Il2CppCoop.CoopLoot.KittyBonus; } catch { return false; } }
        public static bool VaseNow() { try { return Il2CppCoop.Proposals.StatueRules.LootBoostEnabled; } catch { return false; } }
        public static float BonusOf(bool cat, bool vase) => (cat ? CatLuck : 1f) * (vase ? VaseLuck : 1f);
        public static float TotalSet => Mode ? LuckSet * BonusOf(CatNow(), VaseNow()) : 1f;     // 端末に出す運（全部掛けたもの）
        public static bool RunCat { get; private set; }
        public static bool RunVase { get; private set; }
        public static int FeeOf(float luck)
        {
            double f = VanillaFee * Math.Pow((double)MaxFee / VanillaFee, Math.Clamp((luck - 1f) / 3f, 0f, 1f));
            return (int)(Math.Round(f / 100.0) * 100.0);
        }
        public static int FeeSet => FeeOf(LuckSet);

        public static void Load()
        {
            try { if (File.Exists(FilePath)) _s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) ?? new(); }
            catch (Exception e) { Main.Log("[tuning] read failed (defaults): " + e.Message); _s = new(); }
            _s.Power = Snap(0, _s.Power); _s.Count = Snap(1, _s.Count); _s.Hp = Snap(2, _s.Hp);
            Main.Log($"[tuning] loaded: mode={(_s.Mode ? "tuned" : "normal")} power x{_s.Power} count x{_s.Count} hp x{_s.Hp} -> luck x{LuckSet:0.##} fee ${FeeSet:N0}");
        }

        static void Save()
        {
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_s)); } catch (Exception e) { Main.Log("[tuning] save failed: " + e.Message); }
        }

        static float Snap(int which, float v) => Mathf.Clamp(Mathf.Round(v / StepOf(which)) * StepOf(which), Min, MaxOf(which));

        // which: 0 強さ 1 数 2 体力。dir: +1 / −1
        public static void Change(int which, int dir)
        {
            float v = Snap(which, Get(which) + dir * StepOf(which));
            if (which == 0) _s.Power = v; else if (which == 1) _s.Count = v; else _s.Hp = v;
            Save();
        }
        public static float Get(int which) => which == 0 ? _s.Power : which == 1 ? _s.Count : _s.Hp;
        public static void SetForTest(bool mode, float p, float c, float h) { _s.Mode = mode; _s.Power = Snap(0, p); _s.Count = Snap(1, c); _s.Hp = Snap(2, h); Save(); }

        // エンドレスを始めるとき（参加費を払った後）
        public static void BeginRun()
        {
            Active = Mode;
            RunCat = Active && CatNow(); RunVase = Active && VaseNow();
            _power = Active ? _s.Power : 1f; _count = Active ? _s.Count : 1f; _hp = Active ? _s.Hp : 1f; _luck = Active ? LuckSet * BonusOf(RunCat, RunVase) : 1f;
            Main.Log(Active ? $"[run] tuned endless run: power x{_power} count x{_count} hp x{_hp} luck x{_luck:0.##} (tuning x{LuckSet:0.##} cat {RunCat} vase {RunVase})" : "[run] normal endless run");
            if (Active) ApplyDamage();
        }

        public static void EndRun(string why)
        {
            if (!Active && _damaged.Count == 0) return;
            RestoreDamage();
            Active = false; RunCat = RunVase = false; _power = _count = _hp = _luck = 1f;
            Main.Log($"[run] tuning off ({why})");
        }

        public static void OnSceneLoaded(string scene)
        {
            if (scene == Core.HubScene || scene == "MainMenu") { EndRun("left the run"); return; }
            if (Active) ApplyDamage();
        }

        static float _nextCheck, _nextScan;
        public static void Tick()
        {
            if (!Active || Time.realtimeSinceStartup < _nextCheck) return;
            _nextCheck = Time.realtimeSinceStartup + 2f;
            if (Link.InSurvival()) EndRun("survival");
            // 敵の種類の設定を探すのは重い（1 回 20ms 以上。前は 2 秒ごとに探していてカクついた）。
            // シーンを読み込んだときと、新しい階で敵が湧くとき（OnSpawn、30 秒に 1 回まで）だけにした
        }
        // ランの間の重さを 30 秒ごとにログへ（FPS が落ちたという報告の調査用。数えるだけで探し物はしない）
        static float _fpsT0 = -1f, _fpsWorst; static int _fpsN, _fpsSlow; static double _fpsSum;
        public static void TickFps(bool inRun)
        {
            if (!inRun) { _fpsT0 = -1f; return; }
            float dt = Time.unscaledDeltaTime * 1000f;
            if (_fpsT0 < 0f) { _fpsT0 = Time.realtimeSinceStartup; _fpsN = _fpsSlow = 0; _fpsSum = 0; _fpsWorst = 0f; return; }
            _fpsN++; _fpsSum += dt; if (dt > _fpsWorst) _fpsWorst = dt; if (dt > 33f) _fpsSlow++;
            if (Time.realtimeSinceStartup - _fpsT0 < 30f) return;
            double avg = _fpsSum / Math.Max(1, _fpsN);
            Main.Log($"[fps] {(Active ? "tuned" : "normal")} run: avg {avg:0.0}ms ({1000.0 / avg:0}fps) worst {_fpsWorst:0}ms frames>33ms {_fpsSlow}/{_fpsN} | enemies spawned so far {Patches.SpawnGiven} (asked {Patches.SpawnAsked}) hits {Patches.DamageScaled}");
            _fpsT0 = Time.realtimeSinceStartup; _fpsN = _fpsSlow = 0; _fpsSum = 0; _fpsWorst = 0f;
        }

        public static void OnSpawn()
        {
            if (!Active || _power == 1f || Time.realtimeSinceStartup < _nextScan) return;
            _nextScan = Time.realtimeSinceStartup + 30f;
            ApplyDamage();
        }

        // ---------- 敵の弾のダメージ（敵の種類ごとの設定。複製（(Clone)）は元から作られるので触らない） ----------
        static readonly Dictionary<IntPtr, (Il2CppEnemyAI.ClassStats cs, float orig)> _damaged = new();
        static void ApplyDamage()
        {
            if (_power == 1f) return;
            try
            {
                int n = 0;
                foreach (var cs in Resources.FindObjectsOfTypeAll<Il2CppEnemyAI.ClassStats>())
                {
                    if (cs == null || _damaged.ContainsKey(cs.Pointer) || (cs.name ?? "").EndsWith("(Clone)")) continue;
                    _damaged[cs.Pointer] = (cs, cs.bulletDamage);
                    cs.bulletDamage *= _power;
                    n++;
                }
                if (n > 0) Main.Log($"[run] enemy bullet damage x{_power} on {n} enemy classes ({_damaged.Count} total)");
            }
            catch (Exception e) { Core.LogOnce("endl-dmg", "[run] damage scaling failed: " + e.Message); }
        }

        static void RestoreDamage()
        {
            int n = 0;
            foreach (var (cs, orig) in _damaged.Values)
                try { if (cs != null) { cs.bulletDamage = orig; n++; } } catch { }
            if (_damaged.Count > 0) Main.Log($"[run] enemy bullet damage restored on {n} classes");
            _damaged.Clear();
        }
        public static float DamageOfForTest(string name)
        {
            foreach (var cs in Resources.FindObjectsOfTypeAll<Il2CppEnemyAI.ClassStats>()) if (cs.name == name) return cs.bulletDamage;
            return -1f;
        }
        public static string FirstClassForTest() => Resources.FindObjectsOfTypeAll<Il2CppEnemyAI.ClassStats>().Select(c => c.name).FirstOrDefault(n => !n.EndsWith("(Clone)"));
        public static int DamagedForTest => _damaged.Count;
        public static void ScanForTest() => ApplyDamage();
        public static (string name, float orig, float now) FirstDamagedForTest()
        {
            foreach (var (cs, orig) in _damaged.Values) if (cs != null) return (cs.name, orig, cs.bulletDamage);
            return ("", -1f, -1f);
        }
        public static void SetRunForTest(float p, float c, float h) { Active = true; _power = p; _count = c; _hp = h; _luck = LuckOf(p, c, h); }
        public static void SetLuckForTest(float luck) => _luck = luck;

        // 運のレアリティの重み：レアリティ t（0 Common 〜 4 Legendary）の重みに 運^(0.5t) を掛ける
        public const float TiltPower = 0.5f;    // 2026-11-23 一度 0.75 に強めたが、ユーザー指定で 0.5 に戻した
        public static float Tilt(int tier, float luck) => Mathf.Pow(luck, tier * TiltPower);
    }
}
