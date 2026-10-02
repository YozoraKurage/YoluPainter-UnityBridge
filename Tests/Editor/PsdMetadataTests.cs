using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画に関わらない情報（レイヤーのメタデータ・ロック・カラーラベル、ガイドやサムネイルや XMP などの画像リソース）は
    /// 編集可能のまま受け入れ、NotCarriedIntoExport の診断で知らせる。描画や解釈を変えうるもの（sRGB 以外の ICC プロファイル、
    /// 1:1 でないピクセル縦横比、分類できないブロック）は PreserveOnly のまま。</summary>
    public sealed class PsdMetadataTests
    {
        static readonly byte[] Composite = { 10, 20, 30, 255, 40, 50, 60, 255 };

        static List<PsdFixture.Record> Layers()
        {
            var a = PsdFixture.Raster(1, "a", 0, 0, 1, 1, new Rgba32(10, 20, 30, 255));
            var b = PsdFixture.Raster(2, "b", 1, 0, 1, 1, new Rgba32(40, 50, 60, 255));
            return new List<PsdFixture.Record> { a, b };
        }

        static byte[] Resource(int id, byte[] body) => PsdFixture.Bytes(s =>
        {
            PsdFixture.Key(s, "8BIM"); PsdFixture.U16(s, id); PsdFixture.U16(s, 0); PsdFixture.Block(s, body);
            if ((body.Length & 1) != 0) s.WriteByte(0);
        });
        static byte[] DocumentTag(string key, byte[] body) => PsdFixture.Bytes(s => { PsdFixture.Key(s, "8BIM"); PsdFixture.Key(s, key); PsdFixture.Block(s, body); });

        /// <summary>RGB のディスプレイ ICC プロファイル。v2 は textDescriptionType、v4 は multiLocalizedUnicodeType の説明だけを持つ。</summary>
        static byte[] Icc(string description, bool v4 = false, string colorSpace = "RGB ")
        {
            byte[] desc = v4
                ? PsdFixture.Bytes(s =>
                {
                    var text = new UnicodeEncoding(true, false).GetBytes(description);
                    PsdFixture.Key(s, "mluc"); PsdFixture.U32(s, 0); PsdFixture.U32(s, 1); PsdFixture.U32(s, 12);
                    s.Write(Encoding.ASCII.GetBytes("enUS"), 0, 4); PsdFixture.U32(s, text.Length); PsdFixture.U32(s, 28); s.Write(text, 0, text.Length);
                })
                : PsdFixture.Bytes(s =>
                {
                    var text = Encoding.ASCII.GetBytes(description + "\0");
                    PsdFixture.Key(s, "desc"); PsdFixture.U32(s, 0); PsdFixture.U32(s, text.Length); s.Write(text, 0, text.Length); s.Write(new byte[79], 0, 79);
                });
            int tagOffset = 128 + 4 + 12;
            return PsdFixture.Bytes(s =>
            {
                PsdFixture.U32(s, tagOffset + desc.Length); PsdFixture.U32(s, 0); PsdFixture.U32(s, v4 ? 0x04300000 : 0x02100000);
                PsdFixture.Key(s, "mntr"); PsdFixture.Key(s, colorSpace); PsdFixture.Key(s, "XYZ "); s.Write(new byte[12], 0, 12); PsdFixture.Key(s, "acsp");
                s.Write(new byte[128 - 40], 0, 128 - 40);
                PsdFixture.U32(s, 1); PsdFixture.Key(s, "desc"); PsdFixture.U32(s, tagOffset); PsdFixture.U32(s, desc.Length);
                s.Write(desc, 0, desc.Length);
            });
        }

        static byte[] AspectRatio(double ratio) => PsdFixture.Bytes(s => { PsdFixture.U32(s, 1); var b = BitConverter.GetBytes(ratio); if (BitConverter.IsLittleEndian) Array.Reverse(b); s.Write(b, 0, 8); });

        static byte[] Build(string kind)
        {
            var layers = Layers(); byte[] resources = null, document = null;
            void Tag(string key, byte[] body) => layers[0].Tags.Add(new KeyValuePair<string, byte[]>(key, body));
            switch (kind)
            {
                case "lnsr": Tag("lnsr", Encoding.ASCII.GetBytes("rend")); break;
                case "shmd": Tag("shmd", new byte[4]); break;
                case "fxrp": Tag("fxrp", new byte[16]); break;
                case "lyvr": Tag("lyvr", new byte[] { 0, 0, 0, 70 }); break;
                case "lspf": Tag("lspf", new byte[] { 0, 0, 0, 1 }); break;
                case "lclr": Tag("lclr", new byte[] { 0, 3, 0, 0, 0, 0, 0, 0 }); break;
                case "lock": layers[0].Flags = 8 | 1; break;
                case "1005": resources = Resource(1005, new byte[16]); break;
                case "1032": resources = Resource(1032, new byte[16]); break;
                case "1036": resources = Resource(1036, new byte[28]); break;
                case "1060": resources = Resource(1060, Encoding.ASCII.GetBytes("<x:xmpmeta/>")); break;
                case "1057": resources = Resource(1057, new byte[13]); break;
                case "1065": resources = Resource(1065, new byte[8]); break;
                case "1050": resources = Resource(1050, new byte[8]); break;
                case "2000": resources = Resource(2000, new byte[26]); break;
                case "1064": resources = Resource(1064, AspectRatio(1)); break;
                case "srgb": resources = Resource(1039, Icc("sRGB IEC61966-2.1")); break;
                case "srgb-v4": resources = Resource(1039, Icc("sRGB IEC61966-2.1", true)); break;
                case "Patt": case "Txt2": case "FMsk": document = DocumentTag(kind, new byte[8]); break;
                // 描画や解釈を変えうるもの
                case "adobe-rgb": resources = Resource(1039, Icc("Adobe RGB (1998)")); break;
                case "srgb-name-cmyk": resources = Resource(1039, Icc("sRGB IEC61966-2.1", false, "CMYK")); break;
                case "aspect": resources = Resource(1064, AspectRatio(0.9)); break;
                case "4000": resources = Resource(4000, new byte[4]); break;
                case "cinf": Tag("cinf", new byte[8]); break;
                case "tsly": Tag("tsly", PsdFixture.Setting(0)); break;
                case "Lr16": document = DocumentTag("Lr16", new byte[8]); break;
                default: throw new ArgumentException(kind);
            }
            return PsdFixture.Build(2, 1, layers, Composite, resources, document);
        }

        [TestCase("lnsr", "lnsr")] [TestCase("shmd", "shmd")] [TestCase("fxrp", "fxrp")] [TestCase("lyvr", "lyvr")] [TestCase("lspf", "lspf")]
        [TestCase("lclr", "lclr")] [TestCase("lock", "Transparency lock")]
        [TestCase("1005", "1005 (resolution")] [TestCase("1032", "1032 (grid and guides)")] [TestCase("1036", "1036 (thumbnail)")] [TestCase("1060", "1060 (XMP")]
        [TestCase("1057", "1057 (version info)")] [TestCase("1065", "1065 (layer comps)")] [TestCase("1050", "1050 (slices)")] [TestCase("2000", "2000 (saved path)")]
        [TestCase("1064", "pixel aspect ratio 1:1")] [TestCase("srgb", "sRGB IEC61966-2.1")] [TestCase("srgb-v4", "sRGB IEC61966-2.1")]
        [TestCase("Patt", "Pattern library (Patt)")] [TestCase("Txt2", "Txt2")] [TestCase("FMsk", "FMsk")]
        public void NonRenderingInformationIsEditableAndReported(string kind, string mention)
        {
            byte[] bytes = Build(kind);
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            var note = read.Diagnostics.Single();
            Assert.That(note.Code, Is.EqualTo(PsdCodec.NotCarriedIntoExport));
            Assert.That(note.Message, Does.Contain(mention)); Assert.That(note.Message, Does.Contain("not carried into exported PSDs"));
            Assert.That(PsdCodec.IsInformational(note), Is.True);
            Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes), "the original keeps it");
            var native = PsdBridge.Import(read);
            Assert.That(native.Composite(PaintChannel.Color), Is.EqualTo(Composite));
            byte[] exported = PsdCodec.Write(PsdBridge.Export(native, PaintChannel.Color));
            var again = PsdCodec.Read(exported);
            Assert.That(again.Diagnostics, Is.Empty, "the exported PSD does not carry it (and says nothing it does not have)");
            if (kind.Length == 4 && !char.IsDigit(kind[0])) Assert.That(Encoding.ASCII.GetString(exported).Contains(kind), Is.False, kind + " is not written");
        }

        [TestCase("adobe-rgb", "ImageResource", "Adobe RGB (1998)")]
        [TestCase("srgb-name-cmyk", "ImageResource", "cannot be identified")]
        [TestCase("aspect", "ImageResource", "0.9")]
        [TestCase("4000", "ImageResource", "4000")]
        [TestCase("cinf", "TaggedBlock", "cinf")]
        [TestCase("tsly", "TransparencyShapes", "tsly")]
        [TestCase("Lr16", "TaggedBlock", "Lr16")]
        public void InformationThatCanChangeRenderingOrInterpretationStaysPreserveOnly(string kind, string code, string mention)
        {
            byte[] bytes = Build(kind);
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly), PsdFixture.Show(read));
            var diagnostic = read.Diagnostics.Single(x => !PsdCodec.IsInformational(x));
            Assert.That(diagnostic.Code, Is.EqualTo(code)); Assert.That(diagnostic.Message, Does.Contain(mention));
            Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes));
        }

        [Test] public void EachKindIsReportedOnceWithItsCount()
        {
            var layers = Layers();
            foreach (var l in layers) { l.Tags.Add(new KeyValuePair<string, byte[]>("lnsr", Encoding.ASCII.GetBytes("rend"))); l.Tags.Add(new KeyValuePair<string, byte[]>("shmd", new byte[4])); }
            var resources = Resource(1005, new byte[16]).Concat(Resource(1036, new byte[28])).Concat(Resource(1039, Icc("sRGB IEC61966-2.1"))).ToArray();
            var read = PsdCodec.Read(PsdFixture.Build(2, 1, layers, Composite, resources));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            Assert.That(read.Diagnostics.All(PsdCodec.IsInformational), Is.True);
            Assert.That(read.Diagnostics.Count, Is.EqualTo(5), PsdFixture.Show(read));
            Assert.That(read.Diagnostics.Single(x => x.Message.Contains("lnsr")).Message, Does.Contain("(2 records)"));
        }
    }
}
