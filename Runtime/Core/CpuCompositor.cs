using System;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Deterministic RGBA8 reference blend in stored RGB space. This does not claim ICC-managed PSD
    /// equivalence or tangent-space normal-vector composition. Alpha is always linear coverage.</summary>
    public static class CpuCompositor
    {
        public static Rgba32 Blend(Rgba32 destination, Rgba32 source, double opacity = 1, LayerBlendMode mode = LayerBlendMode.Normal)
        {
            MathUtil.RequireFinite(opacity, nameof(opacity));
            if (opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
            if (!Enum.IsDefined(typeof(LayerBlendMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            double sa = source.A / 255.0 * opacity, da = destination.A / 255.0;
            if (sa <= 0) return destination;
            double a = sa + da * (1 - sa);
            if (a <= 0) return Rgba32.Transparent;
            return new Rgba32(BlendComponent(destination.R, source.R, da, sa, a, mode),
                BlendComponent(destination.G, source.G, da, sa, a, mode),
                BlendComponent(destination.B, source.B, da, sa, a, mode), MathUtil.ToByte(a));
        }
        private static byte BlendComponent(byte destination, byte source, double da, double sa, double alpha, LayerBlendMode mode)
        {
            double d = destination / 255.0, s = source / 255.0;
            double blend = s;
            if (mode == LayerBlendMode.Multiply) blend = d * s;
            else if (mode == LayerBlendMode.Screen) blend = 1 - (1 - d) * (1 - s);
            // W3C separable blend + source-over; nonoverlapping source retains its own color.
            double premultiplied = (1 - sa) * da * d + (1 - da) * sa * s + da * sa * blend;
            return MathUtil.ToByte(premultiplied / alpha);
        }
        public static Rgba32 CompositePixel(PaintDocument document, PaintChannel channel, int x, int y)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            PaintLayer.ValidateChannel(channel);
            if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) throw new ArgumentOutOfRangeException("pixel");
            Rgba32 result = Rgba32.Transparent;
            foreach (PaintLayer layer in document.Layers)
            {
                SparseTileSurface surface;
                if (!layer.Visible || layer.Opacity <= 0 || !layer.IsChannelEnabled(channel) || !layer.TryGetChannel(channel, out surface)) continue;
                result = Blend(result, surface.GetPixel(x, y), layer.Opacity, layer.BlendMode);
            }
            return result;
        }
        /// <summary>Explicit full-frame reference output; use CompositeRegion/CompositePixel for narrow inspection.
        /// It deliberately does not allocate persistent full-frame buffers per layer.</summary>
        public static byte[] Composite(PaintDocument document, PaintChannel channel)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            return CompositeRegion(document, channel, 0, 0, document.Width, document.Height);
        }
        public static byte[] CompositeRegion(PaintDocument document, PaintChannel channel, int x, int y, int width, int height)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            PaintLayer.ValidateChannel(channel);
            if (width < 0 || height < 0 || x < 0 || y < 0 || (long)x + width > document.Width || (long)y + height > document.Height)
                throw new ArgumentOutOfRangeException("region");
            var bytes = new byte[checked(width * height * 4)];
            for (int row = 0; row < height; row++) for (int col = 0; col < width; col++)
            {
                var pixel = CompositePixel(document, channel, x + col, y + row);
                int offset = (row * width + col) * 4;
                bytes[offset] = pixel.R; bytes[offset + 1] = pixel.G; bytes[offset + 2] = pixel.B; bytes[offset + 3] = pixel.A;
            }
            return bytes;
        }
    }
}
