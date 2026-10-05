using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.LilToon;
using Yozolab.YoluPainter.Editor.LiveLink;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// Live Link のマテリアルの値: 確かめた lilToon だけを lilToon と判定し、プロパティの値と描いていないスロットの絵を読み（元のマテリアルと
    /// テクスチャは変えない）、機能の印を持つスタンドアロン（ブリッジの中の自己診断）へだけ送る。値を変えると送り直し、同じ絵は送り直さない。
    /// 本物の lilToon が要る（無ければ、シェーダーの壊れたエディタ・グラフィックスの無いエディタでは Ignore）。試験のマテリアルとテクスチャは
    /// 試験が作ったものだけ。
    /// </summary>
    public sealed class LiveLinkMaterialValuesTests
    {
        static int s_counter;
        readonly List<Object> owned = new List<Object>();
        ulong server;
        LiveLinkSession session;

        [SetUp]
        public void Require()
        {
            Assert.That(LiveLinkBridge.Problem, Is.Null, "the bridge library must load on the Linux and Windows editors");
            if (Shader.Find("lilToon") == null) Assert.Ignore("lilToon is not installed in this project; the material value tests need the real package.");
            if (EditorShaderCompiler.IsBroken) Assert.Ignore("Built-in shaders fail to compile in this Editor (devcontainer GUI mode). Use test-daemon.sh start --batch-gl.");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("needs a graphics device (the textures are read through a RenderTexture)");
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

        T Own<T>(T o) where T : Object { owned.Add(o); return o; }

        static string UniqueName() => "ylp-unity-values-" + Process.GetCurrentProcess().Id + "-" + (++s_counter);

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

        /// <summary>赤い 8 × 8 の、読めなくした（CPU の写しを捨てた）テクスチャ。</summary>
        Texture2D RedTexture(string name)
        {
            var t = Own(new Texture2D(8, 8, TextureFormat.RGBA32, false, false) { name = name });
            t.SetPixels32(Enumerable.Repeat(new Color32(230, 20, 10, 255), 64).ToArray());
            t.Apply(false, true);
            return t;
        }

        Material LilToon(Texture2D matcap)
        {
            var m = Own(new Material(Shader.Find("lilToon")) { name = "LilBody" });
            m.SetFloat("_UseShadow", 1);
            m.SetFloat("_ShadowBorder", 0.3f);
            m.SetColor("_ShadowColor", new Color(0.4f, 0.3f, 0.5f, 1));
            m.SetTextureScale("_MainTex", new Vector2(2, 1));
            m.SetFloat("_UseMatCap", 1);
            m.SetTexture("_MatCapTex", matcap);
            return m;
        }

        GameObject Model(Material material)
        {
            var root = Own(new GameObject("LiveLinkValuesModel"));
            root.transform.position = new Vector3(700, 700, 700);
            var cube = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
            cube.transform.SetParent(root.transform, false);
            cube.GetComponent<MeshRenderer>().sharedMaterial = material;
            return root;
        }

        void Connect(ulong features)
        {
            string name = UniqueName();
            server = LiveLinkTestServer.Start(name, 128, 64);
            Assert.That(server, Is.Not.EqualTo(0UL));
            Assert.That(LiveLinkTestServer.Configure(server, new Version(0, 1, 0), null, features), Is.True);
            session = LiveLinkSession.Start(name);
            Pump(() => session.Status == LiveLinkStatus.Connected, "the connection");
        }

        [Test]
        public void OnlyAVerifiedLilToonMaterialHasValues()
        {
            var lil = LilToon(RedTexture("matcap"));
            Assert.That(LiveLinkMaterialValues.IsVerifiedLilToon(PreviewMaterialBindings.Resolve(lil)), Is.True,
                PreviewMaterialBindings.Resolve(lil).LilToon?.Describe());
            var standard = Own(new Material(Shader.Find("Standard")));
            Assert.That(LiveLinkMaterialValues.IsVerifiedLilToon(PreviewMaterialBindings.Resolve(standard)), Is.False);
            var unlit = Own(new Material(Shader.Find("Unlit/Texture")));
            Assert.That(LiveLinkMaterialValues.IsVerifiedLilToon(PreviewMaterialBindings.Resolve(unlit)), Is.False);
            Assert.That(LiveLinkMaterialValues.IsVerifiedLilToon(null), Is.False);
        }

        [Test]
        public void ValuesAreReadAsTheMaterialHoldsThemAndNothingIsChanged()
        {
            var matcap = RedTexture("matcap");
            var lil = LilToon(matcap);
            var binding = PreviewMaterialBindings.Resolve(lil);
            var shader = lil.shader;
            var before = Enumerable.Range(0, shader.GetPropertyCount()).Select(i => Describe(lil, shader, i)).ToList();
            int dirty = EditorUtility.GetDirtyCount(lil), textureDirty = EditorUtility.GetDirtyCount(matcap);
            var contents = matcap.imageContentsHash;
            var routed = new[] { "_MainTex" };

            var snapshot = LiveLinkMaterialValues.Read(lil, binding, routed);
            Assert.That(snapshot.Shader, Is.EqualTo("lilToon"));
            Assert.That(snapshot.Source, Does.Contain("lilToon"));
            float Value(string name) => snapshot.Properties.Single(p => p.Name == name).Value.x;
            Assert.That(Value("_ShadowBorder"), Is.EqualTo(0.3f));
            Assert.That(Value("_UseShadow"), Is.EqualTo(1f));
            var color = snapshot.Properties.Single(p => p.Name == "_ShadowColor");
            Assert.That(color.Type, Is.EqualTo(2));
            Assert.That(color.Value, Is.EqualTo(new Vector4(0.4f, 0.3f, 0.5f, 1)), "the color as the material holds it (gamma)");
            Assert.That(snapshot.Properties.Single(p => p.Name == "_MainTex_ST").Value, Is.EqualTo(new Vector4(2, 1, 0, 0)));
            Assert.That(snapshot.Properties.Count, Is.GreaterThan(300), "every property of lilToon, also the ones the standalone does not draw");
            // 流し込み先（_MainTex）は送らず、ほかの描くスロットは様子を送る
            Assert.That(snapshot.Slots.Select(s => s.Name), Does.Not.Contain("_MainTex").And.Contain("_MatCapTex").And.Contain("_ShadowColorTex"));
            Assert.That(snapshot.Slots.Single(s => s.Name == "_MatCapTex").Texture, Is.SameAs(matcap));
            Assert.That(snapshot.Slots.Single(s => s.Name == "_ShadowColorTex").Texture, Is.Null);

            // 絵は一時の RenderTexture から読む（読めなくしたテクスチャも、読めるようにしない）
            Assert.That(matcap.isReadable, Is.False);
            var pixels = LiveLinkMaterialValues.ReadPixels(matcap, 4, 4, false);
            Assert.That(pixels, Is.Not.Null);
            Assert.That(pixels.Length, Is.EqualTo(4 * 4 * 4));
            Assert.That(pixels.Take(4), Is.EqualTo(new byte[] { 230, 20, 10, 255 }));

            // 元のマテリアルとテクスチャは変わらない
            Assert.That(Enumerable.Range(0, shader.GetPropertyCount()).Select(i => Describe(lil, shader, i)), Is.EqualTo(before));
            Assert.That(EditorUtility.GetDirtyCount(lil), Is.EqualTo(dirty));
            Assert.That(EditorUtility.GetDirtyCount(matcap), Is.EqualTo(textureDirty));
            Assert.That(matcap.isReadable, Is.False);
            Assert.That(matcap.imageContentsHash, Is.EqualTo(contents));
            Assert.That(lil.shader, Is.SameAs(shader));
        }

        static string Describe(Material m, Shader shader, int i)
        {
            string name = shader.GetPropertyName(i);
            switch (shader.GetPropertyType(i))
            {
                case ShaderPropertyType.Color: return name + "=" + m.GetColor(name);
                case ShaderPropertyType.Vector: return name + "=" + m.GetVector(name);
                case ShaderPropertyType.Texture: return name + "=" + (m.GetTexture(name) != null ? m.GetTexture(name).GetInstanceID() : 0) + m.GetTextureScale(name) + m.GetTextureOffset(name);
                case ShaderPropertyType.Int: return name + "=" + m.GetInteger(name);
                default: return name + "=" + m.GetFloat(name).ToString("R");
            }
        }

        [Test]
        public void ValuesReachAStandaloneWithTheMarkAndChangesAreSentAgain()
        {
            var matcap = RedTexture("matcap");
            var lil = LilToon(matcap);
            var root = Model(lil);
            int dirty = EditorUtility.GetDirtyCount(lil);
            Connect(LiveLinkBridge.FeatureMaterialValues);
            Assert.That(session.CommonFeatures & LiveLinkBridge.FeatureMaterialValues, Is.Not.EqualTo(0UL));
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => LiveLinkTestServer.Stats(server).values >= 1 && LiveLinkTestServer.Stats(server).textures >= 1, "the values and the MatCap");
            var stats = LiveLinkTestServer.Stats(server);
            Assert.That(stats.last_values_kind, Is.EqualTo(1u), "lilToon");
            Assert.That(stats.last_values_shader_len, Is.EqualTo((uint)"lilToon".Length));
            Assert.That(LiveLinkTestServer.Value(server, 0, "_ShadowBorder", out var border), Is.EqualTo(0));
            Assert.That(border.x, Is.EqualTo(0.3f));
            Assert.That(LiveLinkTestServer.Value(server, 0, "_ShadowColor", out var shadow), Is.EqualTo(2));
            Assert.That(shadow, Is.EqualTo(new Vector4(0.4f, 0.3f, 0.5f, 1)));
            Assert.That(LiveLinkTestServer.Slot(server, 0, "_MatCapTex"), Is.EqualTo((int)LiveLinkSlotState.Follows));
            Assert.That(LiveLinkTestServer.Slot(server, 0, "_ShadowColorTex"), Is.EqualTo((int)LiveLinkSlotState.Empty));
            Assert.That(LiveLinkTestServer.Slot(server, 0, "_MainTex"), Is.LessThan(0), "the painted slot is not sent");
            Assert.That(LiveLinkTestServer.Texture(server, 0, "_MatCapTex", out var t), Is.True);
            Assert.That((t.width, t.height), Is.EqualTo((8u, 8u)));
            Assert.That(t.center & 0xffffff, Is.EqualTo(230u | 20u << 8 | 10u << 16));
            Assert.That(session.ValuesSent, Is.EqualTo(1));

            // 送っても、マテリアルの変更の印は増えない（読むだけ）
            Assert.That(EditorUtility.GetDirtyCount(lil), Is.EqualTo(dirty), "sending the values does not touch the material");

            // インスペクターで値を変える（試験は値を変えて変更の印を付ける）: 次の見回りで値だけを送り直し、同じ絵は送らない
            lil.SetFloat("_ShadowBorder", 0.6f);
            EditorUtility.SetDirty(lil);
            int changed = EditorUtility.GetDirtyCount(lil);
            session.CheckModel();
            Pump(() => LiveLinkTestServer.Stats(server).values >= 2, "the changed values");
            Assert.That(EditorUtility.GetDirtyCount(lil), Is.EqualTo(changed), "only the test changed the material");
            Assert.That(LiveLinkTestServer.Value(server, 0, "_ShadowBorder", out border), Is.EqualTo(0));
            Assert.That(border.x, Is.EqualTo(0.6f));
            Assert.That(LiveLinkTestServer.Slot(server, 0, "_MatCapTex"), Is.EqualTo((int)LiveLinkSlotState.Unchanged));
            Assert.That(LiveLinkTestServer.Stats(server).textures, Is.EqualTo(1u), "the same MatCap is not sent again");
            // 変わっていなければ送らない
            session.CheckModel();
            Thread.Sleep(100); session.Tick();
            Assert.That(LiveLinkTestServer.Stats(server).values, Is.EqualTo(2u));

            // lilToon でなくなると「値なし」を送る
            lil.shader = Shader.Find("Standard");
            session.CheckModel();
            Pump(() => LiveLinkTestServer.Stats(server).values >= 3, "no values any more");
            Assert.That(LiveLinkTestServer.Stats(server).last_values_kind, Is.EqualTo(0u));
            Assert.That(LiveLinkTestServer.Stats(server).refused, Is.EqualTo(0u));
        }

        [Test]
        public void NoValuesGoToAStandaloneWithoutTheMark()
        {
            var lil = LilToon(RedTexture("matcap"));
            var root = Model(lil);
            Connect(0);
            Assert.That(session.CommonFeatures & LiveLinkBridge.FeatureMaterialValues, Is.EqualTo(0UL));
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => LiveLinkTestServer.Stats(server).models == 1, "the model");
            lil.SetFloat("_ShadowBorder", 0.6f);
            EditorUtility.SetDirty(lil);
            session.CheckModel();
            // 後から送ったモデルを閉じる知らせが着くまで待ち、その前に値も絵も届いていないことを見る
            session.CloseModel();
            Pump(() => LiveLinkTestServer.Stats(server).models_closed == 1, "the closed model");
            var stats = LiveLinkTestServer.Stats(server);
            Assert.That((stats.values, stats.textures, stats.unknown), Is.EqualTo((0u, 0u, 0u)));
            Assert.That(session.ValuesSent, Is.EqualTo(0));
        }

        Texture2D Solid(string name, int width, int height, Color32 color, bool linear)
        {
            var t = Own(new Texture2D(width, height, TextureFormat.RGBA32, false, linear) { name = name });
            t.SetPixels32(Enumerable.Repeat(color, width * height).ToArray());
            t.Apply(false, true);
            return t;
        }

        /// <summary>モデルを送り、最初の値とマットキャップの絵が届くまで待つ（スロットの絵を直に送る試験の前置き。ブリッジはモデルの
        /// マテリアルの番号だけを受ける）。</summary>
        void SendModelAndWait(Material material)
        {
            Connect(LiveLinkBridge.FeatureMaterialValues);
            Assert.That(session.SendModel(Model(material)), Is.Null);
            Pump(() => LiveLinkTestServer.Stats(server).values >= 1 && LiveLinkTestServer.Stats(server).textures >= 1, "the first values and the MatCap");
        }

        static LiveLinkMaterialValues.Snapshot SnapshotWith(params (string Name, Texture Texture, ulong Identity)[] slots)
        {
            var s = new LiveLinkMaterialValues.Snapshot { Shader = "lilToon", Source = "test" };
            foreach (var (name, texture, identity) in slots)
                s.Slots.Add(new LiveLinkMaterialValues.Slot { Name = name, Texture = texture, Srgb = false, Identity = identity });
            return s;
        }

        [Test]
        public void ALilToonThatIsNotVerifiedHasNoValues()
        {
            var lil = LilToon(RedTexture("matcap"));
            // 確かめていない版の lilToon（同じシェーダーでも、版が確かめた一覧に無ければ lilToon と見なさない）
            var options = new LilToonAdapter.Options { Version = "9.9.9" };
            var report = LilToonAdapter.Inspect(lil, options);
            Assert.That((report.IsLilToon, report.IsApplicable), Is.EqualTo((true, false)), report.Describe());
            Assert.That(LiveLinkMaterialValues.IsVerifiedLilToon(PreviewMaterialBindings.Resolve(lil, options)), Is.False);
            // 確かめていないバリアント（宝石）。パッケージのシェーダーだが、描き方を確かめていない
            var gemShader = Shader.Find("Hidden/lilToonGem");
            if (gemShader != null)
            {
                var gem = Own(new Material(gemShader));
                var gemReport = LilToonAdapter.Inspect(gem);
                Assert.That((gemReport.IsLilToon, gemReport.IsApplicable), Is.EqualTo((true, false)), gemReport.Describe());
                Assert.That(LiveLinkMaterialValues.IsVerifiedLilToon(PreviewMaterialBindings.Resolve(gem)), Is.False);
            }
        }

        [Test]
        public void LargeTexturesAreHalvedUntilTheLongEdgeFits()
        {
            Assert.That(LiveLinkMaterialValues.SendSize(2048, 2048), Is.EqualTo(new Vector2Int(2048, 2048)));
            Assert.That(LiveLinkMaterialValues.SendSize(4096, 1024), Is.EqualTo(new Vector2Int(2048, 512)));
            Assert.That(LiveLinkMaterialValues.SendSize(3000, 1500), Is.EqualTo(new Vector2Int(1500, 750)));
            Assert.That(LiveLinkMaterialValues.SendSize(3001, 17), Is.EqualTo(new Vector2Int(1500, 8)), "odd edges round down");
            Assert.That(LiveLinkMaterialValues.SendSize(1, 5000), Is.EqualTo(new Vector2Int(1, 1250)), "never below 1");
            Assert.That(LiveLinkMaterialValues.SendSize(4097, 2), Is.EqualTo(new Vector2Int(2048, 1)));
            Assert.That(LiveLinkMaterialValues.SendSize(4098, 4098), Is.EqualTo(new Vector2Int(2049 / 2, 2049 / 2)), "halved until both edges fit");

            // 読んだ画素は送る大きさのまま（半分ずつ縮めても色は変わらない）
            var color = new Color32(200, 100, 50, 255);
            foreach (var (w, h) in new[] { (4096, 1024), (3001, 17) })
            {
                var t = Solid("large", w, h, color, true);
                var size = LiveLinkMaterialValues.SendSize(t);
                var pixels = LiveLinkMaterialValues.ReadPixels(t, size.x, size.y, false);
                Assert.That(pixels, Is.Not.Null, w + "x" + h);
                Assert.That(pixels.Length, Is.EqualTo(size.x * size.y * 4), w + "x" + h);
                Assert.That(pixels.Skip(pixels.Length / 2 / 4 * 4).Take(4), Is.EqualTo(new byte[] { 200, 100, 50, 255 }).Within(1), w + "x" + h);
            }
        }

        [Test]
        public void TexturesAreReadWithTheirOwnColorSpaceSoTheBytesStayAsStored()
        {
            // sRGB の絵は sRGB の RenderTexture へ、リニアの絵は UNorm へ描くので、どちらも元の値のまま読める（ガンマのまま送り、
            // スタンドアロンがスロットの印でリニアへ直す）。ガンマの色空間のプロジェクトでは Unity が sRGB の形式を使わないので、
            // 印はどちらも切（その色空間は合わせない）
            var color = new Color32(128, 64, 200, 255);
            var srgb = Solid("srgb", 4, 4, color, false);
            var linear = Solid("linear", 4, 4, color, true);
            var lil = LilToon(srgb);
            lil.SetTexture("_ShadowBorderMask", linear);
            var snapshot = LiveLinkMaterialValues.Read(lil, PreviewMaterialBindings.Resolve(lil), new[] { "_MainTex" });
            var matcap = snapshot.Slots.Single(s => s.Name == "_MatCapTex");
            var mask = snapshot.Slots.Single(s => s.Name == "_ShadowBorderMask");
            Assert.That((matcap.Srgb, mask.Srgb), Is.EqualTo((PlayerSettings.colorSpace == ColorSpace.Linear, false)), PlayerSettings.colorSpace.ToString());
            foreach (var slot in new[] { matcap, mask })
            {
                var pixels = LiveLinkMaterialValues.ReadPixels(slot.Texture, 4, 4, slot.Srgb);
                Assert.That(pixels.Take(4), Is.EqualTo(new byte[] { 128, 64, 200, 255 }).Within(1), slot.Name + " (" + PlayerSettings.colorSpace + ")");
            }
        }

        [Test]
        public void TexturesOverTheBudgetAndUnreadableTexturesAreNotSent()
        {
            var lil = LilToon(RedTexture("matcap"));
            SendModelAndWait(lil);
            var before = LiveLinkTestServer.Stats(server);
            var a = Solid("a", 8, 8, new Color32(10, 20, 30, 255), true);
            var b = Solid("b", 8, 8, new Color32(40, 50, 60, 255), true);
            var array = Own(new Texture2DArray(4, 4, 2, TextureFormat.RGBA32, false));
            var cube = Own(new Cubemap(4, TextureFormat.RGBA32, false));
            // 配列・キューブマップは読めない（予算は引かない）。予算は 1 枚分（8 × 8 × 4 = 256 バイト）と少し: 先の絵は送り、後の絵は
            // 予算を超える
            var sent = new Dictionary<string, ulong> { ["_ShadowColorTex"] = 99, ["_RimColorTex"] = 98 };
            long budget = 300;
            var result = LiveLinkMaterialValues.Send(session.Handle, 0,
                SnapshotWith(("_EmissionMap", array, 13), ("_RimColorTex", cube, 14), ("_MatCapTex", a, 11), ("_ShadowColorTex", b, 12)), sent, ref budget);
            Assert.That(result.Result, Is.EqualTo(1));
            Assert.That((result.Textures, result.Bytes), Is.EqualTo((1, 256L)));
            Assert.That(budget, Is.EqualTo(300 - 256), "only the sent texture is taken from the budget");
            Pump(() => LiveLinkTestServer.Stats(server).values >= before.values + 1 && LiveLinkTestServer.Stats(server).textures >= before.textures + 1, "the values and the texture");
            Assert.That(LiveLinkTestServer.Slot(server, 0, "_MatCapTex"), Is.EqualTo((int)LiveLinkSlotState.Follows));
            Assert.That(LiveLinkTestServer.Slot(server, 0, "_ShadowColorTex"), Is.EqualTo((int)LiveLinkSlotState.OverBudget));
            Assert.That(LiveLinkTestServer.Slot(server, 0, "_EmissionMap"), Is.EqualTo((int)LiveLinkSlotState.Unreadable));
            Assert.That(LiveLinkTestServer.Slot(server, 0, "_RimColorTex"), Is.EqualTo((int)LiveLinkSlotState.Unreadable));
            // 送ったものだけを覚え、送らなかったスロットの前の同一性は消す（次に予算が空けば送り直す）
            Assert.That(sent, Is.EquivalentTo(new Dictionary<string, ulong> { ["_MatCapTex"] = 11 }));
            Assert.That(LiveLinkTestServer.Texture(server, 0, "_MatCapTex", out var t), Is.True);
            Assert.That((t.width, t.height, t.center & 0xffffff), Is.EqualTo((8u, 8u, 10u | 20u << 8 | 30u << 16)));
            Assert.That(LiveLinkTestServer.Stats(server).refused, Is.EqualTo(0u));
        }

        [Test]
        public void SendingTheModelAgainSendsTheTexturesAgain()
        {
            // モデルを送り直すと、スタンドアロンは前のモデルの絵を捨てる。同じ絵でも「前と同じ」ではなく送り直す
            var matcap = RedTexture("matcap");
            var lil = LilToon(matcap);
            var root = Model(lil);
            Connect(LiveLinkBridge.FeatureMaterialValues);
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => LiveLinkTestServer.Stats(server).textures >= 1, "the MatCap");
            Assert.That(LiveLinkTestServer.Slot(server, 0, "_MatCapTex"), Is.EqualTo((int)LiveLinkSlotState.Follows));
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => LiveLinkTestServer.Stats(server).models >= 2 && LiveLinkTestServer.Stats(server).values >= 2, "the second model and its values");
            Pump(() => LiveLinkTestServer.Stats(server).textures >= 2, "the MatCap again");
            Assert.That(LiveLinkTestServer.Slot(server, 0, "_MatCapTex"), Is.EqualTo((int)LiveLinkSlotState.Follows));
            Assert.That(LiveLinkTestServer.Stats(server).refused, Is.EqualTo(0u));
        }
    }
}
