using System;
using System.IO;
using System.Text;
using System.Linq;

namespace Yozolab.YoluPainter.Core.Persistence
{
    /// <summary>Versioned, bounded, lossless native sparse source archive. No GPU cache is persisted.</summary>
    public static class DocumentBinary
    {
        const int Version = 1;
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
                    if (reader.ReadInt32() != Version) throw new InvalidDataException("Unsupported archive version; source retained unchanged.");
                    var id = new Guid(ReadExact(reader, 16));
                    int width = reader.ReadInt32(), height = reader.ReadInt32(), tileSize = reader.ReadInt32();
                    if (width < 1 || height < 1 || width > 4096 || height > 4096 || tileSize < 8 || tileSize > 512 || (tileSize & (tileSize - 1)) != 0)
                        throw new InvalidDataException("Unsupported native canvas dimensions or tile size.");
                    var doc = new PaintDocument(width, height, tileSize, 64L * 1024 * 1024, id);
                    int layers = ReadCount(reader, 2048, "layers");
                    for (int l = 0; l < layers; l++)
                    {
                        var layerId = new Guid(ReadExact(reader, 16));
                        var layer = doc.AddLayer(ReadString(reader), layerId);
                        bool visible = reader.ReadBoolean(); double opacity = reader.ReadDouble(); int blend = reader.ReadInt32();
                        if (double.IsNaN(opacity) || double.IsInfinity(opacity) || opacity < 0 || opacity > 1 || !Enum.IsDefined(typeof(LayerBlendMode), blend))
                            throw new InvalidDataException("Invalid layer attributes.");
                        doc.SetLayerVisibility(layer.Id, visible); doc.SetLayerOpacity(layer.Id, opacity); doc.SetLayerBlendMode(layer.Id, (LayerBlendMode)blend);
                        int channelCount = ReadCount(reader, 6, "channels");
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
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException("Trailing native data requires a newer reader.");
                    doc.ClearHistory(); return doc;
                }
                catch (EndOfStreamException ex) { throw new InvalidDataException("Native archive is truncated.", ex); }
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
