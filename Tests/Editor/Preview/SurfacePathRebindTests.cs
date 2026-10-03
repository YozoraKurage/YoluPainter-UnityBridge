using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>3D のパスを別のメッシュに付け直す（モデルの差し替え）: 同じ位置の面へ置き直し（三角形の割り方・UV・並びが違っても）、
    /// 許す距離の外・別のマテリアルの組の面・向きが逆の面には置かない、前のモデルに結び付いていないパスは付け直さない。</summary>
    public sealed class SurfacePathRebindTests
    {
        /// <summary>z = 0 の 1×1 の板（法線 +z）を n×n に割ったもの。UV は (u0, v0) から s 倍。material はマテリアルの組。</summary>
        static List<SurfaceTriangle> Board(int n, float u0 = 0, float v0 = 0, float s = 1, int slot = 0, int material = -1, float z = 0, bool flip = false)
        {
            var list = new List<SurfaceTriangle>();
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    float x0 = i / (float)n, x1 = (i + 1) / (float)n, y0 = j / (float)n, y1 = (j + 1) / (float)n;
                    Vector3 P(float x, float y) => new Vector3(x, y, z);
                    Vector2 U(float x, float y) => new Vector2(u0 + x * s, v0 + y * s);
                    if (!flip)
                    {
                        list.Add(new SurfaceTriangle(P(x0, y0), P(x1, y0), P(x1, y1), U(x0, y0), U(x1, y0), U(x1, y1), 0, slot, material));
                        list.Add(new SurfaceTriangle(P(x0, y0), P(x1, y1), P(x0, y1), U(x0, y0), U(x1, y1), U(x0, y1), 0, slot, material));
                    }
                    else
                    {
                        list.Add(new SurfaceTriangle(P(x0, y0), P(x1, y1), P(x1, y0), U(x0, y0), U(x1, y1), U(x1, y0), 0, slot, material));
                        list.Add(new SurfaceTriangle(P(x0, y0), P(x0, y1), P(x1, y1), U(x0, y0), U(x0, y1), U(x1, y1), 0, slot, material));
                    }
                }
            return list;
        }
        static PathPoint At(SurfaceGeometry g, float x, float y)
        {
            Assert.That(g.TryRaycast(new Ray(new Vector3(x, y, 1), Vector3.back), out var hit), Is.True);
            return SurfacePathRenderer.PointOf(hit, .7);
        }
        static SurfacePath PathOn(SurfaceGeometry g) => new SurfacePath(Guid.NewGuid(), PaintChannel.Color, SurfacePathRenderer.Fingerprint(g),
            new PathBrush { RadiusWorld = .005, Color = new Rgba32(1, 2, 3) }, new[] { At(g, .2f, .3f), At(g, .7f, .6f) });

        [Test] public void APathMovesToTheSamePlaceOnAnotherMesh()
        {
            var from = new SurfaceGeometry(Board(1));
            var to = new SurfaceGeometry(Board(4, .5f, .5f, .5f)); // 割り方・UV・三角形の数が違う
            var path = PathOn(from);
            Assert.That(SurfacePathRebind.TryRebind(path, from, to, 0, out var rebound, out string why), Is.True, why);
            Assert.That(rebound.Id, Is.EqualTo(path.Id)); Assert.That(rebound.Brush, Is.EqualTo(path.Brush)); Assert.That(rebound.Channel, Is.EqualTo(path.Channel));
            Assert.That(rebound.ModelFingerprint, Is.EqualTo(SurfacePathRenderer.Fingerprint(to)));
            Assert.That(rebound.Points.Select(p => p.Pressure), Is.EqualTo(path.Points.Select(p => p.Pressure)), "pressure is kept");
            for (int i = 0; i < path.Points.Count; i++)
                Assert.That((SurfacePathRenderer.Position(to, rebound.Points[i], out _) - SurfacePathRenderer.Position(from, path.Points[i], out _)).magnitude, Is.LessThan(1e-5f), "point " + i);
            // 描くと新しい UV の所に描ける
            var d = new PaintDocument(64, 64, 16);
            var drawn = SurfacePathRenderer.Render(d, to, rebound);
            Assert.That(drawn.Gaps, Is.Zero);
            Assert.That(drawn.Surface.EnumerateTileCoordinates().All(c => c.X >= 2 && c.Y >= 2), Is.True, "only in the new UV square (the upper right quarter)");
        }

        [Test] public void APointWithoutANearSurfaceOfItsMaterialIsNotPlaced()
        {
            var from = new SurfaceGeometry(Board(1)); var path = PathOn(from);
            Assert.That(SurfacePathRebind.TryRebind(path, from, new SurfaceGeometry(Board(1, z: .5f)), 0, out _, out string far), Is.False);
            Assert.That(far, Does.Contain("point 1 has no surface of its material within"));
            Assert.That(SurfacePathRebind.TryRebind(path, from, new SurfaceGeometry(Board(1, z: .001f)), 0, out _, out _), Is.True, "a little closer than the tolerance (1% of the diagonal)");
            // 同じ所に別のマテリアルの組の板と、自分の組の裏向きの板: どちらにも置かない
            var other = new SurfaceGeometry(Board(1, slot: 1, material: 1).Concat(Board(1, slot: 0, material: 0, flip: true)).ToList());
            Assert.That(SurfacePathRebind.TryRebind(path, from, other, 0, out _, out string mine), Is.False, "the facing one is another texture set's, the own one faces away");
            Assert.That(SurfacePathRebind.TryRebind(path, from, other, 1, out var onOther, out _), Is.True);
            Assert.That(onOther.Points.All(p => other.Triangles[p.Triangle].Material == 1), Is.True);
            Assert.That(SurfacePathRebind.TryRebind(path, from, other, -1, out _, out string none), Is.False); Assert.That(none, Does.Contain("not in the new model"));
            var stranger = PathOn(new SurfaceGeometry(Board(2)));
            Assert.That(SurfacePathRebind.TryRebind(stranger, from, other, 1, out _, out string orphan), Is.False); Assert.That(orphan, Does.Contain("another model"));
        }
    }
}
