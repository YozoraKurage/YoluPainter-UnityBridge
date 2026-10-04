using System;
using System.Linq;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>ポーズと BlendShape: スキンメッシュのあるモデルを読み込んだとき、プレビューの複製だけを動かす欄。元のモデルには触れない。
    /// スライダーを離したとき・クリップを当てたときに焼き直し、ストロークの最中は変えられない（形の世代が途中で変わらない）。</summary>
    public sealed partial class TexturePaintWindow
    {
        bool posePending; string poseFilter = ""; AnimationClip poseClip; float poseTime;
        const int MaxBlendShapeRows = 200;
        /// <summary>アニメーションクリップを選ぶオブジェクトピッカーの印。</summary>
        const int PoseClipPickerId = 0x59500011;

        void DrawPosePanel(UiRows rows)
        {
            bool was = GUI.enabled; GUI.enabled = was && stroke == null;
            try
            {
                poseFilter = PaintGui.SearchField(rows.Row(), poseFilter, L.Tr("Filter BlendShapes"), L.Tr("Show BlendShapes whose name contains this"));
                var shapes = preview.BlendShapes.Where(s => String.IsNullOrEmpty(poseFilter) || s.Label.IndexOf(poseFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (preview.BlendShapes.Count == 0) NoteRow(rows, L.Tr("This model has no BlendShapes."));
                else if (shapes.Count == 0) NoteRow(rows, L.Tr("No BlendShape matches the filter."));
                foreach (var shape in shapes.Take(MaxBlendShapeRows))
                {
                    float w = preview.GetBlendShapeWeight(shape);
                    double next = PaintGui.KeepSlider(Spot("pose.shape." + shape.Label, rows.SliderRow()), shape.Label, w, 0, 100, "0.#", "", null, labelIsData: true);
                    if (next != w) { preview.SetBlendShapeWeight(shape, (float)next); posePending = true; }
                }
                if (shapes.Count > MaxBlendShapeRows) PaintGui.Text(rows.Row(18), L.Tr("… {0} more.", shapes.Count - MaxBlendShapeRows), PaintTheme.LabelDim);
                PaintGui.GroupLabel(rows.Row(16), L.Tr("Animation Clip"));
                var clip = poseClip;
                if (PaintGui.ObjectBox(rows.Row(), ref clip, PoseClipPickerId, false, L.Tr("None"), "animation",
                        L.Tr("An animation clip to pose the bones with: Generic clips follow bone paths, Humanoid clips use the model's Avatar"), L.Tr("Stop using the clip")))
                    poseClip = clip;
                poseTime = (float)PaintGui.KeepSlider(rows.SliderRow(), L.TrIn("pose", "Time"), poseTime, 0, poseClip != null ? Mathf.Max(0, poseClip.length) : 0, "0.00", " s", L.Tr("The moment of the clip to pose with (seconds)"), poseClip != null);
                var c = UiRows.Split(rows.Row(), 2, 6);
                if (PaintGui.FitButton(c[0], L.Tr("Pose from Clip"), false, GUI.enabled && poseClip != null, L.Tr("Pose the preview with the clip at this time")))
                    TryAction(() => { preview.SamplePose(poseClip, poseTime); posePending = true; });
                if (PaintGui.FitButton(Spot("pose.reset", c[1]), L.Tr("Reset Pose"), false, GUI.enabled, L.Tr("Back to the pose and BlendShapes the model had when it was loaded")))
                    TryAction(() => { preview.ResetPose(); posePending = true; });
            }
            finally { GUI.enabled = was; }
        }

        /// <summary>スライダーを動かしている間は焼き直さず、離したときに 1 回だけ（欄を閉じていても）。</summary>
        void ApplyPendingPose()
        {
            if (posePending && GUIUtility.hotControl == 0 && Event.current.type == EventType.Repaint) TryAction(ApplyPoseNow);
        }

        /// <summary>今のポーズと BlendShape で形を焼き直す（新しいスナップショットの世代）。ストロークの最中は断る。</summary>
        internal void ApplyPoseNow()
        {
            if (stroke != null) { message = L.Tr("A stroke is in progress."); return; }
            posePending = false;
            if (preview.ApplyPose()) { message = L.Tr("Pose applied to the preview."); Repaint(); }
        }
    }
}
