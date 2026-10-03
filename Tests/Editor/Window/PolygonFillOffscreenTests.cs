using System.IO;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ポリゴン塗りつぶしの、ポインタの下の範囲の強調を表示せずに描いて確かめる（batch-gl。GUI モードのエディタはシェーダーが壊れて
    /// いるので、絵はこの経路で見る）: 3D ビューではその範囲に薄い色が乗り、2D キャンバスではその範囲の UV の輪郭が線で出る。英語と日本語、
    /// 塗る（橙）と消す（桃色）。描いた PNG はテストプロジェクトの Logs/YoluPainterSnapshots に残す（リポジトリには入れない）。</summary>
    public sealed class PolygonFillOffscreenTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots"));

        static Texture2D Render(TexturePaintWindow w, string name, Vector2? mouse = null)
        {
            string path = Path.Combine(Folder, name + ".png");
            OffscreenGui.RenderWindow(w, 1200, 800, path, mouse);
            var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(path)); return t;
        }
        /// <summary>GUI の座標の画素（PNG は下から上の行）。</summary>
        static Color At(Texture2D t, Vector2 gui) => t.GetPixel(Mathf.RoundToInt(gui.x), t.height - 1 - Mathf.RoundToInt(gui.y));
        static bool Near(Texture2D t, Vector2 gui, System.Func<Color, bool> test)
        {
            for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) if (test(At(t, gui + new Vector2(dx, dy)))) return true;
            return false;
        }
        static bool Orange(Color c) => c.r > .8f && c.g > .45f && c.g < .8f && c.b < .35f;
        static bool Pink(Color c) => c.r > .7f && c.r - c.g > .3f && c.b - c.g > .1f;

        [Test] public void TheRegionUnderThePointerIsTintedIn3DAndOutlinedIn2D()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            if (Shader.Find("Hidden/Internal-Colored") == null) Assert.Ignore("Unity's built-in colored shader is missing.");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Preview.LoadDemoMesh(); w.Tool = TexturePaintWindow.PaintTool.PolygonFill; w.SurfacePick = SurfaceRegionKind.UvIsland;
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    w.PolygonFillErase = false; w.UpdatePolygonFillHover(new Vector2(-100, -100)); // 前の言語の強調を消す
                    var plain = Render(w, "polyfill-plain-" + language);
                    Assert.That(w.PolygonFillHoverKey, Is.EqualTo(-1));
                    var center = w.SurfaceRect.center;
                    w.UpdatePolygonFillHover(center);
                    Assert.That(w.PolygonFillHoverKey, Is.Not.EqualTo(-1), "the demo cube is under the 3D view's center");
                    var hover = Render(w, "polyfill-hover-" + language, center);
                    Color before = At(plain, center), after = At(hover, center);
                    Assert.That(after.r - after.b, Is.GreaterThan(before.r - before.b + .1f), $"{language}: the face under the pointer turns orange ({before} → {after})");
                    // 2D: 輪郭の 1 辺の中点に橙の線
                    var outline = w.PolygonFillHoverOutline; Assert.That(outline, Is.Not.Null.And.Not.Empty);
                    var mid = (outline[0] + outline[1]) / 2;
                    var gui = w.PixelToGui(Mathf.FloorToInt(mid.x * w.Document.Width), Mathf.FloorToInt(mid.y * w.Document.Height));
                    Assert.That(Near(hover, gui, Orange), Is.True, $"{language}: the island's UV outline is drawn on the canvas at {gui}");
                    Assert.That(Near(plain, gui, Orange), Is.False, "nothing orange there without the pointer");
                    // 消す: 桃色
                    w.PolygonFillErase = true;
                    var erase = Render(w, "polyfill-hover-erase-" + language, center);
                    Assert.That(Near(erase, gui, Pink), Is.True, $"{language}: erasing outlines in pink");
                    Assert.That(At(erase, center).b - At(erase, center).g, Is.GreaterThan(before.b - before.g + .05f), "and tints the face pink");
                    // マスクの編集中のオプションバー（白・黒）も例外なく描ける
                    if (w.Document.GetLayer(w.SelectedLayer).Mask == null) w.Document.AddLayerMask(w.SelectedLayer);
                    w.EditMask = true; var mask = Render(w, "polyfill-mask-" + language, center); w.EditMask = false;
                    foreach (var t in new[] { plain, hover, erase, mask }) Object.DestroyImmediate(t);
                }
            }
            finally { Object.DestroyImmediate(w); L.OverrideLanguage(PainterLanguage.English); }
        }
    }
}
