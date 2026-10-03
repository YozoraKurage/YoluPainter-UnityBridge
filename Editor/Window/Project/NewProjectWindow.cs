using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>新規プロジェクトの始め方（どのチャンネルを使うか）。</summary>
    internal enum ProjectTemplate { Pbr = 0, LilToon = 1, ColorOnly = 2 }

    /// <summary>プロジェクト設定で編む、テクスチャセット 1 つ（Id が空なら足すセット）。</summary>
    internal sealed class TextureSetDraft
    {
        public Guid Id;
        public string Name;
        public int Slot;
        /// <summary>セットの大きさ（画素）。今と違えば、適用でセットの画素を新しい大きさに再標本化する（<see cref="NewProjectSettings.Resampling"/>）。
        /// 0 × 0 は「変えない」（足すセットなら開いているセットと同じ大きさ）。</summary>
        public int Width, Height;
        /// <summary>設定を開いたときのセットの大きさ（足すセットは 0）。正方形の選択肢に無い大きさ（取り込んだ PSD など）も、そのままなら通す。</summary>
        public int CurrentWidth, CurrentHeight;
        public TextureSetDraft Clone() => (TextureSetDraft)MemberwiseClone();
        /// <summary>大きさを変えるか（今の大きさと違う、0 でない大きさ）。</summary>
        public bool Resizes => Id != Guid.Empty && (Width != 0 || Height != 0) && (Width != CurrentWidth || Height != CurrentHeight);
    }

    /// <summary>新規プロジェクト（とプロジェクト設定）で尋ねること。Substance Painter の New Project と同じく、モデルとテクスチャセット
    /// （マテリアルのスロット。それぞれが同じテンプレートと解像度のセットになる）、解像度、ノーマルマップの形式を最初に決める。</summary>
    internal sealed class NewProjectSettings
    {
        public static readonly int[] Resolutions = { 512, 1024, 2048, 4096, 8192 };
        public ProjectTemplate Template = ProjectTemplate.Pbr;
        public GameObject Model;
        internal PreparedModel PreparedModel;
        /// <summary>新規: テクスチャセットにするマテリアルのスロット（null ならモデルの全部のスロット。モデルが無ければスロット 0 の 1 つ）。
        /// モデルに無いスロットは最後のスロットに寄せる。</summary>
        public int[] Slots;
        /// <summary>プロジェクト設定: テクスチャセットの並び（名前・スロット。消したセットは並びに無い）。null なら変えない。</summary>
        public List<TextureSetDraft> Sets;
        public int Resolution = 2048;
        public NormalYDirection NormalFormat = NormalYDirection.OpenGL;
        public bool BakeMeshMaps;
        /// <summary>プロジェクト設定: セットの大きさを変えるときの再標本化。null は自動（縮めるなら面積平均、広げるならバイリニア）。</summary>
        public CanvasResampling? Resampling;

        public NewProjectSettings Clone()
        {
            var copy = (NewProjectSettings)MemberwiseClone();
            copy.Slots = Slots?.ToArray(); copy.Sets = Sets?.Select(s => s.Clone()).ToList();
            return copy;
        }

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
            if (Resampling.HasValue && !Enum.IsDefined(typeof(CanvasResampling), Resampling.Value)) throw new ArgumentOutOfRangeException(nameof(Resampling));
            if (Slots != null)
            {
                if (Slots.Length == 0) throw new ArgumentException(L.Tr("Choose at least one texture set."), nameof(Slots));
                if (Slots.Any(s => s < 0 || s > YlpFormat.MaxMaterialSlot)) throw new ArgumentOutOfRangeException(nameof(Slots));
            }
            if (Sets != null)
            {
                if (Sets.Count == 0) throw new ArgumentException(L.Tr("A project keeps at least one texture set."), nameof(Sets));
                if (Sets.Count > YlpFormat.MaxTextureSets) throw new ArgumentException(L.Tr("A project has at most {0} texture sets.", YlpFormat.MaxTextureSets), nameof(Sets));
                foreach (var set in Sets)
                {
                    try { YlpFormat.CheckSetName(set.Name); }
                    catch (ArgumentException) { throw new ArgumentException(L.Tr("Every texture set needs a name (at most {0} characters, no control characters).", YlpFormat.MaxTextureSetNameLength), nameof(Sets)); }
                    if (set.Slot < 0 || set.Slot > YlpFormat.MaxMaterialSlot) throw new ArgumentOutOfRangeException(nameof(Sets));
                    bool keep = set.Width == 0 && set.Height == 0 || set.Width == set.CurrentWidth && set.Height == set.CurrentHeight;
                    if (!keep && !(set.Width == set.Height && Resolutions.Contains(set.Width)))
                        throw new ArgumentException(L.Tr("A texture set's size must be one of {0}.", string.Join(", ", Resolutions.Select(r => r + " × " + r))), nameof(Sets));
                }
                var name = Sets.GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (name != null) throw new ArgumentException(L.Tr("Two texture sets are named {0}.", name.Key), nameof(Sets));
                var slot = Sets.GroupBy(s => s.Slot).FirstOrDefault(g => g.Count() > 1);
                if (slot != null) throw new ArgumentException(L.Tr("Two texture sets paint material slot {0}.", slot.Key), nameof(Sets));
                if (Sets.Where(s => s.Id != Guid.Empty).Select(s => s.Id).Distinct().Count() != Sets.Count(s => s.Id != Guid.Empty)) throw new ArgumentException("A texture set is listed twice.", nameof(Sets));
            }
        }
    }

    /// <summary>
    /// 新規プロジェクト（と、開いているプロジェクトの設定）のダイアログ。モデルは隔離したプレビュー（ウィンドウと同じ読み込み）で読み、
    /// マテリアルのスロットの名前と読み込みの注意を出す（スロットの番号がウィンドウと必ず一致する）。元のモデルは Instantiate しない。
    /// 新規ではスロットの一覧をチェックで選び（既定は全部）、それぞれがテクスチャセットになる。設定ではテクスチャセットの並びを編む（名前・
    /// スロット・足す・消す。消すのは決めたときに持ち主が確かめる）。決めたら accept を呼んで閉じる。モーダルにはしない（テストと、ほかの
    /// ウィンドウの操作を止めないため）。
    /// </summary>
    internal sealed class NewProjectWindow : EditorWindow, IPainterShortcutScope
    {
        internal NewProjectSettings Settings = new NewProjectSettings();
        /// <summary>開いているプロジェクトの設定を変える（大きさはテクスチャセットごとに並びの欄で変える）。</summary>
        internal bool Configure;
        Action<NewProjectSettings> accept;
        IsolatedModelPreview preview; GameObject loadedFor; bool loaded;
        Vector2 setScroll; string error;
        const int PickerId = 0x59500003;
        internal const float Width = 640, Height = 600;
        const float SetRow = 24; const int SetRowsShown = 6;

        internal static NewProjectWindow Open(NewProjectSettings initial, bool configure, Action<NewProjectSettings> onAccept)
        {
            var w = CreateInstance<NewProjectWindow>();
            w.Settings = initial.Clone(); w.Configure = configure; w.accept = onAccept;
            w.titleContent = L.Content(configure ? "Project Configuration" : "New Project");
            w.minSize = w.maxSize = new Vector2(Width, Height);
            w.ShowUtility();
            var main = EditorGUIUtility.GetMainWindowPosition();
            w.position = new Rect(main.center.x - Width / 2, main.center.y - Height / 2, Width, Height);
            return w;
        }

        void OnEnable() { wantsMouseMove = true; L.LanguageChanged += Repaint; EditorApplication.update += TickPreparation; }
        void TickPreparation() { if (preview != null && (preview.IsPreparing || preview.PreparationCanceled)) Repaint(); }
        void OnDisable() { EditorApplication.update -= TickPreparation; L.LanguageChanged -= Repaint; preview?.Dispose(); preview = null; }

        /// <summary>モデルが変わったら読み直す（スロットの名前と注意のため）。新規では選んだスロットを全部に戻す。</summary>
        void EnsurePreview()
        {
            if (loaded && loadedFor == Settings.Model) return;
            if (preview == null) preview = new IsolatedModelPreview();
            if (loaded && !Configure) Settings.Slots = null;
            loadedFor = Settings.Model; loaded = true;
            try { if (Settings.Model != null) preview.BeginLoad(Settings.Model); else preview.Load(null); }
            catch (Exception ex) { Debug.LogWarning("YoluPainter: " + ex.Message); }
        }

        bool editingText;
        bool IPainterShortcutScope.EditingText => editingText;
        bool IPainterShortcutScope.TookKey(KeyCode key, EventModifiers modifiers) => false;

        void OnGUI()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown) editingText = GUIUtility.keyboardControl != 0;
            if (e.type == EventType.MouseMove) Repaint();
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && GUIUtility.keyboardControl == 0) { Close(); e.Use(); return; }
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
            rows.Space(4);
            if (Configure) DrawSetDrafts(rows); else DrawSlotChoice(rows);
            rows.Space(8);

            PaintGui.Text(rows.Row(18), L.Tr("Document"), PaintTheme.Header);
            if (Configure)
            {
                // 大きさはセットごと（上の並びの欄）。ここでは変えるときの再標本化を選ぶ
                bool resizing = Settings.Sets != null && Settings.Sets.Any(d => d.Resizes);
                PaintGui.EnumDropdown(rows.Row(24), L.Tr("Resampling"), Settings.Resampling, ResamplingChoices, ResamplingName, v => Settings.Resampling = v, resizing, labelWidth);
                PaintGui.Paragraph(rows, resizing ? L.Tr("Resized texture sets are resampled when you apply; their undo history is cleared.") : L.Tr("Choose a texture set's size in its row above."), PaintTheme.TextDim);
            }
            else PaintGui.EnumDropdown(rows.Row(24), L.Tr("Resolution"), Settings.Resolution, NewProjectSettings.Resolutions, v => v + " × " + v, v => Settings.Resolution = v, true, labelWidth);
            PaintGui.EnumDropdown(rows.Row(24), L.Tr("Normal map format"), Settings.NormalFormat, (NormalYDirection[])Enum.GetValues(typeof(NormalYDirection)),
                v => v == NormalYDirection.OpenGL ? L.Tr("OpenGL (Y+, Unity)") : L.Tr("DirectX (Y−)"), v => Settings.NormalFormat = v, true, labelWidth);
            PaintGui.Text(rows.Row(16, 10), L.Tr("Unity reads normal maps as OpenGL. The format is used when exporting files."), PaintTheme.LabelSmall);
            if (!Configure)
                Settings.BakeMeshMaps = PaintGui.Toggle(rows.Row(22), L.Tr("Bake mesh maps after creating"), Settings.BakeMeshMaps, L.Tr("Normal, position, AO, curvature and thickness from the model"), preview.CanPaint);

            DrawModelSummary(right);

            // 下: 取消と決定（決められない理由があれば左に）
            var foot = new Rect(r.x, r.yMax - 52, r.width, 52);
            PaintGui.Fill(foot, PaintTheme.PanelHeader); PaintGui.HLine(r.x, r.xMax, foot.y, PaintTheme.Border);
            if (!string.IsNullOrEmpty(error))
            {
                var at = new Rect(foot.x + 12, foot.y, foot.width - 260, foot.height);
                PaintGui.Icon(new Rect(at.x, at.y, 18, at.height), "warning", PaintTheme.Warning, 15);
                PaintGui.Text(new Rect(at.x + 22, at.y, at.width - 22, at.height), PaintGui.Fit(error, at.width - 22, PaintTheme.Label, false), PaintTheme.Label, PaintTheme.Warning);
                PaintGui.Tooltip(at, error);
            }
            if (PaintGui.Button(new Rect(foot.xMax - 236, foot.y + 12, 104, 28), L.Tr("Cancel"))) Close();
            if (PaintGui.Button(new Rect(foot.xMax - 124, foot.y + 12, 112, 28), L.Tr(Configure ? "Apply" : "Create"), true)) Accept();
        }

        /// <summary>新規: テクスチャセットにするスロットのチェックの一覧（既定は全部。最後の 1 つは外せない）。モデルが無ければ 1 つ。</summary>
        void DrawSlotChoice(UiRows rows)
        {
            PaintGui.Text(rows.Row(18), L.Tr("Texture Sets"), PaintTheme.Header);
            int slots = preview.HasSnapshot ? Mathf.Max(1, preview.MaterialSlotCount) : 0;
            if (slots == 0) { PaintGui.Text(rows.Row(18, 2), L.Tr("One texture set (no model yet)."), PaintTheme.LabelDim); return; }
            int chosen = Settings.Slots == null ? slots : Settings.Slots.Count(s => s < slots);
            var list = rows.Row(Mathf.Min(slots, SetRowsShown) * SetRow);
            ScrollList(list, slots, i =>
            {
                var row = new Rect(0, i * SetRow, list.width - (slots > SetRowsShown ? 8 : 0), SetRow - 2);
                bool on = Settings.Slots == null || Settings.Slots.Contains(i);
                bool next = PaintGui.Toggle(row, SlotLabel(i), on, on && chosen == 1 ? L.Tr("At least one texture set stays checked.") : L.Tr("Paint this material slot as a texture set"), !(on && chosen == 1));
                if (next != on)
                {
                    var set = new HashSet<int>(Settings.Slots ?? Enumerable.Range(0, slots));
                    if (next) set.Add(i); else set.Remove(i);
                    Settings.Slots = set.Count == slots ? null : set.OrderBy(s => s).ToArray();
                }
            });
            var summary = rows.Row(16, 2);
            PaintGui.Text(summary, L.Tr("{0} of {1} material slots become texture sets.", chosen, slots), PaintTheme.LabelSmall);
            PaintGui.Tooltip(summary, L.Tr("Each texture set gets the template and resolution below."));
        }

        /// <summary>設定: テクスチャセットの並び（名前・スロット・消す）と「足す」。</summary>
        void DrawSetDrafts(UiRows rows)
        {
            var sets = Settings.Sets ?? new List<TextureSetDraft>(); // 描くだけでは設定を変えない（並びが無ければ「変えない」のまま）
            PaintGui.Text(rows.Row(18), L.Tr("Texture Sets"), PaintTheme.Header);
            var list = rows.Row(Mathf.Max(1, Mathf.Min(sets.Count, SetRowsShown - 1)) * SetRow);
            TextureSetDraft remove = null;
            ScrollList(list, sets.Count, i =>
            {
                var d = sets[i];
                var row = new Rect(0, i * SetRow, list.width - (sets.Count > SetRowsShown - 1 ? 8 : 0), SetRow - 2);
                var removeRect = new Rect(row.xMax - 22, row.y, 22, row.height);
                var slotRect = new Rect(removeRect.x - 4 - 96, row.y, 96, row.height);
                var sizeRect = new Rect(slotRect.x - 4 - 96, row.y, 96, row.height);
                var nameRect = new Rect(row.x, row.y, sizeRect.x - 4 - row.x, row.height);
                string name = PaintGui.TextField(nameRect, d.Name, d.Id == Guid.Empty ? L.Tr("A new, empty texture set") : L.Tr("Texture set name"));
                if (name != d.Name) { d.Name = name; error = null; }
                PaintGui.FitDropdown(slotRect, null, ShortSlotLabel(d.Slot), at =>
                {
                    var menu = new GenericMenu();
                    for (int s = 0; s < SlotChoices(); s++)
                    {
                        int slot = s; bool taken = sets.Any(o => o != d && o.Slot == slot);
                        if (taken) menu.AddDisabledItem(new GUIContent(SlotLabel(slot)), slot == d.Slot);
                        else menu.AddItem(new GUIContent(SlotLabel(slot)), slot == d.Slot, () => { d.Slot = slot; error = null; });
                    }
                    menu.DropDown(at);
                }, L.Tr("The material slot this texture set paints") + "\n" + SlotLabel(d.Slot), true, 0, true);
                DrawSizeChoice(sizeRect, d);
                if (PaintGui.IconButton(removeRect, "delete", sets.Count > 1 ? L.Tr("Remove this texture set (asked again when you apply; its work is lost)") : L.Tr("A project keeps at least one texture set."), false, sets.Count > 1, 15)) remove = d;
            });
            if (remove != null) { sets.Remove(remove); error = null; }
            var add = rows.Row(24);
            int free = Enumerable.Range(0, SlotChoices()).FirstOrDefault(s => sets.All(o => o.Slot != s));
            bool canAdd = sets.Count < YlpFormat.MaxTextureSets && sets.All(o => o.Slot != free);
            if (PaintGui.Button(new Rect(add.x, add.y, Mathf.Min(220, add.width), add.height), L.Tr("Add Texture Set"), false, canAdd, L.Tr("An empty texture set for an unused material slot (same size and channels as the open one)"), "add"))
            {
                string baseName = preview.HasSnapshot && preview.SourceMaterial(free) != null ? preview.SourceMaterial(free).name : L.Tr("Texture Set") + " " + (free + 1);
                string unique = baseName; for (int n = 2; sets.Any(o => string.Equals(o.Name, unique, StringComparison.OrdinalIgnoreCase)); n++) unique = baseName + " " + n;
                sets.Add(new TextureSetDraft { Id = Guid.Empty, Name = unique, Slot = free, Width = Settings.Resolution, Height = Settings.Resolution }); Settings.Sets = sets; error = null;
            }
        }

        static readonly CanvasResampling?[] ResamplingChoices = { null, CanvasResampling.Bilinear, CanvasResampling.Area, CanvasResampling.Nearest };
        internal static string ResamplingName(CanvasResampling? resampling)
        {
            switch (resampling)
            {
                case CanvasResampling.Bilinear: return L.Tr("Bilinear");
                case CanvasResampling.Area: return L.Tr("Area average");
                case CanvasResampling.Nearest: return L.Tr("Nearest");
                default: return L.Tr("Automatic");
            }
        }
        /// <summary>大きさの表示（正方形は一辺、ほかは 幅×高さ）。</summary>
        internal static string SizeText(int width, int height) => width == height ? width.ToString() : width + "×" + height;

        /// <summary>セットの大きさのドロップダウン: 新規プロジェクトと同じ正方形の大きさ（と、選択肢に無い今の大きさ）。変えると「→」が付く。</summary>
        void DrawSizeChoice(Rect r, TextureSetDraft d)
        {
            int w = d.Width != 0 || d.Height != 0 ? d.Width : d.CurrentWidth, h = d.Width != 0 || d.Height != 0 ? d.Height : d.CurrentHeight;
            bool known = w > 0 && h > 0;
            string text = !known ? "—" : (d.Resizes ? "→ " : "") + SizeText(w, h);
            string tip = d.Id == Guid.Empty ? L.Tr("Size of the new texture set") : L.Tr("Size of this texture set (now {0} × {1}). A new size resamples its layers when you apply.", d.CurrentWidth, d.CurrentHeight);
            PaintGui.FitDropdown(r, null, text, at =>
            {
                var menu = new GenericMenu();
                bool standard = d.CurrentWidth == d.CurrentHeight && NewProjectSettings.Resolutions.Contains(d.CurrentWidth);
                if (d.Id != Guid.Empty && !standard)
                    menu.AddItem(new GUIContent(d.CurrentWidth + " × " + d.CurrentHeight + " (" + L.TrIn("size", "current") + ")"), w == d.CurrentWidth && h == d.CurrentHeight, () => { d.Width = d.CurrentWidth; d.Height = d.CurrentHeight; error = null; });
                foreach (int size in NewProjectSettings.Resolutions)
                {
                    int chosen = size;
                    string label = size + " × " + size + (d.Id != Guid.Empty && size == d.CurrentWidth && size == d.CurrentHeight ? " (" + L.TrIn("size", "current") + ")" : "");
                    menu.AddItem(new GUIContent(label), w == size && h == size, () => { d.Width = chosen; d.Height = chosen; error = null; });
                }
                menu.DropDown(at);
            }, tip, true, 0, true);
        }

        /// <summary>スロットのドロップダウンに出す数（モデルのスロット。モデルに無いスロットを描くセットがあればそこまで、モデルが無ければ並びより 1 つ多く）。</summary>
        int SlotChoices()
        {
            int maxDraft = Settings.Sets != null && Settings.Sets.Count > 0 ? Settings.Sets.Max(s => s.Slot) + 1 : 1;
            return preview.HasSnapshot ? Mathf.Max(preview.MaterialSlotCount, maxDraft) : Mathf.Max(maxDraft + 1, (Settings.Sets?.Count ?? 0) + 1);
        }

        /// <summary>行の一覧（多ければスクロール）。draw(i) は一覧の中の座標で描く。</summary>
        void ScrollList(Rect viewport, int count, Action<int> draw)
        {
            float content = count * SetRow;
            setScroll.y = Mathf.Clamp(setScroll.y, 0, Mathf.Max(0, content - viewport.height));
            PaintGui.BeginScroll(viewport, setScroll);
            for (int i = 0; i < count; i++) draw(i);
            PaintGui.EndScroll();
            var e = Event.current;
            if (e.type == EventType.ScrollWheel && viewport.Contains(e.mousePosition) && content > viewport.height)
            { setScroll.y = Mathf.Clamp(setScroll.y + e.delta.y * 12, 0, content - viewport.height); e.Use(); Repaint(); }
            if (content > viewport.height + .5f)
                PaintGui.Rounded(new Rect(viewport.xMax - 5, viewport.y + viewport.height * setScroll.y / content, 4, viewport.height * viewport.height / content), PaintTheme.ControlActive, 2);
        }

        void DrawModelSummary(Rect r)
        {
            PaintGui.Rounded(r, PaintTheme.CanvasBg, 4); PaintGui.Outline(r, PaintTheme.Border, 1, 4);
            var view = new Rect(r.x + 1, r.y + 1, r.width - 2, r.width - 2);
            if (preview.HasSnapshot && Event.current.type == EventType.Repaint) { try { preview.Render(view); } catch (Exception) { } }
            else if (!preview.HasSnapshot) PaintGui.Text(view, L.Tr("No model"), PaintTheme.LabelCenter, PaintTheme.TextDim);
            var rows = new UiRows(new Rect(r.x, view.yMax, r.width, r.yMax - view.yMax), 6);
            DrawPreparation(rows);
            if (preview.Geometry != null)
            {
                PaintGui.Text(rows.Row(16, 2), L.Tr("Triangles") + ": " + preview.Geometry.TriangleCount.ToString("N0"), PaintTheme.LabelDim);
                PaintGui.Text(rows.Row(16, 2), L.Tr("Material slots") + ": " + preview.MaterialSlotCount, PaintTheme.LabelDim);
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

        void DrawPreparation(UiRows rows)
        {
            if (preview == null) return;
            if (preview.IsPreparing)
            {
                PaintGui.Text(rows.Row(18), L.Tr("Preparing model: {0}%", Mathf.RoundToInt(preview.PreparationProgress * 100)), PaintTheme.LabelDim);
                if (PaintGui.Button(rows.Row(24), L.Tr("Cancel preparation"))) preview.CancelPreparation();
            }
            else if (preview.PreparationCanceled)
            {
                PaintGui.Text(rows.Row(18), L.Tr("Model preparation canceled. 3D painting is unavailable."), PaintTheme.LabelDim);
                if (PaintGui.Button(rows.Row(24), L.Tr("Prepare again"))) { loaded = false; EnsurePreview(); }
            }
        }

        /// <summary>スロットの番号とマテリアルの名前（狭い欄用。モデルに無ければそう書く）。</summary>
        string ShortSlotLabel(int slot)
        {
            if (preview == null || !preview.HasSnapshot) return slot.ToString();
            if (slot >= preview.MaterialSlotCount) return slot + " (" + L.Tr("not in this model") + ")";
            var material = preview.SourceMaterial(slot);
            return slot + ": " + (material != null ? material.name : preview.MaterialSlotNames[slot]);
        }
        string SlotLabel(int slot) => preview != null && slot < preview.MaterialSlotNames.Count ? slot + ": " + preview.MaterialSlotNames[slot] : slot.ToString() + (preview != null && preview.HasSnapshot ? " (" + L.Tr("not in this model") + ")" : "");

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
        internal NewProjectSettings TakeSettings()
        {
            if (Settings.Sets != null) foreach (var s in Settings.Sets) s.Name = s.Name?.Trim();
            Settings.Validate(); return Settings.Clone();
        }

        /// <summary>決める。設定が通らなければ（名前の重なりなど）理由を下に出して閉じない。</summary>
        internal void Accept()
        {
            EnsurePreview();
            NewProjectSettings settings;
            try { settings = TakeSettings(); }
            catch (ArgumentException ex) { error = ex.Message.Split('\n')[0].Split(new[] { " (Parameter" }, StringSplitOptions.None)[0]; Repaint(); return; }
            if (!Configure && preview != null) { settings.PreparedModel = new PreparedModel(preview); preview = null; }
            Close();
            try { accept?.Invoke(settings); } finally { settings.PreparedModel?.Dispose(); settings.PreparedModel = null; }
        }
        internal string Error => error;
    }
}
