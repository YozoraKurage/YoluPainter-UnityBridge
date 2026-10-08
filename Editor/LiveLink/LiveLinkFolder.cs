using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// 受け渡しのフォルダ。スタンドアロンと同じ道を自分で決める: Windows は <c>%LOCALAPPDATA%\YoluPainter\LiveLink</c>、macOS は
    /// <c>~/Library/Application Support/YoluPainter/LiveLink</c>、ほかは <c>${XDG_DATA_HOME:-~/.local/share}/YoluPainter/LiveLink</c>
    /// （<c>XDG_DATA_HOME</c> が絶対の道でなければ既定）。環境変数 <see cref="EnvDir"/>（絶対の道）があればそれを使う（スタンドアロンも同じ）。
    /// 中に <c>inbox/</c>（Unity → スタンドアロン）・<c>claimed/</c>（スタンドアロンが拾った物）・<c>outbox/</c>（スタンドアロン → Unity）・<c>presence.json</c>。
    /// Unix では作るフォルダを自分だけ（0700）にする（スタンドアロンは、ほかの人も入れるフォルダを読まない）。書くファイルは 0600。
    /// 書くときは必ず <c>&lt;名前&gt;.tmp</c> に書いて閉じてから最終の名前へ置き換え、読むときは <c>.tmp</c> を見ない。
    /// </summary>
    internal sealed class LiveLinkFolder
    {
        public const string EnvDir = "YOLUPAINTER_LIVELINK_DIR";
        public const string TmpSuffix = ".tmp";
        public const string PresenceName = "presence.json";
        /// <summary>起きている印がこれより新しければ、スタンドアロンは起きている。</summary>
        public static readonly TimeSpan PresenceFresh = TimeSpan.FromSeconds(6);
        /// <summary>読む返事・起きている印の大きさの上限（壊れた大きなファイルを読み込まない）。</summary>
        public const long MaxReplyBytes = 4L << 20;

        /// <summary>試験が差し替える（null なら環境から決める）。</summary>
        internal static string Override;

        public string Root { get; }
        public string Inbox => Path.Combine(Root, "inbox");
        public string Claimed => Path.Combine(Root, "claimed");
        public string Outbox => Path.Combine(Root, "outbox");
        public string PresencePath => Path.Combine(Root, PresenceName);

        public LiveLinkFolder(string root) { Root = root; }

        /// <summary>この環境のフォルダ（決まらなければ null）。</summary>
        public static LiveLinkFolder Current()
        {
            string root = Override ?? For(OsName(), Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            return root == null ? null : new LiveLinkFolder(root);
        }

        static string OsName()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: return "windows";
                case RuntimePlatform.OSXEditor: return "macos";
                default: return "linux";
            }
        }

        /// <summary>OS と環境の読み方からフォルダを決める（試験できるように引数にした）。<paramref name="localAppDataFallback"/> は Windows で
        /// <c>LOCALAPPDATA</c> が無いときに使う既知のフォルダ。</summary>
        public static string For(string os, Func<string, string> env, string localAppDataFallback = null)
        {
            string Absolute(string key)
            {
                string v = env(key);
                return IsAbsolute(v) ? v : null;
            }
            string over = Absolute(EnvDir);
            if (over != null) return over;
            string baseDir;
            switch (os)
            {
                case "windows":
                    baseDir = Absolute("LOCALAPPDATA") ?? (IsAbsolute(localAppDataFallback) ? localAppDataFallback : null);
                    break;
                case "macos":
                    baseDir = Absolute("HOME") is string home ? Path.Combine(home, "Library", "Application Support") : null;
                    break;
                default:
                    baseDir = Absolute("XDG_DATA_HOME") ?? (Absolute("HOME") is string h ? Path.Combine(h, ".local", "share") : null);
                    break;
            }
            return baseDir == null ? null : Path.Combine(baseDir, "YoluPainter", "LiveLink");
        }

        /// <summary>絶対の道か（/ で始まる・<c>\\server</c> の共有・ドライブの文字 <c>C:\</c>。どの OS の上でも同じに決める）。</summary>
        public static bool IsAbsolute(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path[0] == '/' || path.StartsWith("\\\\", StringComparison.Ordinal)) return true;
            return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        }

        // ───────── 作る・書く ─────────

        /// <summary>フォルダと inbox・claimed・outbox を作る（Unix では作ったフォルダを 0700 に）。前からあるフォルダの権限は変えない。</summary>
        public void Ensure()
        {
            string parent = Path.GetDirectoryName(Root);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            foreach (var dir in new[] { Root, Inbox, Claimed, Outbox })
            {
                if (Directory.Exists(dir)) continue;
                Directory.CreateDirectory(dir);
                Unix.Chmod(dir, Convert.ToInt32("700", 8));
            }
        }

        /// <summary>書いて閉じてから置き換える（<c>&lt;名前&gt;.tmp</c> に書き、閉じてから最終の名前へ 1 回の置き換えで。前の <c>.tmp</c> が残っていれば消してから書く）。
        /// 最終のファイルがあるときは <see cref="File.Replace(string, string, string)"/>（消してから移す 2 段にしない: 間で落ちても最終のファイルが無くならない）。</summary>
        public static void WriteReplacing(string path, string text)
        {
            string tmp = path + TmpSuffix;
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                Unix.Chmod(tmp, Convert.ToInt32("600", 8));
                Place(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        static void Place(string tmp, string path)
        {
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; }
                catch (FileNotFoundException) when (!File.Exists(path)) { } // 置き換える前に、最終のファイルが消えた（読み手が消した）: 移すだけ
            }
            File.Move(tmp, path);
        }

        /// <summary>読む（大きすぎる・読めないときは null）。</summary>
        public static string ReadSmall(string path) => ReadSmall(path, out _);

        /// <summary>読む。<paramref name="tooLarge"/> は、<see cref="MaxReplyBytes"/> を超えて読まなかったとき（読めない・無いときは false で null）。</summary>
        public static string ReadSmall(string path, out bool tooLarge)
        {
            tooLarge = false;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                if (info.Length > MaxReplyBytes) { tooLarge = true; return null; }
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>フォルダの中の .json（<c>.tmp</c> は書きかけなので見ない。名前の Ordinal の順。返事は <see cref="LiveLinkReplies.Poll"/> が書いた順に並べ直す）。無ければ空。</summary>
        public static List<string> JsonFiles(string dir)
        {
            var result = new List<string>();
            if (!Directory.Exists(dir)) return result;
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                    if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) result.Add(f);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        // ───────── 起きている印 ─────────

        /// <summary>起きている印が新しいか（<paramref name="now"/> は UTC）。無い・読めない・古いなら false。</summary>
        public bool StandaloneAwake(DateTime now) => PresenceAge(now) is TimeSpan age && age < PresenceFresh && age > -PresenceFresh;

        /// <summary>起きている印の古さ（無い・読めないなら null）。</summary>
        public TimeSpan? PresenceAge(DateTime now)
        {
            string text = ReadSmall(PresencePath);
            if (text == null) return null;
            try
            {
                var o = JsonReader.Parse(text) as Dictionary<string, object>;
                if (JsonReader.Num(o, "format") != 1) return null;
                string updated = JsonReader.Str(o, "updated");
                if (!TryParseUtc(updated, out var at)) return null;
                return now - at;
            }
            catch (FormatException) { return null; }
        }

        /// <summary>RFC 3339 の UTC（<c>2026-10-07T12:00:00Z</c>。秒の小数・<c>+00:00</c> も受ける）。</summary>
        public static bool TryParseUtc(string text, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrEmpty(text)) return false;
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc)
                && (text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || text.EndsWith("+00:00", StringComparison.Ordinal));
        }

        /// <summary>UTC の RFC 3339（秒まで、<c>Z</c>）。</summary>
        public static string UtcText(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        /// <summary>Unix の chmod（Windows では何もしない。失敗しても止めない: 作ったフォルダの権限が違えば、スタンドアロンが理由を出して読まない）。</summary>
        internal static class Unix
        {
            [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
            static extern int chmod(string path, int mode);

            public static bool Supported => Application.platform != RuntimePlatform.WindowsEditor;

            public static void Chmod(string path, int mode)
            {
                if (!Supported) return;
                try { chmod(path, mode); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
        }
    }
}
