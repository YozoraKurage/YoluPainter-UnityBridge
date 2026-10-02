using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ウィンドウでの .ylp の形式: 古い形式を開くと知らせ、保存は今の形式と書いたアプリを記録し、退避なしで古い形式を書き換えるときは
    /// 確かめる。新しすぎる形式は開かず、開いているプロジェクトはそのまま。</summary>
    public sealed partial class WindowTests
    {
        static string FormatOneFixture => PackagePaths.Physical("Tests/Editor/Persistence/Fixtures~/format1.ylp");

        [Test] public void AnOlderFormatOpensAndSavingRecordsTheCurrentFormatAndWriter()
        {
            string path = NewTempPath(".ylp"); File.Copy(FormatOneFixture, path);
            var fake = new FakeDialogs { File = path }; window.Dialogs = fake;
            window.OpenProjectAt(path);
            Assert.That(window.OpenedFormat, Is.EqualTo(1));
            Assert.That(window.Document.Layers.Count, Is.EqualTo(5));
            Assert.That(window.StatusMessage, Does.Contain("older .ylp format (1)"));
            var personal = PainterSettings.PersonalSettings; personal.backupsToKeep = 0; PainterSettings.Save(null, personal);
            byte[] before = File.ReadAllBytes(path);
            fake.ConfirmAnswer = false; window.SaveProject(false);
            Assert.That(fake.Asked, Does.Contain("Confirm: Upgrade the file format?"), "without backups, upgrading in place is confirmed");
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "declining leaves the file as it was");
            fake.ConfirmAnswer = true; window.SaveProject(false);
            var saved = YlpFormat.Open(YlpStore.Load(path).Files);
            Assert.That(saved.Info.Format, Is.EqualTo(YlpFormat.Current));
            Assert.That(saved.Info.SavedBy.App, Is.EqualTo("YoluPainter"));
            Assert.That(saved.Info.SavedBy.Version, Is.EqualTo(PackagePaths.Version));
            Assert.That(saved.Info.SavedBy.Unity, Is.EqualTo(Application.unityVersion));
            Assert.That(saved.Info.CreatedBy, Is.Null, "a format 1 file does not say who created it, and saving does not make it up");
            Assert.That(window.OpenedFormat, Is.EqualTo(YlpFormat.Current));
            fake.Asked.Clear(); window.SaveProject(false);
            Assert.That(fake.Asked, Does.Not.Contain("Confirm: Upgrade the file format?"), "once saved, the file is the current format");
        }

        [Test] public void ANewProjectRecordsThisVersionAsItsCreator()
        {
            string path = NewTempPath(".ylp");
            window.Dialogs = new FakeDialogs { File = path };
            window.SaveProject(false);
            var info = YlpFormat.Open(YlpStore.Load(path).Files).Info;
            Assert.That(info.Format, Is.EqualTo(YlpFormat.Current));
            Assert.That(info.CreatedBy?.ToString(), Is.EqualTo(info.SavedBy.ToString()));
            Assert.That(PackagePaths.Version, Is.Not.EqualTo("unknown"), "the package version is found");
        }

        [Test] public void ANewerFormatIsNotOpenedAndTheOpenProjectStays()
        {
            string path = NewTempPath(".ylp");
            var files = new Dictionary<string, byte[]>
            {
                { YlpArchive.NativeName, new byte[] { 1, 2, 3 } },
                { YlpFormat.InfoName, System.Text.Encoding.UTF8.GetBytes("{ \"format\": 99, \"savedBy\": { \"app\": \"YoluPainter\", \"version\": \"9.0.0\", \"unity\": \"6000.0.1f1\" } }") },
            };
            YlpStore.Save(path, files, null, true, 0);
            var document = window.Document;
            window.Dialogs = new FakeDialogs { File = path };
            window.OpenProjectAt(path);
            Assert.That(window.Document, Is.SameAs(document));
            Assert.That(window.ProjectPath, Is.Null);
            Assert.That(window.StatusMessage, Does.Contain("format 99").And.Contain("YoluPainter 9.0.0"));
        }
    }
}
