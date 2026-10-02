using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>プロパティの欄のうち、ツールとブラシの設定（ToolSections）。</summary>
    public sealed partial class TexturePaintWindow
    {
        /// <summary>今のツールの詳しい設定。まだ Unity の標準の部品のものは LegacySection で包んでいる。</summary>
        void ToolSections(UiRows rows)
        {
            switch (tool)
            {
                case PaintTool.Brush: LegacySection(rows, "brush", L.Tr("Brush"), DrawBrushDetails); break;
                case PaintTool.Fill: LegacySection(rows, "surface-pick", L.Tr("3D Pick"), DrawSurfacePick); break;
                case PaintTool.SelectRectangle: case PaintTool.SelectEllipse: case PaintTool.Lasso: case PaintTool.MagicWand:
                    LegacySection(rows, "surface-pick", L.Tr("3D Pick"), DrawSurfacePick);
                    LegacySection(rows, "selection-modify", L.Tr("Modify Selection"), DrawSelectionModify); break;
                case PaintTool.Move: LegacySection(rows, "move", L.Tr("Transform"), DrawMoveSettings); break;
                case PaintTool.Path: LegacySection(rows, "path", L.Tr("Path"), DrawPathSettings); break;
            }
        }

        void DrawSelectionModify()
        {
            using(new EditorGUI.DisabledScope(document.Selection==null))
            {
                GUILayout.BeginHorizontal();
                selectionRadius=Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Modify (px)","Radius for Grow, Shrink, Border and Feather (as GIMP's Select menu)"),selectionRadius),0,SelectionMask.MaxModifyRadius);
                selectionEdgeLock=GUILayout.Toggle(selectionEdgeLock,new GUIContent("Edge lock","Selected areas continue outside the canvas (Shrink, Border and Feather do not pull away from the canvas edge)"),EditorStyles.miniButton,GUILayout.Width(70));
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if(GUILayout.Button(new GUIContent("Grow","Largest amount within a circle of the radius"),EditorStyles.miniButtonLeft))TryAction(()=>ModifySelection(SelectionModifyKind.Grow));
                if(GUILayout.Button(new GUIContent("Shrink","Smallest amount within a circle of the radius"),EditorStyles.miniButtonMid))TryAction(()=>ModifySelection(SelectionModifyKind.Shrink));
                if(GUILayout.Button(new GUIContent("Border","A band around the edge: Grow minus Shrink"),EditorStyles.miniButtonMid))TryAction(()=>ModifySelection(SelectionModifyKind.Border));
                if(GUILayout.Button(new GUIContent("Feather","Soften the edge (Gaussian blur, σ = radius / 3.5)"),EditorStyles.miniButtonMid))TryAction(()=>ModifySelection(SelectionModifyKind.Feather));
                if(GUILayout.Button(new GUIContent("Sharpen","Hard edge: at least half selected becomes fully selected"),EditorStyles.miniButtonRight))TryAction(()=>ModifySelection(SelectionModifyKind.Sharpen));
                GUILayout.EndHorizontal();
            }
        }
        void DrawMoveSettings()
        {
            EditorGUILayout.LabelField("Drag or arrow keys (Shift: 10 px) move the layer and its mask, or the selected pixels with the selection.",EditorStyles.wordWrappedMiniLabel);
            GUILayout.BeginHorizontal();
            if(GUILayout.Button(new GUIContent("Flip H","Mirror left-right about the centre"),EditorStyles.miniButtonLeft))TryAction(()=>TransformSelected(0,0,0,-1,1,"Flipped horizontally."));
            if(GUILayout.Button(new GUIContent("Flip V","Mirror top-bottom about the centre"),EditorStyles.miniButtonMid))TryAction(()=>TransformSelected(0,0,0,1,-1,"Flipped vertically."));
            if(GUILayout.Button(new GUIContent("+90°","Rotate 90° counter-clockwise"),EditorStyles.miniButtonMid))TryAction(()=>TransformSelected(0,0,90,1,1,"Rotated 90° counter-clockwise."));
            if(GUILayout.Button(new GUIContent("-90°","Rotate 90° clockwise"),EditorStyles.miniButtonRight))TryAction(()=>TransformSelected(0,0,-90,1,1,"Rotated 90° clockwise."));
            GUILayout.EndHorizontal();
            moveAngle=EditorGUILayout.FloatField(new GUIContent("Rotate (°)","Counter-clockwise, about the centre of what moves"),moveAngle);
            moveScale=EditorGUILayout.Vector2Field(new GUIContent("Scale (%)","Negative flips"),moveScale);
            moveOffset=EditorGUILayout.Vector2Field("Offset (px)",moveOffset);
            moveResampling=(Resampling)EditorGUILayout.EnumPopup(new GUIContent("Resampling","Bilinear smooths, Nearest keeps hard pixels. Whole-pixel moves, 90° turns and flips copy pixels exactly either way."),moveResampling);
            GUILayout.BeginHorizontal();
            if(GUILayout.Button("Apply"))TryAction(ApplyNumericTransform);
            if(GUILayout.Button("Reset",GUILayout.Width(60))){moveAngle=0;moveScale=new Vector2(100,100);moveOffset=Vector2.zero;}
            GUILayout.EndHorizontal();
        }

        void DrawBrushDetails()
        {
            using (new EditorGUI.DisabledScope(stroke != null))
            {
                GUILayout.Label(L.Tr("Brush"), EditorStyles.boldLabel);
                if (channel == PaintChannel.Roughness || channel == PaintChannel.Metallic || channel == PaintChannel.Height)
                { float scalar = EditorGUILayout.Slider(L.Tr("Value"), brush.color.r, 0, 1); brush.color = new Color(scalar, scalar, scalar, brush.color.a); }
                brush.spacing = EditorGUILayout.Slider(L.Tr("Spacing"), brush.spacing, .01f, 1);
                brush.pressureFlow = EditorGUILayout.Toggle(L.Tr("Pressure flow"), brush.pressureFlow);
                brush.pressureCurve = EditorGUILayout.CurveField(L.Tr("Pressure curve"), brush.pressureCurve);
                DrawStrokeAssist();
                showDynamics = EditorGUILayout.Foldout(showDynamics, L.Tr("Tip & dynamics"), true);
                if (showDynamics)
                {
                    EditorGUILayout.LabelField(L.Tr("Tip"), string.IsNullOrEmpty(brush.tipId) ? L.Tr("Round (hardness)") : BrushTips.ResolveRef(brush.tipId) == null ? L.Tr("Missing") + ": " + brush.tipId : brush.tipId);
                    brush.angle = EditorGUILayout.Slider(L.Tr("Angle"), brush.angle, -180, 180);
                    brush.roundness = EditorGUILayout.Slider(L.Tr("Roundness"), brush.roundness, .01f, 1);
                    brush.followDirection = EditorGUILayout.Toggle(L.Tr("Follow direction"), brush.followDirection);
                    brush.sizeJitter = EditorGUILayout.Slider(L.Tr("Size jitter"), brush.sizeJitter, 0, 1);
                    brush.angleJitter = EditorGUILayout.Slider(L.Tr("Angle jitter"), brush.angleJitter, 0, 1);
                    brush.roundnessJitter = EditorGUILayout.Slider(L.Tr("Roundness jitter"), brush.roundnessJitter, 0, 1);
                    brush.opacityJitter = EditorGUILayout.Slider(L.Tr("Opacity jitter"), brush.opacityJitter, 0, 1);
                    brush.flowJitter = EditorGUILayout.Slider(L.Tr("Flow jitter"), brush.flowJitter, 0, 1);
                    brush.scatter = EditorGUILayout.Slider(L.Tr("Scatter"), brush.scatter, 0, 10);
                    brush.count = EditorGUILayout.IntSlider(L.Tr("Count"), brush.count, 1, 16);
                    EditorGUILayout.LabelField(L.Tr("Texture"), string.IsNullOrEmpty(brush.textureId) ? L.Tr("None") : BrushTips.ResolveRef(brush.textureId) == null ? L.Tr("Missing") + ": " + brush.textureId : brush.textureId);
                    if (!string.IsNullOrEmpty(brush.textureId))
                    {
                        brush.textureDepth = EditorGUILayout.Slider(L.Tr("Texture depth"), brush.textureDepth, 0, 1);
                        brush.textureScale = EditorGUILayout.Slider(L.Tr("Texture scale"), brush.textureScale, .05f, 16);
                    }
                    DrawBrushDynamics();
                }
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(L.Tr("Save Preset…"))) SavePreset();
                if (GUILayout.Button(L.Tr("Load Preset…"))) LoadPreset();
                GUILayout.EndHorizontal();
                if (GUILayout.Button(L.Tr("Import Brushes…"))) ImportBrushes();
                if (BrushLibrary.IsLibraryPreset(brush.presetId) && GUILayout.Button(L.Tr("Delete Imported Brush"))) DeleteImportedBrush();
            }
        }
    }
}
