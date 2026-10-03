using UnityEngine;

namespace Yozolab.YoluPainter.Editor.Preview
{
    /// <summary>モデルの空間の点を 3D ビューの GUI の座標へ写す（<see cref="IsolatedModelPreview.TryWorldToGui"/> と同じ写しを、行列 1 つで）。</summary>
    public readonly struct GuiProjection
    {
        readonly Matrix4x4 worldToClip; readonly Rect rect;
        public GuiProjection(Matrix4x4 worldToClip, Rect rect) { this.worldToClip = worldToClip; this.rect = rect; }
        /// <summary>点の GUI の座標（double）。カメラの後ろ（または真横）なら false。</summary>
        public bool TryProject(Vector3 p, out double x, out double y)
        {
            var m = worldToClip;
            double w = (double)m.m30 * p.x + (double)m.m31 * p.y + (double)m.m32 * p.z + m.m33;
            x = y = 0;
            if (!(w > 1e-12)) return false;
            double cx = ((double)m.m00 * p.x + (double)m.m01 * p.y + (double)m.m02 * p.z + m.m03) / w;
            double cy = ((double)m.m10 * p.x + (double)m.m11 * p.y + (double)m.m12 * p.z + m.m13) / w;
            x = rect.x + (cx * .5 + .5) * rect.width; y = rect.y + (1 - (cy * .5 + .5)) * rect.height;
            return true;
        }
    }
}
