using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// ステンシル（Substance Painter のステンシル）: このプロジェクトの画像を 3D ビューと 2D キャンバスの画面に半透明で重ね、ブラシはその上から
    /// 塗る。画面に貼り付いている（カメラを回しても・キャンバスを動かしても画面の同じ所にある）。置き場は表示域に対する割合（中心）・表示域の
    /// 高さに対する大きさ・画面の上の角度で、T を押したままのドラッグで変える（左 = 回す（Shift で 15° 刻み）、中か Ctrl+左 = 動かす、右か
    /// Alt+左 = 大きさ。Substance の S と同じ割り当て。S はコピースタンプなので T）。N を押しているあいだは効かない（Substance の N）。
    /// <para>塗る値（Core の <see cref="BrushStencil"/>）: 2D はキャンバスの画素の中心を画面へ写し、3D は面のテクセルの点（<see cref="SurfacePixel.Position"/>）を
    /// カメラで画面へ写して、そこのステンシルを読む。灰色の画像は量（輝度 × α）、色の画像は色（α が量）。マテリアルで塗るときは色を Base Color
    /// だけに入れ、ほかのチャンネルは自分の値を α の量で塗る。置き場はストロークの始めに決め、ストロークの間は変えない。</para>
    /// <para>保存: 窓の状態（スクリプトのコンパイルをまたいで残す）。画像はリソースの ID で覚え、プロジェクトから消えたら外して知らせる。.ylp・正本・
    /// ブラシの設定（brush.json・プリセット）には入れない。重ね表示は 3D の絵の上に GUI で描くので、3D の描画のキャッシュの鍵には入れない。</para>
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] string stencilResource = "";
        [SerializeField] Vector2 stencilCenter = DefaultStencilCenter;
        [SerializeField] float stencilSize = DefaultStencilSize, stencilAngle, stencilOpacity = DefaultStencilOpacity;
        [SerializeField] StencilMode stencilMode;
        [SerializeField] StencilTiling stencilTiling;
        [SerializeField] bool stencilInvert;

        internal static readonly Vector2 DefaultStencilCenter = new Vector2(.5f, .5f);
        /// <summary>初めの大きさ（表示域の高さに対する割合）と重ね表示の不透明度。大きさの範囲。</summary>
        internal const float DefaultStencilSize = .6f, DefaultStencilOpacity = .5f, MinStencilSize = .02f, MaxStencilSize = 20;
        /// <summary>Shift を押して回すときの刻み（度）。</summary>
        internal const float StencilRotateStep = 15;
        /// <summary>重ね表示のテクスチャの長い辺（塗る値は元の画像から読む。表示だけ縮める）。</summary>
        internal const int StencilOverlaySide = 1024;
        /// <summary>ステンシルのミップの予算（試験が下げる）。</summary>
        internal long StencilMipBudgetBytes = StencilImage.DefaultMipBudgetBytes;

        StencilImage stencilImage; string stencilImageKey;
        Texture2D stencilOverlay; string stencilOverlayKey;
        bool stencilKeyHeld, stencilIgnoreHeld;
        internal enum StencilDragKind { None, Move, Rotate, Scale }
        StencilDragKind stencilDrag; Rect stencilDragView;
        Vector2 stencilDragStartCenter, stencilDragFrom; float stencilDragStartSize, stencilDragStartAngle, stencilDragSwept, stencilDragLastAngle;
        /// <summary>今の 3D のストロークが読むステンシルの置き場とカメラの写し（ストロークの始めに決める。ステンシルを使わなければ null）。</summary>
        StencilFrame? strokeStencilFrame; GuiProjection strokeStencilProjection;
        const int StencilDragHint = 0x59505354;
        /// <summary>回す角度を測らない、ステンシルの中心からの距離（GUI の点）。</summary>
        const float StencilRotateDeadZone = 4;

        // ───────── 試験と欄の口 ─────────

        /// <summary>ステンシルの画像（このプロジェクトのリソースの ID）。無ければ null。</summary>
        internal Guid? StencilResource => Guid.TryParse(stencilResource, out var id) ? id : (Guid?)null;
        internal StencilImage CurrentStencilImage { get { SyncStencil(); return stencilImage; } }
        /// <summary>表示域に対する中心（左上が 0, 0、右下が 1, 1）。</summary>
        internal Vector2 StencilCenter { get => stencilCenter; set { stencilCenter = new Vector2(Finite(value.x, .5f), Finite(value.y, .5f)); Repaint(); } }
        /// <summary>表示域の高さに対する高さ。</summary>
        internal float StencilSize { get => stencilSize; set { stencilSize = Mathf.Clamp(Finite(value, DefaultStencilSize), MinStencilSize, MaxStencilSize); Repaint(); } }
        /// <summary>画面の上の角度（度、時計回りが正、(-180, 180]）。</summary>
        internal float StencilAngle { get => stencilAngle; set { stencilAngle = CanvasView.NormalizeAngle(value); Repaint(); } }
        internal float StencilOpacity { get => stencilOpacity; set { stencilOpacity = Mathf.Clamp01(Finite(value, DefaultStencilOpacity)); Repaint(); } }
        internal StencilMode StencilModeSetting { get => stencilMode; set { stencilMode = Enum.IsDefined(typeof(StencilMode), value) ? value : StencilMode.Auto; Repaint(); } }
        internal StencilTiling StencilTilingSetting { get => stencilTiling; set { stencilTiling = Enum.IsDefined(typeof(StencilTiling), value) ? value : StencilTiling.None; Repaint(); } }
        internal bool StencilInvert { get => stencilInvert; set { stencilInvert = value; Repaint(); } }
        internal bool StencilKeyHeld => stencilKeyHeld;
        /// <summary>N を押していて、ステンシルを使わない。</summary>
        internal bool StencilIgnored { get => stencilIgnoreHeld; set { stencilIgnoreHeld = value; Repaint(); } }
        internal StencilDragKind StencilDragging => stencilDrag;
        /// <summary>今のツールで、次のストロークがステンシルを通して塗るか（画像があり、ブラシのツールで、N を押していない）。</summary>
        internal bool StencilApplies => IsBrushTool && !stencilIgnoreHeld && CurrentStencilImage != null;
        static float Finite(float v, float fallback) => float.IsNaN(v) || float.IsInfinity(v) ? fallback : v;

        /// <summary>この表示域（GUI の座標）でのステンシルの置き場。画像が無ければ null。</summary>
        internal StencilFrame? StencilFrameIn(Rect view)
        {
            var image = CurrentStencilImage;
            if (image == null || view.width <= 0 || view.height <= 0) return null;
            return new StencilFrame(view, stencilCenter, stencilSize, stencilAngle, image.Width, image.Height);
        }

        // ───────── 画像を選ぶ・外す ─────────

        /// <summary>ステンシルの画像をこのプロジェクトのリソースにする（null で外す）。置き場はそのまま。ミップが予算を超える画像は断る。</summary>
        internal void SetStencil(Guid? id)
        {
            if (stroke != null) throw new InvalidOperationException(L.Tr("Finish the stroke first."));
            if (id == null) { ClearStencil(); message = L.Tr("The stencil was removed."); Repaint(); return; }
            var image = ImageResources.Get(id.Value);
            var built = new StencilImage(image.Content, image.ColorSpace, StencilMipBudgetBytes);
            stencilResource = image.Id.ToString("D"); stencilImage = built; stencilImageKey = StencilKey(image); DisposeStencilOverlay();
            message = L.Tr("Stencil: {0}. The brush paints through it; hold T and drag on the view to place it.", image.Name);
            Repaint();
        }
        static string StencilKey(ImageResource image) => image.Id.ToString("N") + ":" + image.ContentHash + ":" + (int)image.ColorSpace;

        void ClearStencil()
        {
            stencilResource = ""; stencilImage = null; stencilImageKey = null; DisposeStencilOverlay(); EndStencilDrag(false);
        }

        /// <summary>覚えた ID をリソースと合わせる（毎回の OnGUI の始め）: 消えていれば外して知らせ、中身・色空間が変わっていれば作り直す。</summary>
        void SyncStencil()
        {
            if (string.IsNullOrEmpty(stencilResource)) { if (stencilImage != null) { stencilImage = null; stencilImageKey = null; DisposeStencilOverlay(); } return; }
            if (!Guid.TryParse(stencilResource, out var id) || !ImageResources.TryGetImage(id, out var image))
            {
                ClearStencil();
                NoteStencil(L.Tr("The stencil's image is no longer in this project; the stencil was removed."));
                return;
            }
            string key = StencilKey(image);
            if (stencilImage != null && key == stencilImageKey) return;
            try { stencilImage = new StencilImage(image.Content, image.ColorSpace, StencilMipBudgetBytes); stencilImageKey = key; DisposeStencilOverlay(); }
            catch (ResourceRefusedException ex) { ClearStencil(); NoteStencil(ex.Message); }
        }
        void NoteStencil(string text) { message = string.IsNullOrEmpty(message) ? text : message + " " + text; Repaint(); }

        // ───────── ストロークへ渡す ─────────

        /// <summary>今のブラシの設定に、ステンシル（使うなら）を足したもの。ストロークの始めに 1 回（Tools/TexturePaintWindow.Stroke.cs）。</summary>
        BrushSettings StrokeBrush(bool surface)
        {
            var s = GetBrush();
            s.Stencil = StrokeStencil(surface);
            return s;
        }

        /// <summary>今始めるストロークのステンシル（使わなければ null）: 2D はキャンバスの画素 → 画面 → 画像の写し、3D は置き場とカメラの写しを
        /// 覚えて画素ごとに点を渡す。色を受けるのは、マテリアルなら Base Color、そうでなければ今のチャンネル（マスクは量だけ）。</summary>
        BrushStencil StrokeStencil(bool surface)
        {
            strokeStencilFrame = null;
            if (!StencilApplies) return null;
            var frame = StencilFrameIn(surface ? surfaceRect : canvasRect);
            if (!frame.HasValue) return null;
            var colors = EditingMask ? new PaintChannel[0] : brush.material ? new[] { PaintChannel.Color } : new[] { channel };
            if (surface)
            {
                if (!preview.TryGetGuiProjection(surfaceRect, out strokeStencilProjection)) return null;
                strokeStencilFrame = frame;
                return new BrushStencil(stencilImage, stencilMode, stencilTiling, stencilInvert, null, colors);
            }
            return new BrushStencil(stencilImage, stencilMode, stencilTiling, stencilInvert, CanvasStencilMapping(frame.Value), colors);
        }

        /// <summary>キャンバスの点 → 画面（今の 2D の表示）→ ステンシルの画像、を 1 つの写しに。</summary>
        StencilMapping CanvasStencilMapping(StencilFrame frame)
        {
            CanvasViewNow().GuiAffine(out double a, out double b, out double c, out double d, out double e, out double g);
            frame.ImageAffine(out double p, out double q, out double r, out double s, out double t, out double u);
            return new StencilMapping(p * a + q * d, p * b + q * e, p * c + q * g + r, s * a + t * d, s * b + t * e, s * c + t * g + u);
        }

        /// <summary>3D のダブの画素を塗る（ステンシルがあれば、テクセルの点を画面へ写してそこのステンシルを読む）。</summary>
        void PaintSurfacePixels(SurfaceDabResult dab, SurfaceHit hit, float pressure)
        {
            if (!strokeStencilFrame.HasValue) { foreach (var pixel in dab.Pixels) stroke.ApplyPixel(pixel.X, pixel.Y, pixel.Coverage, pressure); return; }
            double footprint = SurfaceStencilFootprint(hit);
            foreach (var pixel in dab.Pixels) stroke.ApplyPixel(pixel.X, pixel.Y, pixel.Coverage, pressure, SurfaceStencilPoint(pixel.Position, footprint));
        }
        /// <summary>効果のブラシの 3D のダブの、画素ごとのステンシルの点（ステンシルを使わなければ null）。</summary>
        List<StencilPoint> SurfaceStencilPoints(SurfaceDabResult dab, SurfaceHit hit)
        {
            if (!strokeStencilFrame.HasValue) return null;
            double footprint = SurfaceStencilFootprint(hit);
            var points = new List<StencilPoint>(dab.Pixels.Count);
            foreach (var pixel in dab.Pixels) points.Add(SurfaceStencilPoint(pixel.Position, footprint));
            return points;
        }
        StencilPoint SurfaceStencilPoint(Vector3 position, double footprint)
        {
            // カメラの後ろ（見えるテクセルでは起きない）は、繰り返しでも読まない遠くの点にして塗らない
            var nowhere = new StencilPoint(-StencilImage.FarAway, -StencilImage.FarAway, footprint);
            if (!strokeStencilProjection.TryProject(position, out double gx, out double gy)) return nowhere;
            strokeStencilFrame.Value.ToImage(gx, gy, out double x, out double y);
            return Math.Abs(x) < StencilImage.FarAway && Math.Abs(y) < StencilImage.FarAway ? new StencilPoint(x, y, footprint) : nowhere;
        }
        /// <summary>ダブの所での、テクセル 1 つに当たる画像の画素の数（ミップの段を選ぶ足跡）: 当たった三角形の面積と UV の面積からテクセルの
        /// 大きさ（モデルの単位）を出し、画面の点にして、ステンシルの画素 / 点を掛ける。ダブの中では一定とみなす。</summary>
        double SurfaceStencilFootprint(SurfaceHit hit)
        {
            var geometry = preview.Geometry;
            if (geometry == null || hit.TriangleIndex < 0 || hit.TriangleIndex >= geometry.TriangleCount) return 1;
            var t = geometry.Triangles[hit.TriangleIndex];
            double area = Vector3.Cross(t.B - t.A, t.C - t.A).magnitude * .5;
            var e1 = t.UvB - t.UvA; var e2 = t.UvC - t.UvA;
            double uvArea = Math.Abs((double)e1.x * e2.y - (double)e1.y * e2.x) * .5 * document.Width * document.Height;
            if (!(area > 0) || !(uvArea > 0)) return 1;
            float texel = (float)Math.Sqrt(area / uvArea);
            double points = preview.WorldRadiusToGuiPoints(hit.Position, texel);
            double footprint = points * strokeStencilFrame.Value.ImagePerPoint;
            return double.IsNaN(footprint) || double.IsInfinity(footprint) || footprint < 0 ? 1 : footprint;
        }

        // ───────── キーとドラッグ ─────────

        /// <summary>T（押しているあいだ置き場を動かす）と N（押しているあいだ効かない）。ドラッグの最中の Esc は始めに戻す。受け持ったら true。</summary>
        bool HandleStencilKeys(Event e)
        {
            if (e.type != EventType.KeyDown) return false;
            if (stencilDrag != StencilDragKind.None && e.keyCode == KeyCode.Escape) { EndStencilDrag(true); NoteTookKey(e); e.Use(); Repaint(); return true; }
            if (GUIUtility.keyboardControl != 0 || e.control || e.command || e.alt) return false;
            if (e.keyCode == KeyCode.T) { stencilKeyHeld = true; NoteTookKey(e); e.Use(); Repaint(); return true; } // 押したままの繰り返しも同じ
            if (e.keyCode == KeyCode.N && !e.shift) { stencilIgnoreHeld = true; NoteTookKey(e); e.Use(); Repaint(); return true; }
            return false;
        }
        /// <summary>T・N を離した（キーを離したイベントは OnGUI の始めに見る）。T を離してもドラッグはボタンを離すまで続く。</summary>
        void NoteStencilKeyUp(Event e)
        {
            if (e.type != EventType.KeyUp) return;
            if (e.keyCode == KeyCode.T) { stencilKeyHeld = false; Repaint(); }
            else if (e.keyCode == KeyCode.N) { stencilIgnoreHeld = false; Repaint(); }
        }
        /// <summary>フォーカスを失ったとき: 押していた印を捨て、ドラッグは今の置き場で終える（キーを離したのを受け取れないので）。</summary>
        void ReleaseStencilInput() { stencilKeyHeld = false; stencilIgnoreHeld = false; EndStencilDrag(false); }

        /// <summary>T を押したまま、表示域（2D か 3D）でのドラッグ: 左 = 回す、中か Ctrl+左 = 動かす、右か Alt+左 = 大きさ。受け持ったら true。</summary>
        bool HandleStencilInput(Event e)
        {
            if (stencilDrag != StencilDragKind.None)
            {
                if (e.type == EventType.MouseDrag) { UpdateStencilDrag(e); e.Use(); Repaint(); return true; }
                if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp) { EndStencilDrag(false); e.Use(); Repaint(); return true; }
                return false;
            }
            if (!stencilKeyHeld || e.type != EventType.MouseDown || stroke != null || toolDragging) return false;
            var view = canvasRect.Contains(e.mousePosition) ? canvasRect : surfaceRect.Contains(e.mousePosition) ? surfaceRect : default;
            if (view.width <= 0) return false;
            var kind = e.button == 2 || e.button == 0 && (e.control || e.command) ? StencilDragKind.Move
                : e.button == 1 || e.button == 0 && e.alt ? StencilDragKind.Scale : e.button == 0 ? StencilDragKind.Rotate : StencilDragKind.None;
            if (kind == StencilDragKind.None) return false;
            if (CurrentStencilImage == null) { message = L.Tr("Choose a stencil image first (Properties ▸ Stencil)."); e.Use(); Repaint(); return true; }
            stencilDrag = kind; stencilDragView = view; stencilDragFrom = e.mousePosition;
            stencilDragStartCenter = stencilCenter; stencilDragStartSize = stencilSize; stencilDragStartAngle = stencilAngle; stencilDragSwept = 0;
            stencilDragLastAngle = StencilPointerAngle(e.mousePosition);
            GUIUtility.hotControl = GUIUtility.GetControlID(StencilDragHint, FocusType.Passive); e.Use(); Repaint(); return true;
        }
        Vector2 StencilCenterIn(Rect view) => new Vector2(view.x + stencilCenter.x * view.width, view.y + stencilCenter.y * view.height);
        float StencilPointerAngle(Vector2 pointer) { var d = pointer - StencilCenterIn(stencilDragView); return Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg; }

        void UpdateStencilDrag(Event e)
        {
            var p = e.mousePosition; var view = stencilDragView;
            switch (stencilDrag)
            {
                case StencilDragKind.Move:
                    stencilCenter = stencilDragStartCenter + new Vector2((p.x - stencilDragFrom.x) / view.width, (p.y - stencilDragFrom.y) / view.height);
                    break;
                case StencilDragKind.Scale:
                    // 右・上へ動かすと大きく、左・下へで小さく（200 点で e 倍）
                    stencilSize = Mathf.Clamp(stencilDragStartSize * Mathf.Exp(((p.x - stencilDragFrom.x) - (p.y - stencilDragFrom.y)) / 200f), MinStencilSize, MaxStencilSize);
                    break;
                case StencilDragKind.Rotate:
                    if ((p - StencilCenterIn(view)).magnitude < StencilRotateDeadZone) break;
                    float a = StencilPointerAngle(p); stencilDragSwept += Mathf.DeltaAngle(stencilDragLastAngle, a); stencilDragLastAngle = a;
                    float target = stencilDragStartAngle + stencilDragSwept;
                    if (e.shift) target = Mathf.Round(target / StencilRotateStep) * StencilRotateStep;
                    stencilAngle = CanvasView.NormalizeAngle(target);
                    break;
            }
        }
        /// <summary>ドラッグを終える（revert なら始めの置き場に戻す。Esc）。</summary>
        void EndStencilDrag(bool revert)
        {
            if (stencilDrag == StencilDragKind.None) return;
            if (revert) { stencilCenter = stencilDragStartCenter; stencilSize = stencilDragStartSize; stencilAngle = stencilDragStartAngle; }
            stencilDrag = StencilDragKind.None; if (GUIUtility.hotControl != 0) GUIUtility.hotControl = 0;
        }
        /// <summary>置き場を初めに戻す（中心・大きさ・角度）。</summary>
        internal void ResetStencilPlacement() { stencilCenter = DefaultStencilCenter; stencilSize = DefaultStencilSize; stencilAngle = 0; Repaint(); }
        /// <summary>T を押しているか、ステンシルを動かしているあいだ（ブラシのカーソルを出さない）。</summary>
        bool StencilHandling => stencilKeyHeld || stencilDrag != StencilDragKind.None;

        // ───────── 重ね表示 ─────────

        /// <summary>重ね表示を出すか: 画像があり、ブラシのツールで、N を押していない（T を押しているあいだはツールによらず出す）。</summary>
        bool StencilShown => (IsBrushTool && !stencilIgnoreHeld || StencilHandling) && CurrentStencilImage != null;

        /// <summary>表示域 view（今の GUI の座標。キャンバスはクリップの中）にステンシルを重ねる。T を押しているあいだは枠と中心の印も。</summary>
        void DrawStencilOverlay(Rect view)
        {
            if (!StencilShown) return;
            if (StencilHandling) EditorGUIUtility.AddCursorRect(view, stencilDrag == StencilDragKind.Move ? MouseCursor.MoveArrow : stencilDrag == StencilDragKind.Scale ? MouseCursor.ScaleArrow : MouseCursor.RotateArrow);
            if (Event.current.type != EventType.Repaint) return;
            var texture = StencilOverlayTexture(); var frame = StencilFrameIn(view);
            if (texture == null || !frame.HasValue) return;
            var f = frame.Value;
            bool repeatX = stencilTiling == StencilTiling.Horizontal || stencilTiling == StencilTiling.Both, repeatY = stencilTiling == StencilTiling.Vertical || stencilTiling == StencilTiling.Both;
            double u0 = 0, u1 = 1, v0 = 0, v1 = 1;
            if (repeatX || repeatY)
            {
                // 表示域の四隅が画像のどこに来るか（画像の幅・高さを 1 として）から、繰り返す軸の範囲を広げる（多くても片側 64 枚）
                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                foreach (var corner in new[] { new Vector2(view.xMin, view.yMin), new Vector2(view.xMax, view.yMin), new Vector2(view.xMin, view.yMax), new Vector2(view.xMax, view.yMax) })
                {
                    f.ToImage(corner.x, corner.y, out double x, out double y);
                    double u = x / f.ImageWidth, v = y / f.ImageHeight;
                    minU = Math.Min(minU, u); maxU = Math.Max(maxU, u); minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
                }
                if (repeatX) { u0 = Math.Max(-64, Math.Floor(minU)); u1 = Math.Min(65, Math.Ceiling(maxU)); }
                if (repeatY) { v0 = Math.Max(-64, Math.Floor(minV)); v1 = Math.Min(65, Math.Ceiling(maxV)); }
            }
            var rect = new Rect((float)(f.Center.x + (u0 - .5) * f.Width), (float)(f.Center.y - (v1 - .5) * f.Height), (float)((u1 - u0) * f.Width), (float)((v1 - v0) * f.Height));
            var color = GUI.color;
            GL.PushMatrix(); GL.modelview = GL.modelview * f.RotationMatrix();
            try
            {
                GUI.color = new Color(1, 1, 1, stencilOpacity);
                GUI.DrawTextureWithTexCoords(rect, texture, new Rect((float)u0, (float)v0, (float)(u1 - u0), (float)(v1 - v0)), true);
            }
            finally { GUI.color = color; GL.PopMatrix(); }
            if (StencilHandling) DrawStencilFrame(f);
        }
        /// <summary>T を押しているあいだの、画像 1 枚の枠と中心の十字（白と黒の二重の線）。</summary>
        static void DrawStencilFrame(StencilFrame f)
        {
            var corners = new Vector3[5];
            for (int i = 0; i < 4; i++)
            {
                double x = (i == 1 || i == 2 ? .5 : -.5) * f.Width, y = (i >= 2 ? .5 : -.5) * f.Height;
                corners[i] = new Vector3((float)(f.Center.x + f.Cos * x - f.Sin * y), (float)(f.Center.y + f.Sin * x + f.Cos * y));
            }
            corners[4] = corners[0];
            var c = f.Center; var old = Handles.color;
            Handles.color = new Color(0, 0, 0, .55f); Handles.DrawAAPolyLine(3, corners);
            Handles.DrawAAPolyLine(3, new Vector3(c.x - 7, c.y), new Vector3(c.x + 7, c.y)); Handles.DrawAAPolyLine(3, new Vector3(c.x, c.y - 7), new Vector3(c.x, c.y + 7));
            Handles.color = new Color(1, 1, 1, .9f); Handles.DrawAAPolyLine(1.2f, corners);
            Handles.DrawAAPolyLine(1.2f, new Vector3(c.x - 7, c.y), new Vector3(c.x + 7, c.y)); Handles.DrawAAPolyLine(1.2f, new Vector3(c.x, c.y - 7), new Vector3(c.x, c.y + 7));
            Handles.color = old;
        }
        /// <summary>3D ビューへの重ね表示（3D の絵を描いた後に、表示域のクリップの中で）。</summary>
        void DrawStencilOverlay3D()
        {
            if (!StencilShown || surfaceRect.width <= 0) return;
            GUI.BeginClip(surfaceRect);
            try { DrawStencilOverlay(new Rect(0, 0, surfaceRect.width, surfaceRect.height)); }
            finally { GUI.EndClip(); }
        }

        /// <summary>重ね表示のテクスチャ（長い辺 <see cref="StencilOverlaySide"/> まで縮めた写し）。量として読むなら輝度（反転）の灰色に、色として
        /// 読むリニアの画像は sRGB にして、塗られる値の見た目にする。</summary>
        Texture2D StencilOverlayTexture()
        {
            var image = CurrentStencilImage;
            if (image == null) return null;
            var mode = BrushStencil.Resolve(stencilMode, image);
            string key = stencilImageKey + ":" + mode + ":" + stencilInvert + ":" + stencilTiling;
            if (stencilOverlay != null && key == stencilOverlayKey) return stencilOverlay;
            DisposeStencilOverlay();
            var (rgba, w, h) = image.Content.Preview(StencilOverlaySide);
            bool linear = mode == StencilMode.Color && image.ColorSpace == ResourceColorSpace.Linear;
            for (int o = 0; o < rgba.Length; o += 4)
            {
                if (mode == StencilMode.Mask)
                {
                    byte v = FillImageColor.Luminance(rgba[o], rgba[o + 1], rgba[o + 2]); if (stencilInvert) v = (byte)(255 - v);
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = v;
                }
                else if (linear) for (int k = 0; k < 3; k++) rgba[o + k] = FillImageColor.Convert(FillImageConversion.LinearToSrgb, rgba[o + k]);
            }
            bool repeatX = stencilTiling == StencilTiling.Horizontal || stencilTiling == StencilTiling.Both, repeatY = stencilTiling == StencilTiling.Vertical || stencilTiling == StencilTiling.Both;
            stencilOverlay = new Texture2D(w, h, TextureFormat.RGBA32, true, true)
            {
                hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Trilinear, anisoLevel = 4,
                wrapModeU = repeatX ? TextureWrapMode.Repeat : TextureWrapMode.Clamp, wrapModeV = repeatY ? TextureWrapMode.Repeat : TextureWrapMode.Clamp
            };
            stencilOverlay.SetPixelData(rgba, 0); stencilOverlay.Apply(true, false);
            stencilOverlayKey = key;
            return stencilOverlay;
        }
        void DisposeStencilOverlay() { if (stencilOverlay != null) DestroyImmediate(stencilOverlay); stencilOverlay = null; stencilOverlayKey = null; }

        // ───────── プロパティの欄（描くツールの「ステンシル」のタブ） ─────────

        static string StencilModeName(StencilMode mode, StencilImage image)
        {
            switch (mode)
            {
                case StencilMode.Mask: return L.TrIn("stencil", "Amount (mask)");
                case StencilMode.Color: return L.TrIn("stencil", "Colour");
                default: return image == null ? L.TrIn("stencil", "Automatic") : L.TrIn("stencil", "Automatic") + " (" + StencilModeName(BrushStencil.Resolve(mode, image), image) + ")";
            }
        }
        static string StencilTilingName(StencilTiling tiling)
        {
            switch (tiling)
            {
                case StencilTiling.Horizontal: return L.TrIn("stencil", "Horizontal");
                case StencilTiling.Vertical: return L.TrIn("stencil", "Vertical");
                case StencilTiling.Both: return L.TrIn("stencil", "Both");
                default: return L.TrIn("stencil", "No tiling");
            }
        }

        void StencilSection(UiRows rows)
        {
            if (!ToolSection(rows, "brush-stencil", L.Tr("Stencil"), "texture")) return;
            var id = StencilResource;
            ImageResource image = id.HasValue && ImageResources.TryGetImage(id.Value, out var found) ? found : null;
            var row = rows.Row(24);
            PaintGui.Text(new Rect(row.x, row.y, PropertyLabelWidth, row.height), L.TrIn("stencil", "Image"), PaintTheme.Label, GUI.enabled ? PaintTheme.Text : PaintTheme.TextDisabled);
            var box = Mark("stencil-image", new Rect(row.x + PropertyLabelWidth, row.y, row.width - PropertyLabelWidth - (image != null ? 28 : 0), row.height));
            DrawStencilImageBox(box, image);
            if (image != null && PaintGui.IconButton(Mark("stencil-clear", new Rect(row.xMax - 24, row.y, 24, row.height)), "close", L.Tr("Stop using the stencil"), false, stroke == null, 15))
                TryAction(() => SetStencil(null));
            if (image == null || CurrentStencilImage == null)
            {
                NoteRow(rows, L.Tr("An image laid over the 2D canvas and the 3D view that the brush paints through. Drop an image from the Assets panel here, or click to choose one of this project's images."));
                rows.Space(4); return;
            }
            var mode = BrushStencil.Resolve(stencilMode, stencilImage);
            ChoiceDropdown(Mark("stencil-mode", rows.Row()), L.TrIn("stencil", "Reads as"), stencilMode, (StencilMode[])Enum.GetValues(typeof(StencilMode)), m => StencilModeName(m, stencilImage),
                m => stencilMode = m, L.Tr("Amount: the image's brightness is how much paint gets through (white paints, black and transparent hold back). Colour: the brush paints the image's colours, its alpha is the amount. Automatic: amount for a grey image, colour otherwise."));
            var c = UiRows.Split(rows.Row(), 2, 6);
            stencilInvert = PaintGui.FitToggle(Mark("stencil-invert", c[0]), L.TrIn("stencil", "Invert"), stencilInvert, L.Tr("Black lets the paint through and white holds it back (transparent still holds back)."), mode == StencilMode.Mask);
            PaintGui.FitDropdown(Mark("stencil-tiling", c[1]), null, StencilTilingName(stencilTiling), at =>
            {
                var menu = new GenericMenu();
                foreach (StencilTiling t in Enum.GetValues(typeof(StencilTiling))) { var item = t; menu.AddItem(new GUIContent(StencilTilingName(item)), item == stencilTiling, () => { stencilTiling = item; Repaint(); }); }
                menu.DropDown(at);
            }, L.Tr("Repeat the image beyond its edges across the view. Without tiling nothing is painted outside the image."));
            stencilOpacity = PercentSlider(Mark("stencil-opacity", rows.SliderRow()), L.Tr("Overlay opacity"), stencilOpacity, 0, 1, L.Tr("How strongly the stencil is shown over the views (display only)."));
            float size = PercentSlider(Mark("stencil-size", rows.SliderRow()), L.TrIn("stencil", "Size"), stencilSize, MinStencilSize, 4, L.Tr("The image's height as a share of the view's height."));
            if (size != stencilSize) StencilSize = size;
            float angle = PaintGui.FitSlider(Mark("stencil-angle", rows.SliderRow()), L.Tr("Angle"), stencilAngle, -180, 180, "0", "°", L.Tr("Rotation on the screen (clockwise)."));
            if (angle != stencilAngle) StencilAngle = angle;
            if (PaintGui.FitButton(Mark("stencil-reset", rows.Row(24)), L.Tr("Reset placement"), false, stencilDrag == StencilDragKind.None, L.Tr("Back to the middle of the view, the first size and no rotation.")))
                ResetStencilPlacement();
            NoteRow(rows, L.Tr("Hold T and drag on the 2D canvas or the 3D view: left turns it (Shift: 15° steps), middle or Ctrl+left moves it, right or Alt+left resizes it. Hold N to paint without it. It stays on the screen when the camera or the canvas moves."), NoteKind.Info);
            if (mode == StencilMode.Color && brush.material && !EditingMask)
                NoteRow(rows, L.Tr("Colour with material painting: Base Color takes the image's colours; the other channels paint their own values through the image's alpha."), NoteKind.Info);
            if (mode == StencilMode.Color && EditingMask)
                NoteRow(rows, L.Tr("A mask takes only the amount: the image's alpha in Colour mode."), NoteKind.Info);
            rows.Space(4);
        }

        /// <summary>ステンシルの画像の箱: サムネイルと名前。押すとこのプロジェクトの画像の一覧、アセットのパネル・Project ウィンドウからのドロップを受ける。</summary>
        void DrawStencilImageBox(Rect box, ImageResource image)
        {
            var e = Event.current; bool enabled = GUI.enabled && stroke == null;
            bool hover = enabled && box.Contains(e.mousePosition);
            bool dropping = enabled && (e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && box.Contains(e.mousePosition) && DroppedImageSource(out _, out _);
            PaintGui.Rounded(box, dropping ? PaintTheme.AccentDim : hover ? PaintTheme.ControlHover : PaintTheme.ControlBg, 3);
            PaintGui.Outline(box, dropping || hover ? PaintTheme.AccentDim : PaintTheme.Border, 1, 3);
            var thumb = new Rect(box.x + 3, box.y + 3, box.height - 6, box.height - 6);
            if (e.type == EventType.Repaint)
            {
                PaintGui.Checker(thumb, 3);
                if (image != null) { var t = CachedThumbnail(AssetKey(AssetSource.Project, image.Id.ToString("D")) + ":" + image.ContentHash, () => image.Content.Preview((int)AssetThumb)); if (t != null) GUI.DrawTexture(thumb, t, ScaleMode.ScaleToFit, true); }
                else PaintGui.Icon(thumb, "texture", PaintTheme.TextDim, 13);
                PaintGui.Outline(thumb, PaintTheme.Border, 1, 0);
            }
            string name = image != null ? image.Name : L.Tr("None (drop an image here)");
            var label = new Rect(thumb.xMax + 6, box.y, box.xMax - thumb.xMax - 26, box.height);
            PaintGui.Text(label, PaintGui.Fit(name, label.width, PaintTheme.Label, false), PaintTheme.Label, !enabled ? PaintTheme.TextDisabled : image != null ? PaintTheme.Text : PaintTheme.TextDim);
            PaintGui.Icon(new Rect(box.xMax - 22, box.y, 20, box.height), "arrow_drop_down", PaintTheme.TextDim, 16);
            PaintGui.Tooltip(box, (image != null ? image.Name + " (" + image.Width + " × " + image.Height + ")\n" : "") + L.Tr("Click to choose one of this project's images; drop one from the Assets panel or the Project window."));
            if (e.type == EventType.MouseDown && e.button == 0 && hover) { e.Use(); OpenStencilMenu(box); }
            if (dropping)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); TryAction(SetStencilFromDrop); }
                e.Use();
            }
        }
        void SetStencilFromDrop()
        {
            if (!DroppedImageSource(out string key, out var texture)) return;
            ImageResource image;
            if (key != null) { if (!TryParseAssetKey(key, out var source, out string id)) return; image = ImportAsset(source, id); }
            else image = ImportUnityTexture(texture);
            SetStencil(image.Id);
        }
        void OpenStencilMenu(Rect at)
        {
            var menu = new GenericMenu(); var images = ImageResources.Images; var current = StencilResource ?? Guid.Empty;
            if (images.Count == 0) menu.AddDisabledItem(new GUIContent(L.Tr("This project has no images yet (import them in the Assets panel)")));
            foreach (var r in images)
            {
                var image = r;
                menu.AddItem(new GUIContent(image.Name + "  (" + image.Width + " × " + image.Height + ")"), image.Id == current, () => { TryAction(() => SetStencil(image.Id)); Repaint(); });
            }
            if (current != Guid.Empty) { menu.AddSeparator(""); menu.AddItem(new GUIContent(L.Tr("None (no stencil)")), false, () => { TryAction(() => SetStencil(null)); Repaint(); }); }
            menu.DropDown(at);
        }
    }

    /// <summary>
    /// ステンシルの、1 つの表示域での置き場: 中心（GUI の座標）、画面の上の幅・高さ（点）、角度（時計回りが正）。GUI の点 → 画像の画素（左下が
    /// 原点、y は上向き）の写しと、その係数・描くときの回転の行列。
    /// </summary>
    internal readonly struct StencilFrame
    {
        public readonly Vector2 Center;
        public readonly double Width, Height, Cos, Sin;
        public readonly int ImageWidth, ImageHeight;

        public StencilFrame(Rect view, Vector2 center, float size, float angle, int imageWidth, int imageHeight)
        {
            Center = new Vector2(view.x + center.x * view.width, view.y + center.y * view.height);
            Height = Math.Max(1e-3, (double)size * view.height); Width = Height * imageWidth / imageHeight;
            ImageWidth = imageWidth; ImageHeight = imageHeight;
            float a = CanvasView.NormalizeAngle(angle);
            if (a == 0) { Cos = 1; Sin = 0; }
            else if (a == 90) { Cos = 0; Sin = 1; }
            else if (a == 180) { Cos = -1; Sin = 0; }
            else if (a == -90) { Cos = 0; Sin = -1; }
            else { double r = a * Math.PI / 180; Cos = Math.Cos(r); Sin = Math.Sin(r); }
        }

        /// <summary>画像の画素 1 つの GUI の点に対する比（画像の画素 / 点）。</summary>
        public double ImagePerPoint => ImageHeight / Height;

        /// <summary>GUI の点 (gx, gy) の、画像の画素の座標（左下が原点、y は上向き。画像の外も返す）。</summary>
        public void ToImage(double gx, double gy, out double x, out double y)
        {
            double dx = gx - Center.x, dy = gy - Center.y;
            double lx = Cos * dx + Sin * dy, ly = -Sin * dx + Cos * dy; // 角度を戻す
            x = (lx / Width + .5) * ImageWidth; y = (.5 - ly / Height) * ImageHeight;
        }
        /// <summary><see cref="ToImage"/> の係数: x = XX·gx + XY·gy + X0、y = YX·gx + YY·gy + Y0。</summary>
        public void ImageAffine(out double xx, out double xy, out double x0, out double yx, out double yy, out double y0)
        {
            double kx = ImageWidth / Width, ky = ImageHeight / Height, cx = Center.x, cy = Center.y;
            xx = kx * Cos; xy = kx * Sin; x0 = ImageWidth * .5 - xx * cx - xy * cy;
            yx = ky * Sin; yy = -ky * Cos; y0 = ImageHeight * .5 - yx * cx - yy * cy;
        }
        /// <summary>描くときに GL.modelview に掛ける、中心のまわりに角度だけ回す行列（回していない矩形を描くと回った絵になる。
        /// <see cref="CanvasView.ImageMatrix"/> と同じく、GUI.matrix ではクリップも回ってしまうので GL の行列に掛ける）。</summary>
        public Matrix4x4 RotationMatrix()
        {
            var m = Matrix4x4.identity; double cx = Center.x, cy = Center.y;
            m.m00 = (float)Cos; m.m01 = (float)-Sin; m.m03 = (float)(cx - Cos * cx + Sin * cy);
            m.m10 = (float)Sin; m.m11 = (float)Cos; m.m13 = (float)(cy - Sin * cx - Cos * cy);
            return m;
        }
    }
}
