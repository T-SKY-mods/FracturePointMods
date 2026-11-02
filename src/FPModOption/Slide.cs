using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;
using FPSController = Il2CppDemo.Scripts.Runtime.Character.FPSController;
using FPSMovement = Il2CppDemo.Scripts.Runtime.Character.FPSMovement;
using FPSMovementState = Il2CppDemo.Scripts.Runtime.Character.FPSMovementState;
using GaitSettings = Il2CppDemo.Scripts.Runtime.Character.GaitSettings;

namespace FPModOption
{
    // スライディングの調整（メイン武器は腰を据えて撃ち合う武器、ハンドガンは動き回りながら対応する武器としてすみ分ける）
    //
    // ゲームのスライディングの仕組み（調査結果）
    //   ・走っている間だけ始められる（FPSMovement.OnSlide）。スライディング中の速さは「進み具合（0→1）の曲線 × slideSpeed」で、
    //     進み具合は1秒で 1 になる（FPSMovement.UpdateSliding。ビルドでは FPSMovement.Update の中に埋め込まれていて、単独では呼ばれない）。
    //     速さは 8m/s から 0 近くまで落ち、約4m 進む
    //   ・終わるとしゃがんだまま（しゃがみ歩き 1.1m/s）になり、走り直せない（＝スライディング後の硬直）
    //   ・始まるときに体のアニメーション（FPSController.OnSlideStarted の Animator.CrossFade）で画面と武器が傾く
    //   ・スライディング中の腰だめ射撃・エイムは、もともと可能（実測: 1回のスライディングで12発、エイムの状態にもなる）
    //   ・敵の弾は LivingEntity.OnHealthChanged（負の値＝ダメージ）→ PlayerHealthManager.Damage でプレイヤーに届く。
    //     手榴弾・地雷は PlayerHealthManager.Damage を直接呼ぶので、ここを通らない
    //
    // 変更点
    // どの Mod がどれを使うか（使う Mod が無ければパッチもしない＝バニラ）
    //   UseRoles（武器カスタマイズmod）
    //     全武器    : スライディング中は、敵の弾のダメージを 70% の確率で無効にする（スライディング後の硬直中は対象外）
    //     メイン武器: スライディングはそのまま（終わるとしゃがむ）。スライディング中はエイムできない（腰だめ射撃はできる）。
    //                 エイムボタンを押したままスライディングが終わると、エイムに戻る
    //   UsePistol（武器カスタマイズmod）
    //     ハンドガン: アニメーションはゲームのまま、約5m 滑る。次第に減速し、走る速さまで落ちたところで立ち上がって
    //                 止まらずにそのまま走り出せる（硬直なし）。スライディング中もエイム・エイム射撃できる。移動速度 +5%
    //                 エイムしたまま走れる（エイム → ダッシュ → スライディング → ダッシュを、エイムを解かずに続けられる）
    //                 ダッシュ中も腰だめ射撃・エイム射撃ができる。アタッチメントで変わる（Link.MobileOverride）
    public static class Slide
    {
        public const float EvadeChance = 0.70f;

        public const float PistolSlideSpeed = 1.15f;    // スライディングの速さ（倍）
        public const float PistolSlideRate = 0.76f;     // 進み具合の進む速さ（倍）。小さいほど長く滑る（約5m になるよう調整）
        public const float PistolMoveSpeed = 1.05f;     // 歩く・走る・しゃがみ歩きの速さ（倍）
        const float MinProgressBeforeStandUp = 0.15f;   // 滑り始めてすぐには立ち上がらない

        static readonly System.Random Rng = new();
        static bool _evadeOn, _pistolStandUpOn;
        public static bool RolesOn { get; private set; }    // 回避とメイン武器のエイム制限（武器カスタマイズmod）
        public static bool PistolOn { get; private set; }   // ハンドガンの動き（武器カスタマイズmod）
        public static int Evaded { get; private set; }      // 無効にしたダメージの回数（テスト用）
        public static int Received { get; private set; }    // スライディング中に受けた弾の回数（テスト用）

