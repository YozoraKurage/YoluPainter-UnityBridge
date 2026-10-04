using System;
using Yozolab.YoluPainter.Core.MeshMaps;

namespace Yozolab.YoluPainter.Core
{
    public sealed partial class PaintDocument
    {
        IdColorAssignments idColors = IdColorAssignments.Empty;
        public IdColorAssignments IdColors => idColors;
        /// <summary>文書の手動 ID 色。画素とレイヤーを変更せず 1 回の Undo。焼いた ID の期待値は呼び手がこの Key を使う。</summary>
        public void SetIdColors(IdColorAssignments colors, bool coalesce = false)
        {
            EnsureNoStroke(); if (colors == null) throw new ArgumentNullException(nameof(colors));
            var old = idColors; if (old.Equals(colors)) return;
            Execute(new DelegateCommand(() => idColors = colors, () => idColors = old, 128 + (old.Colors.Count + colors.Colors.Count) * 16L), coalesce ? (object)"idColors" : null);
        }
    }
}
