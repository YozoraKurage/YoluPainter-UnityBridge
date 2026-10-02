using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>Raster mask of one layer, shared by all of its channels. The surface stores the amount to HIDE in each
    /// pixel's alpha (RGB stays zero), so an absent tile reveals everything and an untouched mask costs no memory.
    /// Painting hides, erasing reveals. Enabled, Inverted and Density are non-destructive parameters.</summary>
    public sealed class RasterMask
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
        public double FactorAt(int x, int y) { return Factor(Surface.GetPixel(x, y).A); }
        /// <summary>True when the mask cannot change any pixel: disabled, zero density, or nothing hidden and not inverted.</summary>
        public bool IsNeutral { get { return !Enabled || Density == 0 || (!Inverted && Surface.TileCount == 0); } }
    }

    /// <summary>Raster layers own pixels. Fill layers own one value per channel and generate their tiles on demand
    /// (the value is the source; nothing is allocated per pixel).
    /// Adjustment layers own no pixels either: they change the composite of the layers below them.</summary>
    public enum LayerKind { Raster = 0, Fill = 1, Adjustment = 2 }

    public sealed class PaintLayer
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
        /// <summary>Raster layers' pixel surfaces. Always empty for fill layers.</summary>
        public IReadOnlyDictionary<PaintChannel, SparseTileSurface> Channels { get; private set; }
        /// <summary>Fill layers' value per channel. Always empty for other kinds.</summary>
        public IReadOnlyDictionary<PaintChannel, Rgba32> FillValues { get; private set; }
        /// <summary>Adjustment layers' parameters; null for other kinds.</summary>
        public AdjustmentSettings Adjustment { get; internal set; }
        /// <summary>The layer's raster mask, or null when it has none.</summary>
        public RasterMask Mask { get; internal set; }
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
                    default: return new List<PaintChannel>(channels.Keys);
                }
            }
        }
        /// <summary>The layer's own pixel (before mask, opacity and blending). Transparent where it has no content.</summary>
        public Rgba32 GetPixel(PaintChannel channel, int x, int y)
        {
            if (Kind == LayerKind.Adjustment) return Rgba32.Transparent; // owns no pixels; see AdjustmentSettings.Composite
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
        /// <summary>Tiles where the layer may have pixels in the channel: a raster surface's occupied tiles, or every
        /// canvas tile for a fill value.</summary>
        public IEnumerable<TileCoord> EnumerateContentTiles(PaintChannel channel)
        {
            if (Kind == LayerKind.Raster)
                return channels.TryGetValue(channel, out var surface) ? surface.EnumerateTileCoordinates() : new TileCoord[0];
            return HasContent(channel) ? document.EnumerateCanvasTiles() : new TileCoord[0];
        }
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
        /// <summary>Adds a fill layer on top. values sets the initial value per channel (each one enabled); the layer
        /// covers the whole canvas wherever its channel has a value. Use a mask to limit where it shows.</summary>
        public PaintLayer AddFillLayer(string name, IDictionary<PaintChannel, Rgba32> values = null, Guid? id = null)
        {
            EnsureNoStroke(); Guid layerId = id ?? Guid.NewGuid();
            if (layerId == Guid.Empty) throw new ArgumentException("Layer ID must not be empty.", nameof(id));
            foreach (var existing in layers) if (existing.Id == layerId) throw new ArgumentException("Duplicate layer ID.", nameof(id));
            var layer = new PaintLayer(this, name ?? "Fill", layerId, LayerKind.Fill);
            if (values != null) foreach (var entry in values) { PaintLayer.ValidateChannel(entry.Key); layer.SetFillValueInternal(entry.Key, entry.Value); layer.Enable(entry.Key, true); }
            int index = layers.Count;
            Execute(LayerScoped(layer, null, () => layers.Insert(index, layer), () => layers.Remove(layer), 128));
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
            Execute(LayerScoped(layer, channel,
                () => { layer.SetFillValueInternal(channel, value); if (value.HasValue) layer.Enable(channel, true); },
                () => { layer.SetFillValueInternal(channel, old); layer.Enable(channel, wasEnabled); }, 64), coalesce ? (object)("fill", id, channel) : null);
        }
        /// <summary>Adds an adjustment layer on top that changes the composite below it in the given channels (all channels
        /// the adjustment applies to when null). Hue/saturation can only target Color and Emission.</summary>
        public PaintLayer AddAdjustmentLayer(string name, AdjustmentSettings settings, IEnumerable<PaintChannel> channels = null, Guid? id = null)
        {
            EnsureNoStroke(); if (settings == null) throw new ArgumentNullException(nameof(settings)); settings.Validate();
            Guid layerId = id ?? Guid.NewGuid();
            if (layerId == Guid.Empty) throw new ArgumentException("Layer ID must not be empty.", nameof(id));
            foreach (var existing in layers) if (existing.Id == layerId) throw new ArgumentException("Duplicate layer ID.", nameof(id));
            var layer = new PaintLayer(this, name ?? settings.Type.ToString(), layerId, LayerKind.Adjustment) { Adjustment = settings };
            foreach (PaintChannel channel in channels ?? (PaintChannel[])Enum.GetValues(typeof(PaintChannel)))
            {
                PaintLayer.ValidateChannel(channel);
                if (!settings.AppliesTo(channel)) { if (channels == null) continue; throw new InvalidOperationException(settings.Type + " cannot be applied to the " + channel + " channel."); }
                layer.Enable(channel, true);
            }
            int index = layers.Count;
            Execute(LayerScoped(layer, null, () => layers.Insert(index, layer), () => layers.Remove(layer), 128));
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
        public void SetLayerOpacity(Guid id, double opacity, bool coalesce = false)
        {
            EnsureNoStroke(); MathUtil.RequireFinite(opacity, nameof(opacity));
            if (opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
            var layer = GetLayer(id); double old = layer.Opacity; if (old == opacity) return;
            Execute(LayerScoped(layer, null, () => layer.Opacity = opacity, () => layer.Opacity = old, 64), coalesce ? (object)("opacity", id) : null);
        }
        public void SetLayerBlendMode(Guid id, LayerBlendMode mode)
        {
            EnsureNoStroke(); if (!Enum.IsDefined(typeof(LayerBlendMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            var layer = GetLayer(id); var old = layer.BlendMode; if (old == mode) return;
            Execute(LayerScoped(layer, null, () => layer.BlendMode = mode, () => layer.BlendMode = old, 64));
        }
        /// <summary>Clips the layer to the layer below (or releases it). Undoable.</summary>
        public void SetLayerClipping(Guid id, bool clipping)
        {
            EnsureNoStroke(); var layer = GetLayer(id); bool old = layer.Clipping; if (old == clipping) return;
            Execute(LayerScoped(layer, null, () => layer.Clipping = clipping, () => layer.Clipping = old, 64));
        }
        /// <summary>True when the layer at index is effectively clipped (flag set and not the bottom layer).</summary>
        public bool IsEffectivelyClipped(int index) { return index > 0 && index < layers.Count && layers[index].Clipping; }
        public void SetChannelEnabled(Guid id, PaintChannel channel, bool enabled)
        {
            EnsureNoStroke(); PaintLayer.ValidateChannel(channel); var layer = GetLayer(id);
            bool old = layer.IsChannelEnabled(channel); if (old == enabled) return;
            if (enabled && layer.Kind == LayerKind.Adjustment && !layer.Adjustment.AppliesTo(channel))
                throw new InvalidOperationException(layer.Adjustment.Type + " cannot be applied to the " + channel + " channel.");
            Execute(LayerScoped(layer, channel, () => layer.Enable(channel, enabled), () => layer.Enable(channel, old), 64));
        }
        /// <summary>Adds an empty raster mask (reveals everything) to a layer. Undoable.</summary>
        public RasterMask AddLayerMask(Guid id)
        {
            EnsureNoStroke(); var layer = GetLayer(id);
            if (layer.Mask != null) throw new InvalidOperationException("The layer already has a mask.");
            var surface = new SparseTileSurface(Width, Height, TileSize);
            surface.BeforeExternalMutation = BeforeExternalMutation;
            surface.AfterExternalMutation = AfterExternalMutation;
            surface.BeforeSourceGrowth = EnsureSourceGrowth;
            surface.TileChanged = coord => MarkMaskTileChanged(layer, coord);
            var mask = new RasterMask(surface);
            Execute(LayerScoped(layer, null, () => layer.Mask = mask, () => layer.Mask = null, 64));
            return mask;
        }
        /// <summary>Removes a layer's mask. Undo restores the same mask, pixels and parameters.</summary>
        public void RemoveLayerMask(Guid id)
        {
            EnsureNoStroke(); var layer = GetLayer(id); var mask = layer.Mask;
            if (mask == null) throw new InvalidOperationException("The layer has no mask.");
            Execute(LayerScoped(layer, null, () => layer.Mask = null, () => { EnsureSourceGrowth(mask.Surface.AllocatedBytes); layer.Mask = mask; }, 64 + mask.Surface.AllocatedBytes));
        }
        public void SetLayerMaskEnabled(Guid id, bool enabled)
        {
            EnsureNoStroke(); var mask = RequireMask(id, out var layer); bool old = mask.Enabled; if (old == enabled) return;
            Execute(LayerScoped(layer, null, () => mask.Enabled = enabled, () => mask.Enabled = old, 64));
        }
        public void SetLayerMaskInverted(Guid id, bool inverted)
        {
            EnsureNoStroke(); var mask = RequireMask(id, out var layer); bool old = mask.Inverted; if (old == inverted) return;
            Execute(LayerScoped(layer, null, () => mask.Inverted = inverted, () => mask.Inverted = old, 64));
        }
        public void SetLayerMaskDensity(Guid id, double density, bool coalesce = false)
        {
            EnsureNoStroke(); MathUtil.RequireFinite(density, nameof(density));
            if (density < 0 || density > 1) throw new ArgumentOutOfRangeException(nameof(density));
            var mask = RequireMask(id, out var layer); double old = mask.Density; if (old == density) return;
            Execute(LayerScoped(layer, null, () => mask.Density = density, () => mask.Density = old, 64), coalesce ? (object)("maskDensity", id) : null);
        }
        /// <summary>Starts a stroke on a layer's mask: painting hides, Erase reveals. The brush colour is ignored
        /// (the mask stores only a hide amount); opacity, flow, hardness and pressure apply as usual.</summary>
        public BrushStroke BeginMaskStroke(Guid layerId, BrushSettings settings)
        {
            EnsureNoStroke(); if (settings == null) throw new ArgumentNullException(nameof(settings)); settings.Validate();
            var mask = RequireMask(layerId, out _);
            var maskSettings = settings.Clone(); maskSettings.Color = new Rgba32(0, 0, 0, 255);
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
            EnsureNoStroke(); if (settings == null) throw new ArgumentNullException(nameof(settings)); settings.Validate();
            var layer = GetLayer(layerId);
            if (layer.Kind == LayerKind.Fill) throw new InvalidOperationException("Fill layers are generated from their values and cannot be painted. Paint on the layer's mask, or add a paint layer.");
            var surface = layer.GetChannel(channel);
            if (!layer.IsChannelEnabled(channel)) throw new InvalidOperationException("Enable the target channel before painting.");
            activeStroke = new BrushStroke(this, surface, settings.Clone()); return activeStroke;
        }
        public byte[] Composite(PaintChannel channel) { return CpuCompositor.Composite(this, channel); }
        public Rgba32 CompositePixel(PaintChannel channel, int x, int y) { return CpuCompositor.CompositePixel(this, channel, x, y); }
        public bool Undo()
        {
            EnsureNoStroke(); if (undo.Count == 0) return false;
            EndCoalescing();
            var command = undo[undo.Count - 1]; command.Revert(); undo.RemoveAt(undo.Count - 1); redo.Add(command); Revision++; return true;
        }
        public bool Redo()
        {
            EnsureNoStroke(); if (redo.Count == 0) return false;
            EndCoalescing();
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
        /// <summary>A mask tile can change the composite of every channel the layer has.</summary>
        internal void MarkMaskTileChanged(PaintLayer layer, TileCoord coord)
        { foreach (var channel in layer.CoveredChannels) MarkTileChanged(channel, coord); }
        /// <summary>Marks every tile the layer holds (in one channel, or all when channel is null) as changed.</summary>
        private void MarkLayerChanged(PaintLayer layer, PaintChannel? channel)
        {
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
                    foreach (var coord in entry.Value.EnumerateTileCoordinates()) MarkTileChanged(entry.Key, coord);
        }
        /// <summary>A history command whose apply and revert change how one layer composites, but not its pixels.
        /// Clipped layers are re-marked too: moving, hiding, adding or removing a layer can change which base a clipped layer
        /// is drawn inside, and so where its pixels are visible, without touching the clipped layer itself.</summary>
        private DelegateCommand LayerScoped(PaintLayer layer, PaintChannel? channel, Action apply, Action revert, long cost)
        {
            return new DelegateCommand(() => { apply(); MarkLayerChanged(layer, channel); MarkClippedLayersChanged(); },
                () => { revert(); MarkLayerChanged(layer, channel); MarkClippedLayersChanged(); }, cost);
        }
        private void MarkClippedLayersChanged()
        { for (int i = 1; i < layers.Count; i++) if (layers[i].Clipping) MarkLayerChanged(layers[i], null); }
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
        private void Push(IHistoryCommand command)
        {
            coalesceKey = null; lastCoalesced = null; // any new history entry ends a coalescing run
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
