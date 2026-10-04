using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>How a stencil's pixels act on a stroke.</summary>
    public enum StencilMode
    {
        /// <summary>A grey image (every texel R = G = B) acts as <see cref="Mask"/>, any other as <see cref="Color"/>.</summary>
        Auto = 0,
        /// <summary>The amount painted: luminance × alpha (white shows the paint, black and transparent hold it back).</summary>
        Mask = 1,
        /// <summary>The colour painted (into the channels the stroke names), with the alpha as the amount.</summary>
        Color = 2,
    }

    /// <summary>Along which axes of the image a stencil repeats beyond its edges (Substance Painter's stencil tiling).</summary>
    public enum StencilTiling { None = 0, Horizontal = 1, Vertical = 2, Both = 3 }

    /// <summary>
    /// An image a brush paints through (Substance Painter's stencil): its pixels as stored, its colour space, whether it is grey, and
    /// its mipmap (<see cref="ImageMipChain"/>, the fill images' one: each level halves the one before by premultiplied area average).
    /// Immutable; strokes on worker threads read it. Built once per content (the mipmap takes a third of the image's bytes and is
    /// refused above a budget).
    /// </summary>
    public sealed class StencilImage
    {
        /// <summary>The default budget of the mipmap (the fill images' cache budget).</summary>
        public const long DefaultMipBudgetBytes = PaintDocument.DefaultFillImageCacheBudgetBytes;
        /// <summary>Image pixels from the origin beyond which nothing is read, repeating or not.</summary>
        public const double FarAway = 1e8;
        public ImageContent Content { get; }
        public ResourceColorSpace ColorSpace { get; }
        /// <summary>Every texel has R = G = B (what <see cref="StencilMode.Auto"/> reads as a mask).</summary>
        public bool IsGrey { get; }
        public int Width => Content.Width;
        public int Height => Content.Height;
        /// <summary>Bytes of the mipmap levels beyond the image itself.</summary>
        public long MipBytes => mips.Bytes;
        internal readonly ImageMipChain mips;

        /// <summary>Refuses (<see cref="ResourceRefusedException"/>, OverBudget) an image whose mipmap needs more than mipBudgetBytes.</summary>
        public StencilImage(ImageContent content, ResourceColorSpace colorSpace, long mipBudgetBytes = DefaultMipBudgetBytes)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (!Enum.IsDefined(typeof(ResourceColorSpace), colorSpace)) throw new ArgumentOutOfRangeException(nameof(colorSpace));
            long need = ImageMipChain.ExtraBytes(content.Width, content.Height);
            if (need > mipBudgetBytes)
                throw new ResourceRefusedException(ResourceRefusal.OverBudget, "The stencil's mipmaps need " + (need >> 20) + " MiB, more than its budget (" + (mipBudgetBytes >> 20) + " MiB).");
            Content = content; ColorSpace = colorSpace; IsGrey = IsGreyImage(content);
            mips = ImageMipChain.Build(content, FillImageConversion.None, false);
        }

        /// <summary>Whether every texel of the image has R = G = B (alpha is not looked at).</summary>
        public static bool IsGreyImage(ImageContent content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            var p = content.Pixels;
            for (long o = 0; o < p.LongLength; o += 4) if (p[o] != p[o + 1] || p[o] != p[o + 2]) return false;
            return true;
        }

        /// <summary>The image read at (x, y) in its pixel units (x from the left, y from the bottom; texel (i, j) covers [i, i + 1) ×
        /// [j, j + 1)) with footprint image texels per painted pixel: bilinear, or trilinear between mipmap levels when the footprint is
        /// above 1. Outside the image along an axis that does not repeat nothing is read (<see cref="StencilTexel.Inside"/> false);
        /// inside it the edge texels repeat outwards. A point <see cref="FarAway"/> or farther from the image is off it even when it repeats
        /// (callers put a point there for "nothing here", e.g. behind the camera).</summary>
        public StencilTexel Read(double x, double y, double footprint, StencilTiling tiling)
        {
            bool repeatX = tiling == StencilTiling.Horizontal || tiling == StencilTiling.Both, repeatY = tiling == StencilTiling.Vertical || tiling == StencilTiling.Both;
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(footprint) || !(Math.Abs(x) < FarAway) || !(Math.Abs(y) < FarAway)) return default;
            if (!(x >= 0 && x <= Width) && !repeatX || !(y >= 0 && y <= Height) && !repeatY) return default;
            var acc = new Acc();
            double u0 = x - .5, v0 = y - .5;
            int last = mips.Levels - 1;
            if (!(footprint > 1) || last == 0) Bilinear(0, u0, v0, 1, repeatX, repeatY, ref acc);
            else
            {
                double lod = Math.Log(footprint) * 1.4426950408889634;
                if (lod >= last) Bilinear(last, Level(last, u0, true), Level(last, v0, false), 1, repeatX, repeatY, ref acc);
                else
                {
                    int k = (int)lod; double f = lod - k;
                    Bilinear(k, Level(k, u0, true), Level(k, v0, false), 1 - f, repeatX, repeatY, ref acc);
                    if (f > 0) Bilinear(k + 1, Level(k + 1, u0, true), Level(k + 1, v0, false), f, repeatX, repeatY, ref acc);
                }
            }
            return acc.Resolve();
        }

        double Level(int level, double c0, bool horizontal)
            => level == 0 ? c0 : (c0 + .5) * (horizontal ? mips.Widths[level] / (double)Width : mips.Heights[level] / (double)Height) - .5;

        void Bilinear(int level, double u, double v, double weight, bool repeatX, bool repeatY, ref Acc acc)
        {
            int w = mips.Widths[level], h = mips.Heights[level];
            double fu = Math.Floor(u), fv = Math.Floor(v), fx = u - fu, fy = v - fv;
            int iu = (int)fu, iv = (int)fv;
            for (int dy = 0; dy < 2; dy++)
            {
                double wy = dy == 0 ? 1 - fy : fy; if (wy <= 0) continue;
                int ty = Wrap(iv + dy, h, repeatY);
                for (int dx = 0; dx < 2; dx++)
                {
                    double wgt = weight * wy * (dx == 0 ? 1 - fx : fx); if (wgt <= 0) continue;
                    mips.Read(level, Wrap(iu + dx, w, repeatX), ty, out int r, out int g, out int b, out int a);
                    acc.Add(wgt, r, g, b, a);
                }
            }
        }
        static int Wrap(int i, int n, bool repeat)
        {
            if (!repeat) return i < 0 ? 0 : i >= n ? n - 1 : i;
            i %= n; return i < 0 ? i + n : i;
        }

        /// <summary>Weighted texels, premultiplied (sums of weight × alpha × colour, all 0..255).</summary>
        struct Acc
        {
            double a, r, g, b; int count, first; bool same;
            public void Add(double w, int cr, int cg, int cb, int ca)
            {
                int packed = cr | cg << 8 | cb << 16 | ca << 24;
                if (count++ == 0) { first = packed; same = true; } else if (packed != first) same = false;
                double k = w * ca; a += k; r += k * cr; g += k * cg; b += k * cb;
            }
            public StencilTexel Resolve()
            {
                if (count == 0) return default;
                if (same)
                {
                    byte cr = (byte)first, cg = (byte)(first >> 8), cb = (byte)(first >> 16), ca = (byte)(first >> 24);
                    return new StencilTexel(true, ca / 255.0, FillImageColor.Luminance(cr, cg, cb) / 255.0 * (ca / 255.0), new Rgba32(cr, cg, cb, ca));
                }
                double alpha = a / 255; if (alpha > 1) alpha = 1;
                // 輝度は FillImageColor.Luminance と同じ重み（灰色ならその値）。premultiplied の和から: Σ w·α·(2126 R + 7152 G + 722 B) / 10000
                double luma = (2126 * r + 7152 * g + 722 * b) / 10000 / (255.0 * 255.0); if (luma > alpha) luma = alpha;
                var color = a > 0 ? new Rgba32(MathUtil.ToByte(r / a / 255), MathUtil.ToByte(g / a / 255), MathUtil.ToByte(b / a / 255), MathUtil.ToByte(alpha)) : Rgba32.Transparent;
                return new StencilTexel(true, alpha, luma, color);
            }
        }
    }

    /// <summary>What a stencil image gives at one point: whether the point is on it, its alpha (0..1), its luminance times alpha
    /// (0..1) and its straight colour.</summary>
    public readonly struct StencilTexel
    {
        public readonly bool Inside;
        public readonly double Alpha, LumaAlpha;
        public readonly Rgba32 Color;
        internal StencilTexel(bool inside, double alpha, double lumaAlpha, Rgba32 color) { Inside = inside; Alpha = alpha; LumaAlpha = lumaAlpha; Color = color; }
    }

    /// <summary>Where a point of the canvas lands on a stencil image: image = (XX·x + XY·y + X0, YX·x + YY·y + Y0) for the canvas
    /// point (x, y) (pixel (i, j)'s centre is (i + .5, j + .5)), in the image's pixel units (see <see cref="StencilImage.Read"/>).
    /// The paint window composes it from the 2D view and where the stencil sits on the screen.</summary>
    public readonly struct StencilMapping : IEquatable<StencilMapping>
    {
        public readonly double XX, XY, X0, YX, YY, Y0;
        public StencilMapping(double xx, double xy, double x0, double yx, double yy, double y0)
        {
            foreach (var v in new[] { xx, xy, x0, yx, yy, y0 }) MathUtil.RequireFinite(v, "stencil mapping");
            XX = xx; XY = xy; X0 = x0; YX = yx; YY = yy; Y0 = y0;
        }
        /// <summary>Image pixels per canvas pixel: the longer of the two canvas axes' images (the footprint mipmaps are chosen by).</summary>
        public double Footprint => Math.Max(Math.Sqrt(XX * XX + YX * YX), Math.Sqrt(XY * XY + YY * YY));
        /// <summary>The image point of canvas pixel (px, py)'s centre.</summary>
        public void Map(int px, int py, out double x, out double y)
        { double cx = px + .5, cy = py + .5; x = XX * cx + XY * cy + X0; y = YX * cx + YY * cy + Y0; }
        /// <summary>The canvas pixel grid laid on the image one to one, moved by (dx, dy) image pixels.</summary>
        public static StencilMapping Translation(double dx, double dy) => new StencilMapping(1, 0, dx, 0, 1, dy);
        public bool Equals(StencilMapping o) => XX == o.XX && XY == o.XY && X0 == o.X0 && YX == o.YX && YY == o.YY && Y0 == o.Y0;
        public override bool Equals(object obj) => obj is StencilMapping m && Equals(m);
        public override int GetHashCode() => XX.GetHashCode() * 31 ^ X0.GetHashCode() * 17 ^ Y0.GetHashCode();
    }

    /// <summary>Where one painted pixel lands on the stencil (image pixel units) and its footprint (image pixels per painted pixel), for
    /// pixels a caller places itself (<see cref="BrushStroke.ApplyPixel(int,int,double,double,StencilPoint)"/>: the 3D view projects
    /// the texel's point on the model to the screen).</summary>
    public readonly struct StencilPoint
    {
        public readonly double X, Y, Footprint;
        public StencilPoint(double x, double y, double footprint = 1)
        {
            MathUtil.RequireFinite(x, nameof(x)); MathUtil.RequireFinite(y, nameof(y)); MathUtil.RequireFinite(footprint, nameof(footprint));
            if (Math.Abs(x) > 1e9 || Math.Abs(y) > 1e9) throw new ArgumentOutOfRangeException(nameof(x), "Stencil points must be within ±1e9 image pixels.");
            if (footprint < 0) throw new ArgumentOutOfRangeException(nameof(footprint));
            X = x; Y = y; Footprint = footprint;
        }
    }

    /// <summary>What the stencil does to one pixel of a stroke: the amount (0..1) the pixel's ceiling is multiplied by, and in
    /// <see cref="StencilMode.Color"/> the colour painted there.</summary>
    public readonly struct StencilSample
    {
        public readonly double Amount;
        public readonly Rgba32 Color;
        public readonly bool HasColor;
        internal StencilSample(double amount, Rgba32 color, bool hasColor) { Amount = amount; Color = color; HasColor = hasColor; }
    }

    /// <summary>
    /// A stencil bound to a stroke (Substance Painter's stencil: an image laid over the screen that the brush paints through): the
    /// image, its resolved mode, tiling and inversion, where the canvas lands on it for 2D dabs (<see cref="CanvasToImage"/>; null when
    /// the caller gives every pixel's point, as the 3D view does) and which channels take its colour in <see cref="StencilMode.Color"/>.
    /// <para>Mask: the amount is luminance × alpha (inverted: (1 − luminance) × alpha). Colour: the amount is alpha and the colour is the
    /// image's straight RGB read like a fill layer's image (<see cref="FillImageColor"/>: a linear image is encoded to sRGB in a colour
    /// channel, the scalar channels take its luminance, Normal reads the values as stored), with the brush colour's alpha. Channels
    /// not named, masks, erasing and the pixel effects take the amount only. Outside the image (along an axis that does not repeat)
    /// the amount is 0: nothing is painted there.</para>
    /// <para>The amount multiplies the dab's ceiling (opacity), like the paper texture, not its flow: overlapping dabs build up to the
    /// stencil's amount and never past it, so a 50 % grey lets at most half the paint through in one stroke.</para>
    /// </summary>
    public sealed class BrushStencil
    {
        public StencilImage Image { get; }
        /// <summary><see cref="StencilMode.Mask"/> or <see cref="StencilMode.Color"/> (Auto is resolved when made).</summary>
        public StencilMode Mode { get; }
        public StencilTiling Tiling { get; }
        public bool Invert { get; }
        public StencilMapping? CanvasToImage { get; }
        readonly int colorChannels;

        public BrushStencil(StencilImage image, StencilMode mode, StencilTiling tiling = StencilTiling.None, bool invert = false,
            StencilMapping? canvasToImage = null, IEnumerable<PaintChannel> colorChannels = null)
        {
            Image = image ?? throw new ArgumentNullException(nameof(image));
            if (!Enum.IsDefined(typeof(StencilMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            if (!Enum.IsDefined(typeof(StencilTiling), tiling)) throw new ArgumentOutOfRangeException(nameof(tiling));
            Mode = Resolve(mode, image); Tiling = tiling; Invert = invert; CanvasToImage = canvasToImage;
            if (colorChannels != null)
                foreach (var c in colorChannels) { PaintLayer.ValidateChannel(c); this.colorChannels |= 1 << (int)c; }
        }

        /// <summary>The mode Auto stands for with this image (a grey image is a mask).</summary>
        public static StencilMode Resolve(StencilMode mode, StencilImage image)
            => mode != StencilMode.Auto ? mode : image != null && image.IsGrey ? StencilMode.Mask : StencilMode.Color;

        /// <summary>Whether a stroke's surface painting channel takes the stencil's colour (Colour mode and the channel is named).</summary>
        public bool PaintsColorInto(PaintChannel channel) => Mode == StencilMode.Color && (colorChannels & 1 << (int)channel) != 0;

        /// <summary>The stencil at image point (x, y) with the footprint (image pixels per painted pixel).</summary>
        public StencilSample Sample(double x, double y, double footprint)
        {
            var t = Image.Read(x, y, footprint, Tiling);
            if (!t.Inside) return default;
            if (Mode == StencilMode.Color) return new StencilSample(t.Alpha, t.Color, true);
            double amount = Invert ? t.Alpha - t.LumaAlpha : t.LumaAlpha;
            return new StencilSample(amount < 0 ? 0 : amount > 1 ? 1 : amount, default, false);
        }
        public StencilSample Sample(StencilPoint at) => Sample(at.X, at.Y, at.Footprint);

        /// <summary>The stencil at canvas pixel (px, py) through <see cref="CanvasToImage"/>.</summary>
        public StencilSample SampleCanvas(int px, int py)
        {
            if (!CanvasToImage.HasValue) throw new InvalidOperationException("This stencil has no mapping from the canvas; give each pixel its point on the stencil.");
            var m = CanvasToImage.Value; m.Map(px, py, out double x, out double y);
            return Sample(x, y, m.Footprint);
        }

        /// <summary>The colour a target channel paints for a stencil colour: converted like a fill image's (sRGB-encoding a linear image
        /// in a colour channel, the luminance in a scalar channel), with the given alpha (the brush colour's).</summary>
        internal Rgba32 PaintFor(PaintChannel channel, Rgba32 sampled, byte alpha)
        {
            var lut = FillImageColor.Table(FillImageColor.ConversionFor(Image.ColorSpace, channel));
            byte r = sampled.R, g = sampled.G, b = sampled.B;
            if (lut != null) { r = lut[r]; g = lut[g]; b = lut[b]; }
            if (FillImageColor.UsesLuminance(channel)) r = g = b = FillImageColor.Luminance(r, g, b);
            return new Rgba32(r, g, b, alpha);
        }
    }

    public sealed partial class BrushSettings
    {
        /// <summary>The stencil the stroke paints through (null = none). Shared, not copied, by <see cref="Clone"/>.</summary>
        public BrushStencil Stencil;
        /// <summary>The channel a copy made by <see cref="ForChannel"/> paints (null: a mask, or not made for a channel). The stroke reads it
        /// to know which surfaces take the stencil's colour.</summary>
        internal PaintChannel? PaintedChannel;

        BrushSettings WithStencilOf(BrushSettings source) { Stencil = source.Stencil; PaintedChannel = source.PaintedChannel; return this; }
    }
}
