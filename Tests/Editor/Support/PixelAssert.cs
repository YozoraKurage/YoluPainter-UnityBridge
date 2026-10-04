using System;
using NUnit.Framework;

namespace Yozolab.YoluPainter.Tests
{
    internal static class PixelAssert
    {
        /// <summary>バイト単位で同じか（NUnit の Is.EqualTo は大きな配列で 1 要素ずつ箱に入れて比べ、1100×700 の比較 1 回に数秒かかる）。
        /// 違えば最初の画素を示す。</summary>
        internal static void SameBytes(byte[] expected, byte[] actual, string context)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), context + ": length");
            if (expected.AsSpan().SequenceEqual(actual)) return;
            int at = 0; while (expected[at] == actual[at]) at++;
            Assert.Fail($"{context}: first difference at pixel {at / 4} channel {at % 4} (expected {expected[at]}, got {actual[at]})");
        }
    }
}
