using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    public sealed partial class WindowTests
    {
        void PrepareCanvasSymmetry(CanvasSymmetryMode mode = CanvasSymmetryMode.Both)
        {
            window.View = TexturePaintWindow.ViewMode.Canvas;
            var b = window.Brush; b.canvasSymmetry = mode; b.canvasSymmetryX = b.canvasSymmetryY = .5f; b.canvasSymmetryCount = 4;
            b.radius = 12; b.hardness = 1; b.flow = .5f; b.pressureSize = b.pressureOpacity = false;
            Repaint(window);
        }
        [TestCase(CanvasSymmetryMode.Vertical)] [TestCase(CanvasSymmetryMode.Horizontal)] [TestCase(CanvasSymmetryMode.Both)] [TestCase(CanvasSymmetryMode.Radial)]
        public void CanvasSymmetrySendEventPaintsCopiesAndUndoRedo(CanvasSymmetryMode mode)
        {
            PrepareCanvasSymmetry(mode); var before = DocumentBinary.Write(window.Document); Click(At(window, 300, 400));
            Assert.That(window.Document.CompositePixel(PaintChannel.Color, 300,400).A, Is.GreaterThan(0));
            int w = window.Document.Width, h = window.Document.Height;
            if (mode == CanvasSymmetryMode.Vertical || mode == CanvasSymmetryMode.Both) Assert.That(window.Document.CompositePixel(PaintChannel.Color,w-301,400).A, Is.GreaterThan(0));
            if (mode == CanvasSymmetryMode.Horizontal || mode == CanvasSymmetryMode.Both) Assert.That(window.Document.CompositePixel(PaintChannel.Color,300,h-401).A, Is.GreaterThan(0));
            if (mode == CanvasSymmetryMode.Radial) Assert.That(window.Document.CompositePixel(PaintChannel.Color,400,w-301).A, Is.GreaterThan(0));
            Assert.That(window.Document.UndoCount, Is.EqualTo(1)); var after = DocumentBinary.Write(window.Document);
            Key(window,KeyCode.Z,EventModifiers.Control); Assert.That(DocumentBinary.Write(window.Document),Is.EqualTo(before));
            Key(window,KeyCode.Y,EventModifiers.Control); Assert.That(DocumentBinary.Write(window.Document),Is.EqualTo(after));
        }
        [Test] public void CanvasSymmetryIsIndependentOfViewRotationAndFlip()
        {
            PrepareCanvasSymmetry(); window.Brush.hardness=.5f; Click(At(window,300,400)); var plain=Snapshot(); window.Document.Undo();
            window.RotateCanvasView(37); window.FlipCanvasView(); Click(At(window,300,400)); var rotated=Snapshot();
            int worst=0; for(int i=3;i<plain.Length;i+=4)worst=Math.Max(worst,Math.Abs(plain[i]-rotated[i]));
            Assert.That(worst,Is.LessThanOrEqualTo(1),"GUIのfloat座標の往復を含めても覆いの差は1段以内");
        }
        [TestCase("Escape")] [TestCase("OnLostFocus")] [TestCase("BeforeReload")] [TestCase("Budget")]
        public void CanvasSymmetryCancellationRestoresAllCopies(string reason)
        {
            PrepareCanvasSymmetry(); var before=DocumentBinary.Write(window.Document);
            if(reason=="Budget")window.Document.ActiveStrokeBudgetBytes=1;
            Mouse(window,EventType.MouseDown,At(window,300,400)); Mouse(window,EventType.MouseDrag,At(window,320,400));
            if(reason=="Escape")Key(window,KeyCode.Escape); else if(reason!="Budget")Invoke(window,reason);
            Assert.That(window.IsStroking,Is.False); Assert.That(DocumentBinary.Write(window.Document),Is.EqualTo(before)); Assert.That(window.Document.UndoCount,Is.Zero);
            Mouse(window,EventType.MouseUp,At(window,320,400));
        }
        [Test] public void CanvasSymmetryFrozenSettingsAndOverlapUseOneDab()
        {
            PrepareCanvasSymmetry(CanvasSymmetryMode.Radial); window.Brush.canvasSymmetryCount=16;
            int c=window.Document.Width/2; Click(At(window,c,c));
            Assert.That(window.Document.CompositePixel(PaintChannel.Color,c,c).A,Is.InRange(126,129),"16 個重なっても 50% の流量で 1 回だけ"); window.Document.Undo();
            PrepareCanvasSymmetry(CanvasSymmetryMode.Vertical); Mouse(window,EventType.MouseDown,At(window,300,400));
            window.Brush.canvasSymmetry=CanvasSymmetryMode.None; Mouse(window,EventType.MouseDrag,At(window,330,400)); Mouse(window,EventType.MouseUp,At(window,330,400));
            Assert.That(window.Document.CompositePixel(PaintChannel.Color,window.Document.Width-331,400).A,Is.GreaterThan(0));
        }
        [Test] public void HiddenSymmetryCopiesPaintWithTheVisibilityOptionAndCancelOnFocusLoss()
        {
            LoadSymmetricBox(yaw:-90); window.Symmetry=true; window.SymmetryIgnoreVisibility=true;
            var before=Snapshot(); Click(SurfacePoint(0,0)); Assert.That(window.LastMirrorOutcome,Is.EqualTo(MirrorOutcome.Painted));
            Assert.That(PaintedOn(Snapshot(),true),Is.GreaterThan(30)); Assert.That(PaintedOn(Snapshot(),false),Is.GreaterThan(30));
            var after=Snapshot(); Key(window,KeyCode.Z,EventModifiers.Control); Assert.That(Snapshot(),Is.EqualTo(before));
            Mouse(window,EventType.MouseDown,SurfacePoint(0,0)); Invoke(window,"OnLostFocus"); Assert.That(Snapshot(),Is.EqualTo(before)); Assert.That(window.IsStroking,Is.False);
            Click(SurfacePoint(0,0)); Assert.That(Snapshot(),Is.EqualTo(after));
        }
        void LoadRadialPetals()
        {
            symmetricMesh=SymmetricBox.Mesh(SurfaceRadialSymmetryTests.Petals()); symmetricSource=new GameObject("Radial test source") {hideFlags=HideFlags.HideAndDontSave};
            symmetricSource.AddComponent<MeshFilter>().sharedMesh=symmetricMesh;
            symmetricMaterials=new[]{new Material(Shader.Find("Hidden/YoluPainter/PreviewSurface")){hideFlags=HideFlags.HideAndDontSave}};
            symmetricSource.AddComponent<MeshRenderer>().sharedMaterials=symmetricMaterials;
            Assert.That(window.Preview.Load(symmetricSource).CanPaint,Is.True); window.View=TexturePaintWindow.ViewMode.Model; window.Preview.ViewFrom(0,0);
            var b=window.Brush; b.radius=20; b.hardness=.5f; b.pressureSize=b.pressureOpacity=false; b.radialSymmetry3D=true; b.radialSymmetryAxis=SymmetryAxis.Z; b.radialSymmetryCount=4; b.symmetryIgnoreVisibility=true; Repaint(window);
        }
        [Test] public void Radial3DSendEventPaintsFourPetalsAndOneUndo()
        {
            LoadRadialPetals(); Assert.That(window.Preview.TryWorldToGui(window.SurfaceRect,Vector3.right,out var at),Is.True);
            var before=Snapshot(); Click(at); Assert.That(window.IsStroking,Is.False,window.StatusMessage); Assert.That(window.LastMirrorOutcome,Is.EqualTo(MirrorOutcome.Painted),window.StatusMessage);
            var rgba=Snapshot(); int w=window.Document.Width,h=window.Document.Height;
            for(int i=0;i<4;i++)Assert.That(Enumerable.Range(0,w*h).Count(k=>k%w*4/w==i && rgba[k*4+3]>0),Is.GreaterThan(20));
            Assert.That(window.Document.UndoCount,Is.EqualTo(1)); Key(window,KeyCode.Z,EventModifiers.Control); Assert.That(Snapshot(),Is.EqualTo(before));
        }
        [TestCase("Escape")] [TestCase("OnLostFocus")] [TestCase("Budget")]
        public void Radial3DSymmetryCancellationDoesNotLeavePaint(string reason)
        {
            LoadRadialPetals(); var before=Snapshot(); if(reason=="Budget")window.Preview.BrushBudget.MaxTriangles=3;
            window.Preview.TryWorldToGui(window.SurfaceRect,Vector3.right,out var at); Mouse(window,EventType.MouseDown,at);
            if(reason=="Escape")Key(window,KeyCode.Escape); else if(reason!="Budget")Invoke(window,reason);
            Assert.That(window.IsStroking,Is.False,window.StatusMessage); Assert.That(Snapshot(),Is.EqualTo(before)); Assert.That(window.Document.UndoCount,Is.Zero);
        }
        [Test] public void SymmetrySettingsSaveAndOpenYlpAndPresetAndOldSettingsUseDefaults()
        {
            var b=window.Brush; b.symmetry3D=true; b.symmetryAxis=SymmetryAxis.Z; b.symmetryOffset=.2f; b.radialSymmetry3D=true; b.radialSymmetryAxis=SymmetryAxis.X; b.radialSymmetryCount=9; b.symmetryIgnoreVisibility=true;
            b.canvasSymmetry=CanvasSymmetryMode.Radial; b.canvasSymmetryX=.3f; b.canvasSymmetryY=.7f; b.canvasSymmetryCount=12; b.symmetryAxesShown=false;
            string expected=JsonUtility.ToJson(b); var dialogs=UseFakeDialogs(window); dialogs.File=NewTempPath(".json"); Invoke(window,"SavePreset"); window.Brush=new TexturePaintWindow.BrushState(); Invoke(window,"LoadPreset"); Assert.That(JsonUtility.ToJson(window.Brush),Is.EqualTo(expected));
            dialogs.File=NewYlpPath(); window.SaveProject(true); Assert.That(window.IsSaved,Is.True,window.StatusMessage);
            var other=Open(); try {UseFakeDialogs(other).File=dialogs.File; other.OpenProject(); Assert.That(JsonUtility.ToJson(other.Brush),Is.EqualTo(expected),other.StatusMessage);} finally{Close(other);}
            var old=(TexturePaintWindow.BrushState)typeof(TexturePaintWindow).GetMethod("ReadBrushState",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{"{\"schema\":3,\"radius\":5}"});
            Assert.That((old.symmetry3D,old.radialSymmetry3D,old.canvasSymmetry,old.canvasSymmetryX,old.canvasSymmetryY,old.radialSymmetryCount),(Is.EqualTo((false,false,CanvasSymmetryMode.None,.5f,.5f,2))));
            window.ApplyPreset(BuiltInBrushes.Presets[1]); Assert.That((window.Brush.radialSymmetryCount,window.Brush.canvasSymmetryCount),(Is.EqualTo((9,12))));
        }
        [TestCase("canvasSymmetry", "99")] [TestCase("radialSymmetryCount", "17")] [TestCase("canvasSymmetryX", "2")]
        public void InvalidSymmetryPresetLeavesTheCurrentBrush(string field,string value)
        {
            var before=JsonUtility.ToJson(window.Brush); var dialogs=UseFakeDialogs(window); dialogs.File=NewTempPath(".json");
            System.IO.File.WriteAllText(dialogs.File,"{\"schema\":3,\""+field+"\":"+value+"}"); Invoke(window,"LoadPreset"); Assert.That(JsonUtility.ToJson(window.Brush),Is.EqualTo(before)); Assert.That(window.StatusMessage,Does.Contain("Unsupported symmetry"));
        }
        [TestCase("Smudge")] [TestCase("Clone")]
        public void CanvasSymmetryRefusesSourceDependentEffectsWithAReason(string toolName)
        {
            PrepareCanvasSymmetry(); window.SelectTool((TexturePaintWindow.PaintTool)Enum.Parse(typeof(TexturePaintWindow.PaintTool),toolName)); var before=Snapshot(); Click(At(window,300,400)); Assert.That(window.IsStroking,Is.False); Assert.That(Snapshot(),Is.EqualTo(before)); Assert.That(window.StatusMessage,Does.Contain("separate source"));
        }
        [Test] public void ExpandedSymmetryControlsChangeSettingsWithoutEditingTheDocument()
        {
            LoadSymmetricBox();window.SetToolSectionsOpen(true);
            ClickToolControl("symmetry-radial");Assert.That(window.RadialSymmetry3D,Is.True);
            ClickToolControl("symmetry-radial-Z");Assert.That(window.RadialSymmetryAxis,Is.EqualTo(SymmetryAxis.Z));
            ClickToolControl("symmetry-visibility");Assert.That(window.SymmetryIgnoreVisibility,Is.True);
            ClickToolControl("symmetry-2d-Both");Assert.That(window.Brush.canvasSymmetry,Is.EqualTo(CanvasSymmetryMode.Both));
            window.Brush.canvasSymmetryX=.3f;window.Brush.canvasSymmetryY=.7f;
            ClickToolControl("symmetry-2d-center");Assert.That((window.Brush.canvasSymmetryX,window.Brush.canvasSymmetryY),Is.EqualTo((.5f,.5f)));
            Assert.That(window.Document.UndoCount,Is.Zero);
        }
        [Test] public void LegacyWindowSymmetryMovesIntoBrushStateOnce()
        {
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;var type=typeof(TexturePaintWindow);
            type.GetField("symmetryStateMigrated",flags).SetValue(window,false);type.GetField("legacySymmetry",flags).SetValue(window,true);
            type.GetField("legacySymmetryAxis",flags).SetValue(window,SymmetryAxis.Y);type.GetField("legacySymmetryOffset",flags).SetValue(window,.125f);type.GetField("legacySymmetryPlaneShown",flags).SetValue(window,false);
            Invoke(window,"MigrateSymmetryState");Assert.That((window.Symmetry,window.SymmetryAxis,window.SymmetryOffset,window.SymmetryPlaneShown),Is.EqualTo((true,SymmetryAxis.Y,.125f,false)));
            window.Symmetry=false;Invoke(window,"MigrateSymmetryState");Assert.That(window.Symmetry,Is.False,"移行は再読み込みしたbrush設定を上書きしない");
        }
        [Test] public void RadialSymmetryKeepsCopiesAndVisibilityFrozenDuringTheStroke()
        {
            LoadRadialPetals();window.Preview.TryWorldToGui(window.SurfaceRect,Vector3.right,out var at);
            Mouse(window,EventType.MouseDown,at);window.RadialSymmetry3D=false;window.RadialSymmetryCount=2;window.SymmetryIgnoreVisibility=false;
            Mouse(window,EventType.MouseDrag,at+new Vector2(0,4));Mouse(window,EventType.MouseUp,at+new Vector2(0,4));
            var rgba=Snapshot();int w=window.Document.Width,h=window.Document.Height;
            for(int i=0;i<4;i++)Assert.That(Enumerable.Range(0,w*h).Count(k=>k%w*4/w==i && rgba[k*4+3]>0),Is.GreaterThan(20));
            Assert.That(window.Document.UndoCount,Is.EqualTo(1));
        }
        [Test] public void BlurWithCanvasSymmetryAndMaterialChannelsCommitsAndUndoesTogether()
        {
            PrepareCanvasSymmetry();window.SelectTool(TexturePaintWindow.PaintTool.Blur);window.SetMaterialMode(true);window.SetMaterialChannel(PaintChannel.Height,true);
            var layer=window.Document.GetLayer(window.SelectedLayer);int tile=window.Document.TileSize;var bytes=new byte[tile*tile*4];
            for(int y=0;y<tile;y++)for(int x=0;x<tile;x++){int k=(y*tile+x)*4;bytes[k]=(byte)(x%4<2?20:240);bytes[k+1]=(byte)(y%4<2?40:200);bytes[k+2]=70;bytes[k+3]=255;}
            foreach(var c in new[]{PaintChannel.Color,PaintChannel.Height})foreach(var coord in window.Document.EnumerateCanvasTiles())layer.GetChannel(c).ImportTile(coord,bytes);
            window.Document.ClearHistory();var before=DocumentBinary.Write(window.Document);Click(At(window,300,400));var after=DocumentBinary.Write(window.Document);
            Assert.That(after,Is.Not.EqualTo(before));Assert.That(window.Document.UndoCount,Is.EqualTo(1));
            foreach(var c in new[]{PaintChannel.Color,PaintChannel.Height})Assert.That(layer.GetPixel(c,300,400),Is.Not.EqualTo(layer.GetPixel(c,300,380)));
            Key(window,KeyCode.Z,EventModifiers.Control);Assert.That(DocumentBinary.Write(window.Document),Is.EqualTo(before));
        }
    }
}
