using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 形のグラデーションをシーンの物から写す（読むだけ。ウィンドウは表示せずに作るので batch でも動く）: BoxCollider・SphereCollider の
    /// 形と、Collider の無い物の位置・回転・大きさを、回して大きさを付けたモデルのルートからの相対で写す（ルートの大きさは掛けない）。
    /// 写すのは 1 回の Undo。シーンの物・ルート・Collider は 1 つも変わらず（値も SetDirty の数も）、シーンも汚れない。モデルがシーンに無い
    /// （デモのキューブ・プレハブのアセットを直接読んだ）・物がシーンに無い・選んでいないときは理由を出して何も変えない。試験の物は
    /// 開いているシーンに作って片付ける（作る・消すだけではシーンは汚れない）。
    /// </summary>
    public sealed class ShapeGradientSceneTests
    {
        TexturePaintWindow window; string project, folder;
        readonly List<Object> made = new List<Object>(); readonly List<TexturePaintWindow> windows = new List<TexturePaintWindow>();

        [SetUp] public void Create()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            project = Path.Combine(Path.GetTempPath(), "yolupainter-shape-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(project);
            PainterSettings.ProjectRoot = project;
            string name = "ShapeGradientSceneTests-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", name), Is.Not.Empty); folder = "Assets/" + name;
            window = NewWindow();
        }
        [TearDown] public void Clean()
        {
            foreach (var w in windows) { if (w == null) continue; string recovery = w.RecoveryRoot; Object.DestroyImmediate(w); if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true); }
            windows.Clear();
            foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            made.Clear();
            AssetDatabase.DeleteAsset(folder); PainterSettings.ProjectRoot = null;
            if (Directory.Exists(project)) Directory.Delete(project, true);
        }
        /// <summary>確かめにはすべて「はい」と答えるダイアログ（ファイルの窓は使わない）。</summary>
        sealed class AcceptingDialogs : IPainterDialogs
        {
            public string SaveFolder(string title, string folder, string defaultName) => "";
            public string OpenFolder(string title, string folder) => "";
            public string OpenFile(string title, string folder, string extension) => "";
            public string SaveFile(string title, string folder, string defaultName, string extension) => "";
            public bool Confirm(string title, string message, string ok, string cancel) => true;
            public void Inform(string title, string message) { }
            public void Progress(string title, string info, float progress) { }
            public void ClearProgress() { }
        }

        TexturePaintWindow NewWindow()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>(); w.MeshBakeProgress = (t, i, p) => false;
            windows.Add(w); return w;
        }
        T Made<T>(T o) where T : Object { made.Add(o); return o; }

        /// <summary>開いているシーンに置く板 1 枚のモデル（読めるメッシュ）。</summary>
        static Mesh Quad()
        {
            var mesh = new Mesh { name = "Shape test model" };
            mesh.SetVertices(new[] { new Vector3(-.5f, 0, -.5f), new Vector3(.5f, 0, -.5f), new Vector3(.5f, 1, .5f), new Vector3(-.5f, 1, .5f) });
            mesh.SetUVs(0, new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }); mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }
        GameObject SceneModel(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            var mesh = Made(Quad());
            var root = Made(new GameObject("Shape test model"));
            root.transform.SetPositionAndRotation(position, rotation); root.transform.localScale = scale;
            root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>();
            return root;
        }
        FilterEffect ShapeGradientOn(TexturePaintWindow w)
        {
            var d = w.Document;
            var fill = d.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(255, 255, 255, 255) } });
            d.AddLayerMask(fill.Id); d.ClearHistory(); w.SelectedLayer = fill.Id;
            var gen = w.AddGenerator(FilterTarget.Mask, GeneratorType.ShapeGradient);
            Assert.That(gen, Is.Not.Null, w.StatusMessage);
            return gen;
        }
        static ShapeVolume VolumeOf(TexturePaintWindow w, Guid id) => w.Document.FindFilter(w.SelectedLayer, id, out _).Settings.Generator.Volume;
        static void AssertVolume(ShapeVolume actual, GeneratorShape shape, Vector3 center, Vector3 euler, Vector3 size, string what)
        {
            Assert.That(actual.Shape, Is.EqualTo(shape), what);
            var c = new Vector3((float)actual.CenterX, (float)actual.CenterY, (float)actual.CenterZ); var s = new Vector3((float)actual.SizeX, (float)actual.SizeY, (float)actual.SizeZ);
            Assert.That(Vector3.Distance(c, center), Is.LessThan(1e-4f), what + ": centre " + c + " expected " + center);
            Assert.That(Quaternion.Angle(Quaternion.Euler((float)actual.RotationX, (float)actual.RotationY, (float)actual.RotationZ), Quaternion.Euler(euler)), Is.LessThan(.01f), what + ": rotation");
            Assert.That(Vector3.Distance(s, size), Is.LessThan(1e-4f), what + ": size " + s + " expected " + size);
            foreach (double a in new[] { actual.RotationX, actual.RotationY, actual.RotationZ }) Assert.That(a, Is.InRange(-180.0, 180.0), what + ": angles are kept within ±180");
        }

        [Test] public void CollidersAndTransformsAreCopiedRelativeToTheModelRootWithoutTouchingTheScene()
        {
            var rootRotation = Quaternion.Euler(0, 90, 0);
            var root = SceneModel(new Vector3(1, 2, 3), rootRotation, new Vector3(2, 2, 2));
            Assert.That(root.scene.IsValid(), Is.True, "the test objects are in the open scene");
            window.SetModel(root);
            Assert.That(window.Preview.HasModel, Is.True, window.StatusMessage);
            var gen = ShapeGradientOn(window); var d = window.Document;

            var boxSource = Made(new GameObject("Box source")); boxSource.transform.SetPositionAndRotation(new Vector3(1.5f, 2.5f, 2), Quaternion.Euler(10, 20, 30)); boxSource.transform.localScale = new Vector3(2, 1, .5f);
            var box = boxSource.AddComponent<BoxCollider>(); box.center = new Vector3(.1f, .2f, .3f); box.size = new Vector3(1, 2, 4);
            var sphereSource = Made(new GameObject("Sphere source")); sphereSource.transform.SetPositionAndRotation(new Vector3(0, 3, 3), Quaternion.Euler(0, 45, 0)); sphereSource.transform.localScale = new Vector3(1, 3, 1);
            var sphere = sphereSource.AddComponent<SphereCollider>(); sphere.center = new Vector3(0, .5f, 0); sphere.radius = .5f;
            var plainSource = Made(new GameObject("Plain source")); plainSource.transform.SetPositionAndRotation(new Vector3(1, 1, 3), Quaternion.Euler(0, 0, -20)); plainSource.transform.localScale = new Vector3(.5f, 1.5f, 2.5f);

            var scene = root.scene; bool sceneDirty = scene.isDirty;
            var watched = new Object[] { root, root.transform, boxSource, boxSource.transform, box, sphereSource, sphereSource.transform, sphere, plainSource, plainSource.transform };
            var dirtyCounts = watched.Select(EditorUtility.GetDirtyCount).ToArray();
            string Snapshot() => string.Join("|", new[] { root.transform, boxSource.transform, sphereSource.transform, plainSource.transform }.Select(t => t.position + " " + t.rotation + " " + t.localScale))
                + "|" + box.center + box.size + "|" + sphere.center + sphere.radius;
            string before = Snapshot();
            var inverse = Quaternion.Inverse(rootRotation);
            Vector3 Local(Vector3 world) => inverse * (world - new Vector3(1, 2, 3));
            Vector3 LocalEuler(Quaternion world) => (inverse * world).eulerAngles;

            int steps = d.UndoCount; var initial = VolumeOf(window, gen.Id);
            Assert.That(window.CopyShapeFromScene(gen.Id, boxSource), Is.True, window.StatusMessage);
            Assert.That(window.StatusMessage, Does.Contain("BoxCollider").And.Contain("not changed"));
            AssertVolume(VolumeOf(window, gen.Id), GeneratorShape.Box, Local(boxSource.transform.TransformPoint(box.center)), LocalEuler(boxSource.transform.rotation), new Vector3(2, 2, 2),
                "box collider (the root's scale 2 is not applied)");
            Assert.That(VolumeOf(window, gen.Id).Falloff, Is.EqualTo(initial.Falloff), "the falloff is kept");
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1), "one undo step");
            var copiedBox = VolumeOf(window, gen.Id);

            Assert.That(window.CopyShapeFromScene(gen.Id, sphereSource), Is.True, window.StatusMessage);
            AssertVolume(VolumeOf(window, gen.Id), GeneratorShape.Sphere, Local(sphereSource.transform.TransformPoint(sphere.center)), LocalEuler(sphereSource.transform.rotation), new Vector3(3, 3, 3),
                "sphere collider: the radius times the largest scale");
            // Collider の無い物: 今の形（球）のまま、直径は大きさのいちばん大きい軸
            Assert.That(window.CopyShapeFromScene(gen.Id, plainSource), Is.True, window.StatusMessage);
            Assert.That(window.StatusMessage, Does.Contain("Transform"));
            AssertVolume(VolumeOf(window, gen.Id), GeneratorShape.Sphere, Local(plainSource.transform.position), LocalEuler(plainSource.transform.rotation), new Vector3(2.5f, 2.5f, 2.5f), "plain transform as a sphere");
            var asBox = VolumeOf(window, gen.Id).WithShape(GeneratorShape.Box);
            var current = d.FindFilter(window.SelectedLayer, gen.Id, out _);
            window.ApplyFilterSettings(gen.Id, current.Settings.WithGenerator(current.Settings.Generator.WithVolume(asBox)));
            Assert.That(window.CopyShapeFromScene(gen.Id, plainSource), Is.True, window.StatusMessage);
            AssertVolume(VolumeOf(window, gen.Id), GeneratorShape.Box, Local(plainSource.transform.position), LocalEuler(plainSource.transform.rotation), new Vector3(.5f, 1.5f, 2.5f), "plain transform as a box");

            // シーンは読むだけ
            Assert.That(Snapshot(), Is.EqualTo(before), "no transform or collider value changed");
            Assert.That(watched.Select(EditorUtility.GetDirtyCount).ToArray(), Is.EqualTo(dirtyCounts), "nothing was marked dirty");
            Assert.That(scene.isDirty, Is.EqualTo(sceneDirty), "the scene is not dirtied");
            // 取り直しは 1 回ずつ Undo できる
            d.Undo(); d.Undo(); d.Undo(); d.Undo();
            Assert.That(VolumeOf(window, gen.Id), Is.EqualTo(copiedBox));
            d.Undo(); Assert.That(VolumeOf(window, gen.Id), Is.EqualTo(initial));
        }

        [Test] public void ModelsAndObjectsOutsideTheSceneAreRefusedWithAReason()
        {
            // デモのキューブ: シーンのルートが無い
            window.Preview.LoadDemoMesh();
            var gen = ShapeGradientOn(window); var d = window.Document; int steps = d.UndoCount; var initial = VolumeOf(window, gen.Id);
            var source = Made(new GameObject("Source")); source.AddComponent<BoxCollider>();
            Assert.That(window.CopyShapeFromScene(gen.Id, source), Is.False);
            Assert.That(window.StatusMessage, Does.Contain("no model from a scene"));
            // プレハブのアセットを直接読んだモデル
            var meshAsset = Quad(); AssetDatabase.CreateAsset(meshAsset, folder + "/Model.asset"); // アセットはフォルダごと消す
            var go = new GameObject("Prefab model"); go.AddComponent<MeshFilter>().sharedMesh = meshAsset; go.AddComponent<MeshRenderer>();
            GameObject prefab;
            try { prefab = PrefabUtility.SaveAsPrefabAsset(go, folder + "/Model.prefab"); }
            finally { Object.DestroyImmediate(go); }
            var other = NewWindow(); other.SetModel(prefab);
            Assert.That(other.Preview.HasModel, Is.True, other.StatusMessage);
            var otherGen = ShapeGradientOn(other); int otherSteps = other.Document.UndoCount;
            Assert.That(other.CopyShapeFromScene(otherGen.Id, source), Is.False);
            Assert.That(other.StatusMessage, Does.Contain("Prefab asset"));
            Assert.That(other.Document.UndoCount, Is.EqualTo(otherSteps));
            // シーンのモデルでも、シーンに無い物・選んでいないときは断る
            var root = SceneModel(Vector3.zero, Quaternion.identity, Vector3.one);
            window.Dialogs = new AcceptingDialogs(); // デモのキューブからの差し替えは確かめを出す（描いたものを保つ差し替え）。受ける
            Assert.That(window.ChangeModel(root), Is.True, window.StatusMessage);
            var hidden = Made(new GameObject("Not in a scene") { hideFlags = HideFlags.HideAndDontSave });
            Assert.That(hidden.scene.IsValid(), Is.False);
            Assert.That(window.CopyShapeFromScene(gen.Id, hidden), Is.False); Assert.That(window.StatusMessage, Does.Contain("not an object in an open scene"));
            Assert.That(window.CopyShapeFromScene(gen.Id, null), Is.False); Assert.That(window.StatusMessage, Does.Contain("Choose a scene object"));
            Assert.That(window.CopyShapeFromScene(gen.Id, prefab), Is.False, "an asset is not a scene object");
            Assert.That(d.UndoCount, Is.EqualTo(steps)); Assert.That(VolumeOf(window, gen.Id), Is.EqualTo(initial), "nothing changed");
            // 大きさ 0 の物は値の範囲で断る（何も変えない）
            var flat = Made(new GameObject("Flat")); flat.transform.localScale = Vector3.zero;
            Assert.That(window.CopyShapeFromScene(gen.Id, flat), Is.True, "sizes are clamped to the smallest allowed size");
            Assert.That(VolumeOf(window, gen.Id).SizeX, Is.EqualTo(ShapeVolume.MinSize));
            var far = Made(new GameObject("Far")); far.transform.position = new Vector3(3e6f, 0, 0);
            Assert.That(window.CopyShapeFromScene(gen.Id, far), Is.False); Assert.That(window.StatusMessage, Does.Contain("cannot be used"));
        }
    }
}
