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
            double dr = destination.R / 255.0, dg = destination.G / 255.0, db = destination.B / 255.0;
            double sr = source.R / 255.0, sg = source.G / 255.0, sb = source.B / 255.0;
            BlendRgb(mode, dr, dg, db, sr, sg, sb, out double br, out double bg, out double bb);
            // W3C separable blend + source-over; nonoverlapping source retains its own color.
            return new Rgba32(MathUtil.ToByte(((1 - sa) * da * dr + (1 - da) * sa * sr + da * sa * br) / a),
                MathUtil.ToByte(((1 - sa) * da * dg + (1 - da) * sa * sg + da * sa * bg) / a),
                MathUtil.ToByte(((1 - sa) * da * db + (1 - da) * sa * sb + da * sa * bb) / a), MathUtil.ToByte(a));
        }

        /// <summary>The blend colour B(below, over) of a mode for one pixel, components in 0..1, result clamped to 0..1.
        /// Separable modes use Photoshop's formulas (soft light is Photoshop's, not the PDF/W3C variant); divisions by zero
        /// follow the W3C/Krita convention (colour dodge keeps black black, colour burn keeps white white, divide by black
        /// gives white unless the base is black). Hue/Saturation/Color/Luminosity use the non-separable W3C formulas with
        /// luminance 0.3/0.59/0.11. Darker/Lighter Color compare the channel sums and keep the base on a tie. Equality with
        /// Photoshop's own output is not measured.</summary>
        public static void BlendRgb(LayerBlendMode mode, double dr, double dg, double db, double sr, double sg, double sb, out double r, out double g, out double b)
        {
            switch (mode)
            {
                case LayerBlendMode.Normal: r = sr; g = sg; b = sb; return;
                case LayerBlendMode.Hue: SetLum(SetSat(sr, sg, sb, Sat(dr, dg, db)), Lum(dr, dg, db), out r, out g, out b); return;
                case LayerBlendMode.Saturation: SetLum(SetSat(dr, dg, db, Sat(sr, sg, sb)), Lum(dr, dg, db), out r, out g, out b); return;
                case LayerBlendMode.Color: SetLum((sr, sg, sb), Lum(dr, dg, db), out r, out g, out b); return;
                case LayerBlendMode.Luminosity: SetLum((dr, dg, db), Lum(sr, sg, sb), out r, out g, out b); return;
                // 和は 8 bit の値の和なので、違うなら 1/255 以上離れている。半段の余裕で同点を浮動小数の誤差から守る（GPU も同じ）。
                case LayerBlendMode.DarkerColor: if (sr + sg + sb < dr + dg + db - TieMargin) { r = sr; g = sg; b = sb; } else { r = dr; g = dg; b = db; } return;
                case LayerBlendMode.LighterColor: if (sr + sg + sb > dr + dg + db + TieMargin) { r = sr; g = sg; b = sb; } else { r = dr; g = dg; b = db; } return;
                default: r = Separable(mode, dr, sr); g = Separable(mode, dg, sg); b = Separable(mode, db, sb); return;
            }
        }

        const double TieMargin = .5 / 255;

        static double Separable(LayerBlendMode mode, double d, double s)
        {
            double v;
            switch (mode)
            {
                case LayerBlendMode.Multiply: v = d * s; break;
                case LayerBlendMode.Screen: v = d + s - d * s; break;
                case LayerBlendMode.Overlay: v = d <= .5 ? 2 * d * s : 1 - 2 * (1 - d) * (1 - s); break;
                case LayerBlendMode.Darken: v = Math.Min(d, s); break;
                case LayerBlendMode.Lighten: v = Math.Max(d, s); break;
                case LayerBlendMode.ColorDodge: v = Dodge(d, s); break;
                case LayerBlendMode.ColorBurn: v = Burn(d, s); break;
                case LayerBlendMode.LinearDodge: v = d + s; break;
                case LayerBlendMode.LinearBurn: v = d + s - 1; break;
                case LayerBlendMode.HardLight: v = s <= .5 ? 2 * d * s : 1 - 2 * (1 - d) * (1 - s); break;
                case LayerBlendMode.SoftLight: v = s <= .5 ? d - (1 - 2 * s) * d * (1 - d) : d + (2 * s - 1) * (Math.Sqrt(d) - d); break;
                case LayerBlendMode.VividLight: v = s <= .5 ? Burn(d, 2 * s) : Dodge(d, 2 * s - 1); break;
                case LayerBlendMode.LinearLight: v = d + 2 * s - 1; break;
                case LayerBlendMode.PinLight: v = s <= .5 ? Math.Min(d, 2 * s) : Math.Max(d, 2 * s - 1); break;
                case LayerBlendMode.HardMix: v = d + s >= 1 - TieMargin ? 1 : 0; break;
                case LayerBlendMode.Difference: v = Math.Abs(d - s); break;
                case LayerBlendMode.Exclusion: v = d + s - 2 * d * s; break;
                case LayerBlendMode.Subtract: v = d - s; break;
                case LayerBlendMode.Divide: v = s <= 0 ? (d <= 0 ? 0 : 1) : d / s; break;
                default: v = s; break;
            }
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }
        static double Dodge(double d, double s) { return d <= 0 ? 0 : s >= 1 ? 1 : Math.Min(1, d / (1 - s)); }
        static double Burn(double d, double s) { return d >= 1 ? 1 : s <= 0 ? 0 : 1 - Math.Min(1, (1 - d) / s); }
        static double Lum(double r, double g, double b) { return .3 * r + .59 * g + .11 * b; }
        static double Sat(double r, double g, double b) { return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)); }
        static void SetLum((double r, double g, double b) c, double l, out double r, out double g, out double b)
        {
            double delta = l - Lum(c.r, c.g, c.b);
            r = c.r + delta; g = c.g + delta; b = c.b + delta;
            // ClipColor
            double lum = Lum(r, g, b), n = Math.Min(r, Math.Min(g, b)), x = Math.Max(r, Math.Max(g, b));
            if (n < 0 && lum - n > 1e-12) { r = lum + (r - lum) * lum / (lum - n); g = lum + (g - lum) * lum / (lum - n); b = lum + (b - lum) * lum / (lum - n); }
            if (x > 1 && x - lum > 1e-12) { r = lum + (r - lum) * (1 - lum) / (x - lum); g = lum + (g - lum) * (1 - lum) / (x - lum); b = lum + (b - lum) * (1 - lum) / (x - lum); }
            r = Clamp01(r); g = Clamp01(g); b = Clamp01(b);
        }
        static (double r, double g, double b) SetSat(double r, double g, double b, double s)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max - min <= 1e-12) return (0, 0, 0);
            double Scale(double c) { return c == max ? s : c == min ? 0 : (c - min) * s / (max - min); }
            // 最大と最小が同じ成分を指すことは上で除いた。中間の成分は比で写す。
            return (Scale(r), Scale(g), Scale(b));
        }
        static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        /// <summary>Composites the colour of a clipped layer onto its clipping group: the clipped colour is combined with the
        /// group colour by the blend mode and mixed in by the clipped pixel's alpha × amount (opacity × mask). The group keeps
        /// its alpha (the base's), so a clipped layer never paints outside the base.</summary>
        public static Rgba32 ClipOnto(Rgba32 group, Rgba32 clipped, double amount, LayerBlendMode mode)
        {
            double t = clipped.A / 255.0 * amount;
            if (t <= 0 || group.A == 0) return group;
            return MixRgb(group, clipped, t, mode);
        }
        /// <summary>below + (blend(below, over) − below) × amount per component, rounded once; the alpha stays below's.</summary>
        internal static Rgba32 MixRgb(Rgba32 below, Rgba32 over, double amount, LayerBlendMode mode)
        {
            double dr = below.R / 255.0, dg = below.G / 255.0, db = below.B / 255.0;
            BlendRgb(mode, dr, dg, db, over.R / 255.0, over.G / 255.0, over.B / 255.0, out double r, out double g, out double b);
            return new Rgba32(MathUtil.ToByte(dr + (r - dr) * amount), MathUtil.ToByte(dg + (g - dg) * amount), MathUtil.ToByte(db + (b - db) * amount), below.A);
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
