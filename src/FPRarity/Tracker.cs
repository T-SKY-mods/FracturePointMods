using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using FPSController = Il2CppDemo.Scripts.Runtime.Character.FPSController;
using BallisticWeapon = Il2CppBallistics.Weapon;

using FPModOption;

namespace FPRarity
{
    // 手持ち・倉庫・地面の武器とレアリティの対応を管理する。
    //
    // ゲームは手持ちが変わるたびに武器の実体を作り直すので、実体にはレアリティを持たせられない。
    // 代わりに「手持ちリスト（Inventory.WeaponsList.weaponPrefabs）と同じ並びのリスト」を持ち、
    // 変化のたびにゲーム側のリストと突き合わせ（LCS）て、増えた分・減った分を判定する。
    public static class Tracker
    {
        public class HeldEntry
        {
            public string Prefab { get; set; }
            public WeaponRoll Roll { get; set; }
        }

        public enum Dest { None, Stash, Drop, Sold }

        public static List<HeldEntry> Held = new();
        public static List<WeaponRoll> Stash = new();

        // 起動直後の最初の突き合わせでは、記録にない武器を「Mod導入前の武器」として扱う
        static bool _heldBootstrapped, _stashBootstrapped;

        // 操作中の文脈（パッチの prefix で設定し postfix で解除）
        public static Dest Outgoing = Dest.None;
        static readonly List<WeaponRoll> _incomingHeld = new();   // 手持ちに入ってくる予定のロール
        static readonly List<WeaponRoll> _toStash = new();        // 手持ちから倉庫へ移る途中のロール
        static readonly Queue<WeaponRoll> _dropped = new();       // 捨てられて地面に出る途中のロール
        static WeaponRoll _stashTaken;                           // 倉庫から取り出し中のロール
        static (int lootId, WeaponRoll roll)? _pickup;           // 拾っている最中の地面の武器

        // 地面の武器（LootableItem のインスタンスID → ロール）
        public static readonly Dictionary<int, WeaponRoll> Ground = new();


        // ---------- ゲーム参照 ----------

        public static Inventory Inv => Refs.Inv;
        static FPSController Fps => Refs.Fps;

        public static WeaponItem ItemByPrefab(string prefab) => Weapons.ItemByPrefab(prefab);
        public static WeaponItem ItemById(int id) => Weapons.ItemById(id);
        public static string NameOf(int weaponId) => Weapons.NameOf(weaponId);
        public static Tier BaseTier(WeaponItem wi) => Weapons.BaseTier(wi);

        static List<string> CurrentHeldPrefabs()
        {
            var list = Inv?.WeaponsList?.weaponPrefabs;
            if (list == null) return null;
            var names = new List<string>(list.Count);
            for (int i = 0; i < list.Count; i++) names.Add(list[i] != null ? list[i].name : null);
            return names;
        }

        static Il2CppSystem.Collections.Generic.List<WeaponStashEntry> CurrentStashEntries()
        {
            var bag = Refs.Bag;
            return bag?.saveData?.Weapons;
        }

        // ---------- 手持ちの突き合わせ ----------

