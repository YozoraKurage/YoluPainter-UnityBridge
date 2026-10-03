using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class SymmetryOffscreenTests
    {
        static void RequireOffscreen()
        {
            if (!Application.isBatchMode) Assert.Ignore("見た目は batch-gl のオフスクリーンで確認する");
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface");
        }
        static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [Test] public void SymmetryPanelDrawsInEnglishAndJapaneseWithoutCutLabels()
        {
            RequireOffscreen(); var w=ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.SetToolSectionsOpen(true); w.Symmetry=true; w.RadialSymmetry3D=true; w.RadialSymmetryCount=16; w.Brush.canvasSymmetry=CanvasSymmetryMode.Radial; w.Brush.canvasSymmetryCount=16;
                foreach(var language in new[]{PainterLanguage.English,PainterLanguage.Japanese})
                {
                    L.OverrideLanguage(language); PaintGui.ShortenedTexts=0;
                    string path=Path.GetFullPath(Path.Combine("Logs","YoluPainterSnapshots","Symmetry","panel-"+language+".png"));
                    OffscreenGui.RenderToPng(310,900,()=>{
                        var rows=new UiRows(new Rect(0,0,310,900),0); typeof(TexturePaintWindow).GetMethod("SymmetrySection",Private).Invoke(w,new object[]{rows});
                        Assert.That(rows.Used,Is.LessThan(900));
                    },path,PaintTheme.PanelBg);
                    Assert.That(File.Exists(path),Is.True); Assert.That(PaintGui.ShortenedTexts,Is.Zero,language.ToString());
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(w);L.OverrideLanguage(PainterLanguage.English);}
        }
        [Test] public void CanvasAxesMoveWithTheDocumentViewAndCanBeHidden()
        {
            RequireOffscreen(); var w=ScriptableObject.CreateInstance<TexturePaintWindow>();
            Texture2D plain=null,shown=null;
            try
            {
                w.View=TexturePaintWindow.ViewMode.Canvas; w.Brush.canvasSymmetry=CanvasSymmetryMode.Both; w.Brush.canvasSymmetryX=.37f; w.Brush.canvasSymmetryY=.62f;
                w.RotateCanvasView(37); w.FlipCanvasView();
                string root=Path.GetFullPath(Path.Combine("Logs","YoluPainterSnapshots","Symmetry"));
                w.SymmetryPlaneShown=false; OffscreenGui.RenderWindow(w,1200,800,Path.Combine(root,"canvas-hidden.png"));
                w.SymmetryPlaneShown=true; OffscreenGui.RenderWindow(w,1200,800,Path.Combine(root,"canvas-axes.png"));
                plain=new Texture2D(2,2); plain.LoadImage(File.ReadAllBytes(Path.Combine(root,"canvas-hidden.png")));
                shown=new Texture2D(2,2); shown.LoadImage(File.ReadAllBytes(Path.Combine(root,"canvas-axes.png")));
                var view=w.CanvasViewNow(); var lines=w.CanvasSymmetryLines(view); Assert.That(lines.Count,Is.EqualTo(4));
                var center=view.ToGui(w.Brush.canvasSymmetryX*w.Document.Width,w.Brush.canvasSymmetryY*w.Document.Height);
                int changes=0;
                for(int dy=-3;dy<=3;dy++)for(int dx=-3;dx<=3;dx++)
                {
                    int x=Mathf.RoundToInt(center.x)+dx,y=799-Mathf.RoundToInt(center.y)+dy;
                    if(plain.GetPixel(x,y)!=shown.GetPixel(x,y))changes++;
                }
                Assert.That(changes,Is.GreaterThan(5),"動かした画素座標の交点に青い線が描かれる");
                Assert.That(Vector2.Distance(lines[0],view.ToGui(w.Brush.canvasSymmetryX*w.Document.Width,0)),Is.LessThan(.001f));
            }
            finally{if(plain!=null)UnityEngine.Object.DestroyImmediate(plain);if(shown!=null)UnityEngine.Object.DestroyImmediate(shown);UnityEngine.Object.DestroyImmediate(w);}
        }
        [Test] public void RadialAxisIsAGuiOverlayAndLeavesThePreviewRenderKeyUnchanged()
        {
            RequireOffscreen(); var w=ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.View=TexturePaintWindow.ViewMode.Model; w.Preview.LoadDemoMesh();
                var rect=new Rect(0,0,400,400); var before=w.Preview.ComputeRenderKey(rect,1);
                w.RadialSymmetry3D=true; w.RadialSymmetryAxis=SymmetryAxis.Z; w.RadialSymmetryCount=7;
                Assert.That(w.Preview.ComputeRenderKey(rect,1),Is.EqualTo(before));
                string root=Path.GetFullPath(Path.Combine("Logs","YoluPainterSnapshots","Symmetry"));
                OffscreenGui.RenderWindow(w,1200,800,Path.Combine(root,"radial-axis.png"));
                w.SymmetryPlaneShown=false; OffscreenGui.RenderWindow(w,1200,800,Path.Combine(root,"radial-hidden.png"));
                Assert.That(File.ReadAllBytes(Path.Combine(root,"radial-axis.png")),Is.Not.EqualTo(File.ReadAllBytes(Path.Combine(root,"radial-hidden.png"))));
            }
            finally{UnityEngine.Object.DestroyImmediate(w);}
        }
    }
}
