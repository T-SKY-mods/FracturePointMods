using System;
using System.Collections.Generic;
using System.Linq;
using FPModOption;
using HarmonyLib;
using Il2CppKINEMATION.FPSAnimationFramework.Runtime.Recoil;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;
using Stat = FPModOption.WeaponStat;

namespace FPCustom
{
    // アタッチメントを付けたときの効果（どれにもプラスとマイナスがある）。
    //   数値の効果（コントロール・エルゴ）は Att.Effects、それ以外は Att.Notes（画面の表示用）。実際の効果はここで武器・動きに当てる
    //
    // ゲームの仕組み（調査結果、build 25787540）
    //   ・発砲炎：Weapon.OnFire が毎発 fireParticlesEffect を SetActive(false→true) する
    //   ・発砲音で敵に気付かれる範囲：Weapon.OnFire → AlertManagement.RootAlertNearby(半径 = customAlertDistance、0 なら 21m)
    //   ・被弾時の手ぶれ：PlayerHealthManager.Update が手ぶれの層（_tremorSettings）の強さを (50 - 体力) / 35 にする（体力 50 未満で揺れ始める）
    //   ・連射：OnFire の最後で、_fireMode が Auto なら fireRate の間隔で OnFire を呼び直す
    //   ・反動：RecoilPattern は毎発、設定（RecoilPatternSettings）の横・縦の範囲を読む（OnFireStart は OnFire に埋め込まれている）
    //   ・弾込め：保管庫（MagUI）とラン中のボタン（AmmoButtonLoader）のコルーチンが「弾数 × 1発の時間」を total に入れて進める
    public static class Effects
    {
        public const float SuppressedAlert = 0.5f;   // サプレッサー：敵が気付く銃声の範囲の倍率（半分）
        public const float DefaultAlert = 21f;       // customAlertDistance が 0 の武器の範囲（ゲームの定数）
        public static readonly float VgripPerShot = StatScale.FromOld(2f);   // バーティカルグリップ：連射 1 発ごとのコントロール（反動 -2% ずつ）
        public const float StockRpm = 500f;          // M93R ストック：フルオートの連射速度（G-18C 以外）
        public const float G18Rate = 0.5f;           // M93R ストック：G-18C（もともとフルオート）は連射速度が半分になり、コントロール +50
        public static readonly float G18Control = Old(50f);
        const int G18C = 13;
        const int StockRecoil = 9;                   // M93R ストックのハンドガンの反動の見本：SP-320
        public const float MagMult = 1.5f;           // 拡張マガジン：容量
        public const float LoadSpeed = 0.85f;        // 拡張マガジン・ファストマグ：弾込め速度
        public const float FastReload = 1.2f;        // ファストマグ：リロード速度
        public const float ChestMult = 2f;           // スイスのスコープ：胸の倍率を 2 倍に（敵の胸は 1.3〜1.8 倍。頭は 55 倍が大半）
        public const int CompShots = 5;              // コンペンセイター：引き金を引いてから何発目までコントロールが上がるか
        public static readonly float CompBonus = Old(20f);   // コンペンセイター：その間のコントロール（7）
        public const float BipodControl = 15f;       // バイポッド：しゃがんでいる間のコントロール
        public const float BoosterRate = 1.15f;      // フラッシュコーン：連射速度
        public const float LinCompDamage = 15f;      // リニアコンペンセイター：ダメージ +15（固定値）
        public const float LegMult = 0.5f;   // アローヘッドブレーキ：敵の腕（肩〜手。ゲームは 0.1〜0.4 倍）は胴体と同じ 1 倍に（ArmFull）、脚（1 倍）は 0.5 倍に
        // 撃っている間・動きに合わせて反動を毎フレーム変えるもの（武器ごとの専用の複製を使う）
        static readonly string[] LiveAtts = { "vertical_grip", "p_compensator", "muzzle_brake", "bipod" };

        // 以前の単位（+1 = 反動 -1%）で決めた値を、表示の単位（StatScale）の整数に直す
        static float Old(float oldPoints) => MathF.Round(StatScale.FromOld(oldPoints));

        // ---------- 効果の一覧 ----------

        public static void Define()
        {
            void D(string id, (Stat, float)[] stats, params (string Ja, string En, bool Plus)[] notes)
            {
                var a = Catalog.Get(id);
                if (a == null) return;
                a.Effects.Clear(); a.Effects.AddRange(stats);
                a.Notes.Clear(); a.Notes.AddRange(notes);
            }
            var none = Array.Empty<(Stat, float)>();
            foreach (var id in new[] { "suppressor", "p_suppressor" })
                D(id, new[] { (Stat.Ergo, -3f) }, ("発砲炎なし", "No muzzle flash", true), ("敵が気付く銃声音範囲半減", "Enemy hearing range halved", true));
            foreach (var id in new[] { "muzzle_brake", "p_compensator" })
                D(id, new[] { (Stat.Control, Old(-10f)) }, ($"最初の{CompShots}発はコントロール+{CompBonus:0}", $"Control +{CompBonus:0} for the first {CompShots} shots", true));
            D("vertical_grip", new[] { (Stat.Control, Old(-8f)) }, ("連射中1発ごとに反動-2%", "Recoil -2% per shot while firing", true));
            D("angled_grip", new[] { (Stat.Ergo, -5f) }, ("スライディング後の硬直なし", "No stiffness after sliding", true), ("ダッシュ中とスライディング中にエイム射撃", "Aim and fire while sprinting or sliding", true));
            D("p_stock", none, ("フルオート500RPM", "Full auto 500 RPM", true), ("移動速度-5%", "Move speed -5%", false), ("スライディング後に硬直", "Stiffness after sliding", false), ("ダッシュ中とスライディング中のエイム射撃不可", "No aiming while sprinting or sliding", false));
            foreach (var id in new[] { "ext_mag", "p_ext_mag" })
                D(id, new[] { (Stat.Control, Old(5f)) }, ("マガジン容量1.5倍", "Magazine capacity x1.5", true), ("弾込め速度-15%", "Loading speed -15%", false));
            foreach (var id in new[] { "fast_mag", "p_fast_mag" })
                D(id, new[] { (Stat.Ergo, 8f) }, ("リロード速度+20%", "Reload speed +20%", true), ("弾込め速度-15%", "Loading speed -15%", false));
            D("swiss_scope", new[] { (Stat.Ergo, -20f) }, ("胸の倍率アップ", "Chest multiplier up", true));
            D("bipod", new[] { (Stat.Control, -5f) }, ($"しゃがみ中コントロール+{BipodControl:0}", $"Control +{BipodControl:0} while crouching", true));
            D("kac_grip", new[] { (Stat.Ergo, -5f) }, ("リーン中手ブレ無効", "No sway while leaning", true));
            D("stubby_grip", new[] { (Stat.Ergo, -5f) }, ($"移動中エルゴノミクス+{StubbyMoveErgo:0}", $"Ergonomics +{StubbyMoveErgo:0} while moving", true));
            foreach (var id in new[] { "arrowhead", "p_arrowhead" })
                D(id, none, ("腕へのダメージ1倍", "Arm damage x1", true), ($"脚へのダメージ{LegMult:0.#}倍", $"Leg damage x{LegMult:0.#}", false));
            foreach (var id in new[] { "flash_hider", "p_flash_hider" })
                D(id, new[] { (Stat.Ergo, 3f), (Stat.Control, -2f) }, ("発砲炎なし", "No muzzle flash", true));
            D("flash_cone", new[] { (Stat.Control, -7f) }, ($"連射速度+{(BoosterRate - 1f) * 100f:0}%", $"Fire rate +{(BoosterRate - 1f) * 100f:0}%", true));
            D("p_linear_comp", new[] { (Stat.Damage, LinCompDamage), (Stat.Control, -10f) });
            foreach (var id in new[] { "laser", "p_laser" }) D(id, new[] { (Stat.Control, 2f), (Stat.Ergo, -4f) });
            foreach (var id in new[] { "laser_blue", "p_laser_blue" }) D(id, new[] { (Stat.Ergo, 4f), (Stat.Control, -2f) });
            foreach (var id in new[] { "laser_green", "p_laser_green" }) D(id, new[] { (Stat.Control, 1f), (Stat.Ergo, 2f) });
        }

