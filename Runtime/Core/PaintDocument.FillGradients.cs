using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Yozolab.YoluPainter.Core
{
    public sealed partial class PaintLayer
    {
        readonly Dictionary<PaintChannel, GeneratorSettings> fillGradients = new Dictionary<PaintChannel, GeneratorSettings>();
        IReadOnlyDictionary<PaintChannel, GeneratorSettings> fillGradientView;
        /// <summary>Fill channels whose source is a shape gradient. Image and gradient are mutually exclusive per channel.</summary>
        public IReadOnlyDictionary<PaintChannel, GeneratorSettings> FillGradients => fillGradientView ?? (fillGradientView = new ReadOnlyDictionary<PaintChannel, GeneratorSettings>(fillGradients));
        public bool HasFillGradient(PaintChannel channel) => fillGradients.ContainsKey(channel);
        internal void SetFillGradientInternal(PaintChannel channel, GeneratorSettings settings)
        { if (settings == null) fillGradients.Remove(channel); else fillGradients[channel] = settings; }
    }
    public sealed partial class PaintDocument
    {
        public bool HasFillGradients => layers.Any(l => l.FillGradients.Count > 0);
        static void ValidateFillGradient(PaintChannel channel, GeneratorSettings settings)
        {
            PaintLayer.ValidateChannel(channel);
            if (channel == PaintChannel.Normal) throw new ArgumentException("A shape gradient is a colour or scalar value, not a tangent normal.");
            if (settings.Type != GeneratorType.ShapeGradient || settings.Ramp == null) throw new ArgumentException("A fill gradient needs a shape gradient with a ramp.");
            if (settings.Blend != GeneratorBlend.Replace) throw new ArgumentException("A fill gradient is a source value and must use Replace.");
        }
        /// <summary>Sets or removes a fill channel's gradient in one undo step; drags coalesce. Missing maps show the retained fill
        /// value with a reason. Setting a gradient removes that channel's image; undo restores it. Type, locks and working budget are
        /// checked before mutation. The normal channel is refused.</summary>
        public void SetFillGradient(Guid id, PaintChannel channel, GeneratorSettings settings, bool coalesce = false)
        {
            EnsureNoStroke(); PaintLayer.ValidateChannel(channel); var layer = GetLayer(id);
            if (layer.Kind != LayerKind.Fill) throw new InvalidOperationException("Only fill layers have a fill gradient.");
            if (settings != null)
            {
                ValidateFillGradient(channel, settings);
                if (BlockWorkingBytes(layer.ActiveChain(channel), filterBlockPixels) > filterWorkingBudget) throw new InvalidOperationException("The fill gradient exceeds the filter working budget. Nothing was changed.");
            }
            layer.FillGradients.TryGetValue(channel, out var old); if (Equals(old, settings)) return;
            RefuseLockedPixels(layer, erase: false); RefuseLockedTransparency(layer);
            Guid? image = layer.FillImages.TryGetValue(channel, out var img) ? img : (Guid?)null;
            bool hadValue = layer.FillValues.TryGetValue(channel, out var value), enabled = layer.IsChannelEnabled(channel);
            var fallback = hadValue ? value : DefaultFillImageFallback(channel);
            long bytes = 96 + (old?.Ramp.ByteSize ?? 0) + (settings?.Ramp.ByteSize ?? 0);
            Execute(LayerScoped(layer, channel,
                () => { layer.SetFillGradientInternal(channel, settings); if (settings != null) { layer.SetFillImageInternal(channel, null); if (!hadValue) layer.SetFillValueInternal(channel, fallback); layer.Enable(channel, true); } FillChanged(layer); },
                () => { layer.SetFillGradientInternal(channel, old); layer.SetFillImageInternal(channel, image); if (!hadValue) layer.SetFillValueInternal(channel, null); layer.Enable(channel, enabled); FillChanged(layer); }, bytes),
                coalesce ? (object)("fillGradient", id, channel) : null);
        }
        internal void SetFillGradientsForLoad(PaintLayer layer, IEnumerable<KeyValuePair<PaintChannel, GeneratorSettings>> gradients)
        {
            if (layer.Kind != LayerKind.Fill) throw new InvalidOperationException("Only fill layers have gradients.");
            foreach (var g in gradients)
            {
                ValidateFillGradient(g.Key, g.Value);
                if (!layer.FillValues.ContainsKey(g.Key) || layer.HasFillImage(g.Key) || layer.HasFillGradient(g.Key)) throw new ArgumentException("Invalid or duplicate fill gradient channel.");
                if (BlockWorkingBytes(layer.ActiveChain(g.Key), filterBlockPixels) > filterWorkingBudget) throw new InvalidOperationException("The fill gradient exceeds the filter working budget.");
                layer.SetFillGradientInternal(g.Key, g.Value);
            }
            FillChanged(layer);
        }
        public GeneratorStatus GetFillGradientStatus(Guid layerId, PaintChannel channel)
        {
            var layer = GetLayer(layerId); if (!layer.FillGradients.TryGetValue(channel, out var g)) throw new ArgumentException("This channel has no fill gradient.");
            var status = GetGeneratorStatus(g);
            return layer.IsDecal ? new GeneratorStatus(status.Maps, GetDecalProblem(layerId)) : status;
        }
        public IReadOnlyList<string> InactiveFillGradients()
        {
            var notes = new List<string>();
            foreach (var layer in layers) foreach (var g in layer.FillGradients)
                if (layer.IsChannelEnabled(g.Key)) { var status = GetFillGradientStatus(layer.Id, g.Key); if (!status.Active) notes.Add("'" + layer.Name + "' (" + g.Key + "): " + (layer.IsDecal ? "the decal gradient is transparent: " : "the gradient shows its fallback value: ") + status.Reason); }
            return notes;
        }
    }
}
