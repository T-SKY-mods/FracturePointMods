using System;
using FPModOption;
using HarmonyLib;

namespace FPCustom
{
    // 解放に使う「壊れた武器部品」（ゲームのアイテム）。手持ちと保管庫の合計を数え、保管庫から先に使う。
    // 戦利品のまとめ売り（LootSeller）では売らない：売る間だけ手持ちから外し、終わったら戻す
    public static class Parts
    {
        public const int ItemId = 82;

        public static int InInventory()
        {
            var inv = Refs.Inv;
            if (inv?.LootItems == null) return 0;
            foreach (var kv in inv.LootItems) if (kv.Key != null && kv.Key.Id == ItemId) return kv.Value;
            return 0;
        }

        public static int InStash()
        {
            var list = Refs.Bag?.saveData?.LootItems;
            if (list == null) return 0;
            int n = 0;
            foreach (var e in list) if (e != null && e.itemId == ItemId) n += e.count;
            return n;
        }

        public static int Count() => InInventory() + InStash();

        public static bool Take(int n)
        {
            if (Count() < n) return false;
            try
            {
                var bag = Refs.Bag;
                var list = bag?.saveData?.LootItems;
                int left = n;
                for (int i = list != null ? list.Count - 1 : -1; i >= 0 && left > 0; i--)
                {
                    var e = list[i];
                    if (e == null || e.itemId != ItemId) continue;
                    int k = Math.Min(left, e.count);
                    e.count -= k; left -= k;
                    if (e.count <= 0) list.RemoveAt(i);
                }
                int fromInv = Math.Min(left, InInventory());
                if (fromInv > 0) Refs.Inv.DeleteLoot(ItemId, fromInv, false);
                left -= fromInv;
                if (bag != null && fromInv < n) { bag.UpdateUI(); bag.SaveProgress(); }
                Main.Log($"[parts] used {n} (inventory {fromInv}, stash {n - fromInv}), left {Count()}");
                return left == 0;
            }
            catch (Exception e) { Main.Log("[parts] take failed: " + e); return false; }
        }

        // ---------- まとめ売りから外す ----------

        public static bool SellPatched;
        static Il2Cpp.LootItem _heldKey;
        static int _held;

        public static void Install(HarmonyLib.Harmony h)
        {
            try
            {
                var t = typeof(Il2Cpp.LootSeller);
                foreach (var name in new[] { "Interact", "GetInventoryLootPrice" })
                    h.Patch(AccessTools.Method(t, name), new HarmonyMethod(typeof(Parts), nameof(Hold)), new HarmonyMethod(typeof(Parts), nameof(Release)));
                SellPatched = true;
            }
            catch (Exception e) { Main.Log("[parts] sell patch failed: " + e.Message); }
        }

        static void Hold()
        {
            Release();
            try
            {
                var d = Refs.Inv?.LootItems;
                if (d == null) return;
                foreach (var kv in d)
                    if (kv.Key != null && kv.Key.Id == ItemId) { _heldKey = kv.Key; _held = kv.Value; break; }
                if (_heldKey != null) d.Remove(_heldKey);
            }
            catch (Exception e) { Main.Log("[parts] hold failed: " + e.Message); }
        }

        // 売った後に戻す（売る処理が途中で止まっても、次のフレームの Tick で戻す）
        public static void Release()
        {
            if (_heldKey == null) return;
            try
            {
                var d = Refs.Inv?.LootItems;
                if (d != null) d[_heldKey] = (d.ContainsKey(_heldKey) ? d[_heldKey] : 0) + _held;
            }
            catch (Exception e) { Main.Log("[parts] release failed: " + e.Message); }
            _heldKey = null; _held = 0;
        }

        public static string Name(bool ja)
        {
            try { var t = Il2CppGameLocalization.LocManager.Translate("Broken Weapon Parts"); if (!string.IsNullOrEmpty(t)) return t; } catch { }
            return ja ? "壊れた武器部品" : "Broken Weapon Parts";
        }
    }
}
