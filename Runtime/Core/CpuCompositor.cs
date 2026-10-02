using System;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Deterministic RGBA8 reference blend in stored RGB space. This does not claim ICC-managed PSD
    /// equivalence. Alpha is always linear coverage. The Normal channel composites as tangent-space unit vectors with the
    /// same structure (<see cref="NormalMaps"/>).</summary>
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
                case LayerBlendMode.Normal: case LayerBlendMode.PassThrough: r = sr; g = sg; b = sb; return;
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

        /// <summary>One step of the layer stack for a channel: an unclipped layer (or group) and the active layers clipped to
        /// it, bottom to top. For a group, Children is the plan of its contents.</summary>
        public sealed class StackEntry
        {
            readonly System.Collections.Generic.List<PaintLayer> clips = new System.Collections.Generic.List<PaintLayer>();
            readonly System.Collections.Generic.List<StackEntry> clipEntries = new System.Collections.Generic.List<StackEntry>();
            public PaintLayer Base { get; internal set; }
            public System.Collections.Generic.IReadOnlyList<PaintLayer> Clips { get { return clips; } }
            /// <summary>The clipped layers as entries (a clipped group carries its own Children).</summary>
            public System.Collections.Generic.IReadOnlyList<StackEntry> ClipEntries { get { return clipEntries; } }
            /// <summary>The plan of a group's contents; empty for other layers.</summary>
            public System.Collections.Generic.IReadOnlyList<StackEntry> Children { get; internal set; } = new StackEntry[0];
            internal void AddClip(StackEntry entry) { clips.Add(entry.Base); clipEntries.Add(entry); }
            /// <summary>A pass-through group without clipped layers: its children composite straight onto the backdrop.</summary>
            public bool PassesThrough { get { return Base.IsGroup && Base.BlendMode == LayerBlendMode.PassThrough && clipEntries.Count == 0; } }
        }
        static bool Active(PaintLayer layer, PaintChannel channel)
        { return layer.Visible && layer.Opacity > 0 && layer.IsChannelEnabled(channel) && layer.HasContent(channel); }
        /// <summary>Groups the layers into clipping groups for a channel, dropping what cannot show: a hidden (or empty in this
        /// channel) base hides its clipped layers too, and an adjustment base has no pixels to clip to. Groups recurse; a hidden
        /// group or one with nothing active inside is dropped with everything in it. Clipping only reaches siblings.</summary>
        public static System.Collections.Generic.List<StackEntry> Plan(PaintDocument document, PaintChannel channel)
        { return PlanLevel(document, channel, Guid.Empty); }
        static System.Collections.Generic.List<StackEntry> PlanLevel(PaintDocument document, PaintChannel channel, Guid parent)
        {
            var plan = new System.Collections.Generic.List<StackEntry>(); var layers = document.Layers;
            var siblings = new System.Collections.Generic.List<int>();
            for (int i = 0; i < layers.Count; i++) if (layers[i].ParentId == parent) siblings.Add(i);
            // 兄弟の中で、一番下でなくクリッピングの印があるものが「クリッピングされている」（IsEffectivelyClipped と同じ規則）
            for (int k = 0; k < siblings.Count; k++)
            {
                int i = siblings[k];
                if (k > 0 && layers[i].Clipping) continue;
                var entry = MakeEntry(document, channel, layers[i]); if (entry == null) continue;
                if (layers[i].Kind != LayerKind.Adjustment)
                    for (int m = k + 1; m < siblings.Count && layers[siblings[m]].Clipping; m++)
                    { var clip = MakeEntry(document, channel, layers[siblings[m]]); if (clip != null) entry.AddClip(clip); }
                plan.Add(entry);
            }
            return plan;
        }
        static StackEntry MakeEntry(PaintDocument document, PaintChannel channel, PaintLayer layer)
        {
            if (layer.IsGroup)
            {
                if (!layer.Visible || layer.Opacity <= 0) return null;
                var children = PlanLevel(document, channel, layer.Id);
                return children.Count == 0 ? null : new StackEntry { Base = layer, Children = children };
            }
            return Active(layer, channel) ? new StackEntry { Base = layer } : null;
        }
        static LayerBlendMode ModeOf(PaintLayer layer) { return layer.BlendMode == LayerBlendMode.PassThrough ? LayerBlendMode.Normal : layer.BlendMode; }
        // Normal チャンネルはベクトルとして合成する（同じ構造の NormalMaps の式）。他のチャンネルは色の式。
        static Rgba32 StackBlend(bool normal, Rgba32 below, Rgba32 over, double amount, LayerBlendMode mode)
        { return normal ? NormalMaps.BlendUnchecked(below, over, amount, mode) : BlendUnchecked(below, over, amount, mode); }
        static Rgba32 StackClip(bool normal, Rgba32 group, Rgba32 clipped, double amount, LayerBlendMode mode)
        { return normal ? NormalMaps.ClipOnto(group, clipped, amount, mode) : ClipOnto(group, clipped, amount, mode); }
        static Rgba32 StackFade(bool normal, Rgba32 backdrop, Rgba32 inner, double amount)
        { return normal ? NormalMaps.Fade(backdrop, inner, amount) : Fade(backdrop, inner, amount); }
        /// <summary>Fades between the backdrop and a pass-through group's result by amount (premultiplied interpolation, so a
        /// transparent side does not darken the other).</summary>
        internal static Rgba32 Fade(Rgba32 backdrop, Rgba32 inner, double amount)
        {
            if (amount >= 1) return inner;
            if (amount <= 0) return backdrop;
            double ba = backdrop.A / 255.0 * (1 - amount), ia = inner.A / 255.0 * amount, a = ba + ia;
            if (a <= 0) return Rgba32.Transparent;
            return new Rgba32(MathUtil.ToByte((backdrop.R / 255.0 * ba + inner.R / 255.0 * ia) / a), MathUtil.ToByte((backdrop.G / 255.0 * ba + inner.G / 255.0 * ia) / a),
                MathUtil.ToByte((backdrop.B / 255.0 * ba + inner.B / 255.0 * ia) / a), MathUtil.ToByte(a));
        }

        /// <summary>Per-pixel reference: composites a plan level over a backdrop. Isolated groups composite their contents from
        /// transparent and blend the result like a layer; pass-through groups composite their contents onto the backdrop and
        /// fade by the group's opacity × mask. A group with clipped layers is treated as isolated (Normal for pass-through).</summary>
        static Rgba32 EvaluatePixel(System.Collections.Generic.IReadOnlyList<StackEntry> plan, Rgba32 backdrop, PaintChannel channel, int x, int y)
        {
            Rgba32 result = backdrop; bool normal = channel == PaintChannel.Normal;
            foreach (var entry in plan)
            {
                var layer = entry.Base;
                double amount = layer.Opacity * (layer.Mask == null ? 1 : layer.Mask.FactorAt(x, y));
                if (layer.Kind == LayerKind.Adjustment) { result = layer.Adjustment.Composite(result, amount, layer.BlendMode); continue; }
                if (entry.PassesThrough) { result = StackFade(normal, result, EvaluatePixel(entry.Children, result, channel, x, y), amount); continue; }
                Rgba32 group = layer.IsGroup ? EvaluatePixel(entry.Children, Rgba32.Transparent, channel, x, y) : layer.GetPixel(channel, x, y);
                foreach (var clip in entry.ClipEntries)
                {
                    var c = clip.Base;
                    double clipAmount = c.Opacity * (c.Mask == null ? 1 : c.Mask.FactorAt(x, y));
                    if (c.Kind == LayerKind.Adjustment) group = c.Adjustment.Composite(group, clipAmount, c.BlendMode);
                    else group = StackClip(normal, group, c.IsGroup ? EvaluatePixel(clip.Children, Rgba32.Transparent, channel, x, y) : c.GetPixel(channel, x, y), clipAmount, ModeOf(c));
                }
                result = StackBlend(normal, result, group, amount, ModeOf(layer));
            }
            return result;
        }

        public static Rgba32 CompositePixel(PaintDocument document, PaintChannel channel, int x, int y)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            PaintLayer.ValidateChannel(channel);
            if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) throw new ArgumentOutOfRangeException("pixel");
            return EvaluatePixel(Plan(document, channel), Rgba32.Transparent, channel, x, y);
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
                if (layer.Kind != LayerKind.Adjustment && !layer.IsGroup) Pixels = new byte[tileBytes];
                // A neutral mask multiplies by exactly 1, so skipping it is exact.
                if (layer.Mask != null && !layer.Mask.IsNeutral) { MaskSource = layer.Mask; Mask = new byte[tileBytes]; }
            }
            public void Load(PaintChannel channel, TileCoord coord)
            {
                Present = Layer.Kind == LayerKind.Adjustment || Layer.CopyTile(channel, coord, Pixels);
                LoadMask(coord);
            }
            public void LoadMask(TileCoord coord) { if (Present && MaskSource != null) MaskSource.Surface.CopyTile(coord, Mask); }
            public double Amount(int offset) { return MaskSource == null ? Layer.Opacity : Layer.Opacity * MaskSource.Factor(Mask[offset + 3]); }
            public Rgba32 Pixel(int offset) { return new Rgba32(Pixels[offset], Pixels[offset + 1], Pixels[offset + 2], Pixels[offset + 3]); }
        }
        /// <summary>The plan with one tile buffer per layer, mirroring StackEntry.</summary>
        sealed class Node
        {
            public StackEntry Entry; public LayerTile Tile; public Node[] Children, Clips;
            public static Node[] Build(System.Collections.Generic.IReadOnlyList<StackEntry> plan, int tileBytes)
            {
                var nodes = new Node[plan.Count];
                for (int i = 0; i < plan.Count; i++)
                {
                    var e = plan[i];
                    nodes[i] = new Node { Entry = e, Tile = new LayerTile(e.Base, tileBytes), Children = Build(e.Children, tileBytes), Clips = Build(e.ClipEntries, tileBytes) };
                }
                return nodes;
            }
            /// <summary>Loads the tile for every node; returns true when any raster or fill pixels are present below it.</summary>
            public static bool Load(Node[] nodes, PaintChannel channel, TileCoord coord)
            {
                bool any = false;
                foreach (var n in nodes)
                {
                    bool pixels;
                    if (n.Entry.Base.IsGroup) { pixels = Load(n.Children, channel, coord); n.Tile.Present = pixels || HasAdjustment(n.Children); n.Tile.LoadMask(coord); }
                    else { n.Tile.Load(channel, coord); pixels = n.Entry.Base.Kind != LayerKind.Adjustment && n.Tile.Present; }
                    if (n.Tile.Present) pixels |= Load(n.Clips, channel, coord);
                    any |= pixels;
                }
                return any;
            }
            static bool HasAdjustment(Node[] nodes) { foreach (var n in nodes) if (n.Tile.Present && n.Entry.Base.Kind == LayerKind.Adjustment || n.Entry.Base.IsGroup && n.Tile.Present) return true; return false; }
        }
        /// <summary>Tile variant of EvaluatePixel (the same arithmetic in the same order).</summary>
        static Rgba32 EvaluateTile(Node[] nodes, Rgba32 backdrop, int offset, bool normal)
        {
            Rgba32 result = backdrop;
            foreach (var n in nodes)
            {
                var b = n.Tile; if (!b.Present) continue;
                var layer = b.Layer; double amount = b.Amount(offset);
                if (layer.Kind == LayerKind.Adjustment) { result = layer.Adjustment.Composite(result, amount, layer.BlendMode); continue; }
                if (n.Entry.PassesThrough) { result = StackFade(normal, result, EvaluateTile(n.Children, result, offset, normal), amount); continue; }
                Rgba32 group = layer.IsGroup ? EvaluateTile(n.Children, Rgba32.Transparent, offset, normal) : b.Pixel(offset);
                foreach (var clip in n.Clips)
                {
                    if (!clip.Tile.Present) continue;
                    var c = clip.Tile.Layer; double clipAmount = clip.Tile.Amount(offset);
                    if (c.Kind == LayerKind.Adjustment) group = c.Adjustment.Composite(group, clipAmount, c.BlendMode);
                    else group = StackClip(normal, group, c.IsGroup ? EvaluateTile(clip.Children, Rgba32.Transparent, offset, normal) : clip.Tile.Pixel(offset), clipAmount, ModeOf(c));
                }
                result = StackBlend(normal, result, group, amount, ModeOf(layer));
            }
            return result;
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
            var nodes = Node.Build(Plan(document, channel), tileBytes);
            for (int ty = y / tile; ty <= (y + height - 1) / tile; ty++)
                for (int tx = x / tile; tx <= (x + width - 1) / tile; tx++)
                {
                    var coord = new TileCoord(tx, ty);
                    // Nothing with pixels here: the result stays transparent, and adjustments leave transparent pixels alone.
                    if (!Node.Load(nodes, channel, coord)) continue;
                    int x0 = Math.Max(x, tx * tile), x1 = Math.Min(x + width, (tx + 1) * tile);
                    int y0 = Math.Max(y, ty * tile), y1 = Math.Min(y + height, (ty + 1) * tile);
                    for (int py = y0; py < y1; py++) for (int px = x0; px < x1; px++)
                    {
                        int source = ((py - ty * tile) * tile + (px - tx * tile)) * 4;
                        var result = EvaluateTile(nodes, Rgba32.Transparent, source, channel == PaintChannel.Normal);
                        int offset = ((py - y) * width + (px - x)) * 4;
                        bytes[offset] = result.R; bytes[offset + 1] = result.G; bytes[offset + 2] = result.B; bytes[offset + 3] = result.A;
                    }
                }
            return bytes;
        }
    }
}
