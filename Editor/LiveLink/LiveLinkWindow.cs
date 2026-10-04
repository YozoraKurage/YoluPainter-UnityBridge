using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// Live Link の小さな窓: スタンドアロンの YoluPainter につなぎ、シーンのモデル（選んだゲームオブジェクトの下のレンダラー）を送り、
    /// 描いたテクスチャをそのレンダラーに MaterialPropertyBlock で当てて見せる（マテリアル・テクスチャのアセットは変えない。切ると外れる）。
    /// </summary>
    internal sealed class LiveLinkWindow : EditorWindow
    {
        const string NameKey = "Yozolab.YoluPainter.LiveLink.Name";
        [SerializeField] GameObject target;
        string linkName;
        string message;
        Vector2 scroll;
        ulong testServer;

        [MenuItem("YozoLab/YoluPainter/Live Link")]
        static void Open() { var w = GetWindow<LiveLinkWindow>(); w.titleContent = new GUIContent("Live Link"); w.Show(); }

        void OnEnable()
        {
            linkName = EditorPrefs.GetString(NameKey, LiveLinkSession.DefaultLinkName);
            if (target == null) target = Selection.activeGameObject;
            EditorApplication.update += RepaintWhileLinked;
        }

        void OnDisable()
        {
            EditorApplication.update -= RepaintWhileLinked;
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
            var (min, max) = LiveLinkBridge.ProtocolVersions;
            EditorGUILayout.LabelField(L.Tr("Bridge"), L.Tr("version {0}, protocol {1}–{2}", LiveLinkBridge.AbiVersion, min, max), EditorStyles.miniLabel);

            var session = LiveLinkSession.Active;
            using (new EditorGUI.DisabledScope(session != null))
            {
                string n = EditorGUILayout.TextField(new GUIContent(L.Tr("Link name"), L.Tr("The name the standalone YoluPainter listens on (the same on both sides).")), linkName);
                if (n != linkName) { linkName = n; EditorPrefs.SetString(NameKey, n); }
            }
            target = (GameObject)EditorGUILayout.ObjectField(new GUIContent(L.Tr("Model"), L.Tr("The GameObject in the scene whose renderers are sent and shown.")), target, typeof(GameObject), true);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L.Tr("Use selection"))) target = Selection.activeGameObject;
                if (session == null)
                {
                    if (GUILayout.Button(L.Tr("Connect"))) Connect(linkName);
                }
                else if (GUILayout.Button(L.Tr("Disconnect"))) { session.Dispose(); StopTestServer(); message = null; }
            }
            if (session != null)
            {
                using (new EditorGUI.DisabledScope(session.Status != LiveLinkStatus.Connected))
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(session.Model == null ? L.Tr("Send model") : L.Tr("Send model again"))) message = session.SendModel(target);
                    using (new EditorGUI.DisabledScope(session.Model == null))
                        if (GUILayout.Button(L.Tr("Stop showing"))) session.CloseModel();
                }
                EditorGUILayout.HelpBox(session.StatusText ?? "", session.Status == LiveLinkStatus.Failed ? MessageType.Warning : MessageType.Info);
            }
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Warning);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (session != null) DrawSession(session);
            DrawDiagnostics(session);
            EditorGUILayout.EndScrollView();
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
