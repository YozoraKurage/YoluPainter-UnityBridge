using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// Live Link の利用者ごとの設定（EditorPrefs。プロジェクトには入れない）: スタンドアロンの実行ファイルのパス（Windows はインストーラーが書く場所を
    /// 先に見るので、無くてもよい）と、書き出しの置き場の元（既定は <c>Assets/YoluPainter</c>。相手の名前のフォルダをこの下に置く。プロジェクトの根からの道か絶対の道）。
    /// </summary>
    internal static class LiveLinkSettings
    {
        public const string StandalonePathKey = "Yozolab.YoluPainter.LiveLink.StandalonePath";
        public const string ExportFolderKey = "Yozolab.YoluPainter.LiveLink.ExportFolder";
        public const string DefaultExportFolder = "Assets/YoluPainter";

        /// <summary>Preferences に置いたスタンドアロンの実行ファイルのパス（無ければ空）。</summary>
        public static string StandalonePath
        {
            get => EditorPrefs.GetString(StandalonePathKey, "");
            set { if (string.IsNullOrEmpty(value)) EditorPrefs.DeleteKey(StandalonePathKey); else EditorPrefs.SetString(StandalonePathKey, value); }
        }

        /// <summary>書き出しの置き場の元（空なら既定）。</summary>
        public static string ExportFolder
        {
            get { var v = EditorPrefs.GetString(ExportFolderKey, DefaultExportFolder); return string.IsNullOrWhiteSpace(v) ? DefaultExportFolder : v.Trim(); }
            set { if (string.IsNullOrWhiteSpace(value) || value.Trim() == DefaultExportFolder) EditorPrefs.DeleteKey(ExportFolderKey); else EditorPrefs.SetString(ExportFolderKey, value.Trim()); }
        }

        /// <summary>プロジェクトの根（Assets の親。区切りは /）。</summary>
        public static string ProjectRoot => Slash(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)));

        /// <summary>相手の書き出しの置き場（絶対の道、区切りは /）: 置き場の元の下の、相手の名前のフォルダ。</summary>
        public static string ExportDirFor(string targetName) => ExportDirFor(ExportFolder, ProjectRoot, targetName);

        public static string ExportDirFor(string folder, string projectRoot, string targetName)
        {
            string baseDir = string.IsNullOrWhiteSpace(folder) ? DefaultExportFolder : folder.Trim().Trim('"');
            if (!Path.IsPathRooted(baseDir)) baseDir = Path.Combine(projectRoot, baseDir);
            return Slash(Path.GetFullPath(Path.Combine(baseDir, SafeName(targetName))));
        }

        /// <summary>フォルダの名前にできる形（使えない文字は _。空・. だけなら Target）。</summary>
        public static string SafeName(string name)
        {
            var bad = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToArray();
            var chars = (name ?? "").Trim().Select(c => bad.Contains(c) || c < 0x20 ? '_' : c).ToArray();
            string s = new string(chars).Trim().TrimEnd('.');
            return s.Length == 0 ? "Target" : s;
        }

        public static string Slash(string path) => path?.Replace('\\', '/');
    }
}
