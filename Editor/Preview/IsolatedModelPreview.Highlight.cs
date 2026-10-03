using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>
    /// 3D ビューに薄く色を付けて見せる三角形の範囲（ポリゴン塗りつぶしの、ポインタの下の範囲）。対称の面と同じく、プレビューの中だけの物
    /// （Hidden/Internal-Colored、奥行きを見て描き、書かない。モデルの面とちょうど重なるので少し手前へずらす）で、当たり判定・ブラシ・ベイクには
    /// 入らない。メッシュは範囲の鍵かジオメトリが変わったときだけ作り直す（ポインタが同じ範囲の中を動くあいだは作らない）。
    /// </summary>
    public sealed partial class IsolatedModelPreview
    {
        GameObject regionObject; Mesh regionMesh; Material regionMaterial;
        SurfaceGeometry regionGeometry, builtRegionGeometry; long regionKey, builtRegionKey; IReadOnlyList<int> regionTriangles; Color regionColor, builtRegionColor;
        bool regionWanted, regionBuilt;

        /// <summary>範囲 triangles（今のジオメトリの三角形の番号。key が同じなら同じ集まり）を color で薄く見せる。</summary>
        public void ShowRegion(long key, IReadOnlyList<int> triangles, Color color)
        {
            regionWanted = triangles != null && geometry != null; regionGeometry = geometry; regionKey = key; regionTriangles = triangles; regionColor = color;
        }
        /// <summary>範囲を見せない。</summary>
        public void HideRegion() { regionWanted = false; regionTriangles = null; }

        /// <summary>試験用: 範囲を見せる物が今の描画に入っているか、その三角形の数。</summary>
        internal bool RegionHighlightVisible => regionObject != null && regionObject.activeSelf;
        internal int RegionHighlightTriangleCount => regionMesh != null && RegionHighlightVisible ? (int)regionMesh.GetIndexCount(0) / 3 : 0;
        /// <summary>計測用: メッシュを作り直した回数。</summary>
        internal int RegionHighlightBuilds { get; private set; }

        void UpdateRegionHighlightObject()
        {
            bool show = regionWanted && HasModel && ReferenceEquals(regionGeometry, geometry) && regionTriangles != null && regionTriangles.Count > 0;
            if (!show) { if (regionObject != null) regionObject.SetActive(false); return; }
            if (regionObject == null)
            {
                var shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) return; // 組み込みのシェーダーが無ければ見せない（塗ることには関わらない）
                regionMaterial = new Material(shader) { name = "Polygon fill region (preview only)", hideFlags = HideFlags.HideAndDontSave, renderQueue = 3000 };
                regionMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); regionMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                regionMaterial.SetInt("_ZWrite", 0); regionMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual); regionMaterial.SetInt("_Cull", (int)CullMode.Off);
                regionMaterial.SetFloat("_ZBias", -2); // モデルの面と同じ位置なので手前へ（Offset -2, -2）
                regionMesh = new Mesh { name = "Polygon fill region (preview only)", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                regionObject = new GameObject("Polygon fill region (preview only)") { hideFlags = HideFlags.HideAndDontSave };
                regionObject.AddComponent<MeshFilter>().sharedMesh = regionMesh;
                var renderer = regionObject.AddComponent<MeshRenderer>(); renderer.sharedMaterial = regionMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                preview.AddSingleGO(regionObject); regionBuilt = false;
            }
            if (!regionBuilt || regionKey != builtRegionKey || !ReferenceEquals(geometry, builtRegionGeometry) || regionColor != builtRegionColor)
            {
                var all = geometry.Triangles; int n = regionTriangles.Count;
                var vertices = new Vector3[n * 3]; var colors = new Color32[n * 3]; var indices = new int[n * 3]; Color32 c = regionColor;
                for (int k = 0; k < n; k++)
                {
                    var t = all[regionTriangles[k]];
                    vertices[3 * k] = t.A; vertices[3 * k + 1] = t.B; vertices[3 * k + 2] = t.C;
                    colors[3 * k] = colors[3 * k + 1] = colors[3 * k + 2] = c;
                    indices[3 * k] = 3 * k; indices[3 * k + 1] = 3 * k + 1; indices[3 * k + 2] = 3 * k + 2;
                }
                regionMesh.Clear();
                regionMesh.SetVertices(vertices); regionMesh.SetColors(colors); regionMesh.SetIndices(indices, MeshTopology.Triangles, 0);
                regionMesh.RecalculateBounds();
                builtRegionKey = regionKey; builtRegionGeometry = geometry; builtRegionColor = regionColor; regionBuilt = true; RegionHighlightBuilds++;
            }
            regionObject.SetActive(true);
        }

        void DisposeRegionHighlight()
        {
            if (regionObject != null) Object.DestroyImmediate(regionObject);
            if (regionMesh != null) Object.DestroyImmediate(regionMesh);
            if (regionMaterial != null) Object.DestroyImmediate(regionMaterial);
            regionObject = null; regionMesh = null; regionMaterial = null; regionBuilt = false; regionWanted = false; regionTriangles = null;
            regionGeometry = builtRegionGeometry = null;
        }
    }
}
