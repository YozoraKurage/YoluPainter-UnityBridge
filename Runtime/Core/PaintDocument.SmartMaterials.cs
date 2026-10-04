using System;
using System.Collections.Generic;
using System.Linq;
using Yozolab.YoluPainter.Core.Paths;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>
    /// Smart materials and smart masks (<see cref="SmartMaterial"/>): saving chosen layers (or a layer's mask) apart from the document, and
    /// placing them back into any document as one undo step. See SmartMaterial for what is kept.
    /// </summary>
    public sealed partial class PaintDocument
    {
        /// <summary>The most layers a native document holds (the reader's limit; placing refuses to go past it).</summary>
        public const int MaxLayers = Persistence.DocumentBinary.MaxLayers;

        // ───────── 保存する ─────────

        /// <summary>
        /// A smart material from the chosen layers (a chosen group with everything in it; a layer inside a chosen group goes with the group),
        /// in their order, at this document's size. Changes nothing here. Paint layers keep their pixels (shared copy-on-write, so saving costs
        /// no time per pixel). Paths on the model become plain pixels (their triangles belong to this model), and generators keep no pin to
        /// the bake they read (pinned ones are listed in <see cref="SmartMaterial.Repin"/>). Chosen layers whose group is not chosen go to the
        /// top level. <paramref name="images"/> looks up the project resources the layers refer to (<paramref name="references"/>, by default
        /// the fill layers' images, <see cref="ResourcesOf"/>), which are kept with it.
        /// </summary>
        public SmartMaterial CaptureSmartMaterial(IEnumerable<Guid> layerIds, string name, IImageResources images = null, Func<PaintLayer, IEnumerable<Guid>> references = null)
        {
            EnsureNoStroke(); SmartMaterial.CheckName(name);
            var members = TopmostOf(layerIds ?? throw new ArgumentNullException(nameof(layerIds)));
            if (members.Count == 0) throw new SmartRefusedException(SmartRefusal.NothingToSave, "No layer is chosen to save as a smart material.");
            var fragment = NewFragment();
            var notes = new List<string>(); var repin = new List<Guid>(); var used = new List<Guid>();
            var ids = new Dictionary<Guid, Guid>(); var clones = new List<PaintLayer>(); var anchors = new Dictionary<Guid, Guid>();
            foreach (var m in members) foreach (var l in Block(m)) ids[l.Id] = Guid.NewGuid();
            foreach (var m in members)
                foreach (var l in Block(m))
                {
                    var copy = fragment.CloneLayer(l, ids[l.Id], l.Name, anchors);
                    copy.ParentId = l == m ? Guid.Empty : ids[l.ParentId];
                    if (copy.Path is SurfacePath) { copy.Path = null; notes.Add("The path on the model of '" + l.Name + "' became plain pixels (a path on a model belongs to that model's triangles)."); }
                    StripPins(copy.FilterList, repin); if (copy.Mask != null) StripPins(copy.Mask.FilterList, repin);
                    foreach (var id in (references ?? ResourcesOf)(l)) if (!used.Contains(id)) used.Add(id);
                    clones.Add(copy);
                }
            fragment.RemapAnchorReferences(clones, anchors); // 選んだ層の中の Anchor を読む段は、断片の Anchor を読む
            fragment.layers.AddRange(clones);
            fragment.ValidateStructure(); fragment.ClearHistory();
            return new SmartMaterial(SmartKind.Material, name, fragment, repin, CollectImages(used, images, notes), notes);
        }

        /// <summary>A smart mask from a layer's mask: its pixels, parameters (on, inverted, density) and filters and generators, at this
        /// document's size. Changes nothing here. Pinned generators are handled like <see cref="CaptureSmartMaterial"/>'s.</summary>
        public SmartMaterial CaptureSmartMask(Guid layerId, string name)
        {
            EnsureNoStroke(); SmartMaterial.CheckName(name);
            var layer = GetLayer(layerId);
            if (layer.Mask == null) throw new SmartRefusedException(SmartRefusal.NoMask, "'" + layer.Name + "' has no mask to save as a smart mask.");
            var fragment = NewFragment();
            var holder = new PaintLayer(fragment, name, Guid.NewGuid(), LayerKind.Fill);
            var anchors = new Dictionary<Guid, Guid>();
            holder.Mask = fragment.CloneMask(layer.Mask, holder, anchors);
            fragment.RemapAnchorReferences(new[] { holder }, anchors);
            var repin = new List<Guid>(); StripPins(holder.Mask.FilterList, repin);
            fragment.layers.Add(holder); fragment.ClearHistory();
            return new SmartMaterial(SmartKind.Mask, name, fragment, repin, null);
        }

        PaintDocument NewFragment()
            => new PaintDocument(Width, Height, TileSize, 0) { sourceBudgetBytes = long.MaxValue, filterWorkingBudget = filterWorkingBudget, filterBlockPixels = filterBlockPixels };

        static void StripPins(List<FilterEffect> stack, List<Guid> repin)
        {
            for (int i = 0; i < stack.Count; i++)
            {
                var e = stack[i];
                if (!e.Settings.IsGenerator || e.Settings.Generator.Pins.Count == 0) continue;
                stack[i] = e.With(settings: e.Settings.WithGenerator(e.Settings.Generator.WithoutPins()));
                repin.Add(e.Id);
            }
        }

        /// <summary>The project resources a layer's settings refer to, by ID: a fill layer's channel images (<see cref="PaintLayer.FillImages"/>),
        /// in channel order, each once. Decals and other stages that refer to resources add theirs here and in <see cref="RemapResources"/>.</summary>
        internal static IEnumerable<Guid> ResourcesOf(PaintLayer layer)
        {
            if (layer.Kind != LayerKind.Fill || layer.FillImages.Count == 0) return Enumerable.Empty<Guid>();
            return layer.FillImages.OrderBy(e => e.Key).Select(e => e.Value).Distinct().ToList();
        }
        /// <summary>Points a placed copy's references at the IDs the images got in the project (see <see cref="ResourcesOf"/>); an ID that is not
        /// in the map is kept as it is (it then shows its fill value and is listed as missing, like an opened file's).</summary>
        void RemapResources(PaintLayer copy, IReadOnlyDictionary<Guid, Guid> ids)
        {
            if (copy.Kind != LayerKind.Fill || copy.FillImages.Count == 0) return;
            bool changed = false;
            foreach (var entry in copy.FillImages.ToList())
                if (ids.TryGetValue(entry.Value, out var to) && to != entry.Value) { copy.SetFillImageInternal(entry.Key, to); changed = true; }
            if (changed) FillChanged(copy);
        }

        static List<SmartImage> CollectImages(List<Guid> used, IImageResources images, List<string> notes)
        {
            var result = new List<SmartImage>();
            foreach (var id in used)
            {
                if (images != null && images.TryGetImage(id, out var image)) result.Add(new SmartImage(id, image.Name, image.Content, image.ColorSpace));
                else notes.Add("A layer refers to a resource this project does not have (" + id + "); the smart material is saved without it.");
            }
            return result;
        }

        // ───────── 置く ─────────

        /// <summary>
        /// Places a smart material's layers into this document as one undo step and returns what was done. A smart material with several
        /// top-level layers goes into a new pass-through group named after it (<see cref="SmartPlacement.GroupName"/>); one top-level layer
        /// (often a group) goes as it is. Everything gets new IDs. Saved at another size, it is resampled to this one first
        /// (<see cref="Resampled"/>: pixels, masks, filter radii, 2D paths). Channels the texture set does not use
        /// (<see cref="SmartPlacement.Channels"/>) are switched off on the placed layers and listed. Generators that were pinned are pinned to
        /// the maps this document reads now when they are usable (else they follow the maps, and the reason is given); generators without maps
        /// pass their input through as everywhere (<see cref="SmartPlaceResult.InactiveGenerators"/>).
        /// <para>Saved with another tile size, the pixels are copied into this document's tiles as they are. Refused before anything changes
        /// (<see cref="SmartRefusedException"/>): a smart mask, more layers than
        /// <see cref="MaxLayers"/>, more layer pixels than the room left under <see cref="SourceBudgetBytes"/>, a filter whose reach or
        /// working memory this document refuses, or a size it cannot be resampled to.</para>
        /// </summary>
        public SmartPlaceResult PlaceSmartMaterial(SmartMaterial material, SmartPlacement placement = null)
        {
            EnsureNoStroke(); if (material == null) throw new ArgumentNullException(nameof(material));
            placement = placement ?? new SmartPlacement();
            if (material.Kind != SmartKind.Material) throw new SmartRefusedException(SmartRefusal.WrongKind, "\"" + material.Name + "\" is a smart mask. Put it on a layer's mask.");
            ResolveParent(placement, out Guid parentId, out int position);
            if (material.Repin.Count > 0) ResolveGeneratorInputs(InputsRevision()); // Generator の無い文書は、入力を読んでいないか古いことがある
            var notes = new List<string>();
            var fragment = FragmentAtSize(material, placement.Resampling, notes, out bool resampled);
            var tops = fragment.Layers.Where(l => l.ParentId == Guid.Empty).ToList();
            bool wrap = tops.Count > 1;
            int adding = fragment.Layers.Count + (wrap ? 1 : 0);
            if (layers.Count + adding > MaxLayers) throw new SmartRefusedException(SmartRefusal.LayerLimit, "\"" + material.Name + "\" has " + adding + " layer(s); the texture set would hold more than " + MaxLayers + " layers. Nothing was placed.");

            // 写しを作る（まだ文書に入れない）。ID は全部新しく、上の層は置き場所の親に（いくつもあれば新しいグループに）
            var ids = new Dictionary<Guid, Guid>(); foreach (var l in fragment.Layers) ids[l.Id] = FreshLayerId(ids.Values);
            PaintLayer group = null;
            if (wrap)
            {
                string groupName = placement.GroupName ?? material.Name;
                group = new PaintLayer(this, groupName, FreshLayerId(ids.Values), LayerKind.Group) { BlendMode = LayerBlendMode.PassThrough, ParentId = parentId };
            }
            var keep = placement.Channels == null ? null : new HashSet<PaintChannel>(placement.Channels);
            var switchedOff = new SortedSet<PaintChannel>(); var unpinned = new List<string>(); int pinned = 0;
            var clones = new List<PaintLayer>(); var anchors = new Dictionary<Guid, Guid>();
            foreach (var l in fragment.Layers)
            {
                var copy = CloneLayer(l, ids[l.Id], l.Name, anchors);
                copy.ParentId = l.ParentId != Guid.Empty ? ids[l.ParentId] : wrap ? group.Id : parentId;
                if (keep != null)
                    foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel)))
                        if (copy.IsChannelEnabled(c) && !keep.Contains(c)) { copy.Enable(c, false); switchedOff.Add(c); }
                pinned += Repin(l.FilterList, copy.FilterList, material.Repin, unpinned);
                if (l.Mask != null) pinned += Repin(l.Mask.FilterList, copy.Mask.FilterList, material.Repin, unpinned);
                if (placement.ResourceIds != null) RemapResources(copy, placement.ResourceIds);
                clones.Add(copy);
            }
            if (wrap) clones.Add(group); // グループの記録は中身の上
            RemapAnchorReferences(clones, anchors); // スマートマテリアルの中の Anchor を読む段は、置いた Anchor を読む
            CheckFilters(clones, material.Name);
            long bytes = 0; foreach (var c in clones) bytes += c.AllocatedBytes;
            long room = sourceBudgetBytes - AllocatedBytes;
            if (bytes > room) throw new SmartRefusedException(SmartRefusal.Budget, "\"" + material.Name + "\" needs " + Mib(bytes) + " MiB of layer pixels; " + Mib(Math.Max(0, room)) + " MiB are left under the layer pixel budget. Nothing was placed.", bytes, Math.Max(0, room));

            var before = SnapshotStructure();
            layers.InsertRange(InsertIndex(parentId, position), clones);
            var after = SnapshotStructure(); RestoreStructure(before);
            var swap = SwapStructure(before, after, 128 + bytes);
            bool generators = clones.Any(LayerHasGenerator);
            Execute(new DelegateCommand(() => { swap.Apply(); if (generators) PollGeneratorInputs(); }, () => { swap.Revert(); if (generators) PollGeneratorInputs(); }, swap.ByteCost));
            var top = wrap ? group : clones.Last(c => c.ParentId == parentId);
            return new SmartPlaceResult
            {
                LayerId = top.Id, Layers = clones.Select(c => c.Id).ToList().AsReadOnly(), Resampled = resampled,
                SwitchedOff = switchedOff.ToList().AsReadOnly(), Pinned = pinned, NotPinned = unpinned.Count,
                InactiveGenerators = InactiveReasons(clones), Notes = notes.Concat(unpinned).ToList().AsReadOnly(),
            };
        }

        /// <summary>
        /// Puts a smart mask on a layer as one undo step: the layer gets a copy of its mask (pixels resampled to this size when it was saved at
        /// another, parameters, filters and generators with new IDs; pinned generators pinned again as in <see cref="PlaceSmartMaterial"/>),
        /// replacing the mask it had (<see cref="SmartPlaceResult.ReplacedMask"/>; undo brings it back). Refused before anything changes: a
        /// smart material, Lock All on the layer or its group (<see cref="LayerLockedException"/>), the layer pixel budget, a filter this
        /// document refuses.
        /// </summary>
        public SmartPlaceResult ApplySmartMask(SmartMaterial mask, Guid layerId, CanvasResampling? resampling = null)
        {
            EnsureNoStroke(); if (mask == null) throw new ArgumentNullException(nameof(mask));
            var layer = GetLayer(layerId);
            if (mask.Kind != SmartKind.Mask) throw new SmartRefusedException(SmartRefusal.WrongKind, "\"" + mask.Name + "\" is a smart material.");
            RefuseLockedAttributes(layer); // マスクを変えるのは、すべてのロックでだけ断る（AddLayerMask と同じ）
            if (mask.Repin.Count > 0) ResolveGeneratorInputs(InputsRevision());
            var notes = new List<string>();
            var fragment = FragmentAtSize(mask, resampling, notes, out bool resampled);
            var source = fragment.Layers[0].Mask;
            var next = CloneMask(source, layer);
            var unpinned = new List<string>();
            int pinned = Repin(source.FilterList, next.FilterList, mask.Repin, unpinned);
            var old = layer.Mask;
            CheckFilterChain(next.ActiveChain(), mask.Name + " (mask)");
            long bytes = next.Surface.AllocatedBytes, freed = old == null ? 0 : old.Surface.AllocatedBytes, room = sourceBudgetBytes - AllocatedBytes;
            if (bytes - freed > room) throw new SmartRefusedException(SmartRefusal.Budget, "\"" + mask.Name + "\" needs " + Mib(bytes) + " MiB of mask pixels; " + Mib(Math.Max(0, room + freed)) + " MiB are left under the layer pixel budget. Nothing was changed.", bytes, Math.Max(0, room + freed));
            void Switch(RasterMask to, RasterMask from)
            {
                EnsureSourceGrowth((to == null ? 0 : to.Surface.AllocatedBytes) - (from == null ? 0 : from.Surface.AllocatedBytes));
                layer.Mask = to;
                FilterEvaluator.Forget(layer.Id, FilterEngine.MaskKey);
                if (to != null && to.FilterList.Any(f => f.Settings.IsGenerator)) PollGeneratorInputs();
            }
            Execute(LayerScoped(layer, null, () => Switch(next, old), () => Switch(old, next), 64 + bytes));
            return new SmartPlaceResult
            {
                LayerId = layer.Id, Layers = new[] { layer.Id }, Resampled = resampled, SwitchedOff = new PaintChannel[0], Pinned = pinned, NotPinned = unpinned.Count,
                InactiveGenerators = InactiveReasons(new[] { layer }), ReplacedMask = old != null, Notes = notes.Concat(unpinned).ToList().AsReadOnly(),
            };
        }

        /// <summary>The fragment at this document's size and tile size (itself when both are equal; never changed).</summary>
        PaintDocument FragmentAtSize(SmartMaterial material, CanvasResampling? resampling, List<string> notes, out bool resampled)
        {
            var fragment = material.Fragment;
            resampled = fragment.Width != Width || fragment.Height != Height;
            if (!resampled && fragment.TileSize == TileSize) return fragment;
            // 大きさが同じでタイルの大きさだけが違えば、最近傍で画素をそのまま写す
            var how = !resampled ? CanvasResampling.Nearest : resampling ?? ImageResampling.Automatic(fragment.Width, fragment.Height, Width, Height);
            long room = Math.Max(0, sourceBudgetBytes - AllocatedBytes);
            ResampledDocument copy;
            try { copy = fragment.Resampled(Width, Height, how, room, TileSize); }
            catch (InvalidOperationException ex)
            {
                bool budget = fragment.AllocatedBytes > 0 && ex.Message.IndexOf("budget", StringComparison.OrdinalIgnoreCase) >= 0 && ex.Message.IndexOf("layer pixel", StringComparison.OrdinalIgnoreCase) >= 0;
                throw new SmartRefusedException(budget ? SmartRefusal.Budget : SmartRefusal.Size, "\"" + material.Name + "\" (" + fragment.Width + " × " + fragment.Height + ") cannot be placed at " + Width + " × " + Height + ": " + ex.Message, 0, room, ex);
            }
            notes.AddRange(copy.Notes);
            return copy.Document;
        }

        /// <summary>Pins the copies of stages that were pinned where they were saved to the maps this document reads now (when every map they
        /// use is usable, like the Generator section's "Only this bake"). Returns how many were pinned; the others' reasons go to unpinned.</summary>
        int Repin(List<FilterEffect> source, List<FilterEffect> copies, IReadOnlyCollection<Guid> repin, List<string> unpinned)
        {
            int pinned = 0;
            for (int i = 0; i < source.Count; i++)
            {
                if (!repin.Contains(source[i].Id)) continue;
                var copy = copies[i]; var g = copy.Settings.Generator.WithoutPins();
                var status = GetGeneratorStatus(g);
                if (status.Maps.Count > 0 && status.Maps.All(m => m.Usable))
                {
                    foreach (var use in status.Maps) g = g.WithPin(use.Kind, use.Map.Provenance.ConditionKey);
                    copies[i] = copy.With(settings: copy.Settings.WithGenerator(g)); pinned++;
                }
                else unpinned.Add(copy.Settings.Name + " was pinned to a bake where it was saved; it follows this texture set's maps until they are baked (" + status.Reason + ")");
            }
            return pinned;
        }

        /// <summary>The distinct reasons the generators (on) of these layers have no effect now.</summary>
        IReadOnlyList<string> InactiveReasons(IEnumerable<PaintLayer> placed)
        {
            var reasons = new List<string>();
            foreach (var l in placed)
                foreach (var e in l.FilterList.Concat(l.Mask == null ? Enumerable.Empty<FilterEffect>() : l.Mask.FilterList))
                {
                    if (!e.Settings.IsGenerator || !e.IsActive) continue;
                    var status = StatusOfStage(e);
                    if (!status.Active && !reasons.Contains(status.Reason)) reasons.Add(status.Reason);
                }
            return reasons.AsReadOnly();
        }

        /// <summary>Refuses copies whose filter stacks this document would not accept (the checks of adding a filter: reach and working memory).</summary>
        void CheckFilters(IEnumerable<PaintLayer> copies, string name)
        {
            foreach (var l in copies)
            {
                foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) CheckFilterChain(l.ActiveChain(c), name + " / " + l.Name);
                if (l.Mask != null) CheckFilterChain(l.Mask.ActiveChain(), name + " / " + l.Name + " (mask)");
            }
        }
        void CheckFilterChain(FilterEffect[] chain, string where)
        {
            if (chain.Length == 0) return;
            int halo = FilterEngine.Halo(chain, chain.Length);
            if (halo > MaxFilterStackHalo) throw new SmartRefusedException(SmartRefusal.Filters, where + ": the filters would reach " + halo + " pixels in total here, more than " + MaxFilterStackHalo + ". Nothing was placed.");
            long need = BlockWorkingBytes(chain, filterBlockPixels);
            if (need > filterWorkingBudget) throw new SmartRefusedException(SmartRefusal.Filters, where + ": the filters need about " + (need >> 20) + " MiB of working memory per block here, more than the filter budget (" + (filterWorkingBudget >> 20) + " MiB). Nothing was placed.", need, filterWorkingBudget);
        }

        void ResolveParent(SmartPlacement placement, out Guid parentId, out int position)
        {
            parentId = placement.ParentId;
            if (parentId != Guid.Empty && !GetLayer(parentId).IsGroup) throw new ArgumentException("Layers can only be placed into a group.", nameof(placement));
            int siblings = 0; foreach (var l in layers) if (l.ParentId == parentId) siblings++;
            position = placement.Position < 0 || placement.Position > siblings ? siblings : placement.Position;
        }

        /// <summary>Where a block goes to sit at a position among a group's children (the index in <see cref="Layers"/>).</summary>
        int InsertIndex(Guid parentId, int position)
        {
            var siblings = new List<PaintLayer>(); foreach (var l in layers) if (l.ParentId == parentId) siblings.Add(l);
            if (position < siblings.Count) return SubtreeStart(layers.IndexOf(siblings[position]));
            return parentId == Guid.Empty ? layers.Count : layers.IndexOf(GetLayer(parentId)); // グループの記録のすぐ下 = 子の一番上
        }

        Guid FreshLayerId(IEnumerable<Guid> taken)
        {
            while (true)
            {
                var id = Guid.NewGuid();
                if (!layers.Any(l => l.Id == id) && !taken.Contains(id)) return id;
            }
        }
    }
}
