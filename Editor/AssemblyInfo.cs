using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Yozolab.YoluPainter.Tests")]
// Live Link（unsafe のコードはこのアセンブリだけに置く）がマテリアルの流し込みの決まりと文字列表を使う。
[assembly: InternalsVisibleTo("Yozolab.YoluPainter.Editor.LiveLink")]
// devcontainer の unity-do.sh run がスニペットをこの名前のアセンブリにして読み込む。
[assembly: InternalsVisibleTo("YoluPainterSnippet")]
