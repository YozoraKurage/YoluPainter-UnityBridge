// Unity 同梱 NUnit は Remoting API を使うため、同梱 Mono の独立プロセスで回す。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NUnit.Framework.Api;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

internal static class NUnitRunner
{
    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) { Console.Error.WriteLine("Core 試験の結果なし: " + error); return 3; }
    }

    static int Run(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        var regex = args[0].Length == 0 ? null : new Regex(args[0], RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        bool list = args[1] == "list";
        var runner = new NUnitTestAssemblyRunner(new DefaultTestAssemblyBuilder());
        var assembly = Assembly.LoadFrom("Yozolab.YoluPainter.Tests.dll");
        // 試験どうしは直列。試験内部で Core が起こす並列処理はそのまま動く。
        if (runner.Load(assembly, new Dictionary<string, object> { ["NumberOfTestWorkers"] = 0 }) == null)
            throw new InvalidOperationException("NUnit が試験を読み込めませんでした。");
        var filter = regex == null ? TestFilter.Empty : new NameFilter(regex);
        var discovery = XElement.Parse(runner.LoadedTest.ToXml(true).OuterXml);
        discovery.Save("discovery.xml");
        // 発見時の例外でクラス全体が消えても、残りだけで成功とは報告しない。
        var invalid = discovery.DescendantsAndSelf().Where(e => (string)e.Attribute("runstate") == "NotRunnable").ToArray();
        if (invalid.Length > 0)
            throw new InvalidOperationException("NUnit の試験発見に失敗:\n" + string.Join("\n", invalid.Select(e =>
                e.Attribute("fullname")?.Value + ": " + e.Element("properties")?.ToString())));
        var cases = discovery.Descendants("test-case").ToArray();
        var classes = cases.GroupBy(c => (string)c.Attribute("classname") ?? "不明").OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
        int selected = runner.CountTestCases(filter);
        Console.WriteLine($"クラス {classes.Length}・発見 {cases.Length} 件・選択 {selected} 件");
        Console.WriteLine("同梱 Mono の独立プロセス・Burst なし。Unity の統合試験を置き換えません。");
        new XElement("selection", new XAttribute("discovered", cases.Length), new XAttribute("selected", selected)).Save("selection.xml");
        if (list)
        {
            Console.WriteLine("\nクラス（パラメーター展開後の発見件数）:");
            foreach (var entry in classes) Console.WriteLine($"  {entry.Key}: {entry.Count()}");
            Console.WriteLine("\n絞り込みで選ばれた試験:");
            foreach (var test in Leaves(runner.LoadedTest).Where(t => filter.Pass(t)))
                Console.WriteLine($"  {test.FullName} [{test.RunState}]");
            return selected > 0 ? 0 : 3;
        }
        if (selected == 0) throw new InvalidOperationException("絞り込みに一致する試験が 0 件です。");
        var clock = Stopwatch.StartNew();
        ITestResult result;
        using (var journal = new JournalListener()) result = runner.Run(journal, filter);
        double runSeconds = clock.Elapsed.TotalSeconds;
        var suite = XElement.Parse(result.ToXml(true).OuterXml);
        var failures = suite.DescendantsAndSelf().Where(e => e.Element("failure") != null &&
            (string)e.Attribute("site") != "Child").ToArray();
        int suiteFailures = failures.Count(e => e.Name == "test-suite");
        int total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
        var xml = new XElement("test-run", new XAttribute("total", total), new XAttribute("passed", result.PassCount),
            new XAttribute("failed", result.FailCount), new XAttribute("skipped", result.SkipCount),
            new XAttribute("inconclusive", result.InconclusiveCount), new XAttribute("duration", runSeconds),
            new XAttribute("result", result.ResultState.Status), new XAttribute("suite-failures", suiteFailures), suite);
        xml.Save("result.xml");
        var parts = new List<string> { $"{total} 件", $"成功 {result.PassCount}", $"失敗 {result.FailCount}" };
        if (result.SkipCount > 0) parts.Add($"スキップ {result.SkipCount}");
        if (result.InconclusiveCount > 0) parts.Add($"不確定 {result.InconclusiveCount}");
        if (suiteFailures > 0) parts.Add($"スイート失敗 {suiteFailures}");
        Console.WriteLine($"EditMode テスト: {string.Join(" / ", parts)}  ({runSeconds:F1}s)");
        foreach (var failure in failures)
            Console.WriteLine($"\nFAILED  {failure.Attribute("fullname")?.Value}\n{failure.Element("failure")?.Element("message")?.Value}\n{failure.Element("failure")?.Element("stack-trace")?.Value}");
        foreach (var skipped in suite.DescendantsAndSelf().Where(e => e.Element("reason") != null))
            Console.WriteLine($"SKIPPED {skipped.Attribute("fullname")?.Value}: {skipped.Element("reason")?.Element("message")?.Value}");
        if (total == 0) return 3;
        return result.ResultState.Status == TestStatus.Failed || result.FailCount > 0 ? 1 : 0;
    }

    static IEnumerable<ITest> Leaves(ITest test)
    {
        if (!test.IsSuite) { yield return test; yield break; }
        foreach (var child in test.Tests) foreach (var leaf in Leaves(child)) yield return leaf;
    }

    sealed class NameFilter : TestFilter
    {
        readonly Regex regex;
        internal NameFilter(Regex regex) { this.regex = regex; }
        public override bool Match(ITest test) => !test.IsSuite && regex.IsMatch(test.FullName);
        public override TNode AddToXml(TNode parentNode, bool recursive) => parentNode.AddElement("filter", regex.ToString());
    }

    sealed class JournalListener : ITestListener, IDisposable
    {
        readonly StreamWriter writer = new StreamWriter("execution.tsv") { AutoFlush = true };
        public void TestStarted(ITest test)
        {
            if (!test.IsSuite) lock (writer) writer.WriteLine("開始\t" + test.FullName);
        }
        public void TestFinished(ITestResult result)
        {
            if (!result.Test.IsSuite) lock (writer) writer.WriteLine($"終了\t{result.Test.FullName}\t{result.ResultState}\t{result.Duration:F6}");
        }
        public void TestOutput(TestOutput output) { }
        public void Dispose() { writer.Dispose(); }
    }
}
