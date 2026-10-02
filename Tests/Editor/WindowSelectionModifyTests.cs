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
    }
}
