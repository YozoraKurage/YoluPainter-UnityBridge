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
            return BlendUnchecked(destination, source, opacity, mode);
        }
        private static Rgba32 BlendUnchecked(Rgba32 destination, Rgba32 source, double opacity, LayerBlendMode mode)
        {
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
        /// <summary>Composites the colour of a clipped layer onto its clipping group: the clipped colour is combined with the
        /// group colour by the blend mode and mixed in by the clipped pixel's alpha × amount (opacity × mask). The group keeps
        /// its alpha (the base's), so a clipped layer never paints outside the base.</summary>
        public static Rgba32 ClipOnto(Rgba32 group, Rgba32 clipped, double amount, LayerBlendMode mode)
        {
            double t = clipped.A / 255.0 * amount;
            if (t <= 0 || group.A == 0) return group;
            return new Rgba32(Mix(group.R, clipped.R, t, mode), Mix(group.G, clipped.G, t, mode), Mix(group.B, clipped.B, t, mode), group.A);
        }
        /// <summary>below + (blend(below, over) − below) × amount for one component, rounded once.</summary>
        internal static byte Mix(byte below, byte over, double amount, LayerBlendMode mode)
        {
            double d = below / 255.0, o = over / 255.0, m = o;
            if (mode == LayerBlendMode.Multiply) m = d * o;
            else if (mode == LayerBlendMode.Screen) m = 1 - (1 - d) * (1 - o);
            return MathUtil.ToByte(d + (m - d) * amount);
        }

        /// <summary>One step of the layer stack for a channel: an unclipped layer and the active layers clipped to it, bottom to top.</summary>
        public sealed class StackEntry
        {
            readonly System.Collections.Generic.List<PaintLayer> clips = new System.Collections.Generic.List<PaintLayer>();
            public PaintLayer Base { get; internal set; }
            public System.Collections.Generic.IReadOnlyList<PaintLayer> Clips { get { return clips; } }
            internal void AddClip(PaintLayer layer) { clips.Add(layer); }
        }
        static bool Active(PaintLayer layer, PaintChannel channel)
        { return layer.Visible && layer.Opacity > 0 && layer.IsChannelEnabled(channel) && layer.HasContent(channel); }
        /// <summary>Groups the layers into clipping groups for a channel, dropping what cannot show: a hidden (or empty in this
        /// channel) base hides its clipped layers too, and an adjustment base has no pixels to clip to.</summary>
        public static System.Collections.Generic.List<StackEntry> Plan(PaintDocument document, PaintChannel channel)
        {
            var plan = new System.Collections.Generic.List<StackEntry>(); var layers = document.Layers;
            for (int i = 0; i < layers.Count; i++)
            {
                if (document.IsEffectivelyClipped(i) || !Active(layers[i], channel)) continue;
                var entry = new StackEntry { Base = layers[i] };
                if (layers[i].Kind != LayerKind.Adjustment)
                    for (int j = i + 1; j < layers.Count && document.IsEffectivelyClipped(j); j++)
                        if (Active(layers[j], channel)) entry.AddClip(layers[j]);
                plan.Add(entry);
            }
            return plan;
        }

        public static Rgba32 CompositePixel(PaintDocument document, PaintChannel channel, int x, int y)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            PaintLayer.ValidateChannel(channel);
            if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) throw new ArgumentOutOfRangeException("pixel");
            Rgba32 result = Rgba32.Transparent;
            foreach (var entry in Plan(document, channel))
            {
                var layer = entry.Base;
                double amount = layer.Opacity * (layer.Mask == null ? 1 : layer.Mask.FactorAt(x, y));
                if (layer.Kind == LayerKind.Adjustment) { result = layer.Adjustment.Composite(result, amount, layer.BlendMode); continue; }
                Rgba32 group = layer.GetPixel(channel, x, y);
                foreach (var clip in entry.Clips)
                {
                    double clipAmount = clip.Opacity * (clip.Mask == null ? 1 : clip.Mask.FactorAt(x, y));
                    group = clip.Kind == LayerKind.Adjustment ? clip.Adjustment.Composite(group, clipAmount, clip.BlendMode)
                        : ClipOnto(group, clip.GetPixel(channel, x, y), clipAmount, clip.BlendMode);
                }
                result = Blend(result, group, amount, layer.BlendMode);
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
        /// <summary>Per-layer tile buffers for one tile of the plan.</summary>
        sealed class LayerTile
        {
            public PaintLayer Layer; public byte[] Pixels, Mask; public bool Present; public RasterMask MaskSource;
            public LayerTile(PaintLayer layer, int tileBytes)
            {
                Layer = layer;
                if (layer.Kind != LayerKind.Adjustment) Pixels = new byte[tileBytes];
                // A neutral mask multiplies by exactly 1, so skipping it is exact.
                if (layer.Mask != null && !layer.Mask.IsNeutral) { MaskSource = layer.Mask; Mask = new byte[tileBytes]; }
            }
            public void Load(PaintChannel channel, TileCoord coord)
            {
                Present = Layer.Kind == LayerKind.Adjustment || Layer.CopyTile(channel, coord, Pixels);
                if (Present && MaskSource != null) MaskSource.Surface.CopyTile(coord, Mask);
            }
            public double Amount(int offset) { return MaskSource == null ? Layer.Opacity : Layer.Opacity * MaskSource.Factor(Mask[offset + 3]); }
            public Rgba32 Pixel(int offset) { return new Rgba32(Pixels[offset], Pixels[offset + 1], Pixels[offset + 2], Pixels[offset + 3]); }
        }
        public static byte[] CompositeRegion(PaintDocument document, PaintChannel channel, int x, int y, int width, int height)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            PaintLayer.ValidateChannel(channel);
            if (width < 0 || height < 0 || x < 0 || y < 0 || (long)x + width > document.Width || (long)y + height > document.Height)
                throw new ArgumentOutOfRangeException("region");
            var bytes = new byte[checked(width * height * 4)];
            if (width == 0 || height == 0) return bytes;
            // Same per-pixel arithmetic as CompositePixel, but each layer's tile is read once instead of one dictionary
            // lookup per pixel per layer.
            int tile = document.TileSize, tileBytes = checked(tile * tile * 4);
            var plan = Plan(document, channel);
            var bases = new LayerTile[plan.Count]; var clips = new LayerTile[plan.Count][];
            for (int e = 0; e < plan.Count; e++)
            {
                bases[e] = new LayerTile(plan[e].Base, tileBytes);
                clips[e] = new LayerTile[plan[e].Clips.Count];
                for (int c = 0; c < clips[e].Length; c++) clips[e][c] = new LayerTile(plan[e].Clips[c], tileBytes);
            }
            for (int ty = y / tile; ty <= (y + height - 1) / tile; ty++)
                for (int tx = x / tile; tx <= (x + width - 1) / tile; tx++)
                {
                    var coord = new TileCoord(tx, ty); bool any = false;
                    for (int e = 0; e < plan.Count; e++)
                    {
                        bases[e].Load(channel, coord);
                        if (plan[e].Base.Kind != LayerKind.Adjustment) any |= bases[e].Present;
                        if (bases[e].Present) foreach (var clip in clips[e]) clip.Load(channel, coord);
                    }
                    // Nothing with pixels here: the result stays transparent, and adjustments leave transparent pixels alone.
                    if (!any) continue;
                    int x0 = Math.Max(x, tx * tile), x1 = Math.Min(x + width, (tx + 1) * tile);
                    int y0 = Math.Max(y, ty * tile), y1 = Math.Min(y + height, (ty + 1) * tile);
                    for (int py = y0; py < y1; py++) for (int px = x0; px < x1; px++)
                    {
                        int source = ((py - ty * tile) * tile + (px - tx * tile)) * 4;
                        Rgba32 result = Rgba32.Transparent;
                        for (int e = 0; e < plan.Count; e++)
                        {
                            var b = bases[e]; if (!b.Present) continue;
                            var layer = b.Layer; double amount = b.Amount(source);
                            if (layer.Kind == LayerKind.Adjustment) { result = layer.Adjustment.Composite(result, amount, layer.BlendMode); continue; }
                            Rgba32 group = b.Pixel(source);
                            foreach (var clip in clips[e])
                            {
                                if (!clip.Present) continue;
                                group = clip.Layer.Kind == LayerKind.Adjustment ? clip.Layer.Adjustment.Composite(group, clip.Amount(source), clip.Layer.BlendMode)
                                    : ClipOnto(group, clip.Pixel(source), clip.Amount(source), clip.Layer.BlendMode);
                            }
                            result = BlendUnchecked(result, group, amount, layer.BlendMode);
                        }
                        int offset = ((py - y) * width + (px - x)) * 4;
                        bytes[offset] = result.R; bytes[offset + 1] = result.G; bytes[offset + 2] = result.B; bytes[offset + 3] = result.A;
                    }
                }
            return bytes;
        }
    }
}
