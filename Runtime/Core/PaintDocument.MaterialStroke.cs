using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>One channel a material stroke paints and the value it lays there (straight RGBA8, as a one-channel stroke's brush colour:
    /// a colour for Color and Emission, a grey for Roughness, Metallic and Height, an encoded direction for Normal).</summary>
    public readonly struct ChannelPaint : IEquatable<ChannelPaint>
    {
        public PaintChannel Channel { get; }
        public Rgba32 Value { get; }
        public ChannelPaint(PaintChannel channel, Rgba32 value) { Channel = channel; Value = value; }
        public bool Equals(ChannelPaint other) => Channel == other.Channel && Value == other.Value;
        public override bool Equals(object obj) => obj is ChannelPaint p && Equals(p);
        public override int GetHashCode() => (int)Channel * 397 ^ Value.GetHashCode();
        public override string ToString() => Channel + " " + Value;
    }

    /// <summary>Material strokes: one stroke that paints several channels of a layer with the same dabs (Substance Painter's material
    /// painting). See <see cref="BrushStroke"/> for how the channels share the coverage.</summary>
    public sealed partial class PaintDocument
    {
        /// <summary>Starts a stroke that paints every listed channel of a paint layer with one coverage: the same dabs, pressure,
        /// stabilizer, tapers, dual brush and selection; each channel with its own value (<see cref="ChannelPaint.Value"/> replaces the
        /// brush colour, colour dynamics only apply to Color and Emission as in <see cref="BrushSettings.ForChannel"/>). Every channel ends
        /// with the bytes a one-channel <see cref="BeginStroke"/> with that value gives. Channels that are switched off on the layer are
        /// switched on now and switched off again by a cancel (or a commit that changed nothing); otherwise switching them on is part of
        /// the stroke's single undo step. Erase erases every listed channel; Lock Transparent Pixels keeps each channel's alpha.
        /// Refused, with nothing changed, for fill and adjustment layers and groups, layers drawn by a path, locked pixels, an empty or
        /// repeated channel list. The rollback of all channels counts against <see cref="ActiveStrokeBudgetBytes"/> together.</summary>
        public BrushStroke BeginMaterialStroke(Guid layerId, IReadOnlyList<ChannelPaint> channels, BrushSettings settings)
        {
            EnsureNoStroke(); RefuseInBatch("A stroke"); if (settings == null) throw new ArgumentNullException(nameof(settings)); settings.Validate();
            if (channels == null) throw new ArgumentNullException(nameof(channels));
            if (channels.Count == 0) throw new ArgumentException("Choose at least one channel to paint.", nameof(channels));
            var seen = new HashSet<PaintChannel>();
            foreach (var c in channels)
            {
                PaintLayer.ValidateChannel(c.Channel);
                if (!seen.Add(c.Channel)) throw new ArgumentException("The " + c.Channel + " channel is listed twice.", nameof(channels));
            }
            var layer = GetLayer(layerId);
            if (layer.Kind == LayerKind.Fill) throw new InvalidOperationException("Fill layers are generated from their values and cannot be painted. Paint on the layer's mask, or add a paint layer.");
            if (layer.Kind == LayerKind.Adjustment) throw new InvalidOperationException("Adjustment layers have no pixel surface. Change the adjustment, or paint on the layer's mask.");
            if (layer.IsGroup) throw new InvalidOperationException("A group has no pixels. Select a layer inside it to paint, or paint the group's mask.");
            RefuseLockedPixels(layer, settings.Erase);
            RefusePathLayer(layer);
            IHistoryCommand enabling = null;
            var off = new List<PaintChannel>(); foreach (var c in channels) if (!layer.IsChannelEnabled(c.Channel)) off.Add(c.Channel);
            if (off.Count > 0) { enabling = EnableForStroke(layer, off); enabling.Apply(); Revision++; }
            try
            {
                var paint = new List<(SparseTileSurface, BrushSettings)>();
                foreach (var c in channels)
                {
                    var s = settings.ForChannel(c.Channel); s.Color = c.Value;
                    paint.Add((layer.GetChannel(c.Channel), s));
                }
                activeStroke = new BrushStroke(this, paint, KeepsAlpha(layer), enabling);
            }
            catch { if (enabling != null) RevertStrokeSetup(enabling); throw; }
            return activeStroke;
        }

        /// <summary>Switching channels on for a stroke (the first step of its undo entry): a channel without a surface gets one that is kept
        /// across undo and redo (detached on undo, the same object attached again on redo).</summary>
        IHistoryCommand EnableForStroke(PaintLayer layer, List<PaintChannel> channels)
        {
            var created = new SparseTileSurface[channels.Count];
            for (int i = 0; i < channels.Count; i++) if (!layer.TryGetChannel(channels[i], out _)) created[i] = layer.NewChannelSurface(channels[i]);
            return new DelegateCommand(() =>
            {
                for (int i = 0; i < channels.Count; i++)
                {
                    if (created[i] != null) layer.AttachChannel(channels[i], created[i]);
                    layer.Enable(channels[i], true); MarkLayerChanged(layer, channels[i]);
                }
                MarkClippedLayersChanged();
            }, () =>
            {
                for (int i = channels.Count - 1; i >= 0; i--)
                {
                    layer.Enable(channels[i], false); MarkLayerChanged(layer, channels[i]);
                    if (created[i] != null) layer.DetachChannel(channels[i]);
                }
                MarkClippedLayersChanged();
            }, 64L * channels.Count);
        }
        /// <summary>Undoes a stroke's setup (channels it switched on) when the stroke is cancelled or committed nothing.</summary>
        internal void RevertStrokeSetup(IHistoryCommand setup) { setup.Revert(); Revision++; }
    }
}
