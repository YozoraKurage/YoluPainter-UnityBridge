using System;
using System.Collections.Generic;

namespace Dot.TexturePainter.Core
{
    /// <summary>A transaction for exactly one layer/channel. Dispose cancels unless committed. Brush settings are
    /// frozen at start. Input order/time and every arc-length stamp are retained; there is no final-endpoint double dab.</summary>
    public sealed class BrushStroke : IDisposable
    {
        private readonly PaintDocument document;
        private readonly SparseTileSurface surface;
        private readonly BrushSettings settings;
        private readonly Dictionary<TileCoord, TileStorage> before = new Dictionary<TileCoord, TileStorage>();
        private bool finished, hasSample;
        private BrushSample previous;
        private double distanceSinceStamp;
        private long rollbackBytes;
        public Guid TransactionId { get; private set; }
        public bool IsFinished { get { return finished; } }
        public long SampleCount { get; private set; }
        public long StampCount { get; private set; }
        public int ChangedTileCount { get { return before.Count; } }
        public long RollbackBytes { get { return rollbackBytes; } }
        internal BrushStroke(PaintDocument document, SparseTileSurface surface, BrushSettings settings)
        { this.document = document; this.surface = surface; this.settings = settings; TransactionId = Guid.NewGuid(); }

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
            double radius = settings.Radius * (settings.PressureSize ? pressure : 1);
            if (radius <= 0) return;
            int minX = Math.Max(0, (int)Math.Ceiling(x - radius - 0.5));
            int maxX = Math.Min(surface.Width - 1, (int)Math.Floor(x + radius - 0.5));
            int minY = Math.Max(0, (int)Math.Ceiling(y - radius - 0.5));
            int maxY = Math.Min(surface.Height - 1, (int)Math.Floor(y + radius - 0.5));
            bool changed = false;
            for (int py = minY; py <= maxY; py++) for (int px = minX; px <= maxX; px++)
            {
                double dx = px + 0.5 - x, dy = py + 0.5 - y;
                double d = Math.Sqrt(dx * dx + dy * dy) / radius;
                if (d > 1) continue;
                double coverage = 1;
                if (d > settings.Hardness)
                {
                    double t = (1 - d) / (1 - settings.Hardness);
                    coverage = t * t * (3 - 2 * t);
                }
                changed |= ApplyPixelInternal(px, py, coverage, pressure);
            }
            if (changed) document.PixelsChanged();
        }
        private bool ApplyPixelInternal(int x, int y, double coverage, double pressure)
        {
            if (x < 0 || y < 0 || x >= surface.Width || y >= surface.Height) return false;
            double strength = coverage * settings.Opacity * settings.Flow;
            if (settings.PressureOpacity) strength *= pressure;
            if (settings.PressureFlow) strength *= pressure;
            if (strength <= 0) return false;
            Rgba32 old = surface.GetPixel(x, y), next;
            if (settings.Erase)
            {
                byte alpha = MathUtil.ToByte(old.A / 255.0 * (1 - strength * settings.Color.A / 255.0));
                next = alpha == 0 ? Rgba32.Transparent : new Rgba32(old.R, old.G, old.B, alpha);
            }
            else next = CpuCompositor.Blend(old, settings.Color, strength);
            if (next == old) return false;
            TileCoord coord = surface.CoordAt(x, y);
            if (!before.ContainsKey(coord))
            {
                long nextBytes = rollbackBytes + 64 + surface.TileBytesAt(coord);
                document.EnsureStrokeBudget(nextBytes);
                before.Add(coord, surface.Capture(coord)); rollbackBytes = nextBytes;
            }
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
                document.FinishStroke(this, command); finished = true; before.Clear(); rollbackBytes = 0;
                return changes.Count > 0;
            }
            catch { if (!finished) Cancel(); throw; }
        }
        public void Cancel()
        {
            if (finished) return;
            foreach (var snapshot in before) surface.Restore(snapshot.Key, snapshot.Value);
            if (before.Count > 0) document.PixelsChanged();
            document.FinishStroke(this, null); finished = true; before.Clear(); rollbackBytes = 0;
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
