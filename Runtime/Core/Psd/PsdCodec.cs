using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Yozolab.YoluPainter.Core.Psd
{
    /// <summary>Conservative PSD v1 RGB8 raster exchange. No third-party dependencies or Unity objects.
    /// Unknown semantics fail closed to whole-file preservation, never a reconstructed partial export.</summary>
    public static class PsdCodec
    {
        private static readonly Encoding Utf16 = new UnicodeEncoding(true, false, true);
        private sealed class Record
        {
            internal PsdRasterLayer Layer;
            internal short[] Channels;
            internal int[] Lengths;
        }
        private sealed class ParseState
        {
            internal readonly PsdLimits Limits;
            internal readonly List<PsdDiagnostic> Diagnostics = new List<PsdDiagnostic>();
            internal bool Unsupported;
            internal long DecodedBytes;
            internal long MetadataBytes;
            internal ParseState(PsdLimits limits) { Limits = limits; }
            internal void Preserve(string code, string text, int offset, int length = 0)
            {
                Unsupported = true;
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
                            if (record.Layer.Id <= 0 || !ids.Add(record.Layer.Id))
                                state.Preserve("LayerIdentity", "Missing, invalid or duplicate layer ID; no automatic identity repair.", info.Position);
                            records.Add(record); document.Layers.Add(record.Layer);
                        }
                        // Parse metadata first. Unsupported documents are never partially exposed as editable DTOs.
                        foreach (var record in records)
                        {
                            if (!state.Unsupported)
                            {
                                int length = checked(record.Layer.Width * record.Layer.Height * 4);
                                state.Pixels(length, info.Position);
                                record.Layer.PixelsRgba = new byte[length];
                                for (int a = 3; a < length; a += 4) record.Layer.PixelsRgba[a] = 255;
                            }
                            for (int c = 0; c < record.Channels.Length; c++)
                            {
                                var channel = info.Slice(record.Lengths[c]);
                                if (!state.Unsupported)
                                {
                                    int component = record.Channels[c] == -1 ? 3 : record.Channels[c];
                                    DecodeChannel(channel, record.Layer.Width, record.Layer.Height, record.Layer.PixelsRgba, component, state);
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
                document.Layers.Reverse();
                if (channels == 4 && !mergedAlpha)
                    state.Preserve("ExtraAlpha", "Fourth channel is not explicitly merged transparency; extra alpha is preserved unchanged.", 12);
                if (channels == 3 && mergedAlpha)
                    throw new PsdFormatException("Merged transparency flag requires a fourth merged channel.", 12);
                if (r.Remaining < 2)
                    state.Preserve("NoComposite", "Merged image is missing; no appearance claim is possible.", r.Position);
                if (!state.Unsupported)
                {
                    long canvasBytes = (long)document.Width * document.Height * 4;
                    state.Pixels(canvasBytes * 2 + (long)document.Width * 32, r.Position);
                    byte[] merged = new byte[(int)canvasBytes];
                    for (int a = 3; a < merged.Length; a += 4) merged[a] = 255;
                    DecodeComposite(r, document.Width, document.Height, channels, merged, state);
                    if (!state.Unsupported)
                    {
                        byte[] expected = CompositeWhiteMatted(document);
                        for (int i = 0; i < merged.Length; i++)
                        {
                            // RGB3 merged result is opaque white-backed; alpha does not represent document transparency.
                            if ((i & 3) == 3 && channels == 3) continue;
                            if (Math.Abs(merged[i] - expected[i]) > 1)
                            {
                                state.Preserve("CompositeMismatch", "Stored composite differs from supported sample-space compositing; edit/export blocked.", r.Position);
                                break;
                            }
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
                state.Preserve("ImageResource", "Image resource " + id + " retained; metadata/color interpretation is not implemented.", start, r.Position - start);
            }
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
            if (width == 0 || height == 0) state.Preserve("EmptyLayer", "Empty or non-raster layer is retained without editing.", start, 16);
            int count = r.U16();
            if (count < 1 || count > 56) throw new PsdFormatException("Invalid layer channel count.", r.Position - 2);
            var record = new Record { Layer = layer, Channels = new short[count], Lengths = new int[count] };
            var channels = new HashSet<short>();
            for (int c = 0; c < count; c++)
            {
                short id = r.I16(); uint length = r.U32();
                if (length > int.MaxValue) throw new PsdFormatException("Channel length overflow.", r.Position - 4);
                if (!channels.Add(id)) throw new PsdFormatException("Duplicate layer channel.", r.Position - 6);
                record.Channels[c] = id; record.Lengths[c] = (int)length;
                if (id < -1 || id > 2) state.Preserve("LayerChannel", "Mask or non-RGB layer channel " + id + " is unsupported.", r.Position - 6, 6);
            }
            if (!channels.Contains(0) || !channels.Contains(1) || !channels.Contains(2))
                state.Preserve("LayerChannels", "Layer does not contain all RGB channels.", start);
            if (r.Key() != "8BIM") throw new PsdFormatException("Invalid layer blend signature.", r.Position - 4);
            string blend = r.Key();
            if (blend != "norm") state.Preserve("BlendMode", "Unsupported blend mode: " + blend, r.Position - 4, 4);
            layer.Opacity = r.U8();
            if (r.U8() != 0) state.Preserve("Clipping", "Clipping relationships are unsupported.", r.Position - 1, 1);
            int flags = r.U8();
            layer.Visible = (flags & 2) == 0; // PSD bit 1 means hidden (despite ambiguous wording in published table).
            if ((flags & ~2) != 0) state.Preserve("LayerFlags", "Lock, irrelevance or additional layer flags are not editable.", r.Position - 1, 1);
            r.Zeros(1);
            var extra = r.Section();
            state.Metadata(extra.Remaining, extra.Position);
            var mask = extra.Section();
            if (mask.Remaining != 0) state.Preserve("LayerMask", "Layer mask metadata is unsupported.", mask.Position, mask.Remaining);
            var ranges = extra.Section();
            if (ranges.Remaining != 0) state.Preserve("BlendIf", "Blend ranges are unsupported, including unverified default ranges.", ranges.Position, ranges.Remaining);
            int nameLength = extra.U8();
            extra.Need(nameLength);
            bool ascii = true;
            var chars = new char[nameLength];
            for (int i = 0; i < nameLength; i++) { int b = extra.U8(); chars[i] = (char)b; if (b > 127) ascii = false; }
            layer.Name = new string(chars);
            extra.Zeros((4 - ((nameLength + 1) % 4)) % 4);
            bool hasUnicode = ParseTags(extra, layer, state);
            if (layer.Name.Length > state.Limits.MaxNameCodeUnits) throw new PsdFormatException("Layer name limit exceeded.", start);
            if (layer.Name.IndexOf('\0') >= 0) state.Preserve("NameNull", "Embedded null in layer name.", start);
            if (!hasUnicode && !ascii) state.Preserve("LegacyNameEncoding", "Non-ASCII Pascal layer name has no Unicode equivalent.", start);
            return record;
        }

        private static bool ParseTags(PsdReader r, PsdRasterLayer layer, ParseState state)
        {
            bool unicode = false, idSeen = false;
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
                if (signature != "8BIM" || layer == null || (key != "luni" && key != "lyid"))
                {
                    state.Preserve("TaggedBlock", "Unsupported " + (layer == null ? "document" : "layer") + " metadata: " + key, start, r.Position - start);
                    continue;
                }
                if (key == "lyid")
                {
                    if (idSeen || size != 4) { state.Preserve("LayerIdentity", "Duplicate or nonstandard layer identity block.", start, r.Position - start); continue; }
                    layer.Id = body.I32(); idSeen = true;
                }
                else
                {
                    if (unicode) { state.Preserve("UnicodeName", "Duplicate Unicode name record.", start, r.Position - start); continue; }
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
                }
            }
            return unicode;
        }

        private static void DecodeChannel(PsdReader r, int width, int height, byte[] rgba, int component, ParseState state)
        {
            int compression = r.U16();
            if (compression == 0)
            {
                int count = checked(width * height);
                if (r.Remaining != count) throw new PsdFormatException("Raw layer channel length does not match its rectangle.", r.Position);
                for (int i = 0; i < count; i++) rgba[i * 4 + component] = r.U8();
            }
            else if (compression == 1)
            {
                var table = r.Slice(checked(height * 2));
                for (int y = 0; y < height; y++) DecodePackBits(r.Slice(table.U16()), width, rgba, y * width * 4 + component);
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
                    for (int y = 0; y < height; y++) DecodePackBits(r.Slice(table.U16()), width, rgba, y * width * 4 + c);
                if (r.Remaining != 0) throw new PsdFormatException("Trailing merged RLE bytes.", r.Position);
            }
            else state.Preserve("CompositeCompression", "ZIP or unknown merged compression is unsupported.", r.Position - 2);
        }

        private static void DecodePackBits(PsdReader row, int width, byte[] output, int offset)
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
                    for (int i = 0; i < count; i++) output[offset + (x++) * 4] = row.U8();
                }
                else
                {
                    byte value = row.U8();
                    for (int i = 0; i < count; i++) output[offset + (x++) * 4] = value;
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

        public static byte[] Write(PsdDocument document, PsdLimits limits = null)
        {
            limits = limits ?? new PsdLimits(); limits.Validate();
            int total = ValidateWrite(document, limits);
            var w = new PsdWriter(total);
            var storageLayers = new List<PsdRasterLayer>(document.Layers);
            storageLayers.Reverse();
            w.Key("8BPS"); w.U16(1); w.Zeros(6); w.U16(4);
            w.U32(document.Height); w.U32(document.Width); w.U16(8); w.U16(3);
            w.U32(0); w.U32(0); // Untagged RGB; no invented profile or unknown metadata.
            int layerMaskLength = w.Position; w.U32(0);
            int infoLength = w.Position; w.U32(0);
            w.U16(-document.Layers.Count); // First merged alpha is transparency, not an extra spot/selection channel.
            foreach (var layer in storageLayers)
            {
                w.U32(layer.Top); w.U32(layer.Left); w.U32((long)layer.Top + layer.Height); w.U32((long)layer.Left + layer.Width);
                w.U16(4);
                int channelLength = checked(layer.Width * layer.Height + 2);
                for (int c = 0; c < 4; c++) { w.U16(c == 3 ? -1 : c); w.U32(channelLength); }
                w.Key("8BIM"); w.Key("norm"); w.U8(layer.Opacity); w.U8(0); w.U8(layer.Visible ? 0 : 2); w.U8(0);
                int extraLength = w.Position; w.U32(0);
                w.U32(0); w.U32(0);
                int nameLength = Math.Min(255, layer.Name.Length);
                w.U8(nameLength);
                for (int i = 0; i < nameLength; i++) w.U8(layer.Name[i] <= 127 ? layer.Name[i] : '?');
                w.Zeros((4 - ((nameLength + 1) % 4)) % 4);
                byte[] unicode = Utf16.GetBytes(layer.Name);
                w.Key("8BIM"); w.Key("luni"); w.U32(4 + unicode.Length); w.U32(layer.Name.Length); w.Bytes(unicode);
                w.Key("8BIM"); w.Key("lyid"); w.U32(4); w.U32(layer.Id);
                w.Patch32(extraLength, w.Position - extraLength - 4);
            }
            foreach (var layer in storageLayers)
                for (int c = 0; c < 4; c++)
                {
                    w.U16(0);
                    for (int i = c; i < layer.PixelsRgba.Length; i += 4) w.U8(layer.PixelsRgba[i]);
                }
            if (((w.Position - infoLength - 4) & 1) != 0) w.Zeros(1);
            w.Patch32(infoLength, w.Position - infoLength - 4);
            w.U32(0); // Global mask.
            w.Patch32(layerMaskLength, w.Position - layerMaskLength - 4);
            w.U16(0);
            byte[] merged = CompositeWhiteMatted(document);
            for (int c = 0; c < 4; c++) for (int i = c; i < merged.Length; i += 4) w.U8(merged[i]);
            if (w.Position != total) throw new InvalidOperationException("PSD writer final length mismatch.");
            return w.Data;
        }

        private static int ValidateWrite(PsdDocument document, PsdLimits limits)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (document.Width < 1 || document.Height < 1 || document.Width > limits.MaxDimension || document.Height > limits.MaxDimension ||
                (long)document.Width * document.Height > limits.MaxCanvasPixels) throw new ArgumentException("Canvas exceeds PSD budget.");
            if (document.Layers == null || document.Layers.Count < 1 || document.Layers.Count > limits.MaxLayers)
                throw new ArgumentException("PSD requires one or more layers within the layer budget.");
            long canvasBytes = (long)document.Width * document.Height * 4;
            long pixels = canvasBytes + (long)document.Width * 32, metadata = 0, infoSize = 2;
            var ids = new HashSet<int>();
            foreach (var layer in document.Layers)
            {
                if (layer == null || layer.Id <= 0 || !ids.Add(layer.Id)) throw new ArgumentException("PSD layer IDs must be positive and unique.");
                if (layer.Width < 1 || layer.Height < 1 || layer.Width > limits.MaxDimension || layer.Height > limits.MaxDimension ||
                    (long)layer.Width * layer.Height > limits.MaxCanvasPixels) throw new ArgumentException("Layer rectangle exceeds PSD budget.");
                if ((long)layer.Top + layer.Height > int.MaxValue || (long)layer.Left + layer.Width > int.MaxValue)
                    throw new ArgumentException("Layer coordinates overflow PSD bounds.");
                long layerBytes = (long)layer.Width * layer.Height * 4;
                if (layer.PixelsRgba == null || layer.PixelsRgba.LongLength != layerBytes) throw new ArgumentException("Layer requires exact straight RGBA8 pixels.");
                if (layer.Name == null || layer.Name.Length > limits.MaxNameCodeUnits || layer.Name.IndexOf('\0') >= 0)
                    throw new ArgumentException("Invalid or over-budget Unicode layer name.");
                Utf16.GetByteCount(layer.Name); // Reject unpaired surrogate; never silently replace it.
                int pascal = Math.Min(255, layer.Name.Length) + 1;
                pascal += (4 - pascal % 4) % 4;
                long extra = 8 + pascal + 16 + layer.Name.Length * 2L + 16;
                metadata += extra;
                infoSize += 58 + extra + 8 + layerBytes;
                pixels += layerBytes;
            }
            if (pixels > limits.MaxDecodedBytes || metadata > limits.MaxMetadataBytes) throw new ArgumentException("PSD pixel or metadata budget exceeded.");
            infoSize += infoSize & 1;
            long total = 26 + 4 + 4 + 4 + 4 + infoSize + 4 + 2 + canvasBytes;
            if (total > limits.MaxOutputBytes || total > int.MaxValue) throw new ArgumentException("PSD output byte budget exceeded.");
            return (int)total;
        }

        // Deterministic encoded-sample source-over. The merged PSD RGB is white-matted, while each layer
        // remains straight RGBA including invisible RGB. Photoshop/CSP rendering parity remains an external gate.
        private static byte[] CompositeWhiteMatted(PsdDocument document)
        {
            var rgba = new byte[checked(document.Width * document.Height * 4)];
            // One scanline of high-precision premultiplied working data avoids a full float canvas.
            var row = new double[checked(document.Width * 4)];
            for (int y = 0; y < document.Height; y++)
            {
                Array.Clear(row, 0, row.Length);
                for (int i = document.Layers.Count - 1; i >= 0; i--)
                {
                    var layer = document.Layers[i];
                    long ly = (long)y - layer.Top;
                    if (!layer.Visible || layer.Opacity == 0 || ly < 0 || ly >= layer.Height) continue;
                    int first = (int)Math.Max(0L, layer.Left);
                    int last = (int)Math.Min(document.Width, (long)layer.Left + layer.Width);
                    if (first >= last) continue;
                    for (int x = first; x < last; x++)
                    {
                        int p = checked(((int)ly * layer.Width + (int)((long)x - layer.Left)) * 4);
                        int dst = x * 4;
                        double a = layer.PixelsRgba[p + 3] * layer.Opacity / 65025.0;
                        row[dst] = layer.PixelsRgba[p] * a + row[dst] * (1 - a);
                        row[dst + 1] = layer.PixelsRgba[p + 1] * a + row[dst + 1] * (1 - a);
                        row[dst + 2] = layer.PixelsRgba[p + 2] * a + row[dst + 2] * (1 - a);
                        row[dst + 3] = a + row[dst + 3] * (1 - a);
                    }
                }
                for (int x = 0; x < document.Width; x++)
                {
                    int src = x * 4, dst = (y * document.Width + x) * 4;
                    double white = 255 * (1 - row[src + 3]);
                    rgba[dst] = RoundByte(row[src] + white);
                    rgba[dst + 1] = RoundByte(row[src + 1] + white);
                    rgba[dst + 2] = RoundByte(row[src + 2] + white);
                    rgba[dst + 3] = RoundByte(row[src + 3] * 255);
                }
            }
            return rgba;
        }
        private static byte RoundByte(double value) { return (byte)Math.Max(0, Math.Min(255, (int)Math.Floor(value + 0.5))); }
    }
}
