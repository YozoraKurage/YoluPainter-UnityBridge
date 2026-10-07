using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// Live Link の窓が見せる今の状態: 最後に送った頼みと、それへの最後の返事（受けた・断られた・書き出した）、Unity が送れなかったレンダラー、
    /// スタンドアロンが合わなかった物。ドメインのリロードをまたいで残す（SessionState。エディターを閉じると消える）。
    /// 文は持たず、理由の言葉と種類だけを持つ（言語を切り替えたら、その言語で出す）。
    /// </summary>
    [Serializable]
    internal sealed class LiveLinkState
    {
        internal enum Phase { None = 0, Sent = 1, Opened = 2, Refused = 3, Exported = 4, Failed = 5 }

        /// <summary>Unity の側で止まった理由（<see cref="Phase.Failed"/> のとき）。</summary>
        internal enum Failure { None = 0, NothingToSend = 1, NoFolder = 2, WriteFailed = 3, LaunchFailed = 4, TooLarge = 5 }

        [Serializable]
        internal sealed class Problem
        {
            public string path = "", reason = "";
            public Problem() { }
            public Problem(string path, string reason) { this.path = path ?? ""; this.reason = reason ?? ""; }
        }

        public int phase;
        public int failure;
        /// <summary>止まった理由の詳しい文（例外の文。言語に依らない）。</summary>
        public string detail = "";
        public string request = "", targetKey = "", targetName = "";
        /// <summary>最後に送った頼みの id（返事で状態が別の頼みの物に変わっても、これは変わらない）。空なら、状態の頼みが最後に送った頼み
        /// （送った・受けた・断られた・送れなかった）か、何も送っていない（書き出しだけを受けた）。</summary>
        public string sent = "";
        public long atTicks;
        public bool launched;
        public int files;
        /// <summary>最後に送った頼みで、Unity が送れなかったレンダラー（相手の根からの道と理由の言葉）。</summary>
        public List<Problem> refused = new List<Problem>();
        /// <summary>スタンドアロンが返した、合わなかった物・断った理由。</summary>
        public List<Problem> problems = new List<Problem>();

        public Phase Kind => (Phase)phase;

        /// <summary>受けた・断られたの返事を待っている頼みの id（最後に送った頼み。何も送っていなければ空）。前に送った頼みへの遅い返事は、これと
        /// 違うので入れない。</summary>
        public string Pending => !string.IsNullOrEmpty(sent) ? sent : (Kind == Phase.None || Kind == Phase.Exported ? "" : request);
        public Failure FailureKind => (Failure)failure;
        public DateTime AtUtc => new DateTime(Math.Max(0, atTicks), DateTimeKind.Utc);

        const string Key = "Yozolab.YoluPainter.LiveLink.State";

        static LiveLinkState current;

        /// <summary>状態が変わった（窓が描き直す）。</summary>
        public static event Action Changed;

        public static LiveLinkState Current
        {
            get
            {
                if (current != null) return current;
                string json = SessionState.GetString(Key, "");
                if (!string.IsNullOrEmpty(json))
                {
                    try { current = JsonUtility.FromJson<LiveLinkState>(json); }
                    catch (ArgumentException) { current = null; }
                }
                return current ?? (current = new LiveLinkState());
            }
        }

        /// <summary>今の状態を置き換える。</summary>
        public static void Set(LiveLinkState state)
        {
            current = state ?? new LiveLinkState();
            SessionState.SetString(Key, JsonUtility.ToJson(current));
            Changed?.Invoke();
        }

        /// <summary>覚えを消す（試験）。</summary>
        internal static void Clear()
        {
            current = null;
            SessionState.EraseString(Key);
            Changed?.Invoke();
        }

        internal LiveLinkState Copy() => JsonUtility.FromJson<LiveLinkState>(JsonUtility.ToJson(this));
    }
}
