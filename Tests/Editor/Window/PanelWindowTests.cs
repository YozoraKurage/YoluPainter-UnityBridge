using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>パネルを別のウィンドウ（<see cref="PainterPanelWindow"/>）に出す・戻す・持ち主を探し直す。実際の EditorWindow を開いて
    /// SendEvent で入力を流すので、batchmode ではスキップする（devcontainer では GUI モードの常駐で回す。絵はマゼンタなので見た目は
    /// DockLayoutTests のオフスクリーンの描画で確かめる）。</summary>
    [Category("Window")]
    public sealed class PanelWindowTests
    {
        TexturePaintWindow painter, other;
        /// <summary>テストの間に出たエラーと例外。GUI モードのエディタではシェーダーの壊れでエラーのログを許している
        /// （TolerateErrorLogsIfBroken）ので、シェーダー以外のもの（別のウィンドウの描画の例外・GUI の積み残しなど）はここで拾う。</summary>
        readonly System.Collections.Generic.List<string> errors = new System.Collections.Generic.List<string>();
        void Collect(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            string lower = message.ToLowerInvariant();
            if (lower.Contains("shader") || lower.Contains("hlsl") || lower.Contains("cginc")) return;
            errors.Add(type + ": " + message);
        }

        [SetUp] public void OpenPainter()
        {
            if (Application.isBatchMode) Assert.Ignore("Separate panel windows need a non-batch Editor (test-daemon.sh start in GUI mode).");
            errors.Clear(); Application.logMessageReceived += Collect;
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            string project = Path.Combine(Path.GetTempPath(), "yolupainter-panels-" + System.Guid.NewGuid().ToString("N")); Directory.CreateDirectory(project); PainterSettings.ProjectRoot = project;
            painter = OpenOne();
        }

        [TearDown] public void CloseAll()
        {
            if (Application.isBatchMode) return;
            try { foreach (var w in PainterPanelWindow.All) w.CloseLeavingLayout(); }
            finally
            {
                Close(other); Close(painter); other = painter = null;
                PainterSettings.ProjectRoot = null;
                Application.logMessageReceived -= Collect;
            }
            Assert.That(errors, Is.Empty, "errors logged while the panel windows were used");
        }

        static TexturePaintWindow OpenOne()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            var w = EditorWindow.CreateWindow<TexturePaintWindow>();
            w.position = new Rect(40, 40, 1200, 800);
            Repaint(w);
            return w;
        }

        static void Close(TexturePaintWindow w)
        {
            if (w == null) return;
            string recovery = w.RecoveryRoot; w.Close();
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
        }

        static void Repaint(EditorWindow w) { EditorShaderCompiler.TolerateErrorLogsIfBroken(); w.SendEvent(new Event { type = EventType.Repaint }); }
        /// <summary>SendEvent の座標はタブを含むホスト側の座標なので、ウィンドウの中の座標にタブの分を足して送る（WindowTests と同じ）。</summary>
        static void Mouse(EditorWindow w, EventType type, Vector2 at, int button = 0)
        { EditorShaderCompiler.TolerateErrorLogsIfBroken(); w.SendEvent(new Event { type = type, mousePosition = at + w.rootVisualElement.worldBound.position, button = button, pressure = 1 }); }
        static void Key(EditorWindow w, KeyCode key, EventModifiers modifiers = EventModifiers.None) => w.SendEvent(new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers });

        DockLayout Layout => painter.DockLayoutForTests;
        PainterPanelWindow OnlyPanelWindow() { var all = PainterPanelWindow.All.Where(w => w.Owner == painter).ToList(); Assert.That(all.Count, Is.EqualTo(1)); return all[0]; }

        void DragHeader(string panel, Vector2 to)
        {
            Repaint(painter);
            var from = painter.PanelHeaderRectForTests(panel).center;
            Mouse(painter, EventType.MouseDown, from);
            Mouse(painter, EventType.MouseDrag, from + new Vector2(-20, 4));
            Mouse(painter, EventType.MouseDrag, to);
            Mouse(painter, EventType.MouseUp, to);
        }

        [Test] public void DraggingAHeaderOutOfTheWindowOpensAPanelWindowAndClosingItDocksThePanel()
        {
            DragHeader("layers", new Vector2(-160, 300));
            Assert.That(Layout.IsFloating("layers"), Is.True);
            Assert.That(GUIUtility.hotControl, Is.Zero, "the drag lets the mouse go");
            var panel = OnlyPanelWindow();
            Assert.That(panel.ShownPanels, Is.EqualTo(new[] { "layers" }));
            Assert.That(panel.titleContent.text, Is.EqualTo("Layers"));
            Repaint(panel); // 持ち主のレイヤーのパネルを、このウィンドウで描ける
            Assert.That(Layout.PanelsIn(DockPlace.Right), Is.EqualTo(new[] { "color", "textureSet", "material", "properties" }));
            panel.Close();
            Assert.That(PainterPanelWindow.All, Is.Empty);
            Assert.That(Layout.IsFloating("layers"), Is.False, "closing the window puts the panel back, it is never lost");
            Assert.That(Layout.PanelsIn(DockPlace.Right), Is.EqualTo(new[] { "color", "textureSet", "layers", "material", "properties" }), "back where it was");
        }

        [Test] public void EscapeCancelsAHeaderDragAndAShortDropOnTheCanvasFloats()
        {
            Repaint(painter);
            var from = painter.PanelHeaderRectForTests("properties").center;
            Mouse(painter, EventType.MouseDown, from); Mouse(painter, EventType.MouseDrag, new Vector2(-160, 300));
            Key(painter, KeyCode.Escape);
            Mouse(painter, EventType.MouseUp, new Vector2(-160, 300));
            Assert.That(Layout.IsFloating("properties"), Is.False); Assert.That(PainterPanelWindow.All, Is.Empty); Assert.That(GUIUtility.hotControl, Is.Zero);
            // 表示域（キャンバス）の上に落としても別のウィンドウになる
            DragHeader("properties", painter.SurfaceRect.center);
            Assert.That(Layout.IsFloating("properties"), Is.True); Assert.That(OnlyPanelWindow().ShownPanels, Is.EqualTo(new[] { "properties" }));
        }

        [Test] public void DroppingAHeaderOnAnotherMakesTabsThatSwitchAndSplit()
        {
            Repaint(painter);
            var target = painter.PanelHeaderRectForTests("layers");
            DragHeader("properties", new Vector2(target.xMax - 20, target.center.y + 2));
            var g = Layout.GroupOf("layers");
            Assert.That(g.panels, Is.EqualTo(new[] { "layers", "properties" })); Assert.That(g.Active, Is.EqualTo("properties"));
            Assert.That(PainterPanelWindow.All, Is.Empty);
            // 見えていないタブを押すと切り替わる
            Repaint(painter);
            var head = painter.PanelHeaderRectForTests("layers");
            Mouse(painter, EventType.MouseDown, new Vector2(head.x + 30, head.center.y)); Mouse(painter, EventType.MouseUp, new Vector2(head.x + 30, head.center.y));
            Assert.That(g.Active, Is.EqualTo("layers")); Assert.That(g.collapsed, Is.False);
            // タブを列の上の端へドラッグすると分かれる
            Repaint(painter);
            var tab = new Vector2(head.x + 30, head.center.y);
            var top = painter.PanelHeaderRectForTests("color");
            Mouse(painter, EventType.MouseDown, tab); Mouse(painter, EventType.MouseDrag, tab + new Vector2(0, -20)); Mouse(painter, EventType.MouseDrag, new Vector2(top.center.x, top.y + 2)); Mouse(painter, EventType.MouseUp, new Vector2(top.center.x, top.y + 2));
            Assert.That(Layout.PanelsIn(DockPlace.Right).First(), Is.EqualTo("layers"));
            Assert.That(Layout.GroupOf("layers").panels, Is.EqualTo(new[] { "layers" })); Assert.That(Layout.GroupOf("properties").panels, Is.EqualTo(new[] { "properties" }));
        }

        [Test] public void ATabGroupFloatsFromTheMenuAndItsTabsSwitchInTheWindow()
        {
            Layout.Join("properties", Layout.GroupOf("layers").id);
            painter.FloatGroup(Layout.GroupOf("layers").id);
            var panel = OnlyPanelWindow();
            Assert.That(panel.ShownPanels, Is.EqualTo(new[] { "layers", "properties" }));
            Repaint(panel);
            Assert.That(panel.titleContent.text, Is.EqualTo("Layers · Properties"));
            Assert.That(Layout.GroupOf("layers").Active, Is.EqualTo("properties"));
            Mouse(panel, EventType.MouseDown, new Vector2(14, 12)); Mouse(panel, EventType.MouseUp, new Vector2(14, 12)); // 最初のタブ
            Assert.That(Layout.GroupOf("layers").Active, Is.EqualTo("layers"));
            // 右クリックのメニューと同じ「このパネルを自分のウィンドウに」
            painter.FloatPanel("properties");
            Assert.That(PainterPanelWindow.All.Count(w => w.Owner == painter), Is.EqualTo(2));
            painter.DockPanelGroup(Layout.GroupOf("layers").id);
            Assert.That(PainterPanelWindow.All.Single().ShownPanels, Is.EqualTo(new[] { "properties" }));
        }

        [Test] public void ThePanelWindowEditsTheOwnersDocumentAndIsLockedDuringAStroke()
        {
            painter.FloatPanel("textureSet");
            var panel = OnlyPanelWindow();
            panel.position = new Rect(300, 120, 300, 260);
            Repaint(panel);
            var roughness = ChannelChip(panel.position.width, PaintChannel.Roughness);
            Mouse(panel, EventType.MouseDown, roughness); Mouse(panel, EventType.MouseUp, roughness);
            Assert.That(painter.Channel, Is.EqualTo(PaintChannel.Roughness), "the panel window changes the owner's state");
            painter.Channel = PaintChannel.Color;
            // ストロークの間は別のウィンドウのパネルも触れない
            Repaint(painter);
            var at = painter.PixelToGui(300, 300);
            Mouse(painter, EventType.MouseDown, at); Mouse(painter, EventType.MouseDrag, at + new Vector2(40, 0));
            Assert.That(painter.IsStroking, Is.True);
            Mouse(panel, EventType.MouseDown, roughness); Mouse(panel, EventType.MouseUp, roughness);
            Assert.That(painter.Channel, Is.EqualTo(PaintChannel.Color));
            Mouse(painter, EventType.MouseUp, at + new Vector2(40, 0));
            Assert.That(painter.IsStroking, Is.False);
            // パネルの窓で使わなかったキーは持ち主のショートカットへ回る（Ctrl+Z で今のストロークを戻す）
            Assert.That(painter.Document.CanUndo, Is.True);
            Key(panel, KeyCode.Z, EventModifiers.Control);
            Assert.That(painter.Document.CanUndo, Is.False);
        }

        /// <summary>テクスチャセットのパネルのチャンネルのボタンの中央（DrawTextureSetPanel の並び: 見出し 24、余白 8、モデル 26+4、
        /// マテリアル 26+4、間 4、3 列のボタン 26+4）。</summary>
        /// <summary>テクスチャセットのパネルのチャンネルの欄の中心（見出し 24、上の余白 8、モデルの行 26+6、セットが 1 つの一覧 30+6、
        /// ベイクの行 26+8 の下に 26+4 の行が並ぶ）。</summary>
        static Vector2 ChannelChip(float width, PaintChannel channel)
        {
            int index = (int)channel, row = index / 3, column = index % 3;
            float y = 24 + 8 + 32 + 36 + 34 + row * 30, cell = (width - 16 - 8) / 3;
            return new Vector2(8 + column * (cell + 4) + cell / 2, y + 13);
        }

        [Test] public void ClosingTheOwnerClosesItsPanelWindowsAndTheLayoutRemembersThem()
        {
            painter.FloatPanel("layers");
            var panel = OnlyPanelWindow(); var layout = Layout;
            Close(painter); painter = null;
            Assert.That(panel == null, Is.True, "the panel window closes with its owner");
            Assert.That(layout.IsFloating("layers"), Is.True, "and is opened again next time");
            // 次に開いた持ち主は、配置で別のウィンドウのまとまりにウィンドウを開く
            painter = OpenOne();
            var rect = new Rect(260, 140, 280, 360);
            Layout.FloatPanel("color", rect);
            painter.SyncPanelWindows();
            var reopened = OnlyPanelWindow();
            Assert.That(reopened.ShownPanels, Is.EqualTo(new[] { "color" }));
            Assert.That(reopened.position.size, Is.EqualTo(rect.size));
        }

        [Test] public void APanelWindowFindsItsOwnerAgain()
        {
            painter.FloatPanel("layers");
            var panel = OnlyPanelWindow();
            // 持ち主の名前・まとまり・パネルはシリアライズされ、ウィンドウと一緒に残る（持ち主への参照は、窓が DontSave なので JSON には
            // 残らない。参照を失った写しは名前で持ち主を見つけるが、同じまとまりはもう別のウィンドウが映しているので引き取られない）
            Assert.That(EditorJsonUtility.ToJson(painter), Does.Contain(painter.PanelOwnerKey), "the owner keeps its name");
            var copy = ScriptableObject.CreateInstance<PainterPanelWindow>();
            try
            {
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(panel), copy);
                Assert.That(copy.OwnerKey, Is.EqualTo(painter.PanelOwnerKey)); Assert.That(copy.GroupId, Is.EqualTo(panel.GroupId)); Assert.That(copy.ShownPanels, Is.EqualTo(new[] { "layers" }));
                Assert.That(copy.Owner == null, Is.True);
                Assert.That(copy.Reconnect(), Is.EqualTo(PainterPanelWindow.Link.NothingToShow), "a duplicate never shows the same panels twice");
            }
            finally { copy.DestroyUnshownForTests(); }
            Assert.That(Layout.IsFloating("layers"), Is.True); Assert.That(OnlyPanelWindow(), Is.SameAs(panel));
            panel.ForgetOwnerForTests(false);
            Assert.That(panel.Owner == null, Is.True);
            Assert.That(panel.Reconnect(), Is.EqualTo(PainterPanelWindow.Link.Attached)); Assert.That(panel.Owner, Is.SameAs(painter));
            // 窓が 2 つでも、持ち主の名前で見つける
            other = OpenOne();
            panel.ForgetOwnerForTests(false);
            Assert.That(panel.Reconnect(), Is.EqualTo(PainterPanelWindow.Link.Attached)); Assert.That(panel.Owner, Is.SameAs(painter));
            // 名前も失って窓が 2 つ以上なら決めずに案内を出す（描いても例外にならない）
            panel.ForgetOwnerForTests(true);
            Assert.That(panel.Reconnect(), Is.EqualTo(PainterPanelWindow.Link.Ambiguous)); Assert.That(panel.Owner == null, Is.True);
            Repaint(panel);
            Assert.That(other.AdoptPanelWindow(panel), Is.False, "the other window does not float this panel");
            Assert.That(painter.AdoptPanelWindow(panel), Is.True); Assert.That(panel.Owner, Is.SameAs(painter));
            // 決まりそのもの（エディタに前から開いている窓に左右されないよう、窓の並びを渡して確かめる）: 名前が同じ窓、無ければ 1 つだけの窓
            var why = PainterPanelWindow.Link.Attached;
            Assert.That(PainterPanelWindow.ChooseOwner(new[] { other, painter }, painter.PanelOwnerKey, out why), Is.SameAs(painter));
            Assert.That(PainterPanelWindow.ChooseOwner(new[] { painter }, null, out why), Is.SameAs(painter)); Assert.That(why, Is.EqualTo(PainterPanelWindow.Link.Attached));
            Assert.That(PainterPanelWindow.ChooseOwner(new[] { other, painter }, "gone", out why), Is.Null); Assert.That(why, Is.EqualTo(PainterPanelWindow.Link.Ambiguous));
            Assert.That(PainterPanelWindow.ChooseOwner(new TexturePaintWindow[0], "gone", out why), Is.Null); Assert.That(why, Is.EqualTo(PainterPanelWindow.Link.NoPainter));
            // 持ち主の配置がもう別のウィンドウにしていなければ、映すものが無い
            panel.ForgetOwnerForTests(false); Layout.Dock(Layout.GroupOf("layers").id);
            Assert.That(panel.Reconnect(), Is.EqualTo(PainterPanelWindow.Link.NothingToShow));
        }

        [Test] public void DraggingATabFromThePanelWindowOntoTheDockPutsItBack()
        {
            painter.FloatPanel("layers");
            var panel = OnlyPanelWindow();
            panel.position = new Rect(200, 200, 300, 400);
            Repaint(panel); Repaint(painter);
            var head = painter.PanelHeaderRectForTests("properties");
            var screen = painter.DockScreenOriginForTests + new Vector2(head.xMax - 20, head.center.y + 3); // 見出しの右寄り = 後ろのタブに
            painter.DropFromPanelWindowForTests(panel, null, screen);
            Assert.That(panel == null, Is.True, "the window closes when its group goes back to the dock");
            Assert.That(Layout.GroupOf("properties").panels, Is.EqualTo(new[] { "properties", "layers" }), "dropped on a header, it becomes a tab");
            // 実際のドラッグ: ウィンドウの見出しを押して、持ち主の窓の列の一番下まで動かして離す
            painter.FloatPanel("color");
            panel = OnlyPanelWindow(); panel.position = new Rect(200, 200, 300, 260);
            Repaint(panel); Repaint(painter);
            var column = painter.PanelHeaderRectForTests("textureSet");
            var bottom = painter.DockScreenOriginForTests + new Vector2(column.center.x, 770) - panel.ScreenOriginForTests;
            Mouse(panel, EventType.MouseDown, new Vector2(40, 12)); Mouse(panel, EventType.MouseDrag, new Vector2(40, 40));
            Mouse(panel, EventType.MouseDrag, bottom); Mouse(panel, EventType.MouseUp, bottom);
            Assert.That(Layout.IsFloating("color"), Is.False);
            Assert.That(Layout.PanelsIn(DockPlace.Right).Last(), Is.EqualTo("color"));
            Assert.That(PainterPanelWindow.All, Is.Empty);
        }
    }
}