        // 武器カスタマイズmod：回避とメイン武器のエイム制限
        public static void UseRoles(HarmonyLib.Harmony h)
        {
            if (RolesOn) return;
            _evadeOn = SafePatcher.Patch(h, typeof(Il2CppBallistics.LivingEntity), "OnHealthChanged", null, nameof(OnHealthChanged_Pre), null, typeof(Slide));
            RolesOn = true;
            if (!_evadeOn) GameVersion.AddDegraded("スライディングの調整");
        }

        // 武器カスタマイズmod：ハンドガンの動き
        public static void UsePistol(HarmonyLib.Harmony h)
        {
            if (PistolOn) return;
            var c = typeof(Slide);
            _pistolStandUpOn = SafePatcher.Patch(h, typeof(FPSMovement), "Update", null, nameof(MovementUpdate_Pre), nameof(MovementUpdate_Post), c);
            bool aimRunOn = SafePatcher.Patch(h, typeof(FPSController), "OnAim", new[] { typeof(bool) }, nameof(OnAim_Pre), nameof(OnAim_Post), c);
            aimRunOn &= SafePatcher.Patch(h, typeof(FPSController), "OnAim", new[] { typeof(UnityEngine.InputSystem.InputValue) }, nameof(OnAim_Pre), nameof(OnAim_Post), c);
            aimRunOn &= SafePatcher.Patch(h, typeof(FPSController), "OnSprintStarted", null, nameof(OnSprintStarted_Pre), null, c);
            aimRunOn &= SafePatcher.Patch(h, typeof(FPSMovement), "UpdateAnimatorParams", null, null, nameof(UpdateAnimatorParams_Post), c);
            aimRunOn &= SafePatcher.Patch(h, typeof(FPSController), "OnFire", new[] { typeof(UnityEngine.InputSystem.InputValue) }, nameof(OnFire_Pre), null, c);
            PistolOn = true;
            if (!_pistolStandUpOn || !aimRunOn) GameVersion.AddDegraded("ハンドガンの動き");
        }

        // 使っていた Mod が止まったとき（パッチはその Mod が外す）
        public static void StopRoles() => RolesOn = false;
        public static void StopPistol() => PistolOn = false;

        // 他の Mod（FPCustom のアタッチメント）から変える
        //   EvadeBonus     : 回避率に足す値（スライディング中・ダッシュ中）
        //   ハンドガンの動きを使うかの上書きは Link.MobileOverride（true＝メイン武器でも使う、false＝ハンドガンでも使わない、null＝武器の種類どおり）
        public static Func<float> EvadeBonus;

        static float Bonus()
        {
            try { return EvadeBonus?.Invoke() ?? 0f; } catch { return 0f; }
        }

        // ハンドガンの動きを使うか（武器カスタマイズmod が無い・オフなら、どの武器も使わない）
        public static bool Mobile()
        {
            if (!PistolOn) return false;
            bool? o = null;
            try { o = Link.MobileOverride?.Invoke(); } catch { }
            return o ?? PistolActive();
        }

        static FPSMovement Movement => Refs.Fps?._movementComponent;

        public static bool IsSliding
        {
            get { var mv = Movement; return mv != null && mv.MovementState == FPSMovementState.Sliding; }
        }

        // ---------- 全武器: スライディング中は弾が当たりにくい ----------

        static bool OnHealthChanged_Pre(Il2CppBallistics.LivingEntity __instance, float amount)
        {
            try
            {
                if (!RolesOn || amount >= 0f || __instance.PlayerHealthManager == null) return true;
                bool sliding = IsSliding;
                float bonus = Bonus();
                if (!sliding && !(bonus > 0f && Movement?.MovementState == FPSMovementState.Sprinting)) return true;
                Received++;
                if (Rng.NextDouble() >= (sliding ? EvadeChance : 0f) + bonus) return true;
                Evaded++;
                return false;   // このダメージは無かったことにする
            }
            catch (Exception e)
            {
                Core.Log("[slide] evade check failed: " + e.Message);
                return true;
            }
        }

