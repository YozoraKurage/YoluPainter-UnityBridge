using System;
using System.Linq;

namespace Yozolab.YoluPainter.Core.Shelf
{
    /// <summary>
    /// A smart material or smart mask held by a project (the Assets panel's This Project): a stable ID, a name, the file it is (the bytes
    /// of a .ylsmart, kept exactly as read or written so the .ylp stores them unchanged), the material read from it, and where it came from
    /// (made in this project, the user's library or a built-in one). Placing it copies its layers; nothing refers to it afterwards, so
    /// removing it never changes a layer.
    /// </summary>
    public sealed class SmartResource
    {
        public Guid Id { get; }
        public ResourceKind ResourceKind { get; }
        public string Name { get; internal set; }
        public SmartMaterial Material { get; }
        public SmartKind Kind => Material.Kind;
        public ResourceOrigin Origin { get; internal set; }
        /// <summary>SHA-256 (lower-case hex) of <see cref="FileBytes"/>: equal files are held once, and the .ylp names the entry after it.</summary>
        public string Hash { get; }
        public long Length => bytes.LongLength;
        /// <summary>Memory the budget counts: the file and the fragment's layer pixels.</summary>
        public long ByteSize => EstimateBytes(bytes.LongLength, Material);
        public static long EstimateBytes(long fileLength, SmartMaterial material) => fileLength + material.PixelBytes + material.Images.GroupBy(i => i.Content.Hash).Sum(g => g.First().Content.ByteSize);
        readonly byte[] bytes;

        internal SmartResource(Guid id, string name, byte[] fileBytes, SmartMaterial material, ResourceOrigin origin, ResourceKind? resourceKind = null)
        {
            if (id == Guid.Empty) throw new ArgumentException("A resource needs an ID.", nameof(id));
            ImageResource.CheckName(name);
            if (fileBytes == null) throw new ArgumentNullException(nameof(fileBytes));
            bytes = (byte[])fileBytes.Clone(); // 保存用の写しで共有するため、呼び出し元の配列から切り離す。
            Material = material ?? throw new ArgumentNullException(nameof(material));
            ResourceKind = resourceKind ?? (material.Kind == SmartKind.Mask ? global::Yozolab.YoluPainter.Core.Shelf.ResourceKind.SmartMask : global::Yozolab.YoluPainter.Core.Shelf.ResourceKind.SmartMaterial);
            if (ResourceKind != global::Yozolab.YoluPainter.Core.Shelf.ResourceKind.SmartMaterial && ResourceKind != global::Yozolab.YoluPainter.Core.Shelf.ResourceKind.SmartMask && ResourceKind != global::Yozolab.YoluPainter.Core.Shelf.ResourceKind.Material || (ResourceKind == global::Yozolab.YoluPainter.Core.Shelf.ResourceKind.SmartMask) != (material.Kind == SmartKind.Mask))
                throw new ArgumentException("Wrong material resource kind.");
            Id = id; Name = name; Origin = origin ?? ResourceOrigin.None;
            Hash = Persistence.GenerationStore.Hash(bytes);
        }

        /// <summary>A copy of the file's bytes (a .ylsmart).</summary>
        public byte[] FileBytes() => (byte[])bytes.Clone();
        internal byte[] Bytes => bytes;

        public override string ToString() => Name + " (" + Kind + ", " + Length + " bytes)";
    }
}
