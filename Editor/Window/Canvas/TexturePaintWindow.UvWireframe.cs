using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>2D キャンバスに、今のテクスチャセット（マテリアルのスロット）の UV のワイヤーフレームを重ねる（Substance Painter の 2D ビューと同じ）。
    /// 表示だけで、文書には何も足さない。辺は 3D ビューのスナップショットの三角形から作り、UV で同じ辺は 1 本にする（UV の継ぎ目では別の辺になる）。</summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] bool showUvWireframe = true;
        internal bool ShowUvWireframe { get => showUvWireframe; set { showUvWireframe = value; Repaint(); } }
        /// <summary>重ねる辺の数の上限。超えるモデルでは描かずに知らせる（1 回の描画で GUI の座標に写す量を抑える）。</summary>
        internal const int MaxUvWireframeEdges = 1 << 20;
        static readonly Color UvWireframeColor = new Color(.35f, .85f, 1f, .6f);

        SurfaceGeometry uvEdgesGeometry; int uvEdgesSlot = -1; Vector2[] uvEdges; bool uvEdgesTruncated;
        Vector3[] uvEdgesGui; (int, int, float, Vector2, float, bool, Vector2, int) uvEdgesGuiKey;

        /// <summary>スロットの三角形の UV の辺（2 点ずつ並べた UV 座標）。UV で両端が同じ辺は 1 本。上限を超えたら null と truncated。</summary>
        internal static Vector2[] UvEdges(IReadOnlyList<SurfaceTriangle> triangles, int slot, int maxEdges, out bool truncated)
        {
            truncated = false;
            var seen = new HashSet<(float, float, float, float)>(); var edges = new List<Vector2>();
            foreach (var t in triangles)
            {
                if (t.MaterialSlot != slot) continue;
                if (!Add(t.UvA, t.UvB) || !Add(t.UvB, t.UvC) || !Add(t.UvC, t.UvA)) { truncated = true; return null; }
            }
            return edges.ToArray();

            bool Add(Vector2 a, Vector2 b)
            {
                if (a == b) return true; // つぶれた辺は描かない
                if (b.x < a.x || b.x == a.x && b.y < a.y) { var swap = a; a = b; b = swap; }
                if (!seen.Add((a.x, a.y, b.x, b.y))) return true;
                if (edges.Count / 2 >= maxEdges) return false;
                edges.Add(a); edges.Add(b); return true;
            }
        }

        /// <summary>今のテクスチャセットの UV の辺（3D ビューの形かスロットが変わったときだけ作り直す）。</summary>
        internal Vector2[] CurrentUvEdges()
        {
            var geometry = preview != null && preview.HasModel ? preview.Geometry : null;
            if (geometry == null) return null;
            if (!ReferenceEquals(geometry, uvEdgesGeometry) || uvEdgesSlot != materialSlot)
            {
                uvEdges = UvEdges(geometry.Triangles, materialSlot, MaxUvWireframeEdges, out uvEdgesTruncated);
                uvEdgesGeometry = geometry; uvEdgesSlot = materialSlot; uvEdgesGui = null;
            }
            return uvEdges;
        }

        /// <summary>キャンバスのクリップの中で、画像の上に UV の辺を描く（Repaint のときだけ）。表示の回転・反転・拡大・パンは CanvasView で写す。
        /// 写した座標は表示が変わったときだけ作り直す（ストロークの間の描き直しでは写さない）。</summary>
        void DrawUvWireframe(CanvasView view)
        {
            if (!showUvWireframe || Event.current.type != EventType.Repaint) return;
            var edges = CurrentUvEdges();
            if (edges == null || edges.Length == 0) return;
            var key = (document.Width, document.Height, canvasZoom, canvasPan, canvasAngle, canvasFlip, canvasRect.size, edges.Length);
            if (uvEdgesGui == null || !uvEdgesGuiKey.Equals(key))
            {
                if (uvEdgesGui == null || uvEdgesGui.Length != edges.Length) uvEdgesGui = new Vector3[edges.Length];
                float w = document.Width, h = document.Height;
                for (int i = 0; i < edges.Length; i++) uvEdgesGui[i] = view.ToGui(edges[i].x * w, edges[i].y * h);
                uvEdgesGuiKey = key;
            }
            var before = Handles.color; Handles.color = UvWireframeColor;
            Handles.DrawLines(uvEdgesGui);
            Handles.color = before;
        }

        /// <summary>2D の見出しの切り替えのボタン。モデルが無い・辺が多すぎるときは押せない理由をツールチップに出す。</summary>
        internal Rect uvWireframeToggleForTests;
        void DrawUvWireframeToggle(Rect r)
        {
            uvWireframeToggleForTests = r;
            bool has = preview != null && preview.HasModel;
            string tip = !has ? L.Tr("UV wireframe: load a model to see its UVs on the canvas")
                : uvEdgesTruncated ? L.Tr("UV wireframe: the model has more than {0} UV edges, so they are not drawn", MaxUvWireframeEdges)
                : L.Tr("UV wireframe of this texture set on the 2D canvas");
            if (PaintGui.IconButton(r, "uv_wireframe", tip, showUvWireframe && has, has, 16)) ShowUvWireframe = !showUvWireframe;
        }
    }
}
