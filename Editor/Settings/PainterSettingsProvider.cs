using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>Project Settings &gt; YoluPainter。上が「プロジェクトで共有」、下が「このプロジェクトでの自分だけの設定」。
    /// 値は変えたその場で検査して保存する（パスと数値の欄は確定したとき）。</summary>
    internal sealed class PainterSettingsProvider : SettingsProvider
    {
        public const string Path = "Project/YoluPainter";
        /// <summary>フォルダ選択と確認のダイアログ（テストは差し替える）。</summary>
        internal static IPainterDialogs Dialogs = EditorPainterDialogs.Instance;

        PainterSettings.Shared shared;
        PainterSettings.Personal personal;
        string error, notice;
        /// <summary>この環境で GPU の合成が使えるか（開いたときに 1 度調べる）。使えなければ理由。</summary>
        string gpuUnavailable; bool gpuChecked;

        PainterSettingsProvider() : base(Path, SettingsScope.Project, new[] { "YoluPainter", "brush", "brushes", "recovery", "budget", "memory", "texture paint", "GPU", "CPU", "compositing", "threads" }) { }

        [SettingsProvider] public static SettingsProvider Create() => new PainterSettingsProvider();

        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement) { Reload(); }

        void Reload() { PainterSettings.Reload(); shared = PainterSettings.SharedSettings; personal = PainterSettings.PersonalSettings; gpuChecked = false; }

        public override void OnGUI(string searchContext)
        {
            if (shared == null || personal == null) Reload();
            EditorGUIUtility.labelWidth = 220;
            foreach (var warning in PainterSettings.Warnings) EditorGUILayout.HelpBox(warning, MessageType.Warning);
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (!string.IsNullOrEmpty(notice)) EditorGUILayout.HelpBox(notice, MessageType.Info);

            Header("Shared with the project", PainterSettings.SharedPath, "Committed with the project; the same for everyone who opens it.");
            using (new EditorGUI.DisabledScope(PainterSettings.SharedIsReadOnly))
            {
                EditorGUI.BeginChangeCheck();
                shared.defaultResolution = EditorGUILayout.IntPopup("Default document size", shared.defaultResolution,
                    PainterSettings.Resolutions.Select(r => r + " × " + r).ToArray(), PainterSettings.Resolutions);
                if (EditorGUI.EndChangeCheck()) SaveShared();

                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                string folder = EditorGUILayout.DelayedTextField(new GUIContent("Shared brush folder", "A folder inside the project (relative path). Brushes imported here are shared through version control. Empty = not used."), shared.projectBrushFolder);
                if (EditorGUI.EndChangeCheck()) { shared.projectBrushFolder = folder.Trim(); SaveShared(); }
                if (GUILayout.Button("Choose…", GUILayout.Width(70))) ChooseSharedFolder();
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(shared.projectBrushFolder))) if (GUILayout.Button("Clear", GUILayout.Width(50))) { shared.projectBrushFolder = ""; SaveShared(); }
                EditorGUILayout.EndHorizontal();
                if (BrushLibrary.Project.Enabled) FolderInfo(BrushLibrary.Project);
            }

            EditorGUILayout.Space(12);
            Header("Only for you in this project", PainterSettings.PersonalPath, "Kept in UserSettings: not committed, not shared.");
            using (new EditorGUI.DisabledScope(PainterSettings.PersonalIsReadOnly))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                string mine = EditorGUILayout.DelayedTextField(new GUIContent("Brush folder", "Where your imported brushes are kept. Empty = UserSettings/YoluPainter/Brushes. May be outside the project to share brushes between your projects."), personal.brushFolder);
                if (EditorGUI.EndChangeCheck()) notice = ChangePersonalBrushFolder(mine.Trim(), Dialogs, out error) ?? notice;
                if (GUILayout.Button("Choose…", GUILayout.Width(70)))
                {
                    string picked = Dialogs.OpenFolder("Brush folder", PainterSettings.BrushFolder);
                    if (!string.IsNullOrEmpty(picked)) notice = ChangePersonalBrushFolder(ToProjectRelativeIfInside(picked), Dialogs, out error) ?? notice;
                }
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(personal.brushFolder))) if (GUILayout.Button("Default", GUILayout.Width(60))) notice = ChangePersonalBrushFolder("", Dialogs, out error) ?? notice;
                EditorGUILayout.EndHorizontal();
                FolderInfo(BrushLibrary.Personal);

                EditorGUI.BeginChangeCheck();
                personal.showBundledBrushes = EditorGUILayout.Toggle(new GUIContent("Show bundled brushes", "The Krita 4 default tips shipped with the package. Hiding them does not break brushes that already use them."), personal.showBundledBrushes);
                personal.backupsToKeep = BackupsPopup(personal.backupsToKeep);
                personal.shortcutGuard = (ShortcutGuardMode)EditorGUILayout.IntPopup(new GUIContent("Unity shortcuts while painting", "While a YoluPainter window has focus. Block all: only YoluPainter's keys work (Unity's and other extensions' shortcuts, such as W switching the Scene tool or Ctrl+P entering Play, do nothing). Block YoluPainter's keys: a key YoluPainter uses does not also reach Unity; other Unity shortcuts work. Off: Unity's default, where letter keys YoluPainter uses (W, E…) also switch the Scene tool. Typing in a YoluPainter text field never triggers shortcuts."),
                    (int)personal.shortcutGuard, new[] { new GUIContent("Block all"), new GUIContent("Block YoluPainter's keys"), new GUIContent("Off (Unity's default)") }, new[] { 0, 1, 2 });
                if (!ShortcutGuard.Available) EditorGUILayout.HelpBox("This Unity version does not expose the hook the shortcut guard needs; Unity's shortcuts are not blocked.", MessageType.Warning);
                CompositingFields(personal);
                personal.recoveryIntervalSeconds = EditorGUILayout.IntSlider(new GUIContent("Recovery checkpoint every (s)", "How often unsaved work is written to Library/YoluPainter for crash recovery. It is also written on focus loss, reload and Play."), personal.recoveryIntervalSeconds, PainterSettings.MinRecoverySeconds, PainterSettings.MaxRecoverySeconds);
                EditorGUILayout.LabelField("Memory budgets (MiB) — automatic values follow this machine's " + (PainterSettings.SystemMemoryMiB / 1024.0).ToString("0.#") + " GB of memory", EditorStyles.miniBoldLabel);
                personal.undoBudgetMiB = BudgetField(new GUIContent("Undo history", "Oldest undo steps are dropped beyond this (the minimum steps below are always kept). 0 keeps only those."), personal.undoBudgetMiB, PainterSettings.Budget.Undo);
                personal.minUndoSteps = EditorGUILayout.IntSlider(new GUIContent("Minimum undo steps", "The newest steps kept even beyond the undo budget, so a large fill or transform can still be undone (GIMP keeps 5 the same way). 0 makes the budget strict."), personal.minUndoSteps, 0, PainterSettings.MaxMinUndoSteps);
                personal.sourceBudgetMiB = BudgetField(new GUIContent("Layer pixels", "Total pixel data of all layers. Edits that would exceed it are refused without changing anything. A full 4096² layer takes 64 MiB."), personal.sourceBudgetMiB, PainterSettings.Budget.Source);
                personal.gpuCacheMiB = BudgetField(new GUIContent("GPU cache", "Layer blocks and partial composites kept on the GPU (automatic: 1/8 of the reported " + PainterSettings.GraphicsMemoryMiB + " MiB of video memory) so sliders and edits on large documents redraw quickly. Released after two minutes without drawing. 0 keeps nothing: the same result, only slower."), personal.gpuCacheMiB, PainterSettings.Budget.GpuCache);
                personal.strokeBudgetMiB = BudgetField(new GUIContent("One operation", "Undo data one stroke, fill, gradient or transform may keep. A bigger one is refused or cancelled safely without changing anything."), personal.strokeBudgetMiB, PainterSettings.Budget.Stroke);
                if (EditorGUI.EndChangeCheck()) SavePersonal();
            }
        }

        /// <summary>予算の欄: 「Auto」のときは自動の値を灰色で見せ、外すと今の自動の値から数値を入れられる。</summary>
        static int BudgetField(GUIContent label, int value, PainterSettings.Budget budget)
        {
            EditorGUILayout.BeginHorizontal();
            bool auto = value == PainterSettings.Automatic;
            int automatic = PainterSettings.AutomaticBudgetMiB(budget);
            using (new EditorGUI.DisabledScope(auto))
            {
                int shown = EditorGUILayout.DelayedIntField(label, auto ? automatic : value);
                if (!auto) value = shown;
            }
            bool nextAuto = GUILayout.Toggle(auto, new GUIContent("Auto", "Follow this machine's memory (" + automatic + " MiB now)"), EditorStyles.miniButton, GUILayout.Width(44));
            EditorGUILayout.EndHorizontal();
            return nextAuto == auto ? value : nextAuto ? PainterSettings.Automatic : automatic;
        }

        static readonly GUIContent[] CompositingNames = { new GUIContent("Automatic"), new GUIContent("GPU"), new GUIContent("CPU") };
        static readonly int[] CompositingValues = { (int)CompositorBackend.Automatic, (int)CompositorBackend.Gpu, (int)CompositorBackend.Cpu };

        /// <summary>「表示の合成」と CPU のスレッドの数の欄。値は呼び出し側の変更の確かめ（保存）に入る。</summary>
        void CompositingFields(PainterSettings.Personal p)
        {
            if (!gpuChecked) { gpuUnavailable = TileGpuCompositor.GpuCompositingAvailable(out string reason) ? null : reason; gpuChecked = true; }
            p.displayCompositing = (CompositorBackend)EditorGUILayout.IntPopup(new GUIContent("Display compositing",
                "Where the layers are composited for the 2D view and the 3D preview. Automatic: on the GPU when it can be used (as before). " +
                "GPU: on the GPU; when it cannot be used, on the CPU, and the status bar says so. CPU: on the CPU with the threads below, sending only the " +
                "changed tiles to the display. A brush stroke is about as fast either way, but changing a layer's opacity, blend mode or visibility " +
                "composites the whole canvas again on the CPU (the GPU keeps the layers below). Saving, exporting and recovery always composite on the CPU, whatever is chosen. Open windows switch after the current stroke."),
                (int)p.displayCompositing, CompositingNames, CompositingValues);
            EditorGUILayout.LabelField(" ", CompositingNote(p.displayCompositing, gpuUnavailable), EditorStyles.wordWrappedMiniLabel);
            p.cpuThreads = ThreadsPopup(p.cpuThreads);
        }
        internal static string CompositingNote(CompositorBackend choice, string gpuUnavailable)
        {
            string device = SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceType;
            switch (choice)
            {
                case CompositorBackend.Cpu: return "Composited on the CPU; changed tiles are sent to the display texture.";
                case CompositorBackend.Gpu: return gpuUnavailable == null ? "Composited on the GPU (" + device + ")." : "The GPU cannot composite here (" + gpuUnavailable + "), so the CPU is used.";
                default:
                    if (gpuUnavailable != null) return "Here: the CPU, because the GPU cannot composite (" + gpuUnavailable + ").";
                    var automatic = TileGpuCompositor.ResolveAutomatic(out string why);
                    return automatic == CompositorBackend.Gpu ? "Here: the GPU (" + device + ")." : "Here: the CPU (" + why + ").";
            }
        }

        internal static int[] ThreadChoices()
        {
            var values = new System.Collections.Generic.List<int> { PainterSettings.Automatic, 1 };
            for (int n = 2; n < PainterSettings.ProcessorCount; n *= 2) values.Add(n);
            if (PainterSettings.ProcessorCount > 1) values.Add(PainterSettings.ProcessorCount);
            return values.ToArray();
        }
        static int ThreadsPopup(int value)
        {
            var choices = ThreadChoices();
            var values = choices.Contains(value) ? choices : choices.Concat(new[] { value }).ToArray();
            var names = values.Select(v => new GUIContent(v == PainterSettings.Automatic ? "Automatic (" + PainterSettings.ProcessorCount + ", one per logical processor)" : v == 1 ? "1 (no parallel work)" : v.ToString())).ToArray();
            return EditorGUILayout.IntPopup(new GUIContent("CPU threads", "The most threads YoluPainter's CPU work uses at once: compositing on the CPU, brushes, fills, gradients, transforms, filters, the magic wand and selection edits. " +
                "Any value gives the same pixels; fewer threads are slower on large canvases but leave cores to other programs. 1 runs everything on Unity's main thread. Mesh baking has its own limit."), value, names, values);
        }

        static readonly int[] BackupChoices = { -1, 0, 1, 3, 5, 10, 20 };
        static int BackupsPopup(int value)
        {
            var values = BackupChoices.Contains(value) ? BackupChoices : BackupChoices.Concat(new[] { value }).ToArray();
            var names = values.Select(v => new GUIContent(v < 0 ? "All (never delete)" : v == 0 ? "None (no backup)" : v.ToString())).ToArray();
            return EditorGUILayout.IntPopup(new GUIContent(".ylp backups to keep", "When a .ylp is saved over, the previous version is moved to <name>.ylp-backups~ next to it (Unity does not import folders ending in ~). Older backups beyond this number are deleted."), value, names, values);
        }

        static void Header(string title, string file, string help)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(Relative(file) + " — " + help, EditorStyles.wordWrappedMiniLabel);
        }
        static void FolderInfo(BrushLibrary library)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(" ", library.Folder + " (" + library.Presets.Count + " brushes)", EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(!Directory.Exists(library.Folder))) if (GUILayout.Button("Show", EditorStyles.miniButton, GUILayout.Width(50))) EditorUtility.RevealInFinder(library.Folder);
            EditorGUILayout.EndHorizontal();
        }

        void SaveShared()
        {
            try { PainterSettings.Save(shared, null); error = null; }
            catch (Exception ex) { error = ex.Message; }
        }
        void SavePersonal()
        {
            try { PainterSettings.Save(null, personal); error = null; }
            catch (Exception ex) { error = ex.Message; }
        }
        void ChooseSharedFolder()
        {
            string picked = Dialogs.OpenFolder("Shared brush folder (inside the project)", PainterSettings.ProjectRoot);
            if (string.IsNullOrEmpty(picked)) return;
            string relative = ToProjectRelativeIfInside(picked);
            if (System.IO.Path.IsPathRooted(relative)) { error = "The shared brush folder must be inside the project folder."; return; }
            shared.projectBrushFolder = relative; SaveShared();
        }

        /// <summary>個人のブラシ置き場を変える。今の置き場にブラシがあれば、新しい置き場へ複写するか尋ねる（元は消さない）。</summary>
        /// <returns>結果の知らせ。何もしなかったら null。</returns>
        internal static string ChangePersonalBrushFolder(string setting, IPainterDialogs dialogs, out string error)
        {
            error = null;
            var next = PainterSettings.PersonalSettings;
            if (next.brushFolder == setting) return null;
            string problem = PainterSettings.CheckPersonalBrushFolder(setting);
            if (problem != null) { error = problem; return null; }
            string oldFolder = PainterSettings.BrushFolder;
            int existing = BrushLibrary.Personal.Presets.Count;
            next.brushFolder = setting;
            string newFolder = string.IsNullOrWhiteSpace(setting) ? PainterSettings.DefaultBrushFolder : System.IO.Path.GetFullPath(System.IO.Path.Combine(PainterSettings.ProjectRoot, setting));
            int copied = 0;
            if (existing > 0 && !SamePath(oldFolder, newFolder) &&
                dialogs.Confirm("Brush folder", "Copy your " + existing + " imported brush(es) from\n" + oldFolder + "\nto\n" + newFolder + "?\n\nThe old folder is left as it is.", "Copy", "Don't copy"))
            {
                try { copied = BrushLibrary.Personal.CopyTo(newFolder); }
                catch (Exception ex) { error = "Copying failed (" + ex.Message + "); the brush folder was not changed."; return null; }
            }
            try { PainterSettings.Save(null, next); }
            catch (Exception ex) { error = ex.Message; return null; }
            return "Brush folder is now " + newFolder + (copied > 0 ? "; copied " + copied + " brush(es)." : ".");
        }

        /// <summary>プロジェクトの中ならプロジェクトからの相対パス（区切りは /）、外ならそのまま。</summary>
        internal static string ToProjectRelativeIfInside(string absolute)
        {
            string full = System.IO.Path.GetFullPath(absolute).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            string root = System.IO.Path.GetFullPath(PainterSettings.ProjectRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (SamePath(full, root)) return ".";
            string prefix = root + System.IO.Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, PainterSettings.PathComparison) ? full.Substring(prefix.Length).Replace('\\', '/') : full;
        }
        static bool SamePath(string a, string b) => string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('/', '\\'), System.IO.Path.GetFullPath(b).TrimEnd('/', '\\'), PainterSettings.PathComparison);
        static string Relative(string path) { string r = ToProjectRelativeIfInside(path); return r; }
    }
}
