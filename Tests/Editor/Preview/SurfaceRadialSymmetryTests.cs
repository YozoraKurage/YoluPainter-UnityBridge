using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class SurfaceRadialSymmetryTests
    {
        internal static List<SurfaceTriangle> Petals(int count = 4, int otherSlot = -1, bool occluder = false)
        {
            var triangles = new List<SurfaceTriangle>();
            for (int i = 0; i < count; i++)
            {
                var r = Quaternion.AngleAxis(360f * i / count, Vector3.forward);
                var a = r * new Vector3(.6f, -.3f, 0); var b = r * new Vector3(1.4f, -.3f, 0); var c = r * new Vector3(1.4f, .3f, 0); var d = r * new Vector3(.6f, .3f, 0);
                float u = i / (float)count, v = (i + 1f) / count;
                int slot = i == otherSlot ? 1 : 0;
                triangles.Add(new SurfaceTriangle(a, c, b, new Vector2(u, .1f), new Vector2(v, .9f), new Vector2(v, .1f), 0, slot));
                triangles.Add(new SurfaceTriangle(a, d, c, new Vector2(u, .1f), new Vector2(u, .9f), new Vector2(v, .9f), 0, slot));
            }
            if (occluder)
            {
                triangles.Add(new SurfaceTriangle(new Vector3(-1.5f, -.5f, -.2f), new Vector3(-1.5f, .5f, -.2f), new Vector3(-.5f, .5f, -.2f), Vector2.zero, Vector2.up, Vector2.one, 1, 1));
                triangles.Add(new SurfaceTriangle(new Vector3(-1.5f, -.5f, -.2f), new Vector3(-.5f, .5f, -.2f), new Vector3(-.5f, -.5f, -.2f), Vector2.zero, Vector2.one, Vector2.right, 1, 1));
            }
            return triangles;
        }
        internal static SurfaceHit Pick(SurfaceGeometry g, Vector3 at, Vector3? camera = null)
        { Vector3 eye = camera ?? new Vector3(0, 0, -5); Assert.That(g.TryRaycast(new Ray(eye, at - eye), out var hit, true), Is.True); return hit; }
        [TestCase(SymmetryAxis.X, 2)] [TestCase(SymmetryAxis.Y, 7)] [TestCase(SymmetryAxis.Z, 16)]
        public void RotationFollowsRootAxesAndMaintainsRadius(SymmetryAxis axis, int count)
        {
            var root = new Vector3(3, 4, 5); var rotation = Quaternion.Euler(20, 35, 12); var s = RadialSymmetry.FromModel(root, rotation, axis, count);
            var localAxis = axis == SymmetryAxis.X ? Vector3.right : axis == SymmetryAxis.Y ? Vector3.up : Vector3.forward;
            var p = root + rotation * new Vector3(.8f, .3f, -.7f);
            for (int i = 0; i < count; i++)
            {
                var expected = root + rotation * (Quaternion.AngleAxis(360f * i / count, localAxis) * new Vector3(.8f, .3f, -.7f));
                Assert.That(Vector3.Distance(s.RotatePoint(p, i), expected), Is.LessThan(1e-5f));
            }
        }
        [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(7)] [TestCase(16)]
        public void RadialDabsLandOnAllPetalsWithMatchingCoverage(int count)
        {
            var g = new SurfaceGeometry(Petals(count)); var camera = new Vector3(0, 0, -5); var hit = Pick(g, Vector3.right);
            var dab = SurfaceRadialSymmetry.Build(g, hit, null, new RadialSymmetry(Vector3.zero, Vector3.forward, count), true, .18f, 512, 128, camera, .5f);
            Assert.That(dab.Result.WasClipped, Is.False, dab.Result.Diagnostic); Assert.That(dab.Copies.Count, Is.EqualTo(count - 1));
            Assert.That(dab.Result.Pixels.Select(p => p.X * count / 512).Distinct().Count(), Is.EqualTo(count));
            foreach (var copy in dab.Copies) Assert.That(copy.Position.magnitude, Is.EqualTo(1).Within(1e-5f));
        }
        [Test] public void MirrorAndRadialCopiesMergeOverlapByMaximum()
        {
            var g = new SurfaceGeometry(Petals()); var eye = new Vector3(0, 0, -5); var hit = Pick(g, Vector3.right);
            var radial = new RadialSymmetry(Vector3.zero, Vector3.forward, 4); var mirror = MirrorPlane.FromModel(Vector3.zero, Quaternion.identity, SymmetryAxis.Y, 0);
            var one = SurfaceRadialSymmetry.Build(g, hit, null, radial, true, .2f, 256, 128, eye);
            var both = SurfaceRadialSymmetry.Build(g, hit, mirror, radial, true, .2f, 256, 128, eye);
            Assert.That(both.Copies.Count, Is.EqualTo(3), "面の上の鏡映と回転の重複は省く");
            Assert.That(both.Result.Pixels.Select(p => (p.X, p.Y, p.Coverage)), Is.EqualTo(one.Result.Pixels.Select(p => (p.X, p.Y, p.Coverage))));
            hit = Pick(g, new Vector3(1, .1f, 0));
            both = SurfaceRadialSymmetry.Build(g, hit, mirror, radial, true, .2f, 256, 128, eye);
            var maximum = new Dictionary<(int,int), float>();
            foreach (var point in new[] { hit }.Concat(both.Copies)) foreach (var p in g.BuildSurfaceDabs(point, .2f, 256, 128, eye, .8f, ignoreVisibility: true).Pixels)
            { var key = (p.X, p.Y); maximum[key] = maximum.TryGetValue(key, out float old) ? Mathf.Max(old, p.Coverage) : p.Coverage; }
            Assert.That(both.Copies.Count, Is.EqualTo(7)); Assert.That(both.Result.Pixels.Count, Is.EqualTo(maximum.Count));
            foreach (var p in both.Result.Pixels) Assert.That(p.Coverage, Is.EqualTo(maximum[(p.X,p.Y)]).Within(1e-5f));
        }
        [Test] public void IgnoreVisibilityPaintsOccludedCopiesAndKeepsTheOriginalVisibleOnly()
        {
            var g = new SurfaceGeometry(Petals(4, occluder:true)); var hit = Pick(g, Vector3.right); var eye = new Vector3(0,0,-5); var radial = new RadialSymmetry(Vector3.zero, Vector3.forward, 4);
            var hidden = SurfaceRadialSymmetry.Build(g, hit, null, radial, false, .2f, 256, 128, eye);
            var painted = SurfaceRadialSymmetry.Build(g, hit, null, radial, true, .2f, 256, 128, eye);
            Assert.That(hidden.Outcome, Is.EqualTo(MirrorOutcome.Hidden)); Assert.That(hidden.Result.Pixels.Any(p => p.X >= 128 && p.X < 192), Is.False);
            Assert.That(painted.Result.Pixels.Any(p => p.X >= 128 && p.X < 192), Is.True); Assert.That(painted.Result.VisibilityRays, Is.LessThan(hidden.Result.VisibilityRays));
        }
        [Test] public void IgnoreVisibilityPaintsTheMirroredBackFaceWithoutCameraRays()
        {
            var g = new SurfaceGeometry(SymmetricBox.Triangles()); var eye = new Vector3(5,0,0); var hit = Pick(g, new Vector3(1,.2f,.3f), eye);
            var dab = SurfaceSymmetry.Build(g, hit, MirrorPlane.FromModel(Vector3.zero, Quaternion.identity, SymmetryAxis.X, 0), .3f, 256, 256, eye, ignoreVisibility:true);
            Assert.That(dab.Outcome, Is.EqualTo(MirrorOutcome.Painted)); Assert.That(dab.Mirror.Pixels.Count, Is.GreaterThan(0)); Assert.That(dab.Mirror.VisibilityRays, Is.Zero);
            Assert.That(dab.Original.VisibilityRays, Is.GreaterThan(0));
        }
        [Test] public void AnotherSlotAndMissingPetalAreSkippedWithAReason()
        {
            var g = new SurfaceGeometry(Petals(4, 2)); var hit = Pick(g, Vector3.right); var eye = new Vector3(0,0,-5); var radial = new RadialSymmetry(Vector3.zero, Vector3.forward, 4);
            var dab = SurfaceRadialSymmetry.Build(g, hit, null, radial, true, .2f, 256, 128, eye);
            Assert.That(dab.Outcome, Is.EqualTo(MirrorOutcome.OtherSlot)); Assert.That(dab.Result.Pixels.Any(p => p.X >= 128 && p.X < 192), Is.False);
            g = new SurfaceGeometry(Petals().Take(6).ToList()); hit = Pick(g, Vector3.right); dab = SurfaceRadialSymmetry.Build(g, hit, null, radial, true, .2f, 256, 128, eye);
            Assert.That(dab.Outcome, Is.EqualTo(MirrorOutcome.NoSurface));
        }
        [Test] public void CombinedTriangleBudgetRefusesAllPixels()
        {
            var g = new SurfaceGeometry(Petals()); var hit = Pick(g, Vector3.right); var eye = new Vector3(0,0,-5); var budget = new SurfaceBrushBudget { MaxTriangles = 3 };
            Assert.That(g.BuildSurfaceDabs(hit, .2f, 256, 128, eye, budget:budget).WasClipped, Is.False);
            var dab = SurfaceRadialSymmetry.Build(g, hit, null, new RadialSymmetry(Vector3.zero, Vector3.forward, 4), true, .2f, 256, 128, eye, budget:budget);
            Assert.That(dab.Result.WasClipped, Is.True); Assert.That(dab.Result.Pixels, Is.Empty); Assert.That(dab.Result.Diagnostic, Does.Contain("budget"));
        }
        [TestCase(1)] [TestCase(17)] public void InvalidCopyCountsAreRefused(int count) => Assert.Throws<ArgumentOutOfRangeException>(() => new RadialSymmetry(Vector3.zero,Vector3.up,count));
    }
}
