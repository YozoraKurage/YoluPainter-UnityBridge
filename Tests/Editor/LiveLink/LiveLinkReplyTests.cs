using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor.LiveLink;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 返事を受ける: このプロジェクトの頼みへの返事だけを読んで消すこと（ほかの頼み・書きかけの .tmp は触らない）、<c>opened</c>・<c>refused</c> の状態と理由、
    /// <c>exported</c> の PNG の取り込みと TextureImporter の sRGB・ノーマルマップ、当てる・当てない・選んだ物だけ・取り消しで戻ること、
    /// <c>Assets</c> の外の PNG を取り込まないこと、FBX の中のマテリアルには当てないこと。スタンドアロンの代わりに、試験が返事を outbox に置く。
    /// </summary>
    public sealed class LiveLinkReplyTests
    {
        LiveLinkTestScope scope;

        [SetUp] public void SetUp() => scope = new LiveLinkTestScope();
        [TearDown] public void TearDown() => scope.Dispose();

        static string Reply(string request, string kind, string problems = "[]", string files = "[]") =>
            "{\"format\":1,\"request\":\"" + request + "\",\"kind\":\"" + kind + "\",\"app\":{\"version\":\"0.5.0\"},\"problems\":" + problems + ",\"files\":" + files + "}";

        static string File(string material, string property, string path, bool srgb, bool normal) =>
            "{\"material\":\"" + material + "\",\"property\":\"" + property + "\",\"path\":\"" + path + "\",\"srgb\":" + (srgb ? "true" : "false") + ",\"normal_map\":" + (normal ? "true" : "false") + "}";

        string Sent(string targetName = "Avatar")
        {
            string id = Guid.NewGuid().ToString("D");
            LiveLinkLedger.Add(id, DateTime.UtcNow, "key-" + targetName, targetName);
            var state = new LiveLinkState { phase = (int)LiveLinkState.Phase.Sent, request = id, targetName = targetName, targetKey = "key-" + targetName };
            state.refused.Add(new LiveLinkState.Problem("Accessory", LiveLinkReason.MeshNotFromFbx));
            LiveLinkState.Set(state);
            return id;
        }

        [Test]
        public void OnlyRepliesToThisProjectsRequestsAreReadAndThenRemoved()
        {
            string id = Sent();
            string mine = scope.PutReply(id, 0, Reply(id, "opened"));
            string other = scope.PutReply(Guid.NewGuid().ToString("D"), 0, Reply("x", "opened"));
            string half = Path.Combine(scope.Folder.Outbox, id + "-1.json" + LiveLinkFolder.TmpSuffix);
            System.IO.File.WriteAllText(half, "{");
            Assert.That(LiveLinkReplies.Poll(scope.Folder), Is.EqualTo(1));
            Assert.That(System.IO.File.Exists(mine), Is.False, "a reply that was read is removed");
            Assert.That(System.IO.File.Exists(other), Is.True, "another project's reply is left for it");
            Assert.That(System.IO.File.Exists(half), Is.True, "a file being written is not touched");
            Assert.That(LiveLinkState.Current.Kind, Is.EqualTo(LiveLinkState.Phase.Opened));
            Assert.That(LiveLinkReplies.RequestIdOf(id + "-12"), Is.EqualTo(id));
            Assert.That(LiveLinkReplies.RequestIdOf(id), Is.Null.Or.Not.EqualTo(id));
            Assert.That(LiveLinkReplies.RequestIdOf("abc-x"), Is.Null);
        }

        [Test]
        public void OpenedAndRefusedShowTheirState()
        {
            string id = Sent();
            scope.PutReply(id, 0, Reply(id, "opened", "[{\"path\":\"Body/Ear\",\"reason\":\"bone_not_found\"}]"));
            LiveLinkReplies.Poll(scope.Folder);
            var s = LiveLinkState.Current;
            Assert.That(s.Kind, Is.EqualTo(LiveLinkState.Phase.Opened));
            Assert.That(LiveLinkWindow.LastText(s), Does.StartWith("Opened " + s.targetName + " in YoluPainter ("));
            Assert.That(LiveLinkWindow.PresenceTip(LiveLinkWindow.Presence.Running, s), Is.EqualTo("YoluPainter is running\n" + LiveLinkWindow.LastText(s)),
                "the last state is in the dot's tooltip");
            Assert.That(LiveLinkWindow.Rows(s).Select(r => r.Name + ":" + r.Reason), Is.EqualTo(new[] { "Accessory:The mesh is not from an FBX", "Body/Ear:A bone is not in the FBX" }));
            Assert.That(s.refused.Select(p => p.path), Is.EqualTo(new[] { "Accessory" }), "what Unity could not send stays listed");
            Assert.That(s.problems.Select(p => p.path + ":" + p.reason), Is.EqualTo(new[] { "Body/Ear:bone_not_found" }));
            Assert.That(LiveLinkReason.Text("bone_not_found"), Is.EqualTo("A bone is not in the FBX"));
            Assert.That(LiveLinkReason.Text("a_new_word"), Is.EqualTo("a_new_word"), "an unknown word is shown as it is");

            scope.PutReply(id, 1, Reply(id, "refused", "[{\"path\":\"\",\"reason\":\"too_large\"}]"));
            LiveLinkReplies.Poll(scope.Folder);
            s = LiveLinkState.Current;
            Assert.That(s.Kind, Is.EqualTo(LiveLinkState.Phase.Refused));
            Assert.That(LiveLinkWindow.LastText(s), Does.StartWith("YoluPainter refused " + s.targetName + " ("));
            Assert.That(s.problems.Single().reason, Is.EqualTo("too_large"));
            Assert.That(LiveLinkWindow.Rows(s).Last().Name + ":" + LiveLinkWindow.Rows(s).Last().Reason, Is.EqualTo(s.targetName + ":Too large"),
                "a reason without a path is the target's");

            scope.PutReply(id, 2, Reply(id, "refused", "[{\"path\":\"\",\"reason\":\"declined\"}]"));
            LiveLinkReplies.Poll(scope.Folder);
            Assert.That(LiveLinkWindow.Rows(LiveLinkState.Current).Last().Reason, Is.EqualTo("Opening was canceled in YoluPainter"));
            Assert.That(LiveLinkWindow.Rows(LiveLinkState.Current).Last().ReasonTip, Is.EqualTo(LiveLinkReason.Declined));

            // 前の頼みへの遅い返事は、今の頼みの状態を書き換えない
            string newer = Sent();
            scope.PutReply(id, 3, Reply(id, "opened"));
            LiveLinkReplies.Poll(scope.Folder);
            Assert.That(LiveLinkState.Current.request, Is.EqualTo(newer));
            Assert.That(LiveLinkState.Current.Kind, Is.EqualTo(LiveLinkState.Phase.Sent));

            // 形式の版が違う返事は読んで捨てる
            string bad = scope.PutReply(newer, 0, "{\"format\":2,\"request\":\"" + newer + "\",\"kind\":\"opened\"}");
            LiveLinkReplies.Poll(scope.Folder);
            Assert.That(System.IO.File.Exists(bad), Is.False);
            Assert.That(LiveLinkState.Current.Kind, Is.EqualTo(LiveLinkState.Phase.Sent));
        }

        [Test]
        public void AnExportOfAnEarlierRequestDoesNotHideTheAnswerToTheLatestOne()
        {
            string first = Sent("First");
            string second = Sent("Second"); // 前の頼みの書き出しが先に届く間に、別の相手へ送った
            scope.PutReply(first, 0, Reply(first, "exported"));
            LiveLinkReplies.Poll(scope.Folder);
            Assert.That(LiveLinkState.Current.request, Is.EqualTo(first));
            Assert.That(LiveLinkState.Current.Kind, Is.EqualTo(LiveLinkState.Phase.Exported));
            Assert.That(LiveLinkState.Current.Pending, Is.EqualTo(second), "the request that waits for its answer is remembered");

            // 保存していない変更があって、開くのをやめた
            scope.PutReply(second, 0, Reply(second, "refused", "[{\"path\":\"\",\"reason\":\"declined\"}]"));
            LiveLinkReplies.Poll(scope.Folder);
            var state = LiveLinkState.Current;
            Assert.That(state.request, Is.EqualTo(second));
            Assert.That(state.Kind, Is.EqualTo(LiveLinkState.Phase.Refused));
            Assert.That(LiveLinkWindow.Rows(state).Select(r => r.Name + ":" + r.ReasonTip), Is.EqualTo(new[] { "Accessory:" + LiveLinkReason.MeshNotFromFbx, "Second:" + LiveLinkReason.Declined }),
                "the declined row is shown, and what Unity could not send is still listed");

            // その後に前の頼みへ遅れて来た返事は、今の状態を書き換えない
            scope.PutReply(first, 1, Reply(first, "opened"));
            LiveLinkReplies.Poll(scope.Folder);
            Assert.That(LiveLinkState.Current.request, Is.EqualTo(second));
            Assert.That(LiveLinkState.Current.Kind, Is.EqualTo(LiveLinkState.Phase.Refused));
        }

        [Test]
        public void RepliesAreReadInTheOrderTheyWereWrittenEvenPastNine()
        {
            string id = Sent();
            var order = new List<string>();
            var handle = LiveLinkReplies.Handle;
            LiveLinkReplies.Handle = r => { order.Add(r.Problems.Single().reason); handle(r); };
            try
            {
                // スタンドアロンは n を桁をそろえずに増やす（名前の順なら -10 が -2 より先になる）
                foreach (int n in new[] { 100, 2, 10, 9, 0 })
                    scope.PutReply(id, n, Reply(id, "opened", "[{\"path\":\"\",\"reason\":\"n" + n + "\"}]"));
                Assert.That(LiveLinkReplies.Poll(scope.Folder), Is.EqualTo(5));
            }
            finally { LiveLinkReplies.Handle = handle; }
            Assert.That(order, Is.EqualTo(new[] { "n0", "n2", "n9", "n10", "n100" }));
            Assert.That(LiveLinkState.Current.problems.Single().reason, Is.EqualTo("n100"), "the last state is the newest reply");
            Assert.That(LiveLinkReplies.CompareNumbers("0007", "7"), Is.EqualTo(0));
            Assert.That(LiveLinkReplies.CompareNumbers("99999999999999999999", "100000000000000000000"), Is.LessThan(0), "no overflow");
        }

        [Test]
        public void AMaterialWithoutAnIdentityIsFoundByItsInstanceOnlyInTheSessionThatSentIt()
        {
            string id = Sent();
            var a = scope.CreateSceneMaterial("A");
            var b = scope.CreateSceneMaterial("B");
            string pngPath = LiveLinkTestScope.Absolute(scope.AssetFolder + "/Main.png");
            System.IO.File.WriteAllBytes(pngPath, LiveLinkTestScope.Png(Color.red));
            string files = "[" + File(LiveLinkRequest.MaterialKey(a), "_MainTex", pngPath, true, false) + "," + File(LiveLinkRequest.MaterialKey(b), "_MainTex", pngPath, true, false) + "]";
            scope.PutReply(id, 0, Reply(id, "exported", files: files));
            LiveLinkReplies.Poll(scope.Folder);
            var prepared = scope.Presented.Single();
            Assert.That(prepared.Rows.Select(r => r.Problem), Is.EqualTo(new string[] { null, null }));
            Assert.That(prepared.Rows.Select(r => r.Material), Is.EqualTo(new[] { a, b }), "each key finds its own material");

            // エディターを開き直した後（送ったのが前のセッション）は、InstanceID が別の物を指しうるので引かない
            string old = Guid.NewGuid().ToString("D");
            LiveLinkLedger.Add(old, DateTime.UtcNow.AddDays(-1), "key", "Avatar");
            scope.PutReply(old, 0, Reply(old, "exported", files: files));
            LiveLinkReplies.Poll(scope.Folder);
            Assert.That(scope.Presented.Count, Is.EqualTo(1), "nothing can be applied: no window");
            Assert.That(LiveLinkState.Current.problems.Select(p => p.reason), Is.EqualTo(new[] { LiveLinkReason.MaterialNotFound, LiveLinkReason.MaterialNotFound }));
        }

        [Test]
        public void AnExportIsImportedWithItsSettingsAndAppliedOnlyWhenChosen()
        {
            string id = Sent();
            var material = scope.CreateMaterial("Body");
            var old = scope.WritePng("old_main", Color.gray);
            material.SetTexture("_MainTex", old);
            string key = LiveLinkRequest.MaterialKey(material);
            // スタンドアロンが書き出した PNG（取り込む前のファイル）
            string mainPath = LiveLinkTestScope.Absolute(scope.AssetFolder + "/Avatar_Body_Main.png");
            string normalPath = LiveLinkTestScope.Absolute(scope.AssetFolder + "/Avatar_Body_Normal.png");
            string maskPath = LiveLinkTestScope.Absolute(scope.AssetFolder + "/Avatar_Body_Metallic.png");
            System.IO.File.WriteAllBytes(mainPath, LiveLinkTestScope.Png(Color.red));
            System.IO.File.WriteAllBytes(normalPath, LiveLinkTestScope.Png(new Color(0.5f, 0.5f, 1)));
            System.IO.File.WriteAllBytes(maskPath, LiveLinkTestScope.Png(Color.black));
            string outside = Path.Combine(scope.Root, "elsewhere.png").Replace('\\', '/');
            System.IO.File.WriteAllBytes(outside, LiveLinkTestScope.Png(Color.green));
            string files = "[" + string.Join(",",
                File(key, "_MainTex", mainPath, true, false),
                File(key, "_BumpMap", normalPath, false, true),
                File(key, "_MetallicGlossMap", maskPath, false, false),
                File(key, "_EmissionMap", outside, true, false)) + "]";
            scope.PutReply(id, 0, Reply(id, "exported", files: files));
            LiveLinkReplies.Poll(scope.Folder);

            var state = LiveLinkState.Current;
            Assert.That(state.Kind, Is.EqualTo(LiveLinkState.Phase.Exported));
            Assert.That(state.problems.Select(p => p.reason), Is.EqualTo(new[] { LiveLinkReason.OutsideAssets }), "the PNG outside Assets is not imported");
            string mainAsset = scope.AssetFolder + "/Avatar_Body_Main.png";
            var mainImporter = (TextureImporter)AssetImporter.GetAtPath(mainAsset);
            Assert.That(mainImporter.sRGBTexture, Is.True);
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(scope.AssetFolder + "/Avatar_Body_Normal.png")).textureType, Is.EqualTo(TextureImporterType.NormalMap));
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(scope.AssetFolder + "/Avatar_Body_Metallic.png")).sRGBTexture, Is.False);
            Assert.That(material.GetTexture("_MainTex"), Is.EqualTo(old), "nothing is applied before the user chooses");

            var prepared = scope.Presented.Single();
            Assert.That(prepared.TargetName, Is.EqualTo("Avatar"));
            var main = prepared.Rows.Single(r => r.Property == "_MainTex");
            Assert.That(main.Material, Is.EqualTo(material));
            Assert.That(main.Old, Is.EqualTo(old));
            Assert.That(main.Texture, Is.EqualTo(AssetDatabase.LoadAssetAtPath<Texture2D>(mainAsset)));
            Assert.That(prepared.Rows.Single(r => r.Property == "_EmissionMap").Problem, Is.EqualTo(LiveLinkReason.OutsideAssets));
            Assert.That(prepared.Changes.Count(), Is.EqualTo(3));

            // 選んだ物だけ: _MainTex と _BumpMap
            prepared.Rows.Single(r => r.Property == "_MetallicGlossMap").Selected = false;
            Assert.That(LiveLinkImport.Apply(prepared.Rows.Where(r => r.Selected)), Is.EqualTo(2));
            Assert.That(material.GetTexture("_MainTex"), Is.EqualTo(main.Texture));
            Assert.That(material.GetTexture("_BumpMap"), Is.Not.Null);
            Assert.That(material.GetTexture("_MetallicGlossMap"), Is.Null, "an unchecked row is not applied");

            // 1 回の取り消しで全部戻る
            Undo.PerformUndo();
            Assert.That(material.GetTexture("_MainTex"), Is.EqualTo(old));
            Assert.That(material.GetTexture("_BumpMap"), Is.Null);
        }

        [Test]
        public void NotApplyingLeavesTheMaterialAndAMaterialInsideAnFbxIsNotWritten()
        {
            string id = Sent();
            var material = scope.CreateMaterial("Body");
            string fbx = scope.WriteFbx("Arm", FbxAscii.ArmScene());
            var inFbx = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Material>().First();
            string pngPath = LiveLinkTestScope.Absolute(scope.AssetFolder + "/Main.png");
            System.IO.File.WriteAllBytes(pngPath, LiveLinkTestScope.Png(Color.red));
            string files = "[" + File(LiveLinkRequest.MaterialKey(material), "_MainTex", pngPath, true, false) + "," +
                           File(LiveLinkRequest.MaterialKey(inFbx), "_MainTex", pngPath, true, false) + "," +
                           File("guid:00000000000000000000000000000001/fileid:5", "_MainTex", pngPath, true, false) + "]";
            scope.PutReply(id, 0, Reply(id, "exported", files: files));
            LiveLinkReplies.Poll(scope.Folder);
            var prepared = scope.Presented.Single();
            Assert.That(prepared.Rows.Select(r => r.Problem), Is.EqualTo(new[] { null, LiveLinkReason.MaterialReadOnly, LiveLinkReason.MaterialNotFound }));
            // 当てない: 何も変わらない
            Assert.That(material.GetTexture("_MainTex"), Is.Null);
            Assert.That(LiveLinkImport.Apply(prepared.Rows), Is.EqualTo(1), "only the writable material");
            Assert.That(inFbx.GetTexture("_MainTex"), Is.Null);
        }

        [Test]
        public void AnExportThatChangesNothingDoesNotAsk()
        {
            string id = Sent();
            var material = scope.CreateMaterial("Body");
            var already = scope.WritePng("Main", Color.red);
            material.SetTexture("_MainTex", already);
            string files = "[" + File(LiveLinkRequest.MaterialKey(material), "_MainTex", LiveLinkTestScope.Absolute(AssetDatabase.GetAssetPath(already)), true, false) + "]";
            scope.PutReply(id, 0, Reply(id, "exported", files: files));
            LiveLinkReplies.Poll(scope.Folder);
            Assert.That(scope.Presented, Is.Empty, "the material already uses the exported file");
            Assert.That(LiveLinkState.Current.Kind, Is.EqualTo(LiveLinkState.Phase.Exported));
        }

        [Test]
        public void AReplyThatIsTooLargeOrBrokenIsRemovedWithoutBeingUsed()
        {
            string id = Sent();
            string large = scope.PutReply(id, 0, "{\"format\":1,\"pad\":\"" + new string('x', (int)LiveLinkFolder.MaxReplyBytes) + "\"}");
            string broken = scope.PutReply(id, 1, "{\"format\":1,");
            Assert.That(LiveLinkReplies.Poll(scope.Folder), Is.EqualTo(2));
            Assert.That(System.IO.File.Exists(large), Is.False);
            Assert.That(System.IO.File.Exists(broken), Is.False);
            Assert.That(LiveLinkState.Current.Kind, Is.EqualTo(LiveLinkState.Phase.Sent), "nothing was applied");
        }

        [Test]
        public void TheSentRequestsAreRememberedAcrossSessionsAndOldOnesAreDropped()
        {
            var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
            LiveLinkLedger.Add("old", now - LiveLinkLedger.MaxAge - TimeSpan.FromDays(1), "k", "Old\tName");
            LiveLinkLedger.Add("a", now, "key\nwith line", "Name\twith tab");
            LiveLinkLedger.Reload(); // 開き直したエディター
            Assert.That(LiveLinkLedger.Knows("a"), Is.True);
            Assert.That(LiveLinkLedger.Knows("old"), Is.False, "older than the limit");
            Assert.That(LiveLinkLedger.Find("a").Value.TargetKey, Is.EqualTo("key\nwith line"));
            Assert.That(LiveLinkLedger.Find("a").Value.TargetName, Is.EqualTo("Name\twith tab"));
            for (int i = 0; i < LiveLinkLedger.Keep + 5; i++) LiveLinkLedger.Add("n" + i, now.AddSeconds(i), "k", "n");
            LiveLinkLedger.Reload();
            Assert.That(LiveLinkLedger.Entries.Count, Is.EqualTo(LiveLinkLedger.Keep));
            Assert.That(LiveLinkLedger.Knows("n0"), Is.False, "the oldest is dropped first");
            Assert.That(LiveLinkLedger.Knows("n" + (LiveLinkLedger.Keep + 4)), Is.True);
        }

        [Test]
        public void PathsOutsideAssetsAreRecognised()
        {
            Assert.That(LiveLinkImport.AssetPathOf("/p/Proj/Assets/A/b.png", "/p/Proj/Assets", false), Is.EqualTo("Assets/A/b.png"));
            Assert.That(LiveLinkImport.AssetPathOf("/p/Proj/AssetsX/b.png", "/p/Proj/Assets", false), Is.Null);
            Assert.That(LiveLinkImport.AssetPathOf("/p/Proj/Packages/b.png", "/p/Proj/Assets", false), Is.Null);
            Assert.That(LiveLinkImport.AssetPathOf("/p/Proj/assets/b.png", "/p/Proj/Assets", false), Is.Null);
            Assert.That(LiveLinkImport.AssetPathOf("/p/Proj/assets/b.png", "/p/Proj/Assets", true), Is.EqualTo("Assets/b.png"), "Windows paths ignore case");
            Assert.That(LiveLinkImport.AssetPathOf("/p/Proj/Assets/../b.png", "/p/Proj/Assets", false), Is.Null);
        }
    }
}
