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
        int ExitCode { get; }
    }

    sealed class LaunchedProcess : ILaunchedStandalone
    {
        readonly Process process;
        public LaunchedProcess(Process process) { this.process = process; }
        public bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }
        public int ExitCode { get { try { return process.ExitCode; } catch (InvalidOperationException) { return -1; } } }
    }

    /// <summary>つなぎの 1 回（試験は偽物を返す。本物は <see cref="LiveLinkSession"/>）。</summary>
    internal interface IOpenLink
    {
        LiveLinkStatus Status { get; }
        /// <summary>終わった知らせの種類（終わっていなければ null）と、断られたときの <c>RejectCode</c>。</summary>
        LiveLinkEventKind? EndedBy { get; }
        int EndedCode { get; }
        string StatusText { get; }
        /// <summary>窓や試験がつながりを切った・別のものに置き換えた。</summary>
        bool Replaced { get; }
        void Tick();
        string SendModel(GameObject root);
        void Dispose();
    }

    sealed class SessionLink : IOpenLink
    {
        readonly LiveLinkSession session;
        public SessionLink(LiveLinkSession session) { this.session = session; }
        public LiveLinkStatus Status => session.Status;
        public LiveLinkEventKind? EndedBy => session.EndedBy;
        public int EndedCode => session.EndedCode;
        public string StatusText => session.StatusText;
        public bool Replaced => LiveLinkSession.Active != session;
        public void Tick() => session.Tick();
        public string SendModel(GameObject root) => session.SendModel(root);
        public void Dispose() => session.Dispose();
    }

    /// <summary>「YoluPainter で開く」の外との境目（試験は差し替える）。</summary>
    internal sealed class LiveLinkOpenOptions
    {
        public string LinkName = LiveLinkSettings.LinkName;
        /// <summary>false なら起動しない（リロードのあとのつなぎ直し。待ち受けていなければ <see cref="ResumeWindow"/> 秒だけ待って静かにやめる）。</summary>
        public bool MayStart = true;
        /// <summary>つながるまでの上限（秒）。つなぎ始めから、起動したときは起動から数え直す。つなぎ中のまま答えが来ない相手にも効く。</summary>
        public double TimeLimit = 60;
        /// <summary>つなぎ直しが、待ち受けを探す・つなぎ中の答えを待つ上限（秒）。</summary>
        public double ResumeWindow = 4;
        /// <summary>ほかの Unity とつながっている（断られた）あいだ、起動せずに待つ上限（秒）。つなぎ直しは <see cref="ResumeWindow"/> が先に切れるので、その秒数まで。
        /// つなぎ直しの直後は、前のつながりが閉じるまでの短い間だけ断られる。</summary>
        public double BusyWindow = 10;
        /// <summary>つなぎ直す間隔（秒）。</summary>
        public double RetryInterval = 0.25;
        public Func<double> Clock = () => EditorApplication.timeSinceStartup;
        /// <summary>つなぎ始める（待たない）。</summary>
        public Func<string, IOpenLink> Connect = LiveLinkOpen.ConnectSession;
        public Func<string> ResolveExe = () => StandaloneLocator.Resolve(out _);
        /// <summary>場所が決まらないときに選んでもらう（空・null は取消）。</summary>
        public Func<string> ChooseExe = LiveLinkOpen.ChooseExeWithPanel;
        public Action<string> RememberExe = path => LiveLinkSettings.StandalonePath = path;
        public Func<string, ILaunchedStandalone> Launch = LiveLinkOpen.StartProcess;
        /// <summary>進み具合（文・0〜1）を出して、取り消されたら true を返す。終わるときは文が null。null なら出さない。</summary>
        public Func<string, float, bool> Progress;
    }

    /// <summary>
    /// 「YoluPainter で開く」: 選んだゲームオブジェクトを、スタンドアロンの YoluPainter へ 1 つの操作で送る。つながっていればそのまま送り、
    /// 待ち受けていなければスタンドアロンを <c>--livelink</c> つきで起動して、待ち受けが始まるまでつなぎ直しながら待ち（上限と取消がある）、つながったら送る。
    /// 待ち受けの有無はつないでみて決める（鍵のファイルの場所の決め方をここで写さない。古い鍵のファイルが残っていても起動を飛ばさない）:
    /// 答える（断る）相手には起動せず、答えない相手（待ち受けていない・設定で Live Link を切っている・挨拶の途中で失敗する）には起動する。
    /// エディタの更新ごとに <see cref="Tick"/> を進める状態機械で、エディタを止めない。元のシーン・マテリアル・アセットには書かない（送るのは
    /// <see cref="LiveLinkSession.SendModel"/> だけ）。
    /// </summary>
    internal sealed class LiveLinkOpen
    {
        internal enum PhaseKind { Connecting, Waiting, Done, Cancelled, Failed }

        /// <summary>今動いている 1 つ（同時に 1 つだけ）。</summary>
        public static LiveLinkOpen Running { get; private set; }
        /// <summary>最後に終わった 1 つ（窓が結果を出す）。</summary>
        public static LiveLinkOpen Last { get; private set; }
        /// <summary>状態が変わった（窓が描き直す）。</summary>
        public static event Action StateChanged;

        public GameObject Root { get; }
        public PhaseKind Phase { get; private set; } = PhaseKind.Connecting;
        /// <summary>失敗の理由（失敗でなければ null）。</summary>
        public string Problem { get; private set; }
        public bool Launched { get; private set; }
        public int Attempts { get; private set; }
        public bool Finished => Phase == PhaseKind.Done || Phase == PhaseKind.Cancelled || Phase == PhaseKind.Failed;
        /// <summary>リロードのあとのつなぎ直し（失敗しても何も言わない）。</summary>
        public bool Quiet => !o.MayStart;

        readonly LiveLinkOpenOptions o;
        IOpenLink link;
        ILaunchedStandalone process;
        double started, launchedAt, deadline = double.PositiveInfinity, nextAttempt;
        string lastText;
        bool registered, progressShown, busy;

        LiveLinkOpen(GameObject root, LiveLinkOpenOptions options) { Root = root; o = options; }

        // ───────── 入口 ─────────

        /// <summary>メニュー・窓のボタンから: 選んだゲームオブジェクトを開く。使えない理由は警告に出す（メニューは使えないときに押せない）。</summary>
        public static LiveLinkOpen Run(GameObject root)
        {
            if (!CanOpen(root, out var reason)) { Debug.LogWarning("YoluPainter Live Link: " + reason); return null; }
            if (!LiveLinkBridge.Available) { Debug.LogWarning("YoluPainter Live Link: " + LiveLinkBridge.Problem); return null; }
            return Begin(root, new LiveLinkOpenOptions { Progress = ProgressBar }, true);
        }

        /// <summary>始める。<paramref name="autoTick"/> が false なら、呼び手が <see cref="Tick"/> を進める（試験）。</summary>
        internal static LiveLinkOpen Begin(GameObject root, LiveLinkOpenOptions options, bool autoTick)
        {
            Running?.Cancel();
            var flow = new LiveLinkOpen(root, options);
            flow.started = options.Clock();
            // 答えが来ないままのつなぎ中でも、上限で終える（起動したら、その時から数え直す）
            flow.deadline = flow.started + (options.MayStart ? options.TimeLimit : options.ResumeWindow);
            Running = flow;
            Last = null;
            // 前のつながりが、もうつながっていないなら捨てる。つながっている・つなぎ中ならそのまま使う
            var active = LiveLinkSession.Active;
            if (active != null && active.LinkName == options.LinkName && (active.Status == LiveLinkStatus.Connected || active.Status == LiveLinkStatus.Connecting)) flow.link = new SessionLink(active);
            else active?.Dispose();
            if (autoTick) { EditorApplication.update += flow.Tick; flow.registered = true; }
            StateChanged?.Invoke();
            if (autoTick) flow.Tick();
            return flow;
        }

        /// <summary>メニューを押せるか（選んだものがシーンのゲームオブジェクトで、送れるレンダラーがあるか）。ブリッジの DLL は読まない
        /// （メニューを開くたびに呼ばれ、使わない人に DLL を読ませないため）。押せない理由はツールチップなどに出す。</summary>
        public static bool CanOpen(GameObject root, out string reason)
        {
            if (root == null) { reason = L.Tr("No GameObject is chosen."); return false; }
            if (EditorUtility.IsPersistent(root)) { reason = L.Tr("A Prefab asset cannot be chosen."); return false; }
            if (!LiveLinkModel.HasSendableRenderer(root)) { reason = L.Tr("{0} has no active mesh renderers to send.", root.name); return false; }
            reason = null;
            return true;
        }

        [MenuItem("GameObject/Open in YoluPainter", false, 30)]
        static void OpenSelected() => Run(Selection.activeGameObject);

        [MenuItem("GameObject/Open in YoluPainter", true)]
        internal static bool CanOpenSelected() => CanOpen(Selection.activeGameObject, out _);

        [InitializeOnLoadMethod]
        static void Hook() => AssemblyReloadEvents.beforeAssemblyReload += () => Running?.Cancel();

        // ───────── 状態 ─────────

        /// <summary>今の状態を短い文で（進み具合の文・窓の状態）。</summary>
        public string Info
        {
            get
            {
                switch (Phase)
                {
                    case PhaseKind.Done: return L.Tr("Showing {0}", Root != null ? Root.name : "—");
                    case PhaseKind.Failed: return Problem;
                    case PhaseKind.Cancelled: return L.Tr("Cancelled");
                    default: return Launched ? L.Tr("Starting YoluPainter…") : L.Tr("Connecting…");
                }
            }
        }

        float Fraction(double now) => Launched && o.TimeLimit > 0 ? (float)Math.Min(1, Math.Max(0, (now - launchedAt) / o.TimeLimit)) : 0f;

        // ───────── 進める ─────────

        public void Tick()
        {
            if (Finished) return;
            double now = o.Clock();
            if (Root == null) { End(PhaseKind.Failed, L.Tr("The GameObject was removed.")); return; }
            // 窓や試験がつながりを切った・置き換えたなら、取り消されたものとして終わる
            if (link != null && link.Replaced) { End(PhaseKind.Cancelled, null); return; }
            // 直ぐに終わる開き方（待ち受けていた）では進み具合の窓を出さず、起動を待つ・0.5 秒を超えるときだけ出す
            if (o.Progress != null && (Launched || now - started > 0.5))
            {
                progressShown = true;
                if (o.Progress(Info, Fraction(now))) { End(PhaseKind.Cancelled, null); return; }
            }
            if (Phase == PhaseKind.Connecting) TickConnecting(now);
            else if (Phase == PhaseKind.Waiting) TickWaiting(now);
        }

        void TickConnecting(double now)
        {
            if (link == null && !StartAttempt()) return;
            var s = link;
            s.Tick();
            switch (s.Status)
            {
                case LiveLinkStatus.Connected:
                    string problem = s.SendModel(Root);
                    if (problem != null) End(PhaseKind.Failed, problem); else End(PhaseKind.Done, null);
                    return;
                case LiveLinkStatus.Connecting:
                    GiveUp(now); // つなぎ中のまま答えが来ない相手でも、上限で終える
                    return;
                default:
                    s.Tick(); // 終わりの知らせを読み切ってから、どう終わったかを見る
                    AttemptEnded(s.EndedBy, s.EndedCode, s.StatusText, now);
                    return;
            }
        }

        bool StartAttempt()
        {
            try { link = o.Connect(o.LinkName); Attempts++; return true; }
            catch (Exception e) { End(PhaseKind.Failed, e.Message); return false; }
        }

        /// <summary>つなぎの 1 回が終わったあとの次の手。</summary>
        internal enum NextStep
        {
            /// <summary>待っても変わらない（版・鍵が合わない）。理由を出して終える。</summary>
            GiveUp,
            /// <summary>ほかの Unity とつながっている。起動せず、短く待ってつなぎ直す。</summary>
            WaitBusy,
            /// <summary>答える相手がいない。起動して、つなぎ直しながら待つ。</summary>
            Launch,
            /// <summary>起動済み・つなぎ直し。つなぎ直す。</summary>
            Retry,
        }

        /// <summary>YoluPainter の RejectCode.Busy。</summary>
        internal const int BusyCode = 2;

        internal static NextStep Decide(LiveLinkEventKind? endedBy, int endedCode, bool mayStart, bool launched)
        {
            if (endedBy == LiveLinkEventKind.Rejected) return endedCode == BusyCode ? NextStep.WaitBusy : NextStep.GiveUp;
            return mayStart && !launched ? NextStep.Launch : NextStep.Retry;
        }

        void AttemptEnded(LiveLinkEventKind? endedBy, int endedCode, string text, double now)
        {
            lastText = text;
            var next = Decide(endedBy, endedCode, o.MayStart, Launched);
            if (next == NextStep.GiveUp) { End(PhaseKind.Failed, lastText); return; }
            link.Dispose();
            link = null;
            busy = next == NextStep.WaitBusy;
            if (busy)
            {
                // 待ち受けている（ほかの Unity とつながっている、またはつなぎ直しの直後）。もう 1 つ起動しない
                deadline = Math.Min(deadline, now + o.BusyWindow);
            }
            else if (next == NextStep.Launch)
            {
                if (!LaunchStandalone(ref now)) return;
            }
            Phase = PhaseKind.Waiting;
            nextAttempt = now + o.RetryInterval;
            StateChanged?.Invoke();
        }

        bool LaunchStandalone(ref double now)
        {
            string exe = o.ResolveExe();
            if (string.IsNullOrEmpty(exe))
            {
                exe = o.ChooseExe();
                if (string.IsNullOrEmpty(exe)) { End(PhaseKind.Cancelled, null); return false; }
                o.RememberExe(exe);
            }
            try
            {
                process = o.Launch(exe);
            }
            catch (Exception e) when (e is Win32Exception || e is IOException || e is InvalidOperationException || e is UnauthorizedAccessException)
            {
                End(PhaseKind.Failed, L.Tr("YoluPainter could not be started: {0}", e.Message));
                return false;
            }
            // 選ぶ窓（モーダルで更新が止まる）や起動に時間がかかっても、上限と次の試しは起動した後から数える
            now = o.Clock();
            Launched = true;
            launchedAt = now;
            deadline = now + o.TimeLimit;
            return true;
        }

        void TickWaiting(double now)
        {
            if (GiveUp(now)) return;
            if (now >= nextAttempt) { Phase = PhaseKind.Connecting; TickConnecting(now); }
        }

        /// <summary>起動したプロセスが終わった・上限を超えたなら、失敗で終える（つなぎ中の session は End が捨てる）。</summary>
        bool GiveUp(double now)
        {
            if (process != null && process.HasExited) { End(PhaseKind.Failed, L.Tr("YoluPainter closed before it was connected (exit code {0}).", process.ExitCode)); return true; }
            if (now < deadline) return false;
            // ほかの Unity とつながっているあいだの待ちは断りの文を、つなぎ直しの間（静かにやめる）も、理由は持っておく
            double window = Launched || !Quiet ? o.TimeLimit : o.ResumeWindow;
            bool reason = !string.IsNullOrEmpty(lastText) && (busy || !Launched);
            End(PhaseKind.Failed, reason ? lastText : L.Tr("YoluPainter did not accept the connection within {0} s.", (int)Math.Round(window)));
            return true;
        }

        public void Cancel()
        {
            if (!Finished) End(PhaseKind.Cancelled, null);
        }

        void End(PhaseKind phase, string problem)
        {
            Phase = phase;
            Problem = problem;
            if (registered) { EditorApplication.update -= Tick; registered = false; }
            if (progressShown) o.Progress(null, 1f);
            // 取り消し・失敗で、つながっていない（つなぎ途中の）つながりを残さない
            if (phase != PhaseKind.Done && link != null && link.Status != LiveLinkStatus.Connected) link.Dispose();
            link = null;
            if (Running == this) Running = null;
            Last = this;
            StateChanged?.Invoke();
        }

        // ───────── 外との境目の既定 ─────────

        /// <summary>スタンドアロンの起動の仕方: 場所のフォルダを作業場所にして、<c>--livelink</c>（設定に関わらず待ち受ける）を付ける。</summary>
        internal static ProcessStartInfo StartInfo(string exe)
        {
            var info = new ProcessStartInfo(exe, "--livelink") { UseShellExecute = false };
            string dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir)) info.WorkingDirectory = dir;
            // Linux: Unity が自分のライブラリの場所を足している LD_LIBRARY_PATH を、そのまま子に渡さない（別の版のライブラリを読ませない）
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

        internal static IOpenLink ConnectSession(string linkName) => new SessionLink(LiveLinkSession.Start(linkName));

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

        static bool ProgressBar(string info, float fraction)
        {
            if (info == null) { EditorUtility.ClearProgressBar(); return false; }
            return EditorUtility.DisplayCancelableProgressBar("YoluPainter Live Link", info, fraction);
        }
    }
}
