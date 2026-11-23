using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using FPModOption;

namespace FPCustom
{
    // KK Models（有料アセット）の模型：暗号化して DLL に埋め込んである（tools\pack_kk.ps1）。起動時にメモリの中で戻して ModelLoader に渡す。
    //   模型・画像のファイルはどこにも書き出さない。鍵（KkKey.private.cs）と中身は配布のソース・GitHub に入れない
    //   → 鍵の無いビルド（公開のソースから作った物）ではメイン武器のストックが出ない（AssetParts.Has が false）
    static partial class KkPack
    {
        const string Resource = "FPCustom.kkpack.bin";
        public static int Loaded { get; private set; }

        public static void Load()
        {
#if KKPACK
            try
            {
                using var s = typeof(KkPack).Assembly.GetManifestResourceStream(Resource);
                if (s == null) { Main.Log("[kkpack] not embedded"); return; }
                var all = new byte[s.Length]; s.Read(all, 0, all.Length);
                byte[] packed;
                using (var aes = Aes.Create())
                {
                    aes.Key = Key(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    var iv = new byte[16]; Array.Copy(all, iv, 16); aes.IV = iv;
                    packed = aes.CreateDecryptor().TransformFinalBlock(all, 16, all.Length - 16);
                }
                using var ds = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
                using var br = new BinaryReader(ds, System.Text.Encoding.UTF8);
                if (new string(br.ReadChars(4)) != "KKP1") { Main.Log("[kkpack] bad data"); return; }
                int n = br.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string name = br.ReadString(); int len = br.ReadInt32();
                    ModelLoader.AddPacked(name, br.ReadBytes(len));
                }
                Loaded = n;
                Main.Log($"[kkpack] {n} files in memory");
            }
            catch (Exception e) { Main.Log("[kkpack] failed: " + e.Message); }
#else
            Main.Log("[kkpack] this build has no KK models (main weapon stocks are off)");
#endif
        }
    }
}