        public static void ReconcileHeld(string reason)
        {
            var cur = CurrentHeldPrefabs();
            if (cur == null) return;
            var prev = Held.Select(h => h.Prefab).ToList();
            if (prev.SequenceEqual(cur)) { _heldBootstrapped = true; return; }

            var (prevMatched, curMatched) = Lcs(prev, cur);
            var next = new List<HeldEntry>(cur.Count);
            var created = new List<WeaponRoll>();
            for (int j = 0; j < cur.Count; j++)
            {
                if (curMatched[j] >= 0) { next.Add(Held[curMatched[j]]); continue; }

                var wi = ItemByPrefab(cur[j]);
                int id = wi?.Id ?? -1;
                WeaponRoll roll = TakeIncoming(id);
                if (roll == null && !_heldBootstrapped) roll = TakeSpare(id, fromStash: true);
                if (roll == null)
                {
                    roll = _heldBootstrapped ? RarityTable.Roll(wi, BaseTier(wi)) : RarityTable.Legacy(id, BaseTier(wi));
                    if (_heldBootstrapped) created.Add(roll);
                }
                roll.WeaponId = id;
                next.Add(new HeldEntry { Prefab = cur[j], Roll = roll });
                Main.Log($"[held] + {cur[j]} ({NameOf(id)}) {roll}");
            }
            for (int i = 0; i < prev.Count; i++)
            {
                if (prevMatched[i] >= 0) continue;
                var r = Held[i].Roll;
                Main.Log($"[held] - {Held[i].Prefab} ({NameOf(r.WeaponId)}) {r} -> {Outgoing}");
                if (!_heldBootstrapped) { KeepSpare(r, "the inventory"); continue; }
                switch (Outgoing)
                {
                    case Dest.Stash: _toStash.Add(r); break;
                    case Dest.Drop: _dropped.Enqueue(r); break;
                }
            }
            Held = next;
            _heldBootstrapped = true;
            Main.Log($"[held] reconciled ({reason}): " + string.Join(" | ", Held.Select(h => $"{NameOf(h.Roll.WeaponId)}:{h.Roll.Tier}")));

            foreach (var r in created.Concat(_announce)) Announce(r);
            _announce.Clear();
        }

        static readonly List<WeaponRoll> _announce = new();

        // 他の Mod（FPSurvival の武器箱）が、これから手持ちに入れる武器のロールを先に決めておく。入ったときに通知も出す
        public static void ExpectIncoming(WeaponRoll roll, bool announce = true)
        {
            if (roll == null) return;
            _incomingHeld.Add(roll);
            if (announce) _announce.Add(roll);
        }

        static WeaponRoll TakeIncoming(int weaponId)
        {
            if (_pickup is { } p && p.roll.WeaponId == weaponId)
            {
                _pickup = null;
                Ground.Remove(p.lootId);
                _announce.Add(p.roll);
                return p.roll;
            }
            var idx = _incomingHeld.FindIndex(r => r.WeaponId == weaponId);
            if (idx < 0) return null;
            var roll = _incomingHeld[idx];
            _incomingHeld.RemoveAt(idx);
            return roll;
        }

        // ---------- 倉庫の突き合わせ ----------

        public static void ReconcileStash(string reason)
        {
            var entries = CurrentStashEntries();
            if (entries == null) return;
            var cur = new List<int>(entries.Count);
            for (int i = 0; i < entries.Count; i++) cur.Add(entries[i]?.weaponId ?? -1);
            var prev = Stash.Select(r => r.WeaponId).ToList();
            if (prev.SequenceEqual(cur)) { _stashBootstrapped = true; return; }

            var (prevMatched, curMatched) = Lcs(prev, cur);
            var next = new List<WeaponRoll>(cur.Count);
            for (int j = 0; j < cur.Count; j++)
            {
                if (curMatched[j] >= 0) { next.Add(Stash[curMatched[j]]); continue; }
                var idx = _toStash.FindIndex(r => r.WeaponId == cur[j]);
                WeaponRoll roll;
                if (idx >= 0) { roll = _toStash[idx]; _toStash.RemoveAt(idx); }
                else roll = (!_stashBootstrapped ? TakeSpare(cur[j], fromStash: false) : null) ?? RarityTable.Legacy(cur[j], BaseTier(ItemById(cur[j])));
                next.Add(roll);
                Main.Log($"[stash] + {NameOf(cur[j])} {roll}");
            }
            for (int i = 0; i < prev.Count; i++)
            {
                if (prevMatched[i] >= 0) continue;
                Main.Log($"[stash] - {NameOf(Stash[i].WeaponId)} {Stash[i]}");
                if (!_stashBootstrapped) KeepSpare(Stash[i], "the stash");
            }
            Stash = next;
            _stashBootstrapped = true;
        }

        // ---------- 操作ごとのフック ----------

        public static void BeginMoveToStash() => Outgoing = Dest.Stash;

        public static void EndMoveToStash()
        {
            ReconcileHeld("MoveWeaponToStash");
            ReconcileStash("MoveWeaponToStash");
            if (_toStash.Count > 0) Main.Log($"[stash] WARN {_toStash.Count} roll(s) never reached stash");
            _toStash.Clear();
            Outgoing = Dest.None;
        }

