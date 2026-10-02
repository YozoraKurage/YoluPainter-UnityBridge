using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>PSD の調整レイヤー（階調の反転 nvrt・レベル補正 levl・色相・彩度 hue2）とネイティブの調整レイヤーの読み書き。
    /// ネイティブの式が正本で、PSD の値がそのまま写せるときだけ編集可能にする。</summary>
    public sealed class PsdAdjustmentTests
    {
        static PaintLayer Raster(PaintDocument d, string name, Func<int, int, Rgba32> pixel)
        {
            var layer = d.AddLayer(name); var surface = layer.GetChannel(PaintChannel.Color);
            for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++) { var c = pixel(x, y); if (c != Rgba32.Transparent) surface.SetPixel(x, y, c); }
            return layer;
        }
        static Rgba32 Gradient(int x, int y) => new Rgba32((byte)(x * 10 + 15), (byte)(y * 14 + 20), (byte)(200 - x * 5), 255);
        static Rgba32 Soft(int x, int y) => new Rgba32((byte)(255 - x * 9), (byte)(x * 7 + y * 5), (byte)(y * 13 + 40), (byte)(x < 3 ? 0 : 60 + x * 8 + y * 3));

        static readonly AdjustmentSettings[] Exact =
        {
            AdjustmentSettings.Invert(),
            AdjustmentSettings.Levels(20 / 255.0, 230 / 255.0, 1.37, 10 / 255.0, 240 / 255.0),
            AdjustmentSettings.HueSaturation(-73, 0.42, -0.18),
        };

        static string Describe(AdjustmentSettings a) => a == null ? "-" : a.Type + "(" + string.Join(",", new[] { a.InputBlack, a.InputWhite, a.Gamma, a.OutputBlack, a.OutputWhite, a.Hue, a.Saturation, a.Lightness }) + ")";

        static string Structure(PaintDocument d)
        {
            var names = d.Layers.ToDictionary(l => l.Id, l => l.Name);
            return string.Join("\n", d.Layers.Select(l => l.Name + " parent=" + (l.ParentId == Guid.Empty ? "-" : names[l.ParentId]) + " kind=" + l.Kind + " mode=" + l.BlendMode
                + " clip=" + l.Clipping + " visible=" + l.Visible + " opacity=" + Math.Round(l.Opacity * 255) + " mask=" + (l.Mask == null ? "-" : l.Mask.Enabled + "/" + Math.Round(l.Mask.Density * 255))
                + " adjustment=" + Describe(l.Adjustment)));
        }

        static PaintDocument RoundTrip(PaintDocument d, out PsdReadResult read, out byte[] bytes)
        {
            bytes = PsdCodec.Write(PsdBridge.Export(d, PaintChannel.Color));
            read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            Assert.That(read.Diagnostics, Is.Empty, "our own adjustment layers re-read without CompositeDiffers or anything else");
            var imported = PsdBridge.Import(read);
            Assert.That(Structure(imported), Is.EqualTo(Structure(d)), "order, nesting, attributes and exact adjustment settings");
            Assert.That(imported.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)), "byte-identical composite after re-import");
            Assert.That(PsdCodec.WriteEdited(read, read.Document), Is.EqualTo(bytes));
            return imported;
        }

        [Test] public void EachAdjustmentRoundTripsWithOpacityMaskAndClipping()
        {
            foreach (var settings in Exact)
            {
                var d = new PaintDocument(24, 16, 8);
                Raster(d, "bg", Gradient);
                var plain = d.AddAdjustmentLayer("plain", settings);
                d.SetLayerOpacity(plain.Id, 160 / 255.0);
                var mask = d.AddLayerMask(plain.Id); for (int x = 0; x < 24; x++) mask.Surface.SetPixel(x, 5, new Rgba32(0, 0, 0, 200));
                var shape = Raster(d, "shape", Soft);
                var clipped = d.AddAdjustmentLayer("clipped", settings); d.SetLayerClipping(clipped.Id, true); d.SetLayerBlendMode(clipped.Id, LayerBlendMode.Overlay);
                var hidden = d.AddAdjustmentLayer("hidden", AdjustmentSettings.Invert()); d.SetLayerVisibility(hidden.Id, false);
                RoundTrip(d, out var read, out var bytes);
                Assert.That(read.Document.Layers[2].Adjustment, Is.EqualTo(settings), settings.Type.ToString());
                Assert.That(read.Document.Layers[1].Clipping && read.Document.Layers[1].BlendMode == LayerBlendMode.Overlay, Is.True);
                Assert.That(read.Document.Layers[3].Mask, Is.Not.Null);
                string key = settings.Type == AdjustmentType.Invert ? "nvrt" : settings.Type == AdjustmentType.Levels ? "levl" : "hue2";
                Assert.That(IndexOf(bytes, "8BIM" + key), Is.GreaterThan(0));
            }
        }

        [Test] public void AdjustmentsInsidePassThroughAndIsolatedGroupsRoundTrip()
        {
            var d = new PaintDocument(24, 16, 8);
            Raster(d, "bg", Gradient);
            var a = Raster(d, "a", Soft);
            var levels = d.AddAdjustmentLayer("levels", Exact[1]);
            var pass = d.GroupLayers(new[] { a.Id, levels.Id }, "pass"); d.SetLayerOpacity(pass.Id, 190 / 255.0);
            var b = Raster(d, "b", Soft);
            var hue = d.AddAdjustmentLayer("hue", Exact[2]);
            var invert = d.AddAdjustmentLayer("invert", Exact[0]); d.SetLayerClipping(invert.Id, true); d.SetLayerOpacity(invert.Id, 100 / 255.0);
            var isolated = d.GroupLayers(new[] { b.Id, hue.Id, invert.Id }, "isolated"); d.SetLayerBlendMode(isolated.Id, LayerBlendMode.Multiply);
            var onlyAdjustment = d.AddAdjustmentLayer("alone", Exact[0]); d.GroupLayers(new[] { onlyAdjustment.Id }, "adjustment only");
            RoundTrip(d, out var read, out _);
            Assert.That(read.Document.Layers.Select(l => l.Name), Is.EqualTo(new[] { "adjustment only", "isolated", "pass", "bg" }));
            Assert.That(read.Document.Layers[1].Children.Select(l => l.Name), Is.EqualTo(new[] { "invert", "hue", "b" }));
        }

        [Test] public void AnAdjustmentAsAClipBaseKeepsTheNativeRule()
        {
            var d = new PaintDocument(16, 12, 8);
            Raster(d, "bg", Gradient);
            d.AddAdjustmentLayer("base", Exact[2]);
            var clipped = Raster(d, "clipped to an adjustment", (x, y) => new Rgba32(255, 0, 0, 255)); d.SetLayerClipping(clipped.Id, true);
            RoundTrip(d, out _, out _);
        }

        [Test] public void HueSaturationExportsHiddenInChannelsItDoesNotApplyTo()
        {
            var d = new PaintDocument(8, 8, 8);
            var bg = d.AddLayer("bg"); bg.GetChannel(PaintChannel.Height).SetPixel(1, 1, new Rgba32(10, 10, 10)); d.SetChannelEnabled(bg.Id, PaintChannel.Height, true);
            d.AddAdjustmentLayer("hue", Exact[2]); d.AddAdjustmentLayer("levels", Exact[1]);
            var height = PsdBridge.Export(d, PaintChannel.Height);
            Assert.That(height.Layers[1].Visible, Is.False, "Hue/Saturation never applies to Height");
            Assert.That(height.Layers[0].Visible, Is.True, "Levels applies to every channel");
            var color = PsdBridge.Export(d, PaintChannel.Color);
            Assert.That(color.Layers[1].Visible, Is.True);
        }

        [TestCase(0.3, 1.0, 1.0, 0.0, 0.0)] [TestCase(0.0, 1.0, 1.234, 0.0, 0.0)] [TestCase(-1, 0, 0, 10.5, 0.0)] [TestCase(-1, 0, 0, 0.0, 0.333)]
        public void SettingsBetweenPsdStepsAreRefusedNotRounded(double inputBlack, double inputWhite, double gamma, double hue, double saturation)
        {
            var settings = inputBlack >= 0 ? AdjustmentSettings.Levels(inputBlack, inputWhite, gamma) : AdjustmentSettings.HueSaturation(hue, saturation);
            Assert.That(PsdCodec.AdjustmentRefusal(settings), Does.Contain("between them"));
            var d = new PaintDocument(8, 8, 8); Raster(d, "bg", Gradient); d.AddAdjustmentLayer("x", settings);
            Assert.That(() => PsdBridge.Export(d, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("between them"));
            var dto = new PsdDocument { Width = 1, Height = 1 };
            dto.Layers.Add(new PsdRasterLayer { Id = 1, Adjustment = settings, PixelsRgba = new byte[0] });
            dto.Layers.Add(new PsdRasterLayer { Id = 2, Width = 1, Height = 1, PixelsRgba = new byte[4] });
            Assert.That(() => PsdCodec.Write(dto), Throws.ArgumentException);
            Assert.That(PsdCodec.AdjustmentRefusal(AdjustmentSettings.Levels(254 / 255.0, 1)), Does.Contain("0-253"), "PSD's documented input range");
        }

        // ---- 書き出し側を使わずに組み立てた調整レイヤー ----

        static PsdFixture.Record Adjustment(int id, string name, string key, byte[] body)
        {
            var r = new PsdFixture.Record { Id = id, Name = name, Flags = 8 | 16 };
            r.Tags.Add(new KeyValuePair<string, byte[]>(key, body));
            return r;
        }

        static readonly int[] Neutral = { 0, 255, 0, 255, 100 };

        /// <summary>levl: 版 2、29 個のレコード（0 番が RGB 全体）、必要なら "Lvls" 版 3 の続き。</summary>
        static byte[] LevelsBody(int[] composite, Func<int, int[]> record = null, int extensionCount = 0, string signature = "Lvls", int version = 2)
        {
            return PsdFixture.Bytes(s =>
            {
                PsdFixture.U16(s, version);
                for (int i = 0; i < 29; i++) foreach (int v in i == 0 ? composite : record?.Invoke(i) ?? Neutral) PsdFixture.U16(s, v);
                if (extensionCount > 0)
                {
                    PsdFixture.Key(s, signature); PsdFixture.U16(s, 3); PsdFixture.U16(s, extensionCount);
                    for (int i = 29; i < extensionCount; i++) foreach (int v in record?.Invoke(i) ?? new int[5]) PsdFixture.U16(s, v);
                }
            });
        }

        static readonly int[][] DefaultRanges = { new[] { 315, 345, 15, 45 }, new[] { 15, 45, 75, 105 }, new[] { 75, 105, 135, 165 }, new[] { 135, 165, 195, 225 }, new[] { 195, 225, 255, 285 }, new[] { 255, 285, 315, 345 } };

        /// <summary>hue2: 版 2、色彩の統一の有無、詰め物、色彩の統一の値、マスター、6 つの色域（範囲 4 + 値 3）、Photoshop の 36 バイトの続き。</summary>
        static byte[] HueBody(int[] master, int colorize = 0, int[] colorization = null, Func<int, int[]> rangeSettings = null, Func<int, int[]> ranges = null, bool photoshopTail = true, int version = 2, byte[] extra = null)
        {
            return PsdFixture.Bytes(s =>
            {
                PsdFixture.U16(s, version); s.WriteByte((byte)colorize); s.WriteByte(0);
                foreach (int v in colorization ?? new[] { 0, 25, 0 }) PsdFixture.U16(s, v);
                foreach (int v in master) PsdFixture.U16(s, v);
                for (int i = 0; i < 6; i++)
                {
                    foreach (int v in ranges?.Invoke(i) ?? DefaultRanges[i]) PsdFixture.U16(s, v);
                    foreach (int v in rangeSettings?.Invoke(i) ?? new int[3]) PsdFixture.U16(s, v);
                }
                if (photoshopTail) for (int i = 0; i < 6; i++) { PsdFixture.U16(s, i * 60); PsdFixture.U16(s, 100); PsdFixture.U16(s, 50); }
                if (extra != null) s.Write(extra, 0, extra.Length);
            });
        }

        static List<PsdFixture.Record> Stack(PsdFixture.Record adjustment)
            => new List<PsdFixture.Record> { PsdFixture.Raster(1, "base", 0, 0, 3, 1, new Rgba32(100, 150, 200, 255)), adjustment };

        static byte[] Pixels(params byte[] rgb) => Enumerable.Range(0, 3).SelectMany(_ => new[] { rgb[0], rgb[1], rgb[2], (byte)255 }).ToArray();

        /// <summary>手計算: レベル補正 入力 50..250、γ 1、出力 0..255 → (c − 50) / 200: 100→63.75→64、150→127.5→128、200→191.25→191。
        /// 色相・彩度 彩度 −100 → HSL の明度 (200 + 100) / 2 = 150 の灰色。階調の反転 → 255 − c。</summary>
        [TestCase("levl", new byte[] { 64, 128, 191 })]
        [TestCase("levl-extension", new byte[] { 64, 128, 191 })]
        [TestCase("hue2", new byte[] { 150, 150, 150 })]
        [TestCase("hue2-100", new byte[] { 150, 150, 150 })]
        [TestCase("nvrt", new byte[] { 155, 105, 55 })]
        public void HandBuiltAdjustmentLayersAreEditableAndCompositeAsComputedByHand(string kind, byte[] expected)
        {
            PsdFixture.Record adjustment;
            switch (kind)
            {
                case "levl": adjustment = Adjustment(2, "levels", "levl", LevelsBody(new[] { 50, 250, 0, 255, 100 })); break;
                // CS 以降の "Lvls" の続き。使わないレコードは 0
                case "levl-extension": adjustment = Adjustment(2, "levels", "levl", LevelsBody(new[] { 50, 250, 0, 255, 100 }, i => i > 3 ? new int[5] : Neutral, 62)); break;
                case "hue2": adjustment = Adjustment(2, "hue", "hue2", HueBody(new[] { 0, -100, 0 })); break;
                case "hue2-100": adjustment = Adjustment(2, "hue", "hue2", HueBody(new[] { 0, -100, 0 }, photoshopTail: false)); break;
                default: adjustment = Adjustment(2, "invert", "nvrt", new byte[0]); break;
            }
            var read = PsdCodec.Read(PsdFixture.Build(3, 1, Stack(adjustment), Pixels(expected)));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            Assert.That(read.Diagnostics, Is.Empty, "the hand-computed merged image matches the native formula");
            var layer = read.Document.Layers[0];
            Assert.That(layer.IsAdjustment, Is.True);
            if (kind.StartsWith("levl")) Assert.That(layer.Adjustment, Is.EqualTo(AdjustmentSettings.Levels(50 / 255.0, 250 / 255.0, 1, 0, 1)));
            if (kind.StartsWith("hue2")) Assert.That(layer.Adjustment, Is.EqualTo(AdjustmentSettings.HueSaturation(0, -1, 0)));
            var native = PsdBridge.Import(read);
            Assert.That(native.Layers[1].Kind, Is.EqualTo(LayerKind.Adjustment));
            Assert.That(native.Composite(PaintChannel.Color), Is.EqualTo(Pixels(expected)));
        }

        [Test] public void AStoredCompositeThatDiffersFromTheNativeFormulaIsReportedNotBlocked()
        {
            // Photoshop の色相・彩度の計算はネイティブ（HSL）と同じではない。値はそのまま写せるので、違いは CompositeDiffers で知らせる
            var read = PsdCodec.Read(PsdFixture.Build(3, 1, Stack(Adjustment(2, "hue", "hue2", HueBody(new[] { 0, -100, 0 }))), Pixels(140, 140, 140)));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster));
            Assert.That(read.Diagnostics.Single().Code, Is.EqualTo(PsdCodec.CompositeDiffers));
            Assert.That(read.Diagnostics.Single().Message, Does.Contain("10/255"));
        }

        [Test] public void UnusedHueSaturationSliderPositionsAreReportedAsNotCarried()
        {
            var body = HueBody(new[] { 30, 0, 0 }, colorization: new[] { 200, 60, -10 }, ranges: i => i == 0 ? new[] { 300, 340, 20, 50 } : DefaultRanges[i]);
            var read = PsdCodec.Read(PsdFixture.Build(3, 1, Stack(Adjustment(2, "hue", "hue2", body)), Pixels(100, 150, 200)));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), PsdFixture.Show(read));
            var notes = read.Diagnostics.Where(x => x.Code == PsdCodec.NotCarriedIntoExport).Select(x => x.Message).ToList();
            Assert.That(notes.Any(m => m.Contains("colorize values")), Is.True, PsdFixture.Show(read));
            Assert.That(notes.Any(m => m.Contains("colour range sliders")), Is.True, PsdFixture.Show(read));
        }

        /// <summary>写せない設定: PreserveOnly と理由。</summary>
        [TestCase("per-channel", "Levels", "Per-channel Levels")]
        [TestCase("extra-channel", "Levels", "extra channels")]
        [TestCase("levels-version", "Levels", "version 1")]
        [TestCase("levels-signature", "Levels", "extension 'ls")]
        [TestCase("levels-range", "Levels", "outside the documented ranges")]
        [TestCase("colorize", "HueSaturation", "Colorize")]
        [TestCase("range-edit", "HueSaturation", "limited to a colour range")]
        [TestCase("hue-version", "HueSaturation", "version 1")]
        [TestCase("hue-range", "HueSaturation", "outside the documented ranges")]
        [TestCase("hue-tail", "HueSaturation", "Unrecognized")]
        [TestCase("pixels", "AdjustmentPixels", "own pixels")]
        [TestCase("two-blocks", "Adjustment", "More than one adjustment")]
        [TestCase("old-hue", "TaggedBlock", "Adjustment layer (hue )")]
        [TestCase("curves", "TaggedBlock", "Adjustment layer (curv)")]
        public void UnrepresentableAdjustmentsArePreserveOnly(string kind, string code, string mention)
        {
            var levels = new[] { 50, 250, 0, 255, 100 };
            PsdFixture.Record adjustment;
            switch (kind)
            {
                case "per-channel": adjustment = Adjustment(2, "l", "levl", LevelsBody(levels, i => i == 2 ? new[] { 10, 255, 0, 255, 100 } : Neutral)); break;
                case "extra-channel": adjustment = Adjustment(2, "l", "levl", LevelsBody(levels, i => i == 5 ? new[] { 10, 255, 0, 255, 100 } : Neutral)); break;
                case "levels-version": adjustment = Adjustment(2, "l", "levl", LevelsBody(levels, version: 1)); break;
                case "levels-signature": adjustment = Adjustment(2, "l", "levl", LevelsBody(levels, extensionCount: 30, signature: "ls\0\u0003")); break; // CLIP STUDIO の誤った署名
                case "levels-range": adjustment = Adjustment(2, "l", "levl", LevelsBody(new[] { 254, 255, 0, 255, 100 })); break;
                case "colorize": adjustment = Adjustment(2, "h", "hue2", HueBody(new[] { 0, 0, 0 }, colorize: 1)); break;
                case "range-edit": adjustment = Adjustment(2, "h", "hue2", HueBody(new[] { 0, 0, 0 }, rangeSettings: i => i == 3 ? new[] { 20, 0, 0 } : new int[3])); break;
                case "hue-version": adjustment = Adjustment(2, "h", "hue2", HueBody(new[] { 0, 0, 0 }, version: 1)); break;
                case "hue-range": adjustment = Adjustment(2, "h", "hue2", HueBody(new[] { 0, 150, 0 })); break;
                case "hue-tail": adjustment = Adjustment(2, "h", "hue2", HueBody(new[] { 0, 0, 0 }, extra: new byte[] { 1, 2, 3, 4 })); break;
                case "pixels": adjustment = Adjustment(2, "i", "nvrt", new byte[0]); adjustment.Width = 1; adjustment.Height = 1; adjustment.Rgba = new byte[] { 1, 2, 3, 4 }; break;
                case "two-blocks": adjustment = Adjustment(2, "i", "nvrt", new byte[0]); adjustment.Tags.Add(new KeyValuePair<string, byte[]>("levl", LevelsBody(levels))); break;
                case "old-hue": adjustment = Adjustment(2, "h", "hue ", new byte[40]); break;
                default: adjustment = Adjustment(2, "c", "curv", new byte[20]); break;
            }
            byte[] bytes = PsdFixture.Build(3, 1, Stack(adjustment), Pixels(100, 150, 200));
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly), PsdFixture.Show(read));
            Assert.That(read.Diagnostics.Any(x => x.Code == code && x.Message.Contains(mention)), Is.True, PsdFixture.Show(read));
            Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes));
        }

        /// <summary>壊れたブロック: 理由付きで拒否、原本はそのまま。</summary>
        [TestCase("levels-short", "shorter than its 29 records")]
        [TestCase("levels-count", "record count does not fit")]
        [TestCase("hue-short", "shorter than its fields")]
        [TestCase("invert-data", "carries data")]
        public void MalformedAdjustmentBlocksAreRejected(string kind, string reason)
        {
            PsdFixture.Record adjustment;
            switch (kind)
            {
                case "levels-short": adjustment = Adjustment(2, "l", "levl", new byte[100]); break;
                case "levels-count": adjustment = Adjustment(2, "l", "levl", LevelsBody(new[] { 50, 250, 0, 255, 100 }).Concat(new byte[] { (byte)'L', (byte)'v', (byte)'l', (byte)'s', 0, 3, 0, 99 }).ToArray()); break;
                case "hue-short": adjustment = Adjustment(2, "h", "hue2", new byte[50]); break;
                default: adjustment = Adjustment(2, "i", "nvrt", new byte[4]); break;
            }
            byte[] bytes = PsdFixture.Build(3, 1, Stack(adjustment), Pixels(100, 150, 200));
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.Rejected), PsdFixture.Show(read));
            Assert.That(read.Diagnostics.Single().Message, Does.Contain(reason));
            Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes));
        }

        [Test] public void MutatedAdjustmentFilesNeverEscapeTheParser()
        {
            var d = new PaintDocument(8, 6, 8);
            Raster(d, "bg", Gradient);
            var a = Raster(d, "a", Soft);
            var levels = d.AddAdjustmentLayer("levels", Exact[1]);
            var hue = d.AddAdjustmentLayer("hue", Exact[2]); d.SetLayerClipping(hue.Id, true);
            d.GroupLayers(new[] { a.Id, levels.Id, hue.Id }, "g");
            d.AddAdjustmentLayer("invert", Exact[0]);
            byte[][] originals =
            {
                PsdCodec.Write(PsdBridge.Export(d, PaintChannel.Color)),
                PsdFixture.Build(3, 1, Stack(Adjustment(2, "levels", "levl", LevelsBody(new[] { 50, 250, 0, 255, 100 }, null, 30))), Pixels(64, 128, 191)),
                PsdFixture.Build(3, 1, Stack(Adjustment(2, "hue", "hue2", HueBody(new[] { 0, -100, 0 }))), Pixels(150, 150, 150)),
            };
            var random = new Random(5531);
            for (int trial = 0; trial < 600; trial++)
            {
                byte[] bytes = (byte[])originals[trial % 3].Clone();
                for (int n = 0; n < 1 + trial % 4; n++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                PsdReadResult read = null;
                Assert.DoesNotThrow(() => read = PsdCodec.Read(bytes, new PsdLimits { MaxCanvasPixels = 4096, MaxDecodedBytes = 1 << 20, MaxGroupDepth = 8 }), "trial " + trial);
                if (read.Mode == PsdCompatibilityMode.EditableRaster)
                {
                    Assert.DoesNotThrow(() => PsdCodec.WriteEdited(read, read.Document), "trial " + trial);
                    try { PsdBridge.Import(read); }
                    catch (InvalidOperationException) { } // 範囲外の画素などの明示的な拒否は可
                }
                Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes));
            }
        }

        static int IndexOf(byte[] data, string pattern)
        {
            var p = pattern.Select(c => (byte)c).ToArray();
            for (int i = 0; i <= data.Length - p.Length; i++) { int k = 0; while (k < p.Length && data[i + k] == p[k]) k++; if (k == p.Length) return i; }
            return -1;
        }
    }
}
