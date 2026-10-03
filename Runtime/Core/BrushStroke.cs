using System;
using System.Collections.Generic;

namespace Yozolab.YoluPainter.Core
{
    /// <summary>A transaction for one layer: one channel (<see cref="PaintDocument.BeginStroke"/>), a layer mask, or several channels of the
    /// layer painted together with one coverage (<see cref="PaintDocument.BeginMaterialStroke"/>, Substance Painter's material painting).
    /// Dispose cancels unless committed. Brush settings are
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
    /// steps; the segment to the newest point waits for the next point (or the commit).
    /// <para>Several channels: the dabs, the dual brush and the per-pixel stroke coverage are computed once and shared; each channel
    /// (a target) keeps its own pixels before the stroke, its own colour (and its own colour-dynamics stream, seeded as a stroke of that
    /// channel alone would seed it) and applies the shared coverage with the arithmetic of a one-channel stroke, so every channel ends
    /// with exactly the bytes a stroke of that channel alone gives. Lock Transparent Pixels is decided per channel and pixel. The
    /// rollback copies of every channel and the shared coverage count together against the document's active-stroke budget. One
    /// commit is one undo step for every channel.</para></summary>
    public sealed class BrushStroke : IDisposable
    {
        private readonly PaintDocument document;
        /// <summary>The settings that shape the dabs (the first target's; the targets' settings differ only in colour).</summary>
        private readonly BrushSettings settings;
        /// <summary>One surface the stroke paints: its settings (the colour painted there), its colour (and colour stream) and the tiles
        /// it has taken over (rollback copies, in <see cref="Before"/>).</summary>
        private sealed class Target
        {
            public SparseTileSurface Surface; public BrushSettings Settings;
            public Rgba32 StrokeColor, DabColor; public Random ColorRandom; public bool TipColors;
            public readonly Dictionary<TileCoord, TileStorage> Before = new Dictionary<TileCoord, TileStorage>();
        }
        private readonly Target[] targets;
        /// <summary>True when some target keeps per-tip colours (then a pixel at the ceiling still moves toward the dab colour).</summary>
        private readonly bool anyTipColors;
        /// <summary>What the stroke keeps for one tile it has touched (created together with the first target's entry in Before).</summary>
        private sealed class StrokeTile
        {
            /// <summary>Accumulated stroke coverage (0..1) per pixel, tile-local row-major, shared by every target. Freed with the stroke.</summary>
            public float[] Wash;
            /// <summary>Per target: whether its pixels of the tile were taken over, its rollback copy, and per-tip colours (the stroke
            /// colour per pixel, straight RGBA 0..1) or null.</summary>
            public bool[] Captured; public TileStorage[] Before; public float[][] Paint;
            /// <summary>Per target: the last pixel pass (<see cref="passSerial"/>) that changed a pixel of the tile.</summary>
            public long[] ChangedInPass;
            public StrokeTile(int targets, float[] wash)
            {
                Wash = wash; Captured = new bool[targets]; Before = new TileStorage[targets]; Paint = new float[targets][];
                ChangedInPass = new long[targets]; for (int k = 0; k < targets; k++) ChangedInPass[k] = -1;
            }
        }
        private readonly Dictionary<TileCoord, StrokeTile> strokeTiles = new Dictionary<TileCoord, StrokeTile>();
        /// <summary>The tile the pixel loop is in. Each dab (or ApplyPixel call) looks its tiles up once per tile instead of once
        /// per pixel; the surface's tile objects only change in Commit / Cancel, after the last pass.</summary>
        private sealed class TileCursor
        {
            public int X, Y; public TileCoord Coord;
            public StrokeTile Stroke; public TileStorage Selected; public float[] Dual;
            /// <summary>Per target: the surface's tile (updated when a write creates it).</summary>
            public readonly TileStorage[] Surfaces;
            /// <summary>Per target: whether the pixel being worked on is painted there (scratch of ApplyPixelAt).</summary>
            public readonly bool[] Accepts;
            public TileCursor(int targets) { Surfaces = new TileStorage[targets]; Accepts = new bool[targets]; }
            public void Reset() { X = int.MinValue; Y = int.MinValue; Stroke = null; Selected = null; Dual = null; Array.Clear(Surfaces, 0, Surfaces.Length); }
        }
        private readonly TileCursor cursor;
        /// <summary>The size shared by every target surface.</summary>
        private readonly int width, height, tileSize;
        // ひと通りの画素の処理（ダブ 1 つ、ApplyPixel 1 回）で変えたタイル（とその面）。面の書き換え番号と変更の記録は、画素ごとでなく
        // その処理の終わりにタイルごとに 1 回進める（結果の画素も、どのタイルが変わったと伝わるかも同じ）。
        private struct PassChange { public int Target; public TileCoord Coord; }
        private readonly List<PassChange> passChanged = new List<PassChange>();
        private long passSerial;
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
        // 画素ごとにストロークの色（straight RGBA 0〜1）を持ち、ダブの色へ流量の割合で寄せる。色の列はターゲットごとに持つ（そのチャンネル
        // だけを塗ったときと同じ列）。
        private const int ColorStream = 0x2545F491, DualStream = 0x5DEECE6;
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
        /// <summary>Lock Transparent Pixels: every pixel keeps its alpha and only its colour moves towards the paint (source-atop);
        /// fully transparent pixels are left as they are, RGB included. Never with Erase (refused when the stroke begins).</summary>
        private readonly bool keepAlpha;
        /// <summary>Channels the stroke switched on when it began (a material stroke on a layer without them), applied already: undone with
        /// a cancel or a commit that changed nothing, else part of the stroke's undo step. Null when none.</summary>
        private readonly IHistoryCommand enabling;
        public Guid TransactionId { get; private set; }
        public bool IsFinished { get { return finished; } }
        public long SampleCount { get; private set; }
        public long StampCount { get; private set; }
        /// <summary>Tiles taken over, summed over the channels the stroke paints.</summary>
        public int ChangedTileCount { get { int n = 0; foreach (var t in targets) n += t.Before.Count; return n; } }
        /// <summary>Rollback payload of the stroke: every channel's tile copies, the shared coverage and the scratch it keeps.</summary>
        public long RollbackBytes { get { return rollbackBytes; } }
        /// <summary>How many surfaces the stroke paints (1 for a channel or a mask).</summary>
        public int TargetCount { get { return targets.Length; } }
        internal BrushStroke(PaintDocument document, SparseTileSurface surface, BrushSettings settings, bool keepAlpha = false)
            : this(document, new[] { (surface, settings) }, keepAlpha, null) { }
        /// <param name="paint">The surfaces with their settings (the shape settings must be the same; only the colour and colour
        /// dynamics may differ).</param>
        internal BrushStroke(PaintDocument document, IReadOnlyList<(SparseTileSurface surface, BrushSettings settings)> paint, bool keepAlpha, IHistoryCommand enabling)
        {
            if (paint == null || paint.Count == 0) throw new ArgumentException("A stroke paints at least one surface.", nameof(paint));
            settings = paint[0].settings;
            if (keepAlpha && settings.Erase) throw new InvalidOperationException("Erasing removes alpha, which a stroke that keeps alpha cannot do.");
            this.keepAlpha = keepAlpha; this.enabling = enabling;
            selection = document.Selection; this.document = document; TransactionId = Guid.NewGuid(); random = new Random(settings.Seed);
            targets = new Target[paint.Count];
            for (int k = 0; k < targets.Length; k++)
            {
                var s = paint[k].settings;
                var t = new Target { Surface = paint[k].surface, Settings = s, StrokeColor = s.Color };
                if (s.HasColorDynamics && !s.Erase)
                {
                    // ストロークの色を最初に 1 回引く（ダブごとでないとき、また 3D の面のブラシ（ダブを持たない）はこの色で塗る）。
                    t.ColorRandom = new Random(s.Seed ^ ColorStream); t.StrokeColor = ColorDynamics.Next(s, t.ColorRandom);
                    if (s.ColorPerTip) { t.TipColors = true; anyTipColors = true; }
                }
                t.DabColor = t.StrokeColor;
                targets[k] = t;
            }
            cursor = new TileCursor(targets.Length);
            width = targets[0].Surface.Width; height = targets[0].Surface.Height; tileSize = targets[0].Surface.TileSize;
            foreach (var t in targets)
                if (t.Surface.Width != width || t.Surface.Height != height || t.Surface.TileSize != tileSize) throw new ArgumentException("Every surface of a stroke must have the same size.", nameof(paint));
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
                bool changed = false;
                if (x >= 0 && y >= 0 && x < width && y < height)
                {
                    int tile = tileSize; BeginPass();
                    try { changed = ApplyPixelAt(cursor, true, x / tile, y / tile, (y % tile) * tile + x % tile, coverage, pressure, 1, 1); }
                    finally { EndPass(); }
                }
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
                if (anyTipColors) foreach (var t in targets) if (t.TipColors) t.DabColor = ColorDynamics.Next(t.Settings, t.ColorRandom);
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
            int minX = Math.Max(0, (int)Math.Ceiling(x - extent - 0.5)), maxX = Math.Min(width - 1, (int)Math.Floor(x + extent - 0.5));
            int minY = Math.Max(0, (int)Math.Ceiling(y - extent - 0.5)), maxY = Math.Min(height - 1, (int)Math.Floor(y + extent - 0.5));
            double angle = dual.Angle * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
            double aspectX = 1, aspectY = 1;
            if (dual.Tip != null) { if (dual.Tip.Width >= dual.Tip.Height) aspectY = dual.Tip.Height / (double)dual.Tip.Width; else aspectX = dual.Tip.Width / (double)dual.Tip.Height; }
            int tile = tileSize;
            float[] cells = null; int cellsX = int.MinValue, cellsY = int.MinValue; // 今いるタイルの溜まり（タイルが変わったときだけ引く）
            for (int py = minY; py <= maxY; py++)
            {
                int ty = py / tile, row = (py - ty * tile) * tile, tx = minX / tile, lx = minX - tx * tile;
                for (int px = minX; px <= maxX; px++, lx++)
                {
                    if (lx == tile) { lx = 0; tx++; }
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
                    if (tx != cellsX || ty != cellsY)
                    {
                        var coord = new TileCoord(tx, ty); cellsX = tx; cellsY = ty;
                        if (!dualCoverage.TryGetValue(coord, out cells))
                        {
                            long nextBytes = rollbackBytes + 64 + (long)tile * tile * 4;
                            document.EnsureStrokeBudget(nextBytes);
                            cells = new float[tile * tile]; dualCoverage.Add(coord, cells); rollbackBytes = nextBytes;
                        }
                    }
                    int local = row + lx;
                    if (coverage > cells[local]) cells[local] = (float)coverage;
                }
            }
        }
        /// <summary>The shape of one dab, read by the pixel loop (on worker threads too; never changed while they run).</summary>
        private sealed class DabShape
        {
            public double X, Y, Radius, Cos, Sin, Roundness, AspectX, AspectY, Hardness, Pressure, OpacityScale, FlowScale;
            public BrushTip Tip; public bool Plain, Textured;
        }
        private readonly DabShape shape = new DabShape();
        /// <summary>Dabs whose bounding box times the number of channels painted has at least this many pixels may change the tiles the
        /// stroke has already taken over on worker threads (see Dab). Below it the cost of starting workers outweighs the gain (measured with radius 32 and 128 on
        /// tiles of 128). Not changed by the core; a harness may lower it to drive the worker path with small documents.</summary>
        internal static int ParallelDabPixels = 128 * 128;
        /// <summary>One dab. With the round tip, no rotation and roundness 1 this is exactly the original circular dab.
        /// <para>Large dabs run in two steps. A tile is safe when the stroke already holds it (its rollback copy and coverage exist)
        /// and its pixels are the surface's own writable buffer: changing its pixels can neither allocate nor be refused by a
        /// budget, and touches nothing outside the tile. First the pixels of the other tiles go through in raster order on this
        /// thread — every budget check, rollback copy and tile allocation happens there, in the same order as a dab done pixel by
        /// pixel — then the safe tiles are done on worker threads in bands of rows. Each pixel is computed from its own inputs
        /// only (its coverage, its stroke coverage and colour, its pixel before the stroke), so the bytes are the same with any
        /// number of threads.</para></summary>
        private bool Dab(double x, double y, double radius, double angle, double roundness, double pressure, double opacityScale, double flowScale, BrushTip tip)
        {
            double extent = tip == null ? radius : radius * 1.4142135623730951; // a square tip's corners reach √2·r when rotated
            int minX = Math.Max(0, (int)Math.Ceiling(x - extent - 0.5));
            int maxX = Math.Min(width - 1, (int)Math.Floor(x + extent - 0.5));
            int minY = Math.Max(0, (int)Math.Ceiling(y - extent - 0.5));
            int maxY = Math.Min(height - 1, (int)Math.Floor(y + extent - 0.5));
            var s = shape;
            s.X = x; s.Y = y; s.Radius = radius; s.Cos = Math.Cos(angle); s.Sin = Math.Sin(angle); s.Roundness = roundness; s.Tip = tip;
            s.AspectX = 1; s.AspectY = 1;
            if (tip != null) { if (tip.Width >= tip.Height) s.AspectY = tip.Height / (double)tip.Width; else s.AspectX = tip.Width / (double)tip.Height; }
            s.Textured = settings.Texture != null && settings.TextureDepth > 0;
            s.Plain = angle == 0 && roundness == 1;
            s.Hardness = settings.Hardness; s.Pressure = pressure; s.OpacityScale = opacityScale; s.FlowScale = flowScale;
            if (minX > maxX || minY > maxY) return false;
            int tile = tileSize, tx0 = minX / tile, ty0 = minY / tile, columns = maxX / tile - tx0 + 1, rows = maxY / tile - ty0 + 1;
            bool[] safe = null; int safeCount = 0, degree = CoreParallelism.Degree;
            // 画素ごとの仕事はチャンネルの数だけ増えるので、外接の箱 × チャンネルの数で比べる（1 チャンネルなら以前と同じ）
            if (degree > 1 && (long)(maxX - minX + 1) * (maxY - minY + 1) * targets.Length >= ParallelDabPixels && columns * rows > 1)
            {
                safe = new bool[columns * rows];
                for (int j = 0; j < rows; j++) for (int i = 0; i < columns; i++)
                {
                    var coord = new TileCoord(tx0 + i, ty0 + j);
                    if (strokeTiles.TryGetValue(coord, out var held) && SafeForWorkers(held, coord)) { safe[j * columns + i] = true; safeCount++; }
                }
                if (safeCount == 0) safe = null;
            }
            bool changed = false;
            BeginPass();
            try
            {
                changed = DabPixels(s, cursor, true, minX, maxX, minY, maxY, safe, tx0, ty0, columns);
                if (safe != null)
                {
                    var jobs = new int[safeCount]; for (int k = 0, n = 0; k < safe.Length; k++) if (safe[k]) jobs[n++] = k;
                    // タイルを行の束に分けて、スレッドが余らないようにする（同じタイルの別の行は別の画素しか触らない）
                    int chunks = Math.Max(1, Math.Min(tile / 16, (degree + safeCount - 1) / safeCount)), rowsPerChunk = (tile + chunks - 1) / chunks;
                    var results = new bool[safeCount * chunks];
                    CoreParallelism.For(safeCount * chunks, degree, item =>
                    {
                        int n = item / chunks, chunk = item - n * chunks, k = jobs[n], tx = tx0 + k % columns, ty = ty0 + k / columns;
                        int y0 = Math.Max(minY, ty * tile + chunk * rowsPerChunk), y1 = Math.Min(maxY, Math.Min(ty * tile + tile - 1, ty * tile + (chunk + 1) * rowsPerChunk - 1));
                        if (y0 > y1) return;
                        var c = new TileCursor(targets.Length); c.Reset();
                        results[item] = DabPixels(s, c, false, Math.Max(minX, tx * tile), Math.Min(maxX, tx * tile + tile - 1), y0, y1, null, 0, 0, 0);
                    });
                    foreach (bool r in results) changed |= r;
                    for (int n = 0; n < safeCount; n++)
                    {
                        int k = jobs[n]; var coord = new TileCoord(tx0 + k % columns, ty0 + k / columns); var held = strokeTiles[coord];
                        for (int t = 0; t < targets.Length; t++) if (held.ChangedInPass[t] == passSerial) passChanged.Add(new PassChange { Target = t, Coord = coord });
                    }
                }
            }
            finally { EndPass(); }
            return changed;
        }
        /// <summary>True when worker threads may change the tile: every target has taken it over (its rollback copy and coverage exist)
        /// and its pixels there are the surface's own writable buffer, so no write can allocate or meet a budget.</summary>
        private bool SafeForWorkers(StrokeTile held, TileCoord coord)
        {
            for (int k = 0; k < targets.Length; k++)
            {
                if (!held.Captured[k]) return false;
                var live = targets[k].Surface.PeekTile(coord);
                if (live == null || !live.Writable) return false;
            }
            return true;
        }
        /// <summary>The pixels [minX, maxX] × [minY, maxY] of the dab in raster order, leaving out the tiles marked in skip (indexed
        /// from tile (tx0, ty0), columns wide). collect: record changed tiles in passChanged (only on the calling thread).</summary>
        private bool DabPixels(DabShape s, TileCursor c, bool collect, int minX, int maxX, int minY, int maxY, bool[] skip, int tx0, int ty0, int columns)
        {
            double x = s.X, y = s.Y, radius = s.Radius, cos = s.Cos, sin = s.Sin, roundness = s.Roundness, hardness = s.Hardness;
            double aspectX = s.AspectX, aspectY = s.AspectY, pressure = s.Pressure, opacityScale = s.OpacityScale, flowScale = s.FlowScale;
            BrushTip tip = s.Tip; bool plain = s.Plain, textured = s.Textured;
            int tile = tileSize; bool changed = false;
            for (int py = minY; py <= maxY; py++)
            {
                int ty = py / tile, row = (py - ty * tile) * tile, tx = minX / tile, lx = minX - tx * tile;
                double dy = py + 0.5 - y;
                for (int px = minX; px <= maxX; px++, lx++)
                {
                    if (lx == tile) { lx = 0; tx++; }
                    if (skip != null && skip[(ty - ty0) * columns + tx - tx0])
                    {
                        // 残りのこのタイルの列を飛ばす（ワーカーが受け持つ）
                        int jump = tile - 1 - lx; px += jump; lx += jump; continue;
                    }
                    double dx = px + 0.5 - x;
                    double coverage;
                    if (tip == null)
                    {
                        // 回転も潰しも無いときは元の式そのもので測る（丸ブラシの結果を以前とビット単位で揃える）。
                        double d;
                        if (plain) d = Math.Sqrt(dx * dx + dy * dy) / radius;
                        else
                        {
                            double u = (cos * dx + sin * dy) / radius, v = (-sin * dx + cos * dy) / (radius * roundness);
                            d = Math.Sqrt(u * u + v * v);
                        }
                        if (d > 1) continue;
                        coverage = 1;
                        if (d > hardness)
                        {
                            double t = (1 - d) / (1 - hardness);
                            coverage = t * t * (3 - 2 * t);
                        }
                    }
                    else
                    {
                        double u = (cos * dx + sin * dy) / radius, v = (-sin * dx + cos * dy) / (radius * roundness);
                        coverage = tip.Sample((u / aspectX + 1) * 0.5, (v / aspectY + 1) * 0.5);
                        if (coverage <= 0) continue;
                    }
                    if (dual != null)
                    {
                        MoveTo(c, tx, ty);
                        float[] cells = c.Dual;
                        coverage = DualBrush.Combine(dual.Mode, coverage, cells == null ? 0 : cells[row + lx]); if (coverage <= 0) continue;
                    }
                    double ceilingScale = opacityScale;
                    if (textured)
                    {
                        // 紙の質感は流量ではなく天井に効かせる（Photoshop の「描点ごとに適用」オフと同じ）。流量に効かせると、
                        // 間隔の細かいブラシでは重なったダブが溜まって質感が消えてしまう。
                        double grain = settings.Texture.SampleTiled((px + 0.5) / settings.TextureScale, (py + 0.5) / settings.TextureScale);
                        ceilingScale *= 1 - settings.TextureDepth * (1 - grain);
                        if (ceilingScale <= 0) continue;
                    }
                    changed |= ApplyPixelAt(c, collect, tx, ty, row + lx, coverage, pressure, ceilingScale, flowScale);
                }
            }
            return changed;
        }
        /// <summary>Starts a pass over pixels (one dab or one ApplyPixel call): tiles are looked up afresh.</summary>
        private void BeginPass() { passSerial++; cursor.Reset(); }
        /// <summary>Ends a pass: the surface records each tile the pass changed once (revision and change journal), also when
        /// the pass stopped with an exception (the cancel that follows records them again when it restores them).</summary>
        private void EndPass()
        {
            cursor.Reset();
            if (passChanged.Count == 0) return;
            for (int i = 0; i < passChanged.Count; i++) targets[passChanged[i].Target].Surface.NotifyTileChanged(passChanged[i].Coord);
            passChanged.Clear();
        }
        private void MoveTo(TileCursor c, int tx, int ty)
        {
            if (c.X == tx && c.Y == ty) return;
            c.X = tx; c.Y = ty; c.Coord = new TileCoord(tx, ty);
            strokeTiles.TryGetValue(c.Coord, out c.Stroke);
            for (int k = 0; k < targets.Length; k++) c.Surfaces[k] = targets[k].Surface.PeekTile(c.Coord);
            c.Selected = selection == null ? null : selection.Surface.PeekTile(c.Coord);
            c.Dual = null; if (dual != null) dualCoverage.TryGetValue(c.Coord, out c.Dual);
        }
        /// <summary>One pixel of a pass: (tx, ty) is its tile, local its index in the tile. The same arithmetic and the same budget
        /// checks in the same order as a per-pixel lookup; only the dictionary lookups are made once per tile. The coverage is accumulated
        /// once and applied to every target with that target's colour and pixels before the stroke.</summary>
        private bool ApplyPixelAt(TileCursor c, bool collect, int tx, int ty, int local, double coverage, double pressure, double opacityScale, double flowScale)
        {
            MoveTo(c, tx, ty);
            double selected = selection == null ? 1 : (c.Selected == null ? 0 : c.Selected.Get(local * 4).A) / 255.0;
            if (selected <= 0) return false;
            double ceiling = settings.Opacity * opacityScale * (settings.PressureOpacity ? pressure : 1);
            double flow = coverage * settings.Flow * flowScale * (settings.PressureFlow ? pressure : 1);
            if (flow <= 0 || ceiling <= 0) return false;
            var st = c.Stroke;
            if (st != null && st.Wash[local] >= ceiling && !anyTipColors) return false;
            int n = targets.Length, tile = tileSize;
            // アルファを守るストロークでは、透明な画素は変わらない（アルファは変わらないので、今の面の値が描く前の値と同じ）。
            // 巻き戻しの写しを取る前にチャンネルごとに見るので、透明なタイルには何も割り当てない
            var accepts = c.Accepts; bool any = false;
            for (int k = 0; k < n; k++) { bool a = !keepAlpha || (c.Surfaces[k] != null && c.Surfaces[k].Get(local * 4).A != 0); accepts[k] = a; any |= a; }
            if (!any) return false;
            if (st == null)
            {
                // タイルを初めて触る: 共有の被覆率と、この画素を塗るチャンネルの写しを合わせて 1 回で予算と比べる（1 チャンネルなら以前と同じ 1 回）
                long nextBytes = rollbackBytes + 64 + (long)tile * tile * 4;
                for (int k = 0; k < n; k++) if (accepts[k]) nextBytes += CaptureBytes(k, c.Coord, c.Surfaces[k]);
                document.EnsureStrokeBudget(nextBytes);
                st = new StrokeTile(n, new float[tile * tile]);
                for (int k = 0; k < n; k++) if (accepts[k]) Capture(k, st, c.Coord, c.Surfaces[k]);
                strokeTiles.Add(c.Coord, st); c.Stroke = st; rollbackBytes = nextBytes;
            }
            else
                for (int k = 0; k < n; k++)
                    if (accepts[k] && !st.Captured[k])
                    {
                        // 透明部分のロックで、このタイルでは初めてこのチャンネルを塗る（ほかのチャンネルが先に塗っていた）
                        long nextBytes = rollbackBytes + CaptureBytes(k, c.Coord, c.Surfaces[k]);
                        document.EnsureStrokeBudget(nextBytes);
                        Capture(k, st, c.Coord, c.Surfaces[k]); rollbackBytes = nextBytes;
                    }
            float[] wash = st.Wash;
            double previousWash = wash[local];
            // 天井に届いた画素も、ダブごとの色ならその色へは寄せる（濃さは天井のまま）。
            double accumulated = previousWash >= ceiling ? previousWash : wash[local] + (ceiling - wash[local]) * Math.Min(1, flow);
            wash[local] = (float)accumulated;
            bool changed = false;
            for (int k = 0; k < n; k++)
            {
                if (!accepts[k]) continue;
                var t = targets[k];
                Rgba32 paint = t.StrokeColor;
                if (t.TipColors)
                {
                    float[] p = st.Paint[k]; int o = local * 4; double w = Math.Min(1, flow);
                    Rgba32 dab = t.DabColor;
                    if (previousWash <= 0) { p[o] = dab.R / 255f; p[o + 1] = dab.G / 255f; p[o + 2] = dab.B / 255f; p[o + 3] = dab.A / 255f; }
                    else
                    {
                        p[o] += (float)((dab.R / 255.0 - p[o]) * w); p[o + 1] += (float)((dab.G / 255.0 - p[o + 1]) * w);
                        p[o + 2] += (float)((dab.B / 255.0 - p[o + 2]) * w); p[o + 3] += (float)((dab.A / 255.0 - p[o + 3]) * w);
                    }
                    paint = new Rgba32(MathUtil.ToByte(p[o]), MathUtil.ToByte(p[o + 1]), MathUtil.ToByte(p[o + 2]), MathUtil.ToByte(p[o + 3]));
                }
                TileStorage original = st.Before[k];
                Rgba32 start = original == null ? Rgba32.Transparent : original.Get(local * 4), next;
                if (t.Settings.Erase)
                {
                    byte alpha = MathUtil.ToByte(start.A / 255.0 * (1 - accumulated * t.Settings.Color.A / 255.0));
                    next = alpha == 0 ? Rgba32.Transparent : new Rgba32(start.R, start.G, start.B, alpha);
                }
                else if (keepAlpha) next = PaintDocument.PaintKeepingAlpha(start, paint, Math.Min(1, accumulated) * selected); // 選択の割合も色の寄せ方に入れる（丸めは 1 回）
                else next = CpuCompositor.BlendUnchecked(start, paint, Math.Min(1, accumulated), LayerBlendMode.Normal); // 0..1 なので Blend の検査は要らない
                if (selected < 1 && !keepAlpha) next = CpuCompositor.Fade(start, next, selected);
                if (!t.Surface.WritePixelQuiet(c.Coord, ref c.Surfaces[k], local * 4, next)) continue;
                if (st.ChangedInPass[k] != passSerial) { st.ChangedInPass[k] = passSerial; if (collect) passChanged.Add(new PassChange { Target = k, Coord = c.Coord }); }
                changed = true;
            }
            return changed;
        }
        /// <summary>Rollback payload of taking over target k's tile: the copy (counted in full although it is shared copy-on-write until the
        /// first write; nothing when a whole-tile edit took it over already) and, with per-tip colours, the per-pixel stroke colour.</summary>
        private long CaptureBytes(int k, TileCoord coord, TileStorage live)
        { return (targets[k].Before.ContainsKey(coord) || live == null ? 0 : live.ByteSize) + (targets[k].TipColors ? (long)tileSize * tileSize * 16 : 0); }
        private void Capture(int k, StrokeTile st, TileCoord coord, TileStorage live)
        {
            // 塗りつぶし（タイル丸ごとの編集）が先に写しを取っていれば、それがストロークの前の画素
            if (!targets[k].Before.TryGetValue(coord, out var captured)) { captured = live == null ? null : live.Clone(); targets[k].Before.Add(coord, captured); }
            st.Before[k] = captured; st.Captured[k] = true;
            if (targets[k].TipColors) st.Paint[k] = new float[tileSize * tileSize * 4];
        }

