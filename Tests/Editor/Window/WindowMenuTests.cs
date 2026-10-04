using System.Collections;
using System.Linq;
using UnityEngine.TestTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed partial class WindowTests
    {
        PaintMenuPopup OpenTestMenu(PaintMenu menu)
        {
            window.Focus(); PaintMenuSession.Open(menu, new Rect(200, 200, 20, 20), window);
            var popup = PaintMenuSession.Current.Windows.Single(); Repaint(popup); return popup;
        }
        [Test] public void MenuKeysSkipDisabledAndSeparatorsAndExecuteAfterClosing()
        {
            bool ran = false, closed = false; var menu = new PaintMenu();
            menu.AddDisabledItem(new GUIContent("Disabled")); menu.AddSeparator(""); menu.AddHeading("Heading");
            menu.AddItem(new GUIContent("Run"), true, () => { ran = true; closed = PaintMenuSession.Current == null && Resources.FindObjectsOfTypeAll<PaintMenuPopup>().All(p => p.Session == null); });
            var popup = OpenTestMenu(menu); Key(popup, KeyCode.DownArrow); Assert.That(popup.Selected, Is.EqualTo(3));
            Key(popup, KeyCode.Return); Assert.That(ran && closed, Is.True);
        }
        [Test] public void MenuSubmenuRightLeftAndEscapeCloseOneLevel()
        {
            var menu = new PaintMenu(); menu.AddItem(new GUIContent("Parent/Child"), false, () => { }); menu.AddItem(new GUIContent("Other"), false, () => { });
            var root = OpenTestMenu(menu); Key(root, KeyCode.DownArrow); Key(root, KeyCode.RightArrow);
            Assert.That(PaintMenuSession.Current.Windows.Count, Is.EqualTo(2));
            var child = PaintMenuSession.Current.Windows.Last(); Assert.That(child.Selected, Is.Zero);
            Key(child, KeyCode.LeftArrow); Assert.That(PaintMenuSession.Current.Windows.Count, Is.EqualTo(1));
            Key(root, KeyCode.RightArrow); child = PaintMenuSession.Current.Windows.Last(); Key(child, KeyCode.Escape);
            Assert.That(PaintMenuSession.Current.Windows.Count, Is.EqualTo(1)); Key(root, KeyCode.Escape); Assert.That(PaintMenuSession.Current, Is.Null);
        }
        [Test] public void MenuMouseExecutesAndDisabledMouseDoesNothing()
        {
            int runs = 0; var menu = new PaintMenu(); menu.AddDisabledItem(new GUIContent("Disabled")); menu.AddItem(new GUIContent("Run"), false, () => runs++);
            var popup = OpenTestMenu(menu); Mouse(popup, EventType.MouseDown, popup.Row(0).center); Mouse(popup, EventType.MouseUp, popup.Row(0).center);
            Assert.That(runs, Is.Zero); Assert.That(PaintMenuSession.Current, Is.Not.Null);
            Mouse(popup, EventType.MouseDown, popup.Row(1).center); Mouse(popup, EventType.MouseUp, popup.Row(1).center); Assert.That(runs, Is.EqualTo(1));
        }
        [Test] public void MenuInitialLettersCycleAndSpaceExecutes()
        {
            int runs = 0; var menu = new PaintMenu(); menu.AddItem(new GUIContent("Alpha"), false, () => { }); menu.AddItem(new GUIContent("Another"), false, () => runs++);
            var popup = OpenTestMenu(menu);
            popup.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.A, character = 'a' }); Assert.That(popup.Selected, Is.Zero);
            popup.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.A, character = 'a' }); Assert.That(popup.Selected, Is.EqualTo(1));
            Key(popup, KeyCode.Space); Assert.That(runs, Is.EqualTo(1));
        }
        [Test] public void MenuBarHoverSwitchesClickTogglesAndArrowMovesHeaders()
        {
            var bar = window.MenuBarForTests;
            var first = bar.ScreenRects[0].center - window.DockScreenOriginForTests;
            Mouse(window, EventType.MouseDown, first); Assert.That(bar.Opened, Is.True);
            var second = bar.ScreenRects[1].center - window.DockScreenOriginForTests;
            Mouse(window, EventType.MouseMove, second); Assert.That(bar.Selected, Is.EqualTo(1));
            var popup = PaintMenuSession.Current.Windows.Single(); Key(popup, KeyCode.RightArrow); Assert.That(bar.Selected, Is.EqualTo(2));
            Mouse(window, EventType.MouseDown, bar.ScreenRects[2].center - window.DockScreenOriginForTests); Assert.That(PaintMenuSession.Current, Is.Null);
        }
        [Test] public void MenuOutsideClickClosesWithoutPaintingAndOpeningCancelsStroke()
        {
            var before = Snapshot(); BeginLine(200, 200);
            window.MenuBarForTests.Open(0); Assert.That(window.IsStroking, Is.False); Assert.That(Snapshot(), Is.EqualTo(before));
            Mouse(window, EventType.MouseDown, window.PixelToGui(300, 300)); Assert.That(PaintMenuSession.Current, Is.Null);
            Assert.That(Snapshot(), Is.EqualTo(before));
        }
        [TestCase(false)] [TestCase(true)] public void MenuProjectDialogOutsideClickOrCloseDismissesPopup(bool closeOwner)
        {
            var dialog = NewProjectWindow.Open(new NewProjectSettings(), false, _ => { });
            try
            {
                Repaint(dialog); var menu = new PaintMenu(); menu.AddItem(new GUIContent("Run"), false, () => { });
                PaintMenuSession.Open(menu, new Rect(dialog.position.x + 40, dialog.position.y + 40, 20, 20), dialog);
                if (closeOwner) dialog.Close(); else Mouse(dialog, EventType.MouseDown, new Vector2(16, 20));
                Assert.That(PaintMenuSession.Current, Is.Null);
            }
            finally { PaintMenuSession.CloseAll(); if (dialog != null) dialog.Close(); }
        }
        [TestCase(false)] [TestCase(true)] public void MenuAndVisibilityChangesReleaseHeldStencilKeys(bool openMenu)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var held = typeof(TexturePaintWindow).GetField("stencilKeyHeld", flags);
            var ignored = typeof(TexturePaintWindow).GetField("stencilIgnoreHeld", flags);
            Key(window, KeyCode.T); Key(window, KeyCode.N);
            Assert.That(held.GetValue(window), Is.True); Assert.That(ignored.GetValue(window), Is.True);
            if (openMenu) window.MenuBarForTests.Open(0); else window.ShowAllModelParts();
            Assert.That(held.GetValue(window), Is.False); Assert.That(ignored.GetValue(window), Is.False);
        }
        [TestCase("BeforeReload")] [TestCase("OnDisable")] public void MenuClosesOnLifecycleNotification(string method)
        {
            OpenTestMenu(window.BuildMenu(0)); Invoke(window, method); Assert.That(PaintMenuSession.Current, Is.Null);
        }
        [Test] public void MenuClosesBeforeEnteringPlay()
        { OpenTestMenu(window.BuildMenu(0)); Invoke(window, "PlayModeChanged", PlayModeStateChange.ExitingEditMode); Assert.That(PaintMenuSession.Current, Is.Null); }
        [Test] public void MenuAltOpensTheBarAndEscapeClosesIt()
        { Key(window, KeyCode.LeftAlt); Assert.That(PaintMenuSession.Current, Is.Null); window.SendEvent(new Event { type = EventType.KeyUp, keyCode = KeyCode.LeftAlt }); Assert.That(PaintMenuSession.Current, Is.Not.Null); Key(PaintMenuSession.Current.Windows.Single(), KeyCode.Escape); Assert.That(PaintMenuSession.Current, Is.Null); }
        [UnityTest] public IEnumerator MenuHoverOpensTheSubmenuAndForeignFocusClosesTheSession()
        {
            window.FloatPanel("layers"); var other = window.PanelWindows.Single();
            var menu = new PaintMenu(); menu.AddItem(new GUIContent("Parent/Child"), false, () => { });
            var root = OpenTestMenu(menu); Mouse(root, EventType.MouseMove, root.Row(0).center);
            yield return new WaitForSecondsRealtime(.3f);
            Assert.That(PaintMenuSession.Current.Windows.Count, Is.EqualTo(2));
            other.Focus(); yield return new WaitForSecondsRealtime(.3f);
            Assert.That(PaintMenuSession.Current, Is.Null);
        }
        [Test] public void MenuAltGestureDoesNotOpenTheBarDuringThreeDimensionalNavigation()
        {
            window.Preview.LoadDemoMesh(); Repaint(window); var at = window.SurfaceRect.center + window.rootVisualElement.worldBound.position;
            Key(window, KeyCode.LeftAlt);
            window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = at, button = 0, modifiers = EventModifiers.Alt });
            window.SendEvent(new Event { type = EventType.MouseDrag, mousePosition = at + new Vector2(12, 4), delta = new Vector2(12, 4), button = 0, modifiers = EventModifiers.Alt });
            window.SendEvent(new Event { type = EventType.MouseUp, mousePosition = at + new Vector2(12, 4), button = 0, modifiers = EventModifiers.Alt });
            window.SendEvent(new Event { type = EventType.KeyUp, keyCode = KeyCode.LeftAlt });
            Assert.That(PaintMenuSession.Current, Is.Null);
        }
        static void AssertPanelBounds(PainterPanelWindow panel, Rect expected)
        {
            Assert.That(panel.position.size, Is.EqualTo(expected.size));
            Assert.That(Mathf.Abs(panel.position.x - expected.x), Is.LessThanOrEqualTo(1));
            // Linux のホストは普通の窓のタブの高さ（21px）だけ上端を補正する。
            Assert.That(Mathf.Abs(panel.position.y - expected.y), Is.LessThanOrEqualTo(22));
        }
        [UnityTest] public IEnumerator PanelWindowModeSwitchKeepsBoundsOwnerTabsAndStoredMode()
        {
            window.FloatPanel("layers"); var first = window.PanelWindows.Single(); var bounds = new Rect(120, 150, 320, 400); first.position = bounds; yield return null; bounds = first.position;
            Assert.That(first.DockableWindow, Is.False); string group = first.GroupId;
            window.SetPanelWindowMode(group, true); var second = window.PanelWindows.Single();
            Assert.That(window.DockLayoutForTests.Group(group).window, Is.EqualTo(bounds)); yield return null;
            Assert.That(first == null, Is.True); Assert.That(second.DockableWindow, Is.True); AssertPanelBounds(second, bounds); Assert.That(second.Owner, Is.EqualTo(window));
            window.SetPanelWindowMode(group, false); var third = window.PanelWindows.Single(); yield return null;
            Assert.That(third.DockableWindow, Is.False); AssertPanelBounds(third, bounds);
            var restored = DockLayout.FromJson(JsonUtility.ToJson(window.DockLayoutForTests)); Assert.That(restored.Group(group).dockableWindow, Is.False);
        }
    }
}
