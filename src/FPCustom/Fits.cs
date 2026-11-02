using System.Collections.Generic;

using FPModOption;

namespace FPCustom
{
    // 武器ごとの銃口の位置（武器の根元の座標、m）。autotest -Dev -Scenario custmuzzle で測った値
    //   Tip  = 銃口の先端の前後位置（最初から付いている部品は外して測る）
    //   Axis = 銃身の中心の高さ
    //   Back = 銃口の部品（フラッシュハイダー等）にかぶせるために下げる長さ。ハンドガンは 0
    //   X    = 銃身の中心の左右（0 でない武器だけ。custmuzfit の正面・上からの画で測った値）
    public static class Fits
    {
        public readonly struct Muzzle
        {
            public readonly float Tip, Axis, Back, X;
            public Muzzle(float tip, float axis, float back, float x = 0f) { Tip = tip; Axis = axis; Back = back; X = x; }
            public UnityEngine.Vector3 At(float z) => new(X, Axis, z);
        }

        // 銃口の部品を付けるときに外す、武器の銃口の部品（Mount）。付ける位置は Tip - Back ＝ その部品の後ろの端
        public static readonly Dictionary<int, string> Devices = new()
        {
            [11] = "1/muzzle_aks74u_izhmash_std_545x39_LOD0_0",
            [18] = "saiga12_custom_parts/saiga12_muzzle",
            [15] = "uploads_files_4287500_ar15_default/ar15_default_muzzle",   // Tactic R-15：ハイダー z 0.303〜0.362（custmuz2）
            [16] = "sk_ace32/Geo/Muzzle",                                       // ACE-31：ハイダー z 0.459〜0.509
        };

        public static readonly Dictionary<int, Muzzle> Muzzles = new()
        {
            [0] = new(0.5107f, 0.0009f, 0.0840f),    // AK-74（ブレーキを除いた銃身の先 0.4297 の 3mm 手前に付ける：custmuzfit）
            [1] = new(0.1566f, 0.0605f, 0f),         // SP-229（高さは正面の画の銃口の穴：custbore。前後はスライドの前の面：custmuz2 の FACE。0.1576 では 1mm 空いた）
            [2] = new(0.4653f, 0.0010f, 0.0025f, -0.0013f),   // AK-12（銃身の中心は 1.3mm 左：custmuzfit。銃身の先の丸みの分 2.5mm かぶせる）
            [3] = new(0.4991f, 0.0077f, 0.0710f, 0.0007f),   // QBZ 191（ハイダーを除いた銃身の中心：custmuzfit。0.0046 では 3mm 低かった）
            [4] = new(0.4471f, 0.0031f, 0f),         // AKMS
            [5] = new(0.2265f, 0.0624f, 0f),         // Eagle .50（同上）
            [6] = new(0.1799f, 0.0512f, 0f),         // TT（同上）
            [7] = new(0.1901f, 0.0615f, 0f),         // Model 92（同上）
            [8] = new(0.1559f, 0.0562f, 0f),         // GR-21（同上）
            [9] = new(0.1705f, 0.0693f, 0f, 0.0010f),   // SP-320（同上。銃口の穴は 1mm 左：custmuzfit の正面の画）
            [10] = new(0.5661f, 0.0085f, 0.0490f),   // M16
            [11] = new(0.3239f, 0.0017f, 0.0689f),   // AKSU（ブースター z 0.222〜0.331 を外して、銃身の先 0.2580 の 3mm 手前に付ける。後ろの端 0.222 では銃身が部品のくびれから透けた。高さはねじの中心：custmuzfit）
            [12] = new(0.4915f, 0.0059f, 0.0253f),   // AKM（銃身とひとつのスラントブレーキ z 0.4662〜を MuzzleCut で除いて付ける。高さは銃身の中心：custmuzfit）
            [13] = new(0.1499f, 0.0414f, 0f),        // G-18C Auto（同上）
            [15] = new(0.3616f, 0.0124f, 0.0590f),   // Tactic R-15
            [16] = new(0.5089f, 0.0091f, 0.0428f),   // ACE-31（ハイダーと銃身の先のリング（MuzzleCut）を外して、銃身の先 0.4691 の 3mm 手前に付ける：custmuzfit）
            [17] = new(0.5829f, -0.0012f, 0.0288f),  // Swiss 751（銃身とひとつのスリットの付いた部品 z 0.5541〜を MuzzleCut で除いて付ける）
            [18] = new(0.4879f, 0.0062f, 0.0774f),   // Hunter-32（ハイダー z 0.411〜0.488 を外して付ける。銃身の中心 y +0.0062）
            [19] = new(0.2952f, 0.0058f, 0f),        // Hunter-12
            [20] = new(0.5372f, 0.0026f, 0.0647f),   // Type 89（細長いハイダー z 0.4725〜0.537：custmuz2 の太さ。銃身とひとつの動くメッシュ。MuzzleCut で除く。高さは横の絵で銃身の中心に合わせた）
            [21] = new(0.4044f, 0.0082f, 0.0092f),   // ER-15S（銃身の先の細いねじ 9mm を部品の中に入れる）
            [22] = new(0.1555f, 0.0522f, 0f, 0.0017f),   // J-941（同上。銃口の穴は 1.7mm 左・0.5mm 上：custmuzfit の正面の画）
            [23] = new(0.1189f, 0.0458f, 0f),        // Compact CV-1
            [24] = new(0.1532f, 0.0454f, 0f),        // GSH
            [25] = new(0.1599f, 0.0536f, 0f),        // 1911 Works（同上）
            [27] = new(0.3584f, -0.0362f, 0.0035f, -0.0034f),   // MPS-5（銃身は 3.4mm 右：custmuzfit。銃身の先の丸みの分 3.5mm かぶせる）
            [28] = new(0.3584f, -0.0361f, 0.0035f, -0.0034f),   // Viper M5A3（同上）
            [29] = new(0.3203f, -0.0356f, 0f, -0.0030f),   // "Beater" MPS-5（-0.0278 は上のコッキングチューブだった：custmuzfit の正面の画）
            [14] = new(0.4888f, 0.0089f, 0f),        // VAL（銃身そのものがサプレッサー。銃口の部品は付けないが、レーザーの位置合わせに使う）
        };

