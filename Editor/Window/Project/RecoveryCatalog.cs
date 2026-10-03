using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>この Unity プロジェクトで閉じたウィンドウの復旧の一覧。読むだけでは世代も情報も書き換えない。</summary>
    internal static class RecoveryCatalog
    {
        internal const string InfoName = "recovery.json";
        [Serializable] internal sealed class Info
        {
            public string title, projectPath, projectToken;
            public bool unchanged;
        }
        internal sealed class Entry
        {
            public string Root, Generation, Title, Problem;
            public DateTime UpdatedUtc;
            public long Bytes;
        }
        // 試験のあいだだけ独立した一時フォルダへ向ける。既存の Library の checkpoint に触れない。
        internal static string RootOverride;
        internal static string BaseRoot => RootOverride ?? Path.GetFullPath(Path.Combine(PainterSettings.ProjectRoot, "Library", "YoluPainter"));
        internal static string NewRoot() => Path.Combine(BaseRoot, "recovery-" + Guid.NewGuid().ToString("N"));
        internal static bool IsRecoveryRoot(string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string name = Path.GetFileName(full);
            return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(BaseRoot).TrimEnd(Path.DirectorySeparatorChar), PainterSettings.PathComparison)
                && name.StartsWith("recovery-", StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(9), "N", out _)
                && (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0);
        }
        internal static bool IsOpen(string root, TexturePaintWindow except = null) => Resources.FindObjectsOfTypeAll<TexturePaintWindow>()
            .Any(w => w != except && string.Equals(w.RecoveryRoot, root, PainterSettings.PathComparison));
        internal static Info ReadInfo(string root)
        {
            var bytes = GenerationStore.ReadFile(root, InfoName, 64 * 1024);
            return bytes == null ? null : JsonUtility.FromJson<Info>(Encoding.UTF8.GetString(bytes));
        }
        internal static List<Entry> List()
        {
            var result = new List<Entry>();
            if (!Directory.Exists(BaseRoot)) return result;
            foreach (string root in Directory.GetDirectories(BaseRoot, "recovery-*"))
            {
                if (!IsRecoveryRoot(root) || IsOpen(root)) continue;
                var entry = new Entry { Root = root, Title = L.Tr("Untitled project"), UpdatedUtc = Directory.GetLastWriteTimeUtc(root) };
                try
                {
                    entry.Bytes = DirectoryBytes(root);
                    string current = Path.Combine(root, "current");
                    if (!File.Exists(current)) { entry.Problem = L.Tr("No committed checkpoint; only interrupted files remain."); }
                    else
                    {
                        entry.Generation = File.ReadAllText(current).Trim(); entry.UpdatedUtc = File.GetLastWriteTimeUtc(current);
                        var info = ReadInfo(root);
                        if (!string.IsNullOrEmpty(info?.title)) entry.Title = info.title;
                        else entry.Title = L.Tr("Older checkpoint (project name unavailable)");
                    }
                }
                catch (Exception ex) { entry.Problem = ex.Message; }
                result.Add(entry);
            }
            return result.OrderByDescending(e => e.UpdatedUtc).ToList();
        }
        internal static long DirectoryBytes(string root)
        {
            if (!Directory.Exists(root)) return 0;
            long bytes = 0;
            foreach (string path in Directory.GetFileSystemEntries(root))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                bytes = checked(bytes + ((attributes & FileAttributes.Directory) != 0 ? DirectoryBytes(path) : new FileInfo(path).Length));
            }
            return bytes;
        }
        internal static string StorageNotice(long bytes) => bytes <= PainterSettings.RecoveryWarningBytes ? null : L.Tr(
            "Library/YoluPainter uses {0} MiB, above the {1} MiB warning size. Review closed checkpoints in File > Recovery. Nothing was automatically discarded.",
            (bytes / 1048576.0).ToString("0.#"), PainterSettings.RecoveryWarningBytes >> 20);
        internal static void Delete(string root, TexturePaintWindow owner = null)
        {
            if (!IsRecoveryRoot(root)) throw new IOException("Not a recovery folder in this project.");
            if (IsOpen(root, owner)) throw new IOException(L.Tr("This checkpoint belongs to an open window."));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        internal static bool Discard(Entry entry, IPainterDialogs dialogs)
        {
            if (IsOpen(entry.Root)) throw new IOException(L.Tr("This checkpoint belongs to an open window."));
            if (!dialogs.Confirm(L.Tr("Discard recovery checkpoint?"), L.Tr("Discard all recovery generations for {0}? This cannot be undone. The saved .ylp is kept.", entry.Title), L.Tr("Discard checkpoint"), L.Tr("Cancel"))) return false;
            string current = Path.Combine(entry.Root, "current");
            if (File.Exists(current) && File.ReadAllText(current).Trim() != entry.Generation) throw new IOException(L.Tr("The checkpoint changed; refresh the list before discarding it."));
            Delete(entry.Root); return true;
        }
    }
}
