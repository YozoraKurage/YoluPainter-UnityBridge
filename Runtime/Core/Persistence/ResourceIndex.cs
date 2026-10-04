using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Core.Persistence
{
    /// <summary>One resource as resources.json lists it (an image's pixels are the entry <see cref="ResourceIndex.ContentEntry"/>, a smart
    /// material's file the entry <see cref="ResourceIndex.SmartEntry"/>).</summary>
    public sealed class YlpResourceEntry
    {
        public Guid Id { get; }
        public ResourceKind Kind { get; }
        public string Name { get; }
        /// <summary>An image: the content hash (<see cref="ImageContent.ComputeHash"/>), which names the PNG entry. A smart material or smart
        /// mask: the SHA-256 of its file, which names the .ylsmart entry.</summary>
        public string Content { get; }
        /// <summary>An image's size (0 for a smart material or smart mask).</summary>
        public int Width { get; }
        public int Height { get; }
        /// <summary>A smart material's or smart mask's file length in bytes (-1 for an image).</summary>
        public long Length { get; }
        public ResourceColorSpace ColorSpace { get; }
        public ResourceOrigin Origin { get; }
        public bool IsSmart => Kind == ResourceKind.SmartMaterial || Kind == ResourceKind.SmartMask || Kind == ResourceKind.Material;
        public bool IsFile => IsSmart || Kind == ResourceKind.Brush;
        public YlpResourceEntry(Guid id, ResourceKind kind, string name, string content, int width, int height, ResourceColorSpace colorSpace, ResourceOrigin origin)
        {
            if (id == Guid.Empty) throw new ArgumentException("A resource needs an ID.", nameof(id));
            if (kind != ResourceKind.Image) throw new ArgumentOutOfRangeException(nameof(kind), "Smart materials and smart masks are made with the smart constructor.");
            ImageResource.CheckName(name);
            if (!ImageContent.IsHash(content)) throw new ArgumentException("A content hash is 64 lower-case hex digits.", nameof(content));
            if (width < 1 || height < 1 || width > ImageContent.MaxSide || height > ImageContent.MaxSide) throw new ArgumentOutOfRangeException(nameof(width), "An image is 1–" + ImageContent.MaxSide + " pixels on each side.");
            if (!Enum.IsDefined(typeof(ResourceColorSpace), colorSpace)) throw new ArgumentOutOfRangeException(nameof(colorSpace));
            Id = id; Kind = kind; Name = name; Content = content; Width = width; Height = height; Length = -1; ColorSpace = colorSpace; Origin = origin ?? ResourceOrigin.None;
        }
        /// <summary>A smart material or smart mask: its file's SHA-256 and length (format 5).</summary>
        public YlpResourceEntry(Guid id, ResourceKind kind, string name, string sha256, long length, ResourceOrigin origin)
        {
            if (id == Guid.Empty) throw new ArgumentException("A resource needs an ID.", nameof(id));
            if (kind != ResourceKind.SmartMaterial && kind != ResourceKind.SmartMask && kind != ResourceKind.Material && kind != ResourceKind.Brush) throw new ArgumentOutOfRangeException(nameof(kind), "Images are made with the image constructor.");
            ImageResource.CheckName(name);
            if (!ImageContent.IsHash(sha256)) throw new ArgumentException("A file hash is 64 lower-case hex digits.", nameof(sha256));
            if (length < 1 || length > YlpArchive.MaxEntryBytes) throw new ArgumentOutOfRangeException(nameof(length), "A smart material file is 1–" + YlpArchive.MaxEntryBytes + " bytes.");
            Id = id; Kind = kind; Name = name; Content = sha256; Length = length; ColorSpace = ResourceColorSpace.Unspecified; Origin = origin ?? ResourceOrigin.None;
        }
    }

    /// <summary>
    /// The project's resources in a .ylp (format 4, Documentation~/YLP_FORMAT.md): <c>resources.json</c> at the root lists them
    /// (ID, kind, name, content hash, size, colour space, origin) and each distinct content is one PNG <c>resources/&lt;hash&gt;.png</c>
    /// (<see cref="RgbaPng"/>; the hash is of the decoded pixels, so the name says what the pixels must be). Both are source entries:
    /// a listed content that is missing, does not decode, has another size or other pixels refuses the whole file (the copy is the
    /// only place the pixels are guaranteed to exist). The recovery checkpoint uses the same entries.
    /// </summary>
    public static class ResourceIndex
    {
        /// <summary>名前・出どころを固定し、不変の画素とスマートファイルを共有する保存用の写し。</summary>
        public sealed class Snapshot
        {
            internal YlpResourceEntry[] Entries;
            internal KeyValuePair<string, ImageContent>[] Images;
            internal KeyValuePair<string, byte[]>[] Smart;
        }
        public static Snapshot Capture(ProjectResources resources) => new Snapshot
        {
            Entries = resources.Images.Select(r => new YlpResourceEntry(r.Id, r.Kind, r.Name, r.ContentHash, r.Width, r.Height, r.ColorSpace, r.Origin))
                .Concat(resources.Smart.Select(r => new YlpResourceEntry(r.Id, KindOf(r.Kind), r.Name, r.Hash, r.Length, r.Origin))).ToArray(),
            Images = resources.Images.Select(r => new KeyValuePair<string, ImageContent>(ContentEntry(r.ContentHash), r.Content)).ToArray(),
            Smart = resources.Smart.Select(r => new KeyValuePair<string, byte[]>(SmartEntry(r.Hash), r.Bytes)).ToArray()
        };
        public static void AddTo(IDictionary<string, byte[]> files, Snapshot snapshot)
        {
            if (snapshot.Entries.Length == 0) return;
            files[EntryName] = Write(snapshot.Entries);
            foreach (var image in snapshot.Images) files[image.Key] = image.Value.EncodePng();
            foreach (var smart in snapshot.Smart) files[smart.Key] = smart.Value;
        }
        public const string EntryName = "resources.json";
        public const string Folder = "resources/";
        public const int MaxBytes = 1024 * 1024;

        public static string ContentEntry(string hash) => Folder + hash + ".png";
        /// <summary>A smart material's or smart mask's file in the .ylp (format 5): resources/&lt;SHA-256 of the file&gt;.ylsmart.</summary>
        public static string FileEntry(YlpResourceEntry entry) => entry.Kind == ResourceKind.Brush ? BrushEntry(entry.Content) : SmartEntry(entry.Content);
        public static string BrushEntry(string hash) => Folder + hash + BrushResourceFile.Extension;
        public static bool TryParseBrushEntry(string name, out string hash)
        {
            hash = null; string suffix = BrushResourceFile.Extension;
            if (name == null || !name.StartsWith(Folder, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal) || name.Length != Folder.Length + 64 + suffix.Length) return false;
            string h = name.Substring(Folder.Length, 64); if (!ImageContent.IsHash(h)) return false; hash = h; return true;
        }
        public static string SmartEntry(string hash) => Folder + hash + SmartMaterialFile.Extension;

        /// <summary>resources/&lt;64 lower-case hex&gt;.ylsmart → the hash.</summary>
        public static bool TryParseSmartEntry(string name, out string hash)
        {
            hash = null;
            string suffix = SmartMaterialFile.Extension;
            if (name == null || !name.StartsWith(Folder, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal) || name.Length != Folder.Length + 64 + suffix.Length) return false;
            string h = name.Substring(Folder.Length, 64);
            if (!ImageContent.IsHash(h)) return false;
            hash = h; return true;
        }

        /// <summary>resources/&lt;64 lower-case hex&gt;.png → the hash.</summary>
        public static bool TryParseContentEntry(string name, out string hash)
        {
            hash = null;
            if (name == null || !name.StartsWith(Folder, StringComparison.Ordinal) || !name.EndsWith(".png", StringComparison.Ordinal) || name.Length != Folder.Length + 64 + 4) return false;
            string h = name.Substring(Folder.Length, 64);
            if (!ImageContent.IsHash(h)) return false;
            hash = h; return true;
        }

        /// <summary>Adds resources.json and one PNG per distinct content to <paramref name="files"/>. Nothing is added for a project
        /// without resources (a file without resources.json has none).</summary>
        public static void AddTo(IDictionary<string, byte[]> files, ProjectResources resources)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            if (resources == null || resources.Count == 0) return;
            files[EntryName] = Write(resources.Images.Select(r => new YlpResourceEntry(r.Id, r.Kind, r.Name, r.ContentHash, r.Width, r.Height, r.ColorSpace, r.Origin))
                .Concat(resources.Smart.Select(r => new YlpResourceEntry(r.Id, r.ResourceKind, r.Name, r.Hash, r.Length, r.Origin)))
                .Concat(resources.Brushes.Select(r => new YlpResourceEntry(r.Id, ResourceKind.Brush, r.Name, r.Hash, r.Bytes.LongLength, r.Origin))));
            foreach (var image in resources.Images) files[ContentEntry(image.ContentHash)] = image.Content.EncodePng();
            foreach (var brush in resources.Brushes) files[BrushEntry(brush.Hash)] = brush.FileBytes();
            foreach (var smart in resources.Smart) files[SmartEntry(smart.Hash)] = smart.Bytes; // 読んだ・作ったバイト列をそのまま
        }

        public static ResourceKind KindOf(SmartKind kind) => kind == SmartKind.Mask ? ResourceKind.SmartMask : ResourceKind.SmartMaterial;

        /// <summary>
        /// Reads the resources of an opened file (<see cref="YlpOpened.Resources"/> and its entries) into a new collection whose
        /// budget is unlimited (the caller applies its own and tells when the file already exceeds it). Every PNG is decoded and its
        /// pixels must hash to its name and have the listed size; anything else is an InvalidDataException naming the resource.
        /// </summary>
        public static ProjectResources Load(IReadOnlyDictionary<string, byte[]> files, IReadOnlyList<YlpResourceEntry> entries)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            var result = new ProjectResources { BudgetBytes = long.MaxValue, ArchiveBudgetBytes = long.MaxValue };
            if (entries == null) return result;
            var decoded = new Dictionary<string, ImageContent>(StringComparer.Ordinal);
            var smart = new Dictionary<string, SmartMaterial>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry.IsFile)
                {
                    string name = FileEntry(entry);
                    if (!files.TryGetValue(name, out var file)) throw new InvalidDataException("Smart material \"" + entry.Name + "\" has no file (" + name + ").");
                    if (file.LongLength != entry.Length || GenerationStore.Hash(file) != entry.Content) throw new InvalidDataException("Smart material \"" + entry.Name + "\" (" + name + ") is not the file resources.json lists (its length or SHA-256 differs).");
                    if (entry.Kind == ResourceKind.Brush) { result.AddBrush(entry.Name, file, entry.Origin, out _, entry.Id, restore: true); continue; }
                    if (!smart.TryGetValue(entry.Content, out var material))
                    {
                        try { material = SmartMaterialFile.Read(file); }
                        catch (InvalidDataException ex) { throw new InvalidDataException("Smart material \"" + entry.Name + "\" (" + name + ") is broken: " + ex.Message, ex); }
                        smart.Add(entry.Content, material);
                    }
                    if (KindOf(material.Kind) != entry.Kind && !(entry.Kind == ResourceKind.Material && material.Kind == SmartKind.Material)) throw new InvalidDataException("Smart material \"" + entry.Name + "\" is listed as a " + Words(entry.Kind) + " but its file is a " + Words(KindOf(material.Kind)) + ".");
                    result.RestoreSmart(entry.Id, entry.Name, file, material, entry.Origin, entry.Kind);
                    continue;
                }
                if (!decoded.TryGetValue(entry.Content, out var content))
                {
                    if (!files.TryGetValue(ContentEntry(entry.Content), out var png)) throw new InvalidDataException("Resource \"" + entry.Name + "\" has no pixels (" + ContentEntry(entry.Content) + ").");
                    try { content = ImageContent.FromPng(png, entry.Content); }
                    catch (Exception ex) when (ex is InvalidDataException || ex is ResourceRefusedException)
                    { throw new InvalidDataException("Resource \"" + entry.Name + "\" (" + ContentEntry(entry.Content) + ") is broken: " + ex.Message, ex); }
                    decoded.Add(entry.Content, content);
                }
                if (content.Width != entry.Width || content.Height != entry.Height)
                    throw new InvalidDataException("Resource \"" + entry.Name + "\" is listed as " + entry.Width + " × " + entry.Height + " but its pixels are " + content.Width + " × " + content.Height + ".");
                result.Restore(entry.Id, entry.Name, content, entry.Origin, entry.ColorSpace);
            }
            return result;
        }

        // ───────── resources.json ─────────

        public static byte[] Write(IEnumerable<YlpResourceEntry> entries)
        {
            var list = (entries ?? throw new ArgumentNullException(nameof(entries))).ToList();
            if (list.Count > ProjectResources.MaxResources) throw new ArgumentException("A project holds at most " + ProjectResources.MaxResources + " resources.", nameof(entries));
            if (list.Select(e => e.Id).Distinct().Count() != list.Count) throw new ArgumentException("Two resources have the same ID.", nameof(entries));
            var s = new StringBuilder("{\n  \"resources\": [");
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                s.Append(i == 0 ? "\n" : ",\n").Append("    { \"id\": ").Append(YlpFormat.Quote(e.Id.ToString("D"))).Append(", \"kind\": \"").Append(KindName(e.Kind)).Append("\", \"name\": ").Append(YlpFormat.Quote(e.Name))
                 .Append(", \"content\": \"").Append(e.Content).Append('"');
                if (e.IsFile) s.Append(", \"length\": ").Append(e.Length.ToString(CultureInfo.InvariantCulture)).Append(",\n      \"origin\": ");
                else s.Append(", \"width\": ").Append(e.Width.ToString(CultureInfo.InvariantCulture))
                      .Append(", \"height\": ").Append(e.Height.ToString(CultureInfo.InvariantCulture)).Append(", \"colorSpace\": \"").Append(ColorSpaceName(e.ColorSpace)).Append("\",\n      \"origin\": ");
                AppendOrigin(s, e.Origin);
                s.Append(" }");
            }
            s.Append(list.Count > 0 ? "\n  ]\n}\n" : "]\n}\n");
            var bytes = Encoding.UTF8.GetBytes(s.ToString());
            if (bytes.Length > MaxBytes) throw new InvalidOperationException(EntryName + " would be larger than " + (MaxBytes >> 10) + " KiB.");
            return bytes;
        }

        static void AppendOrigin(StringBuilder s, ResourceOrigin o)
        {
            switch (o.Kind)
            {
                case ResourceOriginKind.UnityAsset:
                    s.Append("{ \"type\": \"unityAsset\", \"guid\": \"").Append(o.AssetGuid).Append("\", \"localFileID\": ").Append(o.LocalFileId.ToString(CultureInfo.InvariantCulture)).Append(", \"path\": ").Append(YlpFormat.Quote(o.Path))
                     .Append(", \"stamp\": ").Append(YlpFormat.Quote(o.SourceStamp ?? "")).Append(", \"readThroughGpu\": ").Append(o.ReadThroughGpu ? "true" : "false").Append(" }");
                    break;
                case ResourceOriginKind.File:
                case ResourceOriginKind.Library:
                    s.Append("{ \"type\": \"").Append(o.Kind == ResourceOriginKind.File ? "file" : "library").Append("\", \"").Append(o.Kind == ResourceOriginKind.File ? "path" : "file").Append("\": ").Append(YlpFormat.Quote(o.Path))
                     .Append(", \"sha256\": \"").Append(o.SourceStamp).Append("\", \"length\": ").Append(o.SourceLength.ToString(CultureInfo.InvariantCulture)).Append(" }");
                    break;
                case ResourceOriginKind.BuiltIn:
                    s.Append("{ \"type\": \"builtIn\", \"key\": \"").Append(o.BuiltInKey).Append("\", \"version\": ").Append(o.BuiltInVersion.ToString(CultureInfo.InvariantCulture)).Append(" }");
                    break;
                default: s.Append("{ \"type\": \"none\" }"); break;
            }
        }

        static string Words(ResourceKind k) => k == ResourceKind.SmartMaterial ? "smart material" : k == ResourceKind.SmartMask ? "smart mask" : "image";
        static string KindName(ResourceKind k) => k == ResourceKind.Brush ? "brush" : k == ResourceKind.Material ? "material" : k == ResourceKind.SmartMaterial ? "smartMaterial" : k == ResourceKind.SmartMask ? "smartMask" : "image";

        static string ColorSpaceName(ResourceColorSpace c) => c == ResourceColorSpace.Srgb ? "srgb" : c == ResourceColorSpace.Linear ? "linear" : "unspecified";

        /// <summary>Reads resources.json. Unknown keys are skipped; an unknown kind or origin type, a broken value, duplicate IDs or
        /// more than <see cref="ProjectResources.MaxResources"/> entries are InvalidDataException (a resource is part of the work,
        /// so it is not dropped to open the rest).</summary>
        public static IReadOnlyList<YlpResourceEntry> Read(byte[] bytes)
        {
            var root = YlpFormat.ParseObject(bytes, EntryName, MaxBytes);
            if (!root.TryGetValue("resources", out var listValue) || !(listValue is List<object> items)) throw new InvalidDataException(EntryName + " has no \"resources\" list.");
            if (items.Count > ProjectResources.MaxResources) throw new InvalidDataException(EntryName + " lists " + items.Count + " resources; a project holds at most " + ProjectResources.MaxResources + ".");
            var result = new List<YlpResourceEntry>();
            foreach (var item in items)
            {
                if (!(item is Dictionary<string, object> o)) throw new InvalidDataException(EntryName + " \"resources\" holds something that is not an object.");
                string name = o.TryGetValue("name", out var n) && n is string text ? text : null;
                string who = name != null ? "resource \"" + name + "\"" : "a resource";
                InvalidDataException Bad(string what) => new InvalidDataException(EntryName + ": " + who + " " + what + ".");
                if (!o.TryGetValue("id", out var idValue) || !(idValue is string idText) || !Guid.TryParseExact(idText, "D", out var id) || id.ToString("D") != idText || id == Guid.Empty) throw Bad("has no valid \"id\" (a lower-case GUID with hyphens)");
                if (!o.TryGetValue("kind", out var kindValue) || !(kindValue is string kind)) throw Bad("has no \"kind\"");
                if (kind != "image" && kind != "smartMaterial" && kind != "smartMask" && kind != "brush" && kind != "material") throw Bad("is of kind \"" + kind + "\", which this YoluPainter does not know");
                if (name == null) throw Bad("has no \"name\"");
                if (!o.TryGetValue("content", out var contentValue) || !(contentValue is string content) || !ImageContent.IsHash(content)) throw Bad("has no valid \"content\" (64 lower-case hex digits)");
                if (kind != "image")
                {
                    if (!o.TryGetValue("length", out var lengthValue) || !(lengthValue is long length) || length < 1 || length > YlpArchive.MaxEntryBytes) throw Bad("has no valid \"length\" (1–" + YlpArchive.MaxEntryBytes + ")");
                    ResourceOrigin smartOrigin;
                    try { smartOrigin = o.TryGetValue("origin", out var smartOriginValue) && smartOriginValue != null ? ReadOrigin(smartOriginValue, Bad) : ResourceOrigin.None; }
                    catch (ArgumentException ex) { throw Bad("has a broken \"origin\" (" + ex.Message.Split('\n')[0].Split(new[] { " (Parameter" }, StringSplitOptions.None)[0] + ")"); }
                    try { result.Add(new YlpResourceEntry(id, kind == "brush" ? ResourceKind.Brush : kind == "material" ? ResourceKind.Material : kind == "smartMask" ? ResourceKind.SmartMask : ResourceKind.SmartMaterial, name, content, length, smartOrigin)); }
                    catch (ArgumentException ex) { throw Bad("is invalid (" + ex.Message.Split('\n')[0].Split(new[] { " (Parameter" }, StringSplitOptions.None)[0] + ")"); }
                    continue;
                }
                int Side(string key)
                {
                    if (!o.TryGetValue(key, out var v) || !(v is long l) || l < 1 || l > ImageContent.MaxSide) throw Bad("has no valid \"" + key + "\" (1–" + ImageContent.MaxSide + ")");
                    return (int)l;
                }
                int width = Side("width"), height = Side("height");
                var colorSpace = ResourceColorSpace.Unspecified;
                if (o.TryGetValue("colorSpace", out var csValue) && csValue != null)
                {
                    switch (csValue as string)
                    {
                        case "unspecified": break;
                        case "srgb": colorSpace = ResourceColorSpace.Srgb; break;
                        case "linear": colorSpace = ResourceColorSpace.Linear; break;
                        default: throw Bad("has an unknown \"colorSpace\"");
                    }
                }
                ResourceOrigin origin;
                try { origin = o.TryGetValue("origin", out var originValue) && originValue != null ? ReadOrigin(originValue, Bad) : ResourceOrigin.None; }
                catch (ArgumentException ex) { throw Bad("has a broken \"origin\" (" + ex.Message.Split('\n')[0].Split(new[] { " (Parameter" }, StringSplitOptions.None)[0] + ")"); }
                try { result.Add(new YlpResourceEntry(id, ResourceKind.Image, name, content, width, height, colorSpace, origin)); }
                catch (ArgumentException ex) { throw Bad("is invalid (" + ex.Message.Split('\n')[0].Split(new[] { " (Parameter" }, StringSplitOptions.None)[0] + ")"); }
            }
            if (result.Select(e => e.Id).Distinct().Count() != result.Count) throw new InvalidDataException(EntryName + ": two resources have the same ID.");
            return result;
        }

        static ResourceOrigin ReadOrigin(object value, Func<string, InvalidDataException> bad)
        {
            if (!(value is Dictionary<string, object> o)) throw bad("has an \"origin\" that is not an object");
            if (!o.TryGetValue("type", out var typeValue) || !(typeValue is string type)) throw bad("has an \"origin\" without a \"type\"");
            string Text(string key, bool required = true)
            {
                if (o.TryGetValue(key, out var v) && v is string t) return t;
                if (!required && (!o.ContainsKey(key) || o[key] == null)) return null;
                throw bad("has an \"origin\" without a text \"" + key + "\"");
            }
            long Number(string key) { if (o.TryGetValue(key, out var v) && v is long l) return l; throw bad("has an \"origin\" without an integer \"" + key + "\""); }
            switch (type)
            {
                case "none": return ResourceOrigin.None;
                case "unityAsset":
                    bool gpu = o.TryGetValue("readThroughGpu", out var g) && g is bool b && b;
                    return ResourceOrigin.UnityAsset(Text("guid"), Text("path"), Text("stamp", required: false), gpu, o.ContainsKey("localFileID") ? Number("localFileID") : 0);
                case "file": return ResourceOrigin.File(Text("path"), Text("sha256"), Number("length"));
                case "library": return ResourceOrigin.Library(Text("file"), Text("sha256"), Number("length"));
                case "builtIn":
                    long version = Number("version");
                    if (version < 1 || version > int.MaxValue) throw bad("has a built-in \"version\" out of range");
                    return ResourceOrigin.BuiltIn(Text("key"), (int)version);
                default: throw bad("has an origin of type \"" + type + "\", which this YoluPainter does not know");
            }
        }
    }
}
