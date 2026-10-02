using System;
using System.IO;
using System.Linq;
using System.Text;
using Dot.TexturePainter.Core.Psd;
using NUnit.Framework;

namespace Dot.TexturePainter.Tests
{
    public sealed class PsdTests
    {
        private static PsdDocument Document()
        {
            var d = new PsdDocument { Width = 2, Height = 2 };
            d.Layers.Add(new PsdRasterLayer { Id = 7, Name = "色 🎨", Width = 2, Height = 2,
                PixelsRgba = new byte[] { 255, 0, 0, 255, 0, 255, 0, 128, 12, 34, 56, 0, 20, 40, 60, 80 } });
            return d;
        }

        [Test] public void WriterRoundTripsUnicodeIdentityAndInvisibleRgb()
        {
            var original = Document(); var bytes = PsdCodec.Write(original); var result = PsdCodec.Read(bytes);
            Assert.That(result.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), Diagnostics(result));
            Assert.That(result.Document.Layers[0].Name, Is.EqualTo("色 🎨"));
            Assert.That(result.Document.Layers[0].Id, Is.EqualTo(7));
            Assert.That(result.Document.Layers[0].PixelsRgba, Is.EqualTo(original.Layers[0].PixelsRgba));
            Assert.That(PsdCodec.WriteEdited(result, result.Document), Is.EqualTo(bytes));
        }

