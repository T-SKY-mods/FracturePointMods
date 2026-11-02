using System;
using System.Collections.Generic;
using System.Linq;
using FPModOption;
using Il2Cpp;

namespace FPSurvival
{
    // 拠点の端末（2 ページ目）から始める処理：持ち物の確認 → 預かり → 弾とマガジンの支給 → ゲームのエンドレス開始を呼ぶ
    public static class Survival
    {
        public const int StartMags = 5;
        public const int StartAmmo = 250;
        internal static bool Bypass;   // ゲームのエンドレス開始をそのまま通す

        // 手持ちの武器とそのレアリティ（レアリティmod が無い・オフなら null）
        public class HeldWeapon { public WeaponItem Item; public Tier? Tier; }

        public static List<HeldWeapon> Held(Inventory inv)
        {
            var list = new List<HeldWeapon>();
            var prefabs = inv?.WeaponsList?.weaponPrefabs;
            if (prefabs == null) return list;
            for (int i = 0; i < prefabs.Count; i++)
            {
                if (prefabs[i] == null) continue;
                var wi = Weapons.ItemByPrefab(prefabs[i].name);
                if (wi == null) continue;
                list.Add(new HeldWeapon { Item = wi, Tier = Link.HeldTierAt(i, prefabs[i].name) });
            }
            return list;
        }

        public static string Label(HeldWeapon w) =>
            w == null ? "-" : w.Tier != null ? $"{w.Item.GetName()} ({Link.NameOfTier(w.Tier.Value)})" : w.Item.GetName();

        // 記録に残す形（武器名|レアリティ）。表示はそのときの言語で Show が作る
        public static string RecordKey(HeldWeapon w) =>
            w == null ? null : w.Tier != null ? $"{w.Item.GetName()}|{(int)w.Tier.Value}" : w.Item.GetName();

        public static string Show(string key)
        {
            if (string.IsNullOrEmpty(key)) return "-";
            int bar = key.LastIndexOf('|');
            if (bar > 0 && int.TryParse(key.Substring(bar + 1), out var t)) return $"{key.Substring(0, bar)} ({Link.NameOfTier((Tier)t)})";
            // 以前の記録は「武器名 (レアリティ名)」をその時の言語で残している
            foreach (Tier tier in Enum.GetValues(typeof(Tier)))
                foreach (var n in new[] { tier.ToString(), JaTier(tier) })
                    if (n != null && key.EndsWith($" ({n})")) return $"{key.Substring(0, key.Length - n.Length - 3)} ({Link.NameOfTier(tier)})";
            return key;
        }

        static string JaTier(Tier t)
        {
            try { return Il2CppGameLocalization.LocManager.GetCatalog("ja")?.Translate(t.ToString()); } catch { return null; }
        }

        // メイン（大きい武器）・サブ（小さい武器）がそれぞれ 1 本までか。問題があれば理由を返す
        public static string CheckLoadout(List<HeldWeapon> held)
        {
            int big = held.Count(w => w.Item.Type == WeaponItem.SlotType.Big);
            int small = held.Count(w => w.Item.Type == WeaponItem.SlotType.Small);
            if (big > 1 || small > 1) return Main.T("大型武器と小型武器は 1 つずつまでです。残りは保管庫にしまってください", "Bring at most one big and one small weapon. Put the rest in your stash");
            if (big + small == 0) return Main.T("武器を持っていません", "You have no weapon");
            return null;
        }

        // 縛りのポイント（Waves.WeaponPoints / ArmorPoints の合計）。大型・小型それぞれ、持っている中でいちばん良い武器で数える
        public static int HandicapPoints(Inventory inv, out string detail)
        {
            var held = Held(inv);
            int W(WeaponItem.SlotType slot)
            {
                var ws = held.Where(w => w.Item.Type == slot).ToList();
                return ws.Count == 0 ? Waves.WeaponPoints(null, false) : ws.Min(w => Waves.WeaponPoints(w.Tier, true));
            }
            int? R(Armor a) { if (a == null) return null; try { return (int)a.GetRarity(); } catch { return 4; } }
            int big = W(WeaponItem.SlotType.Big), small = W(WeaponItem.SlotType.Small);
            int helmet = Waves.ArmorPoints(R(inv?.HelmetEquipped)), vest = Waves.ArmorPoints(R(inv?.VestEquipped));
            detail = $"big {big} small {small} helmet {helmet} vest {vest}";
            return big + small + helmet + vest;
        }

        public static float CaliberOf(Inventory inv, WeaponItem wi)
        {
            if (inv == null || wi == null) return 0f;
            var name = wi.GetName();
            foreach (var m in inv.MagData.Mags) if (m != null && m.WeaponName == name) return m.Caliber;
            var fps = Refs.Fps;
            if (fps?.InstantiatedWeapons != null)
                foreach (var item in fps.InstantiatedWeapons)
                {
                    var w = item?.TryCast<Il2CppDemo.Scripts.Runtime.Item.Weapon>();
                    if (w != null && w.WeaponItem != null && w.WeaponItem.Id == wi.Id && w.SelectedMag != null) return w.SelectedMag.Caliber;
                }
            return 0f;
        }

        public static void Start(ButtonStartEndlessMode button)
        {
            var inv = Refs.Inv;
            if (inv == null) return;
            if (Stash.Pending != null) { Main.Notify(Main.T("前回のサバイバルの預かり品を返却中です。少し待ってください", "Still returning items from the last survival run"), true); return; }
            var held = Held(inv);
            var err = CheckLoadout(held);
            if (err != null) { Main.Notify(err, true); Main.Log("[start] refused: " + err); return; }
            var main = held.FirstOrDefault(w => w.Item.Type == WeaponItem.SlotType.Big);
            var sub = held.FirstOrDefault(w => w.Item.Type == WeaponItem.SlotType.Small);

            Stash.Begin(inv, RecordKey(main), RecordKey(sub));
            GiveLoadout(inv, held);
            Stash.UpdateHandicap(inv);
            // ゲームのエンドレス開始は $500 を引く。サバイバルは無料なので、引かれる分だけ置いておく
            inv.DollarsCount = 500f;
            Main.Log($"[start] survival: main={Label(main)} sub={Label(sub)}");
            Bypass = true;
            Arena.StopLuckyCat();
            try { button.StartEndlessRun(); }
            finally { Bypass = false; }
        }

        // 武器ごとにマガジン 5 本（弾は満タン）と予備の弾 250 発
        static void GiveLoadout(Inventory inv, List<HeldWeapon> held)
        {
            foreach (var w in held)
            {
                for (int i = 0; i < StartMags; i++) inv.AddMag(w.Item.Id);
                var cal = CaliberOf(inv, w.Item);
                if (cal > 0f) inv.AddAmmo(cal, StartAmmo);
                else Main.Log($"[start] caliber unknown for {w.Item.GetName()}");
            }
            // 拡張マガジンを付けた武器は、マガジンを大きくしてから満タンにする（先に満たすと元の大きさの弾数のまま始まる）
            try { Link.SyncMags?.Invoke(); } catch (System.Exception e) { Main.Log("[start] mag sync failed: " + e.Message); }
            foreach (var m in inv.MagStored) if (m != null) m.AmmoCount = m.MaxAmmoCount;
            Main.Log("[start] loadout: " + Stash.Describe(Stash.Take(inv)));
        }
    }
}
