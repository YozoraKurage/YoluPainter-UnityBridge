using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        /// <summary>表示だけの状態。正本とUndoを変えず、窓のリロードと .ylp の view.json で戻す。</summary>
        [Serializable] internal sealed class VisibilityState
        {
            public List<string> hiddenSets = new List<string>();
            public List<string> hiddenRenderers = new List<string>();
        }
        [SerializeField] VisibilityState visibility = new VisibilityState();
        [SerializeField] bool meshesExpanded;
        Vector2 meshScroll;
        string appliedVisibility; int appliedVisibilitySnapshot = -1; long appliedVisibilitySetsRevision = -1;
        /// <summary>セットが受け持つスロット集合。マテリアル単位のセットとの統合は、この関数だけを差し替える。</summary>
        internal IEnumerable<int> TextureSetSlots(TextureSet set) => set.Slots;
        internal bool TextureSetVisible(Guid id) => !visibility.hiddenSets.Contains(id.ToString("D"));
        internal bool RendererVisible(string key) => !visibility.hiddenRenderers.Contains(key);
        void ResetVisibility(bool sets = true)
        {
            if (sets) visibility.hiddenSets.Clear();
            visibility.hiddenRenderers.Clear(); appliedVisibility = null; ApplyVisibility();
        }
        internal void SetTextureSetVisible(Guid id, bool visible, bool isolate = false)
        {
            if (!textureSets.Any(s => s.Id == id)) throw new ArgumentOutOfRangeException(nameof(id));
            CancelVisibilityInput();
            if (isolate) { visibility.hiddenSets = textureSets.Where(s => s.Id != id).Select(s => s.Id.ToString("D")).ToList(); visibility.hiddenRenderers.Clear(); }
            else ChangeVisibility(visibility.hiddenSets, id.ToString("D"), visible);
            VisibilityChanged();
        }
        internal void SetRendererVisible(string key, bool visible, bool isolate = false)
        {
            if (preview == null || !preview.Renderers.Any(r => r.Key == key)) throw new ArgumentOutOfRangeException(nameof(key));
            CancelVisibilityInput();
            if (isolate)
            {
                visibility.hiddenRenderers = preview.Renderers.Where(r => r.Key != key).Select(r => r.Key).ToList();
                // レンダラーだけを見せるなら、セットの目で隠していた面も見えるようにする。
                visibility.hiddenSets.Clear();
            }
            else ChangeVisibility(visibility.hiddenRenderers, key, visible);
            VisibilityChanged();
        }
        static void ChangeVisibility(List<string> hidden, string key, bool visible)
        { if (visible) hidden.Remove(key); else if (!hidden.Contains(key)) hidden.Add(key); }
        void CancelVisibilityInput()
        { pathDrag = -1; GUIUtility.hotControl = 0; FinishStroke(false); CancelToolDrag(); CancelShapeDrag(); CancelGradientDrafts(); EndLightingDrag(true); ReleaseCanvasViewInput(); ReleaseStencilInput(); preview?.CancelNavigation(); }
        void VisibilityChanged()
        {
            appliedVisibility = null; ApplyVisibility(); ClearPolygonFillHover(); Repaint();
            foreach (var window in PanelWindows) window.Repaint();
        }
        internal void ShowAllModelParts()
        { CancelVisibilityInput(); visibility.hiddenSets.Clear(); visibility.hiddenRenderers.Clear(); VisibilityChanged(); }
        void ApplyVisibility()
        {
            if (preview == null) return;
            if (visibility == null) visibility = new VisibilityState();
            if (appliedVisibility != null && preview.SnapshotRevision == appliedVisibilitySnapshot && setsRevision == appliedVisibilitySetsRevision) return;
            visibility.hiddenSets.RemoveAll(id => textureSets.All(s => s.Id.ToString("D") != id));
            string key = string.Join(",", visibility.hiddenSets) + "|" + string.Join(",", visibility.hiddenRenderers) + "|" + string.Join(",", textureSets.SelectMany(s => TextureSetSlots(s).Select(slot => s.Id + ":" + slot)));
            if (key == appliedVisibility && preview.SnapshotRevision == appliedVisibilitySnapshot) { appliedVisibilitySetsRevision = setsRevision; return; }
            var slots = textureSets.Where(s => !TextureSetVisible(s.Id)).SelectMany(TextureSetSlots).Where(s => s >= 0 && s < preview.MaterialSlotCount);
            var renderers = preview.Renderers.Where(r => !RendererVisible(r.Key)).Select(r => r.Index);
            preview.SetVisibility(renderers, slots);
            appliedVisibility = key; appliedVisibilitySnapshot = preview.SnapshotRevision; appliedVisibilitySetsRevision = setsRevision;
        }
        internal VisibilityState CaptureVisibility() => new VisibilityState { hiddenSets = new List<string>(visibility.hiddenSets), hiddenRenderers = new List<string>(visibility.hiddenRenderers) };
        internal void RestoreVisibility(VisibilityState state)
        {
            state = state ?? new VisibilityState();
            var sets = state.hiddenSets ?? new List<string>(); var renderers = state.hiddenRenderers ?? new List<string>();
            if (sets.Count > 64 || renderers.Count > 4096 || sets.Any(s => !Guid.TryParseExact(s, "D", out var id) || id == Guid.Empty)
                || renderers.Any(s => string.IsNullOrEmpty(s) || s.Length > 2048 || s.Any(c => !(char.IsDigit(c) || c == '/' || c == ':' || c >= 'a' && c <= 'z'))))
                throw new InvalidDataException("Invalid model visibility state.");
            visibility = new VisibilityState { hiddenSets = sets.Distinct().Where(s => textureSets.Any(t => t.Id.ToString("D") == s)).ToList(), hiddenRenderers = renderers.Distinct().ToList() };
            appliedVisibility = null; ApplyVisibility();
        }
        internal IEnumerable<int> VisibleSurfaceRegion(IEnumerable<int> triangles) => triangles.Where(preview.IsTriangleVisible);

        sealed class MeshRow { internal PreviewRendererInfo Renderer; internal string Name; internal int Depth; }
        List<MeshRow> MeshRows()
        {
            var rows = new List<MeshRow>(); var parents = new HashSet<string>();
            foreach (var renderer in preview.Renderers.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                string trail = ""; var indices = renderer.Key.Split(':')[0].Split('/');
                for (int depth = 0; depth < renderer.Names.Length - 1; depth++)
                {
                    trail += "/" + (depth == 0 ? "root" : indices[depth - 1]);
                    if (parents.Add(trail)) rows.Add(new MeshRow { Name = renderer.Names[depth], Depth = depth });
                }
                rows.Add(new MeshRow { Renderer = renderer, Name = renderer.Names.Last(), Depth = renderer.Names.Length - 1 });
            }
            return rows;
        }
        float MeshListHeight => !meshesExpanded || preview == null || !preview.HasModel ? 0 : Mathf.Min(MeshRows().Count, 5) * 24;
        float MeshVisibilityHeight => preview != null && preview.HasModel ? 26 + MeshListHeight + 6 : 0;
        void DrawMeshVisibility(UiRows rows)
        {
            if (preview == null || !preview.HasModel) return;
            var head = rows.Row(24, 2);
            if (PaintGui.FitButton(SetSpot("visibility.meshes", new Rect(head.x, head.y, head.width - 28, head.height)), (meshesExpanded ? "▾ " : "▸ ") + L.Tr("Meshes"))) meshesExpanded = !meshesExpanded;
            if (PaintGui.IconButton(SetSpot("visibility.showAll", new Rect(head.xMax - 24, head.y, 24, 24)), "visibility", L.Tr("Show all meshes and texture sets"), false, !PanelsLocked, 16)) ShowAllModelParts();
            if (!meshesExpanded) { rows.Space(6); return; }
            var list = rows.Row(MeshListHeight, 6); var items = MeshRows(); float content = items.Count * 24;
            meshScroll.y = Mathf.Clamp(meshScroll.y, 0, Mathf.Max(0, content - list.height));
            PaintGui.Rounded(list, PaintTheme.MenuBg, 4);
            string clicked = null; bool alt = false;
            if (PaintGui.Scrollbar(list, ref meshScroll, content, 12)) Repaint();
            PaintGui.BeginScroll(list, meshScroll);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i]; var row = new Rect(0, i * 24, PaintGui.ScrollContentWidth(list, content), 24);
                bool visible = item.Renderer == null || RendererVisible(item.Renderer.Key);
                if (item.Renderer != null)
                {
                    var eye = new Rect(1, row.y, 24, 24);
                    PaintGui.Icon(eye, visible ? "visibility" : "visibility_off", visible ? PaintTheme.Text : PaintTheme.TextDisabled, 16);
                    PaintGui.Tooltip(eye, L.Tr("Toggle mesh visibility. Alt-click shows only this mesh."));
                    var input = Event.current;
                    if (new Rect(0, meshScroll.y, list.width, list.height).Contains(input.mousePosition) && eye.Contains(input.mousePosition)
                        && input.type == EventType.MouseDown && input.button == 0 && GUI.enabled && !PanelsLocked)
                    { clicked = item.Renderer.Key; alt = input.alt; input.Use(); }
                }
                float x = 28 + Mathf.Min(item.Depth, 12) * 12;
                PaintGui.Text(new Rect(x, row.y, Mathf.Max(0, row.width - x - 6), 24), PaintGui.Fit(item.Name, Mathf.Max(0, row.width - x - 6), PaintTheme.LabelSmall, false), PaintTheme.LabelSmall, visible ? PaintTheme.Text : PaintTheme.TextDisabled);
            }
            PaintGui.EndScroll();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Renderer == null) continue;
                var eye = new Rect(list.x + 1, list.y + i * 24 - meshScroll.y, 24, 24);
                if (eye.y >= list.y && eye.yMax <= list.yMax) SetSpot("visibility.renderer." + items[i].Renderer.Index, eye);
            }
            var e = Event.current;
            if (clicked != null) SetRendererVisible(clicked, !RendererVisible(clicked), alt);
        }
        void DrawHiddenModelNotice()
        {
            if (preview == null || !preview.HasModel || preview.AnyVisible || surfaceRect.width <= 0) return;
            var box = new Rect(surfaceRect.center.x - Mathf.Min(210, surfaceRect.width / 2 - 8), surfaceRect.center.y - 38, Mathf.Min(420, surfaceRect.width - 16), 76);
            PaintGui.Rounded(box, PaintTheme.PanelBg, 6);
            PaintGui.Text(new Rect(box.x + 8, box.y + 8, box.width - 16, 24), L.Tr("All meshes are hidden."), PaintTheme.LabelCenter);
            if (PaintGui.FitButton(new Rect(box.x + 16, box.y + 40, box.width - 32, 24), L.Tr("Show all meshes and texture sets"))) ShowAllModelParts();
        }
    }
}
