using System;
using System.Collections.Generic;
using Yozolab.YoluPainter.Core.Paths;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>編集できるパスを持つ層。層の 1 チャンネルの画素はパスから描いた結果で、パスと画素はいつも一緒に変わる（1 回の Undo）。
    /// 描くのは呼ぶ側（モデルの面が要るのでエディタ）で、ここは描いた結果の面とパスを受け取って入れ替える。</summary>
    public sealed partial class PaintDocument
    {
        /// <summary>層にパスを付ける（または差し替える）。層の path.Channel の画素を rendered（同じ大きさの面）にそっくり入れ替える。
        /// 予算はタイルごとに確かめ、超えたら何も変えない。他のチャンネルとマスクは変えない。</summary>
        public void SetPath(Guid layerId, SurfacePath path, SparseTileSurface rendered)
        {
            EnsureNoStroke();
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (rendered == null) throw new ArgumentNullException(nameof(rendered));
            var layer = GetLayer(layerId);
            if (layer.Kind != LayerKind.Raster) throw new InvalidOperationException("Only paint layers can be drawn by a path.");
            if (rendered.Width != Width || rendered.Height != Height || rendered.TileSize != TileSize) throw new ArgumentException("The rendered surface must match the document.", nameof(rendered));
            if (!layer.IsChannelEnabled(path.Channel)) throw new InvalidOperationException("Enable the path's channel on the layer first.");
            if (layer.Path != null && layer.Path.Channel != path.Channel) throw new InvalidOperationException("A layer's path keeps its channel; rasterize it before drawing another channel.");
            var surface = layer.GetChannel(path.Channel);
            var coords = new SortedSet<TileCoord>(surface.EnumerateTileCoordinates()); foreach (var c in rendered.EnumerateTileCoordinates()) coords.Add(c);
            var changes = new List<TileChange>(); long rollback = 0;
            try
            {
                foreach (var coord in coords)
                {
                    var before = surface.Capture(coord); var after = rendered.Capture(coord);
                    if (TileStorage.Same(before, after)) continue;
                    rollback += 64 + (before == null ? 0 : before.ByteSize);
                    EnsureStrokeBudget(rollback);
                    surface.EnsureGrowth((after == null ? 0 : after.ByteSize) - surface.TileBytesAt(coord));
                    surface.Restore(coord, after);
                    changes.Add(new TileChange(coord, before, after));
                }
            }
            catch
            {
                for (int i = changes.Count - 1; i >= 0; i--) surface.Restore(changes[i].Coord, changes[i].Before);
                throw;
            }
            var old = layer.Path; layer.Path = path;
            var commands = new List<IHistoryCommand>();
            if (changes.Count > 0) commands.Add(new TileStrokeCommand(surface, changes, Guid.NewGuid()));
            commands.Add(new DelegateCommand(() => layer.Path = path, () => layer.Path = old, 64 + path.Points.Count * 32));
            Revision++; Push(new CompositeCommand(commands));
        }

        /// <summary>パスを外し、今の画素だけを残す（その後は普通に塗れる）。1 回の Undo。</summary>
        public void Rasterize(Guid layerId)
        {
            EnsureNoStroke();
            var layer = GetLayer(layerId);
            if (layer.Path == null) return;
            var old = layer.Path;
            Execute(new DelegateCommand(() => layer.Path = null, () => layer.Path = old, 64));
        }

        /// <summary>パスで描かれた層は、手で塗る・塗りつぶす・変形すると次の描き直しで消えるので断る。</summary>
        internal static void RefusePathLayer(PaintLayer layer)
        {
            if (layer.Path != null) throw new InvalidOperationException("This layer is drawn by a path. Edit the path, or rasterize the layer to paint on it.");
        }
    }
}
