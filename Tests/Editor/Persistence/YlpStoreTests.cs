using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core.Persistence;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// .ylp の保存と読み込み（YlpStore）。実際の一時フォルダーに書き、外部改変の検出、退避した版の数、途中で落ちたときに
    /// 元のファイルがそのまま残ること、一時ファイルやロックを残さないことを確かめる。
    /// </summary>
    public sealed class YlpStoreTests
    {
        string root, path;

        [SetUp] public void CreateRoot()
        {
            root = Path.Combine(Path.GetTempPath(), "yolupainter-ylp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            path = Path.Combine(root, "doc.ylp");
        }

        [TearDown] public void DeleteRoot()
        { if (Directory.Exists(root)) Directory.Delete(root, true); }

        // ───────────── 新規・上書き・退避 ─────────────

        [Test] public void NewFileLeavesNoBackupTempOrLock()
        {
            var saved = YlpStore.Save(path, Version(1), null, false);
            Assert.That(saved.Path, Is.EqualTo(path));
            Assert.That(saved.Backup, Is.Null);
            Assert.That(Leftovers(), Is.Empty);
            Assert.That(Directory.GetFileSystemEntries(root).Select(Path.GetFileName), Is.EqualTo(new[] { "doc.ylp" }), "no backup folder, no .saving-*~, no .lock~");
            var loaded = YlpStore.Load(path);
            Assert.That(loaded.Files[YlpArchive.NativeName], Is.EqualTo(Version(1)[YlpArchive.NativeName]));
            Assert.That(loaded.Files["composite/Color.png"], Is.EqualTo(Version(1)["composite/Color.png"]));
            Assert.That(loaded.Token, Is.EqualTo(saved.Token), "the token of a save and of a load of the same file agree");
            Assert.That(YlpStore.HasExternalChange(path, saved.Token), Is.False);
            Assert.That(YlpStore.Backups(path), Is.Empty);
        }

        [Test] public void SaveCreatesAMissingFolder()
        {
            string nested = Path.Combine(root, "a", "b", "doc.ylp");
            YlpStore.Save(nested, Version(1), null, false);
            Assert.That(YlpStore.Load(nested).Files[YlpArchive.NativeName], Is.EqualTo(Doc(1)));
        }

        [Test] public void OverwriteWithTheCurrentTokenKeepsTheOldBytesAsABackup()
        {
            var first = YlpStore.Save(path, Version(1), null, false);
            byte[] bytes1 = File.ReadAllBytes(path);
            var second = YlpStore.Save(path, Version(2), first.Token, false);
            byte[] bytes2 = File.ReadAllBytes(path);
            Assert.That(YlpStore.Backups(path), Has.Count.EqualTo(1));
            Assert.That(second.Backup, Is.EqualTo(YlpStore.Backups(path)[0]));
            Assert.That(Path.GetDirectoryName(second.Backup), Is.EqualTo(YlpStore.BackupFolder(path)));
            Assert.That(File.ReadAllBytes(second.Backup), Is.EqualTo(bytes1), "the backup is the previous file byte for byte");
            Assert.That(YlpStore.Load(second.Backup).Files[YlpArchive.NativeName], Is.EqualTo(Doc(1)));
            Assert.That(YlpStore.Load(path).Files[YlpArchive.NativeName], Is.EqualTo(Doc(2)));
            Assert.That(Leftovers(), Is.Empty);

            var third = YlpStore.Save(path, Version(3), second.Token, false);
            var backups = YlpStore.Backups(path);
            Assert.That(backups, Has.Count.EqualTo(2));
            Assert.That(File.ReadAllBytes(backups[0]), Is.EqualTo(bytes2), "newest first");
            Assert.That(File.ReadAllBytes(backups[1]), Is.EqualTo(bytes1));
            Assert.That(third.Backup, Is.EqualTo(backups[0]));
        }

        [Test] public void RapidSavesKeepBackupsInNewestFirstOrder()
        {
            var token = YlpStore.Save(path, Version(0), null, false).Token;
            for (int i = 1; i <= 8; i++) token = YlpStore.Save(path, Version(i), token, false).Token; // 同じミリ秒に重なって "_" 付きの名前にもなりうる
            var docs = YlpStore.Backups(path).Select(b => YlpStore.Load(b).Files[YlpArchive.NativeName]).ToList();
            Assert.That(docs, Is.EqualTo(Enumerable.Range(0, 8).Reverse().Select(Doc).ToList()));
        }

        /// <summary>同じミリ秒に重なった退避は名前に "_" を足す。その名前も新しい順に並ぶこと。</summary>
        [Test] public void CollidingBackupNamesStillSortNewestFirst()
        {
            string folder = YlpStore.BackupFolder(path); Directory.CreateDirectory(folder);
            var names = new[] { "doc-20260101T000000000Z.ylp", "doc-20260101T000000000Z_.ylp", "doc-20260101T000000000Z__.ylp", "doc-20260101T000000001Z.ylp", "doc-20251231T235959999Z_.ylp" };
            foreach (var n in names) File.WriteAllBytes(Path.Combine(folder, n), new byte[] { 1 });
            Assert.That(YlpStore.Backups(path).Select(Path.GetFileName), Is.EqualTo(new[]
            { "doc-20260101T000000001Z.ylp", "doc-20260101T000000000Z__.ylp", "doc-20260101T000000000Z_.ylp", "doc-20260101T000000000Z.ylp", "doc-20251231T235959999Z_.ylp" }));
        }

        [Test] public void UppercaseExtensionIsAcceptedAndItsBackupsAreListed()
        {
            string upper = Path.Combine(root, "Doc.YLP");
            var first = YlpStore.Save(upper, Version(1), null, false);
            YlpStore.Save(upper, Version(2), first.Token, false);
            Assert.That(YlpStore.Backups(upper), Has.Count.EqualTo(1));
            Assert.That(YlpStore.Load(YlpStore.Backups(upper)[0]).Files[YlpArchive.NativeName], Is.EqualTo(Doc(1)));
        }

        // ───────────── 外部改変・上書きの許可 ─────────────

        [Test] public void AStaleTokenIsRefusedAndTheTargetIsUntouched()
        {
            var first = YlpStore.Save(path, Version(1), null, false);
            var second = YlpStore.Save(path, Version(2), first.Token, false);
            byte[] current = File.ReadAllBytes(path);
            Assert.That(() => YlpStore.Save(path, Version(3), first.Token, false), Throws.TypeOf<IOException>().With.Message.Contains("changed outside"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(current));
            Assert.That(YlpStore.Backups(path), Has.Count.EqualTo(1), "a refused save makes no backup");
            Assert.That(Leftovers(), Is.Empty);
            Assert.That(YlpStore.Save(path, Version(3), second.Token, false).Backup, Is.Not.Null, "the current token still works");
        }

        [Test] public void AnOutsideRewriteIsRefusedEvenWithTheLatestToken()
        {
            var saved = YlpStore.Save(path, Version(1), null, false);
            byte[] outside = YlpArchive.Write(Version(99));
            File.WriteAllBytes(path, outside);
            Assert.That(() => YlpStore.Save(path, Version(2), saved.Token, false), Throws.TypeOf<IOException>().With.Message.Contains("changed outside"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(outside));
            Assert.That(Leftovers(), Is.Empty);
            Assert.That(YlpStore.Backups(path), Is.Empty);
        }

        [Test] public void AnOutsideRewriteWithTheSameLengthAndTimeIsStillRefusedBySave()
        {
            var saved = YlpStore.Save(path, Version(1), null, false);
            var time = File.GetLastWriteTimeUtc(path);
            byte[] bytes = File.ReadAllBytes(path); bytes[bytes.Length / 2] ^= 1;
            File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, time);
            Assert.That(() => YlpStore.Save(path, Version(2), saved.Token, false), Throws.TypeOf<IOException>().With.Message.Contains("changed outside"), "save always compares the full hash");
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        }

        [Test] public void TouchingTheFileWithoutChangingItDoesNotBlockSaving()
        {
            var saved = YlpStore.Save(path, Version(1), null, false);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
            Assert.That(YlpStore.Save(path, Version(2), saved.Token, false).Backup, Is.Not.Null);
        }

        [Test] public void ADeletedFileIsRefusedWhenATokenIsExpected()
        {
            var saved = YlpStore.Save(path, Version(1), null, false);
            File.Delete(path);
            Assert.That(() => YlpStore.Save(path, Version(2), saved.Token, false), Throws.TypeOf<IOException>().With.Message.Contains("missing"));
            Assert.That(File.Exists(path), Is.False, "nothing is written in place of the missing file");
            Assert.That(Leftovers(), Is.Empty);
        }

        [Test] public void AMalformedTokenIsRefusedAndTheTargetIsUntouched()
        {
            YlpStore.Save(path, Version(1), null, false);
            byte[] current = File.ReadAllBytes(path);
            foreach (string token in new[] { "", "garbage", "a:b:c", new string('a', 64) + ":x:1", new string('a', 64) + ":1" })
            {
                Assert.That(() => YlpStore.Save(path, Version(2), token, true), Throws.ArgumentException, token);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(current), token);
            }
            Assert.That(Leftovers(), Is.Empty);
            Assert.That(YlpStore.Backups(path), Is.Empty);
        }

        [Test] public void NoTokenOnAnExistingFileNeedsExplicitOverwrite()
        {
            YlpStore.Save(path, Version(1), null, false);
            byte[] bytes1 = File.ReadAllBytes(path);
            Assert.That(() => YlpStore.Save(path, Version(2), null, false), Throws.TypeOf<IOException>().With.Message.Contains("already exists"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes1));
            Assert.That(Leftovers(), Is.Empty);

            var saved = YlpStore.Save(path, Version(2), null, true);
            Assert.That(YlpStore.Load(path).Files[YlpArchive.NativeName], Is.EqualTo(Doc(2)));
            Assert.That(File.ReadAllBytes(saved.Backup), Is.EqualTo(bytes1), "an explicit overwrite still keeps the replaced file");
        }

        [Test] public void OverwritingSomethingThatIsNotAYlpKeepsItAsTheBackup()
        {
            byte[] junk = Encoding.ASCII.GetBytes("not a zip at all");
            File.WriteAllBytes(path, junk);
            var saved = YlpStore.Save(path, Version(1), null, true);
            Assert.That(File.ReadAllBytes(saved.Backup), Is.EqualTo(junk));
        }

        // ───────────── 保持する版の数 ─────────────

        [Test] public void KeepingNBackupsKeepsTheNNewest()
        {
            var bytes = new List<byte[]>();
            var token = YlpStore.Save(path, Version(1), null, false, 2).Token; bytes.Add(File.ReadAllBytes(path));
            for (int i = 2; i <= 5; i++) { token = YlpStore.Save(path, Version(i), token, false, 2).Token; bytes.Add(File.ReadAllBytes(path)); }
            var backups = YlpStore.Backups(path);
            Assert.That(backups, Has.Count.EqualTo(2));
            Assert.That(File.ReadAllBytes(backups[0]), Is.EqualTo(bytes[3]), "version 4");
            Assert.That(File.ReadAllBytes(backups[1]), Is.EqualTo(bytes[2]), "version 3");
        }

        /// <summary>同じミリ秒に何度も保存し、整理で古い版を消した後でも、新しい版がいつも先に並ぶ（同じ名前を使い直さない）。</summary>
        [Test] public void BackupsInTheSameMillisecondStayInOrderAfterPruning()
        {
            string folder = YlpStore.BackupFolder(path); Directory.CreateDirectory(folder);
            var when = new DateTime(2026, 10, 3, 1, 2, 3, 456, DateTimeKind.Utc);
            string first = YlpStore.BackupName(folder, "doc", when); File.WriteAllBytes(first, new byte[] { 1 });
            string second = YlpStore.BackupName(folder, "doc", when); File.WriteAllBytes(second, new byte[] { 2 });
            string third = YlpStore.BackupName(folder, "doc", when); File.WriteAllBytes(third, new byte[] { 3 });
            File.Delete(first); // 整理で一番古いものを消した
            string fourth = YlpStore.BackupName(folder, "doc", when);
            Assert.That(fourth, Is.Not.EqualTo(first), "a freed name is not reused");
            File.WriteAllBytes(fourth, new byte[] { 4 });
            Assert.That(YlpStore.Backups(path).Select(f => File.ReadAllBytes(f)[0]), Is.EqualTo(new byte[] { 4, 3, 2 }), "newest first");
        }

        [Test] public void KeepingZeroBackupsLeavesNoCopyOfTheOldVersion()
        {
            var first = YlpStore.Save(path, Version(1), null, false, 0);
            byte[] bytes1 = File.ReadAllBytes(path);
            var second = YlpStore.Save(path, Version(2), first.Token, false, 0);
            Assert.That(second.Backup, Is.Null);
            Assert.That(Directory.Exists(YlpStore.BackupFolder(path)), Is.False);
            Assert.That(YlpStore.Load(path).Files[YlpArchive.NativeName], Is.EqualTo(Doc(2)));
            Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => File.ReadAllBytes(f).SequenceEqual(bytes1)), Is.Empty, "the old version is gone");
            Assert.That(Leftovers(), Is.Empty);
        }

        [Test] public void KeepAllKeepsEveryBackup()
        {
            var token = YlpStore.Save(path, Version(1), null, false, YlpStore.KeepAllBackups).Token;
            for (int i = 2; i <= 6; i++) token = YlpStore.Save(path, Version(i), token, false, YlpStore.KeepAllBackups).Token;
            Assert.That(YlpStore.Backups(path), Has.Count.EqualTo(5));
        }

        [Test] public void LoweringTheLimitPrunesOlderBackupsButLeavesForeignFiles()
        {
            var token = YlpStore.Save(path, Version(1), null, false).Token;
            for (int i = 2; i <= 4; i++) token = YlpStore.Save(path, Version(i), token, false).Token;
            string folder = YlpStore.BackupFolder(path);
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine");
            File.WriteAllBytes(Path.Combine(folder, "other-20200101T000000000Z.ylp"), new byte[] { 1 });
            Assert.That(YlpStore.Backups(path), Has.Count.EqualTo(3));
            YlpStore.Save(path, Version(5), token, false, 1);
            Assert.That(YlpStore.Backups(path), Has.Count.EqualTo(1));
            Assert.That(YlpStore.Load(YlpStore.Backups(path)[0]).Files[YlpArchive.NativeName], Is.EqualTo(Doc(4)));
            Assert.That(File.Exists(Path.Combine(folder, "notes.txt")) && File.Exists(Path.Combine(folder, "other-20200101T000000000Z.ylp")), "files that are not this document's backups are left alone");
        }

        /// <summary>退避フォルダーに「doc-keep.ylp」のような、時刻ではない名前の同じ接頭辞のファイル（利用者が残したくて改名した版など）が
        /// あっても、保存は直前の版を退避し、返した <see cref="YlpSnapshot.Backup"/> が実在する。</summary>
        [Test] public void TheBackupJustMadeSurvivesPruningNextToARenamedBackup()
        {
            var first = YlpStore.Save(path, Version(1), null, false, 1);
            string folder = YlpStore.BackupFolder(path); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "doc-keep.ylp"), YlpArchive.Write(Version(0)));
            var second = YlpStore.Save(path, Version(2), first.Token, false, 1);
            Assert.That(second.Backup, Is.Not.Null);
            Assert.That(File.Exists(second.Backup), Is.True, "the returned backup path exists");
            Assert.That(YlpStore.Backups(path).Select(b => YlpStore.Load(b).Files[YlpArchive.NativeName]), Has.Member(Doc(1)), "the version that was just replaced is kept");
        }

        [Test] public void ABackupLimitBelowMinusOneIsRefusedBeforeTouchingDisk()
        {
            Assert.That(() => YlpStore.Save(path, Version(1), null, false, -2), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(File.Exists(path), Is.False);
        }

        // ───────────── 途中で落ちる ─────────────

        [TestCase("built")]
        [TestCase("temp-written")]
        [TestCase("verified")]
        [TestCase("before-replace")]
        public void AFailureBeforeReplacingLeavesTheOriginalByteIdentical(string point)
        {
            var first = YlpStore.Save(path, Version(1), null, false);
            byte[] bytes1 = File.ReadAllBytes(path);
            Assert.That(() => YlpStore.Save(path, Version(2), first.Token, false, YlpStore.KeepAllBackups, p => { if (p == point) throw new IOException("injected at " + p); }),
                Throws.TypeOf<IOException>().With.Message.Contains("injected"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes1));
            Assert.That(YlpStore.Load(path).Files[YlpArchive.NativeName], Is.EqualTo(Doc(1)));
            Assert.That(Leftovers(), Is.Empty, "no .saving-*~ and no .lock~ left behind");
            Assert.That(YlpStore.Backups(path), Is.Empty);
            Assert.That(YlpStore.HasExternalChange(path, first.Token), Is.False, "the window's token stays valid");
            Assert.That(YlpStore.Save(path, Version(2), first.Token, false).Backup, Is.Not.Null, "and the next save goes through");
        }

        [TestCase("built")]
        [TestCase("temp-written")]
        [TestCase("verified")]
        [TestCase("before-replace")]
        public void AFailureBeforeCreatingANewFileLeavesNothing(string point)
        {
            Assert.That(() => YlpStore.Save(path, Version(1), null, false, YlpStore.KeepAllBackups, p => { if (p == point) throw new IOException("injected"); }), Throws.TypeOf<IOException>());
            Assert.That(File.Exists(path), Is.False);
            Assert.That(Leftovers(), Is.Empty);
        }

        [Test] public void AFailureAfterReplacingStillCommitsTheNewFile()
        {
            var first = YlpStore.Save(path, Version(1), null, false);
            byte[] bytes1 = File.ReadAllBytes(path);
            Assert.That(() => YlpStore.Save(path, Version(2), first.Token, false, YlpStore.KeepAllBackups, p => { if (p == "after-replace") throw new IOException("lost ack"); }), Throws.TypeOf<IOException>());
            Assert.That(YlpStore.Load(path).Files[YlpArchive.NativeName], Is.EqualTo(Doc(2)));
            Assert.That(File.ReadAllBytes(YlpStore.Backups(path).Single()), Is.EqualTo(bytes1));
            Assert.That(Leftovers(), Is.Empty);
        }

        [TestCase("temp-written")]
        [TestCase("verified")]
        public void AnOutsideChangeDuringTheSaveIsCaughtBeforeReplacing(string point)
        {
            var first = YlpStore.Save(path, Version(1), null, false);
            byte[] outside = YlpArchive.Write(Version(77));
            Assert.That(() => YlpStore.Save(path, Version(2), first.Token, false, YlpStore.KeepAllBackups, p => { if (p == point) File.WriteAllBytes(path, outside); }),
                Throws.TypeOf<IOException>().With.Message.Contains("changed outside"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(outside), "the outside writer's file is kept");
            Assert.That(Leftovers(), Is.Empty);
            Assert.That(YlpStore.Backups(path), Is.Empty);
        }

        [Test] public void AHeldLockMakesSaveFailWithoutTouchingTheTarget()
        {
            var first = YlpStore.Save(path, Version(1), null, false);
            byte[] bytes1 = File.ReadAllBytes(path);
            using (new FileStream(path + ".lock~", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That(() => YlpStore.Save(path, Version(2), first.Token, false), Throws.InstanceOf<IOException>());
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes1));
                Assert.That(Directory.GetFiles(root).Where(f => f.Contains(".saving-")), Is.Empty);
            }
            File.Delete(path + ".lock~");
            Assert.That(YlpStore.Save(path, Version(2), first.Token, false).Backup, Is.Not.Null);
        }

        [Test] public void AStaleLockFileFromACrashDoesNotBlockSavingAndIsRemoved()
        {
            var first = YlpStore.Save(path, Version(1), null, false);
            File.WriteAllText(path + ".lock~", "");
            YlpStore.Save(path, Version(2), first.Token, false);
            Assert.That(File.Exists(path + ".lock~"), Is.False);
            Assert.That(YlpStore.Load(path).Files[YlpArchive.NativeName], Is.EqualTo(Doc(2)));
        }

        [Test] public void ABackupFolderBlockedByAFileFailsWithoutTouchingTheTarget()
        {
            var first = YlpStore.Save(path, Version(1), null, false);
            byte[] bytes1 = File.ReadAllBytes(path);
            File.WriteAllText(YlpStore.BackupFolder(path), "a file where the folder should be");
            Assert.That(() => YlpStore.Save(path, Version(2), first.Token, false), Throws.InstanceOf<IOException>());
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes1));
            Assert.That(Directory.GetFiles(root).Where(f => f.Contains(".saving-") || f.EndsWith(".lock~")), Is.Empty);
        }

        [Test] public void InvalidContentIsRefusedBeforeTouchingDisk()
        {
            string nested = Path.Combine(root, "never", "doc.ylp");
            Assert.That(() => YlpStore.Save(nested, new Dictionary<string, byte[]> { { "other.bin", new byte[1] } }, null, false), Throws.ArgumentException);
            Assert.That(() => YlpStore.Save(nested, new Dictionary<string, byte[]> { { YlpArchive.NativeName, new byte[1] }, { "../x", new byte[1] } }, null, false), Throws.TypeOf<InvalidDataException>());
            Assert.That(Directory.Exists(Path.Combine(root, "never")), Is.False);
        }

        [Test] public void APathThatDoesNotEndInYlpIsRefused()
        {
            foreach (string bad in new[] { null, "", Path.Combine(root, "sub", "doc.txt"), Path.Combine(root, "sub", "doc.ylp.bak"), Path.Combine(root, "sub", "doc"), Path.Combine(root, "sub", "doc.ylp~") })
                Assert.That(() => YlpStore.Save(bad, Version(1), null, false), Throws.ArgumentException, bad ?? "null");
            Assert.That(Directory.Exists(Path.Combine(root, "sub")), Is.False);
        }

        // ───────────── 外部改変の検出（窓の定期確認） ─────────────

        [Test] public void HasExternalChangeReportsRealChangesOnly()
        {
            var saved = YlpStore.Save(path, Version(1), null, false);
            Assert.That(YlpStore.HasExternalChange(path, saved.Token), Is.False, "unchanged");

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            Assert.That(YlpStore.HasExternalChange(path, saved.Token), Is.False, "touched, same content");

            byte[] bytes = File.ReadAllBytes(path); bytes[bytes.Length / 2] ^= 1;
            File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(10));
            Assert.That(YlpStore.HasExternalChange(path, saved.Token), Is.True, "same length, different content, new time");

            File.WriteAllBytes(path, YlpArchive.Write(Version(2)));
            Assert.That(YlpStore.HasExternalChange(path, saved.Token), Is.True, "different content");

            File.Delete(path);
            Assert.That(YlpStore.HasExternalChange(path, saved.Token), Is.True, "deleted");
        }

        [Test] public void HasExternalChangeTreatsAMalformedTokenAsChanged()
        {
            YlpStore.Save(path, Version(1), null, false);
            foreach (string token in new[] { null, "", "garbage", "a:b:c", new string('a', 64) + ":x:1", new string('a', 64) + ":-1:1", new string('a', 64) + ":1:2:3" })
                Assert.That(YlpStore.HasExternalChange(path, token), Is.True, token ?? "null");
        }

        /// <summary>意図したトレードオフ: 窓の定期確認は長さと更新時刻が同じなら中身を読まないので、長さを変えずに書き換えて
        /// 更新時刻を戻されると気づかない（保存の直前は必ずハッシュまで見るので、上書きはされない）。</summary>
        [Test] public void HasExternalChangeMissesSameLengthRewriteWithRestoredTime_DeliberateTradeOff()
        {
            YlpStore.Save(path, Version(1), null, false);
            // Mono の SetLastWriteTimeUtc はマイクロ秒に丸める（読みは 100ns）ので、戻せる時刻（秒ちょうど）にしてから印を取る
            var time = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, time);
            var opened = YlpStore.Load(path);
            byte[] bytes = File.ReadAllBytes(path); bytes[bytes.Length / 2] ^= 1;
            File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, time);
            Assert.That(YlpStore.HasExternalChange(path, opened.Token), Is.False, "fast path: not detected");
            Assert.That(() => YlpStore.Save(path, Version(2), opened.Token, false), Throws.TypeOf<IOException>(), "but saving over it is refused");
        }

        // ───────────── 読み込み ─────────────

        [Test] public void LoadRefusesCorruptAndOversizedFiles()
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("PK not really"));
            Assert.That(() => YlpStore.Load(path), Throws.TypeOf<InvalidDataException>());
            // 疎なファイルで上限（768 MiB + 64 MiB）を 1 バイト超える。読む前に長さで断る。
            using (var s = new FileStream(path, FileMode.Create)) s.SetLength(YlpArchive.MaxTotalBytes + 64L * 1024 * 1024 + 1);
            Assert.That(() => YlpStore.Load(path), Throws.TypeOf<InvalidDataException>().With.Message.Contains("budget"));
            Assert.That(() => YlpStore.Load(Path.Combine(root, "missing.ylp")), Throws.InstanceOf<IOException>());
        }

        // ───────────── 補助 ─────────────

        static byte[] Doc(int version) => Encoding.ASCII.GetBytes("native document version " + version);

        static Dictionary<string, byte[]> Version(int version) => new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            { YlpArchive.NativeName, Doc(version) },
            { "composite/Color.png", YlpArchiveTests.Noise(200, version) },
        };

        /// <summary>保存が残してはいけないもの（一時ファイルとロック）。</summary>
        IEnumerable<string> Leftovers() => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Contains(".saving-") || f.EndsWith(".lock~", StringComparison.Ordinal)).Select(Path.GetFileName);
    }
}
