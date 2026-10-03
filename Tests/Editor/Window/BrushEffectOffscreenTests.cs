using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class BrushEffectOffscreenTests
    {
        [Test] public void BrushEffectPanelsDrawInEnglishAndJapaneseWithoutCutLabels()
        {
            if (!Application.isBatchMode) Assert.Ignore("欄の見た目は batch-gl のオフスクリーンで確認する");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("グラフィックスデバイスなし");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.View = TexturePaintWindow.ViewMode.Canvas;
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                foreach (var tool in new[] { TexturePaintWindow.PaintTool.Blur, TexturePaintWindow.PaintTool.Smudge, TexturePaintWindow.PaintTool.Clone })
                {
                    L.OverrideLanguage(language); w.Tool = tool;
                    PaintGui.ShortenedTexts = 0;
                    var path = Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "BrushEffects", tool + "-" + language + ".png"));
                    OffscreenGui.RenderWindow(w, 1200, 800, path);
                    Assert.That(File.Exists(path), Is.True); Assert.That(new FileInfo(path).Length, Is.GreaterThan(10000));
                    Assert.That(PaintGui.ShortenedTexts, Is.Zero, tool + " " + language);
                    var panel = Path.Combine(Path.GetDirectoryName(path), "panel-" + tool + "-" + language + ".png");
                    OffscreenGui.RenderToPng(310, 300, () => {
                        var rows = new UiRows(new Rect(0, 0, 310, 300), 0);
                        typeof(TexturePaintWindow).GetMethod("BrushEffectSection", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(w, new object[] { rows });
                    }, panel, PaintTheme.PanelBg);
                    Assert.That(PaintGui.ShortenedTexts, Is.Zero, "効果の欄 " + tool + " " + language);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(w); L.OverrideLanguage(PainterLanguage.English); }
        }
    }
}
