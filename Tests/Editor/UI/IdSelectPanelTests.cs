using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// ID の色で選ぶ UI の見た目（batch-gl でオフスクリーンに描く）: ツール「ID の色で選択」のオプションバーとプロパティの「ID マップ」の区画
    /// （ID マップが無いときの焼く口・あるときの元の名前）、Generator「ID の色」の欄（色の一覧・スポイトの入り切り・許容の幅、範囲の行は出さない）、
    /// ベイクの窓の ID の頁（元ごとの説明と「1 色になる」注意）を、英語と日本語、最小と大きい大きさで例外なく描け、UI の文字が詰められず
    /// （… が 0）、部品が描かれていること。描いても文書と焼く設定は変わらない。描いた PNG はテストプロジェクトの
    /// Logs/YoluPainterSnapshots/IdSelect に残す（見て確かめる用）。
    /// </summary>
    public sealed class IdSelectPanelTests
    {
        static string Folder => Path.GetFullPath(Path.Combine("Logs", "YoluPainterSnapshots", "IdSelect"));
        static readonly (int, int)[] WindowSizes = { (980, 640), (1600, 950) };
        static readonly (int, int)[] BakeSizes = { ((int)MeshBakeWindow.MinWidth, (int)MeshBakeWindow.MinHeight), (1100, 760) };
        string settingsRoot;

        [SetUp] public void UseTemporarySettings()
        {
            settingsRoot = Path.Combine(Path.GetTempPath(), "yolupainter-idselect-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(settingsRoot);
            PainterSettings.ProjectRoot = settingsRoot;
        }
        [TearDown] public void Restore()
        {
            L.OverrideLanguage(PainterLanguage.English); PainterSettings.ProjectRoot = null;
            if (Directory.Exists(settingsRoot)) Directory.Delete(settingsRoot, true);
        }

        [Test] public void TheToolTheGeneratorAndTheBakePageDrawInBothLanguagesWithoutCutText()
        {
            if (!Application.isBatchMode) Assert.Ignore("Offscreen drawing is checked on the batch-gl daemon (the GUI-mode editor draws the window itself).");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>(); var bake = ScriptableObject.CreateInstance<MeshBakeWindow>();
            try
            {
                bake.Attach(w);
                w.Preview.LoadDemoMesh(); w.MeshBakeProgress = (t, i, p) => false;
                var d = w.Document; int drawn = 0;
                void Window(string name, IEnumerable<string> toolControls, IEnumerable<string> layerControls)
                {
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        foreach (var (width, height) in WindowSizes)
                        {
                            string full = name + "-" + (language == PainterLanguage.English ? "en" : "ja") + "-" + width;
                            long revision = d.Revision; int undo = d.UndoCount; string bakeKey = Key(w.MeshBakeSettings);
                            PaintGui.ShortenedTexts = 0;
                            Render(w, width, height, full);
                            Assert.That(PaintGui.ShortenedTexts, Is.Zero, full + ": a UI text did not fit and was shortened with …");
                            Assert.That((d.Revision, d.UndoCount, Key(w.MeshBakeSettings)), Is.EqualTo((revision, undo, bakeKey)), full + ": drawing changed the document or the bake settings");
                            foreach (var id in toolControls) Assert.That(w.ToolControlScreenRects.Keys, Has.Member(id), full);
                            foreach (var id in layerControls) Assert.That(w.LayerControlPanelRects.Keys, Has.Member(id), full);
                            drawn++;
                        }
                    }
                }

                // ツール: ID マップが無いとき（焼く口と知らせ）と、焼いた後
                w.Tool = TexturePaintWindow.PaintTool.IdSelect; w.SetToolSectionsOpen(true); w.ToolControlScreenRects.Clear();
                Window("tool-no-map", new[] { "idselect-tolerance", "idselect-bake", "idselect-bake-panel" }, new string[0]);
                w.MeshBakeSettings.Maps = new[] { MeshMapKind.Id }; w.MeshBakeSettings.IdSource = MeshIdSource.UvIsland;
                Assert.That(w.BakeMeshMaps(), Is.EqualTo(MeshBakeStatus.Completed), w.StatusMessage);
                d.SetSelection(SelectionMask.FromIdColors(d, IdMapOf(w), new[] { FirstColours(w, 1)[0] }, 8)); d.ClearHistory();
                w.ToolControlScreenRects.Clear();
                Window("tool-baked", new[] { "idselect-tolerance", "idselect-bake-panel" }, new string[0]);
                Assert.That(w.ToolControlScreenRects.Keys, Has.No.Member("idselect-bake"), "the options bar offers the bake only without a usable map");

                // Generator「ID の色」: 色が無い・3 色、スポイトの入り切り
                w.Tool = TexturePaintWindow.PaintTool.Brush;
                var fill = d.AddFillLayer("Parts", new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, new Rgba32(220, 60, 40, 255) } });
                d.AddLayerMask(fill.Id); w.SelectedLayer = fill.Id;
                var gen = w.AddGenerator(FilterTarget.Mask, GeneratorType.IdColor); d.ClearHistory();
                w.SelectedFilter = gen.Id;
                w.SetSectionOpen("effect", true);
                Window("generator-empty", new string[0], new[] { "generator.id.pick", "generator.id.tolerance", "generator.blend" });
                d.SetFilterSettings(fill.Id, gen.Id, gen.Settings.WithGenerator(gen.Settings.Generator.WithIdColors(FirstColours(w, 3)).WithIdTolerance(12))); d.ClearHistory();
                Assert.That(d.GetGeneratorStatus(fill.Id, gen.Id).Active, Is.True, d.GetGeneratorStatus(fill.Id, gen.Id).Reason);
                Window("generator-colours", new string[0], new[] { "generator.id.color.0", "generator.id.color.2", "generator.id.remove.1", "generator.id.pick" });
                Assert.That(w.LayerControlPanelRects.Keys, Has.No.Member("generator.low").And.No.Member("generator.softness"), "a match is 0 or 1: no range rows");
                w.BeginIdColorPick(gen.Id);
                Window("generator-picking", new string[0], new[] { "generator.id.pick" });
                w.EndIdColorPick();
                foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                {
                    L.OverrideLanguage(language);
                    string name = "generator-section-" + (language == PainterLanguage.English ? "en" : "ja");
                    PaintGui.ShortenedTexts = 0; float used = 0;
                    OffscreenGui.RenderToPng(300, 900, () => used = w.DrawFilterSectionOnly(new Rect(0, 0, 300, 900)), Path.Combine(Folder, name + ".png"), PaintTheme.PanelBg);
                    Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": cut text");
                    Assert.That(used, Is.GreaterThan(120).And.LessThan(900), name + ": the section fits the picture");
                }

                // ベイクの窓の ID の頁: 元ごと
                bake.Page = (int)MeshMapKind.Id;
                foreach (var source in (MeshIdSource[])Enum.GetValues(typeof(MeshIdSource)))
                {
                    w.MeshBakeSettings.IdSource = source;
                    foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
                    {
                        L.OverrideLanguage(language);
                        foreach (var (width, height) in BakeSizes)
                        {
                            string name = "bake-" + source + "-" + (language == PainterLanguage.English ? "en" : "ja") + "-" + width;
                            string bakeKey = Key(w.MeshBakeSettings);
                            PaintGui.ShortenedTexts = 0;
                            OffscreenGui.RenderToPng(width, height, () => w.DrawMeshBakeWindow(new Rect(0, 0, width, height), bake), Path.Combine(Folder, name + ".png"), PaintTheme.PanelBg);
                            Assert.That(PaintGui.ShortenedTexts, Is.Zero, name + ": a UI text did not fit and was shortened with …");
                            Assert.That(Key(w.MeshBakeSettings), Is.EqualTo(bakeKey), name + ": drawing changed the bake settings");
                            drawn++;
                        }
                    }
                }
                Assert.That(drawn, Is.EqualTo(5 * 2 * 2 + Enum.GetValues(typeof(MeshIdSource)).Length * 2 * 2));
            }
            finally
            {
                Object.DestroyImmediate(bake);
                string recovery = w.RecoveryRoot; Object.DestroyImmediate(w);
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
            }
        }

        /// <summary>ベイクの窓から焼く ID マップは、窓で選んだ元（持ち主の設定）で分けたもの: 元ごとに部品の数が違い、由来の設定の文字列に
        /// 元が入る。高ポリが無いのにスロットかメッシュを選ぶと、1 色になると知らせ（焼いた結果も 1 部品）、元を替えると前の ID マップは古くなる。</summary>
        [Test] public void TheBakeWindowBakesTheIdSourceChosenInIt()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>(); var bake = ScriptableObject.CreateInstance<MeshBakeWindow>();
            try
            {
                bake.Attach(w); w.MeshBakeProgress = (t, i, p) => false;
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                typeof(TexturePaintWindow).GetMethod("CreateDocument", flags).Invoke(w, new object[] { 256 });
                typeof(TexturePaintWindow).GetMethod("BindDocument", flags).Invoke(w, null);
                w.Preview.LoadDemoMesh();
                w.MeshBakeSettings.Maps = new[] { MeshMapKind.Id }; bake.Page = (int)MeshMapKind.Id;
                var parts = new Dictionary<MeshIdSource, int>();
                foreach (var source in new[] { MeshIdSource.UvIsland, MeshIdSource.MeshPart, MeshIdSource.MaterialSlot })
                {
                    w.MeshBakeSettings.IdSource = source;
                    Assert.That(w.StartMeshBake(), Is.Null, w.StatusMessage);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (w.PumpMeshBake(20)) { Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(120), "the bake did not finish"); System.Threading.Thread.Sleep(2); }
                    var map = IdMapOf(w);
                    Assert.That(map.Provenance.SettingsKey, Is.EqualTo("source=" + source + ";algorithm=" + MeshBakeSettings.IdAlgorithm));
                    Assert.That(map.Provenance.Check(w.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Current));
                    parts[source] = w.LastMeshBakeReport.IdParts;
                }
                Assert.That(parts[MeshIdSource.UvIsland], Is.GreaterThan(parts[MeshIdSource.MeshPart]), "the demo cube has more UV islands than mesh parts");
                Assert.That(parts[MeshIdSource.MaterialSlot], Is.EqualTo(1), "one texture set without a high poly: one slot, one colour");
                w.MeshBakeSettings.IdSource = MeshIdSource.UvIsland;
                Assert.That(w.MeshMaps.Check(MeshMapKind.Id, w.CurrentMeshMapExpectation()).State, Is.EqualTo(MeshMapState.Stale), "another source makes the baked ID map stale");
            }
            finally
            {
                Object.DestroyImmediate(bake);
                string recovery = w.RecoveryRoot; Object.DestroyImmediate(w);
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
            }
        }

        static string Key(MeshBakeSettings s) => string.Join("|", MeshBakeSettings.AllKinds.Select(s.KindKey)) + "|" + s.Padding + "|" + s.Antialiasing + "|" + string.Join(",", s.Maps);
        static BakedMeshMap IdMapOf(TexturePaintWindow w) { Assert.That(w.MeshMaps.TryGet(MeshMapKind.Id, out var map), Is.True); return map; }
        /// <summary>焼いた ID マップの、行の順に見つかる最初の count 色。</summary>
        static int[] FirstColours(TexturePaintWindow w, int count)
        {
            var map = IdMapOf(w); var found = new List<int>();
            for (int y = 0; y < map.Height && found.Count < count; y += 4)
                for (int x = 0; x < map.Width && found.Count < count; x += 4)
                    if (IdMapColors.TryGet(map, x, y, out int rgb) && !found.Contains(rgb)) found.Add(rgb);
            Assert.That(found.Count, Is.EqualTo(count), "the demo cube's ID map has that many parts");
            return found.ToArray();
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
    }
}
