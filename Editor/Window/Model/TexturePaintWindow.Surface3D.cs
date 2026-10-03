using System;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>3D ビューでの選択とバケツ: クリックした三角形から範囲（三角形・UV アイランド・メッシュの塊・マテリアル）をたどり、その UV を
    /// 選択範囲にする（Shift 追加・Ctrl 削除・Shift+Ctrl 交差）か、塗る（今の選択範囲の内側だけ）。</summary>
    public sealed partial class TexturePaintWindow
    {
        SurfaceRegionKind surfacePick = SurfaceRegionKind.UvIsland;
        internal SurfaceRegionKind SurfacePick { get => surfacePick; set => surfacePick = value; }

        void SurfacePickSection(UiRows rows)
        {
            if (!ToolSection(rows, "surface-pick", L.Tr("3D Pick"), "view_in_ar")) return;
            PaintGui.FitDropdown(rows.Row(), L.TrIn("3D pick", "Region"), SurfacePickName(surfacePick), at =>
            {
                var menu = new GenericMenu();
                foreach (SurfaceRegionKind kind in Enum.GetValues(typeof(SurfaceRegionKind))) { var k = kind; menu.AddItem(new GUIContent(SurfacePickName(k)), k == surfacePick, () => surfacePick = k); }
                menu.DropDown(at);
            }, L.Tr("What a click on the model in the 3D view selects or fills"), true, LabelColumn);
            rows.Space(4);
        }

        static string SurfacePickName(SurfaceRegionKind kind)
        {
            switch (kind)
            {
                case SurfaceRegionKind.Triangle: return L.TrIn("3D pick", "Triangle");
                case SurfaceRegionKind.UvIsland: return L.TrIn("3D pick", "UV Island");
                case SurfaceRegionKind.MeshPart: return L.TrIn("3D pick", "Mesh Part");
                case SurfaceRegionKind.Material: return L.Tr("Material");
                default: return kind.ToString();
            }
        }

        /// <summary>選択ツールとバケツの、3D ビューでのクリック。扱ったら true。</summary>
        bool HandleSurfaceTool(Event e)
        {
            bool selecting=tool>=PaintTool.SelectRectangle&&tool<=PaintTool.MagicWand;
            if(!(selecting||tool==PaintTool.Fill)||e.type!=EventType.MouseDown||e.button!=0||e.alt||!surfaceRect.Contains(e.mousePosition))return false;
            var mode=CombineOf(e); var pointer=e.mousePosition;
            TryAction(()=>
            {
                if(!preview.HasModel){message="Load a model (or the demo cube) to pick on the 3D view.";return;}
                if(!preview.TryPick(surfaceRect,pointer,out var hit)){message="Nothing of the model under the pointer.";return;}
                if(hit.MaterialSlot!=materialSlot){OtherSlotPressed(hit.MaterialSlot);return;}
                var region=SurfaceRegions.Selection(document,preview.Geometry,SurfaceRegions.Region(preview.Geometry,hit.TriangleIndex,surfacePick));
                if(selecting){ApplySelection(region,mode);message=document.Selection==null?"Nothing selected.":"Selected the "+surfacePick+" ("+mode+").";}
                else FillRegion(region,surfacePick.ToString());
            });
            e.Use();Repaint();return true;
        }

        /// <summary>範囲を（今の選択範囲の内側だけ）ブラシの色と不透明度で塗る。マスク編集中はマスクを塗る。</summary>
        internal void FillRegion(SelectionMask region,string what)
        {
            var layer=document.GetLayer(selectedLayer);
            if(!EditingMask&&layer.Kind!=LayerKind.Raster)throw new InvalidOperationException("Fill paints pixels: select a paint layer, or edit the layer's mask.");
            var b=GetBrush();
            if(!EditingMask)document.EnsurePixelsEditable(selectedLayer,b.Erase); // ロックで断るなら、チャンネルを有効にする前に
            if(!EditingMask&&!layer.IsChannelEnabled(channel))document.SetChannelEnabled(selectedLayer,channel,true);
            bool changed=EditingMask?document.FillMask(selectedLayer,b.Opacity,region,reveal:b.Erase):document.Fill(selectedLayer,channel,b.Color,b.Opacity,region,b.Erase);
            message=changed?"Filled the "+what+".":"Nothing to fill there.";repaintPixels=true;
        }
    }
}
