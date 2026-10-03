using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>書き出しのテンプレートの窓の側: 読むものがある画像だけを &lt;名前&gt;_&lt;画像&gt;.png で書き、中身は Core の詰め合わせ（モデルがあれば
    /// パディングを掛ける）、AO が無ければ知らせる。Assets の外へ書くので、取り込み設定とマテリアルには触れない。</summary>
    public sealed class ExportTemplateWindowTests
    {
        string project, folder;

        [SetUp] public void UseTemporaryFolders()
        {
            project = Path.Combine(Path.GetTempPath(), "yolupainter-template-" + Guid.NewGuid().ToString("N"));
            folder = Path.Combine(project, "out");
            Directory.CreateDirectory(project); PainterSettings.ProjectRoot = project;
        }
        [TearDown] public void Restore()
        {
            PainterSettings.ProjectRoot = null;
            if (Directory.Exists(project)) Directory.Delete(project, true);
        }

        static Color32[] Read(string path)
        {
            var texture = new Texture2D(2, 2);
            try { Assert.That(texture.LoadImage(File.ReadAllBytes(path)), Is.True, path); return texture.GetPixels32(); }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test] public void TheTemplatesImagesAreWrittenWithTheCoresPacking()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                var doc = w.Document; int n = doc.Width * doc.Height;
                var metal = doc.AddLayer("Metal"); doc.SetChannelEnabled(metal.Id, PaintChannel.Metallic, true);
                var image = new byte[n * 4];
                for (int i = 0; i < n; i++) { image[i * 4] = (byte)(i % 256); image[i * 4 + 3] = 255; }
                doc.ReplacePixels(metal.Id, PaintChannel.Metallic, image, false);

                string status = w.ExportTemplateTo(folder, ExportTemplate.UnityStandard, "Tex");
                Assert.That(Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(x => x), Is.EqualTo(new[] { "Tex_Albedo.png", "Tex_MetallicSmoothness.png" }),
                    "only the images that read a used channel (the first layer uses Color; no height, normal, emission or AO)");
                Assert.That(status, Does.Contain("no current AO bake").And.Contain("load the model").And.Contain("No material was changed"));
                var expected = ExportTemplates.Build(doc, ExportTemplate.UnityStandard.Images.Single(i => i.Suffix == "MetallicSmoothness"));
                var written = Read(Path.Combine(folder, "Tex_MetallicSmoothness.png"));
                for (int i = 0; i < n; i += 97)
                    Assert.That((written[i].r, written[i].a), Is.EqualTo((expected[i * 4], expected[i * 4 + 3])), "texel " + i);

                // モデルがあればパディング: UV の外は塗り広げられ、UV の中はそのまま
                w.Preview.LoadDemoMesh();
                w.ExportTemplateTo(folder, ExportTemplate.UnityHdrp, "Tex");
                Assert.That(File.Exists(Path.Combine(folder, "Tex_MaskMap.png")), Is.True);
                var coverage = w.ExportCoverage(w.CurrentTextureSet);
                var mask = Read(Path.Combine(folder, "Tex_MaskMap.png"));
                var maskExpected = ExportTemplates.Build(doc, ExportTemplate.UnityHdrp.Images.Single(i => i.Suffix == "MaskMap"));
                int kept = 0, padded = 0;
                for (int i = 0; i < n; i++)
                {
                    if (coverage[i]) { Assert.That(mask[i].r, Is.EqualTo(maskExpected[i * 4]), "a covered texel keeps its value"); kept++; }
                    else if (mask[i].r != maskExpected[i * 4]) padded++;
                }
                Assert.That(kept, Is.GreaterThan(0)); Assert.That(padded, Is.GreaterThan(0), "texels outside the UVs are padded");
            }
            finally { string recovery = w.RecoveryRoot; Object.DestroyImmediate(w); if (Directory.Exists(recovery)) Directory.Delete(recovery, true); }
        }

        [Test] public void NothingIsPlannedWhenNoLayerUsesWhatTheTemplateReads()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                // 新しい文書のレイヤーは Color だけ: lilToon の Main だけが書かれ、HDRP は BaseColor だけ
                Assert.That(w.PlanTemplateExport(ExportTemplate.LilToon, "T").Select(p => p.name), Is.EqualTo(new[] { "T_Main.png" }));
                Assert.That(w.PlanTemplateExport(ExportTemplate.UnityHdrp, "T").Select(p => p.name), Is.EqualTo(new[] { "T_BaseColor.png" }));
            }
            finally { string recovery = w.RecoveryRoot; Object.DestroyImmediate(w); if (Directory.Exists(recovery)) Directory.Delete(recovery, true); }
        }
    }
}
