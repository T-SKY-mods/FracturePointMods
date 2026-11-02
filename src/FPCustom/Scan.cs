using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FPModOption;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

namespace FPCustom
{
    // 調査用（autotest -Dev -Scenario attachscan）。全武器の、ゲームの仕組みとしてのアタッチメント（銃口・グリップ・サイト）と、
    // 部品らしいメッシュ（隠れているものも）、射撃音、マガジン、壊れた武器部品のアイテムを書き出す
    public static class Scan
    {
        public static void Register() => TestMode.ExternalScenarios["attachscan"] = () => new() { ("scan", Run) };

        static readonly Regex Part = new("suppress|silenc|muzzle|brake|comp|flash|barrel|grip|fore|vert|angle|hand|laser|peq|light|torch|stock|butt|mag|clip|drum|rail|picat|mount|scope|sight|holo|rmr|trij|acog|eotech|reflex|dot|optic|attach|bipod", RegexOptions.IgnoreCase);

        static void Run()
        {
            var inv = Tracker.Inv;
            if (inv == null) { Main.Log("[scan] no inventory"); return; }
            var sb = new StringBuilder("===== ATTACHMENT SCAN =====\n");
            var ws = inv.WeaponData.Weapons;
            for (int i = 0; i < ws.Count; i++)
            {
                var wi = ws[i];
                var p = wi?.Weapon?.gameObject;
                if (p == null) { sb.AppendLine($"[{i}] (no prefab)"); continue; }
                sb.AppendLine($"[{wi.Id}] {wi.GetName()} type={wi.Type} prefab='{p.name}'");
                var dw = p.GetComponent<DemoWeapon>();
                if (dw != null)
                {
                    Group(sb, "barrel", Items(dw.barrelAttachments?.attachments), p.transform);
                    Group(sb, "grip", Items(dw.gripAttachments?.attachments), p.transform);
                    var sg = dw.scopeGroups;
                    for (int g = 0; sg != null && g < sg.Count; g++)
                        Group(sb, $"scope{g}", Items(sg[g]?.attachments), p.transform);
                    var clips = dw.fireClips != null ? string.Join(",", dw.fireClips.ToArray().Where(c => c != null).Select(c => c.name)) : "";
                    var fsc = dw.fireSound;
                    var fclips = fsc?.fireClips != null ? string.Join(",", fsc.fireClips.ToArray().Where(c => c != null).Select(c => c.name)) : "";
                    sb.AppendLine($"    sound: weapon.fireClips=[{clips}] fireSound=[{fclips}] reload={dw.ReloadClip?.name} particle={dw.FireParticlePosition?.name}");
                }
                foreach (var r in p.GetComponentsInChildren<Renderer>(true))
                {
                    var path = SightLab.RelPath(r.transform, p.transform);
                    if (!Part.IsMatch(path)) continue;
                    var b = r.bounds.size;
                    var mats = string.Join(",", r.sharedMaterials.Where(m => m != null).Select(m => m.name));
                    sb.AppendLine($"    mesh {(Shown(r.transform, p.transform) && r.enabled ? "ON " : "off")} '{path}' {r.GetIl2CppType().Name} size={b:F3} mats=[{mats}]");
                }
                foreach (var m in inv.MagData.Mags) if (m != null && m.WeaponName == wi.GetName())
                        sb.AppendLine($"    mag '{m.GetName()}' size={m.MagSize} max={m.MaxAmmoCount} cal={m.Caliber} price={m.price} rarity={m.Rarity}");
            }
            sb.AppendLine("--- loot items ---");
            foreach (var l in inv.LootData.Loot) if (l != null) sb.AppendLine($"  loot id={l.Id} '{l.GetName()}' price={l.price} rarity={l.GetRarity()} weight={l.GetWeight()}");
            sb.AppendLine("--- held loot ---");
            foreach (var kv in inv.LootItems) if (kv.Key != null) sb.AppendLine($"  have id={kv.Key.Id} '{kv.Key.GetName()}' x{kv.Value}");
            Main.Log(sb.ToString());
        }

        static IEnumerable<Il2CppDemo.Scripts.Runtime.AttachmentSystem.BaseAttachment> Items<T>(Il2CppSystem.Collections.Generic.List<T> l) where T : Il2CppDemo.Scripts.Runtime.AttachmentSystem.BaseAttachment
        {
            if (l == null) return null;
            var r = new List<Il2CppDemo.Scripts.Runtime.AttachmentSystem.BaseAttachment>();
            for (int i = 0; i < l.Count; i++) r.Add(l[i]);
            return r;
        }

        // プレハブはシーンに無いので activeInHierarchy は使えない。根元までの activeSelf で、普段のプレイで見えるかを判断する
        static bool Shown(Transform t, Transform root)
        {
            for (; t != null; t = t.parent) { if (!t.gameObject.activeSelf) return false; if (t == root) break; }
            return true;
        }

        static void Group(StringBuilder sb, string name, IEnumerable<Il2CppDemo.Scripts.Runtime.AttachmentSystem.BaseAttachment> list, Transform root)
        {
            if (list == null) { sb.AppendLine($"    {name}: -"); return; }
            var items = list.ToList();
            sb.AppendLine($"    {name}: {items.Count}");
            for (int k = 0; k < items.Count; k++)
            {
                var a = items[k];
                if (a == null) { sb.AppendLine($"      [{k}] null"); continue; }
                int layers = a.attachmentLayerSettings?.Count ?? 0;
                var rs = string.Join(",", a.GetComponentsInChildren<Renderer>(true).Select(r => r.name));
                sb.AppendLine($"      [{k}] '{SightLab.RelPath(a.transform, root)}' active={a.gameObject.activeSelf} shown={Shown(a.transform, root)} layers={layers} renderers=[{rs}]");
            }
        }
    }
}
