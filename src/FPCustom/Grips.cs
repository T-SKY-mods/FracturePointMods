using System;
using System.Collections.Generic;
using FPModOption;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using AttachHand = Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.AttachHandLayer.AttachHandLayerSettings;
using LayerSettings = Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorLayerSettings;
using PoseList = Il2CppSystem.Collections.Generic.List<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorLayerSettings>;
using Group = Il2CppDemo.Scripts.Runtime.AttachmentSystem.AttachmentGroup<Il2CppDemo.Scripts.Runtime.AttachmentSystem.BaseAttachment>;

namespace FPCustom
{
    // グリップ。武器にはゲームのグリップ枠（gripAttachments：EmptyGrip / GripMount / AngledGripMount_45）があり、
    // 枠ごとに左手の持ち方（attachmentLayerSettings）が決まっている。枠を切り替えて持ち方はゲームに任せ、
    // 見た目はゲームで実際に付いているグリップ（AK-12）を複製して枠の位置に置く（枠の中の使われていない模型は消す）
    public static class Grips
    {
        public const string EmptyName = "EmptyGrip", EmptyName2 = "GripEmpty", VerticalName = "GripMount", AngledName = "AngledGripMount_45";

        // 複製元：AK-12 のバーティカルグリップ（枠ごと）
        const int DonorWeapon = 2;
        const string DonorPath = "GripMount";

        static string SlotName(Att a) => a?.Id switch
        {
            null => null,
            "vertical_grip" or "stubby_grip" or "kac_grip" => VerticalName,   // スタビー・KAC グリップ：バーティカルグリップと同じ枠・位置・持ち方
            "angled_grip" => AngledName,
            "bipod" => null,   // バイポッド：いつもの持ち方（「なし」の枠）のまま
            _ => "",
        };

        // バーティカルグリップと同じ枠・位置に付くもの（見た目だけ別の模型）
        public static bool IsVertical(Att a) => a?.Id is "vertical_grip" or "stubby_grip" or "kac_grip";
        // 模型（Firearms Kit（CC0）の foregrip.001＝短いスタビー、foregrip.003＝長い KAC 型）
        static string GripModel(Att a) => a?.Id switch { "stubby_grip" => "stubby", "kac_grip" => "kac", "bipod" => BipodModel, _ => null };
        const string BipodModel = "bipod";   // Firearms Kit（CC0）のレール用バイポッド（脚を開いた形）

        static string Leaf(string n) { int i = n.LastIndexOf('/'); return i < 0 ? n : n.Substring(i + 1); }

        // 枠の番号（無ければ -1）。a = null は「なし」の枠
        public static int SlotIndex(DemoWeapon dw, Att a)
        {
            var l = dw?.gripAttachments?.attachments;
            if (l == null) return -1;
            var want = SlotName(a);
            for (int i = 0; i < l.Count; i++)
            {
                var n = l[i] == null ? null : Leaf(l[i].gameObject.name);
                if (n == null) continue;
                if (want == null ? (n == EmptyName || n == EmptyName2) : n == want) return i;
            }
            return -1;
        }

        // グリップ枠（バーティカル、無ければアングル）の前後位置（武器の根元の座標）。左手の位置の目安
        public static float? SlotZ(int id)
        {
            var dw = PrefabDw(id);
            var l = dw?.gripAttachments?.attachments;
            if (l == null) return null;
            foreach (var want in new[] { VerticalName, AngledName })
                for (int i = 0; i < l.Count; i++)
                    if (l[i] != null && Leaf(l[i].gameObject.name) == want) return dw.transform.InverseTransformPoint(l[i].transform.position).z;
            return null;
        }

        static DemoWeapon PrefabDw(int id) => Tracker.ItemById(id)?.Weapon?.gameObject?.GetComponent<DemoWeapon>();

