using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>TileGpuCompositor の差分更新。各段階で表示用テクスチャが CPU 参照合成と一致し、
    /// 差分で済む操作では変わったタイルだけが作り直されることを確かめる。</summary>
    public sealed class CompositorTests
    {
        /// <summary>描画・Undo・別タイル・構造変更・チャンネル切替を順に流し、各段階で read() を参照と比べる。</summary>
        internal static void RunIncrementalScenario(TileGpuCompositor compositor, Func<byte[]> read, Action<byte[], byte[], string> assertMatches)
        {
            var doc = new PaintDocument(80, 48, 16); // 5x3 タイル、上端は部分タイルではないが右端は 80/16=5 ちょうど
            var a = doc.AddLayer("A"); var b = doc.AddLayer("B"); doc.ClearHistory();
            var brush = new BrushSettings { Radius = 3, Hardness = .5, Opacity = .8, Color = new Rgba32(200, 40, 90), PressureSize = false, PressureOpacity = false };
            void Stroke(Guid layer, double x, double y) { using (var s = doc.BeginStroke(layer, PaintChannel.Color, brush)) { s.Add(new BrushSample(x, y)); s.Add(new BrushSample(x + 6, y)); s.Commit(); } }
            void Step(string name, int? maxTiles)
            {
                compositor.Update(doc, PaintChannel.Color);
                assertMatches(doc.Composite(PaintChannel.Color), read(), name);
                if (maxTiles.HasValue) Assert.That(compositor.LastUpdatedTileCount, Is.LessThanOrEqualTo(maxTiles.Value), name + ": tiles recomposited");
            }
            Step("empty", null);
            Stroke(a.Id, 8, 8); Step("first stroke", null);
            Stroke(b.Id, 70, 40); Step("stroke in another tile", 2);
            Step("no change", 0);
            doc.Undo(); Step("undo", 2);
            Stroke(b.Id, 8, 10); Step("overlapping stroke on upper layer", 2);
            doc.SetLayerBlendMode(b.Id, LayerBlendMode.Multiply); Step("blend change", 4);
            doc.SetLayerOpacity(a.Id, .3); Step("opacity change", 4);
            doc.SetLayerVisibility(b.Id, false); Step("hide", 4);
            doc.AddLayerMask(a.Id); Step("add mask", 4);
            using (var s = doc.BeginMaskStroke(a.Id, brush)) { s.Add(new BrushSample(10, 8)); s.Add(new BrushSample(14, 8)); s.Commit(); }
            Step("mask stroke", 2);
            doc.SetLayerMaskInverted(a.Id, true); doc.SetLayerMaskDensity(a.Id, .6); Step("invert and density", 4);
            var fill = doc.AddFillLayer("Fill", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(40, 160, 90, 200) } });
            doc.SetLayerBlendMode(fill.Id, LayerBlendMode.Multiply); Step("fill over everything", null);
            doc.AddLayerMask(fill.Id); doc.SetLayerMaskInverted(fill.Id, true); Step("fill hidden by an inverted mask", null);
            using (var s = doc.BeginMaskStroke(fill.Id, brush)) { s.Add(new BrushSample(60, 20)); s.Commit(); }
            Step("reveal the fill with the mask", 2);
            using (var s = doc.BeginStroke(a.Id, PaintChannel.Color, brush)) { s.Add(new BrushSample(40, 24)); Step("mid-stroke preview", 2); s.Cancel(); }
            Step("after cancel", 2);
            compositor.Update(doc, PaintChannel.Roughness); assertMatches(doc.Composite(PaintChannel.Roughness), read(), "channel switch");
            compositor.Update(doc, PaintChannel.Color); assertMatches(doc.Composite(PaintChannel.Color), read(), "channel switch back");
            var other = new PaintDocument(80, 48, 16); other.AddLayer("other");
            compositor.Update(other, PaintChannel.Color); assertMatches(other.Composite(PaintChannel.Color), read(), "different document of the same size");
        }

        [Test] public void CpuFallbackUpdatesOnlyChangedTilesAndMatchesReference()
        {
            using (var compositor = new TileGpuCompositor(allowGpu: false))
            {
                RunIncrementalScenario(compositor, () => ((Texture2D)compositor.Texture).GetRawTextureData<byte>().ToArray(),
                    (expected, actual, name) => Assert.That(actual, Is.EqualTo(expected), name));
                Assert.That(compositor.Backend, Does.StartWith("CPU composite fallback"));
            }
        }
    }
}
