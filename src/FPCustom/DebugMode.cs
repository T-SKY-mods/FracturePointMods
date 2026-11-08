using System;
using System.IO;
using System.Linq;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // 確認用のデバッグモード（UserData/FPCustom/debug がある間だけ）。
    //   ・すべてのアタッチメントを解放済みにする（解放の記録は書かない）
    //   ・部屋に入ったら、持っていない種類の武器を 1 丁ずつ保管庫へ入れる（手持ちがいっぱいなら最後の 1 丁を保管庫へ移す）
    // 終わったらセーブ（とレジストリ）をデバッグ前の控えに戻し、Mod と UserData/FPCustom を外す（tools/debug_mode.ps1）
    public static class DebugMode
    {
        // ファイルの確認は毎フレームやると重いので、数秒に 1 回だけ（部屋に入った直後は確認し直す）
        public static bool On
        {
            get
            {
                if (Time.unscaledTime >= _onNext || _onScene != Core.Scene)
                {
                    _on = Main.Dir != null && File.Exists(Path.Combine(Main.Dir, "debug"));
                    _onNext = Time.unscaledTime + 5f; _onScene = Core.Scene;
                }
                return _on;
            }
        }
        static bool _on;
        static float _onNext;
        static string _onScene;

        static bool _done;
        static float _next;
        static int _pending = -1;   // 手持ちに入れて、保管庫へ移すのを待っている武器

        public static void Tick()
        {
            if (_done || Core.Scene != Core.HubScene || !On || Refs.Bag == null || Tracker.Inv == null) return;
            if (_next == 0f) { _next = Time.unscaledTime + 3f; return; }   // 部屋に入ってから少し待つ
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1.0f;
            try
            {
                if (_pending >= 0)
                {
                    Refs.Bag.MoveWeaponToStash(Tracker.ItemById(_pending));
                    Main.Log($"[debug] {Tracker.NameOf(_pending)} -> stash");
                    _pending = -1;
                    return;
                }
                var w = Tracker.Inv.WeaponData.Weapons.ToArray().FirstOrDefault(x => Catalog.Eligible(x)
                    && !Tracker.Stash.Any(r => r.WeaponId == x.Id) && !Tracker.Held.Any(h => h.Roll.WeaponId == x.Id));
                if (w == null) { _done = true; Main.Log("[debug] every weapon is in the stash or in hand"); return; }
                if (Tracker.Held.Count >= 3)
                {
                    var last = Tracker.Held.Last().Roll.WeaponId;
                    Refs.Bag.MoveWeaponToStash(Tracker.ItemById(last));
                    Main.Log($"[debug] hands full: {Tracker.NameOf(last)} -> stash");
                    return;
                }
                Main.Log($"[debug] add {w.GetName()} -> {Tracker.Inv.AddWeapon(Tracker.ItemById(w.Id))}");
                _pending = w.Id;
            }
            catch (Exception e) { _done = true; Main.Log("[debug] failed: " + e); }
        }
    }
}
