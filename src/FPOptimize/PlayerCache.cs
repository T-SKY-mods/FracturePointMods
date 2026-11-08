using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using FPSController = Il2CppDemo.Scripts.Runtime.Character.FPSController;
using Object = UnityEngine.Object;

namespace FPOptimize
{
    // 敵が攻撃状態に入るたび（AttackAction.OnEnableAction）、ゲームは Unity の FindAnyObjectByType で
    // シーン全体（非表示のものも含む）からプレイヤー（FPSController）を探し直している。敵が多いと 1 秒に何十回も呼ばれて重い。
    //
    // 地面の物にカーソルを合わせている間も、ゲームはツールチップの文章を作るたび（毎フレーム）に
    // 表示中のものだけ（Exclude）で同じ検索をしていて、1 回 約 6ms かかる。
    //
    // シーン内のプレイヤー（FPSController）がちょうど 1 つのときは、探しても必ず同じものが返るので、
    // 見つけたものを覚えておいて返す。0 個や 2 個以上のとき・壊れたとき・新しく作られたときは元の検索をそのまま行う。
    public static class PlayerCache
    {
        static bool _active;
        static Il2CppSystem.Type _fpsType;
        static Object _cached;
        static bool _dirty = true;
        static bool _passThrough;   // このシーンでは 1 つに決まらなかったので元の処理のまま
        static long _hits, _fills, _verifyMismatch;
        static bool _verifying;

        public static void Install(HarmonyLib.Harmony _)
        {
            var h = new HarmonyLib.Harmony("FPOptimize.PlayerCache");
            _fpsType = Il2CppType.Of<FPSController>();
            bool ok = SafePatcher.Patch(h, typeof(Object), "FindAnyObjectByType", new[] { typeof(Il2CppSystem.Type), typeof(FindObjectsInactive) }, typeof(PlayerCache), nameof(FindPre))
                   && SafePatcher.Patch(h, typeof(FPSController), "Start", null, typeof(PlayerCache), null, nameof(StartPost));
            if (!ok)
            {
                try { h.UnpatchSelf(); } catch (Exception e) { Main.Log("[player] unpatch failed: " + e.Message); }
                Main.Log("[player] OFF (patch failed)");
                return;
            }
            _active = true;
            Main.Log("[player] ON");
        }

        public static void OnSceneLoaded()
        {
            _cached = null;
            _dirty = true;
            _passThrough = false;
        }

        static void StartPost()
        {
            // プレイヤーが新しく作られた：次の検索で数え直す
            _dirty = true;
            _passThrough = false;
        }

        static bool FindPre(Il2CppSystem.Type type, FindObjectsInactive findObjectsInactive, ref Object __result)
        {
            if (!_active || !Main.Enabled) return true;
            if (_verifying || type == null || type.Pointer != _fpsType.Pointer || _passThrough) return true;
            try
            {
                if (_dirty || _cached == null || _cached.WasCollected)
                {
                    var all = Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None);
                    _fills++;
                    _dirty = false;
                    if (all == null || all.Length != 1)
                    {
                        _cached = null;
                        _passThrough = true;
                        Main.Log($"[player] {all?.Length ?? 0} players in scene: using the game's own search here");
                        return true;
                    }
                    _cached = all[0];
                }
                // 表示中のものだけの検索：覚えているプレイヤーが非表示なら、元の検索に任せる（結果が変わりうる）
                if (findObjectsInactive == FindObjectsInactive.Exclude && !_cached.Cast<Component>().gameObject.activeInHierarchy) return true;
                _hits++;
                __result = _cached;
                if (Main.Verify && (_hits % 20) == 1)
                {
                    // 元の検索の結果と同じかを確かめる（この呼び出しは自分を素通りさせる）
                    _verifying = true;
                    try
                    {
                        var real = Object.FindAnyObjectByType(type, findObjectsInactive);
                        if (real == null || real.Pointer != _cached.Pointer) { _verifyMismatch++; Main.Log("[verify] player cache MISMATCH"); }
                    }
                    finally { _verifying = false; }
                    Main.Log("[verify] player " + Stats());
                }
                return false;
            }
            catch (Exception e)
            {
                _passThrough = true;
                Main.Log("[player] failed, pass-through: " + e.Message);
                return true;
            }
        }

        public static string Stats() => $"hits={_hits} fills={_fills} mismatch={_verifyMismatch}";
    }
}