        // ---------- ハンドガン: 約5m 滑り、走る速さまで落ちたら立ち上がってそのまま走る ----------
        // 速さは移動設定の slideSpeed を上げる（Tick）。長さは、1フレームで進む進み具合を減らす（Update の後で戻す）。
        // 走る速さまで落ちたフレームで進み具合を 1 にして立ち上がると、次のフレームでゲームが「走る」状態に戻す。

        public static int StoodUp { get; private set; }   // テスト用
        static float _progressBefore;

        static void MovementUpdate_Pre(FPSMovement __instance)
        {
            _progressBefore = __instance._slideProgress;
        }

        static void MovementUpdate_Post(FPSMovement __instance)
        {
            try
            {
                var mv = __instance;
                if (mv.MovementState != FPSMovementState.Sliding || !Mobile()) return;
                float advanced = mv._slideProgress - _progressBefore;
                if (advanced <= 0f) return;   // このフレームで滑り始めた（進み具合が 0 に戻った）
                mv._slideProgress = _progressBefore + advanced * PistolSlideRate;

                var settings = mv.movementSettings;
                if (settings == null || mv._slideProgress < MinProgressBeforeStandUp) return;
                float speed = settings.slideCurve.Evaluate(mv._slideProgress) * settings.slideSpeed;
                if (speed > settings.sprinting.velocity) return;

                mv._slideProgress = 1f;
                if (mv.PoseState != Il2CppDemo.Scripts.Runtime.Character.FPSPoseState.Standing && HasHeadroom(mv))
                {
                    mv.UnCrouch();
                    StoodUp++;
                }
            }
            catch (Exception e)
            {
                Core.Log("[slide] pistol slide failed: " + e.Message);
            }
        }

        // 立ち上がれるか（立ったときの頭の位置に、自分以外のものが無いか）
        static bool HasHeadroom(FPSMovement mv)
        {
            var cc = mv.GetComponent<CharacterController>();
            float stand = mv._originalHeight > 0f ? mv._originalHeight : 1.8f;
            float r = cc != null ? cc.radius : 0.3f;
            var head = mv.transform.position + Vector3.up * (stand - r);
            foreach (var col in Physics.OverlapSphere(head, r * 0.9f, ~0, QueryTriggerInteraction.Ignore))
                if (!col.transform.IsChildOf(mv.transform.root)) return false;
            return true;
        }

        // ---------- ハンドガン: エイムしたまま走る ----------
        // ゲームでは、エイムを始めると走りが止まり（OnAim → SprintProxy.CancelSprint）、走り始めるとエイムが解ける
        // （OnSprintStarted: エイムの解除・射撃の停止・武器を下げた走りの構え）。ハンドガンでは、
        //   ・エイムを始めても走りの入力を残す。走っている最中なら、走りの構えを解いて（OnSprintEnded）銃を構える
        //   ・エイム中に走り始めても OnSprintStarted を呼ばない（エイムしたまま走る）
        //   ・走っている最中にエイムをやめても走りを続け、走りの構えに戻す（OnSprintStarted）。
        //     ゲームはエイムをやめたときも走りの入力を取り消すので、エイムの開始と同じく入力を残す
        //   ・腕の「走る」アニメーションは体の Animator の Sprinting（0→1 の値。FPSMovement.UpdateAnimatorParams が毎フレーム設定）で
        //     混ざり、銃が画面から消える。エイムして走っている間は、これを 0 に寄せて構えた腕のままにする

        static bool _sprintWasRequested, _toggleWasOn;
        static SprintProxy _proxy;
        public static int AimRunStarts { get; private set; }   // テスト用

        static SprintProxy Proxy
        {
            get
            {
                if (_proxy == null || _proxy.WasCollected) _proxy = UnityEngine.Object.FindObjectOfType<SprintProxy>();
                return _proxy;
            }
        }

        static void OnAim_Pre(FPSController __instance)
        {
            var mv = __instance._movementComponent;
            _sprintWasRequested = mv != null && mv._sprintRequested;
            _toggleWasOn = false;
            try { if (_sprintWasRequested && Mobile()) _toggleWasOn = Proxy?._toggleSprint ?? false; } catch { }
        }

