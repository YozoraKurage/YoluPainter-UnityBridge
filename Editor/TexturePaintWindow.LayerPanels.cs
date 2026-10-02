using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>プロパティの欄のうち、選んだレイヤーとチャンネル・テクスチャセットの設定（LayerSections・ChannelSections）。</summary>
    public sealed partial class TexturePaintWindow
    {
        void LayerSections(UiRows rows)
        {
            var active = document.Layers.FirstOrDefault(l => l.Id == selectedLayer);
            if (active == null) return;
            LegacySection(rows, "layer", L.Tr("Layer") + ": " + active.Name, () => DrawLayerDetails(active));
        }

        void ChannelSections(UiRows rows)
        {
            LegacySection(rows, "normal", L.Tr("Normal"), DrawNormalPanel);
            LegacySection(rows, "mesh-maps", L.Tr("Mesh Maps"), DrawMeshMapPanel);
            LegacySection(rows, "pose", L.Tr("Pose"), DrawPosePanel);
        }

        void DrawLayerDetails(PaintLayer active)
        {
            if (!active.IsGroup) { bool enabled = EditorGUILayout.Toggle(L.Tr("Paint this channel"), active.IsChannelEnabled(channel)); if (enabled != active.IsChannelEnabled(channel)) TryAction(() => document.SetChannelEnabled(active.Id, channel, enabled)); }
            if (active.IsGroup) EditorGUILayout.HelpBox(active.BlendMode == LayerBlendMode.PassThrough ? L.Tr("Pass through: the contents blend with the layers below as if they were not grouped.") : L.Tr("Isolated: the contents are composited together first, then blended."), MessageType.None);
            if (active.Kind == LayerKind.Fill) DrawFill(active);
            if (active.Kind == LayerKind.Adjustment) DrawAdjustment(active);
            DrawMask(active);
            DrawFilters(active);
        }

        void DrawFill(PaintLayer active)
        {
            GUILayout.Space(6);
            GUILayout.Label("Fill value ("+channel+")",EditorStyles.miniBoldLabel);
            if(active.FillValues.TryGetValue(channel,out var value))
            {
                Rgba32 next;
                if(channel==PaintChannel.Roughness||channel==PaintChannel.Metallic||channel==PaintChannel.Height)
                {byte v=(byte)Mathf.RoundToInt(EditorGUILayout.Slider("Value",value.R/255f,0,1)*255);next=new Rgba32(v,v,v,value.A);}
                else{var c=EditorGUILayout.ColorField("Value",new Color32(value.R,value.G,value.B,value.A));var c32=(Color32)c;next=new Rgba32(c32.r,c32.g,c32.b,c32.a);}
                if(next!=value) document.SetFillValue(active.Id,channel,next,coalesce:true);
                if(GUILayout.Button("Remove value for "+channel)) document.SetFillValue(active.Id,channel,null);
            }
            else if(GUILayout.Button("Add value for "+channel)) document.SetFillValue(active.Id,channel,GetBrush().Color);
            EditorGUILayout.HelpBox("A fill covers the whole canvas. Paint its mask to choose where it shows.",MessageType.None);
        }
        void DrawAdjustment(PaintLayer active)
        {
            GUILayout.Space(6);
            var a=active.Adjustment;
            GUILayout.Label("Adjustment: "+a.Type+" (applies to the layers below)",EditorStyles.miniBoldLabel);
            AdjustmentSettings next=a;
            switch(a.Type)
            {
                case AdjustmentType.Levels:
                {
                    float ib=EditorGUILayout.Slider("Input black",(float)a.InputBlack,0,(float)a.InputWhite-.004f);
                    float iw=EditorGUILayout.Slider("Input white",(float)a.InputWhite,ib+.004f,1);
                    float gamma=EditorGUILayout.Slider("Gamma",(float)a.Gamma,.1f,9.99f);
                    float ob=EditorGUILayout.Slider("Output black",(float)a.OutputBlack,0,1);
                    float ow=EditorGUILayout.Slider("Output white",(float)a.OutputWhite,0,1);
                    next=AdjustmentSettings.Levels(ib,iw,gamma,ob,ow); break;
                }
                case AdjustmentType.HueSaturation:
                {
                    float hue=EditorGUILayout.Slider("Hue",(float)a.Hue,-180,180);
                    float sat=EditorGUILayout.Slider("Saturation",(float)a.Saturation,-1,1);
                    float light=EditorGUILayout.Slider("Lightness",(float)a.Lightness,-1,1);
                    next=AdjustmentSettings.HueSaturation(hue,sat,light); break;
                }
                default: EditorGUILayout.LabelField("Inverts the colour of everything below."); break;
            }
            if(!next.Equals(a)) TryAction(()=>document.SetAdjustment(active.Id,next,coalesce:true));
            if(!a.AppliesTo(channel)) EditorGUILayout.HelpBox(a.Type+" does not apply to the "+channel+" channel.",MessageType.None);
        }
        bool EditingMask => editMask && document.Layers.Any(l => l.Id == selectedLayer && l.Mask != null);
        void DrawMask(PaintLayer active)
        {
            GUILayout.Space(6);
            GUILayout.Label("Mask (shared by all channels)",EditorStyles.miniBoldLabel);
            var mask=active.Mask;
            if(mask==null)
            {
                if(GUILayout.Button("Add mask")){document.AddLayerMask(active.Id);editMask=true;}
                return;
            }
            editMask=GUILayout.Toggle(editMask,"Paint on mask (paint hides, erase reveals)");
            bool maskEnabled=EditorGUILayout.Toggle("Mask enabled",mask.Enabled);if(maskEnabled!=mask.Enabled)document.SetLayerMaskEnabled(active.Id,maskEnabled);
            bool inverted=EditorGUILayout.Toggle("Invert mask",mask.Inverted);if(inverted!=mask.Inverted)document.SetLayerMaskInverted(active.Id,inverted);
            float density=EditorGUILayout.Slider("Mask density",(float)mask.Density,0,1);if(Math.Abs(density-mask.Density)>.00001)document.SetLayerMaskDensity(active.Id,density,coalesce:true);
            if(GUILayout.Button("Remove mask")){document.RemoveLayerMask(active.Id);editMask=false;}
        }
    }
}
