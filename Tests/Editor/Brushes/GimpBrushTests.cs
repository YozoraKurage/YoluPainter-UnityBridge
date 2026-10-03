using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Brushes;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>GIMP の .gbr / .gih / .vbr の読み込み。テスト用のファイルは公開された形式どおりにここで組み立てる。</summary>
    public sealed class GimpBrushTests
    {
        /// <summary>.gbr を組み立てる。pixels は上の行から（ファイルの並び）。</summary>
        internal static byte[] Gbr(int width, int height, byte[] pixels, int bytes = 1, string name = "Test", uint spacing = 25, int version = 2)
        {
            using (var s = new MemoryStream())
            {
                void U32(uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
                var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
                U32((uint)((version == 1 ? 20 : 28) + nameBytes.Length)); U32((uint)version); U32((uint)width); U32((uint)height); U32((uint)bytes);
                if (version != 1) { U32(0x47494D50); U32(spacing); }
                s.Write(nameBytes, 0, nameBytes.Length); s.Write(pixels, 0, pixels.Length);
                return s.ToArray();
            }
        }

        [Test] public void GbrGrayscaleIsFlippedToTheCanvasOrigin()
        {
            // 2x2: 上の行 = (255, 0)、下の行 = (0, 128)
            var brush = GimpBrushReader.ReadGbr(Gbr(2, 2, new byte[] { 255, 0, 0, 128 }, name: "Pixel 筆", spacing: 50));
            var tip = brush.Settings.Tip;
            Assert.That(brush.Name, Is.EqualTo("Pixel 筆")); Assert.That(brush.Source, Is.EqualTo("GIMP GBR"));
            Assert.That(tip[0, 1], Is.EqualTo(255), "the file's first row is the tip's top row"); Assert.That(tip[1, 0], Is.EqualTo(128));
            Assert.That(brush.Settings.Radius, Is.EqualTo(1)); Assert.That(brush.Settings.Spacing, Is.EqualTo(.5));
            Assert.That(brush.Warnings, Is.Empty);
        }

        [Test] public void GbrVersion1AndColourBrushesAreReadWithHonestWarnings()
        {
            var v1 = GimpBrushReader.ReadGbr(Gbr(1, 1, new byte[] { 200 }, name: "old", version: 1));
            Assert.That(v1.Settings.Spacing, Is.EqualTo(.25), "version 1 has no spacing field; GIMP uses 25%");
            var rgba = GimpBrushReader.ReadGbr(Gbr(1, 1, new byte[] { 10, 20, 30, 77 }, bytes: 4));
            Assert.That(rgba.Settings.Tip[0, 0], Is.EqualTo(77), "the alpha channel becomes the tip");
            Assert.That(rgba.Warnings.Single(), Does.Contain("colour"));
            // 横長: 間隔は幅に対する % なので、直径（長い辺）に対する割合に直す
            var wide = GimpBrushReader.ReadGbr(Gbr(4, 2, new byte[8], spacing: 100));
            Assert.That(wide.Settings.Spacing, Is.EqualTo(1)); Assert.That(wide.Settings.Radius, Is.EqualTo(2));
            var tall = GimpBrushReader.ReadGbr(Gbr(2, 4, new byte[8], spacing: 100));
            Assert.That(tall.Settings.Spacing, Is.EqualTo(.5));
        }

        [Test] public void MalformedGbrIsRefusedWithAReason()
        {
            var good = Gbr(2, 2, new byte[4]);
            Assert.That(() => GimpBrushReader.ReadGbr(good.Take(good.Length - 1).ToArray()), Throws.TypeOf<BrushImportException>().With.Message.Contains("truncated"));
            var badMagic = (byte[])good.Clone(); badMagic[20] = (byte)'X';
            Assert.That(() => GimpBrushReader.ReadGbr(badMagic), Throws.TypeOf<BrushImportException>().With.Message.Contains("signature"));
            Assert.That(() => GimpBrushReader.ReadGbr(Gbr(2, 2, new byte[4 * 18], bytes: 18)), Throws.TypeOf<BrushImportException>().With.Message.Contains("CinePaint"));
            Assert.That(() => GimpBrushReader.ReadGbr(Gbr(BrushTip.MaxSize + 1, 1, new byte[BrushTip.MaxSize + 1])), Throws.TypeOf<BrushImportException>());
        }

        static byte[] Gih(string header, params byte[][] cells)
        {
            var bytes = new List<byte>(Encoding.UTF8.GetBytes(header));
            foreach (var cell in cells) bytes.AddRange(cell);
            return bytes.ToArray();
        }

        [Test] public void GihBecomesOneBrushWithSeveralTips()
        {
            var cells = new[] { Gbr(2, 2, new byte[] { 255, 255, 0, 0 }, name: "a"), Gbr(3, 1, new byte[] { 1, 2, 3 }, name: "b"), Gbr(2, 2, new byte[4], name: "c") };
            var hose = GimpBrushReader.ReadGih(Gih("Sparks\n3 ncells:3 cellwidth:2 cellheight:2 step:100 dim:1 cols:1 rows:1 placement:constant rank0:3 sel0:random\n", cells));
            Assert.That(hose.Name, Is.EqualTo("Sparks")); Assert.That(hose.Settings.Tips.Length, Is.EqualTo(3));
            Assert.That(hose.Settings.TipSelection, Is.EqualTo(TipSelection.Random));
            Assert.That(hose.Settings.Tips[1].Width, Is.EqualTo(3), "cells may differ in size");
            Assert.That(hose.Warnings, Is.Empty);
            var incremental = GimpBrushReader.ReadGih(Gih("Seq\n2\n", cells.Take(2).ToArray()));
            Assert.That(incremental.Settings.TipSelection, Is.EqualTo(TipSelection.Sequential), "an empty parameter list means incremental");
            var pressure = GimpBrushReader.ReadGih(Gih("Felt\n2 ncells:2 dim:3 rank0:2 sel0:pressure rank1:1 sel1:ytilt rank2:1 sel2:xtilt\n", cells.Take(2).ToArray()));
            Assert.That(pressure.Warnings.Count, Is.EqualTo(2), "pressure selection and the 3-D layout are both reported");
            var short2 = GimpBrushReader.ReadGih(Gih("Short\n3\n", cells.Take(2).ToArray()));
            Assert.That(short2.Settings.Tips.Length, Is.EqualTo(2), "a hose that simply ends early keeps the cells it has");
            Assert.That(short2.Warnings.Single(), Does.Contain("declares 3 cells but the file holds only 2"));
            var cut = Gih("Cut\n2\n", cells.Take(2).ToArray()); cut = cut.Take(cut.Length - 1).ToArray();
            Assert.That(() => GimpBrushReader.ReadGih(cut), Throws.TypeOf<BrushImportException>(), "a cell cut in the middle is still refused");
        }

        [Test] public void VbrCircleMapsToTheRoundTipAndStarsAreRendered()
        {
            var soft = GimpBrushReader.ReadVbr("GIMP-VBR\n1.0\nHardness 050\n10.000000\n25.000000\n0.500000\n1.000000\n0.000000\n");
            Assert.That(soft.Settings.Tip, Is.Null); Assert.That(soft.Settings.Radius, Is.EqualTo(25)); Assert.That(soft.Settings.Hardness, Is.EqualTo(.5));
            Assert.That(soft.Settings.Spacing, Is.EqualTo(.1)); Assert.That(soft.Warnings, Is.Empty);
            var star = GimpBrushReader.ReadVbr("GIMP-VBR\r\n1.5\r\nStar\r\ndiamond\r\n50.000000\r\n25.000000\r\n5\r\n1.000000\r\n2.500000\r\n17.500000\r\n");
            Assert.That(star.Settings.Tip, Is.Not.Null); Assert.That(star.Settings.Roundness, Is.EqualTo(.4)); Assert.That(star.Settings.Angle, Is.EqualTo(17.5));
            Assert.That(star.Settings.Tip.Sample(.5, .5), Is.EqualTo(1).Within(1e-9), "the centre is solid");
            Assert.That(star.Settings.Tip.Sample(.99, .5), Is.GreaterThan(0), "a spike reaches the edge along its axis");
            Assert.That(star.Warnings.Single(), Does.Contain("5 spikes"));
            Assert.That(() => GimpBrushReader.ReadVbr("GIMP-VBR\n1.5\nBad\nhexagon\n1\n1\n2\n1\n1\n0\n"), Throws.TypeOf<BrushImportException>());
            Assert.That(() => GimpBrushReader.ReadVbr("GIMP-VBR\n1.5\nBad\ncircle\n1\n1\n21\n1\n1\n0\n"), Throws.TypeOf<BrushImportException>(), "spikes above 20 are refused like GIMP does");
            Assert.That(() => GimpBrushReader.ReadVbr("GIMP-VBR\n1.0\nShort\n10\n"), Throws.TypeOf<BrushImportException>());
        }

        [Test] public void SeveralTipsAreUsedRandomlyOrInTurn()
        {
            BrushTip Solid(int w) => new BrushTip("s" + w, w, w, Enumerable.Repeat((byte)255, w * w).ToArray());
            var d = new PaintDocument(64, 64, 16); var layer = d.AddLayer("L").Id; d.ClearHistory();
            var settings = new BrushSettings { Radius = 6, PressureSize = false, PressureOpacity = false, Spacing = 2, Color = new Rgba32(0, 0, 0), Tips = new[] { Solid(2), Solid(4) }, TipSelection = TipSelection.Sequential };
            using (var s = d.BeginStroke(layer, PaintChannel.Color, settings)) { s.Add(new BrushSample(10, 32, 1, 0)); s.Add(new BrushSample(58, 32, 1, .1)); s.Commit(); }
            Assert.That(d.CompositePixel(PaintChannel.Color, 10, 32).A, Is.EqualTo(255), "first dab, first tip");
            Assert.That(settings.Clone().Tips, Is.Not.SameAs(settings.Tips), "Clone copies the tip array");
            Assert.Throws<ArgumentException>(() => new BrushSettings { Tips = new BrushTip[] { null } }.Validate());
        }
    }
}
