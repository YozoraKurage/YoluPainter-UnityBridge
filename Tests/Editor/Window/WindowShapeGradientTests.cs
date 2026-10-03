using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウの形のグラデーション（GUI モード、SendEvent）: 足すと 3D ビューでの編集になり、ギズモの矢印・中央の四角・輪・
    /// 面の四角をドラッグすると形が動く・回る・大きさが変わり、どのドラッグも 1 回の Undo（キーボードで戻せる）。Esc・フォーカスの喪失は
    /// ドラッグの前に戻して履歴にも残さない。ストロークの最中はハンドルを掴まない（ハンドルの上を通るストロークは塗るだけ）。プロパティの欄の
    /// 数値の欄のドラッグでも同じ値が変わり、ギズモが付いて行く。.ylp に保存して別の窓で開くと形が戻る。</summary>
    public sealed partial class WindowTests
    {
        /// <summary>デモのキューブで Position を焼き、白い塗りつぶしの層のマスクに形のグラデーションを足して 3D ビューだけを見せる。</summary>
        FilterEffect ShapeGradientOnDemoCube()
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            QuickBake(window); window.MeshBakeSettings.Maps = new[] { MeshMapKind.Position };
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), window.StatusMessage);
            WornFill();
            var gen = window.AddGenerator(FilterTarget.Mask, GeneratorType.ShapeGradient);
            Assert.That(gen, Is.Not.Null, window.StatusMessage);
            Assert.That(window.ShapeEditFilter, Is.EqualTo(gen.Id), "adding one starts editing it in the 3D view");
            window.View = TexturePaintWindow.ViewMode.Model; Repaint(window);
            window.Preview.ViewFrom(30, 20); Repaint(window);
            Assert.That(window.PollGeneratorInputs() || window.Document.GetGeneratorStatus(window.SelectedLayer, gen.Id).Active, Is.True);
            Assert.That(window.Document.GetGeneratorStatus(window.SelectedLayer, gen.Id).Active, Is.True, window.Document.GetGeneratorStatus(window.SelectedLayer, gen.Id).Reason);
            return gen;
        }
        ShapeVolume VolumeOf(Guid id) => window.Document.FindFilter(window.SelectedLayer, id, out _).Settings.Generator.Volume;
        Vector2 Handle(ShapeHandle h) { Repaint(window); var at = window.ShapeHandleGui(h); Assert.That(at.HasValue, Is.True, h + " is not shown"); return at.Value; }
        /// <summary>矢印の上で、面の四角に重ならない点（先から中心へ探す。四角は矢印の上にあっても先に当たる）。</summary>
        Vector2 OnArrow(ShapeHandle arrow)
        {
            var tip = Handle(arrow); var center = Handle(ShapeHandle.MoveFree);
            for (float t = 1; t > .2f; t -= .05f) { var p = Vector2.Lerp(center, tip, t); if (window.ShapeHandleAt(p) == arrow) return p; }
            Assert.Fail(arrow + " has no free spot"); return tip;
        }
        void DragHandle(Vector2 from, Vector2 to, bool release = true)
        {
            Mouse(window, EventType.MouseDown, from);
            Assert.That(window.ShapeDragging, Is.True, "the press grabbed a handle");
            Mouse(window, EventType.MouseDrag, Vector2.Lerp(from, to, .5f)); Mouse(window, EventType.MouseDrag, to);
            if (release) Mouse(window, EventType.MouseUp, to);
            Repaint(window);
        }

        [Test] public void GizmoDragsMoveRotateAndSizeTheShapeEachAsOneUndoStep()
        {
            var gen = ShapeGradientOnDemoCube(); var d = window.Document;
            var start = VolumeOf(gen.Id); var before = d.Composite(PaintChannel.Color); int steps = d.UndoCount;
            Assert.That(window.Preview.ShownShapeGradient.HasValue, Is.True, "the value is overlaid on the model");
            // 上へ: Y の矢印を上にドラッグすると中心の Y だけが増える
            var tip = OnArrow(ShapeHandle.MoveY);
            DragHandle(tip, tip + new Vector2(0, -40));
            var moved = VolumeOf(gen.Id);
            Assert.That(moved.CenterY, Is.GreaterThan(start.CenterY + .05), window.StatusMessage);
            Assert.That(moved.CenterX, Is.EqualTo(start.CenterX).Within(1e-4)); Assert.That(moved.CenterZ, Is.EqualTo(start.CenterZ).Within(1e-4));
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "one drag, one step");
            Assert.That(d.Composite(PaintChannel.Color), Is.Not.EqualTo(before), "redrawn like a slider");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(VolumeOf(gen.Id), Is.EqualTo(start)); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before));
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(VolumeOf(gen.Id), Is.EqualTo(moved));
            // 中央の四角は画面に平行に動かす
            var center = Handle(ShapeHandle.MoveFree);
            DragHandle(center, center + new Vector2(30, 0));
            Assert.That(VolumeOf(gen.Id).CenterX, Is.Not.EqualTo(moved.CenterX).Within(1e-4));
            Assert.That(d.UndoCount, Is.EqualTo(steps + 2));
            // 面の四角: +X の面だけが動き（中心が半分付いて行く）、Shift では両側
            var sized0 = VolumeOf(gen.Id);
            var knob = Handle(ShapeHandle.SizeXPos); var c = Handle(ShapeHandle.MoveFree);
            DragHandle(knob, knob + (knob - c).normalized * 25);
            var sized = VolumeOf(gen.Id);
            Assert.That(sized.SizeX, Is.GreaterThan(sized0.SizeX + .01));
            Assert.That(sized.CenterX - sized0.CenterX, Is.EqualTo((sized.SizeX - sized0.SizeX) / 2).Within(1e-3), "the opposite face stays");
            Assert.That(sized.SizeY, Is.EqualTo(sized0.SizeY)); Assert.That(d.UndoCount, Is.EqualTo(steps + 3));
            knob = Handle(ShapeHandle.SizeXPos); c = Handle(ShapeHandle.MoveFree);
            Mouse(window, EventType.MouseDown, knob);
            window.SendEvent(new Event { type = EventType.MouseDrag, mousePosition = knob + (knob - c).normalized * 20 + window.rootVisualElement.worldBound.position, modifiers = EventModifiers.Shift });
            Mouse(window, EventType.MouseUp, knob + (knob - c).normalized * 20);
            var both = VolumeOf(gen.Id);
            Assert.That(both.SizeX, Is.GreaterThan(sized.SizeX + .01)); Assert.That(both.CenterX, Is.EqualTo(sized.CenterX).Within(1e-4), "Shift: both faces, the centre stays");
            // 輪: Y の輪を回すと Y の回転が変わる
            window.ShapeGizmoMode = ShapeGizmoMode.Rotate; Repaint(window);
            Assert.That(window.ShapeHandleGui(ShapeHandle.MoveY), Is.Null, "no move arrows in the rotate mode");
            Handle(ShapeHandle.RotateY);
            var view = window.Preview.GizmoView(window.SurfaceRect); var wc = ShapeGizmo.WorldCenter(both, window.Preview.ModelRootPosition, window.Preview.ModelRootRotation);
            Assert.That(window.Preview.TryWorldToGui(window.SurfaceRect, wc, out var rc), Is.True);
            // 輪の上で、面の四角に重ならない点を掴む
            var ring = ShapeGizmo.Ring(view, wc, Vector3.up, ShapeGizmo.RingPoints * ShapeGizmo.WorldPerPoint(view, wc), 64).First(p => window.ShapeHandleAt(p) == ShapeHandle.RotateY);
            var tangent = new Vector2(-(ring - rc).y, (ring - rc).x).normalized;
            DragHandle(ring, ring + tangent * 30);
            var turned = VolumeOf(gen.Id);
            Assert.That(Math.Abs(turned.RotationY), Is.GreaterThan(5), turned.ToString());
            Assert.That(d.UndoCount, Is.EqualTo(steps + 5));
            for (int i = 0; i < 5; i++) Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(VolumeOf(gen.Id), Is.EqualTo(start)); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before), "every drag undoes back to the start");
        }

        [Test] public void EscapeFocusLossAndStrokesLeaveNoHalfDoneDrag()
        {
            var gen = ShapeGradientOnDemoCube(); var d = window.Document;
            var start = VolumeOf(gen.Id); var before = d.Composite(PaintChannel.Color); int steps = d.UndoCount, redo = d.RedoCount;
            var tip = OnArrow(ShapeHandle.MoveX);
            DragHandle(tip, tip + new Vector2(50, 0), release: false);
            Assert.That(VolumeOf(gen.Id), Is.Not.EqualTo(start), "moved while dragging");
            Key(window, KeyCode.Z, EventModifiers.Control); // ドラッグ中の Undo のキーは使わない
            Assert.That(window.ShapeDragging, Is.True);
            Key(window, KeyCode.Escape);
            Assert.That(window.ShapeDragging, Is.False);
            Assert.That(VolumeOf(gen.Id), Is.EqualTo(start)); Assert.That((d.UndoCount, d.RedoCount), Is.EqualTo((steps, redo)), "nothing left in the history");
            Repaint(window); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before));
            Mouse(window, EventType.MouseUp, tip + new Vector2(50, 0)); // 取消の後の離しは何もしない
            Assert.That(VolumeOf(gen.Id), Is.EqualTo(start));
            // フォーカスの喪失・リロードの前も同じ
            foreach (var method in new[] { "OnLostFocus", "BeforeReload" })
            {
                tip = OnArrow(ShapeHandle.MoveX);
                DragHandle(tip, tip + new Vector2(0, 30), release: false);
                Invoke(window, method);
                Assert.That(window.ShapeDragging, Is.False, method); Assert.That(VolumeOf(gen.Id), Is.EqualTo(start), method); Assert.That(d.UndoCount, Is.EqualTo(steps), method);
                Mouse(window, EventType.MouseUp, tip);
            }
            // ストロークの最中は掴まない: ハンドルの無いモデルの上から描き始め、矢印の上を通っても形は動かない
            window.EditMask = true; var b = window.Brush; b.radius = 6; window.Brush = b;
            tip = Handle(ShapeHandle.MoveX);
            Vector2? onModel = null;
            for (int y = -60; y <= 60 && onModel == null; y += 10)
                for (int x = -60; x <= 60 && onModel == null; x += 10)
                {
                    var p = window.SurfaceRect.center + new Vector2(x, y);
                    if (window.ShapeHandleAt(p) == ShapeHandle.None && window.Preview.TryPick(window.SurfaceRect, p, out var hit) && hit.MaterialSlot == 0 && Vector2.Distance(p, tip) > 30) onModel = p;
                }
            Assert.That(onModel.HasValue, Is.True, "a free spot on the cube");
            Mouse(window, EventType.MouseDown, onModel.Value);
            Assert.That(window.IsStroking, Is.True); Assert.That(window.ShapeDragging, Is.False);
            Mouse(window, EventType.MouseDrag, tip); Mouse(window, EventType.MouseUp, tip);
            Assert.That(window.IsStroking, Is.False); Assert.That(VolumeOf(gen.Id), Is.EqualTo(start), "the stroke did not grab the handle");
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "only the stroke");
            // 編集をやめるとギズモも重ね表示も消え、ハンドルの所の押下は今のツールへ
            window.ShapeEditFilter = Guid.Empty; Repaint(window);
            Assert.That(window.ShapeHandleGui(ShapeHandle.MoveX), Is.Null); Assert.That(window.Preview.ShownShapeGradient.HasValue, Is.False);
        }

        [Test] public void TheNumberFieldsEditTheSameValuesAndTheShapeIsSavedAndReopened()
        {
            var gen = ShapeGradientOnDemoCube(); var d = window.Document;
            window.View = TexturePaintWindow.ViewMode.Split; window.SelectedFilter = gen.Id; OpenLayerPanels();
            var start = VolumeOf(gen.Id); int steps = d.UndoCount;
            var gizmoBefore = Handle(ShapeHandle.MoveFree);
            DragLayerControl("generator.shape.center.x", .2f, .8f);
            var dragged = VolumeOf(gen.Id);
            Assert.That(dragged.CenterX, Is.GreaterThan(start.CenterX + .05), "scrubbing the X field moves the centre");
            Assert.That((dragged.CenterY, dragged.CenterZ), Is.EqualTo((start.CenterY, start.CenterZ)), "the other fields are untouched (no float rounding written back)");
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            Assert.That(Handle(ShapeHandle.MoveFree), Is.Not.EqualTo(gizmoBefore), "the gizmo follows the field");
            // 形を球にして、半径の欄
            var sphere = dragged.WithShape(GeneratorShape.Sphere);
            window.ApplyFilterSettings(gen.Id, gen.Settings.WithGenerator(gen.Settings.Generator.WithVolume(sphere)));
            window.LayerControlPanelRects.Clear(); Repaint(window);
            Assert.That(window.LayerControlPanelRects.Keys, Has.Member("generator.shape.radius").And.Member("generator.shape.falloff").And.No.Member("generator.shape.size.x"));
            // 保存して別の窓で開くと形が戻る（モデルを読むまでは入力のまま）
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var shown = d.Composite(PaintChannel.Color);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File; other.OpenProject();
                var od = other.Document; var layer = od.GetLayer(window.SelectedLayer);
                Assert.That(layer.Mask.Filters.Single().Settings.Generator.Volume, Is.EqualTo(sphere), other.StatusMessage);
                Assert.That(od.GetGeneratorStatus(layer.Id, gen.Id).Active, Is.False, "no model yet");
                other.Preview.LoadDemoMesh(); other.PollGeneratorInputs();
                Assert.That(od.GetGeneratorStatus(layer.Id, gen.Id).Active, Is.True);
                Assert.That(od.Composite(PaintChannel.Color), Is.EqualTo(shown));
            }
            finally { Close(other); }
        }
    }
}
