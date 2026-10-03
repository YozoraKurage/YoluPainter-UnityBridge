using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    public sealed partial class PaintDocument
    {
        /// <summary>マテリアルの各チャンネルを同じ範囲に塗る。画素は各値を単独で Fill したものとバイト一致する。
        /// 無効のチャンネルの有効化も含めて一回の Undo。消去は組の全部。範囲・ロック・型は変更前に検証し、
        /// 巻き戻し予算は全チャンネルの合計で守る。失敗・変更なしなら有効化も戻す。</summary>
        public bool FillMaterial(Guid layerId, IReadOnlyList<ChannelPaint> channels, double opacity = 1, SelectionMask region = null, bool erase = false)
        {
            MathUtil.RequireFinite(opacity, nameof(opacity));
            if (opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
            return EditMaterialRegion(layerId, channels, region, erase, m =>
            {
                var rule = FillRule(m.Value, opacity, erase, KeepsAlpha(GetLayer(layerId)));
                return (x, y, start, amount) => rule(start, amount);
            });
        }

        /// <summary>各チャンネルの値から透明へのグラデーション。gradient の形・端点・不透明度を使い、From/To は
        /// チャンネルの値/透明に置き換える。データや法線を共通の RGB に近付けず、組を共通の濃度で薄くする。
        /// 単チャンネル Gradient と同じ補間・丸め。履歴・予算・有効化は FillMaterial と同じ。</summary>
        public bool GradientMaterial(Guid layerId, IReadOnlyList<ChannelPaint> channels, GradientSettings gradient, SelectionMask region = null, bool erase = false)
        {
            if (gradient == null) throw new ArgumentNullException(nameof(gradient));
            gradient.Validate();
            var shape = CopyGradient(gradient, gradient.From, gradient.To);
            return EditMaterialRegion(layerId, channels, region, erase,
                m => GradientRule(CopyGradient(shape, m.Value, Rgba32.Transparent), erase, KeepsAlpha(GetLayer(layerId))));
        }

        /// <summary>同じチャンネルの組の始点値から終点値へ補間する。RGBA の式・量子化は単チャンネル Gradient と共通。
        /// 組が違う・重複・未知の型なら変更前に拒否する。法線も encoded RGBA の補間（球面補間ではない）。</summary>
        public bool GradientMaterial(Guid layerId, IReadOnlyList<ChannelPaint> channels, IReadOnlyList<ChannelPaint> toChannels,
            GradientSettings gradient, SelectionMask region = null, bool erase = false)
        {
            if (gradient == null) throw new ArgumentNullException(nameof(gradient));
            gradient.Validate();
            if (channels == null) throw new ArgumentNullException(nameof(channels));
            if (toChannels == null) throw new ArgumentNullException(nameof(toChannels));
            var ends = new Dictionary<PaintChannel, Rgba32>();
            foreach (var m in toChannels)
            {
                PaintLayer.ValidateChannel(m.Channel);
                if (ends.ContainsKey(m.Channel)) throw new ArgumentException("The endpoint material repeats a channel.", nameof(toChannels));
                ends.Add(m.Channel, m.Value);
            }
            if (ends.Count != channels.Count) throw new ArgumentException("Both materials must paint the same channels.", nameof(toChannels));
            foreach (var m in channels)
                if (!ends.ContainsKey(m.Channel)) throw new ArgumentException("Both materials must paint the same channels.", nameof(toChannels));
            var shape = CopyGradient(gradient, gradient.From, gradient.To);
            return EditMaterialRegion(layerId, channels, region, erase,
                m => GradientRule(CopyGradient(shape, m.Value, ends[m.Channel]), erase, KeepsAlpha(GetLayer(layerId))));
        }

        /// <summary>マスクだけにグラデーションのアルファ×不透明度で隠す/見せる量を当てる。FillMask と同じ向き。
        /// RGB は使わない。画像/透明部分のロックはマスクを妨げず、すべてのロックは拒否する。</summary>
        public bool GradientMask(Guid layerId, GradientSettings gradient, SelectionMask region = null, bool reveal = false)
        {
            if (gradient == null) throw new ArgumentNullException(nameof(gradient));
            gradient.Validate(); EnsureNoStroke();
            var mask = RequireMask(layerId, out var owner); RefuseLockedAttributes(owner);
            var g = CopyGradient(gradient, gradient.From, gradient.To);
            var rule = MaskFillRule(g.Opacity, reveal);
            return EditRegion(mask.Surface, region, (x, y, start, amount) => rule(start, amount * g.ColorAt(x + .5, y + .5).A / 255.0));
        }

        static GradientSettings CopyGradient(GradientSettings g, Rgba32 from, Rgba32 to) => new GradientSettings
        { Shape = g.Shape, X0 = g.X0, Y0 = g.Y0, X1 = g.X1, Y1 = g.Y1, Opacity = g.Opacity, From = from, To = to };

        static Func<int, int, Rgba32, double, Rgba32> GradientRule(GradientSettings g, bool erase, bool keepAlpha)
        {
            if (keepAlpha) return (x, y, start, amount) => PaintKeepingAlpha(start, g.ColorAt(x + .5, y + .5), g.Opacity * amount);
            if (!erase) return (x, y, start, amount) => CpuCompositor.BlendUnchecked(start, g.ColorAt(x + .5, y + .5), g.Opacity * amount, LayerBlendMode.Normal);
            return (x, y, start, amount) =>
            {
                byte alpha = MathUtil.ToByte(start.A / 255.0 * (1 - g.Opacity * amount * g.ColorAt(x + .5, y + .5).A / 255.0));
                return alpha == 0 ? Rgba32.Transparent : new Rgba32(start.R, start.G, start.B, alpha);
            };
        }

        bool EditMaterialRegion(Guid layerId, IReadOnlyList<ChannelPaint> channels, SelectionMask region, bool erase,
            Func<ChannelPaint, Func<int, int, Rgba32, double, Rgba32>> pixel)
        {
            EnsureNoStroke();
            if (channels == null) throw new ArgumentNullException(nameof(channels));
            if (channels.Count == 0) throw new ArgumentException("Choose at least one channel to paint.", nameof(channels));
            var material = new List<ChannelPaint>(); var seen = new HashSet<PaintChannel>();
            foreach (var m in channels)
            {
                PaintLayer.ValidateChannel(m.Channel);
                if (!seen.Add(m.Channel)) throw new ArgumentException("The " + m.Channel + " channel is listed twice.", nameof(channels));
                material.Add(m);
            }
            var layer = GetLayer(layerId);
            if (layer.Kind != LayerKind.Raster) throw new InvalidOperationException("Only paint layers have pixels to fill. Use the layer's mask for fill, adjustment and group layers.");
            RefuseLockedPixels(layer, erase); RefusePathLayer(layer);
            var effective = EffectiveRegion(region);
            var coords = new List<TileCoord>(effective != null ? effective.Tiles : EnumerateCanvasTiles());
            var off = new List<PaintChannel>(); foreach (var m in material) if (!layer.IsChannelEnabled(m.Channel)) off.Add(m.Channel);
            var enabling = off.Count == 0 ? null : EnableForStroke(layer, off);
            var parts = new List<TileStrokeCommand>(); long rollback = 0;
            try
            {
                enabling?.Apply();
                foreach (var m in material)
                {
                    var surface = layer.GetChannel(m.Channel);
                    var changes = EditRegionTiles(surface, effective, coords, pixel(m), ref rollback);
                    if (changes.Count > 0) parts.Add(new TileStrokeCommand(surface, changes, Guid.NewGuid()));
                }
            }
            catch
            {
                for (int i = parts.Count - 1; i >= 0; i--) parts[i].RestoreUnchecked(true);
                enabling?.Revert(); throw;
            }
            if (parts.Count == 0) { enabling?.Revert(); return false; }
            IHistoryCommand command = parts.Count == 1 && enabling == null ? (IHistoryCommand)parts[0] : new MaterialRegionCommand(this, parts, enabling);
            Revision++; Push(command); return true;
        }

        // Undo/Redo は有効化を含め何も変える前に全チャンネルの伸びを調べる。各部を順に Apply する CompoundCommand では不足する。
        internal sealed class MaterialRegionCommand : IHistoryCommand
        {
            readonly PaintDocument document;
            readonly List<TileStrokeCommand> parts;
            readonly IHistoryCommand enabling, state;
            public long ByteCost { get; }
            internal MaterialRegionCommand(PaintDocument document, List<TileStrokeCommand> parts, IHistoryCommand enabling, IHistoryCommand state = null)
            {
                this.document = document; this.parts = parts; this.enabling = enabling; this.state = state;
                long cost = (enabling?.ByteCost ?? 0) + (state?.ByteCost ?? 0); foreach (var p in parts) cost += p.ByteCost; ByteCost = cost;
            }
            public void Apply() => Restore(false);
            public void Revert() => Restore(true);
            void Restore(bool backwards)
            {
                long growth = 0; foreach (var p in parts) growth += p.Growth(backwards);
                document.EnsureSourceGrowth(growth);
                if (!backwards) enabling?.Apply();
                foreach (var p in parts) p.RestoreUnchecked(backwards);
                if (backwards) state?.Revert(); else state?.Apply();
                if (backwards) enabling?.Revert();
            }
        }
    }
}
