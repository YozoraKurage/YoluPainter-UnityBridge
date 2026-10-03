using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Yozolab.YoluPainter.Core.Persistence
{
    public sealed class GenerationSnapshot
    {
        public string Generation { get; internal set; }
        public string Token { get; internal set; }
        public Dictionary<string, byte[]> Files { get; internal set; }
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
        public static GenerationSnapshot Load(string root)
        {
            string pointer = Path.Combine(root, "current");
            if (!File.Exists(pointer)) throw new FileNotFoundException("No committed generation exists.", pointer);
            string generation = File.ReadAllText(pointer, Encoding.UTF8).Trim(); ValidateGeneration(generation);
            string directory = Path.Combine(root, "generations", generation);
            string manifestPath = Path.Combine(directory, "manifest.sha256");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("Current generation has no manifest.");
            byte[] manifest = ReadBounded(manifestPath, 1024 * 1024);
            var files = ParseAndVerify(directory, manifest, true);
            return new GenerationSnapshot { Generation = generation, Token = generation + ":" + Hash(manifest), Files = files };
        }

        public static GenerationSnapshot Commit(string root, IDictionary<string, byte[]> files, string expectedToken = null, Action<string> faultInjection = null)
        {
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
                    var manifest = new StringBuilder("DOTPAINT-MANIFEST-1\n");
                    foreach (var entry in files.OrderBy(x => x.Key, StringComparer.Ordinal))
                    {
                        string path = Path.Combine(staging, entry.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        WriteDurable(path, entry.Value);
                        manifest.Append(Hash(entry.Value)).Append(' ').Append(entry.Value.LongLength).Append(' ').Append(entry.Key).Append('\n');
                        faultInjection?.Invoke("file:" + entry.Key);
                    }
                    byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToString());
                    WriteDurable(Path.Combine(staging, "manifest.sha256"), manifestBytes);
                    ParseAndVerify(staging, manifestBytes, false);
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
                    return Load(root);
                }
                catch { /* Keep staged/orphan generation for diagnosis. Never mutate previous source. */ throw; }
            }
        }
        public static bool HasExternalChange(string root, string expectedToken)
        {
            try { return Load(root).Token != expectedToken; } catch { return true; }
        }
        static void CheckExpected(string root, string expectedToken)
        {
            bool exists = File.Exists(Path.Combine(root, "current"));
            if (expectedToken == null)
            {
                if (exists) throw new IOException("The destination already has a document. Open it or choose a new folder; implicit overwrite is blocked.");
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
        static Dictionary<string, byte[]> ParseAndVerify(string directory, byte[] bytes, bool keepBytes)
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            string[] lines = text.Split('\n');
            if (lines.Length < 2 || lines[0] != "DOTPAINT-MANIFEST-1") throw new InvalidDataException("Unsupported manifest.");
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal); long total = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                string[] parts = lines[i].Split(' ');
                if (parts.Length != 3 || parts[0].Length != 64 || !long.TryParse(parts[1], out long length) || length < 0 || length > 512L * 1024 * 1024)
                    throw new InvalidDataException("Malformed manifest entry.");
                ValidateName(parts[2]); if (files.ContainsKey(parts[2])) throw new InvalidDataException("Duplicate manifest file.");
                total = checked(total + length); if (total > 768L * 1024 * 1024) throw new InvalidDataException("Generation exceeds read budget.");
                string path = Path.Combine(directory, parts[2]);
                var info = new FileInfo(path); if (!info.Exists || info.Length != length) throw new InvalidDataException("Generation length mismatch: " + parts[2]);
                byte[] data = ReadBounded(path, length);
                if (!String.Equals(Hash(data), parts[0], StringComparison.Ordinal)) throw new InvalidDataException("Generation checksum mismatch: " + parts[2]);
                files.Add(parts[2], keepBytes ? data : new byte[0]);
            }
            if (!HasNative(files.Keys)) throw new InvalidDataException("Generation missing native source.");
            return files;
        }
        static byte[] ReadBounded(string path, long max)
        {
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (s.Length > max || s.Length > int.MaxValue) throw new InvalidDataException("File exceeds allowed size.");
                byte[] result = new byte[(int)s.Length]; int offset = 0;
                while (offset < result.Length) { int n = s.Read(result, offset, result.Length - offset); if (n == 0) throw new EndOfStreamException(); offset += n; }
                if (s.ReadByte() != -1) throw new IOException("File changed while reading."); return result;
            }
        }
        static void WriteDurable(string path, byte[] bytes)
        { using (var s = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { s.Write(bytes, 0, bytes.Length); s.Flush(true); } }
        public static string Hash(byte[] data)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
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
