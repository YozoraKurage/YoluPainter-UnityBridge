using System.Linq;
using Yozolab.YoluPainter.Core;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>キーボードのショートカット。Unity のショートカットとの関係は ShortcutGuard（この窓が使ったキーと文字の入力中を知らせる）。</summary>
    public sealed partial class TexturePaintWindow : IPainterShortcutScope
    {
        bool editingText; KeyCode tookKey; EventModifiers tookModifiers;

        bool IPainterShortcutScope.EditingText => editingText;
        bool IPainterShortcutScope.TookKey(KeyCode key, EventModifiers modifiers)
        {
            bool took = tookKey != KeyCode.None && key == tookKey && Mods(modifiers) == Mods(tookModifiers);
            tookKey = KeyCode.None; return took;
        }
        /// <summary>キーの比べ方: Ctrl/Cmd・Shift・Alt だけを見る（CapsLock・数字キーパッド・Fn の印は Unity の実装で付いたり付かなかったりする）。</summary>
        static EventModifiers Mods(EventModifiers m) => m & (EventModifiers.Control | EventModifiers.Command | EventModifiers.Shift | EventModifiers.Alt);
        void NoteTookKey(Event e) { tookKey = e.keyCode; tookModifiers = e.modifiers; }

        void HandleKeys(Event e)
        {
            if(e.type!=EventType.KeyDown)return;
            // キーが届いた時点で文字の欄にフォーカスがあったか（Enter で確定して外れる前の状態を見る）
            editingText=GUIUtility.keyboardControl!=0; tookKey=KeyCode.None;
            if(e.keyCode==KeyCode.Escape && toolDragging){CancelToolDrag();GUIUtility.hotControl=0;e.Use();Repaint();}
            else if(e.keyCode==KeyCode.Escape && stroke!=null){FinishStroke(false);e.Use();}
            else if(stroke==null && !toolDragging && tool==PaintTool.Move && GUIUtility.keyboardControl==0 && !(e.control||e.command) && ArrowDelta(e.keyCode)!=Vector2Int.zero)
            {var d=ArrowDelta(e.keyCode)*(e.shift?10:1);TryAction(()=>MoveBy(d.x,d.y));e.Use();Repaint();}
            else if(stroke==null && tool==PaintTool.Path && GUIUtility.keyboardControl==0 && (e.keyCode==KeyCode.Delete||e.keyCode==KeyCode.Backspace)){TryAction(RemoveLastPathPoint);e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.A){document.SetSelection(SelectionMask.All(document));e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.D){document.ClearSelection();e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.shift && e.keyCode==KeyCode.I){if(document.Selection!=null)document.SetSelection(document.Selection.Invert());e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.Z){if(e.shift)document.Redo();else document.Undo();e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && !e.shift && e.keyCode==KeyCode.Y){document.Redo();e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.S){SaveProject(e.shift);e.Use();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.O){OpenProject();e.Use();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.N){NewProjectDialog();e.Use();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.Alpha0){canvasZoom=1;canvasPan=Vector2.zero;e.Use();Repaint();}
            if(e.type==EventType.Used)NoteTookKey(e);
        }
        static Vector2Int ArrowDelta(KeyCode key)=>key==KeyCode.LeftArrow?Vector2Int.left:key==KeyCode.RightArrow?Vector2Int.right:key==KeyCode.UpArrow?Vector2Int.up:key==KeyCode.DownArrow?Vector2Int.down:Vector2Int.zero;

        /// <summary>修飾キー無しの 1 文字のショートカット（ツール・色・ブラシの大きさ・表示）。文字の入力中は使わない。</summary>
        bool HandleToolKeys(Event e)
        {
            if (e.type != EventType.KeyDown || stroke != null || toolDragging || GUIUtility.keyboardControl != 0 || e.control || e.command || e.alt) return false;
            switch (e.keyCode)
            {
                case KeyCode.B: SelectTool(PaintTool.Brush); break;
                case KeyCode.E: SelectTool(PaintTool.Brush, true); break;
                case KeyCode.G: SelectTool(e.shift ? PaintTool.Gradient : PaintTool.Fill); break;
                case KeyCode.M: SelectTool(e.shift ? PaintTool.SelectEllipse : PaintTool.SelectRectangle); break;
                case KeyCode.L: SelectTool(PaintTool.Lasso); break;
                case KeyCode.W: SelectTool(PaintTool.MagicWand); break;
                case KeyCode.V: SelectTool(PaintTool.Move); break;
                case KeyCode.P: SelectTool(PaintTool.Path); break;
                case KeyCode.I: SelectTool(PaintTool.Eyedropper); break;
                case KeyCode.X: SwapColors(); break;
                case KeyCode.D: DefaultColors(); break;
                case KeyCode.LeftBracket: brush.radius = Mathf.Max(.5f, brush.radius / 1.15f); break;
                case KeyCode.RightBracket: brush.radius = Mathf.Min(128, brush.radius * 1.15f); break;
                case KeyCode.F1: View = ViewMode.Canvas; break;
                case KeyCode.F2: View = ViewMode.Model; break;
                case KeyCode.F3: View = ViewMode.Split; break;
                default: return false;
            }
            NoteTookKey(e); e.Use(); Repaint(); return true;
        }
    }
}
