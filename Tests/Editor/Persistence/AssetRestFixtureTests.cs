using System.IO;
using System.Linq;
using NUnit.Framework;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class AssetRestFixtureTests
    {
        static readonly YlpWriterInfo Writer = new YlpWriterInfo("YoluPainter", "0.0.0-test", "2022.3.22f1");
        [Test] public void FormatFiveFixtureUpgradesWithoutChangingSourceEntries()
        {
            var bytes = File.ReadAllBytes(PackagePaths.Physical("Tests/Editor/Persistence/Fixtures~/format5.ylp"));
            var original = YlpArchive.Read(bytes); var opened = YlpFormat.Open(original);
            Assert.That(opened.Info.Format, Is.EqualTo(5)); Assert.That(opened.Upgraded, Is.True);
            var resources = ResourceIndex.Load(opened.Files,opened.Resources);
            Assert.That(resources.Smart.Count, Is.EqualTo(2)); Assert.That(resources.Images.Count, Is.EqualTo(1));
            ResourceIndex.AddTo(opened.Files,resources); YlpFormat.Stamp(opened.Files,Writer,opened.Info.CreatedBy);
            var again = YlpArchive.Read(YlpArchive.Write(opened.Files));
            Assert.That(YlpFormat.ReadInfo(again[YlpFormat.InfoName]).Format, Is.EqualTo(YlpFormat.Current));
            // project.json は 6 → 7 でマテリアルの鍵の形になる（セットの並びと名前は同じ）
            foreach (var pair in original.Where(p=>p.Key!=YlpFormat.InfoName&&p.Key!=YlpFormat.ProjectName)) Assert.That(again[pair.Key], Is.EqualTo(pair.Value), pair.Key);
            Assert.That(YlpFormat.ReadProject(again[YlpFormat.ProjectName]).Sets.Select(s=>s.Name), Is.EqualTo(YlpFormat.ReadProjectOfAnyFormat(original[YlpFormat.ProjectName]).Sets.Select(s=>s.Name)));
        }
    }
}
