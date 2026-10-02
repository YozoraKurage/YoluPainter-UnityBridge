using System.IO;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>パッケージの中のファイルの場所。パッケージがどこに入っても（Packages の下・ローカルのフォルダ・VPM の .unitypackage・
    /// Assets への複製）同じに求まるよう、パッケージの情報か、無ければ Editor のアセンブリ定義の場所から辿る。</summary>
    internal static class PackagePaths
    {
        const string Fallback = "Packages/net.yozolab.yolupainter";
        static string s_root;

        /// <summary>パッケージの根のアセットのパス（ふつうは "Packages/net.yozolab.yolupainter"）。</summary>
        public static string Root => s_root ?? (s_root = Find());

        /// <summary>パッケージの根からの相対パスを、アセットのパスにする。</summary>
        public static string Asset(string relative) => Root + "/" + relative;

        /// <summary>パッケージの根からの相対パスを、ディスク上の絶対パスにする。</summary>
        public static string Physical(string relative) => Path.GetFullPath(FileUtil.GetPhysicalPath(Asset(relative)));

        static string Find()
        {
            var info = PackageInfo.FindForAssembly(typeof(PackagePaths).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.assetPath)) return info.assetPath;
            foreach (var guid in AssetDatabase.FindAssets("Yozolab.YoluPainter.Editor t:AssemblyDefinitionAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) == "Yozolab.YoluPainter.Editor.asmdef") return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace('\\', '/');
            }
            return Fallback;
        }
    }
}
