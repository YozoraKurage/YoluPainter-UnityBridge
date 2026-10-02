using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>.ylp を取り込んだ結果で、.ylp のアセットの主オブジェクト（<see cref="YlpImporter"/>）。テクスチャではないので、マテリアルの
    /// テクスチャ欄には入らない。</summary>
    internal sealed class YlpImportInfo : ScriptableObject
    {
        /// <summary>ドキュメントの大きさ。読めなかったときは 0。</summary>
        public int width, height;
        /// <summary>使っているチャンネル（列挙の順）。読めなかったときは空。</summary>
        public PaintChannel[] channels = new PaintChannel[0];
        /// <summary>合成済みの画像が無いため、正本（document.utpaint）から読んだか。</summary>
        public bool fromNativeDocument;
        /// <summary>中身の形式（YlpFormat。ylp.json の無いものは 1）と、書いたアプリ（分からなければ空）。</summary>
        public int format;
        public string savedBy = "", createdBy = "";
        /// <summary>取り込みのエラーの理由（成功したときは空）。</summary>
        public string error = "";
    }
}
