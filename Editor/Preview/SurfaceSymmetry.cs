using System;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>対称の面に直交する、モデルのローカルの軸（X なら面は X = 中心）。</summary>
    public enum SymmetryAxis { X, Y, Z }

    /// <summary>
    /// プレビューの空間の対称の面: 面の上の点と単位法線、面に沿う 2 本の軸（表示の四角に使う）。モデルのルートのローカルの軸に
    /// 直交する面を、ルートの位置から法線の向きに offset（プレビューの空間の長さ。シーンの単位）ずらして置く（<see cref="FromModel"/>）。
    /// ルートの大きさ（スケール）は面の向きを変えないので使わない（対角のスケールは軸ごとの鏡映と入れ替えられる）。
    /// </summary>
    public readonly struct MirrorPlane : IEquatable<MirrorPlane>
    {
        public readonly Vector3 Point, Normal, AxisU, AxisV;
        public MirrorPlane(Vector3 point, Vector3 normal, Vector3 axisU, Vector3 axisV)
        {
            if (normal.sqrMagnitude < 1e-20f) throw new ArgumentException("The mirror plane needs a nonzero normal.", nameof(normal));
            Point = point; Normal = normal.normalized; AxisU = axisU; AxisV = axisV;
        }
        public static MirrorPlane FromModel(Vector3 rootPosition, Quaternion rootRotation, SymmetryAxis axis, float offset)
        {
            Vector3 n, u, v;
            switch (axis)
            {
                case SymmetryAxis.Y: n = Vector3.up; u = Vector3.forward; v = Vector3.right; break;
                case SymmetryAxis.Z: n = Vector3.forward; u = Vector3.right; v = Vector3.up; break;
                default: n = Vector3.right; u = Vector3.up; v = Vector3.forward; break;
            }
            n = rootRotation * n; u = rootRotation * u; v = rootRotation * v;
            return new MirrorPlane(rootPosition + n * offset, n, u, v);
        }
        /// <summary>面からの符号つきの距離（法線の側が正）。</summary>
        public float SignedDistance(Vector3 p) => Vector3.Dot(p - Point, Normal);
        public Vector3 Reflect(Vector3 p) => p - 2 * SignedDistance(p) * Normal;
        public Vector3 ReflectDirection(Vector3 d) => d - 2 * Vector3.Dot(d, Normal) * Normal;
        public bool Equals(MirrorPlane other) => Point == other.Point && Normal == other.Normal && AxisU == other.AxisU && AxisV == other.AxisV;
        public override bool Equals(object obj) => obj is MirrorPlane other && Equals(other);
        public override int GetHashCode() { unchecked { return Point.GetHashCode() * 397 ^ Normal.GetHashCode(); } }
    }

    /// <summary>映した側のダブがどうなったか。</summary>
    public enum MirrorOutcome
    {
        /// <summary>映した側も塗った（覆いは元の側と画素ごとの大きいほうで合わせた）。</summary>
        Painted,
        /// <summary>ダブの中心が対称の面の上にあり、映しても同じダブなので元の側だけ。</summary>
        OnPlane,
        /// <summary>映した点から届く距離に、向きの合う面が無い（モデルがそこで左右対称でない）。</summary>
        NoSurface,
        /// <summary>映した点のいちばん近い面が、今のテクスチャセットとは別のスロット。</summary>
        OtherSlot,
        /// <summary>可視性を無視しない設定で、映した側の画素がカメラから見えない。</summary>
        Hidden,
    }

    /// <summary>対称のダブ: 塗る画素（<see cref="Result"/>）と、元の側・映した側それぞれの結果。</summary>
    public sealed class SymmetricSurfaceDab
    {
        /// <summary>塗る画素（元の側と映した側を画素ごとに大きいほうの覆いで合わせ、左下からの行の順）。予算を超えたら空で WasClipped。</summary>
        public SurfaceDabResult Result { get; internal set; }
        public SurfaceDabResult Original { get; internal set; }
        /// <summary>映した側のダブ（作らなかったら null）。</summary>
        public SurfaceDabResult Mirror { get; internal set; }
        /// <summary>映した点を面に落とした当たり（<see cref="HasMirrorHit"/> のときだけ意味がある）。</summary>
        public SurfaceHit MirrorHit { get; internal set; }
        public bool HasMirrorHit { get; internal set; }
        public MirrorOutcome Outcome { get; internal set; }
    }

    /// <summary>
    /// 3D ビューのシンメトリー（Substance Painter の Symmetry のミラーと同じ考え方）: ダブの中心（面の上の点）を対称の面で映し、その点に
    /// いちばん近い、向きの合う面の上の点（<see cref="SurfaceGeometry.TryFindClosestPoint"/>）に、同じ半径・硬さでもう 1 つダブを作る。
    /// 映した側もスロット・連結・UV の継ぎ目と同じ予算を守る。ignoreVisibility が有効ならカメラから見えない対称先にも塗り、
    /// 元の側はいつも可視性を確かめる。2 つは画素ごとに大きいほうの覆いで 1 つにする（対称の面の近くで重なっても二重に塗らない。1 つのダブの中で
    /// 三角形が重なったときと同じ）。
    /// </summary>
    public static class SurfaceSymmetry
    {
        /// <summary>映した点から面を探す距離の上限: ブラシの半径（それより遠い面には映したブラシの球が届かない）。ただし小さい筆でも、
        /// 書き出しの丸めほどの左右のずれ（モデルの対角線の 1/1000）は許す。</summary>
        public static float SearchDistance(float radiusWorld, Bounds modelBounds) => Mathf.Max(radiusWorld, modelBounds.size.magnitude * 1e-3f);
        /// <summary>中心がこれより面に近ければ（半径に対する割合）、映しても同じダブとみなす。</summary>
        public const float OnPlaneFraction = 1e-4f;
        /// <summary>映した点を探す問い合わせで BVH の節点を見る数の上限（超えたらダブを断り、ストロークを取り消す）。</summary>
        public const int MaxClosestPointNodeVisits = 1 << 20;

        /// <summary>元のダブと、映した側のダブを作って合わせる。元の側が断られた・作れなかったら、映した側は作らない。</summary>
        public static SymmetricSurfaceDab Build(SurfaceGeometry geometry, SurfaceHit hit, MirrorPlane plane, float radiusWorld, int width, int height,
            Vector3 cameraPosition, float hardness = 0.8f, SurfaceBrushBudget budget = null, SurfaceVisibilityCache cache = null, bool ignoreVisibility = false)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            budget = budget ?? new SurfaceBrushBudget();
            var original = geometry.BuildSurfaceDabs(hit, radiusWorld, width, height, cameraPosition, hardness, budget, cache);
            var dab = new SymmetricSurfaceDab { Original = original, Result = original, Outcome = MirrorOutcome.OnPlane };
            if (original.WasClipped || !string.IsNullOrEmpty(original.Diagnostic)) return dab;

            var mirrored = plane.Reflect(hit.Position);
            if ((mirrored - hit.Position).magnitude <= radiusWorld * OnPlaneFraction) return dab;
            if (!geometry.TryFindClosestPoint(mirrored, SearchDistance(radiusWorld, geometry.Bounds), plane.ReflectDirection(hit.Normal), MaxClosestPointNodeVisits, out var mirrorHit, out bool exceeded))
            {
                if (exceeded)
                {
                    dab.Result = new SurfaceDabResult().Reject("Symmetry: finding the mirrored point exceeded the BVH work budget. No pixels were changed; simplify overlapping geometry or turn symmetry off.");
                    return dab;
                }
                dab.Outcome = MirrorOutcome.NoSurface; return dab;
            }
            dab.MirrorHit = mirrorHit; dab.HasMirrorHit = true;
            if (mirrorHit.MaterialSlot != hit.MaterialSlot || mirrorHit.RendererIndex != hit.RendererIndex) { dab.Outcome = MirrorOutcome.OtherSlot; return dab; }

            var mirror = geometry.BuildSurfaceDabs(mirrorHit, radiusWorld, width, height, cameraPosition, hardness, budget, cache, ignoreVisibility);
            dab.Mirror = mirror;
            if (mirror.WasClipped) { dab.Result = new SurfaceDabResult().Reject("Mirrored side: " + mirror.Diagnostic); return dab; }
            dab.Outcome = mirror.Pixels.Count == 0 && original.Pixels.Count > 0 ? MirrorOutcome.Hidden : MirrorOutcome.Painted;
            dab.Result = Union(original, mirror, width);
            return dab;
        }

        /// <summary>2 つのダブを画素ごとに大きいほうの覆いで合わせる（どちらも左下からの行の順。結果も同じ順で、同じ画素は 1 つ）。</summary>
        internal static SurfaceDabResult Union(SurfaceDabResult a, SurfaceDabResult b, int width)
        {
            var result = new SurfaceDabResult
            {
                CandidatePixels = a.CandidatePixels + b.CandidatePixels, VisibilityRays = a.VisibilityRays + b.VisibilityRays,
                RayTriangleTests = a.RayTriangleTests + b.RayTriangleTests, VisitedTriangles = a.VisitedTriangles + b.VisitedTriangles, RayNodeVisits = a.RayNodeVisits + b.RayNodeVisits,
                Diagnostic = !string.IsNullOrEmpty(a.Diagnostic) ? a.Diagnostic : b.Diagnostic
            };
            var pa = a.Pixels; var pb = b.Pixels; int i = 0, j = 0;
            result.Pixels.Capacity = pa.Count + pb.Count;
            while (i < pa.Count || j < pb.Count)
            {
                long ka = i < pa.Count ? (long)pa[i].Y * width + pa[i].X : long.MaxValue, kb = j < pb.Count ? (long)pb[j].Y * width + pb[j].X : long.MaxValue;
                if (ka < kb) result.Pixels.Add(pa[i++]);
                else if (kb < ka) result.Pixels.Add(pb[j++]);
                else { var p = pa[i++]; float c = Mathf.Max(p.Coverage, pb[j++].Coverage); result.Pixels.Add(new SurfacePixel(p.X, p.Y, c)); }
            }
            return result;
        }
    }
}
