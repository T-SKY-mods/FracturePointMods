using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario perf / perfboss）。エンドレスの階で FPS とその内訳を測り、何が重いかを切り分ける。
    //   perf     … 通常の階（ボス部屋にしない）
    //   perfboss … ボス 4体＋雑魚が多い階（50階相当のボス部屋）
    // 測るもの：FPS・1% 下位、メイン/描画スレッド/GPU の時間（取れる場合）、描画数、敵や Animator などの数、
    // 設定（画質・URP）。切り分けとして一時的に「解像度を下げる」「影を消す」等を入れ替えて差を見る（測定後に必ず戻す）。
    // プレイヤーはテストの間だけ無敵。セーブは autotest.ps1 が前後で元に戻す。
    public static class PerfProbe
    {
        static bool _done;
        public static bool SkipModTick;                 // 切り分け：FPRarity の毎フレーム処理を止める
        public static double ModTickMs;                 // FPRarity の毎フレーム処理にかかった時間（累計）
        public static double LastModTickMs;             // 直前のフレームの分

        // FPRarity の毎フレーム処理の内訳（どれが重いか）
        static readonly string[] SubNames = { "Persistence.Poll", "UiPatches.Tick", "Mastery.Tick", "MasteryUi.Tick", "EndlessBoss.Tick", "SlideTweaks.Tick", "StashTooltip.Tick", "F9 key" };
        static readonly double[] _subSum = new double[8], _subMax = new double[8];
        static readonly int[] _subSlow = new int[8];
        public static void Sub(int i, ref long t)
        {
            long now = Stopwatch.GetTimestamp();
            double ms = (now - t) * 1000.0 / Stopwatch.Frequency;
            t = now;
            _subSum[i] += ms;
            if (ms > _subMax[i]) _subMax[i] = ms;
            if (ms > 2) _subSlow[i]++;
        }

        // ---------- ゲーム側の怪しい処理の時間を直接測る（測定中だけ） ----------

        static readonly (Type, string)[] Timed =
        {
            (typeof(Il2CppGameLocalization.LocBootstrap), "Update"),
            (typeof(Il2CppGameLocalization.LocBootstrap), "LateUpdate"),
            (typeof(Il2CppGameLocalization.LocFonts), "PatchLoadedFontAssets"),
            (typeof(Il2CppGameLocalization.LocTextHook), "ScanAll"),
            (typeof(Il2CppGameLocalization.LocLegacyText), "Scan"),
            (typeof(Il2CppGameLocalization.LocLegacyText), "Tick"),
            (typeof(Il2CppGamepadSupport.PadBootstrap), "Update"),
            (typeof(Il2CppGamepadSupport.PadBootstrap), "FindPlayer"),
            (typeof(Il2CppGamepadSupport.PadSettings), "Reload"),
            (typeof(Il2CppGamepadSupport.PadDevice), "Tick"),
            (typeof(Il2CppGamepadSupport.PadLayers), "Tick"),
            (typeof(Il2CppGamepadSupport.PadPause), "Tick"),
            (typeof(Il2CppGamepadSupport.PadMenu), "Tick"),
            (typeof(Il2CppGamepadSupport.PadChords), "Tick"),
            (typeof(Il2CppGamepadSupport.PadChords), "FindPlayer"),
            (typeof(Il2CppGamepadSupport.PadChords), "UnmuteAll"),
            (typeof(Il2CppGamepadSupport.PadInventory), "Tick"),
            (typeof(Il2CppGamepadSupport.PadCursor), "Tick"),
            (typeof(Il2CppGamepadSupport.PadPrompts), "Refresh"),
            (typeof(Il2CppGamepadSupport.PadPrompts), "KeepHintsClean"),
            (typeof(Il2CppGamepadSupport.PadSettingsUi), "Tick"),
            (typeof(Il2CppGamepadSupport.PadModes), "Tick"),
            (typeof(Il2CppGamepadSupport.PadConflicts), "Tick"),
        };

        sealed class Stat { public double Sum, Max; public int Calls, Slow; }
        static readonly Dictionary<string, Stat> _timed = new();
        static bool _timedInstalled;

        static void InstallTimers()
        {
            if (_timedInstalled) return;
            _timedInstalled = true;
            var h = new HarmonyLib.Harmony("fprarity.perftimers");
            foreach (var (t, m) in Timed)
            {
                var methods = HarmonyLib.AccessTools.GetDeclaredMethods(t).Where(x => x.Name == m).ToList();
                if (methods.Count == 0) { Main.Log($"[perf] timer: {t.Name}.{m} not found"); continue; }
                foreach (var mi in methods)
                {
                    var args = mi.GetParameters().Select(p => p.ParameterType).ToArray();
                    SafePatcher.Patch(h, t, m, args, nameof(TimerPre), nameof(TimerPost), typeof(PerfProbe));
                }
            }
        }

        static int _rayFrame; static double _rayMaxFrame;
        static void TimerPre(out long __state) => __state = Stopwatch.GetTimestamp();

        static void TimerPost(long __state, System.Reflection.MethodBase __originalMethod)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            var key = __originalMethod.DeclaringType.FullName.Replace("Il2Cpp", "") + "." + __originalMethod.Name;
            if (ms > 8 && __originalMethod.DeclaringType != typeof(UnityEngine.Physics)) Main.Log($"[perf] slow {key} {ms:0.0}ms t={Time.time:0.00} bullets={Il2CppBallistics.BulletHandler.instance?.Bullets?.Count} raycast this frame max {_rayMaxFrame:0.0}ms mono gc {System.GC.CollectionCount(0)}/{System.GC.CollectionCount(1)}/{System.GC.CollectionCount(2)} il2cpp gc {Il2CppSystem.GC.CollectionCount(0)}");
            if (__originalMethod.DeclaringType == typeof(UnityEngine.Physics)) { if (Time.frameCount != _rayFrame) { _rayFrame = Time.frameCount; _rayMaxFrame = 0; } if (ms > _rayMaxFrame) _rayMaxFrame = ms; }
            if (!_timed.TryGetValue(key, out var s)) _timed[key] = s = new Stat();
            s.Sum += ms; s.Calls++; if (ms > s.Max) s.Max = ms; if (ms > 2) s.Slow++;
        }

        static void ReportTimers(float seconds)
        {
            var sb = new StringBuilder($"[perf] game method timings over {seconds:0}s (total ms/s, calls, max, calls>2ms):\n");
            foreach (var kv in _timed.OrderByDescending(k => k.Value.Sum))
                sb.AppendLine($"    {kv.Key,-40} {kv.Value.Sum / seconds,8:0.00} ms/s  calls={kv.Value.Calls,6}  avg={kv.Value.Sum / Math.Max(1, kv.Value.Calls):0.000}  max={kv.Value.Max:0.00}  slow={kv.Value.Slow}");
            Main.Log(sb.ToString());
            _timed.Clear();
        }

        // 同じ結果（シーン上の文字・フォント）を得る、より軽い探し方の候補を比べる
        static void EnumTimings()
        {
            void T(string name, Func<int> f)
            {
                var ms = new List<double>(); int n = 0;
                for (int i = 0; i < 3; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    n = f();
                    ms.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                }
                Main.Log($"[perf] enum {name,-60} n={n,6} ms={string.Join(" / ", ms.Select(x => x.ToString("0.00")))}");
            }
            var tmp = Il2CppInterop.Runtime.Il2CppType.Of<Il2CppTMPro.TMP_Text>();
            var txt = Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.UI.Text>();
            var font = Il2CppInterop.Runtime.Il2CppType.Of<Il2CppTMPro.TMP_FontAsset>();
            T("Resources.FindObjectsOfTypeAll(TMP_Text)", () => Resources.FindObjectsOfTypeAll(tmp).Length);
            T("Object.FindObjectsByType(TMP_Text, Include)", () => UnityEngine.Object.FindObjectsByType(tmp, FindObjectsInactive.Include, FindObjectsSortMode.None).Length);
            T("Object.FindObjectsByType(TMP_Text, Exclude)", () => UnityEngine.Object.FindObjectsByType(tmp, FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length);
            T("Resources.FindObjectsOfTypeAll(UI.Text)", () => Resources.FindObjectsOfTypeAll(txt).Length);
            T("Object.FindObjectsByType(UI.Text, Include)", () => UnityEngine.Object.FindObjectsByType(txt, FindObjectsInactive.Include, FindObjectsSortMode.None).Length);
            T("Resources.FindObjectsOfTypeAll(TMP_FontAsset)", () => Resources.FindObjectsOfTypeAll(font).Length);
            T("Resources.FindObjectsOfTypeAll(Material)", () => Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Material>()).Length);
            T("Resources.FindObjectsOfTypeAll(MonoBehaviour)", () => Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MonoBehaviour>()).Length);
            T("Resources.FindObjectsOfTypeAll(ScriptableObject)", () => Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<ScriptableObject>()).Length);
            T("Resources.FindObjectsOfTypeAll(SaveBag)", () => Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<SaveBag>()).Length);
            T("Object.FindObjectsByType(SaveBag, Include)", () => UnityEngine.Object.FindObjectsByType(Il2CppInterop.Runtime.Il2CppType.Of<SaveBag>(), FindObjectsInactive.Include, FindObjectsSortMode.None).Length);
        }

        // ゲームの Unity ログ（例外など）を数える。ゲーム側はログを書かない設定なので、ここで受け取って集計する
        static readonly Dictionary<string, int> _logs = new();
        static bool _logHooked;
        static void HookUnityLog()
        {
            if (_logHooked) return;
            _logHooked = true;
            Application.add_logMessageReceived(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Application.LogCallback>(
                new Action<string, string, LogType>((msg, stack, type) =>
                {
                    if (type == LogType.Log) return;
                    var firstStack = string.Join(" | ", (stack ?? "").Split('\n').Take(4).Select(s => s.Trim()));
                    var key = $"{type}: {(msg ?? "").Split('\n')[0]}  @ {firstStack}";
                    _logs[key] = _logs.TryGetValue(key, out var c) ? c + 1 : 1;
                })));
        }

        static void ReportLogs(string label)
        {
            var sb = new StringBuilder($"[perf] unity log messages ({label}): {_logs.Values.Sum()} total\n");
            foreach (var kv in _logs.OrderByDescending(k => k.Value).Take(20)) sb.AppendLine($"    {kv.Value,6}  {kv.Key}");
            Main.Log(sb.ToString());
            _logs.Clear();
        }

        static void ReportSubs(int frames)
        {
            var sb = new StringBuilder("[perf] FPRarity per-frame work:");
            for (int i = 0; i < SubNames.Length; i++)
                sb.Append($" | {SubNames[i]} avg={_subSum[i] / Math.Max(1, frames):0.000}ms max={_subMax[i]:0.00}ms >2ms x{_subSlow[i]}");
            Main.Log(sb.ToString());
        }

        static bool _fight;

        public static List<(string, Action)> Steps(bool boss, bool fight = false)
        {
            _fight = fight;
            var steps = new List<(string, Action)>
            {
                ("start endless run", (Action)(() =>
                {
                    var force = Path.Combine(Main.Dir, "endless_boss.txt");
                    if (fight) Type.GetType("FPCustom.NewPartsTest, FPCustom")?.GetMethod("LaserAll")?.Invoke(null, new object[] { "laser_blue", "p_laser" });
                    if (boss) File.WriteAllText(force, "50");
                    else { try { File.Delete(force); } catch { } EndlessBoss.ForceNormalForTest = true; }
                    var ff = Path.Combine(Main.Dir, "perf_floor.txt");   // 通常の階を何階として作るか（無ければゲームのまま）
                    if (File.Exists(ff) && int.TryParse(File.ReadAllText(ff).Trim(), out var fl)) EndlessBoss.ForceFloorForTest = fl;
                    Main.FindAll<ButtonStartEndlessMode>().FirstOrDefault()?.StartEndlessRun();
                })),
            };
            for (int i = 0; i < 12; i++) steps.Add(("wait load", () => { }));
            steps.Add(("enter elevator + move", () => EndlessProbe.EnterAndMove()));
            for (int i = 0; i < 16; i++) steps.Add(("wait / pick card", EndlessProbe.PickCard));
            steps.Add(("run", () => { MelonCoroutines.Start(_fight ? RunFight() : Run(boss)); }));
            for (int i = 0; i < 400; i++) steps.Add(("wait", () => { if (_done) TestMode.SkipWaitsForTest(); }));
            steps.Add(("cleanup", (Action)(() =>
            {
                try { File.Delete(Path.Combine(Main.Dir, "endless_boss.txt")); } catch { }
                EndlessBoss.ForceNormalForTest = false;
                EndlessBoss.ForceFloorForTest = -1;
            })));
            return steps;
        }

        // ---------- 撃ち合いの測定（perffight）：ボス＋雑魚の前に無敵で立ち、自分も撃ちながら、切り替えて比べる ----------
        static bool _fireOn = true, _fightRun, _turnEachShot = true;
        static double _fireMs, _hitMs; static int _fireN;

        static void SetField(string type, string name, object v)
        {
            var t = Type.GetType(type);
            var f = t?.GetField(name);
            if (f != null) f.SetValue(null, v); else Main.Log($"[perf] {type}.{name} not there");
        }

        static Il2CppBallistics.LivingEntityCollider NearestPart(Vector3 from)
        {
            Il2CppBallistics.LivingEntityCollider best = null; float d = 1e9f;
            foreach (var e in Main.FindAll<Il2CppBallistics.LivingEntity>())
            {
                if (e == null || e.EnemyHealth == null || e.IsDead || e.EnemyHealth.health <= 0f || !e.gameObject.activeInHierarchy) continue;
                float dist = (e.transform.position - from).sqrMagnitude;
                if (dist >= d) continue;
                foreach (var c in e.GetComponentsInChildren<Il2CppBallistics.LivingEntityCollider>(true))
                    if (c.gameObject.name.StartsWith("spine_02")) { best = c; d = dist; break; }
            }
            return best;
        }

        // 1 秒に 10 発：本物の弾（マズルの処理）と、近い敵の胸への当たり（血・ヒットマーカー）。ダメージは 0 に近くして敵を減らさない
        static IEnumerator FireLoop()
        {
            var target = (Il2CppBallistics.LivingEntityCollider)null;
            float nextFind = 0f;
            while (_fightRun)
            {
                yield return new WaitForSeconds(0.1f);
                if (!_fireOn) continue;
                try
                {
                    var fps = Refs.Fps;
                    var item = fps?.GetActiveItem();
                    var dw = item?.gameObject?.GetComponent<Il2CppDemo.Scripts.Runtime.Item.Weapon>();
                    var bw = item?.gameObject?.GetComponentInChildren<Il2CppBallistics.Weapon>(true);
                    if (dw == null || bw == null) continue;
                    bool find = nextFind == 0f;   // 探すのは重いので最初の 1 回だけ（測定の邪魔にならないように）
                    if (find) { target = NearestPart(fps.transform.position); nextFind = 1f; }
                    var c = target;
                    if (c != null && (find || _turnEachShot)) { var flat = c.transform.position - fps.transform.position; flat.y = 0; fps.transform.rotation = Quaternion.LookRotation(flat); }
                    float dmg = bw.MuzzleDamage; bw.MuzzleDamage = 0.01f;
                    long t0 = Stopwatch.GetTimestamp();
                    dw.hitManager.Fire();
                    long t1 = Stopwatch.GetTimestamp();
                    if (c != null) TestMode.Call(Il2CppBallistics.BulletHandler.instance, "BulletHit", bw, c, null, (c.transform.position - fps.transform.position).normalized, c.transform.position, bw.MaxBulletSpeed, 0f);
                    long t2 = Stopwatch.GetTimestamp();
                    bw.MuzzleDamage = dmg;
                    _fireMs += (t1 - t0) * 1000.0 / Stopwatch.Frequency; _hitMs += (t2 - t1) * 1000.0 / Stopwatch.Frequency; _fireN++;
                }
                catch (Exception e) { Core.LogOnce("perffire", "[perf] fire failed: " + e); }
            }
        }

        // シーンに居るスクリプトの Update / LateUpdate / FixedUpdate をすべて測る（どれが重いかを広く見る）
        static void InstallSceneTimers()
        {
            var h = new HarmonyLib.Harmony("fprarity.perfscenetimers");
            var byName = new Dictionary<string, Type>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!a.GetName().Name.StartsWith("Assembly-CSharp") && !a.GetName().Name.StartsWith("Il2Cpp") && !a.GetName().Name.Contains("RayFire") && !a.GetName().Name.Contains("Kinemation")) continue;
                Type[] ts;
                try { ts = a.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { ts = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in ts) byName[t.FullName] = t;
            }
            var seen = new HashSet<string>();
            int n = 0;
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null) continue;
                var full = mb.GetIl2CppType().FullName;
                if (full == null || !seen.Add(full)) continue;
                if (!byName.TryGetValue("Il2Cpp" + full, out var t) && !byName.TryGetValue("Il2Cpp." + full, out t)) continue;
                foreach (var m in new[] { "Update", "LateUpdate", "FixedUpdate" })
                    if (HarmonyLib.AccessTools.DeclaredMethod(t, m) != null && SafePatcher.Patch(h, t, m, null, nameof(TimerPre), nameof(TimerPost), typeof(PerfProbe))) n++;
            }
            Main.Log($"[perf] scene timers: {n} methods on {seen.Count} script types");
        }

        static void InstallModTimers()
        {
            var h = new HarmonyLib.Harmony("fprarity.perfmodtimers");
            var pre = new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(TimerPre));
            var post = new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(TimerPost));
            foreach (var (type, names) in new[]
            {
                ("FPCustom.Main, FPCustom", "OnUpdate"), ("FPModOption.ModOption, FPModOption", "OnUpdate"), ("FPRarity.Main, FPRarity", "OnUpdate"),
                ("FPSurvival.Main, FPSurvival", "OnUpdate"), ("FPHitMarker.Main, FPHitMarker", "OnUpdate"), ("FPOptimize.Main, FPOptimize", "OnUpdate"),
                ("FPCustom.Effects, FPCustom", "TickVgrip,TickKacSway,SyncMags,Tick"), ("FPCustom.LaserLab, FPCustom", "TickDot"), ("FPCustom.CustomUi, FPCustom", "Tick"),
                ("FPHitMarker.Marker, FPHitMarker", "Show,Tick"), ("FPOptimize.AiSight, FPOptimize", "Pre"),
                ("Il2CppBallistics.BulletHandler, Assembly-CSharp", "BulletHit,SpawnBloodDecals,UpdateBullets"), ("Il2Cpp.FPSFireManager, Assembly-CSharp", "Fire"),
                ("Il2CppBallistics.LivingEntity, Assembly-CSharp", "OnHealthChanged"), ("Il2CppBallistics.Weapon, Assembly-CSharp", "ShootBullet"),
                ("FPHitMarker.Hits, FPHitMarker", "Pre,Post"), ("Il2CppRayFire.RayfireRigid, Il2CppRayFireAssembly", "Demolish,ApplyDamage,Initialize,InitMeshFragments,Slice"), ("FPCustom.Effects, FPCustom", "BulletHit_Pre,BulletHit_Post"),
                ("FPRarity.Mastery, FPRarity", "Flush"), ("FPRarity.Persistence, FPRarity", "Poll"),
                ("Il2CppRayFire.RayfireGun, Il2CppRayFireAssembly", "ImpactRigid,ImpactRoot,ApplyDamage,VfxFlash,VfxDebris,VfxDust"),
            })
                foreach (var n in names.Split(','))
                {
                    var t = Type.GetType(type);
                    var m = t == null ? null : HarmonyLib.AccessTools.Method(t, n);
                    if (m == null) { Main.Log($"[perf] timer: {type} {n} not found"); continue; }
                    try { h.Patch(m, pre, post); } catch (Exception e) { Main.Log($"[perf] timer {n} failed: {e.Message}"); }
                }
            foreach (var m in typeof(Physics).GetMethods().Where(x => x.Name == "RaycastAll"))
                try { h.Patch(m, pre, post); } catch { }
            foreach (var (tn, mn) in new[] { ("Il2CppBallistics.BulletHandler, Assembly-CSharp", "DeactivateBullet"), ("Il2Cpp.PoolManager, Assembly-CSharp", "AddObject"), ("Il2Cpp.PoolManager, Assembly-CSharp", "GetObject") })
            {
                var t = Type.GetType(tn); var m = t == null ? null : HarmonyLib.AccessTools.Method(t, mn);
                if (m != null) try { h.Patch(m, pre, post); } catch { } else Main.Log($"[perf] timer: {tn} {mn} not found");
            }
            // RayfireGun.Shoot は 2 つある（同じ名前で引数が違う）
            var rg = Type.GetType("Il2CppRayFire.RayfireGun, Il2CppRayFireAssembly");
            if (rg != null)
                foreach (var m in HarmonyLib.AccessTools.GetDeclaredMethods(rg).Where(x => x.Name == "Shoot"))
                {
                    try { h.Patch(m, pre, post); } catch (Exception e) { Main.Log($"[perf] timer Shoot failed: {e.Message}"); }
                }
        }

        static IEnumerator RunFight()
        {
            _done = false;
            var player = UnityEngine.Object.FindObjectsOfType<Il2CppBallistics.LivingEntity>().FirstOrDefault(e => e.PlayerHealthManager != null);
            if (player != null) player.PlayerHealthManager.invincible = true;
            EndlessProbe.PlaceInFrontOfBossesForTest(killGrunts: false);
            StartPhaseStamps();
            for (int i = 0; i < 4; i++) { Main.Log($"[perf] waiting enemies alive={AliveEnemies()}"); yield return new WaitForSeconds(2f); }
            Inventory("start");
            int cap = Application.targetFrameRate;
            Application.targetFrameRate = -1;
            _fightRun = true; _fireOn = true;
            MelonCoroutines.Start(FireLoop());
            var dots = UnityEngine.Object.FindObjectsOfType<LaserPoint>(true);
            Main.Log($"[perf] fight: laser points {dots.Length}, weapon {Refs.Fps?.GetActiveItem()?.name}");
            const string Cust = "FPCustom.Effects, FPCustom", Hit = "FPHitMarker.Marker, FPHitMarker";
            var exps = new (string name, Action on, Action off)[]
            {
                ("turn with every shot", () => _turnEachShot = true, () => _turnEachShot = false),
                ("hit marker off", () => SetField(Hit, "OffForTest", true), () => SetField(Hit, "OffForTest", false)),
                ("FPCustom tick off", () => SetField(Cust, "TickOffForTest", true), () => SetField(Cust, "TickOffForTest", false)),
                ("FPRarity tick off", () => SkipModTick = true, () => SkipModTick = false),
                ("laser point off", () => { foreach (var d in dots) if (d != null) d.gameObject.SetActive(false); }, () => { foreach (var d in dots) if (d != null) d.gameObject.SetActive(true); }),
                ("player not firing", () => _fireOn = false, () => _fireOn = true),
                ("render scale x0.5", () => { var u = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>(); if (u != null) u.renderScale *= 0.5f; }, () => { var u = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>(); if (u != null) u.renderScale *= 2f; }),
            };
            InstallModTimers();
            InstallSceneTimers();
            Main.Log($"[perf] physics auto sync = {Physics.autoSyncTransforms}, incremental gc {UnityEngine.Scripting.GarbageCollector.isIncremental}, mode {UnityEngine.Scripting.GarbageCollector.GCMode}, mono heap {System.GC.GetTotalMemory(false) / 1048576} MB, mono gc {System.GC.CollectionCount(0)}/{System.GC.CollectionCount(1)}");
            // 敵が減ると比べられないので、倒れないようにする
            foreach (var e in Main.FindAll<Il2CppBallistics.LivingEntity>())
                if (e != null && e.EnemyHealth != null && e.EnemyHealth.health > 0f) { e.StartHealth = 1e7f; e.Health = 1e7f; e.EnemyHealth.health = 1e7f; }
            for (int i = 0; i < 3; i++) yield return Measure("fight baseline", 6f);
            var abFile = Path.Combine(Main.Dir, "perf_ab.txt");
            string only = File.Exists(abFile) ? File.ReadAllText(abFile).Trim() : null;   // 中身があれば、その名前を含む切り替えだけ（| で複数）
            for (int round = 0; only != null && round < 3; round++)
                foreach (var (name, on, off) in exps)
                {
                    if (only.Length > 0 && !only.Split('|').Any(name.Contains)) continue;
                    try { on(); } catch (Exception e) { Main.Log($"[perf] {name} failed: {e.Message}"); }
                    yield return new WaitForSeconds(0.5f);
                    yield return Measure(name, 6f);
                    try { off(); } catch (Exception e) { Main.Log($"[perf] {name} failed: {e.Message}"); }
                    yield return new WaitForSeconds(0.5f);
                    yield return Measure("fight baseline", 6f);
                }
            _timed.Clear(); _fireMs = _hitMs = 0; _fireN = 0;
            yield return Measure("with method timers", 15f);
            ReportTimers(15f);
            Main.Log($"[perf] per shot: Fire {_fireMs / Math.Max(1, _fireN):0.00} ms, BulletHit call {_hitMs / Math.Max(1, _fireN):0.00} ms ({_fireN} shots)");
            _fightRun = false;
            Main.Log($"[perf] enemies alive at the end: {AliveEnemies()}");
            Application.targetFrameRate = cap;
            Inventory("end");
            _done = true;
        }

        static IEnumerator Run(bool boss)
        {
            _done = false;
            var player = UnityEngine.Object.FindObjectsOfType<Il2CppBallistics.LivingEntity>().FirstOrDefault(e => e.PlayerHealthManager != null);
            if (player != null) player.PlayerHealthManager.invincible = true;
            Main.Log($"[perf] floor boss={EndlessBoss.IsBossFloor} floor={EndlessBoss.Floor} player={player != null}");
            if (boss) { EndlessProbe.PlaceInFrontOfBossesForTest(killGrunts: false); }
            StartPhaseStamps();
            Settings();

            // 敵が出そろうまで待つ（通常の階はウェーブで湧く）
            for (int i = 0; i < (boss ? 4 : 25); i++) { Main.Log($"[perf] waiting enemies alive={AliveEnemies()}"); yield return new WaitForSeconds(2f); }
            Inventory("start");
            try { EnumTimings(); } catch (Exception e) { Main.Log("[perf] enum failed: " + e); }

            var urp = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
            var exps = new List<(string name, Action on, Action off)>();
            float rs = urp != null ? urp.renderScale : 1f;
            float sd = urp != null ? urp.shadowDistance : 0f;
            exps.Add(("render scale x0.5 (GPU bound?)", () => { if (urp != null) urp.renderScale = rs * 0.5f; }, () => { if (urp != null) urp.renderScale = rs; }));
            exps.Add(("FPRarity tick off", () => SkipModTick = true, () => SkipModTick = false));
            exps.Add(("enemy animators off", () => SetEnemyAnimators(false), () => SetEnemyAnimators(true)));
            exps.Add(("steam audio off", () => SetBehaviours(IsAudioFx, false), () => SetBehaviours(IsAudioFx, true)));
            exps.Add(("FIMSpace optimizers off", () => SetBehaviours(IsFim, false), () => SetBehaviours(IsFim, true)));
            exps.Add(("puppetmaster off", () => SetBehaviours(IsPuppet, false), () => SetBehaviours(IsPuppet, true)));
            exps.Add(("post volumes off", () => SetVolumes(false), () => SetVolumes(true)));

            // FPOptimize の有無の比較（perf_opt.txt があるとき）：同じ場面で ON / OFF を交互に測る
            var optField = Type.GetType("FPOptimize.Main, FPOptimize")?.GetField("Enabled");
            if (optField != null && File.Exists(Path.Combine(Main.Dir, "perf_opt.txt")))
            {
                int cap0 = Application.targetFrameRate;
                for (int round = 0; round < 2; round++)
                    foreach (var on in new[] { true, false })
                    {
                        optField.SetValue(null, on);
                        yield return new WaitForSeconds(1f);
                        yield return Measure($"FPOptimize {(on ? "ON" : "OFF")} (game fps cap)", 10f);
                    }
                Application.targetFrameRate = -1;
                for (int round = 0; round < 2; round++)
                    foreach (var on in new[] { true, false })
                    {
                        optField.SetValue(null, on);
                        yield return new WaitForSeconds(1f);
                        yield return Measure($"FPOptimize {(on ? "ON" : "OFF")} (uncapped)", 12f);
                    }
                optField.SetValue(null, true);
                Application.targetFrameRate = cap0;
                Inventory("end");
                _done = true;
                yield break;
            }

            yield return Measure("baseline (game fps cap)", 8f);
            // 以降はフレームレートの上限を外して、本当の余力で比べる（最後に戻す）
            int cap = Application.targetFrameRate;
            Application.targetFrameRate = -1;
            Array.Clear(_subSum); Array.Clear(_subMax); Array.Clear(_subSlow);
            int f0 = Time.frameCount;
            yield return Measure("baseline", 8f);
            ReportSubs(Time.frameCount - f0);
            // 長引いたフレームで何をしているかを調べる（ゲームを止めて調べるため固まることがある。perf_sample.txt があるときだけ）
            if (File.Exists(Path.Combine(Main.Dir, "perf_sample.txt")))
            {
                SpikeSampler.InitOnMainThread();
                try { HookUnityLog(); } catch (Exception e) { Main.Log("[perf] log hook failed: " + e.Message); }
                _logs.Clear();
                SpikeSampler.Start(6);
                yield return Measure("sampling (uncapped)", 20f);
                SpikeSampler.StopAndReport(boss ? "boss floor" : "normal floor");
                ReportLogs("during sampling, 20s");
            }
            InstallTimers();
            _timed.Clear();
            yield return Measure("with method timers", 20f);
            ReportTimers(20f);
            if (!File.Exists(Path.Combine(Main.Dir, "perf_ab.txt"))) exps.Clear();   // 切り分けの入れ替えは指定したときだけ
            foreach (var (name, on, off) in exps)
            {
                try { on(); } catch (Exception e) { Main.Log($"[perf] {name} on failed: {e.Message}"); }
                yield return new WaitForSeconds(0.5f);
                yield return Measure(name);
                try { off(); } catch (Exception e) { Main.Log($"[perf] {name} off failed: {e.Message}"); }
                yield return new WaitForSeconds(0.5f);
                yield return Measure("baseline");
            }
            Application.targetFrameRate = cap;
            Inventory("end");
            ScreenCapture.CaptureScreenshot(Path.Combine(Main.Dir, "shots", $"perf_{(boss ? "boss" : "normal")}.png"));
            _done = true;
        }

        // ---------- 測定 ----------

        // 1フレームごとの記録（カクつきの原因を探すため）
        struct Frame { public float T, Dt; public double Upd, Late, Rest, Mod; public int Gc; }

        static IEnumerator Measure(string label, float seconds = 5f)
        {
            var frames = new List<Frame>();
            double mainMs = 0, renderMs = 0, gpuMs = 0, waitMs = 0; int ft = 0, ftCalls = 0;
            double mod0 = ModTickMs;
            var ftArr = new Il2CppStructArray<FrameTiming>(1);
            int gc0 = Il2CppSystem.GC.CollectionCount(0), gcPrev = gc0;
            yield return null;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < seconds)
            {
                yield return null;
                int gc = Il2CppSystem.GC.CollectionCount(0);
                frames.Add(new Frame { T = Time.realtimeSinceStartup - t0, Dt = Time.unscaledDeltaTime, Upd = _upd, Late = _late, Rest = _rest, Mod = LastModTickMs, Gc = gc - gcPrev });
                gcPrev = gc;
                try
                {
                    FrameTimingManager.CaptureFrameTimings();
                    ftCalls++;
                    if (FrameTimingManager.GetLatestTimings(1, ftArr) > 0)
                    {
                        var f = ftArr[0];
                        mainMs += f.cpuMainThreadFrameTime; renderMs += f.cpuRenderThreadFrameTime; gpuMs += f.gpuFrameTime; waitMs += f.cpuMainThreadPresentWaitTime; ft++;
                    }
                }
                catch (Exception e) { if (ftCalls < 2) Main.Log("[perf] frame timing failed: " + e.Message); }
            }
            if (frames.Count == 0) yield break;
            var dts = frames.Select(f => f.Dt).OrderBy(d => d).ToList();
            float avg = dts.Average();
            float med = dts[dts.Count / 2];
            float low1 = dts.Skip((int)(dts.Count * 0.99f)).DefaultIfEmpty(dts.Last()).Average();
            var spikes = frames.Where(f => f.Dt > Math.Max(med * 2.5f, 0.016f)).ToList();
            var sb = new StringBuilder($"[perf] {label,-30} fps={1f / avg,6:0.0} med={1f / med,6:0.0} 1%low={1f / low1,6:0.0} max={dts.Last() * 1000f:0}ms frames={frames.Count} spikes={spikes.Count} gc={gcPrev - gc0} enemies={AliveEnemies()}");
            if (ft > 0) sb.Append($" | ftm main={mainMs / ft:0.00} render={renderMs / ft:0.00} gpu={gpuMs / ft:0.00} presentWait={waitMs / ft:0.00}");
            else sb.Append(" | ftm none");
            sb.Append($" | phase upd->late={frames.Average(f => f.Upd):0.00} late->eof={frames.Average(f => f.Late):0.00} eof->upd={frames.Average(f => f.Rest):0.00}");
            sb.Append($" | modTick={(ModTickMs - mod0) / frames.Count:0.000}ms/f max={frames.Max(f => f.Mod):0.00}");
            Main.Log(sb.ToString());
            // カクついたフレームの内訳（どの区間が長いか、GC が走ったか）
            foreach (var f in spikes.Take(12))
                Main.Log($"[perf]    spike t={f.T:0.00}s dt={f.Dt * 1000f:0.0}ms upd->late={f.Upd:0.0} late->eof={f.Late:0.0} eof->upd={f.Rest:0.0} mod={f.Mod:0.00} gc={f.Gc}");
        }

        // フレーム内の区間（Update → LateUpdate → 描画終わり → 次の Update）を測る
        static readonly Stopwatch _sw = Stopwatch.StartNew();
        static double _tUpd, _tLate, _tEof, _upd, _late, _rest;
        static bool _phaseValid, _phaseStarted;

        static void StartPhaseStamps()
        {
            if (_phaseStarted) return;
            _phaseStarted = true;
            MelonCoroutines.Start(EndOfFrameLoop());
        }

        public static void OnUpdateStamp()
        {
            if (!_phaseStarted) return;
            SpikeSampler.Mark(1);
            double now = _sw.Elapsed.TotalMilliseconds;
            if (_tEof > 0 && _tLate > 0) { _upd = _tLate - _tUpd; _late = _tEof - _tLate; _rest = now - _tEof; _phaseValid = _tUpd > 0; }
            _tUpd = now;
        }

        public static void OnLateUpdateStamp() { if (_phaseStarted) { SpikeSampler.Mark(2); _tLate = _sw.Elapsed.TotalMilliseconds; } }

        static IEnumerator EndOfFrameLoop()
        {
            var eof = new WaitForEndOfFrame();
            while (true) { yield return eof; SpikeSampler.Mark(3); _tEof = _sw.Elapsed.TotalMilliseconds; }
        }

        // ---------- 状態の書き出し ----------

        static int AliveEnemies() =>
            UnityEngine.Object.FindObjectsOfType<Il2CppEnemyAI.EnemyHealth>().Count(e => e.health > 0f);

        static void Settings()
        {
            var sb = new StringBuilder("[perf] SETTINGS ");
            sb.Append($"screen={Screen.width}x{Screen.height}@{Screen.currentResolution.refreshRateRatio.value:0} full={Screen.fullScreenMode} vsync={QualitySettings.vSyncCount} targetFps={Application.targetFrameRate} ");
            sb.Append($"quality={QualitySettings.GetQualityLevel()}/{string.Join(",", QualitySettings.names)} lodBias={QualitySettings.lodBias} maxLod={QualitySettings.maximumLODLevel} pixelLights={QualitySettings.pixelLightCount} ");
            sb.Append($"aniso={QualitySettings.anisotropicFiltering} skinWeights={QualitySettings.skinWeights} texLimit={QualitySettings.globalTextureMipmapLimit} realtimeReflProbes={QualitySettings.realtimeReflectionProbes} softParticles={QualitySettings.softParticles} ");
            sb.Append($"fixedDt={Time.fixedDeltaTime} maxDt={Time.maximumDeltaTime} timeScale={Time.timeScale} simMode={Physics.simulationMode} autoSyncTransforms={Physics.autoSyncTransforms} solverIters={Physics.defaultSolverIterations} ");
            sb.Append($"gfx={SystemInfo.graphicsDeviceName} api={SystemInfo.graphicsDeviceType} jobs={SystemInfo.processorCount} gfxJobs={SystemInfo.renderingThreadingMode} frameTiming={FrameTimingManager.IsFeatureEnabled()} ");
            var urp = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
            if (urp != null)
                sb.Append($"| URP '{urp.name}' scale={urp.renderScale} upscale={urp.upscalingFilter} msaa={urp.msaaSampleCount} hdr={urp.supportsHDR} depthTex={urp.supportsCameraDepthTexture} opaqueTex={urp.supportsCameraOpaqueTexture} " +
                          $"shadowDist={urp.shadowDistance} cascades={urp.shadowCascadeCount} mainShadowRes={urp.mainLightShadowmapResolution} mainShadows={urp.supportsMainLightShadows} " +
                          $"addLights={urp.additionalLightsRenderingMode} addMax={urp.maxAdditionalLightsCount} addShadows={urp.supportsAdditionalLightShadows} addShadowRes={urp.additionalLightsShadowmapResolution} softShadows={urp.supportsSoftShadows} " +
                          $"srpBatcher={urp.useSRPBatcher} dynBatch={urp.supportsDynamicBatching} lodCrossFade={urp.enableLODCrossFade} colorGrading={urp.colorGradingMode} lutSize={urp.colorGradingLutSize}");
            Main.Log(sb.ToString());
            foreach (var cam in Camera.allCameras)
            {
                var d = cam.GetComponent<UniversalAdditionalCameraData>();
                Main.Log($"[perf] camera '{PathOf(cam.transform)}' depth={cam.depth} far={cam.farClipPlane} near={cam.nearClipPlane} mask=0x{cam.cullingMask:X} target={(cam.targetTexture != null ? cam.targetTexture.width + "x" + cam.targetTexture.height : "-")} " +
                         $"occl={cam.useOcclusionCulling} hdr={cam.allowHDR} msaa={cam.allowMSAA} " +
                         (d != null ? $"type={d.renderType} post={d.renderPostProcessing} aa={d.antialiasing} shadows={d.renderShadows} depthTex={d.requiresDepthTexture} opaqueTex={d.requiresColorTexture} stack={d.cameraStack?.Count}" : "no urp data"));
            }
        }

        static void Inventory(string when)
        {
            var sb = new StringBuilder($"[perf] INVENTORY ({when}) scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}\n");
            try
            {
                var anims = UnityEngine.Object.FindObjectsOfType<Animator>();
                sb.AppendLine($"  animators={anims.Length} enabled={anims.Count(a => a.enabled)} cull=" + string.Join(",", anims.GroupBy(a => a.cullingMode.ToString()).Select(g => $"{g.Key}:{g.Count()}")) + " updateMode=" + string.Join(",", anims.GroupBy(a => a.updateMode.ToString()).Select(g => $"{g.Key}:{g.Count()}")));
                var smr = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>();
                sb.AppendLine($"  skinned={smr.Length} visible={smr.Count(s => s.isVisible)} offscreenUpdate={smr.Count(s => s.updateWhenOffscreen)} quality=" + string.Join(",", smr.GroupBy(s => s.quality.ToString()).Select(g => $"{g.Key}:{g.Count()}")));
                var rends = UnityEngine.Object.FindObjectsOfType<Renderer>();
                sb.AppendLine($"  renderers={rends.Length} visible={rends.Count(r => r.isVisible)} castShadows={rends.Count(r => r.enabled && r.shadowCastingMode != ShadowCastingMode.Off)}");
                var lights = UnityEngine.Object.FindObjectsOfType<Light>();
                sb.AppendLine($"  lights={lights.Length} enabled={lights.Count(l => l.enabled)} " + string.Join(",", lights.Where(l => l.enabled).GroupBy(l => $"{l.type}/{l.shadows}").Select(g => $"{g.Key}:{g.Count()}")));
                var ps = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
                sb.AppendLine($"  particles={ps.Length} playing={ps.Count(p => p.isPlaying)} alive={ps.Sum(p => p.particleCount)}");
                var au = UnityEngine.Object.FindObjectsOfType<AudioSource>();
                sb.AppendLine($"  audioSources={au.Length} playing={au.Count(a => a.isPlaying)} spatialize={au.Count(a => a.spatialize)}");
                sb.AppendLine($"  navAgents={UnityEngine.Object.FindObjectsOfType<UnityEngine.AI.NavMeshAgent>().Length} rigidbodies={UnityEngine.Object.FindObjectsOfType<Rigidbody>().Length} (nonKinematic={UnityEngine.Object.FindObjectsOfType<Rigidbody>().Count(r => !r.isKinematic && !r.IsSleeping())}) colliders={UnityEngine.Object.FindObjectsOfType<Collider>().Length} reflProbes={UnityEngine.Object.FindObjectsOfType<ReflectionProbe>().Count(p => p.mode == ReflectionProbeMode.Realtime)} realtime");
                sb.AppendLine($"  canvases={UnityEngine.Object.FindObjectsOfType<Canvas>().Length} gameObjects={UnityEngine.Object.FindObjectsOfType<Transform>().Length}");
                // 有効なスクリプトの種類ごとの数（毎フレーム動くものの候補）
                var mbs = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                sb.AppendLine($"  monobehaviours={mbs.Length} enabled={mbs.Count(m => m.isActiveAndEnabled)} by type (top 60):");
                foreach (var g in mbs.Where(m => m.isActiveAndEnabled).GroupBy(TypeName).OrderByDescending(g => g.Count()).Take(60))
                    sb.AppendLine($"    {g.Count(),4}  {g.Key}");
            }
            catch (Exception e) { sb.AppendLine("  inventory failed: " + e); }
            Main.Log(sb.ToString());
        }

        static string TypeName(UnityEngine.Object o)
        {
            try { return o.GetIl2CppType().FullName; } catch { return "?"; }
        }

        static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;

        // ---------- 切り分け用の一時変更 ----------

        static readonly List<Behaviour> _off = new();

        static void SetEnemyAnimators(bool on)
        {
            if (on) { foreach (var b in _off) if (b != null) b.enabled = true; _off.Clear(); return; }
            foreach (var e in UnityEngine.Object.FindObjectsOfType<Il2CppEnemyAI.EnemyHealth>())
            {
                var root = e.transform.parent != null ? e.transform.parent : e.transform;
                foreach (var a in root.GetComponentsInChildren<Animator>()) if (a.enabled) { a.enabled = false; _off.Add(a); }
            }
            Main.Log($"[perf] enemy animators disabled: {_off.Count}");
        }

        static bool IsAudioFx(string t) => t.Contains("SteamAudio") || t.Contains("Phonon");
        static bool IsFim(string t) => t.StartsWith("FIMSpace.");
        static bool IsPuppet(string t) => t.StartsWith("RootMotion.");

        static readonly Dictionary<string, List<Behaviour>> _offBy = new();

        static void SetBehaviours(Func<string, bool> match, bool on)
        {
            var key = match.Method.Name;
            if (on)
            {
                if (_offBy.TryGetValue(key, out var l)) foreach (var b in l) if (b != null) b.enabled = true;
                _offBy.Remove(key);
                return;
            }
            var list = new List<Behaviour>();
            foreach (var m in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (m.enabled && match(TypeName(m))) { m.enabled = false; list.Add(m); }
            _offBy[key] = list;
            Main.Log($"[perf] disabled {list.Count} behaviours ({key})");
        }

        static readonly List<Volume> _vols = new();

        static void SetVolumes(bool on)
        {
            if (on) { foreach (var v in _vols) if (v != null) v.enabled = true; _vols.Clear(); return; }
            foreach (var v in UnityEngine.Object.FindObjectsOfType<Volume>()) if (v.enabled) { v.enabled = false; _vols.Add(v); }
            Main.Log($"[perf] volumes disabled: {_vols.Count}");
        }

        static readonly Dictionary<IntPtr, float> _far = new();

        static void SetFar(float far)
        {
            foreach (var c in Camera.allCameras)
            {
                if (far < 0) { if (_far.TryGetValue(c.Pointer, out var f)) c.farClipPlane = f; }
                else { _far[c.Pointer] = c.farClipPlane; c.farClipPlane = Mathf.Min(far, c.farClipPlane); }
            }
            if (far < 0) _far.Clear();
        }
    }
}
