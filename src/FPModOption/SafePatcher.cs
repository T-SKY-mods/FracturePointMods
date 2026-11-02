using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace FPModOption
{
    // Harmony パッチの登録。IL2CPP 特有の「同一実体に畳まれた関数」を避けて安全に差し替える
    public static class SafePatcher
    {
        static Dictionary<IntPtr, int> _ptrCount;

        static void BuildIndex()
        {
            _ptrCount = new Dictionary<IntPtr, int>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()
                         .Where(a => a.GetName().Name is "Assembly-CSharp" or "Il2CppFPLeaderboard.Client"))
            {
                Type[] types;
                try { types = a.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types)
                {
                    FieldInfo[] fields;
                    try { fields = t.GetFields(BindingFlags.Static | BindingFlags.NonPublic); } catch { continue; }
                    foreach (var f in fields)
                    {
                        if (!f.Name.StartsWith("NativeMethodInfoPtr_") || f.FieldType != typeof(IntPtr)) continue;
                        var p = ReadNativePtr(f);
                        if (p != IntPtr.Zero) _ptrCount[p] = _ptrCount.TryGetValue(p, out var c) ? c + 1 : 1;
                    }
                }
            }
        }

        static IntPtr ReadNativePtr(FieldInfo f)
        {
            try
            {
                var info = (IntPtr)f.GetValue(null);
                return info == IntPtr.Zero ? IntPtr.Zero : System.Runtime.InteropServices.Marshal.ReadIntPtr(info);
            }
            catch { return IntPtr.Zero; }
        }

        // 調査用：差し替え可能か（共有実体でないか）を文字列で返す
        public static string Describe(Type type, string method)
        {
            if (_ptrCount == null) BuildIndex();
            var m = AccessTools.Method(type, method);
            if (m == null) return "NOT FOUND";
            var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m);
            var ptr = field == null ? IntPtr.Zero : ReadNativePtr(field);
            if (ptr == IntPtr.Zero) return "no native ptr";
            _ptrCount.TryGetValue(ptr, out var cnt);
            return cnt > 1 ? $"SHARED x{cnt} @0x{ptr.ToInt64():X}" : $"unique @0x{ptr.ToInt64():X}";
        }

        // 成功したら true。共有実体・未発見なら false（ログに理由を残す）
        public static bool Patch(HarmonyLib.Harmony h, Type type, string method, Type[] args,
                                 string prefix = null, string postfix = null, Type patchClass = null)
        {
            if (_ptrCount == null) BuildIndex();
            var name = $"{type.Name}.{method}";
            var m = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
            if (m == null) { Core.Log($"[patch] NOT FOUND {name}"); return false; }

            var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m);
            var ptr = field == null ? IntPtr.Zero : ReadNativePtr(field);
            if (ptr == IntPtr.Zero) { Core.Log($"[patch] no native ptr {name}"); return false; }
            if (_ptrCount.TryGetValue(ptr, out var cnt) && cnt > 1)
            {
                Core.Log($"[patch] SHARED native body, skipped {name} @0x{ptr.ToInt64():X} (x{cnt})");
                return false;
            }

            if (patchClass == null) { Core.Log($"[patch] no patch class for {name}"); return false; }
            HarmonyMethod Hm(string n) => n == null ? null : new HarmonyMethod(AccessTools.Method(patchClass, n));
            try
            {
                h.Patch(m, Hm(prefix), Hm(postfix));
                Core.Log($"[patch] ok {name}");
                return true;
            }
            catch (Exception e)
            {
                Core.Log($"[patch] FAIL {name}: {e}");
                return false;
            }
        }
    }
}
