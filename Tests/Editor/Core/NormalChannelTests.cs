using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>Normal チャンネルの専用合成（単位ベクトルとしての重ね・Overlay の RNM・正規化）と Height → Normal（Sobel、強さ、端、
    /// OpenGL/DirectX）、出力（平らな法線の上に置いて不透明に）の CPU 正本。期待値は手計算（Python で同じ式を倍精度で計算して丸めたもの）。
    /// 設定の Undo・保存（版 7）と古い版の読み込み・PSD の書き出し・型と予算の拒否も確かめる。</summary>
    public sealed class NormalChannelTests
    {
        static readonly Rgba32 Flat = new Rgba32(128, 128, 255), TiltX = new Rgba32(255, 128, 128), Tilt45 = new Rgba32(218, 128, 218);

        static PaintLayer Fill(PaintDocument d, string name, PaintChannel channel, Func<int, int, Rgba32> value)
        {
            var layer = d.AddLayer(name); d.SetChannelEnabled(layer.Id, channel, true); var s = layer.GetChannel(channel);
            for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++) { var v = value(x, y); if (v.A != 0 || v.R != 0 || v.G != 0 || v.B != 0) s.SetPixel(x, y, v); }
            return layer;
        }
        static Rgba32 At(byte[] rgba, int width, int x, int y) { int i = (y * width + x) * 4; return new Rgba32(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]); }
        static NormalSettings Derive(double strength, HeightEdgeMode edges = HeightEdgeMode.Clamp, NormalYDirection direction = NormalYDirection.OpenGL)
            => new NormalSettings(true, strength, edges, direction);

        // ───────────── 合成 ─────────────

        [Test] public void PartialCoverageIsARenormalizedAverageNotAByteLerp()
        {
            // 平ら (0,0,1) と真横 (1,0,0) の半々は 45° の (0.707, 0, 0.707) → (218, 128, 218)。バイトの線形補間なら (192, 128, 192)（長さ 0.71）
            Assert.That(NormalMaps.Blend(Flat, TiltX, .5), Is.EqualTo(new Rgba32(218, 128, 218)));
            Assert.That(CpuCompositor.Blend(Flat, TiltX, .5), Is.EqualTo(new Rgba32(192, 128, 192)), "colour channels still blend as colour");
            var d = new PaintDocument(8, 8, 8);
            var below = Fill(d, "Below", PaintChannel.Normal, (x, y) => Flat); var above = Fill(d, "Above", PaintChannel.Normal, (x, y) => TiltX);
            below.GetChannel(PaintChannel.Color).SetPixel(1, 1, Flat); above.GetChannel(PaintChannel.Color).SetPixel(1, 1, TiltX);
            d.SetLayerOpacity(above.Id, .5);
            Assert.That(d.CompositePixel(PaintChannel.Normal, 1, 1), Is.EqualTo(new Rgba32(218, 128, 218)));
            Assert.That(d.Composite(PaintChannel.Normal).Skip(4 * 9).Take(4), Is.EqualTo(new byte[] { 218, 128, 218, 255 }), "the tile path agrees");
            Assert.That(d.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(192, 128, 192)), "the same layers in Color are unchanged");
            // 1 枚だけでも正規化する: (200, 128, 230) は長さ 0.985 → (201, 128, 232)
            var single = new PaintDocument(8, 8, 8); Fill(single, "One", PaintChannel.Normal, (x, y) => new Rgba32(200, 128, 230));
            Assert.That(single.CompositePixel(PaintChannel.Normal, 0, 0), Is.EqualTo(new Rgba32(201, 128, 232)));
        }

        [Test] public void OverlayReorientsTheDetailAndOtherModesReplace()
        {
            // 45° の上に 45° の細部 → 90°（RNM はベースの向きに細部を回す）。半分なら 45° と 90° の間
            Assert.That(NormalMaps.Blend(Tilt45, Tilt45, 1, LayerBlendMode.Overlay), Is.EqualTo(new Rgba32(255, 128, 127)));
            Assert.That(NormalMaps.Blend(Tilt45, Tilt45, .5, LayerBlendMode.Overlay), Is.EqualTo(new Rgba32(245, 128, 176)));
            // 平らな細部・平らなベースは恒等（倍精度で厳密に）
            NormalMaps.Rnm(.6, -.0, .8, 0, 0, 1, out double x, out double y, out double z);
            Assert.That((x, y, z), Is.EqualTo((.6, 0.0, .8)));
            NormalMaps.Rnm(0, 0, 1, .6, -.0, .8, out x, out y, out z);
            Assert.That((x, y, z), Is.EqualTo((.6, 0.0, .8)));
            foreach (LayerBlendMode mode in Enum.GetValues(typeof(LayerBlendMode)))
            {
                Assert.That(NormalMaps.IsVectorMode(mode), Is.EqualTo(mode == LayerBlendMode.Normal || mode == LayerBlendMode.PassThrough || mode == LayerBlendMode.Overlay), mode.ToString());
                if (mode == LayerBlendMode.Overlay) continue;
                Assert.That(NormalMaps.Blend(Tilt45, new Rgba32(60, 200, 210, 200), .6, mode), Is.EqualTo(NormalMaps.Blend(Tilt45, new Rgba32(60, 200, 210, 200), .6)), mode + " has no vector meaning and replaces");
            }
            Assert.That(() => NormalMaps.Blend(Flat, TiltX, 1.5), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => NormalMaps.Blend(Flat, TiltX, 1, (LayerBlendMode)99), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test] public void TransparentPixelsKeepTheBackdropAndItsHiddenRgb()
        {
            var hidden = new Rgba32(10, 20, 30, 0);
            Assert.That(NormalMaps.Blend(hidden, new Rgba32(255, 0, 0, 0)), Is.EqualTo(hidden), "a transparent source changes nothing, not even hidden RGB");
            Assert.That(NormalMaps.Blend(hidden, TiltX, 0), Is.EqualTo(hidden));
            Assert.That(NormalMaps.ClipOnto(hidden, TiltX, 1, LayerBlendMode.Normal), Is.EqualTo(hidden), "nothing to clip to");
            // 下が透明: 上の法線（正規化）がそのアルファで残る
            Assert.That(NormalMaps.Blend(Rgba32.Transparent, new Rgba32(200, 90, 230, 128)), Is.EqualTo(new Rgba32(198, 91, 227, 128)));
            var d = new PaintDocument(8, 8, 8); var layer = Fill(d, "L", PaintChannel.Normal, (x, y) => x == 3 ? new Rgba32(41, 53, 67, 0) : Flat);
            Assert.That(layer.GetPixel(PaintChannel.Normal, 3, 0), Is.EqualTo(new Rgba32(41, 53, 67, 0)), "the source keeps its hidden RGB");
            Assert.That(d.CompositePixel(PaintChannel.Normal, 3, 0).A, Is.Zero);
        }

        [TestCase(5)] [TestCase(23)]
        public void TileWiseNormalCompositeMatchesThePerPixelReferenceWithGroupsClippingAndMasks(int seed)
        {
            var random = new Random(seed); var d = new PaintDocument(37, 29, 8);
            Rgba32 Any() { var c = new byte[4]; random.NextBytes(c); return new Rgba32(c[0], c[1], (byte)(128 + c[2] / 2), c[3]); }
            var group = d.AddGroup("G"); d.SetLayerOpacity(group.Id, .6);
            var layers = new List<PaintLayer>();
            for (int l = 0; l < 5; l++)
            {
                var layer = d.AddLayer("L" + l);
                if (l < 2) d.MoveLayerTo(layer.Id, group.Id, l); // 下の 2 枚は通過グループ（不透明度 0.6 でフェード）の中
                var s = layer.GetChannel(PaintChannel.Normal); d.SetChannelEnabled(layer.Id, PaintChannel.Normal, true);
                for (int n = 0; n < 400; n++) s.SetPixel(random.Next(37), random.Next(29), Any());
                d.SetLayerBlendMode(layer.Id, new[] { LayerBlendMode.Normal, LayerBlendMode.Overlay, LayerBlendMode.Multiply }[l % 3]);
                d.SetLayerOpacity(layer.Id, .3 + .14 * l);
                layers.Add(layer);
            }
            d.SetLayerClipping(layers[3].Id, true);
            var mask = d.AddLayerMask(layers[2].Id);
            for (int n = 0; n < 200; n++) mask.Surface.SetPixel(random.Next(37), random.Next(29), new Rgba32(0, 0, 0, (byte)random.Next(256)));
            var isolated = d.AddGroup("Isolated"); d.SetLayerBlendMode(isolated.Id, LayerBlendMode.Overlay); d.MoveLayerTo(layers[4].Id, isolated.Id, 0);
            var reference = new byte[37 * 29 * 4];
            for (int y = 0; y < 29; y++) for (int x = 0; x < 37; x++)
            { var p = d.CompositePixel(PaintChannel.Normal, x, y); int i = (y * 37 + x) * 4; reference[i] = p.R; reference[i + 1] = p.G; reference[i + 2] = p.B; reference[i + 3] = p.A; }
            Assert.That(d.Composite(PaintChannel.Normal), Is.EqualTo(reference));
        }

        // ───────────── 出力と Height → Normal ─────────────

        [Test] public void OutputFlattensOntoAFlatNormalAndIsOpaque()
        {
            var d = new PaintDocument(8, 8, 8);
            Assert.That(NormalMaps.Output(d).Select((b, i) => (b, i)).All(p => p.b == new byte[] { 128, 128, 255, 255 }[p.i % 4]), Is.True, "unpainted is flat and opaque");
            Fill(d, "Half", PaintChannel.Normal, (x, y) => x == 2 ? new Rgba32(255, 128, 128, 128) : Rgba32.Transparent);
            var output = NormalMaps.Output(d);
            Assert.That(At(output, 8, 2, 5), Is.EqualTo(new Rgba32(218, 128, 217)), "half coverage of +X over flat");
            Assert.That(At(output, 8, 3, 5), Is.EqualTo(Flat));
            Assert.That(NormalMaps.DerivesNormal(d), Is.False);
        }

        [Test] public void HeightRampDerivesTheExpectedSlopeAndYDirection()
        {
            // h = 2x / 255、強さ 127.5 → 傾き 1（45°）。X が増すと高くなるので法線は −X へ: (37, 128, 218)
            var d = new PaintDocument(16, 16, 8); Fill(d, "Ramp", PaintChannel.Height, (x, y) => new Rgba32((byte)(2 * x), (byte)(2 * x), (byte)(2 * x)));
            d.SetNormalSettings(Derive(127.5));
            Assert.That(NormalMaps.DerivesNormal(d), Is.True);
            var output = NormalMaps.Output(d);
            Assert.That(At(output, 16, 5, 9), Is.EqualTo(new Rgba32(37, 128, 218)));
            Assert.That(At(output, 16, 0, 9), Is.EqualTo(new Rgba32(70, 128, 242)), "clamp: the edge texel repeats, half the slope");
            d.SetNormalSettings(Derive(127.5, HeightEdgeMode.Wrap));
            output = NormalMaps.Output(d);
            Assert.That(At(output, 16, 5, 9), Is.EqualTo(new Rgba32(37, 128, 218)), "inside, the edge rule does not matter");
            // 回り込み: x = 0 の左は x = 15（h = 30/255）。gx = (2 − 30) × 4 / 8 / 255 → 法線は +X へ大きく傾く
            Assert.That(At(output, 16, 0, 9), Is.EqualTo(new Rgba32(254, 128, 146)));
            Assert.That(NormalMaps.DeriveFromHeight(d, PaintChannel.Height, d.NormalSettings), Is.EqualTo(output), "without Normal layers the output is the derived normal");
            // Y: 上（+y、左下原点）ほど高い → 法線は −Y へ。OpenGL は G = 37、DirectX のファイルは 255 − 37 = 218
            var up = new PaintDocument(16, 16, 8); Fill(up, "RampY", PaintChannel.Height, (x, y) => new Rgba32((byte)(2 * y), 0, 0));
            up.SetNormalSettings(Derive(127.5));
            Assert.That(At(NormalMaps.Output(up), 16, 7, 7), Is.EqualTo(new Rgba32(128, 37, 218)));
            Assert.That(At(NormalMaps.FileOutput(up), 16, 7, 7), Is.EqualTo(new Rgba32(128, 37, 218)));
            up.SetNormalSettings(Derive(127.5, direction: NormalYDirection.DirectX));
            Assert.That(At(NormalMaps.FileOutput(up), 16, 7, 7), Is.EqualTo(new Rgba32(128, 218, 218)), "DirectX inverts green in files");
            Assert.That(At(NormalMaps.Output(up), 16, 7, 7), Is.EqualTo(new Rgba32(128, 37, 218)), "the Unity-facing output stays OpenGL");
            // 高さは R × A（塗っていない所は 0）: 半透明の段差は半分の高さ
            var alpha = new PaintDocument(16, 16, 8); Fill(alpha, "A", PaintChannel.Height, (x, y) => new Rgba32(255, 0, 0, (byte)(2 * x)));
            alpha.SetNormalSettings(Derive(127.5));
            Assert.That(At(NormalMaps.Output(alpha), 16, 5, 9), Is.EqualTo(new Rgba32(37, 128, 218)), "255 × 2x / 255² is the same ramp");
        }

        [Test] public void DerivedNormalSitsUnderThePaintedNormal()
        {
            var d = new PaintDocument(16, 16, 8);
            Fill(d, "Ramp", PaintChannel.Height, (x, y) => new Rgba32((byte)(2 * x), 0, 0));
            var painted = Fill(d, "Painted", PaintChannel.Normal, (x, y) => y >= 8 ? Tilt45 : Rgba32.Transparent);
            Assert.That(At(NormalMaps.Output(d), 16, 5, 12), Is.EqualTo(Tilt45), "derivation off: the painted normal");
            Assert.That(At(NormalMaps.Output(d), 16, 5, 3), Is.EqualTo(Flat));
            d.SetNormalSettings(Derive(127.5));
            var output = NormalMaps.Output(d);
            Assert.That(At(output, 16, 5, 3), Is.EqualTo(new Rgba32(37, 128, 218)), "unpainted: the derived normal exactly");
            Assert.That(At(output, 16, 5, 12), Is.EqualTo(Flat), "−45° derived base with a +45° painted detail cancels (RNM)");
            // 帯ごとの計算（タイル 8 の帯）と、全面の合成からの計算が同じ（GPU の出力パスと同じ入力の形）
            var settings = Derive(9.25, HeightEdgeMode.Wrap); d.SetNormalSettings(settings);
            Assert.That(NormalMaps.Output(d), Is.EqualTo(NormalMaps.OutputFromComposites(d.Composite(PaintChannel.Normal), d.Composite(PaintChannel.Height), 16, 16, settings)));
            d.SetLayerVisibility(painted.Id, false);
            Assert.That(NormalMaps.Output(d), Is.EqualTo(NormalMaps.DeriveFromHeight(d, PaintChannel.Height, settings)));
        }

        [Test] public void OddSizesAndManyBandsAgreeWithTheFullFrameReference()
        {
            var random = new Random(7); var d = new PaintDocument(41, 35, 8);
            Fill(d, "H", PaintChannel.Height, (x, y) => new Rgba32((byte)random.Next(256), 0, 0, (byte)random.Next(256)));
            Fill(d, "N", PaintChannel.Normal, (x, y) => new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)(128 + random.Next(128)), (byte)random.Next(256)));
            foreach (var edges in new[] { HeightEdgeMode.Clamp, HeightEdgeMode.Wrap })
                foreach (double strength in new[] { 0, 3.5, -40 })
                {
                    var settings = Derive(strength, edges); d.SetNormalSettings(settings);
                    Assert.That(NormalMaps.Output(d), Is.EqualTo(NormalMaps.OutputFromComposites(d.Composite(PaintChannel.Normal), d.Composite(PaintChannel.Height), 41, 35, settings)), edges + " " + strength);
                }
        }

        // ───────────── 型・予算・値の拒否 ─────────────

        [Test] public void HeightToNormalRefusesOtherChannelsBudgetsAndInvalidSettings()
        {
            var d = new PaintDocument(64, 64, 16); Fill(d, "R", PaintChannel.Roughness, (x, y) => new Rgba32((byte)x, 0, 0));
            foreach (PaintChannel channel in Enum.GetValues(typeof(PaintChannel)))
                if (channel != PaintChannel.Height)
                    Assert.That(() => NormalMaps.DeriveFromHeight(d, channel, Derive(4)), Throws.InvalidOperationException.With.Message.Contains("not one"), channel.ToString());
            Assert.That(() => NormalMaps.DeriveFromHeight(d, (PaintChannel)42, Derive(4)), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(NormalMaps.WorkingBytes(d), Is.EqualTo(4L * 64 * 64 + 4L * 64 * 16), "output + one band");
            d.SetNormalSettings(Derive(4));
            Assert.That(NormalMaps.WorkingBytes(d), Is.EqualTo(4L * 64 * 64 + 4L * 64 * 16 + 12L * 64 * 18), "plus the height band with its halo");
            Assert.That(() => NormalMaps.Output(d, NormalMaps.WorkingBytes(d) - 1), Throws.InvalidOperationException.With.Message.Contains("budget"));
            Assert.That(() => NormalMaps.FileOutput(d, 1024), Throws.InvalidOperationException.With.Message.Contains("budget"));
            Assert.That(() => NormalMaps.DeriveFromHeight(d, PaintChannel.Height, Derive(4), 1024), Throws.InvalidOperationException.With.Message.Contains("budget"));
            Assert.That(NormalMaps.Output(d, NormalMaps.WorkingBytes(d)).Length, Is.EqualTo(64 * 64 * 4), "exactly the budget is enough");
            Assert.That(() => new NormalSettings(true, double.NaN, HeightEdgeMode.Clamp, NormalYDirection.OpenGL), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new NormalSettings(true, 300, HeightEdgeMode.Clamp, NormalYDirection.OpenGL), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new NormalSettings(true, -300, HeightEdgeMode.Clamp, NormalYDirection.OpenGL), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new NormalSettings(true, 1, (HeightEdgeMode)5, NormalYDirection.OpenGL), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new NormalSettings(true, 1, HeightEdgeMode.Clamp, (NormalYDirection)2), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => NormalMaps.OutputFromComposites(new byte[16], null, 2, 2, Derive(1)), Throws.ArgumentException, "deriving needs the height composite");
            Assert.That(() => d.SetNormalSettings(null), Throws.ArgumentNullException);
        }

        // ───────────── Undo・保存 ─────────────

        [Test] public void NormalSettingsAreOneUndoStepAndSliderDragsCoalesce()
        {
            var d = new PaintDocument(16, 16, 8); var layer = Fill(d, "N", PaintChannel.Normal, (x, y) => Flat); d.ClearHistory();
            long serial = d.ChangeSerial, revision = d.Revision;
            d.SetNormalSettings(NormalSettings.Default.WithDerive(true));
            Assert.That(d.UndoCount, Is.EqualTo(1)); Assert.That(d.Revision, Is.GreaterThan(revision));
            var changed = new HashSet<TileCoord>(); d.TryGetChangedTiles(PaintChannel.Normal, serial, changed);
            Assert.That(changed, Is.Empty, "the settings change no layer composite, only the output");
            d.SetNormalSettings(NormalSettings.Default.WithDerive(true));
            Assert.That(d.UndoCount, Is.EqualTo(1), "an unchanged value adds no step");
            for (int i = 5; i <= 9; i++) d.SetNormalSettings(d.NormalSettings.WithStrength(i), coalesce: true);
            Assert.That(d.UndoCount, Is.EqualTo(2), "a slider drag is one step"); Assert.That(d.NormalSettings.Strength, Is.EqualTo(9));
            d.EndCoalescing(); d.SetNormalSettings(d.NormalSettings.WithFileDirection(NormalYDirection.DirectX).WithEdges(HeightEdgeMode.Wrap));
            d.Undo(); Assert.That(d.NormalSettings, Is.EqualTo(new NormalSettings(true, 9, HeightEdgeMode.Clamp, NormalYDirection.OpenGL)));
            d.Undo(); Assert.That(d.NormalSettings.Strength, Is.EqualTo(4), "back to before the drag");
            d.Undo(); Assert.That(d.NormalSettings, Is.EqualTo(NormalSettings.Default));
            d.Redo(); d.Redo(); d.Redo(); Assert.That(d.NormalSettings, Is.EqualTo(new NormalSettings(true, 9, HeightEdgeMode.Wrap, NormalYDirection.DirectX)));
            var stroke = d.BeginStroke(layer.Id, PaintChannel.Normal, new BrushSettings { Color = TiltX });
            Assert.That(() => d.SetNormalSettings(NormalSettings.Default), Throws.InvalidOperationException, "not during a stroke");
            stroke.Cancel(); stroke.Dispose();
            Assert.That(d.NormalSettings.Edges, Is.EqualTo(HeightEdgeMode.Wrap));
        }

        [Test] public void NativeArchiveRoundTripsNormalSettingsAndReadsVersion6()
        {
            var d = new PaintDocument(16, 16, 8, id: Guid.NewGuid()); var layer = d.AddLayer("p");
            layer.GetChannel(PaintChannel.Color).SetPixel(1, 2, new Rgba32(9, 8, 7));
            var plain = DocumentBinary.Write(d);
            Assert.That(BitConverter.ToInt32(plain, 8), Is.EqualTo(DocumentBinary.CurrentVersion));
            var restoredPlain = DocumentBinary.Read(ArchiveTestUtil.AsVersion(plain, "p", 6));
            Assert.That(restoredPlain.NormalSettings, Is.EqualTo(NormalSettings.Default), "version 6 has no Normal settings");
            Assert.That(restoredPlain.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            Assert.That(DocumentBinary.Write(restoredPlain), Is.EqualTo(plain), "rewritten as the current version with the defaults");
            var settings = new NormalSettings(true, 12.5, HeightEdgeMode.Wrap, NormalYDirection.DirectX);
            d.SetNormalSettings(settings);
            var bytes = DocumentBinary.Write(d); var restored = DocumentBinary.Read(bytes);
            Assert.That(restored.NormalSettings, Is.EqualTo(settings)); Assert.That(restored.CanUndo, Is.False);
            Assert.That(DocumentBinary.Write(restored), Is.EqualTo(bytes));
            int at = ArchiveTestUtil.NormalSettingsOffset;
            byte[] Tampered(int offset, byte[] value) { var t = (byte[])bytes.Clone(); value.CopyTo(t, at + offset); return t; }
            Assert.That(() => DocumentBinary.Read(Tampered(0, BitConverter.GetBytes(NormalSettings.AlgorithmVersion + 1))), Throws.TypeOf<InvalidDataException>().With.Message.Contains("algorithm"));
            Assert.That(() => DocumentBinary.Read(Tampered(5, BitConverter.GetBytes(double.NaN))), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => DocumentBinary.Read(Tampered(5, BitConverter.GetBytes(1e6))), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => DocumentBinary.Read(Tampered(13, BitConverter.GetBytes(7))), Throws.TypeOf<InvalidDataException>(), "unknown edge mode");
            Assert.That(() => DocumentBinary.Read(Tampered(17, BitConverter.GetBytes(-1))), Throws.TypeOf<InvalidDataException>(), "unknown Y direction");
            Assert.That(() => DocumentBinary.Read(bytes.Take(at + 10).ToArray()), Throws.TypeOf<InvalidDataException>(), "truncated settings");
        }

        // ───────────── PSD ─────────────

        static byte[] FlipRows(byte[] rgba, int width, int height)
        {
            var flipped = new byte[rgba.Length]; int row = width * 4;
            for (int y = 0; y < height; y++) Buffer.BlockCopy(rgba, y * row, flipped, (height - 1 - y) * row, row);
            return flipped;
        }

        [Test] public void PsdExportOfTheNormalChannelCarriesTheEvaluatedOutput()
        {
            var d = new PaintDocument(16, 16, 8);
            Fill(d, "Ramp", PaintChannel.Height, (x, y) => new Rgba32((byte)(2 * x), 0, 0));
            var n = Fill(d, "N", PaintChannel.Normal, (x, y) => x < 4 ? new Rgba32(200, 60, 230, 200) : Rgba32.Transparent);
            n.GetChannel(PaintChannel.Color).SetPixel(0, 0, new Rgba32(1, 2, 3));
            d.SetNormalSettings(Derive(20));
            var psd = PsdBridge.Export(d, PaintChannel.Normal);
            Assert.That(psd.CompositeRgba, Is.EqualTo(FlipRows(NormalMaps.Output(d), 16, 16)), "the merged image is the evaluated output (derived + painted, opaque)");
            var layer = psd.Layers.Single(l => l.Name == "N");
            Assert.That(layer.PixelsRgba.Take(4), Is.EqualTo(new byte[] { 200, 60, 230, 200 }), "layers keep the painted pixels");
            Assert.That(psd.Layers.Select(l => l.Name), Is.EquivalentTo(new[] { "Ramp", "N" }), "the derived normal is not a layer");
            d.SetNormalSettings(d.NormalSettings.WithFileDirection(NormalYDirection.DirectX));
            psd = PsdBridge.Export(d, PaintChannel.Normal);
            Assert.That(psd.CompositeRgba, Is.EqualTo(FlipRows(NormalMaps.FileOutput(d), 16, 16)));
            Assert.That(psd.Layers.Single(l => l.Name == "N").PixelsRgba.Take(4), Is.EqualTo(new byte[] { 200, 195, 230, 200 }), "DirectX inverts green in the layers too");
            Assert.That(PsdBridge.Export(d, PaintChannel.Color).CompositeRgba, Is.EqualTo(FlipRows(d.Composite(PaintChannel.Color), 16, 16)), "other channels are unchanged");
        }
    }
}
