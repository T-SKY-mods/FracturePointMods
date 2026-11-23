using System;
using FPModOption;
using HarmonyLib;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using AdsState = Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.AdsLayer.AdsLayerState;
using KTransform = Il2CppKINEMATION.KAnimationCore.Runtime.Core.KTransform;
using IkState = Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.IkLayer.IkLayerState;
using ActionState = Il2CppDemo.Scripts.Runtime.Character.FPSActionState;
using AimState = Il2CppDemo.Scripts.Runtime.Character.FPSAimState;
using MoveState = Il2CppDemo.Scripts.Runtime.Character.FPSMovementState;

namespace FPCustom
{
    // ストック付きハンドガンの構え（作りかけ。完成するまで配布しない）。
    //   メイン武器のように肩に当てて持ち、手だけハンドガンの握り方にする。
    //   ・手と腕の動き（握り・リロード・弾薬チェック）はハンドガンのもの（ハンドガンのプロファイル）をそのまま使う。
    //     銃と両手の IK の目標は同じ親（IK WeaponBone）の下にあるので、銃を動かせば手も付いてくる。
    //   ・体の動き（歩く・走る）のアニメーターは見本のメイン武器のものにする（走るときは両手で銃を抱える）。
    //   ・立っているとき・エイム中の銃の位置はゲームのまま（ストックの長さで、もともと床尾が肩のあたりに来る）。
    //   ・動作（リロード・弾薬チェックなど）の間は、IK を解く直前に銃（IK WeaponBone）を平行移動して、
    //     ストックの後ろの端（床尾）を動作を始めたときの位置（肩）の近くに留める。
    //     ハンドガンの動作は銃を顔の前まで持ち上げて大きく回すので、そのままだとストックが体を突き抜ける。
    //     メイン武器のリロードと同じくらい（床尾が 10cm ほど動く）に抑える。
    public static class Stance
    {
        // テスト用の切り替え：0 = 何もしない、2 = アニメーターだけ見本の武器、3 = アニメーター＋肩に寄せる
        public static int Mode = 3;
        public static int Donor = 0;   // 見本の武器（AK-74）

        public static float Follow = 0.35f;     // 動作で床尾が動く量のうち残す割合
        public static float MaxAway = 0.10f;    // 床尾が動作を始めた位置から離れる上限（m）
        const float BlendSpeed = 6f;

        public static bool Patched;
        static float _w;
        static int _frame = -1;
        static Transform _weaponBone;
        static IntPtr _buttFor;
        static Vector3 _butt;
        public static float RotKeep = 0.45f;    // 動作で銃が傾く量のうち残す割合
        const float SettleTime = 1.0f;
        const float FireSettleTime = 0.3f;   // 撃っただけのとき（反動）は短く
        const float SettledFor = 0.15f;      // 構えが止まってからこの時間たてば、撃ったときに床尾を留める
        static float _lastMoving = -10f;
        static bool _holdForFire;
        static bool _hasButt, _inAction, _hold;
        static float _holdUntil;
        static Quaternion _refRot;
        static Vector3 _ref;   // 動作を始めたときの床尾（頭の骨から見た位置。y = 前、z = 下、x = 右）