        // 武器ごとの違い：G-18C の M93R ストックはフルオート 500RPM の代わりに、連射速度 -50%・コントロール +50（マイナスの効果は同じ）
        static bool G18Stock(Att a, int weaponId) => a?.Id == "p_stock" && weaponId == G18C;

        public static IEnumerable<(Stat Stat, float Value)> Stats(Att a, int weaponId)
        {
            if (a == null) return Enumerable.Empty<(Stat, float)>();
            return G18Stock(a, weaponId) ? a.Effects.Append((Stat.Control, G18Control)) : a.Effects;
        }

        public static IEnumerable<(string Ja, string En, bool Plus)> Notes(Att a, int weaponId)
        {
            if (a == null) yield break;
            foreach (var n in a.Notes)
                yield return G18Stock(a, weaponId) && n.En.StartsWith("Full auto") ? ("連射速度-50%", "Fire rate -50%", true) : n;
        }

        // ---------- 付けているか ----------

        static bool Has(int id, params string[] atts)
        {
            if (id < 0) return false;
            foreach (var s in atts)
            {
                var a = Catalog.Get(s);
                if (a != null && Store.Equipped(id, a.Cat)?.Id == s) return true;
            }
            return false;
        }

        // 説明の表示（1 フレームに何度も聞かれる）用に、付け外しがあるまで覚えておく
        static readonly Dictionary<int, (float Control, float Ergo)> _deltas = new();
        static int _deltasVersion = -1;
        static (float Control, float Ergo) Deltas(int id)
        {
            if (_deltasVersion != Store.Version) { _deltas.Clear(); _deltasVersion = Store.Version; }
            if (!_deltas.TryGetValue(id, out var d)) _deltas[id] = d = (Points(id, Stat.Control), Points(id, Stat.Ergo));
            return d;
        }

        static float Points(int id, Stat s)
        {
            float v = 0f;
            foreach (var c in Catalog.CatsFor(id))
                if (Store.Equipped(id, c) is { } a) v += Stats(a, id).Where(e => e.Stat == s).Sum(e => e.Value);
            return v;
        }

        // 手に持っている武器（1 フレームに何度も聞かれるので、フレームごとに覚えておく）
        static int _frame = -1, _activeId = -1;
        static DemoWeapon _active;
        static IntPtr _mobileItem;
        static int _mobileVersion = -1, _mobileFrame = -1000;
        static bool? _mobile;

        static DemoWeapon Active(out int id)
        {
            if (_frame != Time.frameCount)
            {
                _frame = Time.frameCount;
                _active = null; _activeId = -1;
                try
                {
                    _active = Refs.Fps?.GetActiveItem()?.gameObject?.GetComponent<DemoWeapon>();
                    _activeId = Weapons.IdOf(_active);
                }
                catch { }
            }
            id = _activeId;
            return _active;
        }

        // ---------- 取り付け ----------

        public static void Install(HarmonyLib.Harmony h)
        {
            Define();
            // 武器のステータス画面・比較のポップアップ（FPRarity）にアタッチメントの値を足す
            Link.ExtraDelta = Deltas;
            AdsSpeed.ExtraErgo = id => Deltas(id).Ergo + (Has(id, "stubby_grip") && Moving() ? StubbyMoveErgo : 0f);   // スタビーグリップ：歩く・走る・スライディング中は構えが速い
            Link.RpmShown = (id, rpm) => (Has(id, "p_stock") && Catalog.IsPistol(id) ? (id == G18C ? rpm * G18Rate : StockRpm) : rpm) * (Has(id, "flash_cone") ? BoosterRate : 1f);
            Link.DamageShown = (id, dmg) => dmg + DamageAdd(id);
            Link.MobileOverride = () =>
            {
                // 毎フレーム聞かれるので、持ち替え・付け外しがあったとき（念のため 30 フレームごとにも）だけ調べ直す
                var item = Refs.Fps?.GetActiveItem();
                var ptr = item != null ? item.Pointer : IntPtr.Zero;
                if (ptr == _mobileItem && _mobileVersion == Store.Version && Time.frameCount - _mobileFrame < 30) return _mobile;
                _mobileItem = ptr; _mobileVersion = Store.Version; _mobileFrame = Time.frameCount;
                Active(out int id);
                if (Has(id, "p_stock")) _mobile = false;           // ハンドガンの動きを取り除く
                else if (Has(id, "angled_grip")) _mobile = true;   // メイン武器でもハンドガンの動き（移動速度は変わらない）
                else _mobile = null;
                return _mobile;
            };
            ReloadSpeed.ExtraMult = w => Has(Weapons.IdOf(w), "fast_mag", "p_fast_mag") ? FastReload : 1f;

            Patch(h, typeof(DemoWeapon), "OnEquip", null, nameof(OnEquip_Post));
            HitPatched = Patch(h, typeof(Il2CppBallistics.BulletHandler), "BulletHit", nameof(BulletHit_Pre), nameof(BulletHit_Post));
            TremorPatched = Patch(h, typeof(Il2Cpp.PlayerHealthManager), "Update", null, nameof(HealthUpdate_Post));
            // 弾込めが遅いのはラン中のボタンだけ（保管庫で弾を入れるのはそのまま）
            if (Patch(h, typeof(Il2Cpp.AmmoButtonLoader._LoadMagsSequentially_d__23), "MoveNext", null, nameof(ButtonLoad_Post))) LoadPatchesForTest++;
            // 拡張マガジン：セーブには元のマガジンの大きさで書く（Mod を外すと元の大きさ、余った弾は消える）
            SavePatched = Patch(h, typeof(Il2Cpp.SavesManager), "EncryptSaveJson", nameof(Encrypt_Pre), null)
                        & Patch(h, typeof(Il2Cpp.SavesManager), "Load", null, nameof(SaveLoad_Post))
                        & Patch(h, typeof(Il2Cpp.SaveBag), "MoveWeaponToStash", nameof(ToStash_Pre), null);
            _restorePending = true;   // ゲームの起動時の読み込みが Mod より先なら、最初のマガジンの同期で戻す
        }

