using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        IdPartIndex idParts; int idEditPart;
        int[] idPartPalette, idEditorParts, idPartFirst; int idEditorSlot = -1;
        internal IdPartIndex IdParts()
        {
            var input = CurrentMeshBakeInput(); if (input == null) return null;
            if (idParts == null || !ReferenceEquals(input, idParts.Input))
            {
                idParts = new IdPartIndex(input); idPartPalette = null; idEditorParts = null;
                idPartFirst = Enumerable.Repeat(-1, idParts.Count).ToArray();
                for (int t = 0; t < input.TriangleCount; t++) if (idPartFirst[idParts.Parts[t]] < 0) idPartFirst[idParts.Parts[t]] = t;
            }
            return idParts;
        }
        /// <summary>同じアセットは GUID と localFileID で比較する。アセットでないマテリアルはそのオブジェクトの参照だけ同一視する。
        /// null は空（コアでスロットごとの色になる）。元のマテリアルは読むだけ。</summary>
        internal static IReadOnlyList<string> MaterialIdentityKeys(IsolatedModelPreview source)
        {
            var slots = new string[source.MaterialSlotCount];
            for (int slot = 0; slot < slots.Length; slot++)
            {
                var material = source.SourceMaterial(slot); if (material == null) continue;
                slots[slot] = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long fileId)
                    ? guid + ":" + fileId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "instance:" + material.GetInstanceID();
            }
            return source.Geometry.Triangles.Select(t => t.MaterialSlot >= 0 && t.MaterialSlot < slots.Length ? slots[t.MaterialSlot] : null).ToArray();
        }
        internal void AssignIdPartColor(int part, int? rgb, bool coalesce = false)
        {
            var index = IdParts(); if (index == null) throw new InvalidOperationException("Load a model to assign ID colours.");
            if (part < 0 || part >= index.Count) throw new ArgumentOutOfRangeException(nameof(part));
            document.SetIdColors(document.IdColors.WithColor(index.Binding, part, rgb), coalesce);
            message = L.Tr("Manual ID colours changed. Bake ID again; Ctrl+Z undoes the colour edit.");
            Repaint(); RepaintPanelWindowsSoon();
        }
        /// <summary>プロパティとベイクの窓で共有する手動色の欄。低ポリの塊の手動色は生成元と高ポリからの色より優先する。
        /// 対応の違うモデルは明示的にリセットするまで編集・ベイクを断る。</summary>
        void IdColorAssignmentRows(UiRows rows, Action repaint)
        {
            PaintGui.GroupLabel(rows.Row(18, 4), L.Tr("Manual part colours"));
            var index = IdParts();
            if (index == null) { NoteRow(rows, L.Tr("Load a model to assign ID colours.")); return; }
            var colors = document.IdColors;
            bool stale = colors.Colors.Count > 0 && colors.Binding != index.Binding;
            if (stale) NoteRow(rows, L.Tr("These manual colours belong to another model. Reset them before editing or baking ID."), NoteKind.Warning);
            else
            {
                if (idEditorParts == null || idEditorSlot != materialSlot)
                {
                    idEditorParts = Enumerable.Range(0, index.Input.TriangleCount).Where(t => PickSlotInCurrentSet(preview.Geometry.Triangles[t].MaterialSlot))
                        .Select(t => index.Parts[t]).Distinct().OrderBy(p => p).ToArray(); idEditorSlot = materialSlot;
                }
                var parts = idEditorParts;
                if (parts.Length > 0)
                {
                    if (!parts.Contains(idEditPart)) idEditPart = parts[0];
                    string PartName(int p) => L.Tr("Part {0}", p + 1);
                    PaintGui.FitDropdown(Mark("id-part", rows.Row()), L.TrIn("3D pick", "Mesh Part"), PartName(idEditPart), at =>
                    {
                        var menu = new GenericMenu();
                        foreach (int part in parts) { int selected = part; menu.AddItem(new GUIContent(PartName(part)), part == idEditPart, () => { idEditPart = selected; repaint(); Repaint(); }); }
                        menu.DropDown(at);
                    }, L.Tr("Choose a connected mesh part of this texture set"), stroke == null && meshBakeJob == null, LabelColumn);
                    bool assigned = colors.Colors.TryGetValue(idEditPart, out int rgb);
                    if (!assigned)
                    {
                        if (idPartPalette == null) idPartPalette = IdPalette.Colors(index.Count);
                        rgb = idPartPalette[idEditPart];
                        var map = UsableIdMap(out _);
                        int first = idPartFirst[idEditPart];
                        var t = preview.Geometry.Triangles[first]; var uv = (t.UvA + t.UvB + t.UvC) / 3;
                        if (map != null && IdMapColors.TryGetAtUv(map, uv.x, uv.y, out int baked)) rgb = baked;
                    }
                    var row = rows.Row(24); int chosen = idEditPart; var owner = document; string binding = index.Binding;
                    PaintGui.ColorSwatch(Mark("id-part-color", new Rect(row.x, row.y, 48, row.height)), new Color((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f),
                        c => { if (!ReferenceEquals(document, owner) || IdParts()?.Binding != binding || stroke != null || meshBakeJob != null) return;
                            TryAction(() => AssignIdPartColor(chosen, Mathf.RoundToInt(c.r * 255) << 16 | Mathf.RoundToInt(c.g * 255) << 8 | Mathf.RoundToInt(c.b * 255), true)); repaint(); }, false,
                        L.Tr("Choose this part's ID colour"), stroke == null && meshBakeJob == null);
                    PaintGui.Text(new Rect(row.x + 56, row.y, 100, row.height), IdMapColors.Hex(rgb));
                    if (PaintGui.Button(Mark("id-part-reset", new Rect(row.xMax - 100, row.y, 100, row.height)), L.Tr("Automatic"), false, assigned && stroke == null && meshBakeJob == null))
                        TryAction(() => { AssignIdPartColor(chosen, null); repaint(); });
                }
                NoteRow(rows, L.Tr("Manual colours override the source and high poly on these mesh parts. Equal colours select together. Bake ID again after editing."));
            }
            if (PaintGui.Button(Mark("id-colors-reset", rows.Row(24)), L.Tr("Reset all manual colours"), false, colors.Colors.Count > 0 && stroke == null && meshBakeJob == null))
                TryAction(() => { document.SetIdColors(IdColorAssignments.Empty); repaint(); Repaint(); });
        }
    }
}
