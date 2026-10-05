using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// ブリッジのネイティブのライブラリ（Plugins/LiveLink の <c>yolu_bridge.dll</c>・<c>libyolu_bridge.so</c>）を、パッケージの中のファイルではなく
    /// <b>コピーから</b>読む。Unity は <c>[DllImport]</c> で一度読んだライブラリをエディタが終わるまで手放さず、Windows では読み込み中のファイルを
    /// 差し替えも消しもできない（VPM でパッケージを更新できない・更新後の Unity の再起動が要る）。そこで宣言を関数ポインターにし
    /// （<c>tools/gen-livelink-native.py</c>。<see cref="LiveLinkNative.Bind"/>）、ライブラリはこのクラスが読む:
    /// <list type="bullet">
    /// <item>コピーの置き場は、プロジェクトの <c>Library/YoluPainter/LiveLink/&lt;中身のハッシュ&gt;/</c>。ファイル名は同じで、フォルダの名前が中身で決まる。
    /// 中身が同じなら作り直さない。別のエディタが同時に作っても、一時のファイルへ書いてから置き換える。</item>
    /// <item>パッケージを更新すると中身のハッシュが替わり、新しいコピーを読む（前のコピーは、エディタが終わるまで読まれたまま。使わない。呼び出しは新しい
    /// コピーの関数だけに届く）。ほかの中身のコピーは、消せるものを消す（読み込み中のものは残る）。</item>
    /// <item>前のコピーが読み込まれたまま、つながりを持っていることがある（スタンドアロンがつなげる Unity は 1 つだけなので、残ると新しいコピーからのつなぎが断られる）。
    /// 読んだコピーのパスは SessionState に残し、新しいコピーを読むとき、前のコピーとパッケージの中のファイル（コピーから読む前の版が読んだもの）が
    /// <b>すでに読み込まれていれば</b>、その <c>ylb_disconnect_all</c> を呼んでつながりを切る（読み込まれていないものは読まない）。</item>
    /// <item>パッケージの中のファイルは、Unity も（<c>[DllImport]</c> をもう使わないので）読まない。更新・削除のとき、エディタに読まれていない。</item>
    /// </list>
    /// ライブラリは手放さない（裏のスレッドが残っているライブラリを外すのは危ない）。Windows と Linux のエディタだけ。
    /// </summary>
    internal static unsafe class LiveLinkNativeLoader
    {
        /// <summary>このエディタの OS のライブラリのファイル名（無ければ null）。</summary>
        public static string FileName => FileNameFor(Application.platform);

        public static string FileNameFor(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.WindowsEditor: return "yolu_bridge.dll";
                case RuntimePlatform.LinuxEditor: return "libyolu_bridge.so";
                default: return null;
            }
        }

        /// <summary>パッケージの中のライブラリ（コピー元）。</summary>
        public static string SourcePath => PackagePaths.Physical("Plugins/LiveLink/" + FileName);

        /// <summary>コピーの置き場の根（プロジェクトの Library の下。エディタを終えても残るが、Library を消せば作り直す）。</summary>
        public static string DefaultRoot => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "YoluPainter", "LiveLink");

        /// <summary>今読んでいるライブラリのファイル（コピー）。読んでいなければ null。</summary>
        public static string LoadedPath { get; private set; }

        /// <summary>読んだコピーのパスを残す SessionState のキー（ドメインの読み直しをまたぐ。エディタを終えれば消える）。</summary>
        const string LoadedPathKey = "Yozolab.YoluPainter.LiveLink.BridgeLoadedPath";

        static IntPtr library;

        /// <summary>ライブラリを読んで関数を結ぶ（1 度だけ）。読めなければ理由、読めれば null。</summary>
        public static string Load()
        {
            if (library != IntPtr.Zero) return null;
            string name = FileName;
            if (name == null) return L.Tr("The Live Link library is available in the Windows and Linux editors only.");
            string source = SourcePath;
            if (!File.Exists(source)) return L.Tr("The Live Link library file is missing: {0}", source);
            string path = null, problem = null;
            try { path = ShadowCopy(source, DefaultRoot); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            { problem = L.Tr("The Live Link library could not be copied for loading: {0}", e.Message); }
            // 前のドメインのコピーが持つつながりは、新しいコピーが読めたかどうかに関わらず切る（C# の側はもう無い）
            DisconnectStale(path, source);
            if (problem != null) return problem;
            var handle = Open(path, out string error);
            if (handle == IntPtr.Zero) return L.Tr("The Live Link library (yolu_bridge) could not be loaded: {0}", error);
            LiveLinkNative.Bind(symbol => Symbol(handle, symbol));
            library = handle; LoadedPath = path;
            SessionState.SetString(LoadedPathKey, path);
            DeleteOtherCopies(DefaultRoot, Path.GetFileName(Path.GetDirectoryName(path)));
            return null;
        }

        /// <summary>
        /// 今のコピー（<paramref name="current"/>。まだ決まらなければ null）とは別のライブラリで、すでに読み込まれているもののつながりを全部切る。
        /// 対象は、前のドメインが読んだコピー（SessionState のパス）と、コピーから読む前の版が読んだパッケージの中のファイル（<paramref name="packageFile"/>）。
        /// 読み込まれていないものは読まない。切ったつながりの数を返す。
        /// </summary>
        public static int DisconnectStale(string current, string packageFile)
        {
            int cut = 0;
            foreach (var candidate in new[] { SessionState.GetString(LoadedPathKey, ""), packageFile })
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (current != null && SamePath(candidate, current)) continue; // 同じモジュール。今の C# が切る
                cut += Math.Max(0, DisconnectAllIn(candidate));
            }
            return cut;
        }

        static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), Application.platform == RuntimePlatform.WindowsEditor ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        /// <summary>
        /// <paramref name="path"/> のライブラリが<b>すでに読み込まれていれば</b>、その <c>ylb_disconnect_all</c> を呼んで、切った数を返す。
        /// 読み込まれていない・その関数が無いときは -1（読み込みはしない）。
        /// </summary>
        public static int DisconnectAllIn(string path)
        {
            var module = LoadedModule(path);
            if (module == IntPtr.Zero) return -1;
            try
            {
                var function = Symbol(module, "ylb_disconnect_all");
                return function == IntPtr.Zero ? -1 : ((delegate* unmanaged[Cdecl]<int>)function)();
            }
            finally { Release(module); }
        }

        /// <summary><paramref name="path"/> のライブラリがすでに読み込まれていれば、その口（読み込まれていなければ <see cref="IntPtr.Zero"/>。読み込みはしない）。
        /// Linux の口は数えられているので、使い終わったら <see cref="Release"/>。</summary>
        public static IntPtr LoadedModule(string path)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor) return GetModuleHandleW(path);
            return dlopen(path, 2 | 4); // RTLD_NOW | RTLD_NOLOAD
        }

        /// <summary><see cref="LoadedModule"/> で得た口を返す（ライブラリは読み込まれたまま）。</summary>
        public static void Release(IntPtr module)
        {
            if (module != IntPtr.Zero && Application.platform != RuntimePlatform.WindowsEditor) dlclose(module);
        }

        /// <summary>ライブラリを読む（関数は結ばない）。読めなければ <see cref="IntPtr.Zero"/> と理由。</summary>
        public static IntPtr OpenLibrary(string path, out string error) => Open(path, out error);

        /// <summary>読んだライブラリの関数の番地（無ければ <see cref="IntPtr.Zero"/>）。</summary>
        public static IntPtr SymbolOf(IntPtr module, string name) => Symbol(module, name);

        /// <summary>ライブラリのコピーを <paramref name="root"/> の下に作って、そのパスを返す（中身が同じものが既にあれば、それを返す）。</summary>
        public static string ShadowCopy(string source, string root)
        {
            var bytes = File.ReadAllBytes(source);
            string id = ContentId(bytes);
            string dir = Path.Combine(root, id);
            string target = Path.Combine(dir, Path.GetFileName(source));
            if (File.Exists(target) && new FileInfo(target).Length == bytes.Length) return target;
            Directory.CreateDirectory(dir);
            // 一時のファイルへ書いてから置き換える（書きかけのファイルを、別のエディタが読まない）
            string temp = target + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
            File.WriteAllBytes(temp, bytes);
            // 長さの合わない（途中で切れた）コピーが残っていれば、先に外す。外せなければ（読み込み中など）、書き込みの失敗として呼び手へ
            if (File.Exists(target) && new FileInfo(target).Length != bytes.Length)
            {
                try { File.Delete(target); }
                catch (Exception) { File.Delete(temp); throw; }
            }
            try { File.Move(temp, target); }
            catch (IOException) when (File.Exists(target) && new FileInfo(target).Length == bytes.Length) { File.Delete(temp); } // 別のエディタが先に置いた
            return target;
        }

        /// <summary>中身の印（SHA-256 の先頭 16 桁の 16 進）。コピーのフォルダの名前になる。</summary>
        public static string ContentId(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var chars = new char[16];
                for (int i = 0; i < 8; i++) { chars[i * 2] = "0123456789abcdef"[hash[i] >> 4]; chars[i * 2 + 1] = "0123456789abcdef"[hash[i] & 15]; }
                return new string(chars);
            }
        }

        /// <summary>今のコピー（<paramref name="keep"/>）のほかのフォルダを、消せるだけ消す（読み込み中・別のエディタが使っているものは残る）。消した数を返す。</summary>
        public static int DeleteOtherCopies(string root, string keep)
        {
            int deleted = 0;
            if (!Directory.Exists(root)) return 0;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(dir) == keep) continue;
                try { Directory.Delete(dir, true); deleted++; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { /* 読み込み中。残す */ }
            }
            return deleted;
        }

        static IntPtr Open(string path, out string error)
        {
            error = null;
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                var handle = LoadLibraryW(path);
                if (handle == IntPtr.Zero) error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return handle;
            }
            // RTLD_NOW | RTLD_LOCAL
            var h = dlopen(path, 2);
            if (h == IntPtr.Zero) error = Marshal.PtrToStringAnsi(dlerror());
            return h;
        }

        static IntPtr Symbol(IntPtr handle, string name) => Application.platform == RuntimePlatform.WindowsEditor ? GetProcAddress(handle, name) : dlsym(handle, name);

        [DllImport("kernel32", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern IntPtr GetModuleHandleW(string fileName);

        [DllImport("kernel32", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi, ExactSpelling = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("libdl.so.2", EntryPoint = "dlopen")]
        static extern IntPtr dlopen(string path, int flags);

        [DllImport("libdl.so.2", EntryPoint = "dlsym")]
        static extern IntPtr dlsym(IntPtr handle, string name);

        [DllImport("libdl.so.2", EntryPoint = "dlclose")]
        static extern int dlclose(IntPtr handle);

        [DllImport("libdl.so.2", EntryPoint = "dlerror")]
        static extern IntPtr dlerror();
    }
}
