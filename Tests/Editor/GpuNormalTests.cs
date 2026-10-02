using System;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>Normal チャンネルの GPU 合成（TileComposite.shader のベクトルの経路）と Normal の出力パス（NormalOutput.shader）を、CPU の
    /// 正本（CpuCompositor → NormalMaps）と突き合わせる。合成 1 段と、正確な合成を渡した出力パスの許容は RGBA8 の丸め 1 段。出力パスの比較では
    /// Normal の合成を CPU の正本から作った RenderTexture で渡し、合成の誤差と出力の式の誤差を分けて確かめる（Height は 1 枚の層なので GPU の
    /// 合成も正確）。合成から出力まで GPU で続けた表示は許容 2（<see cref="AssertWithin"/>）。</summary>
    [Category("GPU")]
    public sealed class GpuNormalTests
    {
        [SetUp] public void RequireGpu()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/NormalOutput");
        }

        static byte V(int i) => (byte)(((i % 16) + 16) % 16 * 17);
        static void Paint(PaintDocument d, PaintLayer layer, PaintChannel channel, Func<int, int, Rgba32> value)
        {
            d.SetChannelEnabled(layer.Id, channel, true); var s = layer.GetChannel(channel);
            for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++) s.SetPixel(x, y, value(x, y));
        }
        /// <summary>法線らしい値（B は 128 以上）。アルファは 0 か 96 以上（小さなアルファどうしの重なりは丸めの検査にならない）。</summary>
        static Rgba32 NormalAt(int x, int y, int seed)
        {
            bool hole = (x / 5 + y / 3 + seed) % 7 == 0;
            return new Rgba32(V(x / 3 + seed), V(y / 2 + seed * 3), (byte)(128 + V((x + y) / 4 + seed) / 2), hole ? (byte)0 : (byte)(seed == 0 ? 255 : 96 + (x * 7 + y * 3 + seed * 13) % 160));
        }

        /// <summary>合成と出力を GPU で続けた結果の比較用。Normal の合成の 1 段の丸めの差（許容 1）が、強く傾いた下地との RNM で 2 つの
        /// 成分へ回り込むと 2 になる（実測: 4096² で 67,108,864 バイト中 950 バイトが違い、最大 2。正確な合成を渡した出力パスだけなら最大 1）。</summary>
        static void AssertWithin(byte[] expected, byte[] actual, int tolerance, string context)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), context);
            int worst = 0, at = -1;
            for (int i = 0; i < expected.Length; i++) { int e = Math.Abs(expected[i] - actual[i]); if (e > worst) { worst = e; at = i; } }
            Assert.That(worst, Is.LessThanOrEqualTo(tolerance), $"{context}: max byte error {worst} at pixel {at / 4} channel {at % 4} on {SystemInfo.graphicsDeviceType}");
        }

        static RenderTexture Upload(byte[] rgba, int w, int h)
        {
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Bilinear };
            rt.Create();
            try { texture.LoadRawTextureData(rgba); texture.Apply(false, false); Graphics.Blit(texture, rt); }
            finally { Object.DestroyImmediate(texture); }
            return rt;
        }

        [Test] public void NormalLayersCompositeAsVectorsOnTheGpu()
        {
            // 40 は 16 の倍数ではないので部分タイルも通る。置き換え・Overlay（RNM）・Multiply（置き換えとして）・マスク・クリッピング・
            // 通過グループのフェード・分離グループを 1 枚に重ねる。
            var d = new PaintDocument(40, 40, 16);
            var group = d.AddGroup("Pass"); d.SetLayerOpacity(group.Id, .6);
            var a = d.AddLayer("A"); d.MoveLayerTo(a.Id, group.Id, 0); Paint(d, a, PaintChannel.Normal, (x, y) => NormalAt(x, y, 0));
            var b = d.AddLayer("B"); Paint(d, b, PaintChannel.Normal, (x, y) => x > 8 && y < 30 ? NormalAt(x, y, 1) : Rgba32.Transparent);
            d.SetLayerBlendMode(b.Id, LayerBlendMode.Overlay); d.SetLayerOpacity(b.Id, .7);
            var clip = d.AddLayer("Clip"); Paint(d, clip, PaintChannel.Normal, (x, y) => NormalAt(x, y, 2)); d.SetLayerClipping(clip.Id, true); d.SetLayerBlendMode(clip.Id, LayerBlendMode.Overlay);
            var c = d.AddLayer("C"); Paint(d, c, PaintChannel.Normal, (x, y) => x < 24 ? NormalAt(x, y, 3) : Rgba32.Transparent); d.SetLayerBlendMode(c.Id, LayerBlendMode.Multiply); d.SetLayerOpacity(c.Id, .8);
            var mask = d.AddLayerMask(c.Id); for (int y = 0; y < 40; y += 3) for (int x = 0; x < 40; x++) mask.Surface.SetPixel(x, y, new Rgba32(0, 0, 0, (byte)(x * 6)));
            var iso = d.AddGroup("Isolated"); d.SetLayerBlendMode(iso.Id, LayerBlendMode.Overlay);
            var e = d.AddLayer("E"); d.MoveLayerTo(e.Id, iso.Id, 0); Paint(d, e, PaintChannel.Normal, (x, y) => (x + y) % 4 == 0 ? NormalAt(x, y, 4) : Rgba32.Transparent);
            using (var compositor = new TileGpuCompositor())
            {
                compositor.Update(d, PaintChannel.Normal);
                Assert.That(compositor.Backend, Does.StartWith("CPU source brush / GPU"), compositor.Backend);
                GpuTests.AssertMatches(d.Composite(PaintChannel.Normal), GpuTests.Read(compositor.Texture), "Normal composite");
                // 同じ合成器で Color に切り替えると色の式に戻る（シェーダーの切り替えが残らない）
                Paint(d, a, PaintChannel.Color, (x, y) => NormalAt(x, y, 0)); Paint(d, b, PaintChannel.Color, (x, y) => NormalAt(x, y, 1));
                compositor.Update(d, PaintChannel.Color);
                GpuTests.AssertMatches(d.Composite(PaintChannel.Color), GpuTests.Read(compositor.Texture), "Color after Normal");
                compositor.Update(d, PaintChannel.Normal);
                GpuTests.AssertMatches(d.Composite(PaintChannel.Normal), GpuTests.Read(compositor.Texture), "Normal again");
            }
        }

        /// <summary>すべての合成モードを、重ね・クリッピングの経路で 1 回ずつ（正確な 8 bit の入力に 1 回だけ）。</summary>
        [Test] public void EveryBlendModeOnTheNormalChannelMatchesTheCpu()
        {
            foreach (LayerBlendMode mode in Enum.GetValues(typeof(LayerBlendMode)))
                foreach (bool clipped in new[] { false, true })
                {
                    if (mode == LayerBlendMode.PassThrough) continue;
                    var d = new PaintDocument(32, 32, 16);
                    var below = d.AddLayer("Below"); Paint(d, below, PaintChannel.Normal, (x, y) => NormalAt(x, y, 5));
                    var over = d.AddLayer("Over"); Paint(d, over, PaintChannel.Normal, (x, y) => NormalAt(x, y, 6));
                    d.SetLayerBlendMode(over.Id, mode); d.SetLayerOpacity(over.Id, .75); d.SetLayerClipping(over.Id, clipped);
                    using (var compositor = new TileGpuCompositor())
                    {
                        compositor.Update(d, PaintChannel.Normal);
                        GpuTests.AssertMatches(d.Composite(PaintChannel.Normal), GpuTests.Read(compositor.Texture), mode + (clipped ? " clipped" : " stacked"));
                    }
                }
        }

        static PaintDocument OutputScene(int w, int h)
        {
            var d = new PaintDocument(w, h, 16);
            var height = d.AddLayer("Height");
            Paint(d, height, PaintChannel.Height, (x, y) => new Rgba32((byte)((x * x + 3 * y) % 256), 0, 0, (byte)(y < 5 ? 0 : 128 + (x * 5 + y) % 128)));
            var n1 = d.AddLayer("N1"); Paint(d, n1, PaintChannel.Normal, (x, y) => NormalAt(x, y, 7));
            var n2 = d.AddLayer("N2"); Paint(d, n2, PaintChannel.Normal, (x, y) => x < w / 2 ? NormalAt(x, y, 8) : Rgba32.Transparent);
            d.SetLayerBlendMode(n2.Id, LayerBlendMode.Overlay);
            return d;
        }

        [Test] public void NormalOutputPassMatchesTheCpuOutputForEverySetting()
        {
            const int W = 48, H = 40;
            var d = OutputScene(W, H);
            var normal = Upload(d.Composite(PaintChannel.Normal), W, H);
            try
            {
                using (var view = new NormalOutputView())
                {
                    view.Update(d, normal);
                    Assert.That(view.UsedGpu, Is.True, view.Backend); Assert.That(view.HeightCompositor, Is.Null, "no Height compositor without derivation");
                    GpuTests.AssertMatches(NormalMaps.Output(d), GpuTests.Read(view.Texture), "derivation off");
                    foreach (var edges in new[] { HeightEdgeMode.Clamp, HeightEdgeMode.Wrap })
                        foreach (double strength in new[] { 0, 3.5, 40, -12 })
                        {
                            d.SetNormalSettings(new NormalSettings(true, strength, edges, NormalYDirection.OpenGL));
                            view.Update(d, normal);
                            Assert.That(view.UsedGpu, Is.True, view.Backend);
                            GpuTests.AssertMatches(NormalMaps.Output(d), GpuTests.Read(view.Texture), edges + " strength " + strength);
                        }
                    Assert.That(view.HeightCompositor.ResidentBytes, Is.Zero, "the Height compositor keeps no GPU copies");
                    Assert.That(normal.filterMode, Is.EqualTo(FilterMode.Bilinear), "the input's filter mode is restored");
                    // Undo で設定が戻れば出力も戻る。無効にすれば Height の合成器を放す
                    d.Undo(); view.Update(d, normal);
                    GpuTests.AssertMatches(NormalMaps.Output(d), GpuTests.Read(view.Texture), "after undo");
                    d.SetNormalSettings(d.NormalSettings.WithDerive(false)); view.Update(d, normal);
                    Assert.That(view.HeightCompositor, Is.Null);
                    GpuTests.AssertMatches(NormalMaps.Output(d), GpuTests.Read(view.Texture), "switched off");
                }
            }
            finally { normal.Release(); Object.DestroyImmediate(normal); }
        }

        [Test] public void TheWholeGpuPathMatchesTheCpuOutputAndTheCpuFallbackIsExact()
        {
            const int W = 600, H = 520; // 作業ブロック 512 を越えるので、ブロックの境目も通る
            var d = OutputScene(W, H); d.SetNormalSettings(new NormalSettings(true, 6, HeightEdgeMode.Wrap, NormalYDirection.DirectX));
            using (var compositor = new TileGpuCompositor())
            using (var view = new NormalOutputView())
            using (var cpu = new NormalOutputView(allowGpu: false))
            {
                compositor.Update(d, PaintChannel.Normal); view.Update(d, compositor.Texture);
                Assert.That(view.UsedGpu, Is.True, view.Backend);
                var expected = NormalMaps.Output(d);
                AssertWithin(expected, GpuTests.Read(view.Texture), 2, "GPU composite + GPU output");
                cpu.Update(d, compositor.Texture);
                Assert.That(cpu.UsedGpu, Is.False); Assert.That(GpuTests.ReadCpu(cpu.Texture), Is.EqualTo(expected), "the CPU fallback is the reference itself (OpenGL, not the file direction)");
                // Height を描き足すと Height の合成器が変わった所だけ合成し直し、出力も追いつく
                d.GetLayer(d.Layers[0].Id).GetChannel(PaintChannel.Height).SetPixel(300, 300, new Rgba32(255, 0, 0));
                compositor.Update(d, PaintChannel.Normal); view.Update(d, compositor.Texture);
                Assert.That(view.HeightCompositor.LastUpdatedTileCount, Is.EqualTo(1), "only the changed Height tile");
                AssertWithin(NormalMaps.Output(d), GpuTests.Read(view.Texture), 2, "after a Height edit");
            }
        }
    }
}
