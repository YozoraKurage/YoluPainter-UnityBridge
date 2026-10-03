using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Editor.Preview
{
    internal sealed class PreviewLoadTimings
    {
        internal double SnapshotMilliseconds, TrianglesMilliseconds, GeometrySnapshotMilliseconds, AdjacencyMilliseconds, BvhMilliseconds;
    }

    public sealed partial class IsolatedModelPreview
    {
        sealed class BuildState
        {
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal Task<SurfaceGeometry> Task;
            internal volatile float Progress;
            internal int Revision;
            internal bool Incomplete;
        }
        BuildState building;
        int pendingTriangleCount;
        Bounds snapshotBounds = new Bounds(Vector3.zero, Vector3.one);
        internal PreviewLoadTimings Timings { get; private set; } = new PreviewLoadTimings();
        internal int LoadCount { get; private set; }
        internal GameObject LoadedSource { get; private set; }
        internal string SourceFingerprint { get; private set; }
        public bool IsPreparing => building != null;
        public float PreparationProgress => building != null ? building.Progress : 1;
        public bool PreparationCanceled { get; private set; }
        internal const int AsyncTriangleThreshold = 10000;

        void PrepareGeometry(List<SurfaceTriangle> triangles, bool incomplete, bool asynchronous)
        {
            pendingTriangleCount = triangles.Count; report.TriangleCount = triangles.Count;
            snapshotBounds = triangles[0].Bounds;
            for (int i = 1; i < triangles.Count; i++) snapshotBounds.Encapsulate(triangles[i].Bounds);
            PreparationCanceled = false;
            if (!asynchronous || triangles.Count < AsyncTriangleThreshold)
            { InstallGeometry(new SurfaceGeometry(triangles, revision), incomplete); return; }
            var state = new BuildState { Revision = revision, Incomplete = incomplete };
            building = state;
            var cancellation = state.Cancellation.Token; int generation = revision;
            state.Task = Task.Run(() => new SurfaceGeometry(triangles, generation, .000001f, cancellation, p => state.Progress = p), cancellation);
            EditorApplication.update += PollPreparation;
            AssemblyReloadEvents.beforeAssemblyReload += CancelPreparation;
            EditorApplication.playModeStateChanged += CancelForPlayMode;
        }
        void CancelForPlayMode(PlayModeStateChange state) { if (state == PlayModeStateChange.ExitingEditMode) CancelPreparation(); }
        void UnhookPreparation()
        {
            EditorApplication.update -= PollPreparation;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelPreparation;
            EditorApplication.playModeStateChanged -= CancelForPlayMode;
        }
        // 完成した配列を主スレッドでだけ公開する。古い読込みやリロードの後の完成は採用しない。
        internal void PollPreparation()
        {
            var state = building;
            if (state == null || !state.Task.IsCompleted) return;
            building = null; UnhookPreparation();
            try
            {
                if (disposed || state.Revision != revision || state.Task.IsCanceled) return;
                InstallGeometry(state.Task.GetAwaiter().GetResult(), state.Incomplete);
            }
            catch (Exception ex) { geometry = null; report.CanPaint = false; report.Diagnostics.Add(L.Tr("Model preparation failed: {0}", ex.Message)); }
            finally { state.Cancellation.Dispose(); }
        }
        void InstallGeometry(SurfaceGeometry value, bool incomplete)
        {
            geometry = value; report.CanPaint = value.TriangleCount > 0 && !incomplete;
            Timings.GeometrySnapshotMilliseconds = value.SnapshotMilliseconds;
            Timings.AdjacencyMilliseconds = value.AdjacencyMilliseconds; Timings.BvhMilliseconds = value.BvhMilliseconds;
            if (value.NonManifoldEdgeCount > 0) report.Diagnostics.Add("Nonmanifold edges are not crossed by the surface brush.");
            contentVersion++;
        }
        /// <summary>準備だけを取り消す。表示用のスナップショットは残すが、完成していない幾何では描けない。</summary>
        public void CancelPreparation()
        {
            var state = building; building = null; UnhookPreparation();
            if (state == null) return;
            PreparationCanceled = true; report.CanPaint = false; state.Cancellation.Cancel();
            // 取り消した仕事の例外と寿命は、完成を待たずに別スレッドで閉じる。Unity オブジェクトには触らない。
            state.Task.ContinueWith(t => { if (t.IsFaulted) _ = t.Exception; state.Cancellation.Dispose(); }, TaskScheduler.Default);
        }

        static Mesh CopyReadableMesh(Mesh input)
        {
            if (input.isReadable) return Object.Instantiate(input);
            // エディタの API は Read/Write の検査を省略する。元のインポート設定とメッシュを変更しない。
            using (var read = MeshUtility.AcquireReadOnlyMeshData(input))
            {
                var data = read[0]; var write = Mesh.AllocateWritableMeshData(1); bool applied = false;
                var mesh = new Mesh();
                try
                {
                    var dst = write[0]; dst.SetVertexBufferParams(data.vertexCount, input.GetVertexAttributes());
                    for (int stream = 0; stream < data.vertexBufferCount; stream++) dst.GetVertexData<byte>(stream).CopyFrom(data.GetVertexData<byte>(stream));
                    int count = data.indexFormat == IndexFormat.UInt16 ? data.GetIndexData<ushort>().Length : data.GetIndexData<uint>().Length;
                    dst.SetIndexBufferParams(count, data.indexFormat);
                    dst.GetIndexData<byte>().CopyFrom(data.GetIndexData<byte>());
                    dst.subMeshCount = data.subMeshCount;
                    for (int sub = 0; sub < data.subMeshCount; sub++) dst.SetSubMesh(sub, data.GetSubMesh(sub), MeshUpdateFlags.DontRecalculateBounds);
                    Mesh.ApplyAndDisposeWritableMeshData(write, mesh); applied = true; mesh.bounds = input.bounds;
                    return mesh;
                }
                catch { Object.DestroyImmediate(mesh); throw; }
                finally { if (!applied) write.Dispose(); }
            }
        }
    }

    // ダイアログから作成までの間にモデルが変わっていないことを、参照・形・UV・姿勢・スロット・取り込み世代で確かめる。
    internal static class PreviewSourceFingerprint
    {
        struct Digest
        {
            ulong a, b;
            internal void Add(int value) { unchecked { a = (a ^ (uint)value) * 1099511628211UL + 17; b = (b + (uint)value + 0x9e3779b9UL) * 0xbf58476d1ce4e5b9UL; } }
            internal void Add(float value) => Add(value.GetHashCode());
            internal void Add(Matrix4x4 value) { for (int i = 0; i < 16; i++) Add(value[i]); }
            internal void Bytes(Unity.Collections.NativeArray<byte> bytes) { for (int i = 0; i < bytes.Length; i++) Add(bytes[i]); }
            public override string ToString() => a.ToString("x16") + b.ToString("x16");
        }
        internal static string Of(GameObject source)
        {
            if (source == null) return "none";
            var hash = new Digest(); var seen = new HashSet<Mesh>(); var assets = new HashSet<string>();
            hash.Add(source.GetInstanceID());
            foreach (var t in source.GetComponentsInChildren<Transform>(true)) { hash.Add(t.GetInstanceID()); hash.Add(t.localToWorldMatrix); hash.Add(t.gameObject.activeSelf ? 1 : 0); hash.Add(t.name.GetHashCode()); }
            foreach (var r in source.GetComponentsInChildren<Renderer>(true))
            {
                hash.Add(r.GetInstanceID()); hash.Add(r.enabled ? 1 : 0); hash.Add(r.GetType().GetHashCode());
                Mesh mesh = null;
                if (r is SkinnedMeshRenderer skin)
                {
                    mesh = skin.sharedMesh;
                    foreach (var bone in skin.bones) hash.Add(bone != null ? bone.GetInstanceID() : 0);
                    hash.Add(skin.rootBone != null ? skin.rootBone.GetInstanceID() : 0);
                    if (mesh != null)
                    {
                        for (int i = 0; i < mesh.blendShapeCount; i++) hash.Add(skin.GetBlendShapeWeight(i));
                        if (mesh.vertexCount > 250000) return null;
                        // ベイクした今の形も検める。BlendShape フレームや bindpose を同じメッシュ上で変えても、古い形を渡さない。
                        var posed = new Mesh();
                        try
                        {
                            skin.BakeMesh(posed, true);
                            using (var data = MeshUtility.AcquireReadOnlyMeshData(posed))
                                for (int stream = 0; stream < data[0].vertexBufferCount; stream++) hash.Bytes(data[0].GetVertexData<byte>(stream));
                        }
                        catch { return null; }
                        finally { Object.DestroyImmediate(posed); }
                    }
                }
                else if (r is MeshRenderer) { var filter = r.GetComponent<MeshFilter>(); mesh = filter != null ? filter.sharedMesh : null; }
                hash.Add(mesh != null ? mesh.GetInstanceID() : 0);
                if (mesh != null && seen.Add(mesh))
                {
                    if (mesh.vertexCount > 250000) return null;
                    hash.Add(EditorUtility.GetDirtyCount(mesh)); hash.Add(mesh.blendShapeCount);
                    try
                    {
                        using (var data = MeshUtility.AcquireReadOnlyMeshData(mesh))
                        {
                            var m = data[0]; hash.Add(m.vertexCount);
                            for (int stream = 0; stream < m.vertexBufferCount; stream++) hash.Bytes(m.GetVertexData<byte>(stream));
                            hash.Bytes(m.GetIndexData<byte>()); hash.Add(m.subMeshCount);
                            for (int sub = 0; sub < m.subMeshCount; sub++) { var d = m.GetSubMesh(sub); hash.Add(d.indexStart); hash.Add(d.indexCount); hash.Add(d.baseVertex); hash.Add((int)d.topology); }
                        }
                    }
                    catch { return null; } // 読めないデータは再利用せず、本来の読込みで理由を知らせる。
                    AddAsset(mesh);
                }
                foreach (var material in r.sharedMaterials)
                { hash.Add(material != null ? material.GetInstanceID() : 0); if (material != null) { hash.Add(EditorJsonUtility.ToJson(material).GetHashCode()); AddAsset(material); } }
            }
            return hash.ToString();
            void AddAsset(Object asset)
            {
                string path = AssetDatabase.GetAssetPath(asset);
                if (!string.IsNullOrEmpty(path) && assets.Add(path)) hash.Add(AssetDatabase.GetAssetDependencyHash(path).GetHashCode());
            }
        }
    }

    internal sealed class PreparedModel : IDisposable
    {
        IsolatedModelPreview preview;
        internal PreparedModel(IsolatedModelPreview value) { preview = value; }
        internal IsolatedModelPreview Take(GameObject model)
        {
            if (preview == null) return null;
            if (preview.LoadedSource != model || preview.SourceFingerprint == null || preview.SourceFingerprint != PreviewSourceFingerprint.Of(model)) { Dispose(); return null; }
            var result = preview; preview = null; return result;
        }
        public void Dispose() { preview?.Dispose(); preview = null; }
    }
}
