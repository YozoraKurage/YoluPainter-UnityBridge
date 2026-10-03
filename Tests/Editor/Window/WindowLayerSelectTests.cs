using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウのレイヤーの複数選択とロック: パネルの行の Ctrl/Shift クリック（SendEvent）、選んだ層への操作のキー（Ctrl+G・
    /// Ctrl+Shift+G・Ctrl+J・Ctrl+E・Ctrl+,・Ctrl+] / Ctrl+[・Ctrl+Shift+N・Ctrl+Alt+G）とメニュー、ドラッグでまとめて並べ替え、移動ツールで
    /// まとめて動かす、ロックの切り替えのボタンと断ったときの知らせ（チャンネルを有効にするなどの跡を残さない）、文字の欄の入力中は
    /// キーを奪わない、ストローク中は断る、テクスチャセットを切り替えると選択が 1 つに戻る、ロックの保存と読み込み。</summary>
    public sealed partial class WindowTests
    {
        /// <summary>レイヤーのパネルの部品（"row." + ID・"eye." + ID・"lock." + 種類）の中の点（ホストの座標）。行は一覧の見える所にあることを
        /// 確かめる（一覧の外の行には入力が届かない）。</summary>
        Vector2 LayerPanelPoint(string id, float fx = .5f, float fy = .5f)
        {
            Repaint(window);
            Assert.That(window.LayerPanelScreenRects.TryGetValue(id, out var screen), Is.True, id + " was not drawn");
            var host = new Rect(screen.position - HostScreenPosition(window), screen.size);
            var point = new Vector2(host.x + host.width * fx, host.y + host.height * fy);
            if (id.StartsWith("row.", StringComparison.Ordinal))
            {
                var list = window.LayerPanelScreenRects["list"];
                Assert.That(list.Contains(point + HostScreenPosition(window)), Is.True, id + " is outside the visible list " + list + ": make the layers panel taller");
            }
            return point;
        }
        void SendHost(EventType type, Vector2 host, EventModifiers modifiers = EventModifiers.None)
        { EditorShaderCompiler.TolerateErrorLogsIfBroken(); window.SendEvent(new Event { type = type, mousePosition = host, button = 0, modifiers = modifiers, pressure = 1 }); }
        /// <summary>行の名前の辺り（目やサムネイルを避けて右寄り）を押して離す。</summary>
        void ClickRow(PaintLayer layer, EventModifiers modifiers = EventModifiers.None)
        {
            var p = LayerPanelPoint("row." + layer.Id, .7f);
            SendHost(EventType.MouseDown, p, modifiers); SendHost(EventType.MouseUp, p, modifiers); Repaint(window);
        }
        string[] Chosen() => window.SelectedLayers.Select(id => window.Document.GetLayer(id).Name).ToArray();
        static readonly EventModifiers Shift = EventModifiers.Shift;

        /// <summary>a, b, c, e（下から）の 4 枚。b・c・e は少し塗る。</summary>
        (PaintLayer a, PaintLayer b, PaintLayer c, PaintLayer e) FourLayers()
        {
            var d = window.Document; var a = d.Layers.Last(); d.SetLayerName(a.Id, "a");
            var b = d.AddLayer("b"); var c = d.AddLayer("c"); var e = d.AddLayer("e");
            d.Fill(b.Id, PaintChannel.Color, new Rgba32(200, 0, 0, 255), 1, SelectionMask.Rectangle(d, 100, 100, 140, 140));
            d.Fill(c.Id, PaintChannel.Color, new Rgba32(0, 200, 0, 255), 1, SelectionMask.Rectangle(d, 120, 120, 160, 160));
            d.Fill(e.Id, PaintChannel.Color, new Rgba32(0, 0, 200, 255), 1, SelectionMask.Rectangle(d, 10, 10, 20, 20));
            d.ClearHistory(); window.SelectedLayer = e.Id;
            // 4〜5 行が見えるように、右のドックのほかのまとまりを畳む（既定の 1200×800 ではレイヤーのパネルに 2 行ほどしか見えない）。
            // 配置はこの窓のもの（保存しない。設定の置き場は一時フォルダ）
            foreach (var g in window.DockLayoutForTests.Column(DockPlace.Right)) if (!g.panels.Contains("layers")) g.collapsed = true;
            Repaint(window);
            return (a, b, c, e);
        }

        [Test] public void CtrlAndShiftClicksSelectSeveralLayersAndAPlainClickOne()
        {
            var (a, b, c, e) = FourLayers();
            ClickRow(c);
            Assert.That(Chosen(), Is.EqualTo(new[] { "c" }));
            ClickRow(a, Shift);
            Assert.That(Chosen(), Is.EqualTo(new[] { "a", "b", "c" }), "a range of rows, bottom to top");
            Assert.That(window.SelectedLayer, Is.EqualTo(a.Id), "the clicked layer is the one painted on");
            ClickRow(e, Ctrl);
            Assert.That(Chosen(), Is.EqualTo(new[] { "a", "b", "c", "e" })); Assert.That(window.SelectedLayer, Is.EqualTo(e.Id));
            ClickRow(b, Ctrl);
            Assert.That(Chosen(), Is.EqualTo(new[] { "a", "c", "e" }));
            ClickRow(e, Ctrl);
            Assert.That(Chosen(), Is.EqualTo(new[] { "a", "c" })); Assert.That(window.SelectedLayer, Is.EqualTo(c.Id), "taking out the painted one makes the topmost left the painted one");
            ClickRow(c, Ctrl); ClickRow(a, Ctrl);
            Assert.That(Chosen(), Is.EqualTo(new[] { "a" }), "the last one stays");
            ClickRow(e, Shift); ClickRow(b, Ctrl | Shift);
            Assert.That(Chosen(), Is.EqualTo(new[] { "a", "b", "c", "e" }), "Ctrl+Shift adds a range");
            ClickRow(b);
            Assert.That(Chosen(), Is.EqualTo(new[] { "b" }), "a plain click (without a drag) selects only that layer");
            Assert.That(window.Document.UndoCount, Is.Zero, "selecting changes nothing in the document");
        }

        [Test] public void LayerKeysActOnTheSelectionAsOneUndoStepEach()
        {
            var d = window.Document; var (a, b, c, e) = FourLayers();
            ClickRow(b); ClickRow(c, Ctrl);
            Key(window, KeyCode.G, Ctrl);
            var group = d.GetLayer(window.SelectedLayer);
            Assert.That(group.IsGroup, Is.True, window.StatusMessage);
            Assert.That(d.ChildrenOf(group.Id).Select(l => l.Name), Is.EqualTo(new[] { "b", "c" }));
            Assert.That(d.UndoCount, Is.EqualTo(1));
            Key(window, KeyCode.G, Ctrl | EventModifiers.Shift);
            Assert.That(d.Layers.Any(l => l.IsGroup), Is.False, "Ctrl+Shift+G ungroups");
            Key(window, KeyCode.Z, Ctrl); Key(window, KeyCode.Z, Ctrl);
            Assert.That(d.Layers.Select(l => l.Name), Is.EqualTo(new[] { "a", "b", "c", "e" }));

            ClickRow(b); ClickRow(c, Ctrl);
            Key(window, KeyCode.J, Ctrl);
            Assert.That(d.Layers.Select(l => l.Name), Is.EqualTo(new[] { "a", "b", "b copy", "c", "c copy", "e" }), window.StatusMessage);
            Assert.That(Chosen(), Is.EqualTo(new[] { "b copy", "c copy" }), "the copies are selected");
            Assert.That(d.UndoCount, Is.EqualTo(1));
            Key(window, KeyCode.Z, Ctrl);

            ClickRow(b); ClickRow(c, Ctrl);
            Key(window, KeyCode.Comma, Ctrl);
            Assert.That((b.Visible, c.Visible, e.Visible), Is.EqualTo((false, false, true)), window.StatusMessage);
            Key(window, KeyCode.Comma, Ctrl);
            Assert.That((b.Visible, c.Visible), Is.EqualTo((true, true)));

            Key(window, KeyCode.RightBracket, Ctrl);
            Assert.That(d.Layers.Select(l => l.Name), Is.EqualTo(new[] { "a", "e", "b", "c" }), "both step up; c is at the top and b follows");
            Key(window, KeyCode.LeftBracket, Ctrl);
            Assert.That(d.Layers.Select(l => l.Name), Is.EqualTo(new[] { "a", "b", "c", "e" }));
            int undo = d.UndoCount;

            var before = d.Composite(PaintChannel.Color);
            Key(window, KeyCode.E, Ctrl);
            Assert.That(d.Layers.Select(l => l.Name), Is.EqualTo(new[] { "a", "c", "e" }), window.StatusMessage);
            Assert.That(d.Composite(PaintChannel.Color), Is.EqualTo(before), "two opaque layers merge exactly");
            Assert.That(d.UndoCount, Is.EqualTo(undo + 1));
            Key(window, KeyCode.Z, Ctrl);

            window.SelectedLayer = a.Id;
            Key(window, KeyCode.N, Ctrl | EventModifiers.Shift);
            Assert.That(d.Layers.Count, Is.EqualTo(5)); Assert.That(d.Layers[1].Id, Is.EqualTo(window.SelectedLayer), "a new layer above the selected one");
            Key(window, KeyCode.G, Ctrl | EventModifiers.Alt);
            Assert.That(d.GetLayer(window.SelectedLayer).Clipping, Is.True, "Ctrl+Alt+G clips to the layer below");

            // 削除はメニューから（選んだ全部、1 回の Undo）
            ClickRow(b); ClickRow(c, Ctrl); undo = d.UndoCount;
            var menu = new GenericMenu(); Invoke(window, "LayerMenu", menu);
            Assert.That(LayerMenuTexts(menu), Does.Contain("Delete Layers"));
            Assert.That(LayerMenuTexts(menu), Does.Contain("Merge Layers    Ctrl+E"));
            Assert.That(LayerMenuTexts(menu), Does.Contain("Group Layers    Ctrl+G"));
            Run(menu, "Delete Layers");
            Assert.That(d.Layers.Any(l => l.Name == "b" || l.Name == "c"), Is.False);
            Assert.That(d.UndoCount, Is.EqualTo(undo + 1));
        }

        [Test] public void NewLayerKeysAreLeftToATextFieldAndRefusedDuringAStroke()
        {
            var d = window.Document; var (a, b, c, e) = FourLayers();
            ClickRow(b); ClickRow(c, Ctrl);
            // 一番上の行（e）の名前をダブルクリックして名前の欄にし、押して文字の入力にする
            var name = LayerPanelPoint("row." + e.Id, .7f);
            SendHost(EventType.MouseDown, name); SendHost(EventType.MouseUp, name);
            SendHost(EventType.MouseDown, name); SendHost(EventType.MouseUp, name);
            Repaint(window); SendHost(EventType.MouseDown, name); SendHost(EventType.MouseUp, name);
            int count = d.Layers.Count, undo = d.UndoCount;
            foreach (var (key, mods) in new[] { (KeyCode.G, Ctrl), (KeyCode.N, Ctrl | EventModifiers.Shift), (KeyCode.RightBracket, Ctrl), (KeyCode.Comma, Ctrl), (KeyCode.G, Ctrl | EventModifiers.Alt) })
                Key(window, key, mods);
            Assert.That((d.Layers.Count, d.UndoCount), Is.EqualTo((count, undo)), "the text field keeps its keys");
            Key(window, KeyCode.Escape); Repaint(window);

            BeginLine(300, 300);
            Key(window, KeyCode.G, Ctrl); Key(window, KeyCode.N, Ctrl | EventModifiers.Shift);
            Assert.That(d.Layers.Count, Is.EqualTo(count), "not during a stroke");
            Assert.That(window.StatusMessage, Does.Contain("Finish the stroke first"));
            Assert.That(window.IsStroking, Is.True);
            Mouse(window, EventType.MouseUp, At(window, 360, 300));
            Key(window, KeyCode.N, Ctrl | EventModifiers.Shift);
            Assert.That(d.Layers.Count, Is.EqualTo(count + 1));
        }

        [Test] public void DraggingASelectedRowMovesTheWholeSelection()
        {
            var d = window.Document; var (a, b, c, e) = FourLayers();
            ClickRow(a); ClickRow(c, Ctrl);
            var from = LayerPanelPoint("row." + c.Id, .7f); var to = LayerPanelPoint("row." + e.Id, .7f, .15f); // e の行の上の端 = 一番上の線
            SendHost(EventType.MouseDown, from); SendHost(EventType.MouseDrag, from + new Vector2(0, -8)); SendHost(EventType.MouseDrag, to);
            Repaint(window); SendHost(EventType.MouseUp, to); Repaint(window);
            Assert.That(d.Layers.Select(l => l.Name), Is.EqualTo(new[] { "b", "e", "a", "c" }), window.StatusMessage);
            Assert.That(d.UndoCount, Is.EqualTo(1));
            Assert.That(Chosen(), Is.EqualTo(new[] { "a", "c" }), "the selection stays after a drag");
        }

        [Test] public void TheMoveToolMovesEverySelectedLayerTogether()
        {
            var d = window.Document; var (a, b, c, e) = FourLayers();
            ClickRow(b); ClickRow(e, Ctrl);
            window.Tool = TexturePaintWindow.PaintTool.Move;
            Mouse(window, EventType.MouseDown, At(window, 105, 105)); Mouse(window, EventType.MouseDrag, At(window, 125, 105)); Mouse(window, EventType.MouseUp, At(window, 135, 105));
            Assert.That(b.GetPixel(PaintChannel.Color, 135, 105), Is.EqualTo(new Rgba32(200, 0, 0, 255)), window.StatusMessage);
            Assert.That(e.GetPixel(PaintChannel.Color, 45, 15), Is.EqualTo(new Rgba32(0, 0, 200, 255)), "the other selected layer moved by the same amount");
            Assert.That(c.GetPixel(PaintChannel.Color, 125, 125), Is.EqualTo(new Rgba32(0, 200, 0, 255)), "an unselected layer stays");
            Assert.That(d.UndoCount, Is.EqualTo(1));
            Assert.That(e.GetPixel(PaintChannel.Color, 50, 15).A, Is.Zero);
            Key(window, KeyCode.RightArrow);
            Assert.That(e.GetPixel(PaintChannel.Color, 50, 15), Is.EqualTo(new Rgba32(0, 0, 200, 255)), "arrow keys move all of them too");
            Assert.That(b.GetPixel(PaintChannel.Color, 170, 105), Is.EqualTo(new Rgba32(200, 0, 0, 255)));
        }

        [Test] public void LockButtonsLockTheSelectionAndLockedEditsAreRefusedWithoutTraces()
        {
            var d = window.Document; var (a, b, c, e) = FourLayers();
            ClickRow(b); ClickRow(c, Ctrl);
            var pixels = LayerPanelPoint("lock." + LayerLocks.Pixels);
            SendHost(EventType.MouseDown, pixels); SendHost(EventType.MouseUp, pixels); Repaint(window);
            Assert.That((b.Locks, c.Locks, e.Locks), Is.EqualTo((LayerLocks.Pixels, LayerLocks.Pixels, LayerLocks.None)), window.StatusMessage);
            Assert.That(d.UndoCount, Is.EqualTo(1));
            // 描けない: 知らせて、チャンネルを有効にした跡も残さない
            ClickRow(c); window.Channel = PaintChannel.Roughness;
            BeginLineExpectingRefusal(130, 130);
            Assert.That(window.StatusMessage, Does.Contain("image pixels locked"));
            Assert.That(c.IsChannelEnabled(PaintChannel.Roughness), Is.False); Assert.That(d.UndoCount, Is.EqualTo(1));
            window.Channel = PaintChannel.Color;
            window.Tool = TexturePaintWindow.PaintTool.Fill; Mouse(window, EventType.MouseDown, At(window, 130, 130)); Mouse(window, EventType.MouseUp, At(window, 130, 130));
            Assert.That(window.StatusMessage, Does.Contain("image pixels locked")); Assert.That(d.UndoCount, Is.EqualTo(1));
            window.Tool = TexturePaintWindow.PaintTool.Brush;
            // 切り替えで外す（選んでいる層だけ）
            ClickRow(b); ClickRow(c, Ctrl);
            pixels = LayerPanelPoint("lock." + LayerLocks.Pixels); SendHost(EventType.MouseDown, pixels); SendHost(EventType.MouseUp, pixels);
            Assert.That((b.Locks, c.Locks), Is.EqualTo((LayerLocks.None, LayerLocks.None)));
            // 透明部分のロック: 塗ってもアルファは変わらない
            ClickRow(c);
            var transparency = LayerPanelPoint("lock." + LayerLocks.Transparency); SendHost(EventType.MouseDown, transparency); SendHost(EventType.MouseUp, transparency);
            Assert.That(c.Locks, Is.EqualTo(LayerLocks.Transparency));
            var b2 = window.Brush; b2.color = Color.white; b2.radius = 30; b2.hardness = 1; b2.opacity = 1; b2.flow = 1; window.Brush = b2;
            Mouse(window, EventType.MouseDown, At(window, 160, 160)); Mouse(window, EventType.MouseUp, At(window, 160, 160));
            Assert.That(c.GetPixel(PaintChannel.Color, 158, 158), Is.EqualTo(new Rgba32(255, 255, 255, 255)), window.StatusMessage);
            Assert.That(c.GetPixel(PaintChannel.Color, 170, 170).A, Is.Zero, "outside the layer's pixels nothing was painted");
            // すべてのロック: 設定も変えない（Ctrl+Alt+G のクリッピングを断って知らせる）
            var all = LayerPanelPoint("lock." + LayerLocks.All); SendHost(EventType.MouseDown, all); SendHost(EventType.MouseUp, all);
            Key(window, KeyCode.G, Ctrl | EventModifiers.Alt);
            Assert.That(c.Clipping, Is.False); Assert.That(window.StatusMessage, Does.Contain("is locked"));
        }

        [Test] public void SwitchingTextureSetsLeavesOneLayerSelectedAndLocksSaveAndOpen()
        {
            var d = window.Document; var (a, b, c, e) = FourLayers();
            ClickRow(b); ClickRow(c, Ctrl);
            d.SetLayerLocks(b.Id, LayerLocks.Transparency | LayerLocks.Position);
            var first = window.CurrentTextureSet;
            window.AddTextureSet(-1); // モデルが無い: マテリアルに結び付けないセット
            Assert.That(window.SelectedLayers.Count, Is.EqualTo(1));
            window.SwitchTextureSet(first.Id);
            Assert.That(window.SelectedLayers, Is.EqualTo(new[] { c.Id }), "only the painted layer, not the old selection");
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            window.SaveProject(true);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File; other.OpenProject();
                Assert.That(other.Document.Layers.Single(l => l.Name == "b").Locks, Is.EqualTo(LayerLocks.Transparency | LayerLocks.Position), other.StatusMessage);
            }
            finally { Close(other); }
        }
    }
}