        public static void BeginMoveToInventory(WeaponStashEntry entry)
        {
            var entries = CurrentStashEntries();
            if (entries == null || entry == null) return;
            ReconcileStash("pre MoveWeaponToInventory");
            for (int i = 0; i < entries.Count && i < Stash.Count; i++)
            {
                if (entries[i] != null && entries[i].Pointer == entry.Pointer)
                {
                    // ゲームが消すのはこのエントリなので、こちらも同じ位置を外す
                    _stashTaken = Stash[i];
                    Stash.RemoveAt(i);
                    _incomingHeld.Add(_stashTaken);
                            Main.Log($"[stash] taking #{i} {NameOf(_stashTaken.WeaponId)} {_stashTaken}");
                    return;
                }
            }
            Main.Log("[stash] WARN entry not found for MoveWeaponToInventory");
        }

        public static void EndMoveToInventory()
        {
            ReconcileHeld("MoveWeaponToInventory");
            ReconcileStash("MoveWeaponToInventory");
            if (_stashTaken != null && _incomingHeld.Contains(_stashTaken))
            {
                // 手持ちが満杯などで移動しなかった → 倉庫側に戻ったはず。突き合わせで Legacy 扱いになったものを差し替える
                Main.Log($"[stash] move to inventory did not happen; restoring {_stashTaken}");
                var i = Stash.FindLastIndex(r => r.Legacy && r.WeaponId == _stashTaken.WeaponId);
                if (i >= 0) Stash[i] = _stashTaken;
            }
            _incomingHeld.Clear();
            _stashTaken = null;
        }

        public static void BeginSell() => Outgoing = Dest.Sold;

        public static void EndSell()
        {
            ReconcileHeld("SellWeapon");
            Outgoing = Dest.None;
        }

        public static void BeginDrop() => Outgoing = Dest.Drop;

        public static void OnDroppedSpawned(GameObject go)
        {
            if (go == null || _dropped.Count == 0) return;
            var li = go.GetComponentInChildren<LootableItem>(true);
            var roll = _dropped.Dequeue();
            if (li == null) { Main.Log("[drop] WARN dropped object has no LootableItem"); return; }
            Ground[li.GetInstanceID()] = roll;
            Main.Log($"[drop] {go.name} id={li.GetInstanceID()} keeps {roll}");
        }

        public static void EndDrop()
        {
            ReconcileHeld("DeleteWeapon");
            if (_dropped.Count > 0) Main.Log($"[drop] WARN {_dropped.Count} dropped roll(s) had no spawned object");
            _dropped.Clear();
            Outgoing = Dest.None;
        }

        // 地面の武器のロール（未抽選なら今ここで抽選）。表示・性能用はレアリティのオン/オフを通した見え方
        public static WeaponRoll GroundRoll(LootableItem li) => Features.View(GroundRollReal(li));

        static WeaponRoll GroundRollReal(LootableItem li)
        {
            if (li == null || li.ItemType != LootableItem.LootType.Weapon) return null;
            var key = li.GetInstanceID();
            if (Ground.TryGetValue(key, out var r)) return r;
            var gwi = ItemById(li.ItemId);
            r = RarityTable.Roll(gwi, BaseTier(gwi));
            Ground[key] = r;
            Main.Log($"[ground] rolled {li.gameObject.name} id={key} {NameOf(li.ItemId)} {r} floor={RarityTable.CurrentFloor()}");
            return r;
        }

        public static void BeginPickup(LootableItem li)
        {
            var r = GroundRollReal(li);
            if (r != null) _pickup = (li.GetInstanceID(), r);
        }

        public static void EndPickup() => _pickup = null;

        // ゲームが武器を手持ちに入れている最中（AddWeapon の中）の、その武器のロール。
        // ゲームは通知の前に InitializeWeapons で手持ちを作り直すので、ふつうはもう手持ちの突き合わせが済んでいる。
        // ここではロールを作らない（作ると、使われないロールが残って別の武器に付いてしまう）
        public static WeaponRoll RollForAdding(WeaponItem wi)
        {
            if (wi == null) return null;
            var h = Held.FirstOrDefault(e => e.Roll.WeaponId == wi.Id);
            if (h != null) return Features.View(h.Roll);
            if (_pickup is { } p && p.roll.WeaponId == wi.Id) return Features.View(p.roll);
            return Features.View(_incomingHeld.FirstOrDefault(x => x.WeaponId == wi.Id));
        }

