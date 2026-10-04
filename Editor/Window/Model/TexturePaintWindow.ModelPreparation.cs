using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor.Preview;
namespace Yozolab.YoluPainter.Editor
{
    public sealed partial class TexturePaintWindow
    {
        bool wasPreparing;
        bool previewDisplayPending, previewDisplayCanceled;
        int previewDisplayRemaining;
        readonly System.Diagnostics.Stopwatch previewDisplayClock = new System.Diagnostics.Stopwatch();
        double previewDisplayBudget = double.PositiveInfinity;
        void BeginPreviewDisplayFrame(CompositeSchedule schedule)
        {
            previewDisplayClock.Restart(); previewDisplayPending = false; previewDisplayRemaining = 0;
            previewDisplayBudget = schedule == null ? double.PositiveInfinity : schedule.BudgetMilliseconds;
        }
        bool AdmitPreviewDisplayWork()
        {
            if (!previewDisplayCanceled && previewDisplayClock.Elapsed.TotalMilliseconds < previewDisplayBudget) return true;
            previewDisplayRemaining++;
            if (!previewDisplayCanceled) previewDisplayPending = true;
            return false;
        }
        internal bool PreviewDisplayPending => previewDisplayPending;
        internal void CancelPreviewDisplayPreparation() { previewDisplayCanceled = true; previewDisplayPending = false; }
        internal void ResumePreviewDisplayPreparation() { previewDisplayCanceled = false; repaintPixels = true; Repaint(); }

        IsolatedModelPreview bakePreparedModel;
        void TickModelPreparation()
        {
            bool preparing = preview != null && preview.IsPreparing;
            if (preparing || wasPreparing) { Repaint(); if (!preparing) repaintPixels = true; }
            wasPreparing = preparing;
            if (bakePreparedModel != null && !bakePreparedModel.IsPreparing)
            {
                var requested = bakePreparedModel; bakePreparedModel = null;
                if (ReferenceEquals(requested, preview) && preview.CanPaint) TryAction(() => BakeMeshMaps());
            }
        }
        void DrawModelPreparation()
        {
            if (preview == null || surfaceRect.width <= 0 || (!preview.IsPreparing && !preview.PreparationCanceled && !previewDisplayPending && !previewDisplayCanceled)) return;
            var box = new Rect(surfaceRect.x + 12, surfaceRect.y + 12, Mathf.Min(380, surfaceRect.width - 24), 76);
            PaintGui.Fill(box, PaintTheme.PanelHeader);
            string label = preview.IsPreparing ? L.Tr("Preparing model: {0}%", Mathf.RoundToInt(preview.PreparationProgress * 100))
                : preview.PreparationCanceled ? L.Tr("Model preparation canceled. 3D painting is unavailable.")
                : previewDisplayCanceled ? L.Tr("Texture set preview preparation canceled.")
                : L.Tr("Preparing texture set previews: {0} remaining", previewDisplayRemaining);
            PaintGui.Text(new Rect(box.x + 8, box.y + 4, box.width - 16, 24), label, PaintTheme.LabelDim);
            var button = new Rect(box.x + 8, box.y + 34, box.width - 16, 28);
            if (PaintGui.Button(button, preview.IsPreparing || previewDisplayPending ? L.Tr("Cancel preparation") : L.Tr("Prepare again")))
            {
                FinishStroke(false); CancelToolDrag();
                if (preview.IsPreparing) preview.CancelPreparation();
                else if (preview.PreparationCanceled) preview.BeginLoad(model);
                else if (previewDisplayPending) CancelPreviewDisplayPreparation(); else ResumePreviewDisplayPreparation();
            }
            var e = Event.current;
            if ((preview.IsPreparing || preview.PreparationCanceled) && e.type == EventType.MouseDown && e.button == 0 && !e.alt && surfaceRect.Contains(e.mousePosition))
            { message = L.Tr("The model is being prepared."); e.Use(); }
        }
    }
}
