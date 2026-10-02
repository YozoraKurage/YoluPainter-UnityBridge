using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>パスツールを 2D キャンバスで: クリックで点を足し（新しい層）、ドラッグで動かし、Delete で消し、保存して開き直しても残る。</summary>
    public sealed partial class WindowTests
    {
        [Test] public void ThePathToolAddsMovesAndRemovesPointsOnTheCanvas()
        {
            var d = window.Document;
            window.Tool = TexturePaintWindow.PaintTool.Path;
            int layers = d.Layers.Count;
            Mouse(window, EventType.MouseDown, At(window, 100, 100)); Mouse(window, EventType.MouseUp, At(window, 100, 100));
            Assert.That(d.Layers.Count, Is.EqualTo(layers + 1), window.StatusMessage);
            var layer = d.GetLayer(window.SelectedLayer);
            Assert.That(layer.Path, Is.TypeOf<CanvasPath>());
            CanvasPath Path() => (CanvasPath)layer.Path;
            Mouse(window, EventType.MouseDown, At(window, 400, 100)); Mouse(window, EventType.MouseUp, At(window, 400, 100));
            Assert.That(Path().Points.Count, Is.EqualTo(2), window.StatusMessage);
            Assert.That(layer.GetChannel(PaintChannel.Color).GetPixel(250, 100).A, Is.GreaterThan(0), "the segment is drawn");
            // 点を掴んで動かす（増えない）
            Mouse(window, EventType.MouseDown, At(window, 100, 100)); Mouse(window, EventType.MouseDrag, At(window, 100, 300)); Mouse(window, EventType.MouseUp, At(window, 100, 300));
            Assert.That(Path().Points.Count, Is.EqualTo(2), "grabbing a point moves it instead of adding one");
            Assert.That(Path().Points[0].Y, Is.EqualTo(300.5).Within(2), window.StatusMessage);
            Key(window, KeyCode.Delete);
            Assert.That(Path().Points.Count, Is.EqualTo(1));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(Path().Points.Count, Is.EqualTo(2), "each edit is one undo step");
            // 保存して別のウィンドウで開いても同じパス
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            window.SaveProject(true);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File; other.OpenProject();
                var reopened = (CanvasPath)other.Document.GetLayer(layer.Id).Path;
                Assert.That(reopened.Points, Is.EqualTo(Path().Points), other.StatusMessage);
                Assert.That(DocumentBinary.Write(other.Document), Is.EqualTo(DocumentBinary.Write(d)));
            }
            finally { Close(other); }
            window.Tool = TexturePaintWindow.PaintTool.Brush;
            Mouse(window, EventType.MouseDown, At(window, 10, 10)); Mouse(window, EventType.MouseUp, At(window, 10, 10));
            Assert.That(window.StatusMessage, Does.Contain("drawn by a path"));
        }
    }
}
