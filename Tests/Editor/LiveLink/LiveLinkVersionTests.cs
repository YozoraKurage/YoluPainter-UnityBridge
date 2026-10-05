using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.LiveLink;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// Live Link の版の確かめ（Unity の側）: つないだまま版か機能の印がずれていれば、窓の状態の行に警告の印とツールチップ（両方の版・どちらを上げるか・
    /// 使えない機能の名前）を出す。プロトコルの版が重ならなければ断られ、その理由は「どちらを何版以上に」の形で自分の言語で出る。版の欄の無い古い相手とも
    /// つながる。相手はブリッジの中の自己診断のスタンドアロン（名乗りを試験が決める。本物のソケットと共有メモリ）。
    /// </summary>
    public sealed class LiveLinkVersionTests
    {
        static int s_counter;
        ulong server;
        LiveLinkSession session;

        [SetUp]
        public void RequireBridge()
        {
            Assert.That(LiveLinkBridge.Problem, Is.Null, "the bridge library must load on the Linux and Windows editors");
            L.OverrideLanguage(PainterLanguage.English);
        }

        [TearDown]
        public void CleanUp()
        {
            session?.Dispose(); session = null;
            LiveLinkTestServer.Stop(server); server = 0;
            L.OverrideLanguage(null);
        }

        static string UniqueName() => "ylp-unity-version-" + Process.GetCurrentProcess().Id + "-" + (++s_counter);

        void Pump(Func<bool> done, string what, double seconds = 10)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.Elapsed.TotalSeconds > seconds) Assert.Fail("timed out waiting for " + what + " (" + session?.StatusText + ")");
                session?.Tick();
                Thread.Sleep(5);
            }
        }

        static ulong? s_ownFeatures;

        /// <summary>このブリッジが挨拶で出す機能の印（ブリッジの定数。つながった後の報告から読む。試験の相手の印を、これに合わせたり足したりするため）。</summary>
        ulong OwnFeatures()
        {
            if (s_ownFeatures == null)
            {
                Connect(new Version(0, 1, 0), features: 0);
                s_ownFeatures = session.Report.OwnFeatures;
                session.Dispose(); session = null;
                LiveLinkTestServer.Stop(server); server = 0;
            }
            return s_ownFeatures.Value;
        }

        /// <summary>名乗りを決めた自己診断のスタンドアロンにつなぐ（つながるまで、または断られるまで待つ）。<paramref name="features"/> を決めなければ、
        /// このブリッジと同じ印を出す相手（印を立てる機能が増えても、印のずれは起きない）。</summary>
        void Connect(Version standaloneVersion, Version minPeer = null, ulong? features = null, int? protocolMin = null, int protocolMax = 1)
        {
            ulong marks = features ?? OwnFeatures();
            string name = UniqueName();
            server = LiveLinkTestServer.Start(name, 128, 64);
            Assert.That(server, Is.Not.EqualTo(0UL));
            Assert.That(LiveLinkTestServer.Configure(server, standaloneVersion, minPeer, marks), Is.True);
            if (protocolMin != null) Assert.That(LiveLinkTestServer.SetProtocol(server, protocolMin.Value, protocolMax), Is.True);
            session = LiveLinkSession.Start(name);
            Pump(() => session.Status != LiveLinkStatus.Connecting, "the connection to settle");
            session.Tick(); // 終わりの知らせ（断られた・つなげなかった）を読み切る
        }

        [Test]
        public void AStandaloneThatMeetsTheVersionsShowsNoWarning()
        {
            Connect(new Version(0, 1, 0));
            Assert.That(session.Status, Is.EqualTo(LiveLinkStatus.Connected));
            var report = session.Report;
            Assert.That(report.State, Is.EqualTo(LiveLinkReport.Kind.Connected));
            Assert.That(report.PeerVersion, Is.EqualTo(new Version(0, 1, 0)));
            Assert.That(LiveLinkBridge.PeerAppVersion(session.Handle), Is.EqualTo(new Version(0, 1, 0)));
            Assert.That(report.Skewed, Is.False);
            Assert.That(session.VersionTooltip, Is.Null);
            // 状態の行は文字だけ（印もツールチップも付かない）
            var content = LiveLinkWindow.StateContent(session, "Connected");
            Assert.That(content.text, Is.EqualTo("Connected"));
            Assert.That(content.image, Is.Null);
            Assert.That(content.tooltip, Is.Empty);
            Assert.That(session.DisplayStatusText, Is.EqualTo(session.StatusText), "no refusal, so the bridge's own text");
            Assert.That(LiveLinkWindow.DetailsStatusLine(session), Is.EqualTo(session.StatusText), "connected: the state row is a short name, so the details row keeps the bridge's text");
        }

        [Test]
        public void AStandaloneWithoutTheVersionFieldsStillConnectsAndTheWindowShowsTheWarning()
        {
            Connect(null); // 版の欄を送らない古いスタンドアロンの役
            Assert.That(session.Status, Is.EqualTo(LiveLinkStatus.Connected), "a missing version does not stop the link");
            var report = session.Report;
            Assert.That(report.PeerVersion, Is.Null);
            Assert.That(LiveLinkBridge.PeerAppVersion(session.Handle), Is.Null);
            Assert.That(report.Skewed && report.UpdateStandalone && !report.UpdatePackage, Is.True);
            string own = LiveLinkNotice.OwnVersion;
            Assert.That(own, Is.Not.Null.And.Not.EqualTo("unknown"), "the package version is read from package.json");

            var content = LiveLinkWindow.StateContent(session, "Connected");
            Assert.That(content.text, Is.EqualTo("Connected"), "the state itself stays a short name");
            Assert.That(content.image, Is.Not.Null, "the warning mark");
            Assert.That(content.tooltip, Is.EqualTo("Unity package " + own + " · standalone unknown\nThe standalone must be updated."));

            L.OverrideLanguage(PainterLanguage.Japanese);
            Assert.That(session.VersionTooltip, Is.EqualTo("Unity のパッケージ " + own + "・スタンドアロン 不明\nスタンドアロンを更新する必要があります。"));
        }

        [Test]
        public void AStandaloneThatAsksForANewerPackageNamesTheVersionToUpdateTo()
        {
            var wanted = new Version(9, 0, 0);
            Connect(new Version(0, 1, 0), minPeer: wanted);
            var report = session.Report;
            Assert.That(report.Skewed && report.UpdatePackage && !report.UpdateStandalone, Is.True);
            Assert.That(report.PackageTo, Is.EqualTo(wanted));
            string own = LiveLinkNotice.OwnVersion;
            Assert.That(session.VersionTooltip, Is.EqualTo("Unity package " + own + " · standalone 0.1.0\nThe Unity package must be 9.0.0 or newer."));
            L.OverrideLanguage(PainterLanguage.Japanese);
            Assert.That(session.VersionTooltip, Is.EqualTo("Unity のパッケージ " + own + "・スタンドアロン 0.1.0\nUnity のパッケージを 9.0.0 以上に上げる必要があります。"));
        }

        /// <summary>機能の印の名前（画面の文と同じ。名前を知らない印は「新しい機能」にまとめる）。</summary>
        static string ExpectedNames(ulong mask, bool japanese)
        {
            var table = new (ulong bit, string en, string ja)[]
            {
                (LiveLinkFeatures.MaterialValues, "Material values", "マテリアルの値"),
                (LiveLinkFeatures.Assets, "Assets", "アセット"),
                (LiveLinkFeatures.ProjectTransfer, "Project transfer", "プロジェクトの転送"),
                (LiveLinkFeatures.Animation, "Animation", "アニメーション"),
                (LiveLinkFeatures.OriginalTextures, "Original textures", "元のテクスチャ"),
            };
            var names = new System.Collections.Generic.List<string>();
            foreach (var (bit, en, ja) in table) if ((mask & bit) != 0) names.Add(japanese ? ja : en);
            if ((mask & ~LiveLinkFeatures.Known) != 0) names.Add(japanese ? "新しい機能" : "Newer features");
            return string.Join(japanese ? "・" : ", ", names);
        }

        [Test]
        public void FeaturesOnlyOneSideHasAreNamedAndTheCommonPartIsWhatCanBeUsed()
        {
            // スタンドアロンは、このブリッジが出していない名前のある印（全部出しているなら無し）と、名前を知らない印を足して出す。
            // このブリッジの印はブリッジから読み、期待する集合も計算で出す（印を立てる機能が増えても、この試験は変わらない）
            ulong own = OwnFeatures();
            ulong extra = (LiveLinkFeatures.Known & ~own) | (1UL << 50);
            Connect(new Version(0, 1, 0), features: own | extra);
            var report = session.Report;
            Assert.That(report.OwnFeatures, Is.EqualTo(own));
            Assert.That(report.PeerFeatures, Is.EqualTo(own | extra));
            Assert.That(report.MissingHere, Is.EqualTo(extra));
            Assert.That(report.MissingOnPeer, Is.EqualTo(0UL));
            Assert.That(report.CommonFeatures, Is.EqualTo(own));
            Assert.That(session.CommonFeatures, Is.EqualTo(own));
            Assert.That(LiveLinkBridge.CommonFeatures(session.Handle), Is.EqualTo(own));
            Assert.That(report.Skewed && report.UpdatePackage && !report.UpdateStandalone, Is.True);
            string ownVersion = LiveLinkNotice.OwnVersion;
            Assert.That(session.VersionTooltip, Does.Contain("Unavailable: " + ExpectedNames(extra, false)));
            Assert.That(session.VersionTooltip, Does.Contain("The Unity package must be updated."));
            L.OverrideLanguage(PainterLanguage.Japanese);
            Assert.That(session.VersionTooltip, Does.Contain("使えない機能: " + ExpectedNames(extra, true)));
            Assert.That(session.VersionTooltip, Does.StartWith("Unity のパッケージ " + ownVersion));
        }

        [Test]
        public void FeaturesTheBridgeHasAndTheStandaloneLacksAreNamedAndTheStandaloneIsToBeUpdated()
        {
            // スタンドアロンは印を 1 つも出さない（このブリッジの印が無ければ、ずれなし）
            ulong own = OwnFeatures();
            Connect(new Version(0, 1, 0), features: 0);
            var report = session.Report;
            Assert.That(report.MissingOnPeer, Is.EqualTo(own));
            Assert.That(report.CommonFeatures, Is.EqualTo(0UL));
            Assert.That(report.Skewed, Is.EqualTo(own != 0));
            if (own == 0) { Assert.That(session.VersionTooltip, Is.Null); return; }
            Assert.That(session.VersionTooltip, Does.Contain("Unavailable: " + ExpectedNames(own, false)));
            Assert.That(session.VersionTooltip, Does.Contain("The standalone must be updated."));
            L.OverrideLanguage(PainterLanguage.Japanese);
            Assert.That(session.VersionTooltip, Does.Contain("使えない機能: " + ExpectedNames(own, true)));
        }

        [Test]
        public void TheWarningMarkAndTooltipGoWhenTheLinkIsClosedByTheStandalone()
        {
            // 版が古いスタンドアロン（欄の無い古い役）につなぎ、警告の印が付いているところから、スタンドアロンが終わる（診断のサーバーを止める）
            Connect(null);
            Assert.That(session.Status, Is.EqualTo(LiveLinkStatus.Connected));
            Assert.That(LiveLinkWindow.StateContent(session, "Connected").image, Is.Not.Null, "warning while the link is up");
            Assert.That(session.VersionTooltip, Is.Not.Null);

            LiveLinkTestServer.Stop(server); server = 0;
            Pump(() => session.Status != LiveLinkStatus.Connected, "the link to close");
            session.Tick();
            Assert.That(session.Status, Is.Not.EqualTo(LiveLinkStatus.Connected));
            // 閉じたあとは、状態の行に印もツールチップも付かない。相手の版・使える機能も答えない
            var content = LiveLinkWindow.StateContent(session, "Not connected");
            Assert.That(content.text, Is.EqualTo("Not connected"));
            Assert.That(content.image, Is.Null);
            Assert.That(content.tooltip, Is.Empty);
            Assert.That(session.VersionTooltip, Is.Null);
            Assert.That(session.LiveReport.Skewed, Is.False);
            Assert.That(session.Report.Skewed, Is.False, "the bridge forgets the link when it ends");
            Assert.That(LiveLinkBridge.PeerAppVersion(session.Handle), Is.Null);
            Assert.That(session.CommonFeatures, Is.EqualTo(0UL));
        }

        [Test]
        public void ANoneOverlappingProtocolRangeIsRefusedWithWhichSideToUpdateInBothLanguages()
        {
            // スタンドアロンは先のプロトコルの版（2〜3）だけを読み、Unity のパッケージを 0.6.0 以上に求める
            Connect(new Version(0, 9, 0), minPeer: new Version(0, 6, 0), protocolMin: 2, protocolMax: 3);
            Assert.That(session.Status, Is.EqualTo(LiveLinkStatus.Failed));
            Assert.That(session.EndedBy, Is.EqualTo(LiveLinkEventKind.Rejected));
            Assert.That(session.EndedCode, Is.EqualTo(1), "RejectCode.VersionMismatch");
            var report = session.Report;
            Assert.That(report.Refused, Is.True);
            Assert.That(report.RefusedUpdate, Is.EqualTo(1));
            Assert.That(report.RefusedTo, Is.EqualTo(new Version(0, 6, 0)));
            Assert.That(session.DisplayStatusText, Is.EqualTo("Protocol versions do not match (Unity 1–1, standalone 2–3). The Unity package must be 0.6.0 or newer."));
            L.OverrideLanguage(PainterLanguage.Japanese);
            Assert.That(session.DisplayStatusText, Is.EqualTo("プロトコルの版が合いません（Unity 側 1〜1、スタンドアロン 2〜3）。Unity のパッケージを 0.6.0 以上に上げる必要があります。"));
            // 窓は警告の枠で出す（状態の行ではなく）
            L.OverrideLanguage(PainterLanguage.English);
            Assert.That(LiveLinkWindow.StateText(session, out var type), Does.StartWith("Protocol versions do not match"));
            Assert.That(type, Is.EqualTo(MessageType.Warning));
            // 詳しくの行は、ブリッジの生の文（日英併記の「Update …」）ではなく、自分の言語の理由で、警告の枠と重ねない
            Assert.That(session.StatusText, Does.Contain(" / "), "the bridge's own text is bilingual");
            Assert.That(LiveLinkWindow.DetailsStatusLine(session), Is.Null, "the warning box already says it");
            session.Dispose(); session = null;

            // 古い範囲（0〜0）だけを読むスタンドアロンなら、スタンドアロンを上げる（この版のブリッジが求める版は、まだ無い）
            LiveLinkTestServer.Stop(server); server = 0;
            Connect(new Version(0, 0, 1), protocolMin: 0, protocolMax: 0);
            Assert.That(session.Report.RefusedUpdate, Is.EqualTo(2));
            Assert.That(session.DisplayStatusText, Is.EqualTo("Protocol versions do not match (Unity 1–1, standalone 0–0). The standalone must be updated."));
        }

        [Test]
        public void TheVersionTextsAreStatesAndReasonsNotInstructions()
        {
            // 操作の指示の言い回し（Click・Drag・ください など）を含まない。ここでは、出す文の全部を英日で作って確かめる
            var skewed = new LiveLinkReport
            {
                State = LiveLinkReport.Kind.Connected, UpdateStandalone = true, UpdatePackage = true, StandaloneTo = new Version(1, 2, 3), PackageTo = new Version(4, 5, 6),
                MissingOnPeer = LiveLinkFeatures.Assets, MissingHere = LiveLinkFeatures.ProjectTransfer | (1UL << 40),
            };
            var refused = new LiveLinkReport { State = LiveLinkReport.Kind.Refused, RefusedUpdate = 2, RefusedTo = new Version(1, 0, 0), UnityMinProtocol = 1, UnityMaxProtocol = 1, StandaloneMinProtocol = 3, StandaloneMaxProtocol = 4 };
            foreach (var language in new[] { PainterLanguage.English, PainterLanguage.Japanese })
            {
                L.OverrideLanguage(language);
                foreach (var text in new[] { LiveLinkNotice.Tooltip(skewed, "0.3.0"), LiveLinkNotice.Refusal(refused) })
                {
                    Assert.That(NoInstructionTextTests.LooksLikeInstruction(text), Is.False, language + ": " + text);
                    Assert.That(NoInstructionTextTests.LooksLikeInstruction(text, true), Is.False, language + ": " + text);
                }
            }
            L.OverrideLanguage(PainterLanguage.English);
            Assert.That(LiveLinkNotice.Tooltip(skewed, "0.3.0"), Is.EqualTo(
                "Unity package 0.3.0 · standalone unknown\nThe standalone must be 1.2.3 or newer.\nThe Unity package must be 4.5.6 or newer.\nUnavailable: Assets, Project transfer, Newer features"));
            Assert.That(LiveLinkNotice.Tooltip(LiveLinkReport.Empty, "0.3.0"), Is.Null);
            Assert.That(LiveLinkNotice.Refusal(skewed), Is.Null);
        }
    }
}
