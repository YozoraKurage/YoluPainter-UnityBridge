using System;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>An immutable sampled brush tip: coverage 0..255 per pixel, row-major with the lowest row first (the same
    /// bottom-left origin as the canvas). Importers of top-down formats (PNG, GIMP, ABR, CLIP STUDIO) must flip rows.</summary>
    public sealed class BrushTip
    {
        public const int MaxSize = 2048;
        readonly byte[] alpha;
        public string Name { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        public BrushTip(string name, int width, int height, byte[] alpha)
        {
            if (width < 1 || height < 1 || width > MaxSize || height > MaxSize) throw new ArgumentOutOfRangeException("size", "Brush tips must be 1.." + MaxSize + " pixels per side.");
            if (alpha == null) throw new ArgumentNullException(nameof(alpha));
            if (alpha.Length != checked(width * height)) throw new ArgumentException("Alpha length must be width × height.", nameof(alpha));
            Name = name ?? ""; Width = width; Height = height; this.alpha = (byte[])alpha.Clone();
        }

        public byte[] CopyAlpha() { return (byte[])alpha.Clone(); }
        public byte this[int x, int y] { get { return alpha[y * Width + x]; } }

        /// <summary>Bilinear coverage (0..1) at normalized tip coordinates; 0 outside [0,1]². Pixel centres sit at (i + 0.5) / size.</summary>
        public double Sample(double u, double v)
        {
            if (u < 0 || v < 0 || u > 1 || v > 1) return 0;
            return Bilinear(u * Width - 0.5, v * Height - 0.5, false);
        }
        /// <summary>Bilinear coverage (0..1) at pixel coordinates of an infinitely repeated tip (for paper textures).</summary>
        public double SampleTiled(double x, double y) { return Bilinear(x - 0.5, y - 0.5, true); }

        double Bilinear(double x, double y, bool wrap)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            double a = At(x0, y0, wrap), b = At(x0 + 1, y0, wrap), c = At(x0, y0 + 1, wrap), d = At(x0 + 1, y0 + 1, wrap);
            return ((a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy) / 255.0;
        }
        double At(int x, int y, bool wrap)
        {
            if (wrap) { x %= Width; if (x < 0) x += Width; y %= Height; if (y < 0) y += Height; }
            else if (x < 0 || y < 0 || x >= Width || y >= Height) return 0;
            return alpha[y * Width + x];
        }
    }
}
