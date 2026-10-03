using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>3D プレビューの照明にノーマルマップを使う: 傾いた法線で明るさが変わり、平らな法線では変わらず、外せば元どおり。シェーダーが要る（batch-gl）。</summary>
    [Category("GPU")]
    public sealed class PreviewLightingTests
    {
        static Texture2D Solid(Color32 c)
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
            t.SetPixels32(Enumerable.Repeat(c, 16).ToArray()); t.Apply(); return t;
        }
        static float Mean(Texture2D image) => image.GetPixels32().Average(p => (p.r + p.g + p.b) / 3f);

        [Test] public void ANormalMapChangesTheLightingAndAFlatOneDoesNot()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface");
            var white = Solid(new Color32(255, 255, 255, 255)); var flat = Solid(new Color32(128, 128, 255, 255)); var tilted = Solid(new Color32(240, 128, 160, 255));
            using (var preview = new IsolatedModelPreview())
            {
                Assert.That(preview.LoadDemoMesh().CanPaint, Is.True);
                preview.SetPaintTexture(white, 0);
                var plain = preview.RenderStatic(96, 96); float basis = Mean(plain);
                preview.SetNormalTexture(flat, 0); var withFlat = preview.RenderStatic(96, 96);
                preview.SetNormalTexture(tilted, 0); var withTilt = preview.RenderStatic(96, 96);
                preview.SetNormalTexture(null); var back = preview.RenderStatic(96, 96);
                Assert.That(Mathf.Abs(Mean(withFlat) - basis), Is.LessThan(2f), "a flat normal map lights like the mesh normals");
                Assert.That(Mathf.Abs(Mean(withTilt) - basis), Is.GreaterThan(4f), "a tilted normal map changes the shading");
                Assert.That(back.GetPixels32(), Is.EqualTo(plain.GetPixels32()), "removing it restores the image");
                foreach (var o in new Object[] { plain, withFlat, withTilt, back }) Object.DestroyImmediate(o);
            }
            foreach (var o in new Object[] { white, flat, tilted }) Object.DestroyImmediate(o);
        }
    }
}
