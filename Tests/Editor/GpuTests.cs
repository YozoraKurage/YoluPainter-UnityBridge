using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>実 GPU での描画結果を CPU の正本と突き合わせる。グラフィックスデバイスが無い実行
    /// (-nographics) ではスキップする。誤差の許容は RGBA8 の丸め 1 段ぶん。</summary>
    [Category("GPU")]
    public sealed class GpuTests
    {
        const int MaxByteError = 1;

        internal static Shader RequireWorkingShader(string name)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("No graphics device (-nographics). Run with a GPU, e.g. test-daemon.sh start --batch-gl.");
            // 組み込みシェーダーまで壊れているのはエディタ側の問題で、パッケージの不具合ではない。
            if (ShaderUtil.ShaderHasError(Shader.Find("Hidden/BlitCopy")))
                Assert.Ignore("Built-in shaders fail to compile in this Editor (devcontainer GUI mode). Use test-daemon.sh start --batch-gl.");
            var shader = Shader.Find(name);
            Assert.That(shader, Is.Not.Null, name);
            Assert.That(ShaderHealth.IsUsable(shader), Is.True,
                name + ": " + string.Join(" | ", ShaderUtil.GetShaderMessages(shader).Select(m => m.message)));
            return shader;
        }

        internal static byte[] Read(Texture texture)
        {
            var rt = texture as RenderTexture;
            Assert.That(rt, Is.Not.Null, "GPU compositor must expose its RenderTexture, not the CPU fallback.");
            var previous = RenderTexture.active; var readback = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = rt; readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); readback.Apply();
                return readback.GetRawTextureData<byte>().ToArray();
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(readback); }
        }

        internal static void AssertMatches(byte[] expected, byte[] actual, string context)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), context);
            int worst = 0, at = -1;
            for (int i = 0; i < expected.Length; i++)
            {
                int error = Math.Abs(expected[i] - actual[i]);
                if (error > worst) { worst = error; at = i; }
            }
            Assert.That(worst, Is.LessThanOrEqualTo(MaxByteError),
                $"{context}: max byte error {worst} at pixel {at / 4} channel {at % 4} (expected {expected[Math.Max(at, 0)]}, got {actual[Math.Max(at, 0)]}) on {SystemInfo.graphicsDeviceType}");
        }

        [Test] public void OrderedBrushMatchesCpuSourceDab()
        {
            var result = GpuBrushProbe.Measure(RequireWorkingShader("Hidden/YoluPainter/OrderedBrush"));
            Assert.That(result.MaxByteError, Is.LessThanOrEqualTo(MaxByteError), $"mean {result.MeanByteError:F6}");
        }

        [Test] public void TileCompositorMatchesCpuReferenceAcrossBlendModesAndPartialTiles()
        {
            RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            // 40 は 16 の倍数ではないので、右端と上端の部分タイルも通る。
            var doc = new PaintDocument(40, 40, 16);
            var baseLayer = doc.AddLayer("Base"); var multiply = doc.AddLayer("Multiply"); var screen = doc.AddLayer("Screen");
            var hidden = doc.AddLayer("Hidden"); var disabled = doc.AddLayer("Disabled channel");
            for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++)
            {
                if ((x + y) % 3 != 0) baseLayer.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32((byte)(x * 6), (byte)(y * 6), 128, (byte)(255 - x * 3)));
                if (x > 8 && y < 30) multiply.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32(200, (byte)(x * 5), 40, (byte)(y * 8)));
                if (x < 24) screen.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32(30, 90, (byte)(y * 6), 160));
                hidden.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32(255, 0, 0, 255));
                disabled.GetChannel(PaintChannel.Color).SetPixel(x, y, new Rgba32(0, 255, 0, 255));
            }
            // 透明画素の RGB は合成に効いてはいけない。
            screen.GetChannel(PaintChannel.Color).SetPixel(5, 5, new Rgba32(255, 255, 255, 0));
            doc.SetLayerBlendMode(multiply.Id, LayerBlendMode.Multiply); doc.SetLayerOpacity(multiply.Id, .6);
            doc.SetLayerBlendMode(screen.Id, LayerBlendMode.Screen); doc.SetLayerOpacity(screen.Id, .8);
            doc.SetLayerVisibility(hidden.Id, false);
            doc.SetChannelEnabled(disabled.Id, PaintChannel.Color, false);

            using (var compositor = new TileGpuCompositor())
            {
                compositor.Update(doc, PaintChannel.Color);
                Assert.That(compositor.Backend, Does.StartWith("CPU source brush / GPU"), compositor.Backend);
                AssertMatches(doc.Composite(PaintChannel.Color), Read(compositor.Texture), "composite");
            }
        }

        [Test] public void TileCompositorIncrementalUpdatesMatchCpuReference()
        {
            RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            using (var compositor = new TileGpuCompositor())
            {
                CompositorTests.RunIncrementalScenario(compositor, () => Read(compositor.Texture), AssertMatches);
                Assert.That(compositor.Backend, Does.StartWith("CPU source brush / GPU"), compositor.Backend);
            }
        }

        [Test] public void TileCompositorClearsTilesThatBecameEmpty()
        {
            RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            var doc = new PaintDocument(32, 32, 16); var layer = doc.AddLayer("A"); doc.ClearHistory();
            using (var stroke = doc.BeginStroke(layer.Id, PaintChannel.Color, new BrushSettings { Radius = 3, Hardness = 1, Color = new Rgba32(10, 200, 30), PressureSize = false, PressureOpacity = false }))
            { stroke.Add(new BrushSample(24, 24)); stroke.Commit(); }
            using (var compositor = new TileGpuCompositor())
            {
                compositor.Update(doc, PaintChannel.Color);
                AssertMatches(doc.Composite(PaintChannel.Color), Read(compositor.Texture), "after stroke");
                Assert.That(doc.Undo(), Is.True);
                compositor.Update(doc, PaintChannel.Color);
                var cleared = Read(compositor.Texture);
                Assert.That(cleared.All(b => b == 0), Is.True, "A tile emptied by Undo must be recomposited to transparent.");
            }
        }
    }
}
