using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class ScrollProbeWindow : EditorWindow
    {
        internal Vector2 Scroll;
        internal float Content = 1000;
        internal bool Held;
        internal static readonly Rect Viewport = new Rect(10, 10, 120, 100);
        void OnGUI() { if (PaintGui.Scrollbar(Viewport, ref Scroll, Content)) Repaint(); Held = GUIUtility.hotControl != 0; }
    }
    public sealed partial class WindowTests
    {
        ScrollProbeWindow OpenScrollProbe()
        {
            var w = ScriptableObject.CreateInstance<ScrollProbeWindow>(); w.ShowUtility(); w.position = new Rect(200, 200, 160, 140); Repaint(w); return w;
        }
        [Test] public void ScrollbarDraggingClampsAtBothEndsAndReleasesThePen()
        {
            var w = OpenScrollProbe();
            try
            {
                var thumb = PaintGui.ScrollThumb(ScrollProbeWindow.Viewport, w.Content, 0);
                Mouse(w, EventType.MouseDown, thumb.center); Assert.That(w.Held, Is.True);
                Mouse(w, EventType.MouseDrag, thumb.center + new Vector2(0, 1000)); Assert.That(w.Scroll.y, Is.EqualTo(900));
                Mouse(w, EventType.MouseDrag, thumb.center + new Vector2(0, -1000)); Assert.That(w.Scroll.y, Is.Zero);
                Mouse(w, EventType.MouseUp, thumb.center); Assert.That(w.Held, Is.False);
            }
            finally { w.Close(); }
        }
        [Test] public void ScrollbarTrackPagesAndShrinkingContentDropsTheDrag()
        {
            var w = OpenScrollProbe();
            try
            {
                var track = PaintGui.ScrollTrack(ScrollProbeWindow.Viewport);
                Mouse(w, EventType.MouseDown, new Vector2(track.center.x, track.yMax - 5)); Assert.That(w.Scroll.y, Is.EqualTo(100));
                Mouse(w, EventType.MouseUp, track.center);
                var thumb = PaintGui.ScrollThumb(ScrollProbeWindow.Viewport, w.Content, w.Scroll.y);
                Mouse(w, EventType.MouseDown, thumb.center); Assert.That(w.Held, Is.True);
                w.Content = 50; Repaint(w); Assert.That(w.Scroll.y, Is.Zero); Assert.That(w.Held, Is.False);
            }
            finally { w.Close(); }
        }
        [Test] public void MenuCompactViewKeepsBothDropdownsAndExecutesTheLayoutAndSymmetryChoices()
        {
            window.ResetDockLayout(); window.position = new Rect(window.position.position, new Vector2(980, 800));
            window.View = TexturePaintWindow.ViewMode.Split; Repaint(window);
            Assert.That(window.canvasShowButtonForTests.width, Is.GreaterThan(0)); Assert.That(window.modelShowButtonForTests.width, Is.GreaterThan(0));
            Mouse(window, EventType.MouseDown, window.compactViewButtonForTests.center); Mouse(window, EventType.MouseUp, window.compactViewButtonForTests.center);
            var popup = PaintMenuSession.Current.Windows.Single();
            int index = popup.Nodes.FindIndex(n => n.Label == L.Tr("3D View (F2)")); Assert.That(index, Is.GreaterThanOrEqualTo(0));
            Mouse(popup, EventType.MouseDown, popup.Row(index).center); Mouse(popup, EventType.MouseUp, popup.Row(index).center);
            Assert.That(window.View, Is.EqualTo(TexturePaintWindow.ViewMode.Model));
            Key(window, KeyCode.F3); Repaint(window);
            bool before = window.Symmetry;
            Mouse(window, EventType.MouseDown, window.compactShadingButtonForTests.center); Mouse(window, EventType.MouseUp, window.compactShadingButtonForTests.center);
            popup = PaintMenuSession.Current.Windows.Single();
            Assert.That(popup.Nodes.Any(n => n.Label == L.Tr("Scene…")), Is.True);
            index = popup.Nodes.FindIndex(n => n.Label == L.Tr("Symmetry")); Assert.That(index, Is.GreaterThanOrEqualTo(0));
            Mouse(popup, EventType.MouseDown, popup.Row(index).center); Mouse(popup, EventType.MouseUp, popup.Row(index).center);
            Assert.That(window.Symmetry, Is.EqualTo(!before)); Assert.That(PaintMenuSession.Current, Is.Null);
        }
        [Test] public void MenuScrollbarReachesAClippedItemWithoutExecutingOnTheTrack()
        {
            int executed = -1;
            var menu = new PaintMenu();
            for (int i = 0; i < 40; i++) { int item = i; menu.AddItem(new GUIContent("Item " + i), false, () => executed = item); }
            var popup = OpenTestMenu(menu);
            popup.position = new Rect(popup.position.position, new Vector2(popup.position.width, 180)); Repaint(popup);
            var thumb = PaintGui.ScrollThumb(popup.Body, popup.Nodes.Sum(PaintMenuPopup.Height) + PaintMenuPopup.Padding * 2, 0);
            Mouse(popup, EventType.MouseDown, thumb.center);
            Mouse(popup, EventType.MouseDrag, new Vector2(thumb.center.x, popup.Body.yMax + 100));
            Mouse(popup, EventType.MouseUp, new Vector2(thumb.center.x, popup.Body.yMax + 100));
            Assert.That(executed, Is.EqualTo(-1)); Assert.That(PaintMenuSession.Current, Is.Not.Null);
            var row = popup.Row(39); Assert.That(row.yMin, Is.GreaterThanOrEqualTo(popup.Body.yMin));
            Assert.That(row.yMax, Is.LessThanOrEqualTo(popup.Body.yMax));
            Mouse(popup, EventType.MouseDown, row.center); Mouse(popup, EventType.MouseUp, row.center);
            Assert.That(executed, Is.EqualTo(39)); Assert.That(PaintMenuSession.Current, Is.Null);
        }
        [TestCase(0, LayerKind.Raster)] [TestCase(1, LayerKind.Fill)] [TestCase(2, LayerKind.Group)] [TestCase(3, LayerKind.Adjustment)]
        public void BlankLayerMenuCreatesTheChosenKindAndOneUndoRestoresTheList(int row, LayerKind kind)
        {
            int count = window.Document.Layers.Count;
            var list = window.LayerPanelScreenRects["list"];
            var at = new Vector2(list.x + 20, list.yMax - 5) - window.DockScreenOriginForTests;
            Mouse(window, EventType.ContextClick, at);
            Assert.That(PaintMenuSession.Current, Is.Not.Null);
            var popup = PaintMenuSession.Current.Windows.Single();
            for (int i = 0; i <= row; i++) Key(popup, KeyCode.DownArrow);
            if (row == 3) { Key(popup, KeyCode.RightArrow); popup = PaintMenuSession.Current.Windows.Last(); }
            Key(popup, KeyCode.Return);
            Assert.That(PaintMenuSession.Current, Is.Null);
            Assert.That(window.Document.GetLayer(window.SelectedLayer).Kind, Is.EqualTo(kind));
            Assert.That(window.Document.Layers.Count, Is.EqualTo(count + 1));
            window.Document.Undo(); Assert.That(window.Document.Layers.Count, Is.EqualTo(count));
        }
        [Test] public void BlankLayerPasteIsDisabledWithoutAClipboardAndWorksWithOneUndo()
        {
            PainterClipboard.Content = null;
            var entries = window.EmptyLayerMenu().Entries;
            Assert.That(entries.Single(e => e.content.text.StartsWith(L.Tr("Paste"))).enabled, Is.False);
            PaintDot(window, 300, 300); window.RunLayerCommand(TexturePaintWindow.LayerCommand.Copy);
            int count = window.Document.Layers.Count;
            var popup = OpenTestMenu(window.EmptyLayerMenu());
            int index = popup.Nodes.FindIndex(n => n.Label == L.Tr("Paste"));
            // 区切りを飛ばすキーの回数ではなく、実際の行をマウスで押す。
            Mouse(popup, EventType.MouseDown, popup.Row(index).center); Mouse(popup, EventType.MouseUp, popup.Row(index).center);
            Assert.That(window.Document.Layers.Count, Is.EqualTo(count + 1));
            window.Document.Undo(); Assert.That(window.Document.Layers.Count, Is.EqualTo(count));
        }
        [Test] public void LegacyBrushPresetReportsAlphaMigrationAndKeepsItOnProjectRoundTrip()
        {
            var fake = UseFakeDialogs(window); fake.File = NewTempPath(".ylbrush");
            var old = new TexturePaintWindow.BrushState { color = new Color(.2f, .6f, 1, .5f), opacity = .8f };
            File.WriteAllText(fake.File, JsonUtility.ToJson(old)); Invoke(window, "LoadPreset");
            Assert.That(window.Brush.color.a, Is.EqualTo(1)); Assert.That(window.Brush.opacity, Is.EqualTo(.8f * 128 / 255).Within(1e-6));
            Assert.That(window.StatusMessage, Is.EqualTo(window.BrushAlphaMigrationNote()));
            float opacity = window.Brush.opacity;
            fake.File = NewTempPath(".ylp"); window.SaveProject(true);
            var other = Open();
            try
            {
                other.Brush.opacity = 1; other.OpenProjectAt(fake.File);
                Assert.That(other.Brush.opacity, Is.EqualTo(opacity));
                Assert.That(other.StatusMessage, Does.Contain(other.BrushAlphaMigrationNote()));
                var restored = TexturePaintWindow.ReadBrushState(JsonUtility.ToJson(other.Brush));
                Assert.That(restored.opacity, Is.EqualTo(opacity)); Assert.That(restored.colorAlphaMigrated, Is.True);
            }
            finally { Close(other); }
        }
        [Test] public void AlphaNormalizationRunsOnWindowReloadAndReportsIt()
        {
            window.Brush.color = new Color(.2f, .6f, 1, .25f); window.Brush.opacity = .8f;
            Invoke(window, "OnDisable"); Invoke(window, "OnEnable");
            Assert.That(window.Brush.color.a, Is.EqualTo(1)); Assert.That(window.Brush.opacity, Is.EqualTo(.8f * 64 / 255).Within(1e-6));
            Assert.That(window.StatusMessage, Is.EqualTo(window.BrushAlphaMigrationNote()));
        }
        [Test] public void BlankLayerSmartMaterialExecutesAfterClosingAndUndoRestoresTheDocument()
        {
            int count = window.Document.Layers.Count;
            var popup = OpenTestMenu(window.EmptyLayerMenu());
            int index = popup.Nodes.FindIndex(n => n.Label == L.Tr("Apply Smart Material"));
            Mouse(popup, EventType.MouseDown, popup.Row(index).center);
            var group = PaintMenuSession.Current.Windows.Last(); Key(group, KeyCode.DownArrow); Key(group, KeyCode.RightArrow);
            var choices = PaintMenuSession.Current.Windows.Last(); Key(choices, KeyCode.Return);
            Assert.That(PaintMenuSession.Current, Is.Null); Assert.That(window.Document.Layers.Count, Is.GreaterThan(count));
            window.Document.Undo(); Assert.That(window.Document.Layers.Count, Is.EqualTo(count));
        }
        [Test] public void TransparentCorePresetMovesItsAlphaWhileKeepingTheChosenRgb()
        {
            var color = window.Brush.color;
            window.ApplyPreset(new BrushPreset("old", "Old", "Test", new BrushSettings { Color = new Rgba32(1, 2, 3, 128), Opacity = .6 }));
            Assert.That(window.Brush.color, Is.EqualTo(color)); Assert.That(window.Brush.opacity, Is.EqualTo(.6f * 128 / 255).Within(1e-6));
            Assert.That(window.StatusMessage, Is.EqualTo(window.BrushAlphaMigrationNote()));
        }
        [Test] public void ColorPanelSelectsEitherSwatchKeepsItsPositionsAndSwapsWithX()
        {
            var main = window.Brush.color; var sub = window.Brush.secondaryColor;
            var at = window.ColorPanelScreenRects["sub"]; // 前の四角に重ならない右下を押す
            Mouse(window, EventType.MouseDown, new Vector2(at.xMax - 3, at.yMax - 3) - window.DockScreenOriginForTests);
            Assert.That(window.Brush.backgroundColorSelected, Is.True); Assert.That(window.Brush.color, Is.EqualTo(sub));
            window.PickColorAt(new Rect(0, 0, 100, 100), new Vector2(50, 50));
            Assert.That(window.Brush.secondaryColor, Is.EqualTo(main)); Assert.That(window.Brush.color.a, Is.EqualTo(1));
            var picked = window.Brush.color; Key(window, KeyCode.X);
            Assert.That(window.Brush.color, Is.EqualTo(main)); Assert.That(window.Brush.secondaryColor, Is.EqualTo(picked));
            Key(window, KeyCode.D); Assert.That(window.Brush.color, Is.EqualTo(Color.white)); Assert.That(window.Brush.secondaryColor, Is.EqualTo(Color.black));
        }
    }
}
