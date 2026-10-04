using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class SurfaceBrushSamplingTests
    {
        const int W = 32, H = 16;
        static Vector3 P(float x, float y) => new Vector3(x, y, 0);
        internal static SurfaceTriangle[] Faces(bool mirrored = false, bool folded = false, int rightSlot = 0, float gap = 0, bool mirroredV = false)
        {
            Vector3 R(float x, float y) => folded ? new Vector3(1, y, x - 1) : P(x + gap, y);
            Vector2 L(float x, float y) => new Vector2(.375f * x, .125f + .75f * y);
            Vector2 U(float x, float y) => new Vector2(mirrored ? 1 - .375f * (x - 1) : .625f + .375f * (x - 1), .125f + .75f * (mirroredV ? 1 - y : y));
            return new[] {
                new SurfaceTriangle(P(0,0), P(1,0), P(1,1), L(0,0), L(1,0), L(1,1)),
                new SurfaceTriangle(P(0,0), P(1,1), P(0,1), L(0,0), L(1,1), L(0,1)),
                new SurfaceTriangle(R(1,0), R(2,0), R(2,1), U(1,0), U(2,0), U(2,1), 0, rightSlot),
                new SurfaceTriangle(R(1,0), R(2,1), R(1,1), U(1,0), U(2,1), U(1,1), 0, rightSlot) };
        }
        static SurfaceHit Hit(SurfaceGeometry g, Vector3 p, Vector3 normal = default)
        {
            if (normal == default) normal = Vector3.forward;
            Assert.That(g.TryRaycast(new Ray(p + normal * 3, -normal), out var hit), Is.True); return hit;
        }
        static BrushSettings Settings(BrushEffect effect) => new BrushSettings { Effect = effect, Flow = 1, Opacity = 1, SmudgeStrength = 1, PressureSize = false, PressureOpacity = false };
        static List<BrushMappedPixel> Plan(SurfaceGeometry.SamplingChart dest, SurfaceGeometry.SamplingChart source, IEnumerable<SurfacePixel> pixels, Vector2 offset)
        {
            var result = new List<BrushMappedPixel>();
            foreach (var p in pixels)
            {
                Assert.That(dest.TryCoordinates(p, out var point), Is.True);
                if (source.TrySample(point + offset, new BrushPixel(p.X, p.Y, p.Coverage), W, H, out var mapped)) result.Add(mapped);
            }
            return result;
        }
        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void CloneCopiesTheSurfacePatternAcrossSeparatedAndMirroredIslands(bool mirrored, bool mirroredV)
        {
            var g = new SurfaceGeometry(Faces(mirrored, mirroredV: mirroredV)); var source = Hit(g, P(.25f,.5f)); var dest = Hit(g, P(1.25f,.5f));
            var d = new PaintDocument(W,H,8); var layer = d.AddLayer("paint"); var surface = layer.GetChannel(PaintChannel.Color);
            for(int y=0;y<H;y++) for(int x=0;x<12;x++) surface.SetPixel(x,y,new Rgba32((byte)(x*17), (byte)(y*13),90));
            d.ClearHistory(); var before = DocumentBinary.Write(d);
            var a = g.BuildSamplingChart(dest,.6f); var b = g.BuildSamplingChart(source,.6f);
            var dab = g.BuildSurfaceDabs(dest,.3f,W,H,new Vector3(1,.5f,3),1);
            var plan = Plan(a,b,dab.Pixels,Vector2.zero); Assert.That(plan.Count,Is.GreaterThan(10));
            using(var s=d.BeginStroke(layer.Id,PaintChannel.Color,Settings(BrushEffect.Clone))) { s.ApplyMappedDab(plan);s.Commit(); }
            foreach(var p in dab.Pixels)
            {
                if(p.TriangleIndex<2) continue;
                // 世界 x の写し元は x−1。UV の鏡映を独立に逆算して画素の式と比べる。
                float wx = mirrored ? 1+(1-(p.X+.5f)/W)/.375f : 1+((p.X+.5f)/W-.625f)/.375f;
                int sx = Mathf.FloorToInt((wx-1)*.375f*W);
                if(sx>=0&&sx<12) Assert.That(surface.GetPixel(p.X,p.Y),Is.EqualTo(new Rgba32((byte)(sx*17),(byte)((mirroredV ? H-1-p.Y : p.Y)*13),90)), "UVの向きと画素 "+p.X+","+p.Y);
            }
            var after=DocumentBinary.Write(d); Assert.That(d.UndoCount,Is.EqualTo(1));d.Undo();Assert.That(DocumentBinary.Write(d),Is.EqualTo(before));
            d.Redo();Assert.That(DocumentBinary.Write(DocumentBinary.Read(after)),Is.EqualTo(DocumentBinary.Write(d)));
        }
        [TestCase(false)] [TestCase(true)]
        public void SmudgePullsAcrossASeamWithoutSamplingTheAtlasGap(bool mirrored)
        {
            var g=new SurfaceGeometry(Faces(mirrored));var previous=Hit(g,P(.875f,.5f));var current=Hit(g,P(1.125f,.5f));
            var d=new PaintDocument(W,H,4);var layer=d.AddLayer("paint");var surface=layer.GetChannel(PaintChannel.Color);
            for(int y=0;y<H;y++)for(int x=0;x<W;x++)surface.SetPixel(x,y,x<12?new Rgba32(220,30,70):x<20?new Rgba32(0,255,0):new Rgba32(0,0,0));
            var chart=g.BuildSamplingChart(current,.8f);Assert.That(chart.TryCoordinates(previous,out var offset),Is.True);Assert.That(offset.x,Is.EqualTo(-.25f).Within(1e-5));
            var dab=g.BuildSurfaceDabs(current,.2f,W,H,new Vector3(1,.5f,3),1); var plan=Plan(chart,chart,dab.Pixels,offset);
            using(var s=d.BeginStroke(layer.Id,PaintChannel.Color,Settings(BrushEffect.Smudge))){s.ApplyMappedDab(plan);s.Commit();}
            int changed=0;
            foreach(var p in dab.Pixels.Where(p=>p.TriangleIndex>=2))
            {
                var color=surface.GetPixel(p.X,p.Y);Assert.That(color.G,Is.LessThanOrEqualTo(30),"離れた島の間の緑を読まない");if(color.R>0)changed++;
            }
            Assert.That(changed,Is.GreaterThan(4));
        }
        [Test] public void BilinearTapsCrossTheGeometricEdgeInsteadOfReadingTheGap()
        {
            var g=new SurfaceGeometry(Faces());var hit=Hit(g,P(.99f,.5f));var chart=g.BuildSamplingChart(hit,.3f);
            Assert.That(chart.TrySample(Vector2.zero,new BrushPixel(0,0,1),W,H,out var sample),Is.True);
            var taps=new[]{sample.A,sample.B,sample.C,sample.D}.Where(t=>t.Weight>0).ToArray();
            Assert.That(taps.Any(t=>t.X<12),Is.True);Assert.That(taps.Any(t=>t.X>=20),Is.True);Assert.That(taps.All(t=>t.X<12||t.X>=20),Is.True);
            Assert.That(taps.Sum(t=>t.Weight),Is.EqualTo(1).Within(1e-6));
        }
        [Test] public void FoldedTrianglesUnfoldToTheirSurfaceDistanceAndStaleHitsRefuse()
        {
            var g=new SurfaceGeometry(Faces(folded:true));var a=Hit(g,P(.8f,.5f));var b=Hit(g,new Vector3(1,.5f,.2f),Vector3.left);
            var chart=g.BuildSamplingChart(a,1);Assert.That(chart.TryCoordinates(b,out var q),Is.True);
            Assert.That(q.x,Is.EqualTo(.4f).Within(1e-5));Assert.That(q.y,Is.EqualTo(0).Within(1e-5));
            var newer=new SurfaceGeometry(Faces(),2);Assert.Throws<InvalidOperationException>(()=>newer.BuildSamplingChart(a,1));
        }
        [TestCase("slot")] [TestCase("gap")] [TestCase("nonmanifold")]
        public void SamplingNeverCrossesAnUnconnectedOrAmbiguousEdge(string kind)
        {
            var faces=Faces(rightSlot:kind=="slot"?1:0,gap:kind=="gap"?.01f:0).ToList();
            if(kind=="nonmanifold")faces.Add(new SurfaceTriangle(P(1,0),P(1,1),new Vector3(1,.5f,1),Vector2.zero,Vector2.up,Vector2.one));
            var g=new SurfaceGeometry(faces);var anchor=Hit(g,P(.8f,.5f));var chart=g.BuildSamplingChart(anchor,3);
            Assert.That(chart.TriangleCount,Is.EqualTo(2));Assert.That(chart.TrySample(new Vector2(.4f,0),new BrushPixel(0,0,1),W,H,out _),Is.False);
        }
        [Test] public void ChartBudgetsReturnNoPartialPlanAndPixelsKeepTheirProvenance()
        {
            var g=new SurfaceGeometry(Faces());var hit=Hit(g,P(.9f,.5f));
            Assert.Throws<InvalidOperationException>(()=>g.BuildSamplingChart(hit,1,maxTriangles:1));Assert.Throws<InvalidOperationException>(()=>g.BuildSamplingChart(hit,1,maxBytes:127));
            var dab=g.BuildSurfaceDabs(hit,.3f,W,H,new Vector3(1,.5f,3),1);Assert.That(dab.Pixels.Any(p=>p.TriangleIndex>=2),Is.True);
            Assert.That(dab.Pixels.All(p=>p.TriangleIndex>=0),Is.True);
            foreach(var p in dab.Pixels)Assert.That(p.Position.z,Is.EqualTo(0).Within(1e-6));
        }
    }
}
