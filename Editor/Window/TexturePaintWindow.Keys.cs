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
            NoteViewKeyUp(e); NoteStencilKeyUp(e);
            if(e.type!=EventType.KeyDown)return;
            // キーが届いた時点で文字の欄にフォーカスがあったか（Enter で確定して外れる前の状態を見る）
            editingText=GUIUtility.keyboardControl!=0; tookKey=KeyCode.None;
            if(GradientDraftActive){if(e.keyCode==KeyCode.Escape)CancelGradientDrafts();e.Use();Repaint();}
            else if(ShapeDragging){if(e.keyCode==KeyCode.Escape)CancelShapeDrag();e.Use();Repaint();} // 形のハンドルのドラッグ中は Esc で取り消すだけ（Undo などのキーも使わない）
            else if(pathDrag>=0){if(e.keyCode==KeyCode.Escape)CancelToolDrag();e.Use();Repaint();}
            else if(e.keyCode==KeyCode.Escape && toolDragging){CancelToolDrag();GUIUtility.hotControl=0;e.Use();Repaint();}
            else if(e.keyCode==KeyCode.Escape && stroke!=null){FinishStroke(false);e.Use();}
            else if(stroke==null && !toolDragging && tool==PaintTool.Move && GUIUtility.keyboardControl==0 && !(e.control||e.command) && ArrowDelta(e.keyCode)!=Vector2Int.zero)
            {var d=ScreenArrowToCanvas(ArrowDelta(e.keyCode))*(e.shift?10:1);TryAction(()=>MoveBy(d.x,d.y));e.Use();Repaint();}
            else if(stroke==null && tool==PaintTool.Path && GUIUtility.keyboardControl==0 && (e.keyCode==KeyCode.Delete||e.keyCode==KeyCode.Backspace)){TryAction(RemoveLastPathPoint);e.Use();Repaint();}
            // レイヤーの操作（複製・結合・クリップボード・グループ・新規・上へ下へ・表示・クリッピング。選んだ層の全部に効く。LayerOps.cs の
            // LayerCommandOf）。文字の欄で入力中は Unity の文字のコピー・貼り付けに任せる。ストロークの最中は断って知らせる
            else if(GUIUtility.keyboardControl==0 && LayerCommandOf(e)!=LayerCommand.None){RunLayerCommand(LayerCommandOf(e));e.Use();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.A){document.SetSelection(SelectionMask.All(document));e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.D){document.ClearSelection();e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.shift && e.keyCode==KeyCode.I){if(document.Selection!=null)document.SetSelection(document.Selection.Invert());e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.Z){if(e.shift)document.Redo();else document.Undo();e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && !e.shift && e.keyCode==KeyCode.Y){document.Redo();e.Use();Repaint();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.S){SaveProject(e.shift);e.Use();}
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.O){OpenProject();e.Use();}
            else if(stroke==null && (e.control||e.command) && !e.shift && e.keyCode==KeyCode.N){NewProjectDialog();e.Use();} // Ctrl+Shift+N は新しいレイヤー（文字の入力中は何もしない）
            else if(stroke==null && (e.control||e.command) && e.keyCode==KeyCode.Alpha0){FitCanvasView();e.Use();Repaint();}
            else if(stroke==null && !toolDragging && (e.control||e.command) && ZoomKey(e.keyCode)!=0){ZoomCanvasView(ZoomKey(e.keyCode)>0?canvasZoom*1.25f:canvasZoom/1.25f);e.Use();Repaint();}
            if(e.type==EventType.Used)NoteTookKey(e);
        }
        /// <summary>Ctrl++ / Ctrl+- の拡大・縮小（表示の中心が軸）。+ は配列で位置が違うので、US の = のキー（Shift の有無を問わない）・
        /// JIS の ; のキー（Shift で +）・テンキーを受ける。</summary>
        static int ZoomKey(KeyCode key)=>key==KeyCode.Equals||key==KeyCode.Plus||key==KeyCode.KeypadPlus||key==KeyCode.Semicolon?1:key==KeyCode.Minus||key==KeyCode.KeypadMinus?-1:0;
        static Vector2Int ArrowDelta(KeyCode key)=>key==KeyCode.LeftArrow?Vector2Int.left:key==KeyCode.RightArrow?Vector2Int.right:key==KeyCode.UpArrow?Vector2Int.up:key==KeyCode.DownArrow?Vector2Int.down:Vector2Int.zero;
        /// <summary>矢印キーの向き（画面の上で。上は +y）を、表示の回転・反転を戻して、いちばん近いキャンバスの軸の 1 画素に（画面で右を押せば
        /// 画面で右に動く。45° ちょうどのような斜めではどちらかの軸）。</summary>
        Vector2Int ScreenArrowToCanvas(Vector2Int arrow)
        {
            var view=CanvasViewNow(); if(view.AxisAligned)return arrow;
            var d=view.ScreenToCanvasDirection(new Vector2(arrow.x,-arrow.y));
            return Mathf.Abs(d.x)>=Mathf.Abs(d.y)?new Vector2Int(d.x>0?1:-1,0):new Vector2Int(0,d.y>0?1:-1);
        }

        /// <summary>修飾キー無しの 1 文字のショートカット（ツール・色・ブラシの大きさ・表示）。文字の入力中は使わない。</summary>
        bool HandleToolKeys(Event e)
        {
            if (HandleStencilKeys(e)) return true; // T・N を押しているあいだ（ステンシル。Tools/TexturePaintWindow.Stencil.cs）
            if (HandleViewKeys(e)) return true; // 表示の回転・反転（ストロークの最中は断る）
            if (HandleModelShowKeys(e)) return true; // ポインタの2D/3Dビューで見せるもの: C・Shift+C・Shift+B（ストロークの最中は断る）
            if (e.type != EventType.KeyDown || stroke != null || toolDragging || GUIUtility.keyboardControl != 0 || e.control || e.command || e.alt) return false;
            if (HandlePolygonOverlapKey(e)) { NoteTookKey(e); e.Use(); Repaint(); return true; }
            switch (e.keyCode)
            {
                case KeyCode.B: SelectTool(PaintTool.Brush); break;
                case KeyCode.U: SelectTool(e.shift ? PaintTool.Smudge : PaintTool.Blur); break;
                case KeyCode.S: if (e.shift) return false; SelectTool(PaintTool.Clone); break;
                case KeyCode.E: SelectTool(PaintTool.Brush, true); break;
                case KeyCode.G: SelectTool(e.shift ? PaintTool.Gradient : PaintTool.Fill); break;
                case KeyCode.M: SelectTool(e.shift ? PaintTool.SelectEllipse : PaintTool.SelectRectangle); break;
                case KeyCode.L: SelectTool(PaintTool.Lasso); break;
                case KeyCode.W: SelectTool(e.shift ? PaintTool.IdSelect : PaintTool.MagicWand); break; // Shift+W: ID の色で選択（Tools/TexturePaintWindow.IdSelect.cs）
                case KeyCode.V: SelectTool(PaintTool.Move); break;
                case KeyCode.P: SelectTool(PaintTool.Path); break;
                case KeyCode.I: SelectTool(PaintTool.Eyedropper); break;
                case KeyCode.Q: if (e.shift) return false; ProjectionHandlesHidden = !ProjectionHandlesHidden; message = ProjectionHandlesHidden ? L.Tr("Projection handles hidden (Q shows them).") : L.Tr("Projection handles shown (Q hides them)."); break; // Substance と同じ Q（Model/TexturePaintWindow.ShapeGizmo.cs）
                case KeyCode.Alpha4: case KeyCode.Keypad4: if (e.shift) return false; SelectTool(PaintTool.PolygonFill); break; // Substance Painter と同じ 4
                case KeyCode.X: if (tool == PaintTool.PolygonFill && EditingMask) PolygonFillErase = !polyFillErase; else SwapColors(); break; // マスクのポリゴン塗りつぶしでは白と黒（Substance の X）
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
