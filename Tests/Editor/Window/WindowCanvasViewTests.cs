using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>2D キャンバスの表示の回転と左右反転（GUI の窓に SendEvent で入力を送る）: キーと R ＋ ドラッグ・Shift ＋ 中ボタンのドラッグで
    /// 表示の中心のまわりに回ること、ストロークやドラッグの最中は変えないこと、回った表示で描いた線・ツールの当たり判定が回っていない表示の
    /// 同じ画素と同じこと、ホイールの拡大でポインタの下の画素が動かないこと。</summary>
    public sealed partial class WindowTests
    {
        /// <summary>キャンバスの点（画素の座標、左下が原点）の今の表示での GUI 座標（直前に Repaint してレイアウトを最新にする）。</summary>
        Vector2 ViewGui(double x, double y) { Repaint(window); return window.CanvasViewNow().ToGui(x, y); }
        Vector2 ViewCanvas(Vector2 gui) { Repaint(window); return window.CanvasViewNow().ToCanvas(gui); }
        Vector2 ViewCentre() { Repaint(window); return window.CanvasRect.center; }
        /// <summary>2D だけの表示にする（回した画像やドラッグの点が表示域に収まるように）。</summary>
        void CanvasOnly() { window.View = TexturePaintWindow.ViewMode.Canvas; Repaint(window); }
        void SetView(float angle, bool flip, float zoom = 1, Vector2? pan = null)
        {
            Key(window, KeyCode.Alpha0, EventModifiers.Control); // 拡大 100%・パンなし・回転 0（反転はそのまま）
            if (window.CanvasFlipped != flip) Assert.That(window.FlipCanvasView(), Is.True);
            if (zoom != 1) window.ZoomCanvasView(zoom);
            if (pan.HasValue) MiddleDrag(ViewCentre(), pan.Value, EventModifiers.None);
            if (angle != 0) Assert.That(window.RotateCanvasView(angle), Is.True);
            Assert.That((window.CanvasAngle, window.CanvasFlipped), Is.EqualTo((CanvasView.NormalizeAngle(angle), flip)));
        }
        void MiddleDrag(Vector2 to, Vector2 delta, EventModifiers modifiers)
        { window.SendEvent(new Event { type = EventType.MouseDrag, button = 2, mousePosition = to + window.rootVisualElement.worldBound.position, delta = delta, modifiers = modifiers }); }
        void KeyChar(char c) => window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.None, character = c });
        void KeyUp(KeyCode key) => window.SendEvent(new Event { type = EventType.KeyUp, keyCode = key });
        void AssertCentreStays(Vector2 before, string why)
        { var now = ViewCanvas(ViewCentre()); Assert.That(Vector2.Distance(now, before), Is.LessThan(.01f), why + ": the pixel at the centre of the view moved from " + before + " to " + now); }

        [Test] public void KeysRotateAndFlipTheViewAboutItsCentre()
        {
            CanvasOnly();
            MiddleDrag(ViewCentre(), new Vector2(40, -25), EventModifiers.None); // パンしておく（回すのは画像の中心ではなく表示の中心のまわり）
            var centre = ViewCanvas(ViewCentre());
            Key(window, KeyCode.Minus);
            Assert.That(window.CanvasAngle, Is.EqualTo(-15f)); AssertCentreStays(centre, "-");
            KeyChar('^');
            Assert.That(window.CanvasAngle, Is.EqualTo(0f), "^ (by its character, as on a JIS keyboard)");
            Key(window, KeyCode.Equals);
            Assert.That(window.CanvasAngle, Is.EqualTo(15f), "= (US keyboards)");
            // キーのコードのイベントの後に同じキーの文字だけのイベントが来ても、二度は回さない
            Key(window, KeyCode.Minus); KeyChar('-');
            Assert.That(window.CanvasAngle, Is.EqualTo(0f));
            for (int i = 0; i < 13; i++) Key(window, KeyCode.Equals);
            Assert.That(window.CanvasAngle, Is.EqualTo(-165f), "the angle stays in (-180, 180]");
            AssertCentreStays(centre, "13 steps");
            window.RotateCanvasView(195);
            Assert.That(window.CanvasAngle, Is.EqualTo(30f));
            Key(window, KeyCode.H);
            Assert.That((window.CanvasAngle, window.CanvasFlipped), Is.EqualTo((-30f, true)), "H mirrors what is shown: the tilt reverses");
            AssertCentreStays(centre, "H");
            Key(window, KeyCode.R, EventModifiers.Shift);
            Assert.That((window.CanvasAngle, window.CanvasFlipped), Is.EqualTo((0f, true)), "Shift+R resets only the rotation");
            AssertCentreStays(centre, "Shift+R");
            window.RotateCanvasView(90); window.ZoomCanvasView(2);
            Key(window, KeyCode.Alpha0, EventModifiers.Control);
            Assert.That((window.CanvasAngle, window.CanvasZoom, window.CanvasPan, window.CanvasFlipped), Is.EqualTo((0f, 1f, Vector2.zero, true)), "Ctrl+0 fits and resets the rotation, not the flip");
            Key(window, KeyCode.H);
            Assert.That(window.CanvasFlipped, Is.False);
            Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.Brush), "no view key switched the tool");
            Assert.That(window.Document.CanUndo, Is.False, "the view is not part of the document");
            Key(window, KeyCode.R);
            Assert.That(window.RotateKeyHeld, Is.True); KeyUp(KeyCode.R); Assert.That(window.RotateKeyHeld, Is.False);
        }

        /// <summary>Ctrl++ / Ctrl+- が表示の中心を軸に拡大・縮小する（前はメニューに書いてあるだけでキーとしては効かなかった）。+ は US の = のキー・
        /// JIS の ; のキー（Shift で +）・テンキーで受け、Ctrl 付きの - と = は表示を回さない。ストロークの最中は効かない。</summary>
        [Test] public void CtrlPlusAndCtrlMinusZoomAboutTheCentre()
        {
            CanvasOnly();
            MiddleDrag(ViewCentre(), new Vector2(40, -25), EventModifiers.None);
            var centre = ViewCanvas(ViewCentre());
            Key(window, KeyCode.Equals, EventModifiers.Control);
            Assert.That(window.CanvasZoom, Is.EqualTo(1.25f).Within(1e-5f), "Ctrl+=");
            AssertCentreStays(centre, "Ctrl+=");
            Key(window, KeyCode.Semicolon, EventModifiers.Control | EventModifiers.Shift);
            Key(window, KeyCode.KeypadPlus, EventModifiers.Control);
            Assert.That(window.CanvasZoom, Is.EqualTo(1.25f * 1.25f * 1.25f).Within(1e-4f), "Ctrl+; (JIS +) and the keypad +");
            Key(window, KeyCode.Minus, EventModifiers.Control); Key(window, KeyCode.KeypadMinus, EventModifiers.Control);
            Assert.That(window.CanvasZoom, Is.EqualTo(1.25f).Within(1e-4f), "Ctrl+- twice");
            AssertCentreStays(centre, "Ctrl+-");
            Assert.That(window.CanvasAngle, Is.EqualTo(0f), "Ctrl with - or = does not rotate the view");
            Assert.That(window.Document.CanUndo, Is.False, "the view is not part of the document");
            BeginLine(400, 400);
            Key(window, KeyCode.Equals, EventModifiers.Control);
            Assert.That(window.CanvasZoom, Is.EqualTo(1.25f).Within(1e-4f), "not during a stroke");
            Mouse(window, EventType.MouseUp, At(window, 460, 400));
        }

        [Test] public void TheViewDoesNotTurnDuringAStrokeOrADrag()
        {
            BeginLine(400, 400);
            Key(window, KeyCode.Minus); Key(window, KeyCode.H); KeyChar('^'); Key(window, KeyCode.R, EventModifiers.Shift);
            Assert.That((window.CanvasAngle, window.CanvasFlipped), Is.EqualTo((0f, false)));
            Assert.That(window.StatusMessage, Does.Contain("does not rotate or flip"));
            MiddleDrag(ViewCentre() + new Vector2(80, 0), new Vector2(0, 30), EventModifiers.Shift);
            Assert.That(window.CanvasAngle, Is.EqualTo(0f), "Shift + middle drag is refused too");
            Assert.That(window.RotateCanvasView(15), Is.False); Assert.That(window.FlipCanvasView(), Is.False); Assert.That(window.FitCanvasView(), Is.False);
            Assert.That(window.IsStroking, Is.True, "the stroke goes on");
            Mouse(window, EventType.MouseUp, At(window, 460, 400));
            Assert.That(window.Document.UndoCount, Is.EqualTo(1));
            // 矩形選択のドラッグの最中も
            window.Tool = TexturePaintWindow.PaintTool.SelectRectangle;
            var host = new EditorWindowHost { Window = window };
            MouseWith(host, EventType.MouseDown, At(window, 100, 100), EventModifiers.None);
            MouseWith(host, EventType.MouseDrag, At(window, 200, 200), EventModifiers.None);
            Key(window, KeyCode.Equals);
            Assert.That(window.CanvasAngle, Is.EqualTo(0f));
            MouseWith(host, EventType.MouseUp, At(window, 200, 200), EventModifiers.None);
            Assert.That(window.Document.Selection, Is.Not.Null);
            Key(window, KeyCode.Equals);
            Assert.That(window.CanvasAngle, Is.EqualTo(15f), "after the drag the key works");
        }

        /// <summary>表示が回っていても描いた線は回らない: 同じ画素の点を通るストローク（手ぶれ補正・入り抜き・曲線の補間あり）が、回していない表示と
        /// 同じ画素になる。GUI の座標は float なので、画素の座標は 1e-4 画素ほど揺れ、柔らかい筆の縁で 1 段ずれる画素がわずかに出る（バイト一致は
        /// 言わない）。</summary>
        [Test] public void ARotatedViewPaintsTheSameLineAsAStraightView()
        {
            CanvasOnly();
            var b = window.Brush; b.radius = 6; b.hardness = .3f; b.spacing = .1f; b.pressureSize = false; b.pressureOpacity = false;
            b.stabilizer = 24; b.taperIn = 40; b.taperOut = 60; window.Brush = b;
            var points = Enumerable.Range(0, 25).Select(i => (x: 250 + i * 21.7, y: 470 + 90 * Math.Sin(i * .37))).ToArray();
            byte[] Draw()
            {
                Mouse(window, EventType.MouseDown, ViewGui(points[0].x, points[0].y));
                for (int i = 1; i < points.Length; i++) Mouse(window, EventType.MouseDrag, ViewGui(points[i].x, points[i].y));
                Mouse(window, EventType.MouseUp, ViewGui(points[points.Length - 1].x, points[points.Length - 1].y));
                Assert.That(window.IsStroking, Is.False, window.StatusMessage);
                var result = Snapshot(); window.Document.Undo(); return result;
            }
            var straight = Draw();
            int painted = 0; for (int i = 3; i < straight.Length; i += 4) if (straight[i] > 0) painted++;
            Assert.That(painted, Is.GreaterThan(5000), "the line was drawn");
            foreach (var (angle, flip, zoom) in new[] { (90f, false, 1f), (180f, true, 1f), (-37f, true, 1.25f), (15f, false, .6f) })
            {
                SetView(angle, flip, zoom, new Vector2(30, -20));
                var turned = Draw();
                // 透けた画素の色は比べない（アルファ 0 と 1 の違いで RGB は 0 と筆の色に跳ぶ）: プリマルチプライドの色とアルファで比べる
                int differing = 0, maxDiff = 0;
                for (int p = 0; p < straight.Length; p += 4)
                {
                    int diff = Math.Abs(straight[p + 3] - turned[p + 3]);
                    for (int c = 0; c < 3; c++) diff = Math.Max(diff, Math.Abs(straight[p + c] * straight[p + 3] / 255 - turned[p + c] * turned[p + 3] / 255));
                    if (diff > 0) differing++; if (diff > maxDiff) maxDiff = diff;
                }
                string what = angle + "° flip " + flip + " zoom " + zoom + ": " + differing + " pixel(s) differ, at most by " + maxDiff + " (" + painted + " painted pixels)";
                Debug.Log("Rotated line: " + what);
                // 実測（2026-10-03、GUI モード）: 4 つの表示で 6〜12 画素が 1 段だけ違う（7685 画素のうち）
                Assert.That(maxDiff, Is.LessThanOrEqualTo(1), what);
                Assert.That(differing, Is.LessThan(painted / 200), what);
            }
        }

        [Test] public void ToolsHitTheSamePixelsInARotatedView()
        {
            CanvasOnly(); var d = window.Document;
            SetView(90, true, 1.2f, new Vector2(-25, 15));
            // 矩形選択: キャンバスの軸に沿った同じ矩形（画面では回った矩形）
            window.Tool = TexturePaintWindow.PaintTool.SelectRectangle;
            Drag(100, 120, 300, 220);
            Assert.That(d.Selection, Is.Not.Null);
            Assert.That((d.Selection[150, 150], d.Selection[299, 219], d.Selection[100, 120]), Is.EqualTo(((byte)255, (byte)255, (byte)255)));
            Assert.That((d.Selection[310, 150], d.Selection[150, 230], d.Selection[90, 150]), Is.EqualTo(((byte)0, (byte)0, (byte)0)));
            d.ClearSelection();
            // スポイト
            var red = new Rgba32(230, 20, 30); PaintBlock(600, 600, 620, 620, red);
            window.Tool = TexturePaintWindow.PaintTool.Eyedropper;
            var at = At(window, 610, 610); Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That((Color32)window.Brush.color, Is.EqualTo(new Color32(230, 20, 30, 255)), window.StatusMessage);
            // 移動ツール: 角のハンドルで拡大、枠の中で移動、角の外で回転（どれも画素の座標で、回っていない表示と同じ結果）
            window.SelectedLayer = d.AddLayer("Move").Id; d.ClearHistory();
            var grey = new Rgba32(90, 90, 90); var layer = PaintBlock(300, 300, 500, 500, grey);
            layer.GetChannel(PaintChannel.Color).SetPixel(300, 300, red); d.ClearHistory(); // 90° 回しても同じにならないように角に印
            window.Tool = TexturePaintWindow.PaintTool.Move; window.MoveResampling = Resampling.Nearest;
            Drag(500, 500, 700, 700);
            Assert.That(window.StatusMessage, Does.Contain("Scaled"));
            var bounds = d.TransformBounds(layer.Id).Value;
            Assert.That((bounds.x0, bounds.y0), Is.EqualTo((300, 300))); Assert.That(bounds.x1, Is.InRange(699, 702)); Assert.That(bounds.y1, Is.InRange(699, 702));
            d.Undo();
            Drag(400, 400, 450, 420);
            Assert.That(window.StatusMessage, Does.Contain("Moved by (50, 20)"), "inside the box moves");
            d.Undo();
            Drag(512, 512, 288, 512, EventModifiers.Shift);
            Assert.That(window.StatusMessage, Does.Contain("Rotated 90"), "outside a corner rotates");
            d.Undo();
            // 矢印キー: 画面の向きで動く（90° 回して反転した表示では、画面の右はキャンバスの +y）
            var dot = PaintBlock(100, 100, 101, 101, red);
            var expected = window.CanvasViewNow().ScreenToCanvasDirection(Vector2.right);
            Key(window, KeyCode.RightArrow);
            int ex = Mathf.RoundToInt(expected.x), ey = Mathf.RoundToInt(expected.y);
            Assert.That(dot.GetPixel(PaintChannel.Color, 100 + ex, 100 + ey), Is.EqualTo(red), "right moved along " + expected);
            var moved = ViewGui(100 + ex + .5, 100 + ey + .5) - ViewGui(100.5, 100.5);
            Assert.That(moved.x, Is.GreaterThan(0)); Assert.That(Mathf.Abs(moved.y), Is.LessThan(.01f), "on screen it moved right");
        }

        [Test] public void DraggingWithRHeldOrShiftMiddleRotatesTheViewWithoutPainting()
        {
            CanvasOnly();
            var before = Snapshot(); var centre = ViewCentre(); var start = ViewCanvas(centre);
            Key(window, KeyCode.R);
            Mouse(window, EventType.MouseDown, centre + new Vector2(120, 0));
            Mouse(window, EventType.MouseDrag, centre + new Vector2(85, 85));
            Mouse(window, EventType.MouseDrag, centre + new Vector2(0, 120));
            Assert.That(window.CanvasAngle, Is.EqualTo(90f).Within(.01f), "a quarter turn clockwise on screen");
            Mouse(window, EventType.MouseUp, centre + new Vector2(0, 120));
            Assert.That(window.IsStroking, Is.False); Assert.That(Snapshot(), Is.EqualTo(before), "rotating does not paint");
            AssertCentreStays(start, "R + drag");
            // Shift で 15° 刻み、Esc で始めの角度に戻す
            Mouse(window, EventType.MouseDown, centre + new Vector2(120, 0));
            MouseWithShift(EventType.MouseDrag, centre + new Vector2(120, 22));
            Assert.That(window.CanvasAngle, Is.EqualTo(105f), "about 10° more, snapped to 15° steps");
            Key(window, KeyCode.Escape);
            Assert.That(window.CanvasAngle, Is.EqualTo(90f), "Escape puts the angle back");
            Mouse(window, EventType.MouseUp, centre + new Vector2(120, 22));
            KeyUp(KeyCode.R);
            Assert.That(window.RotateKeyHeld, Is.False);
            // Shift ＋ 中ボタンのドラッグ
            var from = centre + new Vector2(0, -100); var to = centre + new Vector2(100, 0);
            MiddleDrag(to, to - from, EventModifiers.Shift);
            Assert.That(window.CanvasAngle, Is.EqualTo(180f).Within(.01f));
            // R を離した後の左ドラッグは描く。フォーカスを失うと R を押した印も消える
            Key(window, KeyCode.R); Invoke(window, "OnLostFocus");
            Assert.That(window.RotateKeyHeld, Is.False);
            BeginLine(300, 300); Mouse(window, EventType.MouseUp, At(window, 360, 300));
            Assert.That(Snapshot(), Is.Not.EqualTo(before));
        }
        void MouseWithShift(EventType type, Vector2 at)
        { window.SendEvent(new Event { type = type, mousePosition = at + window.rootVisualElement.worldBound.position, button = 0, pressure = 1, modifiers = EventModifiers.Shift }); }

        [Test] public void TheWheelZoomsAboutThePointer()
        {
            CanvasOnly();
            SetView(-37, true, 1, new Vector2(15, 10));
            var pointer = ViewCentre() + new Vector2(-90, 60); var under = ViewCanvas(pointer);
            window.SendEvent(new Event { type = EventType.ScrollWheel, mousePosition = pointer + window.rootVisualElement.worldBound.position, delta = new Vector2(0, -6) });
            Assert.That(window.CanvasZoom, Is.GreaterThan(1.3f));
            var after = ViewCanvas(pointer);
            Assert.That(Vector2.Distance(after, under), Is.LessThan(.05f), "the pixel under the pointer stays there (" + under + " → " + after + ")");
            Assert.That(window.CanvasAngle, Is.EqualTo(-37f));
        }
    }
}