        public static void OnSceneChanged() => Ground.Clear();

        // ---------- 性能の反映 ----------

        // 手に持っている武器すべてに当て直す（共通の仕組みが、ゲームの値に戻してから各 Mod の処理を順に呼ぶ）
        public static void ApplyAll() => Weapons.ApplyAll();
        public static void ApplyTo(DemoWeapon w) => Weapons.ApplyTo(w);

        // 共通の仕組みから呼ばれる：手持ちの slot 番目の武器にロールを当てる
        public static void ApplySlot(GameObject inst, GameObject prefab, int weaponId, int slot)
        {
            var roll = HeldRoll(slot, prefab?.name);
            if (roll != null) Apply(inst, prefab, roll);
        }

        // 手持ちの slot 番目のロール（並びがずれていたら同じ種類の武器のもの）
        public static WeaponRoll HeldRoll(int slot, string prefab)
        {
            if (prefab == null) return null;
            if (slot >= 0 && slot < Held.Count && Held[slot].Prefab == prefab) return Features.View(Held[slot].Roll);
            return Features.View(Held.FirstOrDefault(h => h.Prefab == prefab)?.Roll);
        }

        static void Apply(GameObject inst, GameObject prefab, WeaponRoll roll)
        {
            if (inst == null || prefab == null || roll == null) return;
            var dw = inst.GetComponent<DemoWeapon>();
            var pdw = prefab.GetComponent<DemoWeapon>();
            // 補正 = レアリティのロール × 武器熟練度
            if (dw != null && pdw != null) dw.fireRate = pdw.fireRate * Mastery.FireRateMult(roll);

            var bws = inst.GetComponentsInChildren<BallisticWeapon>(true);
            var pbws = prefab.GetComponentsInChildren<BallisticWeapon>(true);
            for (int i = 0; i < bws.Length && i < pbws.Length; i++)
                bws[i].MuzzleDamage = pbws[i].MuzzleDamage * Mastery.DamageMult(roll);

            // ホロサイト（熟練度 Lv10 で解放、ON のときだけ）
            // サイトをアタッチメントで選ぶ Mod（FPCustom）があるときは載せない（両方あると照準点が合わない）
            SightMount.Apply(inst, prefab, roll.WeaponId, Mastery.SightOn(roll.WeaponId) && !Link.CustomSights);

            if (roll.Legacy && !Mastery.HasRecoilBonus(roll))   // レアリティがオフの間も（View で Legacy になる）
            {
                // Mod導入前の武器で熟練度の反動補正もない → 元の反動データに戻す
                if (dw != null && pdw != null) { dw.recoilPatternSettings = pdw.recoilPatternSettings; dw.recoilData = pdw.recoilData; }
                return;
            }

            // コントロール → 反動パターンと射撃時の跳ね上がり（エルゴは構える速さ。FPModOption.AdsSpeed）。
            // 反動データは武器の種類ごとの共有アセットなので、ロールごとに複製してこの1本にだけ割り当てる
            if (dw != null && pdw != null)
            {
                dw.recoilPatternSettings = RecoilClones.Pattern(roll, pdw.recoilPatternSettings);
                dw.recoilData = RecoilClones.Anim(roll, pdw.recoilData);
            }

            Main.Log($"[apply] {inst.name} {roll}: fireRate {pdw?.fireRate}->{dw?.fireRate} " +
                     $"dmg {(pbws.Length > 0 ? pbws[0].MuzzleDamage : 0)}->{(bws.Length > 0 ? bws[0].MuzzleDamage : 0)} " +
                     $"recoil x{RarityTable.RecoilScale(roll):0.00} ads x{AdsSpeed.Mult(Mastery.ErgoDeltaF(roll)):0.00}");
        }

