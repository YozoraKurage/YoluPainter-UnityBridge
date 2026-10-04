using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>2D キャンバスの表示の回転と左右反転（CLIP STUDIO・Photoshop・Krita の表示の回転と同じく、表示だけを変えて正本は画素の座標のまま）。
    /// 拡大・パン・回転・反転はウィンドウの状態としてシリアライズし、スクリプトのコンパイルをまたいで残す（.ylp には入れない）。
    /// 回転・反転は表示域の中心のまわりで、そこに見えている画素は動かない。ストロークやドラッグの最中は変えない。</summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] Vector2 canvasPan;
        [SerializeField] float canvasZoom = 1, canvasAngle;
        [SerializeField] bool canvasFlip;

        /// <summary>キーと表示メニューで回す角度。</summary>
        internal const float CanvasRotateStep = 15;
        internal const float MinCanvasZoom = .2f, MaxCanvasZoom = 16;

        internal float CanvasZoom => canvasZoom;
        /// <summary>2D キャンバスの表示域（ウィンドウの座標。テスト用）。</summary>
        internal Rect CanvasRect => canvasRect;
        internal Vector2 CanvasPan => canvasPan;
        /// <summary>画面の上の回転（度、時計回りが正、(-180, 180]）。</summary>
        internal float CanvasAngle => canvasAngle;
        /// <summary>左右を反転して見せているか。</summary>
        internal bool CanvasFlipped => canvasFlip;

        /// <summary>今の 2D キャンバスの表示の写し（ウィンドウの座標）。</summary>
        internal CanvasView CanvasViewNow() => new CanvasView(canvasRect, document.Width, document.Height, canvasZoom, canvasPan, canvasAngle, canvasFlip);
        /// <summary>キャンバスのクリップの中の座標での写し（描くときはこちら）。</summary>
        CanvasView CanvasViewInClip() => new CanvasView(new Rect(0, 0, canvasRect.width, canvasRect.height), document.Width, document.Height, canvasZoom, canvasPan, canvasAngle, canvasFlip);

        /// <summary>表示を変えられるか。ストローク・ツールのドラッグ・パスの点のドラッグ・回すドラッグの最中は断る（入力の写しが途中で変わらないように。
        /// 回すドラッグは始めの角度とパンから決めるので、途中でキーで変えると食い違う）。</summary>
        bool CanChangeCanvasView()
        {
            if (stroke == null && !toolDragging && pathDrag < 0 && !canvasRotating) return true;
            message = L.Tr("The view does not rotate or flip during a stroke or a drag.");
            return false;
        }

        /// <summary>表示域の中心のまわりに、画面の上で degrees だけ回す（時計回りが正）。断ったら false。</summary>
        internal bool RotateCanvasView(float degrees) => CanChangeCanvasView() && SetCanvasAngle(canvasAngle + degrees);
        /// <summary>回転を 0 に戻す（表示域の中心に見えている画素はそのまま）。</summary>
        internal bool ResetCanvasRotation() => CanChangeCanvasView() && SetCanvasAngle(0);
        /// <summary>表示を左右に反転する（表示域の中心の縦の線で、見えているものをそのまま鏡に映す。角度の符号が変わる）。</summary>
        internal bool FlipCanvasView()
        {
            if (!CanChangeCanvasView()) return false;
            canvasFlip = !canvasFlip; canvasAngle = CanvasView.NormalizeAngle(-canvasAngle); canvasPan.x = -canvasPan.x;
            Repaint(); return true;
        }
        /// <summary>画面に合わせる: 拡大 100%・パンなし・回転 0（反転はそのまま。H で切り替える）。</summary>
        internal bool FitCanvasView()
        {
            if (!CanChangeCanvasView()) return false;
            canvasZoom = 1; canvasPan = Vector2.zero; canvasAngle = 0; Repaint(); return true;
        }
        /// <summary>新しいプロジェクト・取り込みで、表示を初めの状態に（反転も戻す）。</summary>
        void ResetCanvasView() { canvasZoom = 1; canvasPan = Vector2.zero; canvasAngle = 0; canvasFlip = false; EndCanvasRotateDrag(false); }

        /// <summary>角度を変え、表示域の中心にある画素が動かないようにパンも同じだけ回す。</summary>
        bool SetCanvasAngle(float degrees)
        {
            float next = CanvasView.NormalizeAngle(degrees);
            canvasPan = RotatePan(canvasPan, next - canvasAngle); canvasAngle = next;
            Repaint(); return true;
        }
        static Vector2 RotatePan(Vector2 pan, float degrees)
        {
            float a = CanvasView.NormalizeAngle(degrees);
            if (a == 0) return pan;
            double c = a == 90 || a == -90 ? 0 : a == 180 ? -1 : System.Math.Cos(a * System.Math.PI / 180);
            double s = a == 90 ? 1 : a == -90 ? -1 : a == 180 ? 0 : System.Math.Sin(a * System.Math.PI / 180);
            return new Vector2((float)(c * pan.x - s * pan.y), (float)(s * pan.x + c * pan.y));
        }

        /// <summary>拡大率を変える。pointer（ウィンドウの座標）の下の画素は動かない（null なら表示域の中心）。</summary>
        internal void ZoomCanvasView(float zoom, Vector2? pointer = null)
        {
            float next = Mathf.Clamp(zoom, MinCanvasZoom, MaxCanvasZoom);
            if (next == canvasZoom) return;
            float k = next / canvasZoom; var p = (pointer ?? canvasRect.center) - canvasRect.center;
            canvasPan = p - k * (p - canvasPan); canvasZoom = next; Repaint();
        }

        // ───────── 回すドラッグ（R を押したまま左ドラッグ、Shift ＋ 中ボタンのドラッグ） ─────────

        bool rotateKeyHeld, canvasRotating; float rotateStartAngle, rotateSwept, rotateLastPointer; Vector2 rotateStartPan;
        /// <summary>R を押しているあいだ、左ドラッグで表示を回す（Photoshop の回転ビュー、CLIP STUDIO の回転と同じ考え方）。</summary>
        internal bool RotateKeyHeld => rotateKeyHeld;
        /// <summary>回すドラッグの GUI の部品の番号の手がかり。回し始めると 2D の見出しに角度の印（ボタン）が現れ、その分だけ外枠の部品の番号が
        /// ずれるので、手がかり無しの番号ではドラッグの途中で見出しのボタンがこのドラッグの番号を取り、次のドラッグを飲み込んでいた（GUI のテストで
        /// 45° で止まった）。別の手がかりの番号は、ほかの部品が増えても取られない。</summary>
        const int RotateDragHint = 0x59505256;
        /// <summary>ポインタの角度を測らない、表示域の中心からの距離（中心の近くでは角度が暴れるので）。</summary>
        const float RotateDeadZone = 4;

        float PointerAngle(Vector2 pointer) { var d = pointer - canvasRect.center; return Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg; }

        /// <summary>R ＋ 左ドラッグ、Shift ＋ 中ボタンのドラッグで表示を回す。受け持ったら true。</summary>
        bool HandleCanvasRotateInput(Event e)
        {
            if (canvasRotating)
            {
                if (e.type == EventType.MouseDrag && e.button == 0)
                {
                    if ((e.mousePosition - canvasRect.center).magnitude >= RotateDeadZone)
                    {
                        float a = PointerAngle(e.mousePosition); rotateSwept += Mathf.DeltaAngle(rotateLastPointer, a); rotateLastPointer = a;
                        float target = rotateStartAngle + rotateSwept;
                        if (e.shift) target = Mathf.Round(target / CanvasRotateStep) * CanvasRotateStep;
                        canvasAngle = CanvasView.NormalizeAngle(target); canvasPan = RotatePan(rotateStartPan, canvasAngle - rotateStartAngle);
                    }
                    e.Use(); Repaint(); return true;
                }
                if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp) { EndCanvasRotateDrag(false); e.Use(); Repaint(); return true; }
                return false;
            }
            if (e.type == EventType.MouseDown && e.button == 0 && rotateKeyHeld && canvasRect.Contains(e.mousePosition))
            {
                if (!CanChangeCanvasView()) { e.Use(); return true; }
                canvasRotating = true; rotateStartAngle = canvasAngle; rotateStartPan = canvasPan; rotateSwept = 0;
                rotateLastPointer = PointerAngle(e.mousePosition);
                GUIUtility.hotControl = GUIUtility.GetControlID(RotateDragHint, FocusType.Passive); e.Use(); Repaint(); return true;
            }
            if (e.type == EventType.MouseDrag && e.button == 2 && e.shift && canvasRect.Contains(e.mousePosition))
            {
                var previous = e.mousePosition - e.delta;
                if (CanChangeCanvasView() && (previous - canvasRect.center).magnitude >= RotateDeadZone && (e.mousePosition - canvasRect.center).magnitude >= RotateDeadZone)
                    SetCanvasAngle(canvasAngle + Mathf.DeltaAngle(PointerAngle(previous), PointerAngle(e.mousePosition)));
                e.Use(); Repaint(); return true;
            }
            return false;
        }

        /// <summary>フォーカスを失ったとき: R を押していた印と回すドラッグを捨てる（回した表示はそのまま）。キーを離したのを受け取れないので。</summary>
        void ReleaseCanvasViewInput() { rotateKeyHeld = false; EndCanvasRotateDrag(false); }

        /// <summary>回すドラッグを終える（revert なら始めた角度とパンに戻す。Esc）。</summary>
        void EndCanvasRotateDrag(bool revert)
        {
            if (!canvasRotating) return;
            if (revert) { canvasAngle = rotateStartAngle; canvasPan = rotateStartPan; }
            canvasRotating = false; if (GUIUtility.hotControl != 0) GUIUtility.hotControl = 0;
        }

        // ───────── キー ─────────

        /// <summary>直前のキーの文字の入力を飲み込む（キーのコードで回したあと、同じキーの文字だけのイベントでもう一度回さない）。</summary>
        char swallowViewChar;

        /// <summary>表示のキー: - で左に、^（JIS の ^ のキー・Shift+6）や = で右に 15° 回す（CLIP STUDIO と同じ「-」「^」）、Shift+R で回転を戻す、
        /// R を押したまま左ドラッグで回す、H で左右反転。ストロークやドラッグの最中は断る。受け持ったら true。</summary>
        bool HandleViewKeys(Event e)
        {
            if (e.type != EventType.KeyDown) return false;
            if (e.keyCode != KeyCode.None) swallowViewChar = '\0';
            if (GUIUtility.keyboardControl != 0 || e.control || e.command || e.alt) return false;
            if (canvasRotating && e.keyCode == KeyCode.Escape) { EndCanvasRotateDrag(true); NoteTookKey(e); e.Use(); Repaint(); return true; }
            int action = ViewKeyAction(e);
            if (action == 0) return false;
            if (action == ViewKeyHold)
            {
                rotateKeyHeld = true; // 押したままの繰り返しも同じ
                NoteTookKey(e); e.Use(); Repaint(); return true;
            }
            if (e.keyCode != KeyCode.None) swallowViewChar = e.character != '\0' ? '\0' : CharOf(e.keyCode);
            switch (action)
            {
                case ViewKeyLeft: RotateCanvasView(-CanvasRotateStep); break;
                case ViewKeyRight: RotateCanvasView(CanvasRotateStep); break;
                case ViewKeyReset: ResetCanvasRotation(); break;
                case ViewKeyFlip: FlipCanvasView(); break;
                case ViewKeySwallow: break;
            }
            NoteTookKey(e); e.Use(); Repaint(); return true;
        }
        const int ViewKeyLeft = 1, ViewKeyRight = 2, ViewKeyReset = 3, ViewKeyFlip = 4, ViewKeyHold = 5, ViewKeySwallow = 6;
        int ViewKeyAction(Event e)
        {
            switch (e.keyCode)
            {
                case KeyCode.Minus: case KeyCode.KeypadMinus: return ViewKeyLeft;
                case KeyCode.Equals: case KeyCode.KeypadPlus: case KeyCode.Caret: return ViewKeyRight;
                case KeyCode.R: return e.shift ? ViewKeyReset : ViewKeyHold;
                case KeyCode.H: return e.shift ? 0 : ViewKeyFlip;
                case KeyCode.None:
                    // 記号のキーの位置は配列で違うので、^ は文字で見る（JIS の ^ のキー、US の Shift+6）
                    if (e.character == '\0') return 0;
                    if (swallowViewChar != '\0' && e.character == swallowViewChar) { swallowViewChar = '\0'; return ViewKeySwallow; }
                    return e.character == '^' ? ViewKeyRight : 0;
                default:
                    // キーのコードと文字が 1 つのイベントで来る版（US の Shift+6 など）
                    return e.character == '^' ? ViewKeyRight : 0;
            }
        }
        /// <summary>キーのコードで受けたキーが後から送る文字（同じキーで二度回さないため）。</summary>
        static char CharOf(KeyCode key) => key == KeyCode.Minus || key == KeyCode.KeypadMinus ? '-' : key == KeyCode.Equals ? '=' : key == KeyCode.KeypadPlus ? '+' : key == KeyCode.Caret ? '^' : '\0';

        /// <summary>R を離した（キーを離したイベントは OnGUI の始めに見る）。</summary>
        void NoteViewKeyUp(Event e)
        {
            if (e.type == EventType.KeyUp && e.keyCode == KeyCode.R) rotateKeyHeld = false;
        }

        // ───────── メニューと見出し ─────────

        void CanvasViewMenuItems(PaintMenu m)
        {
            bool free = stroke == null && !toolDragging;
            Item(m, "Rotate View Left", () => RotateCanvasView(-CanvasRotateStep), free, keys: "-");
            Item(m, "Rotate View Right", () => RotateCanvasView(CanvasRotateStep), free, keys: "^");
            Item(m, "Reset Rotation", () => ResetCanvasRotation(), free && canvasAngle != 0, keys: "Shift+R");
            Item(m, "Flip View Horizontally", () => FlipCanvasView(), free, canvasFlip, "H");
        }

        /// <summary>2D の見出しの右に、回っていれば角度（押すと 0 に戻す）、反転していればその印（押すと戻す）を出す。返り値は使った幅。</summary>
        float DrawCanvasViewMarks(float x, float y, float right)
        {
            float used = 0;
            bool enabled = stroke == null && !toolDragging;
            if (canvasAngle != 0)
            {
                string text = AngleLabel(canvasAngle);
                float w = 20 + PaintGui.TextWidth(text, PaintTheme.LabelDim) + 6;
                if (x + used + w <= right)
                {
                    var r = new Rect(x + used, y + 2, w, 22);
                    if (PaintGui.Button(r, text, false, enabled, L.Tr("The view is rotated. Click to reset the rotation (Shift+R)."), "rotate_90_degrees_cw")) ResetCanvasRotation();
                    used += w + 4;
                }
            }
            if (canvasFlip && x + used + 24 <= right)
            {
                if (PaintGui.IconButton(new Rect(x + used, y + 2, 24, 22), "flip", L.Tr("The view is flipped horizontally. Click to flip it back (H)."), true, enabled, 16)) FlipCanvasView();
                used += 28;
            }
            return used;
        }
        /// <summary>見出しに出す角度（整数ならそのまま、そうでなければ小数 1 桁）。</summary>
        internal static string AngleLabel(float degrees)
        {
            float rounded = Mathf.Round(degrees * 10) / 10;
            return (Mathf.Approximately(rounded, Mathf.Round(rounded)) ? Mathf.RoundToInt(rounded).ToString(System.Globalization.CultureInfo.InvariantCulture) : rounded.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + "°";
        }
    }
}
