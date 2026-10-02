using System;
using System.IO;
using System.Text;
using System.Linq;

namespace Yozolab.YoluPainter.Core.Persistence
{
    /// <summary>Versioned, bounded, lossless native sparse source archive. No GPU cache is persisted.
    /// Version 2 adds an optional raster mask block after each layer's channels. Version 3 adds the layer kind and fill
    /// values after the layer attributes. Older archives still load.</summary>
    public static class DocumentBinary
    {
        const int Version = 3;
        const long MaxArchiveBytes = 512L * 1024 * 1024;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("DOTPAINT");

        public static byte[] Write(PaintDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (document.HasActiveStroke) throw new InvalidOperationException("Commit or cancel the active stroke before taking a save snapshot.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic); writer.Write(Version);
                writer.Write(document.Id.ToByteArray());
                writer.Write(document.Width); writer.Write(document.Height); writer.Write(document.TileSize);
                writer.Write(document.Layers.Count);
                foreach (var layer in document.Layers)
                {
                    writer.Write(layer.Id.ToByteArray()); WriteString(writer, layer.Name);
                    writer.Write(layer.Visible); writer.Write(layer.Opacity); writer.Write((int)layer.BlendMode);
                    writer.Write((int)layer.Kind);
                    var fills = layer.FillValues.Keys.OrderBy(c => c).ToArray();
                    writer.Write(fills.Length);
                    foreach (var channel in fills)
                    {
                        var value = layer.FillValues[channel];
                        writer.Write((int)channel); writer.Write(layer.IsChannelEnabled(channel));
                        writer.Write(value.R); writer.Write(value.G); writer.Write(value.B); writer.Write(value.A);
                    }
                    var channels = layer.Channels.Keys.OrderBy(c => c).ToArray();
                    writer.Write(channels.Length);
                    foreach (var channel in channels)
                    {
                        writer.Write((int)channel); writer.Write(layer.IsChannelEnabled(channel));
                        long countPosition = stream.Position; writer.Write(0); int count = 0;
                        foreach (var tile in layer.GetChannel(channel).EnumerateTiles())
                        {
                            writer.Write(tile.Coord.X); writer.Write(tile.Coord.Y);
                            writer.Write(tile.Bytes.Length); writer.Write(tile.Bytes); count++;
                            if (stream.Length > MaxArchiveBytes) throw new InvalidOperationException("Native archive exceeds the prototype's 512 MiB safety budget.");
                        }
                        long end = stream.Position; stream.Position = countPosition; writer.Write(count); stream.Position = end;
                    }
                    writer.Write(layer.Mask != null);
                    if (layer.Mask != null)
                    {
                        writer.Write(layer.Mask.Enabled); writer.Write(layer.Mask.Inverted); writer.Write(layer.Mask.Density);
                        WriteTiles(writer, stream, layer.Mask.Surface);
                    }
                }
                writer.Flush(); return stream.ToArray();
            }
        }

