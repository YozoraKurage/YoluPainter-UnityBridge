using System;
using System.Collections.Generic;
using System.Text;

namespace Yozolab.YoluPainter.Core.Brushes
{
    /// <summary>A Photoshop pattern read as a brush texture: grey 0..255 per pixel (white = most paint, Photoshop's "high
    /// points"), rows bottom-up like every <see cref="BrushTip"/>. Warnings say where the grey is not the file's own value.</summary>
    public sealed class PhotoshopPattern
    {
        public string Name { get; internal set; }
        public string Id { get; internal set; }
        public BrushTip Texture { get; internal set; }
        public IReadOnlyList<string> Warnings { get; internal set; }
    }

    /// <summary>Reads Photoshop patterns: the records of an .abr "patt" section (each framed by a 32-bit length and padded to
    /// 4 bytes, like the PSD "Patt" block) and standalone .pat files ("8BPT", version 1, a count, then unframed records).
    /// Layout from the Photoshop File Formats Specification ("Patterns", "Virtual Memory Array List") as implemented by
    /// psd-tools. Grayscale 8-bit patterns are taken byte for byte; RGB and indexed ones are reduced to grey (reported);
    /// 16-bit ones keep the high byte (reported); other modes, ZIP data and patterns larger than a tip are refused.</summary>
    public static class PhotoshopPatternReader
    {
        public sealed class Failure { public string Name; public string Reason; }

        /// <summary>Reads a .pat file. Patterns that cannot be used are listed in failures; a malformed file throws.</summary>
        public static IReadOnlyList<PhotoshopPattern> ReadPat(byte[] data, List<Failure> failures = null)
        {
            var r = new BigEndianReader(data);
            if (r.Remaining < 4 || r.Ascii(4) != "8BPT") throw new BrushImportException("Not a Photoshop pattern file (no 8BPT signature).");
            int version = r.U16(); if (version != 1) throw new BrushImportException("Unsupported pattern file version " + version + ".");
            uint count = r.U32(); if (count > 10000) throw new BrushImportException("Invalid pattern count " + count + ".");
            var result = new List<PhotoshopPattern>();
            // 枠が無いので、1 つ読めないとその後ろの位置も分からない。読めたところまでを返し、残りは理由付きで知らせる。
            for (uint i = 0; i < count; i++)
            {
                var p = ReadPattern(r, out string name, out string reason);
                if (p != null) result.Add(p);
                else if (reason != null) { failures?.Add(new Failure { Name = name, Reason = reason }); }
            }
            return result;
        }

        /// <summary>A .pat file as brushes: one per usable pattern, a round brush whose paper texture is the pattern at full depth
        /// (so it can be picked as a texture for other brushes too). Skipped patterns are listed in every brush's warnings.</summary>
        public static IReadOnlyList<ImportedBrush> ReadPatBrushes(byte[] data)
        {
            var failures = new List<Failure>();
            var patterns = ReadPat(data, failures);
            var skipped = failures.ConvertAll(f => "Pattern '" + f.Name + "' was skipped: " + f.Reason);
            if (patterns.Count == 0) throw new BrushImportException(skipped.Count > 0 ? "No pattern in the file can be used. " + string.Join(" ", skipped) : "The file contains no patterns.");
            var result = new List<ImportedBrush>();
            foreach (var p in patterns)
            {
                var warnings = new List<string>(p.Warnings); warnings.AddRange(skipped);
                result.Add(new ImportedBrush(p.Name, "Photoshop pattern", new BrushSettings { Texture = p.Texture, TextureDepth = 1 }, warnings));
            }
            return result;
        }

        /// <summary>Reads the records of an .abr "patt" section.</summary>
        internal static List<PhotoshopPattern> ReadFramed(BigEndianReader r, List<Failure> failures)
        {
            var result = new List<PhotoshopPattern>();
            while (r.Remaining >= 4)
            {
                int length = r.Count(r.U32(), 1, "pattern length");
                if (length == 0) throw new BrushImportException("An empty pattern record.");
                var p = ReadPattern(new BigEndianReader(r.Bytes(length)), out string name, out string reason);
                if (p != null) result.Add(p); else failures.Add(new Failure { Name = name, Reason = reason });
                int pad = (4 - length % 4) % 4; if (pad > 0 && r.Remaining >= pad) r.Skip(pad);
            }
            return result;
        }

