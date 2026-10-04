using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>描画ウィンドウのポーズ: ストロークの最中は形の世代を変えず、終わったら焼き直す。元のモデルは変わらない。</summary>
    public sealed partial class WindowTests
    {
        [Test] public void ThePoseCannotChangeDuringAStroke()
        {
            var root = new GameObject("WindowPoseSource") { hideFlags = HideFlags.HideAndDontSave };
            var bone = new GameObject("bone"); bone.hideFlags = HideFlags.HideAndDontSave; bone.transform.SetParent(root.transform, false);
            var mesh = new Mesh { vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) }, uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }, triangles = new[] { 0, 2, 1, 1, 2, 3 } };
            mesh.boneWeights = new BoneWeight[4].Select4(new BoneWeight { boneIndex0 = 0, weight0 = 1 });
            mesh.bindposes = new[] { Matrix4x4.identity };
            var delta = new Vector3[4]; delta[0] = new Vector3(0, 0, -.5f); mesh.AddBlendShapeFrame("dent", 100, delta, null, null); mesh.RecalculateNormals();
            var skin = root.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh; skin.bones = new[] { bone.transform }; skin.rootBone = bone.transform;
            try
            {
                var report = window.Preview.Load(root);
                Assert.That(report.CanPaint, Is.True, string.Join("; ", report.Diagnostics));
                Repaint(window);
                int revision = window.Preview.SnapshotRevision;
                var center = window.SurfaceRect.center;
                Mouse(window, EventType.MouseDown, center);
                Assert.That(window.IsStroking, Is.True, window.StatusMessage);
                window.Preview.SetBlendShapeWeight(window.Preview.BlendShapes[0], 100);
                window.ApplyPoseNow();
                Assert.That(window.StatusMessage, Does.Contain("A stroke is in progress"));
                Assert.That(window.Preview.SnapshotRevision, Is.EqualTo(revision), "the surface generation does not change mid-stroke");
                Mouse(window, EventType.MouseUp, center);
                window.ApplyPoseNow();
                Assert.That(window.Preview.SnapshotRevision, Is.GreaterThan(revision));
                Assert.That(skin.GetBlendShapeWeight(0), Is.Zero, "the model itself is unchanged");
                Assert.That(window.Document.CanUndo, Is.True, "the stroke painted on the skinned surface");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }
    }

    static class BoneWeightArrays { public static BoneWeight[] Select4(this BoneWeight[] a, BoneWeight w) { for (int i = 0; i < a.Length; i++) a[i] = w; return a; } }
}
