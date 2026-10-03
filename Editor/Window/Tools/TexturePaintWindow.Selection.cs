using System;
using System.Linq;
using Yozolab.YoluPainter.Core;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>選択範囲: 組み合わせ（追加・削除・交差）、変更（拡張・縮小・境界・ぼかし・鋭く）、選ばれていない所の表示。</summary>
    public sealed partial class TexturePaintWindow
    {
        Texture2D selectionOverlay; SelectionMask overlayFor;
        internal enum SelectionModifyKind { Grow, Shrink, Border, Feather, Sharpen }
        int selectionRadius = 5; bool selectionEdgeLock;
        internal int SelectionRadius { get => selectionRadius; set => selectionRadius = Mathf.Clamp(value, 0, SelectionMask.MaxModifyRadius); }
        internal bool SelectionEdgeLock { get => selectionEdgeLock; set => selectionEdgeLock = value; }
        /// <summary>選択範囲を変える（GIMP の Select メニューと同じ考え方）。1 回の Undo。</summary>
        internal void ModifySelection(SelectionModifyKind kind)
        {
            var current=document.Selection;
            if(current==null){message="Nothing is selected.";return;}
            int r=selectionRadius;
            SelectionMask next;
            switch(kind)
            {
                case SelectionModifyKind.Grow: next=current.Grow(r); break;
                case SelectionModifyKind.Shrink: next=current.Shrink(r,selectionEdgeLock); break;
                case SelectionModifyKind.Border: next=current.Border(r,selectionEdgeLock); break;
                case SelectionModifyKind.Feather: next=current.Feather(r,selectionEdgeLock); break;
                default: next=current.Sharpen(); break;
            }
            document.SetSelection(next);
            message=document.Selection==null?kind+": nothing is left selected.":kind==SelectionModifyKind.Sharpen?"Selection sharpened.":kind+" by "+r+" px.";
        }
        static SelectionCombine CombineOf(Event e)=>e.shift&&(e.control||e.command)?SelectionCombine.Intersect:e.shift?SelectionCombine.Add:(e.control||e.command)?SelectionCombine.Subtract:SelectionCombine.Replace;
        /// <summary>新しい形を今の選択範囲と組み合わせて選択範囲にする（Shift 追加、Ctrl 削除、Shift+Ctrl 交差）。</summary>
        internal void ApplySelection(SelectionMask shape,SelectionCombine mode)
        {
            var current=document.Selection;
            SelectionMask next=mode==SelectionCombine.Replace||current==null?(mode==SelectionCombine.Subtract?null:shape):current.Combine(shape,mode);
            document.SetSelection(next);
            message=document.Selection==null?"Nothing selected.":"Selection: "+mode+".";
        }
        /// <summary>選ばれていない所を暗く覆う表示用のテクスチャ（選択範囲が変わったときだけ作り直す）。</summary>
        void EnsureSelectionOverlay()
        {
            var selection=document.Selection;
            if(ReferenceEquals(selection,overlayFor)&&selectionOverlay!=null)return;
            if(selectionOverlay==null||selectionOverlay.width!=document.Width||selectionOverlay.height!=document.Height)
            {
                if(selectionOverlay!=null)DestroyImmediate(selectionOverlay);
                selectionOverlay=new Texture2D(document.Width,document.Height,TextureFormat.RGBA32,false,true){hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point};
            }
            var pixels=new Color32[document.Width*document.Height]; int tile=document.TileSize; var amounts=new byte[tile*tile];
            for(int i=0;i<pixels.Length;i++)pixels[i]=new Color32(0,0,0,110);
            foreach(var coord in selection.Tiles)
            {
                selection.CopyTile(coord,amounts);
                int w=Math.Min(tile,document.Width-coord.X*tile),h=Math.Min(tile,document.Height-coord.Y*tile);
                for(int y=0;y<h;y++)for(int x=0;x<w;x++)pixels[(coord.Y*tile+y)*document.Width+coord.X*tile+x]=new Color32(0,0,0,(byte)(110*(255-amounts[y*tile+x])/255));
            }
            selectionOverlay.SetPixels32(pixels);selectionOverlay.Apply(false,false);overlayFor=selection;
        }
    }
}
