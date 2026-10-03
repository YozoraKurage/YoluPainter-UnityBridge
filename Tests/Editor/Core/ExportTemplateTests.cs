using System;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>書き出しのテンプレート（ExportTemplates）: 詰め合わせの値（値は r × a、Smoothness は 1 − Roughness、使っていないチャンネルは既定）、
    /// Emission はアルファを掛けて不透明、AO の入れ方、書く画像の選び方。</summary>
    public sealed class ExportTemplateTests
    {
        const int W = 8, H = 4;

        static PaintDocument Document(params (PaintChannel channel, Func<int, int, (byte r, byte g, byte b, byte a)> at)[] layers)
        {
            var doc = new PaintDocument(W, H, 4);
            foreach (var (channel, at) in layers)
            {
                var layer = doc.AddLayer(channel.ToString());
                if (!layer.IsChannelEnabled(channel)) doc.SetChannelEnabled(layer.Id, channel, true);
                if (channel != PaintChannel.Color && layer.IsChannelEnabled(PaintChannel.Color)) doc.SetChannelEnabled(layer.Id, PaintChannel.Color, false); // 新しいレイヤーは Color も使う
                var image = new byte[W * H * 4];
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { var (r, g, b, a) = at(x, y); int o = (y * W + x) * 4; image[o] = r; image[o + 1] = g; image[o + 2] = b; image[o + 3] = a; }
                doc.ReplacePixels(layer.Id, channel, image, false);
            }
            return doc;
        }
        static (byte, byte, byte, byte) Px(byte[] image, int x, int y) { int o = (y * W + x) * 4; return (image[o], image[o + 1], image[o + 2], image[o + 3]); }

        [Test] public void MetallicAndSmoothnessArePackedFromValueTimesAlpha()
        {
            // Metallic: 左半分だけ 200（不透明）。Roughness: 全面 100、アルファ 128 → r × a = 50 → Smoothness 205
            var doc = Document((PaintChannel.Metallic, (x, y) => x < 4 ? ((byte)200, (byte)200, (byte)200, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)),
                               (PaintChannel.Roughness, (x, y) => ((byte)100, (byte)100, (byte)100, (byte)128)));
            var standard = ExportTemplate.UnityStandard.Images.Single(i => i.Suffix == "MetallicSmoothness");
            var packed = ExportTemplates.Build(doc, standard);
            Assert.That(Px(packed, 1, 1), Is.EqualTo(((byte)200, (byte)200, (byte)200, (byte)205)));
            Assert.That(Px(packed, 6, 1), Is.EqualTo(((byte)0, (byte)0, (byte)0, (byte)205)), "unpainted metallic is 0");
            // HDRP の MaskMap: R Metallic・G AO（無ければ 1）・B 1・A Smoothness
            var mask = ExportTemplates.Build(doc, ExportTemplate.UnityHdrp.Images.Single(i => i.Suffix == "MaskMap"));
            Assert.That(Px(mask, 1, 1), Is.EqualTo(((byte)200, (byte)255, (byte)255, (byte)205)));
            var occlusion = Enumerable.Range(0, W * H).Select(i => (byte)(i * 7)).ToArray();
            mask = ExportTemplates.Build(doc, ExportTemplate.UnityHdrp.Images.Single(i => i.Suffix == "MaskMap"), occlusion);
            Assert.That(Px(mask, 3, 2).Item2, Is.EqualTo(occlusion[2 * W + 3]), "the baked AO goes into G");
        }

        [Test] public void UnusedChannelsTakeTheDefaultsAndUnreadImagesAreNotWritten()
        {
            var doc = Document((PaintChannel.Metallic, (x, y) => ((byte)90, (byte)90, (byte)90, (byte)255)));
            var packed = ExportTemplates.Build(doc, ExportTemplate.UnityStandard.Images.Single(i => i.Suffix == "MetallicSmoothness"));
            Assert.That(Px(packed, 0, 0), Is.EqualTo(((byte)90, (byte)90, (byte)90, (byte)128)), "no roughness painted: Unity's default smoothness 0.5");
            Assert.That(ExportTemplates.ShouldWrite(doc, ExportTemplate.UnityStandard.Images.Single(i => i.Suffix == "Height"), false), Is.False, "no height layer");
            Assert.That(ExportTemplates.ShouldWrite(doc, ExportTemplate.UnityStandard.Images.Single(i => i.Suffix == "Albedo"), false), Is.False, "no colour layer");
            var occlusionImage = ExportTemplate.UnityStandard.Images.Single(i => i.Suffix == "Occlusion");
            Assert.That(ExportTemplates.ShouldWrite(doc, occlusionImage, false), Is.False, "no AO baked");
            Assert.That(ExportTemplates.ShouldWrite(doc, occlusionImage, true), Is.True);
            Assert.That(ExportTemplates.ShouldWrite(doc, ExportTemplate.UnityStandard.Images.Single(i => i.Suffix == "MetallicSmoothness"), false), Is.True);
            Assert.That(ExportTemplates.ShouldWrite(doc, ExportTemplate.LilToon.Images.Single(i => i.Suffix == "Smoothness"), false), Is.False, "lilToon's smoothness reads only the roughness");
        }

        [Test] public void EmissionIsPremultipliedAndOpaqueAndColourIsTheComposite()
        {
            var doc = Document((PaintChannel.Color, (x, y) => ((byte)10, (byte)20, (byte)30, (byte)(x * 30))),
                               (PaintChannel.Emission, (x, y) => ((byte)255, (byte)100, (byte)0, (byte)128)));
            var emission = ExportTemplates.Build(doc, ExportTemplate.UnityStandard.Images.Single(i => i.Kind == ExportImageKind.Emission));
            Assert.That(Px(emission, 2, 2), Is.EqualTo(((byte)128, (byte)50, (byte)0, (byte)255)));
            Assert.That(ExportTemplates.Build(doc, ExportTemplate.UnityHdrp.Images.Single(i => i.Kind == ExportImageKind.BaseColor)), Is.EqualTo(doc.Composite(PaintChannel.Color)));
            Assert.That(ExportTemplates.Build(doc, ExportTemplate.LilToon.Images.Single(i => i.Kind == ExportImageKind.Normal)), Is.EqualTo(NormalMaps.Output(doc)));
            Assert.That(ExportTemplate.UnityStandard.Images.Single(i => i.Kind == ExportImageKind.Emission).Srgb, Is.True);
            Assert.That(ExportTemplate.UnityHdrp.Images.Single(i => i.Suffix == "MaskMap").Srgb, Is.False);
        }

        [Test] public void LilToonMatchesTheAssignmentsValues()
        {
            // lilToon の割り当て（LilToonAssignment.Convert）と同じ: 平滑度は 255 − r × a、Metallic は r × a の灰色
            var doc = Document((PaintChannel.Roughness, (x, y) => ((byte)(x * 30), 0, 0, (byte)(255 - y * 50))),
                               (PaintChannel.Metallic, (x, y) => ((byte)77, 0, 0, (byte)200)));
            var smooth = ExportTemplates.Build(doc, ExportTemplate.LilToon.Images.Single(i => i.Suffix == "Smoothness"));
            var expected = Yozolab.YoluPainter.Editor.LilToonApply.LilToonAssignment.Convert(PaintChannel.Roughness, doc.Composite(PaintChannel.Roughness));
            Assert.That(smooth, Is.EqualTo(expected));
            var metallic = ExportTemplates.Build(doc, ExportTemplate.LilToon.Images.Single(i => i.Suffix == "Metallic"));
            Assert.That(metallic, Is.EqualTo(Yozolab.YoluPainter.Editor.LilToonApply.LilToonAssignment.Convert(PaintChannel.Metallic, doc.Composite(PaintChannel.Metallic))));
        }

        [Test] public void EveryTemplateHasDistinctSuffixesAndBadInputsAreRefused()
        {
            foreach (var template in ExportTemplate.BuiltIn)
                Assert.That(template.Images.Select(i => i.Suffix).Distinct().Count(), Is.EqualTo(template.Images.Count), template.Name);
            Assert.That(ExportTemplate.BuiltIn.Select(t => t.Id).Distinct().Count(), Is.EqualTo(ExportTemplate.BuiltIn.Count));
            var doc = new PaintDocument(W, H, 4);
            Assert.Throws<ArgumentException>(() => ExportTemplates.Build(doc, ExportTemplate.UnityHdrp.Images.Single(i => i.Suffix == "MaskMap"), new byte[3]));
            Assert.Throws<ArgumentNullException>(() => ExportTemplates.Build(null, ExportTemplate.UnityHdrp.Images[0]));
        }
    }
}
