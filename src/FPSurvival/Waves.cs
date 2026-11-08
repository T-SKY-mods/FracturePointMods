using System;
using System.Collections.Generic;

using FPModOption;

namespace FPSurvival
{
    public enum Kind { Office, Gallery, Special, Boss }

    // ウェーブの中身。ウェーブに終わりは無く、ずっとゆっくり難しくなる。中身は「段階」（2 ウェーブで 1 段階）で決める
    //   ウェーブ 1〜20   オフィス警備 4 → 9 体。11 から美術館警備が少しずつ混ざる
    //   ウェーブ 21〜40  9〜11 体。オフィス → 美術館 → 特殊部隊へ少しずつ置き換え
    //   ウェーブ 41〜60  特殊部隊が中心（12 体）。50 からは毎ウェーブ ボス 1 体
    //   ウェーブ 61〜80  特殊部隊 12 体 ＋ ボス 1 体（10 の倍数は 2 体）
    //   ウェーブ 81〜100 特殊部隊 12 体 ＋ ボス 2 体（10 の倍数は 3 体）
    //   ウェーブ 101〜   20 ウェーブごとにボスが 1 体ずつ増える（6 体まで）
    //   雑魚の与ダメージは 100 ウェーブで 1.5 倍、200 ウェーブで 2 倍（そこで頭打ち）
    // 同時に出ている敵は 10 体まで。ウェーブ開始時は 3 体、その後は数秒ごとに 1 体ずつ視界の外に出す
    public struct WaveSpec
    {
        public int Office, Gallery, Special, Boss;
        public int MaxAlive;
        public float Damage;      // 雑魚の与ダメージ倍率（ゲームの時間経過のウェーブと同じ仕組み）
        public int Total => Office + Gallery + Special + Boss;
        public override string ToString() => $"office {Office}, gallery {Gallery}, special {Special}, boss {Boss} (max alive {MaxAlive}, dmg x{Damage:0.00})";
    }

    public static class Waves
    {
        public const int RewardCapWave = 100;     // 1 体のドルが増えるのは 100 ウェーブまで
        // ハード：ウェーブの数え方は 1 からのまま、中身はノーマルの 40 ウェーブから。武器箱はレアリティが 5 ウェーブごとに上がり、20 でレジェンダリー確定
        public const int HardOffset = 39;
        public const int HardLegendaryWave = 20;
        public const int InitialSpawn = 3;
        public const int MaxAlive = 10;          // 同時に出ている敵の上限
        public const float BossHp = 600f;
        public const float BossHeadMax = 4f;     // 頭に当てたときの倍率の上限

        // 倒したときのドル。回復（$10,000）を買うか、貯めて持ち帰るかを選べる額にする
        //   序盤は数ウェーブに 1 回、後半は毎ウェーブ 1〜2 回回復できるくらい。ウェーブが進むほど 1 体の額も上がる（50 で約 2 倍）
        public static int Reward(Kind k, int wave)
        {
            int baseReward = k switch { Kind.Office => 400, Kind.Gallery => 700, Kind.Special => 800, _ => 5000 };
            float scale = 1f + 0.02f * (Math.Clamp(wave, 1, RewardCapWave) - 1);
            return (int)(Math.Round(baseReward * scale / 10f) * 10f);
        }

        public static WaveSpec For(int wave)
        {
            wave = Math.Max(1, wave);
            int w = (wave + 1) / 2;                               // 段階：2 ウェーブで 1 段階
            var s = new WaveSpec { Damage = Math.Min(2f, 1f + 0.005f * (wave - 1)) };
            bool fifth = wave % 10 == 0;
            if (w <= 10)
            {
                int total = 4 + w / 2;                            // 4, 5, 5, 6, 6, 7, 7, 8, 8, 9
                s.Gallery = Math.Max(0, w - 5);
                s.Office = total - s.Gallery;
            }
            else if (w <= 20)
            {
                int total = 9 + (w - 11) / 4;                     // 9〜11
                float t = (w - 10) / 10f;                         // 0.1〜1.0：オフィス → 美術館 → 特殊部隊
                s.Special = (int)Math.Round(total * Math.Max(0f, t * 1.2f - 0.4f));
                s.Office = (int)Math.Round(total * Math.Max(0f, 0.5f - t));
                s.Gallery = Math.Max(0, total - s.Special - s.Office);
            }
            else if (w <= 30) { s.Special = 12; s.Boss = wave >= 50 ? 1 : 0; }
            else if (w <= 40) { s.Special = 12; s.Boss = fifth ? 2 : 1; }
            else if (w <= 50) { s.Special = 12; s.Boss = fifth ? 3 : 2; }
            else { s.Special = 12; s.Boss = Math.Min(6, (fifth ? 3 : 2) + (w - 41) / 10); }
            s.MaxAlive = MaxAlive;
            return s;
        }

