using UnityEngine;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>.ylp を取り込んだ結果（インスペクターとテスト用）。<see cref="YlpImporter"/> がサブアセットとして持ち、Project
    /// ウィンドウには出さない。Unity は主オブジェクトの名前をファイル名に置き換えるので、主テクスチャがどのチャンネルかは
    /// 名前からは分からず、ここに残す。</summary>
    internal sealed class YlpImportInfo : ScriptableObject
    {
        /// <summary>ドキュメント（＝テクスチャ）の大きさ。読めなかったときは 0。</summary>
        public int width, height;
        /// <summary>テクスチャにしたチャンネル（列挙の順）。読めなかったときは空。</summary>
        public PaintChannel[] channels = new PaintChannel[0];
        /// <summary>合成済みの画像が無い・使えないため、正本（document.utpaint）から合成したか。</summary>
        public bool fromNativeDocument;
        /// <summary>取り込みのエラーの理由（成功したときは空）。警告はコンソールにだけ出す。</summary>
        public string error = "";
    }
}
