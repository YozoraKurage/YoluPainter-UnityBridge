using System;
using System.Collections.Generic;

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
            double tr, tg, tb;
            switch (mode)
            {
                case LayerBlendMode.Normal: case LayerBlendMode.PassThrough: r = sr; g = sg; b = sb; return;
                case LayerBlendMode.Hue: SetSat(sr, sg, sb, Sat(dr, dg, db), out tr, out tg, out tb); SetLum(tr, tg, tb, Lum(dr, dg, db), out r, out g, out b); return;
                case LayerBlendMode.Saturation: SetSat(dr, dg, db, Sat(sr, sg, sb), out tr, out tg, out tb); SetLum(tr, tg, tb, Lum(dr, dg, db), out r, out g, out b); return;
                case LayerBlendMode.Color: SetLum(sr, sg, sb, Lum(dr, dg, db), out r, out g, out b); return;
                case LayerBlendMode.Luminosity: SetLum(dr, dg, db, Lum(sr, sg, sb), out r, out g, out b); return;
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
        // 色相・彩度・カラー・輝度の式。比べる値はどれも NaN ではなく −0 も入らない（成分は b/255 か、それに差を足したもの・Scale の
        // 結果で、−0 にはならない）ので、Math.Min / Max を比較に書き換えても同じ値（Mono は Math.Min / Max を呼び出しにする）。
        static double Sat(double r, double g, double b)
        {
            double max = g > b ? g : b; if (r > max) max = r;
            double min = g < b ? g : b; if (r < min) min = r;
            return max - min;
        }
        /// <summary>W3C SetLum followed by ClipColor and a clamp to 0..1.</summary>
        static void SetLum(double cr, double cg, double cb, double l, out double r, out double g, out double b)
        {
            double delta = l - Lum(cr, cg, cb);
            r = cr + delta; g = cg + delta; b = cb + delta;
            // ClipColor
            double lum = Lum(r, g, b), n = g < b ? g : b, x = g > b ? g : b;
            if (r < n) n = r; if (r > x) x = r;
            if (n < 0 && lum - n > 1e-12) { r = lum + (r - lum) * lum / (lum - n); g = lum + (g - lum) * lum / (lum - n); b = lum + (b - lum) * lum / (lum - n); }
            if (x > 1 && x - lum > 1e-12) { r = lum + (r - lum) * (1 - lum) / (x - lum); g = lum + (g - lum) * (1 - lum) / (x - lum); b = lum + (b - lum) * (1 - lum) / (x - lum); }
            r = Clamp01(r); g = Clamp01(g); b = Clamp01(b);
        }
        /// <summary>W3C SetSat: the largest component becomes s, the smallest 0, the middle one keeps its ratio.</summary>
        static void SetSat(double r, double g, double b, double s, out double or, out double og, out double ob)
        {
            double max = g > b ? g : b; if (r > max) max = r;
            double min = g < b ? g : b; if (r < min) min = r;
            if (max - min <= 1e-12) { or = 0; og = 0; ob = 0; return; }
            // 最大と最小が同じ成分を指すことは上で除いた。中間の成分は比で写す。
            or = r == max ? s : r == min ? 0 : (r - min) * s / (max - min);
            og = g == max ? s : g == min ? 0 : (g - min) * s / (max - min);
            ob = b == max ? s : b == min ? 0 : (b - min) * s / (max - min);
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

        /// <summary>One region for <see cref="CompositeRegions"/>: where, the backdrop it starts from, where the result goes and an optional
        /// copy of an intermediate result. Entry indices count the top-level entries of <see cref="Plan"/> for the document and channel at
        /// the time of the call.</summary>
        public sealed class CompositeJob
        {
            /// <param name="pixels">At least width × height × 4 bytes, rows packed from the lowest (row r at r × width × 4). On entry the
            /// composite of plan entries [0, start) — ignored when start is 0 or a backdrop is given — and on return the composite of every
            /// entry.</param>
            /// <param name="start">The first plan entry to composite onto the backdrop (0 = from transparent).</param>
            /// <param name="captureAt">−1, or start..entries: <paramref name="capture"/> then receives the composite of entries [0, captureAt)
            /// in the same layout, taken on the way.</param>
            public CompositeJob(int x, int y, int width, int height, byte[] pixels, int start = 0, int captureAt = -1, byte[] capture = null)
            { X = x; Y = y; Width = width; Height = height; Pixels = pixels; Start = start; CaptureAt = captureAt; Capture = capture; }
            /// <summary>A job whose backdrop (the composite of entries [0, start)) is read from another array instead of being placed in the
            /// pixels first: row r of the region at backdrop[backdropOffset + r × backdropStride] (the workers copy it, so a large backdrop
            /// costs no time on the calling thread). The backdrop is only read; several jobs may share it, but it must not be a job's pixels
            /// or capture.</summary>
            public CompositeJob(int x, int y, int width, int height, byte[] pixels, int start, byte[] backdrop, int backdropOffset, int backdropStride, int captureAt = -1, byte[] capture = null)
                : this(x, y, width, height, pixels, start, captureAt, capture)
            { Backdrop = backdrop; BackdropOffset = backdropOffset; BackdropStride = backdropStride; }
            public int X { get; }
            public int Y { get; }
            public int Width { get; }
            public int Height { get; }
            public byte[] Pixels { get; }
            public int Start { get; }
            public int CaptureAt { get; }
            public byte[] Capture { get; }
            public byte[] Backdrop { get; }
            public int BackdropOffset { get; }
            public int BackdropStride { get; }
        }

        /// <summary>Composites several regions in one pass, each from its own backdrop: a job's pixels hold the composite of the plan
        /// entries below its Start, and only the entries from Start up are composited onto them (a display cache keeps that lower part
        /// and redraws only what changed above it). Optionally a job also keeps the composite below CaptureAt. Every pixel is the same
        /// as <see cref="CompositeRegion"/> gives when the backdrop is right; the bytes do not depend on the number of threads. Jobs
        /// may overlap, but no array may be used twice (as two jobs' pixels, or as a job's pixels and capture).</summary>
        public static void CompositeRegions(PaintDocument document, PaintChannel channel, IReadOnlyList<CompositeJob> jobs)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var plan = CheckJobs(document, channel, jobs, document.Width, document.Height);
            Run(document, channel, plan, jobs, false, 1);
        }

        /// <summary><see cref="CompositeRegions"/> on the grid that keeps one pixel of every step × step square: sampled pixel (sx, sy) is
        /// exactly the composite at (sx·step + step/2, sy·step + step/2) — every operation is per pixel and filters are evaluated at full
        /// size before the sample is taken (a cheap preview of a large canvas, 1/step² of the work of compositing it all). Job regions,
        /// pixels and backdrops are in the sampled grid, (width / step) × (height / step). step must divide the tile size and the
        /// document's width and height.</summary>
        public static void CompositeSampledRegions(PaintDocument document, PaintChannel channel, int step, IReadOnlyList<CompositeJob> jobs)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (step < 1 || document.TileSize % step != 0 || document.Width % step != 0 || document.Height % step != 0)
                throw new ArgumentOutOfRangeException(nameof(step), "The step must divide the tile size and the document's width and height.");
            var plan = CheckJobs(document, channel, jobs, document.Width / step, document.Height / step);
            Run(document, channel, plan, jobs, false, step);
        }

        /// <summary>Checks jobs for a grid of width × height, looks at the generator inputs and returns the plan.</summary>
        static List<StackEntry> CheckJobs(PaintDocument document, PaintChannel channel, IReadOnlyList<CompositeJob> jobs, int width, int height)
        {
            PaintLayer.ValidateChannel(channel);
            if (jobs == null) throw new ArgumentNullException(nameof(jobs));
            var arrays = new HashSet<byte[]>(ReferenceComparer.Instance);
            foreach (var job in jobs)
            {
                if (job == null) throw new ArgumentNullException(nameof(jobs), "A job is null.");
                if (job.Width < 0 || job.Height < 0 || job.X < 0 || job.Y < 0 || (long)job.X + job.Width > width || (long)job.Y + job.Height > height)
                    throw new ArgumentOutOfRangeException(nameof(jobs), "A job's region is outside the document.");
                long bytes = (long)job.Width * job.Height * 4;
                if (job.Pixels == null || job.Pixels.Length < bytes) throw new ArgumentException("A job's pixels are missing or too short.", nameof(jobs));
                if (!arrays.Add(job.Pixels)) throw new ArgumentException("An array is used by more than one job.", nameof(jobs));
                if (job.CaptureAt == -1) { if (job.Capture != null) throw new ArgumentException("A capture array without CaptureAt.", nameof(jobs)); }
                else
                {
                    if (job.CaptureAt < job.Start) throw new ArgumentOutOfRangeException(nameof(jobs), "CaptureAt is below Start.");
                    if (job.Capture == null || job.Capture.Length < bytes) throw new ArgumentException("A job's capture array is missing or too short.", nameof(jobs));
                    if (!arrays.Add(job.Capture)) throw new ArgumentException("An array is used by more than one job.", nameof(jobs));
                }
                if (job.Start < 0) throw new ArgumentOutOfRangeException(nameof(jobs), "Start is negative.");
            }
            foreach (var job in jobs)
                if (job.Backdrop != null)
                {
                    if (job.Start == 0) throw new ArgumentException("A backdrop is given for a job that starts from transparent.", nameof(jobs));
                    if (arrays.Contains(job.Backdrop)) throw new ArgumentException("A backdrop is also a job's pixels or capture.", nameof(jobs));
                    if (job.Height > 0 && (job.BackdropOffset < 0 || job.BackdropStride < job.Width * 4 || (long)job.BackdropOffset + (long)(job.Height - 1) * job.BackdropStride + job.Width * 4L > job.Backdrop.Length))
                        throw new ArgumentOutOfRangeException(nameof(jobs), "The backdrop's rows are outside its array.");
                }
            document.PollGeneratorInputs(); // Generator が読むメッシュマップが変わっていれば、新しいマップで合成する
            var plan = Plan(document, channel);
            foreach (var job in jobs)
                if (job.Start > plan.Count || job.CaptureAt > plan.Count) throw new ArgumentOutOfRangeException(nameof(jobs), "Start or CaptureAt is past the plan's " + plan.Count + " entries.");
            return plan;
        }

        public static byte[] CompositeRegion(PaintDocument document, PaintChannel channel, int x, int y, int width, int height)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            PaintLayer.ValidateChannel(channel);
            if (width < 0 || height < 0 || x < 0 || y < 0 || (long)x + width > document.Width || (long)y + height > document.Height)
                throw new ArgumentOutOfRangeException("region");
            var bytes = new byte[checked(width * height * 4)];
            if (width == 0 || height == 0) return bytes;
            document.PollGeneratorInputs(); // Generator が読むメッシュマップが変わっていれば、新しいマップで合成する
            Run(document, channel, Plan(document, channel), new[] { new CompositeJob(x, y, width, height, bytes) }, true, 1);
            return bytes;
        }

        sealed class ReferenceComparer : IEqualityComparer<byte[]>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(byte[] a, byte[] b) { return ReferenceEquals(a, b); }
            public int GetHashCode(byte[] a) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a); }
        }

        /// <summary>Tile pixels shared by every slot during one call, made on the calling thread before the workers start: the expansion
        /// of each uniform tile colour met (layers and masks read in place), a fill layer's tile, and zeros.</summary>
        sealed class SharedTiles
        {
            readonly Dictionary<uint, byte[]> uniform = new Dictionary<uint, byte[]>();
            readonly int tileBytes;
            public readonly byte[] Zero;
            public SharedTiles(int tileBytes) { this.tileBytes = tileBytes; Zero = new byte[tileBytes]; }
            static uint Key(Rgba32 c) { return (uint)(c.R | c.G << 8 | c.B << 16 | c.A << 24); }
            /// <summary>Makes the expanded tile of colour (calling thread only).</summary>
            public byte[] Make(Rgba32 c)
            {
                if (c == Rgba32.Transparent) return Zero;
                if (!uniform.TryGetValue(Key(c), out var t))
                {
                    t = new byte[tileBytes];
                    for (int i = 0; i < t.Length; i += 4) { t[i] = c.R; t[i + 1] = c.G; t[i + 2] = c.B; t[i + 3] = c.A; }
                    uniform.Add(Key(c), t);
                }
                return t;
            }
            /// <summary>The expanded tile made before (workers read only).</summary>
            public byte[] Get(Rgba32 c) { return c == Rgba32.Transparent ? Zero : uniform[Key(c)]; }
        }

        /// <summary>Per-layer tile buffers for the tiles of the plan being composited at once (one set per slot). Unfiltered raster
        /// tiles and masks are read in place from the surface (uniform tiles from a shared expansion), fill layers from one shared tile;
        /// only filter output is copied, into the layer's own slot buffers.</summary>
        sealed class LayerTile
        {
            public readonly PaintLayer Layer; public readonly byte[][] Pixels, Mask; public readonly bool[] Present; public readonly RasterMask MaskSource;
            public readonly double Opacity; public readonly double[] MaskFactor;
            readonly byte[][] ownPixels, ownMask;
            /// <summary>The sampled tile (<see cref="CompositeSampledRegions"/>) per slot, made by <see cref="PrepareSampling"/>.</summary>
            byte[][] sampledPixels, sampledMask;
            /// <summary>Read in place: the raster surface without active filters, and the mask's surface without mask filters.</summary>
            readonly SparseTileSurface surface, maskSurface;
            /// <summary>An unfiltered fill layer's tile (null: the fill has no colour in this channel or it is transparent).</summary>
            readonly byte[] fillTile; readonly bool fill;
            readonly SharedTiles shared;
            public LayerTile(PaintLayer layer, PaintChannel channel, int tileBytes, int slots, SharedTiles shared)
            {
                Layer = layer; Opacity = layer.Opacity; Present = new bool[slots]; this.shared = shared;
                if (layer.Kind != LayerKind.Adjustment && !layer.IsGroup)
                {
                    Pixels = new byte[slots][];
                    if (layer.HasActiveFilters(channel)) { ownPixels = new byte[slots][]; for (int k = 0; k < slots; k++) ownPixels[k] = new byte[tileBytes]; }
                    else if (layer.Kind == LayerKind.Fill)
                    {
                        fill = true;
                        // PaintLayer.CopyTile と同じ: 値が無いか透明なら画素は無い
                        if (layer.FillValues.TryGetValue(channel, out var value) && value != Rgba32.Transparent) fillTile = shared.Make(value);
                    }
                    else layer.TryGetChannel(channel, out surface);
                }
                // A neutral mask multiplies by exactly 1, so skipping it is exact.
                if (layer.Mask != null && !layer.Mask.IsNeutral)
                {
                    MaskSource = layer.Mask; Mask = new byte[slots][];
                    if (MaskSource.HasActiveFilters) { ownMask = new byte[slots][]; for (int k = 0; k < slots; k++) ownMask[k] = new byte[tileBytes]; }
                    else maskSurface = MaskSource.Surface;
                    MaskFactor = new double[256]; for (int h = 0; h < 256; h++) MaskFactor[h] = MaskSource.Factor((byte)h); // the same values Factor gives per pixel
                }
            }
            /// <summary>Calling thread, before the workers: makes the shared expansions of the uniform tiles this layer reads in place.</summary>
            public void PrepareUniform(IEnumerable<TileCoord> coords)
            {
                if (surface == null && maskSurface == null) return;
                foreach (var c in coords)
                {
                    TileStorage t;
                    if (surface != null && (t = surface.PeekTile(c)) != null && t.IsUniform) shared.Make(t.UniformColor);
                    if (maskSurface != null && (t = maskSurface.PeekTile(c)) != null && t.IsUniform) shared.Make(t.UniformColor);
                }
            }
            public void Load(PaintChannel channel, TileCoord coord, int slot)
            {
                if (Layer.Kind == LayerKind.Adjustment) Present[slot] = true;
                else if (ownPixels != null)
                {
                    // フィルターのある層は、フィルターを通した画素（halo を含めて評価した派生物。正本は変えない）
                    Pixels[slot] = ownPixels[slot]; Present[slot] = Layer.CopyOutputTile(channel, coord, ownPixels[slot]);
                }
                else if (fill) { Pixels[slot] = fillTile; Present[slot] = fillTile != null; }
                else Present[slot] = Peek(surface, coord, out Pixels[slot]);
                LoadMask(coord, slot);
            }
            public void LoadMask(TileCoord coord, int slot)
            {
                if (!Present[slot] || MaskSource == null) return;
                if (ownMask != null) { Mask[slot] = ownMask[slot]; MaskSource.CopyOutputTile(coord, ownMask[slot]); }
                else if (!Peek(maskSurface, coord, out Mask[slot])) Mask[slot] = shared.Zero; // 無いタイル = 何も隠さない
            }
            /// <summary>The surface's tile read in place (CopyTile's bytes without the copy); false where it has none.</summary>
            bool Peek(SparseTileSurface s, TileCoord coord, out byte[] pixels)
            {
                var t = s?.PeekTile(coord);
                if (t == null) { pixels = null; return false; }
                pixels = t.PeekData ?? shared.Get(t.UniformColor);
                return true;
            }
            /// <summary>True when reading the tile goes through a filter stack (the filter engine's cache is single-threaded).</summary>
            public bool Filtered { get { return ownPixels != null || ownMask != null; } }
            /// <summary>Calling thread: the per-slot buffers of the sampled tile (sampledBytes each).</summary>
            public void PrepareSampling(int slots, int sampledBytes)
            {
                if (Pixels != null) { sampledPixels = new byte[slots][]; for (int k = 0; k < slots; k++) sampledPixels[k] = new byte[sampledBytes]; }
                if (Mask != null) { sampledMask = new byte[slots][]; for (int k = 0; k < slots; k++) sampledMask[k] = new byte[sampledBytes]; }
            }
            /// <summary>After <see cref="Load"/>: replaces the loaded tile (and mask) of slot by its sample (one pixel of every step × step
            /// square, at step/2 in each). The surface's own arrays are only read.</summary>
            public void Sample(int slot, int tile, int step)
            {
                if (!Present[slot]) return;
                if (Pixels != null && Pixels[slot] != null) { Decimate(Pixels[slot], sampledPixels[slot], tile, step); Pixels[slot] = sampledPixels[slot]; }
                if (Mask != null && Mask[slot] != null) { Decimate(Mask[slot], sampledMask[slot], tile, step); Mask[slot] = sampledMask[slot]; }
            }
            static void Decimate(byte[] source, byte[] sampled, int tile, int step)
            {
                // 4 バイトの画素を 1 つの int として写す（Mono でバイトずつより数倍速い）。並びは同じバイト
                var from = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(source.AsSpan(0, tile * tile * 4));
                var to = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(sampled.AsSpan());
                int size = tile / step, half = step / 2;
                for (int y = 0; y < size; y++)
                    for (int x = 0, s = (y * step + half) * tile + half, d = y * size; x < size; x++, s += step, d++) to[d] = from[s];
            }
        }
        /// <summary>The plan with tile buffers per layer, mirroring StackEntry. Per-layer facts the pixel loop asks for are read once.</summary>
        sealed class Node
        {
            public StackEntry Entry; public LayerTile Tile; public Node[] Children, Clips;
            public bool Adjustment, Group, PassesThrough; public LayerBlendMode Mode, AdjustmentMode; public AdjustmentSettings Settings;
            /// <summary>The separable blend of Mode for every pair of bytes (null for the other modes).</summary>
            public double[] Table;
            /// <param name="sampledBytes">0, or the bytes of a sampled tile (<see cref="CompositeSampledRegions"/>).</param>
            public static Node[] Build(IReadOnlyList<StackEntry> plan, PaintChannel channel, int tileBytes, int slots, SharedTiles shared, int sampledBytes = 0)
            {
                var nodes = new Node[plan.Count];
                for (int i = 0; i < plan.Count; i++)
                {
                    var e = plan[i]; var layer = e.Base;
                    nodes[i] = new Node
                    {
                        Entry = e, Tile = new LayerTile(layer, channel, tileBytes, slots, shared), Children = Build(e.Children, channel, tileBytes, slots, shared, sampledBytes), Clips = Build(e.ClipEntries, channel, tileBytes, slots, shared, sampledBytes),
                        Adjustment = layer.Kind == LayerKind.Adjustment, Group = layer.IsGroup, PassesThrough = e.PassesThrough,
                        Mode = ModeOf(layer), AdjustmentMode = layer.BlendMode, Settings = layer.Adjustment,
                    };
                    if (channel != PaintChannel.Normal) nodes[i].Table = SeparableTable(nodes[i].Mode);
                    if (sampledBytes > 0) nodes[i].Tile.PrepareSampling(slots, sampledBytes);
                }
                return nodes;
            }
            /// <summary>After <see cref="Load"/> of nodes [from, to): replaces every loaded tile by its sample (the same nodes Load read).</summary>
            public static void Sample(Node[] nodes, int from, int to, int slot, int tile, int step)
            {
                for (int i = from; i < to; i++)
                {
                    var n = nodes[i];
                    n.Tile.Sample(slot, tile, step);
                    if (n.Group) Sample(n.Children, 0, n.Children.Length, slot, tile, step);
                    if (n.Tile.Present[slot]) Sample(n.Clips, 0, n.Clips.Length, slot, tile, step);
                }
            }
            /// <summary>Loads the tiles of nodes [from, to) into slot; returns true when any raster or fill pixels are present below
            /// them. present is set when any of them has anything at all (an adjustment counts).</summary>
            public static bool Load(Node[] nodes, int from, int to, PaintChannel channel, TileCoord coord, int slot, out bool present)
            {
                bool any = false; present = false;
                for (int i = from; i < to; i++)
                {
                    var n = nodes[i];
                    bool pixels;
                    if (n.Group) { pixels = Load(n.Children, 0, n.Children.Length, channel, coord, slot, out _); n.Tile.Present[slot] = pixels || HasAdjustment(n.Children, slot); n.Tile.LoadMask(coord, slot); }
                    else { n.Tile.Load(channel, coord, slot); pixels = !n.Adjustment && n.Tile.Present[slot]; }
                    if (n.Tile.Present[slot]) { pixels |= Load(n.Clips, 0, n.Clips.Length, channel, coord, slot, out _); present = true; }
                    any |= pixels;
                }
                return any;
            }
            static bool HasAdjustment(Node[] nodes, int slot) { foreach (var n in nodes) if (n.Tile.Present[slot] && n.Adjustment || n.Group && n.Tile.Present[slot]) return true; return false; }
            public static bool Filtered(Node[] nodes) { foreach (var n in nodes) if (n.Tile.Filtered || Filtered(n.Children) || Filtered(n.Clips)) return true; return false; }
            public static void PrepareUniform(Node[] nodes, IEnumerable<TileCoord> coords)
            { foreach (var n in nodes) { n.Tile.PrepareUniform(coords); PrepareUniform(n.Children, coords); PrepareUniform(n.Clips, coords); } }
        }

        // 分離できるモードの B(下, 上) は 2 つの 8 bit の値だけで決まるので、256×256 の表に Separable の値そのものを入れて引く（同じ関数の
        // 同じ引数なので同じ double）。表はモードごとに 1 回だけ、合成を呼んだスレッドで作る（ワーカーでは割り当てない）。1 つ 512 KiB。
        static readonly double[][] separableTables = new double[(int)LayerBlendMode.PassThrough + 1][];
        static bool IsSeparable(LayerBlendMode mode) { return mode >= LayerBlendMode.Multiply && mode <= LayerBlendMode.Divide; }
        static double[] SeparableTable(LayerBlendMode mode)
        {
            if (!IsSeparable(mode)) return null;
            var t = System.Threading.Volatile.Read(ref separableTables[(int)mode]);
            if (t != null) return t;
            var unit = MathUtil.ByteUnit; t = new double[65536];
            for (int d = 0; d < 256; d++) for (int s = 0; s < 256; s++) t[d << 8 | s] = Separable(mode, unit[d], unit[s]);
            System.Threading.Volatile.Write(ref separableTables[(int)mode], t); // 2 つのスレッドが同時に作っても同じ表
            return t;
        }

        /// <summary>Tile variant of EvaluatePixel: composites a rectangle of rows × count pixels of one tile (tile byte offset src in slot,
        /// rows tileStride apart) onto res (resOff, rows resStride apart), which holds the backdrop, with the nodes [from, to). Node by
        /// node over the rectangle instead of pixel by pixel; every pixel goes through the same arithmetic in the same order as
        /// EvaluatePixel. Groups and clipping use scratch rectangles per depth (rows count × 4 apart). Blends and clipping go through
        /// the registered <see cref="CompositeKernels"/> when there are some (they give the same bytes), else the managed loops.</summary>
        static void EvaluateRect(Node[] nodes, int from, int to, byte[] res, int resOff, int resStride, int slot, int src, int tileStride, int rows, int count, bool normal, RectScratch scratch, int depth, CompositeKernels kernels)
        {
            int packed = count * 4;
            for (int index = from; index < to; index++)
            {
                var n = nodes[index];
                var b = n.Tile; if (!b.Present[slot]) continue;
                if (n.Adjustment)
                {
                    for (int y = 0; y < rows; y++)
                        for (int i = 0, r = resOff + y * resStride, t = src + y * tileStride; i < count; i++, r += 4, t += 4) Write(res, r, n.Settings.Composite(Read(res, r), Amount(b, slot, t), n.AdjustmentMode));
                    continue;
                }
                if (n.PassesThrough)
                {
                    var inner = scratch.Get(depth, 0);
                    for (int y = 0; y < rows; y++) Buffer.BlockCopy(res, resOff + y * resStride, inner, y * packed, packed);
                    EvaluateRect(n.Children, 0, n.Children.Length, inner, 0, packed, slot, src, tileStride, rows, count, normal, scratch, depth + 1, kernels);
                    for (int y = 0; y < rows; y++)
                        for (int i = 0, r = resOff + y * resStride, t = src + y * tileStride, o = y * packed; i < count; i++, r += 4, t += 4, o += 4)
                            Write(res, r, StackFade(normal, Read(res, r), Read(inner, o), Amount(b, slot, t)));
                    continue;
                }
                byte[] g; int gOff, gStride;
                if (n.Group)
                {
                    g = scratch.Get(depth, 1); gOff = 0; gStride = packed; Array.Clear(g, 0, rows * packed);
                    EvaluateRect(n.Children, 0, n.Children.Length, g, 0, packed, slot, src, tileStride, rows, count, normal, scratch, depth + 1, kernels);
                }
                else if (n.Clips.Length > 0)
                {
                    g = scratch.Get(depth, 1); gOff = 0; gStride = packed;
                    for (int y = 0; y < rows; y++) Buffer.BlockCopy(b.Pixels[slot], src + y * tileStride, g, y * packed, packed);
                }
                else { g = b.Pixels[slot]; gOff = src; gStride = tileStride; } // read in place (nothing changes the layer's own pixels)
                foreach (var clip in n.Clips)
                {
                    var ct = clip.Tile; if (!ct.Present[slot]) continue;
                    if (clip.Adjustment)
                    {
                        for (int y = 0; y < rows; y++)
                            for (int i = 0, o = gOff + y * gStride, t = src + y * tileStride; i < count; i++, o += 4, t += 4) Write(g, o, clip.Settings.Composite(Read(g, o), Amount(ct, slot, t), clip.AdjustmentMode));
                        continue;
                    }
                    byte[] c; int cOff, cStride;
                    if (clip.Group)
                    {
                        c = scratch.Get(depth, 2); cOff = 0; cStride = packed; Array.Clear(c, 0, rows * packed);
                        EvaluateRect(clip.Children, 0, clip.Children.Length, c, 0, packed, slot, src, tileStride, rows, count, normal, scratch, depth + 1, kernels);
                    }
                    else { c = ct.Pixels[slot]; cOff = src; cStride = tileStride; }
                    var clipRect = Rect(g, gOff, gStride, c, cOff, cStride, rows, count, ct, slot, src, tileStride, clip.Mode, clip.Table);
                    if (normal) { if (kernels == null || !kernels.NormalClip(ref clipRect)) NormalClipRect(ref clipRect); }
                    else if (kernels == null || !kernels.Clip(ref clipRect)) ClipRect(ref clipRect);
                }
                var blendRect = Rect(res, resOff, resStride, g, gOff, gStride, rows, count, b, slot, src, tileStride, n.Mode, n.Table);
                if (normal) { if (kernels == null || !kernels.NormalBlend(ref blendRect)) NormalBlendRect(ref blendRect); }
                else if (kernels == null || !kernels.Blend(ref blendRect)) BlendRect(ref blendRect);
            }
        }
        static CompositeRect Rect(byte[] below, int belowOffset, int belowStride, byte[] over, int overOffset, int overStride, int rows, int count, LayerTile layer, int slot, int maskOffset, int maskStride, LayerBlendMode mode, double[] table)
        {
            return new CompositeRect
            {
                Below = below, BelowOffset = belowOffset, BelowStride = belowStride, Over = over, OverOffset = overOffset, OverStride = overStride, Rows = rows, Count = count,
                Mask = layer.MaskSource == null ? null : layer.Mask[slot], MaskOffset = maskOffset, MaskStride = maskStride, Opacity = layer.Opacity, MaskFactor = layer.MaskFactor,
                Mode = mode, Table = table, Unit = MathUtil.ByteUnit,
            };
        }
        static double Amount(LayerTile layer, int slot, int offset) { return layer.MaskSource == null ? layer.Opacity : layer.Opacity * layer.MaskFactor[layer.Mask[slot][offset + 3]]; }
        static Rgba32 Read(byte[] a, int o) { return new Rgba32(a[o], a[o + 1], a[o + 2], a[o + 3]); }
        static void Write(byte[] a, int o, Rgba32 c) { a[o] = c.R; a[o + 1] = c.G; a[o + 2] = c.B; a[o + 3] = c.A; }

        /// <summary>Below this the product a·c of the transparent-backdrop shortcut could be subnormal and lose bits.</summary>
        internal const double MinShortcutAlpha = 1e-300;

        /// <summary><see cref="BlendUnchecked"/> over a rectangle, reading and writing the bytes in place (the same result for every pixel;
        /// the editor's Mono runs this about twice as fast as passing each pixel as an Rgba32). Shortcuts that give the formula's own
        /// bytes: a transparent source pixel leaves the backdrop; an opaque one at amount 1 in Normal mode is copied; on a transparent
        /// backdrop the result is the source colour with alpha a (the weights are 0, a, 0 and (a·c)/a rounds back to c); on an opaque
        /// backdrop a = a_s + (1 − a_s) is exactly 1, so the division by a and the zero term drop out. Separable modes look their blend
        /// colour up in the table.</summary>
        static void BlendRect(ref CompositeRect q)
        {
            var unit = q.Unit; var mode = q.Mode; var table = q.Table; bool simple = mode == LayerBlendMode.Normal || mode == LayerBlendMode.PassThrough;
            byte[] res = q.Below, src = q.Over, mask = q.Mask; var factor = q.MaskFactor; double opacity = q.Opacity;
            for (int y = 0; y < q.Rows; y++)
                for (int r = q.BelowOffset + y * q.BelowStride, s = q.OverOffset + y * q.OverStride, m = q.MaskOffset + 3 + y * q.MaskStride, end = r + q.Count * 4; r < end; r += 4, s += 4, m += 4)
                {
                    byte sA = src[s + 3];
                    if (sA == 0) continue; // a_s = 0: the destination as it is
                    double amount = mask == null ? opacity : opacity * factor[mask[m]];
                    if (simple && sA == 255 && amount == 1) { res[r] = src[s]; res[r + 1] = src[s + 1]; res[r + 2] = src[s + 2]; res[r + 3] = 255; continue; } // BlendUnchecked と同じ近道
                    double sa = unit[sA] * amount;
                    if (sa <= 0) continue;
                    byte dA = res[r + 3];
                    if (dA == 0 && sa >= MinShortcutAlpha)
                    {
                        // 下が透明: 重みは 0・a_s・0 で色は (a_s·c)/a_s。積が正規化数なら c から 2 ulp 以内で、×255 + 0.5 の切り捨ては c のバイト
                        res[r] = src[s]; res[r + 1] = src[s + 1]; res[r + 2] = src[s + 2];
                        double v = sa * 255 + 0.5; res[r + 3] = v >= 255 ? (byte)255 : v > 0 ? (byte)(int)v : (byte)0;
                        continue;
                    }
                    double dr = unit[res[r]], dg = unit[res[r + 1]], db = unit[res[r + 2]];
                    double sr = unit[src[s]], sg = unit[src[s + 1]], sb = unit[src[s + 2]];
                    double br, bg, bb;
                    if (simple) { br = sr; bg = sg; bb = sb; }
                    else if (table != null) { br = table[res[r] << 8 | src[s]]; bg = table[res[r + 1] << 8 | src[s + 1]]; bb = table[res[r + 2] << 8 | src[s + 2]]; }
                    else BlendRgb(mode, dr, dg, db, sr, sg, sb, out br, out bg, out bb);
                    // MathUtil.ToByte written out (x × 255 + 0.5, floor by truncation of a positive value, clamped)
                    double vr, vg, vb;
                    if (dA == 255)
                    {
                        // 下が不透明: a = a_s + (1 − a_s) はちょうど 1、重みは 1 − a_s・0・a_s（0 の項と ÷1 は値を変えない）
                        double t = 1 - sa;
                        vr = (t * dr + sa * br) * 255 + 0.5; vg = (t * dg + sa * bg) * 255 + 0.5; vb = (t * db + sa * bb) * 255 + 0.5;
                        res[r + 3] = 255;
                    }
                    else
                    {
                        double da = unit[dA], a = sa + da * (1 - sa);
                        double wd = (1 - sa) * da, ws = (1 - da) * sa, wb = da * sa;
                        vr = (wd * dr + ws * sr + wb * br) / a * 255 + 0.5; vg = (wd * dg + ws * sg + wb * bg) / a * 255 + 0.5;
                        vb = (wd * db + ws * sb + wb * bb) / a * 255 + 0.5; double va = a * 255 + 0.5;
                        res[r + 3] = va >= 255 ? (byte)255 : va > 0 ? (byte)(int)va : (byte)0;
                    }
                    res[r] = vr >= 255 ? (byte)255 : vr > 0 ? (byte)(int)vr : (byte)0;
                    res[r + 1] = vg >= 255 ? (byte)255 : vg > 0 ? (byte)(int)vg : (byte)0;
                    res[r + 2] = vb >= 255 ? (byte)255 : vb > 0 ? (byte)(int)vb : (byte)0;
                }
        }
        /// <summary><see cref="ClipOnto"/> over a rectangle, in place on the clipping group (Below) (the same arithmetic as ClipOnto → MixRgb
        /// for every pixel: below + (B(below, over) − below) × a, the group's alpha kept). Separable modes look B up in the table.</summary>
        static void ClipRect(ref CompositeRect q)
        {
            var unit = q.Unit; var mode = q.Mode; var table = q.Table; bool simple = mode == LayerBlendMode.Normal || mode == LayerBlendMode.PassThrough;
            byte[] g = q.Below, c = q.Over, mask = q.Mask; var factor = q.MaskFactor; double opacity = q.Opacity;
            for (int y = 0; y < q.Rows; y++)
                for (int r = q.BelowOffset + y * q.BelowStride, s = q.OverOffset + y * q.OverStride, m = q.MaskOffset + 3 + y * q.MaskStride, end = r + q.Count * 4; r < end; r += 4, s += 4, m += 4)
                {
                    byte cA = c[s + 3];
                    if (cA == 0 || g[r + 3] == 0) continue; // a = 0, or nothing to clip to: the group as it is
                    double a = unit[cA] * (mask == null ? opacity : opacity * factor[mask[m]]);
                    if (a <= 0) continue;
                    double dr = unit[g[r]], dg = unit[g[r + 1]], db = unit[g[r + 2]];
                    double br, bg, bb;
                    if (simple) { br = unit[c[s]]; bg = unit[c[s + 1]]; bb = unit[c[s + 2]]; }
                    else if (table != null) { br = table[g[r] << 8 | c[s]]; bg = table[g[r + 1] << 8 | c[s + 1]]; bb = table[g[r + 2] << 8 | c[s + 2]]; }
                    else BlendRgb(mode, dr, dg, db, unit[c[s]], unit[c[s + 1]], unit[c[s + 2]], out br, out bg, out bb);
                    double vr = (dr + (br - dr) * a) * 255 + 0.5, vg = (dg + (bg - dg) * a) * 255 + 0.5, vb = (db + (bb - db) * a) * 255 + 0.5;
                    g[r] = vr >= 255 ? (byte)255 : vr > 0 ? (byte)(int)vr : (byte)0;
                    g[r + 1] = vg >= 255 ? (byte)255 : vg > 0 ? (byte)(int)vg : (byte)0;
                    g[r + 2] = vb >= 255 ? (byte)255 : vb > 0 ? (byte)(int)vb : (byte)0;
                }
        }
        /// <summary>The Normal channel's blend over a rectangle (NormalMaps.BlendUnchecked per pixel).</summary>
        static void NormalBlendRect(ref CompositeRect q)
        {
            for (int y = 0; y < q.Rows; y++)
                for (int i = 0, r = q.BelowOffset + y * q.BelowStride, s = q.OverOffset + y * q.OverStride, m = q.MaskOffset + 3 + y * q.MaskStride; i < q.Count; i++, r += 4, s += 4, m += 4)
                    Write(q.Below, r, NormalMaps.BlendUnchecked(Read(q.Below, r), Read(q.Over, s), q.Mask == null ? q.Opacity : q.Opacity * q.MaskFactor[q.Mask[m]], q.Mode));
        }
        /// <summary>The Normal channel's clipping over a rectangle (NormalMaps.ClipOnto per pixel).</summary>
        static void NormalClipRect(ref CompositeRect q)
        {
            for (int y = 0; y < q.Rows; y++)
                for (int i = 0, r = q.BelowOffset + y * q.BelowStride, s = q.OverOffset + y * q.OverStride, m = q.MaskOffset + 3 + y * q.MaskStride; i < q.Count; i++, r += 4, s += 4, m += 4)
                    Write(q.Below, r, NormalMaps.ClipOnto(Read(q.Below, r), Read(q.Over, s), q.Mask == null ? q.Opacity : q.Opacity * q.MaskFactor[q.Mask[m]], q.Mode));
        }
        /// <summary>Scratch rectangles of one worker: per group depth, the pass-through backdrop copy, the group or clipping base, and a
        /// clipped group. Made on the calling thread before the workers start, only those the plan uses (<see cref="ScratchNeeds"/>).</summary>
        sealed class RectScratch
        {
            readonly byte[][] rects; readonly int bytes;
            public RectScratch(bool[] needs, int bytes)
            {
                this.bytes = bytes; rects = new byte[needs.Length][];
                for (int i = 0; i < needs.Length; i++) if (needs[i]) rects[i] = new byte[bytes];
            }
            public byte[] Get(int depth, int purpose) { int i = depth * 3 + purpose; return rects[i] ?? (rects[i] = new byte[bytes]); }
        }
        /// <summary>Which scratch rectangles (depth × 3 + purpose) EvaluateRect uses for these nodes.</summary>
        static void ScratchNeeds(Node[] nodes, int depth, List<bool> needs)
        {
            void Need(int d, int purpose) { int i = d * 3 + purpose; while (needs.Count <= i) needs.Add(false); needs[i] = true; }
            foreach (var n in nodes)
            {
                if (n.Adjustment) continue;
                if (n.PassesThrough) { Need(depth, 0); ScratchNeeds(n.Children, depth + 1, needs); continue; }
                if (n.Group) { Need(depth, 1); ScratchNeeds(n.Children, depth + 1, needs); }
                else if (n.Clips.Length > 0) Need(depth, 1);
                foreach (var clip in n.Clips) if (clip.Group) { Need(depth, 2); ScratchNeeds(clip.Children, depth + 1, needs); }
            }
        }
        /// <summary>Tile buffers held at once by one call when it composites several tiles in parallel (filter output only; other
        /// tiles are read in place).</summary>
        const long ParallelBufferBytes = 64L * 1024 * 1024;
        /// <summary>Below this many pixel × layer steps a region is composited on the calling thread (starting workers costs more).</summary>
        const long ParallelMinimumWork = 1L << 16;

        /// <summary>The tiles of the jobs: every tile a job's region touches (a tile in two jobs is two items).</summary>
        struct Item { public int Job; public TileCoord Coord; }

        // Same per-pixel arithmetic as CompositePixel, but each layer's tile is read once instead of one dictionary lookup per pixel
        // per layer. Tiles are independent: they are loaded into their own slots and their pixels computed on worker threads; every
        // output pixel depends only on its own inputs, so the bytes do not depend on the number of threads or the order.
        // step > 1 (CompositeSampledRegions): the jobs are in the sampled grid, whose tiles are (tile / step)² and stand for the same tile
        // coordinates; each loaded tile is replaced by its sample before the same per-pixel evaluation.
        static void Run(PaintDocument document, PaintChannel channel, List<StackEntry> plan, IReadOnlyList<CompositeJob> jobs, bool fresh, int step)
        {
            int full = document.TileSize, tileBytes = checked(full * full * 4), tile = full / step;
            var items = new List<Item>(); var coords = new HashSet<TileCoord>(); long area = 0;
            for (int j = 0; j < jobs.Count; j++)
            {
                var job = jobs[j]; if (job.Width == 0 || job.Height == 0) continue;
                area += (long)job.Width * job.Height;
                for (int ty = job.Y / tile; ty <= (job.Y + job.Height - 1) / tile; ty++)
                    for (int tx = job.X / tile; tx <= (job.X + job.Width - 1) / tile; tx++) { var c = new TileCoord(tx, ty); items.Add(new Item { Job = j, Coord = c }); coords.Add(c); }
            }
            if (items.Count == 0) return;
            var shared = new SharedTiles(tileBytes);
            int degree = CoreParallelism.Degree, layerCount = Math.Max(1, CountEntries(plan)), buffers = CountOwnBuffers(plan, channel);
            if (area * layerCount < ParallelMinimumWork) degree = 1;
            int slots = (int)Math.Max(1, Math.Min(Math.Min(items.Count, degree), buffers == 0 ? int.MaxValue : ParallelBufferBytes / ((long)buffers * tileBytes)));
            var nodes = Node.Build(plan, channel, tileBytes, slots, shared, step > 1 ? tile * tile * 4 : 0);
            Node.PrepareUniform(nodes, coords);
            // 読み込みもワーカーで: フィルターを通る層が無ければ、読むのは面のタイルそのもの（書き換えは無い）。フィルターのキャッシュは
            // このスレッドからだけ触る。
            bool concurrentLoad = slots > 1 && !Node.Filtered(nodes);
            bool normal = channel == PaintChannel.Normal;
            var kernels = CompositeKernels.Current; kernels?.Prepare();
            var needList = new List<bool>(); ScratchNeeds(nodes, 0, needList); var needs = needList.ToArray();
            var any = new bool[slots];
            if (concurrentLoad && items.Count >= 2 * slots)
            {
                // タイルが十分あれば、ワーカーごとに自分の枠で「読む → 計算する」をタイルごとに続ける（段ごとの待ち合わせが無い）
                var scratches = new RectScratch[slots]; for (int k = 0; k < slots; k++) scratches[k] = new RectScratch(needs, tile * tile * 4);
                CoreParallelism.ForWorkers(items.Count, slots, (worker, i) =>
                {
                    var item = items[i]; var job = jobs[item.Job];
                    bool pixels = Node.Load(nodes, job.Start, nodes.Length, channel, item.Coord, worker, out bool present);
                    if (step > 1) Node.Sample(nodes, job.Start, nodes.Length, worker, full, step);
                    Compute(nodes, job, item.Coord, tile, worker, job.Start == 0 ? pixels : present, 0, tile, normal, scratches[worker], fresh, kernels);
                });
                return;
            }
            RectScratch[] chunkScratches = null; int scratchRows = 0;
            for (int first = 0; first < items.Count; first += slots)
            {
                int count = Math.Min(slots, items.Count - first), batch = first;
                if (concurrentLoad) CoreParallelism.For(count, degree, k => { var job = jobs[items[batch + k].Job]; bool p = Node.Load(nodes, job.Start, nodes.Length, channel, items[batch + k].Coord, k, out bool present); if (step > 1) Node.Sample(nodes, job.Start, nodes.Length, k, full, step); any[k] = job.Start == 0 ? p : present; });
                else for (int k = 0; k < count; k++) { var job = jobs[items[batch + k].Job]; bool p = Node.Load(nodes, job.Start, nodes.Length, channel, items[batch + k].Coord, k, out bool present); if (step > 1) Node.Sample(nodes, job.Start, nodes.Length, k, full, step); any[k] = job.Start == 0 ? p : present; }
                // 行の束に分けて計算する（タイルが少ないときもスレッドが余らないように）
                int chunks = Math.Max(1, Math.Min(tile / 8, (degree + count - 1) / count)), rowsPerChunk = (tile + chunks - 1) / chunks, workers = Math.Min(degree, count * chunks);
                if (chunkScratches == null || chunkScratches.Length < workers || scratchRows < rowsPerChunk)
                {
                    // 束の行数ぶんの作業の矩形を、使うワーカーの数だけ（ここで作り、ワーカーでは確保しない）
                    scratchRows = rowsPerChunk; chunkScratches = new RectScratch[workers];
                    for (int k = 0; k < workers; k++) chunkScratches[k] = new RectScratch(needs, rowsPerChunk * tile * 4);
                }
                var scratches = chunkScratches;
                CoreParallelism.ForWorkers(count * chunks, workers, (worker, w) =>
                {
                    int k = w / chunks, chunk = w - k * chunks; var item = items[batch + k];
                    Compute(nodes, jobs[item.Job], item.Coord, tile, k, any[k], chunk * rowsPerChunk, (chunk + 1) * rowsPerChunk, normal, scratches[worker], fresh, kernels);
                });
            }
        }
        static int CountEntries(IReadOnlyList<StackEntry> plan)
        { int c = 0; foreach (var e in plan) c += 1 + CountEntries(e.Children) + CountEntries(e.ClipEntries); return c; }
        /// <summary>Slot buffers a call needs per slot: one per layer and per mask whose tiles go through a filter stack.</summary>
        static int CountOwnBuffers(IReadOnlyList<StackEntry> plan, PaintChannel channel)
        {
            int b = 0;
            foreach (var e in plan)
            {
                var l = e.Base;
                b += (l.Kind != LayerKind.Adjustment && !l.IsGroup && l.HasActiveFilters(channel) ? 1 : 0) + (l.Mask != null && !l.Mask.IsNeutral && l.Mask.HasActiveFilters ? 1 : 0)
                    + CountOwnBuffers(e.Children, channel) + CountOwnBuffers(e.ClipEntries, channel);
            }
            return b;
        }

        /// <summary>Composites the rows [rowFrom, rowTo) of one tile (tile-relative) of a job's region, with the tile loaded in slot.
        /// any: whether anything from the job's Start up has something in this tile (if not, the result is the backdrop).</summary>
        static void Compute(Node[] nodes, CompositeJob job, TileCoord c, int tile, int slot, bool any, int rowFrom, int rowTo, bool normal, RectScratch scratch, bool fresh, CompositeKernels kernels)
        {
            int tx = c.X, ty = c.Y;
            int x0 = Math.Max(job.X, tx * tile), x1 = Math.Min(job.X + job.Width, (tx + 1) * tile);
            int y0 = Math.Max(job.Y, ty * tile + rowFrom), y1 = Math.Min(job.Y + job.Height, Math.Min((ty + 1) * tile, ty * tile + rowTo));
            if (y1 <= y0 || x1 <= x0) return;
            int length = (x1 - x0) * 4, start = job.Start, capture = job.CaptureAt, rows = y1 - y0, stride = job.Width * 4, tileStride = tile * 4;
            int off = ((y0 - job.Y) * job.Width + (x0 - job.X)) * 4, src = ((y0 - ty * tile) * tile + (x0 - tx * tile)) * 4;
            for (int y = 0, o = off; y < rows; y++, o += stride)
            {
                if (start == 0) { if (!fresh) Array.Clear(job.Pixels, o, length); } // 透明から
                else if (job.Backdrop != null) Buffer.BlockCopy(job.Backdrop, job.BackdropOffset + (y0 - job.Y + y) * job.BackdropStride + (x0 - job.X) * 4, job.Pixels, o, length);
            }
            if (!any) { if (capture >= 0) for (int y = 0, o = off; y < rows; y++, o += stride) Buffer.BlockCopy(job.Pixels, o, job.Capture, o, length); return; }
            if (capture >= 0)
            {
                EvaluateRect(nodes, start, capture, job.Pixels, off, stride, slot, src, tileStride, rows, x1 - x0, normal, scratch, 0, kernels);
                for (int y = 0, o = off; y < rows; y++, o += stride) Buffer.BlockCopy(job.Pixels, o, job.Capture, o, length);
                EvaluateRect(nodes, capture, nodes.Length, job.Pixels, off, stride, slot, src, tileStride, rows, x1 - x0, normal, scratch, 0, kernels);
            }
            else EvaluateRect(nodes, start, nodes.Length, job.Pixels, off, stride, slot, src, tileStride, rows, x1 - x0, normal, scratch, 0, kernels);
        }
    }
}
