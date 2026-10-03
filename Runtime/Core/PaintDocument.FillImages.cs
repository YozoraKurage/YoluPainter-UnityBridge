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

        /// <summary>True when a fill layer has an image channel.</summary>
        public bool HasFillImages { get { foreach (var layer in layers) if (layer.Kind == LayerKind.Fill && layer.FillImages.Count > 0) return true; return false; } }
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

        /// <summary>Replaces a fill layer's projection, as one undo step (slider and gizmo drags coalesce). With images it changes pixels, so
        /// it is refused under Lock Image Pixels, Lock Transparent Pixels and Lock All; without images only under Lock All.</summary>
        public void SetFillProjection(Guid id, FillProjection projection, bool coalesce = false)
        {
            EnsureNoStroke(); if (projection == null) throw new ArgumentNullException(nameof(projection));
            string why = projection.Refusal(); if (why != null) throw new ArgumentOutOfRangeException(nameof(projection), why);
            var layer = GetLayer(id);
            if (layer.Kind != LayerKind.Fill) throw new InvalidOperationException("Only fill layers have a projection.");
            var old = layer.Projection; if (old.Equals(projection)) return;
            if (layer.FillImages.Count > 0) { RefuseLockedPixels(layer, erase: false); RefuseLockedTransparency(layer); } else RefuseLockedAttributes(layer);
            Execute(LayerScoped(layer, null, () => { layer.Projection = projection; FillChanged(layer); }, () => { layer.Projection = old; FillChanged(layer); }, 160),
                coalesce ? (object)("fillProjection", id) : null);
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

        /// <summary>One line per fill image channel (enabled, layer visible) that shows its fill value instead of its image now, naming the
        /// layer, the channel and the reason. Exports and saves list these instead of writing a composite without the image silently.</summary>
        public IReadOnlyList<string> InactiveFillImages()
        {
            var notes = new List<string>();
            if (!HasFillImages) return notes;
            PollGeneratorInputs();
            foreach (var layer in layers)
                if (layer.Kind == LayerKind.Fill)
                    foreach (var entry in layer.FillImages.OrderBy(e => e.Key))
                    {
                        if (!layer.IsChannelEnabled(entry.Key)) continue;
                        var sampler = FillSampler(layer, entry.Key);
                        if (!sampler.Active) notes.Add("'" + layer.Name + "' (" + entry.Key + "): the image is not projected: " + sampler.Reason);
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

        /// <summary>The bound sampler of an image channel (made again when the layer's fill revision or the maps it reads changed).</summary>
        internal FillImageSampler FillSampler(PaintLayer layer, PaintChannel channel)
        {
            var id = layer.FillImages[channel];
            long mapsRevision = 0; IReadOnlyList<BakedMeshMap> maps = null; GeneratorModelFrame frame = null;
            if (layer.Projection.ReadsMeshMaps) maps = GeneratorMapSnapshot(out mapsRevision, out frame);
            if (samplers.TryGetValue((layer.Id, channel), out var bound) && bound.FillRevision == layer.FillRevision && bound.MapsRevision == mapsRevision) return bound.Sampler;
            layer.FillValues.TryGetValue(channel, out var fallback);
            var resolved = Resolved(id);
            FillImageSampler sampler;
            if (resolved.Content == null) sampler = FillImageSampler.Constant(resolved.Reason, fallback);
            else
            {
                var chain = Chain(resolved.Content, FillImageColor.ConversionFor(resolved.ColorSpace, channel), FillImageColor.UsesLuminance(channel), out string why);
                sampler = chain == null ? FillImageSampler.Constant(why, fallback) : FillImageSampler.Bind(layer.Projection, chain, fallback, Width, Height, maps, generatorReasons, frame);
            }
            samplers[(layer.Id, channel)] = new BoundSampler { FillRevision = layer.FillRevision, MapsRevision = mapsRevision, Sampler = sampler };
            return sampler;
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
                foreach (var s in samplers.Where(s => ReferenceEquals(s.Value.Sampler.Mips, entry.Value.Chain)).Select(s => s.Key).ToList()) samplers.Remove(s);
            }
        }
    }
}
