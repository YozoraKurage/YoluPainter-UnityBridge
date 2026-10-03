using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウの塗りつぶしの画像と投影（GUI モード、SendEvent）: アセットのパネルの 1 つを塗りつぶしのチャンネルの欄へドラッグすると
    /// その画像を使い（このプロジェクトに無ければ取り込んでから）、1 回の Undo（Ctrl+Z / Ctrl+Shift+Z）、欄のボタンで外す。型の上の投影にすると
    /// 3D ビューに置き場のボックスのギズモが出て、矢印・面の四角のドラッグで置き場が動き、どれも 1 回の Undo。Esc とフォーカスの喪失はドラッグの前に
    /// 戻して履歴にも残さない。プロパティの欄のタイルの数値をドラッグしても 1 回の Undo。</summary>
    public sealed partial class WindowTests
    {
        PaintLayer FillForImages()
        {
            var d = window.Document;
            var fill = d.AddFillLayer("Projected", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(220, 40, 40, 255) } });
            d.ClearHistory(); window.SelectedLayer = fill.Id; window.Channel = PaintChannel.Color;
            return fill;
        }
        void DropAsset(string controlId, string assetKey)
        {
            var p = LayerControlPoint(controlId);
            DragAndDrop.PrepareStartDrag(); DragAndDrop.SetGenericData("YoluPainter.Asset", assetKey); DragAndDrop.objectReferences = new UnityEngine.Object[0];
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            window.SendEvent(new Event { type = EventType.DragUpdated, mousePosition = p });
            Assert.That(DragAndDrop.visualMode, Is.EqualTo(DragAndDropVisualMode.Copy), "the field accepts the asset");
            window.SendEvent(new Event { type = EventType.DragPerform, mousePosition = p });
            DragAndDrop.SetGenericData("YoluPainter.Asset", null);
            Repaint(window);
        }

        [Test] public void DroppingAnAssetOnAFillChannelUsesItAsOneUndoStep()
        {
            var fill = FillForImages(); var d = window.Document;
            var checker = window.ImportBuiltInImage("uv-checker");
            OpenLayerPanels();
            var before = Snapshot();
            DropAsset("fill.image", "p:" + checker.Id.ToString("D"));
            Assert.That(fill.FillImages.TryGetValue(PaintChannel.Color, out var used), Is.True, window.StatusMessage); Assert.That(used, Is.EqualTo(checker.Id));
            Assert.That(d.UndoCount, Is.EqualTo(1)); Assert.That(d.GetFillImageStatus(fill.Id, PaintChannel.Color).Active, Is.True);
            var withImage = Snapshot();
            Assert.That(withImage, Is.Not.EqualTo(before));
            Assert.That(window.LayerControlPanelRects.Keys, Has.Member("fill.image.colorspace").And.Member("fill.image.clear"), "the image's controls show");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(fill.HasFillImage(PaintChannel.Color), Is.False); Assert.That(Snapshot(), Is.EqualTo(before));
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(fill.FillImages[PaintChannel.Color], Is.EqualTo(checker.Id)); Assert.That(Snapshot(), Is.EqualTo(withImage));
            // 内蔵の画像（まだこのプロジェクトに無い）を落とすと、取り込んでから使う
            DropAsset("fill.image", "b:grid");
            var grid = window.ImageResources.Images.Single(r => r.Origin.BuiltInKey == "grid");
            Assert.That(fill.FillImages[PaintChannel.Color], Is.EqualTo(grid.Id));
            Assert.That(() => window.RemoveResource(grid.Id), Throws.TypeOf<ResourceRefusedException>(), "an image in use stays");
            // 欄の × で外す（1 回の Undo）
            int steps = d.UndoCount;
            ClickLayerControl("fill.image.clear");
            Assert.That(fill.HasFillImage(PaintChannel.Color), Is.False); Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            Assert.That(fill.FillValues[PaintChannel.Color], Is.EqualTo(new Rgba32(220, 40, 40, 255)), "the value stays");
        }

        /// <summary>デモのキューブで Position と法線を焼き、塗りつぶしの Color に画像を差してトライプラナーの投影にする（3D ビューだけを見せる）。</summary>
        PaintLayer ProjectedOnDemoCube()
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            QuickBake(window); window.MeshBakeSettings.Maps = new[] { MeshMapKind.Position, MeshMapKind.WorldNormal };
            Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), window.StatusMessage);
            var fill = FillForImages();
            window.SetFillImageFromAsset(fill.Id, PaintChannel.Color, "b:uv-checker");
            window.SetProjectionMode(fill.Id, FillProjectionMode.Triplanar);
            Assert.That(window.ProjectionEditLayer, Is.EqualTo(fill.Id), "a projection on the model starts editing its box");
            Assert.That(fill.Projection.Placement, Is.Not.EqualTo(FillProjection.DefaultPlacement), "fitted to the model");
            window.View = TexturePaintWindow.ViewMode.Model; Repaint(window);
            window.Preview.ViewFrom(30, 20); Repaint(window);
            window.PollGeneratorInputs();
            Assert.That(window.Document.GetFillImageStatus(fill.Id, PaintChannel.Color).Active, Is.True, window.Document.GetFillImageStatus(fill.Id, PaintChannel.Color).Reason);
            return fill;
        }

        [Test] public void TheProjectionBoxMovesWithTheGizmoAsOneUndoStepAndEscapeCancels()
        {
            var fill = ProjectedOnDemoCube(); var d = window.Document;
            var start = fill.Projection.Placement; var before = d.Composite(PaintChannel.Color); int steps = d.UndoCount;
            Assert.That(window.Preview.ShownShapeGradient.HasValue, Is.False, "no value overlay for a projection (the composite shows it)");
            var tip = OnArrow(ShapeHandle.MoveX);
            DragHandle(tip, tip + new Vector2(40, 0));
            var moved = fill.Projection.Placement;
            Assert.That(Math.Abs(moved.CenterX - start.CenterX), Is.GreaterThan(.02), window.StatusMessage);
            Assert.That(moved.CenterY, Is.EqualTo(start.CenterY).Within(1e-4)); Assert.That(moved.CenterZ, Is.EqualTo(start.CenterZ).Within(1e-4));
            Assert.That(moved.Shape, Is.EqualTo(GeneratorShape.Box)); Assert.That(moved.Falloff, Is.Zero);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "one drag, one step");
            Assert.That(d.Composite(PaintChannel.Color), Is.Not.EqualTo(before), "the projected image moved");
            // 面の四角で大きさ
            var knob = Handle(ShapeHandle.SizeYPos); var c = Handle(ShapeHandle.MoveFree);
            DragHandle(knob, knob + (knob - c).normalized * 25);
            Assert.That(fill.Projection.Placement.SizeY, Is.GreaterThan(moved.SizeY + .01)); Assert.That(d.UndoCount, Is.EqualTo(steps + 2));
            Key(window, KeyCode.Z, EventModifiers.Control); Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(fill.Projection.Placement, Is.EqualTo(start)); Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before));
            // Esc: ドラッグの前に戻し、履歴にも残さない
            tip = OnArrow(ShapeHandle.MoveZ);
            DragHandle(tip, tip + new Vector2(0, 40), release: false);
            Assert.That(fill.Projection.Placement, Is.Not.EqualTo(start), "moved while dragging");
            Key(window, KeyCode.Escape);
            Assert.That(window.ShapeDragging, Is.False); Assert.That(fill.Projection.Placement, Is.EqualTo(start)); Assert.That(d.UndoCount, Is.EqualTo(steps));
            Mouse(window, EventType.MouseUp, tip + new Vector2(0, 40));
            Assert.That(fill.Projection.Placement, Is.EqualTo(start));
            // フォーカスの喪失も同じ
            tip = OnArrow(ShapeHandle.MoveY);
            DragHandle(tip, tip + new Vector2(0, -30), release: false);
            Invoke(window, "OnLostFocus");
            Assert.That(window.ShapeDragging, Is.False); Assert.That(fill.Projection.Placement, Is.EqualTo(start)); Assert.That(d.UndoCount, Is.EqualTo(steps));
            // 球の投影は球として見せる（面の四角は半径）
            window.SetProjectionMode(fill.Id, FillProjectionMode.Spherical); Repaint(window);
            Assert.That(window.ShapeHandleGui(ShapeHandle.SizeXPos), Is.Not.Null);
            // UV に戻すとギズモは消える
            window.SetProjectionMode(fill.Id, FillProjectionMode.Uv); Repaint(window);
            Assert.That(window.ShapeHandleGui(ShapeHandle.MoveFree), Is.Null);
        }

        /// <summary>3D ビューは絵に効く入力が変わったときだけ描く（IsolatedModelPreview.ComputeRenderKey）。塗りつぶしの画像の中身・読み方・投影が
        /// 変わると、表示の合成器が合成し直したテクスチャを渡し直すので 3D ビューも描き直し、変わらない Repaint では描かない。</summary>
        [Test] public void ChangingAFillImageOrItsProjectionRedrawsThe3DViewOnceAndOnlyThen()
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True); window.Preview.FrameRateLimit = 0;
            var fill = FillForImages();
            window.SetFillImageFromAsset(fill.Id, PaintChannel.Color, "b:uv-checker");
            var image = window.ImageResources.Get(fill.FillImages[PaintChannel.Color]);
            Repaint(window); Repaint(window);
            int renders = window.Preview.RenderCount;
            Assert.That(renders, Is.GreaterThan(0));
            void Redraws(string what)
            {
                Repaint(window);
                Assert.That(window.Preview.RenderCount, Is.GreaterThan(renders), what + " redraws the 3D view");
                renders = window.Preview.RenderCount;
                for (int i = 0; i < 10; i++) Repaint(window);
                Assert.That(window.Preview.RenderCount, Is.EqualTo(renders), what + ": then repaints reuse the picture");
            }
            for (int i = 0; i < 10; i++) Repaint(window);
            Assert.That(window.Preview.RenderCount, Is.EqualTo(renders), "nothing changed, nothing drawn");
            window.Document.SetFillProjection(fill.Id, fill.Projection.WithTiles(3, 2)); Redraws("the tiling");
            var rgba = image.Content.CopyPixels(); for (int i = 0; i < rgba.Length; i += 4) rgba[i] = (byte)(255 - rgba[i]);
            window.ImageResources.ReplaceContent(image.Id, ImageContent.FromPixels(rgba, image.Width, image.Height), null); Redraws("updating the image from its source");
            window.ImageResources.SetColorSpace(image.Id, ResourceColorSpace.Linear); Redraws("reading the image as data (encoded to sRGB in Color)");
            window.Document.SetFillImage(fill.Id, PaintChannel.Color, null); Redraws("removing the image");
        }

        [Test] public void DraggingTheTilingFieldIsOneUndoStep()
        {
            var fill = FillForImages(); var d = window.Document;
            window.SetFillImageFromAsset(fill.Id, PaintChannel.Color, "b:uv-checker");
            var open = (Dictionary<string, bool>)typeof(TexturePaintWindow).GetField("sectionOpen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(window);
            open["projection"] = true; OpenLayerPanels();
            int steps = d.UndoCount; double tiles = fill.Projection.TileU;
            DragLayerControl("projection.tile.u", .5f, .9f);
            Assert.That(fill.Projection.TileU, Is.Not.EqualTo(tiles), window.StatusMessage);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "the drag is one step");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(fill.Projection.TileU, Is.EqualTo(tiles));
        }
    }
}
