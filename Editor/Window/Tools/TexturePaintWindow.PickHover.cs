using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        Vector2 pickHoverPointer = new Vector2(-100, -100);
        int? idHoverRgb;
        internal int? IdPickHoverRgb => idHoverRgb;
        /// <summary>今のセットの面か（<see cref="PaintsSlot"/> と同じ。同じマテリアルのスロットは全部）。</summary>
        bool PickSlotInCurrentSet(int slot) => PaintsSlot(slot);
        bool UsesIdHover => tool == PaintTool.IdSelect || IdColorPicking;
        bool UsesRegionHover => tool == PaintTool.PolygonFill || tool == PaintTool.Fill || tool >= PaintTool.SelectRectangle && tool <= PaintTool.MagicWand;
        void HandlePickHover(Event e)
        {
            if (e.type == EventType.MouseLeaveWindow) { pickHoverPointer = new Vector2(-100, -100); ClearPolygonFillHover(); Repaint(); }
            else if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag) UpdatePolygonFillHover(e.mousePosition);
        }
        void UpdateIdPickHover(Vector2 pointer)
        {
            if (!surfaceRect.Contains(pointer) || preview == null || !preview.CanPaint || !preview.TryPick(surfaceRect, pointer, out var hit) || !PickSlotInCurrentSet(hit.MaterialSlot))
            { ClearPolygonFillHover(); return; }
            var map = UsableIdMap(out _);
            if (map == null || !IdMapColors.TryGetAtUv(map, hit.UV.x, hit.UV.y, out int rgb)) { ClearPolygonFillHover(); return; }
            int tolerance = idSelectTolerance;
            if (IdColorPicking)
            {
                var effect = PickedIdGenerator(); if (effect == null) { ClearPolygonFillHover(); return; }
                var g = effect.Settings.Generator; tolerance = g.IdTolerance;
                if (g.Pins.TryGetValue(MeshMapKind.Id, out string pin) && pin != map.Provenance.ConditionKey) { ClearPolygonFillHover(); return; }
            }
            hoverTriangle = -1; hoverKey = -1; hoverOutline = null; hoverOutlineGui = null;
            var index = RegionIndex();
            idHoverRgb = preview.ShowIdRegion(index.Key(hit.TriangleIndex, SurfaceRegionKind.Material), index.Region(hit.TriangleIndex, SurfaceRegionKind.Material),
                PaintHighlight * new Color(1, 1, 1, .24f), map, rgb, tolerance, System.Math.Min(PainterSettings.GpuCacheBytes, PainterSettings.StrokeBudgetBytes)) ? rgb : (int?)null;
            if (!idHoverRgb.HasValue && preview.IdHighlightRefusal != null) message = L.Tr(preview.IdHighlightRefusal);
        }
    }
}
