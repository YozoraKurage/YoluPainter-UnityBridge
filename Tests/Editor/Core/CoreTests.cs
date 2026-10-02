using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class CoreTests
    {
        static BrushSettings Opaque(Rgba32 color)
        { return new BrushSettings { Radius = 2, Hardness = 1, Spacing = 0.5, Color = color, PressureSize = false, PressureOpacity = false }; }
        static PaintDocument Empty(int size = 16, int tile = 4)
        { var doc = new PaintDocument(size, size, tile); doc.AddLayer("Paint"); doc.ClearHistory(); return doc; }
        static void Pixel(PaintDocument doc, int x, int y, Rgba32 color, PaintChannel channel = PaintChannel.Color)
        { using (var s = doc.BeginStroke(doc.Layers[0].Id, channel, Opaque(color))) { s.ApplyPixel(x, y, 1); s.Commit(); } }

        [Test] public void Empty4KLayersAllocateNoPixelBuffers()
        {
            var doc = new PaintDocument(4096, 4096, 128);
            for (int i = 0; i < 100; i++) doc.AddLayer("Layer " + i);
            Assert.That(doc.AllocatedBytes, Is.Zero);
            Assert.That(doc.Layers.All(layer => layer.GetChannel(PaintChannel.Color).TileCount == 0));
        }
        [Test] public void SparsePixelAllocatesOnlyItsTileAndEraseReleasesIt()
        {
            var doc = Empty(); Pixel(doc, 5, 9, new Rgba32(255, 0, 0));
            var surface = doc.Layers[0].GetChannel(PaintChannel.Color);
            Assert.That(surface.TileCount, Is.EqualTo(1)); Assert.That(surface.AllocatedBytes, Is.EqualTo(4 * 4 * 4));
            var brush = Opaque(new Rgba32(255, 255, 255)); brush.Erase = true;
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, brush)) { stroke.ApplyPixel(5, 9, 1); stroke.Commit(); }
            Assert.That(surface.TileCount, Is.Zero); Assert.That(doc.Undo(), Is.True);
            Assert.That(surface.GetPixel(5, 9), Is.EqualTo(new Rgba32(255, 0, 0)));
        }
        [Test] public void UniformImportUsesFourBytesAndDetachesCallerBuffer()
        {
            var surface = new SparseTileSurface(8, 8, 4); var bytes = new byte[64];
            for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = 17; bytes[i + 3] = 255; }
            surface.ImportTile(new TileCoord(1, 0), bytes); bytes[0] = 88;
            Assert.That(surface.AllocatedBytes, Is.EqualTo(4)); Assert.That(surface.GetPixel(4, 0).R, Is.EqualTo(17));
            var exported = surface.EnumerateTiles().Single(); exported.Bytes[0] = 99;
            Assert.That(surface.GetPixel(4, 0).R, Is.EqualTo(17));
        }
        [Test] public void TileCoordinateEnumerationIsSortedImmutableSnapshotWithoutPixelBuffers()
        {
            var surface = new SparseTileSurface(16, 16, 4);
            surface.SetPixel(12, 8, new Rgba32(1, 2, 3)); surface.SetPixel(0, 0, new Rgba32(4, 5, 6));
            var snapshot = surface.EnumerateTileCoordinates();
            surface.SetPixel(8, 4, new Rgba32(7, 8, 9)); surface.SetPixel(0, 0, Rgba32.Transparent);
            CollectionAssert.AreEqual(new[] { new TileCoord(0, 0), new TileCoord(3, 2) }, snapshot);
            CollectionAssert.AreEqual(new[] { new TileCoord(2, 1), new TileCoord(3, 2) }, surface.EnumerateTileCoordinates());
            Assert.Throws<NotSupportedException>(() => ((IList<TileCoord>)snapshot)[0] = new TileCoord(99, 99));
        }
        [Test] public void EdgePaddingRejectedWithoutMutation()
        {
            var surface = new SparseTileSurface(5, 5, 4); var bytes = new byte[64]; bytes[7] = 255;
            Assert.Throws<ArgumentException>(() => surface.ImportTile(new TileCoord(1, 1), bytes));
            Assert.That(surface.TileCount, Is.Zero);
        }
        [Test] public void HiddenTransparentRgbSurvivesImportAndUndo()
        {
            var doc = Empty(); var surface = doc.Layers[0].GetChannel(PaintChannel.Color);
            surface.SetPixel(1, 1, new Rgba32(32, 64, 128, 0));
            Pixel(doc, 1, 1, new Rgba32(220, 4, 8)); doc.Undo();
            Assert.That(surface.GetPixel(1, 1), Is.EqualTo(new Rgba32(32, 64, 128, 0)));
        }
        [Test] public void StrokeUndoRedoRestoresExactPixelsAcrossTileBoundaries()
        {
            var doc = Empty(); byte[] before = doc.Composite(PaintChannel.Color);
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, Opaque(new Rgba32(240, 13, 9, 127))))
            { stroke.Add(new BrushSample(2.5, 3.5)); stroke.Add(new BrushSample(12.5, 10.5, 1, 1)); stroke.Commit(); }
            byte[] after = doc.Composite(PaintChannel.Color); Assert.That(after, Is.Not.EqualTo(before));
            Assert.That(doc.UndoCount, Is.EqualTo(1)); doc.Undo(); CollectionAssert.AreEqual(before, doc.Composite(PaintChannel.Color));
            Assert.That(doc.AllocatedBytes, Is.Zero); doc.Redo(); CollectionAssert.AreEqual(after, doc.Composite(PaintChannel.Color));
        }
        [Test] public void CancelAndDisposeRestoreBeforeWithoutHistory()
        {
            var doc = Empty(); Pixel(doc, 3, 3, new Rgba32(5, 19, 82, 79)); doc.ClearHistory();
            byte[] before = doc.Composite(PaintChannel.Color);
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, Opaque(new Rgba32(255, 0, 0))))
                stroke.Add(new BrushSample(3, 3));
            CollectionAssert.AreEqual(before, doc.Composite(PaintChannel.Color));
            Assert.That(doc.HasActiveStroke, Is.False); Assert.That(doc.UndoCount, Is.Zero);
        }
        [Test] public void NoOpStrokeRetainsRedo()
        {
            var doc = Empty(); Pixel(doc, 1, 1, new Rgba32(1, 2, 3)); doc.Undo();
            var brush = Opaque(Rgba32.Transparent);
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, brush))
            { stroke.Add(new BrushSample(1, 1)); Assert.That(stroke.Commit(), Is.False); }
            Assert.That(doc.CanRedo, Is.True);
        }
        [Test] public void NewEditInvalidatesRedo()
        {
            var doc = Empty(); Pixel(doc, 1, 1, new Rgba32(1, 2, 3)); doc.Undo();
            Pixel(doc, 2, 2, new Rgba32(4, 5, 6)); Assert.That(doc.CanRedo, Is.False);
        }
        [Test] public void ChannelsAndEraserAreIsolated()
        {
            var doc = Empty(); Pixel(doc, 3, 3, new Rgba32(10, 20, 30), PaintChannel.Color);
            Pixel(doc, 3, 3, new Rgba32(88, 88, 88), PaintChannel.Roughness);
            var erase = Opaque(new Rgba32(255, 255, 255)); erase.Erase = true;
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, erase)) { stroke.ApplyPixel(3, 3, 1); stroke.Commit(); }
            Assert.That(doc.CompositePixel(PaintChannel.Color, 3, 3).A, Is.Zero);
            Assert.That(doc.CompositePixel(PaintChannel.Roughness, 3, 3).R, Is.EqualTo(88));
        }
        [Test] public void PressureCanIndependentlyControlSizeOpacityAndFlow()
        {
            var doc = Empty(); var brush = Opaque(new Rgba32(255, 0, 0)); brush.PressureOpacity = true; brush.PressureFlow = true;
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, brush)) { stroke.ApplyPixel(4, 4, 1, 0.5); stroke.Commit(); }
            Assert.That(doc.CompositePixel(PaintChannel.Color, 4, 4).A, Is.EqualTo(64));
            Assert.That(doc.CompositePixel(PaintChannel.Color, 4, 4).R, Is.EqualTo(255), "Storage must remain straight-alpha.");
        }
        [Test] public void PressureSizeZeroDoesNotAllocateAndSoftEdgeFallsOff()
        {
            var doc = Empty(); var brush = Opaque(new Rgba32(255, 255, 255)); brush.Radius = 4; brush.Hardness = 0; brush.PressureSize = true;
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, brush)) { stroke.Add(new BrushSample(8.5, 8.5, 0)); stroke.Commit(); }
            Assert.That(doc.AllocatedBytes, Is.Zero);
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, brush)) { stroke.Add(new BrushSample(8.5, 8.5, 1)); stroke.Commit(); }
            Assert.That(doc.CompositePixel(PaintChannel.Color, 8, 8).A, Is.EqualTo(255));
            Assert.That(doc.CompositePixel(PaintChannel.Color, 10, 8).A, Is.EqualTo(128));
            Assert.That(doc.CompositePixel(PaintChannel.Color, 12, 8).A, Is.Zero);
        }
        [Test] public void StrokeSamplingIsStableAcrossCollinearPacketSplitting()
        {
            var a = Empty(64, 8); var b = Empty(64, 8);
            var brush = Opaque(new Rgba32(91, 23, 188, 71)); brush.Radius = 3; brush.Hardness = 0.3; brush.Spacing = 0.2;
            brush.PressureOpacity = true; brush.PressureSize = true;
            using (var stroke = a.BeginStroke(a.Layers[0].Id, PaintChannel.Color, brush))
            { stroke.Add(new BrushSample(4.5, 8.5, 0.2)); stroke.Add(new BrushSample(52.5, 32.5, 1, 1)); stroke.Commit(); }
            using (var stroke = b.BeginStroke(b.Layers[0].Id, PaintChannel.Color, brush))
            {
                for (int i = 0; i <= 12; i++) stroke.Add(new BrushSample(4.5 + 4 * i, 8.5 + 2 * i, 0.2 + 0.8 * i / 12, i / 12.0));
                stroke.Commit();
            }
            CollectionAssert.AreEqual(a.Composite(PaintChannel.Color), b.Composite(PaintChannel.Color));
        }
        [Test] public void DuplicateInputPositionDoesNotDoubleStamp()
        {
            var doc = Empty(); var brush = Opaque(new Rgba32(255, 0, 0, 128));
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, brush))
            { stroke.Add(new BrushSample(4.5, 4.5)); stroke.Add(new BrushSample(4.5, 4.5, 1, 1)); Assert.That(stroke.StampCount, Is.EqualTo(1)); stroke.Commit(); }
            Assert.That(doc.CompositePixel(PaintChannel.Color, 4, 4).A, Is.EqualTo(128));
        }
        [Test] public void SettingsAreFrozenAndActiveTransactionsRejectStructuralMutation()
        {
            var doc = Empty(); var brush = Opaque(new Rgba32(255, 0, 0));
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, brush))
            {
                brush.Color = new Rgba32(0, 0, 255); stroke.ApplyPixel(1, 1, 1);
                Assert.Throws<InvalidOperationException>(() => doc.AddLayer("No"));
                Assert.Throws<InvalidOperationException>(() => doc.Undo());
                Assert.Throws<InvalidOperationException>(() => doc.Layers[0].GetChannel(PaintChannel.Color).SetPixel(2, 2, new Rgba32(255, 255, 255)));
                stroke.Commit();
            }
            Assert.That(doc.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(255, 0, 0)));
        }
        [Test] public void OutOfOrderInputRejectedWithoutPainting()
        {
            var doc = Empty();
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, Opaque(new Rgba32(3, 4, 5))))
            { stroke.Add(new BrushSample(1, 1, 1, 10)); Assert.Throws<ArgumentException>(() => stroke.Add(new BrushSample(9, 9, 1, 9))); }
            Assert.That(doc.AllocatedBytes, Is.Zero);
        }
        [Test] public void UndoBudgetNotifiesAndNeverExceedsPayloadBudget()
        {
            var doc = Empty(); doc.UndoBudgetBytes = 150; int notifications = 0;
            doc.HistoryTrimming += bytes => { Assert.That(bytes, Is.GreaterThan(0)); notifications++; };
            Pixel(doc, 0, 0, new Rgba32(255, 0, 0)); Pixel(doc, 8, 8, new Rgba32(0, 255, 0));
            Assert.That(doc.HistoryBytes, Is.LessThanOrEqualTo(150)); Assert.That(notifications, Is.EqualTo(1));
            Assert.That(doc.UndoCount, Is.EqualTo(1)); doc.Undo();
            Assert.That(doc.CompositePixel(PaintChannel.Color, 0, 0).A, Is.EqualTo(255));
            Assert.That(doc.CompositePixel(PaintChannel.Color, 8, 8).A, Is.Zero);
        }
        [Test] public void TheNewestUndoStepsAreKeptOverBudgetLikeGimp()
        {
            var doc = Empty(); doc.UndoBudgetBytes = 1; doc.MinimumUndoSteps = 2; long dropped = 0; int notifications = 0;
            doc.HistoryTrimming += bytes => { dropped += bytes; notifications++; };
            Pixel(doc, 0, 0, new Rgba32(255, 0, 0));
            Pixel(doc, 8, 8, new Rgba32(0, 255, 0));
            Assert.That(doc.UndoCount, Is.EqualTo(2)); Assert.That(notifications, Is.Zero, "nothing droppable: no notification");
            Pixel(doc, 4, 4, new Rgba32(0, 0, 255));
            Assert.That(doc.UndoCount, Is.EqualTo(2), "the oldest step is dropped"); Assert.That(notifications, Is.EqualTo(1)); Assert.That(dropped, Is.GreaterThan(0));
            doc.Undo(); doc.Undo();
            Assert.That(doc.CompositePixel(PaintChannel.Color, 4, 4).A, Is.Zero); Assert.That(doc.CompositePixel(PaintChannel.Color, 8, 8).A, Is.Zero);
            Assert.That(doc.CompositePixel(PaintChannel.Color, 0, 0).A, Is.EqualTo(255), "the dropped step stays applied");
            Assert.That(doc.Undo(), Is.False);
            doc.Redo(); doc.Redo();
            doc.MinimumUndoSteps = 0;
            Assert.That(doc.UndoCount, Is.Zero, "a strict budget drops the rest"); Assert.That(doc.HistoryBytes, Is.Zero);
            Assert.That(() => doc.MinimumUndoSteps = -1, Throws.InstanceOf<ArgumentOutOfRangeException>());
        }
        [Test] public void OverBudgetSingleStrokeStillCommitsAndWarns()
        {
            var doc = Empty(); doc.UndoBudgetBytes = 1; bool warning = false; doc.HistoryTrimming += n => warning = true;
            Pixel(doc, 1, 1, new Rgba32(1, 2, 3)); Assert.That(warning, Is.True);
            Assert.That(doc.HistoryBytes, Is.Zero); Assert.That(doc.CompositePixel(PaintChannel.Color, 1, 1).A, Is.EqualTo(255));
        }
        [Test] public void LayerOrderVisibilityOpacityAndStructureUndoAreConsistent()
        {
            var doc = Empty(); var bottom = doc.Layers[0]; bottom.GetChannel(PaintChannel.Color).SetPixel(1, 1, new Rgba32(255, 0, 0));
            var top = doc.AddLayer("Blue"); top.GetChannel(PaintChannel.Color).SetPixel(1, 1, new Rgba32(0, 0, 255));
            doc.SetLayerOpacity(top.Id, 0.5); Assert.That(doc.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(128, 0, 128)));
            doc.SetLayerVisibility(top.Id, false); Assert.That(doc.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(255, 0, 0)));
            doc.Undo(); doc.MoveLayer(bottom.Id, 1); Assert.That(doc.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(255, 0, 0)));
            doc.Undo(); Assert.That(doc.Layers[1].Id, Is.EqualTo(top.Id));
            doc.RemoveLayer(top.Id); doc.Undo(); Assert.That(doc.Layers[1].Id, Is.EqualTo(top.Id));
        }
        [Test] public void BlendReferenceHandlesPartialAlphaAndTransparentBackground()
        {
            var source = new Rgba32(128, 64, 32, 128);
            Assert.That(CpuCompositor.Blend(Rgba32.Transparent, source, 1, LayerBlendMode.Multiply), Is.EqualTo(source));
            Assert.That(CpuCompositor.Blend(new Rgba32(255, 0, 0), new Rgba32(0, 0, 255, 128)), Is.EqualTo(new Rgba32(127, 0, 128)));
            Assert.That(CpuCompositor.Blend(new Rgba32(128, 128, 128), new Rgba32(128, 128, 128), 1, LayerBlendMode.Multiply), Is.EqualTo(new Rgba32(64, 64, 64)));
            Assert.That(CpuCompositor.Blend(new Rgba32(128, 128, 128), new Rgba32(128, 128, 128), 1, LayerBlendMode.Screen), Is.EqualTo(new Rgba32(192, 192, 192)));
        }
        [Test] public void DisabledChannelRetainsPixelsButDoesNotComposite()
        {
            var doc = Empty(); Pixel(doc, 1, 1, new Rgba32(55, 55, 55), PaintChannel.Height); var layer = doc.Layers[0];
            doc.SetChannelEnabled(layer.Id, PaintChannel.Height, false);
            Assert.That(doc.CompositePixel(PaintChannel.Height, 1, 1).A, Is.Zero); Assert.That(layer.GetChannel(PaintChannel.Height).GetPixel(1, 1).R, Is.EqualTo(55));
            Assert.Throws<InvalidOperationException>(() => doc.BeginStroke(layer.Id, PaintChannel.Height, Opaque(new Rgba32(2, 2, 2))));
            doc.Undo(); Assert.That(doc.CompositePixel(PaintChannel.Height, 1, 1).R, Is.EqualTo(55));
        }

        [Test] public void SourceBudgetRefusalCancelsWholeStrokeWithoutPartialPixels()
        {
            var doc = Empty(); doc.SourceBudgetBytes = 64;
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, Opaque(new Rgba32(255, 0, 0))))
            {
                stroke.ApplyPixel(1, 1, 1);
                Assert.Throws<InvalidOperationException>(() => stroke.ApplyPixel(8, 8, 1));
                Assert.That(stroke.IsFinished, Is.True);
            }
            Assert.That(doc.AllocatedBytes, Is.Zero); Assert.That(doc.HasActiveStroke, Is.False); Assert.That(doc.UndoCount, Is.Zero);
        }
        [Test] public void RollbackBudgetRefusalCancelsAndRestoresExistingTiles()
        {
            var doc = Empty(); Pixel(doc, 1, 1, new Rgba32(5, 6, 7)); doc.ClearHistory();
            // 1 タイル分だけ入る予算: 記録 64 + 元のタイル 64 + ストローク中の濃さのバッファ 4x4x4 = 64。
            byte[] before = doc.Composite(PaintChannel.Color); doc.ActiveStrokeBudgetBytes = 192;
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, Opaque(new Rgba32(255, 0, 0))))
            {
                stroke.ApplyPixel(1, 1, 1);
                Assert.Throws<InvalidOperationException>(() => stroke.ApplyPixel(8, 8, 1));
            }
            CollectionAssert.AreEqual(before, doc.Composite(PaintChannel.Color)); Assert.That(doc.UndoCount, Is.Zero);
        }
        [Test] public void UniformTileExpansionPreflightsBudgetWithoutMutatingSource()
        {
            var doc = Empty(); doc.SourceBudgetBytes = 4; var surface = doc.Layers[0].GetChannel(PaintChannel.Color);
            var bytes = new byte[64]; for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = 99; bytes[i + 3] = 255; }
            surface.ImportTile(new TileCoord(0, 0), bytes);
            Assert.Throws<InvalidOperationException>(() => surface.SetPixel(1, 1, new Rgba32(0, 255, 0)));
            Assert.That(surface.AllocatedBytes, Is.EqualTo(4)); Assert.That(surface.GetPixel(1, 1), Is.EqualTo(new Rgba32(99, 0, 0)));
        }
        [Test] public void ReducedSourceBudgetRejectsUndoAtomicallyUntilBudgetIsRestored()
        {
            var doc = Empty(); Pixel(doc, 1, 1, new Rgba32(10, 20, 30));
            var eraser = Opaque(new Rgba32(255, 255, 255)); eraser.Erase = true;
            using (var stroke = doc.BeginStroke(doc.Layers[0].Id, PaintChannel.Color, eraser)) { stroke.ApplyPixel(1, 1, 1); stroke.Commit(); }
            doc.SourceBudgetBytes = 0; int historyCount = doc.UndoCount;
            Assert.Throws<InvalidOperationException>(() => doc.Undo()); Assert.That(doc.AllocatedBytes, Is.Zero); Assert.That(doc.UndoCount, Is.EqualTo(historyCount));
            doc.SourceBudgetBytes = 64; Assert.That(doc.Undo(), Is.True); Assert.That(doc.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(10, 20, 30)));
        }
        [Test] public void SourceBudgetIsAlsoEnforcedByLayerRestore()
        {
            var doc = Empty(); Pixel(doc, 1, 1, new Rgba32(10, 20, 30)); Guid id = doc.Layers[0].Id;
            doc.RemoveLayer(id); doc.SourceBudgetBytes = 0;
            Assert.Throws<InvalidOperationException>(() => doc.Undo()); Assert.That(doc.Layers.Count, Is.Zero);
            doc.SourceBudgetBytes = 64; Assert.That(doc.Undo(), Is.True); Assert.That(doc.Layers[0].Id, Is.EqualTo(id));
        }
        [Test] public void HistoryObserverExceptionCannotInterruptCommittedPixelEdit()
        {
            var doc = Empty(); doc.UndoBudgetBytes = 0;
            doc.HistoryTrimming += n => { throw new Exception("UI observer failed"); };
            Pixel(doc, 1, 1, new Rgba32(21, 22, 23));
            Assert.That(doc.CompositePixel(PaintChannel.Color, 1, 1), Is.EqualTo(new Rgba32(21, 22, 23))); Assert.That(doc.HasActiveStroke, Is.False);
        }

        static DependencyNode Node(Guid set, int layer, int effect, GraphValueType type = GraphValueType.Scalar, PaintChannel? channel = null)
        { return new DependencyNode(Guid.NewGuid(), set, GraphNodeKind.Filter, type, layer, effect, new[] { new GraphInput("input", type, channel) }, channel); }
        [Test] public void GraphRejectsCyclesTypeMismatchesAndLaterLayerSources()
        {
            Guid set = Guid.NewGuid(); var graph = new DependencyGraph(); var a = Node(set, 0, 0); var b = Node(set, 0, 1); var color = Node(set, 0, 2, GraphValueType.Color);
            graph.AddNode(a); graph.AddNode(b); graph.AddNode(color); graph.Connect(a.Id, b.Id, "input"); string reason;
            Assert.That(graph.TryConnect(b.Id, a.Id, "input", out reason), Is.False); Assert.That(reason, Does.Contain("cycle"));
            Assert.That(graph.TryConnect(b.Id, color.Id, "input", out reason), Is.False); Assert.That(reason, Does.Contain("type"));
            var early = Node(set, 0, 0); var late = Node(set, 3, 0); graph.AddNode(early); graph.AddNode(late);
            Assert.That(graph.TryConnect(late.Id, early.Id, "input", out reason), Is.False); Assert.That(reason, Does.Contain("lower layer"));
            Assert.That(graph.Connections.Count, Is.EqualTo(1));
        }
        [Test] public void GraphRejectsCrossSetAndSemanticMismatchAndReferencedDeletion()
        {
            var graph = new DependencyGraph(); Guid set = Guid.NewGuid();
            var a = Node(set, 0, 0, GraphValueType.Scalar, PaintChannel.Height);
            var b = Node(set, 1, 0, GraphValueType.Scalar, PaintChannel.Roughness);
            var other = Node(Guid.NewGuid(), 2, 0, GraphValueType.Scalar, PaintChannel.Height);
            graph.AddNode(a); graph.AddNode(b); graph.AddNode(other); string reason;
            Assert.That(graph.TryConnect(a.Id, b.Id, "input", out reason), Is.False); Assert.That(reason, Does.Contain("semantic"));
            Assert.That(graph.TryConnect(a.Id, other.Id, "input", out reason), Is.False); Assert.That(reason, Does.Contain("texture-set"));
            var c = Node(set, 2, 0, GraphValueType.Scalar, PaintChannel.Height); graph.AddNode(c); graph.Connect(a.Id, c.Id, "input");
            Assert.Throws<InvalidOperationException>(() => graph.RemoveNode(a.Id));
        }
        [Test] public void RejectedGraphReorderIsAtomicAndInvalidationReachesConsumers()
        {
            var graph = new DependencyGraph(); Guid set = Guid.NewGuid(); var a = Node(set, 0, 0); var b = Node(set, 1, 0); var c = Node(set, 2, 0);
            graph.AddNode(a); graph.AddNode(b); graph.AddNode(c); graph.Connect(a.Id, b.Id, "input"); graph.Connect(b.Id, c.Id, "input"); string reason;
            Assert.That(graph.TryMoveNode(a.Id, 3, 0, out reason), Is.False); Assert.That(a.LayerOrder, Is.Zero);
            var orders = new Dictionary<Guid, NodeOrder> { { a.Id, new NodeOrder(1, 0) }, { b.Id, new NodeOrder(0, 0) } };
            Assert.That(graph.TrySetOrders(orders, out reason), Is.False); Assert.That(a.LayerOrder, Is.Zero); Assert.That(b.LayerOrder, Is.EqualTo(1));
            long ar = a.Revision, br = b.Revision, cr = c.Revision;
            CollectionAssert.AreEqual(new[] { a.Id, b.Id, c.Id }, graph.Invalidate(a.Id));
            Assert.That(a.Revision, Is.EqualTo(ar + 1)); Assert.That(b.Revision, Is.EqualTo(br + 1)); Assert.That(c.Revision, Is.EqualTo(cr + 1));
        }
    }
}
