using System;
using System.Collections.Generic;
using System.Linq;

namespace Yozolab.YoluPainter.Core.Shelf
{
    /// <summary>Why a resource operation was refused (nothing changed).</summary>
    public enum ResourceRefusal
    {
        /// <summary>An image larger than <see cref="ImageContent.MaxSide"/> on a side.</summary>
        TooLarge,
        /// <summary>The project's resources would exceed <see cref="ProjectResources.BudgetBytes"/>.</summary>
        OverBudget,
        /// <summary>The project already has <see cref="ProjectResources.MaxResources"/> resources.</summary>
        TooMany,
        /// <summary>Something in the project uses the resource (a usage probe answered).</summary>
        InUse,
        /// <summary>No such resource.</summary>
        Unknown,
        /// <summary>The source cannot be read as an image of a kind YoluPainter takes (an HDR or cube texture, no GPU to read it …).</summary>
        Unsupported,
    }

    public sealed class ResourceRefusedException : InvalidOperationException
    {
        public ResourceRefusal Refusal { get; }
        public ResourceRefusedException(ResourceRefusal refusal, string message) : base(message) { Refusal = refusal; }
    }

    public enum ResourceChangeKind { Added, Removed, Renamed, ContentReplaced, ColorSpaceChanged, Reset }

    /// <summary>One change of a project's resources (<see cref="Reset"/>: the whole set was replaced — a project was opened or
    /// made; <see cref="Id"/> is empty).</summary>
    public readonly struct ResourceChange
    {
        public ResourceChangeKind Kind { get; }
        public Guid Id { get; }
        public ResourceChange(ResourceChangeKind kind, Guid id) { Kind = kind; Id = id; }
        public override string ToString() => Kind + (Id == Guid.Empty ? "" : " " + Id);
    }

    /// <summary>
    /// What a consumer (a stage that projects an image, a decal …) needs from the project's images: look one up by ID and know when
    /// anything changed. <see cref="Revision"/> grows with every change, so a consumer can compare it instead of listening.
    /// </summary>
    public interface IImageResources
    {
        long Revision { get; }
        bool TryGetImage(Guid id, out ImageResource image);
        event Action<ResourceChange> Changed;
    }

    /// <summary>
    /// The resources of a project (shared by all its texture sets), with the embedded copy of each. Equal contents are kept once
    /// (<see cref="Add"/> returns the resource that already holds them), and the budget counts each content once. Every change
    /// raises <see cref="Changed"/> and bumps <see cref="Revision"/>; refused changes throw <see cref="ResourceRefusedException"/>
    /// and change nothing. Resource changes are not in a document's undo history (they belong to the project, like its texture
    /// sets); removing asks every usage probe first. Single-threaded like the document.
    /// <para>Besides images (<see cref="Images"/>) a project holds smart materials and smart masks (<see cref="Smart"/>, .ylp format 5):
    /// equal files are held once, they count against the same budget and the same <see cref="MaxResources"/>, and nothing refers to
    /// them (placing one copies its layers).</para>
    /// </summary>
    public sealed class ProjectResources : IImageResources
    {
        public const int MaxResources = 256;
        /// <summary>The default for <see cref="BudgetBytes"/>: 1 GiB of pixels (four 8192² images).</summary>
        public const long DefaultBudgetBytes = 1L << 30;

        readonly List<ImageResource> images = new List<ImageResource>();
        readonly List<BrushResource> brushes = new List<BrushResource>();
        readonly List<SmartResource> smart = new List<SmartResource>();
        readonly Dictionary<string, (ImageContent content, int users)> contents = new Dictionary<string, (ImageContent, int)>(StringComparer.Ordinal);
        readonly List<Func<Guid, string>> usageProbes = new List<Func<Guid, string>>();
        long budget = DefaultBudgetBytes;

