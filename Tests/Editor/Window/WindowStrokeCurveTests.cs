using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>速く描いたときの線: ウィンドウはドラッグの点の間を曲線で結び（2D は Core のストローク、3D ビューは画面の上で）、
    /// 最後の区間は離したときに描く。ドラッグのイベントでは合成せず、表示の合成は Repaint のときだけ。3D ビューの 1 回の入力の
    /// ダブの上限（超えたらストロークを取り消す）は曲線でも同じ。</summary>
    public sealed partial class WindowTests
    {
        /// <summary>キャンバスの点（画素の座標、左下が原点）の GUI 座標（PixelToGui は画素の中心だけなので、2 点から写す）。</summary>
        Vector2 CanvasToGui(double x, double y)
        {
            Repaint(window);
            Vector2 g0 = window.PixelToGui(0, 0), g1 = window.PixelToGui(1, 1);
            return new Vector2((float)(g0.x + (x - .5) * (g1.x - g0.x)), (float)(g0.y + (y - .5) * (g1.y - g0.y)));
        }

        /// <summary>塗った画素の濃さの重心（キャンバスの画素の座標）。</summary>
        static (double x, double y) Centroid(byte[] rgba, int width, int height)
        {
            double sw = 0, sx = 0, sy = 0;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            { double w = rgba[(y * width + x) * 4 + 3]; sw += w; sx += w * (x + .5); sy += w * (y + .5); }
            return (sx / sw, sy / sw);
        }

        void SoftRoundBrush()
        {
            var b = window.Brush; b.radius = 4; b.hardness = 0; b.spacing = .05f; b.pressureSize = false; b.pressureOpacity = false;
            b.stabilizer = 0; b.taperIn = 0; b.taperOut = 0; window.Brush = b;
        }

        [Test] public void FastDragsAreJoinedByACurveAndTheLastSegmentIsDrawnOnRelease()
        {
            SoftRoundBrush();
            Assert.That(window.GetBrush().CurveInterpolation, Is.True, "the window always joins the points with the curve");
            var d = window.Document; var before = Snapshot();
            const double cx = 512.3, cy = 511.7, r = 200; const int n = 8;
            // GUI の座標とキャンバスの画素の写しの一様なずれ（タブの高さなど）を、1 つのダブの重心で測っておく
            var dot = CanvasToGui(cx, cy);
            Mouse(window, EventType.MouseDown, dot); Mouse(window, EventType.MouseUp, dot);
            var (mx, my) = Centroid(Snapshot(), d.Width, d.Height);
            double ox = mx - cx, oy = my - cy;
            Assert.That(Math.Sqrt(ox * ox + oy * oy), Is.LessThan(2), "a dab lands under the pointer");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(Snapshot(), Is.EqualTo(before));

            var points = Enumerable.Range(0, n * 3 / 4 + 1).Select(i => CanvasToGui(cx + r * Math.Cos(2 * Math.PI * i / n), cy + r * Math.Sin(2 * Math.PI * i / n))).ToArray();
            Mouse(window, EventType.MouseDown, points[0]);
            for (int i = 1; i < points.Length; i++) Mouse(window, EventType.MouseDrag, points[i]);
            Assert.That(window.IsStroking, Is.True);
            int lastX = (int)(mx + r * Math.Cos(2 * Math.PI * (n * 3 / 4) / n)), lastY = (int)(my + r * Math.Sin(2 * Math.PI * (n * 3 / 4) / n));
            Assert.That(d.CompositePixel(PaintChannel.Color, lastX, lastY).A, Is.Zero, "the segment to the newest point waits for the next point");
            Mouse(window, EventType.MouseUp, points[points.Length - 1]);
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            Assert.That(d.CompositePixel(PaintChannel.Color, lastX, lastY).A, Is.GreaterThan(0), "release draws the last segment");
            // 8 等分の点を直線で結ぶと、弦の中ほどで線は円の 15.2 px 内側を通る。曲線のずれは半径に比例し、半径 200 px・8 等分では
            // 式の上で 1.69 px（Core の StrokeCurveTests で半径 40 px なら 0.34 px）。画素で測る揺れ（0.6 px ほど）を足して 3 px 未満。
            var line = StrokeCurveTests.CenterLine(Snapshot(), d.Width, d.Height, mx, my, r, n);
            Assert.That(line.deviation, Is.LessThan(3.0), "the line follows the circle through the sparse drag points (" + line.deviation.ToString("0.000") + " px; offset "
                + ox.ToString("0.00") + ", " + oy.ToString("0.00") + " px)");
            Assert.That(d.UndoCount, Is.EqualTo(1));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(Snapshot(), Is.EqualTo(before), "one undo removes the whole curved stroke");
        }

        [Test] public void DragsAreCompositedOnlyWhenTheWindowRepaints()
        {
            SoftRoundBrush();
            var points = Enumerable.Range(0, 21).Select(i => CanvasToGui(200 + i * 12, 300 + 40 * Math.Sin(i * .4))).ToArray();
            Repaint(window);
            int composites = window.CompositeCount;
            Mouse(window, EventType.MouseDown, points[0]);
            for (int i = 1; i < points.Length; i++) Mouse(window, EventType.MouseDrag, points[i]);
            Assert.That(window.CompositeCount, Is.EqualTo(composites), "input events do not composite (each would make the next drag event wait)");
            Repaint(window);
            Assert.That(window.CompositeCount, Is.EqualTo(composites + 1), "the repaint composites every change since the last one at once");
            Repaint(window);
            Assert.That(window.CompositeCount, Is.EqualTo(composites + 1), "nothing changed, nothing to composite");
            Mouse(window, EventType.MouseUp, points[points.Length - 1]);
            Assert.That(window.CompositeCount, Is.EqualTo(composites + 1));
            Repaint(window);
            Assert.That(window.CompositeCount, Is.EqualTo(composites + 2));
            Assert.That(window.Document.CanUndo, Is.True);
        }

        [Test] public void SurfaceStrokesFollowTheCurveAndKeepThePerEventBudget()
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            Repaint(window);
            var center = window.SurfaceRect.center; var d = window.Document;
            var before = Snapshot();
            Mouse(window, EventType.MouseDown, center);
            Mouse(window, EventType.MouseDrag, center + new Vector2(8, 0));
            Mouse(window, EventType.MouseDrag, center + new Vector2(14, 6));
            Mouse(window, EventType.MouseUp, center + new Vector2(14, 6));
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            Assert.That(d.UndoCount, Is.EqualTo(1), window.StatusMessage);
            Assert.That(Snapshot(), Is.Not.EqualTo(before), "the surface stroke painted");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(Snapshot(), Is.EqualTo(before));

            // 小さい筆（間隔は画面の 0.5 点）で 100 点を 1 区間で飛ぶと 128 個を超える: 曲線を描く入力で断り、何も残さない
            var b = window.Brush; b.radius = .5f; window.Brush = b;
            int undo = d.UndoCount;
            Mouse(window, EventType.MouseDown, center);
            Mouse(window, EventType.MouseDrag, center + new Vector2(100, 0));
            Assert.That(window.IsStroking, Is.True, "the far point waits for the next one");
            Mouse(window, EventType.MouseDrag, center + new Vector2(100, 30));
            Assert.That(window.IsStroking, Is.False); Assert.That(window.StatusMessage, Does.Contain("budget"));
            Assert.That(Snapshot(), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.EqualTo(undo));
            Mouse(window, EventType.MouseUp, center + new Vector2(100, 30)); // 取り消した後の離しは何も起こさない
            Assert.That(Snapshot(), Is.EqualTo(before));

            // 離したときに描く最後の区間も同じ上限で断る
            Mouse(window, EventType.MouseDown, center);
            Mouse(window, EventType.MouseDrag, center + new Vector2(100, 0));
            Mouse(window, EventType.MouseUp, center + new Vector2(100, 0));
            Assert.That(window.IsStroking, Is.False); Assert.That(window.StatusMessage, Does.Contain("budget"));
            Assert.That(Snapshot(), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.EqualTo(undo));
        }
    }
}