        public static int LoadPatchesForTest;
        public static bool HitPatched, SavePatched, TremorPatched;

        static bool Patch(HarmonyLib.Harmony h, Type t, string name, string pre, string post)
        {
            try
            {
                var m = AccessTools.Method(t, name);
                if (m == null) { Main.Log($"[effect] {t.Name}.{name} not found"); return false; }
                h.Patch(m, pre == null ? null : new HarmonyMethod(typeof(Effects), pre), post == null ? null : new HarmonyMethod(typeof(Effects), post));
                return true;
            }
            catch (Exception e) { Main.Log($"[effect] patch {t.Name}.{name} failed: {e.Message}"); return false; }
        }

        // ---------- 武器に当てる（ゲームの値に戻し、レアリティmod が性能を当てた後。持ち替え・付け外しのたび） ----------

        public static void Apply(GameObject inst, GameObject prefab, int id)
        {
            var dw = inst.GetComponent<DemoWeapon>();
            var pdw = prefab.GetComponent<DemoWeapon>();
            if (dw == null || pdw == null) return;
            string key = Link.Key(dw, id);   // 反動の複製を分ける単位（レアリティのロールごと。無ければ武器の種類ごと）

            // M93R ストック：フルオート 500RPM（もともとフルオートの G-18C は連射速度が半分）。外したら元の撃ち方に戻す
            bool stock = Has(id, "p_stock");
            dw.supportsAuto = pdw.supportsAuto || stock;
            if (stock) dw.fireRate = id == G18C ? dw.fireRate * G18Rate : StockRpm;
            if (stock) dw._fireMode = FireMode.Auto;
            if (Has(id, "flash_cone")) dw.fireRate *= BoosterRate;
            // ダメージ（リニアコンペンセイター）：レアリティmod が当てた値に掛ける
            if (DamageAdd(id) != 0f && inst.GetComponentInChildren<Il2CppBallistics.Weapon>(true) is { } bw) bw.MuzzleDamage += DamageAdd(id);
            else if (!pdw.supportsAuto && dw._fireMode == FireMode.Auto) dw._fireMode = FireMode.Semi;

            // コントロール（熟練度・レアリティの値に足す。単位は同じ。レアリティmod が無ければ 0）
            var rd = Link.Recoil(dw);
            float ctlBase = rd?.Control ?? 0f;
            float ctl = Points(id, Stat.Control);
            // M93R ストックでフルオートにしたハンドガン：反動はその銃のものを元に、SP-320（見本）に寄せる（StockRecoilOf）
            var basePat = pdw.recoilPatternSettings; var baseAnim = pdw.recoilData;
            bool swap = false;
            if (stock && !pdw.supportsAuto && Tracker.ItemById(StockRecoil)?.Weapon?.gameObject?.GetComponent<DemoWeapon>() is { } sp && sp != pdw)
            {
                (basePat, baseAnim) = StockRecoilOf(id, pdw, sp);
                swap = true;
            }
            // 銃の跳ね上がりもコントロールで（エルゴは構える速さ。FPModOption.AdsSpeed が手に持っている武器に当てる）
            if (Has(id, LiveAtts) && Live(key, id, basePat, baseAnim, ctlBase + ctl) is { } live)
            {
                dw.recoilPatternSettings = live.Clone;
                if (live.AnimClone != null) dw.recoilData = live.AnimClone;
            }
            else
            {
                if (ctl != 0f || swap) dw.recoilPatternSettings = Pattern(key, basePat, Scale(ctlBase + ctl));
                if (ctl != 0f || swap) dw.recoilData = Anim(key, baseAnim, Scale(ctlBase + ctl));
            }
            AdsSpeed.Refresh();
            var rp = dw._recoilPattern;
            if (rp != null && dw.recoilPatternSettings != null) rp.Init(dw.recoilPatternSettings);
            // 連射するかはプレイヤー側の RecoilAnimation.fireMode で決まる（装備のときに武器から写される）。
            // 手に持ったまま付け替えたときは、ここで渡し直す（装備のときは OnEquip が渡す）
            var ra = dw._recoilAnimation;
            if (ra != null && dw.recoilData != null && Refs.Fps?.GetActiveItem()?.Pointer == dw.Pointer) ra.Init(dw.recoilData, dw.fireRate, dw._fireMode);

            // サプレッサー：発砲炎を出さない（空の物に差し替える）、気付かれる範囲を狭く
            bool sup = Has(id, "suppressor", "p_suppressor");
            // 最初から付いているサプレッサー（CV-1）は発砲炎も範囲もゲームのまま。付けていないときは Sound が決めた範囲（CV-1 から外したら G-18C と同じ）
            bool factorySup = sup && Catalog.IsFactory(Store.Equipped(id, Cat.Muzzle), id);
            Flash(dw, (sup && !factorySup) || Has(id, "flash_hider", "p_flash_hider"));   // フラッシュハイダーも発砲炎なし
            float alert = pdw.customAlertDistance != 0f ? pdw.customAlertDistance : DefaultAlert;
            if (sup && !factorySup) dw.customAlertDistance = alert * SuppressedAlert;
            else if (factorySup) dw.customAlertDistance = pdw.customAlertDistance;
        }

