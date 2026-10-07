using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// このプロジェクトが送った頼みの覚え（<c>Library/YoluPainter/LiveLink/requests.tsv</c>。id・送った時刻・相手の鍵・相手の名前）。
    /// 受け渡しのフォルダはこの PC のすべての Unity が使うので、返事はここにある id の物だけを読む（ほかのプロジェクトの返事を読まない・消さない）。
    /// 書き出しは送ってからずっと後（エディターを開き直した後）にも来るので、エディターのセッションをまたいで残す。新しい <see cref="Keep"/> 件・
    /// <see cref="MaxAge"/> 以内だけを残す。プロジェクトの外に出さない（Library はバージョン管理に入れない場所）。
    /// </summary>
    internal static class LiveLinkLedger
    {
        public const int Keep = 200;
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

        internal readonly struct Entry
        {
            public readonly string Id, TargetKey, TargetName;
            public readonly DateTime SentUtc;
            public Entry(string id, DateTime sentUtc, string targetKey, string targetName) { Id = id; SentUtc = sentUtc; TargetKey = targetKey ?? ""; TargetName = targetName ?? ""; }
        }

        /// <summary>試験が差し替える（null なら Library の下）。</summary>
        internal static string PathOverride;

        public static string FilePath => PathOverride ?? Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "YoluPainter", "LiveLink", "requests.tsv");

        static List<Entry> cache;
        static string cachePath;

        public static IReadOnlyList<Entry> Entries
        {
            get
            {
                string path = FilePath;
                if (cache == null || cachePath != path) { cache = Load(path); cachePath = path; }
                return cache;
            }
        }

        public static bool Knows(string id) => !string.IsNullOrEmpty(id) && Entries.Any(e => e.Id == id);

        public static Entry? Find(string id)
        {
            foreach (var e in Entries) if (e.Id == id) return e;
            return null;
        }

        /// <summary>送った頼みを覚える（古いものを落とす）。</summary>
        public static void Add(string id, DateTime sentUtc, string targetKey, string targetName)
        {
            var list = new List<Entry>(Entries) { new Entry(id, sentUtc, targetKey, targetName) };
            var cutoff = sentUtc - MaxAge;
            list = list.Where(e => e.SentUtc >= cutoff).OrderBy(e => e.SentUtc).ToList();
            if (list.Count > Keep) list = list.Skip(list.Count - Keep).ToList();
            Save(FilePath, list);
            cache = list; cachePath = FilePath;
        }

        /// <summary>覚えを読み直す（試験・別のエディターが書いたとき）。</summary>
        public static void Reload() { cache = null; }

        static List<Entry> Load(string path)
        {
            var list = new List<Entry>();
            try
            {
                if (!File.Exists(path)) return list;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var f = line.Split('\t');
                    if (f.Length < 2 || f[0].Length == 0) continue;
                    if (!long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) continue;
                    list.Add(new Entry(f[0], new DateTime(ticks, DateTimeKind.Utc), f.Length > 2 ? Unescape(f[2]) : "", f.Length > 3 ? Unescape(f[3]) : ""));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return list;
        }

        static void Save(string path, List<Entry> list)
        {
            var sb = new StringBuilder();
            foreach (var e in list)
                sb.Append(e.Id).Append('\t').Append(e.SentUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(Escape(e.TargetKey)).Append('\t').Append(Escape(e.TargetName)).Append('\n');
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            LiveLinkFolder.WriteReplacing(path, sb.ToString());
        }

        static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

        static string Unescape(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                sb.Append(c == 't' ? '\t' : c == 'n' ? '\n' : c == 'r' ? '\r' : c);
            }
            return sb.ToString();
        }
    }
}
