using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    public enum GradientShape { Linear, Radial }

    /// <summary>A two-colour gradient: Linear runs from (X0, Y0) to (X1, Y1); Radial grows from (X0, Y0) out to the distance
    /// of (X1, Y1). Colours are interpolated premultiplied (so a fade to transparent keeps its colour), then laid over the
    /// layer with Opacity like a Normal paint.</summary>
    public sealed class GradientSettings
    {
        public GradientShape Shape = GradientShape.Linear;
        public double X0, Y0, X1, Y1;
        public Rgba32 From = new Rgba32(0, 0, 0), To = Rgba32.Transparent;
        public double Opacity = 1;
        public void Validate()
        {
            foreach (var v in new[] { X0, Y0, X1, Y1, Opacity }) MathUtil.RequireFinite(v, "gradient");
            if (Opacity < 0 || Opacity > 1) throw new ArgumentOutOfRangeException(nameof(Opacity));
            if (!Enum.IsDefined(typeof(GradientShape), Shape)) throw new ArgumentOutOfRangeException(nameof(Shape));
        }
        internal Rgba32 ColorAt(double x, double y)
        {
            double dx = X1 - X0, dy = Y1 - Y0, length2 = dx * dx + dy * dy, t;
            if (length2 <= 1e-12) t = 1;
            else if (Shape == GradientShape.Linear) t = ((x - X0) * dx + (y - Y0) * dy) / length2;
            else t = Math.Sqrt(((x - X0) * (x - X0) + (y - Y0) * (y - Y0)) / length2);
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            double fa = From.A / 255.0 * (1 - t), ta = To.A / 255.0 * t, a = fa + ta;
            if (a <= 0) return Rgba32.Transparent;
            return new Rgba32(MathUtil.ToByte((From.R / 255.0 * fa + To.R / 255.0 * ta) / a), MathUtil.ToByte((From.G / 255.0 * fa + To.G / 255.0 * ta) / a),
                MathUtil.ToByte((From.B / 255.0 * fa + To.B / 255.0 * ta) / a), MathUtil.ToByte(a));
        }
    }

    /// <summary>Selections and whole-region edits (fill, gradient). Each edit is one undo step made of exact tile
    /// before/after states, like a stroke.</summary>
    public sealed partial class PaintDocument
    {
        SelectionMask selection;
        /// <summary>The current selection, or null when nothing is selected (then edits apply everywhere).</summary>
        public SelectionMask Selection => selection;

        /// <summary>Replaces the selection (null deselects). An empty mask also deselects: a selection with nothing in it
        /// would make every brush silently do nothing. Undoable; it does not change any pixel.</summary>
        public void SetSelection(SelectionMask mask)
        {
            EnsureNoStroke();
            if (mask != null && (mask.Width != Width || mask.Height != Height || mask.TileSize != TileSize)) throw new ArgumentException("The selection must match the document size.", nameof(mask));
            var next = mask == null || mask.IsEmpty ? null : mask; var old = selection;
            if (ReferenceEquals(next, old)) return;
            Execute(new DelegateCommand(() => selection = next, () => selection = old, 64 + (next == null ? 0 : next.AllocatedBytes)));
        }
        public void ClearSelection() { SetSelection(null); }
        /// <summary>Puts back a selection read with the document (<see cref="Persistence.SelectionBinary"/>): no undo step and no new
        /// revision, so the freshly opened file still counts as saved. Only before any history exists.</summary>
        public void RestoreSelection(SelectionMask mask)
        {
            EnsureNoStroke();
            if (CanUndo || CanRedo) throw new InvalidOperationException("A selection can only be restored right after the document is loaded.");
            if (mask != null && (mask.Width != Width || mask.Height != Height || mask.TileSize != TileSize)) throw new ArgumentException("The selection must match the document size.", nameof(mask));
            selection = mask == null || mask.IsEmpty ? null : mask;
        }

        /// <summary>Fills a layer's channel with a colour where region (else the selection, else everything) allows, by
        /// opacity × the region's amount. Erase removes alpha instead. Returns false when no pixel changed. With the layer's
        /// transparent pixels locked every pixel keeps its alpha (<see cref="LayerLocks.Transparency"/>; erasing is refused).</summary>
        public bool Fill(Guid layerId, PaintChannel channel, Rgba32 color, double opacity = 1, SelectionMask region = null, bool erase = false)
        {
            MathUtil.RequireFinite(opacity, nameof(opacity)); if (opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
            var surface = PaintableSurface(layerId, channel, erase, out bool keepAlpha);
            if (keepAlpha) return EditRegion(surface, region, (x, y, start, amount) => PaintKeepingAlpha(start, color, opacity * amount));
            return EditRegion(surface, region, (x, y, start, amount) =>
            {
                double a = opacity * amount;
                if (!erase) return CpuCompositor.BlendUnchecked(start, color, a, LayerBlendMode.Normal); // a is 0..1 (checked above)
                byte alpha = MathUtil.ToByte(start.A / 255.0 * (1 - a * color.A / 255.0));
                return alpha == 0 ? Rgba32.Transparent : new Rgba32(start.R, start.G, start.B, alpha);
            });
        }

        /// <summary>Paints a layer's mask over a region: hides by amount × region (or reveals with reveal).</summary>
        public bool FillMask(Guid layerId, double amount = 1, SelectionMask region = null, bool reveal = false)
        {
            MathUtil.RequireFinite(amount, nameof(amount)); if (amount < 0 || amount > 1) throw new ArgumentOutOfRangeException(nameof(amount));
            EnsureNoStroke(); var mask = RequireMask(layerId, out var owner);
            RefuseLockedAttributes(owner);
            double target = reveal ? 0 : 255;
            return EditRegion(mask.Surface, region, (x, y, start, coverage) =>
            {
                byte hide = MathUtil.ToByte((start.A + (target - start.A) * amount * coverage) / 255.0);
                return hide == 0 ? Rgba32.Transparent : new Rgba32(0, 0, 0, hide);
            });
        }

        /// <summary>Lays a gradient over a layer's channel inside region (else the selection, else everything).</summary>
        public bool Gradient(Guid layerId, PaintChannel channel, GradientSettings gradient, SelectionMask region = null)
        {
            if (gradient == null) throw new ArgumentNullException(nameof(gradient)); gradient.Validate();
            var g = new GradientSettings { Shape = gradient.Shape, X0 = gradient.X0, Y0 = gradient.Y0, X1 = gradient.X1, Y1 = gradient.Y1, From = gradient.From, To = gradient.To, Opacity = gradient.Opacity };
            var surface = PaintableSurface(layerId, channel, false, out bool keepAlpha);
            if (keepAlpha) return EditRegion(surface, region, (x, y, start, amount) => PaintKeepingAlpha(start, g.ColorAt(x + .5, y + .5), g.Opacity * amount));
            return EditRegion(surface, region, (x, y, start, amount) => CpuCompositor.BlendUnchecked(start, g.ColorAt(x + .5, y + .5), g.Opacity * amount, LayerBlendMode.Normal)); // 0..1 (Validate)
        }

        /// <summary>Replaces a layer's channel with an image (straight RGBA8, bottom-left origin, the document's size) inside the
        /// selection (or everywhere without one, or when withinSelection is false), as one undo step. Where the selection is fully on, the image's pixels are copied
        /// exactly (the RGB of transparent pixels included); a partial selection amount interpolates between the old and the new
        /// pixel in premultiplied space. Returns false when no pixel changed.</summary>
        /// <remarks>Refused when the layer's image pixels are locked; with its transparent pixels locked only the colour is replaced
        /// (each pixel keeps its alpha, transparent pixels stay as they are).</remarks>
        public bool ReplacePixels(Guid layerId, PaintChannel channel, byte[] rgba, bool withinSelection = true)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (rgba.LongLength != (long)Width * Height * 4) throw new ArgumentException("The image must be " + Width + " × " + Height + " RGBA8 (" + ((long)Width * Height * 4) + " bytes).", nameof(rgba));
            var surface = PaintableSurface(layerId, channel, false, out bool keepAlpha);
            return EditRegion(surface, null, withinSelection, (x, y, start, amount) =>
            {
                int o = (y * Width + x) * 4; var image = new Rgba32(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]);
                if (keepAlpha) return ReplaceKeepingAlpha(start, image, amount);
                return amount >= 1 ? image : Interpolate(start, image, amount);
            });
        }

        /// <summary>Premultiplied interpolation from a to b by t (0..1), returned straight (transparent results are transparent black).</summary>
        static Rgba32 Interpolate(Rgba32 a, Rgba32 b, double t)
        {
            double aa = a.A / 255.0, ba = b.A / 255.0, alpha = aa + (ba - aa) * t;
            if (alpha <= 0) return Rgba32.Transparent;
            double Channel(byte ac, byte bc) => (ac / 255.0 * aa + (bc / 255.0 * ba - ac / 255.0 * aa) * t) / alpha;
            return new Rgba32(MathUtil.ToByte(Channel(a.R, b.R)), MathUtil.ToByte(Channel(a.G, b.G)), MathUtil.ToByte(Channel(a.B, b.B)), MathUtil.ToByte(alpha));
        }

        /// <summary>The surface a fill, gradient or image replacement changes, after the locks are checked (refused under Lock Image
        /// Pixels and Lock All, and for an erase under Lock Transparent Pixels); keepAlpha tells whether each pixel must keep its alpha.</summary>
        SparseTileSurface PaintableSurface(Guid layerId, PaintChannel channel, bool erase, out bool keepAlpha)
        {
            EnsureNoStroke(); PaintLayer.ValidateChannel(channel);
            var layer = GetLayer(layerId);
            if (layer.Kind != LayerKind.Raster) throw new InvalidOperationException("Only paint layers have pixels to fill. Use the layer's mask for fill, adjustment and group layers.");
            if (!layer.IsChannelEnabled(channel)) throw new InvalidOperationException("Enable the target channel before filling.");
            RefuseLockedPixels(layer, erase);
            RefusePathLayer(layer);
            keepAlpha = KeepsAlpha(layer);
            return layer.GetChannel(channel);
        }

        /// <summary>The effective region of an edit: the given region within the selection, or the selection, or null (everything).</summary>
        SelectionMask EffectiveRegion(SelectionMask region)
        {
            if (region != null && (region.Width != Width || region.Height != Height || region.TileSize != TileSize)) throw new ArgumentException("The region must match the document size.", nameof(region));
            if (region == null) return selection;
            return selection == null ? region : region.Combine(selection, SelectionCombine.Intersect);
        }

        /// <summary>Applies pixel(x, y, start, amount) to every pixel the effective region covers, as one undo step. Budgets are
        /// checked before each tile; on failure every tile already changed is restored and nothing is recorded.</summary>
        bool EditRegion(SparseTileSurface surface, SelectionMask region, Func<int, int, Rgba32, double, Rgba32> pixel) => EditRegion(surface, region, true, pixel);
        bool EditRegion(SparseTileSurface surface, SelectionMask region, bool withinSelection, Func<int, int, Rgba32, double, Rgba32> pixel)
        {
            var effective = withinSelection ? EffectiveRegion(region) : region;
            int tile = TileSize, n = tile * tile;
            var coords = new List<TileCoord>(effective != null ? effective.Tiles : EnumerateCanvasTiles());
            var changes = new List<TileChange>(); long rollback = 0;
            // 新しいタイルの計算はタイルごとに独立（pixel は純粋な関数、面とマスクは読むだけ）なので、まとめて並列に計算し、
            // 予算の確認と書き込みはタイルの順にこのスレッドで行う（予算で止まる所も、止まったときに戻すものも逐次と同じ）。
            int degree = CoreParallelism.Degree, batch = degree > 1 ? Math.Min(coords.Count, degree * 4) : 1;
            var selected = new bool[batch]; var same = new bool[batch]; var uniform = new bool[batch]; var bytes = new byte[batch][]; var amounts = new byte[batch][];
            for (int k = 0; k < batch; k++) { bytes[k] = new byte[n * 4]; amounts[k] = new byte[n]; }
            try
            {
                for (int start = 0; start < coords.Count; start += batch)
                {
                    int count = Math.Min(batch, coords.Count - start), first = start;
                    // ワーカーでは割り当てない（エディタの GC は割り当てのたびに全スレッドを止め得る）。新しい画素を作り、元と同じかだけ見る
                    CoreParallelism.For(count, degree, k =>
                    {
                        var coord = coords[first + k];
                        var tileBytes = bytes[k]; var tileAmounts = amounts[k];
                        selected[k] = effective == null || effective.CopyTile(coord, tileAmounts);
                        if (!selected[k]) return;
                        if (effective == null) for (int i = 0; i < n; i++) tileAmounts[i] = 255;
                        surface.CopyTile(coord, tileBytes);
                        int w = Math.Min(tile, Width - coord.X * tile), h = Math.Min(tile, Height - coord.Y * tile);
                        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                        {
                            int i = y * tile + x; if (tileAmounts[i] == 0) continue;
                            int o = i * 4; var startPixel = new Rgba32(tileBytes[o], tileBytes[o + 1], tileBytes[o + 2], tileBytes[o + 3]);
                            var next = pixel(coord.X * tile + x, coord.Y * tile + y, startPixel, tileAmounts[i] / 255.0);
                            tileBytes[o] = next.R; tileBytes[o + 1] = next.G; tileBytes[o + 2] = next.B; tileBytes[o + 3] = next.A;
                        }
                        same[k] = TileStorage.SameAs(surface.PeekTile(coord), tileBytes);
                        uniform[k] = !same[k] && TileStorage.Uniformity(tileBytes);
                    });
                    for (int k = 0; k < count; k++)
                    {
                        if (!selected[k]) continue;
                        var coord = coords[first + k];
                        var before = surface.Capture(coord);
                        if (same[k]) continue;
                        var after = TileStorage.FromBytes(bytes[k], uniform[k]);
                        rollback += 64 + (before == null ? 0 : before.ByteSize);
                        EnsureStrokeBudget(rollback);
                        surface.EnsureGrowth((after == null ? 0 : after.ByteSize) - surface.TileBytesAt(coord));
                        surface.Restore(coord, after);
                        changes.Add(new TileChange(coord, before, after));
                    }
                }
            }
            catch
            {
                for (int i = changes.Count - 1; i >= 0; i--) surface.Restore(changes[i].Coord, changes[i].Before);
                throw;
            }
            if (changes.Count == 0) return false;
            Revision++; Push(new TileStrokeCommand(surface, changes, Guid.NewGuid()));
            return true;
        }
    }
}
