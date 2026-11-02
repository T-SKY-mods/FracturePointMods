using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario aimprobe）。構えたときのカメラ位置が照準点と一致するかを、
    // ① サイトなし ② 持ったままサイトを ON ③ 持ち替え直した後 で比べる
    public static class AimProbe
    {
        static readonly int[] Ids = { 10, 20, 21 };
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();

        static void Equip(int wid)
        {
            var name = Tracker.ItemById(wid).Weapon.gameObject.name;
            var inst = Refs.Fps.InstantiatedWeapons;
            for (int i = 0; i < inst.Count; i++)
                if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == name) { Refs.Fps.StartWeaponChange(i); return; }
        }

        static void EquipOther(int wid)
        {
            var name = Tracker.ItemById(wid).Weapon.gameObject.name;
            var inst = Refs.Fps.InstantiatedWeapons;
            for (int i = 0; i < inst.Count; i++)
                if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() != name) { Refs.Fps.StartWeaponChange(i); return; }
        }

        static void Log(int wid, string when)
        {
            var dw = Active(); var cam = Camera.main;
            if (dw == null || cam == null) return;
            var root = dw.transform;
            var g = dw.scopeGroups;
            var sb = new System.Text.StringBuilder();
            for (int gi = 0; g != null && gi < g.Count; gi++)
                for (int k = 0; g[gi]?.attachments != null && k < g[gi].attachments.Count; k++)
                {
                    var ap = g[gi].attachments[k]?.aimPoint;
                    if (ap != null) sb.Append($" g{gi}[{k}]={root.InverseTransformPoint(ap.position):F4}");
                }
            Main.Log($"[aimprobe] [{wid}] {when}: weapon={dw.gameObject.name} scopeIndex={dw._scopeIndex} cam(root)={root.InverseTransformPoint(cam.transform.position):F4}{sb}");
        }

        // 各武器のアニメーション設定（プロファイルと、付属品ごとのレイヤー設定）
        public static List<(string, Action)> LayerSteps() => new() { ("layers", () =>
        {
            var sb = new System.Text.StringBuilder("===== LAYERS =====\n");
            var ws = Tracker.Inv.WeaponData.Weapons;
            for (int i = 0; i < ws.Count; i++)
            {
                var dw = ws[i]?.Weapon; if (dw == null) continue;
                var ent = dw.GetComponent<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorEntity>();
                var prof = ent?.animatorProfile;
                sb.Append($"[{ws[i].Id}] {Tracker.NameOf(ws[i].Id)} profile={prof?.name}(");
                for (int k = 0; prof?.settings != null && k < prof.settings.Count; k++) sb.Append(prof.settings[k]?.name + " ");
                sb.Append(")");
                void Grp(string label, Il2CppSystem.Collections.Generic.List<Il2CppDemo.Scripts.Runtime.AttachmentSystem.BaseAttachment> atts, int active)
                {
                    sb.Append($" {label}[act={active}]:");
                    for (int k = 0; atts != null && k < atts.Count; k++)
                    {
                        var at = atts[k]; sb.Append($" {(at != null ? at.gameObject.name : "null")}{{");
                        for (int m = 0; at?.attachmentLayerSettings != null && m < at.attachmentLayerSettings.Count; m++) sb.Append(at.attachmentLayerSettings[m]?.name + ",");
                        sb.Append("}");
                    }
                }
                Grp("grip", dw.gripAttachments?.attachments, dw.gripAttachments?._activeIndex ?? -1);
                Grp("barrel", dw.barrelAttachments?.attachments, dw.barrelAttachments?._activeIndex ?? -1);
                var sg = dw.scopeGroups;
                for (int g = 0; sg != null && g < sg.Count; g++)
                {
                    sb.Append($" scope{g}:");
                    for (int k = 0; sg[g]?.attachments != null && k < sg[g].attachments.Count; k++)
                    {
                        var at = sg[g].attachments[k]; sb.Append($" {at?.gameObject.name}{{");
                        for (int m = 0; at?.attachmentLayerSettings != null && m < at.attachmentLayerSettings.Count; m++) sb.Append(at.attachmentLayerSettings[m]?.name + ",");
                        sb.Append("}");
                    }
                }
                sb.AppendLine();
            }
            Main.Log(sb.ToString());
        }) };

        // 前の武器の影響で照準がずれる現象の直し方を試す：MPS-5 → Type 89（サイトなし）
        public static List<(string, Action)> FixSteps()
        {
            var steps = new List<(string, Action)>();
            foreach (var wid in new[] { 27, 20 })
            {
                int w = wid;
                steps.Add(($"add {w}", () => { if (!Tracker.Held.Any(h => h.Roll.WeaponId == w)) TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(w)); }));
                steps.Add(("wait", () => { }));
                steps.Add(("equip", () => Equip(w)));
                steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
                steps.Add(("aim", () => Active()?.OnAimPressed()));
                steps.Add(("wait", () => { }));
                steps.Add(("log", () => Log(w, "A plain")));
                steps.Add(("release", () => Active()?.OnAimReleased()));
                if (w == 27) steps.Add(("stash 27", () => TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(27))));
                steps.Add(("wait", () => { }));
            }
            // ① 照準を2回切り替え（元に戻る）
            steps.Add(("cycle scope x2", () => { var d = Active(); d?.OnCycleScope(); d?.OnCycleScope(); }));
            steps.Add(("wait", () => { }));
            steps.Add(("aim", () => Active()?.OnAimPressed()));
            steps.Add(("wait", () => { }));
            steps.Add(("log", () => Log(20, "B after cycle x2")));
            steps.Add(("release", () => Active()?.OnAimReleased()));
            steps.Add(("wait", () => { }));
            // ② アニメーションのプロファイルを結び直す
            steps.Add(("relink profile", () =>
            {
                var d = Active();
                var anim = d?._fpsAnimator;
                Main.Log($"[aimprobe] animator={(anim != null)} entity={(d?._fpsAnimatorEntity != null)}");
                anim?.LinkAnimatorProfile(d.gameObject);
                d?.InitializeAttachments();
            }));
            steps.Add(("wait", () => { }));
            steps.Add(("aim", () => Active()?.OnAimPressed()));
            steps.Add(("wait", () => { }));
            steps.Add(("log", () => Log(20, "C after relink")));
            steps.Add(("release", () => Active()?.OnAimReleased()));
            return steps;
        }

        public static List<(string, Action)> Steps()
        {
            var steps = new List<(string, Action)>();
            foreach (var id in Ids)
            {
                int wid = id;
                steps.Add(($"add {wid}", () =>
                {
                    Mastery.SetKillsForTest(wid, Mastery.KillsForLevel(Mastery.SightLevel));
                    if (!Tracker.Held.Any(h => h.Roll.WeaponId == wid)) TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(wid));
                }));
                steps.Add(("wait", () => { }));
                steps.Add(("equip", () => Equip(wid)));
                steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
                steps.Add(("aim", () => Active()?.OnAimPressed()));
                steps.Add(("wait", () => { }));
                steps.Add(("log nosight", () => Log(wid, "1 no sight")));
                steps.Add(("release + sight on", () => { Active()?.OnAimReleased(); Mastery.SetSight(wid, true); }));
                steps.Add(("wait", () => { }));
                steps.Add(("aim", () => Active()?.OnAimPressed()));
                steps.Add(("wait", () => { }));
                steps.Add(("log on-while-held", () => Log(wid, "2 sight ON while held")));
                steps.Add(("release + switch away", () => { Active()?.OnAimReleased(); EquipOther(wid); }));
                steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
                steps.Add(("switch back", () => Equip(wid)));
                steps.Add(("wait", () => { })); steps.Add(("wait", () => { }));
                steps.Add(("aim", () => Active()?.OnAimPressed()));
                steps.Add(("wait", () => { }));
                steps.Add(("log re-equipped", () => Log(wid, "3 after re-equip")));
                steps.Add(("cleanup", () => { Active()?.OnAimReleased(); Mastery.SetSight(wid, false); if (wid != 20) TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(wid)); }));
                steps.Add(("wait", () => { }));
            }
            return steps;
        }
    }
}
