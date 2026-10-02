using UnityEditor;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>描画ウィンドウが使うモーダルなダイアログ。テストはこれを差し替えて、エディタを止めずに
    /// 保存・読み込み・確認の分岐を通す（モーダルダイアログは自動テスト中に出すと主スレッドごと止まる）。</summary>
    internal interface IPainterDialogs
    {
        /// <returns>選ばれたフォルダ。取り消されたら空文字列。</returns>
        string SaveFolder(string title, string folder, string defaultName);
        string OpenFolder(string title, string folder);
        string OpenFile(string title, string folder, string extension);
        string SaveFile(string title, string folder, string defaultName, string extension);
        bool Confirm(string title, string message, string ok, string cancel);
        void Inform(string title, string message);
        void Progress(string title, string info, float progress);
        void ClearProgress();
    }

    internal sealed class EditorPainterDialogs : IPainterDialogs
    {
        public static readonly EditorPainterDialogs Instance = new EditorPainterDialogs();
        public string SaveFolder(string title, string folder, string defaultName) => EditorUtility.SaveFolderPanel(title, folder, defaultName);
        public string OpenFolder(string title, string folder) => EditorUtility.OpenFolderPanel(title, folder, "");
        public string OpenFile(string title, string folder, string extension) => EditorUtility.OpenFilePanel(title, folder, extension);
        public string SaveFile(string title, string folder, string defaultName, string extension) => EditorUtility.SaveFilePanel(title, folder, defaultName, extension);
        public bool Confirm(string title, string message, string ok, string cancel) => EditorUtility.DisplayDialog(title, message, ok, cancel);
        public void Inform(string title, string message) => EditorUtility.DisplayDialog(title, message, "OK");
        public void Progress(string title, string info, float progress) => EditorUtility.DisplayProgressBar(title, info, progress);
        public void ClearProgress() => EditorUtility.ClearProgressBar();
    }
}
