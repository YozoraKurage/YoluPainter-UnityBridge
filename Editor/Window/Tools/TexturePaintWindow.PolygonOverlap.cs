using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        readonly List<int> overlapTriangles = new List<int>(), overlapCandidates = new List<int>();
        readonly HashSet<long> overlapRegions = new HashSet<long>();
        SurfaceGeometry overlapGeometry; int overlapSlot = -1, overlapChoice; SurfaceRegionKind overlapKind; long overlapSelected = -1;
        Vector2 overlapPointer;
        internal IReadOnlyList<int> PolygonOverlapCandidates => overlapCandidates;
        internal int PolygonOverlapChoice => overlapChoice;
        int PolygonFillUvCandidate(Vector2 uv)
        {
            var index = RegionIndex(); if (index == null) { overlapCandidates.Clear(); return -1; }
            if (!ReferenceEquals(overlapGeometry, index.Geometry) || overlapSlot != CurrentMaterialGroup || overlapKind != surfacePick)
            { overlapGeometry = index.Geometry; overlapSlot = CurrentMaterialGroup; overlapKind = surfacePick; overlapSelected = -1; }
            index.TrianglesAtUv(CurrentMaterialGroup, uv, overlapTriangles); overlapCandidates.Clear(); overlapRegions.Clear();
            foreach (int triangle in overlapTriangles) if (overlapRegions.Add(index.Key(triangle, surfacePick))) overlapCandidates.Add(triangle);
            overlapChoice = 0;
            for (int i = 0; i < overlapCandidates.Count; i++) if (index.Key(overlapCandidates[i], surfacePick) == overlapSelected) { overlapChoice = i; break; }
            if (overlapCandidates.Count == 0) { overlapSelected = -1; return -1; }
            int chosen = overlapCandidates[overlapChoice]; overlapSelected = index.Key(chosen, surfacePick); return chosen;
        }
        internal void ChoosePolygonOverlap(int choice)
        {
            var index = RegionIndex();
            if (stroke != null || choice < 0 || choice >= overlapCandidates.Count || index == null ||
                !ReferenceEquals(overlapGeometry, index.Geometry) || overlapSlot != CurrentMaterialGroup || overlapKind != surfacePick) return;
            overlapChoice = choice; overlapSelected = RegionIndex().Key(overlapCandidates[choice], surfacePick);
            hoverTriangle = -1; hoverKey = -1; UpdatePolygonFillHover(overlapPointer); Repaint();
            message = L.Tr("UV candidate {0}/{1}", choice + 1, overlapCandidates.Count);
        }
        bool HandlePolygonOverlapKey(Event e)
        {
            if (tool != PaintTool.PolygonFill || e.keyCode != KeyCode.Tab || e.control || e.command || e.alt || stroke != null || !canvasRect.Contains(pickHoverPointer)) return false;
            UpdatePolygonFillHover(pickHoverPointer); if (overlapCandidates.Count < 2) return false;
            ChoosePolygonOverlap((overlapChoice + (e.shift ? overlapCandidates.Count - 1 : 1)) % overlapCandidates.Count); return true;
        }
        void OpenPolygonOverlapMenu(Rect at)
        {
            var menu = new PaintMenu();
            var geometry = overlapGeometry; int slot = overlapSlot; var kind = overlapKind;
            var candidates = overlapCandidates.ToArray();
            for (int i = 0; i < overlapCandidates.Count; i++)
            { int choice = i; menu.AddItem(new GUIContent(L.Tr("Candidate {0} · triangle {1}", i + 1, overlapCandidates[i] + 1)), i == overlapChoice, () =>
                { if (ReferenceEquals(geometry, overlapGeometry) && slot == overlapSlot && kind == overlapKind && candidates.SequenceEqual(overlapCandidates)) ChoosePolygonOverlap(choice); }); }
            menu.DropDown(at);
        }
        void PolygonOverlapOptions(Rect row)
        {
            if (overlapCandidates.Count < 2 || !ReferenceEquals(overlapGeometry, preview?.Geometry) || overlapSlot != CurrentMaterialGroup || overlapKind != surfacePick) return;
            PaintGui.Dropdown(Mark("polyfill-overlap", row), L.Tr("UV overlap"), (overlapChoice + 1) + "/" + overlapCandidates.Count, OpenPolygonOverlapMenu,
                L.Tr("Tab / Shift+Tab or right-click on the 2D canvas chooses an overlapping UV region. Shared pixels still affect all faces."), stroke == null);
        }
    }
}
