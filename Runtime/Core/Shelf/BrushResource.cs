using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Core.Shelf
{
    /// <summary>ブラシ設定と筆先・質感の写し。Unity のオブジェクトや外のファイルに依存しない。</summary>
    public sealed class BrushResource
    {
        public Guid Id { get; }
        public string Name { get; internal set; }
        public ResourceKind Kind => ResourceKind.Brush;
        public ResourceOrigin Origin { get; }
        public string Hash { get; }
        public long PixelBytes { get; }
        public long ByteSize => bytes.LongLength + PixelBytes;
        readonly byte[] bytes;
        public byte[] FileBytes() => (byte[])bytes.Clone();
        internal byte[] Bytes => bytes;
        internal BrushResource(Guid id, string name, byte[] file, ResourceOrigin origin)
        {
            if (id == Guid.Empty) throw new ArgumentException("A resource needs an ID.");
            ImageResource.CheckName(name); var files = BrushResourceFile.Read(file);
            PixelBytes = files.Where(f => f.Key != BrushResourceFile.StateName).Sum(f => { var size = RgbaPng.ReadSize(f.Value); return (long)size.width * size.height; });
            Id = id; Name = name; bytes = (byte[])file.Clone(); Hash = GenerationStore.Hash(bytes); Origin = origin ?? ResourceOrigin.None;
        }
    }

    /// <summary>ブラシの携帯形式。state.json はエディタの BrushState、画像は最大 2048 の RGBA8 PNG。ハッシュと展開上限をアーカイブで検証する。</summary>
    public static class BrushResourceFile
    {
        public const string Extension = ".ylbrush", StateName = "state.json";
        static readonly YlpArchive.Profile Profile = new YlpArchive.Profile
        {
            MimeType = "application/x-yolupainter-brush", What = "YoluPainter brush", Headers = new[] { "YOLUPAINTER-BRUSH-1" }, HeaderPrefix = "YOLUPAINTER-BRUSH-",
            Timestamp = new DateTime(2026, 1, 1), Complete = (names, level) => names.Contains(StateName), Incomplete = "The brush has no state.json.",
            ValidateName = (name, level) => { if (name != YlpArchive.ManifestName && name != StateName && name != "texture.png" && name != "dual.png" && !(name.StartsWith("tip-", StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.Ordinal) && int.TryParse(name.Substring(4, name.Length - 8), out int n) && n >= 0 && n < 256)) throw new InvalidDataException("Unknown brush entry: " + name); },
        };
        public static byte[] Write(IDictionary<string, byte[]> files)
        {
            Validate(files); return YlpArchive.Write(files, Profile, "A brush needs state.json.");
        }
        public static Dictionary<string, byte[]> Read(byte[] bytes)
        {
            var files = YlpArchive.Read(bytes ?? throw new ArgumentNullException(nameof(bytes)), Profile); Validate(files); return files;
        }
        static void Validate(IDictionary<string, byte[]> files)
        {
            foreach (string name in files.Keys) Profile.ValidateName(name, 1);
            if (!files.TryGetValue(StateName, out var state)) throw new InvalidDataException("The brush has no state.json.");
            var json = YlpFormat.ParseObject(state, StateName, 64 * 1024);
            if (!json.TryGetValue("schema", out var schema) || !(schema is long version) || version < 1 || version > 3) throw new InvalidDataException("Unsupported brush schema.");
            foreach (string key in new[] { "tipId", "textureId", "dualTipId" })
                if (json.TryGetValue(key, out var value) && (!(value is string text) || text.Length > 0)) throw new InvalidDataException("Portable brushes keep their images, not external tip IDs.");
            var indices = files.Keys.Where(k => k.StartsWith("tip-", StringComparison.Ordinal)).Select(k => int.TryParse(k.Substring(4, k.Length - 8), out int n) ? n : -1).OrderBy(n => n).ToArray();
            if (!indices.SequenceEqual(Enumerable.Range(0, indices.Length))) throw new InvalidDataException("Brush tips must be consecutively numbered from zero.");
            foreach (var entry in files.Where(f => f.Key != StateName))
            {
                var size = RgbaPng.ReadSize(entry.Value);
                if (size.width > BrushTip.MaxSize || size.height > BrushTip.MaxSize) throw new InvalidDataException("A brush image exceeds the tip size budget.");
                RgbaPng.Decode(entry.Value);
            }
        }
    }
}
