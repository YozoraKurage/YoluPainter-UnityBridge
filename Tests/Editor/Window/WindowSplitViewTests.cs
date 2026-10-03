using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>並べる表示の境目のドラッグと入れ替え（GUI の窓に SendEvent で入力を送る）。</summary>
    public sealed partial class WindowTests
    {
        void SplitMouse(EventType type, Vector2 at, int clicks = 1)
            => window.SendEvent(new Event { type = type, button = 0, clickCount = clicks, mousePosition = at + window.rootVisualElement.worldBound.position });

        /// <summary>境目をドラッグすると 2D キャンバスの幅が変わり、ストロークにはならない。端まで寄せても 15〜85% に収まり、ダブルクリックで半分に戻る。
        /// 入れ替えた表示では、右へドラッグすると左の 3D ビューが広がる。</summary>
        [Test] public void DraggingTheDividerResizesTheViewsWithoutPainting()
        {
            window.View = TexturePaintWindow.ViewMode.Split; window.SplitRatio = .5f; Repaint(window);
            float before = window.CanvasRect.width; var start = window.SplitHandleRect.center;
            SplitMouse(EventType.MouseDown, start); SplitMouse(EventType.MouseDrag, start + new Vector2(120, 0)); SplitMouse(EventType.MouseUp, start + new Vector2(120, 0));
            Repaint(window);
            Assert.That(window.CanvasRect.width - before, Is.EqualTo(120).Within(2), "the canvas follows the pointer");
            Assert.That(window.IsStroking, Is.False); Assert.That(window.Document.CanUndo, Is.False, "the divider does not paint");
            start = window.SplitHandleRect.center;
            SplitMouse(EventType.MouseDown, start); SplitMouse(EventType.MouseDrag, new Vector2(window.CanvasRect.x - 500, start.y)); SplitMouse(EventType.MouseUp, start);
            Assert.That(window.SplitRatio, Is.EqualTo(.15f), "the canvas keeps at least 15%");
            Repaint(window); SplitMouse(EventType.MouseDown, window.SplitHandleRect.center, 2); SplitMouse(EventType.MouseUp, window.SplitHandleRect.center, 2);
            Assert.That(window.SplitRatio, Is.EqualTo(.5f), "a double click halves the area");
            window.ViewsSwapped = true; Repaint(window);
            float surfaceBefore = window.SurfaceRect.width; start = window.SplitHandleRect.center;
            SplitMouse(EventType.MouseDown, start); SplitMouse(EventType.MouseDrag, start + new Vector2(80, 0)); SplitMouse(EventType.MouseUp, start + new Vector2(80, 0));
            Repaint(window);
            Assert.That(window.SurfaceRect.width - surfaceBefore, Is.EqualTo(80).Within(2), "swapped: dragging right widens the 3D view on the left");
            Assert.That(window.SplitRatio, Is.LessThan(.5f), "the ratio is the canvas's share");
        }

        /// <summary>入れ替えた表示（2D キャンバスが右）でも、点はポインタの下の画素に入る。境目を押している間に始めたストロークは無い。</summary>
        [Test] public void StrokesLandUnderThePointerWhenTheViewsAreSwapped()
        {
            window.View = TexturePaintWindow.ViewMode.Split; window.ViewsSwapped = true; Repaint(window);
            Assert.That(window.CanvasRect.x, Is.GreaterThan(window.SurfaceRect.x), "the canvas is on the right");
            var at = At(window, 300, 700);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(window.Document.CompositePixel(PaintChannel.Color, 300, 700).A, Is.GreaterThan((byte)0));
            Assert.That(window.Document.UndoCount, Is.EqualTo(1));
        }
    }
}
