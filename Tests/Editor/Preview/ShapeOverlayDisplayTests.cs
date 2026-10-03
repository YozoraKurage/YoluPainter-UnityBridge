using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>3D ビューの形のグラデーションの重ね表示（シェーダーが要る。batch-gl）: 形の中の面だけに色が乗り、外は変わらず、硬い縁は
    /// 形の境で切れ、別のスロットには乗らず、隠せば元の絵に戻り、当たり判定には入らない。</summary>
    [Category("GPU")]
    public sealed class ShapeOverlayDisplayTests
    {
        const int Size = 192;
        static readonly Color Tint = new Color(1, .55f, .12f, .55f);

        [Test] public void TheValueTintsOnlyTheFacesInsideTheShapeAndHidingItRestoresTheImage()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface");
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/ShapeGradientOverlay");
            using (var preview = new IsolatedModelPreview())
            {
                Assert.That(preview.LoadDemoMesh().CanPaint, Is.True);
                var plain = preview.RenderStatic(Size, Size);
                // キューブの +X の半分を覆う硬い縁のボックス
                var g = GeneratorSettings.Default(GeneratorType.ShapeGradient).WithVolume(new ShapeVolume(GeneratorShape.Box, .5, 0, 0, 0, 0, 0, 1.02, 2, 2, 0));
                preview.ShownShapeGradient = ShapeGradientOverlay.Of(g, preview.ModelRootPosition, preview.ModelRootRotation, 0, Tint);
                var shown = preview.RenderStatic(Size, Size);
                Assert.That(preview.ShapeOverlayDrawn, Is.True);
                // 別のスロットの面には乗らない（デモのキューブはスロット 0 だけ）
                preview.ShownShapeGradient = ShapeGradientOverlay.Of(g, preview.ModelRootPosition, preview.ModelRootRotation, 1, Tint);
                var otherSlot = preview.RenderStatic(Size, Size);
                Assert.That(preview.ShapeOverlayDrawn, Is.False);
                // 反転すると外側に乗る
                preview.ShownShapeGradient = ShapeGradientOverlay.Of(g.WithInvert(true), preview.ModelRootPosition, preview.ModelRootRotation, 0, Tint);
                var inverted = preview.RenderStatic(Size, Size);
                preview.ShownShapeGradient = null;
                var hidden = preview.RenderStatic(Size, Size);
                try
                {
                    Assert.That(hidden.GetPixels32(), Is.EqualTo(plain.GetPixels32()), "hiding the overlay restores the image");
                    Assert.That(otherSlot.GetPixels32(), Is.EqualTo(plain.GetPixels32()), "another texture set's faces are not tinted");
                    Color32 Pixel(Texture2D t, Vector3 world)
                    {
                        Assert.That(preview.TryWorldToGui(new Rect(0, 0, Size, Size), world, out var gui), Is.True);
                        return t.GetPixel(Mathf.FloorToInt(gui.x), Size - 1 - Mathf.FloorToInt(gui.y));
                    }
                    int Change(Texture2D a, Texture2D b, Vector3 world) { Color32 p = Pixel(a, world), q = Pixel(b, world); return Mathf.Abs(p.r - q.r) + Mathf.Abs(p.g - q.g) + Mathf.Abs(p.b - q.b); }
                    var inside = new Vector3(.25f, .5f, 0); var outside = new Vector3(-.25f, .5f, 0);
                    Assert.That(Change(plain, shown, inside), Is.GreaterThan(30), "the top face inside the box is tinted");
                    Assert.That(Pixel(shown, inside).r, Is.GreaterThan(Pixel(shown, inside).b), "an orange tint");
                    Assert.That(Change(plain, shown, outside), Is.LessThanOrEqualTo(3), "outside the box is unchanged");
                    Assert.That(Change(plain, inverted, inside), Is.LessThanOrEqualTo(3), "inverted: inside is 0");
                    Assert.That(Change(plain, inverted, outside), Is.GreaterThan(30), "inverted: outside is 1");
                }
                finally { foreach (var t in new[] { plain, shown, otherSlot, inverted, hidden }) Object.DestroyImmediate(t); }

                // 当たり判定は幾何だけを見る
                var view = new Rect(0, 0, Size, Size);
                preview.ShownShapeGradient = ShapeGradientOverlay.Of(g, preview.ModelRootPosition, preview.ModelRootRotation, 0, Tint);
                Assert.That(preview.TryPick(view, view.center, out var withOverlay), Is.True);
                preview.ShownShapeGradient = null;
                Assert.That(preview.TryPick(view, view.center, out var without), Is.True);
                Assert.That(withOverlay.TriangleIndex, Is.EqualTo(without.TriangleIndex));
            }
        }
    }
}
