using System;
using System.Collections.Generic;
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
    /// of dropping the filter. Version 10 ends each layer (after the filters) with a canvas path flag and, when set, the 2D path
    /// (algorithm version, id, channel, brush, points as x, y, pressure); the version 8 block holds only surface paths, and a
    /// layer with both is refused. Version 11 adds generator stages to the filter stacks: filter type 6 (<see cref="FilterType.Generator"/>,
    /// its filter parameters at their defaults) is followed by the generator block (type, algorithm version, low, high, softness,
    /// invert, breakup amount, scale, seed and space, blend, balance, axis, direction x, y, z, bent normal, and the pins: a count and
    /// per pin the mesh-map kind and the 64-digit condition key); an unknown generator type, algorithm version, blend, space or map
    /// kind refuses the archive, and type 6 in an older archive is refused. The mesh maps themselves are not in this entry (they are
    /// derived and live next to it in the .ylp). Version 12 turns the clipping byte (version 5) into a byte of attribute flags: bit 0
    /// clipping, bit 1 "the layer's locks follow" (then an int of <see cref="LayerLocks"/>, never 0); any other bit, or an unknown lock
    /// bit, refuses the archive. A document without locks is laid out exactly as version 11 (the byte is 0 or 1), only the version
    /// number differs. Version 13 adds the shape gradient (generator type 5): its generator block is followed by the shape (int) and
    /// the volume (centre x, y, z, rotation x, y, z, size x, y, z, falloff as doubles); generators of other types are written as in
    /// version 11. Type 5 in an older archive and an unknown shape are refused. Version 14 adds attribute bit 2 "the layer's per-channel
    /// blend settings follow" (<see cref="ChannelBlend"/>): after the locks, a byte count (1..6) and per entry, in channel order, an int
    /// channel, a byte of parts (bit 0 mode, bit 1 opacity, never 0) and then an int blend mode and/or a double opacity. An unknown part
    /// bit, channel or mode, pass through on a layer that is not a group, an opacity outside 0..1, a repeated channel or an empty list
    /// refuses the archive; bit 2 in an older archive (12 or 13) is an unknown bit. A document without per-channel settings is laid out
    /// exactly as version 13, only the version number differs. Version 15 adds the ID colour generator (type 6): its generator block is
    /// followed by the tolerance (int, 0–255), the number of colours (int, 0–<see cref="GeneratorSettings.MaxIdColors"/>) and the colours
    /// (int 0xRRGGBB each, in the generator's order, no repeats); generators of other types are written as before, so a document without
    /// one is laid out exactly as version 14 and only the version number differs. Type 6 in an older archive, a colour outside 0–0xFFFFFF,
    /// a repeated colour and a count or tolerance out of range are refused. Version 16 adds fill layers' images and projection: bit 3 of
    /// the attribute byte says that after the layer's fill values come an int count (1..6, or 0 with a projection that is not the
    /// default), per image an int channel (one with a fill value, no duplicates) and the 16-byte resource ID (not empty), then the
    /// projection (int algorithm version, int mode, int wrap, doubles tiles u, v, offset u, v, rotation, blend width, placement centre
    /// x, y, z, rotation x, y, z, size x, y, z); the bit on another kind of layer or in an older archive (12–15), an unknown mode, wrap or
    /// algorithm version and values out of range are refused. A document without fill images is laid out as version 15, only the version
    /// number differs. Version 17 adds decals: projection mode 5 (<see cref="FillProjectionMode.Decal"/>) and wrap 2
    /// (<see cref="FillWrap.None"/>); a decal's projection block ends with its culling (doubles depth hardness, back-face angle, back-face
    /// hardness). Mode 5 or wrap 2 in an older archive is refused. A document without decals is laid out as version 16 (only the version
    /// number differs). Version 18 appends the path material to each present 2D/3D path: a byte count (0 means the legacy single
    /// channel, otherwise 1..6), followed by the channel (int) and straight RGBA8 value for each entry. Unknown or repeated channels
    /// and counts above 6 are refused. Older archives still load.</summary>
    public static class DocumentBinary
    {
        /// <summary>The version that added per-channel blend modes and opacities (attribute bit 2).</summary>
        internal const int ChannelBlendsVersion = 14;
        /// <summary>The version that added the ID colour generator (generator type 6 and its colours after the generator block).</summary>
        internal const int IdColorVersion = 15;
        /// <summary>The version that added fill layers' images and projection (attribute bit 3 and the block after the fill values).</summary>
        internal const int FillImageVersion = 16;
        /// <summary>The version that added decals (projection mode 5, wrap 2 and the culling after the projection).</summary>
        internal const int DecalVersion = 17;
        /// <summary>The version that added the path material (channel count, channels and values after each present path).</summary>
        internal const int MaterialPathVersion = 18;
        const int Version = MaterialPathVersion;
        /// <summary>The version <see cref="Write"/> produces.</summary>
        public const int CurrentVersion = Version;
        /// <summary>The version that added the shape gradient (generator type 5 and its volume after the generator block).</summary>
        internal const int ShapeGradientVersion = 13;
        static bool IsReadable(int version) => version >= 1 && version <= Version;
        const long MaxArchiveBytes = 512L * 1024 * 1024;
        /// <summary>The most layers a native document holds (the reader refuses more).</summary>
        public const int MaxLayers = 2048;
        /// <summary>Version 12 layer attribute byte: bit 0 clipping, bit 1 an int of layer locks follows; version 14 bit 2 the per-channel
        /// blend settings follow; version 16 bit 3 the fill images and projection follow the fill values.</summary>
        const int AttributeClipping = 1, AttributeLocks = 2, AttributeChannelBlends = 4, AttributeFillImages = 8;
        /// <summary>Parts of a per-channel blend entry.</summary>
        const int BlendPartMode = 1, BlendPartOpacity = 2;
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
                    // 版 12: 属性の印（ビット 0 クリッピング、ビット 1 ロックが続く）。ロックの無い層は版 11 と同じ 0 か 1。版 14: ビット 2 チャンネルごとの合成。版 16: ビット 3 塗りつぶしの画像と投影が続く
                    bool fillImages = layer.Kind == LayerKind.Fill && (layer.FillImages.Count > 0 || !layer.Projection.Equals(FillProjection.Default));
                    writer.Write((byte)((layer.Clipping ? AttributeClipping : 0) | (layer.Locks != LayerLocks.None ? AttributeLocks : 0) | (layer.HasChannelBlends ? AttributeChannelBlends : 0) | (fillImages ? AttributeFillImages : 0)));
                    if (layer.Locks != LayerLocks.None) writer.Write((int)layer.Locks);
                    if (layer.HasChannelBlends) WriteChannelBlends(writer, layer); // 版 14
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
                    if (fillImages) WriteFillImages(writer, layer);
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
                    var surfacePath = layer.Path as Paths.SurfacePath;
                    writer.Write(surfacePath != null);
                    if (surfacePath != null) WritePath(writer, surfacePath);
                    bool filtered = layer.Filters.Count > 0 || layer.Mask != null && layer.Mask.Filters.Count > 0;
                    writer.Write(filtered);
                    if (filtered) { WriteFilters(writer, layer.Filters, true); if (layer.Mask != null) WriteFilters(writer, layer.Mask.Filters, false); }
                    var canvasPath = layer.Path as Paths.CanvasPath;
                    writer.Write(canvasPath != null);
                    if (canvasPath != null) WriteCanvasPath(writer, canvasPath);
                }
                writer.Flush(); return stream.ToArray();
            }
        }

        /// <summary>The document ID from the archive header (magic, version 1–<see cref="CurrentVersion"/>, ID) without reading the
        /// layers. The .ylp format 2 → 3 migration names the texture set after it.</summary>
        public static Guid ReadId(byte[] bytes)
        {
            if (bytes == null || bytes.Length < Magic.Length + 4 + 16) throw new InvalidDataException("The native document is too short to hold its header.");
            for (int i = 0; i < Magic.Length; i++) if (bytes[i] != Magic[i]) throw new InvalidDataException("Not a dot paint archive.");
            int version = BitConverter.ToInt32(bytes, Magic.Length);
            if (!BitConverter.IsLittleEndian) version = (int)((uint)version >> 24 | ((uint)version >> 8 & 0xff00) | ((uint)version << 8 & 0xff0000) | (uint)version << 24);
            if (!IsReadable(version)) throw new InvalidDataException("Unsupported archive version; source retained unchanged.");
            var id = new Guid(bytes.Skip(Magic.Length + 4).Take(16).ToArray());
            if (id == Guid.Empty) throw new InvalidDataException("The native document has no ID.");
            return id;
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
                    if (!IsReadable(version)) throw new InvalidDataException("Unsupported archive version; source retained unchanged.");
                    var id = new Guid(ReadExact(reader, 16));
                    int width = reader.ReadInt32(), height = reader.ReadInt32(), tileSize = reader.ReadInt32();
                    if (width < 1 || height < 1 || width > PaintDocument.MaxNativeSide || height > PaintDocument.MaxNativeSide || tileSize < 8 || tileSize > 512 || (tileSize & (tileSize - 1)) != 0)
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
                    int layers = ReadCount(reader, MaxLayers, "layers");
                    var lockedLayers = new System.Collections.Generic.List<(PaintLayer layer, LayerLocks locks)>();
                    for (int l = 0; l < layers; l++)
                    {
                        var layerId = new Guid(ReadExact(reader, 16));
                        string layerName = ReadString(reader);
                        bool visible = reader.ReadBoolean(); double opacity = reader.ReadDouble(); int blend = reader.ReadInt32();
                        if (double.IsNaN(opacity) || double.IsInfinity(opacity) || opacity < 0 || opacity > 1 || !Enum.IsDefined(typeof(LayerBlendMode), blend))
                            throw new InvalidDataException("Invalid layer attributes.");
                        bool clipping = false, fillImages = false; var locks = LayerLocks.None; System.Collections.Generic.List<(PaintChannel, ChannelBlend)> channelBlends = null;
                        if (version >= 12)
                        {
                            int attributes = reader.ReadByte();
                            int known = AttributeClipping | AttributeLocks | (version >= ChannelBlendsVersion ? AttributeChannelBlends : 0) | (version >= FillImageVersion ? AttributeFillImages : 0);
                            if ((attributes & ~known) != 0) throw new InvalidDataException("Unknown layer attribute flags " + attributes + "; a newer reader is required (source retained unchanged).");
                            clipping = (attributes & AttributeClipping) != 0; fillImages = (attributes & AttributeFillImages) != 0;
                            if ((attributes & AttributeLocks) != 0)
                            {
                                int value = reader.ReadInt32();
                                if (value == 0 || (value & ~(int)PaintDocument.KnownLocks) != 0) throw new InvalidDataException("Unknown layer lock flags " + value + "; a newer reader is required (source retained unchanged).");
                                locks = (LayerLocks)value;
                            }
                            if ((attributes & AttributeChannelBlends) != 0) channelBlends = ReadChannelBlends(reader);
                        }
                        else clipping = version >= 5 && reader.ReadBoolean();
                        int kind = version >= 3 ? reader.ReadInt32() : (int)LayerKind.Raster;
                        if (!Enum.IsDefined(typeof(LayerKind), kind) || kind == (int)LayerKind.Group && version < 6) throw new InvalidDataException("Unknown layer kind; a newer reader is required.");
                        if (blend == (int)LayerBlendMode.PassThrough && kind != (int)LayerKind.Group) throw new InvalidDataException("Pass through applies to groups only.");
                        var parentId = version >= 6 ? new Guid(ReadExact(reader, 16)) : Guid.Empty;
                        PaintLayer layer; FillProjection projection = null;
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
                            if (fillImages && kind != (int)LayerKind.Fill) throw new InvalidDataException("Only fill layers have images and a projection.");
                            var images = fillImages ? ReadFillImages(reader, values, version, out projection) : null;
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
                            if (images != null) doc.SetFillImagesForLoad(layer, images, projection);
                        }
                        else layer = doc.AddLayer(layerName, layerId);
                        doc.SetParentForLoad(layer, parentId);
                        doc.SetLayerVisibility(layer.Id, visible); doc.SetLayerOpacity(layer.Id, opacity); doc.SetLayerBlendMode(layer.Id, (LayerBlendMode)blend);
                        doc.SetLayerClipping(layer.Id, clipping);
                        if (channelBlends != null)
                            foreach (var (blendChannel, channelBlend) in channelBlends)
                            {
                                try { doc.SetChannelBlendForLoad(layer, blendChannel, channelBlend); }
                                catch (ArgumentException ex) { throw new InvalidDataException("Invalid per-channel blend setting of layer '" + layerName + "': " + ex.Message, ex); }
                            }
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
                            var path = ReadPath(reader, version);
                            if (layer.Kind != LayerKind.Raster || !PathChannelsPresent(layer, path)) throw new InvalidDataException("A surface path needs a paint layer with its channel enabled.");
                            layer.Path = path;
                        }
                        if (version >= 9 && reader.ReadBoolean())
                        {
                            ReadFilters(reader, doc, layer, FilterTarget.Content, version);
                            if (layer.Mask != null) ReadFilters(reader, doc, layer, FilterTarget.Mask, version);
                        }
                        if (version >= 10 && reader.ReadBoolean())
                        {
                            var path = ReadCanvasPath(reader, version);
                            if (layer.Path != null) throw new InvalidDataException("A layer has both a surface path and a canvas path.");
                            if (layer.Kind != LayerKind.Raster || !PathChannelsPresent(layer, path)) throw new InvalidDataException("A canvas path needs a paint layer with its channel enabled.");
                            layer.Path = path;
                        }
                        if (locks != LayerLocks.None) lockedLayers.Add((layer, locks));
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException("Trailing native data requires a newer reader.");
                    try { doc.ValidateStructure(); }
                    catch (InvalidOperationException ex) { throw new InvalidDataException("Invalid layer groups: " + ex.Message, ex); }
                    // ロックは全部を読んでから付ける（読み手自身の設定をロックが断らないように）
                    foreach (var (layer, locks) in lockedLayers) doc.SetLocksForLoad(layer, locks);
                    doc.ClearHistory(); return doc;
                }
                catch (EndOfStreamException ex) { throw new InvalidDataException("Native archive is truncated.", ex); }
            }
        }
        /// <summary>Version 16: a fill layer's images (channel and resource ID per image, by channel) and its projection; version 17 ends a
        /// decal's with its culling.</summary>
        static void WriteFillImages(BinaryWriter writer, PaintLayer layer)
        {
            var images = layer.FillImages.OrderBy(e => e.Key).ToArray();
            writer.Write(images.Length);
            foreach (var image in images) { writer.Write((int)image.Key); writer.Write(image.Value.ToByteArray()); }
            var p = layer.Projection; var v = p.Placement;
            writer.Write(FillProjection.AlgorithmVersion); writer.Write((int)p.Mode); writer.Write((int)p.Wrap);
            foreach (double d in new[] { p.TileU, p.TileV, p.OffsetU, p.OffsetV, p.Rotation, p.BlendWidth, v.CenterX, v.CenterY, v.CenterZ, v.RotationX, v.RotationY, v.RotationZ, v.SizeX, v.SizeY, v.SizeZ }) writer.Write(d);
            if (p.IsDecal) { writer.Write(p.DepthHardness); writer.Write(p.BackfaceAngle); writer.Write(p.BackfaceHardness); } // 版 17: デカールだけ
        }
        /// <summary>The version 16 fill images block (version 17: the decal and its culling). Unknown values are refused, never replaced by defaults.</summary>
        static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<PaintChannel, Guid>> ReadFillImages(BinaryReader reader,
            System.Collections.Generic.Dictionary<PaintChannel, Rgba32> values, int version, out FillProjection projection)
        {
            int count = ReadCount(reader, 6, "fill images");
            var images = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<PaintChannel, Guid>>();
            for (int i = 0; i < count; i++)
            {
                int channel = reader.ReadInt32(); var id = new Guid(ReadExact(reader, 16));
                if (!Enum.IsDefined(typeof(PaintChannel), channel) || images.Any(e => e.Key == (PaintChannel)channel)) throw new InvalidDataException("Invalid or duplicate fill image channel.");
                if (!values.ContainsKey((PaintChannel)channel)) throw new InvalidDataException("A fill image's channel (" + (PaintChannel)channel + ") has no fill value.");
                if (id == Guid.Empty) throw new InvalidDataException("A fill image has no resource ID.");
                images.Add(new System.Collections.Generic.KeyValuePair<PaintChannel, Guid>((PaintChannel)channel, id));
            }
            int algorithm = reader.ReadInt32(), mode = reader.ReadInt32(), wrap = reader.ReadInt32();
            if (algorithm != FillProjection.AlgorithmVersion) throw new InvalidDataException("Fill projection algorithm version " + algorithm + " is not supported by this reader; source retained unchanged.");
            if (!Enum.IsDefined(typeof(FillProjectionMode), mode) || mode == (int)FillProjectionMode.Decal && version < DecalVersion) throw new InvalidDataException("Unknown fill projection " + mode + "; a newer reader is required (source retained unchanged).");
            if (!Enum.IsDefined(typeof(FillWrap), wrap) || wrap == (int)FillWrap.None && version < DecalVersion) throw new InvalidDataException("Unknown fill wrap " + wrap + "; a newer reader is required (source retained unchanged).");
            bool decal = mode == (int)FillProjectionMode.Decal;
            var d = new double[decal ? 18 : 15]; for (int k = 0; k < d.Length; k++) d[k] = reader.ReadDouble();
            try
            {
                var placement = new ShapeVolume(GeneratorShape.Box, d[6], d[7], d[8], d[9], d[10], d[11], d[12], d[13], d[14], 0);
                projection = decal
                    ? new FillProjection((FillProjectionMode)mode, (FillWrap)wrap, d[0], d[1], d[2], d[3], d[4], d[5], placement, d[15], d[16], d[17])
                    : new FillProjection((FillProjectionMode)mode, (FillWrap)wrap, d[0], d[1], d[2], d[3], d[4], d[5], placement);
            }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid fill projection: " + ex.Message, ex); }
            if (count == 0 && projection.Equals(FillProjection.Default)) throw new InvalidDataException("An empty fill images block (no images, default projection) is never written.");
            return images;
        }
        static void WriteChannelBlends(BinaryWriter writer, PaintLayer layer)
        {
            var channels = layer.ChannelBlends.Keys.OrderBy(c => c).ToArray();
            writer.Write((byte)channels.Length);
            foreach (var channel in channels)
            {
                var b = layer.ChannelBlends[channel];
                writer.Write((int)channel);
                writer.Write((byte)((b.Mode.HasValue ? BlendPartMode : 0) | (b.Opacity.HasValue ? BlendPartOpacity : 0)));
                if (b.Mode.HasValue) writer.Write((int)b.Mode.Value);
                if (b.Opacity.HasValue) writer.Write(b.Opacity.Value);
            }
        }
        /// <summary>The version 14 per-channel blend block. Values are checked against the layer (pass through on groups only) once it exists.</summary>
        static System.Collections.Generic.List<(PaintChannel, ChannelBlend)> ReadChannelBlends(BinaryReader reader)
        {
            int count = reader.ReadByte();
            if (count < 1 || count > 6) throw new InvalidDataException("Invalid count of per-channel blend settings (" + count + ").");
            var list = new System.Collections.Generic.List<(PaintChannel, ChannelBlend)>(); var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                int channel = reader.ReadInt32(); int parts = reader.ReadByte();
                if (!Enum.IsDefined(typeof(PaintChannel), channel) || !seen.Add(channel)) throw new InvalidDataException("Invalid or duplicate channel in per-channel blend settings.");
                if (parts == 0 || (parts & ~(BlendPartMode | BlendPartOpacity)) != 0) throw new InvalidDataException("Unknown per-channel blend parts " + parts + "; a newer reader is required (source retained unchanged).");
                LayerBlendMode? mode = null; double? opacity = null;
                if ((parts & BlendPartMode) != 0)
                {
                    int m = reader.ReadInt32();
                    if (!Enum.IsDefined(typeof(LayerBlendMode), m)) throw new InvalidDataException("Unknown blend mode " + m + " in per-channel blend settings; a newer reader is required (source retained unchanged).");
                    mode = (LayerBlendMode)m;
                }
                if ((parts & BlendPartOpacity) != 0)
                {
                    double o = reader.ReadDouble();
                    if (double.IsNaN(o) || double.IsInfinity(o) || o < 0 || o > 1) throw new InvalidDataException("Invalid per-channel opacity.");
                    opacity = o;
                }
                list.Add(((PaintChannel)channel, new ChannelBlend(mode, opacity)));
            }
            return list;
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
                if (f.IsGenerator) WriteGenerator(writer, f.Generator);
            }
        }
        static void WriteGenerator(BinaryWriter writer, GeneratorSettings g)
        {
            writer.Write((int)g.Type); writer.Write(g.AlgorithmVersion);
            writer.Write(g.Low); writer.Write(g.High); writer.Write(g.Softness); writer.Write(g.Invert);
            writer.Write(g.NoiseAmount); writer.Write(g.NoiseScale); writer.Write(g.NoiseSeed); writer.Write((int)g.NoiseSpace);
            writer.Write((int)g.Blend); writer.Write(g.Balance); writer.Write(g.Axis);
            writer.Write(g.DirectionX); writer.Write(g.DirectionY); writer.Write(g.DirectionZ); writer.Write(g.UseBentNormal);
            var pins = g.Pins.Keys.OrderBy(k => k).ToArray();
            writer.Write(pins.Length);
            foreach (var kind in pins) { writer.Write((int)kind); WriteString(writer, g.Pins[kind]); }
            if (g.Type == GeneratorType.ShapeGradient)
            {
                var v = g.Volume;
                writer.Write((int)v.Shape);
                foreach (double d in new[] { v.CenterX, v.CenterY, v.CenterZ, v.RotationX, v.RotationY, v.RotationZ, v.SizeX, v.SizeY, v.SizeZ, v.Falloff }) writer.Write(d);
            }
            if (g.Type == GeneratorType.IdColor)
            {
                writer.Write(g.IdTolerance); writer.Write(g.IdColors.Count);
                foreach (int c in g.IdColors) writer.Write(c);
            }
        }
        const int MaxGeneratorPins = 8;
        /// <summary>The generator block (version 11; the shape gradient's volume from version 13; the ID colours from
        /// <see cref="IdColorVersion"/>). Unknown values are refused, never replaced by defaults.</summary>
        static GeneratorSettings ReadGenerator(BinaryReader reader, int version)
        {
            int type = reader.ReadInt32(), algorithm = reader.ReadInt32();
            if (!Enum.IsDefined(typeof(GeneratorType), type) || type == (int)GeneratorType.ShapeGradient && version < ShapeGradientVersion || type == (int)GeneratorType.IdColor && version < IdColorVersion)
                throw new InvalidDataException("Unknown generator type " + type + "; a newer reader is required (source retained unchanged).");
            if (algorithm != GeneratorSettings.AlgorithmVersionOf((GeneratorType)type)) throw new InvalidDataException("Generator algorithm version " + algorithm + " of " + (GeneratorType)type + " is not supported by this reader; source retained unchanged.");
            double low = reader.ReadDouble(), high = reader.ReadDouble(), softness = reader.ReadDouble(); bool invert = reader.ReadBoolean();
            double noiseAmount = reader.ReadDouble(), noiseScale = reader.ReadDouble(); int noiseSeed = reader.ReadInt32(), noiseSpace = reader.ReadInt32();
            int blend = reader.ReadInt32(); double balance = reader.ReadDouble(); int axis = reader.ReadInt32();
            double dx = reader.ReadDouble(), dy = reader.ReadDouble(), dz = reader.ReadDouble(); bool bent = reader.ReadBoolean();
            if (!Enum.IsDefined(typeof(GeneratorBlend), blend)) throw new InvalidDataException("Unknown generator blend " + blend + "; a newer reader is required (source retained unchanged).");
            if (!Enum.IsDefined(typeof(GeneratorNoiseSpace), noiseSpace)) throw new InvalidDataException("Unknown generator noise space " + noiseSpace + "; a newer reader is required (source retained unchanged).");
            int count = ReadCount(reader, MaxGeneratorPins, "generator pins");
            var pins = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<MeshMaps.MeshMapKind, string>>();
            for (int i = 0; i < count; i++)
            {
                int kind = reader.ReadInt32(); string key = ReadString(reader);
                if (!Enum.IsDefined(typeof(MeshMaps.MeshMapKind), kind)) throw new InvalidDataException("Unknown mesh map kind " + kind + " in a generator pin; a newer reader is required (source retained unchanged).");
                pins.Add(new System.Collections.Generic.KeyValuePair<MeshMaps.MeshMapKind, string>((MeshMaps.MeshMapKind)kind, key));
            }
            var volume = ShapeVolume.Default;
            if (type == (int)GeneratorType.ShapeGradient)
            {
                int shape = reader.ReadInt32();
                if (!Enum.IsDefined(typeof(GeneratorShape), shape)) throw new InvalidDataException("Unknown generator shape " + shape + "; a newer reader is required (source retained unchanged).");
                var v = new double[10]; for (int k = 0; k < v.Length; k++) v[k] = reader.ReadDouble();
                volume = new ShapeVolume((GeneratorShape)shape, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9]);
            }
            int tolerance = IdMapColors.DefaultTolerance; int[] colors = null;
            if (type == (int)GeneratorType.IdColor)
            {
                tolerance = reader.ReadInt32();
                colors = new int[ReadCount(reader, GeneratorSettings.MaxIdColors, "ID colour")];
                for (int k = 0; k < colors.Length; k++) colors[k] = reader.ReadInt32();
            }
            try
            {
                return GeneratorSettings.FromValues((GeneratorType)type, low, high, softness, invert, noiseAmount, noiseScale, noiseSeed, (GeneratorNoiseSpace)noiseSpace,
                    (GeneratorBlend)blend, balance, axis, dx, dy, dz, bent, pins, volume, colors, tolerance);
            }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid generator parameters: " + ex.Message, ex); }
        }
        /// <summary>Reads one filter stack and adds it through the document (the same validation as editing: value types of the
        /// channels, halo and working budget). Unknown types and algorithm versions are refused, never dropped.</summary>
        static void ReadFilters(BinaryReader reader, PaintDocument doc, PaintLayer layer, FilterTarget target, int version)
        {
            int count = ReadCount(reader, PaintDocument.MaxFiltersPerStack, "filters");
            for (int i = 0; i < count; i++)
            {
                var id = new Guid(ReadExact(reader, 16)); int type = reader.ReadInt32(), algorithm = reader.ReadInt32();
                if (!Enum.IsDefined(typeof(FilterType), type) || type == (int)FilterType.Generator && version < 11) throw new InvalidDataException("Unknown filter type " + type + "; a newer reader is required (source retained unchanged).");
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
                GeneratorSettings generator = type == (int)FilterType.Generator ? ReadGenerator(reader, version) : null;
                try
                {
                    FilterSettings settings;
                    if (generator != null)
                    {
                        // Generator の段はフィルターのパラメーターを使わない（既定のままでなければ読み違い）
                        if (radius != 0 || amount != 0 || threshold != 0 || seed != 0 || mono || p[0] != 0 || p[1] != 1 || p[2] != 1 || p[3] != 0 || p[4] != 1)
                            throw new ArgumentException("A generator stage keeps the filter parameters at their defaults.");
                        settings = FilterSettings.FromGenerator(generator);
                    }
                    else settings = FilterSettings.FromValues((FilterType)type, radius, amount, threshold, seed, mono, p[0], p[1], p[2], p[3], p[4]);
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
            WritePathMaterial(writer, path);
        }
        static Paths.SurfacePath ReadPath(BinaryReader reader, int version)
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
                return new Paths.SurfacePath(id, (PaintChannel)channel, fingerprint, brush, points, version >= MaterialPathVersion ? ReadPathMaterial(reader) : null);
            }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid surface path.", ex); }
        }
        static void WriteBrush(BinaryWriter writer, Paths.PathBrush b)
        {
            foreach (double v in new[] { b.RadiusWorld, b.Hardness, b.Spacing, b.Opacity, b.Flow }) writer.Write(v);
            writer.Write(b.Color.R); writer.Write(b.Color.G); writer.Write(b.Color.B); writer.Write(b.Color.A);
            writer.Write(b.Erase); writer.Write(b.PressureSize); writer.Write(b.PressureOpacity); writer.Write(b.PressureFlow);
        }
        static Paths.PathBrush ReadBrush(BinaryReader reader) => new Paths.PathBrush { RadiusWorld = reader.ReadDouble(), Hardness = reader.ReadDouble(), Spacing = reader.ReadDouble(), Opacity = reader.ReadDouble(), Flow = reader.ReadDouble(),
            Color = new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()), Erase = reader.ReadBoolean(), PressureSize = reader.ReadBoolean(), PressureOpacity = reader.ReadBoolean(), PressureFlow = reader.ReadBoolean() };
        static void WriteCanvasPath(BinaryWriter writer, Paths.CanvasPath path)
        {
            writer.Write(Paths.CanvasPath.AlgorithmVersion); writer.Write(path.Id.ToByteArray()); writer.Write((int)path.Channel);
            WriteBrush(writer, path.Brush);
            writer.Write(path.Points.Count);
            foreach (var point in path.Points) { writer.Write(point.X); writer.Write(point.Y); writer.Write(point.Pressure); }
            WritePathMaterial(writer, path);
        }
        static Paths.CanvasPath ReadCanvasPath(BinaryReader reader, int version)
        {
            int algorithm = reader.ReadInt32();
            if (algorithm != Paths.CanvasPath.AlgorithmVersion) throw new InvalidDataException("Canvas path algorithm version " + algorithm + " is not supported by this reader; source retained unchanged.");
            var id = new Guid(ReadExact(reader, 16)); int channel = reader.ReadInt32();
            var brush = ReadBrush(reader);
            int count = ReadCount(reader, Paths.EditablePath.MaxPointCount, "path points");
            var points = new Paths.CanvasPoint[count];
            try
            {
                for (int i = 0; i < count; i++) points[i] = new Paths.CanvasPoint(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
                if (!Enum.IsDefined(typeof(PaintChannel), channel)) throw new ArgumentOutOfRangeException(nameof(channel));
                return new Paths.CanvasPath(id, (PaintChannel)channel, brush, points, version >= MaterialPathVersion ? ReadPathMaterial(reader) : null);
            }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid canvas path.", ex); }
        }
        static bool PathChannelsPresent(PaintLayer layer, Paths.EditablePath path)
        {
            if (path.Material == null) return layer.IsChannelEnabled(path.Channel);
            foreach (var m in path.Material) if (!layer.TryGetChannel(m.Channel, out _)) return false;
            return true;
        }
        static void WritePathMaterial(BinaryWriter writer, Paths.EditablePath path)
        {
            writer.Write((byte)(path.Material?.Count ?? 0));
            if (path.Material == null) return;
            foreach (var m in path.Material)
            {
                writer.Write((int)m.Channel);
                writer.Write(m.Value.R); writer.Write(m.Value.G); writer.Write(m.Value.B); writer.Write(m.Value.A);
            }
        }
        static IReadOnlyList<ChannelPaint> ReadPathMaterial(BinaryReader reader)
        {
            int count = reader.ReadByte();
            if (count == 0) return null;
            if (count > 6) throw new InvalidDataException("Invalid path material channel count.");
            var material = new ChannelPaint[count];
            for (int i = 0; i < count; i++) material[i] = new ChannelPaint((PaintChannel)reader.ReadInt32(),
                new Rgba32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()));
            return material;
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
