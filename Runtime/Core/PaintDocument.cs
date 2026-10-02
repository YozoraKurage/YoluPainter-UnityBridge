using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Yozolab.YoluPainter.Core
{
    public sealed class PaintLayer
    {
        private readonly PaintDocument document;
        private readonly Dictionary<PaintChannel, SparseTileSurface> channels = new Dictionary<PaintChannel, SparseTileSurface>();
        private readonly HashSet<PaintChannel> enabled = new HashSet<PaintChannel>();
        public Guid Id { get; private set; }
        public string Name { get; internal set; }
        public bool Visible { get; internal set; }
        public double Opacity { get; internal set; }
        public LayerBlendMode BlendMode { get; internal set; }
        public IReadOnlyDictionary<PaintChannel, SparseTileSurface> Channels { get; private set; }
        public IReadOnlyList<PaintChannel> EnabledChannels
        {
            get { var values = new List<PaintChannel>(enabled); values.Sort(); return values.AsReadOnly(); }
        }
        public long AllocatedBytes { get { long bytes = 0; foreach (var s in channels.Values) bytes += s.AllocatedBytes; return bytes; } }
        internal PaintLayer(PaintDocument owner, string name, Guid id)
        {
            document = owner; Name = name; Id = id; Visible = true; Opacity = 1;
            Channels = new ReadOnlyDictionary<PaintChannel, SparseTileSurface>(channels);
        }
        /// <summary>Gets (or creates and enables) a channel. For side-effect-free reads use TryGetChannel.</summary>
        public SparseTileSurface GetChannel(PaintChannel channel)
        {
            ValidateChannel(channel);
            SparseTileSurface surface;
            if (!channels.TryGetValue(channel, out surface))
            {
                document.EnsureNoStroke();
                surface = new SparseTileSurface(document.Width, document.Height, document.TileSize);
                surface.BeforeExternalMutation = document.BeforeExternalMutation;
                surface.AfterExternalMutation = document.AfterExternalMutation;
                surface.BeforeSourceGrowth = document.EnsureSourceGrowth;
                surface.TileChanged = coord => document.MarkTileChanged(channel, coord);
                channels.Add(channel, surface); enabled.Add(channel);
            }
            return surface;
        }
        public bool TryGetChannel(PaintChannel channel, out SparseTileSurface surface) { return channels.TryGetValue(channel, out surface); }
        public bool IsChannelEnabled(PaintChannel channel) { return enabled.Contains(channel); }
        internal void Enable(PaintChannel channel, bool value)
        { if (value) { GetChannel(channel); enabled.Add(channel); } else enabled.Remove(channel); }
        internal static void ValidateChannel(PaintChannel channel)
        { if (!Enum.IsDefined(typeof(PaintChannel), channel)) throw new ArgumentOutOfRangeException(nameof(channel)); }
    }

    /// <summary>Single-writer CPU raster document. Layers are bottom-to-top. Histories store exact changed tile states,
    /// never brush replay or a full canvas copy. Structural edits and strokes cannot interleave.</summary>
    public sealed class PaintDocument
    {
        private readonly List<PaintLayer> layers = new List<PaintLayer>();
        private readonly List<IHistoryCommand> undo = new List<IHistoryCommand>();
        private readonly List<IHistoryCommand> redo = new List<IHistoryCommand>();
        private long historyBytes;
        private long undoBudgetBytes;
        private long sourceBudgetBytes = 256L * 1024 * 1024;
        private long activeStrokeBudgetBytes = 64L * 1024 * 1024;
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
        public PaintLayer AddLayer(string name, Guid? id = null)
        {
            EnsureNoStroke(); Guid layerId = id ?? Guid.NewGuid();
            if (layerId == Guid.Empty) throw new ArgumentException("Layer ID must not be empty.", nameof(id));
            foreach (var existing in layers) if (existing.Id == layerId) throw new ArgumentException("Duplicate layer ID.", nameof(id));
            var layer = new PaintLayer(this, name ?? "Layer", layerId);
            layer.GetChannel(PaintChannel.Color);
            int index = layers.Count;
            Execute(LayerScoped(layer, null, () => { EnsureSourceGrowth(layer.AllocatedBytes); layers.Insert(index, layer); }, () => layers.Remove(layer), 128));
            return layer;
        }
        public PaintLayer GetLayer(Guid id)
        {
            foreach (var layer in layers) if (layer.Id == id) return layer;
            throw new KeyNotFoundException("Layer not found: " + id);
        }
        public void RemoveLayer(Guid id)
        {
            EnsureNoStroke(); PaintLayer layer = GetLayer(id); int index = layers.IndexOf(layer);
            Execute(LayerScoped(layer, null, () => layers.Remove(layer), () => { EnsureSourceGrowth(layer.AllocatedBytes); layers.Insert(index, layer); }, 128 + layer.AllocatedBytes));
        }
        public void MoveLayer(Guid id, int newIndex)
        {
            EnsureNoStroke(); var layer = GetLayer(id);
            if (newIndex < 0 || newIndex >= layers.Count) throw new ArgumentOutOfRangeException(nameof(newIndex));
            int oldIndex = layers.IndexOf(layer); if (oldIndex == newIndex) return;
            Execute(LayerScoped(layer, null, () => MoveLayerInternal(layer, newIndex), () => MoveLayerInternal(layer, oldIndex), 64));
        }
        private void MoveLayerInternal(PaintLayer layer, int index) { layers.Remove(layer); layers.Insert(index, layer); }
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
        public void SetLayerOpacity(Guid id, double opacity)
        {
            EnsureNoStroke(); MathUtil.RequireFinite(opacity, nameof(opacity));
            if (opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
            var layer = GetLayer(id); double old = layer.Opacity; if (old == opacity) return;
            Execute(LayerScoped(layer, null, () => layer.Opacity = opacity, () => layer.Opacity = old, 64));
        }
        public void SetLayerBlendMode(Guid id, LayerBlendMode mode)
        {
            EnsureNoStroke(); if (!Enum.IsDefined(typeof(LayerBlendMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            var layer = GetLayer(id); var old = layer.BlendMode; if (old == mode) return;
            Execute(LayerScoped(layer, null, () => layer.BlendMode = mode, () => layer.BlendMode = old, 64));
        }
        public void SetChannelEnabled(Guid id, PaintChannel channel, bool enabled)
        {
            EnsureNoStroke(); PaintLayer.ValidateChannel(channel); var layer = GetLayer(id);
            bool old = layer.IsChannelEnabled(channel); if (old == enabled) return;
            Execute(LayerScoped(layer, channel, () => layer.Enable(channel, enabled), () => layer.Enable(channel, old), 64));
        }
        public BrushStroke BeginStroke(Guid layerId, PaintChannel channel, BrushSettings settings)
        {
            EnsureNoStroke(); if (settings == null) throw new ArgumentNullException(nameof(settings)); settings.Validate();
            var layer = GetLayer(layerId); var surface = layer.GetChannel(channel);
            if (!layer.IsChannelEnabled(channel)) throw new InvalidOperationException("Enable the target channel before painting.");
            activeStroke = new BrushStroke(this, surface, settings.Clone()); return activeStroke;
        }
        public byte[] Composite(PaintChannel channel) { return CpuCompositor.Composite(this, channel); }
        public Rgba32 CompositePixel(PaintChannel channel, int x, int y) { return CpuCompositor.CompositePixel(this, channel, x, y); }
        public bool Undo()
        {
            EnsureNoStroke(); if (undo.Count == 0) return false;
            var command = undo[undo.Count - 1]; command.Revert(); undo.RemoveAt(undo.Count - 1); redo.Add(command); Revision++; return true;
        }
        public bool Redo()
        {
            EnsureNoStroke(); if (redo.Count == 0) return false;
            var command = redo[redo.Count - 1]; command.Apply(); redo.RemoveAt(redo.Count - 1); undo.Add(command); Revision++; return true;
        }
        public void ClearHistory() { EnsureNoStroke(); undo.Clear(); redo.Clear(); historyBytes = 0; }
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
        /// from this document (the caller must then recomposite everything). Can include tiles that changed back.</summary>
        public bool TryGetChangedTiles(PaintChannel channel, long since, ICollection<TileCoord> changed)
        {
            PaintLayer.ValidateChannel(channel);
            if (changed == null) throw new ArgumentNullException(nameof(changed));
            if (since < 0 || since > changeSerial) return false;
            Dictionary<TileCoord, long> serials;
            if (tileSerials.TryGetValue(channel, out serials))
                foreach (var entry in serials) if (entry.Value > since) changed.Add(entry.Key);
            return true;
        }
        internal void MarkTileChanged(PaintChannel channel, TileCoord coord)
        {
            Dictionary<TileCoord, long> serials;
            if (!tileSerials.TryGetValue(channel, out serials)) tileSerials.Add(channel, serials = new Dictionary<TileCoord, long>());
            serials[coord] = ++changeSerial;
        }
        /// <summary>Marks every tile the layer holds (in one channel, or all when channel is null) as changed.</summary>
        private void MarkLayerChanged(PaintLayer layer, PaintChannel? channel)
        {
            foreach (var entry in layer.Channels)
                if (channel == null || entry.Key == channel.Value)
                    foreach (var coord in entry.Value.EnumerateTileCoordinates()) MarkTileChanged(entry.Key, coord);
        }
        /// <summary>A history command whose apply and revert change how one layer composites, but not its pixels.</summary>
        private DelegateCommand LayerScoped(PaintLayer layer, PaintChannel? channel, Action apply, Action revert, long cost)
        { return new DelegateCommand(() => { apply(); MarkLayerChanged(layer, channel); }, () => { revert(); MarkLayerChanged(layer, channel); }, cost); }
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
        private void Push(IHistoryCommand command)
        {
            foreach (var old in redo) historyBytes -= old.ByteCost;
            redo.Clear(); undo.Add(command); historyBytes += command.ByteCost; TrimHistory();
        }
        private void TrimHistory()
        {
            long overage = historyBytes - undoBudgetBytes;
            if (overage <= 0) return;
            long projected = historyBytes, discarded = 0;
            foreach (var command in undo) { if (projected <= undoBudgetBytes) break; projected -= command.ByteCost; discarded += command.ByteCost; }
            foreach (var command in redo) { if (projected <= undoBudgetBytes) break; projected -= command.ByteCost; discarded += command.ByteCost; }
            NotifyHistoryTrimming(discarded);
            // Preserve contiguous reachable history. Oldest undo is farthest away; furthest redo is index zero.
            while (historyBytes > undoBudgetBytes && undo.Count > 0)
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
    internal sealed class DelegateCommand : IHistoryCommand
    {
        private readonly Action apply, revert;
        public long ByteCost { get; private set; }
        internal DelegateCommand(Action apply, Action revert, long cost) { this.apply = apply; this.revert = revert; ByteCost = cost; }
        public void Apply() { apply(); }
        public void Revert() { revert(); }
    }
}
