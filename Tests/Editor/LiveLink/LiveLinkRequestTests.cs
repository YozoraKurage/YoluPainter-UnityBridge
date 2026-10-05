using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core.Shelf;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.LiveLink;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// Live Link の頼み: スタンドアロンが「このマテリアルの値・元の絵がほしい」と頼み、Unity は頼まれたものだけを読んで送る。元の絵は、頼みを出せる
    /// スタンドアロン（機能の印 マテリアルの頼み が双方にある）には自分から押し出さない。頼みの have（スタンドアロンが手元に持つ絵の印）が今の印と同じなら
    /// 絵を読まずに画素なしの Cached で答え、印が違う・取れない（アセットでない絵）ときは必ず画素を送る。世代が違う頼みは答えない。
    /// 試験のアセットは試験が作ったフォルダの中だけで、終わりに消す。
    /// </summary>
    public sealed class LiveLinkRequestTests
    {
        static int s_counter;
        readonly List<Object> owned = new List<Object>();
        readonly List<string> folders = new List<string>();
        ulong server;
        LiveLinkSession session;

        const ulong AllMarks = LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures | LiveLinkBridge.FeatureMaterialRequest;

        [SetUp]
        public void Require()
        {
            Assert.That(LiveLinkBridge.Problem, Is.Null, "the bridge library must load on the Linux and Windows editors");
            L.OverrideLanguage(PainterLanguage.English);
        }

        [TearDown]
        public void CleanUp()
        {
            session?.Dispose(); session = null;
            LiveLinkTestServer.Stop(server); server = 0;
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
            foreach (var f in folders) AssetDatabase.DeleteAsset(f);
            folders.Clear();
            L.OverrideLanguage(null);
        }

        T Own<T>(T o) where T : Object { owned.Add(o); return o; }

        static string UniqueName() => "ylp-unity-request-" + Process.GetCurrentProcess().Id + "-" + (++s_counter);

        static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("needs a graphics device (the textures are read through a RenderTexture)");
            if (EditorShaderCompiler.IsBroken) Assert.Ignore("Built-in shaders fail to compile in this Editor (devcontainer GUI mode). Use test-daemon.sh start --batch-gl.");
        }

        void Connect(ulong features)
        {
            string name = UniqueName();
            server = LiveLinkTestServer.Start(name, 128, 64);
            Assert.That(server, Is.Not.EqualTo(0UL));
            Assert.That(LiveLinkTestServer.Configure(server, new Version(0, 4, 0), null, features), Is.True);
            session = LiveLinkSession.Start(name);
            Pump(() => session.Status == LiveLinkStatus.Connected, "the connection");
        }

        void Pump(Func<bool> done, string what, double seconds = 15)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.Elapsed.TotalSeconds > seconds) Assert.Fail("timed out waiting for " + what + " (" + session?.StatusText + ")");
                session?.Tick();
                Thread.Sleep(5);
            }
        }

        string NewFolder()
        {
            string folder = "Assets/YoluPainterRequests-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder)), Is.Not.Empty);
            folders.Add(folder);
            return folder;
        }

        /// <summary>画素 (x, y)（y は下から）が [x * 50 + seed, y * 80, (x + y) * 20, 255] の、4 × 3 の絵。</summary>
        static byte[] Picture(byte seed = 0)
        {
            var rgba = new byte[4 * 3 * 4];
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 4; x++)
                {
                    int o = (y * 4 + x) * 4;
                    rgba[o] = (byte)(x * 50 + seed); rgba[o + 1] = (byte)(y * 80); rgba[o + 2] = (byte)((x + y) * 20); rgba[o + 3] = 255;
                }
            return rgba;
        }

        /// <summary>ファイルを書いて取り込み、インポート設定を決める（絵を変えない設定）。</summary>
        Texture2D Import(string folder, string file, byte[] png, out string path)
        {
            path = folder + "/" + file;
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.mipmapEnabled = false; importer.alphaIsTransparency = false; importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.npotScale = TextureImporterNPOTScale.None; importer.isReadable = false; importer.sRGBTexture = true;
            importer.SaveAndReimport();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.That(texture, Is.Not.Null, path);
            return texture;
        }

        /// <summary>差し替えたファイルを取り込み直して、新しいテクスチャを返す。</summary>
        static Texture2D Replace(string path, byte[] png)
        {
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>テクスチャごとに 1 つのキューブ（それぞれ別のマテリアル。_MainTex に入れる）を持つモデル。</summary>
        GameObject ModelOf(params Texture[] textures)
        {
            var root = Own(new GameObject("LiveLinkRequestModel"));
            root.transform.position = new Vector3(900, 900, 900);
            for (int i = 0; i < textures.Length; i++)
            {
                var cube = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
                cube.transform.SetParent(root.transform, false);
                cube.transform.localPosition = new Vector3(i * 2, 0, 0);
                var material = Own(new Material(Shader.Find("Standard")) { name = "RequestBody" + i });
                material.SetTexture("_MainTex", textures[i]);
                cube.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            return root;
        }

        Texture2D Solid(Color32 color, int size = 16)
        {
            var t = Own(new Texture2D(size, size, TextureFormat.RGBA32, false, false) { name = "solid" });
            t.SetPixels32(Enumerable.Repeat(color, size * size).ToArray());
            t.Apply(false, true);
            return t;
        }

        static void WaitFor(Func<bool> done, string what, double seconds = 15)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.Elapsed.TotalSeconds > seconds) Assert.Fail("timed out waiting for " + what);
                Thread.Sleep(5);
            }
        }

        static uint Pack(byte r, byte g, byte b, byte a) => (uint)r | (uint)g << 8 | (uint)b << 16 | (uint)a << 24;

        // ───────── 押し出さない・頼まれた分だけ送る ─────────

        [Test]
        public void ARequestingStandaloneIsNotPushedTheOriginalsAndAsksForTheSetsItHolds()
        {
            RequireGraphics();
            var root = ModelOf(Solid(new Color32(200, 30, 10, 255)), Solid(new Color32(10, 200, 30, 255)));
            Connect(AllMarks);
            Assert.That(session.CommonFeatures & LiveLinkBridge.FeatureMaterialRequest, Is.Not.EqualTo(0UL));
            Assert.That(session.SendModel(root), Is.Null);
            Assert.That(session.OriginalsPending, Is.False, "頼みを出せるスタンドアロンには、元の絵を押し出さない（読みもしない）");
            // スタンドアロン役は、元の絵を待たせたセットを自分から頼む。頼みに答えて読み・送る
            Pump(() => session.Display.AppliedCount > 0, "the sets with the originals");
            var stats = LiveLinkTestServer.Stats(server);
            Assert.That((stats.requests, stats.last_request_items), Is.EqualTo((1u, 2u)), "1 つの頼みに 2 つのマテリアル");
            Assert.That(session.RequestsReceived, Is.EqualTo(2));
            Assert.That(session.RequestsIgnored, Is.EqualTo(0));
            Assert.That((stats.originals, stats.held_sets), Is.EqualTo((2u, 0u)));
            Assert.That(session.OriginalsSent, Is.EqualTo(2));
            for (int m = 0; m < 2; m++)
            {
                Assert.That(LiveLinkTestServer.Original(server, m, "_MainTex", out var o), Is.True);
                Assert.That(o.state, Is.EqualTo((uint)LiveLinkOriginalState.Image));
                Assert.That(o.stamp, Is.EqualTo(0UL), "アセットでない絵には印が無い（必ず画素を送る）");
            }
        }

        [Test]
        public void OnlyTheRequestedMaterialIsReadAndSentAndTheOthersAreLeftAlone()
        {
            RequireGraphics();
            var root = ModelOf(Solid(new Color32(200, 30, 10, 255)), Solid(new Color32(10, 200, 30, 255)), Solid(new Color32(30, 10, 200, 255)));
            Connect(AllMarks);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount > 0 && LiveLinkTestServer.Stats(server).originals >= 3, "the first originals");
            uint before = LiveLinkTestServer.Stats(server).originals;
            int sentBefore = session.OriginalsSent;
            // 手でマテリアル 1 だけを頼む（世代 0 は今のモデルの世代）
            Assert.That(LiveLinkTestServer.Request(server, 0, 1, LiveLinkRequest.WantOriginal, "_MainTex", 0), Is.True);
            Pump(() => session.OriginalsSent == sentBefore + 1, "the requested original");
            WaitFor(() => LiveLinkTestServer.Stats(server).originals == before + 1, "the answer");
            Thread.Sleep(100);
            Assert.That(LiveLinkTestServer.Stats(server).originals, Is.EqualTo(before + 1), "頼まれたマテリアルだけ");
            Assert.That(LiveLinkTestServer.Stats(server).last_original_material, Is.EqualTo(1u));
            Assert.That(session.OriginalsSent, Is.EqualTo(sentBefore + 1));
        }

        [Test]
        public void ARequestOfAnotherGenerationIsIgnoredAndNothingIsRead()
        {
            RequireGraphics();
            var root = ModelOf(Solid(new Color32(200, 30, 10, 255)));
            Connect(AllMarks);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount > 0, "the set");
            uint before = LiveLinkTestServer.Stats(server).originals;
            int ignored = session.RequestsIgnored;
            Assert.That(LiveLinkTestServer.Request(server, 4242, 0, LiveLinkRequest.WantOriginal | LiveLinkRequest.WantValues, "_MainTex", 0), Is.True);
            Pump(() => session.RequestsIgnored == ignored + 1, "the stale request to be taken out");
            for (int i = 0; i < 20; i++) { session.Tick(); Thread.Sleep(5); }
            Assert.That(LiveLinkTestServer.Stats(server).originals, Is.EqualTo(before), "古いモデルの頼みには答えない");
            Assert.That(session.OriginalsPending, Is.False);
            Assert.That(session.ValuesResent, Is.EqualTo(0));
        }

        [Test]
        public void ARequestForASlotWithoutATextureIsAnsweredAsUnreadableAndAnOutOfRangeMaterialIsNot()
        {
            RequireGraphics();
            var root = ModelOf(Solid(new Color32(200, 30, 10, 255)));
            Connect(AllMarks);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount > 0, "the set");
            // スロットが違う（絵が外された・名前が違う）: 読めない様子で答える（頼んだ側を待たせ続けない）
            Assert.That(LiveLinkTestServer.Request(server, 0, 0, LiveLinkRequest.WantOriginal, "_OtherTex", 0), Is.True);
            Pump(() => LiveLinkTestServer.Original(server, 0, "_OtherTex", out _), "the unreadable answer");
            Assert.That(LiveLinkTestServer.Original(server, 0, "_OtherTex", out var gone), Is.True);
            Assert.That((gone.state, gone.width, gone.height), Is.EqualTo(((uint)LiveLinkOriginalState.Unreadable, 0u, 0u)));
            // 範囲外のマテリアルの番号には答えない（受けて捨てる）
            uint before = LiveLinkTestServer.Stats(server).originals;
            Assert.That(LiveLinkTestServer.Request(server, 0, 99, LiveLinkRequest.WantOriginal, "_MainTex", 0), Is.True);
            Pump(() => session.RequestsReceived >= 2, "the second request");
            for (int i = 0; i < 20; i++) { session.Tick(); Thread.Sleep(5); }
            Assert.That(LiveLinkTestServer.Stats(server).originals, Is.EqualTo(before));
            Assert.That(session.OriginalsPending, Is.False);
        }

        // ───────── 印 ─────────

        [Test]
        public void AnAssetTextureHasAStampThatFollowsItsContentsAndImportSettingsAndItsFileTimeAndNonAssetsHaveNone()
        {
            RequireGraphics();
            string folder = NewFolder();
            var texture = Import(folder, "Body.png", RgbaPng.Encode(Picture(), 4, 3), out string path);
            ulong stamp = LiveLinkOriginals.StampOf(texture);
            Assert.That(stamp, Is.Not.EqualTo(0UL), "アセットの絵には印がある");
            Assert.That(LiveLinkOriginals.StampOf(texture), Is.EqualTo(stamp), "何も変えなければ同じ");
            // 中身が変わる（別の絵に差し替えて取り込み直す）
            var replaced = Replace(path, RgbaPng.Encode(Picture(9), 4, 3));
            ulong changed = LiveLinkOriginals.StampOf(replaced);
            Assert.That(changed, Is.Not.EqualTo(0UL));
            Assert.That(changed, Is.Not.EqualTo(stamp), "中身が変わったら印も変わる");
            // インポート設定が変わる（絵の読み方が変わる設定）
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false; importer.SaveAndReimport();
            ulong linear = LiveLinkOriginals.StampOf(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            Assert.That(linear, Is.Not.EqualTo(changed), "インポート設定が変わったら印も変わる");
            // 取り込み直さずにファイルだけが触られた（外のツールが書いた直後）: 古い絵と言い切れないので、印が変わる
            ulong beforeTouch = LiveLinkOriginals.StampOf(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(3));
            Assert.That(LiveLinkOriginals.StampOf(AssetDatabase.LoadAssetAtPath<Texture2D>(path)), Is.Not.EqualTo(beforeTouch), "ファイルが触られたら、取り込み直す前でも印が変わる");
            // アセットでない絵・無い絵・2D でない絵は印なし（必ず画素を送る）
            Assert.That(LiveLinkOriginals.StampOf(Solid(new Color32(1, 2, 3, 255))), Is.EqualTo(0UL));
            Assert.That(LiveLinkOriginals.StampOf(Own(new RenderTexture(8, 8, 0))), Is.EqualTo(0UL));
            Assert.That(LiveLinkOriginals.StampOf(null), Is.EqualTo(0UL));
        }

        [Test]
        public void ASameStampIsAnsweredWithoutPixelsAndAChangedFileIsSentAgainAndANonAssetAlwaysSendsPixels()
        {
            RequireGraphics();
            string folder = NewFolder();
            var texture = Import(folder, "Body.png", RgbaPng.Encode(Picture(), 4, 3), out string path);
            var runtime = Solid(new Color32(200, 30, 10, 255));
            var root = ModelOf(texture, runtime);
            Connect(AllMarks);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount > 0 && LiveLinkTestServer.Stats(server).originals >= 2, "the first originals");
            // 最初は手元に何も無い: アセットの絵は印つきの画素で、アセットでない絵は印なしの画素で届く
            Assert.That(LiveLinkTestServer.Original(server, 0, "_MainTex", out var first), Is.True);
            Assert.That(first.state, Is.EqualTo((uint)LiveLinkOriginalState.Image));
            Assert.That(first.stamp, Is.EqualTo(LiveLinkOriginals.StampOf(texture)), "送った印は、読む前に取った今の印");
            Assert.That(LiveLinkTestServer.Original(server, 1, "_MainTex", out var second), Is.True);
            Assert.That(second.stamp, Is.EqualTo(0UL));
            uint kibAfterFirst = LiveLinkTestServer.Stats(server).original_kib;

            // モデルを送り直す: スタンドアロン役は、手元の絵の印（アセットの絵だけ）を頼みに付ける。同じ印なら画素なしで答える。印なしの絵は画素をまた送る
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => LiveLinkTestServer.Stats(server).cached_used == 1 && LiveLinkTestServer.Stats(server).originals >= 4, "the answers of the second send");
            var stats = LiveLinkTestServer.Stats(server);
            Assert.That(session.OriginalsCached, Is.EqualTo(1), "印が同じ絵は、読まずに答えた");
            Assert.That(stats.cached_missed, Is.EqualTo(0u));
            Assert.That(LiveLinkTestServer.Original(server, 0, "_MainTex", out var used), Is.True);
            Assert.That((used.state, used.stamp), Is.EqualTo(((uint)LiveLinkOriginalState.Image, first.stamp)), "手元の絵を使った（画素は最初のもの）");
            Assert.That(used.corner, Is.EqualTo(Pack(0, 0, 0, 255)));
            // 増えた画素は、アセットでない絵の分だけ（16 × 16 × 4 = 1 KiB）
            Assert.That(stats.original_kib, Is.EqualTo(kibAfterFirst + 1), "アセットの絵の画素は送っていない");

            // ファイルが差し替わった: 手元の絵の印と今の印が違うので、画素を送る（古い絵を新しい印で使わない）
            Replace(path, RgbaPng.Encode(Picture(9), 4, 3));
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => LiveLinkTestServer.Stats(server).originals >= 6 && LiveLinkTestServer.Original(server, 0, "_MainTex", out var o) && o.corner == Pack(9, 0, 0, 255), "the new picture");
            Assert.That(LiveLinkTestServer.Original(server, 0, "_MainTex", out var changed), Is.True);
            Assert.That(changed.state, Is.EqualTo((uint)LiveLinkOriginalState.Image));
            Assert.That(changed.stamp, Is.Not.EqualTo(first.stamp));
            Assert.That(changed.stamp, Is.EqualTo(LiveLinkOriginals.StampOf(AssetDatabase.LoadAssetAtPath<Texture2D>(path))));
            Assert.That(LiveLinkTestServer.Stats(server).cached_used, Is.EqualTo(1u), "2 回目の送りで 1 つだけ。3 回目は使っていない");
            Assert.That(session.OriginalsCached, Is.EqualTo(1));
        }

        [Test]
        public void AStampThatCannotBeTakenSendsThePixelsEvenWhenTheStandaloneSaysItHasOne()
        {
            RequireGraphics();
            var root = ModelOf(Solid(new Color32(5, 6, 7, 255)));
            Connect(AllMarks);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount > 0, "the set");
            // スタンドアロンが（でたらめな）印を持つと言っても、印の取れない絵は読んで送る（印が合うことはない）
            uint before = LiveLinkTestServer.Stats(server).originals;
            int sent = session.OriginalsSent;
            Assert.That(LiveLinkTestServer.Request(server, 0, 0, LiveLinkRequest.WantOriginal, "_MainTex", 0x1234_5678_9abc), Is.True);
            Pump(() => session.OriginalsSent == sent + 1, "the pixels");
            WaitFor(() => LiveLinkTestServer.Stats(server).originals == before + 1, "the answer");
            Assert.That(session.OriginalsCached, Is.EqualTo(0));
            Assert.That(LiveLinkTestServer.Original(server, 0, "_MainTex", out var o), Is.True);
            Assert.That((o.state, o.corner), Is.EqualTo(((uint)LiveLinkOriginalState.Image, Pack(5, 6, 7, 255))));
        }

        // ───────── 値 ─────────

        [Test]
        public void AValuesRequestSendsTheValuesOfTheRequestedMaterialAgainAndAMaterialWithoutOneIsAnsweredAsNone()
        {
            RequireGraphics();
            var root = ModelOf(Solid(new Color32(200, 30, 10, 255)), Solid(new Color32(10, 200, 30, 255)));
            // マテリアルの無いサブメッシュの組（マテリアルは 3 つめ）
            var bare = Own(GameObject.CreatePrimitive(PrimitiveType.Sphere));
            bare.transform.SetParent(root.transform, false);
            bare.GetComponent<MeshRenderer>().sharedMaterial = null;
            Connect(AllMarks);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount > 0, "the sets");
            int unassigned = session.Model.Materials.FindIndex(m => m.Material == null);
            Assert.That(unassigned, Is.GreaterThanOrEqualTo(0));
            // モデルの直後に送った値（マテリアルの無い組は送らない。2 つのマテリアルぶん）が届き終わってから、数え始める
            Pump(() => LiveLinkTestServer.Stats(server).values >= 2, "the values sent with the model");
            Thread.Sleep(50);
            uint values = LiveLinkTestServer.Stats(server).values;
            // 頼まれたマテリアルだけの値を送り直す（Standard なので「値なし」）
            Assert.That(LiveLinkTestServer.Request(server, 0, 1, LiveLinkRequest.WantValues, "", 0), Is.True);
            Pump(() => session.ValuesResent == 1, "the values request");
            WaitFor(() => LiveLinkTestServer.Stats(server).values == values + 1, "the values answer");
            Assert.That(LiveLinkTestServer.Stats(server).last_values_material, Is.EqualTo(1u));
            Assert.That(LiveLinkTestServer.Stats(server).last_values_kind, Is.EqualTo(0u), "lilToon でないマテリアルは値なし");
            // マテリアルの無い組にも、値なしと答える（頼んだ側の待ちを閉じる）
            Assert.That(LiveLinkTestServer.Request(server, 0, (uint)unassigned, LiveLinkRequest.WantValues, "", 0), Is.True);
            Pump(() => session.ValuesResent == 2, "the values request of the bare submeshes");
            WaitFor(() => LiveLinkTestServer.Stats(server).values == values + 2, "the answer for the bare submeshes");
            Assert.That(LiveLinkTestServer.Stats(server).last_values_material, Is.EqualTo((uint)unassigned));
            Assert.That(LiveLinkTestServer.Stats(server).last_values_kind, Is.EqualTo(0u));
            Assert.That(LiveLinkTestServer.Stats(server).refused, Is.EqualTo(0u));
            Assert.That(session.Log.Any(l => l.Contains("asked for the values of 1 materials")), Is.True);
        }

        // ───────── 古い相手・大きすぎるモデル ─────────

        [Test]
        public void AStandaloneWithoutTheRequestMarkIsPushedTheOriginalsAsBefore()
        {
            RequireGraphics();
            var root = ModelOf(Solid(new Color32(1, 2, 3, 255)));
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            Assert.That(session.CommonFeatures & LiveLinkBridge.FeatureMaterialRequest, Is.EqualTo(0UL));
            Assert.That(session.SendModel(root), Is.Null);
            Assert.That(session.OriginalsPending, Is.True, "頼みを知らない相手には、今までどおり全部を押し出す");
            Pump(() => session.Display.AppliedCount > 0, "the set");
            var stats = LiveLinkTestServer.Stats(server);
            Assert.That((stats.requests, stats.originals, session.RequestsReceived), Is.EqualTo((0u, 1u, 0)));
            // 頼みを知らない相手には、頼みの印が無いので届かない
            Assert.That(LiveLinkTestServer.Request(server, 0, 0, LiveLinkRequest.WantOriginal, "_MainTex", 0), Is.False);
        }

        [Test]
        public void AModelOverTheFrameLimitIsRefusedWithAReasonAndNothingIsSentAndTheLinkStaysUp()
        {
            RequireGraphics();
            var root = ModelOf(Solid(new Color32(1, 2, 3, 255)));
            Connect(AllMarks);
            LiveLinkTestServer.SetPayloadLimit(session.Handle, 100);
            string problem = session.SendModel(root);
            Assert.That(problem, Is.Not.Null);
            Assert.That(problem, Does.Contain("larger than the 512 MiB"));
            Assert.That(session.ModelsSent, Is.EqualTo(0));
            Assert.That(session.Status, Is.EqualTo(LiveLinkStatus.Connected), "つながりは保たれる");
            for (int i = 0; i < 10; i++) { session.Tick(); Thread.Sleep(5); }
            Assert.That(LiveLinkTestServer.Stats(server).models, Is.EqualTo(0u));
            L.OverrideLanguage(PainterLanguage.Japanese);
            Assert.That(session.SendModel(root), Does.Contain("512 MiB"));
            L.OverrideLanguage(PainterLanguage.English);
            // 上限を戻せば、同じモデルを送れる
            LiveLinkTestServer.SetPayloadLimit(session.Handle, ulong.MaxValue);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => LiveLinkTestServer.Stats(server).models == 1, "the model");
        }

        [Test]
        public void TheRequestMarkHasItsNameInBothLanguages()
        {
            Assert.That(LiveLinkFeatures.Names(LiveLinkFeatures.MaterialRequest), Is.EqualTo("Material requests"));
            L.OverrideLanguage(PainterLanguage.Japanese);
            Assert.That(LiveLinkFeatures.Names(LiveLinkFeatures.MaterialRequest), Is.EqualTo("マテリアルの頼み"));
            Assert.That(LiveLinkFeatures.MaterialRequest, Is.EqualTo(LiveLinkBridge.FeatureMaterialRequest));
            Assert.That(LiveLinkFeatures.Known & LiveLinkFeatures.MaterialRequest, Is.Not.EqualTo(0UL));
            Assert.That(LiveLinkBridge.ExpectedAbi, Is.EqualTo(6u));
        }
    }
}
