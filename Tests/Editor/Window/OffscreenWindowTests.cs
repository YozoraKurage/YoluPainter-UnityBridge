using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ウィンドウの外枠を、表示せずに RenderTexture へ描く（batch-gl。この devcontainer の GUI モードのエディタは画面が
    /// マゼンタになるので、見た目の確認はこの経路で行う）。英語と日本語、最小と大きい大きさ、どのツールと表示でも例外なく描けて、
    /// 画像が空でないこと。描いた PNG はテストプロジェクトの Logs/YoluPainterSnapshots に残す（見て確かめる用。リポジトリには入れない）。</summary>
    public sealed class OffscreenWindowTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots"));

        [Test] public void TheWindowDrawsInEveryLanguageSizeToolAndView()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Preview.LoadDemoMesh();
                var d = w.Document; var layer = d.AddLayer("Details");
                d.AddFillLayer("Tint", new System.Collections.Generic.Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(200, 120, 80) } });
                d.AddLayerMask(layer.Id); d.ClearHistory();
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    foreach (var (width, height) in new[] { (980, 640), (1600, 950) })
                        Render(w, width, height, language + "-" + width + "x" + height);
                    foreach (TexturePaintWindow.PaintTool tool in System.Enum.GetValues(typeof(TexturePaintWindow.PaintTool)))
                    { w.Tool = tool; Render(w, 1200, 800, language + "-tool-" + tool); }
                    w.Tool = TexturePaintWindow.PaintTool.Brush;
                    foreach (TexturePaintWindow.ViewMode view in System.Enum.GetValues(typeof(TexturePaintWindow.ViewMode)))
                    { w.View = view; Render(w, 1200, 800, language + "-view-" + view); }
                    w.View = TexturePaintWindow.ViewMode.Split;
                }
            }
            finally { Object.DestroyImmediate(w); L.OverrideLanguage(PainterLanguage.English); }
        }

        /// <summary>3D を描いた後に外枠を描くとき、マウスの位置が OnGUI の始めのまま（3D の描画が (0,0) にしていた。外枠のマウスの乗った見た目が
        /// 並べる表示と 3D の表示で出なかった）。</summary>
        [Test] public void TheShellSeesTheMouseWhereItIsAfterThe3DViewIsDrawn()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon.");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                w.Preview.LoadDemoMesh();
                foreach (var view in new[] { TexturePaintWindow.ViewMode.Split, TexturePaintWindow.ViewMode.Model })
                {
                    w.View = view; var mouse = new Vector2(1000, 50);
                    OffscreenGui.RenderWindow(w, 1200, 800, Path.Combine(Folder, "mouse-" + view + ".png"), mouse);
                    Assert.That(w.shellMouseForTests, Is.EqualTo(mouse), view.ToString());
                }
            }
            finally { Object.DestroyImmediate(w); }
        }

        static void Render(TexturePaintWindow w, int width, int height, string name)
        {
            string path = Path.Combine(Folder, name + ".png");
            OffscreenGui.RenderWindow(w, width, height, path);
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, name);
                Assert.That((texture.width, texture.height), Is.EqualTo((width, height)), name);
                // 外枠が描けていれば色の数は多い（何も描けなければ背景の 1 色）
                int colors = texture.GetPixels32().Select(c => c.r << 16 | c.g << 8 | c.b).Distinct().Take(200).Count();
                Assert.That(colors, Is.GreaterThan(50), name + ": the window looks empty");
            }
            finally { Object.DestroyImmediate(texture); }
        }
    }
}
