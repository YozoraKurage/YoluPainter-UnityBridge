using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>返事 1 つ（<c>outbox/&lt;id&gt;-&lt;n&gt;.json</c>）。</summary>
    internal sealed class LiveLinkReply
    {
        internal enum ReplyKind { Opened, Refused, Exported }

        internal sealed class File
        {
            public string Material, Property, Path;
            public bool Srgb, NormalMap;
        }

        public string Request = "", AppVersion = "";
        public ReplyKind Kind;
        public readonly List<LiveLinkState.Problem> Problems = new List<LiveLinkState.Problem>();
        public readonly List<File> Files = new List<File>();

        /// <summary>読む（形式の版が違う・形が違うなら null）。</summary>
        public static LiveLinkReply Parse(string text)
        {
            Dictionary<string, object> o;
            try { o = JsonReader.Parse(text) as Dictionary<string, object>; }
            catch (FormatException) { return null; }
            if (o == null || JsonReader.Num(o, "format") != 1) return null;
            var r = new LiveLinkReply { Request = JsonReader.Str(o, "request") ?? "", AppVersion = JsonReader.Str(JsonReader.Obj(o, "app"), "version") ?? "" };
            switch (JsonReader.Str(o, "kind"))
            {
                case "opened": r.Kind = ReplyKind.Opened; break;
                case "refused": r.Kind = ReplyKind.Refused; break;
                case "exported": r.Kind = ReplyKind.Exported; break;
                default: return null;
            }
            foreach (var p in JsonReader.Arr(o, "problems") ?? new List<object>())
                if (p is Dictionary<string, object> d) r.Problems.Add(new LiveLinkState.Problem(JsonReader.Str(d, "path"), JsonReader.Str(d, "reason")));
            foreach (var f in JsonReader.Arr(o, "files") ?? new List<object>())
            {
                if (!(f is Dictionary<string, object> d)) continue;
                string path = JsonReader.Str(d, "path");
                if (string.IsNullOrEmpty(path)) continue;
                r.Files.Add(new File
                {
                    Material = JsonReader.Str(d, "material") ?? "", Property = JsonReader.Str(d, "property") ?? "", Path = path,
                    Srgb = JsonReader.Bool(d, "srgb") ?? true, NormalMap = JsonReader.Bool(d, "normal_map") ?? false,
                });
            }
            return r;
        }
    }

    /// <summary>
    /// 返事を拾う: 受け渡しのフォルダの <c>outbox/</c> を 1 秒ごとに見て、このプロジェクトが送った頼み（<see cref="LiveLinkLedger"/>）への返事だけを
    /// 読み、読んだら消す。見るのは、Live Link の窓が開いている間と、このプロジェクトが頼みを送ったことがある間（書き出しは後から来る）。
    /// Play の間・コンパイルとアセットの更新の間は見ない（取り込み・マテリアルへの当てを、終わってからにする）。
    /// <c>opened</c>・<c>refused</c> は窓の状態に、<c>exported</c> は <see cref="LiveLinkImport"/> が取り込んで、当てるかを確かめる。
    /// </summary>
    [InitializeOnLoad]
    internal static class LiveLinkReplies
    {
        public const double Interval = 1.0;

        static double next;
        static int windows;

        /// <summary>返事を当てる口（試験は差し替える。既定は <see cref="Apply"/>）。</summary>
        internal static Action<LiveLinkReply> Handle = Apply;

        static LiveLinkReplies()
        {
            EditorApplication.update += Tick;
        }

        /// <summary>窓が開いた・閉じた（開いている間は、送った頼みが無くても見る）。</summary>
        public static void WindowOpened() => windows++;
        public static void WindowClosed() => windows = Math.Max(0, windows - 1);

        /// <summary>次の見回りをすぐにする（頼みを送った直後）。</summary>
        public static void Wake() => next = 0;

        static bool Busy => EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating;

        static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < next) return;
            next = now + Interval;
            if (Busy) return;
            if (windows == 0 && LiveLinkLedger.Entries.Count == 0) return;
            var folder = LiveLinkFolder.Current();
            if (folder != null) Poll(folder);
        }

        /// <summary>1 回見る: このプロジェクトの頼みへの返事を、頼みごと・<c>n</c> の数の順（スタンドアロンが書いた順）に読んで消し、当てる。読んだ数を返す。</summary>
        public static int Poll(LiveLinkFolder folder)
        {
            int read = 0;
            var replies = new List<(string path, string id, string n)>();
            foreach (var path in LiveLinkFolder.JsonFiles(folder.Outbox))
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                string id = RequestIdOf(stem);
                if (id != null && LiveLinkLedger.Knows(id)) replies.Add((path, id, stem.Substring(id.Length + 1)));
            }
            // スタンドアロンは n を桁をそろえずに増やすので（-2 のあとが -10）、名前の順は書いた順にならない
            replies.Sort((a, b) => { int c = string.CompareOrdinal(a.id, b.id); return c != 0 ? c : CompareNumbers(a.n, b.n); });
            foreach (var (path, id, _) in replies)
            {
                string text = LiveLinkFolder.ReadSmall(path, out bool tooLarge);
                if (text == null && !tooLarge) continue; // 読めない（ほかのプロセスが開いている）: 次の見回りで
                try { System.IO.File.Delete(path); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                read++;
                if (text == null) continue; // 大きすぎる返事は読まずに捨てる
                var reply = LiveLinkReply.Parse(text);
                if (reply == null || reply.Request != id) continue; // 決まりに合わない返事は捨てる
                Handle(reply);
            }
            return read;
        }

        /// <summary>返事の名前（拡張子の前）<c>&lt;id&gt;-&lt;n&gt;</c> から id（形が違えば null）。</summary>
        internal static string RequestIdOf(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return null;
            int dash = stem.LastIndexOf('-');
            if (dash <= 0 || dash == stem.Length - 1) return null;
            for (int i = dash + 1; i < stem.Length; i++) if (stem[i] < '0' || stem[i] > '9') return null;
            return stem.Substring(0, dash);
        }

        /// <summary>10 進の数字だけの 2 つの文字列を、数として比べる（桁があふれない。先頭の 0 は無いものとして）。</summary>
        internal static int CompareNumbers(string a, string b)
        {
            a = a.TrimStart('0'); b = b.TrimStart('0');
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        }

        /// <summary>返事を窓の状態に入れる。書き出しは取り込んで、当てるかを確かめる（どの頼みへの返事でも。書き出しは送ってからずっと後にも来る）。
        /// 受けた・断られたは、最後に送った頼み（<see cref="LiveLinkState.Pending"/>）への返事だけを入れる（前に送った頼みへの遅い返事で、今の状態を
        /// 書き換えない）。別の頼みの書き出しが先に来て状態の頼みが変わっても、最後に送った頼みは覚えている（<see cref="LiveLinkState.sent"/>）ので、
        /// その頼みへの返事はそのあとにも入る。</summary>
        public static void Apply(LiveLinkReply reply)
        {
            var current = LiveLinkState.Current;
            string pending = current.Pending;
            if (reply.Kind != LiveLinkReply.ReplyKind.Exported && pending.Length > 0 && pending != reply.Request) return;
            var entry = LiveLinkLedger.Find(reply.Request);
            var state = new LiveLinkState
            {
                request = reply.Request,
                targetKey = entry?.TargetKey ?? "", targetName = entry?.TargetName ?? "",
                sent = pending,
                atTicks = DateTime.UtcNow.Ticks,
            };
            state.problems.AddRange(reply.Problems);
            // 最後に送った頼みの、Unity が送れなかったレンダラーは残す
            state.refused.AddRange(current.refused);
            LiveLinkImport.Prepared prepared = null;
            switch (reply.Kind)
            {
                case LiveLinkReply.ReplyKind.Opened: state.phase = (int)LiveLinkState.Phase.Opened; break;
                case LiveLinkReply.ReplyKind.Refused: state.phase = (int)LiveLinkState.Phase.Refused; break;
                default:
                    state.phase = (int)LiveLinkState.Phase.Exported;
                    prepared = LiveLinkImport.Prepare(reply);
                    state.files = prepared.Rows.Count(r => r.Texture != null);
                    state.problems.AddRange(prepared.Rows.Where(r => r.Problem != null).Select(r => new LiveLinkState.Problem(r.NewPath, r.Problem)));
                    break;
            }
            LiveLinkState.Set(state);
            if (prepared != null) LiveLinkImport.Present(prepared);
        }
    }
}