        public static PaintDocument Read(byte[] bytes)
        {
            if (bytes == null || bytes.LongLength > MaxArchiveBytes) throw new InvalidDataException("Archive is missing or exceeds its safety budget.");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                try
                {
                    for (int i = 0; i < Magic.Length; i++) if (reader.ReadByte() != Magic[i]) throw new InvalidDataException("Not a dot paint archive.");
                    int version = reader.ReadInt32();
                    if (version < 1 || version > Version) throw new InvalidDataException("Unsupported archive version; source retained unchanged.");
                    var id = new Guid(ReadExact(reader, 16));
                    int width = reader.ReadInt32(), height = reader.ReadInt32(), tileSize = reader.ReadInt32();
                    if (width < 1 || height < 1 || width > 4096 || height > 4096 || tileSize < 8 || tileSize > 512 || (tileSize & (tileSize - 1)) != 0)
                        throw new InvalidDataException("Unsupported native canvas dimensions or tile size.");
                    var doc = new PaintDocument(width, height, tileSize, 64L * 1024 * 1024, id);
                    int layers = ReadCount(reader, 2048, "layers");
                    for (int l = 0; l < layers; l++)
                    {
                        var layerId = new Guid(ReadExact(reader, 16));
                        string layerName = ReadString(reader);
                        bool visible = reader.ReadBoolean(); double opacity = reader.ReadDouble(); int blend = reader.ReadInt32();
                        if (double.IsNaN(opacity) || double.IsInfinity(opacity) || opacity < 0 || opacity > 1 || !Enum.IsDefined(typeof(LayerBlendMode), blend))
                            throw new InvalidDataException("Invalid layer attributes.");
                        int kind = version >= 3 ? reader.ReadInt32() : (int)LayerKind.Raster;
                        if (!Enum.IsDefined(typeof(LayerKind), kind)) throw new InvalidDataException("Unknown layer kind; a newer reader is required.");
                        PaintLayer layer;
                        if (version >= 3)
                        {
                            int fillCount = ReadCount(reader, 6, "fill values");
                            if (kind != (int)LayerKind.Fill && fillCount != 0) throw new InvalidDataException("Only fill layers have fill values.");
                            var values = new System.Collections.Generic.Dictionary<PaintChannel, Rgba32>(); var disabled = new System.Collections.Generic.List<PaintChannel>();
                            for (int f = 0; f < fillCount; f++)
                            {
                                int channelValue = reader.ReadInt32(); bool channelEnabled = reader.ReadBoolean();
                                if (!Enum.IsDefined(typeof(PaintChannel), channelValue) || values.ContainsKey((PaintChannel)channelValue)) throw new InvalidDataException("Invalid or duplicate fill channel.");
                                values.Add((PaintChannel)channelValue, new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()));
                                if (!channelEnabled) disabled.Add((PaintChannel)channelValue);
                            }
                            layer = kind == (int)LayerKind.Fill ? doc.AddFillLayer(layerName, values, layerId) : doc.AddLayer(layerName, layerId);
                            foreach (var channel in disabled) doc.SetChannelEnabled(layer.Id, channel, false);
                        }
                        else layer = doc.AddLayer(layerName, layerId);
                        doc.SetLayerVisibility(layer.Id, visible); doc.SetLayerOpacity(layer.Id, opacity); doc.SetLayerBlendMode(layer.Id, (LayerBlendMode)blend);
                        int channelCount = ReadCount(reader, 6, "channels");
                        if (layer.Kind == LayerKind.Fill && channelCount != 0) throw new InvalidDataException("Fill layers have no pixel channels.");
                        var seenChannels = new System.Collections.Generic.HashSet<int>();
                        for (int c = 0; c < channelCount; c++)
                        {
                            int channelValue = reader.ReadInt32(); bool channelEnabled = reader.ReadBoolean();
                            if (!Enum.IsDefined(typeof(PaintChannel), channelValue) || !seenChannels.Add(channelValue)) throw new InvalidDataException("Invalid or duplicate channel.");
                            layer.GetChannel((PaintChannel)channelValue);
                            doc.SetChannelEnabled(layer.Id, (PaintChannel)channelValue, channelEnabled);
                            int maxTiles = checked(((width + tileSize - 1) / tileSize) * ((height + tileSize - 1) / tileSize));
                            int tiles = ReadCount(reader, maxTiles, "tiles");
                            var seenTiles = new System.Collections.Generic.HashSet<TileCoord>();
                            for (int t = 0; t < tiles; t++)
                            {
                                int x = reader.ReadInt32(), y = reader.ReadInt32(), length = reader.ReadInt32();
                                var coord = new TileCoord(x, y);
                                if (x < 0 || y < 0 || x >= (width + tileSize - 1) / tileSize || y >= (height + tileSize - 1) / tileSize || !seenTiles.Add(coord) || length != checked(tileSize * tileSize * 4))
                                    throw new InvalidDataException("Invalid or duplicate tile.");
                                layer.GetChannel((PaintChannel)channelValue).ImportTile(coord, ReadExact(reader, length));
                            }
                        }
                        if (version >= 2 && reader.ReadBoolean())
                        {
                            bool maskEnabled = reader.ReadBoolean(), maskInverted = reader.ReadBoolean(); double density = reader.ReadDouble();
                            if (double.IsNaN(density) || double.IsInfinity(density) || density < 0 || density > 1) throw new InvalidDataException("Invalid mask density.");
                            var mask = doc.AddLayerMask(layer.Id);
                            doc.SetLayerMaskEnabled(layer.Id, maskEnabled); doc.SetLayerMaskInverted(layer.Id, maskInverted); doc.SetLayerMaskDensity(layer.Id, density);
                            ReadTiles(reader, mask.Surface, width, height, tileSize, maskOnly: true);
                        }
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException("Trailing native data requires a newer reader.");
                    doc.ClearHistory(); return doc;
                }
                catch (EndOfStreamException ex) { throw new InvalidDataException("Native archive is truncated.", ex); }
            }
        }
        static void WriteTiles(BinaryWriter writer, Stream stream, SparseTileSurface surface)
        {
            long countPosition = stream.Position; writer.Write(0); int count = 0;
            foreach (var tile in surface.EnumerateTiles())
            {
                writer.Write(tile.Coord.X); writer.Write(tile.Coord.Y);
                writer.Write(tile.Bytes.Length); writer.Write(tile.Bytes); count++;
                if (stream.Length > MaxArchiveBytes) throw new InvalidOperationException("Native archive exceeds the prototype's 512 MiB safety budget.");
            }
            long end = stream.Position; stream.Position = countPosition; writer.Write(count); stream.Position = end;
        }
        static void ReadTiles(BinaryReader reader, SparseTileSurface surface, int width, int height, int tileSize, bool maskOnly)
        {
            int columns = (width + tileSize - 1) / tileSize, rows = (height + tileSize - 1) / tileSize;
            int tiles = ReadCount(reader, checked(columns * rows), "tiles");
            var seen = new System.Collections.Generic.HashSet<TileCoord>();
            for (int t = 0; t < tiles; t++)
            {
                int x = reader.ReadInt32(), y = reader.ReadInt32(), length = reader.ReadInt32();
                var coord = new TileCoord(x, y);
                if (x < 0 || y < 0 || x >= columns || y >= rows || !seen.Add(coord) || length != checked(tileSize * tileSize * 4))
                    throw new InvalidDataException("Invalid or duplicate tile.");
                byte[] bytes = ReadExact(reader, length);
                // A mask stores only the hide amount in alpha. Colour bytes would be silently ignored, so refuse them.
                if (maskOnly) for (int i = 0; i < bytes.Length; i += 4) if ((bytes[i] | bytes[i + 1] | bytes[i + 2]) != 0) throw new InvalidDataException("Mask tiles must keep RGB at zero.");
                surface.ImportTile(coord, bytes);
            }
        }
        static int ReadCount(BinaryReader reader, int maximum, string label)
        {
            int value = reader.ReadInt32(); if (value < 0 || value > maximum) throw new InvalidDataException("Invalid " + label + " count."); return value;
        }
        static byte[] ReadExact(BinaryReader reader, int length)
        { var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return bytes; }
        static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? ""); if (bytes.Length > 4096) throw new InvalidDataException("Name too long."); writer.Write(bytes.Length); writer.Write(bytes);
        }
        static string ReadString(BinaryReader reader) => new UTF8Encoding(false, true).GetString(ReadExact(reader, ReadCount(reader, 4096, "name bytes")));
    }
}
