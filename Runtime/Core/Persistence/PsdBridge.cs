using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core.Psd;

namespace Yozolab.YoluPainter.Core.Persistence
{
    public static class PsdBridge
    {
        /// <summary>Export a native channel into the strictly supported raster subset. Does not flatten unsupported semantics.</summary>
        public static PsdDocument Export(PaintDocument source, PaintChannel channel)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.HasActiveStroke) throw new InvalidOperationException("Commit or cancel the active stroke before exporting PSD.");
            var result = new PsdDocument { Width = source.Width, Height = source.Height };
            var usedIds = new HashSet<int>(); long byteBudget = 0;
            for (int i = source.Layers.Count - 1; i >= 0; i--)
            {
                var layer = source.Layers[i];
                if (layer.BlendMode != LayerBlendMode.Normal) throw new InvalidOperationException("PSD projection currently supports Normal layers only. Native project can still be saved losslessly.");
                if (layer.Mask != null && !layer.Mask.IsNeutral) throw new InvalidOperationException("PSD projection does not write layer masks yet, and flattening the mask into the pixels would lose it. Native project can still be saved losslessly.");
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
                if (surface != null)
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                    {
                        var p = surface.GetPixel(left+x, top-1-y); int n=(y*width+x)*4;
                        pixels[n]=p.R; pixels[n+1]=p.G; pixels[n+2]=p.B; pixels[n+3]=p.A;
                    }
                byte[] guid = layer.Id.ToByteArray(); int id = BitConverter.ToInt32(guid,0) & 0x7fffffff; if (id == 0) id=1;
                while (!usedIds.Add(id)) id = id == int.MaxValue ? 1 : id+1;
                result.Layers.Add(new PsdRasterLayer { Id=id, Name=layer.Name, Left=left, Top=source.Height-top,
                    Width=width, Height=height, Opacity=(byte)Math.Round(layer.Opacity*255), Visible=layer.Visible && layer.IsChannelEnabled(channel), PixelsRgba=pixels });
            }
            return result;
        }
        public static PaintDocument Import(PsdReadResult result)
        {
            if (result == null || result.Mode != PsdCompatibilityMode.EditableRaster || result.Document == null)
                throw new InvalidOperationException("PSD is not in the verified structural editable subset. Original source must remain protected.");
            var source = result.Document;
            if (source.Width > 4096 || source.Height > 4096) throw new InvalidOperationException("Native prototype is limited to 4096 dimensions.");
            // Native v1 has no off-canvas tile coordinates; do not crop imported pixels silently.
            foreach (var layer in source.Layers)
                if (layer.Left < 0 || layer.Top < 0 || (long)layer.Left+layer.Width > source.Width || (long)layer.Top+layer.Height > source.Height)
                    throw new InvalidOperationException("PSD contains off-canvas layer pixels. Native import would crop them, so import is blocked.");
            var doc = new PaintDocument(source.Width,source.Height);
            for (int i=source.Layers.Count-1;i>=0;i--)
            {
                var original=source.Layers[i]; var layer=doc.AddLayer(original.Name);
                var surface=layer.GetChannel(PaintChannel.Color);
                for (int y=0;y<original.Height;y++) for (int x=0;x<original.Width;x++)
                {
                    int n=(y*original.Width+x)*4;
                    surface.SetPixel(original.Left+x,source.Height-1-(original.Top+y), new Rgba32(original.PixelsRgba[n],original.PixelsRgba[n+1],original.PixelsRgba[n+2],original.PixelsRgba[n+3]));
                }
                doc.SetLayerVisibility(layer.Id,original.Visible); doc.SetLayerOpacity(layer.Id,original.Opacity/255.0);
            }
            doc.ClearHistory(); return doc;
        }
    }
}
