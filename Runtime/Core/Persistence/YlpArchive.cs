using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Globalization;

namespace Yozolab.YoluPainter.Core.Persistence
{
    /// <summary>
    /// YoluPainter の 1 ファイル形式 .ylp（zip）。先頭に無圧縮の "mimetype"（OpenRaster / ODF と同じ識別の仕方）、
    /// 全エントリーの SHA-256 と長さを並べた "manifest.sha256"、その後に中身。読むときは並び・名前・長さ・ハッシュを
    /// すべて確かめ、manifest に無いエントリーや足りないエントリーがあれば読まない。
    /// <list type="bullet">
    /// <item>YOLUPAINTER-YLP-1: エントリーの名前は根と composite/ の下だけ、正本（document.utpaint）は根（.ylp の形式 1・2）。読むだけ。</item>
    /// <item>YOLUPAINTER-YLP-2: テクスチャセットの置き場 sets/&lt;ID&gt;/ の下にも同じ名前を置ける（.ylp の形式 3）。正本は根か
    /// どれかのセットの下にあればよい（どのセットに要るかは中身の形式 <see cref="YlpFormat"/> が決める）。名前の決まりが
    /// 広がったので番号を上げた（YLP-1 の読み手は名前で断る代わりに「新しい YoluPainter で書かれた」と断る）。読むだけ。</item>
    /// <item>YOLUPAINTER-YLP-3: 加えて、根の resources/ の下に 1 段の名前を置ける（プロジェクトのリソースの画素。.ylp の形式 4 から）。
    /// 書くのはこれ。YLP-2 までの読み手は「新しい YoluPainter で書かれた」と断る。</item>
    /// </list>
    /// </summary>
    public static class YlpArchive
    {
        public const string Extension = ".ylp";
        public const string MimeType = "application/x-yolupainter";
        public const string ManifestName = "manifest.sha256";
        /// <summary>書く manifest の 1 行目（テクスチャセットの置き場とリソースの置き場を認める版）。</summary>
        public const string ManifestHeader = "YOLUPAINTER-YLP-3";
        /// <summary>前の版の manifest の 1 行目（読むだけ。テクスチャセットの置き場は認め、リソースの置き場は認めない）。</summary>
        public const string ManifestHeaderV2 = "YOLUPAINTER-YLP-2";
        /// <summary>最初の版の manifest の 1 行目（読むだけ。テクスチャセットの置き場もリソースの置き場も認めない）。</summary>
        public const string ManifestHeaderV1 = "YOLUPAINTER-YLP-1";
        /// <summary>リソースの置き場（YLP-3 から）。</summary>
        public const string ResourceFolder = "resources/";
        public const string NativeName = "document.utpaint";
        public const string CompositeFolder = "composite/";
        public const long MaxEntryBytes = 512L * 1024 * 1024, MaxTotalBytes = 768L * 1024 * 1024;
        public const int MaxEntries = 1000;

        static uint[] crcTable;
        internal static uint Crc32(byte[] data)
        {
            if (crcTable == null)
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++) { uint v = i; for (int k = 0; k < 8; k++) v = (v & 1) != 0 ? 0xEDB88320u ^ (v >> 1) : v >> 1; t[i] = v; }
                crcTable = t;
            }
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        /// <summary>中身（名前 → バイト列）から .ylp のバイト列を作る。PNG は圧縮済みなので無圧縮で入れる。</summary>
        public static byte[] Write(IDictionary<string, byte[]> files)
        {
            if (files == null || !HasNative(files.Keys, 3)) throw new ArgumentException("A complete native document is required.");
            long total = 0;
            foreach (var entry in files)
            {
                ValidateName(entry.Key, 3);
                if (entry.Key == ManifestName || entry.Key == "mimetype") throw new ArgumentException("Reserved entry name: " + entry.Key);
                if (entry.Value == null) throw new ArgumentException("Null content: " + entry.Key);
                if (entry.Value.LongLength > MaxEntryBytes) throw new InvalidOperationException(entry.Key + " exceeds the " + (MaxEntryBytes >> 20) + " MiB entry budget.");
                total = checked(total + entry.Value.LongLength);
            }
            if (total > MaxTotalBytes) throw new InvalidOperationException("Save exceeds the " + (MaxTotalBytes >> 20) + " MiB file budget.");
            if (files.Count > MaxEntries) throw new InvalidOperationException("Too many entries.");
            var ordered = files.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
            var manifest = new StringBuilder(ManifestHeader).Append('\n');
            foreach (var entry in ordered) manifest.Append(GenerationStore.Hash(entry.Value)).Append(' ').Append(entry.Value.LongLength).Append(' ').Append(entry.Key).Append('\n');
            var zip = new ZipWriter(DateTime.Now);
            zip.Add("mimetype", Encoding.ASCII.GetBytes(MimeType), false);
            zip.Add(ManifestName, Encoding.UTF8.GetBytes(manifest.ToString()), true);
            foreach (var entry in ordered) zip.Add(entry.Key, entry.Value, !entry.Key.EndsWith(".png", StringComparison.Ordinal));
            return zip.Finish();
        }

