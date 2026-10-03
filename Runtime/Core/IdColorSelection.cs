using System;
using System.Collections.Generic;
using System.Globalization;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>
    /// ID マップの色（0xRRGGBB、8 bit）とその比べ方。ID マップは 16 bit で持つが、色は 8 bit の値 × 257 で焼くので、8 bit に丸めて
    /// （(v × 255 + 32767) ÷ 65535、表示の重ねと同じ丸め）比べる。許容の幅はマジックワンドと同じく、チャンネルごとの差の最大
    /// （0〜255。0 は同じ色だけ）。焼いていない（Empty）テクセルには色が無い。
    /// </summary>
    public static class IdMapColors
    {
        public const int MaxTolerance = 255;
        /// <summary>既定の許容の幅。<see cref="IdPalette.MinimumSeparation"/> は 4080 部品まで 17 以上なので、焼いた ID の色を 1 つずつ見分ける。</summary>
        public const int DefaultTolerance = 8;

        /// <summary>テクセル (x, y)（左下原点）の色。範囲の外か Empty なら false。</summary>
        public static bool TryGet(BakedMeshMap map, int x, int y, out int rgb)
        {
            RequireIdMap(map);
            rgb = 0;
            if ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height || map.Coverage[y * map.Width + x] == (byte)MeshTexelCoverage.Empty) return false;
            rgb = Rgb(map.Data, y * map.Width + x);
            return true;
        }
        /// <summary>UV（0〜1、(0, 0) が左下）の点のテクセルの色。</summary>
        public static bool TryGetAtUv(BakedMeshMap map, double u, double v, out int rgb)
        {
            RequireIdMap(map);
            rgb = 0;
            if (!(u >= 0 && u <= 1 && v >= 0 && v <= 1)) return false; // NaN も
            int x = Math.Min((int)Math.Floor(u * map.Width), map.Width - 1), y = Math.Min((int)Math.Floor(v * map.Height), map.Height - 1);
            return TryGet(map, x, y, out rgb);
        }

        internal static int Rgb(ushort[] data, int texel)
        {
            int o = texel * 3;
            return (data[o] * 255 + 32767) / 65535 << 16 | (data[o + 1] * 255 + 32767) / 65535 << 8 | (data[o + 2] * 255 + 32767) / 65535;
        }
        /// <summary>a と b のチャンネルごとの差の最大が tolerance 以下か。</summary>
        public static bool Near(int a, int b, int tolerance)
            => Math.Abs((a >> 16 & 255) - (b >> 16 & 255)) <= tolerance && Math.Abs((a >> 8 & 255) - (b >> 8 & 255)) <= tolerance && Math.Abs((a & 255) - (b & 255)) <= tolerance;
        /// <summary>"#RRGGBB"。</summary>
        public static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

        /// <summary>ID マップか（種類）を確かめる。違えば ArgumentException。</summary>
        public static void RequireIdMap(BakedMeshMap map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (map.Kind != MeshMapKind.Id) throw new ArgumentException("A colour selection reads the ID map, not the " + map.Kind + " map.", nameof(map));
        }
        internal static void RequireColors(IReadOnlyList<int> colors, int tolerance)
        {
            if (colors == null) throw new ArgumentNullException(nameof(colors));
            foreach (int c in colors) if (c < 0 || c > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(colors), "ID colours are 0xRRGGBB (0–0xFFFFFF).");
            if (tolerance < 0 || tolerance > MaxTolerance) throw new ArgumentOutOfRangeException(nameof(tolerance), "The tolerance must be 0–" + MaxTolerance + ".");
        }
    }

    public sealed partial class SelectionMask
    {
        /// <summary>
        /// ID マップの色で選ぶ（Substance Painter の Color Selection）: ID マップで colors のどれかから tolerance 以内の色のテクセル（Empty で
        /// ないもの。余白も島のテクセルの写しなので入る）を選ぶ（0 か 255、ふちをぼかさない）。ID マップはドキュメントと同じ大きさでなければ
        /// 断る（テクセル = 画素）。タイルごとに並列に求める（読むだけ）。
        /// </summary>
        public static SelectionMask FromIdColors(PaintDocument document, BakedMeshMap idMap, IReadOnlyList<int> colors, int tolerance)
        {
            Require(document); IdMapColors.RequireIdMap(idMap); IdMapColors.RequireColors(colors, tolerance);
            if (idMap.Width != document.Width || idMap.Height != document.Height)
                throw new ArgumentException("The ID map is " + idMap.Width + "×" + idMap.Height + ", the document " + document.Width + "×" + document.Height + ". Bake it again for this texture set.", nameof(idMap));
            if (colors.Count == 0) return None(document);
            var wanted = new int[colors.Count]; for (int i = 0; i < wanted.Length; i++) wanted[i] = colors[i];
            var data = idMap.Data; var coverage = idMap.Coverage; int w = document.Width;
            return Build(document.Width, document.Height, document.TileSize, (x, y) =>
            {
                int i = y * w + x;
                if (coverage[i] == (byte)MeshTexelCoverage.Empty) return (byte)0;
                int rgb = IdMapColors.Rgb(data, i);
                foreach (int c in wanted) if (IdMapColors.Near(rgb, c, tolerance)) return (byte)255;
                return (byte)0;
            }, parallel: true);
        }
    }
}
