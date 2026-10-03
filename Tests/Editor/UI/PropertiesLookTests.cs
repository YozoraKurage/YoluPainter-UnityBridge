using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// プロパティの欄の見た目（オフスクリーンの描画、batch-gl）: 英語と日本語、ドックの幅 220・300・420 で、どの文脈も例外なく描け、大見出しは
    /// 全幅の濃い帯、小見出しは帯でなく一段下、中身はラベルの列をそろえて大見出しの文字の位置まで字下げする。既定の幅（300）と広い幅（420）では
    /// UI の文字を詰めない（220 は描けることだけ）。PNG はテストプロジェクトの Logs/YoluPainterSnapshots/Properties に残す（見て確かめる用）。
    /// </summary>
    public sealed class PropertiesLookTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "Properties"));
        [TearDown] public void BackToEnglish() => L.OverrideLanguage(PainterLanguage.English);

        [Test] public void EveryContextDrawsAtEveryDockWidthWithBandsAndIndents()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Preview.LoadDemoMesh();
                var d = w.Document; var paint = d.Layers[0]; d.AddLayerMask(paint.Id);
                var blur = d.AddFilter(paint.Id, FilterTarget.Content, FilterSettings.GaussianBlur(4), new[] { PaintChannel.Color });
                var fill = d.AddFillLayer("Tint", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(200, 120, 80) } });
                var levels = d.AddAdjustmentLayer("Levels", AdjustmentSettings.Levels());
                d.ClearHistory();
                foreach (var key in new[] { "brush-jitter", "brush-dual" }) w.SetSectionOpen(key, true); // 小見出しの中身も
                var contexts = new (string name, System.Action setup, PropertyContext context)[]
                {
                    ("brush", () => { w.SelectTool(TexturePaintWindow.PaintTool.Brush); w.SelectedLayer = paint.Id; w.EditMask = false; w.SetPropertyTab(PropertyContext.Brush, TexturePaintWindow.TabBrush); }, PropertyContext.Brush),
                    ("alpha", () => w.SetPropertyTab(PropertyContext.Brush, TexturePaintWindow.TabAlpha), PropertyContext.Brush),
                    ("stencil", () => w.SetPropertyTab(PropertyContext.Brush, TexturePaintWindow.TabStencil), PropertyContext.Brush),
                    ("material", () => w.SetPropertyTab(PropertyContext.Brush, TexturePaintWindow.TabMaterial), PropertyContext.Brush),
                    ("mask", () => { w.EditMask = true; w.SetPropertyTab(PropertyContext.Brush, TexturePaintWindow.TabMask); }, PropertyContext.Brush),
                    ("filter", () => { w.EditMask = false; w.SelectedFilter = blur.Id; }, PropertyContext.Effect),
                    ("fill", () => { w.SelectedLayer = fill.Id; }, PropertyContext.FillLayer),
                    ("adjustment", () => { w.SelectedLayer = levels.Id; }, PropertyContext.Adjustment),
                    ("selection", () => { w.SelectTool(TexturePaintWindow.PaintTool.SelectRectangle); }, PropertyContext.Selection),
                    ("eyedropper", () => { w.SelectTool(TexturePaintWindow.PaintTool.Eyedropper); }, PropertyContext.Eyedropper),
                };
                int drawn = 0;
                foreach (var (name, setup, context) in contexts)
                {
                    setup();
                    Assert.That(w.PropertyContextNow(), Is.EqualTo(context), name);
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        foreach (int dock in new[] { 220, 300, 420 })
                        {
                            int width = dock - 1 - 8; // 枠とスクロールの印
                            string file = name + "-" + (language == PainterLanguage.English ? "en" : "ja") + "-" + dock;
                            PaintGui.ShortenedTexts = 0; float used = 0; const int height = 1100;
                            string path = Path.Combine(Folder, file + ".png");
                            OffscreenGui.RenderToPng(width, height, () => used = w.DrawPropertiesOnly(new Rect(0, 0, width, height)), path, PaintTheme.PanelBg);
                            if (dock >= 300) Assert.That(PaintGui.ShortenedTexts, Is.Zero, file + ": a UI text did not fit and was shortened with …");
                            Assert.That(used, Is.GreaterThan(context == PropertyContext.Eyedropper ? 20 : 60).And.LessThan(height), file);
                            CheckBandsAndIndents(w, path, width, height, file);
                            drawn++;
                        }
                    }
                }
                Assert.That(drawn, Is.EqualTo(contexts.Length * 2 * 3));
            }
            finally { Object.DestroyImmediate(w); }
        }

        /// <summary>大見出しは左端から右端まで帯の色、小見出しの左端は欄の地の色（帯ではない）で一段下、中身の部品は字下げした列から始まる。</summary>
        static void CheckBandsAndIndents(TexturePaintWindow w, string path, int width, int height, string name)
        {
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, name);
                Color At(float x, float guiY) => texture.GetPixel(Mathf.Clamp(Mathf.RoundToInt(x), 0, width - 1), Mathf.Clamp(height - 1 - Mathf.RoundToInt(guiY), 0, height - 1));
                bool Near(Color a, Color b) => Mathf.Abs(a.r - b.r) < .02f && Mathf.Abs(a.g - b.g) < .02f && Mathf.Abs(a.b - b.b) < .02f;
                foreach (var key in w.LastPropertySections)
                {
                    var (rect, sub) = w.SectionHeaderRects[key];
                    float y = rect.y + 3; // 帯の上の線の下・文字の上
                    if (!sub)
                    {
                        Assert.That(rect.x, Is.EqualTo(0), name + " " + key + ": the band starts at the left edge");
                        Assert.That(rect.width, Is.EqualTo(width), name + " " + key + ": the band is as wide as the panel");
                        Assert.That(Near(At(1, y), PaintTheme.SectionBand) && Near(At(width - 2, y), PaintTheme.SectionBand), Is.True, name + " " + key + ": a dark band from edge to edge");
                    }
                    else
                    {
                        Assert.That(rect.x, Is.GreaterThan(PaintTheme.Padding), name + " " + key + ": a subsection is one level in");
                        Assert.That(Near(At(1, y), PaintTheme.PanelBg), Is.True, name + " " + key + ": a subsection is not a band");
                    }
                }
                // 中身の字下げ: ブラシの大きさ・マスクの濃度など、欄の最初の部品
                float indent = TexturePaintWindow.SectionIndentFor(width - 2 * PaintTheme.Padding);
                foreach (var id in new[] { "brush.size", "material.toggle" })
                    if (w.ToolControlOwner(id) == "properties" && w.TryToolControlPanelRect(id, out var r))
                        Assert.That(r.x, Is.EqualTo(PaintTheme.Padding + indent).Within(.5f), name + " " + id + ": contents start at the section's indent");
            }
            finally { Object.DestroyImmediate(texture); }
        }
    }
}
