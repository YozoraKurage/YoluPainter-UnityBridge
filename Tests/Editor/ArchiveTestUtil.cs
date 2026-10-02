using System;
using System.Linq;
using NUnit.Framework;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>今の版のネイティブアーカイブ（1 レイヤーの文書）から、古い版の並びを作る。古い版の読み込みの試験用。</summary>
    internal static class ArchiveTestUtil
    {
        /// <summary>targetVersion: 5 なら版 6 で足した親グループ ID（16 バイト）を抜く。4 以下ならクリッピングの 1 バイト（版 5）も抜く。
        /// 版 3 以下で無くなる種類・Fill・調整の並びは呼び出し側が持たない文書で使うこと。</summary>
        public static byte[] AsVersion(byte[] current, string layerName, int targetVersion)
        {
            Assert.That(BitConverter.ToInt32(current, 8), Is.EqualTo(6), "the helper converts from version 6");
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
