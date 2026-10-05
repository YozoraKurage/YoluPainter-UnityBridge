using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.LiveLink;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// ブリッジのライブラリは、パッケージの中のファイルでなく、そのコピー（プロジェクトの Library の下。フォルダの名前が中身のハッシュ）から読む。
    /// Unity は一度読んだライブラリを手放さず、Windows では読み込み中のファイルを差し替え・削除できないので、パッケージの更新が失敗する／更新後に
    /// エディタの再起動が要る。コピーから読めば、パッケージの中のファイルはエディタに読まれない。
    /// </summary>
    public sealed class LiveLinkNativeLoaderTests
    {
        string scratch;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong ConnectWithFn(byte[] name, int nameLength, byte[] agent, int agentLength, byte[] version, int versionLength);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int StatusFn(ulong handle);
        const string PathKey = "Yozolab.YoluPainter.LiveLink.BridgeLoadedPath";
        static int s_counter;
        static string UniqueName() => "ylp-loader-test-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + (++s_counter);
        static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

        /// <summary>今読んでいるライブラリに 1 バイト足した、中身の違うコピー（パッケージの更新で別の中身のライブラリを読む場面）を読んで、そのパスと口を返す。
        /// 置き場は決まった場所で、読んだモジュールはエディタが終わるまで読まれたまま（Windows では消せない）なので、試験のたびに別の場所を作らない。</summary>
        static string LoadOtherCopy(string scratch, out IntPtr module)
        {
            string package = Path.Combine(scratch, "package");
            Directory.CreateDirectory(package);
            string source = Path.Combine(package, LiveLinkNativeLoader.FileName);
            File.WriteAllBytes(source, File.ReadAllBytes(LiveLinkNativeLoader.SourcePath).Concat(new byte[] { 0xFE }).ToArray());
            string copy = LiveLinkNativeLoader.ShadowCopy(source, Path.Combine(Path.GetTempPath(), "ylp-loader-test-other-copy"));
            module = LiveLinkNativeLoader.OpenLibrary(copy, out string error);
            Assert.That(module, Is.Not.EqualTo(IntPtr.Zero), error);
            LiveLinkNativeLoader.DisconnectAllIn(copy); // 前の試験が失敗して残したつながりがあれば片付ける
            return copy;
        }

        static T Function<T>(IntPtr module, string name) where T : Delegate
        {
            var address = LiveLinkNativeLoader.SymbolOf(module, name);
            Assert.That(address, Is.Not.EqualTo(IntPtr.Zero), name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        static ulong ConnectIn(IntPtr module, string name)
        {
            ulong handle = Function<ConnectWithFn>(module, "ylb_connect_with")(Utf8(name), Utf8(name).Length, Utf8("test"), 4, new byte[0], 0);
            Assert.That(handle, Is.Not.EqualTo(0UL));
            return handle;
        }

        [SetUp]
        public void Fresh()
        {
            scratch = Path.Combine(Path.GetTempPath(), "ylp-loader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
        }

        [TearDown]
        public void Clean()
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }

        [Test]
        public void TheLibraryIsReadFromACopyUnderLibraryAndNotFromThePackage()
        {
            Assert.That(LiveLinkBridge.Problem, Is.Null, "the bridge library must load on the Linux and Windows editors");
            string loaded = LiveLinkNativeLoader.LoadedPath;
            Assert.That(loaded, Is.Not.Null);
            string source = LiveLinkNativeLoader.SourcePath;
            Assert.That(Path.GetFullPath(loaded), Is.Not.EqualTo(Path.GetFullPath(source)), "読むのはコピー");
            Assert.That(Path.GetFullPath(loaded), Does.StartWith(Path.GetFullPath(LiveLinkNativeLoader.DefaultRoot)), "プロジェクトの Library の下");
            Assert.That(Path.GetFileName(loaded), Is.EqualTo(Path.GetFileName(source)), "ファイル名は同じ（フォルダが中身で決まる）");
            Assert.That(Path.GetFileName(Path.GetDirectoryName(loaded)), Is.EqualTo(LiveLinkNativeLoader.ContentId(File.ReadAllBytes(source))), "フォルダの名前は元の中身のハッシュ");
            Assert.That(File.ReadAllBytes(loaded), Is.EqualTo(File.ReadAllBytes(source)), "コピーは元と同じ中身");
        }

        [Test]
        public void ThePackageFileCanBeOpenedForWritingWhileTheLibraryIsLoaded()
        {
            Assert.That(LiveLinkBridge.Problem, Is.Null);
            // Windows で読み込み中のファイルは、書き込みでも削除でも開けない。コピーから読むので、パッケージの中のファイルは開ける（中身は変えない）
            using (var stream = new FileStream(LiveLinkNativeLoader.SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.That(stream.Length, Is.GreaterThan(0));
        }

        [Test]
        public void ACopyIsReusedForTheSameContentAndANewFolderIsMadeForNewContent()
        {
            string source = Path.Combine(scratch, "package", "libyolu_bridge.so");
            Directory.CreateDirectory(Path.GetDirectoryName(source));
            string root = Path.Combine(scratch, "Library", "YoluPainter", "LiveLink");
            File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4, 5 });
            string first = LiveLinkNativeLoader.ShadowCopy(source, root);
            Assert.That(File.ReadAllBytes(first), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
            Assert.That(Path.GetFileName(first), Is.EqualTo("libyolu_bridge.so"));
            var written = File.GetLastWriteTimeUtc(first);
            // 同じ中身: 作り直さない（同じパス・書き換えない）
            System.Threading.Thread.Sleep(20);
            Assert.That(LiveLinkNativeLoader.ShadowCopy(source, root), Is.EqualTo(first));
            Assert.That(File.GetLastWriteTimeUtc(first), Is.EqualTo(written));
            // 中身が変わった（パッケージの更新）: 別のフォルダに新しいコピー。前のコピーはそのまま
            File.WriteAllBytes(source, new byte[] { 9, 8, 7 });
            string second = LiveLinkNativeLoader.ShadowCopy(source, root);
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(File.ReadAllBytes(second), Is.EqualTo(new byte[] { 9, 8, 7 }));
            Assert.That(File.Exists(first), Is.True, "前のコピーは、エディタが読んでいる間は残る");
            // 書きかけの一時のファイルは残らない
            Assert.That(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories), Is.Empty);
            // 今のコピーのほかを消す（消せるものを）
            Assert.That(LiveLinkNativeLoader.DeleteOtherCopies(root, Path.GetFileName(Path.GetDirectoryName(second))), Is.EqualTo(1));
            Assert.That(File.Exists(first), Is.False);
            Assert.That(File.Exists(second), Is.True);
            Assert.That(LiveLinkNativeLoader.DeleteOtherCopies(Path.Combine(scratch, "no-such-root"), "x"), Is.EqualTo(0));
        }

        [Test]
        public void ACopyThatAnotherEditorMadeFirstIsUsedAsItIsAndABrokenOneIsReplaced()
        {
            string source = Path.Combine(scratch, "yolu_bridge.dll");
            string root = Path.Combine(scratch, "root");
            var content = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
            File.WriteAllBytes(source, content);
            // 別のエディタが先に同じ中身を置いた
            string dir = Path.Combine(root, LiveLinkNativeLoader.ContentId(content));
            Directory.CreateDirectory(dir);
            string existing = Path.Combine(dir, "yolu_bridge.dll");
            File.WriteAllBytes(existing, content);
            var written = File.GetLastWriteTimeUtc(existing);
            System.Threading.Thread.Sleep(20);
            Assert.That(LiveLinkNativeLoader.ShadowCopy(source, root), Is.EqualTo(existing));
            Assert.That(File.GetLastWriteTimeUtc(existing), Is.EqualTo(written));
            // 長さが違う（途中で切れた）コピーは使わず、置き直す
            File.WriteAllBytes(existing, content.Take(10).ToArray());
            LiveLinkNativeLoader.ShadowCopy(source, root);
            Assert.That(File.ReadAllBytes(existing), Is.EqualTo(content));
        }

        [Test]
        public void TheContentIdIsTheFirstSixteenHexDigitsOfTheSha256()
        {
            // SHA-256("abc") = ba7816bf 8f01cfea 414140de 5dae2223 b00361a3 96177a9c b410ff61 f20015ad
            Assert.That(LiveLinkNativeLoader.ContentId(new[] { (byte)'a', (byte)'b', (byte)'c' }), Is.EqualTo("ba7816bf8f01cfea"));
            using (var sha = SHA256.Create())
                Assert.That(sha.ComputeHash(new byte[] { 1 })[0], Is.EqualTo(Convert.ToByte(LiveLinkNativeLoader.ContentId(new byte[] { 1 }).Substring(0, 2), 16)));
        }

        [Test]
        public void AnotherCopyIsAnotherModuleAndOnlyALoadedOneIsAsked()
        {
            Assert.That(LiveLinkBridge.Problem, Is.Null);
            string copy = LoadOtherCopy(scratch, out IntPtr other);
            Assert.That(copy, Is.Not.EqualTo(LiveLinkNativeLoader.LoadedPath));
            var current = LiveLinkNativeLoader.LoadedModule(LiveLinkNativeLoader.LoadedPath);
            try
            {
                Assert.That(current, Is.Not.EqualTo(IntPtr.Zero), "今のコピーは読み込まれている");
                Assert.That(current, Is.Not.EqualTo(other), "ファイル名が同じでも、中身の違うコピーは別のモジュール");
            }
            finally { LiveLinkNativeLoader.Release(current); }
            // 読み込まれていないものは読まない（-1。あとからも読み込まれていない）
            string neverLoaded = Path.Combine(scratch, "never", LiveLinkNativeLoader.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(neverLoaded));
            File.WriteAllBytes(neverLoaded, File.ReadAllBytes(LiveLinkNativeLoader.SourcePath).Concat(new byte[] { 0xFD, 0xFD }).ToArray());
            Assert.That(LiveLinkNativeLoader.DisconnectAllIn(neverLoaded), Is.EqualTo(-1));
            Assert.That(LiveLinkNativeLoader.LoadedModule(neverLoaded), Is.EqualTo(IntPtr.Zero), "尋ねただけで読み込まない");
            Assert.That(LiveLinkNativeLoader.DisconnectAllIn(Path.Combine(scratch, "no-such-file")), Is.EqualTo(-1));
        }

        [Test]
        public void TheLinksOfTheEarlierCopyAreCutWhenAnotherCopyIsLoadedAndTheCurrentOnesStay()
        {
            Assert.That(LiveLinkBridge.Problem, Is.Null);
            string copy = LoadOtherCopy(scratch, out IntPtr other);
            var status = Function<StatusFn>(other, "ylb_status");
            string saved = SessionState.GetString(PathKey, "");
            ulong mine = 0;
            try
            {
                ulong earlier = ConnectIn(other, UniqueName());
                ulong earlierToo = ConnectIn(other, UniqueName());
                mine = LiveLinkBridge.Connect(UniqueName(), "test");
                Assert.That(mine, Is.Not.EqualTo(0UL));
                Assert.That(status(earlier), Is.GreaterThanOrEqualTo(0));
                // 前のドメインが読んだのはこのコピー（SessionState にパスが残っている）。パッケージの中のファイルは読み込まれていない
                SessionState.SetString(PathKey, copy);
                Assert.That(LiveLinkNativeLoader.DisconnectStale(LiveLinkNativeLoader.LoadedPath, Path.Combine(scratch, "package-not-loaded.dll")), Is.EqualTo(2));
                Assert.That(status(earlier), Is.LessThan(0), "前のコピーのつながりは切れた");
                Assert.That(status(earlierToo), Is.LessThan(0));
                Assert.That(LiveLinkBridge.Status(mine), Is.Not.EqualTo(LiveLinkStatus.Unknown), "今のコピーのつながりは切らない");
                // 前のコピーが今のコピーと同じなら、何もしない（今の C# が切る）
                SessionState.SetString(PathKey, LiveLinkNativeLoader.LoadedPath);
                Assert.That(LiveLinkNativeLoader.DisconnectStale(LiveLinkNativeLoader.LoadedPath, null), Is.EqualTo(0));
                Assert.That(LiveLinkBridge.Status(mine), Is.Not.EqualTo(LiveLinkStatus.Unknown));
                // 何も残っていなければ（初めての読み込み）何もしない
                SessionState.EraseString(PathKey);
                Assert.That(LiveLinkNativeLoader.DisconnectStale(null, null), Is.EqualTo(0));
            }
            finally
            {
                LiveLinkBridge.Disconnect(mine);
                if (saved.Length > 0) SessionState.SetString(PathKey, saved); else SessionState.EraseString(PathKey);
            }
        }

        [Test]
        public void ThePackageFileThatAnEarlierVersionLoadedIsAskedToo()
        {
            Assert.That(LiveLinkBridge.Problem, Is.Null);
            // コピーから読む前の版は、パッケージの中のファイルを読んだ。SessionState にパスは無く、そのファイルのパスだけが手がかり
            string copy = LoadOtherCopy(scratch, out IntPtr other);
            var status = Function<StatusFn>(other, "ylb_status");
            string saved = SessionState.GetString(PathKey, "");
            try
            {
                SessionState.EraseString(PathKey);
                ulong earlier = ConnectIn(other, UniqueName());
                Assert.That(LiveLinkNativeLoader.DisconnectStale(LiveLinkNativeLoader.LoadedPath, copy), Is.EqualTo(1));
                Assert.That(status(earlier), Is.LessThan(0));
            }
            finally { if (saved.Length > 0) SessionState.SetString(PathKey, saved); }
        }

        [Test]
        public void AConnectedLinkOfTheEarlierCopyIsClosedSoTheStandaloneIsFreeForTheNewOne()
        {
            Assert.That(LiveLinkBridge.Problem, Is.Null);
            string copy = LoadOtherCopy(scratch, out IntPtr other);
            var status = Function<StatusFn>(other, "ylb_status");
            string name = UniqueName();
            ulong server = LiveLinkTestServer.Start(name, 64, 32);
            Assert.That(server, Is.Not.EqualTo(0UL));
            try
            {
                ulong earlier = ConnectIn(other, name);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (status(earlier) != (int)LiveLinkStatus.Connected)
                {
                    Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(10), "the earlier copy connects to the server");
                    System.Threading.Thread.Sleep(5);
                }
                Assert.That(LiveLinkNativeLoader.DisconnectAllIn(copy), Is.EqualTo(1));
                Assert.That(status(earlier), Is.LessThan(0));
                // 切れたあとに今のコピーからつなげる
                ulong mine = LiveLinkBridge.Connect(name, "test");
                try
                {
                    clock.Restart();
                    while (LiveLinkBridge.Status(mine) != LiveLinkStatus.Connected)
                    {
                        Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(10), "the current copy connects: " + LiveLinkBridge.StatusText(mine));
                        System.Threading.Thread.Sleep(5);
                    }
                }
                finally { LiveLinkBridge.Disconnect(mine); }
            }
            finally { LiveLinkTestServer.Stop(server); }
        }

        [Test]
        public void OnlyTheWindowsAndLinuxEditorsHaveALibrary()
        {
            Assert.That(LiveLinkNativeLoader.FileNameFor(RuntimePlatform.WindowsEditor), Is.EqualTo("yolu_bridge.dll"));
            Assert.That(LiveLinkNativeLoader.FileNameFor(RuntimePlatform.LinuxEditor), Is.EqualTo("libyolu_bridge.so"));
            foreach (var platform in new[] { RuntimePlatform.OSXEditor, RuntimePlatform.WindowsPlayer, RuntimePlatform.Android })
                Assert.That(LiveLinkNativeLoader.FileNameFor(platform), Is.Null, platform.ToString());
        }
    }
}
