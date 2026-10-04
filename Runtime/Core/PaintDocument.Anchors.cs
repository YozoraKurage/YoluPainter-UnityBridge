using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>
    /// Anchor points (<see cref="AnchorPoint"/>) and the anchor generators that read them (<see cref="GeneratorType.Anchor"/>).
    /// <para>Editing: <see cref="AddAnchor"/>, <see cref="RemoveAnchor"/> and <see cref="RenameAnchor"/> are one undo step each; an anchor goes
    /// with its layer (or mask) when that is deleted, merged, duplicated (the copy gets its own anchor, and references inside the copied block
    /// point at the copy's), saved as a smart material or resized. <see cref="SetGeneratorAnchor"/> chooses what a generator reads and refuses an
    /// anchor that is not below its layer (checked with the <see cref="DependencyGraph"/>: same texture set, value type, lower layer, no
    /// cycle). Moving, grouping, deleting or merging layers is never refused because of anchors: a reference that ends up not below its reader,
    /// or whose anchor is gone, passes its input through and says why (<see cref="AnchorIssues"/>, the generator's status), and undo restores it.</para>
    /// <para>Evaluation: an anchor generator's stage reads the anchor's tiles, made on the calling thread before the filter engine's workers
    /// start (a layer anchor's composite through its layer, <see cref="CpuCompositor.AnchorPlan"/>, cached under
    /// <see cref="AnchorCacheBudgetBytes"/>; a mask anchor's filtered mask). Change tracking: each anchor value has per-tile versions that
    /// grow whenever something that can change it changes there (the same changed tiles the display reads, followed through every reader
    /// below it), and the readers' filter stamps add them up, so cached filter output and display copies never mix old and new anchor values.
    /// <see cref="TryGetChangedTiles"/> reports, for a channel, the tiles where a reader's output may have changed because what it reads did
    /// (grown by the reader's halo), through chains of readers. What cannot be followed tile by tile (an anchor added, removed or moved, the
    /// layers at or below a layer anchor reordered, the groups around it changing between pass-through and isolated, a mask anchor's on/off,
    /// invert, density or filters, a reference becoming usable or not) counts as a change everywhere for every reader.</para>
    /// </summary>
    public sealed partial class PaintDocument
    {
        /// <summary>The default budget of cached layer-anchor tiles.</summary>
        public const long DefaultAnchorCacheBudgetBytes = 256L * 1024 * 1024;
        /// <summary>The effect order given to anchors in the dependency graph: above every stage of their layer (a stage never reads its own
        /// layer's anchor).</summary>
        const int AnchorEffectOrder = 1 << 20;

        // ───────────── editing ─────────────

        /// <summary>Every anchor, bottom to top (on a layer before the one on its mask).</summary>
        public IReadOnlyList<AnchorInfo> Anchors { get { RefreshAnchors(); return anchorList; } }

        /// <summary>The anchor with this ID and its layer, or null.</summary>
        public AnchorInfo FindAnchor(Guid anchorId) { RefreshAnchors(); return anchorTable.TryGetValue(anchorId, out var info) ? info : null; }

        /// <summary>Puts an anchor on a layer (the stack's result through it) or on its mask, as one undo step. A layer holds at most one of each
        /// placement. Refused (nothing changes) without a mask for <see cref="AnchorPlacement.Mask"/>, when the layer already has one there,
        /// under Lock All, or with a name that is empty or too long. The name defaults to the layer's (with " (mask)" for a mask).</summary>
        public AnchorPoint AddAnchor(Guid layerId, AnchorPlacement placement = AnchorPlacement.Layer, string name = null, Guid? id = null)
        {
            EnsureNoStroke(); if (!Enum.IsDefined(typeof(AnchorPlacement), placement)) throw new ArgumentOutOfRangeException(nameof(placement));
            var layer = GetLayer(layerId);
            if (placement == AnchorPlacement.Mask && layer.Mask == null) throw new InvalidOperationException("'" + layer.Name + "' has no mask to put an anchor on.");
            if ((placement == AnchorPlacement.Layer ? layer.Anchor : layer.Mask.Anchor) != null)
                throw new InvalidOperationException("'" + layer.Name + "'" + (placement == AnchorPlacement.Mask ? " (mask)" : "") + " already has an anchor.");
            RefuseLockedAttributes(layer); // 画素は変えないので、すべてのロックでだけ断る（フィルターと同じ）
            var anchorId = id ?? Guid.NewGuid();
            if (anchorId == Guid.Empty) throw new ArgumentException("Anchor ID must not be empty.", nameof(id));
            if (FindAnchor(anchorId) != null) throw new ArgumentException("Duplicate anchor ID.", nameof(id));
            var anchor = new AnchorPoint(anchorId, name ?? (placement == AnchorPlacement.Mask ? layer.Name + " (mask)" : layer.Name), placement);
            var mask = layer.Mask;
            if (placement == AnchorPlacement.Layer) Execute(new DelegateCommand(() => layer.Anchor = anchor, () => layer.Anchor = null, 64));
            else Execute(new DelegateCommand(() => mask.Anchor = anchor, () => mask.Anchor = null, 64));
            return anchor;
        }

        /// <summary>Removes an anchor as one undo step. Allowed while generators read it: they pass their input through and say why
        /// (<see cref="AnchorIssues"/>) until another anchor is chosen or the removal is undone.</summary>
        public void RemoveAnchor(Guid anchorId)
        {
            EnsureNoStroke();
            var info = FindAnchor(anchorId) ?? throw new KeyNotFoundException("Anchor not found: " + anchorId);
            var layer = info.Layer; var anchor = info.Anchor;
            RefuseLockedAttributes(layer);
            if (anchor.Placement == AnchorPlacement.Layer) Execute(new DelegateCommand(() => layer.Anchor = null, () => layer.Anchor = anchor, 64));
            else { var mask = layer.Mask; Execute(new DelegateCommand(() => mask.Anchor = null, () => mask.Anchor = anchor, 64)); }
        }

        /// <summary>Renames an anchor as one undo step (what it holds and who reads it do not change).</summary>
        public void RenameAnchor(Guid anchorId, string name)
        {
            EnsureNoStroke(); AnchorPoint.CheckName(name);
            var info = FindAnchor(anchorId) ?? throw new KeyNotFoundException("Anchor not found: " + anchorId);
            var anchor = info.Anchor; string old = anchor.Name; if (old == name) return;
            Execute(new DelegateCommand(() => anchor.Name = name, () => anchor.Name = old, 64 + 2L * (old.Length + name.Length)));
        }

        /// <summary>Why a generator on the layer cannot read the anchor (it is gone, not below the layer, or on the layer itself), or null when
        /// it can. Checked with the dependency graph like the references of the document's own generators.</summary>
        public string AnchorReferenceRefusal(Guid layerId, Guid anchorId)
        {
            var layer = GetLayer(layerId); RefreshAnchors();
            if (anchorId == Guid.Empty) return null; // 選ばない（入力のまま通す）は断らない
            var probe = new FilterEffect(Guid.NewGuid(), FilterSettings.FromGenerator(GeneratorSettings.Default(GeneratorType.Anchor).WithAnchor(anchorId, PaintChannel.Height, AnchorRead.Value)), true, 1, null);
            var b = ResolveBinding(probe, layer, FilterTarget.Content, 0, new DependencyGraph(), new Dictionary<(Guid, int), Guid>());
            return b.Valid ? null : b.Reason;
        }

        /// <summary>The anchors a generator on the layer can read (below it), bottom to top.</summary>
        public IReadOnlyList<AnchorInfo> AnchorsReadableFrom(Guid layerId)
        {
            GetLayer(layerId); var result = new List<AnchorInfo>();
            foreach (var info in Anchors) if (AnchorReferenceRefusal(layerId, info.Anchor.Id) == null) result.Add(info);
            return result.AsReadOnly();
        }

        /// <summary>Chooses what an anchor generator reads, as one undo step (slider-like changes coalesce with coalesce). Refused (nothing
        /// changes) when the anchor is not one the layer can read (<see cref="AnchorReferenceRefusal"/>), the stage is not an anchor generator,
        /// or the channel is Normal. Guid.Empty reads nothing (the stage passes its input through).</summary>
        public void SetGeneratorAnchor(Guid layerId, Guid filterId, Guid anchorId, PaintChannel channel, AnchorRead read, bool coalesce = false)
        {
            EnsureNoStroke();
            var effect = FindFilter(layerId, filterId, out _) ?? throw new KeyNotFoundException("Filter not found on layer: " + filterId);
            if (!effect.Settings.ReadsAnchor) throw new ArgumentException(effect.Settings.Name + " is not an anchor generator.", nameof(filterId));
            string why = AnchorReferenceRefusal(layerId, anchorId);
            if (why != null) throw new InvalidOperationException(why + " Nothing was changed.");
            SetFilterSettings(layerId, filterId, effect.Settings.WithGenerator(effect.Settings.Generator.WithAnchor(anchorId, channel, read)), coalesce);
        }

        /// <summary>Every anchor generator whose reference is not usable now (none chosen, the anchor gone, or not below its layer), bottom to
        /// top. Each passes its input through.</summary>
        public IReadOnlyList<AnchorIssue> AnchorIssues()
        {
            RefreshAnchors(); var issues = new List<AnchorIssue>();
            foreach (var b in anchorBindingList)
                if (!b.Valid) issues.Add(new AnchorIssue(b.Reader.Id, b.FilterId, b.Target, b.AnchorRefId, b.Kind, b.Reason, b.GraphReason));
            return issues.AsReadOnly();
        }

        /// <summary>The generators that read an anchor (usable or not), bottom to top: the layer, the stage and its stack.</summary>
        public IReadOnlyList<(PaintLayer layer, FilterEffect stage, FilterTarget target)> AnchorReaders(Guid anchorId)
        {
            RefreshAnchors(); var result = new List<(PaintLayer, FilterEffect, FilterTarget)>();
            foreach (var b in anchorBindingList) if (b.AnchorRefId == anchorId) result.Add((b.Reader, b.Effect, b.Target));
            return result.AsReadOnly();
        }

        /// <summary>True when a stack of the document holds an anchor generator (on or off).</summary>
        public bool HasAnchorReaders { get { RefreshAnchors(); return anchorBindingList.Count > 0; } }

        /// <summary>Budget of cached layer-anchor tiles (derived display state, least recently used first out). 0 caches nothing (each
        /// evaluation composites what it needs).</summary>
        public long AnchorCacheBudgetBytes
        {
            get { return anchorCacheBudget; }
            set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); anchorCacheBudget = value; TrimAnchorCache(); }
        }
        /// <summary>Bytes of layer-anchor tiles cached now.</summary>
        public long AnchorCacheBytes { get { return anchorTileBytes; } }
        /// <summary>Layer-anchor tiles composited so far (diagnostics, tests).</summary>
        public long AnchorTilesComposited { get; private set; }

        // ───────────── loaders, copies ─────────────

        /// <summary>For loaders: puts an anchor without history (refuses a duplicate ID, a second anchor of the placement, a mask anchor without
        /// a mask).</summary>
        internal void SetAnchorForLoad(PaintLayer layer, AnchorPoint anchor)
        {
            if (anchor == null) throw new ArgumentNullException(nameof(anchor));
            foreach (var l in layers)
                if (l.Anchor != null && l.Anchor.Id == anchor.Id || l.Mask?.Anchor != null && l.Mask.Anchor.Id == anchor.Id) throw new ArgumentException("Duplicate anchor ID " + anchor.Id + ".");
            if (anchor.Placement == AnchorPlacement.Layer)
            {
                if (layer.Anchor != null) throw new ArgumentException("'" + layer.Name + "' has two anchors.");
                layer.Anchor = anchor;
            }
            else
            {
                if (layer.Mask == null) throw new ArgumentException("'" + layer.Name + "' has a mask anchor but no mask.");
                if (layer.Mask.Anchor != null) throw new ArgumentException("The mask of '" + layer.Name + "' has two anchors.");
                layer.Mask.Anchor = anchor;
            }
            editSerial++;
        }
        /// <summary>For loaders, after every layer is read: refuses a generator that reads an anchor on its own layer (a value that would depend on
        /// itself; no edit makes one). References to anchors that are gone or not below stay as they are (they load inactive, as they were saved).</summary>
        internal void CheckAnchorReferencesForLoad()
        {
            RefreshAnchors();
            foreach (var b in anchorBindingList)
                if (b.Host != null && b.Host == b.Reader) throw new InvalidOperationException("A generator on '" + b.Reader.Name + "' reads the anchor on its own layer, a value that would depend on itself.");
        }
        /// <summary>The same anchors (IDs, names, placements) on another document's copy of a layer (resizing).</summary>
        internal static void CopyAnchors(PaintDocument copy, PaintLayer from, PaintLayer to)
        {
            if (from.Anchor != null) copy.SetAnchorForLoad(to, new AnchorPoint(from.Anchor.Id, from.Anchor.Name, AnchorPlacement.Layer));
            if (from.Mask?.Anchor != null && to.Mask != null) copy.SetAnchorForLoad(to, new AnchorPoint(from.Mask.Anchor.Id, from.Mask.Anchor.Name, AnchorPlacement.Mask));
        }
        /// <summary>A copy of an anchor under a new ID (old → new added to map when given).</summary>
        static AnchorPoint CloneAnchor(AnchorPoint source, IDictionary<Guid, Guid> map)
        {
            var copy = new AnchorPoint(Guid.NewGuid(), source.Name, source.Placement);
            if (map != null) map[source.Id] = copy.Id;
            return copy;
        }
        /// <summary>Points the anchor generators of copied layers that read an anchor copied with them at the copy (map: old → new). References to
        /// other anchors are kept.</summary>
        internal void RemapAnchorReferences(IEnumerable<PaintLayer> copies, IDictionary<Guid, Guid> map)
        {
            if (map == null || map.Count == 0) return;
            foreach (var l in copies)
            {
                Remap(l.FilterList); if (l.Mask != null) Remap(l.Mask.FilterList);
            }
            editSerial++;
            void Remap(List<FilterEffect> stack)
            {
                for (int i = 0; i < stack.Count; i++)
                {
                    var e = stack[i]; if (!e.Settings.ReadsAnchor) continue;
                    var g = e.Settings.Generator;
                    if (map.TryGetValue(g.AnchorId, out var to)) stack[i] = e.With(settings: e.Settings.WithGenerator(g.WithAnchor(to, g.AnchorChannel, g.AnchorRead)));
                }
            }
        }

        /// <summary>The layer's index in <see cref="Layers"/> (−1 when it is not in this document).</summary>
        internal int IndexOfLayer(PaintLayer layer) { return layers.IndexOf(layer); }

        // ───────────── resolution (rebuilt when the document changes) ─────────────

        List<AnchorInfo> anchorList = new List<AnchorInfo>();
        Dictionary<Guid, AnchorInfo> anchorTable = new Dictionary<Guid, AnchorInfo>();
        Dictionary<Guid, AnchorBinding> anchorBindings = new Dictionary<Guid, AnchorBinding>();
        List<AnchorBinding> anchorBindingList = new List<AnchorBinding>();
        long anchorsSeenEdit = -1, anchorsSeenInputs = -1;
        string anchorSignature = "";
        /// <summary>The change serial and the version clock of the last change that cannot be followed tile by tile.</summary>
        long anchorEpochSerial, anchorEpochClock;
        /// <summary>The clock of anchor versions (only grows).</summary>
        long anchorClock;

        /// <summary>Looks at the anchors and their readers again when the document changed since the last look: rebuilds the table and the
        /// references (checked with the dependency graph) and, when the anchors' arrangement or a reference's usability changed, counts a change
        /// everywhere for every reader (the change serial moves on).</summary>
        internal void RefreshAnchors()
        {
            if (anchorsSeenEdit == editSerial && anchorsSeenInputs == inputsRevision) return;
            anchorsSeenEdit = editSerial; anchorsSeenInputs = inputsRevision;
            var list = new List<AnchorInfo>(); var table = new Dictionary<Guid, AnchorInfo>();
            foreach (var layer in layers)
            {
                if (layer.Anchor != null && !table.ContainsKey(layer.Anchor.Id)) { var info = new AnchorInfo(layer.Anchor, layer); list.Add(info); table.Add(layer.Anchor.Id, info); }
                var m = layer.Mask?.Anchor;
                if (m != null && !table.ContainsKey(m.Id)) { var info = new AnchorInfo(m, layer); list.Add(info); table.Add(m.Id, info); }
            }
            anchorList = list; anchorTable = table;
            var bindings = new Dictionary<Guid, AnchorBinding>(); var bindingList = new List<AnchorBinding>();
            var graph = new DependencyGraph(); var nodes = new Dictionary<(Guid, int), Guid>();
            foreach (var layer in layers)
            {
                for (int i = 0; i < layer.FilterList.Count; i++) Bind(layer.FilterList[i], layer, FilterTarget.Content, i);
                if (layer.Mask != null) for (int i = 0; i < layer.Mask.FilterList.Count; i++) Bind(layer.Mask.FilterList[i], layer, FilterTarget.Mask, PaintDocument.MaxFiltersPerStack + i);
            }
            anchorBindings = bindings; anchorBindingList = bindingList;
            string signature = list.Count == 0 && bindingList.Count == 0 ? "" : AnchorSignature();
            if (signature != anchorSignature) { anchorSignature = signature; anchorEpochSerial = ++changeSerial; anchorEpochClock = ++anchorClock; anchorClosure = null; }
            foreach (var key in anchorVersions.Keys.Where(k => !table.ContainsKey(k.Item1)).ToList()) anchorVersions.Remove(key);
            foreach (var key in anchorTiles.Keys.Where(k => !table.ContainsKey(k.Item1)).ToList()) DropAnchorTiles(key);
            void Bind(FilterEffect e, PaintLayer layer, FilterTarget target, int order)
            {
                if (!e.Settings.ReadsAnchor || bindings.ContainsKey(e.Id)) return;
                var b = ResolveBinding(e, layer, target, order, graph, nodes);
                bindings.Add(e.Id, b); bindingList.Add(b);
            }
        }

        /// <summary>Resolves one anchor generator stage: the anchor it names, and whether the dependency graph accepts the edge from the anchor
        /// (its layer's order, above every stage of that layer) to the stage (its layer's order and its place in the stack).</summary>
        AnchorBinding ResolveBinding(FilterEffect e, PaintLayer reader, FilterTarget target, int order, DependencyGraph graph, Dictionary<(Guid, int), Guid> nodes)
        {
            var g = e.Settings.Generator;
            var b = new AnchorBinding { FilterId = e.Id, Effect = e, Reader = reader, Target = target, Channel = g.AnchorChannel, Read = g.AnchorRead, AnchorRefId = g.AnchorId };
            string where = "'" + reader.Name + "'" + (target == FilterTarget.Mask ? " (mask)" : "");
            if (g.AnchorId == Guid.Empty)
            {
                b.Kind = AnchorIssueKind.NotChosen;
                b.Reason = "The anchor generator on " + where + " has no anchor chosen yet: choose one below this layer in its settings. Until then it passes its input through.";
                return b;
            }
            if (!anchorTable.TryGetValue(g.AnchorId, out var info))
            {
                b.Kind = AnchorIssueKind.Missing;
                b.Reason = "The anchor the generator on " + where + " reads is gone (removed, or its layer or mask was deleted or merged). Choose another anchor, or undo; until then it passes its input through.";
                return b;
            }
            b.Anchor = info.Anchor; b.Host = info.Layer;
            bool mask = info.Anchor.Placement == AnchorPlacement.Mask;
            var type = mask ? GraphValueType.Scalar : FilterSettings.ValueTypeOf(g.AnchorChannel);
            PaintChannel? channel = mask ? (PaintChannel?)null : g.AnchorChannel;
            int readerIndex = layers.IndexOf(reader), hostIndex = layers.IndexOf(info.Layer);
            if (readerIndex < 0 || hostIndex < 0)
            {
                b.Kind = AnchorIssueKind.Missing; b.Reason = "The anchor or the layer reading it is not in this document."; return b;
            }
            // 依存のグラフ: Anchor（その層の、どの段よりも上）→ 読む段（その層の、スタックの位置）。下の層からだけ・同じテクスチャセット・型・循環なし
            var stageNode = new DependencyNode(Guid.NewGuid(), Id, GraphNodeKind.Generator, GraphValueType.Scalar, readerIndex, order, new[] { new GraphInput("anchor", type, channel) });
            graph.AddNode(stageNode);
            if (!nodes.TryGetValue((info.Anchor.Id, b.Key), out var anchorNode))
            {
                anchorNode = Guid.NewGuid(); nodes.Add((info.Anchor.Id, b.Key), anchorNode);
                graph.AddNode(new DependencyNode(anchorNode, Id, GraphNodeKind.Anchor, type, hostIndex, AnchorEffectOrder, null, channel));
            }
            if (!graph.TryConnect(anchorNode, stageNode.Id, "anchor", out var refusal))
            {
                b.Kind = AnchorIssueKind.NotBelow; b.GraphReason = refusal;
                b.Reason = info.Layer == reader
                    ? "The generator on " + where + " reads the anchor '" + info.Anchor.Name + "' on its own layer; an anchor holds the stack through its layer, so only layers above it can read it. Choose an anchor below this layer."
                    : "The anchor '" + info.Anchor.Name + "' is on '" + info.Layer.Name + "', which is not below " + where + " (the layers were moved); only layers above an anchor can read it. Move '"
                      + info.Layer.Name + "' below, choose another anchor, or undo. Until then the generator passes its input through.";
                return b;
            }
            b.Valid = true;
            return b;
        }

        /// <summary>What the anchors hold and who may read them, as far as it cannot be followed tile by tile: each layer anchor with the
        /// layers at or below its layer (order and groups) and the groups around it (blend modes in every channel, clipping); each mask anchor
        /// with its mask's on/off, invert, density and filters (and the mesh maps when they hold generators); each reference with its anchor
        /// and whether it is usable.</summary>
        string AnchorSignature()
        {
            var sb = new StringBuilder(256); var c = CultureInfo.InvariantCulture;
            foreach (var info in anchorList)
            {
                var a = info.Anchor; var host = info.Layer; int at = layers.IndexOf(host);
                sb.Append(a.Placement == AnchorPlacement.Layer ? 'L' : 'M').Append(a.Id.ToString("N")).Append('@').Append(host.Id.ToString("N"));
                if (a.Placement == AnchorPlacement.Layer)
                {
                    sb.Append('[');
                    for (int i = 0; i <= at; i++) sb.Append(layers[i].Id.ToString("N")).Append('/').Append(layers[i].ParentId.ToString("N")).Append(';');
                    sb.Append(']');
                    for (var p = host.ParentId; p != Guid.Empty;)
                    {
                        var g = GetLayer(p); sb.Append('^').Append(g.Id.ToString("N")).Append(IsEffectivelyClipped(layers.IndexOf(g)) ? 'c' : '-');
                        foreach (PaintChannel ch in Enum.GetValues(typeof(PaintChannel))) sb.Append((int)g.BlendModeIn(ch)).Append(',');
                        p = g.ParentId;
                    }
                }
                else
                {
                    var m = host.Mask;
                    sb.Append(m.Enabled ? 'e' : '-').Append(m.Inverted ? 'i' : '-').Append(m.Density.ToString("R", c)).Append(':').Append(m.FilterRevision);
                    if (ContainsGenerator(m.FilterList.ToArray())) sb.Append(":g").Append(generatorRevision);
                }
                sb.Append('|');
            }
            foreach (var b in anchorBindingList) sb.Append(b.FilterId.ToString("N")).Append('>').Append(b.AnchorRefId.ToString("N")).Append(b.Valid ? '+' : '-').Append(b.Key).Append('|');
            return sb.ToString();
        }

        /// <summary>The resolved references of a chain's anchor stages by index (null when it has none).</summary>
        internal AnchorBinding[] AnchorBindingsOf(FilterEffect[] chain)
        {
            AnchorBinding[] result = null;
            for (int i = 0; i < chain.Length; i++)
            {
                if (!chain[i].Settings.ReadsAnchor) continue;
                if (result == null) { RefreshAnchors(); result = new AnchorBinding[chain.Length]; }
                result[i] = AnchorBindingOf(chain[i].Id) ?? new AnchorBinding { FilterId = chain[i].Id, Effect = chain[i], Kind = AnchorIssueKind.Missing, Reason = "The stage is not in this document." };
            }
            return result;
        }
        AnchorBinding AnchorBindingOf(Guid filterId) { RefreshAnchors(); return anchorBindings.TryGetValue(filterId, out var b) ? b : null; }

        /// <summary>The reason a generator's settings alone cannot read anything (no anchor chosen, or it is gone), or null.</summary>
        string AnchorSettingsReason(GeneratorSettings settings)
        {
            if (settings.AnchorId == Guid.Empty) return "No anchor is chosen yet: choose one below the layer in the generator's settings.";
            return FindAnchor(settings.AnchorId) == null ? "The anchor it reads is gone (removed, or its layer or mask was deleted or merged)." : null;
        }

        // ───────────── change tracking ─────────────

        /// <summary>Per layer with a mask anchor: the change serial of each mask tile that changed since the anchor was put there.</summary>
        readonly Dictionary<Guid, Dictionary<TileCoord, long>> maskAnchorSerials = new Dictionary<Guid, Dictionary<TileCoord, long>>();
        void RecordMaskAnchorTile(PaintLayer layer, TileCoord coord)
        {
            if (layer.Mask?.Anchor == null) return;
            if (!maskAnchorSerials.TryGetValue(layer.Id, out var serials)) maskAnchorSerials.Add(layer.Id, serials = new Dictionary<TileCoord, long>());
            serials[coord] = ++changeSerial;
        }

        /// <summary>The tiles that may have changed after a change serial, per channel and per mask anchor, with the readers followed. Per channel
        /// and tile, the lowest layer (index in <see cref="Layers"/>) at which the channel's stack may have changed there: what the display reads
        /// (<see cref="AddRawChanges"/>, counted at layer 0 since it does not say which layer), plus, for each usable anchor generator, the
        /// changes of what it reads (for a layer anchor, the changes at or below its layer) grown by the reader's stack halo, at the reader's layer,
        /// in the channels its output reaches (a mask stack: every channel its layer covers, and its own mask anchor). A reader's own output
        /// therefore never feeds the anchors below it. AllAt: the lowest layer at which everything may have changed (int.MaxValue: none).</summary>
        sealed class ChangeClosure
        {
            public long Since, Serial;
            public readonly Dictionary<TileCoord, int>[] Channels = new Dictionary<TileCoord, int>[6];
            public readonly int[] AllAt = { int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue };
            public readonly Dictionary<PaintLayer, HashSet<TileCoord>> Masks = new Dictionary<PaintLayer, HashSet<TileCoord>>();
            public readonly HashSet<PaintLayer> MaskAll = new HashSet<PaintLayer>();
            /// <summary>What a usable reference reads changed: everything, or these tiles.</summary>
            public bool Source(AnchorBinding b, int hostIndex, out List<TileCoord> tiles)
            {
                tiles = null;
                if (b.Mask)
                {
                    if (MaskAll.Contains(b.Host)) return true;
                    if (Masks.TryGetValue(b.Host, out var set) && set.Count > 0) tiles = set.ToList();
                    return false;
                }
                if (AllAt[b.Key] <= hostIndex) return true;
                foreach (var e in Channels[b.Key]) if (e.Value <= hostIndex) (tiles ?? (tiles = new List<TileCoord>())).Add(e.Key);
                return false;
            }
        }
        ChangeClosure anchorClosure;

        ChangeClosure ClosureSince(long since)
        {
            if (anchorClosure != null && anchorClosure.Since == since && anchorClosure.Serial == changeSerial) return anchorClosure;
            var c = new ChangeClosure { Since = since, Serial = changeSerial };
            foreach (PaintChannel ch in Enum.GetValues(typeof(PaintChannel)))
            {
                var set = new HashSet<TileCoord>(); AddRawChanges(ch, since, set);
                var tiles = new Dictionary<TileCoord, int>(set.Count); foreach (var t in set) tiles[t] = 0; // どの層かは分からない: 一番下で変わったとみなす
                c.Channels[(int)ch] = tiles;
            }
            int columns = (Width + TileSize - 1) / TileSize, rows = (Height + TileSize - 1) / TileSize;
            foreach (var info in anchorList)
            {
                if (info.Anchor.Placement != AnchorPlacement.Mask) continue;
                var set = new HashSet<TileCoord>(); var host = info.Layer;
                if (maskAnchorSerials.TryGetValue(host.Id, out var serials)) foreach (var entry in serials) if (entry.Value > since) set.Add(entry.Key);
                var chain = host.Mask.ActiveChain();
                if (chain.Length > 0 && filterJournal.TryGetValue((host.Id, FilterEngine.MaskKey), out var journal)) Spread(journal, since, chain, columns, rows, set, EnumerateCanvasTiles);
                c.Masks[host] = set;
            }
            bool epoch = anchorEpochSerial > since, grew = true;
            // 読む元はいつも読む層より下なので、下から順に回せば数回で止まる（念のため参照の数 + 1 回まで）
            for (int round = 0; grew && round <= anchorBindingList.Count; round++)
            {
                grew = false;
                foreach (var b in anchorBindingList)
                {
                    // 使えない参照の段は入力のまま通す: 出力が変わるのは、使える・使えないが変わりうるとき（全体の変化）だけ
                    if (!b.Effect.IsActive || !b.Valid && !epoch) continue;
                    int at = layers.IndexOf(b.Reader);
                    List<TileCoord> source = null;
                    bool all = epoch || c.Source(b, layers.IndexOf(b.Host), out source);
                    if (!all && source == null) continue;
                    var reader = b.Reader;
                    if (b.Target == FilterTarget.Content)
                    {
                        foreach (PaintChannel ch in Enum.GetValues(typeof(PaintChannel)))
                        {
                            var chain = reader.ActiveChain(ch);
                            if (Array.IndexOf(chain, b.Effect) >= 0) grew |= GrowInto(c, (int)ch, at, all, source, chain, columns, rows);
                        }
                    }
                    else if (reader.Mask != null)
                    {
                        var chain = reader.Mask.ActiveChain();
                        if (Array.IndexOf(chain, b.Effect) < 0) continue;
                        foreach (var ch in reader.CoveredChannels) grew |= GrowInto(c, (int)ch, at, all, source, chain, columns, rows);
                        if (reader.Mask.Anchor != null && c.Masks.TryGetValue(reader, out var own) && !c.MaskAll.Contains(reader))
                        {
                            if (all || FilterEngine.IsGlobal(chain, chain.Length)) { c.MaskAll.Add(reader); grew = true; }
                            else foreach (var t in Grown(source, chain, columns, rows)) grew |= own.Add(t);
                        }
                    }
                }
            }
            return anchorClosure = c;
        }
        /// <summary>Adds the source grown by the chain's halo (everything for a global chain or an "everything" source) to a channel at a layer.
        /// True when something was added or moved lower.</summary>
        bool GrowInto(ChangeClosure c, int channel, int at, bool all, List<TileCoord> source, FilterEffect[] chain, int columns, int rows)
        {
            if (c.AllAt[channel] <= at) return false;
            if (all || FilterEngine.IsGlobal(chain, chain.Length)) { c.AllAt[channel] = at; return true; }
            var tiles = c.Channels[channel]; bool grew = false;
            foreach (var t in Grown(source, chain, columns, rows))
                if (!tiles.TryGetValue(t, out int old) || old > at) { tiles[t] = at; grew = true; }
            return grew;
        }
        IEnumerable<TileCoord> Grown(List<TileCoord> source, FilterEffect[] chain, int columns, int rows)
        {
            int m = (FilterEngine.Halo(chain, chain.Length) + TileSize - 1) / TileSize;
            if (m == 0) { foreach (var t in source) yield return t; yield break; }
            var seen = new HashSet<TileCoord>();
            foreach (var t in source)
                for (int y = Math.Max(0, t.Y - m); y <= Math.Min(rows - 1, t.Y + m); y++)
                    for (int x = Math.Max(0, t.X - m); x <= Math.Min(columns - 1, t.X + m); x++) { var g = new TileCoord(x, y); if (seen.Add(g)) yield return g; }
        }

        /// <summary>TryGetChangedTiles with anchor readers: the closure's tiles of the channel.</summary>
        void AddAnchorClosure(PaintChannel channel, long since, ICollection<TileCoord> changed)
        {
            var c = ClosureSince(since);
            if (c.AllAt[(int)channel] != int.MaxValue) { foreach (var t in EnumerateCanvasTiles()) changed.Add(t); return; }
            foreach (var t in c.Channels[(int)channel].Keys) changed.Add(t);
        }

        // ───────────── versions ─────────────

        sealed class AnchorVersions { public long Base, Pulled; public readonly Dictionary<TileCoord, long> Tiles = new Dictionary<TileCoord, long>(); }
        readonly Dictionary<(Guid, int), AnchorVersions> anchorVersions = new Dictionary<(Guid, int), AnchorVersions>();

        /// <summary>A reader's part of a filter stamp over the tiles [tx0, tx1) × [ty0, ty1): the largest version of what it reads there (each
        /// tile's version only grows), or, for a reference that is not usable, the clock of the last change that could have made it so.</summary>
        internal long AnchorStampPart(AnchorBinding b, int tx0, int ty0, int tx1, int ty1)
        {
            RefreshAnchors();
            if (!b.Valid) return anchorEpochClock;
            var v = VersionsOf(b);
            int columns = (Width + TileSize - 1) / TileSize, rows = (Height + TileSize - 1) / TileSize;
            long max = v.Base;
            for (int y = Math.Max(0, ty0); y < Math.Min(rows, ty1); y++)
                for (int x = Math.Max(0, tx0); x < Math.Min(columns, tx1); x++)
                    if (v.Tiles.TryGetValue(new TileCoord(x, y), out long s) && s > max) max = s;
            return max;
        }
        /// <summary>The per-tile versions of what a usable reference reads, brought up to the current change serial.</summary>
        AnchorVersions VersionsOf(AnchorBinding b)
        {
            var key = (b.Anchor.Id, b.Key);
            if (!anchorVersions.TryGetValue(key, out var v)) { anchorVersions.Add(key, v = new AnchorVersions { Base = ++anchorClock, Pulled = changeSerial }); return v; }
            if (v.Pulled == changeSerial) return v;
            if (anchorEpochSerial > v.Pulled) { v.Base = ++anchorClock; v.Tiles.Clear(); }
            else
            {
                var c = ClosureSince(v.Pulled);
                if (c.Source(b, layers.IndexOf(b.Host), out var tiles)) { v.Base = ++anchorClock; v.Tiles.Clear(); }
                else if (tiles != null) { long s = ++anchorClock; foreach (var t in tiles) v.Tiles[t] = s; }
            }
            v.Pulled = changeSerial;
            return v;
        }

        // ───────────── pixels ─────────────

        sealed class AnchorTile { public long Stamp; public byte[] Bytes; public long Used; }
        readonly Dictionary<(Guid, int), Dictionary<TileCoord, AnchorTile>> anchorTiles = new Dictionary<(Guid, int), Dictionary<TileCoord, AnchorTile>>();
        long anchorTileBytes, anchorTileClock, anchorCacheBudget = DefaultAnchorCacheBudgetBytes;

        /// <summary>The anchor's pixels over the tiles (calling thread): a layer anchor's composite tiles (cached ones whose version is current,
        /// the others composited in one call), or a mask anchor's filtered mask tiles.</summary>
        internal AnchorSample AnchorSampleFor(AnchorBinding b, ICollection<TileCoord> coords)
        {
            int tx0 = int.MaxValue, ty0 = int.MaxValue, tx1 = int.MinValue, ty1 = int.MinValue;
            foreach (var t in coords) { tx0 = Math.Min(tx0, t.X); ty0 = Math.Min(ty0, t.Y); tx1 = Math.Max(tx1, t.X); ty1 = Math.Max(ty1, t.Y); }
            if (coords.Count == 0) { tx0 = ty0 = 0; tx1 = ty1 = -1; }
            int columns = tx1 - tx0 + 1, rows = ty1 - ty0 + 1, tileBytes = TileSize * TileSize * 4;
            var tiles = new byte[Math.Max(0, columns * rows)][];
            if (b.Mask)
            {
                var mask = b.Host.Mask;
                foreach (var t in coords) { var bytes = new byte[tileBytes]; mask.CopyOutputTile(t, bytes); tiles[(t.Y - ty0) * columns + t.X - tx0] = bytes; }
                return AnchorSample.ForMask(TileSize, tx0, ty0, columns, rows, tiles, mask);
            }
            var key = (b.Anchor.Id, b.Key);
            if (!anchorTiles.TryGetValue(key, out var cache)) anchorTiles.Add(key, cache = new Dictionary<TileCoord, AnchorTile>());
            var missing = new List<TileCoord>(); var stamps = new List<long>();
            foreach (var t in coords)
            {
                long stamp = AnchorStampPart(b, t.X, t.Y, t.X + 1, t.Y + 1);
                if (cache.TryGetValue(t, out var entry) && entry.Stamp == stamp) { entry.Used = ++anchorTileClock; tiles[(t.Y - ty0) * columns + t.X - tx0] = entry.Bytes; }
                else { missing.Add(t); stamps.Add(stamp); }
            }
            if (missing.Count > 0)
            {
                var made = CpuCompositor.CompositeAnchorTiles(this, b.Channel, b.Host, missing);
                AnchorTilesComposited += missing.Count;
                for (int i = 0; i < missing.Count; i++)
                {
                    var t = missing[i]; tiles[(t.Y - ty0) * columns + t.X - tx0] = made[i];
                    if (anchorCacheBudget < tileBytes) continue;
                    if (cache.TryGetValue(t, out var old) && old.Bytes != null) anchorTileBytes -= old.Bytes.Length;
                    cache[t] = new AnchorTile { Stamp = stamps[i], Bytes = made[i], Used = ++anchorTileClock };
                    if (made[i] != null) anchorTileBytes += made[i].Length;
                }
                TrimAnchorCache();
            }
            return AnchorSample.ForLayer(TileSize, tx0, ty0, columns, rows, tiles, b.Channel, b.Read);
        }
        /// <summary>Drops least recently used anchor tiles until the cache is within three quarters of its budget.</summary>
        void TrimAnchorCache()
        {
            if (anchorTileBytes <= anchorCacheBudget) return;
            var all = new List<(long used, (Guid, int) key, TileCoord coord, int bytes)>();
            foreach (var t in anchorTiles) foreach (var e in t.Value) all.Add((e.Value.Used, t.Key, e.Key, e.Value.Bytes == null ? 0 : e.Value.Bytes.Length));
            all.Sort((a, b) => a.used.CompareTo(b.used));
            foreach (var item in all)
            {
                if (anchorTileBytes <= anchorCacheBudget * 3 / 4) break;
                anchorTiles[item.key].Remove(item.coord); anchorTileBytes -= item.bytes;
            }
        }
        void DropAnchorTiles((Guid, int) key)
        {
            if (!anchorTiles.TryGetValue(key, out var cache)) return;
            foreach (var e in cache.Values) if (e.Bytes != null) anchorTileBytes -= e.Bytes.Length;
            anchorTiles.Remove(key);
        }
        /// <summary>Forgets every cached anchor tile (they are made again when read).</summary>
        public void ReleaseAnchorCache() { anchorTiles.Clear(); anchorTileBytes = 0; }
    }
}
