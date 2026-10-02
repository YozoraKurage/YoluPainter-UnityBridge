using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウのツール: バケツ、グラデーション、矩形/楕円/投げ縄の選択とその組み合わせ、選択範囲の外に描かないこと、
    /// Esc とフォーカス喪失でドラッグを残さないこと、ショートカット。入力は SendEvent で本物の経路に通す。</summary>
    public sealed partial class WindowTests
    {
        static void MouseWith(EditorWindowHost w, EventType type, Vector2 at, EventModifiers modifiers)
        { EditorShaderCompiler.TolerateErrorLogsIfBroken(); w.Window.SendEvent(new Event { type = type, mousePosition = at + w.Window.rootVisualElement.worldBound.position, button = 0, pressure = 1, modifiers = modifiers }); }
        sealed class EditorWindowHost { public TexturePaintWindow Window; }
        void Drag(int x0, int y0, int x1, int y1, EventModifiers modifiers = EventModifiers.None)
        {
            var host = new EditorWindowHost { Window = window };
            MouseWith(host, EventType.MouseDown, At(window, x0, y0), modifiers);
            MouseWith(host, EventType.MouseDrag, At(window, (x0 + x1) / 2, (y0 + y1) / 2), modifiers);
            MouseWith(host, EventType.MouseDrag, At(window, x1, y1), modifiers);
            MouseWith(host, EventType.MouseUp, At(window, x1, y1), modifiers);
        }

        [Test] public void TheBucketFillsTheConnectedAreaAsOneUndoStep()
        {
            var d = window.Document; var layer = d.Layers[d.Layers.Count - 1];
            var s = layer.GetChannel(PaintChannel.Color);
            for (int y = 0; y < d.Height; y++) s.SetPixel(512, y, new Rgba32(0, 0, 0)); // 縦の線で左右に分ける
            d.ClearHistory();
            window.Tool = TexturePaintWindow.PaintTool.Fill; window.WandTolerance = 0;
            var at = At(window, 200, 200); Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(window.StatusMessage, Does.Contain("Filled"));
            Assert.That(d.CompositePixel(PaintChannel.Color, 100, 900).A, Is.EqualTo(255), "the whole left side");
            Assert.That(d.CompositePixel(PaintChannel.Color, 800, 900).A, Is.EqualTo(0), "not across the line");
            Assert.That(d.UndoCount, Is.EqualTo(1));
            d.Undo(); Assert.That(d.CompositePixel(PaintChannel.Color, 100, 900).A, Is.EqualTo(0));
        }

        [Test] public void SelectionsLimitTheBrushAndCombineWithModifiers()
        {
            var d = window.Document;
            window.Tool = TexturePaintWindow.PaintTool.SelectRectangle;
            Drag(100, 100, 300, 300);
            Assert.That(d.Selection, Is.Not.Null, window.StatusMessage);
            Assert.That(d.Selection[200, 200], Is.EqualTo(255)); Assert.That(d.Selection[600, 600], Is.EqualTo(0));
            window.Tool = TexturePaintWindow.PaintTool.SelectEllipse;
            Drag(500, 500, 700, 700, EventModifiers.Shift);
            Assert.That(d.Selection[600, 600], Is.EqualTo(255), "Shift adds"); Assert.That(d.Selection[200, 200], Is.EqualTo(255));
            window.Tool = TexturePaintWindow.PaintTool.Lasso;
            var host = new EditorWindowHost { Window = window };
            MouseWith(host, EventType.MouseDown, At(window, 150, 150), EventModifiers.Control);
            foreach (var (x, y) in new[] { (250, 150), (250, 250), (150, 250) }) MouseWith(host, EventType.MouseDrag, At(window, x, y), EventModifiers.Control);
            MouseWith(host, EventType.MouseUp, At(window, 150, 250), EventModifiers.Control);
            Assert.That(d.Selection[200, 200], Is.EqualTo(0), "Ctrl subtracts the lasso"); Assert.That(d.Selection[120, 120], Is.EqualTo(255));
            // ブラシは選択範囲の外を塗らない
            window.Tool = TexturePaintWindow.PaintTool.Brush;
            var outside = At(window, 900, 900); Mouse(window, EventType.MouseDown, outside); Mouse(window, EventType.MouseUp, outside);
            Assert.That(d.CompositePixel(PaintChannel.Color, 900, 900).A, Is.EqualTo(0));
            PaintDot(window, 120, 120);
            Assert.That(d.CompositePixel(PaintChannel.Color, 120, 120).A, Is.GreaterThan(0));
            Key(window, KeyCode.D, EventModifiers.Control);
            Assert.That(d.Selection, Is.Null, "Ctrl+D deselects");
            Key(window, KeyCode.A, EventModifiers.Control);
            Assert.That(d.Selection[5, 5], Is.EqualTo(255), "Ctrl+A selects all");
            Key(window, KeyCode.I, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(d.Selection, Is.Null, "inverting everything leaves nothing selected");
            window.Tool = TexturePaintWindow.PaintTool.SelectRectangle;
            Drag(100, 100, 300, 300); var click = At(window, 50, 50);
            Mouse(window, EventType.MouseDown, click); Mouse(window, EventType.MouseUp, click);
            Assert.That(d.Selection, Is.Null, "a click without dragging deselects");
        }

        [Test] public void AGradientDragPaintsAndEscapeOrFocusLossCancelsADrag()
        {
            var d = window.Document; var layer = d.Layers[d.Layers.Count - 1];
            window.Tool = TexturePaintWindow.PaintTool.Gradient; window.GradientTo = new Color(0, 0, 1, 1);
            Drag(0, 500, 1000, 500);
            Assert.That(window.StatusMessage, Does.Contain("Gradient"));
            Assert.That(d.CompositePixel(PaintChannel.Color, 1020, 10).B, Is.GreaterThan(240), "the end colour past the end");
            Assert.That(d.UndoCount, Is.GreaterThan(0));
            int steps = d.UndoCount;
            var host = new EditorWindowHost { Window = window };
            MouseWith(host, EventType.MouseDown, At(window, 100, 100), EventModifiers.None);
            MouseWith(host, EventType.MouseDrag, At(window, 400, 100), EventModifiers.None);
            Key(window, KeyCode.Escape);
            MouseWith(host, EventType.MouseUp, At(window, 400, 100), EventModifiers.None);
            Assert.That(d.UndoCount, Is.EqualTo(steps), "Escape cancels the gradient drag");
            MouseWith(host, EventType.MouseDown, At(window, 100, 100), EventModifiers.None);
            MouseWith(host, EventType.MouseDrag, At(window, 400, 100), EventModifiers.None);
            Invoke(window, "OnLostFocus");
            MouseWith(host, EventType.MouseUp, At(window, 400, 100), EventModifiers.None);
            Assert.That(d.UndoCount, Is.EqualTo(steps), "losing focus cancels it too");
        }
    }
}
