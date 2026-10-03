using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core.Paths;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Why a layer operation (duplicate, merge, copy, cut, paste) was refused. The exception's message is English; a UI shows
    /// its own text for the reason.</summary>
    public enum LayerOpRefusal
    {
        /// <summary>Merge down: the layer is the bottom one of its group (or of the document).</summary>
        NoLayerBelow,
        /// <summary>Merge down: the layer below is a group (merge the group first).</summary>
        LayerBelowIsGroup,
        /// <summary>Merge down: the layer below is an adjustment layer (it has no pixels to merge into).</summary>
        LayerBelowIsAdjustment,
        /// <summary>Merge down: the layer or the layer below is hidden.</summary>
        HiddenLayer,
        /// <summary>Merge down was asked for a group (use merge group).</summary>
        IsGroup,
        /// <summary>Merge group was asked for a layer that is not a group.</summary>
        NotGroup,
        /// <summary>Merge group: the group has no layers in it.</summary>
        EmptyGroup,
        /// <summary>Merge visible: no layer shows anything.</summary>
        NothingVisible,
        /// <summary>Copy / cut: the layer owns no pixels (a group or an adjustment layer).</summary>
        NoPixels,
        /// <summary>Copy / cut: there is nothing (no pixel with any value) in the selection.</summary>
        NothingToCopy,
        /// <summary>Copy / cut / paste: the pixels are larger than the clipboard limit.</summary>
        ClipboardTooLarge,
        /// <summary>The result would need more memory than the one-operation budget (<see cref="PaintDocument.ActiveStrokeBudgetBytes"/>).</summary>
        OperationBudget,
        /// <summary>Cut: the layer is drawn by a path (edit the path or rasterize it).</summary>
        PathLayer,
        /// <summary>Cut: only paint layers and masks can be cut (a fill layer is generated from its value).</summary>
        NotPaintLayer,
        /// <summary>Merge: the result would change how the document looks by more than the allowed difference.</summary>
        AppearanceChanges,
        /// <summary>The clipboard comes from a document of another tile layout or holds no pixels.</summary>
        InvalidClipboard,
        /// <summary>A lock on the layer (or a group it is in) refuses the operation (<see cref="LayerLockedException"/>).</summary>
        Locked,
        /// <summary>An operation on several layers needs them in the same group (grouping, merging).</summary>
        DifferentGroups,
    }

    /// <summary>A refused layer operation. Nothing was changed.</summary>
    public class LayerOpException : InvalidOperationException
    {
        public LayerOpRefusal Reason { get; }
        /// <summary>The bytes asked for (budget and clipboard refusals), else 0.</summary>
        public long Bytes { get; }
        /// <summary>The limit that was exceeded (budget and clipboard refusals), else 0.</summary>
        public long Limit { get; }
        public LayerOpException(LayerOpRefusal reason, string message, long bytes = 0, long limit = 0) : base(message) { Reason = reason; Bytes = bytes; Limit = limit; }
    }

    /// <summary>Where copied pixels came from.</summary>
    public enum ClipboardSource { Layer = 0, Mask = 1, Composite = 2 }

    /// <summary>Pixels copied from a layer's channel, a layer mask or the composite: a rectangle of straight RGBA8 (bottom-left origin,
    /// rows from the bottom), where it was in a document of the recorded size. Immutable. A mask is copied as grey (white = shown, the
    /// stored hide amount inverted) and opaque, like a mask copied in Photoshop.</summary>
    public sealed class PixelClipboard
    {
        readonly byte[] pixels;
        public int DocumentWidth { get; }
        public int DocumentHeight { get; }
        /// <summary>The rectangle in the source document (bottom-left origin).</summary>
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
        public ClipboardSource Source { get; }
        /// <summary>The channel it was copied from (Color for a mask).</summary>
        public PaintChannel Channel { get; }
        public long ByteSize => pixels.LongLength;

        public PixelClipboard(int documentWidth, int documentHeight, int x, int y, int width, int height, byte[] rgba, ClipboardSource source = ClipboardSource.Layer, PaintChannel channel = PaintChannel.Color)
        {
            if (documentWidth <= 0 || documentHeight <= 0) throw new ArgumentOutOfRangeException(nameof(documentWidth));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "The copied rectangle must not be empty.");
            if (x < 0 || y < 0 || (long)x + width > documentWidth || (long)y + height > documentHeight) throw new ArgumentOutOfRangeException(nameof(x), "The rectangle must lie inside the document.");
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (rgba.LongLength != (long)width * height * 4) throw new ArgumentException("The pixels must be width × height RGBA8.", nameof(rgba));
            if (!Enum.IsDefined(typeof(ClipboardSource), source)) throw new ArgumentOutOfRangeException(nameof(source));
            PaintLayer.ValidateChannel(channel);
            DocumentWidth = documentWidth; DocumentHeight = documentHeight; X = x; Y = y; Width = width; Height = height;
            Source = source; Channel = channel; pixels = (byte[])rgba.Clone();
        }
        /// <summary>A copy of the pixels (width × height RGBA8, rows from the bottom).</summary>
        public byte[] GetPixels() => (byte[])pixels.Clone();
        /// <summary>The pixel at (x, y) inside the rectangle.</summary>
        public Rgba32 GetPixel(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) throw new ArgumentOutOfRangeException("pixel");
            int o = (y * Width + x) * 4; return new Rgba32(pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]);
        }
        internal byte[] Pixels => pixels;
    }

    /// <summary>What <see cref="PaintDocument.PasteAsLayer"/> made.</summary>
    public sealed class PasteResult
    {
        public PaintLayer Layer { get; }
        /// <summary>Where the clipboard's bottom-left corner went (may be outside the canvas when it was clipped).</summary>
        public int X { get; }
        public int Y { get; }
        /// <summary>True when the document has another size than the copy's, so the pixels were centred instead of kept in place.</summary>
        public bool Centered { get; }
        /// <summary>Pixels with any value that fell outside the canvas and were cut off.</summary>
        public long ClippedPixels { get; }
        internal PasteResult(PaintLayer layer, int x, int y, bool centered, long clipped) { Layer = layer; X = x; Y = y; Centered = centered; ClippedPixels = clipped; }
    }

    /// <summary>Layer operations a painter expects from Photoshop / CLIP STUDIO: duplicate (with a group's contents), the clipboard
    /// (copy, copy merged, cut, paste as a new layer) and, in PaintDocument.Merge.cs, merge down / merge group / merge visible. Each change
    /// is one undo step and changes nothing when it is refused.</summary>
    public sealed partial class PaintDocument
    {
        // ───────────── duplicate ─────────────

        /// <summary>Duplicates a layer — a group with everything in it — with all of its attributes (visibility, opacity, blend mode,
        /// clipping, channels and which are enabled, fill values, adjustment, mask with its parameters and filters, content filters and
        /// generators, path) and puts the copy directly above the original, in the same group, as one undo step. The copy and everything in it
        /// get new IDs, and so do its filters, generators and paths. Pixels are shared copy-on-write, so the copy costs no time per pixel but
        /// counts in full against <see cref="SourceBudgetBytes"/> (refused before anything changes when it does not fit). The copy is
        /// independent: editing one never changes the other. A selection does not matter (the whole layer is copied).</summary>
        /// <param name="name">The copy's name (the original's when null). Layers inside a duplicated group keep their names.</param>
        public PaintLayer DuplicateLayer(Guid id, string name = null, Guid? newId = null)
        {
            EnsureNoStroke(); var layer = GetLayer(id);
            Guid topId = newId ?? Guid.NewGuid();
            if (topId == Guid.Empty) throw new ArgumentException("Layer ID must not be empty.", nameof(newId));
            foreach (var existing in layers) if (existing.Id == topId) throw new ArgumentException("Duplicate layer ID.", nameof(newId));
            var block = Block(layer); var ids = new Dictionary<Guid, Guid>(); var copies = new List<PaintLayer>(); long bytes = 0;
            foreach (var l in block) ids[l.Id] = l == layer ? topId : Guid.NewGuid();
            foreach (var l in block)
            {
                var copy = CloneLayer(l, ids[l.Id], l == layer ? (name ?? l.Name) : l.Name);
                copy.ParentId = ids.TryGetValue(l.ParentId, out var parent) ? parent : l.ParentId;
                copies.Add(copy); bytes += copy.AllocatedBytes;
            }
            var before = SnapshotStructure();
            layers.InsertRange(layers.IndexOf(layer) + 1, copies);
            var after = SnapshotStructure(); RestoreStructure(before);
            Execute(SwapStructure(before, after, 128 + bytes));
            return copies[copies.Count - 1];
        }

        /// <summary>A deep copy of a layer of this document under a new ID (not inserted; ParentId is the original's): attributes, channels
        /// (tiles shared copy-on-write), fill values, images and projection, adjustment, mask with its filters, content filters and the path, each effect and path
        /// with a new ID.</summary>
        PaintLayer CloneLayer(PaintLayer source, Guid id, string name)
        {
            var copy = new PaintLayer(this, name, id, source.Kind)
            { Visible = source.Visible, Opacity = source.Opacity, BlendMode = source.BlendMode, Clipping = source.Clipping, ParentId = source.ParentId, Adjustment = source.Adjustment, Locks = source.Locks };
            copy.CopyChannelBlendsFrom(source);
            foreach (var entry in source.FillValues) copy.SetFillValueInternal(entry.Key, entry.Value);
            if (source.Kind == LayerKind.Fill && (source.FillImages.Count > 0 || !source.Projection.Equals(FillProjection.Default))) SetFillImagesForLoad(copy, source.FillImages, source.Projection);
            foreach (var entry in source.Channels)
            {
                var surface = copy.GetChannel(entry.Key);
                foreach (var coord in entry.Value.EnumerateTileCoordinates()) surface.Restore(coord, entry.Value.Capture(coord));
            }
            foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) copy.Enable(c, source.IsChannelEnabled(c));
            foreach (var e in source.FilterList) copy.FilterList.Add(CloneEffect(e));
            if (source.FilterList.Count > 0) copy.FilterRevision = ++filterRevisionCounter;
            if (source.Mask != null) copy.Mask = CloneMask(source.Mask, copy);
            if (source.Path is SurfacePath surfacePath) copy.Path = new SurfacePath(Guid.NewGuid(), surfacePath.Channel, surfacePath.ModelFingerprint, surfacePath.Brush, surfacePath.Points, surfacePath.Material);
            else if (source.Path is CanvasPath canvasPath) copy.Path = new CanvasPath(Guid.NewGuid(), canvasPath.Channel, canvasPath.Brush, canvasPath.Points, canvasPath.Material);
            return copy;
        }
        static FilterEffect CloneEffect(FilterEffect e) => new FilterEffect(Guid.NewGuid(), e.Settings, e.Enabled, e.Strength, e.Channels.Count == 0 ? null : e.Channels);
        /// <summary>A copy of a mask (tiles shared copy-on-write, parameters, filters with new IDs) owned by another layer.</summary>
        RasterMask CloneMask(RasterMask source, PaintLayer owner)
        {
            var mask = NewMask(owner);
            foreach (var coord in source.Surface.EnumerateTileCoordinates()) mask.Surface.Restore(coord, source.Surface.Capture(coord));
            mask.Enabled = source.Enabled; mask.Inverted = source.Inverted; mask.Density = source.Density;
            foreach (var e in source.FilterList) mask.FilterList.Add(CloneEffect(e));
            if (source.FilterList.Count > 0) mask.FilterRevision = ++filterRevisionCounter;
            return mask;
        }
        /// <summary>An empty mask wired to this document like <see cref="AddLayerMask"/>'s (not attached to the owner yet).</summary>
        RasterMask NewMask(PaintLayer owner)
        {
            var surface = new SparseTileSurface(Width, Height, TileSize);
            surface.BeforeExternalMutation = BeforeExternalMutation;
            surface.AfterExternalMutation = AfterExternalMutation;
            surface.BeforeSourceGrowth = EnsureSourceGrowth;
            surface.TileChanged = coord => MarkMaskTileChanged(owner, coord);
            return new RasterMask(surface) { Owner = owner };
        }

        /// <summary>A history command that switches between two layer lists (order and nesting), for structural edits that add or remove
        /// layers: the source budget is checked before each switch (nothing changes when it does not fit), and every layer in either list
        /// is marked changed before and after.</summary>
        DelegateCommand SwapStructure(Structure before, Structure after, long cost)
        {
            var all = new List<PaintLayer>(before.Order); var seen = new HashSet<PaintLayer>(before.Order);
            foreach (var l in after.Order) if (seen.Add(l)) all.Add(l);
            void Switch(Structure to)
            {
                long next = 0; foreach (var l in to.Order) next += l.AllocatedBytes;
                EnsureSourceGrowth(next - AllocatedBytes);
                foreach (var l in all) MarkLayerChanged(l, null);
                RestoreStructure(to);
                foreach (var l in all) MarkLayerChanged(l, null);
                MarkClippedLayersChanged();
            }
            return new DelegateCommand(() => Switch(after), () => Switch(before), cost);
        }

        // ───────────── clipboard ─────────────

        /// <summary>Copies the pixels of a layer's channel (or of its mask) inside the selection — everywhere without one — into a
        /// <see cref="PixelClipboard"/>. Changes nothing. Where the selection is fully on, pixels are copied exactly (the RGB of
        /// transparent pixels included); a partial selection fades the pixel towards transparent in premultiplied space (the rule of
        /// <see cref="ReplacePixels"/>). The layer's own pixels are copied: its filters, generators, mask and opacity are not applied
        /// (use <see cref="CopyMerged"/> for what shows). A fill layer copies its value. The rectangle is trimmed to the pixels that hold
        /// any value; refused when there are none, or when it is larger than maxBytes.</summary>
        public PixelClipboard CopyPixels(Guid layerId, PaintChannel channel, bool fromMask = false, long maxBytes = long.MaxValue)
        {
            EnsureNoStroke(); PaintLayer.ValidateChannel(channel); var layer = GetLayer(layerId);
            if (fromMask)
            {
                var mask = layer.Mask ?? throw new InvalidOperationException("The layer has no mask.");
                return CopyRegion(ClipboardSource.Mask, PaintChannel.Color, maxBytes, coords => ReadTiles(coords, (coord, tile) =>
                {
                    mask.Surface.CopyTile(coord, tile);
                    for (int i = 0; i < tile.Length; i += 4) { byte grey = (byte)(255 - tile[i + 3]); tile[i] = grey; tile[i + 1] = grey; tile[i + 2] = grey; tile[i + 3] = 255; }
                }));
            }
            if (layer.Kind == LayerKind.Group || layer.Kind == LayerKind.Adjustment)
                throw new LayerOpException(LayerOpRefusal.NoPixels, layer.Kind == LayerKind.Group ? "A group has no pixels to copy. Select a layer inside it, or use Copy Merged." : "An adjustment layer has no pixels to copy.");
            return CopyRegion(ClipboardSource.Layer, channel, maxBytes, coords => ReadTiles(coords, (coord, tile) => layer.CopyTile(channel, coord, tile)));
        }

        /// <summary>Copies the composite of a channel (every visible layer, as the document shows it; Normal as the painted vector
        /// composite, not the Unity output) inside the selection, like <see cref="CopyPixels"/>.</summary>
        public PixelClipboard CopyMerged(PaintChannel channel, long maxBytes = long.MaxValue)
        {
            EnsureNoStroke(); PaintLayer.ValidateChannel(channel);
            return CopyRegion(ClipboardSource.Composite, channel, maxBytes, coords => CompositeTiles(channel, coords));
        }

        /// <summary>Copies like <see cref="CopyPixels"/> and then clears what was copied, as one undo step: a paint layer's channel loses
        /// alpha by the selection amount (fully selected pixels become transparent black), a mask reveals. Paths, fill, adjustment and group
        /// layers are refused (nothing is copied either), and so are a paint layer whose image or transparent pixels are locked and a mask under Lock All.</summary>
        public PixelClipboard CutPixels(Guid layerId, PaintChannel channel, bool fromMask = false, long maxBytes = long.MaxValue)
        {
            EnsureNoStroke(); PaintLayer.ValidateChannel(channel); var layer = GetLayer(layerId);
            if (!fromMask)
            {
                if (layer.Kind != LayerKind.Raster)
                    throw new LayerOpException(layer.Kind == LayerKind.Fill ? LayerOpRefusal.NotPaintLayer : LayerOpRefusal.NoPixels,
                        layer.Kind == LayerKind.Fill ? "A fill layer is generated from its value and cannot be cut. Copy it, or paint on its mask." : "This layer has no pixels to cut.");
                if (layer.Path != null) throw new LayerOpException(LayerOpRefusal.PathLayer, "This layer is drawn by a path. Edit the path, or rasterize the layer to cut from it.");
            }
            if (!fromMask && !layer.IsChannelEnabled(channel)) throw new InvalidOperationException("Enable the channel before cutting from it.");
            if (fromMask) RefuseLockedAttributes(layer); else RefuseLockedPixels(layer, erase: true); // 写す前に断る（クリップボードも変えない）
            var copied = CopyPixels(layerId, channel, fromMask, maxBytes);
            if (fromMask) FillMask(layerId, 1, null, reveal: true);
            else Fill(layerId, channel, new Rgba32(0, 0, 0, 255), 1, null, erase: true);
            return copied;
        }

        byte[][] ReadTiles(List<TileCoord> coords, Action<TileCoord, byte[]> read)
        {
            var tiles = new byte[coords.Count][];
            for (int k = 0; k < coords.Count; k++) read(coords[k], tiles[k] = new byte[TileSize * TileSize * 4]);
            return tiles;
        }

        /// <summary>Reads source tiles over the selection (or the canvas) a chunk at a time, applies the selection amount and trims to the
        /// pixels that hold any value. Each tile is read once; only tiles with a value are kept until the rectangle is copied out. Refused as
        /// soon as the rectangle grows past maxBytes.</summary>
        PixelClipboard CopyRegion(ClipboardSource source, PaintChannel channel, long maxBytes, Func<List<TileCoord>, byte[][]> readTiles)
        {
            if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            int tile = TileSize, n = tile * tile; var sel = selection;
            var amounts = new byte[n];
            var kept = new List<(TileCoord coord, byte[] pixels)>();
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            foreach (var chunk in Chunks(sel != null ? sel.Tiles : EnumerateCanvasTiles()))
            {
                var read = readTiles(chunk);
                for (int index = 0; index < chunk.Count; index++)
                {
                    var coord = chunk[index]; var bytes = read[index];
                    if (sel != null) { if (!sel.CopyTile(coord, amounts)) continue; } else for (int i = 0; i < n; i++) amounts[i] = 255;
                    int w = Math.Min(tile, Width - coord.X * tile), h = Math.Min(tile, Height - coord.Y * tile); bool any = false;
                    for (int y = 0; y < tile; y++) for (int x = 0; x < tile; x++)
                    {
                        int i = y * tile + x, o = i * 4; byte a = x < w && y < h ? amounts[i] : (byte)0;
                        if (a == 0) { bytes[o] = 0; bytes[o + 1] = 0; bytes[o + 2] = 0; bytes[o + 3] = 0; continue; }
                        if (a < 255)
                        {
                            var p = Interpolate(Rgba32.Transparent, new Rgba32(bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]), a / 255.0);
                            bytes[o] = p.R; bytes[o + 1] = p.G; bytes[o + 2] = p.B; bytes[o + 3] = p.A;
                        }
                        if ((bytes[o] | bytes[o + 1] | bytes[o + 2] | bytes[o + 3]) == 0) continue;
                        any = true; int px = coord.X * tile + x, py = coord.Y * tile + y;
                        if (px < x0) x0 = px; if (py < y0) y0 = py; if (px > x1) x1 = px; if (py > y1) y1 = py;
                    }
                    if (!any) continue;
                    kept.Add((coord, bytes));
                    long sofar = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * 4;
                    if (sofar > maxBytes) throw ClipboardTooLarge(sofar, maxBytes);
                }
            }
            if (x1 < 0) throw new LayerOpException(LayerOpRefusal.NothingToCopy, sel != null ? "There is nothing to copy inside the selection." : "There is nothing to copy: the layer is empty.");
            int width = x1 - x0 + 1, height = y1 - y0 + 1;
            var rgba = new byte[(long)width * height * 4];
            foreach (var (coord, pixels) in kept)
            {
                int bx = coord.X * tile, by = coord.Y * tile;
                int cx0 = Math.Max(bx, x0), cx1 = Math.Min(bx + tile - 1, x1), cy0 = Math.Max(by, y0), cy1 = Math.Min(by + tile - 1, y1);
                for (int y = cy0; y <= cy1; y++)
                    Buffer.BlockCopy(pixels, ((y - by) * tile + (cx0 - bx)) * 4, rgba, ((y - y0) * width + (cx0 - x0)) * 4, (cx1 - cx0 + 1) * 4);
            }
            return new PixelClipboard(Width, Height, x0, y0, width, height, rgba, source, channel);
        }
        static LayerOpException ClipboardTooLarge(long size, long limit)
            => new LayerOpException(LayerOpRefusal.ClipboardTooLarge, "The copy needs " + Mib(size) + " MiB, more than the clipboard limit (" + Mib(limit) + " MiB). Nothing was copied. Select a smaller area or raise the one-operation budget.", size, limit);

        /// <summary>Pastes clipboard pixels as a new paint layer above another layer (in its group; on top when null) in one channel, and
        /// deselects, as one undo step (like Photoshop's paste, after which the move tool moves the new layer). In a document of the size it
        /// was copied from the pixels keep their place; in a document of another size they are centred and what falls outside the canvas is
        /// cut off (<see cref="PasteResult.ClippedPixels"/>). Pixels are written exactly, the RGB of transparent pixels included. Refused when
        /// the new layer's pixels exceed <see cref="ActiveStrokeBudgetBytes"/> or the source budget.</summary>
        public PasteResult PasteAsLayer(PixelClipboard clip, PaintChannel channel, string name = null, Guid? above = null, Guid? id = null)
        {
            EnsureNoStroke(); if (clip == null) throw new ArgumentNullException(nameof(clip)); PaintLayer.ValidateChannel(channel);
            Guid layerId = NewLayerId(id);
            bool centered = clip.DocumentWidth != Width || clip.DocumentHeight != Height;
            int ox = centered ? (Width - clip.Width) / 2 : clip.X, oy = centered ? (Height - clip.Height) / 2 : clip.Y;
            int tile = TileSize, tileBytes = tile * tile * 4; var src = clip.Pixels; long clipped = 0;
            int x0 = Math.Max(0, ox), y0 = Math.Max(0, oy), x1 = Math.Min(Width, ox + clip.Width), y1 = Math.Min(Height, oy + clip.Height);
            if (x0 != ox || y0 != oy || x1 != ox + clip.Width || y1 != oy + clip.Height)
                for (int y = 0; y < clip.Height; y++)
                    for (int x = 0; x < clip.Width; x++)
                    {
                        int px = ox + x, py = oy + y; if (px >= x0 && px < x1 && py >= y0 && py < y1) continue;
                        int o = (y * clip.Width + x) * 4; if ((src[o] | src[o + 1] | src[o + 2] | src[o + 3]) != 0) clipped++;
                    }
            // 行ごとにタイルの幅の区間をまとめて写す（全部 0 のタイルは後で捨てる）
            var tiles = new Dictionary<TileCoord, byte[]>();
            for (int py = y0; py < y1; py++)
                for (int tx = x0 / tile; tx * tile < x1; tx++)
                {
                    int sx0 = Math.Max(x0, tx * tile), sx1 = Math.Min(x1, (tx + 1) * tile); var coord = new TileCoord(tx, py / tile);
                    if (!tiles.TryGetValue(coord, out var t)) tiles.Add(coord, t = new byte[tileBytes]);
                    Buffer.BlockCopy(src, ((py - oy) * clip.Width + sx0 - ox) * 4, t, ((py % tile) * tile + sx0 - tx * tile) * 4, (sx1 - sx0) * 4);
                }
            var storages = new List<(TileCoord coord, TileStorage storage)>(); long bytes = 0;
            foreach (var entry in tiles)
            {
                var storage = TileStorage.FromBytes(entry.Value); if (storage == null) continue;
                bytes += 64 + storage.ByteSize;
                if (bytes > ActiveStrokeBudgetBytes) throw new LayerOpException(LayerOpRefusal.OperationBudget, "The pasted layer would need more than the one-operation budget (" + Mib(ActiveStrokeBudgetBytes) + " MiB). Nothing was pasted.", bytes, ActiveStrokeBudgetBytes);
                storages.Add((entry.Key, storage));
            }
            var layer = new PaintLayer(this, name ?? "Layer", layerId);
            var surface = layer.GetChannel(channel);
            foreach (var (coord, storage) in storages) surface.Restore(coord, storage);
            Guid parent = Guid.Empty; int index = layers.Count;
            if (above.HasValue) { var reference = GetLayer(above.Value); index = layers.IndexOf(reference) + 1; parent = reference.ParentId; }
            layer.ParentId = parent;
            var before = SnapshotStructure(); layers.Insert(index, layer); var after = SnapshotStructure(); RestoreStructure(before);
            var commands = new List<IHistoryCommand> { SwapStructure(before, after, 128 + layer.AllocatedBytes) };
            commands[0].Apply();
            if (selection != null) { var old = selection; var drop = new DelegateCommand(() => selection = null, () => selection = old, 64); drop.Apply(); commands.Add(drop); }
            Revision++; Push(new CompositeCommand(commands));
            return new PasteResult(layer, ox, oy, centered, clipped);
        }
    }

}
