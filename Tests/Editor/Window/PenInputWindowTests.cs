using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed partial class WindowTests
    {
        static void PenMouse(TexturePaintWindow w, EventType type, Vector2 at, float pressure)
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            w.SendEvent(new Event { type = type, pointerType = PointerType.Pen,
                mousePosition = at + w.rootVisualElement.worldBound.position, delta = new Vector2(7, -3),
                pressure = pressure, tilt = new Vector2(.25f, -.5f), twist = .75f,
                penStatus = PenStatus.Contact | PenStatus.Barrel | PenStatus.Inverted,
                button = 0, modifiers = EventModifiers.Shift });
        }

        [Test] public void PenInputCapturesRawSendEventsBeforeBrushProcessingAndWritesNumericCsv()
        {
            var a = At(window, 250, 100); var b = At(window, 300, 100); var c = At(window, 350, 100);
            double now = 1000; window.PenInputClock = () => now;
            window.PenInputEnabled = true;
            var fake = UseFakeDialogs(window); fake.File = NewTempPath(".csv");
            window.StartPenInputRecording();
            PenMouse(window, EventType.MouseDown, a, .25f);
            now += 10; PenMouse(window, EventType.MouseDrag, b, .375f);
            now += 20; PenMouse(window, EventType.MouseDrag, c, .75f);
            now += 1; PenMouse(window, EventType.MouseUp, c, .5f);
            var m = window.CurrentPenInputMetrics;
            Assert.That((m.Drags, m.PressureLevels, m.GapCount, m.MedianGapMs, m.MaxGapMs), Is.EqualTo((2, 4, 2, 15d, 20d)));
            var raw = window.PenInput.Recorded.Where(e => e.Type == EventType.MouseDrag).ToArray();
            Assert.That(raw.Length, Is.EqualTo(2));
            Assert.That((raw[0].Pointer, raw[0].Pressure, raw[0].Position, raw[0].Delta, raw[0].Tilt, raw[0].Twist),
                Is.EqualTo((PointerType.Pen, .375f, b, new Vector2(7, -3), new Vector2(.25f, -.5f), .75f)));
            Assert.That(raw[0].Pen, Is.EqualTo(PenStatus.Contact | PenStatus.Barrel | PenStatus.Inverted));
            Assert.That(raw[0].Modifiers, Is.EqualTo(EventModifiers.Shift));
            Assert.That(window.Document.CanUndo, Is.True); Assert.That(window.IsStroking, Is.False);
            window.StopPenInputRecording(); Invoke(window, "UpdatePenInput");
            Assert.That(fake.Asked.Count(s => s == "SaveFile"), Is.EqualTo(1));
            Assert.That(fake.LastFolder, Is.EqualTo(Path.GetFullPath(Path.Combine(PainterSettings.ProjectRoot, "Logs", "YoluPainter"))));
            var csv = File.ReadAllLines(fake.File).Select(l => l.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToArray();
            Assert.That(csv.All(row => row.Length == 16), Is.True);
            var drags = csv.Where(row => row[1] == (int)EventType.MouseDrag).ToArray();
            Assert.That((drags[0][0], drags[1][0], drags[0][3], drags[0][8], drags[0][15]),
                Is.EqualTo((10d, 30d, (double)PointerType.Pen, .375d, (double)raw[0].Repaint)));
            Assert.That(window.PenInput.HasRecording, Is.False);
        }

        [Test] public void PenInputOffReadsNothingAndTogglingDoesNotAlterStrokeOrUndo()
        {
            var a = At(window, 250, 100); var b = At(window, 330, 100); var before = Snapshot();
            PenMouse(window, EventType.MouseDown, a, .5f); PenMouse(window, EventType.MouseDrag, b, .75f); PenMouse(window, EventType.MouseUp, b, .75f);
            var painted = Snapshot(); Assert.That(window.PenInput, Is.Null);
            Key(window, KeyCode.Z, EventModifiers.Control); Assert.That(Snapshot(), Is.EqualTo(before));
            window.PenInputEnabled = true; window.StartPenInputRecording();
            var fake = UseFakeDialogs(window); fake.File = "";
            PenMouse(window, EventType.MouseDown, a, .5f); PenMouse(window, EventType.MouseDrag, b, .75f);
            var d = window.PenInput; window.PenInputEnabled = false;
            int count = d.RecordedCount; Assert.That(window.IsStroking, Is.True);
            Invoke(window, "UpdatePenInput"); Assert.That(fake.Asked, Is.Empty, "save waits for the active stroke");
            PenMouse(window, EventType.MouseUp, b, .75f); Repaint(window);
            Assert.That(d.RecordedCount, Is.EqualTo(count), "off adds no samples, including repaint");
            Assert.That(Snapshot(), Is.EqualTo(painted));
            Key(window, KeyCode.Z, EventModifiers.Control); Assert.That(Snapshot(), Is.EqualTo(before));
            Invoke(window, "UpdatePenInput"); Assert.That(fake.Asked.Count(s => s == "SaveFile"), Is.EqualTo(1));
        }

        [Test] public void PenInputTimeoutWaitsForReleaseAndCancelledOrFailedExportsCanBeRetried()
        {
            var a = At(window, 250, 100); var b = At(window, 330, 100);
            double now = 0; window.PenInputClock = () => now; window.PenInputEnabled = true;
            var fake = UseFakeDialogs(window); window.StartPenInputRecording();
            PenMouse(window, EventType.MouseDown, a, .5f); now = 1; PenMouse(window, EventType.MouseDrag, b, .75f);
            now = 30000; Invoke(window, "UpdatePenInput");
            Assert.That(window.PenInput.Recording, Is.False); Assert.That(fake.Asked, Is.Empty);
            Assert.That(window.IsStroking, Is.True);
            PenMouse(window, EventType.MouseUp, b, .75f); Invoke(window, "UpdatePenInput");
            Assert.That(fake.Asked.Count(s => s == "SaveFile"), Is.EqualTo(1));
            Assert.That(window.PenInput.HasRecording, Is.True, "cancel keeps raw data");
            var writer = new StringWriter(); window.PenInput.WriteCsv(writer); string expected = writer.ToString();
            fake.File = Path.Combine(NewTempPath(), "missing.csv"); window.SavePenInputRecording();
            Assert.That(window.PenInput.HasRecording, Is.True, "write failure keeps raw data");
            fake.File = NewTempPath(".csv"); File.WriteAllText(fake.File, "old capture"); window.SavePenInputRecording();
            Assert.That(File.ReadAllText(fake.File), Is.EqualTo(expected));
            Assert.That(window.PenInput.HasRecording, Is.False);
        }

        [Test] public void PenInputPanelButtonsRecordAndStopWithoutPaintingTheCanvas()
        {
            double now = 0; window.PenInputClock = () => now; window.PenInputEnabled = true;
            var fake = UseFakeDialogs(window); Repaint(window);
            var before = window.Document.Revision;
            void Click(Vector2 at) { Mouse(window, EventType.MouseDown, at); now += 1; Mouse(window, EventType.MouseUp, at); }
            Click(window.PenInputRecordRect.center);
            Assert.That(window.PenInput.Recording, Is.True);
            now += 5; Click(window.PenInputRecordRect.center);
            Assert.That(window.PenInput.Recording, Is.False);
            Invoke(window, "UpdatePenInput");
            Assert.That(window.PenInput.HasRecording, Is.True);
            fake.File = NewTempPath(".csv"); Click(window.PenInputSaveRect.center);
            Assert.That(File.Exists(fake.File), Is.True);
            Assert.That(window.Document.Revision, Is.EqualTo(before)); Assert.That(window.Document.CanUndo, Is.False);
        }

        [Test] public void PenInputToggleIsWindowStateAndDoesNotEnterYlpOrBrushSettings()
        {
            window.PenInputEnabled = true;
            Assert.That(EditorJsonUtility.ToJson(window), Does.Contain("\"penInputEnabled\":true"));
            var fake = UseFakeDialogs(window); fake.File = NewYlpPath(); window.SaveProject(true);
            var files = YlpStore.Load(fake.File).Files;
            Assert.That(System.Text.Encoding.UTF8.GetString(files["view.json"]), Does.Not.Contain("penInput"));
            Assert.That(System.Text.Encoding.UTF8.GetString(files["brush.json"]), Does.Not.Contain("penInput"));
            var other = ScriptableObject.CreateInstance<TexturePaintWindow>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"penInputEnabled\":true}", other); Invoke(other, "RestorePenInput");
                Assert.That(other.PenInputEnabled, Is.True); Assert.That(other.PenInput, Is.Not.Null);
                Assert.That(other.PenInput.RecordedCount, Is.Zero, "samples are session-only");
            }
            finally
            {
                string recovery = other.RecoveryRoot; UnityEngine.Object.DestroyImmediate(other);
                if (Directory.Exists(recovery)) Directory.Delete(recovery, true);
            }
        }
    }
}
