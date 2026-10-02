using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウ: Normal を使う文書では、Color を描いていても 3D の照明に Normal の出力を渡し、切り替えで外す。</summary>
    public sealed partial class WindowTests
    {
        [Test] public void ThePreviewIsLitWithTheNormalOutputWhileOtherChannelsArePainted()
        {
            var d = window.Document; var layer = d.Layers[d.Layers.Count - 1];
            window.Preview.LoadDemoMesh(); Repaint(window);
            Assert.That(window.PreviewNormalTexture, Is.Null, "no Normal in the document: nothing to light with");
            d.SetChannelEnabled(layer.Id, PaintChannel.Normal, true);
            layer.GetChannel(PaintChannel.Normal).SetPixel(5, 5, new Rgba32(200, 128, 200));
            Repaint(window);
            Assert.That(window.PreviewNormalTexture, Is.Not.Null, "lit with the Normal output while the Color channel is shown");
            window.PreviewNormals = false; Repaint(window);
            Assert.That(window.PreviewNormalTexture, Is.Null);
        }
    }
}
