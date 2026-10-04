namespace Yozolab.YoluPainter.Core
{
    public sealed partial class PaintDocument
    {
        /// <summary>文書を所有するスレッドで取る、履歴を含まない保存用の写し。ID・属性・選択を保ち、タイルは
        /// コピーオンライトで共有する。呼び出し後は元を編集してよい。写しは一つの読み手に渡し、読み終わるまで編集しない。</summary>
        public PaintDocument CaptureSnapshot()
        {
            EnsureNoStroke();
            var copy = new PaintDocument(Width, Height, TileSize, 0, Id);
            foreach (var layer in layers) copy.layers.Add(copy.CloneLayer(layer, layer.Id, layer.Name, preserveIds: true));
            copy.selection = selection; // SelectionMask と FilterEffect/Settings は不変。
            copy.normalSettings = normalSettings;
            copy.idColors = idColors; // IdColorAssignments は不変。
            copy.Revision = Revision;
            return copy;
        }
    }
}
