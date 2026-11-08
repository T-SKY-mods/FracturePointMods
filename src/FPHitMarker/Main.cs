using System;
using FPModOption;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FPHitMarker.Main), "FPHitMarker", "1.0.0", "Claude")]
[assembly: MelonGame("Kyrylo Burlaka", "Fracture Point Release")]

namespace FPHitMarker
{
    // ヒットマーカー：自分の弾が敵に当たると画面の中央に斜めの十字を出す。
    //   ふつうは白、頭は二重の十字、倒した弾は赤、倍率が 1 倍未満の部位（腕・手）は青。ログは共通の fpmod.log（[hit]）
    public class Main : MelonMod
    {
        public static bool Active;

        public override void OnInitializeMelon()
        {
            try
            {
                if (GameVersion.Disabled) { Core.Log("[hit] all mods are disabled: hit marker OFF"); return; }
                if (!Settings.Enabled("FPHitMarker")) { Core.Log("[hit] turned off in the MOD tab"); return; }
                Active = Hits.Install(HarmonyInstance);
                HitTest.Register();
                Core.Log($"[hit] hit marker {(Active ? "on" : "OFF (patch failed)")}");
            }
            catch (Exception e) { Active = false; Core.Log("[hit] init failed: " + e); }
        }

        public override void OnUpdate()
        {
            if (!Active) return;
            try { Marker.Tick(); } catch (Exception e) { Core.LogOnce("hitmarker", "[hit] draw failed: " + e.Message); }
        }
    }

    public enum Kind { Normal, Weak, Head, Kill, HeadKill }

    // BulletHandler.BulletHit（弾が当たった部位ごとに体力を減らす）の前後で、当たった部位・倒したかを見る
    public static class Hits
    {
        static Il2CppBallistics.LivingEntity _target;
        static bool _wasAlive;
        static float _mult;
        static string _part;

        public static bool Install(HarmonyLib.Harmony h)
        {
            var m = AccessTools.Method(typeof(Il2CppBallistics.BulletHandler), "BulletHit");
            if (m == null) { Core.Log("[hit] BulletHandler.BulletHit not found"); return false; }
            // 武器カスタマイズmod が部位の倍率を変える前処理より後に見る（効いている倍率で青を決める）
            h.Patch(m, new HarmonyMethod(typeof(Hits), nameof(Pre)) { priority = Priority.Last }, new HarmonyMethod(typeof(Hits), nameof(Post)) { priority = Priority.First });
            return true;
        }

        static void Pre(Il2CppBallistics.Weapon weapon, Il2CppBallistics.LivingEntityCollider hitEntity)
        {
            _target = null;
            try
            {
                if (weapon == null || hitEntity == null) return;
                var le = hitEntity.ParentLivingEntity;
                if (le == null || le.PlayerHealthManager != null || le.EnemyHealth == null) return;   // 敵に当たったときだけ
                if (!FromPlayer(weapon)) return;
                _target = le;
                _wasAlive = Alive(le);
                _mult = hitEntity.DamageMultiplier;
                _part = hitEntity.gameObject.name;
            }
            catch { _target = null; }
        }

        static void Post()
        {
            try
            {
                if (_target == null || !_wasAlive) return;
                Marker.Show(Classify(_part, _mult, !Alive(_target)));
            }
            catch { }
            _target = null;
        }

        // 敵の体力は 2 つある（当たり判定側と敵側）。倒れるのは敵側が 0 になったときだけで、当たり判定側が先に 0 になっても動き続ける
        //   （特殊部隊やボス：当たり判定側 100 に対して、敵側は 300〜2000）
        public static bool Alive(Il2CppBallistics.LivingEntity le) => le.EnemyHealth != null ? le.EnemyHealth.health > 0f : !le.IsDead && le.Health > 0f;

        public static Kind Classify(string part, float mult, bool kill)
        {
            bool head = part != null && part.ToLowerInvariant().StartsWith("head");
            if (kill) return head ? Kind.HeadKill : Kind.Kill;
            if (head) return Kind.Head;
            return mult < 1f ? Kind.Weak : Kind.Normal;
        }

        // 自分の持っている武器の弾か（敵の弾は撃った敵の武器）
        static bool FromPlayer(Il2CppBallistics.Weapon weapon)
        {
            var item = Refs.Fps?.GetActiveItem();
            return item != null && weapon.transform.IsChildOf(item.transform);
        }
    }
}
