using System;
using System.Collections.Generic;
using UnityEngine;
using Yozolab.YoluPainter.Core.Paths;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>
    /// 3D のパスを別のメッシュに付け直す（モデルの差し替え。Substance Painter の「ストロークの位置をメッシュの上に保つ」に当たる）: 制御点を前の
    /// スナップショットの三角形と重心座標から 3D の位置に戻し、新しいスナップショットのいちばん近い面の点（同じ向きの面、テクスチャセットの
    /// マテリアルの組の三角形だけ、許す距離の内側）に置き直す。1 点でも置けなければ付け直さない（呼ぶ側が画素にして知らせる）。
    /// 位置は両方のスナップショットの空間（ルートの位置を引いたワールド）で比べるので、ルートの置き方が同じモデルどうしを前提にする。
    /// </summary>
    public static class SurfacePathRebind
    {
        /// <summary>許す距離: 前のモデルの境界箱の対角線のこの割合か、パスの筆の半径の大きい方。</summary>
        public const float ToleranceFraction = 0.01f;
        /// <summary>1 点の近い点の探索で調べる BVH の節の数の上限。</summary>
        public const int MaxNodeVisits = 200000;

        /// <summary>許す距離（前のモデルの大きさと筆の半径から）。</summary>
        public static float Tolerance(SurfaceGeometry from, SurfacePath path)
            => Mathf.Max((float)path.Brush.RadiusWorld, from.Bounds.size.magnitude * ToleranceFraction);

        /// <summary>
        /// path（from に結び付いたもの）を to の material の組の三角形に置き直す。置けたら true と新しい指紋のパス（ID・筆・チャンネルはそのまま）。
        /// 置けなければ false と理由（どの点が、どれだけ離れていたか）。
        /// </summary>
        public static bool TryRebind(SurfacePath path, SurfaceGeometry from, SurfaceGeometry to, int material, out SurfacePath rebound, out string reason)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (from == null) throw new ArgumentNullException(nameof(from));
            if (to == null) throw new ArgumentNullException(nameof(to));
            rebound = null; reason = null;
            if (path.ModelFingerprint != SurfacePathRenderer.Fingerprint(from)) { reason = L.Tr("the path was drawn on another model"); return false; }
            if (material < 0) { reason = L.Tr("its material is not in the new model"); return false; }
            float tolerance = Tolerance(from, path);
            var points = new List<PathPoint>(path.Points.Count);
            for (int i = 0; i < path.Points.Count; i++)
            {
                var p = path.Points[i];
                if (p.Triangle >= from.TriangleCount) { reason = L.Tr("point {0} refers to a triangle the model does not have", i + 1); return false; }
                var position = SurfacePathRenderer.Position(from, p, out var normal);
                if (!to.TryFindClosestPoint(position, tolerance, normal, MaxNodeVisits, out var hit, out bool exceeded, material))
                {
                    reason = exceeded ? L.Tr("finding point {0} on the new mesh took too long", i + 1)
                        : L.Tr("point {0} has no surface of its material within {1} on the new mesh", i + 1, tolerance.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
                    return false;
                }
                points.Add(SurfacePathRenderer.PointOf(hit, p.Pressure));
            }
            rebound = new SurfacePath(path.Id, path.Channel, SurfacePathRenderer.Fingerprint(to), path.Brush, points);
            return true;
        }
    }
}
