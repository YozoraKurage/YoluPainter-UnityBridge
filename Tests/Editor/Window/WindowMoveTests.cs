using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウの移動ツール: ドラッグでの移動（1 回の Undo）、選択範囲の中身だけを動かして選択範囲も動くこと、矢印キー、
    /// Esc でドラッグを残さないこと、数値の回転・反転・拡大縮小、画素を持たない層とゼロ倍率の拒否。入力は SendEvent で本物の経路に通す。</summary>
    public sealed partial class WindowTests
    {
        PaintLayer PaintBlock(int x0, int y0, int x1, int y1, Rgba32 color)
        {
            var d = window.Document; var layer = d.GetLayer(window.SelectedLayer);
            var s = layer.GetChannel(PaintChannel.Color);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) s.SetPixel(x, y, color);
            d.ClearHistory(); return layer;
        }

        [Test] public void DraggingWithTheMoveToolMovesTheLayerAsOneUndoStep()
        {
            var red = new Rgba32(220, 10, 10); var layer = PaintBlock(100, 100, 120, 110, red); var d = window.Document;
            window.Tool = TexturePaintWindow.PaintTool.Move;
            Drag(105, 105, 305, 205);
            Assert.That(window.StatusMessage, Does.Contain("Moved by (200, 100)"));
            Assert.That(layer.GetPixel(PaintChannel.Color, 300, 200), Is.EqualTo(red)); Assert.That(layer.GetPixel(PaintChannel.Color, 319, 209), Is.EqualTo(red));
            Assert.That(layer.GetPixel(PaintChannel.Color, 105, 105).A, Is.EqualTo(0));
            Assert.That(d.UndoCount, Is.EqualTo(1));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(layer.GetPixel(PaintChannel.Color, 105, 105), Is.EqualTo(red)); Assert.That(layer.GetPixel(PaintChannel.Color, 300, 200).A, Is.EqualTo(0));
        }

        [Test] public void MovingInsideASelectionMovesOnlyThosePixelsAndTheSelection()
        {
            var blue = new Rgba32(0, 0, 200); var layer = PaintBlock(100, 100, 200, 200, blue); var d = window.Document;
            d.SetSelection(SelectionMask.Rectangle(d, 100, 100, 150, 200)); d.ClearHistory();
            window.Tool = TexturePaintWindow.PaintTool.Move;
            Drag(120, 150, 520, 150);
            Assert.That(layer.GetPixel(PaintChannel.Color, 120, 150).A, Is.EqualTo(0), "the selected half was lifted");
            Assert.That(layer.GetPixel(PaintChannel.Color, 170, 150), Is.EqualTo(blue), "the other half stays");
            Assert.That(layer.GetPixel(PaintChannel.Color, 520, 150), Is.EqualTo(blue));
            Assert.That(d.Selection[520, 150], Is.EqualTo(255)); Assert.That(d.Selection[120, 150], Is.EqualTo(0), "the selection moved with it");
            Assert.That(d.UndoCount, Is.EqualTo(1));
        }

        [Test] public void ArrowKeysNudgeAndEscapeLeavesNoMoveDrag()
        {
            var green = new Rgba32(0, 180, 0); var layer = PaintBlock(300, 300, 301, 301, green); var d = window.Document;
            window.Tool = TexturePaintWindow.PaintTool.Move;
            Key(window, KeyCode.RightArrow);
            Assert.That(layer.GetPixel(PaintChannel.Color, 301, 300), Is.EqualTo(green));
            Key(window, KeyCode.UpArrow, EventModifiers.Shift);
            Assert.That(layer.GetPixel(PaintChannel.Color, 301, 310), Is.EqualTo(green), "Shift moves 10 px; up is +y (bottom-left origin)");
            Assert.That(d.UndoCount, Is.EqualTo(2), "each nudge is its own step");
            var host = new EditorWindowHost { Window = window };
            MouseWith(host, EventType.MouseDown, At(window, 301, 310), EventModifiers.None);
            MouseWith(host, EventType.MouseDrag, At(window, 600, 600), EventModifiers.None);
            Key(window, KeyCode.Escape);
            MouseWith(host, EventType.MouseUp, At(window, 600, 600), EventModifiers.None);
            Assert.That(layer.GetPixel(PaintChannel.Color, 301, 310), Is.EqualTo(green), "Escape cancelled the drag");
            Assert.That(d.UndoCount, Is.EqualTo(2));
            var empty = d.AddLayer("Empty"); window.SelectedLayer = empty.Id; d.ClearHistory();
            Drag(10, 10, 50, 50);
            Assert.That(window.StatusMessage, Does.Contain("Nothing to move")); Assert.That(d.UndoCount, Is.EqualTo(0));
        }

        [Test] public void CornerHandlesScaleAndDraggingOutsideACornerRotates()
        {
            var red = new Rgba32(250, 0, 0); var grey = new Rgba32(90, 90, 90);
            var layer = PaintBlock(300, 300, 500, 500, grey); var d = window.Document;
            layer.GetChannel(PaintChannel.Color).SetPixel(300, 300, red); d.ClearHistory();
            window.Tool = TexturePaintWindow.PaintTool.Move; window.MoveResampling = Resampling.Nearest;
            Drag(500, 500, 700, 700); // 右上の角のハンドル（左下が軸）
            Assert.That(window.StatusMessage, Does.Contain("Scaled"));
            var b = d.TransformBounds(layer.Id).Value;
            Assert.That(b.x0, Is.EqualTo(300)); Assert.That(b.y0, Is.EqualTo(300));
            Assert.That(b.x1, Is.InRange(699, 702)); Assert.That(b.y1, Is.InRange(699, 702), "twice as large about the opposite corner");
            Assert.That(layer.GetPixel(PaintChannel.Color, 301, 301), Is.EqualTo(red), "nearest keeps hard pixels");
            Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo();
            Drag(500, 400, 600, 400); // 右の辺の中点: 横だけ
            b = d.TransformBounds(layer.Id).Value;
            Assert.That(b.x1, Is.InRange(599, 602)); Assert.That((b.y0, b.y1), Is.EqualTo((300, 500)), "an edge handle scales one axis");
            d.Undo();
            // 右上の角の少し外を掴み、中心のまわりに 90° 回す（Shift で 15° 刻み）。「少し外」は画面の点で決まる（角のハンドルの
            // HandleHitPoints の外、RotateReachPoints の内）ので、今の表示の倍率で画素の数を選ぶ（既定の配置で表示の幅が変わっても同じ所）
            Repaint(window);
            int k = 1;
            while (k < 200 && Vector2.Distance(window.PixelToGui(500 + k, 500 + k), window.PixelToGui(500, 500)) <= TexturePaintWindow.HandleHitPoints + 2) k++;
            Assert.That(Vector2.Distance(window.PixelToGui(500 + k, 500 + k), window.PixelToGui(500, 500)), Is.LessThan(TexturePaintWindow.RotateReachPoints - 2), "the grab point is in the rotate ring");
            Drag(500 + k, 500 + k, 300 - k, 500 + k, EventModifiers.Shift);
            Assert.That(window.StatusMessage, Does.Contain("Rotated 90"));
            Assert.That(layer.GetPixel(PaintChannel.Color, 499, 300), Is.EqualTo(red), "bottom-left goes to bottom-right, pixel-exact");
            Assert.That(d.TransformBounds(layer.Id).Value, Is.EqualTo((300, 300, 500, 500)));
            d.Undo();
            Drag(400, 400, 450, 420);
            Assert.That(window.StatusMessage, Does.Contain("Moved by (50, 20)"), "inside the box moves");
        }

        [Test] public void NumericTransformsRotateFlipAndScaleAboutTheContent()
        {
            var layer = PaintBlock(100, 100, 104, 102, new Rgba32(10, 20, 30)); var d = window.Document;
            layer.GetChannel(PaintChannel.Color).SetPixel(100, 100, new Rgba32(250, 0, 0)); d.ClearHistory();
            window.Tool = TexturePaintWindow.PaintTool.Move;
            window.TransformSelected(0, 0, 90, 1, 1, "Rotated.");
            // 4×2 の塊が中心の画素の格子で 90° 回り、2×4 になる。角の赤は (左下 → 右下) へ
            var bounds = d.TransformBounds(layer.Id).Value;
            Assert.That((bounds.x1 - bounds.x0, bounds.y1 - bounds.y0), Is.EqualTo((2, 4)), "a quarter turn copies whole pixels");
            Assert.That(layer.GetPixel(PaintChannel.Color, bounds.x1 - 1, bounds.y0), Is.EqualTo(new Rgba32(250, 0, 0)));
            d.Undo();
            window.TransformSelected(0, 0, 0, -1, 1, "Flipped.");
            Assert.That(layer.GetPixel(PaintChannel.Color, 103, 100), Is.EqualTo(new Rgba32(250, 0, 0)), "a flip stays in place");
            d.Undo();
            window.MoveScale = new Vector2(200, 200); window.MoveOffset = new Vector2(50, 0); window.MoveResampling = Resampling.Nearest;
            window.ApplyNumericTransform();
            bounds = d.TransformBounds(layer.Id).Value;
            Assert.That((bounds.x1 - bounds.x0, bounds.y1 - bounds.y0), Is.EqualTo((8, 4)));
            Assert.That(bounds.x0, Is.EqualTo(148), "scaled about the centre (102, 101), then moved 50 px");
            Assert.That(window.MoveScale, Is.EqualTo(new Vector2(100, 100)), "the fields reset after applying");
            window.MoveScale = new Vector2(0, 100);
            Assert.That(() => window.ApplyNumericTransform(), Throws.InvalidOperationException);
            var fill = d.AddFillLayer("F"); window.SelectedLayer = fill.Id;
            Assert.That(() => window.MoveBy(1, 0), Throws.InvalidOperationException.With.Message.Contains("paint layers"));
        }
    }
}
