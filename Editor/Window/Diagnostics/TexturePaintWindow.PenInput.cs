using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] bool penInputEnabled;
        [NonSerialized] PenInputDiagnostics penInput;
        bool penInputSaveRequested, penInputSaving;
        double penInputLastUpdate = double.NegativeInfinity;
        PenInputMetrics penInputMetrics;
        string penInputNote;
        string[] penInputPanelRows;
        GUIStyle penInputPanelStyle;
        internal Func<double> PenInputClock;
        double PenInputNow => PenInputClock != null ? PenInputClock() : Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);
        internal PenInputDiagnostics PenInput => penInput;
        internal PenInputMetrics CurrentPenInputMetrics => penInput?.Metrics(PenInputNow) ?? default;
        internal Rect PenInputPanelRect { get; private set; }
        internal Rect PenInputRecordRect { get; private set; }
        internal Rect PenInputSaveRect { get; private set; }
        internal Rect PenInputDiscardRect { get; private set; }

        internal bool PenInputEnabled
        {
            get => penInputEnabled;
            set
            {
                if (penInputEnabled == value) return;
                penInputEnabled = value;
                if (value) EnablePenInput();
                else if (penInput != null) { if (penInput.Recording) StopPenInputRecording(); penInput.ResetMeasurements(); }
                if (!value && !penInputSaveRequested) EditorApplication.update -= UpdatePenInput;
                Repaint();
            }
        }

        void EnablePenInput()
        {
            if (penInput == null) penInput = new PenInputDiagnostics();
            penInputLastUpdate = double.NegativeInfinity; penInputMetrics = default;
            EditorApplication.update -= UpdatePenInput;
            EditorApplication.update += UpdatePenInput;
        }
        void RestorePenInput() { if (penInputEnabled) EnablePenInput(); }
        void DisposePenInput() { EditorApplication.update -= UpdatePenInput; penInput = null; penInputSaveRequested = false; }
        void ObservePenInput(Event e) => penInput.Observe(e, PenInputNow);

        // 計器の更新は最大 10 Hz。記録の上限に達しても、ストローク/操作の捕捉を手放すまで保存のダイアログを出さない。
        void UpdatePenInput()
        {
            if (penInput == null || penInputSaving) return;
            double now = PenInputNow; bool wasRecording = penInput.Recording;
            penInput.Advance(now);
            if (wasRecording && !penInput.Recording) penInputSaveRequested = true;
            if (penInputSaveRequested && !PenInputInteractionActive) SavePenInputRecording();
            if (penInputEnabled && now - penInputLastUpdate >= 100)
            { penInputMetrics = penInput.Metrics(now); penInputLastUpdate = now; Repaint(); }
            if (!penInputEnabled && !penInputSaveRequested) EditorApplication.update -= UpdatePenInput;
        }
        bool PenInputInteractionActive => stroke != null || toolDragging || ShapeDragging || GUIUtility.hotControl != 0;

        internal void StartPenInputRecording()
        {
            if (!penInputEnabled || penInput == null || penInput.Recording || penInput.HasRecording || PenInputInteractionActive) return;
            penInput.StartRecording(PenInputNow); penInputSaveRequested = false; penInputNote = null; Repaint();
        }
        internal void StopPenInputRecording()
        {
            if (penInput == null || !penInput.Recording) return;
            penInput.StopRecording(); penInputSaveRequested = penInput.HasRecording;
            // 自動停止が先に入口で起きた場合も Observe 側から要求を立てる。
            Repaint();
        }
        internal void SavePenInputRecording()
        {
            if (penInput == null || penInput.Recording || !penInput.HasRecording || PenInputInteractionActive || penInputSaving) return;
            penInputSaveRequested = false; penInputSaving = true;
            string temporary = null;
            try
            {
                string folder = Path.GetFullPath(Path.Combine(PainterSettings.ProjectRoot, "Logs", "YoluPainter"));
                Directory.CreateDirectory(folder);
                string path = Dialogs.SaveFile(L.Tr("Save Pen Input CSV"), folder, "pen-input", "csv");
                if (string.IsNullOrEmpty(path)) return;
                temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), ".pen-input-" + Guid.NewGuid().ToString("N") + "~");
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false))) penInput.WriteCsv(writer);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                temporary = null;
                bool limited = penInput.RecordingLimited;
                penInput.DiscardRecording(); penInputNote = limited ? L.Tr("CSV saved · event limit reached") : L.Tr("CSV saved");
            }
            catch (Exception ex) { penInputNote = string.Format(L.Tr("CSV could not be saved: {0}"), ex.Message); }
            finally
            {
                if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                penInputSaving = false; Repaint();
            }
        }

        // 窓全体に来るイベントを読む。Layout/Repaint/キーも数値 CSV に含め、点の集計はマウス系の座標イベントだけ。
        void CapturePenInput(Event e)
        {
            bool wasRecording = penInput.Recording;
            ObservePenInput(e);
            if (wasRecording && !penInput.Recording) penInputSaveRequested = true;
        }

        internal string[] PenInputRows(PenInputMetrics metrics)
        {
            var latest = penInput?.Latest;
            string pair(double x, double y) => Number(x) + " / " + Number(y);
            return new[] {
                L.Tr("Pointer") + "|" + (latest.HasValue ? PointerName(latest.Value.Pointer) : "—"),
                L.Tr("Pressure now") + "|" + (latest.HasValue ? Number(latest.Value.Pressure) : "—"),
                L.Tr("Pressure min / max") + "|" + (metrics.PressureLevels > 0 ? pair(metrics.MinPressure, metrics.MaxPressure) : "—"),
                L.Tr("Pressure levels") + "|" + metrics.PressureLevels.ToString(CultureInfo.InvariantCulture),
                L.Tr("Tilt x / y (rad)") + "|" + (latest.HasValue ? pair(latest.Value.Tilt.x, latest.Value.Tilt.y) : "—"),
                L.Tr("Twist (rad)") + "|" + (latest.HasValue ? Number(latest.Value.Twist) : "—"),
                L.Tr("Pen status") + "|" + (latest.HasValue ? ((int)latest.Value.Pen).ToString(CultureInfo.InvariantCulture) : "—"),
                L.Tr("MouseDrag / s") + "|" + metrics.Drags.ToString(CultureInfo.InvariantCulture),
                L.Tr("Gap median / max") + "|" + (metrics.GapCount > 0 ? pair(metrics.MedianGapMs, metrics.MaxGapMs) + " ms" : "—"),
                L.Tr("Drags / repaint max") + "|" + metrics.MaxDragsPerRepaint.ToString(CultureInfo.InvariantCulture),
                L.Tr("Step max (GUI pt)") + "|" + Number(metrics.MaxStep) };
        }
        static string Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? "—" : value.ToString("0.###", CultureInfo.InvariantCulture);
        static string PointerName(PointerType pointer)
        {
            switch (pointer)
            { case PointerType.Mouse: return L.Tr("Mouse"); case PointerType.Pen: return L.Tr("Pen"); case PointerType.Touch: return L.Tr("Touch"); default: return ((int)pointer).ToString(CultureInfo.InvariantCulture); }
        }
        internal static string PenStatusName(PenStatus status)
        {
            var names = new List<string>();
            if ((status & PenStatus.Contact) != 0) names.Add(L.Tr("Contact"));
            if ((status & PenStatus.Barrel) != 0) names.Add(L.Tr("Side button"));
            if ((status & PenStatus.Eraser) != 0) names.Add(L.Tr("Eraser end"));
            if ((status & PenStatus.Inverted) != 0) names.Add(L.Tr("Inverted"));
            return names.Count > 0 ? string.Join(" · ", names) : "—";
        }

        void DrawPenInputPanel()
        {
            // 幅の狭い側ではもう片方のビューへ置く。並べる表示の比率や左右の入れ替えにも付いて行く。
            Rect area = canvasRect.width >= 296 ? canvasRect : surfaceRect.width > canvasRect.width ? surfaceRect : canvasRect;
            float width = Mathf.Min(336, area.width - 16);
            if (width < 160 || area.height < 200) return;
            float height = Mathf.Min(336, area.height - 16);
            var panel = new Rect(area.x + 8, area.y + 8, width, height); PenInputPanelRect = panel;
            PaintGui.Rounded(panel, PaintTheme.PanelBg); PaintGui.Outline(panel, PaintTheme.Border);
            PaintGui.Text(new Rect(panel.x + 10, panel.y + 4, width - 20, 22), L.Tr("Pen Input · last 1 s"), PaintTheme.LabelBold);
            PaintGui.Tooltip(panel, L.Tr("Reads events arriving at this window before brush processing. Pressure levels are exact values of the latest pointer type. Gaps and steps are Down→Drag or Drag→Drag in one contact. Drags per repaint also includes the current unfinished frame. This cannot count samples the OS or Unity dropped."));
            if (Event.current.type == EventType.Repaint || penInputPanelRows == null) penInputPanelRows = PenInputRows(penInputMetrics);
            var rows = penInputPanelRows; float y = panel.y + 28;
            float labelWidth = Mathf.Min(145, (width - 20) * .53f);
            if (penInputPanelStyle == null) penInputPanelStyle = new GUIStyle(PaintTheme.LabelDim);
            var style = penInputPanelStyle; style.fontSize = width < 280 ? 9 : 11;
            for (int i = 0; i < rows.Length; i++)
            {
                if (Event.current.type == EventType.Repaint)
                {
                    var parts = rows[i].Split('|');
                    PaintGui.Text(new Rect(panel.x + 10, y, labelWidth, 20), parts[0], style);
                    PaintGui.Text(new Rect(panel.x + 14 + labelWidth, y, width - 24 - labelWidth, 20), parts[1], style, PaintTheme.Text);
                }
                y += 20;
                if (i == 6)
                {
                    if (Event.current.type == EventType.Repaint)
                    {
                        string status = penInput.Latest.HasValue ? PenStatusName(penInput.Latest.Value.Pen) : "—";
                        PaintGui.Text(new Rect(panel.x + 10, y, width - 20, 20), status, style);
                    }
                    y += 20;
                }
            }
            string note = penInput.Recording ? string.Format(L.Tr("Recording {0:0.0} / 30 s · {1} events"), penInput.RecordedDurationMs(PenInputNow) / 1000, penInput.RecordedCount)
                : penInput.HasRecording ? string.Format(L.Tr("{0} events ready to save"), penInput.RecordedCount) : penInputNote ?? L.Tr("Record up to 30 s");
            if (penInput.RecordingLimited || penInputMetrics.Limited) note = L.Tr("Event limit reached · incomplete");
            PaintGui.Text(new Rect(panel.x + 10, y + 2, width - 20, 20), note, style);
            PaintGui.Tooltip(new Rect(panel.x + 10, y + 2, width - 20, 20), note);
            y += 28;
            float third = (width - 28) / 3;
            PenInputRecordRect = new Rect(panel.x + 10, y, third, 24);
            PenInputSaveRect = new Rect(PenInputRecordRect.xMax + 4, y, third, 24);
            PenInputDiscardRect = new Rect(PenInputSaveRect.xMax + 4, y, third, 24);
            bool idle = stroke == null && !toolDragging && !ShapeDragging;
            if (PaintGui.Button(PenInputRecordRect, penInput.Recording ? L.Tr("Stop") : L.Tr("Record"), penInput.Recording,
                idle && (penInput.Recording || !penInput.HasRecording)))
            { if (penInput.Recording) StopPenInputRecording(); else StartPenInputRecording(); }
            if (PaintGui.Button(PenInputSaveRect, L.Tr("Save CSV…"), enabled: idle && !penInput.Recording && penInput.HasRecording)) SavePenInputRecording();
            if (PaintGui.Button(PenInputDiscardRect, L.Tr("Discard"), enabled: idle && !penInput.Recording && penInput.HasRecording))
            { penInput.DiscardRecording(); penInputSaveRequested = false; penInputNote = null; Repaint(); }
            // 計器の上で押しても、その下のキャンバスへ描き始めない。進行中のストロークはパネルを横切ってもそのまま通す。
            var e = Event.current;
            if (idle && panel.Contains(e.mousePosition) && (e.type == EventType.MouseDown || e.type == EventType.ScrollWheel)) e.Use();
        }
    }
}
