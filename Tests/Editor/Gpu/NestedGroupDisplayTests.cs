using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>表示の合成（TileGpuCompositor）の入れ子のグループの写し: グループの中の層を変えたとき、グループの中の「最初に変わった項目より下」の
    /// 写しから合成し直す。CPU の経路は表示が CPU の正本（PaintDocument.Composite）と、GPU の経路は写しを持たない新しい合成器の結果と、
    /// どの手順の後もバイト単位で同じ。ランダムな入れ子の文書とランダムな編集（ドラッグのように同じ層を続けて変える、合成モード・表示・
    /// クリッピング・マスク・塗りつぶし・調整・ストローク・グループへの出し入れ・並べ替え・グループ化と解除・Undo/Redo・チャンネル・文書の
    /// 切り替え・予算）で確かめ、写しが実際に使われること、予算の内側にあること、予算を下げる・捨てると正しく戻ることも確かめる。</summary>
    public sealed class NestedGroupDisplayTests
    {
        const string ShaderName = "Hidden/YoluPainter/TileComposite";

        static PaintLayer Pick(System.Random rnd, PaintDocument d, Func<PaintLayer, bool> filter = null)
        {
            var list = d.Layers.Where(l => filter == null || filter(l)).ToList();
            return list.Count == 0 ? null : list[rnd.Next(list.Count)];
        }
        static readonly LayerBlendMode[] Modes = { LayerBlendMode.Normal, LayerBlendMode.Multiply, LayerBlendMode.Screen, LayerBlendMode.Overlay, LayerBlendMode.Difference, LayerBlendMode.Hue, LayerBlendMode.LinearBurn };

        /// <summary>ランダムな編集を 1 つ。ドラッグ（同じ層・同じ種類を続けて）が多めになるよう、focus の層を何回か続けて変える。</summary>
        static string Edit(System.Random rnd, PaintDocument d, ref PaintLayer focus, ref int focusLeft, ref PaintChannel channel)
        {
            if (focus != null && focusLeft > 0 && d.Layers.Contains(focus))
            {
                focusLeft--;
                if (focus.IsGroup && rnd.Next(3) == 0 && focus.Mask != null) { d.SetLayerMaskDensity(focus.Id, rnd.NextDouble(), coalesce: true); return "drag mask density of " + focus.Name; }
                d.SetLayerOpacity(focus.Id, .05 + .95 * rnd.NextDouble(), coalesce: true); return "drag opacity of " + focus.Name;
            }
            var l = Pick(rnd, d);
            if (l == null) return "nothing";
            var brush = new BrushSettings { Radius = 6 + rnd.Next(60), Hardness = .5, Opacity = .9, Color = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)(80 + rnd.Next(176))), PressureSize = false, PressureOpacity = false };
            try
            {
                switch (rnd.Next(17))
                {
                    case 0: case 1: case 2: focus = l; focusLeft = 1 + rnd.Next(3); d.SetLayerOpacity(l.Id, .05 + .95 * rnd.NextDouble(), coalesce: true); return "opacity of " + l.Name;
                    case 3: d.SetLayerBlendMode(l.Id, l.IsGroup && rnd.Next(2) == 0 ? LayerBlendMode.PassThrough : Modes[rnd.Next(Modes.Length)]); return "blend mode of " + l.Name;
                    case 4: d.SetLayerVisibility(l.Id, !l.Visible); return "visibility of " + l.Name;
                    case 5: case 6:
                        var painted = channel;
                        var r = Pick(rnd, d, x => x.Kind == LayerKind.Raster && x.IsChannelEnabled(painted));
                        if (r == null) return "no raster";
                        using (var s = d.BeginStroke(r.Id, painted, brush))
                        {
                            double x = rnd.Next(d.Width), y = rnd.Next(d.Height);
                            for (int k = 0; k < 3; k++) s.Add(new BrushSample(x += rnd.Next(-90, 91), y += rnd.Next(-60, 61)));
                            if (rnd.Next(6) == 0) s.Cancel(); else s.Commit();
                        }
                        return "stroke on " + r.Name;
                    case 7: d.SetLayerClipping(l.Id, !l.Clipping); return "clipping of " + l.Name;
                    case 8:
                        if (l.Mask == null) { d.AddLayerMask(l.Id); return "mask on " + l.Name; }
                        if (rnd.Next(2) == 0) d.SetLayerMaskInverted(l.Id, !l.Mask.Inverted); else { focus = l; focusLeft = 2; d.SetLayerMaskDensity(l.Id, rnd.NextDouble(), coalesce: true); }
                        return "mask of " + l.Name;
                    case 9:
                        var groups = d.Layers.Where(x => x.IsGroup).ToList();
                        var parent = groups.Count == 0 || rnd.Next(3) == 0 ? Guid.Empty : groups[rnd.Next(groups.Count)].Id;
                        d.MoveLayerTo(l.Id, parent, rnd.Next(d.ChildrenOf(parent).Count(c => c.Id != l.Id) + 1));
                        return "move " + l.Name + " into " + (parent == Guid.Empty ? "the top level" : d.GetLayer(parent).Name);
                    case 10: return d.CanUndo && d.Undo() ? "undo" : "nothing to undo";
                    case 11: return d.CanRedo && d.Redo() ? "redo" : "nothing to redo";
                    case 12: channel = NestedGroupCopyTests.Channels[rnd.Next(NestedGroupCopyTests.Channels.Length)]; return "channel " + channel;
                    case 13: d.SetChannelOpacity(l.Id, channel, rnd.Next(3) == 0 ? (double?)null : rnd.NextDouble(), coalesce: true); return "channel opacity of " + l.Name;
                    case 14:
                        if (l.IsGroup && rnd.Next(2) == 0) { d.Ungroup(l.Id); return "ungroup " + l.Name; }
                        var siblings = d.ChildrenOf(l.ParentId).Where(x => rnd.Next(2) == 0 || x == l).Select(x => x.Id).ToList();
                        d.GroupLayers(siblings, "new group"); return "group around " + l.Name;
                    case 15:
                        if (l.Kind == LayerKind.Fill) { d.SetFillValue(l.Id, PaintChannel.Color, new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256))); return "fill of " + l.Name; }
                        if (l.Kind == LayerKind.Adjustment) { d.SetAdjustment(l.Id, AdjustmentSettings.Levels(.1 * rnd.NextDouble(), .9, .6 + rnd.NextDouble(), 0, 1)); return "adjustment of " + l.Name; }
                        d.MoveLayer(l.Id, rnd.Next(d.ChildrenOf(l.ParentId).Count)); return "reorder " + l.Name;
                    default: d.SetChannelBlendMode(l.Id, PaintChannel.Color, rnd.Next(2) == 0 ? (LayerBlendMode?)null : l.IsGroup && rnd.Next(2) == 0 ? LayerBlendMode.PassThrough : Modes[rnd.Next(Modes.Length)]); return "channel blend of " + l.Name;
                }
            }
            catch (InvalidOperationException e) { return "refused: " + e.Message; }
            catch (ArgumentException e) { return "refused: " + e.Message; }
        }

        static byte[] Read(TileGpuCompositor c) => c.Texture is RenderTexture ? GpuTests.Read(c.Texture) : GpuTests.ReadCpu(c.Texture);

        /// <summary>CPU の経路（CPU 側の Texture2D、デバイスがあれば表示の RenderTexture も）。表示はいつも CPU の正本と同じ。</summary>
        [Test] public void TheCpuDisplayMatchesTheReferenceThroughRandomEditsInNestedGroups()
        {
            var rnd = new System.Random(3141);
            long[] budgets = { 0, 1L << 20, 3L << 20, 64L << 20 };
            bool device = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            int reuse = 0, captures = 0, steps = 0;
            for (int c = 0; c < 6; c++)
            {
                var d = NestedGroupCopyTests.NestedDocument(rnd, 600 + rnd.Next(200), 300 + rnd.Next(300), 32);
                var channel = PaintChannel.Color;
                var compositors = new List<TileGpuCompositor> { new TileGpuCompositor(allowGpu: false) };
                if (device) compositors.Add(new TileGpuCompositor(CompositorBackend.Cpu));
                // 時間で区切る表示（1 回に 1 ブロック）: 編集のあいだに予定が残り、何回かに分けて合成する
                var sliced = device ? new TileGpuCompositor(CompositorBackend.Cpu) : null;
                if (sliced != null) compositors.Add(sliced);
                try
                {
                    foreach (var x in compositors) x.ResidentBudgetBytes = budgets[c % budgets.Length];
                    PaintLayer focus = null; int focusLeft = 0;
                    for (int i = 0; i < 70; i++)
                    {
                        string step = i == 0 ? "first" : Edit(rnd, d, ref focus, ref focusLeft, ref channel);
                        if (i == 45) { foreach (var x in compositors) x.ResidentBudgetBytes = 2L << 20; step += " (budget lowered)"; }
                        if (i == 55) { foreach (var x in compositors) x.ReleaseResidentCaches(); step += " (released)"; }
                        var expected = d.Composite(channel);
                        foreach (var x in compositors)
                        {
                            if (x == sliced)
                            {
                                bool done = x.Update(d, channel, new CompositeSchedule { BudgetMilliseconds = 0 });
                                if (i % 4 != 3) continue;
                                while (!done) done = x.Update(d, channel, new CompositeSchedule { BudgetMilliseconds = 0 });
                            }
                            else x.Update(d, channel);
                            CpuCompositingTests.AssertSameBytes(expected, Read(x), "document " + c + " step " + i + " " + step + " (" + x.Path + (x == sliced ? ", sliced" : "") + ")");
                            Assert.That(x.ResidentBytes, Is.LessThanOrEqualTo(x.ResidentBudgetBytes), step);
                            reuse += x.LastNestedReuseCount; captures += x.LastNestedCaptureCount;
                        }
                        steps++;
                    }
                    // 同じ大きさの別の文書: 写しは使わず、全部を合成し直す
                    var other = NestedGroupCopyTests.NestedDocument(rnd, d.Width, d.Height, 32);
                    foreach (var x in compositors)
                    {
                        x.Update(other, channel);
                        Assert.That(x.LastNestedReuseCount, Is.Zero, "another document");
                        CpuCompositingTests.AssertSameBytes(other.Composite(channel), Read(x), "document " + c + ": another document (" + x.Path + ")");
                    }
                }
                finally { foreach (var x in compositors) x.Dispose(); }
            }
            Assert.That(captures, Is.GreaterThan(20), "copies were taken inside groups");
            Assert.That(reuse, Is.GreaterThan(20), "groups started from their copies");
        }

        /// <summary>4 層の下に置いた 10 層を、(a) グループの中、(b) 2 段の入れ子の中に、通過・分離・マスクとフェードのあるグループで。</summary>
        static PaintDocument TenLayers(string kind, out PaintLayer middle, out PaintLayer below, out List<PaintLayer> groups, int w = 1024, int h = 512, int tile = 64)
        {
            var d = new PaintDocument(w, h, tile);
            var rnd = new System.Random(kind.GetHashCode() & 0xffff);
            var bytes = new byte[tile * tile * 4];
            PaintLayer Painted(string name, bool opaque)
            {
                var l = d.AddLayer(name);
                for (int ty = 0; ty < h / tile; ty++) for (int tx = 0; tx < w / tile; tx++)
                {
                    rnd.NextBytes(bytes); if (opaque) for (int k = 3; k < bytes.Length; k += 4) bytes[k] = 255;
                    l.GetChannel(PaintChannel.Color).ImportTile(new TileCoord(tx, ty), bytes);
                }
                return l;
            }
            below = Painted("below", true);
            var ids = new List<Guid>(); middle = null;
            for (int i = 0; i < 10; i++)
            {
                var l = Painted("L" + i, false);
                d.SetLayerBlendMode(l.Id, new[] { LayerBlendMode.Normal, LayerBlendMode.Multiply, LayerBlendMode.Screen, LayerBlendMode.Overlay }[i % 4]);
                d.SetLayerOpacity(l.Id, .55 + .04 * i);
                ids.Add(l.Id); if (i == 4) middle = l;
            }
            groups = new List<PaintLayer>();
            var g = d.GroupLayers(ids, "G1"); groups.Add(g);
            if (kind.Contains("2")) { g = d.GroupLayers(new[] { g.Id }, "G2"); groups.Insert(0, g); }
            foreach (var group in groups)
            {
                if (kind.Contains("isolated")) d.SetLayerBlendMode(group.Id, LayerBlendMode.Normal);
                if (kind.Contains("masked")) { var m = d.AddLayerMask(group.Id); for (int x = 0; x < w; x += 3) m.Surface.SetPixel(x, (x * 7) % h, new Rgba32(0, 0, 0, 200)); d.SetLayerOpacity(group.Id, .9); }
            }
            d.AddLayer("empty top");
            d.ClearHistory();
            return d;
        }

        static TileGpuCompositor Compositor(string backend)
        {
            if (backend == "Gpu") { GpuTests.RequireWorkingShader(ShaderName); return new TileGpuCompositor() { ResidentBudgetBytes = 256L << 20 }; }
            return new TileGpuCompositor(allowGpu: false) { ResidentBudgetBytes = 256L << 20 };
        }
        static byte[] Expected(string backend, PaintDocument d, PaintChannel channel = PaintChannel.Color)
        {
            if (backend != "Gpu") return d.Composite(channel);
            using (var fresh = new TileGpuCompositor()) { fresh.Update(d, channel); Assert.That(fresh.LastNestedReuseCount + fresh.LastBelowReuseCount, Is.Zero); return GpuTests.Read(fresh.Texture); }
        }

        /// <summary>グループの中の層の不透明度のドラッグ: 1 回目にグループの中の写しを取り、2 回目からはそこから（CPU は変わったタイルだけを）合成する。
        /// グループ自身の不透明度のドラッグは中身の全部の写しから。分離のグループの写しは、グループの下の層を変えても使える（通過は使えない）。</summary>
        [TestCase("Cpu", "pass-through"), TestCase("Cpu", "isolated"), TestCase("Cpu", "masked"), TestCase("Cpu", "2 pass-through"), TestCase("Cpu", "2 isolated"), TestCase("Cpu", "2 masked isolated")]
        [TestCase("Gpu", "pass-through"), TestCase("Gpu", "isolated"), TestCase("Gpu", "masked"), TestCase("Gpu", "2 pass-through"), TestCase("Gpu", "2 isolated"), TestCase("Gpu", "2 masked isolated")]
        public void DraggingALayerInsideGroupsStartsFromTheCopyInsideTheGroup(string backend, string kind)
        {
            var d = TenLayers(kind, out var middle, out var below, out var groups);
            using (var c = Compositor(backend))
            {
                void Check(string step)
                {
                    c.Update(d, PaintChannel.Color);
                    CpuCompositingTests.AssertSameBytes(Expected(backend, d), Read(c), kind + ": " + step);
                    Assert.That(c.ResidentBytes, Is.LessThanOrEqualTo(c.ResidentBudgetBytes));
                }
                Check("first");
                int blocks = 2;
                d.SetLayerOpacity(middle.Id, .3, coalesce: true); Check("middle 1");
                Assert.That(c.LastNestedCaptureCount, Is.EqualTo(blocks), "the first change takes the copy inside the innermost group");
                foreach (var v in new[] { .35, .4 })
                {
                    d.SetLayerOpacity(middle.Id, v, coalesce: true); Check("middle " + v);
                    Assert.That(c.LastNestedReuseCount, Is.EqualTo(blocks), "every block starts the group from its copy");
                    Assert.That(c.LastNestedCaptureCount, Is.Zero, "the copy stays where it is");
                    if (backend == "Cpu") Assert.That(c.LastCpuJobCount, Is.EqualTo(blocks), "only the changed tiles (one rectangle per block) are composited");
                }
                // グループ自身の不透明度: 中身の全部の写し（グループの内側の結果）を取り、次からはそこから
                var inner = groups[groups.Count - 1];
                d.SetLayerOpacity(inner.Id, .8, coalesce: true); Check("group 1");
                Assert.That(c.LastNestedCaptureCount, Is.EqualTo(blocks));
                d.SetLayerOpacity(inner.Id, .7, coalesce: true); Check("group 2");
                Assert.That(c.LastNestedReuseCount, Is.GreaterThanOrEqualTo(blocks)); Assert.That(c.LastNestedCaptureCount, Is.Zero);
                // グループの下の層: 分離のグループの写しは使える、通過は下の結果を含むので使えない
                d.SetLayerOpacity(below.Id, .6); Check("below 1");
                bool isolated = kind.Contains("isolated");
                if (isolated) Assert.That(c.LastNestedReuseCount, Is.GreaterThanOrEqualTo(blocks), "an isolated group does not depend on what is below it");
                else Assert.That(c.LastNestedReuseCount, Is.Zero, "a pass-through group's copy holds what was below it");
                d.SetLayerOpacity(below.Id, .5); Check("below 2");
                // Undo/Redo・並べ替え・グループの合成モード・グループからの出し入れ・チャンネルの切り替え
                d.Undo(); Check("undo"); d.Undo(); Check("undo 2"); d.Redo(); Check("redo");
                d.SetLayerOpacity(middle.Id, .45, coalesce: true); Check("middle again");
                d.MoveLayer(middle.Id, 2); Check("move the middle layer down");
                d.SetLayerOpacity(middle.Id, .5, coalesce: true); Check("middle after the move 1");
                d.SetLayerOpacity(middle.Id, .55, coalesce: true); Check("middle after the move 2");
                Assert.That(c.LastNestedReuseCount, Is.GreaterThanOrEqualTo(blocks));
                d.SetLayerBlendMode(inner.Id, isolated ? LayerBlendMode.PassThrough : LayerBlendMode.Multiply); Check("group blend mode (isolation changes)");
                d.SetLayerOpacity(middle.Id, .6, coalesce: true); Check("middle after the group mode 1");
                d.SetLayerOpacity(middle.Id, .65, coalesce: true); Check("middle after the group mode 2");
                d.MoveLayerTo(middle.Id, Guid.Empty, 1); Check("move the middle layer out of the groups");
                d.MoveLayerTo(middle.Id, inner.Id, 3); Check("move it back in");
                d.SetLayerOpacity(middle.Id, .7, coalesce: true); Check("middle back in 1");
                d.SetLayerOpacity(middle.Id, .75, coalesce: true); Check("middle back in 2");
                d.SetLayerVisibility(groups[0].Id, false); Check("hide the outer group");
                d.SetLayerVisibility(groups[0].Id, true); Check("show the outer group");
                d.SetLayerClipping(middle.Id, true); Check("clip the middle layer");
                d.SetLayerOpacity(middle.Id, .8, coalesce: true); Check("clipped middle 1");
                d.SetLayerOpacity(middle.Id, .85, coalesce: true); Check("clipped middle 2");
                var roughness = d.Layers.First(l => l.Name == "L7"); d.SetChannelEnabled(roughness.Id, PaintChannel.Roughness, true);
                using (var s = d.BeginStroke(roughness.Id, PaintChannel.Roughness, new BrushSettings { Radius = 200, Color = new Rgba32(90, 90, 90, 255), PressureSize = false, PressureOpacity = false })) { s.Add(new BrushSample(300, 250)); s.Commit(); }
                c.Update(d, PaintChannel.Roughness); CpuCompositingTests.AssertSameBytes(Expected(backend, d, PaintChannel.Roughness), Read(c), kind + ": roughness");
                Check("back to colour");
            }
        }

        /// <summary>グループの下の層のドラッグ: 分離のグループは、1 回目に中身の全部の写しを先回りで取り（空いた予算で）、2 回目からは中身を合成し直さない
        /// （グループ自身の不透明度を変えても）。通過のグループは下の結果を含むので取らない。</summary>
        [TestCase("Cpu", "isolated"), TestCase("Cpu", "2 isolated"), TestCase("Cpu", "masked isolated"), TestCase("Cpu", "pass-through"), TestCase("Cpu", "masked")]
        [TestCase("Gpu", "isolated"), TestCase("Gpu", "2 isolated"), TestCase("Gpu", "masked isolated"), TestCase("Gpu", "pass-through"), TestCase("Gpu", "masked")]
        public void DraggingALayerBelowAnIsolatedGroupKeepsTheGroupsWholeContents(string backend, string kind)
        {
            var d = TenLayers(kind, out _, out var below, out var groups);
            bool isolated = kind.Contains("isolated");
            using (var c = Compositor(backend))
            {
                void Check(string step)
                {
                    c.Update(d, PaintChannel.Color);
                    CpuCompositingTests.AssertSameBytes(Expected(backend, d), Read(c), kind + ": " + step);
                    Assert.That(c.ResidentBytes, Is.LessThanOrEqualTo(c.ResidentBudgetBytes));
                }
                Check("first");
                int blocks = 2;
                d.SetLayerOpacity(below.Id, .9, coalesce: true); Check("below 1");
                if (isolated) Assert.That(c.LastNestedCaptureCount, Is.EqualTo(blocks * groups.Count), "the whole contents of every isolated group are kept");
                else Assert.That(c.LastNestedCaptureCount, Is.Zero, "a pass-through group's contents depend on what is below");
                foreach (var v in new[] { .8, .7 })
                {
                    d.SetLayerOpacity(below.Id, v, coalesce: true); Check("below " + v);
                    Assert.That(c.LastNestedReuseCount, Is.EqualTo(isolated ? blocks : 0));
                    if (backend == "Cpu") Assert.That(c.LastCpuJobCount, Is.EqualTo(blocks));
                }
                d.SetLayerOpacity(groups[0].Id, .6, coalesce: true); Check("the outer group's opacity");
                if (isolated) Assert.That(c.LastNestedReuseCount, Is.EqualTo(blocks), "the group's own opacity does not touch its contents");
                d.SetLayerBlendMode(groups[0].Id, LayerBlendMode.Screen); Check("the outer group's blend mode");
                if (isolated) Assert.That(c.LastNestedReuseCount, Is.EqualTo(blocks));
                d.SetLayerVisibility(below.Id, false); Check("hide below");
                d.SetLayerVisibility(below.Id, true); Check("show below");
                d.Undo(); Check("undo"); d.Undo(); Check("undo 2"); d.Redo(); Check("redo");
            }
        }

        /// <summary>塗りつぶしの画像（評価した出力を読む層、HasEvaluatedOutput）を入れ子のグループの中と外に置いた文書: 写しを持つ合成器と持たない
        /// （予算 0）合成器が、ランダムな編集（画像の差し替え・外す・投影のドラッグ・値、ほかの層の編集）の後ごとにバイト単位で同じ。CPU の経路は
        /// CPU の正本とも、GPU の経路は写しの無い新しい合成器とも比べる。</summary>
        [TestCase("Cpu"), TestCase("Gpu")]
        public void FillImagesInsideNestedGroupsGiveTheSameBytesWithAndWithoutCopies(string backend)
        {
            if (backend == "Gpu") GpuTests.RequireWorkingShader(ShaderName);
            var rnd = new System.Random(1618);
            var resources = new ProjectResources();
            var images = new List<ImageResource>();
            for (int i = 0; i < 3; i++)
            {
                int w = 24 + 40 * i, h = 16 + 30 * i; var rgba = new byte[w * h * 4]; rnd.NextBytes(rgba);
                for (int k = 3; k < rgba.Length; k += 4) rgba[k] = (byte)(rgba[k] < 50 ? 0 : rgba[k] > 170 ? 255 : rgba[k]);
                images.Add(resources.Add("image " + i, ImageContent.FromPixels(rgba, w, h), ResourceOrigin.None, i == 1 ? ResourceColorSpace.Linear : ResourceColorSpace.Srgb, out _));
            }
            FillProjection RandomProjection() => FillProjection.Default.WithTiles(.5 + 3 * rnd.NextDouble(), .5 + 3 * rnd.NextDouble()).WithOffset(rnd.NextDouble() - .5, rnd.NextDouble() - .5).WithRotation(rnd.Next(-180, 181));
            int reuse = 0, evaluated = 0;
            for (int c = 0; c < 3; c++)
            {
                var d = NestedGroupCopyTests.NestedDocument(rnd, 1024, 512, 64);
                d.ImageResources = resources;
                // 塗りつぶしの層を足してグループの中へ、画像を付ける（Color・Normal は値があるチャンネル、Roughness は値を足して）
                var groups = d.Layers.Where(l => l.IsGroup).ToList();
                for (int i = 0; i < 4; i++)
                {
                    var f = d.AddFillLayer("image fill " + i, new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(90, 120, 150, 255) }, { PaintChannel.Roughness, new Rgba32(128, 128, 128, 255) } });
                    if (groups.Count > 0 && rnd.Next(4) != 0) { var g = groups[rnd.Next(groups.Count)]; d.MoveLayerTo(f.Id, g.Id, rnd.Next(d.ChildrenOf(g.Id).Count(x => x.Id != f.Id) + 1)); }
                    if (rnd.Next(2) == 0) d.SetLayerBlendMode(f.Id, Modes[rnd.Next(Modes.Length)]);
                    if (rnd.Next(2) == 0) d.SetLayerOpacity(f.Id, .3 + .7 * rnd.NextDouble());
                }
                foreach (var f in d.Layers.Where(l => l.Kind == LayerKind.Fill).ToList())
                {
                    foreach (var ch in new[] { PaintChannel.Color, PaintChannel.Roughness })
                        if (f.FillValues.ContainsKey(ch) && rnd.Next(4) != 0) d.SetFillImage(f.Id, ch, images[rnd.Next(images.Count)].Id);
                    d.SetFillProjection(f.Id, RandomProjection());
                }
                d.ClearHistory();
                Assert.That(d.Layers.Any(l => l.HasEvaluatedOutput(PaintChannel.Color) && !l.HasActiveFilters(PaintChannel.Color)), Is.True, "a fill image is read as evaluated output");
                var channel = PaintChannel.Color;
                using (var live = Compositor(backend))
                using (var none = Compositor(backend))
                {
                    live.ResidentBudgetBytes = 64L << 20; none.ResidentBudgetBytes = 0;
                    PaintLayer focus = null; int focusLeft = 0;
                    for (int i = 0; i < 40; i++)
                    {
                        string step = "first";
                        if (i > 0)
                        {
                            var fills = d.Layers.Where(l => l.Kind == LayerKind.Fill).ToList();
                            var f = fills.Count == 0 ? null : fills[rnd.Next(fills.Count)];
                            switch (f == null ? 9 : rnd.Next(6))
                            {
                                case 0: case 1:
                                    // 投影のドラッグ（同じ層を続けて）
                                    var p = f.Projection;
                                    for (int k = 0; k < 2; k++) { p = p.WithOffset(Math.Max(-.9, Math.Min(.9, p.OffsetU + .01)), p.OffsetV); d.SetFillProjection(f.Id, p, coalesce: true); }
                                    step = "drag the projection of " + f.Name; break;
                                case 2: d.SetFillImage(f.Id, PaintChannel.Color, rnd.Next(4) == 0 ? (Guid?)null : images[rnd.Next(images.Count)].Id); step = "image of " + f.Name; break;
                                default: step = Edit(rnd, d, ref focus, ref focusLeft, ref channel); break;
                            }
                        }
                        live.Update(d, channel); none.Update(d, channel);
                        var withCopies = Read(live);
                        CpuCompositingTests.AssertSameBytes(Read(none), withCopies, "document " + c + " step " + i + " " + step + ": with and without copies");
                        CpuCompositingTests.AssertSameBytes(Expected(backend, d, channel), withCopies, "document " + c + " step " + i + " " + step + ": the reference");
                        Assert.That(none.NestedCopyCount, Is.Zero); Assert.That(live.ResidentBytes, Is.LessThanOrEqualTo(live.ResidentBudgetBytes));
                        reuse += live.LastNestedReuseCount;
                        if (d.Layers.Any(l => l.HasEvaluatedOutput(channel) && !l.HasActiveFilters(channel))) evaluated++;
                    }
                }
            }
            Assert.That(reuse, Is.GreaterThan(10), "groups started from their copies"); Assert.That(evaluated, Is.GreaterThan(60));
        }

        /// <summary>予算: 0 なら何も残さない、ブロック 1 つ分なら一部だけ、下げれば手放す、捨てても次の更新は正しい、しばらく使わなければ捨てる。</summary>
        [TestCase("Cpu"), TestCase("Gpu")]
        public void TheBudgetLimitsTheCopiesInsideGroupsAndDroppingThemStaysExact(string backend)
        {
            var d = TenLayers("2 masked isolated", out var middle, out _, out _);
            using (var c = Compositor(backend))
            {
                void Check(string step)
                {
                    c.Update(d, PaintChannel.Color);
                    CpuCompositingTests.AssertSameBytes(Expected(backend, d), Read(c), step);
                    Assert.That(c.ResidentBytes, Is.LessThanOrEqualTo(c.ResidentBudgetBytes), step);
                }
                double v = .3;
                void Drag(string step) { d.SetLayerOpacity(middle.Id, v += .02, coalesce: true); Check(step); }
                c.ResidentBudgetBytes = 0; Check("first");
                Drag("no budget 1"); Drag("no budget 2");
                Assert.That(c.NestedCopyCount, Is.Zero); Assert.That(c.LastNestedReuseCount, Is.Zero);
                long block = 4L * 512 * 512;
                c.ResidentBudgetBytes = block;
                Drag("one block 1"); Drag("one block 2"); Drag("one block 3");
                Assert.That(c.NestedCopyCount, Is.LessThanOrEqualTo(1)); Assert.That(c.LastNestedReuseCount, Is.LessThanOrEqualTo(1));
                c.ResidentBudgetBytes = 256L << 20;
                Drag("budget back 1"); Drag("budget back 2"); Drag("budget back 3");
                Assert.That(c.LastNestedReuseCount, Is.EqualTo(2));
                Assert.That(c.NestedCopyCount, Is.EqualTo(2));
                c.ResidentBudgetBytes = c.ResidentBytes - 1;
                Assert.That(c.NestedCopyCount, Is.Zero, "lowering the budget below what is kept releases every copy");
                Drag("after lowering 1"); Drag("after lowering 2");
                c.ResidentBudgetBytes = 256L << 20; Drag("again 1"); Drag("again 2");
                Assert.That(c.NestedCopyCount, Is.GreaterThan(0));
                c.ReleaseResidentCaches();
                Assert.That(c.NestedCopyCount, Is.Zero); Assert.That(c.ResidentBytes, Is.Zero);
                Drag("after releasing 1"); Drag("after releasing 2"); Drag("after releasing 3");
                Assert.That(c.LastNestedReuseCount, Is.EqualTo(2));
                for (int i = 0; i <= TileGpuCompositor.IdleUpdatesBeforeRelease; i++) c.Update(d, PaintChannel.Color);
                Assert.That(c.NestedCopyCount, Is.Zero, "copies unused for a while are released");
                Drag("after the idle release");
            }
        }

        [Test, Category("GPU")]
        public void SmallGpuBudgetsKeepCompositeCopiesAheadOfUploadedInputs()
        {
            GpuTests.RequireWorkingShader(ShaderName);
            var d = TenLayers("2 isolated", out var middle, out _, out _);
            using (var c = Compositor("Gpu"))
            {
                c.ResidentBudgetBytes = 4L * 512 * 512 * 4; // 2 ブロック × 下の写しとグループの写しだけ。
                c.Update(d, PaintChannel.Color);
                for (int i = 0; i < 5; i++)
                {
                    d.SetLayerOpacity(middle.Id, .4 + i * .02, coalesce: true); c.Update(d, PaintChannel.Color);
                    CpuCompositingTests.AssertSameBytes(Expected("Gpu", d), Read(c), "small budget change " + i);
                    Assert.That(c.Path, Is.EqualTo(TileGpuCompositor.CompositePath.Gpu), c.Backend);
                    Assert.That(c.LastCopyEvictionCount, Is.Zero, "input uploads cannot evict composite copies");
                    Assert.That(c.NestedCopyCount, Is.EqualTo(2));
                    Assert.That(c.ResidentBytes, Is.LessThanOrEqualTo(c.ResidentBudgetBytes));
                    if (i > 0) { Assert.That(c.LastNestedReuseCount, Is.EqualTo(2)); Assert.That(c.LastBelowReuseCount, Is.EqualTo(2)); }
                }
                c.ResidentBudgetBytes = 0;
                Assert.That(c.ResidentBytes, Is.Zero); Assert.That(c.NestedCopyCount, Is.Zero);
                d.SetLayerOpacity(middle.Id, .7); c.Update(d, PaintChannel.Color);
                CpuCompositingTests.AssertSameBytes(Expected("Gpu", d), Read(c), "zero budget");
                Assert.That(c.LastNestedCaptureCount, Is.Zero);
            }
        }

        [Test]
        public void InvalidatedCpuCopiesReuseTheirArraysWithinTheResidentBudget()
        {
            var d = TenLayers("2 isolated", out var middle, out _, out _);
            var lower = d.Layers.First(l => l.Name == "L2");
            using (var c = Compositor("Cpu"))
            {
                c.ResidentBudgetBytes = 4L * 512 * 512 * 4;
                void Check() { c.Update(d, PaintChannel.Color); CpuCompositingTests.AssertSameBytes(d.Composite(PaintChannel.Color), Read(c), "CPU reused arrays"); Assert.That(c.ResidentBytes, Is.LessThanOrEqualTo(c.ResidentBudgetBytes)); }
                Check(); d.SetLayerOpacity(middle.Id, .5); Check();
                Assert.That(c.LastResidentArrayAllocationCount, Is.EqualTo(4));
                d.SetLayerOpacity(lower.Id, .4); Check();
                Assert.That(c.LastResidentArrayReuseCount, Is.EqualTo(2));
                Assert.That(c.LastResidentArrayAllocationCount, Is.Zero, "retaking below an invalidated group copy does not allocate another pixel array");
                // 0 の位置へ変わると写しを取り直さない。使わない配列も予算に数える。
                var bottom = d.Layers.First(l => l.Name == "L0"); d.SetLayerOpacity(bottom.Id, .3); Check();
                Assert.That(c.PooledResidentBytes, Is.EqualTo(2L * 512 * 512 * 4));
                Assert.That(c.NestedCopyCount, Is.Zero);
                Assert.That(c.ResidentBytes, Is.EqualTo(c.ResidentBudgetBytes));
                for (int i = 0; i <= TileGpuCompositor.IdleUpdatesBeforeRelease; i++) c.Update(d, PaintChannel.Color);
                Assert.That(c.ResidentBytes, Is.Zero); Assert.That(c.PooledResidentBytes, Is.Zero);
                d.SetLayerOpacity(middle.Id, .6); Check(); d.SetLayerOpacity(lower.Id, .6); Check();
                c.ResidentBudgetBytes = c.ResidentBytes - 1;
                Assert.That(c.ResidentBytes, Is.Zero); Assert.That(c.PooledResidentBytes, Is.Zero);
                Assert.That(() => c.ResidentBudgetBytes = -1, Throws.TypeOf<ArgumentOutOfRangeException>());
                c.ResidentBudgetBytes = 8L << 20; d.SetLayerOpacity(middle.Id, .7); Check();
                var other = TenLayers("isolated", out _, out _, out _, 384, 192);
                c.Update(other, PaintChannel.Color);
                Assert.That(c.NestedCopyCount, Is.Zero); Assert.That(c.PooledResidentBytes, Is.Zero);
                CpuCompositingTests.AssertSameBytes(other.Composite(PaintChannel.Color), Read(c), "document and size changed");
                c.ReleaseResidentCaches(); Assert.That(c.ResidentBytes, Is.Zero);
            }
        }

        /// <summary>クリッピングされたグループの中で Anchor を読むマスク: 別チャンネルの参照元を変えると、中身の写しも更新する。</summary>
        [TestCase("Cpu", true), TestCase("Cpu", false), TestCase("Gpu", true), TestCase("Gpu", false)]
        public void ClippedGroupCopiesFollowAnchorChangesInAnotherChannel(string backend, bool copyTexture)
        {
            if (backend == "Gpu") GpuTests.RequireWorkingShader(ShaderName);
            var d = new PaintDocument(96, 64, 16);
            var source = d.AddFillLayer("height", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Height, new Rgba32(100, 100, 100, 255) } });
            var anchor = d.AddAnchor(source.Id, AnchorPlacement.Layer, "height anchor");
            d.AddFillLayer("clip base", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(40, 70, 120, 200) } });
            var prefix = d.AddFillLayer("prefix", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(90, 80, 130, 255) } });
            var reader = d.AddFillLayer("reader", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(220, 140, 30, 255) } });
            d.AddLayerMask(reader.Id);
            d.AddFilter(reader.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Anchor).WithAnchor(anchor.Id, PaintChannel.Height, AnchorRead.Value)));
            var inner = d.GroupLayers(new[] { prefix.Id, reader.Id }, "inner");
            var outer = d.GroupLayers(new[] { inner.Id }, "clipped group"); d.SetLayerClipping(outer.Id, true);
            d.ClearHistory();
            using (var c = new TileGpuCompositor(backend == "Gpu", copyTexture) { ResidentBudgetBytes = 4L << 20 })
            {
                void Check(string step)
                {
                    c.Update(d, PaintChannel.Color);
                    var actual = Read(c);
                    var fresh = DocumentBinary.Read(DocumentBinary.Write(d));
                    CpuCompositingTests.AssertSameBytes(Expected(backend, fresh), actual, step + ": fresh display");
                    if (backend == "Gpu")
                    {
                        GpuTests.AssertMatches(fresh.Composite(PaintChannel.Color), actual, step + ": CPU reference");
                        Assert.That(c.Path, Is.EqualTo(TileGpuCompositor.CompositePath.Gpu), c.Backend);
                    }
                    Assert.That(c.ResidentBytes, Is.LessThanOrEqualTo(c.ResidentBudgetBytes));
                }
                Check("first"); d.SetLayerOpacity(reader.Id, .8); Check("capture");
                d.SetLayerOpacity(reader.Id, .7); Check("reuse");
                Assert.That(c.LastNestedReuseCount, Is.GreaterThan(0));
                var before = Read(c);
                d.SetFillValue(source.Id, PaintChannel.Height, new Rgba32(230, 230, 230, 255)); Check("Height changes Color");
                Assert.That(Read(c), Is.Not.EqualTo(before), "the anchor changes the clipped reader's mask");
                d.Undo(); Check("undo Height"); d.Redo(); Check("redo Height");
                d.RemoveAnchor(anchor.Id); Check("anchor removed"); d.Undo(); Check("undo removal");
                d.SetFillValue(prefix.Id, PaintChannel.Color, new Rgba32(130, 100, 40, 255)); Check("retake lower");
                c.ResidentBudgetBytes = 0;
                d.SetFillValue(source.Id, PaintChannel.Height, new Rgba32(30, 30, 30, 255)); Check("zero budget");
                Assert.That(c.NestedCopyCount, Is.Zero);
            }
        }

        static PaintDocument ClippedDocument(out PaintLayer middle, out PaintLayer basis, out PaintLayer outer)
        {
            var d = new PaintDocument(96, 64, 16);
            var values = new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(80, 110, 140, 255) }, { PaintChannel.Roughness, new Rgba32(120, 120, 120, 255) }, { PaintChannel.Normal, new Rgba32(128, 128, 255, 255) } };
            d.AddFillLayer("background", values);
            basis = d.AddLayer("clip base");
            var bottom = d.AddFillLayer("bottom", values);
            middle = d.AddLayer("middle");
            var top = d.AddFillLayer("top", values);
            foreach (var ch in NestedGroupCopyTests.Channels)
            {
                d.SetChannelEnabled(basis.Id, ch, true); d.SetChannelEnabled(middle.Id, ch, true);
                for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++)
                {
                    var p = ch == PaintChannel.Normal ? new Rgba32(136, 120, 250, 255) : ch == PaintChannel.Roughness ? new Rgba32(160, 160, 160, 255) : new Rgba32((byte)(30 + x), (byte)(70 + y), 180, 255);
                    middle.GetChannel(ch).SetPixel(x, y, p);
                    if (x >= 16 || y >= 16) basis.GetChannel(ch).SetPixel(x, y, values[ch]); // 基底の左下のタイルは無い。
                }
            }
            var inner = d.GroupLayers(new[] { bottom.Id, middle.Id, top.Id }, "inner"); d.SetLayerBlendMode(inner.Id, LayerBlendMode.Normal);
            outer = d.GroupLayers(new[] { inner.Id }, "clipped group"); d.SetLayerClipping(outer.Id, true);
            d.AddLayerMask(outer.Id); d.SetLayerMaskDensity(outer.Id, .8);
            d.SetLayerOpacity(top.Id, .5); d.ClearHistory();
            return d;
        }

        [TestCase("Cpu", true), TestCase("Cpu", false), TestCase("Gpu", true), TestCase("Gpu", false)]
        public void ClippedGroupCopiesStayExactThroughEditsUndoCancellationAndReload(string backend, bool copyTexture)
        {
            if (backend == "Gpu") GpuTests.RequireWorkingShader(ShaderName);
            var d = ClippedDocument(out var middle, out var basis, out var outer);
            using (var c = new TileGpuCompositor(backend == "Gpu", copyTexture))
            {
                foreach (var channel in NestedGroupCopyTests.Channels)
                {
                    void Check(string step, bool sliced = false)
                    {
                        var schedule = sliced ? new CompositeSchedule { BudgetMilliseconds = 0 } : null;
                        while (!c.Update(d, channel, schedule)) { }
                        var actual = Read(c);
                        CpuCompositingTests.AssertSameBytes(Expected(backend, d, channel), actual, step + ": cache vs no cache");
                        if (backend == "Gpu") { GpuTests.AssertMatches(d.Composite(channel), actual, step + ": CPU vs GPU"); Assert.That(c.Path, Is.EqualTo(TileGpuCompositor.CompositePath.Gpu), c.Backend); }
                        Assert.That(c.ResidentBytes, Is.LessThanOrEqualTo(c.ResidentBudgetBytes));
                    }
                    Check("first"); d.SetLayerOpacity(middle.Id, .6); Check("capture");
                    Assert.That(c.LastNestedCaptureCount, Is.GreaterThan(0));
                    d.SetLayerOpacity(middle.Id, .5); Check("resume", true);
                    Assert.That(c.LastNestedReuseCount, Is.GreaterThan(0));
                    d.SetLayerOpacity(outer.Id, .7); Check("clipped group's opacity");
                    d.SetLayerOpacity(outer.Id, .6); Check("whole clipped contents");
                    Assert.That(c.LastNestedReuseCount, Is.GreaterThan(0));
                    using (var s = d.BeginStroke(basis.Id, channel, new BrushSettings { Radius = 20, Color = new Rgba32(130, 130, 230, 255), PressureSize = false, PressureOpacity = false })) { s.Add(new BrushSample(8, 8)); s.Commit(); }
                    Check("base gains its missing tile"); d.Undo(); Check("undo base"); d.Redo(); Check("redo base");
                    using (var s = d.BeginStroke(middle.Id, channel, new BrushSettings { Radius = 12, Color = new Rgba32(170, 150, 220, 255), PressureSize = false, PressureOpacity = false })) { s.Add(new BrushSample(35, 30)); Check("active stroke"); s.Cancel(); }
                    Check("cancel stroke", true);
                    using (var s = d.BeginMaskStroke(outer.Id, new BrushSettings { Radius = 12, Color = new Rgba32(0, 0, 0, 150), PressureSize = false, PressureOpacity = false })) { s.Add(new BrushSample(40, 30)); s.Commit(); }
                    Check("group mask"); d.SetLayerBlendMode(outer.Id, LayerBlendMode.Normal); Check("isolated clip");
                    d.SetLayerClipping(outer.Id, false); Check("unclip"); d.SetLayerClipping(outer.Id, true); Check("clip again");
                    var binary = DocumentBinary.Write(d); var restored = DocumentBinary.Read(binary);
                    c.Update(restored, channel); CpuCompositingTests.AssertSameBytes(Expected(backend, restored, channel), Read(c), "save and reload");
                    Assert.That(c.LastNestedReuseCount, Is.Zero, "another document never resumes the previous document's copies");
                    Assert.That(DocumentBinary.Write(restored), Is.EqualTo(binary), "display caches do not enter the native document");
                    c.ResidentBudgetBytes = 0; d.SetLayerOpacity(middle.Id, .8); Check("no copy budget");
                    Assert.That(c.NestedCopyCount, Is.Zero); c.ResidentBudgetBytes = 64L << 20;
                }
            }
        }

        /// <summary>GPU の経路: ランダムな入れ子の文書とランダムな編集で、写しを持たない新しい合成器と毎回バイト単位で同じ（時間で区切って何回かに
        /// 分けて合成する合成器も、合成し終えたとき）。</summary>
        [Test, Category("GPU")] public void TheGpuDisplayMatchesAFreshCompositeThroughRandomEditsInNestedGroups()
        {
            GpuTests.RequireWorkingShader(ShaderName);
            var rnd = new System.Random(2718);
            int reuse = 0, captures = 0;
            long[] budgets = { 0, 3L << 20, 8L << 20, 256L << 20 };
            for (int c = 0; c < 4; c++)
            {
                var d = NestedGroupCopyTests.NestedDocument(rnd, 1024, 512, 64);
                var channel = PaintChannel.Color;
                using (var live = new TileGpuCompositor { ResidentBudgetBytes = budgets[c % budgets.Length] })
                using (var sliced = new TileGpuCompositor { ResidentBudgetBytes = budgets[(c + 1) % budgets.Length] })
                {
                    PaintLayer focus = null; int focusLeft = 0;
                    for (int i = 0; i < 50; i++)
                    {
                        string step = i == 0 ? "first" : Edit(rnd, d, ref focus, ref focusLeft, ref channel);
                        if (i == 35) { live.ReleaseResidentCaches(); step += " (released)"; }
                        live.Update(d, channel);
                        var expected = Expected("Gpu", d, channel);
                        CpuCompositingTests.AssertSameBytes(expected, GpuTests.Read(live.Texture), "document " + c + " step " + i + " " + step);
                        // 時間で区切る表示（1 回に 1 ブロック）: 4 回に 1 回だけ最後まで合成して比べる
                        bool done = sliced.Update(d, channel, new CompositeSchedule { BudgetMilliseconds = 0 });
                        if (i % 4 == 3)
                        {
                            while (!done) done = sliced.Update(d, channel, new CompositeSchedule { BudgetMilliseconds = 0 });
                            CpuCompositingTests.AssertSameBytes(expected, GpuTests.Read(sliced.Texture), "document " + c + " step " + i + " " + step + " (sliced)");
                        }
                        Assert.That(live.ResidentBytes, Is.LessThanOrEqualTo(live.ResidentBudgetBytes), step);
                        reuse += live.LastNestedReuseCount; captures += live.LastNestedCaptureCount;
                    }
                }
            }
            Assert.That(captures, Is.GreaterThan(10)); Assert.That(reuse, Is.GreaterThan(10));
        }
    }
}
