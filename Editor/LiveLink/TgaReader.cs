using System;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// TGA の画素を、圧縮の無い本当の値で読む（Live Link が元の絵を原本のファイルから読むため）。読むのは真色の 24 ビット・32 ビット
    /// （無圧縮と RLE）だけ。32 ビットはアルファの桁が 8 のものだけ（桁が 0 の TGA は、Unity の取り込みがアルファをどう扱うか確かめられない
    /// ので読まない）。カラーマップ・グレー・16 ビット・読めない・壊れたファイルは null を返し、呼び手は Unity の取り込み済みの絵で読む。
    /// 画素は straight RGBA8、行は下から（Unity の並び）。辺は <paramref name="maxSide"/> まで。ファイルには書かない。
    /// </summary>
    internal static class TgaReader
    {
        public static byte[] Decode(byte[] data, int maxSide, out int width, out int height)
        {
            width = height = 0;
            if (data == null || data.Length < 18) return null;
            int idLength = data[0], mapType = data[1], imageType = data[2];
            int mapLength = data[5] | data[6] << 8, mapEntryBits = data[7];
            int w = data[12] | data[13] << 8, h = data[14] | data[15] << 8, depth = data[16], descriptor = data[17];
            bool rle = imageType == 10;
            if (imageType != 2 && imageType != 10) return null;
            if (depth != 24 && depth != 32) return null;
            if (depth == 32 && (descriptor & 0x0f) != 8) return null;
            if (w < 1 || h < 1 || w > maxSide || h > maxSide) return null;
            bool topFirst = (descriptor & 0x20) != 0, rightFirst = (descriptor & 0x10) != 0;
            if (rightFirst) return null;
            // カラーマップが付いていても、真色の画素の前に読み飛ばすだけ
            long mapBytes = mapType == 1 ? (long)mapLength * ((mapEntryBits + 7) / 8) : 0;
            long at = 18L + idLength + mapBytes;
            int bytesPerPixel = depth / 8;
            long count = (long)w * h;
            var rgba = new byte[count * 4];
            long pixel = 0;
            try
            {
                if (!rle)
                {
                    if (at + count * bytesPerPixel > data.Length) return null;
                    for (; pixel < count; pixel++, at += bytesPerPixel) Put(rgba, pixel, data, at, bytesPerPixel, w, h, topFirst);
                }
                else
                {
                    while (pixel < count)
                    {
                        if (at >= data.Length) return null;
                        int packet = data[at++]; int run = (packet & 0x7f) + 1;
                        if (pixel + run > count) return null;
                        if ((packet & 0x80) != 0)
                        {
                            if (at + bytesPerPixel > data.Length) return null;
                            for (int i = 0; i < run; i++) Put(rgba, pixel++, data, at, bytesPerPixel, w, h, topFirst);
                            at += bytesPerPixel;
                        }
                        else
                        {
                            if (at + (long)run * bytesPerPixel > data.Length) return null;
                            for (int i = 0; i < run; i++, at += bytesPerPixel) Put(rgba, pixel++, data, at, bytesPerPixel, w, h, topFirst);
                        }
                    }
                }
            }
            catch (IndexOutOfRangeException) { return null; }
            width = w; height = h;
            return rgba;
        }

        /// <summary>ファイルの順の画素 <paramref name="index"/> を、行を下からの並びへ置く（BGR[A] → RGBA）。</summary>
        static void Put(byte[] rgba, long index, byte[] data, long at, int bytesPerPixel, int w, int h, bool topFirst)
        {
            long fileRow = index / w, x = index % w;
            long row = topFirst ? h - 1 - fileRow : fileRow;
            long o = (row * w + x) * 4;
            rgba[o] = data[at + 2]; rgba[o + 1] = data[at + 1]; rgba[o + 2] = data[at];
            rgba[o + 3] = bytesPerPixel == 4 ? data[at + 3] : (byte)255;
        }
    }
}
