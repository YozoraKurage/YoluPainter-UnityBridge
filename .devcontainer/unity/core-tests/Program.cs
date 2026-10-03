// 開発用の実行器。パッケージと試験のソースを変えず、Unity の参照を渡さずに組む。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class Program
{
    static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.CSharp9,
        preprocessorSymbols: new[] { "UNITY_EDITOR", "UNITY_EDITOR_LINUX", "UNITY_EDITOR_64", "UNITY_2022_3",
            "UNITY_2022_3_OR_NEWER", "UNITY_INCLUDE_TESTS", "DEBUG", "TRACE", "UNITY_ASSERTIONS" });
    static readonly CSharpCompilationOptions CompileOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Debug, allowUnsafe: false);
    static string source, output;

    static int Main(string[] args)
    {
        try { return Execute(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine("Core 試験の結果なし: " + error);
            return 3;
        }
    }

    static SyntaxTree[] ReadTrees(string relative) => Directory.GetFiles(Path.Combine(source, relative), "*.cs", SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.Ordinal)
        .Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), ParseOptions, p, Encoding.UTF8)).ToArray();
    static string Relative(string path) => Path.GetRelativePath(source, path);
    static string Describe(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return diagnostic.Id + ": " + diagnostic.GetMessage(CultureInfo.InvariantCulture) +
            (diagnostic.Location.IsInSource ? $"（{Relative(span.Path)}:{span.StartLinePosition.Line + 1}）" : "");
    }
    static void Json(string file, object value) => File.WriteAllText(Path.Combine(output, file),
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    static Diagnostic[] Errors(CSharpCompilation compilation) => compilation.GetDiagnostics()
        .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    static string Emit(CSharpCompilation compilation)
    {
        var path = Path.Combine(output, compilation.AssemblyName + ".dll");
        var result = compilation.Emit(path);
        if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error).Select(Describe)));
        return path;
    }

    static int Execute(string[] args)
    {
        source = Path.GetFullPath(args[0]); output = Path.GetFullPath(args[1]);
        var clock = Stopwatch.StartNew();
        var references = Directory.GetFiles(args[2], "*.dll", SearchOption.AllDirectories).Select(p => MetadataReference.CreateFromFile(p)).ToList();
        var coreTrees = ReadTrees("Runtime/Core");
        if (coreTrees.Length == 0) throw new InvalidOperationException("Core のソースが 0 件です。");
        var core = CSharpCompilation.Create("Yozolab.YoluPainter.Core", coreTrees, references, CompileOptions);
        string corePath = Emit(core);
        references.Add(MetadataReference.CreateFromFile(corePath));
        references.Add(MetadataReference.CreateFromFile(Path.Combine(output, "nunit.framework.dll")));
        var allTrees = ReadTrees("Tests/Editor");
        var tests = CSharpCompilation.Create("Yozolab.YoluPainter.Tests", allTrees, references, CompileOptions);
        // 部分クラスのセットアップだけが落ち、残りの試験が条件を失ったまま通るのを防ぐ。
        var partials = allTrees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(type => type.Modifiers.Any(SyntaxKind.PartialKeyword))
            .Select(type => new { tree, name = tests.GetSemanticModel(tree).GetDeclaredSymbol(type)?.ToDisplayString() }))
            .Where(part => part.name != null).GroupBy(part => part.name)
            .Select(group => group.Select(part => part.tree).Distinct().ToArray()).ToArray();
        var excluded = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        int rounds = 0;
        while (true)
        {
            var errors = Errors(tests);
            if (errors.Length == 0) break;
            var globalErrors = errors.Where(e => !e.Location.IsInSource).ToArray();
            if (globalErrors.Length != 0) throw new InvalidOperationException(string.Join("\n", globalErrors.Select(Describe)));
            var bad = errors.GroupBy(d => d.Location.SourceTree).ToArray();
            foreach (var group in bad) excluded.Add(Relative(group.Key.FilePath), group.Select(Describe).Distinct().ToArray());
            var removed = new HashSet<SyntaxTree>(bad.Select(g => g.Key));
            bool expanded;
            do
            {
                expanded = false;
                foreach (var group in partials.Where(group => group.Any(removed.Contains)))
                {
                    var cause = group.First(removed.Contains);
                    foreach (var tree in group.Where(tree => tests.SyntaxTrees.Contains(tree)))
                        if (removed.Add(tree))
                        {
                            excluded.Add(Relative(tree.FilePath), new[] { "同じ部分クラスのファイルを除外したため: " + Relative(cause.FilePath) });
                            expanded = true;
                        }
                }
            } while (expanded);
            tests = tests.RemoveSyntaxTrees(removed);
            rounds++;
        }
        Emit(tests);
        var runnerSource = CSharpSyntaxTree.ParseText(File.ReadAllText(args[3]), ParseOptions, args[3], Encoding.UTF8);
        Emit(CSharpCompilation.Create("NUnitRunner", new[] { runnerSource }, references,
            CompileOptions.WithOutputKind(OutputKind.ConsoleApplication)));
        double buildSeconds = clock.Elapsed.TotalSeconds;
        var included = tests.SyntaxTrees.Select(t => Relative(t.FilePath)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var managedDifferences = allTrees.Where(t => Regex.IsMatch(t.GetText().ToString(),
            @"\b(KernelScope|KernelChoice|CompositeKernels|Burst)\b"))
            .Select(t => Relative(t.FilePath)).ToArray();
        Json("inventory.json", new { source, coreFiles = coreTrees.Length, candidateFiles = allTrees.Length,
            included, excluded, removalRounds = rounds, managedDifferences, buildSeconds,
            defines = ParseOptions.PreprocessorSymbolNames.ToArray() });
        return 0;
    }
}
