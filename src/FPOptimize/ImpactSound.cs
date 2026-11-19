using System;
using System.Collections.Generic;
using UnityEngine;
using HitEffectPool = Il2CppBallistics.HitEffectPool;
using ImpactObject = Il2CppBallistics.ImpactObject;

namespace FPOptimize
{
    // 撃ち合い中のカクつき対策（弾の当たり音を間引く）。
    // 弾が当たるたびに、ゲームは HitEffectPool から当たりエフェクト（種類ごとに 8 個を使い回し）を出して、音を鳴らす。
    // 使い回すときに前の音（AudioSource）が止められ、これが 1 回 0.2〜0.9ms（ときどき 10ms 以上）かかる。
    // 音の部品が最初から止まっていれば、使い回しでも止める処理が起きない。
    //
    // そこで、エフェクトを出す直前（まだ非表示のうち）に、次の音は鳴らさないと決めたものだけ音の部品を止めておく。
    //   ・聞こえない距離の当たり音（音の設定から計算した音量がほぼ 0）
    //   ・同じ種類の音がほんの少し前に鳴ったばかりのもの（重なって聞き分けられない）。遠めは 0.1 秒、近くは 0.04 秒
    //     （連射でも 1 発ごとの間隔は 0.06 秒以上あるので、自分の 1 発ずつの音は消えない）
    // 見た目（破片・血しぶき）はそのまま。
    //
    // 倒れた敵の体や落ちた武器が床に当たる音（ImpactCFX）も同じ。こちらは止めるのが 1 回 5〜15ms と特に重い。
    // 音の素材を取り出す直前（まだ非表示）に、ほんの少し前に同じような音が鳴ったばかりなら、今回は鳴らさない（音の部品を止めておく）。
    public static class ImpactSound
    {
        public static bool On = true;          // 計測用（PerfProbe が A/B で切り替える）
        const float Near = 12f;                // ここより近い当たり音はいつも鳴らす（m）
        const float Silent = 0.03f;            // これより小さい音量は聞こえないとみなす（約 -30dB）
        const float MinGap = 0.10f;            // 遠めの同じ種類の音は、この秒数に 1 回まで
        const float NearGap = 0.04f;           // 近くでも、同じ種類の音がこれより短い間隔で重なったら 1 つにまとめる（聞き分けられない）
        const float BodyGap = 0.12f;           // 体・武器が床に当たる音は、全体でこの秒数に 1 回まで

        static bool _active;
        static bool _inSpawn, _skipNext;
        static readonly Dictionary<int, AudioSource> _srcOf = new();      // エフェクト（インスタンス ID）→ 音の部品（無ければ null）
        static readonly Dictionary<int, AudioSource> _prefabSrc = new();  // 元のプレハブ → 音の部品（音の設定を読む）
        static readonly Dictionary<int, float> _lastPlay = new();         // プレハブごとの最後に鳴らした時刻
        static Transform _listener;
        static long _spawns, _played, _skipFar, _skipRate, _playBlocked;
        static float _lastBody = -1f;
        static readonly HashSet<int> _bodySkip = new();   // 今回は鳴らさないと決めた音の素材（インスタンス ID）
        static long _bodyAll, _bodyPlayed, _bodySkipped, _bodyWasOn;
        static bool _bodyOn;

        public static void Install(HarmonyLib.Harmony _)
        {
            var h = new HarmonyLib.Harmony("FPOptimize.ImpactSound");
            bool ok = SafePatcher.Patch(h, typeof(HitEffectPool), "Spawn", null, typeof(ImpactSound), nameof(SpawnPre), nameof(SpawnPost))
                   && SafePatcher.Patch(h, typeof(GameObject), "SetActive", new[] { typeof(bool) }, typeof(ImpactSound), nameof(SetActivePre))
                   // 止めた音の部品で Play が呼ばれると、Unity は警告を出すだけで鳴らさない。警告の処理も重いので、ここで何もせず返す
                   && SafePatcher.Patch(h, typeof(AudioSource), "PlayHelper", null, typeof(ImpactSound), nameof(PlayPre));
            if (!ok)
            {
                try { h.UnpatchSelf(); } catch (Exception e) { Main.Log("[impact] unpatch failed: " + e.Message); }
                Main.Log("[impact] OFF (patch failed)");
                return;
            }
            _active = true;
            Main.Log("[impact] ON");
            // 体・武器が床に当たる音（入らなくても弾の当たり音のほうはそのまま動く）
            var hb = new HarmonyLib.Harmony("FPOptimize.ImpactSound.Body");
            _bodyOn = SafePatcher.Patch(hb, typeof(Il2CppImpactCFX.PooledEffectObjectBase), "RetrieveFromPool", null, typeof(ImpactSound), nameof(RetrievePre))
                   && SafePatcher.Patch(hb, typeof(Il2CppImpactCFX.Audio.ImpactAudioSource), "playAudio", null, typeof(ImpactSound), nameof(PlayAudioPre));
            if (!_bodyOn) { try { hb.UnpatchSelf(); } catch { } Main.Log("[impact] body sounds OFF (patch failed)"); }
            else Main.Log("[impact] body sounds ON");
        }

        public static void OnSceneLoaded()
        {
            _srcOf.Clear(); _prefabSrc.Clear(); _lastPlay.Clear(); _listener = null; _bodySkip.Clear(); _lastBody = -1f;
        }