        // その武器（手に持っている実体）のロール
        public static WeaponRoll RollFor(DemoWeapon w)
        {
            var inst = Fps?.InstantiatedWeapons;
            if (w == null || inst == null) return null;
            var name = w.gameObject.name.Replace("(Clone)", "").Trim();
            for (int i = 0; i < inst.Count; i++)
                if (inst[i] != null && inst[i].Pointer == w.Pointer)
                    return Features.View(i < Held.Count && Held[i].Prefab == name ? Held[i].Roll : Held.FirstOrDefault(h => h.Prefab == name)?.Roll);
            return null;
        }

        // 熟練度の変更後、手に持っている武器の反動パターンを渡し直す（装備時と同じ処理）
        public static void ReinitActiveRecoil()
        {
            var dw = Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();
            var rp = dw?._recoilPattern;
            if (rp != null && dw.recoilPatternSettings != null) rp.Init(dw.recoilPatternSettings);
            // 撃った瞬間の跳ね上がり（エルゴ）は、プレイヤー側の RecoilAnimation に装備のときに写される。手に持ったままでも渡し直す
            var ra = dw?._recoilAnimation;
            if (ra != null && dw.recoilData != null) ra.Init(dw.recoilData, dw.fireRate, dw._fireMode);
        }

        // 今手に持っている武器のロールと武器データ
        public static WeaponRoll ActiveRoll(out WeaponItem item)
        {
            item = null;
            var fps = Fps;
            var prefabs = Inv?.WeaponsList?.weaponPrefabs;
            var active = fps?.GetActiveItem();
            if (active == null || prefabs == null) return null;
            var inst = fps.InstantiatedWeapons;
            for (int i = 0; i < inst.Count && i < Held.Count; i++)
            {
                if (inst[i] == null || inst[i].Pointer != active.Pointer) continue;
                item = ItemByPrefab(Held[i].Prefab);
                return Features.View(Held[i].Roll);
            }
            return null;
        }

        // ---------- 売値 ----------

        // 武器データに対応する手持ちのロール（同じ種類が複数あるときは先頭）
        public static WeaponRoll HeldRollFor(WeaponItem wi)
        {
            if (wi == null) return null;
            return Features.View(Held.FirstOrDefault(e => e.Roll.WeaponId == wi.Id)?.Roll);
        }

        public static float SellMultFor(WeaponItem wi)
        {
            if (wi == null) return 1f;
            var h = Held.FirstOrDefault(e => e.Roll.WeaponId == wi.Id);
            return RarityTable.SellPriceMult(Features.View(h?.Roll));
        }

        // ---------- 表示 ----------

        public static string Label(WeaponRoll r, string weaponName) =>
            $"<color=#{RarityTable.Hex(r.Tier)}>[{r.Tier}] {weaponName}</color>";

        static void Announce(WeaponRoll r)
        {
            // 画面下の通知は武器名だけ（枠の色でレアリティがわかる）。性能はログにだけ残す。レアリティがオフなら出さない（ゲームの通知だけ）
            if (!Features.Rarity) return;
            var text = NameOf(r.WeaponId);
            Main.Log($"[notify] {text}  {TooltipRewriter.Summary(r)}");
            try { Inv?.NotificationsManager?.NotifyWithColor(text, RarityTable.ColorOf(r.Tier)); }
            catch (Exception e) { Main.Log("[notify] failed: " + e.Message); }
        }

        // ---------- 保存 ----------

        public static void Restore(List<HeldEntry> held, List<WeaponRoll> stash, List<WeaponRoll> spare = null)
        {
            Held = held ?? new();
            Stash = stash ?? new();
            Spare = spare ?? new();
        }

        // ---------- オフの間に動いた武器のロール ----------
        // この Mod をオフにして遊んでいる間に手持ち・倉庫の武器が動くと、起動直後の突き合わせで記録と合わなくなる。
        // 合わなくなったロールは捨てずに取っておき、同じ種類の武器が記録の無いまま見つかったら、そのロールを戻す
        public static List<WeaponRoll> Spare = new();
        const int SpareMax = 60;

        static void KeepSpare(WeaponRoll r, string from)
        {
            if (r == null || r.Legacy) return;
            Spare.Add(r);
            if (Spare.Count > SpareMax) Spare.RemoveAt(0);
            Main.Log($"[spare] keep {NameOf(r.WeaponId)} {r} (no longer in {from})");
        }

