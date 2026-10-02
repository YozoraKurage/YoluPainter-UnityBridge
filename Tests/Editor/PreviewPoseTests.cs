using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>プレビューのスキンメッシュ: Transform だけを複製した骨で動かし、BakeMesh した形を表示と当たり判定に使う。ポーズと BlendShape は
    /// 複製だけを変え、元のモデルには触れない。形を変えるとスナップショットの世代が進み、UV は変わらない。</summary>
    public sealed class PreviewPoseTests
    {
        GameObject source;
        Mesh skinMesh, staticMesh;

        /// <summary>root / bone0 / bone1 と、骨 2 本の四角形（上の 2 頂点が bone1、BlendShape "push" で右上が手前へ）、静的な四角形 1 枚。</summary>
        [SetUp] public void BuildSource()
        {
            source = new GameObject("PoseSource") { hideFlags = HideFlags.HideAndDontSave };
            var b0 = new GameObject("bone0"); b0.hideFlags = HideFlags.HideAndDontSave; b0.transform.SetParent(source.transform, false);
            var b1 = new GameObject("bone1"); b1.hideFlags = HideFlags.HideAndDontSave; b1.transform.SetParent(b0.transform, false); b1.transform.localPosition = new Vector3(0, 1, 0);
            var body = new GameObject("body"); body.hideFlags = HideFlags.HideAndDontSave; body.transform.SetParent(source.transform, false);
            skinMesh = new Mesh { vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) }, uv = new[] { new Vector2(.1f, .1f), new Vector2(.4f, .1f), new Vector2(.1f, .4f), new Vector2(.4f, .4f) }, triangles = new[] { 0, 2, 1, 1, 2, 3 } };
            skinMesh.boneWeights = new[] { new BoneWeight { boneIndex0 = 0, weight0 = 1 }, new BoneWeight { boneIndex0 = 0, weight0 = 1 }, new BoneWeight { boneIndex0 = 1, weight0 = 1 }, new BoneWeight { boneIndex0 = 1, weight0 = 1 } };
            skinMesh.bindposes = new[] { b0.transform.worldToLocalMatrix * body.transform.localToWorldMatrix, b1.transform.worldToLocalMatrix * body.transform.localToWorldMatrix };
            var delta = new Vector3[4]; delta[3] = new Vector3(0, 0, -1);
            skinMesh.AddBlendShapeFrame("push", 100, delta, null, null);
            skinMesh.RecalculateNormals();
            var skin = body.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = skinMesh; skin.bones = new[] { b0.transform, b1.transform }; skin.rootBone = b0.transform;
            var prop = new GameObject("prop"); prop.hideFlags = HideFlags.HideAndDontSave; prop.transform.SetParent(source.transform, false); prop.transform.localPosition = new Vector3(3, 0, 0);
            staticMesh = new Mesh { vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) }, uv = new[] { new Vector2(.6f, .6f), new Vector2(.9f, .6f), new Vector2(.6f, .9f) }, triangles = new[] { 0, 2, 1 } };
            prop.AddComponent<MeshFilter>().sharedMesh = staticMesh; prop.AddComponent<MeshRenderer>();
        }

        [TearDown] public void DestroySource() { Object.DestroyImmediate(source); Object.DestroyImmediate(skinMesh); Object.DestroyImmediate(staticMesh); }

        static Vector3 TopRight(IsolatedModelPreview p) => p.Geometry.Triangles.Where(t => t.MaterialSlot == 0).SelectMany(t => new[] { (t.A, t.UvA), (t.B, t.UvB), (t.C, t.UvC) }).First(v => v.Item2 == new Vector2(.4f, .4f)).Item1;

        [Test] public void SkinnedMeshesLoadPoseAndLeaveTheSourceAlone()
        {
            var skin = source.GetComponentInChildren<SkinnedMeshRenderer>(); var bone1 = skin.bones[1];
            using (var preview = new IsolatedModelPreview())
            {
                var report = preview.Load(source);
                Assert.That(report.CanPaint, Is.True, string.Join("\n", report.Diagnostics));
                Assert.That(preview.Geometry.TriangleCount, Is.EqualTo(3), "two skinned triangles and one static triangle");
                Assert.That(preview.HasSkinnedMeshes, Is.True);
                Assert.That(Vector3.Distance(TopRight(preview), new Vector3(1, 1, 0)), Is.LessThan(1e-4f));
                var shape = preview.BlendShapes.Single();
                Assert.That(shape.Label, Is.EqualTo("body / push"));
                int revision = preview.SnapshotRevision;
                preview.SetBlendShapeWeight(shape, 100);
                Assert.That(Vector3.Distance(TopRight(preview), new Vector3(1, 1, 0)), Is.LessThan(1e-4f), "nothing changes until ApplyPose");
                Assert.That(preview.ApplyPose(), Is.True);
                Assert.That(preview.SnapshotRevision, Is.GreaterThan(revision), "a new snapshot generation");
                Assert.That(Vector3.Distance(TopRight(preview), new Vector3(1, 1, -1)), Is.LessThan(1e-4f), "the BlendShape moved the vertex");
                Assert.That(preview.Geometry.Triangles.Count(t => t.MaterialSlot == 1), Is.EqualTo(1), "the static mesh is still there");

                var clip = new AnimationClip();
                foreach (var (axis, angle, position) in new[] { ("x", 0f, 0f), ("y", 0f, 1f), ("z", 90f, 0f) })
                {
                    clip.SetCurve("bone0/bone1", typeof(Transform), "localEulerAnglesRaw." + axis, AnimationCurve.Constant(0, 1, angle));
                    clip.SetCurve("bone0/bone1", typeof(Transform), "localPosition." + axis, AnimationCurve.Constant(0, 1, position));
                }
                preview.SamplePose(clip, .5f); preview.ApplyPose();
                var posed = TopRight(preview);
                Assert.That(Vector3.Distance(posed, new Vector3(0, 2, -1)), Is.LessThan(1e-3f), "bone1 turned 90° about z: (1, 1) → (0, 2)");
                Assert.That(preview.Geometry.Triangles.Where(t => t.MaterialSlot == 0).SelectMany(t => new[] { t.UvA, t.UvB, t.UvC }).Distinct().Count(), Is.EqualTo(4), "UVs do not change with the pose");

                Assert.That(skin.GetBlendShapeWeight(0), Is.Zero, "the source renderer is not touched");
                Assert.That(bone1.localRotation, Is.EqualTo(Quaternion.identity), "the source bones are not touched");

                preview.ResetPose(); preview.ApplyPose();
                Assert.That(Vector3.Distance(TopRight(preview), new Vector3(1, 1, 0)), Is.LessThan(1e-4f));
                Object.DestroyImmediate(clip);
            }
        }

        [Test] public void BonesOutsideTheModelAndHumanoidClipsAreRefused()
        {
            var outside = new GameObject("elsewhere") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var skin = source.GetComponentInChildren<SkinnedMeshRenderer>();
                skin.bones = new[] { skin.bones[0], outside.transform };
                using (var preview = new IsolatedModelPreview())
                {
                    var report = preview.Load(source);
                    Assert.That(report.CanPaint, Is.False, "a part of the model is missing, so painting through it is not allowed");
                    Assert.That(report.Diagnostics, Has.Some.Contains("outside the loaded model"));
                    Assert.That(() => preview.SamplePose(null, 0), Throws.ArgumentNullException);
                }
            }
            finally { Object.DestroyImmediate(outside); }
        }

        [Test] public void DisposingReleasesEveryCopy()
        {
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length, objects = Resources.FindObjectsOfTypeAll<GameObject>().Length, skins = Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>().Length;
            var preview = new IsolatedModelPreview();
            preview.Load(source); preview.SetBlendShapeWeight(preview.BlendShapes[0], 50); preview.ApplyPose();
            preview.Load(source); // 読み直しも前の複製を片付ける
            preview.Dispose();
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Length, Is.EqualTo(meshes));
            Assert.That(Resources.FindObjectsOfTypeAll<GameObject>().Length, Is.EqualTo(objects));
            Assert.That(Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>().Length, Is.EqualTo(skins));
        }
    }
}