        /// <summary>One pattern record. Structural damage throws; a readable record this engine cannot use returns null with a
        /// reason (the reader is then past the record).</summary>
        static PhotoshopPattern ReadPattern(BigEndianReader r, out string name, out string reason)
        {
            name = null; reason = null;
            int version = r.I32(); if (version != 1) throw new BrushImportException("Unsupported pattern version " + version + ".");
            int mode = r.I32(); int height = r.I16(), width = r.I16();
            name = ActionDescriptorReader.UnicodeString(r);
            string id = Encoding.ASCII.GetString(r.Bytes(r.U8()));
            byte[] palette = null;
            if (mode == 2) { palette = r.Bytes(256 * 3); r.Skip(4); }
            int listVersion = r.I32(); if (listVersion != 3) throw new BrushImportException("Unsupported pattern data version " + listVersion + ".");
            var list = new BigEndianReader(r.Bytes(r.Count(r.U32(), 1, "pattern data length")));
            int top = list.I32(), left = list.I32(), bottom = list.I32(), right = list.I32();
            int channelCount = list.I32(); if (channelCount < 0 || channelCount > 56) throw new BrushImportException("Invalid pattern channel count " + channelCount + ".");
            var channels = new List<byte[]>(); var warnings = new List<string>(); int extra = 0, depthSeen = 8;
            int needed = mode == 1 || mode == 2 ? 1 : mode == 3 ? 3 : 0;
            string modeName = mode == 0 ? "bitmap" : mode == 4 ? "CMYK" : mode == 7 ? "multichannel" : mode == 8 ? "duotone" : mode == 9 ? "Lab" : "mode " + mode;
            int w = right - left, h = bottom - top;
            for (int c = 0; c < channelCount + 2; c++)
            {
                uint written = list.U32(); if (written == 0) continue;
                int length = list.Count(list.U32(), 1, "pattern channel length"); if (length == 0) continue;
                if (length < 23) throw new BrushImportException("Invalid pattern channel length " + length + ".");
                var body = new BigEndianReader(list.Bytes(length));
                if (needed == 0 || c >= needed) { if (needed > 0) extra++; continue; }
                body.I32(); int ct = body.I32(), cl = body.I32(), cb = body.I32(), cr = body.I32(); int depth = body.I16(), compression = body.U8();
                if (cr - cl != w || cb - ct != h) { reason = "Its channels differ in size."; return null; }
                if (w < 1 || h < 1 || w > BrushTip.MaxSize || h > BrushTip.MaxSize) { reason = "It is " + w + "x" + h + " pixels; textures must be 1.." + BrushTip.MaxSize + " per side."; return null; }
                if (depth != 8 && depth != 16) { reason = depth + "-bit patterns are not supported."; return null; }
                if (compression > 1) { reason = "ZIP-compressed pattern data is not supported."; return null; }
                depthSeen = depth;
                int bytesPerPixel = depth / 8, rowBytes = w * bytesPerPixel;
                var plane = new byte[w * h]; int[] rleLengths = null;
                for (int y = 0; y < h; y++)
                {
                    byte[] row;
                    if (compression == 1)
                    {
                        if (rleLengths == null) { rleLengths = new int[h]; for (int i = 0; i < h; i++) rleLengths[i] = body.U16(); }
                        row = PackBits.DecodeRow(body.Bytes(rleLengths[y]), rowBytes);
                    }
                    else row = body.Bytes(rowBytes);
                    for (int x = 0; x < w; x++) plane[(h - 1 - y) * w + x] = row[x * bytesPerPixel]; // 上位バイト。上から下の行を左下原点へ
                }
                channels.Add(plane);
            }
            if (needed == 0) { reason = "Patterns in " + modeName + " mode are not supported."; return null; }
            if (channels.Count < needed) { reason = "Its colour channels are missing."; return null; }
            if (height != h || width != w) warnings.Add("The pattern's stated size (" + width + "x" + height + ") differs from its data (" + w + "x" + h + "); the data is used.");
            if (depthSeen == 16) warnings.Add("The 16-bit pattern is reduced to 8 bits.");
            if (extra > 0) warnings.Add("The pattern's transparency / mask channels are ignored (" + extra + ").");
            var grey = new byte[w * h];
            if (mode == 1) Buffer.BlockCopy(channels[0], 0, grey, 0, grey.Length);
            else
            {
                for (int i = 0; i < grey.Length; i++)
                {
                    int rr, gg, bb;
                    if (mode == 2) { int k = channels[0][i] * 3; rr = palette[k]; gg = palette[k + 1]; bb = palette[k + 2]; }
                    else { rr = channels[0][i]; gg = channels[1][i]; bb = channels[2][i]; }
                    grey[i] = (byte)((rr * 299 + gg * 587 + bb * 114 + 500) / 1000);
                }
                warnings.Add("The " + (mode == 2 ? "indexed" : "RGB") + " pattern was converted to grey with Rec. 601 weights; Photoshop's own conversion may differ slightly.");
            }
            return new PhotoshopPattern { Name = string.IsNullOrEmpty(name) ? "Pattern" : name, Id = id, Texture = new BrushTip(string.IsNullOrEmpty(name) ? "Pattern" : name, w, h, grey), Warnings = warnings.AsReadOnly() };
        }
    }
}
