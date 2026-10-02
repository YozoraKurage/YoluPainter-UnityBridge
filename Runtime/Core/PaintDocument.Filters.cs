using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>The mask's own filter stack (filters the stored hide amount; shared by all channels, kept apart from the layer
    /// pixels' stack).</summary>
    public sealed partial class RasterMask
    {
        readonly List<FilterEffect> filters = new List<FilterEffect>();
        ReadOnlyCollection<FilterEffect> filtersView;
        /// <summary>The mask's filters, applied in order to the stored hide amount before Enabled / Inverted / Density.</summary>
        public IReadOnlyList<FilterEffect> Filters { get { return filtersView ?? (filtersView = filters.AsReadOnly()); } }
        /// <summary>Changes on every edit (and undo / redo) of the mask's filter stack; unique within the document.</summary>
        public long FilterRevision { get; internal set; }
        internal PaintLayer Owner { get; set; }
        internal List<FilterEffect> FilterList { get { return filters; } }
        static readonly FilterEffect[] NoChain = new FilterEffect[0];
        internal FilterEffect[] ActiveChain()
        {
            if (filters.Count == 0) return NoChain;
            var chain = new List<FilterEffect>(); foreach (var e in filters) if (e.IsActive) chain.Add(e);
            return chain.Count == 0 ? NoChain : chain.ToArray();
        }
        public bool HasActiveFilters { get { foreach (var e in filters) if (e.IsActive) return true; return false; } }
        /// <summary>The filtered hide amount of one tile (alpha; RGB zero), or the stored tile when no filter is active.</summary>
        public bool CopyOutputTile(TileCoord coord, byte[] destination)
        {
            var s = FilterEngine.MaskSource(this);
            return s == null ? Surface.CopyTile(coord, destination) : Owner.Document.FilterEvaluator.CopyTile(s, coord, destination);
        }
        /// <summary>The filtered hide amount (0..255) of one pixel.</summary>
        public byte OutputHideAt(int x, int y)
        {
            var s = FilterEngine.MaskSource(this);
            if (s == null) return Surface.GetPixel(x, y).A;
            if (x < 0 || y < 0 || x >= Surface.Width || y >= Surface.Height) throw new ArgumentOutOfRangeException("pixel");
            return Owner.Document.FilterEvaluator.GetPixel(s, x, y).A;
        }
        /// <summary>Validity stamp of the filtered tiles [tx0, tx1) × [ty0, ty1): equal stamps mean equal output.</summary>
        public FilterStamp OutputStamp(int tx0, int ty0, int tx1, int ty1)
        {
            var s = FilterEngine.MaskSource(this);
            return s == null ? new FilterStamp(0, Surface.MaxTileRevision(tx0, ty0, tx1, ty1)) : Owner.Document.FilterEvaluator.Stamp(s, tx0, ty0, tx1, ty1);
        }
        /// <summary>Reference evaluation of the region in one piece (no blocks, no cache): hide amounts, row-major from the
        /// bottom row. Refused above <see cref="PaintDocument.FilterWorkingBudgetBytes"/>.</summary>
        public byte[] EvaluateOutputRegion(int x, int y, int width, int height)
        {
            CheckRegion(x, y, width, height, Surface.Width, Surface.Height);
            var result = new byte[checked(width * height)];
            var s = FilterEngine.MaskSource(this);
            if (s == null) { for (int j = 0; j < height; j++) for (int i = 0; i < width; i++) result[j * width + i] = Surface.GetPixel(x + i, y + j).A; return result; }
            var grey = Owner.Document.FilterEvaluator.EvaluateRect(s, s.Chain.Length, new FilterEngine.Rect(x, y, x + width, y + height));
            for (int i = 0; i < result.Length; i++) result[i] = grey[i * 4];
            return result;
        }
        internal static void CheckRegion(int x, int y, int width, int height, int w, int h)
        {
            if (width <= 0 || height <= 0 || x < 0 || y < 0 || (long)x + width > w || (long)y + height > h) throw new ArgumentOutOfRangeException("region");
        }
    }

    /// <summary>The layer's own filter stack (applied to its pixels, per channel, before mask, opacity and blending).</summary>
    public sealed partial class PaintLayer
    {
        readonly List<FilterEffect> filters = new List<FilterEffect>();
        ReadOnlyCollection<FilterEffect> filtersView;
        static readonly FilterEffect[] NoChain = new FilterEffect[0];
        /// <summary>The filters on the layer's pixels, bottom (applied first) to top. Each lists the channels it applies to.</summary>
        public IReadOnlyList<FilterEffect> Filters { get { return filtersView ?? (filtersView = filters.AsReadOnly()); } }
        /// <summary>Changes on every edit (and undo / redo) of the layer's filter stack; unique within the document.</summary>
        public long FilterRevision { get; internal set; }
        internal PaintDocument Document { get { return document; } }
        internal List<FilterEffect> FilterList { get { return filters; } }
        /// <summary>The active filters (on, strength above zero) that apply to the channel, in order.</summary>
        internal FilterEffect[] ActiveChain(PaintChannel channel)
        {
            if (filters.Count == 0) return NoChain;
            List<FilterEffect> chain = null;
            foreach (var e in filters) if (e.IsActive && e.AppliesTo(channel)) (chain ?? (chain = new List<FilterEffect>())).Add(e);
            return chain == null ? NoChain : chain.ToArray();
        }
        public bool HasActiveFilters(PaintChannel channel)
        { foreach (var e in filters) if (e.IsActive && e.AppliesTo(channel)) return true; return false; }
        /// <summary>The layer's pixels after its filters (before mask, opacity and blending) for one tile, padding zero. Without
        /// an active filter this is <see cref="CopyTile"/>. False (and zeros) where the output has nothing.</summary>
        public bool CopyOutputTile(PaintChannel channel, TileCoord coord, byte[] destination)
        {
            var s = FilterEngine.ContentSource(this, channel);
            return s == null ? CopyTile(channel, coord, destination) : document.FilterEvaluator.CopyTile(s, coord, destination);
        }
        /// <summary>The layer's pixel after its filters (before mask, opacity and blending).</summary>
        public Rgba32 GetOutputPixel(PaintChannel channel, int x, int y)
        {
            var s = FilterEngine.ContentSource(this, channel);
            if (s == null) return GetPixel(channel, x, y);
            if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) throw new ArgumentOutOfRangeException("pixel");
            return document.FilterEvaluator.GetPixel(s, x, y);
        }
        /// <summary>True when the filtered output can have pixels in the tiles [tx0, tx1) × [ty0, ty1).</summary>
        public bool OutputMayCover(PaintChannel channel, int tx0, int ty0, int tx1, int ty1)
        {
            var s = FilterEngine.ContentSource(this, channel);
            if (s != null) return document.FilterEvaluator.MayCover(s, tx0, ty0, tx1, ty1);
            if (Kind == LayerKind.Fill) return HasContent(channel);
            if (Kind != LayerKind.Raster || !channels.TryGetValue(channel, out var surface)) return false;
            for (int y = ty0; y < ty1; y++) for (int x = tx0; x < tx1; x++) if (surface.HasTile(new TileCoord(x, y))) return true;
            return false;
        }
        /// <summary>Validity stamp of the filtered tiles [tx0, tx1) × [ty0, ty1): equal stamps mean equal output.</summary>
        public FilterStamp OutputStamp(PaintChannel channel, int tx0, int ty0, int tx1, int ty1)
        {
            var s = FilterEngine.ContentSource(this, channel);
            if (s != null) return document.FilterEvaluator.Stamp(s, tx0, ty0, tx1, ty1);
            return new FilterStamp(0, channels.TryGetValue(channel, out var surface) ? surface.MaxTileRevision(tx0, ty0, tx1, ty1) : 0);
        }
        /// <summary>Reference evaluation of the region in one piece (no blocks, no cache), straight RGBA8 row-major from the bottom
        /// row. Refused above <see cref="PaintDocument.FilterWorkingBudgetBytes"/>.</summary>
        public byte[] EvaluateOutputRegion(PaintChannel channel, int x, int y, int width, int height)
        {
            RasterMask.CheckRegion(x, y, width, height, document.Width, document.Height);
            var s = FilterEngine.ContentSource(this, channel);
            if (s != null) return document.FilterEvaluator.EvaluateRect(s, s.Chain.Length, new FilterEngine.Rect(x, y, x + width, y + height));
            var result = new byte[checked(width * height * 4)];
            for (int j = 0; j < height; j++) for (int i = 0; i < width; i++)
            {
                var p = GetPixel(channel, x + i, y + j); int o = (j * width + i) * 4;
                result[o] = p.R; result[o + 1] = p.G; result[o + 2] = p.B; result[o + 3] = p.A;
            }
            return result;
        }
        /// <summary>The raster tiles where the filtered output can have pixels: the source tiles grown by the blur reach.</summary>
        IEnumerable<TileCoord> OutputContentTiles(SparseTileSurface surface, PaintChannel channel)
        {
            var chain = ActiveChain(channel); int reach = chain.Length == 0 ? 0 : FilterEngine.Expansion(chain);
            if (reach == 0) return surface.EnumerateTileCoordinates();
            int m = (reach + document.TileSize - 1) / document.TileSize, columns = (document.Width + document.TileSize - 1) / document.TileSize, rows = (document.Height + document.TileSize - 1) / document.TileSize;
            var set = new HashSet<TileCoord>();
            foreach (var c in surface.EnumerateTileCoordinates())
                for (int y = Math.Max(0, c.Y - m); y <= Math.Min(rows - 1, c.Y + m); y++)
                    for (int x = Math.Max(0, c.X - m); x <= Math.Min(columns - 1, c.X + m); x++) set.Add(new TileCoord(x, y));
            var list = new List<TileCoord>(set); list.Sort(); return list.AsReadOnly();
        }
    }

    /// <summary>Non-destructive filter stacks: editing (each change one undo step; slider drags coalesce), budgets, change
    /// tracking with halos, and baking into pixels.
    /// <para>A layer's content stack applies to raster or fill pixels per channel (each filter lists its channels and is refused
    /// for a channel whose value type it does not accept). A mask stack filters the hide amount. The painted source is never
    /// changed; the compositors read <see cref="PaintLayer.CopyOutputTile"/> / <see cref="RasterMask.CopyOutputTile"/>.</para>
    /// <para>Change tracking: a filter-stack edit marks every tile the layer's output covers before and after it. A source
    /// pixel change in a filtered layer is also reported for the tiles within the stack's halo (and for every output tile when the
    /// stack has a global filter), so <see cref="TryGetChangedTiles"/> never claims a filtered change is more local than it is.</para></summary>
    public sealed partial class PaintDocument
    {
        /// <summary>Largest total halo (sum of the radii of a stack's active filters for one channel or the mask).</summary>
        public const int MaxFilterStackHalo = 512;
        public const int MaxFiltersPerStack = 32;
        public const long DefaultFilterWorkingBudgetBytes = 256L * 1024 * 1024;
        public const long DefaultFilterCacheBudgetBytes = 256L * 1024 * 1024;

        FilterEngine filterEngine;
        internal FilterEngine FilterEvaluator { get { return filterEngine ?? (filterEngine = new FilterEngine(this)); } }
        long filterWorkingBudget = DefaultFilterWorkingBudgetBytes, filterCacheBudget = DefaultFilterCacheBudgetBytes, filterRevisionCounter;
        int filterBlockPixels = 256;
        readonly Dictionary<(Guid, int), Dictionary<TileCoord, long>> filterJournal = new Dictionary<(Guid, int), Dictionary<TileCoord, long>>();

        /// <summary>Working memory one filter evaluation (one block with its halo) may use. Adding or editing a filter whose block
        /// would need more is refused before anything is allocated. Cannot be set below what the current stacks need.</summary>
        public long FilterWorkingBudgetBytes
        {
            get { return filterWorkingBudget; }
            set
            {
                EnsureNoStroke(); if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                long need = CurrentFilterWorkingBytes(filterBlockPixels);
                if (value < need) throw new ArgumentOutOfRangeException(nameof(value), "The current filters need " + (need >> 20) + " MiB per block.");
                filterWorkingBudget = value;
            }
        }
        /// <summary>Budget of cached filtered tiles (derived display state, least recently used first out). 0 caches nothing.</summary>
        public long FilterCacheBudgetBytes
        {
            get { return filterCacheBudget; }
            set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); filterCacheBudget = value; if (value == 0 && filterEngine != null) filterEngine.Clear(); }
        }
        /// <summary>Bytes of filtered tiles cached now.</summary>
        public long FilterCacheBytes { get { return filterEngine == null ? 0 : filterEngine.CachedBytes; } }
        /// <summary>Blocks of filtered pixels evaluated so far (diagnostics, tests).</summary>
        public long FilterEvaluatedBlocks { get { return filterEngine == null ? 0 : filterEngine.EvaluatedBlocks; } }
        /// <summary>Side of the aligned square of tiles evaluated together (rounded down to whole tiles, at least one tile). The
        /// result does not depend on it; it trades halo overhead against latency and working memory.</summary>
        public int FilterBlockPixels
        {
            get { return filterBlockPixels; }
            set
            {
                if (value < 1 || value > 4096) throw new ArgumentOutOfRangeException(nameof(value));
                if (CurrentFilterWorkingBytes(value) > filterWorkingBudget) throw new InvalidOperationException("Blocks of " + value + " pixels would exceed the filter working budget for the current filters.");
                filterBlockPixels = value;
            }
        }
        long CurrentFilterWorkingBytes(int blockPixels)
        {
            long need = 0;
            foreach (var layer in layers)
            {
                foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) { var chain = layer.ActiveChain(c); if (chain.Length > 0) need = Math.Max(need, BlockWorkingBytes(chain, blockPixels)); }
                if (layer.Mask != null) { var chain = layer.Mask.ActiveChain(); if (chain.Length > 0) need = Math.Max(need, BlockWorkingBytes(chain, blockPixels)); }
            }
            return need;
        }
        long BlockWorkingBytes(FilterEffect[] chain, int blockPixels)
        {
            int side = Math.Max(1, blockPixels / TileSize) * TileSize;
            return FilterEngine.WorkingBytes(chain, chain.Length, Math.Min(side, Width), Math.Min(side, Height), Width, Height);
        }

        // ───────────── editing ─────────────

        /// <summary>Why a filter cannot be added to the layer's stack (for the channel on a content stack), or null when it can.</summary>
        public string FilterRefusal(Guid layerId, FilterTarget target, FilterSettings settings, PaintChannel channel = PaintChannel.Color)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var layer = GetLayer(layerId);
            if (target == FilterTarget.Mask) return layer.Mask == null ? "The layer has no mask." : settings.RefusalForMask();
            return KindRefusal(layer) ?? settings.RefusalFor(channel);
        }
        static string KindRefusal(PaintLayer layer)
        {
            switch (layer.Kind)
            {
                case LayerKind.Adjustment: return "Adjustment layers have no pixels to filter (they change what is below). Filter the layers below, or the adjustment's mask.";
                case LayerKind.Group: return "Filtering a group's composite is not supported yet; filter the layers inside it, or the group's mask.";
                default: return null;
            }
        }

        /// <summary>Adds a filter to a layer's content stack or mask stack as one undo step. Content filters apply to channels
        /// (null = every channel the filter accepts); a channel whose value type the filter does not accept is refused here, as is
        /// a stack whose halo or working memory would exceed <see cref="MaxFilterStackHalo"/> /
        /// <see cref="FilterWorkingBudgetBytes"/>. index −1 puts it on top (applied last).</summary>
        public FilterEffect AddFilter(Guid layerId, FilterTarget target, FilterSettings settings, IEnumerable<PaintChannel> channels = null, int index = -1, Guid? id = null, bool enabled = true, double strength = 1)
        {
            EnsureNoStroke(); if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (!Enum.IsDefined(typeof(FilterTarget), target)) throw new ArgumentOutOfRangeException(nameof(target));
            var layer = GetLayer(layerId);
            List<PaintChannel> targets = null;
            if (target == FilterTarget.Content)
            {
                string refusal = KindRefusal(layer); if (refusal != null) throw new InvalidOperationException(refusal);
                targets = new List<PaintChannel>();
                if (channels == null) { foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) if (settings.RefusalFor(c) == null) targets.Add(c); }
                else foreach (var c in channels)
                    {
                        string why = settings.RefusalFor(c); if (why != null) throw new InvalidOperationException(why);
                        targets.Add(c);
                    }
                if (targets.Count == 0) throw new ArgumentException("Choose at least one channel for the filter.", nameof(channels));
            }
            else
            {
                if (layer.Mask == null) throw new InvalidOperationException("The layer has no mask. Add a mask before adding mask filters.");
                if (channels != null) foreach (var _ in channels) throw new ArgumentException("Mask filters apply to the mask, which all channels share; they have no channels.", nameof(channels));
                string why = settings.RefusalForMask(); if (why != null) throw new InvalidOperationException(why);
            }
            Guid filterId = id ?? Guid.NewGuid();
            if (filterId == Guid.Empty) throw new ArgumentException("Filter ID must not be empty.", nameof(id));
            if (FindFilterInternal(filterId, out _, out _) != null) throw new ArgumentException("Duplicate filter ID.", nameof(id));
            var effect = new FilterEffect(filterId, settings, enabled, strength, targets);
            var stack = new List<FilterEffect>(StackOf(layer, target));
            if (index < -1 || index > stack.Count) throw new ArgumentOutOfRangeException(nameof(index));
            stack.Insert(index < 0 ? stack.Count : index, effect);
            ExecuteStack(layer, target, stack.ToArray(), null);
            return effect;
        }
        public void RemoveFilter(Guid layerId, Guid filterId)
        {
            EnsureNoStroke(); var layer = GetLayer(layerId); var effect = RequireFilter(layer, filterId, out var target);
            var stack = new List<FilterEffect>(StackOf(layer, target)); stack.Remove(effect);
            ExecuteStack(layer, target, stack.ToArray(), null);
        }
        /// <summary>Moves a filter to a position in its stack (0 = applied first).</summary>
        public void MoveFilter(Guid layerId, Guid filterId, int index)
        {
            EnsureNoStroke(); var layer = GetLayer(layerId); var effect = RequireFilter(layer, filterId, out var target);
            var stack = new List<FilterEffect>(StackOf(layer, target));
            if (index < 0 || index >= stack.Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (stack.IndexOf(effect) == index) return;
            stack.Remove(effect); stack.Insert(index, effect);
            ExecuteStack(layer, target, stack.ToArray(), null);
        }
        public void SetFilterEnabled(Guid layerId, Guid filterId, bool enabled)
        {
            EnsureNoStroke(); var layer = GetLayer(layerId); var effect = RequireFilter(layer, filterId, out var target);
            if (effect.Enabled == enabled) return;
            Replace(layer, target, effect, effect.With(enabled: enabled), null);
        }
        /// <summary>How much of the filter's result is used (0..1). Slider drags coalesce into one undo step.</summary>
        public void SetFilterStrength(Guid layerId, Guid filterId, double strength, bool coalesce = false)
        {
            EnsureNoStroke(); MathUtil.RequireFinite(strength, nameof(strength));
            if (strength < 0 || strength > 1) throw new ArgumentOutOfRangeException(nameof(strength));
            var layer = GetLayer(layerId); var effect = RequireFilter(layer, filterId, out var target);
            if (effect.Strength == strength) return;
            Replace(layer, target, effect, effect.With(strength: strength), coalesce ? (object)("filterStrength", filterId) : null);
        }
        /// <summary>Replaces a filter's parameters (the type may change if every channel it applies to accepts the new one).
        /// Slider drags coalesce into one undo step.</summary>
        public void SetFilterSettings(Guid layerId, Guid filterId, FilterSettings settings, bool coalesce = false)
        {
            EnsureNoStroke(); if (settings == null) throw new ArgumentNullException(nameof(settings));
            var layer = GetLayer(layerId); var effect = RequireFilter(layer, filterId, out var target);
            if (target == FilterTarget.Mask) { string why = settings.RefusalForMask(); if (why != null) throw new InvalidOperationException(why); }
            else foreach (var c in effect.Channels) { string why = settings.RefusalFor(c); if (why != null) throw new InvalidOperationException(why); }
            if (effect.Settings.Equals(settings)) return;
            Replace(layer, target, effect, effect.With(settings: settings), coalesce ? (object)("filterSettings", filterId) : null);
        }
        /// <summary>Changes the channels a content filter applies to (each must accept the filter).</summary>
        public void SetFilterChannels(Guid layerId, Guid filterId, IEnumerable<PaintChannel> channels)
        {
            EnsureNoStroke(); if (channels == null) throw new ArgumentNullException(nameof(channels));
            var layer = GetLayer(layerId); var effect = RequireFilter(layer, filterId, out var target);
            if (target == FilterTarget.Mask) throw new InvalidOperationException("Mask filters have no channels.");
            var list = new List<PaintChannel>();
            foreach (var c in channels) { string why = effect.Settings.RefusalFor(c); if (why != null) throw new InvalidOperationException(why); if (!list.Contains(c)) list.Add(c); }
            if (list.Count == 0) throw new ArgumentException("Choose at least one channel for the filter.", nameof(channels));
            list.Sort(); bool same = list.Count == effect.Channels.Count;
            for (int i = 0; same && i < list.Count; i++) same = list[i] == effect.Channels[i];
            if (same) return;
            Replace(layer, target, effect, effect.With(channels: list), null);
        }
        /// <summary>The filter with this id on the layer (content or mask stack), or null.</summary>
        public FilterEffect FindFilter(Guid layerId, Guid filterId, out FilterTarget target)
        {
            var layer = GetLayer(layerId); target = FilterTarget.Content;
            foreach (var e in layer.FilterList) if (e.Id == filterId) return e;
            if (layer.Mask != null) foreach (var e in layer.Mask.FilterList) if (e.Id == filterId) { target = FilterTarget.Mask; return e; }
            return null;
        }
        FilterEffect RequireFilter(PaintLayer layer, Guid filterId, out FilterTarget target)
        {
            var effect = FindFilter(layer.Id, filterId, out target);
            if (effect == null) throw new KeyNotFoundException("Filter not found on layer '" + layer.Name + "': " + filterId);
            return effect;
        }
        FilterEffect FindFilterInternal(Guid filterId, out PaintLayer owner, out FilterTarget target)
        {
            foreach (var layer in layers) { var e = FindFilter(layer.Id, filterId, out target); if (e != null) { owner = layer; return e; } }
            owner = null; target = FilterTarget.Content; return null;
        }
        static List<FilterEffect> StackOf(PaintLayer layer, FilterTarget target) { return target == FilterTarget.Content ? layer.FilterList : layer.Mask.FilterList; }
        void Replace(PaintLayer layer, FilterTarget target, FilterEffect old, FilterEffect next, object coalesceKey)
        {
            var stack = StackOf(layer, target).ToArray(); stack[Array.IndexOf(stack, old)] = next;
            ExecuteStack(layer, target, stack, coalesceKey);
        }
        /// <summary>Validates the new stack (count, halo and working memory of every chain) and swaps it in as one undo step that
        /// marks everything the layer's output covers before and after.</summary>
        void ExecuteStack(PaintLayer layer, FilterTarget target, FilterEffect[] after, object coalesceKey)
        {
            if (after.Length > MaxFiltersPerStack) throw new InvalidOperationException("A stack holds at most " + MaxFiltersPerStack + " filters.");
            var probe = new List<FilterEffect[]>();
            if (target == FilterTarget.Mask) { var chain = new List<FilterEffect>(); foreach (var e in after) if (e.IsActive) chain.Add(e); probe.Add(chain.ToArray()); }
            else foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel)))
                {
                    var chain = new List<FilterEffect>(); foreach (var e in after) if (e.IsActive && e.AppliesTo(c)) chain.Add(e); probe.Add(chain.ToArray());
                }
            foreach (var chain in probe)
            {
                int halo = FilterEngine.Halo(chain, chain.Length);
                if (halo > MaxFilterStackHalo) throw new InvalidOperationException("The filters would reach " + halo + " pixels in total, more than " + MaxFilterStackHalo + ". Use smaller radii.");
                long need = BlockWorkingBytes(chain, filterBlockPixels);
                if (need > filterWorkingBudget) throw new InvalidOperationException("These filters need about " + (need >> 20) + " MiB of working memory per block, more than the filter budget (" + (filterWorkingBudget >> 20) + " MiB). Nothing was changed; use smaller radii or raise FilterWorkingBudgetBytes.");
            }
            var mask = layer.Mask; var before = StackOf(layer, target).ToArray();
            Execute(new DelegateCommand(
                () => { MarkLayerChanged(layer, null); SetStack(layer, target, mask, after); MarkLayerChanged(layer, null); MarkClippedLayersChanged(); },
                () => { MarkLayerChanged(layer, null); SetStack(layer, target, mask, before); MarkLayerChanged(layer, null); MarkClippedLayersChanged(); },
                64 + 96L * (before.Length + after.Length)), coalesceKey);
        }
        void SetStack(PaintLayer layer, FilterTarget target, RasterMask mask, FilterEffect[] stack)
        {
            var list = target == FilterTarget.Content ? layer.FilterList : mask.FilterList;
            list.Clear(); list.AddRange(stack);
            long revision = ++filterRevisionCounter;
            if (target == FilterTarget.Content)
            {
                layer.FilterRevision = revision;
                foreach (PaintChannel c in Enum.GetValues(typeof(PaintChannel))) { FilterEvaluator.Forget(layer.Id, (int)c); if (stack.Length == 0) filterJournal.Remove((layer.Id, (int)c)); }
            }
            else { mask.FilterRevision = revision; FilterEvaluator.Forget(layer.Id, FilterEngine.MaskKey); if (stack.Length == 0) filterJournal.Remove((layer.Id, FilterEngine.MaskKey)); }
        }

        /// <summary>Applies the layer's filters to its pixels and removes them, as one undo step: every channel's content stack
        /// into that channel's raster tiles and the mask stack into the mask. Fill layers (no pixels) and layers drawn by a path
        /// refuse a content bake. The undo data is checked against <see cref="ActiveStrokeBudgetBytes"/> before anything changes.
        /// Returns false when the layer has no filters.</summary>
        public bool BakeFilters(Guid layerId)
        {
            EnsureNoStroke(); var layer = GetLayer(layerId); var mask = layer.Mask;
            bool content = layer.FilterList.Count > 0, masked = mask != null && mask.FilterList.Count > 0;
            if (!content && !masked) return false;
            if (content && layer.Kind != LayerKind.Raster) throw new InvalidOperationException("A " + layer.Kind.ToString().ToLowerInvariant() + " layer has no pixels to bake its filters into. Remove the filters, or put the content on a paint layer.");
            if (content) RefusePathLayer(layer);
            var engine = FilterEvaluator; int tileBytes = TileSize * TileSize * 4; long estimate = 0;
            var plans = new List<(SparseTileSurface surface, List<(TileCoord coord, byte[] bytes)> tiles)>();
            if (content)
                foreach (var channel in new List<PaintChannel>(layer.Channels.Keys))
                {
                    var s = FilterEngine.ContentSource(layer, channel); if (s == null) continue;
                    var tiles = new List<(TileCoord, byte[])>();
                    foreach (var coord in layer.EnumerateContentTiles(channel))
                    {
                        var bytes = new byte[tileBytes]; engine.CopyTile(s, coord, bytes); tiles.Add((coord, bytes));
                        estimate += 64 + s.Surface.TileBytesAt(coord) + tileBytes;
                        if (estimate > ActiveStrokeBudgetBytes) throw BakeBudget(estimate);
                    }
                    plans.Add((s.Surface, tiles));
                }
            if (masked)
            {
                var s = FilterEngine.MaskSource(mask);
                if (s != null)
                {
                    var tiles = new List<(TileCoord, byte[])>();
                    foreach (var coord in EnumerateCanvasTiles())
                    {
                        if (!engine.MayCover(s, coord.X, coord.Y, coord.X + 1, coord.Y + 1)) continue;
                        var bytes = new byte[tileBytes]; engine.CopyTile(s, coord, bytes); tiles.Add((coord, bytes));
                        estimate += 64 + mask.Surface.TileBytesAt(coord) + tileBytes;
                        if (estimate > ActiveStrokeBudgetBytes) throw BakeBudget(estimate);
                    }
                    plans.Add((mask.Surface, tiles));
                }
            }
            // 全部を評価し終えてから書く（書いた画素が、まだ評価していないタイルの halo の入力を変えないように）
            var commands = new List<IHistoryCommand>();
            try
            {
                foreach (var plan in plans)
                {
                    var changes = new List<TileChange>();
                    try
                    {
                        foreach (var (coord, bytes) in plan.tiles)
                        {
                            var before = plan.surface.Capture(coord); var after = TileStorage.FromBytes(bytes);
                            if (TileStorage.Same(before, after)) continue;
                            plan.surface.EnsureGrowth((after == null ? 0 : after.ByteSize) - plan.surface.TileBytesAt(coord));
                            plan.surface.Restore(coord, after); changes.Add(new TileChange(coord, before, after));
                        }
                    }
                    catch { for (int i = changes.Count - 1; i >= 0; i--) plan.surface.Restore(changes[i].Coord, changes[i].Before); throw; }
                    if (changes.Count > 0) commands.Add(new TileStrokeCommand(plan.surface, changes, Guid.NewGuid()));
                }
            }
            catch { for (int i = commands.Count - 1; i >= 0; i--) commands[i].Revert(); throw; }
            var contentBefore = layer.FilterList.ToArray(); var maskBefore = masked ? mask.FilterList.ToArray() : null; var none = new FilterEffect[0];
            var clear = new DelegateCommand(
                () => { MarkLayerChanged(layer, null); SetStack(layer, FilterTarget.Content, mask, none); if (masked) SetStack(layer, FilterTarget.Mask, mask, none); MarkLayerChanged(layer, null); MarkClippedLayersChanged(); },
                () => { MarkLayerChanged(layer, null); SetStack(layer, FilterTarget.Content, mask, contentBefore); if (masked) SetStack(layer, FilterTarget.Mask, mask, maskBefore); MarkLayerChanged(layer, null); MarkClippedLayersChanged(); },
                64 + 96L * (contentBefore.Length + (maskBefore == null ? 0 : maskBefore.Length)));
            clear.Apply(); commands.Add(clear);
            Revision++; Push(new CompositeCommand(commands));
            return true;
        }
        Exception BakeBudget(long estimate)
        {
            return new InvalidOperationException("Baking these filters would keep about " + (estimate >> 20) + " MiB of undo data, more than the one-operation budget (" + (ActiveStrokeBudgetBytes >> 20)
                + " MiB). Nothing was changed. Raise the budget (Project Settings > YoluPainter > One stroke, or ActiveStrokeBudgetBytes).");
        }

        // ───────────── change tracking ─────────────

        /// <summary>A raster tile of the layer changed: the tile itself, and (for a filtered layer) the tiles within the stack's
        /// halo when the journal is read.</summary>
        internal void MarkSourceTileChanged(PaintLayer layer, PaintChannel channel, TileCoord coord)
        {
            MarkTileChanged(channel, coord);
            if (layer.FilterList.Count > 0 && Influences(layer.ActiveChain(channel))) Journal((layer.Id, (int)channel), coord);
        }
        /// <summary>A mask tile changed (called after its covered channels were marked).</summary>
        internal void RecordMaskHalo(PaintLayer layer, TileCoord coord)
        {
            if (layer.Mask != null && layer.Mask.FilterList.Count > 0 && Influences(layer.Mask.ActiveChain())) Journal((layer.Id, FilterEngine.MaskKey), coord);
        }
        static bool Influences(FilterEffect[] chain) { return chain.Length > 0 && (FilterEngine.Halo(chain, chain.Length) > 0 || FilterEngine.IsGlobal(chain, chain.Length)); }
        void Journal((Guid, int) key, TileCoord coord)
        {
            if (!filterJournal.TryGetValue(key, out var serials)) filterJournal.Add(key, serials = new Dictionary<TileCoord, long>());
            serials[coord] = changeSerial;
        }
        /// <summary>Adds the tiles a filtered layer's source changes after since reach through the filters' halos (all output tiles
        /// for a global filter).</summary>
        internal void AddFilterInfluence(PaintChannel channel, long since, ICollection<TileCoord> changed)
        {
            if (filterJournal.Count == 0) return;
            int columns = (Width + TileSize - 1) / TileSize, rows = (Height + TileSize - 1) / TileSize;
            foreach (var layer in layers)
            {
                if (filterJournal.TryGetValue((layer.Id, (int)channel), out var serials))
                {
                    var chain = layer.ActiveChain(channel);
                    if (chain.Length > 0) Spread(serials, since, chain, columns, rows, changed, () => layer.EnumerateContentTiles(channel));
                }
                if (layer.Mask != null && filterJournal.TryGetValue((layer.Id, FilterEngine.MaskKey), out serials) && new List<PaintChannel>(layer.CoveredChannels).Contains(channel))
                {
                    var chain = layer.Mask.ActiveChain();
                    if (chain.Length > 0) Spread(serials, since, chain, columns, rows, changed, EnumerateCanvasTiles);
                }
            }
        }
        void Spread(Dictionary<TileCoord, long> serials, long since, FilterEffect[] chain, int columns, int rows, ICollection<TileCoord> changed, Func<IEnumerable<TileCoord>> everything)
        {
            bool global = FilterEngine.IsGlobal(chain, chain.Length);
            int m = (FilterEngine.Halo(chain, chain.Length) + TileSize - 1) / TileSize;
            foreach (var entry in serials)
            {
                if (entry.Value <= since) continue;
                if (global) { foreach (var c in everything()) changed.Add(c); return; }
                var t = entry.Key;
                for (int y = Math.Max(0, t.Y - m); y <= Math.Min(rows - 1, t.Y + m); y++)
                    for (int x = Math.Max(0, t.X - m); x <= Math.Min(columns - 1, t.X + m); x++) changed.Add(new TileCoord(x, y));
            }
        }
        /// <summary>The tiles a mask can affect: its own tiles, or every tile when it has active filters (they can spread it or
        /// produce values from nothing).</summary>
        internal IEnumerable<TileCoord> MaskMarkTiles(RasterMask mask)
        { return mask.HasActiveFilters ? EnumerateCanvasTiles() : mask.Surface.EnumerateTileCoordinates(); }
    }
}
