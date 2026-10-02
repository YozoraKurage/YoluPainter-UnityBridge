using System;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 文書の編集のまとめ（PaintDocument.Batch: 中の変更が 1 回の Undo になり、失敗したら履歴ごと元に戻る）と、画像で層の画素を置き換える
    /// ReplacePixels（選択範囲の中は画像のとおり、透明な画素の RGB も、部分的な選択はプリマルチプライドで補間、型と大きさの拒否）。
    /// プラグインの API（IPainterSession）の変更はこの 2 つの上にある。
    /// </summary>
    public sealed class DocumentBatchTests
    {
        static PaintDocument NewDocument() => new PaintDocument(256, 256, 128, 64L << 20);
        static byte[] Image(int w, int h, Func<int, int, Rgba32> pixel)
        {
            var bytes = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var c = pixel(x, y); int o = (y * w + x) * 4; bytes[o] = c.R; bytes[o + 1] = c.G; bytes[o + 2] = c.B; bytes[o + 3] = c.A; }
            return bytes;
        }

        [Test] public void ABatchIsOneUndoStepThatRedoesAsAWhole()
        {
            var doc = NewDocument(); var first = doc.AddLayer("First"); doc.ClearHistory();
            Guid added = Guid.Empty;
            doc.Batch(() =>
            {
                var layer = doc.AddLayer("Added"); added = layer.Id;
                doc.SetChannelEnabled(layer.Id, PaintChannel.Color, true);
                doc.Fill(layer.Id, PaintChannel.Color, new Rgba32(255, 0, 0, 255));
                doc.SetLayerOpacity(layer.Id, .5);
            });
            Assert.That(doc.Layers.Count, Is.EqualTo(2));
            Assert.That(doc.Undo(), Is.True);
            Assert.That(doc.Layers.Select(l => l.Id), Is.EqualTo(new[] { first.Id }), "one undo reverts the whole batch");
            Assert.That(doc.CanUndo, Is.False);
            Assert.That(doc.Redo(), Is.True);
            Assert.That(doc.GetLayer(added).Opacity, Is.EqualTo(.5));
            Assert.That(doc.GetLayer(added).GetPixel(PaintChannel.Color, 10, 10), Is.EqualTo(new Rgba32(255, 0, 0, 255)));
        }

        [Test] public void AFailingBatchLeavesTheDocumentAndHistoryAsBefore()
        {
            var doc = NewDocument(); var layer = doc.AddLayer("Layer"); doc.SetChannelEnabled(layer.Id, PaintChannel.Color, true);
            doc.Fill(layer.Id, PaintChannel.Color, new Rgba32(0, 0, 255, 255));
            doc.SetLayerOpacity(layer.Id, .7); doc.Undo(); // 1 段のやり直しを残す
            long revision = doc.Revision, history = doc.HistoryBytes; var before = doc.Composite(PaintChannel.Color);
            Assert.That(() => doc.Batch(() =>
            {
                doc.Fill(layer.Id, PaintChannel.Color, new Rgba32(0, 255, 0, 255));
                doc.AddLayer("Doomed");
                throw new InvalidOperationException("boom");
            }), Throws.InvalidOperationException.With.Message.EqualTo("boom"));
            Assert.That(doc.Layers.Count, Is.EqualTo(1));
            Assert.That(doc.Composite(PaintChannel.Color), Is.EqualTo(before), "the pixels are back");
            Assert.That(doc.HistoryBytes, Is.EqualTo(history));
            Assert.That(doc.Revision, Is.GreaterThan(revision), "the revision moves on so displays redraw");
            Assert.That(doc.CanRedo, Is.True, "the redo step that a batch would have dropped is back");
            Assert.That(doc.Redo(), Is.True); Assert.That(doc.GetLayer(layer.Id).Opacity, Is.EqualTo(.7));
        }

        [Test] public void BatchesRefuseNestingStrokesAndUndo()
        {
            var doc = NewDocument(); var layer = doc.AddLayer("Layer"); doc.SetChannelEnabled(layer.Id, PaintChannel.Color, true);
            long history = doc.HistoryBytes, revision = doc.Revision;
            Assert.That(() => doc.Batch(() => doc.Batch(() => { })), Throws.InvalidOperationException);
            Assert.That(() => doc.Batch(() => doc.BeginStroke(layer.Id, PaintChannel.Color, new BrushSettings())), Throws.InvalidOperationException);
            Assert.That(() => doc.Batch(() => doc.Undo()), Throws.InvalidOperationException);
            Assert.That(doc.IsBatching, Is.False);
            Assert.That(doc.HistoryBytes, Is.EqualTo(history), "the refused batches left no history");
            Assert.That(doc.Revision, Is.EqualTo(revision)); Assert.That(doc.CanRedo, Is.False);
        }

        [Test] public void ReplacePixelsCopiesTheImageExactlyInsideTheSelection()
        {
            var doc = NewDocument(); var layer = doc.AddLayer("Layer"); doc.SetChannelEnabled(layer.Id, PaintChannel.Color, true);
            var image = Image(256, 256, (x, y) => x < 128 ? new Rgba32((byte)x, (byte)y, 7, 255) : new Rgba32(9, 8, 7, 0)); // 右半分は透明だが RGB がある
            Assert.That(doc.ReplacePixels(layer.Id, PaintChannel.Color, image), Is.True);
            Assert.That(doc.GetLayer(layer.Id).GetPixel(PaintChannel.Color, 100, 50), Is.EqualTo(new Rgba32(100, 50, 7, 255)));
            Assert.That(doc.GetLayer(layer.Id).GetPixel(PaintChannel.Color, 200, 50), Is.EqualTo(new Rgba32(9, 8, 7, 0)), "transparent pixels keep their RGB");
            Assert.That(doc.ReplacePixels(layer.Id, PaintChannel.Color, image), Is.False, "nothing changes the second time");
            doc.Undo();
            Assert.That(doc.GetLayer(layer.Id).GetPixel(PaintChannel.Color, 100, 50).A, Is.EqualTo(0));

            // 選択範囲: 中は置き換え、外はそのまま。選択を無視することもできる
            doc.SetSelection(SelectionMask.Rectangle(doc, 0, 0, 64, 64));
            var red = Image(256, 256, (x, y) => new Rgba32(255, 0, 0, 255));
            doc.ReplacePixels(layer.Id, PaintChannel.Color, red);
            Assert.That(doc.GetLayer(layer.Id).GetPixel(PaintChannel.Color, 10, 10), Is.EqualTo(new Rgba32(255, 0, 0, 255)));
            Assert.That(doc.GetLayer(layer.Id).GetPixel(PaintChannel.Color, 100, 100).A, Is.EqualTo(0));
            doc.ReplacePixels(layer.Id, PaintChannel.Color, red, withinSelection: false);
            Assert.That(doc.GetLayer(layer.Id).GetPixel(PaintChannel.Color, 100, 100), Is.EqualTo(new Rgba32(255, 0, 0, 255)));
        }

        /// <summary>どこも 128/255 だけ選ばれた選択範囲（選択範囲の保存形式 YLSL で組み立てる）。</summary>
        static SelectionMask HalfSelected(PaintDocument doc)
        {
            using (var stream = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(stream))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("YLSL")); w.Write(1); w.Write(doc.Width); w.Write(doc.Height); w.Write(doc.TileSize); w.Write(4);
                for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++) { w.Write(x); w.Write(y); w.Write(Enumerable.Repeat((byte)128, doc.TileSize * doc.TileSize).ToArray()); }
                w.Flush();
                return Yozolab.YoluPainter.Core.Persistence.SelectionBinary.Read(stream.ToArray(), doc);
            }
        }

        [Test] public void APartialSelectionInterpolatesInPremultipliedSpace()
        {
            var doc = NewDocument(); var layer = doc.AddLayer("Layer"); doc.SetChannelEnabled(layer.Id, PaintChannel.Color, true);
            doc.Fill(layer.Id, PaintChannel.Color, new Rgba32(0, 0, 255, 255));
            doc.SetSelection(HalfSelected(doc));
            doc.ReplacePixels(layer.Id, PaintChannel.Color, Image(256, 256, (x, y) => new Rgba32(255, 0, 0, 0)));
            var p = doc.GetLayer(layer.Id).GetPixel(PaintChannel.Color, 5, 5);
            Assert.That(p.A, Is.InRange(126, 129), "half way to transparent");
            Assert.That(p.B, Is.EqualTo(255), "a transparent source adds no colour: the remaining coverage stays blue");
            Assert.That(p.R, Is.EqualTo(0));
        }

        [Test] public void ReplacePixelsRefusesWrongSizesAndLayersWithoutPixels()
        {
            var doc = NewDocument(); var layer = doc.AddLayer("Layer"); doc.SetChannelEnabled(layer.Id, PaintChannel.Color, true);
            var group = doc.AddGroup("Group"); var fill = doc.AddFillLayer("Fill");
            Assert.That(() => doc.ReplacePixels(layer.Id, PaintChannel.Color, new byte[16]), Throws.ArgumentException.With.Message.Contains("256 × 256"));
            Assert.That(() => doc.ReplacePixels(layer.Id, PaintChannel.Color, null), Throws.ArgumentNullException);
            var image = new byte[256 * 256 * 4];
            Assert.That(() => doc.ReplacePixels(group.Id, PaintChannel.Color, image), Throws.InvalidOperationException);
            Assert.That(() => doc.ReplacePixels(fill.Id, PaintChannel.Color, image), Throws.InvalidOperationException);
            Assert.That(() => doc.ReplacePixels(layer.Id, PaintChannel.Roughness, image), Throws.InvalidOperationException.With.Message.Contains("Enable"));
            var small = new PaintDocument(256, 256, 128, 64L << 20) { SourceBudgetBytes = 1024 };
            var l2 = small.AddLayer("L"); small.SetChannelEnabled(l2.Id, PaintChannel.Color, true); long revision = small.Revision;
            Assert.That(() => small.ReplacePixels(l2.Id, PaintChannel.Color, Image(256, 256, (x, y) => new Rgba32((byte)x, (byte)y, 3, 255))), Throws.InvalidOperationException);
            Assert.That(small.GetLayer(l2.Id).GetPixel(PaintChannel.Color, 0, 0).A, Is.EqualTo(0), "over budget: nothing changed");
            Assert.That(small.Revision, Is.EqualTo(revision));
        }
    }
}
