using System;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 2D キャンバスの表示の写し: キャンバスの画素の座標（左下が原点、y は上向き）と GUI の座標（y は下向き）の間。
    /// 文書を表示域に収める大きさ × 拡大率で縮め、表示域の中心 + パンを画像の中心に置き、画像の中心のまわりで左右を反転してから
    /// 回す。回転は画面の上で時計回りが正（GUI の y が下向きなので、GUIUtility.RotateAroundPivot と同じ向き）。
    /// 表示だけの写しで、正本は画素の座標のまま（描く入力はこれの逆で画素の座標に戻す）。
    /// 回転も反転も無いときは前からの float の式そのまま（表示も入力の画素の座標も前とバイト単位で同じ）。回っているときは double で
    /// 求め、90° の倍数の cos・sin はちょうどの値を使う。
    /// </summary>
    internal readonly struct CanvasView
    {
        /// <summary>回転を戻さない（左右反転しない）ときの画像の矩形。表示の中心 + パンを中心に、拡大率を掛けた大きさ。</summary>
        public readonly Rect Image;
        /// <summary>画面の上の回転（度、時計回りが正、(-180, 180]）。</summary>
        public readonly float Angle;
        public readonly bool Flip;
        readonly int width, height;
        readonly double cos, sin, scale, cx, cy;

        public CanvasView(Rect view, int width, int height, float zoom, Vector2 pan, float angle, bool flip)
        {
            this.width = width; this.height = height;
            float fit = Mathf.Min(view.width / width, view.height / height) * zoom;
            float w = width * fit, h = height * fit;
            Image = new Rect(view.center.x - w * .5f + pan.x, view.center.y - h * .5f + pan.y, w, h);
            Angle = NormalizeAngle(angle); Flip = flip;
            CosSin(Angle, out cos, out sin);
            scale = (double)Image.width / width;
            cx = Image.x + Image.width * .5; cy = Image.y + Image.height * .5;
        }

        /// <summary>回転も反転も無い（前からの軸に沿った表示）。</summary>
        public bool AxisAligned => Angle == 0 && !Flip;
        /// <summary>画素 1 つの GUI の大きさ（点）。</summary>
        public float PixelSize => Image.width / width;
        /// <summary>画像の中心（GUI の座標）。回転と反転はこの点のまわり。</summary>
        public Vector2 Center => new Vector2((float)cx, (float)cy);

        /// <summary>角度を (-180, 180] に。</summary>
        public static float NormalizeAngle(float degrees)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees)) return 0;
            float a = Mathf.Repeat(degrees + 180, 360) - 180;
            return a <= -180 ? 180 : a == 0 ? 0 : a; // -0 も 0 に
        }

        /// <summary>cos と sin（90° の倍数はちょうどの値。そうしないと 180° で sin が 1e-7 ほど残り、往復がずれる）。</summary>
        static void CosSin(float degrees, out double c, out double s)
        {
            if (degrees == 0) { c = 1; s = 0; return; }
            if (degrees == 90) { c = 0; s = 1; return; }
            if (degrees == 180) { c = -1; s = 0; return; }
            if (degrees == -90) { c = 0; s = -1; return; }
            double r = degrees * Math.PI / 180; c = Math.Cos(r); s = Math.Sin(r);
        }

        /// <summary>キャンバスの点（画素の座標、範囲外も可）の GUI の座標。</summary>
        public Vector2 ToGui(double x, double y)
        {
            if (AxisAligned) return new Vector2(Image.x + (float)x / width * Image.width, Image.y + (1 - (float)y / height) * Image.height);
            double ux = scale * (x - width * .5), uy = -scale * (y - height * .5);
            if (Flip) ux = -ux;
            return new Vector2((float)(cx + cos * ux - sin * uy), (float)(cy + sin * ux + cos * uy));
        }
        public Vector2 ToGui(Vector2 p) => ToGui(p.x, p.y);

        /// <summary>GUI の座標をキャンバスの画素の座標（左下が原点、範囲外も返す）に。</summary>
        public Vector2 ToCanvas(Vector2 gui)
        {
            if (AxisAligned) return new Vector2((gui.x - Image.x) / Image.width * width, (1 - (gui.y - Image.y) / Image.height) * height);
            ToCanvas(gui, out double x, out double y); return new Vector2((float)x, (float)y);
        }
        /// <summary>GUI の座標をキャンバスの画素の座標に（double。ストロークの点はこちら）。</summary>
        public void ToCanvas(Vector2 gui, out double x, out double y)
        {
            if (AxisAligned) { x = (gui.x - Image.x) / Image.width * width; y = (1 - (gui.y - Image.y) / Image.height) * height; return; }
            double dx = gui.x - cx, dy = gui.y - cy;
            double ux = cos * dx + sin * dy, uy = -sin * dx + cos * dy;
            if (Flip) ux = -ux;
            x = ux / scale + width * .5; y = -uy / scale + height * .5;
        }

        /// <summary>キャンバスの点 (x, y) から GUI の座標への写しの係数（double）: gui.x = XX·x + XY·y + X0、gui.y = YX·x + YY·y + Y0。
        /// <see cref="ToGui(double, double)"/> と同じ写し（回っていないときの float の式とは丸めだけが違う。ステンシルの写しが使う）。</summary>
        public void GuiAffine(out double xx, out double xy, out double x0, out double yx, out double yy, out double y0)
        {
            if (AxisAligned)
            {
                xx = (double)Image.width / width; xy = 0; x0 = Image.x;
                yx = 0; yy = -(double)Image.height / height; y0 = (double)Image.y + Image.height;
                return;
            }
            // ux = f·s·(x − w/2)、uy = −s·(y − h/2)、gui = c + [cos −sin; sin cos]·(ux, uy)
            double f = Flip ? -1 : 1, hx = width * .5, hy = height * .5;
            xx = cos * f * scale; xy = sin * scale; x0 = cx - xx * hx - xy * hy;
            yx = sin * f * scale; yy = -cos * scale; y0 = cy - yx * hx - yy * hy;
        }

        /// <summary>画像を描くときに GL.modelview に掛ける行列（画像の中心のまわりに反転してから回す。<see cref="Image"/> の矩形をそのまま
        /// 描くと、回って反転した画像になる）。GUI.matrix ではなく GL の行列に掛けるのは、GUI.matrix だとクリップも一緒に回ってしまい
        /// （GUI のシェーダーはクリップを GUI.matrix の前の座標で切る）、表示域の外に画像がはみ出すため。</summary>
        public Matrix4x4 ImageMatrix()
        {
            double f = Flip ? -1 : 1, a = cos * f, b = -sin, c = sin * f, d = cos; // [a b; c d] = 回転 × 反転
            var m = Matrix4x4.identity;
            m.m00 = (float)a; m.m01 = (float)b; m.m03 = (float)(cx - a * cx - b * cy);
            m.m10 = (float)c; m.m11 = (float)d; m.m13 = (float)(cy - c * cx - d * cy);
            return m;
        }

        /// <summary>画面の上の向き（GUI の座標の軸、y は下向き）をキャンバスの向き（y は上向き）に。長さは変えない（拡大率は掛けない）。</summary>
        public Vector2 ScreenToCanvasDirection(Vector2 screen)
        {
            if (AxisAligned) return new Vector2(screen.x, -screen.y);
            double ux = cos * screen.x + sin * screen.y, uy = -sin * screen.x + cos * screen.y;
            if (Flip) ux = -ux;
            return new Vector2((float)ux, (float)-uy);
        }
    }
}
