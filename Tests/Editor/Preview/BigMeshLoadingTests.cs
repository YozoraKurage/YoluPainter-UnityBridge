using System.Collections;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using System.IO;
using UnityEngine.TestTools;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;
namespace Yozolab.YoluPainter.Tests
{
    public sealed class BigMeshLoadingTests
    {
        [SetUp] public void Setup() => EditorShaderCompiler.TolerateErrorLogsIfBroken();
        [UnityTest]
        public IEnumerator BackgroundBuildShowsSnapshotButRejectsPaintingUntilReady()
        {
            using (var fixture = new BigMeshFixture()) using (var preview = new IsolatedModelPreview())
            {
                preview.BeginLoad(fixture.Root); Assert.That(preview.IsPreparing, Is.True); Assert.That(preview.HasSnapshot, Is.True);
                Assert.That(preview.Geometry, Is.Null); Assert.That(preview.CanPaint, Is.False);
                preview.ViewFrom(90, 25);
                Assert.That(preview.TryPick(new Rect(0, 0, 200, 200), new Vector2(100, 100), out _), Is.False);
                var clock = Stopwatch.StartNew();
                while (preview.IsPreparing && clock.Elapsed.TotalSeconds < 15) { preview.PollPreparation(); yield return null; }
                Assert.That(preview.IsPreparing, Is.False); Assert.That(preview.CanPaint, Is.True); Assert.That(preview.Geometry.TriangleCount, Is.EqualTo(70000));
                Assert.That(preview.CameraYaw, Is.EqualTo(90)); Assert.That(preview.CameraPitch, Is.EqualTo(25));
            }
        }
        [Test]
        public void CancelReplaceAndDisposeNeverAdoptAnOldBuild()
        {
            using (var fixture = new BigMeshFixture())
            {
                var preview = new IsolatedModelPreview();
                preview.BeginLoad(fixture.Root); preview.CancelPreparation(); preview.PollPreparation();
                Assert.That(preview.Geometry, Is.Null); Assert.That(preview.HasSnapshot, Is.True); Assert.That(preview.PreparationCanceled, Is.True);
                preview.BeginLoad(fixture.Root); preview.LoadDemoMesh(); preview.PollPreparation();
                Assert.That(preview.Geometry.TriangleCount, Is.EqualTo(12)); Assert.That(preview.CanPaint, Is.True);
                preview.BeginLoad(fixture.Root); preview.Dispose(); preview.PollPreparation();
                Assert.That(preview.Geometry, Is.Null); Assert.That(preview.IsPreparing, Is.False);
            }
        }
        [TestCase(false)] [TestCase(true)]
        public void UnreadableStaticAndSkinnedMeshesLoadWithoutChangingSource(bool skinned)
        {
            using (var fixture = new BigMeshFixture(skinned)) using (var preview = new IsolatedModelPreview())
            {
                var meshes = skinned ? fixture.Root.GetComponentsInChildren<SkinnedMeshRenderer>().Select(s => s.sharedMesh).ToArray()
                    : fixture.Root.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh).ToArray();
                var reference = preview.Load(fixture.Root); var fingerprint = SurfacePathRenderer.Fingerprint(preview.Geometry);
                foreach (var mesh in meshes) mesh.UploadMeshData(true);
                var report = preview.Load(fixture.Root);
                Assert.That(report.CanPaint, Is.True, string.Join("\n", report.Diagnostics)); Assert.That(report.TriangleCount, Is.EqualTo(reference.TriangleCount));
                Assert.That(SurfacePathRenderer.Fingerprint(preview.Geometry), Is.EqualTo(fingerprint));
                Assert.That(meshes.All(m => !m.isReadable), Is.True);
            }
        }
        [Test]
        public void ImportedUnreadableMeshPreservesImportSettingsAndAssetBytes()
        {
            string path = "Assets/SyntheticReadOnly-" + System.Guid.NewGuid().ToString("N") + ".obj";
            File.WriteAllText(path, "v 0 0 0\nv 0 1 0\nv 1 0 0\nvt 0 0\nvt 0 1\nvt 1 0\nf 1/1 2/2 3/3\n");
            try
            {
                AssetDatabase.ImportAsset(path); var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = false; importer.SaveAndReimport();
                string settings = EditorJsonUtility.ToJson(importer); byte[] bytes = File.ReadAllBytes(path);
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path); var mesh = model.GetComponentInChildren<MeshFilter>().sharedMesh;
                Assert.That(mesh.isReadable, Is.False);
                using (var preview = new IsolatedModelPreview()) Assert.That(preview.Load(model).CanPaint, Is.True, string.Join("\n", preview.Diagnostics));
                Assert.That(mesh.isReadable, Is.False); Assert.That(EditorJsonUtility.ToJson(importer), Is.EqualTo(settings)); Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }
        [Test]
        public void UnsupportedTopologyDoesNotAllowPaintingThroughMissingGeometry()
        {
            var source = new GameObject("Synthetic unsupported topology"); var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.up } };
            try
            {
                mesh.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0); source.AddComponent<MeshFilter>().sharedMesh = mesh; source.AddComponent<MeshRenderer>();
                using (var preview = new IsolatedModelPreview()) Assert.That(preview.Load(source).CanPaint, Is.False);
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(mesh); }
        }
        [TestCase(false)] [TestCase(true)]
        public void BudgetsStillRejectReadableAndUnreadableMeshes(bool unreadable)
        {
            using (var fixture = new BigMeshFixture()) using (var preview = new IsolatedModelPreview())
            {
                if (unreadable) foreach (var f in fixture.Root.GetComponentsInChildren<MeshFilter>()) f.sharedMesh.UploadMeshData(true);
                var report = preview.Load(fixture.Root, new PreviewLoadOptions { MaxVerticesPerMesh = 10 });
                Assert.That(report.Success, Is.False); Assert.That(preview.CanPaint, Is.False); Assert.That(report.Diagnostics.Any(s => s.Contains("vertex budget")), Is.True);
                report = preview.Load(fixture.Root, new PreviewLoadOptions { MaxTriangles = 100 });
                Assert.That(report.Success, Is.False); Assert.That(preview.CanPaint, Is.False);
            }
        }
        [Test]
        public void PreparedOwnershipTransfersExactlyOnceAndFingerprintDetectsChanges()
        {
            using (var fixture = new BigMeshFixture())
            {
                var preview = new IsolatedModelPreview(); preview.Load(fixture.Root);
                using (var prepared = new PreparedModel(preview))
                using (var taken = prepared.Take(fixture.Root))
                { Assert.That(taken, Is.SameAs(preview)); Assert.That(taken.LoadCount, Is.EqualTo(1)); Assert.That(prepared.Take(fixture.Root), Is.Null); }
                preview = new IsolatedModelPreview(); preview.Load(fixture.Root);
                using (var prepared = new PreparedModel(preview))
                {
                    var mesh = fixture.Root.GetComponentInChildren<MeshFilter>().sharedMesh; var vertices = mesh.vertices; vertices[0].z += .02f; mesh.vertices = vertices;
                    Assert.That(prepared.Take(fixture.Root), Is.Null); Assert.That(preview.Geometry, Is.Null);
                }
            }
        }
    }
}
