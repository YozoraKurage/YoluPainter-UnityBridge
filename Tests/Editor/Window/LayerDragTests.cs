using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>レイヤーの一覧のドラッグでの並べ替え（落とす先の決まり）。一覧は上が手前（Layers の後ろ）で、行と行の間 gap に落とすと
    /// gap 番目の行の層のすぐ上（同じ親の中）、グループの行の中ほどに落とすとその中の一番上。どれも 1 回の Undo。ウィンドウは表示しない。</summary>
    public sealed class LayerDragTests
    {
        TexturePaintWindow window; PaintDocument d;

        [SetUp] public void Create() { window = ScriptableObject.CreateInstance<TexturePaintWindow>(); d = window.Document; }
        [TearDown] public void Clean() { UnityEngine.Object.DestroyImmediate(window); }

        string Order() => string.Join(",", d.Layers.Select(l => (l.ParentId == Guid.Empty ? "" : d.GetLayer(l.ParentId).Name + "/") + l.Name)); // 下から上

        [Test] public void DroppingBetweenRowsMovesTheLayerThere()
        {
            foreach (var l in d.Layers.ToList()) if (d.Layers.Count > 1) d.RemoveLayer(l.Id);
            d.GetLayer(d.Layers[0].Id); var first = d.Layers[0]; d.SetLayerName(first.Id, "A");
            var b = d.AddLayer("B"); var c = d.AddLayer("C"); d.ClearHistory();
            Assert.That(Order(), Is.EqualTo("A,B,C")); // 一覧では C,B,A
            window.DropLayer(first.Id, 0, null); // 一番上の行（C）の上
            Assert.That(Order(), Is.EqualTo("B,C,A"));
            Assert.That(d.UndoCount, Is.EqualTo(1));
            window.DropLayer(first.Id, 3, null); // 一番下
            Assert.That(Order(), Is.EqualTo("A,B,C"));
            window.DropLayer(c.Id, 1, null); // 2 行目（B）の上 = 今の場所
            Assert.That(Order(), Is.EqualTo("A,B,C"), "dropping right above itself changes nothing");
            d.Undo(); d.Undo();
            Assert.That(Order(), Is.EqualTo("A,B,C"));
        }

        [Test] public void DroppingOnAGroupPutsTheLayerOnTopInsideIt()
        {
            var a = d.Layers[0]; d.SetLayerName(a.Id, "A");
            var b = d.AddLayer("B"); var group = d.GroupLayers(new[] { b.Id }, "G"); var c = d.AddLayer("C"); d.ClearHistory();
            Assert.That(Order(), Is.EqualTo("A,G/B,G,C")); // 一覧では C, G, B, A
            window.DropLayer(a.Id, -1, group);
            Assert.That(Order(), Is.EqualTo("G/B,G/A,G,C"));
            // グループの中の行の間に落とすと、そのグループの中に入る（一覧の 3 行目 = B の上）
            window.DropLayer(c.Id, 3, null);
            Assert.That(Order(), Is.EqualTo("G/B,G/C,G/A,G"));
            Assert.That(() => window.DropLayer(group.Id, -1, group), Throws.Nothing, "dropping a group on itself is ignored");
            Assert.That(() => d.MoveLayerTo(group.Id, group.Id, 0), Throws.InvalidOperationException, "the document refuses a group inside itself");
            d.Undo(); d.Undo();
            Assert.That(Order(), Is.EqualTo("A,G/B,G,C"));
        }
    }
}