        // 発砲炎はゲームが装備のとき（OnEquip）にも入れ直すので、その後にも呼ぶ
        static void Flash(DemoWeapon dw, bool sup)
        {
            var cur = dw.fireParticlesEffect;
            if (cur != null && !cur.name.StartsWith(Mount.Prefix)) _flash[dw.Pointer] = cur;
            _flash.TryGetValue(dw.Pointer, out var orig);
            // 空の物は武器に 1 つだけ（装備のたびに作らない）。外したら消す
            var have = dw.transform.Find(Mount.Prefix + "noflash");
            if (sup)
            {
                if (have == null)
                {
                    var dummy = new GameObject(Mount.Prefix + "noflash");
                    dummy.transform.SetParent(dw.transform, false);
                    dummy.SetActive(false);
                    have = dummy.transform;
                }
                dw.fireParticlesEffect = have.gameObject;
            }
            else
            {
                if (orig != null && cur != null && cur.name == Mount.Prefix + "noflash") dw.fireParticlesEffect = orig;
                // 火が無い（空・消えた・控えが無いまま空の物だった）と OnFire が途中で止まり撃てない。ゲームが装備のときに入れる物を探す
                var now = dw.fireParticlesEffect;
                if (now == null || now.name == Mount.Prefix + "noflash") dw.fireParticlesEffect = GameObject.Find(GameFlash);
                if (have != null) { have.SetParent(null, false); UnityEngine.Object.Destroy(have.gameObject); }   // 消えるのはフレームの終わりなので、先に武器から外す
            }
        }

        static void OnEquip_Post(DemoWeapon __instance)
        {
            try
            {
                int id = Weapons.IdOf(__instance);
                if (id >= 0) Flash(__instance, (Has(id, "suppressor", "p_suppressor") && !Catalog.IsFactory(Store.Equipped(id, Cat.Muzzle), id)) || Has(id, "flash_hider", "p_flash_hider"));
                // 装備のときゲームは武器ごとに覚えた撃ち方（PlayerPrefs「名前+FireMode」）に戻す（CV-1 は "semi" が残っている）。
                // M93R ストックならフルオートに。フルオートの無い武器に "auto" が残っていたらセミに
                bool stock = id >= 0 && Has(id, "p_stock");
                var want = stock ? FireMode.Auto : !__instance.supportsAuto && __instance._fireMode == FireMode.Auto ? FireMode.Semi : __instance._fireMode;
                if (__instance._fireMode != want)
                {
                    __instance._fireMode = want;
                    var ra = __instance._recoilAnimation;
                    if (ra != null && __instance.recoilData != null) ra.Init(__instance.recoilData, __instance.fireRate, __instance._fireMode);
                }
            }
            catch (Exception e) { Main.Log("[effect] flash on equip failed: " + e.Message); }
        }

        static readonly Dictionary<IntPtr, GameObject> _flash = new();
        const string GameFlash = "MuzzleFlash10";   // Weapon.OnEquip が GameObject.Find で入れる銃口の火（全武器共通）

        // 反動の倍率の範囲（レアリティmod と同じ）
        const float MinRecoilScale = 0f;
        static float Scale(float points) => Mathf.Max(MinRecoilScale, StatScale.RecoilMult(points));

        // ---------- M93R ストックのハンドガンの反動 ----------
        // ハンドガンの反動の動き（RecoilAnimData.recoilCurves）にはフルオートで続けて撃つときの形が無く、2 発目で RecoilAnimation.Play が
        // 例外で止まる（Eagle：KCurves.GetCurveLength の IndexOutOfRange）。動きの形は SP-320 のものを使う。
        // 大きさはその銃のものを残しつつ、SP-320 より大きい分だけ寄せる（跳ね上がりすぎて照準の中心に弾が行かない）：
        //   大きさ = 自分^(1-StockPull) × SP-320^StockPull（SP-320 以下ならそのまま）
        const float StockPull = 0.75f;
        static readonly Dictionary<int, (RecoilPatternSettings, RecoilAnimData)> _stockRecoil = new();

        static (RecoilPatternSettings, RecoilAnimData) StockRecoilOf(int id, DemoWeapon pdw, DemoWeapon sp)
        {
            if (_stockRecoil.TryGetValue(id, out var r) && r.Item1 != null && r.Item2 != null) return r;
            RecoilPatternSettings pat = pdw.recoilPatternSettings;
            if (pat != null && sp.recoilPatternSettings != null)
            {
                pat = UnityEngine.Object.Instantiate(pdw.recoilPatternSettings).Cast<RecoilPatternSettings>();
                pat.name = pdw.recoilPatternSettings.name + "_stock"; pat.hideFlags = HideFlags.DontUnloadUnusedAsset;
                pat.verticalRecoil = Pull(pat.verticalRecoil, sp.recoilPatternSettings.verticalRecoil);
                pat.horizontalRecoil = Pull(pat.horizontalRecoil, sp.recoilPatternSettings.horizontalRecoil);
            }
            RecoilAnimData anim = pdw.recoilData;
            if (anim != null && sp.recoilData != null)
            {
                var s = sp.recoilData;
                anim = UnityEngine.Object.Instantiate(pdw.recoilData).Cast<RecoilAnimData>();
                anim.name = pdw.recoilData.name + "_stock"; anim.hideFlags = HideFlags.DontUnloadUnusedAsset;
                anim.recoilCurves = s.recoilCurves;
                anim.pitch = Pull(anim.pitch, s.pitch); anim.roll = Pull(anim.roll, s.roll); anim.yaw = Pull(anim.yaw, s.yaw);
                anim.kickback = Pull(anim.kickback, s.kickback); anim.kickUp = Pull(anim.kickUp, s.kickUp); anim.kickRight = Pull(anim.kickRight, s.kickRight);
            }
            Main.Log($"[effect] stock recoil {Tracker.NameOf(id)}: pattern v={pat?.verticalRecoil} anim pitch={anim?.pitch} (own v={pdw.recoilPatternSettings?.verticalRecoil} pitch={pdw.recoilData?.pitch})");
            return _stockRecoil[id] = (pat, anim);
        }

        static float PullK(float own, float sp) => own <= sp || own <= 0f ? 1f : Mathf.Pow(sp / own, StockPull);
        static Vector2 Pull(Vector2 own, Vector2 sp) => own * PullK(own.magnitude, sp.magnitude);
        static Vector4 Pull(Vector4 own, Vector4 sp) => own * PullK(own.magnitude, sp.magnitude);

        // 反動データ（共有アセット）の複製。ロールと倍率ごとに使い回す
        static readonly Dictionary<string, RecoilPatternSettings> _patterns = new();
        static readonly Dictionary<string, RecoilAnimData> _anims = new();

        static RecoilPatternSettings Pattern(string uid, RecoilPatternSettings original, float s)
        {
            if (original == null) return null;
            var key = $"{uid}|{original.name}|{s:0.000}";
            if (_patterns.TryGetValue(key, out var c) && c != null) return c;
            c = UnityEngine.Object.Instantiate(original).Cast<RecoilPatternSettings>();
            c.hideFlags = HideFlags.DontUnloadUnusedAsset;
            c.horizontalRecoil = original.horizontalRecoil * s;
            c.verticalRecoil = original.verticalRecoil * s;
            return _patterns[key] = c;
        }

