using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Il2CppInterop.Runtime;

using FPModOption;

namespace FPRarity
{
    // 調査用の簡易サンプリングプロファイラ（PerfProbe から使う。通常プレイでは動かない）。
    // メインスレッドの処理が長引いたフレームの間だけ、別スレッドからメインスレッドを一瞬止めて
    // OS の巻き戻し（RtlVirtualUnwind）で呼び出し履歴をたどり、どのゲーム関数（IL2CPP のメソッド）の中にいるかを数える。
    //   ・関数の名前は IL2CPP のメタデータ（全クラスの全メソッドの実体アドレス）と関数の先頭アドレスの一致で引く
    //   ・止めている間はメモリ確保をしない（.NET の GC と行き違って固まらないように）
    public static class SpikeSampler
    {
        // ---------- メインスレッド側から更新する区間情報 ----------
        public static volatile int Phase;            // 1 = Update 区間, 2 = LateUpdate〜描画, 3 = 描画後〜次の Update
        public static long PhaseStart;               // Stopwatch のタイムスタンプ
        public static void Mark(int phase) { PhaseStart = Stopwatch.GetTimestamp(); Phase = phase; }

        static IntPtr _mainThread;
        static Thread _thread;
        static volatile bool _run;
        static double _thresholdMs = 6;

        static IntPtr _ctx;
        static ulong _gaLo, _gaHi, _upLo, _upHi;
        static readonly Dictionary<string, int> _leaf = new(), _incl = new(), _rip = new(), _stacks = new(), _up = new();
        static readonly int[] _phaseSamples = new int[4];
        static int _samples;

        public static void InitOnMainThread()
        {
            if (_mainThread != IntPtr.Zero) return;
            _mainThread = OpenThread(0x0002 | 0x0008 | 0x0040, false, GetCurrentThreadId()); // SUSPEND_RESUME | GET_CONTEXT | QUERY_INFORMATION
            _ctx = Marshal.AllocHGlobal(1232 + 16);
            Module("GameAssembly.dll", out _gaLo, out _gaHi);
            Module("UnityPlayer.dll", out _upLo, out _upHi);
            BuildTable();
            // 解析用：メソッドの位置表（GameAssembly 内の RVA と名前）
            try
            {
                using var w = new System.IO.StreamWriter(System.IO.Path.Combine(Main.Dir, "methods_rva.tsv"));
                for (int i = 0; i < _addr.Length; i++) w.WriteLine($"{_addr[i] - _gaLo:X}\t{_names[i]}");
            }
            catch { }
            Main.Log($"[sampler] main thread handle={_mainThread} GameAssembly=0x{_gaLo:X}-0x{_gaHi:X} UnityPlayer=0x{_upLo:X}-0x{_upHi:X} methods={_addr.Length}");
        }

        public static void Start(double thresholdMs)
        {
            _thresholdMs = thresholdMs;
            _leaf.Clear(); _incl.Clear(); _rip.Clear(); _stacks.Clear(); _up.Clear(); Array.Clear(_phaseSamples); _samples = 0;
            _run = true;
            _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "FPRarity sampler" };
            _thread.Start();
        }

        public static void StopAndReport(string label)
        {
            _run = false;
            _thread?.Join(500);
            var sb = new StringBuilder($"[sampler] {label}: {_samples} samples (threshold {_thresholdMs}ms) by phase upd={_phaseSamples[1]} late/render={_phaseSamples[2]} after-render={_phaseSamples[3]}\n");
            sb.AppendLine("  where the CPU was (instruction pointer):");
            foreach (var kv in _rip.OrderByDescending(k => k.Value).Take(25)) sb.AppendLine($"    {kv.Value,5}  {kv.Key}");
            sb.AppendLine("  innermost game method on the stack:");
            foreach (var kv in _leaf.OrderByDescending(k => k.Value).Take(40)) sb.AppendLine($"    {kv.Value,5}  {kv.Key}");
            sb.AppendLine("  game methods anywhere on the stack (inclusive):");
            foreach (var kv in _incl.OrderByDescending(k => k.Value).Take(90)) sb.AppendLine($"    {kv.Value,5}  {kv.Key}");
            sb.AppendLine("  engine functions at the instruction pointer:");
            foreach (var kv in _up.OrderByDescending(k => k.Value).Take(25)) sb.AppendLine($"    {kv.Value,5}  {kv.Key}");
            sb.AppendLine("  call chains (innermost game method first):");
            foreach (var kv in _stacks.OrderByDescending(k => k.Value).Take(60)) sb.AppendLine($"    {kv.Value,5}  {kv.Key}");
            Main.Log(sb.ToString());
        }

        static void Loop()
        {
            double tick = 1000.0 / Stopwatch.Frequency;
            while (_run)
            {
                int ph = Phase;
                double ms = (Stopwatch.GetTimestamp() - PhaseStart) * tick;
                if (ms < _thresholdMs) { Thread.SpinWait(200); continue; }
                try { Sample(ph); } catch { }
                Thread.Sleep(1);   // 長いフレームの間、約 1〜2ms ごとに 1 回
            }
        }

