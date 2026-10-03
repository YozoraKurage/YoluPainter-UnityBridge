using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウのパスツール: 3D ビューのクリックで点を足し（新しい層を作る）、ドラッグで動かし、Delete で消す。パスの層は手で塗れない。</summary>
    public sealed partial class WindowTests
    {
        [Test] public void ThePathToolAddsMovesAndRemovesPointsOnTheModel()
        {
            var d = window.Document;
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            Repaint(window);
            window.Tool = TexturePaintWindow.PaintTool.Path;
            int layers = d.Layers.Count;
            var c = window.SurfaceRect.center;
            Mouse(window, EventType.MouseDown, c); Mouse(window, EventType.MouseUp, c);
            Assert.That(d.Layers.Count, Is.EqualTo(layers + 1), window.StatusMessage);
            var layer = d.GetLayer(window.SelectedLayer);
            Assert.That(layer.Name, Is.EqualTo("Path")); Assert.That(((SurfacePath)layer.Path).Points.Count, Is.EqualTo(1));
            Assert.That(d.Composite(PaintChannel.Color).Where((b, i) => i % 4 == 3).Any(a => a > 0), Is.True, "the first point paints a dab");
            var second = c + new Vector2(25, 10);
            Mouse(window, EventType.MouseDown, second); Mouse(window, EventType.MouseUp, second);
            Assert.That(((SurfacePath)layer.Path).Points.Count, Is.EqualTo(2), window.StatusMessage);
            var before = ((SurfacePath)layer.Path).Points[0];
            Mouse(window, EventType.MouseDown, c); Mouse(window, EventType.MouseDrag, c + new Vector2(-20, 0)); Mouse(window, EventType.MouseUp, c + new Vector2(-20, 0));
            Assert.That(((SurfacePath)layer.Path).Points.Count, Is.EqualTo(2), "grabbing a point moves it instead of adding one");
            Assert.That(((SurfacePath)layer.Path).Points[0], Is.Not.EqualTo(before), window.StatusMessage);
            Key(window, KeyCode.Delete);
            Assert.That(((SurfacePath)layer.Path).Points.Count, Is.EqualTo(1));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(((SurfacePath)layer.Path).Points.Count, Is.EqualTo(2), "each edit is one undo step");
            window.Tool = TexturePaintWindow.PaintTool.Brush;
            Mouse(window, EventType.MouseDown, At(window, 10, 10)); Mouse(window, EventType.MouseUp, At(window, 10, 10));
            Assert.That(window.StatusMessage, Does.Contain("drawn by a path"));
        }
    }
}
