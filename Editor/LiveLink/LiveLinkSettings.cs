using UnityEditor;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// Live Link の利用者ごとの設定（EditorPrefs。プロジェクトには入れない）: つなぎ先の名前（既定のままの人が多い）と、
    /// スタンドアロンの実行ファイルのパス（Windows はインストーラーが書く場所を先に見るので、無くてもよい。Linux は Preferences のこのパスだけ）。
    /// </summary>
    internal static class LiveLinkSettings
    {
        public const string NameKey = "Yozolab.YoluPainter.LiveLink.Name";
        public const string StandalonePathKey = "Yozolab.YoluPainter.LiveLink.StandalonePath";

        /// <summary>つなぎ先の名前（空なら既定）。</summary>
        public static string LinkName
        {
            get { var name = EditorPrefs.GetString(NameKey, LiveLinkSession.DefaultLinkName); return string.IsNullOrWhiteSpace(name) ? LiveLinkSession.DefaultLinkName : name; }
            set => EditorPrefs.SetString(NameKey, value ?? "");
        }

        /// <summary>Preferences に置いたスタンドアロンの実行ファイルのパス（無ければ空）。</summary>
        public static string StandalonePath
        {
            get => EditorPrefs.GetString(StandalonePathKey, "");
            set { if (string.IsNullOrEmpty(value)) EditorPrefs.DeleteKey(StandalonePathKey); else EditorPrefs.SetString(StandalonePathKey, value); }
        }
    }
}
