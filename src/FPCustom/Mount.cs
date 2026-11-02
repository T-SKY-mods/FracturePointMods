using System;
using System.Collections.Generic;
using FPModOption;
using HarmonyLib;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // 手に持っている武器に、装着中の部品の見た目を付ける。
    // 共通の反映の流れ（Weapons）で、レアリティの後に呼ばれるので、持ち替え・作り直しのたびに付け直る
    public static class Mount
    {
        public const string Prefix = "FPC_";

        public static void Install(HarmonyLib.Harmony h)
        {
            if (!Weapons.Ready) Main.Log("[mount] weapon apply pipeline unavailable: visuals OFF");
            Weapons.AddApplier(100, "custom", Apply_All);
            // サイトはこの Mod で選ぶので、熟練度のサイト（レアリティmod）は載せない（両方あると照準点が合わない）
            Link.CustomSights = true;
            CustomUi.Changed += _ => { try { Weapons.ApplyAll(); } catch (Exception e) { Main.Log("[mount] re-apply failed: " + e.Message); } };
        }

        static void Apply_All(GameObject inst, GameObject prefab, int id, int slot)
        {
            if (inst == null || prefab == null || id < 0) return;
            try { Apply(inst, prefab, id); }
            catch (Exception e) { Main.Log($"[mount] failed on {inst.name}: {e}"); }
            // 付けたときの効果（見た目の後。サプレッサーの空の発砲炎は前の FPC_ を消した後に作る）
            try { Effects.Apply(inst, prefab, id); }
            catch (Exception e) { Main.Log($"[effect] failed on {inst.name}: {e}"); }
        }

        // ゲームの部品を複製して付けるもの（アタッチメント → 複製元）
        static Lab.Donor DonorFor(Att a) => a?.Id switch
        {
            "muzzle_brake" => Lab.ArBrake,
            "p_suppressor" => Lab.PistolSup,
            _ => null,
        };

        // QBZ 191 のホイールの構え（斜めのアイアンサイト）は、画面の中心が照星の先の丸に重なり、レーザーの点がそこに隠れる。
        // レーザーを付けたときだけ、目の位置を構えの上へ少し上げて、照星を点のすぐ下に見せる（遠くの当たる位置はほぼ変わらない）
        // 銃の向きでの、画面の上の方向（ホイールの構えは銃を右へ 34° 傾けて見る）× 上げる量
        static readonly Dictionary<int, Vector3> WheelLift = new() { [3] = new Vector3(0.56f, 0.83f, 0f) * 0.02f };
        static void LiftWheelAim(GameObject inst, GameObject prefab, int id, bool laser)
        {
            if (!WheelLift.TryGetValue(id, out var lift)) return;
            var g = inst.GetComponent<DemoWeapon>()?.scopeGroups; var pg = prefab.GetComponent<DemoWeapon>()?.scopeGroups;
            if (g == null || pg == null || g.Count < 2 || pg.Count < 2 || g[1]?.attachments == null || pg[1]?.attachments == null
                || g[1].attachments.Count == 0 || pg[1].attachments.Count == 0) return;
            var ap = g[1].attachments[0]?.aimPoint; var pap = pg[1].attachments[0]?.aimPoint;
            if (ap == null || pap == null) return;
            var local = prefab.transform.InverseTransformPoint(pap.position);
            ap.position = inst.transform.TransformPoint(local + (laser ? lift : Vector3.zero));
        }

        public static void Apply(GameObject inst, GameObject prefab, int id)
        {
            var root = inst.transform;
            // 前に付けたものを外す
            var old = new List<GameObject>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name.StartsWith(Prefix)) old.Add(t.gameObject);
            foreach (var g in old) UnityEngine.Object.DestroyImmediate(g);
            MuzzleCut.Undo(inst, prefab, id);   // 銃口の部品を除いた形を戻す（サイト・ハンドガードの差し替えより前に）

            // 最初から付いている部品：装着中ならそのまま、外していれば消す
            foreach (var p in Factory.On(id))
            {
                var a = Catalog.Get(p.Att);
                bool on = a != null && Catalog.LookOf(Store.Equipped(id, a.Cat))?.Id == p.Att;
                Factory.Show(root, prefab.transform, p, on);
            }

            var added = new List<string>();
            // グリップ（ゲームのグリップ枠を切り替える。左手の持ち方もゲームのまま）
            Grips.Apply(inst.GetComponent<DemoWeapon>(), prefab.GetComponent<DemoWeapon>(), id);
            // サイト（外したときはアイアンサイトの高さで狙う）
            Sights.Apply(inst, prefab, id);
            // グリップ用のレール（サイトの形の差し替えの後に。M16 は両方を合わせた形）
            Guards.Restore(inst, prefab, id);
            GameObject vgrip = null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == Prefix + "vgrip" || t.name == Prefix + "agrip" || t.name == Prefix + "bipod") { vgrip = t.gameObject; break; }
            Guards.Apply(inst, prefab, id, vgrip);

            // 銃口
            var mz = Store.Equipped(id, Cat.Muzzle);
            // 武器の銃口の部品（AKSU のブースター・Hunter-32 のハイダー）は、銃口の部品を付けるときは外し、その後ろの端に付ける
            if (Fits.Devices.TryGetValue(id, out var dev) && root.Find(dev)?.GetComponent<Renderer>() is { } dr && prefab.transform.Find(dev)?.GetComponent<Renderer>() is { } pdr)
                dr.enabled = pdr.enabled && (mz == null || Catalog.IsFactory(mz, id));
            // 銃身とひとつのメッシュの銃口の部品（AK-74・QBZ・M16）は、銃口の部品を付けるとき除いた形に
            if (mz != null && !Catalog.IsFactory(mz, id) && Fits.Muzzles.ContainsKey(id)) MuzzleCut.Apply(inst, prefab, id);
            if (mz != null && !Catalog.IsFactory(mz, id) && DonorFor(mz) is { } d && Fits.Muzzles.TryGetValue(id, out var f))
            {
                if (Lab.Attach(root, prefab.transform, d, f.At(f.Tip - f.Back)) != null) added.Add(mz.Id);
                else Main.Log($"[mount] {mz.Id}: game part not found");
            }
            else if (mz != null && !Catalog.IsFactory(mz, id) && AssetParts.MuzzleModel(id, mz) is { } pm && Fits.Muzzles.TryGetValue(id, out var f2))
            {
                if (AssetParts.AttachRear(root, pm, f2.At(AssetParts.RearZ(id, pm, f2.Tip, f2.Back)), mz.Id) != null) added.Add(mz.Id);
            }
            // ストック（ハンドガン）
            var st = Store.Equipped(id, Cat.Stock);
            if (st != null && AssetParts.Supported(id, st) && AssetParts.AttachStock(root, id, st.Id) != null) added.Add(st.Id);
            // 射撃音（サプレッサー）
            Sound.Apply(inst.GetComponent<DemoWeapon>(), prefab.GetComponent<DemoWeapon>(), id);
            // サイドレール（レーザー。付けている間はずっと点いている）
            var sr = Store.Equipped(id, Cat.SideRail);
            if (sr != null && !Catalog.ShowsFactory(sr, id) && Fits.Lasers.TryGetValue(id, out var lp))
            {
                // メイン武器：左手の指先より前に（ホイールの構えで光線の出口が手に隠れないように。ゲームのレーザーも手より前）。銃口の部品にはかからない
                if (!sr.Pistol)
                {
                    var ls = LaserLab.DonorSize(LaserLab.Rifle);
                    float front = Fits.Muzzles.TryGetValue(id, out var mzf) ? mzf.Tip - mzf.Back - 0.02f : float.MaxValue;
                    lp.z = Mathf.Min(Mathf.Max(lp.z, Fits.HandFrontZ(id) + ls.z / 2f), front - ls.z / 2f);
                }
                // レール付きハンドガードに替えたときは、その右側面に
                if (!sr.Pistol && root.Find(Prefix + "guard") != null && Guards.Replace.TryGetValue(id, out var gd))
                {
                    var ls = LaserLab.DonorSize(LaserLab.Rifle);
                    lp.x = gd.Max.x + ls.x / 2f;
                    lp.z = Mathf.Min(lp.z, gd.Max.z - ls.z / 2f - 0.005f);   // 前にはみ出さない
                }
                if (LaserLab.Attach(root, sr.Pistol ? LaserLab.Pistol : LaserLab.Rifle, lp, true) != null) added.Add(sr.Id);
                else Main.Log($"[mount] {sr.Id}: game part not found");
            }
            // レーザー（最初から付いているものも）：光線を、弾の通り道と 15m 先で交わる向きに（光線の先に弾が当たるように）
            LaserLab.AimAtBullets(inst, prefab);
            LaserLab.Tint(inst, sr?.Beam);
            LiftWheelAim(inst, prefab, id, root.Find(Prefix + "laser") != null);
            Main.Log($"[mount] {inst.name} (id {id}): {Store.Describe(id)} added=[{string.Join(",", added)}]");
        }

        // テスト用：付けた部品の数（label = 複製元の名前。null ならすべて）
        public static int Count(Transform root, string label = null)
        {
            int n = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == Prefix + label || (label == null && t.name.StartsWith(Prefix))) n++;
            return n;
        }
    }
}
