using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Yozolab.YoluPainter.Core.Shelf
{
    /// <summary>What a smart asset holds: a stack of layers (Substance Painter's smart material) or one mask (its smart mask).
    /// Values are stored (the .ylsmart file and resources.json); append only.</summary>
    public enum SmartKind { Material = 0, Mask = 1 }

    /// <summary>Why saving or placing a smart material or smart mask was refused (nothing changed).</summary>
    public enum SmartRefusal
    {
        /// <summary>Nothing to save: no layer chosen.</summary>
        NothingToSave,
        /// <summary>A smart mask was asked of a layer without a mask.</summary>
        NoMask,
        /// <summary>A smart mask was placed as layers, or a smart material onto a mask.</summary>
        WrongKind,
        /// <summary>The placed layers would exceed the document's layer pixel budget (<see cref="PaintDocument.SourceBudgetBytes"/>).</summary>
        Budget,
        /// <summary>The document would hold more layers than a native document can be read back with.</summary>
        LayerLimit,
        /// <summary>A filter of it cannot run in this document (its reach or working memory at this size).</summary>
        Filters,
        /// <summary>It cannot be made at the document's size (a resampled setting or path cannot be drawn there).</summary>
        Size,
    }

    public sealed class SmartRefusedException : InvalidOperationException
    {
        public SmartRefusal Reason { get; }
        /// <summary>Budget refusals: the bytes asked for and the room there was (else 0).</summary>
        public long Bytes { get; }
        public long Limit { get; }
        public SmartRefusedException(SmartRefusal reason, string message, long bytes = 0, long limit = 0, Exception inner = null) : base(message, inner) { Reason = reason; Bytes = bytes; Limit = limit; }
    }

    /// <summary>An image the layers of a smart material refer to, kept with it so it can be placed into another project. The ID is the one
    /// the layers use; placing adds the image to the project's resources (an equal image already there is used instead) and points the
    /// placed layers at the ID it got there.</summary>
    public sealed class SmartImage
    {
        public Guid Id { get; }
        public string Name { get; }
        public ImageContent Content { get; }
        public ResourceColorSpace ColorSpace { get; }
        public SmartImage(Guid id, string name, ImageContent content, ResourceColorSpace colorSpace)
        {
            if (id == Guid.Empty) throw new ArgumentException("An image needs an ID.", nameof(id));
            ImageResource.CheckName(name);
            if (!Enum.IsDefined(typeof(ResourceColorSpace), colorSpace)) throw new ArgumentOutOfRangeException(nameof(colorSpace));
            Id = id; Name = name; Content = content ?? throw new ArgumentNullException(nameof(content)); ColorSpace = colorSpace;
        }
    }

    /// <summary>
    /// A smart material (layers) or smart mask (one mask) kept apart from any texture set (Substance Painter's smart materials and smart
    /// masks): saved from a document with <see cref="PaintDocument.CaptureSmartMaterial"/> / <see cref="PaintDocument.CaptureSmartMask"/>,
    /// written as a .ylsmart file (<see cref="Persistence.SmartMaterialFile"/>) or kept in a project, and placed into a document with
    /// <see cref="PaintDocument.PlaceSmartMaterial"/> / <see cref="PaintDocument.ApplySmartMask"/>.
    /// <para>The content is a <i>fragment</i>: a document of the size it was saved at that holds only those layers (a smart mask: one fill
    /// layer without values that carries the mask), so every layer attribute the native document knows — kind, blend mode and opacity,
    /// per-channel blends, fill values, adjustments, masks with their filters, content filters and generators, clipping, groups, locks,
    /// 2D paths — is kept by the same validated code (<see cref="Persistence.DocumentBinary"/>), and new layer attributes come along with
    /// new native versions. Paint layers keep their pixels; placing into a texture set of another size resamples the whole fragment with
    /// <see cref="PaintDocument.Resampled"/> (filter radii and 2D paths follow the scale), so it looks the same on the model.</para>
    /// <para>What depends on a model is not kept: paths on the model become plain pixels when saving, and generators keep no pin to the
    /// bake they read. A generator that was pinned is listed in <see cref="Repin"/> and is pinned again, where it is placed, to the maps that
    /// texture set has then (when they are usable). Immutable: placing reads the fragment and never changes it.</para>
    /// </summary>
    public sealed class SmartMaterial
    {
        public const int MaxNameLength = ImageResource.MaxNameLength;
        public SmartKind Kind { get; }
        public string Name { get; }
        /// <summary>The fragment's size (the size of the texture set it was saved from).</summary>
        public int Width => Fragment.Width;
        public int Height => Fragment.Height;
        public int TileSize => Fragment.TileSize;
        /// <summary>The layers (read only; never change them).</summary>
        internal PaintDocument Fragment { get; }
        /// <summary>Generator stages (IDs in the fragment) that were pinned to a bake: placing pins them to the maps of the texture set they
        /// go into.</summary>
        public IReadOnlyCollection<Guid> Repin { get; }
        public IReadOnlyList<SmartImage> Images { get; }
        /// <summary>The channels its layers have switched on (a smart mask: none), in channel order.</summary>
        public IReadOnlyList<PaintChannel> Channels { get; }
        /// <summary>English notes about what saving changed (paths on the model became pixels …). Not stored.</summary>
        public IReadOnlyList<string> Notes { get; }
        public int LayerCount => Fragment.Layers.Count;
        /// <summary>Layers at the top of the fragment (placing puts more than one into a group named after the smart material).</summary>
        public int TopLevelCount => Fragment.Layers.Count(l => l.ParentId == Guid.Empty);
        /// <summary>True when it holds pixels (paint layers' tiles or mask tiles), which are resampled for another size.</summary>
        public bool HasPixels => Fragment.Layers.Any(l => l.Channels.Values.Any(s => s.TileCount > 0) || l.Mask != null && l.Mask.Surface.TileCount > 0);
        /// <summary>The generator stages it holds.</summary>
        public int GeneratorCount => Fragment.Layers.Sum(l => l.Filters.Count(f => f.Settings.IsGenerator) + (l.Mask == null ? 0 : l.Mask.Filters.Count(f => f.Settings.IsGenerator)));
        /// <summary>Layer pixel bytes the fragment holds (what placing it at the same size adds to a document).</summary>
        public long PixelBytes => Fragment.AllocatedBytes;
        /// <summary>The layers' names, bottom to top (for showing).</summary>
        public IReadOnlyList<string> LayerNames => Fragment.Layers.Select(l => l.Name).ToList().AsReadOnly();
        /// <summary>The mask a smart mask carries (null for a smart material).</summary>
        internal RasterMask MaskSource => Kind == SmartKind.Mask ? Fragment.Layers[0].Mask : null;

        /// <summary>Makes a smart material from a fragment (the readers and the capture). Checks what a fragment must be: a name, at least one
        /// layer, no path on a model, every <paramref name="repin"/> ID a generator stage of it, the images' IDs distinct, and for a smart mask
        /// exactly one fill layer without values that has a mask. The fragment is taken over (the caller must not change it).</summary>
        internal SmartMaterial(SmartKind kind, string name, PaintDocument fragment, IEnumerable<Guid> repin, IEnumerable<SmartImage> images, IEnumerable<string> notes = null)
        {
            if (!Enum.IsDefined(typeof(SmartKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            CheckName(name);
            Fragment = fragment ?? throw new ArgumentNullException(nameof(fragment));
            if (fragment.HasActiveStroke) throw new ArgumentException("The fragment has a stroke in progress.", nameof(fragment));
            if (fragment.Layers.Count == 0) throw new ArgumentException("A smart material holds at least one layer.", nameof(fragment));
            if (fragment.Layers.Any(l => l.Path is Paths.SurfacePath)) throw new ArgumentException("A smart material holds no path on a model (they belong to one model's triangles).", nameof(fragment));
            if (kind == SmartKind.Mask)
            {
                var only = fragment.Layers[0];
                if (fragment.Layers.Count != 1 || only.Kind != LayerKind.Fill || only.FillValues.Count != 0 || only.Mask == null || only.Filters.Count != 0)
                    throw new ArgumentException("A smart mask holds one fill layer without values that carries the mask.", nameof(fragment));
            }
            var stages = new HashSet<Guid>();
            foreach (var l in fragment.Layers)
            {
                foreach (var f in l.Filters) if (f.Settings.IsGenerator) stages.Add(f.Id);
                if (l.Mask != null) foreach (var f in l.Mask.Filters) if (f.Settings.IsGenerator) stages.Add(f.Id);
            }
            var pins = new HashSet<Guid>(repin ?? Enumerable.Empty<Guid>());
            foreach (var id in pins) if (!stages.Contains(id)) throw new ArgumentException("A stage to pin again (" + id + ") is not a generator of the smart material.", nameof(repin));
            if (fragment.Layers.Any(l => l.Filters.Concat(l.Mask == null ? Enumerable.Empty<FilterEffect>() : l.Mask.Filters).Any(f => f.Settings.IsGenerator && f.Settings.Generator.Pins.Count > 0)))
                throw new ArgumentException("A smart material keeps no generator pinned to a bake (it is pinned again where it is placed).", nameof(fragment));
            var list = (images ?? Enumerable.Empty<SmartImage>()).ToList();
            if (list.Any(i => i == null) || list.Select(i => i.Id).Distinct().Count() != list.Count) throw new ArgumentException("The images need distinct IDs.", nameof(images));
            Kind = kind; Name = name;
            Repin = new ReadOnlyCollection<Guid>(pins.OrderBy(g => g).ToList());
            Images = list.AsReadOnly();
            Channels = kind == SmartKind.Mask ? new PaintChannel[0]
                : ((PaintChannel[])Enum.GetValues(typeof(PaintChannel))).Where(c => fragment.Layers.Any(l => l.IsChannelEnabled(c))).ToList().AsReadOnly();
            Notes = (notes ?? Enumerable.Empty<string>()).ToList().AsReadOnly();
        }

        /// <summary>A name: 1–256 characters, not only white space, no control characters.</summary>
        public static void CheckName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A smart material needs a name.", nameof(name));
            if (name.Length > MaxNameLength) throw new ArgumentException("A smart material name is at most " + MaxNameLength + " characters.", nameof(name));
            if (name.Any(c => c < 0x20 || c == 0x7f)) throw new ArgumentException("A smart material name cannot hold control characters.", nameof(name));
        }

        /// <summary>The same content under another name.</summary>
        public SmartMaterial Renamed(string name) => name == Name ? this : new SmartMaterial(Kind, name, Fragment, Repin, Images, Notes);

        /// <summary>The native bytes of the fragment (what the file stores).</summary>
        public byte[] FragmentBytes() => Persistence.DocumentBinary.Write(Fragment);

        public override string ToString() => Name + " (" + Kind + ", " + LayerCount + " layer(s), " + Width + " × " + Height + ")";
    }

    /// <summary>Where and how <see cref="PaintDocument.PlaceSmartMaterial"/> puts a smart material.</summary>
    public sealed class SmartPlacement
    {
        /// <summary>The group the layers go into (Guid.Empty: the top level) …</summary>
        public Guid ParentId;
        /// <summary>… at this position among its children (0 = bottom; -1 or more than there are = on top).</summary>
        public int Position = -1;
        /// <summary>The channels the texture set uses. A channel of the smart material that is not here is switched off on the placed layers
        /// (its values and pixels are kept; switching it on later loses nothing). Null keeps every channel on.</summary>
        public IReadOnlyCollection<PaintChannel> Channels;
        /// <summary>How pixels are resampled when the smart material was saved at another size (null: area average when shrinking,
        /// bilinear when enlarging, like placing an image).</summary>
        public CanvasResampling? Resampling;
        /// <summary>The name of the group that holds a smart material with several top-level layers (null: its name).</summary>
        public string GroupName;
        /// <summary>Its images' IDs → the IDs they have in the project's resources (from <see cref="SmartImages.AddTo"/>). Null when it has none.</summary>
        public IReadOnlyDictionary<Guid, Guid> ResourceIds;

        /// <summary>Directly above a layer, in that layer's group (above a group: above the group and its contents).</summary>
        public static SmartPlacement Above(PaintDocument document, Guid layerId)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var layer = document.GetLayer(layerId);
            var siblings = document.ChildrenOf(layer.ParentId);
            int index = 0; for (int i = 0; i < siblings.Count; i++) if (siblings[i] == layer) index = i;
            return new SmartPlacement { ParentId = layer.ParentId, Position = index + 1 };
        }
    }

    /// <summary>What placing a smart material or smart mask did.</summary>
    public sealed class SmartPlaceResult
    {
        /// <summary>The placed top layer (the group holding a smart material with several top-level layers), or the layer whose mask was set.</summary>
        public Guid LayerId { get; internal set; }
        /// <summary>Every placed layer, bottom to top (a smart mask: the masked layer).</summary>
        public IReadOnlyList<Guid> Layers { get; internal set; }
        /// <summary>True when it was saved at another size and was resampled to the document's.</summary>
        public bool Resampled { get; internal set; }
        /// <summary>Channels its layers use that the texture set does not: switched off on the placed layers (values kept).</summary>
        public IReadOnlyList<PaintChannel> SwitchedOff { get; internal set; }
        /// <summary>Generators pinned to the maps the texture set has now.</summary>
        public int Pinned { get; internal set; }
        /// <summary>Generators that were pinned where they were saved but could not be pinned here (no usable maps): they follow whatever
        /// maps the texture set gets.</summary>
        public int NotPinned { get; internal set; }
        /// <summary>Why generators of it have no effect here now (distinct reasons; empty when every generator that is on works).</summary>
        public IReadOnlyList<string> InactiveGenerators { get; internal set; }
        /// <summary>A smart mask replaced the layer's mask (undo brings it back).</summary>
        public bool ReplacedMask { get; internal set; }
        /// <summary>Settings that could not keep their look at this size (English, from <see cref="PaintDocument.Resampled"/>).</summary>
        public IReadOnlyList<string> Notes { get; internal set; }
    }

    /// <summary>Putting a smart material's images into a project's resources before its layers are placed.</summary>
    public static class SmartImages
    {
        /// <summary>
        /// Adds the smart material's images to <paramref name="resources"/> and returns where each went: an image whose pixels the project
        /// already holds uses that resource; a new one keeps its ID when it is free. Checked first, so a refusal (budget, count) adds nothing.
        /// </summary>
        public static IReadOnlyDictionary<Guid, Guid> AddTo(ProjectResources resources, SmartMaterial material)
        {
            if (resources == null) throw new ArgumentNullException(nameof(resources));
            if (material == null) throw new ArgumentNullException(nameof(material));
            var map = new Dictionary<Guid, Guid>();
            if (material.Images.Count == 0) return map;
            // 先に全部を確かめる（途中で断って半分だけ入れない）
            long more = 0; int count = 0; var counted = new HashSet<string>();
            foreach (var image in material.Images)
            {
                if (resources.FindContent(image.Content.Hash) != null || !counted.Add(image.Content.Hash)) continue;
                more += image.Content.ByteSize; count++;
            }
            if (resources.Count + count > ProjectResources.MaxResources) throw new ResourceRefusedException(ResourceRefusal.TooMany, "A project holds at most " + ProjectResources.MaxResources + " resources; \"" + material.Name + "\" brings " + count + " more image(s).");
            if (resources.UsedBytes + more > resources.BudgetBytes)
                throw new ResourceRefusedException(ResourceRefusal.OverBudget, "\"" + material.Name + "\" brings " + Mib(more) + " MiB of images; the project's resources would exceed the resource budget of " + Mib(resources.BudgetBytes) + " MiB.");
            foreach (var image in material.Images)
            {
                bool free = !resources.TryGetImage(image.Id, out _) && !resources.Smart.Any(s => s.Id == image.Id);
                var held = resources.Add(image.Name, image.Content, ResourceOrigin.None, image.ColorSpace, out _, free ? image.Id : (Guid?)null);
                map[image.Id] = held.Id;
            }
            return map;
        }
        static string Mib(long bytes) => (bytes / (1024.0 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }
}
