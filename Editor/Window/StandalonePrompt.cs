using System;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    /// <summary>
    /// スタンドアロン版 YoluPainter（YoluPainter-rs）への移行のおすすめ。Unity 版は機能を増やさずブリッジ（Live Link）に専念するので、描く窓
    /// （<see cref="TexturePaintWindow"/>）を開いたとき、エディターの起動ごとに 1 回だけ、小さな窓でスタンドアロン版を知らせる。
    /// 選べるのは「ダウンロードのページを開く」「あとで」「今後表示しない」。Preferences ▸ YoluPainter で切れる（既定は入）。
    /// 描く窓の機能は変えない（窓を出すだけで、文書にもプロジェクトにも何も書かない）。
    /// </summary>
    internal static class StandalonePrompt
    {
        /// <summary>スタンドアロン版の配布ページ。</summary>
        public const string DownloadUrl = "https://github.com/YozoraKurage/YoluPainter-rs/releases/latest";
        const string KeyPrefix = "Yozolab.YoluPainter.SuggestStandalone";
        public const string EnabledKey = KeyPrefix + ".Enabled";
        public const string ShownKey = KeyPrefix + ".Shown";

        internal enum Choice { Download, Later, Never }

        /// <summary>設定と、このセッションで出したかの覚え（試験は差し替える）。</summary>
        internal interface IStore
        {
            bool Enabled { get; set; }
            bool ShownThisSession { get; set; }
        }

        /// <summary>入切はこの PC の自分だけの設定（EditorPrefs。既定は入）、出したかはエディタのセッションごと（SessionState。ドメインの読み直しでは残り、再起動で消える）。</summary>
        sealed class EditorStore : IStore
        {
            readonly string enabledKey, shownKey;
            public EditorStore(string prefix) { enabledKey = prefix + ".Enabled"; shownKey = prefix + ".Shown"; }
            public bool Enabled { get => EditorPrefs.GetBool(enabledKey, true); set => EditorPrefs.SetBool(enabledKey, value); }
            public bool ShownThisSession { get => SessionState.GetBool(shownKey, false); set => SessionState.SetBool(shownKey, value); }
        }

        /// <summary>本物の設定の入口（キーの頭を選べる。試験は別のキーで既定と往復を確かめる）。</summary>
        internal static IStore CreateEditorStore(string keyPrefix) => new EditorStore(keyPrefix);

        internal static IStore Store = new EditorStore(KeyPrefix);
        /// <summary>窓を出す（試験は差し替える）。</summary>
        internal static Action Presenter = StandalonePromptWindow.ShowWindow;
        /// <summary>ページを開く（試験は差し替える）。</summary>
        internal static Action<string> OpenUrl = url => Application.OpenURL(url);

        /// <summary>あとで実行する（窓を開いている最中ではなく、開き終わってから出すため。試験は差し替える）。</summary>
        internal static Action<Action> Schedule = action => EditorApplication.delayCall += () => action();

        /// <summary>おすすめを出す設定になっているか。</summary>
        public static bool Enabled { get => Store.Enabled; set => Store.Enabled = value; }

        /// <summary>描く窓が開いた（OnEnable）。窓が開き終わってから <see cref="NotifyPaintWindowOpened"/> を呼ぶ。</summary>
        public static void OnPaintWindowOpened() => Schedule(() => NotifyPaintWindowOpened());

        /// <summary>描く窓を開いたとき。設定が入で、このセッションでまだ出していなければ、出したと覚えてから窓を出す。出したら true。</summary>
        public static bool NotifyPaintWindowOpened()
        {
            if (!Store.Enabled || Store.ShownThisSession) return false;
            Store.ShownThisSession = true;
            Presenter();
            return true;
        }

        /// <summary>窓の選び。ダウンロード → ページを開く、あとで → 何もしない（このセッションではもう出さない）、今後表示しない → 設定を切る。</summary>
        public static void Choose(Choice choice)
        {
            switch (choice)
            {
                case Choice.Download: OpenUrl(DownloadUrl); break;
                case Choice.Never: Store.Enabled = false; break;
            }
        }

        /// <summary>Preferences の入切（変えたら true）。</summary>
        public static bool DrawPreference()
        {
            bool now = Enabled;
            bool next = EditorGUILayout.Toggle(L.Content("Suggest the standalone YoluPainter", "Shows a small window once per editor session when the YoluPainter window is opened."), now);
            if (next == now) return false;
            Enabled = next;
            return true;
        }
    }

    /// <summary>おすすめの小さな窓（モードなし。閉じるだけなら「あとで」と同じ）。</summary>
    internal sealed class StandalonePromptWindow : EditorWindow
    {
        internal static StandalonePromptWindow Current { get; private set; }

        internal static void ShowWindow()
        {
            // バッチモードでは窓を出さない（誰も見ていない）
            if (Application.isBatchMode) return;
            var window = CreateInstance<StandalonePromptWindow>();
            window.titleContent = new GUIContent("YoluPainter");
            var size = new Vector2(440, 112);
            window.minSize = window.maxSize = size;
            var main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.center.x - size.x / 2, main.center.y - size.y / 2, size.x, size.y);
            window.ShowUtility();
        }

        void OnEnable() => Current = this;
        void OnDisable() { if (Current == this) Current = null; }

        void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(L.Tr("The standalone YoluPainter is available"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(L.Tr("Only in the standalone: user channels, noise and grunge, CLIP STUDIO brushes (.sut)"), EditorStyles.wordWrappedLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L.Tr("Open the download page"))) Pick(StandalonePrompt.Choice.Download);
                if (GUILayout.Button(L.Tr("Later"))) Pick(StandalonePrompt.Choice.Later);
                if (GUILayout.Button(L.Tr("Don't show again"))) Pick(StandalonePrompt.Choice.Never);
            }
            EditorGUILayout.Space(6);
        }

        /// <summary>選びを当てて閉じる（試験が呼ぶ）。</summary>
        internal void Pick(StandalonePrompt.Choice choice)
        {
            StandalonePrompt.Choose(choice);
            Close();
        }
    }
}