        static void OnAim_Post(FPSController __instance)
        {
            try
            {
                var fps = __instance;
                var mv = fps._movementComponent;
                if (mv == null || !Mobile()) return;
                bool sprinting = mv.MovementState == FPSMovementState.Sprinting;
                if (_sprintWasRequested && !mv._sprintRequested)
                {
                    mv.SetSprintRequested(true);
                    var proxy = Proxy;
                    if (_toggleWasOn && proxy != null) proxy._toggleSprint = true;   // 切り替え式の走りも ON のまま
                }
                if (IsAiming(fps))
                {
                    if (sprinting) fps.OnSprintEnded();   // 走りの構えを解いて、銃を構えたまま走る
                }
                else if (sprinting)
                {
                    fps.OnSprintStarted();                // エイムをやめた: ふつうの走りの構えに戻す
                }
            }
            catch (Exception e)
            {
                Core.Log("[slide] aim run failed: " + e.Message);
            }
        }

        static void UpdateAnimatorParams_Post(FPSMovement __instance)
        {
            try
            {
                var fps = Refs.Fps;
                if (fps == null || __instance.MovementState != FPSMovementState.Sprinting || !(IsAiming(fps) || _hipFireRun) || !Mobile()) return;
                var anim = __instance._animator;
                if (anim == null) return;
                int hash = FPSMovement.Sprinting;
                anim.SetFloat(hash, Mathf.MoveTowards(anim.GetFloat(hash), 0f, Time.deltaTime * 8f));
            }
            catch (Exception e)
            {
                Core.Log("[slide] aim run animation failed: " + e.Message);
            }
        }

        // ---------- ハンドガン: ダッシュ中も撃てる ----------
        // ゲームは走っている間の射撃ボタンを無視する（FPSController.OnFire: 移動状態が Sprinting なら何もしない）。
        // ハンドガンでは、走っている間もゲームの射撃処理（OnFirePressed / OnFireReleased）を呼ぶ。
        // 腰だめで撃つ間は、走りの構え（銃を下げる）を解いて銃を前に向け、離したら走りの構えに戻す。

        static bool _hipFireRun;
        public static int SprintShots { get; private set; }   // テスト用（走りながら押した回数）

        static bool OnFire_Pre(FPSController __instance, UnityEngine.InputSystem.InputValue value)
        {
            try
            {
                var fps = __instance;
                var mv = fps._movementComponent;
                if (mv == null || mv.MovementState != FPSMovementState.Sprinting || value == null || !Mobile()) return true;
                if (value.isPressed)
                {
                    if (!IsAiming(fps) && !_hipFireRun) { fps.OnSprintEnded(); _hipFireRun = true; }
                    SprintShots++;
                    fps.OnFirePressed();
                }
                else
                {
                    fps.OnFireReleased();
                    if (_hipFireRun && !IsAiming(fps)) fps.OnSprintStarted();
                    _hipFireRun = false;
                }
                return false;
            }
            catch (Exception e)
            {
                Core.Log("[slide] sprint fire failed: " + e.Message);
                return true;
            }
        }

        static bool FireHeld()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var pad = UnityEngine.InputSystem.Gamepad.current;
            return (mouse != null && mouse.leftButton.isPressed) || (pad != null && pad.rightTrigger.isPressed);
        }

        static bool OnSprintStarted_Pre(FPSController __instance)
        {
            if (!Mobile() || !(IsAiming(__instance) || FireHeld())) return true;
            AimRunStarts++;
            return false;   // エイムしたまま走る
        }

        // ---------- メイン武器: スライディング中はエイムできない ----------
        // エイムの入力は FPSController.OnAim（押した・離した時だけ呼ばれる）。設定の「エイムの切り替え」が
        //   ON  : 押すたびに OnAim(true) でエイムの ON/OFF が切り替わる（OnAim(false) は何もしない）
        //   OFF : 押すと OnAim(true)、離すと OnAim(false)
        // スライディング中にエイムしていたら、ゲームのこの処理でエイムを解き、終わったらエイムに戻す
        // （切り替え OFF のときは、エイムボタンをまだ押しているときだけ）。

