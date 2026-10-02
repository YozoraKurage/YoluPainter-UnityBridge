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

        /// <summary>Fills a layer's channel with a colour where region (else the selection, else everything) allows, by
        /// opacity × the region's amount. Erase removes alpha instead. Returns false when no pixel changed.</summary>
        public bool Fill(Guid layerId, PaintChannel channel, Rgba32 color, double opacity = 1, SelectionMask region = null, bool erase = false)
        {
            MathUtil.RequireFinite(opacity, nameof(opacity)); if (opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
            var surface = PaintableSurface(layerId, channel);
            return EditRegion(surface, region, (x, y, start, amount) =>
            {
                double a = opacity * amount;
                if (!erase) return CpuCompositor.Blend(start, color, a);
                byte alpha = MathUtil.ToByte(start.A / 255.0 * (1 - a * color.A / 255.0));
                return alpha == 0 ? Rgba32.Transparent : new Rgba32(start.R, start.G, start.B, alpha);
            });
        }

        /// <summary>Paints a layer's mask over a region: hides by amount × region (or reveals with reveal).</summary>
        public bool FillMask(Guid layerId, double amount = 1, SelectionMask region = null, bool reveal = false)
        {
            MathUtil.RequireFinite(amount, nameof(amount)); if (amount < 0 || amount > 1) throw new ArgumentOutOfRangeException(nameof(amount));
            EnsureNoStroke(); var mask = RequireMask(layerId, out _);
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
            var surface = PaintableSurface(layerId, channel);
            return EditRegion(surface, region, (x, y, start, amount) => CpuCompositor.Blend(start, g.ColorAt(x + .5, y + .5), g.Opacity * amount));
        }

        SparseTileSurface PaintableSurface(Guid layerId, PaintChannel channel)
        {
            EnsureNoStroke(); PaintLayer.ValidateChannel(channel);
            var layer = GetLayer(layerId);
            if (layer.Kind != LayerKind.Raster) throw new InvalidOperationException("Only paint layers have pixels to fill. Use the layer's mask for fill, adjustment and group layers.");
            if (!layer.IsChannelEnabled(channel)) throw new InvalidOperationException("Enable the target channel before filling.");
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
        bool EditRegion(SparseTileSurface surface, SelectionMask region, Func<int, int, Rgba32, double, Rgba32> pixel)
        {
            var effective = EffectiveRegion(region);
            int tile = TileSize, n = tile * tile;
            var coords = new List<TileCoord>(effective != null ? effective.Tiles : EnumerateCanvasTiles());
            var changes = new List<TileChange>(); var bytes = new byte[n * 4]; var amounts = new byte[n]; long rollback = 0;
            try
            {
                foreach (var coord in coords)
                {
                    if (effective != null) { if (!effective.CopyTile(coord, amounts)) continue; }
                    else for (int i = 0; i < n; i++) amounts[i] = 255;
                    var before = surface.Capture(coord);
                    surface.CopyTile(coord, bytes);
                    int w = Math.Min(tile, Width - coord.X * tile), h = Math.Min(tile, Height - coord.Y * tile);
                    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    {
                        int i = y * tile + x; if (amounts[i] == 0) continue;
                        int o = i * 4; var start = new Rgba32(bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]);
                        var next = pixel(coord.X * tile + x, coord.Y * tile + y, start, amounts[i] / 255.0);
                        bytes[o] = next.R; bytes[o + 1] = next.G; bytes[o + 2] = next.B; bytes[o + 3] = next.A;
                    }
                    var after = TileStorage.FromBytes(bytes);
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
            if (changes.Count == 0) return false;
            Revision++; Push(new TileStrokeCommand(surface, changes, Guid.NewGuid()));
            return true;
        }
    }
}
