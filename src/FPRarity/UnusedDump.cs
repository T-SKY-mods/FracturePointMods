using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using BallisticWeapon = Il2CppBallistics.Weapon;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario unused のときだけ動く）。読み込まれている全データから、
    // 武器一覧（WeaponsData）に載っていない武器・武器データ・弾倉などを洗い出してログに書く。何も変更しない。
    public static class UnusedDump
    {
        public static List<(string, Action)> Steps() => new() { ("dump unused", Dump) };

        static IEnumerable<T> All<T>() where T : UnityEngine.Object
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<T>()))
            {
                var t = o.TryCast<T>();
                if (t != null) yield return t;
            }
        }

        static string Where(Component c) => c.gameObject.scene.IsValid() ? $"scene:{c.gameObject.scene.name}" : "asset";

        static string Path(Transform t)
        {
            var a = new List<string>();
            for (int i = 0; t != null && i < 6; i++, t = t.parent) a.Insert(0, t.name);
            return string.Join("/", a);
        }

        static void Dump()
        {
            var sb = new StringBuilder("===== UNUSED SCAN =====\n");
            var listed = Tracker.Inv.WeaponData.Weapons;
            var listedItems = new HashSet<IntPtr>();
            var listedPrefabs = new HashSet<string>();
            var listedNames = new HashSet<string>();
            for (int i = 0; i < listed.Count; i++)
            {
                listedItems.Add(listed[i].Pointer);
                if (listed[i].Weapon != null) listedPrefabs.Add(listed[i].Weapon.gameObject.name);
                try { listedNames.Add(listed[i].GetName().Trim()); } catch { }
            }

            sb.AppendLine("-- WeaponsData assets");
            foreach (var wd in All<WeaponsData>()) sb.AppendLine($"  '{wd.name}' count={wd.Weapons?.Count}");

            // WeaponItem は WeaponsData の中のデータ（アセットではない）なので、全 WeaponsData の中身を比べる
            sb.AppendLine("-- WeaponItem entries in other WeaponsData assets that are not in the main list");
            foreach (var wd in All<WeaponsData>())
                for (int k = 0; wd.Weapons != null && k < wd.Weapons.Count; k++)
                    if (!listedItems.Contains(wd.Weapons[k].Pointer))
                        sb.AppendLine($"  [{wd.name}] id={wd.Weapons[k].Id} name='{SafeName(wd.Weapons[k])}' prefab='{wd.Weapons[k].Weapon?.gameObject.name}'");

            sb.AppendLine("-- Player weapon prefabs (Demo Weapon) NOT used by the weapon list");
            foreach (var dw in All<DemoWeapon>())
            {
                var n = dw.gameObject.name.Replace("(Clone)", "").Trim();
                if (listedPrefabs.Contains(n)) continue;
                var bw = dw.GetComponentInChildren<BallisticWeapon>(true);
                sb.AppendLine($"  '{dw.gameObject.name}' [{Where(dw)}] fireRate={dw.fireRate} dmg={bw?.MuzzleDamage} auto={dw.supportsAuto} item='{SafeName(dw.WeaponItem)}' path={Path(dw.transform)}");
            }

            sb.AppendLine("-- Readiness of candidate weapons (compared with Tactic R-15 / M16 / SP-229)");
            var done = new HashSet<string>();
            foreach (var dw in All<DemoWeapon>())
            {
                var n = dw.gameObject.name.Replace("(Clone)", "").Trim();
                bool candidate = !listedPrefabs.Contains(n) && dw.transform.parent == null; // 拠点に単独で置かれている物
                bool reference = n is "ar15 ergonomic" or "m16" or "Mk23Mod0_Scriptable";
                if ((!candidate && !reference) || !done.Add(n + Where(dw))) continue;
                string N(UnityEngine.Object o) => o == null ? "-" : o.name;
                var magParts = dw.GetComponentsInChildren<Transform>(true).Where(t => t.name.ToLowerInvariant().Contains("mag")).Select(t => t.name).Take(6);
                sb.AppendLine($"  [{n}] {Where(dw)} reload={N(dw.reloadClip)} reloadEmpty={N(dw.reloadEmptyClip)} fire={N(dw.fireClip)} fireLast={N(dw.fireLastBulletClip)} " +
                              $"camReload={N(dw.cameraReloadAnimation)} recoilAnim={N(dw.recoilData)} recoilPat={N(dw.recoilPatternSettings)} shake={N(dw.cameraShake)} " +
                              $"hitMgr={(dw.GetComponentInChildren<FPSFireManager>(true) != null)} " +
                              $"scopes={dw.scopeGroups?.Count} grips={dw.gripAttachments?.attachments?.Count} mags=[{string.Join(",", magParts)}]");
            }

            sb.AppendLine("-- Ballistics guns without a player weapon (NPC / props)");
            var seen = new HashSet<string>();
            foreach (var bw in All<BallisticWeapon>())
            {
                if (bw.GetComponentInParent<DemoWeapon>(true) != null) continue;
                var key = $"{bw.gameObject.name}|{bw.MuzzleDamage}|{Where(bw)}";
                if (!seen.Add(key)) continue;
                sb.AppendLine($"  '{bw.gameObject.name}' [{Where(bw)}] dmg={bw.MuzzleDamage} speed={bw.MaxBulletSpeed} path={Path(bw.transform)}");
            }

            sb.AppendLine("-- Ground weapon pickups (LootableItem, Weapon) with unknown ItemId");
            foreach (var li in All<LootableItem>())
                if (li.ItemType == LootableItem.LootType.Weapon && (li.ItemId < 0 || li.ItemId >= listed.Count))
                    sb.AppendLine($"  '{li.gameObject.name}' [{Where(li)}] ItemId={li.ItemId}");

            sb.AppendLine("-- Mags whose WeaponName matches no listed weapon");
            var mags = Tracker.Inv.MagData.Mags;
            for (int i = 0; i < mags.Count; i++)
                if (!listedNames.Contains((mags[i].WeaponName ?? "").Trim()))
                    sb.AppendLine($"  index={mags[i].Index} WeaponName='{mags[i].WeaponName}' {mags[i].MaxAmmoCount}rds caliber={mags[i].Caliber} price={mags[i].price}");

            sb.AppendLine("-- Ammo types");
            foreach (var ad in All<AmmoData>())
                for (int i = 0; i < ad.Ammo.Count; i++)
                    sb.AppendLine($"  [{ad.name}] {ad.Ammo[i].Index} '{SafeName(ad.Ammo[i])}' caliber={ad.Ammo[i].Caliber} price={ad.Ammo[i].price}");

            sb.AppendLine("-- Store weapon list (what the shop can sell)");
            foreach (var st in All<Store>())
                sb.AppendLine($"  Store '{st.gameObject.name}' [{Where(st)}]");

            Main.Log(sb.ToString());
        }

        static string SafeName(WeaponItem it) { try { return it?.GetName() ?? "null"; } catch { return "?"; } }
        static string SafeName(Ammo it) { try { return it?.GetName() ?? "null"; } catch { return "?"; } }
    }
}
