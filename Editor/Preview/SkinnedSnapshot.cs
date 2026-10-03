using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>読み込んだモデルの Transform の階層だけを複製したもの（コンポーネントもスクリプトも複製しない）。スキンメッシュの骨を、
    /// 元のモデルに触れずに動かすために使う。プレビュー専用のシーンに置き、保存しない。</summary>
    internal sealed class TransformCopy : IDisposable
    {
        public GameObject Root { get; }
        readonly Dictionary<Transform, Transform> map = new Dictionary<Transform, Transform>();
        readonly Dictionary<Transform, (Vector3, Quaternion, Vector3)> initial = new Dictionary<Transform, (Vector3, Quaternion, Vector3)>();

        public TransformCopy(Transform source)
        {
            Root = new GameObject(source.name + " (pose copy)") { hideFlags = HideFlags.HideAndDontSave };
            Root.transform.SetPositionAndRotation(source.position, source.rotation);
            Root.transform.localScale = source.lossyScale;
            map.Add(source, Root.transform);
            Copy(source, Root.transform);
            foreach (var pair in map) initial.Add(pair.Value, (pair.Value.localPosition, pair.Value.localRotation, pair.Value.localScale));
        }

        void Copy(Transform source, Transform copy)
        {
            for (int i = 0; i < source.childCount; i++)
            {
                var child = source.GetChild(i);
                var go = new GameObject(child.name) { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(copy, false);
                go.transform.localPosition = child.localPosition; go.transform.localRotation = child.localRotation; go.transform.localScale = child.localScale;
                map.Add(child, go.transform);
                Copy(child, go.transform);
            }
        }

        /// <summary>元の Transform に当たる複製。読み込んだモデルの外なら null。</summary>
        public Transform this[Transform source] => source != null && map.TryGetValue(source, out var copy) ? copy : null;

        /// <summary>骨を読み込んだときの姿勢に戻す。</summary>
        public void Reset()
        {
            foreach (var pair in initial) { pair.Key.localPosition = pair.Value.Item1; pair.Key.localRotation = pair.Value.Item2; pair.Key.localScale = pair.Value.Item3; }
        }

        public void Dispose() { if (Root != null) Object.DestroyImmediate(Root); }
    }

    /// <summary>スキンメッシュ 1 つ: 複製の骨に付けた描画しない SkinnedMeshRenderer（メッシュも複製）。BakeMesh で今のポーズと BlendShape
    /// の形を焼き、その形を表示と当たり判定の両方に使う（GPU と CPU で違う形を見せない）。</summary>
    internal sealed class SkinnedSnapshot : IDisposable
    {
        public SkinnedMeshRenderer Renderer { get; private set; }
        public string Name { get; private set; }
        Mesh mesh, baked;
        float[] initialWeights;

        public static SkinnedSnapshot Create(TransformCopy skeleton, SkinnedMeshRenderer source)
        {
            var host = skeleton[source.transform];
            if (host == null) throw new InvalidOperationException("the renderer is outside the loaded model");
            var bones = source.bones; var copies = new Transform[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                copies[i] = skeleton[bones[i]];
                if (copies[i] == null) throw new InvalidOperationException("bone '" + bones[i].name + "' is outside the loaded model; load the model's root");
            }
            var snapshot = new SkinnedSnapshot { Name = source.name };
            try
            {
                snapshot.mesh = Object.Instantiate(source.sharedMesh); // Mesh only
                snapshot.mesh.name = source.sharedMesh.name + " (pose snapshot)"; snapshot.mesh.hideFlags = HideFlags.HideAndDontSave;
                snapshot.baked = new Mesh { name = source.sharedMesh.name + " (baked pose)", hideFlags = HideFlags.HideAndDontSave };
                var renderer = host.gameObject.AddComponent<SkinnedMeshRenderer>();
                renderer.enabled = false; renderer.sharedMesh = snapshot.mesh; renderer.bones = copies; renderer.rootBone = skeleton[source.rootBone];
                snapshot.Renderer = renderer;
                snapshot.initialWeights = new float[snapshot.mesh.blendShapeCount];
                for (int i = 0; i < snapshot.initialWeights.Length; i++) { snapshot.initialWeights[i] = source.GetBlendShapeWeight(i); renderer.SetBlendShapeWeight(i, snapshot.initialWeights[i]); }
                return snapshot;
            }
            catch { snapshot.Dispose(); throw; }
        }

        /// <summary>今の骨と BlendShape の形（Renderer の Transform からの位置、スケールを含む）。</summary>
        public Mesh Bake() { Renderer.BakeMesh(baked, true); return baked; }

        public int BlendShapeCount => mesh.blendShapeCount;
        public string BlendShapeName(int index) => mesh.GetBlendShapeName(index);
        public float GetBlendShapeWeight(int index) => Renderer.GetBlendShapeWeight(index);
        public void SetBlendShapeWeight(int index, float weight) => Renderer.SetBlendShapeWeight(index, weight);
        public void ResetBlendShapes() { for (int i = 0; i < initialWeights.Length; i++) Renderer.SetBlendShapeWeight(i, initialWeights[i]); }

        public void Dispose()
        {
            if (Renderer != null) Object.DestroyImmediate(Renderer);
            if (mesh != null) Object.DestroyImmediate(mesh);
            if (baked != null) Object.DestroyImmediate(baked);
            Renderer = null; mesh = null; baked = null;
        }
    }
}