        // 出す順番：弱い敵から混ぜて、ボスは最後に
        public static List<Kind> Queue(WaveSpec s, Random rng)
        {
            var list = new List<Kind>();
            for (int i = 0; i < s.Office; i++) list.Add(Kind.Office);
            for (int i = 0; i < s.Gallery; i++) list.Add(Kind.Gallery);
            for (int i = 0; i < s.Special; i++) list.Add(Kind.Special);
            for (int i = list.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
            for (int i = 0; i < s.Boss; i++) list.Add(Kind.Boss);
            return list;
        }

        // 武器箱のレアリティ：10 ウェーブごとに上がる基本のレアリティ（0-9 コモン、10 アンコモン … 40 以上はレジェンダリー）が
        //   ほぼ確定で、ごくたまに上のレアリティが出る。
        //   「そのレアリティ以上」の確率は、そのレアリティが確定するウェーブまでの残りだけで決める（UpChance）。
        //   ウェーブが進むと残りが減るので、どのレアリティ以上の確率も下がらない（9 より 10 の方がエリートが出にくい、のような逆転がない）。
        //   一番遠いレジェンダリー（ウェーブ 0 で残り 40）でも 0.1% 少しは出る。
        //   確定の手前の数ウェーブで少しずつ上がるので、1 ウェーブでどのレアリティの確率も 20% より大きくは変わらない
        //   例：1 → コモン 96%、5 → コモン 77%、9 → コモン 20%・アンコモン 77%、10 → アンコモン 96.5%（コモンは出ない）
        public static Tier MinTier(int cleared, bool hard = false) => (Tier)Math.Min(4, cleared / TierStep(hard));
        static int TierStep(bool hard) => hard ? HardLegendaryWave / 4 : CrateDollarsFrom / 4;

        // 武器箱のドル：40 ウェーブ以降（レジェンダリー確定）は、40 で $4,000、そこから 1 ウェーブごとに $4,000 増える
        public const int CrateDollarsFrom = 40;
        public const float CrateDollarsPerWave = 4000f;
        public static float CrateDollars(int cleared, bool hard = false)
        {
            int from = hard ? HardLegendaryWave : CrateDollarsFrom;
            return cleared < from ? 0f : CrateDollarsPerWave * (cleared - from + 1);
        }
        static readonly int[] UpLeft = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 19, 29, 39 };                                       // 確定まで残り何ウェーブか
        static readonly double[] UpProb = { 0.80, 0.62, 0.46, 0.33, 0.23, 0.155, 0.10, 0.065, 0.04, 0.01, 0.004, 0.0015 };  // その時の「そのレアリティ以上」の確率（間は対数で補間）

        static double UpChance(int left)
        {
            if (left <= 0) return 1.0;
            if (left <= UpLeft[0]) return UpProb[0];
            int i = 0;
            while (i < UpLeft.Length - 2 && left > UpLeft[i + 1]) i++;   // 残りが表より多ければ最後の区間を延ばす
            double t = (double)(left - UpLeft[i]) / (UpLeft[i + 1] - UpLeft[i]);
            return Math.Exp(Math.Log(UpProb[i]) + t * (Math.Log(UpProb[i + 1]) - Math.Log(UpProb[i])));
        }

        // 各レアリティ（コモン〜レジェンダリー）の確率
        public static float[] CrateOdds(int cleared, bool hard = false)
        {
            var atLeast = new double[6];
            atLeast[0] = 1.0;
            for (int t = 1; t <= 4; t++) atLeast[t] = UpChance(TierStep(hard) * t - cleared);
            var odds = new float[5];
            for (int t = 0; t < 5; t++) odds[t] = (float)(atLeast[t] - atLeast[t + 1]);
            return odds;
        }

        // ---------- 縛りの報酬（内部の計算だけ。端末には「装備を絞るほど良い報酬が出やすい」とだけ書く） ----------
        // 持ち込んだ装備が少ない・レアリティが低いほどポイントが増え、武器箱はそのぶん先のウェーブの報酬（今の報酬の表のまま）になる
        //   武器（大型・小型）：なし 10、コモン 8、アンコモン 6、レア 4、エリート 2、レジェンダリー 0（レアリティが分からない武器は 0）
        //   防具（ヘルメット・ベスト）：なし 5、コモン 4、アンコモン 3、レア 2、エリート 1、レジェンダリー 0
        //   コモンの小型武器だけ（大型武器・防具なし）= 28 ポイント = ウェーブ +20（上限）。全部レジェンダリー = 0 ポイント = ±0
        //   途中でより良い装備に替えたときは、そのときのポイントで（ラン中のいちばん低いポイントを使う）
        //   ハードはレジェンダリー確定が半分の 20 ウェーブなので、足すウェーブも半分（上限 +10）
        public const int HandicapMaxWaves = 20;
        public const int HandicapMaxWavesHard = 10;
        public const int HandicapFullPoints = 28;
        public static int WeaponPoints(Tier? tier, bool has) => !has ? 10 : tier == null ? 0 : 2 * (4 - Math.Clamp((int)tier.Value, 0, 4));
        public static int ArmorPoints(int? rarity) => rarity == null ? 5 : 4 - Math.Clamp(rarity.Value, 0, 4);
        public static int HandicapWaves(int points, bool hard = false)
        {
            int max = hard ? HandicapMaxWavesHard : HandicapMaxWaves;
            return points <= 0 ? 0 : Math.Min(max, (int)Math.Round(points * (double)max / HandicapFullPoints, MidpointRounding.AwayFromZero));
        }

        public static Tier CrateTier(int cleared, Random rng, bool hard = false)
        {
            var odds = CrateOdds(cleared, hard);
            double r = rng.NextDouble();
            for (int i = 0; i < odds.Length; i++)
            {
                if (r < odds[i]) return (Tier)i;
                r -= odds[i];
            }
            return MinTier(cleared, hard);
        }
    }
}
