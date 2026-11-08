using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using UnityEngine;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario tipperf）。地面の武器を見ているとき（ポップアップ表示中）と見ていないときの
    //   フレーム時間を比べる。Mod の後処理を止めたときも測り、重いのが Mod かゲームかを分ける。読むだけ
    public static class TipPerf
    {
        static GameObject _gun;
        static readonly List<float> _dt = new();
        static bool _on, _hooked;
        static long _calls0, _ticks0, _miss0, _uiTicks0, _uiCalls0;

        static void Sample() { if (_on) _dt.Add(Time.unscaledDeltaTime * 1000f); }

        // ゲームの処理ごとの時間（テストの間だけ付ける）
        static readonly Dictionary<string, (long ticks, long calls)> _t = new();
        static readonly Dictionary<System.Reflection.MethodBase, string> _names = new();
        static void Pre(out long __state) => __state = System.Diagnostics.Stopwatch.GetTimestamp();
        static void Post(System.Reflection.MethodBase __originalMethod, long __state)
        {
            if (!_on || !_names.TryGetValue(__originalMethod, out var n)) return;
            _t.TryGetValue(n, out var v);
            _t[n] = (v.ticks + System.Diagnostics.Stopwatch.GetTimestamp() - __state, v.calls + 1);
        }

        static readonly Dictionary<string, int> _findArgs = new();
        static void FindArgs(Il2CppSystem.Type type, FindObjectsInactive findObjectsInactive)
        {
            if (!_on) return;
            var k = $"{type?.FullName} {findObjectsInactive}";
            _findArgs.TryGetValue(k, out var c); _findArgs[k] = c + 1;
        }

        static void Hook()
        {
            var h = new HarmonyLib.Harmony("fprarity.tipperf");
            var pre = new HarmonyLib.HarmonyMethod(typeof(TipPerf).GetMethod(nameof(Pre), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
            var post = new HarmonyLib.HarmonyMethod(typeof(TipPerf).GetMethod(nameof(Post), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
            foreach (var (type, name) in new (Type, string)[]
            {
                (typeof(UserInteractions), "Update"), (typeof(UserInteractions), "UpdateTooltip"), (typeof(UserInteractions), "UpdateInteractionUI"),
                (typeof(LootableItem), "GetTooltipText"),
                (typeof(Il2CppGameLocalization.LocTextHook), "PreprocessText"), (typeof(Il2CppGameLocalization.LocTextHook), "OnTextChanged"),
                (typeof(Il2CppGameLocalization.LocTextHook), "OnWillRenderCanvases"),
            })
            {
                try
                {
                    var m = HarmonyLib.AccessTools.Method(type, name);
                    if (m == null) { Main.Log($"[tipperf] no method {type.Name}.{name}"); continue; }
                    _names[m] = $"{type.Name}.{name}";
                    h.Patch(m, pre, post);
                }
                catch (Exception e) { Main.Log($"[tipperf] patch {type.Name}.{name} failed: {e.Message}"); }
            }
            try
            {
                var fm = HarmonyLib.AccessTools.Method(typeof(UnityEngine.Object), "FindAnyObjectByType", new[] { typeof(Il2CppSystem.Type), typeof(FindObjectsInactive) });
                h.Patch(fm, new HarmonyLib.HarmonyMethod(typeof(TipPerf).GetMethod(nameof(FindArgs), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            }
            catch (Exception e) { Main.Log("[tipperf] find args patch failed: " + e.Message); }
            // Unity の検索（引数の型ごとに全部）
            foreach (var m in typeof(UnityEngine.Object).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public))
            {
                if (m.IsGenericMethodDefinition || !m.Name.StartsWith("Find")) continue;
                try
                {
                    _names[m] = "Object." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")";
                    h.Patch(m, pre, post);
                }
                catch (Exception e) { Main.Log($"[tipperf] patch {m.Name} failed: {e.Message}"); }
            }
        }

        static void Begin()
        {
            if (!_hooked) { MelonLoader.MelonEvents.OnUpdate.Subscribe(Sample); _hooked = true; }
            _dt.Clear(); _t.Clear(); _on = true;
            _calls0 = Patches.TooltipPostCalls; _ticks0 = Patches.TooltipPostTicks; _miss0 = Patches.TooltipMisses;
            _uiTicks0 = UiPatches.TooltipUiTicks; _uiCalls0 = UiPatches.TooltipUiCalls;
        }

        static void End(string phase)
        {
            _on = false;
            if (_dt.Count == 0) { Main.Log($"[tipperf] {phase}: no frames"); return; }
            var s = _dt.OrderBy(x => x).ToList();
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            long calls = Patches.TooltipPostCalls - _calls0, uiCalls = UiPatches.TooltipUiCalls - _uiCalls0;
            var ui = Refs.Interactions;
            Main.Log($"[tipperf] {phase}: frames {s.Count} avg {s.Average():0.00} ms p50 {s[s.Count / 2]:0.00} p95 {s[(int)(s.Count * 0.95)]:0.00} max {s[^1]:0.00}"
                + $" | text postfix {calls} calls {(Patches.TooltipPostTicks - _ticks0) * f / Math.Max(1, calls):0.000} ms/call misses {Patches.TooltipMisses - _miss0}"
                + $" | ui postfix {uiCalls} calls {(UiPatches.TooltipUiTicks - _uiTicks0) * f / Math.Max(1, uiCalls):0.000} ms/call | tooltip shown {ui?.tooltip?.activeInHierarchy}");
            foreach (var kv in _findArgs) Main.Log($"[tipperf]   find {kv.Key}: {kv.Value}");
            _findArgs.Clear();
            foreach (var kv in _t) Main.Log($"[tipperf]   {kv.Key}: {kv.Value.calls} calls, {kv.Value.ticks * f / s.Count:0.000} ms/frame, {kv.Value.ticks * f / Math.Max(1, kv.Value.calls):0.000} ms/call");
        }

        static void Place(bool inView)
        {
            var cam = Camera.main;
            if (_gun == null || cam == null) return;
            _gun.transform.position = inView ? cam.transform.position + cam.transform.forward * 1.2f : cam.transform.position + Vector3.up * 30f;
        }

        public static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>
            {
                ("spawn ground weapon", () =>
                {
                    Hook();
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<LootableItem>()))
                    {
                        var li = o.TryCast<LootableItem>();
                        if (li == null || li.gameObject.scene.IsValid() || li.ItemType != LootableItem.LootType.Weapon) continue;
                        _gun = UnityEngine.Object.Instantiate(li.gameObject);
                        foreach (var rb in _gun.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.useGravity = false; }
                        Main.Log($"[tipperf] ground weapon '{li.gameObject.name}'");
                        break;
                    }
                    Place(false);
                }),
                ("wait", () => { }),
            };
            void Phase(string name, bool inView, bool modOff)
            {
                s.Add(($"{name} start", () => { Patches.RawTooltip = modOff; UiPatches.SkipTooltipUi = modOff; Place(inView); }));
                s.Add(("settle", () => Place(inView)));
                s.Add(("measure", () => { Place(inView); Begin(); }));
                s.Add(("measure", () => Place(inView)));
                s.Add(($"{name} end", () => { End(name); Patches.RawTooltip = false; UiPatches.SkipTooltipUi = false; }));
            }
            Phase("away", false, false);
            Phase("hover", true, false);


            return s;
        }
    }
}