        // その武器で付けられるか（枠があるか。見た目の素材が無いものは付けられない）
        public static bool Supported(int id, Att a)
        {
            var dw = PrefabDw(id);
            if (dw == null) return false;
            // 枠が合わない武器（Manual）：「なし」の枠を使い、持ち方を作る（AFG）／借りる（バーティカル）
            if (GripModel(a) is { } gm && !AssetParts.Has(gm)) return false;
            if (a?.Id == "bipod") return Guards.Available(id) && BipodSpot(id) != null && (SlotIndex(dw, null) >= 0 || EmptyPoseFrom.ContainsKey(id));
            if (a != null && Manual.ContainsKey(id))
                return SlotIndex(dw, null) >= 0 && Guards.Available(id)
                    && (a.Id == "angled_grip" ? AssetParts.Has(AfgModel) : IsVertical(a) && VDonor.ContainsKey(id));
            if (SlotIndex(dw, a) < 0) return false;
            if (a != null && !Guards.Available(id)) return false;   // レールのモデルが無い
            return a == null || IsVertical(a) || (a.Id == "angled_grip" && AssetParts.Has(AfgModel) && AfgSpot(id) != null);
        }

        public static void Apply(DemoWeapon dw, DemoWeapon pdw, int id)
        {
            var group = dw?.gripAttachments;
            if (group?.attachments == null || pdw?.gripAttachments == null) return;
            // 前に差し替えた枠（Mount が消した複製）をゲームの枠に戻す
            for (int i = 0; i < group.attachments.Count && i < pdw.gripAttachments.attachments.Count; i++)
                if (group.attachments[i] == null || group.attachments[i].gameObject.name.StartsWith(Mount.Prefix)) group.attachments[i] = pdw.gripAttachments.attachments[i];
            var a = Store.Equipped(id, Cat.Grip);
            bool factory = Catalog.IsFactory(a, id);
            bool afg = !factory && a?.Id == "angled_grip";
            bool manualV = !factory && IsVertical(a) && Manual.ContainsKey(id);
            var slotAtt = a?.Id == "bipod" ? null : a;   // バイポッドは持ち方も枠も「なし」と同じ
            // アングルフォアグリップは「なし」の枠（持ち方を少しずらす）。「なし」の枠が無い武器（Hunter-32）はアングルの枠
            int idx = factory ? pdw.gripAttachments._activeIndex : (afg || manualV) ? SlotIndex(dw, null) : SlotIndex(dw, slotAtt);
            if (afg && idx < 0) idx = SlotIndex(dw, a);
            // 「なし」の枠が無い武器（Hunter-32）：最初の枠のまま、左手の持ち方だけ同じ形の武器の「なし」の枠から借りる
            bool borrow = slotAtt == null && idx < 0 && EmptyPoseFrom.ContainsKey(id);
            if (idx < 0) { if (!borrow) Main.Log($"[grip] {dw.name}: no slot for {a?.Id ?? "none"}"); idx = pdw.gripAttachments._activeIndex; if (!borrow) a = null; afg = false; }
            var want = afg ? AfgPose(id) : manualV ? VgripPose(id) : borrow ? EmptyPose(id) : null;
            if (!factory && GripModel(a) is { } hm && a.Id != "bipod") want = RaisedPose(id, hm, want ?? pdw.gripAttachments.attachments[idx]?.attachmentLayerSettings);
            // 枠がほかの武器と共有の物（武器の外の AK12_Scriptable。ACE-31・Swiss・Hunter-12・MPS-5）は、持ち方を変えると他の武器に移るので、この武器用に複製して差し替える
            if (want != null && group.attachments[idx] is { } shared && !shared.transform.IsChildOf(dw.transform))
            {
                var cg = UnityEngine.Object.Instantiate(shared.gameObject, dw.transform, false);
                cg.name = Mount.Prefix + "slot_" + shared.gameObject.name;
                cg.transform.localPosition = shared.transform.localPosition;
                cg.transform.localRotation = shared.transform.localRotation;
                var ca = cg.GetComponent<Il2CppDemo.Scripts.Runtime.AttachmentSystem.BaseAttachment>();
                if (ca != null) { group.attachments[idx] = ca; Main.Log($"[grip] {dw.name}: own copy of shared slot {shared.gameObject.name}"); }
                else UnityEngine.Object.Destroy(cg);
            }
            bool poseChanged = SetPose(group, pdw.gripAttachments, idx, want);

            if (group._activeIndex != idx || poseChanged)
            {
                group._activeIndex = idx;
                // 持っている武器なら、ゲームの手順で持ち方を付け直す（持っていない武器は次に持ったときにゲームがやる）
                var active = Refs.Fps?.GetActiveItem();
                if (active != null && active.Pointer == dw.Pointer)
                {
                    try
                    {
                        var anim = dw._fpsAnimator ?? Refs.Fps?.GetComponentInChildren<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimator>(true);
                        if (anim != null) group.Initialize(anim);
                        else Main.Log("[grip] no animator yet (game will link on equip)");
                    }
                    catch (Exception e) { Main.Log("[grip] Initialize failed: " + e); }
                }
            }
            // 枠の表示：選んだ枠だけ有効、他は消す（ゲームの Initialize と同じ）
            for (int i = 0; i < group.attachments.Count; i++)
            {
                var s = group.attachments[i];
                if (s == null) continue;
                bool on = i == idx;
                if (s.gameObject.activeSelf != on) s.gameObject.SetActive(on);
            }
            if (a == null || factory) return;

            Place(group.attachments[idx].transform, dw.transform, id, a);
        }

