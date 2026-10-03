using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class PenInputTests
    {
        internal static PenInputEvent Point(double ms, EventType type, float pressure = .5f, float x = 0, long repaint = 1,
            PointerType pointer = PointerType.Pen, int button = 0) => new PenInputEvent(ms, type, type, pointer, new Vector2(x, 10),
                new Vector2(3, -4), pressure, new Vector2(.2f, -.4f), .75f,
                PenStatus.Contact | PenStatus.Barrel | PenStatus.Eraser | PenStatus.Inverted,
                EventModifiers.Shift | EventModifiers.Alt, button, repaint);

        [Test] public void ASlidingSecondCountsExactPressuresGapsBatchesAndDistances()
        {
            var d = new PenInputDiagnostics();
            d.Observe(Point(0, EventType.MouseDown, .25f));
            d.Observe(Point(10, EventType.MouseDrag, .5f, 3));
            d.Observe(Point(30, EventType.MouseDrag, .5f, 8));
            d.Observe(Point(90, EventType.MouseDrag, .75f, 14));
            d.Observe(Point(400, EventType.MouseDrag, .25f, 26, 2));
            var m = d.Metrics(400);
            Assert.That((m.Drags, m.PressureLevels, m.GapCount, m.MaxDragsPerRepaint), Is.EqualTo((4, 3, 4, 3)));
            Assert.That((m.MinPressure, m.MaxPressure), Is.EqualTo((.25f, .75f)));
            Assert.That((m.MedianGapMs, m.MaxGapMs, m.MaxStep), Is.EqualTo((40d, 310d, 12d)));
            m = d.Metrics(1090); // 90 ms は左端そのものなので、400 ms の点だけ残り、その前の間隔は入らない
            Assert.That((m.Drags, m.PressureLevels, m.GapCount, m.MaxDragsPerRepaint), Is.EqualTo((1, 1, 0, 1)));
            m = d.Metrics(1400);
            Assert.That((m.Drags, m.PressureLevels, m.GapCount, m.MaxDragsPerRepaint, m.MaxStep), Is.EqualTo((0, 0, 0, 0, 0d)));
        }

        [Test] public void OddMedianAndAnUnfinishedRepaintAreIncluded()
        {
            var d = new PenInputDiagnostics(); d.Observe(Point(0, EventType.MouseDown));
            foreach (var t in new[] { 5, 25, 35 }) d.Observe(Point(t, EventType.MouseDrag));
            Assert.That((d.Metrics(35).MedianGapMs, d.Metrics(35).MaxDragsPerRepaint), Is.EqualTo((10d, 3)));
            d.Observe(Point(40, EventType.Repaint, repaint: 2));
            for (int i = 0; i < 4; i++) d.Observe(Point(50 + i, EventType.MouseDrag, repaint: 2));
            Assert.That(d.Metrics(60).MaxDragsPerRepaint, Is.EqualTo(4));
        }

        [Test] public void ContactsPointerKindsButtonsAndHoverDoNotBridgeGaps()
        {
            var d = new PenInputDiagnostics();
            d.Observe(Point(0, EventType.MouseDrag, .25f, 1));
            d.Observe(Point(10, EventType.MouseUp));
            d.Observe(Point(300, EventType.MouseDrag, .5f, 100));
            d.Observe(Point(310, EventType.MouseDrag, .6f, 200, pointer: PointerType.Mouse));
            d.Observe(Point(320, EventType.MouseDrag, .7f, 300, pointer: PointerType.Mouse, button: 1));
            d.Observe(Point(325, EventType.MouseMove, .8f, 500, pointer: PointerType.Mouse));
            d.Observe(Point(330, EventType.MouseDrag, .8f, 400, pointer: PointerType.Mouse, button: 1));
            var m = d.Metrics(340);
            Assert.That((m.Drags, m.GapCount, m.MaxStep), Is.EqualTo((5, 0, 0d)));
            Assert.That((m.PressureLevels, m.MinPressure, m.MaxPressure), Is.EqualTo((3, .6f, .8f)), "pressure range belongs to the latest pointer kind");
        }

        [Test] public void DistinctPressuresAreNotRoundedOrClamped()
        {
            var d = new PenInputDiagnostics();
            foreach (float p in new[] { 0f, .5f, .500001f, .5f, 1.1f, float.NaN }) d.Observe(Point(1, EventType.MouseMove, p));
            var m = d.Metrics(2);
            Assert.That((m.PressureLevels, m.MinPressure, m.MaxPressure), Is.EqualTo((4, 0f, 1.1f)));
        }

        [Test] public void RecordingStopsExactlyAtThirtySecondsWithoutAnotherPointerEvent()
        {
            var d = new PenInputDiagnostics(); d.StartRecording(200);
            d.Observe(Point(201, EventType.MouseDrag)); d.Observe(Point(30199, EventType.Repaint));
            d.Advance(30200); Assert.That(d.Recording, Is.False);
            d.Observe(Point(30201, EventType.MouseDrag));
            Assert.That(d.RecordedCount, Is.EqualTo(2));
            Assert.That(d.RecordedDurationMs(40000), Is.EqualTo(30000));
        }

        [Test] public void DisabledRecordingIsEmptyAndStopLeavesRawValuesForExport()
        {
            var d = new PenInputDiagnostics(); d.Observe(Point(1, EventType.MouseDrag));
            Assert.That(d.RecordedCount, Is.Zero);
            d.StartRecording(2); d.Observe(Point(4, EventType.MouseDrag)); d.StopRecording();
            d.Observe(Point(5, EventType.MouseDrag));
            Assert.That(d.RecordedCount, Is.EqualTo(1)); Assert.That(d.Recorded[0].TimeMs, Is.EqualTo(4));
            d.DiscardRecording(); Assert.That(d.HasRecording, Is.False);
        }

        [Test] public void NumericCsvRoundTripsAllRawFieldsIndependentOfCulture()
        {
            var d = new PenInputDiagnostics(); d.StartRecording(100);
            d.Observe(Point(123.456, EventType.MouseDrag, .500001f, 123.125f, 42, button: -1));
            var previous = CultureInfo.CurrentCulture;
            try
            {
                var culture = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone(); culture.NumberFormat.NegativeSign = "~";
                CultureInfo.CurrentCulture = culture;
                var writer = new StringWriter(); d.WriteCsv(writer);
                string line = writer.ToString(); var cells = line.Trim().Split(',');
                Assert.That(cells.Length, Is.EqualTo(16));
                var values = cells.Select(c => double.Parse(c, CultureInfo.InvariantCulture)).ToArray();
                var e = d.Recorded[0];
                Assert.That(values, Is.EqualTo(new[] { e.TimeMs - 100, (double)e.Type, (double)e.RawType, (double)e.Pointer,
                    e.Position.x, e.Position.y, e.Delta.x, e.Delta.y, e.Pressure, e.Tilt.x, e.Tilt.y, e.Twist,
                    (double)e.Pen, (double)e.Modifiers, e.Button, e.Repaint }).Within(.00000005));
                Assert.That(line, Does.Not.Contain("Pen").And.Not.Contain("Logs").And.Not.Contain("/"));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Test] public void BothMemoryLimitsAreVisibleAndRecordingDoesNotSilentlyOverwrite()
        {
            var d = new PenInputDiagnostics(); d.StartRecording(0);
            for (int i = 0; i < PenInputDiagnostics.MaxRecordedEvents + 2; i++) d.Observe(Point(i / 10000d, EventType.MouseDrag));
            Assert.That(d.Recording, Is.False); Assert.That(d.RecordingLimited, Is.True);
            Assert.That(d.RecordedCount, Is.EqualTo(PenInputDiagnostics.MaxRecordedEvents));
            Assert.That(d.Recorded[0].TimeMs, Is.Zero); Assert.That(d.Metrics(30).Limited, Is.True);
            Assert.That(d.Metrics(1100).Limited, Is.False);
        }
    }
}
