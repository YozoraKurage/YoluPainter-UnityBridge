using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>ネイティブメニューを使っていた全ファイルの登録式と順序を比較する。新しい項目の追加は許す。</summary>
    public sealed class MenuRegistrationTests
    {
        [Serializable] sealed class Baseline { public List<FileEntries> files; }
        [Serializable] sealed class FileEntries { public string path; public List<string> calls; }
        static string Root => PackageInfo.FindForAssembly(typeof(TexturePaintWindow).Assembly).resolvedPath;
        static List<string> Registrations(string source)
        {
            var result = new List<string>();
            foreach (Match match in Regex.Matches(source, @"(?:(?:\b\w+\.)?(?:AddItem|AddRadioItem|AddHeading|AddDisabledItem|AddSeparator)|\bItem)\s*\("))
            {
                int i = match.Index + match.Length, depth = 1; char quote = '\0';
                while (i < source.Length && depth > 0)
                {
                    char c = source[i];
                    if (quote != '\0') { if (c == '\\') { i += 2; continue; } if (c == quote) quote = '\0'; }
                    else if (c == '"' || c == '\'') quote = c;
                    else if (c == '(') depth++; else if (c == ')') depth--;
                    i++;
                }
                result.Add(Regex.Replace(source.Substring(match.Index, i - match.Index), @"\s+", " "));
            }
            return result;
        }
        [Test] public void EveryFormerMenuKeepsItsRegisteredItemsInTheSameOrder()
        {
            var baseline = JsonUtility.FromJson<Baseline>(File.ReadAllText(Path.Combine(Root, "Tests/Editor/UI/MenuRegistrations.json")));
            foreach (var file in baseline.files)
            {
                string source = File.ReadAllText(Path.Combine(Root, file.path));
                Assert.That(source, Does.Not.Match(@"\b(?:new|typeof)\s*\(?\s*(?:UnityEditor[.])?GenericMenu\b"), file.path);
                var actual = Registrations(source); int next = 0;
                foreach (string expected in file.calls)
                {
                    while (next < actual.Count && actual[next] != expected) next++;
                    Assert.That(next, Is.LessThan(actual.Count), file.path + ": " + expected); next++;
                }
            }
            TestContext.WriteLine("置換前の登録式: " + baseline.files.Count + " ファイルを比較（実際の有効状態とコールバックは WindowTests / PainterPluginTests）。");
        }
    }
}
