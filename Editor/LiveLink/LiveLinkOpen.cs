using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>起動したスタンドアロンのプロセス（試験は偽物を返す）。</summary>
    internal interface ILaunchedStandalone
    {
        bool HasExited { get; }
    }

    sealed class LaunchedProcess : ILaunchedStandalone
    {
        readonly Process process;
        public LaunchedProcess(Process process) { this.process = process; }
        public bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }
    }

    /// <summary>「YoluPainter で開く」の外との境目（試験は差し替える）。</summary>
    internal sealed class LiveLinkOpenOptions
    {
        public Func<DateTime> UtcNow = () => DateTime.UtcNow;
        public Func<LiveLinkFolder> Folder = LiveLinkFolder.Current;
        public Func<string> ResolveExe = () => StandaloneLocator.Resolve(out _);
        /// <summary>場所が決まらないときに選んでもらう（空・null は取消）。</summary>
        public Func<string> ChooseExe = LiveLinkOpen.ChooseExeWithPanel;
        public Action<string> RememberExe = path => LiveLinkSettings.StandalonePath = path;
        /// <summary>スタンドアロンを <c>--livelink</c> つきで起動する。</summary>
        public Func<string, ILaunchedStandalone> Launch = LiveLinkOpen.StartProcess;
    }

    /// <summary>
    /// 「YoluPainter で開く」（送り直しも同じ）: 選んだ相手から頼みを作り、受け渡しのフォルダの <c>inbox/</c> に置く。スタンドアロンの起きている印
    /// （<c>presence.json</c>）が新しければ置くだけ、古い・無ければ <c>--livelink</c> つきで起動してから置く（起動を待たない。スタンドアロンは起きてから拾う）。
    /// 起動した直後（印がまだ無い間）にもう一度押されても、2 つ目は起動しない。同じ相手への、まだ拾われていない前の頼みは消してから置く
    /// （古い頼みを後から開かない）。返事は <see cref="LiveLinkReplies"/> が拾う。元のシーン・マテリアル・アセットには書かない。
    /// </summary>
    internal static class LiveLinkOpen
    {
        /// <summary>起動したプロセスが印を書くまで、もう一度は起動しない間（秒）。</summary>
        public const double LaunchGrace = 60;

        static ILaunchedStandalone launched;
        static DateTime launchedAt;

        /// <summary>選んだものを送れるか（シーンの GameObject か）。送れない理由はツールチップに出す。</summary>
        public static bool CanOpen(GameObject target, out string reason)
        {
            if (target == null) { reason = L.Tr("No GameObject is chosen."); return false; }
            if (EditorUtility.IsPersistent(target)) { reason = L.Tr("A Prefab asset cannot be chosen."); return false; }
            reason = null;
            return true;
        }

        [MenuItem("GameObject/Open in YoluPainter", false, 30)]
        static void OpenSelected() => Run(Selection.activeGameObject);

        [MenuItem("GameObject/Open in YoluPainter", true)]
        internal static bool CanOpenSelected() => CanOpen(Selection.activeGameObject, out _);

        /// <summary>メニュー・窓のボタンから。</summary>
        public static LiveLinkState Run(GameObject target)
        {
            if (!CanOpen(target, out var reason)) { Debug.LogWarning("YoluPainter Live Link: " + reason); return null; }
            return Send(target, new LiveLinkOpenOptions());
        }

        /// <summary>頼みを作って置く。結果の状態を <see cref="LiveLinkState"/> に入れて返す（取り消したら null で、状態は変えない）。</summary>
        internal static LiveLinkState Send(GameObject target, LiveLinkOpenOptions o)
        {
            var request = LiveLinkRequest.Build(target);
            var state = new LiveLinkState
            {
                phase = (int)LiveLinkState.Phase.Sent,
                request = request.Id, targetKey = request.TargetKey, targetName = request.TargetName,
                atTicks = o.UtcNow().Ticks,
            };
            foreach (var x in request.Refused) state.refused.Add(new LiveLinkState.Problem(x.Path, x.Reason));
            if (request.Renderers.Count == 0) return Fail(state, LiveLinkState.Failure.NothingToSend, "");
            string json = request.ToJson();
            if (request.OverLimits(json)) return Fail(state, LiveLinkState.Failure.TooLarge, "");

            var folder = o.Folder();
            if (folder == null) return Fail(state, LiveLinkState.Failure.NoFolder, "");
            try { folder.Ensure(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return Fail(state, LiveLinkState.Failure.NoFolder, e.Message); }

            if (!folder.StandaloneAwake(o.UtcNow()) && !RecentlyLaunched(o.UtcNow()))
            {
                string exe = o.ResolveExe();
                if (string.IsNullOrEmpty(exe))
                {
                    exe = o.ChooseExe();
                    if (string.IsNullOrEmpty(exe)) return null;
                    o.RememberExe(exe);
                }
                try { launched = o.Launch(exe); launchedAt = o.UtcNow(); }
                catch (Exception e) when (e is Win32Exception || e is IOException || e is InvalidOperationException || e is UnauthorizedAccessException)
                {
                    return Fail(state, LiveLinkState.Failure.LaunchFailed, e.Message);
                }
                state.launched = true;
            }

            try
            {
                RemoveUnclaimed(folder, request.TargetKey);
                // 覚えてから置く（返事がどれほど早く来ても、このプロジェクトの頼みとして読める）
                LiveLinkLedger.Add(request.Id, o.UtcNow(), request.TargetKey, request.TargetName);
                LiveLinkFolder.WriteReplacing(Path.Combine(folder.Inbox, request.Id + ".json"), json);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return Fail(state, LiveLinkState.Failure.WriteFailed, e.Message);
            }
            LiveLinkState.Set(state);
            LiveLinkReplies.Wake();
            return state;
        }

        static LiveLinkState Fail(LiveLinkState state, LiveLinkState.Failure failure, string detail)
        {
            state.phase = (int)LiveLinkState.Phase.Failed;
            state.failure = (int)failure;
            state.detail = detail ?? "";
            LiveLinkState.Set(state);
            return state;
        }

        static bool RecentlyLaunched(DateTime now) =>
            launched != null && !launched.HasExited && (now - launchedAt).TotalSeconds < LaunchGrace;

        /// <summary>起動したスタンドアロンが、まだ起きている印を書いていないかもしれない間か（窓の印）。</summary>
        internal static bool Starting(DateTime now) => RecentlyLaunched(now);

        /// <summary>試験: 起動の覚えを消す。</summary>
        internal static void ForgetLaunch() { launched = null; }

        /// <summary>同じ相手への、このプロジェクトが置いてまだ拾われていない頼みを消す。</summary>
        static void RemoveUnclaimed(LiveLinkFolder folder, string targetKey)
        {
            foreach (var e in LiveLinkLedger.Entries.Where(e => e.TargetKey == targetKey))
            {
                string path = Path.Combine(folder.Inbox, e.Id + ".json");
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { } // 拾われている最中: スタンドアロンに任せる
                catch (UnauthorizedAccessException) { }
            }
        }

        // ───────── 起動 ─────────

        /// <summary>
        /// スタンドアロンの起動の仕方: 場所のフォルダを作業場所にして、<c>--livelink</c>（設定に関わらず頼みを受ける）を付ける。
        /// Linux: Unity が自分のライブラリの場所を足している LD_LIBRARY_PATH を、そのまま子に渡さない（別の版のライブラリを読ませない）。
        /// </summary>
        internal static ProcessStartInfo StartInfo(string exe)
        {
            var info = new ProcessStartInfo(exe, "--livelink") { UseShellExecute = false };
            string dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir)) info.WorkingDirectory = dir;
            string libs = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            if (!string.IsNullOrEmpty(libs))
            {
                string kept = WithoutEditorLibraries(libs, EditorInstallFolder());
                if (kept.Length > 0) info.EnvironmentVariables["LD_LIBRARY_PATH"] = kept; else info.EnvironmentVariables.Remove("LD_LIBRARY_PATH");
            }
            return info;
        }

        static string EditorInstallFolder()
        {
            try { return Path.GetDirectoryName(EditorApplication.applicationPath) ?? ""; } catch (Exception) { return ""; }
        }

        /// <summary>LD_LIBRARY_PATH から、エディタのインストール先の下の項目を除く。</summary>
        internal static string WithoutEditorLibraries(string libraryPath, string editorFolder)
        {
            if (string.IsNullOrEmpty(editorFolder)) return libraryPath;
            string prefix = editorFolder.TrimEnd('/', '\\') + "/";
            return string.Join(":", libraryPath.Split(':').Where(entry => entry.Length > 0 && !entry.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal) && entry.Replace('\\', '/') != prefix.TrimEnd('/')));
        }

        internal static ILaunchedStandalone StartProcess(string exe)
        {
            var process = Process.Start(StartInfo(exe));
            return process == null ? null : new LaunchedProcess(process);
        }

        internal static string ChooseExeWithPanel()
        {
            bool windows = Application.platform == RuntimePlatform.WindowsEditor;
            return EditorUtility.OpenFilePanel(L.Tr("Choose the YoluPainter executable"), "", windows ? "exe" : "");
        }
    }
}
