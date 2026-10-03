using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウの表示の合成は、1 回の描画（Repaint）の時間で区切られ、残りは次の描画に回る（個人の設定「Compositing time
    /// per frame」）。ストロークの所はその回に出ること、保存・書き出し・スポイトは途中の表示ではなく文書を読むこと、テクスチャセットの
    /// 切り替え・チャンネルの切り替え・Undo の途中でも最後は CPU の正本と同じになること。時間は合成器の時計を差し替えて決める
    /// （ブロック 1 つ = 5 ms、予算 1 ms なので 1 回の描画に 1 ブロック）。</summary>
    public sealed partial class WindowTests
    {
        /// <summary>表示を（描画を送らずに）読む。</summary>
        static byte[] Shown(TexturePaintWindow w) => w.Compositor.Texture is RenderTexture ? GpuTests.Read(w.Compositor.Texture) : GpuTests.ReadCpu(w.Compositor.Texture);
        /// <summary>描画を 1 回だけ送る（残りの仕事を流し終えない）。</summary>
        static void OneRepaint(TexturePaintWindow w) { EditorShaderCompiler.TolerateErrorLogsIfBroken(); w.SendEvent(new Event { type = EventType.Repaint }); }
        /// <summary>予算 1 ms、ブロック 1 つの合成 = 5 ms の時計。</summary>
        static void SliceOneBlockPerRepaint(TexturePaintWindow w)
        {
            PainterSettings.UpdatePersonal(p => p.displayFrameBudgetMs = 1);
            double now = 0; w.Compositor.ClockForTests = () => now; w.Compositor.BlockCompositedForTests = (x, y) => now += 5;
        }
        static void AssertShowsTheDocument(TexturePaintWindow w, string context)
        {
            var expected = w.Document.Composite(w.Channel);
            if (w.Compositor.Path == TileGpuCompositor.CompositePath.Gpu) GpuTests.AssertMatches(expected, Shown(w), context);
            else CpuCompositingTests.AssertSameBytes(expected, Shown(w), context);
        }

        [Test] public void TheDisplayIsCompositedInSlicesWhileSavingExportingAndPickingReadTheDocument()
        {
            var d = window.Document; Assert.That(d.Width, Is.EqualTo(1024), "four blocks of 512");
            var layer = d.Layers[d.Layers.Count - 1];
            PaintDot(window, 200, 200); PaintDot(window, 800, 700); PaintDot(window, 800, 200); // ブロック (0,0)・(1,1)・(1,0)
            Repaint(window);
            Assert.That(window.Compositor.HasPendingWork, Is.False);
            SliceOneBlockPerRepaint(window);
            Assert.That(window.DisplaySchedule().BudgetMilliseconds, Is.EqualTo(1));
            Assert.That(window.DisplaySchedule().Visible, Is.EqualTo(new RectInt(0, 0, 1024, 1024)), "the whole canvas is in view");
            var before = Shown(window);
            d.SetLayerOpacity(layer.Id, .35);
            OneRepaint(window);
            Assert.That(window.Compositor.LastProcessedBlocks.Count, Is.EqualTo(1), "one block per repaint");
            Assert.That(window.Compositor.HasPendingWork, Is.True, "the other two blocks wait for the next repaints");
            var waiting = new[] { (0, 0), (1, 1), (1, 0) }.Except(window.Compositor.LastProcessedBlocks).ToArray();
            Assert.That(waiting.Length, Is.EqualTo(2));
            var (wx, wy) = waiting[0]; int px = wx == 0 ? 200 : 800, py = wy == 0 ? 200 : 700;
            Assert.That(Shown(window).Skip((py * 1024 + px) * 4).Take(4), Is.EqualTo(before.Skip((py * 1024 + px) * 4).Take(4)), "a waiting block still shows the old picture");

            // スポイト（全レイヤー）は文書の合成を読む
            window.WandSampleAll = true;
            window.PickColor(new Vector2(px + .5f, py + .5f));
            var c = d.CompositePixel(PaintChannel.Color, px, py);
            Assert.That(window.Brush.color, Is.EqualTo(new Color(c.R / 255f, c.G / 255f, c.B / 255f, 1)), "the eyedropper picks the new composite, not the display");
            // 書き出しと保存は CPU の正本
            var fake = UseFakeDialogs(window); fake.Folder = NewTempPath(); Directory.CreateDirectory(fake.Folder);
            window.ExportImages();
            Assert.That(Pixels(File.ReadAllBytes(Path.Combine(fake.Folder, "Texture_Color.png"))), Is.EqualTo(d.Composite(PaintChannel.Color)), "Export Images");
            fake.File = NewYlpPath(); window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var setFiles = SetFiles(YlpStore.Load(fake.File).Files);
            Assert.That(Pixels(setFiles["composite/Color.png"]), Is.EqualTo(d.Composite(PaintChannel.Color)), "the .ylp composite");

            Repaint(window); // 残りを流し終える
            Assert.That(window.Compositor.HasPendingWork, Is.False);
            AssertShowsTheDocument(window, "all blocks");
        }

        [Test] public void AStrokeIsShownInTheSameRepaintWhateverTheBudget()
        {
            var d = window.Document; var layer = d.Layers[d.Layers.Count - 1];
            PaintDot(window, 200, 200); PaintDot(window, 800, 700); PaintDot(window, 800, 200);
            Repaint(window);
            SliceOneBlockPerRepaint(window);
            d.SetLayerOpacity(layer.Id, .5); OneRepaint(window);
            Assert.That(window.Compositor.HasPendingWork, Is.True);
            // ブロック (0, 1) から (1, 1) へ横切る線: 描いているあいだ、描いた所は毎回の描画に出る（待っているブロックより先に、予算を超えても）
            var from = At(window, 300, 800); // At は Repaint を送る（流し終える）ので、先に位置を決める
            d.SetLayerOpacity(layer.Id, .45); OneRepaint(window);
            Assert.That(window.Compositor.HasPendingWork, Is.True);
            var mid = window.PixelToGui(500, 810); var to = window.PixelToGui(700, 820); var past = window.PixelToGui(900, 830);
            Mouse(window, EventType.MouseDown, from);
            // 2D のストロークは点の間を曲線で結び、最新の点への区間は次の点を待つので、(700, 820) までを描かせるのに 1 つ先の点も送る
            Mouse(window, EventType.MouseDrag, mid); Mouse(window, EventType.MouseDrag, to); Mouse(window, EventType.MouseDrag, past);
            Assert.That(window.IsStroking, Is.True);
            OneRepaint(window);
            var shown = Shown(window); var expected = d.Composite(PaintChannel.Color);
            foreach (var x in new[] { 300, 500, 700 })
            {
                int y = 800 + (x - 300) / 20, i = (y * 1024 + x) * 4; // 線の上
                for (int k = 0; k < 4; k++) Assert.That(System.Math.Abs(shown[i + k] - expected[i + k]), Is.LessThanOrEqualTo(1), "the stroke at x " + x + " is shown in the same repaint");
            }
            Assert.That(window.Compositor.LastProcessedBlocks, Has.Member((0, 1)).And.Member((1, 1)), "both blocks of the stroke, over the 1 ms budget");
            Mouse(window, EventType.MouseUp, past);
            Repaint(window);
            AssertShowsTheDocument(window, "after the stroke");
        }

        [Test] public void SwitchingChannelsAndUndoingWhileWorkIsLeftEndsWithTheDocument()
        {
            var d = window.Document; var layer = d.Layers[d.Layers.Count - 1];
            PaintDot(window, 200, 200); PaintDot(window, 800, 700);
            window.Channel = PaintChannel.Roughness; PaintDot(window, 600, 300); window.Channel = PaintChannel.Color;
            Repaint(window);
            SliceOneBlockPerRepaint(window);
            d.SetLayerOpacity(layer.Id, .3); OneRepaint(window);
            Assert.That(window.Compositor.HasPendingWork, Is.True);
            window.Channel = PaintChannel.Roughness; OneRepaint(window);
            Repaint(window); AssertShowsTheDocument(window, "Roughness");
            window.Channel = PaintChannel.Color; OneRepaint(window);
            d.Undo(); OneRepaint(window);
            Repaint(window); AssertShowsTheDocument(window, "after Undo");
            d.Redo(); OneRepaint(window); d.Undo(); OneRepaint(window); d.Redo();
            Repaint(window); AssertShowsTheDocument(window, "after Redo");
            // 0 = 分けない（以前の動き）: 1 回の描画で全部
            PainterSettings.UpdatePersonal(p => p.displayFrameBudgetMs = 0);
            Assert.That(window.DisplaySchedule().BudgetMilliseconds, Is.EqualTo(double.PositiveInfinity));
            d.SetLayerOpacity(layer.Id, .9); OneRepaint(window);
            Assert.That(window.Compositor.HasPendingWork, Is.False, "no limit composites everything in one repaint");
            AssertShowsTheDocument(window, "no limit");
        }

        /// <summary>縮小表示の幅: コントロールのドラッグの最中だけ、2D の表示が画面の 1 画素に文書の画素を 4 つ以上映しているとき 1/4。
        /// 拡大して見ているとき・ストロークの最中・ドラッグしていないときは使わない。</summary>
        [Test] public void TheReducedPreviewIsOnlyForDragsWhereTheViewDoesNotNeedFullResolution()
        {
            Invoke(window, "CreateDocument", 4096); Invoke(window, "BindDocument"); Repaint(window);
            Assert.That(window.CanvasRect.width, Is.GreaterThan(0));
            Assert.That(window.PreviewStepNow(), Is.EqualTo(1), "no drag");
            int saved = GUIUtility.hotControl;
            try
            {
                GUIUtility.hotControl = 4242; // スライダーをつかんでいる
                float onScreen = window.CanvasViewNow().PixelSize * EditorGUIUtility.pixelsPerPoint;
                Assert.That(window.PreviewStepNow(), Is.EqualTo(onScreen <= .25f ? 4 : 1), "fit to the window: " + onScreen + " screen pixels per texel");
                Assert.That(window.DisplaySchedule().PreviewStep, Is.EqualTo(window.PreviewStepNow()));
                window.ZoomCanvasView(16, window.CanvasRect.center); // 1 画素が大きく見える
                Assert.That(window.PreviewStepNow(), Is.EqualTo(1), "zoomed in: full resolution is needed");
            }
            finally { GUIUtility.hotControl = saved; }
        }

        [Test] public void TheModelsUvTilesAreKnownForThePreview()
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            Repaint(window);
            var tiles = window.ModelTiles();
            Assert.That(tiles, Is.Not.Null.And.Not.Empty, "the demo mesh's UVs cover some tiles");
            Assert.That(tiles.All(t => t.X >= 0 && t.Y >= 0 && t.X < 8 && t.Y < 8), Is.True);
            Assert.That(window.ModelTiles(), Is.SameAs(tiles), "kept while the model and the slot are the same");
            if (window.SurfaceRect.width > 0) Assert.That(window.DisplaySchedule().ModelTiles, Is.SameAs(tiles));
        }
    }
}
