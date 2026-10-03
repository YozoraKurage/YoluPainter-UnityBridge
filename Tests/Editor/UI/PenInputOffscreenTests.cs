using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class PenInputOffscreenTests
    {
        [Test] public void PenInputPanelFitsEnglishAndJapaneseInAllViewsAndMinimumWindowSize()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen visuals are checked in batch-gl.");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device.");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                double now = 0; w.PenInputClock = () => now; w.PenInputEnabled = true;
                w.PenInput.Observe(PenInputTests.Point(0, EventType.MouseDown, .25f));
                for (int i = 1; i <= 20; i++) w.PenInput.Observe(PenInputTests.Point(i * 3.125, EventType.MouseDrag, i / 20f, i * 9.25f));
                now = 100; typeof(TexturePaintWindow).GetMethod("UpdatePenInput", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(w, null);
                w.Preview.LoadDemoMesh();
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    foreach (var size in new[] { new Vector2Int(980, 640), new Vector2Int(1600, 950) })
                    foreach (var view in new[] { TexturePaintWindow.ViewMode.Canvas, TexturePaintWindow.ViewMode.Model, TexturePaintWindow.ViewMode.Split })
                    {
                        w.View = view; w.SplitRatio = .5f; Draw(w, size, language + "-" + view);
                    }
                    w.View = TexturePaintWindow.ViewMode.Split; w.SplitRatio = .15f; w.ViewsSwapped = true;
                    Draw(w, new Vector2Int(980, 640), language + "-narrow-swapped");
                    w.SplitRatio = .5f; w.ViewsSwapped = false;
                }
            }
            finally { Object.DestroyImmediate(w); L.OverrideLanguage(PainterLanguage.English); }
        }

        static void Draw(TexturePaintWindow w, Vector2Int size, string name)
        {
            string path = Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "pen-input-" + name + "-" + size.x + ".png"));
            OffscreenGui.RenderWindow(w, size.x, size.y, path);
            var r = w.PenInputPanelRect;
            Assert.That(r.xMin, Is.GreaterThanOrEqualTo(0)); Assert.That(r.xMax, Is.LessThanOrEqualTo(size.x));
            Assert.That(r.yMax, Is.LessThan(size.y - PaintTheme.StatusBarHeight));
            Assert.That(w.PenInputDiscardRect.yMax, Is.LessThanOrEqualTo(r.yMax - 8), "buttons must fit inside the panel");
            float labelWidth = Mathf.Min(145, (r.width - 20) * .53f), valueWidth = r.width - 24 - labelWidth;
            foreach (var row in w.PenInputRows(w.CurrentPenInputMetrics))
            {
                var columns = row.Split('|');
                Assert.That(PaintTheme.LabelDim.CalcSize(new GUIContent(columns[0])).x, Is.LessThanOrEqualTo(labelWidth), name + ": " + columns[0]);
                Assert.That(PaintTheme.LabelDim.CalcSize(new GUIContent(columns[1])).x, Is.LessThanOrEqualTo(valueWidth), name + ": " + columns[1]);
            }
            string flags = TexturePaintWindow.PenStatusName(w.PenInput.Latest.Value.Pen);
            Assert.That(PaintTheme.LabelDim.CalcSize(new GUIContent(flags)).x, Is.LessThanOrEqualTo(r.width - 20), name + ": pen flags");
            foreach (var pair in new[] { (w.PenInputRecordRect, L.Tr("Record")), (w.PenInputSaveRect, L.Tr("Save CSV…")), (w.PenInputDiscardRect, L.Tr("Discard")) })
                Assert.That(PaintTheme.LabelCenter.CalcSize(new GUIContent(pair.Item2)).x, Is.LessThanOrEqualTo(pair.Item1.width), name + ": " + pair.Item2);
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True);
                var colors = texture.GetPixels((int)r.x + 2, size.y - (int)r.yMax + 2, (int)r.width - 4, (int)r.height - 4);
                Assert.That(colors.Select(c => (Color32)c).Distinct().Take(50).Count(), Is.GreaterThan(20), "the panel should contain text and controls");
            }
            finally { Object.DestroyImmediate(texture); }
        }
    }
}