        static RecoilAnimData Anim(string uid, RecoilAnimData original, float s)
        {
            if (original == null) return null;
            var key = $"{uid}|{original.name}|{s:0.000}";
            if (_anims.TryGetValue(key, out var c) && c != null) return c;
            c = UnityEngine.Object.Instantiate(original).Cast<RecoilAnimData>();
            c.hideFlags = HideFlags.DontUnloadUnusedAsset;
            c.pitch = original.pitch * s; c.roll = original.roll * s; c.yaw = original.yaw * s;
            c.kickback = original.kickback * s; c.kickUp = original.kickUp * s; c.kickRight = original.kickRight * s;
            c.noiseScalar = original.noiseScalar * s; c.pushAmount = original.pushAmount * s;   // 撃ったときの小刻みな揺れ・押し込み（0 倍で無ブレ）
            return _anims[key] = c;
        }

        // ---------- バーティカルグリップ：撃ち続けるほどコントロールが上がり、指を離すと戻る ----------
        // ---------- コンペンセイター・マズルブレーキ：引き金を引いてから CompShots 発目まではコントロール +CompBonus（指を離すと最初から） ----------
        // 武器ごとに専用の複製（視点の持ち上がり＝RecoilPatternSettings と、銃の跳ね上がり＝RecoilAnimData）を持ち、
        // 撃った弾数に合わせて毎フレーム書き換える（どちらも毎発その場で読むので、次の 1 発から効く）。
        // 撃った弾数は弾の減りで数える（キーの割り当て・パッドに関係なく）。次の弾が来ないまま連射の間隔の 2 倍がたったら、指を離した扱い

        sealed class LiveRecoil { public RecoilPatternSettings Clone, Original; public RecoilAnimData AnimClone, AnimOriginal; public float Points; public bool Vgrip, Comp, Bipod; }
        static readonly Dictionary<string, LiveRecoil> _live = new();
        static int _shots, _lastAmmo;
        static float _lastShotAt;
        static IntPtr _shotWeapon;

        static LiveRecoil Live(string key, int id, RecoilPatternSettings original, RecoilAnimData anim, float points)
        {
            if (original == null) return null;
            if (!_live.TryGetValue(key, out var l) || l.Clone == null || (anim != null && l.AnimClone == null))
            {
                var c = UnityEngine.Object.Instantiate(original).Cast<RecoilPatternSettings>();
                c.hideFlags = HideFlags.DontUnloadUnusedAsset;
                RecoilAnimData ac = null;
                if (anim != null) { ac = UnityEngine.Object.Instantiate(anim).Cast<RecoilAnimData>(); ac.hideFlags = HideFlags.DontUnloadUnusedAsset; }
                _live[key] = l = new LiveRecoil { Clone = c, AnimClone = ac };
            }
            l.Original = original;
            l.AnimOriginal = anim;
            l.Points = points;
            l.Vgrip = Has(id, "vertical_grip");
            l.Comp = Has(id, "p_compensator", "muzzle_brake");
            l.Bipod = Has(id, "bipod");
            SetLive(l, 0);
            return l;
        }

        // バーティカルグリップ：撃ち始めは普通の倍率。撃つほど小さくなり、最後は反動なし（倍率 0）まで。
        // コンペンセイター：撃った弾が CompShots 発より少ない間（次の弾が最初の CompShots 発のうち）は +CompBonus
        static float LiveScale(LiveRecoil l, int shots)
        {
            float points = l.Points + (l.Comp && shots < CompShots ? CompBonus : 0f) + (l.Bipod && Crouching ? BipodControl : 0f);
            if (!l.Vgrip || shots <= 0) return Scale(points);
            return Mathf.Min(Scale(points), StatScale.RecoilMult(points + VgripPerShot * shots));
        }

        static void SetLive(LiveRecoil l, int shots)
        {
            float s = LiveScale(l, shots);
            l.Clone.horizontalRecoil = l.Original.horizontalRecoil * s;
            l.Clone.verticalRecoil = l.Original.verticalRecoil * s;
            var a = l.AnimClone; var o = l.AnimOriginal;
            if (a != null && o != null)
            {
                a.pitch = o.pitch * s; a.roll = o.roll * s; a.yaw = o.yaw * s;
                a.kickback = o.kickback * s; a.kickUp = o.kickUp * s; a.kickRight = o.kickRight * s;
                a.noiseScalar = o.noiseScalar * s; a.pushAmount = o.pushAmount * s;
            }
        }

        // ---------- バイポッド：しゃがんでいる間 ----------
        static bool Crouching;
        public static bool? CrouchOverrideForTest;
        public static float DamageAdd(int id) => Has(id, "p_linear_comp") ? LinCompDamage : 0f;

        // スタビーグリップ：移動中のエルゴ
        public const float StubbyMoveErgo = 15f;
        public static bool? MovingOverrideForTest;
        static bool Moving()
        {
            if (MovingOverrideForTest is { } t) return t;
            var s = Refs.Fps?._movementComponent?.MovementState;
            return s == Il2CppDemo.Scripts.Runtime.Character.FPSMovementState.Walking || s == Il2CppDemo.Scripts.Runtime.Character.FPSMovementState.Sprinting
                || s == Il2CppDemo.Scripts.Runtime.Character.FPSMovementState.Sliding;
        }
        static bool IsCrouching()
        {
            var mv = Refs.Fps?.GetComponent<Il2CppDemo.Scripts.Runtime.Character.FPSMovement>();
            return mv != null && mv.PoseState != Il2CppDemo.Scripts.Runtime.Character.FPSPoseState.Standing;   // しゃがみ（伏せも）
        }