        static void SpawnPre(ImpactObject prefab, Vector3 position)
        {
            _inSpawn = false;
            if (!_active || prefab == null) return;
            try
            {
                // オフのときも「鳴らす」として通す（前に止めた音の部品を元に戻すため）
                _inSpawn = true;
                _skipNext = On && Main.Enabled && Decide(prefab, position);
            }
            catch (Exception e) { _inSpawn = false; Fail(e); }
        }

        static void SpawnPost() => _inSpawn = false;

        static bool Decide(ImpactObject prefab, Vector3 pos)
        {
            _spawns++;
            int pid = prefab.GetInstanceID();
            if (!_prefabSrc.TryGetValue(pid, out var ps))
            {
                ps = prefab.GetComponent<AudioSource>();
                _prefabSrc[pid] = ps;
                if (ps != null)
                    Main.Log($"[impact] sound settings {prefab.name}: vol={ps.volume:0.00} blend={ps.spatialBlend:0.00} rolloff={ps.rolloffMode} min={ps.minDistance:0.0} max={ps.maxDistance:0.0} spat={ps.spatialize} playOnAwake={ps.playOnAwake}");
            }
            if (ps == null) return false;
            if (_listener == null)
            {
                var l = UnityEngine.Object.FindObjectOfType<AudioListener>();
                _listener = l != null ? l.transform : null;
                if (_listener == null) return false;
            }
            float d = Vector3.Distance(_listener.position, pos);
            float now = Time.unscaledTime;
            bool far = d > Near && ps.spatialBlend > 0.5f;
            if (far && Gain(ps, d) * ps.volume < Silent) { _skipFar++; return true; }
            if (_lastPlay.TryGetValue(pid, out var last) && now - last < (far ? MinGap : NearGap)) { _skipRate++; return true; }
            _lastPlay[pid] = now;
            _played++;
            return false;
        }

        // 距離による音量（Unity の減衰の設定どおり）
        static float Gain(AudioSource s, float d)
        {
            float min = Mathf.Max(0.01f, s.minDistance), max = Mathf.Max(min + 0.01f, s.maxDistance);
            switch (s.rolloffMode)
            {
                case AudioRolloffMode.Linear: return Mathf.Clamp01((max - d) / (max - min));
                case AudioRolloffMode.Logarithmic: return d <= min ? 1f : min / Mathf.Min(d, max);
                default:
                    var c = s.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                    return c == null ? 1f : Mathf.Clamp01(c.Evaluate(d / max));
            }
        }

        // エフェクトを出す直前（まだ非表示）に、音の部品を止める・戻す。非表示の物なので部品を切り替えても何も起きない
        static void SetActivePre(GameObject __instance, bool value)
        {
            if (!_inSpawn || !value) return;
            try
            {
                if (__instance.activeSelf) return;
                int id = __instance.GetInstanceID();
                if (!_srcOf.TryGetValue(id, out var src)) { src = __instance.GetComponent<AudioSource>(); _srcOf[id] = src; }
                if (src == null) return;
                bool want = !_skipNext;
                if (src.enabled != want) src.enabled = want;
            }
            catch (Exception e) { Fail(e); }
        }

        static bool PlayPre(AudioSource source)
        {
            if (!_active || source == null) return true;
            try
            {
                if (!source.enabled) { _playBlocked++; return false; }   // バニラでも鳴らない（警告だけ）
            }
            catch { }
            return true;
        }

        // 音の素材を出す直前（非表示のうち）：鳴らすかを決めて、鳴らさないなら音の部品を止めておく
        static void RetrievePre(Il2CppImpactCFX.PooledEffectObjectBase __instance)
        {
            if (!_active || !_bodyOn) return;
            try
            {
                var go = __instance.gameObject;
                if (go.activeSelf) return;   // 使用中のものを取り直すとき：何もしない
                int id = go.GetInstanceID();
                if (!_srcOf.TryGetValue(id, out var src)) { src = go.GetComponent<AudioSource>(); _srcOf[id] = src; }
                if (src == null) return;
                _bodyAll++;
                if (src.enabled) _bodyWasOn++;
                float now = Time.unscaledTime;
                bool skip = On && Main.Enabled && _lastBody >= 0f && now - _lastBody < BodyGap;
                if (skip) { _bodySkipped++; _bodySkip.Add(id); if (src.enabled) src.enabled = false; }
                else { _bodyPlayed++; _bodySkip.Remove(id); _lastBody = now; }
            }
            catch (Exception e) { Fail(e); }
        }

        // 鳴らさないと決めたものは、ゲームの「音を鳴らす」処理（部品を入れて Play）を通さない
        static bool PlayAudioPre(Il2CppImpactCFX.Audio.ImpactAudioSource __instance)
        {
            if (!_active || !_bodyOn) return true;
            try { return !_bodySkip.Contains(__instance.gameObject.GetInstanceID()); }
            catch { return true; }
        }

        static void Fail(Exception e)
        {
            Main.Log("[impact] failed, turning off: " + e.Message);
            _active = false;
        }

        // テスト用：[動いているか, 出した数, 鳴らした数, 間引いた数, 体の音の数, 体の音を鳴らした数, 体の音を間引いた数]
        public static long[] TestStats() => new[] { _active ? 1L : 0L, _spawns, _played, _skipFar + _skipRate, _bodyAll, _bodyPlayed, _bodySkipped };

        public static string Stats() => $"spawns={_spawns} played={_played} skipFar={_skipFar} skipRate={_skipRate} playBlocked={_playBlocked} body: all={_bodyAll} played={_bodyPlayed} skipped={_bodySkipped} srcWasOn={_bodyWasOn}";
    }
}
