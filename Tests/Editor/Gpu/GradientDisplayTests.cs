using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    [Category("GPU")]
    public sealed class GradientDisplayTests
    {
        static PaintDocument Scene(int size, out PaintLayer layer)
        {
            var d = new PaintDocument(size, size, 64);
            layer = d.AddFillLayer("Gradient", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(255, 255, 255, 255) } });
            d.GeneratorInputs = new TestGeneratorInputs().Put(TestMeshMaps.Make(MeshMapKind.Position, size, size, (x, y, c) => c == 0 ? (x + .5) / size : c == 1 ? (y + .5) / size : .5));
            d.SetFillGradient(layer.Id, PaintChannel.Color, GradientRampTests.Plane(GradientRamp.Default)); d.ClearHistory(); return d;
        }
        static void Move(PaintDocument d, PaintLayer layer, double x)
        {
            if (layer.HasFillGradient(PaintChannel.Color))
            {
                var g = layer.FillGradients[PaintChannel.Color];
                d.SetFillGradient(layer.Id, PaintChannel.Color, g.WithVolume(g.Volume.WithCenter(.5, x, .5)), true);
            }
            else
            {
                var effect = layer.Mask.Filters.Single(); var g = effect.Settings.Generator;
                d.SetFilterSettings(layer.Id, effect.Id, effect.Settings.WithGenerator(g.WithVolume(g.Volume.WithCenter(.5, x, .5))), true);
            }
        }
        static void Drain(TileGpuCompositor c, PaintDocument d, CompositeSchedule schedule)
        {
            for (int n = 0; n < 10000 && c.HasPendingWork; n++) c.Update(d, PaintChannel.Color, schedule);
            Assert.That(c.HasPendingWork, Is.False);
        }
        static void Check(TileGpuCompositor c, PaintDocument d)
        {
            byte[] shown = c.Texture is RenderTexture ? GpuTests.Read(c.Texture) : GpuTests.ReadCpu(c.Texture);
            if (c.Path == TileGpuCompositor.CompositePath.Gpu) GpuTests.AssertMatches(d.Composite(PaintChannel.Color), shown, "gradient after draining");
            else CpuCompositingTests.AssertSameBytes(d.Composite(PaintChannel.Color), shown, "gradient after draining");
        }
        [TearDown] public void ResetThreads() => CoreParallelism.MaxDegreeOfParallelism = 0;
        [TestCase(false), TestCase(true)]
        public void RepeatedGradientDragsLeaveEvaluationForTheNextUpdateAndFinishExactly(bool gpu)
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            CoreParallelism.MaxDegreeOfParallelism = 4;
            var d = Scene(1024, out var layer); d.FilterBlockPixels = 128;
            using (var c = new TileGpuCompositor(gpu ? CompositorBackend.Gpu : CompositorBackend.Cpu))
            {
                c.Update(d, PaintChannel.Color);
                var drag = new CompositeSchedule { BudgetMilliseconds = 0, PreviewStep = 4 };
                for (int k = 0; k < 3; k++)
                {
                    Move(d, layer, .4 + .03 * k); long before = d.FilterEvaluatedBlocks;
                    c.Update(d, PaintChannel.Color, drag);
                    Assert.That(c.HasPendingWork, Is.True); Assert.That(c.LastProcessedBlocks.Count, Is.EqualTo(1));
                    Assert.That(d.FilterEvaluatedBlocks - before, Is.LessThan(64), "a reduced preview must not evaluate the entire gradient at once");
                }
                d.EndCoalescing(); Drain(c, d, new CompositeSchedule { BudgetMilliseconds = 0 }); Check(c, d);
                d.Undo(); DrainAfterUndo();
                void DrainAfterUndo() { c.Update(d, PaintChannel.Color, drag); Drain(c, d, drag); Check(c, d); }
            }
        }
        [TestCase(false), TestCase(true)]
        public void NestedAndClippedGradientSourcesKeepThePreviewEvaluationSliced(bool mask)
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite"); CoreParallelism.MaxDegreeOfParallelism = 4;
            var d = Scene(1024, out var layer); d.FilterBlockPixels = 128;
            var baseLayer = d.AddFillLayer("Base", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(30, 80, 120, 255) } });
            d.MoveLayer(layer.Id, 1); d.SetLayerClipping(layer.Id, true); d.GroupLayers(new[] { baseLayer.Id, layer.Id }, "Nested");
            if (mask) { var g = layer.FillGradients[PaintChannel.Color].WithRamp(null); d.SetFillGradient(layer.Id, PaintChannel.Color, null); d.AddLayerMask(layer.Id); d.AddFilter(layer.Id, FilterTarget.Mask, FilterSettings.FromGenerator(g)); }
            using (var c = new TileGpuCompositor(CompositorBackend.Cpu))
            {
                c.Update(d, PaintChannel.Color); var drag = new CompositeSchedule { BudgetMilliseconds = 0, PreviewStep = 4 };
                for (int k = 0; k < 3; k++)
                {
                    Move(d, layer, .4 + .03 * k); long before = d.FilterEvaluatedBlocks; c.Update(d, PaintChannel.Color, drag);
                    Assert.That(c.HasPendingWork, Is.True); Assert.That(c.LastPreviewed, Is.False);
                    Assert.That(d.FilterEvaluatedBlocks - before, Is.LessThan(64));
                }
                Drain(c, d, drag); Check(c, d);
            }
        }
        /// <summary>実時計で4Kの移動に相当するパラメーター変更を3回。速度の閾値は置かず、評価の区切りと最終画素を検証する。
        /// マップ作成とGPU読み戻しは計時から除く。通常の全件では重い試験として除外、絞り込みで実行する。</summary>
        [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true)]
        public void FourKGradientDragReportsTheTimeAndEvaluatedBlocks(bool gpu, bool mask)
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            var d = Scene(4096, out var layer);
            if (mask) { var g = layer.FillGradients[PaintChannel.Color].WithRamp(null); d.SetFillGradient(layer.Id, PaintChannel.Color, null); d.AddLayerMask(layer.Id); d.AddFilter(layer.Id, FilterTarget.Mask, FilterSettings.FromGenerator(g)); }
            var lines = new List<string>();
            void Report(string line) { lines.Add(line); TestContext.WriteLine(line); }
            using (var c = new TileGpuCompositor(gpu ? CompositorBackend.Gpu : CompositorBackend.Cpu))
            {
                var timer = Stopwatch.StartNew(); c.Update(d, PaintChannel.Color); timer.Stop();
                Report($"4K {c.Path} source={(mask ? "mask" : "direct")} device={SystemInfo.graphicsDeviceName} workers={CoreParallelism.Degree} initial-full={timer.Elapsed.TotalMilliseconds:F2}ms");
                var drag = new CompositeSchedule { BudgetMilliseconds = 8, PreviewStep = 4 };
                for (int k = 0; k < 3; k++)
                {
                    Move(d, layer, .4 + .03 * k); long before = d.FilterEvaluatedBlocks;
                    timer.Restart(); c.Update(d, PaintChannel.Color, drag); timer.Stop();
                    Assert.That(c.LastPreviewed, Is.False, "a preview must not force full-resolution evaluation of every source tile");
                    Assert.That(d.FilterEvaluatedBlocks - before, Is.LessThan(256));
                    Report($"4K drag {k} update={timer.Elapsed.TotalMilliseconds:F2}ms evaluated={d.FilterEvaluatedBlocks - before}/256 displayed={c.LastProcessedBlocks.Count}/64 pending={c.PendingBlockCount} preview={c.LastPreviewed} preview-ms={c.LastPreviewMilliseconds:F2}");
                }
                d.EndCoalescing(); timer.Restart(); int frames = 0; double largest = 0;
                while (c.HasPendingWork && frames++ < 1000)
                {
                    var t = Stopwatch.StartNew(); c.Update(d, PaintChannel.Color, new CompositeSchedule { BudgetMilliseconds = 8 });
                    largest = Math.Max(largest, t.Elapsed.TotalMilliseconds);
                }
                Report($"4K release drain={timer.Elapsed.TotalMilliseconds:F2}ms updates={frames} largest={largest:F2}ms");
                Assert.That(c.HasPendingWork, Is.False); Check(c, d);
                Directory.CreateDirectory("Logs/YoluPainterMeasurements");
                File.WriteAllLines($"Logs/YoluPainterMeasurements/Gradient-{gpu}-{mask}.txt", lines);
            }
        }
    }
}