        // ───── whole-tile edits (TriangleFill): tiles computed from their state before the stroke, not from dabs ─────
        // 1 チャンネル（マスクを含む）のストロークだけ。マテリアルのストローク（複数のチャンネル）では断る。

        /// <summary>The single surface of a one-channel (or mask) stroke; whole-tile edits are refused for a material stroke.</summary>
        private Target Single
        {
            get
            {
                if (targets.Length != 1) throw new InvalidOperationException("Whole-tile edits (polygon fill) paint one channel or a mask; this stroke paints " + targets.Length + " channels.");
                return targets[0];
            }
        }
        /// <summary>The tile as it was before the stroke (null when absent), captured now under the rollback budget the first time a
        /// whole-tile edit asks for it. The returned object is never written to.</summary>
        internal TileStorage OriginalTile(TileCoord coord)
        {
            CheckOpen(); var t = Single;
            if (t.Before.TryGetValue(coord, out var original)) return original;
            var current = t.Surface.PeekTile(coord);
            long next = rollbackBytes + 64 + (current == null ? 0 : current.ByteSize);
            document.EnsureStrokeBudget(next);
            original = current == null ? null : current.Clone();
            t.Before.Add(coord, original); rollbackBytes = next;
            return original;
        }
        /// <summary>Puts after (computed from <see cref="OriginalTile"/>) in place of a captured tile, under the source budget. Commit
        /// records it like any other changed tile; Cancel puts the original back.</summary>
        internal void ReplaceTile(TileCoord coord, TileStorage after)
        {
            CheckOpen(); var t = Single;
            if (!t.Before.ContainsKey(coord)) throw new InvalidOperationException("Capture the tile with OriginalTile before replacing it.");
            t.Surface.EnsureGrowth((after == null ? 0 : after.ByteSize) - t.Surface.TileBytesAt(coord));
            t.Surface.Restore(coord, after); cursor.Reset();
        }
        /// <summary>The tile the surface holds now (read only, not a copy; null when absent).</summary>
        internal TileStorage PeekSurfaceTile(TileCoord coord) => Single.Surface.PeekTile(coord);
        /// <summary>Scratch memory a whole-tile edit keeps until the stroke ends, counted in the rollback budget like the brush's own
        /// per-tile scratch (throws, changing nothing, when it does not fit).</summary>
        internal void ReserveScratch(long bytes)
        {
            CheckOpen(); _ = Single;
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            long next = rollbackBytes + bytes; document.EnsureStrokeBudget(next); rollbackBytes = next;
        }

