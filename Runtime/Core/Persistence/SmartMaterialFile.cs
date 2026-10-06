using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Core.Persistence
{
    /// <summary>What a .ylsmart says about itself without reading its layers (for listings).</summary>
    public sealed class SmartFileInfo
    {
        public int Format { get; internal set; }
        public SmartKind Kind { get; internal set; }
        public string Name { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int LayerCount { get; internal set; }
        public IReadOnlyList<PaintChannel> Channels { get; internal set; }
        public YlpWriterInfo SavedBy { get; internal set; }
        /// <summary>The thumbnail PNG (derived; null when the file has none).</summary>
        public byte[] Thumbnail { get; internal set; }
        public bool SphereThumbnail { get; internal set; }
    }

    /// <summary>
    /// A smart material or smart mask as one file (.ylsmart; the standalone repository's docs/YLP_FORMAT.md). The outer layer is the .ylp's zip layer
    /// (<see cref="YlpArchive"/>: the uncompressed <c>mimetype</c> first, then <c>manifest.sha256</c> with every entry's SHA-256 and length,
    /// names, sizes and expansion limits checked before anything is read) with its own MIME type and manifest version
    /// (<c>YOLUPAINTER-SMART-1</c>), so neither kind of file opens as the other. Inside:
    /// <list type="bullet">
    /// <item><c>smart.json</c> (source): the format (now 1), the kind, the name, the fragment's size, layer count and channels (for
    /// listings; checked against the layers when read), the generator stages to pin again, and the application that wrote it.</item>
    /// <item><c>layers.utpaint</c> (source): the fragment, a native document (<see cref="DocumentBinary"/>, any version it reads).</item>
    /// <item><c>resources.json</c> and <c>resources/&lt;content&gt;.png</c> (source, when its layers refer to images): as in the .ylp
    /// (<see cref="ResourceIndex"/>).</item>
    /// <item><c>thumbnail.png</c> (derived): a preview for listings.</item>
    /// </list>
    /// A newer format, an unknown kind or channel, a broken or mismatching entry is refused with the reason; nothing is dropped to read the
    /// rest. Entries the format does not list are ignored (the file's bytes are kept as they are wherever it is held). The zip's dates are
    /// fixed, so the same content always gives the same bytes in one runtime.
    /// </summary>
    public static class SmartMaterialFile
    {
        public const string Extension = ".ylsmart";
        public const string MimeType = "application/x-yolupainter-smart";
        public const string ManifestHeader = "YOLUPAINTER-SMART-1";
        public const string InfoName = "smart.json", LayersName = "layers.utpaint", ThumbnailName = "thumbnail.png";
        /// <summary>The format this version writes and reads up to.</summary>
        public const int Format = 1;
        const int MaxInfoBytes = 64 * 1024;

        static readonly YlpArchive.Profile Profile = new YlpArchive.Profile
        {
            MimeType = MimeType, What = "YoluPainter smart material file", Headers = new[] { ManifestHeader }, HeaderPrefix = "YOLUPAINTER-SMART-",
            ValidateName = ValidateName, Complete = (names, level) => names.Contains(InfoName) && names.Contains(LayersName),
            Incomplete = "The smart material file has no " + InfoName + " or " + LayersName + ".", Timestamp = new DateTime(2026, 1, 1),
        };

        /// <summary>Names: a root name, or one name under resources/; letters, digits and . - _, at most 96 characters.</summary>
        static void ValidateName(string name, int level)
        {
            string leaf = name != null && name.StartsWith(ResourceIndex.Folder, StringComparison.Ordinal) ? name.Substring(ResourceIndex.Folder.Length) : name;
            if (string.IsNullOrEmpty(leaf) || name.Length > 96 || leaf.Contains("..") || leaf.StartsWith(".", StringComparison.Ordinal) || leaf.Any(c => !(char.IsLetterOrDigit(c) && c < 128 || c == '.' || c == '-' || c == '_')))
                throw new InvalidDataException("Unsafe entry name: " + name);
        }

        /// <summary>True when the bytes start like a .ylsmart (the uncompressed mimetype entry). Says nothing about the rest.</summary>
        public static bool LooksLike(byte[] head)
        {
            if (head == null || head.Length < 38 + MimeType.Length || head[0] != 0x50 || head[1] != 0x4b || head[2] != 3 || head[3] != 4) return false;
            return Encoding.ASCII.GetString(head, 30, 8) == "mimetype" && Encoding.ASCII.GetString(head, 38, MimeType.Length) == MimeType;
        }

        /// <summary>The outer layer only: a .ylsmart holding these entries (like <see cref="YlpArchive.Write"/> for the .ylp). It needs
        /// smart.json and layers.utpaint but does not check what they hold; <see cref="Read"/> does.</summary>
        public static byte[] WriteArchive(IDictionary<string, byte[]> files) => YlpArchive.Write(files, Profile, "A smart material needs " + InfoName + " and " + LayersName + ".");
        /// <summary>The outer layer only: the entries of a .ylsmart, each checked against the manifest (like <see cref="YlpArchive.Read"/>).</summary>
        public static Dictionary<string, byte[]> ReadArchive(byte[] bytes) => YlpArchive.Read(bytes ?? throw new ArgumentNullException(nameof(bytes)), Profile);

        // ───────── 書く ─────────

        /// <summary>The file of a smart material (thumbnail optional).</summary>
        public static byte[] Write(SmartMaterial material, YlpWriterInfo savedBy, byte[] thumbnailPng = null, CancellationToken cancellationToken = default, bool sphereThumbnail = false)
        {
            if (material == null) throw new ArgumentNullException(nameof(material));
            if (savedBy == null) throw new ArgumentNullException(nameof(savedBy));
            cancellationToken.ThrowIfCancellationRequested();
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { InfoName, WriteInfo(material, savedBy, sphereThumbnail && thumbnailPng != null) },
                { LayersName, material.FragmentBytes() },
            };
            if (thumbnailPng != null) files.Add(ThumbnailName, thumbnailPng);
            if (material.Images.Count > 0)
            {
                files.Add(ResourceIndex.EntryName, ResourceIndex.Write(material.Images.Select(i => new YlpResourceEntry(i.Id, ResourceKind.Image, i.Name, i.Content.Hash, i.Content.Width, i.Content.Height, i.ColorSpace, ResourceOrigin.None))));
                foreach (var image in material.Images) { cancellationToken.ThrowIfCancellationRequested(); files[ResourceIndex.ContentEntry(image.Content.Hash)] = image.Content.EncodePng(); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return WriteArchive(files);
        }

        static byte[] WriteInfo(SmartMaterial m, YlpWriterInfo w, bool sphereThumbnail)
        {
            var s = new StringBuilder("{\n  \"format\": ").Append(Format.ToString(CultureInfo.InvariantCulture))
                .Append(",\n  \"kind\": \"").Append(KindName(m.Kind)).Append('"')
                .Append(",\n  \"name\": ").Append(YlpFormat.Quote(m.Name))
                .Append(",\n  \"width\": ").Append(m.Width.ToString(CultureInfo.InvariantCulture)).Append(", \"height\": ").Append(m.Height.ToString(CultureInfo.InvariantCulture))
                .Append(",\n  \"layers\": ").Append(m.LayerCount.ToString(CultureInfo.InvariantCulture))
                .Append(",\n  \"channels\": [").Append(string.Join(", ", m.Channels.Select(c => "\"" + c + "\""))).Append(']')
                .Append(",\n  \"repin\": [").Append(string.Join(", ", m.Repin.Select(g => "\"" + g.ToString("D") + "\""))).Append(']')
                .Append(",\n  \"savedBy\": { \"app\": ").Append(YlpFormat.Quote(w.App)).Append(", \"version\": ").Append(YlpFormat.Quote(w.Version)).Append(", \"unity\": ").Append(YlpFormat.Quote(w.Unity)).Append(" }")
                .Append(sphereThumbnail ? ",\n  \"thumbnailShape\": \"sphere\"" : "").Append("\n}\n");
            return Encoding.UTF8.GetBytes(s.ToString());
        }

        public static string KindName(SmartKind kind) => kind == SmartKind.Mask ? "smartMask" : "smartMaterial";
        public static bool TryParseKind(string text, out SmartKind kind)
        {
            switch (text)
            {
                case "smartMaterial": kind = SmartKind.Material; return true;
                case "smartMask": kind = SmartKind.Mask; return true;
                default: kind = default; return false;
            }
        }

        // ───────── 読む ─────────

        /// <summary>Reads and checks a whole file (every entry, the layers, the images). InvalidDataException with the reason otherwise.</summary>
        public static SmartMaterial Read(byte[] bytes)
        {
            var files = YlpArchive.Read(bytes ?? throw new ArgumentNullException(nameof(bytes)), Profile);
            var info = ParseInfo(files[InfoName], out var repin);
            PaintDocument fragment;
            try { fragment = DocumentBinary.Read(files[LayersName]); }
            catch (InvalidDataException ex) { throw new InvalidDataException("The smart material's layers cannot be read: " + ex.Message, ex); }
            var images = new List<SmartImage>();
            if (files.TryGetValue(ResourceIndex.EntryName, out var index))
            {
                var entries = ResourceIndex.Read(index);
                if (entries.Any(e => e.Kind != ResourceKind.Image)) throw new InvalidDataException("The smart material's " + ResourceIndex.EntryName + " lists something that is not an image.");
                var loaded = ResourceIndex.Load(files, entries);
                foreach (var image in loaded.Images) images.Add(new SmartImage(image.Id, image.Name, image.Content, image.ColorSpace));
            }
            SmartMaterial material;
            try { material = new SmartMaterial(info.Kind, info.Name, fragment, repin, images); }
            catch (ArgumentException ex) { throw new InvalidDataException("The smart material is broken: " + Reason(ex), ex); }
            if (material.Width != info.Width || material.Height != info.Height || material.LayerCount != info.LayerCount || !material.Channels.SequenceEqual(info.Channels))
                throw new InvalidDataException(InfoName + " does not match the layers (" + info.Width + " × " + info.Height + ", " + info.LayerCount + " layer(s) listed; the layers are "
                    + material.Width + " × " + material.Height + ", " + material.LayerCount + ").");
            return material;
        }

        /// <summary>Reads only smart.json and the thumbnail (both checked against the manifest); the layers are not read.</summary>
        public static SmartFileInfo ReadInfo(byte[] bytes)
        {
            var files = YlpArchive.Read(bytes ?? throw new ArgumentNullException(nameof(bytes)), Profile, name => name == InfoName || name == ThumbnailName);
            var info = ParseInfo(files[InfoName], out _);
            if (files.TryGetValue(ThumbnailName, out var thumbnail)) info.Thumbnail = thumbnail;
            return info;
        }

        static SmartFileInfo ParseInfo(byte[] bytes, out List<Guid> repin)
        {
            var root = YlpFormat.ParseObject(bytes, InfoName, MaxInfoBytes);
            if (!root.TryGetValue("format", out var formatValue) || !(formatValue is long format) || format < 1 || format > int.MaxValue)
                throw new InvalidDataException(InfoName + " has no valid \"format\" (an integer of at least 1).");
            var savedBy = root.TryGetValue("savedBy", out var w) ? Writer(w) : null;
            if (format > Format)
                throw new InvalidDataException("This smart material uses format " + format + (savedBy != null ? ", saved by " + savedBy : "") + ". This YoluPainter reads up to format " + Format + "; update YoluPainter to use it.");
            if (savedBy == null) throw new InvalidDataException(InfoName + " has no \"savedBy\".");
            if (!root.TryGetValue("kind", out var kindValue) || !(kindValue is string kindText)) throw new InvalidDataException(InfoName + " has no \"kind\".");
            if (!TryParseKind(kindText, out var kind)) throw new InvalidDataException(InfoName + ": the kind \"" + kindText + "\" is not one this YoluPainter knows.");
            if (!root.TryGetValue("name", out var nameValue) || !(nameValue is string name)) throw new InvalidDataException(InfoName + " has no \"name\".");
            try { SmartMaterial.CheckName(name); } catch (ArgumentException ex) { throw new InvalidDataException(InfoName + ": " + Reason(ex)); }
            int Integer(string key, int min, int max)
            {
                if (!root.TryGetValue(key, out var v) || !(v is long l) || l < min || l > max) throw new InvalidDataException(InfoName + " has no valid \"" + key + "\" (" + min + "–" + max + ").");
                return (int)l;
            }
            int width = Integer("width", 1, PaintDocument.MaxNativeSide), height = Integer("height", 1, PaintDocument.MaxNativeSide), layers = Integer("layers", 1, DocumentBinary.MaxLayers);
            var channels = new List<PaintChannel>();
            if (!root.TryGetValue("channels", out var channelValue) || !(channelValue is List<object> channelList)) throw new InvalidDataException(InfoName + " has no \"channels\" list.");
            foreach (var c in channelList)
            {
                if (!(c is string text) || !Enum.TryParse(text, false, out PaintChannel channel) || !Enum.IsDefined(typeof(PaintChannel), channel) || channel.ToString() != text)
                    throw new InvalidDataException(InfoName + ": the channel " + (c is string t ? "\"" + t + "\"" : "value") + " is not one this YoluPainter knows.");
                if (channels.Contains(channel)) throw new InvalidDataException(InfoName + " lists " + channel + " twice.");
                channels.Add(channel);
            }
            repin = new List<Guid>();
            if (root.TryGetValue("repin", out var repinValue) && repinValue != null)
            {
                if (!(repinValue is List<object> list)) throw new InvalidDataException(InfoName + " \"repin\" is not a list.");
                foreach (var item in list)
                {
                    if (!(item is string text) || !Guid.TryParseExact(text, "D", out var id) || id.ToString("D") != text || id == Guid.Empty || repin.Contains(id))
                        throw new InvalidDataException(InfoName + " \"repin\" holds something that is not a distinct lower-case GUID.");
                    repin.Add(id);
                }
            }
            return new SmartFileInfo { Format = (int)format, Kind = kind, Name = name, Width = width, Height = height, LayerCount = layers, Channels = channels.AsReadOnly(), SavedBy = savedBy, SphereThumbnail = root.TryGetValue("thumbnailShape", out var shape) && shape as string == "sphere" };
        }

        static YlpWriterInfo Writer(object value)
        {
            if (!(value is Dictionary<string, object> o)) throw new InvalidDataException(InfoName + " \"savedBy\" is not an object.");
            string Text(string field)
            {
                if (!o.TryGetValue(field, out var v) || !(v is string t) || t.Length == 0 || t.Length > YlpFormat.MaxText)
                    throw new InvalidDataException(InfoName + " \"savedBy." + field + "\" is not a string of 1–" + YlpFormat.MaxText + " characters.");
                return t;
            }
            return new YlpWriterInfo(Text("app"), Text("version"), Text("unity"));
        }

        static string Reason(ArgumentException ex) => ex.Message.Split('\n')[0].Split(new[] { " (Parameter" }, StringSplitOptions.None)[0];
    }
}
