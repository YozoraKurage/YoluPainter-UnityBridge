using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// The user's own library folder (personal settings, <see cref="PainterSettings.LibraryFolder"/>; several projects can point at
    /// the same folder). It is a plain folder of image files: YoluPainter puts resources there as PNG (<see cref="RgbaPng"/>, the
    /// exact pixels), and any PNG or JPEG the user drops in shows up too. Subfolders are listed by their relative paths; symbolic links are skipped. Files are never overwritten:
    /// a new name gets a number, and a file with the very bytes YoluPainter would write is reused instead of a second copy.
    /// </summary>
    internal static class ResourceLibraryFolder
    {
        internal sealed class Item
        {
            public string FileName, Path; public long Length; public DateTime Modified;
            public string Name => System.IO.Path.GetFileNameWithoutExtension(FileName);
        }

        /// <summary>The image files under the folder, by name (an absent folder is empty).</summary>
        public static List<Item> List(string folder)
        {
            var items = new List<Item>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return items;
            foreach (var path in Files(folder))
            {
                if (!ImageFiles.IsImageFile(path)) continue;
                string name = Relative(folder, path);
                var info = new FileInfo(path);
                items.Add(new Item { FileName = name, Path = path, Length = info.Length, Modified = info.LastWriteTimeUtc });
            }
            return items.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Puts a resource into the folder as &lt;name&gt;.png and returns the file name. When the folder already has a file
        /// with exactly these bytes, that file's name is returned and <paramref name="existed"/> is true. Written to a temporary file
        /// first and moved into place, so a half-written file never shows up under the real name.</summary>
        public static string Add(string folder, ImageResource resource, out bool existed)
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            folder = System.IO.Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);
            Resolve(folder, "entry");
            var png = resource.Content.EncodePng();
            string hash = null;
            foreach (var item in List(folder).Where(i => i.Length == png.LongLength))
            {
                hash = hash ?? GenerationStore.Hash(png);
                if (GenerationStore.Hash(File.ReadAllBytes(item.Path)) == hash) { existed = true; return item.FileName; }
            }
            existed = false;
            string stem = SafeStem(resource.Name), name = stem + ".png";
            for (int n = 2; File.Exists(System.IO.Path.Combine(folder, name)); n++) name = stem + " " + n + ".png";
            string target = System.IO.Path.Combine(folder, name), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp~";
            try
            {
                using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { s.Write(png, 0, png.Length); s.Flush(true); }
                File.Move(temp, target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return name;
        }

        /// <summary>The smart material and smart mask files (.ylsmart) under the folder, by name (an absent folder is empty).</summary>
        public static List<Item> ListSmart(string folder)
        {
            var items = new List<Item>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return items;
            foreach (var path in Files(folder))
            {
                if (!path.EndsWith(SmartMaterialFile.Extension, StringComparison.OrdinalIgnoreCase)) continue;
                string name = Relative(folder, path);
                var info = new FileInfo(path);
                items.Add(new Item { FileName = name, Path = path, Length = info.Length, Modified = info.LastWriteTimeUtc });
            }
            return items.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Puts a smart material's file into the folder as &lt;name&gt;.ylsmart and returns the file name; a file with exactly
        /// these bytes is reused (<paramref name="existed"/>). Written to a temporary file first and moved into place.</summary>
        public static string AddSmart(string folder, string name, byte[] bytes, out bool existed, string extension = SmartMaterialFile.Extension)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (extension != SmartMaterialFile.Extension && extension != MaterialExtension) throw new ArgumentException("Unsupported material extension.");
            folder = System.IO.Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);
            Resolve(folder, "entry");
            string hash = null;
            foreach (var item in (extension == MaterialExtension ? ListMaterials(folder) : ListSmart(folder)).Where(i => i.Length == bytes.LongLength))
            {
                hash = hash ?? GenerationStore.Hash(bytes);
                if (GenerationStore.Hash(File.ReadAllBytes(item.Path)) == hash) { existed = true; return item.FileName; }
            }
            existed = false;
            string stem = SafeStem(name), file = stem + extension;
            for (int n = 2; File.Exists(System.IO.Path.Combine(folder, file)); n++) file = stem + " " + n + extension;
            string target = System.IO.Path.Combine(folder, file), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp~";
            try
            {
                using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { s.Write(bytes, 0, bytes.Length); s.Flush(true); }
                File.Move(temp, target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return file;
        }

        public const string MaterialExtension = ".ylmaterial";
        public static List<Item> ListMaterials(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return new List<Item>();
            return Files(folder).Where(p => p.EndsWith(MaterialExtension, StringComparison.OrdinalIgnoreCase)).Select(p => new Item
                { Path = p, FileName = Relative(folder, p), Length = new FileInfo(p).Length, Modified = File.GetLastWriteTimeUtc(p) }).ToList();
        }
        public static byte[] ReadFile(string path)
        {
            if (new FileInfo(path).Length > YlpArchive.MaxEntryBytes) throw new InvalidDataException("The library file exceeds the 512 MiB read budget.");
            return File.ReadAllBytes(path);
        }
        public static string AddFile(string folder, string name, string extension, byte[] bytes)
        {
            if (extension != BrushResourceFile.Extension) throw new ArgumentException("Unsupported library file kind.");
            Directory.CreateDirectory(folder); string stem = SafeStem(name), file = stem + extension;
            for (int n = 2; File.Exists(Resolve(folder, file)); n++) file = stem + " " + n + extension;
            string target = Resolve(folder, file), temp = target + ".tmp~";
            try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); } File.Move(temp, target); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return file;
        }

        public static List<Item> ListBrushes(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return new List<Item>();
            return Files(folder).Where(p => p.EndsWith(BrushResourceFile.Extension, StringComparison.OrdinalIgnoreCase)).Select(p => new Item
                { Path = p, FileName = Relative(folder, p), Length = new FileInfo(p).Length, Modified = File.GetLastWriteTimeUtc(p) }).ToList();
        }

        static string Relative(string folder, string path) => path.Substring(System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');

        // シンボリックリンクを辿らず、置き場の外のファイルを列挙・操作しない。
        static IEnumerable<string> Files(string folder)
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) yield break;
            foreach (string path in Directory.GetFileSystemEntries(System.IO.Path.GetFullPath(folder)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string leaf = System.IO.Path.GetFileName(path);
                if (leaf.StartsWith(".", StringComparison.Ordinal) || leaf.EndsWith("~", StringComparison.Ordinal)) continue;
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                if ((attrs & FileAttributes.Directory) != 0) { foreach (string child in Files(path)) yield return child; }
                else yield return path;
            }
        }

        public static string Resolve(string folder, string relative)
        {
            if (!ResourceOrigin.IsLibraryPath(relative)) throw new ArgumentException("Unsafe library path.", nameof(relative));
            string root = System.IO.Path.GetFullPath(folder), current = root;
            if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("A library folder cannot be a symbolic link.");
            foreach (string part in relative.Split('/'))
            {
                current = System.IO.Path.Combine(current, part);
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A library path cannot pass through a symbolic link.");
            }
            return current;
        }

        public static string Rename(string folder, string relative, string name)
        {
            ImageResource.CheckName(name);
            string old = Resolve(folder, relative);
            string parent = relative.Contains("/") ? relative.Substring(0, relative.LastIndexOf('/') + 1) : "";
            string next = parent + SafeStem(name) + System.IO.Path.GetExtension(relative);
            string target = Resolve(folder, next);
            if (old == target) return relative;
            if (File.Exists(target)) throw new IOException("The library already has a file with that name.");
            File.Move(old, target);
            return next;
        }

        public static void Remove(string folder, string relative) => File.Delete(Resolve(folder, relative));

        /// <summary>A file name from a resource name: characters that file systems refuse become '_', at most 100 characters.</summary>
        internal static string SafeStem(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToArray();
            var chars = (name ?? "").Trim().Select(c => invalid.Contains(c) || c < 0x20 ? '_' : c).ToArray();
            string stem = new string(chars).Trim('.', ' ');
            if (stem.Length > 100) stem = stem.Substring(0, 100);
            return stem.Length == 0 ? "Image" : stem;
        }
    }
}
