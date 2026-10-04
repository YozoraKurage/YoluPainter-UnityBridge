using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.Persistence;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    public sealed class AssetRestWindowTests
    {
        string project, folder; TexturePaintWindow window; TestDialogs dialogs;
        sealed class TestDialogs : IPainterDialogs
        {
            public bool Answer; public string File = "";
            public string SaveFolder(string t,string f,string n)=>""; public string OpenFolder(string t,string f)=>"";
            public string OpenFile(string t,string f,string e)=>File; public string SaveFile(string t,string f,string n,string e)=>File;
            public bool Confirm(string t,string m,string o,string c)=>Answer; public void Inform(string t,string m){} public void Progress(string t,string i,float p){} public void ClearProgress(){}
        }
        [SetUp] public void Create()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            project = Path.Combine(Path.GetTempPath(), "yolupainter-assetrest-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(project); PainterSettings.ProjectRoot=project;
            folder="Assets/YoluPainterAssetRest-"+Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets",Path.GetFileName(folder));
            window=ScriptableObject.CreateInstance<TexturePaintWindow>(); dialogs=new TestDialogs(); window.Dialogs=dialogs;
            window.CreateProject(new NewProjectSettings{Model=null,Resolution=512,Template=ProjectTemplate.Pbr});
        }
        [TearDown] public void Clean()
        {
            if(window!=null){string recovery=window.RecoveryRoot;Object.DestroyImmediate(window);if(Directory.Exists(recovery))Directory.Delete(recovery,true);}
            AssetDatabase.DeleteAsset(folder); PainterSettings.ProjectRoot=null; if(Directory.Exists(project))Directory.Delete(project,true);
        }
        [Test] public void LibraryHierarchyRenameAndRemovalRequireConfirmationAndKeepPlacedPixels()
        {
            string root=PainterSettings.LibraryFolder; Directory.CreateDirectory(Path.Combine(root,"Nested"));
            var bytes=RgbaPng.Encode(new byte[]{9,20,30,0},1,1); File.WriteAllBytes(Path.Combine(root,"Nested","Image.png"),bytes);
            var smart=BuiltInSmartMaterials.Make("rusty-metal"); var file=SmartMaterialFile.Write(smart,YlpContent.Writer); File.WriteAllBytes(Path.Combine(root,"Nested","Metal.ylsmart"),file);
            Assert.That(ResourceLibraryFolder.List(root).Single().FileName,Is.EqualTo("Nested/Image.png")); Assert.That(ResourceLibraryFolder.ListSmart(root).Single().FileName,Is.EqualTo("Nested/Metal.ylsmart"));
            var resource=window.ImportLibraryImage("Nested/Image.png"); var layer=window.PlaceResourceAsLayer(resource.Id); var before=window.Document.Composite(PaintChannel.Color);
            Assert.That(window.RenameLibraryAsset("Nested/Metal.ylsmart","Copper"),Is.False); Assert.That(window.RemoveLibraryAsset("Nested/Image.png"),Is.False);
            dialogs.Answer=true; Assert.That(window.RenameLibraryAsset("Nested/Metal.ylsmart","Copper"),Is.True);
            Assert.That(window.SmartAsset("l:Nested/Copper.ylsmart",out var name),Is.Not.Null);Assert.That(name,Is.EqualTo("Copper"));
            Assert.That(window.RemoveLibraryAsset("Nested/Image.png"),Is.True); Assert.That(window.Document.GetLayer(layer),Is.Not.Null);
            Assert.That(window.Document.Composite(PaintChannel.Color),Is.EqualTo(before));Assert.That(resource.Content.CopyPixels(),Is.EqualTo(new byte[]{9,20,30,0}));
            File.WriteAllBytes(Path.Combine(root,"Nested","Existing.ylsmart"),file);
            Assert.That(()=>ResourceLibraryFolder.Rename(root,"Nested/Copper.ylsmart","Existing"),Throws.TypeOf<IOException>());
            Assert.That(()=>ResourceLibraryFolder.Remove(root,"../escape.png"),Throws.ArgumentException);
        }
        [TestCase(".ylbrush")][TestCase(".ylmaterial")]
        public void LibraryBrushAndMaterialRenameAndRemoveKeepTheDocumentHistory(string extension)
        {
            string root=PainterSettings.LibraryFolder;Directory.CreateDirectory(Path.Combine(root,"Nested"));
            var bytes=extension==".ylbrush"?BrushResourceFile.Write(new Dictionary<string,byte[]>{{BrushResourceFile.StateName,System.Text.Encoding.UTF8.GetBytes("{\"schema\":3}")}})
                :SmartMaterialFile.Write(BuiltInSmartMaterials.Make("rusty-metal"),YlpContent.Writer);
            File.WriteAllBytes(Path.Combine(root,"Nested","Example"+extension),bytes);long revision=window.Document.Revision;int undo=window.Document.UndoCount;
            Assert.That(window.RenameLibraryAsset("Nested/Example"+extension,"Renamed"),Is.False);dialogs.Answer=true;
            Assert.That(window.RenameLibraryAsset("Nested/Example"+extension,"Renamed"),Is.True);
            Assert.That(File.ReadAllBytes(Path.Combine(root,"Nested","Renamed"+extension)),Is.EqualTo(bytes));
            Assert.That((extension==".ylbrush"?ResourceLibraryFolder.ListBrushes(root):ResourceLibraryFolder.ListMaterials(root)).Single().FileName,Is.EqualTo("Nested/Renamed"+extension));
            Assert.That(window.RemoveLibraryAsset("Nested/Renamed"+extension),Is.True);
            Assert.That((window.Document.Revision,window.Document.UndoCount),Is.EqualTo((revision,undo)));
        }
        [Test] public void TwoSubassetTexturesKeepTheirIdentityAcrossRenameSaveAndReload()
        {
            var holder=ScriptableObject.CreateInstance<SmartMaterialImportInfo>();AssetDatabase.CreateAsset(holder,folder+"/Container.asset");
            var a=new Texture2D(1,1,TextureFormat.RGBA32,false,true){name="One"};a.SetPixel(0,0,Color.red);a.Apply();AssetDatabase.AddObjectToAsset(a,holder);
            var b=new Texture2D(1,1,TextureFormat.RGBA32,false,true){name="Two"};b.SetPixel(0,0,Color.blue);b.Apply();AssetDatabase.AddObjectToAsset(b,holder);AssetDatabase.SaveAssets();
            var first=window.ImportUnityTexture(a);var second=window.ImportUnityTexture(b);
            Assert.That(first.Origin.AssetGuid,Is.EqualTo(second.Origin.AssetGuid));Assert.That(first.Origin.LocalFileId,Is.Not.EqualTo(second.Origin.LocalFileId));
            Assert.That(AssetDatabase.MoveAsset(folder+"/Container.asset",folder+"/Renamed.asset"),Is.Empty);
            Assert.That(window.CheckResourceSources(),Is.Empty);
            b.SetPixel(0,0,Color.green);b.Apply();EditorUtility.SetDirty(b);AssetDatabase.SaveAssets();
            Assert.That(window.CheckResourceSources().Select(r=>r.Id),Is.EqualTo(new[]{second.Id}));
            Assert.That(window.UpdateResourceFromSource(second.Id),Is.True);Assert.That(second.Content.GetPixel(0,0),Is.EqualTo(new Rgba32(0,255,0,255)));Assert.That(first.Content.GetPixel(0,0),Is.EqualTo(new Rgba32(255,0,0,255)));
            var files=new Dictionary<string,byte[]>();ResourceIndex.AddTo(files,window.ImageResources);var loaded=ResourceIndex.Load(files,ResourceIndex.Read(files[ResourceIndex.EntryName]));
            var restored=UnityResourceObject.Load<Texture2D>(loaded.Images[1].Origin.AssetGuid,loaded.Images[1].Origin.LocalFileId);
            Assert.That(UnityResourceObject.LocalId(restored),Is.EqualTo(second.Origin.LocalFileId));
            Assert.That(AssetDatabase.GetAssetPath(restored),Is.EqualTo(folder+"/Renamed.asset"));
            Assert.That(restored.GetPixel(0,0),Is.EqualTo(Color.green));
        }
        [Test] public void ShelfBrushCarriesTipTextureDualAndSettingsThroughAProjectFile()
        {
            window.ApplyPreset(BuiltInBrushes.Presets.First(p=>p.CreateSettings().Tip!=null));
            window.Brush.textureId="builtin:grain";window.Brush.textureDepth=.4f;window.Brush.dualEnabled=true;window.Brush.dualTipId="builtin:dots";window.Brush.dualRadius=5;window.Brush.dualMode=(int)DualBrushMode.Darken;
            var before=window.GetBrush();var brush=window.SaveShelfBrush("Portable");
            window.SelectShelfBrush(brush.Id);Assert.That(window.GetBrush().Tip.CopyAlpha(),Is.EqualTo(before.Tip.CopyAlpha()));
            Assert.That(window.GetBrush().Radius,Is.EqualTo(before.Radius));
            Assert.That(window.GetBrush().Texture.CopyAlpha(),Is.EqualTo(before.Texture.CopyAlpha()));Assert.That(window.GetBrush().Dual.Tip.CopyAlpha(),Is.EqualTo(before.Dual.Tip.CopyAlpha()));
            Assert.That((window.GetBrush().TextureDepth,window.GetBrush().Dual.Radius,window.GetBrush().Dual.Mode),Is.EqualTo((before.TextureDepth,before.Dual.Radius,before.Dual.Mode)));
            dialogs.File=Path.Combine(project,"Brush.ylp");window.SaveProject(true);
            window.CreateProject(new NewProjectSettings{Model=null,Resolution=512});window.OpenProjectAt(dialogs.File);
            Assert.That(window.ImageResources.Brushes.Single().Hash,Is.EqualTo(brush.Hash)); Assert.That(window.GetBrush().Tip.CopyAlpha(),Is.EqualTo(before.Tip.CopyAlpha()));
            Assert.That(window.GetBrush().Texture.CopyAlpha(),Is.EqualTo(before.Texture.CopyAlpha()));Assert.That(window.GetBrush().Dual.Tip.CopyAlpha(),Is.EqualTo(before.Dual.Tip.CopyAlpha()));
            Assert.That(()=>window.ImageResources.Remove(brush.Id),Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.InUse));
        }
        [Test] public void MaterialValuesAreASeparateResourceAndPlaceWithOneUndo()
        {
            window.SetMaterialMode(true);window.SetMaterialChannel(PaintChannel.Roughness,true);var material=window.SaveShelfMaterial("Paint");
            Assert.That(material.ResourceKind,Is.EqualTo(ResourceKind.Material));int count=window.Document.Layers.Count;window.Document.ClearHistory();
            var placed=window.PlaceSmartAsset("p:"+material.Id);Assert.That(placed,Is.Not.Null);Assert.That(window.Document.Layers.Count,Is.EqualTo(count+1));Assert.That(window.Document.UndoCount,Is.EqualTo(1));
            window.Document.Undo();Assert.That(window.Document.Layers.Count,Is.EqualTo(count));
            dialogs.File=Path.Combine(project,"Material.ylp");window.SaveProject(true);window.CreateProject(new NewProjectSettings{Model=null,Resolution=512});window.OpenProjectAt(dialogs.File);
            Assert.That(window.ImageResources.Smart.Single().ResourceKind,Is.EqualTo(ResourceKind.Material));
        }
        [Test] public void AUnityMaterialImportsCopiesAndKeepsTheSourceUntouched()
        {
            var shader=Shader.Find("Standard");if(EditorShaderCompiler.IsBroken||shader==null||ShaderUtil.ShaderHasError(shader))Assert.Ignore("The Standard shader is broken in this GUI environment.");
            var m=new Material(shader){name="Example"};m.SetColor("_Color",Color.red);m.SetFloat("_Metallic",.75f);AssetDatabase.CreateAsset(m,folder+"/Example.mat");AssetDatabase.SaveAssets();
            string path=Path.GetFullPath(folder+"/Example.mat");var before=File.ReadAllBytes(path);int dirty=EditorUtility.GetDirtyCount(m);
            var imported=window.ImportUnityMaterial(m);Assert.That(imported.ResourceKind,Is.EqualTo(ResourceKind.Material));var placed=window.PlaceSmartAsset("p:"+imported.Id);Assert.That(placed,Is.Not.Null);
            Assert.That(File.ReadAllBytes(path),Is.EqualTo(before));Assert.That(EditorUtility.GetDirtyCount(m),Is.EqualTo(dirty));
        }
        [TestCase(true)][TestCase(false)] public void NormalMapsAreDecodedToUnitRgbWithoutChangingImportSettings(bool readable)
        {
            if (!UnityTextureReader.GpuReadbackWorks(out _)) Assert.Ignore("Normal decoding needs the working batch-gl graphics device.");
            string path = folder + "/Normal.png";
            File.WriteAllBytes(Path.GetFullPath(path), RgbaPng.Encode(Enumerable.Range(0,16).SelectMany(i => i%2==0?new byte[]{128,128,255,255}:new byte[]{160,96,247,255}).ToArray(),4,4));
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path); importer.textureType=TextureImporterType.NormalMap; importer.textureCompression=TextureImporterCompression.Uncompressed; importer.isReadable=readable; importer.SaveAndReimport();
            byte[] before=File.ReadAllBytes(Path.GetFullPath(path)+".meta"); var texture=AssetDatabase.LoadAssetAtPath<Texture2D>(path); var result=window.ImportUnityTexture(texture);
            var pixel=result.Content.GetPixel(0,0);Assert.That(pixel.A,Is.EqualTo(255));Assert.That(pixel.R,Is.InRange(126,130));Assert.That(pixel.G,Is.InRange(126,130));Assert.That(pixel.B,Is.EqualTo(255));
            var tilted=result.Content.GetPixel(1,0);float x=tilted.R/127.5f-1,y=tilted.G/127.5f-1,z=tilted.B/127.5f-1;
            Assert.That(x*x+y*y+z*z,Is.EqualTo(1).Within(.025));Assert.That(tilted.R,Is.InRange(156,164));Assert.That(tilted.G,Is.InRange(92,100));
            Assert.That(result.ColorSpace,Is.EqualTo(ResourceColorSpace.Linear));Assert.That(result.Origin.ReadThroughGpu,Is.True);Assert.That(File.ReadAllBytes(Path.GetFullPath(path)+".meta"),Is.EqualTo(before));
        }
        [Test] public void UnitySmartImporterSummarizesAndMakesASpherePreview()
        {
            var m=BuiltInSmartMaterials.Make("rusty-metal");string asset=folder+"/Example.ylsmart";File.WriteAllBytes(Path.GetFullPath(asset),SmartMaterialFile.Write(m,YlpContent.Writer));AssetDatabase.ImportAsset(asset,ImportAssetOptions.ForceSynchronousImport);
            Assert.That(AssetImporter.GetAtPath(asset),Is.TypeOf<SmartMaterialImporter>());var info=AssetDatabase.LoadAssetAtPath<SmartMaterialImportInfo>(asset);
            Assert.That(TexturePaintWindow.UnityDropKeys(new Object[]{info}),Is.EqualTo(new[]{"u:"+AssetDatabase.AssetPathToGUID(asset)}));
            Assert.That(info.error,Is.Empty);Assert.That(info.layerCount,Is.EqualTo(m.LayerCount));Assert.That(info.thumbnail,Is.Not.Null);Assert.That(info.thumbnail.GetPixel(0,0).a,Is.Zero);
            Assert.That(window.ImportSmartAsset("u:"+AssetDatabase.AssetPathToGUID(asset)).Material.LayerCount,Is.EqualTo(m.LayerCount));
        }
        [Test] public void ACancelledLargeSmartSaveNeverAddsAProjectOrLibraryEntry()
        {
            PrepareLargeSmart();
            Assert.That(window.SaveSmartMaterial("Large"),Is.Null);Assert.That(window.SmartSavePending,Is.True);
            window.CancelSmartSave();window.TickSmartSave();Assert.That(window.SmartSavePending,Is.False);Assert.That(window.ImageResources.Smart,Is.Empty);Assert.That(ResourceLibraryFolder.ListSmart(PainterSettings.LibraryFolder),Is.Empty);
        }
        [Test] public void LargeSmartSaveCompletesUsingTheCapturedPixels()
        {
            var (d,b)=PrepareLargeSmart();
            var timing=System.Diagnostics.Stopwatch.StartNew();window.SaveSmartMaterial("Large");double captureMs=timing.Elapsed.TotalMilliseconds;Assert.That(window.SmartSavePending,Is.True);
            d.Fill(b.Id,PaintChannel.Color,new Rgba32(88,99,111,255));var watch=System.Diagnostics.Stopwatch.StartNew();
            while(window.SmartSavePending&&watch.Elapsed.TotalSeconds<20){Thread.Sleep(10);window.TickSmartSave();}
            Assert.That(window.SmartSavePending,Is.False,window.StatusMessage);var saved=window.ImageResources.Smart.Single();Assert.That(saved.Material.PixelBytes,Is.GreaterThanOrEqualTo(8L<<20));
            TestContext.Progress.WriteLine("アセット保存計測: 画素 "+saved.Material.PixelBytes+" バイト / 取り出し "+captureMs.ToString("F1")+" ms / 完了 "+timing.Elapsed.TotalMilliseconds.ToString("F1")+" ms / ファイル "+saved.Length+" バイト");
            Assert.That(SmartMaterialFile.Read(saved.FileBytes()).LayerCount,Is.EqualTo(2));Assert.That(ResourceLibraryFolder.ListSmart(PainterSettings.LibraryFolder).Count,Is.EqualTo(1));
            Assert.That(DocumentBinary.Read(saved.Material.FragmentBytes()).Composite(PaintChannel.Color).Take(4),Is.EqualTo(new byte[]{33,44,55,255}));
        }
        (PaintDocument document, PaintLayer top) PrepareLargeSmart()
        {
            window.CreateProject(new NewProjectSettings{Model=null,Resolution=1024});var d=window.Document;
            var a=d.AddLayer("One");var b=d.AddLayer("Two");
            var pixels=new byte[d.Width*d.Height*4];
            for(int y=0;y<d.Height;y++)for(int x=0;x<d.Width;x++)
            {int i=(y*d.Width+x)*4;pixels[i]=(byte)(11+x%64);pixels[i+1]=(byte)(22+y%64);pixels[i+2]=33;pixels[i+3]=255;}
            d.ReplacePixels(a.Id,PaintChannel.Color,pixels,false);
            for(int i=0;i<pixels.Length;i+=4){pixels[i]+=22;pixels[i+1]+=22;pixels[i+2]=55;}
            d.ReplacePixels(b.Id,PaintChannel.Color,pixels,false);d.ClearHistory();
            window.SelectLayers(new[]{a.Id,b.Id},b.Id);Assert.That(d.AllocatedBytes,Is.GreaterThanOrEqualTo(8L<<20));return(d,b);
        }
    }
}
