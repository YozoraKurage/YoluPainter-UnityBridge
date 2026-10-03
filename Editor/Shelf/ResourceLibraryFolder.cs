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
    /// exact pixels), and any PNG or JPEG the user drops in shows up too. Only the top level is listed. Files are never overwritten:
    /// a new name gets a number, and a file with the very bytes YoluPainter would write is reused instead of a second copy.
    /// </summary>
    internal static class ResourceLibraryFolder
    {
        internal sealed class Item
        {
            public string FileName, Path; public long Length; public DateTime Modified;
            public string Name => System.IO.Path.GetFileNameWithoutExtension(FileName);
        }

        /// <summary>The image files at the top of the folder, by name (an absent folder is empty).</summary>
        public static List<Item> List(string folder)
        {
            var items = new List<Item>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return items;
            foreach (var path in Directory.GetFiles(folder))
            {
                string name = System.IO.Path.GetFileName(path);
                if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal) || !ImageFiles.IsImageFile(path)) continue;
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
            Directory.CreateDirectory(folder);
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

        /// <summary>The smart material and smart mask files (.ylsmart) at the top of the folder, by name (an absent folder is empty).</summary>
        public static List<Item> ListSmart(string folder)
        {
            var items = new List<Item>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return items;
            foreach (var path in Directory.GetFiles(folder))
            {
                string name = System.IO.Path.GetFileName(path);
                if (name.StartsWith(".", StringComparison.Ordinal) || !name.EndsWith(SmartMaterialFile.Extension, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(path);
                items.Add(new Item { FileName = name, Path = path, Length = info.Length, Modified = info.LastWriteTimeUtc });
            }
            return items.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Puts a smart material's file into the folder as &lt;name&gt;.ylsmart and returns the file name; a file with exactly
        /// these bytes is reused (<paramref name="existed"/>). Written to a temporary file first and moved into place.</summary>
        public static string AddSmart(string folder, string name, byte[] bytes, out bool existed)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            Directory.CreateDirectory(folder);
            string hash = null;
            foreach (var item in ListSmart(folder).Where(i => i.Length == bytes.LongLength))
            {
                hash = hash ?? GenerationStore.Hash(bytes);
                if (GenerationStore.Hash(File.ReadAllBytes(item.Path)) == hash) { existed = true; return item.FileName; }
            }
            existed = false;
            string stem = SafeStem(name), file = stem + SmartMaterialFile.Extension;
            for (int n = 2; File.Exists(System.IO.Path.Combine(folder, file)); n++) file = stem + " " + n + SmartMaterialFile.Extension;
            string target = System.IO.Path.Combine(folder, file), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp~";
            try
            {
                using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { s.Write(bytes, 0, bytes.Length); s.Flush(true); }
                File.Move(temp, target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return file;
        }

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
