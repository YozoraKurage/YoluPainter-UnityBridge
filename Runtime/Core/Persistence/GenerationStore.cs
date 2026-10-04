using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

namespace Yozolab.YoluPainter.Core.Persistence
{
    /// <summary>Commit の段階計測。呼び出したスレッドだけを測り、各時間は重複しない。</summary>
    public sealed class GenerationTimings
    {
        public double HashMilliseconds, ReadMilliseconds, WriteMilliseconds, FlushMilliseconds, CleanupMilliseconds;
        internal static double Milliseconds(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    }
    public sealed class GenerationSnapshot
    {
        public string Generation { get; internal set; }
        public string Token { get; internal set; }
        public Dictionary<string, byte[]> Files { get; internal set; }
        /// <summary>今回新しく書いた内容のバイト数（manifest・ポインタは含めない）。Load では 0。</summary>
        public long WrittenContentBytes { get; internal set; }
        public int ReusedContentFiles { get; internal set; }
    }
    /// <summary>
    /// Immutable generation directories, verified content hashes, current pointer changed last.
    /// Flush(true) flushes files. Directory fsync/power-loss durability varies by OS and is not claimed.
    /// The lock serializes this tool; external noncooperating writers are detected by hashes, not locked.
    /// File names are flat, or flat inside one texture-set folder <c>sets/&lt;lower-case GUID&gt;/</c> (.ylp format 3) or the resource
    /// folder <c>resources/</c> (format 4); a generation holds a native document at the root or in at least one texture set.
    /// </summary>
    public static class GenerationStore
    {
        [ThreadStatic] static GenerationTimings timing;
        const string FlatManifest = "DOTPAINT-MANIFEST-1", SharedManifest = "DOTPAINT-MANIFEST-2";
        public static GenerationSnapshot Load(string root)
        {
            string pointer = Path.Combine(root, "current");
            if (!File.Exists(pointer)) throw new FileNotFoundException("No committed generation exists.", pointer);
            string generation = File.ReadAllText(pointer, Encoding.UTF8).Trim(); ValidateGeneration(generation);
            string directory = Path.Combine(root, "generations", generation);
            string manifestPath = Path.Combine(directory, "manifest.sha256");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("Current generation has no manifest.");
            byte[] manifest = ReadBounded(manifestPath, 1024 * 1024);
            var files = ParseAndVerify(root, directory, manifest, true);
            return new GenerationSnapshot { Generation = generation, Token = generation + ":" + Hash(manifest), Files = files };
        }

