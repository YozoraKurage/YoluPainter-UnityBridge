using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Brushes;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ウィンドウからのカラーダイナミクス・デュアルブラシ・傾き と、brush.json（schema 3）の保存復元・古い schema の読み込み。</summary>
    public sealed partial class WindowTests
    {
        void UseDynamicBrush()
        {
            var b = window.Brush;
            b.radius = 10; b.hardness = 1; b.spacing = .2f; b.opacity = 1; b.flow = 1; b.color = new Color(.8f, .2f, .1f, 1);
            b.randomSeedPerStroke = false; b.hueJitter = 1; b.brightnessJitter = .3f; b.colorPerTip = true;
        }
        void DrawLine(int x0, int x1, int y)
        {
            Mouse(window, EventType.MouseDown, At(window, x0, y));
            for (int x = x0 + 10; x <= x1; x += 10) Mouse(window, EventType.MouseDrag, At(window, x, y));
            Mouse(window, EventType.MouseUp, At(window, x1, y));
        }
        int DistinctOpaqueColours(PaintChannel channel)
        {
            var c = window.Document.Composite(channel);
            return Enumerable.Range(0, c.Length / 4).Where(i => c[i * 4 + 3] == 255).Select(i => (c[i * 4], c[i * 4 + 1], c[i * 4 + 2])).Distinct().Count();
        }

        [Test] public void PerTipColoursPaintFromTheWindowAndUndoExactly()
        {
            UseDynamicBrush();
            var before = Snapshot();
            DrawLine(200, 400, 300);
            Assert.That(DistinctOpaqueColours(PaintChannel.Color), Is.GreaterThan(5), window.StatusMessage);
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(Snapshot(), Is.EqualTo(before));
            BeginLine(300, 500); Key(window, KeyCode.Escape);
            Assert.That(window.IsStroking, Is.False); Assert.That(Snapshot(), Is.EqualTo(before), "Escape cancels a dynamic stroke too");
        }

        [Test] public void DataChannelsIgnoreColourDynamicsInTheWindow()
        {
            UseDynamicBrush(); window.Brush.fgBgJitter = 1; window.Brush.secondaryColor = Color.white; window.Brush.color = new Color(.5f, .5f, .5f, 1);
            window.Channel = PaintChannel.Roughness;
            DrawLine(200, 400, 300);
            Assert.That(DistinctOpaqueColours(PaintChannel.Roughness), Is.EqualTo(1), "Roughness gets the exact scalar");
            Assert.That(window.Document.CompositePixel(PaintChannel.Roughness, 300, 300), Is.EqualTo(new Rgba32(128, 128, 128, 255)));
        }

        [Test] public void TheDualBrushAndPenTiltShapeWindowDabs()
        {
            int Painted() => window.Document.Composite(PaintChannel.Color).Where((v, i) => i % 4 == 3 && v > 0).Count();
            var b = window.Brush; b.radius = 16; b.hardness = 1; b.randomSeedPerStroke = false;
            var at = At(window, 300, 300);
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            int plain = Painted(); window.Document.Undo();
            b.dualEnabled = true; b.dualRadius = 6; b.dualHardness = 1; b.dualTipId = "";
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            int dual = Painted(); window.Document.Undo();
            Assert.That(dual, Is.LessThan(plain / 4).And.GreaterThan(0), "the 6 px dual tip masks the 16 px dab");
            b.dualEnabled = false; b.tiltSize = true;
            // Event.tilt: 直立から X 方向へ 60° → 傾き 2/3 → 大きさ 1/3
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            var offset = window.rootVisualElement.worldBound.position;
            window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = at + offset, button = 0, pressure = 1, tilt = new Vector2(Mathf.PI / 3, 0) });
            window.SendEvent(new Event { type = EventType.MouseUp, mousePosition = at + offset, button = 0, pressure = 1, tilt = new Vector2(Mathf.PI / 3, 0) });
            int tilted = Painted();
            Assert.That(tilted, Is.LessThan(plain / 6).And.GreaterThan(0), "a leaning pen paints a smaller dab");
            window.Document.Undo();
            Mouse(window, EventType.MouseDown, at); Mouse(window, EventType.MouseUp, at);
            Assert.That(Painted(), Is.EqualTo(plain), "a mouse reports no tilt");
        }

        [Test] public void BrushSettingsSchema3RoundTripsAndOlderFilesReadWithDefaults()
        {
            var fake = UseFakeDialogs(window); fake.File = NewTempPath(".json");
            UseDynamicBrush(); var b = window.Brush;
            b.secondaryColor = new Color(0, 1, 0, 1); b.purity = -.25f; b.colorPerTip = false; b.dualEnabled = true; b.dualTipId = "builtin:dots"; b.dualRadius = 5; b.dualMode = (int)DualBrushMode.Darken;
            b.dualCount = 3; b.fadeSize = 12; b.fadeFlow = 4; b.tiltOpacity = true; b.tiltAngle = true;
            Invoke(window, "SavePreset");
            string saved = File.ReadAllText(fake.File);
            Assert.That(saved, Does.Contain("\"schema\": 3"));
            window.Brush = new TexturePaintWindow.BrushState();
            Invoke(window, "LoadPreset");
            Assert.That(JsonUtility.ToJson(window.Brush, true), Is.EqualTo(saved), window.StatusMessage);
            var s = window.GetBrush();
            Assert.That(s.Dual.Tip, Is.SameAs(BuiltInBrushes.Tip("dots"))); Assert.That((s.Dual.Mode, s.Dual.Count, s.FadeSize, s.FadeFlow, s.TiltOpacity, s.TiltAngle, s.ColorPerTip), Is.EqualTo((DualBrushMode.Darken, 3, 12, 4, true, true, false)));
            Assert.That(s.SecondaryColor, Is.EqualTo(new Rgba32(0, 255, 0, 255))); Assert.That(s.Purity, Is.EqualTo(-.25));
            // schema 2 のファイル: schema 3 の項目を取り除く
            var keys = new[] { "secondaryColor", "fgBgJitter", "hueJitter", "saturationJitter", "brightnessJitter", "purity", "colorPerTip", "dualEnabled", "dualTipId", "dualRadius", "dualHardness",
                "dualSpacing", "dualAngle", "dualRoundness", "dualScatter", "dualCount", "dualMode", "fadeSize", "fadeOpacity", "fadeFlow", "tiltSize", "tiltOpacity", "tiltFlow", "tiltAngle" };
            string old = saved.Replace("\"schema\": 3", "\"schema\": 2");
            foreach (var key in keys) old = Regex.Replace(old, "\\s*\"" + key + "\": (\\{[^}]*\\}|[^,\\n]*),?", "");
            old = Regex.Replace(old, ",(\\s*)}\\s*$", "$1}");
            Assert.That(old, Does.Not.Contain("hueJitter")); Assert.That(old, Does.Contain("\"radius\""));
            File.WriteAllText(fake.File, old);
            Invoke(window, "LoadPreset");
            var r = window.Brush;
            Assert.That(r.schema, Is.EqualTo(3)); Assert.That(r.radius, Is.EqualTo(10));
            Assert.That((r.hueJitter, r.purity, r.colorPerTip, r.dualEnabled, r.dualRadius, r.dualRoundness, r.dualCount, r.fadeSize, r.tiltAngle), Is.EqualTo((0f, 0f, true, false, 8f, 1f, 1, 0, false)));
            Assert.That(r.secondaryColor, Is.EqualTo(Color.black));
            File.WriteAllText(fake.File, saved.Replace("\"schema\": 3", "\"schema\": 4"));
            Invoke(window, "LoadPreset");
            Assert.That(window.StatusMessage, Does.Contain("Unsupported brush settings")); Assert.That(window.Brush, Is.SameAs(r), "a newer schema changes nothing");
        }

        [Test] public void TheDynamicsAreSavedInTheProjectAndPresetsCopyThem()
        {
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            UseDynamicBrush(); window.Brush.dualEnabled = true; window.Brush.fadeOpacity = 9; window.Brush.secondaryColor = Color.cyan;
            PaintDot(window, 300, 300); window.SaveProject(true);
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File; other.OpenProject();
                Assert.That(JsonUtility.ToJson(other.Brush), Is.EqualTo(JsonUtility.ToJson(window.Brush)), other.StatusMessage);
            }
            finally { Close(other); }
            UseTemporaryBrushLibrary();
            var dualTip = new BrushTip("d", 2, 2, new byte[] { 9, 8, 7, 6 });
            var preset = BrushLibrary.Personal.Add(new[] { new ImportedBrush("Dyn", "test", new BrushSettings { SaturationJitter = .5, TiltFlow = true, Dual = new DualBrush { Tip = dualTip, Radius = 3, Mode = DualBrushMode.Overlay } }) }, "")[0];
            window.ApplyPreset(preset);
            var b = window.Brush;
            Assert.That((b.saturationJitter, b.hueJitter, b.tiltFlow, b.fadeOpacity, b.dualEnabled, b.dualRadius, b.dualMode), Is.EqualTo((.5f, 0f, true, 0, true, 3f, (int)DualBrushMode.Overlay)));
            Assert.That(b.secondaryColor, Is.EqualTo(Color.cyan), "the background colour is the painter's, like the colour");
            Assert.That(window.GetBrush().Dual.Tip.CopyAlpha(), Is.EqualTo(dualTip.CopyAlpha()));
            window.ApplyPreset(BuiltInBrushes.Presets[0]);
            Assert.That(window.Brush.dualEnabled || window.Brush.saturationJitter != 0 || window.Brush.tiltFlow, Is.False, "a preset without dynamics clears them");
            window.Brush.textureDepth = 0; window.SetTexture("builtin:grain");
            Assert.That(window.Brush.textureDepth, Is.EqualTo(1)); Assert.That(window.GetBrush().Texture, Is.SameAs(BuiltInBrushes.Tip("grain")));
        }
    }
}
