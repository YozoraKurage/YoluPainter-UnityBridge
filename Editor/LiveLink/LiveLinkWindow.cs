using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// Live Link の小さな窓: 前に出すのは「YoluPainter で開く」の大きなボタン 1 つと今の状態だけ（選んだゲームオブジェクトを、スタンドアロンの
    /// YoluPainter へ送って見せる。起動・つなぐ・送るを 1 つの操作で。LiveLinkOpen）。つなぎ先の名前・実行ファイルの場所・個別の操作・診断は「詳しく」に畳む。
    /// 描いたテクスチャはそのレンダラーに MaterialPropertyBlock で当てて見せる（マテリアル・テクスチャのアセットは変えない。切ると外れる）。
    /// </summary>
    internal sealed class LiveLinkWindow : EditorWindow
    {
        [SerializeField] bool details;
        string linkName;
        string message;
        Vector2 scroll;
        ulong testServer;

        [MenuItem("YozoLab/YoluPainter/Live Link")]
        static void Open() { var w = GetWindow<LiveLinkWindow>(); w.titleContent = new GUIContent("Live Link"); w.Show(); }

        void OnEnable()
        {
            linkName = LiveLinkSettings.LinkName;
            EditorApplication.update += RepaintWhileLinked;
            Selection.selectionChanged += Repaint;
            LiveLinkOpen.StateChanged += Repaint;
        }

        void OnDisable()
        {
            EditorApplication.update -= RepaintWhileLinked;
            Selection.selectionChanged -= Repaint;
            LiveLinkOpen.StateChanged -= Repaint;
            StopTestServer();
        }

        double nextRepaint;
        void RepaintWhileLinked()
        {
            if (LiveLinkSession.Active == null || EditorApplication.timeSinceStartup < nextRepaint) return;
            nextRepaint = EditorApplication.timeSinceStartup + 0.25;
            Repaint();
        }

        void OnGUI()
        {
            string problem = LiveLinkBridge.Problem;
            if (problem != null) { EditorGUILayout.HelpBox(problem, MessageType.Error); return; }

            var session = LiveLinkSession.Active;
            DrawOpen(session);
            EditorGUILayout.Space();
            details = EditorGUILayout.Foldout(details, L.Tr("Details"), true);
            if (!details) return;
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawDetails(session);
            if (session != null) DrawSession(session);
            DrawDiagnostics(session);
            EditorGUILayout.EndScrollView();
        }

        // ───────── 前に出すもの: 大きなボタン 1 つと状態 ─────────

        void DrawOpen(LiveLinkSession session)
        {
            var target = Selection.activeGameObject;
            bool can = LiveLinkOpen.CanOpen(target, out string reason);
            bool running = LiveLinkOpen.Running != null;
            using (new EditorGUI.DisabledScope(!can || running))
            {
                var content = new GUIContent(L.Tr("Open in YoluPainter"),
                    can ? L.Tr("Starts YoluPainter if it is not running, connects to it and shows the selected GameObject with the textures painted there.") : reason);
                if (GUILayout.Button(content, GUILayout.Height(40))) { message = null; LiveLinkOpen.Run(target); }
            }
            string state = StateText(session, out MessageType? warning);
            if (warning != null) EditorGUILayout.HelpBox(state, warning.Value);
            else EditorGUILayout.LabelField(StateContent(session, state), EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Warning);
        }

        /// <summary>状態の行: 文字と、つないだまま版か機能がずれているときの警告の印・ツールチップ（両方の版・どちらを上げるか・使えない機能の名前）。
        /// つながりが閉じたあとは付けない（<see cref="LiveLinkSession.LiveReport"/>）。</summary>
        internal static GUIContent StateContent(LiveLinkSession session, string state)
            => session == null || LiveLinkOpen.Running != null ? new GUIContent(state) : LiveLinkNotice.StateContent(state, session.LiveReport, LiveLinkNotice.OwnVersion);

        /// <summary>今の状態を短い文で。失敗の理由は警告の枠で出す。</summary>
        internal static string StateText(LiveLinkSession session, out MessageType? warning)
        {
            warning = null;
            var flow = LiveLinkOpen.Running;
            if (flow != null) return flow.Info;
            if (session != null)
            {
                switch (session.Status)
                {
                    case LiveLinkStatus.Connected:
                        return session.Model != null && session.Model.Root != null ? L.Tr("Showing {0}", session.Model.Root.name) : L.Tr("Connected");
                    case LiveLinkStatus.Connecting:
                        return L.Tr("Connecting…");
                    case LiveLinkStatus.Failed:
                        warning = MessageType.Warning;
                        return session.DisplayStatusText ?? "";
                }
            }
            var last = LiveLinkOpen.Last;
            if (last != null && last.Phase == LiveLinkOpen.PhaseKind.Failed && !last.Quiet) { warning = MessageType.Warning; return last.Problem; }
            return L.Tr("Not connected");
        }

        // ───────── 詳しく ─────────

        void DrawDetails(LiveLinkSession session)
        {
            var (min, max) = LiveLinkBridge.ProtocolVersions;
            EditorGUILayout.LabelField(L.Tr("Bridge"), L.Tr("version {0}, protocol {1}–{2}", LiveLinkBridge.AbiVersion, min, max), EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(session != null))
            {
                string n = EditorGUILayout.TextField(new GUIContent(L.Tr("Link name"), L.Tr("The name the standalone YoluPainter listens on (the same on both sides).")), linkName);
                if (n != linkName) { linkName = n; LiveLinkSettings.LinkName = n; }
            }
            DrawExecutable();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (session == null)
                {
                    if (GUILayout.Button(L.Tr("Connect"))) Connect(LiveLinkSettings.LinkName);
                }
                else if (GUILayout.Button(L.Tr("Disconnect"))) { session.Dispose(); StopTestServer(); message = null; }
            }
            if (session == null) return;
            var model = session.Model != null && session.Model.Root != null ? session.Model.Root : Selection.activeGameObject;
            using (new EditorGUI.DisabledScope(session.Status != LiveLinkStatus.Connected || model == null))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(session.Model == null ? L.Tr("Send model") : L.Tr("Send model again"))) message = session.SendModel(model);
                using (new EditorGUI.DisabledScope(session.Model == null))
                    if (GUILayout.Button(L.Tr("Stop showing"))) session.CloseModel();
            }
            string line = DetailsStatusLine(session);
            if (!string.IsNullOrEmpty(line)) EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
        }

        /// <summary>詳しくの一番下の、ブリッジの今の文（断られたときは自分の言語の理由。警告の枠が同じ文を出しているときは重ねない）。</summary>
        internal static string DetailsStatusLine(LiveLinkSession session)
        {
            string text = session.DisplayStatusText;
            if (string.IsNullOrEmpty(text)) return null;
            return StateText(session, out MessageType? warning) == text && warning != null ? null : text;
        }

        /// <summary>スタンドアロンの実行ファイル（空なら、Windows はインストーラーの場所を使う）。</summary>
        internal static void DrawExecutable()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string path = EditorGUILayout.TextField(new GUIContent(L.Tr("YoluPainter executable"), L.Tr("The standalone YoluPainter that Open in YoluPainter starts. When empty, the location written by the Windows installer is used.")), LiveLinkSettings.StandalonePath);
                if (path != LiveLinkSettings.StandalonePath) LiveLinkSettings.StandalonePath = path;
                if (GUILayout.Button(L.Tr("Choose…"), GUILayout.Width(80)))
                {
                    string chosen = LiveLinkOpen.ChooseExeWithPanel();
                    if (!string.IsNullOrEmpty(chosen)) { LiveLinkSettings.StandalonePath = chosen; GUI.FocusControl(null); }
                }
            }
        }

        void Connect(string name)
        {
            try { LiveLinkSession.Start(name); message = null; }
            catch (System.Exception e) { message = e.Message; }
        }

        void DrawSession(LiveLinkSession session)
        {
            var model = session.Model;
            if (model != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(L.Tr("Model: {0} ({1} renderers, {2} vertices)", model.Root != null ? model.Root.name : "—", model.Meshes.Count, model.VertexTotal), EditorStyles.boldLabel);
                foreach (var note in model.Notes) EditorGUILayout.LabelField(note, EditorStyles.wordWrappedMiniLabel);
                for (int i = 0; i < model.Materials.Count; i++)
                {
                    var m = model.Materials[i];
                    var set = session.Display.Sets.FirstOrDefault(s => s.Info.generation == (uint)model.Generation && s.Info.material == i);
                    string shown = m.Shown.Count == 0 ? L.Tr("nothing to show") : string.Join(", ", m.Shown.Select(c => L.Tr(c.Channel.ToString()) + " → " + c.Property));
                    string state = set == null ? L.Tr("no texture set yet") : L.Tr("{0} × {1}, {2} uploads", set.Info.width, set.Info.height, set.Uploads);
                    EditorGUILayout.LabelField(m.Name, shown + " · " + state);
                    foreach (var note in m.Notes.Distinct()) EditorGUILayout.LabelField("  " + note, EditorStyles.wordWrappedMiniLabel);
                }
            }
            var last = session.Display.LastUpload;
            if (last.Tiles > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(L.Tr("Last update"), L.Tr("{0} tiles ({1}): copy {2:0.00} ms, upload {3:0.00} ms, {4:0.0} ms after painting", last.Tiles, last.Strip ? L.Tr("strip") : L.Tr("whole texture"), last.CopyMs, last.UploadMs, last.LatencyMs));
                EditorGUILayout.LabelField(L.Tr("Property blocks"), session.Display.AppliedCount.ToString(), EditorStyles.miniLabel);
            }
            if (session.Log.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(L.Tr("Log"), EditorStyles.boldLabel);
                for (int i = session.Log.Count - 1; i >= 0 && i >= session.Log.Count - 12; i--) EditorGUILayout.LabelField(session.Log[i], EditorStyles.wordWrappedMiniLabel);
            }
        }

        void DrawDiagnostics(LiveLinkSession session)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(L.Tr("Diagnostics"), EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(session != null))
                if (GUILayout.Button(new GUIContent(L.Tr("Connect to the test pattern"), L.Tr("Without the standalone: a test pattern server inside this editor (a checker per material) checks the link, the shared memory and the property blocks.")))) StartTestServer();
        }

        void StartTestServer()
        {
            StopTestServer();
            string name = "yolupainter-selftest-" + System.Diagnostics.Process.GetCurrentProcess().Id;
            testServer = LiveLinkTestServer.Start(name, 1024, 128);
            if (testServer == 0) { message = L.Tr("The test pattern server could not start."); return; }
            Connect(name);
        }

        void StopTestServer()
        {
            if (testServer == 0) return;
            if (LiveLinkSession.Active != null && LiveLinkSession.Active.LinkName.StartsWith("yolupainter-selftest-")) LiveLinkSession.Active.Dispose();
            LiveLinkTestServer.Stop(testServer);
            testServer = 0;
        }
    }
}