        // 「なし」の枠が無い武器 → 持ち方を借りる武器（同じ形の銃）
        static readonly Dictionary<int, int> EmptyPoseFrom = new() { [18] = 19 };   // Hunter-32 ← Hunter-12

        // その武器（無ければ借りる武器）の「なし」の枠の持ち方
        static PoseList EmptyPose(int id)
        {
            var dw = PrefabDw(id);
            int di = SlotIndex(dw, null);
            if (di < 0 && EmptyPoseFrom.TryGetValue(id, out var donor)) { dw = PrefabDw(donor); di = SlotIndex(dw, null); }
            return di < 0 ? null : dw.gripAttachments.attachments[di].attachmentLayerSettings;
        }

        // アングルフォアグリップの左手：グリップなしの持ち方の手の位置だけをずらす（武器の根元の座標。ゲームの値はほぼそのまま根元の座標で効く）。
        // 実物の持ち方：ハンドガードを手のひらで包んで、AFG の後ろの斜面に手のひらの付け根を当てる
        // 前後は武器ごとに、手首（Left_Hand）が AFG の後ろの端の AfgGap 後ろに来るように（custprobe：AK-74 で 3.5cm 下げた形が実物に近い）
        public static Vector3 AfgHandShift = Vector3.zero;
        const float AfgGap = 0.059f;
        const float AfgLength = 0.110f;   // AFG の模型の前後の長さ
        static readonly Dictionary<int, (Vector3 shift, PoseList list)> _afgPose = new();
        // 手首が弾倉に当たる武器（ハンドガードが短く AFG を前へ出せない）：手だけ前へ（手のひらの付け根が AFG の斜面に乗る）
        static readonly Dictionary<int, float> AfgHandFwd = new() { [16] = 0.012f, [19] = 0.015f, [11] = 0.015f };   // ACE-31・Hunter-12・AKSU
        static PoseList AfgPose(int id)
        {
            var spot = AfgSpot(id);
            float rear = (spot?.y ?? 0f) - AfgLength / 2f + (Fits.GripShiftZ.TryGetValue(id, out var gz) ? gz : 0f) + (AfgShiftZ.TryGetValue(id, out var az) ? az : 0f);
            float dz = spot == null ? 0f : rear - AfgGap - Fits.EmptyHandZ(id);
            var shift = AfgHandShift + new Vector3(0f, 0f, dz + (AfgHandFwd.TryGetValue(id, out var hf) ? hf : 0f));
            if (_afgPose.TryGetValue(id, out var c) && c.shift == shift && c.list != null) return c.list;
            var src = EmptyPose(id);
            if (src == null) return null;
            var list = new PoseList();
            for (int k = 0; k < src.Count; k++)
            {
                var st = src[k];
                var ah = st?.TryCast<AttachHand>();
                if (ah == null) { list.Add(st); continue; }
                var cl = UnityEngine.Object.Instantiate(ah);
                cl.name = ah.name + "_FPC_afg";
                cl.hideFlags = HideFlags.DontUnloadUnusedAsset;
                var o = cl.handPoseOffset; o.position += shift; cl.handPoseOffset = o;
                list.Add(cl.Cast<LayerSettings>());
            }
            _afgPose[id] = (shift, list);
            return list;
        }

        // バーティカルグリップの枠が無い武器（Manual）：同じ形の持ち方の武器のバーティカルの枠から持ち方を借りて、グリップの位置の差だけ手をずらす
        static readonly Dictionary<int, int> VDonor = new() { [0] = 2, [16] = 2, [17] = 2, [19] = 2, [14] = 2, [27] = 28 };   // AK 型 ← AK-12、MP5 型 ← Viper M5A3
        static readonly Dictionary<int, (Vector3 shift, PoseList list)> _vPose = new();

