using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Yozolab.YoluPainter.Core.Persistence
{
    public sealed class YlpSnapshot
    {
        public string Path { get; internal set; }
        /// <summary>保存・読み込みした時点のファイルの印（SHA-256、長さ、更新時刻）。外部改変の検出に使う。</summary>
        public string Token { get; internal set; }
        public Dictionary<string, byte[]> Files { get; internal set; }
        /// <summary>保存で退避した直前の版のパス。退避しなかったら null。</summary>
        public string Backup { get; internal set; }
    }

    /// <summary>
    /// .ylp の保存と読み込み。保存はメモリ上で作って読み直して確かめ、同じフォルダの一時ファイル（名前が ~ で終わるので
    /// Unity は取り込まない）に書いて Flush(true) し、読み直して確かめ、直前に外部改変がないことをもう一度確かめてから
    /// 置き換える。置き換えは最後の 1 回だけで、途中で失敗しても元のファイルはそのまま。直前の版は
    /// &lt;名前&gt;.ylp-backups~/ に退避し、保持する数は呼び出し側（設定）が決める（既定はすべて残す）。
    /// ディレクトリの fsync と電源断での保証は OS 次第で、主張しない。
    /// </summary>
    public static class YlpStore
    {
        /// <summary>保持数: すべて残す。</summary>
        public const int KeepAllBackups = -1;
        const string ExternalChange = "The file changed outside this window. Save As to another file or reopen it; local work was not discarded.";

        public static string BackupFolder(string path) => path + "-backups~";

        public static YlpSnapshot Load(string path)
        {
            byte[] data = ReadBounded(path);
            var files = YlpArchive.Read(data);
            return new YlpSnapshot { Path = path, Token = TokenOf(path, data), Files = files };
        }

        /// <param name="expectedToken">上書きする既存ファイルの、開いた/保存した時点の印。null なら新しいファイルとして保存する
        /// （既にあれば <paramref name="allowOverwrite"/> が要る）。</param>
        /// <param name="backupsToKeep">退避した版をいくつ残すか。<see cref="KeepAllBackups"/> ですべて、0 で退避しない。</param>
        public static YlpSnapshot Save(string path, IDictionary<string, byte[]> files, string expectedToken, bool allowOverwrite, int backupsToKeep = KeepAllBackups, Action<string> faultInjection = null)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(YlpArchive.Extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A YoluPainter file must end with " + YlpArchive.Extension + ".");
            if (backupsToKeep < KeepAllBackups) throw new ArgumentOutOfRangeException(nameof(backupsToKeep));
            path = System.IO.Path.GetFullPath(path);
            byte[] data = YlpArchive.Write(files);
            YlpArchive.Read(data); // 書いたものが読めることを、ファイルに触る前に確かめる
            faultInjection?.Invoke("built");
            string directory = System.IO.Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            using (new FileStream(path + ".lock~", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                CheckExpected(path, expectedToken, allowOverwrite);
                string temp = path + ".saving-" + Guid.NewGuid().ToString("N") + "~";
                try
                {
                    using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { s.Write(data, 0, data.Length); s.Flush(true); }
                    faultInjection?.Invoke("temp-written");
                    byte[] reread = ReadBounded(temp);
                    if (GenerationStore.Hash(reread) != GenerationStore.Hash(data)) throw new IOException("The temporary file did not read back identically; nothing was replaced.");
                    faultInjection?.Invoke("verified");
                    CheckExpected(path, expectedToken, allowOverwrite); // 置き換える直前にもう一度
                    faultInjection?.Invoke("before-replace");
                    string backup = null;
                    if (File.Exists(path))
                    {
                        if (backupsToKeep != 0)
                        {
                            string folder = BackupFolder(path); Directory.CreateDirectory(folder);
                            backup = BackupName(folder, System.IO.Path.GetFileNameWithoutExtension(path), DateTime.UtcNow);
                            File.Replace(temp, path, backup);
                        }
                        else File.Replace(temp, path, null);
                    }
                    else File.Move(temp, path);
                    faultInjection?.Invoke("after-replace");
                    if (backup != null && backupsToKeep > 0) Prune(path, backupsToKeep, backup);
                    return new YlpSnapshot { Path = path, Token = TokenOf(path, data), Files = new Dictionary<string, byte[]>(files, StringComparer.Ordinal), Backup = backup };
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }

        /// <summary>開いた/保存した時点から変わったか。長さと更新時刻が同じなら中身は読まない（窓の定期確認用。置き換え直前の
        /// 確かめは常にハッシュまで見る）。</summary>
        public static bool HasExternalChange(string path, string token)
        {
            try
            {
                if (!TryParseToken(token, out string hash, out long length, out long ticks)) return true;
                var info = new FileInfo(path);
                if (!info.Exists) return true;
                if (info.Length == length && info.LastWriteTimeUtc.Ticks == ticks) return false;
                return GenerationStore.Hash(ReadBounded(path)) != hash;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) { return true; }
        }

        /// <summary>保存が退避した版（新しい順）。名前が「&lt;名前&gt;-&lt;UTC の時刻&gt;.ylp」の形のものだけで、利用者が名前を変えて
        /// 残したファイルは含めない（整理の対象にもしない）。</summary>
        public static IReadOnlyList<string> Backups(string path)
        {
            string folder = BackupFolder(System.IO.Path.GetFullPath(path));
            if (!Directory.Exists(folder)) return new string[0];
            var pattern = new Regex("^" + Regex.Escape(System.IO.Path.GetFileNameWithoutExtension(path)) + @"-\d{8}T\d{9}Z_*" + Regex.Escape(YlpArchive.Extension) + "$", RegexOptions.CultureInvariant);
            return Directory.GetFiles(folder, "*" + YlpArchive.Extension)
                .Where(f => pattern.IsMatch(System.IO.Path.GetFileName(f)))
                .OrderByDescending(f => System.IO.Path.GetFileName(f).Replace("_", ""), StringComparer.Ordinal)
                .ThenByDescending(f => System.IO.Path.GetFileName(f).Length).ToList();
        }

        /// <summary>退避の名前「&lt;名前&gt;-&lt;UTC の時刻（ミリ秒）&gt;.ylp」。同じミリ秒の版が既にあれば、そのうちいちばん多い下線より 1 つ多い
        /// 下線を付ける（整理で古い版を消した後に同じ名前を使い直すと、新しい版が古い版より前に並んでしまうため）。</summary>
        public static string BackupName(string folder, string stem, DateTime utc)
        {
            string stamp = stem + "-" + utc.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            int underscores = -1;
            if (Directory.Exists(folder))
                foreach (var file in Directory.GetFiles(folder, stamp + "*" + YlpArchive.Extension))
                {
                    string name = System.IO.Path.GetFileName(file), middle = name.Substring(stamp.Length, name.Length - stamp.Length - YlpArchive.Extension.Length);
                    if (middle.All(c => c == '_')) underscores = Math.Max(underscores, middle.Length);
                }
            return System.IO.Path.Combine(folder, stamp + new string('_', underscores + 1) + YlpArchive.Extension);
        }

        /// <summary>保持数を超えた古い版を消す。いま退避した版は数に入れるが、決して消さない。</summary>
        static void Prune(string path, int keep, string justMade)
        {
            var backups = Backups(path).Where(b => !string.Equals(b, justMade, StringComparison.Ordinal)).ToList();
            foreach (var old in backups.Skip(Math.Max(0, keep - 1))) File.Delete(old);
        }

        static void CheckExpected(string path, string expectedToken, bool allowOverwrite)
        {
            bool exists = File.Exists(path);
            if (expectedToken == null)
            {
                if (exists && !allowOverwrite) throw new IOException("The destination file already exists; implicit overwrite is blocked.");
                return;
            }
            if (!exists) throw new IOException(ExternalChange + " (The file is missing.)");
            if (!TryParseToken(expectedToken, out string hash, out _, out _)) throw new ArgumentException("Malformed file token.");
            string actual;
            try { actual = GenerationStore.Hash(ReadBounded(path)); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) { throw new IOException(ExternalChange + " (" + ex.Message + ")", ex); }
            if (actual != hash) throw new IOException(ExternalChange);
        }

        static string TokenOf(string path, byte[] data)
        {
            var info = new FileInfo(path);
            return GenerationStore.Hash(data) + ":" + data.LongLength + ":" + info.LastWriteTimeUtc.Ticks;
        }
        static bool TryParseToken(string token, out string hash, out long length, out long ticks)
        {
            hash = null; length = ticks = 0;
            var parts = token?.Split(':');
            if (parts == null || parts.Length != 3 || parts[0].Length != 64) return false;
            hash = parts[0];
            return long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out length) && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ticks);
        }

        static byte[] ReadBounded(string path)
        {
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (s.Length > YlpArchive.MaxTotalBytes + 64L * 1024 * 1024) throw new InvalidDataException("The file exceeds the read budget.");
                var result = new byte[(int)s.Length]; int offset = 0;
                while (offset < result.Length) { int n = s.Read(result, offset, result.Length - offset); if (n == 0) throw new EndOfStreamException(); offset += n; }
                if (s.ReadByte() != -1) throw new IOException("The file changed while reading.");
                return result;
            }
        }
    }
}
