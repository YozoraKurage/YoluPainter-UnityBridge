using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>カラーパネルの色相の円: 円の上の位置と色相（真上が赤・時計回り）、円の中の四角が輪に重ならない、円で色相を選ぶと彩度と明度を
    /// 保つ、四角と帯・円のどちらの形でも描ける（英語・日本語、batch-gl のオフスクリーン）、形は描き手ごとに覚える。</summary>
    public sealed class ColorPanelTests
    {
        const string Key = "Yozolab.YoluPainter.ColorWheel";
        bool hadKey, saved;
        [SetUp] public void RememberPreference() { hadKey = EditorPrefs.HasKey(Key); saved = EditorPrefs.GetBool(Key, false); }
        [TearDown] public void RestorePreference() { if (hadKey) EditorPrefs.SetBool(Key, saved); else EditorPrefs.DeleteKey(Key); }

        [Test] public void TheWheelMapsPositionsToHuesClockwiseFromRedAtTheTop()
        {
            var wheel = new Rect(10, 20, 200, 200); var c = wheel.center; float r = 90;
            Assert.That(TexturePaintWindow.HueAt(wheel, c + new Vector2(0, -r)), Is.EqualTo(0).Within(1e-4), "top is red");
            Assert.That(TexturePaintWindow.HueAt(wheel, c + new Vector2(r, 0)), Is.EqualTo(.25f).Within(1e-4), "right");
            Assert.That(TexturePaintWindow.HueAt(wheel, c + new Vector2(0, r)), Is.EqualTo(.5f).Within(1e-4), "bottom");
            Assert.That(TexturePaintWindow.HueAt(wheel, c + new Vector2(-r, 0)), Is.EqualTo(.75f).Within(1e-4), "left");
            Assert.That(TexturePaintWindow.InRing(wheel, c + new Vector2(0, -95)), Is.True);
            Assert.That(TexturePaintWindow.InRing(wheel, c + new Vector2(0, -50)), Is.False, "inside the ring is the square, not the ring");
            var square = TexturePaintWindow.WheelSquare(wheel);
            foreach (var corner in new[] { new Vector2(square.xMin, square.yMin), new Vector2(square.xMax, square.yMax), new Vector2(square.xMin, square.yMax), new Vector2(square.xMax, square.yMin) })
                Assert.That(Vector2.Distance(corner, c), Is.LessThan(100 * (1 - TexturePaintWindow.RingThickness)), "the square's corners stay inside the ring");
        }

        [Test] public void PickingOnTheWheelChangesOnlyTheHue()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Brush.color = Color.HSVToRGB(0, .5f, .8f);
                var wheel = new Rect(0, 0, 200, 200);
                w.PickHueAt(wheel, wheel.center + new Vector2(0, 90)); // 下 = 0.5（シアン）
                Color.RGBToHSV(w.Brush.color, out float h, out float s, out float v);
                Assert.That(h, Is.EqualTo(.5f).Within(.01f)); Assert.That(s, Is.EqualTo(.5f).Within(.01f)); Assert.That(v, Is.EqualTo(.8f).Within(.01f));
                Assert.That(w.PickedHue, Is.EqualTo(.5f).Within(1e-4));
                w.ColorWheel = true; Assert.That(EditorPrefs.GetBool(Key, false), Is.True, "remembered per user");
                w.ColorWheel = false; Assert.That(EditorPrefs.GetBool(Key, true), Is.False);
            }
            finally { Object.DestroyImmediate(w); }
        }

        [TestCase(180, "English")] [TestCase(180, "Japanese")]
        [TestCase(300, "English")] [TestCase(300, "Japanese")]
        public void OpaqueColorPanelFitsNarrowWidthsInBothLanguages(int width, string languageName)
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on batch-gl.");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device.");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            string folder = Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "pen-ui")); Directory.CreateDirectory(folder);
            var language = (PainterLanguage)System.Enum.Parse(typeof(PainterLanguage), languageName);
            var draw = typeof(TexturePaintWindow).GetMethod("DrawColorPanel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            try
            {
                L.OverrideLanguage(language);
                foreach (bool wheel in new[] { false, true })
                {
                    w.ColorWheel = wheel;
                    string path = Path.Combine(folder, language + "-" + width + (wheel ? "-wheel" : "-square") + ".png");
                    OffscreenGui.RenderToPng(width, 210, () => draw.Invoke(w, new object[] { new Rect(0, 0, width, 210) }), path, PaintTheme.PanelBg);
                    foreach (var id in new[] { "main", "sub", "hex" })
                    {
                        var r = w.ColorPanelScreenRects[id];
                        Assert.That(r.width, Is.GreaterThan(0)); Assert.That(r.xMin, Is.GreaterThanOrEqualTo(0));
                        Assert.That(r.xMax, Is.LessThanOrEqualTo(width)); Assert.That(r.yMax, Is.LessThanOrEqualTo(210));
                    }
                    Assert.That(w.Brush.color.a, Is.EqualTo(1));
                }
            }
            finally { Object.DestroyImmediate(w); L.OverrideLanguage(PainterLanguage.English); }
        }
        [Test] public void BothStylesDrawOffscreen()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon.");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            string folder = Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "color-panel")); Directory.CreateDirectory(folder);
            try
            {
                w.Brush.color = new Color(.2f, .6f, 1, 1);
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    foreach (var wheel in new[] { false, true })
                    {
                        L.OverrideLanguage(language); w.ColorWheel = wheel;
                        string path = Path.Combine(folder, language + (wheel ? "-wheel" : "-square") + ".png");
                        OffscreenGui.RenderWindow(w, 1200, 900, path);
                        var texture = new Texture2D(2, 2);
                        try
                        {
                            Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True);
                            // 円のときは色相の円の色（彩度の高い色）がたくさん出る
                            int vivid = texture.GetPixels32().Count(c => Mathf.Max(c.r, c.g, c.b) - Mathf.Min(c.r, c.g, c.b) > 200);
                            Assert.That(vivid, Is.GreaterThan(wheel ? 3000 : 500), path);
                        }
                        finally { Object.DestroyImmediate(texture); }
                    }
            }
            finally { Object.DestroyImmediate(w); L.OverrideLanguage(PainterLanguage.English); }
        }
    }
}
