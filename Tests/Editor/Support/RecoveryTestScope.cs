using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ウィンドウを作る全試験の checkpoint を一時領域へ隔離し、試験後にまとめて片付ける。</summary>
    [SetUpFixture]
    public sealed class RecoveryTestScope
    {
        string root;
        [OneTimeSetUp] public void IsolateRecovery()
        {
            root = Path.Combine(Path.GetTempPath(), "yolupainter-recovery-tests-" + Guid.NewGuid().ToString("N"));
            RecoveryCatalog.RootOverride = root;
        }
        [OneTimeTearDown] public void DeleteTestRecovery()
        {
            try
            {
                foreach (var window in Resources.FindObjectsOfTypeAll<TexturePaintWindow>())
                    if (window.RecoveryRoot != null && window.RecoveryRoot.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        UnityEngine.Object.DestroyImmediate(window);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            finally { RecoveryCatalog.RootOverride = null; }
        }
    }
}
