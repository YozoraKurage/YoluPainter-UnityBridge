using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;
namespace Yozolab.YoluPainter.Tests
{
    public sealed class BigMeshGeometryTests
    {
        [Test]
        public void RandomAdjacencyAndNonManifoldEdgesMatchLegacyInTraversalOrder()
        {
            var random = new System.Random(48302);
            for (int fixture = 0; fixture < 20; fixture++)
            {
                var triangles = new List<SurfaceTriangle>();
                for (int i = 0; i < 500; i++)
                {
                    Vector3 Point() => new Vector3(random.Next(-10, 11), random.Next(-10, 11), random.Next(-2, 3)) * .01f;
                    var a = Point(); var b = Point(); var c = Point();
                    triangles.Add(new SurfaceTriangle(a, b, c, Vector2.zero, Vector2.up, Vector2.right, random.Next(2), random.Next(3)));
                    if (i % 9 == 0) { var t = triangles[triangles.Count - 1]; t.C = Point(); t.UvA = Vector2.one; triangles.Add(t); }
                    if (i % 29 == 0) triangles.Add(triangles[triangles.Count - 1]);
                }
                var legacy = new LegacySurfaceGeometry(triangles); var current = new SurfaceGeometry(triangles);
                Assert.That(current.NonManifoldEdgeCount, Is.EqualTo(legacy.NonManifoldEdgeCount), "fixture " + fixture);
                for (int i = 0; i < triangles.Count; i++) Assert.That(current.Neighbors[i], Is.EqualTo(legacy.Neighbors[i]), "fixture " + fixture + " triangle " + i);
            }
        }
        [Test]
        public void RandomRaysMatchLegacyIncludingCullAndMaximumDistance()
        {
            var random = new System.Random(74365); var triangles = new List<SurfaceTriangle>();
            float Next() => (float)random.NextDouble();
            for (int i = 0; i < 2000; i++)
            {
                var a = new Vector3(Next() * 20 - 10, Next() * 20 - 10, Next() * 5);
                triangles.Add(new SurfaceTriangle(a, a + Vector3.up * (Next() + .05f), a + Vector3.right * (Next() + .05f), Vector2.zero, Vector2.up, Vector2.right, i % 24, i % 32));
            }
            var legacy = new LegacySurfaceGeometry(triangles, 9); var current = new SurfaceGeometry(triangles, 9);
            for (int i = 0; i < 5000; i++)
            {
                int target = i % triangles.Count; var t = triangles[target];
                Vector3 end = i % 2 == 0 ? t.A * .2f + t.B * .3f + t.C * .5f : new Vector3(Next() * 20 - 10, Next() * 20 - 10, 3);
                var ray = new Ray(new Vector3(end.x + Next() - .5f, end.y + Next() - .5f, i % 3 == 0 ? 9 : -3), end - new Vector3(end.x + Next() - .5f, end.y + Next() - .5f, i % 3 == 0 ? 9 : -3));
                bool cull = i % 4 != 0; float max = i % 5 == 0 ? 2 : float.PositiveInfinity;
                bool expected = legacy.TryRaycast(ray, out var old, cull, max), actual = current.TryRaycast(ray, out var hit, cull, max);
                Assert.That(actual, Is.EqualTo(expected), "ray " + i);
                if (!actual) continue;
                Assert.That(hit.TriangleIndex, Is.EqualTo(old.TriangleIndex), "ray " + i);
                Assert.That(hit.Distance, Is.EqualTo(old.Distance).Within(1e-6f));
                Assert.That(hit.UV, Is.EqualTo(old.UV)); Assert.That(hit.RendererIndex, Is.EqualTo(old.RendererIndex)); Assert.That(hit.MaterialSlot, Is.EqualTo(old.MaterialSlot));
            }
        }
        [Test]
        public void QuantizationBoundariesAndCollapsedEdgesMatchLegacy()
        {
            var points = new[] { -.0000015f, -.0000005f, 0, .0000005f, .0000015f, -1f, 1f };
            var triangles = new List<SurfaceTriangle>();
            foreach (float x in points) foreach (float y in points)
                triangles.Add(new SurfaceTriangle(new Vector3(x, y, 0), new Vector3(y, x, 0), Vector3.up, Vector2.zero, Vector2.up, Vector2.right));
            var old = new LegacySurfaceGeometry(triangles); var value = new SurfaceGeometry(triangles);
            Assert.That(value.NonManifoldEdgeCount, Is.EqualTo(old.NonManifoldEdgeCount));
            for (int i = 0; i < triangles.Count; i++) Assert.That(value.Neighbors[i], Is.EqualTo(old.Neighbors[i]));
        }
        [TestCase(.2f)] [TestCase(.7f)]
        public void CancelDuringAdjacencyOrBvhDoesNotPublishPartialGeometry(float at)
        {
            var t = new SurfaceTriangle(Vector3.zero, Vector3.up, Vector3.right, Vector2.zero, Vector2.up, Vector2.right);
            using (var cancel = new CancellationTokenSource())
                Assert.Throws<OperationCanceledException>(() => new SurfaceGeometry(Enumerable.Repeat(t, 20000).ToArray(), 1, .000001f, cancel.Token, p => { if (p >= at) cancel.Cancel(); }));
        }
        [Test]
        public void CoincidentCentersAndSortedInputsTerminateAndKeepEveryTriangle()
        {
            var triangles = new List<SurfaceTriangle>();
            for (int i = 0; i < 8192; i++)
            { float size = .5f + i * .00001f; triangles.Add(new SurfaceTriangle(new Vector3(-size, -size, i * .001f), new Vector3(0, size, i * .001f), new Vector3(size, -size, i * .001f), Vector2.zero, Vector2.up, Vector2.right)); }
            var current = new SurfaceGeometry(triangles);
            Assert.That(current.TryRaycast(new Ray(new Vector3(0, 0, -1), Vector3.forward), out var hit), Is.True);
            Assert.That(hit.TriangleIndex, Is.EqualTo(0)); Assert.That(current.TriangleCount, Is.EqualTo(triangles.Count));
        }
    }
}
