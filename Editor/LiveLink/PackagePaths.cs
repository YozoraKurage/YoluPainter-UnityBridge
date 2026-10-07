using System.IO;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>パッケージの中のファイルの場所。パッケージがどこに入っても（Packages の下・ローカルのフォルダ・VPM の zip・
    /// Assets への複製）同じに求まるよう、パッケージの情報か、無ければこのアセンブリ定義の場所から辿る。</summary>
    internal static class PackagePaths
    {
        const string Fallback = "Packages/net.yozolab.yolupainter";
        /// <summary>このアセンブリ定義のファイルの名前と、パッケージの根からのフォルダ。</summary>
        internal const string AssemblyDefinition = "Yozolab.YoluPainter.Editor.LiveLink.asmdef";
        const string AssemblyDefinitionFolder = "Editor/LiveLink";
        static string s_root;

        /// <summary>パッケージの根のアセットのパス（ふつうは "Packages/net.yozolab.yolupainter"）。</summary>
        public static string Root => s_root ?? (s_root = Find());

        /// <summary>パッケージの根からの相対パスを、アセットのパスにする。</summary>
        public static string Asset(string relative) => Root + "/" + relative;

        /// <summary>パッケージの根からの相対パスを、ディスク上の絶対パスにする。</summary>
        public static string Physical(string relative) => Path.GetFullPath(FileUtil.GetPhysicalPath(Asset(relative)));

        /// <summary>パッケージの情報から取れるパッケージの根のアセットのパス（取れなければ null。Assets への複製のとき）。試験は、取れない配置の道をここを差し替えて通す。</summary>
        internal static System.Func<string> InfoAssetPath = () =>
        {
            var info = PackageInfo.FindForAssembly(typeof(PackagePaths).Assembly);
            return info != null && !string.IsNullOrEmpty(info.assetPath) ? info.assetPath : null;
        };

        /// <summary>覚えた根を忘れて、次に求めるときにもう一度探す（試験用）。</summary>
        internal static void Reset() => s_root = null;

        static string Find() => InfoAssetPath() ?? FromAssemblyDefinition() ?? Fallback;

        /// <summary>このアセンブリ定義の場所（根/Editor/LiveLink/）から辿ったパッケージの根のアセットのパス（見つからなければ null）。パッケージの情報が取れない配置の道。</summary>
        internal static string FromAssemblyDefinition()
        {
            foreach (var guid in AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(AssemblyDefinition) + " t:AssemblyDefinitionAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid).Replace('\\', '/');
                string suffix = "/" + AssemblyDefinitionFolder + "/" + AssemblyDefinition;
                if (path.EndsWith(suffix, System.StringComparison.Ordinal)) return path.Substring(0, path.Length - suffix.Length);
            }
            return null;
        }
    }
}
