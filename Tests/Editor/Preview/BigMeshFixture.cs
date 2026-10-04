using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
namespace Yozolab.YoluPainter.Tests
{
    // 24 レンダラー・32 サブメッシュ・7 万三角形。位置が同じ別頂点（UV の継ぎ目）と非多様体の辺を持つ合成モデル。
    internal sealed class BigMeshFixture : IDisposable
    {
        internal readonly GameObject Root = new GameObject("Synthetic large model");
        readonly List<Mesh> meshes = new List<Mesh>();
        // スロットごとに別のマテリアル（テクスチャセットはマテリアルごとなので、どのスロットも自分のセットになる）
        readonly List<Material> materials = new List<Material>();
        Material[] Materials(int count)
        {
            var list = new Material[count];
            for (int i = 0; i < count; i++) { var m = new Material(Shader.Find("Unlit/Texture")) { name = "Synthetic material " + materials.Count, hideFlags = HideFlags.HideAndDontSave }; materials.Add(m); list[i] = m; }
            return list;
        }
        internal BigMeshFixture(bool skinned = false)
        {
            for (int r = 0; r < 24; r++)
            {
                var go = new GameObject("Synthetic part " + r); go.transform.SetParent(Root.transform, false);
                go.transform.localPosition = new Vector3(r % 6 * 1.1f, r / 6 * .6f, 0);
                var vertices = new List<Vector3>(); var uv = new List<Vector2>();
                for (int y = 0; y <= 27; y++) for (int x = 0; x <= 54; x++)
                { vertices.Add(new Vector3(x / 54f, y / 54f, .015f * Mathf.Sin(x))); uv.Add(new Vector2(x / 54f, y / 27f)); }
                var first = new List<int>(); var second = new List<int>();
                for (int y = 0; y < 27; y++) for (int x = 0; x < 54; x++)
                {
                    int a = y * 55 + x, b = a + 55, c = a + 1, d = b + 1;
                    var dst = r < 8 && y >= 14 ? second : first;
                    dst.AddRange(new[] { a, b, c });
                    if (x % 9 == 0) { int seam = vertices.Count; vertices.Add(vertices[b]); uv.Add(new Vector2(.5f + uv[b].x * .5f, uv[b].y)); b = seam; }
                    dst.AddRange(new[] { c, b, d });
                }
                if (r == 0) for (int i = 0; i < 16; i++)
                { int c = vertices.Count; vertices.Add(new Vector3(.01f * i, .025f, .03f + i * .001f)); uv.Add(new Vector2(.01f * i, .05f)); first.AddRange(new[] { 0, 55, c }); }
                var mesh = new Mesh { name = "Synthetic surface", indexFormat = IndexFormat.UInt32 };
                meshes.Add(mesh); mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.subMeshCount = r < 8 ? 2 : 1;
                mesh.SetTriangles(first, 0); if (r < 8) mesh.SetTriangles(second, 1); mesh.RecalculateNormals(); mesh.RecalculateBounds();
                if (skinned)
                {
                    var weights = new BoneWeight[vertices.Count]; for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1 };
                    mesh.boneWeights = weights; mesh.bindposes = new[] { Matrix4x4.identity };
                    var skin = go.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh; skin.bones = new[] { go.transform }; skin.rootBone = go.transform;
                    skin.sharedMaterials = Materials(mesh.subMeshCount);
                }
                else { go.AddComponent<MeshFilter>().sharedMesh = mesh; go.AddComponent<MeshRenderer>().sharedMaterials = Materials(mesh.subMeshCount); }
            }
        }
        public void Dispose() { Object.DestroyImmediate(Root); foreach (var m in meshes) Object.DestroyImmediate(m); foreach (var m in materials) Object.DestroyImmediate(m); }
    }
}
