using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

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
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            window = Open();
        }

        [TearDown] public void CloseWindow() { Close(window); window = null; }

        static TexturePaintWindow Open()
        {
            // ウィンドウの生成・破棄をまたぐと LogAssert.ignoreFailingMessages が戻ることがある（実測）ので毎回入れ直す。
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            var w = EditorWindow.CreateWindow<TexturePaintWindow>();
            w.position = new Rect(40, 40, 1200, 800);
            Repaint(w); // OnGUI でキャンバスと 3D の矩形を決めさせる
            return w;
        }

        static void Close(TexturePaintWindow w)
        {
            if (w == null) return;
            string recovery = w.RecoveryRoot;
            w.Close();
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
        }

        /// <summary>ピクセルの GUI 座標。直前に Repaint してレイアウトを最新にしておく（ウィンドウマネージャが
        /// 大きさを変えることがある）。</summary>
        static Vector2 At(TexturePaintWindow w, int x, int y)
        { Repaint(w); return w.PixelToGui(x, y); }

        static void Repaint(EditorWindow w)
        { EditorShaderCompiler.TolerateErrorLogsIfBroken(); w.SendEvent(new Event { type = EventType.Repaint }); }

        /// <summary>SendEvent の座標はタブを含むホスト側の座標として扱われ、ウィンドウに届く前にタブの
        /// 高さぶん引かれる。実際のマウス入力と同じ位置に届くよう、その分を足して送る。</summary>
        static void Mouse(EditorWindow w, EventType type, Vector2 position, float pressure = 1)
        { EditorShaderCompiler.TolerateErrorLogsIfBroken(); w.SendEvent(new Event { type = type, mousePosition = position + w.rootVisualElement.worldBound.position, button = 0, pressure = pressure }); }

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
            Repaint(window);
            var center = window.SurfaceRect.center;
            Assert.That(window.Preview.TryPick(window.SurfaceRect, center, out _), Is.True, "the default camera must see the demo cube at the 3D view center");
            Mouse(window, EventType.MouseDown, center);
            Mouse(window, EventType.MouseDrag, center + new Vector2(6, 0));
            Mouse(window, EventType.MouseUp, center + new Vector2(6, 0));
            Assert.That(window.IsStroking, Is.False, window.StatusMessage);
            Assert.That(window.Document.CanUndo, Is.True, window.StatusMessage);
            Assert.That(Snapshot().Where((b, i) => i % 4 == 3).Any(a => a > 0), Is.True, window.StatusMessage);
        }

        [Test] public void PaintingASceneModelLeavesTheSourceUntouched()
        {
            // Standard シェーダーは使わない（GUI モードの devcontainer ではシーンビューの描画でコンパイルエラーを
            // ログし、このテストの主題と無関係に落ちる）。組み込みのキューブメッシュ＋独自マテリアルで足りる。
            var source = new GameObject("Painter source model");
            source.transform.position = new Vector3(3, 4, 5);
            var mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            source.AddComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(Shader.Find("Hidden/YoluPainter/PreviewSurface")) { mainTexture = Texture2D.grayTexture, color = new Color(.3f, .6f, .9f) };
            var renderer = source.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            var vertices = mesh.vertices; var uvs = mesh.uv; var mainTexture = material.mainTexture; var color = material.color;
            int materialDirty = EditorUtility.GetDirtyCount(material), meshDirty = EditorUtility.GetDirtyCount(mesh), objectDirty = EditorUtility.GetDirtyCount(source);
            int components = source.GetComponents<Component>().Length;
            try
            {
                var report = window.Preview.Load(source);
                Assert.That(report.CanPaint, Is.True, string.Join("; ", report.Diagnostics));
                Repaint(window);
                var center = window.SurfaceRect.center;
                Mouse(window, EventType.MouseDown, center); Mouse(window, EventType.MouseDrag, center + new Vector2(8, 4)); Mouse(window, EventType.MouseUp, center + new Vector2(8, 4));
                Repaint(window);
                Assert.That(window.Document.CanUndo, Is.True, "the surface stroke must have painted: " + window.StatusMessage);

                Assert.That(renderer.sharedMaterial, Is.SameAs(material));
                Assert.That(material.mainTexture, Is.SameAs(mainTexture));
                Assert.That(material.color, Is.EqualTo(color));
                Assert.That(source.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(mesh));
                Assert.That(mesh.vertices, Is.EqualTo(vertices)); Assert.That(mesh.uv, Is.EqualTo(uvs));
                Assert.That(source.transform.position, Is.EqualTo(new Vector3(3, 4, 5)));
                Assert.That(source.GetComponents<Component>().Length, Is.EqualTo(components));
                Assert.That(EditorUtility.GetDirtyCount(material), Is.EqualTo(materialDirty), "material dirty count");
                Assert.That(EditorUtility.GetDirtyCount(mesh), Is.EqualTo(meshDirty), "mesh dirty count");
                Assert.That(EditorUtility.GetDirtyCount(source), Is.EqualTo(objectDirty), "GameObject dirty count");
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(material); }
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
                w.Preview.LoadDemoMesh(); Repaint(w);
                Mouse(w, EventType.MouseDown, At(w, 10, 10)); Mouse(w, EventType.MouseUp, At(w, 10, 10));
                Repaint(w);
            }
            finally { Close(w); }
            Assert.That(Resources.FindObjectsOfTypeAll<RenderTexture>().Length, Is.EqualTo(textures), "RenderTextures");
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Length, Is.EqualTo(meshes), "Meshes");
            Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(materials), "Materials");
        }
    }
}
