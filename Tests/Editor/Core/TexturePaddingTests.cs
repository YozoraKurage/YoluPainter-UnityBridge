using System;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>書き出しのパディング（TexturePadding）: UV の三角形が少しでも重なるテクセルの印と、その外の塗り広げ。</summary>
    public sealed class TexturePaddingTests
    {
        static bool[] Cover(int w, int h, params (double, double, double, double, double, double)[] triangles) => TexturePadding.Coverage(w, h, triangles);

        [Test] public void CoverageMarksEveryTexelATriangleTouches()
        {
            // テクセル (2, 2) の中だけの小さな三角形 → そのテクセルだけ
            var c = Cover(8, 8, (2.2, 2.2, 2.8, 2.3, 2.5, 2.9));
            Assert.That(Enumerable.Range(0, 64).Where(i => c[i]), Is.EqualTo(new[] { 2 * 8 + 2 }));
            // 4 つのテクセルの境目の点を覆う三角形 → 4 つとも（角に触れる分も保守的に入る）
            c = Cover(8, 8, (3.9, 3.9, 4.1, 3.9, 4.0, 4.1));
            Assert.That(Enumerable.Range(0, 64).Where(i => c[i]), Is.EquivalentTo(new[] { 3 * 8 + 3, 3 * 8 + 4, 4 * 8 + 3, 4 * 8 + 4 }));
            // 斜めの細い三角形: 外接矩形の隅のテクセルは入らない（分離軸で外れる）
            c = Cover(8, 8, (0.5, 0.5, 7.5, 7.5, 7.5, 7.0));
            Assert.That(c[0 * 8 + 0], Is.True); Assert.That(c[7 * 8 + 7], Is.True);
            Assert.That(c[7 * 8 + 0], Is.False, "the far corner of the bounding box"); Assert.That(c[0 * 8 + 7], Is.False);
            // 線分につぶれた三角形も、通るテクセルを覆う。画像の外は切る。NaN は飛ばす
            c = Cover(8, 8, (0.5, 4.5, 6.5, 4.5, 6.5, 4.5), (-5, -5, -1, -5, -3, -1), (double.NaN, 0, 1, 1, 0, 1));
            Assert.That(Enumerable.Range(0, 64).Where(i => c[i]), Is.EqualTo(Enumerable.Range(0, 7).Select(x => 4 * 8 + x)));
        }

        static byte[] Image(int w, int h, Func<int, int, (byte, byte, byte, byte)> at)
        {
            var image = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var (r, g, b, a) = at(x, y); int o = (y * w + x) * 4; image[o] = r; image[o + 1] = g; image[o + 2] = b; image[o + 3] = a; }
            return image;
        }
        static (byte, byte, byte, byte) Px(byte[] image, int w, int x, int y) { int o = (y * w + x) * 4; return (image[o], image[o + 1], image[o + 2], image[o + 3]); }

        [Test] public void DilationKeepsTheCoveredTexelsAndGrowsOneRingPerStep()
        {
            const int w = 9, h = 9;
            var keep = new bool[w * h]; keep[4 * w + 4] = true;
            var source = Image(w, h, (x, y) => x == 4 && y == 4 ? ((byte)200, (byte)100, (byte)50, (byte)255) : ((byte)1, (byte)2, (byte)3, (byte)0));
            var two = TexturePadding.Dilate(source, w, h, keep, 2);
            Assert.That(source[(4 * w + 4) * 4], Is.EqualTo(200), "the input is not changed");
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int ring = Math.Max(Math.Abs(x - 4), Math.Abs(y - 4));
                    var expected = ring <= 2 ? ((byte)200, (byte)100, (byte)50, (byte)255) : ((byte)1, (byte)2, (byte)3, (byte)0);
                    Assert.That(Px(two, w, x, y), Is.EqualTo(expected), "texel " + x + "," + y + " (ring " + ring + ")");
                }
            var all = TexturePadding.Dilate(source, w, h, keep, TexturePadding.Fill);
            Assert.That(Enumerable.Range(0, w * h).All(i => all[i * 4] == 200 && all[i * 4 + 3] == 255), Is.True, "Fill reaches every texel");
            Assert.That(TexturePadding.Dilate(source, w, h, keep, 0), Is.EqualTo(source), "0 texels: no padding");
            Assert.That(TexturePadding.Dilate(source, w, h, new bool[w * h], TexturePadding.Fill), Is.EqualTo(source), "nothing covered: nothing to grow from");
        }

        /// <summary>色はアルファで重みを付けた平均: 不透明な赤と、透明な黒の隣では、赤のまま半分のアルファ（透明な色が黒くにじまない）。全部が透明なら色の平均。</summary>
        [Test] public void DilationWeighsColoursByAlpha()
        {
            const int w = 3, h = 1;
            var keep = new[] { true, false, true };
            var image = Image(w, h, (x, y) => x == 0 ? ((byte)255, (byte)0, (byte)0, (byte)255) : x == 2 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)9, (byte)9, (byte)9, (byte)9));
            Assert.That(Px(TexturePadding.Dilate(image, w, h, keep, 1), w, 1, 0), Is.EqualTo(((byte)255, (byte)0, (byte)0, (byte)128)));
            var clear = Image(w, h, (x, y) => x == 0 ? ((byte)10, (byte)20, (byte)30, (byte)0) : ((byte)30, (byte)40, (byte)50, (byte)0));
            Assert.That(Px(TexturePadding.Dilate(clear, w, h, keep, 1), w, 1, 0), Is.EqualTo(((byte)20, (byte)30, (byte)40, (byte)0)));
        }

        /// <summary>同じ段の中では前の段の結果だけを読むので、左右対称の入力からは左右対称の結果になる（処理の順に依らない）。</summary>
        [Test] public void DilationDoesNotDependOnTheScanOrder()
        {
            const int w = 16, h = 5;
            var keep = new bool[w * h]; keep[2 * w + 0] = true; keep[2 * w + 15] = true;
            var image = Image(w, h, (x, y) => x == 0 ? ((byte)255, (byte)0, (byte)0, (byte)255) : x == 15 ? ((byte)0, (byte)0, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));
            var output = TexturePadding.Dilate(image, w, h, keep, TexturePadding.Fill);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var (r, g, b, a) = Px(output, w, x, y); var (r2, g2, b2, a2) = Px(output, w, w - 1 - x, y);
                    Assert.That((r, g, b, a), Is.EqualTo((b2, g2, r2, a2)), x + "," + y + " mirrors " + (w - 1 - x));
                }
        }

        [Test] public void BadInputsAreRefused()
        {
            Assert.Throws<ArgumentException>(() => TexturePadding.Dilate(new byte[12], 2, 2, new bool[4], 1));
            Assert.Throws<ArgumentException>(() => TexturePadding.Dilate(new byte[16], 2, 2, new bool[3], 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => TexturePadding.Dilate(new byte[16], 2, 2, new bool[4], -2));
            Assert.Throws<InvalidOperationException>(() => TexturePadding.Dilate(new byte[16], 2, 2, new bool[4], 1, maxWorkingBytes: 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => TexturePadding.Coverage(0, 4, new (double, double, double, double, double, double)[0]));
        }
    }
}
