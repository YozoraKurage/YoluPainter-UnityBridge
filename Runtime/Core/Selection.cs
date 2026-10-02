using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    public enum SelectionCombine { Replace, Add, Subtract, Intersect }

    /// <summary>A selection: how much of each pixel is selected (0..255), document-sized and sparse like a layer (an absent
    /// tile is unselected). Strokes, fills and gradients only change pixels in proportion to it. Builders return new
    /// masks; a mask is never changed after it is built, so the document can keep one for undo.</summary>
    public sealed class SelectionMask
    {
        readonly SparseTileSurface surface;
        public int Width => surface.Width;
        public int Height => surface.Height;
        public int TileSize => surface.TileSize;
        public bool IsEmpty => surface.TileCount == 0;
        public long AllocatedBytes => surface.AllocatedBytes;

        SelectionMask(int width, int height, int tileSize) { surface = new SparseTileSurface(width, height, tileSize); }

        /// <summary>The selected amount at a pixel, 0..255.</summary>
        public byte this[int x, int y] => surface.GetPixel(x, y).A;
        public double Coverage(int x, int y) => surface.GetPixel(x, y).A / 255.0;
        public IEnumerable<TileCoord> Tiles => surface.EnumerateTileCoordinates();
        /// <summary>The selected amount of every pixel in a tile (TileSize², padding zero). False when nothing is selected there.</summary>
        public bool CopyTile(TileCoord coord, byte[] amounts)
        {
            int n = TileSize * TileSize;
            if (amounts == null || amounts.Length != n) throw new ArgumentException("Incorrect tile length.", nameof(amounts));
            var rgba = new byte[n * 4];
            if (!surface.CopyTile(coord, rgba)) { Array.Clear(amounts, 0, n); return false; }
            for (int i = 0; i < n; i++) amounts[i] = rgba[i * 4 + 3];
            return true;
        }

        static SelectionMask Build(int width, int height, int tileSize, Func<int, int, byte> amount, int x0 = 0, int y0 = 0, int x1 = int.MaxValue, int y1 = int.MaxValue)
        {
            var mask = new SelectionMask(width, height, tileSize);
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(width, x1); y1 = Math.Min(height, y1);
            if (x0 >= x1 || y0 >= y1) return mask;
            var bytes = new byte[tileSize * tileSize * 4];
            for (int ty = y0 / tileSize; ty <= (y1 - 1) / tileSize; ty++)
                for (int tx = x0 / tileSize; tx <= (x1 - 1) / tileSize; tx++)
                {
                    Array.Clear(bytes, 0, bytes.Length); bool any = false;
                    int px0 = Math.Max(x0, tx * tileSize), px1 = Math.Min(x1, (tx + 1) * tileSize);
                    int py0 = Math.Max(y0, ty * tileSize), py1 = Math.Min(y1, (ty + 1) * tileSize);
                    for (int y = py0; y < py1; y++) for (int x = px0; x < px1; x++)
                    {
                        byte a = amount(x, y); if (a == 0) continue;
                        bytes[((y - ty * tileSize) * tileSize + (x - tx * tileSize)) * 4 + 3] = a; any = true;
                    }
                    if (any) mask.surface.ImportTile(new TileCoord(tx, ty), bytes);
                }
            return mask;
        }
        static void Require(PaintDocument document) { if (document == null) throw new ArgumentNullException(nameof(document)); }

        public static SelectionMask None(PaintDocument document) { Require(document); return new SelectionMask(document.Width, document.Height, document.TileSize); }
        public static SelectionMask All(PaintDocument document) { Require(document); return Build(document.Width, document.Height, document.TileSize, (x, y) => 255); }

        /// <summary>Pixels with centres inside [x0, x1) × [y0, y1) (canvas pixel coordinates, bottom-left origin).</summary>
        public static SelectionMask Rectangle(PaintDocument document, int x0, int y0, int x1, int y1)
        {
            Require(document);
            if (x1 < x0) { int t = x0; x0 = x1; x1 = t; }
            if (y1 < y0) { int t = y0; y0 = y1; y1 = t; }
            return Build(document.Width, document.Height, document.TileSize, (x, y) => 255, x0, y0, x1, y1);
        }

        const int Supersample = 4;
        /// <summary>Anti-aliased ellipse (4×4 samples per pixel) with centre and radii in pixels.</summary>
        public static SelectionMask Ellipse(PaintDocument document, double cx, double cy, double rx, double ry)
        {
            Require(document); MathUtil.RequireFinite(cx, nameof(cx)); MathUtil.RequireFinite(cy, nameof(cy));
            MathUtil.RequireFinite(rx, nameof(rx)); MathUtil.RequireFinite(ry, nameof(ry));
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            if (rx <= 0 || ry <= 0) return None(document);
            return Build(document.Width, document.Height, document.TileSize, (x, y) =>
            {
                int inside = 0;
                for (int sy = 0; sy < Supersample; sy++) for (int sx = 0; sx < Supersample; sx++)
                {
                    double u = (x + (sx + .5) / Supersample - cx) / rx, v = (y + (sy + .5) / Supersample - cy) / ry;
                    if (u * u + v * v <= 1) inside++;
                }
                return (byte)((inside * 255 + Supersample * Supersample / 2) / (Supersample * Supersample));
            }, (int)Math.Floor(cx - rx), (int)Math.Floor(cy - ry), (int)Math.Ceiling(cx + rx) + 1, (int)Math.Ceiling(cy + ry) + 1);
        }

        /// <summary>Anti-aliased polygon (lasso), even-odd rule, 4×4 samples per pixel. Points are canvas coordinates.</summary>
        public static SelectionMask Polygon(PaintDocument document, IReadOnlyList<(double x, double y)> points)
        {
            Require(document);
            if (points == null || points.Count < 3) return None(document);
            if (points.Count > 100000) throw new ArgumentException("Too many polygon points.", nameof(points));
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in points) { MathUtil.RequireFinite(p.x, "x"); MathUtil.RequireFinite(p.y, "y"); minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x); minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y); }
            bool Inside(double px, double py)
            {
                bool inside = false;
                for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
                {
                    var a = points[i]; var b = points[j];
                    if ((a.y > py) != (b.y > py) && px < (b.x - a.x) * (py - a.y) / (b.y - a.y) + a.x) inside = !inside;
                }
                return inside;
            }
            return Build(document.Width, document.Height, document.TileSize, (x, y) =>
            {
                int count = 0;
                for (int sy = 0; sy < Supersample; sy++) for (int sx = 0; sx < Supersample; sx++)
                    if (Inside(x + (sx + .5) / Supersample, y + (sy + .5) / Supersample)) count++;
                return (byte)((count * 255 + Supersample * Supersample / 2) / (Supersample * Supersample));
            }, (int)Math.Floor(minX), (int)Math.Floor(minY), (int)Math.Ceiling(maxX) + 1, (int)Math.Ceiling(maxY) + 1);
        }

        /// <summary>Magic wand / bucket fill region: pixels whose colour (RGBA, largest channel difference) is within
        /// tolerance (0..255) of the seed pixel. The reference is one layer's own pixels in a channel, or the composite of
        /// the channel when layer is null. Contiguous follows 4-connected neighbours from the seed; otherwise every
        /// matching pixel on the canvas is selected. Hard-edged (0 or 255).</summary>
        public static SelectionMask MagicWand(PaintDocument document, Guid? layer, PaintChannel channel, int seedX, int seedY, int tolerance, bool contiguous)
        {
            Require(document); PaintLayer.ValidateChannel(channel);
            if (seedX < 0 || seedY < 0 || seedX >= document.Width || seedY >= document.Height) throw new ArgumentOutOfRangeException("seed");
            if (tolerance < 0 || tolerance > 255) throw new ArgumentOutOfRangeException(nameof(tolerance));
            var reference = new TileReader(document, layer, channel);
            int w = document.Width, h = document.Height;
            var seed = reference.Get(seedX, seedY);
            bool Matches(Rgba32 c) =>
                Math.Abs(c.R - seed.R) <= tolerance && Math.Abs(c.G - seed.G) <= tolerance && Math.Abs(c.B - seed.B) <= tolerance && Math.Abs(c.A - seed.A) <= tolerance;
            if (!contiguous) return Build(w, h, document.TileSize, (x, y) => Matches(reference.Get(x, y)) ? (byte)255 : (byte)0);
            var selected = new System.Collections.BitArray(w * h);
            var stack = new Stack<int>(); stack.Push(seedY * w + seedX); selected[seedY * w + seedX] = true;
            int minX = seedX, maxX = seedX, minY = seedY, maxY = seedY;
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % w, y = i / w;
                if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                void Visit(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) return;
                    int n = ny * w + nx; if (selected[n] || !Matches(reference.Get(nx, ny))) return;
                    selected[n] = true; stack.Push(n);
                }
                Visit(x + 1, y); Visit(x - 1, y); Visit(x, y + 1); Visit(x, y - 1);
            }
            return Build(w, h, document.TileSize, (x, y) => selected[y * w + x] ? (byte)255 : (byte)0, minX, minY, maxX + 1, maxY + 1);
        }

        /// <summary>Reads pixels tile by tile (a layer's own pixels, or the composite) with a small tile cache.</summary>
        sealed class TileReader
        {
            readonly PaintDocument document; readonly PaintLayer layer; readonly PaintChannel channel; readonly int tile;
            readonly Dictionary<TileCoord, byte[]> cache = new Dictionary<TileCoord, byte[]>();
            public TileReader(PaintDocument document, Guid? layerId, PaintChannel channel)
            {
                this.document = document; this.channel = channel; tile = document.TileSize;
                layer = layerId.HasValue ? document.GetLayer(layerId.Value) : null;
            }
            public Rgba32 Get(int x, int y)
            {
                var coord = new TileCoord(x / tile, y / tile);
                if (!cache.TryGetValue(coord, out var bytes))
                {
                    if (cache.Count > 4096) cache.Clear();
                    if (layer != null) { bytes = new byte[tile * tile * 4]; layer.CopyTile(channel, coord, bytes); }
                    else
                    {
                        int x0 = coord.X * tile, y0 = coord.Y * tile, tw = Math.Min(tile, document.Width - x0), th = Math.Min(tile, document.Height - y0);
                        var region = CpuCompositor.CompositeRegion(document, channel, x0, y0, tw, th);
                        bytes = new byte[tile * tile * 4];
                        for (int r = 0; r < th; r++) Buffer.BlockCopy(region, r * tw * 4, bytes, r * tile * 4, tw * 4);
                    }
                    cache.Add(coord, bytes);
                }
                int i = ((y % tile) * tile + x % tile) * 4;
                return new Rgba32(bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
            }
        }

        /// <summary>Builds a mask tile by tile from the amounts of up to two masks (absent tiles read as zero).</summary>
        static SelectionMask FromTiles(int width, int height, int tileSize, SelectionMask a, SelectionMask b, Func<byte, byte, byte> combine, bool everyTile)
        {
            var mask = new SelectionMask(width, height, tileSize);
            int n = tileSize * tileSize; var ta = new byte[n]; var tb = new byte[n]; var rgba = new byte[n * 4];
            var coords = new HashSet<TileCoord>();
            if (everyTile) { for (int ty = 0; ty * tileSize < height; ty++) for (int tx = 0; tx * tileSize < width; tx++) coords.Add(new TileCoord(tx, ty)); }
            else { foreach (var c in a.Tiles) coords.Add(c); if (b != null) foreach (var c in b.Tiles) coords.Add(c); }
            foreach (var coord in coords)
            {
                a.CopyTile(coord, ta); if (b != null) b.CopyTile(coord, tb); else Array.Clear(tb, 0, n);
                Array.Clear(rgba, 0, rgba.Length); bool any = false;
                int w = Math.Min(tileSize, width - coord.X * tileSize), h = Math.Min(tileSize, height - coord.Y * tileSize);
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int i = y * tileSize + x; byte v = combine(ta[i], tb[i]);
                    if (v != 0) { rgba[i * 4 + 3] = v; any = true; }
                }
                if (any) mask.surface.ImportTile(coord, rgba);
            }
            return mask;
        }

        public SelectionMask Invert() { return FromTiles(Width, Height, TileSize, this, null, (a, _) => (byte)(255 - a), everyTile: true); }

        /// <summary>Combines this selection with another of the same size: Add takes the larger amount, Subtract removes the
        /// other's amount, Intersect takes the smaller, Replace returns the other.</summary>
        public SelectionMask Combine(SelectionMask other, SelectionCombine mode)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            if (other.Width != Width || other.Height != Height || other.TileSize != TileSize) throw new ArgumentException("Selections must be the same size.", nameof(other));
            switch (mode)
            {
                case SelectionCombine.Replace: return other;
                case SelectionCombine.Add: return FromTiles(Width, Height, TileSize, this, other, (a, b) => Math.Max(a, b), false);
                case SelectionCombine.Subtract: return FromTiles(Width, Height, TileSize, this, other, (a, b) => (byte)((a * (255 - b) + 127) / 255), false);
                case SelectionCombine.Intersect: return FromTiles(Width, Height, TileSize, this, other, (a, b) => Math.Min(a, b), false);
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }
    }
}
