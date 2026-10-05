using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.LiveLink;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// Live Link の送りの列が混んだとき（スタンドアロンが読むのを止めて、ブリッジの送りの列が上限に近い）: ブリッジは積まずに「混んでいる」と返し、Unity は
    /// 主のスレッドを止めず、ログを溢れさせず、混みが引いてから送り直す。送ってよいものを断らない（制御の命令）・読み直しを避ける（絵は入るかを先に見る）。
    /// 相手は同じプロセスの自己診断のスタンドアロン（読むのを止められる。本物のソケット）。
    /// </summary>
    public sealed class LiveLinkBusyTests
    {
        static int s_counter;
        readonly List<Object> owned = new List<Object>();
        readonly List<LiveLinkModel> models = new List<LiveLinkModel>();
        ulong server;
        LiveLinkSession session;

        const string BusyText = "The standalone is slow to read, so sending waits.";

        [SetUp]
        public void Require()
        {
            Assert.That(LiveLinkBridge.Problem, Is.Null, "the bridge library must load on the Linux and Windows editors");
            L.OverrideLanguage(PainterLanguage.English);
        }

        [TearDown]
        public void CleanUp()
        {
            if (server != 0) LiveLinkTestServer.PauseReading(server, false);
            session?.Dispose(); session = null;
            LiveLinkTestServer.Stop(server); server = 0;
            foreach (var m in models) m.Dispose();
            models.Clear();
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
            L.OverrideLanguage(null);
        }

        T Own<T>(T o) where T : Object { owned.Add(o); return o; }

        static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("needs a graphics device (the textures are read through a RenderTexture)");
            if (EditorShaderCompiler.IsBroken) Assert.Ignore("Built-in shaders fail to compile in this Editor (devcontainer GUI mode). Use test-daemon.sh start --batch-gl.");
        }

        void Connect(ulong features)
        {
            string name = "ylp-unity-busy-" + Process.GetCurrentProcess().Id + "-" + (++s_counter);
            server = LiveLinkTestServer.Start(name, 128, 64);
            Assert.That(server, Is.Not.EqualTo(0UL));
            Assert.That(LiveLinkTestServer.Configure(server, new Version(0, 4, 0), null, features), Is.True);
            session = LiveLinkSession.Start(name);
            session.BusyRetrySeconds = 0.05;
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

        /// <summary>立方体 1 つ（Standard。<paramref name="texture"/> があれば _MainTex に入れる）のモデル。</summary>
        GameObject ModelOf(Texture texture = null)
        {
            var root = Own(new GameObject("LiveLinkBusyModel"));
            root.transform.position = new Vector3(900, 900, 900);
            var cube = Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
            cube.transform.SetParent(root.transform, false);
            var material = Own(new Material(Shader.Find("Standard")) { name = "BusyBody" });
            if (texture != null) material.SetTexture("_MainTex", texture);
            cube.GetComponent<MeshRenderer>().sharedMaterial = material;
            return root;
        }

        Texture2D Solid(Color32 color, int size = 16)
        {
            var t = Own(new Texture2D(size, size, TextureFormat.RGBA32, false, false) { name = "solid" });
            t.SetPixels32(Enumerable.Repeat(color, size * size).ToArray());
            t.Apply(false, true);
            return t;
        }

        /// <summary>モデルを送って届くまで待つ（最初のモデルと、その値の「値なし」まで）。</summary>
        void SendFirstModel(GameObject root)
        {
            Assert.That(session.SendModel(root), Is.Null);
            Pump(() => session.ModelsSent == 1 && LiveLinkTestServer.Stats(server).models == 1 && LiveLinkTestServer.Stats(server).values >= 1, "the first model");
        }

        /// <summary>スタンドアロンの読みを止め（読みの時間切れの分だけ待って、途中まで読んでいた枠を読み終えさせる）、送りの列を、小さな命令も入らない満杯にする。
        /// 積んだ量を返す。</summary>
        long StallAndFill()
        {
            Assert.That(LiveLinkTestServer.PauseReading(server, true), Is.True);
            Thread.Sleep(400);
            LiveLinkTestServer.SetOutboxLimit(session.Handle, 10UL << 20);
            var pixels = new byte[1024 * 1024 * 4];
            Assert.That(LiveLinkBridge.TextureSend(session.Handle, 0, "_fill0", 1024, 1024, false, pixels), Is.EqualTo(1));
            Assert.That(LiveLinkBridge.TextureSend(session.Handle, 0, "_fill1", 1024, 1024, false, pixels), Is.EqualTo(1));
            long held = LiveLinkBridge.PendingBytes(session.Handle);
            Assert.That(held, Is.GreaterThan(8L << 20));
            // 積んだ量のすぐ上を上限にする（これ以上は、小さな命令も入らない）
            LiveLinkTestServer.SetOutboxLimit(session.Handle, (ulong)held + 10);
            Assert.That(LiveLinkBridge.SendRoom(session.Handle), Is.EqualTo(10));
            return held;
        }

        void Resume()
        {
            Assert.That(LiveLinkTestServer.PauseReading(server, false), Is.True);
            Pump(() => LiveLinkBridge.PendingBytes(session.Handle) == 0, "the queue to empty");
        }

        [Test]
        public void TheBusyValueAndTheRoomAreWhatTheLibraryReports()
        {
            Connect(LiveLinkBridge.FeatureMaterialValues);
            Assert.That(LiveLinkBridge.Busy, Is.EqualTo(-8));
            Assert.That(LiveLinkBridge.SendRoom(session.Handle), Is.EqualTo(long.MaxValue), "何も積んでいなければ、どんな大きさも入る");
            Assert.That(LiveLinkBridge.SendRoom(0), Is.EqualTo(0), "つながりが無ければ、空きは無い");
            Assert.That(LiveLinkBridge.PendingBytes(session.Handle), Is.EqualTo(0));
        }

        [Test]
        public void AModelThatCannotBeQueuedWaitsWithoutBlockingTheEditorAndGoesWhenTheStandaloneReadsAgain()
        {
            var root = ModelOf();
            Connect(LiveLinkBridge.FeatureMaterialValues);
            SendFirstModel(root);
            StallAndFill();
            var clock = Stopwatch.StartNew();
            Assert.That(session.SendModel(root), Is.Null, "混んでいるのは失敗ではない");
            Assert.That(clock.ElapsedMilliseconds, Is.LessThan(5000), "待たずに返る");
            Assert.That(session.ModelPending, Is.True);
            Assert.That(session.ModelsSent, Is.EqualTo(1), "まだ積めていない");
            Assert.That(session.Model.Generation, Is.EqualTo(0));
            Assert.That(session.BusyRefusals, Is.GreaterThan(0));
            // 待っている間に更新を回しても、主のスレッドを止めず、知らせは混みが続く間に 1 回だけ
            long worst = 0;
            for (int i = 0; i < 60; i++)
            {
                var tick = Stopwatch.StartNew();
                session.Tick();
                worst = Math.Max(worst, tick.ElapsedMilliseconds);
                Thread.Sleep(5);
            }
            Assert.That(worst, Is.LessThan(1000), "Tick は待たない");
            Assert.That(session.ModelPending, Is.True);
            Assert.That(session.Log.Count(l => l.Contains(BusyText)), Is.EqualTo(1), "ログを溢れさせない");
            Assert.That(LiveLinkTestServer.Stats(server).models, Is.EqualTo(1u), "断られたモデルは届いていない");
            // 読み始める: 写してあったモデルが、写し直さずに積まれる
            LiveLinkTestServer.PauseReading(server, false);
            Pump(() => session.ModelsSent == 2, "the model after the queue empties");
            Assert.That(session.ModelPending, Is.False);
            Assert.That(session.Model.Generation, Is.EqualTo(2));
            Pump(() => LiveLinkTestServer.Stats(server).models == 2, "the model to arrive");
            Assert.That(LiveLinkTestServer.Stats(server).generation, Is.EqualTo(2u));
            Assert.That(session.Log.Count(l => l.Contains(BusyText)), Is.EqualTo(1));
            Assert.That(session.Log.Any(l => l.Contains("Sent LiveLinkBusyModel")), Is.True);
        }

        [Test]
        public void ClosingTheModelIsNotRefusedWhenTheQueueIsFull()
        {
            var root = ModelOf();
            Connect(LiveLinkBridge.FeatureMaterialValues);
            SendFirstModel(root);
            StallAndFill();
            // 制御の命令は小さく、終わりを知らせるので、混んでいても積む（閉じたことが相手に伝わる）
            Assert.That(LiveLinkBridge.ModelClose(session.Handle), Is.EqualTo(0));
            Resume();
            Pump(() => LiveLinkTestServer.Stats(server).models_closed == 1, "the closed model");
        }

        [Test]
        public void ValuesThatCannotBeQueuedKeepTheirTurnAndAreSentWhenThereIsRoom()
        {
            var root = ModelOf();
            Connect(LiveLinkBridge.FeatureMaterialValues);
            SendFirstModel(root);
            uint before = LiveLinkTestServer.Stats(server).values;
            StallAndFill();
            // モデルを送った直後の全部送りが、満杯で断られる。送る番は残る
            var report = session.Model.SendValues(session.Handle, true);
            Assert.That(report.Busy, Is.True);
            Assert.That(session.Model.Busy, Is.True);
            Assert.That(report.Problem, Is.Null, "混んでいるのは失敗ではない");
            Assert.That(session.Model.Materials[0].ValuesDue, Is.True);
            // 次の見回り（all が偽）でも、残った番として送ろうとして、まだ満杯なら断られる
            Assert.That(session.Model.SendValues(session.Handle, false).Busy, Is.True);
            Assert.That(session.Model.Materials[0].ValuesDue, Is.True);
            Assert.That(LiveLinkTestServer.Stats(server).values, Is.EqualTo(before));
            Resume();
            report = session.Model.SendValues(session.Handle, false);
            Assert.That(report.Busy, Is.False);
            Assert.That(session.Model.Materials[0].ValuesDue, Is.False, "送り終えたら番は消える");
            Pump(() => LiveLinkTestServer.Stats(server).values == before + 1, "the values");
            // 送り終えたあとの見回りは、何も送らない
            Assert.That(session.Model.SendValues(session.Handle, false).Busy, Is.False);
            Thread.Sleep(100);
            Assert.That(LiveLinkTestServer.Stats(server).values, Is.EqualTo(before + 1));
        }

        [Test]
        public void AValuesRequestThatCannotBeAnsweredIsKeptAndAnsweredWhenThereIsRoom()
        {
            var root = ModelOf();
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureMaterialRequest);
            SendFirstModel(root);
            uint before = LiveLinkTestServer.Stats(server).values;
            StallAndFill();
            Assert.That(LiveLinkTestServer.Request(server, 0, 0, LiveLinkRequest.WantValues, "", 0), Is.True);
            Pump(() => session.RequestsReceived == 1, "the request");
            for (int i = 0; i < 20; i++) { session.Tick(); Thread.Sleep(5); }
            Assert.That(session.ValuesResent, Is.EqualTo(0), "混んでいる間は答えていない");
            Assert.That(session.BusyRefusals, Is.GreaterThan(0));
            Assert.That(LiveLinkTestServer.Stats(server).values, Is.EqualTo(before));
            // 頼みは取り出し済み。覚えていて、空いたら答える
            LiveLinkTestServer.PauseReading(server, false);
            Pump(() => session.ValuesResent == 1, "the answer after the queue empties");
            Pump(() => LiveLinkTestServer.Stats(server).values == before + 1, "the answer to arrive");
            Assert.That(session.Log.Count(l => l.Contains(BusyText)), Is.EqualTo(1));
        }

        [Test]
        public void AMaterialUpdateThatCannotBeQueuedIsNotRememberedAsSentAndGoesLater()
        {
            RequireGraphics();
            var root = ModelOf();
            Connect(LiveLinkBridge.FeatureMaterialValues);
            SendFirstModel(root);
            StallAndFill();
            var material = root.GetComponentInChildren<MeshRenderer>().sharedMaterial;
            material.EnableKeyword("_NORMALMAP");
            Pump(() => session.BusyRefusals > 0, "the refused update");
            Assert.That(session.MaterialUpdatesSent, Is.EqualTo(0));
            for (int i = 0; i < 40; i++) { session.Tick(); Thread.Sleep(5); }
            Assert.That(session.MaterialUpdatesSent, Is.EqualTo(0), "送ったものとして覚えない");
            Assert.That(session.Log.Count(l => l.Contains(BusyText)), Is.EqualTo(1));
            Assert.That(LiveLinkTestServer.Stats(server).materials_updates, Is.EqualTo(0u));
            LiveLinkTestServer.PauseReading(server, false);
            Pump(() => session.MaterialUpdatesSent == 1, "the update after the queue empties");
            Pump(() => LiveLinkTestServer.Stats(server).materials_updates == 1, "the update to arrive");
            Assert.That(LiveLinkTestServer.Stats(server).models, Is.EqualTo(1u), "モデルは送り直さない");
        }

        [Test]
        public void ThePicturesOfValuesAreNotReadWhileTheyWouldNotFit()
        {
            RequireGraphics();
            var root = ModelOf();
            Connect(LiveLinkBridge.FeatureMaterialValues);
            SendFirstModel(root);
            StallAndFill();
            var snapshot = new LiveLinkMaterialValues.Snapshot { Shader = "Hidden/lilToon", Source = "test" };
            snapshot.Slots.Add(new LiveLinkMaterialValues.Slot { Name = "_MatCapTex", Texture = Solid(new Color32(10, 20, 30, 255), 64), Srgb = true, Identity = 7 });
            var sentSlots = new Dictionary<string, ulong>();
            long budget = LiveLinkMaterialValues.Budget;
            // 満杯: 絵を読む前に断る（何も組み立てず、予算も使わず、送ったことにしない）
            var refused = LiveLinkMaterialValues.Send(session.Handle, 0, snapshot, sentSlots, ref budget);
            Assert.That(refused.Result, Is.EqualTo(LiveLinkBridge.Busy));
            Assert.That(sentSlots, Is.Empty);
            Assert.That(budget, Is.EqualTo(LiveLinkMaterialValues.Budget), "読んでいない");
            Assert.That(refused.Retry, Is.False);
            // 空いたら、値と絵が届く
            Resume();
            uint values = LiveLinkTestServer.Stats(server).values;
            var sent = LiveLinkMaterialValues.Send(session.Handle, 0, snapshot, sentSlots, ref budget);
            Assert.That((sent.Result, sent.Textures, sent.Retry), Is.EqualTo((1, 1, false)));
            Assert.That(sentSlots["_MatCapTex"], Is.EqualTo(7UL));
            Pump(() => LiveLinkTestServer.Stats(server).values == values + 1 && LiveLinkTestServer.Stats(server).textures >= 3, "the values and the picture");
            Assert.That(LiveLinkTestServer.Texture(server, 0, "_MatCapTex", out var texture), Is.True);
            Assert.That((texture.width, texture.height), Is.EqualTo((64u, 64u)));
        }

        [Test]
        public void AnOriginalWaitsForRoomWithoutReadingItsPictureAndIsSentWhenThereIsRoom()
        {
            RequireGraphics();
            // 元の絵を押し出す（頼みの印が無い）スタンドアロン。送る列を自分で持って、混みの間の様子を見る
            var root = ModelOf(Solid(new Color32(200, 30, 10, 255)));
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            var model = LiveLinkModel.Capture(root);
            models.Add(model);
            Assert.That(model.Send(session.Handle), Is.Null);
            Assert.That(model.Generation, Is.GreaterThan(0));
            Pump(() => LiveLinkTestServer.Stats(server).models == 1, "the model");
            StallAndFill();
            var sender = new LiveLinkOriginals.Sender(model, LiveLinkOriginals.Plan(model));
            // 押し出しの列の 1 枚目: 入らないので、読まずに待つ（枠は残る）。積んだ量の目安の制限（pendingLimit）に隠れないよう、制限は外す
            var report = sender.Pump(session.Handle, 1000, long.MaxValue);
            Assert.That(report.Busy, Is.True);
            Assert.That(report.Refused, Is.False, "入る空きが無いと見て待っただけで、ブリッジは断っていない");
            Assert.That(report.Problem, Is.Null);
            Assert.That((sender.Reads, sender.Images, sender.Done), Is.EqualTo((0, 0, false)), "読んでいない・送っていない・枠は残る");
            // 何度呼んでも読まない
            for (int i = 0; i < 5; i++) Assert.That(sender.Pump(session.Handle, 1000, long.MaxValue).Busy, Is.True);
            Assert.That(sender.Reads, Is.EqualTo(0));
            Resume();
            report = sender.Pump(session.Handle, 1000, long.MaxValue);
            Assert.That(report.Busy, Is.False);
            Assert.That((sender.Reads, sender.Images, sender.Done), Is.EqualTo((1, 1, true)));
            Pump(() => LiveLinkTestServer.Stats(server).originals == 1, "the original");
            Assert.That(LiveLinkTestServer.Original(server, 0, "_MainTex", out var original), Is.True);
            Assert.That(original.state, Is.EqualTo((uint)LiveLinkOriginalState.Image));
        }

        [Test]
        public void AnOriginalTheBridgeRefusesIsMarkedRefusedAndKeepsItsPixelsForTheNextTry()
        {
            RequireGraphics();
            var root = ModelOf(Solid(new Color32(200, 30, 10, 255)));
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            var model = LiveLinkModel.Capture(root);
            models.Add(model);
            Assert.That(model.Send(session.Handle), Is.Null);
            Pump(() => LiveLinkTestServer.Stats(server).models == 1, "the model");
            long held = StallAndFill();
            // 画素の分（16 x 16 x 4）は入るが、枠の頭と付き物は入らない空き: 読む前の見積もりは通り、ブリッジが断る
            LiveLinkTestServer.SetOutboxLimit(session.Handle, (ulong)held + 16 * 16 * 4 + 1);
            var sender = new LiveLinkOriginals.Sender(model, LiveLinkOriginals.Plan(model));
            var report = sender.Pump(session.Handle, 1000, long.MaxValue);
            Assert.That((report.Busy, report.Refused), Is.EqualTo((true, true)), "ブリッジが断った");
            Assert.That((sender.Reads, sender.Images, sender.Done), Is.EqualTo((1, 0, false)), "読んだ・送っていない・枠は残る");
            Resume();
            report = sender.Pump(session.Handle, 1000, long.MaxValue);
            Assert.That((report.Busy, report.Refused), Is.EqualTo((false, false)));
            Assert.That((sender.Reads, sender.Images, sender.Done), Is.EqualTo((1, 1, true)), "読んだ画素を使い回す（読み直さない）");
            Pump(() => LiveLinkTestServer.Stats(server).originals == 1, "the original");
        }

        [Test]
        public void OnlyWhatTheBridgeRefusesCountsAsABusyRefusalAndWaitingForRoomDoesNot()
        {
            RequireGraphics();
            var root = ModelOf(Solid(new Color32(200, 30, 10, 255)));
            Connect(LiveLinkBridge.FeatureMaterialValues | LiveLinkBridge.FeatureOriginalTextures);
            session.OriginalsPendingLimit = 0; // 元の絵は、列を満杯にするまで読まずに残す
            SendFirstModel(root);
            Assert.That(session.OriginalsPending, Is.True);
            long held = StallAndFill();
            session.OriginalsPendingLimit = long.MaxValue;
            // 入る空きが無い: 読まずに待つだけ。ブリッジは断っていないので数えず、間も置かない（知らせは 1 回）
            for (int i = 0; i < 20; i++) { session.Tick(); Thread.Sleep(5); }
            Assert.That(session.OriginalsPending, Is.True);
            Assert.That(session.BusyRefusals, Is.EqualTo(0));
            Assert.That(session.Congested, Is.False);
            Assert.That(session.Log.Count(l => l.Contains(BusyText)), Is.EqualTo(1));
            // 画素の分は入ると見えるが、枠が入らない空き: ブリッジが断る。これは数える
            LiveLinkTestServer.SetOutboxLimit(session.Handle, (ulong)held + 16 * 16 * 4 + 1);
            Pump(() => session.BusyRefusals > 0, "the refusal of the bridge");
            Assert.That(session.OriginalsSent, Is.EqualTo(0));
            Assert.That(session.OriginalsPending, Is.True);
            Assert.That(session.Log.Count(l => l.Contains(BusyText)), Is.EqualTo(1), "ログを溢れさせない");
            Resume();
            Pump(() => session.OriginalsSent == 1, "the original after the queue empties");
            Pump(() => LiveLinkTestServer.Stats(server).originals == 1, "the original to arrive");
        }
    }
}
