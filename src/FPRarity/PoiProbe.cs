using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using BallisticWeapon = Il2CppBallistics.Weapon;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario poiprobe）。構えたときに弾が画面の中心（サイトの照準）からどれだけずれて飛ぶかを、
    // サイトなし（アイアンサイト）とサイトありで比べる。弾は「弾の発射点の前方向＋ゼロイン補正」に飛ぶ（FPSFireManager.Fire → Weapon.ShootBullet）。
    public static class PoiProbe
    {
        static readonly int[] DefaultIds = { 10, 27, 13, 0 };
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();

        static void Equip(int wid)
        {
            var name = Tracker.ItemById(wid).Weapon.gameObject.name;
            var inst = Refs.Fps.InstantiatedWeapons;
            for (int i = 0; i < inst.Count; i++)
                if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == name) { Refs.Fps.StartWeaponChange(i); return; }
        }

        public static List<(string, Action)> Steps()
        {
            var f = System.IO.Path.Combine(Main.Dir, "poi_ids.txt");
            var ids = System.IO.File.Exists(f) ? System.IO.File.ReadAllText(f).Split(',').Select(x => int.Parse(x.Trim())).ToArray() : DefaultIds;
            var steps = new List<(string, Action)>();
            foreach (var id in ids)
            {
                int w = id;
                steps.Add(($"add {w}", () =>
                {
                    Mastery.SetKillsForTest(w, Mastery.KillsForLevel(Mastery.SightLevel));
                    Mastery.SetSight(w, false);
                    if (!Tracker.Held.Any(h => h.Roll.WeaponId == w)) TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(w));
                }));
                steps.Add(("wait", () => { }));
                steps.Add(("equip", () => Equip(w)));
                steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
                steps.Add(("aim", () => Active()?.OnAimPressed()));
                steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
                steps.Add(("log off", () => Log(w, "iron")));
                steps.Add(("release", () => Active()?.OnAimReleased()));
                steps.Add(("wait", () => { }));
                steps.Add(("sight on", () => Mastery.SetSight(w, true)));
                steps.Add(("wait", () => { }));
                steps.Add(("aim", () => Active()?.OnAimPressed()));
                steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
                steps.Add(("log on", () => Log(w, "sight")));
                steps.Add(("release", () => Active()?.OnAimReleased()));
                steps.Add(("wait", () => { }));
                steps.Add(("cleanup", () => { Mastery.SetSight(w, false); if (w != 20) TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(w)); }));
                steps.Add(("wait", () => { }));
            }
            return steps;
        }

        public static void Log(int id, string when)
        {
            var dw = Active(); var cam = Camera.main;
            if (dw == null || cam == null) { Main.Log("[poi] no weapon/camera"); return; }
            var bw = dw.GetComponentInChildren<BallisticWeapon>(true);
            var sp = bw?.PhysicalBulletSpawnPoint;
            if (sp == null) { Main.Log("[poi] no spawn point"); return; }
            var root = dw.transform;
            float corr = 0f;
            var zd = bw.BarrelZeroingDistances; var zc = bw.BarrelZeroingCorrections;
            if (zc != null && bw.currentBarrelZero >= 0 && bw.currentBarrelZero < zc.Count) corr = zc[bw.currentBarrelZero];
            var sb = new System.Text.StringBuilder($"[poi] [{id}] {Tracker.NameOf(id)} {when}: active={dw.gameObject.name} zero={bw.currentBarrelZero} dist=[");
            for (int i = 0; zd != null && i < zd.Count; i++) sb.Append($"{zd[i]:F0},");
            sb.Append("] corr=[");
            for (int i = 0; zc != null && i < zc.Count; i++) sb.Append($"{zc[i]:F4},");
            sb.Append($"] spawn(root)={F(root.InverseTransformPoint(sp.position))} spawnFwd(root)={F(root.InverseTransformDirection(sp.forward))} cam(root)={F(root.InverseTransformPoint(cam.transform.position))} camFwd(root)={F(root.InverseTransformDirection(cam.transform.forward))}");
            Main.Log(sb.ToString());
            // 実際に1発撃って（弾道の計算だけ。反動・弾数は使わない）、弾の情報（位置・向き・速さ）から着弾を計算する
            try
            {
                dw.hitManager.Fire();
                var bh = Il2CppBallistics.BulletHandler.instance;
                var arr = bh.Bullets.ToArray();
                var bd = arr[arr.Length - 1];
                float g = 9.81f;
                try { g = bh.g; } catch { }
                var c = cam.transform.position; var fw = cam.transform.forward;
                var line = new System.Text.StringBuilder($"[poi] [{id}] {when} BULLET pos(root)={F(root.InverseTransformPoint(bd.bulletPos))} dir(root)={F(root.InverseTransformDirection(bd.bulletDir))} speed={bd.Speed:F0} ySpeed={bd.ySpeed:F2} g={g:F2} offset={F(bd.VisualOffset)} start={F(bd.StartOffset)}:");
                var p = bd.bulletPos; float ys = bd.ySpeed; float dt = 0.0005f; int next = 0; var ds = new[] { 10f, 25f, 50f, 100f };
                for (int k = 0; k < 400000 && next < ds.Length; k++)
                {
                    p += bd.bulletDir * bd.Speed * dt; ys -= g * dt; p.y += ys * dt;
                    if (Vector3.Dot(p - c, fw) >= ds[next])
                    {
                        var off = p - (c + fw * ds[next]);
                        line.Append($" {ds[next]:F0}m=({Vector3.Dot(off, cam.transform.right) * 100f:F1},{Vector3.Dot(off, cam.transform.up) * 100f:F1})cm");
                        next++;
                    }
                }
                Main.Log(line.ToString());
                _track = bd; _trackC = c; _trackF = fw; _trackR = cam.transform.right; _trackU = cam.transform.up; _trackId = id; _trackWhen = when; _frames = 0;
            }
            catch (Exception e) { Main.Log("[poi] fire failed " + e.Message); }
            // 補正なし / 補正（発射点の右軸まわりに ±corr 度）それぞれで、画面の中心の線からのずれ（cm、右・上）
            foreach (var (label, dir) in new[] { ("raw", sp.forward), ("corr+", Quaternion.AngleAxis(corr, sp.right) * sp.forward), ("corr-", Quaternion.AngleAxis(-corr, sp.right) * sp.forward) })
            {
                var line = new System.Text.StringBuilder($"[poi] [{id}] {when} {label}:");
                foreach (var d in new[] { 10f, 25f, 50f, 100f })
                {
                    var c = cam.transform.position; var fw = cam.transform.forward;
                    float t = (d - Vector3.Dot(sp.position - c, fw)) / Vector3.Dot(dir, fw);
                    var q = sp.position + dir * t;
                    var off = q - (c + fw * d);
                    line.Append($" {d:F0}m=({Vector3.Dot(off, cam.transform.right) * 100f:F1},{Vector3.Dot(off, cam.transform.up) * 100f:F1})cm");
                }
                Main.Log(line.ToString());
            }
        }

        // 撃った弾のその後の位置（実際に飛んだ道筋）を、撃った瞬間の画面の中心の線からのずれで記録する
        static Il2CppBallistics.BulletData _track; static Vector3 _trackC, _trackF, _trackR, _trackU; static int _trackId; static string _trackWhen;
        static int _frames;
        public static void Tick() { if (_track != null && _frames++ < 8) Track(); }
        public static void Track()
        {
            if (_track == null) return;
            var p = _track.bulletPos; var d = Vector3.Dot(p - _trackC, _trackF);
            var off = p - (_trackC + _trackF * d);
            Main.Log($"[poi] [{_trackId}] {_trackWhen} TRACK d={d:F1}m off=({Vector3.Dot(off, _trackR) * 100f:F1},{Vector3.Dot(off, _trackU) * 100f:F1})cm dir={F(_track.bulletDir)} life={_track.lifeTime:F3}");
        }

        static string F(Vector3 v) => $"({v.x:F4},{v.y:F4},{v.z:F4})";
    }
}
