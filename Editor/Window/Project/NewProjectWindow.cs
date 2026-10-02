using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>新規プロジェクトの始め方（どのチャンネルを使うか）。</summary>
    internal enum ProjectTemplate { Pbr = 0, LilToon = 1, ColorOnly = 2 }

    /// <summary>新規プロジェクト（とプロジェクト設定）で尋ねること。Substance Painter の New Project と同じく、モデルとテクスチャセット
    /// （マテリアルのスロット）、解像度、ノーマルマップの形式を最初に決める。</summary>
    internal sealed class NewProjectSettings
    {
        public static readonly int[] Resolutions = { 512, 1024, 2048, 4096, 8192 };
        public ProjectTemplate Template = ProjectTemplate.Pbr;
        public GameObject Model;
        public int MaterialSlot;
        public int Resolution = 2048;
        public NormalYDirection NormalFormat = NormalYDirection.OpenGL;
        public bool BakeMeshMaps;

        public NewProjectSettings Clone() => (NewProjectSettings)MemberwiseClone();

        /// <summary>テンプレートが最初のレイヤーで使うチャンネル（列挙の順）。</summary>
        public static PaintChannel[] Channels(ProjectTemplate template)
        {
            switch (template)
            {
                case ProjectTemplate.ColorOnly: return new[] { PaintChannel.Color };
                case ProjectTemplate.LilToon: return new[] { PaintChannel.Color, PaintChannel.Normal, PaintChannel.Emission };
                default: return new[] { PaintChannel.Color, PaintChannel.Roughness, PaintChannel.Metallic, PaintChannel.Height, PaintChannel.Normal, PaintChannel.Emission };
            }
        }
        public static string TemplateName(ProjectTemplate template)
        {
            switch (template)
            {
                case ProjectTemplate.LilToon: return "lilToon (Color, Normal, Emission)";
                case ProjectTemplate.ColorOnly: return "Color only";
                default: return "PBR (all channels)";
            }
        }

        public void Validate()
        {
            if (!Resolutions.Contains(Resolution)) throw new ArgumentOutOfRangeException(nameof(Resolution), "Resolution must be one of " + string.Join(", ", Resolutions) + ".");
            if (!Enum.IsDefined(typeof(ProjectTemplate), Template)) throw new ArgumentOutOfRangeException(nameof(Template));
            if (!Enum.IsDefined(typeof(NormalYDirection), NormalFormat)) throw new ArgumentOutOfRangeException(nameof(NormalFormat));
            if (MaterialSlot < 0) throw new ArgumentOutOfRangeException(nameof(MaterialSlot));
        }
    }

    /// <summary>
    /// 新規プロジェクト（と、開いているプロジェクトの設定）のダイアログ。モデルは隔離したプレビュー（ウィンドウと同じ読み込み）で読み、
    /// マテリアルのスロットの名前と読み込みの注意を出す（スロットの番号がウィンドウと必ず一致する）。元のモデルは Instantiate しない。
    /// 決めたら accept を呼んで閉じる。モーダルにはしない（テストと、ほかのウィンドウの操作を止めないため）。
    /// </summary>
    internal sealed class NewProjectWindow : EditorWindow
    {
        internal NewProjectSettings Settings = new NewProjectSettings();
        /// <summary>開いているプロジェクトの設定を変える（解像度は変えられない）。</summary>
        internal bool Configure;
        Action<NewProjectSettings> accept;
        IsolatedModelPreview preview; GameObject loadedFor; bool loaded;
        const int PickerId = 0x59500003;

        internal static NewProjectWindow Open(NewProjectSettings initial, bool configure, Action<NewProjectSettings> onAccept)
        {
            var w = CreateInstance<NewProjectWindow>();
            w.Settings = initial.Clone(); w.Configure = configure; w.accept = onAccept;
            w.titleContent = L.Content(configure ? "Project Configuration" : "New Project");
            w.minSize = w.maxSize = new Vector2(640, 520);
            w.ShowUtility();
            var main = EditorGUIUtility.GetMainWindowPosition();
            w.position = new Rect(main.center.x - 320, main.center.y - 260, 640, 520);
            return w;
        }

        void OnEnable() { wantsMouseMove = true; L.LanguageChanged += Repaint; }
        void OnDisable() { L.LanguageChanged -= Repaint; preview?.Dispose(); preview = null; }

        /// <summary>モデルが変わったら読み直す（スロットの名前と注意のため）。</summary>
        void EnsurePreview()
        {
            if (loaded && loadedFor == Settings.Model) return;
            if (preview == null) preview = new IsolatedModelPreview();
            loadedFor = Settings.Model; loaded = true;
            try { if (Settings.Model != null) preview.Load(Settings.Model); else preview.Load(null); }
            catch (Exception ex) { Debug.LogWarning("YoluPainter: " + ex.Message); }
            Settings.MaterialSlot = Mathf.Clamp(Settings.MaterialSlot, 0, Mathf.Max(0, preview.MaterialSlotCount - 1));
        }

        void OnGUI()
        {
            var e = Event.current;
            if (e.type == EventType.MouseMove) Repaint();
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { Close(); e.Use(); return; }
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && GUIUtility.keyboardControl == 0) { Accept(); e.Use(); return; }
            if (e.type == EventType.ExecuteCommand && EditorGUIUtility.GetObjectPickerControlID() == PickerId && (e.commandName == "ObjectSelectorUpdated" || e.commandName == "ObjectSelectorClosed"))
            { if (EditorGUIUtility.GetObjectPickerObject() is GameObject picked) Settings.Model = picked; e.Use(); }
            DrawContent(new Rect(0, 0, position.width, position.height));
        }

        /// <summary>中身を r に描く（オフスクリーンの描画からも呼ぶ）。</summary>
        internal void DrawContent(Rect r)
        {
            EnsurePreview();
            PaintGui.Fill(r, PaintTheme.PanelBg);
            // 見出し
            var head = new Rect(r.x, r.y, r.width, 44);
            PaintGui.Fill(head, PaintTheme.PanelHeader); PaintGui.HLine(r.x, r.xMax, head.yMax - 1, PaintTheme.Border);
            PaintGui.Icon(new Rect(r.x + 12, head.y, 24, head.height), Configure ? "settings" : "add", PaintTheme.Text, 20);
            PaintGui.Text(new Rect(r.x + 44, head.y, r.width - 60, head.height), L.Tr(Configure ? "Project Configuration" : "New Project"), new GUIStyle(PaintTheme.LabelBold) { fontSize = 14 });
            // 左: 設定、右: モデルの表示
            var body = new Rect(r.x, head.yMax, r.width, r.height - head.height - 52);
            var left = new Rect(body.x, body.y, body.width - 250, body.height);
            var right = new Rect(left.xMax, body.y + 12, 238, body.height - 24);
            var rows = new UiRows(left, 12);
            const float labelWidth = 160;

            if (!Configure)
            {
                PaintGui.Text(rows.Row(18), L.Tr("Template"), PaintTheme.Header);
                PaintGui.EnumDropdown(rows.Row(24), null, Settings.Template, (ProjectTemplate[])Enum.GetValues(typeof(ProjectTemplate)), t => L.Tr(NewProjectSettings.TemplateName(t)), t => Settings.Template = t);
                PaintGui.Text(rows.Row(16, 10), L.Tr("Channels") + ": " + string.Join(" · ", NewProjectSettings.Channels(Settings.Template).Select(c => L.Tr(c.ToString()))), PaintTheme.LabelSmall);
            }

            PaintGui.Text(rows.Row(18), L.Tr("Mesh"), PaintTheme.Header);
            var modelRow = rows.Row(26);
            var box = new Rect(modelRow.x, modelRow.y, modelRow.width - 30, modelRow.height);
            PaintGui.Rounded(box, PaintTheme.ControlBg, 3); PaintGui.Outline(box, PaintTheme.Border, 1, 3);
            PaintGui.Icon(new Rect(box.x + 2, box.y, 22, box.height), "deployed_code", PaintTheme.TextDim, 15);
            string modelName = Settings.Model != null ? Settings.Model.name : L.Tr("None (paint in 2D; choose a model later)");
            PaintGui.Text(new Rect(box.x + 26, box.y, box.width - 30, box.height), modelName, PaintTheme.Label, Settings.Model != null ? PaintTheme.Text : PaintTheme.TextDim);
            HandleDrop(box);
            if (PaintGui.IconButton(new Rect(box.xMax + 4, modelRow.y, 26, modelRow.height), "folder_open", L.Tr("Choose a model…"), false, true, 17))
                EditorGUIUtility.ShowObjectPicker<GameObject>(Settings.Model, false, "t:Model t:Prefab", PickerId);
            string path = Settings.Model != null ? AssetDatabase.GetAssetPath(Settings.Model) : null;
            PaintGui.Text(rows.Row(16), string.IsNullOrEmpty(path) ? (Settings.Model != null ? L.Tr("A scene object (its meshes are copied, the object is not changed)") : "") : path, PaintTheme.LabelSmall);
            int slots = Mathf.Max(1, preview.MaterialSlotCount);
            PaintGui.Dropdown(rows.Row(24), L.Tr("Texture set (material)"), SlotLabel(Settings.MaterialSlot), at =>
            {
                var menu = new GenericMenu();
                for (int i = 0; i < slots; i++) { int s = i; menu.AddItem(new GUIContent(SlotLabel(s)), s == Settings.MaterialSlot, () => Settings.MaterialSlot = s); }
                menu.DropDown(at);
            }, L.Tr("The material slot this project paints"), preview.HasModel, labelWidth);
            rows.Space(8);

            PaintGui.Text(rows.Row(18), L.Tr("Document"), PaintTheme.Header);
            PaintGui.EnumDropdown(rows.Row(24), L.Tr("Resolution"), Settings.Resolution, NewProjectSettings.Resolutions, v => v + " × " + v, v => Settings.Resolution = v, !Configure, labelWidth);
            PaintGui.EnumDropdown(rows.Row(24), L.Tr("Normal map format"), Settings.NormalFormat, (NormalYDirection[])Enum.GetValues(typeof(NormalYDirection)),
                v => v == NormalYDirection.OpenGL ? L.Tr("OpenGL (Y+, Unity)") : L.Tr("DirectX (Y−)"), v => Settings.NormalFormat = v, true, labelWidth);
            PaintGui.Text(rows.Row(16, 10), L.Tr("Unity reads normal maps as OpenGL. The format is used when exporting files."), PaintTheme.LabelSmall);
            if (!Configure)
                Settings.BakeMeshMaps = PaintGui.Toggle(rows.Row(22), L.Tr("Bake mesh maps after creating"), Settings.BakeMeshMaps, L.Tr("Normal, position, AO, curvature and thickness from the model"), preview.CanPaint);

            DrawModelSummary(right);

            // 下: 取消と決定
            var foot = new Rect(r.x, r.yMax - 52, r.width, 52);
            PaintGui.Fill(foot, PaintTheme.PanelHeader); PaintGui.HLine(r.x, r.xMax, foot.y, PaintTheme.Border);
            if (PaintGui.Button(new Rect(foot.xMax - 236, foot.y + 12, 104, 28), L.Tr("Cancel"))) Close();
            if (PaintGui.Button(new Rect(foot.xMax - 124, foot.y + 12, 112, 28), L.Tr(Configure ? "Apply" : "Create"), true)) Accept();
        }

        void DrawModelSummary(Rect r)
        {
            PaintGui.Rounded(r, PaintTheme.CanvasBg, 4); PaintGui.Outline(r, PaintTheme.Border, 1, 4);
            var view = new Rect(r.x + 1, r.y + 1, r.width - 2, r.width - 2);
            if (preview.HasModel && Event.current.type == EventType.Repaint) { try { preview.Render(view); } catch (Exception) { } }
            else if (!preview.HasModel) PaintGui.Text(view, L.Tr("No model"), PaintTheme.LabelCenter, PaintTheme.TextDim);
            var rows = new UiRows(new Rect(r.x, view.yMax, r.width, r.yMax - view.yMax), 6);
            if (preview.HasModel)
            {
                PaintGui.Text(rows.Row(16, 2), L.Tr("Triangles") + ": " + preview.Geometry.TriangleCount.ToString("N0"), PaintTheme.LabelDim);
                PaintGui.Text(rows.Row(16, 2), L.Tr("Texture sets") + ": " + preview.MaterialSlotCount, PaintTheme.LabelDim);
            }
            var notes = preview.Diagnostics;
            if (notes.Count > 0)
            {
                var at = rows.Row(16, 2);
                PaintGui.Icon(new Rect(at.x, at.y, 16, at.height), "warning", PaintTheme.Warning, 14);
                PaintGui.Text(new Rect(at.x + 20, at.y, at.width - 20, at.height), L.Tr("{0} note(s) while loading", notes.Count), PaintTheme.LabelDim, PaintTheme.Warning);
                PaintGui.Tooltip(at, string.Join("\n", notes));
            }
        }

        string SlotLabel(int slot) => preview != null && slot < preview.MaterialSlotNames.Count ? slot + ": " + preview.MaterialSlotNames[slot] : slot.ToString();

        void HandleDrop(Rect box)
        {
            var e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !box.Contains(e.mousePosition)) return;
            var dropped = DragAndDrop.objectReferences.OfType<GameObject>().FirstOrDefault();
            if (dropped == null) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            if (e.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); Settings.Model = dropped; }
            e.Use();
        }

        /// <summary>今の設定を検めて写す（ダイアログの中で後から変えても渡したものは変わらない）。</summary>
        internal NewProjectSettings TakeSettings() { Settings.Validate(); return Settings.Clone(); }

        internal void Accept()
        {
            var settings = TakeSettings();
            Close();
            accept?.Invoke(settings);
        }
    }
}
