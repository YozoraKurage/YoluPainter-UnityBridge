using System;
using System.Collections.Generic;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class PaintDocumentSnapshotTests
    {
        static PaintDocument Document()
        {
            var d = new PaintDocument(32, 32, 16); var layer = d.AddLayer("Paint");
            layer.GetChannel(PaintChannel.Color).SetPixel(1, 2, new Rgba32(20, 30, 40, 255)); return d;
        }
        [Test] public void SnapshotPreservesNativeAttributesAndSharesOnlyFrozenTileBuffers()
        {
            var d = Document(); var l = d.Layers[0];
            d.SetNormalSettings(NormalSettings.Default.WithStrength(7));
            d.SetChannelOpacity(l.Id, PaintChannel.Color, .4);
            d.SetLayerOpacity(l.Id, .7); d.SetLayerClipping(l.Id, true);
            var mask = d.AddLayerMask(l.Id); mask.Surface.SetPixel(1, 2, new Rgba32(0, 0, 0, 200));
            d.SetLayerMaskDensity(l.Id, .3);
            d.AddFilter(l.Id, FilterTarget.Content, FilterSettings.Noise(.2, 123, true));
            d.AddFilter(l.Id, FilterTarget.Mask, FilterSettings.Invert());
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 9, 10));
            var resources = new ProjectResources(); d.ImageResources = resources;
            var image = resources.Add("Image", ImageContent.FromPixels(new byte[] { 1, 2, 3, 255 }, 1, 1), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            var fill = d.AddFillLayer("Fill"); d.SetFillImage(fill.Id, PaintChannel.Color, image.Id);
            d.AddAdjustmentLayer("Adjustment", AdjustmentSettings.Invert());
            var path = new CanvasPath(Guid.NewGuid(), PaintChannel.Color, new PathBrush(), new[] { new CanvasPoint(3, 4) });
            d.SetPath(d.AddLayer("Path").Id, path, new SparseTileSurface(32, 32, 16));
            byte[] expected = DocumentBinary.Write(d), selected = SelectionBinary.Write(d.Selection);
            var copy = d.CaptureSnapshot();
            object Buffer(SparseTileSurface surface)
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var tiles = (System.Collections.IDictionary)typeof(SparseTileSurface).GetField("tiles", flags).GetValue(surface);
                var tile = tiles[new TileCoord(0, 0)]; return tile.GetType().GetField("data", flags).GetValue(tile);
            }
            Assert.That(Buffer(copy.Layers[0].GetChannel(PaintChannel.Color)), Is.SameAs(Buffer(l.GetChannel(PaintChannel.Color))), "capture shares the nonuniform pixel buffer");
            l.GetChannel(PaintChannel.Color).SetPixel(1, 2, new Rgba32(90, 80, 70, 255));
            mask.Surface.Clear(); d.SetLayerName(l.Id, "Edited"); d.RemoveLayer(fill.Id); d.ClearSelection();
            path.Brush.RadiusWorld = 13;
            Assert.That(DocumentBinary.Write(copy), Is.EqualTo(expected));
            Assert.That(SelectionBinary.Write(copy.Selection), Is.EqualTo(selected));
            Assert.That(copy.CanUndo, Is.False);
        }
        [Test] public void SnapshotDuringAnActiveStrokeIsRefusedWithoutChangingTheSource()
        {
            var d = Document(); byte[] expected = DocumentBinary.Write(d);
            using (var stroke = d.BeginStroke(d.Layers[0].Id, PaintChannel.Color, new BrushSettings()))
                Assert.That(() => d.CaptureSnapshot(), Throws.InvalidOperationException);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(expected));
        }
        [TestCase(false)] [TestCase(true)]
        public void SnapshotPreservesPathMaterialDecalsAndManualIdColors(bool surface)
        {
            var d = Document(); var material = new[] {
                new ChannelPaint(PaintChannel.Color, new Rgba32(210, 30, 75, 215)),
                new ChannelPaint(PaintChannel.Roughness, new Rgba32(65, 65, 65, 180)) };
            EditablePath path = surface
                ? (EditablePath)new SurfacePath(Guid.NewGuid(), PaintChannel.Color, "0123456789abcdef", new PathBrush(), new[] { new PathPoint(0, .2, .3) }, material)
                : new CanvasPath(Guid.NewGuid(), PaintChannel.Color, new PathBrush(), new[] { new CanvasPoint(3, 4) }, material);
            var layer = d.AddLayer("Path");
            d.SetPath(layer.Id, path, new Dictionary<PaintChannel, SparseTileSurface> {
                [PaintChannel.Color] = new SparseTileSurface(32, 32, 16),
                [PaintChannel.Roughness] = new SparseTileSurface(32, 32, 16) });
            var decal = d.AddFillLayer("Decal");
            d.SetFillProjection(decal.Id, FillProjection.DecalAt(FillProjection.DefaultPlacement).WithCulling(.25, 75, .5));
            d.SetIdColors(new IdColorAssignments(new string('a', 64), new Dictionary<int, int> { [0] = 0x123456, [1] = 0x654321 }));
            var expected = DocumentBinary.Write(d); var copy = d.CaptureSnapshot();
            d.SetIdColors(IdColorAssignments.Empty); d.SetFillProjection(decal.Id, FillProjection.Default);
            path.Brush.Flow = .1; d.RemoveLayer(layer.Id);
            var bytes = DocumentBinary.Write(copy);
            Assert.That(bytes, Is.EqualTo(expected));
            var restored = DocumentBinary.Read(bytes);
            Assert.That(restored.GetLayer(layer.Id).Path.Material, Is.EqualTo(material));
            Assert.That(restored.GetLayer(layer.Id).Path.Id, Is.EqualTo(path.Id));
            Assert.That(restored.GetLayer(decal.Id).Projection.IsDecal, Is.True);
            Assert.That(restored.IdColors.Colors[0], Is.EqualTo(0x123456));
            Assert.That(restored.IdColors.Colors[1], Is.EqualTo(0x654321));
            Assert.That(copy.Revision, Is.LessThan(d.Revision));
        }
    }
}
