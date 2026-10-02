using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>このフォルダを指すためだけの型（ファイル名とクラス名を揃えておく必要がある）。<see cref="PoCatalog"/> はこの
    /// スクリプトの在りかから .po を探すので、パッケージがどこに入っても（Packages・Assets・VPM のキャッシュ）見つかる。</summary>
    internal sealed class LocalizationAnchor : ScriptableObject { }
}
