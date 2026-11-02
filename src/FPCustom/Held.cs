using System.Collections.Generic;
using System.Linq;
using FPModOption;
using Il2Cpp;

namespace FPCustom
{
    // 手持ち・倉庫の武器の種類（ゲームの並び順）。テスト・デバッグの手順で使う（レアリティmod の記録ではなく、ゲームの手持ちを見る）
    internal static class Tracker
    {
        public sealed class Roll { public int WeaponId; }
        public sealed class Entry { public Roll Roll; }
        public static List<Entry> Held => Weapons.HeldIds().Select(id => new Entry { Roll = new Roll { WeaponId = id } }).ToList();
        public static List<Roll> Stash => Weapons.StashIds().Select(id => new Roll { WeaponId = id }).ToList();
        public static WeaponItem ItemById(int id) => Weapons.ItemById(id);
        public static string NameOf(int id) => Weapons.NameOf(id);
        public static Inventory Inv => Weapons.Inv;
        public static void ApplyAll() => Weapons.ApplyAll();
    }
}
