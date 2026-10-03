using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// レイヤーのパネルの複数選択とロックの見た目: 見出しの 2 行目のロックの切り替え（持っているロック・グループやすべてのロックから
    /// 効いているだけのもの）、行の右端の錠（塗った錠・線の錠・グループからの薄い錠）、選んだ層の色と描く先の帯を、英語と日本語で、
    /// ドックの最小の幅（220）と既定の幅（300）で描いて PNG にする（Logs/YoluPainterSnapshots/layer-select。見て確かめる用）。
    /// 描くだけでは文書も選択も変わらないこと。batch-gl でだけ描く。
    /// </summary>
    public sealed class LayerSelectPanelTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "layer-select"));

        [Test] public void TheLayersPanelWithSelectionAndLocksDrawsInBothLanguagesAtNarrowAndDefaultWidths()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon.");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            Directory.CreateDirectory(Folder);
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                var d = w.Document; var a = d.Layers.Last(); d.SetLayerName(a.Id, "Base colour");
                d.Fill(a.Id, PaintChannel.Color, new Rgba32(180, 120, 90, 255));
                var shade = d.AddLayer("Shading with a long name"); d.SetLayerLocks(shade.Id, LayerLocks.Transparency);
                var detail = d.AddLayer("Detail"); var g = d.GroupLayers(new[] { detail.Id }, "Locked group"); d.SetLayerLocks(g.Id, LayerLocks.Pixels);
                var top = d.AddLayer("Top"); d.SetLayerLocks(top.Id, LayerLocks.All);
                w.SelectLayers(new[] { shade.Id, top.Id }, top.Id);
                long revision = d.Revision; var chosen = w.SelectedLayers.ToArray();
                var draw = typeof(TexturePaintWindow).GetMethod("DrawLayersPanel", BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    foreach (int width in new[] { 220, 300 })
                    {
                        L.OverrideLanguage(language);
                        string path = Path.Combine(Folder, "layers-" + language + "-" + width + ".png");
                        OffscreenGui.RenderToPng(width, 300, () => draw.Invoke(w, new object[] { new Rect(0, 0, width, 300) }), path, PaintTheme.PanelBg);
                        var texture = new Texture2D(2, 2);
                        try
                        {
                            Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, path);
                            int colors = texture.GetPixels32().Select(c => c.r << 16 | c.g << 8 | c.b).Distinct().Take(100).Count();
                            Assert.That(colors, Is.GreaterThan(20), path + ": the panel looks empty");
                        }
                        finally { Object.DestroyImmediate(texture); }
                    }
                Assert.That(d.Revision, Is.EqualTo(revision), "drawing changes nothing");
                Assert.That(w.SelectedLayers, Is.EqualTo(chosen));
            }
            finally
            {
                L.OverrideLanguage(PainterLanguage.English);
                string recovery = w.RecoveryRoot; Object.DestroyImmediate(w);
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
            }
        }
    }
}
