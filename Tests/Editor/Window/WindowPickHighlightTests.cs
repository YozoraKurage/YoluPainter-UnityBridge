using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    public sealed partial class WindowTests
    {
        [TestCase((int)TexturePaintWindow.PaintTool.SelectRectangle)]
        [TestCase((int)TexturePaintWindow.PaintTool.SelectEllipse)]
        [TestCase((int)TexturePaintWindow.PaintTool.Lasso)]
        [TestCase((int)TexturePaintWindow.PaintTool.MagicWand)]
        [TestCase((int)TexturePaintWindow.PaintTool.Fill)]
        public void SurfaceSelectionAndBucketHoverUseTheSameRegionAsTheClick(int toolId)
        {
            var geometry=PolygonFillCube(); window.Tool=(TexturePaintWindow.PaintTool)toolId; var pointer=window.SurfaceRect.center; var d=window.Document; var before=DocumentBinary.Write(d);
            Assert.That(window.Preview.TryPick(window.SurfaceRect,pointer,out var hit),Is.True);
            var index=new SurfaceRegionIndex(geometry); Mouse(window,EventType.MouseMove,pointer); Repaint(window);
            Assert.That(window.PolygonFillHoverKey,Is.EqualTo(index.Key(hit.TriangleIndex,window.SurfacePick)));
            Assert.That(DocumentBinary.Write(d),Is.EqualTo(before),"hover changes no source or undo");
            int lookups=window.PolygonFillHoverLookups;
            for(int i=0;i<20;i++) { Mouse(window,EventType.MouseMove,pointer+new Vector2(i*.1f,0)); Repaint(window); }
            Assert.That(window.PolygonFillHoverLookups,Is.EqualTo(lookups));
            Mouse(window,EventType.MouseMove,window.CanvasRect.center); Assert.That(window.PolygonFillHoverKey,Is.EqualTo(-1),"these tools keep 2D pixel selection");
            Mouse(window,EventType.MouseMove,pointer); Invoke(window,"OnLostFocus"); Repaint(window); Assert.That(window.PolygonFillHoverKey,Is.EqualTo(-1));
        }
        [Test] public void IdToolAndGeneratorHoverUseTheClickedMapColourAndClearWhenUnavailable()
        {
            IdCube(); window.Tool=TexturePaintWindow.PaintTool.IdSelect; var pointer=window.SurfaceRect.center; var before=DocumentBinary.Write(window.Document);
            Assert.That(window.Preview.TryPick(window.SurfaceRect,pointer,out var hit),Is.True); Assert.That(IdMapColors.TryGetAtUv(BakedIdMap(),hit.UV.x,hit.UV.y,out int rgb),Is.True);
            Mouse(window,EventType.MouseMove,pointer); Repaint(window); Assert.That(window.IdPickHoverRgb,Is.EqualTo(rgb)); Assert.That(DocumentBinary.Write(window.Document),Is.EqualTo(before));
            window.Tool=TexturePaintWindow.PaintTool.Brush;
            var filter=window.Document.AddFilter(window.SelectedLayer,FilterTarget.Content,FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.IdColor)));
            window.SelectedFilter=filter.Id; window.BeginIdColorPick(filter.Id); Mouse(window,EventType.MouseMove,pointer); Repaint(window); Assert.That(window.IdPickHoverRgb,Is.EqualTo(rgb));
            Key(window,KeyCode.Escape); Repaint(window); Assert.That(window.IdPickHoverRgb,Is.Null);
            window.Tool=TexturePaintWindow.PaintTool.IdSelect; window.MeshBakeSettings.IdSource=MeshIdSource.Mesh; Mouse(window,EventType.MouseMove,pointer); Assert.That(window.IdPickHoverRgb,Is.Null,"stale maps do not highlight");
        }
        [Test] public void ManualColourEditIsSavedUndoableAndMakesTheIdMapStaleUntilRebaked()
        {
            IdCube(); var d=window.Document; var index=window.IdParts(); var pointer=window.SurfaceRect.center; Assert.That(window.Preview.TryPick(window.SurfaceRect,pointer,out var hit),Is.True);
            int part=index.Parts[hit.TriangleIndex]; var before=DocumentBinary.Write(d); int count=d.UndoCount;
            var gen = IdColorGenerator();
            Assert.That(IdMapColors.TryGetAtUv(BakedIdMap(),hit.UV.x,hit.UV.y,out int original),Is.True);
            d.SetFilterSettings(window.SelectedLayer,gen.Id,gen.Settings.WithGenerator(gen.Settings.Generator.WithIdColors(new[]{original})));
            var prior = d.GeneratorInputsRevision;
            Assert.That(d.GetGeneratorStatus(window.SelectedLayer,gen.Id).Active,Is.True);
            before=DocumentBinary.Write(d); count=d.UndoCount;
            window.AssignIdPartColor(part,0x2468AC); Assert.That(d.UndoCount,Is.EqualTo(count+1)); Assert.That(window.UsableIdMap(out _),Is.Null);
            Assert.That(d.GetGeneratorStatus(window.SelectedLayer,gen.Id).Active,Is.False);
            Assert.That(d.GeneratorInputsRevision,Is.GreaterThan(prior));
            var restored=DocumentBinary.Read(DocumentBinary.Write(d)); Assert.That(restored.IdColors.Colors[part],Is.EqualTo(0x2468AC));
            Key(window,KeyCode.Z,EventModifiers.Control); Assert.That(DocumentBinary.Write(d),Is.EqualTo(before)); Assert.That(window.UsableIdMap(out _),Is.Not.Null);
            Key(window,KeyCode.Z,EventModifiers.Control|EventModifiers.Shift); Assert.That(window.BakeMeshMaps(),Is.EqualTo(MeshBakeStatus.Completed));
            Assert.That(IdMapColors.TryGetAtUv(BakedIdMap(),hit.UV.x,hit.UV.y,out int rgb),Is.True); Assert.That(rgb,Is.EqualTo(0x2468AC));
        }
        [Test] public void OverlappingUvCandidatesCycleBeforePaintingAndTheChosenTriangleControlsFillUndoAndCancel()
        {
            var root=new GameObject("Overlap model"); var mesh=new Mesh {name="Overlap mesh"};
            try
            {
                mesh.vertices=new[]{new Vector3(0,0,0),new Vector3(0,1,0),new Vector3(1,0,0),new Vector3(2,0,0),new Vector3(2,1,0),new Vector3(3,0,0)};
                mesh.uv=new[]{new Vector2(.1f,.1f),new Vector2(.1f,.7f),new Vector2(.7f,.1f),new Vector2(.3f,.1f),new Vector2(.3f,.7f),new Vector2(.9f,.1f)}; mesh.triangles=new[]{0,1,2,3,4,5}; mesh.RecalculateNormals();
                root.AddComponent<MeshFilter>().sharedMesh=mesh; root.AddComponent<MeshRenderer>(); window.Preview.Load(root);
                window.Tool=TexturePaintWindow.PaintTool.PolygonFill; window.SurfacePick=SurfaceRegionKind.Triangle; window.Brush.color=Color.red; window.Brush.opacity=1; Repaint(window);
                var point=At(window,410,205); Mouse(window,EventType.MouseMove,point); Repaint(window); Assert.That(window.PolygonOverlapCandidates,Is.EqualTo(new[]{0,1}));
                Mouse(window,EventType.MouseMove,Vector2.zero); Repaint(window);
                window.ChoosePolygonOverlap(1); Assert.That(window.PolygonOverlapChoice,Is.EqualTo(1),"the options menu can choose the last canvas candidates");
                window.ChoosePolygonOverlap(0);
                var before=Snapshot(); int count=window.Document.UndoCount; Key(window,KeyCode.Tab); Assert.That(window.PolygonOverlapChoice,Is.EqualTo(1)); Assert.That(window.PolygonFillHoverKey,Is.EqualTo(new SurfaceRegionIndex(window.Preview.Geometry).Key(1,SurfaceRegionKind.Triangle)));
                Assert.That(Snapshot(),Is.EqualTo(before)); Assert.That(window.Document.UndoCount,Is.EqualTo(count));
                Mouse(window,EventType.MouseDown,point); Mouse(window,EventType.MouseUp,point); var painted=Snapshot(); Assert.That(painted,Is.Not.EqualTo(before)); Assert.That(window.Document.UndoCount,Is.EqualTo(count+1));
                Assert.That(LayerAlpha(new Vector2Int(700,150)),Is.EqualTo(255),"area unique to triangle 1 is filled"); Assert.That(LayerAlpha(new Vector2Int(150,150)),Is.Zero,"area unique to triangle 0 is untouched");
                Key(window,KeyCode.Z,EventModifiers.Control); Assert.That(Snapshot(),Is.EqualTo(before)); Key(window,KeyCode.Tab,EventModifiers.Shift); Assert.That(window.PolygonOverlapChoice,Is.Zero);
                Mouse(window,EventType.MouseDown,point); Key(window,KeyCode.Escape); Assert.That(Snapshot(),Is.EqualTo(before)); Assert.That(window.Document.HasActiveStroke,Is.False);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }
    }
}
