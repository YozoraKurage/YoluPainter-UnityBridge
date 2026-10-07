using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Editor.LiveLink;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// Live Link の窓と、マテリアルに当てるかを確かめる窓（実際の EditorWindow に Layout と Repaint を流して、描く途中で例外やエラーが出ないこと）:
    /// 相手なし・相手あり・状態ごと（送った・起動中・受けた・断られた・書き出した・止まった）・印のツールチップ・理由の一覧。エラーのログは、壊れたエディターの
    /// シェーダーのコンパイルのエラーのほかは全部落とす（<see cref="LiveLinkTestScope"/>）。窓は batchmode では動かないのでスキップする（GUI モードの常駐で回す）。
    /// </summary>
    [Category("Window")]
    public sealed class LiveLinkWindowTests
    {
        LiveLinkTestScope scope;
        readonly List<EditorWindow> windows = new List<EditorWindow>();
        Object[] selection;
        System.Func<bool> awake, launching;

        [SetUp]
        public void Open()
        {
            if (Application.isBatchMode) Assert.Ignore("The window needs a non-batch Editor (test-daemon.sh start in GUI mode).");
            scope = new LiveLinkTestScope();
            selection = Selection.objects;
            awake = LiveLinkWindow.Awake;
            launching = LiveLinkWindow.Launching;
        }

        [TearDown]
        public void Close()
        {
            if (Application.isBatchMode) return;
            try
            {
                foreach (var w in windows) if (w != null) w.Close();
                windows.Clear();
                LiveLinkApplyWindow.ClearWaiting();
                Selection.objects = selection;
                LiveLinkWindow.Awake = awake;
                LiveLinkWindow.Launching = launching;
            }
            finally { scope.Dispose(); }
        }

        void Draw(EditorWindow w)
        {
            scope.TolerateShaderErrors();
            w.SendEvent(new Event { type = EventType.Layout });
            w.SendEvent(new Event { type = EventType.Repaint });
        }

        LiveLinkWindow Window()
        {
            var w = EditorWindow.CreateWindow<LiveLinkWindow>();
            w.position = new Rect(40, 40, 360, 300);
            windows.Add(w);
            return w;
        }

        static LiveLinkState State(LiveLinkState.Phase phase, LiveLinkState.Failure failure = LiveLinkState.Failure.None)
        {
            var s = new LiveLinkState { phase = (int)phase, failure = (int)failure, targetName = "Avatar", request = "r", atTicks = System.DateTime.UtcNow.Ticks };
            s.refused.Add(new LiveLinkState.Problem("Accessory", LiveLinkReason.MeshNotFromFbx));
            s.problems.Add(new LiveLinkState.Problem("Body/Ear", "a_word_from_a_newer_version"));
            return s;
        }

        [Test]
        public void TheWindowDrawsEveryState()
        {
            var w = Window();
            Selection.activeGameObject = null;
            Draw(w);
            var target = scope.Own(new GameObject("Avatar")); // 描く物の無い物（インスペクターがマテリアルを描かない）
            Selection.activeGameObject = target;
            Draw(w);
            Assert.That(w.Target, Is.EqualTo(target), "the target follows the selection");
            Selection.activeGameObject = null;
            Draw(w);
            Assert.That(w.Target, Is.EqualTo(target), "an empty selection keeps the target");
            LiveLinkWindow.Awake = () => true;
            foreach (LiveLinkState.Phase phase in System.Enum.GetValues(typeof(LiveLinkState.Phase)))
            {
                LiveLinkState.Set(State(phase));
                Draw(w);
            }
            foreach (LiveLinkState.Failure failure in System.Enum.GetValues(typeof(LiveLinkState.Failure)))
            {
                LiveLinkState.Set(State(LiveLinkState.Phase.Failed, failure));
                Draw(w);
            }
            LiveLinkWindow.Awake = () => false;
            LiveLinkWindow.Launching = () => true;
            Draw(w);
            var sent = State(LiveLinkState.Phase.Sent);
            Assert.That(LiveLinkWindow.PresenceTip(LiveLinkWindow.Presence.Starting, sent), Does.StartWith("Starting YoluPainter…\nSent Avatar ("));
            Assert.That(LiveLinkWindow.PresenceTip(LiveLinkWindow.Presence.NotRunning, new LiveLinkState()), Is.EqualTo("YoluPainter is not running"),
                "nothing sent yet: no last state");
            var refusedWithoutReason = State(LiveLinkState.Phase.Refused); refusedWithoutReason.problems.Clear();
            Assert.That(LiveLinkWindow.Rows(refusedWithoutReason).First().Reason, Is.EqualTo("Refused"));
        }

        [Test]
        public void TheApplyWindowDrawsItsRowsAndAppliesOnlyWhatWasChosen()
        {
            var a = scope.CreateMaterial("A");
            var b = scope.CreateMaterial("B");
            var oldTexture = scope.WritePng("old", Color.gray);
            a.SetTexture("_MainTex", oldTexture);
            var newA = scope.WritePng("new_a", Color.red);
            var newB = scope.WritePng("new_b", Color.blue);
            var prepared = new LiveLinkImport.Prepared { TargetName = "Avatar" };
            prepared.Rows.Add(new LiveLinkImport.Row { Material = a, Property = "_MainTex", Old = oldTexture, Texture = newA, NewPath = "/x/new_a.png" });
            prepared.Rows.Add(new LiveLinkImport.Row { Material = b, Property = "_MainTex", Old = null, Texture = newB, NewPath = "/x/new_b.png", Selected = false });
            prepared.Rows.Add(new LiveLinkImport.Row { MaterialKey = "guid:x/fileid:1", Property = "_BumpMap", NewPath = "/elsewhere/n.png", Problem = LiveLinkReason.OutsideAssets });
            LiveLinkApplyWindow.Show(prepared);
            var w = Resources.FindObjectsOfTypeAll<LiveLinkApplyWindow>().Single();
            windows.Add(w);
            Assert.That(w.Current, Is.SameAs(prepared));
            Draw(w);
            w.Choose(true, true);
            Assert.That(a.GetTexture("_MainTex"), Is.EqualTo(newA));
            Assert.That(b.GetTexture("_MainTex"), Is.Null, "an unchecked row is not applied");
        }
    }
}
