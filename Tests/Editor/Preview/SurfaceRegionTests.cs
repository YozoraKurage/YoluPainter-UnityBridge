using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>3D ビューでの選択: UV の三角形の和集合の選択範囲（共有辺に継ぎ目も二重もない）と、クリックした三角形からの範囲
    /// （三角形・UV アイランド・メッシュの塊・マテリアル）。</summary>
    public sealed class SurfaceRegionTests
    {
        [Test] public void TrianglesSharingAnEdgeCoverOnceWithoutASeam()
        {
            var d = new PaintDocument(16, 16, 8);
            var square = SelectionMask.FromTriangles(d, new List<(double, double, double, double, double, double)> { (4, 4, 12, 4, 12, 12), (4, 4, 12, 12, 4, 12) });
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                Assert.That(square[x, y], Is.EqualTo(x >= 4 && x < 12 && y >= 4 && y < 12 ? 255 : 0), x + "," + y);
            var one = SelectionMask.FromTriangles(d, new List<(double, double, double, double, double, double)> { (0, 0, 8, 0, 0, 8) });
            double area = 0; for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) area += one[x, y] / 255.0;
            Assert.That(area, Is.EqualTo(32).Within(1), "the triangle's area");
            Assert.That(Enumerable.Range(0, 8).Any(x => one[x, 7 - x] > 0 && one[x, 7 - x] < 255), Is.True, "the hypotenuse is anti-aliased");
            Assert.That(SelectionMask.FromTriangles(d, new List<(double, double, double, double, double, double)>()).IsEmpty, Is.True);
            Assert.That(SelectionMask.FromTriangles(d, new List<(double, double, double, double, double, double)> { (1, 1, 5, 5, 9, 9) }).IsEmpty, Is.True, "a degenerate triangle covers nothing");
            Assert.That(SelectionMask.FromTriangles(d, new List<(double, double, double, double, double, double)> { (40, 40, 50, 40, 40, 50) }).IsEmpty, Is.True, "off the canvas");
        }

        /// <summary>2 つの四角（4 三角形）は 3D では辺 x = 1 を共有するが、UV では離れている。別のスロットの三角形を 1 つ足す。</summary>
        static SurfaceGeometry TwoIslands()
        {
            var t = new List<SurfaceTriangle>();
            void Quad(float x0, float u0, float u1, int slot)
            {
                Vector3 a = new Vector3(x0, 0, 0), b = new Vector3(x0 + 1, 0, 0), c = new Vector3(x0 + 1, 1, 0), d = new Vector3(x0, 1, 0);
                Vector2 ua = new Vector2(u0, 0), ub = new Vector2(u1, 0), uc = new Vector2(u1, .5f), ud = new Vector2(u0, .5f);
                t.Add(new SurfaceTriangle(a, b, c, ua, ub, uc, 0, slot)); t.Add(new SurfaceTriangle(a, c, d, ua, uc, ud, 0, slot));
            }
            Quad(0, 0, .4f, 0); Quad(1, .6f, 1, 0); Quad(5, 0, 1, 1);
            return new SurfaceGeometry(t);
        }

        [Test] public void RegionsFollowUvIslandsMeshPartsAndMaterials()
        {
            var g = TwoIslands();
            Assert.That(SurfaceRegions.Region(g, 0, SurfaceRegionKind.Triangle), Is.EqualTo(new[] { 0 }));
            Assert.That(SurfaceRegions.Region(g, 0, SurfaceRegionKind.UvIsland), Is.EqualTo(new[] { 0, 1 }), "the UV gap separates the islands");
            Assert.That(SurfaceRegions.Region(g, 3, SurfaceRegionKind.UvIsland), Is.EqualTo(new[] { 2, 3 }));
            Assert.That(SurfaceRegions.Region(g, 0, SurfaceRegionKind.MeshPart), Is.EqualTo(new[] { 0, 1, 2, 3 }), "the shared 3D edge joins them");
            Assert.That(SurfaceRegions.Region(g, 0, SurfaceRegionKind.Material), Is.EqualTo(new[] { 0, 1, 2, 3 }), "only slot 0");
            Assert.That(SurfaceRegions.Region(g, 4, SurfaceRegionKind.MeshPart), Is.EqualTo(new[] { 4, 5 }), "another slot is never crossed");
            Assert.That(() => SurfaceRegions.Region(g, 99, SurfaceRegionKind.Triangle), Throws.InstanceOf<System.ArgumentOutOfRangeException>());

            var doc = new PaintDocument(20, 20, 8);
            var island = SurfaceRegions.Selection(doc, g, SurfaceRegions.Region(g, 0, SurfaceRegionKind.UvIsland));
            Assert.That(island[3, 5], Is.EqualTo(255)); Assert.That(island[8, 5], Is.EqualTo(0), "u 0..0.4 → x 0..8"); Assert.That(island[3, 12], Is.EqualTo(0), "v 0..0.5 → y 0..10");
            Assert.That(island[7, 9], Is.EqualTo(255)); Assert.That(island[15, 5], Is.EqualTo(0), "the other island is not selected");
        }
    }
}
