using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.LiveLink;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>メモリの設定とセッションの覚え（本物の EditorPrefs・SessionState に触れない）。</summary>
    sealed class MemoryPromptStore : StandalonePrompt.IStore
    {
        public bool Enabled { get; set; } = true;
        public bool ShownThisSession { get; set; }
    }

    /// <summary>テストの間は、スタンドアロン版のおすすめの設定・出したかの覚えをメモリに、窓とページを開く口を何もしないものに替える。ほかのテストが
    /// 描く窓を開いても、本物の窓が出ず、描き手の設定とセッションの覚えを変えない。</summary>
    [SetUpFixture]
    public sealed class StandalonePromptPin
    {
        StandalonePrompt.IStore store; Action presenter; Action<string> openUrl; Action<Action> schedule;

        [OneTimeSetUp]
        public void Pin()
        {
            store = StandalonePrompt.Store; presenter = StandalonePrompt.Presenter; openUrl = StandalonePrompt.OpenUrl; schedule = StandalonePrompt.Schedule;
            StandalonePrompt.Store = new MemoryPromptStore(); StandalonePrompt.Presenter = () => { }; StandalonePrompt.OpenUrl = _ => { };
        }

        [OneTimeTearDown]
        public void Unpin()
        {
            StandalonePrompt.Store = store; StandalonePrompt.Presenter = presenter; StandalonePrompt.OpenUrl = openUrl; StandalonePrompt.Schedule = schedule;
        }
    }

    /// <summary>
    /// スタンドアロン版への移行のおすすめ: 描く窓を開いたとき、エディタの起動ごとに 1 回だけ小さな窓を出す。選べるのは「ダウンロードのページを開く」
    /// 「あとで」「今後表示しない」。「今後表示しない」と Preferences ▸ YoluPainter の入切は同じ設定（既定は入）。描く窓の機能は変えない。
    /// </summary>
    public sealed class StandalonePromptTests
    {
        MemoryPromptStore store;
        int shown;
        readonly List<string> opened = new List<string>();
        Action<Action> savedSchedule;

        [SetUp]
        public void Fresh()
        {
            L.OverrideLanguage(PainterLanguage.English);
            store = new MemoryPromptStore(); shown = 0; opened.Clear();
            savedSchedule = StandalonePrompt.Schedule;
            StandalonePrompt.Store = store;
            StandalonePrompt.Presenter = () => shown++;
            StandalonePrompt.OpenUrl = url => opened.Add(url);
            StandalonePrompt.Schedule = action => action(); // 窓を開き終わってからの代わりに、すぐ
        }

        [TearDown]
        public void Restore()
        {
            StandalonePrompt.Presenter = () => { }; StandalonePrompt.OpenUrl = _ => { };
            StandalonePrompt.Schedule = savedSchedule;
            StandalonePrompt.Store = new MemoryPromptStore();
        }

        /// <summary>エディタを起動し直した（SessionState が消える。設定は残る）。</summary>
        void RestartEditor() => store.ShownThisSession = false;

        [Test]
        public void ItIsShownOnceInTheEditorSessionWhenThePaintWindowIsOpened()
        {
            Assert.That(StandalonePrompt.Enabled, Is.True, "on by default");
            Assert.That(StandalonePrompt.NotifyPaintWindowOpened(), Is.True);
            Assert.That(shown, Is.EqualTo(1));
            // 同じセッションでもう 1 つ開いても、閉じて開き直しても、出さない
            Assert.That(StandalonePrompt.NotifyPaintWindowOpened(), Is.False);
            StandalonePrompt.OnPaintWindowOpened();
            Assert.That(shown, Is.EqualTo(1));
            // エディタを起動し直すと、また 1 回
            RestartEditor();
            StandalonePrompt.OnPaintWindowOpened();
            Assert.That(shown, Is.EqualTo(2));
            StandalonePrompt.OnPaintWindowOpened();
            Assert.That(shown, Is.EqualTo(2));
        }

        [Test]
        public void LaterOpensNothingAndAsksNoMoreInThisSessionButAgainInTheNext()
        {
            StandalonePrompt.NotifyPaintWindowOpened();
            StandalonePrompt.Choose(StandalonePrompt.Choice.Later);
            Assert.That(opened, Is.Empty);
            Assert.That(StandalonePrompt.Enabled, Is.True, "Later does not turn it off");
            Assert.That(StandalonePrompt.NotifyPaintWindowOpened(), Is.False);
            RestartEditor();
            Assert.That(StandalonePrompt.NotifyPaintWindowOpened(), Is.True);
        }

        [Test]
        public void TheDownloadButtonOpensTheLatestReleasePageAndKeepsTheSetting()
        {
            StandalonePrompt.NotifyPaintWindowOpened();
            StandalonePrompt.Choose(StandalonePrompt.Choice.Download);
            Assert.That(opened, Is.EqualTo(new[] { "https://github.com/YozoraKurage/YoluPainter/releases/latest" }));
            Assert.That(StandalonePrompt.DownloadUrl, Is.EqualTo(opened[0]));
            Assert.That(StandalonePrompt.Enabled, Is.True);
        }

        [Test]
        public void DoNotShowAgainTurnsItOffForGoodAndThePreferenceTurnsItBackOn()
        {
            StandalonePrompt.NotifyPaintWindowOpened();
            StandalonePrompt.Choose(StandalonePrompt.Choice.Never);
            Assert.That(StandalonePrompt.Enabled, Is.False);
            Assert.That(opened, Is.Empty);
            RestartEditor();
            Assert.That(StandalonePrompt.NotifyPaintWindowOpened(), Is.False, "turned off, so not even after a restart");
            StandalonePrompt.OnPaintWindowOpened();
            Assert.That(shown, Is.EqualTo(1), "only the first time, before it was turned off");
            // Preferences の入切は同じ設定
            StandalonePrompt.Enabled = true;
            Assert.That(StandalonePrompt.NotifyPaintWindowOpened(), Is.True);
            Assert.That(shown, Is.EqualTo(2));
        }

        [Test]
        public void TurnedOffInThePreferencesItIsNeverShown()
        {
            StandalonePrompt.Enabled = false;
            for (int i = 0; i < 3; i++) { RestartEditor(); StandalonePrompt.OnPaintWindowOpened(); }
            Assert.That(shown, Is.Zero);
            Assert.That(store.ShownThisSession, Is.False, "nothing was recorded either");
        }

        [Test]
        public void TheRealStoreIsOnByDefaultKeepsTheSettingAndForgetsTheSessionOnRestart()
        {
            // 本物の EditorPrefs・SessionState を使う入口の既定と往復（試験用のキーで。描き手の設定は変えない）
            var real = StandalonePrompt.CreateEditorStore("Yozolab.YoluPainter.Tests.SuggestStandalone");
            try
            {
                Assert.That(real.Enabled, Is.True, "the default is on");
                Assert.That(real.ShownThisSession, Is.False);
                real.Enabled = false; real.ShownThisSession = true;
                var again = StandalonePrompt.CreateEditorStore("Yozolab.YoluPainter.Tests.SuggestStandalone");
                Assert.That(again.Enabled, Is.False);
                Assert.That(again.ShownThisSession, Is.True);
            }
            finally
            {
                EditorPrefs.DeleteKey("Yozolab.YoluPainter.Tests.SuggestStandalone.Enabled");
                SessionState.EraseBool("Yozolab.YoluPainter.Tests.SuggestStandalone.Shown");
            }
            Assert.That(StandalonePrompt.EnabledKey, Is.Not.EqualTo(StandalonePrompt.ShownKey));
        }

        [Test]
        public void ThePreferencePageHasTheSwitchAndItsTextsAreTranslated()
        {
            var provider = LiveLinkPreferences.Create();
            Assert.That(provider.settingsPath, Is.EqualTo("Preferences/YoluPainter"));
            Assert.That(provider.keywords, Does.Contain("suggest"));
            L.OverrideLanguage(PainterLanguage.Japanese);
            Assert.That(L.Tr("Suggest the standalone YoluPainter"), Is.EqualTo("スタンドアロン版をすすめる"));
            Assert.That(L.Tr("Open the download page"), Is.EqualTo("ダウンロードのページを開く"));
            Assert.That(L.Tr("Later"), Is.EqualTo("あとで"));
            Assert.That(L.Tr("Don't show again"), Is.EqualTo("今後表示しない"));
        }

        /// <summary>窓と Preferences の入切の描き。窓は batchmode では動かないのでスキップする（GUI モードの常駐で回す）。</summary>
        [Test]
        public void TheWindowAndThePreferenceSwitchDrawWithoutErrors()
        {
            if (Application.isBatchMode) Assert.Ignore("The window needs a non-batch Editor (test-daemon.sh start in GUI mode).");
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            StandalonePrompt.Presenter = StandalonePromptWindow.ShowWindow;
            var probe = EditorWindow.CreateWindow<PreferenceProbeWindow>();
            try
            {
                probe.position = new Rect(60, 60, 420, 80);
                probe.SendEvent(new Event { type = EventType.Layout }); probe.SendEvent(new Event { type = EventType.Repaint });
                StandalonePrompt.NotifyPaintWindowOpened();
                var window = StandalonePromptWindow.Current;
                Assert.That(window, Is.Not.Null, "the small window is shown");
                window.SendEvent(new Event { type = EventType.Layout }); window.SendEvent(new Event { type = EventType.Repaint });
                // 選ぶと当たって閉じる
                window.Pick(StandalonePrompt.Choice.Never);
                Assert.That(StandalonePrompt.Enabled, Is.False);
                Assert.That(StandalonePromptWindow.Current, Is.Null, "closed");
            }
            finally { probe.Close(); if (StandalonePromptWindow.Current != null) StandalonePromptWindow.Current.Close(); }
        }

        /// <summary>おすすめの文は、スタンドアロン版にしかない機能の名前を 1 行で言うだけ（使い方の説明・指示の文を置かない）。</summary>
        [Test]
        public void TheTextIsNamesOfStandaloneOnlyFeaturesNotInstructions()
        {
            foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
            {
                L.OverrideLanguage(language);
                foreach (var text in new[] { L.Tr("The standalone YoluPainter is available"), L.Tr("Only in the standalone: user channels, noise and grunge, CLIP STUDIO brushes (.sut)") })
                {
                    Assert.That(NoInstructionTextTests.LooksLikeInstruction(text, true), Is.False, language + ": " + text);
                    Assert.That(text, Does.Not.Contain("\n"), "one line");
                }
            }
        }
    }
}
