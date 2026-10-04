using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>書き出しのパディング: プロジェクトの設定（既定は届くかぎり全部、選べる値だけ、以前のファイルは全部）と、窓の書き出しの画像への掛け方
    /// （UV が覆うテクセルは変えず、その外だけを埋める。モデルが無ければ掛けずに知らせる）。</summary>
    public sealed class ExportPaddingTests
    {
        string project;

        [SetUp] public void UseTemporaryProject()
        {
            project = Path.Combine(Path.GetTempPath(), "yolupainter-padding-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(project); PainterSettings.ProjectRoot = project;
        }
        [TearDown] public void RestoreProject()
        {
            PainterSettings.ProjectRoot = null;
            if (Directory.Exists(project)) Directory.Delete(project, true);
        }

        static void SetPadding(string project, int texels)
        {
            var shared = PainterSettings.SharedSettings; shared.exportPadding = texels; PainterSettings.Save(shared, null);
            PainterSettings.ProjectRoot = project; // 読み直す
        }

        [Test] public void ThePaddingSettingDefaultsToFillAndOnlyTakesTheOfferedValues()
        {
            Assert.That(PainterSettings.ExportPadding, Is.EqualTo(PainterSettings.ExportPaddingFill), "by default the padding fills all the way");
            var shared = PainterSettings.SharedSettings; shared.exportPadding = 5;
            Assert.That(() => PainterSettings.Save(shared, null), Throws.ArgumentException.With.Message.Contains("Export padding"));
            SetPadding(project, 16);
            Assert.That(PainterSettings.ExportPadding, Is.EqualTo(16));
            Assert.That(File.ReadAllText(PainterSettings.SharedPath), Does.Contain("\"exportPadding\": 16"));
            // この項目の無い以前のファイルは「全部」
            File.WriteAllText(PainterSettings.SharedPath, "{\"schema\": 1, \"defaultResolution\": 1024, \"projectBrushFolder\": \"\"}");
            PainterSettings.ProjectRoot = project;
            Assert.That(PainterSettings.ExportPadding, Is.EqualTo(PainterSettings.ExportPaddingFill));
        }

        [Test] public void ExportedImagesArePaddedOnlyOutsideTheUvs()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                var set = w.CurrentTextureSet; var d = set.Document;
                var notes = new List<string>(); var unchanged = new byte[d.Width * d.Height * 4];
                Assert.That(w.PadForExport(set, unchanged, notes), Is.SameAs(unchanged), "no model: no padding");
                Assert.That(notes.Single(), Does.Contain("no model in the 3D view"));

                w.Preview.LoadDemoMesh();
                var coverage = w.ExportCoverage(set);
                int covered = coverage.Count(c => c);
                Assert.That(covered, Is.GreaterThan(0).And.LessThan(coverage.Length), "the demo cube's UVs leave gaps");
                // UV の中は橙、外は透明な黒
                var image = new byte[coverage.Length * 4];
                for (int i = 0; i < coverage.Length; i++) if (coverage[i]) { image[i * 4] = 200; image[i * 4 + 1] = 90; image[i * 4 + 2] = 30; image[i * 4 + 3] = 255; }

                var filled = w.PadForExport(set, image, notes = new List<string>());
                Assert.That(notes, Is.Empty);
                for (int i = 0; i < coverage.Length; i++)
                {
                    if (coverage[i]) Assert.That(filled.Skip(i * 4).Take(4), Is.EqualTo(image.Skip(i * 4).Take(4)), "a covered texel is kept");
                    else Assert.That((filled[i * 4], filled[i * 4 + 3]), Is.EqualTo(((byte)200, (byte)255)), "texel " + (i % d.Width) + "," + (i / d.Width) + " is filled");
                }

                SetPadding(project, 2);
                var two = w.PadForExport(set, image, null);
                int grown = 0, far = 0;
                for (int i = 0; i < coverage.Length; i++)
                {
                    if (coverage[i]) continue;
                    if (two[i * 4 + 3] != 0) { grown++; continue; }
                    far++;
                }
                Assert.That(grown, Is.GreaterThan(0), "two rings are filled"); Assert.That(far, Is.GreaterThan(0), "texels further out stay as they were");
                Assert.That(image.Where((b, k) => k % 4 == 3).Count(a => a != 0), Is.EqualTo(covered), "the input image is not changed");

                SetPadding(project, 0);
                Assert.That(w.PadForExport(set, image, null), Is.SameAs(image), "padding off");
            }
            finally { string recovery = w.RecoveryRoot; Object.DestroyImmediate(w); if (Directory.Exists(recovery)) Directory.Delete(recovery, true); }
        }
    }
}
