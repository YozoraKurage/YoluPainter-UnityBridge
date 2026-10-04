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
    /// <summary>編集できる 3D の筆跡: パスと画素はいつも一緒に変わり（1 回の Undo）、点を動かすと描き直し、パスの層は手で塗れない。描画はカメラに
    /// 依らず決まり、別のモデルのパスは描かない。角をまたぐ線は両方の面に描く。</summary>
    public sealed class SurfacePathTests
    {
        const int Size = 64;
        /// <summary>z = 0 の 1×1 の面（UV 0..1）。法線は +z。</summary>
        static SurfaceGeometry Plane()
        {
            Vector3 a = new Vector3(0, 0, 0), b = new Vector3(1, 0, 0), c = new Vector3(1, 1, 0), d = new Vector3(0, 1, 0);
            return new SurfaceGeometry(new List<SurfaceTriangle> { new SurfaceTriangle(a, b, c, Vector2.zero, Vector2.right, Vector2.one), new SurfaceTriangle(a, c, d, Vector2.zero, Vector2.one, Vector2.up) });
        }
        /// <summary>床（y = 0、UV 下半分）と壁（x = 0、UV 上半分）が x = 0 の辺で直角につながった L 字。</summary>
        static SurfaceGeometry LShape()
        {
            var t = new List<SurfaceTriangle>();
            // 床: (0,0,0)-(1,0,0)-(1,0,1)-(0,0,1)、上向き（+y）
            Vector3 f0 = new Vector3(0, 0, 0), f1 = new Vector3(1, 0, 0), f2 = new Vector3(1, 0, 1), f3 = new Vector3(0, 0, 1);
            t.Add(new SurfaceTriangle(f0, f3, f2, new Vector2(0, 0), new Vector2(0, .5f), new Vector2(1, .5f))); t.Add(new SurfaceTriangle(f0, f2, f1, new Vector2(0, 0), new Vector2(1, .5f), new Vector2(1, 0)));
            // 壁: (0,0,0)-(0,1,0)-(0,1,1)-(0,0,1)、+x 向き
            Vector3 w0 = new Vector3(0, 0, 0), w1 = new Vector3(0, 1, 0), w2 = new Vector3(0, 1, 1), w3 = new Vector3(0, 0, 1);
            t.Add(new SurfaceTriangle(w0, w1, w2, new Vector2(0, .5f), new Vector2(0, 1), new Vector2(1, 1))); t.Add(new SurfaceTriangle(w0, w2, w3, new Vector2(0, .5f), new Vector2(1, 1), new Vector2(1, .5f)));
            return new SurfaceGeometry(t);
        }
        static PathBrush Brush(double radius = .05) => new PathBrush { RadiusWorld = radius, Hardness = 1, Spacing = .1, Color = new Rgba32(200, 30, 30), PressureSize = false, PressureOpacity = false };
        /// <summary>面の点 (x, y) に当たる制御点。</summary>
        static PathPoint At(SurfaceGeometry g, Vector3 from, Vector3 direction)
        {
            Assert.That(g.TryRaycast(new Ray(from, direction), out var hit), Is.True);
            return SurfacePathRenderer.PointOf(hit);
        }
        static (PaintDocument d, PaintLayer l) Doc() { var d = new PaintDocument(Size, Size, 16); var l = d.AddLayer("P"); d.ClearHistory(); return (d, l); }
        static int A(PaintLayer l, int x, int y) => l.GetPixel(PaintChannel.Color, x, y).A;

        [Test] public void APathPaintsBetweenItsPointsAndMovingAPointRedraws()
        {
            var g = Plane(); var (d, l) = Doc();
            var p0 = At(g, new Vector3(.2f, .5f, 1), Vector3.back); var p1 = At(g, new Vector3(.8f, .5f, 1), Vector3.back);
            var path = new SurfacePath(Guid.NewGuid(), PaintChannel.Color, SurfacePathRenderer.Fingerprint(g), Brush(), new[] { p0, p1 });
            var r = SurfacePathRenderer.Render(d, g, path);
            Assert.That(r.Gaps, Is.Zero); Assert.That(r.Dabs, Is.GreaterThan(5));
            d.SetPath(l.Id, path, r.Surface);
            Assert.That(A(l, 32, 32), Is.EqualTo(255), "the middle of the line"); Assert.That(A(l, 32, 50), Is.Zero);
            Assert.That(l.Path, Is.SameAs(path)); Assert.That(d.UndoCount, Is.EqualTo(1));
            Assert.That(SurfacePathRenderer.Render(d, g, path).Surface.EnumerateTiles().Select(t => t.Bytes).SelectMany(b => b), Is.EqualTo(r.Surface.EnumerateTiles().Select(t => t.Bytes).SelectMany(b => b)), "the same path draws the same pixels");

            var moved = path.WithPoints(new[] { p0, At(g, new Vector3(.8f, .9f, 1), Vector3.back) });
            d.SetPath(l.Id, moved, SurfacePathRenderer.Render(d, g, moved).Surface);
            Assert.That(A(l, 51, 32), Is.Zero, "the old end is cleared when the path is redrawn"); Assert.That(A(l, 51, 57), Is.EqualTo(255));
            d.Undo();
            Assert.That(l.Path, Is.SameAs(path)); Assert.That(A(l, 51, 32), Is.EqualTo(255), "undo brings back the pixels and the path together");
            d.Undo();
            Assert.That(l.Path, Is.Null); Assert.That(A(l, 32, 32), Is.Zero);
        }

        [Test] public void ALineAcrossACornerPaintsBothFaces()
        {
            var g = LShape(); var (d, l) = Doc();
            var onFloor = At(g, new Vector3(.6f, 1, .5f), Vector3.down); var onWall = At(g, new Vector3(1, .6f, .5f), Vector3.left);
            var path = new SurfacePath(Guid.NewGuid(), PaintChannel.Color, SurfacePathRenderer.Fingerprint(g), Brush(.03), new[] { onFloor, onWall });
            var r = SurfacePathRenderer.Render(d, g, path);
            d.SetPath(l.Id, path, r.Surface);
            int floor = 0, wall = 0;
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) if (A(l, x, y) > 0) { if (y < Size / 2) floor++; else wall++; }
            Assert.That(floor, Is.GreaterThan(0), "painted on the floor (UV lower half)"); Assert.That(wall, Is.GreaterThan(0), "and on the wall (UV upper half)");
        }

        [Test] public void PathLayersRefuseHandEditsUntilRasterized()
        {
            var g = Plane(); var (d, l) = Doc();
            var path = new SurfacePath(Guid.NewGuid(), PaintChannel.Color, SurfacePathRenderer.Fingerprint(g), Brush(), new[] { At(g, new Vector3(.5f, .5f, 1), Vector3.back) });
            d.SetPath(l.Id, path, SurfacePathRenderer.Render(d, g, path).Surface);
            Assert.That(() => d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings()), Throws.InvalidOperationException.With.Message.Contains("drawn by a path"));
            Assert.That(() => d.Fill(l.Id, PaintChannel.Color, new Rgba32(1, 1, 1)), Throws.InvalidOperationException.With.Message.Contains("drawn by a path"));
            Assert.That(() => d.Transform(l.Id, Affine2D.Translation(1, 0)), Throws.InvalidOperationException.With.Message.Contains("drawn by a path"));
            d.Rasterize(l.Id);
            Assert.That(l.Path, Is.Null); Assert.That(A(l, 32, 32), Is.EqualTo(255), "rasterizing keeps the pixels");
            Assert.That(d.Fill(l.Id, PaintChannel.Color, new Rgba32(1, 1, 1)), Is.True);
            d.Undo(); d.Undo(); Assert.That(l.Path, Is.SameAs(path));
        }

        [Test] public void PathsAreSavedWithTheirPixelsAndOpenWithoutTheModel()
        {
            var g = Plane(); var (d, l) = Doc();
            var brush = Brush(); brush.Opacity = .7; brush.PressureFlow = true;
            var path = new SurfacePath(Guid.NewGuid(), PaintChannel.Color, SurfacePathRenderer.Fingerprint(g), brush,
                new[] { At(g, new Vector3(.2f, .3f, 1), Vector3.back), new PathPoint(1, .25, .5, .4) });
            d.SetPath(l.Id, path, SurfacePathRenderer.Render(d, g, path).Surface);
            var bytes = Yozolab.YoluPainter.Core.Persistence.DocumentBinary.Write(d);
            var restored = Yozolab.YoluPainter.Core.Persistence.DocumentBinary.Read(bytes);
            var p = (SurfacePath)restored.Layers[0].Path;
            Assert.That(p, Is.Not.Null);
            Assert.That((p.Id, p.Channel, p.ModelFingerprint), Is.EqualTo((path.Id, path.Channel, path.ModelFingerprint)));
            Assert.That(p.Brush, Is.EqualTo(path.Brush)); Assert.That(p.Points, Is.EqualTo(path.Points));
            Assert.That(restored.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)), "the pixels open without the model");
            Assert.That(Yozolab.YoluPainter.Core.Persistence.DocumentBinary.Write(restored), Is.EqualTo(bytes));
            Assert.That(restored.UndoCount, Is.Zero);
            // パスの版を書き換えたファイルは読まない（黙って捨てない）
            var tampered = (byte[])bytes.Clone();
            int at = IndexOfPathBlock(tampered, path.Id); BitConverter.GetBytes(99).CopyTo(tampered, at);
            Assert.That(() => Yozolab.YoluPainter.Core.Persistence.DocumentBinary.Read(tampered), Throws.TypeOf<System.IO.InvalidDataException>().With.Message.Contains("path algorithm"));
        }
        /// <summary>パスのアルゴリズム版の位置（パスの ID のすぐ前の 4 バイト）。</summary>
        static int IndexOfPathBlock(byte[] bytes, Guid id)
        {
            var key = id.ToByteArray();
            for (int i = 0; i + key.Length <= bytes.Length; i++) if (bytes.Skip(i).Take(key.Length).SequenceEqual(key)) return i - 4;
            throw new InvalidOperationException("path id not found");
        }

        [Test] public void OtherModelsChannelsAndBudgetsAreRefused()
        {
            var g = Plane(); var (d, l) = Doc();
            var path = new SurfacePath(Guid.NewGuid(), PaintChannel.Color, "0123456789abcdef", Brush(), new[] { new PathPoint(0, .2, .2) });
            Assert.That(() => SurfacePathRenderer.Render(d, g, path), Throws.InvalidOperationException.With.Message.Contains("another model"));
            var good = new SurfacePath(Guid.NewGuid(), PaintChannel.Roughness, SurfacePathRenderer.Fingerprint(g), Brush(), new[] { new PathPoint(0, .2, .2) });
            Assert.That(() => d.SetPath(l.Id, good, SurfacePathRenderer.Render(d, g, good).Surface), Throws.InvalidOperationException.With.Message.Contains("not enabled"));
            var fill = d.AddFillLayer("F");
            var color = new SurfacePath(Guid.NewGuid(), PaintChannel.Color, SurfacePathRenderer.Fingerprint(g), Brush(), new[] { new PathPoint(0, .2, .2) });
            Assert.That(() => d.SetPath(fill.Id, color, SurfacePathRenderer.Render(d, g, color).Surface), Throws.InvalidOperationException);
            Assert.That(() => new PathPoint(0, .8, .8), Throws.InstanceOf<ArgumentOutOfRangeException>(), "outside its triangle");
            Assert.That(() => new SurfacePath(Guid.NewGuid(), PaintChannel.Color, "", Brush(), new PathPoint[0]), Throws.ArgumentException);
            var wide = new SurfacePath(Guid.NewGuid(), PaintChannel.Color, SurfacePathRenderer.Fingerprint(g), Brush(.4), new[] { At(g, new Vector3(.1f, .5f, 1), Vector3.back), At(g, new Vector3(.9f, .5f, 1), Vector3.back) });
            var rendered = SurfacePathRenderer.Render(d, g, wide).Surface;
            d.ActiveStrokeBudgetBytes = 100;
            int undo = d.UndoCount;
            Assert.That(() => d.SetPath(l.Id, wide, rendered), Throws.InvalidOperationException.With.Message.Contains("budget"));
            Assert.That(l.Path, Is.Null); Assert.That(d.UndoCount, Is.EqualTo(undo)); Assert.That(l.GetChannel(PaintChannel.Color).TileCount, Is.Zero, "nothing changed");
        }
    }
}