        // 1 サンプル分の呼び出し履歴（命令位置と、その関数の先頭）。止めている間に書くので固定長
        const int MaxFrames = 64;
        static readonly ulong[] _pc = new ulong[MaxFrames], _fn = new ulong[MaxFrames];

        static void Sample(int ph)
        {
            // --- ここからメインスレッドを止めている間（確保・ログ禁止） ---
            int n = 0;
            if (SuspendThread(_mainThread) == unchecked((uint)-1)) return;
            try
            {
                var ctx = (IntPtr)(((long)_ctx + 15) & ~15L);
                Marshal.WriteInt32(ctx, 0x30, 0x0010000B);    // CONTEXT_FULL
                if (GetThreadContext(_mainThread, ctx) != 0)
                {
                    for (; n < MaxFrames; n++)
                    {
                        ulong pc = (ulong)Marshal.ReadInt64(ctx, 0xF8);
                        if (pc < 0x10000) break;
                        _pc[n] = pc; _fn[n] = 0;
                        var rf = RtlLookupFunctionEntry(pc, out ulong imageBase, IntPtr.Zero);
                        if (rf == IntPtr.Zero)
                        {
                            // 葉関数：戻り先はスタックの先頭
                            ulong rsp = (ulong)Marshal.ReadInt64(ctx, 0x98);
                            Marshal.WriteInt64(ctx, 0xF8, Marshal.ReadInt64((IntPtr)(long)rsp));
                            Marshal.WriteInt64(ctx, 0x98, (long)(rsp + 8));
                        }
                        else
                        {
                            _fn[n] = imageBase + (uint)Marshal.ReadInt32(rf);
                            RtlVirtualUnwind(0, imageBase, pc, rf, ctx, out _, out _, IntPtr.Zero);
                        }
                    }
                }
            }
            catch { }
            finally { ResumeThread(_mainThread); }
            // --- ここまで ---
            if (n == 0) return;
            _samples++; _phaseSamples[ph & 3]++;
            Add(_rip, Where(_pc[0], _fn[0]));
            for (int i = 0; i < n; i++) if (InGa(_pc[i])) { Add(_leaf, Where(_pc[i], _fn[i])); break; }
            if (InUp(_pc[0])) Add(_up, $"UnityPlayer+0x{_fn[0] - _upLo:X}");
            var seen = new HashSet<string>();
            var chain = new List<string>();
            // ゲームの外（エンジン・DLL）にいるときは、ゲームの関数に戻るまでに通った DLL を先頭に並べる（同じ DLL が続く分はまとめる）
            for (int i = 0; i < n && !InGa(_pc[i]); i++)
            {
                var w = InUp(_pc[i]) ? $"[UP+0x{_fn[i] - _upLo:X}]" : Where(_pc[i], _fn[i]);
                if (chain.Count == 0 || chain[^1] != w) chain.Add(w);
                if (chain.Count >= 5) break;
            }
            for (int i = 0; i < n; i++)
            {
                if (!InGa(_pc[i])) continue;
                var w = Where(_pc[i], _fn[i]);
                if (seen.Add(w) && chain.Count < 10) chain.Add(w);
            }
            foreach (var w in seen) Add(_incl, w);
            if (chain.Count > 0) Add(_stacks, string.Join(" < ", chain));
        }

        // 関数の先頭アドレスから名前（IL2CPP のメソッド表と完全一致）。無ければ位置で示す
        static string Where(ulong pc, ulong fn)
        {
            if (InGa(pc))
            {
                if (fn != 0)
                {
                    int i = Array.BinarySearch(_addr, fn);
                    if (i >= 0) return _names[i];
                    return $"GA+0x{fn - _gaLo:X}";
                }
                return $"GA?+0x{pc - _gaLo:X}";
            }
            if (InUp(pc)) return "[UnityPlayer]";
            return "[" + ModuleOf(pc) + ExportNear(pc) + "]";
        }

        // DLL の公開関数名（直前の公開関数。Windows の DLL なら待ち・例外処理などが分かる）
        static readonly Dictionary<ulong, (uint[] rva, string[] name)> _exports = new();
        static unsafe string ExportNear(ulong pc)
        {
            if (!GetModuleHandleEx(0x4 | 0x2, (IntPtr)(long)pc, out var h) || h == IntPtr.Zero) return "";
            ulong b = (ulong)(long)h;
            if (!_exports.TryGetValue(b, out var t))
            {
                var rv = new List<(uint, string)>();
                try
                {
                    byte* p = (byte*)b;
                    int pe = *(int*)(p + 0x3C);
                    uint expRva = *(uint*)(p + pe + 24 + 112);   // PE32+ の DataDirectory[0]
                    if (expRva != 0)
                    {
                        byte* ed = p + expRva;
                        uint nNames = *(uint*)(ed + 24), funcs = *(uint*)(ed + 28), names = *(uint*)(ed + 32), ords = *(uint*)(ed + 36);
                        for (uint i = 0; i < nNames; i++)
                        {
                            string nm = Marshal.PtrToStringAnsi((IntPtr)(p + *(uint*)(p + names + i * 4)));
                            ushort ord = *(ushort*)(p + ords + i * 2);
                            rv.Add((*(uint*)(p + funcs + ord * 4), nm));
                        }
                    }
                }
                catch { }
                rv.Sort((x, y) => x.Item1.CompareTo(y.Item1));
                t = (rv.Select(x => x.Item1).ToArray(), rv.Select(x => x.Item2).ToArray());
                _exports[b] = t;
            }
            uint r = (uint)(pc - b);
            int i2 = Array.BinarySearch(t.rva, r);
            if (i2 < 0) i2 = ~i2 - 1;
            return i2 >= 0 && r - t.rva[i2] < 0x4000 ? "!" + t.name[i2] : "";
        }

