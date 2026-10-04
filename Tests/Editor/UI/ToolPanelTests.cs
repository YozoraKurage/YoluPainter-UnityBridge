using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// プロパティの欄のツールの設定（ToolPanels・Stroke・BrushDynamics・Paths・Surface3D）: 自前の部品（PaintGui）だけで描き、
    /// どのツールでも、英語でも日本語でも、最小（980×640）と大きい（1600×950）ウィンドウで例外なく描け、UI の文字が欄に収まる
    /// （… で詰めた文字が 0）。日本語の説明文は文字の間で折り返し、閉じ括弧・句読点を行頭に置かない。描いた PNG はテスト
    /// プロジェクトの Logs/YoluPainterSnapshots/ToolPanels に残す（見て確かめる用。リポジトリには入れない）。
    /// </summary>
    public sealed class ToolPanelTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "ToolPanels"));

        [TearDown] public void BackToEnglish() => L.OverrideLanguage(PainterLanguage.English);

        [Test] public void EveryToolPanelDrawsInBothLanguagesAndBothSizesWithoutCutText()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            var drawnTexts = new HashSet<string>(); PaintGui.TextDrawn = t => drawnTexts.Add(t);
            try
            {
                w.Preview.LoadDemoMesh();
                w.SetToolSectionsOpen(true); // 折りたたんだセクションの中身も描く
                int drawn = 0, tabs = 0;
                foreach (var scenario in new[] { "plain", "busy", "canvas-path" })
                {
                    Prepare(w, scenario);
                    var tools = scenario == "canvas-path" ? new[] { TexturePaintWindow.PaintTool.Path } : (TexturePaintWindow.PaintTool[])Enum.GetValues(typeof(TexturePaintWindow.PaintTool));
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        foreach (var tool in tools)
                            foreach (var (width, height) in new[] { (980, 640), (1600, 950) })
                            {
                                w.Tool = tool;
                                string name = scenario + "-" + language + "-" + tool + "-" + width + "x" + height;
                                PaintGui.ShortenedTexts = 0;
                                Render(w, width, height, name);
                                Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": a UI label did not fit its box and was cut with …: " + PaintGui.ShortenedTextsSummary);
                                drawn++;
                            }
                        // プロパティの欄のほかのタブ（ブラシ｜マテリアル、マスクに描くときはマスク）を、既定のドックの欄の幅（300 − 枠 1 − スクロールの印 8）で
                        foreach (var tool in tools)
                        {
                            w.Tool = tool; var context = w.PropertyContextNow();
                            foreach (var tab in w.PropertyTabsNow())
                            {
                                w.SetPropertyTab(context, tab);
                                string name = scenario + "-" + language + "-" + tool + "-tab-" + tab;
                                PaintGui.ShortenedTexts = 0;
                                RenderProperties(w, 291, name);
                                Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": a UI label did not fit its box and was cut with …: " + PaintGui.ShortenedTextsSummary);
                                tabs++;
                            }
                            if (w.PropertyTabsNow().Count > 0) w.SetPropertyTab(context, w.PropertyTabsNow()[0]);
                        }
                        // オプションバーの対称と手ぶれ補正の小さな窓
                        foreach (OptionPopupKind kind in Enum.GetValues(typeof(OptionPopupKind)))
                        {
                            string name = scenario + "-" + language + "-popup-" + kind;
                            PaintGui.ShortenedTexts = 0;
                            var size = w.OptionPopupSize(kind);
                            OffscreenGui.RenderToPng((int)size.x, 900, () => w.DrawOptionPopup(kind, new Rect(0, 0, size.x, 900)), Path.Combine(Folder, name + ".png"), PaintTheme.PanelBg);
                            Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": a UI label did not fit the popup and was cut with …: " + PaintGui.ShortenedTextsSummary);
                            Assert.That(w.OptionPopupSize(kind).y, Is.LessThan(900), name + ": the popup's content fits the window size it asks for");
                        }
                    }
                }
                Assert.That(drawn, Is.EqualTo(2 * 2 * (2 * Enum.GetValues(typeof(TexturePaintWindow.PaintTool)).Length + 1)));
                Assert.That(tabs, Is.GreaterThan(0), "the painting tools have tabs");
                // 描いた文字（英語・日本語）に、操作の説明文（クリックして〜、ドラッグで〜）が無い（説明はツールチップと ヘルプ ▸ キーボードショートカット）
                Assert.That(drawnTexts.Count, Is.GreaterThan(100), "the drawn texts were collected");
                Assert.That(drawnTexts.Where(t => NoInstructionTextTests.LooksLikeInstruction(t)), Is.Empty, "a drawn text reads like operating instructions");
            }
            finally { PaintGui.TextDrawn = null; Object.DestroyImmediate(w); }
        }

        /// <summary>busy: データのチャンネル（値のスライダー）、見つからない先端、紙の質感、画像の先端のデュアルブラシ、選択範囲、
        /// マスクの編集（色の変化の知らせ）、別のモデルで描いたパス（注意と無効のボタン）。canvas-path: 2D のパスの層。</summary>
        static void Prepare(TexturePaintWindow w, string scenario)
        {
            if (scenario == "plain") return;
            var d = w.Document;
            if (scenario == "busy")
            {
                string tip = "builtin:" + BuiltInBrushes.TipIds.First();
                w.Channel = PaintChannel.Roughness;
                w.Brush.tipId = "lib:nowhere/a-tip-that-is-not-installed-anywhere"; w.SetTexture(tip);
                w.Brush.dualEnabled = true; w.Brush.dualTipId = tip;
                w.Symmetry = true; w.SymmetryOffset = -123.4567f; // シンメトリーの欄（切り替えた後の有効な部品と長い数値）と 3D ビューの対称の面
                // ステンシルの欄（長い名前の画像、色として読む・マスクの編集中の知らせ、繰り返し）
                var stencil = w.ImageResources.Add("A stencil image with a rather long name for its box", ImageContent.FromPixels(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 0 }, 2, 2), ResourceOrigin.None, ResourceColorSpace.Srgb, out _);
                w.SetStencil(stencil.Id); w.StencilTilingSetting = StencilTiling.Both;
                d.SetSelection(SelectionMask.All(d));
                var layer = d.AddLayer("Model path"); d.SetChannelEnabled(layer.Id, PaintChannel.Color, true);
                d.SetPath(layer.Id, new SurfacePath(Guid.NewGuid(), PaintChannel.Color, "another-model", new PathBrush(), new[] { new PathPoint(0, .3, .3) }), new SparseTileSurface(d.Width, d.Height, d.TileSize));
                d.AddLayerMask(layer.Id);
                w.SelectedLayer = layer.Id; w.EditMask = true;
            }
            else
            {
                w.EditMask = false;
                var layer = d.AddLayer("Canvas path"); d.SetChannelEnabled(layer.Id, PaintChannel.Color, true);
                d.SetCanvasPath(layer.Id, new CanvasPath(Guid.NewGuid(), PaintChannel.Color, new PathBrush(), new[] { new CanvasPoint(10, 10), new CanvasPoint(200, 300) }));
                w.SelectedLayer = layer.Id;
            }
        }

        static void Render(TexturePaintWindow w, int width, int height, string name)
        {
            string path = Path.Combine(Folder, name + ".png");
            OffscreenGui.RenderWindow(w, width, height, path);
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, name);
                int colors = texture.GetPixels32().Select(c => c.r << 16 | c.g << 8 | c.b).Distinct().Take(200).Count();
                Assert.That(colors, Is.GreaterThan(50), name + ": the window looks empty");
            }
            finally { Object.DestroyImmediate(texture); }
        }

        /// <summary>プロパティの欄だけを width の幅で描く（オフスクリーン）。</summary>
        static void RenderProperties(TexturePaintWindow w, int width, string name)
        {
            float used = 0;
            OffscreenGui.RenderToPng(width, 1400, () => used = w.DrawPropertiesOnly(new Rect(0, 0, width, 1400)), Path.Combine(Folder, name + ".png"), PaintTheme.PanelBg);
            Assert.That(used, Is.GreaterThan(40), name + ": the tab is empty");
        }

        // ───────── 部品だけで描いていること ─────────

        static readonly string[] ToolPanelFiles = { "Window/Tools/TexturePaintWindow.ToolPanels.cs", "Window/Tools/TexturePaintWindow.Stroke.cs", "Window/Tools/TexturePaintWindow.BrushDynamics.cs", "Window/Tools/TexturePaintWindow.Paths.cs", "Window/Model/TexturePaintWindow.Surface3D.cs", "Window/Model/TexturePaintWindow.Symmetry.cs", "Window/Shell/TexturePaintWindow.OptionPopups.cs", "Window/Shell/TexturePaintWindow.PropertyRoutes.cs" };
        /// <summary>Unity の標準の見た目の部品（と、それで描く区画）。EditorGUI.DrawRect はパスの印を描くだけなので除く。</summary>
        static readonly Regex UnityControls = new Regex(@"\b(?:EditorGUILayout|GUILayout|EditorStyles)\.|\bLegacySection\s*\(|\bEditorGUI\.(?!DrawRect\b)|\bGUI\.(?:Button|Toggle|TextField|TextArea|HorizontalSlider|VerticalSlider|Box|Label|Toolbar|SelectionGrid|BeginScrollView)\b");

        [Test] public void TheToolPanelsUseOnlyThePaintKit()
        {
            string editor = PackagePaths.Physical("Editor");
            var found = new List<string>();
            foreach (var file in ToolPanelFiles)
            {
                string path = Path.Combine(editor, file);
                Assert.That(File.Exists(path), Is.True, file);
                var lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                    if (UnityControls.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) found.Add(file + ":" + (i + 1) + ": " + lines[i].Trim());
            }
            Assert.That(found, Is.Empty, "the tool part of the Properties panel draws Unity's default controls:\n" + string.Join("\n", found));
        }

        // ───────── 収まらない文字と折り返し ─────────

        [Test] public void FitCutsOnlyWhatDoesNotFitAndCountsIt()
        {
            var style = PaintTheme.Label;
            Assume.That(PaintGui.TextWidth("Brush", style), Is.GreaterThan(10), "no font to measure with");
            PaintGui.ShortenedTexts = 0;
            Assert.That(PaintGui.Fit("Brush", 500, style), Is.EqualTo("Brush"));
            Assert.That(PaintGui.ShortenedTexts, Is.Zero);
            string cut = PaintGui.Fit("A label that is far too long for its box", 80, style);
            Assert.That(cut, Does.EndWith("…")); Assert.That(PaintGui.TextWidth(cut, style), Is.LessThanOrEqualTo(80));
            Assert.That(PaintGui.ShortenedTexts, Is.EqualTo(1));
            PaintGui.Fit("user-data-that-is-far-too-long-for-its-box", 40, style, count: false);
            Assert.That(PaintGui.ShortenedTexts, Is.EqualTo(1), "user data (brush names) is not counted");
        }

        [Test] public void JapaneseParagraphsBreakBetweenCharactersButNotBeforeClosingMarks()
        {
            var style = PaintTheme.Wrap;
            float em = PaintGui.TextWidth("あ", style);
            Assume.That(em, Is.GreaterThan(4), "no Japanese font to measure with");
            const string text = "ドラッグか矢印キー（Shift で 10 px）で、レイヤーとそのマスクを動かします。選択範囲があれば、選択した画素と選択範囲を動かします。";
            float width = em * 13;
            var lines = PaintGui.WrapLines(text, width, style);
            Assert.That(lines.Length, Is.GreaterThan(2));
            Assert.That(string.Concat(lines).Replace(" ", ""), Is.EqualTo(text.Replace(" ", "").Replace(" ", "")), "nothing is lost or added");
            Assert.That(lines.Any(l => l.Contains("10 px")), Is.True, "a no-break space keeps the number with its unit");
            foreach (var line in lines)
            {
                Assert.That("、。）」ーぁっゃ".IndexOf(line[0]), Is.EqualTo(-1), "a line starts with a closing mark: " + line);
                Assert.That(line[line.Length - 1], Is.Not.EqualTo('（'), "a line ends with an opening bracket: " + line);
            }
            // IMGUI の wordWrap は空白でしか折らないので、空白の無い長い日本語は次の行へ送られて右が大きく空く。ここでは行が詰まる
            for (int i = 0; i < lines.Length - 1; i++)
                Assert.That(PaintGui.TextWidth(lines[i], style), Is.GreaterThan(width - 3 * em), "line " + i + " was left short: " + lines[i]);
        }

        [Test] public void EnglishParagraphsBreakOnlyAtSpaces()
        {
            var style = PaintTheme.Wrap;
            Assume.That(PaintGui.TextWidth("move", style), Is.GreaterThan(10), "no font to measure with");
            const string text = "Drag or arrow keys move the layer and its mask, or the selected pixels with the selection.";
            float width = PaintGui.TextWidth("the layer and its mask,", style) + 2;
            var lines = PaintGui.WrapLines(text, width, style);
            Assert.That(lines.Length, Is.GreaterThan(2));
            Assert.That(string.Join(" ", lines), Is.EqualTo(text), "words are kept whole");
            foreach (var line in lines) Assert.That(PaintGui.TextWidth(line, style), Is.LessThanOrEqualTo(width + .5f), line);
        }
    }
}
