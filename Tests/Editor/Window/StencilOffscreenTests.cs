using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ステンシルの重ね表示を表示せずに描いて確かめる（batch-gl。GUI モードのエディタはシェーダーが壊れているので、絵はこの経路で見る）:
    /// 2D キャンバスと 3D ビューの両方の、置き場（中心・大きさ・角度）どおりの所に画像が重なり、外には何も描かない。繰り返しは表示域の端まで、
    /// 量として読む画像は灰色、N を押しているあいだは出ない。英語と日本語でブラシの欄のステンシルの節が例外なく、文字を詰めずに描ける。
    /// 描いた PNG はテストプロジェクトの Logs/YoluPainterSnapshots に残す（リポジトリには入れない）。</summary>
    public sealed class StencilOffscreenTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots"));

        static Texture2D Render(TexturePaintWindow w, string name)
        {
            string path = Path.Combine(Folder, name + ".png");
            OffscreenGui.RenderWindow(w, 1200, 800, path);
            var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(path)); return t;
        }
        static Color At(Texture2D t, Vector2 gui) => t.GetPixel(Mathf.RoundToInt(gui.x), t.height - 1 - Mathf.RoundToInt(gui.y));
        static bool Magenta(Color c) => c.r > .85f && c.g < .2f && c.b > .85f;
        static bool Same(Color a, Color b) => Mathf.Abs(a.r - b.r) < .02f && Mathf.Abs(a.g - b.g) < .02f && Mathf.Abs(a.b - b.b) < .02f;
        /// <summary>置き場の、画像の (u, v)（0〜1、v は上向き）の GUI の点。</summary>
        static Vector2 Point(StencilFrame f, double u, double v)
        {
            double x = (u - .5) * f.Width, y = (.5 - v) * f.Height;
            return new Vector2((float)(f.Center.x + f.Cos * x - f.Sin * y), (float)(f.Center.y + f.Sin * x + f.Cos * y));
        }

        [Test] public void TheStencilIsDrawnWhereItIsPlacedOnBothViewsAndNowhereElse()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            var textures = new List<Texture2D>();
            try
            {
                w.Preview.LoadDemoMesh(); w.View = TexturePaintWindow.ViewMode.Split; w.Tool = TexturePaintWindow.PaintTool.Brush;
                var rgba = new byte[64 * 32 * 4];
                for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = 255; rgba[i + 1] = 0; rgba[i + 2] = 255; rgba[i + 3] = 255; }
                var magenta = w.ImageResources.Add("Magenta", ImageContent.FromPixels(rgba, 64, 32), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
                var plain = Render(w, "stencil-plain"); textures.Add(plain);
                w.SetStencil(magenta.Id); w.StencilOpacity = 1; w.StencilSize = .25f; w.StencilAngle = 35; w.StencilCenter = new Vector2(.48f, .52f); // 回した全体が両方の表示域に収まる
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    PaintGui.ShortenedTexts = 0;
                    var shown = Render(w, "stencil-shown-" + language); textures.Add(shown);
                    Assert.That(PaintGui.ShortenedTexts, Is.Zero, language + ": the Stencil section's labels fit");
                    foreach (var (view, name) in new[] { (w.CanvasRect, "2D"), (w.SurfaceRect, "3D") })
                    {
                        var f = w.StencilFrameIn(view).Value;
                        foreach (var (u, v) in new[] { (.5, .5), (.1, .15), (.9, .85), (.85, .2) })
                        {
                            Assert.That(view.Contains(Point(f, u, v)), Is.True, $"{name}: ({u}, {v}) is in the view");
                            Assert.That(Magenta(At(shown, Point(f, u, v))), Is.True, $"{language} {name}: inside the stencil at ({u}, {v}) → {At(shown, Point(f, u, v))}");
                        }
                        // 回っているので、回さない矩形の角のすぐ外（回した矩形の外）には描かない
                        foreach (var (u, v) in new[] { (-.15, .5), (1.15, .5), (.5, -.25), (.5, 1.25) })
                        {
                            var p = Point(f, u, v);
                            Assert.That(view.Contains(p), Is.True, $"{name}: ({u}, {v}) is in the view");
                            Assert.That(Same(At(shown, p), At(plain, p)), Is.True, $"{language} {name}: nothing drawn outside the stencil at ({u}, {v})");
                        }
                    }
                }
                L.OverrideLanguage(PainterLanguage.English);
                // 繰り返し（両方）: 表示域の中なら画像 1 枚の外にも
                w.StencilTilingSetting = StencilTiling.Both;
                var tiled = Render(w, "stencil-tiled"); textures.Add(tiled);
                var tf = w.StencilFrameIn(w.CanvasRect).Value; var outside = Point(tf, .5, 1.6); // 1 枚の上の隣の 1 枚の中
                Assert.That(w.CanvasRect.Contains(outside), Is.True);
                Assert.That(Magenta(At(tiled, outside)), Is.True, "tiled beyond the image");
                w.StencilTilingSetting = StencilTiling.None;
                // N を押しているあいだは出ない
                w.StencilIgnored = true;
                var ignored = Render(w, "stencil-ignored"); textures.Add(ignored);
                var center = w.StencilFrameIn(w.CanvasRect).Value.Center;
                Assert.That(Same(At(ignored, center), At(plain, center)), Is.True, "hidden while N is held");
                w.StencilIgnored = false;
                // 量として読む灰色の画像（とその反転）は灰色で見せる
                var grey = new byte[16 * 16 * 4];
                for (int i = 0; i < grey.Length; i += 4) { grey[i] = grey[i + 1] = grey[i + 2] = 200; grey[i + 3] = 255; }
                var mask = w.ImageResources.Add("Grey", ImageContent.FromPixels(grey, 16, 16), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
                w.SetStencil(mask.Id); w.StencilInvert = true;
                var inverted = Render(w, "stencil-mask-inverted"); textures.Add(inverted);
                w.StencilInvert = false;
                var masked = Render(w, "stencil-mask"); textures.Add(masked);
                var mc = w.StencilFrameIn(w.CanvasRect).Value.Center;
                Color gi = At(inverted, mc), gm = At(masked, mc);
                foreach (var g in new[] { gi, gm }) Assert.That(Mathf.Abs(g.r - g.g) + Mathf.Abs(g.r - g.b), Is.LessThan(.02f), "an amount shows grey: " + g);
                Assert.That(gm.r, Is.GreaterThan(gi.r + .2f), "200 shows lighter than its inversion 55 (" + gm + " / " + gi + ")");
                // 外すと何も重ならない
                w.SetStencil(null);
                var removed = Render(w, "stencil-removed"); textures.Add(removed);
                Assert.That(Same(At(removed, center), At(plain, center)), Is.True);
            }
            finally { foreach (var t in textures) Object.DestroyImmediate(t); Object.DestroyImmediate(w); L.OverrideLanguage(PainterLanguage.English); }
        }
    }
}
