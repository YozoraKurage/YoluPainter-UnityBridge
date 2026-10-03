using System.Diagnostics;
using NUnit.Framework;
using Yozolab.YoluPainter.Editor.Preview;
namespace Yozolab.YoluPainter.Tests
{
    public sealed class BigMeshTests
    {
        [SetUp] public void Setup() => EditorShaderCompiler.TolerateErrorLogsIfBroken();
        [TestCase(false)] [TestCase(true)]
        public void MeasureSyntheticLargeModel(bool skinned)
        {
            using (var fixture = new BigMeshFixture(skinned)) using (var preview = new IsolatedModelPreview())
            {
                var clock = Stopwatch.StartNew(); var report = preview.Load(fixture.Root); double load = clock.Elapsed.TotalMilliseconds;
                Assert.That(report.CanPaint, Is.True); Assert.That(report.TriangleCount, Is.EqualTo(70000));
                Assert.That(report.LoadedRendererCount, Is.EqualTo(24)); Assert.That(preview.MaterialSlotCount, Is.EqualTo(32));
                Assert.That(preview.Geometry.NonManifoldEdgeCount, Is.GreaterThan(0));
                var copy = new System.Collections.Generic.List<SurfaceTriangle>(preview.Geometry.Triangles);
                clock.Restart(); var legacy = new LegacySurfaceGeometry(copy); double old = clock.Elapsed.TotalMilliseconds;
                UnityEngine.Debug.Log($"合成モデル skin={skinned}: Load={load:F2} ms, 旧幾何={old:F2} ms, 旧隣接={legacy.AdjacencyMs:F2} ms, 旧BVH={legacy.BvhMs:F2} ms, snapshot={preview.Timings.SnapshotMilliseconds:F2} ms, triangles={preview.Timings.TrianglesMilliseconds:F2} ms, 新幾何写し={preview.Timings.GeometrySnapshotMilliseconds:F2} ms, 新隣接={preview.Timings.AdjacencyMilliseconds:F2} ms, 新BVH={preview.Timings.BvhMilliseconds:F2} ms");
            }
        }
    }
}
