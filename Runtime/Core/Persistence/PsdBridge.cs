using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core.Psd;

namespace Yozolab.YoluPainter.Core.Persistence
{
    /// <summary>Native document ⇔ PSD DTO. Raster layers, groups (nested to the codec's depth budget), solid colour fill layers
    /// (opaque values, as SoCo) and Invert / Levels / Hue/Saturation adjustment layers with any of the 26 PSD blend modes (pass-through or isolated for groups), clipping, a
    /// raster mask (enabled, disabled, density), visibility and opacity map both ways. Anything without an exact PSD form here
    /// (translucent fill values, adjustment settings between PSD's steps, inverted masks, clipped groups, layers or masks with filters) is
    /// refused instead of being flattened into pixels.</summary>
    public static class PsdBridge
    {
        /// <summary>Export a native channel. The merged image is the CPU composite of that channel. Opacity and mask density are
        /// rounded to the nearest 1/255 (PSD stores bytes). For the Normal channel the merged image is the evaluated Normal
        /// output (<see cref="NormalMaps.FileOutput"/>: vector composite flattened onto flat, opaque, with Height → Normal when
        /// it is on), which Photoshop does not reproduce when it recomposites the layers; the derived normal is not written as
        /// a layer (it is regenerated from Height, not painted pixels). With a DirectX file direction the green byte of the
        /// Normal layers' pixels is inverted too, so the layers and the merged image share one convention.</summary>
        public static PsdDocument Export(PaintDocument source, PaintChannel channel)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.HasActiveStroke) throw new InvalidOperationException("Commit or cancel the active stroke before exporting PSD.");
            var result = new PsdDocument { Width = source.Width, Height = source.Height };
            var usedIds = new HashSet<int>(); long byteBudget = 0;
            result.Layers = ExportLevel(source, channel, Guid.Empty, usedIds, ref byteBudget);
            result.CompositeRgba = FlipRows(channel == PaintChannel.Normal ? NormalMaps.FileOutput(source) : source.Composite(channel), source.Width, source.Height);
            return result;
        }

        /// <summary>The children of a group (Guid.Empty: the top level) as DTO layers, top to bottom.</summary>
        static List<PsdRasterLayer> ExportLevel(PaintDocument source, PaintChannel channel, Guid parent, HashSet<int> usedIds, ref long byteBudget)
        {
            var children = source.ChildrenOf(parent);
            var list = new List<PsdRasterLayer>();
            for (int i = children.Count - 1; i >= 0; i--) list.Add(ExportLayer(source, channel, children[i], usedIds, ref byteBudget));
            return list;
        }

        static int UniqueId(byte[] guid, int offset, HashSet<int> usedIds)
        {
            int id = BitConverter.ToInt32(guid, offset) & 0x7fffffff; if (id == 0) id = 1;
            while (!usedIds.Add(id)) id = id == int.MaxValue ? 1 : id + 1;
            return id;
        }

        static bool HasGenerator(PaintLayer layer)
        {
            foreach (var e in layer.Filters) if (e.Settings.IsGenerator) return true;
            if (layer.Mask != null) foreach (var e in layer.Mask.Filters) if (e.Settings.IsGenerator) return true;
            return false;
        }

        static PsdRasterLayer ExportLayer(PaintDocument source, PaintChannel channel, PaintLayer layer, HashSet<int> usedIds, ref long byteBudget)
        {
            if (layer.Kind == LayerKind.Adjustment)
            {
                string refusal = PsdCodec.AdjustmentRefusal(layer.Adjustment);
                if (refusal != null) throw new InvalidOperationException("Adjustment layer '" + layer.Name + "': " + refusal + " Native project can still be saved losslessly.");
            }
            if (layer.IsGroup && layer.Clipping) throw new InvalidOperationException("Group '" + layer.Name + "' is clipped. Photoshop's handling of a clipped folder is not verified (psd-tools treats it as unsupported in Photoshop), so it is not exported; turn its clipping off or export from the native project. Native project can still be saved losslessly.");
            if (layer.Filters.Count > 0 || layer.Mask != null && layer.Mask.Filters.Count > 0)
                throw new InvalidOperationException("Layer '" + layer.Name + "' has non-destructive filters" + (HasGenerator(layer) ? " or generators" : "") + ". PSD has no exact form for them here (Photoshop keeps smart filters inside smart objects, which this exporter does not write, and has no mesh-map generators), and writing only the filtered pixels would drop the filter stack silently. Bake the filters into the layer (or remove them) before exporting PSD. Native project can still be saved losslessly.");
            if (layer.Kind == LayerKind.Fill && layer.FillValues.TryGetValue(channel, out var fillValue) && fillValue.A != 255)
                throw new InvalidOperationException("Fill layer '" + layer.Name + "': a PSD solid colour fill is opaque, and this fill's " + channel + " value has alpha " + fillValue.A + ". Use the layer opacity instead. Native project can still be saved losslessly.");
            if (layer.Kind != LayerKind.Raster && layer.Kind != LayerKind.Group && layer.Kind != LayerKind.Adjustment && layer.Kind != LayerKind.Fill) throw new InvalidOperationException("PSD projection does not write " + layer.Kind + " layers yet. Native project can still be saved losslessly.");
            if (layer.Mask != null && layer.Mask.Inverted) throw new InvalidOperationException("PSD has no non-destructive mask inversion; turn Inverted off (or invert the mask pixels) before exporting. Native project can still be saved losslessly.");
            byte[] guid = layer.Id.ToByteArray();
            if (layer.Mask != null)
            {
                byteBudget = checked(byteBudget + (long)source.Width * source.Height);
                if (byteBudget > 128L * 1024 * 1024) throw new InvalidOperationException("PSD projection exceeds the prototype's 128 MiB decoded-layer budget. Save the native project instead.");
            }
            var mask = layer.Mask == null ? null : ExportMask(layer.Mask, source.Width, source.Height);
            var opacity = (byte)Math.Round(layer.Opacity * 255);
            if (layer.Kind == LayerKind.Adjustment)
            {
                if (PsdCodec.BlendKey(layer.BlendMode) == null) throw new InvalidOperationException("Blend mode " + layer.BlendMode + " has no PSD equivalent for an adjustment layer. Native project can still be saved losslessly.");
                // An adjustment that does not apply to this channel (or is switched off in it) exports hidden, as raster layers do.
                return new PsdRasterLayer { Id = UniqueId(guid, 0, usedIds), Name = layer.Name, Opacity = opacity,
                    Visible = layer.Visible && layer.IsChannelEnabled(channel) && layer.Adjustment.AppliesTo(channel),
                    BlendMode = layer.BlendMode, Clipping = layer.Clipping, Mask = mask, Adjustment = layer.Adjustment, PixelsRgba = new byte[0] };
            }
            if (layer.Kind == LayerKind.Fill)
            {
                if (PsdCodec.BlendKey(layer.BlendMode) == null) throw new InvalidOperationException("Blend mode " + layer.BlendMode + " has no PSD equivalent for a fill layer. Native project can still be saved losslessly.");
                // 塗りつぶしは単色の SoCo として書く（画素に焼かない）。このチャンネルに値が無ければ非表示で書く
                bool covers = layer.FillValues.TryGetValue(channel, out var value) && layer.IsChannelEnabled(channel);
                return new PsdRasterLayer { Id = UniqueId(guid, 0, usedIds), Name = layer.Name, Opacity = opacity, Visible = layer.Visible && covers,
                    BlendMode = layer.BlendMode, Clipping = layer.Clipping, Mask = mask, FillColor = covers ? value : new Rgba32(0, 0, 0, 255), PixelsRgba = new byte[0] };
            }
            if (layer.IsGroup)
            {
                if (layer.BlendMode != LayerBlendMode.PassThrough && PsdCodec.BlendKey(layer.BlendMode) == null)
                    throw new InvalidOperationException("Blend mode " + layer.BlendMode + " has no PSD folder equivalent. Native project can still be saved losslessly.");
                var group = new PsdRasterLayer { Id = UniqueId(guid, 0, usedIds), Name = layer.Name, Opacity = opacity, Visible = layer.Visible,
                    BlendMode = layer.BlendMode, Clipping = layer.Clipping, Mask = mask, PixelsRgba = new byte[0] };
                group.DividerId = UniqueId(guid, 4, usedIds);
                group.Children = ExportLevel(source, channel, layer.Id, usedIds, ref byteBudget);
                return group;
            }
            if (PsdCodec.BlendKey(layer.BlendMode) == null) throw new InvalidOperationException("Blend mode " + layer.BlendMode + " has no PSD equivalent for a raster layer. Native project can still be saved losslessly.");
            int left = source.Width, bottom = source.Height, right = 0, top = 0;
            SparseTileSurface surface;
            if (layer.TryGetChannel(channel, out surface))
                foreach (var tile in surface.EnumerateTiles())
                {
                    left = Math.Min(left, tile.Coord.X * source.TileSize); bottom = Math.Min(bottom, tile.Coord.Y * source.TileSize);
                    right = Math.Min(source.Width, Math.Max(right, (tile.Coord.X + 1) * source.TileSize));
                    top = Math.Min(source.Height, Math.Max(top, (tile.Coord.Y + 1) * source.TileSize));
                }
            if (right <= left || top <= bottom) { left = bottom = 0; right = top = 1; }
            int width = right-left, height = top-bottom;
            byteBudget = checked(byteBudget + (long)width*height*4);
            if (byteBudget > 128L * 1024 * 1024) throw new InvalidOperationException("PSD projection exceeds the prototype's 128 MiB decoded-layer budget. Save the native project instead.");
            var pixels = new byte[checked(width*height*4)];
            bool flipGreen = channel == PaintChannel.Normal && source.NormalSettings.FileDirection == NormalYDirection.DirectX;
            if (surface != null)
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    var p = surface.GetPixel(left+x, top-1-y); int n=(y*width+x)*4;
                    pixels[n]=p.R; pixels[n+1]=flipGreen?(byte)(255-p.G):p.G; pixels[n+2]=p.B; pixels[n+3]=p.A;
                }
            return new PsdRasterLayer { Id=UniqueId(guid, 0, usedIds), Name=layer.Name, Left=left, Top=source.Height-top,
                Width=width, Height=height, Opacity=opacity, Visible=layer.Visible && layer.IsChannelEnabled(channel),
                BlendMode=layer.BlendMode, Clipping=layer.Clipping, Mask=mask, PixelsRgba=pixels };
        }

        /// <summary>Native mask (hide amount in alpha, bottom-left origin) → PSD mask (255 shows, top-down). The rectangle is the
        /// bounding box of the samples that differ from the default colour; the default colour is whichever of 255 / 0 gives
        /// the smaller box, so the cropped mask is identical everywhere on the canvas.</summary>
        static PsdLayerMask ExportMask(RasterMask mask, int width, int height)
        {
            var values = new byte[checked(width * height)];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                values[y * width + x] = (byte)(255 - mask.Surface.GetPixel(x, height - 1 - y).A);
            int[] white = Bounds(values, width, height, 255), black = Bounds(values, width, height, 0);
            long whiteArea = (long)white[2] * white[3], blackArea = (long)black[2] * black[3];
            byte background = blackArea < whiteArea ? (byte)0 : (byte)255;
            int[] box = background == 0 ? black : white;
            // A uniform mask still gets a 1x1 rectangle: some readers mishandle empty mask rectangles.
            if (box[2] == 0 || box[3] == 0) box = new[] { 0, 0, 1, 1 };
            var pixels = new byte[box[2] * box[3]];
            for (int y = 0; y < box[3]; y++) Buffer.BlockCopy(values, (box[1] + y) * width + box[0], pixels, y * box[2], box[2]);
            return new PsdLayerMask { Left = box[0], Top = box[1], Width = box[2], Height = box[3], DefaultColor = background,
                Enabled = mask.Enabled, Density = (byte)Math.Round(mask.Density * 255), Pixels = pixels };
        }

        /// <summary>Bounding box {left, top, width, height} of the samples that are not <paramref name="background"/>.</summary>
        static int[] Bounds(byte[] values, int width, int height, byte background)
        {
            int left = width, top = height, right = 0, bottom = 0;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                if (values[y * width + x] != background)
                { left = Math.Min(left, x); right = Math.Max(right, x + 1); top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1); }
            return right <= left ? new[] { 0, 0, 0, 0 } : new[] { left, top, right - left, bottom - top };
        }

        static byte[] FlipRows(byte[] rgba, int width, int height)
        {
            var flipped = new byte[rgba.Length]; int row = width * 4;
            for (int y = 0; y < height; y++) Buffer.BlockCopy(rgba, y * row, flipped, (height - 1 - y) * row, row);
            return flipped;
        }

        public static PaintDocument Import(PsdReadResult result)
        {
            if (result == null || result.Mode != PsdCompatibilityMode.EditableRaster || result.Document == null)
                throw new InvalidOperationException("PSD is not in the verified structural editable subset. Original source must remain protected.");
            var source = result.Document;
            if (source.Width > 4096 || source.Height > 4096) throw new InvalidOperationException("Native prototype is limited to 4096 dimensions.");
            // Native order: bottom to top, a group's contents directly below the group (the PSD record order without dividers).
            var order = new List<KeyValuePair<PsdRasterLayer, PsdRasterLayer>>();
            Order(source.Layers, null, order);
            foreach (var entry in order)
            {
                var layer = entry.Key;
                // Native v1 has no off-canvas tile coordinates; do not crop imported pixels silently.
                if (!layer.IsGroup && !layer.IsAdjustment && !layer.IsFill && (layer.Left < 0 || layer.Top < 0 || (long)layer.Left+layer.Width > source.Width || (long)layer.Top+layer.Height > source.Height))
                    throw new InvalidOperationException("PSD contains off-canvas layer pixels. Native import would crop them, so import is blocked.");
                if (layer.Mask != null && MaskDiffersOffCanvas(layer.Mask, source.Width, source.Height))
                    throw new InvalidOperationException("PSD layer mask '" + layer.Name + "' has samples outside the canvas that differ from its default colour. Native import would crop them, so import is blocked.");
            }
            var doc = new PaintDocument(source.Width,source.Height);
            var created = new Dictionary<PsdRasterLayer, PaintLayer>();
            byte[] salt = Guid.NewGuid().ToByteArray();
            foreach (var entry in order)
            {
                var original = entry.Key;
                PaintLayer layer;
                if (original.IsGroup) layer = doc.AddGroup(original.Name, IdFor(original.Id, original.DividerId, salt));
                else if (original.IsAdjustment) layer = doc.AddAdjustmentLayer(original.Name, original.Adjustment, null, IdFor(original.Id, 0, salt));
                else if (original.IsFill) layer = doc.AddFillLayer(original.Name, new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, original.FillColor.Value } }, IdFor(original.Id, 0, salt));
                else
                {
                    layer = doc.AddLayer(original.Name, IdFor(original.Id, 0, salt));
                    var surface = layer.GetChannel(PaintChannel.Color);
                    for (int y=0;y<original.Height;y++) for (int x=0;x<original.Width;x++)
                    {
                        int n=(y*original.Width+x)*4;
                        surface.SetPixel(original.Left+x,source.Height-1-(original.Top+y), new Rgba32(original.PixelsRgba[n],original.PixelsRgba[n+1],original.PixelsRgba[n+2],original.PixelsRgba[n+3]));
                    }
                }
                created.Add(original, layer);
                doc.SetLayerVisibility(layer.Id,original.Visible); doc.SetLayerOpacity(layer.Id,original.Opacity/255.0);
                doc.SetLayerBlendMode(layer.Id, original.BlendMode);
                doc.SetLayerClipping(layer.Id, original.Clipping);
                if (original.Mask != null) ImportMask(doc, layer.Id, original.Mask);
            }
            foreach (var entry in order)
                if (entry.Value != null) doc.SetParentForLoad(created[entry.Key], created[entry.Value].Id);
            doc.ValidateStructure();
            doc.ClearHistory(); return doc;
        }

        /// <summary>Native layer ID that carries the PSD layer ID (bytes 0-3) and a folder's divider ID (bytes 4-7), so that
        /// <see cref="Export"/> writes the same IDs again. The rest is random per import.</summary>
        static Guid IdFor(int psdId, int dividerId, byte[] salt)
        {
            var bytes = (byte[])salt.Clone();
            Buffer.BlockCopy(BitConverter.GetBytes(psdId), 0, bytes, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(dividerId), 0, bytes, 4, 4);
            return new Guid(bytes);
        }

        /// <summary>Appends a level (top to bottom) in native order with each layer's parent group (null at the top level).</summary>
        static void Order(List<PsdRasterLayer> topDown, PsdRasterLayer parent, List<KeyValuePair<PsdRasterLayer, PsdRasterLayer>> order)
        {
            for (int i = topDown.Count - 1; i >= 0; i--)
            {
                var layer = topDown[i];
                if (layer.IsGroup) Order(layer.Children, layer, order);
                order.Add(new KeyValuePair<PsdRasterLayer, PsdRasterLayer>(layer, parent));
            }
        }

        static bool MaskDiffersOffCanvas(PsdLayerMask mask, int width, int height)
        {
            for (int y = 0; y < mask.Height; y++) for (int x = 0; x < mask.Width; x++)
            {
                long cx = (long)mask.Left + x, cy = (long)mask.Top + y;
                if ((cx < 0 || cy < 0 || cx >= width || cy >= height) && mask.Pixels[y * mask.Width + x] != mask.DefaultColor) return true;
            }
            return false;
        }

        /// <summary>The native mask stores the amount to hide (255 - PSD value); absent tiles hide nothing, so only hidden
        /// samples are written.</summary>
        static void ImportMask(PaintDocument doc, Guid layerId, PsdLayerMask source)
        {
            var mask = doc.AddLayerMask(layerId);
            int width = doc.Width, height = doc.Height;
            bool hiddenOutside = source.DefaultColor != 255;
            int x0 = hiddenOutside ? 0 : Math.Max(0, source.Left), y0 = hiddenOutside ? 0 : Math.Max(0, source.Top);
            int x1 = hiddenOutside ? width : (int)Math.Min(width, (long)source.Left + source.Width);
            int y1 = hiddenOutside ? height : (int)Math.Min(height, (long)source.Top + source.Height);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
            {
                int hide = 255 - source.ValueAt(x, y);
                if (hide != 0) mask.Surface.SetPixel(x, height - 1 - y, new Rgba32(0, 0, 0, (byte)hide));
            }
            doc.SetLayerMaskEnabled(layerId, source.Enabled);
            doc.SetLayerMaskDensity(layerId, source.Density / 255.0);
        }
    }
}
