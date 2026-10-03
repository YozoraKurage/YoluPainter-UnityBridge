using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// チャンネルごとの合成モードと不透明度の表示の合成: CPU の表示の経路は CPU の正本とバイトまで一致する。GPU の合成器は、そのチャンネルでの
    /// 値を層の値にした文書の GPU の合成とバイトまで同じで、CPU の正本とは今の許容（GpuTests.MaxByteError = 1）で一致する（グループ・通過・
    /// クリッピング・調整・マスクを重ねた ChannelBlendTests の文書の Height と Normal は、設定の無い文書でも 2 違うので、その 2 つは GPU どうし
    /// だけを比べる）。設定を変えた後の差分の合成（変更の記録と、ブロックの署名に入るチャンネルでの値）、チャンネルの切り替え、取り消しでも、
    /// 差分の結果が作り直しと同じ。
    /// </summary>
    [Category("GPU")]
    public sealed class GpuChannelBlendTests
    {
        const string ShaderName = "Hidden/YoluPainter/TileComposite";

        static byte[] Read(TileGpuCompositor c) => c.Texture is RenderTexture ? GpuTests.Read(c.Texture) : GpuTests.ReadCpu(c.Texture);

        /// <summary>A fresh compositor's full composite of the document in the channel.</summary>
        static byte[] Fresh(PaintDocument doc, PaintChannel channel, CompositorBackend backend)
        { using (var c = new TileGpuCompositor(backend)) { c.Update(doc, channel); return Read(c); } }

        /// <summary>Updates the long-lived compositor (incremental after the first call) and compares it: the CPU path with the CPU reference
        /// byte for byte; the GPU path with a fresh GPU composite byte for byte (the change journal and the block signatures that carry the
        /// channel's values found everything that changed) and, where the stack keeps within it, with the CPU reference within the GPU
        /// tolerance.</summary>
        static void Check(TileGpuCompositor compositor, PaintDocument doc, PaintChannel channel, string context, bool cpu)
        {
            compositor.Update(doc, channel);
            if (cpu) { CpuCompositingTests.AssertSameBytes(doc.Composite(channel), Read(compositor), context); return; }
            CpuCompositingTests.AssertSameBytes(Fresh(doc, channel, CompositorBackend.Gpu), Read(compositor), context + ": incremental = from scratch");
            if (channel == PaintChannel.Color || channel == PaintChannel.Roughness) GpuTests.AssertMatches(doc.Composite(channel), Read(compositor), context);
        }

        static void Exercise(TileGpuCompositor compositor, bool cpu)
        {
            var doc = ChannelBlendTests.Build(true);
            foreach (var c in ChannelBlendTests.Painted) Check(compositor, doc, c, c + " from scratch", cpu);
            // 表示しているチャンネルの設定を変える（差分の合成）
            doc.SetChannelOpacity(new Guid("4b1d0000-0000-4000-8000-000000000003"), PaintChannel.Normal, .35);
            Check(compositor, doc, PaintChannel.Normal, "Normal after its own opacity changed", cpu);
            doc.SetChannelBlendMode(new Guid("4b1d0000-0000-4000-8000-000000000008"), PaintChannel.Normal, LayerBlendMode.Overlay);
            Check(compositor, doc, PaintChannel.Normal, "Normal after a mode changed", cpu);
            // 表示していないチャンネルの設定を変えてから、そのチャンネルへ切り替える
            doc.SetChannelBlend(new Guid("4b1d0000-0000-4000-8000-000000000005"), PaintChannel.Roughness, new ChannelBlend(LayerBlendMode.PassThrough, .5));
            Check(compositor, doc, PaintChannel.Normal, "Normal unchanged by a Roughness setting", cpu);
            Check(compositor, doc, PaintChannel.Roughness, "Roughness with the group passing through at 50 %", cpu);
            doc.Undo();
            Check(compositor, doc, PaintChannel.Roughness, "Roughness after undo", cpu);
            doc.SetChannelBlend(new Guid("4b1d0000-0000-4000-8000-000000000006"), PaintChannel.Roughness, default);
            Check(compositor, doc, PaintChannel.Roughness, "Roughness after a setting was cleared", cpu);
            doc.SetLayerOpacity(new Guid("4b1d0000-0000-4000-8000-000000000005"), .25);
            Check(compositor, doc, PaintChannel.Height, "Height keeps the group's own 50 % when the layer's opacity changes", cpu);
            Check(compositor, doc, PaintChannel.Color, "Color follows the layer's opacity", cpu);
        }

        [Test] public void OnTheGpuEachChannelIsTheCompositeOfTheLayersSetToThatChannelsValues()
        {
            GpuTests.RequireWorkingShader(ShaderName);
            foreach (var c in ChannelBlendTests.Painted)
            {
                // 同じ式に同じ値が渡る: チャンネルごとの設定の文書と、そのチャンネルでの値を層の値にした文書の GPU の合成はバイトまで同じ
                // （Height と Normal はこの重ね方そのものの丸めで CPU と 2 まで違う。設定の無い文書でも同じ 2。2026-10-03 に測った）
                CpuCompositingTests.AssertSameBytes(Fresh(ChannelBlendTests.Build(false, c), c, CompositorBackend.Gpu), Fresh(ChannelBlendTests.Build(true), c, CompositorBackend.Gpu), c.ToString());
            }
        }

        [Test] public void TheGpuCompositorFollowsChangesOfTheSettings()
        {
            GpuTests.RequireWorkingShader(ShaderName);
            using (var compositor = new TileGpuCompositor(allowCopyTexture: true))
            {
                Exercise(compositor, cpu: false);
                Assert.That(compositor.Backend, Does.StartWith("CPU source brush / GPU"), compositor.Backend);
            }
        }

        [Test] public void TheCpuDisplayPathIsByteIdentical()
        {
            using (var compositor = new TileGpuCompositor(CompositorBackend.Cpu)) Exercise(compositor, cpu: true);
        }

        [Test] public void APlainStackWithChannelSettingsNeedsNoCpuTiles()
        {
            GpuTests.RequireWorkingShader(ShaderName);
            var doc = new PaintDocument(48, 48, 16);
            foreach (var (name, alpha) in new[] { ("back", 255), ("a", 200), ("b", 150) })
            {
                var l = doc.AddLayer(name);
                foreach (var c in new[] { PaintChannel.Color, PaintChannel.Roughness })
                {
                    var s = l.GetChannel(c);
                    for (int y = 0; y < 48; y++) for (int x = 0; x < 48; x++) if ((x + y + name.Length) % 3 != 0 || name == "back") s.SetPixel(x, y, new Rgba32((byte)(x * 5), (byte)(y * 5 + (int)c * 40), (byte)(name.Length * 60), (byte)alpha));
                }
            }
            doc.SetChannelBlend(doc.Layers[1].Id, PaintChannel.Roughness, new ChannelBlend(LayerBlendMode.Multiply, .6));
            doc.SetChannelBlend(doc.Layers[2].Id, PaintChannel.Roughness, new ChannelBlend(LayerBlendMode.LinearDodge, null));
            using (var compositor = new TileGpuCompositor())
                foreach (var c in new[] { PaintChannel.Roughness, PaintChannel.Color })
                {
                    compositor.Update(doc, c);
                    GpuTests.AssertMatches(doc.Composite(c), GpuTests.Read(compositor.Texture), c.ToString());
                    Assert.That(compositor.LastCpuTileCount, Is.Zero, c + ": no tile is composited on the CPU");
                }
        }
    }
}
