using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    public sealed partial class BrushStroke
    {
        /// <summary>対称の1ダブで調べる候補画素の上限。超えたらストローク全体を取消。</summary>
        public const long MaxSymmetryCandidatePixels = 4L << 20;
        long symmetryScratchBytes;

        void VisitSymmetricPixels(DabShape s, double extent, Action<int, int, double> accept)
        {
            long work = 0;
            var bounds = new List<(CanvasSymmetryTransform transform, int x0, int x1, int y0, int y1)>();
            foreach (var transform in settings.CanvasSymmetry.Transforms())
            {
                transform.Map(s.X, s.Y, out double cx, out double cy);
                int x0 = Math.Max(0, (int)Math.Ceiling(cx - extent - .5)), x1 = Math.Min(width - 1, (int)Math.Floor(cx + extent - .5));
                int y0 = Math.Max(0, (int)Math.Ceiling(cy - extent - .5)), y1 = Math.Min(height - 1, (int)Math.Floor(cy + extent - .5));
                if (x0 > x1 || y0 > y1) continue;
                work = checked(work + (long)(x1 - x0 + 1) * (y1 - y0 + 1));
                if (work > MaxSymmetryCandidatePixels) throw new InvalidOperationException("Symmetry dab exceeded the pixel work budget. Reduce the brush size or the number of copies.");
                bounds.Add((transform, x0, x1, y0, y1));
            }
            foreach (var b in bounds)
                for (int y = b.y0; y <= b.y1; y++) for (int x = b.x0; x <= b.x1; x++)
                {
                    b.transform.Inverse(x + .5, y + .5, out double ox, out double oy);
                    double coverage = SymmetryCoverage(s, ox - s.X, oy - s.Y);
                    if (coverage > 0) accept(x, y, coverage);
                }
        }
        static double SymmetryCoverage(DabShape s, double dx, double dy)
        {
            double u = (s.Cos * dx + s.Sin * dy) / s.Radius, v = (-s.Sin * dx + s.Cos * dy) / (s.Radius * s.Roundness);
            if (s.Tip != null) return s.Tip.Sample((u / s.AspectX + 1) * .5, (v / s.AspectY + 1) * .5);
            double distance = s.Plain ? Math.Sqrt(dx * dx + dy * dy) / s.Radius : Math.Sqrt(u * u + v * v);
            if (distance > 1) return 0;
            if (distance <= s.Hardness) return 1;
            double t = (1 - distance) / (1 - s.Hardness); return t * t * (3 - 2 * t);
        }
        bool SymmetricDab(DabShape s, double extent)
        {
            var pixels = new Dictionary<int, double>();
            try
            {
                VisitSymmetricPixels(s, extent, (x, y, coverage) =>
                {
                    int key = y * width + x;
                    if (pixels.TryGetValue(key, out double old)) { if (coverage > old) pixels[key] = coverage; return; }
                    // 辞書の容量と並べ替え用のキーを含む一時領域を保守的に数える。
                    long bytes = checked(256 + (long)(pixels.Count + 1) * 96);
                    document.EnsureStrokeBudget(checked(rollbackBytes + effectScratchBytes + bytes)); symmetryScratchBytes = bytes;
                    pixels.Add(key, coverage);
                });
                if (pixels.Count == 0) return false;
                var keys = new List<int>(pixels.Keys); keys.Sort();
                int x0 = width, y0 = height, x1 = -1, y1 = -1;
                foreach (int key in keys) { int x = key % width, y = key / width; x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
                if (!PrepareEffectDab(x0, x1, y0, y1, s.X, s.Y)) return false;
                bool changed = false; BeginPass();
                try
                {
                    foreach (int key in keys)
                    {
                        int x = key % width, y = key / width, tx = x / tileSize, ty = y / tileSize, local = (y % tileSize) * tileSize + x % tileSize;
                        double coverage = pixels[key], ceiling = s.OpacityScale;
                        if (dual != null) { MoveTo(cursor, tx, ty); coverage = DualBrush.Combine(dual.Mode, coverage, cursor.Dual == null ? 0 : cursor.Dual[local]); }
                        if (s.Textured) ceiling *= 1 - settings.TextureDepth * (1 - settings.Texture.SampleTiled((x + .5) / settings.TextureScale, (y + .5) / settings.TextureScale));
                        if (coverage > 0 && ceiling > 0) changed |= ApplyPixelAt(cursor, true, tx, ty, local, coverage, s.Pressure, ceiling, s.FlowScale);
                    }
                }
                finally { EndPass(); ReleaseEffectDab(); }
                return changed;
            }
            finally { symmetryScratchBytes = 0; }
        }
    }
}
