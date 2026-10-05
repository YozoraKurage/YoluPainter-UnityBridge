using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.LiveLink;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// Live Link の窓（実際の EditorWindow に Layout と Repaint を流して、描く途中で例外やエラーが出ないこと）: 何も選んでいないとき・レンダラーを選んだとき・
    /// 「詳しく」を開いたとき・つながってモデルを見せているとき。窓は batchmode では動かないのでスキップする（devcontainer では GUI モードの常駐で回す）。
    /// </summary>
    [Category("Window")]
    public sealed class LiveLinkWindowTests
    {
        LiveLinkWindow window;
        ulong server;
        readonly List<Object> owned = new List<Object>();
        readonly List<string> errors = new List<string>();
        Object[] selection;

        void Collect(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            string lower = message.ToLowerInvariant();
            if (lower.Contains("shader") || lower.Contains("hlsl") || lower.Contains("cginc")) return;
            errors.Add(type + ": " + message);
        }

        [SetUp]
        public void Open()
        {
            if (Application.isBatchMode) Assert.Ignore("The window needs a non-batch Editor (test-daemon.sh start in GUI mode).");
            Assert.That(LiveLinkBridge.Problem, Is.Null);
            L.OverrideLanguage(PainterLanguage.English);
            errors.Clear(); Application.logMessageReceived += Collect;
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            selection = Selection.objects;
            window = EditorWindow.CreateWindow<LiveLinkWindow>();
            window.position = new Rect(40, 40, 420, 520);
        }

        [TearDown]
        public void Close()
        {
            if (Application.isBatchMode) return;
            try
            {
                LiveLinkSession.StopActive();
                LiveLinkTestServer.Stop(server); server = 0;
                if (window != null) window.Close();
                Selection.objects = selection;
                foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            }
            finally { Application.logMessageReceived -= Collect; L.OverrideLanguage(null); }
            Assert.That(errors, Is.Empty, "errors logged while the window was drawn");
        }

        void Draw()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            window.SendEvent(new Event { type = EventType.Layout });
            window.SendEvent(new Event { type = EventType.Repaint });
        }

        void ShowDetails()
        {
            var so = new SerializedObject(window);
            so.FindProperty("details").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        GameObject Model()
        {
            var root = new GameObject("WindowTestModel"); owned.Add(root);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); owned.Add(cube);
            cube.transform.SetParent(root.transform, false);
            return root;
        }

        [Test]
        public void TheWindowDrawsWithNothingSelectedWithARendererSelectedAndWithDetailsOpen()
        {
            Selection.activeGameObject = null;
            Draw();
            Selection.activeGameObject = Model();
            Draw();
            ShowDetails();
            Draw();
            Selection.activeGameObject = null;
            Draw();
        }

        [Test]
        public void TheWindowDrawsWhileConnectedAndShowingAModel()
        {
            var root = Model();
            string name = "ylp-unity-window-" + Process.GetCurrentProcess().Id;
            server = LiveLinkTestServer.Start(name, 256, 64);
            Assert.That(server, Is.Not.EqualTo(0UL));
            Selection.activeGameObject = root;
            var flow = LiveLinkOpen.Begin(root, new LiveLinkOpenOptions { LinkName = name }, false);
            var clock = Stopwatch.StartNew();
            while (!flow.Finished && clock.Elapsed.TotalSeconds < 15) { flow.Tick(); Draw(); Thread.Sleep(10); }
            Assert.That(flow.Phase, Is.EqualTo(LiveLinkOpen.PhaseKind.Done), flow.Problem);
            ShowDetails();
            Draw();
            LiveLinkSession.Active.Tick();
            Draw();
        }
    }
}
