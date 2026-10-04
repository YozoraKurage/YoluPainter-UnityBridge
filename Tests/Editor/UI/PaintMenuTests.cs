using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class PaintMenuTests
    {
        [Test] public void EntriesKeepOrderChecksSeparatorsDisabledItemsAndUserData()
        {
            var menu = new PaintMenu(); object result = null;
            menu.AddItem(new GUIContent("Open    Ctrl+O"), true, () => { }); menu.AddSeparator("");
            menu.AddDisabledItem(new GUIContent("Unavailable"), true);
            menu.AddItem(new GUIContent("Options/Run"), false, data => result = data, 7);
            menu.AddSeparator("Options/"); menu.AddRadioItem(new GUIContent("Options/Radio"), true, () => { }); menu.AddHeading("Options/Heading");
            var nodes = menu.Build();
            Assert.That(menu.GetItemCount(), Is.EqualTo(7));
            Assert.That(nodes.Select(n => n.Label), Is.EqualTo(new[] { "Open", "", "Unavailable", "Options" }));
            Assert.That(nodes[0].Shortcut, Is.EqualTo("Ctrl+O")); Assert.That(nodes[0].Item.on, Is.True);
            Assert.That(nodes[1].Separator, Is.True); Assert.That(nodes[2].Enabled, Is.False); Assert.That(nodes[2].Item.on, Is.True);
            Assert.That(nodes[3].Children.Select(n => n.Label), Is.EqualTo(new[] { "Run", "", "Radio", "Heading" }));
            Assert.That(nodes[3].Children[2].Item.radio, Is.True); Assert.That(nodes[3].Children[3].Heading, Is.True);
            nodes[3].Children[0].Item.Run(); Assert.That(result, Is.EqualTo(7));
        }
        [Test] public void AlternativeShortcutsDoNotCreateAnotherSubmenu()
        {
            var menu = new PaintMenu(); menu.AddItem(new GUIContent("Edit/Redo    Ctrl+Shift+Z / Ctrl+Y"), false, () => { });
            var node = menu.Build().Single().Children.Single();
            Assert.That(node.Label, Is.EqualTo("Redo")); Assert.That(node.Shortcut, Is.EqualTo("Ctrl+Shift+Z / Ctrl+Y")); Assert.That(node.Children, Is.Empty);
        }
        [Test] public void DisabledSubmenuDoesNotBecomeSelectable()
        {
            var menu = new PaintMenu(); menu.AddDisabledItem(new GUIContent("Parent/Nested/Disabled")); menu.AddSeparator("Parent/");
            Assert.That(menu.Build().Single().Enabled, Is.False);
        }
        [TestCase(false)] [TestCase(true)] public void PlacementFitsTheMonitorAtNegativeCoordinatesAndFlipsAtEdges(bool child)
        {
            var desktop = new Rect(-1920, -100, 1920, 1080); var anchor = new Rect(-30, 940, 25, 24);
            var result = PaintMenuPopup.Place(anchor, new Vector2(350, 300), desktop, child);
            Assert.That(result.xMin, Is.GreaterThanOrEqualTo(desktop.xMin)); Assert.That(result.xMax, Is.LessThanOrEqualTo(desktop.xMax));
            Assert.That(result.yMin, Is.GreaterThanOrEqualTo(desktop.yMin)); Assert.That(result.yMax, Is.LessThanOrEqualTo(desktop.yMax));
            if (child) Assert.That(result.xMax, Is.LessThanOrEqualTo(anchor.x + PaintMenuPopup.Margin));
        }
        [Test] public void OversizedPopupIsClampedForScrolling()
        {
            var rect = PaintMenuPopup.Place(new Rect(300, 180, 10, 20), new Vector2(5000, 5000), new Rect(0, 0, 320, 200), false);
            Assert.That(rect, Is.EqualTo(new Rect(0, 0, 320, 200)));
        }
        [TestCase(1)] [TestCase(2)] public void MenuDrawsOffscreenInBothLanguages(int language)
        {
            string folder = Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "menus")); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "menu-" + language + ".png");
            try
            {
                L.OverrideLanguage((PainterLanguage)language);
                var menu = new PaintMenu(); menu.AddHeading(L.Tr("File"));
                menu.AddItem(new GUIContent(L.Tr("Save") + "\tCtrl+S"), true, () => { }); menu.AddSeparator("");
                menu.AddDisabledItem(new GUIContent(L.Tr("Undo"))); menu.AddRadioItem(new GUIContent(L.Tr("3D View")), true, () => { });
                menu.AddItem(new GUIContent(L.Tr("Export") + "/" + L.Tr("Channel as PNG…")), false, () => { });
                var nodes = menu.Build(); var size = PaintMenuPopup.Measure(nodes);
                OffscreenGui.RenderToPng((int)size.x, (int)size.y, () => PaintMenuPopup.Draw(new Rect(Vector2.zero, size), nodes, 1), path, PaintTheme.WindowBg);
                Assert.That(new FileInfo(path).Length, Is.GreaterThan(500));
            }
            finally { L.OverrideLanguage(PainterLanguage.English); }
        }
        [Test] public void NewFloatingPanelsUseUtilityWhileOldFloatingRecordsRemainDockable()
        {
            var layout = DockLayout.Default(); var group = layout.FloatPanel("layers", new Rect(50, 60, 300, 400));
            Assert.That(group.dockableWindow, Is.False);
            group.dockableWindow = true; var restored = DockLayout.FromJson(JsonUtility.ToJson(layout));
            Assert.That(restored.GroupOf("layers").dockableWindow, Is.True); Assert.That(restored.GroupOf("layers").window, Is.EqualTo(group.window));
            group.dockableWindow = false; layout.version = 1;
            Assert.That(DockLayout.FromJson(JsonUtility.ToJson(layout)).GroupOf("layers").dockableWindow, Is.True);
        }
    }
}
