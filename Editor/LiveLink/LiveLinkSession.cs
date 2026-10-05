using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.YoluPainter.Editor.LiveLink
{
    /// <summary>
    /// Live Link のつながり 1 つ（同時に 1 つだけ）。エディタの更新ごとに <see cref="Tick"/> で、ブリッジの知らせを読み、来たテクスチャを
    /// 上げて PropertyBlock に当て、ポーズの変化（0.2 秒ごとに確かめる。変わったときだけ）を送る。どれもブリッジを待たない。
    /// ドメインの読み直し・Play へ移る・エディタの終了のときは、当てた PropertyBlock を外して切る（シーンに何も残さない）。
    /// </summary>
    internal sealed class LiveLinkSession : IDisposable
    {
        public const string DefaultLinkName = "yolupainter-livelink";
        const double CheckInterval = 0.2;

        public static LiveLinkSession Active { get; private set; }

        public string LinkName { get; }
        public ulong Handle { get; private set; }
        public LiveLinkModel Model { get; private set; }
        public LiveLinkDisplay Display { get; } = new LiveLinkDisplay();
        /// <summary>最近の知らせ（新しいものが後ろ、64 まで）。</summary>
        public readonly List<string> Log = new List<string>();
        /// <summary>送ったポーズの数（試験・表示用）。</summary>
        public int PosesSent { get; private set; }
        public int ModelsSent { get; private set; }
        /// <summary>送ったマテリアルの更新の数（モデルを送り直さずに済んだ回数。試験・表示用）。</summary>
        public int MaterialUpdatesSent { get; private set; }
        /// <summary>lilToon の値を送ったマテリアルの数の合計（試験・表示用）。</summary>
        public int ValuesSent { get; private set; }
        /// <summary>つながりが終わった知らせ（断られた・つなげなかった・相手が閉じた）の種類。終わっていなければ null。</summary>
        public LiveLinkEventKind? EndedBy { get; private set; }
        /// <summary>終わった知らせの数値（断られたときは YoluPainter の RejectCode: 1 版が合わない・2 ほかの Unity とつながっている・3 鍵）。</summary>
        public int EndedCode { get; private set; }
        public event Action Changed;

        ulong lastSerial; bool pending; double nextCheck; bool disposed;

        LiveLinkSession(string linkName, ulong handle) { LinkName = linkName; Handle = handle; }

        public LiveLinkStatus Status => Handle == 0 ? LiveLinkStatus.Closed : LiveLinkBridge.Status(Handle);
        public string StatusText => Handle == 0 ? L.Tr("Not connected.") : LiveLinkBridge.StatusText(Handle);

        /// <summary>版のずれと使える機能の様子（ブリッジが挨拶から決めたもの）。つながるまでは <see cref="LiveLinkReport.Empty"/>。</summary>
        public LiveLinkReport Report => LiveLinkBridge.Report(Handle);

        /// <summary>画面に出す版のずれの様子: つながっている間だけ（相手が終わったあと・失敗したあとは <see cref="LiveLinkReport.Empty"/>。
        /// 閉じたつながりの版のずれを、状態の行に残さない）。</summary>
        public LiveLinkReport LiveReport => Status == LiveLinkStatus.Connected ? Report : LiveLinkReport.Empty;

        /// <summary>つないだまま版がずれているときのツールチップ（両方の版・どちらを上げるか・使えない機能の名前）。ずれが無い・つながっていないときは null。</summary>
        public string VersionTooltip => LiveLinkNotice.Tooltip(LiveReport, LiveLinkNotice.OwnVersion);

        /// <summary>画面に出す状態の文: プロトコルの版の範囲が合わずに断られたときは、どちらを何版以上に上げるかを自分の言語で。それ以外はブリッジの文。</summary>
        public string DisplayStatusText => Handle == 0 ? StatusText : LiveLinkNotice.Refusal(Report) ?? StatusText;

        /// <summary>使える機能（双方の印の共通部分）。印の要る新しい命令は、立っているときだけ送る。</summary>
        public ulong CommonFeatures => LiveLinkBridge.CommonFeatures(Handle);

        /// <summary>つなぎ始める（待たない。前のつながりは切る）。使えなければ理由を投げる。</summary>
        public static LiveLinkSession Start(string linkName)
        {
            if (!LiveLinkBridge.Available) throw new InvalidOperationException(LiveLinkBridge.Problem);
            Active?.Dispose();
            ulong handle = LiveLinkBridge.Connect(linkName, "YoluPainter " + PackagePaths.Version + " (Unity " + Application.unityVersion + ")", LiveLinkNotice.OwnVersion ?? "");
            if (handle == 0) throw new ArgumentException(L.Tr("The link name must be 1–64 letters, digits, '.', '_' or '-'."));
            var s = new LiveLinkSession(linkName, handle);
            Active = s;
            EditorApplication.update += s.Tick;
            return s;
        }

        [InitializeOnLoadMethod]
        static void Hook()
        {
            // リロード・Play の出入りでは、つないでいたことを覚えて切り、リロードの後に自動でつなぎ直す（LiveLinkResume）。終了では覚えない
            AssemblyReloadEvents.beforeAssemblyReload += StopForReload;
            EditorApplication.quitting += StopActive;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode) StopForReload();
                else if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode) LiveLinkResume.TryResume();
            };
            // リロードの後。Play へ移る途中は、Play のシーンが出来てから（EnteredPlayMode）つなぎ直す
            EditorApplication.delayCall += () => { if (!EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying) LiveLinkResume.TryResume(); };
        }

        /// <summary>今のつながりを切る（PropertyBlock を外す）。つなぎ直しの覚えも消す。</summary>
        public static void StopActive() => Active?.Dispose();

        /// <summary>リロード・Play の出入りのための切り方: つないでモデルを見せていたことを覚えてから切る（LiveLinkResume.TryResume がつなぎ直す）。</summary>
        public static void StopForReload()
        {
            var s = Active;
            if (s == null) return;
            LiveLinkResume.Remember(s);
            s.DisposeCore();
        }

        /// <summary>モデルを写して送る（前のモデルに当てた PropertyBlock は外す）。失敗すれば理由。</summary>
        public string SendModel(GameObject root)
        {
            if (root == null) return L.Tr("No GameObject is chosen.");
            if (EditorUtility.IsPersistent(root)) return L.Tr("A Prefab asset cannot be chosen.");
            if (Status != LiveLinkStatus.Connected) return L.Tr("Not connected to the standalone yet.");
            Display.Clear();
            Model?.Dispose();
            Model = LiveLinkModel.Capture(root);
            if (Model.Meshes.Count == 0) { var none = L.Tr("{0} has no active mesh renderers to send.", root.name); Model.Dispose(); Model = null; return none; }
            string problem = Model.Send(Handle);
            if (problem != null) return problem;
            ModelsSent++;
            AddLog(L.Tr("Sent {0}: {1} renderers, {2} materials, {3} vertices.", root.name, Model.Meshes.Count, Model.Materials.Count, Model.VertexTotal));
            SendValues(true);
            nextCheck = EditorApplication.timeSinceStartup + CheckInterval;
            Changed?.Invoke();
            return null;
        }

        /// <summary>送ったモデルを閉じる（PropertyBlock を外し、スタンドアロンに知らせる）。</summary>
        public void CloseModel()
        {
            Display.Clear();
            if (Model != null && Handle != 0) LiveLinkBridge.ModelClose(Handle);
            Model?.Dispose();
            Model = null;
            Changed?.Invoke();
        }

        /// <summary>lilToon のマテリアルの値を送る（スタンドアロンが受けるときだけ。<see cref="LiveLinkModel.SendValues"/>）。</summary>
        void SendValues(bool all)
        {
            if (Model == null) return;
            var r = Model.SendValues(Handle, all);
            if (r.Problem != null) AddLog(r.Problem);
            if (r.Materials == 0) return;
            ValuesSent += r.Materials;
            AddLog(r.Textures > 0
                ? L.Tr("Sent the lilToon values of {0} materials and {1} textures.", r.Materials, r.Textures)
                : L.Tr("Sent the lilToon values of {0} materials.", r.Materials));
            Changed?.Invoke();
        }

        void AddLog(string text)
        {
            Log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
            if (Log.Count > 64) Log.RemoveAt(0);
        }

        /// <summary>エディタの更新ごと（試験は手で呼ぶ）。</summary>
        public void Tick()
        {
            if (disposed || Handle == 0) return;
            bool changed = false;
            while (LiveLinkBridge.NextEvent(Handle, out var e))
            {
                changed = true;
                if (e.Kind == LiveLinkEventKind.Rejected || e.Kind == LiveLinkEventKind.Failed || e.Kind == LiveLinkEventKind.Closed) { EndedBy = e.Kind; EndedCode = e.Code; }
                switch (e.Kind)
                {
                    case LiveLinkEventKind.SetAdded: AddLog(L.Tr("Texture set {0} arrived.", e.Text)); break;
                    case LiveLinkEventKind.SetRemoved: AddLog(L.Tr("A texture set was removed.")); break;
                    default: if (!string.IsNullOrEmpty(e.Text)) AddLog(e.Text); break;
                }
            }
            ulong serial = LiveLinkBridge.Serial(Handle);
            if (Display.RebuildLost(Handle)) pending = true;
            if (serial != lastSerial || pending)
            {
                lastSerial = serial;
                bool sets = Display.SyncSets(Handle);
                bool uploaded = Display.Upload(Handle, out pending);
                if (Model != null) Display.Bind(Model, sets);
                if (sets || uploaded)
                {
                    // シーンビューと、エディットモードのゲームビューだけを描き直す（ほかの窓は描き直さない）
                    changed = true; SceneView.RepaintAll(); EditorApplication.QueuePlayerLoopUpdate();
                }
            }
            if (Model != null && Status == LiveLinkStatus.Connected && EditorApplication.timeSinceStartup >= nextCheck)
            {
                nextCheck = EditorApplication.timeSinceStartup + CheckInterval;
                CheckModel();
            }
            if (changed) Changed?.Invoke();
        }

        /// <summary>モデルの変化を見る: 根が消えた → 閉じる、レンダラー・メッシュ・マテリアルの組み合わせが変わった → モデルを送り直す、
        /// シェーダー・キーワード・テクスチャが変わった → マテリアルの更新だけを送る、lilToon の値が変わった → 値を送る（スタンドアロンが
        /// 受けるときだけ）、形が変わった → ポーズを送る。</summary>
        public void CheckModel()
        {
            if (Model == null) return;
            if (Model.Root == null) { AddLog(L.Tr("The model was removed from the scene.")); CloseModel(); return; }
            if (Model.ComputeStructureHash() != Model.StructureHash)
            {
                AddLog(L.Tr("The renderers, meshes or materials changed; sending the model again."));
                string problem = SendModel(Model.Root);
                if (problem != null) AddLog(problem);
                return;
            }
            if (Model.ComputeMaterialHash() != Model.MaterialHash)
            {
                string problem = Model.SendMaterials(Handle);
                if (problem != null) { AddLog(problem); Model.AcceptMaterialHash(); return; }
                MaterialUpdatesSent++;
                Display.Rebind(Model);
                AddLog(L.Tr("The shaders, keywords or textures of the materials changed; sent the material information again."));
                Changed?.Invoke();
            }
            // lilToon の値（インスペクターで変えた値・差し替えたテクスチャ）。変わったマテリアルだけ
            SendValues(false);
            if (Model == null) return;
            int sent = Model.SendPoseIfChanged(Handle);
            if (sent > 0) PosesSent++;
        }

        /// <summary>切る。つなぎ直しの覚えも消す（利用者が切った・つなぎ直す）。</summary>
        public void Dispose()
        {
            if (disposed) return;
            LiveLinkResume.Forget();
            DisposeCore();
        }

        void DisposeCore()
        {
            if (disposed) return;
            disposed = true;
            EditorApplication.update -= Tick;
            Display.Dispose();
            Model?.Dispose(); Model = null;
            LiveLinkBridge.Disconnect(Handle);
            Handle = 0;
            if (Active == this) Active = null;
            Changed?.Invoke();
        }
    }
}
