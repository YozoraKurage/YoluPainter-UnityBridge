using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>実際の EditorWindow に SendEvent でマウス・キー入力を流し、CPU 正本の中身で確かめる。
    /// ウィンドウは batchmode では動かないのでスキップする（devcontainer では GUI モードの常駐で回す）。</summary>
    [Category("Window")]
    public sealed class WindowTests
    {
        TexturePaintWindow window;

        [SetUp] public void OpenWindow()
        {
            if (Application.isBatchMode) Assert.Ignore("EditorWindow input needs a non-batch Editor (test-daemon.sh start in GUI mode).");
            // devcontainer の GUI モードは組み込みシェーダーすらコンパイルできず、描画のたびにエラーを
            // ログする。エディタ側の問題なので、その状態のときだけログで落とさない。
            if (ShaderUtil.ShaderHasError(Shader.Find("Hidden/BlitCopy"))) LogAssert.ignoreFailingMessages = true;
            window = Open();
        }

        [TearDown] public void CloseWindow() { Close(window); window = null; }

        static TexturePaintWindow Open()
        {
            var w = EditorWindow.CreateWindow<TexturePaintWindow>();
            w.position = new Rect(40, 40, 1200, 800);
            w.SendEvent(new Event { type = EventType.Repaint }); // OnGUI でキャンバスと 3D の矩形を決めさせる
            return w;
        }

        static void Close(TexturePaintWindow w)
        {
            if (w == null) return;
            string recovery = w.RecoveryRoot;
            w.Close();
            if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
        }

        /// <summary>ピクセルの GUI 座標。直前に Repaint してレイアウトを最新にしておく（ウィンドウマネージャが
        /// 大きさを変えることがある）。</summary>
        static Vector2 At(TexturePaintWindow w, int x, int y)
        { w.SendEvent(new Event { type = EventType.Repaint }); return w.PixelToGui(x, y); }

        /// <summary>SendEvent の座標はタブを含むホスト側の座標として扱われ、ウィンドウに届く前にタブの
        /// 高さぶん引かれる。実際のマウス入力と同じ位置に届くよう、その分を足して送る。</summary>
        static void Mouse(EditorWindow w, EventType type, Vector2 position, float pressure = 1)
        { w.SendEvent(new Event { type = type, mousePosition = position + w.rootVisualElement.worldBound.position, button = 0, pressure = pressure }); }

        static void Key(EditorWindow w, KeyCode key, EventModifiers modifiers = EventModifiers.None)
        { w.SendEvent(new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers }); }

        static void Invoke(object target, string method, params object[] args)
        { target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args); }

        byte[] Snapshot() => window.Document.Composite(PaintChannel.Color);

        void BeginLine(int x, int y)
        {
            Mouse(window, EventType.MouseDown, At(window, x, y));
            Mouse(window, EventType.MouseDrag, At(window, x + 60, y));
            Assert.That(window.IsStroking, Is.True);
        }

        [Test] public void DotLandsUnderThePointerWithBottomLeftOrigin()
        {
            var at = At(window, 300, 700);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(window.IsStroking, Is.False);
            Assert.That(window.Document.CompositePixel(PaintChannel.Color, 300, 700).A, Is.GreaterThan((byte)0));
            Assert.That(window.Document.CompositePixel(PaintChannel.Color, 300, 1023 - 700).A, Is.Zero, "Y must not be flipped");
            Assert.That(window.Document.CanUndo, Is.True);
        }

        [Test] public void UndoAndRedoShortcutsRestoreExactPixels()
        {
            var before = Snapshot();
            BeginLine(200, 200); Mouse(window, EventType.MouseUp, At(window, 260, 200));
            var painted = Snapshot();
            Assert.That(painted, Is.Not.EqualTo(before));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(Snapshot(), Is.EqualTo(before));
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(Snapshot(), Is.EqualTo(painted));
        }

        [Test] public void EscapeCancelsTheActiveStroke()
        {
            var before = Snapshot();
            BeginLine(400, 400);
            Key(window, KeyCode.Escape);
            Assert.That(window.IsStroking, Is.False);
            Assert.That(Snapshot(), Is.EqualTo(before));
            Assert.That(window.Document.CanUndo, Is.False);
            Mouse(window, EventType.MouseUp, At(window, 460, 400)); // 取消後の離しは何も起こさない
            Assert.That(Snapshot(), Is.EqualTo(before));
        }

        [TestCase("OnLostFocus")]
        [TestCase("BeforeReload")]
        public void LifecycleEventsCancelTheActiveStroke(string method)
        {
            var before = Snapshot();
            BeginLine(400, 400);
            Invoke(window, method);
            Assert.That(window.IsStroking, Is.False);
            Assert.That(Snapshot(), Is.EqualTo(before));
        }

        [Test] public void LeavingEditModeCancelsTheActiveStroke()
        {
            var before = Snapshot();
            BeginLine(400, 400);
            Invoke(window, "PlayModeChanged", PlayModeStateChange.ExitingEditMode);
            Assert.That(window.IsStroking, Is.False);
            Assert.That(Snapshot(), Is.EqualTo(before));
        }

        [Test] public void ReleaseOutsideTheWindowStillCommits()
        {
            BeginLine(100, 100);
            Mouse(window, EventType.MouseUp, new Vector2(-300, -300));
            Assert.That(window.IsStroking, Is.False);
            Assert.That(window.Document.CanUndo, Is.True);
            Assert.That(window.Document.CompositePixel(PaintChannel.Color, 130, 100).A, Is.GreaterThan((byte)0));
        }

        [Test] public void ZeroPressureWithPressureSizeLeavesNoPaint()
        {
            var before = Snapshot();
            var at = At(window, 500, 500);
            Mouse(window, EventType.MouseDown, at, 0); Mouse(window, EventType.MouseDrag, At(window, 540, 500), 0); Mouse(window, EventType.MouseUp, at, 0);
            Assert.That(Snapshot(), Is.EqualTo(before));
        }

        [Test] public void DemoCubeSurfaceStrokePaintsTheDocument()
        {
            Assert.That(window.Preview.LoadDemoMesh().CanPaint, Is.True);
            window.SendEvent(new Event { type = EventType.Repaint });
            var center = window.SurfaceRect.center;
            Assert.That(window.Preview.TryPick(window.SurfaceRect, center, out _), Is.True, "the default camera must see the demo cube at the 3D view center");
            Mouse(window, EventType.MouseDown, center);
            Mouse(window, EventType.MouseDrag, center + new Vector2(6, 0));
            Mouse(window, EventType.MouseUp, center + new Vector2(6, 0));
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            Assert.That(window.Document.CanUndo, Is.True, window.StatusMessage);
            Assert.That(Snapshot().Where((b, i) => i % 4 == 3).Any(a => a > 0), Is.True, window.StatusMessage);
        }

        [Test] public void WindowsKeepIndependentDocuments()
        {
            var other = Open();
            try
            {
                var otherBefore = other.Document.Composite(PaintChannel.Color);
                BeginLine(300, 300); Mouse(window, EventType.MouseUp, At(window, 360, 300));
                Assert.That(window.Document.CanUndo, Is.True);
                Assert.That(other.Document.Composite(PaintChannel.Color), Is.EqualTo(otherBefore));
                Assert.That(other.Document.CanUndo, Is.False);
            }
            finally { Close(other); }
        }

        [Test] public void ClosingReleasesPreviewAndGpuResources()
        {
            Close(window); window = null;
            int textures = Resources.FindObjectsOfTypeAll<RenderTexture>().Length;
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            var w = Open();
            try
            {
                w.Preview.LoadDemoMesh(); w.SendEvent(new Event { type = EventType.Repaint });
                Mouse(w, EventType.MouseDown, At(w, 10, 10)); Mouse(w, EventType.MouseUp, At(w, 10, 10));
                w.SendEvent(new Event { type = EventType.Repaint });
            }
            finally { Close(w); }
            Assert.That(Resources.FindObjectsOfTypeAll<RenderTexture>().Length, Is.EqualTo(textures), "RenderTextures");
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Length, Is.EqualTo(meshes), "Meshes");
            Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(materials), "Materials");
        }
    }
}
