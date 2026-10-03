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
    /// </summary>
    public sealed class ProjectResources : IImageResources
    {
        public const int MaxResources = 256;
        /// <summary>The default for <see cref="BudgetBytes"/>: 1 GiB of pixels (four 8192² images).</summary>
        public const long DefaultBudgetBytes = 1L << 30;

        readonly List<ImageResource> images = new List<ImageResource>();
        readonly Dictionary<string, (ImageContent content, int users)> contents = new Dictionary<string, (ImageContent, int)>(StringComparer.Ordinal);
        readonly List<Func<Guid, string>> usageProbes = new List<Func<Guid, string>>();
        long budget = DefaultBudgetBytes;

        public event Action<ResourceChange> Changed;
        public long Revision { get; private set; }
        public IReadOnlyList<ImageResource> Images => images.AsReadOnly();
        public int Count => images.Count;
        /// <summary>Pixel bytes held (each distinct content once).</summary>
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
            if (images.Count >= MaxResources) { refusal = ResourceRefusal.TooMany; return "A project holds at most " + MaxResources + " resources."; }
            if (UsedBytes + content.ByteSize > budget) { refusal = ResourceRefusal.OverBudget; return OverBudget(content.ByteSize); }
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
            if (images.Count >= MaxResources) throw new ResourceRefusedException(ResourceRefusal.TooMany, "A project holds at most " + MaxResources + " resources.");
            var newId = id ?? Guid.NewGuid();
            if (images.Any(r => r.Id == newId)) throw new ArgumentException("The project already has a resource " + newId + ".", nameof(id));
            var held = Hold(content); // may throw OverBudget
            var image = new ImageResource(newId, name, held, origin, colorSpace);
            images.Add(image); added = true;
            Raise(ResourceChangeKind.Added, image.Id);
            return image;
        }

        /// <summary>Adds the entries of a file as they were saved: IDs kept, equal contents shared (two entries may hold the same
        /// pixels after an update), no deduplication by content. Used by the readers; refused like <see cref="Add"/>.</summary>
        public ImageResource Restore(Guid id, string name, ImageContent content, ResourceOrigin origin, ResourceColorSpace colorSpace, long revision = 0)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (images.Count >= MaxResources) throw new ResourceRefusedException(ResourceRefusal.TooMany, "A project holds at most " + MaxResources + " resources.");
            if (images.Any(r => r.Id == id)) throw new ArgumentException("Two resources have the ID " + id + ".", nameof(id));
            var image = new ImageResource(id, name, content, origin, colorSpace) { Revision = revision };
            image.Content = Hold(content);
            images.Add(image);
            Raise(ResourceChangeKind.Added, id);
            return image;
        }

        public void Rename(Guid id, string name)
        {
            ImageResource.CheckName(name);
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
            Release(old);
            image.Content = HoldUnchecked(content); image.Origin = origin ?? image.Origin; image.Revision++;
            Raise(ResourceChangeKind.ContentReplaced, id);
        }

        /// <summary>Removes everything (a new project). Raises <see cref="ResourceChangeKind.Reset"/>.</summary>
        public void Clear()
        {
            images.Clear(); contents.Clear(); UsedBytes = 0;
            Raise(ResourceChangeKind.Reset, Guid.Empty);
        }

        /// <summary>Takes over all entries of <paramref name="source"/> (which is left empty) — opening a project — and raises
        /// <see cref="ResourceChangeKind.Reset"/>. The budget stays this collection's; the probes stay registered.</summary>
        public void ResetTo(ProjectResources source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source == this) return;
            images.Clear(); contents.Clear();
            images.AddRange(source.images); foreach (var c in source.contents) contents.Add(c.Key, c.Value); UsedBytes = source.UsedBytes;
            source.images.Clear(); source.contents.Clear(); source.UsedBytes = 0;
            Raise(ResourceChangeKind.Reset, Guid.Empty);
        }

        ImageContent Hold(ImageContent content)
        {
            if (!contents.ContainsKey(content.Hash) && UsedBytes + content.ByteSize > budget) throw new ResourceRefusedException(ResourceRefusal.OverBudget, OverBudget(content.ByteSize));
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
