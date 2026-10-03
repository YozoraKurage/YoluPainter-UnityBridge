using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>2D キャンバスの表示の写し（CanvasView）: 画素の座標 ↔ GUI の座標の往復（回転・左右反転・拡大・パン込み）、回転も反転も無いときは
    /// 前からの float の式とバイト単位で同じこと、画像を描く行列と入力の写しが同じこと、ペンの傾きの向きを画面からキャンバスに直すこと。
    /// ウィンドウを開かない（batch-gl でも GUI でも走る）。</summary>
    public sealed class CanvasViewTests
    {
        static readonly float[] Angles = { 0, 15, 90, 180, -37, -90, 135 };
        static readonly Rect ViewRect = new Rect(212, 64, 520, 610);

        static IEnumerable<CanvasView> Views(int width = 1024, int height = 768)
        {
            foreach (float angle in Angles)
                foreach (bool flip in new[] { false, true })
                    foreach (float zoom in new[] { 1f, 2.5f, .37f })
                        foreach (var pan in new[] { Vector2.zero, new Vector2(37.5f, -112.25f) })
                            yield return new CanvasView(ViewRect, width, height, zoom, pan, angle, flip);
        }
        static void Near(Vector2 actual, Vector2 expected) => Assert.That(Vector2.Distance(actual, expected), Is.LessThan(1e-3f), actual + " vs " + expected);
        static string Name(CanvasView v) => v.Angle + "° flip " + v.Flip + " image " + v.Image;

        [Test] public void PixelsGoToTheGuiAndBackForEveryAngleFlipZoomAndPan()
        {
            int checkedPoints = 0;
            foreach (var view in Views())
            {
                foreach (var (x, y) in new[] { (0.0, 0.0), (1024.0, 768.0), (511.5, 383.25), (-40.0, 900.0), (17.125, 700.5) })
                {
                    var g = view.ToGui(x, y);
                    view.ToCanvas(g, out double bx, out double by);
                    // GUI の座標は float（1000 点ほどで 6e-5 点）なので、画素に戻すと 1 点の大きさで割った分だけずれる
                    double tolerance = 2e-4 / view.PixelSize + 1e-6;
                    Assert.That(bx, Is.EqualTo(x).Within(tolerance), Name(view) + " x of " + (x, y));
                    Assert.That(by, Is.EqualTo(y).Within(tolerance), Name(view) + " y of " + (x, y));
                    var f = view.ToCanvas(g);
                    Assert.That(f.x, Is.EqualTo(x).Within(tolerance + 2e-4), Name(view)); Assert.That(f.y, Is.EqualTo(y).Within(tolerance + 2e-4), Name(view));
                    checkedPoints++;
                }
                foreach (var g in new[] { new Vector2(300, 200), new Vector2(ViewRect.center.x, ViewRect.center.y), new Vector2(700.25f, 650.75f) })
                {
                    var back = view.ToGui(view.ToCanvas(g));
                    Assert.That(Vector2.Distance(back, g), Is.LessThan(2e-3f), Name(view) + " gui " + g);
                }
            }
            Assert.That(checkedPoints, Is.EqualTo(Angles.Length * 2 * 3 * 2 * 5));
        }

        /// <summary>回転も反転も無いときは、前の ImageRect・ToGui・CanvasPoint・PaintAt と同じ float の式（表示も描いた画素も前と同じ）。</summary>
        [Test] public void WithoutRotationOrFlipTheOldFloatFormulasAreKept()
        {
            const int w = 1024, h = 512; const float zoom = 1.37f; var pan = new Vector2(13.5f, -7.25f);
            var view = new CanvasView(ViewRect, w, h, zoom, pan, 0, false);
            Assert.That(view.AxisAligned, Is.True);
            float fit = Mathf.Min(ViewRect.width / w, ViewRect.height / h) * zoom; float width = w * fit, height = h * fit;
            var image = new Rect(ViewRect.center.x - width * .5f + pan.x, ViewRect.center.y - height * .5f + pan.y, width, height);
            Assert.That(view.Image, Is.EqualTo(image));
            for (int i = 0; i < 50; i++)
            {
                float px = i * 21.37f - 30, py = i * 11.9f - 5;
                var gui = new Vector2(image.x + px / w * image.width, image.y + (1 - py / h) * image.height);
                Assert.That(view.ToGui(px, py), Is.EqualTo(gui));
                var pointer = new Vector2(300 + i * 7.3f, 100 + i * 9.1f);
                var canvas = new Vector2((pointer.x - image.x) / image.width * w, (1 - (pointer.y - image.y) / image.height) * h);
                Assert.That(view.ToCanvas(pointer), Is.EqualTo(canvas));
                view.ToCanvas(pointer, out double dx, out double dy);
                Assert.That((dx, dy), Is.EqualTo(((double)((pointer.x - image.x) / image.width * w), (double)((1 - (pointer.y - image.y) / image.height) * h))));
            }
        }

        [Test] public void RotationTurnsTheImageClockwiseOnScreenAndFlipMirrorsIt()
        {
            var straight = new CanvasView(ViewRect, 1000, 1000, 1, Vector2.zero, 0, false);
            var c = straight.Center; float s = straight.PixelSize;
            // 90°: キャンバスの +x（右）は画面の下へ、+y（上）は画面の右へ
            var quarter = new CanvasView(ViewRect, 1000, 1000, 1, Vector2.zero, 90, false);
            Near(quarter.ToGui(600, 500) - c, new Vector2(0, 100 * s));
            Near(quarter.ToGui(500, 600) - c, new Vector2(100 * s, 0));
            // 180° はちょうど点対称（sin を 0 にしている）
            var half = new CanvasView(ViewRect, 1000, 1000, 1, Vector2.zero, 180, false);
            Near(half.ToGui(600, 550) - c, -(straight.ToGui(600, 550) - c));
            // 反転: 画像の中心の縦の線で鏡に映す
            var mirror = new CanvasView(ViewRect, 1000, 1000, 1, Vector2.zero, 0, true);
            var a = straight.ToGui(700, 300) - c; var b = mirror.ToGui(700, 300) - c;
            Assert.That(b.x, Is.EqualTo(-a.x).Within(1e-3f)); Assert.That(b.y, Is.EqualTo(a.y).Within(1e-3f));
            // 角度の範囲
            Assert.That(CanvasView.NormalizeAngle(180), Is.EqualTo(180f)); Assert.That(CanvasView.NormalizeAngle(-180), Is.EqualTo(180f));
            Assert.That(CanvasView.NormalizeAngle(195), Is.EqualTo(-165f)); Assert.That(CanvasView.NormalizeAngle(540), Is.EqualTo(180f));
            Assert.That(CanvasView.NormalizeAngle(-375), Is.EqualTo(-15f)); Assert.That(CanvasView.NormalizeAngle(float.NaN), Is.EqualTo(0f));
            Assert.That(TexturePaintWindow.AngleLabel(-37), Is.EqualTo("-37°")); Assert.That(TexturePaintWindow.AngleLabel(12.34f), Is.EqualTo("12.3°"));
        }

        /// <summary>画像を描く行列（GL.modelview に掛ける）で回した画像の矩形の点は、入力の写し（ToGui）と同じ所に来る。</summary>
        [Test] public void TheDrawingMatrixPutsEveryPixelWhereTheInputMapsIt()
        {
            foreach (var view in Views(800, 600))
            {
                var image = view.Image; var m = view.ImageMatrix();
                foreach (var (x, y) in new[] { (0f, 0f), (800f, 600f), (123.5f, 456.25f), (800f, 0f) })
                {
                    var unturned = new Vector3(image.x + x / 800 * image.width, image.y + (1 - y / 600) * image.height, 0);
                    var drawn = m.MultiplyPoint3x4(unturned);
                    Assert.That(Vector2.Distance(drawn, view.ToGui(x, y)), Is.LessThan(1e-2f), Name(view) + " pixel " + (x, y));
                }
            }
        }

        /// <summary>画面の上の向きをキャンバスの向きに直す写しは、2 点を写した差と同じ向き（拡大率は掛けない）。</summary>
        [Test] public void ScreenDirectionsTurnLikeThePoints()
        {
            foreach (var view in Views())
                foreach (var d in new[] { new Vector2(1, 0), new Vector2(0, 1), new Vector2(.6f, -.8f) })
                {
                    var g = new Vector2(400, 300);
                    var moved = (view.ToCanvas(g + d * 50) - view.ToCanvas(g)) / (50 / view.PixelSize);
                    var direction = view.ScreenToCanvasDirection(d);
                    Assert.That(Vector2.Distance(moved, direction), Is.LessThan(1e-3f), Name(view) + " " + d);
                }
        }

        /// <summary>ペンの傾き: 表示が回っていれば倒れた向きを回してキャンバスの軸ごとの角度に。倒れた量（PenTilt.Amount）は変わらない。
        /// 回っていなければ前と同じ (x, -y)。</summary>
        [Test] public void PenTiltTurnsWithTheViewAndKeepsItsAmount()
        {
            var tilt = new Vector2(.5f, -.3f);
            var straight = new CanvasView(ViewRect, 512, 512, 1, Vector2.zero, 0, false);
            var (sx, sy) = TexturePaintWindow.TiltToCanvas(straight, tilt);
            Assert.That(sx, Is.EqualTo(.5).Within(1e-6)); Assert.That(sy, Is.EqualTo(.3).Within(1e-6));
            foreach (var view in Views(512, 512))
            {
                var (x, y) = TexturePaintWindow.TiltToCanvas(view, tilt);
                Assert.That(PenTilt.Amount(x, y), Is.EqualTo(PenTilt.Amount(sx, sy)).Within(1e-5), Name(view));
                // 倒れた向き: 画面の向き (tan x, tan y)（y は下向き）をキャンバスに写した向き
                var expected = view.ScreenToCanvasDirection(new Vector2((float)Math.Tan(tilt.x), (float)Math.Tan(tilt.y)));
                Assert.That(PenTilt.Azimuth(x, y), Is.EqualTo(Math.Atan2(expected.y, expected.x)).Within(1e-4), Name(view));
            }
            // 90° 回すと、画面で右に倒したペンはキャンバスの +y（上）に倒れている
            var (qx, qy) = TexturePaintWindow.TiltToCanvas(new CanvasView(ViewRect, 512, 512, 1, Vector2.zero, 90, false), new Vector2(.4f, 0));
            Assert.That(qx, Is.EqualTo(0).Within(1e-6)); Assert.That(qy, Is.EqualTo(.4).Within(1e-6));
        }
            /// <summary>拡大・パン・回転・反転はウィンドウの状態としてシリアライズされ、スクリプトのコンパイル（ここでは状態の書き出しと読み戻しで
        /// 代える）で戻らない。</summary>
        [Test] public void TheViewSurvivesTheWindowsSerialization()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>(); var copy = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                Assert.That((copy.CanvasZoom, copy.CanvasAngle, copy.CanvasFlipped), Is.EqualTo((1f, 0f, false)), "a new window starts straight");
                w.ZoomCanvasView(2.5f, new Vector2(40, 30)); Assert.That(w.RotateCanvasView(-37), Is.True); Assert.That(w.FlipCanvasView(), Is.True);
                Assert.That(w.CanvasPan, Is.Not.EqualTo(Vector2.zero));
                string json = UnityEditor.EditorJsonUtility.ToJson(w);
                UnityEditor.EditorJsonUtility.FromJsonOverwrite(json, copy);
                Assert.That((copy.CanvasZoom, copy.CanvasPan, copy.CanvasAngle, copy.CanvasFlipped), Is.EqualTo((w.CanvasZoom, w.CanvasPan, w.CanvasAngle, w.CanvasFlipped)));
                Assert.That((copy.CanvasAngle, copy.CanvasFlipped), Is.EqualTo((37f, true)), "flipping mirrors the angle too");
            }
            finally
            {
                foreach (var x in new[] { w, copy }) { string recovery = x.RecoveryRoot; UnityEngine.Object.DestroyImmediate(x); if (Directory.Exists(recovery)) Directory.Delete(recovery, true); }
            }
        }

        // ───────── オフスクリーンの描画（batch-gl） ─────────

        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "canvas-view"));
        static readonly Rgba32 FillColor = new Rgba32(250, 20, 200), MarkColor = new Rgba32(20, 220, 40);

        static void RequireOffscreen()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
        }
        /// <summary>全面を 1 色で塗り、左下の 128 画素四方に印の色を置いた窓（2D だけの表示）。</summary>
        static TexturePaintWindow MarkedWindow()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            var d = w.Document;
            d.Fill(w.SelectedLayer, PaintChannel.Color, FillColor, 1, SelectionMask.All(d));
            d.Fill(w.SelectedLayer, PaintChannel.Color, MarkColor, 1, SelectionMask.Rectangle(d, 0, 0, 128, 128));
            d.ClearHistory(); w.View = TexturePaintWindow.ViewMode.Canvas;
            return w;
        }
        static void Close(TexturePaintWindow w) { string recovery = w.RecoveryRoot; UnityEngine.Object.DestroyImmediate(w); if (Directory.Exists(recovery)) Directory.Delete(recovery, true); }
        /// <summary>窓を描いた PNG の画素（GUI の座標、y は下向き）。</summary>
        static Color32[] RenderPixels(TexturePaintWindow w, int width, int height, string name)
        {
            string path = Path.Combine(Folder, name + ".png");
            OffscreenGui.RenderWindow(w, width, height, path);
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, name);
                var rows = texture.GetPixels32(); var gui = new Color32[rows.Length];
                for (int y = 0; y < height; y++) Array.Copy(rows, (height - 1 - y) * width, gui, y * width, width); // 読んだ行は下から
                return gui;
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
        static bool Looks(Color32 c, Rgba32 want) => Math.Abs(c.r - want.R) <= 4 && Math.Abs(c.g - want.G) <= 4 && Math.Abs(c.b - want.B) <= 4;
        static Color32 At(Color32[] gui, int width, Vector2 p) => gui[Mathf.FloorToInt(p.y) * width + Mathf.FloorToInt(p.x)];

        /// <summary>回した画像は表示域の中だけに描かれ（GUI.matrix で回すとクリップがずれてはみ出した。GL の行列で回す）、入力の写しと同じ所に
        /// 同じ向きで見える（左下の印が、写しで求めた GUI の位置にある）。英語と日本語の見出しの印も描いて残す（見て確かめる用）。</summary>
        [Test] public void TheTurnedCanvasIsDrawnWhereTheInputMapsItAndOnlyInsideItsView()
        {
            RequireOffscreen();
            const int width = 1200, height = 800;
            var w = MarkedWindow();
            try
            {
                // 大きく拡大して 45° 回すと、表示域は全部が画像で覆われ、外には 1 画素もはみ出さない
                w.ZoomCanvasView(3); w.RotateCanvasView(45);
                var gui = RenderPixels(w, width, height, "cover-45");
                var view = w.CanvasRect; int inside = 0, outside = 0, missing = 0;
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    bool fill = Looks(gui[y * width + x], FillColor);
                    bool within = x >= view.xMin && x < view.xMax && y >= view.yMin && y < view.yMax;
                    // 外は表示域のまわり 12 点の帯で見る（ドックのサムネイルは同じ色の文書を見せるので数えない）
                    bool band = x >= view.xMin - 12 && x < view.xMax + 12 && y >= view.yMin - 12 && y < view.yMax + 12;
                    if (fill && within) inside++; else if (fill && band) outside++;
                    else if (x > view.xMin + 1 && x < view.xMax - 2 && y > view.yMin + 1 && y < view.yMax - 2) missing++;
                }
                Assert.That(outside, Is.Zero, "the turned image stays inside the canvas view " + view);
                Assert.That(missing, Is.Zero, "the zoomed image covers the whole view");
                Assert.That(inside, Is.GreaterThan((int)(view.width * view.height * .95f)));

                // 拡大を戻し、いくつかの回転と反転で、左下の印が写しの示す所に見える
                foreach (var (angle, flip) in new[] { (90f, false), (-37f, true), (180f, true), (30f, false) })
                {
                    w.FitCanvasView(); if (w.CanvasFlipped != flip) w.FlipCanvasView();
                    w.ZoomCanvasView(.7f); w.RotateCanvasView(angle);
                    string name = "marker-" + angle + (flip ? "-flip" : "");
                    gui = RenderPixels(w, width, height, name);
                    var map = w.CanvasViewNow();
                    Assert.That(Looks(At(gui, width, map.ToGui(64, 64)), MarkColor), Is.True, name + ": the mark is where the input maps pixel (64, 64)");
                    Assert.That(Looks(At(gui, width, map.ToGui(900, 900)), FillColor), Is.True, name + ": the fill at (900, 900)");
                    Assert.That(Looks(At(gui, width, map.ToGui(960, 64)), FillColor), Is.True, name + ": not mirrored or turned the wrong way");
                    Assert.That(Looks(At(gui, width, map.ToGui(64, 960)), FillColor), Is.True, name + ": not mirrored or turned the wrong way");
                }

                // 見出しの印（英語と日本語。30° 回して反転）
                if (!w.CanvasFlipped) w.FlipCanvasView();
                w.Document.SetSelection(SelectionMask.Ellipse(w.Document, 300, 200, 250, 150)); // 選択範囲の重ねも画像と一緒に回る
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    w.View = TexturePaintWindow.ViewMode.Split;
                    RenderPixels(w, width, height, language + "-split-30-flip");
                    RenderPixels(w, 980, 640, language + "-split-small");
                    w.View = TexturePaintWindow.ViewMode.Canvas; w.Tool = TexturePaintWindow.PaintTool.Move;
                    RenderPixels(w, width, height, language + "-move-handles");
                    w.Tool = TexturePaintWindow.PaintTool.Brush;
                }
            }
            finally { Close(w); L.OverrideLanguage(PainterLanguage.English); }
        }
    }
}