        // ---------- KAC グリップ：リーン中は被弾時の手ぶれ（体力が減ったときの揺れ）を 0 に（移動による揺れは TickKacSway） ----------
        // PlayerHealthManager.Update が毎フレーム手ぶれの層の強さ（alpha）を決めるので、その後で 0 にする
        public static bool? LeanOverrideForTest;
        static void HealthUpdate_Post(Il2Cpp.PlayerHealthManager __instance)
        {
            try
            {
                Active(out int id);
                if (id < 0 || !Has(id, "kac_grip")) return;
                bool lean = LeanOverrideForTest ?? (Refs.Fps != null && Refs.Fps._isLeaning);
                if (lean && __instance._tremorSettings != null) __instance._tremorSettings.alpha = 0f;
            }
            catch (Exception e) { Core.LogOnce("kacgrip", "[effect] KAC grip failed: " + e.Message); }
        }
        // KAC グリップ：リーン中は移動による揺れ（SwayLayer の Move Sway。歩く・走ると武器と視点が揺れる）と、
        //   構えたときの歩きのアニメーションの揺れ（AdditiveLayer の adsScalar。歩くとサイトがゆらゆらする）も 0 に。
        //   設定は同じ種類の武器で共有なので、効かせている間だけ 0 にして、外れたら元の値に戻す
        static Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.SwayLayer.SwayLayerSettings _kacSway;
        static Vector3 _kacPos, _kacRot;
        static Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.AdditiveLayer.AdditiveLayerSettings _kacAdd;
        static float _kacAds;
        public static bool KacSwayOffForTest => _kacSway != null;
        static void TickKacSway()
        {
            var dw = Active(out int id);
            bool on = dw != null && id >= 0 && Has(id, "kac_grip") && (LeanOverrideForTest ?? (Refs.Fps != null && Refs.Fps._isLeaning));
            var s = on ? LayerOf<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.SwayLayer.SwayLayerSettings>(dw) : null;
            if (_kacSway != null && (s == null || s.Pointer != _kacSway.Pointer)) { SetSwayScale(_kacSway, _kacPos, _kacRot); _kacSway = null; }
            if (s != null && _kacSway == null)
            {
                _kacSway = s; _kacPos = s.moveSwayPositionSpring.scale; _kacRot = s.moveSwayRotationSpring.scale;
                SetSwayScale(s, Vector3.zero, Vector3.zero);
            }
            var a = on ? LayerOf<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.AdditiveLayer.AdditiveLayerSettings>(dw) : null;
            if (_kacAdd != null && (a == null || a.Pointer != _kacAdd.Pointer)) { _kacAdd.adsScalar = _kacAds; _kacAdd = null; }
            if (a != null && _kacAdd == null) { _kacAdd = a; _kacAds = a.adsScalar; a.adsScalar = 0f; }
        }
        static void SetSwayScale(Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.SwayLayer.SwayLayerSettings s, Vector3 pos, Vector3 rot)
        {
            var p = s.moveSwayPositionSpring; p.scale = pos; s.moveSwayPositionSpring = p;
            var r = s.moveSwayRotationSpring; r.scale = rot; s.moveSwayRotationSpring = r;
        }
        static T LayerOf<T>(DemoWeapon w) where T : Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorLayerSettings
        {
            var prof = w?.GetComponent<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorEntity>()?.animatorProfile;
            if (prof?.settings == null) return null;
            foreach (var x in prof.settings) { var a = x?.TryCast<T>(); if (a != null) return a; }
            return null;
        }
        public static Vector3 MoveSwayScaleForTest(DemoWeapon w) => LayerOf<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.SwayLayer.SwayLayerSettings>(w)?.moveSwayPositionSpring.scale ?? -Vector3.one;
        public static float AdsBobForTest(DemoWeapon w) => LayerOf<Il2CppKINEMATION.FPSAnimationFramework.Runtime.Layers.AdditiveLayer.AdditiveLayerSettings>(w)?.adsScalar ?? -1f;

        public static float TremorForTest => UnityEngine.Object.FindObjectOfType<Il2Cpp.PlayerHealthManager>()?._tremorSettings?.alpha ?? -1f;

        public static int ShotsForTest => _shots;
        public static float LiveScaleForTest(string uid) => _live.TryGetValue(uid, out var l) ? LiveScale(l, _shots) : -1f;
        public static int? ShotsOverrideForTest;   // テスト：撃った弾数の代わり（指を離した扱いは -1）

        // テスト：手に持っている武器の今の倍率と、ゲームが実際に使っている反動のデータがその倍率の複製か
        public static bool LiveUsedForTest(DemoWeapon dw, int id, out float scale)
        {
            scale = -1f;
            if (dw == null || !_live.TryGetValue(Link.Key(dw, id), out var l) || l.Clone == null) return false;
            scale = LiveScale(l, _shots);
            var pat = dw._recoilPattern?.recoilSettings; var ra = dw._recoilAnimation?.RecoilData;
            bool patOk = pat != null && pat.Pointer == l.Clone.Pointer && Mathf.Abs(l.Clone.verticalRecoil.y - l.Original.verticalRecoil.y * scale) < 1e-4f;
            bool animOk = l.AnimClone == null || (ra != null && ra.Pointer == l.AnimClone.Pointer && Mathf.Abs(l.AnimClone.kickUp.x - l.AnimOriginal.kickUp.x * scale) < 1e-4f);
            return patOk && animOk;
        }

        static void TickVgrip()
        {
            var dw = Active(out int id);
            if (dw == null || id < 0 || !Has(id, LiveAtts) || !_live.TryGetValue(Link.Key(dw, id), out var l) || l.Clone == null || l.Original == null) { _shots = 0; return; }
            Crouching = CrouchOverrideForTest ?? IsCrouching();
            int ammo = dw.SelectedMag?.AmmoCount ?? 0;
            float gap = Mathf.Max(0.25f, 2f * 60f / Mathf.Max(60f, dw.fireRate));
            if (dw.Pointer != _shotWeapon) { _shotWeapon = dw.Pointer; _shots = 0; }
            else if (ammo < _lastAmmo) { _shots += _lastAmmo - ammo; _lastShotAt = Time.unscaledTime; }
            else if (_shots > 0 && Time.unscaledTime - _lastShotAt > gap) _shots = 0;   // 撃ち止めた
            if (ShotsOverrideForTest is int t) _shots = Math.Max(0, t);
            _lastAmmo = ammo;
            SetLive(l, _shots);
        }

        // ---------- スイスのスコープ：胸の倍率を ChestMult 倍に ----------
        // プレイヤーの弾が敵に当たると BulletHandler.BulletHit が「当たった部位の倍率（LivingEntityCollider.DamageMultiplier）」をかける。
        // その 1 発の間だけ倍率を変えて、終わったら戻す。胸＝spine_02（敵の頭は 55 倍が大半、胸は 1.3〜1.8 倍）

        static Il2CppBallistics.LivingEntityCollider _hit;
        static float _hitMult;

        static void BulletHit_Pre(Il2CppBallistics.Weapon weapon, Il2CppBallistics.LivingEntityCollider hitEntity, Rigidbody hitRigid)
        {
            _hit = null;
            try
            {
                if (hitEntity == null || weapon == null) return;
                float mult = PartMult(hitEntity, weapon);
                if (mult == 1f) return;
                _hit = hitEntity;
                _hitMult = hitEntity.DamageMultiplier;
                hitEntity.DamageMultiplier = _hitMult * mult;
                if (IsChest(hitEntity)) ChestHitsForTest++;
            }
            catch { }
        }

