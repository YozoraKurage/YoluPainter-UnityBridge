using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>PSD の単色の塗りつぶしレイヤー（SoCo）: ネイティブの Fill レイヤーと往復し（画素に焼かない）、Photoshop の半端な値は丸めて知らせ、
    /// RGB 以外の色や知らない形は保護、不透明でない値の書き出しは断る。</summary>
    public sealed class PsdFillTests
    {
        static void Double(System.IO.Stream s, double v) { long bits = BitConverter.DoubleToInt64Bits(v); PsdFixture.U32(s, (bits >> 32) & 0xffffffffL); PsdFixture.U32(s, bits & 0xffffffffL); }
        static void Name(System.IO.Stream s) { PsdFixture.U32(s, 1); PsdFixture.U16(s, 0); }
        static void Id(System.IO.Stream s, string k) { PsdFixture.U32(s, 0); PsdFixture.Key(s, k); }
        /// <summary>SoCo: 版 16、記述子（名前 ""、クラス null、'Clr ' = colorClass { 3 つの値 }）。</summary>
        static byte[] SoCo(string colorClass, string[] keys, double[] values, int version = 16) => PsdFixture.Bytes(s =>
        {
            PsdFixture.U32(s, version); Name(s); Id(s, "null"); PsdFixture.U32(s, 1);
            Id(s, "Clr "); PsdFixture.Key(s, "Objc"); Name(s); Id(s, colorClass); PsdFixture.U32(s, keys.Length);
            for (int i = 0; i < keys.Length; i++) { Id(s, keys[i]); PsdFixture.Key(s, "doub"); Double(s, values[i]); }
        });
        static readonly string[] Rgb = { "Rd  ", "Grn ", "Bl  " };
        static PsdFixture.Record FillRecord(byte[] body, int width = 0, int height = 0)
        {
            var r = width > 0 ? PsdFixture.Raster(2, "fill", 0, 0, width, height, new Rgba32(1, 2, 3, 255)) : new PsdFixture.Record { Id = 2, Name = "fill", Flags = 8 | 16 };
            if (width > 0) r.Flags = 8 | 16;
            r.Tags.Add(new KeyValuePair<string, byte[]>("SoCo", body)); return r;
        }
        static byte[] Merged(Rgba32 c) => PsdFixture.Fill(3, 1, c);
        static List<PsdFixture.Record> Stack(PsdFixture.Record fill) => new List<PsdFixture.Record> { PsdFixture.Raster(1, "base", 0, 0, 3, 1, new Rgba32(100, 150, 200, 255)), fill };

        [Test] public void NativeFillLayersRoundTripAsSolidColourFills()
        {
            var d = new PaintDocument(16, 12, 8);
            var bg = d.AddLayer("bg"); for (int y = 0; y < 12; y++) for (int x = 0; x < 16; x++) bg.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32((byte)(x * 15), (byte)(y * 20), 90));
            var fill = d.AddFillLayer("tint", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(40, 90, 200) } });
            d.SetLayerOpacity(fill.Id, 150 / 255.0); d.SetLayerBlendMode(fill.Id, LayerBlendMode.Multiply);
            var mask = d.AddLayerMask(fill.Id); for (int x = 0; x < 16; x++) mask.Surface.SetPixel(x, 3, new Rgba32(0, 0, 0, 255));
            d.ClearHistory();
            var bytes = PsdCodec.Write(PsdBridge.Export(d, PaintChannel.Color));
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            Assert.That(read.Diagnostics, Is.Empty, PsdFixture.Show(read));
            Assert.That(read.Document.Layers[0].FillColor, Is.EqualTo(new Rgba32(40, 90, 200)));
            Assert.That(read.Document.Layers[0].PixelsRgba, Is.Empty, "the fill is not baked into pixels");
            var imported = PsdBridge.Import(read);
            Assert.That(imported.Layers[1].Kind, Is.EqualTo(LayerKind.Fill)); Assert.That(imported.Layers[1].FillValues[PaintChannel.Color], Is.EqualTo(new Rgba32(40, 90, 200)));
            Assert.That(imported.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            Assert.That(PsdCodec.WriteEdited(read, read.Document), Is.EqualTo(bytes));
            Assert.That(IndexOf(bytes, "8BIMSoCo"), Is.GreaterThan(0));
        }

        [Test] public void FillsAreHiddenInChannelsTheyDoNotCoverAndTranslucentValuesAreRefused()
        {
            var d = new PaintDocument(8, 8, 8); var bg = d.AddLayer("bg"); bg.GetChannel(PaintChannel.Color).SetPixel(0, 0, new Rgba32(1, 1, 1));
            d.SetChannelEnabled(bg.Id, PaintChannel.Roughness, true); bg.GetChannel(PaintChannel.Roughness).SetPixel(0, 0, new Rgba32(9, 9, 9));
            d.AddFillLayer("color only", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(10, 20, 30) } });
            Assert.That(PsdBridge.Export(d, PaintChannel.Roughness).Layers[0].Visible, Is.False, "no Roughness value: written hidden");
            Assert.That(PsdBridge.Export(d, PaintChannel.Color).Layers[0].Visible, Is.True);
            var e = new PaintDocument(8, 8, 8); e.AddLayer("bg"); e.AddFillLayer("glass", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(10, 20, 30, 128) } });
            Assert.That(() => PsdBridge.Export(e, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("opaque"));
        }

        [Test] public void PhotoshopFractionsAreRoundedAndReported()
        {
            // Photoshop は 16 bit から換算した値を書く（127.99610137939453 など）。丸めて、丸めたことを知らせる
            var body = SoCo("RGBC", Rgb, new[] { 127.99610137939453, 0.0, 255.0 });
            var read = PsdCodec.Read(PsdFixture.Build(3, 1, Stack(FillRecord(body)), Merged(new Rgba32(128, 0, 255))));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            Assert.That(read.Document.Layers[0].FillColor, Is.EqualTo(new Rgba32(128, 0, 255)));
            Assert.That(read.Diagnostics.Select(x => x.Message), Has.Some.Contains("rounded"));
            Assert.That(PsdBridge.Import(read).Composite(PaintChannel.Color), Is.EqualTo(Merged(new Rgba32(128, 0, 255))));
        }

        [Test] public void APixelCacheIsNotCarriedButTheFillIsEditable()
        {
            var read = PsdCodec.Read(PsdFixture.Build(3, 1, Stack(FillRecord(SoCo("RGBC", Rgb, new[] { 10.0, 20, 30 }), 3, 1)), Merged(new Rgba32(10, 20, 30))));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            Assert.That(read.Diagnostics.Select(x => x.Message), Has.Some.Contains("pixel cache"));
            Assert.That(read.Document.Layers[0].FillColor, Is.EqualTo(new Rgba32(10, 20, 30)));
        }

        [TestCase("gray", "other than RGB")]
        [TestCase("version", "version 1")]
        [TestCase("missing", "Unrecognized")]
        [TestCase("range", "outside 0")]
        [TestCase("garbage", "Unreadable")]
        public void OtherFillsArePreserved(string kind, string message)
        {
            byte[] body;
            switch (kind)
            {
                case "gray": body = SoCo("Grsc", new[] { "Gry " }, new[] { 50.0 }); break;
                case "version": body = SoCo("RGBC", Rgb, new[] { 1.0, 2, 3 }, 1); break;
                case "missing": body = SoCo("RGBC", new[] { "Rd  ", "Grn " }, new[] { 1.0, 2 }); break;
                case "range": body = SoCo("RGBC", Rgb, new[] { 300.0, 2, 3 }); break;
                default: body = PsdFixture.Bytes(s => { PsdFixture.U32(s, 16); PsdFixture.U32(s, 99999); }); break;
            }
            var read = PsdCodec.Read(PsdFixture.Build(3, 1, Stack(FillRecord(body)), Merged(new Rgba32(100, 150, 200))));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly), PsdFixture.Show(read));
            Assert.That(read.Diagnostics.Select(x => x.Message), Has.Some.Contains(message));
        }

        static int IndexOf(byte[] haystack, string needle)
        {
            var n = System.Text.Encoding.ASCII.GetBytes(needle);
            for (int i = 0; i + n.Length <= haystack.Length; i++) if (haystack.Skip(i).Take(n.Length).SequenceEqual(n)) return i;
            return -1;
        }
    }
}
