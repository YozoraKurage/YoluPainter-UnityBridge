using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 描画ウィンドウ: 窓の Repaint を何度送っても、3D ビューの絵が変わらなければ 3D は描かない（前は Repaint のたびに描き、ほかの窓につられた
    /// 描き直しでも GPU を使っていた）。ホイールでカメラを動かす・キャンバスに描くと描く。ステータスバーに描き直しの回数を出せる。
    /// </summary>
    public sealed partial class WindowTests
    {
        [Test] public void RepaintsWithoutChangesDoNotDrawThe3DView()
        {
            window.Preview.LoadDemoMesh(); window.Preview.FrameRateLimit = 0;
            Repaint(window); Repaint(window);
            int renders = window.Preview.RenderCount, repaints = window.WindowRepaints;
            Assert.That(renders, Is.GreaterThan(0), "the 3D view was drawn once");
            for (int i = 0; i < 30; i++) Repaint(window);
            Assert.That(window.WindowRepaints, Is.GreaterThanOrEqualTo(repaints + 30));
            Assert.That(window.Preview.RenderCount, Is.EqualTo(renders), "30 repaints of the window reuse the 3D picture");
            // ホイールでカメラを近づけると描く
            var at = window.SurfaceRect.center + window.rootVisualElement.worldBound.position;
            window.SendEvent(new Event { type = EventType.ScrollWheel, mousePosition = at, delta = new Vector2(0, -3) });
            Repaint(window);
            Assert.That(window.Preview.RenderCount, Is.EqualTo(renders + 1), "the camera moved");
            for (int i = 0; i < 10; i++) Repaint(window);
            Assert.That(window.Preview.RenderCount, Is.EqualTo(renders + 1));
            // キャンバスに描くと、合成した中身を渡すので描く
            var point = At(window, 300, 300);
            Mouse(window, EventType.MouseDown, point); Mouse(window, EventType.MouseUp, point);
            Repaint(window);
            Assert.That(window.Preview.RenderCount, Is.GreaterThan(renders + 1), "the painted texture changed");
            int afterPaint = window.Preview.RenderCount;
            for (int i = 0; i < 10; i++) Repaint(window);
            Assert.That(window.Preview.RenderCount, Is.EqualTo(afterPaint));
            // 確かめる表示
            window.ShowRedrawRate = true; Repaint(window);
            Assert.That(window.WindowRepaints, Is.GreaterThan(repaints));
        }
    }
}
