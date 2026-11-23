using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using FPModOption;
using Il2Cpp;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FPEndless
{
    // 調査用（autotest -Dev -Scenario endmaps）：調節したエンドレスの階ごと（オフィス・刑務所・ギャラリー・駐車場）に、
    //   ゲームの何が重いかを測る。フレームの区間（Update まで／LateUpdate〜描画／描画の後）、ゲームのスクリプトごとの時間、
    //   数（敵・倒れた体・Animator・影を落とす明かり…）、切り替えて差を見る（倒れた体の部品・敵のアニメーション・解像度・影）。
    //   切り替えたものは測った後に必ず元に戻す。普段のプレイでは何もしない（On のときだけ）。
    public static class GameProf
    {
        public static bool On;            // 測定中だけ（区間の印を付ける）
        public static bool Busy;          // 1 つの階の測定が終わるまで true
        public static int ForceLocation = -1;   // テスト用：次に作る階の種類（0 オフィス、1 刑務所、2・3 ギャラリー／駐車場）

        // ---------- 階の種類を決める ----------
        static bool _locPatched;
        public static void PatchLocation(HarmonyLib.Harmony h)
        {
            if (_locPatched) return;
            _locPatched = true;
            SafePatcher.Patch(h, typeof(LevelElevator), "SetupEndlessFloor", null, nameof(Loc_Pre), null, typeof(GameProf), gate: false);
        }
        static void Loc_Pre(ref int forcedLocation)
        {
            if (ForceLocation >= 0) { forcedLocation = ForceLocation; Main.Log($"[prof] forcing location {ForceLocation}"); }
        }

        // ---------- フレームの区間 ----------
        static readonly Stopwatch _sw = Stopwatch.StartNew();
        static double _tUpd, _tLate, _tEof;
        static double _upd, _late, _rest;
        static bool _eofRunning;
        public static void OnUpdate()
        {
            if (!On) return;
            double now = _sw.Elapsed.TotalMilliseconds;
            if (_tEof > 0 && _tLate > 0 && _tUpd > 0) { _upd = _tLate - _tUpd; _late = _tEof - _tLate; _rest = now - _tEof; }
            _tUpd = now;
        }
        public static void OnLateUpdate() { if (On) _tLate = _sw.Elapsed.TotalMilliseconds; }
        static IEnumerator Eof()
        {
            var w = new WaitForEndOfFrame();
            while (true) { yield return w; if (On) _tEof = _sw.Elapsed.TotalMilliseconds; }
        }

        struct F { public float Dt; public double U, L, R; }
        static IEnumerator Frames(string label, int n, List<string> outLines = null)
        {
            var fs = new List<F>(n);
            yield return null; yield return null;
            for (int i = 0; i < n; i++) { yield return null; fs.Add(new F { Dt = Time.unscaledDeltaTime * 1000f, U = _upd, L = _late, R = _rest }); }
            var d = fs.Select(f => f.Dt).OrderBy(x => x).ToList();
            float avg = d.Average(), p99 = d[(int)(d.Count * 0.99f)];
            string line = $"[prof] {label,-34} {avg,5:0.0}ms {1000f / avg,4:0}fps p99 {p99,5:0.0}ms | upd {fs.Average(f => f.U),5:0.00} late+render {fs.Average(f => f.L),5:0.00} after {fs.Average(f => f.R),5:0.00} | alive {Alive()}";
            Main.Log(line);
            _lastAvg = avg;
        }
        static float _lastAvg;

        // ---------- ゲームのスクリプトごとの時間 ----------
        sealed class Stat { public double Ms; public int Calls; }
        static readonly Dictionary<MethodBase, Stat> _stats = new();
        static readonly long[] _stack = new long[64];
        static int _sp;
        static bool _timing;
        static readonly HashSet<string> _patched = new();
        static HarmonyLib.Harmony _h;
        static Dictionary<string, Type> _byName;

        static void TPre() { if (_timing && _sp < _stack.Length) _stack[_sp++] = Stopwatch.GetTimestamp(); }
        static void TPost(MethodBase __originalMethod)
        {
            if (!_timing || _sp <= 0) return;
            long t = _stack[--_sp];
            if (!_stats.TryGetValue(__originalMethod, out var s)) _stats[__originalMethod] = s = new Stat();
            s.Ms += (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency; s.Calls++;
        }

        static void InstallTimers()
        {
            _h ??= new HarmonyLib.Harmony("fpendless.gameprof");
            if (_byName == null)
            {
                _byName = new Dictionary<string, Type>();
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string loc; try { loc = a.Location ?? ""; } catch { loc = ""; }
                    if (!loc.Replace('/', '\\').Contains("\\Il2CppAssemblies\\")) continue;
                    Type[] ts; try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(x => x != null).ToArray(); }
                    foreach (var t in ts) if (t.FullName != null) _byName[t.FullName] = t;
                }
            }
            int n = 0, types = 0;
            var seen = new HashSet<string>();
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null) continue;
                string full; try { full = mb.GetIl2CppType().FullName; } catch { continue; }
                if (full == null || !seen.Add(full) || _patched.Contains(full)) continue;
                if (!_byName.TryGetValue("Il2Cpp" + full, out var t) && !_byName.TryGetValue("Il2Cpp." + full, out t) && !_byName.TryGetValue(full, out t)) continue;
                _patched.Add(full); types++;
                foreach (var m in new[] { "Update", "LateUpdate", "FixedUpdate" })
                    if (HarmonyLib.AccessTools.DeclaredMethod(t, m, Type.EmptyTypes) != null && SafePatcher.Patch(_h, t, m, Type.EmptyTypes, nameof(TPre), nameof(TPost), typeof(GameProf), gate: false)) n++;
            }
            Main.Log($"[prof] script timers: +{n} methods on +{types} new script types (total types {_patched.Count})");
        }

        static void ReportTimers(int frames, string label)
        {
            var sb = new StringBuilder($"[prof] {label}: game scripts per frame over {frames} frames (ms/frame, calls/frame):\n");
            double total = 0;
            foreach (var kv in _stats.OrderByDescending(k => k.Value.Ms).Take(22))
            {
                var key = kv.Key.DeclaringType.Name + "." + kv.Key.Name;
                sb.AppendLine($"    {key,-48} {kv.Value.Ms / frames,6:0.000}  x{(double)kv.Value.Calls / frames,5:0.0}");
            }
            foreach (var kv in _stats) total += kv.Value.Ms;
            sb.Append($"    total of all timed scripts {total / frames:0.00} ms/frame");
            Main.Log(sb.ToString());
        }

        // プレイヤー（無敵）を、いちばん近い敵の 5m 手前（ナビメッシュの上）へ動かして撃ち合いにする
        static void Engage()
        {
            try
            {
                var fps = Refs.Fps; if (fps == null) return;
                var p = fps.transform.position;
                var e = Bodies(false).OrderBy(t => (t.position - p).sqrMagnitude).FirstOrDefault();
                if (e == null) { Main.Log("[prof] engage: no enemy"); return; }
                var dir = (p - e.position); dir.y = 0; dir = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.forward;
                var want = e.position + dir * 5f;
                if (UnityEngine.AI.NavMesh.SamplePosition(want, out var hit, 3f, -1)) want = hit.position;
                var cc = fps.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;
                fps.transform.position = want + Vector3.up * 0.1f;
                if (cc != null) cc.enabled = true;
                Main.Log($"[prof] engage: moved {Vector3.Distance(p, want):0}m to near {e.name}");
            }
            catch (Exception ex) { Main.Log("[prof] engage failed: " + ex.Message); }
        }

        // ---------- 数 ----------
        static int Alive() { int n = 0; foreach (var e in Core.FindAll<Il2CppEnemyAI.EnemyHealth>()) if (e != null && e.health > 0f && e.gameObject.activeInHierarchy) n++; return n; }

        static List<Transform> Bodies(bool dead)
        {
            var l = new List<Transform>();
            foreach (var e in Core.FindAll<Il2CppEnemyAI.EnemyHealth>())
                if (e != null && e.gameObject.activeInHierarchy && (e.health <= 0f) == dead) l.Add(e.transform.root);
            return l;
        }

        static void Census(string label)
        {
            var sb = new StringBuilder($"[prof] census {label}:");
            try
            {
                var alive = Bodies(false); var dead = Bodies(true);
                int animAll = 0, animOn = 0, animDead = 0, animAlive = 0;
                var deadSet = new HashSet<IntPtr>(dead.Select(t => t.Pointer)); var aliveSet = new HashSet<IntPtr>(alive.Select(t => t.Pointer));
                foreach (var a in Core.FindAll<Animator>())
                {
                    animAll++; if (!a.isActiveAndEnabled) continue; animOn++;
                    var r = a.transform.root.Pointer;
                    if (deadSet.Contains(r)) animDead++; else if (aliveSet.Contains(r)) animAlive++;
                }
                var smr = Core.FindAll<SkinnedMeshRenderer>().ToList();
                var rends = Core.FindAll<Renderer>().ToList();
                var lights = Core.FindAll<Light>().Where(x => x.isActiveAndEnabled).ToList();
                var ps = Core.FindAll<ParticleSystem>().ToList();
                var rbs = Core.FindAll<Rigidbody>().ToList();
                var au = Core.FindAll<AudioSource>().ToList();
                sb.Append($" alive {alive.Count} dead {dead.Count} | animators on {animOn}/{animAll} (alive enemies {animAlive}, dead {animDead})");
                sb.Append($" | skinned {smr.Count} visible {smr.Count(x => x.isVisible)} | renderers {rends.Count} visible {rends.Count(x => x.isVisible)} shadowcasters {rends.Count(x => x.enabled && x.gameObject.activeInHierarchy && x.shadowCastingMode != ShadowCastingMode.Off)}");
                sb.Append($" | lights {lights.Count} shadowed " + string.Join(",", lights.Where(x => x.shadows != LightShadows.None).GroupBy(x => x.type.ToString()).Select(g => $"{g.Key}:{g.Count()}")));
                sb.Append($" | bullets {Il2CppBallistics.BulletHandler.instance?.Bullets?.Count ?? -1} | particles playing {ps.Count(x => x.isPlaying)} | rigidbodies dyn {rbs.Count(x => !x.isKinematic)} awake {rbs.Count(x => !x.isKinematic && !x.IsSleeping())} | audio playing {au.Count(x => x.isPlaying)}/{au.Count(x => x.isActiveAndEnabled)}");
                // 倒れた体 1 つに何が動いているか（多い種類）
                if (dead.Count > 0)
                {
                    var byType = new Dictionary<string, int>();
                    foreach (var b in dead.Take(3))
                        foreach (var c in b.GetComponentsInChildren<Behaviour>(false))
                            if (c != null && c.enabled) { var n = c.GetIl2CppType().Name; byType[n] = byType.GetValueOrDefault(n) + 1; }
                    sb.Append("\n    on 3 dead bodies: " + string.Join(", ", byType.OrderByDescending(k => k.Value).Take(14).Select(k => $"{k.Key} {k.Value}")));
                }
                if (alive.Count > 0)
                {
                    var byType = new Dictionary<string, int>();
                    foreach (var c in alive[0].GetComponentsInChildren<Behaviour>(false))
                        if (c != null && c.enabled) { var n = c.GetIl2CppType().Name; byType[n] = byType.GetValueOrDefault(n) + 1; }
                    sb.Append($"\n    on 1 live enemy ({alive[0].name}): " + string.Join(", ", byType.OrderByDescending(k => k.Value).Take(14).Select(k => $"{k.Key} {k.Value}")));
                }
            }
            catch (Exception e) { sb.Append(" failed: " + e.Message); }
            Main.Log(sb.ToString());
        }

        // ---------- 切り替え（測った後に戻す） ----------
        static readonly List<Behaviour> _off = new();
        static void Restore() { foreach (var b in _off) try { if (b != null) b.enabled = true; } catch { } _off.Clear(); }
        static void OffOn(IEnumerable<Transform> roots, Func<Behaviour, bool> pick)
        {
            foreach (var r in roots)
                foreach (var b in r.GetComponentsInChildren<Behaviour>(false))
                    if (b != null && b.enabled && pick(b)) { b.enabled = false; _off.Add(b); }
        }
        static void OffAll(Func<string, bool> fullName)
        {
            foreach (var b in Core.FindAll<Behaviour>())
            {
                if (b == null || !b.isActiveAndEnabled) continue;
                string n; try { n = b.GetIl2CppType().FullName ?? ""; } catch { continue; }
                if (fullName(n)) { b.enabled = false; _off.Add(b); }
            }
        }
        static bool IsAnim(Behaviour b) => b.GetIl2CppType().Name == "Animator";
        static bool IsCorpsePart(Behaviour b)
        {
            var t = b.GetIl2CppType(); var ns = t.Namespace ?? "";
            return t.Name == "Animator" || ns.StartsWith("EnemyAI") || ns.StartsWith("RootMotion") || ns.StartsWith("FIMSpace") || t.Name == "NavMeshAgent";
        }
        static readonly List<(Light l, LightShadows s)> _shadows = new();

        static IEnumerator AB(string name, Action on, Action off, int n)
        {
            yield return Frames("  baseline", n);
            float b = _lastAvg;
            try { on(); } catch (Exception e) { Main.Log($"[prof] {name} on failed: {e.Message}"); }
            yield return Frames("  " + name, n);
            float a = _lastAvg;
            try { off(); } catch (Exception e) { Main.Log($"[prof] {name} off failed: {e.Message}"); }
            Main.Log($"[prof]   => {name}: {b - a:+0.00;-0.00}ms/frame saved");
        }

        // 1 つの階を測る（テストの手順から呼ぶ）
        public static void MeasureFloor(string map) { Busy = true; MelonLoader.MelonCoroutines.Start(Floor(map)); }
        static IEnumerator Floor(string map)
        {
            On = true;
            if (!_eofRunning) { _eofRunning = true; MelonLoader.MelonCoroutines.Start(Eof()); }
            int cap = Application.targetFrameRate;
            Application.targetFrameRate = -1;   // 上限を外して本当の重さで比べる（最後に戻す）
            try
            {
                Main.Log($"[prof] ===== {map} ===== vsync {QualitySettings.vSyncCount} cap {cap} screen {Screen.width}x{Screen.height}");
                Census(map + " start");
            }
            catch (Exception e) { Main.Log("[prof] start failed: " + e.Message); }
            // 敵が湧いて来るまで待つ（調節で 2 倍。最大 60 秒）
            float w0 = Time.realtimeSinceStartup;
            while (Alive() < 8 && Time.realtimeSinceStartup - w0 < 60f) { yield return new WaitForSeconds(5f); Main.Log($"[prof] waiting for enemies: alive {Alive()}"); }
            Engage();
            yield return new WaitForSeconds(4f);
            Census(map + " fight");
            yield return Frames(map + " live fight", 360);
            Census(map + " fight end");
            // ※ ゲームのスクリプトに時間計測を付ける方法（Harmony）は、この場面でゲームが固まったので使わない。切り替えて差を見る
            var urp = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
            float rs = urp != null ? urp.renderScale : 1f;
            yield return AB("live enemy animators off", () => OffOn(Bodies(false), IsAnim), Restore, 240);
            yield return AB("live enemy AI scripts off", () => OffOn(Bodies(false), b => (b.GetIl2CppType().Namespace ?? "").StartsWith("EnemyAI") && b.GetIl2CppType().Name != "EnemyHealth"), Restore, 240);
            yield return AB("PuppetMaster (all) off", () => OffAll(n => n.StartsWith("RootMotion")), Restore, 240);
            yield return AB("FIMSpace off", () => OffAll(n => n.StartsWith("FIMSpace")), Restore, 240);
            yield return AB("Steam Audio off", () => OffAll(n => n.Contains("SteamAudio") || n.Contains("Phonon")), Restore, 240);
            yield return AB("render scale x0.5", () => { if (urp != null) urp.renderScale = rs * 0.5f; }, () => { if (urp != null) urp.renderScale = rs; }, 240);
            yield return AB("realtime shadows off", () => { foreach (var l in Core.FindAll<Light>()) if (l != null && l.isActiveAndEnabled && l.shadows != LightShadows.None) { _shadows.Add((l, l.shadows)); l.shadows = LightShadows.None; } },
                                                    () => { foreach (var (l, s) in _shadows) try { if (l != null) l.shadows = s; } catch { } _shadows.Clear(); }, 240);
            // 倒れた体を増やす：生きている敵を全部倒す（2 回、湧いてくる分も）
            for (int round = 0; round < 2; round++)
            {
                int k = 0;
                foreach (var e in Core.FindAll<Il2CppEnemyAI.EnemyHealth>())
                    if (e != null && e.health > 0f && e.gameObject.activeInHierarchy) { try { e.TakeDamage(1e6f); k++; } catch { } }
                Main.Log($"[prof] killed {k}");
                yield return new WaitForSeconds(6f);
            }
            Census(map + " after kills");
            yield return Frames(map + " corpses", 360);
            yield return AB("corpse animators+AI off", () => OffOn(Bodies(true), IsCorpsePart), Restore, 300);
            Application.targetFrameRate = cap;
            Main.Log($"[prof] ===== {map} done =====");
            On = false; Busy = false;
        }
    }
}
