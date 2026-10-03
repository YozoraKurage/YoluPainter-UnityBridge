using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>How a merge made the merged layer's pixels.</summary>
    public enum MergeMethod
    {
        /// <summary>Merge down of a clipped layer into its clipping base: the clipped layer is applied to the base's own pixels exactly as the
        /// compositor clips it, and the result keeps the base's mode, opacity, mask and clipping. The composite does not change.</summary>
        IntoClippingBase,
        /// <summary>Merge down onto the layer below (Photoshop's merge down): the upper layer is laid over the lower layer's own pixels with
        /// its mode and amount, and the result keeps the lower layer's mode, opacity, mask and clipping.</summary>
        OntoLowerLayer,
        /// <summary>Merge down at the bottom of an isolated stack (nothing shows below): the two layers are composited from transparent as
        /// the document does, into a Normal layer at 100 % without a mask. The composite does not change.</summary>
        Isolated,
        /// <summary>Merge group: the group's contents composited from transparent; the result keeps the group's opacity, mask, clipping and
        /// mode (Normal for pass through).</summary>
        Group,
        /// <summary>Merge visible: the composite of the document, as a Normal layer at 100 % on the top level.</summary>
        Visible,
    }

    /// <summary>What a merge also did, besides combining pixels.</summary>
    [Flags]
    public enum MergeNotes
    {
        None = 0,
        /// <summary>Filters or generators of merged layers were applied to the pixels (the result has none).</summary>
        EffectsBaked = 1,
        /// <summary>A merged layer was drawn by a path; the result is plain pixels.</summary>
        PathsRasterized = 2,
        /// <summary>Hidden layers inside a merged group were dropped with it (Undo brings them back).</summary>
        HiddenLayersDropped = 4,
        /// <summary>Pixels of a channel that was switched off on a merged layer (not shown) were dropped.</summary>
        DisabledChannelsDropped = 8,
    }

    /// <summary>The result of a merge, with the measured change of the document's composite. Every channel a merged layer covers is
    /// composited before and after the merge with <see cref="CpuCompositor"/> over the tiles it can affect, and compared pixel by pixel;
    /// pixels that are fully transparent both before and after count as equal whatever their RGB (a single layer cannot reproduce the RGB of
    /// a composite pixel whose alpha rounded to 0). <see cref="MaxDifference"/> is the largest change of a stored byte;
    /// <see cref="MaxVisibleDifference"/> weighs colour changes by the pixel's alpha (premultiplied, the part that shows), which is what a
    /// merge's tolerance is compared with: a nearly transparent pixel whose colour moves several levels shows almost nothing.</summary>
    public sealed class LayerMergeReport
    {
        public Guid ResultId { get; }
        public MergeMethod Method { get; }
        public MergeNotes Notes { get; }
        /// <summary>Pixel × channel pairs compared.</summary>
        public long ComparedPixels { get; }
        /// <summary>Pixel × channel pairs whose composite changed.</summary>
        public long ChangedPixels { get; }
        /// <summary>The largest change of one stored component (straight RGBA bytes, 0..255).</summary>
        public int MaxDifference { get; }
        /// <summary>The largest change of alpha or of a premultiplied colour component (colour × alpha / 255, rounded), 0..255.</summary>
        public int MaxVisibleDifference { get; }
        /// <summary>Changed pixels per channel (only channels with changes).</summary>
        public IReadOnlyDictionary<PaintChannel, long> ChangedByChannel { get; }
        public bool Exact => ChangedPixels == 0;
        internal LayerMergeReport(Guid result, MergeMethod method, MergeNotes notes, long compared, long changed, int max, int visible, Dictionary<PaintChannel, long> byChannel)
        { ResultId = result; Method = method; Notes = notes; ComparedPixels = compared; ChangedPixels = changed; MaxDifference = max; MaxVisibleDifference = visible; ChangedByChannel = byChannel; }
    }

    /// <summary>A merge refused because it would change the composite more than allowed. Nothing was changed; <see cref="Report"/> says by
    /// how much (its ResultId names no layer).</summary>
    public sealed class LayerMergeException : LayerOpException
    {
        public LayerMergeReport Report { get; }
        internal LayerMergeException(LayerMergeReport report)
            : base(LayerOpRefusal.AppearanceChanges, "Merging would change how the document looks: " + report.ChangedPixels + " pixels, visibly by up to " + report.MaxVisibleDifference + " levels. Nothing was changed.")
        { Report = report; }
    }

    /// <summary>Merging layers. The merged layer replaces the merged ones in one undo step; the composite is compared before and after (see
    /// <see cref="LayerMergeReport"/>) and a merge that would change it by more than the caller allows is undone and refused.
    /// <para>Exact by construction (tested with random pixels, modes, opacities, masks, clipping, effects and groups): a clipped layer merged
    /// into its clipping base; a merge down at the bottom of an isolated stack; merging an isolated group; merge visible. In the Normal channel
    /// the merged pixels are composited again as vectors (renormalized and re-encoded), which moves some by one level, except for groups and
    /// clipping bases (their merged pixels are used as they are). Merging down onto layers with something below them is the same arithmetic
    /// in another order with 8-bit rounding after each layer, so overlapping semi-transparent pixels round differently (measured: up to 2
    /// levels where both layers are semi-transparent over an opaque background); a lower layer in another mode than Normal, or at reduced
    /// opacity, cannot hold the upper layer's look in general (Photoshop changes the look there too), and a pass-through group becomes
    /// isolated.</para></summary>
    public sealed partial class PaintDocument
    {
        /// <summary>The visible difference (<see cref="LayerMergeReport.MaxVisibleDifference"/>) a merge may make by default: the rounding of
        /// compositing two layers one after the other or in one piece (two levels).</summary>
        public const int MergeRoundingTolerance = 2;

        // ───────────── refusals (for menus) ─────────────

        /// <summary>Why <see cref="MergeDown"/> would be refused, or null when it can run (it can still be refused for the look or a budget).</summary>
        public LayerOpRefusal? MergeDownRefusal(Guid upperId)
        {
            var upper = GetLayer(upperId);
            if (upper.IsGroup) return LayerOpRefusal.IsGroup;
            var lower = SiblingBelow(upper);
            if (lower == null) return LayerOpRefusal.NoLayerBelow;
            if (lower.IsGroup) return LayerOpRefusal.LayerBelowIsGroup;
            if (lower.Kind == LayerKind.Adjustment) return LayerOpRefusal.LayerBelowIsAdjustment;
            if (!upper.Visible || !lower.Visible) return LayerOpRefusal.HiddenLayer;
            return null;
        }
        /// <summary>Why <see cref="MergeGroup"/> would be refused, or null.</summary>
        public LayerOpRefusal? MergeGroupRefusal(Guid groupId)
        {
            var group = GetLayer(groupId);
            if (!group.IsGroup) return LayerOpRefusal.NotGroup;
            foreach (var l in layers) if (l.ParentId == groupId) return null;
            return LayerOpRefusal.EmptyGroup;
        }
        /// <summary>Why <see cref="MergeVisible"/> would be refused, or null.</summary>
        public LayerOpRefusal? MergeVisibleRefusal() { return Contributing().Count == 0 ? LayerOpRefusal.NothingVisible : (LayerOpRefusal?)null; }

        PaintLayer SiblingBelow(PaintLayer layer)
        {
            for (int i = layers.IndexOf(layer) - 1; i >= 0; i--) if (layers[i].ParentId == layer.ParentId) return layers[i];
            return null;
        }
        static LayerOpException Refusal(LayerOpRefusal reason)
        {
            switch (reason)
            {
                case LayerOpRefusal.IsGroup: return new LayerOpException(reason, "This is a group. Use Merge Group to merge what is in it.");
                case LayerOpRefusal.NoLayerBelow: return new LayerOpException(reason, "There is no layer below in the same group to merge into.");
                case LayerOpRefusal.LayerBelowIsGroup: return new LayerOpException(reason, "The layer below is a group. Merge the group first, or move the layer into it.");
                case LayerOpRefusal.LayerBelowIsAdjustment: return new LayerOpException(reason, "The layer below is an adjustment layer, which has no pixels to merge into.");
                case LayerOpRefusal.HiddenLayer: return new LayerOpException(reason, "Hidden layers are not merged (their pixels would be lost or hidden). Show both layers first.");
                case LayerOpRefusal.NotGroup: return new LayerOpException(reason, "This is not a group.");
                case LayerOpRefusal.EmptyGroup: return new LayerOpException(reason, "The group is empty: there is nothing to merge.");
                case LayerOpRefusal.NothingVisible: return new LayerOpException(reason, "No layer shows anything: there is nothing to merge.");
                default: return new LayerOpException(reason, reason.ToString());
            }
        }

        // ───────────── merge down ─────────────

        /// <summary>Merges a layer into the layer directly below it in the same group (Photoshop's Merge Down), as one undo step. The result
        /// replaces both, gets a new ID and the lower layer's name:
        /// <list type="bullet">
        /// <item>a clipped layer merged into its clipping base keeps the base's mode, opacity, mask and clipping (<see cref="MergeMethod.IntoClippingBase"/>, exact);</item>
        /// <item>two unclipped layers with nothing visible below them in an isolated stack, the lower one not plain (another mode, opacity
        /// below 100 % or a mask), become one Normal layer at 100 % (<see cref="MergeMethod.Isolated"/>, exact);</item>
        /// <item>otherwise the upper layer is laid over the lower one's pixels and the result keeps the lower layer's mode, opacity, mask and
        /// clipping (<see cref="MergeMethod.OntoLowerLayer"/>).</item>
        /// </list>
        /// Filters and generators of both layers are baked, paths are rasterized (<see cref="MergeNotes"/>); layers clipped to the upper layer
        /// now clip to the result. Refused for groups (<see cref="MergeGroup"/>), with no layer below, a group or adjustment below, hidden layers,
        /// generators without usable maps, a result larger than <see cref="ActiveStrokeBudgetBytes"/>, and when the composite would change by
        /// more than tolerance levels (<see cref="LayerMergeReport.MaxVisibleDifference"/>; <see cref="LayerMergeException"/>; 255 allows any change).</summary>
        public LayerMergeReport MergeDown(Guid upperId, int tolerance = MergeRoundingTolerance, Guid? resultId = null)
        {
            EnsureNoStroke(); CheckTolerance(tolerance);
            var refusal = MergeDownRefusal(upperId); if (refusal.HasValue) throw Refusal(refusal.Value);
            var upper = GetLayer(upperId); var lower = SiblingBelow(upper);
            RefuseInactiveGenerators(upper, true, true); RefuseInactiveGenerators(lower, true, false);
            bool upperClipped = IsEffectivelyClipped(layers.IndexOf(upper)), lowerClipped = IsEffectivelyClipped(layers.IndexOf(lower));
            bool plain = lower.BlendMode == LayerBlendMode.Normal && lower.Opacity == 1 && (lower.Mask == null || lower.Mask.IsNeutral);
            MergeMethod method = upperClipped && !lowerClipped ? MergeMethod.IntoClippingBase
                : !upperClipped && !lowerClipped && !plain && NothingShowsBelow(lower) ? MergeMethod.Isolated : MergeMethod.OntoLowerLayer;
            Guid id = NewLayerId(resultId);
            var result = new PaintLayer(this, lower.Name, id);
            MergeNotes notes = MergeNotes.None;
            if (upper.FilterList.Count > 0 || lower.FilterList.Count > 0 || upper.Mask != null && upper.Mask.FilterList.Count > 0 || method == MergeMethod.Isolated && lower.Mask != null && lower.Mask.FilterList.Count > 0) notes |= MergeNotes.EffectsBaked;
            if (upper.Path != null || lower.Path != null) notes |= MergeNotes.PathsRasterized;
            foreach (var c in upper.Channels.Keys) if (!upper.IsChannelEnabled(c) && upper.Channels[c].TileCount > 0) notes |= MergeNotes.DisabledChannelsDropped;
            if (method == MergeMethod.Isolated) { result.Visible = true; result.Opacity = 1; result.BlendMode = LayerBlendMode.Normal; }
            else
            {
                result.Visible = lower.Visible; result.Opacity = lower.Opacity; result.BlendMode = lower.BlendMode; result.Clipping = lower.Clipping;
                if (lower.Mask != null) result.Mask = CloneMask(lower.Mask, result);
            }
            long budget = 0;
            foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel)))
            {
                bool lowerHas = lower.Kind == LayerKind.Fill ? lower.FillValues.ContainsKey(c) : lower.Channels.ContainsKey(c);
                bool lowerOn = lowerHas && lower.IsChannelEnabled(c);
                var upperNode = MakeNode(upper, c);
                bool upperOn = upperNode != null && (method != MergeMethod.IntoClippingBase || lowerOn && lower.Opacity > 0);
                if (!lowerHas && !upperOn) continue;
                var tiles = new SortedSet<TileCoord>(lower.EnumerateContentTiles(c));
                if (lower.Kind == LayerKind.Raster && lower.Channels.TryGetValue(c, out var raw)) tiles.UnionWith(raw.EnumerateTileCoordinates());
                if (upperOn) tiles.UnionWith(upper.EnumerateContentTiles(c));
                if (!lowerOn && lowerHas && !upperOn)
                {
                    if (lower.Kind == LayerKind.Fill) notes |= MergeNotes.DisabledChannelsDropped;
                    // 下の層の無効のチャンネル: 見えていない画素はそのまま残す（無効のまま）
                    CopyRawTiles(lower, c, result.GetChannel(c), ref budget);
                    result.Enable(c, false); continue;
                }
                // 結果のチャンネルは、下の層にあったか、画素ができたときだけ作る（上の調整レイヤーが透明の上で何も作らなかったチャンネルに空の面を残さない）
                var surface = new SparseTileSurface(Width, Height, TileSize);
                if (!lowerOn && lowerHas) notes |= MergeNotes.DisabledChannelsDropped;
                var lowerNode = lowerOn ? MakeNode(lower, c) : null;
                var nodes = new List<MergeNode>(); if (lowerNode != null) nodes.Add(lowerNode); if (upperOn) nodes.Add(upperNode);
                EvaluateTiles(c, tiles, nodes, surface, ref budget, lowerOn ? lower : null, (slot, i, normal, below) =>
                {
                    switch (method)
                    {
                        // 下の層（クリッピングの基）の画素に、合成器がクリッピングするとおりに上の層を当てる。基の画素が無いタイルでは当てない
                        case MergeMethod.IntoClippingBase: return lowerNode != null && lowerNode.Present[slot] && upperOn ? ClipStep(below, upperNode, slot, i, normal) : below;
                        case MergeMethod.Isolated: return EvaluateNodes(nodes, Rgba32.Transparent, slot, i, normal);
                        default: return upperOn ? EvaluateNodes(upperNode, below, slot, i, normal) : below;
                    }
                });
                if (!lowerHas && surface.TileCount == 0) continue;
                var target = result.GetChannel(c);
                foreach (var coord in surface.EnumerateTileCoordinates()) target.Restore(coord, surface.Capture(coord));
                result.Enable(c, lowerOn || upperOn);
            }
            var before = SnapshotStructure(); int at = layers.IndexOf(lower);
            result.ParentId = lower.ParentId;
            layers.Remove(upper); layers[at] = result;
            var after = SnapshotStructure(); RestoreStructure(before);
            return CommitMerge(before, after, new[] { lower, upper }, result, method, notes, tolerance, null);
        }

        /// <summary>True when nothing below the layer in its group shows (every sibling below is hidden) and the group is isolated (the top
        /// level, or a group in another mode than pass through), so the backdrop under the layer is transparent.</summary>
        bool NothingShowsBelow(PaintLayer layer)
        {
            if (layer.ParentId != Guid.Empty && GetLayer(layer.ParentId).BlendMode == LayerBlendMode.PassThrough) return false;
            for (int i = layers.IndexOf(layer) - 1; i >= 0; i--) if (layers[i].ParentId == layer.ParentId && layers[i].Visible) return false;
            return true;
        }

        // ───────────── merge group ─────────────

        /// <summary>Merges a group and everything in it into one paint layer (Photoshop's Merge Group), as one undo step: the contents
        /// composited from transparent as the document composites the group. The result takes the group's place, name, visibility,
        /// opacity, mask, clipping and mode (Normal instead of pass through) and gets a new ID; layers clipped to the group now clip to it.
        /// Exact for an isolated group; a pass-through group becomes isolated (exact only when nothing shows below it). Hidden layers inside
        /// are dropped with the group (<see cref="MergeNotes.HiddenLayersDropped"/>). Refusals as <see cref="MergeDown"/>.</summary>
        public LayerMergeReport MergeGroup(Guid groupId, int tolerance = MergeRoundingTolerance, Guid? resultId = null)
        {
            EnsureNoStroke(); CheckTolerance(tolerance);
            var refusal = MergeGroupRefusal(groupId); if (refusal.HasValue) throw Refusal(refusal.Value);
            var group = GetLayer(groupId); var block = Block(group);
            MergeNotes notes = MergeNotes.None;
            foreach (var l in block)
            {
                if (l == group) continue;
                RefuseInactiveGenerators(l, true, true);
                if (l.FilterList.Count > 0 || l.Mask != null && l.Mask.FilterList.Count > 0) notes |= MergeNotes.EffectsBaked;
                if (l.Path != null) notes |= MergeNotes.PathsRasterized;
                if (!ShownWithin(l, group)) notes |= MergeNotes.HiddenLayersDropped;
                foreach (var c in l.Channels.Keys) if (!l.IsChannelEnabled(c) && l.Channels[c].TileCount > 0) notes |= MergeNotes.DisabledChannelsDropped;
            }
            Guid id = NewLayerId(resultId);
            var result = new PaintLayer(this, group.Name, id)
            { Visible = group.Visible, Opacity = group.Opacity, Clipping = group.Clipping, BlendMode = group.BlendMode == LayerBlendMode.PassThrough ? LayerBlendMode.Normal : group.BlendMode };
            if (group.Mask != null) result.Mask = CloneMask(group.Mask, result);
            long budget = 0;
            foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel)))
            {
                var children = PlanNodes(group.Id, c);
                if (children.Count == 0) continue;
                var tiles = new SortedSet<TileCoord>();
                foreach (var l in block) if (!l.IsGroup) tiles.UnionWith(l.EnumerateContentTiles(c));
                var surface = new SparseTileSurface(Width, Height, TileSize);
                EvaluateTiles(c, tiles, children, surface, ref budget, null, (slot, i, normal, below) => EvaluateNodes(children, Rgba32.Transparent, slot, i, normal));
                if (surface.TileCount == 0) continue;
                var target = result.GetChannel(c);
                foreach (var coord in surface.EnumerateTileCoordinates()) target.Restore(coord, surface.Capture(coord));
            }
            var before = SnapshotStructure(); int start = layers.IndexOf(block[0]);
            result.ParentId = group.ParentId;
            layers.RemoveRange(start, block.Count); layers.Insert(start, result);
            var after = SnapshotStructure(); RestoreStructure(before);
            return CommitMerge(before, after, block, result, MergeMethod.Group, notes, tolerance, null);
        }

        /// <summary>True when the layer and every group between it and the given ancestor are visible.</summary>
        bool ShownWithin(PaintLayer layer, PaintLayer ancestor)
        {
            for (var l = layer; l != ancestor; l = GetLayer(l.ParentId)) if (!l.Visible) return false;
            return true;
        }

        // ───────────── merge visible ─────────────

        /// <summary>Merges every layer that shows into one paint layer on the top level (Photoshop's Merge Visible), as one undo step: its
        /// pixels are the document's composite of each channel, so the document looks the same (exact, except the Normal channel, see the
        /// class summary). Layers that show nothing — hidden ones, those at 0 % opacity or without content, clipped to a hidden base — stay
        /// where they are; groups stay while they still hold such layers. The result is put where the top-level part holding
        /// <paramref name="anchor"/> was (else where the topmost merged part was) and gets a new ID; it is named after the anchor when
        /// the anchor is merged (Photoshop merges into the selected layer), else <paramref name="name"/>.</summary>
        public LayerMergeReport MergeVisible(string name, Guid? anchor = null, int tolerance = MergeRoundingTolerance, Guid? resultId = null)
        {
            EnsureNoStroke(); CheckTolerance(tolerance);
            var merged = Contributing();
            if (merged.Count == 0) throw Refusal(LayerOpRefusal.NothingVisible);
            MergeNotes notes = MergeNotes.None;
            foreach (var l in merged)
            {
                if (l.IsGroup) continue;
                RefuseInactiveGenerators(l, true, true);
                if (l.FilterList.Count > 0 || l.Mask != null && l.Mask.FilterList.Count > 0) notes |= MergeNotes.EffectsBaked;
                if (l.Path != null) notes |= MergeNotes.PathsRasterized;
                foreach (var c in l.Channels.Keys) if (!l.IsChannelEnabled(c) && l.Channels[c].TileCount > 0) notes |= MergeNotes.DisabledChannelsDropped;
            }
            // グループは、中身が全部まとまるときだけ消す（見えない層が残るなら、その親として残す。中身は見えないので合成は変わらない）
            merged.RemoveWhere(l => l.IsGroup);
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var l in layers)
                {
                    if (!l.IsGroup || merged.Contains(l)) continue;
                    bool all = true, any = false;
                    foreach (var d in layers) if (d != l && IsDescendant(d, l)) { any = true; if (!merged.Contains(d)) { all = false; break; } }
                    if (any && all) { merged.Add(l); grew = true; }
                }
            }
            Guid id = NewLayerId(resultId);
            // Photoshop と同じく、選んでいる層が結合されるならその層に結合する（その名前）
            var named = anchor.HasValue ? layers.Find(l => l.Id == anchor.Value) : null;
            var result = new PaintLayer(this, named != null && merged.Contains(named) ? named.Name : name ?? "Merged", id);
            long budget = 0;
            foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel)))
            {
                var tiles = new SortedSet<TileCoord>(); foreach (var l in layers) if (!l.IsGroup) tiles.UnionWith(l.EnumerateContentTiles(c));
                if (tiles.Count == 0 || PlanNodes(Guid.Empty, c).Count == 0) continue;
                SparseTileSurface surface = null;
                foreach (var chunk in Chunks(tiles))
                {
                    var buffers = CompositeTiles(c, chunk);
                    for (int k = 0; k < chunk.Count; k++)
                    {
                        ClearTransparent(buffers[k]);
                        var storage = TileStorage.FromBytes(buffers[k]); if (storage == null) continue;
                        budget += 64 + storage.ByteSize;
                        if (budget > ActiveStrokeBudgetBytes) throw MergeBudget(budget);
                        if (surface == null) surface = result.GetChannel(c);
                        surface.Restore(chunk[k], storage);
                    }
                }
            }
            var before = SnapshotStructure();
            // 置き場: anchor を含む最上位のまとまり（まとめる層を含むなら）、無ければまとめる層を含むいちばん上の最上位のまとまり
            int anchorTop = -1;
            if (anchor.HasValue && layers.Exists(l => l.Id == anchor.Value))
            {
                var top = GetLayer(anchor.Value); while (top.ParentId != Guid.Empty) top = GetLayer(top.ParentId);
                int t = layers.IndexOf(top), s = SubtreeStart(t);
                for (int i = s; i <= t; i++) if (merged.Contains(layers[i])) { anchorTop = t; break; }
            }
            if (anchorTop < 0)
                for (int i = layers.Count - 1; i >= 0 && anchorTop < 0; i--)
                {
                    if (layers[i].ParentId != Guid.Empty) continue;
                    for (int j = SubtreeStart(i); j <= i; j++) if (merged.Contains(layers[j])) { anchorTop = i; break; }
                }
            var order = new List<PaintLayer>(); int insertAt = 0;
            for (int i = 0; i < layers.Count; i++) { if (merged.Contains(layers[i])) continue; order.Add(layers[i]); if (i <= anchorTop) insertAt = order.Count; }
            order.Insert(insertAt, result); result.ParentId = Guid.Empty;
            layers.Clear(); layers.AddRange(order);
            var after = SnapshotStructure(); RestoreStructure(before);
            var removed = new List<PaintLayer>(); foreach (var l in layers) if (merged.Contains(l)) removed.Add(l);
            return CommitMerge(before, after, removed, result, MergeMethod.Visible, notes, tolerance, result);
        }

        /// <summary>The layers that show in some channel: every layer the compositor's plan of any channel holds (a base, a clipped layer, a
        /// group or a layer inside one).</summary>
        HashSet<PaintLayer> Contributing()
        {
            var set = new HashSet<PaintLayer>();
            void Walk(List<MergeNode> nodes) { foreach (var n in nodes) { set.Add(n.Layer); Walk(n.Children); Walk(n.Clips); } }
            foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) Walk(PlanNodes(Guid.Empty, c));
            return set;
        }

        // ───────────── commit and verification ─────────────

        static void CheckTolerance(int tolerance) { if (tolerance < 0 || tolerance > 255) throw new ArgumentOutOfRangeException(nameof(tolerance), "The tolerance is 0..255 levels."); }
        Exception MergeBudget(long bytes)
            => new LayerOpException(LayerOpRefusal.OperationBudget, "The merged layer would need more than the one-operation budget (" + Mib(ActiveStrokeBudgetBytes) + " MiB). Nothing was changed. Raise the budget (Project Settings > YoluPainter > One stroke, or ActiveStrokeBudgetBytes).", bytes, ActiveStrokeBudgetBytes);

        /// <summary>Applies the structural swap, compares the composite of every affected channel before and after (the swap is undone and
        /// redone around each chunk of tiles; with <paramref name="storedBefore"/> the "before" composite is that layer's pixels instead) and
        /// either records the swap as one undo step or undoes it and refuses.</summary>
        LayerMergeReport CommitMerge(Structure before, Structure after, IList<PaintLayer> removed, PaintLayer result, MergeMethod method, MergeNotes notes, int tolerance, PaintLayer storedBefore)
        {
            long cost = 128 + result.AllocatedBytes; foreach (var l in removed) cost += l.AllocatedBytes;
            var swap = SwapStructure(before, after, cost);
            swap.Apply();
            long compared = 0, changed = 0; int max = 0, visible = 0; var byChannel = new Dictionary<PaintChannel, long>();
            try
            {
                foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel)))
                {
                    var tiles = new SortedSet<TileCoord>();
                    foreach (var l in removed) if (!l.IsGroup) tiles.UnionWith(l.EnumerateContentTiles(c));
                    if (result.TryGetChannel(c, out var own)) tiles.UnionWith(own.EnumerateTileCoordinates());
                    if (storedBefore != null) foreach (var l in layers) if (!l.IsGroup) tiles.UnionWith(l.EnumerateContentTiles(c));
                    long channelChanged = 0;
                    foreach (var chunk in Chunks(tiles))
                    {
                        var now = CompositeTiles(c, chunk); byte[][] then;
                        if (storedBefore != null)
                        {
                            then = new byte[chunk.Count][];
                            for (int k = 0; k < chunk.Count; k++) { then[k] = new byte[TileSize * TileSize * 4]; if (storedBefore.TryGetChannel(c, out var s)) s.CopyTile(chunk[k], then[k]); }
                        }
                        else { swap.Revert(); try { then = CompositeTiles(c, chunk); } finally { swap.Apply(); } }
                        for (int k = 0; k < chunk.Count; k++)
                        {
                            int w = Math.Min(TileSize, Width - chunk[k].X * TileSize), h = Math.Min(TileSize, Height - chunk[k].Y * TileSize);
                            var a = now[k]; var b = then[k];
                            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                            {
                                int o = (y * TileSize + x) * 4; compared++;
                                if (a[o + 3] == 0 && b[o + 3] == 0) continue;
                                int d = Math.Max(Math.Max(Math.Abs(a[o] - b[o]), Math.Abs(a[o + 1] - b[o + 1])), Math.Max(Math.Abs(a[o + 2] - b[o + 2]), Math.Abs(a[o + 3] - b[o + 3])));
                                if (d == 0) continue;
                                channelChanged++; if (d > max) max = d;
                                int v = Math.Abs(a[o + 3] - b[o + 3]);
                                for (int q = 0; q < 3; q++) v = Math.Max(v, (Math.Abs(a[o + q] * a[o + 3] - b[o + q] * b[o + 3]) + 127) / 255);
                                if (v > visible) visible = v;
                            }
                        }
                    }
                    if (channelChanged > 0) { byChannel[c] = channelChanged; changed += channelChanged; }
                }
            }
            catch { swap.Revert(); throw; }
            var report = new LayerMergeReport(result.Id, method, notes, compared, changed, max, visible, byChannel);
            if (visible > tolerance) { swap.Revert(); throw new LayerMergeException(report); }
            Revision++; Push(swap);
            return report;
        }

        /// <summary>Tile lists of at most a few MiB of composite (bounded by a quarter of the one-operation budget, at least one tile).</summary>
        IEnumerable<List<TileCoord>> Chunks(IEnumerable<TileCoord> tiles)
        {
            long tileBytes = (long)TileSize * TileSize * 4;
            int size = (int)Math.Max(1, Math.Min(16L * 1024 * 1024, ActiveStrokeBudgetBytes / 4) / tileBytes);
            var chunk = new List<TileCoord>();
            foreach (var t in tiles) { chunk.Add(t); if (chunk.Count == size) { yield return chunk; chunk = new List<TileCoord>(); } }
            if (chunk.Count > 0) yield return chunk;
        }
        /// <summary>The composite of each tile (TileSize² RGBA, padding zero) with one <see cref="CpuCompositor.CompositeRegions"/> call.</summary>
        byte[][] CompositeTiles(PaintChannel channel, List<TileCoord> coords)
        {
            var jobs = new List<CpuCompositor.CompositeJob>(); var pixels = new byte[coords.Count][];
            foreach (var coord in coords)
            {
                int x = coord.X * TileSize, y = coord.Y * TileSize, w = Math.Min(TileSize, Width - x), h = Math.Min(TileSize, Height - y);
                jobs.Add(new CpuCompositor.CompositeJob(x, y, w, h, new byte[w * h * 4]));
            }
            CpuCompositor.CompositeRegions(this, channel, jobs);
            for (int k = 0; k < coords.Count; k++)
            {
                var job = jobs[k]; var tile = pixels[k] = new byte[TileSize * TileSize * 4];
                for (int row = 0; row < job.Height; row++) Buffer.BlockCopy(job.Pixels, row * job.Width * 4, tile, row * TileSize * 4, job.Width * 4);
            }
            return pixels;
        }
        void CopyRawTiles(PaintLayer layer, PaintChannel channel, SparseTileSurface target, ref long budget)
        {
            if (!layer.Channels.TryGetValue(channel, out var source)) return;
            foreach (var coord in source.EnumerateTileCoordinates())
            {
                var t = source.Capture(coord); budget += 64 + t.ByteSize;
                if (budget > ActiveStrokeBudgetBytes) throw MergeBudget(budget);
                target.Restore(coord, t);
            }
        }

        // ───────────── evaluation (the compositor's per-pixel reference, over chosen layers) ─────────────

        /// <summary>One layer of a merge's plan, mirroring <see cref="CpuCompositor.StackEntry"/>: a base with its clipped layers, or a group
        /// with its contents, and per-slot tile buffers.</summary>
        sealed class MergeNode
        {
            internal PaintLayer Layer; internal bool Adjustment, Group, PassesThrough; internal LayerBlendMode Mode;
            internal readonly List<MergeNode> Children = new List<MergeNode>(), Clips = new List<MergeNode>();
            internal byte[][] Pixels, Hide; internal bool[] Present; internal double[] Factor;
            internal void Allocate(int slots, int tileBytes)
            {
                Present = new bool[slots];
                if (!Adjustment && !Group) { Pixels = new byte[slots][]; for (int k = 0; k < slots; k++) Pixels[k] = new byte[tileBytes]; }
                if (Layer.Mask != null && !Layer.Mask.IsNeutral)
                {
                    Hide = new byte[slots][]; for (int k = 0; k < slots; k++) Hide[k] = new byte[tileBytes];
                    Factor = new double[256]; for (int h = 0; h < 256; h++) Factor[h] = Layer.Mask.Factor((byte)h);
                }
                foreach (var n in Children) n.Allocate(slots, tileBytes);
                foreach (var n in Clips) n.Allocate(slots, tileBytes);
            }
        }
        static bool ActiveIn(PaintLayer layer, PaintChannel channel)
        { return layer.Visible && layer.Opacity > 0 && layer.IsChannelEnabled(channel) && layer.HasContent(channel); }
        /// <summary>The plan of a group's contents (Guid.Empty: the top level) for a channel, as <see cref="CpuCompositor.Plan"/> builds it.</summary>
        List<MergeNode> PlanNodes(Guid parent, PaintChannel channel)
        {
            var plan = new List<MergeNode>(); var siblings = new List<PaintLayer>();
            foreach (var l in layers) if (l.ParentId == parent) siblings.Add(l);
            for (int k = 0; k < siblings.Count; k++)
            {
                if (k > 0 && siblings[k].Clipping) continue;
                var node = MakeNode(siblings[k], channel); if (node == null) continue;
                if (siblings[k].Kind != LayerKind.Adjustment)
                    for (int m = k + 1; m < siblings.Count && siblings[m].Clipping; m++) { var clip = MakeNode(siblings[m], channel); if (clip != null) node.Clips.Add(clip); }
                node.PassesThrough = node.Group && siblings[k].BlendMode == LayerBlendMode.PassThrough && node.Clips.Count == 0;
                plan.Add(node);
            }
            return plan;
        }
        /// <summary>A node for one layer (a group with its contents; clipped layers only through <see cref="PlanNodes"/>), or null when it
        /// shows nothing in the channel.</summary>
        MergeNode MakeNode(PaintLayer layer, PaintChannel channel)
        {
            var node = new MergeNode { Layer = layer, Adjustment = layer.Kind == LayerKind.Adjustment, Group = layer.IsGroup, Mode = layer.BlendMode == LayerBlendMode.PassThrough ? LayerBlendMode.Normal : layer.BlendMode };
            if (layer.IsGroup)
            {
                if (!layer.Visible || layer.Opacity <= 0) return null;
                node.Children.AddRange(PlanNodes(layer.Id, channel));
                return node.Children.Count == 0 ? null : node;
            }
            return ActiveIn(layer, channel) ? node : null;
        }

        /// <summary>One merged pixel: slot and index of the pixel in the tile, whether the channel is Normal, and the basis layer's output
        /// pixel (transparent without a basis).</summary>
        delegate Rgba32 PixelRule(int slot, int index, bool normal, Rgba32 below);

        /// <summary>Evaluates rule for every pixel of the tiles into target: the nodes' tiles (and the basis layer's output and stored
        /// pixels) are read on this thread a batch at a time, the pixels computed in parallel. A merged pixel that comes out fully
        /// transparent keeps the basis layer's stored pixel when that is transparent too (the RGB kept under zero alpha), otherwise
        /// becomes transparent black: a layer pixel with zero alpha never shows, so neither changes the composite. Empty tiles are not
        /// stored. The stored tiles count against the one-operation budget.</summary>
        void EvaluateTiles(PaintChannel channel, IEnumerable<TileCoord> tiles, List<MergeNode> nodes, SparseTileSurface target, ref long budget, PaintLayer basis, PixelRule rule)
        {
            int tile = TileSize, tileBytes = tile * tile * 4; bool normal = channel == PaintChannel.Normal;
            int count = 0; foreach (var n in nodes) count += CountNodes(n);
            int degree = CoreParallelism.Degree;
            int slots = (int)Math.Max(1, Math.Min(degree * 2L, 32L * 1024 * 1024 / ((count + 3L) * tileBytes)));
            foreach (var n in nodes) n.Allocate(slots, tileBytes);
            var below = new byte[slots][]; var raw = new byte[slots][]; var output = new byte[slots][];
            for (int k = 0; k < slots; k++) { below[k] = new byte[tileBytes]; raw[k] = new byte[tileBytes]; output[k] = new byte[tileBytes]; }
            var list = new List<TileCoord>(tiles);
            for (int start = 0; start < list.Count; start += slots)
            {
                int batch = Math.Min(slots, list.Count - start), first = start;
                for (int k = 0; k < batch; k++)
                {
                    var coord = list[first + k];
                    LoadNodes(nodes, channel, coord, k);
                    if (basis != null) { basis.CopyOutputTile(channel, coord, below[k]); basis.CopyTile(channel, coord, raw[k]); }
                }
                CoreParallelism.For(batch, degree, k =>
                {
                    var res = output[k]; var coord = list[first + k];
                    int w = Math.Min(tile, Width - coord.X * tile), h = Math.Min(tile, Height - coord.Y * tile);
                    Array.Clear(res, 0, tileBytes);
                    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    {
                        int i = y * tile + x, o = i * 4;
                        var p = rule(k, i, normal, basis == null ? Rgba32.Transparent : Read(below[k], o));
                        if (p.A == 0) { if (basis != null && raw[k][o + 3] == 0) { res[o] = raw[k][o]; res[o + 1] = raw[k][o + 1]; res[o + 2] = raw[k][o + 2]; } continue; }
                        res[o] = p.R; res[o + 1] = p.G; res[o + 2] = p.B; res[o + 3] = p.A;
                    }
                });
                for (int k = 0; k < batch; k++)
                {
                    var storage = TileStorage.FromBytes(output[k]); if (storage == null) continue;
                    budget += 64 + storage.ByteSize;
                    if (budget > ActiveStrokeBudgetBytes) throw MergeBudget(budget);
                    target.Restore(list[first + k], storage);
                }
            }
        }
        /// <summary>Turns every fully transparent pixel into transparent black.</summary>
        static void ClearTransparent(byte[] tile)
        { for (int o = 0; o < tile.Length; o += 4) if (tile[o + 3] == 0) { tile[o] = 0; tile[o + 1] = 0; tile[o + 2] = 0; } }
        static int CountNodes(MergeNode n) { int c = 1; foreach (var x in n.Children) c += CountNodes(x); foreach (var x in n.Clips) c += CountNodes(x); return c; }
        static bool HasAdjustmentNode(List<MergeNode> nodes, int slot) { foreach (var n in nodes) if (n.Present[slot] && (n.Adjustment || n.Group)) return true; return false; }

        /// <summary>Loads the tiles of the nodes into slot, as the compositor does: a raster or fill layer is present where its output has a
        /// tile, an adjustment always, a group where its contents have pixels or an adjustment; clipped layers only under a present base.
        /// Returns true when raster or fill pixels are present.</summary>
        static bool LoadNodes(List<MergeNode> nodes, PaintChannel channel, TileCoord coord, int slot)
        {
            bool any = false;
            foreach (var n in nodes)
            {
                bool pixels;
                if (n.Group) { pixels = LoadNodes(n.Children, channel, coord, slot); n.Present[slot] = pixels || HasAdjustmentNode(n.Children, slot); }
                else if (n.Adjustment) { n.Present[slot] = true; pixels = false; }
                else { n.Present[slot] = n.Layer.CopyOutputTile(channel, coord, n.Pixels[slot]); pixels = n.Present[slot]; }
                if (n.Present[slot])
                {
                    if (n.Hide != null) n.Layer.Mask.CopyOutputTile(coord, n.Hide[slot]);
                    pixels |= LoadNodes(n.Clips, channel, coord, slot);
                }
                else ClearPresence(n.Clips, slot);
                any |= pixels;
            }
            return any;
        }
        static void ClearPresence(List<MergeNode> nodes, int slot) { foreach (var n in nodes) { n.Present[slot] = false; ClearPresence(n.Children, slot); ClearPresence(n.Clips, slot); } }

        static Rgba32 Read(byte[] bytes, int o) => new Rgba32(bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]);
        static double Amount(MergeNode n, int slot, int i) => n.Hide == null ? n.Layer.Opacity : n.Layer.Opacity * n.Factor[n.Hide[slot][i * 4 + 3]];

        /// <summary>The compositor's per-pixel stack (CpuCompositor.EvaluatePixel) over these nodes, onto backdrop.</summary>
        static Rgba32 EvaluateNodes(List<MergeNode> nodes, Rgba32 backdrop, int slot, int i, bool normal)
        {
            var result = backdrop;
            foreach (var n in nodes) result = EvaluateNodes(n, result, slot, i, normal);
            return result;
        }
        static Rgba32 EvaluateNodes(MergeNode n, Rgba32 result, int slot, int i, bool normal)
        {
            if (!n.Present[slot]) return result;
            double amount = Amount(n, slot, i);
            if (n.Adjustment) return n.Layer.Adjustment.Composite(result, amount, n.Layer.BlendMode);
            if (n.PassesThrough)
            {
                var inner = EvaluateNodes(n.Children, result, slot, i, normal);
                return normal ? NormalMaps.Fade(result, inner, amount) : CpuCompositor.Fade(result, inner, amount);
            }
            var g = n.Group ? EvaluateNodes(n.Children, Rgba32.Transparent, slot, i, normal) : Read(n.Pixels[slot], i * 4);
            foreach (var clip in n.Clips) g = ClipStep(g, clip, slot, i, normal);
            return normal ? NormalMaps.BlendUnchecked(result, g, amount, n.Mode) : CpuCompositor.BlendUnchecked(result, g, amount, n.Mode);
        }
        /// <summary>One clipped layer onto its clipping group's colour, as the compositor applies it.</summary>
        static Rgba32 ClipStep(Rgba32 g, MergeNode clip, int slot, int i, bool normal)
        {
            if (!clip.Present[slot]) return g;
            double a = Amount(clip, slot, i);
            if (clip.Adjustment) return clip.Layer.Adjustment.Composite(g, a, clip.Layer.BlendMode);
            var c = clip.Group ? EvaluateNodes(clip.Children, Rgba32.Transparent, slot, i, normal) : Read(clip.Pixels[slot], i * 4);
            return normal ? NormalMaps.ClipOnto(g, c, a, clip.Mode) : CpuCompositor.ClipOnto(g, c, a, clip.Mode);
        }
    }
}
