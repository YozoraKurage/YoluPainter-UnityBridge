using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.LiveLink;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// Live Link の受け口: ブリッジの DLL（Plugins/LiveLink）の版の問い合わせ、ブリッジの中の自己診断のスタンドアロン（本物のソケットと
    /// 共有メモリ）にモデルを送って模様が返り、PropertyBlock でレンダラーに当たり、元のマテリアルとテクスチャが変わらず、切ると外れること。
    /// 試験のモデルは試験が作ったゲームオブジェクトとマテリアルだけ。
    /// </summary>
    public sealed class LiveLinkTests
    {
        static int s_counter;
        readonly List<Object> owned = new List<Object>();
        ulong server;
        LiveLinkSession session;

        [SetUp]
        public void RequireBridge()
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
            L.OverrideLanguage(null);
        }

        static string UniqueName() => "ylp-unity-test-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + (++s_counter);

        void Pump(Func<bool> done, string what, double seconds = 10)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.Elapsed.TotalSeconds > seconds) Assert.Fail("timed out waiting for " + what + " (" + session?.StatusText + ")");
                session?.Tick();
                Thread.Sleep(5);
            }
        }

        static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("needs a graphics device (the shaders and the material bindings)");
        }

        T Own<T>(T o) where T : Object { owned.Add(o); return o; }

        /// <summary>テクスチャ（RenderTexture でも Texture2D でも）の、ミップ 1 つぶんの生の画素を GPU から読む（変換しない。行は下から）。</summary>
        static Color32[] Read(Texture t, int mip = 0)
        {
            var request = AsyncGPUReadback.Request(t, mip);
            request.WaitForCompletion();
            Assert.That(request.hasError, Is.False, "readback of " + t.name);
            return request.GetData<Color32>().ToArray();
        }

        static Color32 Px(Texture t, int x, int y, int mip = 0) => Read(t, mip)[y * Math.Max(1, t.width >> mip) + x];

        /// <summary>根の下に 2 つの立方体（Unlit/Texture と Standard のマテリアル）。</summary>
        GameObject Model(out MeshRenderer unlit, out MeshRenderer standard, out Material unlitMaterial, out Texture2D original)
        {
            var root = Own(new GameObject("LiveLinkTestModel"));
            root.transform.position = new Vector3(500, 500, 500);
            original = Own(new Texture2D(4, 4) { name = "original" });
            unlitMaterial = Own(new Material(Shader.Find("Unlit/Texture")) { name = "UnlitMat" });
            unlitMaterial.SetTexture("_MainTex", original);
            var standardMaterial = Own(new Material(Shader.Find("Standard")) { name = "StandardMat" });
            var a = Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); a.name = "A"; a.transform.SetParent(root.transform, false);
            var b = Own(GameObject.CreatePrimitive(PrimitiveType.Cube)); b.name = "B"; b.transform.SetParent(root.transform, false); b.transform.localPosition = new Vector3(2, 0, 0);
            unlit = a.GetComponent<MeshRenderer>(); unlit.sharedMaterial = unlitMaterial;
            standard = b.GetComponent<MeshRenderer>(); standard.sharedMaterial = standardMaterial;
            return root;
        }

        void Connect(int size, int tile)
        {
            string name = UniqueName();
            server = LiveLinkTestServer.Start(name, size, tile);
            Assert.That(server, Is.Not.EqualTo(0UL));
            session = LiveLinkSession.Start(name);
            Pump(() => session.Status == LiveLinkStatus.Connected, "the connection");
        }

        [Test]
        public void TheBridgeAnswersItsVersionFirst()
        {
            Assert.That(LiveLinkBridge.AbiVersion, Is.EqualTo(LiveLinkBridge.ExpectedAbi));
            Assert.That(LiveLinkBridge.ProtocolVersions, Is.EqualTo((1, 1)));
        }

        [Test]
        public void AModelComesBackAsAPatternOnPropertyBlocksAndDisconnectingRemovesThem()
        {
            RequireGraphics();
            var root = Model(out var unlit, out var standard, out var unlitMaterial, out var original);
            int materialDirty = EditorUtility.GetDirtyCount(unlitMaterial), rendererDirty = EditorUtility.GetDirtyCount(unlit);
            Assert.That(unlit.HasPropertyBlock(), Is.False);
            Connect(256, 64);

            Assert.That(session.SendModel(root), Is.Null);
            Assert.That(session.Model.Materials.Select(m => m.Name), Is.EqualTo(new[] { "UnlitMat", "StandardMat" }));
            Assert.That(session.Model.Materials.All(m => m.Shown.Any(c => c.Channel == PaintChannel.Color && c.Property == "_MainTex")), Is.True);
            Pump(() => session.Display.AppliedCount == 2 && session.Display.Sets.Count() == 2 && session.Display.Sets.All(s => s.Uploads > 0), "both texture sets on the renderers");

            var block = new MaterialPropertyBlock();
            unlit.GetPropertyBlock(block, 0);
            var shown = block.GetTexture("_MainTex") as RenderTexture;
            Assert.That(shown, Is.Not.Null.And.Not.SameAs(original));
            Assert.That(shown.width, Is.EqualTo(256));
            Assert.That(shown.sRGB, Is.EqualTo(QualitySettings.activeColorSpace == ColorSpace.Linear), "Color is sRGB where the colour space is linear (in gamma space nothing is converted)");
            Assert.That(shown.useMipMap && shown.mipmapCount == 9, Is.True, "the GPU keeps the mip chain (256 → 1)");
            // 市松の模様（マテリアル 0、タイル (1, 0) は半分の明るさ、(2, 2) は元の色）
            Assert.That(Px(shown, 64 + 3, 3), Is.EqualTo(LiveLinkTestServer.Pattern(0, 1, 0)));
            Assert.That(Px(shown, 128 + 9, 128 + 9), Is.EqualTo(LiveLinkTestServer.Pattern(0, 2, 2)));
            standard.GetPropertyBlock(block, 0);
            Assert.That(Px(block.GetTexture("_MainTex"), 5, 5), Is.EqualTo(LiveLinkTestServer.Pattern(1, 0, 0)));

            // 元のマテリアル・テクスチャ・レンダラーは変わらない
            Assert.That(unlitMaterial.GetTexture("_MainTex"), Is.SameAs(original));
            Assert.That(unlit.sharedMaterial, Is.SameAs(unlitMaterial));
            Assert.That(EditorUtility.GetDirtyCount(unlitMaterial), Is.EqualTo(materialDirty));
            Assert.That(EditorUtility.GetDirtyCount(unlit), Is.EqualTo(rendererDirty));

            // 1 タイルだけ塗ると、そのタイルだけが届く（帯で上げられるなら帯）
            var red = new Color32(250, 10, 20, 255);
            Assert.That(LiveLinkTestServer.Paint(server, 0, PaintChannel.Color, 2, 1, 3, 2, red), Is.EqualTo(1));
            Pump(() => Px(shown, 2 * 64 + 1, 64 + 1).Equals(red), "the painted tile");
            Assert.That(session.Display.LastUpload.Tiles, Is.EqualTo(1));
            Assert.That(session.Display.LastUpload.Strip, Is.EqualTo(LiveLinkDisplay.CopySupported));
            Assert.That(Px(shown, 64 + 1, 64 + 1), Is.EqualTo(LiveLinkTestServer.Pattern(0, 1, 1)), "the neighbour stays");

            // レンダラーを動かすと、変わったメッシュのポーズを送る（毎フレームではなく変わったときだけ）
            int before = (int)LiveLinkTestServer.Stats(server).poses;
            standard.transform.localPosition += new Vector3(0, 1, 0);
            Pump(() => LiveLinkTestServer.Stats(server).poses > before, "the pose");
            var stats = LiveLinkTestServer.Stats(server);
            Assert.That(stats.pose_meshes, Is.EqualTo(1), "only the moved renderer");
            Assert.That(stats.vertices, Is.EqualTo(48u), "two cubes of 24 vertices");
            Thread.Sleep(300); session.Tick(); session.CheckModel();
            Assert.That(LiveLinkTestServer.Stats(server).poses, Is.EqualTo(stats.poses), "nothing moved, nothing sent");

            // 切ると外れる
            session.Dispose(); session = null;
            Assert.That(unlit.HasPropertyBlock(), Is.False);
            Assert.That(standard.HasPropertyBlock(), Is.False);
            Assert.That(shown == null, Is.True, "the Live Link texture is released");
            Assert.That(unlitMaterial.GetTexture("_MainTex"), Is.SameAs(original));
        }

        [Test]
        public void APropertyBlockThatWasAlreadyThereComesBack()
        {
            RequireGraphics();
            var root = Model(out var unlit, out _, out _, out _);
            var mine = new MaterialPropertyBlock();
            mine.SetColor("_Color", Color.green);
            unlit.SetPropertyBlock(mine, 0);
            Connect(128, 64);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount == 2, "the property blocks");
            var block = new MaterialPropertyBlock();
            unlit.GetPropertyBlock(block, 0);
            Assert.That(block.GetTexture("_MainTex"), Is.Not.Null);
            Assert.That(block.GetColor("_Color"), Is.EqualTo(Color.green), "the user's value is kept while shown");
            session.CloseModel();
            unlit.GetPropertyBlock(block, 0);
            Assert.That(block.GetColor("_Color"), Is.EqualTo(Color.green));
            Assert.That(block.GetTexture("_MainTex"), Is.Null, "only the user's block is back");
        }

        [Test]
        public void ASkinnedMeshAndANonReadableMeshAreReadAndAMovedBoneSendsThePose()
        {
            RequireGraphics();
            var root = Own(new GameObject("LiveLinkSkinModel"));
            root.transform.position = new Vector3(-500, 0, 0);
            var material = Own(new Material(Shader.Find("Unlit/Texture")) { name = "SkinMat" });
            var cube = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
            var source = cube.GetComponent<MeshFilter>().sharedMesh;
            Vector3 first = source.vertices[0];
            // Read/Write の無いメッシュ（エディタの API で読む）
            var plain = Own(Object.Instantiate(source)); plain.UploadMeshData(true);
            Assert.That(plain.isReadable, Is.False);
            var still = Own(new GameObject("Still")); still.transform.SetParent(root.transform, false); still.transform.localPosition = new Vector3(3, 0, 0);
            still.AddComponent<MeshFilter>().sharedMesh = plain; still.AddComponent<MeshRenderer>().sharedMaterial = material;
            // 骨 1 本のスキンメッシュ（これも Read/Write 無し）
            var skinned = Own(Object.Instantiate(source));
            var weights = new BoneWeight[skinned.vertexCount];
            for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1 };
            skinned.boneWeights = weights; skinned.bindposes = new[] { Matrix4x4.identity };
            skinned.UploadMeshData(true);
            var skin = Own(new GameObject("Skin")); skin.transform.SetParent(root.transform, false);
            var bone = Own(new GameObject("Bone")).transform; bone.SetParent(skin.transform, false);
            var smr = skin.AddComponent<SkinnedMeshRenderer>(); smr.sharedMesh = skinned; smr.bones = new[] { bone }; smr.rootBone = bone; smr.sharedMaterial = material;
            Object.DestroyImmediate(cube);
            Connect(128, 64);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => LiveLinkTestServer.Stats(server).models == 1, "the model");
            var stats = LiveLinkTestServer.Stats(server);
            Assert.That((stats.meshes, stats.vertices), Is.EqualTo((2u, 48u)));
            Assert.That(session.Model.Meshes.Select(m => m.Skinned), Is.EquivalentTo(new[] { true, false }));
            int skinIndex = session.Model.Meshes.FindIndex(m => m.Skinned);

            bone.localPosition = new Vector3(0, 2, 0);
            Pump(() => LiveLinkTestServer.Stats(server).poses == 1, "the pose");
            stats = LiveLinkTestServer.Stats(server);
            Assert.That(stats.pose_meshes, Is.EqualTo(1), "only the skinned mesh changed");
            Assert.That(skinIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(stats.last_pose_x, Is.EqualTo(first.x).Within(1e-4));
            Assert.That(stats.last_pose_y, Is.EqualTo(first.y + 2).Within(1e-4), "the baked position, in the root's local space");
            Assert.That(stats.last_pose_z, Is.EqualTo(first.z).Within(1e-4));
            Assert.That(smr.sharedMesh, Is.SameAs(skinned), "the renderer is not changed");
        }

        [Test]
        public void APrefabAssetOrAnEmptyObjectIsRefused()
        {
            Connect(128, 64);
            var empty = Own(new GameObject("Empty"));
            Assert.That(session.SendModel(empty), Does.Contain("no active mesh renderers"));
            Assert.That(session.SendModel(null), Does.Contain("No GameObject"));
        }

        [Test]
        public void TheRenderedColorIsThePattern()
        {
            RequireGraphics();
            var root = Model(out var unlit, out var standard, out _, out _);
            standard.gameObject.SetActive(false);
            const int layer = 31;
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            Connect(256, 64);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount == 1 && session.Display.Sets.All(s => s.Uploads > 0), "the texture on the cube");
            var cameraObject = Own(new GameObject("LiveLinkTestCamera"));
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false; camera.cullingMask = 1 << layer; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            camera.transform.position = unlit.transform.position + new Vector3(0, 0, -3); camera.transform.LookAt(unlit.transform.position);
            camera.fieldOfView = 20;
            // 面が画像いっぱいに写る（ほぼ等倍で、ミップを使わない）。画面の中心は UV (0.5, 0.5) でタイルの角なので、タイルの真ん中を読む
            const int size = 256;
            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var read = Own(new Texture2D(size, size, TextureFormat.RGBA32, false));
            try
            {
                camera.targetTexture = rt; camera.Render();
                var active = RenderTexture.active; RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, size, size), 0, 0); read.Apply();
                RenderTexture.active = active;
            }
            finally { camera.targetTexture = null; rt.Release(); Object.DestroyImmediate(rt); }
            var pixels = read.GetPixelData<Color32>(0);
            // 面の幅は画像の 1.14 倍ほど。UV で 0.125 は 36 画素: タイル (2, 2)・(1, 1) の真ん中（明るい色）と、(2, 1) の真ん中（半分の明るさ）
            foreach (var (x, y, tx, ty) in new[] { (128 + 36, 128 + 36, 2u, 2u), (128 - 36, 128 - 36, 1u, 1u), (128 + 36, 128 - 36, 2u, 1u) })
            {
                var got = pixels[y * size + x];
                var expected = LiveLinkTestServer.Pattern(0, tx, ty);
                Assert.That(CloseTo(got, expected, 3), Is.True, "tile (" + tx + ", " + ty + "): " + got + " vs " + expected);
            }
        }

        bool DisplayIdle() => session.Display.Sets.All(v => v.Channels.Keys.All(c => LiveLinkNative.ylb_channel_dirty(session.Handle, v.Info.set, (int)c) == 0));

        [Test]
        public void TheGpuMakesTheMipmapsAndTheyFollowTheTiles()
        {
            RequireGraphics();
            var root = Model(out var unlit, out var standard, out _, out _);
            standard.gameObject.SetActive(false);
            Connect(256, 64);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount == 1 && session.Display.Sets.All(s => s.Uploads > 0) && DisplayIdle(), "the whole pattern");
            var shown = session.Display.Sets.First().Channels[PaintChannel.Color].Texture;
            Assert.That(shown, Is.InstanceOf<RenderTexture>());
            Assert.That(shown.filterMode, Is.EqualTo(FilterMode.Trilinear));
            Assert.That(CloseTo(Px(shown, 32, 32, 2), LiveLinkTestServer.Pattern(0, 2, 2), 2), Is.True, "mip 2 (64²) at the middle of the tile (2, 2)");
            Assert.That(CloseTo(Px(shown, 4, 4, 4), LiveLinkTestServer.Pattern(0, 1, 1), 2), Is.True, "mip 4 (16²) inside the tile (1, 1)");
            // 1 タイルを塗ると、ミップにも届く
            var red = new Color32(250, 10, 20, 255);
            Assert.That(LiveLinkTestServer.Paint(server, 0, PaintChannel.Color, 2, 2, 3, 3, red), Is.EqualTo(1));
            Pump(() => CloseTo(Px(shown, 32, 32, 2), red, 2), "the painted tile in mip 2");
            Assert.That(CloseTo(Px(shown, 128 + 5, 128 + 5, 0), red, 0), Is.True, "mip 0 is exact");
        }

        static bool CloseTo(Color32 a, Color32 b, int tolerance) => Math.Abs(a.r - b.r) <= tolerance && Math.Abs(a.g - b.g) <= tolerance && Math.Abs(a.b - b.b) <= tolerance && Math.Abs(a.a - b.a) <= tolerance;

        [Test]
        public void AWholeUpdateIsSpreadOverSeveralUpdatesAndEveryTileArrives()
        {
            RequireGraphics();
            var root = Model(out _, out var standard, out _, out _);
            standard.gameObject.SetActive(false);
            double budget = LiveLinkDisplay.BudgetMs;
            try
            {
                Connect(1024, 128);   // 64 tiles: two strips of 32
                Assert.That(session.SendModel(root), Is.Null);
                Pump(() => session.Display.AppliedCount == 1 && session.Display.Sets.All(s => s.Uploads > 0) && DisplayIdle(), "the first whole pattern");
                LiveLinkDisplay.BudgetMs = 0;   // 1 回の更新で帯 1 本だけ
                long uploads = session.Display.UploadCount, tilesBefore = session.Display.TilesUploaded;
                var blue = new Color32(10, 20, 240, 255);
                Assert.That(LiveLinkTestServer.Paint(server, 0, PaintChannel.Color, 0, 0, 8, 8, blue), Is.EqualTo(64));
                var view = session.Display.Sets.First();
                var clock = Stopwatch.StartNew();
                // 知らせが届いて、全部が上がるまで（1 回の Tick で増える記録は 1 つまで）
                while (session.Display.UploadCount == uploads || !DisplayIdle())
                {
                    long count = session.Display.UploadCount;
                    session.Tick();
                    Assert.That(session.Display.UploadCount - count, Is.LessThanOrEqualTo(1), "one strip per update with a zero budget");
                    if (clock.Elapsed.TotalSeconds > 30) Assert.Fail("the update did not finish");
                    Thread.Sleep(1);
                }
                var records = session.Display.History.Skip(Math.Max(0, session.Display.History.Count - (int)(session.Display.UploadCount - uploads))).ToList();
                Assert.That(session.Display.UploadCount - uploads, Is.GreaterThanOrEqualTo(2), "64 tiles do not fit in one strip of " + LiveLinkDisplay.StripTiles);
                Assert.That(records.All(r => r.Tiles <= LiveLinkDisplay.StripTiles && r.Strip), Is.True);
                Assert.That(session.Display.TilesUploaded - tilesBefore, Is.EqualTo(64));
                var pixels = Read(view.Channels[PaintChannel.Color].Texture);
                foreach (var (tx, ty) in new[] { (0, 0), (7, 7), (3, 5), (7, 0) }) Assert.That(pixels[(ty * 128 + 9) * 1024 + tx * 128 + 9], Is.EqualTo(blue), "tile " + tx + "," + ty);
            }
            finally { LiveLinkDisplay.BudgetMs = budget; }
        }

        /// <summary>Standard のマテリアル 1 つで、Color・Normal・Emission・Metallic・Roughness の 5 チャンネルを見せるモデル（Metallic と Roughness は _MetallicGlossMap に詰め直す）。</summary>
        GameObject FiveChannelModel()
        {
            var root = Model(out var unlit, out var standard, out _, out _);
            unlit.gameObject.SetActive(false);
            foreach (var keyword in new[] { "_NORMALMAP", "_EMISSION", "_METALLICGLOSSMAP" }) standard.sharedMaterial.EnableKeyword(keyword);
            return root;
        }

        static readonly PaintChannel[] FiveChannels = { PaintChannel.Color, PaintChannel.Normal, PaintChannel.Emission, PaintChannel.Metallic, PaintChannel.Roughness };

        /// <summary>全タイルが汚れ、ミップも作り終えて落ち着いている。</summary>
        bool Quiet() => DisplayIdle() && session.Display.Settled && session.Display.Sets.All(v => v.Channels.Values.All(c => !c.MipsStale));

        [TestCase(0.0)]
        [TestCase(1.5)]
        public void SeveralChannelsTakeTurnsAndThePackedTextureIsBuiltOnceWhenTheWholeUpdateSettles(double budgetMs)
        {
            RequireGraphics();
            var root = FiveChannelModel();
            double budget = LiveLinkDisplay.BudgetMs;
            try
            {
                Connect(1024, 128);   // 8 × 8 タイル。塗るのは 8 × 5 = 40 タイル（チャンネルごとに帯 2 本）
                Assert.That(session.SendModel(root), Is.Null);
                Assert.That(session.Model.Materials[0].Shown.Select(c => c.Channel), Is.EquivalentTo(FiveChannels));
                Pump(() => session.Display.AppliedCount == 1 && session.Display.Sets.All(s => s.Uploads > 0) && Quiet(), "the first whole pattern", 60);
                var view = session.Display.Sets.First();
                Assert.That(view.HasPacked, Is.True, "Metallic and Roughness are packed into _MetallicGlossMap");

                LiveLinkDisplay.BudgetMs = budgetMs;
                int builds = session.Display.PackBuilds;
                long uploads = session.Display.UploadCount;
                var paint = new Dictionary<PaintChannel, Color32>
                {
                    { PaintChannel.Color, new Color32(10, 20, 240, 255) }, { PaintChannel.Normal, new Color32(130, 120, 250, 255) }, { PaintChannel.Emission, new Color32(200, 100, 10, 255) },
                    { PaintChannel.Metallic, new Color32(60, 60, 60, 255) }, { PaintChannel.Roughness, new Color32(100, 100, 100, 255) },
                };
                foreach (var pair in paint) Assert.That(LiveLinkTestServer.Paint(server, 0, pair.Key, 0, 0, 8, 5, pair.Value), Is.EqualTo(40));
                // 5 つのチャンネルの全タイルの知らせが届いてから、更新を 1 回ずつ進める
                var clock = Stopwatch.StartNew();
                while (!paint.Keys.All(c => LiveLinkNative.ylb_channel_dirty(session.Handle, view.Info.set, (int)c) == 40))
                {
                    if (clock.Elapsed.TotalSeconds > 20) Assert.Fail("the notifications did not arrive");
                    Thread.Sleep(1);
                }
                var order = new List<PaintChannel>(); bool backlog = false;
                while (order.Count == 0 || !Quiet())
                {
                    long count = session.Display.UploadCount;
                    session.Tick();
                    backlog |= !session.Display.Settled;
                    int added = (int)(session.Display.UploadCount - count);
                    order.AddRange(session.Display.History.Skip(session.Display.History.Count - added).Select(r => r.Channel));
                    if (clock.Elapsed.TotalSeconds > 60) Assert.Fail("the update did not finish");
                    Thread.Sleep(1);
                }
                Assert.That(session.Display.UploadCount - uploads, Is.EqualTo(10), "two strips per channel");
                if (budgetMs == 0)
                {
                    Assert.That(backlog, Is.True, "one strip per update leaves a backlog");
                    Assert.That(order.Take(5).Distinct().Count(), Is.EqualTo(5), "every channel gets a strip in the first five updates, not the first channel's two strips first: " + string.Join(",", order));
                }
                Assert.That(session.Display.PackBuilds - builds, Is.EqualTo(1), "the packed texture is built once, when the update settles");
                Assert.That(view.Channels.Values.All(c => !c.MipsStale), Is.True);
                foreach (var pair in paint.Where(p => p.Key != PaintChannel.Metallic && p.Key != PaintChannel.Roughness))
                {
                    var texture = view.Channels[pair.Key].Texture;
                    Assert.That(Px(texture, 9, 9), Is.EqualTo(pair.Value), pair.Key + " mip 0");
                    Assert.That(CloseTo(Px(texture, 4, 4, 3), pair.Value, 2), Is.True, pair.Key + " mip 3 follows the tile");
                }
                var packed = view.Packed["_MetallicGlossMap"];
                Assert.That(Px(packed, 9, 9), Is.EqualTo(new Color32(60, 60, 60, 155)), "R = Metallic, A = 1 − Roughness");
                Assert.That(CloseTo(Px(packed, 4, 4, 3), new Color32(60, 60, 60, 155), 2), Is.True, "the packed mip chain follows");
                Assert.That(Px(packed, 9, 128 * 6 + 9), Is.EqualTo(new Color32(200, 200, 200, 255 - 200)), "an unpainted tile keeps the standalone's value");
            }
            finally { LiveLinkDisplay.BudgetMs = budget; }
        }

        [Test]
        public void EdgeTilesOfATextureThatIsNotAMultipleOfTheTileAreInPlaceAndTheirNeighboursStayIntact()
        {
            RequireGraphics();
            var root = Model(out _, out var standard, out _, out _);
            standard.gameObject.SetActive(false);
            Connect(200, 64);   // 4 × 4 タイル。右端の列と上端の行は 8 画素ぶんだけが画像の中
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount == 1 && session.Display.Sets.All(s => s.Uploads > 0) && Quiet(), "the whole pattern");
            var shown = session.Display.Sets.First().Channels[PaintChannel.Color].Texture;
            Assert.That((shown.width, shown.height), Is.EqualTo((200, 200)));
            Color32 Pattern(int x, int y) => LiveLinkTestServer.Pattern(0, (uint)(x / 64), (uint)(y / 64));
            var pixels = Read(shown);
            foreach (var (x, y) in new[] { (191, 5), (192, 5), (199, 5), (5, 191), (5, 192), (5, 199), (199, 199), (191, 191), (192, 191), (191, 192), (0, 199), (199, 0), (192, 64), (192, 63) })
                Assert.That(pixels[y * 200 + x], Is.EqualTo(Pattern(x, y)), "pixel (" + x + ", " + y + ")");
            // 端のタイル（右端・上端・角）を塗ると、そのタイルの画像の中の所だけが変わり、隣は壊れない
            var red = new Color32(250, 10, 20, 255);
            foreach (var (tx, ty) in new[] { (3, 1), (1, 3), (3, 3) }) Assert.That(LiveLinkTestServer.Paint(server, 0, PaintChannel.Color, (uint)tx, (uint)ty, (uint)tx + 1, (uint)ty + 1, red), Is.EqualTo(1));
            Pump(() => Quiet() && Px(shown, 199, 199).Equals(red) && Px(shown, 199, 69).Equals(red) && Px(shown, 69, 199).Equals(red), "the painted edge tiles");
            pixels = Read(shown);
            bool Painted(int x, int y) => (x / 64, y / 64) is (3, 1) or (1, 3) or (3, 3);
            foreach (var (x, y) in new[] { (192, 64), (199, 69), (199, 127), (192, 127), (64, 192), (69, 199), (127, 199), (199, 192), (192, 192), (199, 199) })
                Assert.That(pixels[y * 200 + x], Is.EqualTo(red), "inside a painted tile (" + x + ", " + y + ")");
            foreach (var (x, y) in new[] { (191, 69), (69, 191), (191, 199), (199, 191), (191, 191), (63, 199), (63, 192), (199, 63), (192, 63), (199, 128), (128, 199) })
            {
                Assert.That(Painted(x, y), Is.False, "the check itself");
                Assert.That(pixels[y * 200 + x], Is.EqualTo(Pattern(x, y)), "neighbour (" + x + ", " + y + ") stays");
            }
        }

        [Test]
        public void WithoutCopyTextureTheWholeTexture2DIsUploadedWithItsMipmaps()
        {
            RequireGraphics();
            var root = Model(out _, out var standard, out _, out _);
            standard.gameObject.SetActive(false);
            LiveLinkDisplay.AllowCopyTexture = false;
            try
            {
                Connect(256, 64);
                Assert.That(session.SendModel(root), Is.Null);
                Pump(() => session.Display.AppliedCount == 1 && session.Display.Sets.All(s => s.Uploads > 0) && DisplayIdle(), "the whole pattern");
                var shown = session.Display.Sets.First().Channels[PaintChannel.Color].Texture;
                Assert.That(shown, Is.InstanceOf<Texture2D>());
                Assert.That(shown.mipmapCount, Is.EqualTo(9));
                Assert.That(Px(shown, 64 + 3, 3), Is.EqualTo(LiveLinkTestServer.Pattern(0, 1, 0)));
                Assert.That(CloseTo(Px(shown, 32, 32, 2), LiveLinkTestServer.Pattern(0, 2, 2), 2), Is.True, "Apply makes the mip chain");
                var red = new Color32(250, 10, 20, 255);
                Assert.That(LiveLinkTestServer.Paint(server, 0, PaintChannel.Color, 2, 2, 3, 3, red), Is.EqualTo(1));
                Pump(() => Px(shown, 2 * 64 + 1, 2 * 64 + 1).Equals(red), "the painted tile");
                Assert.That(session.Display.LastUpload.Strip, Is.False, "the whole texture is uploaded");
                Assert.That(CloseTo(Px(shown, 32, 32, 2), red, 2), Is.True);
            }
            finally { LiveLinkDisplay.AllowCopyTexture = true; }
        }

        [Test]
        public void ARenderTextureLostOnTheGpuIsBuiltAgainAndFilledAgain()
        {
            RequireGraphics();
            var root = Model(out var unlit, out var standard, out _, out _);
            standard.gameObject.SetActive(false);
            Connect(256, 64);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount == 1 && session.Display.Sets.All(s => s.Uploads > 0) && DisplayIdle(), "the whole pattern");
            var rt = (RenderTexture)session.Display.Sets.First().Channels[PaintChannel.Color].Texture;
            rt.Release();
            Assert.That(rt.IsCreated(), Is.False);
            Pump(() => rt.IsCreated() && DisplayIdle() && Px(rt, 64 + 3, 3).Equals(LiveLinkTestServer.Pattern(0, 1, 0)), "the texture to be refilled");
        }

        [Test]
        public void ChangingAShaderOrAKeywordSendsOnlyTheMaterialsAndShowsTheNewChannels()
        {
            RequireGraphics();
            var root = Model(out var unlit, out var standard, out _, out _);
            unlit.gameObject.SetActive(false);
            Connect(128, 64);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount == 1 && LiveLinkTestServer.Stats(server).models == 1, "the model");
            var material = standard.sharedMaterial;
            Assert.That(session.Model.Materials[0].Shown.Select(c => c.Channel), Has.No.Member(PaintChannel.Normal), "the normal map keyword is off");
            // キーワードを入れる: モデルは送り直さず、マテリアルの更新だけが届き、法線が見せられるチャンネルに入る
            material.EnableKeyword("_NORMALMAP");
            Pump(() => LiveLinkTestServer.Stats(server).materials_updates == 1, "the material update");
            var stats = LiveLinkTestServer.Stats(server);
            Assert.That((stats.models, stats.refused), Is.EqualTo((1u, 0u)), "the model is not sent again");
            Assert.That(session.MaterialUpdatesSent, Is.EqualTo(1));
            Assert.That(session.Model.Materials[0].Shown.Select(c => c.Channel), Has.Member(PaintChannel.Normal));
            Assert.That(stats.last_materials_count, Is.EqualTo(1u));
            Assert.That(session.Display.AppliedCount, Is.EqualTo(1), "the property block is shown again");
            // シェーダーを替える
            material.shader = Shader.Find("Unlit/Texture");
            Pump(() => LiveLinkTestServer.Stats(server).materials_updates == 2, "the second material update");
            stats = LiveLinkTestServer.Stats(server);
            Assert.That(stats.last_materials_shader_len, Is.EqualTo((uint)"Unlit/Texture".Length));
            Assert.That((stats.models, stats.refused), Is.EqualTo((1u, 0u)));
            Thread.Sleep(400); session.Tick(); session.CheckModel();
            Assert.That(LiveLinkTestServer.Stats(server).materials_updates, Is.EqualTo(2u), "nothing changed, nothing sent");
        }

        [Test]
        public void AStandaloneWhoseKeyDoesNotMatchIsRefusedWithTheReason()
        {
            string name = UniqueName();
            server = LiveLinkTestServer.Start(name, 64, 64);
            Assert.That(server, Is.Not.EqualTo(0UL));
            // 鍵のファイルが別の（古い）ものになっている状態
            Assert.That(LiveLinkTestServer.ReplaceKey(server), Is.True);
            session = LiveLinkSession.Start(name);
            Pump(() => session.Status == LiveLinkStatus.Failed, "the refusal");
            Assert.That(session.StatusText, Does.Contain("鍵"));
            Assert.That(session.SendModel(Own(new GameObject("Empty"))), Is.Not.Null, "nothing is sent");
            Assert.That(LiveLinkTestServer.Stats(server).models, Is.EqualTo(0u));
        }

        [Test]
        public void ANameNobodyListensOnFailsWithoutBlockingTheEditor()
        {
            var clock = Stopwatch.StartNew();
            session = LiveLinkSession.Start(UniqueName());
            Assert.That(clock.ElapsedMilliseconds, Is.LessThan(500), "connecting does not wait");
            Pump(() => session.Status == LiveLinkStatus.Failed, "the failure");
            Assert.That(session.StatusText, Is.Not.Empty);
        }

        /// <summary>
        /// 計測（明示して回す）: 4096² のテクスチャセットで、1 タイルと全面の更新を、共有メモリ → 帯 → GPU（帯の Apply と CopyTexture）→ ミップまで。
        /// 主スレッドの Tick ごとの時間（全面は数回の Tick に分かれる）と、塗ってから GPU が上げ終えるまでの時間。値はコンテナの WSL の GL
        /// （Mesa d3d12）でのもので、Windows の D3D11 のものではない。
        /// </summary>
        [Test, Explicit("measurement")]
        public void MeasureUploads4096()
        {
            RequireGraphics();
            var root = Model(out _, out var standard, out _, out _);
            standard.gameObject.SetActive(false);
            Connect(4096, 128);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount == 1 && session.Display.Sets.All(s => s.Uploads > 0) && DisplayIdle(), "the first full upload", 120);
            var rt = (RenderTexture)session.Display.Sets.First().Channels[PaintChannel.Color].Texture;
            var names = new[] { "one tile", "full" };
            var results = new Dictionary<string, List<double[]>> { { "one tile", new List<double[]>() }, { "full", new List<double[]>() } };
            for (int run = 0; run < 7; run++)
            {
                foreach (bool full in new[] { false, true })
                {
                    var color = new Color32((byte)(run * 30), (byte)(full ? 200 : 50), 99, 255);
                    int expectedTiles = full ? 1024 : 1;
                    var wall = Stopwatch.StartNew();
                    if (full) LiveLinkTestServer.Paint(server, 0, PaintChannel.Color, 0, 0, 32, 32, color);
                    else LiveLinkTestServer.Paint(server, 0, PaintChannel.Color, 16, 16, 17, 17, color);
                    double paintMs = wall.Elapsed.TotalMilliseconds;
                    var ticks = new List<double>(); long tiles = 0;
                    while (tiles < expectedTiles || !DisplayIdle())
                    {
                        long count = session.Display.UploadCount, tilesBefore = session.Display.TilesUploaded;
                        var tick = Stopwatch.StartNew();
                        session.Tick();
                        double ms = tick.Elapsed.TotalMilliseconds;
                        if (session.Display.UploadCount > count) { ticks.Add(ms); tiles += session.Display.TilesUploaded - tilesBefore; }
                        if (wall.Elapsed.TotalSeconds > 120) Assert.Fail("the upload did not finish");
                        Thread.Sleep(1);
                    }
                    // GPU が上げ終えたことを確かめる（一番小さいミップを読み戻す）
                    AsyncGPUReadback.Request(rt, rt.mipmapCount - 1).WaitForCompletion();
                    double wallMs = wall.Elapsed.TotalMilliseconds;
                    var rec = session.Display.LastUpload;
                    if (run > 0) results[full ? "full" : "one tile"].Add(new[] { paintMs, ticks.Max(), ticks.Sum(), ticks.Count, wallMs, rec.CopyMs, rec.UploadMs });
                }
            }
            foreach (var name in names)
            {
                var list = results[name];
                double Median(int i) { var v = list.Select(x => x[i]).OrderBy(x => x).ToList(); return v[v.Count / 2]; }
                Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "LiveLink 4096² {0} (strips of up to {1} tiles + CopyTexture + GPU mips, budget {2} ms): write+notify {3:0.00} ms; main thread per update: max {4:0.00} ms, total {5:0.00} ms over {6} updates; painted→GPU done {7:0.00} ms; last strip: shm→CPU {8:0.00} ms, upload {9:0.00} ms (median of {10}, {11} {12})",
                    name, LiveLinkDisplay.StripTiles, LiveLinkDisplay.BudgetMs, Median(0), Median(1), Median(2), Median(3), Median(4), Median(5), Median(6), list.Count, SystemInfo.graphicsDeviceType, SystemInfo.graphicsDeviceName));
            }
        }

        /// <summary>
        /// 計測（明示して回す）: 4096² のテクスチャセットで、Color・Normal・Emission・Metallic・Roughness（Metallic と Roughness は _MetallicGlossMap へ詰め直す）の
        /// 全面を同時に更新するときの、主スレッドの Tick ごとの時間（帯の上げ・ミップ・詰め直しとバインドを含む）の最大と合計、塗ってから GPU が落ち着くまでの時間。
        /// 値はコンテナの WSL の GL（Mesa d3d12）でのもので、Windows の D3D11 のものではない。
        /// </summary>
        [Test, Explicit("measurement")]
        public void MeasureFiveChannelUpdates4096()
        {
            RequireGraphics();
            var root = FiveChannelModel();
            Connect(4096, 128);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.Display.AppliedCount == 1 && session.Display.Sets.All(s => s.Uploads > 0) && Quiet(), "the first full upload", 300);
            var view = session.Display.Sets.First();
            var rows = new List<double[]>();
            for (int run = 0; run < 4; run++)
            {
                var wall = Stopwatch.StartNew();
                int builds = session.Display.PackBuilds;
                foreach (var c in FiveChannels) LiveLinkTestServer.Paint(server, 0, c, 0, 0, 32, 32, new Color32((byte)(run * 40 + 10), (byte)(30 + (int)c * 20), 99, 255));
                while (!FiveChannels.All(c => LiveLinkNative.ylb_channel_dirty(session.Handle, view.Info.set, (int)c) == 1024))
                {
                    if (wall.Elapsed.TotalSeconds > 60) Assert.Fail("the notifications did not arrive");
                    Thread.Sleep(1);
                }
                var ticks = new List<double>();
                while (ticks.Count == 0 || !Quiet())
                {
                    var tick = Stopwatch.StartNew();
                    session.Tick();
                    ticks.Add(tick.Elapsed.TotalMilliseconds);
                    if (wall.Elapsed.TotalSeconds > 300) Assert.Fail("the update did not finish");
                    Thread.Sleep(1);
                }
                // GPU が落ち着いたことを確かめる（詰め直したテクスチャと Color の一番小さいミップを読み戻す）
                var packed = view.Packed["_MetallicGlossMap"];
                AsyncGPUReadback.Request(packed, packed.mipmapCount - 1).WaitForCompletion();
                var color = view.Channels[PaintChannel.Color].Texture;
                AsyncGPUReadback.Request(color, color.mipmapCount - 1).WaitForCompletion();
                rows.Add(new[] { ticks.Max(), ticks.Sum(), ticks.Count, wall.Elapsed.TotalMilliseconds, session.Display.PackBuilds - builds });
            }
            rows.RemoveAt(0);
            double Median(int i) { var v = rows.Select(x => x[i]).OrderBy(x => x).ToList(); return v[v.Count / 2]; }
            Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "LiveLink 4096² five channels at once (budget {0} ms, 5 × 1024 tiles): main thread per update: max {1:0.00} ms, total {2:0.00} ms over {3} updates; painted→GPU settled {4:0.00} ms; packed textures built {5} (median of {6}, {7} {8})",
                LiveLinkDisplay.BudgetMs, Median(0), Median(1), Median(2), Median(3), Median(4), rows.Count, SystemInfo.graphicsDeviceType, SystemInfo.graphicsDeviceName));
        }
    }
}
