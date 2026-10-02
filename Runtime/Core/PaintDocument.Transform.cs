using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>2D affine map (canvas pixel coordinates, bottom-left origin): x' = A x + B y + Tx, y' = C x + D y + Ty.</summary>
    public readonly struct Affine2D
    {
        public readonly double A, B, C, D, Tx, Ty;
        public Affine2D(double a, double b, double c, double d, double tx, double ty) { A = a; B = b; C = c; D = d; Tx = tx; Ty = ty; }
        public static Affine2D Identity => new Affine2D(1, 0, 0, 1, 0, 0);
        public static Affine2D Translation(double dx, double dy) => new Affine2D(1, 0, 0, 1, dx, dy);
        /// <summary>Scale (sx, sy; negative flips), then rotate counter-clockwise by degrees, both about pivot, then move by (dx, dy).</summary>
        public static Affine2D FromParts(double pivotX, double pivotY, double dx, double dy, double degrees, double sx, double sy)
        {
            foreach (var v in new[] { pivotX, pivotY, dx, dy, degrees, sx, sy }) MathUtil.RequireFinite(v, "transform");
            double r = degrees * Math.PI / 180, cos = Math.Cos(r), sin = Math.Sin(r);
            double a = cos * sx, b = -sin * sy, c = sin * sx, d = cos * sy;
            return new Affine2D(a, b, c, d, pivotX + dx - (a * pivotX + b * pivotY), pivotY + dy - (c * pivotX + d * pivotY));
        }
        public double Determinant => A * D - B * C;
        public Affine2D Inverse()
        {
            double det = Determinant;
            if (Math.Abs(det) < 1e-9) throw new InvalidOperationException("The transform flattens the image (zero scale).");
            double ia = D / det, ib = -B / det, ic = -C / det, id = A / det;
            return new Affine2D(ia, ib, ic, id, -(ia * Tx + ib * Ty), -(ic * Tx + id * Ty));
        }
        public (double x, double y) Apply(double x, double y) => (A * x + B * y + Tx, C * x + D * y + Ty);
        public bool IsIdentity => A == 1 && B == 0 && C == 0 && D == 1 && Tx == 0 && Ty == 0;
        internal bool IsFinite
        { get { foreach (var v in new[] { A, B, C, D, Tx, Ty }) if (double.IsNaN(v) || double.IsInfinity(v)) return false; return true; } }
    }

    public enum Resampling { Bilinear, Nearest }

    public sealed partial class PaintDocument
    {
        /// <summary>The bounding box [x0, x1) × [y0, y1) of what a transform of the layer would move: pixels with alpha in any
        /// channel (or mask hide amounts), limited to the region (else the selection). Null when there is nothing.</summary>
        public (int x0, int y0, int x1, int y1)? TransformBounds(Guid layerId, SelectionMask region = null)
        {
            var layer = GetLayer(layerId); var effective = EffectiveRegion(region);
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue; int tile = TileSize; var bytes = new byte[tile * tile * 4]; var amounts = new byte[tile * tile];
            foreach (var surface in TransformSurfaces(layer, true))
                foreach (var coord in surface.EnumerateTileCoordinates())
                {
                    if (effective != null && !effective.CopyTile(coord, amounts)) continue;
                    surface.CopyTile(coord, bytes);
                    int w = Math.Min(tile, Width - coord.X * tile), h = Math.Min(tile, Height - coord.Y * tile);
                    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    {
                        int i = y * tile + x; if (bytes[i * 4 + 3] == 0 || effective != null && amounts[i] == 0) continue;
                        int px = coord.X * tile + x, py = coord.Y * tile + y;
                        if (px < x0) x0 = px; if (py < y0) y0 = py; if (px + 1 > x1) x1 = px + 1; if (py + 1 > y1) y1 = py + 1;
                    }
                }
            return x0 == int.MaxValue ? ((int, int, int, int)?)null : (x0, y0, x1, y1);
        }

        /// <summary>Moves, rotates, scales or flips a paint layer's pixels — every channel it has and its mask together — as one
        /// undo step. With a region (else the selection) only the selected pixels are lifted (leaving transparency behind, in
        /// proportion to the selected amount) and laid over what remains at their new place; without one the whole layer moves.
        /// When the document's selection was used, the selection moves with the pixels in the same step. A pixel that lands
        /// exactly on one source pixel (whole-pixel moves, 90° turns, flips) is copied as is, including the colour of
        /// transparent pixels; others are resampled premultiplied (transparent pixels do not darken edges). Pixels moved off
        /// the canvas are lost. Budgets are checked per tile; on failure nothing changes. Returns false when nothing changed.</summary>
        public bool Transform(Guid layerId, Affine2D transform, SelectionMask region = null, Resampling resampling = Resampling.Bilinear, bool includeMask = true)
        {
            EnsureNoStroke();
            var layer = GetLayer(layerId);
            if (layer.Kind != LayerKind.Raster) throw new InvalidOperationException(layer.IsGroup
                ? "A group has no pixels to transform. Select a layer inside it."
                : "Only paint layers have pixels to transform.");
            if (!Enum.IsDefined(typeof(Resampling), resampling)) throw new ArgumentOutOfRangeException(nameof(resampling));
            RefusePathLayer(layer);
            if (!transform.IsFinite) throw new ArgumentException("The transform must be finite.", nameof(transform));
            if (transform.IsIdentity) return false;
            var inverse = transform.Inverse();
            var effective = EffectiveRegion(region);
            var lifted = effective == null ? null : new SurfaceSnapshot(effective.Surface);
            // 変える前に、変わり得るタイルの巻き戻し分を見積もる。超えるなら何もせずに断る（4K の全面で 1 秒以上かけてから断らない）
            var plans = new List<(SparseTileSurface surface, SurfaceSnapshot source, List<TileCoord> targets)>(); long estimate = 0;
            foreach (var surface in TransformSurfaces(layer, includeMask))
            {
                var source = new SurfaceSnapshot(surface); if (source.IsEmpty) continue;
                var targets = AffineResampler.Targets(source, lifted, transform);
                foreach (var coord in targets) estimate += 64 + source.TileBytes(coord);
                plans.Add((surface, source, targets));
            }
            if (plans.Count == 0) return false;
            if (estimate > ActiveStrokeBudgetBytes)
                throw new InvalidOperationException("This transform would keep " + Mib(estimate) + " MiB of undo data, more than the one-operation budget (" + Mib(ActiveStrokeBudgetBytes)
                    + " MiB). Nothing was changed. Raise the budget (Project Settings > YoluPainter > One stroke, or ActiveStrokeBudgetBytes) or transform a smaller selection.");
            var commands = new List<IHistoryCommand>(); long rollback = 0;
            try
            {
                foreach (var plan in plans)
                {
                    var command = TransformSurface(plan.surface, plan.source, plan.targets, inverse, lifted, resampling, ref rollback);
                    if (command != null) commands.Add(command);
                }
            }
            catch
            {
                for (int i = commands.Count - 1; i >= 0; i--) commands[i].Revert();
                throw;
            }
            if (commands.Count == 0) return false;
            if (region == null && selection != null)
            {
                var old = selection; var moved = old.Transformed(transform, inverse, resampling); var next = moved.IsEmpty ? null : moved;
                selection = next;
                commands.Add(new DelegateCommand(() => selection = next, () => selection = old, 64 + moved.AllocatedBytes));
            }
            Revision++; Push(new CompositeCommand(commands));
            return true;
        }

        static string Mib(long bytes) => (bytes / (1024.0 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        IEnumerable<SparseTileSurface> TransformSurfaces(PaintLayer layer, bool includeMask)
        {
            foreach (var entry in layer.Channels) yield return entry.Value;
            if (includeMask && layer.Mask != null) yield return layer.Mask.Surface;
        }

        /// <summary>Transforms one surface in place with exact before/after tile states (applied immediately; the returned
        /// command can undo it). Null when no tile changed.</summary>
        TileStrokeCommand TransformSurface(SparseTileSurface surface, SurfaceSnapshot source, List<TileCoord> targets, Affine2D inverse, SurfaceSnapshot lifted, Resampling resampling, ref long rollback)
        {
            var changes = new List<TileChange>(); var bytes = new byte[TileSize * TileSize * 4]; var scratch = new AffineResampler.Scratch(TileSize);
            try
            {
                foreach (var coord in targets)
                {
                    AffineResampler.Render(source, lifted, inverse, resampling, coord, bytes, scratch);
                    var before = surface.Capture(coord); var after = TileStorage.FromBytes(bytes);
                    if (TileStorage.Same(before, after)) continue;
                    rollback += 64 + (before == null ? 0 : before.ByteSize);
                    EnsureStrokeBudget(rollback);
                    surface.EnsureGrowth((after == null ? 0 : after.ByteSize) - surface.TileBytesAt(coord));
                    surface.Restore(coord, after);
                    changes.Add(new TileChange(coord, before, after));
                }
            }
            catch
            {
                for (int i = changes.Count - 1; i >= 0; i--) surface.Restore(changes[i].Coord, changes[i].Before);
                throw;
            }
            return changes.Count == 0 ? null : new TileStrokeCommand(surface, changes, Guid.NewGuid());
        }
    }

    /// <summary>A read-only view of a surface's tiles at one moment. Tiles are copy-on-write captures, so taking one copies no
    /// pixels, and later writes to the surface do not show through.</summary>
    internal sealed class SurfaceSnapshot
    {
        readonly Dictionary<TileCoord, TileStorage> tiles = new Dictionary<TileCoord, TileStorage>();
        internal readonly int Width, Height, TileSize;
        int lastX = -1, lastY = -1; TileStorage last;
        internal SurfaceSnapshot(SparseTileSurface surface)
        {
            Width = surface.Width; Height = surface.Height; TileSize = surface.TileSize;
            foreach (var coord in surface.EnumerateTileCoordinates()) tiles.Add(coord, surface.Capture(coord));
        }
        internal bool IsEmpty => tiles.Count == 0;
        internal ICollection<TileCoord> Coords => tiles.Keys;
        internal bool HasTile(TileCoord coord) => tiles.ContainsKey(coord);
        internal long TileBytes(TileCoord coord) => tiles.TryGetValue(coord, out var t) ? t.ByteSize : 0;
        /// <summary>Copies a tile (TileSize² RGBA, padding zero). False (and zeros) when the tile is absent.</summary>
        internal bool CopyTile(TileCoord coord, byte[] destination)
        {
            if (tiles.TryGetValue(coord, out var t)) { t.CopyTo(destination, destination.Length); return true; }
            Array.Clear(destination, 0, destination.Length); return false;
        }
        /// <summary>The pixel, transparent outside the canvas or in an absent tile.</summary>
        internal Rgba32 Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return Rgba32.Transparent;
            int tx = x / TileSize, ty = y / TileSize;
            if (tx != lastX || ty != lastY) { lastX = tx; lastY = ty; tiles.TryGetValue(new TileCoord(tx, ty), out last); }
            return last == null ? Rgba32.Transparent : last.Get(((y - ty * TileSize) * TileSize + x - tx * TileSize) * 4);
        }
    }

    /// <summary>Per-tile affine resampling shared by layer surfaces, masks and selections (all RGBA8 tiles; masks and
    /// selections keep their amount in alpha with RGB zero, so the same rules move them).</summary>
    internal static class AffineResampler
    {
        // サンプル位置がこれだけ画素の中心に近ければ、その 1 画素をそのまま写す（90° 回転・反転の三角関数の誤差を吸収する）
        const double SnapEpsilon = 1e-6;

        /// <summary>The tiles whose pixels can change, in Y-then-X order: the tiles pixels are lifted from and the tiles
        /// they can land on (one pixel of margin for bilinear footprints).</summary>
        internal static List<TileCoord> Targets(SurfaceSnapshot source, SurfaceSnapshot lifted, Affine2D forward)
        {
            int tile = source.TileSize, w = source.Width, h = source.Height;
            var targets = new HashSet<TileCoord>();
            foreach (var coord in source.Coords)
            {
                if (lifted != null && !lifted.HasTile(coord)) continue;
                targets.Add(coord);
                double cx0 = coord.X * tile, cy0 = coord.Y * tile, cx1 = Math.Min(w, cx0 + tile), cy1 = Math.Min(h, cy0 + tile);
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var (px, py) in new[] { (cx0, cy0), (cx1, cy0), (cx0, cy1), (cx1, cy1) })
                { var q = forward.Apply(px, py); minX = Math.Min(minX, q.x); maxX = Math.Max(maxX, q.x); minY = Math.Min(minY, q.y); maxY = Math.Max(maxY, q.y); }
                if (maxX < -1 || maxY < -1 || minX > w + 1 || minY > h + 1) continue;
                int tx0 = Math.Max(0, (int)Math.Floor((Math.Max(minX, -1) - 1) / tile)), ty0 = Math.Max(0, (int)Math.Floor((Math.Max(minY, -1) - 1) / tile));
                int tx1 = Math.Min((w - 1) / tile, (int)Math.Floor((Math.Min(maxX, w + 1) + 1) / tile)), ty1 = Math.Min((h - 1) / tile, (int)Math.Floor((Math.Min(maxY, h + 1) + 1) / tile));
                for (int ty = ty0; ty <= ty1; ty++) for (int tx = tx0; tx <= tx1; tx++) targets.Add(new TileCoord(tx, ty));
            }
            var ordered = new List<TileCoord>(targets); ordered.Sort(); return ordered;
        }

        internal sealed class Scratch
        {
            internal readonly byte[] Original, Lifted;
            internal Scratch(int tileSize) { Original = new byte[tileSize * tileSize * 4]; Lifted = new byte[tileSize * tileSize * 4]; }
        }

        /// <summary>Writes the transformed tile into bytes (TileSize² RGBA, padding zero). lifted = how much of each source pixel
        /// moves (null: all of it). What stays is the source × (1 − lifted); what arrives is laid over it with Normal.</summary>
        internal static void Render(SurfaceSnapshot source, SurfaceSnapshot lifted, Affine2D inverse, Resampling resampling, TileCoord coord, byte[] bytes, Scratch scratch)
        {
            int tile = source.TileSize, w = source.Width, h = source.Height;
            Array.Clear(bytes, 0, bytes.Length);
            // 行き先のタイルの元の画素と持ち上げる量は先に読む（画素ごとに元の位置のタイルと交互に引くとキャッシュが効かない）
            source.CopyTile(coord, scratch.Original);
            bool anyLifted = lifted == null || lifted.CopyTile(coord, scratch.Lifted);
            int tw = Math.Min(tile, w - coord.X * tile), th = Math.Min(tile, h - coord.Y * tile);
            for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++)
            {
                int px = coord.X * tile + x, py = coord.Y * tile + y, i = (y * tile + x) * 4;
                var original = new Rgba32(scratch.Original[i], scratch.Original[i + 1], scratch.Original[i + 2], scratch.Original[i + 3]);
                int here = lifted == null ? 255 : anyLifted ? scratch.Lifted[i + 3] : 0;
                var remaining = here == 0 ? original : here == 255 ? Rgba32.Transparent : new Rgba32(original.R, original.G, original.B, MathUtil.ToByte(original.A / 255.0 * (255 - here) / 255.0));
                var s = inverse.Apply(px + .5, py + .5);
                var moved = Sample(source, lifted, resampling, s.x, s.y);
                Rgba32 result;
                // 全部持ち上げた所には届いたものをそのまま置く（透明でも色を持って行く）。残りがあれば通常で重ねる
                if (moved.A == 0) result = here == 255 ? moved : remaining;
                else result = remaining.A == 0 || moved.A == 255 ? moved : CpuCompositor.Blend(remaining, moved);
                bytes[i] = result.R; bytes[i + 1] = result.G; bytes[i + 2] = result.B; bytes[i + 3] = result.A;
            }
        }

        static int Amount(SurfaceSnapshot lifted, int x, int y)
        {
            if (x < 0 || y < 0 || x >= (lifted?.Width ?? int.MaxValue) || y >= (lifted?.Height ?? int.MaxValue)) return 0;
            return lifted == null ? 255 : lifted.Get(x, y).A;
        }

        /// <summary>What arrives at a sample position: the lifted part of the source there. Exactly one source pixel is copied
        /// straight (its colour kept even when transparent); otherwise bilinear in premultiplied space.</summary>
        static Rgba32 Sample(SurfaceSnapshot source, SurfaceSnapshot lifted, Resampling resampling, double sx, double sy)
        {
            double fx = sx - .5, fy = sy - .5;
            int rx = (int)Math.Round(fx), ry = (int)Math.Round(fy);
            bool single = resampling == Resampling.Nearest || Math.Abs(fx - rx) < SnapEpsilon && Math.Abs(fy - ry) < SnapEpsilon;
            if (single)
            {
                int ix = resampling == Resampling.Nearest ? (int)Math.Floor(sx) : rx, iy = resampling == Resampling.Nearest ? (int)Math.Floor(sy) : ry;
                if (ix < 0 || iy < 0 || ix >= source.Width || iy >= source.Height) return Rgba32.Transparent;
                int amount = Amount(lifted, ix, iy);
                if (amount == 0) return Rgba32.Transparent;
                var p = source.Get(ix, iy);
                return amount == 255 ? p : new Rgba32(p.R, p.G, p.B, MathUtil.ToByte(p.A / 255.0 * amount / 255.0));
            }
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy); double tx = fx - x0, ty = fy - y0;
            double r = 0, g = 0, b = 0, a = 0;
            Accumulate(source, lifted, x0, y0, (1 - tx) * (1 - ty), ref r, ref g, ref b, ref a);
            Accumulate(source, lifted, x0 + 1, y0, tx * (1 - ty), ref r, ref g, ref b, ref a);
            Accumulate(source, lifted, x0, y0 + 1, (1 - tx) * ty, ref r, ref g, ref b, ref a);
            Accumulate(source, lifted, x0 + 1, y0 + 1, tx * ty, ref r, ref g, ref b, ref a);
            if (a <= 1e-9) return Rgba32.Transparent;
            return new Rgba32(MathUtil.ToByte(r / a), MathUtil.ToByte(g / a), MathUtil.ToByte(b / a), MathUtil.ToByte(a));
        }

        static void Accumulate(SurfaceSnapshot source, SurfaceSnapshot lifted, int x, int y, double weight, ref double r, ref double g, ref double b, ref double a)
        {
            if (weight <= 0) return;
            int amount = Amount(lifted, x, y); if (amount == 0) return;
            var p = source.Get(x, y); if (p.A == 0) return;
            double k = p.A / 255.0 * amount / 255.0 * weight;
            r += p.R / 255.0 * k; g += p.G / 255.0 * k; b += p.B / 255.0 * k; a += k;
        }
    }

    /// <summary>Several already-applied history commands undone and redone together.</summary>
    internal sealed class CompositeCommand : IHistoryCommand
    {
        readonly List<IHistoryCommand> commands;
        public long ByteCost { get; }
        internal CompositeCommand(List<IHistoryCommand> commands) { this.commands = commands; long cost = 32; foreach (var c in commands) cost += c.ByteCost; ByteCost = cost; }
        public void Apply() { foreach (var c in commands) c.Apply(); }
        public void Revert() { for (int i = commands.Count - 1; i >= 0; i--) commands[i].Revert(); }
    }
}
