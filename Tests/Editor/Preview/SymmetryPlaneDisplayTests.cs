using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>3D ビューの対称の面の表示（シェーダーが要る。batch-gl）: 面は奥行きを見て薄く重なり（カメラとの間に面がある所だけ色が変わる）、
    /// 隠せば元の絵に戻り、当たり判定には入らない。</summary>
    [Category("GPU")]
    public sealed class SymmetryPlaneDisplayTests
    {
        const int Size = 192;

        [Test] public void ThePlaneIsDrawnFaintlyWithDepthAndHidingItRestoresTheImage()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/PreviewSurface");
            using (var preview = new IsolatedModelPreview())
            {
                Assert.That(preview.LoadDemoMesh().CanPaint, Is.True);
                var plain = preview.RenderStatic(Size, Size);
                preview.ShownSymmetryPlane = preview.SymmetryPlane(SymmetryAxis.X, 0);
                var shown = preview.RenderStatic(Size, Size);
                Assert.That(preview.SymmetryPlaneVisible, Is.True);
                preview.ShownSymmetryPlane = null;
                var hidden = preview.RenderStatic(Size, Size);
                Assert.That(preview.SymmetryPlaneVisible, Is.False);
                try
                {
                    Assert.That(hidden.GetPixels32(), Is.EqualTo(plain.GetPixels32()), "hiding the plane restores the image");
                    // 既定のカメラは −X・+Y・−Z の側から見る。x = 0 の面より手前（x < 0）の上の面は変わらず、面の奥（x > 0）は面越しに見え、
                    // 箱の上の背景の所（面の上の点）にも面が見える
                    Color32 Pixel(Texture2D t, Vector3 world)
                    {
                        Assert.That(preview.TryWorldToGui(new Rect(0, 0, Size, Size), world, out var gui), Is.True);
                        return t.GetPixel(Mathf.FloorToInt(gui.x), Size - 1 - Mathf.FloorToInt(gui.y));
                    }
                    int Change(Vector3 world) { Color32 a = Pixel(plain, world), b = Pixel(shown, world); return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b); }
                    Assert.That(Change(new Vector3(-.25f, .5f, 0)), Is.LessThanOrEqualTo(3), "the top face in front of the plane is not covered");
                    Assert.That(Change(new Vector3(.25f, .5f, 0)), Is.GreaterThan(12), "the top face behind the plane is seen through it");
                    Assert.That(Change(new Vector3(0, .85f, 0)), Is.GreaterThan(12), "the plane shows above the cube");
                    Assert.That(Pixel(shown, new Vector3(.25f, .5f, 0)).b, Is.GreaterThan(Pixel(plain, new Vector3(.25f, .5f, 0)).b), "a light blue tint");
                }
                finally { Object.DestroyImmediate(plain); Object.DestroyImmediate(shown); Object.DestroyImmediate(hidden); }

                // 当たり判定とブラシは幾何だけを見る（面を見せても変わらない）
                preview.ShownSymmetryPlane = preview.SymmetryPlane(SymmetryAxis.X, 0);
                var view = new Rect(0, 0, Size, Size);
                Assert.That(preview.TryPick(view, view.center, out var withPlane), Is.True);
                preview.ShownSymmetryPlane = null;
                Assert.That(preview.TryPick(view, view.center, out var without), Is.True);
                Assert.That(withPlane.TriangleIndex, Is.EqualTo(without.TriangleIndex));
                Assert.That(withPlane.Position, Is.EqualTo(without.Position));
            }
        }

        [Test] public void ThePlaneFollowsTheLoadedModelsRoot()
        {
            var source = new GameObject("Rotated symmetric model") { hideFlags = HideFlags.HideAndDontSave };
            var mesh = SymmetricBox.Mesh(SymmetricBox.Triangles());
            var material = new Material(Shader.Find("Hidden/YoluPainter/PreviewSurface")) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                source.transform.SetPositionAndRotation(new Vector3(3, 4, 5), Quaternion.Euler(0, 90, 0));
                source.AddComponent<MeshFilter>().sharedMesh = mesh; source.AddComponent<MeshRenderer>().sharedMaterial = material;
                using (var preview = new IsolatedModelPreview())
                {
                    Assert.That(preview.Load(source).CanPaint, Is.True);
                    var plane = preview.SymmetryPlane(SymmetryAxis.X, 0);
                    Assert.That(Vector3.Distance(plane.Normal, Quaternion.Euler(0, 90, 0) * Vector3.right), Is.LessThan(1e-5f), "the model's local X, not the world's");
                    Assert.That(plane.SignedDistance(Vector3.zero), Is.EqualTo(0).Within(1e-5f), "the root is the snapshot origin");
                    // 箱の右の前の面の点を映すと、左の前の面の対応する点（スナップショットの空間で）
                    var right = Quaternion.Euler(0, 90, 0) * new Vector3(.4f, .1f, -1); var left = Quaternion.Euler(0, 90, 0) * new Vector3(-.4f, .1f, -1);
                    Assert.That(Vector3.Distance(plane.Reflect(right), left), Is.LessThan(1e-5f));
                    Assert.That(preview.Geometry.TryFindClosestPoint(plane.Reflect(right), .1f, default, SurfaceSymmetry.MaxClosestPointNodeVisits, out var hit, out _), Is.True);
                    Assert.That(hit.Distance, Is.LessThan(1e-4f));
                    // 読み直すと（デモのキューブ）ルートは原点と回転なしに戻る
                    preview.LoadDemoMesh();
                    Assert.That(preview.ModelRootRotation, Is.EqualTo(Quaternion.identity));
                    Assert.That(preview.ModelRootPosition, Is.EqualTo(Vector3.zero));
                }
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(mesh); Object.DestroyImmediate(material); }
        }
    }
}