        /// <summary>.ylp の展開後の上限から、文書などの予約量を引いたリソースの保存予算。読み込みでは無制限にして旧データを捨てない。</summary>
        public long ArchiveBudgetBytes { get; set; } = Persistence.YlpArchive.MaxTotalBytes;
        public long ArchiveUsedBytes => contents.Values.Sum(v => v.content.EncodePng().LongLength)
            + smart.GroupBy(s => s.Hash).Sum(g => g.First().Length) + brushes.GroupBy(b => b.Hash).Sum(g => g.First().Bytes.LongLength);
        void EnsureArchiveRoom(long more, long freed = 0)
        {
            if (more > Persistence.YlpArchive.MaxEntryBytes || more > ArchiveBudgetBytes - ArchiveUsedBytes + freed)
                throw new ResourceRefusedException(ResourceRefusal.OverBudget, "The resource would exceed the .ylp archive budget (768 MiB including documents, composites and resources). Nothing was added.");
        }

        public void CheckSmartArchiveRoom(byte[] bytes)
        {
            string hash = Persistence.GenerationStore.Hash(bytes);
            if (!smart.Any(s => s.Hash == hash)) EnsureArchiveRoom(bytes.LongLength);
        }
        public event Action<ResourceChange> Changed;
        public long Revision { get; private set; }
        public IReadOnlyList<ImageResource> Images => images.AsReadOnly();
        /// <summary>The smart materials and smart masks, in the order the panel shows them.</summary>
        public IReadOnlyList<SmartResource> Smart => smart.AsReadOnly();
        /// <summary>Every resource: images and smart ones.</summary>
        public IReadOnlyList<BrushResource> Brushes => brushes.AsReadOnly();
        public int Count => images.Count + smart.Count + brushes.Count;
        /// <summary>Bytes held: image pixels (each distinct content once) and smart resources (<see cref="SmartResource.ByteSize"/>).</summary>
        public long UsedBytes { get; private set; }
        /// <summary>The most pixel bytes the resources may hold. Lowering it below <see cref="UsedBytes"/> keeps what is there and
        /// refuses only further growth.</summary>
        public long BudgetBytes { get => budget; set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); budget = value; } }

        public bool TryGetImage(Guid id, out ImageResource image) { image = images.FirstOrDefault(r => r.Id == id); return image != null; }
        public ImageResource Get(Guid id) => TryGetImage(id, out var image) ? image : throw new ResourceRefusedException(ResourceRefusal.Unknown, "The project has no resource " + id + ".");
        /// <summary>The first resource holding this content, or null.</summary>
        public ImageResource FindByHash(string hash) => images.FirstOrDefault(r => r.ContentHash == hash);
        /// <summary>The held content with this hash (to share it instead of keeping a second copy), or null.</summary>
        public ImageContent FindContent(string hash) => hash != null && contents.TryGetValue(hash, out var held) ? held.content : null;

        /// <summary>Why adding <paramref name="content"/> would be refused, or null (equal content already held is always fine).</summary>
        public string AddRefusal(ImageContent content, out ResourceRefusal refusal)
        {
            refusal = default;
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (contents.ContainsKey(content.Hash)) return null;
            if (Count >= MaxResources) { refusal = ResourceRefusal.TooMany; return "A project holds at most " + MaxResources + " resources."; }
            if (UsedBytes + content.ByteSize > budget) { refusal = ResourceRefusal.OverBudget; return OverBudget(content.ByteSize); }
            try { EnsureArchiveRoom(content.EncodePng().LongLength); }
            catch (ResourceRefusedException ex) { refusal = ex.Refusal; return ex.Message; }
            return null;
        }

        string OverBudget(long more) => "The project's resources would hold " + Mib(UsedBytes + more) + " MiB of pixels, over the resource budget of " + Mib(budget) + " MiB (Project Settings > YoluPainter).";
        static string Mib(long bytes) => (bytes / (1024.0 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Adds an image. When the project already holds equal pixels, nothing is added and that resource is returned with
        /// <paramref name="added"/> false (its name and origin are kept). Refused (nothing changes) when over the budget or count.
        /// </summary>
        public ImageResource Add(string name, ImageContent content, ResourceOrigin origin, ResourceColorSpace colorSpace, out bool added, Guid? id = null)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            ImageResource.CheckName(name);
            added = false;
            var existing = FindByHash(content.Hash);
            if (existing != null) return existing;
            if (Count >= MaxResources) throw new ResourceRefusedException(ResourceRefusal.TooMany, "A project holds at most " + MaxResources + " resources.");
            var newId = id ?? Guid.NewGuid();
            if (HasId(newId)) throw new ArgumentException("The project already has a resource " + newId + ".", nameof(id));
            var image = new ImageResource(newId, name, content, origin, colorSpace);
            image.Content = Hold(content); // may throw OverBudget
            images.Add(image); added = true;
            Raise(ResourceChangeKind.Added, image.Id);
            return image;
        }

        /// <summary>Adds the entries of a file as they were saved: IDs kept, equal contents shared (two entries may hold the same
        /// pixels after an update), no deduplication by content. Used by the readers; refused like <see cref="Add"/>.</summary>
        public ImageResource Restore(Guid id, string name, ImageContent content, ResourceOrigin origin, ResourceColorSpace colorSpace, long revision = 0)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (Count >= MaxResources) throw new ResourceRefusedException(ResourceRefusal.TooMany, "A project holds at most " + MaxResources + " resources.");
            if (HasId(id)) throw new ArgumentException("Two resources have the ID " + id + ".", nameof(id));
            var image = new ImageResource(id, name, content, origin, colorSpace) { Revision = revision };
            image.Content = Hold(content);
            images.Add(image);
            Raise(ResourceChangeKind.Added, id);
            return image;
        }

        public void Rename(Guid id, string name)
        {
            ImageResource.CheckName(name);
            if (TryGetBrush(id, out var brush)) { if (brush.Name == name) return; brush.Name = name; Raise(ResourceChangeKind.Renamed, id); return; }
            if (TryGetSmart(id, out var held)) { if (held.Name == name) return; held.Name = name; Raise(ResourceChangeKind.Renamed, id); return; }
            var image = Get(id);
            if (image.Name == name) return;
            image.Name = name; Raise(ResourceChangeKind.Renamed, id);
        }

        public void SetColorSpace(Guid id, ResourceColorSpace colorSpace)
        {
            if (!Enum.IsDefined(typeof(ResourceColorSpace), colorSpace)) throw new ArgumentOutOfRangeException(nameof(colorSpace));
            var image = Get(id);
            if (image.ColorSpace == colorSpace) return;
            image.ColorSpace = colorSpace; Raise(ResourceChangeKind.ColorSpaceChanged, id);
        }

        /// <summary>Registers a question asked before a resource is removed: it returns what uses the resource, or null.</summary>
        public void AddUsageProbe(Func<Guid, string> probe) { if (probe == null) throw new ArgumentNullException(nameof(probe)); usageProbes.Add(probe); }
        public void RemoveUsageProbe(Func<Guid, string> probe) => usageProbes.Remove(probe);
        /// <summary>What uses the resource (the first probe that answers), or null.</summary>
        public string UsageOf(Guid id)
        {
            foreach (var probe in usageProbes) { var use = probe(id); if (!string.IsNullOrEmpty(use)) return use; }
            return null;
        }

        /// <summary>Removes a resource. Refused (InUse) when a usage probe says something uses it.</summary>
        public void Remove(Guid id)
        {
            if (TryGetBrush(id, out var brush)) { var usage = UsageOf(id); if (usage != null) throw new ResourceRefusedException(ResourceRefusal.InUse, "The brush is used by " + usage); brushes.Remove(brush); UsedBytes -= brush.ByteSize; Raise(ResourceChangeKind.Removed, id); return; }
            if (TryGetSmart(id, out var held)) { smart.Remove(held); UsedBytes -= held.ByteSize; Raise(ResourceChangeKind.Removed, id); return; }
            var image = Get(id);
            var use = UsageOf(id);
            if (use != null) throw new ResourceRefusedException(ResourceRefusal.InUse, "\"" + image.Name + "\" is used by " + use + "; it was not removed.");
            images.Remove(image); Release(image.Content);
            Raise(ResourceChangeKind.Removed, id);
        }

        /// <summary>Replaces a resource's pixels under the same ID (updating the copy from its changed source). Refused when the
        /// new pixels would exceed the budget. Equal pixels only update the origin (no content change is raised then).</summary>
        public void ReplaceContent(Guid id, ImageContent content, ResourceOrigin origin)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            var image = Get(id);
            if (image.ContentHash == content.Hash) { image.Origin = origin ?? image.Origin; return; }
            var old = image.Content;
            long freed = contents[old.Hash].users == 1 ? old.ByteSize : 0;
            if (!contents.ContainsKey(content.Hash) && UsedBytes - freed + content.ByteSize > budget) throw new ResourceRefusedException(ResourceRefusal.OverBudget, OverBudget(content.ByteSize - freed));
            if (!contents.ContainsKey(content.Hash)) EnsureArchiveRoom(content.EncodePng().LongLength, freed > 0 ? old.EncodePng().LongLength : 0);
            Release(old);
            image.Content = HoldUnchecked(content); image.Origin = origin ?? image.Origin; image.Revision++;
            Raise(ResourceChangeKind.ContentReplaced, id);
        }

        /// <summary>Removes everything (a new project). Raises <see cref="ResourceChangeKind.Reset"/>.</summary>
        public void Clear()
        {
            images.Clear(); smart.Clear(); brushes.Clear(); contents.Clear(); UsedBytes = 0;
            Raise(ResourceChangeKind.Reset, Guid.Empty);
        }

        /// <summary>Takes over all entries of <paramref name="source"/> (which is left empty) — opening a project — and raises
        /// <see cref="ResourceChangeKind.Reset"/>. The budget stays this collection's; the probes stay registered.</summary>
        public void ResetTo(ProjectResources source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source == this) return;
            images.Clear(); smart.Clear(); brushes.Clear(); contents.Clear();
            images.AddRange(source.images); smart.AddRange(source.smart); brushes.AddRange(source.brushes); foreach (var c in source.contents) contents.Add(c.Key, c.Value); UsedBytes = source.UsedBytes;
            source.images.Clear(); source.smart.Clear(); source.brushes.Clear(); source.contents.Clear(); source.UsedBytes = 0;
            Raise(ResourceChangeKind.Reset, Guid.Empty);
        }

        bool HasId(Guid id) => images.Any(r => r.Id == id) || smart.Any(r => r.Id == id) || brushes.Any(r => r.Id == id);

        // ───────── スマートマテリアル・スマートマスク ─────────

        public bool TryGetSmart(Guid id, out SmartResource resource) { resource = smart.FirstOrDefault(r => r.Id == id); return resource != null; }
        public SmartResource GetSmart(Guid id) => TryGetSmart(id, out var r) ? r : throw new ResourceRefusedException(ResourceRefusal.Unknown, "The project has no smart material " + id + ".");
        /// <summary>The smart resource holding exactly this file (SHA-256 of its bytes), or null.</summary>
        public SmartResource FindSmartByHash(string hash) => smart.FirstOrDefault(r => r.Hash == hash);

        /// <summary>Why adding a smart file of <paramref name="byteSize"/> bytes (<see cref="SmartResource.ByteSize"/>) would be refused, or null.</summary>
        public string AddSmartRefusal(long byteSize, out ResourceRefusal refusal)
        {
            refusal = default;
            if (Count >= MaxResources) { refusal = ResourceRefusal.TooMany; return "A project holds at most " + MaxResources + " resources."; }
            if (UsedBytes + byteSize > budget) { refusal = ResourceRefusal.OverBudget; return OverBudget(byteSize); }
            return null;
        }

        /// <summary>
        /// Adds a smart material or smart mask (<paramref name="fileBytes"/>: its .ylsmart file, <paramref name="material"/>: what it reads
        /// as). When the project already holds the very same file, nothing is added and that resource is returned with
        /// <paramref name="added"/> false. Refused (nothing changes) when over the budget or count.
        /// </summary>
        public SmartResource AddSmart(string name, byte[] fileBytes, SmartMaterial material, ResourceOrigin origin, out bool added, Guid? id = null, ResourceKind? resourceKind = null)
        {
            var resource = new SmartResource(id ?? Guid.NewGuid(), name, fileBytes, material, origin, resourceKind);
            added = false;
            var existing = smart.FirstOrDefault(s => s.Hash == resource.Hash && s.ResourceKind == resource.ResourceKind);
            if (existing != null) return existing;
            if (HasId(resource.Id)) throw new ArgumentException("The project already has a resource " + resource.Id + ".", nameof(id));
            string why = AddSmartRefusal(resource.ByteSize, out var refusal);
            if (why != null) throw new ResourceRefusedException(refusal, why);
            if (!smart.Any(s => s.Hash == resource.Hash)) EnsureArchiveRoom(resource.Length);
            smart.Add(resource); UsedBytes += resource.ByteSize; added = true;
            Raise(ResourceChangeKind.Added, resource.Id);
            return resource;
        }

        /// <summary>Adds a smart resource of a file as it was saved (ID kept, no deduplication). Used by the readers; refused like
        /// <see cref="AddSmart"/>.</summary>
        public SmartResource RestoreSmart(Guid id, string name, byte[] fileBytes, SmartMaterial material, ResourceOrigin origin, ResourceKind? resourceKind = null)
        {
            if (HasId(id)) throw new ArgumentException("Two resources have the ID " + id + ".", nameof(id));
            var resource = new SmartResource(id, name, fileBytes, material, origin, resourceKind);
            string why = AddSmartRefusal(resource.ByteSize, out var refusal);
            if (why != null) throw new ResourceRefusedException(refusal, why);
            if (!smart.Any(s => s.Hash == resource.Hash)) EnsureArchiveRoom(resource.Length);
            smart.Add(resource); UsedBytes += resource.ByteSize;
            Raise(ResourceChangeKind.Added, id);
            return resource;
        }

        public bool TryGetBrush(Guid id, out BrushResource resource) { resource = brushes.FirstOrDefault(b => b.Id == id); return resource != null; }
        public BrushResource GetBrush(Guid id) => TryGetBrush(id, out var b) ? b : throw new ResourceRefusedException(ResourceRefusal.Unknown, "The project has no brush " + id + ".");
        public BrushResource AddBrush(string name, byte[] file, ResourceOrigin origin, out bool added, Guid? id = null, bool restore = false)
        {
            var resource = new BrushResource(id ?? Guid.NewGuid(), name, file, origin); added = false;
            var same = brushes.FirstOrDefault(b => b.Hash == resource.Hash);
            if (!restore && same != null) return same;
            if (HasId(resource.Id)) throw new ArgumentException("Duplicate resource ID.");
            string why = AddSmartRefusal(resource.ByteSize, out var refusal);
            if (why != null) throw new ResourceRefusedException(refusal, why);
            if (!brushes.Any(b => b.Hash == resource.Hash)) EnsureArchiveRoom(resource.Bytes.LongLength);
            brushes.Add(resource); UsedBytes += resource.ByteSize; added = true; Raise(ResourceChangeKind.Added, resource.Id); return resource;
        }

        ImageContent Hold(ImageContent content)
        {
            if (!contents.ContainsKey(content.Hash) && UsedBytes + content.ByteSize > budget) throw new ResourceRefusedException(ResourceRefusal.OverBudget, OverBudget(content.ByteSize));
            if (!contents.ContainsKey(content.Hash)) EnsureArchiveRoom(content.EncodePng().LongLength);
            return HoldUnchecked(content);
        }
        ImageContent HoldUnchecked(ImageContent content)
        {
            if (contents.TryGetValue(content.Hash, out var held)) { contents[content.Hash] = (held.content, held.users + 1); return held.content; }
            contents.Add(content.Hash, (content, 1)); UsedBytes += content.ByteSize;
            return content;
        }
        void Release(ImageContent content)
        {
            var held = contents[content.Hash];
            if (held.users > 1) { contents[content.Hash] = (held.content, held.users - 1); return; }
            contents.Remove(content.Hash); UsedBytes -= held.content.ByteSize;
        }

        void Raise(ResourceChangeKind kind, Guid id) { Revision++; Changed?.Invoke(new ResourceChange(kind, id)); }
    }
}
