using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>
    /// A fill that grows while it runs (the polygon fill's drag): the union of the triangles added so far (UV triangles in canvas pixel
    /// coordinates, bottom-left origin), filled exactly as <see cref="PaintDocument.Fill"/> / <see cref="PaintDocument.FillMask"/> fill
    /// <see cref="SelectionMask.FromTriangles"/> of that union, inside the selection the document had when the fill began. Each pixel
    /// keeps which of its 4×4 samples the added triangles cover (<see cref="TriangleSamples"/>): a sample covered twice counts once, so
    /// triangles that share an edge leave no seam and no double coverage whatever order they come in, and adding a triangle again
    /// changes nothing. A pixel whose samples change is recomputed from its value before the fill.
    /// The fill is carried by the document's active stroke (<see cref="Stroke"/>): nothing else edits the document until that is
    /// committed (one undo step for everything added) or cancelled (every pixel back as it was). An error while adding (invalid
    /// coordinates, a budget) cancels it, like <see cref="BrushStroke.Add"/>.
    /// </summary>
    public sealed class TriangleFill
    {
        readonly PaintDocument document; readonly BrushStroke stroke; readonly SelectionMask selection;
        readonly Func<Rgba32, double, Rgba32> rule;
        readonly int width, height, tileSize;
        /// <summary>The samples covered so far, per tile (tile-local, row-major). A tile whose every pixel is fully covered shares
        /// <see cref="FullTile"/> (and keeps no memory of its own).</summary>
        readonly Dictionary<TileCoord, ushort[]> samples = new Dictionary<TileCoord, ushort[]>();
        static readonly ushort[] FullTile = new ushort[0];

        /// <summary>The document's active stroke that carries this fill: Commit (one undo step), Cancel or Dispose it to end the fill.</summary>
        public BrushStroke Stroke => stroke;
        public bool IsFinished => stroke.IsFinished;
        /// <summary>Triangles passed to <see cref="Add"/> so far (repeats included).</summary>
        public long TrianglesAdded { get; private set; }
        /// <summary>Tiles some added triangle covers.</summary>
        public int CoveredTileCount => samples.Count;

        internal TriangleFill(PaintDocument document, BrushStroke stroke, Func<Rgba32, double, Rgba32> rule)
        {
            this.document = document; this.stroke = stroke; this.rule = rule;
            selection = document.Selection; width = document.Width; height = document.Height; tileSize = document.TileSize;
        }

        /// <summary>Adds triangles to the filled union. Returns true when a pixel changed. Coordinates must be finite (a triangle with
        /// almost no area, or off the canvas, covers nothing).</summary>
        public bool Add(IReadOnlyList<(double ax, double ay, double bx, double by, double cx, double cy)> triangles)
        {
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (stroke.IsFinished) throw new InvalidOperationException("The fill is already finished.");
            try
            {
                var prepared = TriangleSamples.Prepare(triangles, width, height);
                TrianglesAdded += triangles.Count;
                if (prepared.Count == 0) return false;
                var bins = TriangleSamples.Bin(prepared, tileSize);
                var coords = new List<TileCoord>();
                foreach (var c in bins.Keys) if (!(samples.TryGetValue(c, out var held) && ReferenceEquals(held, FullTile))) coords.Add(c); // 全部覆った所は変わらない
                if (coords.Count == 0) return false;
                coords.Sort();
                int n = tileSize * tileSize;
                // 1. サンプルのビット（新しいタイルの分はここで作り、全部覆われたら手放す）。タイルごとに別のスレッドで
                var tiles = new ushort[coords.Count][]; var fresh = new bool[coords.Count]; var grew = new bool[coords.Count];
                for (int k = 0; k < coords.Count; k++)
                    if (!samples.TryGetValue(coords[k], out tiles[k])) { tiles[k] = new ushort[n]; fresh[k] = true; }
                CoreParallelism.For(coords.Count, CoreParallelism.Degree, k =>
                    grew[k] = TriangleSamples.Rasterize(prepared, bins[coords[k]], coords[k], tileSize, width, height, tiles[k]));
                var changed = new List<TileCoord>();
                for (int k = 0; k < coords.Count; k++)
                {
                    if (!grew[k]) continue;
                    var held = IsFull(coords[k], tiles[k]) ? FullTile : tiles[k];
                    if (fresh[k] && !ReferenceEquals(held, FullTile)) stroke.ReserveScratch(16 + 2L * n); // 予算を超えれば取り消す
                    samples[coords[k]] = held; changed.Add(coords[k]);
                }
                if (changed.Count == 0) return false;
                // 2. 変わったタイルを、描く前の画素から計算し直す（計算は並列、写しの取得・予算・置き換えはこのスレッドで順に）
                bool any = Recompute(changed);
                if (any) document.PixelsChanged();
                return any;
            }
            catch { stroke.Cancel(); throw; }
        }

        /// <summary>Every canvas pixel of the tile is fully covered.</summary>
        bool IsFull(TileCoord coord, ushort[] bits)
        {
            int w = Math.Min(tileSize, width - coord.X * tileSize), h = Math.Min(tileSize, height - coord.Y * tileSize);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (bits[y * tileSize + x] != TriangleSamples.Full) return false;
            return true;
        }

        bool Recompute(List<TileCoord> coords)
        {
            int n = tileSize * tileSize, degree = CoreParallelism.Degree, batch = degree > 1 ? Math.Min(coords.Count, degree * 4) : 1;
            var originals = new TileStorage[batch]; var bytes = new byte[batch][]; var selected = new byte[batch][];
            var same = new bool[batch]; var uniform = new bool[batch];
            for (int k = 0; k < batch; k++) { bytes[k] = new byte[n * 4]; selected[k] = new byte[n]; }
            bool any = false;
            for (int start = 0; start < coords.Count; start += batch)
            {
                int count = Math.Min(batch, coords.Count - start), first = start;
                for (int k = 0; k < count; k++) originals[k] = stroke.OriginalTile(coords[first + k]); // 写しと巻き戻しの予算
                // ワーカーでは割り当てない。描く前の画素に、和集合の被覆率（と選択範囲の小さい方。SelectionCombine.Intersect と同じ）で式を当てる
                CoreParallelism.For(count, degree, k =>
                {
                    var coord = coords[first + k]; var tile = bytes[k]; var sel = selected[k]; var bits = samples[coord];
                    if (originals[k] == null) Array.Clear(tile, 0, tile.Length); else originals[k].CopyTo(tile, tile.Length);
                    bool hasSelection = selection != null && selection.CopyTile(coord, sel);
                    int w = Math.Min(tileSize, width - coord.X * tileSize), h = Math.Min(tileSize, height - coord.Y * tileSize);
                    bool full = ReferenceEquals(bits, FullTile);
                    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    {
                        int i = y * tileSize + x;
                        int amount = full ? 255 : bits[i] == 0 ? 0 : TriangleSamples.Coverage(bits[i]);
                        if (selection != null) amount = hasSelection ? Math.Min(amount, sel[i]) : 0;
                        if (amount == 0) continue;
                        int o = i * 4; var before = new Rgba32(tile[o], tile[o + 1], tile[o + 2], tile[o + 3]);
                        var next = rule(before, amount / 255.0);
                        tile[o] = next.R; tile[o + 1] = next.G; tile[o + 2] = next.B; tile[o + 3] = next.A;
                    }
                    same[k] = TileStorage.SameAs(stroke.PeekSurfaceTile(coord), tile); // 読むだけ（置き換えは並列の後）
                    uniform[k] = !same[k] && TileStorage.Uniformity(tile);
                });
                for (int k = 0; k < count; k++)
                {
                    if (same[k]) continue;
                    stroke.ReplaceTile(coords[first + k], TileStorage.FromBytes(bytes[k], uniform[k]));
                    any = true;
                }
            }
            return any;
        }
    }

    public sealed partial class PaintDocument
    {
        /// <summary>Starts a <see cref="TriangleFill"/> of a paint layer's channel: color by opacity × the union's coverage (erase takes
        /// alpha away instead), as <see cref="Fill"/> does. Refused like Fill: a layer that is not a paint layer, a disabled channel, a
        /// path layer, image pixels or everything locked, erasing with transparent pixels locked (with them locked, painting keeps each
        /// pixel's alpha).</summary>
        public TriangleFill BeginTriangleFill(Guid layerId, PaintChannel channel, Rgba32 color, double opacity = 1, bool erase = false)
        {
            MathUtil.RequireFinite(opacity, nameof(opacity)); if (opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
            EnsureNoStroke(); RefuseInBatch("A fill"); PaintLayer.ValidateChannel(channel);
            var layer = GetLayer(layerId);
            if (layer.Kind != LayerKind.Raster) throw new InvalidOperationException("Only paint layers have pixels to fill. Use the layer's mask for fill, adjustment and group layers.");
            if (!layer.IsChannelEnabled(channel)) throw new InvalidOperationException("Enable the target channel before filling.");
            RefuseLockedPixels(layer, erase);
            RefusePathLayer(layer);
            bool keepAlpha = KeepsAlpha(layer);
            var stroke = new BrushStroke(this, layer.GetChannel(channel), new BrushSettings { Color = color, Opacity = opacity, Erase = erase });
            activeStroke = stroke;
            return new TriangleFill(this, stroke, FillRule(color, opacity, erase, keepAlpha));
        }

        /// <summary>Starts a <see cref="TriangleFill"/> of a layer's mask: hides by amount × the union's coverage, or reveals with reveal,
        /// as <see cref="FillMask"/> does. Refused when the layer has no mask, or everything on it is locked (a mask can be painted with
        /// its image pixels locked).</summary>
        public TriangleFill BeginMaskTriangleFill(Guid layerId, double amount = 1, bool reveal = false)
        {
            MathUtil.RequireFinite(amount, nameof(amount)); if (amount < 0 || amount > 1) throw new ArgumentOutOfRangeException(nameof(amount));
            EnsureNoStroke(); RefuseInBatch("A fill");
            var mask = RequireMask(layerId, out var owner);
            RefuseLockedAttributes(owner);
            var stroke = new BrushStroke(this, mask.Surface, new BrushSettings { Color = new Rgba32(0, 0, 0, 255), Opacity = amount, Erase = reveal });
            activeStroke = stroke;
            return new TriangleFill(this, stroke, MaskFillRule(amount, reveal));
        }
    }
}
