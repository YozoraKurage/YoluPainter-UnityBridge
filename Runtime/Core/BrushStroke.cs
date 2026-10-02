using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>A transaction for exactly one layer/channel. Dispose cancels unless committed. Brush settings are
    /// frozen at start. Input order/time and every arc-length stamp are retained; there is no final-endpoint double dab.
    /// Paint builds up per pixel during the stroke like Photoshop / CLIP STUDIO: each dab moves the pixel's stroke coverage
    /// toward the dab's ceiling (Opacity × pressure) by its flow (Flow × coverage × pressure), and the pixel is recomputed
    /// from its colour before the stroke. Overlapping dabs therefore never exceed the stroke's opacity.
    /// Dynamics (BrushDynamics.cs): per-tip colours keep a per-pixel stroke colour that each dab pulls toward its own colour by
    /// its flow; the dual brush lays its own dabs along the same path and a main dab at arc length s uses those up to s; fade and
    /// tilt multiply size / ceiling / flow. Colour and dual randomness use their own streams, so they never move the main dabs.
    /// ApplyPixel (mesh dabs) uses one colour per stroke and no dual brush, fade or tilt.
    /// With CurveInterpolation the path points (after the stabilizer) are joined by a centripetal Catmull-Rom curve, cut into
    /// pieces of about <see cref="CurvePieceLength"/> px that go through the same straight-segment spacing, dual-brush and taper
    /// steps; the segment to the newest point waits for the next point (or the commit).</summary>
    public sealed class BrushStroke : IDisposable
    {
        private readonly PaintDocument document;
        private readonly SparseTileSurface surface;
        private readonly BrushSettings settings;
        private readonly Dictionary<TileCoord, TileStorage> before = new Dictionary<TileCoord, TileStorage>();
        // Accumulated stroke coverage (0..1) per touched pixel, tile-local row-major. Freed with the stroke.
        private readonly Dictionary<TileCoord, float[]> washes = new Dictionary<TileCoord, float[]>();
        /// <summary>The document's selection when the stroke began (null = everything). Results are mixed back toward the
        /// pixel before the stroke by the selected amount, so a half-selected pixel never changes more than halfway.</summary>
        private readonly SelectionMask selection;
        private bool finished, hasSample;
        private BrushSample previous;
        private double distanceSinceStamp;
        private double direction; // radians of the current input segment, for FollowDirection
        // 手ぶれ補正: 糸の先（実際に描く点）。入り抜き: ここまでの線の長さと、抜きのために待たせているダブ
        private bool hasPen; private BrushSample pen, lastInput;
        private double strokeLength;
        private struct PendingDab { public double X, Y, Pressure, Arc, Direction, TiltX, TiltY; }
        private readonly Queue<PendingDab> pending = new Queue<PendingDab>();
        private readonly Random random;
        // カラーダイナミクス: 乱数は位置のゆらぎと別の列（色を足してもダブの位置は動かない）。tipColors ではダブごとに色が変わるので、
        // 画素ごとにストロークの色（straight RGBA 0〜1）を持ち、ダブの色へ流量の割合で寄せる。
        private const int ColorStream = 0x2545F491, DualStream = 0x5DEECE6;
        private readonly Random colorRandom;
        private readonly bool tipColors;
        private readonly Rgba32 strokeColor;
        private Rgba32 dabColor;
        private readonly Dictionary<TileCoord, float[]> paints;
        // デュアルブラシ: 2 つ目の筆先のダブ（道筋の上の位置と線の長さ）と、画素ごとの最大の被覆率
        private struct DualDab { public double X, Y, Arc; }
        private readonly DualBrush dual;
        private readonly Random dualRandom;
        private readonly Queue<DualDab> dualPending;
        private readonly Dictionary<TileCoord, float[]> dualCoverage;
        private double dualSinceStamp;
        private int tipIndex; // next tip for TipSelection.Sequential
        // 曲線の補間（CurveInterpolation）: 描いた点（curveFrom）と、その前の点（curveBefore）、まだ描いていない最新の点（curveTo）
        private int curvePoints; // 0: まだ無い / 1: curveFrom だけ / 2: curveTo を待たせている
        private bool hasCurveBefore;
        private BrushSample curveBefore, curveFrom, curveTo;
        private readonly List<BrushSample> curvePieces = new List<BrushSample>();
        /// <summary>曲線の区間を刻む折れ線の 1 本の長さ（画素）。弧と弦のずれは半径 R の曲がりで 1/(8R) px 以下。</summary>
        public const double CurvePieceLength = 1;
        /// <summary>曲線の 1 区間を刻む数の上限（これを超える長い区間は刻みを粗くする。ダブの数の上限は別に確かめる）。</summary>
        public const int MaxCurvePieces = 65536;
        private long rollbackBytes;
        public Guid TransactionId { get; private set; }
        public bool IsFinished { get { return finished; } }
        public long SampleCount { get; private set; }
        public long StampCount { get; private set; }
        public int ChangedTileCount { get { return before.Count; } }
        public long RollbackBytes { get { return rollbackBytes; } }
        internal BrushStroke(PaintDocument document, SparseTileSurface surface, BrushSettings settings)
        {
            selection = document.Selection; this.document = document; this.surface = surface; this.settings = settings; TransactionId = Guid.NewGuid(); random = new Random(settings.Seed);
            strokeColor = settings.Color;
            if (settings.HasColorDynamics && !settings.Erase)
            {
                // ストロークの色を最初に 1 回引く（ダブごとでないとき、また 3D の面のブラシ（ダブを持たない）はこの色で塗る）。
                colorRandom = new Random(settings.Seed ^ ColorStream); strokeColor = ColorDynamics.Next(settings, colorRandom);
                if (settings.ColorPerTip) { tipColors = true; paints = new Dictionary<TileCoord, float[]>(); }
            }
            dabColor = strokeColor;
            if (settings.Dual != null)
            { dual = settings.Dual; dualRandom = new Random(settings.Seed ^ DualStream); dualPending = new Queue<DualDab>(); dualCoverage = new Dictionary<TileCoord, float[]>(); }
        }

        public void Add(BrushSample sample)
        {
            CheckOpen();
            try
            {
                if (hasPen && sample.Time < lastInput.Time) throw new ArgumentException("Input time must be nondecreasing.", nameof(sample));
                if (Math.Abs(sample.X) > 10000000 || Math.Abs(sample.Y) > 10000000)
                    throw new ArgumentOutOfRangeException(nameof(sample), "Sample exceeds the guarded pixel-space range.");
                lastInput = sample;
                if (settings.Stabilizer <= 0 || !hasPen) { hasPen = true; pen = sample; AddPenPoint(sample); return; }
                double dx = sample.X - pen.X, dy = sample.Y - pen.Y, d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= settings.Stabilizer) return; // 糸がたるんでいる間は筆は動かない
                double k = (d - settings.Stabilizer) / d;
                pen = new BrushSample(pen.X + dx * k, pen.Y + dy * k, sample.Pressure, sample.Time, sample.TiltX, sample.TiltY);
                AddPenPoint(pen);
            }
            catch { Cancel(); throw; }
        }

        /// <summary>A point of the pen (after the stabilizer): straight to the path, or held back until the curve to it is known.</summary>
        private void AddPenPoint(BrushSample sample)
        {
            if (!settings.CurveInterpolation) { AddPathPoint(sample); return; }
            if (curvePoints == 0) { AddPathPoint(sample); curveFrom = sample; curvePoints = 1; return; }
            if (curvePoints == 1)
            {
                // 描いた点に重なる点は、直線のときと同じく筆圧などだけを次の区間の始まりに入れる
                if (StrokeCurve.Coincident(curveFrom.X, curveFrom.Y, sample.X, sample.Y)) { AddPathPoint(sample); curveFrom = sample; }
                else { curveTo = sample; curvePoints = 2; }
                return;
            }
            // 待たせている点に重なる点は、その点の筆圧・傾き・時刻を新しくするだけ（長さ 0 の区間は作らない）
            if (StrokeCurve.Coincident(curveTo.X, curveTo.Y, sample.X, sample.Y)) { curveTo = sample; return; }
            DrawCurveSegment(sample.X, sample.Y);
            curveTo = sample;
        }
        /// <summary>Draws the held segment curveFrom → curveTo, shaped by the point before it and (nextX, nextY) after it.</summary>
        private void DrawCurveSegment(double nextX, double nextY)
        {
            BrushSample a = curveFrom, b = curveTo;
            double beforeX, beforeY;
            if (hasCurveBefore) { beforeX = curveBefore.X; beforeY = curveBefore.Y; }
            else StrokeCurve.Reflect(b.X, b.Y, a.X, a.Y, out beforeX, out beforeY);
            // 折れ線に刻む（弦の長さから数を決める）。ダブの数の上限は直線のときと同じく区間ごとに確かめる（刻んだ後では 1 本ずつは短い）。
            double chord = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            int count = (int)Math.Min(MaxCurvePieces, Math.Max(1, Math.Ceiling(chord / CurvePieceLength)));
            curvePieces.Clear();
            double length = 0, lastX = a.X, lastY = a.Y;
            for (int i = 1; i <= count; i++)
            {
                double t = i / (double)count, x, y;
                if (i == count) { x = b.X; y = b.Y; } // 端は制御点そのもの（丸めで揺らさない）
                else StrokeCurve.Point(beforeX, beforeY, a.X, a.Y, b.X, b.Y, nextX, nextY, t, out x, out y);
                length += Math.Sqrt((x - lastX) * (x - lastX) + (y - lastY) * (y - lastY)); lastX = x; lastY = y;
                curvePieces.Add(new BrushSample(x, y, a.Pressure + (b.Pressure - a.Pressure) * t, a.Time + (b.Time - a.Time) * t,
                    a.TiltX + (b.TiltX - a.TiltX) * t, a.TiltY + (b.TiltY - a.TiltY) * t));
            }
            double spacing = Math.Max(0.01, settings.Radius * 2 * settings.Spacing);
            if (length / spacing > 1000000) throw new InvalidOperationException("Input segment exceeds the one-million-stamp safety limit; split or cancel the stroke.");
            if (dual != null && length / Math.Max(0.01, dual.Radius * 2 * dual.Spacing) > 1000000)
                throw new InvalidOperationException("Input segment exceeds the one-million-stamp safety limit for the dual brush; split or cancel the stroke.");
            for (int i = 0; i < curvePieces.Count; i++) AddPathPoint(curvePieces[i], i == curvePieces.Count - 1);
            curvePieces.Clear();
            curveBefore = a; hasCurveBefore = true; curveFrom = b;
        }

        /// <summary>The path the brush actually follows (after the stabilizer): dabs at every spacing step along it. counted is
        /// false for the in-between points of a curve (SampleCount counts the points the curve passes through).</summary>
        private void AddPathPoint(BrushSample sample, bool counted = true)
        {
            if (!hasSample) { if (dual != null) dualPending.Enqueue(new DualDab { X = sample.X, Y = sample.Y, Arc = 0 }); Emit(sample.X, sample.Y, sample.Pressure, 0, sample.TiltX, sample.TiltY); hasSample = true; }
            else
            {
                double dx = sample.X - previous.X, dy = sample.Y - previous.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length > 0) direction = Math.Atan2(dy, dx);
                double spacing = Math.Max(0.01, settings.Radius * 2 * settings.Spacing);
                if (length / spacing > 1000000) throw new InvalidOperationException("Input segment exceeds the one-million-stamp safety limit; split or cancel the stroke.");
                if (length > 0 && dual != null)
                {
                    // 2 つ目の筆先のダブは自分の間隔で先に並べておき、主のダブが自分の線の長さまでのものを使う（入力の区切り方によらない）。
                    double dualSpacing = Math.Max(0.01, dual.Radius * 2 * dual.Spacing);
                    if (length / dualSpacing > 1000000) throw new InvalidOperationException("Input segment exceeds the one-million-stamp safety limit for the dual brush; split or cancel the stroke.");
                    double at = dualSpacing - dualSinceStamp;
                    while (at <= length + 1e-9)
                    {
                        double t = Math.Min(1, at / length);
                        dualPending.Enqueue(new DualDab { X = previous.X + dx * t, Y = previous.Y + dy * t, Arc = strokeLength + at });
                        at += dualSpacing;
                    }
                    dualSinceStamp = length - (at - dualSpacing);
                    if (dualSinceStamp < 1e-9) dualSinceStamp = 0;
                    if (dualSinceStamp >= dualSpacing) dualSinceStamp %= dualSpacing;
                }
                if (length > 0)
                {
                    double position = spacing - distanceSinceStamp;
                    // Tolerance only absorbs roundoff at a sample boundary; the stored accumulator is clamped below.
                    while (position <= length + 1e-9)
                    {
                        double t = Math.Min(1, position / length);
                        Emit(previous.X + dx * t, previous.Y + dy * t, previous.Pressure + (sample.Pressure - previous.Pressure) * t, strokeLength + position,
                            previous.TiltX + (sample.TiltX - previous.TiltX) * t, previous.TiltY + (sample.TiltY - previous.TiltY) * t);
                        position += spacing;
                    }
                    strokeLength += length;
                    FlushPending(strokeLength - settings.TaperOut, null);
                    distanceSinceStamp = length - (position - spacing);
                    if (distanceSinceStamp < 1e-9) distanceSinceStamp = 0;
                    if (distanceSinceStamp >= spacing) distanceSinceStamp %= spacing;
                }
            }
            previous = sample; if (counted) SampleCount++;
        }

        /// <summary>A dab at arc length arc along the path: stamped now, or held back while it is within TaperOut of the end.</summary>
        private void Emit(double x, double y, double pressure, double arc, double tiltX, double tiltY)
        {
            var dab = new PendingDab { X = x, Y = y, Pressure = pressure, Arc = arc, Direction = direction, TiltX = tiltX, TiltY = tiltY };
            if (settings.TaperOut > 0) { pending.Enqueue(dab); return; }
            Stamp(dab, Taper(arc, double.PositiveInfinity));
        }
        /// <summary>Stamps the held-back dabs up to arc length limit. With end (the final stroke length) they shrink toward it.</summary>
        private void FlushPending(double limit, double? end)
        {
            double saved = direction;
            while (pending.Count > 0 && pending.Peek().Arc <= limit)
            {
                var dab = pending.Dequeue(); direction = dab.Direction;
                Stamp(dab, Taper(dab.Arc, end ?? double.PositiveInfinity));
            }
            direction = saved;
        }
        /// <summary>Size factor of a dab at arc length arc on a stroke of length end: grows over TaperIn, shrinks over TaperOut.</summary>
        private double Taper(double arc, double end)
        {
            double f = 1;
            if (settings.TaperIn > 0) f = Math.Min(f, arc / settings.TaperIn);
            if (settings.TaperOut > 0 && !double.IsInfinity(end)) f = Math.Min(f, (end - arc) / settings.TaperOut);
            return Math.Max(0, Math.Min(1, f));
        }
        /// <summary>Ends the input: the stabilized pen is drawn on to the last input point, then held-back dabs are tapered out.</summary>
        private void FinishInput()
        {
            if (hasPen && settings.Stabilizer > 0 && (pen.X != lastInput.X || pen.Y != lastInput.Y)) { pen = lastInput; AddPenPoint(lastInput); }
            if (curvePoints == 2)
            {
                // 最後の区間: その先は無いので、終わりの点の向こうへ折り返した点で向きを決める
                StrokeCurve.Reflect(curveFrom.X, curveFrom.Y, curveTo.X, curveTo.Y, out double endX, out double endY);
                DrawCurveSegment(endX, endY); curvePoints = 1;
            }
            FlushPending(double.PositiveInfinity, strokeLength);
        }
        /// <summary>Paints a supplied geometric coverage (e.g. mesh-surface brush). Coordinates outside the canvas
        /// are clipped. Callers must union duplicate triangle/pixel coverage for each geometric dab before calling.</summary>
        public bool ApplyPixel(int x, int y, double coverage, double pressure = 1)
        {
            CheckOpen();
            try
            {
                MathUtil.RequireFinite(coverage, nameof(coverage)); MathUtil.RequireFinite(pressure, nameof(pressure));
                if (coverage < 0 || coverage > 1 || pressure < 0 || pressure > 1) throw new ArgumentOutOfRangeException("coverage/pressure");
                bool changed = ApplyPixelInternal(x, y, coverage, pressure);
                if (changed) document.PixelsChanged();
                return changed;
            }
            catch { Cancel(); throw; }
        }
        private void Stamp(PendingDab dab, double sizeFactor)
        {
            double x = dab.X, y = dab.Y, pressure = dab.Pressure;
            long index = StampCount++;
            if (dual != null) StampDual(dab.Arc);
            // フェード（ストロークの何番目の描点か）と傾きは、筆圧と掛け合わせる。どれも使わなければ 1 のまま（結果は以前と同じ）。
            double tilt = settings.TiltSize || settings.TiltOpacity || settings.TiltFlow || settings.TiltAngle ? PenTilt.Amount(dab.TiltX, dab.TiltY) : 0;
            double sizeControl = Fade(settings.FadeSize, index) * (settings.TiltSize ? 1 - tilt : 1);
            double opacityControl = Fade(settings.FadeOpacity, index) * (settings.TiltOpacity ? 1 - tilt : 1);
            double flowControl = Fade(settings.FadeFlow, index) * (settings.TiltFlow ? 1 - tilt : 1);
            double tiltTurn = settings.TiltAngle && tilt > 0 ? PenTilt.Azimuth(dab.TiltX, dab.TiltY) : 0;
            bool changed = false;
            for (int n = 0; n < settings.Count; n++)
            {
                if (tipColors) dabColor = ColorDynamics.Next(settings, colorRandom);
                double radius = settings.Radius * (settings.PressureSize ? pressure : 1) * sizeFactor;
                if (sizeControl != 1) radius *= sizeControl;
                if (settings.SizeJitter > 0) radius *= 1 - settings.SizeJitter * random.NextDouble();
                if (radius <= 0) continue;
                double cx = x, cy = y;
                if (settings.Scatter > 0)
                {
                    double reach = settings.Radius * 2 * settings.Scatter;
                    cx += (random.NextDouble() * 2 - 1) * reach; cy += (random.NextDouble() * 2 - 1) * reach;
                }
                double angle = settings.Angle * Math.PI / 180 + (settings.FollowDirection ? direction : 0);
                if (tiltTurn != 0) angle += tiltTurn;
                if (settings.AngleJitter > 0) angle += (random.NextDouble() * 2 - 1) * Math.PI * settings.AngleJitter;
                double roundness = settings.Roundness;
                if (settings.RoundnessJitter > 0) roundness = Math.Max(0.01, roundness * (1 - settings.RoundnessJitter * random.NextDouble()));
                double opacityScale = settings.OpacityJitter > 0 ? 1 - settings.OpacityJitter * random.NextDouble() : 1;
                double flowScale = settings.FlowJitter > 0 ? 1 - settings.FlowJitter * random.NextDouble() : 1;
                if (opacityControl != 1) opacityScale *= opacityControl;
                if (flowControl != 1) flowScale *= flowControl;
                BrushTip tip = settings.Tip;
                var tips = settings.Tips;
                if (tips != null && tips.Length > 0)
                    tip = settings.TipSelection == TipSelection.Sequential ? tips[tipIndex++ % tips.Length] : tips[random.Next(tips.Length)];
                changed |= Dab(cx, cy, radius, angle, roundness, pressure, opacityScale, flowScale, tip);
            }
            if (changed) document.PixelsChanged();
        }
        /// <summary>Fade factor of the stamp at index (0-based): falls linearly from 1 to 0 over length stamps; 1 when off.</summary>
        internal static double Fade(int length, long index) { return length <= 0 ? 1 : Math.Max(0, 1 - index / (double)length); }
        /// <summary>Lays the dual tip's dabs up to arc length limit into the per-pixel dual coverage (maximum).</summary>
        private void StampDual(double limit)
        {
            while (dualPending.Count > 0 && dualPending.Peek().Arc <= limit)
            {
                var d = dualPending.Dequeue();
                for (int n = 0; n < dual.Count; n++)
                {
                    double cx = d.X, cy = d.Y;
                    if (dual.Scatter > 0)
                    {
                        double reach = dual.Radius * 2 * dual.Scatter;
                        cx += (dualRandom.NextDouble() * 2 - 1) * reach; cy += (dualRandom.NextDouble() * 2 - 1) * reach;
                    }
                    DualDabAt(cx, cy);
                }
            }
        }
        private void DualDabAt(double x, double y)
        {
            double radius = dual.Radius, extent = dual.Tip == null ? radius : radius * 1.4142135623730951;
            int minX = Math.Max(0, (int)Math.Ceiling(x - extent - 0.5)), maxX = Math.Min(surface.Width - 1, (int)Math.Floor(x + extent - 0.5));
            int minY = Math.Max(0, (int)Math.Ceiling(y - extent - 0.5)), maxY = Math.Min(surface.Height - 1, (int)Math.Floor(y + extent - 0.5));
            double angle = dual.Angle * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
            double aspectX = 1, aspectY = 1;
            if (dual.Tip != null) { if (dual.Tip.Width >= dual.Tip.Height) aspectY = dual.Tip.Height / (double)dual.Tip.Width; else aspectX = dual.Tip.Width / (double)dual.Tip.Height; }
            int tile = surface.TileSize;
            for (int py = minY; py <= maxY; py++) for (int px = minX; px <= maxX; px++)
            {
                double dx = px + 0.5 - x, dy = py + 0.5 - y;
                double u = (cos * dx + sin * dy) / radius, v = (-sin * dx + cos * dy) / (radius * dual.Roundness);
                double coverage;
                if (dual.Tip == null)
                {
                    double dist = Math.Sqrt(u * u + v * v);
                    if (dist > 1) continue;
                    coverage = 1;
                    if (dist > dual.Hardness) { double t = (1 - dist) / (1 - dual.Hardness); coverage = t * t * (3 - 2 * t); }
                }
                else coverage = dual.Tip.Sample((u / aspectX + 1) * 0.5, (v / aspectY + 1) * 0.5);
                if (coverage <= 0) continue;
                TileCoord coord = surface.CoordAt(px, py);
                float[] cells;
                if (!dualCoverage.TryGetValue(coord, out cells))
                {
                    long nextBytes = rollbackBytes + 64 + (long)tile * tile * 4;
                    document.EnsureStrokeBudget(nextBytes);
                    cells = new float[tile * tile]; dualCoverage.Add(coord, cells); rollbackBytes = nextBytes;
                }
                int local = (py % tile) * tile + px % tile;
                if (coverage > cells[local]) cells[local] = (float)coverage;
            }
        }
        private double DualAt(int x, int y)
        {
            float[] cells;
            if (!dualCoverage.TryGetValue(surface.CoordAt(x, y), out cells)) return 0;
            int tile = surface.TileSize;
            return cells[(y % tile) * tile + x % tile];
        }
        /// <summary>One dab. With the round tip, no rotation and roundness 1 this is exactly the original circular dab.</summary>
        private bool Dab(double x, double y, double radius, double angle, double roundness, double pressure, double opacityScale, double flowScale, BrushTip tip)
        {
            double extent = tip == null ? radius : radius * 1.4142135623730951; // a square tip's corners reach √2·r when rotated
            int minX = Math.Max(0, (int)Math.Ceiling(x - extent - 0.5));
            int maxX = Math.Min(surface.Width - 1, (int)Math.Floor(x + extent - 0.5));
            int minY = Math.Max(0, (int)Math.Ceiling(y - extent - 0.5));
            int maxY = Math.Min(surface.Height - 1, (int)Math.Floor(y + extent - 0.5));
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double aspectX = 1, aspectY = 1;
            if (tip != null) { if (tip.Width >= tip.Height) aspectY = tip.Height / (double)tip.Width; else aspectX = tip.Width / (double)tip.Height; }
            bool textured = settings.Texture != null && settings.TextureDepth > 0;
            bool plain = angle == 0 && roundness == 1;
            bool changed = false;
            for (int py = minY; py <= maxY; py++) for (int px = minX; px <= maxX; px++)
            {
                double dx = px + 0.5 - x, dy = py + 0.5 - y;
                double u = (cos * dx + sin * dy) / radius, v = (-sin * dx + cos * dy) / (radius * roundness);
                double coverage;
                if (tip == null)
                {
                    // 回転も潰しも無いときは元の式そのもので測る（丸ブラシの結果を以前とビット単位で揃える）。
                    double d = plain ? Math.Sqrt(dx * dx + dy * dy) / radius : Math.Sqrt(u * u + v * v);
                    if (d > 1) continue;
                    coverage = 1;
                    if (d > settings.Hardness)
                    {
                        double t = (1 - d) / (1 - settings.Hardness);
                        coverage = t * t * (3 - 2 * t);
                    }
                }
                else
                {
                    coverage = tip.Sample((u / aspectX + 1) * 0.5, (v / aspectY + 1) * 0.5);
                    if (coverage <= 0) continue;
                }
                if (dual != null) { coverage = DualBrush.Combine(dual.Mode, coverage, DualAt(px, py)); if (coverage <= 0) continue; }
                double ceilingScale = opacityScale;
                if (textured)
                {
                    // 紙の質感は流量ではなく天井に効かせる（Photoshop の「描点ごとに適用」オフと同じ）。流量に効かせると、
                    // 間隔の細かいブラシでは重なったダブが溜まって質感が消えてしまう。
                    double grain = settings.Texture.SampleTiled((px + 0.5) / settings.TextureScale, (py + 0.5) / settings.TextureScale);
                    ceilingScale *= 1 - settings.TextureDepth * (1 - grain);
                    if (ceilingScale <= 0) continue;
                }
                changed |= ApplyPixelInternal(px, py, coverage, pressure, ceilingScale, flowScale);
            }
            return changed;
        }
        private bool ApplyPixelInternal(int x, int y, double coverage, double pressure, double opacityScale = 1, double flowScale = 1)
        {
            if (x < 0 || y < 0 || x >= surface.Width || y >= surface.Height) return false;
            double selected = selection == null ? 1 : selection.Coverage(x, y);
            if (selected <= 0) return false;
            double ceiling = settings.Opacity * opacityScale * (settings.PressureOpacity ? pressure : 1);
            double flow = coverage * settings.Flow * flowScale * (settings.PressureFlow ? pressure : 1);
            if (flow <= 0 || ceiling <= 0) return false;
            TileCoord coord = surface.CoordAt(x, y);
            int tile = surface.TileSize, local = (y % tile) * tile + x % tile;
            float[] wash;
            if (washes.TryGetValue(coord, out wash) && wash[local] >= ceiling && !tipColors) return false;
            if (!before.ContainsKey(coord))
            {
                long nextBytes = rollbackBytes + 64 + surface.TileBytesAt(coord) + (long)tile * tile * (tipColors ? 20 : 4);
                document.EnsureStrokeBudget(nextBytes);
                before.Add(coord, surface.Capture(coord)); rollbackBytes = nextBytes;
            }
            if (wash == null) { wash = new float[tile * tile]; washes.Add(coord, wash); if (tipColors) paints.Add(coord, new float[tile * tile * 4]); }
            double previousWash = wash[local];
            // 天井に届いた画素も、ダブごとの色ならその色へは寄せる（濃さは天井のまま）。
            double accumulated = previousWash >= ceiling ? previousWash : wash[local] + (ceiling - wash[local]) * Math.Min(1, flow);
            wash[local] = (float)accumulated;
            Rgba32 paint = strokeColor;
            if (tipColors)
            {
                float[] p = paints[coord]; int o = local * 4; double w = Math.Min(1, flow);
                Rgba32 k = dabColor;
                if (previousWash <= 0) { p[o] = k.R / 255f; p[o + 1] = k.G / 255f; p[o + 2] = k.B / 255f; p[o + 3] = k.A / 255f; }
                else
                {
                    p[o] += (float)((k.R / 255.0 - p[o]) * w); p[o + 1] += (float)((k.G / 255.0 - p[o + 1]) * w);
                    p[o + 2] += (float)((k.B / 255.0 - p[o + 2]) * w); p[o + 3] += (float)((k.A / 255.0 - p[o + 3]) * w);
                }
                paint = new Rgba32(MathUtil.ToByte(p[o]), MathUtil.ToByte(p[o + 1]), MathUtil.ToByte(p[o + 2]), MathUtil.ToByte(p[o + 3]));
            }
            TileStorage original = before[coord];
            Rgba32 start = original == null ? Rgba32.Transparent : original.Get(local * 4), next;
            if (settings.Erase)
            {
                byte alpha = MathUtil.ToByte(start.A / 255.0 * (1 - accumulated * settings.Color.A / 255.0));
                next = alpha == 0 ? Rgba32.Transparent : new Rgba32(start.R, start.G, start.B, alpha);
            }
            else next = CpuCompositor.Blend(start, paint, Math.Min(1, accumulated));
            if (selected < 1) next = CpuCompositor.Fade(start, next, selected);
            if (next == surface.GetPixel(x, y)) return false;
            return surface.SetPixelInternal(x, y, next);
        }
        /// <summary>Commits exact before/after tile states. Returns false when the stroke made no net pixel change.</summary>
        public bool Commit()
        {
            CheckOpen();
            var changes = new List<TileChange>();
            try
            {
                FinishInput();
                var coordinates = new List<TileCoord>(before.Keys); coordinates.Sort();
                foreach (var coord in coordinates)
                {
                    surface.Compact(coord); TileStorage after = surface.Capture(coord);
                    if (!TileStorage.Same(before[coord], after)) changes.Add(new TileChange(coord, before[coord], after));
                }
                var command = changes.Count > 0 ? new TileStrokeCommand(surface, changes, TransactionId) : null;
                document.FinishStroke(this, command); finished = true; ReleaseScratch();
                return changes.Count > 0;
            }
            catch { if (!finished) Cancel(); throw; }
        }
        public void Cancel()
        {
            if (finished) return;
            foreach (var snapshot in before) surface.Restore(snapshot.Key, snapshot.Value);
            if (before.Count > 0) document.PixelsChanged();
            document.FinishStroke(this, null); finished = true; ReleaseScratch();
        }
        private void ReleaseScratch()
        {
            before.Clear(); washes.Clear(); rollbackBytes = 0; curvePieces.Clear(); curvePoints = 0;
            if (paints != null) paints.Clear();
            if (dual != null) { dualCoverage.Clear(); dualPending.Clear(); }
        }
        public void Dispose() { Cancel(); }
        private void CheckOpen() { if (finished) throw new InvalidOperationException("Stroke is already finished."); }
    }
    internal sealed class TileChange
    {
        internal readonly TileCoord Coord;
        internal readonly TileStorage Before, After;
        internal TileChange(TileCoord coord, TileStorage before, TileStorage after) { Coord = coord; Before = before; After = after; }
    }
    internal sealed class TileStrokeCommand : IHistoryCommand
    {
        private readonly SparseTileSurface surface;
        private readonly List<TileChange> changes;
        internal readonly Guid TransactionId;
        public long ByteCost { get; private set; }
        internal TileStrokeCommand(SparseTileSurface surface, List<TileChange> changes, Guid transactionId)
        {
            this.surface = surface; this.changes = changes; TransactionId = transactionId; ByteCost = 64;
            foreach (var change in changes) ByteCost += 16 + (change.Before == null ? 0 : change.Before.ByteSize) + (change.After == null ? 0 : change.After.ByteSize);
        }
        public void Apply() { RestoreAll(false); }
        public void Revert() { RestoreAll(true); }
        private void RestoreAll(bool backwards)
        {
            long growth = 0;
            foreach (var change in changes)
            {
                var state = backwards ? change.Before : change.After;
                growth += (state == null ? 0 : state.ByteSize) - surface.TileBytesAt(change.Coord);
            }
            surface.EnsureGrowth(growth);
            foreach (var change in changes) surface.Restore(change.Coord, backwards ? change.Before : change.After);
        }
    }
}
