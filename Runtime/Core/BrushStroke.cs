using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>A transaction for exactly one layer/channel. Dispose cancels unless committed. Brush settings are
    /// frozen at start. Input order/time and every arc-length stamp are retained; there is no final-endpoint double dab.
    /// Paint builds up per pixel during the stroke like Photoshop / CLIP STUDIO: each dab moves the pixel's stroke coverage
    /// toward the dab's ceiling (Opacity × pressure) by its flow (Flow × coverage × pressure), and the pixel is recomputed
    /// from its colour before the stroke. Overlapping dabs therefore never exceed the stroke's opacity.</summary>
    public sealed class BrushStroke : IDisposable
    {
        private readonly PaintDocument document;
        private readonly SparseTileSurface surface;
        private readonly BrushSettings settings;
        private readonly Dictionary<TileCoord, TileStorage> before = new Dictionary<TileCoord, TileStorage>();
        // Accumulated stroke coverage (0..1) per touched pixel, tile-local row-major. Freed with the stroke.
        private readonly Dictionary<TileCoord, float[]> washes = new Dictionary<TileCoord, float[]>();
        private bool finished, hasSample;
        private BrushSample previous;
        private double distanceSinceStamp;
        private double direction; // radians of the current input segment, for FollowDirection
        private readonly Random random;
        private int tipIndex; // next tip for TipSelection.Sequential
        private long rollbackBytes;
        public Guid TransactionId { get; private set; }
        public bool IsFinished { get { return finished; } }
        public long SampleCount { get; private set; }
        public long StampCount { get; private set; }
        public int ChangedTileCount { get { return before.Count; } }
        public long RollbackBytes { get { return rollbackBytes; } }
        internal BrushStroke(PaintDocument document, SparseTileSurface surface, BrushSettings settings)
        { this.document = document; this.surface = surface; this.settings = settings; TransactionId = Guid.NewGuid(); random = new Random(settings.Seed); }

        public void Add(BrushSample sample)
        {
            CheckOpen();
            try
            {
                if (hasSample && sample.Time < previous.Time) throw new ArgumentException("Input time must be nondecreasing.", nameof(sample));
                if (Math.Abs(sample.X) > 10000000 || Math.Abs(sample.Y) > 10000000)
                    throw new ArgumentOutOfRangeException(nameof(sample), "Sample exceeds the guarded pixel-space range.");
                if (!hasSample) { Stamp(sample.X, sample.Y, sample.Pressure); hasSample = true; }
                else
                {
                    double dx = sample.X - previous.X, dy = sample.Y - previous.Y;
                    double length = Math.Sqrt(dx * dx + dy * dy);
                    if (length > 0) direction = Math.Atan2(dy, dx);
                    double spacing = Math.Max(0.01, settings.Radius * 2 * settings.Spacing);
                    if (length / spacing > 1000000) throw new InvalidOperationException("Input segment exceeds the one-million-stamp safety limit; split or cancel the stroke.");
                    if (length > 0)
                    {
                        double position = spacing - distanceSinceStamp;
                        // Tolerance only absorbs roundoff at a sample boundary; the stored accumulator is clamped below.
                        while (position <= length + 1e-9)
                        {
                            double t = Math.Min(1, position / length);
                            Stamp(previous.X + dx * t, previous.Y + dy * t, previous.Pressure + (sample.Pressure - previous.Pressure) * t);
                            position += spacing;
                        }
                        distanceSinceStamp = length - (position - spacing);
                        if (distanceSinceStamp < 1e-9) distanceSinceStamp = 0;
                        if (distanceSinceStamp >= spacing) distanceSinceStamp %= spacing;
                    }
                }
                previous = sample; SampleCount++;
            }
            catch { Cancel(); throw; }
        }
        /// <summary>Paints a supplied geometric coverage (e.g. mesh-surface brush). Coordinates outside the canvas
        /// are clipped. Callers must union duplicate triangle/pixel coverage for each geometric dab before calling.</summary>
        public bool ApplyPixel(int x, int y, double coverage, double pressure = 1)
        {
            CheckOpen();
            try
            {
                MathUtil.RequireFinite(coverage, nameof(coverage)); MathUtil.RequireFinite(pressure, nameof(pressure));
                if (coverage < 0 || coverage > 1 || pressure < 0 || pressure > 1) throw new ArgumentOutOfRangeException("coverage/pressure");
                bool changed = ApplyPixelInternal(x, y, coverage, pressure);
                if (changed) document.PixelsChanged();
                return changed;
            }
            catch { Cancel(); throw; }
        }
        private void Stamp(double x, double y, double pressure)
        {
            StampCount++;
            bool changed = false;
            for (int n = 0; n < settings.Count; n++)
            {
                double radius = settings.Radius * (settings.PressureSize ? pressure : 1);
                if (settings.SizeJitter > 0) radius *= 1 - settings.SizeJitter * random.NextDouble();
                if (radius <= 0) continue;
                double cx = x, cy = y;
                if (settings.Scatter > 0)
                {
                    double reach = settings.Radius * 2 * settings.Scatter;
                    cx += (random.NextDouble() * 2 - 1) * reach; cy += (random.NextDouble() * 2 - 1) * reach;
                }
                double angle = settings.Angle * Math.PI / 180 + (settings.FollowDirection ? direction : 0);
                if (settings.AngleJitter > 0) angle += (random.NextDouble() * 2 - 1) * Math.PI * settings.AngleJitter;
                double roundness = settings.Roundness;
                if (settings.RoundnessJitter > 0) roundness = Math.Max(0.01, roundness * (1 - settings.RoundnessJitter * random.NextDouble()));
                double opacityScale = settings.OpacityJitter > 0 ? 1 - settings.OpacityJitter * random.NextDouble() : 1;
                double flowScale = settings.FlowJitter > 0 ? 1 - settings.FlowJitter * random.NextDouble() : 1;
                BrushTip tip = settings.Tip;
                var tips = settings.Tips;
                if (tips != null && tips.Length > 0)
                    tip = settings.TipSelection == TipSelection.Sequential ? tips[tipIndex++ % tips.Length] : tips[random.Next(tips.Length)];
                changed |= Dab(cx, cy, radius, angle, roundness, pressure, opacityScale, flowScale, tip);
            }
            if (changed) document.PixelsChanged();
        }
        /// <summary>One dab. With the round tip, no rotation and roundness 1 this is exactly the original circular dab.</summary>
        private bool Dab(double x, double y, double radius, double angle, double roundness, double pressure, double opacityScale, double flowScale, BrushTip tip)
        {
            double extent = tip == null ? radius : radius * 1.4142135623730951; // a square tip's corners reach √2·r when rotated
            int minX = Math.Max(0, (int)Math.Ceiling(x - extent - 0.5));
            int maxX = Math.Min(surface.Width - 1, (int)Math.Floor(x + extent - 0.5));
            int minY = Math.Max(0, (int)Math.Ceiling(y - extent - 0.5));
            int maxY = Math.Min(surface.Height - 1, (int)Math.Floor(y + extent - 0.5));
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double aspectX = 1, aspectY = 1;
            if (tip != null) { if (tip.Width >= tip.Height) aspectY = tip.Height / (double)tip.Width; else aspectX = tip.Width / (double)tip.Height; }
            bool textured = settings.Texture != null && settings.TextureDepth > 0;
            bool plain = angle == 0 && roundness == 1;
            bool changed = false;
            for (int py = minY; py <= maxY; py++) for (int px = minX; px <= maxX; px++)
            {
                double dx = px + 0.5 - x, dy = py + 0.5 - y;
                double u = (cos * dx + sin * dy) / radius, v = (-sin * dx + cos * dy) / (radius * roundness);
                double coverage;
                if (tip == null)
                {
                    // 回転も潰しも無いときは元の式そのもので測る（丸ブラシの結果を以前とビット単位で揃える）。
                    double d = plain ? Math.Sqrt(dx * dx + dy * dy) / radius : Math.Sqrt(u * u + v * v);
                    if (d > 1) continue;
                    coverage = 1;
                    if (d > settings.Hardness)
                    {
                        double t = (1 - d) / (1 - settings.Hardness);
                        coverage = t * t * (3 - 2 * t);
                    }
                }
                else
                {
                    coverage = tip.Sample((u / aspectX + 1) * 0.5, (v / aspectY + 1) * 0.5);
                    if (coverage <= 0) continue;
                }
                double ceilingScale = opacityScale;
                if (textured)
                {
                    // 紙の質感は流量ではなく天井に効かせる（Photoshop の「描点ごとに適用」オフと同じ）。流量に効かせると、
                    // 間隔の細かいブラシでは重なったダブが溜まって質感が消えてしまう。
                    double grain = settings.Texture.SampleTiled((px + 0.5) / settings.TextureScale, (py + 0.5) / settings.TextureScale);
                    ceilingScale *= 1 - settings.TextureDepth * (1 - grain);
                    if (ceilingScale <= 0) continue;
                }
                changed |= ApplyPixelInternal(px, py, coverage, pressure, ceilingScale, flowScale);
            }
            return changed;
        }
        private bool ApplyPixelInternal(int x, int y, double coverage, double pressure, double opacityScale = 1, double flowScale = 1)
        {
            if (x < 0 || y < 0 || x >= surface.Width || y >= surface.Height) return false;
            double ceiling = settings.Opacity * opacityScale * (settings.PressureOpacity ? pressure : 1);
            double flow = coverage * settings.Flow * flowScale * (settings.PressureFlow ? pressure : 1);
            if (flow <= 0 || ceiling <= 0) return false;
            TileCoord coord = surface.CoordAt(x, y);
            int tile = surface.TileSize, local = (y % tile) * tile + x % tile;
            float[] wash;
            if (washes.TryGetValue(coord, out wash) && wash[local] >= ceiling) return false;
            if (!before.ContainsKey(coord))
            {
                long nextBytes = rollbackBytes + 64 + surface.TileBytesAt(coord) + (long)tile * tile * 4;
                document.EnsureStrokeBudget(nextBytes);
                before.Add(coord, surface.Capture(coord)); rollbackBytes = nextBytes;
            }
            if (wash == null) { wash = new float[tile * tile]; washes.Add(coord, wash); }
            double accumulated = wash[local] + (ceiling - wash[local]) * Math.Min(1, flow);
            wash[local] = (float)accumulated;
            TileStorage original = before[coord];
            Rgba32 start = original == null ? Rgba32.Transparent : original.Get(local * 4), next;
            if (settings.Erase)
            {
                byte alpha = MathUtil.ToByte(start.A / 255.0 * (1 - accumulated * settings.Color.A / 255.0));
                next = alpha == 0 ? Rgba32.Transparent : new Rgba32(start.R, start.G, start.B, alpha);
            }
            else next = CpuCompositor.Blend(start, settings.Color, Math.Min(1, accumulated));
            if (next == surface.GetPixel(x, y)) return false;
            return surface.SetPixelInternal(x, y, next);
        }
        /// <summary>Commits exact before/after tile states. Returns false when the stroke made no net pixel change.</summary>
        public bool Commit()
        {
            CheckOpen();
            var changes = new List<TileChange>();
            try
            {
                var coordinates = new List<TileCoord>(before.Keys); coordinates.Sort();
                foreach (var coord in coordinates)
                {
                    surface.Compact(coord); TileStorage after = surface.Capture(coord);
                    if (!TileStorage.Same(before[coord], after)) changes.Add(new TileChange(coord, before[coord], after));
                }
                var command = changes.Count > 0 ? new TileStrokeCommand(surface, changes, TransactionId) : null;
                document.FinishStroke(this, command); finished = true; before.Clear(); washes.Clear(); rollbackBytes = 0;
                return changes.Count > 0;
            }
            catch { if (!finished) Cancel(); throw; }
        }
        public void Cancel()
        {
            if (finished) return;
            foreach (var snapshot in before) surface.Restore(snapshot.Key, snapshot.Value);
            if (before.Count > 0) document.PixelsChanged();
            document.FinishStroke(this, null); finished = true; before.Clear(); washes.Clear(); rollbackBytes = 0;
        }
        public void Dispose() { Cancel(); }
        private void CheckOpen() { if (finished) throw new InvalidOperationException("Stroke is already finished."); }
    }
    internal sealed class TileChange
    {
        internal readonly TileCoord Coord;
        internal readonly TileStorage Before, After;
        internal TileChange(TileCoord coord, TileStorage before, TileStorage after) { Coord = coord; Before = before; After = after; }
    }
    internal sealed class TileStrokeCommand : IHistoryCommand
    {
        private readonly SparseTileSurface surface;
        private readonly List<TileChange> changes;
        internal readonly Guid TransactionId;
        public long ByteCost { get; private set; }
        internal TileStrokeCommand(SparseTileSurface surface, List<TileChange> changes, Guid transactionId)
        {
            this.surface = surface; this.changes = changes; TransactionId = transactionId; ByteCost = 64;
            foreach (var change in changes) ByteCost += 16 + (change.Before == null ? 0 : change.Before.ByteSize) + (change.After == null ? 0 : change.After.ByteSize);
        }
        public void Apply() { RestoreAll(false); }
        public void Revert() { RestoreAll(true); }
        private void RestoreAll(bool backwards)
        {
            long growth = 0;
            foreach (var change in changes)
            {
                var state = backwards ? change.Before : change.After;
                growth += (state == null ? 0 : state.ByteSize) - surface.TileBytesAt(change.Coord);
            }
            surface.EnsureGrowth(growth);
            foreach (var change in changes) surface.Restore(change.Coord, backwards ? change.Before : change.After);
        }
    }
}
