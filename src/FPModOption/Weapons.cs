using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using FPSController = Il2CppDemo.Scripts.Runtime.Character.FPSController;
using BallisticWeapon = Il2CppBallistics.Weapon;

namespace FPModOption
{
    public enum Tier { Common = 0, Uncommon = 1, Rare = 2, Elite = 3, Legendary = 4 }

    // 武器の性能の種類（レアリティ・熟練度・アタッチメントで共通）
    public enum WeaponStat { Damage = 0, FireRate = 1, Control = 2, Ergo = 3 }

    // 武器の参照と、手に持っている武器の実体へ各 Mod の変更を当てる流れ。
    //   ゲームは手持ちが変わるたびに武器の実体を作り直すので、作り直し（InitializeWeapons）と装備（OnEquip）のたびに
    //   ① ゲームの値に戻す → ② 登録された順（FPRarity → FPCustom）に当てる
    //   どの Mod も無効ならゲームの値のまま
    public static class Weapons
    {
        // ---------- 武器の参照 ----------

        public static Inventory Inv => Refs.Inv;
        static FPSController Fps => Refs.Fps;

        static Dictionary<string, WeaponItem> _byPrefab;
        static Dictionary<int, WeaponItem> _byId;

        static void EnsureMaps()
        {
            if (_byPrefab != null) return;
            var data = Inv?.WeaponData?.Weapons;
            if (data == null) return;
            _byPrefab = new Dictionary<string, WeaponItem>();
            _byId = new Dictionary<int, WeaponItem>();
            for (int i = 0; i < data.Count; i++)
            {
                var wi = data[i];
                if (wi == null) continue;
                _byId[wi.Id] = wi;
                var go = wi.Weapon?.gameObject;
                if (go != null) _byPrefab[go.name] = wi;
            }
            Core.Log($"[map] weapons={_byId.Count} prefabMapped={_byPrefab.Count}: " +
                     string.Join(", ", _byPrefab.Select(kv => $"{kv.Key}=>{kv.Value.Id}")));
            try { StatScale.Apply(_byId.Values); } catch (Exception e) { Core.Log("[statscale] failed: " + e.Message); }
        }

        public static WeaponItem ItemByPrefab(string prefab)
        {
            EnsureMaps();
            return _byPrefab != null && prefab != null && _byPrefab.TryGetValue(prefab, out var wi) ? wi : null;
        }

        public static List<WeaponItem> All()
        {
            EnsureMaps();
            return _byId == null ? new List<WeaponItem>() : _byId.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        }

        public static WeaponItem ItemById(int id)
        {
            EnsureMaps();
            return _byId != null && _byId.TryGetValue(id, out var wi) ? wi : null;
        }

        public static string NameOf(int weaponId)
        {
            try { return ItemById(weaponId)?.GetName() ?? $"#{weaponId}"; } catch { return $"#{weaponId}"; }
        }

        // ゲームが決めている武器の基本のレアリティ（枠の色）
        public static Tier BaseTier(WeaponItem wi)
        {
            try { return wi == null ? Tier.Common : (Tier)(int)wi.GetRarity(); } catch { return Tier.Common; }
        }

        public static bool IsHandgun(int weaponId) => ItemById(weaponId) is { } wi && wi.Type == WeaponItem.SlotType.Small;

        static string PrefabName(GameObject go) => go == null ? null : go.name.Replace("(Clone)", "").Trim();

        // 実体（手に持っている武器）の種類。分からなければ -1
        public static int IdOf(DemoWeapon w) => w == null ? -1 : ItemByPrefab(PrefabName(w.gameObject))?.Id ?? -1;

        // 手持ちの武器の種類（ゲームの並び順）
        public static List<int> HeldIds()
        {
            var list = Inv?.WeaponsList?.weaponPrefabs;
            var ids = new List<int>();
            if (list == null) return ids;
            for (int i = 0; i < list.Count; i++) ids.Add(ItemByPrefab(list[i]?.name)?.Id ?? -1);
            return ids;
        }

        // 保管庫の武器の種類（ゲームの並び順）
        public static List<int> StashIds()
        {
            var entries = Refs.Bag?.saveData?.Weapons;
            var ids = new List<int>();
            if (entries == null) return ids;
            for (int i = 0; i < entries.Count; i++) ids.Add(entries[i]?.weaponId ?? -1);
            return ids;
        }

        public static DemoWeapon Active() => Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();

        public static void OnSceneChanged() { }

        // ---------- 手持ちの武器への反映 ----------

        // inst = 手に持っている実体、prefab = その元（ゲームの値）、weaponId、slot = 手持ちリストでの位置
        public delegate void Applier(GameObject inst, GameObject prefab, int weaponId, int slot);
        static readonly List<(int order, string name, Applier apply)> _appliers = new();

        // order の小さい順に当てる（FPRarity = 0、FPCustom = 100）
        public static void AddApplier(int order, string name, Applier apply)
        {
            _appliers.Add((order, name, apply));
            _appliers.Sort((a, b) => a.order.CompareTo(b.order));
            Core.Log($"[weapons] applier {name} ({order})");
        }

        // 当てる前に呼ぶ（FPRarity：手持ちの記録をゲームの手持ちと突き合わせる）。引数はきっかけの名前
        public static event Action<string> BeforeApply;

