using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Psd;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>PSD のレイヤー機能（合成モード・クリッピング・ラスターマスク・非表示・不透明度）の読み書き。ネイティブ → PSD → ネイティブの
    /// 往復と、書き出し側を使わずに組み立てた PSD のバイト列（読み手を自分の書き手だけで試さないため）の両方で確かめる。</summary>
    public sealed class PsdLayerFeatureTests
    {
        // ---- ネイティブ側 ----

        static PaintLayer Raster(PaintDocument d, string name, Func<int, int, Rgba32> pixel)
        {
            var layer = d.AddLayer(name); var surface = layer.GetChannel(PaintChannel.Color);
            for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++)
            {
                var c = pixel(x, y);
                if (c != Rgba32.Transparent) surface.SetPixel(x, y, c);
            }
            return layer;
        }

        static Rgba32 Gradient(int x, int y) => new Rgba32((byte)(x * 10 + 15), (byte)(y * 14 + 20), (byte)(200 - x * 5), 255);
        static Rgba32 Soft(int x, int y) => new Rgba32((byte)(255 - x * 9), (byte)(x * 7 + y * 5), (byte)(y * 13 + 40), (byte)(x < 3 ? 0 : 60 + x * 8 + y * 3));

        static void HidePixel(PaintDocument d, Guid layer, int x, int y, byte hide) => d.GetLayer(layer).Mask.Surface.SetPixel(x, y, new Rgba32(0, 0, 0, hide));

        static byte[] Matte(byte[] straight)
        {
            var m = new byte[straight.Length];
            for (int i = 0; i < straight.Length; i += 4)
            {
                for (int c = 0; c < 3; c++) m[i + c] = (byte)Math.Floor(straight[i + c] * straight[i + 3] / 255.0 + (255 - straight[i + 3]) + 0.5);
                m[i + 3] = straight[i + 3];
            }
            return m;
        }

        static byte[] Flip(byte[] rgba, int width, int height)
        {
            var f = new byte[rgba.Length];
            for (int y = 0; y < height; y++) Buffer.BlockCopy(rgba, y * width * 4, f, (height - 1 - y) * width * 4, width * 4);
            return f;
        }

        /// <summary>PSD の末尾の合成画像（無圧縮・チャンネルごと）を RGBA に。</summary>
        static byte[] StoredMerged(byte[] psd, int width, int height)
        {
            int pixels = width * height, start = psd.Length - pixels * 4;
            Assert.That(psd[start - 2] == 0 && psd[start - 1] == 0, "raw merged image");
            var rgba = new byte[pixels * 4];
            for (int c = 0; c < 4; c++) for (int i = 0; i < pixels; i++) rgba[i * 4 + c] = psd[start + c * pixels + i];
            return rgba;
        }

        static string Show(PsdReadResult r) => string.Join("; ", r.Diagnostics.Select(x => x.ToString()));

        /// <summary>書き出して読み直し、取り込む。編集可能のまま、合成の食い違いの注記も無く、取り込んだドキュメントの合成が元と同じ。</summary>
        static PaintDocument RoundTrip(PaintDocument d, out byte[] bytes, out PsdReadResult read)
        {
            var dto = PsdBridge.Export(d, PaintChannel.Color);
            bytes = PsdCodec.Write(dto);
            Assert.That(StoredMerged(bytes, d.Width, d.Height), Is.EqualTo(Matte(Flip(d.Composite(PaintChannel.Color), d.Width, d.Height))), "the merged image is the CPU composite, matted on white");
            read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), Show(read));
            Assert.That(read.Diagnostics, Is.Empty, "our own output agrees with the reference composite exactly");
            var imported = PsdBridge.Import(read);
            Assert.That(imported.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)), "the re-imported document composites byte-identically");
            Assert.That(PsdCodec.WriteEdited(read, read.Document), Is.EqualTo(bytes), "an unchanged editable import is written back identically");
            return imported;
        }

        static readonly Dictionary<LayerBlendMode, string> ExpectedKeys = new Dictionary<LayerBlendMode, string>
        {
            { LayerBlendMode.Normal, "norm" }, { LayerBlendMode.Multiply, "mul " }, { LayerBlendMode.Screen, "scrn" }, { LayerBlendMode.Overlay, "over" },
            { LayerBlendMode.Darken, "dark" }, { LayerBlendMode.Lighten, "lite" }, { LayerBlendMode.ColorDodge, "div " }, { LayerBlendMode.ColorBurn, "idiv" },
            { LayerBlendMode.LinearDodge, "lddg" }, { LayerBlendMode.LinearBurn, "lbrn" }, { LayerBlendMode.HardLight, "hLit" }, { LayerBlendMode.SoftLight, "sLit" },
            { LayerBlendMode.VividLight, "vLit" }, { LayerBlendMode.LinearLight, "lLit" }, { LayerBlendMode.PinLight, "pLit" }, { LayerBlendMode.HardMix, "hMix" },
            { LayerBlendMode.Difference, "diff" }, { LayerBlendMode.Exclusion, "smud" }, { LayerBlendMode.Subtract, "fsub" }, { LayerBlendMode.Divide, "fdiv" },
            { LayerBlendMode.Hue, "hue " }, { LayerBlendMode.Saturation, "sat " }, { LayerBlendMode.Color, "colr" }, { LayerBlendMode.Luminosity, "lum " },
            { LayerBlendMode.DarkerColor, "dkCl" }, { LayerBlendMode.LighterColor, "lgCl" },
        };

        [Test] public void EveryBlendModeHasItsPhotoshopKey()
        {
            foreach (var pair in ExpectedKeys)
            {
                Assert.That(PsdCodec.BlendKey(pair.Key), Is.EqualTo(pair.Value), pair.Key.ToString());
                Assert.That(PsdCodec.TryGetBlendMode(pair.Value, out var mode), Is.True); Assert.That(mode, Is.EqualTo(pair.Key));
            }
            foreach (LayerBlendMode mode in Enum.GetValues(typeof(LayerBlendMode)))
                if (!ExpectedKeys.ContainsKey(mode)) Assert.That(PsdCodec.BlendKey(mode), Is.Null, mode + " (groups only) has no raster-layer key");
            Assert.That(PsdCodec.TryGetBlendMode("diss", out _), Is.False);
            Assert.That(PsdCodec.TryGetBlendMode("pass", out _), Is.False);
        }

        [Test] public void EveryBlendModeRoundTripsAndCompositesIdentically()
        {
            foreach (var pair in ExpectedKeys)
            {
                var d = new PaintDocument(24, 16, 8);
                Raster(d, "below", Gradient);
                var top = Raster(d, "over", Soft);
                d.SetLayerBlendMode(top.Id, pair.Key);
                var imported = RoundTrip(d, out var bytes, out var read);
                Assert.That(Find(bytes, "8BIM" + pair.Value), Is.GreaterThan(0), pair.Key + " is written as '" + pair.Value + "'");
                Assert.That(read.Document.Layers[0].BlendMode, Is.EqualTo(pair.Key));
                Assert.That(imported.Layers[1].BlendMode, Is.EqualTo(pair.Key));
            }
        }

        [Test] public void ClippingRoundTripsWithTheClippedGroupComposite()
        {
            var d = new PaintDocument(24, 16, 8);
            var bottom = Raster(d, "bottom", (x, y) => new Rgba32(10, 20, 30, 255));
            var shape = Raster(d, "shape", (x, y) => x > 4 && x < 18 && y > 2 ? new Rgba32(200, 120, 60, (byte)(120 + x * 5)) : Rgba32.Transparent);
            var shade = Raster(d, "shade", Soft);
            var light = Raster(d, "light", (x, y) => new Rgba32(250, 240, 100, (byte)(y * 15)));
            var free = Raster(d, "free", (x, y) => x == y ? new Rgba32(0, 0, 255, 255) : Rgba32.Transparent);
            d.SetLayerClipping(shade.Id, true); d.SetLayerBlendMode(shade.Id, LayerBlendMode.Multiply);
            d.SetLayerClipping(light.Id, true); d.SetLayerBlendMode(light.Id, LayerBlendMode.Screen); d.SetLayerOpacity(light.Id, 200 / 255.0);
            d.SetLayerClipping(bottom.Id, true); // 一番下のクリッピングは効かないが、旗は残る
            var imported = RoundTrip(d, out var bytes, out var read);
            Assert.That(read.Document.Layers.Select(l => l.Clipping), Is.EqualTo(new[] { false, true, true, false, true }), "top to bottom");
            Assert.That(imported.Layers.Select(l => l.Clipping), Is.EqualTo(d.Layers.Select(l => l.Clipping)));
            Assert.That(imported.IsEffectivelyClipped(0), Is.False);
            Assert.That(read.Document.Layers[1].Opacity, Is.EqualTo(200));
        }

        [Test] public void HiddenLayersAndOpacityRoundTrip()
        {
            var d = new PaintDocument(24, 16, 8);
            Raster(d, "base", Gradient);
            var hidden = Raster(d, "hidden", (x, y) => new Rgba32(255, 0, 0, 255));
            var faint = Raster(d, "faint", Soft);
            var clipped = Raster(d, "clip on hidden", (x, y) => new Rgba32(0, 255, 0, 255));
            d.SetLayerVisibility(hidden.Id, false); d.SetLayerOpacity(faint.Id, 77 / 255.0);
            var imported = RoundTrip(d, out _, out var read);
            Assert.That(read.Document.Layers.Select(l => l.Visible), Is.EqualTo(new[] { true, true, false, true }));
            Assert.That(read.Document.Layers[1].Opacity, Is.EqualTo(77));
            Assert.That(imported.Layers[1].Visible, Is.False); Assert.That(imported.Layers[2].Opacity, Is.EqualTo(77 / 255.0));
            Assert.That(read.Document.Layers.All(l => l.Mask == null && l.BlendMode == LayerBlendMode.Normal), Is.True);
            Assert.That(clipped.Visible, Is.True);
        }

        /// <summary>マスクの往復: 有効・無効・濃度・既定色 255 / 0・レイヤーより小さい矩形。</summary>
        [TestCase(true, 255, false)] [TestCase(false, 255, false)] [TestCase(true, 128, false)] [TestCase(true, 255, true)] [TestCase(false, 40, true)]
        public void MasksRoundTrip(bool enabled, int density, bool mostlyHidden)
        {
            var d = new PaintDocument(24, 16, 8);
            Raster(d, "base", Gradient);
            var layer = Raster(d, "masked", Soft).Id;
            d.AddLayerMask(layer);
            if (mostlyHidden)
            {
                // 全体を隠し、小さな窓だけ見せる → 既定色 0 の小さな矩形で書かれる
                for (int y = 0; y < 16; y++) for (int x = 0; x < 24; x++) HidePixel(d, layer, x, y, 255);
                for (int y = 5; y < 8; y++) for (int x = 10; x < 14; x++) HidePixel(d, layer, x, y, (byte)((x - 10) * 60));
            }
            else
                for (int y = 2; y < 6; y++) for (int x = 6; x < 11; x++) HidePixel(d, layer, x, y, (byte)(x * 20 + y));
            d.SetLayerMaskEnabled(layer, enabled); d.SetLayerMaskDensity(layer, density / 255.0);

            var dto = PsdBridge.Export(d, PaintChannel.Color).Layers[0].Mask;
            Assert.That(dto.DefaultColor, Is.EqualTo(mostlyHidden ? 0 : 255));
            if (mostlyHidden) Assert.That(new[] { dto.Left, dto.Top, dto.Width, dto.Height }, Is.EqualTo(new[] { 10, 16 - 8, 4, 3 }), "top-down bounding box of the samples that are not fully hidden");
            else Assert.That(new[] { dto.Left, dto.Top, dto.Width, dto.Height }, Is.EqualTo(new[] { 6, 16 - 6, 5, 4 }), "top-down rectangle of the hidden samples");
            Assert.That(dto.Width * dto.Height, Is.LessThan(24 * 16), "smaller than the layer");

            var imported = RoundTrip(d, out _, out var read);
            var mask = read.Document.Layers[0].Mask;
            Assert.That(mask.Enabled, Is.EqualTo(enabled)); Assert.That(mask.Density, Is.EqualTo(density)); Assert.That(mask.DefaultColor, Is.EqualTo(dto.DefaultColor));
            var native = imported.Layers[1].Mask;
            Assert.That(native.Enabled, Is.EqualTo(enabled)); Assert.That(native.Density, Is.EqualTo(density / 255.0)); Assert.That(native.Inverted, Is.False);
            for (int y = 0; y < 16; y++) for (int x = 0; x < 24; x++)
                Assert.That(native.Surface.GetPixel(x, y), Is.EqualTo(d.Layers[1].Mask.Surface.GetPixel(x, y)), "mask sample " + x + "," + y);
        }

        [Test] public void AnUntouchedMaskStaysAMaskAndAClippedLayerKeepsItsOwnMask()
        {
            var d = new PaintDocument(16, 16, 8);
            Raster(d, "base", Gradient);
            var empty = Raster(d, "empty mask", Soft).Id; d.AddLayerMask(empty);
            var clip = Raster(d, "clipped", (x, y) => new Rgba32(255, 255, 0, 200)).Id; d.SetLayerClipping(clip, true);
            d.AddLayerMask(clip); for (int x = 0; x < 16; x++) HidePixel(d, clip, x, 3, 255);
            var imported = RoundTrip(d, out _, out var read);
            Assert.That(read.Document.Layers[1].Mask, Is.Not.Null, "a mask that changes nothing is still written");
            Assert.That(read.Document.Layers[1].Mask.Width * read.Document.Layers[1].Mask.Height, Is.EqualTo(1), "uniform mask: 1x1 rectangle with the default colour");
            Assert.That(imported.Layers[1].Mask, Is.Not.Null); Assert.That(imported.Layers[1].Mask.Surface.TileCount, Is.Zero);
            Assert.That(imported.Layers[2].Mask.Surface.GetPixel(5, 3).A, Is.EqualTo(255));
        }

        [Test] public void AnImportedMaskIsWrittenBackExactly()
        {
            var doc = new PsdDocument { Width = 6, Height = 4 };
            doc.Layers.Add(new PsdRasterLayer { Id = 5, Name = "m", Left = 1, Top = 0, Width = 5, Height = 4, BlendMode = LayerBlendMode.Overlay,
                PixelsRgba = Enumerable.Range(0, 5 * 4 * 4).Select(i => (byte)(i * 13)).ToArray(),
                Mask = new PsdLayerMask { Left = 2, Top = 1, Width = 3, Height = 2, DefaultColor = 0, Density = 200, Enabled = true, Pixels = new byte[] { 255, 128, 0, 1, 2, 254 } } });
            doc.Layers.Add(new PsdRasterLayer { Id = 6, Name = "b", Width = 6, Height = 4, PixelsRgba = Fill(6, 4, new Rgba32(200, 190, 180, 255)) });
            byte[] bytes = PsdCodec.Write(doc);
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), Show(read));
            var mask = read.Document.Layers[0].Mask;
            Assert.That(new[] { mask.Left, mask.Top, mask.Width, mask.Height, mask.DefaultColor, mask.Density }, Is.EqualTo(new[] { 2, 1, 3, 2, 0, 200 }));
            Assert.That(mask.Pixels, Is.EqualTo(doc.Layers[0].Mask.Pixels));
            Assert.That(PsdCodec.WriteEdited(read, read.Document), Is.EqualTo(bytes));
            Assert.That(PsdBridge.Import(read).Composite(PaintChannel.Color), Is.EqualTo(Flip(StraightOf(bytes, read), 6, 4)), "native composite equals the reference the writer stored");
        }

        /// <summary>保存された合成（白でマット済み）から、全画素不透明な場合の straight RGBA を戻す。</summary>
        static byte[] StraightOf(byte[] bytes, PsdReadResult read)
        {
            var merged = StoredMerged(bytes, read.Document.Width, read.Document.Height);
            for (int i = 3; i < merged.Length; i += 4) Assert.That(merged[i], Is.EqualTo(255), "opaque fixture");
            return merged;
        }

        [Test] public void MasksOutsideTheCanvasImportOnlyWhenNothingWouldBeCropped()
        {
            var doc = new PsdDocument { Width = 4, Height = 4 };
            doc.Layers.Add(new PsdRasterLayer { Id = 1, Width = 4, Height = 4, PixelsRgba = Enumerable.Repeat((byte)255, 64).ToArray(),
                Mask = new PsdLayerMask { Left = -2, Top = 0, Width = 4, Height = 1, DefaultColor = 255, Pixels = new byte[] { 255, 255, 0, 0 } } });
            var read = PsdCodec.Read(PsdCodec.Write(doc));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), Show(read));
            var imported = PsdBridge.Import(read);
            Assert.That(imported.Layers[0].Mask.Surface.GetPixel(0, 3).A, Is.EqualTo(255), "the on-canvas part is kept");
            doc.Layers[0].Mask.Pixels[0] = 7;
            read = PsdCodec.Read(PsdCodec.Write(doc));
            Assert.That(() => PsdBridge.Import(read), Throws.InvalidOperationException.With.Message.Contains("outside the canvas"));
        }

        [Test] public void ExportRefusesWhatPsdCannotRepresentInsteadOfFlattening()
        {
            var fill = new PaintDocument(8, 8, 8); Raster(fill, "a", Gradient);
            fill.AddFillLayer("F", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(1, 2, 3) } });
            Assert.That(() => PsdBridge.Export(fill, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("fill layers"));

            var adjustment = new PaintDocument(8, 8, 8); Raster(adjustment, "a", Gradient); adjustment.AddAdjustmentLayer("I", AdjustmentSettings.Invert());
            Assert.That(() => PsdBridge.Export(adjustment, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("adjustment layers"));

            var inverted = new PaintDocument(8, 8, 8); var layer = Raster(inverted, "a", Gradient).Id; inverted.AddLayerMask(layer); inverted.SetLayerMaskInverted(layer, true);
            Assert.That(() => PsdBridge.Export(inverted, PaintChannel.Color), Throws.InvalidOperationException.With.Message.Contains("inversion"));

            // Groups are written as PSD folders now (PsdGroupTests).

            foreach (LayerBlendMode mode in Enum.GetValues(typeof(LayerBlendMode)))
                if (PsdCodec.BlendKey(mode) == null)
                {
                    var dto = new PsdDocument { Width = 1, Height = 1 };
                    dto.Layers.Add(new PsdRasterLayer { Id = 1, Width = 1, Height = 1, BlendMode = mode, PixelsRgba = new byte[4] });
                    Assert.That(() => PsdCodec.Write(dto), Throws.ArgumentException, mode + " is never written as some other mode");
                }
        }

        [Test] public void MutatedFeatureFilesNeverEscapeTheParser()
        {
            var d = new PaintDocument(8, 6, 8);
            Raster(d, "base", Gradient);
            var top = Raster(d, "top", Soft).Id; d.SetLayerClipping(top, true); d.SetLayerBlendMode(top, LayerBlendMode.SoftLight);
            d.AddLayerMask(top); HidePixel(d, top, 4, 2, 200);
            byte[] original = PsdCodec.Write(PsdBridge.Export(d, PaintChannel.Color));
            var random = new Random(4411);
            for (int trial = 0; trial < 500; trial++)
            {
                byte[] bytes = (byte[])original.Clone();
                for (int n = 0; n < 1 + trial % 4; n++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                PsdReadResult read = null;
                Assert.DoesNotThrow(() => read = PsdCodec.Read(bytes, new PsdLimits { MaxCanvasPixels = 4096, MaxDecodedBytes = 1 << 20 }), "trial " + trial);
                if (read.Mode == PsdCompatibilityMode.EditableRaster) Assert.DoesNotThrow(() => PsdCodec.WriteEdited(read, read.Document), "trial " + trial);
                Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes));
            }
        }

        // ---- 書き出し側を使わずに組み立てる PSD ----

        sealed class FixtureLayer
        {
            public int Top, Left, Width, Height, Id;
            public byte[] Rgba;                       // 上から下、straight RGBA
            public string Blend = "norm", Name = "L";
            public byte Opacity = 255, Clipping, Flags;
            public byte[] MaskData;                   // レイヤーマスクの区画の中身（null で無し）
            public byte[] MaskValues; public int MaskWidth, MaskHeight; public bool MaskChannel = true;
            public byte[] MaskChannelOverride;        // マスクのチャンネルのバイト列をそのまま使う（壊れたデータ用）
            public byte[] Ranges;
            public bool Rle;
            public readonly List<KeyValuePair<string, byte[]>> Tags = new List<KeyValuePair<string, byte[]>>();
            public readonly List<KeyValuePair<short, byte[]>> ExtraChannels = new List<KeyValuePair<short, byte[]>>();
        }

        static void U16(Stream s, int n) { s.WriteByte((byte)(n >> 8)); s.WriteByte((byte)n); }
        static void U32(Stream s, long n) { U16(s, (int)(n >> 16)); U16(s, (int)n); }
        static void Key(Stream s, string key) { var b = Encoding.ASCII.GetBytes(key); Assert.That(b.Length, Is.EqualTo(4)); s.Write(b, 0, 4); }
        static void Block(Stream s, byte[] bytes) { U32(s, bytes.Length); s.Write(bytes, 0, bytes.Length); }

        /// <summary>1 チャンネル（圧縮の種類 + データ）。RLE は行が全部同じ値なら繰り返し、他は素の並び（幅 128 以下）。</summary>
        static byte[] Plane(byte[] values, int width, int height, bool rle)
        {
            using (var s = new MemoryStream())
            {
                U16(s, rle ? 1 : 0);
                if (!rle) { s.Write(values, 0, values.Length); return s.ToArray(); }
                var rows = new List<byte[]>();
                for (int y = 0; y < height; y++)
                {
                    var row = values.Skip(y * width).Take(width).ToArray();
                    rows.Add(row.Length > 1 && row.All(v => v == row[0]) ? new[] { (byte)(1 - row.Length), row[0] } : new[] { (byte)(row.Length - 1) }.Concat(row).ToArray());
                }
                foreach (var row in rows) U16(s, row.Length);
                foreach (var row in rows) s.Write(row, 0, row.Length);
                return s.ToArray();
            }
        }

        static byte[] Component(byte[] rgba, int c) { var v = new byte[rgba.Length / 4]; for (int i = 0; i < v.Length; i++) v[i] = rgba[i * 4 + c]; return v; }

        static byte[] MaskRecord(int top, int left, int bottom, int right, int color, int flags, params byte[] tail)
        {
            using (var s = new MemoryStream())
            {
                U32(s, top); U32(s, left); U32(s, bottom); U32(s, right); s.WriteByte((byte)color); s.WriteByte((byte)flags);
                s.Write(tail, 0, tail.Length);
                return s.ToArray();
            }
        }

        static readonly byte[] NeutralRanges = Enumerable.Range(0, 10).SelectMany(_ => new byte[] { 0, 0, 255, 255 }).ToArray();
        static byte[] ByteSetting(int value) => new byte[] { (byte)value, 0, 0, 0 };

        /// <summary>レイヤーは下から上。合成画像は straight RGBA（上から下）を渡し、ここで白にマットして無圧縮で書く。</summary>
        static byte[] Build(int width, int height, IList<FixtureLayer> bottomUp, byte[] mergedStraight)
        {
            using (var info = new MemoryStream()) using (var layers = new MemoryStream()) using (var psd = new MemoryStream())
            {
                U16(info, -bottomUp.Count);
                var channelData = new List<byte[]>();
                foreach (var l in bottomUp)
                {
                    var channels = new List<KeyValuePair<short, byte[]>>();
                    for (int c = 0; c < 4; c++) channels.Add(new KeyValuePair<short, byte[]>((short)(c == 3 ? -1 : c), Plane(Component(l.Rgba, c), l.Width, l.Height, l.Rle)));
                    if (l.MaskChannelOverride != null) channels.Add(new KeyValuePair<short, byte[]>(-2, l.MaskChannelOverride));
                    else if (l.MaskValues != null && l.MaskChannel) channels.Add(new KeyValuePair<short, byte[]>(-2, Plane(l.MaskValues, l.MaskWidth, l.MaskHeight, l.Rle)));
                    channels.AddRange(l.ExtraChannels);
                    U32(info, l.Top); U32(info, l.Left); U32(info, l.Top + l.Height); U32(info, l.Left + l.Width); U16(info, channels.Count);
                    foreach (var c in channels) { U16(info, c.Key); U32(info, c.Value.Length); channelData.Add(c.Value); }
                    Key(info, "8BIM"); Key(info, l.Blend); info.WriteByte(l.Opacity); info.WriteByte(l.Clipping); info.WriteByte(l.Flags); info.WriteByte(0);
                    using (var extra = new MemoryStream())
                    {
                        Block(extra, l.MaskData ?? new byte[0]);
                        Block(extra, l.Ranges ?? new byte[0]);
                        var name = Encoding.ASCII.GetBytes(l.Name); extra.WriteByte((byte)name.Length); extra.Write(name, 0, name.Length);
                        for (int p = (name.Length + 1) % 4; p != 0 && p < 4; p++) extra.WriteByte(0);
                        Key(extra, "8BIM"); Key(extra, "lyid"); U32(extra, 4); U32(extra, l.Id);
                        foreach (var tag in l.Tags) { Key(extra, "8BIM"); Key(extra, tag.Key); Block(extra, tag.Value); if ((tag.Value.Length & 1) != 0) extra.WriteByte(0); }
                        Block(info, extra.ToArray());
                    }
                }
                foreach (var data in channelData) info.Write(data, 0, data.Length);
                if ((info.Length & 1) != 0) info.WriteByte(0);
                Block(layers, info.ToArray()); U32(layers, 0);
                Key(psd, "8BPS"); U16(psd, 1); U32(psd, 0); U16(psd, 0); U16(psd, 4); U32(psd, height); U32(psd, width); U16(psd, 8); U16(psd, 3);
                U32(psd, 0); U32(psd, 0); Block(psd, layers.ToArray()); U16(psd, 0);
                var merged = Matte(mergedStraight);
                for (int c = 0; c < 4; c++) for (int i = 0; i < width * height; i++) psd.WriteByte(merged[i * 4 + c]);
                return psd.ToArray();
            }
        }

        static byte[] Fill(int width, int height, Rgba32 c) => Enumerable.Range(0, width * height).SelectMany(_ => new[] { c.R, c.G, c.B, c.A }).ToArray();

        /// <summary>4x2 のキャンバス。下から: 不透明な土台、乗算でクリップしたグレー（2x2 のマスク、既定色 0）、非表示の青。
        /// Photoshop と同じく Flags = 8、無効な Blend If、iOpa = 255、clbl = 1 を付ける。</summary>
        static List<FixtureLayer> Sample(bool rle)
        {
            var b = new FixtureLayer { Id = 1, Name = "base", Width = 4, Height = 2, Rgba = Fill(4, 2, new Rgba32(200, 100, 50, 255)), Flags = 8, Ranges = NeutralRanges, Rle = rle };
            b.Tags.Add(new KeyValuePair<string, byte[]>("iOpa", ByteSetting(255))); b.Tags.Add(new KeyValuePair<string, byte[]>("clbl", ByteSetting(1)));
            var clip = new FixtureLayer { Id = 2, Name = "clip", Width = 4, Height = 2, Rgba = Fill(4, 2, new Rgba32(128, 128, 128, 255)), Blend = "mul ", Clipping = 1, Flags = 8, Ranges = NeutralRanges, Rle = rle,
                MaskData = MaskRecord(0, 1, 2, 3, 0, 0, 0, 0), MaskValues = new byte[] { 255, 0, 0, 255 }, MaskWidth = 2, MaskHeight = 2 };
            clip.Tags.Add(new KeyValuePair<string, byte[]>("knko", ByteSetting(0))); clip.Tags.Add(new KeyValuePair<string, byte[]>("lspf", new byte[4]));
            var hidden = new FixtureLayer { Id = 3, Name = "hidden", Width = 1, Height = 1, Rgba = new byte[] { 0, 0, 255, 255 }, Flags = 8 | 2, Rle = rle };
            return new List<FixtureLayer> { b, clip, hidden };
        }

        /// <summary>手計算の合成（上から下）: マスクが見せる 2 画素だけが乗算 (200,100,50)×128/255 = (100,50,25)。</summary>
        static readonly byte[] SampleComposite =
        {
            200, 100, 50, 255,  100, 50, 25, 255,  200, 100, 50, 255,  200, 100, 50, 255,
            200, 100, 50, 255,  200, 100, 50, 255,  100, 50, 25, 255,  200, 100, 50, 255,
        };

        [TestCase(false)] [TestCase(true)] public void AHandBuiltPsdWithClippingMultiplyAndARleOrRawMaskIsEditable(bool rle)
        {
            byte[] bytes = Build(4, 2, Sample(rle), SampleComposite);
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), Show(read));
            Assert.That(read.Diagnostics, Is.Empty, "the stored (hand-computed) composite matches YoluPainter's compositing");
            var layers = read.Document.Layers;
            Assert.That(layers.Select(l => l.Name), Is.EqualTo(new[] { "hidden", "clip", "base" }));
            Assert.That(layers[0].Visible, Is.False); Assert.That(layers[1].Visible && layers[2].Visible, Is.True);
            Assert.That(layers[1].BlendMode, Is.EqualTo(LayerBlendMode.Multiply)); Assert.That(layers[1].Clipping, Is.True); Assert.That(layers[2].Clipping, Is.False);
            var mask = layers[1].Mask;
            Assert.That(new[] { mask.Left, mask.Top, mask.Width, mask.Height, mask.DefaultColor, mask.Density }, Is.EqualTo(new[] { 1, 0, 2, 2, 0, 255 }));
            Assert.That(mask.Enabled, Is.True); Assert.That(mask.Pixels, Is.EqualTo(new byte[] { 255, 0, 0, 255 }));
            var imported = PsdBridge.Import(read);
            Assert.That(imported.Composite(PaintChannel.Color), Is.EqualTo(Flip(SampleComposite, 4, 2)));
            Assert.That(imported.Layers[1].Mask.Surface.GetPixel(0, 1).A, Is.EqualTo(255), "outside the rectangle the default colour 0 hides");
            Assert.That(imported.Layers[1].Mask.Surface.GetPixel(1, 1).A, Is.EqualTo(0), "white shows");
            Assert.That(imported.Layers[1].Mask.Surface.GetPixel(2, 1).A, Is.EqualTo(255), "black hides");
        }

        [Test] public void ADefaultWhiteDisabledMaskWithDensityIsReadFromHandBuiltBytes()
        {
            var layers = Sample(false);
            layers[1].MaskData = MaskRecord(1, 2, 2, 4, 255, 2 | 16, 1, 128); // 既定色 255、無効、濃度 128（引数の順は上・左・下・右）
            layers[1].MaskValues = new byte[] { 0, 10 }; layers[1].MaskWidth = 2; layers[1].MaskHeight = 1;
            var expected = (byte[])SampleComposite.Clone();
            // 無効なマスクは効かない: 乗算が全面に
            for (int i = 0; i < expected.Length; i += 4) { expected[i] = 100; expected[i + 1] = 50; expected[i + 2] = 25; }
            var read = PsdCodec.Read(Build(4, 2, layers, expected));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), Show(read));
            Assert.That(read.Diagnostics, Is.Empty, Show(read));
            var mask = read.Document.Layers[1].Mask;
            Assert.That(new[] { mask.Left, mask.Top, mask.Width, mask.Height, mask.DefaultColor, mask.Density }, Is.EqualTo(new[] { 2, 1, 2, 1, 255, 128 }));
            Assert.That(mask.Enabled, Is.False);
            var native = PsdBridge.Import(read).Layers[1].Mask;
            Assert.That(native.Enabled, Is.False); Assert.That(native.Density, Is.EqualTo(128 / 255.0));
            Assert.That(native.Surface.GetPixel(2, 0).A, Is.EqualTo(255)); Assert.That(native.Surface.GetPixel(3, 0).A, Is.EqualTo(245)); Assert.That(native.Surface.GetPixel(1, 0).A, Is.EqualTo(0));
        }

        [Test] public void AStoredCompositeThatDiffersIsReportedForBlendModesAndBlocksPlainStacks()
        {
            var wrong = Fill(4, 2, new Rgba32(1, 2, 3, 255));
            var read = PsdCodec.Read(Build(4, 2, Sample(false), wrong));
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster), "another application's blend arithmetic is not a reason to lock the layers");
            Assert.That(read.Diagnostics.Single().Code, Is.EqualTo("CompositeDiffers"));
            Assert.That(read.Diagnostics.Single().Message, Does.Contain("/255"));

            var plain = Sample(false); plain.RemoveAt(1);
            Assert.That(PsdCodec.Read(Build(4, 2, plain, wrong)).Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly), "a plain normal stack has one result: a different image means stale or misread");
        }

        static FixtureLayer Clip(List<FixtureLayer> l) => l[1];

        /// <summary>表現していない機能: 文書全体を PreserveOnly にし、理由を診断に残し、原本を保ち、書き戻しを拒む。</summary>
        [TestCase("diss", "BlendMode", "diss")]
        [TestCase("pass", "BlendMode", "pass")]
        [TestCase("iOpa", "FillOpacity", "iOpa")]
        [TestCase("vmsk", "TaggedBlock", "Vector mask (vmsk)")]
        [TestCase("feather", "MaskFeather", "feather")]
        [TestCase("lsct", "SectionDivider", "lsct")]
        [TestCase("levl", "TaggedBlock", "Adjustment layer (levl)")]
        [TestCase("SoCo", "TaggedBlock", "Fill layer (SoCo)")]
        [TestCase("lfx2", "TaggedBlock", "Layer effects (lfx2)")]
        [TestCase("clbl", "ClippedBlend", "clbl")]
        [TestCase("relative", "MaskPosition", "relative")]
        [TestCase("vector+user", "UserAndVectorMask", "vector mask")]
        [TestCase("-3", "RealUserMask", "-3")]
        [TestCase("blendif", "BlendIf", "Blend-If")]
        [TestCase("clip2", "Clipping", "clipping value 2")]
        [TestCase("hidden-pixels", "LayerFlags", "irrelevant")]
        public void UnrepresentedFeaturesArePreserveOnlyWithAReason(string feature, string code, string mention)
        {
            var layers = Sample(false); var clip = Clip(layers);
            switch (feature)
            {
                case "diss": case "pass": clip.Blend = feature; break;
                case "iOpa": layers[0].Tags[0] = new KeyValuePair<string, byte[]>("iOpa", ByteSetting(128)); break;
                case "vmsk": case "lsct": case "levl": case "SoCo": case "lfx2": clip.Tags.Add(new KeyValuePair<string, byte[]>(feature, new byte[8])); break;
                case "feather": clip.MaskData = MaskRecord(0, 1, 2, 3, 0, 16, new byte[] { 2, 0x40, 0x14, 0, 0, 0, 0, 0, 0, 0, 0 }); break; // 羽毛 5.0、2 バイトの詰め物で 28
                case "clbl": layers[0].Tags[1] = new KeyValuePair<string, byte[]>("clbl", ByteSetting(0)); break;
                case "hidden-pixels": clip.Flags = 8 | 16; break;
                case "relative": clip.MaskData = MaskRecord(0, 1, 2, 3, 0, 1, 0, 0); break;
                case "vector+user": clip.MaskData = MaskRecord(0, 1, 2, 3, 0, 0, MaskRecord(0, 1, 2, 3, 0, 255).Skip(16).Concat(MaskRecord(0, 1, 2, 3, 0, 0).Take(16)).ToArray()); break;
                case "-3": clip.ExtraChannels.Add(new KeyValuePair<short, byte[]>(-3, Plane(new byte[4], 2, 2, false))); break;
                case "blendif": clip.Ranges = NeutralRanges.Select((v, i) => i == 2 ? (byte)200 : v).ToArray(); break;
                case "clip2": clip.Clipping = 2; break;
            }
            byte[] bytes = Build(4, 2, layers, SampleComposite);
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.PreserveOnly), Show(read));
            Assert.That(read.Document, Is.Null);
            Assert.That(read.Diagnostics.Any(d => d.Code == code && d.Message.IndexOf(mention, StringComparison.OrdinalIgnoreCase) >= 0), Is.True, Show(read));
            Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes));
            Assert.That(() => PsdCodec.WriteEdited(read, new PsdDocument()), Throws.InvalidOperationException);
        }

        /// <summary>壊れたマスクのデータ: 理由付きで拒否し、原本はそのまま。</summary>
        [TestCase("short", "shorter than its fixed fields")]
        [TestCase("color", "default color")]
        [TestCase("inverted-rect", "mask rectangle")]
        [TestCase("huge-rect", "mask rectangle")]
        [TestCase("truncated", "does not match its rectangle")]
        [TestCase("rle-overflow", "beyond its row width")]
        [TestCase("channel-without-data", "mask channel without")]
        [TestCase("data-without-channel", "without its mask channel")]
        [TestCase("missing-parameter", "Truncated")]
        public void MalformedMaskDataIsRefusedWithAReason(string damage, string reason)
        {
            var layers = Sample(false); var clip = Clip(layers);
            switch (damage)
            {
                case "short": clip.MaskData = new byte[4]; break;
                case "color": clip.MaskData = MaskRecord(0, 1, 2, 3, 7, 0, 0, 0); break;
                case "inverted-rect": clip.MaskData = MaskRecord(2, 1, 0, 3, 0, 0, 0, 0); break;
                case "huge-rect": clip.MaskData = MaskRecord(0, 0, 100000, 3, 0, 0, 0, 0); break;
                case "truncated": clip.MaskChannelOverride = new byte[] { 0, 0, 255, 0, 0 }; break;
                case "rle-overflow": clip.MaskChannelOverride = new byte[] { 0, 1, 0, 3, 0, 3, 127, 1, 2, 2, 1, 255 }; break;
                case "channel-without-data": clip.MaskData = null; break;
                case "data-without-channel": clip.MaskChannel = false; break;
                case "missing-parameter": clip.MaskData = MaskRecord(0, 1, 2, 3, 0, 16, 3, 128); break; // 濃度の後の羽毛 8 バイトが無い
            }
            byte[] bytes = Build(4, 2, layers, SampleComposite);
            var read = PsdCodec.Read(bytes);
            Assert.That(read.Mode, Is.EqualTo(PsdCompatibilityMode.Rejected), Show(read));
            Assert.That(read.Diagnostics.Single().Message, Does.Contain(reason));
            Assert.That(read.CopyOriginalBytes(), Is.EqualTo(bytes));
        }

        [Test] public void MaskRectanglesAndSamplesCountAgainstTheBudgets()
        {
            var layers = Sample(false); Clip(layers).MaskData = MaskRecord(0, 0, 2, 300, 0, 0, 0, 0);
            Clip(layers).MaskValues = new byte[600]; Clip(layers).MaskWidth = 300; Clip(layers).MaskHeight = 2;
            byte[] bytes = Build(4, 2, layers, SampleComposite);
            Assert.That(PsdCodec.Read(bytes).Mode, Is.Not.EqualTo(PsdCompatibilityMode.Rejected), "within the default budgets");
            var limited = PsdCodec.Read(bytes, new PsdLimits { MaxDimension = 256 });
            Assert.That(limited.Mode, Is.EqualTo(PsdCompatibilityMode.Rejected)); Assert.That(limited.Diagnostics.Single().Message, Does.Contain("mask rectangle"));
            // Layers 68 bytes + merged checks 96: 164 without the mask's 600 samples.
            Assert.That(PsdCodec.Read(Build(4, 2, Sample(false), SampleComposite), new PsdLimits { MaxDecodedBytes = 700 }).Mode, Is.EqualTo(PsdCompatibilityMode.EditableRaster));
            Assert.That(PsdCodec.Read(bytes, new PsdLimits { MaxDecodedBytes = 700 }).Mode, Is.EqualTo(PsdCompatibilityMode.Rejected), "mask samples are decoded pixels");
        }

        static int Find(byte[] data, string key)
        {
            byte[] pattern = Encoding.ASCII.GetBytes(key);
            for (int p = 0; p <= data.Length - pattern.Length; p++)
            {
                int i = 0; while (i < pattern.Length && data[p + i] == pattern[i]) i++;
                if (i == pattern.Length) return p;
            }
            return -1;
        }
    }
}
