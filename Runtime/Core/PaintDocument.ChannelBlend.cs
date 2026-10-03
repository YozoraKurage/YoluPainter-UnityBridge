using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>A layer's own blend mode and/or opacity in one channel (Substance Painter's per-channel blending). A part that is null
    /// follows the layer's <see cref="PaintLayer.BlendMode"/> / <see cref="PaintLayer.Opacity"/>; a part that is set replaces it in that
    /// channel only. Visibility, the mask and clipping stay shared by all channels. Saved with the document (native version 14).</summary>
    public readonly struct ChannelBlend : IEquatable<ChannelBlend>
    {
        /// <summary>The mode in this channel, or null for the layer's. Pass through is for groups only.</summary>
        public LayerBlendMode? Mode { get; }
        /// <summary>The opacity (0..1) in this channel, or null for the layer's.</summary>
        public double? Opacity { get; }
        public ChannelBlend(LayerBlendMode? mode, double? opacity) { Mode = mode; Opacity = opacity; }
        /// <summary>Neither part is set: the channel follows the layer.</summary>
        public bool IsEmpty => Mode == null && Opacity == null;
        public bool Equals(ChannelBlend other) => Mode == other.Mode && Nullable.Equals(Opacity, other.Opacity);
        public override bool Equals(object obj) => obj is ChannelBlend b && Equals(b);
        public override int GetHashCode() => (Mode.HasValue ? (int)Mode.Value + 1 : 0) * 397 ^ (Opacity?.GetHashCode() ?? 0);
        public override string ToString() => "(" + (Mode?.ToString() ?? "layer") + ", " + (Opacity?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "layer") + ")";
    }

    public sealed partial class PaintLayer
    {
        private readonly Dictionary<PaintChannel, ChannelBlend> channelBlends = new Dictionary<PaintChannel, ChannelBlend>();
        private ReadOnlyDictionary<PaintChannel, ChannelBlend> channelBlendsView;
        /// <summary>The channels whose blend mode or opacity the layer sets for itself (never an empty <see cref="ChannelBlend"/>).</summary>
        public IReadOnlyDictionary<PaintChannel, ChannelBlend> ChannelBlends => channelBlendsView ?? (channelBlendsView = new ReadOnlyDictionary<PaintChannel, ChannelBlend>(channelBlends));
        /// <summary>The blend mode the layer composites with in a channel: its own for the channel, else the layer's.</summary>
        public LayerBlendMode BlendModeIn(PaintChannel channel) => channelBlends.TryGetValue(channel, out var b) && b.Mode.HasValue ? b.Mode.Value : BlendMode;
        /// <summary>The opacity the layer composites with in a channel: its own for the channel, else the layer's.</summary>
        public double OpacityIn(PaintChannel channel) => channelBlends.TryGetValue(channel, out var b) && b.Opacity.HasValue ? b.Opacity.Value : Opacity;
        /// <summary>The layer's own setting for a channel (empty when it follows the layer).</summary>
        public ChannelBlend ChannelBlendOf(PaintChannel channel) => channelBlends.TryGetValue(channel, out var b) ? b : default;
        /// <summary>True when some channel uses another mode or opacity than the layer's.</summary>
        public bool HasChannelBlends => channelBlends.Count > 0;
        internal void SetChannelBlendInternal(PaintChannel channel, ChannelBlend blend)
        { if (blend.IsEmpty) channelBlends.Remove(channel); else channelBlends[channel] = blend; }
        internal void CopyChannelBlendsFrom(PaintLayer source, bool passThroughAsNormal = false)
        {
            channelBlends.Clear();
            foreach (var entry in source.channelBlends)
            {
                var b = entry.Value;
                if (passThroughAsNormal && b.Mode == LayerBlendMode.PassThrough) b = new ChannelBlend(LayerBlendMode.Normal, b.Opacity);
                channelBlends.Add(entry.Key, b);
            }
        }
    }

    /// <summary>Per-channel blend modes and opacities (<see cref="ChannelBlend"/>): each change is one undo step, refused under Lock All like
    /// the layer's own mode and opacity, and marks only the changed channel's tiles in the change journal.</summary>
    public sealed partial class PaintDocument
    {
        /// <summary>Sets a layer's blend mode and opacity in one channel together (an empty value makes the channel follow the layer
        /// again), as one undo step.</summary>
        public void SetChannelBlend(Guid id, PaintChannel channel, ChannelBlend blend, bool coalesce = false)
        {
            EnsureNoStroke(); PaintLayer.ValidateChannel(channel); var layer = GetLayer(id);
            ValidateChannelBlend(layer, blend);
            var old = layer.ChannelBlendOf(channel); if (old.Equals(blend)) return;
            RefuseLockedAttributes(layer);
            Execute(LayerScoped(layer, channel, () => layer.SetChannelBlendInternal(channel, blend), () => layer.SetChannelBlendInternal(channel, old), 64),
                coalesce ? (object)("channelBlend", id, channel) : null);
        }
        /// <summary>Sets (or with null clears, so the layer's mode applies) a layer's blend mode in one channel, as one undo step.</summary>
        public void SetChannelBlendMode(Guid id, PaintChannel channel, LayerBlendMode? mode)
        {
            PaintLayer.ValidateChannel(channel);
            SetChannelBlend(id, channel, new ChannelBlend(mode, GetLayer(id).ChannelBlendOf(channel).Opacity));
        }
        /// <summary>Sets (or with null clears, so the layer's opacity applies) a layer's opacity in one channel, as one undo step (a slider
        /// drag with coalesce is one step).</summary>
        public void SetChannelOpacity(Guid id, PaintChannel channel, double? opacity, bool coalesce = false)
        {
            PaintLayer.ValidateChannel(channel);
            SetChannelBlend(id, channel, new ChannelBlend(GetLayer(id).ChannelBlendOf(channel).Mode, opacity), coalesce);
        }

        /// <summary>The checks of a per-channel setting: a defined mode (pass through only on a group) and a finite opacity in 0..1.</summary>
        internal static void ValidateChannelBlend(PaintLayer layer, ChannelBlend blend)
        {
            if (blend.Mode.HasValue)
            {
                if (!Enum.IsDefined(typeof(LayerBlendMode), blend.Mode.Value)) throw new ArgumentOutOfRangeException(nameof(blend), "Unknown blend mode " + (int)blend.Mode.Value + ".");
                if (blend.Mode.Value == LayerBlendMode.PassThrough && !layer.IsGroup) throw new ArgumentException("Pass through applies to groups only.", nameof(blend));
            }
            if (blend.Opacity.HasValue)
            {
                MathUtil.RequireFinite(blend.Opacity.Value, nameof(blend));
                if (blend.Opacity.Value < 0 || blend.Opacity.Value > 1) throw new ArgumentOutOfRangeException(nameof(blend), "Opacity must be 0..1.");
            }
        }
        /// <summary>For loaders: sets a per-channel setting without history (checked like <see cref="SetChannelBlend"/>, locks aside).</summary>
        internal void SetChannelBlendForLoad(PaintLayer layer, PaintChannel channel, ChannelBlend blend)
        { PaintLayer.ValidateChannel(channel); ValidateChannelBlend(layer, blend); layer.SetChannelBlendInternal(channel, blend); }

        /// <summary>True when the layer composites as a plain layer in every channel: Normal at 100 % (its own settings included).</summary>
        internal static bool PlainInEveryChannel(PaintLayer layer)
        {
            if (layer.BlendMode != LayerBlendMode.Normal || layer.Opacity != 1) return false;
            foreach (var entry in layer.ChannelBlends)
                if (layer.BlendModeIn(entry.Key) != LayerBlendMode.Normal || layer.OpacityIn(entry.Key) != 1) return false;
            return true;
        }
    }
}
