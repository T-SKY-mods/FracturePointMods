using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FPModOption;

namespace FPRarity
{
    // 武器アップデートの準備（autotest -Scenario weaponaudit）。ゲームにあるすべての武器について、Mod がどう扱うかを一覧にする。
    //   ・レアリティごとの平均的な個体（ダメージ・連射・コントロール・エルゴ）。バニラより弱くならないか
    //   ・武器カスタマイズで付けられるもの（種類ごとの数）。位置を確かめていない新しい武器は、見た目の無いもの（マガジン）だけか
    //   ・サイト（熟練度のホロサイト・ドットサイト）が付くか
    //   一覧は UserData/FPRarity/weapon_audit.csv（autotest の後で research に移す）。新しい武器は NEW と表示する
    public static class WeaponAudit
    {
        static void Check(string what, bool ok) => Main.Log($"[test] CHECK {what}: {(ok ? "PASS" : "FAIL")}");

        public static List<(string, Action)> Steps() => new() { ("audit every weapon", Run) };

        // 散弾（1 回で何発も出る）らしい値。ダメージの計算は 1 発分なので、散弾の武器が来たら発数を掛ける必要がある
        static string Pellets(object dw, object bw)
        {
            var found = new List<string>();
            foreach (var o in new[] { dw, bw })
            {
                if (o == null) continue;
                foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var n = p.Name.ToLowerInvariant();
                    if (!(n.Contains("pellet") || n.Contains("projectile") || n.Contains("bulletsper") || n.Contains("shotcount") || n.Contains("buckshot"))) continue;
                    if (p.PropertyType != typeof(int) && p.PropertyType != typeof(float)) continue;
                    try { found.Add($"{p.Name}={p.GetValue(o)}"); } catch { }
                }
            }
            return found.Count == 0 ? "" : " PELLETS? " + string.Join(",", found);
        }

        static void Run()
        {
            var items = Weapons.All().Where(w => w != null).OrderBy(w => w.Id).ToList();
            var cust = Type.GetType("FPCustom.Catalog, FPCustom");
            var catType = Type.GetType("FPCustom.Cat, FPCustom");
            var forM = cust?.GetMethod("For", BindingFlags.Public | BindingFlags.Static);
            var checkedM = cust?.GetMethod("Checked", BindingFlags.Public | BindingFlags.Static);
            var srcProp = Type.GetType("FPCustom.Att, FPCustom")?.GetField("Source");
            // どの拾い物の一覧（SpawnableLoot）に入っているか（新しい武器がもう手に入るか）
            var inLoot = new Dictionary<int, HashSet<string>>();
            foreach (var o in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Il2Cpp.SpawnableLoot>()))
            {
                var l = o.TryCast<Il2Cpp.SpawnableLoot>();
                var drops = l?.lootDrop;
                for (int i = 0; drops != null && i < drops.Count; i++)
                {
                    var d = drops[i];
                    if (d == null || d.dropType != Il2Cpp.LootDrop.DropType.Weapon) continue;
                    if (!inLoot.TryGetValue(d.ItemIndex, out var set)) inLoot[d.ItemIndex] = set = new HashSet<string>();
                    set.Add(l.name);
                }
            }
            var sb = new StringBuilder("Id,Name,New,Slot,Auto,Mag,MuzzleDamage,FireRate,BaseTier,Tier,DamageMult,FireRateMult,ControlDelta,ErgoDelta,ShotDamage,Attachments,VisualAttachments,Sight,LootTables\n");
            int noRoll = 0, weaker = 0, badVisual = 0, newCount = 0;
            foreach (var wi in items)
            {
                bool isNew = checkedM != null && !(bool)checkedM.Invoke(null, new object[] { wi.Id });
                if (isNew) newCount++;
                var dw = wi.Weapon;
                var bw = dw?.GetComponentInChildren<Il2CppBallistics.Weapon>(true);
                float muzzle = bw?.MuzzleDamage ?? 0f;
                int mag = RarityTable.MagSize(wi);
                // 付けられるアタッチメント
                int att = 0, visual = 0;
                if (forM != null && catType != null)
                    foreach (var c in Enum.GetValues(catType))
                    {
                        var list = forM.Invoke(null, new[] { wi.Id, c }) as System.Collections.IEnumerable;
                        if (list == null) continue;
                        foreach (var a in list)
                        {
                            att++;
                            if (srcProp != null && srcProp.GetValue(a)?.ToString() != "StatsOnly") visual++;
                        }
                    }
                if (isNew && visual > 0) badVisual++;
                bool sight = false; try { sight = SightMount.Supported(wi.Id); } catch { }
                int loot = inLoot.TryGetValue(wi.Id, out var ls) ? ls.Count : 0;
                var line = new List<string>();
                foreach (Tier t in Enum.GetValues(typeof(Tier)))
                {
                    WeaponRoll r = null;
                    try { r = RarityTable.Average(wi, t); } catch (Exception e) { Main.Log($"[audit] {wi.GetName()} {t}: {e.Message}"); }
                    if (r == null)
                    {
                        if (wi.Id != RarityTable.RpgId) noRoll++;
                        sb.AppendLine($"{wi.Id},\"{wi.GetName()}\",{isNew},{wi.Type},{dw?.supportsAuto},{mag},{muzzle},{dw?.fireRate},{Weapons.BaseTier(wi)},{t},,,,,,{att},{visual},{sight},{loot}");
                        continue;
                    }
                    float shot = muzzle * r.DamageMult;
                    // 新しい武器だけ確かめる（今の武器は調整済み。Swiss 751 はフルオート 220 ダメージが強すぎるので、レジェンダリーでもバニラの 0.92 倍）
                    if (t == Tier.Legendary && r.DamageMult < 1f) { if (isNew) weaker++; else Main.Log($"[audit] note: {wi.GetName()} Legendary x{r.DamageMult:0.00} (balance, unchanged)"); }
                    line.Add($"{t.ToString()[0]} x{r.DamageMult:0.00}");
                    sb.AppendLine($"{wi.Id},\"{wi.GetName()}\",{isNew},{wi.Type},{dw?.supportsAuto},{mag},{muzzle},{dw?.fireRate},{Weapons.BaseTier(wi)},{t},{r.DamageMult:0.00},{r.FireRateMult:0.00},{r.ControlDelta},{r.ErgoDelta},{shot:0},{att},{visual},{sight},{loot}");
                }
                Main.Log($"[audit] {wi.Id} {wi.GetName()}{(isNew ? " NEW" : "")} {wi.Type} auto={dw?.supportsAuto} mag={mag} dmg={muzzle} rpm={dw?.fireRate} | {string.Join(" ", line)} | attachments {att} (visual {visual}) sight={sight} lootTables={loot}{Pellets(dw, bw)}");
            }
            try { File.WriteAllText(Path.Combine(Main.Dir, "weapon_audit.csv"), sb.ToString(), new UTF8Encoding(true)); } catch (Exception e) { Main.Log("[audit] csv failed " + e.Message); }
            Main.Log($"[audit] {items.Count} weapons, {newCount} not yet checked for attachment positions");
            Check($"every weapon (except RPG) gets a rarity roll for every tier (missing {noRoll})", noRoll == 0);
            Check($"no new weapon is weaker than vanilla at Legendary (weaker {weaker})", weaker == 0);
            Check($"weapons not yet checked get no visual attachments (wrong {badVisual})", badVisual == 0);
        }
    }
}
