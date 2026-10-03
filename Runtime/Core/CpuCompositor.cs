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
        internal static Rgba32 BlendUnchecked(Rgba32 destination, Rgba32 source, double opacity, LayerBlendMode mode)
        {
            // 不透明な画素を通常・量 1 で重ねると、式の結果は下の色と不透明度によらず上の画素そのもの（下の 256³ 通りの組を
            // 全部確かめた: 各成分は下の成分と下の不透明度だけに依る）
            if (source.A == 255 && opacity == 1 && (mode == LayerBlendMode.Normal || mode == LayerBlendMode.PassThrough)) return source;
            var unit = MathUtil.ByteUnit; // b / 255.0
            double sa = unit[source.A] * opacity, da = unit[destination.A];
            if (sa <= 0) return destination;
            double a = sa + da * (1 - sa);
            if (a <= 0) return Rgba32.Transparent;
            double dr = unit[destination.R], dg = unit[destination.G], db = unit[destination.B];
            double sr = unit[source.R], sg = unit[source.G], sb = unit[source.B];
            double br, bg, bb;
            if (mode == LayerBlendMode.Normal || mode == LayerBlendMode.PassThrough) { br = sr; bg = sg; bb = sb; }
            else BlendRgb(mode, dr, dg, db, sr, sg, sb, out br, out bg, out bb);
            // W3C separable blend + source-over; nonoverlapping source retains its own color. The weights are the same
            // products as (1 − sa)·da·c, (1 − da)·sa·c and da·sa·c evaluated left to right, computed once for the three channels.
            double wd = (1 - sa) * da, ws = (1 - da) * sa, wb = da * sa;
            return new Rgba32(MathUtil.ToByte((wd * dr + ws * sr + wb * br) / a), MathUtil.ToByte((wd * dg + ws * sg + wb * bg) / a),
                MathUtil.ToByte((wd * db + ws * sb + wb * bb) / a), MathUtil.ToByte(a));
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
            double t = MathUtil.ByteUnit[clipped.A] * amount;
            if (t <= 0 || group.A == 0) return group;
            return MixRgb(group, clipped, t, mode);
        }
        /// <summary>below + (blend(below, over) − below) × amount per component, rounded once; the alpha stays below's.</summary>
        internal static Rgba32 MixRgb(Rgba32 below, Rgba32 over, double amount, LayerBlendMode mode)
        {
            var unit = MathUtil.ByteUnit;
            double dr = unit[below.R], dg = unit[below.G], db = unit[below.B];
            BlendRgb(mode, dr, dg, db, unit[over.R], unit[over.G], unit[over.B], out double r, out double g, out double b);
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
            var unit = MathUtil.ByteUnit;
            double ba = unit[backdrop.A] * (1 - amount), ia = unit[inner.A] * amount, a = ba + ia;
            if (a <= 0) return Rgba32.Transparent;
            return new Rgba32(MathUtil.ToByte((unit[backdrop.R] * ba + unit[inner.R] * ia) / a), MathUtil.ToByte((unit[backdrop.G] * ba + unit[inner.G] * ia) / a),
                MathUtil.ToByte((unit[backdrop.B] * ba + unit[inner.B] * ia) / a), MathUtil.ToByte(a));
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
                Rgba32 group = layer.IsGroup ? EvaluatePixel(entry.Children, Rgba32.Transparent, channel, x, y) : layer.GetOutputPixel(channel, x, y);
                foreach (var clip in entry.ClipEntries)
                {
                    var c = clip.Base;
                    double clipAmount = c.Opacity * (c.Mask == null ? 1 : c.Mask.FactorAt(x, y));
                    if (c.Kind == LayerKind.Adjustment) group = c.Adjustment.Composite(group, clipAmount, c.BlendMode);
                    else group = StackClip(normal, group, c.IsGroup ? EvaluatePixel(clip.Children, Rgba32.Transparent, channel, x, y) : c.GetOutputPixel(channel, x, y), clipAmount, ModeOf(c));
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
            document.PollGeneratorInputs();
            return EvaluatePixel(Plan(document, channel), Rgba32.Transparent, channel, x, y);
        }
        /// <summary>Explicit full-frame reference output; use CompositeRegion/CompositePixel for narrow inspection.
        /// It deliberately does not allocate persistent full-frame buffers per layer.</summary>
        public static byte[] Composite(PaintDocument document, PaintChannel channel)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            return CompositeRegion(document, channel, 0, 0, document.Width, document.Height);
        }
        /// <summary>Per-layer tile buffers for the tiles of the plan being composited at once (one set per slot).</summary>
        sealed class LayerTile
        {
            public readonly PaintLayer Layer; public readonly byte[][] Pixels, Mask; public readonly bool[] Present; public readonly RasterMask MaskSource;
            readonly double opacity; readonly double[] maskFactor;
            public LayerTile(PaintLayer layer, int tileBytes, int slots)
            {
                Layer = layer; opacity = layer.Opacity; Present = new bool[slots];
                if (layer.Kind != LayerKind.Adjustment && !layer.IsGroup) { Pixels = new byte[slots][]; for (int k = 0; k < slots; k++) Pixels[k] = new byte[tileBytes]; }
                // A neutral mask multiplies by exactly 1, so skipping it is exact.
                if (layer.Mask != null && !layer.Mask.IsNeutral)
                {
                    MaskSource = layer.Mask; Mask = new byte[slots][]; for (int k = 0; k < slots; k++) Mask[k] = new byte[tileBytes];
                    maskFactor = new double[256]; for (int h = 0; h < 256; h++) maskFactor[h] = MaskSource.Factor((byte)h); // the same values Factor gives per pixel
                }
            }
            public void Load(PaintChannel channel, TileCoord coord, int slot)
            {
                // フィルターのある層は、フィルターを通した画素（halo を含めて評価した派生物。正本は変えない）
                Present[slot] = Layer.Kind == LayerKind.Adjustment || Layer.CopyOutputTile(channel, coord, Pixels[slot]);
                LoadMask(coord, slot);
            }
            public void LoadMask(TileCoord coord, int slot) { if (Present[slot] && MaskSource != null) MaskSource.CopyOutputTile(coord, Mask[slot]); }
            public double Amount(int slot, int offset) { return MaskSource == null ? opacity : opacity * maskFactor[Mask[slot][offset + 3]]; }
            /// <summary>True when reading the tile goes through a filter stack (the filter engine's cache is single-threaded).</summary>
            public bool Filtered(PaintChannel channel) { return (Pixels != null && Layer.HasActiveFilters(channel)) || (MaskSource != null && MaskSource.HasActiveFilters); }
        }
        /// <summary>The plan with tile buffers per layer, mirroring StackEntry. Per-layer facts the pixel loop asks for are read once.</summary>
        sealed class Node
        {
            public StackEntry Entry; public LayerTile Tile; public Node[] Children, Clips;
            public bool Adjustment, Group, PassesThrough; public LayerBlendMode Mode, AdjustmentMode; public AdjustmentSettings Settings;
            public static Node[] Build(System.Collections.Generic.IReadOnlyList<StackEntry> plan, int tileBytes, int slots)
            {
                var nodes = new Node[plan.Count];
                for (int i = 0; i < plan.Count; i++)
                {
                    var e = plan[i]; var layer = e.Base;
                    nodes[i] = new Node
                    {
                        Entry = e, Tile = new LayerTile(layer, tileBytes, slots), Children = Build(e.Children, tileBytes, slots), Clips = Build(e.ClipEntries, tileBytes, slots),
                        Adjustment = layer.Kind == LayerKind.Adjustment, Group = layer.IsGroup, PassesThrough = e.PassesThrough,
                        Mode = ModeOf(layer), AdjustmentMode = layer.BlendMode, Settings = layer.Adjustment,
                    };
                }
                return nodes;
            }
            /// <summary>Loads the tile for every node into slot; returns true when any raster or fill pixels are present below it.</summary>
            public static bool Load(Node[] nodes, PaintChannel channel, TileCoord coord, int slot)
            {
                bool any = false;
                foreach (var n in nodes)
                {
                    bool pixels;
                    if (n.Group) { pixels = Load(n.Children, channel, coord, slot); n.Tile.Present[slot] = pixels || HasAdjustment(n.Children, slot); n.Tile.LoadMask(coord, slot); }
                    else { n.Tile.Load(channel, coord, slot); pixels = !n.Adjustment && n.Tile.Present[slot]; }
                    if (n.Tile.Present[slot]) pixels |= Load(n.Clips, channel, coord, slot);
                    any |= pixels;
                }
                return any;
            }
            static bool HasAdjustment(Node[] nodes, int slot) { foreach (var n in nodes) if (n.Tile.Present[slot] && n.Adjustment || n.Group && n.Tile.Present[slot]) return true; return false; }
            public static bool Filtered(Node[] nodes, PaintChannel channel) { foreach (var n in nodes) if (n.Tile.Filtered(channel) || Filtered(n.Children, channel) || Filtered(n.Clips, channel)) return true; return false; }
        }
        /// <summary>Tile variant of EvaluatePixel (it replaced the per-pixel EvaluateTile): composites count pixels of one tile row
        /// (tile byte offset src in slot) onto res at resOff, which holds the backdrop. Node by node over the span instead of pixel
        /// by pixel; every pixel goes through the same arithmetic in the same order as EvaluatePixel. Groups and clipping use
        /// scratch spans per depth.</summary>
        static void EvaluateSpan(Node[] nodes, byte[] res, int resOff, int slot, int src, int count, bool normal, SpanScratch scratch, int depth)
        {
            foreach (var n in nodes)
            {
                var b = n.Tile; if (!b.Present[slot]) continue;
                if (n.Adjustment)
                {
                    for (int i = 0, r = resOff, t = src; i < count; i++, r += 4, t += 4) Write(res, r, n.Settings.Composite(Read(res, r), b.Amount(slot, t), n.AdjustmentMode));
                    continue;
                }
                if (n.PassesThrough)
                {
                    var inner = scratch.Get(depth, 0); Buffer.BlockCopy(res, resOff, inner, 0, count * 4);
                    EvaluateSpan(n.Children, inner, 0, slot, src, count, normal, scratch, depth + 1);
                    for (int i = 0, r = resOff, t = src; i < count; i++, r += 4, t += 4) Write(res, r, StackFade(normal, Read(res, r), Read(inner, i * 4), b.Amount(slot, t)));
                    continue;
                }
                byte[] g; int gOff;
                if (n.Group)
                {
                    g = scratch.Get(depth, 1); gOff = 0; Array.Clear(g, 0, count * 4);
                    EvaluateSpan(n.Children, g, 0, slot, src, count, normal, scratch, depth + 1);
                }
                else if (n.Clips.Length > 0) { g = scratch.Get(depth, 1); gOff = 0; Buffer.BlockCopy(b.Pixels[slot], src, g, 0, count * 4); }
                else { g = b.Pixels[slot]; gOff = src; } // read in place (nothing changes the layer's own pixels)
                foreach (var clip in n.Clips)
                {
                    var ct = clip.Tile; if (!ct.Present[slot]) continue;
                    if (clip.Adjustment)
                    {
                        for (int i = 0, t = src; i < count; i++, t += 4) Write(g, gOff + i * 4, clip.Settings.Composite(Read(g, gOff + i * 4), ct.Amount(slot, t), clip.AdjustmentMode));
                        continue;
                    }
                    byte[] c; int cOff;
                    if (clip.Group) { c = scratch.Get(depth, 2); cOff = 0; Array.Clear(c, 0, count * 4); EvaluateSpan(clip.Children, c, 0, slot, src, count, normal, scratch, depth + 1); }
                    else { c = ct.Pixels[slot]; cOff = src; }
                    for (int i = 0, t = src; i < count; i++, t += 4)
                        Write(g, gOff + i * 4, StackClip(normal, Read(g, gOff + i * 4), Read(c, cOff + i * 4), ct.Amount(slot, t), clip.Mode));
                }
                if (normal)
                    for (int i = 0, r = resOff, t = src; i < count; i++, r += 4, t += 4) Write(res, r, NormalMaps.BlendUnchecked(Read(res, r), Read(g, gOff + i * 4), b.Amount(slot, t), n.Mode));
                else BlendSpan(res, resOff, g, gOff, count, b, slot, src, n.Mode);
            }
        }
        static Rgba32 Read(byte[] a, int o) { return new Rgba32(a[o], a[o + 1], a[o + 2], a[o + 3]); }
        static void Write(byte[] a, int o, Rgba32 c) { a[o] = c.R; a[o + 1] = c.G; a[o + 2] = c.B; a[o + 3] = c.A; }

        /// <summary><see cref="BlendUnchecked"/> over a span, reading and writing the bytes in place (the same arithmetic in the same
        /// order for every pixel; the editor's Mono runs this about twice as fast as passing each pixel as an Rgba32).</summary>
        static void BlendSpan(byte[] res, int resOff, byte[] src, int srcOff, int count, LayerTile layer, int slot, int tileOff, LayerBlendMode mode)
        {
            var unit = MathUtil.ByteUnit; bool simple = mode == LayerBlendMode.Normal || mode == LayerBlendMode.PassThrough;
            // 分離できるモードは BlendRgb の既定の枝（成分ごとの Separable）を直接呼ぶ
            bool separable = !simple && mode != LayerBlendMode.Hue && mode != LayerBlendMode.Saturation && mode != LayerBlendMode.Color && mode != LayerBlendMode.Luminosity
                && mode != LayerBlendMode.DarkerColor && mode != LayerBlendMode.LighterColor;
            for (int i = 0; i < count; i++)
            {
                int r = resOff + i * 4, s = srcOff + i * 4;
                double amount = layer.Amount(slot, tileOff + i * 4);
                if (simple && src[s + 3] == 255 && amount == 1) { res[r] = src[s]; res[r + 1] = src[s + 1]; res[r + 2] = src[s + 2]; res[r + 3] = 255; continue; } // BlendUnchecked と同じ近道
                double sa = unit[src[s + 3]] * amount, da = unit[res[r + 3]];
                if (sa <= 0) continue; // the destination as it is
                double a = sa + da * (1 - sa);
                if (a <= 0) { res[r] = 0; res[r + 1] = 0; res[r + 2] = 0; res[r + 3] = 0; continue; }
                double dr = unit[res[r]], dg = unit[res[r + 1]], db = unit[res[r + 2]];
                double sr = unit[src[s]], sg = unit[src[s + 1]], sb = unit[src[s + 2]];
                double br, bg, bb;
                if (simple) { br = sr; bg = sg; bb = sb; }
                else if (separable) { br = Separable(mode, dr, sr); bg = Separable(mode, dg, sg); bb = Separable(mode, db, sb); }
                else BlendRgb(mode, dr, dg, db, sr, sg, sb, out br, out bg, out bb);
                double wd = (1 - sa) * da, ws = (1 - da) * sa, wb = da * sa;
                // MathUtil.ToByte written out (x × 255 + 0.5, floor by truncation of a positive value, clamped)
                double vr = (wd * dr + ws * sr + wb * br) / a * 255 + 0.5, vg = (wd * dg + ws * sg + wb * bg) / a * 255 + 0.5;
                double vb = (wd * db + ws * sb + wb * bb) / a * 255 + 0.5, va = a * 255 + 0.5;
                res[r] = vr >= 255 ? (byte)255 : vr > 0 ? (byte)(int)vr : (byte)0;
                res[r + 1] = vg >= 255 ? (byte)255 : vg > 0 ? (byte)(int)vg : (byte)0;
                res[r + 2] = vb >= 255 ? (byte)255 : vb > 0 ? (byte)(int)vb : (byte)0;
                res[r + 3] = va >= 255 ? (byte)255 : va > 0 ? (byte)(int)va : (byte)0;
            }
        }
        /// <summary>Scratch spans of one worker: per group depth, the pass-through backdrop copy, the group or clipping base, and a
        /// clipped group.</summary>
        sealed class SpanScratch
        {
            readonly System.Collections.Generic.List<byte[]> spans = new System.Collections.Generic.List<byte[]>(); readonly int length;
            public SpanScratch(int tileSize) { length = tileSize * 4; }
            public byte[] Get(int depth, int purpose)
            {
                int index = depth * 3 + purpose;
                while (spans.Count <= index) spans.Add(null);
                return spans[index] ?? (spans[index] = new byte[length]);
            }
        }
        /// <summary>Tile buffers held at once by one CompositeRegion call when it composites several tiles in parallel.</summary>
        const long ParallelBufferBytes = 64L * 1024 * 1024;
        /// <summary>Below this many pixel × layer steps a region is composited on the calling thread (starting workers costs more).</summary>
        const long ParallelMinimumWork = 1L << 16;
        public static byte[] CompositeRegion(PaintDocument document, PaintChannel channel, int x, int y, int width, int height)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            PaintLayer.ValidateChannel(channel);
            if (width < 0 || height < 0 || x < 0 || y < 0 || (long)x + width > document.Width || (long)y + height > document.Height)
                throw new ArgumentOutOfRangeException("region");
            var bytes = new byte[checked(width * height * 4)];
            if (width == 0 || height == 0) return bytes;
            document.PollGeneratorInputs(); // Generator が読むメッシュマップが変わっていれば、新しいマップで合成する
            // Same per-pixel arithmetic as CompositePixel, but each layer's tile is read once instead of one dictionary
            // lookup per pixel per layer. Tiles are independent: several are loaded into their own buffers (slots) and their
            // pixels computed on worker threads; every output pixel depends only on its own inputs, so the bytes do not depend
            // on the number of threads or the order.
            int tile = document.TileSize, tileBytes = checked(tile * tile * 4);
            var plan = Plan(document, channel);
            var coords = new System.Collections.Generic.List<TileCoord>();
            for (int ty = y / tile; ty <= (y + height - 1) / tile; ty++)
                for (int tx = x / tile; tx <= (x + width - 1) / tile; tx++) coords.Add(new TileCoord(tx, ty));
            int degree = CoreParallelism.Degree, layerCount = Math.Max(1, CountEntries(plan));
            if ((long)width * height * layerCount < ParallelMinimumWork) degree = 1;
            int buffers = Math.Max(1, CountBuffers(plan));
            int slots = (int)Math.Max(1, Math.Min(Math.Min(coords.Count, degree), ParallelBufferBytes / ((long)buffers * tileBytes)));
            var nodes = Node.Build(plan, tileBytes, slots);
            // 読み込みもワーカーで: フィルターを通る層が無ければ、読むのは面のタイルの写しだけ（書き換えは無い）。フィルターの
            // キャッシュはこのスレッドからだけ触る。
            bool concurrentLoad = slots > 1 && !Node.Filtered(nodes, channel);
            bool normal = channel == PaintChannel.Normal;
            var any = new bool[slots];
            for (int start = 0; start < coords.Count; start += slots)
            {
                int count = Math.Min(slots, coords.Count - start), first = start;
                // Nothing with pixels in a tile: the result stays transparent, and adjustments leave transparent pixels alone.
                if (concurrentLoad) CoreParallelism.For(count, degree, k => any[k] = Node.Load(nodes, channel, coords[first + k], k));
                else for (int k = 0; k < count; k++) any[k] = Node.Load(nodes, channel, coords[first + k], k);
                // 行の束に分けて計算する（タイルが少ないときもスレッドが余らないように）
                int chunks = Math.Max(1, Math.Min(tile / 8, (degree + count - 1) / count)), rowsPerChunk = (tile + chunks - 1) / chunks;
                CoreParallelism.For(count * chunks, degree, item =>
                {
                    int k = item / chunks, chunk = item - k * chunks; if (!any[k]) return;
                    var c = coords[first + k]; int tx = c.X, ty = c.Y;
                    int x0 = Math.Max(x, tx * tile), x1 = Math.Min(x + width, (tx + 1) * tile);
                    int y0 = Math.Max(y, Math.Max(ty * tile, ty * tile + chunk * rowsPerChunk)), y1 = Math.Min(y + height, Math.Min((ty + 1) * tile, ty * tile + (chunk + 1) * rowsPerChunk));
                    var scratch = new SpanScratch(tile);
                    // 出力は透明（0）から始まるので、そのまま背景として行ごとに重ねる
                    for (int py = y0; py < y1; py++)
                        EvaluateSpan(nodes, bytes, ((py - y) * width + (x0 - x)) * 4, k, ((py - ty * tile) * tile + (x0 - tx * tile)) * 4, x1 - x0, normal, scratch, 0);
                });
            }
            return bytes;
        }
        static int CountEntries(System.Collections.Generic.IReadOnlyList<StackEntry> plan)
        { int c = 0; foreach (var e in plan) c += 1 + CountEntries(e.Children) + CountEntries(e.ClipEntries); return c; }
        static int CountBuffers(System.Collections.Generic.IReadOnlyList<StackEntry> plan)
        {
            int b = 0;
            foreach (var e in plan)
            {
                var l = e.Base;
                b += (l.Kind != LayerKind.Adjustment && !l.IsGroup ? 1 : 0) + (l.Mask != null && !l.Mask.IsNeutral ? 1 : 0) + CountBuffers(e.Children) + CountBuffers(e.ClipEntries);
            }
            return b;
        }
    }
}
