using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FPOptimize.Main), "FPOptimize", "1.0.0", "Claude")]
[assembly: MelonGame("Kyrylo Burlaka", "Fracture Point Release")]

namespace FPOptimize
{
    // ゲームの処理結果を変えずに、CPU の無駄な処理だけを減らす Mod。
    // 画質・操作・表示内容は変えない。差し替えに失敗した機能や、未検証のゲームバージョンでは何もしない（バニラのまま）。
    public class Main : MelonMod
    {
        public const string TestedBuildGuid = "4f260e57b1bf43b3b51f9793578eb875";
        public static string Dir;
        static StreamWriter _log;

        // 計測用の切り替え（false にすると全機能がバニラの処理に戻る）。他の Mod から反射で切り替える
        public static bool Enabled = true;
        // 検証モード：UserData/FPOptimize/verify.txt があると、元の処理と結果が一致するかを定期的に調べてログに出す
        public static bool Verify;
        public static string DisableList = "";

        public override void OnInitializeMelon()
        {
            Dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "FPOptimize");
            Directory.CreateDirectory(Dir);
            var logPath = Path.Combine(Dir, "fpoptimize.log");
            try { if (File.Exists(logPath)) File.Copy(logPath, Path.Combine(Dir, "fpoptimize.prev.log"), true); } catch { }
            _log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            Log("FPOptimize 1.0.0 start");
            Verify = File.Exists(Path.Combine(Dir, "verify.txt"));
            if (Verify) Log("[verify] verify mode ON");

            try
            {
                // 公式の Mod ルール：Mod を使っている間はリーダーボードに送信しない（共通の土台 FPModOption が止める）。止められなければ何もしない（バニラのまま）
                if (FPModOption.GameVersion.Disabled) { Log("[init] all mods are disabled (leaderboard block or update): all features OFF (vanilla)"); Enabled = false; return; }
                if (!FPModOption.Settings.Enabled("FPOptimize")) { Log("[init] turned off in the MOD tab: all features OFF (vanilla)"); Enabled = false; return; }

                var guid = ReadBuildGuid();
                if (File.Exists(Path.Combine(Dir, "simulate_update.txt"))) { guid = "simulated-update"; Log("[test] simulating a game update"); }
                Log($"[version] game build-guid={guid} tested={TestedBuildGuid}");
                // ゲームの処理を同じ結果になるよう置き換えているため、中身が変わった可能性のあるバージョンでは動かさない
                if (guid != TestedBuildGuid) { Log("[version] untested game build: all features OFF (vanilla)"); Enabled = false; return; }

                // 切り分け用：disable.txt に書いた機能は入れない
                var off = File.Exists(Path.Combine(Dir, "disable.txt")) ? File.ReadAllText(Path.Combine(Dir, "disable.txt")) : "";
                DisableList = off;
                if (off.Length > 0) Log("[init] disabled by disable.txt: " + off.Trim());
                if (!off.Contains("loc")) LocScan.Install(HarmonyInstance);
                if (!off.Contains("ai")) AiSight.Install(HarmonyInstance);
                if (!off.Contains("player")) PlayerCache.Install(HarmonyInstance);
                Log("[init] done");
            }
            catch (Exception e) { Enabled = false; Log("[init] exception, all features OFF: " + e); }
        }

        static string ReadBuildGuid()
        {
            try
            {
                var line = File.ReadAllLines(Path.Combine(Application.dataPath, "boot.config")).FirstOrDefault(l => l.StartsWith("build-guid="));
                return line?.Substring("build-guid=".Length).Trim() ?? "?";
            }
            catch (Exception e) { Log("[version] read failed: " + e.Message); return "?"; }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Log($"SCENE {buildIndex} {sceneName}");
            LocScan.OnSceneLoaded();
            PlayerCache.OnSceneLoaded();
        }

        public override void OnUpdate()
        {
            LocScan.Update();
            if (Verify) { LocScan.VerifyTick(); AiSight.VerifyTick(); }
        }

        public static void Log(string msg)
        {
            try { _log?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}"); } catch { }
        }
    }

    // Harmony パッチの登録。IL2CPP で「複数の関数が同じ実体に畳まれている」ものは差し替えると無関係な処理まで変わるので避ける
    public static class SafePatcher
    {
        static Dictionary<IntPtr, int> _ptrCount;
        static readonly string[] Assemblies = { "Assembly-CSharp", "Il2CppFPLeaderboard.Client", "Unity.TextMeshPro", "UnityEngine.UI", "UnityEngine.CoreModule" };

        static void BuildIndex()
        {
            _ptrCount = new Dictionary<IntPtr, int>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies().Where(a => Assemblies.Contains(a.GetName().Name)))
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

        // 成功したら true。共有実体・未発見なら false（ログに理由を残す）
        public static bool Patch(HarmonyLib.Harmony h, Type type, string method, Type[] args, Type patchClass,
                                 string prefix = null, string postfix = null)
        {
            if (_ptrCount == null) BuildIndex();
            var name = $"{type.Name}.{method}";
            var m = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
            if (m == null) { Main.Log($"[patch] NOT FOUND {name}"); return false; }

            var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m);
            var ptr = field == null ? IntPtr.Zero : ReadNativePtr(field);
            if (ptr == IntPtr.Zero) { Main.Log($"[patch] no native ptr {name}"); return false; }
            if (_ptrCount.TryGetValue(ptr, out var cnt) && cnt > 1)
            {
                Main.Log($"[patch] SHARED native body, skipped {name} @0x{ptr.ToInt64():X} (x{cnt})");
                return false;
            }

            HarmonyMethod Hm(string n) => n == null ? null : new HarmonyMethod(AccessTools.Method(patchClass, n));
            try
            {
                h.Patch(m, Hm(prefix), Hm(postfix));
                Main.Log($"[patch] ok {name}");
                return true;
            }
            catch (Exception e)
            {
                Main.Log($"[patch] FAIL {name}: {e}");
                return false;
            }
        }
    }
}