        public static void Install(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(IkState), "OnEvaluatePose"), FPModOption.CoopGuard.Hm(typeof(Stance), nameof(Ik_Pre)));
                h.Patch(AccessTools.Method(typeof(DemoWeapon), "OnEquip"), FPModOption.CoopGuard.Hm(typeof(Stance), nameof(Equip_Pre)));
                h.Patch(AccessTools.Method(typeof(AdsState), "OnGameThreadUpdate"), FPModOption.CoopGuard.Hm(typeof(Stance), nameof(Ads_Pre)));
                h.Patch(AccessTools.Method(typeof(AdsState), "InitializeState"), null, FPModOption.CoopGuard.Hm(typeof(Stance), nameof(AdsInit_Post)));
                h.Patch(AccessTools.Method(typeof(Il2Cpp.FPSFireManager), "Fire"), FPModOption.CoopGuard.Hm(typeof(Stance), nameof(Fire_Pre)), FPModOption.CoopGuard.Hm(typeof(Stance), nameof(Fire_Post)));
                Patched = true;
            }
            catch (Exception e) { Main.Log("[stance] patch failed: " + e.Message); }
        }

        public static bool StockedId(int id) => Mode != 0 && id >= 0 && Catalog.IsPistol(id) && Store.Equipped(id, Cat.Stock) != null;

        // 武器の実体のアニメーターをストックの有無に合わせる。変えたら true
        static bool SetFields(DemoWeapon dw, int id)
        {
            var pdw = Tracker.ItemById(id)?.Weapon?.gameObject?.GetComponent<DemoWeapon>();
            if (dw == null || pdw == null) return false;
            var src = StockedId(id) ? Tracker.ItemById(Donor)?.Weapon?.gameObject?.GetComponent<DemoWeapon>() : pdw;
            if (src == null || src.overrideController == null) return false;
            bool changed = dw.overrideController?.Pointer != src.overrideController.Pointer;
            dw.overrideController = src.overrideController;
            return changed;
        }

        static void Equip_Pre(DemoWeapon __instance)
        {
            try { SetFields(__instance, Weapons.IdOf(__instance)); } catch (Exception e) { Main.Log("[stance] equip failed: " + e.Message); }
        }

        // 付け外しのたび（Effects.Apply から）。手に持っている武器なら、その場でアニメーターを付け替える
        public static void Apply(DemoWeapon dw, int id)
        {
            try
            {
                _buttFor = IntPtr.Zero;   // ストックの形が変わったかもしれない
                if (!SetFields(dw, id) || Weapons.Active()?.Pointer != dw.Pointer) return;
                var ca = dw._controllerAnimator;
                if (ca != null && dw.overrideController != null) ca.runtimeAnimatorController = dw.overrideController;
                Main.Log($"[stance] {id}: animator {dw.overrideController?.name}");
            }
            catch (Exception e) { Main.Log("[stance] apply failed: " + e.Message); }
        }

        static void Ik_Pre(IkState __instance)
        {
            try
            {
                if (Mode != 3) return;
                var fps = Refs.Fps;
                if (fps == null) return;
                if (Time.frameCount == _frame) return;   // 1 フレームに 1 回だけ
                _frame = Time.frameCount;
                var dw = Weapons.Active();
                bool stocked = dw != null && StockedId(Weapons.IdOf(dw));
                // 構えきったか（ストックの有無に関係なく。弾をサイトへ飛ばすのに使う）
                _aimAll = Mathf.MoveTowards(_aimAll, fps._aimState == AimState.Aiming && fps._actionState != ActionState.WeaponChange ? 1f : 0f, AimPullSpeed * Time.deltaTime);
                // 撃っている間も動作と同じに扱う：ハンドガンの反動は銃を手首のまわりで大きく回す（床尾が跳ね上がって画面に入る）。
                //   床尾を肩に留め、回る量を抑える（肩を支点にした反動に見える）
                // 構えが動いている途中（エイムの出し入れ・走りの構えから戻る途中）は、撃っても床尾を留めない。
                //   留めると、その途中の位置に銃が止まったまま（撃ち続ける間と、その後しばらく）になる（ダッシュ中にエイムしてすぐ撃ったとき）
                var mv = fps._movementComponent;
                float sprintAnim = 0f;
                try { if (mv?._animator != null) sprintAnim = mv._animator.GetFloat(Il2CppDemo.Scripts.Runtime.Character.FPSMovement.Sprinting); } catch { }
                if ((_aimW > 0.001f && _aimW < 0.999f) || sprintAnim > 0.02f || (mv != null && mv.MovementState == MoveState.Sprinting)) _lastMoving = Time.time;
                bool settled = Time.time - _lastMoving > SettledFor;
                bool playing = fps._actionState == ActionState.PlayingAnimation;
                bool firing = FireAnchor && settled && Time.time - _lastFire < FireHold;
                if (!playing && !settled && _hold && _holdForFire) { _hold = false; }   // 撃って留めていた途中で構えが動き出したら、すぐ放す
                bool act = stocked && (playing || firing);
                Shoulder(stocked ? dw : null);
                if (!stocked)
                {
                    _inAction = false; _hold = false; _w = 0f; _armW = 0f; _aimW = 0f;
                    _mRightHint.Restore(__instance._rightHandHint); _mLeftHint.Restore(__instance._leftHandHint); _mLeftHand.Restore(__instance._leftHandIk);
                    return;
                }
                if (_weaponBone == null) foreach (var t in fps.GetComponentsInChildren<Transform>(true)) if (t.name == "IK WeaponBone") { _weaponBone = t; break; }
                var head = _weaponBone?.parent;
                if (head == null) return;
                if (_buttFor != dw.Pointer)
                {
                    _buttFor = dw.Pointer;
                    _butt = ButtLocal(dw.transform, true);
                    _hasButt = _butt != Vector3.zero;
                    Main.Log($"[stance] butt of {dw.name}: {_butt.ToString("F3")}");
                }
                if (!_hasButt) return;
                var inBone = _weaponBone.InverseTransformPoint(dw.transform.TransformPoint(_butt));   // 銃の骨から見た床尾
                var now = head.InverseTransformPoint(_weaponBone.TransformPoint(inBone));
                var nowRot = Quaternion.Inverse(head.rotation) * _weaponBone.rotation;
                if (act && !_inAction && !_hold) { _ref = now; _refRot = nowRot; _holdForFire = !playing; }
                if (playing) _holdForFire = false;
                if (act) { _hold = true; _holdUntil = Time.time + (_holdForFire ? FireSettleTime : SettleTime); }
                _inAction = act;
                // 動作の終わりの合図の後も、アニメーションの残りで銃が動く。元の位置に戻るまで（長くても SettleTime）続ける
                if (_hold && !act && (Time.time > _holdUntil || ((now - _ref).magnitude < 0.03f && Quaternion.Angle(nowRot, _refRot) < 8f))) _hold = false;
                _w = Mathf.MoveTowards(_w, _hold ? 1f : 0f, BlendSpeed * Time.deltaTime);
                Anchor(dw, head, inBone, now, nowRot);
                // エイム中は銃を肩へ引き付ける（床尾を胸の前から肩へ）。カメラはサイトに付いて行く
                _aimW = Mathf.MoveTowards(_aimW, fps._aimState == AimState.Aiming && fps._actionState != ActionState.WeaponChange ? 1f : 0f, AimPullSpeed * Time.deltaTime);
                if (_aimW > 0.001f)
                {
                    float aw = Mathf.SmoothStep(0f, 1f, _aimW);
                    // 後ろへ引く量は銃ごと：床尾が肩の位置（頭の骨から ShoulderY）に来るまで。ストックが長い銃は引かない
                    var pull = new Vector3(AimPull.x, Mathf.Clamp(ShoulderY - (IdleRootY + _butt.z), -MaxPullBack, 0f), AimPull.z);
                    _weaponBone.position += head.TransformVector(pull) * aw;
                    // カメラの位置は構えの層が先に決めているので、銃を動かした分（左右・上下）だけカメラも動かす。前後は AimEye だけ前へ（目をサイトに近づける）
                    if (_camBone != null) _camBone.position += head.TransformVector(new Vector3(AimPull.x, AimEye, AimPull.z)) * aw;
                }
                Arms(__instance, fps, dw);
            }
            catch { }
        }

        // ---------- エイム：メイン武器と同じく、銃は肩に当てたまま、目（カメラ）をサイトへ寄せる ----------
        //   ハンドガンの構えの設定（AdsLayerSettings.cameraBlend = 0）は、銃を顔の真ん中の前へ持ってくる（床尾があごの下へ来る）。
        //   AK 系は cameraBlend = 1（銃は動かさず、カメラがサイトへ行く）。ストック付きのハンドガンを持っている間だけ 1 にする。
        //   設定はハンドガンどうしで共有なので、外れたら元の値に戻す
        public static float CameraBlend = 1f;
        public static Vector3 AimPull = new(0.025f, -0.030f, -0.040f);   // エイム中に銃を動かす量（頭の骨の座標。x = 右、y = 前、z = 下）
        public static float AimPullSpeed = 5f;
        public static float ShoulderY = -0.095f, IdleRootY = 0.272f, MaxPullBack = 0.060f;   // 肩の位置（頭の骨から前後）・構えていないときの銃の根元の位置・引く量の上限
        public static float AimEye = 0.0f;
        public static Vector3 AimElbow = new(0.10f, -0.04f, 0.00f);   // エイム中（銃を肩へ引いたとき）に右肘をさらに外・下へ
        static float _aimW;
        static Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.AdsLayer.AdsLayerSettings _ads;
        static float _adsOrig;
        public static float CameraBlendForTest(DemoWeapon w) => AdsSpeed.SettingsOf(w)?.cameraBlend ?? -1f;
        static void Shoulder(DemoWeapon dw)
        {
            var a = dw != null ? AdsSpeed.SettingsOf(dw) : null;
            if (_ads != null && (a == null || a.Pointer != _ads.Pointer)) { _ads.cameraBlend = _adsOrig; _ads = null; }
            if (a != null && _ads == null) { _ads = a; _adsOrig = a.cameraBlend; }
            if (_ads != null) _ads.cameraBlend = CameraBlend;
        }

        // カメラを寄せる骨：AK 系の設定は「Camera」（カメラが付いている骨）、ハンドガンは「CameraSocket」（カメラは付いて来ない）。
        //   構えの層（AdsLayerState）は作られたときに骨を覚えるので、ストック付きのハンドガンの間だけ「Camera」に差し替え、外れたら戻す
        static Transform _camBone;
        static readonly System.Collections.Generic.Dictionary<IntPtr, (Transform Bone, Vector3 Pose)> _adsOrigBone = new();
        static Vector3 _camPose;   // 「Camera」の骨の元の姿勢（親＝頭の骨から見た位置・向き）。構えの層はこれを基準にカメラを動かす
        public static string AimBoneForTest = "";
        static AdsState _lastAds; static bool _lastAdsStocked;
        // ---------- ハンドガンの「CameraSocket」を元の位置に保つ（2026-11-23 エイムできない不具合） ----------
        //   ハンドガンの構えの層は CameraSocket（Camera の子）を毎フレーム「元の位置＋構えの分」に書く。
        //   ストック付きの間は書く先を Camera に替えているので、構えたまま持ち替えて層の書く先を戻した瞬間に
        //   層が CameraSocket を構えの途中の位置に書いたまま捨てられると、CameraSocket がずれたまま残る（ゲームを再起動するまで）。
        //   構えの層は持つたびに作り直され、そのとき CameraSocket の位置から「構えの差分」と「元の位置」を覚えるので、
        //   以後のイーグルは構えてもサイトが画面の中心から 4cm ずれる（腰だめのように見える）。
        //   → 層が作られたとき、CameraSocket が最初に見た位置からずれていたら戻し、覚えた差分も直す
        static readonly System.Collections.Generic.Dictionary<IntPtr, Vector3> _sockRest = new();
        public static int SocketFixesForTest;
        static void AdsInit_Post(AdsState __instance)
        {
            try
            {
                var sock = __instance._aimTargetBone; var root = __instance._owner?.transform; var wb = __instance._weaponIkBone;
                if (sock == null || root == null || wb == null || sock.name == "Camera") return;
                if (!_sockRest.TryGetValue(sock.Pointer, out var rest)) { _sockRest[sock.Pointer] = sock.localPosition; return; }
                if ((sock.localPosition - rest).sqrMagnitude < 1e-8f) return;
                var was = sock.localPosition;
                sock.localPosition = rest;
                __instance._targetDefaultPose = rest;
                var add = __instance._additivePose;
                add.position = root.InverseTransformPoint(sock.position) - root.InverseTransformPoint(wb.position);
                __instance._additivePose = add;
                SocketFixesForTest++;
                Main.Log($"[stance] {sock.name} was left at {was.ToString("F3")}: put back to {rest.ToString("F3")}");
            }
            catch (Exception e) { Core.LogOnce("stanceinit", "[stance] ads init failed: " + e.Message); }
        }
        // 古い構えの層の記録が溜まらないように
        static void Trim(IntPtr keep)
        {
            if (_adsOrigBone.Count <= 8) return;
            var k = new System.Collections.Generic.List<IntPtr>(_adsOrigBone.Keys);
            foreach (var p in k) if (p != keep) _adsOrigBone.Remove(p);
        }
        // テスト用：構えの状態をまとめて文字にする
        public static string DiagForTest()
        {
            string s = $"aimW {_aimW:0.00} aimAll {_aimAll:0.00} w {_w:0.00} hold {_hold} armW {_armW:0.00}";
            try
            {
                if (_camBone != null) s += $" cam {_camBone.localPosition.ToString("F3")} pose {_camPose.ToString("F3")}";
                if (_weaponBone != null) s += $" wb {_weaponBone.localPosition.ToString("F3")} {_weaponBone.localEulerAngles.ToString("F0")}";
                var a = _lastAds;
                if (a != null) s += $" | ads {a.Pointer} stocked {_lastAdsStocked} target {a._aimTargetBone?.name} def {a._targetDefaultPose.ToString("F3")} cachedAp {a._cachedAimPoint?.name}/{a._cachedAimPoint?.parent?.name} active {a._cachedAimPoint?.gameObject.activeInHierarchy} weight {a._aimingWeight:0.00} apPlay {a._aimPointPlayback:0.00} blend {a._settings?.cameraBlend} settings {a._settings?.name}";
                var dw = Weapons.Active();
                if (dw != null && a?._cachedAimPoint != null) s += $" apOnWeapon {a._cachedAimPoint.IsChildOf(dw.transform)}";
                if (a != null) s += $" apPose {a._aimPoint.position.ToString("F3")} {a._aimPoint.rotation.eulerAngles.ToString("F0")} prev {a._prevAimPoint.position.ToString("F3")} add {a._additivePose.position.ToString("F3")}";
                if (dw != null && _weaponBone != null) s += $" gunInWb {_weaponBone.InverseTransformPoint(dw.transform.position).ToString("F3")}";
            }
            catch (Exception e) { s += " diag failed: " + e.Message; }
            return s;
        }
        static void Ads_Pre(AdsState __instance)
        {
            try
            {
                if (Mode != 3) return;
                var dw = Weapons.Active();
                bool stocked = dw != null && StockedId(Weapons.IdOf(dw));
                var key = __instance.Pointer;
                if (stocked)
                {
                    if (_camBone == null)
                    {
                        if (Refs.Fps == null || Refs.Fps._aimState == AimState.Aiming) return;   // 元の姿勢は構えていないときに覚える
                        var sock = __instance._aimTargetBone;
                        // 「Camera」は「CameraSocket」と同じ骨組みの中の骨（名前が同じ別の物を拾わないよう、近い親から探す）
                        for (var t = sock != null ? sock.parent : null; t != null && _camBone == null; t = t.parent)
                            foreach (var c in t.GetComponentsInChildren<Transform>(true)) if (c.name == "Camera") { _camBone = c; break; }
                        if (_camBone != null) _camPose = _camBone.localPosition;
                        Main.Log($"[stance] camera bone: {(_camBone != null ? _camBone.name + " under " + _camBone.parent?.name : "not found")} (was {sock?.name} under {sock?.parent?.name})");
                    }
                    if (_camBone == null) return;
                    if (!_adsOrigBone.ContainsKey(key)) { _adsOrigBone[key] = (__instance._aimTargetBone, __instance._targetDefaultPose); Trim(key); }
                    if (__instance._aimTargetBone == null || __instance._aimTargetBone.Pointer != _camBone.Pointer) { __instance._aimTargetBone = _camBone; __instance._targetDefaultPose = _camPose; }
                }
                // 構えが残っている間は戻さない（戻した書く先に、層が構えの途中の位置を書いたまま残る）
                else if (_adsOrigBone.TryGetValue(key, out var orig) && __instance._aimingWeight <= 0.001f)
                {
                    if (orig.Bone != null) { __instance._aimTargetBone = orig.Bone; __instance._targetDefaultPose = orig.Pose; }
                    _adsOrigBone.Remove(key);
                }
                AimBoneForTest = __instance._aimTargetBone?.name ?? "";
                _lastAds = __instance; _lastAdsStocked = stocked;
            }
            catch (Exception e) { Core.LogOnce("stanceads", "[stance] ads bone failed: " + e.Message); }
        }

        // ---------- エイム中の弾道：画面の中心（サイト）へまっすぐ ----------
        //   弾は発射点の前方向に飛ぶが、ハンドガンの発射点は銃ごとに 0.3〜1.8 度下を向いていて、25m 先で 15〜75cm 下に当たる。
        //   エイム中はサイトがカメラの真ん前に来る（ストック無しのハンドガンも）ので、撃つ瞬間だけ発射点をカメラの線の上へ移し、
        //   向きをカメラの前にそろえる（撃った後に戻す）。腰だめはゲームのまま
        static float _aimAll;
        public static bool AimBulletsOn = true;
        static Transform _spMoved; static Vector3 _spPos; static Quaternion _spRot;
        static void Fire_Pre(Il2Cpp.FPSFireManager __instance)
        {
            _spMoved = null;
            try
            {
                if (!AimBulletsOn || Mode != 3 || _aimAll < 0.999f) return;
                var dw = Weapons.Active(); var cam = Camera.main;
                if (dw == null || cam == null || dw.hitManager?.Pointer != __instance.Pointer || !Catalog.IsPistol(Weapons.IdOf(dw))) return;
                var sp = dw.GetComponentInChildren<Il2CppBallistics.Weapon>(true)?.PhysicalBulletSpawnPoint;
                if (sp == null) return;
                _spMoved = sp; _spPos = sp.localPosition; _spRot = sp.localRotation;
                var c = cam.transform;
                sp.position = c.position + c.forward * Mathf.Max(0.05f, Vector3.Dot(sp.position - c.position, c.forward));
                sp.rotation = Quaternion.FromToRotation(sp.forward, c.forward) * sp.rotation;
            }
            catch (Exception e) { _spMoved = null; Core.LogOnce("stancefire", "[stance] aim bullets failed: " + e.Message); }
        }
        public static Action AfterFireForTest;
        public static bool FireAnchor = true;
        const float FireHold = 0.15f;
        static float _lastFire = -10f;
        static void Fire_Post()
        {
            _lastFire = Time.time;
            try { AfterFireForTest?.Invoke(); } catch { }
            if (_spMoved == null) return;
            try { _spMoved.localPosition = _spPos; _spMoved.localRotation = _spRot; } catch { }
            _spMoved = null;
        }

        static void Anchor(DemoWeapon dw, Transform head, Vector3 inBone, Vector3 now, Quaternion nowRot)
        {
            {
                if (_w <= 0.001f) return;
                float w = Mathf.SmoothStep(0f, 1f, _w);
                var wantRot = Quaternion.Slerp(nowRot, Quaternion.Slerp(_refRot, nowRot, RotKeep), w);
                var want = Vector3.Lerp(now, _ref + Vector3.ClampMagnitude((now - _ref) * Follow, MaxAway), w);
                var rot = head.rotation * wantRot;
                _weaponBone.rotation = rot;
                _weaponBone.position = head.TransformPoint(want) - rot * inBone;
                if (_w >= 0.999f) { MaxDist = Mathf.Max(MaxDist, (want - _ref).magnitude); MaxAngle = Mathf.Max(MaxAngle, Quaternion.Angle(wantRot, _refRot)); RawAngle = Mathf.Max(RawAngle, Quaternion.Angle(nowRot, _refRot)); }
            }
        }

        // ---------- 腕がストックに当たらないようにする ----------
        //   ハンドガンの構えは、両手首がグリップのすぐ後ろ、右の前腕がその下後ろへ下りる（ストックの通り道を横切る）。
        //   ・右肘を外（右）と上へ張る（肩に当てて撃つときの形）。前腕がストックの線の右に出る
        //   ・左手がグリップより後ろへ行くとき（マガジンを取りに行く）は、ストックの左に出す。左肘も外へ
        public static Vector3 RightElbow = new(0.30f, 0.04f, 0.00f);   // 右肘のヒントをずらす量（武器の座標。x = 右、y = 上、z = 前）
        public static Vector3 LeftElbow = new(-0.30f, -0.10f, 0.05f);         // 左手が後ろへ行ったときの左肘のヒントのずらし
        public static float LeftKeepOut = 0.0f;
        public static float LeftDrop = 0.075f;                         // 左手がグリップより後ろへ行ったときに下げる量                      // 左手をストックの中心から左へ離す距離
        public static bool ArmsOn = true;
        static float _armW;

        // IK の目標は、ゲームが毎フレーム書き直すとは限らない。前のフレームに自分が動かしたままなら、先に元へ戻してから動かす
        sealed class Moved
        {
            Vector3 _written, _orig; bool _has;
            public void Restore(Transform t) { if (_has && t != null && (t.localPosition - _written).sqrMagnitude < 1e-10f) t.localPosition = _orig; _has = false; }
            public void Set(Transform t, Vector3 world) { _orig = t.localPosition; t.position = world; _written = t.localPosition; _has = true; }
        }
        static readonly Moved _mRightHint = new(), _mLeftHint = new(), _mLeftHand = new();

        static void Arms(IkState ik, Il2CppDemo.Scripts.Runtime.Character.FPSController fps, DemoWeapon dw)
        {
            _mRightHint.Restore(ik._rightHandHint); _mLeftHint.Restore(ik._leftHandHint); _mLeftHand.Restore(ik._leftHandIk);
            bool on = ArmsOn && fps._actionState != ActionState.WeaponChange;
            _armW = Mathf.MoveTowards(_armW, on ? 1f : 0f, 4f * Time.deltaTime);
            if (_armW <= 0.001f) return;
            float w = Mathf.SmoothStep(0f, 1f, _armW);
            var g = dw.transform;
            if (ik._rightHandHint != null) _mRightHint.Set(ik._rightHandHint, ik._rightHandHint.position + g.TransformVector(RightElbow) * w + g.TransformVector(AimElbow) * Mathf.SmoothStep(0f, 1f, _aimW));
            if (ik._leftHandIk != null && AssetParts.StockAt.TryGetValue(Weapons.IdOf(dw), out var fit))
            {
                var lp = g.InverseTransformPoint(ik._leftHandIk.position);
                float behind = Mathf.Clamp01((fit.HeelZ - 0.030f - lp.z) / 0.05f) * w;   // 手首がグリップの背中より 3cm 後ろから効き始め、8cm 後ろで全部
                if (behind > 0f)
                {
                    // 左手は指を上へ向けたままストックの真下を後ろへ行く（弾倉を取りに行く）。指先がストックの下の面に入らないよう、手を下げる
                    lp.y -= LeftDrop * behind;
                    if (LeftKeepOut > 0f && lp.x > -LeftKeepOut) lp.x = Mathf.Lerp(lp.x, -LeftKeepOut, behind);
                    _mLeftHand.Set(ik._leftHandIk, g.TransformPoint(lp));
                    if (ik._leftHandHint != null) _mLeftHint.Set(ik._leftHandHint, ik._leftHandHint.position + g.TransformVector(LeftElbow) * behind);
                }
            }
        }

        // 武器の根から見た、描画物のいちばん後ろの端（床尾）。stockOnly = ストックの模型だけで測る
        public static Vector3 ButtLocal(Transform root, bool stockOnly)
        {
            float minZ = float.MaxValue; var best = Vector3.zero;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                bool inStock = false;
                for (var t = r.transform; t != null && t != root; t = t.parent) if (t.name == Mount.Prefix + "stock") { inStock = true; break; }
                if (stockOnly && !inStock) continue;
                if (r.TryCast<ParticleSystemRenderer>() != null || r.TryCast<LineRenderer>() != null) continue;
                var b = r.localBounds;
                var ctr = root.InverseTransformPoint(r.transform.TransformPoint(b.center));
                for (int i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var q = root.InverseTransformPoint(r.transform.TransformPoint(c));
                    if (q.z < minZ) { minZ = q.z; best = new Vector3(ctr.x, ctr.y, q.z); }
                }
            }
            return minZ == float.MaxValue ? Vector3.zero : best;
        }

        public static float WeightForTest => _w;
        // テスト用：動作中に床尾が動き始めの位置から離れた最大の距離（m）と、銃が傾いた最大の角度（抑えた後・抑える前）
        public static float MaxDist, MaxAngle, RawAngle;
        public static bool HoldingForTest => _hold;
    }
}
