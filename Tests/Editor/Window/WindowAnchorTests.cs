using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウの Anchor: レイヤーのメニューと同じ入口で描いた層に Anchor を置き（レイヤーの重なりの子の行に出る）、上の塗りつぶしのマスクに
    /// Generator「Anchor」を足すと、すぐ下の Anchor を読んで表示が変わる。キーボードで層を上へ動かすと参照が使えなくなり、知らせに理由が
    /// 出て入力のまま通る（表示も）、Ctrl+Z で戻る。マスクの Anchor（マスクのタブのボタン、本物のマウスの入力）、名前の変更と Undo、外すボタン
    /// （Anchor の行を選んだときのプロパティ）、.ylp の保存と開き直し（Anchor と参照が残る）。</summary>
    public sealed partial class WindowTests
    {
        [Test] public void AnAnchorFromThePropertiesDrivesAMaskAboveAndMovingItShowsWhy()
        {
            var d = window.Document; var paint = d.Layers[0];
            window.SelectedLayer = paint.Id;
            d.SetChannelEnabled(paint.Id, PaintChannel.Height, true);
            d.Fill(paint.Id, PaintChannel.Height, new Rgba32(255, 255, 255, 255), 1, SelectionMask.Ellipse(d, 300, 300, 160, 110));
            int steps = d.UndoCount;
            OpenLayerPanels();
            // ペイントの層を選んでいるとプロパティにはブラシが出るので、Anchor はレイヤーのメニュー（右クリック）の「Anchor を追加」と同じ入口で置く
            Assert.That(window.AddAnchorTo(AnchorPlacement.Layer), Is.Not.Null, window.StatusMessage);
            Assert.That(paint.Anchor, Is.Not.Null, window.StatusMessage); Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
            LayerPanelPoint("anchor." + paint.Anchor.Id); // 置いた Anchor はレイヤーの重なりの層の下の子の行に出る（描かれていなければここで落ちる）
            Assert.That(window.StatusMessage, Does.Contain("Put the anchor"));
            Assert.That(paint.Anchor.Name, Is.EqualTo(paint.Name));
            var plain = d.Composite(PaintChannel.Color);

            var fill = WornFill();
            var gen = window.AddGenerator(FilterTarget.Mask, GeneratorType.Anchor);
            Assert.That(gen, Is.Not.Null, window.StatusMessage);
            Assert.That(gen.Settings.Generator.AnchorId, Is.EqualTo(paint.Anchor.Id), "it reads the anchor right below");
            Assert.That(d.GetGeneratorStatus(fill.Id, gen.Id).Active, Is.True);
            var worn = d.Composite(PaintChannel.Color);
            Assert.That(worn, Is.Not.EqualTo(plain));
            AssertShows(window, worn, "the mask reads the painted Height");

            // 描いた層を上へ（Ctrl+]）: 読む層より上になり、入力のまま通して理由を知らせる
            window.SelectedLayer = paint.Id; window.EditMask = false;
            Key(window, KeyCode.RightBracket, EventModifiers.Control);
            Assert.That(d.Layers.Last().Id, Is.EqualTo(paint.Id), window.StatusMessage);
            Assert.That(d.AnchorIssues().Single().Kind, Is.EqualTo(AnchorIssueKind.NotBelow));
            Assert.That(window.StatusMessage, Does.Contain("now pass their input through").And.Contain("not below"));
            AssertShows(window, d.Composite(PaintChannel.Color), "passes through");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(d.AnchorIssues(), Is.Empty);
            AssertShows(window, worn, "undo brings the reference back");

            // マスクの Anchor（マスクに描くときのプロパティの「マスク」のタブのボタン）と、名前の変更・Undo
            window.SelectedLayer = paint.Id; d.AddLayerMask(paint.Id);
            window.EditMask = true; window.SetPropertyTab(PropertyContext.Brush, TexturePaintWindow.TabMask); OpenLayerPanels();
            ClickLayerControl("mask.anchor.add");
            Assert.That(paint.Mask.Anchor, Is.Not.Null, window.StatusMessage);
            window.RenameAnchor(paint.Anchor, "Height details");
            Assert.That(paint.Anchor.Name, Is.EqualTo("Height details"));
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(paint.Anchor.Name, Is.EqualTo(paint.Name));
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);

            // 保存して開き直す: Anchor（ID・名前・置き場所）と読む段の参照が残り、同じ結果
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File; other.OpenProject();
                var od = other.Document;
                Assert.That(od.Anchors.Select(a => (a.Anchor.Id, a.Anchor.Name, a.Anchor.Placement)), Is.EqualTo(d.Anchors.Select(a => (a.Anchor.Id, a.Anchor.Name, a.Anchor.Placement))), other.StatusMessage);
                Assert.That(od.GetLayer(fill.Id).Mask.Filters.Single().Settings, Is.EqualTo(fill.Mask.Filters.Single().Settings));
                Assert.That(od.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            }
            finally { Close(other); }

            // 外すボタン（レイヤーの重なりの Anchor の行を選んだときのプロパティ）: 読む段は入力のまま通り、理由を知らせる
            window.SelectedLayer = paint.Id; window.EditMask = false; OpenLayerPanels();
            var anchorRow = LayerPanelPoint("anchor." + paint.Anchor.Id, .5f); SendHost(EventType.MouseDown, anchorRow); SendHost(EventType.MouseUp, anchorRow); Repaint(window);
            Assert.That(window.LastPropertyContext, Is.EqualTo(PropertyContext.Anchor), "the row shows the anchor in Properties");
            ClickLayerControl("anchor.remove");
            Assert.That(paint.Anchor, Is.Null);
            Assert.That(d.AnchorIssues().Single().Kind, Is.EqualTo(AnchorIssueKind.Missing));
            Assert.That(window.StatusMessage, Does.Contain("Removed the anchor").And.Contain("now pass their input through"));
            AssertShows(window, d.Composite(PaintChannel.Color), "the anchor removed: passes through");
        }
    }
}
