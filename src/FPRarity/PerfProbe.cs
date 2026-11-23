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
        static readonly Dictionary<string, Stat> _setActive = new();
        public static bool RicAB, NoRic;   // perfreal_ric：跳弾あり/なしを切り替えて比べる（なしは測定のためだけ）
        static bool _inUB; static long _ubT0;
        static readonly double[] _ubMs = new double[2]; static readonly long[] _ubCalls = new long[2], _ricChecks = new long[2], _spawnsInUB = new long[2], _lookRot = new long[2], _shotsBy = new long[2];
        static int RicIdx => NoRic ? 1 : 0;
        static void UBPre() { _inUB = true; _ubT0 = Stopwatch.GetTimestamp(); }
        static void UBPost() { _inUB = false; int i = RicIdx; _ubMs[i] += (Stopwatch.GetTimestamp() - _ubT0) * 1000.0 / Stopwatch.Frequency; _ubCalls[i]++; }
        static void EvalPost(ref float __result) { if (!_inUB) return; _ricChecks[RicIdx]++; if (NoRic) __result = 0f; }
        static void SpawnInUBPost() { if (_inUB) _spawnsInUB[RicIdx]++; }
        static void LookRotPost() { if (_inUB) _lookRot[RicIdx]++; }
        public static bool AB;   // perfreal_ab：FPOptimize の音の部品の先止めを 5 秒ごとにオン/オフして比べる
        public static string OffFirst;   // 試し：消す前にこの部品だけ先に止める（AudioReverbFilter / SteamAudioSource / AudioSource）
        static bool _dmgDead;
        static void DmgPre(Component __instance, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            try { _dmgDead = __instance.Cast<Il2CppEnemyAI.EnemyHealth>().health <= 0f; } catch { _dmgDead = false; }
        }
        static void DmgPost(long __state)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            string key = "EnemyHealth.TakeDamage " + (_dmgDead ? "[already dead]" : "[alive]");
            if (!_timed.TryGetValue(key, out var st)) _timed[key] = st = new Stat();
            st.Sum += ms; st.Calls++; if (ms > st.Max) st.Max = ms; if (ms > 2) st.Slow++;
        }

        static void SetActivePre(out long __state, GameObject __instance, bool value)
        {
            __state = Stopwatch.GetTimestamp();
            if (OffFirst == null || value) return;
            try
            {
                foreach (var c in __instance.GetComponentsInChildren<Behaviour>(true))
                    if (c != null && c.enabled && OffFirst.Contains("|" + c.GetIl2CppType().Name + "|")) c.enabled = false;
            }
            catch { }
        }
        static void SetActivePost(long __state, GameObject __instance, bool value)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            string key;
            try { var p = __instance.transform.parent; key = (p != null ? p.name + "/" : "") + __instance.name + (value ? " ON" : " off");
                  if (!value && p != null && p.name == "HitEffectPool") { foreach (var a in __instance.GetComponents<Behaviour>()) if (a != null && a.GetIl2CppType().Name == "AudioSource") { key += a.enabled ? " [src on]" : " [src off]"; break; } } } catch { key = "?"; }
            if (!_setActive.TryGetValue(key, out var st)) _setActive[key] = st = new Stat();
            st.Sum += ms; st.Calls++; if (ms > st.Max) st.Max = ms; if (ms > 2) st.Slow++;
            if (ms > 5 && st.Slow == 1)
                try
                {
                    var sb = new StringBuilder($"[perf] slow SetActive {ms:0.0}ms {key} comps:");
                    foreach (var c in __instance.GetComponentsInChildren<Component>(true)) if (c != null) sb.Append(' ').Append(c.GetIl2CppType().Name);
                    Main.Log(sb.ToString());
                }
                catch { }
        }
        static void ReportSetActive(float seconds)
        {
            var sb = new StringBuilder($"[perf] SetActive by object over {seconds:0}s (total ms/s, calls, avg, max):\n");
            foreach (var kv in _setActive.OrderByDescending(k => k.Value.Sum).Take(25))
                sb.Append($"    {kv.Key,-60} {kv.Value.Sum / seconds,7:0.00} ms/s  calls={kv.Value.Calls,6}  avg={kv.Value.Sum / kv.Value.Calls:0.000}  max={kv.Value.Max:0.00}\n");
            Main.Log(sb.ToString());
        }
        static void TimerPre(out long __state) => __state = Stopwatch.GetTimestamp();

        static void TimerPost(long __state, System.Reflection.MethodBase __originalMethod)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            var key = __originalMethod.DeclaringType.FullName.Replace("Il2Cpp", "") + "." + __originalMethod.Name;
            if (ms > (Real ? 3 : 8) && __originalMethod.DeclaringType != typeof(UnityEngine.Physics)) Main.Log($"[perf] slow {key} {ms:0.0}ms t={Time.time:0.00} bullets={Il2CppBallistics.BulletHandler.instance?.Bullets?.Count} raycast this frame max {_rayMaxFrame:0.0}ms mono gc {System.GC.CollectionCount(0)}/{System.GC.CollectionCount(1)}/{System.GC.CollectionCount(2)} il2cpp gc {Il2CppSystem.GC.CollectionCount(0)}");
            if (__originalMethod.DeclaringType == typeof(UnityEngine.Physics)) { if (Time.frameCount != _rayFrame) { _rayFrame = Time.frameCount; _rayMaxFrame = 0; } if (ms > _rayMaxFrame) _rayMaxFrame = ms; }
            if (!_timed.TryGetValue(key, out var s)) _timed[key] = s = new Stat();
            s.Sum += ms; s.Calls++; if (ms > s.Max) s.Max = ms; if (ms > 2) s.Slow++;
        }

        static int _voicePlays, _voiceUnloaded;
        static void VoicePre(Il2CppSystem.Collections.Generic.List<AudioClip> soundList)
        {
            try
            {
                if (soundList == null || soundList.Count == 0) return;
                _voicePlays++;
                for (int i = 0; i < soundList.Count; i++) { var c = soundList[i]; if (c != null && c.loadState == AudioDataLoadState.Unloaded) { _voiceUnloaded++; Main.Log($"[perf] voice: unloaded clip {c.name} in the list being played"); break; } }
            }
            catch { }
        }
        static void VoiceChecks(string when)
        {
            var vt = Type.GetType("FPOptimize.VoicePreload, FPOptimize");
            Main.Log($"[test] voice preload ({when}): {vt?.GetMethod("Stats")?.Invoke(null, null)}; plays {_voicePlays}, lists with an unloaded clip {_voiceUnloaded}");
            var st = vt?.GetMethod("TestStats")?.Invoke(null, null) as long[];
            Main.Log($"[test] CHECK voice preload ran and enemy voices are loaded before they play ({when}): {(st != null && st[0] > 0 && st[1] > 0 && st[4] == 0 && _voicePlays > 0 && _voiceUnloaded == 0 ? "PASS" : "FAIL")}");
        }
        static readonly Dictionary<string, Stat> _finds = new();
        static void FindPost(long __state, System.Reflection.MethodBase __originalMethod, object[] __args)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            string arg = "";
            try { foreach (var a in __args) { if (a is Il2CppSystem.Type it) { arg = it.Name; break; } if (a is string s) { arg = s; break; } } } catch { }
            var key = __originalMethod.DeclaringType.Name.Replace("Il2Cpp", "") + "." + __originalMethod.Name + (arg != "" ? "<" + arg + ">" : "");
            foreach (var d in new[] { _finds, _timed })
            {
                if (!d.TryGetValue(key, out var s)) d[key] = s = new Stat();
                s.Sum += ms; s.Calls++; if (ms > s.Max) s.Max = ms; if (ms > 2) s.Slow++;
            }
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
            steps.Add(("run", () => { MelonCoroutines.Start(KillProbe ? RunKills() : _fight ? RunFight() : Run(boss)); }));
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
        public static bool Real;   // perfreal：敵を硬くしない・本当のダメージで撃って倒していく
        static int _kills;
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
                    // 本当の撃ち合い：狙った敵が倒れたら次の敵へ（探すのは倒れたときだけ）
                    if (Real && target != null && target.GetComponentInParent<Il2CppBallistics.LivingEntity>() is var le0 && (le0 == null || le0.IsDead || le0.EnemyHealth == null || le0.EnemyHealth.health <= 0f)) { target = null; nextFind = 0f; _kills++; }
                    bool find = nextFind == 0f;   // 探すのは重いので最初の 1 回だけ（測定の邪魔にならないように）
                    if (find) { target = NearestPart(fps.transform.position); nextFind = 1f; }
                    var c = target;
                    if (c != null && (find || _turnEachShot)) { var flat = c.transform.position - fps.transform.position; flat.y = 0; fps.transform.rotation = Quaternion.LookRotation(flat); }
                    float dmg = bw.MuzzleDamage; if (!Real) bw.MuzzleDamage = 0.01f;
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
                    if (HarmonyLib.AccessTools.DeclaredMethod(t, m) != null && SafePatcher.Patch(h, t, m, null, nameof(TimerPre), nameof(TimerPost), typeof(PerfProbe), gate: false)) n++;   // gate だと __state が壊れて毎回「遅い」と記録していた
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
            // 本当の撃ち合い：敵が倒れるときの処理を細かく（敵の体力・死亡・倒れた体・落とし物と、Mod の撃破時の処理）
            if (Real)
            {
                foreach (var tn in new[] { "Il2CppEnemyAI.EnemyHealth, Assembly-CSharp", "Il2CppBallistics.LivingEntity, Assembly-CSharp", "Il2CppRootMotion.Dynamics.PuppetMaster, Il2CppRootMotion",
                                           "Il2CppRootMotion.Dynamics.BehaviourPuppet, Il2CppRootMotion", "Il2CppEnemyAI.StateController, Assembly-CSharp", "Il2Cpp.EnemyDeathBounds, Assembly-CSharp",
                                           "Il2Cpp.LootSpawnPoint, Assembly-CSharp", "Il2CppImpactCFX.ImpactCFXManager, Il2CppImpactCFX.Runtime" })
                {
                    var full = tn.Split(',')[0];
                    var t = Type.GetType(tn) ?? AppDomain.CurrentDomain.GetAssemblies().Select(x => { try { return x.GetType(full); } catch { return null; } }).FirstOrDefault(x => x != null);
                    if (t == null) { Main.Log($"[perf] death timers: {tn} not found"); continue; }
                    int n = 0;
                    foreach (var m in HarmonyLib.AccessTools.GetDeclaredMethods(t))
                    {
                        if (m.IsSpecialName || m.IsAbstract || m.IsGenericMethod || m.ContainsGenericParameters || m.Name.StartsWith("<") || m.Name is "Update" or "LateUpdate" or "FixedUpdate" or "Equals" or "GetHashCode" or "ToString" or "Finalize") continue;
                        if (m.GetParameters().Any(pp => pp.ParameterType.IsByRef || pp.IsOut)) continue;
                        try { h.Patch(m, pre, post); n++; } catch { }
                    }
                    Main.Log($"[perf] death timers: {t.Name} {n} methods");
                }
                foreach (var (tn, mn) in new[] { ("FPRarity.Mastery, FPRarity", "OnEnemyKilled"), ("FPModOption.Slide, FPModOption", "OnHealthChanged_Pre"), ("FPSurvival.Patches, FPSurvival", "Drop_Post"),
                                                 ("Il2Cpp.Inventory, Assembly-CSharp", "SpawnDroppedObject"), ("Il2Cpp.AchievementUnlocker, Assembly-CSharp", "Unlock"),
                                                 ("Il2CppFPLeaderboard.Unity.LeaderboardService, Il2CppFPLeaderboard.Client", "EnsureActiveRun"), ("UnityEngine.PlayerPrefs, UnityEngine.CoreModule", "SetInt"),
                                                 ("UnityEngine.GameObject, UnityEngine.CoreModule", "Find"), ("Il2Cpp.QuestManager, Assembly-CSharp", "OnKillEnemy"), ("UnityEngine.GameObject, UnityEngine.CoreModule", "set_layer"),
                                                 ("UnityEngine.Transform, UnityEngine.CoreModule", "SetParent"),
                                                 ("UnityEngine.GameObject, UnityEngine.CoreModule", "FindGameObjectWithTag"), ("UnityEngine.GameObject, UnityEngine.CoreModule", "SendMessage"),
                                                 ("UnityEngine.PlayerPrefs, UnityEngine.CoreModule", "HasKey"), ("UnityEngine.PlayerPrefs, UnityEngine.CoreModule", "GetFloat"),
                                                 ("Il2Cpp.EnemySoundController, Assembly-CSharp", "PlaySound"), ("Il2Cpp.HealthBar, Assembly-CSharp", "TakeDamage"),
                                                 ("UnityEngine.Events.UnityEvent, UnityEngine.CoreModule", "Invoke"), ("UnityEngine.Animator, UnityEngine.AnimationModule", "GetBoneTransform"),
                                                 ("Il2Cpp.PlayerHealthManager, Assembly-CSharp", "Damage"),
                                                 ("Il2CppEnemyAI.EnemyHealth+_DieAdaptive_d__26, Assembly-CSharp", "MoveNext"), ("Il2CppEnemyAI.EnemyHealth+_DisablePhysics_d__28, Assembly-CSharp", "MoveNext"),
                                                 ("Il2CppEnemyAI.EnemyHealth+_DieAdaptive_d__26, Assembly-CSharp", "System_Runtime_CompilerServices_IAsyncStateMachine_MoveNext"),
                                                 ("Il2CppEnemyAI.EnemyHealth+_DisablePhysics_d__28, Assembly-CSharp", "System_Runtime_CompilerServices_IAsyncStateMachine_MoveNext") })
                {
                    var full = tn.Split(',')[0];
                    var t = Type.GetType(tn) ?? AppDomain.CurrentDomain.GetAssemblies().Select(x => { try { return x.GetType(full); } catch { return null; } }).FirstOrDefault(x => x != null);
                    // 同じ名前が複数あるときは全部に付ける
                    var ms = t == null ? new System.Reflection.MethodInfo[0] : HarmonyLib.AccessTools.GetDeclaredMethods(t).Where(x => x.Name == mn && !x.IsGenericMethod).ToArray();
                    if (ms.Length == 0) Main.Log($"[perf] timer: {tn} {mn} not found");
                    foreach (var m in ms) try { h.Patch(m, pre, post); } catch (Exception e) { Main.Log($"[perf] timer {mn} failed: {e.Message}"); }
                }
            }
            if (Real && !AB)
            {
                // SetActive：どの物の切り替えが重いか（名前・オン/オフごと）
                // 敵へのダメージ：生きている敵か、倒れた体か（倒れた体の部位）で分けて測る
                if (RicAB)
                {
                    try
                    {
                        h.Patch(HarmonyLib.AccessTools.Method(typeof(Il2CppBallistics.BulletHandler), "UpdateBullets"), new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(UBPre)), new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(UBPost)));
                        h.Patch(HarmonyLib.AccessTools.Method(typeof(AnimationCurve), "Evaluate", new[] { typeof(float) }), null, new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(EvalPost)));
                        h.Patch(HarmonyLib.AccessTools.Method(typeof(Il2CppBallistics.HitEffectPool), "Spawn"), null, new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(SpawnInUBPost)));
                        foreach (var lr in typeof(Quaternion).GetMethods().Where(x => x.Name == "LookRotation")) h.Patch(lr, null, new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(LookRotPost)));
                    }
                    catch (Exception e) { Main.Log("[perf] ricochet probe failed: " + e.Message); }
                }
                foreach (var tdm in HarmonyLib.AccessTools.GetDeclaredMethods(typeof(Il2CppEnemyAI.EnemyHealth)).Where(x => x.Name == "TakeDamage"))
                    try { h.Patch(tdm, new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(DmgPre)), new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(DmgPost))); } catch (Exception e) { Main.Log("[perf] dmg timer failed: " + e.Message); }
                var sa = HarmonyLib.AccessTools.Method(typeof(GameObject), "SetActive");
                try { h.Patch(sa, new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(SetActivePre)), new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(SetActivePost))); } catch (Exception e) { Main.Log("[perf] SetActive timer failed: " + e.Message); }
            }
            // シーン全体を探す呼び出し：何を何回・何 ms 探したか（型ごと）
            if (KillProbe)
            {
                var fpre = new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(TimerPre)); var fpost = new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(FindPost));
                int nf = 0;
                foreach (var m in typeof(UnityEngine.Object).GetMethods().Where(x => x.Name.StartsWith("Find") && !x.IsGenericMethod && x.GetParameters().Length > 0 && !x.Name.Contains("InstanceID")))
                    try { h.Patch(m, fpre, fpost); nf++; } catch (Exception e) { Main.Log($"[perf] find timer {m.Name} failed: {e.Message}"); }
                foreach (var m in typeof(GameObject).GetMethods().Where(x => x.Name.StartsWith("Find") && !x.IsGenericMethod))
                    try { h.Patch(m, fpre, fpost); nf++; } catch (Exception e) { Main.Log($"[perf] find timer {m.Name} failed: {e.Message}"); }
                foreach (var m in typeof(Resources).GetMethods().Where(x => x.Name == "FindObjectsOfTypeAll" && !x.IsGenericMethod))
                    try { h.Patch(m, fpre, fpost); nf++; } catch { }
                foreach (var (tn, mn) in new[] { ("Il2Cpp.LootableItem, Assembly-CSharp", "Start"), ("Il2CppEnemyAI.EnemyHealth+_DieAdaptive_d__32, Assembly-CSharp", "MoveNext"),
                                                 ("Il2CppEnemyAI.EnemyHealth, Assembly-CSharp", "Kill"), ("Il2Cpp.EnemyDeathBounds, Assembly-CSharp", "UpdateBounds") })
                {
                    var t = Type.GetType(tn); var ms = t == null ? new System.Reflection.MethodInfo[0] : HarmonyLib.AccessTools.GetDeclaredMethods(t).Where(x => x.Name == mn).ToArray();
                    if (ms.Length == 0) Main.Log($"[perf] find timer: {tn} {mn} not found");
                    foreach (var m in ms) try { h.Patch(m, fpre, fpost); nf++; } catch (Exception e) { Main.Log($"[perf] find timer {mn} failed: {e.Message}"); }
                }
                Main.Log($"[perf] find timers: {nf}");
            }
            // 敵の声：鳴らすときに、まだ読み込まれていない音がリストに残っているか（残っていれば、その場で読み込んで止まりうる）
            if (Real)
                try { h.Patch(HarmonyLib.AccessTools.Method(typeof(Il2Cpp.EnemySoundController), "PlaySound"), new HarmonyLib.HarmonyMethod(typeof(PerfProbe), nameof(VoicePre))); }
                catch (Exception e) { Main.Log("[perf] voice probe failed: " + e.Message); }
            if (Real)
                foreach (var m in typeof(UnityEngine.Debug).GetMethods().Where(x => x.Name is "Log" or "LogError" or "LogWarning" && x.GetParameters().Length == 1))
                    try { h.Patch(m, pre, post); } catch (Exception e) { Main.Log($"[perf] timer Debug.{m.Name} failed: {e.Message}"); }
            // RayfireGun.Shoot は 2 つある（同じ名前で引数が違う）
            var rg = Type.GetType("Il2CppRayFire.RayfireGun, Il2CppRayFireAssembly");
            if (rg != null)
                foreach (var m in HarmonyLib.AccessTools.GetDeclaredMethods(rg).Where(x => x.Name == "Shoot"))
                {
                    try { h.Patch(m, pre, post); } catch (Exception e) { Main.Log($"[perf] timer Shoot failed: {e.Message}"); }
                }
        }

        // ---------- 敵が倒れるときの重さ（killprobe）：1 体ずつ倒して、その呼び出しと続くフレームを測る。長いフレームは呼び出し履歴を取る ----------
        public static bool KillProbe;
        static IEnumerator RunKills()
        {
            _done = false;
            var player = UnityEngine.Object.FindObjectsOfType<Il2CppBallistics.LivingEntity>().FirstOrDefault(e => e.PlayerHealthManager != null);
            if (player != null) player.PlayerHealthManager.invincible = true;
            EndlessProbe.PlaceInFrontOfBossesForTest(killGrunts: false);
            StartPhaseStamps();
            yield return new WaitForSeconds(6f);
            int cap = Application.targetFrameRate;
            Application.targetFrameRate = -1;
            InstallModTimers();
            yield return Measure("before kills (no kill)", 3f);
            bool sample = File.Exists(Path.Combine(Main.Dir, "perf_sample.txt"));
            if (sample) { SpikeSampler.InitOnMainThread(); SpikeSampler.Start(15); }
            _timed.Clear();
            var list = Main.FindAll<Il2CppEnemyAI.EnemyHealth>().Where(e => e != null && e.health > 0f && e.gameObject.activeInHierarchy).OrderBy(e => e.health).Take(10).ToList();
            Main.Log($"[kill] {list.Count} enemies to kill");
            int i = 0;
            foreach (var e in list)
            {
                yield return null;
                if (e == null || e.health <= 0f) continue;
                string name = e.name; float hp = e.health;
                // 偶数番目＝ダメージを直接、奇数番目＝本物の弾の当たり（血・当たりの効果・ヒットマーカーを含む）
                bool bullet = i % 2 == 1;
                var fps = Refs.Fps;
                var bw = fps?.GetActiveItem()?.gameObject?.GetComponentInChildren<Il2CppBallistics.Weapon>(true);
                Il2CppBallistics.LivingEntityCollider part = null;
                if (bullet) foreach (var c in e.transform.parent != null ? e.transform.parent.GetComponentsInChildren<Il2CppBallistics.LivingEntityCollider>(true) : e.GetComponentsInChildren<Il2CppBallistics.LivingEntityCollider>(true)) if (c.gameObject.name.StartsWith("spine_02")) { part = c; break; }
                if (bullet && (bw == null || part == null)) { bullet = false; Main.Log("[kill] no weapon/part for the bullet path"); }
                float dmg0 = bw != null ? bw.MuzzleDamage : 0f;
                _finds.Clear();
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    if (bullet) { bw.MuzzleDamage = 1e6f; TestMode.Call(Il2CppBallistics.BulletHandler.instance, "BulletHit", bw, part, null, (part.transform.position - fps.transform.position).normalized, part.transform.position, bw.MaxBulletSpeed, 0f); }
                    else e.TakeDamage(1e6f);
                }
                catch (Exception ex) { Main.Log("[kill] failed: " + ex.Message); }
                if (bw != null) bw.MuzzleDamage = dmg0;
                double call = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                var sb = new StringBuilder();
                for (int f = 0; f < 8; f++) { yield return null; sb.Append($" {Time.unscaledDeltaTime * 1000f:0}({_upd:0}/{_late:0}/{_rest:0})"); }
                Main.Log($"[kill] #{i++} {name} hp {hp:0} dead {e.health <= 0f}: {(bullet ? "BulletHit" : "TakeDamage")} call {call:0.0}ms | next frames dt(upd/late/rest):{sb}");
                if (_finds.Count > 0) Main.Log("[kill]    searches: " + string.Join(", ", _finds.OrderByDescending(k => k.Value.Sum).Select(k => $"{k.Key} x{k.Value.Calls} {k.Value.Sum:0.0}ms (max {k.Value.Max:0.0})")));
                yield return new WaitForSeconds(1.5f);
            }
            if (sample) SpikeSampler.StopAndReport("kills");
            FindCacheChecks("kills");
            VoiceChecks("kills");
            ReportTimers(i * 1.8f);
            ReportSetActive(i * 1.8f);
            yield return Measure("after kills (corpses)", 3f);
            Application.targetFrameRate = cap;
            _done = true;
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
            if (RealToggles) MelonCoroutines.Start(SteamIdleWatch());
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
            if (Real)
            {
                // 本当の撃ち合い：最初から測る（メソッドの時間も）。敵が全滅するか 90 秒まで、5 秒ごと
                _timed.Clear(); _fireMs = _hitMs = 0; _fireN = 0; _kills = 0;
                float t0 = Time.realtimeSinceStartup;
                for (int k = 0; k < 18 && AliveEnemies() > 0; k++)
                {
                    if (RicAB) NoRic = k % 2 == 1;
                    var abField = AB ? Type.GetType("FPOptimize.ImpactSound, FPOptimize")?.GetField("On") : null;
                    if (abField != null) abField.SetValue(null, k % 2 == 0);
                    long shots0 = _fireN;
                    // 撃ち合いの中で切り替えて比べる（perfreal_tunedab）：偶数の窓 = そのまま、奇数の窓 = 1 つ止める（測った後に戻す）
                    var tg = RealToggles && k % 2 == 1 && k / 2 < Toggles.Length ? Toggles[k / 2] : default;
                    if (tg.name != null) { try { tg.on(); } catch (Exception e) { Main.Log($"[perf] {tg.name} failed: {e.Message}"); } yield return new WaitForSeconds(0.3f); }
                    if (RealToggles) yield return Measure($"real fight {k * 5:00}s " + (tg.name ?? "baseline"), 5f); else
                    yield return Measure($"real fight {k * 5:00}-{k * 5 + 5:00}s" + (abField == null ? "" : k % 2 == 0 ? " A on" : " B off") + (RicAB ? (NoRic ? " ric OFF" : " ric on") : ""), 5f);
                    if (tg.name != null) try { tg.off(); } catch (Exception e) { Main.Log($"[perf] {tg.name} restore failed: {e.Message}"); }
                    if (RicAB) _shotsBy[RicIdx] += _fireN - shots0;
                    Main.Log($"[perf] real fight: kills so far {_kills}, enemies alive {AliveEnemies()}");
                }
                ReportTimers(Time.realtimeSinceStartup - t0);
                ReportSetActive(Time.realtimeSinceStartup - t0);
                if (RealToggles && SteamIdleT != null)
                {
                    var st = SteamIdleT.GetMethod("TestStats")?.Invoke(null, null) as long[];
                    Main.Log($"[test] steam idle: tracked {st?[0]} sleeping {st?[1]} slept {st?[2]} woken before play {st?[3]} woken late {st?[4]}; {SteamIdleT.GetMethod("Stats")?.Invoke(null, null)}");
                    Main.Log($"[test] CHECK steam idle: sources were put to sleep and woken again: {(st != null && st[2] > 0 && st[3] + st[4] > 0 ? "PASS" : "FAIL")}");
                    Main.Log($"[test] CHECK steam idle: never asleep while its sound plays ({_asleepPlaying} in {_asleepChecks} checks): {(_asleepChecks > 0 && _asleepPlaying == 0 ? "PASS" : "FAIL")}");
                    FindCacheChecks("real fight");
                    VoiceChecks("real fight");
                }
                if (RicAB)
                    for (int i = 0; i < 2; i++)
                        Main.Log($"[perf] ricochet {(i == 0 ? "ON " : "OFF")}: UpdateBullets {_ubMs[i] / Math.Max(1, _ubCalls[i]):0.000} ms/frame ({_ubCalls[i]} frames), ricochet checks {_ricChecks[i]}, impacts in bullet update {_spawnsInUB[i]}, LookRotation {_lookRot[i]}, player shots {_shotsBy[i]}");
                NoRic = false;
                // FPOptimize の当たり音の間引きが動いているか（鳴らす音も残っているか）
                try
                {
                    var ts = Type.GetType("FPOptimize.ImpactSound, FPOptimize")?.GetMethod("TestStats")?.Invoke(null, null) as long[];
                    if (ts != null && !AB)
                    {
                        Main.Log($"[test] impact sounds: active={ts[0]} spawns={ts[1]} played={ts[2]} skipped={ts[3]} body={ts[4]} bodyPlayed={ts[5]} bodySkipped={ts[6]}");
                        Main.Log($"[test] CHECK impact sound limiter active and some hit sounds still play: {(ts[0] == 1 && ts[1] > 0 && ts[2] > 0 && ts[3] > 0 ? "PASS" : "FAIL")}");
                        Main.Log($"[test] CHECK body/weapon floor sounds still play: {(ts[4] == 0 || ts[5] > 0 ? "PASS" : "FAIL")}");
                    }
                }
                catch (Exception e) { Main.Log("[perf] impact check failed: " + e.Message); }
                // 倒れた敵の体（ラグドール）がまだ動いているか
                try
                {
                    foreach (var pm in Main.FindAll<Il2CppRootMotion.Dynamics.PuppetMaster>())
                    {
                        int rb = 0, sleeping = 0;
                        float maxV = 0, maxW = 0, thr = 0; int kin = 0;
                        foreach (var r in pm.GetComponentsInChildren<Rigidbody>(false)) { rb++; if (r.IsSleeping()) sleeping++; if (r.isKinematic) kin++; maxV = Mathf.Max(maxV, r.velocity.sqrMagnitude); maxW = Mathf.Max(maxW, r.angularVelocity.sqrMagnitude); thr = r.sleepThreshold; }
                        Main.Log($"[perf]    v2={maxV:0.0000} w2={maxW:0.0000} sleepThr={thr:0.000} kinematic={kin} muscleW={pm.muscleWeight:0.00} pinW={pm.pinWeight:0.00} spring={pm.muscleSpring:0} freezeV2={pm.stateSettings.maxFreezeSqrVelocity:0.000} freezePerm={pm.stateSettings.freezePermanently} solverIter={Physics.defaultSolverIterations}");
                        var eh = pm.transform.root.GetComponentInChildren<Il2CppEnemyAI.EnemyHealth>(true);
                        Main.Log($"[perf] puppet {pm.transform.root.name}: active={pm.gameObject.activeInHierarchy} enabled={pm.enabled} state={pm.state} mode={pm.mode} frozen={pm.isFrozen} rigidbodies={rb} sleeping={sleeping} hp={(eh == null ? -1 : eh.health):0}");
                    }
                }
                catch (Exception e) { Main.Log("[perf] puppet report failed: " + e.Message); }
                Main.Log($"[perf] per shot: Fire {_fireMs / Math.Max(1, _fireN):0.00} ms, BulletHit call {_hitMs / Math.Max(1, _fireN):0.00} ms ({_fireN} shots), kills {_kills}");
                _fightRun = false; Application.targetFrameRate = cap; Inventory("end"); _done = true; Real = false;
                yield break;
            }
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
        static readonly List<Behaviour> _idleSteam = new();
        // 鳴っていない音源の Steam Audio だけ止める（音は変わらないはず）
        static void SetIdleSteam(bool on)
        {
            if (on) { foreach (var b in _idleSteam) if (b != null) b.enabled = true; _idleSteam.Clear(); return; }
            int all = 0, playing = 0, noSrc = 0; var types = new Dictionary<string, int>();
            foreach (var m in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (!m.enabled) continue;
                var tn = TypeName(m);
                if (!IsAudioFx(tn)) continue;
                types[tn] = types.GetValueOrDefault(tn) + 1;
                if (!tn.EndsWith("SteamAudioSource")) continue;
                all++;
                var src = m.GetComponent<AudioSource>();
                if (src == null) { noSrc++; continue; }
                if (src.isPlaying) { playing++; continue; }
                m.enabled = false; _idleSteam.Add(m);
            }
            Main.Log($"[perf] steam audio: sources {all}, playing {playing}, no AudioSource {noSrc}, idle disabled {_idleSteam.Count}; types " + string.Join(", ", types.Select(k => $"{k.Key} {k.Value}")));
        }
        public static bool RealToggles;
        static readonly Type SteamIdleT = Type.GetType("FPOptimize.SteamIdle, FPOptimize");
        static void SteamIdleOn(bool on) { SteamIdleT?.GetField("On")?.SetValue(null, on); }
        // 撃ち合いの間、0.5 秒ごとに「鳴っているのに休ませたまま」の音源が無いかを見る
        static int _asleepPlaying, _asleepChecks;
        static IEnumerator SteamIdleWatch()
        {
            var m = SteamIdleT?.GetMethod("PlayingWhileAsleep");
            if (m == null) yield break;
            while (_fightRun) { yield return new WaitForSeconds(0.5f); try { _asleepPlaying += (int)m.Invoke(null, null); _asleepChecks++; } catch { } }
        }
        static readonly List<(Light l, LightShadows s)> _shadowsOff = new();
        static float _rs = -1f;
        static readonly Type FindCacheT = Type.GetType("FPOptimize.PlayerCache, FPOptimize");
        static void FindCacheOn(bool on) { FindCacheT?.GetField("On")?.SetValue(null, on); }
        // FPOptimize の「1 つしかない物の検索」を覚える処理が動いたか・元の検索と食い違わなかったか（食い違いは verify モードのときだけ数える）
        static void FindCacheChecks(string when)
        {
            var st = FindCacheT?.GetMethod("TestStats")?.Invoke(null, null) as long[];
            Main.Log($"[test] find cache ({when}): {FindCacheT?.GetMethod("Detail")?.Invoke(null, null)}");
            Main.Log($"[test] CHECK find cache answers the game's searches ({when}, hits {st?[0]}): {(st != null && st[0] > 0 ? "PASS" : "FAIL")}");
            Main.Log($"[test] CHECK find cache same result as the game's search ({when}, mismatches {st?[2]}): {(st != null && st[2] == 0 ? "PASS" : "FAIL")}");
        }
        static readonly (string name, Action on, Action off)[] Toggles =
        {
            ("FPOptimize find cache OFF", () => FindCacheOn(false), () => FindCacheOn(true)),
            ("FPOptimize steam idle OFF", () => SteamIdleOn(false), () => SteamIdleOn(true)),
            ("FPOptimize find cache OFF", () => FindCacheOn(false), () => FindCacheOn(true)),
            ("FPOptimize find cache OFF", () => FindCacheOn(false), () => FindCacheOn(true)),
            ("PuppetMaster off", () => SetBehaviours(IsPuppet, false), () => SetBehaviours(IsPuppet, true)),
            ("enemy animators off", () => SetEnemyAnimators(false), () => SetEnemyAnimators(true)),
            ("FIMSpace off", () => SetBehaviours(IsFim, false), () => SetBehaviours(IsFim, true)),
            ("render scale x0.5", () => { var u = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>(); if (u != null) { _rs = u.renderScale; u.renderScale = _rs * 0.5f; } },
                                  () => { var u = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>(); if (u != null && _rs > 0) u.renderScale = _rs; }),
            ("realtime shadows off", () => { foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>()) if (l.shadows != LightShadows.None) { _shadowsOff.Add((l, l.shadows)); l.shadows = LightShadows.None; } },
                                     () => { foreach (var (l, s) in _shadowsOff) if (l != null) l.shadows = s; _shadowsOff.Clear(); }),
            ("post volumes off", () => SetVolumes(false), () => SetVolumes(true)),
        };

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
