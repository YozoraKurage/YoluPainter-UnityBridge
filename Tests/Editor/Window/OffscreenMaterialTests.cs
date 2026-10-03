using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>マテリアルで塗る（ブラシの欄の「マテリアル」の節）と、チャンネルごとの合成モード（レイヤーのパネルの上の行の切り替えと、
    /// プロパティのレイヤーの節の一覧）を、英語と日本語でオフスクリーンに描く（batch-gl。Logs/YoluPainterSnapshots/material-*.png を目で見る）。</summary>
    public sealed class OffscreenMaterialTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots"));

        [Test] public void TheMaterialBrushAndPerChannelBlendingDrawInBothLanguages()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Preview.LoadDemoMesh();
                var d = w.Document; var layer = d.GetLayer(w.SelectedLayer);
                d.SetChannelBlend(layer.Id, PaintChannel.Roughness, new ChannelBlend(LayerBlendMode.Multiply, .6));
                d.SetChannelBlend(layer.Id, PaintChannel.Normal, new ChannelBlend(LayerBlendMode.Overlay, null));
                d.ClearHistory();
                w.Tool = TexturePaintWindow.PaintTool.Brush;
                var b = w.Brush; b.material = true; b.materialChannels = (1 << 6) - 1; b.materialEmission = new Color(1, .6f, .1f, 1); w.Brush = b;
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    foreach (var channel in new[] { PaintChannel.Roughness, PaintChannel.Color })
                    {
                        w.Channel = channel;
                        string name = "material-" + language + "-" + channel;
                        string path = Path.Combine(Folder, name + ".png");
                        OffscreenGui.RenderWindow(w, 1400, 1000, path); // 1 回目で部品の位置を覚え、プロパティの欄をマテリアルの節へ送って描き直す
                        w.ScrollPropertiesTo("material.toggle");
                        OffscreenGui.RenderWindow(w, 1400, 1000, path);
                        var texture = new Texture2D(2, 2);
                        try
                        {
                            Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, name);
                            int colors = texture.GetPixels32().Select(c => c.r << 16 | c.g << 8 | c.b).Distinct().Take(200).Count();
                            Assert.That(colors, Is.GreaterThan(50), name + ": the window looks empty");
                        }
                        finally { Object.DestroyImmediate(texture); }
                        Assert.That(w.ToolControlScreenRects.ContainsKey("material.toggle"), Is.True, name + ": the Material section was drawn");
                        Assert.That(w.ToolControlScreenRects.Keys.Count(k => k.StartsWith("material.value.")), Is.EqualTo(6), name + ": a value row per channel");
                        Assert.That(w.LayerPanelScreenRects.ContainsKey("channelBlend"), Is.True, name + ": the per-channel switch in the Layers panel");
                    }
                }
            }
            finally { Object.DestroyImmediate(w); L.OverrideLanguage(PainterLanguage.English); }
        }
    }
}
