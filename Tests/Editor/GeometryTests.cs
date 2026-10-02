using System.Collections.Generic;
using System.Linq;
using Yozolab.YoluPainter.Editor.Preview;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests.Editor
{
    public sealed class GeometryTests
    {
        static readonly Vector3 Camera = new Vector3(0.5f, 0.5f, -3);
        static SurfaceTriangle FrontTriangle(int renderer = 0, int slot = 0)
        {
            return new SurfaceTriangle(Vector3.zero, Vector3.up, Vector3.right, Vector2.zero, Vector2.up, Vector2.right, renderer, slot);
        }
        static SurfaceHit Hit(SurfaceGeometry geometry, Vector3 position)
        {
            Assert.That(geometry.TryRaycast(new Ray(Camera, position - Camera), out var hit), Is.True);
            return hit;
        }
        static SurfaceGeometry SeamQuad(bool differentMaterial = false)
        {
            return new SurfaceGeometry(new[]
            {
                new SurfaceTriangle(Vector3.zero, Vector3.up, Vector3.right,
                    Vector2.zero, new Vector2(0, 0.4f), new Vector2(0.4f, 0)),
                new SurfaceTriangle(Vector3.right, Vector3.up, Vector3.one - Vector3.forward,
                    new Vector2(0.6f, 0), new Vector2(0.6f, 0.4f), new Vector2(1, 0.4f), 0, differentMaterial ? 1 : 0)
            });
        }

        [Test]
        public void RaycastReturnsBarycentricUvAndNearestDistance()
        {
            var geometry = new SurfaceGeometry(new[] { FrontTriangle() }, 17);
            var hit = Hit(geometry, new Vector3(0.25f, 0.25f, 0));
            Assert.That(hit.SnapshotRevision, Is.EqualTo(17));
            Assert.That(hit.Barycentric.x, Is.EqualTo(0.5f).Within(0.00001f));
            Assert.That(hit.Barycentric.y, Is.EqualTo(0.25f).Within(0.00001f));
            Assert.That(hit.Barycentric.z, Is.EqualTo(0.25f).Within(0.00001f));
            Assert.That(hit.UV.x, Is.EqualTo(0.25f).Within(0.00001f));
            Assert.That(hit.UV.y, Is.EqualTo(0.25f).Within(0.00001f));
            Assert.That(hit.Normal.z, Is.EqualTo(-1).Within(0.00001f));
        }

        [Test]
        public void RaycastRejectsBackfacesUnlessExplicitlyTwoSided()
        {
            var geometry = new SurfaceGeometry(new[] { FrontTriangle() });
            var ray = new Ray(new Vector3(0.2f, 0.2f, 1), Vector3.back);
            Assert.That(geometry.TryRaycast(ray, out _), Is.False);
            Assert.That(geometry.TryRaycast(ray, out _, false), Is.True);
        }

        [Test]
        public void BvhFindsClosestTriangleWithNonUnitInputDirection()
        {
            var triangles = new List<SurfaceTriangle>();
            for (int i = 20; i >= 0; i--)
            {
                var t = FrontTriangle(); t.A.z = t.B.z = t.C.z = i;
                triangles.Add(t);
            }
            var geometry = new SurfaceGeometry(triangles);
            Assert.That(geometry.TryRaycast(new Ray(new Vector3(0.2f, 0.2f, -1), Vector3.forward * 10), out var hit), Is.True);
            Assert.That(hit.Distance, Is.EqualTo(1).Within(0.00001f));
            Assert.That(hit.TriangleIndex, Is.EqualTo(20));
        }

        [Test]
        public void FootprintCrossesGeometricUvSeamIntoSeparateUvIsland()
        {
            var geometry = SeamQuad();
            var result = geometry.BuildSurfaceDabs(Hit(geometry, new Vector3(0.45f, 0.45f, 0)), 0.25f, 64, 64, Camera);
            Assert.That(result.WasClipped, Is.False);
            Assert.That(result.Pixels.Any(p => p.X < 26), Is.True, "First island must receive paint.");
            Assert.That(result.Pixels.Any(p => p.X >= 38), Is.True, "Second, UV-disconnected island must receive the same world footprint.");
            Assert.That(result.Pixels.All(p => p.X < 26 || p.X >= 38), Is.True, "The empty UV gap must remain untouched.");
        }

        [Test]
        public void FootprintDoesNotCrossMaterialSlotBoundary()
        {
            var geometry = SeamQuad(true);
            var result = geometry.BuildSurfaceDabs(Hit(geometry, new Vector3(0.45f, 0.45f, 0)), 0.4f, 64, 64, Camera);
            Assert.That(result.Pixels.Count, Is.GreaterThan(0));
            Assert.That(result.Pixels.All(p => p.X < 26), Is.True);
        }

        [Test]
        public void NearbyDisconnectedShellDoesNotReceivePaint()
        {
            var first = FrontTriangle(); first.UvB *= 0.4f; first.UvC *= 0.4f;
            var second = FrontTriangle(); second.A += Vector3.right * 1.05f; second.B += Vector3.right * 1.05f; second.C += Vector3.right * 1.05f;
            second.UvA = new Vector2(0.6f, 0); second.UvB = new Vector2(0.6f, 0.4f); second.UvC = new Vector2(1, 0);
            var geometry = new SurfaceGeometry(new[] { first, second });
            var result = geometry.BuildSurfaceDabs(Hit(geometry, new Vector3(0.85f, 0.05f, 0)), 0.5f, 64, 64, Camera);
            Assert.That(result.Pixels.Count, Is.GreaterThan(0));
            Assert.That(result.Pixels.All(p => p.X < 26), Is.True);
        }

        [TestCase(false, -0.2f)]
        [TestCase(true, -0.2f)]
        [TestCase(false, -0.000001f)]
        public void ForegroundGeometryOccludesTexelsEvenWhenCoveringTriangleFacesAway(bool reverseOccluder, float planeZ)
        {
            var triangles = new List<SurfaceTriangle> { FrontTriangle() };
            Vector3 a = new Vector3(0.4f, -0.1f, planeZ), b = new Vector3(0.4f, 1.1f, planeZ);
            Vector3 c = new Vector3(1.1f, -0.1f, planeZ), d = new Vector3(1.1f, 1.1f, planeZ);
            triangles.Add(new SurfaceTriangle(a, reverseOccluder ? c : b, reverseOccluder ? b : c, Vector2.zero, Vector2.zero, Vector2.zero, 1, 1));
            triangles.Add(new SurfaceTriangle(c, reverseOccluder ? d : b, reverseOccluder ? b : d, Vector2.zero, Vector2.zero, Vector2.zero, 1, 1));
            var geometry = new SurfaceGeometry(triangles);
            var result = geometry.BuildSurfaceDabs(Hit(geometry, new Vector3(0.1f, 0.1f, 0)), 1.5f, 32, 32, Camera, 1);
            Assert.That(result.Pixels.Any(p => p.X < 8), Is.True);
            Assert.That(result.Pixels.Any(p => p.X >= 20 && p.Y >= 3), Is.False, "Hidden target texels must be rejected by visibility rays.");
        }

        [Test]
        public void SharedTriangleEdgeIsDeduplicatedAndCoordinatesAreBottomLeft()
        {
            var geometry = new SurfaceGeometry(new[]
            {
                FrontTriangle(),
                new SurfaceTriangle(Vector3.right, Vector3.up, new Vector3(1, 1, 0), Vector2.right, Vector2.up, Vector2.one)
            });
            var result = geometry.BuildSurfaceDabs(Hit(geometry, new Vector3(0.3f, 0.3f, 0)), 3, 16, 16, Camera, 1);
            Assert.That(result.Pixels.Count, Is.EqualTo(256));
            Assert.That(result.Pixels.Select(p => p.Y * 16 + p.X).Distinct().Count(), Is.EqualTo(256));
            Assert.That(result.Pixels[0].X, Is.EqualTo(0)); Assert.That(result.Pixels[0].Y, Is.EqualTo(0));
            Assert.That(result.Pixels[255].X, Is.EqualTo(15)); Assert.That(result.Pixels[255].Y, Is.EqualTo(15));
            Assert.That(result.Pixels.All(p => Mathf.Approximately(p.Coverage, 1)), Is.True);
        }

        [Test]
        public void SoftFalloffProducesCoverageWithinUnitRange()
        {
            var geometry = new SurfaceGeometry(new[] { FrontTriangle() });
            var result = geometry.BuildSurfaceDabs(Hit(geometry, new Vector3(0.25f, 0.25f, 0)), 0.2f, 64, 64, Camera, 0);
            Assert.That(result.Pixels.Any(p => p.Coverage > 0 && p.Coverage < 0.5f), Is.True);
            Assert.That(result.Pixels.All(p => p.Coverage >= 0 && p.Coverage <= 1), Is.True);
        }

        [Test]
        public void PixelBudgetRejectsEntireDabInsteadOfReturningPartialPixels()
        {
            var geometry = SeamQuad();
            var result = geometry.BuildSurfaceDabs(Hit(geometry, new Vector3(0.45f, 0.45f, 0)), 0.5f, 64, 64, Camera, 1,
                new SurfaceBrushBudget { MaxCandidatePixels = 1 });
            Assert.That(result.WasClipped, Is.True);
            Assert.That(result.Pixels, Is.Empty);
            Assert.That(result.Diagnostic, Does.Contain("No pixels were changed"));
        }

        [Test]
        public void VisibilityBudgetRejectsEntireDab()
        {
            var geometry = new SurfaceGeometry(new[] { FrontTriangle() });
            var result = geometry.BuildSurfaceDabs(Hit(geometry, new Vector3(0.2f, 0.2f, 0)), 0.5f, 32, 32, Camera, 1,
                new SurfaceBrushBudget { MaxVisibilityRays = 1 });
            Assert.That(result.WasClipped, Is.True); Assert.That(result.Pixels, Is.Empty);
        }

        [TestCase(0, 100)]
        [TestCase(100, 0)]
        public void BvhWorkBudgetRejectsWholeDab(int maxTriangleTests, int maxNodeVisits)
        {
            var geometry = new SurfaceGeometry(new[] { FrontTriangle() });
            var result = geometry.BuildSurfaceDabs(Hit(geometry, new Vector3(0.2f, 0.2f, 0)), 0.5f, 32, 32, Camera, 1,
                new SurfaceBrushBudget { MaxRayTriangleTests = maxTriangleTests, MaxRayNodeVisits = maxNodeVisits });
            Assert.That(result.WasClipped, Is.True); Assert.That(result.Pixels, Is.Empty);
            Assert.That(result.Diagnostic, Does.Contain("BVH work budget"));
        }

        [Test]
        public void StaleSnapshotCannotPaint()
        {
            var old = new SurfaceGeometry(new[] { FrontTriangle() }, 1);
            var current = new SurfaceGeometry(new[] { FrontTriangle() }, 2);
            var result = current.BuildSurfaceDabs(Hit(old, new Vector3(0.25f, 0.25f, 0)), 0.1f, 16, 16, Camera);
            Assert.That(result.Pixels, Is.Empty); Assert.That(result.Diagnostic, Does.Contain("snapshot changed"));
        }

        [Test]
        public void MissingUvTriangleStillOccludesButCannotBePainted()
        {
            var t = FrontTriangle(); t.UvA = t.UvB = t.UvC = Vector2.zero;
            var geometry = new SurfaceGeometry(new[] { t });
            var hit = Hit(geometry, new Vector3(0.2f, 0.2f, 0));
            Assert.That(geometry.BuildSurfaceDabs(hit, 0.3f, 16, 16, Camera).Pixels, Is.Empty);
        }

        [Test]
        public void NonFiniteMeshDataIsRejected()
        {
            var triangle = FrontTriangle(); triangle.A.x = float.NaN;
            Assert.Throws<System.ArgumentException>(() => new SurfaceGeometry(new[] { triangle }));
        }

        [Test]
        public void SmallWorldTrianglesHaveUnitNormalsAndReceivePaint()
        {
            var t = FrontTriangle(); t.B *= 0.001f; t.C *= 0.001f;
            var geometry = new SurfaceGeometry(new[] { t });
            var camera = new Vector3(0.00025f, 0.00025f, -0.003f);
            Assert.That(geometry.TryRaycast(new Ray(camera, Vector3.forward), out var hit), Is.True);
            Assert.That(hit.Normal.z, Is.EqualTo(-1).Within(0.00001f));
            Assert.That(geometry.BuildSurfaceDabs(hit, 0.0002f, 32, 32, camera).Pixels.Count, Is.GreaterThan(0));
        }

        [Test]
        public void LoadClonesOnlyRenderDataAndLeavesSourceUnchanged()
        {
            var source = new GameObject("Texture painter test source");
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, uv = new[] { Vector2.zero, Vector2.up, Vector2.right }, triangles = new[] { 0, 1, 2 } };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            var shader = Shader.Find("Hidden/YoluPainter/PreviewSurface");
            Assert.That(shader, Is.Not.Null, "The package preview shader must be imported before running Unity tests.");
            var material = new Material(shader); material.SetColor("_Color", Color.magenta);
            try
            {
                source.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = source.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                source.AddComponent<PreviewSourceProbe>();
                source.transform.position = new Vector3(10, 20, 30);
                source.transform.localScale = new Vector3(2, 3, 1);
                int executions = PreviewSourceProbe.EnableCount;
                using (var preview = new IsolatedModelPreview())
                {
                    var report = preview.Load(source);
                    Assert.That(report.Success, Is.True); Assert.That(report.CanPaint, Is.True);
                    Assert.That(preview.Bounds.size.x, Is.EqualTo(2).Within(0.00001f));
                    Assert.That(preview.Bounds.size.y, Is.EqualTo(3).Within(0.00001f));
                    preview.SetPaintTexture(Texture2D.blackTexture, 0);
                    Assert.That(renderer.sharedMaterial, Is.SameAs(material));
                    Assert.That(material.GetColor("_Color"), Is.EqualTo(Color.magenta));
                    Assert.That(material.GetTexture("_MainTex"), Is.Not.SameAs(Texture2D.blackTexture));
                    Assert.That(source.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(mesh));
                    Assert.That(mesh.vertices[1], Is.EqualTo(Vector3.up));
                    Assert.That(PreviewSourceProbe.EnableCount, Is.EqualTo(executions), "Source behaviours must never be copied or enabled by Load.");
                    preview.Dispose(); // Idempotent cleanup, including using's later Dispose.
                }
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(mesh); Object.DestroyImmediate(material); }
        }

        [Test]
        public void LoadDemoProvidesScriptFreeCubeAndSafePainting()
        {
            using (var preview = new IsolatedModelPreview())
            {
                var report = preview.LoadDemoMesh();
                Assert.That(report.Success, Is.True);
                Assert.That(report.CanPaint, Is.True);
                Assert.That(report.TriangleCount, Is.EqualTo(12));
                Assert.That(preview.MaterialSlotCount, Is.EqualTo(1));
                Assert.That(preview.Bounds.size, Is.EqualTo(Vector3.one));
            }
        }

        [Test]
        public void OutOfRangeUvDisablesSurfacePaintRatherThanWrappingOrClipping()
        {
            var source = new GameObject("Out-of-range UV test");
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right },
                uv = new[] { Vector2.zero, Vector2.up * 2, Vector2.right }, triangles = new[] { 0, 1, 2 } };
            try
            {
                source.AddComponent<MeshFilter>().sharedMesh = mesh;
                source.AddComponent<MeshRenderer>();
                using (var preview = new IsolatedModelPreview())
                {
                    var report = preview.Load(source);
                    Assert.That(report.Success, Is.True);
                    Assert.That(report.CanPaint, Is.False);
                    Assert.That(report.Diagnostics.Any(d => d.Contains("outside 0–1")), Is.True);
                }
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(mesh); }
        }
    }

    [ExecuteAlways]
    public sealed class PreviewSourceProbe : MonoBehaviour
    {
        public static int EnableCount;
        void OnEnable() { EnableCount++; }
    }
}
