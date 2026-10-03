using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>SendEvent で組のポリゴン塗り・2D/3D パス・二素材グラデーションを操作する。</summary>
    public sealed partial class WindowTests
    {
        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void MaterialPolygonFillUsesAllChannelsIn2DAnd3D(bool surface, bool erase)
        {
            var geometry = PolygonFillCube(); RegionMaterial(); var d = window.Document;
            var at = window.SurfaceRect.center;
            int triangle;
            if (surface) { Assert.That(window.Preview.TryPick(window.SurfaceRect, at, out var hit), Is.True); triangle = hit.TriangleIndex; }
            else { triangle = 0; var p = UvCentroidPixel(geometry, triangle); at = At(window, p.x, p.y); }
            var uv = UvCentroidPixel(geometry, triangle);
            var region = SurfaceRegions.Selection(d, geometry, SurfaceRegions.Region(geometry, triangle, window.SurfacePick));
            if (erase) d.FillMaterial(window.SelectedLayer, window.StrokeChannels(), region: region);
            d.SetSelection(SelectionMask.Ellipse(d, uv.x, uv.y, 45, 35).Feather(2)); d.ClearHistory();
            window.PolygonFillErase = erase;
            var expected = RegionClone(); var before = DocumentBinary.Write(d);
            expected.FillMaterial(window.SelectedLayer, window.StrokeChannels(), window.Brush.opacity, region, erase);
            Mouse(window, EventType.MouseDown, at); Assert.That(window.IsPolygonFilling, Is.True);
            Mouse(window, EventType.MouseUp, at);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(DocumentBinary.Write(expected)), window.StatusMessage);
            Assert.That(d.UndoCount, Is.EqualTo(1)); RegionUndoRedo(before, DocumentBinary.Write(d));
        }

        [TestCase("escape")] [TestCase("focus")] [TestCase("reload")] [TestCase("play")]
        public void MaterialPolygonCancellationRevertsEveryChannel(string how)
        {
            var g = PolygonFillCube(); RegionMaterial(); var d = window.Document; d.ClearHistory(); var before = DocumentBinary.Write(d);
            var p = UvCentroidPixel(g, 0); var at = At(window, p.x, p.y);
            Mouse(window, EventType.MouseDown, at); Assert.That(window.IsPolygonFilling, Is.True);
            foreach (var m in window.StrokeChannels()) Assert.That(d.GetLayer(window.SelectedLayer).GetPixel(m.Channel, p.x, p.y).A, Is.GreaterThan(0));
            if (how == "escape") Key(window, KeyCode.Escape);
            else if (how == "focus") Invoke(window, "OnLostFocus");
            else if (how == "reload") Invoke(window, "BeforeReload");
            else Invoke(window, "PlayModeChanged", PlayModeStateChange.ExitingEditMode);
            Mouse(window, EventType.MouseUp, at);
            Assert.That(d.HasActiveStroke, Is.False); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.CanUndo, Is.False);
        }

        [Test] public void MaterialPolygonMaskAndBudgetRefusalLeaveImageChannelsUntouched()
        {
            var g = PolygonFillCube(); RegionMaterial(); var d = window.Document; var l = d.GetLayer(window.SelectedLayer);
            var p = UvCentroidPixel(g, 0); var at = At(window, p.x, p.y);
            d.ActiveStrokeBudgetBytes = 64; d.ClearHistory(); var before = DocumentBinary.Write(d);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.CanUndo, Is.False);
            Assert.That(window.StatusMessage, Does.Contain("budget"));
            d.ActiveStrokeBudgetBytes = long.MaxValue; d.AddLayerMask(l.Id); window.EditMask = true;
            d.SetLayerLocks(l.Id, LayerLocks.Pixels | LayerLocks.Transparency); d.ClearHistory(); before = DocumentBinary.Write(d);
            window.PolygonFillErase = true;
            var expected = RegionClone(); expected.FillMask(l.Id, window.Brush.opacity, SurfaceRegions.Selection(expected, g, SurfaceRegions.Region(g, 0, window.SurfacePick)));
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(DocumentBinary.Write(expected)));
            RegionUndoRedo(before, DocumentBinary.Write(d));
        }

        [TestCase(false)] [TestCase(true)]
        public void MaterialPathEventsCreateAndMoveAllChannelsAndSaveTheirValues(bool surface)
        {
            RegionMaterial(); var d = window.Document;
            if (surface) { Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True); Repaint(window); }
            window.Tool = TexturePaintWindow.PaintTool.Path; d.ClearHistory(); var before = DocumentBinary.Write(d);
            var at = surface ? window.SurfaceRect.center : At(window, 160, 160);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            var l = d.GetLayer(window.SelectedLayer); Assert.That(l.Path, Is.Not.Null, window.StatusMessage);
            Assert.That(l.Path.Material, Is.EqualTo(window.StrokeChannels())); Assert.That(d.UndoCount, Is.EqualTo(1));
            RegionUndoRedo(before, DocumentBinary.Write(d));
            var savedMaterial = l.Path.Material.ToArray();
            var b = window.Brush; b.material = false; b.color = Color.green; window.Brush = b;
            before = DocumentBinary.Write(d); d.ClearHistory();
            var end = surface ? at + new Vector2(18, 0) : At(window, 220, 230);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseDrag, end); Mouse(window, EventType.MouseUp, end);
            Assert.That(l.Path.Material, Is.EqualTo(savedMaterial), "点の編集は現在のブラシで組を上書きしない");
            RegionUndoRedo(before, DocumentBinary.Write(d));
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath(); window.SaveProject(true);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File; other.OpenProject();
                Assert.That(DocumentBinary.Write(other.Document), Is.EqualTo(DocumentBinary.Write(d)), other.StatusMessage);
                Assert.That(other.Document.GetLayer(l.Id).Path.Material, Is.EqualTo(savedMaterial));
            }
            finally { Close(other); }
        }

        [TestCase(false)] [TestCase(true)]
        public void MaterialPathCreationRefusalDoesNotLeaveAnEmptyLayer(bool surface)
        {
            RegionMaterial(); var d = window.Document;
            if (surface) { Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True); Repaint(window); }
            window.Tool = TexturePaintWindow.PaintTool.Path; d.ActiveStrokeBudgetBytes = 64; d.ClearHistory();
            var before = DocumentBinary.Write(d); var selected = window.SelectedLayer;
            var at = surface ? window.SurfaceRect.center : At(window, 160, 160);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(window.SelectedLayer, Is.EqualTo(selected));
            Assert.That(d.CanUndo, Is.False); Assert.That(window.StatusMessage, Does.Contain("budget"));
        }

        [TestCase(false, "escape")] [TestCase(false, "focus")] [TestCase(false, "reload")] [TestCase(false, "play")]
        [TestCase(true, "escape")] [TestCase(true, "focus")] [TestCase(true, "reload")] [TestCase(true, "play")]
        public void MaterialPathDragCancellationKeepsSavedPointsAndEveryChannel(bool surface, string how)
        {
            RegionMaterial(); var d = window.Document;
            if (surface) { window.Preview.LoadDemoMesh(); Repaint(window); }
            window.Tool = TexturePaintWindow.PaintTool.Path;
            var at = surface ? window.SurfaceRect.center : At(window, 160, 160);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at); d.ClearHistory();
            var before = DocumentBinary.Write(d); var end = surface ? at + new Vector2(18, 0) : At(window, 220, 230);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseDrag, end);
            if (how == "escape") Key(window, KeyCode.Escape);
            else if (how == "focus") Invoke(window, "OnLostFocus");
            else if (how == "reload") Invoke(window, "BeforeReload");
            else Invoke(window, "PlayModeChanged", PlayModeStateChange.ExitingEditMode);
            Mouse(window, EventType.MouseUp, end);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.CanUndo, Is.False);
        }

        [Test] public void MaterialPathUseBrushChangesTheSavedSetAsOneUndo()
        {
            RegionMaterial(); window.Tool = TexturePaintWindow.PaintTool.Path;
            var at = At(window, 160, 160); Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            var d = window.Document; var l = d.GetLayer(window.SelectedLayer); d.ClearHistory(); var before = DocumentBinary.Write(d);
            window.Brush.materialChannels = (1 << (int)PaintChannel.Roughness) | (1 << (int)PaintChannel.Metallic);
            window.SetMaterialScalar(PaintChannel.Roughness, .95f);
            ClickToolControl("path.use-brush");
            Assert.That(l.Path.Material, Is.EqualTo(window.StrokeChannels())); Assert.That(l.GetChannel(PaintChannel.Color).TileCount, Is.Zero);
            RegionUndoRedo(before, DocumentBinary.Write(d));
        }

        [Test] public void MaterialGradientOptionsCaptureAnEndMaterialAndUseItForEveryChannel()
        {
            RegionMaterial(); window.Tool = TexturePaintWindow.PaintTool.Gradient;
            ClickToolControl("gradient.between-materials"); Assert.That(window.GradientBetweenMaterials, Is.True);
            ClickToolControl("gradient.capture-end"); var ends = window.GradientEndChannels().ToArray();
            window.Brush.color = new Color(.1f, .2f, .9f, .6f); window.SetMaterialScalar(PaintChannel.Roughness, .9f); window.SetMaterialNormal(-.4f, .3f);
            var d = window.Document; d.SetSelection(SelectionMask.Rectangle(d, 120, 160, 380, 250)); d.ClearHistory();
            var before = DocumentBinary.Write(d); var expected = RegionClone(); Drag(140, 200, 350, 200);
            var g = RegionDraggedGradient(Rgba32.Transparent, Rgba32.Transparent);
            expected.GradientMaterial(window.SelectedLayer, window.StrokeChannels(), ends, g);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(DocumentBinary.Write(expected)), window.StatusMessage);
            RegionUndoRedo(before, DocumentBinary.Write(d));
            window.Tool = TexturePaintWindow.PaintTool.PolygonFill; Repaint(window); Assert.That(window.ToolControlScreenRects.ContainsKey("material.toggle"), Is.True);
            window.Tool = TexturePaintWindow.PaintTool.Path; Repaint(window); Assert.That(window.ToolControlScreenRects.ContainsKey("material.toggle"), Is.True);
        }
    }
}
