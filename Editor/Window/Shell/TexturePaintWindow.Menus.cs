using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>メニューバー（ファイル・編集・レイヤー・選択範囲・フィルター・3D・表示・ウィンドウ・ヘルプ）。</summary>
    public sealed partial class TexturePaintWindow
    {
        static readonly string[] MenuTitles = { "File", "Edit", "Layer", "Select", "Filter", "3D", "View", "Window", "Help" };

        void DrawMenuBar()
        {
            // プラグインが「Plugins」に入れるコマンドがあれば、その名前のメニューを最後に足す
            var titles = PainterPluginRegistry.HasPluginsMenu ? MenuTitles.Append(PainterPluginRegistry.PluginsMenu).ToArray() : MenuTitles;
            PaintGui.MenuBar(menuRect, titles.Select(L.Tr).ToArray(), (index, at) =>
            {
                var menu = new GenericMenu();
                switch (index)
                {
                    case 0: FileMenu(menu); break;
                    case 1: EditMenu(menu); break;
                    case 2: LayerMenu(menu); break;
                    case 3: SelectMenu(menu); break;
                    case 4: FilterMenuItems(menu); break;
                    case 5: ModelMenu(menu); break;
                    case 6: ViewMenu(menu); break;
                    case 7: WindowMenu(menu); break;
                    case 8: HelpMenu(menu); break;
                }
                AddPluginCommands(menu, index < MenuTitles.Length ? MenuTitles[index] : PainterPluginRegistry.PluginsMenu);
                menu.DropDown(at);
            });
            // 右端: プロジェクトの名前と保存の状態
            string name = projectPath != null ? System.IO.Path.GetFileName(projectPath) : L.Tr("Untitled");
            string state = IsSaved ? "" : " •";
            var title = new Rect(menuRect.xMax - 360, menuRect.y, 352, menuRect.height);
            PaintGui.Text(title, name + state, new GUIStyle(PaintTheme.LabelDim) { alignment = TextAnchor.MiddleRight }, IsSaved ? PaintTheme.TextDim : PaintTheme.Text);
        }

        static string Shortcut(string text, string keys) => text + "    " + keys;
        void Item(GenericMenu menu, string text, Action action, bool enabled = true, bool on = false, string keys = null)
        {
            var content = new GUIContent(keys == null ? L.Tr(text) : Shortcut(L.Tr(text), keys));
            if (enabled) menu.AddItem(content, on, () => { TryAction(action); Repaint(); }); else menu.AddDisabledItem(content, on);
        }

        void FileMenu(GenericMenu m)
        {
            Item(m, "New Project…", NewProjectDialog, keys: "Ctrl+N");
            Item(m, "Open…", OpenProject, keys: "Ctrl+O");
            m.AddSeparator("");
            Item(m, "Save", () => SaveProject(false), keys: "Ctrl+S");
            Item(m, "Save As…", () => SaveProject(true), keys: "Ctrl+Shift+S");
            m.AddSeparator("");
            Item(m, "Import PSD…", ImportPsd);
            m.AddItem(new GUIContent(L.Tr("Export") + "/" + L.Tr("Channel as PNG…")), false, () => TryAction(ExportPng));
            m.AddItem(new GUIContent(L.Tr("Export") + "/" + L.Tr("All Channels as Images…")), false, () => TryAction(ExportImages));
            m.AddItem(new GUIContent(L.Tr("Export") + "/" + L.Tr("Channel as PSD…")), false, () => TryAction(ExportPsd));
            Item(m, "Assign to lilToon Material…", AssignToLilToon);
            m.AddSeparator("");
            Item(m, "Project Configuration…", ProjectConfigurationDialog);
            Item(m, "Project Settings…", OpenSettings);
        }

        void EditMenu(GenericMenu m)
        {
            Item(m, "Undo", () => document.Undo(), document.CanUndo, keys: "Ctrl+Z");
            Item(m, "Redo", () => document.Redo(), document.CanRedo, keys: "Ctrl+Shift+Z / Ctrl+Y");
            m.AddSeparator("");
            Item(m, "Free Transform", () => Tool = PaintTool.Move, keys: "V");
            Item(m, "Flip Horizontal", () => TransformSelected(0, 0, 0, -1, 1, L.Tr("Flipped horizontally.")));
            Item(m, "Flip Vertical", () => TransformSelected(0, 0, 0, 1, -1, L.Tr("Flipped vertically.")));
            Item(m, "Rotate 90° Clockwise", () => TransformSelected(0, 0, -90, 1, 1, L.Tr("Rotated 90° clockwise.")));
            Item(m, "Rotate 90° Counter-clockwise", () => TransformSelected(0, 0, 90, 1, 1, L.Tr("Rotated 90° counter-clockwise.")));
            m.AddSeparator("");
            Item(m, "Preferences…", OpenSettings);
        }

        void LayerMenu(GenericMenu m)
        {
            var active = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            Item(m, "New Layer", AddPaintLayer, keys: "Ctrl+Shift+N");
            Item(m, "New Fill Layer", AddFillLayerHere);
            foreach (var (label, make) in AdjustmentMenu) { var mk = make; var lb = label; m.AddItem(new GUIContent(L.Tr("New Adjustment Layer") + "/" + L.Tr(lb)), false, () => TryAction(() => selectedLayer = document.AddAdjustmentLayer(L.Tr(lb), mk(), above: AboveSelected()).Id)); }
            Item(m, "Group Layers", () => selectedLayer = document.GroupLayers(new[] { selectedLayer }, L.Tr("Group") + " " + (document.Layers.Count(l => l.IsGroup) + 1)).Id, active != null, keys: "Ctrl+G");
            Item(m, "Ungroup", UngroupSelected, active != null && active.IsGroup);
            m.AddSeparator("");
            if (active?.Mask == null) Item(m, "Add Layer Mask", () => { document.AddLayerMask(selectedLayer); editMask = true; }, active != null);
            else
            {
                Item(m, "Edit Layer Mask", () => editMask = !editMask, true, editMask);
                Item(m, "Delete Layer Mask", () => { document.RemoveLayerMask(selectedLayer); editMask = false; });
            }
            Item(m, "Clip to Layer Below", () => document.SetLayerClipping(selectedLayer, !active.Clipping), active != null, active != null && active.Clipping, "Ctrl+Alt+G");
            m.AddSeparator("");
            Item(m, "Rasterize Path", () => document.Rasterize(selectedLayer), active?.Path != null);
            Item(m, "Bake Filters into Pixels", BakeFilters, active != null && (active.Filters.Count > 0 || active.Mask != null && active.Mask.Filters.Count > 0));
            m.AddSeparator("");
            Item(m, "Move Layer Up", () => MoveSelectedLayer(+1), active != null, keys: "Ctrl+]");
            Item(m, "Move Layer Down", () => MoveSelectedLayer(-1), active != null, keys: "Ctrl+[");
            Item(m, "Delete Layer", DeleteSelectedLayer, document.Layers.Count > 1);
        }

        void SelectMenu(GenericMenu m)
        {
            Item(m, "All", () => document.SetSelection(SelectionMask.All(document)), keys: "Ctrl+A");
            Item(m, "Deselect", () => document.ClearSelection(), document.Selection != null, keys: "Ctrl+D");
            Item(m, "Inverse", () => document.SetSelection(document.Selection.Invert()), document.Selection != null, keys: "Ctrl+Shift+I");
            m.AddSeparator("");
            bool any = document.Selection != null;
            Item(m, "Grow", () => ModifySelection(SelectionModifyKind.Grow), any);
            Item(m, "Shrink", () => ModifySelection(SelectionModifyKind.Shrink), any);
            Item(m, "Border", () => ModifySelection(SelectionModifyKind.Border), any);
            Item(m, "Feather", () => ModifySelection(SelectionModifyKind.Feather), any);
            Item(m, "Sharpen Edge", () => ModifySelection(SelectionModifyKind.Sharpen), any);
        }

        void FilterMenuItems(GenericMenu m)
        {
            var active = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            foreach (var target in new[] { FilterTarget.Content, FilterTarget.Mask })
            {
                if (target == FilterTarget.Mask && active?.Mask == null) continue;
                string prefix = target == FilterTarget.Content ? "" : L.Tr("On Mask") + "/";
                foreach (var (label, make) in FilterMenu)
                {
                    var settings = make(); var t = target;
                    string why = active == null ? L.Tr("no layer") : document.FilterRefusal(active.Id, target, settings, channel);
                    var content = new GUIContent(prefix + L.Tr(label));
                    if (why == null) m.AddItem(content, false, () => AddFilter(t, settings)); else m.AddDisabledItem(new GUIContent(prefix + L.Tr(label) + " — " + why));
                }
            }
        }

        void ModelMenu(GenericMenu m)
        {
            Item(m, "Choose Model…", () => EditorGUIUtility.ShowObjectPicker<GameObject>(model, false, "t:Model t:Prefab", ModelPickerId));
            Item(m, "Demo Cube", LoadDemoCube);
            m.AddSeparator("");
            Item(m, "Light with Normal Output", () => PreviewNormals = !previewNormals, true, previewNormals);
        }

        void ViewMenu(GenericMenu m)
        {
            Item(m, "2D Canvas", () => View = ViewMode.Canvas, true, viewMode == ViewMode.Canvas, "F1");
            Item(m, "3D View", () => View = ViewMode.Model, true, viewMode == ViewMode.Model, "F2");
            Item(m, "2D + 3D", () => View = ViewMode.Split, true, viewMode == ViewMode.Split, "F3");
            m.AddSeparator("");
            Item(m, "Zoom In", () => canvasZoom = Mathf.Clamp(canvasZoom * 1.25f, .2f, 16), keys: "Ctrl++");
            Item(m, "Zoom Out", () => canvasZoom = Mathf.Clamp(canvasZoom / 1.25f, .2f, 16), keys: "Ctrl+-");
            Item(m, "Fit on Screen", () => { canvasZoom = 1; canvasPan = Vector2.zero; }, keys: "Ctrl+0");
        }

        void WindowMenu(GenericMenu m)
        {
            // パネルの表示（畳む・開く・タブを見せる）、別のウィンドウにする・戻す、配置を元に戻す
            foreach (var panel in Panels) { var id = panel.Id; Item(m, panel.Title, () => ShowOrHidePanel(id), true, PanelShown(id)); }
            m.AddSeparator("");
            foreach (var panel in Panels) { var id = panel.Id; m.AddItem(new GUIContent(L.Tr("Open in Separate Window") + "/" + L.Tr(panel.Title)), Layout.IsFloating(id), () => { TryAction(() => ToggleFloating(id)); Repaint(); }); }
            Item(m, "Return All Panels to the Dock", DockAllPanels, Layout.Column(DockPlace.Floating).Count > 0);
            Item(m, "Reset Panel Layout", ResetDockLayout);
            m.AddSeparator("");
            foreach (var (language, label) in new[] { (PainterLanguage.Auto, L.Tr("Automatic (Unity's language)")), (PainterLanguage.Japanese, "日本語"), (PainterLanguage.English, "English") })
            { var lang = language; m.AddItem(new GUIContent(L.Tr("Language") + "/" + label), L.Language == lang, () => { L.Language = lang; Repaint(); }); }
        }

        void HelpMenu(GenericMenu m)
        {
            Item(m, "Keyboard Shortcuts", () => Dialogs.Inform(L.Tr("Keyboard Shortcuts"), L.Tr(ShortcutHelp)));
            Item(m, "Implementation Limits", () => Dialogs.Inform(L.Tr("Implementation Limits"), L.Tr(LimitsHelp)));
            Item(m, "Plugins…", ShowPluginList);
        }

        const string ShortcutHelp = "Tools: B brush · E eraser · G fill · Shift+G gradient · M rectangle select · Shift+M ellipse select · L lasso · W magic wand · V move · P path · I eyedropper\n[ / ] brush size · X swap colors · D default colors\nCtrl+Z undo · Ctrl+Shift+Z or Ctrl+Y redo · Ctrl+S save · Ctrl+Shift+S save as · Ctrl+O open · Ctrl+N new\nCtrl+A select all · Ctrl+D deselect · Ctrl+Shift+I inverse\nF1 2D · F2 3D · F3 2D + 3D · Ctrl+0 fit · wheel zoom · middle drag pan\n3D: Alt or right drag orbits, middle drag pans, wheel zooms\nEsc cancels a stroke or a drag\nBy default, Unity's own shortcuts do nothing while this window has focus (Project Settings ▸ YoluPainter ▸ Unity shortcuts while painting)";
        const string LimitsHelp = "Prototype: the CPU paints the source and the GPU composites tiles. Readable static and skinned meshes (posed on a copy). One channel is painted at a time. .ylp saves; PSD with 8-bit RGB layers, groups, masks, fills and three adjustment types. Generators and anchors are not finished, and the 3D preview does not reproduce lilToon exactly. See Documentation~/STATUS.md in the package.";
    }
}