        // 反映の仕組みが使えるか（ゲームの関数の差し替えに成功したか）
        public static bool Ready { get; private set; }

        public static void Install(HarmonyLib.Harmony h)
        {
            bool a = SafePatcher.Patch(h, typeof(FPSController), "InitializeWeapons", null, null, nameof(InitializeWeapons_Post), typeof(Weapons));
            // 装備処理（反動アニメーションの初期化で fireRate を使う）より前に当てる
            bool b = SafePatcher.Patch(h, typeof(DemoWeapon), "OnEquip", null, nameof(OnEquip_Pre), nameof(OnEquip_Post), typeof(Weapons));
            Ready = a && b;
            if (!Ready) GameVersion.AddDegraded("武器の性能の反映");
        }

        static void Safe(string where, Action act)
        {
            try { act(); } catch (Exception e) { Core.Log($"[weapons] {where} ERROR {e}"); }
        }

        static void InitializeWeapons_Post() => Safe("InitializeWeapons", () => { RaiseBefore("InitializeWeapons"); ApplyAll(); });

        static void OnEquip_Pre(DemoWeapon __instance) => Safe("OnEquip", () =>
        {
            // 武器の追加直後は InitializeWeapons の完了前にここへ来るので、先に突き合わせておく
            RaiseBefore("OnEquip");
            ApplyTo(__instance);
        });

        // 反動パターンは装備時にプレイヤー側へ渡される。確実にこの 1 本用の値が使われるよう渡し直す
        static void OnEquip_Post(DemoWeapon __instance) => Safe("OnEquip", () =>
        {
            var rp = __instance?._recoilPattern;
            if (rp != null && __instance.recoilPatternSettings != null) rp.Init(__instance.recoilPatternSettings);
        });

        static void RaiseBefore(string why)
        {
            if (BeforeApply == null) return;
            foreach (Action<string> d in BeforeApply.GetInvocationList())
                try { d(why); } catch (Exception e) { Core.Log($"[weapons] before-apply ERROR {e}"); }
        }

        // 手に持っている武器すべて
        public static void ApplyAll()
        {
            var fps = Fps;
            var prefabs = Inv?.WeaponsList?.weaponPrefabs;
            if (fps == null || prefabs == null) return;
            var inst = fps.InstantiatedWeapons;
            var used = new HashSet<int>();
            for (int i = 0; i < inst.Count; i++)
            {
                var item = inst[i];
                if (item == null) continue;
                int k = SlotOf(prefabs, PrefabName(item.gameObject), i, used);
                if (k < 0) continue;
                used.Add(k);
                Apply(item.gameObject, prefabs[k], k);
            }
        }

        public static void ApplyTo(DemoWeapon w)
        {
            if (w == null) return;
            var fps = Fps;
            var prefabs = Inv?.WeaponsList?.weaponPrefabs;
            if (fps == null || prefabs == null) return;
            var inst = fps.InstantiatedWeapons;
            for (int i = 0; i < inst.Count; i++)
            {
                if (inst[i] == null || inst[i].Pointer != w.Pointer) continue;
                int k = SlotOf(prefabs, PrefabName(w.gameObject), i, null);
                if (k >= 0) Apply(w.gameObject, prefabs[k], k);
                return;
            }
        }

        // 実体 i に対応する手持ちリストの位置（ふつうは同じ位置。ずれていたら同じ種類の空いている位置）
        static int SlotOf(Il2CppSystem.Collections.Generic.List<GameObject> prefabs, string name, int i, HashSet<int> used)
        {
            if (i < prefabs.Count && prefabs[i] != null && prefabs[i].name == name && (used == null || !used.Contains(i))) return i;
            for (int k = 0; k < prefabs.Count; k++)
                if (prefabs[k] != null && prefabs[k].name == name && (used == null || !used.Contains(k))) return k;
            return -1;
        }

        static void Apply(GameObject inst, GameObject prefab, int slot)
        {
            if (inst == null || prefab == null) return;
            int id = ItemByPrefab(prefab.name)?.Id ?? -1;
            ResetToPrefab(inst, prefab);
            foreach (var (_, name, apply) in _appliers)
            {
                try { apply(inst, prefab, id, slot); }
                catch (Exception e) { Core.Log($"[weapons] {name} failed on {inst.name}: {e}"); }
            }
        }

        // ゲームの値に戻す（Mod を外した・付け替えたときに前の変更が残らないように）
        static void ResetToPrefab(GameObject inst, GameObject prefab)
        {
            var dw = inst.GetComponent<DemoWeapon>();
            var pdw = prefab.GetComponent<DemoWeapon>();
            if (dw != null && pdw != null)
            {
                dw.fireRate = pdw.fireRate;
                dw.recoilPatternSettings = pdw.recoilPatternSettings;
                dw.recoilData = pdw.recoilData;
            }
            var bws = inst.GetComponentsInChildren<BallisticWeapon>(true);
            var pbws = prefab.GetComponentsInChildren<BallisticWeapon>(true);
            for (int i = 0; i < bws.Length && i < pbws.Length; i++) bws[i].MuzzleDamage = pbws[i].MuzzleDamage;
        }
    }
}