        // AK-12 のグリップ（複製元）の上面・前後の中心の、枠からの差（武器の根元の座標）
        static Vector2? _gripTop;
        static Vector2? GripTop()
        {
            if (_gripTop != null) return _gripTop;
            var dp = Tracker.ItemById(DonorWeapon)?.Weapon?.gameObject;
            var src = dp?.transform.Find(DonorPath);
            if (src == null) return null;
            var b = Lab.MeshBounds(src, dp.transform);
            var sp = dp.transform.InverseTransformPoint(src.position);
            return _gripTop = new Vector2(b.max.y - sp.y, b.center.z - sp.z);
        }

        // Manual の武器で、グリップの上面が Manual.Y・中心が Manual.Z に来る枠の位置
        static Vector3? VirtualSlot(int id)
        {
            if (!Manual.TryGetValue(id, out var m) || GripTop() is not Vector2 t) return null;
            // 足したレールの下に付く武器は、レールの厚みの分だけ下（Guards.Apply がグリップを下げるのと同じ量。手もそこへ）
            return new Vector3(0f, m.Y - t.x - Guards.RailDrop(id), m.Z - t.y);
        }

        static Vector3 HandOffset(PoseList l)
        {
            for (int k = 0; l != null && k < l.Count; k++) if (l[k]?.TryCast<AttachHand>() is { } ah) return ah.handPoseOffset.position;
            return Vector3.zero;
        }

        static PoseList VgripPose(int id)
        {
            if (!VDonor.TryGetValue(id, out var d) || VirtualSlot(id) is not Vector3 vp) return null;
            var ddw = PrefabDw(d);
            int di = SlotIndex(ddw, Catalog.Get("vertical_grip"));
            if (di < 0) return null;
            var dslot = ddw.gripAttachments.attachments[di];
            var dpos = ddw.transform.InverseTransformPoint(dslot.transform.position);
            // グリップの位置の差＋「なし」の持ち方の差（同じ形の持ち方なら 0）
            var shift = vp - dpos + (HandOffset(EmptyPose(id)) - HandOffset(EmptyPose(d))) + new Vector3(0f, 0f, Fits.EmptyHandZ(d) - Fits.EmptyHandZ(id));
            if (_vPose.TryGetValue(id, out var c) && c.shift == shift && c.list != null) return c.list;
            var src = dslot.attachmentLayerSettings;
            if (src == null) return null;
            var list = new PoseList();
            for (int k = 0; k < src.Count; k++)
            {
                var st = src[k];
                var ah = st?.TryCast<AttachHand>();
                if (ah == null) { list.Add(st); continue; }
                var cl = UnityEngine.Object.Instantiate(ah);
                cl.name = ah.name + "_FPC_vgrip";
                cl.hideFlags = HideFlags.DontUnloadUnusedAsset;
                var o = cl.handPoseOffset; o.position += shift; cl.handPoseOffset = o;
                list.Add(cl.Cast<LayerSettings>());
            }
            Main.Log($"[grip] [{id}] vertical pose from [{d}] shift={shift}");
            _vPose[id] = (shift, list);
            return list;
        }

        // 枠 idx の持ち方を want に（null ならゲームのまま）。他の枠はゲームのままに戻す。選んだ枠が変わったら true
        static bool SetPose(Group group, Group pgroup, int idx, PoseList want)
        {
            bool changed = false;
            for (int i = 0; i < group.attachments.Count && i < pgroup.attachments.Count; i++)
            {
                var slot = group.attachments[i]; var ps = pgroup.attachments[i];
                if (slot == null || ps == null) continue;
                var w = i == idx && want != null ? want : ps.attachmentLayerSettings;
                if (slot.attachmentLayerSettings != null && w != null && slot.attachmentLayerSettings.Pointer == w.Pointer) continue;
                slot.attachmentLayerSettings = w;
                if (i == idx) { changed = true; Main.Log($"[grip] {slot.gameObject.name}: hand pose {(want != null ? w[0]?.name : "restored")}"); }
            }
            return changed;
        }

        // 枠の中の使われていない模型は消して、AK-12 のグリップを複製して枠の位置に置く
        // ハンドガードの下にレールが無い武器は、レール／レール付きハンドガードを付ける（Guards。サイトの後に Mount から）
        public static IEnumerable<int> NeedsRail => new[] { 0, 3, 4, 10, 11, 12, 14, 20 };   // AK-74・QBZ・AKMS・M16・AKSU・AKM・VAL・Type 89

