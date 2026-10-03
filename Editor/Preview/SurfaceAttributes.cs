using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>
    /// スナップショットの三角形ごとの頂点の属性（読むだけ）。並びは <see cref="SurfaceGeometry.Triangles"/> と同じで、同じ世代の
    /// スナップショットから作る。法線・接線はプレビューが表示に使うメッシュのもの（元のメッシュの法線、無ければ Unity の再計算）を
    /// スナップショットの空間に置いたもの。接線の w は、左右反転したレンダラーでは反転済み（表示と同じく Unity がノーマルマップを
    /// 読む向き）。色はメッシュの頂点カラー（無ければ null）。
    /// </summary>
    public sealed class SurfaceAttributes
    {
        /// <summary>三角形ごとに 3 頂点 × xyz（9 個）。</summary>
        public float[] Normals { get; }
        /// <summary>三角形ごとに 3 頂点 × xyzw（12 個）。UV の無いメッシュの三角形は 0。どのメッシュにも接線が無ければ null。</summary>
        public float[] Tangents { get; }
        /// <summary>三角形ごとに 3 頂点 × RGBA（12 個）。頂点カラーの無いメッシュの三角形は白。どのメッシュにも無ければ null。</summary>
        public float[] Colors { get; }
        /// <summary>レンダラーの名前（<see cref="SurfaceTriangle.RendererIndex"/> で引く）。</summary>
        public IReadOnlyList<string> RendererNames { get; }
        public int TriangleCount => Normals.Length / 9;

        internal SurfaceAttributes(float[] normals, float[] tangents, float[] colors, IReadOnlyList<string> rendererNames)
        { Normals = normals; Tangents = tangents; Colors = colors; RendererNames = rendererNames; }

        /// <summary>三角形を並べながら属性も同じ並びで積む。</summary>
        internal sealed class Builder
        {
            readonly List<float> normals = new List<float>(), tangents = new List<float>(), colors = new List<float>();
            bool anyTangents, anyColors;
            public void Add(Vector3[] n, Vector4[] t, Color[] c, int a, int b, int d)
            {
                foreach (int i in new[] { a, b, d })
                {
                    var v = n != null && i < n.Length ? n[i] : Vector3.zero;
                    normals.Add(v.x); normals.Add(v.y); normals.Add(v.z);
                    bool hasT = t != null && i < t.Length; anyTangents |= hasT;
                    var w = hasT ? t[i] : Vector4.zero;
                    tangents.Add(w.x); tangents.Add(w.y); tangents.Add(w.z); tangents.Add(w.w);
                    bool hasC = c != null && i < c.Length; anyColors |= hasC;
                    var col = hasC ? c[i] : Color.white;
                    colors.Add(col.r); colors.Add(col.g); colors.Add(col.b); colors.Add(col.a);
                }
            }
            public SurfaceAttributes Build(IReadOnlyList<string> names) => new SurfaceAttributes(normals.ToArray(), anyTangents ? tangents.ToArray() : null, anyColors ? colors.ToArray() : null, names);
        }
    }

    /// <summary>読み込みの上限と原点。高ポリ（ベイクの参照）は表示の試作の上限より大きい予算で、低ポリのモデルの原点で読む。</summary>
    public sealed class PreviewLoadOptions
    {
        public int MaxTriangles = 150000, MaxVerticesPerMesh = 250000;
        /// <summary>スナップショットの原点（ワールド）。null なら読み込むモデルのルートの位置。</summary>
        public Vector3? Origin;
    }
}
