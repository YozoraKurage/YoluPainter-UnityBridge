using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// GPU の重い仕事（メッシュマップのベイクの 1 回の Dispatch と読み戻し）を、同じ機械の Unity のあいだで 1 つずつにする順番待ち。
    /// 環境変数 <c>YOLUPAINTER_GPU_HEAVY_LOCK</c> にロックのファイルのパスがあり、Linux のエディタのときだけ効く（開発環境のテストの台に
    /// .devcontainer の common.sh が設定する。使う人の Unity では何もしない）。ロックは Linux の flock で、持ったままプロセスが落ちても
    /// OS が外す。入れ子にしてよい（外側だけが取る）。
    /// </summary>
    internal static class GpuHeavyWorkGate
    {
        public const string EnvironmentVariable = "YOLUPAINTER_GPU_HEAVY_LOCK";
        const int LockExclusive = 2, LockRelease = 8;
        [DllImport("libc", SetLastError = true)] static extern int flock(int fd, int operation);
        static FileStream held; static int depth;

        /// <summary>順番を取る（取れるまで待つ）。効かないときは null（using に渡してよい）。</summary>
        public static IDisposable Enter()
        {
            string path = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrEmpty(path) || Application.platform != RuntimePlatform.LinuxEditor) return null;
            if (depth++ > 0) return new Exit();
            try
            {
                held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                int fd = (int)held.SafeFileHandle.DangerousGetHandle();
                while (flock(fd, LockExclusive) != 0 && Marshal.GetLastWin32Error() == 4) { } // EINTR はやり直す
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is DllNotFoundException || exception is EntryPointNotFoundException)
            {
                // 順番待ちが使えなくても仕事は止めない（開発環境の補助なので）
                held?.Dispose(); held = null;
            }
            return new Exit();
        }

        public static bool Held => held != null;

        sealed class Exit : IDisposable
        {
            bool done;
            public void Dispose()
            {
                if (done) return; done = true;
                if (--depth > 0 || held == null) return;
                try { flock((int)held.SafeFileHandle.DangerousGetHandle(), LockRelease); } catch (Exception) { }
                held.Dispose(); held = null;
            }
        }
    }
}
