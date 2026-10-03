using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    public sealed partial class WindowTests
    {
        void DirectGradientOnDemo()
        {
            var gen = ShapeGradientOnDemoCube(); var d = window.Document;
            d.RemoveFilter(window.SelectedLayer, gen.Id); window.EditMask = false;
            window.EnableFillGradient(true); Assert.That(window.Document.GetLayer(window.SelectedLayer).HasFillGradient(window.Channel), Is.True, window.StatusMessage);
            Assert.That(window.FillGradientEditLayer, Is.EqualTo(window.SelectedLayer)); Repaint(window);
        }
        [Test] public void DirectFillGradientGizmoUsesOneUndoAndEscapeFocusLossAndReloadCancel()
        {
            DirectGradientOnDemo(); var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            var projection = FillProjection.DecalAt(layer.FillGradients[PaintChannel.Color].Volume.WithShape(GeneratorShape.Box).WithFalloff(0));
            d.SetFillProjection(layer.Id, projection); Repaint(window);
            Assert.That(window.ProjectionEditLayer, Is.EqualTo(Guid.Empty), "勾配の編集が投影の自動表示より優先");
            var start = layer.FillGradients[PaintChannel.Color]; int count = d.UndoCount;
            var center = Handle(ShapeHandle.MoveFree); DragHandle(center, center + new Vector2(18, -22));
            Assert.That(layer.FillGradients[PaintChannel.Color].Volume, Is.Not.EqualTo(start.Volume)); Assert.That(layer.Projection, Is.EqualTo(projection), "動かすのは勾配の形だけ"); Assert.That(d.UndoCount, Is.EqualTo(count + 1));
            Key(window, KeyCode.Z, EventModifiers.Control); Assert.That(layer.FillGradients[PaintChannel.Color], Is.EqualTo(start));
            foreach (string cancel in new[] { "Escape", "OnLostFocus", "BeforeReload" })
            {
                center = Handle(ShapeHandle.MoveFree); DragHandle(center, center + new Vector2(-15, 12), release: false);
                if (cancel == "Escape") Key(window, KeyCode.Escape); else Invoke(window, cancel);
                Assert.That(layer.FillGradients[PaintChannel.Color], Is.EqualTo(start), cancel); Assert.That(d.UndoCount, Is.EqualTo(count)); Assert.That(window.ShapeDragging, Is.False);
                Mouse(window, EventType.MouseUp, center);
            }
            window.ProjectionHandlesHidden = false; Repaint(window);
            Assert.That(window.FillGradientEditLayer, Is.EqualTo(Guid.Empty)); Assert.That(window.ProjectionEditLayer, Is.EqualTo(layer.Id));
            window.FillGradientEditLayer = layer.Id; Repaint(window);
            Assert.That(window.ProjectionEditLayer, Is.EqualTo(Guid.Empty)); Assert.That(d.UndoCount, Is.EqualTo(count));
        }
        [Test] public void DirectFillGradientAndStopsReopenFromYlpAndScalarChannelsStayIndependent()
        {
            DirectGradientOnDemo(); var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            var ramp = GradientRamp.Preset(GradientPreset.WarmCool, new Rgba32(0, 0, 0, 255), new Rgba32(255, 255, 255, 255));
            d.SetFillGradient(layer.Id, PaintChannel.Color, layer.FillGradients[PaintChannel.Color].WithRamp(ramp));
            window.Channel = PaintChannel.Roughness; window.EnableFillGradient(true);
            Assert.That(layer.FillGradients[PaintChannel.Color].Ramp, Is.EqualTo(ramp)); Assert.That(layer.FillGradients[PaintChannel.Roughness].Ramp, Is.EqualTo(GradientRamp.Default));
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath(); window.SaveProject(true); Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File; other.OpenProject(); var restored = other.Document.GetLayer(layer.Id);
                Assert.That(restored.FillGradients[PaintChannel.Color].Ramp, Is.EqualTo(ramp)); Assert.That(restored.FillGradients.Count, Is.EqualTo(2));
                Assert.That(other.Document.GetFillGradientStatus(layer.Id, PaintChannel.Color).Active, Is.False);
                other.Preview.LoadDemoMesh(); other.PollGeneratorInputs();
                Assert.That(other.Document.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            }
            finally { Close(other); }
        }
        [Test] public void GradientStopAndValueCurveDragsCommitOnReleaseAndCancelWithoutHistory()
        {
            DirectGradientOnDemo(); OpenLayerPanels(); var d = window.Document; var id = window.SelectedLayer;
            var start = d.GetLayer(id).FillGradients[PaintChannel.Color].Ramp; int count = d.UndoCount;
            // 下の分岐点の列（部品の中央から +29 点）をクリックして追加し、動かして離す
            var at = LayerControlPoint("gradient.stops", .4f) + new Vector2(0, 29);
            HostMouse(EventType.MouseDown, at); HostMouse(EventType.MouseDrag, at + new Vector2(18, 0));
            Assert.That(d.GetLayer(id).FillGradients[PaintChannel.Color].Ramp, Is.EqualTo(start)); Assert.That(d.UndoCount, Is.EqualTo(count));
            Key(window, KeyCode.Escape); HostMouse(EventType.MouseUp, at); Repaint(window);
            Assert.That(d.GetLayer(id).FillGradients[PaintChannel.Color].Ramp, Is.EqualTo(start)); Assert.That(d.UndoCount, Is.EqualTo(count));
            at = LayerControlPoint("gradient.stops", .4f) + new Vector2(0, 29);
            HostMouse(EventType.MouseDown, at); HostMouse(EventType.MouseDrag, at + new Vector2(18, 0)); HostMouse(EventType.MouseUp, at + new Vector2(18, 0)); Repaint(window);
            Assert.That(d.GetLayer(id).FillGradients[PaintChannel.Color].Ramp.Colors.Count, Is.EqualTo(3)); Assert.That(d.UndoCount, Is.EqualTo(count + 1));
            Key(window, KeyCode.Z, EventModifiers.Control); Assert.That(d.GetLayer(id).FillGradients[PaintChannel.Color].Ramp, Is.EqualTo(start));
            var curve = LayerControlPoint("gradient.curve", .45f);
            HostMouse(EventType.MouseDown, curve); HostMouse(EventType.MouseDrag, curve + new Vector2(10, -18));
            Assert.That(d.GetLayer(id).FillGradients[PaintChannel.Color].Ramp, Is.EqualTo(start));
            Invoke(window, "OnLostFocus"); HostMouse(EventType.MouseUp, curve); Repaint(window);
            Assert.That(d.GetLayer(id).FillGradients[PaintChannel.Color].Ramp, Is.EqualTo(start)); Assert.That(d.UndoCount, Is.EqualTo(count));
            curve = LayerControlPoint("gradient.curve", .45f);
            HostMouse(EventType.MouseDown, curve); HostMouse(EventType.MouseDrag, curve + new Vector2(10, -18)); HostMouse(EventType.MouseUp, curve + new Vector2(10, -18)); Repaint(window);
            Assert.That(d.GetLayer(id).FillGradients[PaintChannel.Color].Ramp.Curve.Count, Is.GreaterThan(2)); Assert.That(d.UndoCount, Is.EqualTo(count + 1));
        }
        [Test] public void ShapeGizmoReadsUnitySceneIncrementSettingsWithoutChangingThem()
        {
            var move = EditorSnapSettings.move; float scale = EditorSnapSettings.scale, rotate = EditorSnapSettings.rotate;
            try
            {
                EditorSnapSettings.move = new Vector3(.2f, .3f, .4f); EditorSnapSettings.scale = .25f; EditorSnapSettings.rotate = 30;
                var snap = TexturePaintWindow.SceneShapeSnap;
                Assert.That(snap.Move, Is.EqualTo(EditorSnapSettings.move)); Assert.That(snap.Size, Is.EqualTo(.25f)); Assert.That(snap.Rotation, Is.EqualTo(30));
                DirectGradientOnDemo(); var layer = window.Document.GetLayer(window.SelectedLayer); var start = layer.FillGradients[PaintChannel.Color].Volume; var at = OnArrow(ShapeHandle.MoveX);
                Mouse(window, EventType.MouseDown, at);
                MouseWith(new EditorWindowHost { Window = window }, EventType.MouseDrag, at + new Vector2(70, -10), EventModifiers.Control);
                Mouse(window, EventType.MouseUp, at); Repaint(window);
                double delta = layer.FillGradients[PaintChannel.Color].Volume.CenterX - start.CenterX;
                Assert.That(Math.Abs(delta), Is.GreaterThan(.01)); Assert.That(delta / .2, Is.EqualTo(Math.Round(delta / .2)).Within(1e-4)); Assert.That(EditorSnapSettings.move, Is.EqualTo(new Vector3(.2f, .3f, .4f)));
            }
            finally { EditorSnapSettings.move = move; EditorSnapSettings.scale = scale; EditorSnapSettings.rotate = rotate; }
        }
    }
}
