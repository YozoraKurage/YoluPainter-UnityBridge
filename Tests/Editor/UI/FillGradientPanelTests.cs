using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class FillGradientPanelTests
    {
        [TearDown] public void ResetLanguage() => L.OverrideLanguage(PainterLanguage.English);
        [Test] public void FillGradientPropertiesDrawBothLanguagesAndOccludedHandlesStayVisible()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen panel validation uses batch-gl.");
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Preview.LoadDemoMesh(); var d = w.Document;
                var l = d.AddFillLayer("Gradient", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(255, 255, 255, 255) } });
                w.SelectedLayer = l.Id; w.View = TexturePaintWindow.ViewMode.Split;
                w.SetSectionOpen("layer", true); w.SetSectionOpen("mask", false); w.SetSectionOpen("filters", false);
                foreach (var channel in new[] { PaintChannel.Color, PaintChannel.Roughness })
                {
                    w.Channel = channel; w.EnableFillGradient(true);
                    var g = l.FillGradients[channel]; d.SetFillGradient(l.Id, channel, g.WithVolume(ShapeVolume.Default.WithSize(.4, .4, .4)));
                    // ボックスの面も中心もキューブの中。画面の上に描いたハンドルは遮蔽されない
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese }) foreach (var width in new[] { 980, 1600 })
                    {
                        L.OverrideLanguage(language); var before = d.Revision; var undo = d.UndoCount; PaintGui.ShortenedTexts = 0;
                        string path = Path.Combine("Logs", "YoluPainterSnapshots", "FillGradient", channel + "-" + language + "-" + width + ".png");
                        OffscreenGui.RenderWindow(w, width, width == 980 ? 640 : 950, path);
                        // 点とカーブまで実際のスクロール表示にも出し、区画全体の画像も残す
                        float stopY = w.LayerControlPanelRects["gradient.stops"].y;
                        typeof(TexturePaintWindow).GetField("propertiesScroll", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, new Vector2(0, Math.Max(0, stopY - 12)));
                        OffscreenGui.RenderWindow(w, width, width == 980 ? 640 : 950, path);
                        float used = 0;
                        OffscreenGui.RenderToPng(300, 1100, () => used = w.DrawFillGradientSectionOnly(new Rect(0, 0, 300, 1100)), path.Replace(".png", "-section.png"), PaintTheme.PanelBg);
                        Assert.That(used, Is.InRange(400, 1100), "all gradient properties fit in the captured section");
                        Assert.That(PaintGui.ShortenedTexts, Is.Zero); Assert.That((d.Revision, d.UndoCount), Is.EqualTo((before, undo)));
                        Assert.That(w.LayerControlPanelRects.Keys, Has.Member("gradient.stops").And.Member("gradient.curve").And.Member("fill.gradient.edit"));
                        var at = w.ShapeHandleGui(ShapeHandle.MoveFree); Assert.That(at.HasValue, Is.True);
                        Assert.That(w.ShapeHandleAt(at.Value), Is.EqualTo(ShapeHandle.MoveFree));
                        var image = new Texture2D(2, 2);
                        try
                        {
                            image.LoadImage(File.ReadAllBytes(path)); int bright = 0; var center = at.Value; int height = image.height;
                            for (int y = -8; y <= 8; y++) for (int x = -8; x <= 8; x++)
                            { var c = image.GetPixel((int)center.x + x, height - 1 - (int)center.y - y); if (c.r > .85 && c.g > .85 && c.b > .85) bright++; }
                            Assert.That(bright, Is.GreaterThan(8), "the centre square is visible through the opaque model");
                        }
                        finally { Object.DestroyImmediate(image); }
                    }
                }
            }
            finally { Object.DestroyImmediate(w); }
        }
    }
}