        // アングルフォアグリップ：Magpul AFG（無料モデル）を、枠の取り付け金具の上面・前後の中心に合わせて銃と平行に置く
        const string AfgModel = "afg";
        const bool AfgFlip = true;    // 模型の前が -Z（深い方が後ろ。実物の向き）

        public static GameObject Place(Transform slot, Transform root, int id, Att a = null)
        {
            if (a?.Id == "angled_grip") return PlaceAfg(slot, root, id);
            if (a?.Id == "bipod") return PlaceBipod(slot, root, id);
            if (GripModel(a) is { } gm)
            {
                // スタビー・KAC グリップ：バーティカルグリップを置いてその上面・中心を測り、同じところへ模型を置き換える
                var v = Place(slot, root, id);
                if (v == null) return null;
                var gb = Lab.MeshBounds(v.transform, root);
                UnityEngine.Object.DestroyImmediate(v);
                return PlaceModel(root, gm, false, new Vector3(gb.center.x, gb.max.y, gb.center.z), "vgrip");
            }
            foreach (var r in slot.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            var dp = Tracker.ItemById(DonorWeapon)?.Weapon?.gameObject;
            var src = dp?.transform.Find(DonorPath);
            if (src == null) { Main.Log("[grip] donor grip not found"); return null; }
            var go = UnityEngine.Object.Instantiate(src.gameObject, slot.parent, false);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
            go.name = Mount.Prefix + "vgrip";
            go.SetActive(true);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            // 大きさは複製元の武器と同じに（武器の根元に対しての大きさをそろえる）
            var want = Div(src.lossyScale, dp.transform.lossyScale);
            if (VirtualSlot(id) is Vector3 vs)
            {
                // 枠が合わない武器：ハンドガードの下面に上面を合わせる
                go.transform.SetParent(root, false);
                go.transform.localPosition = vs;
                go.transform.localRotation = Quaternion.Inverse(dp.transform.rotation) * src.rotation;
                go.transform.localScale = want;
            }
            else
            {
                go.transform.localPosition = slot.localPosition;
                go.transform.localRotation = slot.localRotation;
                var parentRel = Div(slot.parent.lossyScale, root.lossyScale);
                go.transform.localScale = Div(want, parentRel);
            }
            SetLayer(go.transform, root.gameObject.layer);
            if (Fits.GripShiftZ.TryGetValue(id, out var dz))
                go.transform.position += root.TransformVector(new Vector3(0f, 0f, dz));
            return go;
        }

        // 枠の取り付け金具（枠そのもののメッシュ。中の RK-2 は使わない）の範囲（武器の根元の座標）
        static Bounds? MountBounds(Transform slot, Transform root)
        {
            var mf = slot?.GetComponent<MeshFilter>();
            if (mf?.sharedMesh == null) return null;
            var m = root.worldToLocalMatrix * slot.localToWorldMatrix; var lb = mf.sharedMesh.bounds;
            var mb = new Bounds(m.MultiplyPoint3x4(lb.center), Vector3.zero);
            for (int i = 0; i < 8; i++) mb.Encapsulate(m.MultiplyPoint3x4(lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return mb;
        }

        // アングルフォアグリップの位置（武器の根元の座標：上面の高さと前後の中心）。
        //   高さはバーティカルの枠の金具の上面（バーティカルグリップと同じ高さ）。バーティカルの枠が無い武器は、アングルの枠の金具の上面。
        //   ただしアングルの枠が別の武器（AK-12）の部品の中にある武器は、位置が合っていないので付けない
        static readonly HashSet<int> NoAfg = new() { 29 };   // Beater：枠の位置がハンドガードの中（レールも無い）
        // グリップ枠が AK-12 の部品の中にある武器（位置が合わない）：ハンドガードの下面を測った値（custrail）。
        //   y = AFG の上面（レールがある武器はレールの下面＋かかり、無い武器はハンドガードの下面＝足すレールの上面）、z = 中心、Slope = 下面の傾き（前へ 1m で上へ）
        public readonly struct ManualSpot { public readonly float Y, Z, Slope; public ManualSpot(float y, float z, float slope = 0f) { Y = y; Z = z; Slope = slope; } }
        public static readonly Dictionary<int, ManualSpot> Manual = new()
        {
            // z は AFG を付けたときに手首が弾倉に当たらない位置（custcheck の横・下からの画像）
            [0] = new(-0.035f + Guards.RailHook, 0.165f),   // AK-74：レール付きハンドガード（Guards.Replace。下面 -0.035・z 0.064〜0.221）。z はゲームのアングルの枠（0.162）の辺り
            [16] = new(-0.0165f, 0.170f),            // ACE-31：もともと下にレール（歯の下面 -0.0205）
            [19] = new(-0.0166f, 0.166f),            // Hunter-12：短いハンドガード z 0.105〜0.215（レールを足す）
            [14] = new(-0.0232f, 0.206f),            // VAL：サプレッサーの根元のレールの台（Guards.Blocks）の下のレールの下面＋かかり・台の中央
            [27] = new(-0.0562f, 0.1988f),           // MPS-5：Viper のレール付きハンドガードに替える（Guards.FromWeapon）。Viper の AFG と同じ位置
        };
        public static float Slope(int id) => Manual.TryGetValue(id, out var m) ? m.Slope : 0f;

        // アングルフォアグリップの前後の中心を決めて置く武器（ゲームの枠が前すぎて、ハンドガードの前の端から出る）
        static readonly Dictionary<int, float> AfgZ = new() { [11] = 0.138f };   // AKSU：レール付きハンドガード z 0.073〜0.200 の中央

        public static Vector2? AfgSpot(int id)
        {
            var s = AfgSpotRaw(id);
            return s != null && AfgZ.TryGetValue(id, out var z) ? new Vector2(s.Value.x, z) : s;
        }

        static Vector2? AfgSpotRaw(int id)
        {
            if (Manual.TryGetValue(id, out var ms)) return new Vector2(ms.Y, ms.Z);
            var dw = PrefabDw(id);
            var l = dw?.gripAttachments?.attachments;
            if (l == null) return null;
            var root = dw.transform;
            Transform v = null, an = null;
            for (int i = 0; i < l.Count; i++)
            {
                if (l[i] == null) continue;
                var n = Leaf(l[i].gameObject.name);
                if (n == VerticalName) v = l[i].transform; else if (n == AngledName) an = l[i].transform;
            }
            if (an == null) return null;
            var ab = MountBounds(an, root);
            if (ab == null) return null;
            if (v != null && MountBounds(v, root) is Bounds vb) return new Vector2(vb.max.y, ab.Value.center.z);
            if (an.parent != root || NoAfg.Contains(id)) return null;
            return new Vector2(ab.Value.max.y, ab.Value.center.z);
        }

        // アングルフォアグリップだけ前へずらす（後ろの端が弾倉・弾倉の差し込み口に入る武器。custparts の AFGFIT で確認）
        static readonly Dictionary<int, float> AfgShiftZ = new() { [28] = 0.032f };   // Viper M5A3

        static GameObject PlaceAfg(Transform slot, Transform root, int id)
        {
            var spot = AfgSpot(id);
            foreach (var r in slot.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            if (spot == null) return null;
            var model = new AssetParts.Model { Name = AfgModel, Flip = AfgFlip };
            var dir = System.IO.Path.Combine(Guards.ModelDir, AfgModel);
            GameObject go;
            try { go = ModelLoader.Spawn(dir, root, root.gameObject.layer); }
            catch (Exception e) { Main.Log("[grip] afg model failed: " + e.Message); return null; }
            go.name = Mount.Prefix + "agrip";
            var b = ModelLoader.Load(dir).Bounds;
            var rot = model.Flip ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            var c = rot * b.center;
            go.transform.localRotation = rot;
            go.transform.localPosition = new Vector3(-c.x, spot.Value.x - (c.y + b.extents.y), spot.Value.y - c.z);
            if (Fits.GripShiftZ.TryGetValue(id, out var dz)) go.transform.localPosition += new Vector3(0f, 0f, dz);
            if (AfgShiftZ.TryGetValue(id, out var adz)) go.transform.localPosition += new Vector3(0f, 0f, adz);
            Skin.Apply(go, root, Skin.Role.Grip);
            return go;
        }

        // 模型を、描画の範囲の「上面の中心」が top に来るように置く（flip：Y で 180 度回す）。名前は Mount.Prefix + name
        static GameObject PlaceModel(Transform root, string model, bool flip, Vector3 top, string name, Skin.Role role = Skin.Role.Grip)
        {
            var dir = System.IO.Path.Combine(Guards.ModelDir, model);
            GameObject go;
            try { go = ModelLoader.Spawn(dir, root, root.gameObject.layer); }
            catch (Exception e) { Main.Log($"[grip] {model} model failed: {e.Message}"); return null; }
            go.name = Mount.Prefix + name;
            var b = ModelLoader.Load(dir).Bounds;
            var rot = flip ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            var c = rot * b.center;
            go.transform.localRotation = rot;
            go.transform.localPosition = new Vector3(top.x - c.x, top.y - (c.y + b.extents.y), top.z - c.z);
            Skin.ApplyPart(go, root, role);   // ほかのアタッチメントと同じ細かさの汚れ・凹凸・つや（銃の材質を引き伸ばすとのっぺりする）
            return go;
        }

        // ---------- スタビー・KAC グリップ：ゲームのバーティカルグリップ（高さ VanillaGripH）より短いので、左手を上へずらして握る ----------
        // ずらす量＝高さの差 × RaiseRate（手の位置は武器の根元の座標で上へ）。値は custnew の手の骨（Left_Hand）の位置と横からの画像で確かめる
        public const float VanillaGripH = 0.125f;
        public static float RaiseRate = 1f;
        static readonly Dictionary<(int, string), (float raise, PoseList list)> _raised = new();
        // AR 型の持ち方（M16・R-15・Type 89・ER-15S）：ゲームのバーティカルの持ち方がもともと高い（手首がグリップの上面の 5cm 下。AK 型は 8.5cm 下）ので、
        //   上へずらすと手が銃にめり込む。ずらさない（custgriphand：スタビーの手首が AK-74 と同じ 5cm 下）
        static readonly HashSet<int> NoRaise = new() { 10, 15, 20, 21 };
        static PoseList RaisedPose(int id, string model, PoseList src)
        {
            if (src == null) return null;
            if (NoRaise.Contains(id)) return src;
            float h = ModelLoader.Load(System.IO.Path.Combine(Guards.ModelDir, model)).Bounds.size.y;
            float raise = Mathf.Max(0f, VanillaGripH - h) * RaiseRate;
            if (_raised.TryGetValue((id, model), out var c) && c.raise == raise && c.list != null) return c.list;
            var list = new PoseList();
            for (int k = 0; k < src.Count; k++)
            {
                var st = src[k];
                var ah = st?.TryCast<AttachHand>();
                if (ah == null) { list.Add(st); continue; }
                var cl = UnityEngine.Object.Instantiate(ah);
                cl.name = ah.name + "_FPC_" + model;
                cl.hideFlags = HideFlags.DontUnloadUnusedAsset;
                var o = cl.handPoseOffset;
                o.position += new Vector3(0f, raise, 0f);
                cl.handPoseOffset = o;
                list.Add(cl.Cast<LayerSettings>());
            }
            _raised[(id, model)] = (raise, list);
            return list;
        }

        // ---------- バイポッド：レールの前の方（左手の指先より前）。持ち方はいつものまま ----------
        public const float BipodHandGap = 0.025f;   // 左手の指先の前の端からのすき間
        // バイポッドの上面の高さ・前後の中心（武器の根元の座標）。上面はグリップと同じ高さ（レールの下面）
        // 武器ごとの測った値（上面の高さ, 前後の中心）。custnew の BIPODAT（PartsLab.BipodFit）を書き写す。無い武器は下の計算
        //   レール（ハンドガード）の下面の前の端。2026-11-01 測定
        //   （付け替えたハンドガードの武器の高さは Guards がレールの下面に合わせ直す。足したレールの武器は下の計算）
        static readonly Dictionary<int, Vector2> BipodAt = new()
        {
            [0] = new(-0.0174f, 0.1985f),   // AK-74：レール付きハンドガードの前の端（0.221）から AKMS・AKM と同じだけ手前
            [2] = new(-0.0174f, 0.2125f),   // AK-12
            [3] = new(-0.0117f, 0.2387f),   // QBZ 191：ハンドガードの前の下の元からあるレール（z 0.20〜0.255、歯の下 -0.0157）に直接
            [4] = new(-0.0174f, 0.2015f),   // AKMS
            [10] = new(-0.0098f, 0.3315f),   // M16
            [12] = new(-0.0174f, 0.2145f),   // AKM
            [15] = new(0.0009f, 0.2735f),   // Tactic R-15
            [16] = new(-0.0165f, 0.2087f),   // ACE-31
            [18] = new(-0.0129f, 0.2500f),   // Hunter-32：短いレール（前の端 0.200）では左手と重なるので、その前のハンドガードの下面（z 0.197〜0.277、-0.0129）に
            [21] = new(-0.0140f, 0.3067f),   // ER-15S：ハンドガードの下の短いレール（z 0.228〜0.323。歯の下 -0.0182 から 4.2mm 上＝ほかのレールと同じかかり方）
            [27] = new(-0.0545f, 0.2635f),   // MPS-5
            [28] = new(-0.0546f, 0.2635f),   // Viper M5A3
        };
        // 足したレールの武器のバイポッド用のレール：（上面の高さ, 前の端＝ハンドガードの前の端）。custnew の BIPODPROF（足したレールを隠して測った下面）＋4mm
        //   （測った一番下の画素は見た目の下面より 4mm ほど下に出る。横からの画像で 4mm のすき間が出たので合わせた。グリップの高さ（AK-74 -0.0174）とも合う）
        public const float BipodRailLen = 0.07f;
        static readonly Dictionary<int, Vector2> BipodRails = new()
        {
            [17] = new(-0.0271f, 0.378f),   // Swiss 751：下面 -0.0311、前の端 0.381
            [20] = new(-0.0056f, 0.346f),   // Type 89：ハンドガードの前の部分（0.258〜0.348）の下面 -0.0096
        };
        // 元からレールがあるので、バイポッドには足したレールを付けない武器（グリップは今まで通り足したレール）
        public static readonly HashSet<int> BipodOwnRail = new() { 3 };   // QBZ 191
        public static Vector2? BipodRail(int id) => BipodRails.TryGetValue(id, out var r) ? r : null;
        // 左手より前にレールが無い武器：バイポッドは付けない
        static readonly HashSet<int> NoBipod = new() { 11, 19 };   // AKSU・Hunter-12
        public static Vector2? RailSpot(int id) => AfgSpotRaw(id);   // グリップが付く所（高さ＝レールの下面, 前後）
        public static Vector2? BipodSpot(int id)
        {
            if (NoBipod.Contains(id) || !AssetParts.Has(BipodModel)) return null;
            if (BipodAt.TryGetValue(id, out var at)) return at;
            float depth = ModelLoader.Load(System.IO.Path.Combine(Guards.ModelDir, BipodModel)).Bounds.size.z;
            // 足したレールの武器：レール（BipodRail）の前の端から 4mm 手前にバイポッドの前の端。高さは Guards がレールの下に合わせる
            if (BipodRail(id) is { } br) return new Vector2(br.x, br.y - 0.004f - depth / 2f);
            var spot = AfgSpotRaw(id);   // グリップの枠の無い武器（Swiss 751）は null
            if (spot == null) return null;
            // 台（AS VAL）：台の前の端
            if (Guards.Blocks.TryGetValue(id, out var bk)) return new Vector2(spot.Value.x, bk.Max.z - depth / 2f - 0.003f);
            float z = Fits.HandFrontZ(id) + BipodHandGap + depth / 2f;
            // 銃口の部品・レール付きハンドガードの前の端より前には出さない
            if (Fits.Muzzles.TryGetValue(id, out var mf)) z = Mathf.Min(z, mf.Tip - mf.Back - 0.03f - depth / 2f);
            if (Guards.Replace.TryGetValue(id, out var gd)) z = Mathf.Min(z, gd.Max.z - depth / 2f - 0.005f);
            return new Vector2(spot.Value.x, z);
        }

        static GameObject PlaceBipod(Transform slot, Transform root, int id)
        {
            var spot = BipodSpot(id);
            if (spot == null) return null;
            float y = spot.Value.x - (Manual.ContainsKey(id) && !BipodAt.ContainsKey(id) && !Guards.Blocks.ContainsKey(id) ? Guards.RailDrop(id) : 0f);
            return PlaceModel(root, BipodModel, false, new Vector3(0f, y, spot.Value.y), "bipod", Skin.Role.Body);   // 金属の部品
        }

        static Vector3 Div(Vector3 a, Vector3 b) => new(a.x / b.x, a.y / b.y, a.z / b.z);

        static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
        }
    }
}
