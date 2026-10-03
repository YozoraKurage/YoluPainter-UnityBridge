using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Yozolab.YoluPainter.Core.Shelf
{
    /// <summary>
    /// The PNG form of an image resource (straight RGBA8, bottom-left rows in memory, top-down rows in the file). Written as
    /// colour type 6 (RGBA), bit depth 8, no interlace, one adaptive filter per row (the smallest sum of absolute values, the
    /// usual heuristic), zlib with deflate, no ancillary chunks — so the same pixels always give the same bytes with the same
    /// runtime's deflate. Reading is strict and bounded: 8-bit greyscale, grey + alpha, RGB and RGBA (colour types 0, 4, 2, 6)
    /// without interlace; palette, 16-bit, interlaced files and unknown critical chunks are refused with a reason (the editor reads
    /// such files with Unity's decoder instead). Every chunk's CRC, the zlib header and Adler-32 are checked, and inflation stops at
    /// the size the header promises. The RGB of fully transparent pixels is kept exactly in both directions.
    /// </summary>
    public static class RgbaPng
    {
        static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        const int IdatChunk = 1 << 18;

        /// <summary>Encodes straight RGBA8 (bottom-left origin, <paramref name="width"/> × <paramref name="height"/> × 4 bytes).</summary>
        public static byte[] Encode(byte[] rgba, int width, int height)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (width < 1 || height < 1 || width > ImageContent.MaxSide || height > ImageContent.MaxSide) throw new ArgumentOutOfRangeException(nameof(width), "An image is 1–" + ImageContent.MaxSide + " pixels on each side.");
            if (rgba.LongLength != (long)width * height * 4) throw new ArgumentException("The image must be " + width + " × " + height + " RGBA8.", nameof(rgba));
            int stride = width * 4;
            var output = new MemoryStream();
            output.Write(Signature, 0, Signature.Length);
            var header = new byte[13];
            WriteU32(header, 0, (uint)width); WriteU32(header, 4, (uint)height);
            header[8] = 8; header[9] = 6; header[10] = 0; header[11] = 0; header[12] = 0;
            WriteChunk(output, "IHDR", header, 0, header.Length);
            byte[] zlib;
            using (var compressed = new MemoryStream())
            {
                compressed.WriteByte(0x78); compressed.WriteByte(0x9C); // deflate, 32 KiB window, default level marker
                uint adler = 1;
                using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true))
                {
                    var row = new byte[1 + stride]; var best = new byte[1 + stride];
                    for (int fileRow = 0; fileRow < height; fileRow++)
                    {
                        int y = height - 1 - fileRow; // the file is top-down
                        int current = y * stride, previous = fileRow == 0 ? -1 : (y + 1) * stride;
                        long bestScore = long.MaxValue;
                        for (byte filter = 0; filter <= 4; filter++)
                        {
                            row[0] = filter; long score = 0;
                            for (int i = 0; i < stride; i++)
                            {
                                int x = rgba[current + i], a = i >= 4 ? rgba[current + i - 4] : 0, b = previous >= 0 ? rgba[previous + i] : 0, c = i >= 4 && previous >= 0 ? rgba[previous + i - 4] : 0;
                                byte v = (byte)(x - Predict(filter, a, b, c));
                                row[1 + i] = v; score += v < 128 ? v : 256 - v;
                                if (score >= bestScore) break;
                            }
                            if (score < bestScore) { bestScore = score; Buffer.BlockCopy(row, 0, best, 0, row.Length); }
                        }
                        deflate.Write(best, 0, best.Length);
                        adler = Adler32(adler, best, 0, best.Length);
                    }
                }
                var tail = new byte[4]; WriteU32(tail, 0, adler); compressed.Write(tail, 0, 4);
                zlib = compressed.ToArray();
            }
            for (int at = 0; at < zlib.Length; at += IdatChunk) WriteChunk(output, "IDAT", zlib, at, Math.Min(IdatChunk, zlib.Length - at));
            WriteChunk(output, "IEND", new byte[0], 0, 0);
            return output.ToArray();
        }

        /// <summary>Whether the bytes start like a PNG file.</summary>
        public static bool LooksLikePng(byte[] data)
        {
            if (data == null || data.Length < Signature.Length) return false;
            for (int i = 0; i < Signature.Length; i++) if (data[i] != Signature[i]) return false;
            return true;
        }

        /// <summary>Reads only the size from the header (IHDR). InvalidDataException when it is not a PNG.</summary>
        public static (int width, int height) ReadSize(byte[] data)
        {
            if (!LooksLikePng(data) || data.Length < 33 || Encoding.ASCII.GetString(data, 12, 4) != "IHDR") throw new InvalidDataException("Not a PNG image.");
            uint w = ReadU32(data, 16), h = ReadU32(data, 20);
            if (w < 1 || h < 1 || w > int.MaxValue || h > int.MaxValue) throw new InvalidDataException("The PNG has an impossible size.");
            return ((int)w, (int)h);
        }

        /// <summary>Decodes to straight RGBA8 with bottom-left origin. Refuses images larger than <paramref name="maxSide"/> on a side
        /// before inflating anything. InvalidDataException with the reason for anything it does not read.</summary>
        public static (byte[] rgba, int width, int height) Decode(byte[] data, int maxSide = ImageContent.MaxSide)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (!LooksLikePng(data)) throw new InvalidDataException("Not a PNG image (wrong signature).");
            int at = Signature.Length, width = 0, height = 0, channels = 0;
            bool seenHeader = false, seenEnd = false;
            var idat = new MemoryStream();
            while (at < data.Length)
            {
                if (at + 12 > data.Length) throw new InvalidDataException("The PNG ends inside a chunk.");
                uint length = ReadU32(data, at);
                if (length > int.MaxValue || at + 12L + length > data.Length) throw new InvalidDataException("A PNG chunk is longer than the file.");
                string type = Encoding.ASCII.GetString(data, at + 4, 4);
                if (Crc(data, at + 4, 4 + (int)length) != ReadU32(data, at + 8 + (int)length)) throw new InvalidDataException("The PNG chunk " + type + " has a wrong CRC.");
                int body = at + 8;
                if (!seenHeader && type != "IHDR") throw new InvalidDataException("The PNG does not start with IHDR.");
                switch (type)
                {
                    case "IHDR":
                        if (seenHeader || length != 13) throw new InvalidDataException("The PNG has a malformed IHDR.");
                        seenHeader = true;
                        uint w = ReadU32(data, body), h = ReadU32(data, body + 4);
                        byte depth = data[body + 8], colour = data[body + 9], method = data[body + 10], filterMethod = data[body + 11], interlace = data[body + 12];
                        if (w < 1 || h < 1 || w > maxSide || h > maxSide) throw new InvalidDataException("The PNG is " + w + " × " + h + "; images are 1–" + maxSide + " pixels on each side.");
                        if (method != 0 || filterMethod != 0) throw new InvalidDataException("The PNG uses an unknown compression or filter method.");
                        if (interlace != 0) throw new InvalidDataException("Interlaced PNG images are not read here.");
                        if (depth != 8) throw new InvalidDataException("Only 8-bit PNG images are read here (this one has " + depth + " bits per sample).");
                        switch (colour) { case 0: channels = 1; break; case 4: channels = 2; break; case 2: channels = 3; break; case 6: channels = 4; break; default: throw new InvalidDataException("PNG colour type " + colour + " (palette) is not read here."); }
                        width = (int)w; height = (int)h;
                        break;
                    case "IDAT": idat.Write(data, body, (int)length); break;
                    case "IEND": seenEnd = true; break;
                    case "PLTE": break; // only meaningful for palette images, which are refused above; allowed (suggested palette) otherwise
                    default:
                        if ((data[at + 4] & 0x20) == 0) throw new InvalidDataException("The PNG has an unknown critical chunk " + type + ".");
                        break; // ancillary chunks (gAMA, sRGB, iCCP, tEXt …) are ignored: the stored values are the image
                }
                at += 12 + (int)length;
                if (seenEnd) break;
            }
            if (!seenHeader || !seenEnd) throw new InvalidDataException("The PNG is incomplete (no IHDR or IEND).");
            var zlib = idat.ToArray();
            if (zlib.Length < 6) throw new InvalidDataException("The PNG has no image data.");
            int cmf = zlib[0], flg = zlib[1];
            if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0) throw new InvalidDataException("The PNG image data is not a plain zlib stream.");
            int stride = width * channels;
            long expected = (long)height * (1 + stride);
            var raw = new byte[expected];
            using (var inflate = new DeflateStream(new MemoryStream(zlib, 2, zlib.Length - 2, false), CompressionMode.Decompress))
            {
                long total = 0;
                try
                {
                    while (total < expected)
                    {
                        int n = inflate.Read(raw, (int)total, (int)Math.Min(expected - total, 1 << 20));
                        if (n <= 0) break;
                        total += n;
                    }
                    if (total < expected) throw new InvalidDataException("The PNG image data is shorter than " + width + " × " + height + ".");
                    if (inflate.Read(new byte[1], 0, 1) > 0) throw new InvalidDataException("The PNG image data is longer than " + width + " × " + height + ".");
                }
                catch (Exception ex) when (!(ex is InvalidDataException)) { throw new InvalidDataException("The PNG image data could not be decompressed (" + ex.Message + ").", ex); }
            }
            if (Adler32(1, raw, 0, raw.Length) != ReadU32(zlib, zlib.Length - 4)) throw new InvalidDataException("The PNG image data has a wrong Adler-32 checksum.");
            // filters, then expand to RGBA with the rows turned bottom-up
            var rgba = new byte[(long)width * height * 4];
            var prior = new byte[stride]; var line = new byte[stride];
            for (int fileRow = 0; fileRow < height; fileRow++)
            {
                int start = fileRow * (1 + stride); byte filter = raw[start];
                if (filter > 4) throw new InvalidDataException("The PNG uses an unknown row filter " + filter + ".");
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= channels ? line[i - channels] : 0, b = prior[i], c = i >= channels ? prior[i - channels] : 0;
                    line[i] = (byte)(raw[start + 1 + i] + Predict(filter, a, b, c));
                }
                int o = (height - 1 - fileRow) * width * 4;
                for (int x = 0; x < width; x++, o += 4)
                {
                    int s = x * channels;
                    switch (channels)
                    {
                        case 1: rgba[o] = rgba[o + 1] = rgba[o + 2] = line[s]; rgba[o + 3] = 255; break;
                        case 2: rgba[o] = rgba[o + 1] = rgba[o + 2] = line[s]; rgba[o + 3] = line[s + 1]; break;
                        case 3: rgba[o] = line[s]; rgba[o + 1] = line[s + 1]; rgba[o + 2] = line[s + 2]; rgba[o + 3] = 255; break;
                        default: rgba[o] = line[s]; rgba[o + 1] = line[s + 1]; rgba[o + 2] = line[s + 2]; rgba[o + 3] = line[s + 3]; break;
                    }
                }
                var swap = prior; prior = line; line = swap;
            }
            return (rgba, width, height);
        }

        static int Predict(byte filter, int a, int b, int c)
        {
            switch (filter)
            {
                case 0: return 0;
                case 1: return a;
                case 2: return b;
                case 3: return (a + b) >> 1;
                default:
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
            }
        }

        static void WriteChunk(Stream output, string type, byte[] data, int offset, int length)
        {
            var head = new byte[8]; WriteU32(head, 0, (uint)length);
            Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
            output.Write(head, 0, 8); output.Write(data, offset, length);
            uint crc = Crc(head, 4, 4, data, offset, length);
            var tail = new byte[4]; WriteU32(tail, 0, crc); output.Write(tail, 0, 4);
        }

        static void WriteU32(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
        static uint ReadU32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

        static uint[] crcTable;
        static uint Crc(byte[] a, int offset, int length, byte[] b = null, int bOffset = 0, int bLength = 0)
        {
            if (crcTable == null)
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++) { uint v = i; for (int k = 0; k < 8; k++) v = (v & 1) != 0 ? 0xEDB88320u ^ (v >> 1) : v >> 1; t[i] = v; }
                crcTable = t;
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++) crc = crcTable[(crc ^ a[offset + i]) & 0xFF] ^ (crc >> 8);
            if (b != null) for (int i = 0; i < bLength; i++) crc = crcTable[(crc ^ b[bOffset + i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        static uint Adler32(uint adler, byte[] data, int offset, int length)
        {
            uint a = adler & 0xFFFF, b = adler >> 16;
            while (length > 0)
            {
                int n = Math.Min(length, 5552);
                for (int i = 0; i < n; i++) { a += data[offset + i]; b += a; }
                a %= 65521; b %= 65521; offset += n; length -= n;
            }
            return b << 16 | a;
        }
    }
}
