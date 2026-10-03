using System;

namespace Yozolab.YoluPainter.Core.Shelf
{
    /// <summary>
    /// Resamples a whole straight RGBA8 image (bottom-left origin) to another size with the formulas of
    /// <see cref="PaintDocument.Resampled"/> (the same <see cref="CanvasResampling"/> weights and per-pixel rule: premultiplied
    /// averages, a pixel read only from equal pixels is that pixel, a fully transparent result keeps the plain average colour of
    /// the transparent pixels it covers). Rows are computed in parallel under <see cref="CoreParallelism"/>; each output pixel
    /// depends only on the source, so the bytes do not depend on the thread count.
    /// </summary>
    public static class ImageResampling
    {
        /// <summary>The automatic choice (as for texture sets): the same size copies, growing on both axes is bilinear, anything that
        /// shrinks an axis is the area average.</summary>
        public static CanvasResampling Automatic(int width, int height, int targetWidth, int targetHeight)
            => targetWidth >= width && targetHeight >= height ? CanvasResampling.Bilinear : CanvasResampling.Area;

        public static byte[] Resample(byte[] rgba, int width, int height, int targetWidth, int targetHeight, CanvasResampling resampling)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (width < 1 || height < 1 || rgba.LongLength != (long)width * height * 4) throw new ArgumentException("The image must be " + width + " × " + height + " RGBA8.", nameof(rgba));
            if (targetWidth < 1 || targetHeight < 1 || targetWidth > PaintDocument.MaxNativeSide || targetHeight > PaintDocument.MaxNativeSide) throw new ArgumentOutOfRangeException(nameof(targetWidth), "The target is 1–" + PaintDocument.MaxNativeSide + " pixels on each side.");
            if (!Enum.IsDefined(typeof(CanvasResampling), resampling)) throw new ArgumentOutOfRangeException(nameof(resampling));
            if (targetWidth == width && targetHeight == height) return (byte[])rgba.Clone();
            var xs = new ResampleAxis(width, targetWidth, resampling); var ys = new ResampleAxis(height, targetHeight, resampling);
            var result = new byte[(long)targetWidth * targetHeight * 4];
            CoreParallelism.For(targetHeight, CoreParallelism.Degree, y =>
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    var p = Pixel(rgba, width, xs, ys, x, y);
                    int o = (y * targetWidth + x) * 4;
                    result[o] = p.R; result[o + 1] = p.G; result[o + 2] = p.B; result[o + 3] = p.A;
                }
            });
            return result;
        }

        /// <summary>The per-pixel rule of the canvas resampler (CanvasResampler.Pixel without the Normal case), reading a flat image.</summary>
        static Rgba32 Pixel(byte[] rgba, int width, ResampleAxis xs, ResampleAxis ys, int x, int y)
        {
            int cx = xs.Count[x], cy = ys.Count[y], ox = xs.Offset[x], oy = ys.Offset[y], jx = xs.Start[x], jy = ys.Start[y];
            if (cx == 1 && cy == 1) return Get(rgba, width, jx, jy);
            double a = 0, r = 0, g = 0, b = 0, zw = 0, zr = 0, zg = 0, zb = 0;
            bool same = true; Rgba32 firstPixel = default; bool first = true;
            for (int v = 0; v < cy; v++)
            {
                double wy = ys.Weight[oy + v]; if (wy <= 0) continue;
                for (int u = 0; u < cx; u++)
                {
                    double wgt = wy * xs.Weight[ox + u]; if (wgt <= 0) continue;
                    var p = Get(rgba, width, jx + u, jy + v);
                    if (first) { firstPixel = p; first = false; } else if (same && p != firstPixel) same = false;
                    if (p.A == 0) { zw += wgt; zr += wgt * p.R; zg += wgt * p.G; zb += wgt * p.B; continue; }
                    double k = wgt * p.A; a += k; r += k * p.R; g += k * p.G; b += k * p.B;
                }
            }
            if (same) return firstPixel;
            byte alpha = MathUtil.ToByte(a / 255);
            if (alpha == 0)
                return zw > 0 ? new Rgba32(MathUtil.ToByte(zr / zw / 255), MathUtil.ToByte(zg / zw / 255), MathUtil.ToByte(zb / zw / 255), 0) : Rgba32.Transparent;
            return new Rgba32(MathUtil.ToByte(r / a / 255), MathUtil.ToByte(g / a / 255), MathUtil.ToByte(b / a / 255), alpha);
        }

        static Rgba32 Get(byte[] rgba, int width, int x, int y) { int o = (y * width + x) * 4; return new Rgba32(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]); }
    }
}
