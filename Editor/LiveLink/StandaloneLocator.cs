using System;
using System.IO;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// スタンドアロンの YoluPainter の実行ファイルの場所の決め方。Windows はインストーラーが書く
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\YoluPainter</c> の <c>InstallLocation</c>（の中の <c>yolupainter.exe</c>）を先に、
    /// 無ければ（Windows 以外はいつも）Preferences に置いたパス。どちらもファイルが実在するときだけ使い、無ければ null（呼び手が選んでもらう）。
    /// </summary>
    internal static class StandaloneLocator
    {
        public const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\YoluPainter";
        public const string ExeName = "yolupainter.exe";

        internal enum Source { None, Installer, Preferences }

        /// <summary>レジストリの InstallLocation を読む口（試験は差し替える。Windows 以外・読めないときは null）。</summary>
        public static Func<string> InstallLocation = ReadInstallLocation;

        static string ReadInstallLocation()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) return null;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKey))
                    return key?.GetValue("InstallLocation") as string;
            }
            catch (Exception) { return null; } // 権限・壊れた値。見つからなかったことにして Preferences へ
        }

        /// <summary>今の環境での場所。</summary>
        public static string Resolve(out Source source) =>
            Resolve(Application.platform == RuntimePlatform.WindowsEditor, InstallLocation, LiveLinkSettings.StandalonePath, out source);

        /// <summary>環境を引数にした決め方（試験できるように）。</summary>
        public static string Resolve(bool windows, Func<string> installLocation, string preferencePath, out Source source)
        {
            if (windows)
            {
                string dir = null;
                try { dir = installLocation?.Invoke(); } catch (Exception) { }
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    string exe = SafeCombine(Tidy(dir), ExeName);
                    if (exe != null && File.Exists(exe)) { source = Source.Installer; return exe; }
                }
            }
            // Windows の「パスのコピー」は引用符つきで貼れる。前後の空白と引用符を外した値で調べ、その値を返す
            string preferred = Tidy(preferencePath);
            if (preferred.Length > 0 && File.Exists(preferred)) { source = Source.Preferences; return preferred; }
            source = Source.None;
            return null;
        }

        /// <summary>前後の空白と引用符を外す（null は空）。</summary>
        static string Tidy(string path) => (path ?? "").Trim().Trim('"').Trim();

        static string SafeCombine(string dir, string name)
        {
            try { return Path.Combine(dir, name); } catch (ArgumentException) { return null; }
        }
    }
}
