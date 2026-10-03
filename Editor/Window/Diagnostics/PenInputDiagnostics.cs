using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>OnGUI の入口で写した値。位置と傾きは GUI のまま、筆圧もカーブ・範囲補正前。</summary>
    internal readonly struct PenInputEvent
    {
        public readonly double TimeMs;
        public readonly EventType Type, RawType;
        public readonly PointerType Pointer;
        public readonly Vector2 Position, Delta, Tilt;
        public readonly float Pressure, Twist;
        public readonly PenStatus Pen;
        public readonly EventModifiers Modifiers;
        public readonly int Button;
        public readonly long Repaint;

        public PenInputEvent(double timeMs, EventType type, EventType rawType, PointerType pointer, Vector2 position,
            Vector2 delta, float pressure, Vector2 tilt, float twist, PenStatus pen, EventModifiers modifiers,
            int button, long repaint)
        {
            TimeMs = timeMs; Type = type; RawType = rawType; Pointer = pointer; Position = position;
            Delta = delta; Pressure = pressure; Tilt = tilt; Twist = twist; Pen = pen; Modifiers = modifiers;
            Button = button; Repaint = repaint;
        }

        public static PenInputEvent Read(Event e, double timeMs, long repaint) => new PenInputEvent(timeMs,
            e.type, e.rawType, e.pointerType, e.mousePosition, e.delta, e.pressure, e.tilt, e.twist,
            e.penStatus, e.modifiers, e.button, repaint);
    }

    internal readonly struct PenInputMetrics
    {
        public readonly int Drags, PressureLevels, MaxDragsPerRepaint;
        public readonly float MinPressure, MaxPressure;
        public readonly double MedianGapMs, MaxGapMs, MaxStep;
        public readonly int GapCount;
        public readonly bool Limited;
        public PenInputMetrics(int drags, int levels, float min, float max, double median, double gap,
            int gapCount, int batch, double step, bool limited)
        { Drags = drags; PressureLevels = levels; MinPressure = min; MaxPressure = max; MedianGapMs = median;
            MaxGapMs = gap; GapCount = gapCount; MaxDragsPerRepaint = batch; MaxStep = step; Limited = limited; }
    }

    /// <summary>時刻を呼び手が与える集計器。入力では追加・古い値の除去だけを行い、中央値の並べ替えは表示の更新時だけ。</summary>
    internal sealed class PenInputDiagnostics
    {
        internal const double WindowMs = 1000, RecordingMs = 30000;
        internal const int MaxWindowPoints = 32768, MaxRecordedEvents = 250000;
        readonly Queue<Point> points = new Queue<Point>();
        readonly List<double> gaps = new List<double>();
        readonly HashSet<float> pressures = new HashSet<float>();
        readonly List<PenInputEvent> recorded = new List<PenInputEvent>();
        PenInputEvent? previous;
        double limitedUntil, recordingStart;
        internal PenInputEvent? Latest { get; private set; }
        internal long RepaintNumber { get; private set; }
        internal bool Recording { get; private set; }
        internal bool RecordingLimited { get; private set; }
        internal bool HasRecording => recorded.Count > 0;
        internal int RecordedCount => recorded.Count;
        internal IReadOnlyList<PenInputEvent> Recorded => recorded;
        internal double RecordedDurationMs(double now) => Math.Max(0, Math.Min(RecordingMs, now - recordingStart));

        readonly struct Point
        {
            public readonly PenInputEvent Event;
            public readonly double PreviousTime, Gap, Step;
            public readonly bool HasGap;
            public Point(PenInputEvent e, PenInputEvent? prev)
            {
                Event = e; HasGap = prev.HasValue; PreviousTime = prev?.TimeMs ?? 0;
                Gap = prev.HasValue ? e.TimeMs - prev.Value.TimeMs : 0;
                double dx = prev.HasValue ? (double)e.Position.x - prev.Value.Position.x : 0;
                double dy = prev.HasValue ? (double)e.Position.y - prev.Value.Position.y : 0;
                Step = Math.Sqrt(dx * dx + dy * dy);
            }
        }

        internal void Observe(Event e, double now)
        {
            if (e.type == EventType.Repaint) RepaintNumber++;
            Observe(PenInputEvent.Read(e, now, RepaintNumber));
        }

        internal void Observe(PenInputEvent e)
        {
            RepaintNumber = Math.Max(RepaintNumber, e.Repaint);
            Advance(e.TimeMs);
            if (Recording)
            {
                recorded.Add(e);
                if (recorded.Count >= MaxRecordedEvents) { RecordingLimited = true; Recording = false; }
            }
            if (e.Type != EventType.MouseDown && e.Type != EventType.MouseDrag && e.Type != EventType.MouseUp && e.Type != EventType.MouseMove)
            {
                if (e.Type == EventType.MouseLeaveWindow) previous = null;
                return;
            }
            Latest = e;
            bool drag = e.Type == EventType.MouseDrag;
            var prev = drag && previous.HasValue && previous.Value.Pointer == e.Pointer && previous.Value.Button == e.Button ? previous : null;
            points.Enqueue(new Point(e, prev));
            previous = drag || e.Type == EventType.MouseDown ? e : (PenInputEvent?)null;
            if (points.Count > MaxWindowPoints) { points.Dequeue(); limitedUntil = e.TimeMs + WindowMs; }
        }

        internal void Advance(double now)
        {
            while (points.Count > 0 && points.Peek().Event.TimeMs <= now - WindowMs) points.Dequeue();
            if (Recording && now - recordingStart >= RecordingMs) Recording = false;
        }

        /// <summary>(now − 1000, now] の窓。間隔は両端が窓内の Down→Drag / Drag→Drag、筆圧は最新の入力の種類だけ。</summary>
        internal PenInputMetrics Metrics(double now)
        {
            Advance(now); pressures.Clear(); gaps.Clear();
            int drags = 0, batch = 0, maxBatch = 0; long frame = -1;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            double maxGap = 0, maxStep = 0;
            foreach (var p in points)
            {
                var e = p.Event;
                if (e.TimeMs > now) continue;
                if (Latest.HasValue && e.Pointer == Latest.Value.Pointer && !float.IsNaN(e.Pressure) && !float.IsInfinity(e.Pressure))
                { pressures.Add(e.Pressure); min = Math.Min(min, e.Pressure); max = Math.Max(max, e.Pressure); }
                if (e.Type != EventType.MouseDrag) continue;
                drags++;
                if (frame != e.Repaint) { frame = e.Repaint; batch = 0; }
                maxBatch = Math.Max(maxBatch, ++batch);
                if (p.HasGap && p.PreviousTime > now - WindowMs)
                {
                    gaps.Add(p.Gap); maxGap = Math.Max(maxGap, p.Gap);
                    if (!double.IsNaN(p.Step) && !double.IsInfinity(p.Step)) maxStep = Math.Max(maxStep, p.Step);
                }
            }
            gaps.Sort(); int n = gaps.Count;
            double median = n == 0 ? 0 : n % 2 == 1 ? gaps[n / 2] : (gaps[n / 2 - 1] + gaps[n / 2]) / 2;
            return new PenInputMetrics(drags, pressures.Count, pressures.Count == 0 ? 0 : min, pressures.Count == 0 ? 0 : max,
                median, maxGap, n, maxBatch, maxStep, now < limitedUntil);
        }

        internal void StartRecording(double now)
        {
            recorded.Clear(); recordingStart = now; RecordingLimited = false; Recording = true;
        }
        internal void StopRecording() => Recording = false;
        internal void ResetMeasurements() { points.Clear(); previous = null; Latest = null; limitedUntil = 0; }
        internal void DiscardRecording() { Recording = false; recorded.Clear(); RecordingLimited = false; }

        /// <summary>ヘッダー無しの 16 数値列。時計は記録開始からの ms、enum は Unity の数値。列の意味は ARCHITECTURE に記載。</summary>
        internal void WriteCsv(TextWriter writer)
        {
            var c = CultureInfo.InvariantCulture;
            foreach (var e in recorded)
            {
                writer.Write((e.TimeMs - recordingStart).ToString("R", c));
                writer.Write(','); writer.Write(((int)e.Type).ToString(c)); writer.Write(','); writer.Write(((int)e.RawType).ToString(c));
                writer.Write(','); writer.Write(((int)e.Pointer).ToString(c));
                foreach (float value in new[] { e.Position.x, e.Position.y, e.Delta.x, e.Delta.y, e.Pressure, e.Tilt.x, e.Tilt.y, e.Twist })
                { writer.Write(','); writer.Write(value.ToString("R", c)); }
                writer.Write(','); writer.Write(((int)e.Pen).ToString(c)); writer.Write(','); writer.Write(((int)e.Modifiers).ToString(c));
                writer.Write(','); writer.Write(e.Button.ToString(c)); writer.Write(','); writer.Write(e.Repaint.ToString(c));
                writer.Write('\n');
            }
        }
    }
}