        // 手に持っている武器の弾が当たった部位にかける倍率（1 なら何もしない）
        static float PartMult(Il2CppBallistics.LivingEntityCollider c, Il2CppBallistics.Weapon weapon)
        {
            if (c.ParentLivingEntity?.PlayerHealthManager != null) return 1f;   // プレイヤーへの弾は対象外
            var dw = Active(out int id);
            if (dw == null || !weapon.transform.IsChildOf(dw.transform)) return 1f;
            if (IsChest(c) && Has(id, "swiss_scope")) return ChestMult;
            if (Has(id, "arrowhead", "p_arrowhead")) return IsArm(c) ? ArmFull(c) : IsLeg(c) ? LegMult : 1f;
            return 1f;
        }

        // 腕のダメージを胴体と同じ 1 倍にする倍率（もともと 1 倍以上なら変えない）
        static float ArmFull(Il2CppBallistics.LivingEntityCollider c) => Mathf.Max(1f, 1f / Mathf.Max(c.DamageMultiplier, 0.01f));

        // 敵の部位の名前：腕＝upperarm・lowerarm・hand、脚＝thigh・calf・foot（左右 _l・_r）
        static bool IsArm(Il2CppBallistics.LivingEntityCollider c) { var n = c.gameObject.name.ToLowerInvariant(); return n.StartsWith("upperarm") || n.StartsWith("lowerarm") || n.StartsWith("hand"); }
        static bool IsLeg(Il2CppBallistics.LivingEntityCollider c) { var n = c.gameObject.name.ToLowerInvariant(); return n.StartsWith("thigh") || n.StartsWith("calf") || n.StartsWith("foot"); }
        public static float PartMultForTest(Il2CppBallistics.LivingEntityCollider c, Il2CppBallistics.Weapon w) => PartMult(c, w);

        static bool IsChest(Il2CppBallistics.LivingEntityCollider c)
        {
            var n = c.gameObject.name.ToLowerInvariant();
            return n == "spine_02" || n.Contains("chest");
        }

        static void BulletHit_Post()
        {
            try { if (_hit != null) _hit.DamageMultiplier = _hitMult; } catch { }
            _hit = null;
        }

        public static int ChestHitsForTest;
        public static float ChestTargetForTest(Il2CppBallistics.LivingEntityCollider c) => IsChest(c) ? c.DamageMultiplier * ChestMult : -1f;
        public static void BulletHitForTest(Il2CppBallistics.Weapon w, Il2CppBallistics.LivingEntityCollider c, out float during)
        {
            BulletHit_Pre(w, c, null);
            during = c.DamageMultiplier;
            BulletHit_Post();
        }

        // ---------- 拡張マガジン・ファストマグ：ラン中のボタンでの弾込めが遅い ----------
        // コルーチンが入れた全体の時間を延ばす（同じ値を 2 度延ばさないよう、延ばした値を覚えておく）

        static readonly Dictionary<IntPtr, float> _loadScaled = new();

        static bool SlowLoad(string weaponName) => Has(IdByName(weaponName), "ext_mag", "p_ext_mag", "fast_mag", "p_fast_mag");

        static float Stretch(IntPtr it, float total)
        {
            if (_loadScaled.TryGetValue(it, out var v) && Mathf.Approximately(v, total)) return total;
            if (_loadScaled.Count > 64) _loadScaled.Clear();
            return _loadScaled[it] = total / LoadSpeed;
        }

        static void ButtonLoad_Post(Il2Cpp.AmmoButtonLoader._LoadMagsSequentially_d__23 __instance)
        {
            try
            {
                var mag = __instance._mag_5__4;
                if (mag != null && SlowLoad(mag.WeaponName) && __instance._totalLoadTime_5__6 > 0f)
                    __instance._totalLoadTime_5__6 = Stretch(__instance.Pointer, __instance._totalLoadTime_5__6);
            }
            catch { }
        }

        // ---------- 拡張マガジン：容量 1.5 倍 ----------
        // 持っているマガジン（Inventory.MagStored）の最大弾数を、その武器に拡張マガジンを付けているかに合わせる。
        // 外したときに入りきらない弾は弾薬に戻す

        static Dictionary<string, int> _idByName;

        static int IdByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            if (_idByName == null || !_idByName.ContainsKey(name))
            {
                _idByName = new Dictionary<string, int>();
                for (int i = 0; i < 64; i++) { var wi = Tracker.ItemById(i); if (wi != null) _idByName[wi.GetName()] = i; }
            }
            return _idByName.TryGetValue(name, out var id) ? id : -1;
        }

        public static int Capacity(int baseCap, int weaponId) => Has(weaponId, "ext_mag", "p_ext_mag") ? Mathf.RoundToInt(baseCap * MagMult) : baseCap;

        // ゲームのマガジンの元の大きさ（MagData の同じ番号）
        static int BaseCap(int index)
        {
            var data = Refs.Inv?.MagData?.Mags;
            if (data == null) return 0;
            for (int k = 0; k < data.Count; k++) if (data[k] != null && data[k].Index == index) return data[k].MaxAmmoCount;
            return 0;
        }

        static float _nextSync;

        public static void SyncMags()
        {
            var inv = Refs.Inv;
            var mags = inv?.MagStored;
            if (mags == null || inv.MagData?.Mags == null) return;
            // 拠点でもランの中でも、セーブを読み込んだ後に余りの弾を戻す（ランに出るときもセーブを読み直す。以前は拠点だけで、ランでは弾が減っていた）
            if (_restorePending) RestoreExtra(mags);
            for (int i = 0; i < mags.Count; i++)
            {
                var m = mags[i];
                if (m == null) continue;
                int baseCap = BaseCap(m.Index);
                if (baseCap <= 0) continue;
                int want = Capacity(baseCap, IdByName(m.WeaponName));
                if (m.MaxAmmoCount == want) continue;
                if (m.AmmoCount > want)
                {
                    int extra = m.AmmoCount - want;
                    m.AmmoCount = want;
                    inv.AddAmmo(m.Caliber, extra);
                }
                Main.Log($"[effect] mag {m.WeaponName} capacity {m.MaxAmmoCount} -> {want}");
                m.MaxAmmoCount = want;
            }
        }

        // ---------- 拡張マガジンとセーブ ----------
        // ゲームのセーブ（暗号化の直前の JSON）では、持っているマガジン（MagStored）を元の大きさ・元の大きさまでの弾数に書き換える。
        // 余った弾は UserData/FPCustom/mag_extra.json に覚えておき、Mod があれば読み込みの後で戻す（Mod を外すとその弾は消える）。
        // 保管庫へ移す武器のマガジンは、移す前に元の大きさにして余った弾を弾薬に戻す（保管庫のセーブは別のファイル）

        public sealed class Extra { public int Slot { get; set; } public string Weapon { get; set; } public int Ammo { get; set; } }
        static string ExtraPath => System.IO.Path.Combine(Main.Dir, "mag_extra.json");
        static bool _restorePending;
        public static bool RestorePendingForTest => _restorePending;

