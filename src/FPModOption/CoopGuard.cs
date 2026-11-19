using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FPModOption
{
    // co-op では Mod を使わない（すべての Mod 共通）。
    //   ・co-op が始まったら（メニューで「Continue Co-op」を選んだ・招待で参加した）Off になり、この起動の間はずっと Off のまま。
    //     ソロで Mod を使うには、ゲームを再起動する（co-op の途中で Mod の状態が混ざらないように）
    //   ・Off の間は、各 Mod の Harmony パッチの中身を一切実行しない（パッチは Wrap で包んで登録する）。
    //     MelonMod の毎フレームの処理などは各 Mod が Off を見て止める
    //   ・ソロで遊んだ後に co-op に入ったときは、ゲームのデータに書き込んだ値（表示のエルゴ・コントロール、移動の速さ、エイムの速さ）を元に戻す
    //   ・リーダーボード送信の停止だけは co-op でも続ける（Wrap しない）
    //   ・Mod のセーブ（state.json など）は co-op の間は読み書きしない（その処理もパッチの中なので止まる）
    public static class CoopGuard
    {
        public static bool Off { get; private set; }
        public static string Why { get; private set; }
        // co-op に入ったとき、各 Mod が自分の後片付けをする（画面の表示を消す等）
        public static event Action Entered;
        static bool _notified;

        // テスト用：co-op に入ったことにする
        public static void EnterForTest() => Enter("test");

        public static void Install(HarmonyLib.Harmony h)
        {
            // 「Continue Co-op」・招待での参加は、どれも CoopSaves.Begin(モード, プロファイル) を通る。シーンを読む前に Off にする
            try
            {
                var t = typeof(Il2CppCoop.CoopSaves);
                if (!SafePatcher.Patch(h, t, "Begin", null, null, nameof(Begin_Post), typeof(CoopGuard), gate: false))
                    Core.Log("[coop] Begin hook not installed (checking every frame instead)");
            }
            catch (Exception e) { Core.Log("[coop] hook failed: " + e.Message); }
        }

        static void Begin_Post(Il2CppCoop.CoopPlayMode mode)
        {
            try { if (mode == Il2CppCoop.CoopPlayMode.Host || mode == Il2CppCoop.CoopPlayMode.Guest) Enter("Begin " + mode); } catch { }
        }

        // 毎フレーム（OnUpdate）。フックが無い・効かなかったときの保険
        public static void Tick()
        {
            if (Off) { Notify(); return; }
            if (InCoop(out var why)) Enter(why);
        }

        public static bool InCoop(out string why)
        {
            why = null;
            try
            {
                var m = Il2CppCoop.CoopSaves.Mode;
                if (m == Il2CppCoop.CoopPlayMode.Host || m == Il2CppCoop.CoopPlayMode.Guest) { why = "mode " + m; return true; }
            }
            catch { }
            try { if (Il2CppCoop.Coop.Active) { why = "session active"; return true; } } catch { }
            return false;
        }

        static void Enter(string why)
        {
            if (Off) return;
            Off = true;
            Why = why;
            Core.Log($"[coop] co-op started ({why}): all mods OFF until the game is restarted");
            // ゲームのデータ（ScriptableObject）に書き込んだ値を元に戻す
            Revert("statscale", StatScale.Revert);
            Revert("slide", Slide.Revert);
            Revert("ads", AdsSpeed.Revert);
            foreach (var d in Entered?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { ((Action)d)(); } catch (Exception e) { Core.Log($"[coop] cleanup failed ({d.Method.DeclaringType?.Name}): {e.Message}"); }
        }

        static void Revert(string name, Action a)
        {
            try { a(); } catch (Exception e) { Core.Log($"[coop] revert {name} failed: {e.Message}"); }
        }

        // 拠点に入ったら一度だけ知らせる
        static void Notify()
        {
            if (_notified || Core.Scene == "MainMenu") return;
            var nm = Refs.Inv?.NotificationsManager;
            if (nm == null) return;
            _notified = true;
            try
            {
                nm.NotifyWithColor(Core.T("MOD: co-op では MOD は無効です（ソロで使うにはゲームを再起動）",
                                          "MOD: mods are off in co-op (restart the game to use them in single-player)"), new Color(1f, 0.8f, 0.2f));
            }
            catch { }
        }

        // ---------- パッチの包み ----------
        // 各 Mod のパッチの関数を、同じ引数（名前も同じ）の関数で包む。Off なら中身を呼ばずに戻る（前置きの bool はゲームの処理を止めない true）。
        // 引数の名前（__instance・__result など）を Harmony が見るので、名前付きの引数を作れる動的アセンブリで作る

        static ModuleBuilder _module;
        static int _n;
        static readonly Dictionary<MethodInfo, MethodInfo> _wrapped = new();
        static readonly HashSet<string> _access = new();
        static AssemblyBuilder _asm;

        public static int WrappedCount => _wrapped.Count;
        // co-op の間に止めた呼び出しの数（確認用）
        public static long Skipped;
        public static void NoteSkip() => Skipped++;

        public static MethodInfo Wrap(MethodInfo m)
        {
            if (m == null) return null;
            if (_wrapped.TryGetValue(m, out var done)) return done;
            if (!m.IsStatic || (m.ReturnType != typeof(void) && m.ReturnType != typeof(bool)))
            {
                Core.Log($"[coop] not wrapped (static={m.IsStatic}, returns {m.ReturnType.Name}): {m.DeclaringType?.Name}.{m.Name}");
                return m;
            }
            try
            {
                if (_module == null)
                {
                    _asm = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FPModOption.CoopGate"), AssemblyBuilderAccess.Run);
                    _module = _asm.DefineDynamicModule("FPModOption.CoopGate");
                }
                // 包む関数から、各 Mod の private な関数を呼べるようにする
                var target = m.DeclaringType.Assembly.GetName().Name;
                if (_access.Add(target))
                    _asm.SetCustomAttribute(new CustomAttributeBuilder(IgnoresAccessCtor, new object[] { target }));

                var ps = m.GetParameters();
                var tb = _module.DefineType($"Gate{_n++}_{m.DeclaringType.Name}_{m.Name}", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var mb = tb.DefineMethod(m.Name, MethodAttributes.Public | MethodAttributes.Static, m.ReturnType, ps.Select(p => p.ParameterType).ToArray());
                for (int i = 0; i < ps.Length; i++) mb.DefineParameter(i + 1, ps[i].Attributes, ps[i].Name);
                var il = mb.GetILGenerator();
                var run = il.DefineLabel();
                il.Emit(OpCodes.Call, typeof(CoopGuard).GetProperty(nameof(Off)).GetGetMethod());
                il.Emit(OpCodes.Brfalse, run);
                il.Emit(OpCodes.Call, typeof(CoopGuard).GetMethod(nameof(NoteSkip)));
                if (m.ReturnType == typeof(bool)) il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(run);
                for (int i = 0; i < ps.Length; i++) il.Emit(OpCodes.Ldarg, (short)i);
                il.Emit(OpCodes.Call, m);
                il.Emit(OpCodes.Ret);
                var t = tb.CreateType();
                var w = t.GetMethod(m.Name, BindingFlags.Public | BindingFlags.Static);
                _wrapped[m] = w;
                return w;
            }
            catch (Exception e)
            {
                Core.Log($"[coop] wrap failed {m.DeclaringType?.Name}.{m.Name}: {e.Message}");
                return m;
            }
        }

        // 包んだ HarmonyMethod（各 Mod が h.Patch に直接渡すとき用）
        public static HarmonyMethod Hm(Type type, string name)
        {
            var m = AccessTools.Method(type, name);
            return m == null ? null : new HarmonyMethod(Wrap(m));
        }
        public static HarmonyMethod Hm(HarmonyMethod hm)
        {
            if (hm?.method == null) return hm;
            var w = new HarmonyMethod(Wrap(hm.method)) { priority = hm.priority, before = hm.before, after = hm.after };
            return w;
        }

        static ConstructorInfo _ignores;
        static ConstructorInfo IgnoresAccessCtor => _ignores ??= BuildIgnoresAccess();

        // System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute（ランタイムが名前で認識する）を動的アセンブリの中に作る
        static ConstructorInfo BuildIgnoresAccess()
        {
            var tb = _module.DefineType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute", TypeAttributes.Public | TypeAttributes.Class, typeof(Attribute));
            var f = tb.DefineField("_name", typeof(string), FieldAttributes.Private);
            var c = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(string) });
            var il = c.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, f);
            il.Emit(OpCodes.Ret);
            tb.SetCustomAttribute(new CustomAttributeBuilder(typeof(AttributeUsageAttribute).GetConstructor(new[] { typeof(AttributeTargets) }),
                new object[] { AttributeTargets.Assembly }, new[] { typeof(AttributeUsageAttribute).GetProperty("AllowMultiple") }, new object[] { true }));
            return tb.CreateType().GetConstructor(new[] { typeof(string) });
        }
    }
}
