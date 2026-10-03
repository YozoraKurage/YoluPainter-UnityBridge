using System;
using System.Collections.Generic;
using System.Globalization;
using Yozolab.YoluPainter.Core.Paths;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>How <see cref="PaintDocument.Resampled"/> computes the pixels of the new size. Every method maps the canvas onto
    /// the new canvas edge to edge (pixel centres x + 0.5 scale by new / old) and repeats the edge pixel outside the canvas.
    /// <list type="bullet">
    /// <item>Nearest: the source pixel under the new pixel's centre, copied as it is (RGBA, the colour of transparent pixels
    /// included). Whole-number enlargements repeat every pixel exactly; halving keeps the upper-right pixel of each 2 × 2 block.</item>
    /// <item>Bilinear: the four source pixels around the new centre, weighted by distance. Enlarging by a whole number interpolates
    /// (it does not repeat pixels); halving averages each 2 × 2 block; shrinking further skips pixels (use Area).</item>
    /// <item>Area: the average of the source area the new pixel covers (a box filter with exact fractional overlaps). Shrinking by
    /// 2ⁿ averages each 2ⁿ × 2ⁿ block; whole-number enlargements repeat every pixel exactly, like Nearest.</item>
    /// </list>
    /// Averages are taken premultiplied, so transparent pixels do not darken edges. A new pixel that comes out fully transparent
    /// keeps the plain (unweighted by alpha) average colour of the transparent source pixels it covers, so the colour stored under
    /// zero alpha survives (a uniform transparent colour stays exactly that colour; an erased region stays (0, 0, 0, 0)). A new
    /// pixel whose source pixels are all equal is that pixel exactly. On the Normal channel an average of different pixels is
    /// decoded, renormalized and encoded again (like the blur filter).</summary>
    public enum CanvasResampling { Nearest = 0, Bilinear = 1, Area = 2 }

    /// <summary>A resampled copy of a document (<see cref="PaintDocument.Resampled"/>).</summary>
    public sealed class ResampledDocument
    {
        /// <summary>The new document: the same ID, layers, layer IDs, structure and settings, no history.</summary>
        public PaintDocument Document { get; }
        /// <summary>Settings that could not keep their look at the new size and were limited instead (English, one per setting).</summary>
        public IReadOnlyList<string> Notes { get; }
        /// <summary>Layers drawn by a path on the model. Their path channel holds resampled pixels; the caller redraws them from
        /// the path with the model (the core has no model), or leaves the resampled pixels and says so.</summary>
        public IReadOnlyList<Guid> SurfacePathLayers { get; }
        internal ResampledDocument(PaintDocument document, List<string> notes, List<Guid> surfacePaths)
        { Document = document; Notes = notes.AsReadOnly(); SurfacePathLayers = surfacePaths.AsReadOnly(); }
    }

    /// <summary>Changing the canvas size (Substance Painter's texture set resolution): a resampled copy of the whole document.</summary>
    public sealed partial class PaintDocument
    {
        /// <summary>The largest canvas side the native archive (<see cref="Persistence.DocumentBinary"/>) reads back, and so the
        /// largest size <see cref="Resampled"/> makes.</summary>
        public const int MaxNativeSide = 8192;

        /// <summary>
        /// A copy of the document at width × height. This document is not changed. Paint layers resample every channel and the
        /// mask (<paramref name="resampling"/>); fill, adjustment and group layers own no pixels and are copied. The selection is
        /// resampled the same way. Things measured in pixels follow the scale so the result looks the same on the model: layer and
        /// mask filter radii (blur, sharpen; rounded half away from zero, at least 1, limited to the filter's maximum), the
        /// Height → Normal strength (slope per texel), and 2D paths (points and brush radius; the path channel is drawn again from
        /// the scaled path, not resampled). With different horizontal and vertical scales, radii and strength use the geometric
        /// mean. Noise keeps its seed and per-pixel pattern (its grain is finer or coarser on the model). Paths on the model are
        /// UV-bound and kept; their pixels are resampled and listed in <see cref="ResampledDocument.SurfacePathLayers"/> for the
        /// caller to redraw. Limited settings are listed in <see cref="ResampledDocument.Notes"/>.
        /// <para>The copy has no history and its <see cref="Revision"/> continues after this document's. Its layer pixels are
        /// checked against <paramref name="sourceBudgetBytes"/> (this document's budget when null) as they are made; going over
        /// throws and leaves nothing behind (this document is untouched either way). Filters that would exceed
        /// <see cref="MaxFilterStackHalo"/> or the filter working budget at the new size are refused the same way.</para>
        /// </summary>
        public ResampledDocument Resampled(int width, int height, CanvasResampling resampling, long? sourceBudgetBytes = null) => Resampled(width, height, resampling, sourceBudgetBytes, TileSize);

        /// <summary><see cref="Resampled(int, int, CanvasResampling, long?)"/> into a document of another tile size (a smart material placed
        /// into a texture set that uses another one; at the same size with Nearest the pixels are copied as they are).</summary>
        internal ResampledDocument Resampled(int width, int height, CanvasResampling resampling, long? sourceBudgetBytes, int tileSize)
        {
            EnsureNoStroke();
            if (width < 1 || height < 1 || width > MaxNativeSide || height > MaxNativeSide)
                throw new ArgumentOutOfRangeException(width < 1 || width > MaxNativeSide ? nameof(width) : nameof(height), "A canvas side must be 1.." + MaxNativeSide + " pixels.");
            if (!Enum.IsDefined(typeof(CanvasResampling), resampling)) throw new ArgumentOutOfRangeException(nameof(resampling));
            long budget = sourceBudgetBytes ?? SourceBudgetBytes;
            if (budget < 0) throw new ArgumentOutOfRangeException(nameof(sourceBudgetBytes));
            double sx = width / (double)Width, sy = height / (double)Height, scale = Math.Sqrt(sx * sy);
            var xs = new ResampleAxis(Width, width, resampling); var ys = new ResampleAxis(Height, height, resampling);
            var notes = new List<string>(); var surfacePaths = new List<Guid>();
            var copy = new PaintDocument(width, height, tileSize, undoBudgetBytes, Id)
            {
                sourceBudgetBytes = budget, activeStrokeBudgetBytes = activeStrokeBudgetBytes, minimumUndoSteps = minimumUndoSteps,
                filterWorkingBudget = filterWorkingBudget, filterCacheBudget = filterCacheBudget, filterBlockPixels = filterBlockPixels,
                fillImageCacheBudget = fillImageCacheBudget, imageResources = imageResources,
            };
            copy.normalSettings = ScaledNormalSettings(normalSettings, scale, notes);
            string size = width + "×" + height;
            Action<long> ensure = growth =>
            {
                if (growth > 0 && growth > budget - copy.AllocatedBytes)
                    throw new InvalidOperationException("At " + size + " the layers need more than the " + Mib(budget) + " MiB layer pixel budget (" + Mib(copy.AllocatedBytes + growth)
                        + " MiB before every layer was resampled). Nothing was changed. Raise the budget (Project Settings > YoluPainter > Layer pixels) or choose a smaller size.");
            };
            foreach (var layer in layers)
            {
                var target = CopyLayerShell(copy, layer);
                var canvasPath = layer.Path as CanvasPath;
                foreach (var entry in layer.Channels)
                {
                    var surface = target.GetChannel(entry.Key);
                    if (canvasPath != null && canvasPath.Channel == entry.Key) continue; // 下で縮尺したパスから描き直す
                    CanvasResampler.Resample(entry.Value, surface, xs, ys, resampling, entry.Key == PaintChannel.Normal, ensure);
                }
                foreach (PaintChannel channel in Enum.GetValues(typeof(PaintChannel)))
                    if (target.IsChannelEnabled(channel) != layer.IsChannelEnabled(channel)) target.Enable(channel, layer.IsChannelEnabled(channel));
                if (layer.Kind == LayerKind.Raster && !layer.Channels.ContainsKey(PaintChannel.Color)) target.DropEmptyChannel(PaintChannel.Color);
                if (layer.Mask != null)
                {
                    var mask = copy.AddLayerMask(target.Id);
                    mask.Enabled = layer.Mask.Enabled; mask.Inverted = layer.Mask.Inverted; mask.Density = layer.Mask.Density;
                    CanvasResampler.Resample(layer.Mask.Surface, mask.Surface, xs, ys, resampling, false, ensure);
                }
                if (canvasPath != null)
                {
                    var scaled = ScaledCanvasPath(canvasPath, sx, sy, scale, layer.Name, notes);
                    target.Path = scaled;
                    SparseTileSurface rendered;
                    try { rendered = CanvasPathRenderer.Render(copy, scaled); }
                    catch (InvalidOperationException ex) { throw new InvalidOperationException("The path on '" + layer.Name + "' cannot be drawn at " + size + ": " + ex.Message + " Nothing was changed.", ex); }
                    var surface = target.GetChannel(scaled.Channel);
                    foreach (var coord in rendered.EnumerateTileCoordinates())
                    {
                        var tile = rendered.Capture(coord);
                        ensure(tile.ByteSize); surface.EnsureGrowth(tile.ByteSize); surface.Restore(coord, tile);
                    }
                }
                else if (layer.Path is SurfacePath) { target.Path = layer.Path; surfacePaths.Add(layer.Id); }
                CopyFilters(copy, layer, target, scale, size, notes);
            }
            copy.ValidateStructure();
            // ロックは最後に写す（写しの層へのフィルターなどの追加をロックが断らないように）。大きさの変更はロックに関わらず全部の層に効く（Photoshop の画像解像度と同じ）
            foreach (var layer in layers) copy.SetLocksForLoad(copy.GetLayer(layer.Id), layer.Locks);
            if (selection != null)
            {
                var resized = selection.ResampledTo(width, height, xs, ys, resampling, tileSize);
                copy.selection = resized.IsEmpty ? null : resized;
                if (copy.selection == null) notes.Add("The selection was too small to keep at " + size + "; nothing is selected.");
            }
            copy.ClearHistory();
            copy.Revision = Revision + 1; // 版で覚えた表示（サムネイルなど）が同じ版の古い絵を使わないように、元の続きにする
            return new ResampledDocument(copy, notes, surfacePaths);
        }

        /// <summary>The layer itself in the copy (kind, id, name, group, attributes, fill values, images and projection, adjustment) without pixels.</summary>
        static PaintLayer CopyLayerShell(PaintDocument copy, PaintLayer layer)
        {
            PaintLayer target;
            switch (layer.Kind)
            {
                case LayerKind.Fill:
                    var values = new Dictionary<PaintChannel, Rgba32>(); foreach (var entry in layer.FillValues) values.Add(entry.Key, entry.Value);
                    target = copy.AddFillLayer(layer.Name, values, layer.Id);
                    // 画像と投影は UV・モデルの空間で決まるので、大きさによらずそのまま写す
                    if (layer.FillImages.Count > 0 || !layer.Projection.Equals(FillProjection.Default)) copy.SetFillImagesForLoad(target, layer.FillImages, layer.Projection);
                    break;
                case LayerKind.Adjustment: target = copy.AddAdjustmentLayer(layer.Name, layer.Adjustment, layer.EnabledChannels, layer.Id); break;
                case LayerKind.Group: target = copy.AddGroup(layer.Name, layer.Id); break;
                default: target = copy.AddLayer(layer.Name, layer.Id); break;
            }
            copy.SetParentForLoad(target, layer.ParentId);
            target.Visible = layer.Visible; target.Opacity = layer.Opacity; target.BlendMode = layer.BlendMode; target.Clipping = layer.Clipping;
            target.CopyChannelBlendsFrom(layer);
            return target;
        }

        /// <summary>The layer's content and mask filter stacks (same ids, order, on/off, strength, channels) with radii scaled.</summary>
        static void CopyFilters(PaintDocument copy, PaintLayer layer, PaintLayer target, double scale, string size, List<string> notes)
        {
            try
            {
                foreach (var effect in layer.Filters)
                    copy.AddFilter(target.Id, FilterTarget.Content, ScaledFilter(effect.Settings, scale, layer.Name, notes), effect.Channels, -1, effect.Id, effect.Enabled, effect.Strength);
                if (layer.Mask != null)
                    foreach (var effect in layer.Mask.Filters)
                        copy.AddFilter(target.Id, FilterTarget.Mask, ScaledFilter(effect.Settings, scale, layer.Name + " (mask)", notes), null, -1, effect.Id, effect.Enabled, effect.Strength);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("Layer '" + layer.Name + "' at " + size + ": " + ex.Message + " Nothing was changed.", ex);
            }
        }

        static FilterSettings ScaledFilter(FilterSettings settings, double scale, string owner, List<string> notes)
        {
            if (settings.HaloPixels == 0 || scale == 1) return settings;
            int max = settings.Type == FilterType.Sharpen ? FilterSettings.MaxSharpenRadius : FilterSettings.MaxBlurRadius;
            double wanted = settings.Radius * scale;
            int radius = (int)Math.Round(wanted, MidpointRounding.AwayFromZero);
            if (radius > max) { notes.Add(settings.Name + " on '" + owner + "': radius " + settings.Radius + " → " + max + " px (the largest; " + Format(wanted) + " px would keep the look)."); radius = max; }
            else if (radius < 1) { notes.Add(settings.Name + " on '" + owner + "': radius " + settings.Radius + " → 1 px (the smallest; " + Format(wanted) + " px would keep the look)."); radius = 1; }
            return radius == settings.Radius ? settings : settings.WithRadius(radius);
        }

        static NormalSettings ScaledNormalSettings(NormalSettings settings, double scale, List<string> notes)
        {
            if (scale == 1 || settings.Strength == 0) return settings;
            double wanted = settings.Strength * scale, strength = Math.Max(-NormalSettings.MaxStrength, Math.Min(NormalSettings.MaxStrength, wanted));
            if (strength != wanted) notes.Add("Height → Normal strength " + Format(settings.Strength) + " → " + Format(strength) + " (the limit; " + Format(wanted) + " would keep the slope).");
            return settings.WithStrength(strength);
        }

        static CanvasPath ScaledCanvasPath(CanvasPath path, double sx, double sy, double scale, string owner, List<string> notes)
        {
            var brush = path.Brush.Clone(); double wanted = brush.RadiusWorld * scale;
            brush.RadiusWorld = Math.Min(4096, wanted);
            if (brush.RadiusWorld != wanted) notes.Add("The path on '" + owner + "': brush radius " + Format(path.Brush.RadiusWorld) + " → 4096 px (the largest; " + Format(wanted) + " px would keep the look).");
            var points = new List<CanvasPoint>(); bool clamped = false;
            foreach (var p in path.Points)
            {
                double x = p.X * sx, y = p.Y * sy;
                if (Math.Abs(x) > CanvasPoint.Limit || Math.Abs(y) > CanvasPoint.Limit) { clamped = true; x = Math.Max(-CanvasPoint.Limit, Math.Min(CanvasPoint.Limit, x)); y = Math.Max(-CanvasPoint.Limit, Math.Min(CanvasPoint.Limit, y)); }
                points.Add(new CanvasPoint(x, y, p.Pressure));
            }
            if (clamped) notes.Add("The path on '" + owner + "': points far outside the canvas were moved in to ±" + CanvasPoint.Limit.ToString(CultureInfo.InvariantCulture) + " px.");
            return new CanvasPath(path.Id, path.Channel, brush, points);
        }

        static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public sealed partial class SelectionMask
    {
        /// <summary>The selection at another canvas size (amounts resampled like a mask).</summary>
        internal SelectionMask ResampledTo(int width, int height, ResampleAxis xs, ResampleAxis ys, CanvasResampling resampling, int tileSize)
        {
            var mask = new SelectionMask(width, height, tileSize);
            CanvasResampler.Resample(surface, mask.surface, xs, ys, resampling, false, null);
            return mask;
        }
    }

    /// <summary>The source pixels one axis of a resampled canvas reads: target index i reads the source indices Start[i] ..
    /// Start[i] + Count[i] − 1 with the weights Weight[Offset[i] + k] (summing to 1). Computed with integer arithmetic, so the
    /// same sizes always give the same weights.</summary>
    internal sealed class ResampleAxis
    {
        internal readonly int Source, Target;
        internal readonly int[] Start, Count, Offset;
        internal readonly double[] Weight;

        internal ResampleAxis(int source, int target, CanvasResampling resampling)
        {
            Source = source; Target = target;
            Start = new int[target]; Count = new int[target]; Offset = new int[target];
            var weights = new List<double>();
            long s = source, t = target;
            for (int i = 0; i < target; i++)
            {
                Offset[i] = weights.Count;
                switch (resampling)
                {
                    case CanvasResampling.Nearest:
                        Start[i] = (int)((2 * i + 1) * s / (2 * t)); Count[i] = 1; weights.Add(1);
                        break;
                    case CanvasResampling.Bilinear:
                    {
                        // 中心 (i + 0.5)·s/t − 0.5 = num / den（den = 2t）。その左の画素 j0 と右の画素 j0 + 1 を距離で混ぜる
                        long num = (2 * i + 1) * s - t, den = 2 * t;
                        long j0 = num >= 0 ? num / den : -((-num + den - 1) / den), rem = num - j0 * den;
                        double frac = rem / (double)den;
                        long a = Math.Max(0, j0), b = Math.Min(s - 1, j0 + 1);
                        if (rem == 0 || a == b) { Start[i] = (int)(rem == 0 ? Math.Max(0, Math.Min(s - 1, j0)) : a); Count[i] = 1; weights.Add(1); }
                        else { Start[i] = (int)a; Count[i] = 2; weights.Add(1 - frac); weights.Add(frac); }
                        break;
                    }
                    default:
                    {
                        // 新しい画素が覆う [i·s, (i+1)·s) と元の画素 j の [j·t, (j+1)·t)（どちらも 1/t 画素の単位）の重なり
                        long lo = i * s, hi = (i + 1) * s;
                        long first = lo / t, last = (hi - 1) / t;
                        Start[i] = (int)first; Count[i] = (int)(last - first + 1);
                        for (long j = first; j <= last; j++) weights.Add((Math.Min(hi, (j + 1) * t) - Math.Max(lo, j * t)) / (double)s);
                        break;
                    }
                }
            }
            Weight = weights.ToArray();
        }
        internal int Last(int i) => Start[i] + Count[i] - 1;
    }

    /// <summary>Resamples one RGBA8 surface into an empty surface of another size (<see cref="CanvasResampling"/> has the formulas).
    /// Target tiles are computed in parallel (each from the source only) and stored in tile order on the calling thread, where
    /// ensure(growth) is asked before each one; it throws to stop (the caller drops the half-made target).</summary>
    internal static class CanvasResampler
    {
        internal static void Resample(SparseTileSurface source, SparseTileSurface target, ResampleAxis xs, ResampleAxis ys, CanvasResampling resampling, bool normal, Action<long> ensure)
        {
            if (source.TileCount == 0) return;
            int sourceTile = source.TileSize, tile = target.TileSize, w = target.Width, h = target.Height;
            var tiles = new Dictionary<TileCoord, TileStorage>();
            foreach (var coord in source.EnumerateTileCoordinates()) tiles.Add(coord, source.PeekTile(coord)); // 読むだけ（呼ぶ側はこの間に元を変えない）
            // 行き先のタイルごとに、読む元のタイルが全部無ければ飛ばし、全部が同じ一様な色ならその色で埋める（計算しない）
            var work = new List<(TileCoord coord, Rgba32? uniform)>();
            int columns = (w + tile - 1) / tile, rows = (h + tile - 1) / tile;
            for (int ty = 0; ty < rows; ty++)
                for (int tx = 0; tx < columns; tx++)
                {
                    int x0 = tx * tile, x1 = Math.Min(w, x0 + tile) - 1, y0 = ty * tile, y1 = Math.Min(h, y0 + tile) - 1;
                    int sx0 = xs.Start[x0] / sourceTile, sx1 = xs.Last(x1) / sourceTile, sy0 = ys.Start[y0] / sourceTile, sy1 = ys.Last(y1) / sourceTile;
                    bool any = false, allUniform = true; Rgba32 color = default; bool first = true;
                    for (int y = sy0; y <= sy1; y++)
                        for (int x = sx0; x <= sx1; x++)
                        {
                            if (!tiles.TryGetValue(new TileCoord(x, y), out var t)) { allUniform = false; continue; }
                            any = true;
                            if (!allUniform) continue;
                            if (!t.IsUniform) { allUniform = false; continue; }
                            var c = t.Get(0);
                            if (first) { color = c; first = false; } else if (c != color) allUniform = false;
                        }
                    if (any) work.Add((new TileCoord(tx, ty), allUniform ? color : (Rgba32?)null));
                }
            int degree = CoreParallelism.Degree, batch = degree > 1 ? Math.Min(work.Count, degree * 4) : 1;
            var buffers = new byte[batch][]; var uniform = new bool[batch]; var readers = new TileReader[batch];
            var reader = new TileReader(tiles, source.Width, source.Height, sourceTile);
            for (int start = 0; start < work.Count; start += batch)
            {
                int count = Math.Min(batch, work.Count - start), offset = start;
                for (int k = 0; k < count; k++) if (buffers[k] == null) { buffers[k] = new byte[tile * tile * 4]; readers[k] = reader.View(); }
                CoreParallelism.For(count, degree, k =>
                {
                    var item = work[offset + k];
                    Render(readers[k], xs, ys, resampling, normal, item.coord, item.uniform, tile, w, h, buffers[k]);
                    uniform[k] = TileStorage.Uniformity(buffers[k]);
                });
                for (int k = 0; k < count; k++)
                {
                    var after = TileStorage.FromBytes(buffers[k], uniform[k]);
                    if (after == null) continue;
                    ensure?.Invoke(after.ByteSize);
                    target.EnsureGrowth(after.ByteSize);
                    target.Restore(work[offset + k].coord, after);
                }
            }
        }

        /// <summary>Writes one target tile (TileSize² RGBA, padding zero).</summary>
        static void Render(TileReader reader, ResampleAxis xs, ResampleAxis ys, CanvasResampling resampling, bool normal, TileCoord coord, Rgba32? uniform, int tile, int w, int h, byte[] bytes)
        {
            Array.Clear(bytes, 0, bytes.Length);
            int x0 = coord.X * tile, y0 = coord.Y * tile, tw = Math.Min(tile, w - x0), th = Math.Min(tile, h - y0);
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    Rgba32 p = uniform ?? Pixel(reader, xs, ys, normal, x0 + x, y0 + y);
                    int i = (y * tile + x) * 4;
                    bytes[i] = p.R; bytes[i + 1] = p.G; bytes[i + 2] = p.B; bytes[i + 3] = p.A;
                }
        }

        static Rgba32 Pixel(TileReader reader, ResampleAxis xs, ResampleAxis ys, bool normal, int x, int y)
        {
            int cx = xs.Count[x], cy = ys.Count[y], ox = xs.Offset[x], oy = ys.Offset[y], jx = xs.Start[x], jy = ys.Start[y];
            if (cx == 1 && cy == 1) return reader.Get(jx, jy);
            double a = 0, r = 0, g = 0, b = 0, zw = 0, zr = 0, zg = 0, zb = 0;
            bool same = true; Rgba32 firstPixel = default; bool first = true;
            for (int v = 0; v < cy; v++)
            {
                double wy = ys.Weight[oy + v]; if (wy <= 0) continue;
                for (int u = 0; u < cx; u++)
                {
                    double wgt = wy * xs.Weight[ox + u]; if (wgt <= 0) continue;
                    var p = reader.Get(jx + u, jy + v);
                    if (first) { firstPixel = p; first = false; } else if (same && p != firstPixel) same = false;
                    if (p.A == 0) { zw += wgt; zr += wgt * p.R; zg += wgt * p.G; zb += wgt * p.B; continue; }
                    double k = wgt * p.A; a += k; r += k * p.R; g += k * p.G; b += k * p.B;
                }
            }
            if (same) return firstPixel; // 同じ画素だけを読むならその画素（Normal を正規化し直して変えない、一様な所と継ぎ目を作らない）
            byte alpha = MathUtil.ToByte(a / 255);
            if (alpha == 0)
                return zw > 0 ? new Rgba32(MathUtil.ToByte(zr / zw / 255), MathUtil.ToByte(zg / zw / 255), MathUtil.ToByte(zb / zw / 255), 0) : Rgba32.Transparent;
            if (normal) return NormalMaps.Encode(2 * r / a / 255 - 1, 2 * g / a / 255 - 1, 2 * b / a / 255 - 1, alpha);
            return new Rgba32(MathUtil.ToByte(r / a / 255), MathUtil.ToByte(g / a / 255), MathUtil.ToByte(b / a / 255), alpha);
        }

        /// <summary>Reads source pixels (inside the canvas) from the tile map, remembering the last tile. One per thread.</summary>
        sealed class TileReader
        {
            readonly Dictionary<TileCoord, TileStorage> tiles; readonly int width, height, tile;
            int lastX = -1, lastY = -1; TileStorage last;
            internal TileReader(Dictionary<TileCoord, TileStorage> tiles, int width, int height, int tile) { this.tiles = tiles; this.width = width; this.height = height; this.tile = tile; }
            internal TileReader View() => new TileReader(tiles, width, height, tile);
            internal Rgba32 Get(int x, int y)
            {
                int tx = x / tile, ty = y / tile;
                if (tx != lastX || ty != lastY) { lastX = tx; lastY = ty; tiles.TryGetValue(new TileCoord(tx, ty), out last); }
                return last == null ? Rgba32.Transparent : last.Get(((y - ty * tile) * tile + x - tx * tile) * 4);
            }
        }
    }
}