        [Test] public void LayerOrderOpacityVisibilityAndOffCanvasPixelsRoundTrip()
        {
            var d = Document();
            d.Layers.Insert(0, new PsdRasterLayer { Id = 33, Name = "hidden", Width = 1, Height = 1,
                Left = -100, Top = 1, Visible = false, Opacity = 101, PixelsRgba = new byte[] { 90, 80, 70, 255 } });
            var result = PsdCodec.Read(PsdCodec.Write(d));
            Assert.That(result.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), Diagnostics(result));
            Assert.That(result.Document.Layers.Select(x => x.Id), Is.EqualTo(new[] { 33, 7 }));
            Assert.That(result.Document.Layers[0].Visible, Is.False);
            Assert.That(result.Document.Layers[0].Opacity, Is.EqualTo(101));
            Assert.That(result.Document.Layers[0].Left, Is.EqualTo(-100));
            Assert.That(result.Document.Layers[0].PixelsRgba, Is.EqualTo(d.Layers[0].PixelsRgba));
        }

        [TestCase(false)] [TestCase(true)] public void IndependentlyConstructedRawAndRleFixturesDecode(bool rle)
        {
            var result = PsdCodec.Read(IndependentFixture(rle));
            Assert.That(result.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), Diagnostics(result));
            Assert.That(result.Document.Layers[0].Id, Is.EqualTo(77));
            Assert.That(result.Document.Layers[0].PixelsRgba, Is.EqualTo(Document().Layers[0].PixelsRgba));
        }

        [TestCase("curv")] [TestCase("TySh")] [TestCase("SoLd")] [TestCase("vmsk")]
        [TestCase("lsct")] [TestCase("lfx2")] [TestCase("zzzz")] [TestCase("iOpa")]
        public void UnsupportedTagPreservesWholeOriginalAndBlocksAnyRewrite(string tag)
        {
            var source = IndependentFixture(false, tag); var result = PsdCodec.Read(source);
            Assert.That(result.Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
            Assert.That(result.Document, Is.Null);
            Assert.That(result.CopyOriginalBytes(), Is.EqualTo(source));
            Assert.That(result.Diagnostics.Any(x => x.Message.Contains(tag)), Is.True);
            Assert.Throws<InvalidOperationException>(() => PsdCodec.WriteEdited(result, Document()));
        }

        [Test] public void OriginalRetentionIsNotAliasedToCallerArrays()
        {
            byte[] source = IndependentFixture(false, "TySh"), expected = (byte[])source.Clone();
            var result = PsdCodec.Read(source); source[0] = 0;
            byte[] copy = result.CopyOriginalBytes(); copy[1] = 0;
            Assert.That(result.CopyOriginalBytes(), Is.EqualTo(expected));
        }

        [Test] public void NonNormalBlendClippingAndLocksFailClosed()
        {
            byte[] original = PsdCodec.Write(Document()); int norm = Find(original, "norm");
            foreach (int offset in new[] { norm, norm + 5, norm + 6 })
            {
                byte[] bytes = (byte[])original.Clone(); bytes[offset] = 1;
                Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
            }
        }

        [Test] public void DuplicateAndMissingIdsAreNeverSilentlyRepaired()
        {
            var d = Document(); var layer = Document().Layers[0]; layer.Id = 8; d.Layers.Add(layer);
            var bytes = PsdCodec.Write(d); int first = Find(bytes, "lyid"), second = Find(bytes, "lyid", first + 4);
            Buffer.BlockCopy(bytes, first + 8, bytes, second + 8, 4);
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
            bytes = PsdCodec.Write(Document()); int id = Find(bytes, "lyid"); bytes[id] = (byte)'x';
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
        }

        [Test] public void MergedMismatchAndMissingCompositeBlockEdits()
        {
            var bytes = PsdCodec.Write(Document()); bytes[bytes.Length - 16] = 0;
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
            bytes = PsdCodec.Write(Document()); Array.Resize(ref bytes, bytes.Length - 18);
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
        }

        [Test] public void SourceDecodedMetadataAndOutputLimitsAreEnforced()
        {
            byte[] bytes = IndependentFixture(false);
            var sourceResult = PsdCodec.Read(bytes, new PsdLimits { MaxSourceBytes = 26 });
            Assert.That(sourceResult.Mode, Is.EqualTo(PsdCompatibilityMode.Rejected));
            Assert.That(sourceResult.HasOriginalBytes, Is.False);
            Assert.That(PsdCodec.Read(bytes, new PsdLimits { MaxDecodedBytes = 4 }).Mode, Is.EqualTo(PsdCompatibilityMode.Rejected));
            Assert.That(PsdCodec.Read(bytes, new PsdLimits { MaxMetadataBytes = 1 }).Mode, Is.EqualTo(PsdCompatibilityMode.Rejected));
            Assert.Throws<ArgumentException>(() => PsdCodec.Write(Document(), new PsdLimits { MaxOutputBytes = 26 }));
        }

        [Test] public void StreamReadEnforcesCapAndLeavesStreamOpen()
        {
            byte[] bytes = IndependentFixture(true);
            using (var stream = new MemoryStream(bytes))
            {
                Assert.That(PsdCodec.Read(stream).Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster));
                Assert.That(stream.CanRead, Is.True);
                stream.Position = 0;
                Assert.That(PsdCodec.Read(stream, new PsdLimits { MaxSourceBytes = 26 }).Mode, Is.EqualTo(PsdCompatibilityMode.Rejected));
            }
        }

        [Test] public void HugeSectionLengthAndTruncatedRecordAreRejected()
        {
            var bytes = IndependentFixture(false); Put32(bytes, 26, 0xFFFFFFFFU);
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.Rejected));
            bytes = IndependentFixture(false); Array.Resize(ref bytes, 60);
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.Rejected));
        }

        [Test] public void RleRowExpansionCannotOverflowDestination()
        {
            var bytes = IndependentFixture(true);
            // First layer channel starts immediately after the single layer record.
            int channel = Find(bytes, "lyid") + 12;
            bytes[channel + 6] = 127; // Literal run 128 for a width-two row.
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.Rejected));
            bytes = IndependentFixture(true); bytes[channel + 6] = 255; // Repeat two is valid, but row has a trailing literal byte.
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.Rejected));
        }

        [Test] public void RleShortRowIsRejected()
        {
            var bytes = IndependentFixture(true); int channel = Find(bytes, "lyid") + 12;
            bytes[channel + 6] = 128; bytes[channel + 7] = 128; bytes[channel + 8] = 128;
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.Rejected));
        }

        [Test] public void UnsupportedCompressionRetainsSource()
        {
            byte[] bytes = IndependentFixture(false); int channel = Find(bytes, "lyid") + 12;
            bytes[channel + 1] = 2;
            var result = PsdCodec.Read(bytes);
            Assert.That(result.Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
            Assert.That(result.CopyOriginalBytes(), Is.EqualTo(bytes));
        }

        [Test] public void InvalidDocumentInputsFailBeforeWriting()
        {
            var d = Document(); d.Layers[0].Id = 0; Assert.Throws<ArgumentException>(() => PsdCodec.Write(d));
            d = Document(); d.Layers[0].PixelsRgba = new byte[1]; Assert.Throws<ArgumentException>(() => PsdCodec.Write(d));
            d = Document(); d.Layers[0].Name = "\ud800"; Assert.Throws<EncoderFallbackException>(() => PsdCodec.Write(d));
            d = Document(); d.Layers[0].Left = int.MaxValue; Assert.Throws<ArgumentException>(() => PsdCodec.Write(d));
        }

        [Test] public void SixteenBitAndPsbArePreservedWithoutQuantization()
        {
            var bytes = IndependentFixture(false); bytes[23] = 16;
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
            bytes = IndependentFixture(false); bytes[5] = 2;
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
            Array.Resize(ref bytes, 6);
            Assert.That(PsdCodec.Read(bytes).Mode, Is.EqualTo(PsdCompatibilityMode.Rejected));
        }

        [Test] public void DeterministicRandomMutationsDoNotEscapeParser()
        {
            var random = new Random(7729); byte[] original = IndependentFixture(true);
            for (int trial = 0; trial < 500; trial++)
            {
                byte[] bytes = (byte[])original.Clone();
                for (int n = 0; n < 1 + trial % 5; n++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                Assert.DoesNotThrow(() => PsdCodec.Read(bytes, new PsdLimits { MaxCanvasPixels = 64, MaxDecodedBytes = 4096 }));
            }
        }

        [Test] public void ValidPackBitsRepeatAndNoOpAreSupported()
        {
            var bytes = IndependentFixture(true); int firstChannel = Find(bytes, "lyid") + 12;
            int blueFirstRow = firstChannel + 24 + 6;
            bytes[blueFirstRow] = 255; bytes[blueFirstRow + 1] = 0; bytes[blueFirstRow + 2] = 128;
            var result = PsdCodec.Read(bytes);
            Assert.That(result.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), Diagnostics(result));
            Assert.That(result.Document.Layers[0].PixelsRgba, Is.EqualTo(Document().Layers[0].PixelsRgba));
        }

        [Test] public void IccResourcesAreRetainedAndNeverAssumedSrgb()
        {
            byte[] source = IndependentFixture(false), bytes;
            using (var resource = new MemoryStream()) using (var result = new MemoryStream())
            {
                Key(resource, "8BIM"); U16(resource, 1039); U16(resource, 0); U32(resource, 4); U32(resource, 123);
                result.Write(source, 0, 30); Block(result, resource.ToArray()); result.Write(source, 34, source.Length - 34);
                bytes = result.ToArray();
            }
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly));
            Assert.That(read.Diagnostics.Any(x => x.Code == "ImageResource" && x.Message.Contains("1039")), Is.True);
            Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes));
        }

        [Test] public void LayerMaskAndBlendRangesAreNeverDiscarded()
        {
            foreach (int relative in new[] { 0, 4 })
            {
                var original = IndependentFixture(false); int norm = Find(original, "norm");
                int insert = norm + 12 + relative;
                var bytes = new byte[original.Length + 4];
                Buffer.BlockCopy(original, 0, bytes, 0, insert);
                Put32(bytes, insert, 4); Put32(bytes, insert + 4, 123);
                Buffer.BlockCopy(original, insert + 4, bytes, insert + 8, original.Length - insert - 4);
                foreach (int field in new[] { 34, 38, norm + 8 })
                {
                    uint n = ((uint)original[field] << 24) | ((uint)original[field + 1] << 16) |
                             ((uint)original[field + 2] << 8) | original[field + 3];
                    Put32(bytes, field, n + 4);
                }
                var read = PsdCodec.Read(bytes);
                Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly), Diagnostics(read));
                Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes));
            }
        }

        private static string Diagnostics(PsdReadResult r) { return string.Join("; ", r.Diagnostics.Select(x => x.ToString())); }
        private static int Find(byte[] data, string key, int start = 0)
        {
            byte[] pattern = Encoding.ASCII.GetBytes(key);
            for (int p = start; p <= data.Length - pattern.Length; p++)
            {
                int i = 0; while (i < pattern.Length && data[p + i] == pattern[i]) i++;
                if (i == pattern.Length) return p;
            }
            throw new InvalidOperationException("Fixture key missing: " + key);
        }
        private static void Put32(byte[] b, int p, uint n)
        { b[p] = (byte)(n >> 24); b[p + 1] = (byte)(n >> 16); b[p + 2] = (byte)(n >> 8); b[p + 3] = (byte)n; }
        private static void U16(Stream s, int n) { s.WriteByte((byte)(n >> 8)); s.WriteByte((byte)n); }
        private static void U32(Stream s, int n) { U16(s, n >> 16); U16(s, n); }
        private static void Key(Stream s, string value) { byte[] bytes = Encoding.ASCII.GetBytes(value); s.Write(bytes, 0, bytes.Length); }
        private static void Block(Stream s, byte[] bytes) { U32(s, bytes.Length); s.Write(bytes, 0, bytes.Length); }
        private static byte[] Plane(byte[] rgba, int channel, bool rle)
        {
            using (var s = new MemoryStream())
            {
                U16(s, rle ? 1 : 0);
                if (rle) { U16(s, 3); U16(s, 3); }
                for (int y = 0; y < 2; y++)
                { if (rle) s.WriteByte(1); for (int x = 0; x < 2; x++) s.WriteByte(rgba[(y * 2 + x) * 4 + channel]); }
                return s.ToArray();
            }
        }
        // Independent explicit PSD encoder for parser tests; never calls production Write.
        private static byte[] IndependentFixture(bool rle, string extraTag = null)
        {
            byte[] rgba = Document().Layers[0].PixelsRgba;
            byte[] merged = { 255, 0, 0, 255, 127, 255, 127, 128, 255, 255, 255, 0, 181, 188, 194, 80 };
            using (var extra = new MemoryStream()) using (var info = new MemoryStream())
            using (var layers = new MemoryStream()) using (var psd = new MemoryStream())
            {
                U32(extra, 0); U32(extra, 0); extra.WriteByte(7); Key(extra, "Fixture");
                Key(extra, "8BIM"); Key(extra, "lyid"); U32(extra, 4); U32(extra, 77);
                if (extraTag != null) { Key(extra, "8BIM"); Key(extra, extraTag); U32(extra, 4); U32(extra, 123); }
                U16(info, -1); U32(info, 0); U32(info, 0); U32(info, 2); U32(info, 2); U16(info, 4);
                for (int c = 0; c < 4; c++) { U16(info, c == 3 ? -1 : c); U32(info, Plane(rgba, c, rle).Length); }
                Key(info, "8BIM"); Key(info, "norm"); info.WriteByte(255); info.WriteByte(0); info.WriteByte(0); info.WriteByte(0); Block(info, extra.ToArray());
                for (int c = 0; c < 4; c++) { byte[] data = Plane(rgba, c, rle); info.Write(data, 0, data.Length); }
                if ((info.Length & 1) != 0) info.WriteByte(0);
                Block(layers, info.ToArray()); U32(layers, 0);
                Key(psd, "8BPS"); U16(psd, 1); U32(psd, 0); U16(psd, 0); U16(psd, 4); U32(psd, 2); U32(psd, 2); U16(psd, 8); U16(psd, 3);
                U32(psd, 0); U32(psd, 0); Block(psd, layers.ToArray()); U16(psd, rle ? 1 : 0);
                if (rle) for (int row = 0; row < 8; row++) U16(psd, 3);
                for (int c = 0; c < 4; c++) for (int y = 0; y < 2; y++)
                { if (rle) psd.WriteByte(1); for (int x = 0; x < 2; x++) psd.WriteByte(merged[(y * 2 + x) * 4 + c]); }
                return psd.ToArray();
            }
        }
    }
}
