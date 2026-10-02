using System.Linq;
using NUnit.Framework;
using UnityEditor;
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

        /// <summary>Humanoid の最小の骨格（必須の骨と肩・胸・首）。左の前腕と手に重み 1 の四角形のスキンメッシュ。Animator に Avatar を付ける。</summary>
        static (GameObject root, Avatar avatar, Mesh mesh) HumanoidSource()
        {
            var root = new GameObject("HumanSource") { hideFlags = HideFlags.HideAndDontSave };
            Transform Bone(string name, Transform parent, Vector3 local) { var g = new GameObject(name); g.hideFlags = HideFlags.HideAndDontSave; g.transform.SetParent(parent, false); g.transform.localPosition = local; return g.transform; }
            var hips = Bone("Hips", root.transform, new Vector3(0, 1, 0));
            var chest = Bone("Chest", Bone("Spine", hips, new Vector3(0, .1f, 0)), new Vector3(0, .2f, 0));
            Bone("Head", Bone("Neck", chest, new Vector3(0, .2f, 0)), new Vector3(0, .1f, 0));
            foreach (var (side, sign) in new[] { ("Left", -1f), ("Right", 1f) })
            {
                Bone(side + "Foot", Bone(side + "LowerLeg", Bone(side + "UpperLeg", hips, new Vector3(.1f * sign, -.05f, 0)), new Vector3(0, -.4f, 0)), new Vector3(0, -.4f, 0));
                Bone(side + "Hand", Bone(side + "LowerArm", Bone(side + "UpperArm", Bone(side + "Shoulder", chest, new Vector3(.05f * sign, .15f, 0)), new Vector3(.1f * sign, 0, 0)), new Vector3(.25f * sign, 0, 0)), new Vector3(.25f * sign, 0, 0));
            }
            var all = root.GetComponentsInChildren<Transform>();
            var desc = new HumanDescription
            {
                human = all.Where(t => System.Array.IndexOf(HumanTrait.BoneName, t.name) >= 0).Select(t => new HumanBone { boneName = t.name, humanName = t.name, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
                skeleton = all.Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
                upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f, armStretch = .05f, legStretch = .05f,
            };
            var avatar = AvatarBuilder.BuildHumanAvatar(root, desc);
            root.AddComponent<Animator>().avatar = avatar;
            var lower = all.First(t => t.name == "LeftLowerArm"); var hand = all.First(t => t.name == "LeftHand");
            var body = new GameObject("body"); body.hideFlags = HideFlags.HideAndDontSave; body.transform.SetParent(root.transform, false);
            var mesh = new Mesh { vertices = new[] { lower.position, hand.position, lower.position + Vector3.up * .05f, hand.position + Vector3.up * .05f }, uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }, triangles = new[] { 0, 2, 1, 1, 2, 3 } };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray();
            mesh.bindposes = new[] { lower.worldToLocalMatrix * body.transform.localToWorldMatrix };
            mesh.RecalculateNormals();
            var skin = body.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh; skin.bones = new[] { lower }; skin.rootBone = lower;
            return (root, avatar, mesh);
        }

        [Test] public void HumanoidClipsPoseThroughTheModelsAvatar()
        {
            var (root, avatar, mesh) = HumanoidSource();
            var clip = new AnimationClip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Left Arm Down-Up"), AnimationCurve.Constant(0, 1, -1));
            try
            {
                Assert.That(avatar.isValid && avatar.isHuman, Is.True); Assert.That(clip.humanMotion, Is.True);
                var upper = root.GetComponentsInChildren<Transform>().First(t => t.name == "LeftUpperArm"); var rest = upper.localRotation;
                using (var preview = new IsolatedModelPreview())
                {
                    Assert.That(preview.Load(root).CanPaint, Is.True);
                    Assert.That(preview.HasHumanoidAvatar, Is.True);
                    var before = preview.Geometry.Triangles.Select(t => t.B).ToArray();
                    preview.SamplePose(clip, 0); preview.ApplyPose();
                    var after = preview.Geometry.Triangles.Select(t => t.B).ToArray();
                    Assert.That(Enumerable.Range(0, before.Length).Max(i => Vector3.Distance(before[i], after[i])), Is.GreaterThan(.1f), "lowering the arm moves the forearm");
                    Assert.That(upper.localRotation, Is.EqualTo(rest), "the source bones are not touched");
                    preview.ResetPose(); preview.ApplyPose();
                    var reset = preview.Geometry.Triangles.Select(t => t.B).ToArray();
                    Assert.That(Enumerable.Range(0, before.Length).Max(i => Vector3.Distance(before[i], reset[i])), Is.LessThan(1e-4f));
                }
            }
            finally { Object.DestroyImmediate(clip); Object.DestroyImmediate(root); Object.DestroyImmediate(avatar); Object.DestroyImmediate(mesh); }
        }

        [Test] public void FingerCurvesMapToMuscleNames()
        {
            Assert.That(IsolatedModelPreview.MuscleIndex("LeftHand.Thumb.1 Stretched"), Is.EqualTo(System.Array.IndexOf(HumanTrait.MuscleName, "Left Thumb 1 Stretched")));
            Assert.That(IsolatedModelPreview.MuscleIndex("RightHand.Index.Spread"), Is.EqualTo(System.Array.IndexOf(HumanTrait.MuscleName, "Right Index Spread")));
            Assert.That(IsolatedModelPreview.MuscleIndex("Spine Front-Back"), Is.GreaterThanOrEqualTo(0));
            Assert.That(IsolatedModelPreview.MuscleIndex("RootT.x"), Is.EqualTo(-1));
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
                skin.bones = new[] { skin.bones[0], skin.bones[0] };
                using (var preview = new IsolatedModelPreview())
                {
                    preview.Load(source);
                    var human = new AnimationClip();
                    AnimationUtility.SetEditorCurve(human, EditorCurveBinding.FloatCurve("", typeof(Animator), "Left Arm Down-Up"), AnimationCurve.Constant(0, 1, -1));
                    try { Assert.That(() => preview.SamplePose(human, 0), Throws.InvalidOperationException.With.Message.Contains("no valid Humanoid Avatar")); }
                    finally { Object.DestroyImmediate(human); }
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
