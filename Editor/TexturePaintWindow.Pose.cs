using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>ポーズと BlendShape: スキンメッシュのあるモデルを読み込んだとき、プレビューの複製だけを動かす欄。元のモデルには触れない。
    /// スライダーを離したとき・クリップを当てたときに焼き直し、ストロークの最中は変えられない（形の世代が途中で変わらない）。</summary>
    public sealed partial class TexturePaintWindow
    {
        bool showPose, posePending; string poseFilter = ""; AnimationClip poseClip; float poseTime;
        const int MaxBlendShapeRows = 200;

        void DrawPosePanel()
        {
            if(preview==null||!preview.HasSkinnedMeshes)return;
            GUILayout.Space(6);
            showPose=EditorGUILayout.Foldout(showPose,"Pose & BlendShapes (preview only)",true);
            if(!showPose)return;
            using(new EditorGUI.DisabledScope(stroke!=null))
            {
                poseFilter=EditorGUILayout.TextField(new GUIContent("Filter","Show BlendShapes whose name contains this"),poseFilter);
                var shapes=preview.BlendShapes.Where(s=>String.IsNullOrEmpty(poseFilter)||s.Label.IndexOf(poseFilter,StringComparison.OrdinalIgnoreCase)>=0).ToList();
                foreach(var shape in shapes.Take(MaxBlendShapeRows))
                {
                    float w=preview.GetBlendShapeWeight(shape);
                    float next=EditorGUILayout.Slider(shape.Label,w,0,100);
                    if(next!=w){preview.SetBlendShapeWeight(shape,next);posePending=true;}
                }
                if(shapes.Count>MaxBlendShapeRows)EditorGUILayout.LabelField("… "+(shapes.Count-MaxBlendShapeRows)+" more; narrow the filter.",EditorStyles.miniLabel);
                poseClip=(AnimationClip)EditorGUILayout.ObjectField(new GUIContent("Clip","A Generic animation clip to pose the bones with (Humanoid clips are not supported yet)"),poseClip,typeof(AnimationClip),false);
                using(new EditorGUI.DisabledScope(poseClip==null))
                {
                    poseTime=EditorGUILayout.Slider("Time (s)",poseTime,0,poseClip!=null?Mathf.Max(0,poseClip.length):0);
                    if(GUILayout.Button("Pose from clip"))TryAction(()=>{preview.SamplePose(poseClip,poseTime);posePending=true;});
                }
                if(GUILayout.Button(new GUIContent("Reset pose","Back to the pose and BlendShapes the model had when it was loaded")))TryAction(()=>{preview.ResetPose();posePending=true;});
            }
            // スライダーを動かしている間は焼き直さず、離したときに 1 回だけ
            if(posePending&&GUIUtility.hotControl==0&&Event.current.type==EventType.Repaint)TryAction(ApplyPoseNow);
        }

        /// <summary>今のポーズと BlendShape で形を焼き直す（新しいスナップショットの世代）。ストロークの最中は断る。</summary>
        internal void ApplyPoseNow()
        {
            if(stroke!=null){message="Finish the stroke before changing the pose.";return;}
            posePending=false;
            if(preview.ApplyPose()){message="Pose applied to the preview (the model itself is unchanged). Painting follows the posed surface; the texture stays in UV space.";Repaint();}
        }
    }
}
