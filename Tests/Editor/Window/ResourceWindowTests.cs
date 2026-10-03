using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// <summary>
    /// プロジェクトのリソースをウィンドウの側から（表示しないウィンドウ。batch でも GUI でも回る）: Unity の Texture2D の取り込み（読み取りの許される・
    /// 許されない × sRGB・リニアで画素が保存された値と一致、圧縮は見えている値、インポートの大きさと法線マップの知らせ、HDR・アセットでないもの・
    /// 予算の拒否）、元のファイル・.meta・インポート設定・読み込んだテクスチャが変わらないこと、出どころが変わったときの知らせと、頼んだときだけの
    /// 更新（インポート設定だけの変化は変わったことにしない・消えても写しで使える）、開いたときに尋ねる、保存と開く・復旧、レイヤーとして置く
    /// （1 回の Undo・今のチャンネルだけ・選択範囲は変えない・1 回の操作の予算）、自分の置き場、画像のファイル、消すときの確かめと使用中の拒否。
    /// GPU で読む道は、この環境で GPU の読み戻しが正しく働くとき（batch-gl）は値を確かめ、働かないとき（GUI モードの壊れたシェーダー）は理由を添えて
    /// 断ることを確かめる（間違った値で取り込まない）。テクスチャはテストの一時フォルダ（Assets の下）に作って片付ける。
    /// </summary>
    public sealed class ResourceWindowTests
    {
        string folder, project; TexturePaintWindow window; Dialogs dialogs;
        readonly List<string> temporary = new List<string>();
        readonly List<TexturePaintWindow> others = new List<TexturePaintWindow>();

        sealed class Dialogs : IPainterDialogs
        {
            public string Folder = "", File = "";
            public bool ConfirmAnswer = true;
            public readonly List<string> Asked = new List<string>();
            public string SaveFolder(string title, string folder, string defaultName) { Asked.Add("SaveFolder"); return Folder; }
            public string OpenFolder(string title, string folder) { Asked.Add("OpenFolder"); return Folder; }
            public string OpenFile(string title, string folder, string extension) { Asked.Add("OpenFile"); return File; }
            public string SaveFile(string title, string folder, string defaultName, string extension) { Asked.Add("SaveFile"); return File; }
            public bool Confirm(string title, string message, string ok, string cancel) { Asked.Add("Confirm: " + title); return ConfirmAnswer; }
            public void Inform(string title, string message) { Asked.Add("Inform: " + title); }
            public void Progress(string title, string info, float progress) { }
            public void ClearProgress() { }
        }

        [SetUp] public void Create()
        {
            EditorShaderCompiler.TolerateErrorLogsIfBroken();
            project = Temp(); Directory.CreateDirectory(project);
            PainterSettings.ProjectRoot = project; // 自分の置き場と設定は一時のプロジェクトに
            string name = "YoluPainterResourceTests-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", name), Is.Not.Empty); folder = "Assets/" + name;
            window = NewWindow();
            window.CreateProject(new NewProjectSettings { Model = null, Resolution = 512, Template = ProjectTemplate.Pbr });
        }

        [TearDown] public void Clean()
        {
            foreach (var w in others.Concat(new[] { window }))
            {
                if (w == null) continue;
                string recovery = w.RecoveryRoot; Object.DestroyImmediate(w);
                if (!string.IsNullOrEmpty(recovery) && Directory.Exists(recovery)) Directory.Delete(recovery, true);
            }
            others.Clear(); window = null;
            AssetDatabase.DeleteAsset(folder); PainterSettings.ProjectRoot = null;
            foreach (var path in temporary) { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); }
            temporary.Clear();
        }

        string Temp(string extension = "") { string path = Path.Combine(Path.GetTempPath(), "yolupainter-resources-" + Guid.NewGuid().ToString("N") + extension); temporary.Add(path); return path; }
        TexturePaintWindow NewWindow()
        {
            var w = ScriptableObject.CreateInstance<TexturePaintWindow>();
            var own = new Dialogs(); w.Dialogs = own;
            if (window != null) others.Add(w); else dialogs = own; // dialogs は最初の窓のもの
            return w;
        }

        /// <summary>左と下で違う模様（向きが分かる）、透明画素にも RGB、seed で別の中身。</summary>
        static byte[] Pattern(int w, int h, int seed)
        {
            var rgba = new byte[w * h * 4]; var random = new System.Random(seed);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = (byte)(x * 255 / Math.Max(1, w - 1)); rgba[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1)); rgba[o + 2] = (byte)random.Next(256);
                rgba[o + 3] = (byte)(x < w / 8 ? 0 : 128 + random.Next(128));
            }
            return rgba;
        }

        /// <summary>PNG を一時フォルダに置いて取り込み、設定してから取り込み直す（試験の準備。試す対象のコードはインポート設定を変えない）。</summary>
        Texture2D MakeTexture(string name, byte[] rgba, int w, int h, Action<TextureImporter> setup)
        {
            string path = folder + "/" + name + ".png";
            File.WriteAllBytes(Path.GetFullPath(path), RgbaPng.Encode(rgba, w, h));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default; importer.alphaIsTransparency = false; importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed; importer.npotScale = TextureImporterNPOTScale.None;
            setup?.Invoke(importer);
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>元のファイル・.meta・インポート設定・読み込んだテクスチャの状態の写し（取り込みの前後で比べる）。</summary>
        sealed class AssetState
        {
            public byte[] File, Meta; public string Settings; public bool Readable, Dirty; public string Hash;
            public override string ToString() => Settings;
        }
        static AssetState StateOf(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            return new AssetState
            {
                File = File.ReadAllBytes(Path.GetFullPath(path)), Meta = File.ReadAllBytes(Path.GetFullPath(path) + ".meta"),
                Settings = EditorJsonUtility.ToJson(AssetImporter.GetAtPath(path)), Readable = texture.isReadable, Dirty = EditorUtility.IsDirty(texture),
                Hash = AssetDatabase.GetAssetDependencyHash(path).ToString(),
            };
        }
        static void AssertUnchanged(AssetState before, Texture2D texture, string what)
        {
            var after = StateOf(texture);
            Assert.That(after.File, Is.EqualTo(before.File), what + ": the source file's bytes");
            Assert.That(after.Meta, Is.EqualTo(before.Meta), what + ": the .meta");
            Assert.That(after.Settings, Is.EqualTo(before.Settings), what + ": the import settings");
            Assert.That((after.Readable, after.Dirty, after.Hash), Is.EqualTo((before.Readable, before.Dirty, before.Hash)), what + ": the loaded texture (readable, dirty) and Unity's hash of the asset");
        }

        // ───────── Unity のテクスチャ ─────────

        [Test] public void TexturesAreReadAsStoredWithoutChangingTheAsset([Values(true, false)] bool readable, [Values(true, false)] bool srgb)
        {
            const int w = 48, h = 32;
            var rgba = Pattern(w, h, (readable ? 1 : 2) + (srgb ? 10 : 20));
            var texture = MakeTexture("T-" + readable + "-" + srgb, rgba, w, h, i => { i.isReadable = readable; i.sRGBTexture = srgb; });
            Assert.That(texture.isReadable, Is.EqualTo(readable));
            var before = StateOf(texture);
            bool gpu = UnityTextureReader.GpuReadbackWorks(out string why);
            if (!readable && !gpu)
            {
                // GPU で正しく読み戻せない環境（GUI モードの壊れたシェーダー・グラフィックスデバイス無し）: 間違った値で取り込まず、理由を添えて断る
                Assert.That(() => window.ImportUnityTexture(texture), Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.Unsupported).And.Message.Contains("cannot be read through the GPU"));
                Assert.That(window.ImageResources.Count, Is.Zero);
                AssertUnchanged(before, texture, "refused");
                Assert.Pass("GPU readback unavailable here (" + why + "); the refusal was checked.");
            }
            var image = window.ImportUnityTexture(texture);
            Assert.That((image.Width, image.Height), Is.EqualTo((w, h)));
            Assert.That(image.Content.CopyPixels(), Is.EqualTo(rgba), (readable ? "GetPixels32" : "the GPU") + ", " + (srgb ? "sRGB" : "linear") + ": the stored values, bottom row first, the RGB under zero alpha included");
            Assert.That(image.ColorSpace, Is.EqualTo(srgb ? ResourceColorSpace.Srgb : ResourceColorSpace.Linear));
            Assert.That(image.Origin.Kind, Is.EqualTo(ResourceOriginKind.UnityAsset));
            Assert.That(image.Origin.AssetGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(texture))));
            Assert.That(image.Origin.Path, Is.EqualTo(AssetDatabase.GetAssetPath(texture)));
            Assert.That(image.Origin.SourceStamp, Is.EqualTo(before.Hash)); Assert.That(image.Origin.ReadThroughGpu, Is.EqualTo(!readable));
            Assert.That(image.Name, Is.EqualTo(texture.name));
            AssertUnchanged(before, texture, "imported");
            Assert.That(window.IsSaved, Is.False, "a new resource is an unsaved change");
            // 同じテクスチャをもう一度: 同じリソース
            Assert.That(window.ImportUnityTexture(texture), Is.SameAs(image)); Assert.That(window.ImageResources.Count, Is.EqualTo(1));
            Assert.That(window.StatusMessage, Does.Contain("already in this project"));
        }

        /// <summary>
        /// 色空間の扱い: 読み取りの許されないテクスチャを GPU で写して読むと、ガンマとリニアのどちらのプロジェクトでも、sRGB とリニアのどちらの
        /// テクスチャでも、0〜255 の全部の値（透明画素の RGB も）が保存された値のまま戻る（リニアのプロジェクトでは sRGB のテクスチャを読むときに
        /// 復号され、sRGB のレンダーテクスチャに書くときに符号化し直される）。テストの間だけ Player の色空間を切り替え、終わったら戻す
        /// （テストプロジェクトの ProjectSettings を書き換えて戻す。1 回 2〜7 秒）。
        /// </summary>
        [Test] public void UnreadableTexturesReadEveryValueInBothColourSpaces()
        {
            if (!UnityTextureReader.GpuReadbackWorks(out string why)) Assert.Ignore("GPU readback unavailable here: " + why);
            const int w = 256, h = 4;
            var ramp = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int o = (y * w + x) * 4; ramp[o] = (byte)x; ramp[o + 1] = (byte)(255 - x); ramp[o + 2] = (byte)((x * 7 + y * 64) & 255); ramp[o + 3] = (byte)((x + y * 85) & 255); }
            var srgb = MakeTexture("Ramp-srgb", ramp, w, h, i => { i.isReadable = false; i.sRGBTexture = true; });
            var linear = MakeTexture("Ramp-linear", ramp, w, h, i => { i.isReadable = false; i.sRGBTexture = false; });
            var before = PlayerSettings.colorSpace;
            try
            {
                foreach (var space in new[] { ColorSpace.Gamma, ColorSpace.Linear })
                {
                    PlayerSettings.colorSpace = space; UnityTextureReader.ResetGpuCheck();
                    Assert.That(UnityTextureReader.GpuReadbackWorks(out why), Is.True, space + ": " + why);
                    foreach (var texture in new[] { srgb, linear })
                    {
                        var read = UnityTextureReader.Read(texture);
                        Assert.That(read.ThroughGpu, Is.True);
                        Assert.That(read.Content.CopyPixels(), Is.EqualTo(ramp), space + " project, " + texture.name + ": every value comes back as stored");
                        Assert.That(read.ColorSpace, Is.EqualTo(texture == srgb ? ResourceColorSpace.Srgb : ResourceColorSpace.Linear));
                    }
                }
            }
            finally { PlayerSettings.colorSpace = before; UnityTextureReader.ResetGpuCheck(); }
            Assert.That(PlayerSettings.colorSpace, Is.EqualTo(before), "the project's colour space is restored");
        }

        /// <summary>圧縮したテクスチャは、画素の元のファイルの値ではなく、見えている（復号した）値になる。CPU（読み取り可）と GPU（不可）で同じ値。</summary>
        [Test] public void ACompressedTextureGivesTheValuesItShows()
        {
            const int w = 64, h = 64;
            var rgba = Pattern(w, h, 5);
            var cpu = MakeTexture("Compressed-cpu", rgba, w, h, i => { i.isReadable = true; i.textureCompression = TextureImporterCompression.CompressedHQ; });
            var gpuTexture = MakeTexture("Compressed-gpu", rgba, w, h, i => { i.isReadable = false; i.textureCompression = TextureImporterCompression.CompressedHQ; });
            Assert.That(GraphicsFormatOf(cpu), Is.Not.EqualTo(TextureFormat.RGBA32), "the test texture is compressed (" + cpu.format + ")");
            var before = StateOf(gpuTexture);
            var a = window.ImportUnityTexture(cpu);
            Assert.That(window.StatusMessage, Does.Contain("compressed"));
            Assert.That(a.Content.CopyPixels(), Is.Not.EqualTo(rgba), "lossy: the values shown, not the file's");
            Assert.That(MaxDifference(a.Content.CopyPixels(), rgba), Is.LessThan(64), "but close to them");
            if (!UnityTextureReader.GpuReadbackWorks(out _)) Assert.Pass("GPU readback unavailable here; the CPU decode was checked.");
            var read = UnityTextureReader.Read(gpuTexture);
            Assert.That(read.ThroughGpu, Is.True);
            int diff = MaxDifference(a.Content.CopyPixels(), read.Content.CopyPixels());
            Assert.That(diff, Is.LessThanOrEqualTo(2), "the GPU decodes the same blocks as the CPU (up to the decoders' rounding)");
            var b = window.ImportUnityTexture(gpuTexture);
            if (diff == 0) Assert.That(b, Is.SameAs(a), "equal decoded pixels are the same resource");
            AssertUnchanged(before, gpuTexture, "compressed, unreadable");
            TestContext.WriteLine("CPU vs GPU decode of " + cpu.format + ": largest difference " + diff);
        }
        static TextureFormat GraphicsFormatOf(Texture2D t) => t.format;
        static int MaxDifference(byte[] a, byte[] b) { int m = 0; for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i])); return m; }

        [Test] public void TheImportedSizeAndANormalMapAreNoted()
        {
            var small = MakeTexture("Limited", Pattern(64, 64, 7), 64, 64, i => { i.isReadable = true; i.maxTextureSize = 32; });
            var image = window.ImportUnityTexture(small);
            Assert.That((image.Width, image.Height), Is.EqualTo((32, 32)), "the imported size");
            Assert.That(window.StatusMessage, Does.Contain("imported at 32 × 32").And.Contain("64 × 64"));
            var normal = MakeTexture("Bumps", Pattern(16, 16, 8), 16, 16, i => { i.isReadable = true; i.textureType = TextureImporterType.NormalMap; });
            window.ImportUnityTexture(normal);
            Assert.That(window.StatusMessage, Does.Contain("normal map"));
            Assert.That(window.ImageResources.Images.Last().ColorSpace, Is.EqualTo(ResourceColorSpace.Linear), "normal maps are data");
        }

        [Test] public void HdrNonAssetsOtherTypesAndTheBudgetAreRefused()
        {
            // HDR（EXR → 半精度）は 8 bit に切り詰めないで断る
            var hdr = new Texture2D(8, 8, TextureFormat.RGBAHalf, false, true);
            string exr = folder + "/Bright.exr";
            try { hdr.SetPixels(Enumerable.Repeat(new Color(4, 2, 1, 1), 64).ToArray()); File.WriteAllBytes(Path.GetFullPath(exr), hdr.EncodeToEXR(Texture2D.EXRFlags.None)); }
            finally { Object.DestroyImmediate(hdr); }
            AssetDatabase.ImportAsset(exr, ImportAssetOptions.ForceSynchronousImport);
            var bright = AssetDatabase.LoadAssetAtPath<Texture2D>(exr);
            Assert.That(bright, Is.Not.Null);
            Assert.That(() => window.ImportUnityTexture(bright), Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.Unsupported).And.Message.Contains("HDR"));
            // メモリの上だけのテクスチャ（アセットでない）・テクスチャでないもの
            var loose = new Texture2D(4, 4);
            try { Assert.That(() => window.ImportUnityTexture(loose), Throws.TypeOf<ResourceRefusedException>().With.Message.Contains("not an asset")); }
            finally { Object.DestroyImmediate(loose); }
            var material = new Material(Shader.Find("Unlit/Texture"));
            AssetDatabase.CreateAsset(material, folder + "/M.mat");
            Assert.That(UnityTextureReader.Refusal(material), Does.Contain("not a 2D texture"));
            // 別のアセットの中のテクスチャ（GUID だけでは指せない）
            var inner = new Texture2D(4, 4) { name = "Inner" }; AssetDatabase.AddObjectToAsset(inner, material); AssetDatabase.SaveAssets();
            Assert.That(() => window.ImportUnityTexture(inner), Throws.TypeOf<ResourceRefusedException>().With.Message.Contains("inside another asset"));
            // 予算: 読む前に断る（何も変えない）
            var big = MakeTexture("Big", Pattern(64, 64, 9), 64, 64, i => i.isReadable = true);
            window.ImageResources.BudgetBytes = 1000;
            long revision = window.ImageResources.Revision;
            Assert.That(() => window.ImportUnityTexture(big), Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.OverBudget).And.Message.Contains("budget"));
            Assert.That((window.ImageResources.Count, window.ImageResources.Revision), Is.EqualTo((0, revision)));
        }

        // ───────── 出どころが変わったとき ─────────

        [Test] public void AChangedSourceIsReportedAndOnlyReplacedWhenAsked()
        {
            var texture = MakeTexture("Source", Pattern(32, 32, 1), 32, 32, i => i.isReadable = true);
            string path = AssetDatabase.GetAssetPath(texture);
            var image = window.ImportUnityTexture(texture);
            var original = image.Content;
            Assert.That(window.CheckResourceSources(), Is.Empty);
            var changes = new List<ResourceChange>(); window.ImageResources.Changed += changes.Add;
            // インポート設定だけ（画素は同じ）: Unity の印は変わるが、読み直して同じ中身なので変わったことにしない
            var importer = (TextureImporter)AssetImporter.GetAtPath(path); importer.filterMode = FilterMode.Point; importer.SaveAndReimport();
            Assert.That(AssetDatabase.GetAssetDependencyHash(path).ToString(), Is.Not.EqualTo(image.Origin.SourceStamp));
            Assert.That(window.CheckResourceSources(), Is.Empty, "only the import settings changed; the pixels are the same");
            Assert.That(window.ResourceSource(image.Id).State, Is.EqualTo(TexturePaintWindow.ResourceSourceState.Unchanged));
            // 画素が変わった: 知らせるが、置き換えない
            var next = Pattern(32, 32, 2);
            File.WriteAllBytes(Path.GetFullPath(path), RgbaPng.Encode(next, 32, 32)); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(window.CheckResourceSources(), Is.EqualTo(new[] { image }));
            Assert.That(image.Content, Is.SameAs(original), "nothing is replaced automatically");
            Assert.That(changes, Is.Empty);
            // 写しのまま: 知らせない。もう一度変われば、また知らせる
            window.KeepResourceCopy(image.Id);
            Assert.That(window.ChangedResources, Is.Empty);
            var third = Pattern(32, 32, 3);
            File.WriteAllBytes(Path.GetFullPath(path), RgbaPng.Encode(third, 32, 32)); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(window.CheckResourceSources(), Is.EqualTo(new[] { image }), "a new change is reported again");
            // 更新: 同じ ID のまま中身が変わり、使う側に知らされる
            Assert.That(window.UpdateResourceFromSource(image.Id), Is.True);
            Assert.That(image.Content.CopyPixels(), Is.EqualTo(third)); Assert.That(image.Revision, Is.EqualTo(1));
            Assert.That(image.Origin.SourceStamp, Is.EqualTo(AssetDatabase.GetAssetDependencyHash(path).ToString()));
            Assert.That(changes.Select(c => (c.Kind, c.Id)), Is.EqualTo(new[] { (ResourceChangeKind.ContentReplaced, image.Id) }));
            Assert.That(window.CheckResourceSources(), Is.Empty);
            Assert.That(window.UpdateResourceFromSource(image.Id), Is.False, "nothing to update");
            // 元が消えても写しで使える
            AssetDatabase.DeleteAsset(path);
            window.CheckResourceSources();
            Assert.That(window.ResourceSource(image.Id).State, Is.EqualTo(TexturePaintWindow.ResourceSourceState.Missing));
            int layers = window.Document.Layers.Count;
            window.PlaceResourceAsLayer(image.Id);
            Assert.That(window.Document.Layers.Count, Is.EqualTo(layers + 1));
        }

        [Test] public void OpeningAProjectWhoseSourceChangedAsksBeforeUpdating([Values(false, true)] bool update)
        {
            var texture = MakeTexture("Asked", Pattern(16, 16, 1), 16, 16, i => i.isReadable = true);
            string path = AssetDatabase.GetAssetPath(texture);
            var image = window.ImportUnityTexture(texture);
            dialogs.File = Temp(".ylp"); window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var next = Pattern(16, 16, 2);
            File.WriteAllBytes(Path.GetFullPath(path), RgbaPng.Encode(next, 16, 16)); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var other = NewWindow(); var asked = (Dialogs)other.Dialogs; asked.ConfirmAnswer = update;
            other.OpenProjectAt(dialogs.File);
            Assert.That(asked.Asked, Is.EqualTo(new[] { "Confirm: Resources changed" }), other.StatusMessage);
            var opened = other.ImageResources.Images.Single();
            Assert.That(opened.Id, Is.EqualTo(image.Id));
            Assert.That(opened.Content.CopyPixels(), Is.EqualTo(update ? next : Pattern(16, 16, 1)));
            Assert.That(other.IsSaved, Is.EqualTo(!update), "updating is an unsaved change; keeping is not");
            Assert.That(other.StatusMessage, Does.Contain(update ? "Updated 1 resource" : "copies in this project are kept"));
            Assert.That(other.ChangedResources.Count, Is.Zero, "kept copies are not reported again in this session");
        }

        // ───────── 保存・開く・復旧 ─────────

        [Test] public void ResourcesSaveOpenAndRecover()
        {
            var texture = MakeTexture("Saved", Pattern(24, 20, 4), 24, 20, i => i.isReadable = true);
            var fromUnity = window.ImportUnityTexture(texture);
            var builtIn = window.ImportBuiltInImage("grid");
            string file = Temp(".png"); File.WriteAllBytes(file, RgbaPng.Encode(Pattern(10, 12, 6), 10, 12));
            var fromFile = window.ImportImageFile(file);
            window.RenameResource(builtIn.Id, "Grid for decals");
            dialogs.File = Temp(".ylp"); window.SaveProject(true);
            Assert.That(window.IsSaved, Is.True, window.StatusMessage);
            var saved = YlpFormat.Open(YlpStore.Load(dialogs.File).Files);
            Assert.That(saved.Info.Format, Is.EqualTo(4)); Assert.That(saved.Resources.Count, Is.EqualTo(3));
            Assert.That(saved.Files.Keys.Count(k => k.StartsWith(ResourceIndex.Folder, StringComparison.Ordinal)), Is.EqualTo(3));

            var other = NewWindow(); other.OpenProjectAt(dialogs.File);
            Assert.That(other.ImageResources.Images.Select(r => (r.Id, r.Name, r.ContentHash, r.Origin.Kind, r.ColorSpace)),
                Is.EqualTo(window.ImageResources.Images.Select(r => (r.Id, r.Name, r.ContentHash, r.Origin.Kind, r.ColorSpace))));
            Assert.That(other.ImageResources.Get(fromFile.Id).Origin.Path, Is.EqualTo(Path.GetFullPath(file)));
            Assert.That(other.IsSaved, Is.True, "opening is not an edit");
            Assert.That(((Dialogs)other.Dialogs).Asked, Is.Empty, "nothing changed in the sources, nothing asked");
            // 新しいプロジェクトはリソース無し
            other.CreateProject(new NewProjectSettings { Model = null, Resolution = 512 });
            Assert.That(other.ImageResources.Count, Is.Zero);

            // 復旧の checkpoint（フォーカスを失ったとき）から別の窓が戻す（ドメインのリロードと同じ道）
            window.GetType().GetMethod("OnLostFocus", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
            window.ImportBuiltInImage("value-noise");
            window.GetType().GetMethod("OnLostFocus", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
            var restored = NewWindow(); var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            restored.GetType().GetMethod("OnDisable", flags).Invoke(restored, null);
            string own = restored.RecoveryRoot; if (Directory.Exists(own)) Directory.Delete(own, true);
            restored.GetType().GetField("recoveryRoot", flags).SetValue(restored, window.RecoveryRoot);
            restored.GetType().GetMethod("OnEnable", flags).Invoke(restored, null);
            Assert.That(restored.StatusMessage, Does.StartWith("Recovered"));
            Assert.That(restored.ImageResources.Images.Select(r => (r.Id, r.ContentHash)), Is.EqualTo(window.ImageResources.Images.Select(r => (r.Id, r.ContentHash))), "the resource added after saving is recovered too");
        }

        [Test] public void ABrokenResourceRefusesToOpenAndLeavesTheProjectAsItWas()
        {
            window.ImportBuiltInImage("radial-gradient");
            dialogs.File = Temp(".ylp"); window.SaveProject(true);
            var files = YlpStore.Load(dialogs.File).Files;
            var image = window.ImageResources.Images.Single();
            files[ResourceIndex.ContentEntry(image.ContentHash)] = BuiltInImages.Make("grid").EncodePng(); // 名前と違う画素（manifest は合わせ直す）
            string broken = Temp(".ylp"); YlpStore.Save(broken, files, null, false);
            var other = NewWindow(); other.ImportBuiltInImage("uv-checker");
            var before = other.Document; long revision = other.ImageResources.Revision;
            other.OpenProjectAt(broken);
            Assert.That(other.StatusMessage, Does.Contain("is broken"));
            Assert.That(other.Document, Is.SameAs(before), "the open project is kept"); Assert.That(other.ImageResources.Revision, Is.EqualTo(revision));
            Assert.That(other.ImageResources.Images.Single().Origin.BuiltInKey, Is.EqualTo("uv-checker"));
        }

        // ───────── 置く ─────────

        [Test] public void PlacingAResourceIsOneUndoStepInTheCurrentChannel()
        {
            var d = window.Document;
            d.SetSelection(SelectionMask.Rectangle(d, 10, 10, 50, 50));
            var checker = window.ImportBuiltInImage("uv-checker"); // 1024² を 512² の文書へ: 面積平均
            var below = window.SelectedLayer; int count = d.Layers.Count; long saved = d.Revision;
            var id = window.PlaceResourceAsLayer(checker.Id);
            var layer = d.GetLayer(id);
            Assert.That(d.Layers.Count, Is.EqualTo(count + 1)); Assert.That(window.SelectedLayer, Is.EqualTo(id));
            Assert.That(d.Layers.ToList().IndexOf(layer), Is.EqualTo(d.Layers.ToList().IndexOf(d.GetLayer(below)) + 1), "above the selected layer");
            Assert.That(layer.Name, Is.EqualTo(checker.Name));
            Assert.That(d.Selection, Is.Not.Null, "placing does not deselect");
            var expected = checker.Content.Resampled(512, 512, CanvasResampling.Area);
            Assert.That(LayerPixels(d, layer, PaintChannel.Color), Is.EqualTo(expected), "the whole layer, not only the selection");
            Assert.That(window.StatusMessage, Does.Contain("resized from 1024 × 1024"));
            Assert.That(d.Undo(), Is.True);
            Assert.That(d.Layers.Count, Is.EqualTo(count)); Assert.That(d.Layers.Any(l => l.Id == id), Is.False, "one undo step removes it");
            Assert.That(d.Redo(), Is.True); Assert.That(LayerPixels(d, d.GetLayer(id), PaintChannel.Color), Is.EqualTo(expected));
            // データのチャンネルでは、そのチャンネルだけ
            window.Channel = PaintChannel.Roughness;
            var noise = window.ImportBuiltInImage("value-noise");
            var data = d.GetLayer(window.PlaceResourceAsLayer(noise.Id));
            Assert.That(data.IsChannelEnabled(PaintChannel.Roughness), Is.True); Assert.That(data.IsChannelEnabled(PaintChannel.Color), Is.False);
            // 同じ大きさはそのまま（透明画素の RGB も）
            var exact = new byte[512 * 512 * 4]; new System.Random(3).NextBytes(exact);
            string file = Temp(".png"); File.WriteAllBytes(file, RgbaPng.Encode(exact, 512, 512));
            var same = window.ImportImageFile(file);
            window.Channel = PaintChannel.Color;
            Assert.That(LayerPixels(d, d.GetLayer(window.PlaceResourceAsLayer(same.Id)), PaintChannel.Color), Is.EqualTo(exact));
            // リソースを後で消しても層の画素は残る
            dialogs.ConfirmAnswer = true; Assert.That(window.RemoveResource(checker.Id), Is.True);
            Assert.That(LayerPixels(d, d.GetLayer(id), PaintChannel.Color), Is.EqualTo(expected));
        }

        [Test] public void PlacingBeyondTheOneOperationBudgetIsRefused()
        {
            var settings = PainterSettings.PersonalSettings; settings.strokeBudgetMiB = PainterSettings.MinStrokeMiB; PainterSettings.Save(null, settings);
            window.CreateProject(new NewProjectSettings { Model = null, Resolution = 2048 }); // 16 MiB > 8 MiB
            var image = window.ImportBuiltInImage("grid");
            int count = window.Document.Layers.Count; bool undo = window.Document.CanUndo;
            Assert.That(() => window.PlaceResourceAsLayer(image.Id), Throws.TypeOf<ResourceRefusedException>().With.Property("Refusal").EqualTo(ResourceRefusal.OverBudget).And.Message.Contains("one-operation budget"));
            Assert.That((window.Document.Layers.Count, window.Document.CanUndo), Is.EqualTo((count, undo)), "nothing placed, no history");
        }

        static byte[] LayerPixels(PaintDocument d, PaintLayer layer, PaintChannel channel)
        {
            var image = new byte[d.Width * d.Height * 4];
            if (!layer.TryGetChannel(channel, out var surface)) return image;
            int tile = d.TileSize; var bytes = new byte[tile * tile * 4];
            foreach (var coord in surface.EnumerateTileCoordinates())
            {
                if (!surface.CopyTile(coord, bytes)) continue;
                int w = Math.Min(tile, d.Width - coord.X * tile), h = Math.Min(tile, d.Height - coord.Y * tile);
                for (int y = 0; y < h; y++) Buffer.BlockCopy(bytes, y * tile * 4, image, ((coord.Y * tile + y) * d.Width + coord.X * tile) * 4, w * 4);
            }
            return image;
        }

        // ───────── 自分の置き場・画像のファイル・消す ─────────

        [Test] public void TheLibraryKeepsExactPixelsAndIsSharedByName()
        {
            var image = window.ImportBuiltInImage("radial-gradient");
            string name = window.AddResourceToLibrary(image.Id);
            string path = Path.Combine(PainterSettings.LibraryFolder, name);
            Assert.That(path, Does.StartWith(Path.Combine(project, "UserSettings", "YoluPainter", "Library")), "the default library is in the project's UserSettings");
            Assert.That(RgbaPng.Decode(File.ReadAllBytes(path)).rgba, Is.EqualTo(image.Content.CopyPixels()));
            Assert.That(window.AddResourceToLibrary(image.Id), Is.EqualTo(name), "the same bytes are not written twice");
            Assert.That(window.StatusMessage, Does.Contain("already in your library"));
            Assert.That(Directory.GetFiles(PainterSettings.LibraryFolder).Length, Is.EqualTo(1));
            Assert.That(ResourceLibraryFolder.List(PainterSettings.LibraryFolder).Single().FileName, Is.EqualTo(name));
            // 別のプロジェクト（同じ置き場）で取り込む
            window.CreateProject(new NewProjectSettings { Model = null, Resolution = 512 });
            var again = window.ImportLibraryImage(name);
            Assert.That(again.Origin.Kind, Is.EqualTo(ResourceOriginKind.Library)); Assert.That(again.Origin.Path, Is.EqualTo(name));
            Assert.That(again.ContentHash, Is.EqualTo(image.ContentHash));
            // 置き場のファイルが変われば知らせる
            File.WriteAllBytes(path, RgbaPng.Encode(Pattern(8, 8, 1), 8, 8));
            Assert.That(window.CheckResourceSources(), Is.EqualTo(new[] { again }));
            // 置き場の名前は SafeStem で作る
            Assert.That(ResourceLibraryFolder.SafeStem("a/b:c*?.\u0001"), Is.EqualTo("a_b_c__._"));
            Assert.That(ResourceLibraryFolder.SafeStem("  "), Is.EqualTo("Image"));
            // 設定の検査: Assets の下は断る
            Assert.That(PainterSettings.CheckLibraryFolder("Assets/Lib"), Does.Contain("library folder"));
            Assert.That(PainterSettings.CheckLibraryFolder("Assets/Lib~"), Is.Null);
        }

        [Test] public void ImageFilesKeepTheirPathAndJpegIsDecodedByUnity()
        {
            string png = Temp(".png"); var rgba = Pattern(9, 7, 2); File.WriteAllBytes(png, RgbaPng.Encode(rgba, 9, 7));
            var a = window.ImportImageFile(png);
            Assert.That(a.Content.CopyPixels(), Is.EqualTo(rgba)); Assert.That(a.Origin.Kind, Is.EqualTo(ResourceOriginKind.File));
            Assert.That(a.Origin.SourceStamp, Is.EqualTo(GenerationStore.Hash(File.ReadAllBytes(png)))); Assert.That(a.ColorSpace, Is.EqualTo(ResourceColorSpace.Unspecified));
            var texture = new Texture2D(8, 8, TextureFormat.RGB24, false);
            string jpg = Temp(".jpg");
            try { texture.SetPixels32(Enumerable.Range(0, 64).Select(i => new Color32((byte)(i * 4), 100, 50, 255)).ToArray()); texture.Apply(); File.WriteAllBytes(jpg, texture.EncodeToJPG(90)); }
            finally { Object.DestroyImmediate(texture); }
            var b = window.ImportImageFile(jpg);
            Assert.That((b.Width, b.Height), Is.EqualTo((8, 8))); Assert.That(window.StatusMessage, Does.Contain("decoded by Unity"));
            string text = Temp(".txt"); File.WriteAllText(text, "x");
            Assert.That(() => window.ImportImageFile(text), Throws.TypeOf<ResourceRefusedException>());
            string fake = Temp(".png"); File.WriteAllText(fake, "not an image");
            Assert.That(() => window.ImportImageFile(fake), Throws.TypeOf<InvalidDataException>());
            File.Delete(png);
            window.CheckResourceSources();
            Assert.That(window.ResourceSource(a.Id).State, Is.EqualTo(TexturePaintWindow.ResourceSourceState.Missing));
        }

        [Test] public void RemovingAsksFirstAndAResourceInUseStays()
        {
            var image = window.ImportBuiltInImage("grid");
            dialogs.File = Temp(".ylp"); window.SaveProject(true);
            dialogs.ConfirmAnswer = false;
            Assert.That(window.RemoveResource(image.Id), Is.False); Assert.That(window.ImageResources.Count, Is.EqualTo(1)); Assert.That(window.IsSaved, Is.True);
            Func<Guid, string> probe = id => "decal \"Logo\"";
            window.ImageResources.AddUsageProbe(probe);
            dialogs.ConfirmAnswer = true;
            Assert.That(() => window.RemoveResource(image.Id), Throws.TypeOf<ResourceRefusedException>().With.Message.Contains("Logo"));
            window.ImageResources.RemoveUsageProbe(probe);
            Assert.That(window.RemoveResource(image.Id), Is.True);
            Assert.That(window.ImageResources.Count, Is.Zero); Assert.That(window.IsSaved, Is.False, "removing is an unsaved change");
            Assert.That(dialogs.Asked.Count(a => a == "Confirm: Remove resource?"), Is.EqualTo(2));
        }
    }
}
