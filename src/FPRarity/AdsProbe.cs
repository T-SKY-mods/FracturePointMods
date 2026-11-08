using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core;
using Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.AdsLayer;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

using FPModOption;

namespace FPRarity
{
    // 調査用（autotest -Scenario adsprobe）。構えるボタンを押してから、武器がカメラの中で止まるまでの実時間を測る。読むだけ
    public static class AdsProbe
    {
        static readonly int[] Ids = { 0, 4, 17, 7, 5 };
        static DemoWeapon Active() => Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();

        static readonly List<(float t, Vector3 p, float fov)> _samples = new();
        static bool _on, _hooked;

        static void Sample()
        {
            if (!_on) return;
            var dw = Active(); var cam = Camera.main;
            if (dw == null || cam == null) return;
            var rs = dw.GetComponentsInChildren<Renderer>();
            var center = rs.Length > 0 ? rs[0].bounds.center : dw.transform.position;
            _samples.Add((Time.unscaledTime, cam.transform.InverseTransformPoint(center) + Refs.Fps.transform.InverseTransformPoint(cam.transform.position), cam.fieldOfView));
        }

        static void Start()
        {
            if (!_hooked) { MelonLoader.MelonEvents.OnLateUpdate.Subscribe(Sample); _hooked = true; }
            _samples.Clear(); _on = true;
        }

        static string Settings(DemoWeapon dw)
        {
            var prof = dw?.GetComponent<FPSAnimatorEntity>()?.animatorProfile;
            var sb = new System.Text.StringBuilder($"profile={prof?.name}");
            for (int k = 0; prof?.settings != null && k < prof.settings.Count; k++)
            {
                var a = prof.settings[k]?.TryCast<AdsLayerSettings>();
                if (a != null) sb.Append($" ads[{k}]={a.name} aim={a.aimingSpeed:0.##} point={a.aimPointSpeed:0.##} camBlend={a.cameraBlend:0.##}");
            }
            return sb.ToString();
        }

        static void Report(int wid, float pressed)
        {
            _on = false;
            var dw = Active();
            if (_samples.Count < 10) { Main.Log($"[ads] [{wid}] too few samples"); return; }
            var end = _samples[_samples.Count - 1];
            var last = _samples.Skip(_samples.Count - 30).ToList();
            end.p = new Vector3(last.Average(x => x.p.x), last.Average(x => x.p.y), last.Average(x => x.p.z));
            float d0 = (_samples[0].p - end.p).magnitude, f0 = _samples[0].fov;
            float At(float frac)
            {
                for (int i = 0; i < _samples.Count; i++)
                {
                    bool settled = true;
                    for (int k = i; k < _samples.Count && settled; k++) settled = (_samples[k].p - end.p).magnitude <= d0 * (1f - frac);
                    if (settled) return _samples[i].t - pressed;
                }
                return -1f;
            }
            float Fov(float frac)
            {
                float span = end.fov - f0;
                if (Mathf.Abs(span) < 0.01f) return -1f;
                foreach (var s in _samples) if ((s.fov - f0) / span >= frac) return s.t - pressed;
                return -1f;
            }
            Main.Log($"[ads] [{wid}] {Tracker.NameOf(wid)} ergoMult={AdsSpeed.Mult(AdsSpeed.ErgoOf(dw)):0.00} dist={d0:0.000} t10={At(.1f):0.00} t50={At(.5f):0.00} t90={At(.9f):0.00} t95={At(.95f):0.00} fov {f0:0.0}->{end.fov:0.0} fov90={Fov(.9f):0.00} frames={_samples.Count} | {Settings(dw)}");
        }

        public static List<(string, Action)> Steps()
        {
            var s = new List<(string, Action)>();
            foreach (var wid in Ids)
            {
                int w = wid; bool added = false; float pressed = 0f;
                s.Add(($"add {w}", () =>
                {
                    if (Tracker.Held.Any(h => h.Roll.WeaponId == w)) return;
                    added = true;
                    TestMode.Call(Tracker.Inv, "AddWeapon", Tracker.ItemById(w));
                }));
                s.Add(("equip", () =>
                {
                    var name = Tracker.ItemById(w).Weapon.gameObject.name;
                    var inst = Refs.Fps.InstantiatedWeapons;
                    for (int i = 0; i < inst.Count; i++)
                        if (inst[i] != null && inst[i].gameObject.name.Replace("(Clone)", "").Trim() == name) { Refs.Fps.StartWeaponChange(i); return; }
                    Main.Log($"[ads] [{w}] not in hand");
                }));
                s.Add(("settle", () => { }));
                s.Add(("settle", () => { }));
                s.Add(("aim", () => { Start(); pressed = Time.unscaledTime; Active()?.OnAimPressed(); }));
                s.Add(("hold", () => { }));
                s.Add(("release", () => { Report(w, pressed); Active()?.OnAimReleased(); }));
                s.Add(("stash", () => { if (added) TestMode.Call(Refs.Bag, "MoveWeaponToStash", Tracker.ItemById(w)); }));
            }
            return s;
        }
    }
}
