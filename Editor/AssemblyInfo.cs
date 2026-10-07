using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Yozolab.YoluPainter.Tests")]
// Live Link が文字列表（L）と、Preferences に並べるスタンドアロン版のおすすめの入切を使う。
[assembly: InternalsVisibleTo("Yozolab.YoluPainter.Editor.LiveLink")]
// devcontainer の unity-do.sh run がスニペットをこの名前のアセンブリにして読み込む。
[assembly: InternalsVisibleTo("YoluPainterSnippet")]
