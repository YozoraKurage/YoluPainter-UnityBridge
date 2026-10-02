using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Yozolab.YoluPainter.Core.Psd
{
    /// <summary>Conservative PSD v1 RGB8 raster exchange. No third-party dependencies or Unity objects.
    /// Unknown semantics fail closed to whole-file preservation, never a reconstructed partial export.
    /// Editable layer features: the 26 blend modes with a native equivalent, clipping, raster user masks (with density),
    /// visibility and opacity. Groups, fill/adjustment/text/smart-object layers, effects and vector masks are PreserveOnly.</summary>
    public static class PsdCodec
    {
        /// <summary>Non-blocking diagnostic: non-rendering information (layer metadata, locks, colour labels, folder open/closed
        /// state, print/guide/thumbnail/XMP… image resources) that the editable document does not carry. It stays in the
        /// original PSD bytes but is not written into PSDs exported from YoluPainter. One diagnostic per kind.</summary>
        public const string NotCarriedIntoExport = "NotCarriedIntoExport";
        /// <summary>Non-blocking diagnostic: the stored merged image differs from YoluPainter's compositing of blend modes,
        /// clipping, masks or groups.</summary>
        public const string CompositeDiffers = "CompositeDiffers";
        /// <summary>True for the diagnostics that do not block editing (<see cref="NotCarriedIntoExport"/>, <see cref="CompositeDiffers"/>).</summary>
        public static bool IsInformational(PsdDiagnostic diagnostic)
        { return diagnostic != null && (diagnostic.Code == NotCarriedIntoExport || diagnostic.Code == CompositeDiffers); }

        private static readonly Encoding Utf16 = new UnicodeEncoding(true, false, true);
        /// <summary>PSD blend keys of the native modes. Photoshop calls Color Dodge "div " and Color Burn "idiv"; Exclusion is "smud".</summary>
        private static readonly KeyValuePair<LayerBlendMode, string>[] BlendKeys =
        {
            Pair(LayerBlendMode.Normal, "norm"), Pair(LayerBlendMode.Multiply, "mul "), Pair(LayerBlendMode.Screen, "scrn"),
            Pair(LayerBlendMode.Overlay, "over"), Pair(LayerBlendMode.Darken, "dark"), Pair(LayerBlendMode.Lighten, "lite"),
            Pair(LayerBlendMode.ColorDodge, "div "), Pair(LayerBlendMode.ColorBurn, "idiv"), Pair(LayerBlendMode.LinearDodge, "lddg"),
            Pair(LayerBlendMode.LinearBurn, "lbrn"), Pair(LayerBlendMode.HardLight, "hLit"), Pair(LayerBlendMode.SoftLight, "sLit"),
            Pair(LayerBlendMode.VividLight, "vLit"), Pair(LayerBlendMode.LinearLight, "lLit"), Pair(LayerBlendMode.PinLight, "pLit"),
            Pair(LayerBlendMode.HardMix, "hMix"), Pair(LayerBlendMode.Difference, "diff"), Pair(LayerBlendMode.Exclusion, "smud"),
            Pair(LayerBlendMode.Subtract, "fsub"), Pair(LayerBlendMode.Divide, "fdiv"), Pair(LayerBlendMode.Hue, "hue "),
            Pair(LayerBlendMode.Saturation, "sat "), Pair(LayerBlendMode.Color, "colr"), Pair(LayerBlendMode.Luminosity, "lum "),
            Pair(LayerBlendMode.DarkerColor, "dkCl"), Pair(LayerBlendMode.LighterColor, "lgCl"),
        };
        private static KeyValuePair<LayerBlendMode, string> Pair(LayerBlendMode mode, string key) { return new KeyValuePair<LayerBlendMode, string>(mode, key); }

        /// <summary>PSD key of a native blend mode, or null when it has none on a raster layer (pass-through is for groups).</summary>
        public static string BlendKey(LayerBlendMode mode)
        {
            foreach (var pair in BlendKeys) if (pair.Key == mode) return pair.Value;
            return null;
        }
        public static bool TryGetBlendMode(string key, out LayerBlendMode mode)
        {
            foreach (var pair in BlendKeys) if (pair.Value == key) { mode = pair.Key; return true; }
            mode = LayerBlendMode.Normal; return false;
        }

        /// <summary>Tagged-block keys whose presence means a feature this codec does not represent, for a clearer diagnostic.</summary>
        private static string DescribeTag(string key)
        {
            switch (key)
            {
                case "vmsk": case "vsms": return "Vector mask (" + key + ") is not represented.";
                case "SoCo": case "GdFl": case "PtFl": return "Fill layer (" + key + ") is not represented.";
                case "levl": case "curv": case "brit": case "blnc": case "hue ": case "hue2": case "selc": case "thrs": case "nvrt":
                case "post": case "mixr": case "grdm": case "phfl": case "expA": case "vibA": case "clrL": case "blwh": case "CgEd":
                    return "Adjustment layer (" + key + ") is not represented.";
                case "lfx2": case "lrFX": case "lfxs": case "lmfx": return "Layer effects (" + key + ") are not represented.";
                case "TySh": case "tySh": return "Text layer (" + key + ") is not represented.";
                case "SoLd": case "SoLE": case "PlLd": case "plLd": return "Smart object (" + key + ") is not represented.";
                default: return null;
            }
        }

        /// <summary>Section divider kind of a layer record (lsct / lsdk type): 0 any other layer, 1 open folder, 2 closed folder,
        /// 3 bounding divider ("&lt;/Layer group&gt;", the bottom of a folder's contents).</summary>
        private enum RecordKind { Raster, Folder, Divider }
        private sealed class Record
        {
            internal PsdRasterLayer Layer;
            internal short[] Channels;
            internal int[] Lengths;
            internal RecordKind Kind;
            internal int Offset;
            // Raw values interpreted once the section divider setting (a tagged block after them) is known.
            internal string BlendKey;
            internal int Clipping, Flags;
            internal bool NeutralRanges;
            internal int SectionType = -1, SectionSubType;
            internal string SectionKey;
            /// <summary>The record has a section divider setting this codec cannot interpret, so the folder structure is unknown.</summary>
            internal bool UnknownSection;
        }
        private sealed class ParseState
        {
            internal readonly PsdLimits Limits;
            internal readonly List<PsdDiagnostic> Diagnostics = new List<PsdDiagnostic>();
            internal bool Unsupported;
            internal long DecodedBytes;
            internal long MetadataBytes;
            private readonly List<string> notCarriedOrder = new List<string>();
            private readonly Dictionary<string, int[]> notCarried = new Dictionary<string, int[]>();
            internal ParseState(PsdLimits limits) { Limits = limits; }
            /// <summary>Accepts non-rendering information that the editable document does not carry; reported once per kind.</summary>
            internal void NotCarried(string what, int offset, int length)
            {
                int[] seen;
                if (notCarried.TryGetValue(what, out seen)) { seen[0]++; return; }
                notCarried.Add(what, new[] { 1, offset, length }); notCarriedOrder.Add(what);
            }
            internal void FlushNotCarried()
            {
                foreach (var what in notCarriedOrder)
                {
                    var seen = notCarried[what];
                    Note(NotCarriedIntoExport, what + (seen[0] > 1 ? " (" + seen[0] + " records)" : "") + ": kept in the original PSD bytes, not carried into exported PSDs.", seen[1], seen[2]);
                }
                notCarriedOrder.Clear();
            }
            internal void Preserve(string code, string text, int offset, int length = 0)
            {
                Unsupported = true;
                if (Diagnostics.Count < Limits.MaxDiagnostics) Diagnostics.Add(new PsdDiagnostic(code, text, offset, length));
            }
            /// <summary>Informational diagnostic that does not block editing.</summary>
            internal void Note(string code, string text, int offset, int length = 0)
            {
                if (Diagnostics.Count < Limits.MaxDiagnostics) Diagnostics.Add(new PsdDiagnostic(code, text, offset, length));
            }
            internal void Metadata(int count, int offset)
            {
                MetadataBytes += count;
                if (MetadataBytes > Limits.MaxMetadataBytes) throw new PsdFormatException("Metadata budget exceeded.", offset);
            }
            internal void Pixels(long count, int offset)
            {
                DecodedBytes += count;
                if (count < 0 || DecodedBytes > Limits.MaxDecodedBytes) throw new PsdFormatException("Decoded pixel budget exceeded.", offset);
            }
        }

        public static PsdReadResult Read(byte[] bytes, PsdLimits limits = null)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            limits = limits ?? new PsdLimits(); limits.Validate();
            if (bytes.Length > limits.MaxSourceBytes) return Rejected(null, "SourceLimit", "PSD source exceeds the configured byte limit.", 0);
            return ReadOwned((byte[])bytes.Clone(), limits);
        }

        /// <summary>Bounded input, including non-seekable streams. Leaves the caller's stream open.</summary>
        public static PsdReadResult Read(Stream stream, PsdLimits limits = null)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            limits = limits ?? new PsdLimits(); limits.Validate();
            using (var buffer = new MemoryStream())
            {
                byte[] chunk = new byte[Math.Min(8192, limits.MaxSourceBytes)];
                while (true)
                {
                    int count = stream.Read(chunk, 0, Math.Min(chunk.Length, limits.MaxSourceBytes - (int)buffer.Length));
                    if (count == 0) break;
                    buffer.Write(chunk, 0, count);
                    if (buffer.Length == limits.MaxSourceBytes)
                    {
                        if (stream.ReadByte() != -1) return Rejected(null, "SourceLimit", "PSD stream exceeds the configured byte limit.", 0);
                        break;
                    }
                }
                return ReadOwned(buffer.ToArray(), limits);
            }
        }

        private static PsdReadResult Rejected(byte[] original, string code, string text, int offset)
        { return new PsdReadResult(PsdCompatibilityMode.Rejected, null, original, new List<PsdDiagnostic> { new PsdDiagnostic(code, text, offset, 0) }); }

        private static PsdReadResult ReadOwned(byte[] bytes, PsdLimits limits)
        {
            var state = new ParseState(limits);
            try
            {
                var r = new PsdReader(bytes, 0, bytes.Length);
                if (r.Key() != "8BPS") throw new PsdFormatException("Not a Photoshop file signature.", 0);
                int version = r.U16();
                if (version != 1 && version != 2) throw new PsdFormatException("Invalid Photoshop version.", 4);
                r.Zeros(6);
                int channels = r.U16();
                uint h = r.U32(), w = r.U32();
                int depth = r.U16(), colorMode = r.U16();
                uint formatMaximum = version == 2 ? 300000U : 30000U;
                if (channels < 1 || channels > 56 || w == 0 || h == 0 || w > formatMaximum || h > formatMaximum)
                    throw new PsdFormatException("Invalid PSD dimensions or channel count.", 12);
                if (version == 2)
                {
                    state.Preserve("PSB", "PSB is retained without editing or decoding.", 4);
                    return Finish(bytes, null, state);
                }
                if (w > limits.MaxDimension || h > limits.MaxDimension || (long)w * h > limits.MaxCanvasPixels)
                    throw new PsdFormatException("Canvas budget exceeded.", 14);
                if (depth != 8 || colorMode != 3 || (channels != 3 && channels != 4))
                {
                    state.Preserve("ColorFormat", "Only RGB8 with three or four merged channels is editable; original color data retained.", 12);
                    return Finish(bytes, null, state);
                }
                var document = new PsdDocument { Width = (int)w, Height = (int)h };
                var colors = r.Section();
                state.Metadata(colors.Remaining, colors.Position);
                if (colors.Remaining != 0) state.Preserve("ColorData", "Unexpected RGB color-mode data is retained.", colors.Position, colors.Remaining);
                ParseResources(r.Section(), state);
                var layerMask = r.Section();
                bool mergedAlpha = false;
                var records = new List<Record>();
                if (layerMask.Remaining == 0)
                    state.Preserve("NoLayers", "Flattened PSD has no editable layer identity; retained unchanged.", layerMask.Position);
                else
                {
                    var info = layerMask.Section();
                    if (info.Remaining == 0) state.Preserve("NoLayers", "PSD has no layer records.", info.Position);
                    else
                    {
                        int signedCount = info.I16();
                        mergedAlpha = signedCount < 0;
                        int count = Math.Abs(signedCount);
                        if (count > limits.MaxLayers) throw new PsdFormatException("Layer count budget exceeded.", info.Position - 2);
                        if (count == 0) state.Preserve("NoLayers", "PSD has no layer records.", info.Position - 2);
                        var ids = new HashSet<int>();
                        for (int i = 0; i < count; i++)
                        {
                            var record = ReadRecord(info, state);
                            // A bounding divider has no layer identity of its own in the native model; its ID is optional.
                            bool optional = record.Kind == RecordKind.Divider && record.Layer.Id == 0;
                            if (!optional && (record.Layer.Id <= 0 || !ids.Add(record.Layer.Id)))
                                state.Preserve("LayerIdentity", "Missing, invalid or duplicate layer ID; no automatic identity repair.", info.Position);
                            records.Add(record);
                        }
                        // Parse metadata first. Unsupported documents are never partially exposed as editable DTOs.
                        foreach (var record in records)
                        {
                            var layer = record.Layer;
                            if (!state.Unsupported)
                            {
                                int length = checked(layer.Width * layer.Height * 4);
                                state.Pixels(length, info.Position);
                                layer.PixelsRgba = new byte[length];
                                for (int a = 3; a < length; a += 4) layer.PixelsRgba[a] = 255;
                                if (layer.Mask != null)
                                {
                                    int maskLength = checked(layer.Mask.Width * layer.Mask.Height);
                                    state.Pixels(maskLength, info.Position);
                                    layer.Mask.Pixels = new byte[maskLength];
                                }
                            }
                            for (int c = 0; c < record.Channels.Length; c++)
                            {
                                var channel = info.Slice(record.Lengths[c]);
                                if (state.Unsupported) continue;
                                if (record.Channels[c] == -2)
                                {
                                    // An empty mask rectangle has no samples; some producers then omit even the compression field.
                                    if (channel.Remaining == 0 && layer.Mask.Pixels.Length == 0) continue;
                                    DecodeChannel(channel, layer.Mask.Width, layer.Mask.Height, layer.Mask.Pixels, 0, 1, state);
                                }
                                else
                                {
                                    int component = record.Channels[c] == -1 ? 3 : record.Channels[c];
                                    DecodeChannel(channel, layer.Width, layer.Height, layer.PixelsRgba, component, 4, state);
                                }
                            }
                        }
                        if (info.Remaining > 1) state.Preserve("LayerInfoTail", "Unrecognized layer-info bytes retained.", info.Position, info.Remaining);
                        else if (info.Remaining == 1) info.Zeros(1);
                    }
                    var mask = layerMask.Section();
                    state.Metadata(mask.Remaining, mask.Position);
                    if (mask.Remaining != 0) state.Preserve("GlobalMask", "Global layer mask is unsupported.", mask.Position, mask.Remaining);
                    ParseTags(layerMask, null, state);
                }
                // PSD records are bottom-to-top; public DTOs are top-to-bottom.
                document.Layers = BuildTree(records, state);
                if (channels == 4 && !mergedAlpha)
                    state.Preserve("ExtraAlpha", "Fourth channel is not explicitly merged transparency; extra alpha is preserved unchanged.", 12);
                if (channels == 3 && mergedAlpha)
                    throw new PsdFormatException("Merged transparency flag requires a fourth merged channel.", 12);
                if (r.Remaining < 2)
                    state.Preserve("NoComposite", "Merged image is missing; no appearance claim is possible.", r.Position);
                if (!state.Unsupported)
                {
                    long canvasBytes = (long)document.Width * document.Height * 4;
                    state.Pixels(canvasBytes * 2, r.Position);
                    byte[] merged = new byte[(int)canvasBytes];
                    for (int a = 3; a < merged.Length; a += 4) merged[a] = 255;
                    int compositeOffset = r.Position;
                    DecodeComposite(r, document.Width, document.Height, channels, merged, state);
                    if (!state.Unsupported)
                    {
                        byte[] expected = Matte(ReferenceComposite(document), true);
                        int worst = 0;
                        for (int i = 0; i < merged.Length; i++)
                        {
                            // RGB3 merged result is opaque white-backed; alpha does not represent document transparency.
                            if ((i & 3) == 3 && channels == 3) continue;
                            worst = Math.Max(worst, Math.Abs(merged[i] - expected[i]));
                        }
                        if (worst > 1)
                        {
                            // Plain normal stacks have one well-defined result, so a difference means a stale image or a
                            // misread. Blend modes, clipping and masks are composited by YoluPainter's own formulas, which
                            // are not measured against Photoshop's; there a difference is reported but does not block editing.
                            if (UsesCompositingFeatures(document.Layers))
                                state.Note("CompositeDiffers", "Stored composite differs by up to " + worst + "/255 from YoluPainter's compositing of the blend modes, clipping or masks; the layers are editable, but the result may look different from the application that wrote the file.", compositeOffset);
                            else
                                state.Preserve("CompositeMismatch", "Stored composite differs from supported sample-space compositing; edit/export blocked.", compositeOffset);
                        }
                    }
                }
                return Finish(bytes, document, state);
            }
            catch (PsdFormatException e) { return Rejected(bytes, "MalformedOrLimit", e.Message, e.Offset); }
            catch (OverflowException) { return Rejected(bytes, "Overflow", "PSD length or pixel count overflow.", 0); }
            catch (DecoderFallbackException) { return Rejected(bytes, "Unicode", "Invalid UTF-16 layer name.", 0); }
        }

        private static PsdReadResult Finish(byte[] original, PsdDocument document, ParseState state)
        {
            state.FlushNotCarried();
            return new PsdReadResult(state.Unsupported ? PsdCompatibilityMode.PreserveOnly : PsdCompatibilityMode.EditableRaster,
                state.Unsupported ? null : document, original, state.Diagnostics);
        }

        private static void ParseResources(PsdReader r, ParseState state)
        {
            state.Metadata(r.Remaining, r.Position);
            while (r.Remaining > 0)
            {
                int start = r.Position;
                if (r.Key() != "8BIM") throw new PsdFormatException("Invalid image resource signature.", start);
                int id = r.U16();
                int nameLength = r.U8(); r.Skip(nameLength); if (((nameLength + 1) & 1) != 0) r.Zeros(1);
                var body = r.Section();
                int length = body.Remaining;
                if ((length & 1) != 0) r.Zeros(1);
                if (id == 1039)
                {
                    string description = IccDescription(body);
                    if (description == SrgbDescription) state.NotCarried("Image resource 1039 (ICC profile '" + SrgbDescription + "', the sRGB this codec assumes)", start, r.Position - start);
                    else state.Preserve("ImageResource", "Image resource 1039 (ICC profile " + (description == null ? "that cannot be identified" : "'" + description + "'") + ") is not sRGB IEC61966-2.1; colour interpretation is not implemented.", start, r.Position - start);
                    continue;
                }
                if (id == 1064)
                {
                    double ratio = body.Remaining == 12 ? PixelAspectRatio(body) : double.NaN;
                    if (ratio == 1) state.NotCarried("Image resource 1064 (pixel aspect ratio 1:1)", start, r.Position - start);
                    else state.Preserve("ImageResource", "Image resource 1064 (pixel aspect ratio " + ratio + ") changes how the pixels are interpreted.", start, r.Position - start);
                    continue;
                }
                string what = NonRenderingResource(id);
                if (what != null) state.NotCarried("Image resource " + id + " (" + what + ")", start, r.Position - start);
                else state.Preserve("ImageResource", "Image resource " + id + " retained; metadata/color interpretation is not implemented.", start, r.Position - start);
            }
        }

        /// <summary>Image resources that do not change how the pixels render or are interpreted on screen: print settings,
        /// guides, slices, thumbnails, metadata, saved paths, layer comps and selection state. The global light angle and
        /// altitude only affect layer effects, which are PreserveOnly themselves.</summary>
        private static string NonRenderingResource(int id)
        {
            switch (id)
            {
                case 1005: return "resolution / print size";
                case 1010: return "background colour swatch";
                case 1011: return "print flags";
                case 1024: return "target layer";
                case 1025: return "work path";
                case 1026: return "layer link groups";
                case 1028: return "IPTC metadata";
                case 1032: return "grid and guides";
                case 1033: case 1036: return "thumbnail";
                case 1034: return "copyright flag";
                case 1035: return "URL";
                case 1037: return "global light angle";
                case 1044: return "layer ID seed";
                case 1049: return "global light altitude";
                case 1050: return "slices";
                case 1054: return "URL list";
                case 1057: return "version info";
                case 1058: case 1059: return "EXIF metadata";
                case 1060: return "XMP metadata";
                case 1061: return "caption digest";
                case 1062: return "print scale";
                case 1065: return "layer comps";
                case 1069: return "selected layers";
                case 1072: return "layer group enabled IDs";
                case 1082: return "print information";
                case 1083: return "print style";
                case 1088: return "path selection state";
                case 7000: case 7001: return "variables / data sets";
                case 8000: return "Lightroom workflow";
                case 10000: return "print flags information";
                default: return id >= 2000 && id <= 2997 ? "saved path" : null;
            }
        }

        /// <summary>The profile description YoluPainter accepts as its own encoded-sRGB assumption (the IEC 61966-2.1 profile
        /// Photoshop and Windows embed).</summary>
        private const string SrgbDescription = "sRGB IEC61966-2.1";

        /// <summary>Description ('desc' tag, v2 textDescriptionType or v4 multiLocalizedUnicodeType) of an RGB display/input
        /// ICC profile, or null when the profile is not an RGB profile or cannot be read. Never throws.</summary>
        private static string IccDescription(PsdReader body)
        {
            try
            {
                byte[] d = body.Data; int o = body.Position, n = body.Remaining;
                Func<int, uint> u32 = p => (uint)(d[o + p] << 24 | d[o + p + 1] << 16 | d[o + p + 2] << 8 | d[o + p + 3]);
                Func<int, string> sig = p => Encoding.ASCII.GetString(d, o + p, 4);
                if (n < 132 || u32(0) > n || sig(36) != "acsp" || sig(16) != "RGB ") return null;
                uint count = u32(128);
                if (count > 1000 || 132 + count * 12 > n) return null;
                for (int t = 0; t < count; t++)
                {
                    int entry = 132 + t * 12;
                    if (sig(entry) != "desc") continue;
                    long at = u32(entry + 4), size = u32(entry + 8);
                    if (at + size > n || size < 12) return null;
                    int p = (int)at;
                    string type = sig(p);
                    if (type == "desc")
                    {
                        long length = u32(p + 8);
                        if (length < 1 || 12 + length > size) return null;
                        return Encoding.ASCII.GetString(d, o + p + 12, (int)length).TrimEnd('\0');
                    }
                    if (type == "mluc")
                    {
                        long records = u32(p + 8), recordSize = u32(p + 12);
                        if (records < 1 || recordSize < 12 || 16 + recordSize > size) return null;
                        long length = u32(p + 16 + 4), offset = u32(p + 16 + 8);
                        if (offset + length > size || (length & 1) != 0) return null;
                        return new UnicodeEncoding(true, false, false).GetString(d, o + p + (int)offset, (int)length).TrimEnd('\0');
                    }
                    return null;
                }
                return null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Pixel aspect ratio resource: version (4 bytes) and the ratio as a big-endian double.</summary>
        private static double PixelAspectRatio(PsdReader body)
        {
            body.U32();
            var bytes = new byte[8];
            for (int i = 7; i >= 0; i--) bytes[i] = body.U8();
            return BitConverter.IsLittleEndian ? BitConverter.ToDouble(bytes, 0) : BitConverter.ToDouble(new[] { bytes[7], bytes[6], bytes[5], bytes[4], bytes[3], bytes[2], bytes[1], bytes[0] }, 0);
        }

        private static Record ReadRecord(PsdReader r, ParseState state)
        {
            int start = r.Position;
            int top = r.I32(), left = r.I32(), bottom = r.I32(), right = r.I32();
            long width = (long)right - left, height = (long)bottom - top;
            if (width < 0 || height < 0 || width > state.Limits.MaxDimension || height > state.Limits.MaxDimension ||
                width * height > state.Limits.MaxCanvasPixels)
                throw new PsdFormatException("Invalid or over-budget layer rectangle.", start);
            var layer = new PsdRasterLayer { Top = top, Left = left, Width = (int)width, Height = (int)height };
            int count = r.U16();
            if (count < 1 || count > 56) throw new PsdFormatException("Invalid layer channel count.", r.Position - 2);
            var record = new Record { Layer = layer, Channels = new short[count], Lengths = new int[count], Offset = start };
            var channels = new HashSet<short>();
            for (int c = 0; c < count; c++)
            {
                short id = r.I16(); uint length = r.U32();
                if (length > int.MaxValue) throw new PsdFormatException("Channel length overflow.", r.Position - 4);
                if (!channels.Add(id)) throw new PsdFormatException("Duplicate layer channel.", r.Position - 6);
                record.Channels[c] = id; record.Lengths[c] = (int)length;
                if (id == -3) state.Preserve("RealUserMask", "Separate user mask channel (-3) of a layer that also has a vector mask is not represented.", r.Position - 6, 6);
                else if (id < -2 || id > 2) state.Preserve("LayerChannel", "Non-RGB layer channel " + id + " is unsupported.", r.Position - 6, 6);
            }
            if (r.Key() != "8BIM") throw new PsdFormatException("Invalid layer blend signature.", r.Position - 4);
            int blendOffset = r.Position;
            record.BlendKey = r.Key();
            layer.Opacity = r.U8();
            record.Clipping = r.U8();
            record.Flags = r.U8();
            int flagsOffset = r.Position - 1;
            layer.Visible = (record.Flags & 2) == 0; // PSD bit 1 means hidden (despite ambiguous wording in published table).
            // Bit 0 is the transparency lock: editing state, not rendering.
            if ((record.Flags & 1) != 0) state.NotCarried("Transparency lock (layer flag bit 0)", flagsOffset, 1);
            r.Zeros(1);
            var extra = r.Section();
            state.Metadata(extra.Remaining, extra.Position);
            ParseMask(extra.Section(), layer, state);
            if (channels.Contains(-2) && layer.Mask == null) throw new PsdFormatException("Layer mask channel without layer mask data.", start);
            if (layer.Mask != null && !channels.Contains(-2) && layer.Mask.Width * (long)layer.Mask.Height != 0)
                throw new PsdFormatException("Layer mask data without its mask channel.", start);
            var ranges = extra.Section();
            record.NeutralRanges = NeutralBlendRanges(ranges);
            int nameLength = extra.U8();
            extra.Need(nameLength);
            bool ascii = true;
            var chars = new char[nameLength];
            for (int i = 0; i < nameLength; i++) { int b = extra.U8(); chars[i] = (char)b; if (b > 127) ascii = false; }
            layer.Name = new string(chars);
            extra.Zeros((4 - ((nameLength + 1) % 4)) % 4);
            bool hasUnicode = ParseTags(extra, record, state);
            if (layer.Name.Length > state.Limits.MaxNameCodeUnits) throw new PsdFormatException("Layer name limit exceeded.", start);

            record.Kind = record.SectionType == 3 ? RecordKind.Divider : record.SectionType == 1 || record.SectionType == 2 ? RecordKind.Folder : RecordKind.Raster;
            if (record.Kind == RecordKind.Divider)
            {
                // The divider only marks where a folder's contents begin. Its blend mode, opacity, visibility, clipping
                // and name do not render in Photoshop (the folder record carries them) and are not kept.
                if (width != 0 || height != 0) state.Preserve("DividerPixels", "A bounding section divider with pixels is not represented.", start, 16);
                if (layer.Mask != null) state.Preserve("DividerMask", "A bounding section divider with a mask is not represented.", start);
                if ((record.Flags & ~(1 | 2 | 8 | 16)) != 0) state.Preserve("LayerFlags", "Unknown flags on a section divider are not editable.", flagsOffset, 1);
                if (!record.NeutralRanges) state.Preserve("BlendIf", "Blend-If ranges on a section divider are unsupported.", ranges.Position, ranges.Remaining);
                if (channels.Contains(-2) || channels.Contains(-3)) state.Preserve("DividerMask", "A bounding section divider with mask channels is not represented.", start);
                return record;
            }
            if (!record.NeutralRanges) state.Preserve("BlendIf", "Blend-If ranges other than the neutral default are unsupported.", ranges.Position, ranges.Remaining);
            if (record.Clipping == 1) layer.Clipping = true;
            else if (record.Clipping != 0) state.Preserve("Clipping", "Unknown clipping value " + record.Clipping + ".", flagsOffset - 1, 1);
            if (layer.Name.IndexOf('\0') >= 0) state.Preserve("NameNull", "Embedded null in layer name.", start);
            if (!hasUnicode && !ascii) state.Preserve("LegacyNameEncoding", "Non-ASCII Pascal layer name has no Unicode equivalent.", start);
            LayerBlendMode mode;
            if (record.Kind == RecordKind.Folder)
            {
                layer.Children = new List<PsdRasterLayer>();
                layer.PixelsRgba = new byte[0];
                // Open/closed is Layers-panel view state; a scene group only matters to the animation timeline.
                if (record.SectionType == 2) state.NotCarried("Closed folder state (lsct type 2)", start, 0);
                if (record.SectionSubType != 0) state.NotCarried("Scene group folder (lsct sub type " + record.SectionSubType + ")", start, 0);
                if (width != 0 || height != 0) state.Preserve("GroupPixels", "A folder record with its own pixels is not represented.", start, 16);
                // Bit 4 (pixel data irrelevant) is what a folder is; bit 3 says bit 4 is meaningful.
                if ((record.Flags & ~(1 | 2 | 8 | 16)) != 0) state.Preserve("LayerFlags", "Unknown folder flags are not editable.", flagsOffset, 1);
                // The folder's blend mode is the one in its section divider setting; Photoshop writes "norm" in the
                // record of a pass-through folder.
                string key = record.SectionKey ?? record.BlendKey;
                if (key == "pass") layer.BlendMode = LayerBlendMode.PassThrough;
                else if (TryGetBlendMode(key, out mode)) layer.BlendMode = mode;
                else state.Preserve("BlendMode", "Unsupported folder blend mode: " + key, blendOffset, 4);
                if (record.SectionKey != null && record.BlendKey != record.SectionKey && !(record.SectionKey == "pass" && record.BlendKey == "norm"))
                    state.Preserve("GroupBlend", "Folder record blend '" + record.BlendKey + "' contradicts its section divider blend '" + record.SectionKey + "'.", blendOffset, 4);
                return record;
            }
            if (width == 0 || height == 0) state.Preserve("EmptyLayer", "Empty or non-raster layer is retained without editing.", start, 16);
            if (!channels.Contains(0) || !channels.Contains(1) || !channels.Contains(2))
                state.Preserve("LayerChannels", "Layer does not contain all RGB channels.", start);
            if (TryGetBlendMode(record.BlendKey, out mode)) layer.BlendMode = mode;
            else if (record.BlendKey == "pass") state.Preserve("BlendMode", "Pass-through blend (pass) is only meaningful for groups.", blendOffset, 4);
            else state.Preserve("BlendMode", "Unsupported blend mode: " + record.BlendKey, blendOffset, 4);
            // Bit 3 only says that bit 4 is meaningful; bit 4 clear (pixel data relevant to appearance) is what this codec assumes.
            if ((record.Flags & ~(1 | 2 | 8)) != 0) state.Preserve("LayerFlags", "Irrelevant-pixel-data or unknown layer flags are not editable.", flagsOffset, 1);
            return record;
        }

        /// <summary>Nests the records (bottom to top) into folders: a bounding divider opens a folder's contents, the folder
        /// record closes them. Returns the top level, top to bottom. Unbalanced dividers and folders, and nesting beyond the
        /// depth budget, are malformed.</summary>
        private static List<PsdRasterLayer> BuildTree(List<Record> records, ParseState state)
        {
            // A setting that cannot be interpreted leaves the structure unknown: the file is PreserveOnly (already
            // diagnosed), not malformed, and no tree is built from a guess.
            foreach (var record in records)
                if (record.UnknownSection) { var flat = new List<PsdRasterLayer>(); for (int i = records.Count - 1; i >= 0; i--) flat.Add(records[i].Layer); return flat; }
            var open = new Stack<KeyValuePair<List<PsdRasterLayer>, int>>();
            var current = new List<PsdRasterLayer>();
            foreach (var record in records)
            {
                switch (record.Kind)
                {
                    case RecordKind.Divider:
                        if (open.Count >= state.Limits.MaxGroupDepth) throw new PsdFormatException("Folder nesting exceeds the depth budget of " + state.Limits.MaxGroupDepth + ".", record.Offset);
                        open.Push(new KeyValuePair<List<PsdRasterLayer>, int>(current, record.Layer.Id));
                        current = new List<PsdRasterLayer>();
                        break;
                    case RecordKind.Folder:
                        if (open.Count == 0) throw new PsdFormatException("Folder record without a bounding section divider below it.", record.Offset);
                        var group = record.Layer;
                        current.Reverse(); group.Children = current;
                        var outer = open.Pop(); group.DividerId = outer.Value; current = outer.Key;
                        current.Add(group);
                        break;
                    default: current.Add(record.Layer); break;
                }
            }
            if (open.Count != 0) throw new PsdFormatException("Bounding section divider without a folder record above it.", records[records.Count - 1].Offset);
            current.Reverse();
            return current;
        }

        /// <summary>Layer mask / adjustment layer data. Only a raster user mask with an optional density is represented;
        /// structural errors are rejected, unrepresented rendering parameters make the document PreserveOnly.</summary>
        private static void ParseMask(PsdReader m, PsdRasterLayer layer, ParseState state)
        {
            if (m.Remaining == 0) return;
            int start = m.Position;
            if (m.Remaining < 18) throw new PsdFormatException("Layer mask data is shorter than its fixed fields.", start);
            int top = m.I32(), left = m.I32(), bottom = m.I32(), right = m.I32();
            long width = (long)right - left, height = (long)bottom - top;
            if (width < 0 || height < 0 || width > state.Limits.MaxDimension || height > state.Limits.MaxDimension ||
                width * height > state.Limits.MaxCanvasPixels)
                throw new PsdFormatException("Invalid or over-budget layer mask rectangle.", start);
            int color = m.U8();
            if (color != 0 && color != 255) throw new PsdFormatException("Layer mask default color must be 0 or 255.", m.Position - 1);
            int flags = m.U8();
            var mask = new PsdLayerMask { Top = top, Left = left, Width = (int)width, Height = (int)height, DefaultColor = (byte)color, Enabled = (flags & 2) == 0 };
            layer.Mask = mask;
            if ((flags & 1) != 0) state.Preserve("MaskPosition", "Layer mask positioned relative to the layer (flag bit 0) is not represented.", m.Position - 1, 1);
            if ((flags & 4) != 0) state.Preserve("MaskInvert", "Obsolete 'invert layer mask when blending' flag is not represented.", m.Position - 1, 1);
            if ((flags & 8) != 0) state.Preserve("MaskFromRender", "Layer mask rendered from other data (flag bit 3) is not represented.", m.Position - 1, 1);
            if ((flags & ~31) != 0) state.Preserve("MaskFlags", "Unknown layer mask flags " + flags + ".", m.Position - 1, 1);
            if ((flags & 16) != 0)
            {
                int parameters = m.U8();
                if ((parameters & 1) != 0) mask.Density = m.U8();
                if ((parameters & 2) != 0) { m.Skip(8); state.Preserve("MaskFeather", "User mask feather is not represented.", m.Position - 8, 8); }
                if ((parameters & 4) != 0) { m.Skip(1); state.Preserve("VectorMaskDensity", "Vector mask density is not represented.", m.Position - 1, 1); }
                if ((parameters & 8) != 0) { m.Skip(8); state.Preserve("VectorMaskFeather", "Vector mask feather is not represented.", m.Position - 8, 8); }
                if ((parameters & ~15) != 0)
                {
                    // Unknown parameters have unknown sizes: the rest of the record cannot be interpreted.
                    state.Preserve("MaskParameters", "Unknown layer mask parameters " + parameters + ".", start, m.End - start);
                    m.Skip(m.Remaining); return;
                }
            }
            if (m.Remaining >= 18)
            {
                // "Real" flags, background and rectangle: present when a layer has both a user mask and a vector mask.
                state.Preserve("UserAndVectorMask", "Combined user and vector mask (real mask fields) is not represented.", m.Position, 18);
                m.Skip(18);
            }
            // What is left is padding; anything non-zero there is a field this codec does not know.
            for (int i = m.Remaining; i > 0; i--)
                if (m.U8() != 0) { state.Preserve("MaskTail", "Unrecognized layer mask bytes.", m.Position - 1, m.Remaining + 1); m.Skip(m.Remaining); break; }
        }

        /// <summary>Blend-If ranges that change nothing: every range is black 0..0, white 255..255 (bytes 00 00 FF FF), as
        /// Photoshop writes for layers without Blend If.</summary>
        private static bool NeutralBlendRanges(PsdReader ranges)
        {
            int n = ranges.Remaining;
            if (n == 0) return true;
            if (n % 8 != 0) return false;
            for (int i = 0; i < n; i += 4)
            {
                int p = ranges.Position + i;
                if (ranges.Data[p] != 0 || ranges.Data[p + 1] != 0 || ranges.Data[p + 2] != 255 || ranges.Data[p + 3] != 255) return false;
            }
            return true;
        }

        private static bool ParseTags(PsdReader r, Record record, ParseState state)
        {
            var layer = record == null ? null : record.Layer;
            bool unicode = false, idSeen = false;
            var seen = new HashSet<string>();
            while (r.Remaining > 0)
            {
                int start = r.Position;
                if (r.Remaining < 12) { state.Preserve("UnknownTail", "Unparsed additional metadata tail retained.", start, r.Remaining); r.Skip(r.Remaining); break; }
                string signature = r.Key();
                if (signature != "8BIM" && signature != "8B64") throw new PsdFormatException("Invalid tagged block signature.", start);
                string key = r.Key();
                var body = r.Section();
                int size = body.Remaining;
                if (layer == null) state.Metadata(size + 12, start);
                if ((size & 1) != 0) r.Zeros(1);
                int blockLength = r.Position - start;
                if (signature != "8BIM" || layer == null)
                {
                    string resource = layer == null && signature == "8BIM" ? NonRenderingDocumentBlock(key) : null;
                    if (resource != null) state.NotCarried(resource, start, blockLength);
                    else state.Preserve("TaggedBlock", DescribeTag(key) ?? "Unsupported " + (layer == null ? "document" : "layer") + " metadata: " + key, start, blockLength);
                    continue;
                }
                switch (key)
                {
                    case "lyid":
                        if (idSeen || size != 4) { state.Preserve("LayerIdentity", "Duplicate or nonstandard layer identity block.", start, blockLength); continue; }
                        layer.Id = body.I32(); idSeen = true;
                        break;
                    case "luni":
                    {
                        if (unicode) { state.Preserve("UnicodeName", "Duplicate Unicode name record.", start, blockLength); continue; }
                        uint units = body.U32();
                        if (units > state.Limits.MaxNameCodeUnits) throw new PsdFormatException("Layer name limit exceeded.", start);
                        int byteCount = checked((int)units * 2);
                        body.Need(byteCount);
                        layer.Name = Utf16.GetString(body.Data, body.Position, byteCount);
                        body.Skip(byteCount);
                        // Both plain code-unit strings and terminal/padding zeros are used by producers.
                        if (body.Remaining > 3) state.Preserve("UnicodeNameTail", "Unrecognized Unicode name suffix.", body.Position, body.Remaining);
                        else body.Zeros(body.Remaining);
                        if (layer.Name.IndexOf('\0') >= 0) state.Preserve("UnicodeNameNull", "Embedded null in layer name.", start);
                        unicode = true;
                        break;
                    }
                    // Settings whose default value changes nothing are accepted only at that default (and are not
                    // written back, since absence means the same); any other value keeps the whole file PreserveOnly.
                    case "iOpa":
                        if (!seen.Add(key) || !DefaultByteSetting(body, 255))
                            state.Preserve("FillOpacity", "Fill opacity (iOpa) other than 100% is not represented: the native layer has one opacity.", start, blockLength);
                        break;
                    case "clbl":
                        if (!seen.Add(key) || !DefaultByteSetting(body, 1))
                            state.Preserve("ClippedBlend", "Clipped layers not blended as a group (clbl) are not represented.", start, blockLength);
                        break;
                    case "infx":
                        if (!seen.Add(key) || !DefaultByteSetting(body, 0))
                            state.Preserve("InteriorBlend", "Blend interior effects as group (infx) is not represented.", start, blockLength);
                        break;
                    case "knko":
                        if (!seen.Add(key) || !DefaultByteSetting(body, 0))
                            state.Preserve("Knockout", "Knockout (knko) is not represented.", start, blockLength);
                        break;
                    // Editing and Layers-panel state that does not render: accepted and reported, not carried.
                    case "lspf":
                        if (size != 4) state.Preserve("LayerLocks", "Nonstandard layer lock block (lspf).", start, blockLength);
                        else if (body.U32() != 0) state.NotCarried("Layer locks (lspf)", start, blockLength);
                        break;
                    case "lclr":
                        if (size != 8) state.Preserve("SheetColor", "Nonstandard layer colour label block (lclr).", start, blockLength);
                        else if (body.U32() != 0 || body.U32() != 0) state.NotCarried("Layer colour label (lclr)", start, blockLength);
                        break;
                    case "lnsr": state.NotCarried("Layer name source (lnsr)", start, blockLength); break;
                    case "shmd": state.NotCarried("Layer metadata (shmd)", start, blockLength); break;
                    case "fxrp": state.NotCarried("Layer reference point (fxrp)", start, blockLength); break;
                    case "lyvr": state.NotCarried("Layer version (lyvr)", start, blockLength); break;
                    case "tsly":
                        if (!seen.Add(key) || !DefaultByteSetting(body, 1))
                            state.Preserve("TransparencyShapes", "Transparency shapes layer (tsly) off is not represented.", start, blockLength);
                        break;
                    case "brst":
                        if (size != 0) state.Preserve("ChannelRestrictions", "Channel blending restrictions (brst) are not represented.", start, blockLength);
                        break;
                    case "lsct": case "lsdk":
                        // Section divider setting: type, then optionally "8BIM" + blend key, then optionally a sub type.
                        // lsdk is the same structure under another key; rewrites use lsct.
                        if (!seen.Add("lsct")) { record.UnknownSection = true; state.Preserve("SectionDivider", "More than one section divider setting (" + key + ").", start, blockLength); break; }
                        if (size != 4 && size != 12 && size != 16) { record.UnknownSection = true; state.Preserve("SectionDivider", "Unrecognized section divider (" + key + ") layout of " + size + " bytes.", start, blockLength); break; }
                        uint type = body.U32();
                        if (type > 3) { record.UnknownSection = true; state.Preserve("SectionDivider", "Unknown section divider (" + key + ") type " + type + ".", start, blockLength); break; }
                        record.SectionType = (int)type;
                        if (size >= 12)
                        {
                            if (body.Key() != "8BIM") throw new PsdFormatException("Invalid section divider blend signature.", body.Position - 4);
                            record.SectionKey = body.Key();
                        }
                        if (size == 16) record.SectionSubType = body.I32();
                        break;
                    default:
                        state.Preserve("TaggedBlock", DescribeTag(key) ?? "Unsupported layer metadata: " + key, start, blockLength);
                        break;
                }
            }
            return unicode;
        }

        /// <summary>Document-level tagged blocks that do not render by themselves: pattern and text-engine libraries (used only by
        /// fill/effect/text layers, which are PreserveOnly) and the smart-filter mask defaults.</summary>
        private static string NonRenderingDocumentBlock(string key)
        {
            switch (key)
            {
                case "Patt": case "Pat2": case "Pat3": return "Pattern library (" + key + ")";
                case "Txt2": return "Text engine data (Txt2)";
                case "FMsk": return "Smart filter mask defaults (FMsk)";
                default: return null;
            }
        }

        /// <summary>A one-byte setting padded to four bytes (iOpa, clbl, infx, knko, tsly) that holds <paramref name="value"/>.</summary>
        private static bool DefaultByteSetting(PsdReader body, int value)
        {
            if (body.Remaining != 4) return false;
            return body.U8() == value && body.U8() == 0 && body.U8() == 0 && body.U8() == 0;
        }

        private static void DecodeChannel(PsdReader r, int width, int height, byte[] output, int component, int stride, ParseState state)
        {
            int compression = r.U16();
            if (compression == 0)
            {
                int count = checked(width * height);
                if (r.Remaining != count) throw new PsdFormatException("Raw layer channel length does not match its rectangle.", r.Position);
                for (int i = 0; i < count; i++) output[i * stride + component] = r.U8();
            }
            else if (compression == 1)
            {
                var table = r.Slice(checked(height * 2));
                for (int y = 0; y < height; y++) DecodePackBits(r.Slice(table.U16()), width, output, y * width * stride + component, stride);
                if (r.Remaining != 0) throw new PsdFormatException("Trailing RLE layer channel bytes.", r.Position);
            }
            else state.Preserve("Compression", "ZIP or unknown channel compression " + compression + " is retained without decoding.", r.Position - 2);
        }

        private static void DecodeComposite(PsdReader r, int width, int height, int channels, byte[] rgba, ParseState state)
        {
            int compression = r.U16();
            if (compression == 0)
            {
                int pixels = checked(width * height);
                if (r.Remaining != checked(pixels * channels)) throw new PsdFormatException("Merged raw channel length mismatch.", r.Position);
                for (int c = 0; c < channels; c++) for (int i = 0; i < pixels; i++) rgba[i * 4 + c] = r.U8();
            }
            else if (compression == 1)
            {
                var table = r.Slice(checked(channels * height * 2));
                for (int c = 0; c < channels; c++)
                    for (int y = 0; y < height; y++) DecodePackBits(r.Slice(table.U16()), width, rgba, y * width * 4 + c, 4);
                if (r.Remaining != 0) throw new PsdFormatException("Trailing merged RLE bytes.", r.Position);
            }
            else state.Preserve("CompositeCompression", "ZIP or unknown merged compression is unsupported.", r.Position - 2);
        }

        private static void DecodePackBits(PsdReader row, int width, byte[] output, int offset, int stride)
        {
            int x = 0;
            while (row.Remaining > 0)
            {
                int control = unchecked((sbyte)row.U8());
                if (control == -128) continue;
                int count = control >= 0 ? control + 1 : 1 - control;
                if (count > width - x) throw new PsdFormatException("RLE expands beyond its row width.", row.Position - 1);
                if (control >= 0)
                {
                    row.Need(count);
                    for (int i = 0; i < count; i++) output[offset + (x++) * stride] = row.U8();
                }
                else
                {
                    byte value = row.U8();
                    for (int i = 0; i < count; i++) output[offset + (x++) * stride] = value;
                }
            }
            if (x != width) throw new PsdFormatException("RLE row did not fill its declared width.", row.Position);
        }

        /// <summary>Guarded rewrite of an editable import. Never call Write on a hand-built partial copy of an unsupported import.</summary>
        public static byte[] WriteEdited(PsdReadResult origin, PsdDocument edited, PsdLimits limits = null)
        {
            if (origin == null) throw new ArgumentNullException("origin");
            if (origin.Mode != PsdCompatibilityMode.EditableRaster)
                throw new InvalidOperationException("PreserveOnly or rejected PSD cannot be edited/exported. Keep the unchanged original.");
            return Write(edited, limits);
        }

        /// <summary>One physical layer record in storage order (bottom to top).</summary>
        private struct Emit
        {
            internal PsdRasterLayer Layer;
            internal RecordKind Kind;
            internal Emit(PsdRasterLayer layer, RecordKind kind) { Layer = layer; Kind = kind; }
        }
        private const string DividerName = "</Layer group>";

        /// <summary>Folders are written as Photoshop lays them out: the bounding divider, the contents (bottom to top), then
        /// the folder record.</summary>
        private static void Flatten(List<PsdRasterLayer> topDown, List<Emit> output)
        {
            for (int i = topDown.Count - 1; i >= 0; i--)
            {
                var layer = topDown[i];
                if (!layer.IsGroup) { output.Add(new Emit(layer, RecordKind.Raster)); continue; }
                output.Add(new Emit(layer, RecordKind.Divider));
                Flatten(layer.Children, output);
                output.Add(new Emit(layer, RecordKind.Folder));
            }
        }

        public static byte[] Write(PsdDocument document, PsdLimits limits = null)
        {
            limits = limits ?? new PsdLimits(); limits.Validate();
            List<Emit> records;
            int total = ValidateWrite(document, limits, out records);
            var w = new PsdWriter(total);
            w.Key("8BPS"); w.U16(1); w.Zeros(6); w.U16(4);
            w.U32(document.Height); w.U32(document.Width); w.U16(8); w.U16(3);
            w.U32(0); w.U32(0); // Untagged RGB; no invented profile or unknown metadata.
            int layerMaskLength = w.Position; w.U32(0);
            int infoLength = w.Position; w.U32(0);
            w.U16(-records.Count); // First merged alpha is transparency, not an extra spot/selection channel.
            foreach (var record in records)
            {
                var layer = record.Layer;
                bool raster = record.Kind == RecordKind.Raster, divider = record.Kind == RecordKind.Divider;
                var mask = divider ? null : layer.Mask;
                if (raster) { w.U32(layer.Top); w.U32(layer.Left); w.U32((long)layer.Top + layer.Height); w.U32((long)layer.Left + layer.Width); }
                else w.Zeros(16);
                w.U16(mask != null ? 5 : 4);
                int channelLength = raster ? checked(layer.Width * layer.Height + 2) : 2;
                for (int c = 0; c < 4; c++) { w.U16(c == 3 ? -1 : c); w.U32(channelLength); }
                if (mask != null) { w.U16(-2); w.U32(checked(mask.Width * mask.Height + 2)); }
                w.Key("8BIM");
                if (divider) { w.Key("norm"); w.U8(255); w.U8(0); w.U8(0); }
                else
                {
                    // A pass-through folder has "norm" in its record and "pass" in its section divider setting, as Photoshop writes it.
                    w.Key(layer.BlendMode == LayerBlendMode.PassThrough ? "norm" : BlendKey(layer.BlendMode));
                    w.U8(layer.Opacity); w.U8(layer.Clipping ? 1 : 0); w.U8(layer.Visible ? 0 : 2);
                }
                w.U8(0);
                int extraLength = w.Position; w.U32(0);
                WriteMaskData(w, mask);
                w.U32(0); // No Blend-If ranges: the same as the neutral default.
                string name = divider ? DividerName : layer.Name;
                int nameLength = Math.Min(255, name.Length);
                w.U8(nameLength);
                for (int i = 0; i < nameLength; i++) w.U8(name[i] <= 127 ? name[i] : '?');
                w.Zeros((4 - ((nameLength + 1) % 4)) % 4);
                byte[] unicode = Utf16.GetBytes(name);
                w.Key("8BIM"); w.Key("luni"); w.U32(4 + unicode.Length); w.U32(name.Length); w.Bytes(unicode);
                int id = divider ? layer.DividerId : layer.Id;
                if (id != 0) { w.Key("8BIM"); w.Key("lyid"); w.U32(4); w.U32(id); }
                if (divider) { w.Key("8BIM"); w.Key("lsct"); w.U32(4); w.U32(3); }
                else if (!raster)
                {
                    w.Key("8BIM"); w.Key("lsct"); w.U32(12); w.U32(1); w.Key("8BIM");
                    w.Key(layer.BlendMode == LayerBlendMode.PassThrough ? "pass" : BlendKey(layer.BlendMode));
                }
                w.Patch32(extraLength, w.Position - extraLength - 4);
            }
            foreach (var record in records)
            {
                var layer = record.Layer;
                if (record.Kind != RecordKind.Raster) { for (int c = 0; c < 4; c++) w.U16(0); }
                else
                    for (int c = 0; c < 4; c++)
                    {
                        w.U16(0);
                        for (int i = c; i < layer.PixelsRgba.Length; i += 4) w.U8(layer.PixelsRgba[i]);
                    }
                if (record.Kind != RecordKind.Divider && layer.Mask != null) { w.U16(0); w.Bytes(layer.Mask.Pixels); }
            }
            if (((w.Position - infoLength - 4) & 1) != 0) w.Zeros(1);
            w.Patch32(infoLength, w.Position - infoLength - 4);
            w.U32(0); // Global mask.
            w.Patch32(layerMaskLength, w.Position - layerMaskLength - 4);
            w.U16(0);
            // The caller's composite is copied, never matted in place (DTOs are not mutated while writing).
            byte[] merged = document.CompositeRgba != null ? Matte(document.CompositeRgba, false) : Matte(ReferenceComposite(document), true);
            for (int c = 0; c < 4; c++) for (int i = c; i < merged.Length; i += 4) w.U8(merged[i]);
            if (w.Position != total) throw new InvalidOperationException("PSD writer final length mismatch.");
            return w.Data;
        }

        /// <summary>Layer mask data is always 20 bytes: rectangle, default colour, flags, then either the user mask density
        /// parameter (flag bit 4, parameter bit 0) or two bytes of padding.</summary>
        private const int MaskDataBytes = 20;
        private static void WriteMaskData(PsdWriter w, PsdLayerMask mask)
        {
            if (mask == null) { w.U32(0); return; }
            w.U32(MaskDataBytes);
            w.U32(mask.Top); w.U32(mask.Left); w.U32((long)mask.Top + mask.Height); w.U32((long)mask.Left + mask.Width);
            w.U8(mask.DefaultColor);
            bool density = mask.Density != 255;
            w.U8((mask.Enabled ? 0 : 2) | (density ? 16 : 0));
            if (density) { w.U8(1); w.U8(mask.Density); }
            else w.Zeros(2);
        }

        private static int ValidateWrite(PsdDocument document, PsdLimits limits, out List<Emit> records)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (document.Width < 1 || document.Height < 1 || document.Width > limits.MaxDimension || document.Height > limits.MaxDimension ||
                (long)document.Width * document.Height > limits.MaxCanvasPixels) throw new ArgumentException("Canvas exceeds PSD budget.");
            if (document.Layers == null || document.Layers.Count < 1) throw new ArgumentException("PSD requires one or more layers.");
            long canvasBytes = (long)document.Width * document.Height * 4;
            if (document.CompositeRgba != null && document.CompositeRgba.LongLength != canvasBytes)
                throw new ArgumentException("The merged composite must be exact straight RGBA8 for the canvas.");
            long pixels = canvasBytes, metadata = 0, infoSize = 2;
            var ids = new HashSet<int>();
            CheckTree(document.Layers, 0, limits, ids, new HashSet<PsdRasterLayer>());
            records = new List<Emit>();
            Flatten(document.Layers, records);
            if (records.Count > limits.MaxLayers) throw new ArgumentException("PSD layer records (including folder dividers) exceed the layer budget.");
            foreach (var record in records)
            {
                var layer = record.Layer;
                bool raster = record.Kind == RecordKind.Raster, divider = record.Kind == RecordKind.Divider;
                string name = divider ? DividerName : layer.Name;
                int pascal = Math.Min(255, name.Length) + 1;
                pascal += (4 - pascal % 4) % 4;
                long id = (divider ? layer.DividerId : layer.Id) != 0 ? 16 : 0;
                long section = divider ? 16 : raster ? 0 : 24;
                long extra = 8 + pascal + 16 + name.Length * 2L + id + section;
                long layerBytes = raster ? (long)layer.Width * layer.Height * 4 : 0;
                long recordBytes = 58, channelData = 8 + layerBytes;
                var mask = divider ? null : layer.Mask;
                if (mask != null) { extra += MaskDataBytes; recordBytes += 6; channelData += 2 + mask.Pixels.LongLength; pixels += mask.Pixels.LongLength; }
                metadata += extra;
                infoSize += recordBytes + extra + channelData;
                pixels += layerBytes;
            }
            if (pixels > limits.MaxDecodedBytes || metadata > limits.MaxMetadataBytes) throw new ArgumentException("PSD pixel or metadata budget exceeded.");
            infoSize += infoSize & 1;
            long total = 26 + 4 + 4 + 4 + 4 + infoSize + 4 + 2 + canvasBytes;
            if (total > limits.MaxOutputBytes || total > int.MaxValue) throw new ArgumentException("PSD output byte budget exceeded.");
            return (int)total;
        }

        private static void CheckTree(List<PsdRasterLayer> topDown, int depth, PsdLimits limits, HashSet<int> ids, HashSet<PsdRasterLayer> seen)
        {
            if (topDown == null) throw new ArgumentException("A group requires a Children list.");
            foreach (var layer in topDown)
            {
                if (layer == null || !seen.Add(layer)) throw new ArgumentException("A layer appears more than once in the PSD tree.");
                if (layer.Id <= 0 || !ids.Add(layer.Id)) throw new ArgumentException("PSD layer IDs must be positive and unique.");
                if (layer.Name == null || layer.Name.Length > limits.MaxNameCodeUnits || layer.Name.IndexOf('\0') >= 0)
                    throw new ArgumentException("Invalid or over-budget Unicode layer name.");
                Utf16.GetByteCount(layer.Name); // Reject unpaired surrogate; never silently replace it.
                CheckMask(layer.Mask, limits);
                if (layer.IsGroup)
                {
                    if (layer.BlendMode != LayerBlendMode.PassThrough && BlendKey(layer.BlendMode) == null) throw new ArgumentException("Blend mode " + layer.BlendMode + " has no PSD folder equivalent.");
                    if (layer.DividerId < 0 || layer.DividerId > 0 && !ids.Add(layer.DividerId)) throw new ArgumentException("Folder divider IDs must be positive and unique, or 0 for none.");
                    if (depth + 1 > limits.MaxGroupDepth) throw new ArgumentException("Folder nesting exceeds the depth budget of " + limits.MaxGroupDepth + ".");
                    CheckTree(layer.Children, depth + 1, limits, ids, seen);
                    continue;
                }
                if (BlendKey(layer.BlendMode) == null) throw new ArgumentException("Blend mode " + layer.BlendMode + " has no PSD raster-layer equivalent.");
                if (layer.Width < 1 || layer.Height < 1 || layer.Width > limits.MaxDimension || layer.Height > limits.MaxDimension ||
                    (long)layer.Width * layer.Height > limits.MaxCanvasPixels) throw new ArgumentException("Layer rectangle exceeds PSD budget.");
                if ((long)layer.Top + layer.Height > int.MaxValue || (long)layer.Left + layer.Width > int.MaxValue)
                    throw new ArgumentException("Layer coordinates overflow PSD bounds.");
                if (layer.PixelsRgba == null || layer.PixelsRgba.LongLength != (long)layer.Width * layer.Height * 4) throw new ArgumentException("Layer requires exact straight RGBA8 pixels.");
            }
        }

        private static void CheckMask(PsdLayerMask mask, PsdLimits limits)
        {
            if (mask == null) return;
            if (mask.Width < 0 || mask.Height < 0 || mask.Width > limits.MaxDimension || mask.Height > limits.MaxDimension ||
                (long)mask.Width * mask.Height > limits.MaxCanvasPixels) throw new ArgumentException("Layer mask rectangle exceeds PSD budget.");
            if ((long)mask.Top + mask.Height > int.MaxValue || (long)mask.Left + mask.Width > int.MaxValue)
                throw new ArgumentException("Layer mask coordinates overflow PSD bounds.");
            if (mask.Pixels == null || mask.Pixels.LongLength != (long)mask.Width * mask.Height) throw new ArgumentException("Layer mask requires one sample per pixel of its rectangle.");
            if (mask.DefaultColor != 0 && mask.DefaultColor != 255) throw new ArgumentException("Layer mask default color must be 0 or 255.");
        }

        /// <summary>True when the stack uses anything beyond plain normal source-over: another blend mode, clipping, a mask or a group.</summary>
        private static bool UsesCompositingFeatures(List<PsdRasterLayer> layers)
        {
            foreach (var layer in layers)
                if (layer.IsGroup || layer.BlendMode != LayerBlendMode.Normal || layer.Clipping || layer.Mask != null) return true;
            return false;
        }

        /// <summary>One step of the stack, as CpuCompositor.StackEntry: an unclipped layer or group, the layers clipped to it,
        /// and for a group the plan of its contents.</summary>
        private sealed class Entry
        {
            internal PsdRasterLayer Base;
            internal readonly List<Entry> Clips = new List<Entry>();
            internal List<Entry> Children = new List<Entry>();
            internal bool PassesThrough { get { return Base.IsGroup && Base.BlendMode == LayerBlendMode.PassThrough && Clips.Count == 0; } }
        }
        /// <summary>CpuCompositor.PlanLevel on the PSD tree: siblings bottom to top; a sibling that is not the bottom one and has
        /// the clipping flag is clipped; an inactive base drops its clipped layers; a hidden or zero-opacity group, or one with
        /// nothing active inside, is dropped.</summary>
        private static List<Entry> Plan(List<PsdRasterLayer> topDown)
        {
            var plan = new List<Entry>();
            var siblings = new List<PsdRasterLayer>(topDown); siblings.Reverse();
            for (int k = 0; k < siblings.Count; k++)
            {
                if (k > 0 && siblings[k].Clipping) continue;
                var entry = MakeEntry(siblings[k]); if (entry == null) continue;
                for (int m = k + 1; m < siblings.Count && siblings[m].Clipping; m++) { var clip = MakeEntry(siblings[m]); if (clip != null) entry.Clips.Add(clip); }
                plan.Add(entry);
            }
            return plan;
        }
        private static Entry MakeEntry(PsdRasterLayer layer)
        {
            if (layer.IsGroup)
            {
                if (!layer.Visible || layer.Opacity == 0) return null;
                var children = Plan(layer.Children);
                return children.Count == 0 ? null : new Entry { Base = layer, Children = children };
            }
            return layer.Visible && layer.Opacity > 0 ? new Entry { Base = layer } : null;
        }
        private static LayerBlendMode ModeOf(PsdRasterLayer layer) { return layer.BlendMode == LayerBlendMode.PassThrough ? LayerBlendMode.Normal : layer.BlendMode; }

        /// <summary>Straight RGBA8 merged image (top-down) with the same per-pixel arithmetic as CpuCompositor.EvaluatePixel
        /// applied to the native document that <c>PsdBridge.Import</c> builds: isolated groups composite their contents from
        /// transparent and blend like a layer, pass-through groups composite onto the backdrop and fade by opacity x mask
        /// (CpuCompositor.Fade), clipped layers and groups mix onto their base keeping its alpha (ClipOnto), and amount =
        /// opacity x mask factor, where the mask hides (255 - value) x density. Rounded once per step as the native compositor
        /// does. Encoded sample space; not Photoshop's own rendering.</summary>
        internal static byte[] ReferenceComposite(PsdDocument document)
        {
            var plan = Plan(document.Layers);
            var rgba = new byte[checked(document.Width * document.Height * 4)];
            for (int y = 0; y < document.Height; y++)
                for (int x = 0; x < document.Width; x++)
                {
                    var result = Evaluate(plan, Rgba32.Transparent, x, y);
                    int p = (y * document.Width + x) * 4;
                    rgba[p] = result.R; rgba[p + 1] = result.G; rgba[p + 2] = result.B; rgba[p + 3] = result.A;
                }
            return rgba;
        }
        private static Rgba32 Evaluate(List<Entry> plan, Rgba32 backdrop, int x, int y)
        {
            Rgba32 result = backdrop;
            foreach (var entry in plan)
            {
                var layer = entry.Base;
                double amount = AmountAt(layer, x, y);
                if (entry.PassesThrough) { result = CpuCompositor.Fade(result, Evaluate(entry.Children, result, x, y), amount); continue; }
                Rgba32 group = layer.IsGroup ? Evaluate(entry.Children, Rgba32.Transparent, x, y) : PixelAt(layer, x, y);
                foreach (var clip in entry.Clips)
                {
                    var c = clip.Base;
                    group = CpuCompositor.ClipOnto(group, c.IsGroup ? Evaluate(clip.Children, Rgba32.Transparent, x, y) : PixelAt(c, x, y), AmountAt(c, x, y), ModeOf(c));
                }
                result = CpuCompositor.Blend(result, group, amount, ModeOf(layer));
            }
            return result;
        }
        private static Rgba32 PixelAt(PsdRasterLayer layer, int x, int y)
        {
            long lx = (long)x - layer.Left, ly = (long)y - layer.Top;
            if (lx < 0 || ly < 0 || lx >= layer.Width || ly >= layer.Height) return Rgba32.Transparent;
            int p = (int)(ly * layer.Width + lx) * 4;
            return new Rgba32(layer.PixelsRgba[p], layer.PixelsRgba[p + 1], layer.PixelsRgba[p + 2], layer.PixelsRgba[p + 3]);
        }
        private static double AmountAt(PsdRasterLayer layer, int x, int y)
        {
            double opacity = layer.Opacity / 255.0;
            var mask = layer.Mask;
            if (mask == null || !mask.Enabled) return opacity;
            // Same expression as RasterMask.Factor with Density = density / 255 and the stored hide = 255 - value.
            return opacity * (1 - mask.Density / 255.0 * ((255 - mask.ValueAt(x, y)) / 255.0));
        }

        /// <summary>White-matted merged RGB (PSD convention) plus the straight alpha as the merged transparency channel.</summary>
        private static byte[] Matte(byte[] straight, bool inPlace)
        {
            var matted = inPlace ? straight : new byte[straight.Length];
            for (int i = 0; i < straight.Length; i += 4)
            {
                double a = straight[i + 3] / 255.0, white = 255 * (1 - a);
                matted[i] = RoundByte(straight[i] * a + white);
                matted[i + 1] = RoundByte(straight[i + 1] * a + white);
                matted[i + 2] = RoundByte(straight[i + 2] * a + white);
                matted[i + 3] = straight[i + 3];
            }
            return matted;
        }
        private static byte RoundByte(double value) { return (byte)Math.Max(0, Math.Min(255, (int)Math.Floor(value + 0.5))); }
    }
}
