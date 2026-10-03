using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>モデルのルートの軸のまわりの放射状対称。スケールに関わらずシーンの単位で回す。</summary>
    public readonly struct RadialSymmetry
    {
        public readonly Vector3 Origin, Axis;
        public readonly int Count;
        public RadialSymmetry(Vector3 origin, Vector3 axis, int count)
        {
            if (count < 2 || count > 16) throw new ArgumentOutOfRangeException(nameof(count));
            if (!Finite(origin) || !Finite(axis) || axis.sqrMagnitude < 1e-20f) throw new ArgumentException("Radial symmetry needs a finite origin and a nonzero axis.");
            Origin = origin; Axis = axis.normalized; Count = count;
        }
        static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        public Vector3 RotatePoint(Vector3 point, int copy) => Origin + Rotation(copy) * (point - Origin);
        public Vector3 RotateDirection(Vector3 direction, int copy) => Rotation(copy) * direction;
        Quaternion Rotation(int copy) => Quaternion.AngleAxis(360f * copy / Count, Axis);
        public static RadialSymmetry FromModel(Vector3 origin, Quaternion rotation, SymmetryAxis axis, int count) =>
            new RadialSymmetry(origin, rotation * (axis == SymmetryAxis.Y ? Vector3.up : axis == SymmetryAxis.Z ? Vector3.forward : Vector3.right), count);
    }

    public sealed class ExpandedSurfaceDab
    {
        public SurfaceDabResult Result { get; internal set; }
        public readonly List<SurfaceHit> Copies = new List<SurfaceHit>();
        public MirrorOutcome Outcome { get; internal set; } = MirrorOutcome.OnPlane;
    }

    /// <summary>回転 N 個と、各回転の鏡映を1ダブへ合併。予算はコピーの合計、失敗は全拒否。</summary>
    public static class SurfaceRadialSymmetry
    {
        public static ExpandedSurfaceDab Build(SurfaceGeometry geometry, SurfaceHit hit, MirrorPlane? mirror, RadialSymmetry? radial,
            bool ignoreVisibility, float radius, int width, int height, Vector3 camera, float hardness = .8f, SurfaceBrushBudget budget = null, SurfaceVisibilityCache cache = null)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            budget = budget ?? new SurfaceBrushBudget();
            var result = new ExpandedSurfaceDab { Result = geometry.BuildSurfaceDabs(hit, radius, width, height, camera, hardness, budget, cache) };
            if (result.Result.WasClipped || !string.IsNullOrEmpty(result.Result.Diagnostic) || result.Result.Pixels.Count == 0) return result;
            var positions = new List<Vector3> { hit.Position };
            int count = radial?.Count ?? 1;
            for (int i = 0; i < count; i++) for (int reflected = 0; reflected < (mirror.HasValue ? 2 : 1); reflected++)
            {
                if (i == 0 && reflected == 0) continue;
                // 鏡映を先に、回転を後に適用する。独立の軸を選べるため最大 N×2 個。
                Vector3 p = reflected == 1 ? mirror.Value.Reflect(hit.Position) : hit.Position;
                Vector3 n = reflected == 1 ? mirror.Value.ReflectDirection(hit.Normal) : hit.Normal;
                if (radial.HasValue) { p = radial.Value.RotatePoint(p, i); n = radial.Value.RotateDirection(n, i); }
                bool duplicate = false;
                foreach (var old in positions) if ((old - p).magnitude <= radius * SurfaceSymmetry.OnPlaneFraction) { duplicate = true; break; }
                if (duplicate) continue;
                positions.Add(p);
                if (!geometry.TryFindClosestPoint(p, SurfaceSymmetry.SearchDistance(radius, geometry.Bounds), n, SurfaceSymmetry.MaxClosestPointNodeVisits, out var copy, out bool exceeded))
                {
                    if (exceeded) { result.Result = result.Result.Reject("Symmetry: finding a copied point exceeded the BVH work budget."); return result; }
                    result.Outcome = MirrorOutcome.NoSurface; continue;
                }
                if (copy.MaterialSlot != hit.MaterialSlot || copy.RendererIndex != hit.RendererIndex) { result.Outcome = MirrorOutcome.OtherSlot; continue; }
                result.Copies.Add(copy);
                var used = result.Result;
                var remaining = new SurfaceBrushBudget {
                    MaxTriangles = budget.MaxTriangles - used.VisitedTriangles,
                    MaxCandidatePixels = budget.MaxCandidatePixels - used.CandidatePixels,
                    MaxVisibilityRays = budget.MaxVisibilityRays - used.VisibilityRays,
                    MaxRayTriangleTests = budget.MaxRayTriangleTests - used.RayTriangleTests,
                    MaxRayNodeVisits = budget.MaxRayNodeVisits - (int)Math.Min(int.MaxValue, used.RayNodeVisits)
                };
                var dab = geometry.BuildSurfaceDabs(copy, radius, width, height, camera, hardness, remaining, cache, ignoreVisibility);
                if (dab.WasClipped || !string.IsNullOrEmpty(dab.Diagnostic))
                { result.Result = used.Reject("Symmetry copy: " + dab.Diagnostic); return result; }
                if (dab.Pixels.Count == 0) result.Outcome = MirrorOutcome.Hidden;
                else if (result.Outcome == MirrorOutcome.OnPlane) result.Outcome = MirrorOutcome.Painted;
                result.Result = SurfaceSymmetry.Union(used, dab, width);
            }
            return result;
        }
    }
}
