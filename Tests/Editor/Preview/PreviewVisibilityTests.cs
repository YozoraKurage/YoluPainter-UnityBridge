using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class PreviewVisibilityTests
    {
        static readonly Vector3 Camera = new Vector3(.3f, .3f, -2);
        static readonly Ray Ray = new Ray(Camera, Vector3.forward);
        static SurfaceGeometry Plates()
        {
            SurfaceTriangle Plate(float z, int renderer) => new SurfaceTriangle(new Vector3(0, 0, z), new Vector3(0, 1, z), new Vector3(1, 0, z), Vector2.zero, Vector2.up, Vector2.right, renderer, renderer);
            return new SurfaceGeometry(new[] { Plate(0, 0), Plate(.5f, 1) });
        }
        [Test] public void VisibleSamplingKeepsSeamWeldingAndRefusesHiddenTriangles()
        {
            var full = new SurfaceGeometry(new[]
            {
                new SurfaceTriangle(Vector3.zero, Vector3.up, Vector3.right, Vector2.zero, Vector2.up, Vector2.right, 0, 0),
                new SurfaceTriangle(Vector3.right, Vector3.up, new Vector3(1, 1, 0), Vector2.right, Vector2.up, Vector2.one, 0, 0),
            });
            Assert.That(full.TryRaycast(new Ray(new Vector3(.25f, .25f, -2), Vector3.forward), out var hit), Is.True);
            Assert.That(full.BuildSamplingChart(hit, 2).TriangleCount, Is.EqualTo(2));
            Assert.That(full.VisibleView(t => true).BuildSamplingChart(hit, 2).TriangleCount, Is.EqualTo(2), "the view keeps the snapshot's seam tolerance");
            Assert.That(full.VisibleView(t => t.C.y < .5f).BuildSamplingChart(hit, 2).TriangleCount, Is.EqualTo(1));
            Assert.That(() => full.VisibleView(t => false).BuildSamplingChart(hit, 2), Throws.TypeOf<System.InvalidOperationException>());
        }
        [Test] public void HiddenFrontPassesRaysAndClosestPointsThroughToTheBack()
        {
            var full = Plates(); var visible = full.VisibleView(t => t.RendererIndex == 1);
            Assert.That(full.TryRaycast(Ray, out var front), Is.True); Assert.That(front.RendererIndex, Is.Zero);
            Assert.That(visible.TryRaycast(Ray, out var back), Is.True); Assert.That(back.RendererIndex, Is.EqualTo(1));
            Assert.That(visible.VisibleView(t => true).TryRaycast(Ray, out var stillBack), Is.True); Assert.That(stillBack.RendererIndex, Is.EqualTo(1), "a second query filter keeps the hidden faces hidden");
            Assert.That(visible.TryFindClosestPoint(front.Position, 1, Vector3.back, 100, out var closest, out var exceeded), Is.True);
            Assert.That(closest.TriangleIndex, Is.EqualTo(back.TriangleIndex)); Assert.That(exceeded, Is.False);
            Assert.That(visible.Triangles, Is.SameAs(full.Triangles)); Assert.That(visible.SnapshotRevision, Is.EqualTo(full.SnapshotRevision));
            Assert.That(full.VisibleView(t => false).TryRaycast(Ray, out _), Is.False);
            Assert.That(full.TryRaycast(Ray, out front), Is.True); Assert.That(front.RendererIndex, Is.Zero, "the bake snapshot remains complete");
        }
        [Test] public void HiddenFacesNeitherReceivePixelsNorOccludeTheBackAndCachedRaysAreInvalidated()
        {
            var full = Plates(); var cache = new SurfaceVisibilityCache();
            var backOnly = full.VisibleView(t => t.RendererIndex == 1);
            Assert.That(backOnly.TryRaycast(Ray, out var back), Is.True);
            var blocked = full.BuildSurfaceDabs(back, .15f, 64, 64, Camera, .8f, null, cache);
            Assert.That(blocked.Pixels, Is.Empty); Assert.That(cache.Count, Is.GreaterThan(0));
            var revealed = backOnly.BuildSurfaceDabs(back, .15f, 64, 64, Camera, .8f, null, cache);
            Assert.That(revealed.Pixels, Is.Not.Empty);
            Assert.That(revealed.Pixels.Select(p => (p.X, p.Y, p.Coverage)), Is.EqualTo(backOnly.BuildSurfaceDabsReference(back, .15f, 64, 64, Camera).Pixels.Select(p => (p.X, p.Y, p.Coverage))));
            Assert.That(full.TryRaycast(Ray, out var front), Is.True);
            Assert.That(backOnly.BuildSurfaceDabs(front, .15f, 64, 64, Camera).Pixels, Is.Empty, "an old hidden hit cannot paint");
            Assert.That(full.BuildSurfaceDabs(back, .15f, 64, 64, Camera, .8f, null, cache).Pixels, Is.Empty, "showing the occluder invalidates the rays again");
        }
        [Test] public void VisibleQueriesStillRefuseInvalidAndOverBudgetDabsWithoutPartialPixels()
        {
            var visible = Plates().VisibleView(t => t.RendererIndex == 1); visible.TryRaycast(Ray, out var hit);
            Assert.That(visible.BuildSurfaceDabs(hit, float.NaN, 64, 64, Camera).Pixels, Is.Empty);
            var refused = visible.BuildSurfaceDabs(hit, .3f, 64, 64, Camera, .8f, new SurfaceBrushBudget { MaxCandidatePixels = 1 });
            Assert.That(refused.WasClipped, Is.True); Assert.That(refused.Pixels, Is.Empty); Assert.That(refused.Diagnostic, Does.Contain("budget"));
        }
        [Test] public void VisibilityChangesRenderOnceAndHidingEverythingChangesPixels()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface");
            using (var p = new IsolatedModelPreview { FrameRateLimit = 0 })
            {
                p.LoadDemoMesh(); var full = p.Geometry; var view = new Rect(0, 0, 96, 96);
                var before = p.RenderStatic(96, 96); var bytes = before.GetPixels32(); Object.DestroyImmediate(before);
                p.RenderCached(view, 1); int count = p.RenderCount;
                p.SetVisibility(new[] { 0 }, null); p.RenderCached(view, 1); Assert.That(p.RenderCount, Is.EqualTo(count + 1));
                p.SetVisibility(new[] { 0 }, null); p.RenderCached(view, 1); Assert.That(p.RenderCount, Is.EqualTo(count + 1), "same flags reuse the picture");
                var hidden = p.RenderStatic(96, 96);
                Assert.That(hidden.GetPixels32(), Is.Not.EqualTo(bytes)); Object.DestroyImmediate(hidden);
                Assert.That(p.AnyVisible, Is.False); Assert.That(p.Geometry, Is.SameAs(full));
                p.SetVisibility(null, null); var restored = p.RenderStatic(96, 96);
                Assert.That(restored.GetPixels32(), Is.EqualTo(bytes)); Object.DestroyImmediate(restored);
                Assert.That(p.AnyVisible, Is.True);
            }
        }
        [Test] public void AVisibilityViewSharesTheLargeSnapshotAndItsCostIsRecorded()
        {
            const int side = 100; var triangles = new System.Collections.Generic.List<SurfaceTriangle>();
            for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
            {
                var a = new Vector3(x, y, 0); var b = a + Vector3.up; var c = a + Vector3.right; var d = b + Vector3.right;
                triangles.Add(new SurfaceTriangle(a, b, c, Vector2.zero, Vector2.up, Vector2.right, x % 2, 0));
                triangles.Add(new SurfaceTriangle(b, d, c, Vector2.up, Vector2.one, Vector2.right, x % 2, 0));
            }
            var full = new SurfaceGeometry(triangles); full.VisibleView(t => true); // 初回のJITを計測から外す
            long start = GC.GetAllocatedBytesForCurrentThread(); var clock = System.Diagnostics.Stopwatch.StartNew();
            var visible = full.VisibleView(t => t.RendererIndex == 0); clock.Stop(); long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.That(visible.Triangles, Is.SameAs(full.Triangles));
            string measured = "可視性の写し: " + full.TriangleCount + " 三角形 / " + clock.Elapsed.TotalMilliseconds.ToString("F3") + " ms / 呼出しスレッド " + bytes + " bytes（BVHと三角形は共有）。";
            TestContext.WriteLine(measured);
            string folder = System.IO.Path.GetFullPath(System.IO.Path.Combine("Logs", "YoluPainterSnapshots", "visibility")); System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "surface-cost.txt"), measured);
        }
        [TestCase(false)] [TestCase(true)] public void HiddenSymmetryCopiesStayHiddenWhenCameraVisibilityIsIgnored(bool ignoreCamera)
        {
            var full = new SurfaceGeometry(SurfaceRadialSymmetryTests.Petals(4, occluder: true));
            var visible = full.VisibleView(t => t.RendererIndex == 0 && (t.A + t.B + t.C).x / 3 > -.5f);
            var hit = SurfaceRadialSymmetryTests.Pick(visible, Vector3.right); var eye = new Vector3(0, 0, -5);
            var radial = new RadialSymmetry(Vector3.zero, Vector3.forward, 4);
            var result = SurfaceRadialSymmetry.Build(visible, hit, null, radial, ignoreCamera, .2f, 256, 128, eye).Result;
            Assert.That(result.WasClipped, Is.False, result.Diagnostic); Assert.That(result.Pixels, Is.Not.Empty);
            Assert.That(result.Pixels.Any(p => p.X >= 128 && p.X < 192), Is.False, "camera occlusion is separate from the eye's hidden surfaces");
            var all = SurfaceRadialSymmetry.Build(full, hit, null, radial, true, .2f, 256, 128, eye).Result;
            Assert.That(all.Pixels.Any(p => p.X >= 128 && p.X < 192), Is.True);
        }
        [Test] public void InvalidRendererOrSlotIsRefusedBeforeChangingVisibility()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            using (var p = new IsolatedModelPreview())
            {
                p.LoadDemoMesh(); p.SetVisibility(new[] { 0 }, null);
                Assert.That(() => p.SetVisibility(new[] { 1 }, null), Throws.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(() => p.SetVisibility(null, new[] { -1 }), Throws.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(p.AnyVisible, Is.False);
            }
        }
    }
}
