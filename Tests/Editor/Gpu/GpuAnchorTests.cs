using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>Anchor を読む層の表示の合成（TileGpuCompositor）: 入れ子のグループ（通過と分離）の中で、Anchor の下の Height を変える・Undo・
    /// 並べ替えで上下が逆になる・Anchor を消す・読む段のスライダーのドラッグのたびに、表示の Color が全面を新しく合成したものと同じ
    /// （CPU の経路はバイト一致で、下の写しとグループの中の写しを使いながら。GPU の経路は写しを持たない新しい GPU の合成とバイト一致、CPU の
    /// 正本とは GpuTests と同じ許容 1 以内）。表示は Color しか見ていないので、Height の変化は変更の記録の Anchor の閉包だけが伝える。
    /// Anchor を読むのは Core（CPU）で、GPU は読む層の評価したタイルを載せるだけ。</summary>
    public sealed class GpuAnchorTests
    {
        const int W = 640, H = 384, T = 32;
        static Rgba32 Grey(int v, int a = 255) => new Rgba32((byte)v, (byte)v, (byte)v, (byte)a);

        /// <summary>下地、通過のグループ G1（細部の Height に Anchor、その上に色）、分離のグループ G2（色・Anchor を読む塗りつぶし（マスクに
        /// Anchor の Generator とぼかし）・半透明の色）、一番上の色。</summary>
        static PaintDocument Scene(out PaintLayer detail, out AnchorPoint anchor, out PaintLayer reader, out FilterEffect stage, out PaintLayer g1, out PaintLayer g2)
        {
            var d = new PaintDocument(W, H, T);
            var rnd = new System.Random(77);
            var bytes = new byte[T * T * 4];
            PaintLayer Painted(string name, PaintChannel channel, bool opaque)
            {
                var l = d.AddLayer(name);
                for (int ty = 0; ty < H / T; ty++) for (int tx = 0; tx < W / T; tx++)
                    {
                        if ((tx + ty) % 3 == 0 && !opaque) continue;
                        rnd.NextBytes(bytes); if (opaque) for (int k = 3; k < bytes.Length; k += 4) bytes[k] = 255;
                        l.GetChannel(channel).ImportTile(new TileCoord(tx, ty), bytes);
                    }
                return l;
            }
            Painted("Bottom", PaintChannel.Color, true);
            d.AddFillLayer("Base height", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Height, Grey(120) } });
            detail = Painted("Detail", PaintChannel.Height, false);
            var over = Painted("Over", PaintChannel.Color, false); d.SetLayerOpacity(over.Id, .7);
            g1 = d.GroupLayers(new[] { detail.Id, over.Id }, "G1");
            anchor = d.AddAnchor(detail.Id, AnchorPlacement.Layer, "Details");
            var a = Painted("A", PaintChannel.Color, false); d.SetLayerBlendMode(a.Id, LayerBlendMode.Multiply);
            reader = d.AddFillLayer("Wear", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(230, 210, 40, 255) } });
            d.AddLayerMask(reader.Id);
            stage = d.AddFilter(reader.Id, FilterTarget.Mask, FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Anchor).WithLevels(.3, .8, .5).WithAnchor(anchor.Id, PaintChannel.Height, AnchorRead.Value)));
            d.AddFilter(reader.Id, FilterTarget.Mask, FilterSettings.GaussianBlur(6));
            var b = Painted("B", PaintChannel.Color, false); d.SetLayerOpacity(b.Id, .4);
            g2 = d.GroupLayers(new[] { a.Id, reader.Id, b.Id }, "G2"); d.SetLayerBlendMode(g2.Id, LayerBlendMode.Normal);
            Painted("Top", PaintChannel.Color, false);
            d.ClearHistory();
            return d;
        }

        static byte[] Read(TileGpuCompositor c) => c.Texture is RenderTexture ? GpuTests.Read(c.Texture) : GpuTests.ReadCpu(c.Texture);
        /// <summary>The composite of a fresh copy (nothing cached anywhere).</summary>
        static byte[] Reference(PaintDocument d) => DocumentBinary.Read(DocumentBinary.Write(d)).Composite(PaintChannel.Color);

        /// <summary>The edits of both tests, each followed by check(step).</summary>
        static void Edits(PaintDocument d, PaintLayer detail, AnchorPoint anchor, PaintLayer reader, FilterEffect stage, PaintLayer g1, Action<string> check)
        {
            check("first");
            for (int i = 0; i < 3; i++) { d.Fill(detail.Id, PaintChannel.Height, Grey(250, 200), 1, SelectionMask.Ellipse(d, 150 + 30 * i, 120, 20, 15)); check("Height below the anchor " + i); }
            using (var s = d.BeginStroke(detail.Id, PaintChannel.Height, new BrushSettings { Radius = 18, Hardness = .6, Opacity = 1, Color = Grey(10), PressureSize = false, PressureOpacity = false }))
            { s.Add(new BrushSample(400, 200)); s.Add(new BrushSample(480, 260)); s.Commit(); }
            check("stroke below the anchor");
            d.Undo(); check("undo the stroke");
            d.SetLayerVisibility(g1.Id, false); check("the anchor's group hidden (the display changes, the anchor does not)");
            d.Undo(); check("undo");
            for (int i = 0; i < 4; i++)
            {
                var g = stage.Settings.Generator.WithLevels(.2 + .05 * i, .9, .5);
                d.SetFilterSettings(reader.Id, stage.Id, stage.Settings.WithGenerator(g), coalesce: true); check("slider " + i);
            }
            d.EndCoalescing();
            d.MoveLayerTo(detail.Id, Guid.Empty, d.ChildrenOf(Guid.Empty).Count); check("the anchor's layer moved above the reader: passes through");
            Assert.That(d.AnchorIssues().Single().Kind, Is.EqualTo(AnchorIssueKind.NotBelow));
            d.Undo(); check("undo the move");
            d.RemoveAnchor(anchor.Id); check("anchor removed");
            d.Undo(); check("undo the removal");
            d.Fill(detail.Id, PaintChannel.Height, Grey(0), 1, SelectionMask.Rectangle(d, 0, 0, W, 40)); check("a band below the anchor");
        }

        /// <summary>CPU の経路: どの手順の後も CPU の正本とバイト一致。局所の変更は一部のタイルだけを、下の写しやグループの中の写しから描き直す。</summary>
        [Test] public void TheCpuDisplayRedrawsTheReaderExactlyFromItsCopies()
        {
            var d = Scene(out var detail, out var anchor, out var reader, out var stage, out var g1, out _);
            var compositors = new List<TileGpuCompositor> { new TileGpuCompositor(allowGpu: false) { ResidentBudgetBytes = 256L << 20 } };
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null) compositors.Add(new TileGpuCompositor(CompositorBackend.Cpu) { ResidentBudgetBytes = 256L << 20 });
            int reused = 0, partial = 0, total = (W / T) * (H / T);
            try
            {
                Edits(d, detail, anchor, reader, stage, g1, step =>
                {
                    var expected = Reference(d);
                    foreach (var c in compositors)
                    {
                        c.Update(d, PaintChannel.Color);
                        CpuCompositingTests.AssertSameBytes(expected, Read(c), step + " (" + c.Path + ")");
                        reused += c.LastBelowReuseCount + c.LastNestedReuseCount;
                        if (step.StartsWith("Height below") && c.LastUpdatedTileCount < total) partial++;
                    }
                });
            }
            finally { foreach (var c in compositors) c.Dispose(); }
            Assert.That(partial, Is.GreaterThan(0), "a local Height change redraws only the tiles the reader's output can change in");
            Assert.That(reused, Is.GreaterThan(0), "the copies below the first changed item were used");
        }

        /// <summary>GPU の経路: 新しい GPU の合成とバイト一致、CPU の正本と許容 1 以内（CopyTexture の有無の両方）。</summary>
        [TestCase(true)]
        [TestCase(false)]
        [Category("GPU")]
        public void TheGpuDisplayMatchesAFreshCompositeAndTheCpuReference(bool copyTexture)
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            var d = Scene(out var detail, out var anchor, out var reader, out var stage, out var g1, out _);
            var c = new TileGpuCompositor(allowCopyTexture: copyTexture) { ResidentBudgetBytes = 256L << 20 };
            try
            {
                Edits(d, detail, anchor, reader, stage, g1, step =>
                {
                    c.Update(d, PaintChannel.Color);
                    var shown = GpuTests.Read(c.Texture);
                    using (var fresh = new TileGpuCompositor(allowCopyTexture: copyTexture))
                    {
                        var copy = DocumentBinary.Read(DocumentBinary.Write(d));
                        fresh.Update(copy, PaintChannel.Color);
                        CpuCompositingTests.AssertSameBytes(GpuTests.Read(fresh.Texture), shown, step + ": a fresh GPU composite");
                    }
                    GpuTests.AssertMatches(Reference(d), shown, step + ": the CPU reference");
                });
            }
            finally { c.Dispose(); }
        }
    }
}
