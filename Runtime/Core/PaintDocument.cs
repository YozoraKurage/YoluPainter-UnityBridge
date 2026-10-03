using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Raster mask of one layer, shared by all of its channels. The surface stores the amount to HIDE in each
    /// pixel's alpha (RGB stays zero), so an absent tile reveals everything and an untouched mask costs no memory.
    /// Painting hides, erasing reveals. Enabled, Inverted and Density are non-destructive parameters.</summary>
    public sealed partial class RasterMask
    {
        public SparseTileSurface Surface { get; private set; }
        public bool Enabled { get; internal set; }
        public bool Inverted { get; internal set; }
        public double Density { get; internal set; }
        internal RasterMask(SparseTileSurface surface) { Surface = surface; Enabled = true; Density = 1; }
        /// <summary>Multiplier applied to the layer's source alpha for a stored hide amount (0..255).</summary>
        public double Factor(byte hide)
        {
            if (!Enabled) return 1;
            double h = hide / 255.0;
            return Inverted ? 1 - Density * (1 - h) : 1 - Density * h;
        }
        public double FactorAt(int x, int y) { return Factor(OutputHideAt(x, y)); }
        /// <summary>True when the mask cannot change any pixel: disabled, zero density, or nothing hidden, not inverted and no active
        /// mask filter (an inverting or noise filter can hide pixels of an empty mask).</summary>
        public bool IsNeutral { get { return !Enabled || Density == 0 || (!Inverted && Surface.TileCount == 0 && !HasActiveFilters); } }
    }

    /// <summary>Raster layers own pixels. Fill layers own one value per channel and generate their tiles on demand
    /// (the value is the source; nothing is allocated per pixel).
    /// Adjustment layers own no pixels either: they change the composite of the layers below them.
    /// Groups own no pixels: their children (the layers whose ParentId is the group) composite through them.</summary>
    public enum LayerKind { Raster = 0, Fill = 1, Adjustment = 2, Group = 3 }

    public sealed partial class PaintLayer
    {
        private readonly PaintDocument document;
        private readonly Dictionary<PaintChannel, Rgba32> fillValues = new Dictionary<PaintChannel, Rgba32>();
        private readonly Dictionary<PaintChannel, SparseTileSurface> channels = new Dictionary<PaintChannel, SparseTileSurface>();
        private readonly HashSet<PaintChannel> enabled = new HashSet<PaintChannel>();
        public Guid Id { get; private set; }
        public string Name { get; internal set; }
        public bool Visible { get; internal set; }
        public double Opacity { get; internal set; }
        public LayerBlendMode BlendMode { get; internal set; }
        public LayerKind Kind { get; private set; }
        /// <summary>Clipped to the layer below: drawn only inside the clipping base (the nearest unclipped layer below) and
        /// composited together with it, like Photoshop's default "blend clipped layers as group". The bottom layer cannot
        /// be clipped (the flag is kept but has no effect there).</summary>
        public bool Clipping { get; internal set; }
        /// <summary>The group this layer is in, or Guid.Empty at the top level. A group's descendants always sit directly
        /// below it in <see cref="PaintDocument.Layers"/> (the same order as PSD folders).</summary>
        public Guid ParentId { get; internal set; }
        public bool IsGroup { get { return Kind == LayerKind.Group; } }
        /// <summary>Raster layers' pixel surfaces. Always empty for fill layers.</summary>
        public IReadOnlyDictionary<PaintChannel, SparseTileSurface> Channels { get; private set; }
        /// <summary>Fill layers' value per channel. Always empty for other kinds.</summary>
        public IReadOnlyDictionary<PaintChannel, Rgba32> FillValues { get; private set; }
        /// <summary>Adjustment layers' parameters; null for other kinds.</summary>
        public AdjustmentSettings Adjustment { get; internal set; }
        /// <summary>The layer's raster mask, or null when it has none.</summary>
        public RasterMask Mask { get; internal set; }
        /// <summary>The editable path (on the model or on the canvas) the layer's pixels are drawn from (one channel), or null for ordinary pixels.</summary>
        public Paths.EditablePath Path { get; internal set; }
        public IReadOnlyList<PaintChannel> EnabledChannels
        {
            get { var values = new List<PaintChannel>(enabled); values.Sort(); return values.AsReadOnly(); }
        }
        public long AllocatedBytes
        {
            get { long bytes = Mask == null ? 0 : Mask.Surface.AllocatedBytes; foreach (var s in channels.Values) bytes += s.AllocatedBytes; return bytes; }
        }
        internal PaintLayer(PaintDocument owner, string name, Guid id, LayerKind kind = LayerKind.Raster)
        {
            document = owner; Name = name; Id = id; Visible = true; Opacity = 1; Kind = kind;
            Channels = new ReadOnlyDictionary<PaintChannel, SparseTileSurface>(channels);
            FillValues = new ReadOnlyDictionary<PaintChannel, Rgba32>(fillValues);
        }
        /// <summary>Gets (or creates and enables) a raster channel. For side-effect-free reads use TryGetChannel.
        /// Fill layers have no pixel surfaces; set their values with PaintDocument.SetFillValue.</summary>
        public SparseTileSurface GetChannel(PaintChannel channel)
        {
            ValidateChannel(channel);
            if (Kind == LayerKind.Fill) throw new InvalidOperationException("Fill layers have no pixel surface. Change the fill value, or paint on the layer's mask.");
            if (Kind == LayerKind.Adjustment) throw new InvalidOperationException("Adjustment layers have no pixel surface. Change the adjustment, or paint on the layer's mask.");
            if (Kind == LayerKind.Group) throw new InvalidOperationException("Groups have no pixel surface. Paint on a layer inside the group, or on the group's mask.");
            SparseTileSurface surface;
            if (!channels.TryGetValue(channel, out surface))
            {
                document.EnsureNoStroke();
                surface = new SparseTileSurface(document.Width, document.Height, document.TileSize);
                surface.BeforeExternalMutation = document.BeforeExternalMutation;
                surface.AfterExternalMutation = document.AfterExternalMutation;
                surface.BeforeSourceGrowth = document.EnsureSourceGrowth;
                surface.TileChanged = coord => document.MarkSourceTileChanged(this, channel, coord);
                channels.Add(channel, surface); enabled.Add(channel);
            }
            return surface;
        }
        public bool TryGetChannel(PaintChannel channel, out SparseTileSurface surface) { return channels.TryGetValue(channel, out surface); }
        /// <summary>Removes a disabled channel's surface again when it holds no tiles (the undo of enabling it), so the layer is
        /// exactly as before and saves no empty surface.</summary>
        internal void DropEmptyChannel(PaintChannel channel)
        { if (!enabled.Contains(channel) && channels.TryGetValue(channel, out var surface) && surface.TileCount == 0) channels.Remove(channel); }
        public bool IsChannelEnabled(PaintChannel channel) { return enabled.Contains(channel); }
        internal void Enable(PaintChannel channel, bool value)
        { if (value) { if (Kind == LayerKind.Raster) GetChannel(channel); enabled.Add(channel); } else enabled.Remove(channel); }
        internal void SetFillValueInternal(PaintChannel channel, Rgba32? value)
        { if (value.HasValue) fillValues[channel] = value.Value; else fillValues.Remove(channel); }

        /// <summary>True when the layer can contribute to the channel: a raster surface, a fill value, or an adjustment that
        /// applies to the channel (adjustments are only active in enabled channels).</summary>
        public bool HasContent(PaintChannel channel)
        {
            switch (Kind)
            {
                case LayerKind.Fill: return fillValues.ContainsKey(channel);
                case LayerKind.Adjustment: return Adjustment != null && Adjustment.AppliesTo(channel) && enabled.Contains(channel);
                case LayerKind.Group: return false; // 中身は子が持つ。合成は CpuCompositor.Plan が子から判断する
                default: return channels.ContainsKey(channel);
            }
        }
        /// <summary>Channels whose composite this layer can change (used to fan out mask edits).</summary>
        internal IEnumerable<PaintChannel> CoveredChannels
        {
            get
            {
                switch (Kind)
                {
                    case LayerKind.Fill: return new List<PaintChannel>(fillValues.Keys);
                    case LayerKind.Adjustment: return new List<PaintChannel>(enabled);
                    case LayerKind.Group: return (PaintChannel[])Enum.GetValues(typeof(PaintChannel));
                    default: return new List<PaintChannel>(channels.Keys);
                }
            }
        }
        /// <summary>The layer's own pixel (before mask, opacity and blending). Transparent where it has no content.</summary>
        public Rgba32 GetPixel(PaintChannel channel, int x, int y)
        {
            if (Kind == LayerKind.Adjustment || Kind == LayerKind.Group) return Rgba32.Transparent; // owns no pixels
            if (Kind == LayerKind.Fill)
            {
                if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) throw new ArgumentOutOfRangeException("pixel");
                return fillValues.TryGetValue(channel, out var value) ? value : Rgba32.Transparent;
            }
            return channels.TryGetValue(channel, out var surface) ? surface.GetPixel(x, y) : Rgba32.Transparent;
        }
        /// <summary>Copies the layer's own pixels for one tile into a caller buffer (TileSize²×4, padding zero).
        /// Returns false (and writes zeros) where the layer has nothing. Fill tiles are generated, never stored.</summary>
        public bool CopyTile(PaintChannel channel, TileCoord coord, byte[] destination)
        {
            if (Kind != LayerKind.Fill)
            {
                // Adjustment layers own no pixels (the compositors apply them to the result below instead).
                if (Kind == LayerKind.Raster && channels.TryGetValue(channel, out var surface)) return surface.CopyTile(coord, destination);
                if (destination == null) throw new ArgumentNullException(nameof(destination));
                Array.Clear(destination, 0, destination.Length); return false;
            }
            int tile = document.TileSize, length = checked(tile * tile * 4);
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (destination.Length != length) throw new ArgumentException("Incorrect tile byte length.", nameof(destination));
            if (coord.X < 0 || coord.Y < 0 || (long)coord.X * tile >= document.Width || (long)coord.Y * tile >= document.Height) throw new ArgumentOutOfRangeException(nameof(coord));
            Array.Clear(destination, 0, length);
            if (!fillValues.TryGetValue(channel, out var fill) || fill == Rgba32.Transparent) return false;
            int w = Math.Min(tile, document.Width - coord.X * tile), h = Math.Min(tile, document.Height - coord.Y * tile);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            { int i = (y * tile + x) * 4; destination[i] = fill.R; destination[i + 1] = fill.G; destination[i + 2] = fill.B; destination[i + 3] = fill.A; }
            return true;
        }
        /// <summary>Tiles where the layer may have pixels in the channel: a raster surface's occupied tiles (grown by the reach
        /// of its active blurs), or every canvas tile for a fill value.</summary>
        public IEnumerable<TileCoord> EnumerateContentTiles(PaintChannel channel)
        {
            if (Kind == LayerKind.Group) return new TileCoord[0];
            if (Kind == LayerKind.Raster)
                return channels.TryGetValue(channel, out var surface) ? OutputContentTiles(surface, channel) : new TileCoord[0];
            return HasContent(channel) ? document.EnumerateCanvasTiles() : new TileCoord[0];
        }
        internal static void ValidateChannel(PaintChannel channel)
        { if (!Enum.IsDefined(typeof(PaintChannel), channel)) throw new ArgumentOutOfRangeException(nameof(channel)); }
    }

    /// <summary>Single-writer CPU raster document. Layers are bottom-to-top. Histories store exact changed tile states,
    /// never brush replay or a full canvas copy. Structural edits and strokes cannot interleave.</summary>
    public sealed partial class PaintDocument
    {
        private readonly List<PaintLayer> layers = new List<PaintLayer>();
        private readonly List<IHistoryCommand> undo = new List<IHistoryCommand>();
        private readonly List<IHistoryCommand> redo = new List<IHistoryCommand>();
        private long historyBytes;
        private long undoBudgetBytes;
        private long sourceBudgetBytes = 256L * 1024 * 1024;
        private long activeStrokeBudgetBytes = 64L * 1024 * 1024;
        private int minimumUndoSteps;
        private BrushStroke activeStroke;
        private bool notifyingHistory;
        // Composite invalidation journal. Not part of undo history or persistence.
        private long changeSerial;
        private readonly Dictionary<PaintChannel, Dictionary<TileCoord, long>> tileSerials = new Dictionary<PaintChannel, Dictionary<TileCoord, long>>();
        public Guid Id { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int TileSize { get; private set; }
        public long Revision { get; private set; }
        public IReadOnlyList<PaintLayer> Layers { get; private set; }
        public int UndoCount { get { return undo.Count; } }
        public int RedoCount { get { return redo.Count; } }
        public bool CanUndo { get { return activeStroke == null && undo.Count > 0; } }
        public bool CanRedo { get { return activeStroke == null && redo.Count > 0; } }
        public bool HasActiveStroke { get { return activeStroke != null; } }
        public long HistoryBytes { get { return historyBytes; } }
        public long AllocatedBytes { get { long bytes = 0; foreach (var layer in layers) bytes += layer.AllocatedBytes; return bytes; } }
        /// <summary>Payload-byte budget (tile data plus nominal command headers), excluding CLR object/dictionary overhead.
        /// Active-stroke rollback tiles are not history; PeakWorkingBytes exposes that additional payload.</summary>
        public long UndoBudgetBytes
        {
            get { return undoBudgetBytes; }
            set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); EnsureNoStroke(); undoBudgetBytes = value; TrimHistory(); }
        }
        /// <summary>The newest undo steps kept even when they exceed UndoBudgetBytes (like GIMP's minimal undo levels), so a
        /// large edit — a fill or transform of a whole 4K layer — can still be undone. 0 makes the budget strict.</summary>
        public int MinimumUndoSteps
        {
            get { return minimumUndoSteps; }
            set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); EnsureNoStroke(); minimumUndoSteps = value; TrimHistory(); }
        }
        /// <summary>Configurable source tile payload limit; refusal happens before tile expansion. Managed overhead is additional.</summary>
        public long SourceBudgetBytes
        {
            get { return sourceBudgetBytes; }
            set { EnsureNoStroke(); if (value < AllocatedBytes) throw new ArgumentOutOfRangeException(nameof(value), "Budget is below current source payload."); sourceBudgetBytes = value; }
        }
        /// <summary>Maximum rollback tile payload plus nominal tracking headers for an in-flight stroke.</summary>
        public long ActiveStrokeBudgetBytes
        {
            get { return activeStrokeBudgetBytes; }
            set { EnsureNoStroke(); if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); activeStrokeBudgetBytes = value; }
        }
        /// <summary>Conservative current payload estimate; excludes managed overhead and is not a measured process-memory peak.</summary>
        public long PeakWorkingBytes { get { return AllocatedBytes + HistoryBytes + (activeStroke == null ? 0 : activeStroke.RollbackBytes); } }
        /// <summary>Raised before budget eviction, with the payload bytes to discard. Notification handlers cannot abort a committed edit.</summary>
        public event Action<long> HistoryTrimming;
        /// <summary>Monotonic counter of changes that can affect a composite. Read it after consuming a composite, then
        /// pass it to TryGetChangedTiles to learn what changed since. Independent of Revision and of undo history.</summary>
        public long ChangeSerial { get { return changeSerial; } }
        public PaintDocument(int width, int height, int tileSize = 128, long undoBudgetBytes = 67108864, Guid? id = null)
        {
            // Share the surface's dimension contract without allocating any pixels.
            var dimensions = new SparseTileSurface(width, height, tileSize);
            Width = dimensions.Width; Height = dimensions.Height; TileSize = dimensions.TileSize;
            if (undoBudgetBytes < 0) throw new ArgumentOutOfRangeException(nameof(undoBudgetBytes));
            this.undoBudgetBytes = undoBudgetBytes; Id = id ?? Guid.NewGuid();
            if (Id == Guid.Empty) throw new ArgumentException("Document ID must not be empty.", nameof(id));
            Layers = layers.AsReadOnly();
        }
        /// <param name="above">Places the new layer directly above this layer, in the same group (the top of the document when
        /// null). Above a group means above the group and its contents, as a sibling of the group.</param>
        public PaintLayer AddLayer(string name, Guid? id = null, Guid? above = null)
        {
            EnsureNoStroke(); Guid layerId = NewLayerId(id);
            var layer = new PaintLayer(this, name ?? "Layer", layerId);
            layer.GetChannel(PaintChannel.Color);
            Insert(layer, above, () => EnsureSourceGrowth(layer.AllocatedBytes));
            return layer;
        }
        Guid NewLayerId(Guid? id)
        {
            Guid layerId = id ?? Guid.NewGuid();
            if (layerId == Guid.Empty) throw new ArgumentException("Layer ID must not be empty.", nameof(id));
            foreach (var existing in layers) if (existing.Id == layerId) throw new ArgumentException("Duplicate layer ID.", nameof(id));
            return layerId;
        }
        /// <summary>Adds a new layer as one undo step: on top, or directly above another layer in that layer's group.</summary>
        void Insert(PaintLayer layer, Guid? above, Action beforeInsert = null)
        {
            int index = layers.Count; Guid parent = Guid.Empty;
            if (above.HasValue) { var reference = GetLayer(above.Value); index = layers.IndexOf(reference) + 1; parent = reference.ParentId; }
            Execute(LayerScoped(layer, null, () => { beforeInsert?.Invoke(); layer.ParentId = parent; layers.Insert(index, layer); }, () => layers.Remove(layer), 128));
        }
        /// <summary>Adds a fill layer on top. values sets the initial value per channel (each one enabled); the layer
        /// covers the whole canvas wherever its channel has a value. Use a mask to limit where it shows.</summary>
        public PaintLayer AddFillLayer(string name, IDictionary<PaintChannel, Rgba32> values = null, Guid? id = null, Guid? above = null)
        {
            EnsureNoStroke(); Guid layerId = NewLayerId(id);
            var layer = new PaintLayer(this, name ?? "Fill", layerId, LayerKind.Fill);
            if (values != null) foreach (var entry in values) { PaintLayer.ValidateChannel(entry.Key); layer.SetFillValueInternal(entry.Key, entry.Value); layer.Enable(entry.Key, true); }
            Insert(layer, above);
            return layer;
        }
        /// <summary>Sets (or with null removes) a fill layer's value for one channel. Setting a value enables the channel.</summary>
        public void SetFillValue(Guid id, PaintChannel channel, Rgba32? value, bool coalesce = false)
        {
            EnsureNoStroke(); PaintLayer.ValidateChannel(channel); var layer = GetLayer(id);
            if (layer.Kind != LayerKind.Fill) throw new InvalidOperationException("Only fill layers have fill values.");
            Rgba32? old = layer.FillValues.TryGetValue(channel, out var current) ? current : (Rgba32?)null;
            bool wasEnabled = layer.IsChannelEnabled(channel);
            if (Nullable.Equals(old, value) && (value == null || wasEnabled)) return;
            RefuseLockedPixels(layer, erase: false); // 塗りつぶしの値は層の中身
            if ((old?.A ?? 0) != (value?.A ?? 0)) RefuseLockedTransparency(layer);
            Execute(LayerScoped(layer, channel,
                () => { layer.SetFillValueInternal(channel, value); if (value.HasValue) layer.Enable(channel, true); },
                () => { layer.SetFillValueInternal(channel, old); layer.Enable(channel, wasEnabled); }, 64), coalesce ? (object)("fill", id, channel) : null);
        }
        /// <summary>Adds an adjustment layer on top that changes the composite below it in the given channels (all channels
        /// the adjustment applies to when null). Hue/saturation can only target Color and Emission.</summary>
        public PaintLayer AddAdjustmentLayer(string name, AdjustmentSettings settings, IEnumerable<PaintChannel> channels = null, Guid? id = null, Guid? above = null)
        {
            EnsureNoStroke(); if (settings == null) throw new ArgumentNullException(nameof(settings)); settings.Validate();
            Guid layerId = NewLayerId(id);
            var layer = new PaintLayer(this, name ?? settings.Type.ToString(), layerId, LayerKind.Adjustment) { Adjustment = settings };
            foreach (PaintChannel channel in channels ?? (PaintChannel[])Enum.GetValues(typeof(PaintChannel)))
            {
                PaintLayer.ValidateChannel(channel);
                if (!settings.AppliesTo(channel)) { if (channels == null) continue; throw new InvalidOperationException(settings.Type + " cannot be applied to the " + channel + " channel."); }
                layer.Enable(channel, true);
            }
            Insert(layer, above);
            return layer;
        }
        /// <summary>Replaces an adjustment layer's parameters (the type may change if every enabled channel still applies).</summary>
        public void SetAdjustment(Guid id, AdjustmentSettings settings, bool coalesce = false)
        {
            EnsureNoStroke(); if (settings == null) throw new ArgumentNullException(nameof(settings)); settings.Validate();
            var layer = GetLayer(id);
            if (layer.Kind != LayerKind.Adjustment) throw new InvalidOperationException("Only adjustment layers have adjustment settings.");
            foreach (var channel in layer.EnabledChannels)
                if (!settings.AppliesTo(channel)) throw new InvalidOperationException(settings.Type + " cannot be applied to the enabled " + channel + " channel. Disable it first.");
            var old = layer.Adjustment; if (old.Equals(settings)) return;
            RefuseLockedAttributes(layer);
            Execute(LayerScoped(layer, null, () => layer.Adjustment = settings, () => layer.Adjustment = old, 128), coalesce ? (object)("adjustment", id) : null);
        }
        /// <summary>Every tile coordinate of the canvas, Y then X.</summary>
        public IEnumerable<TileCoord> EnumerateCanvasTiles()
        {
            int columns = (Width + TileSize - 1) / TileSize, rows = (Height + TileSize - 1) / TileSize;
            for (int y = 0; y < rows; y++) for (int x = 0; x < columns; x++) yield return new TileCoord(x, y);
        }
        public PaintLayer GetLayer(Guid id)
        {
            foreach (var layer in layers) if (layer.Id == id) return layer;
            throw new KeyNotFoundException("Layer not found: " + id);
        }
        /// <summary>Removes a layer. Removing a group removes everything in it (one undo step restores all of it).</summary>
        public void RemoveLayer(Guid id)
        {
            EnsureNoStroke(); PaintLayer layer = GetLayer(id); int top = layers.IndexOf(layer), start = SubtreeStart(top);
            var block = layers.GetRange(start, top - start + 1); long bytes = 0; foreach (var l in block) bytes += l.AllocatedBytes;
            Execute(SubtreeScoped(block, () => layers.RemoveRange(start, block.Count), () => { EnsureSourceGrowth(bytes); layers.InsertRange(start, block); }, 128 + bytes));
        }

        // ---------------- groups ----------------

        /// <summary>Adds an empty group on top of the top level. Groups pass through by default (their children composite
        /// as if they were not grouped); any other blend mode makes the group isolated.</summary>
        public PaintLayer AddGroup(string name, Guid? id = null, Guid? above = null)
        {
            EnsureNoStroke(); Guid layerId = NewLayerId(id);
            var layer = new PaintLayer(this, name ?? "Group", layerId, LayerKind.Group) { BlendMode = LayerBlendMode.PassThrough };
            Insert(layer, above);
            return layer;
        }
        /// <summary>The direct children of a group (Guid.Empty for the top level), bottom to top.</summary>
        public IReadOnlyList<PaintLayer> ChildrenOf(Guid parentId)
        {
            if (parentId != Guid.Empty && !GetLayer(parentId).IsGroup) throw new ArgumentException("Not a group.", nameof(parentId));
            var result = new List<PaintLayer>(); foreach (var layer in layers) if (layer.ParentId == parentId) result.Add(layer);
            return result.AsReadOnly();
        }
        /// <summary>Nesting depth (0 at the top level).</summary>
        public int DepthOf(Guid id)
        {
            int depth = 0; var layer = GetLayer(id);
            while (layer.ParentId != Guid.Empty) { layer = GetLayer(layer.ParentId); depth++; }
            return depth;
        }
        /// <summary>Moves a layer (with its contents when it is a group) to a position among the siblings of its current
        /// group (0 = bottom). Without groups this is the index in <see cref="Layers"/>.</summary>
        public void MoveLayer(Guid id, int newIndex) { MoveLayerTo(id, GetLayer(id).ParentId, newIndex); }
        /// <summary>Moves a layer (with its contents when it is a group) into a group (Guid.Empty for the top level) at a
        /// position among that group's children (0 = bottom). A group cannot be moved into itself or its descendants.</summary>
        public void MoveLayerTo(Guid id, Guid parentId, int position)
        {
            EnsureNoStroke(); var layer = GetLayer(id);
            if (parentId != Guid.Empty)
            {
                var parent = GetLayer(parentId);
                if (!parent.IsGroup) throw new ArgumentException("Layers can only be moved into groups.", nameof(parentId));
                for (var p = parent; ; p = GetLayer(p.ParentId)) { if (p == layer) throw new InvalidOperationException("A group cannot be moved into itself."); if (p.ParentId == Guid.Empty) break; }
            }
            var siblings = new List<PaintLayer>(); foreach (var l in layers) if (l.ParentId == parentId && l != layer) siblings.Add(l);
            if (position < 0 || position > siblings.Count) throw new ArgumentOutOfRangeException(nameof(position));
            var before = SnapshotStructure(); var block = Block(layer);
            MoveSubtreeInternal(layer, parentId, position);
            var after = SnapshotStructure(); RestoreStructure(before);
            if (SameStructure(before, after)) return;
            Execute(StructureCommand(before, after, 64, block));
        }
        /// <summary>Puts sibling layers into a new group placed where the topmost of them was. They keep their order.</summary>
        public PaintLayer GroupLayers(IReadOnlyCollection<Guid> ids, string name = null, Guid? groupId = null)
        {
            EnsureNoStroke(); if (ids == null || ids.Count == 0) throw new ArgumentException("Choose at least one layer.", nameof(ids));
            var members = new List<PaintLayer>(); foreach (var id in ids) { var l = GetLayer(id); if (!members.Contains(l)) members.Add(l); }
            Guid parentId = members[0].ParentId;
            foreach (var m in members) if (m.ParentId != parentId) throw new InvalidOperationException("Only layers in the same group can be grouped together.");
            members.Sort((a, b) => layers.IndexOf(a).CompareTo(layers.IndexOf(b)));
            Guid gid = groupId ?? Guid.NewGuid();
            if (gid == Guid.Empty) throw new ArgumentException("Layer ID must not be empty.", nameof(groupId));
            foreach (var existing in layers) if (existing.Id == gid) throw new ArgumentException("Duplicate layer ID.", nameof(groupId));
            var group = new PaintLayer(this, name ?? "Group", gid, LayerKind.Group) { BlendMode = LayerBlendMode.PassThrough };
            var before = SnapshotStructure();
            var siblings = ChildrenOf(parentId); int topPosition = 0;
            for (int i = 0; i < siblings.Count; i++) if (siblings[i] == members[members.Count - 1]) topPosition = i;
            // グループを一番上の対象の上に置き、対象を下から順に中へ入れる
            int insertAt = layers.IndexOf(members[members.Count - 1]) + 1;
            var moved = new List<PaintLayer>(); foreach (var m in members) moved.AddRange(Block(m));
            group.ParentId = parentId; layers.Insert(insertAt, group);
            for (int i = 0; i < members.Count; i++) MoveSubtreeInternal(members[i], gid, i);
            var after = SnapshotStructure(); RestoreStructure(before);
            Execute(StructureCommand(before, after, 128, moved));
            return group;
        }
        /// <summary>Removes a group but keeps its contents, which take the group's place in its parent.</summary>
        public void Ungroup(Guid groupId)
        {
            EnsureNoStroke(); var group = GetLayer(groupId);
            if (!group.IsGroup) throw new InvalidOperationException("Not a group.");
            var before = SnapshotStructure(); var block = Block(group);
            int index = layers.IndexOf(group);
            foreach (var child in layers) if (child.ParentId == groupId) child.ParentId = group.ParentId;
            layers.RemoveAt(index); // 子は既にグループのすぐ下にあるので、グループの記録を抜くだけで位置が保たれる
            var after = SnapshotStructure(); RestoreStructure(before);
            Execute(StructureCommand(before, after, 128, block));
        }

        int SubtreeStart(int top)
        {
            int start = top;
            if (layers[top].IsGroup) while (start > 0 && IsDescendant(layers[start - 1], layers[top])) start--;
            return start;
        }
        bool IsDescendant(PaintLayer layer, PaintLayer group)
        {
            for (var id = layer.ParentId; id != Guid.Empty;)
            {
                if (id == group.Id) return true;
                PaintLayer parent = null; foreach (var l in layers) if (l.Id == id) { parent = l; break; }
                if (parent == null) return false;
                id = parent.ParentId;
            }
            return false;
        }
        /// <summary>Moves a layer's block into parent at position (no history; the caller records before/after).</summary>
        void MoveSubtreeInternal(PaintLayer layer, Guid parentId, int position)
        {
            int top = layers.IndexOf(layer), start = SubtreeStart(top);
            var block = layers.GetRange(start, top - start + 1); layers.RemoveRange(start, block.Count);
            var siblings = new List<PaintLayer>(); foreach (var l in layers) if (l.ParentId == parentId) siblings.Add(l);
            int insertAt;
            if (position < siblings.Count) insertAt = SubtreeStart(layers.IndexOf(siblings[position]));
            else if (parentId == Guid.Empty) insertAt = layers.Count;
            else insertAt = layers.IndexOf(GetLayer(parentId)); // グループの記録のすぐ下 = 子の一番上
            layer.ParentId = parentId;
            layers.InsertRange(insertAt, block);
        }
        sealed class Structure { public PaintLayer[] Order; public Guid[] Parents; }
        Structure SnapshotStructure()
        {
            var s = new Structure { Order = layers.ToArray(), Parents = new Guid[layers.Count] };
            for (int i = 0; i < layers.Count; i++) s.Parents[i] = layers[i].ParentId;
            return s;
        }
        void RestoreStructure(Structure s)
        {
            layers.Clear(); layers.AddRange(s.Order);
            for (int i = 0; i < s.Order.Length; i++) s.Order[i].ParentId = s.Parents[i];
        }
        static bool SameStructure(Structure a, Structure b)
        {
            if (a.Order.Length != b.Order.Length) return false;
            for (int i = 0; i < a.Order.Length; i++) if (a.Order[i] != b.Order[i] || a.Parents[i] != b.Parents[i]) return false;
            return true;
        }
        /// <summary>A history command that switches between two layer orders/nestings. Every layer present in either is
        /// re-marked (moving into or out of a group changes how all of them composite).</summary>
        IHistoryCommand StructureCommand(Structure before, Structure after, long cost, IEnumerable<PaintLayer> moved)
        {
            // 動かしたレイヤー（まとまりの全員）だけが自分のタイルで合成を変える。並べ替えで合成が変わるのは動いたものと
            // 追い越されたものが重なる所だけなので、動いた側のタイルで足りる。クリッピングの下地が変わり得るレイヤーは
            // MarkClippedLayersChanged が拾う。
            var affected = new List<PaintLayer>(moved);
            return new DelegateCommand(() => { RestoreStructure(after); foreach (var l in affected) MarkLayerChanged(l, null); MarkClippedLayersChanged(); },
                () => { RestoreStructure(before); foreach (var l in affected) MarkLayerChanged(l, null); MarkClippedLayersChanged(); }, cost);
        }
        List<PaintLayer> Block(PaintLayer layer)
        { int top = layers.IndexOf(layer), start = SubtreeStart(top); return layers.GetRange(start, top - start + 1); }
        /// <summary>Checks the nesting: every parent exists and is a group, there are no cycles, and each group's descendants
        /// sit directly below it. DocumentBinary uses it for loaded archives.</summary>
        public void ValidateStructure()
        {
            var byId = new Dictionary<Guid, int>(); for (int i = 0; i < layers.Count; i++) byId[layers[i].Id] = i;
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i]; int hops = 0;
                for (var id = layer.ParentId; id != Guid.Empty; id = layers[byId[id]].ParentId)
                {
                    if (!byId.TryGetValue(id, out int parentIndex)) throw new InvalidOperationException("Layer '" + layer.Name + "' is in a group that does not exist.");
                    if (!layers[parentIndex].IsGroup) throw new InvalidOperationException("Layer '" + layer.Name + "' is inside a layer that is not a group.");
                    if (++hops > layers.Count) throw new InvalidOperationException("Groups are nested in a cycle.");
                }
                if (layer.ParentId != Guid.Empty)
                {
                    int parent = byId[layer.ParentId];
                    if (parent <= i) throw new InvalidOperationException("Layer '" + layer.Name + "' must be below its group.");
                    for (int j = i + 1; j < parent; j++)
                        if (!IsDescendant(layers[j], layers[parent])) throw new InvalidOperationException("The contents of group '" + layers[parent].Name + "' are not contiguous.");
                }
            }
        }
        /// <summary>For loaders: sets a layer's group without history. Call ValidateStructure afterwards.</summary>
        internal void SetParentForLoad(PaintLayer layer, Guid parentId) { layer.ParentId = parentId; }
        public void SetLayerName(Guid id, string name)
        {
            EnsureNoStroke(); if (name == null) throw new ArgumentNullException(nameof(name)); var layer = GetLayer(id);
            string old = layer.Name; if (old == name) return;
            Execute(new DelegateCommand(() => layer.Name = name, () => layer.Name = old, 64 + 2L * (old.Length + name.Length)));
        }
        public void SetLayerVisibility(Guid id, bool visible)
        {
            EnsureNoStroke(); var layer = GetLayer(id); bool old = layer.Visible; if (old == visible) return;
            Execute(LayerScoped(layer, null, () => layer.Visible = visible, () => layer.Visible = old, 64));
        }
        public void SetLayerOpacity(Guid id, double opacity, bool coalesce = false)
        {
            EnsureNoStroke(); MathUtil.RequireFinite(opacity, nameof(opacity));
            if (opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
            var layer = GetLayer(id); double old = layer.Opacity; if (old == opacity) return;
            RefuseLockedAttributes(layer);
            Execute(LayerScoped(layer, null, () => layer.Opacity = opacity, () => layer.Opacity = old, 64), coalesce ? (object)("opacity", id) : null);
        }
        public void SetLayerBlendMode(Guid id, LayerBlendMode mode)
        {
            EnsureNoStroke(); if (!Enum.IsDefined(typeof(LayerBlendMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            var layer = GetLayer(id);
            if (mode == LayerBlendMode.PassThrough && !layer.IsGroup) throw new ArgumentException("Pass through applies to groups only.", nameof(mode));
            var old = layer.BlendMode; if (old == mode) return;
            RefuseLockedAttributes(layer);
            Execute(LayerScoped(layer, null, () => layer.BlendMode = mode, () => layer.BlendMode = old, 64));
        }
        /// <summary>Clips the layer to the layer below (or releases it). Undoable.</summary>
        public void SetLayerClipping(Guid id, bool clipping)
        {
            EnsureNoStroke(); var layer = GetLayer(id); bool old = layer.Clipping; if (old == clipping) return;
            RefuseLockedAttributes(layer);
            Execute(LayerScoped(layer, null, () => layer.Clipping = clipping, () => layer.Clipping = old, 64));
        }
        /// <summary>True when the layer at index is effectively clipped: the flag is set and it has a sibling below it in the
        /// same group (the bottom layer of the document or of a group has nothing to clip to).</summary>
        public bool IsEffectivelyClipped(int index)
        {
            if (index <= 0 || index >= layers.Count || !layers[index].Clipping) return false;
            var parent = layers[index].ParentId;
            for (int j = index - 1; j >= 0; j--) if (layers[j].ParentId == parent) return true;
            return false;
        }
        public void SetChannelEnabled(Guid id, PaintChannel channel, bool enabled)
        {
            EnsureNoStroke(); PaintLayer.ValidateChannel(channel); var layer = GetLayer(id);
            bool old = layer.IsChannelEnabled(channel); if (old == enabled) return;
            RefuseLockedAttributes(layer);
            if (enabled && layer.Kind == LayerKind.Adjustment && !layer.Adjustment.AppliesTo(channel))
                throw new InvalidOperationException(layer.Adjustment.Type + " cannot be applied to the " + channel + " channel.");
            // 有効にして初めて面ができたときは、取り消しで面も消す（空の面が残ると保存のバイト列が変わる）
            bool hadSurface = layer.TryGetChannel(channel, out _);
            Execute(LayerScoped(layer, channel, () => layer.Enable(channel, enabled), () => { layer.Enable(channel, old); if (!hadSurface) layer.DropEmptyChannel(channel); }, 64));
        }
        /// <summary>Adds an empty raster mask (reveals everything) to a layer. Undoable.</summary>
        public RasterMask AddLayerMask(Guid id)
        {
            EnsureNoStroke(); var layer = GetLayer(id);
            if (layer.Mask != null) throw new InvalidOperationException("The layer already has a mask.");
            RefuseLockedAttributes(layer);
            var surface = new SparseTileSurface(Width, Height, TileSize);
            surface.BeforeExternalMutation = BeforeExternalMutation;
            surface.AfterExternalMutation = AfterExternalMutation;
            surface.BeforeSourceGrowth = EnsureSourceGrowth;
            surface.TileChanged = coord => MarkMaskTileChanged(layer, coord);
            var mask = new RasterMask(surface) { Owner = layer };
            Execute(LayerScoped(layer, null, () => layer.Mask = mask, () => layer.Mask = null, 64));
            return mask;
        }
        /// <summary>Removes a layer's mask. Undo restores the same mask, pixels and parameters.</summary>
        public void RemoveLayerMask(Guid id)
        {
            EnsureNoStroke(); var layer = GetLayer(id); var mask = layer.Mask;
            if (mask == null) throw new InvalidOperationException("The layer has no mask.");
            RefuseLockedAttributes(layer);
            Execute(LayerScoped(layer, null, () => layer.Mask = null, () => { EnsureSourceGrowth(mask.Surface.AllocatedBytes); layer.Mask = mask; }, 64 + mask.Surface.AllocatedBytes));
        }
        public void SetLayerMaskEnabled(Guid id, bool enabled)
        {
            EnsureNoStroke(); var mask = RequireMask(id, out var layer); bool old = mask.Enabled; if (old == enabled) return;
            RefuseLockedAttributes(layer);
            Execute(LayerScoped(layer, null, () => mask.Enabled = enabled, () => mask.Enabled = old, 64));
        }
        public void SetLayerMaskInverted(Guid id, bool inverted)
        {
            EnsureNoStroke(); var mask = RequireMask(id, out var layer); bool old = mask.Inverted; if (old == inverted) return;
            RefuseLockedAttributes(layer);
            Execute(LayerScoped(layer, null, () => mask.Inverted = inverted, () => mask.Inverted = old, 64));
        }
        public void SetLayerMaskDensity(Guid id, double density, bool coalesce = false)
        {
            EnsureNoStroke(); MathUtil.RequireFinite(density, nameof(density));
            if (density < 0 || density > 1) throw new ArgumentOutOfRangeException(nameof(density));
            var mask = RequireMask(id, out var layer); double old = mask.Density; if (old == density) return;
            RefuseLockedAttributes(layer);
            Execute(LayerScoped(layer, null, () => mask.Density = density, () => mask.Density = old, 64), coalesce ? (object)("maskDensity", id) : null);
        }
        /// <summary>Starts a stroke on a layer's mask: painting hides, Erase reveals. The brush colour is ignored
        /// (the mask stores only a hide amount); opacity, flow, hardness and pressure apply as usual.</summary>
        public BrushStroke BeginMaskStroke(Guid layerId, BrushSettings settings)
        {
            EnsureNoStroke(); RefuseInBatch("A stroke"); if (settings == null) throw new ArgumentNullException(nameof(settings)); settings.Validate();
            var mask = RequireMask(layerId, out var owner);
            RefuseLockedAttributes(owner); // マスクは画像のロックでは描ける（Photoshop と同じ）。すべてのロックでだけ断る
            var maskSettings = settings.ForChannel(null); maskSettings.Color = new Rgba32(0, 0, 0, 255);
            activeStroke = new BrushStroke(this, mask.Surface, maskSettings); return activeStroke;
        }
        private RasterMask RequireMask(Guid id, out PaintLayer layer)
        {
            layer = GetLayer(id);
            if (layer.Mask == null) throw new InvalidOperationException("The layer has no mask.");
            return layer.Mask;
        }
        public BrushStroke BeginStroke(Guid layerId, PaintChannel channel, BrushSettings settings)
        {
            EnsureNoStroke(); RefuseInBatch("A stroke"); if (settings == null) throw new ArgumentNullException(nameof(settings)); settings.Validate();
            var layer = GetLayer(layerId);
            if (layer.Kind == LayerKind.Fill) throw new InvalidOperationException("Fill layers are generated from their values and cannot be painted. Paint on the layer's mask, or add a paint layer.");
            RefuseLockedPixels(layer, settings.Erase);
            RefusePathLayer(layer);
            var surface = layer.GetChannel(channel);
            if (!layer.IsChannelEnabled(channel)) throw new InvalidOperationException("Enable the target channel before painting.");
            activeStroke = new BrushStroke(this, surface, settings.ForChannel(channel), KeepsAlpha(layer)); return activeStroke;
        }
        public byte[] Composite(PaintChannel channel) { return CpuCompositor.Composite(this, channel); }
        public Rgba32 CompositePixel(PaintChannel channel, int x, int y) { return CpuCompositor.CompositePixel(this, channel, x, y); }
        public bool Undo()
        {
            EnsureNoStroke(); RefuseInBatch("Undo"); if (undo.Count == 0) return false;
            EndCoalescing();
            var command = undo[undo.Count - 1]; command.Revert(); undo.RemoveAt(undo.Count - 1); redo.Add(command); Revision++; return true;
        }
        public bool Redo()
        {
            EnsureNoStroke(); RefuseInBatch("Redo"); if (redo.Count == 0) return false;
            EndCoalescing();
            var command = redo[redo.Count - 1]; command.Apply(); redo.RemoveAt(redo.Count - 1); undo.Add(command); Revision++; return true;
        }
        public void ClearHistory() { EnsureNoStroke(); RefuseInBatch("Clearing the history"); undo.Clear(); redo.Clear(); historyBytes = 0; }
        internal void EnsureNoStroke()
        {
            if (notifyingHistory) throw new InvalidOperationException("Do not mutate document state inside a history notification.");
            if (activeStroke != null) throw new InvalidOperationException("Finish or cancel the active stroke first.");
        }
        internal void BeforeExternalMutation() { EnsureNoStroke(); }
        internal void AfterExternalMutation() { ClearHistory(); Revision++; }
        internal void PixelsChanged() { Revision++; }
        /// <summary>Adds the tiles of a channel whose composite may have changed after since (a ChangeSerial value) to
        /// changed: tiles whose pixels changed (strokes, cancel, undo/redo, SetPixel/ImportTile/Clear), and every tile a
        /// layer holds when its order, visibility, opacity, blend mode or enabled channels change or it is added or
        /// removed — a layer cannot affect tiles where it has no pixels. Returns false when since is not a ChangeSerial
        /// from this document (the caller must then recomposite everything). Can include tiles that changed back. Layers with a
        /// generator are included when the mesh maps it reads changed (the inputs are looked at here; see
        /// <see cref="GeneratorInputs"/>).</summary>
        public bool TryGetChangedTiles(PaintChannel channel, long since, ICollection<TileCoord> changed)
        {
            PaintLayer.ValidateChannel(channel);
            if (changed == null) throw new ArgumentNullException(nameof(changed));
            if (since < 0 || since > changeSerial) return false;
            PollGeneratorInputs(); // 焼き直したマップなどで Generator の層が変われば、ここで「変わった」に入る
            Dictionary<TileCoord, long> serials;
            if (tileSerials.TryGetValue(channel, out serials))
                foreach (var entry in serials) if (entry.Value > since) changed.Add(entry.Key);
            AddFilterInfluence(channel, since, changed);
            return true;
        }
        internal void MarkTileChanged(PaintChannel channel, TileCoord coord)
        {
            Dictionary<TileCoord, long> serials;
            if (!tileSerials.TryGetValue(channel, out serials)) tileSerials.Add(channel, serials = new Dictionary<TileCoord, long>());
            serials[coord] = ++changeSerial;
        }
        /// <summary>A mask tile can change the composite of every channel the layer has.</summary>
        internal void MarkMaskTileChanged(PaintLayer layer, TileCoord coord)
        { foreach (var channel in layer.CoveredChannels) MarkTileChanged(channel, coord); RecordMaskHalo(layer, coord); }
        /// <summary>Marks every tile the layer holds (in one channel, or all when channel is null) as changed.</summary>
        private void MarkLayerChanged(PaintLayer layer, PaintChannel? channel)
        {
            if (layer.IsGroup)
            {
                // グループ自身は画素を持たない。中身のどれかが関わるタイルだけが変わり得る。
                foreach (var l in layers) if (IsDescendant(l, layer)) MarkLayerChanged(l, channel);
                if (layer.Mask != null) foreach (var target in channel.HasValue ? new[] { channel.Value } : (PaintChannel[])Enum.GetValues(typeof(PaintChannel)))
                        foreach (var coord in MaskMarkTiles(layer.Mask)) MarkTileChanged(target, coord);
                return;
            }
            if (layer.Kind != LayerKind.Raster)
            {
                // A fill or adjustment covers the whole canvas. Mark every tile for the channel (or all channels): the value may just
                // have been removed, so the layer's current content cannot tell which channels it used to cover.
                var targets = channel.HasValue ? new[] { channel.Value } : (PaintChannel[])Enum.GetValues(typeof(PaintChannel));
                foreach (var target in targets) foreach (var coord in EnumerateCanvasTiles()) MarkTileChanged(target, coord);
                return;
            }
            foreach (var entry in layer.Channels)
                if (channel == null || entry.Key == channel.Value)
                    foreach (var coord in layer.EnumerateContentTiles(entry.Key)) MarkTileChanged(entry.Key, coord);
        }
        /// <summary>A history command whose apply and revert change how one layer composites, but not its pixels.
        /// Clipped layers are re-marked too: moving, hiding, adding or removing a layer can change which base a clipped layer
        /// is drawn inside, and so where its pixels are visible, without touching the clipped layer itself.</summary>
        /// <summary>LayerScoped for a whole block (a group and its contents).</summary>
        private DelegateCommand SubtreeScoped(List<PaintLayer> block, Action apply, Action revert, long cost)
        {
            return new DelegateCommand(() => { foreach (var l in block) MarkLayerChanged(l, null); apply(); MarkClippedLayersChanged(); },
                () => { revert(); foreach (var l in block) MarkLayerChanged(l, null); MarkClippedLayersChanged(); }, cost);
        }
        private DelegateCommand LayerScoped(PaintLayer layer, PaintChannel? channel, Action apply, Action revert, long cost)
        {
            return new DelegateCommand(() => { apply(); MarkLayerChanged(layer, channel); MarkClippedLayersChanged(); },
                () => { revert(); MarkLayerChanged(layer, channel); MarkClippedLayersChanged(); }, cost);
        }
        /// <summary>Every layer with a clipping mark, the bottom one included: a marked layer at the bottom is drawn unclipped, so moving
        /// it to or from the bottom changes where it shows.</summary>
        private void MarkClippedLayersChanged()
        { for (int i = 0; i < layers.Count; i++) if (layers[i].Clipping) MarkLayerChanged(layers[i], null); }
        internal void EnsureSourceGrowth(long additionalBytes)
        {
            if (additionalBytes > 0 && additionalBytes > sourceBudgetBytes - AllocatedBytes)
                throw new InvalidOperationException("Source tile payload budget exceeded. Cancelled safely; raise SourceBudgetBytes or reduce document data.");
        }
        internal void EnsureStrokeBudget(long projectedBytes)
        {
            if (projectedBytes > activeStrokeBudgetBytes)
                throw new InvalidOperationException("Active stroke rollback budget exceeded. Stroke cancelled safely; use shorter strokes or raise ActiveStrokeBudgetBytes.");
        }
        internal void FinishStroke(BrushStroke stroke, IHistoryCommand command)
        {
            if (!ReferenceEquals(stroke, activeStroke)) throw new InvalidOperationException("Stroke does not own this document.");
            if (command != null) Push(command); activeStroke = null;
        }
        private void Execute(IHistoryCommand command) { command.Apply(); Revision++; Push(command); }
        // 連続した同じ対象への変更（スライダーのドラッグ）を 1 つの Undo にまとめるための状態。
        private object coalesceKey; private IHistoryCommand lastCoalesced;
        /// <summary>With a key, an edit merges into the previous edit that used the same key, as long as nothing else
        /// happened in between (no other edit, stroke, undo or redo) and EndCoalescing was not called. The merged entry
        /// undoes back to the value before the first edit of the run.</summary>
        private void Execute(IHistoryCommand command, object key)
        {
            if (key != null && Equals(key, coalesceKey) && redo.Count == 0 && undo.Count > 0 && ReferenceEquals(undo[undo.Count - 1], lastCoalesced))
            {
                var top = undo[undo.Count - 1];
                command.Apply(); Revision++;
                var merged = new DelegateCommand(command.Apply, top.Revert, top.ByteCost);
                undo[undo.Count - 1] = merged; lastCoalesced = merged; return;
            }
            Execute(command);
            if (key != null && undo.Count > 0) { coalesceKey = key; lastCoalesced = undo[undo.Count - 1]; }
        }
        /// <summary>Ends the current run of coalesced edits (the UI calls this when a slider drag ends), so the next edit
        /// becomes its own undo step.</summary>
        public void EndCoalescing() { coalesceKey = null; lastCoalesced = null; }
        /// <summary>Reverts the current run of coalesced edits and drops its history entry, as if the run had not happened (a drag
        /// the user cancels with Escape or by leaving the window). Redo is left as it is (the run's first edit already cleared it).
        /// False, changing nothing, when no run is open.</summary>
        public bool CancelCoalescing()
        {
            EnsureNoStroke(); RefuseInBatch("Cancelling an edit");
            if (lastCoalesced == null || undo.Count == 0 || !ReferenceEquals(undo[undo.Count - 1], lastCoalesced)) { EndCoalescing(); return false; }
            var command = undo[undo.Count - 1]; command.Revert(); undo.RemoveAt(undo.Count - 1); historyBytes -= command.ByteCost; Revision++;
            EndCoalescing();
            return true;
        }

        private bool batching;
        /// <summary>True while <see cref="Batch"/> runs its edits.</summary>
        public bool IsBatching { get { return batching; } }
        /// <summary>Runs edits as one undo step: every history entry they add is merged into one, so one Undo reverts them all
        /// and one Redo applies them again. If the edits throw, the entries they added are reverted in reverse order and the
        /// history (undo, redo and its byte count) is as before. History is trimmed to the budget once, after the batch. Strokes,
        /// Undo, Redo and clearing the history are refused inside a batch, and batches do not nest.</summary>
        public void Batch(Action edits)
        {
            if (edits == null) throw new ArgumentNullException(nameof(edits));
            EnsureNoStroke(); RefuseInBatch("A batch");
            EndCoalescing(); // 前の連続した変更（スライダー）に、まとめの中の変更を混ぜない
            var savedRedo = new List<IHistoryCommand>(redo); long savedBytes = historyBytes; int start = undo.Count;
            batching = true;
            try { edits(); }
            catch
            {
                for (int i = undo.Count - 1; i >= start; i--) { undo[i].Revert(); undo.RemoveAt(i); Revision++; }
                redo.Clear(); redo.AddRange(savedRedo); historyBytes = savedBytes; EndCoalescing();
                throw;
            }
            finally { batching = false; }
            int added = undo.Count - start;
            if (added > 1)
            {
                var steps = undo.GetRange(start, added); undo.RemoveRange(start, added);
                undo.Add(new CompoundCommand(steps)); // 合計の大きさは同じなので historyBytes はそのまま
            }
            EndCoalescing(); TrimHistory();
        }
        private void RefuseInBatch(string what) { if (batching) throw new InvalidOperationException(what + " cannot run inside a batch of edits."); }
        private void Push(IHistoryCommand command)
        {
            coalesceKey = null; lastCoalesced = null; // any new history entry ends a coalescing run
            foreach (var old in redo) historyBytes -= old.ByteCost;
            redo.Clear(); undo.Add(command); historyBytes += command.ByteCost;
            if (!batching) TrimHistory(); // まとめの途中で古い履歴を落とすと、失敗したときに元へ戻せない
        }
        private void TrimHistory()
        {
            long overage = historyBytes - undoBudgetBytes;
            if (overage <= 0) return;
            long projected = historyBytes, discarded = 0; int droppable = Math.Max(0, undo.Count - minimumUndoSteps);
            for (int i = 0; i < droppable; i++) { if (projected <= undoBudgetBytes) break; projected -= undo[i].ByteCost; discarded += undo[i].ByteCost; }
            foreach (var command in redo) { if (projected <= undoBudgetBytes) break; projected -= command.ByteCost; discarded += command.ByteCost; }
            if (discarded == 0) return; // only protected steps are over budget
            NotifyHistoryTrimming(discarded);
            // Preserve contiguous reachable history. Oldest undo is farthest away; furthest redo is index zero.
            // The newest MinimumUndoSteps undo steps stay even over budget.
            while (historyBytes > undoBudgetBytes && undo.Count > minimumUndoSteps)
            { historyBytes -= undo[0].ByteCost; undo.RemoveAt(0); }
            while (historyBytes > undoBudgetBytes && redo.Count > 0)
            { historyBytes -= redo[0].ByteCost; redo.RemoveAt(0); }
        }
        private void NotifyHistoryTrimming(long bytes)
        {
            var handlers = HistoryTrimming; if (handlers == null) return;
            notifyingHistory = true;
            try
            {
                foreach (Action<long> handler in handlers.GetInvocationList())
                { try { handler(bytes); } catch { /* A UI observer cannot corrupt history bookkeeping. */ } }
            }
            finally { notifyingHistory = false; }
        }
    }

    internal interface IHistoryCommand { long ByteCost { get; } void Apply(); void Revert(); }
    /// <summary>Several history entries undone and redone as one (<see cref="PaintDocument.Batch"/>).</summary>
    internal sealed class CompoundCommand : IHistoryCommand
    {
        private readonly List<IHistoryCommand> steps;
        public long ByteCost { get; private set; }
        internal CompoundCommand(List<IHistoryCommand> steps) { this.steps = steps; foreach (var s in steps) ByteCost += s.ByteCost; }
        public void Apply() { foreach (var s in steps) s.Apply(); }
        public void Revert() { for (int i = steps.Count - 1; i >= 0; i--) steps[i].Revert(); }
    }
    internal sealed class DelegateCommand : IHistoryCommand
    {
        private readonly Action apply, revert;
        public long ByteCost { get; private set; }
        internal DelegateCommand(Action apply, Action revert, long cost) { this.apply = apply; this.revert = revert; ByteCost = cost; }
        public void Apply() { apply(); }
        public void Revert() { revert(); }
    }
}