        static bool _aimBlocked, _wasSliding;
        public static int AimBlocked { get; private set; }   // テスト用

        static void TickMainWeaponAim()
        {
            var fps = Refs.Fps;
            if (fps == null) return;
            bool sliding = IsSliding;
            // メイン武器だけ（ハンドガンの動きが無いときのハンドガンはゲームのまま）。ストックを付けたハンドガンはメイン武器と同じ
            if (sliding && RolesOn && !Mobile() && (PistolOn || !PistolActive()) && IsAiming(fps))
            {
                fps.OnAim(!fps.toggleAim ? false : true);   // 切り替え ON なら「もう一度押した」、OFF なら「離した」
                _aimBlocked = true;
                AimBlocked++;
            }
            if (_wasSliding && !sliding && _aimBlocked)
            {
                _aimBlocked = false;
                if (!IsAiming(fps) && (fps.toggleAim || AimButtonHeld())) fps.OnAim(true);
            }
            _wasSliding = sliding;
        }

        static bool IsAiming(Il2CppDemo.Scripts.Runtime.Character.FPSController fps)
        {
            int s = (int)fps._aimState;
            return s == 2 || s == 3;   // Aiming / PointAiming
        }

        static bool AimButtonHeld()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var pad = UnityEngine.InputSystem.Gamepad.current;
            return (mouse != null && mouse.rightButton.isPressed) || (pad != null && pad.leftTrigger.isPressed);
        }

        // ---------- ハンドガン: 移動速度 +5%、スライディングの速さ ----------

        // 移動設定（ScriptableObject）ごとの元の速さ
        static readonly Dictionary<IntPtr, (float walk, float sprint, float crouch, float slide)> _baseGaits = new();

        public static void Tick()
        {
            if (!RolesOn && !PistolOn) return;
            try { TickMainWeaponAim(); }
            catch (Exception e) { Core.Log("[slide] aim block failed: " + e.Message); }
            if (!PistolOn) return;
            try
            {
                var settings = Movement?.movementSettings;
                if (settings == null) return;
                if (!_baseGaits.TryGetValue(settings.Pointer, out var b))
                    _baseGaits[settings.Pointer] = b = (settings.walking.velocity, settings.sprinting.velocity, settings.crouching.velocity, settings.slideSpeed);
                // 移動速度はハンドガンの動きのハンドガンだけ、スライディングの速さはハンドガンの動き全部
                bool mobile = Mobile();
                float m = mobile && PistolActive() ? PistolMoveSpeed : 1f;
                float slide = b.slide * (mobile ? PistolSlideSpeed : 1f);
                if (Mathf.Approximately(settings.walking.velocity, b.walk * m) && Mathf.Approximately(settings.slideSpeed, slide)) return;
                settings.walking = WithVelocity(settings.walking, b.walk * m);
                settings.sprinting = WithVelocity(settings.sprinting, b.sprint * m);
                settings.crouching = WithVelocity(settings.crouching, b.crouch * m);
                settings.slideSpeed = slide;
            }
            catch (Exception e)
            {
                Core.Log("[slide] move speed update failed: " + e.Message);
            }
        }

        static GaitSettings WithVelocity(GaitSettings g, float v) { g.velocity = v; return g; }

        // ---------- 今の武器がハンドガンか ----------

        static IntPtr _cachedItem;
        static bool _cachedPistol;

        public static bool PistolActive()
        {
            var active = Refs.Fps?.GetActiveItem();
            if (active == null) return false;
            if (active.Pointer == _cachedItem) return _cachedPistol;
            _cachedItem = active.Pointer;
            _cachedPistol = false;
            var wi = Weapons.ItemByPrefab(active.gameObject.name.Replace("(Clone)", "").Trim());
            _cachedPistol = wi != null && wi.Type != WeaponItem.SlotType.Big;
            return _cachedPistol;
        }
    }
}
