using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Shelf;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class AssetRestTests
    {
        static readonly YlpWriterInfo Writer = new YlpWriterInfo("YoluPainter", "0.0.0-test", "2022.3.22f1");
        static byte[] Brush() => BrushResourceFile.Write(new Dictionary<string, byte[]> { { BrushResourceFile.StateName, Encoding.UTF8.GetBytes("{\"schema\":3}") }, { "tip-0.png", RgbaPng.Encode(new byte[] { 17,17,17,255 }, 1, 1) } });

        [Test] public void EveryResourceKindKeepsItsIdTypeSourceAndBytes()
        {
            var resources = new ProjectResources();
            var origin = ResourceOrigin.UnityAsset(new string('a', 32), "Assets/Example.asset", "stamp", false, 4123456789L);
            resources.Add("image", ImageContent.Adopt(new byte[] { 30,20,10,0 }, 1, 1), origin, ResourceColorSpace.Linear, out _);
            resources.AddBrush("brush", Brush(), ResourceOrigin.Library("Ink/Fine.ylbrush", new string('b', 64), 42), out _);
            var material = BuiltInSmartMaterials.Make("rusty-metal"); var file = SmartMaterialFile.Write(material, Writer);
            resources.AddSmart("smart", file, material, ResourceOrigin.None, out _);
            resources.AddSmart("material", file, material, origin, out _, resourceKind: ResourceKind.Material);
            var mask = BuiltInSmartMaterials.Make("edges"); resources.AddSmart("mask", SmartMaterialFile.Write(mask, Writer), mask, null, out _);
            Assert.That(resources.Count, Is.EqualTo(5));
            var files = new Dictionary<string, byte[]>(); ResourceIndex.AddTo(files, resources);
            var entries = ResourceIndex.Read(files[ResourceIndex.EntryName]);
            Assert.That(entries.Select(e => e.Kind), Is.EquivalentTo(Enum.GetValues(typeof(ResourceKind)).Cast<ResourceKind>()));
            var loaded = ResourceIndex.Load(files, entries); var again = new Dictionary<string, byte[]>(); ResourceIndex.AddTo(again, loaded);
            Assert.That(again.Keys, Is.EquivalentTo(files.Keys)); foreach (var pair in files) Assert.That(again[pair.Key], Is.EqualTo(pair.Value), pair.Key);
            Assert.That(loaded.Images[0].Origin.LocalFileId, Is.EqualTo(4123456789L));
            Assert.That(loaded.Brushes[0].Origin.Path, Is.EqualTo("Ink/Fine.ylbrush"));
        }
        [TestCase("../a.png")][TestCase("a/../b.png")][TestCase("/a.png")][TestCase("a\\b.png")][TestCase("a//b.png")][TestCase("C:/a.png")]
        public void AResourceNeverReferencesAnUnsafeLibraryPath(string path)
            => Assert.That(() => ResourceOrigin.Library(path, new string('a',64), 1), Throws.ArgumentException);

        [Test] public void OldUnityReferencesReadAsMainAssetsAndMalformedIdsAreRefused()
        {
            var r = new ProjectResources(); r.Add("image", ImageContent.Adopt(new byte[4],1,1), ResourceOrigin.UnityAsset(new string('a',32), "Assets/Example.png", "", false), ResourceColorSpace.Srgb, out _);
            var files = new Dictionary<string, byte[]>(); ResourceIndex.AddTo(files,r);
            var json = Encoding.UTF8.GetString(files[ResourceIndex.EntryName]).Replace(", \"localFileID\": 0", "");
            Assert.That(ResourceIndex.Read(Encoding.UTF8.GetBytes(json))[0].Origin.LocalFileId, Is.Zero);
            Assert.That(() => ResourceIndex.Read(Encoding.UTF8.GetBytes(json.Replace("\"guid\":", "\"localFileID\": \"bad\", \"guid\":"))), Throws.TypeOf<InvalidDataException>());
        }
        [Test] public void ArchiveBudgetRefusesImagesBrushesMaterialsAndReplacementAtomically()
        {
            var r = new ProjectResources { BudgetBytes = long.MaxValue, ArchiveBudgetBytes = 0 }; var image = ImageContent.Adopt(new byte[4],1,1);
            Assert.That(() => r.Add("image",image,null,ResourceColorSpace.Linear,out _), Throws.TypeOf<ResourceRefusedException>().With.Message.Contains("archive budget"));
            Assert.That(() => r.AddBrush("brush",Brush(),null,out _), Throws.TypeOf<ResourceRefusedException>());
            var m = BuiltInSmartMaterials.Make("rusty-metal"); var file = SmartMaterialFile.Write(m,Writer);
            Assert.That(() => r.AddSmart("material",file,m,null,out _,resourceKind:ResourceKind.Material), Throws.TypeOf<ResourceRefusedException>());
            Assert.That((r.Count,r.UsedBytes,r.Revision), Is.EqualTo((0,0L,0L)));
            r.ArchiveBudgetBytes = image.EncodePng().LongLength; var held = r.Add("image",image,null,ResourceColorSpace.Linear,out _);
            r.ArchiveBudgetBytes = 0;
            Assert.That(r.Add("same",image,null,ResourceColorSpace.Linear,out _), Is.SameAs(held), "deduplication adds no archive bytes");
            var before = r.Revision;
            Assert.That(() => r.ReplaceContent(held.Id,ImageContent.Adopt(new byte[16],2,2),null), Throws.TypeOf<ResourceRefusedException>());
            Assert.That((held.ContentHash,r.Revision), Is.EqualTo((image.Hash,before)));
        }
        [Test] public void CountingTheNativeArchiveUsesTheSameWriter()
        {
            var d = new PaintDocument(128,64,64); var l = d.AddLayer("paint"); d.Fill(l.Id,PaintChannel.Color,new Rgba32(20,40,80,255));
            d.AddFillLayer("fill",new Dictionary<PaintChannel,Rgba32>{{PaintChannel.Roughness,new Rgba32(50,50,50,255)}});
            Assert.That(DocumentBinary.Measure(d), Is.EqualTo(DocumentBinary.Write(d).LongLength));
            var withoutIds = DocumentBinary.Measure(d);
            d.SetIdColors(IdColorAssignments.Empty.WithColor(new string('a',64),0,0x123456));
            Assert.That(DocumentBinary.Measure(d), Is.GreaterThan(withoutIds));
            Assert.That(DocumentBinary.Measure(d), Is.EqualTo(DocumentBinary.Write(d).LongLength), "manual ID suffix counts toward the archive budget");
        }
        [Test] public void ABrushRejectsUnknownSchemaEntriesAndOversizedImages()
        {
            Assert.That(() => BrushResourceFile.Write(new Dictionary<string,byte[]>{{"state.json",Encoding.UTF8.GetBytes("{\"schema\":99}")}}), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => BrushResourceFile.Write(new Dictionary<string,byte[]>{{"state.json",Encoding.UTF8.GetBytes("{\"schema\":3}")},{"external.txt",new byte[1]}}), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => BrushResourceFile.Write(new Dictionary<string,byte[]>{{"state.json",Encoding.UTF8.GetBytes("{\"schema\":3}")},{"tip-0.png",RgbaPng.Encode(new byte[(BrushTip.MaxSize+1)*4],BrushTip.MaxSize+1,1)}}), Throws.TypeOf<InvalidDataException>());
            var mask = BuiltInSmartMaterials.Make("edges");
            Assert.That(() => new ProjectResources().AddSmart("wrong",SmartMaterialFile.Write(mask,Writer),mask,null,out _,resourceKind:ResourceKind.Material), Throws.ArgumentException);
        }

    }
}
