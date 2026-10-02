using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Dot.TexturePainter.Core;
using Dot.TexturePainter.Core.Persistence;
using Dot.TexturePainter.Core.Psd;

static class Program
{
    static int passed;
    static void Main()
    {
        Test("sparse canvas and channel isolation", () => {
            var doc=new PaintDocument(4096,4096);var layer=doc.AddLayer("Sparse");doc.ClearHistory();
            Equal(doc.AllocatedBytes,0L);
            using(var s=doc.BeginStroke(layer.Id,PaintChannel.Color,new BrushSettings{Radius=4,PressureSize=false,PressureOpacity=false})) {s.Add(new BrushSample(2048,2048));s.Commit();}
            True(doc.AllocatedBytes<=4L*128*128*4);Equal(doc.CompositePixel(PaintChannel.Roughness,2048,2048).A,(byte)0);True(doc.CompositePixel(PaintChannel.Color,2048,2048).A>0);
        });
        Test("exact undo redo and cancellation",()=>{
            var d=new PaintDocument(64,64,16);var l=d.AddLayer("A");d.ClearHistory();
            var before=DocumentBinary.Write(d);using(var s=d.BeginStroke(l.Id,PaintChannel.Color,new BrushSettings{Radius=8,Color=new Rgba32(90,10,130,128)})){s.Add(new BrushSample(30,30));s.Add(new BrushSample(40,40));s.Commit();}
            var painted=DocumentBinary.Write(d);True(!before.SequenceEqual(painted));True(d.Undo());Bytes(before,DocumentBinary.Write(d));True(d.Redo());Bytes(painted,DocumentBinary.Write(d));
            using(var s=d.BeginStroke(l.Id,PaintChannel.Color,new BrushSettings())){s.Add(new BrushSample(10,10));s.Cancel();}Bytes(painted,DocumentBinary.Write(d));
        });
        Test("native archive exact attributes and hidden RGB",()=>{
            var d=new PaintDocument(35,19,16);var l=d.AddLayer("透明 日本語 🎨");
            l.GetChannel(PaintChannel.Color).SetPixel(2,3,new Rgba32(41,53,67,0));
            l.GetChannel(PaintChannel.Height).SetPixel(34,18,new Rgba32(102,102,102,192));d.SetChannelEnabled(l.Id,PaintChannel.Height,false);
            d.SetLayerBlendMode(l.Id,LayerBlendMode.Screen);d.SetLayerOpacity(l.Id,.37);d.SetLayerVisibility(l.Id,false);
            byte[] bytes=DocumentBinary.Write(d);var restored=DocumentBinary.Read(bytes);Bytes(bytes,DocumentBinary.Write(restored));Equal(restored.GetLayer(l.Id).GetChannel(PaintChannel.Color).GetPixel(2,3),new Rgba32(41,53,67,0));True(!restored.GetLayer(l.Id).IsChannelEnabled(PaintChannel.Height));
        });
        Test("native corruption refuses unknown versions and truncation",()=>{
            var d=new PaintDocument(16,16,16);d.AddLayer("A");var bytes=DocumentBinary.Write(d);
            Throws(()=>DocumentBinary.Read(bytes.Take(bytes.Length-1).ToArray()));bytes[8]=127;Throws(()=>DocumentBinary.Read(bytes));
        });
        Test("save interruption leaves valid current generation",()=>{
            string root=Path.Combine(Path.GetTempPath(),"dot-paint-test-"+Guid.NewGuid().ToString("N"));
            try{
                var d=new PaintDocument(16,16,16);var l=d.AddLayer("A");var bytes=DocumentBinary.Write(d);
                var first=GenerationStore.Commit(root,new Dictionary<string,byte[]>{{"document.utpaint",bytes}});
                foreach(string point in new[]{"file:document.utpaint","verified","generation-renamed","before-pointer"}){
                    Throws(()=>GenerationStore.Commit(root,new Dictionary<string,byte[]>{{"document.utpaint",bytes}},first.Token,p=>{if(p==point)throw new IOException("injected");}));
                    Equal(GenerationStore.Load(root).Token,first.Token);Bytes(bytes,GenerationStore.Load(root).Files["document.utpaint"]);
                }
                l.GetChannel(PaintChannel.Color).SetPixel(0,0,new Rgba32(1,2,3));var next=GenerationStore.Commit(root,new Dictionary<string,byte[]>{{"document.utpaint",DocumentBinary.Write(d)}},first.Token);True(next.Token!=first.Token);
                Throws(()=>GenerationStore.Commit(root,new Dictionary<string,byte[]>{{"document.utpaint",bytes}},first.Token));
                File.AppendAllText(Path.Combine(root,"generations",next.Generation,"document.utpaint"),"tamper");True(GenerationStore.HasExternalChange(root,next.Token));Throws(()=>GenerationStore.Load(root));
            }finally{Directory.Delete(root,true);}
        });
        Test("PSD bridge layered native roundtrip",()=>{
            var d=new PaintDocument(32,24,16);var a=d.AddLayer("底 🎨");var b=d.AddLayer("上");
            a.GetChannel(PaintChannel.Color).SetPixel(2,3,new Rgba32(90,80,70,255));b.GetChannel(PaintChannel.Color).SetPixel(5,7,new Rgba32(30,40,50,128));b.GetChannel(PaintChannel.Color).SetPixel(1,9,new Rgba32(11,12,13,0));
            var bytes=PsdCodec.Write(PsdBridge.Export(d,PaintChannel.Color));var read=PsdCodec.Read(bytes);Equal(read.Mode,PsdCompatibilityMode.EditableRaster);var imported=PsdBridge.Import(read);
            Bytes(d.Composite(PaintChannel.Color),imported.Composite(PaintChannel.Color));Equal(imported.Layers[1].GetChannel(PaintChannel.Color).GetPixel(1,9),new Rgba32(11,12,13,0));
            d.SetLayerBlendMode(b.Id,LayerBlendMode.Multiply);Throws(()=>PsdBridge.Export(d,PaintChannel.Color));
        });
        Test("save refuses provisional stroke",()=>{
            var d=new PaintDocument(16,16,16);var l=d.AddLayer("A");
            using(var stroke=d.BeginStroke(l.Id,PaintChannel.Color,new BrushSettings())){stroke.Add(new BrushSample(8,8));Throws(()=>DocumentBinary.Write(d));Throws(()=>PsdBridge.Export(d,PaintChannel.Color));stroke.Cancel();}
        });
        Test("committed generation survives interrupted acknowledgement",()=>{
            string root=Path.Combine(Path.GetTempPath(),"dot-paint-postcommit-"+Guid.NewGuid().ToString("N"));
            try{var d=new PaintDocument(16,16,16);d.AddLayer("A");var files=new Dictionary<string,byte[]>{{"document.utpaint",DocumentBinary.Write(d)}};
                Throws(()=>GenerationStore.Commit(root,files,null,p=>{if(p=="after-pointer")throw new IOException("lost ack");}));
                Bytes(files["document.utpaint"],GenerationStore.Load(root).Files["document.utpaint"]);
            }finally{Directory.Delete(root,true);}
        });
        Console.WriteLine("PASS "+passed+" integration scenarios (production pure C# sources, .NET 8). Unity adapter/GPU/device tests NOT run.");
    }
    static void Test(string name,Action run){try{run();passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){Console.Error.WriteLine("FAIL "+name+": "+ex);Environment.Exit(1);}}
    static void True(bool value){if(!value)throw new Exception("Assertion failed");}
    static void Equal<T>(T a,T b){if(!EqualityComparer<T>.Default.Equals(a,b))throw new Exception("Expected "+b+", got "+a);}
    static void Bytes(byte[] a,byte[] b){True(a.SequenceEqual(b));}
    static void Throws(Action action){try{action();}catch{return;}throw new Exception("Expected failure");}
}
