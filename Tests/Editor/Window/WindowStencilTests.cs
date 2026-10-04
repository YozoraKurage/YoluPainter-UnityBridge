using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ステンシルを本物のウィンドウで（GUI モード、SendEvent）: ブラシの欄に画像を落とすと 2D と 3D の画面に重なり、塗った画素は
    /// 「その画素が画面のどこに映るか」のステンシルの値で決まる（2D はキャンバスの画素、3D はテクセルの点をカメラで写した所。カメラを回しても
    /// 画面に貼り付いたまま）。T を押したままのドラッグで動かす・回す（Shift で 15°）・大きさを変え、Esc で戻す。N を押しているあいだは効かない。
    /// Undo/Redo と Esc の取消はふつうのストロークと同じ。マテリアルでは色を Base Color に、ほかは α の量で。置き場は窓の状態で、プロジェクトから
    /// 画像が消えたら外して知らせる。</summary>
    public sealed partial class WindowTests
    {
        static readonly Rgba32 StencilWhite = new Rgba32(255, 255, 255, 255), StencilBlack = new Rgba32(0, 0, 0, 255);

        ImageResource AddStencilImage(string name, int w, int h, Func<int, int, Rgba32> pixel, ResourceColorSpace space = ResourceColorSpace.Srgb)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var p = pixel(x, y); int o = (y * w + x) * 4; rgba[o] = p.R; rgba[o + 1] = p.G; rgba[o + 2] = p.B; rgba[o + 3] = p.A; }
            return window.ImageResources.Add(name, ImageContent.FromPixels(rgba, w, h), ResourceOrigin.None, space, out _);
        }
        /// <summary>左半分が白・右半分が黒の 64² の画像（量のステンシル）。</summary>
        ImageResource HalfStencil() => AddStencilImage("Half", 64, 64, (x, y) => x < 32 ? StencilWhite : StencilBlack);

        void HardBrush(float radius)
        {
            var b = window.Brush; b.radius = radius; b.hardness = 1; b.flow = 1; b.opacity = 1; b.spacing = .1f;
            b.pressureSize = false; b.pressureOpacity = false; b.pressureFlow = false; b.color = new Color(.9f, .3f, .1f, 1); window.Brush = b;
        }
        /// <summary>ボタンと修飾キーを選んで送る（<see cref="Mouse"/> は左ボタンだけ）。</summary>
        void Button(EventType type, Vector2 at, int button, EventModifiers modifiers = EventModifiers.None)
        { EditorShaderCompiler.TolerateErrorLogsIfBroken(); window.SendEvent(new Event { type = type, mousePosition = at + window.rootVisualElement.worldBound.position, button = button, modifiers = modifiers, pressure = 1 }); }
        void Drag(Vector2 from, Vector2 to, int button, EventModifiers modifiers = EventModifiers.None)
        {
            Button(EventType.MouseDown, from, button, modifiers);
            for (int i = 1; i <= 4; i++) Button(EventType.MouseDrag, Vector2.Lerp(from, to, i / 4f), button, modifiers);
            Button(EventType.MouseUp, to, button, modifiers);
        }

        /// <summary>GUI の点の、ステンシルの画像の x（画素）。</summary>
        double StencilX(Rect view, Vector2 gui) { window.StencilFrameIn(view).Value.ToImage(gui.x, gui.y, out double x, out _); return x; }

        // ───────── 2D ─────────

        /// <summary>キャンバスの中央の 1 つのダブを、ステンシル無し（N）と有りで塗り、白が映る画素は無しと同じ、黒が映る画素は塗らないことを
        /// 確かめる（画素の中心の GUI の点から、ステンシルのどこかを求める）。塗ったものを返す（1 回の Undo で戻ることも確かめる）。</summary>
        byte[] AssertCanvasDabFollowsTheStencil(string what)
        {
            Key(window, KeyCode.N); Assert.That(window.StencilIgnored, Is.True);
            var at = At(window, 512, 512);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            var plain = Snapshot(); Key(window, KeyCode.Z, EventModifiers.Control);
            KeyUp(KeyCode.N); Assert.That(window.StencilIgnored, Is.False);
            var before = Snapshot();
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            var painted = Snapshot();
            int w = window.Document.Width, through = 0, held = 0;
            for (int y = 420; y < 604; y += 2) for (int x = 420; x < 604; x += 2)
            {
                double sx = StencilX(window.CanvasRect, window.PixelToGui(x, y));
                int o = (y * w + x) * 4;
                if (sx < 30.5) { Assert.That(painted[o + 3], Is.EqualTo(plain[o + 3]), what + ": white lets the paint through at " + x + "," + y); if (plain[o + 3] > 0) through++; }
                else if (sx > 33.5) { Assert.That(painted[o + 3], Is.Zero, what + ": black holds it back at " + x + "," + y); if (plain[o + 3] > 0) held++; }
            }
            Assert.That(through, Is.GreaterThan(200), what); Assert.That(held, Is.GreaterThan(200), what);
            Key(window, KeyCode.Z, EventModifiers.Control); Assert.That(Snapshot(), Is.EqualTo(before), what + ": one undo");
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift); Assert.That(Snapshot(), Is.EqualTo(painted), what + ": redo");
            Key(window, KeyCode.Z, EventModifiers.Control);
            return painted;
        }

        [Test] public void OnTheCanvasThePaintGoesThroughWhereThePixelShowsUnderTheStencil()
        {
            window.View = TexturePaintWindow.ViewMode.Canvas; HardBrush(80); Repaint(window);
            var half = HalfStencil();
            window.SetStencil(half.Id); Repaint(window);
            Assert.That(window.StencilApplies, Is.True); Assert.That(window.CurrentStencilImage.IsGrey, Is.True);
            window.StencilAngle = 30; window.StencilCenter = new Vector2(.45f, .55f); Repaint(window);
            AssertCanvasDabFollowsTheStencil("straight view");
            // 表示を回し・左右反転し・拡大しても、ステンシルは画面のまま（キャンバスの画素の画面の位置で読む）
            Assert.That(window.RotateCanvasView(30), Is.True); Assert.That(window.FlipCanvasView(), Is.True); window.ZoomCanvasView(1.4f); Repaint(window);
            var painted = AssertCanvasDabFollowsTheStencil("rotated, flipped and zoomed view");
            // Esc はステンシルのストロークも何も残さない
            var before = Snapshot();
            BeginLine(300, 500); Key(window, KeyCode.Escape);
            Assert.That(window.IsStroking, Is.False); Assert.That(Snapshot(), Is.EqualTo(before));
            Assert.That(window.Document.UndoCount, Is.Zero);
        }

        [Test] public void HoldingTAndDraggingMovesTurnsAndResizesTheStencilAndEscapePutsItBack()
        {
            window.View = TexturePaintWindow.ViewMode.Canvas; Repaint(window);
            window.SetStencil(HalfStencil().Id); Repaint(window);
            var view = window.CanvasRect; var center = view.center;
            Key(window, KeyCode.T);
            Assert.That(window.StencilKeyHeld, Is.True);
            Assert.That(((IPainterShortcutScope)window).TookKey(KeyCode.T, EventModifiers.None), Is.True, "the shortcut guard sees T as the window's key");
            // 中ボタン: 動かす
            Drag(center, center + new Vector2(100, 50), 2);
            Assert.That(window.StencilCenter.x, Is.EqualTo(.5f + 100 / view.width).Within(1e-4)); Assert.That(window.StencilCenter.y, Is.EqualTo(.5f + 50 / view.height).Within(1e-4));
            // Ctrl+左も動かす
            Drag(center, center - new Vector2(100, 50), 0, EventModifiers.Control);
            Assert.That(window.StencilCenter.x, Is.EqualTo(.5f).Within(1e-4)); Assert.That(window.StencilCenter.y, Is.EqualTo(.5f).Within(1e-4));
            // 右ボタン: 大きさ（右へ 100 点で e^0.5 倍）。Alt+左も
            Drag(center, center + new Vector2(100, 0), 1);
            Assert.That(window.StencilSize, Is.EqualTo(TexturePaintWindow.DefaultStencilSize * Mathf.Exp(.5f)).Within(1e-4));
            Drag(center, center - new Vector2(100, 0), 0, EventModifiers.Alt);
            Assert.That(window.StencilSize, Is.EqualTo(TexturePaintWindow.DefaultStencilSize).Within(1e-4));
            // 左: 中心のまわりに回す（右から下へ = 時計回りに 90°）。Shift で 15° 刻み
            var right = center + new Vector2(100, 0);
            Button(EventType.MouseDown, right, 0);
            for (int i = 1; i <= 6; i++) { float a = i * 15 * Mathf.Deg2Rad; Button(EventType.MouseDrag, center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 100, 0); }
            Button(EventType.MouseUp, center + new Vector2(0, 100), 0);
            Assert.That(window.StencilAngle, Is.EqualTo(90).Within(.05f));
            Drag(right, center + new Vector2(100, 30), 0, EventModifiers.Shift);
            Assert.That(window.StencilAngle, Is.EqualTo(105).Within(1e-3), "16.7° more, snapped to 15");
            Assert.That(window.Document.UndoCount, Is.Zero, "placing the stencil paints nothing and is not an undo step");
            Assert.That(window.IsStroking, Is.False);
            // Esc はドラッグの前に戻す
            var placed = (window.StencilCenter, window.StencilSize, window.StencilAngle);
            Button(EventType.MouseDown, center, 2); Button(EventType.MouseDrag, center + new Vector2(80, 0), 2);
            Assert.That(window.StencilDragging, Is.EqualTo(TexturePaintWindow.StencilDragKind.Move));
            Assert.That(window.StencilCenter, Is.Not.EqualTo(placed.Item1));
            Key(window, KeyCode.Escape);
            Assert.That(window.StencilDragging, Is.EqualTo(TexturePaintWindow.StencilDragKind.None));
            Assert.That((window.StencilCenter, window.StencilSize, window.StencilAngle), Is.EqualTo(placed));
            Button(EventType.MouseUp, center + new Vector2(80, 0), 2);
            Assert.That(window.StencilCenter, Is.EqualTo(placed.Item1), "the release after Esc changes nothing");
            // フォーカスを失うと T を押していた印は消える。離した後の左クリックは塗る
            Invoke(window, "OnLostFocus"); Assert.That(window.StencilKeyHeld, Is.False);
            Key(window, KeyCode.T); KeyUp(KeyCode.T); Assert.That(window.StencilKeyHeld, Is.False);
            HardBrush(30); var at = At(window, 512, 512);
            window.ResetStencilPlacement(); window.StencilSize = 4; Repaint(window); // 白の半分が点を覆う大きさ
            window.StencilCenter = new Vector2(.5f + (float)(10 / view.width), .5f); Repaint(window);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(window.Document.UndoCount, Is.EqualTo(1), window.StatusMessage);
        }

        // ───────── 3D ─────────

        /// <summary>デモのキューブのテクセル (x, y) の中心の、面の上の点（UV の島が離れているので 1 つ）。</summary>
        static bool TexelPoint(SurfaceGeometry g, int width, int height, int x, int y, out Vector3 point)
        {
            var uv = new Vector2((x + .5f) / width, (y + .5f) / height);
            foreach (var t in g.Triangles)
                if (SurfaceGeometry.TryUvBarycentric(uv, t, out var b)) { point = t.A * b.x + t.B * b.y + t.C * b.z; return true; }
            point = default; return false;
        }

        /// <summary>3D ビューを左から右へ横切るストロークを、ステンシル無し（N）と有りで塗り、ステンシルの白が映るテクセルは無しと同じ、
        /// 黒が映るテクセルは塗らないことを確かめる（テクセルの点をカメラで画面へ写して、ステンシルのどこかを求める）。</summary>
        void AssertSurfaceStrokeFollowsTheStencil(string what)
        {
            var rect = window.SurfaceRect; var c = rect.center;
            void Stroke() { Mouse(window, EventType.MouseDown, c + new Vector2(-90, 10)); Mouse(window, EventType.MouseDrag, c + new Vector2(-30, -5)); Mouse(window, EventType.MouseDrag, c + new Vector2(30, 8)); Mouse(window, EventType.MouseUp, c + new Vector2(90, -4)); }
            Key(window, KeyCode.N); Stroke(); KeyUp(KeyCode.N);
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            var plain = Snapshot(); Key(window, KeyCode.Z, EventModifiers.Control);
            var before = Snapshot();
            Stroke();
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            var painted = Snapshot();
            var g = window.Preview.Geometry; int w = window.Document.Width, h = window.Document.Height, through = 0, held = 0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                if (plain[o + 3] == 0) { Assert.That(painted[o + 3], Is.Zero, what + ": only where the brush reached"); continue; }
                Assert.That(TexelPoint(g, w, h, x, y, out var p), Is.True);
                Assert.That(window.Preview.TryWorldToGui(rect, p, out var gui), Is.True);
                double sx = StencilX(rect, gui);
                if (sx < 30.5) { Assert.That(painted[o + 3], Is.EqualTo(plain[o + 3]), what + ": white lets it through at texel " + x + "," + y); through++; }
                else if (sx > 33.5) { Assert.That(painted[o + 3], Is.Zero, what + ": black holds it back at texel " + x + "," + y); held++; }
            }
            Assert.That(through, Is.GreaterThan(50), what); Assert.That(held, Is.GreaterThan(50), what);
            Key(window, KeyCode.Z, EventModifiers.Control); Assert.That(Snapshot(), Is.EqualTo(before), what + ": one undo");
        }

        [Test] public void In3DTheTexelReadsTheStencilWhereItShowsAndTheStencilStaysOnTheScreenWhenTheCameraTurns()
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            window.View = TexturePaintWindow.ViewMode.Model; HardBrush(90); Repaint(window);
            window.Preview.ViewFrom(25, 15); Repaint(window);
            window.SetStencil(HalfStencil().Id); window.StencilAngle = -20; Repaint(window);
            AssertSurfaceStrokeFollowsTheStencil("first view");
            // カメラを回しても、ステンシルは画面の同じ所（置き場は変わらない）。テクセルは新しい見え方で読む
            var placed = (window.StencilCenter, window.StencilSize, window.StencilAngle);
            window.Preview.ViewFrom(-35, 25); Repaint(window);
            Assert.That((window.StencilCenter, window.StencilSize, window.StencilAngle), Is.EqualTo(placed));
            AssertSurfaceStrokeFollowsTheStencil("after turning the camera");
            // 3D ビューでも T ＋ ドラッグで動かせる（カメラは回らない）
            float yaw = window.Preview.CameraYaw;
            Key(window, KeyCode.T); var c = window.SurfaceRect.center;
            Drag(c, c + new Vector2(60, 0), 2);
            Assert.That(window.StencilCenter.x, Is.EqualTo(placed.Item1.x + 60 / window.SurfaceRect.width).Within(1e-4));
            Drag(c, c + new Vector2(60, 0), 1);
            Assert.That(window.Preview.CameraYaw, Is.EqualTo(yaw), "the right drag resized the stencil instead of orbiting");
            KeyUp(KeyCode.T);
        }

        // ───────── マテリアル・欄・保存 ─────────

        [Test] public void AColourStencilWithMaterialPaintsBaseColourAndTheOtherChannelsThroughItsAlpha()
        {
            window.View = TexturePaintWindow.ViewMode.Canvas; HardBrush(40); Repaint(window);
            // 不透明な青紫・透明が半分ずつ（色の画像なので自動で色）
            var image = AddStencilImage("Colour", 64, 64, (x, y) => x < 32 ? new Rgba32(60, 40, 200, 255) : new Rgba32(60, 40, 200, 0));
            window.SetStencil(image.Id); Repaint(window); // 初めの置き場: 境目が表示域の中心（ダブの中）を通る
            Assert.That(BrushStencil.Resolve(window.StencilModeSetting, window.CurrentStencilImage), Is.EqualTo(StencilMode.Color));
            window.SetMaterialMode(true); window.SetMaterialChannel(PaintChannel.Roughness, true); window.SetMaterialScalar(PaintChannel.Roughness, .8f);
            var at = At(window, 512, 512);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(window.Document.UndoCount, Is.EqualTo(1), window.StatusMessage);
            var layer = window.Document.GetLayer(window.SelectedLayer);
            int painted = 0, held = 0, w = window.Document.Width;
            for (int y = 480; y < 544; y += 3) for (int x = 480; x < 544; x += 3)
            {
                if ((x - 512) * (x - 512) + (y - 512) * (y - 512) > 34 * 34) continue; // ダブ（半径 40）の内側だけ
                double sx = StencilX(window.CanvasRect, window.PixelToGui(x, y));
                var color = layer.GetPixel(PaintChannel.Color, x, y); var rough = layer.GetPixel(PaintChannel.Roughness, x, y);
                if (sx < 30.5) { Assert.That(color, Is.EqualTo(new Rgba32(60, 40, 200, 255)), "Base Color takes the image"); Assert.That(rough, Is.EqualTo(new Rgba32(204, 204, 204, 255)), "Roughness its value"); painted++; }
                else if (sx > 33.5) { Assert.That(color.A, Is.Zero); Assert.That(rough.A, Is.Zero, "transparent holds back every channel"); held++; }
            }
            Assert.That(painted, Is.GreaterThan(20)); Assert.That(held, Is.GreaterThan(20));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(layer.GetPixel(PaintChannel.Color, 500, 512).A, Is.Zero);
        }

        [Test] public void ThePanelTakesADroppedAssetAndTheStencilIsWindowStateThatLeavesWhenItsImageDoes()
        {
            window.View = TexturePaintWindow.ViewMode.Canvas; Repaint(window);
            var half = HalfStencil();
            // ブラシの欄の画像の箱へ、アセットのパネルから落とす
            var p = ToolControlPoint("stencil-image") + window.rootVisualElement.worldBound.position;
            DragAndDrop.PrepareStartDrag(); DragAndDrop.SetGenericData("YoluPainter.Asset", "p:" + half.Id.ToString("D")); DragAndDrop.objectReferences = new UnityEngine.Object[0];
            window.SendEvent(new Event { type = EventType.DragUpdated, mousePosition = p });
            Assert.That(DragAndDrop.visualMode, Is.EqualTo(DragAndDropVisualMode.Copy), "the field accepts the asset");
            window.SendEvent(new Event { type = EventType.DragPerform, mousePosition = p });
            DragAndDrop.SetGenericData("YoluPainter.Asset", null); Repaint(window);
            Assert.That(window.StencilResource, Is.EqualTo(half.Id), window.StatusMessage);
            Assert.That(window.ToolControlScreenRects.Keys, Has.Member("stencil-mode").And.Member("stencil-tiling").And.Member("stencil-reset"));
            // 内蔵の画像（まだこのプロジェクトに無い）は取り込んでから
            p = ToolControlPoint("stencil-image") + window.rootVisualElement.worldBound.position;
            DragAndDrop.PrepareStartDrag(); DragAndDrop.SetGenericData("YoluPainter.Asset", "b:grid"); DragAndDrop.objectReferences = new UnityEngine.Object[0];
            window.SendEvent(new Event { type = EventType.DragUpdated, mousePosition = p }); window.SendEvent(new Event { type = EventType.DragPerform, mousePosition = p });
            DragAndDrop.SetGenericData("YoluPainter.Asset", null); Repaint(window);
            var grid = window.ImageResources.Images.Single(r => r.Origin.BuiltInKey == "grid");
            Assert.That(window.StencilResource, Is.EqualTo(grid.Id));
            // 置き場を変えて「元に戻す」のボタン
            window.StencilAngle = 40; window.StencilSize = 1.5f; Repaint(window);
            ClickToolControl("stencil-reset"); Repaint(window);
            Assert.That(window.StencilAngle, Is.Zero); Assert.That(window.StencilSize, Is.EqualTo(TexturePaintWindow.DefaultStencilSize));
            // 窓の状態: ブラシの設定（brush.json・プリセット）にも文書にも入らず、窓のシリアライズ（リロードをまたぐ）には入る
            Assert.That(JsonUtility.ToJson(window.Brush), Does.Not.Contain("stencil"));
            Assert.That(EditorJsonUtility.ToJson(window), Does.Contain(grid.Id.ToString("D")));
            Assert.That(window.Document.UndoCount, Is.Zero, "choosing and placing the stencil is not an undo step");
            // × で外す
            ClickToolControl("stencil-clear"); Repaint(window);
            Assert.That(window.StencilResource, Is.Null);
            // プロジェクトから画像が消えたら外して知らせる
            window.SetStencil(half.Id); Repaint(window);
            window.ImageResources.Remove(half.Id); Repaint(window);
            Assert.That(window.StencilResource, Is.Null);
            Assert.That(window.StatusMessage, Does.Contain("no longer"));
            // ミップの予算を超える画像は断り、前のステンシルのまま
            window.SetStencil(grid.Id);
            window.StencilMipBudgetBytes = 16;
            Assert.That(() => window.SetStencil(AddStencilImage("Big", 64, 64, (x, y) => StencilWhite).Id), Throws.TypeOf<ResourceRefusedException>());
            Assert.That(window.StencilResource, Is.EqualTo(grid.Id));
        }
    }
}
