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
        /// <summary>Scale (sx, sy), then rotate counter-clockwise by degrees, both about pivot, then move by (dx, dy).</summary>
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
    }

    public enum Resampling { Bilinear, Nearest }

    public sealed partial class PaintDocument
    {
        /// <summary>The bounding box [x0, x1) × [y0, y1) of what a transform of the layer would move: pixels with alpha in any
        /// enabled channel (or mask hide amounts), limited to the region (else the selection). Null when there is nothing.</summary>
        public (int x0, int y0, int x1, int y1)? TransformBounds(Guid layerId, SelectionMask region = null)
        {
            var layer = GetLayer(layerId); var effective = EffectiveRegion(region);
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue; int tile = TileSize; var bytes = new byte[tile * tile * 4]; var amounts = new byte[tile * tile];
            foreach (var surface in TransformSurfaces(layer))
                foreach (var coord in surface.EnumerateTileCoordinates())
                {
                    surface.CopyTile(coord, bytes);
                    bool hasRegion = effective == null || effective.CopyTile(coord, amounts);
                    if (!hasRegion) continue;
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

        /// <summary>Moves, rotates or scales a paint layer's pixels — every channel it has and its mask together — as one undo
        /// step. With a region (else the selection) only the selected pixels are lifted (leaving transparency behind, in
        /// proportion to the selected amount) and laid back over what remains at their new place; without one the whole
        /// layer moves. Resampling is premultiplied (transparent pixels do not darken edges); pixels moved off the canvas
        /// are lost. Returns false when nothing changed.</summary>
        public bool Transform(Guid layerId, Affine2D transform, SelectionMask region = null, Resampling resampling = Resampling.Bilinear, bool includeMask = true)
        {
            EnsureNoStroke();
            var layer = GetLayer(layerId);
            if (layer.Kind != LayerKind.Raster) throw new InvalidOperationException("Only paint layers have pixels to transform.");
            if (!Enum.IsDefined(typeof(Resampling), resampling)) throw new ArgumentOutOfRangeException(nameof(resampling));
            if (transform.IsIdentity) return false;
            var inverse = transform.Inverse();
            var effective = EffectiveRegion(region);
            var surfaces = new List<SparseTileSurface>();
            foreach (var entry in layer.Channels) surfaces.Add(entry.Value);
            if (includeMask && layer.Mask != null) surfaces.Add(layer.Mask.Surface);
            var commands = new List<IHistoryCommand>(); long rollback = 0;
            try
            {
                foreach (var surface in surfaces)
                {
                    var command = TransformSurface(surface, transform, inverse, effective, resampling, ref rollback);
                    if (command != null) commands.Add(command);
                }
            }
            catch
            {
                for (int i = commands.Count - 1; i >= 0; i--) commands[i].Revert();
                throw;
            }
            if (commands.Count == 0) return false;
            Revision++; Push(new CompositeCommand(commands));
            return true;
        }

        IEnumerable<SparseTileSurface> TransformSurfaces(PaintLayer layer)
        {
            foreach (var entry in layer.Channels) yield return entry.Value;
            if (layer.Mask != null) yield return layer.Mask.Surface;
        }

        /// <summary>Transforms one surface: reads it into a working copy, computes the new pixels for every tile the result can
        /// touch, and writes them with exact before/after tile states (applied immediately; the returned command can undo).</summary>
        TileStrokeCommand TransformSurface(SparseTileSurface surface, Affine2D forward, Affine2D inverse, SelectionMask region, Resampling resampling, ref long rollback)
        {
            int tile = TileSize, n = tile * tile, w = Width, h = Height;
            // 元の画素（読み取り専用の写し）。範囲の量も同じ座標で引く
            var source = new Dictionary<TileCoord, byte[]>(); var amounts = new Dictionary<TileCoord, byte[]>();
            foreach (var coord in surface.EnumerateTileCoordinates()) { var b = new byte[n * 4]; surface.CopyTile(coord, b); source[coord] = b; }
            if (source.Count == 0) return null;
            byte Amount(int x, int y)
            {
                if (region == null) return 255;
                var coord = new TileCoord(x / tile, y / tile);
                if (!amounts.TryGetValue(coord, out var a)) { a = new byte[n]; if (!region.CopyTile(coord, a)) a = null; amounts[coord] = a; }
                return a == null ? (byte)0 : a[(y % tile) * tile + x % tile];
            }
            Rgba32 Source(int x, int y)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) return Rgba32.Transparent;
                if (!source.TryGetValue(new TileCoord(x / tile, y / tile), out var b)) return Rgba32.Transparent;
                int i = ((y % tile) * tile + x % tile) * 4; return new Rgba32(b[i], b[i + 1], b[i + 2], b[i + 3]);
            }
            // 持ち上げる画素（範囲の量を掛けたもの）を逆写像で引く。プリマルチプライドで補間する
            void Lifted(int x, int y, out double r, out double g, out double bl, out double a)
            {
                var p = Source(x, y); double k = p.A / 255.0 * Amount(Math.Max(0, Math.Min(w - 1, x)), Math.Max(0, Math.Min(h - 1, y))) / 255.0;
                if (x < 0 || y < 0 || x >= w || y >= h) k = 0;
                r = p.R / 255.0 * k; g = p.G / 255.0 * k; bl = p.B / 255.0 * k; a = k;
            }
            Rgba32 Sample(double sx, double sy)
            {
                double r, g, b, a;
                if (resampling == Resampling.Nearest) Lifted((int)Math.Floor(sx), (int)Math.Floor(sy), out r, out g, out b, out a);
                else
                {
                    double fx = sx - .5, fy = sy - .5; int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy); double tx = fx - x0, ty = fy - y0;
                    Lifted(x0, y0, out var r00, out var g00, out var b00, out var a00); Lifted(x0 + 1, y0, out var r10, out var g10, out var b10, out var a10);
                    Lifted(x0, y0 + 1, out var r01, out var g01, out var b01, out var a01); Lifted(x0 + 1, y0 + 1, out var r11, out var g11, out var b11, out var a11);
                    double w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
                    r = r00 * w00 + r10 * w10 + r01 * w01 + r11 * w11; g = g00 * w00 + g10 * w10 + g01 * w01 + g11 * w11;
                    b = b00 * w00 + b10 * w10 + b01 * w01 + b11 * w11; a = a00 * w00 + a10 * w10 + a01 * w01 + a11 * w11;
                }
                if (a <= 1e-9) return Rgba32.Transparent;
                return new Rgba32(MathUtil.ToByte(r / a), MathUtil.ToByte(g / a), MathUtil.ToByte(b / a), MathUtil.ToByte(a));
            }
            // 結果に関わるタイル: 元のタイル（持ち上げた跡）と、持ち上げる画素の行き先
            var targets = new HashSet<TileCoord>(source.Keys);
            foreach (var coord in source.Keys)
            {
                double cx0 = coord.X * tile, cy0 = coord.Y * tile, cx1 = cx0 + tile, cy1 = cy0 + tile;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var (px, py) in new[] { (cx0, cy0), (cx1, cy0), (cx0, cy1), (cx1, cy1) })
                { var q = forward.Apply(px, py); minX = Math.Min(minX, q.x); maxX = Math.Max(maxX, q.x); minY = Math.Min(minY, q.y); maxY = Math.Max(maxY, q.y); }
                int tx0 = Math.Max(0, (int)Math.Floor((minX - 1) / tile)), ty0 = Math.Max(0, (int)Math.Floor((minY - 1) / tile));
                int tx1 = Math.Min((w - 1) / tile, (int)Math.Floor((maxX + 1) / tile)), ty1 = Math.Min((h - 1) / tile, (int)Math.Floor((maxY + 1) / tile));
                for (int ty = ty0; ty <= ty1; ty++) for (int tx = tx0; tx <= tx1; tx++) targets.Add(new TileCoord(tx, ty));
            }
            var ordered = new List<TileCoord>(targets); ordered.Sort();
            var changes = new List<TileChange>(); var bytes = new byte[n * 4];
            try
            {
                foreach (var coord in ordered)
                {
                    Array.Clear(bytes, 0, bytes.Length);
                    int tw = Math.Min(tile, w - coord.X * tile), th = Math.Min(tile, h - coord.Y * tile);
                    for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++)
                    {
                        int px = coord.X * tile + x, py = coord.Y * tile + y;
                        // 残る画素 = 元 × (1 − 範囲の量)、その上に行き先へ移った画素を通常で重ねる
                        var original = Source(px, py); double keep = 1 - Amount(px, py) / 255.0;
                        var remaining = keep >= 1 ? original : keep <= 0 ? Rgba32.Transparent : new Rgba32(original.R, original.G, original.B, MathUtil.ToByte(original.A / 255.0 * keep));
                        var s = inverse.Apply(px + .5, py + .5);
                        var moved = Sample(s.x, s.y);
                        var result = moved.A == 0 ? remaining : CpuCompositor.Blend(remaining, moved);
                        int o = (y * tile + x) * 4; bytes[o] = result.R; bytes[o + 1] = result.G; bytes[o + 2] = result.B; bytes[o + 3] = result.A;
                    }
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