        /// <summary>最小限の zip 書き出し（zip64 なし、名前は ASCII）。System.IO.Compression の ZipArchive は Unity の Mono で
        /// 「無圧縮」を指定しても deflate 方式で書くため、先頭の mimetype を本当に無圧縮（method 0）で置けない。読む方は
        /// ZipArchive を使う。</summary>
        sealed class ZipWriter
        {
            readonly MemoryStream output = new MemoryStream();
            readonly MemoryStream central = new MemoryStream();
            readonly ushort time, date;
            int count;
            public ZipWriter(DateTime now)
            {
                if (now.Year < 1980) now = new DateTime(1980, 1, 1);
                time = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
                date = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);
            }
            public void Add(string name, byte[] data, bool deflate)
            {
                byte[] stored = data; ushort method = 0;
                if (deflate)
                {
                    using (var compressed = new MemoryStream())
                    {
                        using (var d = new DeflateStream(compressed, CompressionLevel.Optimal, true)) d.Write(data, 0, data.Length);
                        if (compressed.Length < data.Length) { stored = compressed.ToArray(); method = 8; }
                    }
                }
                uint crc = Crc32(data); byte[] nameBytes = Encoding.ASCII.GetBytes(name);
                long offset = output.Position;
                if (offset > uint.MaxValue || output.Length + stored.LongLength > uint.MaxValue) throw new InvalidOperationException("The file is too large for a zip without zip64.");
                var w = new BinaryWriter(output); // little-endian
                w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0); w.Write(method); w.Write(time); w.Write(date);
                w.Write(crc); w.Write((uint)stored.Length); w.Write((uint)data.Length); w.Write((ushort)nameBytes.Length); w.Write((ushort)0);
                w.Write(nameBytes); w.Write(stored); w.Flush();
                var c = new BinaryWriter(central);
                c.Write(0x02014b50u); c.Write((ushort)20); c.Write((ushort)20); c.Write((ushort)0); c.Write(method); c.Write(time); c.Write(date);
                c.Write(crc); c.Write((uint)stored.Length); c.Write((uint)data.Length); c.Write((ushort)nameBytes.Length); c.Write((ushort)0); c.Write((ushort)0);
                c.Write((ushort)0); c.Write((ushort)0); c.Write(0u); c.Write((uint)offset); c.Write(nameBytes); c.Flush();
                count++;
            }
            public byte[] Finish()
            {
                long start = output.Position;
                central.Position = 0; central.CopyTo(output);
                var w = new BinaryWriter(output);
                w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)count); w.Write((ushort)count);
                w.Write((uint)central.Length); w.Write((uint)start); w.Write((ushort)0); w.Flush();
                return output.ToArray();
            }
        }

        /// <summary>.ylp を読んで中身を返す。<paramref name="load"/> を渡すと、それが true を返すエントリーだけを展開して確かめ
        /// （インポーターが合成済みの画像だけを読むため）、他は manifest に載っていることだけを確かめる。</summary>
        public static Dictionary<string, byte[]> Read(byte[] data, Func<string, bool> load = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            CheckMagic(data);
            try
            {
                using (var zip = ZipLayer(() => new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read, false, new UTF8Encoding(false)), "The file is not a readable zip archive"))
                {
                    var entries = ZipLayer(() => zip.Entries, "The file is not a readable zip archive");
                    if (entries.Count > MaxEntries + 2) throw new InvalidDataException("Too many entries.");
                    if (entries.Count < 3 || entries[0].FullName != "mimetype") throw new InvalidDataException("Not a YoluPainter file (the first entry is not 'mimetype').");
                    string mime = Encoding.ASCII.GetString(Extract(entries[0], 256));
                    if (mime != MimeType) throw new InvalidDataException("Not a YoluPainter file (mimetype '" + mime + "').");
                    var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                    // 名前の決まりは manifest の版で決まる（manifest の 1 行目を先に読む）
                    var manifestEntry = entries.Skip(1).FirstOrDefault(e => e.FullName == ManifestName);
                    if (manifestEntry == null) throw new InvalidDataException("The file has no manifest.");
                    byte[] manifestBytes = Extract(manifestEntry, 1024 * 1024);
                    int level = ManifestLevel(manifestBytes);
                    foreach (var entry in entries.Skip(1))
                    {
                        ValidateName(entry.FullName, level);
                        if (entry.FullName == "mimetype" || byName.ContainsKey(entry.FullName)) throw new InvalidDataException("Duplicate entry: " + entry.FullName);
                        byName.Add(entry.FullName, entry);
                    }
                    var manifest = ParseManifest(manifestBytes);
                    foreach (var name in byName.Keys) if (name != ManifestName && !manifest.ContainsKey(name)) throw new InvalidDataException("Entry not listed in the manifest: " + name);
                    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    foreach (var item in manifest)
                    {
                        if (!byName.TryGetValue(item.Key, out var entry)) throw new InvalidDataException("Entry missing from the file: " + item.Key);
                        if (entry.Length != item.Value.Length) throw new InvalidDataException("Length mismatch: " + item.Key);
                        if (load != null && !load(item.Key)) continue;
                        // 中身の正しさは manifest の SHA-256 で決める。zip の CRC-32 はその後に（壊れた zip を他のツールと同じく断るため）。
                        byte[] bytes = Extract(entry, item.Value.Length, checkCrc: false);
                        if (bytes.LongLength != item.Value.Length || GenerationStore.Hash(bytes) != item.Value.Hash) throw new InvalidDataException("Checksum mismatch: " + item.Key);
                        CheckCrc(entry, bytes);
                        files.Add(item.Key, bytes);
                    }
                    return files;
                }
            }
            catch (ZipLayerException ex) { throw new InvalidDataException(ex.Message, ex.InnerException); }
        }

        struct Listed { public string Hash; public long Length; }

        static string[] ManifestLines(byte[] bytes)
        {
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw new InvalidDataException("The manifest is not valid UTF-8."); }
            return text.Split('\n');
        }

        /// <summary>manifest の版の番号（1: 根と composite/ だけ、2: テクスチャセットの置き場も、3: リソースの置き場も）。知らない版は
        /// ここで断る（新しい版は「新しい YoluPainter で書かれた」）。</summary>
        static int ManifestLevel(byte[] bytes)
        {
            var lines = ManifestLines(bytes);
            const string prefix = "YOLUPAINTER-YLP-";
            if (lines[0] == ManifestHeader) return 3;
            if (lines[0] == ManifestHeaderV2) return 2;
            if (lines[0] == ManifestHeaderV1) return 1;
            {
                string number = lines[0].StartsWith(prefix, StringComparison.Ordinal) ? lines[0].Substring(prefix.Length) : "";
                if (number.Length > 0 && number.Length < 9 && number.All(c => c >= '0' && c <= '9') && number[0] != '0' && int.Parse(number, CultureInfo.InvariantCulture) > 3)
                    throw new InvalidDataException("The file was written by a newer YoluPainter (" + lines[0] + ").");
                throw new InvalidDataException("Unsupported manifest.");
            }
        }

        static Dictionary<string, Listed> ParseManifest(byte[] bytes)
        {
            int level = ManifestLevel(bytes);
            var lines = ManifestLines(bytes);
            var result = new Dictionary<string, Listed>(StringComparer.Ordinal); long total = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                var parts = lines[i].Split(' ');
                if (parts.Length != 3 || parts[0].Length != 64 || parts[0].Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
                    || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long length) || length > MaxEntryBytes)
                    throw new InvalidDataException("Malformed manifest entry.");
                ValidateName(parts[2], level);
                if (parts[2] == ManifestName || parts[2] == "mimetype" || result.ContainsKey(parts[2])) throw new InvalidDataException("Duplicate or reserved manifest entry: " + parts[2]);
                total = checked(total + length); if (total > MaxTotalBytes) throw new InvalidDataException("The file exceeds the " + (MaxTotalBytes >> 20) + " MiB read budget.");
                result.Add(parts[2], new Listed { Hash = parts[0], Length = length });
            }
            if (!HasNative(result.Keys, level)) throw new InvalidDataException("The file has no native document.");
            return result;
        }

        /// <summary>宣言された長さを信用しない: 上限を超えて展開せず（zip bomb 対策）、前もって確保する量も小さく抑え、
        /// 展開した長さが宣言どおりで CRC-32 も合うことを確かめる。</summary>
        static byte[] Extract(ZipArchiveEntry entry, long max, bool checkCrc = true)
        {
            if (entry.Length > max) throw new InvalidDataException(entry.FullName + " is larger than allowed.");
            using (var s = ZipLayer(() => entry.Open(), entry.FullName + " could not be opened"))
            using (var output = new MemoryStream((int)Math.Min(entry.Length, 1 << 20)))
            {
                var buffer = new byte[81920]; long total = 0; int n;
                while ((n = ZipLayer(() => s.Read(buffer, 0, buffer.Length), entry.FullName + " could not be decompressed")) > 0)
                {
                    total += n; if (total > entry.Length) throw new InvalidDataException(entry.FullName + " expands beyond its declared size.");
                    output.Write(buffer, 0, n);
                }
                if (total != entry.Length) throw new InvalidDataException(entry.FullName + " is shorter than its declared size.");
                var bytes = output.ToArray();
                if (checkCrc) CheckCrc(entry, bytes);
                return bytes;
            }
        }

        static void CheckCrc(ZipArchiveEntry entry, byte[] bytes)
        { if (Crc32(bytes) != entry.Crc32) throw new InvalidDataException("CRC mismatch: " + entry.FullName); }

        /// <summary>zip の層（System.IO.Compression）が投げた例外を、理由を添えた InvalidDataException に包み直すための印。
        /// 自分の検査が投げる InvalidDataException とは区別する。</summary>
        sealed class ZipLayerException : Exception { public ZipLayerException(string message, Exception inner) : base(message, inner) { } }
        static T ZipLayer<T>(Func<T> action, string what)
        {
            try { return action(); }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is NotSupportedException || ex is ArgumentException || ex is InvalidOperationException)
            { throw new ZipLayerException(what + " (" + ex.Message + ").", ex); }
        }

        /// <summary>先頭が OpenRaster / ODF と同じ識別の形か: オフセット 0 に最初のローカルヘッダー（無圧縮・追加フィールドなし）、
        /// 30 に "mimetype"、38 に MIME タイプ。</summary>
        static void CheckMagic(byte[] data)
        {
            byte[] mime = Encoding.ASCII.GetBytes(MimeType);
            bool ok = data.Length >= 38 + mime.Length
                && data[0] == 0x50 && data[1] == 0x4b && data[2] == 3 && data[3] == 4
                && data[8] == 0 && data[9] == 0               // method 0（無圧縮）
                && U32(data, 18) == mime.Length && U32(data, 22) == mime.Length
                && data[26] == 8 && data[27] == 0             // 名前の長さ 8
                && data[28] == 0 && data[29] == 0             // 追加フィールドなし
                && Encoding.ASCII.GetString(data, 30, 8) == "mimetype";
            for (int i = 0; ok && i < mime.Length; i++) ok = data[38 + i] == mime[i];
            if (!ok) throw new InvalidDataException("Not a YoluPainter file (it does not start with the uncompressed 'mimetype' entry).");
        }
        static uint U32(byte[] d, int o) { return (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24); }

        /// <summary>正本が根か（版 2 から）どれかのテクスチャセットの下にあるか。</summary>
        static bool HasNative(IEnumerable<string> names, int level)
            => names.Any(n => n == NativeName || level >= 2 && YlpFormat.TrySplitSetEntry(n, out _, out var leaf) && leaf == NativeName);

        /// <summary>エントリー名: 英数字と . - _、フォルダは composite/ だけ。版 2 からは、その前に sets/&lt;小文字のハイフン付きの ID&gt;/ を
        /// 1 つ置ける（ID の書き方の違う同じセットを作らない）。版 3 からは、根の resources/ の下に 1 段の名前を置ける（その下に composite/ は
        /// 置けない）。全体で 96 文字まで。</summary>
        static void ValidateName(string name, int level)
        {
            string rest = name, leaf;
            if (level >= 2 && name != null && YlpFormat.TrySplitSetEntry(name, out _, out var inSet)) rest = inSet;
            if (level >= 3 && name != null && name.StartsWith(ResourceFolder, StringComparison.Ordinal)) leaf = name.Substring(ResourceFolder.Length);
            else leaf = rest != null && rest.StartsWith(CompositeFolder, StringComparison.Ordinal) ? rest.Substring(CompositeFolder.Length) : rest;
            if (string.IsNullOrEmpty(leaf) || name.Length > 96 || leaf.Contains("..") || leaf.StartsWith(".", StringComparison.Ordinal) || leaf.Any(c => !(char.IsLetterOrDigit(c) && c < 128 || c == '.' || c == '-' || c == '_')))
                throw new InvalidDataException("Unsafe entry name: " + name);
        }
    }
}
