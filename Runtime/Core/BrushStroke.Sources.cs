using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>画素中心の参照。Weight が 0 のタップは参照しない。</summary>
    public readonly struct BrushSourceTap
    {
        public readonly int X, Y;
        public readonly double Weight;
        public BrushSourceTap(int x, int y, double weight = 1) { X = x; Y = y; Weight = weight; }
    }
    /// <summary>面のダブの画素と、継ぎ目の向こうも含む最大 4 点の参照。正の重みの和で正規化する。</summary>
    public readonly struct BrushMappedPixel
    {
        public readonly BrushPixel Pixel;
        public readonly BrushSourceTap A, B, C, D;
        public BrushMappedPixel(BrushPixel pixel, BrushSourceTap a, BrushSourceTap b = default, BrushSourceTap c = default, BrushSourceTap d = default)
        { Pixel = pixel; A = a; B = b; C = c; D = d; }
    }

    public sealed partial class BrushStroke
    {
        Dictionary<TileCoord, CpuCompositor.CompositeJob>[] cloneSources;
        long cloneSourceBytes;
        bool effectDabStarted;
        Dictionary<int, int> mappedIndices;
        Rgba32[][] mappedColors;
        public long CloneSourceBytes => cloneSourceBytes;
        void ReleaseCloneSource() { cloneSources = null; cloneSourceBytes = 0; }

        /// <summary>クローンが読む全表示レイヤーのチャンネル合成を、最初のダブの前に凍結する。マスクは拒否。
        /// 合成の候補タイルの写しと索引をストローク予算に合算する。失敗はストローク全体を取り消す。</summary>
        public void UseCompositeCloneSource()
        {
            CheckOpen();
            try
            {
                if (settings.Effect != BrushEffect.Clone || hasSample || effectDabStarted || strokeTiles.Count != 0 || cloneSources != null)
                    throw new InvalidOperationException("Set the composite clone source before the first clone dab.");
                var channels = new PaintChannel[targets.Length];
                for (int k = 0; k < targets.Length; k++)
                {
                    bool found = false;
                    foreach (var layer in document.Layers) foreach (var channel in layer.Channels)
                        if (ReferenceEquals(channel.Value, targets[k].Surface)) { channels[k] = channel.Key; found = true; }
                    if (!found) throw new InvalidOperationException("A mask cannot sample the channel composite. Choose Current layer.");
                }
                var sources = new Dictionary<TileCoord, CpuCompositor.CompositeJob>[targets.Length];
                cloneSources = sources;
                for (int k = 0; k < sources.Length; k++)
                {
                    var coords = new HashSet<TileCoord>();
                    foreach (var layer in document.Layers)
                        if (layer.Visible && layer.IsChannelEnabled(channels[k]))
                            foreach (var coord in layer.EnumerateContentTiles(channels[k]))
                                if (coords.Add(coord)) document.EnsureStrokeBudget(checked(rollbackBytes + symmetryScratchBytes + cloneSourceBytes + (long)coords.Count * 64));
                    var sorted = new List<TileCoord>(coords); sorted.Sort();
                    long bytes = 0;
                    foreach (var coord in sorted)
                    {
                        int w = Math.Min(tileSize, width - coord.X * tileSize), h = Math.Min(tileSize, height - coord.Y * tileSize);
                        bytes = checked(bytes + 64 + (long)w * h * 4);
                    }
                    document.EnsureStrokeBudget(checked(rollbackBytes + symmetryScratchBytes + cloneSourceBytes + bytes)); cloneSourceBytes += bytes;
                    var jobs = new List<CpuCompositor.CompositeJob>(); var tiles = sources[k] = new Dictionary<TileCoord, CpuCompositor.CompositeJob>();
                    foreach (var coord in sorted)
                    {
                        int x = coord.X * tileSize, y = coord.Y * tileSize, w = Math.Min(tileSize, width - x), h = Math.Min(tileSize, height - y);
                        var job = new CpuCompositor.CompositeJob(x, y, w, h, new byte[w * h * 4]); jobs.Add(job); tiles.Add(coord, job);
                    }
                    CpuCompositor.CompositeRegions(document, channels[k], jobs);
                }
            }
            catch { Cancel(); throw; }
        }
        Rgba32 ReadCloneComposite(int k, int x, int y)
        {
            if (!cloneSources[k].TryGetValue(new TileCoord(x / tileSize, y / tileSize), out var tile)) return Rgba32.Transparent;
            int i = ((y - tile.Y) * tile.Width + x - tile.X) * 4; var p = tile.Pixels;
            return new Rgba32(p[i], p[i + 1], p[i + 2], p[i + 3]);
        }
        Rgba32 ReadEffectSource(int k, int x, int y)
        {
            if (cloneSources != null) return ReadCloneComposite(k, x, y);
            var target = targets[k]; var coord = new TileCoord(x / tileSize, y / tileSize);
            var tile = settings.Effect == BrushEffect.Clone && target.Before.TryGetValue(coord, out var before) ? before : target.Surface.PeekTile(coord);
            return tile == null ? Rgba32.Transparent : tile.Get(((y % tileSize) * tileSize + x % tileSize) * 4);
        }
        Rgba32 SampleMapped(int k, BrushMappedPixel p)
        {
            double r = 0, g = 0, b = 0, alpha = 0, weight = 0; int count = 0; Rgba32 single = default;
            void Add(BrushSourceTap tap)
            {
                if (tap.Weight == 0) return;
                var c = ReadEffectSource(k, tap.X, tap.Y); count++; single = c;
                double a = c.A * tap.Weight; r += c.R * a; g += c.G * a; b += c.B * a; alpha += a; weight += tap.Weight;
            }
            Add(p.A); Add(p.B); Add(p.C); Add(p.D);
            if (count == 1) return single;
            return alpha <= 0 ? Rgba32.Transparent : new Rgba32(Byte255(r / alpha), Byte255(g / alpha), Byte255(b / alpha), Byte255(alpha / weight));
        }
        /// <summary>クローン/指先の面の参照を全チャンネルで凍結してから 1 ダブ適用する。同じ画素の重複は拒否。
        /// 指先の最初の拾いと面の方向は呼び手が管理する。選択量は描き込みだけに掛ける。</summary>
        public bool ApplyMappedDab(IReadOnlyList<BrushMappedPixel> pixels, double pressure = 1, long samplingBytes = 0, IReadOnlyList<StencilPoint> stencilPoints = null)
        {
            CheckOpen();
            try
            {
                if (settings.Effect != BrushEffect.Clone && settings.Effect != BrushEffect.Smudge) throw new InvalidOperationException("Mapped dabs need Clone or Smudge.");
                if (pixels == null) throw new ArgumentNullException(nameof(pixels));
                MathUtil.RequireFinite(pressure, nameof(pressure)); if (pressure < 0 || pressure > 1) throw new ArgumentOutOfRangeException(nameof(pressure));
                if (stencilPoints != null && stencilPoints.Count != pixels.Count) throw new ArgumentException("Stencil points must match the dab pixels.", nameof(stencilPoints));
                ReleaseEffectDab(); effectHasPosition = true; effectDabStarted = true;
                // 呼び手の参照計画も含む名目サイズ。CLR の実際のヒープ上限ではない。
                if (samplingBytes < 0) throw new ArgumentOutOfRangeException(nameof(samplingBytes));
                long bytes = checked((long)pixels.Count * (160 + targets.Length * 4 + (stencilPoints == null ? 0 : 24)) + samplingBytes);
                document.EnsureStrokeBudget(checked(rollbackBytes + symmetryScratchBytes + cloneSourceBytes + bytes)); effectScratchBytes = bytes;
                mappedIndices = new Dictionary<int, int>(); mappedColors = new Rgba32[targets.Length][];
                for (int k = 0; k < targets.Length; k++) mappedColors[k] = new Rgba32[pixels.Count];
                for (int i = 0; i < pixels.Count; i++)
                {
                    var p = pixels[i]; var dest = p.Pixel;
                    MathUtil.RequireFinite(dest.Coverage, nameof(dest.Coverage));
                    if (dest.X < 0 || dest.X >= width || dest.Y < 0 || dest.Y >= height || dest.Coverage < 0 || dest.Coverage > 1) throw new ArgumentOutOfRangeException(nameof(pixels));
                    void Check(BrushSourceTap tap)
                    {
                        MathUtil.RequireFinite(tap.Weight, nameof(tap.Weight));
                        if (tap.Weight < 0 || tap.Weight > 1 || (tap.Weight > 0 && (tap.X < 0 || tap.X >= width || tap.Y < 0 || tap.Y >= height))) throw new ArgumentOutOfRangeException(nameof(pixels));
                    }
                    Check(p.A); Check(p.B); Check(p.C); Check(p.D);
                    if (p.A.Weight + p.B.Weight + p.C.Weight + p.D.Weight <= 0) throw new ArgumentException("A mapped pixel needs a source.", nameof(pixels));
                    mappedIndices.Add(dest.Y * width + dest.X, i);
                    for (int k = 0; k < targets.Length; k++) mappedColors[k][i] = SampleMapped(k, p);
                }
                bool changed = false; BeginPass();
                try
                {
                    for (int i = 0; i < pixels.Count; i++)
                    {
                        var d = pixels[i].Pixel;
                        changed |= ApplyPixelAt(cursor, true, d.X / tileSize, d.Y / tileSize, (d.Y % tileSize) * tileSize + d.X % tileSize, d.Coverage, pressure, 1, 1, stencilPoints == null ? (StencilPoint?)null : stencilPoints[i]);
                    }
                }
                finally { EndPass(); ReleaseEffectDab(); }
                if (changed) document.PixelsChanged(); return changed;
            }
            catch { Cancel(); throw; }
        }
    }
}
