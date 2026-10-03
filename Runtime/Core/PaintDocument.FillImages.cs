using System;
using System.Collections.Generic;
using System.Linq;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>
    /// Fill layers' images: each channel of a fill layer can read an image resource of the project instead of its one value, laid
    /// onto the texture set by the layer's <see cref="FillProjection"/>. The images live outside the document (the project's
    /// resources, <see cref="ImageResources"/>); the document only stores their IDs, and resolves each ID to the image's content and
    /// colour space when it is first read and again whenever the resources' revision changes.
    /// <para>Evaluation: an image channel's pixels are evaluated like a filter stack's output (<see cref="PaintLayer.HasEvaluatedOutput"/>):
    /// block by block on the CPU by the filter engine, cached under the filter cache budget, stamped with the layer's fill revision (and
    /// the mesh maps' revision for the model projections), so the compositors (CPU and GPU) read the same bytes. The image's mipmap is
    /// built once per content, conversion and luminance and kept under <see cref="FillImageCacheBudgetBytes"/>.</para>
    /// <para>Change tracking: a change of an image, the projection or a channel's value is one undo step that marks the layer's channels
    /// changed. When an image an ID resolves to changes (the resource was updated from its source, its colour space changed, it was
    /// removed, another set of resources was connected) every layer reading it is marked changed. The model projections read the mesh
    /// maps like generators do (<see cref="GeneratorInputs"/>) and are marked changed with them.</para>
    /// <para>When an image cannot be used (no resources connected, the ID is not in the project, the mipmap is over budget, a mesh map
    /// is missing or stale, the model root is not known) the channel shows its fill value and <see cref="GetFillImageStatus"/> says
    /// why; nothing reads a missing image as black, and the ID is kept.</para>
    /// </summary>
    public sealed partial class PaintDocument
    {
        public const long DefaultFillImageCacheBudgetBytes = 256L * 1024 * 1024;

        IImageResources imageResources;
        long imageResourcesSeen; bool imagesPolled;
        /// <summary>The revision callers watch for anything an evaluation reads from outside the document (maps, images).</summary>
        long inputsRevision = 1;
        sealed class ResolvedImage { public ImageResource Resource; public ImageContent Content; public ResourceColorSpace ColorSpace; public string Reason; }
        readonly Dictionary<Guid, ResolvedImage> resolvedImages = new Dictionary<Guid, ResolvedImage>();
        sealed class BoundSampler { public long FillRevision, MapsRevision; public FillImageSampler Sampler; }
        readonly Dictionary<(Guid, PaintChannel), BoundSampler> samplers = new Dictionary<(Guid, PaintChannel), BoundSampler>();
        sealed class CachedChain { public ImageMipChain Chain; public long Used; }
        readonly Dictionary<(string, FillImageConversion, bool), CachedChain> chains = new Dictionary<(string, FillImageConversion, bool), CachedChain>();
        long chainBytes, chainClock, fillImageCacheBudget = DefaultFillImageCacheBudgetBytes;

        /// <summary>Where fill layers look up their images (the project's resources). Not part of history or persistence. Setting another
        /// provider resolves every image again and reports the layers whose image changed.</summary>
        public IImageResources ImageResources
        {
            get { return imageResources; }
            set
            {
                if (ReferenceEquals(value, imageResources)) return;
                imageResources = value; imagesPolled = false;
                PollImageResources();
            }
        }

        /// <summary>Bytes of image mipmaps the document may keep (each content, conversion and luminance once; least recently used
        /// first out). An image whose mipmap alone needs more is refused when it is set and shown as its fill value (with the reason)
        /// if the budget is lowered later.</summary>
        public long FillImageCacheBudgetBytes
        {
            get { return fillImageCacheBudget; }
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                if (value == fillImageCacheBudget) return;
                fillImageCacheBudget = value; TrimChains(null);
                // 予算で使えなかった（使えるようになった）画像があり得るので、画像の層を描き直す
                foreach (var layer in layers) if (layer.Kind == LayerKind.Fill && layer.FillImages.Count > 0) { FillChanged(layer); MarkLayerChanged(layer, null); }
            }
        }
        /// <summary>Bytes of image mipmaps kept now.</summary>
        public long FillImageCacheBytes { get { return chainBytes; } }

        /// <summary>True when a fill layer has an image channel or is a decal (it reads the mesh maps even without an image).</summary>
        public bool HasFillImages { get { foreach (var layer in layers) if (layer.Kind == LayerKind.Fill && (layer.FillImages.Count > 0 || layer.IsDecal)) return true; return false; } }
        bool HasMapProjections { get { foreach (var layer in layers) if (layer.ReadsMeshMapsForFill) return true; return false; } }

        /// <summary>The fill value a channel gets when an image is set on a channel that had none (shown where the image cannot be used).</summary>
        public static Rgba32 DefaultFillImageFallback(PaintChannel channel)
            => channel == PaintChannel.Normal ? new Rgba32(128, 128, 255, 255) : FillImageColor.IsScalarChannel(channel) ? new Rgba32(128, 128, 128, 255) : new Rgba32(255, 255, 255, 255);

        // ───────────── editing ─────────────

        /// <summary>Sets (or with null removes) the image a fill layer's channel reads, as one undo step. Setting an image on a channel without
        /// a value gives it <see cref="DefaultFillImageFallback"/> and enables the channel; removing the image keeps the value. Refused when no
        /// resources are connected, the project has no such image, its mipmap would exceed <see cref="FillImageCacheBudgetBytes"/>, or the
        /// layer's pixels or transparent pixels are locked.</summary>
        public void SetFillImage(Guid id, PaintChannel channel, Guid? resourceId)
        {
            EnsureNoStroke(); PaintLayer.ValidateChannel(channel); var layer = GetLayer(id);
            if (layer.Kind != LayerKind.Fill) throw new InvalidOperationException("Only fill layers have images. Place the image as a layer instead.");
            Guid? old = layer.FillImages.TryGetValue(channel, out var current) ? current : (Guid?)null;
            if (Nullable.Equals(old, resourceId)) return;
            if (resourceId.HasValue)
            {
                if (resourceId.Value == Guid.Empty) throw new ArgumentException("An image resource ID must not be empty.", nameof(resourceId));
                if (imageResources == null) throw new InvalidOperationException("No image resources are connected to this document (open it in the YoluPainter window, where the project's resources are).");
                if (!imageResources.TryGetImage(resourceId.Value, out var image)) throw new ResourceRefusedException(ResourceRefusal.Unknown, "The project has no image resource " + resourceId.Value + ".");
                long need = ImageMipChain.ExtraBytes(image.Width, image.Height);
                if (need > fillImageCacheBudget)
                    throw new ResourceRefusedException(ResourceRefusal.OverBudget, "\"" + image.Name + "\" needs " + Mib(need) + " MiB of mipmaps to be projected, more than the fill image budget (" + Mib(fillImageCacheBudget) + " MiB). Nothing was changed.");
            }
            RefuseLockedPixels(layer, erase: false);
            RefuseLockedTransparency(layer); // 画像は場所ごとに不透明度が変わる
            bool hadValue = layer.FillValues.TryGetValue(channel, out var value); bool wasEnabled = layer.IsChannelEnabled(channel);
            var fallback = hadValue ? value : DefaultFillImageFallback(channel);
            Execute(LayerScoped(layer, channel,
                () =>
                {
                    layer.SetFillImageInternal(channel, resourceId);
                    if (resourceId.HasValue) { if (!hadValue) layer.SetFillValueInternal(channel, fallback); layer.Enable(channel, true); }
                    FillChanged(layer);
                },
                () =>
                {
                    layer.SetFillImageInternal(channel, old);
                    if (!hadValue) layer.SetFillValueInternal(channel, null);
                    layer.Enable(channel, wasEnabled);
                    FillChanged(layer);
                }, 96));
        }

        /// <summary>Replaces a fill layer's projection, as one undo step (slider and gizmo drags coalesce). With images, or as a decal before
        /// or after, it changes pixels, so it is refused under Lock Image Pixels, Lock Transparent Pixels and Lock All; otherwise only under
        /// Lock All. Moving a decal (a decal before and after, without filters) reports only the tiles either placement may cover as changed.</summary>
        public void SetFillProjection(Guid id, FillProjection projection, bool coalesce = false)
        {
            EnsureNoStroke(); if (projection == null) throw new ArgumentNullException(nameof(projection));
            string why = projection.Refusal(); if (why != null) throw new ArgumentOutOfRangeException(nameof(projection), why);
            var layer = GetLayer(id);
            if (layer.Kind != LayerKind.Fill) throw new InvalidOperationException("Only fill layers have a projection.");
            var old = layer.Projection; if (old.Equals(projection)) return;
            if (layer.FillImages.Count > 0 || old.IsDecal || projection.IsDecal) { RefuseLockedPixels(layer, erase: false); RefuseLockedTransparency(layer); } else RefuseLockedAttributes(layer);
            var command = old.IsDecal && projection.IsDecal && !HasAnyActiveFilters(layer)
                ? DecalScoped(layer, () => { layer.Projection = projection; FillChanged(layer); }, () => { layer.Projection = old; FillChanged(layer); }, 160)
                : LayerScoped(layer, null, () => { layer.Projection = projection; FillChanged(layer); }, () => { layer.Projection = old; FillChanged(layer); }, 160);
            Execute(command, coalesce ? (object)("fillProjection", id) : null);
        }

        static bool HasAnyActiveFilters(PaintLayer layer) { foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) if (layer.HasActiveFilters(c)) return true; return false; }

        /// <summary>A history command for an edit of a decal that only moves where it shows: marks the tiles the decal may cover before and
        /// after it (in the channels it has values for) instead of the whole canvas; every tile when that cannot be told.</summary>
        DelegateCommand DecalScoped(PaintLayer layer, Action apply, Action revert, long cost)
        {
            void Run(Action change)
            {
                var tiles = new HashSet<TileCoord>();
                bool known = AddDecalTiles(layer, tiles);
                change();
                known = known && AddDecalTiles(layer, tiles);
                if (!known) MarkLayerChanged(layer, null);
                else foreach (var channel in layer.FillValues.Keys) foreach (var coord in tiles) MarkTileChanged(channel, coord);
                MarkClippedLayersChanged();
            }
            return new DelegateCommand(() => Run(apply), () => Run(revert), cost);
        }
        /// <summary>The tiles a decal layer may cover now, from the maps as last resolved (what the display shows); false when not known.</summary>
        bool AddDecalTiles(PaintLayer layer, ICollection<TileCoord> into)
        {
            if (!layer.IsDecal || !generatorResolved) return false;
            var positions = generatorMaps[(int)MeshMapKind.Position];
            if (FillImageSampler.PlacementProblem(layer.Projection, Width, Height, generatorMaps, generatorReasons, generatorFrame) != null) return true; // 置けない: どこにも出ない
            return FillImageSampler.AddDecalTiles(layer.Projection, positions, generatorFrame, DecalTileBounds(positions), into);
        }

        PositionTileBounds decalTileBounds;
        /// <summary>The Position map's per-tile bounds (made once per map and kept while it is the one resolved).</summary>
        internal PositionTileBounds DecalTileBounds(BakedMeshMap positions)
        {
            if (positions == null) return null;
            var bounds = decalTileBounds;
            if (bounds == null || !ReferenceEquals(bounds.Map, positions) || bounds.TileSize != TileSize) decalTileBounds = bounds = PositionTileBounds.Of(positions, TileSize);
            return bounds;
        }

        /// <summary>The layer's fill content changed: a new fill revision (unique in the document) and its evaluated tiles and sampler forgotten.</summary>
        internal void FillChanged(PaintLayer layer)
        {
            layer.FillRevision = ++filterRevisionCounter;
            foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) { samplers.Remove((layer.Id, c)); filterEngine?.Forget(layer.Id, (int)c); }
        }

        /// <summary>For loaders (native, resampling, duplicate): the fill images and projection without history or checks against the
        /// resources (an ID that is not in the project shows the fill value and is listed by <see cref="MissingFillImages"/>).</summary>
        internal void SetFillImagesForLoad(PaintLayer layer, IEnumerable<KeyValuePair<PaintChannel, Guid>> images, FillProjection projection)
        {
            if (layer.Kind != LayerKind.Fill) throw new InvalidOperationException("Only fill layers have images.");
            foreach (var image in images)
            {
                if (!layer.FillValues.ContainsKey(image.Key)) throw new InvalidOperationException("The " + image.Key + " image of '" + layer.Name + "' has no fill value.");
                if (image.Value == Guid.Empty) throw new InvalidOperationException("An image resource ID must not be empty.");
                layer.SetFillImageInternal(image.Key, image.Value);
            }
            layer.Projection = projection ?? FillProjection.Default;
            FillChanged(layer);
        }

        // ───────────── status ─────────────

        /// <summary>What a fill layer's image channel shows now (and why it shows its fill value instead, if it does).</summary>
        public FillImageStatus GetFillImageStatus(Guid layerId, PaintChannel channel)
        {
            PaintLayer.ValidateChannel(channel);
            var layer = GetLayer(layerId);
            if (layer.Kind != LayerKind.Fill || !layer.FillImages.TryGetValue(channel, out var id)) throw new ArgumentException("'" + layer.Name + "' has no image in the " + channel + " channel.", nameof(channel));
            PollGeneratorInputs(); // マップと画像の今を読んでから
            var resolved = Resolved(id); var sampler = FillSampler(layer, channel);
            var conversion = resolved.Content == null ? FillImageConversion.None : FillImageColor.ConversionFor(resolved.ColorSpace, channel);
            return new FillImageStatus(id, resolved.Resource, sampler.Reason, conversion, FillImageColor.UsesLuminance(channel), sampler.Mips?.Levels ?? 0, sampler.Mips?.Bytes ?? 0);
        }

        /// <summary>Why a decal layer is not shown now (its maps are missing, stale or of another size, the model root is not known), or
        /// null when it is placed. Its channels are then transparent everywhere.</summary>
        public string GetDecalProblem(Guid layerId)
        {
            var layer = GetLayer(layerId);
            if (!layer.IsDecal) throw new ArgumentException("'" + layer.Name + "' is not a decal.", nameof(layerId));
            PollGeneratorInputs();
            var maps = GeneratorMapSnapshot(out _, out var frame);
            return FillImageSampler.PlacementProblem(layer.Projection, Width, Height, maps, generatorReasons, frame);
        }

        /// <summary>Where a decal reaches, for a view: a width × height grid over the texture set (row-major from the bottom row; each cell
        /// reads the texel at its centre) of the decal's coverage, 0..255 (its box, depth and facing; not its image). Null when the decal is not
        /// placed now (<see cref="GetDecalProblem"/>).</summary>
        public byte[] DecalCoverage(Guid layerId, int width, int height)
        {
            var layer = GetLayer(layerId);
            if (!layer.IsDecal) throw new ArgumentException("'" + layer.Name + "' is not a decal.", nameof(layerId));
            if (width < 1 || height < 1 || width > Width || height > Height) throw new ArgumentOutOfRangeException(nameof(width), "The grid must be 1 to the texture set's size on each side.");
            PollGeneratorInputs();
            var maps = GeneratorMapSnapshot(out _, out var frame);
            var sampler = FillImageSampler.BindDecal(layer.Projection, null, null, null, new Rgba32(255, 255, 255, 255), Width, Height, maps, generatorReasons, frame, DecalTileBounds);
            if (!sampler.Placed) return null;
            var result = new byte[width * height]; int w = Width, h = Height;
            CoreParallelism.For(height, CoreParallelism.Degree, j =>
            {
                int y = (int)((j + .5) * h / height);
                for (int i = 0; i < width; i++) result[j * width + i] = MathUtil.ToByte(sampler.DecalCoverage((int)((i + .5) * w / width), y));
            });
            return result;
        }

        /// <summary>Baking (merging, flattening) a decal that is not shown now would leave it out: refused with the reason.</summary>
        void RefuseUnplacedDecal(PaintLayer layer)
        {
            if (!layer.IsDecal || !layer.FillValues.Keys.Any(layer.IsChannelEnabled)) return;
            string problem = GetDecalProblem(layer.Id);
            if (problem != null) throw new InvalidOperationException("'" + layer.Name + "': the decal cannot be baked because it is not shown now (" + problem + ") Baking would leave it out. Bake the mesh maps (Position and World Normal) first. Nothing was changed.");
        }

        /// <summary>One line per fill image channel (enabled, layer visible) that shows its fill value instead of its image now, naming the
        /// layer, the channel and the reason, and one per decal that is not shown (it would be missing from the images). Exports and saves
        /// list these instead of writing a composite without the image silently.</summary>
        public IReadOnlyList<string> InactiveFillImages()
        {
            var notes = new List<string>();
            if (!HasFillImages) return notes;
            PollGeneratorInputs();
            foreach (var layer in layers)
                if (layer.Kind == LayerKind.Fill)
                {
                    if (layer.IsDecal && layer.FillValues.Keys.Any(layer.IsChannelEnabled))
                    {
                        string problem = GetDecalProblem(layer.Id);
                        if (problem != null) { notes.Add("'" + layer.Name + "' (decal): the decal is not shown: " + problem); continue; }
                    }
                    foreach (var entry in layer.FillImages.OrderBy(e => e.Key))
                    {
                        if (!layer.IsChannelEnabled(entry.Key)) continue;
                        var sampler = FillSampler(layer, entry.Key);
                        if (!sampler.Active) notes.Add("'" + layer.Name + "' (" + entry.Key + "): the image is not projected: " + sampler.Reason);
                    }
                }
            return notes;
        }

        /// <summary>The image channels whose resource ID the connected resources do not have (each "'layer' (channel): id"), for the notice
        /// when a project is opened. Empty when every ID resolves.</summary>
        public IReadOnlyList<string> MissingFillImages()
        {
            var notes = new List<string>();
            PollGeneratorInputs(); // リソースの今を読んでから
            foreach (var layer in layers)
                if (layer.Kind == LayerKind.Fill)
                    foreach (var entry in layer.FillImages.OrderBy(e => e.Key))
                        if (Resolved(entry.Value).Content == null) notes.Add("'" + layer.Name + "' (" + entry.Key + "): " + entry.Value);
            return notes;
        }

        /// <summary>The layers whose fill reads the resource now ("'name' (Color, Roughness)"), for the resources' usage probe. Empty when none.</summary>
        public IReadOnlyList<string> LayersUsingResource(Guid resourceId)
        {
            var users = new List<string>();
            foreach (var layer in layers)
            {
                if (layer.Kind != LayerKind.Fill) continue;
                var channels = layer.FillImages.Where(e => e.Value == resourceId).Select(e => e.Key).OrderBy(c => c).ToList();
                if (channels.Count > 0) users.Add("'" + layer.Name + "' (" + string.Join(", ", channels) + ")");
            }
            return users;
        }

        // ───────────── resolving ─────────────

        /// <summary>Resolves the images again when the resources' revision changed (or another provider was set). Layers reading an ID whose
        /// image, colour space or presence changed are reported as changed. True when any did.</summary>
        internal bool PollImageResources()
        {
            if (!HasFillImages) { resolvedImages.Clear(); imagesPolled = false; return false; } // 次に使うときに読み直す
            long seen = ResourcesRevision();
            if (imagesPolled && seen == imageResourcesSeen) return false;
            imageResourcesSeen = seen; imagesPolled = true;
            var ids = new HashSet<Guid>(resolvedImages.Keys);
            foreach (var layer in layers) if (layer.Kind == LayerKind.Fill) foreach (var id in layer.FillImages.Values) ids.Add(id);
            var changed = new HashSet<Guid>();
            foreach (var id in ids)
            {
                var now = Resolve(id);
                if (resolvedImages.TryGetValue(id, out var before) && Same(before, now)) { before.Resource = now.Resource; continue; }
                bool known = resolvedImages.ContainsKey(id);
                resolvedImages[id] = now;
                if (known) changed.Add(id);
            }
            if (changed.Count == 0) return false;
            bool any = false;
            foreach (var layer in layers)
            {
                if (layer.Kind != LayerKind.Fill || !layer.FillImages.Values.Any(changed.Contains)) continue;
                FillChanged(layer); MarkLayerChanged(layer, null); any = true;
            }
            if (any) { MarkClippedLayersChanged(); inputsRevision++; }
            return any;
        }
        long ResourcesRevision()
        {
            if (imageResources == null) return long.MinValue;
            try { return imageResources.Revision; }
            catch (Exception) { return long.MinValue + 1; }
        }
        static bool Same(ResolvedImage a, ResolvedImage b)
            => a.Content == null ? b.Content == null && a.Reason == b.Reason : b.Content != null && a.Content.Hash == b.Content.Hash && a.ColorSpace == b.ColorSpace;

        ResolvedImage Resolved(Guid id)
        {
            if (!resolvedImages.TryGetValue(id, out var resolved)) resolvedImages[id] = resolved = Resolve(id);
            return resolved;
        }
        ResolvedImage Resolve(Guid id)
        {
            if (imageResources == null) return new ResolvedImage { Reason = "No image resources are connected to this document (open it in the YoluPainter window, where the project's resources are)." };
            try
            {
                if (!imageResources.TryGetImage(id, out var image) || image == null) return new ResolvedImage { Reason = "The project has no image resource " + id + " (it was removed, or the file is incomplete). Choose another image." };
                return new ResolvedImage { Resource = image, Content = image.Content, ColorSpace = image.ColorSpace };
            }
            catch (Exception ex) { return new ResolvedImage { Reason = "Reading the image resource failed: " + ex.Message }; }
        }

        /// <summary>The bound sampler of an image channel, or of any channel with a value of a decal (made again when the layer's fill
        /// revision or the maps it reads changed).</summary>
        internal FillImageSampler FillSampler(PaintLayer layer, PaintChannel channel)
        {
            bool hasImage = layer.FillImages.TryGetValue(channel, out var id);
            long mapsRevision = 0; IReadOnlyList<BakedMeshMap> maps = null; GeneratorModelFrame frame = null;
            if (layer.Projection.ReadsMeshMaps) maps = GeneratorMapSnapshot(out mapsRevision, out frame);
            if (samplers.TryGetValue((layer.Id, channel), out var bound) && bound.FillRevision == layer.FillRevision && bound.MapsRevision == mapsRevision) return bound.Sampler;
            layer.FillValues.TryGetValue(channel, out var fallback);
            FillImageSampler sampler;
            if (layer.Projection.IsDecal) sampler = DecalSampler(layer, channel, hasImage ? id : (Guid?)null, fallback, maps, frame);
            else
            {
                var resolved = Resolved(id);
                if (resolved.Content == null) sampler = FillImageSampler.Constant(resolved.Reason, fallback);
                else
                {
                    var chain = Chain(resolved.Content, FillImageColor.ConversionFor(resolved.ColorSpace, channel), FillImageColor.UsesLuminance(channel), out string why);
                    sampler = chain == null ? FillImageSampler.Constant(why, fallback) : FillImageSampler.Bind(layer.Projection, chain, fallback, Width, Height, maps, generatorReasons, frame);
                }
            }
            samplers[(layer.Id, channel)] = new BoundSampler { FillRevision = layer.FillRevision, MapsRevision = mapsRevision, Sampler = sampler };
            return sampler;
        }

        /// <summary>A decal's channel: its own image (or its value, with the reason when the image cannot be read) and the shape image, the
        /// image of the first channel in channel order that has one, when that is another channel.</summary>
        FillImageSampler DecalSampler(PaintLayer layer, PaintChannel channel, Guid? image, Rgba32 value, IReadOnlyList<BakedMeshMap> maps, GeneratorModelFrame frame)
        {
            ImageMipChain own = null, shape = null; string ownReason = null;
            if (image.HasValue) own = ChainOf(image.Value, channel, out ownReason);
            PaintChannel shapeChannel = channel; bool any = false;
            foreach (var c in layer.FillImages.Keys) if (!any || c < shapeChannel) { shapeChannel = c; any = true; }
            if (any && shapeChannel != channel) shape = ChainOf(layer.FillImages[shapeChannel], shapeChannel, out _);
            return FillImageSampler.BindDecal(layer.Projection, own, ownReason, shape, value, Width, Height, maps, generatorReasons, frame, DecalTileBounds);
        }
        /// <summary>The mipmap of the image an ID resolves to as a channel reads it, or null with the reason.</summary>
        ImageMipChain ChainOf(Guid id, PaintChannel channel, out string reason)
        {
            var resolved = Resolved(id);
            if (resolved.Content == null) { reason = resolved.Reason; return null; }
            return Chain(resolved.Content, FillImageColor.ConversionFor(resolved.ColorSpace, channel), FillImageColor.UsesLuminance(channel), out reason);
        }

        ImageMipChain Chain(ImageContent content, FillImageConversion conversion, bool luminance, out string reason)
        {
            reason = null; var key = (content.Hash, conversion, luminance);
            if (chains.TryGetValue(key, out var cached)) { cached.Used = ++chainClock; return cached.Chain; }
            long need = ImageMipChain.ExtraBytes(content.Width, content.Height);
            if (need > fillImageCacheBudget)
            {
                reason = "The image's mipmaps need " + Mib(need) + " MiB, more than the fill image budget (" + Mib(fillImageCacheBudget) + " MiB, PaintDocument.FillImageCacheBudgetBytes).";
                return null;
            }
            var chain = ImageMipChain.Build(content, conversion, luminance);
            chains.Add(key, new CachedChain { Chain = chain, Used = ++chainClock }); chainBytes += chain.Bytes;
            TrimChains(key);
            return chain;
        }
        /// <summary>Drops least recently used mipmaps (and the samplers that read them) until within the budget, keeping <paramref name="keep"/>.</summary>
        void TrimChains((string, FillImageConversion, bool)? keep)
        {
            if (chainBytes <= fillImageCacheBudget) return;
            foreach (var entry in chains.OrderBy(e => e.Value.Used).ToList())
            {
                if (chainBytes <= fillImageCacheBudget) break;
                if (keep.HasValue && entry.Key.Equals(keep.Value)) continue;
                chains.Remove(entry.Key); chainBytes -= entry.Value.Chain.Bytes;
                foreach (var s in samplers.Where(s => ReferenceEquals(s.Value.Sampler.Mips, entry.Value.Chain) || ReferenceEquals(s.Value.Sampler.Shape, entry.Value.Chain)).Select(s => s.Key).ToList()) samplers.Remove(s);
            }
        }
    }
}
