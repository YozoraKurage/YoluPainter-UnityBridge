using System.Collections.Generic;
using System.Linq;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor.Preview;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>書き出しのパディング: 書き出す画像の、テクスチャセットの UV の外のテクセルに、UV の境目の色を塗り広げる（Core の <see cref="TexturePadding"/>、
    /// 量はプロジェクトの設定 <see cref="PainterSettings.ExportPadding"/>）。UV は 3D ビューに読み込んだモデルから取るので、モデルが無ければ塗り広げずに知らせる。
    /// 正本・.ylp の中の合成の PNG・表示は変えない（書き出すファイルだけ）。</summary>
    public sealed partial class TexturePaintWindow
    {
        SurfaceGeometry paddingGeometry; string paddingSlots; int paddingWidth, paddingHeight; bool[] paddingCoverage;

        /// <summary>テクスチャセットの UV が覆うテクセルの印（3D ビューの形・スロット・大きさが同じなら覚えたものを使う）。モデルが無ければ null。</summary>
        internal bool[] ExportCoverage(TextureSet set)
        {
            var geometry = preview != null && preview.HasModel ? preview.Geometry : null;
            if (geometry == null || set == null) return null;
            var d = set.Document;
            string slots = string.Join(",", set.Slots);
            if (!ReferenceEquals(geometry, paddingGeometry) || paddingSlots != slots || paddingWidth != d.Width || paddingHeight != d.Height)
            {
                double w = d.Width, h = d.Height;
                paddingCoverage = TexturePadding.Coverage(d.Width, d.Height, geometry.Triangles.Where(t => set.PaintsSlot(t.MaterialSlot)) // マテリアルを使う全部のスロット
                    .Select(t => ((double)t.UvA.x * w, (double)t.UvA.y * h, (double)t.UvB.x * w, (double)t.UvB.y * h, (double)t.UvC.x * w, (double)t.UvC.y * h)));
                paddingGeometry = geometry; paddingSlots = slots; paddingWidth = d.Width; paddingHeight = d.Height;
            }
            return paddingCoverage;
        }

        /// <summary>書き出す画像（straight RGBA8、左下が原点、そのセットの大きさ）に、設定の量だけパディングを掛けた画像を返す。掛けられない理由は notes に
        /// 1 度だけ足す（同じ理由を繰り返さない）。設定が 0 なら、そのまま返す。</summary>
        internal byte[] PadForExport(TextureSet set, byte[] rgba, ICollection<string> notes)
        {
            int padding = PainterSettings.ExportPadding;
            if (padding == 0) return rgba;
            var coverage = ExportCoverage(set);
            string why = coverage == null ? L.Tr("No export padding: load the model in the 3D view so its UVs are known.")
                : !coverage.Contains(true) ? L.Tr("No export padding for {0}: no UV triangle of its material.", set.Name) : null;
            if (why != null) { if (notes != null && !notes.Contains(why)) notes.Add(why); return rgba; }
            var d = set.Document;
            return TexturePadding.Dilate(rgba, d.Width, d.Height, coverage, padding, PainterSettings.StrokeBudgetBytes);
        }
    }
}
