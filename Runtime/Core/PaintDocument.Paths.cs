using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core.Paths;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>編集できるパスを持つ層。層の対象チャンネルの画素はパスから描いた結果で、パスと画素はいつも一緒に変わる（1 回の Undo）。
    /// 3D のパスを描くのは呼ぶ側（モデルの面が要るのでエディタ）で、ここは描いた結果の面とパスを受け取って入れ替える。2D のパスは
    /// <see cref="SetCanvasPath"/> がここで描く。</summary>
    public sealed partial class PaintDocument
    {
        /// <summary>層にパスを付ける（または差し替える）。層の対象チャンネルの画素を rendered（同じ大きさの面）にそっくり入れ替える。
        /// 予算はタイルごとに確かめ、超えたら何も変えない。他のチャンネルとマスクは変えない。</summary>
        public void SetPath(Guid layerId, EditablePath path, SparseTileSurface rendered)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (path.Paints.Count != 1) throw new ArgumentException("Supply every material path channel.", nameof(rendered));
            SetPath(layerId, path, new Dictionary<PaintChannel, SparseTileSurface> { { path.Paints[0].Channel, rendered } });
        }

        /// <summary>組全体の描画結果とパスを一回の Undo で入れ替える。組から外した旧パスのチャンネルは空にする。
        /// その他のチャンネルとマスクは保つ。復元も全チャンネルの伸びを先に検証し、予算拒否で途中まで戻さない。</summary>
        public void SetPath(Guid layerId, EditablePath path, IReadOnlyDictionary<PaintChannel, SparseTileSurface> rendered)
        {
            EnsureNoStroke();
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (rendered == null) throw new ArgumentNullException(nameof(rendered));
            var layer = GetLayer(layerId);
            ValidatePathTarget(layer, path);
            var paints = path.Paints;
            if (rendered.Count != paints.Count) throw new ArgumentException("Supply exactly the path's channels.", nameof(rendered));
            var channels = new SortedSet<PaintChannel>(); var off = new List<PaintChannel>();
            var previousChannels = new HashSet<PaintChannel>();
            if (layer.Path != null) foreach (var m in layer.Path.Paints) previousChannels.Add(m.Channel);
            foreach (var m in paints)
            {
                if (!rendered.TryGetValue(m.Channel, out var surface) || surface == null || surface.Width != Width || surface.Height != Height || surface.TileSize != TileSize)
                    throw new ArgumentException("Every rendered surface must match the document and the path's channels.", nameof(rendered));
                channels.Add(m.Channel);
                if (!layer.IsChannelEnabled(m.Channel) && !previousChannels.Contains(m.Channel)) off.Add(m.Channel);
            }
            if (layer.Path != null) foreach (var m in layer.Path.Paints) channels.Add(m.Channel);
            var enabling = off.Count == 0 ? null : EnableForStroke(layer, off);
            var parts = new List<TileStrokeCommand>(); long rollback = 0;
            try
            {
                enabling?.Apply();
                foreach (var channel in channels)
                {
                    var surface = layer.GetChannel(channel);
                    rendered.TryGetValue(channel, out var replacement);
                    var coords = new SortedSet<TileCoord>(surface.EnumerateTileCoordinates());
                    if (replacement != null) foreach (var c in replacement.EnumerateTileCoordinates()) coords.Add(c);
                    var changes = new List<TileChange>();
                    // 今のチャンネルの途中失敗も外側の catch が戻す。
                    parts.Add(new TileStrokeCommand(surface, changes, Guid.NewGuid()));
                    foreach (var coord in coords)
                    {
                        var before = surface.Capture(coord); var after = replacement?.Capture(coord);
                        if (TileStorage.Same(before, after)) continue;
                        rollback += 64 + (before == null ? 0 : before.ByteSize); EnsureStrokeBudget(rollback);
                        surface.EnsureGrowth((after == null ? 0 : after.ByteSize) - surface.TileBytesAt(coord));
                        surface.Restore(coord, after); changes.Add(new TileChange(coord, before, after));
                    }
                    parts[parts.Count - 1] = new TileStrokeCommand(surface, changes, Guid.NewGuid());
                }
            }
            catch
            {
                for (int i = parts.Count - 1; i >= 0; i--) parts[i].RestoreUnchecked(true);
                enabling?.Revert(); throw;
            }
            var old = layer.Path;
            var state = new DelegateCommand(() => layer.Path = path, () => layer.Path = old,
                64 + path.PointCount * 32L + (path.Material?.Count ?? 0) * 8L);
            state.Apply(); Revision++;
            Push(new MaterialRegionCommand(this, parts, enabling, state));
        }

        /// <summary>描画済みパスの新しい層を作る。作成・有効化・画素を一回の Undo にまとめ、拒否なら層も作らない。</summary>
        public PaintLayer AddPathLayer(string name, EditablePath path, IReadOnlyDictionary<PaintChannel, SparseTileSurface> rendered, Guid? afterLayer = null)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (rendered == null) throw new ArgumentNullException(nameof(rendered));
            long bytes = 0;
            foreach (var surface in rendered.Values)
            {
                if (surface == null) throw new ArgumentException("A rendered surface is missing.", nameof(rendered));
                bytes = checked(bytes + surface.AllocatedBytes);
            }
            EnsureSourceGrowth(bytes);
            PaintLayer created = null;
            Batch(() =>
            {
                created = AddLayer(name, null, afterLayer);
                if (path.Material == null && !created.IsChannelEnabled(path.Channel)) SetChannelEnabled(created.Id, path.Channel, true);
                SetPath(created.Id, path, rendered);
            });
            // 新しい層を復元する前に組全体を予約する。層を戻してから画素予算で失敗することを防ぐ。
            if (undo.Count > 0)
            {
                var command = undo[undo.Count - 1];
                undo[undo.Count - 1] = new DelegateCommand(() => { EnsureSourceGrowth(bytes); command.Apply(); }, command.Revert, command.ByteCost);
            }
            return created;
        }

        void ValidatePathTarget(PaintLayer layer, EditablePath path)
        {
            if (layer.Kind != LayerKind.Raster) throw new InvalidOperationException("Only paint layers can be drawn by a path.");
            if (path.Material == null && !layer.IsChannelEnabled(path.Channel)) throw new InvalidOperationException("The path's channel is not enabled on the layer.");
            if (layer.Path != null && layer.Path.Channel != path.Channel) throw new InvalidOperationException("A layer's path keeps its channel.");
            if (layer.Path != null && layer.Path.GetType() != path.GetType()) throw new InvalidOperationException("A layer keeps the kind of its path (on the model or on the canvas).");
            RefuseLockedPath(layer);
        }

        /// <summary>2D のパスの組を描いて層に付ける。選択に依存しない再描画と一括 Undo。</summary>
        public void SetCanvasPath(Guid layerId, CanvasPath path)
        {
            EnsureNoStroke();
            if (path == null) throw new ArgumentNullException(nameof(path));
            ValidatePathTarget(GetLayer(layerId), path);
            SetPath(layerId, path, CanvasPathRenderer.RenderChannels(this, path));
        }

        /// <summary>パスを外し、今の画素だけを残す（その後は普通に塗れる）。1 回の Undo。</summary>
        public void Rasterize(Guid layerId)
        {
            EnsureNoStroke();
            var layer = GetLayer(layerId);
            if (layer.Path == null) return;
            RefuseLockedAttributes(layer);
            var old = layer.Path;
            Execute(new DelegateCommand(() => layer.Path = null, () => layer.Path = old, 64));
        }

        /// <summary>パスは層の画素を描き直す: 画像のロック・すべてのロックで断り、透明部分のロックでも断る（描き直すとアルファが変わる）。</summary>
        void RefuseLockedPath(PaintLayer layer) { RefuseLockedPixels(layer, erase: false); RefuseLockedTransparency(layer); }

        /// <summary>パスで描かれた層は、手で塗る・塗りつぶす・変形すると次の描き直しで消えるので断る。</summary>
        internal static void RefusePathLayer(PaintLayer layer)
        {
            if (layer.Path != null) throw new InvalidOperationException("This layer is drawn by a path.");
        }
    }
}
