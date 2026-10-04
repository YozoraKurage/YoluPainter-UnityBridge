using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class GradientRampTests
    {
        static readonly Rgba32 Red = new Rgba32(255, 0, 0, 255), Blue = new Rgba32(0, 0, 255, 255), White = new Rgba32(255, 255, 255, 255);
        static GradientRamp Colored() => new GradientRamp(new[] { new GradientStop(0, Red), new GradientStop(1, Blue) }, new[] { new GradientOpacityStop(0, 1), new GradientOpacityStop(1, 0) });
        internal static GeneratorSettings Plane(GradientRamp ramp = null) => GeneratorSettings.Default(GeneratorType.ShapeGradient).WithVolume(ShapeVolume.Default.WithShape(GeneratorShape.Plane).WithCenter(.5, .5, .5).WithSize(1, 1, 1)).WithBlend(GeneratorBlend.Replace).WithRamp(ramp ?? Colored());
        static (PaintDocument d, PaintLayer l, TestGeneratorInputs inputs) Scene(int w = 32, int h = 24)
        {
            var d = new PaintDocument(w, h, 8); var l = d.AddFillLayer("Gradient", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, White }, { PaintChannel.Roughness, White } });
            var inputs = new TestGeneratorInputs().Put(TestMeshMaps.Make(MeshMapKind.Position, w, h, (x, y, c) => c == 1 ? (y + .5) / h : .5));
            d.GeneratorInputs = inputs; d.ClearHistory(); return (d, l, inputs);
        }
        static byte Byte(double value) => (byte)Math.Floor(value * 255 + .5);
        [TearDown] public void ResetThreads() { CoreParallelism.MaxDegreeOfParallelism = 0; }

        [Test] public void StopsInterpolateStoredRgbAndOpacityIndependentlyWithMidpoints()
        {
            var ramp = Colored(); Assert.That(ramp.Evaluate(.5), Is.EqualTo(new Rgba32(128, 0, 128, 128)));
            Assert.That(ramp.Evaluate(1), Is.EqualTo(new Rgba32(0, 0, 255, 0)), "transparent RGB survives");
            var mid = GradientRamp.Default.WithColors(new[] { new GradientStop(0, new Rgba32(0, 0, 0, 255), .25), new GradientStop(1, White) });
            Assert.That(mid.Evaluate(.25).R, Is.EqualTo(128)); Assert.That(mid.Evaluate(.125).R, Is.EqualTo(64));
            var opacity = mid.WithOpacities(new[] { new GradientOpacityStop(0, 0, .75), new GradientOpacityStop(1, 1) });
            Assert.That(opacity.Evaluate(.75).A, Is.EqualTo(128)); Assert.That(opacity.Evaluate(.375).A, Is.EqualTo(64));
            var ends = new GradientRamp(new[] { new GradientStop(.2, Red), new GradientStop(.8, Blue) }, ramp.Opacities);
            Assert.That(ends.Evaluate(0).R, Is.EqualTo(255)); Assert.That(ends.Evaluate(1).B, Is.EqualTo(255));
        }
        [Test] public void ListsAreImmutableAndRejectNonFiniteUnorderedOrOverBudgetPoints()
        {
            var c = GradientRamp.Default.Colors.ToArray(); var ramp = new GradientRamp(c, GradientRamp.Default.Opacities); c[0] = new GradientStop(0, Red);
            Assert.That(ramp.Colors[0].Color.R, Is.Zero);
            Assert.That(() => new GradientRamp(new[] { new GradientStop(double.NaN, Red), new GradientStop(1, Blue) }, ramp.Opacities), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => new GradientRamp(new[] { new GradientStop(.8, Red), new GradientStop(.2, Blue) }, ramp.Opacities), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => new GradientRamp(new[] { new GradientStop(0, Red, 0), new GradientStop(1, Blue) }, ramp.Opacities), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => ramp.WithOpacities(new[] { new GradientOpacityStop(0, double.PositiveInfinity), new GradientOpacityStop(1, 1) }), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => ramp.WithColors(Enumerable.Range(0, GradientRamp.MaxStops + 1).Select(k => new GradientStop(k / (double)GradientRamp.MaxStops, Red))), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => ramp.WithCurve(new[] { new GradientCurvePoint(.1, 0), new GradientCurvePoint(1, 1) }), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => GeneratorSettings.Default(GeneratorType.Dirt).WithRamp(ramp), Throws.InstanceOf<ArgumentException>());
        }
        [Test] public void EveryPresetIsBoundedAndTheFadeKeepsItsForegroundRgb()
        {
            foreach (GradientPreset p in Enum.GetValues(typeof(GradientPreset))) for (int k = 0; k <= 20; k++) Assert.That(GradientRamp.Preset(p, Red, Blue).CurveValue(k / 20.0), Is.InRange(0, 1));
            var fade = GradientRamp.Preset(GradientPreset.ForegroundTransparent, Red, Blue);
            Assert.That(fade.Evaluate(1), Is.EqualTo(new Rgba32(255, 0, 0, 0))); Assert.That(fade.Evaluate(.5), Is.EqualTo(new Rgba32(255, 0, 0, 128)));
        }
        [Test] public void DirectFillUsesTheRampOnColorAndStoredValuesOnScalarChannels()
        {
            var (d, l, _) = Scene(); d.SetFillGradient(l.Id, PaintChannel.Color, Plane()); d.SetFillGradient(l.Id, PaintChannel.Roughness, Plane());
            for (int y = 0; y < d.Height; y++)
            {
                double t = TestMeshMaps.Q((y + .5) / d.Height);
                var expected = new Rgba32(Byte(1 - t), 0, Byte(t), Byte(1 - t));
                Assert.That(l.GetOutputPixel(PaintChannel.Color, 3, y), Is.EqualTo(expected));
                byte v = Byte(.2126 * (1 - t) + .0722 * t); Assert.That(l.GetOutputPixel(PaintChannel.Roughness, 3, y), Is.EqualTo(new Rgba32(v, v, v, expected.A)));
            }
            Assert.That(l.Channels, Is.Empty, "no raster source was manufactured"); Assert.That(d.AllocatedBytes, Is.Zero);
        }
        [Test] public void ADecalGradientKeepsTheBoxFacingAndSharedImageAlphaEvenWithATransparentFallback()
        {
            var (d, l, inputs) = Scene();
            var positions = TestMeshMaps.Make(MeshMapKind.Position, d.Width, d.Height, (x, y, c) => c == 0 ? (x + .5) / d.Width : c == 1 ? (y + .5) / d.Height : .5);
            var normals = TestMeshMaps.Make(MeshMapKind.WorldNormal, d.Width, d.Height, (x, y, c) => c == 2 ? (x < d.Width / 2 ? 0 : 1) : .5);
            inputs.Put(positions).Put(normals); d.RefreshGeneratorInputs();
            var resources = new ProjectResources();
            var rgba = Enumerable.Range(0, 4 * 4 * 4).Select(i => (byte)(i % 4 == 3 ? 128 : 255)).ToArray();
            var image = resources.Add("Shape", ImageContent.FromPixels(rgba, 4, 4), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            d.ImageResources = resources; d.SetFillImage(l.Id, PaintChannel.Emission, image.Id);
            d.SetFillValue(l.Id, PaintChannel.Color, Rgba32.Transparent); d.SetFillGradient(l.Id, PaintChannel.Color, Plane());
            d.SetFillProjection(l.Id, FillProjection.DecalAt(FillProjection.DefaultPlacement.WithCenter(.5, .5, .5).WithSize(.5, .5, 1)).WithCulling(1, 90, 1));
            for (int y = 9; y <= 14; y++) for (int x = 11; x <= 14; x++)
            {
                double t = TestMeshMaps.Q((y + .5) / d.Height);
                var color = Colored().Evaluate(t);
                Assert.That(l.GetOutputPixel(PaintChannel.Color, x, y), Is.EqualTo(new Rgba32(color.R, color.G, color.B, Byte(color.A / 255.0 * 128 / 255.0))));
            }
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 2, 12), Is.EqualTo(Rgba32.Transparent), "箱の外");
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 18, 12), Is.EqualTo(Rgba32.Transparent), "箱の内側の裏向きの面");
            var bytes = DocumentBinary.Write(d); var loaded = DocumentBinary.Read(bytes); loaded.GeneratorInputs = inputs; loaded.ImageResources = resources;
            Assert.That(DocumentBinary.Write(loaded), Is.EqualTo(bytes)); Assert.That(loaded.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            var notes = new List<Yozolab.YoluPainter.Core.Psd.PsdDiagnostic>(); PsdBridge.Export(d, PaintChannel.Color, notes);
            Assert.That(notes.Single().Message, Does.Contain("decal placement and culling"));
            inputs.Refuse(MeshMapKind.WorldNormal, "古い法線"); d.RefreshGeneratorInputs();
            Assert.That(d.GetFillGradientStatus(l.Id, PaintChannel.Color).Active, Is.False);
            Assert.That(d.InactiveFillGradients().Single(), Does.Contain("古い法線"));
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 12, 12), Is.EqualTo(Rgba32.Transparent));
        }
        [Test] public void GeneratorColorScalarAndMaskUseTheSameRampWithoutChangingSources()
        {
            var (d, l, _) = Scene(); var g = Plane();
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.FromGenerator(g), new[] { PaintChannel.Color, PaintChannel.Roughness });
            d.AddLayerMask(l.Id); d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.FromGenerator(g));
            for (int y = 0; y < d.Height; y++)
            {
                var color = Colored().Evaluate(TestMeshMaps.Q((y + .5) / d.Height)); var scalar = Colored().Evaluate(TestMeshMaps.Q((y + .5) / d.Height), true);
                Assert.That(l.GetOutputPixel(PaintChannel.Color, 2, y), Is.EqualTo(color)); Assert.That(l.GetOutputPixel(PaintChannel.Roughness, 2, y), Is.EqualTo(scalar));
                Assert.That(l.Mask.OutputHideAt(2, y), Is.EqualTo(255 - Byte(scalar.R / 255.0 * scalar.A / 255.0)));
            }
            Assert.That(l.FillValues[PaintChannel.Color], Is.EqualTo(White)); Assert.That(l.Mask.Surface.TileCount, Is.Zero);
        }
        [Test] public void SwitchingBetweenImageAndGradientRestoresTheSourceAndFallbackOnUndo()
        {
            var (d, l, _) = Scene(); var resources = new ProjectResources();
            var image = resources.Add("Picture", ImageContent.FromPixels(new byte[] { 30, 80, 120, 255 }, 1, 1), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            d.ImageResources = resources; d.SetFillImage(l.Id, PaintChannel.Color, image.Id); d.ClearHistory();
            var before = DocumentBinary.Write(d); d.SetFillGradient(l.Id, PaintChannel.Color, Plane()); var after = DocumentBinary.Write(d);
            Assert.That(l.FillImages, Is.Empty); d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); d.Redo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(after));
            d.SetFillImage(l.Id, PaintChannel.Color, image.Id); Assert.That(l.FillGradients, Is.Empty); d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(after));
            var curved = GradientRamp.Default.WithCurve(new[] { new GradientCurvePoint(0, 0), new GradientCurvePoint(.5, .2), new GradientCurvePoint(1, 1) });
            Assert.That(curved.SampleStops(.5).R, Is.EqualTo(128)); Assert.That(curved.Evaluate(.5).R, Is.EqualTo(51));
        }
        [Test] public void MissingStaleOrRebakedMapsInvalidateDirectFillWithAnExplicitFallback()
        {
            var (d, l, inputs) = Scene(); d.SetFillGradient(l.Id, PaintChannel.Color, Plane()); var before = d.Composite(PaintChannel.Color);
            inputs.Refuse(MeshMapKind.Position, "stale Position"); d.RefreshGeneratorInputs();
            Assert.That(d.GetFillGradientStatus(l.Id, PaintChannel.Color).Active, Is.False); Assert.That(d.InactiveGenerators().Single(), Does.Contain("stale Position"));
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 3, 4), Is.EqualTo(White));
            inputs.Put(TestMeshMaps.Make(MeshMapKind.Position, d.Width, d.Height, (x, y, c) => c == 1 ? (y + .5) / d.Height : .5)); d.RefreshGeneratorInputs();
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before));
            inputs.Put(TestMeshMaps.Make(MeshMapKind.Position, d.Width, d.Height, (x, y, c) => c == 1 ? 1 : .5)); d.RefreshGeneratorInputs();
            Assert.That(l.GetOutputPixel(PaintChannel.Color, 3, 4), Is.EqualTo(new Rgba32(0, 0, 255, 0)));
        }
        [Test] public void UndoRedoCoalescingAndCancellationRestoreAllFillSettings()
        {
            var (d, l, _) = Scene(); var before = DocumentBinary.Write(d); d.SetFillGradient(l.Id, PaintChannel.Color, Plane()); var applied = DocumentBinary.Write(d);
            Assert.That(d.UndoCount, Is.EqualTo(1)); d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); d.Redo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(applied));
            d.ClearHistory(); var g = l.FillGradients[PaintChannel.Color];
            d.SetFillGradient(l.Id, PaintChannel.Color, g.WithVolume(g.Volume.WithCenter(.6, .5, .5)), true);
            d.SetFillGradient(l.Id, PaintChannel.Color, g.WithVolume(g.Volume.WithCenter(.8, .5, .5)), true);
            Assert.That(d.UndoCount, Is.EqualTo(1)); Assert.That(d.CancelCoalescing(), Is.True); Assert.That(d.UndoCount, Is.Zero); Assert.That(DocumentBinary.Write(d), Is.EqualTo(applied));
            d.SetFillValue(l.Id, PaintChannel.Color, null); Assert.That(l.FillGradients, Is.Empty); d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(applied));
        }
        [Test] public void WrongLayerNormalLocksAndWorkingBudgetRefuseBeforeAnyMutation()
        {
            var (d, l, _) = Scene(); var raster = d.AddLayer("Paint"); d.ClearHistory(); var before = DocumentBinary.Write(d);
            Assert.That(() => d.SetFillGradient(raster.Id, PaintChannel.Color, Plane()), Throws.InvalidOperationException);
            Assert.That(() => d.SetFillGradient(l.Id, PaintChannel.Normal, Plane()), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => d.SetFillGradient(l.Id, PaintChannel.Color, Plane().WithBlend(GeneratorBlend.Multiply)), Throws.InstanceOf<ArgumentException>());
            d.FilterWorkingBudgetBytes = 1; Assert.That(() => d.SetFillGradient(l.Id, PaintChannel.Color, Plane()), Throws.InvalidOperationException.With.Message.Contains("budget"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.Zero);
            d.FilterWorkingBudgetBytes = PaintDocument.DefaultFilterWorkingBudgetBytes;
            foreach (LayerLocks locks in new[] { LayerLocks.Pixels, LayerLocks.Transparency, LayerLocks.All })
            {
                d.SetLayerLocks(l.Id, locks); var locked = DocumentBinary.Write(d); int count = d.UndoCount;
                Assert.That(() => d.SetFillGradient(l.Id, PaintChannel.Color, Plane()), Throws.TypeOf<LayerLockedException>()); Assert.That(DocumentBinary.Write(d), Is.EqualTo(locked)); Assert.That(d.UndoCount, Is.EqualTo(count));
            }
        }
        [Test] public void NativeRoundTripIncludesFillAndGeneratorRampsAndRejectsTruncationOrDowngrade()
        {
            var (d, l, inputs) = Scene(); var ramp = Colored().WithCurve(new[] { new GradientCurvePoint(0, .1), new GradientCurvePoint(.4, .8), new GradientCurvePoint(1, 1) });
            d.SetFillGradient(l.Id, PaintChannel.Color, Plane(ramp)); d.AddLayerMask(l.Id); d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.FromGenerator(Plane(ramp)));
            d.SetIdColors(new IdColorAssignments(new string('a', 64), new Dictionary<int, int> { { 3, 0x123456 } }));
            var bytes = DocumentBinary.Write(d); Assert.That(BitConverter.ToInt32(bytes, 8), Is.EqualTo(DocumentBinary.CurrentVersion));
            var loaded = DocumentBinary.Read(bytes); loaded.GeneratorInputs = inputs;
            Assert.That(DocumentBinary.Write(loaded), Is.EqualTo(bytes)); Assert.That(loaded.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            Assert.That(loaded.IdColors, Is.EqualTo(d.IdColors));
            for (int k = bytes.Length - 1; k > bytes.Length - 20; k--) Assert.That(() => DocumentBinary.Read(bytes.Take(k).ToArray()), Throws.TypeOf<InvalidDataException>());
            foreach (int version in new[] { 16, 20 })
            {
                var older = (byte[])bytes.Clone(); BitConverter.GetBytes(version).CopyTo(older, 8);
                Assert.That(() => DocumentBinary.Read(older), Throws.TypeOf<InvalidDataException>(), "勾配を導入前の版として読むと断る");
            }
            d.SetFillGradient(l.Id, PaintChannel.Color, null); // 直接の勾配の印が無くても、マスクのアルゴリズム2を版20で断る
            var generatorOnly = DocumentBinary.Write(d); BitConverter.GetBytes(20).CopyTo(generatorOnly, 8);
            Assert.That(() => DocumentBinary.Read(generatorOnly), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Generator algorithm version 2"));
        }
        [TestCase(false), TestCase(true)]
        public void AnchorReadsDirectGradientAndKeepsItsChangesUndoAndSave(bool content)
        {
            var (d, host, inputs) = Scene(); d.SetFillGradient(host.Id, PaintChannel.Height, Plane());
            var anchor = d.AddAnchor(host.Id, name: "Gradient height");
            var reader = d.AddFillLayer("Reader", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, White } });
            d.SetFillGradient(reader.Id, PaintChannel.Color, Plane()); // 同じ評価ソースが勾配と Anchor の両方を持つ
            if (!content) d.AddLayerMask(reader.Id);
            d.AddFilter(reader.Id, content ? FilterTarget.Content : FilterTarget.Mask,
                FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Anchor).WithBlend(GeneratorBlend.Replace).WithAnchor(anchor.Id, PaintChannel.Height, AnchorRead.Value)),
                content ? new[] { PaintChannel.Color } : null);
            d.ClearHistory(); Check(d);
            var before = d.Composite(PaintChannel.Color); var native = DocumentBinary.Write(d);
            var snapshot = d.CaptureSnapshot(); Assert.That(DocumentBinary.Write(snapshot), Is.EqualTo(native), "復旧の保存用の写しに勾配とAnchorを保持する");
            var loaded = DocumentBinary.Read(native); loaded.GeneratorInputs = inputs; Check(loaded);
            Assert.That(loaded.Composite(PaintChannel.Color), Is.EqualTo(before)); Assert.That(DocumentBinary.Write(loaded), Is.EqualTo(native));
            d.SetFillGradient(host.Id, PaintChannel.Height, Plane().WithVolume(Plane().Volume.WithCenter(.5, .7, .5)), true);
            d.SetFillGradient(host.Id, PaintChannel.Height, Plane().WithVolume(Plane().Volume.WithCenter(.5, .8, .5)), true);
            d.EndCoalescing(); Check(d); Assert.That(d.Composite(PaintChannel.Color), Is.Not.EqualTo(before)); Assert.That(d.UndoCount, Is.EqualTo(1));
            Assert.That(DocumentBinary.Write(snapshot), Is.EqualTo(native), "編集が進んでも保存用の写しは変わらない");
            d.Undo(); Check(d); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before));
            d.Redo(); Check(d); Assert.That(d.Composite(PaintChannel.Color), Is.Not.EqualTo(before));
            void Check(PaintDocument doc)
            {
                var h = doc.GetLayer(host.Id); var r = doc.GetLayer(reader.Id);
                for (int y = 0; y < doc.Height; y++)
                {
                    var p = h.GetOutputPixel(PaintChannel.Height, 3, y); byte v = Byte(p.R / 255.0 * (p.A / 255.0));
                    var own = Colored().Evaluate(TestMeshMaps.Q((y + .5) / doc.Height));
                    Assert.That(r.GetOutputPixel(PaintChannel.Color, 3, y), Is.EqualTo(content ? new Rgba32(v, v, v, own.A) : own), "勾配を読むAnchor " + y);
                    if (!content) Assert.That(r.Mask.OutputHideAt(3, y), Is.EqualTo(255 - v), "マスクへ渡すスカラーと覆い " + y);
                }
            }
        }
        [Test] public void PsdReportsBothAnchorsAndGradientWhileNativeKeepsThem()
        {
            var (d, l, _) = Scene(); d.SetFillGradient(l.Id, PaintChannel.Color, Plane()); d.AddLayerMask(l.Id);
            d.AddAnchor(l.Id, name: "Colour gradient"); d.AddAnchor(l.Id, AnchorPlacement.Mask, "Gradient mask");
            var before = DocumentBinary.Write(d); var notes = new List<Yozolab.YoluPainter.Core.Psd.PsdDiagnostic>();
            PsdBridge.Export(d, PaintChannel.Color, notes);
            Assert.That(notes.Count, Is.EqualTo(3)); Assert.That(notes.Count(n => n.Message.Contains("PSD has no anchor points")), Is.EqualTo(2));
            Assert.That(notes.Single(n => n.Message.Contains("world-space gradient")).Message, Does.Contain("shape, stops or curve"));
            Assert.That(() => PsdBridge.Export(d, PaintChannel.Color), Throws.InstanceOf<InvalidOperationException>());
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before), "損失のあるPSDを書いても正本を変えない");
        }
        [Test] public void NativeRejectsHugeCountsNonFiniteMidpointsAndUnorderedRampPositions()
        {
            var (d, l, _) = Scene();
            var ramp = GradientRamp.Default.WithColors(new[] { new GradientStop(0, new Rgba32(231, 32, 17, 255), .371), new GradientStop(1, new Rgba32(45, 198, 67, 255), .619) });
            d.SetFillGradient(l.Id, PaintChannel.Color, Plane(ramp)); var original = DocumentBinary.Write(d);
            byte[] marker;
            using (var stream = new MemoryStream())
            {
                using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                { w.Write(2); foreach (var stop in ramp.Colors) { w.Write(stop.Position); w.Write(stop.Color.R); w.Write(stop.Color.G); w.Write(stop.Color.B); w.Write(stop.Midpoint); } }
                marker = stream.ToArray();
            }
            var matches = Enumerable.Range(0, original.Length - marker.Length + 1).Where(k => original.Skip(k).Take(marker.Length).SequenceEqual(marker)).ToArray();
            Assert.That(matches.Length, Is.EqualTo(1)); int offset = matches[0];
            void Reject(int relative, byte[] replacement)
            { var broken = (byte[])original.Clone(); Buffer.BlockCopy(replacement, 0, broken, offset + relative, replacement.Length); Assert.That(() => DocumentBinary.Read(broken), Throws.TypeOf<InvalidDataException>()); }
            foreach (int count in new[] { -1, 0, 1, GradientRamp.MaxStops + 1, int.MaxValue }) Reject(0, BitConverter.GetBytes(count));
            Reject(4, BitConverter.GetBytes(double.NaN)); Reject(4 + 8 + 3, BitConverter.GetBytes(0.0));
            Reject(4 + 19, BitConverter.GetBytes(0.0)); // 次の位置が前の点と重なる
            Reject(marker.Length, BitConverter.GetBytes(int.MaxValue)); // 不透明度の数
            Reject(marker.Length + 4 + 8, BitConverter.GetBytes(double.PositiveInfinity));
            int curve = marker.Length + 4 + 2 * 24; Reject(curve, BitConverter.GetBytes(GradientRamp.MaxCurvePoints + 1));
            Reject(curve + 4, BitConverter.GetBytes(.1));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(original), "rejected bytes never mutate the original document");
        }
        [Test] public void OldVersionSixteenStillReadsWithNoRampAndTheSamePixels()
        {
            var (d, l, inputs) = Scene(); d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.FromGenerator(Plane().WithRamp(null)), new[] { PaintChannel.Color });
            var expected = d.Composite(PaintChannel.Color); var bytes = DocumentBinary.Write(d); Buffer.BlockCopy(BitConverter.GetBytes(16), 0, bytes, 8, 4);
            var loaded = DocumentBinary.Read(bytes); loaded.GeneratorInputs = inputs;
            Assert.That(loaded.Layers[0].Filters[0].Settings.Generator.Ramp, Is.Null); Assert.That(loaded.Composite(PaintChannel.Color), Is.EqualTo(expected));
        }
        [Test] public void DuplicateAndResizeKeepEditableStopsAndPlacement()
        {
            var (d, l, _) = Scene(); d.SetFillGradient(l.Id, PaintChannel.Color, Plane()); var duplicate = d.DuplicateLayer(l.Id);
            Assert.That(duplicate.FillGradients[PaintChannel.Color], Is.EqualTo(l.FillGradients[PaintChannel.Color]));
            var resized = d.Resampled(48, 48, CanvasResampling.Bilinear).Document; Assert.That(resized.Layers[0].FillGradients[PaintChannel.Color], Is.EqualTo(l.FillGradients[PaintChannel.Color]));
            Assert.That(resized.Layers[0].Channels, Is.Empty);
        }
        [Test] public void ThreadAndBlockChoicesGiveByteIdenticalDirectFillAndFilteredOutput()
        {
            var (d, l, _) = Scene(96, 64); d.SetFillGradient(l.Id, PaintChannel.Color, Plane()); d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Invert(), new[] { PaintChannel.Color });
            byte[] expected = null;
            foreach (int degree in new[] { 1, 3, 0 }) foreach (int block in new[] { 8, 32, 256 })
            { CoreParallelism.MaxDegreeOfParallelism = degree; d.FilterBlockPixels = block; d.FilterCacheBudgetBytes = 0; var pixels = d.Composite(PaintChannel.Color); if (expected == null) expected = pixels; Assert.That(pixels, Is.EqualTo(expected)); }
        }
        [Test] public void PsdWritesRasterPixelsAndReportsTheLostGradientEditing()
        {
            var (d, l, _) = Scene(); d.SetFillGradient(l.Id, PaintChannel.Color, Plane());
            Assert.That(() => PsdBridge.Export(d, PaintChannel.Color), Throws.InvalidOperationException);
            var notes = new List<Yozolab.YoluPainter.Core.Psd.PsdDiagnostic>(); var psd = PsdBridge.Export(d, PaintChannel.Color, notes);
            Assert.That(psd.Layers.Single().IsFill, Is.False); Assert.That(notes.Single().Message, Does.Contain("gradient").And.Contain("stops"));
            var restored = PsdBridge.Import(Yozolab.YoluPainter.Core.Psd.PsdCodec.Read(Yozolab.YoluPainter.Core.Psd.PsdCodec.Write(psd)));
            Assert.That(restored.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
        }
    }
}
