using System;
using System.IO;
using System.Text;
using System.Linq;

namespace Yozolab.YoluPainter.Core.Persistence
{
    /// <summary>Versioned, bounded, lossless native sparse source archive. No GPU cache is persisted.
    /// Version 2 adds an optional raster mask block after each layer's channels. Version 3 adds the layer kind and fill
    /// values after the layer attributes. Version 4 adds adjustment parameters after the fill values. Version 5 adds the
    /// clipping flag after the blend mode. Version 6 adds groups: layer kind 3, the PassThrough blend mode, and each layer's
    /// parent group id after the layer kind. Version 7 adds the document's Normal-output settings (algorithm version, Height →
    /// Normal on/off, strength, edges, file Y direction; 21 bytes) after the tile size; older archives read as
    /// <see cref="NormalSettings.Default"/>. Version 8 ends each layer with an editable surface path flag and, when set, the path
    /// (algorithm version, id, channel, model fingerprint, brush, points); the layer's pixels stay stored as before, so a
    /// document opens without its model. Version 9 ends each layer (after the path) with a filter flag and, when set, the layer's
    /// content filter stack and, when the layer has a mask, the mask's filter stack (per filter: id, type, algorithm version,
    /// enabled, strength, content channels, parameters); an unknown filter type or algorithm version refuses the archive instead
    /// of dropping the filter. Older archives still load.</summary>
    public static class DocumentBinary
    {
        const int Version = 9;
        /// <summary>The version <see cref="Write"/> produces.</summary>
        public const int CurrentVersion = Version;
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
                var normal = document.NormalSettings;
                writer.Write(NormalSettings.AlgorithmVersion); writer.Write(normal.DeriveFromHeight); writer.Write(normal.Strength);
                writer.Write((int)normal.Edges); writer.Write((int)normal.FileDirection);
                writer.Write(document.Layers.Count);
                foreach (var layer in document.Layers)
                {
                    writer.Write(layer.Id.ToByteArray()); WriteString(writer, layer.Name);
                    writer.Write(layer.Visible); writer.Write(layer.Opacity); writer.Write((int)layer.BlendMode);
                    writer.Write(layer.Clipping);
                    writer.Write((int)layer.Kind);
                    writer.Write(layer.ParentId.ToByteArray());
                    var fills = layer.FillValues.Keys.OrderBy(c => c).ToArray();
                    writer.Write(fills.Length);
                    foreach (var channel in fills)
                    {
                        var value = layer.FillValues[channel];
                        writer.Write((int)channel); writer.Write(layer.IsChannelEnabled(channel));
                        writer.Write(value.R); writer.Write(value.G); writer.Write(value.B); writer.Write(value.A);
                    }
                    if (layer.Kind == LayerKind.Adjustment)
                    {
                        var a = layer.Adjustment;
                        writer.Write((int)a.Type); writer.Write(AdjustmentSettings.AlgorithmVersion);
                        foreach (double v in new[] { a.InputBlack, a.InputWhite, a.Gamma, a.OutputBlack, a.OutputWhite, a.Hue, a.Saturation, a.Lightness }) writer.Write(v);
                        var enabled = layer.EnabledChannels; writer.Write(enabled.Count); foreach (var c in enabled) writer.Write((int)c);
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
                    writer.Write(layer.Path != null);
                    if (layer.Path != null) WritePath(writer, layer.Path);
                    bool filtered = layer.Filters.Count > 0 || layer.Mask != null && layer.Mask.Filters.Count > 0;
                    writer.Write(filtered);
                    if (filtered) { WriteFilters(writer, layer.Filters, true); if (layer.Mask != null) WriteFilters(writer, layer.Mask.Filters, false); }
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
                    if (version >= 7)
                    {
                        int algorithm = reader.ReadInt32();
                        if (algorithm != NormalSettings.AlgorithmVersion) throw new InvalidDataException("Normal algorithm version " + algorithm + " is not supported by this reader; source retained unchanged.");
                        bool derive = reader.ReadBoolean(); double strength = reader.ReadDouble(); int edges = reader.ReadInt32(), direction = reader.ReadInt32();
                        try { doc.SetNormalSettings(new NormalSettings(derive, strength, (HeightEdgeMode)edges, (NormalYDirection)direction)); }
                        catch (ArgumentException ex) { throw new InvalidDataException("Invalid Normal output settings.", ex); }
                    }
                    int layers = ReadCount(reader, 2048, "layers");
                    for (int l = 0; l < layers; l++)
                    {
                        var layerId = new Guid(ReadExact(reader, 16));
                        string layerName = ReadString(reader);
                        bool visible = reader.ReadBoolean(); double opacity = reader.ReadDouble(); int blend = reader.ReadInt32();
                        if (double.IsNaN(opacity) || double.IsInfinity(opacity) || opacity < 0 || opacity > 1 || !Enum.IsDefined(typeof(LayerBlendMode), blend))
                            throw new InvalidDataException("Invalid layer attributes.");
                        bool clipping = version >= 5 && reader.ReadBoolean();
                        int kind = version >= 3 ? reader.ReadInt32() : (int)LayerKind.Raster;
                        if (!Enum.IsDefined(typeof(LayerKind), kind) || kind == (int)LayerKind.Group && version < 6) throw new InvalidDataException("Unknown layer kind; a newer reader is required.");
                        if (blend == (int)LayerBlendMode.PassThrough && kind != (int)LayerKind.Group) throw new InvalidDataException("Pass through applies to groups only.");
                        var parentId = version >= 6 ? new Guid(ReadExact(reader, 16)) : Guid.Empty;
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
                            if (kind == (int)LayerKind.Adjustment)
                            {
                                if (version < 4) throw new InvalidDataException("Adjustment layers require archive version 4.");
                                int type = reader.ReadInt32(), algorithm = reader.ReadInt32();
                                if (!Enum.IsDefined(typeof(AdjustmentType), type)) throw new InvalidDataException("Unknown adjustment type; a newer reader is required.");
                                if (algorithm != AdjustmentSettings.AlgorithmVersion) throw new InvalidDataException("Adjustment algorithm version " + algorithm + " is not supported by this reader; source retained unchanged.");
                                var p = new double[8]; for (int i = 0; i < 8; i++) p[i] = reader.ReadDouble();
                                AdjustmentSettings settings;
                                try
                                {
                                    switch ((AdjustmentType)type)
                                    {
                                        case AdjustmentType.Invert: settings = AdjustmentSettings.Invert(); break;
                                        case AdjustmentType.Levels: settings = AdjustmentSettings.Levels(p[0], p[1], p[2], p[3], p[4]); break;
                                        default: settings = AdjustmentSettings.HueSaturation(p[5], p[6], p[7]); break;
                                    }
                                }
                                catch (ArgumentException ex) { throw new InvalidDataException("Invalid adjustment parameters.", ex); }
                                int enabledCount = ReadCount(reader, 6, "adjustment channels"); var targets = new System.Collections.Generic.List<PaintChannel>();
                                for (int e = 0; e < enabledCount; e++)
                                {
                                    int c = reader.ReadInt32();
                                    if (!Enum.IsDefined(typeof(PaintChannel), c) || targets.Contains((PaintChannel)c) || !settings.AppliesTo((PaintChannel)c)) throw new InvalidDataException("Invalid adjustment channel.");
                                    targets.Add((PaintChannel)c);
                                }
                                layer = doc.AddAdjustmentLayer(layerName, settings, targets, layerId);
                            }
                            else if (kind == (int)LayerKind.Group) layer = doc.AddGroup(layerName, layerId);
                            else layer = kind == (int)LayerKind.Fill ? doc.AddFillLayer(layerName, values, layerId) : doc.AddLayer(layerName, layerId);
                            foreach (var channel in disabled) doc.SetChannelEnabled(layer.Id, channel, false);
                        }
                        else layer = doc.AddLayer(layerName, layerId);
                        doc.SetParentForLoad(layer, parentId);
                        doc.SetLayerVisibility(layer.Id, visible); doc.SetLayerOpacity(layer.Id, opacity); doc.SetLayerBlendMode(layer.Id, (LayerBlendMode)blend);
                        doc.SetLayerClipping(layer.Id, clipping);
                        int channelCount = ReadCount(reader, 6, "channels");
                        if (layer.Kind != LayerKind.Raster && channelCount != 0) throw new InvalidDataException("Only raster layers have pixel channels.");
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
                        if (version >= 8 && reader.ReadBoolean())
                        {
                            var path = ReadPath(reader);
                            if (layer.Kind != LayerKind.Raster || !layer.IsChannelEnabled(path.Channel)) throw new InvalidDataException("A surface path needs a paint layer with its channel enabled.");
                            layer.Path = path;
                        }
                        if (version >= 9 && reader.ReadBoolean())
                        {
                            ReadFilters(reader, doc, layer, FilterTarget.Content);
                            if (layer.Mask != null) ReadFilters(reader, doc, layer, FilterTarget.Mask);
                        }
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException("Trailing native data requires a newer reader.");
                    try { doc.ValidateStructure(); }
                    catch (InvalidOperationException ex) { throw new InvalidDataException("Invalid layer groups: " + ex.Message, ex); }
                    doc.ClearHistory(); return doc;
                }
                catch (EndOfStreamException ex) { throw new InvalidDataException("Native archive is truncated.", ex); }
            }
        }
        static void WriteFilters(BinaryWriter writer, System.Collections.Generic.IReadOnlyList<FilterEffect> filters, bool content)
        {
            writer.Write(filters.Count);
            foreach (var e in filters)
            {
                var f = e.Settings;
                writer.Write(e.Id.ToByteArray()); writer.Write((int)f.Type); writer.Write(f.AlgorithmVersion); writer.Write(e.Enabled); writer.Write(e.Strength);
                if (content) { writer.Write(e.Channels.Count); foreach (var c in e.Channels) writer.Write((int)c); }
                writer.Write(f.Radius); writer.Write(f.Amount); writer.Write(f.Threshold); writer.Write(f.Seed); writer.Write(f.Monochrome);
                foreach (double v in new[] { f.InputBlack, f.InputWhite, f.Gamma, f.OutputBlack, f.OutputWhite }) writer.Write(v);
            }
        }
        /// <summary>Reads one filter stack and adds it through the document (the same validation as editing: value types of the
        /// channels, halo and working budget). Unknown types and algorithm versions are refused, never dropped.</summary>
        static void ReadFilters(BinaryReader reader, PaintDocument doc, PaintLayer layer, FilterTarget target)
        {
            int count = ReadCount(reader, PaintDocument.MaxFiltersPerStack, "filters");
            for (int i = 0; i < count; i++)
            {
                var id = new Guid(ReadExact(reader, 16)); int type = reader.ReadInt32(), algorithm = reader.ReadInt32();
                if (!Enum.IsDefined(typeof(FilterType), type)) throw new InvalidDataException("Unknown filter type " + type + "; a newer reader is required (source retained unchanged).");
                if (algorithm != FilterSettings.AlgorithmVersionOf((FilterType)type)) throw new InvalidDataException("Filter algorithm version " + algorithm + " of " + (FilterType)type + " is not supported by this reader; source retained unchanged.");
                bool enabled = reader.ReadBoolean(); double strength = reader.ReadDouble();
                System.Collections.Generic.List<PaintChannel> channels = null;
                if (target == FilterTarget.Content)
                {
                    int n = ReadCount(reader, 6, "filter channels"); channels = new System.Collections.Generic.List<PaintChannel>();
                    for (int c = 0; c < n; c++) { int v = reader.ReadInt32(); if (!Enum.IsDefined(typeof(PaintChannel), v) || channels.Contains((PaintChannel)v)) throw new InvalidDataException("Invalid or duplicate filter channel."); channels.Add((PaintChannel)v); }
                }
                int radius = reader.ReadInt32(); double amount = reader.ReadDouble(); int threshold = reader.ReadInt32(), seed = reader.ReadInt32(); bool mono = reader.ReadBoolean();
                var p = new double[5]; for (int k = 0; k < 5; k++) p[k] = reader.ReadDouble();
                try
                {
                    var settings = FilterSettings.FromValues((FilterType)type, radius, amount, threshold, seed, mono, p[0], p[1], p[2], p[3], p[4]);
                    doc.AddFilter(layer.Id, target, settings, channels, -1, id, enabled, strength);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) { throw new InvalidDataException("Invalid filter on layer '" + layer.Name + "': " + ex.Message, ex); }
            }
        }
        static void WritePath(BinaryWriter writer, Paths.SurfacePath path)
        {
            writer.Write(Paths.SurfacePath.AlgorithmVersion); writer.Write(path.Id.ToByteArray()); writer.Write((int)path.Channel); WriteString(writer, path.ModelFingerprint);
            var b = path.Brush;
            foreach (double v in new[] { b.RadiusWorld, b.Hardness, b.Spacing, b.Opacity, b.Flow }) writer.Write(v);
            writer.Write(b.Color.R); writer.Write(b.Color.G); writer.Write(b.Color.B); writer.Write(b.Color.A);
            writer.Write(b.Erase); writer.Write(b.PressureSize); writer.Write(b.PressureOpacity); writer.Write(b.PressureFlow);
            writer.Write(path.Points.Count);
            foreach (var point in path.Points) { writer.Write(point.Triangle); writer.Write(point.U); writer.Write(point.V); writer.Write(point.Pressure); }
        }
        static Paths.SurfacePath ReadPath(BinaryReader reader)
        {
            int algorithm = reader.ReadInt32();
            if (algorithm != Paths.SurfacePath.AlgorithmVersion) throw new InvalidDataException("Surface path algorithm version " + algorithm + " is not supported by this reader; source retained unchanged.");
            var id = new Guid(ReadExact(reader, 16)); int channel = reader.ReadInt32(); string fingerprint = ReadString(reader);
            var brush = new Paths.PathBrush { RadiusWorld = reader.ReadDouble(), Hardness = reader.ReadDouble(), Spacing = reader.ReadDouble(), Opacity = reader.ReadDouble(), Flow = reader.ReadDouble(),
                Color = new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()), Erase = reader.ReadBoolean(), PressureSize = reader.ReadBoolean(), PressureOpacity = reader.ReadBoolean(), PressureFlow = reader.ReadBoolean() };
            int count = ReadCount(reader, Paths.SurfacePath.MaxPoints, "path points");
            var points = new Paths.PathPoint[count];
            try
            {
                for (int i = 0; i < count; i++) points[i] = new Paths.PathPoint(reader.ReadInt32(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
                if (!Enum.IsDefined(typeof(PaintChannel), channel)) throw new ArgumentOutOfRangeException(nameof(channel));
                return new Paths.SurfacePath(id, (PaintChannel)channel, fingerprint, brush, points);
            }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid surface path.", ex); }
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
