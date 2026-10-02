using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// ウィンドウの外枠（ペイントソフトの配置）: 上にメニューバーとツールのオプションバー、左にツールの帯と描画色/背景色、
    /// 中央に 2D キャンバスと 3D ビュー（どちらか、または並べて）、右にドック（テクスチャセット・レイヤー・プロパティ）、下に
    /// ステータスバー。部品は <see cref="PaintGui"/>、文字は <see cref="L"/> で訳す。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        internal enum ViewMode { Split, Canvas, Model }
        /// <summary>ツールの帯の 1 つ（消しゴムはブラシの消す設定）。</summary>
        sealed class ToolSlot { public PaintTool Tool; public bool Erase; public string Id, Name, Key; }

        [SerializeField] ViewMode viewMode = ViewMode.Split;
        Rect menuRect, optionsRect, toolStripRect, viewAreaRect, dockRect, statusRect;

        Guid renamingLayer; double lastLayerClick; Guid lastLayerClicked;
        /// <summary>テストとオフスクリーンの描画用: position の代わりに使う大きさ。</summary>
        internal Rect? LayoutOverride;
        internal ViewMode View { get => viewMode; set { viewMode = value; Repaint(); } }

        static readonly ToolSlot[] ToolSlots =
        {
            new ToolSlot { Tool = PaintTool.Brush, Id = "brush", Name = "Brush", Key = "B" },
            new ToolSlot { Tool = PaintTool.Brush, Erase = true, Id = "eraser", Name = "Eraser", Key = "E" },
            new ToolSlot { Tool = PaintTool.Fill, Id = "fill", Name = "Fill", Key = "G" },
            new ToolSlot { Tool = PaintTool.Gradient, Id = "gradient", Name = "Gradient", Key = "Shift+G" },
            null,
            new ToolSlot { Tool = PaintTool.SelectRectangle, Id = "select-rectangle", Name = "Rectangle Select", Key = "M" },
            new ToolSlot { Tool = PaintTool.SelectEllipse, Id = "select-ellipse", Name = "Ellipse Select", Key = "Shift+M" },
            new ToolSlot { Tool = PaintTool.Lasso, Id = "lasso", Name = "Lasso", Key = "L" },
            new ToolSlot { Tool = PaintTool.MagicWand, Id = "magic-wand", Name = "Magic Wand", Key = "W" },
            null,
            new ToolSlot { Tool = PaintTool.Move, Id = "move", Name = "Move / Transform", Key = "V" },
            new ToolSlot { Tool = PaintTool.Path, Id = "path", Name = "Path", Key = "P" },
            new ToolSlot { Tool = PaintTool.Eyedropper, Id = "eyedropper", Name = "Eyedropper", Key = "I" },
        };

        Rect WindowRect => LayoutOverride ?? new Rect(0, 0, position.width, position.height);

        /// <summary>外枠の矩形と、キャンバス・3D ビューの矩形を決める。</summary>
        void LayoutShell()
        {
            var w = WindowRect;
            menuRect = new Rect(0, 0, w.width, PaintTheme.MenuBarHeight);
            optionsRect = new Rect(0, menuRect.yMax, w.width, PaintTheme.OptionsBarHeight);
            statusRect = new Rect(0, w.height - PaintTheme.StatusBarHeight, w.width, PaintTheme.StatusBarHeight);
            float top = optionsRect.yMax, bottom = statusRect.y;
            toolStripRect = new Rect(0, top, PaintTheme.ToolStripWidth, bottom - top);
            float rightWidth = DockWidth(false, w.width), leftWidth = DockWidth(true, w.width);
            dockRect = new Rect(w.width - rightWidth, top, rightWidth, bottom - top);
            leftDockRect = new Rect(toolStripRect.xMax, top, leftWidth, bottom - top);
            viewAreaRect = new Rect(leftDockRect.xMax, top, dockRect.x - leftDockRect.xMax, bottom - top);
            var inner = new Rect(viewAreaRect.x + 1, viewAreaRect.y + 26, viewAreaRect.width - 2, viewAreaRect.height - 27);
            switch (viewMode)
            {
                case ViewMode.Canvas: canvasRect = inner; surfaceRect = new Rect(inner.xMax, inner.y, 0, inner.height); break;
                case ViewMode.Model: surfaceRect = inner; canvasRect = new Rect(inner.x, inner.y, 0, inner.height); break;
                default:
                    float half = (inner.width - 2) * .5f;
                    canvasRect = new Rect(inner.x, inner.y, half, inner.height);
                    surfaceRect = new Rect(canvasRect.xMax + 2, inner.y, inner.width - half - 2, inner.height);
                    break;
            }
        }

        /// <summary>外枠を描く（キャンバスと 3D の中身は呼ぶ側が先に描く）。</summary>
        /// <summary>テスト用: 外枠を描き始めたときのマウスの位置（3D の描画の後でも、OnGUI の始めの位置であること）。</summary>
        internal Vector2 shellMouseForTests;
        void DrawShell()
        {
            shellMouseForTests = Event.current.mousePosition;
            using (new EditorGUI.DisabledScope(stroke != null))
            {
                DrawMenuBar();
                DrawOptionsBar();
                DrawToolStrip();
                DrawDocks();
            }
            DrawViewHeader();
            DrawStatusBar();
        }

        // ───────── オプションバー（今のツールの設定） ─────────

        void DrawOptionsBar()
        {
            var r = optionsRect;
            PaintGui.Fill(r, PaintTheme.PanelBg);
            PaintGui.HLine(r.x, r.xMax, r.yMax - 1, PaintTheme.Border);
            var slot = CurrentToolSlot();
            float x = r.x + 8, y = r.y + 6, h = r.height - 12;
            PaintGui.ToolIcon(new Rect(x, r.y, 22, r.height), slot.Id, false, PaintTheme.Text, 20); x += 30;
            PaintGui.VLine(x - 4, r.y + 6, r.yMax - 6, PaintTheme.Separator);
            Rect Next(float width) { var at = new Rect(x + 4, y, width, h); x += width + 8; return at; }
            Rect Fit(string text) => Next(PaintTheme.LabelDim.CalcSize(new GUIContent(text)).x + 6); // 説明文は文字の幅に合わせる
            switch (tool)
            {
                case PaintTool.Brush:
                {
                    PaintGui.Dropdown(Next(150), null, string.IsNullOrEmpty(brush.presetName) || brush.presetName == "Custom" ? L.Tr("Custom") : brush.presetName, OpenPresetMenu, L.Tr("Brush preset"));
                    float size = PaintGui.Slider(Next(150), L.Tr("Size"), brush.radius * 2, 1, 256, "0", " px", L.Tr("Brush diameter ([ and ])")); brush.radius = Mathf.Max(.5f, size / 2);
                    brush.hardness = PaintGui.Slider(Next(130), L.Tr("Hardness"), brush.hardness * 100, 0, 100, "0", "%") / 100;
                    brush.opacity = PaintGui.Slider(Next(130), L.Tr("Opacity"), brush.opacity * 100, 0, 100, "0", "%") / 100;
                    brush.flow = PaintGui.Slider(Next(120), L.Tr("Flow"), brush.flow * 100, 0, 100, "0", "%") / 100;
                    if (PaintGui.IconButton(Next(28), "stylus", L.Tr("Pressure controls size"), brush.pressureSize)) brush.pressureSize = !brush.pressureSize;
                    if (PaintGui.IconButton(Next(28), "opacity", L.Tr("Pressure controls opacity"), brush.pressureOpacity)) brush.pressureOpacity = !brush.pressureOpacity;
                    if (EditingMask) { var t = L.Tr("Painting the layer mask (paint hides, erase reveals)"); PaintGui.Text(Fit(t), t, PaintTheme.LabelDim, PaintTheme.Warning); }
                    break;
                }
                case PaintTool.Fill:
                case PaintTool.MagicWand:
                    wandTolerance = PaintGui.IntSlider(Next(160), L.Tr("Tolerance"), wandTolerance, 0, 255);
                    wandContiguous = PaintGui.Toggle(Next(110), L.Tr("Contiguous"), wandContiguous);
                    wandSampleAll = PaintGui.Toggle(Next(150), L.Tr("Sample All Layers"), wandSampleAll, L.Tr("Use the composite instead of the selected layer"));
                    if (tool == PaintTool.Fill) brush.opacity = PaintGui.Slider(Next(130), L.Tr("Opacity"), brush.opacity * 100, 0, 100, "0", "%") / 100;
                    else SelectionModeHint(Fit(L.Tr("Shift adds · Ctrl subtracts · Shift+Ctrl intersects")));
                    break;
                case PaintTool.Gradient:
                    PaintGui.EnumDropdown(Next(170), L.Tr("Shape"), gradientShape, (GradientShape[])Enum.GetValues(typeof(GradientShape)), s => L.Tr(s.ToString()), s => gradientShape = s);
                    PaintGui.Text(Next(36), L.Tr("From"), PaintTheme.Label);
                    PaintGui.ColorSwatch(Next(36), brush.color, c => brush.color = c, true, L.Tr("Start color (the brush color)"));
                    PaintGui.Text(Next(22), L.Tr("To"), PaintTheme.Label);
                    PaintGui.ColorSwatch(Next(36), gradientTo, c => gradientTo = c, true, L.Tr("End color"));
                    brush.opacity = PaintGui.Slider(Next(130), L.Tr("Opacity"), brush.opacity * 100, 0, 100, "0", "%") / 100;
                    break;
                case PaintTool.SelectRectangle:
                case PaintTool.SelectEllipse:
                case PaintTool.Lasso:
                    SelectionModeHint(Fit(L.Tr("Shift adds · Ctrl subtracts · Shift+Ctrl intersects")));
                    if (PaintGui.IconButton(Next(28), "select_all", L.Tr("Select All (Ctrl+A)"))) document.SetSelection(SelectionMask.All(document));
                    if (PaintGui.IconButton(Next(28), "deselect", L.Tr("Deselect (Ctrl+D)"), false, document.Selection != null)) document.ClearSelection();
                    if (PaintGui.IconButton(Next(28), "invert_colors", L.Tr("Inverse (Ctrl+Shift+I)"), false, document.Selection != null)) document.SetSelection(document.Selection.Invert());
                    break;
                case PaintTool.Move:
                    { var t = L.Tr("Drag to move, corners to scale, outside to rotate. Arrow keys nudge."); PaintGui.Text(Fit(t), t, PaintTheme.LabelDim); }
                    if (PaintGui.IconButton(Next(28), "flip", L.Tr("Flip Horizontal"))) TryAction(() => TransformSelected(0, 0, 0, -1, 1, L.Tr("Flipped horizontally.")));
                    if (PaintGui.IconButton(Next(28), "rotate_90_degrees_cw", L.Tr("Rotate 90° Clockwise"))) TryAction(() => TransformSelected(0, 0, -90, 1, 1, L.Tr("Rotated 90° clockwise.")));
                    PaintGui.EnumDropdown(Next(200), L.Tr("Resampling"), moveResampling, (Resampling[])Enum.GetValues(typeof(Resampling)), s => L.Tr(s.ToString()), s => moveResampling = s);
                    break;
                case PaintTool.Path:
                    { var t = L.Tr("Click the 2D canvas or the model to add points; drag a point to move it; Delete removes the last one."); PaintGui.Text(Fit(t), t, PaintTheme.LabelDim); }
                    break;
                case PaintTool.Eyedropper:
                    wandSampleAll = PaintGui.Toggle(Next(150), L.Tr("Sample All Layers"), wandSampleAll, L.Tr("Pick from the composite instead of the selected layer"));
                    { var t = L.Tr("Click the 2D canvas to pick the brush color."); PaintGui.Text(Fit(t), t, PaintTheme.LabelDim); }
                    break;
            }
        }

        void SelectionModeHint(Rect r) => PaintGui.Text(r, L.Tr("Shift adds · Ctrl subtracts · Shift+Ctrl intersects"), PaintTheme.LabelDim);

        void OpenPresetMenu(Rect at)
        {
            var menu = new GenericMenu();
            foreach (var preset in BuiltInBrushes.Presets) { var p = preset; menu.AddItem(new GUIContent(p.Category + "/" + p.Name), brush.presetId == p.Id, () => ApplyPreset(p)); }
            if (PainterSettings.ShowBundledBrushes)
                foreach (var preset in BundledBrushSets.Presets) { var p = preset; menu.AddItem(new GUIContent(p.Category + "/" + p.Name), brush.presetId == p.Id, () => ApplyPreset(p)); }
            foreach (var library in BrushLibrary.All)
                foreach (var preset in library.Presets) { var p = preset; menu.AddItem(new GUIContent(library.MenuName + "/" + (string.IsNullOrEmpty(p.Category) ? "" : p.Category + "/") + p.Name), brush.presetId == p.Id, () => ApplyPreset(p)); }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(L.Tr("Import Brushes…")), false, () => TryAction(ImportBrushes));
            menu.DropDown(at);
        }

        // ───────── ツールの帯 ─────────

        ToolSlot CurrentToolSlot() => ToolSlots.First(s => s != null && s.Tool == tool && (tool != PaintTool.Brush || s.Erase == brush.erase));

        internal void SelectTool(PaintTool next, bool erase = false)
        {
            Tool = next;
            if (next == PaintTool.Brush) brush.erase = erase;
            Repaint();
        }

        void DrawToolStrip()
        {
            var r = toolStripRect;
            PaintGui.Fill(r, PaintTheme.PanelBg);
            PaintGui.VLine(r.xMax - 1, r.y, r.yMax, PaintTheme.Border);
            float y = r.y + 6; var current = CurrentToolSlot();
            foreach (var slot in ToolSlots)
            {
                if (slot == null) { PaintGui.StripSeparator(new Rect(r.x, y, r.width, 8)); y += 8; continue; }
                var at = new Rect(r.x + 5, y, r.width - 10, 32);
                var s = slot;
                if (PaintGui.ToolButton(at, slot.Id, L.Tr(slot.Name) + " (" + slot.Key + ")", slot == current, _ => ToolIconMenu(s))) SelectTool(slot.Tool, slot.Erase);
                y += 34;
            }
            // 描画色と背景色（Photoshop の配置: 描画色が左上、背景色が右下に重なる。右上に入れ替え、左下に初期設定）
            float bottom = r.yMax - 10, left = r.x + 6;
            var front = new Rect(left, bottom - 42, 22, 22);
            var back = new Rect(left + 11, bottom - 31, 22, 22);
            PaintGui.ColorSwatch(back, brush.secondaryColor, c => brush.secondaryColor = c, true, L.Tr("Background color"));
            PaintGui.Fill(new Rect(front.x - 1, front.y - 1, front.width + 2, front.height + 2), PaintTheme.PanelBg);
            PaintGui.ColorSwatch(front, brush.color, c => brush.color = c, true, L.Tr("Foreground color (the brush value)"));
            if (PaintGui.IconButton(new Rect(left + 21, bottom - 56, 14, 14), "swap_horiz", L.Tr("Swap colors (X)"), false, true, 12)) SwapColors();
            if (PaintGui.IconButton(new Rect(left - 2, bottom - 8, 14, 14), "restart_alt", L.Tr("Default colors (D)"), false, true, 11)) DefaultColors();
        }

        /// <summary>ツールのボタンの右クリック: アイコンを描き手の画像に差し替える（CLIP STUDIO のサブツールのアイコンのように）。</summary>
        void ToolIconMenu(ToolSlot slot)
        {
            var menu = new GenericMenu(); string id = slot.Id;
            menu.AddItem(new GUIContent(L.Tr("Change Icon…")), false, () => PickToolIcon(id, false));
            menu.AddItem(new GUIContent(L.Tr("Change Selected Icon…")), false, () => PickToolIcon(id, true));
            if (PainterToolIcons.HasUserIcon(id)) menu.AddItem(new GUIContent(L.Tr("Reset Icon")), false, () => TryAction(() => { PainterToolIcons.ResetUserIcon(id); message = L.Tr("The tool icon is back to the bundled one."); }));
            else menu.AddDisabledItem(new GUIContent(L.Tr("Reset Icon")));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(L.Tr("Open Icon Folder")), false, () => { System.IO.Directory.CreateDirectory(PainterToolIcons.UserFolder); EditorUtility.RevealInFinder(PainterToolIcons.UserFolder); });
            menu.ShowAsContext();
        }
        void PickToolIcon(string id, bool selected)
        {
            string path = Dialogs.OpenFile(L.Tr("Choose a tool icon (PNG, up to 256 px)"), PainterToolIcons.UserFolder, "png");
            if (string.IsNullOrEmpty(path)) return;
            TryAction(() => { PainterToolIcons.SetUserIcon(id, path, selected); message = L.Tr("The tool icon was changed. Right-click the tool to reset it."); });
        }

        void SwapColors() { var c = brush.color; brush.color = brush.secondaryColor; brush.secondaryColor = c; Repaint(); }
        void DefaultColors() { brush.color = Color.black; brush.secondaryColor = Color.white; Repaint(); }

        // ───────── 表示域の見出し ─────────

        void DrawViewHeader()
        {
            var bar = new Rect(viewAreaRect.x, viewAreaRect.y, viewAreaRect.width, 26);
            PaintGui.Fill(bar, PaintTheme.PanelHeader);
            PaintGui.HLine(bar.x, bar.xMax, bar.yMax - 1, PaintTheme.Border);
            float x = bar.x + 6;
            foreach (var (mode, icon, tip) in new[] { (ViewMode.Canvas, "square", "2D Canvas (F1)"), (ViewMode.Model, "view_in_ar", "3D View (F2)"), (ViewMode.Split, "splitscreen_right", "2D + 3D (F3)") })
            {
                if (PaintGui.IconButton(new Rect(x, bar.y + 2, 26, 22), icon, L.Tr(tip), viewMode == mode, true, 17)) View = mode;
                x += 28;
            }
            string channelName = L.Tr(channel.ToString());
            string what = EditingMask ? L.Tr("Layer mask") : channelName;
            if (textureSets.Count > 1) what = currentSet.Name + " · " + what;
            if (canvasRect.width > 0) PaintGui.Text(new Rect(Mathf.Max(x + 8, canvasRect.x + 8), bar.y, 300, bar.height), PaintGui.Fit("2D · " + what + "  " + Mathf.RoundToInt(canvasZoom * 100) + "%", 300, PaintTheme.LabelDim, false), PaintTheme.LabelDim, EditingMask ? PaintTheme.Warning : PaintTheme.TextDim);
            if (surfaceRect.width > 0)
            {
                float right = DrawShadingSwitch(bar);
                string label = "3D · " + (preview.HasModel ? (model != null ? model.name : L.Tr("Demo cube")) : L.Tr("No model"));
                float width = Mathf.Max(0, Mathf.Min(300, right - surfaceRect.x - 12));
                PaintGui.Text(new Rect(surfaceRect.x + 8, bar.y, width, bar.height), PaintGui.Fit(label, width, PaintTheme.LabelDim, false), PaintTheme.LabelDim);
            }
            if (surfaceRect.width > 0 && !preview.HasModel)
                PaintGui.Text(surfaceRect, L.Tr("Choose a model in Texture Set, or 3D ▸ Demo Cube.\nThe original prefab is never instantiated."), new GUIStyle(PaintTheme.LabelDim) { alignment = TextAnchor.MiddleCenter, wordWrap = true });
        }

        // ───────── ステータスバー ─────────

        void DrawStatusBar()
        {
            var r = statusRect;
            PaintGui.Fill(r, PaintTheme.MenuBg);
            PaintGui.HLine(r.x, r.xMax, r.y, PaintTheme.Border);
            bool gpu = compositor.Backend != null && compositor.Backend.Contains("GPU tiled");
            string right = document.Width + " × " + document.Height + "   " + L.Tr("Layers") + " " + (document.AllocatedBytes / 1048576.0).ToString("F1") + " MiB   " + L.Tr("History") + " " + (document.HistoryBytes / 1048576.0).ToString("F1") + " MiB   " + L.Tr(gpu ? "GPU compositing" : compositor.FellBackToCpu ? "CPU compositing (GPU unavailable)" : "CPU compositing");
            float rw = Mathf.Min(r.width * .5f, PaintTheme.LabelSmall.CalcSize(new GUIContent(right)).x + 16);
            var color = externalConflict ? PaintTheme.Warning : PaintTheme.TextDim;
            if (externalConflict) PaintGui.Icon(new Rect(r.x + 4, r.y, 18, r.height), "warning", PaintTheme.Warning, 15);
            PaintGui.Text(new Rect(r.x + (externalConflict ? 24 : 8), r.y, r.width - rw - 16, r.height), message, PaintTheme.LabelDim, color);
            PaintGui.Text(new Rect(r.xMax - rw - 8, r.y, rw, r.height), right, new GUIStyle(PaintTheme.LabelSmall) { alignment = TextAnchor.MiddleRight });
            PaintGui.Tooltip(new Rect(r.x, r.y, r.width - rw, r.height), message);
            PaintGui.Tooltip(new Rect(r.xMax - rw - 8, r.y, rw, r.height), compositor.Backend);
        }

    }
}
