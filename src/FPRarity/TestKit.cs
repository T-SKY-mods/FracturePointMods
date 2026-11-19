using System.Linq;
using Il2Cpp;
using FPModOption;

namespace FPRarity
{
    // 自動テストの共通の準備。テストはプレイヤーのセーブのまま始まるので、メイン武器を持っていないこともある
    public static class TestKit
    {
        // メイン武器（大型）が手持ちに無ければ M16 を加える（セーブはテストの後で元に戻る）
        public static void EnsureMainWeapon()
        {
            if (Tracker.Held.Any(h => Tracker.ItemById(h.Roll.WeaponId)?.Type == WeaponItem.SlotType.Big)) return;
            if (Tracker.Held.Count >= 3)
            {
                var other = Tracker.Held.First();
                TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(other.Roll.WeaponId));
            }
            Main.Log("[test] no main weapon in the save: adding M16 for the test");
            TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(10));
        }
    }
}
