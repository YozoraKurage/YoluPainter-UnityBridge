using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed partial class WindowTests
    {
        /// <summary>OpenGL の GUI 台で、描画・Undo・取消・セット切替・サムネイル・プレビュー・窓の終了と再作成を繰り返す。
        /// GUI 台ではシェーダーが使えないことがあるので、画素の GPU 一致は別の batch-gl の試験で確かめる。
        /// Unity の計器の許容幅を調べる試験であり、Windows の D3D11 の検証ではない。</summary>
        [Test] public void OpenGlWindowPaintingSetSwitchAndReopenStayWithinWarmGraphicsMemory()
        {
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore)
                Assert.Ignore("この周回計測は OpenGL 用。Windows の D3D11 の検証ではない。");
            Close(window); window = null;
            long baseline = 0; int textures = 0;
            for (int cycle = 0; cycle < 12; cycle++)
            {
                window = Open();
                window.CreateProject(new NewProjectSettings { Model = null, Resolution = 512, Template = ProjectTemplate.Pbr });
                Invoke(window, "LoadDemoCube");
                var first = window.CurrentTextureSet;
                var second = window.AddTextureSet(-1, "Second"); // デモのキューブのマテリアルは 1 つ: マテリアルに結び付けないセット
                Repaint(window);
                var at = At(window, 120, 120);
                Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
                Assert.That(window.Document.CompositePixel(PaintChannel.Color, 120, 120).A, Is.GreaterThan(0));
                Key(window, KeyCode.Z, EventModifiers.Control);
                Assert.That(window.Document.CompositePixel(PaintChannel.Color, 120, 120).A, Is.Zero);
                Mouse(window, EventType.MouseDown, at); Key(window, KeyCode.Escape);
                Assert.That(window.IsStroking, Is.False);
                Assert.That(window.Document.CompositePixel(PaintChannel.Color, 120, 120).A, Is.Zero);
                Assert.That(window.SwitchTextureSet(first.Id), Is.True); Repaint(window);
                Assert.That(window.SwitchTextureSet(second.Id), Is.True); Repaint(window);
                Close(window); window = null;
                long bytes = Profiler.GetAllocatedMemoryForGraphicsDriver();
                int count = Resources.FindObjectsOfTypeAll<Texture>().Length;
                GpuReadbackMemoryTests.Record("window", cycle, bytes, count);
                if (cycle == 3) { baseline = bytes; textures = count; }
                if (cycle < 4) continue;
                Assert.That(bytes, Is.LessThanOrEqualTo(baseline + (32L << 20)), "暖機後の保持量の増加は 32 MiB 以内");
                Assert.That(count, Is.LessThanOrEqualTo(textures + 2), "窓ごとの所有するテクスチャを取り残さない");
            }
        }
    }
}