        /// <summary>Commits exact before/after tile states of every channel as one undo step (with the channels the stroke switched on).
        /// Returns false when the stroke made no net pixel change; then nothing is recorded and switched-on channels are off again.</summary>
        public bool Commit()
        {
            CheckOpen();
            try
            {
                FinishInput();
                var parts = new List<TileStrokeCommand>();
                foreach (var t in targets)
                {
                    var changes = new List<TileChange>();
                    var coordinates = new List<TileCoord>(t.Before.Keys); coordinates.Sort();
                    foreach (var coord in coordinates)
                    {
                        t.Surface.Compact(coord); TileStorage after = t.Surface.Capture(coord);
                        if (!TileStorage.Same(t.Before[coord], after)) changes.Add(new TileChange(coord, t.Before[coord], after));
                    }
                    if (changes.Count > 0) parts.Add(new TileStrokeCommand(t.Surface, changes, TransactionId));
                }
                IHistoryCommand command = parts.Count == 0 ? null : parts.Count == 1 ? parts[0] : (IHistoryCommand)new MultiSurfaceStrokeCommand(parts);
                if (enabling != null)
                {
                    // 有効にしたチャンネルは、何も変わらなければ戻し、変われば同じ Undo に入れる（Redo は有効にしてから画素を戻す）
                    if (command == null) document.RevertStrokeSetup(enabling);
                    else command = new CompoundCommand(new List<IHistoryCommand> { enabling, command });
                }
                document.FinishStroke(this, command); finished = true; ReleaseScratch();
                return parts.Count > 0;
            }
            catch { if (!finished) Cancel(); throw; }
        }
        public void Cancel()
        {
            if (finished) return;
            bool restored = false;
            foreach (var t in targets) foreach (var snapshot in t.Before) { t.Surface.Restore(snapshot.Key, snapshot.Value); restored = true; }
            if (restored) document.PixelsChanged();
            if (enabling != null) document.RevertStrokeSetup(enabling);
            document.FinishStroke(this, null); finished = true; ReleaseScratch();
        }
        private void ReleaseScratch()
        {
            foreach (var t in targets) t.Before.Clear();
            strokeTiles.Clear(); passChanged.Clear(); cursor.Reset(); rollbackBytes = 0; curvePieces.Clear(); curvePoints = 0;
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
        internal SparseTileSurface Surface { get { return surface; } }
        private void RestoreAll(bool backwards) { surface.EnsureGrowth(Growth(backwards)); RestoreUnchecked(backwards); }
        /// <summary>Source bytes the surface grows by when it goes to the after (or before) state.</summary>
        internal long Growth(bool backwards)
        {
            long growth = 0;
            foreach (var change in changes)
            {
                var state = backwards ? change.Before : change.After;
                growth += (state == null ? 0 : state.ByteSize) - surface.TileBytesAt(change.Coord);
            }
            return growth;
        }
        internal void RestoreUnchecked(bool backwards) { foreach (var change in changes) surface.Restore(change.Coord, backwards ? change.Before : change.After); }
    }
    /// <summary>The tile changes of a stroke that painted several surfaces (a material stroke), undone and redone together. The source
    /// budget is checked once for the growth of all of them before any surface changes, so a refused undo or redo changes nothing.</summary>
    internal sealed class MultiSurfaceStrokeCommand : IHistoryCommand
    {
        private readonly List<TileStrokeCommand> parts;
        public long ByteCost { get; private set; }
        internal MultiSurfaceStrokeCommand(List<TileStrokeCommand> parts)
        { this.parts = parts; foreach (var p in parts) ByteCost += p.ByteCost; }
        public void Apply() { RestoreAll(false); }
        public void Revert() { RestoreAll(true); }
        private void RestoreAll(bool backwards)
        {
            long growth = 0;
            foreach (var p in parts) growth += p.Growth(backwards);
            parts[0].Surface.EnsureGrowth(growth); // 面はどれも同じ文書の予算（BeforeSourceGrowth）を見る
            foreach (var p in parts) p.RestoreUnchecked(backwards);
        }
    }
}
