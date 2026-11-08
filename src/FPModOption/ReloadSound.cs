using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPModOption
{
    // 速くしたリロードの音。音の高さは変えずに、音声の中の一つひとつの音（マガジンを抜く・入れる など）の
    //   始まる時刻だけを速さに合わせて前に詰めた音声を作って流す。作った音声は元の音声と倍率ごとに使い回す。
    //   音声を読めないときは元の音のまま（高さも速さも変えない）。速さの係数を読めたビルドでだけ動く
    public static class ReloadSound
    {
        static readonly Dictionary<(IntPtr, int), AudioClip> _cache = new();
        const long VolumeRva = 0x425ABA4;   // OnReload が PlayOneShot に渡す音量（build 25787540）
        static float _volume = -1f;

        public static (string clip, float origLen, float newLen, int sounds) LastForTest;

        // リロードが始まった直後。ゲームは OnReload で武器のリロードの音（ReloadClip）を PlayOneShot で鳴らしている。
        //   それを止めて、詰めた音声を同じ音量で鳴らし直す
        public static void Start(DemoWeapon w, float m)
        {
            LastForTest = default;
            var a = w.ReloadAudio;
            var clip = w.ReloadClip;
            if (a == null || clip == null) return;
            var fast = Get(clip, m);
            LastForTest = (clip.name, clip.length, fast != null ? fast.length : 0f, _lastSounds);
            if (fast == null) return;
            a.Stop();
            a.PlayOneShot(fast, Volume());
        }

        public static float Volume()
        {
            if (_volume >= 0f) return _volume;
            _volume = 1f;
            try
            {
                var mod = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                    .FirstOrDefault(x => string.Equals(x.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
                if (mod != null)
                {
                    float v = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(mod.BaseAddress + (nint)VolumeRva));
                    if (v > 0.05f && v <= 2f) _volume = v;
                    Core.Log($"[reload] sound volume {v:0.###} (using {_volume:0.###})");
                }
            }
            catch (Exception e) { Core.Log("[reload] volume read failed: " + e.Message); }
            return _volume;
        }

        static int _lastSounds;

        // AudioClip.GetData / SetData は Mod の仕組み側で壊れているので、Unity の中の処理を直接呼ぶ
        [StructLayout(LayoutKind.Sequential)] struct SpanW { public IntPtr begin; public int length; }
        [return: MarshalAs(UnmanagedType.U1)] delegate bool DataCall(IntPtr self, ref SpanW data, int offset);
        static DataCall _get, _set;
        delegate void CreateCall(IntPtr self, ref SpanW name, int samples, int channels, int hz, [MarshalAs(UnmanagedType.U1)] bool stream);
        static CreateCall _create;

        // AudioClip.Create と同じ：空の音声を作って、長さと形を決める
        static unsafe AudioClip Create(string name, int samples, int ch, int hz)
        {
            var mi = typeof(AudioClip).GetMethod("Construct_Internal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (mi == null) { Core.Log("[reload] sound: no Construct_Internal"); return null; }
            var o = mi.Invoke(null, null) as AudioClip;
            if (o == null) { Core.Log("[reload] sound: Construct_Internal gave null"); return null; }
            _create ??= IL2CPP.ResolveICall<CreateCall>("UnityEngine.AudioClip::CreateUserSound_Injected");
            if (_create == null) { Core.Log("[reload] sound: no CreateUserSound"); return null; }
            fixed (char* p = name)
            {
                var w = new SpanW { begin = (IntPtr)p, length = name.Length };
                _create(o.m_CachedPtr, ref w, samples, ch, hz, false);
            }
            return o;
        }

        static unsafe bool Data(bool write, AudioClip clip, float[] buf)
        {
            _get ??= IL2CPP.ResolveICall<DataCall>("UnityEngine.AudioClip::GetData_Injected");
            _set ??= IL2CPP.ResolveICall<DataCall>("UnityEngine.AudioClip::SetData_Injected");
            if ((write ? _set : _get) == null) { Core.Log("[reload] sound: no " + (write ? "SetData" : "GetData")); return false; }
            fixed (float* p = buf)
            {
                var w = new SpanW { begin = (IntPtr)p, length = buf.Length };
                return (write ? _set : _get)(clip.m_CachedPtr, ref w, 0);
            }
        }

        static AudioClip Get(AudioClip clip, float m)
        {
            var key = (clip.Pointer, (int)MathF.Round(m * 20f));   // 音は 0.05 刻みで十分（作り直しを減らす）
            m = key.Item2 / 20f;
            if (_cache.TryGetValue(key, out var c) && c != null) { _lastSounds = -1; LastBuildMs = 0; return c; }
            c = Build(clip, m);
            _cache[key] = c;
            return c;
        }

        public static double LastBuildMs, LastReadMs;

        static AudioClip Build(AudioClip clip, float m)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (clip.loadState != AudioDataLoadState.Loaded) clip.LoadAudioData();
                int ch = clip.channels, n = clip.samples, hz = clip.frequency;
                if (ch <= 0 || n <= 0 || hz <= 0) return null;
                var src = new float[n * ch];
                if (!Data(false, clip, src)) { Core.Log($"[reload] sound '{clip.name}' can't be read ({clip.loadType}): kept as is"); return null; }
                LastReadMs = sw.Elapsed.TotalMilliseconds;

                // 10ms ごとの音の大きさ
                int win = Math.Max(1, hz / 100), nw = (n + win - 1) / win;
                var env = new float[nw];
                float peak = 0f;
                for (int k = 0; k < nw; k++)
                {
                    float v = 0f;
                    int e = Math.Min(n, (k + 1) * win) * ch;
                    for (int i = k * win * ch; i < e; i++) { float x = MathF.Abs(src[i]); if (x > v) v = x; }
                    env[k] = v; if (v > peak) peak = v;
                }
                if (peak <= 0f) return null;
                float thr = MathF.Max(peak * 0.06f, 0.002f);

                // 音の始まり：静かなところから大きくなった所、または急に大きくなった所（50ms 以上あける）
                var on = new List<int>();
                for (int k = 0; k < nw; k++)
                {
                    if (env[k] < thr) continue;
                    float before = float.MaxValue;
                    for (int j = Math.Max(0, k - 3); j < k; j++) before = MathF.Min(before, env[j]);
                    bool rise = k == 0 || before < thr * 0.5f || env[k] > before * 3f;
                    if (rise && (on.Count == 0 || k - on[^1] >= 5)) on.Add(k);
                }
                _lastSounds = on.Count;
                if (on.Count == 0) return null;

                // 始まりを 1/m の時刻に置く。次の音と重なる所は短くフェードして切る
                int pad = win;
                var starts = new int[on.Count];
                for (int i = 0; i < on.Count; i++) starts[i] = Math.Max(0, on[i] * win - pad);
                int fade = Math.Max(1, hz * 15 / 1000), fadeIn = Math.Max(1, hz * 2 / 1000);
                int outLen = 0;
                var place = new (int from, int len, int at)[on.Count];
                for (int i = 0; i < on.Count; i++)
                {
                    int at = (int)(starts[i] / m);
                    int len = (i + 1 < on.Count ? starts[i + 1] : n) - starts[i];
                    if (i + 1 < on.Count) len = Math.Min(len, (int)(starts[i + 1] / m) - at);
                    else len = n - starts[i];
                    place[i] = (starts[i], Math.Max(0, len), at);
                    outLen = Math.Max(outLen, at + len);
                }
                // 最後の音以外はその音の頭から次の音の頭までなので、元より長くはならない
                var dst = new float[Math.Max(1, outLen) * ch];
                for (int i = 0; i < place.Length; i++)
                {
                    var (from, len, at) = place[i];
                    bool cut = i + 1 < place.Length && len < starts[i + 1] - starts[i];
                    for (int f = 0; f < len; f++)
                    {
                        float g = 1f;
                        if (i > 0 && f < fadeIn) g = (f + 1f) / fadeIn;
                        if (cut && f >= len - fade) g = MathF.Min(g, (len - f) / (float)fade);
                        int si = (from + f) * ch, di = (at + f) * ch;
                        for (int c = 0; c < ch; c++) dst[di + c] += src[si + c] * g;
                    }
                }

                AudioClip o = null;
                try { o = Create(clip.name + "_x" + m.ToString("0.00"), Math.Max(1, outLen), ch, hz); }
                catch (Exception e) { Core.Log("[reload] sound create: " + e); }
                // 作れないときは元の音声を複製して、長さはそのまま（後ろは無音）で中身を書き換える
                if (o == null)
                {
                    o = UnityEngine.Object.Instantiate(clip);
                    if (o == null || o.samples < outLen || o.channels != ch) { Core.Log("[reload] sound: copy failed"); return null; }
                    if (dst.Length < o.samples * ch) Array.Resize(ref dst, o.samples * ch);
                    o.name = clip.name + "_x" + m.ToString("0.00");
                }
                o.hideFlags = HideFlags.DontUnloadUnusedAsset;
                if (!Data(true, o, dst)) { Core.Log($"[reload] sound '{clip.name}': write failed"); return null; }
                Core.Log($"[reload] sound '{clip.name}' x{m:0.00}: {on.Count} sounds, {clip.length:0.00}s -> {o.length:0.00}s, pitch kept, {sw.Elapsed.TotalMilliseconds:0.0} ms (read {LastReadMs:0.0})");
                LastBuildMs = sw.Elapsed.TotalMilliseconds;
                return o;
            }
            catch (Exception e) { Core.Log("[reload] sound build: " + e); return null; }
        }
    }
}
