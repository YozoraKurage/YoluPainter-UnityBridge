using System;
using UnityEngine;
using UnityEngine.TestTools;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>オフスクリーンにはネイティブの GUIView が無い。数値ラベルのカーソル登録だけは Unity がエラーを出すので、
    /// その完全一致のログだけを描画中の既知の診断として期待する。ShaderGUI の例外など、ほかのログは通常どおり試験を落とす。</summary>
    internal sealed class OffscreenInspectorLogs : IDisposable
    {
        public OffscreenInspectorLogs() => Application.logMessageReceived += ExpectCursor;
        static void ExpectCursor(string message, string stack, LogType type)
        {
            if (type == LogType.Error && message == "EditorGUIUtility.AddCursorRect called outside an editor OnGUI") LogAssert.Expect(type, message);
        }
        public void Dispose() => Application.logMessageReceived -= ExpectCursor;
    }
}
