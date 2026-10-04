using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.Preview
{
    internal sealed class PreviewRendererInfo
    {
        internal int Index; internal string Key; internal string[] Names;
        internal int[] Slots;
    }

    public sealed partial class IsolatedModelPreview
    {
        readonly List<PreviewRendererInfo> rendererInfos = new List<PreviewRendererInfo>();
        readonly HashSet<int> hiddenRenderers = new HashSet<int>(), hiddenSlots = new HashSet<int>();
        SurfaceGeometry visibleGeometry, visibleSource;
        long visibilityVersion;
        SurfaceGeometry anyVisibleSource; bool anyVisible, anyVisibleValid;
        internal IReadOnlyList<PreviewRendererInfo> Renderers => rendererInfos;
        /// <summary>描画・ピックだけの幾何。三角形の番号とBVHは元と同じで、ベイク・パスの再評価は Geometry を読む。</summary>
        internal SurfaceGeometry PickingGeometry
        {
            get
            {
                if (geometry == null || hiddenRenderers.Count == 0 && hiddenSlots.Count == 0) return geometry;
                if (visibleGeometry == null || visibleSource != geometry)
                { visibleSource = geometry; visibleGeometry = geometry.VisibleView(t => IsVisible(t.RendererIndex, t.MaterialSlot)); }
                return visibleGeometry;
            }
        }
        internal bool IsVisible(int renderer, int slot) => !hiddenRenderers.Contains(renderer) && !hiddenSlots.Contains(slot);
        internal bool IsTriangleVisible(int index) => geometry != null && index >= 0 && index < geometry.TriangleCount && IsVisible(geometry.Triangles[index].RendererIndex, geometry.Triangles[index].MaterialSlot);
        internal bool AnyVisible
        {
            get
            {
                if (!anyVisibleValid || anyVisibleSource != geometry)
                { anyVisibleSource = geometry; anyVisible = geometry != null && geometry.Triangles.Any(t => IsVisible(t.RendererIndex, t.MaterialSlot)); anyVisibleValid = true; }
                return anyVisible;
            }
        }
        internal void SetVisibility(IEnumerable<int> renderers, IEnumerable<int> slots)
        {
            var nextRenderers = new HashSet<int>(renderers ?? Enumerable.Empty<int>());
            var nextSlots = new HashSet<int>(slots ?? Enumerable.Empty<int>());
            if (nextRenderers.Any(i => i < 0 || i >= report.LoadedRendererCount) || nextSlots.Any(i => i < 0 || i >= MaterialSlotCount)) throw new ArgumentOutOfRangeException("visibility");
            if (hiddenRenderers.SetEquals(nextRenderers) && hiddenSlots.SetEquals(nextSlots)) return;
            hiddenRenderers.Clear(); hiddenRenderers.UnionWith(nextRenderers); hiddenSlots.Clear(); hiddenSlots.UnionWith(nextSlots);
            visibleGeometry = visibleSource = null; anyVisibleValid = false; visibilityVersion++; HideRegion();
            for (int i = 0; i < slotRenderers.Count; i++)
            {
                var (renderer, materialSlots) = slotRenderers[i];
                renderer.enabled = !hiddenRenderers.Contains(i) && materialSlots.Any(s => !hiddenSlots.Contains(s));
                var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                var entry = entries.FirstOrDefault(e => e.RendererIndex == i);
                var full = entry?.Indices ?? demoIndices;
                for (int sub = 0; sub < materialSlots.Length; sub++) mesh.SetTriangles(IsVisible(i, materialSlots[sub]) ? full[sub] : Array.Empty<int>(), sub, false);
            }
        }
        int[][] demoIndices;
        void ClearVisibility()
        {
            rendererInfos.Clear(); hiddenRenderers.Clear(); hiddenSlots.Clear(); demoIndices = null;
            visibleGeometry = visibleSource = null; anyVisibleValid = false; visibilityVersion++;
        }
        static PreviewRendererInfo RendererInfo(Renderer renderer, Transform root, int index, int[] slots)
        {
            var names = new List<string>(); var keys = new List<string>();
            for (var t = renderer.transform; t != null; t = t.parent)
            {
                names.Insert(0, t.name);
                if (t == root) break;
                keys.Insert(0, t.GetSiblingIndex().ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
            }
            int component = Array.IndexOf(renderer.GetComponents<Renderer>(), renderer);
            return new PreviewRendererInfo { Index = index, Key = string.Join("/", keys) + ":" + component, Names = names.ToArray(), Slots = (int[])slots.Clone() };
        }
        Transform visibilityRoot;
    }
}
