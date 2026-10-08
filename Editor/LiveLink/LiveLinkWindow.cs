using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// Live Link の窓: 相手（シーンの GameObject。選んだものに付いていく）・「YoluPainter で開く」（送り直しも同じ）・スタンドアロンの印
    /// （起動している・起動中・起動していない。ツールチップに最後の状態と時刻）・送れなかったレンダラーと合わなかった物と止まった理由の一覧。
    /// 状態の文の行は置かない（印とツールチップに寄せる）。説明の文も置かない（ツールチップ・理由だけ）。
    /// </summary>
    internal sealed class LiveLinkWindow : EditorWindow
    {
        [SerializeField] GameObject target;
        GameObject lastSelection;
        Vector2 scroll;
        double nextPresence;
        Presence presence;

        /// <summary>スタンドアロンの印。</summary>
        internal enum Presence { NotRunning, Starting, Running }

        /// <summary>試験が差し替える（起きている印を見る口）。</summary>
        internal static Func<bool> Awake = () => LiveLinkFolder.Current()?.StandaloneAwake(DateTime.UtcNow) ?? false;
        /// <summary>試験が差し替える（起動した直後で、まだ印が無い間か）。</summary>
        internal static Func<bool> Launching = () => LiveLinkOpen.Starting(DateTime.UtcNow);

        [MenuItem("YozoLab/YoluPainter/Live Link")]
        static void Open() { var w = GetWindow<LiveLinkWindow>(); w.titleContent = new GUIContent("Live Link"); w.Show(); }

        internal GameObject Target { get => target; set => target = value; }

        void OnEnable()
        {
            titleContent = new GUIContent("Live Link");
            if (target == null) FollowSelection();
            Selection.selectionChanged += OnSelection;
            LiveLinkState.Changed += OnStateChanged;
            EditorApplication.update += WatchPresence;
            L.LanguageChanged += Repaint;
            presence = SafePresence();
            LiveLinkReplies.WindowOpened();
        }

        void OnDisable()
        {
            Selection.selectionChanged -= OnSelection;
            LiveLinkState.Changed -= OnStateChanged;
            EditorApplication.update -= WatchPresence;
            L.LanguageChanged -= Repaint;
            LiveLinkReplies.WindowClosed();
        }

        void OnSelection() { FollowSelection(); Repaint(); }

        // 起動した直後は、次の見回りを待たずに印を起動中にする
        void OnStateChanged() { presence = SafePresence(); Repaint(); }

        void FollowSelection()
        {
            var go = Selection.activeGameObject;
            lastSelection = go;
            if (go != null && !EditorUtility.IsPersistent(go)) target = go;
        }

        void WatchPresence()
        {
            if (EditorApplication.timeSinceStartup < nextPresence) return;
            nextPresence = EditorApplication.timeSinceStartup + 1;
            var now = SafePresence();
            if (now != presence) { presence = now; Repaint(); }
        }

        static Presence SafePresence()
        {
            try { return Awake() ? Presence.Running : Launching() ? Presence.Starting : Presence.NotRunning; }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { return Presence.NotRunning; }
        }

        static GUIStyle dotRunning, dotStarting, dotStopped;
        static GUIStyle Dot(Presence p)
        {
            if (dotRunning == null)
            {
                dotRunning = new GUIStyle(EditorStyles.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
                dotRunning.normal.textColor = new Color(0.35f, 0.8f, 0.4f);
                dotStarting = new GUIStyle(dotRunning);
                dotStarting.normal.textColor = new Color(0.95f, 0.7f, 0.25f);
                dotStopped = new GUIStyle(dotRunning);
                dotStopped.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
            }
            return p == Presence.Running ? dotRunning : p == Presence.Starting ? dotStarting : dotStopped;
        }

        void OnGUI()
        {
            if (Selection.activeGameObject != lastSelection) FollowSelection();
            target = (GameObject)EditorGUILayout.ObjectField(new GUIContent(L.Tr("Target"), L.Tr("The GameObject in the scene that is opened in YoluPainter. It follows the selection.")), target, typeof(GameObject), true);
            if (target != null && EditorUtility.IsPersistent(target)) target = null;
            bool can = LiveLinkOpen.CanOpen(target, out string reason);
            using (new EditorGUI.DisabledScope(!can))
            {
                var content = new GUIContent(L.Tr("Open in YoluPainter"),
                    can ? L.Tr("Sends the target to the standalone YoluPainter, and starts it when it is not running. Pressing again sends the pose, BlendShapes and material values again.") : reason);
                if (GUILayout.Button(content, GUILayout.Height(36))) { LiveLinkOpen.Run(target); GUIUtility.ExitGUI(); }
            }
            var state = LiveLinkState.Current;
            DrawPresence(state);
            DrawReasons(state);
        }

        void DrawPresence(LiveLinkState s)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string tip = PresenceTip(presence, s);
                GUILayout.Label(new GUIContent("●", tip), Dot(presence), GUILayout.Width(16));
                GUILayout.Label(new GUIContent("YoluPainter", tip), EditorStyles.label);
                GUILayout.FlexibleSpace();
            }
        }

        void DrawReasons(LiveLinkState s)
        {
            var rows = Rows(s);
            if (rows.Count == 0) return;
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var row in rows)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(new GUIContent(row.Name, row.NameTip), EditorStyles.label, GUILayout.MinWidth(60), GUILayout.ExpandWidth(true));
                    GUILayout.Label(new GUIContent(row.Reason, row.ReasonTip), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                }
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>印のツールチップ: 起動しているか と、最後の状態（相手の名前と時刻つき）。</summary>
        internal static string PresenceTip(Presence p, LiveLinkState s)
        {
            string head = p == Presence.Running ? L.Tr("YoluPainter is running") : p == Presence.Starting ? L.Tr("Starting YoluPainter…") : L.Tr("YoluPainter is not running");
            string last = LastText(s);
            return string.IsNullOrEmpty(last) ? head : head + "\n" + last;
        }

        /// <summary>最後の状態の文（相手の名前と、その時刻。何もしていなければ空）。</summary>
        internal static string LastText(LiveLinkState s)
        {
            string name = s.targetName ?? "";
            string at = s.AtUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            switch (s.Kind)
            {
                case LiveLinkState.Phase.Sent: return L.Tr("Sent {0} ({1})", name, at);
                case LiveLinkState.Phase.Opened: return L.Tr("Opened {0} in YoluPainter ({1})", name, at);
                case LiveLinkState.Phase.Refused: return L.Tr("YoluPainter refused {0} ({1})", name, at);
                case LiveLinkState.Phase.Exported: return L.Tr("Exported the textures of {0} ({1})", name, at);
                case LiveLinkState.Phase.Failed: return L.Tr("{0} was not sent ({1})", name, at);
                default: return "";
            }
        }

        /// <summary>理由の一覧の 1 行（名前・その全部の道・理由の文・理由の言葉か詳しい文）。</summary>
        internal readonly struct Row
        {
            public readonly string Name, NameTip, Reason, ReasonTip;
            public Row(string name, string nameTip, string reason, string reasonTip) { Name = name; NameTip = nameTip; Reason = reason; ReasonTip = reasonTip; }
        }

        /// <summary>一覧の行: Unity の側で止まった理由（相手の名前で）・断られて理由が無いとき・Unity が送れなかったレンダラー・スタンドアロンが
        /// 合わなかった物。ファイルの道は名前だけ（全部はツールチップに）、レンダラーは相手の根からの道。</summary>
        internal static List<Row> Rows(LiveLinkState s)
        {
            var rows = new List<Row>();
            string failure = FailureText(s);
            if (failure != null) rows.Add(new Row(s.targetName, "", failure, s.detail ?? ""));
            else if (s.Kind == LiveLinkState.Phase.Refused && s.problems.Count == 0) rows.Add(new Row(s.targetName, "", L.Tr("Refused"), ""));
            foreach (var p in s.refused) rows.Add(ProblemRow(p, s.targetName));
            foreach (var p in s.problems) rows.Add(ProblemRow(p, s.targetName));
            return rows;
        }

        static Row ProblemRow(LiveLinkState.Problem p, string targetName)
        {
            string name = string.IsNullOrEmpty(p.path) ? targetName : LiveLinkFolder.IsAbsolute(p.path) ? System.IO.Path.GetFileName(p.path) : p.path;
            return new Row(name, p.path, LiveLinkReason.Text(p.reason), p.reason);
        }

        /// <summary>Unity の側で止まった理由の文（止まっていなければ null）。詳しい文（例外の文）はツールチップに。</summary>
        internal static string FailureText(LiveLinkState s)
        {
            if (s.Kind != LiveLinkState.Phase.Failed) return null;
            switch (s.FailureKind)
            {
                case LiveLinkState.Failure.NothingToSend: return L.Tr("No renderer can be sent");
                case LiveLinkState.Failure.NoFolder: return L.Tr("The Live Link folder cannot be used");
                case LiveLinkState.Failure.WriteFailed: return L.Tr("The request cannot be written");
                case LiveLinkState.Failure.LaunchFailed: return L.Tr("YoluPainter cannot be started");
                case LiveLinkState.Failure.TooLarge: return LiveLinkReason.Text(LiveLinkReason.TooLarge);
                default: return null;
            }
        }

        /// <summary>スタンドアロンの実行ファイル（空なら、Windows はインストーラーの場所を使う）。Preferences に出す。</summary>
        internal static void DrawExecutable()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string path = EditorGUILayout.TextField(new GUIContent(L.Tr("YoluPainter executable"), L.Tr("The standalone YoluPainter that Open in YoluPainter starts. When empty, the location written by the Windows installer is used.")), LiveLinkSettings.StandalonePath);
                if (path != LiveLinkSettings.StandalonePath) LiveLinkSettings.StandalonePath = path;
                if (GUILayout.Button(L.TrIn("LiveLink", "Choose…"), GUILayout.Width(80)))
                {
                    string chosen = LiveLinkOpen.ChooseExeWithPanel();
                    if (!string.IsNullOrEmpty(chosen)) { LiveLinkSettings.StandalonePath = chosen; GUI.FocusControl(null); }
                }
            }
        }

        /// <summary>書き出しの置き場の元。Preferences に出す。</summary>
        internal static void DrawExportFolder()
        {
            string folder = EditorGUILayout.TextField(new GUIContent(L.Tr("Export folder"), L.Tr("Where YoluPainter suggests exporting the textures of a target: a folder named after the target, under this folder. Relative to the project.")), LiveLinkSettings.ExportFolder);
            if (folder != LiveLinkSettings.ExportFolder) LiveLinkSettings.ExportFolder = folder;
        }
    }
}
