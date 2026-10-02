using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウの Normal: Normal チャンネルの表示が出力（平らな法線の上・不透明・Height → Normal 込み）であること、傾きで選んだ
    /// ブラシの値、Height → Normal の設定が 1 回の Undo（Ctrl+Z / Ctrl+Shift+Z）で戻り・進むこと、描画中は設定を変えられず Esc で何も残らない
    /// こと、.ylp の保存と開き直しで設定が残り、.ylp の Normal のテクスチャは OpenGL、Export Images は設定の向き（DirectX なら緑を反転）で
    /// 書かれること。入力は SendEvent で本物の経路に通す。</summary>
    public sealed partial class WindowTests
    {
        static readonly byte[] FlatPixel = { 128, 128, 255, 255 };

        /// <summary>表示中のテクスチャの画素（GPU の RenderTexture でも CPU 代替の Texture2D でも）。</summary>
        static byte[] ReadDisplay(TexturePaintWindow w)
        {
            Repaint(w);
            return w.DisplayTexture is RenderTexture ? GpuTests.Read(w.DisplayTexture) : GpuTests.ReadCpu(w.DisplayTexture);
        }
        /// <summary>CPU の経路なら一致、GPU で合成と出力を続けた表示なら丸め 2 段まで（GpuNormalTests と同じ根拠）。</summary>
        static void AssertShows(TexturePaintWindow w, byte[] expected, string context)
        {
            var shown = ReadDisplay(w); int tolerance = w.DisplayTexture is RenderTexture ? 2 : 0, worst = 0;
            Assert.That(shown.Length, Is.EqualTo(expected.Length), context);
            for (int i = 0; i < shown.Length; i++) worst = System.Math.Max(worst, System.Math.Abs(shown[i] - expected[i]));
            Assert.That(worst, Is.LessThanOrEqualTo(tolerance), context + " (" + w.NormalOutput?.Backend + ")");
        }
        static bool AllFlat(byte[] rgba) => rgba.Select((b, i) => b == FlatPixel[i % 4]).All(x => x);

        [Test] public void TheNormalChannelShowsTheOutputAndTheBrushPaintsUnitNormals()
        {
            var d = window.Document;
            window.Channel = PaintChannel.Normal;
            Repaint(window);
            Assert.That(window.NormalOutput, Is.Not.Null); Assert.That(window.DisplayTexture, Is.SameAs(window.NormalOutput.Texture));
            Assert.That(AllFlat(ReadDisplay(window)), Is.True, "an unpainted Normal channel shows flat, opaque normals");
            window.SetBrushNormal(1, 0); // 真横 (+X): (255, 128, 128)
            Assert.That(window.GetBrush().Color, Is.EqualTo(new Rgba32(255, 128, 128)));
            window.SetBrushNormal(3, 4); // 長さが 1 を超える傾きは z = 0 の向きに縮める: (0.6, 0.8, 0)
            var c = window.GetBrush().Color;
            Assert.That((c.R, c.B), Is.EqualTo(((byte)204, (byte)128))); Assert.That((int)c.G, Is.InRange(229, 230));
            window.SetBrushNormal(1, 0);
            PaintDot(window, 300, 300);
            Assert.That(d.CompositePixel(PaintChannel.Normal, 300, 300), Is.EqualTo(new Rgba32(255, 128, 128)));
            AssertShows(window, NormalMaps.Output(d), "painted output");
            window.ShowNormalOutput = false; Repaint(window);
            Assert.That(window.DisplayTexture, Is.SameAs(window.Compositor.Texture), "the painted layers with transparency");
            Assert.That(window.NormalOutput, Is.Null, "the output's textures are released while it is not shown");
            window.ShowNormalOutput = true; Repaint(window);
            Assert.That(window.NormalOutput, Is.Not.Null);
            window.Channel = PaintChannel.Color; Repaint(window);
            Assert.That(window.NormalOutput, Is.Null); Assert.That(window.DisplayTexture, Is.SameAs(window.Compositor.Texture));
        }

        [Test] public void HeightToNormalIsOneUndoStepAndShapesTheOutput()
        {
            var d = window.Document;
            window.Channel = PaintChannel.Height; window.Brush.color = Color.white;
            PaintDot(window, 400, 400);
            int steps = d.UndoCount;
            window.ApplyNormalSettings(d.NormalSettings.WithDerive(true).WithStrength(32));
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1)); Assert.That(window.StatusMessage, Does.Contain("Height → Normal on"));
            window.Channel = PaintChannel.Normal;
            var output = NormalMaps.Output(d);
            Assert.That(AllFlat(output), Is.False, "the Height dot shows up as a bump");
            Assert.That(output.Skip((400 * d.Width + 400) * 4).Take(4), Is.EqualTo(FlatPixel), "the top of the dot is flat");
            AssertShows(window, output, "derived output");
            Key(window, KeyCode.Z, EventModifiers.Control);
            Assert.That(d.NormalSettings, Is.EqualTo(NormalSettings.Default));
            Assert.That(AllFlat(ReadDisplay(window)), Is.True, "undo switches the derived normal off again");
            Key(window, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            Assert.That(d.NormalSettings.DeriveFromHeight, Is.True); Assert.That(d.NormalSettings.Strength, Is.EqualTo(32));
            AssertShows(window, output, "redo");
            // スライダーのドラッグ（coalesce）は 1 回にまとまる
            steps = d.UndoCount;
            for (int s = 10; s < 15; s++) window.ApplyNormalSettings(d.NormalSettings.WithStrength(s), coalesce: true);
            d.EndCoalescing();
            Assert.That(d.UndoCount, Is.EqualTo(steps + 1));
        }

        [Test] public void SettingsCannotChangeDuringAStrokeAndEscapeLeavesNothing()
        {
            var d = window.Document;
            window.Channel = PaintChannel.Normal; window.SetBrushNormal(0, 1);
            BeginLine(300, 300);
            Assert.That(() => window.ApplyNormalSettings(NormalSettings.Default.WithDerive(true)), Throws.InvalidOperationException);
            Key(window, KeyCode.Escape);
            Assert.That(window.IsStroking, Is.False);
            Mouse(window, EventType.MouseUp, At(window, 360, 300));
            Assert.That(d.NormalSettings, Is.EqualTo(NormalSettings.Default));
            Assert.That(d.CompositePixel(PaintChannel.Normal, 330, 300).A, Is.Zero, "the cancelled stroke left nothing");
            Assert.That(AllFlat(ReadDisplay(window)), Is.True);
        }

        [Test] public void SaveOpenAndExportKeepTheNormalOutput()
        {
            var d = window.Document;
            window.Channel = PaintChannel.Height; window.Brush.color = Color.white; PaintDot(window, 200, 200);
            var settings = new NormalSettings(true, 8, HeightEdgeMode.Wrap, NormalYDirection.DirectX);
            window.ApplyNormalSettings(settings);
            Assert.That(window.StatusMessage, Does.Contain("Height → Normal on"));
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath();
            window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var files = SetFiles(YlpStore.Load(fake.File).Files);
            Assert.That(files.ContainsKey("composite/Normal.png"), Is.True, "Height → Normal gives a Normal texture without any Normal layer");
            Assert.That(Pixels(files["composite/Normal.png"]), Is.EqualTo(NormalMaps.Output(d)), "the .ylp texture is the Unity (OpenGL) output");
            var other = Open();
            try
            {
                UseFakeDialogs(other).File = fake.File;
                other.OpenProject();
                Assert.That(other.Document.NormalSettings, Is.EqualTo(settings), other.StatusMessage);
                Assert.That(other.Document.CanUndo, Is.False);
            }
            finally { Close(other); }
            fake.Folder = NewTempPath(); Directory.CreateDirectory(fake.Folder);
            window.ExportImages();
            Assert.That(window.StatusMessage, Does.Contain("DirectX (Y−)"));
            var exported = Pixels(File.ReadAllBytes(Path.Combine(fake.Folder, Path.GetFileNameWithoutExtension(fake.File) + "_Normal.png"))); // 保存後は .ylp の名前が頭に付く
            Assert.That(exported, Is.EqualTo(NormalMaps.FileOutput(d)), "files use the chosen direction");
            var opengl = NormalMaps.Output(d);
            Assert.That(exported.Where((b, i) => i % 4 == 1).Zip(opengl.Where((b, i) => i % 4 == 1), (a, b) => a + b).All(sum => sum == 255), Is.True, "DirectX is OpenGL with green inverted");
        }
    }
}
