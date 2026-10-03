using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>
    /// 3D ビューに薄く色を付けて見せる三角形の範囲（ポリゴン塗りつぶしの、ポインタの下の範囲）。対称の面と同じく、プレビューの中だけの物
    /// （PickHighlight シェーダーで面とワイヤーを描き、奥行きには書かない。モデルの面から少し手前へずらす）で、当たり判定・ブラシ・ベイクには
    /// 入らない。メッシュは範囲の鍵かジオメトリが変わったときだけ作り直す（ポインタが同じ範囲の中を動くあいだは作らない）。
    /// </summary>
    public sealed partial class IsolatedModelPreview
    {
        GameObject regionObject; Mesh regionMesh; Material regionMaterial;
        SurfaceGeometry regionGeometry, builtRegionGeometry; long regionKey, builtRegionKey; IReadOnlyList<int> regionTriangles; Color regionColor;
        bool regionWanted, regionBuilt, regionUsesId;
        BakedMeshMap regionIdMap; Texture2D regionIdTexture; int regionIdRgb, regionIdTolerance;
        internal int IdHighlightTextureBuilds { get; private set; }
        internal string IdHighlightRefusal { get; private set; }
        internal (int, int, int)? IdHighlightInput => regionUsesId && regionIdTexture != null ? (regionIdTexture.GetInstanceID(), regionIdRgb, regionIdTolerance) : ((int, int, int)?)null;
        /// <summary>焼いた ID のテクセルを、そのまま照合して強調する。テクスチャはベイクが変わったときだけ、メッシュはスロットが変わったときだけ作る。</summary>
        internal bool ShowIdRegion(long key, IReadOnlyList<int> triangles, Color color, BakedMeshMap map, int rgb, int tolerance, long budget)
        {
            IdMapColors.RequireIdMap(map);
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (rgb < 0 || rgb > 0xFFFFFF || tolerance < 0 || tolerance > 255) throw new ArgumentOutOfRangeException(nameof(rgb));
            // 新テクスチャの画素配列・CPU・GPU の分と、メッシュの配列・バリセントリックのリスト・CPU/GPU の分を上から見積もる。
            // 入れ替えでは前の GPU テクスチャとメッシュも同時に生きている。ドライバー内部の量を測った値ではない。
            long textureBytes = (long)map.Width * map.Height * (ReferenceEquals(map, regionIdMap) && regionIdTexture != null ? 4 : 12);
            long oldTextureBytes = regionIdTexture != null && !ReferenceEquals(map, regionIdMap) ? (long)regionIdTexture.width * regionIdTexture.height * 4 : 0;
            long oldMeshBytes = regionMesh != null && (!ReferenceEquals(geometry, builtRegionGeometry) || key != builtRegionKey) ? (long)regionMesh.vertexCount * 72 : 0;
            if (textureBytes + oldTextureBytes + oldMeshBytes + (long)triangles.Count * 384 > budget)
            { IdHighlightRefusal = "ID highlight exceeds the preview memory budget."; HideRegion(); return false; }
            if (!ReferenceEquals(map, regionIdMap) || regionIdTexture == null)
            {
                var pixels = new Color32[map.Width * map.Height];
                for (int y = 0; y < map.Height; y++) for (int x = 0; x < map.Width; x++)
                    if (IdMapColors.TryGet(map, x, y, out int id)) pixels[y * map.Width + x] = new Color32((byte)(id >> 16), (byte)(id >> 8), (byte)id, 255);
                var texture = new Texture2D(map.Width, map.Height, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                try { texture.SetPixels32(pixels); texture.Apply(false, true); }
                catch { Object.DestroyImmediate(texture); throw; }
                if (regionIdTexture != null) Object.DestroyImmediate(regionIdTexture);
                regionIdTexture = texture; regionIdMap = map; IdHighlightTextureBuilds++;
            }
            ShowRegion(key, triangles, color); regionUsesId = true; regionIdRgb = rgb; regionIdTolerance = tolerance; IdHighlightRefusal = null;
            return true;
        }

        /// <summary>範囲 triangles（今のジオメトリの三角形の番号。key が同じなら同じ集まり）を color で薄く見せる。</summary>
        public void ShowRegion(long key, IReadOnlyList<int> triangles, Color color)
        {
            regionUsesId = false; regionWanted = triangles != null && geometry != null; regionGeometry = geometry; regionKey = key; regionTriangles = triangles; regionColor = color;
        }
        /// <summary>範囲を見せない。</summary>
        public void HideRegion() { regionWanted = false; regionUsesId = false; regionTriangles = null; }

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
                var shader = Shader.Find("Hidden/YoluPainter/PickHighlight");
                if (shader == null) return; // 強調のシェーダーが無ければ見せない（塗ることには関わらない）
                regionMaterial = new Material(shader) { name = "Polygon fill region (preview only)", hideFlags = HideFlags.HideAndDontSave, renderQueue = 3000 };
                regionMesh = new Mesh { name = "Polygon fill region (preview only)", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                regionObject = new GameObject("Polygon fill region (preview only)") { hideFlags = HideFlags.HideAndDontSave };
                regionObject.AddComponent<MeshFilter>().sharedMesh = regionMesh;
                var renderer = regionObject.AddComponent<MeshRenderer>(); renderer.sharedMaterial = regionMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                preview.AddSingleGO(regionObject); regionBuilt = false;
            }
            if (!regionBuilt || regionKey != builtRegionKey || !ReferenceEquals(geometry, builtRegionGeometry))
            {
                var all = geometry.Triangles; int n = regionTriangles.Count;
                var vertices = new Vector3[n * 3]; var uvs = new Vector2[n * 3]; var bary = new Vector3[n * 3]; var indices = new int[n * 3];
                for (int k = 0; k < n; k++)
                {
                    var t = all[regionTriangles[k]];
                    vertices[3 * k] = t.A; vertices[3 * k + 1] = t.B; vertices[3 * k + 2] = t.C;
                    uvs[3 * k] = t.UvA; uvs[3 * k + 1] = t.UvB; uvs[3 * k + 2] = t.UvC;
                    bary[3 * k] = Vector3.right; bary[3 * k + 1] = Vector3.up; bary[3 * k + 2] = Vector3.forward;
                    indices[3 * k] = 3 * k; indices[3 * k + 1] = 3 * k + 1; indices[3 * k + 2] = 3 * k + 2;
                }
                regionMesh.Clear();
                regionMesh.SetVertices(vertices); regionMesh.SetUVs(0, uvs); regionMesh.SetUVs(1, new List<Vector3>(bary)); regionMesh.SetIndices(indices, MeshTopology.Triangles, 0);
                regionMesh.RecalculateBounds();
                builtRegionKey = regionKey; builtRegionGeometry = geometry; regionBuilt = true; RegionHighlightBuilds++;
            }
            regionMaterial.SetColor("_Color", regionColor);
            regionMaterial.SetFloat("_UseId", regionUsesId ? 1 : 0);
            regionMaterial.SetTexture("_IdMap", regionIdTexture);
            regionMaterial.SetVector("_IdRgb", new Vector4(regionIdRgb >> 16 & 255, regionIdRgb >> 8 & 255, regionIdRgb & 255, 0));
            regionMaterial.SetFloat("_IdTolerance", regionIdTolerance);
            regionObject.SetActive(true);
        }

        void DisposeRegionHighlight()
        {
            if (regionIdTexture != null) Object.DestroyImmediate(regionIdTexture); regionIdTexture = null; regionIdMap = null; regionUsesId = false;
            if (regionObject != null) Object.DestroyImmediate(regionObject);
            if (regionMesh != null) Object.DestroyImmediate(regionMesh);
            if (regionMaterial != null) Object.DestroyImmediate(regionMaterial);
            regionObject = null; regionMesh = null; regionMaterial = null; regionBuilt = false; regionWanted = false; regionTriangles = null;
            regionGeometry = builtRegionGeometry = null;
        }
    }
}
