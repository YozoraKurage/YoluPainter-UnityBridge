using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>コンテナの OpenGL で、準備・同期読み戻し・解放をフレームをまたいで繰り返す。
    /// 暖機後の Unity のグラフィックス計器の幅とテクスチャ数を調べる。Windows の D3D11 の検証ではない。</summary>
    [Category("GPU")]
    public sealed class GpuReadbackMemoryTests
    {
        internal static void RequireOpenGl()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLCore)
                Assert.Ignore("この計測は OpenGL 用。Windows の D3D11 の検証ではない。");
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite");
        }

        static void RequireGpuBake()
        {
            RequireOpenGl();
            string why = GpuMeshBakeRayTracer.Unavailable();
            if (why != null) Assert.Ignore("GPU ベイクが利用できない環境: " + why);
        }

        sealed class CaptureScene : IMeshBakeRayTracer
        {
            public MeshBakeRayScene Scene;
            public string Name => "シーンの取得";
            public string Prepare(MeshBakeRayScene scene) { Scene = scene; return "試験では CPU で焼く"; }
            public void Trace(MeshBakeRayJob[] jobs, int count, MeshBakeRayResult[] output) => throw new NotSupportedException();
            public void Release() { }
        }

        internal static void Record(string name, int cycle, long bytes, int textures)
        {
            string path = Path.Combine("Logs", "YoluPainterSnapshots", "readback", name + "-opengl.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (cycle == 0) File.WriteAllText(path, $"# {SystemInfo.graphicsDeviceName} / {SystemInfo.graphicsDeviceType}\ncycle\tgraphics_bytes\ttextures\n");
            File.AppendAllText(path, $"{cycle}\t{bytes}\t{textures}\n");
            TestContext.WriteLine($"OpenGL {name} {cycle}: graphics={bytes} B textures={textures}");
        }

        static MeshBakeRayScene Scene()
        {
            var corners = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            var input = new MeshBakeInput(corners, MeshBakeInput.ReconstructNormals(corners), new float[] { 0, 0, 1, 0, 0, 1 }, new[] { 0 }, 0, "test");
            var capture = new CaptureScene();
            var result = MeshBaker.Bake(input, new MeshBakeSettings { Width = 8, Height = 8, Padding = 0, Maps = new[] { MeshMapKind.AmbientOcclusion }, AoSamples = 1 }, new MeshBakeBudget(), null, default, null, capture);
            Assert.That(result.Status, Is.EqualTo(MeshBakeStatus.Completed));
            Assert.That(capture.Scene, Is.Not.Null);
            return capture.Scene;
        }

        [UnityTest] public IEnumerator OpenGlRepeatedBakePrepareReadAndReleaseStayWithinWarmGraphicsMemory()
        {
            RequireGpuBake();
            var tracer = new GpuMeshBakeRayTracer(32L << 20);
            var scene = Scene();
            var jobs = new[] { new MeshBakeRayJob { Ox = 10, Oy = 10, Oz = 10, Nz = 1, Gz = 1, Rc = 1 } }; var output = new MeshBakeRayResult[1];
            void Cycle()
            {
                Assert.That(tracer.Prepare(scene), Is.Null);
                var oldJobs = (ComputeBuffer)typeof(GpuMeshBakeRayTracer).GetField("jobs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(tracer);
                Assert.That(tracer.Prepare(scene), Is.Null, "再準備は前のバッファを手放す");
                Assert.That(oldJobs.IsValid(), Is.False, "旧バッファの GPU 所有権は GC に任せない");
                tracer.Trace(jobs, 1, output);
                Assert.That(output[0].Ao, Is.EqualTo(1).Within(.0001));
                tracer.Release(); tracer.Release();
                Assert.That(tracer.UploadedBytes, Is.Zero);
            }
            try
            {
                for (int i = 0; i < 4; i++) { Cycle(); yield return null; }
                long baseline = Profiler.GetAllocatedMemoryForGraphicsDriver();
                int textures = Resources.FindObjectsOfTypeAll<Texture>().Length;
                Record("bake", 0, baseline, textures);
                for (int i = 0; i < 16; i++)
                {
                    Cycle(); yield return null; long bytes = Profiler.GetAllocatedMemoryForGraphicsDriver();
                    Record("bake", i + 1, bytes, Resources.FindObjectsOfTypeAll<Texture>().Length);
                    Assert.That(bytes, Is.LessThanOrEqualTo(baseline + (16L << 20)), "暖機後の保持量の増加は 16 MiB 以内");
                    Assert.That(Resources.FindObjectsOfTypeAll<Texture>().Length, Is.LessThanOrEqualTo(textures + 1), "エディタが既存のテクスチャを捨てる減少は許し、増加を制限する");
                }
            }
            finally { tracer.Release(); }
        }

        [Test] public void BakeWorkBuffersGrowOnlyWhenTheDispatchNeedsThemAndKeepTheTailResults()
        {
            RequireGpuBake();
            var tracer = new GpuMeshBakeRayTracer(32L << 20);
            try
            {
                Assert.That(tracer.Prepare(Scene()), Is.Null);
                var field = typeof(GpuMeshBakeRayTracer).GetField("jobs", BindingFlags.Instance | BindingFlags.NonPublic);
                var initial = (ComputeBuffer)field.GetValue(tracer);
                Assert.That(initial.count, Is.EqualTo(GpuMeshBakeRayTracer.FirstDispatchJobs));
                long before = tracer.UploadedBytes;
                int count = GpuMeshBakeRayTracer.FirstDispatchJobs * 4 + 3;
                var jobs = Enumerable.Repeat(new MeshBakeRayJob { Ox = 10, Oy = 10, Oz = 10, Nz = 1, Gz = 1, Rc = 1 }, count).ToArray();
                var output = new MeshBakeRayResult[count];
                // 遅い GPU でも拡張の経路を必ず通す（時間による chunk の調整自体は既存のベイク試験で確認する）。
                typeof(GpuMeshBakeRayTracer).GetField("chunk", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(tracer, count);
                tracer.Trace(jobs, count, output);
                Assert.That(initial.IsValid(), Is.False);
                Assert.That(((ComputeBuffer)field.GetValue(tracer)).count, Is.GreaterThanOrEqualTo(count));
                Assert.That(tracer.UploadedBytes, Is.GreaterThan(before).And.LessThanOrEqualTo(32L << 20));
                Assert.That(output.Select(r => r.Ao), Is.All.EqualTo(1).Within(.0001));
            }
            finally { tracer.Release(); }
        }

        [Test] public void RejectedBakeReprepareDoesNotKeepThePreviousBuffersUsable()
        {
            RequireGpuBake();
            var tracer = new GpuMeshBakeRayTracer(32L << 20);
            try
            {
                Assert.That(tracer.Prepare(Scene()), Is.Null);
                Assert.That(tracer.UploadedBytes, Is.GreaterThan(0));
                Assert.That(tracer.Prepare(null), Is.Not.Null);
                Assert.That(tracer.UploadedBytes, Is.Zero);
                Assert.Throws<InvalidOperationException>(() => tracer.Trace(new MeshBakeRayJob[1], 1, new MeshBakeRayResult[1]));
                var denied = new GpuMeshBakeRayTracer(0);
                try { Assert.That(denied.Prepare(Scene()), Does.Contain("budget")); Assert.That(denied.UploadedBytes, Is.Zero); }
                finally { denied.Release(); }
            }
            finally { tracer.Release(); }
        }

        [Test] public void DisabledBakeIsRefusedBeforeAllocatingAndUsesTheCpuResult()
        {
            string saved = Environment.GetEnvironmentVariable(GpuMeshBakeRayTracer.OffEnvironmentVariable);
            var tracer = new GpuMeshBakeRayTracer(32L << 20);
            try
            {
                // 停止設定を有効にするだけで、既に停止された環境の GPU ベイクを有効にはしない。
                Environment.SetEnvironmentVariable(GpuMeshBakeRayTracer.OffEnvironmentVariable, "1");
                Assert.That(tracer.Prepare(Scene()), Does.Contain(GpuMeshBakeRayTracer.OffEnvironmentVariable));
                Assert.That(tracer.UploadedBytes, Is.Zero);
                Assert.That(typeof(GpuMeshBakeRayTracer).GetField("jobs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(tracer), Is.Null);
                Assert.Throws<InvalidOperationException>(() => tracer.Trace(new MeshBakeRayJob[1], 1, new MeshBakeRayResult[1]));
                var corners = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
                var input = new MeshBakeInput(corners, MeshBakeInput.ReconstructNormals(corners), new float[] { 0, 0, 1, 0, 0, 1 }, new[] { 0 }, 0, "test");
                var settings = new MeshBakeSettings { Width = 8, Height = 8, Padding = 0, Maps = new[] { MeshMapKind.AmbientOcclusion }, AoSamples = 1 };
                var cpu = MeshBaker.Bake(input, settings);
                var fallback = MeshBaker.Bake(input, settings, null, null, default, null, tracer);
                Assert.That(fallback.Status, Is.EqualTo(MeshBakeStatus.Completed));
                Assert.That(fallback.Report.RayBackend, Is.EqualTo("CPU"));
                Assert.That(fallback.Report.Diagnostics.Any(d => d.Contains(GpuMeshBakeRayTracer.OffEnvironmentVariable)), Is.True);
                Assert.That(MeshMapBinary.Write(fallback.Maps[0]), Is.EqualTo(MeshMapBinary.Write(cpu.Maps[0])));
                Assert.That(tracer.UploadedBytes, Is.Zero);
            }
            finally { tracer.Release(); Environment.SetEnvironmentVariable(GpuMeshBakeRayTracer.OffEnvironmentVariable, saved); }
        }

        [Test] public void RgbaReadbackBandsFitTheirBudgetAndKeepSmallImagesInOneTransfer()
        {
            foreach (int width in new[] { 1, 513, 4096, 8192 })
            {
                int rows = UnityTextureReader.ReadbackBandHeight(width, 8192);
                Assert.That(rows, Is.GreaterThan(0));
                Assert.That((long)width * rows * 4, Is.LessThanOrEqualTo(UnityTextureReader.ReadbackBandBytes));
            }
            Assert.That(UnityTextureReader.ReadbackBandHeight(4, 4), Is.EqualTo(4));
        }

        [UnityTest] public IEnumerator OpenGlImageReadbacksKeepPixelsAssetsAndWarmMemoryAcrossFrames()
        {
            RequireOpenGl();
            Assert.That(UnityTextureReader.GpuReadbackWorks(out string why), Is.True, why);
            const int w = 513, h = 513;
            string folder = "Assets/YoluPainterReadback-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder)), Is.Not.Empty);
            string path = folder + "/Pattern.png";
            try
            {
                var pixels = new byte[w * h * 4];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    pixels[o] = (byte)x; pixels[o + 1] = (byte)y; pixels[o + 2] = (byte)(x + y); pixels[o + 3] = (byte)(x % 4 == 0 ? 0 : 255);
                }
                var png = RgbaPng.Encode(pixels, w, h); File.WriteAllBytes(path, png);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = false; importer.sRGBTexture = false; importer.mipmapEnabled = false;
                importer.alphaIsTransparency = false; importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.npotScale = TextureImporterNPOTScale.None; importer.SaveAndReimport();
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                string stamp = UnityTextureReader.Stamp(path); var meta = File.ReadAllBytes(path + ".meta");
                long baseline = 0; int textures = 0;
                for (int i = 0; i < 16; i++)
                {
                    var read = UnityTextureReader.Read(texture);
                    Assert.That(read.ThroughGpu, Is.True);
                    Assert.That(read.Content.CopyPixels(), Is.EqualTo(pixels), "透明画素の RGB と行の向き、末尾の部分行");
                    yield return null;
                    long bytes = Profiler.GetAllocatedMemoryForGraphicsDriver(); int count = Resources.FindObjectsOfTypeAll<Texture>().Length;
                    Record("image", i, bytes, count);
                    if (i == 3) { baseline = bytes; textures = count; }
                    if (i < 4) continue;
                    Assert.That(bytes, Is.LessThanOrEqualTo(baseline + (16L << 20)));
                    Assert.That(count, Is.LessThanOrEqualTo(textures + 1));
                }
                Assert.That(texture.isReadable, Is.False);
                Assert.That(UnityTextureReader.Stamp(path), Is.EqualTo(stamp));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(png)); Assert.That(File.ReadAllBytes(path + ".meta"), Is.EqualTo(meta));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test] public void OpenGlNormalReadbackBandsMatchTheFullDecodedImage()
        {
            RequireOpenGl();
            const string shaderName = "Hidden/YoluPainter/NormalResourceReadback";
            GpuTests.RequireWorkingShader(shaderName);
            const int w = 513, h = 513;
            string folder = "Assets/YoluPainterNormalReadback-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder)), Is.Not.Empty);
            Texture2D full = null; RenderTexture rt = null; Material decode = null;
            var previous = RenderTexture.active;
            try
            {
                var bytes = new byte[w * h * 4];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    bytes[o] = (byte)(96 + x % 65); bytes[o + 1] = (byte)(96 + y % 65); bytes[o + 2] = 245; bytes[o + 3] = 255;
                }
                string path = folder + "/Normal.png";
                File.WriteAllBytes(path, RgbaPng.Encode(bytes, w, h));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.NormalMap; importer.isReadable = false;
                importer.mipmapEnabled = false; importer.npotScale = TextureImporterNPOTScale.None;
                importer.textureCompression = TextureImporterCompression.Uncompressed; importer.SaveAndReimport();
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                var meta = File.ReadAllBytes(path + ".meta");
                decode = new Material(Shader.Find(shaderName));
                rt = new RenderTexture(new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 0) { sRGB = false, useMipMap = false, msaaSamples = 1 });
                Assert.That(rt.Create(), Is.True);
                full = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                Graphics.Blit(texture, rt, decode); RenderTexture.active = rt;
                full.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                var pixels = full.GetPixels32(); var expected = new byte[bytes.Length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    int o = i * 4; var p = pixels[i];
                    expected[o] = p.r; expected[o + 1] = p.g; expected[o + 2] = p.b; expected[o + 3] = p.a;
                }
                var read = UnityTextureReader.Read(texture);
                Assert.That(read.ThroughGpu, Is.True); Assert.That(read.ColorSpace, Is.EqualTo(ResourceColorSpace.Linear));
                Assert.That(read.Content.CopyPixels(), Is.EqualTo(expected), "復号用のマテリアルと末尾の部分行を分割後も保つ");
                Assert.That(texture.isReadable, Is.False); Assert.That(File.ReadAllBytes(path + ".meta"), Is.EqualTo(meta));
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                UnityEngine.Object.DestroyImmediate(full); UnityEngine.Object.DestroyImmediate(decode);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test] public void CpuGroupFallbackRemovesTheReleasedGpuCopyFromItsBudget()
        {
            GpuTests.RequireWorkingShader("Hidden/YoluPainter/TileComposite");
            var doc = new PaintDocument(32, 32, 16);
            var group = doc.AddGroup("Group");
            var bottom = doc.AddLayer("Bottom"); doc.MoveLayerTo(bottom.Id, group.Id, 0);
            var top = doc.AddLayer("Top");
            bottom.GetChannel(PaintChannel.Color).SetPixel(5, 5, new Rgba32(120, 70, 20, 255));
            top.GetChannel(PaintChannel.Color).SetPixel(5, 5, new Rgba32(20, 70, 120, 255));
            using (var c = new TileGpuCompositor())
            {
                c.Update(doc, PaintChannel.Color);
                doc.SetLayerOpacity(top.Id, .5); c.Update(doc, PaintChannel.Color);
                long before = c.ResidentBytes;
                doc.SetLayerOpacity(top.Id, .6); c.Update(doc, PaintChannel.Color);
                Assert.That(c.LastBelowReuseCount, Is.GreaterThan(0), "下の写しができている");
                c.CompositeGroupsOnCpu = true;
                doc.SetLayerOpacity(top.Id, .7); c.Update(doc, PaintChannel.Color);
                Assert.That(c.LastCpuTileCount, Is.GreaterThan(0));
                Assert.That(c.ResidentBytes, Is.LessThan(before), "破棄した下の写しを予算から引く");
                GpuTests.AssertMatches(doc.Composite(PaintChannel.Color), GpuTests.Read(c.Texture), "CPU グループ代替");
            }
        }
    }
}