        static readonly System.Text.RegularExpressions.Regex MagRx = new(@"\{[^{}]*\}");

        public static string CleanSaveJson(string json, List<Extra> extras)
        {
            int start = json.IndexOf("\"MagStored\"", StringComparison.Ordinal);
            if (start < 0) return json;
            int open = json.IndexOf('[', start), close = open < 0 ? -1 : json.IndexOf(']', open);
            if (open < 0 || close < 0) return json;
            string part = json.Substring(open, close - open);
            var sb = new System.Text.StringBuilder();
            int slot = 0, at = 0;
            for (var mt = MagRx.Match(part); mt.Success; mt = mt.NextMatch(), slot++)
            {
                sb.Append(part, at, mt.Index - at);
                at = mt.Index + mt.Length;
                string o = mt.Value;
                int idx = Int(o, "Index"), ammo = Int(o, "AmmoCount"), max = Int(o, "MaxAmmoCount");
                int baseCap = idx >= 0 ? BaseCap(idx) : 0;
                if (baseCap > 0 && ammo >= 0 && max >= 0 && (max > baseCap || ammo > baseCap))
                {
                    if (ammo > baseCap) extras.Add(new Extra { Slot = slot, Weapon = Str(o, "WeaponName"), Ammo = ammo - baseCap });
                    o = SetInt(SetInt(o, "AmmoCount", Math.Min(ammo, baseCap)), "MaxAmmoCount", baseCap);
                }
                sb.Append(o);
            }
            sb.Append(part, at, part.Length - at);
            return json.Substring(0, open) + sb + json.Substring(close);
        }

        static int Int(string o, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(o, $"\"{key}\"\\s*:\\s*(-?\\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }
        static string Str(string o, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(o, $"\"{key}\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
        static string SetInt(string o, string key, int v)
        {
            var m = System.Text.RegularExpressions.Regex.Match(o, $"(\"{key}\"\\s*:\\s*)(-?\\d+)");
            return m.Success ? o.Substring(0, m.Groups[2].Index) + v + o.Substring(m.Groups[2].Index + m.Groups[2].Length) : o;
        }

        static void Encrypt_Pre(ref string json)
        {
            try
            {
                var extras = new List<Extra>();
                json = CleanSaveJson(json, extras);
                // 読み込んだ後、まだ余りの弾を戻していない間は、覚えている余りの弾をそのまま残す（上書きすると弾が消える）
                if (_restorePending && System.IO.File.Exists(ExtraPath)) { Main.Log("[effect] save before the extra rounds were restored: kept mag_extra.json"); return; }
                System.IO.File.WriteAllText(ExtraPath, System.Text.Json.JsonSerializer.Serialize(extras));
                if (extras.Count > 0) Main.Log($"[effect] save: {extras.Count} extended mags written at normal size ({extras.Sum(e => e.Ammo)} extra rounds kept by the mod)");
            }
            catch (Exception e) { Main.Log("[effect] save clean failed: " + e.Message); }
        }

        static void SaveLoad_Post() => _restorePending = true;

        // Mod をオフにしたとき：覚えておいた余りの弾を消す
        public static void DropExtraRounds()
        {
            try
            {
                if (!System.IO.File.Exists(ExtraPath)) return;
                var extras = System.Text.Json.JsonSerializer.Deserialize<List<Extra>>(System.IO.File.ReadAllText(ExtraPath)) ?? new List<Extra>();
                System.IO.File.Delete(ExtraPath);
                Main.Log($"[effect] mod off: dropped {extras.Sum(e => e.Ammo)} extra rounds of {extras.Count} extended mags");
            }
            catch (Exception e) { Main.Log("[effect] drop extra rounds failed: " + e.Message); }
        }

        // 読み込んだセーブのマガジン（元の大きさで満タン）に、覚えておいた余りの弾を戻す
        public static void RestoreExtra(Il2CppSystem.Collections.Generic.List<Il2Cpp.Mag> mags)
        {
            _restorePending = false;
            try
            {
                if (!System.IO.File.Exists(ExtraPath)) return;
                var extras = System.Text.Json.JsonSerializer.Deserialize<List<Extra>>(System.IO.File.ReadAllText(ExtraPath)) ?? new List<Extra>();
                int n = 0;
                foreach (var e in extras)
                {
                    if (e.Slot < 0 || e.Slot >= mags.Count) continue;
                    var m = mags[e.Slot];
                    int baseCap = m != null ? BaseCap(m.Index) : 0;
                    if (m == null || m.WeaponName != e.Weapon || baseCap <= 0 || m.MaxAmmoCount != baseCap || m.AmmoCount != baseCap) continue;
                    int cap = Capacity(baseCap, IdByName(m.WeaponName));
                    m.MaxAmmoCount = cap;
                    m.AmmoCount = Math.Min(cap, baseCap + e.Ammo);
                    n++;
                }
                System.IO.File.Delete(ExtraPath);
                if (n > 0) Main.Log($"[effect] restored extra rounds to {n} extended mags");
            }
            catch (Exception e) { Main.Log("[effect] restore extra rounds failed: " + e.Message); }
        }

        static void ToStash_Pre(Il2Cpp.WeaponItem weaponItem)
        {
            try
            {
                var inv = Refs.Inv;
                var mags = inv?.MagStored;
                string name = weaponItem?.GetName();
                if (mags == null || name == null) return;
                foreach (var m in mags)
                {
                    int baseCap = m != null && m.WeaponName == name ? BaseCap(m.Index) : 0;
                    if (baseCap <= 0 || m.MaxAmmoCount <= baseCap) continue;
                    if (m.AmmoCount > baseCap) { inv.AddAmmo(m.Caliber, m.AmmoCount - baseCap); m.AmmoCount = baseCap; }
                    m.MaxAmmoCount = baseCap;
                }
            }
            catch (Exception e) { Main.Log("[effect] stash mags failed: " + e.Message); }
        }

        public static void Tick()
        {
            try { TickVgrip(); } catch (Exception e) { Main.Log("[effect] vertical grip failed: " + e.Message); }
            try { TickKacSway(); } catch (Exception e) { Core.LogOnce("kacsway", "[effect] KAC sway failed: " + e.Message); }
            try { Active(out int aid); LaserLab.TickDot(aid); } catch (Exception e) { Core.LogOnce("laserdot", "[effect] laser dot color failed: " + e.Message); }
            if (Time.unscaledTime < _nextSync) return;
            _nextSync = Time.unscaledTime + 0.5f;
            try { SyncMags(); } catch (Exception e) { Main.Log("[effect] mag sync failed: " + e.Message); }
        }
    }
}
