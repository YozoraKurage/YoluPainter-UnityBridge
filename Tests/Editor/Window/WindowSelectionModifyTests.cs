using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウの選択範囲の変更（拡張・縮小・境界・ぼかし・鋭く）。それぞれ 1 回の Undo で戻り、選択範囲が無ければ何もしない。</summary>
    public sealed partial class WindowTests
    {
        [Test] public void SelectionModifyGrowsShrinksFeathersAndUndoes()
        {
            var d = window.Document;
            window.ModifySelection(TexturePaintWindow.SelectionModifyKind.Grow);
            Assert.That(window.StatusMessage, Does.Contain("Nothing is selected")); Assert.That(d.Selection, Is.Null);
            window.Tool = TexturePaintWindow.PaintTool.SelectRectangle;
            Drag(100, 100, 200, 200);
            Assert.That(d.Selection, Is.Not.Null, window.StatusMessage);
            int steps = d.UndoCount;
            window.SelectionRadius = 10;
            window.ModifySelection(TexturePaintWindow.SelectionModifyKind.Grow);
            Assert.That(window.StatusMessage, Does.Contain("Grow by 10 px"));
            Assert.That(d.Selection[95, 150], Is.EqualTo(255)); Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            window.ModifySelection(TexturePaintWindow.SelectionModifyKind.Feather);
            Assert.That(d.Selection[91, 150], Is.GreaterThan(0).And.LessThan(255), "feathered edge");
            window.ModifySelection(TexturePaintWindow.SelectionModifyKind.Sharpen);
            Assert.That(d.Selection[91, 150], Is.EqualTo(0).Or.EqualTo(255));
            window.ModifySelection(TexturePaintWindow.SelectionModifyKind.Shrink);
            window.ModifySelection(TexturePaintWindow.SelectionModifyKind.Border);
            Assert.That(d.Selection[150, 150], Is.Zero, "the border leaves the middle out");
            for (int i = 0; i < 5; i++) Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(d.Selection[95, 150], Is.Zero); Assert.That(d.Selection[150, 150], Is.EqualTo(255), "back to the rectangle");
            window.SelectionRadius = 9999; Assert.That(window.SelectionRadius, Is.EqualTo(SelectionMask.MaxModifyRadius));
        }

        [Test] public void ClickingTheModelSelectsAnIslandAndTheBucketFillsIt()
        {
            var d = window.Document;
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            Repaint(window);
            var center = window.SurfaceRect.center;
            Assert.That(window.Preview.TryPick(window.SurfaceRect, center, out var hit), Is.True);
            window.Tool = TexturePaintWindow.PaintTool.SelectRectangle; window.SurfacePick = Yozolab.YoluPainter.Editor.Preview.SurfaceRegionKind.UvIsland;
            Mouse(window, EventType.MouseDown, center); Mouse(window, EventType.MouseUp, center);
            Assert.That(d.Selection, Is.Not.Null, window.StatusMessage);
            int px = Mathf.FloorToInt(hit.UV.x * d.Width), py = Mathf.FloorToInt(hit.UV.y * d.Height);
            Assert.That(d.Selection[px, py], Is.EqualTo(255), "the clicked point is inside the selected island");
            int selected = 0; for (int y = 0; y < d.Height; y += 8) for (int x = 0; x < d.Width; x += 8) if (d.Selection[x, y] > 0) selected++;
            Assert.That(selected, Is.LessThan(d.Width / 8 * d.Height / 8), "an island, not the whole texture");
            d.ClearSelection(); d.ClearHistory();
            window.Tool = TexturePaintWindow.PaintTool.Fill;
            Mouse(window, EventType.MouseDown, center); Mouse(window, EventType.MouseUp, center);
            Assert.That(window.StatusMessage, Does.Contain("Filled"));
            Assert.That(d.CompositePixel(PaintChannel.Color, px, py).A, Is.EqualTo(255));
            Assert.That(d.UndoCount, Is.GreaterThanOrEqualTo(1));
            window.Tool = TexturePaintWindow.PaintTool.Gradient;
            Mouse(window, EventType.MouseDown, center); Mouse(window, EventType.MouseUp, center);
            Assert.That(window.StatusMessage, Does.Contain("works on the 2D canvas"));
        }
    }
}