        // GameAssembly / UnityPlayer 以外のアドレスが入っている DLL 名（サンプルを止めた後に引くので確保してよい）
        static readonly Dictionary<ulong, string> _mods = new();
        static string ModuleOf(ulong pc)
        {
            if (!GetModuleHandleEx(0x4 | 0x2, (IntPtr)(long)pc, out var h) || h == IntPtr.Zero) return "other";
            ulong key = (ulong)(long)h;
            if (_mods.TryGetValue(key, out var n)) return n;
            var sb = new StringBuilder(260);
            GetModuleFileName(h, sb, 260);
            n = System.IO.Path.GetFileName(sb.ToString());
            _mods[key] = n;
            return n;
        }

        static void Add(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var c) ? c + 1 : 1;
        static bool InGa(ulong a) => a >= _gaLo && a < _gaHi;
        static bool InUp(ulong a) => a >= _upLo && a < _upHi;

        // ---------- IL2CPP メソッドの位置表 ----------

        static ulong[] _addr = Array.Empty<ulong>();
        static string[] _names = Array.Empty<string>();

        static unsafe void BuildTable()
        {
            var list = new List<(ulong, string)>();
            try
            {
                var domain = IL2CPP.il2cpp_domain_get();
                uint count = 0;
                var asms = IL2CPP.il2cpp_domain_get_assemblies(domain, ref count);
                for (uint a = 0; a < count; a++)
                {
                    var image = IL2CPP.il2cpp_assembly_get_image(((IntPtr*)asms)[a]);
                    uint classes = IL2CPP.il2cpp_image_get_class_count(image);
                    for (uint c = 0; c < classes; c++)
                    {
                        var klass = IL2CPP.il2cpp_image_get_class(image, c);
                        string cn = null;
                        IntPtr iter = IntPtr.Zero, m;
                        while ((m = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                        {
                            var p = (ulong)Marshal.ReadIntPtr(m);
                            if (p == 0) continue;
                            cn ??= ClassName(klass);
                            list.Add((p, cn + "." + Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(m))));
                        }
                    }
                }
            }
            catch (Exception e) { Main.Log("[sampler] table failed: " + e.Message); }
            // 同じ実体を共有するメソッドは名前をまとめる
            var grouped = list.GroupBy(x => x.Item1).OrderBy(g => g.Key).ToList();
            _addr = grouped.Select(g => g.Key).ToArray();
            _names = grouped.Select(g => g.Count() == 1 ? g.First().Item2 : g.First().Item2 + $" (+{g.Count() - 1} shared)").ToArray();
        }

        static string ClassName(IntPtr klass)
        {
            var ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
            var n = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
            var decl = IL2CPP.il2cpp_class_get_declaring_type(klass);
            if (decl != IntPtr.Zero) return ClassName(decl) + "/" + n;
            return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
        }

        // ---------- Windows API ----------

        static void Module(string name, out ulong lo, out ulong hi)
        {
            lo = hi = 0;
            var h = GetModuleHandle(name);
            if (h == IntPtr.Zero) return;
            if (GetModuleInformation(Process.GetCurrentProcess().Handle, h, out var mi, (uint)Marshal.SizeOf<MODULEINFO>()))
            { lo = (ulong)mi.lpBaseOfDll; hi = lo + mi.SizeOfImage; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MODULEINFO { public IntPtr lpBaseOfDll; public uint SizeOfImage; public IntPtr EntryPoint; }

        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll"), SuppressGCTransition] static extern uint SuspendThread(IntPtr h);
        [DllImport("kernel32.dll"), SuppressGCTransition] static extern uint ResumeThread(IntPtr h);
        [DllImport("kernel32.dll"), SuppressGCTransition] static extern int GetThreadContext(IntPtr h, IntPtr ctx);
        [DllImport("kernel32.dll"), SuppressGCTransition] static extern IntPtr RtlLookupFunctionEntry(ulong pc, out ulong imageBase, IntPtr history);
        [DllImport("kernel32.dll"), SuppressGCTransition] static extern IntPtr RtlVirtualUnwind(uint type, ulong imageBase, ulong pc, IntPtr fn, IntPtr ctx, out IntPtr handlerData, out ulong establisher, IntPtr ptrs);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool GetModuleHandleEx(uint flags, IntPtr addr, out IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetModuleFileName(IntPtr module, StringBuilder name, int size);
        [DllImport("psapi.dll")] static extern bool GetModuleInformation(IntPtr proc, IntPtr mod, out MODULEINFO info, uint cb);
    }
}
