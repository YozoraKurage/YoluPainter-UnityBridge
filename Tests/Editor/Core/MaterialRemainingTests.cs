using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor.Preview;
using Tri = System.ValueTuple<double, double, double, double, double, double>;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>組のポリゴン・パス・二素材補間を、単一チャンネルの結果と独立に比較する。</summary>
    public sealed class MaterialRemainingTests
    {
        static readonly ChannelPaint[] Material = {
            new ChannelPaint(PaintChannel.Color, new Rgba32(210, 30, 75, 215)),
            new ChannelPaint(PaintChannel.Roughness, new Rgba32(65, 65, 65, 180)),
            new ChannelPaint(PaintChannel.Metallic, new Rgba32(230, 230, 230)),
            new ChannelPaint(PaintChannel.Height, new Rgba32(151, 151, 151, 205)),
            new ChannelPaint(PaintChannel.Normal, new Rgba32(177, 101, 233)),
            new ChannelPaint(PaintChannel.Emission, new Rgba32(15, 190, 90, 192)) };
        static readonly Tri[] Triangles = { (2.25, 3.125, 28.5, 5.5, 25.125, 29.75), (2.25, 3.125, 25.125, 29.75, 3.5, 25.75), (6.1, 5.4, 30.5, 15.125, 9.5, 30.25) };
        static readonly Tri[] Whole = { (0, 0, 32, 0, 32, 32), (0, 0, 32, 32, 0, 32) };
        static PaintDocument Doc(out PaintLayer layer, bool pixels = false)
        {
            var d = new PaintDocument(32, 32, 8); layer = d.AddLayer("paint");
            if (pixels) foreach (var m in Material)
            {
                d.SetChannelEnabled(layer.Id, m.Channel, true);
                for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
                    layer.GetChannel(m.Channel).SetPixel(x, y, new Rgba32((byte)(x * 7), (byte)(y * 8), (byte)(x + y * 3), (byte)((x + y) % 4 * 85)));
            }
            d.ClearHistory(); return d;
        }
        static byte[] Pixels(SparseTileSurface s) => Enumerable.Range(0, s.Height).SelectMany(y => Enumerable.Range(0, s.Width)
            .SelectMany(x => { var p = s.GetPixel(x, y); return new[] { p.R, p.G, p.B, p.A }; })).ToArray();
        static void RoundTrip(PaintDocument d)
        {
            var bytes = DocumentBinary.Write(d); Assert.That(BitConverter.ToInt32(bytes, 8), Is.EqualTo(DocumentBinary.CurrentVersion)); var loaded = DocumentBinary.Read(bytes);
            Assert.That(DocumentBinary.Write(loaded), Is.EqualTo(bytes)); Assert.That(loaded.CanUndo, Is.False);
        }
        static void UndoRedo(PaintDocument d, byte[] before)
        {
            var after = DocumentBinary.Write(d); Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
            d.Redo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(after)); RoundTrip(d);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)]
        public void PolygonMaterialMatchesEachChannelWithUnionSelectionAndLocks(bool keepAlpha, bool erase)
        {
            var d = Doc(out var l, true); d.SetSelection(SelectionMask.Ellipse(d, 16, 16, 12.25, 10.5).Feather(2));
            if (keepAlpha) d.SetLayerLocks(l.Id, LayerLocks.Transparency);
            d.ClearHistory(); var before = DocumentBinary.Write(d);
            var fill = d.BeginMaterialTriangleFill(l.Id, Material, .73, erase);
            fill.Add(new[] { Triangles[2], Triangles[0] }); fill.Add(new[] { Triangles[1], Triangles[2] }); fill.Stroke.Commit();
            foreach (var m in Material)
            {
                var one = DocumentBinary.Read(before); one.RestoreSelection(d.Selection);
                one.Fill(l.Id, m.Channel, m.Value, .73, SelectionMask.FromTriangles(one, Triangles), erase);
                Assert.That(Pixels(l.GetChannel(m.Channel)), Is.EqualTo(Pixels(one.GetLayer(l.Id).GetChannel(m.Channel))), m.Channel.ToString());
            }
            UndoRedo(d, before);
        }

        [TestCase("cancel")] [TestCase("invalid")] [TestCase("stroke-budget")] [TestCase("source-budget")]
        public void PolygonRollbackRestoresPixelsAndEnabling(string how)
        {
            var d = Doc(out var l); var before = DocumentBinary.Write(d);
            if (how == "stroke-budget") d.ActiveStrokeBudgetBytes = 16 * 64 * 3;
            if (how == "source-budget") d.SourceBudgetBytes = 16 * 4 * 3;
            var fill = d.BeginMaterialTriangleFill(l.Id, Material);
            if (how.EndsWith("budget")) Assert.That(() => fill.Add(Whole), Throws.InvalidOperationException);
            else
            {
                fill.Add(Whole);
                if (how == "invalid") Assert.That(() => fill.Add(new[] { (double.NaN, 0d, 1d, 0d, 0d, 1d) }), Throws.InstanceOf<ArgumentException>());
                else fill.Stroke.Cancel();
            }
            Assert.That(d.HasActiveStroke, Is.False); Assert.That(d.CanUndo, Is.False);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
        }

        [Test] public void PolygonNoChangeAndRedoRefusalLeaveNoEnabledChannels()
        {
            var d = Doc(out var l); var before = DocumentBinary.Write(d);
            var fill = d.BeginMaterialTriangleFill(l.Id, Material); Assert.That(fill.Stroke.Commit(), Is.False);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
            fill = d.BeginMaterialTriangleFill(l.Id, Material); fill.Add(Whole); fill.Stroke.Commit(); d.Undo();
            d.SourceBudgetBytes = 0; Assert.That(() => d.Redo(), Throws.InvalidOperationException);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.RedoCount, Is.EqualTo(1));
        }

        [TestCase(LayerKind.Fill)] [TestCase(LayerKind.Adjustment)] [TestCase(LayerKind.Group)]
        public void PolygonRejectsNonPaintLayers(LayerKind kind)
        {
            var d = Doc(out _); var l = kind == LayerKind.Fill ? d.AddFillLayer("fill") : kind == LayerKind.Group ? d.AddGroup("group") : d.AddAdjustmentLayer("adjust", AdjustmentSettings.Invert());
            var before = DocumentBinary.Write(d);
            Assert.That(() => d.BeginMaterialTriangleFill(l.Id, Material), Throws.InvalidOperationException);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
        }

        static PathBrush Brush(bool surface) => new PathBrush { RadiusWorld = surface ? .045 : 2.5, Hardness = .67, Opacity = .76, Flow = .55, Spacing = .2, PressureFlow = true };
        static SurfaceGeometry Plane() => new SurfaceGeometry(new[] {
            new SurfaceTriangle(Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector2.zero, Vector2.right, Vector2.one),
            new SurfaceTriangle(Vector3.zero, new Vector3(1, 1, 0), Vector3.up, Vector2.zero, Vector2.one, Vector2.up) });
        static EditablePath Path(bool surface)
        {
            if (surface) return new SurfacePath(Guid.NewGuid(), PaintChannel.Color, SurfacePathRenderer.Fingerprint(Plane()), Brush(true),
                new[] { new PathPoint(0, .2, .2, .75), new PathPoint(1, .3, .4, .5) }, Material);
            return new CanvasPath(Guid.NewGuid(), PaintChannel.Color, Brush(false),
                new[] { new CanvasPoint(5.25, 6.5, .75), new CanvasPoint(20.5, 25.25, .5), new CanvasPoint(28.25, 12.5, .8) }, Material);
        }
        static IReadOnlyDictionary<PaintChannel, SparseTileSurface> Render(PaintDocument d, EditablePath path)
            => path is CanvasPath c ? CanvasPathRenderer.RenderChannels(d, c) : SurfacePathRenderer.Render(d, Plane(), (SurfacePath)path).Channels;

        [TestCase(false)] [TestCase(true)]
        public void MaterialPathMatchesSingleChannelsAndKeepsSavedValuesWhenEdited(bool surface)
        {
            var d = Doc(out var l); d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 1, 1)); d.ClearHistory();
            var before = DocumentBinary.Write(d); var path = Path(surface); var rendered = Render(d, path);
            d.SetPath(l.Id, path, rendered);
            foreach (var m in Material)
            {
                var brush = path.Brush.Clone(); brush.Color = m.Value;
                EditablePath one = path is CanvasPath c ? (EditablePath)new CanvasPath(c.Id, m.Channel, brush, c.Points)
                    : new SurfacePath(path.Id, m.Channel, ((SurfacePath)path).ModelFingerprint, brush, ((SurfacePath)path).Points);
                Assert.That(Pixels(l.GetChannel(m.Channel)), Is.EqualTo(Pixels(Render(d, one)[m.Channel])), m.Channel.ToString());
            }
            UndoRedo(d, before);
            var loaded = DocumentBinary.Read(DocumentBinary.Write(d)); var p = loaded.Layers[0].Path;
            Assert.That(p.Material, Is.EqualTo(Material));
            EditablePath moved = p is CanvasPath cp ? (EditablePath)cp.WithPoints(cp.Points.Reverse()) : ((SurfacePath)p).WithPoints(((SurfacePath)p).Points.Reverse());
            Assert.That(moved.Material, Is.EqualTo(Material));
            d.ClearHistory(); before = DocumentBinary.Write(d); d.SetPath(l.Id, moved, Render(d, moved)); UndoRedo(d, before);
            d.Rasterize(l.Id); Assert.That(l.Path, Is.Null); d.Undo(); Assert.That(l.Path.Material, Is.EqualTo(Material));
        }

        [TestCase(false)] [TestCase(true)]
        public void PathSetAndRedoBudgetsAreAtomic(bool surface)
        {
            var d = Doc(out var l); var p = Path(surface); var render = Render(d, p); var before = DocumentBinary.Write(d);
            d.ActiveStrokeBudgetBytes = 64; Assert.That(() => d.SetPath(l.Id, p, render), Throws.InvalidOperationException);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.CanUndo, Is.False);
            d.ActiveStrokeBudgetBytes = long.MaxValue; d.SetPath(l.Id, p, render); d.Undo(); d.SourceBudgetBytes = 0;
            Assert.That(() => d.Redo(), Throws.InvalidOperationException); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
        }

        [TestCase(false)] [TestCase(true)]
        public void PathUndoBudgetRefusalKeepsEveryChannelAndPath(bool surface)
        {
            var d = Doc(out var l, true); var p = Path(surface);
            EditablePath empty = p is CanvasPath c ? (EditablePath)c.WithPoints(new CanvasPoint[0]) : ((SurfacePath)p).WithPoints(new PathPoint[0]);
            d.SetPath(l.Id, empty, Render(d, empty)); var cleared = DocumentBinary.Write(d); d.SourceBudgetBytes = 0;
            Assert.That(() => d.Undo(), Throws.InvalidOperationException); Assert.That(DocumentBinary.Write(d), Is.EqualTo(cleared));
            Assert.That(d.UndoCount, Is.EqualTo(1)); Assert.That(l.Path, Is.SameAs(empty));
        }

        [Test] public void NewPathLayerCreationAndRedoAreAtomicAndZeroHistoryBudgetWorks()
        {
            var d = Doc(out _); var p = Path(false); var render = Render(d, p); var before = DocumentBinary.Write(d);
            d.ActiveStrokeBudgetBytes = 64;
            Assert.That(() => d.AddPathLayer("path", p, render), Throws.InvalidOperationException);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.CanUndo, Is.False);
            d.ActiveStrokeBudgetBytes = long.MaxValue; d.AddPathLayer("path", p, render); UndoRedo(d, before);
            d.Undo(); d.SourceBudgetBytes = 0; Assert.That(() => d.Redo(), Throws.InvalidOperationException);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
            var noHistory = new PaintDocument(32, 32, 8, 0);
            Assert.That(() => noHistory.AddPathLayer("path", p, Render(noHistory, p)), Throws.Nothing);
            Assert.That(noHistory.CanUndo, Is.False);
        }

        [TestCase(LayerLocks.Pixels)] [TestCase(LayerLocks.Transparency)] [TestCase(LayerLocks.All)]
        public void PathsRejectLocksBeforeChangingAnyChannel(LayerLocks locks)
        {
            var d = Doc(out var l); d.SetLayerLocks(l.Id, locks); d.ClearHistory(); var before = DocumentBinary.Write(d);
            Assert.That(() => d.SetCanvasPath(l.Id, (CanvasPath)Path(false)), Throws.TypeOf<LayerLockedException>());
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.CanUndo, Is.False);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void PathRenderingEnforcesTheWholeMaterialBudget(bool surface, bool source)
        {
            var d = Doc(out _); var p = Path(surface); var render = Render(d, p); var before = DocumentBinary.Write(d);
            if (source) d.SourceBudgetBytes = render.Values.Sum(s => s.AllocatedBytes) / 2;
            else d.ActiveStrokeBudgetBytes = 64;
            Assert.That(() => Render(d, p), Throws.InvalidOperationException); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
        }

        [TestCase(LayerLocks.Pixels, false)] [TestCase(LayerLocks.All, false)] [TestCase(LayerLocks.Transparency, true)]
        public void PolygonRejectsLocksBeforeEnablingAnyChannel(LayerLocks locks, bool erase)
        {
            var d = Doc(out var l); d.SetLayerLocks(l.Id, locks); d.ClearHistory(); var before = DocumentBinary.Write(d);
            Assert.That(() => d.BeginMaterialTriangleFill(l.Id, Material, erase:erase), Throws.TypeOf<LayerLockedException>());
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.CanUndo, Is.False);
        }

        [Test] public void ChangingPathMaterialClearsRemovedOutputsAndPreservesOtherPixels()
        {
            var d = Doc(out var l); var path = (CanvasPath)Path(false); d.SetCanvasPath(l.Id, path);
            var before = DocumentBinary.Write(d); d.ClearHistory();
            var changed = path.WithMaterial(new[] { Material[1], Material[2] }); d.SetCanvasPath(l.Id, changed);
            Assert.That(l.GetChannel(PaintChannel.Color).TileCount, Is.Zero); Assert.That(l.GetChannel(PaintChannel.Emission).TileCount, Is.Zero);
            UndoRedo(d, before);
        }

        [Test] public void PathMaterialSurvivesDuplicationAndCanvasResize()
        {
            var d = Doc(out var l); var path = (CanvasPath)Path(false); d.SetCanvasPath(l.Id, path);
            var copy = d.DuplicateLayer(l.Id); Assert.That(copy.Path.Material, Is.EqualTo(Material)); Assert.That(copy.Path.Id, Is.Not.EqualTo(path.Id));
            var resized = d.Resampled(64, 64, CanvasResampling.Bilinear).Document;
            foreach (var layer in resized.Layers)
            {
                Assert.That(layer.Path.Material, Is.EqualTo(Material));
                var render = CanvasPathRenderer.RenderChannels(resized, (CanvasPath)layer.Path);
                foreach (var m in Material) Assert.That(Pixels(layer.GetChannel(m.Channel)), Is.EqualTo(Pixels(render[m.Channel])), m.Channel.ToString());
            }
            RoundTrip(resized);
        }

        [Test] public void MaterialPathKeepsDisabledChannelsThroughSmartPlacementAndRedraw()
        {
            var source = Doc(out var layer); source.SetCanvasPath(layer.Id, (CanvasPath)Path(false));
            var smart = source.CaptureSmartMaterial(new[] { layer.Id }, "path material");
            smart = SmartMaterialFile.Read(SmartMaterialFile.Write(smart, new YlpWriterInfo("YoluPainter", "0.2.0-test", "2022.3.22f1")));
            var target = new PaintDocument(64, 64, 8);
            var placed = target.PlaceSmartMaterial(smart, new Yozolab.YoluPainter.Core.Shelf.SmartPlacement { Channels = new[] { PaintChannel.Roughness } });
            var copy = target.GetLayer(placed.LayerId);
            Assert.That(copy.Path.Material, Is.EqualTo(Material)); RoundTrip(target);
            foreach (var m in Material) Assert.That(copy.IsChannelEnabled(m.Channel), Is.EqualTo(m.Channel == PaintChannel.Roughness));
            var p = (CanvasPath)copy.Path; target.SetCanvasPath(copy.Id, p.WithPoints(p.Points.Reverse()));
            foreach (var m in Material) Assert.That(copy.IsChannelEnabled(m.Channel), Is.EqualTo(m.Channel == PaintChannel.Roughness));
            RoundTrip(target);
            var single = Doc(out var l); single.SetCanvasPath(l.Id, ((CanvasPath)Path(false)).WithMaterial(null));
            Assert.That(() => single.SetChannelEnabled(l.Id, PaintChannel.Color, false), Throws.InvalidOperationException);
        }

        [Test] public void InvalidPathMaterialsAndRenderedSetsAreRejected()
        {
            var d = Doc(out var l); var p = (CanvasPath)Path(false); var before = DocumentBinary.Write(d);
            Assert.That(() => p.WithMaterial(new ChannelPaint[0]), Throws.ArgumentException);
            Assert.That(() => p.WithMaterial(new[] { Material[0], Material[0] }), Throws.ArgumentException);
            Assert.That(() => p.WithMaterial(new[] { new ChannelPaint((PaintChannel)999, new Rgba32()) }), Throws.InstanceOf<ArgumentException>());
            Assert.That(() => d.SetPath(l.Id, p, new Dictionary<PaintChannel, SparseTileSurface>()), Throws.ArgumentException);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
            d.SetCanvasPath(l.Id, p); var bytes = DocumentBinary.Write(d);
            int tail = bytes.Length - (1 + Material.Length * 8); bytes[tail] = 7;
            Assert.That(() => DocumentBinary.Read(bytes), Throws.TypeOf<InvalidDataException>());
            bytes = DocumentBinary.Write(d); BitConverter.GetBytes(999).CopyTo(bytes, tail + 1);
            Assert.That(() => DocumentBinary.Read(bytes), Throws.TypeOf<InvalidDataException>());
        }

        [TestCase(GradientShape.Linear, false, false)] [TestCase(GradientShape.Radial, false, false)]
        [TestCase(GradientShape.Linear, true, false)] [TestCase(GradientShape.Radial, false, true)]
        public void TwoMaterialGradientMatchesEverySingleChannel(GradientShape shape, bool keepAlpha, bool erase)
        {
            var d = Doc(out var l, true); d.SetSelection(SelectionMask.Ellipse(d, 16, 16, 11, 13).Feather(2));
            if (keepAlpha) d.SetLayerLocks(l.Id, LayerLocks.Transparency);
            d.ClearHistory(); var before = DocumentBinary.Write(d);
            var ends = Material.Select(m => new ChannelPaint(m.Channel, new Rgba32((byte)(255-m.Value.R), (byte)(255-m.Value.G), (byte)(255-m.Value.B), (byte)(255-m.Value.A)))).Reverse().ToArray();
            var g = new GradientSettings { Shape = shape, X0 = 3.25, Y0 = 5.75, X1 = 27.5, Y1 = 25.25, Opacity = .73 };
            d.GradientMaterial(l.Id, Material, ends, g);
            // 消去も同じ端点のアルファで組を削る。
            if (erase) { d.Undo(); d.ClearHistory(); d.GradientMaterial(l.Id, Material, ends, g, erase:true); }
            foreach (var m in Material)
            {
                var one = DocumentBinary.Read(before); one.RestoreSelection(d.Selection);
                g.From = m.Value; g.To = ends.Single(e => e.Channel == m.Channel).Value;
                one.Gradient(l.Id, m.Channel, g, null, erase);
                Assert.That(Pixels(l.GetChannel(m.Channel)), Is.EqualTo(Pixels(one.GetLayer(l.Id).GetChannel(m.Channel))), m.Channel.ToString());
            }
            UndoRedo(d, before);
        }

        [Test] public void TwoMaterialGradientRejectsMismatchedTypesAndCombinedBudget()
        {
            var d = Doc(out var l); var before = DocumentBinary.Write(d); var g = new GradientSettings { X1=32, Y1=16 };
            foreach (var ends in new[] { Material.Take(5).ToArray(), new[] { Material[0], Material[0], Material[2], Material[3], Material[4], Material[5] },
                Material.Select(m => m.Channel == PaintChannel.Height ? new ChannelPaint((PaintChannel)999, m.Value) : m).ToArray() })
                Assert.That(() => d.GradientMaterial(l.Id, Material, ends, g), Throws.InstanceOf<ArgumentException>());
            d.ActiveStrokeBudgetBytes = 64; Assert.That(() => d.GradientMaterial(l.Id, Material, Material, g), Throws.InvalidOperationException);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.CanUndo, Is.False);
        }
    }
}