        // 銃口の部品を付ける所の銃身の太さ（m。custmuzfit の一番細い所の高さ）。銃口の部品の大きさを合わせる（AssetParts.RifleScale）
        public static readonly Dictionary<int, float> Barrel = new()
        {
            [0] = 0.0161f, [2] = 0.0139f, [3] = 0.0175f, [4] = 0.0148f, [10] = 0.0164f, [11] = 0.0152f, [12] = 0.0159f, [15] = 0.0142f,
            [16] = 0.0132f, [17] = 0.0159f, [18] = 0.0258f, [19] = 0.0214f, [20] = 0.0134f, [21] = 0.0106f, [27] = 0.0147f, [28] = 0.0147f,
        };

        // レーザーの中心（武器の根元の座標、m）。autotest -Dev -Scenario custlaser で測った値
        //   メイン武器：グリップ枠の 5cm 前の、銃の右側面の外（+ 部品の半分の幅 0.0204）
        //   ハンドガン：銃口の下のレールの面（+ 4.5mm 食い込ませる = Model 92 の最初の位置）の下（- 部品の半分の高さ 0.0211）。
        //              後ろの端は用心金の 3mm 前、または前の端が銃口の 7mm 後ろの、後ろでない方（+ 部品の半分の長さ 0.0383）
        public static readonly Dictionary<int, UnityEngine.Vector3> Lasers = new()
        {
            [0] = new(0.0198f + 0.0204f, 0.0009f, 0.2120f),   // AK-74
            [1] = new(0f, 0.0245f + 0.0045f - 0.0211f, 0.0966f + 0.0383f),   // SP-229
            [2] = new(0.0230f + 0.0204f, 0.0170f, 0.2059f),   // AK-12
            [3] = new(0.0338f + 0.0204f, 0.0346f, 0.2059f),   // QBZ 191
            [4] = new(0.0154f + 0.0204f, 0.0071f, 0.2059f),   // AKMS
            [5] = new(0f, 0.0208f + 0.0045f - 0.0211f, 0.1429f + 0.0383f),   // Eagle .50
            [6] = new(0f, 0.0261f + 0.0045f - 0.0211f, 0.0963f + 0.0383f),   // TT
            [7] = new(0f, 0.0284f + 0.0045f - 0.0211f, 0.1065f + 0.0383f),   // Model 92
            [8] = new(0f, 0.0256f + 0.0045f - 0.0211f, 0.0909f + 0.0383f),   // GR-21
            [9] = new(0f, 0.0267f + 0.0045f - 0.0211f, 0.1015f + 0.0383f),   // SP-320
            [10] = new(0.0134f + 0.0204f, 0.0085f, 0.2560f),   // M16
            [11] = new(0.0264f + 0.0204f, -0.0026f, 0.2059f),   // AKSU
            [12] = new(0.0172f + 0.0204f, 0.0174f, 0.2059f),   // AKM
            [13] = new(0f, 0.0135f + 0.0045f - 0.0211f, 0.0909f + 0.0383f),   // G-18C Auto
            [14] = new(0.0236f + 0.0204f, 0.0089f, 0.2059f),   // VAL
            [15] = new(0.0202f + 0.0204f, 0.0124f, 0.2522f),   // Tactic R-15
            [16] = new(0.0298f + 0.0204f, 0.0151f, 0.2120f),   // ACE-31
            [17] = new(0.0208f + 0.0204f, -0.0012f, 0.2120f),   // Swiss 751
            [18] = new(0.0202f + 0.0204f, 0.0038f, 0.2059f),   // Hunter-32
            [19] = new(0.0202f + 0.0204f, 0.0058f, 0.2120f),   // Hunter-12
            [20] = new(0.0140f + 0.0204f, 0.0165f, 0.2560f),   // Type 89
            [21] = new(0.0260f + 0.0204f, 0.0082f, 0.2560f),   // ER-15S
            [22] = new(0f, 0.0176f + 0.0045f - 0.0211f, 0.0945f + 0.0383f),   // J-941
            [23] = new(0f, 0.0143f + 0.0045f - 0.0211f, 0.0789f + 0.0383f),   // Compact CV-1
            [24] = new(0f, 0.0182f + 0.0045f - 0.0211f, 0.1002f + 0.0383f),   // GSH
            [25] = new(0f, 0.0229f + 0.0045f - 0.0211f, 0.0763f + 0.0383f),   // 1911 Works
            [27] = new(0.0110f + 0.0204f, -0.0362f, 0.2120f),   // MPS-5
            [28] = new(0.0236f + 0.0204f, -0.0361f, 0.2656f),   // Viper M5A3
            [29] = new(0.0134f + 0.0204f, -0.0278f, 0.2120f),   // "Beater" MPS-5 
        };

        // グリップなしで構えたときの左手首（Left_Hand）の前後位置（武器の根元の座標）。custprobe で測った値。持ち方の設定ごとに同じ
        public static float EmptyHandZ(int id) => id switch
        {
            10 or 15 or 20 or 21 => 0.0797f,   // M16・R-15・Type 89・ER-15S（Mk18 の持ち方）
            27 or 28 or 29 => 0.1218f,         // MPS-5・Viper・Beater（MP5 の持ち方）
            _ => 0.0876f,                      // AK の持ち方
        };
        // 左手の指先の前後位置（手首 + 12.5cm）。レーザーはこれより前に付ける（ホイールの構えで手に隠れないように。ゲームのレーザーと同じ）
        public static float HandFrontZ(int id) => EmptyHandZ(id) + 0.125f;

        // バーティカルグリップを前後にずらす量（m、武器の根元の座標。マイナス＝後ろ）。custgrip で、前が銃からはみ出す武器だけ
        public static readonly Dictionary<int, float> GripShiftZ = new()
        {
            [21] = -0.0119f,   // ER-15S
        };
    }
}
