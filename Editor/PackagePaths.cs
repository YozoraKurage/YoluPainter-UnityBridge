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
        static string s_root, s_version;

        /// <summary>パッケージの根のアセットのパス（ふつうは "Packages/net.yozolab.yolupainter"）。</summary>
        public static string Root => s_root ?? (s_root = Find());

        /// <summary>パッケージの版（package.json の version。分からなければ "unknown"）。</summary>
        public static string Version => s_version ?? (s_version = PackageInfo.FindForAssembly(typeof(PackagePaths).Assembly)?.version is string v && v.Length > 0 ? v : "unknown");

        /// <summary>パッケージの根からの相対パスを、アセットのパスにする。</summary>
        public static string Asset(string relative) => Root + "/" + relative;

        /// <summary>パッケージの根からの相対パスを、ディスク上の絶対パスにする。</summary>
        public static string Physical(string relative) => Path.GetFullPath(FileUtil.GetPhysicalPath(Asset(relative)));

        /// <summary>パッケージの根の、ディスク上の絶対パス（末尾の区切りなし）。<c>BrushSets~</c> のように、Unity がインポートしないフォルダの場所を求めるときの根。</summary>
        public static string PhysicalRoot => Path.GetFullPath(FileUtil.GetPhysicalPath(Root)).TrimEnd('/', '\\');

        /// <summary>パッケージの情報から取れるパッケージの根のアセットのパス（取れなければ null。Assets への複製のとき）。試験は、取れない配置の道をここを差し替えて通す。</summary>
        internal static System.Func<string> InfoAssetPath = () =>
        {
            var info = PackageInfo.FindForAssembly(typeof(PackagePaths).Assembly);
            return info != null && !string.IsNullOrEmpty(info.assetPath) ? info.assetPath : null;
        };

        /// <summary>覚えた根を忘れて、次に求めるときにもう一度探す（試験用）。</summary>
        internal static void Reset() => s_root = null;

        static string Find() => InfoAssetPath() ?? FromAssemblyDefinition() ?? Fallback;

        /// <summary>Editor のアセンブリ定義の場所から辿ったパッケージの根のアセットのパス（見つからなければ null）。パッケージの情報が取れない配置の道。</summary>
        internal static string FromAssemblyDefinition()
        {
            foreach (var guid in AssetDatabase.FindAssets("Yozolab.YoluPainter.Editor t:AssemblyDefinitionAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) == "Yozolab.YoluPainter.Editor.asmdef") return Path.GetDirectoryName(Path.GetDirectoryName(path)).Replace('\\', '/');
            }
            return null;
        }
    }
}
