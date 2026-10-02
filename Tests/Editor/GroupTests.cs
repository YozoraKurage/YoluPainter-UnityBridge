using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>レイヤーのグループ（フォルダ）: 入れ子の構造と Undo、通過/分離の合成、クリッピングとの関係、変更追跡、保存往復。</summary>
    public sealed class GroupTests
    {
        static PaintLayer Solid(PaintDocument d, string name, Rgba32 color, int x0 = 0, int y0 = 0, int x1 = -1, int y1 = -1)
        {
            var layer = d.AddLayer(name); var s = layer.GetChannel(PaintChannel.Color);
            if (x1 < 0) x1 = d.Width; if (y1 < 0) y1 = d.Height;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) s.SetPixel(x, y, color);
            return layer;
        }
        static string Shape(PaintDocument d) => string.Join(" ", d.Layers.Select(l => l.Name + (l.ParentId == Guid.Empty ? "" : "<" + d.GetLayer(l.ParentId).Name)));
        static void AssertTileMatchesPixelReference(PaintDocument d)
        {
            var tiles = d.Composite(PaintChannel.Color);
            for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++)
            {
                var p = CpuCompositor.CompositePixel(d, PaintChannel.Color, x, y); int i = (y * d.Width + x) * 4;
                Assert.That(new[] { tiles[i], tiles[i + 1], tiles[i + 2], tiles[i + 3] }, Is.EqualTo(new[] { p.R, p.G, p.B, p.A }), "pixel " + x + "," + y);
            }
        }

        [Test] public void GroupsKeepTheirContentsDirectlyBelowThemAndEveryEditIsOneUndoStep()
        {
            var d = new PaintDocument(16, 16, 8);
            var a = d.AddLayer("A"); var b = d.AddLayer("B"); var c = d.AddLayer("C"); var g = d.AddGroup("G");
            d.ClearHistory();
            Assert.That(g.BlendMode, Is.EqualTo(LayerBlendMode.PassThrough), "groups pass through by default");
            d.MoveLayerTo(a.Id, g.Id, 0);
            Assert.That(Shape(d), Is.EqualTo("B C A<G G"));
            d.MoveLayerTo(c.Id, g.Id, 1);
            Assert.That(Shape(d), Is.EqualTo("B A<G C<G G"));
            Assert.That(d.ChildrenOf(g.Id).Select(l => l.Name), Is.EqualTo(new[] { "A", "C" }));
            Assert.That(d.DepthOf(c.Id), Is.EqualTo(1));
            d.MoveLayer(c.Id, 0); // グループの中で一番下へ
            Assert.That(Shape(d), Is.EqualTo("B C<G A<G G"));
            d.MoveLayer(g.Id, 0); // グループを中身ごと一番下へ
            Assert.That(Shape(d), Is.EqualTo("C<G A<G G B"));
            Assert.That(() => d.MoveLayerTo(g.Id, g.Id, 0), Throws.InvalidOperationException, "a group cannot go into itself");
            d.ValidateStructure();
            int steps = d.UndoCount; Assert.That(steps, Is.EqualTo(4));
            for (int i = 0; i < steps; i++) d.Undo();
            Assert.That(Shape(d), Is.EqualTo("A B C G"));
            for (int i = 0; i < steps; i++) d.Redo();
            Assert.That(Shape(d), Is.EqualTo("C<G A<G G B"));
        }

        [Test] public void NewLayersCanBePlacedDirectlyAboveAnotherLayerInItsGroup()
        {
            var d = new PaintDocument(16, 16, 8);
            var a = d.AddLayer("A"); var b = d.AddLayer("B"); var g = d.GroupLayers(new[] { a.Id, b.Id }, "G"); var top = d.AddLayer("Top");
            d.ClearHistory();
            var c = d.AddLayer("C", above: a.Id);
            Assert.That(Shape(d), Is.EqualTo("A<G C<G B<G G Top"), "inside the group, right above A");
            var f = d.AddFillLayer("F", above: g.Id);
            Assert.That(Shape(d), Is.EqualTo("A<G C<G B<G G F Top"), "above a group = above its contents, as its sibling");
            var adj = d.AddAdjustmentLayer("Adj", AdjustmentSettings.Invert(), above: b.Id);
            var inner = d.AddGroup("Inner", above: adj.Id);
            Assert.That(Shape(d), Is.EqualTo("A<G C<G B<G Adj<G Inner<G G F Top"));
            d.ValidateStructure();
            Assert.That(d.UndoCount, Is.EqualTo(4), "each add is one undo step");
            for (int i = 0; i < 4; i++) d.Undo();
            Assert.That(Shape(d), Is.EqualTo("A<G B<G G Top"));
            Assert.That(() => d.AddLayer("X", above: Guid.NewGuid()), Throws.TypeOf<KeyNotFoundException>());
        }

        [Test] public void GroupUngroupAndDeleteWithContents()
        {
            var d = new PaintDocument(16, 16, 8);
            var a = d.AddLayer("A"); var b = d.AddLayer("B"); var c = d.AddLayer("C"); var top = d.AddLayer("Top");
            d.ClearHistory();
            var g = d.GroupLayers(new[] { c.Id, a.Id }, "G");
            Assert.That(Shape(d), Is.EqualTo("B A<G C<G G Top"), "members keep their order; the group takes the topmost member's place");
            var outer = d.GroupLayers(new[] { g.Id, b.Id }, "Outer");
            Assert.That(Shape(d), Is.EqualTo("B<Outer A<G C<G G<Outer Outer Top"));
            Assert.That(d.DepthOf(a.Id), Is.EqualTo(2));
            Assert.That(() => d.GroupLayers(new[] { a.Id, top.Id }), Throws.InvalidOperationException, "only siblings");
            d.Ungroup(outer.Id);
            Assert.That(Shape(d), Is.EqualTo("B A<G C<G G Top"));
            d.RemoveLayer(g.Id);
            Assert.That(Shape(d), Is.EqualTo("B Top"), "deleting a group deletes its contents");
            d.Undo();
            Assert.That(Shape(d), Is.EqualTo("B A<G C<G G Top"), "and one undo brings all of it back");
            d.Undo(); d.Undo(); d.Undo();
            Assert.That(Shape(d), Is.EqualTo("A B C Top"));
            Assert.That(() => d.Ungroup(a.Id), Throws.InvalidOperationException);
        }

        [Test] public void APassThroughGroupCompositesLikeNoGroupAndItsOpacityFadesIt()
        {
            var flat = new PaintDocument(8, 8, 8); Solid(flat, "Base", new Rgba32(200, 100, 50)); var m1 = Solid(flat, "Mul", new Rgba32(128, 255, 64, 200), 2, 2, 7, 7);
            flat.SetLayerBlendMode(m1.Id, LayerBlendMode.Multiply);
            var grouped = new PaintDocument(8, 8, 8); Solid(grouped, "Base", new Rgba32(200, 100, 50)); var m2 = Solid(grouped, "Mul", new Rgba32(128, 255, 64, 200), 2, 2, 7, 7);
            grouped.SetLayerBlendMode(m2.Id, LayerBlendMode.Multiply);
            var g = grouped.GroupLayers(new[] { m2.Id }, "G");
            Assert.That(grouped.Composite(PaintChannel.Color), Is.EqualTo(flat.Composite(PaintChannel.Color)), "pass through = as if not grouped");
            grouped.SetLayerOpacity(g.Id, .5);
            var below = new Rgba32(200, 100, 50); var full = flat.CompositePixel(PaintChannel.Color, 4, 4);
            var faded = grouped.CompositePixel(PaintChannel.Color, 4, 4);
            Assert.That(faded.R, Is.EqualTo((byte)Math.Round((below.R + full.R) / 2.0)).Within(1));
            Assert.That(grouped.CompositePixel(PaintChannel.Color, 0, 0), Is.EqualTo(below), "outside the child nothing changes");
            AssertTileMatchesPixelReference(grouped);
        }

        [Test] public void AnIsolatedGroupBlendsItsContentsOnlyWithEachOther()
        {
            var d = new PaintDocument(8, 8, 8); Solid(d, "Base", new Rgba32(200, 100, 50));
            var mul = Solid(d, "Mul", new Rgba32(128, 255, 64));
            d.SetLayerBlendMode(mul.Id, LayerBlendMode.Multiply);
            var g = d.GroupLayers(new[] { mul.Id }, "G");
            var passThrough = d.CompositePixel(PaintChannel.Color, 1, 1);
            Assert.That(passThrough, Is.EqualTo(new Rgba32(100, 100, 13)), "pass through: the child multiplies the base");
            d.SetLayerBlendMode(g.Id, LayerBlendMode.Normal);
            Assert.That(d.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(128, 255, 64)), "isolated: multiply over the group's transparent start is the child itself");
            d.SetLayerBlendMode(g.Id, LayerBlendMode.Multiply);
            Assert.That(d.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(passThrough), "the group's own mode then blends the result");
            AssertTileMatchesPixelReference(d);
        }

        [Test] public void AdjustmentsInsideAGroupReachOutOnlyWhenItPassesThrough()
        {
            var d = new PaintDocument(8, 8, 8); Solid(d, "Base", new Rgba32(200, 100, 50));
            var inside = Solid(d, "Inside", new Rgba32(10, 20, 30), 0, 0, 4, 8);
            var invert = d.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert());
            var g = d.GroupLayers(new[] { inside.Id, invert.Id }, "G");
            Assert.That(d.CompositePixel(PaintChannel.Color, 6, 1), Is.EqualTo(new Rgba32(55, 155, 205)), "pass through: the adjustment also changes the base");
            Assert.That(d.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(245, 235, 225)));
            d.SetLayerBlendMode(g.Id, LayerBlendMode.Normal);
            Assert.That(d.CompositePixel(PaintChannel.Color, 6, 1), Is.EqualTo(new Rgba32(200, 100, 50)), "isolated: only the group's own contents are adjusted");
            Assert.That(d.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(245, 235, 225)));
            AssertTileMatchesPixelReference(d);
        }

        [Test] public void ClippingStaysInsideTheGroupAndGroupsCanClipAndBeClipped()
        {
            var d = new PaintDocument(8, 8, 8); var below = Solid(d, "Below", new Rgba32(0, 0, 255));
            var first = Solid(d, "First", new Rgba32(255, 0, 0), 0, 0, 4, 8);
            var g = d.GroupLayers(new[] { first.Id }, "G");
            d.SetLayerClipping(first.Id, true);
            Assert.That(d.IsEffectivelyClipped(d.Layers.ToList().IndexOf(first)), Is.False, "the bottom layer of a group has nothing to clip to, even with a layer below the group");
            Assert.That(d.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(255, 0, 0)));
            d.SetLayerClipping(first.Id, false);
            // グループを下地にする: グループの合成の内側だけに描く
            var clip = Solid(d, "Clip", new Rgba32(0, 255, 0));
            d.SetLayerClipping(clip.Id, true);
            Assert.That(d.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(0, 255, 0)), "inside the group's pixels");
            Assert.That(d.CompositePixel(PaintChannel.Color, 6, 1), Is.EqualTo(new Rgba32(0, 0, 255)), "outside them the clipped layer does not show");
            AssertTileMatchesPixelReference(d);
            // グループ自身を下のレイヤーにクリッピングする
            d.RemoveLayer(clip.Id);
            var holder = new PaintDocument(8, 8, 8); Solid(holder, "Base", new Rgba32(0, 0, 255), 0, 0, 8, 4);
            var inner = Solid(holder, "Inner", new Rgba32(255, 255, 0));
            var clippedGroup = holder.GroupLayers(new[] { inner.Id }, "Clipped group");
            holder.SetLayerClipping(clippedGroup.Id, true);
            Assert.That(holder.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(255, 255, 0)));
            Assert.That(holder.CompositePixel(PaintChannel.Color, 1, 6).A, Is.EqualTo(0), "a clipped group shows only inside the base");
            AssertTileMatchesPixelReference(holder);
        }

        [Test] public void HiddenGroupsAndGroupMasksHideEverythingInside()
        {
            var d = new PaintDocument(8, 8, 8); Solid(d, "Base", new Rgba32(0, 0, 255));
            var child = Solid(d, "Child", new Rgba32(255, 0, 0));
            var g = d.GroupLayers(new[] { child.Id }, "G");
            d.SetLayerVisibility(g.Id, false);
            Assert.That(d.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(0, 0, 255)));
            d.SetLayerVisibility(g.Id, true);
            var mask = d.AddLayerMask(g.Id); mask.Surface.SetPixel(1, 1, new Rgba32(0, 0, 0, 255));
            Assert.That(d.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(0, 0, 255)), "the group's mask hides its contents");
            Assert.That(d.CompositePixel(PaintChannel.Color, 2, 2), Is.EqualTo(new Rgba32(255, 0, 0)));
            Assert.That(() => g.GetChannel(PaintChannel.Color), Throws.InvalidOperationException, "groups have no pixels");
            Assert.That(() => d.BeginStroke(g.Id, PaintChannel.Color, new BrushSettings()), Throws.InvalidOperationException);
            Assert.That(() => d.SetLayerBlendMode(child.Id, LayerBlendMode.PassThrough), Throws.ArgumentException, "pass through is for groups only");
            AssertTileMatchesPixelReference(d);
        }

        [Test] public void ChangingAGroupInvalidatesTheTilesOfItsContents()
        {
            var d = new PaintDocument(32, 32, 8); Solid(d, "Base", new Rgba32(0, 0, 255), 0, 0, 8, 8);
            var child = Solid(d, "Child", new Rgba32(255, 0, 0), 16, 16, 24, 24);
            var g = d.GroupLayers(new[] { child.Id }, "G");
            long serial = d.ChangeSerial; var changed = new HashSet<TileCoord>();
            d.SetLayerOpacity(g.Id, .5);
            Assert.That(d.TryGetChangedTiles(PaintChannel.Color, serial, changed), Is.True);
            Assert.That(changed, Has.Member(new TileCoord(2, 2)), "the child's tile");
            Assert.That(changed, Has.No.Member(new TileCoord(0, 0)), "the base's tile is not affected");
            // 差分更新の結果が全面の再合成と一致する
            using (var compositor = new Yozolab.YoluPainter.Editor.TileGpuCompositor(allowGpu: false))
            {
                compositor.Update(d, PaintChannel.Color);
                foreach (Action edit in new Action[] { () => d.SetLayerVisibility(g.Id, false), () => d.SetLayerVisibility(g.Id, true), () => d.SetLayerBlendMode(g.Id, LayerBlendMode.Screen), () => d.Ungroup(g.Id), () => d.Undo() })
                {
                    edit(); compositor.Update(d, PaintChannel.Color);
                    Assert.That(GpuTests.ReadCpu(compositor.Texture), Is.EqualTo(d.Composite(PaintChannel.Color)));
                }
            }
        }

        [Test] public void NestedGroupsSurviveSavingAndBrokenNestingIsRefused()
        {
            var d = new PaintDocument(16, 16, 8); Solid(d, "Base", new Rgba32(30, 60, 90));
            var a = Solid(d, "A", new Rgba32(200, 10, 10, 180), 2, 2, 12, 12); var b = Solid(d, "B", new Rgba32(10, 200, 10, 200), 6, 6, 16, 16);
            var inner = d.GroupLayers(new[] { a.Id }, "Inner"); d.SetLayerBlendMode(inner.Id, LayerBlendMode.Overlay);
            var outer = d.GroupLayers(new[] { inner.Id, b.Id }, "Outer"); d.SetLayerOpacity(outer.Id, .7);
            d.AddLayerMask(outer.Id).Surface.SetPixel(8, 8, new Rgba32(0, 0, 0, 128));
            var bytes = DocumentBinary.Write(d);
            var back = DocumentBinary.Read(bytes);
            Assert.That(Shape(back), Is.EqualTo(Shape(d)));
            Assert.That(back.GetLayer(inner.Id).BlendMode, Is.EqualTo(LayerBlendMode.Overlay));
            Assert.That(back.GetLayer(outer.Id).BlendMode, Is.EqualTo(LayerBlendMode.PassThrough));
            Assert.That(back.Composite(PaintChannel.Color), Is.EqualTo(d.Composite(PaintChannel.Color)));
            Assert.That(DocumentBinary.Write(back), Is.EqualTo(bytes), "byte-identical round trip");
            AssertTileMatchesPixelReference(back);
            // 子の親 ID（最初に現れる Inner の ID は A のレコードの親の欄）を存在しないものに書き換える
            var tampered = (byte[])bytes.Clone(); var idBytes = inner.Id.ToByteArray();
            int at = IndexOf(tampered, idBytes); Assert.That(at, Is.GreaterThan(0));
            Array.Copy(Guid.NewGuid().ToByteArray(), 0, tampered, at, 16);
            Assert.That(() => DocumentBinary.Read(tampered), Throws.TypeOf<InvalidDataException>().With.Message.Contains("group"));
        }

        static int IndexOf(byte[] data, byte[] key)
        {
            for (int i = 0; i + key.Length <= data.Length; i++) { int k = 0; while (k < key.Length && data[i + k] == key[k]) k++; if (k == key.Length) return i; }
            return -1;
        }
    }
}