        public static GenerationSnapshot Commit(string root, IDictionary<string, byte[]> files, string expectedToken = null, Action<string> faultInjection = null,
            int? generationsToKeep = null, bool shareContents = false, GenerationTimings timings = null)
        {
            var previousTiming = timing; timing = timings;
            try { return CommitCore(root, files, expectedToken, faultInjection, generationsToKeep, shareContents); }
            finally { timing = previousTiming; }
        }
        static GenerationSnapshot CommitCore(string root, IDictionary<string, byte[]> files, string expectedToken, Action<string> faultInjection,
            int? generationsToKeep, bool shareContents)
        {
            if (generationsToKeep.HasValue && generationsToKeep.Value < 2) throw new ArgumentOutOfRangeException(nameof(generationsToKeep), "Keep at least current and previous.");
            if (files == null || !HasNative(files.Keys)) throw new ArgumentException("A complete native document is required.");
            long total = 0;
            foreach (var entry in files)
            {
                ValidateName(entry.Key); if (entry.Value == null) throw new ArgumentException("Null content."); total = checked(total + entry.Value.LongLength);
            }
            if (total > 768L * 1024 * 1024) throw new InvalidOperationException("Save exceeds the prototype's 768 MiB staging budget.");
            Directory.CreateDirectory(root);
            using (var lockFile = new FileStream(Path.Combine(root, ".save.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                CheckExpected(root, expectedToken);
                string generation = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
                string staging = Path.Combine(root, ".staging-" + generation);
                string generations = Path.Combine(root, "generations");
                Directory.CreateDirectory(staging); Directory.CreateDirectory(generations);
                try
                {
                    long written = 0; int reused = 0;
                    var manifest = new StringBuilder(shareContents ? SharedManifest + "\n" : FlatManifest + "\n");
                    foreach (var entry in files.OrderBy(x => x.Key, StringComparer.Ordinal))
                    {
                        string hash = Hash(entry.Value);
                        string path = shareContents ? ContentPath(root, hash) : Path.Combine(staging, entry.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        if (shareContents && File.Exists(path))
                        {
                            if (new FileInfo(path).Length != entry.Value.LongLength || Hash(ReadBounded(path, entry.Value.LongLength)) != hash)
                                throw new InvalidDataException("Shared recovery content was changed outside this tool.");
                            reused++;
                        }
                        else
                        {
                            if (shareContents)
                            {
                                string pending = Path.Combine(staging, hash + ".pending");
                                WriteDurable(pending, entry.Value); File.Move(pending, path);
                            }
                            else WriteDurable(path, entry.Value);
                            written += entry.Value.LongLength;
                        }
                        manifest.Append(hash).Append(' ').Append(entry.Value.LongLength).Append(' ').Append(entry.Key).Append('\n');
                        faultInjection?.Invoke("file:" + entry.Key);
                    }
                    byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToString());
                    WriteDurable(Path.Combine(staging, "manifest.sha256"), manifestBytes);
                    ParseAndVerify(root, staging, manifestBytes, false);
                    faultInjection?.Invoke("verified");
                    string committed = Path.Combine(generations, generation);
                    Directory.Move(staging, committed);
                    faultInjection?.Invoke("generation-renamed");
                    CheckExpected(root, expectedToken); // includes all hashes of current generation immediately before pointer commit
                    string nextPointer = Path.Combine(root, ".current-" + Guid.NewGuid().ToString("N"));
                    WriteDurable(nextPointer, Encoding.UTF8.GetBytes(generation + "\n"));
                    faultInjection?.Invoke("before-pointer");
                    string current = Path.Combine(root, "current");
                    if (File.Exists(current))
                    {
                        // No delete+move fallback: failing safely is better than a missing current pointer.
                        File.Replace(nextPointer, current, Path.Combine(root, "previous"));
                    }
                    else File.Move(nextPointer, current);
                    faultInjection?.Invoke("after-pointer");
                    var saved = Load(root);
                    saved.WrittenContentBytes = written; saved.ReusedContentFiles = reused;
                    // 保存の確定後だけ整理する。整理の失敗は新しい current を取り消さず、次の保存で再試行する。
                    if (generationsToKeep.HasValue)
                    {
                        long started = Stopwatch.GetTimestamp(); var savedTiming = timing; timing = null;
                        try { Prune(root, generationsToKeep.Value, faultInjection); }
                        catch (Exception) { }
                        finally { timing = savedTiming; if (timing != null) timing.CleanupMilliseconds += GenerationTimings.Milliseconds(started); }
                    }
                    return saved;
                }
                catch { /* Keep staged/orphan generation for diagnosis. Never mutate previous source. */ throw; }
            }
        }
        public static bool HasExternalChange(string root, string expectedToken)
        {
            try { return Load(root).Token != expectedToken; } catch { return true; }
        }
        /// <summary>一覧用の小さな情報だけ読む。正本全体の検証は Load が行う。</summary>
        public static byte[] ReadFile(string root, string name, long maxBytes)
        {
            ValidateName(name);
            string generation = File.ReadAllText(Path.Combine(root, "current"), Encoding.UTF8).Trim(); ValidateGeneration(generation);
            string directory = Path.Combine(root, "generations", generation);
            string[] lines = new UTF8Encoding(false, true).GetString(ReadBounded(Path.Combine(directory, "manifest.sha256"), 1024 * 1024)).Split('\n');
            if (lines[0] != FlatManifest && lines[0] != SharedManifest) throw new InvalidDataException("Unsupported manifest.");
            byte[] result = null;
            foreach (string line in lines.Skip(1).Where(l => l.Length > 0))
            {
                string[] parts = line.Split(' ');
                if (parts.Length != 3 || !IsHash(parts[0]) || !long.TryParse(parts[1], out long length) || length < 0) throw new InvalidDataException("Malformed manifest entry.");
                ValidateName(parts[2]);
                if (parts[2] != name) continue;
                if (result != null || length > maxBytes) throw new InvalidDataException("Recovery information exceeds its budget or is duplicated.");
                result = ReadBounded(lines[0] == SharedManifest ? ContentPath(root, parts[0]) : Path.Combine(directory, name), length);
                if (result.LongLength != length || Hash(result) != parts[0]) throw new InvalidDataException("Recovery information checksum mismatch.");
            }
            return result;
        }
        static void CheckExpected(string root, string expectedToken)
        {
            bool exists = File.Exists(Path.Combine(root, "current"));
            if (expectedToken == null)
            {
                if (exists) throw new IOException("The destination already has a document; implicit overwrite is blocked.");
            }
            else
            {
                const string changed = "Saved generation changed outside this window. Save to a new folder or reopen; local work was not discarded.";
                if (!exists) throw new IOException(changed + " (The current pointer is missing.)");
                string token;
                // 外部の改変で current が読めない（ハッシュ・長さの不一致など）のも「外で変わった」の一種として伝える。
                try { token = Load(root).Token; }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                { throw new IOException(changed + " (" + ex.Message + ")", ex); }
                if (token != expectedToken) throw new IOException(changed);
            }
        }
        static Dictionary<string, byte[]> ParseAndVerify(string root, string directory, byte[] bytes, bool keepBytes)
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            string[] lines = text.Split('\n');
            if (lines.Length < 2 || lines[0] != FlatManifest && lines[0] != SharedManifest) throw new InvalidDataException("Unsupported manifest.");
            bool shared = lines[0] == SharedManifest;
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal); long total = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                string[] parts = lines[i].Split(' ');
                if (parts.Length != 3 || !IsHash(parts[0]) || !long.TryParse(parts[1], out long length) || length < 0 || length > 512L * 1024 * 1024)
                    throw new InvalidDataException("Malformed manifest entry.");
                ValidateName(parts[2]); if (files.ContainsKey(parts[2])) throw new InvalidDataException("Duplicate manifest file.");
                total = checked(total + length); if (total > 768L * 1024 * 1024) throw new InvalidDataException("Generation exceeds read budget.");
                string path = shared ? ContentPath(root, parts[0]) : Path.Combine(directory, parts[2]);
                var info = new FileInfo(path); if (!info.Exists || info.Length != length) throw new InvalidDataException("Generation length mismatch: " + parts[2]);
                byte[] data = ReadBounded(path, length);
                if (!String.Equals(Hash(data), parts[0], StringComparison.Ordinal)) throw new InvalidDataException("Generation checksum mismatch: " + parts[2]);
                files.Add(parts[2], keepBytes ? data : new byte[0]);
            }
            if (!HasNative(files.Keys)) throw new InvalidDataException("Generation missing native source.");
            return files;
        }
        static bool IsHash(string value) => value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        static string ContentPath(string root, string hash) => Path.Combine(root, "contents", hash + ".bin");

