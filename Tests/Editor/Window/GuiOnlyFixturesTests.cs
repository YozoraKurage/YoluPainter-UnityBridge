using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>run-tests.sh --both は GUI の台で Tests/Editor/Support/GuiOnlyFixtures.txt のクラスだけを回す。batch-gl で飛ばす（GUI でしか
    /// 回らない）テストのクラスが一覧から漏れると、そのテストはどの台でも回らなくなるので、ソースから探して一覧と比べる。</summary>
    public sealed class GuiOnlyFixturesTests
    {
        [Test] public void EveryTestThatSkipsInBatchModeIsListedForTheGuiRunner()
        {
            string tests = PackagePaths.Physical("Tests/Editor");
            var listed = File.ReadAllLines(Path.Combine(tests, "Support", "GuiOnlyFixtures.txt"))
                .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToHashSet();
            // 「batch-gl なら飛ばす」: Application.isBatchMode が真のとき Assert.Ignore（! の付いた逆の条件は数えない）
            var guard = new Regex(@"(?<!!)Application\.isBatchMode\s*\)\s*Assert\.Ignore");
            var missing = Directory.GetFiles(tests, "*.cs", SearchOption.AllDirectories)
                .Where(f => guard.IsMatch(File.ReadAllText(f)))
                .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\bclass\s+(\w+Tests)\b").Cast<Match>().Select(m => m.Groups[1].Value))
                .Distinct().Where(c => !listed.Contains(c)).ToList();
            Assert.That(missing, Is.Empty, "classes that skip in batch mode but are not in GuiOnlyFixtures.txt (they would run on no runner with --both)");
            Assert.That(listed, Does.Contain("WindowTests"));
        }

        /// <summary>担当のふだんの全件は Tests/Editor/Support/SlowTests.txt の重い試験を飛ばす（統合と --full では回す）。一覧の行が今ある
        /// [Test]・[TestCase]・[UnityTest] のメソッドを指していること（名前を変えた試験が一覧に残って、黙って重いまま回り続けないように）。</summary>
        [Test] public void EverySlowTestEntryNamesAnExistingTest()
        {
            string tests = PackagePaths.Physical("Tests/Editor");
            var entries = File.ReadAllLines(Path.Combine(tests, "Support", "SlowTests.txt"))
                .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
            Assert.That(entries, Is.Not.Empty);
            var assembly = typeof(GuiOnlyFixturesTests).Assembly;
            foreach (var entry in entries)
            {
                Assert.That(entry, Does.Match(@"^\w+\.\w+$"), "one Class.Method per line");
                var parts = entry.Split('.');
                var type = assembly.GetType("Yozolab.YoluPainter.Tests." + parts[0]);
                Assert.That(type, Is.Not.Null, entry + ": no such test class");
                var method = type.GetMethods().FirstOrDefault(m => m.Name == parts[1]);
                Assert.That(method, Is.Not.Null, entry + ": no such method");
                Assert.That(method.GetCustomAttributes(true).Any(a => a is TestAttribute || a is TestCaseAttribute || a is TestCaseSourceAttribute || a is UnityEngine.TestTools.UnityTestAttribute), Is.True, entry + ": not a test");
            }
        }
    }
}
