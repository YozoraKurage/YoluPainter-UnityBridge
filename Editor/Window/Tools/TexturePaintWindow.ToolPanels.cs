using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// プロパティの欄のうち、今のツールの設定（ToolSections）。部品は <see cref="PaintGui"/>、文字は <see cref="L"/> で訳す。
    /// ブラシは Photoshop のブラシ設定のように、よく触る「ブラシ」と「手ぶれ補正・入り抜き」を開いておき、細かい設定（ジッター・散布、
    /// テクスチャ、デュアルブラシ、色の変化、フェード・ペンの傾き）は初めは見出しだけにする。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        void ToolSections(UiRows rows)
        {
            switch (tool)
            {
                case PaintTool.Brush: BrushSections(rows); break;
                case PaintTool.Blur: case PaintTool.Smudge: case PaintTool.Clone: BrushEffectSection(rows); BrushSections(rows); break;
                case PaintTool.PolygonFill: case PaintTool.Fill: MaterialSection(rows); SurfacePickSection(rows); break;
                case PaintTool.Gradient:
                    MaterialSection(rows);
                    GradientMaterialSection(rows);
                    break;
                case PaintTool.SelectRectangle: case PaintTool.SelectEllipse: case PaintTool.Lasso: case PaintTool.MagicWand:
                    SurfacePickSection(rows);
                    SelectionModifySection(rows); break;
                case PaintTool.IdSelect: IdMapSection(rows); SelectionModifySection(rows); break; // Tools/TexturePaintWindow.IdSelect.cs
                case PaintTool.Move: TransformSection(rows); break;
                case PaintTool.Path: MaterialSection(rows); PathSection(rows); break;
            }
        }

        /// <summary>初めは閉じておくセクション（細かい設定。Photoshop のブラシ設定の一覧のように、見出しだけを並べる）。</summary>
        static readonly HashSet<string> ToolSectionsClosedAtFirst = new HashSet<string> { "brush-jitter", "brush-texture", "brush-dual", "brush-color", "brush-fade" };
        /// <summary>ツールのセクションのキー（テストがすべて開いて描くため）。</summary>
        internal static readonly string[] ToolSectionKeys = { "brush-effect", "brush", "brush-material", "brush-stroke", "brush-symmetry", "brush-jitter", "brush-texture", "brush-dual", "brush-color", "brush-fade", "surface-pick", "selection-modify", "id-map", "move", "path" };
        internal void SetToolSectionsOpen(bool open) { foreach (var key in ToolSectionKeys) sectionOpen[key] = open; }

        /// <summary>ツールのセクションの見出し（開いていれば true）。初めの開閉は <see cref="ToolSectionsClosedAtFirst"/>。</summary>
        bool ToolSection(UiRows rows, string key, string title, string icon)
        {
            if (!sectionOpen.ContainsKey(key) && ToolSectionsClosedAtFirst.Contains(key)) sectionOpen[key] = false;
            return Section(rows, key, title, icon);
        }

        /// <summary>行の左に名前を置く部品（ドロップダウン・値の表示・カーブ）の名前の幅。</summary>
        const float LabelColumn = 80;

        /// <summary>描いたツールの部品の画面上の矩形（Repaint のたびに覚える）。テストがその部品を本物のマウスの入力で押すため。</summary>
        internal readonly Dictionary<string, Rect> ToolControlScreenRects = new Dictionary<string, Rect>();
        readonly Dictionary<string, Rect> toolControlPanelRects = new Dictionary<string, Rect>();
        Rect Mark(string id, Rect r)
        {
            if (Event.current.type == EventType.Repaint) { ToolControlScreenRects[id] = GUIUtility.GUIToScreenRect(r); toolControlPanelRects[id] = r; }
            return r;
        }
        /// <summary>テスト用: 覚えた部品が見えるところまでプロパティの欄を送る（行き過ぎは次の描画で欄が範囲に収める）。</summary>
        internal void ScrollPropertiesTo(string id) { if (toolControlPanelRects.TryGetValue(id, out var r)) propertiesScroll.y = Mathf.Max(0, r.y - 40); }

        /// <summary>0..1 の値を % で見せるスライダー。動かしたときだけ値を変える（描くだけで ×100÷100 の丸めが入らないように）。</summary>
        static float PercentSlider(Rect r, string label, float value, float min, float max, string tooltip = null, bool enabled = true)
        {
            float shown = value * 100, next = PaintGui.FitSlider(r, label, shown, min * 100, max * 100, "0", "%", tooltip, enabled);
            return next != shown ? next / 100 : value;
        }

        // ───────── ブラシ ─────────

        void BrushSections(UiRows rows)
        {
            BrushTipSection(rows);
            MaterialSection(rows); // マテリアルで塗る（Tools/TexturePaintWindow.MaterialBrush.cs）
            StrokeAssistSection(rows);
            SymmetrySection(rows); // 3D ビューのシンメトリー（Model/TexturePaintWindow.Symmetry.cs）
            JitterSection(rows);
            TextureSection(rows);
            DualBrushSection(rows);
            if (tool == PaintTool.Brush) ColorDynamicsSection(rows);
            FadeTiltSection(rows);
        }

        void BrushTipSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush", L.Tr("Brush"), "paint_brush")) return;
            if (tool == PaintTool.Brush && !brush.material && (channel == PaintChannel.Roughness || channel == PaintChannel.Metallic || channel == PaintChannel.Height))
            {
                // データのチャンネルはブラシの色の明るさを値として描く（マテリアルで塗るときは「マテリアル」の節の値）（色は灰色にそろえる。以前の欄と同じ）
                float scalar = PaintGui.FitSlider(rows.Row(), L.Tr("Value"), brush.color.r, 0, 1, "0.00", "", L.Tr("The value this channel is painted with"));
                brush.color = new Color(scalar, scalar, scalar, brush.color.a);
            }
            bool round = string.IsNullOrEmpty(brush.tipId), missing = !round && BrushTips.ResolveRef(brush.tipId) == null;
            PaintGui.ValueBox(rows.Row(), L.Tr("Tip"), round ? L.Tr("Round (hardness)") : missing ? L.Tr("Missing") + ": " + brush.tipId : brush.tipId, LabelColumn,
                missing ? PaintTheme.Warning : (Color?)null, L.Tr("The tip comes with the brush preset. Choose a preset or import brushes to change it."), !round);
            var c = UiRows.Split(rows.Row(), 2, 6);
            brush.angle = PaintGui.FitSlider(c[0], L.Tr("Angle"), brush.angle, -180, 180, "0", "°", L.Tr("Rotation of the tip"));
            brush.roundness = PercentSlider(c[1], L.Tr("Roundness"), brush.roundness, .01f, 1, L.Tr("Squashes the tip along its angle (100% keeps its shape)"));
            c = UiRows.Split(rows.Row(), 2, 6);
            brush.spacing = PercentSlider(Mark("spacing", c[0]), L.Tr("Spacing"), brush.spacing, .01f, 1, L.Tr("Distance between dabs, in % of the diameter"));
            brush.followDirection = PaintGui.FitToggle(c[1], L.TrIn("brush", "Follow direction"), brush.followDirection, L.Tr("Turns the tip with the direction of the stroke (added to the angle)"));

            PaintGui.GroupLabel(rows.Row(16), L.TrIn("brush", "Pen pressure"));
            c = UiRows.Split(rows.Row(), 3, 6);
            brush.pressureSize = PaintGui.FitToggle(c[0], L.Tr("Size"), brush.pressureSize, L.Tr("Pressure controls size"));
            brush.pressureOpacity = PaintGui.FitToggle(c[1], L.Tr("Opacity"), brush.pressureOpacity, L.Tr("Pressure controls opacity"));
            brush.pressureFlow = PaintGui.FitToggle(c[2], L.Tr("Flow"), brush.pressureFlow, L.Tr("Pressure flow"));
            PressureCurveRows(rows);

            rows.Space(2);
            var preset = rows.Row(24);
            float x = preset.xMax - 4 * 28 + 2;
            PaintGui.Text(new Rect(preset.x, preset.y, x - preset.x - 4, preset.height), PaintGui.Fit(L.TrIn("brush", "Preset"), x - preset.x - 4, PaintTheme.Label), PaintTheme.Label);
            Rect B() { var b = new Rect(x, preset.y, 26, preset.height); x += 28; return b; }
            if (PaintGui.IconButton(B(), "save", L.Tr("Save Preset…"), false, true, 17)) SavePreset();
            if (PaintGui.IconButton(B(), "folder_open", L.Tr("Load Preset…"), false, true, 17)) LoadPreset();
            if (PaintGui.IconButton(B(), "import", L.Tr("Import Brushes…"), false, true, 17)) ImportBrushes();
            if (PaintGui.IconButton(B(), "delete", L.Tr("Delete Imported Brush"), false, BrushLibrary.IsLibraryPreset(brush.presetId), 17)) DeleteImportedBrush();
            rows.Space(4);
        }

        /// <summary>筆圧のカーブのグラフの高さ。</summary>
        internal const float PressureCurveHeight = 84;

        /// <summary>筆圧のカーブ: 名前・よく使う形のメニュー・線形に戻すボタンの行と、直接編集するグラフ（<see cref="PaintGui.CurveEditor"/>）。</summary>
        void PressureCurveRows(UiRows rows)
        {
            var head = rows.Row();
            PaintGui.Text(new Rect(head.x, head.y, LabelColumn, head.height), PaintGui.Fit(L.TrIn("brush", "Curve"), LabelColumn - 6, PaintTheme.Label), PaintTheme.Label);
            int shape = Array.FindIndex(PressureCurve.Presets, p => PressureCurve.Matches(brush.pressureCurve, p.Points));
            PaintGui.FitDropdown(Mark("pressure-curve-preset", new Rect(head.x + LabelColumn, head.y, head.width - LabelColumn - 30, head.height)), null,
                shape >= 0 ? CurvePresetTitle(shape) : L.Tr("Custom"), at =>
                {
                    var menu = new GenericMenu();
                    for (int i = 0; i < PressureCurve.Presets.Length; i++) { int k = i; menu.AddItem(new GUIContent(CurvePresetTitle(k)), k == shape, () => SetPressureCurve(PressureCurve.Presets[k].Points)); }
                    menu.DropDown(at);
                }, L.Tr("Common pressure curves"));
            if (PaintGui.IconButton(Mark("pressure-curve-reset", new Rect(head.xMax - 26, head.y, 26, head.height)), "restart_alt", L.TrIn("pressure curve", "Reset to linear"), false, shape != 0, 17))
                SetPressureCurve(PressureCurve.Presets[0].Points);
            brush.pressureCurve = PaintGui.CurveEditor(Mark("pressure-curve", rows.Row(PressureCurveHeight)), brush.pressureCurve,
                L.Tr("Pressure curve") + "\n" + L.Tr("Pen pressure (left to right) becomes the pressure the brush uses (bottom to top). Click to add a point and drag it; right-click or drag a point out of the box to remove it."));
        }

        static string CurvePresetTitle(int index)
        {
            switch (index)
            {
                case 0: return L.Tr("Linear");
                case 1: return L.TrIn("pressure curve", "Soft");
                case 2: return L.TrIn("pressure curve", "Hard");
                default: return L.TrIn("pressure curve", "S-curve");
            }
        }

        /// <summary>筆圧のカーブを points を通る形にする（よく使う形のメニューと、線形に戻すボタン）。</summary>
        internal void SetPressureCurve(System.Collections.Generic.IList<Vector2> points) { brush.pressureCurve = PressureCurve.FromPoints(points); Repaint(); }

        void JitterSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-jitter", L.Tr("Jitter & Scatter"), "data_scatter")) return;
            PaintGui.GroupLabel(rows.Row(16), L.TrIn("brush", "Jitter"), L.Tr("Each dab varies at random by up to this much"));
            var c = UiRows.Split(rows.Row(), 2, 6);
            brush.sizeJitter = PercentSlider(c[0], L.Tr("Size"), brush.sizeJitter, 0, 1, L.Tr("Size jitter"));
            brush.angleJitter = PercentSlider(c[1], L.Tr("Angle"), brush.angleJitter, 0, 1, L.Tr("Angle jitter"));
            c = UiRows.Split(rows.Row(), 2, 6);
            brush.roundnessJitter = PercentSlider(c[0], L.Tr("Roundness"), brush.roundnessJitter, 0, 1, L.Tr("Roundness jitter"));
            brush.opacityJitter = PercentSlider(c[1], L.Tr("Opacity"), brush.opacityJitter, 0, 1, L.Tr("Opacity jitter"));
            c = UiRows.Split(rows.Row(), 2, 6);
            brush.flowJitter = PercentSlider(c[0], L.Tr("Flow"), brush.flowJitter, 0, 1, L.Tr("Flow jitter"));
            PaintGui.GroupLabel(rows.Row(16), L.Tr("Scatter"));
            c = UiRows.Split(rows.Row(), 2, 6);
            brush.scatter = PercentSlider(c[0], L.Tr("Scatter"), brush.scatter, 0, 10, L.Tr("How far the dabs spread across the stroke, in % of the diameter"));
            brush.count = PaintGui.FitIntSlider(c[1], L.Tr("Count"), brush.count, 1, 16, "", L.Tr("Dabs placed at every spacing step"));
            rows.Space(4);
        }

        // ───────── 選択範囲 ─────────

        void SelectionModifySection(UiRows rows)
        {
            if (!ToolSection(rows, "selection-modify", L.Tr("Modify Selection"), "select_all")) return;
            bool any = document.Selection != null;
            var row = rows.Row();
            float split = Mathf.Round(row.width * .58f);
            selectionRadius = Mathf.Clamp(PaintGui.FitIntSlider(Mark("radius", new Rect(row.x, row.y, split, row.height)), L.TrIn("selection", "Radius"), selectionRadius, 0, SelectionMask.MaxModifyRadius, " px",
                L.Tr("Radius for Grow, Shrink, Border and Feather (as GIMP's Select menu)"), any), 0, SelectionMask.MaxModifyRadius);
            selectionEdgeLock = PaintGui.FitToggle(new Rect(row.x + split + 8, row.y, row.width - split - 8, row.height), L.TrIn("selection", "Edge lock"), selectionEdgeLock,
                L.Tr("Selected areas continue outside the canvas (Shrink, Border and Feather do not pull away from the canvas edge)"), any);
            var c = UiRows.Split(rows.Row(24), 3, 4);
            if (PaintGui.FitButton(Mark("grow", c[0]), L.Tr("Grow"), false, any, L.Tr("Largest amount within a circle of the radius"))) TryAction(() => ModifySelection(SelectionModifyKind.Grow));
            if (PaintGui.FitButton(c[1], L.Tr("Shrink"), false, any, L.Tr("Smallest amount within a circle of the radius"))) TryAction(() => ModifySelection(SelectionModifyKind.Shrink));
            if (PaintGui.FitButton(c[2], L.Tr("Border"), false, any, L.Tr("A band around the edge: Grow minus Shrink"))) TryAction(() => ModifySelection(SelectionModifyKind.Border));
            c = UiRows.Split(rows.Row(24), 2, 4);
            if (PaintGui.FitButton(c[0], L.Tr("Feather"), false, any, L.Tr("Soften the edge (Gaussian blur, σ = radius / 3.5)"))) TryAction(() => ModifySelection(SelectionModifyKind.Feather));
            if (PaintGui.FitButton(c[1], L.Tr("Sharpen Edge"), false, any, L.Tr("Hard edge: at least half selected becomes fully selected"))) TryAction(() => ModifySelection(SelectionModifyKind.Sharpen));
            if (!any) PaintGui.Text(rows.Row(16), PaintGui.Fit(L.Tr("Make a selection first."), rows.Width, PaintTheme.LabelSmall), PaintTheme.LabelSmall);
            rows.Space(4);
        }

        // ───────── 移動・変形 ─────────

        void TransformSection(UiRows rows)
        {
            if (!ToolSection(rows, "move", L.Tr("Transform"), "transform")) return;
            PaintGui.Paragraph(rows, L.Tr("Drag or arrow keys (Shift: 10 px) move the layer and its mask, or the selected pixels with the selection."));
            var row = rows.Row(24); float x = row.x;
            Rect B() { var b = new Rect(x, row.y, 28, row.height); x += 30; return b; }
            if (PaintGui.IconButton(Mark("flip-horizontal", B()), "flip", L.Tr("Flip Horizontal"), false, true, 18)) TryAction(() => TransformSelected(0, 0, 0, -1, 1, L.Tr("Flipped horizontally.")));
            if (PaintGui.IconButton(B(), "flip_vertical", L.Tr("Flip Vertical"), false, true, 18)) TryAction(() => TransformSelected(0, 0, 0, 1, -1, L.Tr("Flipped vertically.")));
            if (PaintGui.IconButton(B(), "rotate_90_degrees_ccw", L.Tr("Rotate 90° Counter-clockwise"), false, true, 18)) TryAction(() => TransformSelected(0, 0, 90, 1, 1, L.Tr("Rotated 90° counter-clockwise.")));
            if (PaintGui.IconButton(B(), "rotate_90_degrees_cw", L.Tr("Rotate 90° Clockwise"), false, true, 18)) TryAction(() => TransformSelected(0, 0, -90, 1, 1, L.Tr("Rotated 90° clockwise.")));

            PaintGui.GroupLabel(rows.Row(16), L.Tr("Numeric transform"));
            var c = UiRows.Split(rows.Row(), 2, 6);
            moveOffset.x = PaintGui.NumberField(Mark("offset-x", c[0]), "X", moveOffset.x, "0.##", " px", 1, tooltip: L.Tr("Horizontal offset (px, + is right)"));
            moveOffset.y = PaintGui.NumberField(c[1], "Y", moveOffset.y, "0.##", " px", 1, tooltip: L.Tr("Vertical offset (px, + is up)"));
            c = UiRows.Split(rows.Row(), 2, 6);
            moveScale.x = PaintGui.NumberField(c[0], "W", moveScale.x, "0.##", "%", 1, tooltip: L.Tr("Horizontal scale (%; a negative value flips)"));
            moveScale.y = PaintGui.NumberField(c[1], "H", moveScale.y, "0.##", "%", 1, tooltip: L.Tr("Vertical scale (%; a negative value flips)"));
            c = UiRows.Split(rows.Row(), 2, 6);
            moveAngle = PaintGui.NumberField(c[0], L.Tr("Angle"), moveAngle, "0.##", "°", 1, tooltip: L.Tr("Counter-clockwise, about the centre of what moves"));
            if (PaintGui.FitButton(c[1], L.TrIn("transform", "Reset"), false, true, L.Tr("Back to no change"))) { moveAngle = 0; moveScale = new Vector2(100, 100); moveOffset = Vector2.zero; }
            PaintGui.FitDropdown(rows.Row(), L.Tr("Resampling"), L.Tr(moveResampling.ToString()), at =>
            {
                var menu = new GenericMenu();
                foreach (Resampling mode in Enum.GetValues(typeof(Resampling))) { var m = mode; menu.AddItem(new GUIContent(L.Tr(m.ToString())), m == moveResampling, () => moveResampling = m); }
                menu.DropDown(at);
            }, L.Tr("Bilinear smooths, Nearest keeps hard pixels. Whole-pixel moves, 90° turns and flips copy pixels exactly either way."), true, LabelColumn);
            if (PaintGui.FitButton(Mark("apply-transform", rows.Row(24)), L.TrIn("transform", "Apply"), true, true, L.Tr("Transform by these numbers (one undo step)"))) TryAction(ApplyNumericTransform);
            rows.Space(4);
        }
    }
}
