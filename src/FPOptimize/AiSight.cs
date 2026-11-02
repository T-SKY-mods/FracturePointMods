using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2Cpp;
using Il2CppEnemyAI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace FPOptimize
{
    // 敵 AI の「死体を見つける」判定（LookDecision.MyHandleBodys）は、範囲内のコライダーごとに、親をたどって
    // 敵の体力コンポーネントを探す。それが無いコライダーでは NullReferenceException が起きて、ゲーム内で捕捉して
    // そのコライダーだけ飛ばしている。例外の発生と捕捉はとても重く、敵が多いと CPU 時間の 1 割以上を使う。
    //
    // 例外が起きる手前までの処理には副作用がない（位置・角度の計算だけ）ので、
    // 例外になるコライダーを先に配列から除いて渡せば、結果（見つけた死体の登録・声）は元と同じで、例外だけが無くなる。
    public static class AiSight
    {
        static bool _active;
        static int _state;            // 0 = X の型をまだ調べていない, 1 = 有効, -1 = 調べられなかったので何もしない
        static Il2CppSystem.Type _xType;
        static int _probeCalls;

        // GameAssembly.dll 内で MyHandleBodys が使う GetComponentInParent<X> のメソッド情報の置き場所（ビルド 25787540）
        const long SlotRva = 0x4721F40;

        // 計測用
        static long _calls, _filtered, _nulls, _destroyed, _noX, _kept, _cacheHits;
        static readonly System.Collections.Generic.Dictionary<IntPtr, bool> _hasX = new();
        static int _cacheFrame = -1;
        static double _ms;

        public static void Install(HarmonyLib.Harmony _)
        {
            var h = new HarmonyLib.Harmony("FPOptimize.AiSight");
            if (!SafePatcher.Patch(h, typeof(LookDecision), "MyHandleBodys", null, typeof(AiSight), nameof(Pre)))
            {
                Main.Log("[ai] OFF (patch failed)");
                return;
            }
            _active = true;
            Main.Log("[ai] installed (waiting for the first call to read the component type)");
        }

        static bool Pre(bool hasTargets, ref Il2CppReferenceArray<Collider> targetsInViewRadius)
        {
            if (!_active || !Main.Enabled || !hasTargets) return true;
            if (_state == 0) { Resolve(); return true; }
            if (_state < 0) return true;
            var arr = targetsInViewRadius;
            if (arr == null) return true;
            var sw = Main.Verify ? Stopwatch.StartNew() : null;
            try
            {
                int frame = Time.frameCount;
                if (frame != _cacheFrame) { _hasX.Clear(); _cacheFrame = frame; }
                int n = arr.Length, bad = 0;
                var keep = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    keep[i] = Usable(arr, i);
                    if (!keep[i]) bad++;
                }
                _calls++;
                if (bad == 0) return true;
                _filtered++;
                var outArr = new Il2CppReferenceArray<Collider>(n - bad);
                for (int i = 0, j = 0; i < n; i++)
                    if (keep[i]) outArr[j++] = arr[i];
                targetsInViewRadius = outArr;
                return true;
            }
            catch (Exception e)
            {
                // 想定外のことが起きたら、以後は元の処理のまま
                _state = -1;
                Main.Log("[ai] filter failed, OFF: " + e);
                return true;
            }
            finally { if (sw != null) _ms += sw.Elapsed.TotalMilliseconds; }
        }

        // 元の処理で例外にならず最後まで進むコライダーか
        static unsafe bool Usable(Il2CppReferenceArray<Collider> arr, int i)
        {
            var elems = (IntPtr*)((byte*)arr.Pointer + 4 * IntPtr.Size);   // Il2CppArray のヘッダ（klass, monitor, bounds, max_length）の後ろ
            var p = elems[i];
            if (p == IntPtr.Zero) { _nulls++; return false; }
            if (*(IntPtr*)((byte*)p + 2 * IntPtr.Size) == IntPtr.Zero) { _destroyed++; return false; }   // UnityEngine.Object.m_CachedPtr
            // 同じフレームの中では、どの敵が調べても結果は同じなので使い回す
            if (!_hasX.TryGetValue(p, out var has))
            {
                var go = new Collider(p).gameObject;
                has = go != null && go.GetComponentInParent(_xType, false) != null;
                _hasX[p] = has;
            }
            else _cacheHits++;
            if (!has) { _noX++; return false; }
            _kept++;
            return true;
        }

        // ゲームのコードが実際に使っている型を、初期化済みのメソッド情報から読む（ビルドが違えば読めないので何もしない）
        static void Resolve()
        {
            if (++_probeCalls < 2) return;   // 1 回目の呼び出しで元の処理がメソッド情報を初期化する
            try
            {
                var ga = GetModuleHandle("GameAssembly.dll");
                if (ga == IntPtr.Zero) { Fail("GameAssembly not found"); return; }
                var slot = Marshal.ReadIntPtr(ga + (int)SlotRva);
                if (slot == IntPtr.Zero || (slot.ToInt64() & 1) != 0) { if (_probeCalls > 200) Fail("slot not initialized"); return; }
                var mname = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(slot));
                if (mname != "GetComponentInParent") { Fail("unexpected method " + mname); return; }
                var klass = IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_method_get_return_type(slot));
                var cname = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass)) + "." + Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
                _xType = Il2CppType.TypeFromPointer(klass, cname);
                if (_xType == null) { Fail("type object null for " + cname); return; }
                _state = 1;
                Main.Log("[ai] ON: component type = " + cname);
            }
            catch (Exception e) { Fail(e.ToString()); }
        }

        static void Fail(string why) { _state = -1; Main.Log("[ai] OFF: " + why); }

        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        static float _nextVerify;
        public static void VerifyTick()
        {
            if (Time.realtimeSinceStartup < _nextVerify || _calls == 0) return;
            _nextVerify = Time.realtimeSinceStartup + 10f;
            Main.Log($"[verify] ai calls={_calls} filtered={_filtered} kept={_kept} nulls={_nulls} destroyed={_destroyed} noX={_noX} cacheHits={_cacheHits} prefixMs={_ms:F1} ({_ms * 1000 / Math.Max(1, _calls):F1}us/call)");
        }
    }
}
