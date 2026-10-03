using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>3D の面などから渡す、ダブ内で重複をまとめた画素と被覆率。</summary>
    public readonly struct BrushPixel
    {
        public readonly int X, Y;
        public readonly double Coverage;
        public BrushPixel(int x, int y, double coverage) { X = x; Y = y; Coverage = coverage; }
    }

    public sealed partial class BrushStroke
    {
        // ダブ前に読む領域だけを凍結する。書き込み中に面を読み返さず、ワーカーとタイルの順序によらない。
        EffectFrame[] effectFrames;
        long effectScratchBytes;
        bool effectHasPosition;
        double effectX, effectY, effectOffsetX, effectOffsetY;
        void EnsureEffectBudget(long bytes) => document.EnsureStrokeBudget(checked(bytes + effectScratchBytes + symmetryScratchBytes));
        void ReleaseEffectDab() { effectFrames = null; effectScratchBytes = 0; }

        bool PrepareEffectDab(int minX, int maxX, int minY, int maxY, double x, double y)
        {
            if (settings.Effect == BrushEffect.Paint) return true;
            ReleaseEffectDab();
            if (settings.Effect == BrushEffect.Smudge)
            {
                double dx = effectX - x, dy = effectY - y;
                bool first = !effectHasPosition;
                effectHasPosition = true; effectX = x; effectY = y;
                if (first || (dx == 0 && dy == 0)) return false;
                effectOffsetX = dx; effectOffsetY = dy;
            }
            else { effectOffsetX = settings.CloneOffsetX; effectOffsetY = settings.CloneOffsetY; }
            int radius = settings.Effect == BrushEffect.Blur ? settings.BlurRadius : 0;
            int x0 = Math.Max(0, (int)Math.Floor(minX + (radius > 0 ? 0 : effectOffsetX)) - radius);
            int y0 = Math.Max(0, (int)Math.Floor(minY + (radius > 0 ? 0 : effectOffsetY)) - radius);
            int x1 = Math.Min(width - 1, (int)Math.Ceiling(maxX + (radius > 0 ? 0 : effectOffsetX)) + radius + (radius > 0 ? 0 : 1));
            int y1 = Math.Min(height - 1, (int)Math.Ceiling(maxY + (radius > 0 ? 0 : effectOffsetY)) + radius + (radius > 0 ? 0 : 1));
            if (x1 < x0 || y1 < y0) return false;
            long cells = checked((long)(x1 - x0 + 1) * (y1 - y0 + 1));
            long bytes = checked(targets.Length * (cells * 4 + (radius > 0 ? (long)(x1 - x0 + 2) * (y1 - y0 + 2) * 32 : 0)));
            document.EnsureStrokeBudget(checked(rollbackBytes + bytes + symmetryScratchBytes)); effectScratchBytes = bytes;
            effectFrames = new EffectFrame[targets.Length];
            for (int k = 0; k < targets.Length; k++)
            {
                var t = targets[k];
                var frame = new EffectFrame(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
                // タイルは読む行の区間ごとに引く。クローンは既に触ったタイルだけ巻き戻しの写しを読む。
                for (int py = y0; py <= y1; py++) for (int px = x0; px <= x1;)
                {
                    var coord = new TileCoord(px / tileSize, py / tileSize);
                    var tile = settings.Effect == BrushEffect.Clone && t.Before.TryGetValue(coord, out var original) ? original : t.Surface.PeekTile(coord);
                    int end = Math.Min(x1, (px / tileSize + 1) * tileSize - 1);
                    for (; px <= end; px++) frame.Pixels[(py - y0) * frame.Width + px - x0] = tile == null ? Rgba32.Transparent : tile.Get(((py % tileSize) * tileSize + px % tileSize) * 4);
                }
                if (radius > 0) frame.BuildIntegral();
                effectFrames[k] = frame;
            }
            return true;
        }
        bool EffectPixel(int k, int x, int y, out Rgba32 sampled)
        {
            if (settings.Effect == BrushEffect.Blur) { sampled = effectFrames[k].Blur(x, y, settings.BlurRadius, width, height); return true; }
            double sx = x + effectOffsetX, sy = y + effectOffsetY;
            if (sx < 0 || sy < 0 || sx > width - 1 || sy > height - 1) { sampled = default; return false; }
            sampled = effectFrames[k].Sample(sx, sy, width, height); return true;
        }
        static Rgba32 MixEffect(Rgba32 start, Rgba32 sample, double amount, bool keep)
        {
            if (keep) return PaintDocument.PaintKeepingAlpha(start, new Rgba32(sample.R, sample.G, sample.B, 255), amount * sample.A / 255.0);
            double a = start.A * (1 - amount), b = sample.A * amount, alpha = a + b;
            if (alpha <= 0) return new Rgba32(start.R, start.G, start.B, 0);
            return new Rgba32(Byte255((start.R * a + sample.R * b) / alpha), Byte255((start.G * a + sample.G * b) / alpha), Byte255((start.B * a + sample.B * b) / alpha), Byte255(alpha));
        }
        static byte Byte255(double v) => (byte)Math.Max(0, Math.Min(255, Math.Floor(v + .5)));

        /// <summary>面のダブを丸ごと適用する。効果の読み元は全画素を書き込む前に凍結。指先の中心は UV の画素座標で、継ぎ目を跨ぐときは呼び手が ResetEffectDirection する。</summary>
        public bool ApplyDab(IReadOnlyList<BrushPixel> pixels, double centerX, double centerY, double pressure = 1)
        {
            CheckOpen();
            try
            {
                if (pixels == null) throw new ArgumentNullException(nameof(pixels));
                MathUtil.RequireFinite(centerX, nameof(centerX)); MathUtil.RequireFinite(centerY, nameof(centerY)); MathUtil.RequireFinite(pressure, nameof(pressure));
                if (Math.Abs(centerX) > 10000000 || Math.Abs(centerY) > 10000000 || pressure < 0 || pressure > 1) throw new ArgumentOutOfRangeException("dab");
                int x0 = width, y0 = height, x1 = -1, y1 = -1;
                foreach (var p in pixels)
                {
                    MathUtil.RequireFinite(p.Coverage, nameof(p.Coverage)); if (p.Coverage < 0 || p.Coverage > 1) throw new ArgumentOutOfRangeException(nameof(p.Coverage));
                    if (p.X < 0 || p.Y < 0 || p.X >= width || p.Y >= height) continue;
                    x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X); y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
                }
                if (x1 < x0 || !PrepareEffectDab(x0, x1, y0, y1, centerX, centerY)) return false;
                bool changed = false; BeginPass();
                try
                {
                    foreach (var p in pixels) if (p.X >= 0 && p.Y >= 0 && p.X < width && p.Y < height)
                        changed |= ApplyPixelAt(cursor, true, p.X / tileSize, p.Y / tileSize, (p.Y % tileSize) * tileSize + p.X % tileSize, p.Coverage, pressure, 1, 1);
                }
                finally { EndPass(); ReleaseEffectDab(); }
                if (changed) document.PixelsChanged(); return changed;
            }
            catch { Cancel(); throw; }
        }
        public void ResetEffectDirection() { CheckOpen(); effectHasPosition = false; }

        sealed class EffectFrame
        {
            public readonly int X, Y, Width, Height;
            public readonly Rgba32[] Pixels;
            long[] integral;
            public EffectFrame(int x, int y, int w, int h) { X = x; Y = y; Width = w; Height = h; Pixels = new Rgba32[checked(w * h)]; }
            public void BuildIntegral()
            {
                int stride = (Width + 1) * 4; integral = new long[checked(stride * (Height + 1))];
                for (int y = 1; y <= Height; y++)
                {
                    long r = 0, g = 0, b = 0, a = 0;
                    for (int x = 1; x <= Width; x++)
                    {
                        var p = Pixels[(y - 1) * Width + x - 1]; r += p.R * p.A; g += p.G * p.A; b += p.B * p.A; a += p.A;
                        int n = y * stride + x * 4, up = n - stride;
                        integral[n] = integral[up] + r; integral[n + 1] = integral[up + 1] + g; integral[n + 2] = integral[up + 2] + b; integral[n + 3] = integral[up + 3] + a;
                    }
                }
            }
            public Rgba32 Blur(int x, int y, int radius, int w, int h)
            {
                int x0 = Math.Max(0, x - radius), y0 = Math.Max(0, y - radius), x1 = Math.Min(w - 1, x + radius) + 1, y1 = Math.Min(h - 1, y + radius) + 1;
                int stride = (Width + 1) * 4;
                int tl = (y0 - Y) * stride + (x0 - X) * 4, tr = (y0 - Y) * stride + (x1 - X) * 4;
                int bl = (y1 - Y) * stride + (x0 - X) * 4, br = (y1 - Y) * stride + (x1 - X) * 4;
                long Sum(int c) => integral[br + c] - integral[bl + c] - integral[tr + c] + integral[tl + c];
                long a = Sum(3); if (a == 0) return Rgba32.Transparent;
                return new Rgba32(Byte255(Sum(0) / (double)a), Byte255(Sum(1) / (double)a), Byte255(Sum(2) / (double)a), Byte255(a / (double)((x1 - x0) * (y1 - y0))));
            }
            public Rgba32 Sample(double x, double y, int w, int h)
            {
                int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y); double fx = x - ix, fy = y - iy;
                Rgba32 At(int px, int py) => Pixels[(Math.Min(h - 1, py) - Y) * Width + Math.Min(w - 1, px) - X];
                if (fx == 0 && fy == 0) return At(ix, iy);
                double r = 0, g = 0, b = 0, a = 0;
                void Add(Rgba32 p, double weight) { double v = p.A * weight; r += p.R * v; g += p.G * v; b += p.B * v; a += v; }
                Add(At(ix, iy), (1 - fx) * (1 - fy)); Add(At(ix + 1, iy), fx * (1 - fy));
                Add(At(ix, iy + 1), (1 - fx) * fy); Add(At(ix + 1, iy + 1), fx * fy);
                return a <= 0 ? Rgba32.Transparent : new Rgba32(Byte255(r / a), Byte255(g / a), Byte255(b / a), Byte255(a));
            }
        }
    }
}
