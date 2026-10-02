using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Brushes;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>同梱のブラシセット、取り込んだブラシの置き場、拡張子での読み分け、筆先 ID の対応。</summary>
    public sealed class BrushLibraryTests
    {
        string folder;

        [SetUp] public void UseTemporaryLibrary()
        {
            folder = Path.Combine(Path.GetTempPath(), "yolupainter-brushes-" + Guid.NewGuid().ToString("N"));
            BrushLibrary.Folder = folder;
        }

        [TearDown] public void RestoreLibrary()
        {
            BrushLibrary.Folder = null;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        static BrushTip Tip(string name, int width, int height, int seed)
        {
            var alpha = new byte[width * height]; var random = new System.Random(seed); random.NextBytes(alpha);
            return new BrushTip(name, width, height, alpha);
        }
        static void AssertSamePixels(BrushTip actual, BrushTip expected)
        {
            Assert.That(actual.Width, Is.EqualTo(expected.Width)); Assert.That(actual.Height, Is.EqualTo(expected.Height));
            Assert.That(actual.CopyAlpha(), Is.EqualTo(expected.CopyAlpha()));
        }
        static PaintDocument PaintWith(BrushSettings settings)
        {
            var d = new PaintDocument(96, 96, 32); var layer = d.AddLayer("L").Id;
            settings.Color = new Rgba32(0, 0, 0); settings.PressureSize = false; settings.PressureOpacity = false;
            using (var s = d.BeginStroke(layer, PaintChannel.Color, settings)) { s.Add(new BrushSample(20, 48, 1, 0)); s.Add(new BrushSample(76, 48, 1, .1)); s.Commit(); }
            return d;
        }
        static int Painted(PaintDocument d)
        { var rgba = d.Composite(PaintChannel.Color); int n = 0; for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] > 0) n++; return n; }

        [Test] public void TheBundledKritaSetLoadsCompletelyAndMatchesItsChecksums()
        {
            string set = Path.Combine(BundledBrushSets.PackageRoot, "BrushSets~", "Krita4Default");
            foreach (var file in new[] { "README.md", "meta.xml", "SHA256SUMS" }) Assert.That(File.Exists(Path.Combine(set, file)), file);
            Assert.That(File.ReadAllText(Path.Combine(set, "meta.xml")), Does.Contain("CC-0"), "the licence statement stays with the files");
            var sums = File.ReadAllLines(Path.Combine(set, "SHA256SUMS")).Where(l => l.Trim().Length > 0).Select(l => l.Split(new[] { ' ' }, 2)).ToDictionary(p => p[1].Trim().TrimStart('*'), p => p[0]);
            var files = Directory.GetFiles(Path.Combine(set, "brushes")).Select(Path.GetFileName).ToList();
            CollectionAssert.AreEquivalent(sums.Keys.Select(k => k.Replace("brushes/", "")), files, "every bundled file is listed and nothing extra is shipped");
            using (var sha = SHA256.Create())
                foreach (var file in files)
                {
                    string key = sums.ContainsKey("brushes/" + file) ? "brushes/" + file : file;
                    string actual = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(set, "brushes", file)))).Replace("-", "").ToLowerInvariant();
                    Assert.That(actual, Is.EqualTo(sums[key]), file + " is the unmodified upstream file");
                }
            Assert.That(BundledBrushSets.LoadWarnings, Is.Empty);
            Assert.That(BundledBrushSets.Presets.Count, Is.EqualTo(files.Count), "one preset per tip file");
            Assert.That(BundledBrushSets.Presets.Count(p => p.CreateSettings().Tips != null), Is.GreaterThan(0), "the .gih hoses keep their several tips");
        }

        [Test] public void EveryBundledBrushPaintsAndKeepsItsId()
        {
            foreach (var preset in BundledBrushSets.Presets)
            {
                var s = preset.CreateSettings();
                Assert.That(s.Radius, Is.InRange(4, 40), preset.Name);
                Assert.That(Painted(PaintWith(s)), Is.GreaterThan(0), preset.Name + " leaves paint");
                string id = BrushTips.IdOf(preset.CreateSettings());
                Assert.That(id, Is.EqualTo(preset.Id), "the tip id resolves back to the bundled file");
                var applied = new BrushSettings(); BrushTips.Apply(applied, id);
                Assert.That(BrushTips.ResolveRef(id).Matches(applied), Is.True);
            }
        }

        [Test] public void PngTipsFollowTheDarkIsPaintConvention()
        {
            var texture = new Texture2D(3, 1, TextureFormat.RGBA32, false, true);
            string path = Path.Combine(folder + "-png", "dot.png"); Directory.CreateDirectory(Path.GetDirectoryName(path));
            try
            {
                texture.SetPixels32(new[] { new Color32(0, 0, 0, 255), new Color32(255, 255, 255, 255), new Color32(0, 0, 0, 0) }); texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
                var brush = BrushImport.ReadFile(path).Single();
                Assert.That(brush.Name, Is.EqualTo("Dot"));
                var tip = brush.Settings.Tip;
                Assert.That(new[] { tip[0, 0], tip[1, 0], tip[2, 0] }, Is.EqualTo(new byte[] { 255, 0, 0 }), "black paints; white and transparent do not");
            }
            finally { Object.DestroyImmediate(texture); Directory.Delete(Path.GetDirectoryName(path), true); }
        }

        [Test] public void FilesAreReadByExtensionAndUnsupportedOnesAreRefused()
        {
            string dir = folder + "-files"; Directory.CreateDirectory(dir);
            try
            {
                string gbr = Path.Combine(dir, "soft_round.gbr"); File.WriteAllBytes(gbr, GimpBrushTests.Gbr(2, 2, new byte[] { 255, 255, 0, 0 }, name: ""));
                var brush = BrushImport.ReadFile(gbr).Single();
                Assert.That(brush.Name, Is.EqualTo("Soft round"), "an unnamed brush takes the file name");
                string vbr = Path.Combine(dir, "v.VBR"); File.WriteAllText(vbr, "GIMP-VBR\n1.0\nHard\n10\n8\n1\n1\n0\n");
                Assert.That(BrushImport.ReadFile(vbr).Single().Settings.Radius, Is.EqualTo(8), "the extension is matched case-insensitively");
                foreach (var (file, reason) in new[] { ("a.sut", "Clip Studio"), ("a.kpp", "Krita brush presets"), ("a.myb", "Unsupported brush file type") })
                {
                    File.WriteAllBytes(Path.Combine(dir, file), new byte[16]);
                    Assert.That(() => BrushImport.ReadFile(Path.Combine(dir, file)), Throws.TypeOf<BrushImportException>().With.Message.Contains(reason), file);
                }
                Assert.That(() => BrushImport.ReadFile(Path.Combine(dir, "missing.abr")), Throws.TypeOf<BrushImportException>());
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test] public void ImportedBrushesSurviveAReloadWithTheirTipsAndSettings()
        {
            var hose = new BrushSettings { Tips = new[] { Tip("a", 5, 3, 1), Tip("b", 4, 4, 2) }, TipSelection = TipSelection.Sequential, Radius = 12, Spacing = .4, SizeJitter = .3, Scatter = 2, Count = 3, PressureFlow = true };
            var textured = new BrushSettings { Tip = Tip("t", 7, 2, 3), Texture = Tip("paper", 16, 16, 4), TextureDepth = .6, TextureScale = 2, Angle = 30, Roundness = .5, FollowDirection = true, Opacity = .7 };
            var added = BrushLibrary.Add(new[] { new ImportedBrush("Hose 葉", "test", hose, new[] { "note" }), new ImportedBrush("Paper", "test", textured) }, "Set");
            Assert.That(added.Select(p => p.Name), Is.EqualTo(new[] { "Hose 葉", "Paper" }));
            Assert.That(added.All(p => p.Category == "Set"));

            BrushLibrary.Folder = folder; // 読み直させる
            var presets = BrushLibrary.Presets;
            Assert.That(presets.Count, Is.EqualTo(2));
            var h = presets.Single(p => p.Name == "Hose 葉").CreateSettings();
            Assert.That(h.Tip, Is.Null); Assert.That(h.Tips.Length, Is.EqualTo(2)); Assert.That(h.TipSelection, Is.EqualTo(TipSelection.Sequential));
            AssertSamePixels(h.Tips[0], hose.Tips[0]); AssertSamePixels(h.Tips[1], hose.Tips[1]);
            Assert.That((h.Radius, h.Spacing, h.SizeJitter, h.Scatter, h.Count, h.PressureFlow), Is.EqualTo((12.0, .4, .3, 2.0, 3, true)));
            var t = presets.Single(p => p.Name == "Paper").CreateSettings();
            AssertSamePixels(t.Tip, textured.Tip); AssertSamePixels(t.Texture, textured.Texture);
            Assert.That((t.TextureDepth, t.TextureScale, t.Angle, t.Roundness, t.FollowDirection, t.Opacity), Is.EqualTo((.6, 2.0, 30.0, .5, true, .7)));

            // 筆先 ID は取り込んだブラシへ戻り、紙の質感にも ID がある
            string hoseId = BrushTips.IdOf(h), paperTexture = BrushTips.IdOf(t.Texture);
            Assert.That(hoseId, Does.StartWith("library:"));
            Assert.That(paperTexture, Does.EndWith(":texture"));
            AssertSamePixels(BrushTips.Resolve(paperTexture), textured.Texture);
            var applied = new BrushSettings(); BrushTips.Apply(applied, hoseId);
            Assert.That(applied.Tips, Is.EqualTo(h.Tips)); Assert.That(applied.TipSelection, Is.EqualTo(TipSelection.Sequential));
            Assert.That(Painted(PaintWith(h)), Is.GreaterThan(0));

            BrushLibrary.Remove(presets.Single(p => p.Name == "Hose 葉").Id);
            Assert.That(BrushLibrary.Presets.Select(p => p.Name), Is.EqualTo(new[] { "Paper" }));
            Assert.That(Directory.GetFiles(folder).Any(f => Path.GetFileName(f).StartsWith("Hose", StringComparison.Ordinal)), Is.False, "the settings and tip images are removed");
            var unknown = new BrushSettings(); BrushTips.Apply(unknown, hoseId);
            Assert.That(unknown.Tip, Is.Null, "a removed brush falls back to the round tip"); Assert.That(unknown.Tips, Is.Null);
        }

        [Test] public void ABrokenLibraryEntryIsSkippedWithAWarning()
        {
            BrushLibrary.Add(new[] { new ImportedBrush("Good", "test", new BrushSettings { Tip = Tip("g", 3, 3, 5) }) }, "");
            File.WriteAllText(Path.Combine(folder, "broken.json"), "{\"schema\":1,\"name\":\"Broken\",\"tipFile\":\"missing.png\"}");
            File.WriteAllText(Path.Combine(folder, "future.json"), "{\"schema\":99,\"name\":\"Future\"}");
            BrushLibrary.Folder = folder;
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("skipped imported brush broken.json"));
            Assert.That(BrushLibrary.Presets.Select(p => p.Name), Is.EqualTo(new[] { "Good" }), "unreadable and newer entries are not loaded as round brushes");
        }

        [Test] public void BuiltInTipIdsRoundTrip()
        {
            foreach (var preset in BuiltInBrushes.Presets)
            {
                var s = preset.CreateSettings();
                string id = BrushTips.IdOf(s);
                if (s.Tip == null) { Assert.That(id, Is.EqualTo("")); continue; }
                Assert.That(id, Does.StartWith("builtin:"), preset.Name);
                Assert.That(BrushTips.Resolve(id), Is.SameAs(s.Tip));
                Assert.That(BrushTips.Thumbnail(id), Is.Not.Null);
            }
            Assert.That(BrushTips.ResolveRef("builtin:does-not-exist"), Is.Null);
            Assert.That(BrushTips.Thumbnail(""), Is.Null, "the round tip has no image thumbnail");
        }
    }
}