        static void Prune(string root, int keep, Action<string> faultInjection)
        {
            var protectedGenerations = new HashSet<string>(StringComparer.Ordinal);
            foreach (string pointer in new[] { "current", "previous" })
            {
                string path = Path.Combine(root, pointer);
                if (!File.Exists(path)) continue;
                string generation = File.ReadAllText(path, Encoding.UTF8).Trim(); ValidateGeneration(generation);
                protectedGenerations.Add(generation);
            }
            string generations = Path.Combine(root, "generations");
            var candidates = Directory.GetDirectories(generations).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();
            var retained = new HashSet<string>(protectedGenerations, StringComparer.Ordinal);
            foreach (string directory in candidates)
            {
                string name = Path.GetFileName(directory); ValidateGeneration(name);
                if (retained.Count < keep) retained.Add(name);
            }
            foreach (string directory in candidates.Where(d => !retained.Contains(Path.GetFileName(d))))
                try
                {
                    faultInjection?.Invoke("prune-generation:" + Path.GetFileName(directory));
                    Directory.Delete(directory, true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            PruneContents(root, faultInjection);
        }

        static void PruneContents(string root, Action<string> faultInjection)
        {
            string contents = Path.Combine(root, "contents"); if (!Directory.Exists(contents)) return;
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            // 診断用に残る作業中の世代も守る。manifest が無い/読めない世代があれば共有内容を整理しない。
            foreach (string directory in Directory.GetDirectories(Path.Combine(root, "generations")).Concat(Directory.GetDirectories(root, ".staging-*")))
            {
                string manifest = Path.Combine(directory, "manifest.sha256"); if (!File.Exists(manifest)) return;
                string[] lines = new UTF8Encoding(false, true).GetString(ReadBounded(manifest, 1024 * 1024)).Split('\n');
                if (lines[0] == FlatManifest) continue;
                if (lines[0] != SharedManifest) return;
                foreach (string line in lines.Skip(1).Where(l => l.Length > 0))
                {
                    var parts = line.Split(' ');
                    if (parts.Length != 3 || !IsHash(parts[0])) return;
                    referenced.Add(parts[0]);
                }
            }
            foreach (string path in Directory.GetFiles(contents, "*.bin"))
            {
                string hash = Path.GetFileNameWithoutExtension(path);
                if (!IsHash(hash) || referenced.Contains(hash)) continue;
                try { faultInjection?.Invoke("prune-content:" + hash); File.Delete(path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
        static byte[] ReadBounded(string path, long max)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (s.Length > max || s.Length > int.MaxValue) throw new InvalidDataException("File exceeds allowed size.");
                    byte[] result = new byte[(int)s.Length]; int offset = 0;
                    while (offset < result.Length) { int n = s.Read(result, offset, result.Length - offset); if (n == 0) throw new EndOfStreamException(); offset += n; }
                    if (s.ReadByte() != -1) throw new IOException("File changed while reading."); return result;
                }
            }
            finally { if (timing != null) timing.ReadMilliseconds += GenerationTimings.Milliseconds(started); }
        }
        static void WriteDurable(string path, byte[] bytes)
        {
            long started = Stopwatch.GetTimestamp();
            using (var s = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                s.Write(bytes, 0, bytes.Length);
                if (timing != null) timing.WriteMilliseconds += GenerationTimings.Milliseconds(started);
                started = Stopwatch.GetTimestamp(); s.Flush(true);
                if (timing != null) timing.FlushMilliseconds += GenerationTimings.Milliseconds(started);
            }
        }
        public static string Hash(byte[] data)
        {
            long started = Stopwatch.GetTimestamp();
            try { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
            finally { if (timing != null) timing.HashMilliseconds += GenerationTimings.Milliseconds(started); }
        }
        const string NativeName = "document.utpaint";
        static bool HasNative(IEnumerable<string> names) => names.Any(n => n == NativeName || YlpFormat.TrySplitSetEntry(n, out _, out var leaf) && leaf == NativeName);
        static void ValidateName(string value)
        {
            string leaf = value != null && YlpFormat.TrySplitSetEntry(value, out _, out var inSet) ? inSet
                : value != null && value.StartsWith(YlpArchive.ResourceFolder, StringComparison.Ordinal) ? value.Substring(YlpArchive.ResourceFolder.Length) : value;
            if (String.IsNullOrEmpty(leaf) || leaf.Length > 80 || leaf == "manifest.sha256" || leaf.Any(c => !(Char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')) || leaf.Contains("..")) throw new InvalidDataException("Unsafe generation filename.");
        }
        static void ValidateGeneration(string value)
        { if (String.IsNullOrEmpty(value) || value.Length > 80 || value.Any(c => !(Char.IsLetterOrDigit(c) || c == '-'))) throw new InvalidDataException("Unsafe generation pointer."); }
    }
}
