using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
namespace Yozolab.YoluPainter.Tests
{
    public sealed partial class WindowTests
    {
        [UnityTest]
        public IEnumerator LargeModelDialogTransfersOneLoadAndMeasuresPostLoadStages()
        {
            using (var fixture = new BigMeshFixture())
            {
                IsolatedModelPreview loaded = null;
                var dialog = NewProjectWindow.Open(new NewProjectSettings { Model = fixture.Root, Template = ProjectTemplate.Pbr, Resolution = 512, Materials = new[] { 0, 1 } }, false,
                    s => { window.CreateProject(s); loaded = window.Preview; });
                try
                {
                    Repaint(dialog);
                    var original = (IsolatedModelPreview)typeof(NewProjectWindow).GetField("preview", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(dialog);
                    dialog.Accept();
                    Assert.That(loaded, Is.SameAs(original)); Assert.That(loaded.LoadCount, Is.EqualTo(1));
                    var clock = Stopwatch.StartNew();
                    while (loaded.IsPreparing && clock.Elapsed.TotalSeconds < 15) { loaded.PollPreparation(); yield return null; }
                    Assert.That(loaded.CanPaint, Is.True);
                    clock.Restart(); var index = new SurfaceRegionIndex(loaded.Geometry); double regions = clock.Elapsed.TotalMilliseconds;
                    clock.Restart(); index.Region(0, SurfaceRegionKind.UvIsland); double islands = clock.Elapsed.TotalMilliseconds;
                    clock.Restart(); index.Region(0, SurfaceRegionKind.MeshPart); double parts = clock.Elapsed.TotalMilliseconds;
                    clock.Restart(); var wire = TexturePaintWindow.UvEdges(loaded.Geometry.Triangles, 0, TexturePaintWindow.MaxUvWireframeEdges, out _); double uv = clock.Elapsed.TotalMilliseconds;
                    window.TextureSets[1].DisposeTextures();
                    clock.Restart(); var other = window.SetDisplay(window.TextureSets[1]); double composite = clock.Elapsed.TotalMilliseconds;
                    Invoke(window, "DisposeLighting"); Invoke(window, "BeginPreviewDisplayFrame", new object[] { null });
                    clock.Restart(); Invoke(window, "UpdatePreviewLighting"); double lighting = clock.Elapsed.TotalMilliseconds;
                    int before = loaded.RenderCount; clock.Restart(); loaded.FrameRateLimit = 0; Repaint(window); double frame = clock.Elapsed.TotalMilliseconds;
                    Assert.That(index, Is.Not.Null); Assert.That(wire, Is.Not.Empty); Assert.That(other, Is.Not.Null); Assert.That(loaded.RenderCount, Is.GreaterThan(before));
                    UnityEngine.Debug.Log($"GUI 読込み後の段（512 PBR/2 セット）: SurfaceRegionIndex={regions:F2} ms, UV島初回={islands:F2} ms, メッシュの塊初回={parts:F2} ms, UV線={uv:F2} ms, 他セットCPU合成={composite:F2} ms, 照明={lighting:F2} ms, 最初の窓描画={frame:F2} ms");
                }
                finally { if (dialog != null) dialog.Close(); }
            }
        }
        [Test]
        public void LargeModelDisplayPreparationHonorsFrameBudgetAndCancelResume()
        {
            using (var fixture = new BigMeshFixture())
            {
                window.CreateProject(new NewProjectSettings { Model = fixture.Root, Resolution = 512, Template = ProjectTemplate.ColorOnly, Materials = new[] { 0, 1, 2 } });
                window.Preview.Load(fixture.Root);
                foreach (var set in window.TextureSets) set.DisposeTextures();
                var before = Snapshot();
                Invoke(window, "BeginPreviewDisplayFrame", new CompositeSchedule { BudgetMilliseconds = 0 });
                Invoke(window, "ShowTextureSets");
                Assert.That(window.PreviewDisplayPending, Is.True); Assert.That(window.TextureSets[1].Display, Is.Null);
                window.CancelPreviewDisplayPreparation(); Assert.That(window.PreviewDisplayPending, Is.False);
                window.ResumePreviewDisplayPreparation(); window.RefreshPreviewTextures();
                Assert.That(window.PreviewDisplayPending, Is.False);
                foreach (var set in window.TextureSets.Skip(1))
                {
                    Assert.That(set.Display, Is.Not.Null);
                    Assert.That(set.Display.GetRawTextureData<byte>().ToArray(), Is.EqualTo(set.Document.Composite(PaintChannel.Color)));
                }
                Assert.That(Snapshot(), Is.EqualTo(before)); Assert.That(window.Document.CanUndo, Is.False);
            }
        }
        [UnityTest]
        public IEnumerator LargeModelYlpKeepsPrefabBindingAndNativePixels()
        {
            string folder = "Assets/SyntheticBigMesh-" + System.Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder)), Is.Not.Empty);
            using (var fixture = new BigMeshFixture())
            {
                try
                {
                    int i = 0;
                    foreach (var f in fixture.Root.GetComponentsInChildren<MeshFilter>())
                    {
                        var copy = UnityEngine.Object.Instantiate(f.sharedMesh); AssetDatabase.CreateAsset(copy, folder + "/Part" + i++ + ".asset"); f.sharedMesh = copy;
                    }
                    var prefab = PrefabUtility.SaveAsPrefabAsset(fixture.Root, folder + "/Model.prefab");
                    var dialog = NewProjectWindow.Open(new NewProjectSettings { Model = prefab, Resolution = 512, Template = ProjectTemplate.ColorOnly, Materials = new[] { 0 } }, false, s => window.CreateProject(s));
                    try { Repaint(dialog); dialog.Accept(); } finally { if (dialog != null) dialog.Close(); }
                    var clock = Stopwatch.StartNew();
                    while (window.Preview.IsPreparing && clock.Elapsed.TotalSeconds < 15) { window.Preview.PollPreparation(); yield return null; }
                    Assert.That(window.Preview.CanPaint, Is.True);
                    string fingerprint = SurfacePathRenderer.Fingerprint(window.Preview.Geometry); PaintDot(window, 100, 100); var pixels = Snapshot();
                    var fake = UseFakeDialogs(window); fake.File = NewYlpPath(); window.SaveProject(true);
                    var stored = YlpStore.Load(fake.File); string state = System.Text.Encoding.UTF8.GetString(stored.Files[YlpContent.ViewName]);
                    Assert.That(state, Does.Contain(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab))));
                    PaintDot(window, 200, 200); Assert.That(Snapshot(), Is.Not.EqualTo(pixels));
                    window.OpenProjectAt(fake.File);
                    Assert.That(window.Preview.CanPaint, Is.True); Assert.That(SurfacePathRenderer.Fingerprint(window.Preview.Geometry), Is.EqualTo(fingerprint));
                    Assert.That(Snapshot(), Is.EqualTo(pixels)); Assert.That(window.IsSaved, Is.True); Assert.That(window.Document.CanUndo, Is.False);
                }
                finally { AssetDatabase.DeleteAsset(folder); }
            }
        }

        [Test]
        public void LargeModelPreparationAllowsLayerUndoAnd2DPaintAndCancelsOnReload()
        {
            using (var fixture = new BigMeshFixture())
            {
                window.CreateProject(new NewProjectSettings { Model = fixture.Root, Resolution = 512, Template = ProjectTemplate.ColorOnly, Materials = new[] { 0 } });
                Assert.That(window.Preview.IsPreparing, Is.True);
                var before = Snapshot(); PaintDot(window, 100, 100);
                Assert.That(window.IsStroking, Is.False); Assert.That(Snapshot(), Is.Not.EqualTo(before));
                Key(window, KeyCode.Z, EventModifiers.Control); Assert.That(Snapshot(), Is.EqualTo(before));
                var saved = window.Document; Invoke(window, "BeforeReload"); window.Preview.PollPreparation();
                Assert.That(window.Preview.IsPreparing, Is.False); Assert.That(window.Preview.Geometry, Is.Null); Assert.That(window.Document, Is.SameAs(saved)); Assert.That(window.IsStroking, Is.False);
            }
        }
        [Test]
        public void NewProjectReloadsWhenDialogModelChangesBeforeAccepting()
        {
            using (var fixture = new BigMeshFixture())
            {
                var dialog = NewProjectWindow.Open(new NewProjectSettings { Model = fixture.Root, Resolution = 512, Template = ProjectTemplate.ColorOnly, Materials = new[] { 0 } }, false, s => window.CreateProject(s));
                try
                {
                    Repaint(dialog);
                    var original = (IsolatedModelPreview)typeof(NewProjectWindow).GetField("preview", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(dialog);
                    var mesh = fixture.Root.GetComponentInChildren<MeshFilter>().sharedMesh; var vertices = mesh.vertices; vertices[0].z += .2f; mesh.vertices = vertices;
                    dialog.Accept();
                    Assert.That(window.Preview, Is.Not.SameAs(original));
                    Assert.That(original.Geometry, Is.Null);
                    Assert.That(window.Preview.LoadCount, Is.EqualTo(1), "新しい持ち主の 1 回。ダイアログの読込みは引き継がない");
                    Assert.That(window.Preview.SourceFingerprint, Is.EqualTo(PreviewSourceFingerprint.Of(fixture.Root)));
                }
                finally { if (dialog != null) dialog.Close(); }
            }
        }
    }
}
