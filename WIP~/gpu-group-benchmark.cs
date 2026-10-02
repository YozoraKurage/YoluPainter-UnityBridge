using System.Collections.Generic;
using System.Diagnostics;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
var sb = new System.Text.StringBuilder();
var doc = new PaintDocument(2048, 2048, 128);
var fills = new Dictionary<string, Rgba32> { };
PaintLayer Fill(string name, Rgba32 c) => doc.AddFillLayer(name, new Dictionary<PaintChannel, Rgba32> { { PaintChannel.Color, c } });
var back = Fill("Backdrop", new Rgba32(90, 120, 160, 255));
var a = Fill("A", new Rgba32(200, 60, 40, 180));
var b = Fill("B", new Rgba32(30, 200, 90, 140));
var c = Fill("C", new Rgba32(240, 230, 20, 200));
var r = doc.AddLayer("Raster");
var brush = new BrushSettings { Radius = 40, Hardness = .5, Opacity = .9, Color = new Rgba32(20, 40, 220), PressureSize = false, PressureOpacity = false };
using (var s = doc.BeginStroke(r.Id, PaintChannel.Color, brush)) { for (int i = 0; i < 40; i++) s.Add(new BrushSample(50 + i * 50, 100 + (i * 37) % 1800)); s.Commit(); }
var adjust = doc.AddAdjustmentLayer("Invert", AdjustmentSettings.Invert()); doc.SetLayerOpacity(adjust.Id, .5);
var g4 = doc.GroupLayers(new[] { c.Id, r.Id }, "G4"); doc.SetLayerBlendMode(g4.Id, LayerBlendMode.Multiply);
var mask = doc.AddLayerMask(g4.Id);
using (var s = doc.BeginMaskStroke(g4.Id, brush)) { for (int i = 0; i < 30; i++) s.Add(new BrushSample(1000 + i * 30, 200 + i * 50)); s.Commit(); }
var g3 = doc.GroupLayers(new[] { b.Id, g4.Id, adjust.Id }, "G3");
var g2 = doc.GroupLayers(new[] { a.Id, g3.Id }, "G2"); doc.SetLayerBlendMode(g2.Id, LayerBlendMode.Screen); doc.SetLayerOpacity(g2.Id, .9);
var g1 = doc.GroupLayers(new[] { g2.Id }, "G1"); doc.SetLayerOpacity(g1.Id, .8);
doc.ClearHistory();
sb.AppendLine("device " + SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceVersion);
sb.AppendLine("levels needed " + TileGpuCompositor.LevelsNeeded(CpuCompositor.Plan(doc, PaintChannel.Color)));
int toggle = 0;
var probe = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
void Sync(TileGpuCompositor comp)
{
  var rt = comp.Texture as RenderTexture; if (rt == null) return;
  var old = RenderTexture.active; RenderTexture.active = rt; probe.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); probe.Apply(); RenderTexture.active = old;
}
foreach (var mode in new[] { "gpu", "cpu-tiles (old path)", "cpu fallback (no GPU)" })
{
  using (var comp = mode == "cpu fallback (no GPU)" ? new TileGpuCompositor(allowGpu: false) : new TileGpuCompositor { CompositeGroupsOnCpu = mode.StartsWith("cpu-tiles") })
  {
    comp.Update(doc, PaintChannel.Color); Sync(comp);
    var times = new List<double>();
    for (int run = 0; run < 7; run++)
    {
      doc.SetLayerOpacity(g1.Id, .5 + 0.01 * (++toggle % 40));
      var sw = Stopwatch.StartNew();
      comp.Update(doc, PaintChannel.Color); Sync(comp);
      sw.Stop(); times.Add(sw.Elapsed.TotalMilliseconds);
    }
    times.Sort();
    sb.AppendLine($"{mode}: tiles {comp.LastUpdatedTileCount}, cpu tiles {comp.LastCpuTileCount}, nested RTs {comp.NestedRenderTextureCount}, median {times[3]:F1} ms (min {times[0]:F1}, max {times[6]:F1}) — {comp.Backend}");
  }
}
Object.DestroyImmediate(probe);
return sb.ToString();
