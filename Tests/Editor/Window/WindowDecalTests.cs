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
    /// <summary>描画ウィンドウのデカール（GUI モード、SendEvent）: アセットのパネルの画像を 3D ビューのモデルへドラッグすると、落とした所の面に
    /// 向けて画像の縦横比のデカールを置き（選んだ層の上、1 回の Undo、Ctrl+Z / Ctrl+Shift+Z）、モデルの外では受け取らない。デカールを選んでいる間は
    /// 置き場のギズモが出て（マスクの編集中と Q で隠したときは出ない）、矢印のドラッグで動き 1 回の Undo、Esc とフォーカスの喪失はドラッグの前に戻して履歴にも残さない。2D キャンバスには選んだ
    /// デカールの届く範囲を重ね、投影の欄の間引きのスライダーのドラッグも 1 回の Undo。マップが無いときは欄のボタンで焼いて出す。</summary>
    public sealed partial class WindowTests
    {
        /// <summary>透明な縁の中に不透明な円（ロゴのような形）の、横長の画像。</summary>
        static ImageContent DecalLogo(int w = 48, int h = 24)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4; double dx = (x + .5 - w / 2.0) / (h / 2.0), dy = (y + .5 - h / 2.0) / (h / 2.0);
                rgba[o] = 250; rgba[o + 1] = (byte)(x * 255 / (w - 1)); rgba[o + 2] = 40; rgba[o + 3] = (byte)(dx * dx + dy * dy < .8 ? 255 : 0);
            }
            return ImageContent.FromPixels(rgba, w, h);
        }

        /// <summary>デモのキューブで Position と法線を焼き、3D ビューだけを見せる。</summary>
        void DecalScene(bool bake = true)
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            if (bake)
            {
                QuickBake(window); window.MeshBakeSettings.Maps = new[] { MeshMapKind.Position, MeshMapKind.WorldNormal };
                Assert.That(window.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), window.StatusMessage);
            }
            window.View = TexturePaintWindow.ViewMode.Model; Repaint(window);
            window.Preview.ViewFrom(30, 20); Repaint(window);
        }
        Vector2 Host(Vector2 gui) => gui + window.rootVisualElement.worldBound.position;
        /// <summary>アセットの鍵を GUI の点へドラッグして落とす。受け取ったか（visualMode が Copy か）を返す。</summary>
        bool DropAssetAt(string key, Vector2 gui)
        {
            DragAndDrop.PrepareStartDrag(); DragAndDrop.SetGenericData("YoluPainter.Asset", key); DragAndDrop.objectReferences = new UnityEngine.Object[0];
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            window.SendEvent(new Event { type = EventType.DragUpdated, mousePosition = Host(gui) });
            bool accepted = DragAndDrop.visualMode == DragAndDropVisualMode.Copy;
            window.SendEvent(new Event { type = EventType.DragPerform, mousePosition = Host(gui) });
            DragAndDrop.SetGenericData("YoluPainter.Asset", null);
            Repaint(window);
            return accepted;
        }
        PaintLayer DropDecal(out ImageResource logo)
        {
            logo = window.ImageResources.Add("Logo", DecalLogo(), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            Assert.That(DropAssetAt("p:" + logo.Id.ToString("D"), window.SurfaceRect.center), Is.True, window.StatusMessage);
            var layer = window.Document.GetLayer(window.SelectedLayer);
            Assert.That(layer.IsDecal, Is.True, window.StatusMessage);
            return layer;
        }

        [Test] public void DroppingAnImageOnTheModelPlacesADecalFacingTheSurfaceAsOneUndoStep()
        {
            DecalScene(); var d = window.Document; d.ClearHistory();
            int layers = d.Layers.Count; var before = Snapshot();
            // モデルの外（3D ビューの角）では受け取らない
            var corner = new Vector2(window.SurfaceRect.xMin + 4, window.SurfaceRect.yMin + 4);
            var unused = window.ImageResources.Add("Unused", DecalLogo(8, 8), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            Assert.That(DropAssetAt("p:" + unused.Id.ToString("D"), corner), Is.False, "nothing to place it on");
            Assert.That(d.Layers.Count, Is.EqualTo(layers)); Assert.That(window.StatusMessage, Does.Contain("on the model"));
            // モデルの真ん中へ
            Assert.That(window.Preview.TryPick(window.SurfaceRect, window.SurfaceRect.center, out var hit), Is.True);
            var decal = DropDecal(out var logo);
            Assert.That(d.Layers.Count, Is.EqualTo(layers + 1)); Assert.That(d.UndoCount, Is.EqualTo(1), "placing is one step");
            Assert.That(decal.FillImages[PaintChannel.Color], Is.EqualTo(logo.Id)); Assert.That(decal.Name, Is.EqualTo("Logo"));
            Assert.That(window.ProjectionEditLayer, Is.EqualTo(decal.Id), "its box can be dragged at once");
            Assert.That(d.GetDecalProblem(decal.Id), Is.Null);
            var v = decal.Projection.Placement;
            Assert.That(Vector3.Distance(new Vector3((float)v.CenterX, (float)v.CenterY, (float)v.CenterZ), hit.Position), Is.LessThan(1e-3f), "centred where it was dropped (the cube's root is at the origin)");
            var into = Quaternion.Euler((float)v.RotationX, (float)v.RotationY, (float)v.RotationZ) * Vector3.forward;
            Assert.That(Vector3.Dot(into, -hit.Normal), Is.GreaterThan(.99f), "+Z goes into the surface, so the image is seen from outside");
            Assert.That(v.SizeX / v.SizeY, Is.EqualTo(2).Within(1e-6), "the image's aspect");
            Assert.That(v.SizeZ, Is.EqualTo(v.SizeX * TexturePaintWindow.DecalDepthFraction).Within(1e-6));
            var placed = Snapshot();
            Assert.That(placed, Is.Not.EqualTo(before), "the decal shows on the cube");
            // 落とした所の UV の画素がロゴの色（赤 250・青 40。ロゴの真ん中は不透明）になる
            int ux = Mathf.Clamp((int)(hit.UV.x * d.Width), 0, d.Width - 1), uy = Mathf.Clamp((int)(hit.UV.y * d.Height), 0, d.Height - 1);
            var at = d.CompositePixel(PaintChannel.Color, ux, uy);
            Assert.That((at.R, at.B, at.A), Is.EqualTo(((byte)250, (byte)40, (byte)255)), "the logo at the dropped point: " + at);
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(d.Layers.Count, Is.EqualTo(layers)); Assert.That(Snapshot(), Is.EqualTo(before));
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(d.Layers.Count, Is.EqualTo(layers + 1)); Assert.That(Snapshot(), Is.EqualTo(placed));
            // 2D キャンバスへ落とすのは今までどおりペイントの層（UV の全面）
            window.View = TexturePaintWindow.ViewMode.Split; Repaint(window);
            Assert.That(DropAssetAt("p:" + logo.Id.ToString("D"), window.PixelToGui(d.Width / 2, d.Height / 2)), Is.True);
            Assert.That(d.GetLayer(window.SelectedLayer).Kind, Is.EqualTo(LayerKind.Raster));
        }

        [Test] public void TheDecalMovesWithTheGizmoAsOneUndoStepAndEscapeOrFocusLossCancels()
        {
            DecalScene(); var d = window.Document;
            var decal = DropDecal(out _); d.ClearHistory();
            var start = decal.Projection.Placement; var before = Snapshot();
            var tip = OnArrow(ShapeHandle.MoveX);
            DragHandle(tip, tip + new Vector2(30, 0));
            var moved = decal.Projection.Placement;
            Assert.That(new Vector3((float)(moved.CenterX - start.CenterX), (float)(moved.CenterY - start.CenterY), (float)(moved.CenterZ - start.CenterZ)).magnitude, Is.GreaterThan(.02f), window.StatusMessage);
            Assert.That((moved.SizeX, moved.SizeY, moved.SizeZ, moved.RotationY), Is.EqualTo((start.SizeX, start.SizeY, start.SizeZ, start.RotationY)), "a move keeps the size and the turn");
            Assert.That(d.UndoCount, Is.EqualTo(1), "one drag, one step");
            var after = Snapshot();
            Assert.That(after, Is.Not.EqualTo(before), "the decal moved");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(decal.Projection.Placement, Is.EqualTo(start)); Assert.That(Snapshot(), Is.EqualTo(before));
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(Snapshot(), Is.EqualTo(after));
            Key(window, KeyCode.Z, EventModifiers.Control);
            // Esc: ドラッグの前に戻し、履歴にも残さない
            tip = OnArrow(ShapeHandle.MoveY);
            DragHandle(tip, tip + new Vector2(0, -30), release: false);
            Assert.That(decal.Projection.Placement, Is.Not.EqualTo(start), "moved while dragging");
            Key(window, KeyCode.Escape);
            Assert.That(window.ShapeDragging, Is.False); Assert.That(decal.Projection.Placement, Is.EqualTo(start)); Assert.That(d.UndoCount, Is.EqualTo(0));
            Mouse(window, EventType.MouseUp, tip + new Vector2(0, -30));
            Assert.That(decal.Projection.Placement, Is.EqualTo(start)); Assert.That(Snapshot(), Is.EqualTo(before));
            // フォーカスの喪失も同じ
            window.ShapeGizmoMode = ShapeGizmoMode.Rotate; Repaint(window);
            var ring = Handle(ShapeHandle.RotateZ);
            DragHandle(ring, ring + new Vector2(12, 12), release: false);
            Invoke(window, "OnLostFocus");
            Assert.That(window.ShapeDragging, Is.False); Assert.That(decal.Projection.Placement, Is.EqualTo(start)); Assert.That(d.UndoCount, Is.EqualTo(0));
        }

        /// <summary>Substance Painter のマニピュレーターと同じく、デカール（型の上の投影）の層を選んでいる間は置き場のハンドルが出て、選びを外すと
        /// 消える。マスクの編集中は出さず、ハンドルのあった所のクリックはマスクを塗る（置き場は動かない）。層そのものを選び直すと出て、同じ所の
        /// 押下はハンドルを掴んで塗らない（同じクリックで両方は起きない）。Q と投影の欄のボタンで隠す・出す。</summary>
        [Test] public void SelectingADecalShowsItsHandlesAndEditingItsMaskOrQHidesThem()
        {
            DecalScene(); var d = window.Document;
            var decal = DropDecal(out _);
            var paint = d.AddLayer("Paint"); d.ClearHistory();
            window.SelectedLayer = paint.Id; Repaint(window);
            Assert.That(window.ShapeHandleGui(ShapeHandle.MoveFree), Is.Null, "a paint layer has no box");
            Assert.That(window.ProjectionEditLayer, Is.EqualTo(Guid.Empty));
            window.SelectedLayer = decal.Id; Repaint(window);
            var centre = Handle(ShapeHandle.MoveFree);
            Assert.That(window.ProjectionEditLayer, Is.EqualTo(decal.Id), "selecting the decal shows its handles");
            // マスクの編集中: 出さない。ハンドルのあった所を描くツールで押すと、マスクを塗る
            d.AddLayerMask(decal.Id); window.EditMask = true; window.SelectTool(TexturePaintWindow.PaintTool.Brush); d.ClearHistory(); Repaint(window);
            Assert.That(window.ShapeHandleGui(ShapeHandle.MoveFree), Is.Null, "no handles while the mask is edited");
            Assert.That(window.ShapeHandleAt(centre), Is.EqualTo(ShapeHandle.None));
            var placement = decal.Projection.Placement; long mask = decal.Mask.Surface.Revision;
            Mouse(window, EventType.MouseDown, centre); Assert.That(window.ShapeDragging, Is.False);
            Mouse(window, EventType.MouseDrag, centre + new Vector2(12, 0)); Mouse(window, EventType.MouseUp, centre + new Vector2(12, 0)); Repaint(window);
            Assert.That(decal.Mask.Surface.Revision, Is.Not.EqualTo(mask), "the press painted the mask: " + window.StatusMessage);
            Assert.That(decal.Projection.Placement, Is.EqualTo(placement), "and did not move the box");
            Assert.That(d.UndoCount, Is.EqualTo(1));
            // 層そのものを選び直すと出る。ハンドルの押下は掴むだけで、塗らない
            window.EditMask = false; Repaint(window);
            Assert.That(window.ProjectionEditLayer, Is.EqualTo(decal.Id));
            mask = decal.Mask.Surface.Revision;
            var arrow = OnArrow(ShapeHandle.MoveX);
            Mouse(window, EventType.MouseDown, arrow);
            Assert.That(window.ShapeDragging, Is.True); Assert.That(d.HasActiveStroke, Is.False, "the press on a handle starts no stroke");
            Mouse(window, EventType.MouseDrag, arrow + new Vector2(30, 0)); Mouse(window, EventType.MouseUp, arrow + new Vector2(30, 0)); Repaint(window);
            Assert.That(decal.Projection.Placement, Is.Not.EqualTo(placement), window.StatusMessage);
            Assert.That(decal.Mask.Surface.Revision, Is.EqualTo(mask), "nothing painted"); Assert.That(d.UndoCount, Is.EqualTo(2), "one step for the drag");
            // Q で隠し、もう一度で出す
            Key(window, KeyCode.Q); Repaint(window);
            Assert.That(window.ProjectionHandlesHidden, Is.True); Assert.That(window.ShapeHandleGui(ShapeHandle.MoveFree), Is.Null);
            Key(window, KeyCode.Q); Repaint(window);
            Assert.That(window.ShapeHandleGui(ShapeHandle.MoveFree), Is.Not.Null);
            // 投影の欄のボタンも同じ切り替え
            var open = (Dictionary<string, bool>)typeof(TexturePaintWindow).GetField("sectionOpen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(window);
            open["projection"] = true; OpenLayerPanels();
            ClickLayerControl("projection.edit");
            Assert.That(window.ProjectionHandlesHidden, Is.True); Assert.That(window.ShapeHandleGui(ShapeHandle.MoveFree), Is.Null);
            ClickLayerControl("projection.edit");
            Assert.That(window.ShapeHandleGui(ShapeHandle.MoveFree), Is.Not.Null);
            // ほかの型の上の投影（平面）の塗りつぶしも、選べば出る。UV に戻すと消える
            var planar = d.AddFillLayer("Planar", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(10, 200, 10, 255) } });
            window.SelectedLayer = planar.Id; window.SetProjectionMode(planar.Id, FillProjectionMode.Planar); Repaint(window);
            Assert.That(window.ProjectionEditLayer, Is.EqualTo(planar.Id)); Assert.That(window.ShapeHandleGui(ShapeHandle.MoveFree), Is.Not.Null);
            window.SetProjectionMode(planar.Id, FillProjectionMode.Uv); Repaint(window);
            Assert.That(window.ShapeHandleGui(ShapeHandle.MoveFree), Is.Null);
        }

        [Test] public void TheCanvasShowsTheSelectedDecalsReachAndTheCullingSlidersAreOneUndoStep()
        {
            DecalScene(); var d = window.Document;
            var decal = DropDecal(out _); d.ClearHistory();
            var overlay = window.EnsureDecalOverlay();
            Assert.That(overlay, Is.Not.Null, "a placed decal shows its reach on the 2D canvas");
            var pixels = overlay.GetPixels32();
            int reach = pixels.Count(p => p.a > 0);
            Assert.That(reach, Is.GreaterThan(0).And.LessThan(pixels.Length / 2), "only where the box reaches");
            // 選んだ層がデカールでなければ重ねない
            var plain = d.AddLayer("Paint"); window.SelectedLayer = plain.Id;
            Assert.That(window.EnsureDecalOverlay(), Is.Null);
            window.SelectedLayer = decal.Id;
            // 投影の欄: 裏向きの角度のスライダーのドラッグは 1 回の Undo
            var open = (Dictionary<string, bool>)typeof(TexturePaintWindow).GetField("sectionOpen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(window);
            open["projection"] = true; OpenLayerPanels();
            Assert.That(window.LayerControlPanelRects.Keys, Has.Member("decal.depth").And.Member("decal.angle").And.Member("decal.angle.edge").And.Member("projection.center.x"));
            int steps = d.UndoCount; double angle = decal.Projection.BackfaceAngle;
            DragLayerControl("decal.angle", .5f, .15f);
            Assert.That(decal.Projection.BackfaceAngle, Is.LessThan(angle), window.StatusMessage);
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "the drag is one step");
            var narrowed = window.EnsureDecalOverlay().GetPixels32().Count(p => p.a > 0);
            Assert.That(narrowed, Is.LessThanOrEqualTo(reach), "fewer faces turn towards the image within a smaller angle");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(decal.Projection.BackfaceAngle, Is.EqualTo(angle));
        }

        [Test] public void ADecalWithoutMapsSaysWhyAndTheButtonBakesThem()
        {
            DecalScene(bake: false); var d = window.Document;
            var logo = window.ImageResources.Add("Logo", DecalLogo(), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
            Assert.That(DropAssetAt("p:" + logo.Id.ToString("D"), window.SurfaceRect.center), Is.True);
            var decal = d.GetLayer(window.SelectedLayer);
            Assert.That(decal.IsDecal, Is.True); Assert.That(window.StatusMessage, Does.Contain("not shown yet"));
            Assert.That(d.GetDecalProblem(decal.Id), Does.Contain("Position"));
            Assert.That(window.EnsureDecalOverlay(), Is.Null, "nothing to show on the canvas");
            var open = (Dictionary<string, bool>)typeof(TexturePaintWindow).GetField("sectionOpen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(window);
            open["projection"] = true; OpenLayerPanels();
            QuickBake(window);
            ClickLayerControl("decal.bake");
            Assert.That(d.GetDecalProblem(decal.Id), Is.Null, window.StatusMessage);
            Assert.That(window.MeshBakeSettings.Maps, Is.EqualTo(MeshBakeSettings.DefaultKinds.ToArray()), "the chosen maps are kept");
            Assert.That(window.EnsureDecalOverlay(), Is.Not.Null);
        }
    }
}
