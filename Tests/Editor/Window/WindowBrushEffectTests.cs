using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    public sealed partial class WindowTests
    {
        void PrepareEffect(TexturePaintWindow.PaintTool tool)
        {
            window.View = TexturePaintWindow.ViewMode.Canvas;
            var b = window.Brush; b.radius = 5; b.hardness = 1; b.spacing = .2f; b.opacity = 1; b.flow = 1; b.blurRadius = 2; b.smudgeStrength = 1;
            b.pressureSize = b.pressureOpacity = b.pressureFlow = false; b.randomSeedPerStroke = false; window.Brush = b;
            var s = window.Document.GetLayer(window.SelectedLayer).GetChannel(PaintChannel.Color);
            for (int y = 290; y <= 310; y++) for (int x = 180; x <= 390; x++) s.SetPixel(x, y, new Rgba32((byte)(x % 3 == 0 ? 20 : 230), (byte)(x % 5 * 40), 50, 255));
            window.Document.ClearHistory(); window.SelectTool(tool);
            if (tool == TexturePaintWindow.PaintTool.Clone) CloneSourceClick(200, 300);
            Repaint(window);
        }
        void CloneSourceClick(int x, int y)
        {
            var at = At(window, x, y);
            window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = at + window.rootVisualElement.worldBound.position, button = 0, pressure = 1, modifiers = EventModifiers.Alt });
            Mouse(window, EventType.MouseUp, at);
        }
        void EffectLine()
        { Mouse(window, EventType.MouseDown, At(window, 300, 300)); Mouse(window, EventType.MouseDrag, At(window, 335, 300)); }

        [Test] public void BrushEffectsKeysStayInTheFocusedLayerNameField()
        {
            foreach (var item in new[] { (KeyCode.S, EventModifiers.None, 's'), (KeyCode.U, EventModifiers.None, 'u'), (KeyCode.U, EventModifiers.Shift, 'U') })
            {
                window.SelectTool(TexturePaintWindow.PaintTool.Brush);
                typeof(TexturePaintWindow).GetField("renamingLayer", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, window.SelectedLayer);
                var at = LayerPanelPoint("row." + window.SelectedLayer, .7f);
                HostMouse(EventType.MouseDown, at); HostMouse(EventType.MouseUp, at);
                var old = window.Document.GetLayer(window.SelectedLayer).Name;
                window.SendEvent(new Event { type = EventType.KeyDown, keyCode = item.Item1, modifiers = item.Item2, character = item.Item3 });
                Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.Brush), "文字の欄ではツールを替えない");
                Assert.That(((IPainterShortcutScope)window).EditingText, Is.True);
                Key(window, KeyCode.Return); // 自前の文字欄は Enter かフォーカスを外したときに確定する
                Assert.That(window.Document.GetLayer(window.SelectedLayer).Name, Is.Not.EqualTo(old), "文字はレイヤー名へ届く");
            }
        }
        [TestCase("Blur")] [TestCase("Smudge")] [TestCase("Clone")]
        public void BrushEffectsDragCommitAsOneUndoAndRedo(string toolName)
        {
            var tool = (TexturePaintWindow.PaintTool)Enum.Parse(typeof(TexturePaintWindow.PaintTool), toolName);
            PrepareEffect(tool); var before = DocumentBinary.Write(window.Document);
            EffectLine(); Assert.That(window.IsStroking, Is.True); Mouse(window, EventType.MouseUp, At(window, 335, 300));
            Assert.That(window.IsStroking, Is.False); var after = DocumentBinary.Write(window.Document); Assert.That(after, Is.Not.EqualTo(before));
            Assert.That(window.Document.UndoCount, Is.EqualTo(1)); Key(window, KeyCode.Z, EventModifiers.Control); Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(before));
            Key(window, KeyCode.Y, EventModifiers.Control); Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(after));
        }
        [TestCase("Blur", "Escape")] [TestCase("Smudge", "Escape")] [TestCase("Clone", "Escape")]
        [TestCase("Blur", "OnLostFocus")] [TestCase("Smudge", "OnLostFocus")] [TestCase("Clone", "OnLostFocus")]
        [TestCase("Blur", "BeforeReload")] [TestCase("Smudge", "BeforeReload")] [TestCase("Clone", "BeforeReload")]
        [TestCase("Blur", "Play")] [TestCase("Smudge", "Play")] [TestCase("Clone", "Play")]
        [TestCase("Blur", "Budget")] [TestCase("Smudge", "Budget")] [TestCase("Clone", "Budget")]
        public void BrushEffectsLeaveNoStrokeOrPixelsAfterCancellation(string toolName, string reason)
        {
            var tool = (TexturePaintWindow.PaintTool)Enum.Parse(typeof(TexturePaintWindow.PaintTool), toolName);
            PrepareEffect(tool); var before = DocumentBinary.Write(window.Document);
            if (reason == "Budget") window.Document.ActiveStrokeBudgetBytes = 1;
            EffectLine();
            if (reason == "Budget") Mouse(window, EventType.MouseUp, At(window, 335, 300)); // 指先の最初の点は拾うだけ。曲線の最後の区間は離したときに描く
            if (reason == "Escape") Key(window, KeyCode.Escape);
            else if (reason == "Play") Invoke(window, "PlayModeChanged", PlayModeStateChange.ExitingEditMode);
            else if (reason != "Budget") Invoke(window, reason);
            Assert.That(window.IsStroking, Is.False); Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(before)); Assert.That(window.Document.UndoCount, Is.Zero);
            Mouse(window, EventType.MouseUp, At(window, 335, 300)); Assert.That(window.IsStroking, Is.False);
        }
        [Test] public void BrushEffectsKeysSelectToolsAndJoinTheShortcutGuard()
        {
            Key(window, KeyCode.S); Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.Clone));
            var scope = (IPainterShortcutScope)window; Assert.That(scope.TookKey(KeyCode.S, EventModifiers.None), Is.True);
            Key(window, KeyCode.U); Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.Blur)); Assert.That(scope.TookKey(KeyCode.U, EventModifiers.None), Is.True);
            Key(window, KeyCode.U, EventModifiers.Shift); Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.Smudge)); Assert.That(scope.TookKey(KeyCode.U, EventModifiers.Shift), Is.True);
            Key(window, KeyCode.R); Assert.That(window.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.Smudge), "R は表示の回転");
            // 純粋なガードの境界（文字の欄の実キーは既存の ShortcutGuardTests で確認）
            Assert.That(ShortcutGuard.ShouldBlock(ShortcutGuardMode.BlockPainterKeys, true, false), Is.True);
        }
        [TestCase("Blur")] [TestCase("Smudge")]
        public void BrushEffectsOperateThroughSurfaceDabsAndUndo(string toolName)
        {
            var tool = (TexturePaintWindow.PaintTool)Enum.Parse(typeof(TexturePaintWindow.PaintTool), toolName);
            PrepareEffect(tool); window.View = TexturePaintWindow.ViewMode.Model; window.Preview.LoadDemoMesh(); window.Brush.radius = 14;
            var pixels = window.Document.GetLayer(window.SelectedLayer).GetChannel(PaintChannel.Color); int tile = pixels.TileSize;
            var bytes = new byte[tile * tile * 4];
            for (int y = 0; y < tile; y++) for (int x = 0; x < tile; x++)
            { int n = (y * tile + x) * 4; bytes[n] = (byte)(x % 4 < 2 ? 30 : 230); bytes[n + 1] = (byte)(y % 4 < 2 ? 40 : 210); bytes[n + 2] = 90; bytes[n + 3] = 255; }
            for (int y = 0; y < window.Document.Height / tile; y++) for (int x = 0; x < window.Document.Width / tile; x++) pixels.ImportTile(new TileCoord(x, y), bytes);
            window.Document.ClearHistory(); Repaint(window); var before = DocumentBinary.Write(window.Document); var at = window.SurfaceRect.center;
            Assert.That(window.Preview.TryPick(window.SurfaceRect, at, out _), Is.True);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseDrag, at + new Vector2(9, 2)); Mouse(window, EventType.MouseUp, at + new Vector2(9, 2));
            Assert.That(window.IsStroking, Is.False, window.StatusMessage); Assert.That(window.Document.UndoCount, Is.EqualTo(1), window.StatusMessage);
            var after = DocumentBinary.Write(window.Document); Assert.That(after, Is.Not.EqualTo(before)); window.Document.Undo(); Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(before));
            window.Document.Redo(); Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(after));
        }
        [Test] public void CloneSourceAltClickAlignedAndRestartedOffsets()
        {
            PrepareEffect(TexturePaintWindow.PaintTool.Clone); Assert.That(window.HasCloneSource, Is.True); Assert.That(window.CloneSource, Is.EqualTo(new Vector2(200.5f, 300.5f)));
            Assert.That(window.Document.UndoCount, Is.Zero, "写し元の指定は画素も履歴も変えない");
            void Dot(int x) { Mouse(window, EventType.MouseDown, At(window, x, 300)); Mouse(window, EventType.MouseUp, At(window, x, 300)); }
            var layer = window.Document.GetLayer(window.SelectedLayer); var source200 = layer.GetPixel(PaintChannel.Color, 200, 300); var source240 = layer.GetPixel(PaintChannel.Color, 240, 300);
            Dot(300); Assert.That(layer.GetPixel(PaintChannel.Color, 300, 300), Is.EqualTo(source200));
            Dot(340); Assert.That(layer.GetPixel(PaintChannel.Color, 340, 300), Is.EqualTo(source240)); Assert.That(window.CloneOffset, Is.EqualTo(new Vector2(-100, 0)));
            window.Brush.cloneAligned = false; Dot(360); Assert.That(layer.GetPixel(PaintChannel.Color, 360, 300), Is.EqualTo(source200));
            window.Document.AddLayerMask(window.SelectedLayer); window.EditMask = true; Assert.That(window.HasCloneSource, Is.False, "別の編集面には写し元を持ち込まない");
        }
        [Test] public void CloneNeedsASourceInTheSameView()
        {
            window.SelectTool(TexturePaintWindow.PaintTool.Clone); Mouse(window, EventType.MouseDown, At(window, 300, 300));
            Assert.That(window.IsStroking, Is.False); Assert.That(window.StatusMessage, Does.Contain("Alt-click"));
            window.View = TexturePaintWindow.ViewMode.Split; window.Preview.LoadDemoMesh(); Repaint(window);
            Mouse(window, EventType.MouseDown, window.SurfaceRect.center); Assert.That(window.IsStroking, Is.False); Assert.That(window.StatusMessage, Does.Contain("Alt-click"));
        }
        [Test] public void BrushEffectParametersSaveAndOpenYlpAndPresetFiles()
        {
            var b = window.Brush; b.blurRadius = 21; b.smudgeStrength = .37f; b.cloneAligned = false; b.cloneAllLayers = true;
            var dialogs = UseFakeDialogs(window); dialogs.File = NewTempPath(".json");
            Invoke(window, "SavePreset"); window.Brush = new TexturePaintWindow.BrushState(); Invoke(window, "LoadPreset");
            Assert.That((window.Brush.blurRadius, window.Brush.smudgeStrength, window.Brush.cloneAligned, window.Brush.cloneAllLayers), Is.EqualTo((21, .37f, false, true)));
            dialogs.File = NewYlpPath(); window.SaveProject(true); Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = dialogs.File; other.OpenProject();
                Assert.That((other.Brush.blurRadius, other.Brush.smudgeStrength, other.Brush.cloneAligned, other.Brush.cloneAllLayers), Is.EqualTo((21, .37f, false, true)));
                Assert.That(other.HasCloneSource, Is.False, "写し元は保存した文書の画素ではなく一時的な入力状態");
            }
            finally { Close(other); }
        }
        [Test] public void BrushEffectParametersRoundTripInBrushJsonAndPresetJsonWithoutSchemaChange()
        {
            var b = window.Brush; b.blurRadius = 17; b.smudgeStrength = .72f; b.cloneAligned = false; b.cloneAllLayers = true;
            var json = JsonUtility.ToJson(b); var read = (TexturePaintWindow.BrushState)typeof(TexturePaintWindow).GetMethod("ReadBrushState", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { json });
            Assert.That((read.schema, read.blurRadius, read.smudgeStrength, read.cloneAligned, read.cloneAllLayers), Is.EqualTo((3, 17, .72f, false, true)));
            var old = (TexturePaintWindow.BrushState)typeof(TexturePaintWindow).GetMethod("ReadBrushState", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { "{\"schema\":3,\"radius\":5}" });
            Assert.That((old.blurRadius, old.smudgeStrength, old.cloneAligned, old.cloneAllLayers), Is.EqualTo((3, .5f, true, false)));
            window.ApplyPreset(BuiltInBrushes.Presets[1]); Assert.That((window.Brush.blurRadius, window.Brush.smudgeStrength, window.Brush.cloneAligned, window.Brush.cloneAllLayers), Is.EqualTo((17, .72f, false, true)));
        }

        void LoadBrushSamplingSurface(bool mirrored = false)
        {
            symmetricMesh = SymmetricBox.Mesh(SurfaceBrushSamplingTests.Faces(mirrored));
            symmetricSource = new GameObject("Brush sampling source") { hideFlags = HideFlags.HideAndDontSave };
            symmetricSource.AddComponent<MeshFilter>().sharedMesh = symmetricMesh;
            symmetricMaterials = new[] { new Material(Shader.Find("Hidden/YoluPainter/PreviewSurface")) { hideFlags = HideFlags.HideAndDontSave } };
            symmetricSource.AddComponent<MeshRenderer>().sharedMaterials = symmetricMaterials;
            Assert.That(window.Preview.Load(symmetricSource).CanPaint, Is.True);
            window.View = TexturePaintWindow.ViewMode.Model; Repaint(window); window.Preview.ViewFrom(180, 0); Repaint(window);
            var b = window.Brush; b.radius = 24; b.hardness = 1; b.opacity = b.flow = b.smudgeStrength = 1;
            b.pressureSize = b.pressureOpacity = b.pressureFlow = false; window.Brush = b;
            var surface = window.Document.GetLayer(window.SelectedLayer).GetChannel(PaintChannel.Color);
            for (int y = window.Document.Height/8; y < window.Document.Height*7/8; y++)
                for (int x = 0; x < window.Document.Width*3/8; x++) surface.SetPixel(x,y,new Rgba32(210,30,70));
            window.Document.ClearHistory();
        }
        Vector2 BrushSurfacePoint(float x, float y = .5f)
        {
            Assert.That(window.Preview.TryWorldToGui(window.SurfaceRect, new Vector3(x,y,0), out var p), Is.True);
            Assert.That(window.Preview.TryPick(window.SurfaceRect, p, out _), Is.True); return p;
        }
        void SurfaceCloneSourceClick(Vector2 at)
        {
            window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = at + window.rootVisualElement.worldBound.position, button = 0, modifiers = EventModifiers.Alt });
            Mouse(window, EventType.MouseUp, at);
        }
        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void CloneIn3DCopiesAcrossUvIslandsAndUndoesInOneStep(bool mirrored, bool composite)
        {
            LoadBrushSamplingSurface(mirrored);
            if (composite) { var target = window.Document.AddLayer("clone"); window.SelectedLayer = target.Id; window.Brush.cloneAllLayers = true; window.Document.ClearHistory(); }
            window.SelectTool(TexturePaintWindow.PaintTool.Clone);
            SurfaceCloneSourceClick(BrushSurfacePoint(.25f)); Assert.That(window.HasSurfaceCloneSource, Is.True);
            var before = DocumentBinary.Write(window.Document); var at = BrushSurfacePoint(1.25f);
            Mouse(window,EventType.MouseDown,at);Mouse(window,EventType.MouseUp,at);
            Assert.That(window.IsStroking,Is.False,window.StatusMessage);Assert.That(window.Document.UndoCount,Is.EqualTo(1),window.StatusMessage);
            window.Preview.TryPick(window.SurfaceRect,at,out var hit);
            var color=window.Document.GetLayer(window.SelectedLayer).GetPixel(PaintChannel.Color,Mathf.FloorToInt(hit.UV.x*window.Document.Width),Mathf.FloorToInt(hit.UV.y*window.Document.Height));
            Assert.That(color,Is.EqualTo(new Rgba32(210,30,70)));
            var after=DocumentBinary.Write(window.Document);window.Document.Undo();Assert.That(DocumentBinary.Write(window.Document),Is.EqualTo(before));
            window.Document.Redo();Assert.That(DocumentBinary.Write(window.Document),Is.EqualTo(after));
            window.Preview.LoadDemoMesh();Assert.That(window.HasCloneSource,Is.False,"モデルの世代をまたいで参照しない");
        }
        [TestCase("Escape")] [TestCase("OnLostFocus")] [TestCase("BeforeReload")] [TestCase("Play")] [TestCase("Budget")]
        public void SurfaceCloneCancellationLeavesNoPixelsStrokeOrAlignment(string reason)
        {
            LoadBrushSamplingSurface();window.SelectTool(TexturePaintWindow.PaintTool.Clone);SurfaceCloneSourceClick(BrushSurfacePoint(.25f));
            var before=DocumentBinary.Write(window.Document);if(reason=="Budget")window.Document.ActiveStrokeBudgetBytes=1;
            var at=BrushSurfacePoint(1.25f);Mouse(window,EventType.MouseDown,at);
            if(reason=="Escape")Key(window,KeyCode.Escape);else if(reason=="Play")Invoke(window,"PlayModeChanged",PlayModeStateChange.ExitingEditMode);
            else if(reason!="Budget")Invoke(window,reason);
            Assert.That(window.IsStroking,Is.False,window.StatusMessage);Assert.That(DocumentBinary.Write(window.Document),Is.EqualTo(before));Assert.That(window.Document.UndoCount,Is.Zero);
            Assert.That((bool)typeof(TexturePaintWindow).GetField("cloneOffsetValid",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window),Is.False);
            Mouse(window,EventType.MouseUp,at);Assert.That(window.IsStroking,Is.False);
        }
        [TestCase(false)] [TestCase(true)]
        public void SurfaceSmudgeCarriesColorAcrossTheSeamAndMirroredUvDirection(bool mirrored)
        {
            LoadBrushSamplingSurface(mirrored);window.SelectTool(TexturePaintWindow.PaintTool.Smudge);
            var before=DocumentBinary.Write(window.Document);var a=BrushSurfacePoint(.95f);var b=BrushSurfacePoint(1.05f);
            Mouse(window,EventType.MouseDown,a);Mouse(window,EventType.MouseDrag,b);Mouse(window,EventType.MouseUp,b);
            Assert.That(window.IsStroking,Is.False,window.StatusMessage);Assert.That(window.Document.UndoCount,Is.EqualTo(1),window.StatusMessage);
            var surface=window.Document.GetLayer(window.SelectedLayer).GetChannel(PaintChannel.Color);int count=0;
            for(int y=window.Document.Height/3;y<window.Document.Height*2/3;y++)for(int x=window.Document.Width*5/8;x<window.Document.Width;x++)if(surface.GetPixel(x,y).R>0)count++;
            Assert.That(count,Is.GreaterThan(5),"継ぎ目の向こうに色が届く");window.Document.Undo();Assert.That(DocumentBinary.Write(window.Document),Is.EqualTo(before));
        }
        [Test] public void AllVisibleLayersCloneCanPaintAnEmptyLayerFromTheComposite()
        {
            PrepareEffect(TexturePaintWindow.PaintTool.Clone);var baseLayer=window.SelectedLayer;
            var target=window.Document.AddLayer("clone");window.SelectedLayer=target.Id;window.Brush.cloneAllLayers=true;window.Document.ClearHistory();
            CloneSourceClick(200,300);var expected=window.Document.CompositePixel(PaintChannel.Color,200,300);
            Mouse(window,EventType.MouseDown,At(window,300,300));Mouse(window,EventType.MouseUp,At(window,300,300));
            Assert.That(target.GetPixel(PaintChannel.Color,300,300),Is.EqualTo(expected));Assert.That(window.Document.UndoCount,Is.EqualTo(1));
            Assert.That(window.Document.GetLayer(baseLayer).GetPixel(PaintChannel.Color,200,300),Is.EqualTo(expected));
        }

        [Test] public void SurfaceCloneAlignedKeepsTheRelationAndOffRestartsAtTheSource()
        {
            LoadBrushSamplingSurface();window.SelectTool(TexturePaintWindow.PaintTool.Clone);
            var surface=window.Document.GetLayer(window.SelectedLayer).GetChannel(PaintChannel.Color);
            for(int y=window.Document.Height/8;y<window.Document.Height*7/8;y++)for(int x=window.Document.Width*3/20;x<window.Document.Width*3/8;x++)surface.SetPixel(x,y,new Rgba32(20,70,210));
            SurfaceCloneSourceClick(BrushSurfacePoint(.25f));
            Rgba32 Dot(float x)
            {
                var at=BrushSurfacePoint(x);Mouse(window,EventType.MouseDown,at);Mouse(window,EventType.MouseUp,at);
                Assert.That(window.IsStroking,Is.False,window.StatusMessage);window.Preview.TryPick(window.SurfaceRect,at,out var hit);
                return surface.GetPixel(Mathf.FloorToInt(hit.UV.x*window.Document.Width),Mathf.FloorToInt(hit.UV.y*window.Document.Height));
            }
            Assert.That(Dot(1.25f),Is.EqualTo(new Rgba32(210,30,70)));
            Assert.That(Dot(1.5f),Is.EqualTo(new Rgba32(20,70,210)),"写し元が面に沿って進む");
            window.Brush.cloneAligned=false;Assert.That(Dot(1.75f),Is.EqualTo(new Rgba32(210,30,70)),"写し元から再開する");
        }
        [Test] public void SurfaceCloneUsesTheStencilAtEachDestinationAcrossTheSeam()
        {
            LoadBrushSamplingSurface(); window.SelectTool(TexturePaintWindow.PaintTool.Clone);
            SurfaceCloneSourceClick(BrushSurfacePoint(.25f)); var at = BrushSurfacePoint(1.25f);
            var layer = window.Document.GetLayer(window.SelectedLayer); var before = DocumentBinary.Write(window.Document);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            var plain = window.Document.Composite(PaintChannel.Color); window.Document.Undo();
            window.SetStencil(HalfStencil().Id);
            var view = window.SurfaceRect; window.StencilCenter = new Vector2((at.x-view.x)/view.width, (at.y-view.y)/view.height);
            Repaint(window); var frame = window.StencilFrameIn(window.SurfaceRect).Value;
            Assert.That(window.Preview.TryPick(window.SurfaceRect, at, out var hit), Is.True);
            float radius = window.Preview.Bounds.size.magnitude * window.Brush.radius / window.Document.Width;
            var dab = window.Preview.BuildSurfaceDabs(hit, radius, window.Document.Width, window.Document.Height, 1);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(window.IsStroking, Is.False, window.StatusMessage); Assert.That(window.Document.UndoCount, Is.EqualTo(1), window.StatusMessage);
            int through = 0, held = 0;
            foreach (var p in dab.Pixels)
            {
                Assert.That(window.Preview.TryWorldToGui(window.SurfaceRect, p.Position, out var gui), Is.True);
                frame.ToImage(gui.x, gui.y, out double x, out _); int i = (p.Y*window.Document.Width+p.X)*4;
                if (plain[i+3] == 0) continue;
                var actual = layer.GetPixel(PaintChannel.Color, p.X, p.Y);
                if (x < 30.5) { Assert.That(actual, Is.EqualTo(new Rgba32(plain[i],plain[i+1],plain[i+2],plain[i+3]))); through++; }
                else if (x > 33.5) { Assert.That(actual, Is.EqualTo(Rgba32.Transparent)); held++; }
            }
            Assert.That(through, Is.GreaterThan(20)); Assert.That(held, Is.GreaterThan(20));
            window.Document.Undo(); Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(before));
        }

        [TestCase("Smudge", "mirror")]
        [TestCase("Clone", "mirror")]
        [TestCase("Smudge", "radial")]
        [TestCase("Clone", "radial")]
        [TestCase("Smudge", "both")]
        [TestCase("Clone", "both")]
        public void SurfaceBrushEffectsRefuseSymmetryWithAReasonAndNoPartialStroke(string effectName, string symmetry)
        {
            var effect = (TexturePaintWindow.PaintTool)Enum.Parse(typeof(TexturePaintWindow.PaintTool), effectName);
            LoadBrushSamplingSurface(); window.SelectTool(effect);
            if (effect == TexturePaintWindow.PaintTool.Clone) SurfaceCloneSourceClick(BrushSurfacePoint(.25f));
            window.Symmetry = symmetry != "radial"; window.RadialSymmetry3D = symmetry != "mirror";
            var before = DocumentBinary.Write(window.Document); var at = BrushSurfacePoint(1.25f);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(window.IsStroking, Is.False); Assert.That(window.Document.HasActiveStroke, Is.False);
            Assert.That(window.Document.UndoCount, Is.Zero); Assert.That(DocumentBinary.Write(window.Document), Is.EqualTo(before));
            Assert.That(window.StatusMessage, Does.Contain("separate source and motion"));
        }
    }
}
