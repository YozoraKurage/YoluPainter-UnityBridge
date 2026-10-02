using System;
using System.Linq;
using NUnit.Framework;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>今の版のネイティブアーカイブ（1 レイヤーの文書）から、古い版の並びを作る。古い版の読み込みの試験用。</summary>
    internal static class ArchiveTestUtil
    {
        /// <summary>版 7 で文書の頭（タイルの大きさの直後）に足した Normal の出力設定のバイト数。</summary>
        public const int NormalSettingsBytes = 4 + 1 + 8 + 4 + 4;
        /// <summary>Normal の出力設定の位置（magic, 版, 文書 ID, 幅・高さ・タイルの後）。</summary>
        public const int NormalSettingsOffset = 8 + 4 + 16 + 12;

        /// <summary>targetVersion: 6 なら版 7 で足した Normal の出力設定（21 バイト）を抜く。5 なら版 6 で足した親グループ ID（16 バイト）も
        /// 抜く。4 以下ならクリッピングの 1 バイト（版 5）も抜く。版 3 以下で無くなる種類・Fill・調整の並びは呼び出し側が持たない文書で使うこと。</summary>
        public static byte[] AsVersion(byte[] current, string layerName, int targetVersion)
        {
            Assert.That(BitConverter.ToInt32(current, 8), Is.EqualTo(7), "the helper converts from version 7");
            current = current.Take(NormalSettingsOffset).Concat(current.Skip(NormalSettingsOffset + NormalSettingsBytes)).ToArray();
            if (targetVersion == 6) { BitConverter.GetBytes(6).CopyTo(current, 8); return current; }
            // magic, 版, 文書 ID, 幅・高さ・タイル, レイヤー数, レイヤー ID, 名前, 表示, 不透明度, 合成モード
            int clippingByte = 8 + 4 + 16 + 12 + 4 + 16 + 4 + System.Text.Encoding.UTF8.GetByteCount(layerName) + 1 + 8 + 4;
            int parent = clippingByte + 1 + 4;
            Assert.That(current.Skip(parent).Take(16), Is.All.EqualTo((byte)0), "a top-level layer has an empty parent id");
            var bytes = current.Take(parent).Concat(current.Skip(parent + 16)).ToArray();
            if (targetVersion <= 4)
            {
                Assert.That(bytes[clippingByte], Is.EqualTo(0));
                bytes = bytes.Take(clippingByte).Concat(bytes.Skip(clippingByte + 1)).ToArray();
            }
            BitConverter.GetBytes(targetVersion).CopyTo(bytes, 8);
            return bytes;
        }
    }
}
