using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// .ylp の zip 容器（YlpArchive）。書き出しの並び・識別子・圧縮の選び方と、壊れた / 偽った zip を読まないことを確かめる。
    /// 壊れた zip はテスト内の最小の zip 組み立て（<see cref="ZipBuilder"/>）で作る。
    /// </summary>
    public sealed class YlpArchiveTests
    {
        // ───────────── 書き出し: 往復・並び・識別子・圧縮 ─────────────

        [Test] public void RoundTripKeepsEveryEntryByteForByte()
        {
            var files = Sample();
            files["empty.bin"] = new byte[0];
            files["one.bin"] = new byte[] { 7 };
            var read = YlpArchive.Read(YlpArchive.Write(files));
            Assert.That(read.Keys.OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(files.Keys.OrderBy(k => k, StringComparer.Ordinal)));
            foreach (var entry in files) Assert.That(read[entry.Key], Is.EqualTo(entry.Value), entry.Key);
        }

        [Test] public void MimetypeIsTheFirstStoredEntrySoTheMagicSitsAtOffset38()
        {
            byte[] zip = YlpArchive.Write(Sample());
            Assert.That(U32(zip, 0), Is.EqualTo(0x04034b50u), "local header signature");
            Assert.That(U16(zip, 8), Is.EqualTo(0), "method: stored");
            Assert.That(U32(zip, 18), Is.EqualTo((uint)YlpArchive.MimeType.Length), "compressed size");
            Assert.That(U32(zip, 22), Is.EqualTo((uint)YlpArchive.MimeType.Length), "uncompressed size");
            Assert.That(U16(zip, 26), Is.EqualTo(8), "name length");
            Assert.That(U16(zip, 28), Is.EqualTo(0), "no extra field (ODF)");
            Assert.That(Encoding.ASCII.GetString(zip, 30, 8), Is.EqualTo("mimetype"));
            Assert.That(Encoding.ASCII.GetString(zip, 38, YlpArchive.MimeType.Length), Is.EqualTo("application/x-yolupainter"));
        }

        [Test] public void EntriesAreWrittenMimetypeManifestThenOrdinalOrderInBothDirectories()
        {
            var files = Sample();
            files["Zeta.bin"] = new byte[] { 1 }; files["alpha.bin"] = new byte[] { 2 };
            byte[] zip = YlpArchive.Write(files);
            var expected = new[] { "mimetype", YlpArchive.ManifestName }.Concat(files.Keys.OrderBy(k => k, StringComparer.Ordinal)).ToArray();
            Assert.That(LocalHeaders(zip).Select(h => h.Name), Is.EqualTo(expected), "physical order");
            Assert.That(CentralHeaders(zip).Select(h => h.Name), Is.EqualTo(expected), "central directory order");
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                Assert.That(archive.Entries.Select(e => e.FullName), Is.EqualTo(expected), "as ZipArchive sees it");
        }

        [Test] public void PngIsStoredCompressibleIsDeflatedIncompressibleIsStored()
        {
            var files = Sample();
            files["noise.bin"] = Noise(4096, 9);
            byte[] zip = YlpArchive.Write(files);
            var local = LocalHeaders(zip).ToDictionary(h => h.Name);
            Assert.That(local["composite/Color.png"].Method, Is.EqualTo(0), "PNG is already compressed: stored even though this one would deflate well");
            Assert.That(local["composite/Color.png"].CompressedSize, Is.EqualTo(files["composite/Color.png"].Length));
            Assert.That(local["notes.txt"].Method, Is.EqualTo(8), "compressible text is deflated");
            Assert.That(local["notes.txt"].CompressedSize, Is.LessThan(files["notes.txt"].Length));
            Assert.That(local["noise.bin"].Method, Is.EqualTo(0), "deflate would grow random bytes, so they are stored");
            Assert.That(local["noise.bin"].CompressedSize, Is.EqualTo(4096));
        }

        /// <summary>他の zip 実装（unzip, 7-Zip, Python）が読めるよう、CRC・大きさ・オフセットが独立に計算した値と合い、
        /// ローカルヘッダーと中央ディレクトリが食い違わないこと。</summary>
        [Test] public void WrittenZipIsInternallyConsistentForOtherZipReaders()
        {
            Assert.That(ZipBuilder.Crc32(Encoding.ASCII.GetBytes("123456789")), Is.EqualTo(0xCBF43926u), "the test's CRC-32 matches the standard check value");
            var files = Sample(); files["noise.bin"] = Noise(777, 3);
            byte[] zip = YlpArchive.Write(files);
            var local = LocalHeaders(zip); var central = CentralHeaders(zip);
            Assert.That(central.Count, Is.EqualTo(local.Count));
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                for (int i = 0; i < local.Count; i++)
                {
                    var l = local[i]; var c = central[i];
                    Assert.That(c.LocalOffset, Is.EqualTo(l.Offset), l.Name);
                    Assert.That(new object[] { c.Method, c.Crc, c.CompressedSize, c.UncompressedSize, c.Flags }, Is.EqualTo(new object[] { l.Method, l.Crc, l.CompressedSize, l.UncompressedSize, l.Flags }), l.Name);
                    byte[] content;
                    using (var s = archive.Entries[i].Open()) using (var m = new MemoryStream()) { s.CopyTo(m); content = m.ToArray(); }
                    Assert.That(l.Crc, Is.EqualTo(ZipBuilder.Crc32(content)), "CRC-32 of " + l.Name);
                    Assert.That(l.UncompressedSize, Is.EqualTo(content.Length), l.Name);
                    int year = 1980 + (l.Date >> 9), month = (l.Date >> 5) & 15, day = l.Date & 31;
                    Assert.That(year, Is.InRange(2020, 2107)); Assert.That(month, Is.InRange(1, 12)); Assert.That(day, Is.InRange(1, 31));
                }
            }
            int eocd = zip.Length - 22;
            Assert.That(U32(zip, eocd), Is.EqualTo(0x06054b50u), "end record is the last 22 bytes (no comment, nothing appended)");
            Assert.That(U16(zip, eocd + 8), Is.EqualTo(local.Count)); Assert.That(U16(zip, eocd + 10), Is.EqualTo(local.Count));
        }

        [Test] public void ManifestListsHashLengthAndNameOfEveryEntryInOrder()
        {
            var files = Sample();
            using (var archive = new ZipArchive(new MemoryStream(YlpArchive.Write(files)), ZipArchiveMode.Read))
            {
                string text;
                using (var r = new StreamReader(archive.GetEntry(YlpArchive.ManifestName).Open(), Encoding.UTF8)) text = r.ReadToEnd();
                var expected = new StringBuilder(YlpArchive.ManifestHeader).Append('\n');
                foreach (var e in files.OrderBy(x => x.Key, StringComparer.Ordinal)) expected.Append(Line(e.Key, e.Value)).Append('\n');
                Assert.That(text, Is.EqualTo(expected.ToString()));
            }
        }

        [Test] public void LongestAllowedNamesAndAThousandEntriesRoundTrip()
        {
            var files = new Dictionary<string, byte[]> { { YlpArchive.NativeName, new byte[] { 1 } } };
            files["composite/" + new string('c', 86)] = new byte[] { 2 }; // 96 文字ちょうど
            files[new string('r', 96)] = new byte[] { 3 };
            for (int i = files.Count; i < 1000; i++) files["e" + i] = new byte[] { (byte)i };
            var read = YlpArchive.Read(YlpArchive.Write(files));
            Assert.That(read.Count, Is.EqualTo(1000));
            Assert.That(read["composite/" + new string('c', 86)], Is.EqualTo(new byte[] { 2 }));
        }

        // ───────────── 書き出しの拒否 ─────────────

        [Test] public void WriteRefusesAMissingNativeDocument()
        {
            Assert.That(() => YlpArchive.Write(null), Throws.ArgumentException);
            Assert.That(() => YlpArchive.Write(new Dictionary<string, byte[]>()), Throws.ArgumentException);
            Assert.That(() => YlpArchive.Write(new Dictionary<string, byte[]> { { "Document.utpaint", new byte[1] } }), Throws.ArgumentException, "the name is case-sensitive");
        }

        [TestCase("mimetype")]
        [TestCase("manifest.sha256")]
        public void WriteRefusesReservedNames(string name)
        {
            var files = Sample(); files[name] = new byte[] { 1 };
            Assert.That(() => YlpArchive.Write(files), Throws.ArgumentException.With.Message.Contains("Reserved"));
        }

        [Test] public void WriteRefusesNullContent()
        {
            var files = Sample(); files["layer.bin"] = null;
            Assert.That(() => YlpArchive.Write(files), Throws.ArgumentException.With.Message.Contains("Null content"));
            files = Sample(); files[YlpArchive.NativeName] = null;
            Assert.That(() => YlpArchive.Write(files), Throws.ArgumentException.With.Message.Contains("Null content"));
        }

        static readonly string[] UnsafeNames =
        {
            "../x", "/abs", "a\\b", "日本語.bin", "café.bin", ".hidden", "composite/.hidden", "sub/x.bin", "composite/", "composite/sub/x.png",
            "composite/../x", "composite//x.png", "a..b", "", " ", "a b", "a:b", "a\nb", "a\0b", "a\tb", new string('n', 97), "composite/" + new string('c', 87),
            "C:x", "x/", "./x", "COMPOSITE/x.png",
        };

        static string Show(string name) => "'" + string.Concat(name.Select(c => c < 32 ? "\\x" + ((int)c).ToString("x2") : c.ToString())) + "'";

        [Test] public void WriteRefusesUnsafeNames()
        {
            var problems = new List<string>();
            foreach (string name in UnsafeNames)
            {
                var files = Sample(); files[name] = new byte[] { 1 };
                try { YlpArchive.Write(files); problems.Add(Show(name) + ": accepted"); }
                catch (InvalidDataException ex) when (ex.Message.Contains("Unsafe entry name")) { }
                catch (Exception ex) { problems.Add(Show(name) + ": " + ex.GetType().Name + " " + ex.Message); }
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [Test] public void WriteRefusesMoreThanAThousandEntries()
        {
            var files = new Dictionary<string, byte[]> { { YlpArchive.NativeName, new byte[] { 1 } } };
            for (int i = 1; i <= 1000; i++) files["e" + i] = new byte[] { 1 };
            Assert.That(() => YlpArchive.Write(files), Throws.InvalidOperationException.With.Message.Contains("Too many"));
        }

        // ───────────── 読み込みの拒否: zip として壊れている ─────────────

        [Test] public void ReadRefusesNull()
        { Assert.That(() => YlpArchive.Read(null), Throws.ArgumentNullException); }

        [Test] public void ReadRefusesThingsThatAreNotZips()
        {
            foreach (var bytes in new[] { new byte[0], new byte[] { 0x50, 0x4b }, Noise(1000, 4), Encoding.ASCII.GetBytes("application/x-yolupainter"), FakePng(64), new byte[22] })
                Assert.That(() => YlpArchive.Read(bytes), Throws.TypeOf<InvalidDataException>(), "length " + bytes.Length);
        }

        [Test] public void ReadRefusesAnEmptyZip()
        {
            Assert.That(() => YlpArchive.Read(new ZipBuilder().Build()), Throws.TypeOf<InvalidDataException>());
        }

        /// <summary>途中で切れたファイルは、どこで切れても InvalidDataException で拒む（他の例外型が漏れない）。</summary>
        [Test] public void ReadRefusesEveryTruncation()
        {
            byte[] zip = YlpArchive.Write(SmallSample());
            var problems = new List<string>();
            for (int length = 0; length < zip.Length; length++)
            {
                try { YlpArchive.Read(zip.Take(length).ToArray()); problems.Add(length + ": accepted"); }
                catch (InvalidDataException) { }
                catch (Exception ex) { problems.Add(length + ": " + ex.GetType().Name + " " + ex.Message); }
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems.Take(10)));
        }

        /// <summary>1 バイトをどこで変えても、拒む（InvalidDataException）か、元と同じ中身を返すかのどちらか。
        /// 時刻や属性など中身に関わらない欄は変えても読めてよいが、違う中身を黙って返したり、他の例外型を漏らしたりしない。</summary>
        [TestCase(0xFF)]
        [TestCase(0x01)]
        [TestCase(0x80)]
        public void EverySingleByteChangeIsRefusedOrHarmless(int mask)
        {
            var files = SmallSample();
            byte[] zip = YlpArchive.Write(files);
            var problems = new List<string>();
            for (int i = 0; i < zip.Length; i++)
            {
                byte[] bad = (byte[])zip.Clone(); bad[i] ^= (byte)mask;
                try
                {
                    var read = YlpArchive.Read(bad);
                    if (read.Count != files.Count || files.Any(f => !read.TryGetValue(f.Key, out var v) || !v.SequenceEqual(f.Value)))
                        problems.Add("offset " + i + ": accepted with different content");
                }
                catch (InvalidDataException) { }
                catch (Exception ex) { problems.Add("offset " + i + ": " + ex.GetType().Name + " " + ex.Message); }
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems.Take(10)));
        }

        /// <summary>どの位置にも、大きさ・オフセット・個数の欄で問題になりやすい値（0、zip64 の印 0xFFFFFFFF、符号の境目）を
        /// 書き込んでみる。拒むか元と同じ中身を返すかのどちらかで、他の例外型（OverflowException、OutOfMemoryException など）を漏らさない。</summary>
        [Test] public void ExtremeFieldValuesAnywhereAreRefusedOrHarmless()
        {
            var files = SmallSample();
            byte[] zip = YlpArchive.Write(files);
            var patterns = new[] { new byte[] { 0xff, 0xff, 0xff, 0xff }, new byte[] { 0, 0, 0, 0 }, new byte[] { 0xff, 0xff, 0xff, 0x7f }, new byte[] { 0, 0, 0, 0x80 }, new byte[] { 0xff, 0xff }, new byte[] { 0, 0 } };
            var problems = new List<string>();
            foreach (var pattern in patterns)
                for (int i = 0; i + pattern.Length <= zip.Length; i++)
                {
                    byte[] bad = (byte[])zip.Clone(); Array.Copy(pattern, 0, bad, i, pattern.Length);
                    try
                    {
                        var read = YlpArchive.Read(bad);
                        if (read.Count != files.Count || files.Any(f => !read.TryGetValue(f.Key, out var v) || !v.SequenceEqual(f.Value)))
                            problems.Add("offset " + i + " " + BitConverter.ToString(pattern) + ": accepted with different content");
                    }
                    catch (InvalidDataException) { }
                    catch (Exception ex) { problems.Add("offset " + i + " " + BitConverter.ToString(pattern) + ": " + ex.GetType().Name + " " + ex.Message); }
                }
            Assert.That(problems, Is.Empty, string.Join("\n", problems.Take(10)));
        }

        /// <summary>固定の種で、1〜6 バイトを同時に変えた版を 3000 通り読む。</summary>
        [Test] public void RandomMultiByteDamageIsRefusedOrHarmless()
        {
            var files = SmallSample();
            byte[] zip = YlpArchive.Write(files);
            var random = new Random(20261002); var problems = new List<string>();
            for (int round = 0; round < 3000; round++)
            {
                byte[] bad = (byte[])zip.Clone(); int changes = 1 + random.Next(6); var where = new List<int>();
                for (int k = 0; k < changes; k++) { int at = random.Next(bad.Length); bad[at] = (byte)random.Next(256); where.Add(at); }
                try
                {
                    var read = YlpArchive.Read(bad);
                    if (read.Count != files.Count || files.Any(f => !read.TryGetValue(f.Key, out var v) || !v.SequenceEqual(f.Value)))
                        problems.Add("round " + round + " at " + string.Join(",", where) + ": accepted with different content");
                }
                catch (InvalidDataException) { }
                catch (Exception ex) { problems.Add("round " + round + " at " + string.Join(",", where) + ": " + ex.GetType().Name + " " + ex.Message); }
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems.Take(10)));
        }

        // ───────────── 読み込みの拒否: mimetype ─────────────

        [Test] public void TheTestBuilderMakesFilesTheReaderAccepts()
        {
            var files = Sample();
            var read = YlpArchive.Read(StandardYlp(files).Build());
            Assert.That(read.Count, Is.EqualTo(files.Count));
            foreach (var e in files) Assert.That(read[e.Key], Is.EqualTo(e.Value));
        }

        [Test] public void ReadRefusesAMissingMimetype()
        {
            var z = StandardYlp(Sample()); z.Entries.RemoveAt(0);
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("mimetype"));
        }

        [Test] public void ReadRefusesAMimetypeThatIsNotFirst()
        {
            var z = StandardYlp(Sample()); var mime = z.Entries[0]; z.Entries.RemoveAt(0); z.Entries.Insert(2, mime);
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("mimetype"));
        }

        [TestCase("application/zip")]
        [TestCase("application/x-yolupainter\n")]
        [TestCase("APPLICATION/X-YOLUPAINTER")]
        [TestCase("application/x-yolupainter2")]
        [TestCase("")]
        public void ReadRefusesAWrongMimetype(string mime)
        {
            var z = StandardYlp(Sample()); z.Entries[0] = ZipBuilder.Stored("mimetype", Encoding.ASCII.GetBytes(mime));
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("mimetype"));
        }

        [Test] public void ReadRefusesASecondMimetype()
        {
            var z = StandardYlp(Sample()); z.Entries.Add(ZipBuilder.Stored("mimetype", Encoding.ASCII.GetBytes(YlpArchive.MimeType)));
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Duplicate"));
        }

        [Test] public void ReadRefusesAMimetypeDeclaredLargerThanItsBudget()
        {
            var z = StandardYlp(Sample()); z.Entries[0].UncompressedSize = 300;
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>());
            z.Entries[0].UncompressedSize = uint.MaxValue;
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>());
        }

        /// <summary>
        /// 識別は ODF / OpenRaster と同じく「先頭 30 バイト目に無圧縮・拡張フィールド無しの mimetype、38 バイト目に中身」。
        /// 中央ディレクトリの並びだけ先頭でも、実体が先頭でない・圧縮されている・拡張フィールドがあるファイルは、
        /// 38 バイト目に識別子が無いので .ylp ではない。
        /// </summary>
        [TestCase("deflated")]
        [TestCase("extra-field")]
        [TestCase("not-physically-first")]
        [TestCase("data-before-mimetype")]
        public void ReadRefusesFilesWhoseMagicIsNotAtOffset38(string variant)
        {
            var z = StandardYlp(Sample());
            byte[] mime = Encoding.ASCII.GetBytes(YlpArchive.MimeType);
            switch (variant)
            {
                case "deflated": z.Entries[0] = ZipBuilder.Deflated("mimetype", mime, force: true); break;
                case "extra-field": z.Entries[0].LocalExtra = new byte[] { 0xfe, 0xca, 4, 0, 1, 2, 3, 4 }; break;
                case "not-physically-first":
                    var first = z.Entries[0]; z.Entries.RemoveAt(0); z.Entries.Add(first);
                    z.CentralOrder = new[] { z.Entries.Count - 1 }.Concat(Enumerable.Range(0, z.Entries.Count - 1)).ToArray();
                    break;
                case "data-before-mimetype":
                    // 中央ディレクトリに載らないローカルエントリーを先頭に置く（ストリームで読む zip 実装にはこれが見える）
                    z.Hidden.Add(ZipBuilder.Stored("hidden.bin", Encoding.ASCII.GetBytes("not listed anywhere")));
                    break;
            }
            byte[] bytes = z.Build();
            Assume.That(Encoding.ASCII.GetString(bytes, 38, mime.Length) == YlpArchive.MimeType, Is.False, "the crafted file really lacks the magic");
            Assert.That(() => YlpArchive.Read(bytes), Throws.TypeOf<InvalidDataException>());
        }

        // ───────────── 読み込みの拒否: manifest ─────────────

        [Test] public void ReadRefusesAMissingManifest()
        {
            var z = StandardYlp(Sample()); z.Entries.RemoveAt(1);
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("no manifest"));
        }

        [Test] public void ReadRefusesANewerManifestNamingANewerYoluPainter()
        {
            var files = Sample();
            var z = StandardYlp(files, "YOLUPAINTER-YLP-2");
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("newer YoluPainter"));
        }

        [TestCase("")]
        [TestCase("DOTPAINT-MANIFEST-1")]
        [TestCase("yolupainter-ylp-1")]
        public void ReadRefusesAnUnknownManifestHeader(string header)
        {
            var z = StandardYlp(Sample(), header);
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unsupported manifest"));
        }

        [Test] public void ReadRefusesAnEntryNotListedInTheManifest()
        {
            var z = StandardYlp(Sample()); z.Entries.Add(ZipBuilder.Stored("extra.bin", new byte[] { 1, 2, 3 }));
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("not listed").And.Message.Contains("extra.bin"));
        }

        [Test] public void ReadRefusesAManifestEntryMissingFromTheZip()
        {
            var files = Sample();
            var z = StandardYlp(files); z.Entries.RemoveAll(e => e.Name == "notes.txt");
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("missing").And.Message.Contains("notes.txt"));
        }

        [Test] public void ReadRefusesAManifestWithoutTheNativeDocument()
        {
            var files = Sample(); files.Remove(YlpArchive.NativeName);
            Assert.That(() => YlpArchive.Read(StandardYlp(files).Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("native document"));
        }

        [Test] public void ReadRefusesAManifestLengthThatDisagrees()
        {
            var files = Sample();
            var z = StandardYlp(files, lines: ManifestLines(files, "notes.txt", (h, l) => h + " " + (l + 1)));
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Length mismatch"));
        }

        [Test] public void ReadRefusesACentralDirectorySizeThatDisagreesWithTheManifest()
        {
            var z = StandardYlp(Sample()); var doc = z.Entries.Single(e => e.Name == YlpArchive.NativeName); doc.UncompressedSize -= 1;
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>());
        }

        [Test] public void ReadRefusesAManifestHashThatDisagrees()
        {
            var files = Sample();
            var z = StandardYlp(files, lines: ManifestLines(files, YlpArchive.NativeName, (h, l) => GenerationStore.Hash(new byte[] { 1 }) + " " + l));
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Checksum mismatch"));
        }

        static readonly string[] MalformedManifestLines =
        {
            "{h} {l}",                 // 欄が足りない
            "{h} {l} {n} extra",       // 欄が多い
            "{h}0 {l} {n}",            // ハッシュが 65 文字
            "{h7} {l} {n}",            // ハッシュが 63 文字
            "{hx} {l} {n}",            // 16 進でない
            "{h} -1 {n}",              // 負の長さ
            "{h} x {n}",               // 数でない長さ
            "{h} 536870913 {n}",       // 512 MiB を超える宣言
            "{h} {l} {n}\n{h} {l} {n}", // 同じ名前が 2 回
            "{h} {l} mimetype",        // 予約名
            "{h} {l} manifest.sha256", // 予約名
            "{h} {l} ../{n}",          // 危ない名前
            "{h}  {l} {n}",            // 空の欄
        };

        [Test] public void ReadRefusesMalformedManifestLines()
        {
            var problems = new List<string>();
            foreach (string template in MalformedManifestLines)
            {
                var files = Sample(); byte[] doc = files[YlpArchive.NativeName]; string h = GenerationStore.Hash(doc);
                string line = template.Replace("{hx}", "z" + h.Substring(1)).Replace("{h7}", h.Substring(1)).Replace("{h}", h)
                    .Replace("{l}", doc.Length.ToString()).Replace("{n}", YlpArchive.NativeName);
                var lines = files.Where(f => f.Key != YlpArchive.NativeName).Select(f => Line(f.Key, f.Value)).Concat(new[] { line });
                try { YlpArchive.Read(StandardYlp(files, lines: lines).Build()); problems.Add(Show(template) + ": accepted"); }
                catch (InvalidDataException) { }
                catch (Exception ex) { problems.Add(Show(template) + ": " + ex.GetType().Name + " " + ex.Message); }
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        [Test] public void ReadRefusesAManifestThatIsNotUtf8()
        {
            var files = Sample(); var z = StandardYlp(files);
            var text = new List<byte>(Encoding.UTF8.GetBytes(YlpArchive.ManifestHeader + "\n" + string.Join("\n", files.Select(f => Line(f.Key, f.Value))) + "\n"));
            text.AddRange(new byte[] { 0xff, 0xfe, (byte)'\n' });
            z.Entries[1] = ZipBuilder.Stored(YlpArchive.ManifestName, text.ToArray());
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>());
        }

        [Test] public void ReadRefusesManifestTotalsAndDeclaredSizesAboveTheBudget()
        {
            var files = Sample();
            // 合計の宣言が 768 MiB を超える（1 つずつは 512 MiB 以下）
            var lines = files.Select(f => Line(f.Key, f.Value)).Concat(new[] { GenerationStore.Hash(new byte[0]) + " 419430400 big1.bin", GenerationStore.Hash(new byte[0]) + " 419430400 big2.bin" });
            Assert.That(() => YlpArchive.Read(StandardYlp(files, lines: lines).Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("budget"));
            // manifest 自体が 1 MiB を超えると宣言する
            var z = StandardYlp(files); z.Entries[1].UncompressedSize = 2 * 1024 * 1024;
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("larger than allowed"));
            z.Entries[1].UncompressedSize = uint.MaxValue;
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>());
        }

        // ───────────── 読み込みの拒否: 中身 ─────────────

        /// <summary>無圧縮のエントリーの 1 バイトを変える。zip の CRC も合わなくなるので、どちらの層が見つけても拒めばよいが、
        /// どちらが報告したかをメッセージで固定しておく（Unity の Mono の ZipArchive は CRC を確かめないので SHA-256 が見つける）。</summary>
        [Test] public void AFlippedByteInsideAStoredEntryIsRefused()
        {
            var files = Sample();
            byte[] zip = YlpArchive.Write(files);
            var png = LocalHeaders(zip).Single(h => h.Name == "composite/Color.png");
            Assert.That(png.Method, Is.EqualTo(0));
            zip[png.DataOffset + 20] ^= 0x40;
            Assert.That(() => YlpArchive.Read(zip), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Checksum mismatch: composite/Color.png"));
        }

        [Test] public void AFlippedByteInsideADeflatedEntryIsRefused()
        {
            var files = Sample();
            byte[] zip = YlpArchive.Write(files);
            var notes = LocalHeaders(zip).Single(h => h.Name == "notes.txt");
            Assert.That(notes.Method, Is.EqualTo(8));
            for (int at = 0; at < notes.CompressedSize; at++)
            {
                byte[] bad = (byte[])zip.Clone(); bad[notes.DataOffset + at] ^= 0x10;
                Assert.That(() => YlpArchive.Read(bad), Throws.TypeOf<InvalidDataException>(), "byte " + at);
            }
        }

        [Test] public void ReadRefusesAWrongZipCrcEvenWhenTheContentMatchesTheManifest()
        {
            // zip として壊れている（他の zip 実装は CRC エラーで読まない）。中身は manifest と合う。
            var z = StandardYlp(Sample()); z.Entries.Single(e => e.Name == YlpArchive.NativeName).Crc ^= 1;
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>());
        }

        [TestCase("document.utpaint")]
        [TestCase("manifest.sha256")]
        [TestCase("composite/Color.png")]
        public void ReadRefusesDuplicateNames(string name)
        {
            var files = Sample(); var z = StandardYlp(files);
            var original = z.Entries.Single(e => e.Name == name);
            z.Entries.Add(ZipBuilder.Stored(name, original.Data));
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Duplicate"));
        }

        [Test] public void ReadRefusesUnsafeNamesInTheZip()
        {
            var problems = new List<string>();
            foreach (string name in UnsafeNames.Where(n => n.Length > 0))
            {
                var files = Sample(); var z = StandardYlp(files);
                byte[] data = { 1, 2, 3 };
                z.Entries.Add(ZipBuilder.Stored(name, data));
                // manifest にも載せて「載っていない」で落ちないようにする（空白・改行を含む名前は manifest の行に書けないので書かない）
                var lines = files.Select(f => Line(f.Key, f.Value)).ToList();
                if (!name.Contains(" ") && !name.Contains("\n")) lines.Add(Line(name, data));
                z.Entries[1] = ZipBuilder.Deflated(YlpArchive.ManifestName, Manifest(YlpArchive.ManifestHeader, lines));
                try { YlpArchive.Read(z.Build()); problems.Add(Show(name) + ": accepted"); }
                catch (InvalidDataException ex) when (ex.Message.Contains("Unsafe entry name")) { }
                catch (Exception ex) { problems.Add(Show(name) + ": " + ex.GetType().Name + " " + ex.Message); }
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        /// <summary>小さく宣言して大きく膨らむ（zip bomb）。宣言どおりの長さとその長さ分のハッシュを manifest に書いておき、
        /// 宣言の長さで切って読む実装でも通らないことを確かめる。</summary>
        [Test] public void ReadRefusesAnEntryThatInflatesFarBeyondItsDeclaredSize()
        {
            var files = Sample();
            byte[] huge = new byte[16 * 1024 * 1024];
            byte[] declared = huge.Take(64).ToArray();
            files[YlpArchive.NativeName] = declared;
            var z = StandardYlp(files);
            var bomb = ZipBuilder.Deflated(YlpArchive.NativeName, huge, force: true);
            Assert.That(bomb.Payload.Length, Is.LessThan(64 * 1024), "the crafted payload is tiny");
            bomb.Crc = ZipBuilder.Crc32(declared); bomb.UncompressedSize = 64;
            z.Entries[z.Entries.FindIndex(e => e.Name == YlpArchive.NativeName)] = bomb;
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("beyond its declared size"));
        }

        /// <summary>中身は 10 バイトなのに中央ディレクトリと manifest で 32 MiB と宣言する（予算内の嘘）。数百バイトのファイルで
        /// 宣言どおりの大きさを先に確保させられないこと（宣言を信用しない）。確保が起きれば GC.GetTotalMemory に 32 MiB 現れる。</summary>
        [Test] public void ReadDoesNotPreallocateTheDeclaredSizeOfATinyEntry()
        {
            const uint declared = 32u << 20;
            var files = Sample();
            var lines = ManifestLines(files, YlpArchive.NativeName, (h, l) => h + " " + declared);
            var z = StandardYlp(files, lines: lines);
            z.Entries.Single(e => e.Name == YlpArchive.NativeName).UncompressedSize = declared;
            byte[] tiny = z.Build();
            Assert.That(tiny.Length, Is.LessThan(16 * 1024));
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetTotalMemory(false);
            Assert.That(() => YlpArchive.Read(tiny), Throws.TypeOf<InvalidDataException>());
            long grown = GC.GetTotalMemory(false) - before;
            GC.Collect();
            Assert.That(grown, Is.LessThan(8L << 20), "bytes allocated while refusing a " + tiny.Length + "-byte file");
        }

        [Test] public void ReadRefusesAManifestThatInflatesBeyondItsBudget()
        {
            var files = Sample(); var z = StandardYlp(files);
            var lines = files.Select(f => Line(f.Key, f.Value)).ToList();
            byte[] manifest = Manifest(YlpArchive.ManifestHeader, lines).Concat(Enumerable.Repeat((byte)'\n', 3 * 1024 * 1024)).ToArray();
            var bomb = ZipBuilder.Deflated(YlpArchive.ManifestName, manifest, force: true); bomb.UncompressedSize = 1000;
            z.Entries[1] = bomb;
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>().With.Message.Contains("beyond its declared size"));
        }

        /// <summary>manifest も zip のエントリーなので、宣言した大きさと実際の大きさが違えば壊れた zip として拒む。</summary>
        [Test] public void ReadRefusesAManifestWhoseDeclaredSizeIsWrong()
        {
            var files = Sample(); var z = StandardYlp(files);
            z.Entries[1].UncompressedSize = 10;
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>());
        }

        [Test] public void ReadRefusesAStoredEntryWhoseStoredBytesAreLongerThanDeclared()
        {
            var files = Sample(); var z = StandardYlp(files);
            var doc = z.Entries.Single(e => e.Name == YlpArchive.NativeName);
            doc.Payload = doc.Data.Concat(new byte[100]).ToArray(); doc.CompressedSize = (uint)doc.Payload.Length;
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>());
        }

        [Test] public void ReadRefusesAnEntryWithAnUnsupportedMethod()
        {
            var z = StandardYlp(Sample()); z.Entries.Single(e => e.Name == YlpArchive.NativeName).Method = 12; // bzip2
            Assert.That(() => YlpArchive.Read(z.Build()), Throws.TypeOf<InvalidDataException>());
        }

        // ───────────── load で絞った読み込み ─────────────

        [Test] public void LoadFilterReturnsOnlyRequestedEntriesAndVerifiesThem()
        {
            var files = Sample(); files["composite/Height.png"] = FakePng(40);
            byte[] zip = YlpArchive.Write(files);
            var read = YlpArchive.Read(zip, n => n.StartsWith(YlpArchive.CompositeFolder, StringComparison.Ordinal));
            Assert.That(read.Keys.OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(new[] { "composite/Color.png", "composite/Height.png" }));
            Assert.That(read["composite/Color.png"], Is.EqualTo(files["composite/Color.png"]));
            Assert.That(YlpArchive.Read(zip, n => false), Is.Empty);

            var png = LocalHeaders(zip).Single(h => h.Name == "composite/Height.png");
            zip[png.DataOffset + 10] ^= 1;
            Assert.That(() => YlpArchive.Read(zip, n => n == "composite/Height.png"), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Checksum mismatch"));
        }

        /// <summary>意図したトレードオフ: load で選ばなかったエントリーは展開もハッシュ確認もしないので、その中身が壊れていても
        /// 絞った読み込みは成功する（長さと manifest への記載だけは確かめる）。全部読めば拒む。</summary>
        [Test] public void LoadFilterDoesNotDetectCorruptionInUnrequestedEntries_DeliberateTradeOff()
        {
            var files = Sample();
            byte[] zip = YlpArchive.Write(files);
            var doc = LocalHeaders(zip).Single(h => h.Name == YlpArchive.NativeName);
            Assert.That(doc.Method, Is.EqualTo(0), "noise is stored, so a flipped byte keeps the length");
            zip[doc.DataOffset + 5] ^= 0xff;
            var partial = YlpArchive.Read(zip, n => n == "composite/Color.png");
            Assert.That(partial["composite/Color.png"], Is.EqualTo(files["composite/Color.png"]), "corruption in the unrequested native document is not detected");
            Assert.That(() => YlpArchive.Read(zip), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Checksum mismatch: document.utpaint"));
        }

        [Test] public void LoadFilterStillChecksListingAndLengthsOfUnrequestedEntries()
        {
            var files = Sample();
            Func<string, bool> onlyComposite = n => n.StartsWith(YlpArchive.CompositeFolder, StringComparison.Ordinal);
            var unlisted = StandardYlp(files); unlisted.Entries.Add(ZipBuilder.Stored("extra.bin", new byte[] { 1 }));
            Assert.That(() => YlpArchive.Read(unlisted.Build(), onlyComposite), Throws.TypeOf<InvalidDataException>().With.Message.Contains("not listed"));
            var longer = StandardYlp(files, lines: ManifestLines(files, "notes.txt", (h, l) => h + " " + (l + 1)));
            Assert.That(() => YlpArchive.Read(longer.Build(), onlyComposite), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Length mismatch"));
            var missing = StandardYlp(files); missing.Entries.RemoveAll(e => e.Name == YlpArchive.NativeName);
            Assert.That(() => YlpArchive.Read(missing.Build(), onlyComposite), Throws.TypeOf<InvalidDataException>().With.Message.Contains("missing"));
        }

        // ───────────── 補助 ─────────────

        internal static Dictionary<string, byte[]> Sample() => new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            { YlpArchive.NativeName, Noise(3000, 1) },
            { "composite/Color.png", FakePng(2000) },
            { "notes.txt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compressible text ", 200))) },
        };

        static Dictionary<string, byte[]> SmallSample() => new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            { YlpArchive.NativeName, Noise(120, 2) },
            { "composite/Color.png", FakePng(40) },
            { "notes.txt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("abc ", 60))) },
        };

        internal static byte[] Noise(int length, int seed) { var b = new byte[length]; new Random(seed).NextBytes(b); return b; }

        /// <summary>PNG の署名の後ろが 0 だけ（deflate すればよく縮むが、名前が .png なので無圧縮で入るはず）。</summary>
        static byte[] FakePng(int length)
        {
            var b = new byte[length]; new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(b, 0); return b;
        }

        static string Line(string name, byte[] data) => GenerationStore.Hash(data) + " " + data.LongLength + " " + name;

        static IEnumerable<string> ManifestLines(Dictionary<string, byte[]> files, string target, Func<string, long, string> hashAndLength)
            => files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key == target ? hashAndLength(GenerationStore.Hash(f.Value), f.Value.LongLength) + " " + f.Key : Line(f.Key, f.Value));

        static byte[] Manifest(string header, IEnumerable<string> lines)
            => Encoding.UTF8.GetBytes(header + "\n" + string.Concat(lines.Select(l => l + "\n")));

        /// <summary>YlpArchive.Write と同じ並び（mimetype、manifest、中身）の .ylp をテストの組み立てで作る。壊すのは呼び出し側。</summary>
        static ZipBuilder StandardYlp(Dictionary<string, byte[]> files, string header = YlpArchive.ManifestHeader, IEnumerable<string> lines = null)
        {
            var ordered = files.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
            var z = new ZipBuilder();
            z.Entries.Add(ZipBuilder.Stored("mimetype", Encoding.ASCII.GetBytes(YlpArchive.MimeType)));
            z.Entries.Add(ZipBuilder.Deflated(YlpArchive.ManifestName, Manifest(header, lines ?? ordered.Select(f => Line(f.Key, f.Value)))));
            foreach (var f in ordered) z.Entries.Add(f.Key.EndsWith(".png", StringComparison.Ordinal) ? ZipBuilder.Stored(f.Key, f.Value) : ZipBuilder.Deflated(f.Key, f.Value));
            return z;
        }

        static ushort U16(byte[] b, long at) => (ushort)(b[at] | b[at + 1] << 8);
        static uint U32(byte[] b, long at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

        sealed class Header
        {
            public string Name; public ushort Method, Flags, Date; public uint Crc, CompressedSize, UncompressedSize; public long Offset, DataOffset, LocalOffset;
        }

        /// <summary>ファイルの先頭からローカルヘッダーを順にたどる（実体の並び）。</summary>
        static List<Header> LocalHeaders(byte[] zip)
        {
            var result = new List<Header>(); long at = 0;
            while (at + 30 <= zip.Length && U32(zip, at) == 0x04034b50u)
            {
                var h = new Header
                {
                    Offset = at, Flags = U16(zip, at + 6), Method = U16(zip, at + 8), Date = U16(zip, at + 12), Crc = U32(zip, at + 14),
                    CompressedSize = U32(zip, at + 18), UncompressedSize = U32(zip, at + 22),
                };
                int nameLength = U16(zip, at + 26), extraLength = U16(zip, at + 28);
                h.Name = Encoding.UTF8.GetString(zip, (int)at + 30, nameLength);
                h.DataOffset = at + 30 + nameLength + extraLength;
                result.Add(h); at = h.DataOffset + h.CompressedSize;
            }
            return result;
        }

        /// <summary>末尾の終端レコード（コメント無し）から中央ディレクトリを読む。</summary>
        static List<Header> CentralHeaders(byte[] zip)
        {
            int eocd = zip.Length - 22;
            Assert.That(U32(zip, eocd), Is.EqualTo(0x06054b50u));
            int count = U16(zip, eocd + 10); long at = U32(zip, eocd + 16);
            var result = new List<Header>();
            for (int i = 0; i < count; i++)
            {
                Assert.That(U32(zip, at), Is.EqualTo(0x02014b50u));
                var h = new Header
                {
                    Flags = U16(zip, at + 8), Method = U16(zip, at + 10), Date = U16(zip, at + 14), Crc = U32(zip, at + 16),
                    CompressedSize = U32(zip, at + 20), UncompressedSize = U32(zip, at + 24), LocalOffset = U32(zip, at + 42),
                };
                int n = U16(zip, at + 28), x = U16(zip, at + 30), c = U16(zip, at + 32);
                h.Name = Encoding.UTF8.GetString(zip, (int)at + 46, n);
                result.Add(h); at += 46 + n + x + c;
            }
            return result;
        }
    }

    /// <summary>
    /// 壊れた / 偽った zip を作るための最小の zip 組み立て。ローカルヘッダーの実体の並び（<see cref="Entries"/>）と
    /// 中央ディレクトリの並び（<see cref="CentralOrder"/>）を別々に決められ、方式・CRC・宣言する大きさ・拡張フィールドを
    /// 自由に偽れる。<see cref="Hidden"/> は中央ディレクトリに載せないローカルエントリーで、ファイルの先頭に置く。
    /// </summary>
    internal sealed class ZipBuilder
    {
        internal sealed class Entry
        {
            public string Name; public byte[] Data, Payload, LocalExtra = new byte[0];
            public ushort Method, Flags; public uint Crc, CompressedSize, UncompressedSize;
        }

        public readonly List<Entry> Entries = new List<Entry>();
        public readonly List<Entry> Hidden = new List<Entry>();
        public int[] CentralOrder;

        public static Entry Stored(string name, byte[] data) => new Entry
        {
            Name = name, Data = data, Payload = data, Method = 0, Crc = Crc32(data), CompressedSize = (uint)data.Length, UncompressedSize = (uint)data.Length,
            Flags = (ushort)(name.Any(ch => ch >= 128) ? 0x800 : 0),
        };

        /// <summary>raw deflate で入れる。<paramref name="force"/> が無ければ、縮まないときは無圧縮にする（YlpArchive.Write と同じ）。</summary>
        public static Entry Deflated(string name, byte[] data, bool force = false)
        {
            byte[] packed;
            using (var m = new MemoryStream())
            {
                using (var d = new DeflateStream(m, System.IO.Compression.CompressionLevel.Optimal, true)) d.Write(data, 0, data.Length);
                packed = m.ToArray();
            }
            if (!force && packed.Length >= data.Length) return Stored(name, data);
            var e = Stored(name, data); e.Payload = packed; e.Method = 8; e.CompressedSize = (uint)packed.Length; return e;
        }

        public byte[] Build()
        {
            var output = new MemoryStream(); var w = new BinaryWriter(output);
            foreach (var e in Hidden) WriteLocal(w, e);
            var offsets = new long[Entries.Count];
            for (int i = 0; i < Entries.Count; i++) { offsets[i] = output.Position; WriteLocal(w, Entries[i]); }
            long start = output.Position;
            foreach (int i in CentralOrder ?? Enumerable.Range(0, Entries.Count).ToArray())
            {
                var e = Entries[i]; byte[] name = Encoding.UTF8.GetBytes(e.Name);
                w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write(e.Flags); w.Write(e.Method); w.Write((ushort)0); w.Write(DosDate);
                w.Write(e.Crc); w.Write(e.CompressedSize); w.Write(e.UncompressedSize); w.Write((ushort)name.Length); w.Write((ushort)0); w.Write((ushort)0);
                w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write((uint)offsets[i]); w.Write(name);
            }
            long length = output.Position - start; int count = (CentralOrder ?? offsets.Select((_, i) => i).ToArray()).Length;
            w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)count); w.Write((ushort)count);
            w.Write((uint)length); w.Write((uint)start); w.Write((ushort)0); w.Flush();
            return output.ToArray();
        }

        const ushort DosDate = ((2020 - 1980) << 9) | (1 << 5) | 1;

        static void WriteLocal(BinaryWriter w, Entry e)
        {
            byte[] name = Encoding.UTF8.GetBytes(e.Name);
            w.Write(0x04034b50u); w.Write((ushort)20); w.Write(e.Flags); w.Write(e.Method); w.Write((ushort)0); w.Write(DosDate);
            w.Write(e.Crc); w.Write(e.CompressedSize); w.Write(e.UncompressedSize); w.Write((ushort)name.Length); w.Write((ushort)e.LocalExtra.Length);
            w.Write(name); w.Write(e.LocalExtra); w.Write(e.Payload);
        }

        static uint[] table;
        public static uint Crc32(byte[] data)
        {
            if (table == null)
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++) { uint v = i; for (int k = 0; k < 8; k++) v = (v & 1) != 0 ? 0xEDB88320u ^ (v >> 1) : v >> 1; t[i] = v; }
                table = t;
            }
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }
}
