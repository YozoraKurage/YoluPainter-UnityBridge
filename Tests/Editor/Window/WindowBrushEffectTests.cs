using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;

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
        [Test] public void CloneNeedsASourceAndRefuses3DWithAReason()
        {
            window.SelectTool(TexturePaintWindow.PaintTool.Clone); Mouse(window, EventType.MouseDown, At(window, 300, 300));
            Assert.That(window.IsStroking, Is.False); Assert.That(window.StatusMessage, Does.Contain("Alt-click"));
            window.View = TexturePaintWindow.ViewMode.Split; window.Preview.LoadDemoMesh(); Repaint(window);
            Mouse(window, EventType.MouseDown, window.SurfaceRect.center); Assert.That(window.IsStroking, Is.False); Assert.That(window.StatusMessage, Does.Contain("2D"));
        }
        [Test] public void BrushEffectParametersSaveAndOpenYlpAndPresetFiles()
        {
            var b = window.Brush; b.blurRadius = 21; b.smudgeStrength = .37f; b.cloneAligned = false;
            var dialogs = UseFakeDialogs(window); dialogs.File = NewTempPath(".json");
            Invoke(window, "SavePreset"); window.Brush = new TexturePaintWindow.BrushState(); Invoke(window, "LoadPreset");
            Assert.That((window.Brush.blurRadius, window.Brush.smudgeStrength, window.Brush.cloneAligned), Is.EqualTo((21, .37f, false)));
            dialogs.File = NewYlpPath(); window.SaveProject(true); Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = dialogs.File; other.OpenProject();
                Assert.That((other.Brush.blurRadius, other.Brush.smudgeStrength, other.Brush.cloneAligned), Is.EqualTo((21, .37f, false)));
                Assert.That(other.HasCloneSource, Is.False, "写し元は保存した文書の画素ではなく一時的な入力状態");
            }
            finally { Close(other); }
        }
        [Test] public void BrushEffectParametersRoundTripInBrushJsonAndPresetJsonWithoutSchemaChange()
        {
            var b = window.Brush; b.blurRadius = 17; b.smudgeStrength = .72f; b.cloneAligned = false;
            var json = JsonUtility.ToJson(b); var read = (TexturePaintWindow.BrushState)typeof(TexturePaintWindow).GetMethod("ReadBrushState", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { json });
            Assert.That((read.schema, read.blurRadius, read.smudgeStrength, read.cloneAligned), Is.EqualTo((3, 17, .72f, false)));
            var old = (TexturePaintWindow.BrushState)typeof(TexturePaintWindow).GetMethod("ReadBrushState", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { "{\"schema\":3,\"radius\":5}" });
            Assert.That((old.blurRadius, old.smudgeStrength, old.cloneAligned), Is.EqualTo((3, .5f, true)));
            window.ApplyPreset(BuiltInBrushes.Presets[1]); Assert.That((window.Brush.blurRadius, window.Brush.smudgeStrength, window.Brush.cloneAligned), Is.EqualTo((17, .72f, false)));
        }
    }
}
