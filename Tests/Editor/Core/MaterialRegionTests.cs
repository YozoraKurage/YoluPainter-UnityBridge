using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Paths;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>マテリアルの範囲編集。全チャンネルの単独編集とのバイト一致、有効化と履歴、予算超過の全体取消を検証する。</summary>
    public sealed class MaterialRegionTests
    {
        static readonly ChannelPaint[] Material =
        {
            new ChannelPaint(PaintChannel.Color, new Rgba32(223, 31, 97, 211)),
            new ChannelPaint(PaintChannel.Roughness, new Rgba32(71, 71, 71, 180)),
            new ChannelPaint(PaintChannel.Metallic, new Rgba32(247, 247, 247, 255)),
            new ChannelPaint(PaintChannel.Height, new Rgba32(145, 145, 145, 231)),
            new ChannelPaint(PaintChannel.Normal, new Rgba32(179, 97, 235, 255)),
            new ChannelPaint(PaintChannel.Emission, new Rgba32(13, 203, 73, 190))
        };
        static PaintDocument Document(out PaintLayer l, bool populated = true)
        {
            var d = new PaintDocument(35, 29, 8); l = d.AddLayer("paint");
            if (populated)
                foreach (var c in new[] { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Normal, PaintChannel.Metallic })
                {
                    var s = l.GetChannel(c);
                    for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++)
                        s.SetPixel(x, y, new Rgba32((byte)(x * 7), (byte)(y * 8), (byte)(x + y * 3), (byte)((x + y + (int)c) % 4 * 85)));
                    if (c == PaintChannel.Metallic) d.SetChannelEnabled(l.Id, c, false);
                }
            d.ClearHistory(); return d;
        }
        static byte[] Pixels(PaintDocument d, PaintLayer l, PaintChannel c)
        {
            var bytes = new byte[d.Width * d.Height * 4];
            for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++)
            {
                var p = l.GetPixel(c, x, y); int o = (y * d.Width + x) * 4;
                bytes[o] = p.R; bytes[o + 1] = p.G; bytes[o + 2] = p.B; bytes[o + 3] = p.A;
            }
            return bytes;
        }
        static GradientSettings Gradient(GradientShape shape = GradientShape.Linear) => new GradientSettings
        { Shape = shape, X0 = 3.25, Y0 = 4.75, X1 = 25.5, Y1 = 20.25, Opacity = .73, From = new Rgba32(1, 2, 3), To = new Rgba32(9, 8, 7) };
        static void Prepare(PaintDocument d, PaintLayer l, bool keepAlpha)
        {
            d.SetSelection(SelectionMask.Ellipse(d, 18, 14, 13.25, 10.75).Feather(2));
            if (keepAlpha) d.SetLayerLocks(l.Id, LayerLocks.Transparency);
            d.ClearHistory();
        }
        static bool Edit(PaintDocument d, PaintLayer l, string kind, SelectionMask region = null)
            => kind == "fill" ? d.FillMaterial(l.Id, Material, .73, region) : d.GradientMaterial(l.Id, Material, Gradient(), region);

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)]
        public void MaterialFillMatchesEverySingleChannelWithSelectionRegionLocksAndErase(bool keepAlpha, bool erase)
        {
            var multi = Document(out var ml); Prepare(multi, ml, keepAlpha);
            var before = DocumentBinary.Write(multi);
            var region = SelectionMask.Rectangle(multi, 8, 2, 31, 27);
            Assert.That(multi.FillMaterial(ml.Id, Material, .73, region, erase), Is.True);
            foreach (var m in Material)
            {
                var one = Document(out var ol); Prepare(one, ol, keepAlpha);
                one.SetChannelEnabled(ol.Id, m.Channel, true);
                one.Fill(ol.Id, m.Channel, m.Value, .73, SelectionMask.Rectangle(one, 8, 2, 31, 27), erase);
                Assert.That(Pixels(multi, ml, m.Channel), Is.EqualTo(Pixels(one, ol, m.Channel)), m.Channel.ToString());
            }
            Assert.That(multi.UndoCount, Is.EqualTo(1));
            var painted = DocumentBinary.Write(multi);
            multi.Undo(); Assert.That(DocumentBinary.Write(multi), Is.EqualTo(before));
            multi.Redo(); Assert.That(DocumentBinary.Write(multi), Is.EqualTo(painted));
        }

        [TestCase(GradientShape.Linear, false, false)] [TestCase(GradientShape.Radial, false, false)]
        [TestCase(GradientShape.Linear, true, false)] [TestCase(GradientShape.Radial, true, false)]
        [TestCase(GradientShape.Linear, false, true)] [TestCase(GradientShape.Radial, false, true)]
        public void MaterialGradientMatchesEverySingleChannelFade(GradientShape shape, bool keepAlpha, bool erase)
        {
            var multi = Document(out var ml); Prepare(multi, ml, keepAlpha);
            var g = Gradient(shape); var before = DocumentBinary.Write(multi);
            Assert.That(multi.GradientMaterial(ml.Id, Material, g, SelectionMask.Rectangle(multi, 5, 5, 33, 28), erase), Is.True);
            foreach (var m in Material)
            {
                var one = Document(out var ol); Prepare(one, ol, keepAlpha); one.SetChannelEnabled(ol.Id, m.Channel, true);
                var single = Gradient(shape); single.From = m.Value; single.To = Rgba32.Transparent;
                one.Gradient(ol.Id, m.Channel, single, SelectionMask.Rectangle(one, 5, 5, 33, 28), erase);
                Assert.That(Pixels(multi, ml, m.Channel), Is.EqualTo(Pixels(one, ol, m.Channel)), m.Channel.ToString());
            }
            Assert.That(g.From, Is.EqualTo(new Rgba32(1, 2, 3)), "呼び手の設定は変更しない");
            Assert.That(multi.UndoCount, Is.EqualTo(1));
            var painted = DocumentBinary.Write(multi);
            Assert.That(DocumentBinary.Write(DocumentBinary.Read(painted)), Is.EqualTo(painted), "正本の保存復元");
            multi.Undo(); Assert.That(DocumentBinary.Write(multi), Is.EqualTo(before));
            multi.Redo(); Assert.That(DocumentBinary.Write(multi), Is.EqualTo(painted));
        }

        [TestCase("fill")] [TestCase("gradient")]
        public void DisabledChannelsAndNewSurfacesReturnAsTheSameObjects(string kind)
        {
            var d = Document(out var l); var before = DocumentBinary.Write(d);
            Assert.That(Edit(d, l, kind), Is.True);
            var surfaces = Material.ToDictionary(m => m.Channel, m => l.GetChannel(m.Channel));
            var painted = DocumentBinary.Write(d);
            d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
            Assert.That(l.TryGetChannel(PaintChannel.Height, out _), Is.False);
            Assert.That(l.IsChannelEnabled(PaintChannel.Metallic), Is.False);
            d.Redo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(painted));
            foreach (var m in Material) Assert.That(l.GetChannel(m.Channel), Is.SameAs(surfaces[m.Channel]));
        }

        [TestCase("fill")] [TestCase("gradient")]
        public void RollbackAndSourceBudgetsAreSharedAndFailureKeepsHistory(string kind)
        {
            var d = Document(out var l, false);
            d.Fill(l.Id, PaintChannel.Color, new Rgba32(20, 30, 40)); d.Undo();
            long source = d.AllocatedBytes, history = d.HistoryBytes; var before = DocumentBinary.Write(d);
            int redo = d.RedoCount;
            // 1 チャンネルの巻き戻しは 20 タイル×64。複数チャンネルの途中で超える。
            d.ActiveStrokeBudgetBytes = 20 * 64;
            Assert.That(() => Edit(d, l, kind), Throws.InvalidOperationException.With.Message.Contains("rollback budget"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.HistoryBytes, Is.EqualTo(history));
            Assert.That(d.RedoCount, Is.EqualTo(redo)); Assert.That(d.UndoCount, Is.Zero);
            d.ActiveStrokeBudgetBytes = 1 << 20;
            var single = DocumentBinary.Read(before); var sl = single.GetLayer(l.Id);
            if (kind == "fill") single.Fill(sl.Id, Material[0].Channel, Material[0].Value, .73);
            else { var g = Gradient(); g.From = Material[0].Value; g.To = Rgba32.Transparent; single.Gradient(sl.Id, Material[0].Channel, g); }
            d.SourceBudgetBytes = single.AllocatedBytes; // 最初のチャンネルは収まるが、次以降は収まらない
            Assert.That(() => Edit(d, l, kind), Throws.InvalidOperationException.With.Message.Contains("Source tile payload budget"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.HistoryBytes, Is.EqualTo(history));
            Assert.That(d.RedoCount, Is.EqualTo(redo)); Assert.That(d.AllocatedBytes, Is.EqualTo(source));
        }

        [TestCase("fill")] [TestCase("gradient")]
        public void ARefusedRedoDoesNotEnableOrRestoreAnyChannel(string kind)
        {
            var d = Document(out var l, false); Edit(d, l, kind);
            var painted = DocumentBinary.Write(d); long bytes = d.AllocatedBytes;
            d.Undo(); var before = DocumentBinary.Write(d); long history = d.HistoryBytes;
            d.SourceBudgetBytes = bytes / 2;
            Assert.That(() => d.Redo(), Throws.InvalidOperationException.With.Message.Contains("budget"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.Zero);
            Assert.That(d.RedoCount, Is.EqualTo(1)); Assert.That(d.HistoryBytes, Is.EqualTo(history));
            d.SourceBudgetBytes = bytes; d.Redo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(painted));
        }

        [Test] public void ARefusedUndoDoesNotRestoreAnyOfTheDenseChannels()
        {
            var d = new PaintDocument(8, 8, 8); var l = d.AddLayer("paint");
            foreach (var m in Material)
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                    l.GetChannel(m.Channel).SetPixel(x, y, new Rgba32((byte)x, (byte)y, 11));
            d.ClearHistory(); var before = DocumentBinary.Write(d);
            d.FillMaterial(l.Id, Material.Select(m => new ChannelPaint(m.Channel, new Rgba32(200, 30, 50))).ToArray());
            var painted = DocumentBinary.Write(d); d.SourceBudgetBytes = d.AllocatedBytes + 256;
            Assert.That(() => d.Undo(), Throws.InvalidOperationException.With.Message.Contains("budget"));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(painted)); Assert.That(d.UndoCount, Is.EqualTo(1));
            d.SourceBudgetBytes = 4096; d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
        }

        [TestCase("fill")] [TestCase("gradient")]
        public void NoOpDoesNotEnableChannelsOrDiscardRedo(string kind)
        {
            var d = Document(out var l, false);
            d.Fill(l.Id, PaintChannel.Color, new Rgba32(1, 2, 3)); d.Undo();
            var before = DocumentBinary.Write(d); long history = d.HistoryBytes;
            Assert.That(Edit(d, l, kind, SelectionMask.None(d)), Is.False);
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.RedoCount, Is.EqualTo(1));
            Assert.That(d.HistoryBytes, Is.EqualTo(history));
            if (kind == "fill") Assert.That(d.FillMaterial(l.Id, Material, 0), Is.False);
            else { var g = Gradient(); g.Opacity = 0; Assert.That(d.GradientMaterial(l.Id, Material, g), Is.False); }
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
        }

        [TestCase("fill", LayerLocks.Pixels)] [TestCase("fill", LayerLocks.All)]
        [TestCase("gradient", LayerLocks.Pixels)] [TestCase("gradient", LayerLocks.All)]
        public void GroupLocksRefuseBeforeEnablingAnyChannel(string kind, LayerLocks locks)
        {
            var d = Document(out var l, false); var group = d.AddGroup("locked"); d.MoveLayerTo(l.Id, group.Id, 0);
            d.SetLayerLocks(group.Id, locks); d.ClearHistory(); var before = DocumentBinary.Write(d);
            Assert.That(() => Edit(d, l, kind), Throws.TypeOf<LayerLockedException>());
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.CanUndo, Is.False);
        }

        [TestCase("fill")] [TestCase("gradient")]
        public void BadArgumentsLayersAndActiveStrokesRefuseWithoutChanges(string kind)
        {
            var d = Document(out var l, false);
            bool Run(Guid id, IReadOnlyList<ChannelPaint> values, SelectionMask region = null)
                => kind == "fill" ? d.FillMaterial(id, values, .5, region) : d.GradientMaterial(id, values, Gradient(), region);
            var before = DocumentBinary.Write(d);
            Assert.Throws<ArgumentNullException>(() => Run(l.Id, null));
            Assert.Throws<ArgumentException>(() => Run(l.Id, Array.Empty<ChannelPaint>()));
            Assert.Throws<ArgumentException>(() => Run(l.Id, new[] { Material[0], Material[0] }));
            Assert.Throws<ArgumentOutOfRangeException>(() => Run(l.Id, new[] { new ChannelPaint((PaintChannel)99, new Rgba32(1, 2, 3)) }));
            Assert.Throws<ArgumentException>(() => Run(l.Id, Material, SelectionMask.All(new PaintDocument(16, 16, 8))));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
            var fill = d.AddFillLayer("fill"); var adjustment = d.AddAdjustmentLayer("adjustment", AdjustmentSettings.Invert()); var group = d.AddGroup("group");
            foreach (var target in new[] { fill, adjustment, group })
            {
                before = DocumentBinary.Write(d); int steps = d.UndoCount;
                Assert.Throws<InvalidOperationException>(() => Run(target.Id, Material));
                Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); Assert.That(d.UndoCount, Is.EqualTo(steps));
            }
            d.SetCanvasPath(l.Id, new CanvasPath(Guid.NewGuid(), PaintChannel.Color, new PathBrush(), new CanvasPoint[0]));
            before = DocumentBinary.Write(d); Assert.Throws<InvalidOperationException>(() => Run(l.Id, Material));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); d.Rasterize(l.Id);
            using (var stroke = d.BeginStroke(l.Id, PaintChannel.Color, new BrushSettings()))
                Assert.Throws<InvalidOperationException>(() => Run(l.Id, Material));
        }

        [Test] public void InvalidGradientAndOpacityAndTransparencyEraseAreAtomic()
        {
            var d = Document(out var l, false); var before = DocumentBinary.Write(d);
            Assert.Throws<ArgumentNullException>(() => d.GradientMaterial(l.Id, Material, null));
            var g = Gradient(); g.X0 = double.NaN; Assert.Throws<ArgumentOutOfRangeException>(() => d.GradientMaterial(l.Id, Material, g));
            g = Gradient(); g.Shape = (GradientShape)99; Assert.Throws<ArgumentOutOfRangeException>(() => d.GradientMaterial(l.Id, Material, g));
            Assert.Throws<ArgumentOutOfRangeException>(() => d.FillMaterial(l.Id, Material, 1.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => d.FillMaterial(l.Id, Material, double.NaN));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
            d.SetLayerLocks(l.Id, LayerLocks.Transparency); before = DocumentBinary.Write(d);
            Assert.Throws<LayerLockedException>(() => d.FillMaterial(l.Id, Material, erase: true));
            Assert.Throws<LayerLockedException>(() => d.GradientMaterial(l.Id, Material, Gradient(), erase: true));
            Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
        }

        [TestCase("fill")] [TestCase("gradient")]
        public void OneMaterialChannelHasTheSingleChannelBytesAndHistoryCost(string kind)
        {
            var multi = Document(out var ml); var one = DocumentBinary.Read(DocumentBinary.Write(multi)); var ol = one.GetLayer(ml.Id);
            var material = new[] { Material[0] };
            if (kind == "fill") { multi.FillMaterial(ml.Id, material, .73); one.Fill(ol.Id, PaintChannel.Color, material[0].Value, .73); }
            else
            {
                var g = Gradient(); multi.GradientMaterial(ml.Id, material, g);
                g.From = material[0].Value; g.To = Rgba32.Transparent; one.Gradient(ol.Id, PaintChannel.Color, g);
            }
            Assert.That(DocumentBinary.Write(multi), Is.EqualTo(DocumentBinary.Write(one)));
            Assert.That(multi.HistoryBytes, Is.EqualTo(one.HistoryBytes));
            multi.Undo(); one.Undo(); Assert.That(DocumentBinary.Write(multi), Is.EqualTo(DocumentBinary.Write(one)));
            multi.Redo(); one.Redo(); Assert.That(DocumentBinary.Write(multi), Is.EqualTo(DocumentBinary.Write(one)));
        }

        [TestCase(1)] [TestCase(4)]
        public void ParallelismKeepsTheSameBytes(int degree)
        {
            int old = CoreParallelism.MaxDegreeOfParallelism;
            try
            {
                CoreParallelism.MaxDegreeOfParallelism = 1; var d = Document(out var l); Edit(d, l, "gradient");
                var pixels = Material.Select(m => Pixels(d, l, m.Channel)).ToArray();
                CoreParallelism.MaxDegreeOfParallelism = degree; var p = Document(out var pl); Edit(p, pl, "gradient");
                for (int i = 0; i < Material.Length; i++) Assert.That(Pixels(p, pl, Material[i].Channel), Is.EqualTo(pixels[i]));
            }
            finally { CoreParallelism.MaxDegreeOfParallelism = old; }
        }

        [TestCase(false)] [TestCase(true)]
        public void MaskGradientChangesOnlyTheMaskAndUndoesExactly(bool reveal)
        {
            var d = Document(out var l); d.AddLayerMask(l.Id);
            if (reveal) d.FillMask(l.Id);
            d.SetLayerLocks(l.Id, LayerLocks.Pixels | LayerLocks.Transparency);
            d.SetSelection(SelectionMask.Rectangle(d, 0, 0, 20, 29)); d.ClearHistory();
            var before = DocumentBinary.Write(d); var pixels = Material.Select(m => Pixels(d, l, m.Channel)).ToArray();
            var g = Gradient(); g.From = new Rgba32(20, 30, 40); g.To = Rgba32.Transparent;
            Assert.That(d.GradientMask(l.Id, g, reveal: reveal), Is.True);
            for (int i = 0; i < Material.Length; i++) Assert.That(Pixels(d, l, Material[i].Channel), Is.EqualTo(pixels[i]));
            Assert.That(l.Mask.Surface.GetPixel(30, 2).A, Is.EqualTo(reveal ? 255 : 0), "選択の外");
            Assert.That(d.UndoCount, Is.EqualTo(1)); var after = DocumentBinary.Write(d);
            d.Undo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before)); d.Redo(); Assert.That(DocumentBinary.Write(d), Is.EqualTo(after));
            d.SetLayerLocks(l.Id, LayerLocks.All); before = DocumentBinary.Write(d);
            Assert.Throws<LayerLockedException>(() => d.GradientMask(l.Id, g)); Assert.That(DocumentBinary.Write(d), Is.EqualTo(before));
        }
    }
}
