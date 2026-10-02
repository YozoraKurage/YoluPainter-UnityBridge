using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Yozolab.YoluPainter.Core.MeshMaps
{
    /// <summary>
    /// mesh map 1 枚の保存形式（.ylp の中の meshmap-&lt;種類&gt;.bin）。リトルエンディアン:
    /// "YLPMMAP\0"、形式の版、種類、エンジンの版、モデルの指紋、形の指紋（UV・三角形）、UV チャンネル、幅、高さ、スロット、余白、チャンネル数、
    /// 種類の設定・空間・姿勢・焼く元の文字列（長さ付き UTF-8）、境界箱（double × 6）、圧縮した中身の長さと中身。
    /// 中身は由来の面（1 バイト/テクセル）と、チャンネルごとに「行の中で左との差（16 bit）」の上位バイトの面・下位バイトの面を
    /// 並べて Deflate したもの。日時は入れない（同じ結果なら同じバイト列）。読むときは長さ・範囲・版を確かめ、宣言した大きさを
    /// 超えて展開しない。新しい版は読まずに断る。
    /// </summary>
    public static class MeshMapBinary
    {
        public const int FormatVersion = 1;
        public const string EntryPrefix = "meshmap-", EntrySuffix = ".bin";
        const int MaxString = 4096;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("YLPMMAP\0");

        public static string EntryName(MeshMapKind kind) => EntryPrefix + kind + EntrySuffix;
        public static bool TryParseEntryName(string entry, out MeshMapKind kind)
        {
            kind = default;
            if (entry == null || !entry.StartsWith(EntryPrefix, StringComparison.Ordinal) || !entry.EndsWith(EntrySuffix, StringComparison.Ordinal)) return false;
            string name = entry.Substring(EntryPrefix.Length, entry.Length - EntryPrefix.Length - EntrySuffix.Length);
            return Enum.TryParse(name, false, out kind) && Enum.IsDefined(typeof(MeshMapKind), kind) && kind.ToString() == name;
        }

        public static byte[] Write(BakedMeshMap map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            var p = map.Provenance;
            using (var stream = new MemoryStream())
            {
                using (var w = new BinaryWriter(stream, new UTF8Encoding(false), true))
                {
                    w.Write(Magic); w.Write(FormatVersion); w.Write((int)p.Kind); w.Write(p.EngineVersion); WriteString(w, p.MeshHash); WriteString(w, p.TopologyHash);
                    w.Write(p.UvChannel); w.Write(p.Width); w.Write(p.Height); w.Write(p.TargetSlot); w.Write(p.Padding); w.Write(map.Channels);
                    WriteString(w, p.SettingsKey); WriteString(w, p.Space); WriteString(w, p.Pose); WriteString(w, p.Source);
                    for (int a = 0; a < 3; a++) w.Write(p.BoundsMin(a));
                    for (int a = 0; a < 3; a++) w.Write(p.BoundsMax(a));
                    byte[] payload = Compress(Filter(map));
                    w.Write(payload.Length); w.Write(payload);
                }
                return stream.ToArray();
            }
        }

        static void WriteString(BinaryWriter w, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            if (bytes.Length > MaxString) throw new InvalidOperationException("Mesh map metadata is too long.");
            w.Write(bytes.Length); w.Write(bytes);
        }

        /// <summary>由来の面、チャンネルごとに差の上位バイトの面と下位バイトの面。</summary>
        static byte[] Filter(BakedMeshMap map)
        {
            int width = map.Width, height = map.Height, channels = map.Channels; long texels = (long)width * height;
            var raw = new byte[texels * (1 + 2 * channels)];
            Buffer.BlockCopy(map.Coverage, 0, raw, 0, map.Coverage.Length);
            var data = map.Data;
            for (int c = 0; c < channels; c++)
            {
                long high = texels * (1 + 2 * c), low = high + texels;
                for (int y = 0; y < height; y++)
                {
                    int previous = 0;
                    for (int x = 0; x < width; x++)
                    {
                        long i = (long)y * width + x; int value = data[i * channels + c];
                        int delta = (value - previous) & 0xFFFF; previous = value;
                        raw[high + i] = (byte)(delta >> 8); raw[low + i] = (byte)delta;
                    }
                }
            }
            return raw;
        }
        static byte[] Compress(byte[] raw)
        {
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true)) deflate.Write(raw, 0, raw.Length);
                return output.ToArray();
            }
        }

        public static BakedMeshMap Read(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                using (var r = new BinaryReader(stream, new UTF8Encoding(false, true)))
                {
                    var magic = r.ReadBytes(Magic.Length);
                    for (int i = 0; i < Magic.Length; i++) if (magic.Length != Magic.Length || magic[i] != Magic[i]) throw new InvalidDataException("Not a YoluPainter mesh map.");
                    int version = r.ReadInt32();
                    if (version > FormatVersion) throw new InvalidDataException("The mesh map was written by a newer YoluPainter (format " + version + ").");
                    if (version < 1) throw new InvalidDataException("Unsupported mesh map format " + version + ".");
                    var kind = (MeshMapKind)r.ReadInt32();
                    if (!Enum.IsDefined(typeof(MeshMapKind), kind)) throw new InvalidDataException("Unknown mesh map kind " + (int)kind + ".");
                    int engine = r.ReadInt32(); string hash = ReadString(r), topology = ReadString(r);
                    int uv = r.ReadInt32(), width = r.ReadInt32(), height = r.ReadInt32(), slot = r.ReadInt32(), padding = r.ReadInt32(), channels = r.ReadInt32();
                    if (width < 1 || height < 1 || width > MeshBakeSettings.MaxSize || height > MeshBakeSettings.MaxSize) throw new InvalidDataException("Mesh map size " + width + "×" + height + " is out of range.");
                    if (channels != BakedMeshMap.ChannelCount(kind)) throw new InvalidDataException("Mesh map channel count does not match its kind.");
                    if (uv < 0 || uv > 7 || slot < -1 || padding < 0 || padding > MeshBakeSettings.MaxPadding || engine < 1) throw new InvalidDataException("Mesh map metadata is out of range.");
                    string settings = ReadString(r), space = ReadString(r), pose = ReadString(r), source = ReadString(r);
                    var min = new double[3]; var max = new double[3];
                    for (int a = 0; a < 3; a++) min[a] = r.ReadDouble();
                    for (int a = 0; a < 3; a++) max[a] = r.ReadDouble();
                    for (int a = 0; a < 3; a++) if (double.IsNaN(min[a]) || double.IsInfinity(min[a]) || double.IsNaN(max[a]) || double.IsInfinity(max[a]) || max[a] < min[a]) throw new InvalidDataException("Mesh map bounds are invalid.");
                    int length = r.ReadInt32();
                    if (length < 0 || length > stream.Length - stream.Position) throw new InvalidDataException("Mesh map payload is truncated.");
                    byte[] payload = r.ReadBytes(length);
                    if (stream.Position != stream.Length) throw new InvalidDataException("Mesh map has trailing bytes.");
                    long texels = (long)width * height;
                    byte[] raw = Inflate(payload, texels * (1 + 2 * channels));
                    var coverage = new byte[texels]; Buffer.BlockCopy(raw, 0, coverage, 0, (int)texels);
                    foreach (byte b in coverage) if (b > (byte)MeshTexelCoverage.Padding) throw new InvalidDataException("Mesh map coverage holds an unknown value.");
                    var data = new ushort[texels * channels];
                    for (int c = 0; c < channels; c++)
                    {
                        long high = texels * (1 + 2 * c), low = high + texels;
                        for (int y = 0; y < height; y++)
                        {
                            int previous = 0;
                            for (int x = 0; x < width; x++)
                            {
                                long i = (long)y * width + x;
                                int value = (previous + (raw[high + i] << 8 | raw[low + i])) & 0xFFFF; previous = value;
                                data[i * channels + c] = (ushort)value;
                            }
                        }
                    }
                    var provenance = new MeshMapProvenance(kind, engine, hash, topology, uv, width, height, slot, padding, settings, space, pose, source, min, max);
                    return new BakedMeshMap(provenance, data, coverage);
                }
            }
            catch (EndOfStreamException) { throw new InvalidDataException("Mesh map is truncated."); }
            catch (DecoderFallbackException) { throw new InvalidDataException("Mesh map text is not valid UTF-8."); }
        }

        static string ReadString(BinaryReader r)
        {
            int length = r.ReadInt32();
            if (length < 0 || length > MaxString) throw new InvalidDataException("Mesh map text is too long.");
            var bytes = r.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        /// <summary>宣言した長さちょうどに展開する（超える・足りないものは断る。zip bomb 対策で、余分は読まない）。</summary>
        static byte[] Inflate(byte[] payload, long expected)
        {
            if (expected > int.MaxValue) throw new InvalidDataException("Mesh map is too large.");
            var result = new byte[expected];
            try
            {
                using (var deflate = new DeflateStream(new MemoryStream(payload, false), CompressionMode.Decompress))
                {
                    int total = 0, n;
                    while (total < result.Length && (n = deflate.Read(result, total, result.Length - total)) > 0) total += n;
                    if (total != result.Length) throw new InvalidDataException("Mesh map payload is shorter than its declared size.");
                    if (deflate.Read(new byte[1], 0, 1) != 0) throw new InvalidDataException("Mesh map payload expands beyond its declared size.");
                }
            }
            catch (Exception ex) when (ex is IOException && !(ex is EndOfStreamException) || ex is InvalidOperationException || ex is ArgumentException)
            { throw new InvalidDataException("Mesh map payload is not valid compressed data (" + ex.Message + ").", ex); }
            return result;
        }
    }
}