        // 同じ種類の取っておいたロール（無ければ、まだ突き合わせていない倉庫の記録から）
        static WeaponRoll TakeSpare(int weaponId, bool fromStash)
        {
            int i = Spare.FindLastIndex(r => r.WeaponId == weaponId);
            if (i >= 0) { var r = Spare[i]; Spare.RemoveAt(i); Main.Log($"[spare] restore {NameOf(weaponId)} {r}"); return r; }
            if (fromStash && !_stashBootstrapped)
            {
                // 倉庫から手持ちに移した武器（倉庫の記録はまだゲームと突き合わせていない）
                var cur = CurrentStashEntries();
                int have = 0;
                if (cur != null) for (int k = 0; k < cur.Count; k++) if (cur[k]?.weaponId == weaponId) have++;
                int recorded = Stash.Count(x => x.WeaponId == weaponId);
                if (cur != null && recorded > have)
                {
                    int j = Stash.FindLastIndex(x => x.WeaponId == weaponId && !x.Legacy);
                    if (j >= 0) { var r = Stash[j]; Stash.RemoveAt(j); Main.Log($"[spare] restore {NameOf(weaponId)} {r} (moved out of the stash)"); return r; }
                }
            }
            return null;
        }

        public static void DumpState(string why)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"===== STATE ({why}) =====");
            var cur = CurrentHeldPrefabs();
            sb.AppendLine("game held prefabs: " + (cur == null ? "n/a" : string.Join(", ", cur)));
            var fps = Fps;
            if (fps != null)
            {
                var inst = fps.InstantiatedWeapons;
                for (int i = 0; i < inst.Count; i++)
                {
                    var go = inst[i]?.gameObject;
                    if (go == null) continue;
                    var dw = go.GetComponent<DemoWeapon>();
                    var bw = go.GetComponentInChildren<BallisticWeapon>(true);
                    // 実際に撃つときに使われる Ballistics.Weapon（hitManager.fireBallistics）が、書き換えたものと同じか
                    var fb = dw?.hitManager?.fireBallistics;
                    string fbInfo = fb == null ? "null"
                        : $"{fb.gameObject.name}#{fb.GetInstanceID()} dmg={fb.MuzzleDamage} same={(bw != null && fb.Pointer == bw.Pointer)}";
                    sb.AppendLine($"  inst[{i}] {go.name} active={go.activeInHierarchy} fireRate={dw?.fireRate} MuzzleDamage={bw?.MuzzleDamage}#{bw?.GetInstanceID()} fireBallistics={fbInfo}");
                }
            }
            for (int i = 0; i < Held.Count; i++) sb.AppendLine($"  held[{i}] {Held[i].Prefab} {NameOf(Held[i].Roll.WeaponId)} {Held[i].Roll}");
            var entries = CurrentStashEntries();
            sb.AppendLine("game stash ids: " + (entries == null ? "n/a" : string.Join(",", Enumerable.Range(0, entries.Count).Select(i => entries[i]?.weaponId))));
            for (int i = 0; i < Stash.Count; i++) sb.AppendLine($"  stash[{i}] {NameOf(Stash[i].WeaponId)} {Stash[i]}");
            foreach (var kv in Ground) sb.AppendLine($"  ground id={kv.Key} {NameOf(kv.Value.WeaponId)} {kv.Value}");
            Main.Log(sb.ToString());
        }

        // ---------- LCS（最長共通部分列）で2つの並びを対応付ける ----------

        static (int[] aToB, int[] bToA) Lcs<T>(IList<T> a, IList<T> b)
        {
            int n = a.Count, m = b.Count;
            var dp = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    dp[i, j] = Equals(a[i], b[j]) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            var aToB = Enumerable.Repeat(-1, n).ToArray();
            var bToA = Enumerable.Repeat(-1, m).ToArray();
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (Equals(a[x], b[y])) { aToB[x] = y; bToA[y] = x; x++; y++; }
                else if (dp[x + 1, y] >= dp[x, y + 1]) x++;
                else y++;
            }
            return (aToB, bToA);
        }
    }
}
