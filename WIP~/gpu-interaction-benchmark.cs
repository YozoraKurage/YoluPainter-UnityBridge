using System.Collections.Generic;
using System.Diagnostics;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
var sb = new System.Text.StringBuilder();
int size = 4096, tile = 128, n = size / tile;
var doc = new PaintDocument(size, size, tile, 0); doc.SourceBudgetBytes = 2L << 30;
var rnd = new System.Random(1);
var buf = new byte[tile * tile * 4];
var layers = new List<PaintLayer>();
var swAll = Stopwatch.StartNew();
for (int l = 0; l < 8; l++)
{
  var layer = doc.AddLayer("L" + l); layers.Add(layer);
  var surface = layer.GetChannel(PaintChannel.Color);
  for (int ty = 0; ty < n; ty++) for (int tx = 0; tx < n; tx++)
  {
    for (int i = 0; i < buf.Length; i += 4) { buf[i] = (byte)(i * 7 + l * 31 + tx); buf[i + 1] = (byte)(i * 3 + ty); buf[i + 2] = (byte)(l * 40 + i); buf[i + 3] = (byte)(l == 0 ? 255 : 120 + (i % 100)); }
    surface.ImportTile(new TileCoord(tx, ty), buf);
  }
  if (l > 0) doc.SetLayerOpacity(layer.Id, .8);
}
doc.ClearHistory();
sb.AppendLine($"setup {swAll.ElapsedMilliseconds} ms, allocated {doc.AllocatedBytes >> 20} MiB");
var probe = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
void Sync(Texture t) { var rt = t as RenderTexture; if (rt == null) return; var old = RenderTexture.active; RenderTexture.active = rt; probe.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); probe.Apply(); RenderTexture.active = old; }
using (var comp = new TileGpuCompositor())
{
  var sw = Stopwatch.StartNew(); comp.Update(doc, PaintChannel.Color); Sync(comp.Texture); sb.AppendLine($"first full update {sw.ElapsedMilliseconds} ms, tiles {comp.LastUpdatedTileCount}");
  foreach (int which in new[] { 7, 0, 4 })
  {
    var times = new List<double>();
    for (int r = 0; r < 3; r++) { doc.SetLayerOpacity(layers[which].Id, .5 + .1 * r); sw.Restart(); comp.Update(doc, PaintChannel.Color); Sync(comp.Texture); times.Add(sw.Elapsed.TotalMilliseconds); }
    times.Sort(); sb.AppendLine($"opacity drag on layer {which}: median {times[1]:F0} ms, tiles {comp.LastUpdatedTileCount}");
  }
}
// breakdown
var swc = Stopwatch.StartNew(); for (int l = 0; l < 8; l++) for (int ty = 0; ty < n; ty++) for (int tx = 0; tx < n; tx++) layers[l].CopyTile(PaintChannel.Color, new TileCoord(tx, ty), buf); sb.AppendLine($"CopyTile x{8 * n * n}: {swc.ElapsedMilliseconds} ms");
var up = new Texture2D(tile, tile, TextureFormat.RGBA32, false, true);
var rt1 = new RenderTexture(tile, tile, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear); rt1.Create();
var rt2 = new RenderTexture(tile, tile, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear); rt2.Create();
swc.Restart(); for (int i = 0; i < 8 * n * n; i++) { up.LoadRawTextureData(buf); up.Apply(false, false); } Sync(rt1); sb.AppendLine($"upload x{8 * n * n}: {swc.ElapsedMilliseconds} ms");
var mat = new Material(Shader.Find("Hidden/YoluPainter/TileComposite"));
mat.SetTexture("_LayerTex", up); mat.SetFloat("_Opacity", .8f); mat.SetInt("_BlendMode", 0); mat.SetVector("_Mask", Vector4.zero);
swc.Restart(); for (int i = 0; i < 8 * n * n; i++) { if (i % 2 == 0) Graphics.Blit(rt1, rt2, mat, 0); else Graphics.Blit(rt2, rt1, mat, 0); } Sync(rt1); sb.AppendLine($"blit x{8 * n * n}: {swc.ElapsedMilliseconds} ms");
var big1 = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear); big1.Create();
var big2 = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear); big2.Create();
var bigTex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
mat.SetTexture("_LayerTex", bigTex);
swc.Restart(); for (int i = 0; i < 8; i++) { if (i % 2 == 0) Graphics.Blit(big1, big2, mat, 0); else Graphics.Blit(big2, big1, mat, 0); } Sync(big1); sb.AppendLine($"full-canvas blit x8: {swc.ElapsedMilliseconds} ms");
var rawBig = new byte[size * size * 4];
swc.Restart(); bigTex.LoadRawTextureData(rawBig); bigTex.Apply(false, false); Sync(big1); sb.AppendLine($"one 4096² upload: {swc.ElapsedMilliseconds} ms");
Object.DestroyImmediate(mat); Object.DestroyImmediate(up); Object.DestroyImmediate(bigTex); foreach (var r in new[] { rt1, rt2, big1, big2 }) { r.Release(); Object.DestroyImmediate(r); } Object.DestroyImmediate(probe);
return sb.ToString();
