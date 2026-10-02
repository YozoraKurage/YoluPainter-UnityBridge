using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// ショートカットのガード: YoluPainter の窓にフォーカスがあるとき、Unity と他の拡張のショートカットを止める。
    /// Unity はキーをまずフォーカスのある窓へ送り、そのあと全体のキー処理（ShortcutManager）を呼ぶ。窓が Use() しても修飾キーの
    /// 無い文字のキーは全体の処理に届く（W でシーンのツールが「移動」になる）ことを、コンテナの GUI モードで xdotool の実キーで
    /// 確かめた（2026-10-03）。ここではその順番（窓 → 全体の処理）を SendEvent と全体の処理の呼び出しで再現し、本物の
    /// ShortcutManager の Tools/Move・Tools/Scale が動くかどうかで止まったかを見る。実キーのテストは xdotool のある GUI だけ。
    /// </summary>
    [Category("Window")]
    public sealed class ShortcutGuardTests
    {
        static readonly FieldInfo GlobalHandler = typeof(EditorApplication).GetField("globalEventHandler", BindingFlags.NonPublic | BindingFlags.Static);
        string project; Tool savedTool;

        [SetUp] public void UseTemporaryProject()
        {
            project = Path.Combine(Path.GetTempPath(), "yolupainter-guard-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(project);
            PainterSettings.ProjectRoot = project; savedTool = Tools.current;
        }
        [TearDown] public void Clean()
        {
            Tools.current = savedTool; PainterSettings.ProjectRoot = null;
            if (Directory.Exists(project)) Directory.Delete(project, true);
        }

        static void Set(ShortcutGuardMode mode) => PainterSettings.UpdatePersonal(p => p.shortcutGuard = mode);

        /// <summary>
        /// Unity と同じ順番でキーを渡す: フォーカスのある窓（SendEvent）→ 全体のキー処理。全体のキー処理には、窓が Use() しても
        /// KeyDown のままの写しが渡る（実キーで測った）ので、写しを先頭のガードに、次に Unity の ShortcutManager に渡す。
        /// （C# から Event.current に入れたイベントは全体のキー処理からは見えない（実測）ので、globalEventHandler は呼ばずに
        /// 並びのとおりに 1 つずつ呼ぶ。ガードがその並びの先頭にいることは TheGuardStaysFirstInUnitysGlobalKeyHandler が確かめる。）
        /// </summary>
        static void Press(EditorWindow w, KeyCode key, bool control = false)
        {
            var e = Event.KeyboardEvent((control ? "^" : "") + key.ToString().ToLowerInvariant());
            w.SendEvent(new Event(e));
            var global = new Event(e);
            ShortcutGuard.Filter(global, EditorWindow.focusedWindow);
            var shortcuts = ShortcutIntegration();
            var contextManager = shortcuts.GetType().GetProperty("contextManager", Any).GetValue(shortcuts);
            contextManager.GetType().GetMethod("SetFocusedWindow", Any).Invoke(contextManager, new object[] { EditorWindow.focusedWindow });
            shortcuts.GetType().GetMethod("HandleKeyEvent", Any, null, new[] { typeof(Event) }, null).Invoke(shortcuts, new object[] { global });
        }

        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        static readonly Type IntegrationType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ShortcutManagement.ShortcutIntegration");
        /// <summary>Unity の ShortcutManager の本体（ShortcutController）。</summary>
        static object ShortcutIntegration() => IntegrationType.GetProperty("instance", Any).GetValue(null);

        [Test] public void TheDecisionFollowsTheSettingAndTypingIsAlwaysProtected()
        {
            Assert.That(ShortcutGuard.ShouldBlock(ShortcutGuardMode.BlockAll, editingText: false, tookKey: false), Is.True, "block all: even keys YoluPainter does not use");
            Assert.That(ShortcutGuard.ShouldBlock(ShortcutGuardMode.BlockPainterKeys, false, tookKey: true), Is.True);
            Assert.That(ShortcutGuard.ShouldBlock(ShortcutGuardMode.BlockPainterKeys, false, tookKey: false), Is.False, "other Unity shortcuts (Ctrl+P…) still work");
            Assert.That(ShortcutGuard.ShouldBlock(ShortcutGuardMode.Off, false, tookKey: true), Is.False, "Unity's default");
            foreach (var mode in (ShortcutGuardMode[])Enum.GetValues(typeof(ShortcutGuardMode)))
                Assert.That(ShortcutGuard.ShouldBlock(mode, editingText: true, tookKey: false), Is.True, mode + ": typing in a YoluPainter text field never triggers shortcuts");
        }

        [Test] public void TheGuardStaysFirstInUnitysGlobalKeyHandler()
        {
            Assert.That(ShortcutGuard.Available, Is.True, "Unity 2022.3 has EditorApplication.globalEventHandler");
            ShortcutGuard.Install();
            Assert.That(ShortcutGuard.Installed, Is.True);
            var names = ((Delegate)GlobalHandler.GetValue(null)).GetInvocationList().Select(d => d.Method.DeclaringType.Name + "." + d.Method.Name).ToList();
            Assert.That(names.IndexOf("ShortcutIntegration.EventHandler"), Is.GreaterThan(0), "Unity's ShortcutManager runs after the guard: " + string.Join(", ", names));
            EditorApplication.CallbackFunction later = () => { };
            var before = (EditorApplication.CallbackFunction)GlobalHandler.GetValue(null);
            GlobalHandler.SetValue(null, before + later); // 後から足された処理（ShortcutManager の再登録など）は後ろに付く
            try
            {
                Assert.That(ShortcutGuard.Installed, Is.True);
                ShortcutGuard.Install(); ShortcutGuard.Install();
                var list = ((Delegate)GlobalHandler.GetValue(null)).GetInvocationList();
                Assert.That(list.Count(d => d.Method.DeclaringType == typeof(ShortcutGuard)), Is.EqualTo(1), "installing again keeps a single copy");
                Assert.That(list.Contains(later), Is.True, "nothing else is dropped");
            }
            finally { GlobalHandler.SetValue(null, (EditorApplication.CallbackFunction)GlobalHandler.GetValue(null) - later); }
        }

        [Test] public void TheSettingIsKeptAndAnUnknownValueFallsBackToBlockAll()
        {
            Assert.That(PainterSettings.ShortcutGuard, Is.EqualTo(ShortcutGuardMode.BlockAll), "blocking is the default");
            Set(ShortcutGuardMode.BlockPainterKeys); PainterSettings.Reload();
            Assert.That(PainterSettings.ShortcutGuard, Is.EqualTo(ShortcutGuardMode.BlockPainterKeys));
            string json = File.ReadAllText(PainterSettings.PersonalPath);
            Assert.That(json, Does.Contain("\"shortcutGuard\": 1"));
            File.WriteAllText(PainterSettings.PersonalPath, json.Replace("\"shortcutGuard\": 1", "\"shortcutGuard\": 9"));
            PainterSettings.Reload();
            Assert.That(PainterSettings.ShortcutGuard, Is.EqualTo(ShortcutGuardMode.BlockAll));
            Assert.That(PainterSettings.Warnings.Any(w => w.Contains("Unity shortcuts while YoluPainter has focus")), Is.True, "the repair is reported");
        }

        [Test] public void UnitysToolShortcutsDoNotFireWhileAPainterWindowHasFocus()
        {
            if (Application.isBatchMode) Assert.Ignore("Window focus needs a non-batch Editor (test-daemon.sh start in GUI mode).");
            var w = EditorWindow.CreateWindow<GuardProbeWindow>(); w.position = new Rect(80, 80, 400, 300); w.Show(); w.Focus();
            try
            {
                Assert.That(EditorWindow.focusedWindow, Is.SameAs(w));
                // 止めないとき: 窓が W を使っても、Unity の Tools/Move が動く（実キーで見たのと同じ。この対照で、全体の処理が本物だとわかる）
                Set(ShortcutGuardMode.Off); Tools.current = Tool.View; Press(w, KeyCode.W);
                Assert.That(Tools.current, Is.EqualTo(Tool.Move), "control: with the guard off, Unity's W shortcut runs after the window");
                int blocked = ShortcutGuard.BlockedCount;
                Set(ShortcutGuardMode.BlockAll); Tools.current = Tool.View; w.Seen = ""; Press(w, KeyCode.W); Press(w, KeyCode.R);
                Assert.That(Tools.current, Is.EqualTo(Tool.View), "block all: neither W nor R reaches Unity");
                Assert.That(w.Seen, Is.EqualTo("W R "), "the window still receives every key");
                Assert.That(ShortcutGuard.BlockedCount, Is.EqualTo(blocked + 2));
                Set(ShortcutGuardMode.BlockPainterKeys); w.Took = true; Press(w, KeyCode.W);
                Assert.That(Tools.current, Is.EqualTo(Tool.View), "a key the window used does not also reach Unity");
                w.Took = false; Press(w, KeyCode.R);
                Assert.That(Tools.current, Is.EqualTo(Tool.Scale), "a key the window did not use still reaches Unity's shortcut");
                Set(ShortcutGuardMode.Off); Tools.current = Tool.View; w.Editing = true; Press(w, KeyCode.W);
                Assert.That(Tools.current, Is.EqualTo(Tool.View), "typing is protected even with the guard off");
            }
            finally { w.Close(); }
        }

        [Test] public void ThePainterTakesItsOwnToolKeyWithoutSwitchingTheSceneTool()
        {
            if (Application.isBatchMode) Assert.Ignore("Window focus needs a non-batch Editor (test-daemon.sh start in GUI mode).");
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            var w = EditorWindow.CreateWindow<TexturePaintWindow>(); w.position = new Rect(40, 40, 1200, 800); w.Show(); w.Focus();
            string recovery = w.RecoveryRoot;
            try
            {
                w.SendEvent(new Event { type = EventType.Repaint });
                // YoluPainter が使ったキーだけを止める設定: W は自動選択に、シーンのツールはそのまま
                Set(ShortcutGuardMode.BlockPainterKeys); Tools.current = Tool.View; w.Tool = TexturePaintWindow.PaintTool.Brush;
                Press(w, KeyCode.W);
                Assert.That(w.Tool, Is.EqualTo(TexturePaintWindow.PaintTool.MagicWand));
                Assert.That(Tools.current, Is.EqualTo(Tool.View));
                // 使わないキー（T）は Unity に届く。R は表示を回すのに使うので止まる（Unity の拡大縮小のツールにはならない）。
                // Ctrl+Y はやり直しとして使う（Unity のやり直しにはならない）
                Press(w, KeyCode.T);
                Assert.That(Tools.current, Is.EqualTo(Tool.Rect));
                Tools.current = Tool.View; Press(w, KeyCode.R);
                Assert.That(Tools.current, Is.EqualTo(Tool.View), "R (hold to rotate the view) is the painter's key now");
                w.Document.AddLayer("extra"); w.Document.Undo(); int layers = w.Document.Layers.Count;
                Press(w, KeyCode.Y, control: true);
                Assert.That(w.Document.Layers.Count, Is.EqualTo(layers + 1), "Ctrl+Y redoes in the painter");
            }
            finally { w.Close(); EditorShaderCompiler.TolerateErrorLogsIfBroken(); if (Directory.Exists(recovery)) Directory.Delete(recovery, true); }
        }

        /// <summary>本物の X のキー入力（xdotool）で、既定の設定ではシーンのツールが変わらないこと。xdotool と GUI の Unity があるときだけ。</summary>
        [UnityTest] public IEnumerator ARealKeyPressDoesNotSwitchTheSceneTool()
        {
            if (Application.isBatchMode) Assert.Ignore("Real key input needs a non-batch Editor.");
            if (!Xdotool("version", out _)) Assert.Ignore("xdotool is not available.");
            var w = EditorWindow.CreateWindow<GuardProbeWindow>(); w.titleContent = new GUIContent("YoluGuardProbe"); w.position = new Rect(120, 120, 420, 300); w.Show(); w.Focus();
            try
            {
                yield return Seconds(1);
                if (!Xdotool("search --name YoluGuardProbe", out string ids) || string.IsNullOrWhiteSpace(ids)) Skip("The probe window has no X window here.");
                Xdotool("windowfocus --sync " + ids.Split('\n')[0].Trim(), out _);
                yield return Seconds(.5);
                Set(ShortcutGuardMode.Off); Tools.current = Tool.View; w.Seen = "";
                Xdotool("key w", out _);
                yield return Until(() => w.Seen.Length > 0, 3);
                if (w.Seen.Length == 0) Skip("The real key did not reach the Editor during the test (input is not pumped here).");
                Assert.That(Tools.current, Is.EqualTo(Tool.Move), "control: Unity's default lets W through");
                Set(ShortcutGuardMode.BlockAll); Tools.current = Tool.View; w.Seen = "";
                Xdotool("key w", out _);
                yield return Until(() => w.Seen.Length > 0, 3);
                Assert.That(w.Seen, Is.EqualTo("W "));
                Assert.That(Tools.current, Is.EqualTo(Tool.View), "with the guard, the real W does not switch the Scene tool");
            }
            finally { w.Close(); }
        }

        /// <summary>スキップの理由はテストの一覧に出ないので、ログにも残す。</summary>
        static void Skip(string why) { UnityEngine.Debug.Log("ShortcutGuardTests real key: " + why); Assert.Ignore(why); }
        static IEnumerator Seconds(double s) { double end = EditorApplication.timeSinceStartup + s; while (EditorApplication.timeSinceStartup < end) yield return null; }
        static IEnumerator Until(Func<bool> done, double s) { double end = EditorApplication.timeSinceStartup + s; while (!done() && EditorApplication.timeSinceStartup < end) yield return null; }

        static bool Xdotool(string args, out string output)
        {
            output = "";
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("xdotool", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }))
                { output = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); return p.ExitCode == 0; }
            }
            catch (Exception) { return false; }
        }
    }
}
