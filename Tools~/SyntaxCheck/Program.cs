using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
class Program
{
    static int Main(string[] args)
    {
        int count=0, errors=0;
        foreach(var file in (args.Length>0?args:new[]{"."}).SelectMany(dir=>Directory.GetFiles(dir, "*.cs",SearchOption.AllDirectories)))
        {
            count++;var tree=CSharpSyntaxTree.ParseText(File.ReadAllText(file),new CSharpParseOptions(LanguageVersion.CSharp8),file);
            foreach(var error in tree.GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error)){errors++;Console.WriteLine(error);}
        }
        Console.WriteLine($"C#8 syntax: {count} source files, {errors} errors. This does NOT resolve Unity APIs or compile shaders.");return errors==0?0:1;
    }
}
