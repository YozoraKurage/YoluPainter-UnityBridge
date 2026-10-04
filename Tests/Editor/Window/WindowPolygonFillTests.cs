using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ポリゴン塗りつぶし（キー 4）を本物の窓にマウスとキーを流して確かめる（GUI モード）: 3D ビューと 2D キャンバスのクリックと
    /// ドラッグ（離して 1 回の Undo）、今までの 3D Pick のバケツと同じ画素、消す、選択範囲の内側だけ、ロックで断る、マスクの白と黒、
    /// Esc・フォーカスを失うと取り消し、ポインタの下の強調（範囲を引くのは範囲が変わったときだけ）、キーとショートカットのガード。</summary>
    public sealed partial class WindowTests
    {
        static readonly Color FillRed = new Color(.9f, .1f, .1f, 1);

        SurfaceGeometry PolygonFillCube()
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            window.Tool = TexturePaintWindow.PaintTool.PolygonFill; window.SurfacePick = SurfaceRegionKind.UvIsland;
            window.Brush.color = FillRed; window.Brush.opacity = 1;
            Repaint(window);
            return window.Preview.Geometry;
        }

        /// <summary>三角形 t の UV の重心の画素（2D キャンバスでその三角形を押す点）。</summary>
        Vector2Int UvCentroidPixel(SurfaceGeometry g, int t)
        {
            var tri = g.Triangles[t]; var uv = (tri.UvA + tri.UvB + tri.UvC) / 3;
            return new Vector2Int(Mathf.FloorToInt(uv.x * window.Document.Width), Mathf.FloorToInt(uv.y * window.Document.Height));
        }
        byte LayerAlpha(Vector2Int p) => window.Document.GetLayer(window.SelectedLayer).GetChannel(PaintChannel.Color).GetPixel(p.x, p.y).A;

        [Test] public void PolygonFillOnTheModelMatchesTheOld3DPickBucketAndIsOneUndo()
        {
            var g = PolygonFillCube(); var d = window.Document; var before = Snapshot(); int steps = d.UndoCount;
            var center = window.SurfaceRect.center;
            Assert.That(window.Preview.TryPick(window.SurfaceRect, center, out var hit), Is.True);
            Click(center);
            Assert.That(window.IsStroking, Is.False); Assert.That(window.StatusMessage, Does.Contain("Filled UV Island × 1"));
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            var filled = Snapshot(); Assert.That(filled, Is.Not.EqualTo(before));
            // 今までの 3D Pick（バケツの 3D のクリック）と同じ画素
            Key(window, KeyCode.Z, EventModifiers.Control); Assert.That(Snapshot(), Is.EqualTo(before));
            window.FillRegion(SurfaceRegions.Selection(d, g, SurfaceRegions.Region(g, hit.TriangleIndex, SurfaceRegionKind.UvIsland)), "island");
            Assert.That(Snapshot(), Is.EqualTo(filled), "the same pixels as the bucket's 3D click");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift); Assert.That(Snapshot(), Is.EqualTo(filled), "redo");
            // ほかのアイランドは塗らない
            int other = Enumerable.Range(0, g.TriangleCount).First(t => !SurfaceRegions.Region(g, hit.TriangleIndex, SurfaceRegionKind.UvIsland).Contains(t));
            Assert.That(LayerAlpha(UvCentroidPixel(g, other)), Is.Zero);
        }

        [Test] public void DraggingOverTheModelAddsEveryIslandItPassesAsOneUndo()
        {
            var g = PolygonFillCube(); var d = window.Document; int steps = d.UndoCount; var index = new SurfaceRegionIndex(g);
            var center = window.SurfaceRect.center; Vector2 end = center; var islands = new HashSet<long>();
            foreach (var direction in new[] { Vector2.right, Vector2.left, Vector2.up, Vector2.down })
            {
                islands.Clear(); end = center + direction * window.SurfaceRect.height * .45f;
                for (int i = 0; i <= 40; i++)
                    if (window.Preview.TryPick(window.SurfaceRect, Vector2.Lerp(center, end, i / 40f), out var h)) islands.Add(index.Key(h.TriangleIndex, SurfaceRegionKind.UvIsland));
                if (islands.Count >= 2) break;
            }
            Assert.That(islands.Count, Is.GreaterThanOrEqualTo(2), "no line from the center crosses two faces of the demo cube");
            Mouse(window, EventType.MouseDown, center);
            Assert.That(window.IsPolygonFilling, Is.True); Assert.That(d.HasActiveStroke, Is.True);
            Mouse(window, EventType.MouseDrag, Vector2.Lerp(center, end, .5f)); Mouse(window, EventType.MouseDrag, end);
            Mouse(window, EventType.MouseUp, end);
            Assert.That(window.IsPolygonFilling, Is.False); Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "one undo for the whole drag");
            Assert.That(window.StatusMessage, Does.Contain("× " + islands.Count));
            for (int t = 0; t < g.TriangleCount; t++)
                Assert.That(LayerAlpha(UvCentroidPixel(g, t)), Is.EqualTo(islands.Contains(index.Key(t, SurfaceRegionKind.UvIsland)) ? 255 : 0), "triangle " + t);
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(Enumerable.Range(0, g.TriangleCount).All(t => LayerAlpha(UvCentroidPixel(g, t)) == 0), Is.True, "one undo takes the whole drag back");
        }

        [Test] public void TheCanvasFillsTheUvRegionUnderThePointerAndDragsAcrossIslands()
        {
            var g = PolygonFillCube(); var d = window.Document; int steps = d.UndoCount;
            // 隙間（アイランドの間）: 何も塗らず、履歴も増えない
            Click(At(window, 341, 256));
            Assert.That(window.StatusMessage, Does.Contain("No triangle of this texture set")); Assert.That(d.UndoCount, Is.EqualTo(steps));
            // 三角形 0 のアイランド（三角形 0 と 1）
            Click(At(window, UvCentroidPixel(g, 0).x, UvCentroidPixel(g, 0).y));
            Assert.That(LayerAlpha(UvCentroidPixel(g, 0)), Is.EqualTo(255)); Assert.That(LayerAlpha(UvCentroidPixel(g, 1)), Is.EqualTo(255), "the whole island");
            Assert.That(LayerAlpha(UvCentroidPixel(g, 2)), Is.Zero, "not the next island");
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            // 三角形 2 のアイランドから 4 のアイランドへドラッグ（UV で隣り合う）
            Mouse(window, EventType.MouseDown, At(window, UvCentroidPixel(g, 2).x, UvCentroidPixel(g, 2).y));
            Mouse(window, EventType.MouseDrag, At(window, UvCentroidPixel(g, 4).x, UvCentroidPixel(g, 4).y));
            Mouse(window, EventType.MouseUp, At(window, UvCentroidPixel(g, 4).x, UvCentroidPixel(g, 4).y));
            Assert.That(LayerAlpha(UvCentroidPixel(g, 2)), Is.EqualTo(255)); Assert.That(LayerAlpha(UvCentroidPixel(g, 4)), Is.EqualTo(255));
            Assert.That(d.UndoCount, Is.EqualTo(steps + 2));
        }

        [Test] public void EscapeFocusLossAndReloadCancelTheDrag()
        {
            var g = PolygonFillCube(); var d = window.Document; var before = Snapshot(); int steps = d.UndoCount;
            foreach (var cancel in new System.Action[] { () => Key(window, KeyCode.Escape), () => Invoke(window, "OnLostFocus"), () => Invoke(window, "BeforeReload"),
                () => Invoke(window, "PlayModeChanged", PlayModeStateChange.ExitingEditMode) })
            {
                Mouse(window, EventType.MouseDown, At(window, UvCentroidPixel(g, 0).x, UvCentroidPixel(g, 0).y));
                Mouse(window, EventType.MouseDrag, At(window, UvCentroidPixel(g, 2).x, UvCentroidPixel(g, 2).y));
                Assert.That(window.IsPolygonFilling, Is.True); Assert.That(Snapshot(), Is.Not.EqualTo(before), "the drag paints while it runs");
                cancel();
                Assert.That(window.IsPolygonFilling, Is.False); Assert.That(d.HasActiveStroke, Is.False);
                Assert.That(Snapshot(), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.EqualTo(steps));
                Mouse(window, EventType.MouseUp, At(window, UvCentroidPixel(g, 2).x, UvCentroidPixel(g, 2).y)); // 取り消した後の離しは何もしない
                Assert.That(Snapshot(), Is.EqualTo(before));
            }
        }

        [Test] public void EraseStaysInsideTheSelectionAndLocksRefuseWithAReason()
        {
            var g = PolygonFillCube(); var d = window.Document; var layer = window.SelectedLayer;
            var p0 = UvCentroidPixel(g, 0); var p1 = UvCentroidPixel(g, 1);
            Click(At(window, p0.x, p0.y)); Assert.That(LayerAlpha(p0), Is.EqualTo(255));
            // 消す（オプションバーのボタン）を選択範囲の内側だけ: 三角形 0 の重心は内側、三角形 1 の重心は外側
            ClickToolControl("polyfill-erase"); Assert.That(window.PolygonFillErase, Is.True);
            int split = (p0.x + p1.x) / 2; d.SetSelection(SelectionMask.Rectangle(d, 0, 0, split, d.Height));
            Assert.That(p0.x, Is.LessThan(split)); Assert.That(p1.x, Is.GreaterThan(split));
            Click(At(window, p0.x, p0.y));
            Assert.That(LayerAlpha(p0), Is.Zero, "erased inside the selection"); Assert.That(LayerAlpha(p1), Is.EqualTo(255), "kept outside the selection");
            Assert.That(window.StatusMessage, Does.Contain("Erased"));
            d.ClearSelection();
            // ロック: 画像・すべては断り、透明部分のロックでは消すのを断る。何も変えず、理由を出す
            var before = Snapshot(); int steps = d.UndoCount;
            foreach (var (locks, erase, reason) in new[] { (LayerLocks.Pixels, false, "image pixels locked"), (LayerLocks.All, false, "is locked"), (LayerLocks.Transparency, true, "transparent pixels locked") })
            {
                d.SetLayerLocks(layer, locks); steps = d.UndoCount; window.PolygonFillErase = erase;
                Click(At(window, p1.x, p1.y));
                Assert.That(window.StatusMessage, Does.Contain(reason), locks.ToString()); Assert.That(window.IsStroking, Is.False);
                Assert.That(Snapshot(), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.EqualTo(steps));
            }
            // 透明部分のロックで塗る: 色だけが変わり、透明な画素は透明のまま
            window.PolygonFillErase = false; window.Brush.color = Color.blue;
            Click(At(window, p1.x, p1.y));
            var s = d.GetLayer(layer).GetChannel(PaintChannel.Color);
            Assert.That(s.GetPixel(p1.x, p1.y), Is.EqualTo(new Rgba32(0, 0, 255, 255)), "recoloured, alpha kept");
            Assert.That(s.GetPixel(p0.x, p0.y).A, Is.Zero, "the erased pixel stays transparent");
        }

        [Test] public void OnAMaskPaintIsWhiteAndEraseIsBlack()
        {
            var g = PolygonFillCube(); var d = window.Document; var layer = window.SelectedLayer; var p0 = UvCentroidPixel(g, 0);
            d.AddLayerMask(layer); window.EditMask = true; Repaint(window);
            var mask = d.GetLayer(layer).Mask.Surface;
            Key(window, KeyCode.X); Assert.That(window.PolygonFillErase, Is.True, "X swaps white and black on a mask");
            Click(At(window, p0.x, p0.y)); Assert.That(mask.GetPixel(p0.x, p0.y).A, Is.EqualTo(255), "black hides");
            Key(window, KeyCode.X); Assert.That(window.PolygonFillErase, Is.False);
            Click(At(window, p0.x, p0.y)); Assert.That(mask.GetPixel(p0.x, p0.y).A, Is.Zero, "white shows");
            // 反転したマスクでは、白（見せる）は隠す量 255 を書く（サムネイルで白）
            d.SetLayerMaskInverted(layer, true);
            Click(At(window, p0.x, p0.y)); Assert.That(mask.GetPixel(p0.x, p0.y).A, Is.EqualTo(255));
            Assert.That(d.GetLayer(layer).GetChannel(PaintChannel.Color).TileCount, Is.Zero, "the layer's own pixels were never touched");
            // マスクの編集をやめると X は描画色と背景色の入れ替えに戻る
            window.EditMask = false; var color = window.Brush.color; Key(window, KeyCode.X);
            Assert.That(window.Brush.color, Is.Not.EqualTo(color)); Assert.That(window.PolygonFillErase, Is.False);
        }

        [Test] public void ThePointerHighlightFollowsTheRegionAndIsLookedUpOnlyWhenItChanges()
        {
            var g = PolygonFillCube(); var index = new SurfaceRegionIndex(g);
            window.Preview.FrameRateLimit = 0; // 光らせる物の表示は 3D を描くときに合わせる。1/60 秒の上限で描くのが後回しになると、前の状態を読んでしまう
            var center = window.SurfaceRect.center;
            Assert.That(window.Preview.TryPick(window.SurfaceRect, center, out var hit), Is.True);
            Mouse(window, EventType.MouseMove, center); Repaint(window);
            Assert.That(window.PolygonFillHoverKey, Is.EqualTo(index.Key(hit.TriangleIndex, SurfaceRegionKind.UvIsland)));
            if (Shader.Find("Hidden/Internal-Colored") != null)
            {
                Assert.That(window.Preview.RegionHighlightVisible, Is.True);
                Assert.That(window.Preview.RegionHighlightTriangleCount, Is.EqualTo(2), "one face of the demo cube");
            }
            Assert.That(window.PolygonFillHoverOutline.Length, Is.EqualTo(8), "the island's 4 UV edges for the 2D canvas");
            // 同じ範囲の中で動いても引き直さない（三角形が変わっても範囲は同じ）
            int lookups = window.PolygonFillHoverLookups; int builds = window.Preview.RegionHighlightBuilds;
            for (int i = 1; i <= 10; i++) { Mouse(window, EventType.MouseMove, center + new Vector2(i, i * .5f)); Repaint(window); }
            Assert.That(window.PolygonFillHoverLookups, Is.EqualTo(lookups)); Assert.That(window.Preview.RegionHighlightBuilds, Is.EqualTo(builds));
            // 範囲の種類を変えると次の描画で合わせる（マテリアル = 12 三角形）
            window.SurfacePick = SurfaceRegionKind.Material; Repaint(window);
            Assert.That(window.PolygonFillHoverKey, Is.EqualTo(index.Key(hit.TriangleIndex, SurfaceRegionKind.Material)));
            if (Shader.Find("Hidden/Internal-Colored") != null) Assert.That(window.Preview.RegionHighlightTriangleCount, Is.EqualTo(12));
            // 2D キャンバスの上でも同じ（UV の点の三角形の範囲）
            window.SurfacePick = SurfaceRegionKind.Triangle; var p = UvCentroidPixel(g, 5);
            Mouse(window, EventType.MouseMove, At(window, p.x, p.y)); Repaint(window);
            Assert.That(window.PolygonFillHoverKey, Is.EqualTo(index.Key(5, SurfaceRegionKind.Triangle)));
            Assert.That(window.PolygonFillHoverOutline.Length, Is.EqualTo(6));
            // 何も無い所・ほかのツール: 消える
            Mouse(window, EventType.MouseMove, At(window, 341, 256)); Assert.That(window.PolygonFillHoverKey, Is.EqualTo(-1));
            Mouse(window, EventType.MouseMove, At(window, p.x, p.y)); Assert.That(window.PolygonFillHoverKey, Is.Not.EqualTo(-1));
            window.Tool = TexturePaintWindow.PaintTool.Brush; Repaint(window);
            Assert.That(window.PolygonFillHoverKey, Is.EqualTo(-1)); Assert.That(window.Preview.RegionHighlightVisible, Is.False);
        }

        [Test] public void TheFourKeyPicksPolygonFillAndTheShortcutGuardKnowsIt()
        {
            window.Tool = TexturePaintWindow.PaintTool.Brush;
            PainterSettings.UpdatePersonal(s => s.shortcutGuard = ShortcutGuardMode.BlockPainterKeys);
            var down = new Event { type = EventType.KeyDown, keyCode = KeyCode.Alpha4 };
            window.SendEvent(new Event(down));
            Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.PolygonFill));
            Assert.That(ShortcutGuard.Filter(new Event(down), window), Is.True, "the 4 the window used does not reach Unity's shortcuts");
            window.Tool = TexturePaintWindow.PaintTool.Brush;
            window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Keypad4 });
            Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.PolygonFill), "the keypad 4 too");
            window.Tool = TexturePaintWindow.PaintTool.Brush;
            window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Alpha4, modifiers = EventModifiers.Shift });
            Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.Brush), "Shift+4 is not the tool");
        }

        [Test] public void WithoutAModelThePolygonFillSaysWhatItNeeds()
        {
            window.Tool = TexturePaintWindow.PaintTool.PolygonFill; var before = Snapshot(); int steps = window.Document.UndoCount;
            Click(At(window, 300, 300));
            Assert.That(window.StatusMessage, Does.Contain("No model")); Assert.That(window.IsStroking, Is.False);
            Assert.That(Snapshot(), Is.EqualTo(before)); Assert.That(window.Document.UndoCount, Is.EqualTo(steps));
        }
    }
}
