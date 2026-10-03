using System;
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
        public int resourceCount, imageCount, smartMaterialCount, smartMaskCount, brushCount, materialCount;
        public int format;
        public string savedBy = "", createdBy = "";
        /// <summary>テクスチャセットの一覧（project.json の並び。形式 2 までのファイルは 1 つ）。上の大きさ・チャンネルは今のセットのもの。</summary>
        public TextureSetSummary[] textureSets = new TextureSetSummary[0];
        /// <summary>取り込みのエラーの理由（成功したときは空）。</summary>
        public string error = "";

        /// <summary>1 つのテクスチャセットの名前・スロット・大きさ・使っているチャンネル。</summary>
        [Serializable] internal sealed class TextureSetSummary
        {
            public string name = "";
            public int materialSlot;
            public int width, height;
            public PaintChannel[] channels = new PaintChannel[0];
            /// <summary>合成済みの画像が無い・使えないため、正本から読んだか。</summary>
            public bool fromNativeDocument;
            /// <summary>保存したときの今のセットか。</summary>
            public bool current;
        }
    }
}
