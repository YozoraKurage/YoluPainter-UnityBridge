using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// 3D ビューを「変わったときだけ」描く窓の側（描く・描かないの判断はプレビュー: IsolatedModelPreview.RenderCache.cs）: 上限の設定を渡し、
    /// 上限で描かなかった変更・シェーダーのコンパイルの待ちを Tick で拾って描き直させる（3D ビューが見えているときだけ）。確かめる表示として、
    /// ステータスバーに「窓の描き直し N 回/秒・3D の描画 M 回/秒」を出せる（表示 ▸ 描き直しの回数。窓の状態）。
    /// </summary>
    public sealed partial class TexturePaintWindow
    {
        [SerializeField] bool showRedrawRate;
        /// <summary>窓の描き直し（OnGUI の Repaint）の回数。</summary>
        internal int WindowRepaints { get; private set; }
        int rateRepaints, rate3D, rateStartRepaints, rateStart3D; double rateStart = double.NegativeInfinity;
        internal bool ShowRedrawRate { get => showRedrawRate; set { showRedrawRate = value; Repaint(); } }

        void ApplyPreviewFrameRate() { if (preview != null) preview.FrameRateLimit = PainterSettings.PreviewFrameRateLimit; }

        /// <summary>OnGUI の Repaint のたびに数える（1 秒ごとに回数/秒を決め直す）。</summary>
        void CountRepaint()
        {
            WindowRepaints++;
            double now = EditorApplication.timeSinceStartup; int renders = preview != null ? preview.RenderCount : 0;
            if (now - rateStart < 1) return;
            if (rateStart > double.NegativeInfinity) { double span = now - rateStart; rateRepaints = (int)System.Math.Round((WindowRepaints - rateStartRepaints) / span); rate3D = (int)System.Math.Round((renders - rateStart3D) / span); }
            rateStart = now; rateStartRepaints = WindowRepaints; rateStart3D = renders;
        }

        /// <summary>Tick から: 3D ビューが見えていて、プレビューが描き直したいとき（上限で描かなかった変更の時刻が来た・コンパイルの待ち）に描き直させる。</summary>
        void RepaintPreviewIfWanted()
        {
            if (preview != null && surfaceRect.width > 0 && preview.WantsRepaint()) Repaint();
        }

        /// <summary>ステータスバーの右に足す文字（表示していなければ空）。</summary>
        string RedrawRateText() => showRedrawRate ? L.Tr("Redraws {0}/s · 3D {1}/s", rateRepaints, rate3D) + "   " : "";
    }
}
